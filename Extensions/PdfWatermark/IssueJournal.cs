using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>Что человек ввел в окне. Номера здесь нет: его подбирает сам журнал.</summary>
public sealed record IssueInput(string Document, string Holder, DateTime Date);

/// <summary>Заведенная строка журнала.</summary>
public sealed record Issue(int RecordId, int Number);

/// <summary>
/// Занятые номера за прогон — в памяти; окончательный резерв подтверждает база.
///
/// Без нее подбор номера перечитывал бы журнал ЦЕЛИКОМ на каждый файл: одна пачка из 20 000
/// документов дала бы 20 000 полных чтений коллекции, которая по ходу прогона сама растет до
/// 20 000 строк. Квадрат. На закрепленном подключении SQLite data_version меняется только
/// после записи другого подключения: собственные выдачи дополняют память, а соседние изменения
/// требуют свежего чтения под блокировкой коллекции. Отдельно загруженная ведомость либо PG
/// перед резервом проверяют занятые номера документа заново.
/// </summary>
public sealed class NumberLedger
{
    private readonly Dictionary<string, SortedSet<int>> _taken;
    private readonly HashSet<string> _documents;
    private long? _externalVersion;

    private NumberLedger(Dictionary<string, SortedSet<int>> taken, long? externalVersion = null)
    {
        _taken = taken;
        _documents = taken.Keys.ToHashSet(StringComparer.Ordinal);
        _externalVersion = externalVersion;
    }

    internal static NumberLedger Empty() => new(new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal));

    /// <summary>Прочитать занятые номера всех документов пачки — два запроса на весь набор.</summary>
    public static NumberLedger Load(ConstructorRepository repo, JournalShape shape,
                                    IReadOnlyCollection<string> documents) =>
        new(IssueJournal.Taken(repo, shape, documents));

    /// <summary>Только для движка, который держит то же подключение открытым до конца пачки.
    /// Счетчик SQLite принадлежит физическому подключению, его нельзя переносить между ними.</summary>
    internal static NumberLedger LoadPinned(ConstructorRepository repo, JournalShape shape,
                                             IReadOnlyCollection<string> documents) =>
        repo.InCollectionWriteTransaction(shape.CollectionId, () =>
        {
            IssueJournal.VerifyShape(repo, shape);
            return new NumberLedger(IssueJournal.Taken(repo, shape, documents), repo.ReadExternalWriteVersion());
        });

    /// <summary>Вызывать после захвата коллекции, до выбора номера. Кеш не может разрешить
    /// выдачу номера, занятого соседним подключением после начальной загрузки.</summary>
    internal void Refresh(ConstructorRepository repo, JournalShape shape, string document)
    {
        var key = JournalSchema.Fold(document);
        var newDocument = !_documents.Contains(key);
        var version = repo.ReadExternalWriteVersion();
        if (_externalVersion is { } previous && version == previous && !newDocument) return;
        IssueJournal.VerifyShape(repo, shape);

        if (_externalVersion is not null && version is not null)
        {
            // Чужое изменение инвалидирует снимок всей пачки. До следующей чужой записи
            // собственные коммиты этого подключения не вынуждают перечитывать журнал.
            var wanted = newDocument ? _documents.Append(key).ToArray() : _documents.ToArray();
            foreach (var (name, numbers) in IssueJournal.Taken(repo, shape, wanted))
                Taken(name).UnionWith(numbers);
            _externalVersion = version;
        }
        else
        {
            // У переносимой ведомости нет надежного счетчика внешних изменений; снимок
            // из другого контекста и счетчик PG нельзя считать разрешением на выдачу.
            Taken(document).UnionWith(IssueJournal.TakenFor(repo, shape, document));
            _externalVersion = null;
        }
        _documents.Add(key);
    }

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
    /// Номер проверяется под блокировкой коллекции и резервируется вместе со всей строкой.
    /// Ведомость прогона ускоряет чтение; устаревший снимок не позволяет обойти свежую проверку.
    ///
    /// Номер всегда первый свободный: задать его руками нельзя. Поправить выданный номер человек
    /// может прямо в коллекции журнала — там это обычная ячейка.
    ///
    /// Любая ошибка откатывает всю строку: неполный резерв не становится подтвержденной выдачей.
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

        var book = ledger ?? NumberLedger.Empty();
        var issue = repo.InCollectionWriteTransaction(shape.CollectionId, () =>
        {
            book.Refresh(repo, shape, document);
            var number = CopyNumber.FirstFree(book.Taken(document))
                ?? throw new PdfWatermarkException(
                    $"у документа «{document}» заняты все номера " +
                    $"{CopyNumber.MinNumber}–{CopyNumber.MaxNumber}");

            // MAX позиции и номер читаются после блокировки коллекции. Заголовок и все
            // три поля публикуются одним коммитом: сосед не увидит половину резерва.
            var record = repo.AddRecordAfter(new DbRecord
            {
                CollectionId = shape.CollectionId,
                Title = document,
            }, afterRecordId: 0);
            var recordId = record.Id!.Value;

            repo.SetFieldValue(recordId, shape.CopyFieldId,   number.ToString());
            repo.SetFieldValue(recordId, shape.HolderFieldId, holder);
            repo.SetFieldValue(recordId, shape.DateFieldId,   StampFor(input.Date));

            var stored = repo.GetFieldValues(recordId);
            if (!stored.TryGetValue(shape.CopyFieldId, out var copy) || CopyNumber.NumberIn(copy) != number
                || !stored.TryGetValue(shape.HolderFieldId, out var issuedTo) || issuedTo != holder
                || !stored.TryGetValue(shape.DateFieldId, out var date) || date != StampFor(input.Date))
                throw new PdfWatermarkException("структура журнала изменилась, номер не выдан");
            return new Issue(recordId, number);
        });

        // Только после успешного коммита scope: повтор откаченной попытки не должен
        // пропускать номер из-за преждевременного изменения памяти ведомости.
        book.Occupy(document, issue.Number);
        return issue;
    }

    internal static void VerifyShape(ConstructorRepository repo, JournalShape shape)
    {
        var check = JournalSchema.Check(repo, shape.CollectionId, shape.CollectionName);
        if (check.Shape != shape)
            throw new PdfWatermarkException("структура журнала изменилась, проверьте выбранную коллекцию");
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
