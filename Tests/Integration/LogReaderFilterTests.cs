using Microsoft.Data.Sqlite;
using SmartBase.Logging;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Окно фильтра «Диагностики» ведет себя как в таблице конструктора: список значений колонки
/// считается по строкам, прошедшим фильтры остальных колонок (<see cref="LogReader.GetCountsAsync"/>),
/// а «Уровень» сортируется по подписи в ячейке, а не по «Error»/«Warning».
/// </summary>
public sealed class LogReaderFilterTests
{
    // Схема таблицы logs приемника Serilog + засев строк (как в CsvExportTests).
    private static void SeedLogs(string path, params (string Timestamp, string Level, string Message)[] rows)
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

        foreach (var (ts, level, msg) in rows)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText =
                "INSERT INTO logs (Timestamp, Level, Exception, RenderedMessage, Properties) " +
                "VALUES (@t, @l, NULL, @m, '{}')";
            ins.Parameters.AddWithValue("@t", ts);
            ins.Parameters.AddWithValue("@l", level);
            ins.Parameters.AddWithValue("@m", msg);
            ins.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task Counts_follow_filters_of_other_columns_but_not_their_own()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        SeedLogs(path,
            ("2026-09-01 10:00:00", "Information", "Запуск"),
            ("2026-09-01 10:01:00", "Warning",     "Медленно"),
            ("2026-09-01 10:02:00", "Warning",     "Запуск"),
            ("2026-09-02 10:03:00", "Error",       "Сбой"));

        var reader   = new LogReader(path);
        var warnings = new Dictionary<string, IReadOnlyCollection<string>> { ["level"] = ["Warning"] };

        // Отобрали предупреждения — в «Сообщении» только их сообщения.
        var messages = await reader.GetCountsAsync("message", warnings);
        Assert.Equal(new Dictionary<string, int> { ["Медленно"] = 1, ["Запуск"] = 1 },
                     messages.ToDictionary(c => c.Value, c => c.Count));

        // Свой фильтр «Уровня» список уровней не режет.
        var levels = await reader.GetCountsAsync("level", warnings);
        Assert.Equal(new Dictionary<string, int> { ["Information"] = 1, ["Warning"] = 2, ["Error"] = 1 },
                     levels.ToDictionary(c => c.Value, c => c.Count));
    }

    [Fact]
    public async Task Sort_by_level_follows_labels_and_pages_do_not_lose_rows()
    {
        using var dir = new TempDir();
        var path = dir.File("logs.db");
        SeedLogs(path,
            ("2026-09-01 10:00:00", "Warning",     "1"),
            ("2026-09-01 10:01:00", "Error",       "2"),
            ("2026-09-01 10:02:00", "Information", "3"),
            ("2026-09-01 10:03:00", "Debug",       "4"),
            ("2026-09-01 10:04:00", "Error",       "5"),
            ("2026-09-01 10:05:00", "Fatal",       "6"));

        var reader = new LogReader(path);
        var query  = new LogReader.LogQuery { SortColumn = "level", SortAsc = true };

        var all    = await reader.GetPageAsync(query, 1000);
        var labels = all.Select(r => LogLabels.LevelLabel(r.Level)).ToList();
        // «Информация, Критическая, Ошибка, Отладка, Предупреждение» — по-русски от А до Я.
        Assert.Equal(labels.OrderBy(l => l, StringComparer.Ordinal), labels);
        Assert.Equal("Information", all[0].Level);

        var paged = new List<LogReader.LogRow>();
        LogReader.LogRow? last = null;
        while (true)
        {
            var page = await reader.GetPageAsync(query with
            {
                AfterId    = last?.Id,
                AfterValue = last is null ? null : LogReader.SortValue("level", last),
            }, 1);
            if (page.Count == 0) break;
            paged.AddRange(page);
            last = page[^1];
        }

        Assert.Equal(all.Select(r => r.Id), paged.Select(r => r.Id));
    }
}
