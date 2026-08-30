using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>Что человек ввел в окне. Номера здесь нет: его подбирает сам журнал.</summary>
public sealed record IssueInput(string Document, string Holder, DateTime Date);

/// <summary>Заведенная строка журнала.</summary>
public sealed record Issue(int RecordId, int Number);

/// <summary>
/// Занятые номера за прогон — в памяти.
///
/// Без нее подбор номера перечитывал бы журнал ЦЕЛИКОМ на каждый файл: одна пачка из 20 000
/// документов дала бы 20 000 полных чтений коллекции, которая по ходу прогона сама растет до
/// 20 000 строк. Квадрат. С ведомостью журнал читается ОДИН раз на прогон, а дальше номера
/// раздаются из памяти.
///
/// Плата за это названа честно: между чтением и концом прогона другой выдающий может занять
/// номер, и мы этого не увидим. Защиты от такой гонки не было и раньше — перечитывание помогало
/// лишь сузить окно, а настоящая защита требует уникального индекса в базе, которого у коллекций
/// нет (см. «Сознательно не покрыто» в testing/extension-tests.md).
/// </summary>
public sealed class NumberLedger
{
    private readonly Dictionary<string, SortedSet<int>> _taken;

    private NumberLedger(Dictionary<string, SortedSet<int>> taken) => _taken = taken;

    /// <summary>Прочитать занятые номера всех документов пачки — два запроса на весь набор.</summary>
    public static NumberLedger Load(ConstructorRepository repo, JournalShape shape,
                                    IReadOnlyCollection<string> documents) =>
        new(IssueJournal.Taken(repo, shape, documents));

    /// <summary>Ведомость на один документ. Для одиночной выдачи, где пачки нет.</summary>
    public static NumberLedger For(ConstructorRepository repo, JournalShape shape, string document) =>
        Load(repo, shape, [document]);

    /// <summary>Занятые номера документа. Документ, которого не было в наборе, читается как
    /// пустой: имена берутся из тех же файлов, что и пачка.</summary>
    public SortedSet<int> Taken(string document)
    {
        var key = JournalSchema.Fold(document);

        if (!_taken.TryGetValue(key, out var numbers))
            _taken[key] = numbers = [];

        return numbers;
    }

    /// <summary>Пометить номер выданным.</summary>
    public void Occupy(string document, int number) => Taken(document).Add(number);

    /// <summary>Вернуть номер в оборот — резерв снят, знак не лег.</summary>
    public void Free(string document, int number) => Taken(document).Remove(number);
}

/// <summary>
/// Журнал выдачи: чтение занятых номеров и запись строки.
///
/// <b>Строка журнала — это и есть резерв номера.</b> Поэтому она заводится ДО наложения знака:
/// исход «знак на бумаге есть, а записи нет» опасен (номер выдадут второму человеку, и два
/// физических экземпляра будут носить один номер, чего никто не заметит), а обратный —
/// «запись есть, знака нет» — всего лишь сгоревший номер, и расхождение видно глазами.
/// Из двух исходов выбран обратимый.
/// </summary>
public static class IssueJournal
{
    /// <summary>
    /// Занятые номера по каждому из перечисленных документов — ДВА запроса на весь набор,
    /// сколько бы файлов ни было в пачке.
    ///
    /// Ключи результата — свернутые имена (<see cref="JournalSchema.Fold"/>): «Отчет» и «отчет »
    /// в журнале означают один документ, и второй экземпляр обязан получить номер 2, а не начать
    /// нумерацию заново.
    /// </summary>
    public static Dictionary<string, SortedSet<int>> Taken(
        ConstructorRepository repo, JournalShape shape, IReadOnlyCollection<string> documents)
    {
        var wanted = documents.Select(JournalSchema.Fold).ToHashSet(StringComparer.Ordinal);
        var result = wanted.ToDictionary(d => d, _ => new SortedSet<int>(), StringComparer.Ordinal);
        if (result.Count == 0) return result;

        // Запрос 1: пары (Id, Title) всей коллекции. Не GetRecords — тот материализует полные
        // DbRecord, а нам нужны две колонки.
        var mine = new Dictionary<int, string>();
        foreach (var (id, title) in repo.GetRecordTitlesOrdered(shape.CollectionId))
        {
            var folded = JournalSchema.Fold(title);
            if (wanted.Contains(folded)) mine[id] = folded;
        }
        if (mine.Count == 0) return result;

        // Запрос 2: значения ТОЛЬКО отобранных записей — обычно это единицы строк на документ.
        var values = repo.GetFieldValuesBatch([.. mine.Keys]);

        foreach (var (id, folded) in mine)
            if (values.TryGetValue(id, out var row) &&
                row.TryGetValue(shape.CopyFieldId, out var raw) &&
                CopyNumber.NumberIn(raw) is { } number)
                result[folded].Add(number);

        return result;
    }

