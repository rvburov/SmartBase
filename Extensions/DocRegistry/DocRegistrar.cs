using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Что человек ввел в окне. Обозначение сюда не входит — его собирает расширение.</summary>
/// <param name="Name">Наименование ЕСКД — оно же название записи (столбец «Наименование»).</param>
/// <param name="Org">Код организации из списка настроек.</param>
/// <param name="Eskd">Шестизначный код ЕСКД.</param>
/// <param name="Fio">ФИО регистрирующего.</param>
/// <param name="Project">Проект документа; поле обязательное — пустым не регистрируем.</param>
/// <param name="Accounting">Отметка учета; пусто — столбец останется пустым.</param>
/// <param name="FirstUse">Примечание (первичное применение); пусто — столбец останется пустым.</param>
/// <param name="Summary">Примечание (краткая характеристика); пусто — столбец останется пустым.</param>
/// <param name="Execution">Номер исполнения, выбранный человеком; <c>null</c> — документ без
/// исполнения.</param>
/// <param name="Number">Номер документа в серии; <c>null</c> — подобрать первый свободный. Не
/// <c>null</c> приходит из окна, когда исполнения одного документа заводят подряд: номер там
/// держится на месте, иначе второе исполнение ушло бы к следующему номеру.</param>
/// <param name="ConfirmedDuplicate">Человек подтвердил запись поверх уже занятого исполнения.
/// Без этого занятое исполнение регистрацию не пропускает.</param>
public sealed record RegistrationInput(
    string Name, string Org, string Eskd, string Fio, string Project,
    string Accounting = "", string FirstUse = "", string Summary = "",
    int? Execution = null, int? Number = null, bool ConfirmedDuplicate = false);

/// <summary>Что получилось: присвоенное обозначение, его номер и id созданной записи.</summary>
/// <param name="Number">Номер документа в серии — у исполнения это номер того же документа.</param>
/// <param name="Execution">Номер исполнения; <c>null</c> — документ без исполнения.</param>
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

    /// <summary>Номер, который получит следующий документ серии; <c>null</c> — серия исчерпана.
    /// Для предпросмотра в окне: фактический номер считается заново при записи.</summary>
    public static int? NextNumber(IEnumerable<string> designations, string org, string eskd) =>
        Designation.FirstFree(designations, org, eskd);

    /// <summary>
    /// Зарегистрировать документ: подобрать номер, создать запись в КОНЦЕ коллекции и разложить
    /// значения по столбцам. Пустые значения не пишутся — пустая строка значения и отсутствие
    /// строки для таблицы одно и то же, а лишних строк в базе не остается.
    ///
    /// Исполнение (<see cref="RegistrationInput.Execution"/>) — часть обозначения, а не отдельный
    /// вид записи: строка та же, меняется только хвост номера.
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

        var designations = Designations(repo, shape);
        var number       = PickNumber(designations, org, eskd, input.Number);
        var execution    = CheckExecution(designations, org, eskd, number, input);

        var designation = execution is { } e
            ? Designation.FormatExecution(org, eskd, number, e)
            : Designation.Format(org, eskd, number);

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

    /// <summary>
    /// Номер документа: первый свободный в серии или тот, который назвало окно.
    ///
    /// Названный номер окно передает, только пока подряд заводят исполнения ОДНОГО документа:
    /// после записи «-01» первый свободный стал бы уже следующим, и «-02» ушло бы к чужому
    /// номеру. Во всех остальных случаях номер подбирается здесь и в момент записи — между
    /// показом в окне и нажатием кнопки тот же номер мог занять другой регистратор.
    /// </summary>
    private static int PickNumber(IReadOnlyList<string> designations, string org, string eskd, int? asked)
    {
        if (asked is { } number)
            return number is >= Designation.MinNumber and <= Designation.MaxNumber
                ? number
                : throw new DocRegistryException(
                    $"Номер документа должен быть от {Designation.MinNumber:D3} " +
                    $"до {Designation.MaxNumber:D3}.");

        return Designation.FirstFree(designations, org, eskd)
            ?? throw new DocRegistryException(
                $"В серии «{Designation.Prefix(org, eskd)}» заняты все номера " +
                $"{Designation.MinNumber:D3}–{Designation.MaxNumber:D3}.");
    }

    /// <summary>
    /// Проверить выбранное исполнение. <c>null</c> на входе и на выходе — документ без исполнения,
    /// и проверять нечего.
    ///
    /// Занятое исполнение — отказ, и пропускает его только подтверждение человека
    /// (<see cref="RegistrationInput.ConfirmedDuplicate"/>): окно спрашивает об этом само, а
    /// здесь стоит второй рубеж — окно не единственный вход в запись.
    /// </summary>
    private static int? CheckExecution(IReadOnlyList<string> designations, string org, string eskd,
                                       int number, RegistrationInput input)
    {
        if (input.Execution is not { } execution) return null;

        if (execution is < Designation.MinExecution or > Designation.MaxExecution)
            throw new DocRegistryException(
                $"Исполнение должно быть от {Designation.MinExecution:D2} " +
                $"до {Designation.MaxExecution:D2}.");

        if (!input.ConfirmedDuplicate &&
            Designation.ExecutionsOf(designations, org, eskd, number).Contains(execution))
            throw new DocRegistryException(
                $"{Designation.FormatExecution(org, eskd, number, execution)} уже зарегистрировано.");

        return execution;
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
