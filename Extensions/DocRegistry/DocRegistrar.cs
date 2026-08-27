using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Что человек ввел в окне. Обозначение сюда не входит — его собирает расширение.</summary>
/// <param name="Name">Наименование ЕСКД — оно же название записи (столбец «Наименование»).</param>
/// <param name="Org">Код организации из списка настроек.</param>
/// <param name="Eskd">Шестизначный код ЕСКД.</param>
/// <param name="Fio">Фамилия, имя, отчество регистрирующего.</param>
/// <param name="Topic">Тема документа; пусто — столбец останется пустым.</param>
/// <param name="Accounting">Отметка учета; пусто — столбец останется пустым.</param>
/// <param name="FirstUse">Примечания (первичное применение); пусто — столбец останется пустым.</param>
/// <param name="Summary">Примечание (краткая характеристика); пусто — столбец останется пустым.</param>
public sealed record RegistrationInput(
    string Name, string Org, string Eskd, string Fio,
    string Topic = "", string Accounting = "", string FirstUse = "", string Summary = "");

/// <summary>Что получилось: присвоенное обозначение, его номер и id созданной записи.</summary>
public sealed record Registration(int RecordId, string Designation, int Number);

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
    /// Любая ошибка после создания записи убирает эту запись: полузаполненная строка в журнале
    /// регистрации хуже, чем отсутствие строки, — по ней выдали бы номер повторно.
    /// </summary>
    public static Registration Register(ConstructorRepository repo, RegistryShape shape,
                                        RegistrationInput input, DateTime now)
    {
        var name = input.Name.Trim();
        var org  = input.Org.Trim();
        var eskd = input.Eskd.Trim();

        if (name.Length == 0) throw new DocRegistryException("Не заполнено наименование ЕСКД.");
        if (org.Length  == 0) throw new DocRegistryException("Не выбран код организации.");
        if (!EskdCatalog.IsFullCode(eskd))
            throw new DocRegistryException($"Код ЕСКД должен быть из {EskdCatalog.CodeLength} цифр.");

        var number = Designation.FirstFree(Designations(repo, shape), org, eskd)
            ?? throw new DocRegistryException(
                $"В серии «{Designation.Prefix(org, eskd)}» заняты все номера " +
                $"{Designation.MinNumber:D3}–{Designation.MaxNumber:D3}.");

        var designation = Designation.Format(org, eskd, number);

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
            Set(repo, recordId, shape.TopicFieldId,       input.Topic);
            Set(repo, recordId, shape.AccountingFieldId,  input.Accounting);
            Set(repo, recordId, shape.FirstUseFieldId,    input.FirstUse);
            Set(repo, recordId, shape.SummaryFieldId,     input.Summary);
        }
        catch
        {
            try { repo.DeleteRecord(recordId); } catch { /* уборка — best-effort */ }
            throw;
        }

        return new Registration(recordId, designation, number);
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
