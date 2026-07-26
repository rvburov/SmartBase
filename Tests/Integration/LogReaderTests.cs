using Microsoft.Data.Sqlite;
using SmartBase.Logging;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Чтение диагностического лога <c>logs.db</c> (<see cref="LogReader"/>): keyset-пагинация
/// (порядок id и строковая сортировка), фильтры по колонкам и счётчики значений. В бою схему
/// таблицы <c>logs</c> задаёт приёмник Serilog.Sinks.SQLite — здесь воспроизводим её и сеем
/// строки напрямую (LogReader только читает, raw SQL, без EF).
/// </summary>
public sealed class LogReaderTests
{
    // Схема таблицы logs приёмника + засев строк (в порядке аргументов = по возрастанию id
    // = хронология). Pooling=false — не держим файл в пуле, read-only reader откроет чисто.
    private static void Seed(string path,
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

    // Пролистать журнал постранично до конца (как LoadMoreLogsAsync в UI): курсор —
    // (AfterValue, AfterId) последней строки; rawOf даёт сырое значение колонки сортировки.
    private static async Task<List<LogReader.LogRow>> ReadAll(
        LogReader reader, LogReader.LogQuery baseQuery, int take,
        Func<LogReader.LogRow, string?> rawOf)
    {
        var all = new List<LogReader.LogRow>();
        LogReader.LogRow? last = null;
        while (true)
        {
            var q = baseQuery with
            {
                AfterId    = last?.Id,
                AfterValue = last is null ? null : rawOf(last),
            };
            var page = await reader.GetPageAsync(q, take);
            all.AddRange(page);
            if (page.Count < take) break;
            last = page[^1];
        }
        return all;
    }

    [Fact]
    public async Task Missing_file_returns_empty()
    {
        using var dir = new TempDir();
        var reader = new LogReader(dir.File("nope.db"));

        Assert.False(reader.Exists);
        Assert.Empty(await reader.GetPageAsync(new LogReader.LogQuery(), 100));
        Assert.Empty(await reader.GetCountsAsync("level"));
    }

    [Fact]
    public async Task Default_order_is_newest_first()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-27 00:00:01", "Information", "первая", null),
            ("2026-07-27 00:00:02", "Warning",     "вторая", null),
            ("2026-07-27 00:00:03", "Error",       "третья", "boom"));
        var reader = new LogReader(path);

        var page = await reader.GetPageAsync(new LogReader.LogQuery(), 100);

