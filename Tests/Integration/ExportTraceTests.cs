using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Models;
using SmartBase.Database;

namespace SmartBase.Tests.Integration;

/// <summary>
/// След ручной выгрузки в журнале безопасности (<see cref="ExportTrace"/>, docs/ROADMAP-API.md,
/// шаг 6).
///
/// Проверяется здесь не запись в журнал как таковая — это дело <c>SecurityJournalTests</c>, — а
/// то, ЧТО именно попадает в строку про файл: отпечаток снят с готового файла, объем настоящий,
/// метка закрытой базы видна пояснением, значений полей нет ни при каких настройках. И главное —
/// ответ «следа не осталось», по которому вызывающий удаляет файл.
///
/// Набор идет в общей очереди шлюза: журнал безопасности один на приложение
/// (<c>SecurityJournal.Attach</c>).
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class ExportTraceTests : IDisposable
{
    private readonly string _journal =
        Path.Combine(Path.GetTempPath(), $"sbexp_{Guid.NewGuid():N}.db");

    private readonly string _folder =
        Directory.CreateTempSubdirectory("sbexpfiles_").FullName;

    public void Dispose()
    {
        SecurityJournal.Attach(null);
        try { SqliteConnection.ClearAllPools(); } catch { /* best-effort */ }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_journal + suffix)) File.Delete(_journal + suffix); }
            catch { /* временный файл */ }
        }

        try { Directory.Delete(_folder, recursive: true); } catch { /* временная папка */ }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);

    // ── Стенд ─────────────────────────────────────────────────────────────────

    private SecurityJournal Attach()
    {
        var journal = new SecurityJournal(_journal);
        SecurityJournal.Attach(journal);
        return journal;
    }

    /// <summary>
    /// Файл «выгрузки» с заданным содержимым — отпечаток снимается с него. Пишется байтами, без
    /// метки порядка байт: тест сверяет хэш с <c>UTF8.GetBytes</c>, и невидимые три байта в
    /// начале файла развели бы их, ничего при этом не объяснив.
    /// </summary>
    private string File_(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    private static ExportedCollection Sheet(string name, long rows, string[] columns,
                                            ConfidentialityLabel label = ConfidentialityLabel.Open,
                                            int dbId = 1) =>
        new(dbId, name, rows, columns, label);

    // ── Строка о файле ────────────────────────────────────────────────────────

    [Fact]
    public void Note_writes_one_row_about_the_file()
    {
        var journal = Attach();
        var path    = File_("Заказы.csv", "Название;Сумма\nЗаказ 1;100\n");

        Assert.True(ExportTrace.Note(
            path, [Sheet("Заказы", 1, ["Название", "Сумма"])], Now));

        var row = journal.Recent().Single();

        Assert.Equal(SecurityEvent.Export, row.Event);
        Assert.True(row.Allowed);                       // выгрузка разрешена, след — не запрет
        Assert.Equal(path,                 row.Address);
        Assert.Equal("Название, Сумма",    row.Fields);
        Assert.Equal(1,                    row.Rows);
        Assert.Equal(1,                    row.DbId);
        Assert.Equal("",                   row.Scenario);   // нажал человек, а не расписание
        Assert.Contains("«Заказы» (1)",    row.Explanation);
    }

    /// <summary>
    /// Отпечаток — единственный способ сверить принесенный файл с записью, поэтому он должен
    /// совпадать с хэшем содержимого байт в байт, а объем — с длиной файла.
    /// </summary>
    [Fact]
    public void Fingerprint_and_size_are_taken_from_the_finished_file()
    {
        var journal = Attach();
        var content = "Название;Сумма\nЗаказ 1;100\nЗаказ 2;200\n";
        var path    = File_("Заказы.csv", content);

        ExportTrace.Note(path, [Sheet("Заказы", 2, ["Название", "Сумма"])], Now);

        var row      = journal.Recent().Single();
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

        Assert.Equal(expected,                              row.BodyHash);
        Assert.Equal(new FileInfo(path).Length,             row.Bytes);
    }

    /// <summary>
    /// Книга с листом на коллекцию — ОДНА строка: наружу ушел один файл, и три строки об одном
    /// предмете заставили бы делить его объем между листами.
    /// </summary>
    [Fact]
    public void Workbook_with_several_sheets_gives_one_row()
    {
        var journal = Attach();
        var path    = File_("Выгрузка.xlsx", "книга");

        ExportTrace.Note(path,
        [
            Sheet("Заказы",     3, ["Название", "Сумма"]),
            Sheet("Контрагенты", 2, ["Название", "ИНН"]),
        ], Now);

        var row = journal.Recent().Single();

        Assert.Equal(5, row.Rows);                                  // строки сложились
        Assert.Equal("Название, Сумма, ИНН", row.Fields);           // повторы убраны
        Assert.Contains("«Заказы» (3)",      row.Explanation);
        Assert.Contains("«Контрагенты» (2)", row.Explanation);
    }

    /// <summary>
    /// Книга из РАЗНЫХ баз не принадлежит ни одной: назвать первую значило бы соврать отбору по
    /// базе на экране журнала.
    /// </summary>
    [Fact]
    public void File_from_several_databases_belongs_to_none()
    {
        var journal = Attach();
        var path    = File_("Свод.xlsx", "книга");

        ExportTrace.Note(path,
        [
            Sheet("Заказы", 1, ["Название"], dbId: 1),
            Sheet("Люди",   1, ["Название"], dbId: 2),
        ], Now);

        Assert.Equal(0, journal.Recent().Single().DbId);
    }

    // ── Метки конфиденциальности ──────────────────────────────────────────────

    /// <summary>
    /// Выгрузка из закрытой базы не запрещается — запрет на исходящие про сетевую дверь, — но
    /// метка обязана быть видна в строке: это первое, что проверяющий ищет глазами.
    /// </summary>
    [Fact]
    public void Label_of_closed_base_is_named_in_the_row()
    {
        var journal = Attach();
        var path    = File_("Секретное.csv", "данные");

        Assert.True(ExportTrace.Note(
            path,
            [Sheet("Изделия", 1, ["Название"], ConfidentialityLabel.Closed)],
            Now));

        Assert.StartsWith("Закрытый контур.", journal.Recent().Single().Explanation);
    }

    /// <summary>Ничего не помечено — пояснение не начинается с метки, иначе она обесценится.</summary>
    [Fact]
    public void Open_data_adds_no_label_to_the_row()
    {
        var journal = Attach();
        var path    = File_("Обычное.csv", "данные");

        ExportTrace.Note(path, [Sheet("Заказы", 1, ["Название"])], Now);

        Assert.StartsWith("выгружено руками:", journal.Recent().Single().Explanation);
    }

    /// <summary>Самая строгая метка среди листов — она и уходит в строку.</summary>
    [Fact]
    public void Strictest_label_among_sheets_wins()
    {
        var journal = Attach();
        var path    = File_("Смесь.xlsx", "книга");

        ExportTrace.Note(path,
        [
            Sheet("Заказы",  1, ["Название"]),
            Sheet("Изделия", 1, ["Чертеж"], ConfidentialityLabel.Restricted),
        ], Now);

        Assert.StartsWith("Для служебного пользования.", journal.Recent().Single().Explanation);
    }

    // ── Что в строку НЕ попадает ──────────────────────────────────────────────

    /// <summary>
    /// Имена столбцов — да, значения — никогда: журнал выгружают проверяющему целиком, и
    /// значение поля в нем стало бы второй утечкой следом за первой.
    /// </summary>
    [Fact]
    public void Values_never_reach_the_row()
    {
        var journal = Attach();
        var path    = File_("Люди.csv", "Фамилия\nИванов\n");

        ExportTrace.Note(path, [Sheet("Люди", 1, ["Фамилия"])], Now);

        var row = journal.Recent().Single();

        Assert.DoesNotContain("Иванов", row.Fields);
        Assert.DoesNotContain("Иванов", row.Explanation);
    }

    /// <summary>
    /// Коллекция на двести свойств не должна превращать строку журнала в простыню: лишние
    /// обозначаются числом.
    /// </summary>
    [Fact]
    public void Long_column_list_is_cut_with_a_count()
    {
        var journal = Attach();
        var path    = File_("Широкая.csv", "данные");
        var columns = Enumerable.Range(1, ExportTrace.MaxFields + 5)
                                .Select(n => $"Поле {n}").ToArray();

        ExportTrace.Note(path, [Sheet("Широкая", 1, columns)], Now);

        var row = journal.Recent().Single();

        Assert.Contains("Поле 1,",   row.Fields);
        Assert.Contains("и еще 5",   row.Fields);
        Assert.DoesNotContain($"Поле {ExportTrace.MaxFields + 5}", row.Fields);
    }

    // ── Правило «не записалось — не выгружаем» ────────────────────────────────

    /// <summary>
    /// Журнала нет — <see cref="ExportTrace.Ready"/> честно отвечает «нет» ДО начала выгрузки.
    /// Узнать об этом, когда файл уже лежит на диске, поздно.
    /// </summary>
    [Fact]
    public void Without_journal_export_is_not_ready()
    {
        SecurityJournal.Attach(null);

        Assert.False(ExportTrace.Ready);
    }

    /// <summary>
    /// Журнала нет — след не лег, и об этом сказано ответом, а не молчанием: вызывающий по
    /// этому ответу удаляет готовый файл.
    /// </summary>
    [Fact]
    public void Without_journal_note_reports_no_trace()
    {
        SecurityJournal.Attach(null);

        Assert.False(ExportTrace.Note(
            File_("Заказы.csv", "данные"), [Sheet("Заказы", 1, ["Название"])], Now));
    }

    /// <summary>
    /// Файл не прочитался — строка все равно ложится, без отпечатка: потерять запись о выгрузке
    /// из-за занятого файла было бы обменом наоборот.
    /// </summary>
    [Fact]
    public void Unreadable_file_still_leaves_a_trace()
    {
        var journal = Attach();
        var missing = Path.Combine(_folder, "которого-нет.csv");

        Assert.True(ExportTrace.Note(missing, [Sheet("Заказы", 1, ["Название"])], Now));

        var row = journal.Recent().Single();

        Assert.Equal("", row.BodyHash);
        Assert.Equal(0,  row.Bytes);
        Assert.Equal(missing, row.Address);
    }

    /// <summary>Строка о выгрузке встает в ту же цепочку хэшей, что и отправки.</summary>
    [Fact]
    public void Export_row_continues_the_hash_chain()
    {
        var journal = Attach();

        journal.Write(SecurityEntry.New(SecurityEvent.Send, allowed: true, "Иванов И. И.", "",
                                        "https://api.zavod.local/orders", "Название", 1, 10,
                                        SecurityJournal.Sha256("тело"), "разрешено", 1, Now));

        ExportTrace.Note(File_("Заказы.csv", "данные"),
                         [Sheet("Заказы", 1, ["Название"])], Now.AddMinutes(1));

        Assert.Equal(0, journal.Verify());   // 0 — цепочка цела
    }
}
