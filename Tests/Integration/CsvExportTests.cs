using System.Text;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Exporters;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Logging;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Выгрузка таблиц в CSV кнопкой навбара: журнал изменений («Профиль → История изменений»)
/// и диагностика («Настройки → Диагностика»). Окно не тестируется — проверяется то,
/// из чего оно состоит: файл (<see cref="CsvDump"/>) и потоки строк источников
/// (<c>ReadChangeLogCsvRowsIsolated</c> / <see cref="LogReader.ReadCsvRows"/>).
///
/// Главные инварианты: в файле лежит ровно то, что показано на экране (человеческие
/// подписи, тот же порядок, те же фильтры), страницы keyset не теряют и не дублируют
/// строки на объёме больше страницы, а недописанный файл после отмены не остаётся.
/// </summary>
public sealed class CsvExportTests
{
    // ── CsvDump: сам файл ─────────────────────────────────────────────────────

    [Fact]
    public void Dump_writes_bom_header_and_escapes_values()
    {
        using var dir = new TempDir();
        var path = dir.File("dump.csv");

        var written = CsvDump.Write(path, ["Колонка A", "Колонка B"],
        [
            ["простое", "значение"],
            ["текст с ; и \"кавычками\"", "перенос\nстроки"],
        ]);

        Assert.Equal(2, written);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], "CSV должен начинаться с BOM — иначе Excel ломает кириллицу");

        var text = File.ReadAllText(path, Encoding.UTF8);
        Assert.Contains("\r\n", text);   // CRLF, как у экспорта коллекций
        Assert.StartsWith("Колонка A;Колонка B\r\n", text);
        Assert.Contains("\"текст с ; и \"\"кавычками\"\"\";\"перенос\nстроки\"", text);
    }

    [Fact]
    public void Dump_overwrites_an_existing_file()
    {
        using var dir = new TempDir();
        var path = dir.File("dump.csv");
        File.WriteAllText(path, "старое содержимое");

        CsvDump.Write(path, ["Колонка"], [["новое"]]);

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(["Колонка", "новое"], lines);
    }

    /// <summary>
    /// Отмена посреди выгрузки не должна оставлять обрезанный файл: половина журнала
    /// выглядит как целый журнал, и это худший вид ошибки — незаметный.
    /// </summary>
    [Fact]
    public void Cancelling_removes_the_half_written_file()
    {
        using var dir = new TempDir();
        var path = dir.File("dump.csv");
        using var cts = new CancellationTokenSource();

        IEnumerable<IReadOnlyList<string>> Rows()
        {
            for (var i = 0; i < 1000; i++)
            {
                if (i == 10) cts.Cancel();
                yield return [$"строка {i}"];
            }
        }

        Assert.Throws<OperationCanceledException>(
            () => CsvDump.Write(path, ["Колонка"], Rows(), cts.Token));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_failing_source_removes_the_file_too()
    {
        using var dir = new TempDir();
        var path = dir.File("dump.csv");

        IEnumerable<IReadOnlyList<string>> Rows()
        {
            yield return ["ок"];
            throw new InvalidOperationException("источник упал");
        }

        Assert.Throws<InvalidOperationException>(() => CsvDump.Write(path, ["Колонка"], Rows()));
        Assert.False(File.Exists(path));
    }

    // ── Журнал изменений ──────────────────────────────────────────────────────

    [Fact]
    public void Change_log_export_writes_what_the_table_shows()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();

        var col = SampleData.AddCollection(db.Constructor, "Товары");
        SampleData.AddRecord(db.Constructor, col.Id!.Value, "Договор");
        db.Constructor.DeleteCollection(col.Id!.Value);

        var path = dir.File("журнал.csv");
        var rows = db.Constructor.ReadChangeLogCsvRowsIsolated(new ConstructorRepository.ChangeLogQuery());
        CsvDump.Write(path, ConstructorRepository.ChangeLogCsvHeaders, rows);

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal("Пользователь;Действие;Тип;Объект;Коллекция;Дата и время", lines[0]);

        // Порядок как в таблице — новые сверху: последним было удаление коллекции.
        var first = lines[1].Split(';');
        Assert.Equal("Удаление", first[1]);
        Assert.Equal("Коллекция", first[2]);
        Assert.StartsWith("Товары", first[3]);

        // Русские подписи и локальное время вместо «create»/«collection»/ISO.
        var text = string.Join('\n', lines);
        Assert.Contains("Создание;Коллекция;Товары", text);
        Assert.DoesNotContain("create;", text);
        Assert.DoesNotContain("collection;", text);
        Assert.Matches(@"\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}", text);
    }

    /// <summary>
    /// Фильтр таблицы уезжает в выгрузку: пользователь отобрал «Создание» — в файле
    /// должны быть только созданные объекты, а счётчик окна подтверждения обязан
    /// показывать то же число строк.
    /// </summary>
    [Fact]
    public async Task Change_log_export_respects_filters_and_count()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();

        var col = SampleData.AddCollection(db.Constructor, "Клиенты");
        SampleData.AddRecord(db.Constructor, col.Id!.Value, "Первая");
        SampleData.AddRecord(db.Constructor, col.Id!.Value, "Вторая");
        db.Constructor.DeleteCollection(col.Id!.Value);

        var query = new ConstructorRepository.ChangeLogQuery
        {
            Filters = new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["action"] = new HashSet<string> { "create" },
            },
        };

        var expected = await db.Constructor.CountChangeLogAsync(query);
        var path = dir.File("создания.csv");
        var written = CsvDump.Write(path, ConstructorRepository.ChangeLogCsvHeaders,
                                    db.Constructor.ReadChangeLogCsvRowsIsolated(query));

        Assert.Equal(expected, written);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(expected + 1, lines.Length);                   // + шапка
        Assert.All(lines.Skip(1), l => Assert.Contains(";Создание;", l));
        Assert.DoesNotContain(lines, l => l.Contains(";Удаление;"));
    }

    /// <summary>
    /// Больше одной страницы keyset: строки не теряются и не дублируются. Это главный
    /// риск выгрузки на 10 млн строк — на границе страниц.
    /// </summary>
    [Fact]
    public async Task Change_log_export_pages_through_everything_without_gaps()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();

        var col = SampleData.AddCollection(db.Constructor, "Много");
        SampleData.AddRecords(db.Constructor, col.Id!.Value, 250);

        var query = new ConstructorRepository.ChangeLogQuery();
        var total = await db.Constructor.CountChangeLogAsync(query);
        Assert.True(total > 200, "нужен объём больше страницы");

        var path = dir.File("страницы.csv");
        // Страница нарочно мелкая — чтобы курсор прошёл много кругов на скромных данных.
        var written = CsvDump.Write(path, ConstructorRepository.ChangeLogCsvHeaders,
                                    db.Constructor.ReadChangeLogCsvRowsIsolated(query, pageSize: 25));

        Assert.Equal(total, written);
        var lines = File.ReadAllLines(path, Encoding.UTF8).Skip(1).ToList();
        Assert.Equal(total, lines.Count);
        Assert.Equal(lines.Count, lines.Distinct().Count());   // дублей нет
    }

    /// <summary>
    /// Сортировка по колонке (не по дате) листается курсором «значение + Id».
    /// Главный инвариант выгрузки: в файле — ровно та же последовательность строк,
    /// что показала бы таблица с этими настройками (сортировка идёт по СЫРЫМ значениям,
    /// поэтому сверяемся с таблицей, а не с алфавитом русских подписей).
    /// </summary>
    [Fact]
    public async Task Change_log_export_matches_what_the_table_would_show()
    {
        using var db  = new TempDatabase();
        using var dir = new TempDir();

        var col = SampleData.AddCollection(db.Constructor, "Сорт");
        SampleData.AddRecords(db.Constructor, col.Id!.Value, 120);

        var query = new ConstructorRepository.ChangeLogQuery { SortColumn = "type", SortAsc = true };
        var total = await db.Constructor.CountChangeLogAsync(query);

        // Как листает таблица: страницами с курсором последней показанной строки.
        var onScreen = new List<string>();
        ConstructorRepository.ChangeLogRow? last = null;
        while (true)
        {
            var page = await db.Constructor.GetChangeLogPageAsync(
                query with { AfterId = last?.Id, AfterValue = last?.TargetType }, 20);
            onScreen.AddRange(page.Select(r => ChangeLogLabels.TypeLabel(r.TargetType)));
            if (page.Count < 20) break;
            last = page[^1];
        }

        var path = dir.File("сортировка.csv");
        var written = CsvDump.Write(path, ConstructorRepository.ChangeLogCsvHeaders,
                                    db.Constructor.ReadChangeLogCsvRowsIsolated(query, pageSize: 20));

        Assert.Equal(total, written);
        var inFile = File.ReadAllLines(path, Encoding.UTF8).Skip(1)
                         .Select(l => l.Split(';')[2]).ToList();
        Assert.Equal(onScreen, inFile);
    }

    // ── Диагностика (logs.db) ─────────────────────────────────────────────────

    // Схема таблицы logs приёмника Serilog + засев строк (как в LogReaderTests).
    private static void SeedLogs(string path,
        params (string Timestamp, string Level, string Message, string? Exception)[] rows)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling    = false,
        }.ToString());
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE logs (" +
                "id INTEGER PRIMARY KEY AUTOINCREMENT, " +
                "Timestamp TEXT, Level TEXT, Exception TEXT, " +
                "RenderedMessage TEXT, Properties TEXT)";
            create.ExecuteNonQuery();
        }

        foreach (var (ts, level, msg, ex) in rows)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO logs (Timestamp, Level, Exception, RenderedMessage, Properties) " +
                "VALUES (@t, @l, @e, @m, '{}')";
            ins.Parameters.AddWithValue("@t", ts);
            ins.Parameters.AddWithValue("@l", level);
            ins.Parameters.AddWithValue("@e", (object?)ex ?? DBNull.Value);
            ins.Parameters.AddWithValue("@m", msg);
            ins.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Logs_export_writes_human_readable_values()
    {
        using var dir = new TempDir();
        var dbPath = dir.File("logs.db");
        SeedLogs(dbPath,
            ("2026-08-15 17:39:37", "Information", "Запуск SmartBase", null),
            ("2026-08-15 17:40:01", "Warning",     "Медленный запрос; 2 с", null),
            ("2026-08-15 17:41:12", "Error",       "Сбой", "System.Exception: сбой"));

        var reader = new LogReader(dbPath);
        var path   = dir.File("диагностика.csv");
        var written = CsvDump.Write(path, LogReader.CsvHeaders,
                                    reader.ReadCsvRows(new LogReader.LogQuery()));

        Assert.Equal(3, written);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal("Время;Уровень;Сообщение;Ошибка", lines[0]);

        // Новые сверху — как в таблице; уровни по-русски; «;» в сообщении экранирован.
        Assert.Contains(";Ошибка;", lines[1]);
        Assert.Contains("\"Медленный запрос; 2 с\"", lines[2]);
        Assert.Contains(";Информация;", lines[3]);

        var local = new DateTime(2026, 8, 15, 17, 39, 37, DateTimeKind.Utc).ToLocalTime();
        Assert.StartsWith(local.ToString("dd.MM.yyyy HH:mm:ss") + ";", lines[3]);
    }

    [Fact]
    public async Task Logs_export_respects_filters_and_count()
    {
        using var dir = new TempDir();
        var dbPath = dir.File("logs.db");
        SeedLogs(dbPath,
            ("2026-08-15 17:39:37", "Information", "Запуск", null),
            ("2026-08-15 17:40:01", "Warning",     "Внимание", null),
            ("2026-08-15 17:41:12", "Error",       "Сбой", "трейс"));

        var reader = new LogReader(dbPath);
        var query  = new LogReader.LogQuery
        {
            Filters = new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["level"] = new HashSet<string> { "Warning", "Error" },
            },
        };

        Assert.Equal(2, await reader.CountAsync(query));

        var path    = dir.File("важное.csv");
        var written = CsvDump.Write(path, LogReader.CsvHeaders, reader.ReadCsvRows(query));

        Assert.Equal(2, written);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        Assert.Equal(3, lines.Length);
        Assert.DoesNotContain(lines, l => l.Contains(";Информация;"));
    }

    [Fact]
    public void Logs_export_pages_through_everything_without_gaps()
    {
        using var dir = new TempDir();
        var dbPath = dir.File("logs.db");
        var seed = Enumerable.Range(0, 500)
            .Select(i => ($"2026-08-15 10:{i / 60 % 60:00}:{i % 60:00}", "Information", $"Сообщение {i}", (string?)null))
            .ToArray();
        SeedLogs(dbPath, seed);

        var reader  = new LogReader(dbPath);
        var path    = dir.File("все.csv");
        var written = CsvDump.Write(path, LogReader.CsvHeaders,
                                    reader.ReadCsvRows(new LogReader.LogQuery(), pageSize: 40));

        Assert.Equal(500, written);
        var lines = File.ReadAllLines(path, Encoding.UTF8).Skip(1).ToList();
        Assert.Equal(500, lines.Distinct().Count());
    }

    [Fact]
    public async Task Missing_log_file_exports_nothing()
    {
        using var dir = new TempDir();
        var reader = new LogReader(dir.File("нет-такого.db"));

        Assert.Equal(0, await reader.CountAsync(new LogReader.LogQuery()));
        Assert.Empty(reader.ReadCsvRows(new LogReader.LogQuery()));
    }
}