        Assert.Equal(3, page.Count);
        Assert.Equal("третья", page[0].Message);   // id DESC — новейшая сверху
        Assert.Equal("первая", page[2].Message);
        Assert.True(page[0].Id > page[1].Id && page[1].Id > page[2].Id);
    }

    [Fact]
    public async Task Keyset_paging_covers_every_row_once()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        var seed = Enumerable.Range(1, 5)
            .Select(i => ($"2026-07-27 00:00:0{i}", "Information", $"msg{i}", (string?)null))
            .ToArray();
        Seed(path, seed);
        var reader = new LogReader(path);

        var all = await ReadAll(reader, new LogReader.LogQuery(), take: 2, _ => null);

        Assert.Equal(5, all.Count);
        Assert.Equal(5, all.Select(r => r.Id).Distinct().Count());   // без дублей/пропусков
        for (var i = 1; i < all.Count; i++)                          // страницы стыкуются встык
            Assert.True(all[i - 1].Id > all[i].Id);
    }

    [Fact]
    public async Task Filter_by_level_narrows_rows()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-27 00:00:01", "Information", "a", null),
            ("2026-07-27 00:00:02", "Error",       "b", "x"),
            ("2026-07-27 00:00:03", "Error",       "c", "y"),
            ("2026-07-27 00:00:04", "Warning",     "d", null));
        var reader = new LogReader(path);

        var q = new LogReader.LogQuery
        {
            Filters = new Dictionary<string, IReadOnlyCollection<string>> { ["level"] = ["Error"] },
        };
        var page = await reader.GetPageAsync(q, 100);

        Assert.Equal(2, page.Count);
        Assert.All(page, r => Assert.Equal("Error", r.Level));
    }

    [Fact]
    public async Task Empty_exception_filter_matches_null_and_blank()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-27 00:00:01", "Information", "no-exc-null",  null),
            ("2026-07-27 00:00:02", "Information", "no-exc-blank", ""),
            ("2026-07-27 00:00:03", "Error",       "has-exc",      "boom"));
        var reader = new LogReader(path);

        var q = new LogReader.LogQuery
        {
            Filters = new Dictionary<string, IReadOnlyCollection<string>> { ["exception"] = [""] },
        };
        var page = await reader.GetPageAsync(q, 100);

        Assert.Equal(2, page.Count);   // IFNULL: NULL и '' — одна корзина «(Пусто)»
        Assert.DoesNotContain(page, r => r.Message == "has-exc");
    }

    [Fact]
    public async Task Sort_by_message_ascending_pages_in_order()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-27 00:00:01", "Information", "banana", null),
            ("2026-07-27 00:00:02", "Information", "apple",  null),
            ("2026-07-27 00:00:03", "Information", "cherry", null),
            ("2026-07-27 00:00:04", "Information", "date",   null));
        var reader = new LogReader(path);

        var q = new LogReader.LogQuery { SortColumn = "message", SortAsc = true };
        var all = await ReadAll(reader, q, take: 2, r => r.Message);   // курсор по значению+id

        Assert.Equal(new[] { "apple", "banana", "cherry", "date" },
                     all.Select(r => r.Message).ToArray());
    }

    [Fact]
    public async Task Sort_by_message_descending()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-27 00:00:01", "Information", "banana", null),
            ("2026-07-27 00:00:02", "Information", "apple",  null),
            ("2026-07-27 00:00:03", "Information", "cherry", null));
        var reader = new LogReader(path);

        var q = new LogReader.LogQuery { SortColumn = "message", SortAsc = false };
        var page = await reader.GetPageAsync(q, 100);

        Assert.Equal(new[] { "cherry", "banana", "apple" },
                     page.Select(r => r.Message).ToArray());
    }

    [Fact]
    public async Task Date_sort_ascending_is_chronological()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-25 10:00:00", "Information", "old",    null),
            ("2026-07-26 10:00:00", "Information", "mid",    null),
            ("2026-07-27 10:00:00", "Information", "newest", null));
        var reader = new LogReader(path);

        var q = new LogReader.LogQuery { SortColumn = "date", SortAsc = true };
        var page = await reader.GetPageAsync(q, 100);

        Assert.Equal(new[] { "old", "mid", "newest" },
                     page.Select(r => r.Message).ToArray());
        Assert.True(page[0].Id < page[1].Id && page[1].Id < page[2].Id);   // date = порядок id
    }

    [Fact]
    public async Task Counts_group_by_level()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-27 00:00:01", "Error",       "a", "x"),
            ("2026-07-27 00:00:02", "Error",       "b", "y"),
            ("2026-07-27 00:00:03", "Information", "c", null));
        var reader = new LogReader(path);

        var counts = await reader.GetCountsAsync("level");

        Assert.Equal(2, counts.Single(c => c.Value == "Error").Count);
        Assert.Equal(1, counts.Single(c => c.Value == "Information").Count);
    }

    [Fact]
    public async Task Counts_by_date_group_by_day()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-26 09:00:00", "Information", "a", null),
            ("2026-07-27 09:00:00", "Information", "b", null),
            ("2026-07-27 23:00:00", "Information", "c", null));
        var reader = new LogReader(path);

        var counts = await reader.GetCountsAsync("date");

        Assert.Equal(1, counts.Single(c => c.Value == "2026-07-26").Count);
        Assert.Equal(2, counts.Single(c => c.Value == "2026-07-27").Count);   // substr(1,10) по дню
    }

    [Fact]
    public async Task Counts_by_exception_merge_null_and_blank()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        Seed(path,
            ("2026-07-27 00:00:01", "Information", "a", null),
            ("2026-07-27 00:00:02", "Information", "b", ""),
            ("2026-07-27 00:00:03", "Error",       "c", "boom"));
        var reader = new LogReader(path);

        var counts = await reader.GetCountsAsync("exception");

        Assert.Equal(2, counts.Single(c => c.Value == "").Count);   // NULL + '' → одна корзина
        Assert.Equal(1, counts.Single(c => c.Value == "boom").Count);
    }
}