    /// <summary>Занятые номера одного документа. Тот же код, что и у набора, — расходиться
    /// нечему.</summary>
    public static SortedSet<int> TakenFor(ConstructorRepository repo, JournalShape shape, string document) =>
        Taken(repo, shape, [document])[JournalSchema.Fold(document)];

    /// <summary>
    /// Завести строку журнала и вернуть выданный номер.
    ///
    /// Номер берется из <paramref name="ledger"/> — ведомости прогона; она же сразу помечается
    /// занятой, поэтому следующий файл того же документа получит следующий номер, не заглядывая
    /// в базу. Ведомости нет — читаем занятые номера этого документа сами (одиночная выдача).
    ///
    /// Номер всегда первый свободный: задать его руками нельзя. Поправить выданный номер человек
    /// может прямо в коллекции журнала — там это обычная ячейка.
    ///
    /// Любая ошибка после создания записи убирает эту запись: полузаполненная строка хуже, чем
    /// отсутствие строки, — по ней номер выдали бы повторно.
    /// </summary>
    /// <exception cref="PdfWatermarkException">Пустое имя или получатель, исчерпанный
    /// диапазон номеров.</exception>
    public static Issue Reserve(ConstructorRepository repo, JournalShape shape, IssueInput input,
                                NumberLedger? ledger = null)
    {
        var document = input.Document.Trim();
        var holder   = input.Holder.Trim();

        if (document.Length == 0) throw new PdfWatermarkException("не удалось определить имя документа");
        if (holder.Length   == 0) throw new PdfWatermarkException("не заполнено, кому выдан документ");

        var book  = ledger ?? NumberLedger.For(repo, shape, document);
        var taken = book.Taken(document);

        var number = CopyNumber.FirstFree(taken)
            ?? throw new PdfWatermarkException(
                $"у документа «{document}» заняты все номера " +
                $"{CopyNumber.MinNumber}–{CopyNumber.MaxNumber}");

        // Запись дописывается в конец: журнал ведется по порядку выдачи. afterRecordId = 0 —
        // записи с таким id не бывает, и репозиторий по контракту ставит такую запись в конец
        // (одним индексным MAX, без загрузки коллекции в память).
        var record = repo.AddRecordAfter(new DbRecord
        {
            CollectionId = shape.CollectionId,
            Title        = document,
        }, afterRecordId: 0);

        var recordId = record.Id!.Value;

        // Номер занят с этой секунды: строка уже есть в базе, и следующий файл того же документа
        // обязан получить следующий номер, даже если значения еще не разложены.
        book.Occupy(document, number);

        try
        {
            // «Экземпляр» пишется ПЕРВЫМ: именно он делает строку резервом номера, и окно между
            // вставкой и этим значением надо держать как можно короче.
            repo.SetFieldValue(recordId, shape.CopyFieldId,   number.ToString());
            repo.SetFieldValue(recordId, shape.HolderFieldId, holder);
            repo.SetFieldValue(recordId, shape.DateFieldId,   StampFor(input.Date));
        }
        catch
        {
            Release(repo, recordId);
            book.Free(document, number);
            throw;
        }

        return new Issue(recordId, number);
    }

    /// <summary>Убрать резерв — знак не наложился. <c>false</c> — убрать не удалось, и об этом
    /// надо сказать громко: номер останется занятым.</summary>
    public static bool Release(ConstructorRepository repo, int recordId)
    {
        try { repo.DeleteRecord(recordId); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Дата выдачи в каноне колонки типа «дата». «Весь день» — потому что человек вводит ДАТУ
    /// выдачи, а не момент нажатия кнопки; время в журнале выдачи не значит ничего.
    /// Тип колонки проверен схемой, поэтому ветвления по типу здесь нет.
    /// </summary>
    public static string StampFor(DateTime date) =>
        new DateValue(date.Date, date.Date, AllDay: true).ToStorage();
}
