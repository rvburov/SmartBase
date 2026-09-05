using System.Diagnostics;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

/// <summary>
/// Производительность PostgreSQL-провайдера на путях, которыми ходит приложение
/// (репозиторий, не голый SQL): массовая заливка (BulkAddRecords — путь импорта),
/// одиночные вставки, keyset-страницы (первая и самая глубокая должны стоить одинаково —
/// row-value сик O(take)), offset-чтение для контраста, значения пакета, фильтр колонки,
/// точечные правки, дубль и удаление пакета. Требует стенд sb-pg; схема public базы
/// СБРАСЫВАЕТСЯ, как в PgProviderTests, и подчищается в конце.
///
/// Запуск: dotnet run -c Release --project Tests/Benchmarks -- pgperf [--rows N] [--db ИМЯ]
/// </summary>
internal static class PgPerfBench
{
    private const int PageSize  = 200;
    private const int BatchSize = 1000;   // одиночные вставки / правки / дубль / удаление

    public static int Run(string[] args)
    {
        var rows = BenchUtil.ArgInt(args, "--rows") ?? 50_000;
        var db   = BenchUtil.ArgStr(args, "--db") ?? "smartbase";
        var cs   = $"Host=localhost;Port=5455;Username=postgres;Password=postgres;Database={db};Timeout=5";

        Console.WriteLine($"Производительность PostgreSQL: {rows} записей × 6 полей (база {db})");
        Console.WriteLine($"Внимание: схема public базы {db} сбрасывается (как в PgProviderTests).");
        try
        {
            using var probe = new NpgsqlConnection(cs);
            probe.Open();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Стенд PostgreSQL sb-pg (порт 5455) недоступен: {ex.Message}");
            Console.WriteLine("Поднимите его — см. docs/developer/testing/automated-tests.md.");
            return 1;
        }

        ResetSchema(cs);
        try
        {
            return Measure(cs, rows);
        }
        finally
        {
            ResetSchema(cs);   // стенд остается пустым для тестов
        }
    }

