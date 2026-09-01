using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Регистрируется ИСПОЛНЕНИЕ существующего документа, а не новый документ: номер серии не
/// расходуется, к уже выданному номеру приписывается «-NN».
/// </summary>
/// <param name="Number">Номер документа в серии — тот, у которого появляется исполнение.</param>
/// <param name="Execution">Номер исполнения; <c>null</c> — подобрать первое свободное.</param>
/// <param name="Confirmed">Человек подтвердил запись поверх уже занятого исполнения. Без этого
/// занятое исполнение регистрацию не пропускает: два документа с одним обозначением — ошибка,
/// и решать ее вправе только человек.</param>
public sealed record ExecutionRequest(int Number, int? Execution = null, bool Confirmed = false);

/// <summary>Что человек ввел в окне. Обозначение сюда не входит — его собирает расширение.</summary>
/// <param name="Name">Наименование ЕСКД — оно же название записи (столбец «Наименование»).</param>
/// <param name="Org">Код организации из списка настроек.</param>
/// <param name="Eskd">Шестизначный код ЕСКД.</param>
/// <param name="Fio">Фамилия, имя, отчество регистрирующего.</param>
/// <param name="Project">Проект документа; поле обязательное — пустым не регистрируем.</param>
/// <param name="Accounting">Отметка учета; пусто — столбец останется пустым.</param>
/// <param name="FirstUse">Примечания (первичное применение); пусто — столбец останется пустым.</param>
/// <param name="Summary">Примечание (краткая характеристика); пусто — столбец останется пустым.</param>
/// <param name="ForExecution">Не <c>null</c> — регистрируется исполнение существующего документа:
/// новый номер серии не выдается.</param>
public sealed record RegistrationInput(
    string Name, string Org, string Eskd, string Fio, string Project,
    string Accounting = "", string FirstUse = "", string Summary = "",
    ExecutionRequest? ForExecution = null);

/// <summary>Что получилось: присвоенное обозначение, его номер и id созданной записи.</summary>
/// <param name="Number">Номер документа в серии — у исполнения это номер основного документа.</param>
/// <param name="Execution">Номер исполнения; <c>null</c> — зарегистрирован сам документ.</param>
public sealed record Registration(int RecordId, string Designation, int Number, int? Execution = null);

/// <summary>Понятная пользователю ошибка расширения (показывается в статусе окна).</summary>
public sealed class DocRegistryException(string message) : Exception(message);

/// <summary>
/// Регистрация одного документа в коллекции — логика без UI.
///
/// Работает на ОБЩЕМ репозитории в UI-потоке, в отличие от «Документов в коллекцию» и
/// «Сводной трудоемкости»: там в базу уходят десятки тысяч записей и фон обязателен, здесь —
/// одна запись и восемь значений, ради которых заводить отдельное подключение незачем. Заодно
/// на общем репозитории срабатывают проверки прав (<c>CreateRecord</c>/<c>EditRecord</c>).
///
/// Номер подбирается ПРЯМО ПЕРЕД записью, а не берется из показанного в окне предпросмотра:
/// между открытием окна и нажатием кнопки тот же номер мог занять другой регистратор.
/// </summary>
public static class DocRegistrar
{
    /// <summary>
    /// Все обозначения коллекции — одним группирующим запросом (не построчным обходом записей).
    /// Пустые значения репозиторий отдает строкой <c>""</c>, разбор их отбрасывает сам.
    /// </summary>
    public static IReadOnlyList<string> Designations(ConstructorRepository repo, RegistryShape shape) =>
        repo.GetFieldValueCounts(shape.CollectionId, shape.DesignationFieldId)
            .Select(v => v.Value)
            .ToList();

