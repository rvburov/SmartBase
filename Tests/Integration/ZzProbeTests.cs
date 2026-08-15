using System.Text;
using Microsoft.Data.Sqlite;
using SmartBase.Core.Exporters;
using SmartBase.Database.Repositories;
using SmartBase.Logging;

namespace SmartBase.Tests.Integration;

/// <summary>Временные зондирующие тесты (удалить).</summary>
public sealed class ZzProbeTests
{
    private static void SeedLogs(string path,
        params (string Timestamp, string? Level, string? Message, string? Exception)[] rows)
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
            ins.Parameters.AddWithValue("@l", (object?)level ?? DBNull.Value);
            ins.Parameters.AddWithValue("@e", (object?)ex ?? DBNull.Value);
            ins.Parameters.AddWithValue("@m", (object?)msg ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }
    }

    // ── A: логи, сортировка по строковой колонке, много страниц ────────────────
    [Theory]
    [InlineData("level", true)]
    [InlineData("level", false)]
    [InlineData("message", true)]
    [InlineData("message", false)]
    [InlineData("exception", true)]
    [InlineData("exception", false)]
    [InlineData("date", true)]
    [InlineData("date", false)]
    public async Task Probe_logs_string_sort_pages(string sortKey, bool asc)
    {
        using var dir = new TempDir();
        var dbPath = dir.File("logs.db");
        string[] levels = ["Information", "Warning", "Error", "Debug"];
        var seed = Enumerable.Range(0, 500)
            .Select(i => ($"2026-08-15 10:{i / 60 % 60:00}:{i % 60:00}",
                          (string?)levels[i % levels.Length],
                          (string?)$"Сообщение {i % 7}",
                          i % 10 == 0 ? (string?)"трейс" : null))
            .ToArray();
        SeedLogs(dbPath, seed);

        var reader = new LogReader(dbPath);
        var query  = new LogReader.LogQuery { SortColumn = sortKey, SortAsc = asc };
        var total  = await reader.CountAsync(query);
        Assert.Equal(500, total);

        var rows = reader.ReadCsvRows(query, pageSize: 40).ToList();
        Assert.Equal(500, rows.Count);
    }

    // ── B: логи с NULL в сортируемой колонке ──────────────────────────────────
    [Fact]
    public async Task Probe_logs_null_message_sort()
    {
        using var dir = new TempDir();
        var dbPath = dir.File("logs.db");
        var seed = Enumerable.Range(0, 200)
            .Select(i => ($"2026-08-15 10:00:{i % 60:00}", (string?)"Information",
                          i % 2 == 0 ? null : (string?)$"Сообщение {i}", (string?)null))
            .ToArray();
        SeedLogs(dbPath, seed);

        var reader = new LogReader(dbPath);
        var query  = new LogReader.LogQuery { SortColumn = "message", SortAsc = true };
        var total  = await reader.CountAsync(query);
        var rows   = reader.ReadCsvRows(query, pageSize: 20).ToList();
        Assert.Equal(total, rows.Count);
    }

    // ── C: журнал изменений, строковая сортировка ─────────────────────────────
    private static void SeedChangeLog(string dbPath, int n)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var tx  = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO change_log (Ts, UserKey, UserName, Action, TargetType, TargetName, CollectionName, Count, Detail) " +
            "VALUES (@ts, 'k', 'Тест', @a, @tt, @tn, @cn, NULL, NULL)";
        var ts = cmd.Parameters.Add("@ts", SqliteType.Text);
        var a  = cmd.Parameters.Add("@a",  SqliteType.Text);
        var tt = cmd.Parameters.Add("@tt", SqliteType.Text);
        var tn = cmd.Parameters.Add("@tn", SqliteType.Text);
        var cn = cmd.Parameters.Add("@cn", SqliteType.Text);

        string[] actions = ["create", "update", "delete"];
        string[] types   = ["record", "collection", "field"];
        // Имена с вариантами регистра — проверяем совпадение ORDER BY и предиката курсора.
        string[] names   = ["альфа", "Альфа", "БЕТА", "бета", "гамма"];
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < n; i++)
        {
            ts.Value = start.AddSeconds(i).ToString("o");
            a.Value  = actions[i % actions.Length];
            tt.Value = types[i % types.Length];
            tn.Value = names[i % names.Length];
            cn.Value = i % 4 == 0 ? (object)DBNull.Value : $"Коллекция {i % 3}";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [Theory]
    [InlineData("object", true)]
    [InlineData("object", false)]
    [InlineData("collection", true)]
    [InlineData("collection", false)]
    [InlineData("user", true)]
    [InlineData("action", true)]
    [InlineData("type", false)]
    [InlineData("date", true)]
    [InlineData("date", false)]
    public async Task Probe_changelog_sort_pages(string sortKey, bool asc)
    {
        using var db = new TempDatabase();
        SeedChangeLog(db.Path, 300);

        var query = new ConstructorRepository.ChangeLogQuery { SortColumn = sortKey, SortAsc = asc };
        var total = await db.Constructor.CountChangeLogAsync(query);
        Assert.True(total >= 300);

        var rows = db.Constructor.ReadChangeLogCsvRowsIsolated(query, pageSize: 25).ToList();
        Assert.Equal(total, rows.Count);
    }

    // ── D: пустой источник ────────────────────────────────────────────────────
    [Fact]
    public void Probe_empty_source_writes_header_only()
    {
        using var dir = new TempDir();
        var path = dir.File("пусто.csv");
        var written = CsvDump.Write(path, ["A", "B"], []);
        Assert.Equal(0, written);
        Assert.True(File.Exists(path));
        Assert.Equal(["A;B"], File.ReadAllLines(path, Encoding.UTF8));
    }
}