    private static int Measure(string cs, int rows)
    {
        using var m = DatabaseManager.Open(9101, "pgperf", cs, provider: "postgres");
        var repo = m.Constructor;

        var col      = repo.AddCollection(new DbCollection { Name = "Перф" }).Id!.Value;
        var fName    = AddField(repo, col, "Название объекта", "text",   10);
        var fComment = AddField(repo, col, "Комментарий",      "text",   20);
        var fCount   = AddField(repo, col, "Количество",       "number", 30);
        var fDate    = AddField(repo, col, "Дата",             "date",   40);
        var fStatus  = AddField(repo, col, "Статус",           "status", 50);
        var fOwner   = AddField(repo, col, "Ответственный",    "user",   60);
        var fieldIds = new[] { fName, fComment, fCount, fDate, fStatus, fOwner };

        string[] statuses = ["Новая", "В работе", "На проверке", "Готово", "Отменена"];

        // 1. Массовая заливка — путь импорта (BulkAddRecords, пакеты по 10 000).
        var sw = Stopwatch.StartNew();
        var bulkIds = repo.BulkAddRecords(col, fieldIds, BulkRows(rows, statuses), rows,
                                          CancellationToken.None);
        sw.Stop();
        Report("Массовая заливка (BulkAddRecords)", sw,
               $"{rows / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} записей/с");

        // 2. Одиночные вставки — путь «человек добавляет запись» (запись + 2 значения).
        sw.Restart();
        for (var i = 0; i < BatchSize; i++)
        {
            var rec = repo.AddRecord(new DbRecord
            { CollectionId = col, Title = $"Одиночная {i}", Position = 1_000_000_000 - i });
            repo.SetFieldValue(rec.Id!.Value, fName,   $"Объект {i}");
            repo.SetFieldValue(rec.Id!.Value, fStatus, statuses[i % statuses.Length]);
        }
        sw.Stop();
        Report($"Одиночные вставки ({BatchSize} записей)", sw,
               $"{BatchSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} записей/с");

        var total = rows + BatchSize;

        // 3. Счетчик записей (заголовок коллекции в сайдбаре).
        sw.Restart();
        var count = repo.CountRecords(col);
        sw.Stop();
        Report("CountRecords", sw, $"строк: {count}");
        if (count != total)
        {
            Console.WriteLine($"РАСХОЖДЕНИЕ: ожидалось {total} записей");
            return 1;
        }

        // 4–5. Keyset-чтение всей коллекции страницами по PageSize: первая и самая глубокая
        // страница обязаны стоить сопоставимо — row-value сик не зависит от глубины.
        sw.Restart();
        var pageSw    = Stopwatch.StartNew();
        var firstPage = repo.GetRecordsPageAfter(col, null, null, PageSize);
        pageSw.Stop();
        var firstMs = pageSw.Elapsed.TotalMilliseconds;

        var page     = firstPage;
        var pages    = 1;
        var deepMs   = firstMs;
        var lastRead = firstPage;
        while (page.Count == PageSize)
        {
            var tail = page[^1];
            pageSw.Restart();
            page = repo.GetRecordsPageAfter(col, tail.Position, tail.Id!.Value, PageSize);
            pageSw.Stop();
            if (page.Count > 0) { pages++; deepMs = pageSw.Elapsed.TotalMilliseconds; lastRead = page; }
        }
        sw.Stop();
        Report($"Keyset-скан ({pages} страниц × {PageSize})", sw,
               $"{pages / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} страниц/с");
        Console.WriteLine($"    первая страница: {firstMs:F1} мс, самая глубокая: {deepMs:F1} мс");

        // 6. Offset-чтение середины — контраст к keyset (OFFSET сканирует все пропущенное).
        sw.Restart();
        repo.GetRecordsPage(col, total / 2, PageSize);
        sw.Stop();
        Report($"Offset-чтение середины (skip {total / 2})", sw);

        // 7. Значения одной страницы (ячейки таблицы).
        var pageIds = lastRead.Select(r => r.Id!.Value).ToList();
        sw.Restart();
        var values = repo.GetFieldValuesBatch(pageIds);
        sw.Stop();
        Report($"GetFieldValuesBatch ({pageIds.Count} записей)", sw, $"строк значений: {values.Count}");

        // 8. Фильтр колонки «Статус» (окно фильтра + отбор таблицы).
        var filters = new Dictionary<string, HashSet<string>>
        { [fStatus.ToString()] = ["В работе"] };
        sw.Restart();
        var filtered = repo.GetFilteredRecordIds(col, filters);
        sw.Stop();
        Report("GetFilteredRecordIds (Статус = В работе)", sw, $"найдено: {filtered.Count}");

        // 9. Точечные правки случайных записей (редактирование ячеек).
        sw.Restart();
        for (var i = 0; i < BatchSize; i++)
            repo.SetFieldValue(bulkIds[Random.Shared.Next(bulkIds.Count)], fComment, $"правка {i}");
        sw.Stop();
        Report($"Точечные правки ({BatchSize} значений)", sw,
               $"{BatchSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0} оп/с");

        // 10–11. Дубль и удаление пакета (PG-путь дубля — отдельный код с картой id).
        var dupSrc = bulkIds.Take(BatchSize).ToList();
        sw.Restart();
        var dupIds = repo.DuplicateRecordsData(col, dupSrc);
        sw.Stop();
        Report($"Дубль пакета ({BatchSize} записей)", sw);

        sw.Restart();
        repo.DeleteRecordsData(dupIds);
        sw.Stop();
        Report($"Удаление пакета ({BatchSize} записей)", sw);

        Console.WriteLine("ЗАМЕР ЗАВЕРШЕН");
        return 0;
    }

    // Строки массовой заливки потоком: значения в порядке fieldIds (см. Measure).
    private static IEnumerable<BulkRecordRow> BulkRows(int rows, string[] statuses)
    {
        string[] owners = Enumerable.Range(1, 20).Select(i => $"Сотрудник {i:D2}").ToArray();
        for (var i = 1; i <= rows; i++)
        {
            var date = new DateTime(2026, 1, 1).AddDays(i % 365).ToString("yyyy-MM-dd");
            yield return new BulkRecordRow($"Запись {i:D6}",
            [
                $"Объект {i}",
                $"Комментарий к записи {i}: длинное поле, похожее на реальные данные",
                (i % 1000).ToString(),
                date,
                statuses[i % statuses.Length],
                owners[i % owners.Length],
            ]);
        }
    }

    private static int AddField(ConstructorRepository repo, int col, string name, string type, int pos) =>
        repo.AddField(new DbField { CollectionId = col, Name = name, FieldType = type, Position = pos }).Id!.Value;

    private static void Report(string label, Stopwatch sw, string? extra = null)
    {
        var tail = extra is null ? "" : $"  ({extra})";
        Console.WriteLine($"{label,-46} {BenchUtil.Fmt(sw.Elapsed.TotalMilliseconds),10}{tail}");
    }

    private static void ResetSchema(string cs)
    {
        using var conn = new NpgsqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }
}