    /// <summary>
    /// Наименования по обозначениям: «АБВГ.301121.012» → «Втулка». Нужно окну, чтобы показать, к
    /// КАКОМУ документу приписывается исполнение: номер из трех цифр сам по себе ни о чем не
    /// говорит, а промах виден сразу, если рядом стоит чужое наименование.
    ///
    /// Две колонки, а не все поля коллекции: названия записей и значения столбца «Обозначение».
    /// Дубликаты обозначений сюда попадают первым встреченным — для подсказки этого достаточно.
    /// </summary>
    public static Dictionary<string, string> NamesByDesignation(ConstructorRepository repo, RegistryShape shape)
    {
        var designations = repo.GetColumnValues(shape.CollectionId, shape.DesignationFieldId.ToString());
        var titles       = repo.GetRecordTitlesOrdered(shape.CollectionId)
                               .ToDictionary(t => t.Id, t => t.Title ?? "");

        var names = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var (recordId, designation) in designations)
        {
            var key = (designation ?? "").Trim();
            if (key.Length == 0) continue;
            if (!titles.TryGetValue(recordId, out var title) || title.Length == 0) continue;

            names.TryAdd(key, title);
        }

        return names;
    }

    /// <summary>Номер, который получит следующий документ серии; <c>null</c> — серия исчерпана.
    /// Для предпросмотра в окне: фактический номер считается заново при записи.</summary>
    public static int? NextNumber(IEnumerable<string> designations, string org, string eskd) =>
        Designation.FirstFree(designations, org, eskd);

    /// <summary>Исполнение, которое получит следующее исполнение документа; <c>null</c> — заняты
    /// все 01–99. Для предпросмотра в окне: фактическое считается заново при записи.</summary>
    public static int? NextExecution(IEnumerable<string> designations, string org, string eskd, int number) =>
        Designation.FirstFreeExecution(designations, org, eskd, number);

    /// <summary>
    /// Зарегистрировать документ: подобрать номер, создать запись в КОНЦЕ коллекции и разложить
    /// значения по столбцам. Пустые значения не пишутся — пустая строка значения и отсутствие
    /// строки для таблицы одно и то же, а лишних строк в базе не остается.
    ///
    /// <see cref="RegistrationInput.ForExecution"/> меняет только подбор обозначения: новый номер
    /// серии не выдается, к указанному номеру приписывается исполнение. Все остальное — те же
    /// столбцы и та же запись в конец коллекции.
    ///
    /// Любая ошибка после создания записи убирает эту запись: полузаполненная строка в журнале
    /// регистрации хуже, чем отсутствие строки, — по ней выдали бы номер повторно.
    /// </summary>
    public static Registration Register(ConstructorRepository repo, RegistryShape shape,
                                        RegistrationInput input, DateTime now)
    {
        var name    = input.Name.Trim();
        var org     = input.Org.Trim();
        var eskd    = input.Eskd.Trim();
        var project = input.Project.Trim();

        if (name.Length == 0) throw new DocRegistryException("Не заполнено наименование ЕСКД.");
        if (org.Length  == 0) throw new DocRegistryException("Не выбран код организации.");
        if (!EskdCatalog.IsFullCode(eskd))
            throw new DocRegistryException($"Код ЕСКД должен быть из {EskdCatalog.CodeLength} цифр.");
        if (project.Length == 0) throw new DocRegistryException("Не заполнен проект.");

        var (number, execution, designation) = input.ForExecution is { } request
            ? PickExecution(Designations(repo, shape), org, eskd, request)
            : PickNumber(Designations(repo, shape), org, eskd);

        // Запись дописывается в конец: журнал регистрации ведется по порядку выдачи номеров.
        // afterRecordId = 0 — записи с таким id не бывает, и репозиторий по контракту ставит
        // такую запись в конец (одним индексным MAX, без загрузки коллекции в память).
        var record = repo.AddRecordAfter(new DbRecord
        {
            CollectionId = shape.CollectionId,
            Title        = name,
        }, afterRecordId: 0);

        var recordId = record.Id!.Value;

        try
        {
            Set(repo, recordId, shape.DesignationFieldId, designation);
            Set(repo, recordId, shape.FioFieldId,         input.Fio);
            Set(repo, recordId, shape.DateFieldId,        StampFor(shape.DateFieldType, now));
            Set(repo, recordId, shape.ProjectFieldId,     project);
            Set(repo, recordId, shape.AccountingFieldId,  input.Accounting);
            Set(repo, recordId, shape.FirstUseFieldId,    input.FirstUse);
            Set(repo, recordId, shape.SummaryFieldId,     input.Summary);
        }
        catch
        {
            try { repo.DeleteRecord(recordId); } catch { /* уборка — best-effort */ }
            throw;
        }

        return new Registration(recordId, designation, number, execution);
    }

    /// <summary>Новый документ: первый свободный номер серии.</summary>
    private static (int Number, int? Execution, string Designation) PickNumber(
        IReadOnlyList<string> designations, string org, string eskd)
    {
        var number = Designation.FirstFree(designations, org, eskd)
            ?? throw new DocRegistryException(
                $"В серии «{Designation.Prefix(org, eskd)}» заняты все номера " +
                $"{Designation.MinNumber:D3}–{Designation.MaxNumber:D3}.");

        return (number, null, Designation.Format(org, eskd, number));
    }

    /// <summary>
    /// Исполнение существующего документа: номер серии НЕ расходуется.
    ///
    /// Занятое исполнение пропускается только с подтверждением человека
    /// (<see cref="ExecutionRequest.Confirmed"/>): окно спрашивает об этом само, а здесь стоит
    /// второй рубеж — окно не единственный вход в запись.
    ///
    /// Того, что основного документа нет в журнале, здесь НЕ проверяем: это предупреждение окна,
    /// а не отказ. Старые документы в журнал часто не заносились, и запретить регистрацию их
    /// исполнений значило бы запретить работу.
    /// </summary>
    private static (int Number, int? Execution, string Designation) PickExecution(
        IReadOnlyList<string> designations, string org, string eskd, ExecutionRequest request)
    {
        var number = request.Number;

        if (number is < Designation.MinNumber or > Designation.MaxNumber)
            throw new DocRegistryException(
                $"Номер документа должен быть от {Designation.MinNumber:D3} " +
                $"до {Designation.MaxNumber:D3}.");

        var execution = request.Execution
            ?? Designation.FirstFreeExecution(designations, org, eskd, number)
            ?? throw new DocRegistryException(
                $"У документа {Designation.Format(org, eskd, number)} заняты все исполнения " +
                $"{Designation.MinExecution:D2}–{Designation.MaxExecution:D2}.");

        if (execution is < Designation.MinExecution or > Designation.MaxExecution)
            throw new DocRegistryException(
                $"Исполнение должно быть от {Designation.MinExecution:D2} " +
                $"до {Designation.MaxExecution:D2}.");

        var designation = Designation.FormatExecution(org, eskd, number, execution);

        if (!request.Confirmed &&
            Designation.ExecutionsOf(designations, org, eskd, number).Contains(execution))
            throw new DocRegistryException($"{designation} уже зарегистрировано.");

        return (number, execution, designation);
    }

    /// <summary>
    /// Дата и время регистрации в каноне ТОГО типа колонки, который в коллекции стоит.
    /// Обычно это «дата» — тогда пишем канон <see cref="DateValue"/> с временем (не «весь
    /// день»: важен момент выдачи номера). Если колонку завели текстом — пишем то, что человек
    /// и увидел бы в ячейке, иначе в журнале оказалась бы ISO-строка вида «2026-08-27T18:23».
    /// </summary>
    public static string StampFor(string dateFieldType, DateTime now) =>
        dateFieldType == "date"
            ? new DateValue(now, now, AllDay: false).ToStorage()
            : now.ToString("dd.MM.yyyy HH:mm");

    private static void Set(ConstructorRepository repo, int recordId, int fieldId, string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return;
        repo.SetFieldValue(recordId, fieldId, text);
    }
}
