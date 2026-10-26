using SmartBase.Core.Models;
using SmartBase.Database;
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
/// Запись в коллекцию-журнал — логика без UI. Два входа: <see cref="Register"/> — один документ,
/// которому расширение выдает номер; <see cref="RegisterAll"/> — список готовых номеров,
/// загруженный файлом.
///
/// Работает на ОБЩЕМ репозитории в UI-потоке, в отличие от «Документов в коллекцию» и
/// «Сводной трудоемкости»: там в базу уходят десятки тысяч записей и фон обязателен, здесь —
/// одна запись и восемь значений, ради которых заводить отдельное подключение незачем (файл со
/// списком номеров — тоже не тот размер: журнал регистрации ведут строками, а не тысячами).
/// Заодно на общем репозитории срабатывают проверки прав
/// (<c>CreateRecord</c>/<c>EditRecord</c>).
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
    /// </summary>
    public static Registration Register(ConstructorRepository repo, RegistryShape shape,
                                        RegistrationInput input, DateTime now) =>
        repo.InCollectionWriteTransaction(shape.CollectionId, () => RegisterCore(repo, shape, input, now));

    private static Registration RegisterCore(ConstructorRepository repo, RegistryShape shape,
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

        VerifyShape(repo, shape);

        var designations = Designations(repo, shape);
        var number       = PickNumber(designations, org, eskd, input.Number);
        var execution    = CheckExecution(designations, org, eskd, number, input);

        var designation = execution is { } e
            ? Designation.FormatExecution(org, eskd, number, e)
            : Designation.Format(org, eskd, number);

        var recordId = WriteRow(repo, shape, name, designation, input.Fio,
                                StampFor(shape.DateFieldType, now),
                                project, input.Accounting, input.FirstUse, input.Summary);

        return new Registration(recordId, designation, number, execution);
    }

    /// <summary>
    /// Записать в журнал разом весь список номеров, загруженный файлом
    /// (<see cref="RegistryFile"/>, <see cref="RegistryImport"/>). Отдает число записанных строк.
    ///
    /// Обозначение каждой строки уже готово: номера выдала проверка
    /// (<see cref="RegistryImport.Check"/>) — она одна видит весь список и раздает номера серии
    /// подряд, чего построчная выдача здесь сделать не смогла бы. Дата регистрации, наоборот,
    /// одна на все строки — <paramref name="now"/>: она означает момент, когда строка попала в
    /// журнал, и столбец даты в файле поэтому не читается вовсе.
    ///
    /// ЦЕЛИКОМ ИЛИ НИКАК: сорвалось на любой строке (или человек нажал «Отмена») — убираются все
    /// записанные до нее. Иначе в журнале осталась бы часть файла, и разобрать, какая именно,
    /// можно было бы только сличением строка за строкой.
    /// </summary>
    /// <param name="progress">Сколько строк уже записано — для окна прогресса.</param>
    public static int RegisterAll(ConstructorRepository repo, RegistryShape shape,
                                  IReadOnlyList<ImportRow> rows, DateTime now,
                                  IProgress<int>? progress = null,
                                  CancellationToken ct = default) =>
        repo.InCollectionWriteTransaction(shape.CollectionId,
            () => RegisterAllCore(repo, shape, rows, now, progress, ct));

    private static int RegisterAllCore(ConstructorRepository repo, RegistryShape shape,
                                      IReadOnlyList<ImportRow> rows, DateTime now,
                                      IProgress<int>? progress, CancellationToken ct)
    {
        VerifyShape(repo, shape);
        var written = 0;
        // Номера подготовлены до начала записи: за время чтения файла сосед мог занять
        // обозначение. Повторная проверка идет под тем же замком коллекции, что и запись.
        var occupied = Designations(repo, shape).Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            // Второй рубеж: строки приходят проверенными (RegistryImport), но окно — не
            // единственный вход в запись, а строка без обозначения или без наименования
            // сделала бы журнал непригодным молча.
            if (row.Designation.Trim().Length == 0)
                throw new DocRegistryException(
                    $"Строка {row.Line}: не заполнено «{RegistrySchema.DesignationColumn}».");

            if (row.Name.Trim().Length == 0)
                throw new DocRegistryException(
                    $"Строка {row.Line}: не заполнено «{RegistrySchema.TitleColumn}».");

            if (!occupied.Add(row.Designation.Trim()))
                throw new DocRegistryException($"{row.Designation.Trim()} уже зарегистрировано.");

            WriteRow(repo, shape, row.Name, row.Designation, row.Fio, StampFor(shape.DateFieldType, now),
                row.Project, row.Accounting, row.FirstUse, row.Summary);

            written++;
            progress?.Report(written);
        }

        return written;
    }

    /// <summary>
    /// То же самое, но на СВОЕМ подключении к базе и одной транзакцией на весь список. Так пишет
    /// окно: работа уходит в фон, а окно остается живым и показывает прогресс.
    ///
    /// Отдельное подключение — потому что общий контекст EF принадлежит UI-потоку и делить его с
    /// фоном нельзя (тот же прием, что у массовых операций репозитория и у «Документов в
    /// коллекцию»). Прав такое подключение не несет: разрешение спрашивается на общем
    /// репозитории до вызова — см. <c>DocRegistryWindow.RegisterFromFile</c>.
    ///
    /// Транзакция на весь список — не только про целость, но и про СКОРОСТЬ. Строка журнала это
    /// запись плюс семь значений, и каждое значение репозиторий коммитит сам (само значение,
    /// аудит, очередь уведомлений): восемь с лишним коммитов на строку, а коммит на общем файле —
    /// это цикл захвата и снятия блокировки с записью на диск. Пять строк из файла успевали
    /// заметно подумать. Под своей транзакцией <c>SaveChanges</c> внутри репозитория попадает в
    /// нее же (см. <c>ConstructorRepository.WriteScope</c>: чужую транзакцию шаги не трогают), и
    /// на весь файл выходит один коммит.
    /// </summary>
    public static int RegisterAllIsolated(string dbPath, RegistryShape shape,
                                          IReadOnlyList<ImportRow> rows, DateTime now,
                                          IProgress<int>? progress = null,
                                          CancellationToken ct = default)
    {
        using var ctx = new AppDbContext(dbPath);
        ctx.ApplyBulkPerformancePragmas();

        var repo = new ConstructorRepository(ctx);

        // RegisterAll захватывает коллекцию до проверки уже занятых обозначений и коммитит
        // весь файл вместе. Отдельная внешняя транзакция здесь помешала бы этому порядку.
        return RegisterAll(repo, shape, rows, now, new IsolatedProgress(ctx, progress), ct);
    }

    private sealed class IsolatedProgress(AppDbContext context, IProgress<int>? progress) : IProgress<int>
    {
        public void Report(int value)
        {
            progress?.Report(value);
            // Только наше отдельное подключение: строки уже сохранены в общей транзакции.
            // Ограничиваем трекер одной строкой, иначе вложенные savepoint-снимки и аудит
            // обходили бы растущий список всех прежних документов на каждом поле.
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Одна строка журнала: запись с названием и семь значений по столбцам. Отдает id записи.
    ///
    /// Запись дописывается в конец: журнал регистрации ведется по порядку выдачи номеров.
    /// afterRecordId = 0 — записи с таким id не бывает, и репозиторий по контракту ставит такую
    /// запись в конец (одним индексным MAX, без загрузки коллекции в память).
    ///
    /// Ошибка откатывается внешним scope вместе со всеми полями и журналом изменений.
    /// </summary>
    private static int WriteRow(ConstructorRepository repo, RegistryShape shape,
                                string title, string designation, string fio, string stamp,
                                string project, string accounting, string firstUse, string summary)
    {
        var record = repo.AddRecordAfter(new DbRecord
        {
            CollectionId = shape.CollectionId,
            Title        = title,
        }, afterRecordId: 0);

        var recordId = record.Id!.Value;

        Set(repo, recordId, shape.DesignationFieldId, designation);
        Set(repo, recordId, shape.FioFieldId,         fio);
        Set(repo, recordId, shape.DateFieldId,        stamp);
        Set(repo, recordId, shape.ProjectFieldId,     project);
        Set(repo, recordId, shape.AccountingFieldId,  accounting);
        Set(repo, recordId, shape.FirstUseFieldId,    firstUse);
        Set(repo, recordId, shape.SummaryFieldId,     summary);

        var stored = repo.GetFieldValues(recordId);
        foreach (var (field, value) in new[]
        {
            (shape.DesignationFieldId, designation), (shape.FioFieldId, fio),
            (shape.DateFieldId, stamp), (shape.ProjectFieldId, project),
            (shape.AccountingFieldId, accounting), (shape.FirstUseFieldId, firstUse),
            (shape.SummaryFieldId, summary),
        })
            if (!string.IsNullOrWhiteSpace(value) && !stored.ContainsKey(field))
                throw new DocRegistryException("Структура журнала изменилась, строка не зарегистрирована.");

        return recordId;
    }

    private static void VerifyShape(ConstructorRepository repo, RegistryShape shape)
    {
        var check = RegistrySchema.Check(repo, shape.CollectionId, shape.CollectionName);
        if (check.Shape != shape)
            throw new DocRegistryException("Структура журнала изменилась. Проверьте выбранную коллекцию.");
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
