using System.Text.Json;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Массовые и prepared сырые пути на PostgreSQL — все, что пишет мимо EF. SQLite нетипизирован
/// и прощает и параметры без типа, и boolean в integer-колонку, и подзапрос без алиаса —
/// поэтому эти пути ловятся только на живом PG. Класс появился как регресс на 42804
/// «столбец visible имеет тип integer, а выражение — boolean» из выгрузки «Сводной
/// трудоемкости» (BulkSetRowStyle) и на Prepare без DbType в правках значений поля.
/// Каждый тест — SkippableFact: без стенда sb-pg (порт 5455) помечается Skipped.
/// </summary>
public sealed class PgBulkWriteTests
{
    private static void ResetSchema(string connString)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    private static long PgScalar(string connString, string sql)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string PgText(string connString, string sql)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)(cmd.ExecuteScalar() ?? "");
    }

    /// <summary>
    /// Путь «Сводной трудоемкости»: заливка BulkAddRecords и раскраска BulkSetRowStyle/
    /// BulkSetCellStyle. 2500 строк — больше пакета DbBatch (2000 команд), значит id из
    /// RETURNING сшиваются через границу пакета и обязаны идти в порядке строк.
    /// До правки @visible раскраска падала на Prepare с 42804.
    /// </summary>
    [SkippableFact]
    public void Bulk_add_records_and_bulk_styles_pass_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "Свод");
        var deno = SampleData.AddField(repo, col.Id!.Value, "Обозначение", "text", 1000);
        var norm = SampleData.AddField(repo, col.Id!.Value, "Тр-ть", "number", 2000);

        const int total = 2500;
        var ids = repo.BulkAddRecords(col.Id!.Value, [deno.Id!.Value, norm.Id!.Value],
            Enumerable.Range(1, total).Select(i => new BulkRecordRow($"Строка {i:D4}",
                [$"ДЕНО-{i}", i % 2 == 0 ? $"{i}" : ""])),   // пустые значения ячеек не заводятся
            total, CancellationToken.None);

        Assert.Equal(total, ids.Count);
        Assert.Equal(total, repo.CountRecords(col.Id!.Value));
        // Порядок id = порядок строк, включая стык пакетов DbBatch.
        Assert.Equal("Строка 0001", PgText(cs, $"SELECT title FROM db_records WHERE id = {ids[0]}"));
        Assert.Equal($"Строка {total:D4}", PgText(cs, $"SELECT title FROM db_records WHERE id = {ids[^1]}"));
        Assert.Equal(total, PgScalar(cs, $"SELECT COUNT(*) FROM db_field_values WHERE fieldid = {deno.Id!.Value}"));
        Assert.Equal(total / 2, PgScalar(cs, $"SELECT COUNT(*) FROM db_field_values WHERE fieldid = {norm.Id!.Value}"));

        repo.BulkSetRowStyle(col.Id!.Value, ids, StyleAspect.Background, "#FFCC00", CancellationToken.None);
        repo.BulkSetCellStyle(col.Id!.Value,
            ids.Take(500).Select(id => (id, norm.Id!.Value.ToString())).ToList(),
            StyleAspect.Background, "#FF3B30", CancellationToken.None);

        // Флаг visible в PG-схеме — integer 0/1, как и во всех bool-колонках (BoolToZeroOneConverter).
        Assert.Equal(total, PgScalar(cs, $"SELECT COUNT(*) FROM record_display " +
                                         $"WHERE collectionid = {col.Id!.Value} AND bg = '#FFCC00' AND visible = 1"));
        Assert.Equal(500, PgScalar(cs, $"SELECT COUNT(*) FROM cell_style " +
                                       $"WHERE collectionid = {col.Id!.Value} AND bg = '#FF3B30'"));
        Assert.Equal(new RowStyle("#FFCC00", null), repo.GetRowStyles(col.Id!.Value)[ids[0]]);
    }

    /// <summary>
    /// Правки значений при смене настроек поля — prepared-команды, которым Npgsql требует
    /// DbType ДО Prepare: смена десятичного разделителя (NormalizeNumberSeparatorValues)
    /// и смена типа дата → текст (ConvertFieldValuesForTypeChange).
    /// </summary>
    [SkippableFact]
    public void Field_setting_changes_rewrite_stored_values_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "Поля");

        // Число: точка → запятая, сохраненные значения мигрируют (как в NumberFieldFormatTests).
        var num = repo.AddField(new DbField
        {
            CollectionId = col.Id!.Value, Name = "Число", FieldType = "number", Position = 1000,
            ConfigJson   = NumberConfig(DecimalMark.Dot),
        });
        var r1 = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        repo.SetFieldValue(r1.Id!.Value, num.Id!.Value, "1,5");   // хранится «1.5» — поле с точкой

        var numField = repo.GetFields(col.Id!.Value).First(x => x.Id == num.Id);
        numField.ConfigJson = NumberConfig(DecimalMark.Comma);
        repo.UpdateField(numField);
        Assert.Equal("1,5", repo.GetFieldValuesBatch([r1.Id!.Value])[r1.Id!.Value][num.Id!.Value]);

        // Дата → текст: ISO-канон хранения переписывается человекочитаемой датой.
        var date    = SampleData.AddField(repo, col.Id!.Value, "Срок", "date", 2000);
        var day     = new DateTime(2026, 8, 15);
        var storage = new DateValue(day, day.Add(DateValue.EndOfDay), AllDay: true).ToStorage();
        repo.SetFieldValue(r1.Id!.Value, date.Id!.Value, storage);

        var dateField = repo.GetFields(col.Id!.Value).First(x => x.Id == date.Id);
        dateField.FieldType = "text";
        repo.UpdateField(dateField);
        var rewritten = repo.GetFieldValuesBatch([r1.Id!.Value])[r1.Id!.Value][date.Id!.Value];
        Assert.NotEqual(storage, rewritten);
        Assert.Contains("15.08.2026", rewritten);
    }

    /// <summary>Выгрузка журнала изменений в CSV с сортировкой по колонке: до правки ветка
    /// безусловно слала «PRAGMA temp_store=FILE» и падала на PG с 42601.</summary>
    [SkippableFact]
    public void Sorted_changelog_csv_export_streams_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "Журнал");
        SampleData.AddRecords(repo, col.Id!.Value, count: 3, step: 1000);

        var rows = repo.ReadChangeLogCsvRowsIsolated(
            new ConstructorRepository.ChangeLogQuery { SortColumn = "user", SortAsc = true }).ToList();
        Assert.NotEmpty(rows);
    }

    /// <summary>Счет пилюль задач под крышкой: подзапрос во FROM обязан иметь алиас —
    /// SQLite прощает его отсутствие, PG отвечает 42601, и пилюли молча занижались бы.</summary>
    [SkippableFact]
    public void Capped_task_pill_counts_work_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        ResetSchema(cs);

        using var m = DatabaseManager.Open(101, "pg-test", cs, provider: "postgres");
        var repo = m.Constructor;
        var col  = SampleData.AddCollection(repo, "Задачи");
        var due  = SampleData.AddCalendarDateField(repo, col.Id!.Value);
        var exec = SampleData.AddField(repo, col.Id!.Value, "Исполнитель", "user", 2000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);

        var recs = SampleData.AddRecords(repo, col.Id!.Value, count: 5, step: 1000);
        foreach (var r in recs.Take(3))
            repo.SetFieldValue(r.Id!.Value, due.Id!.Value, "2026-09-01");

        var stats = repo.GetTaskCollectionStats();
        Assert.NotEmpty(stats);
        var view   = new ConstructorRepository.TaskView("All", "", ["Тестов Тест"], new DateTime(2026, 9, 16));
        var counts = repo.CountTaskViews(stats, [view], cap: 2);
        Assert.Equal(2, counts["All"]);   // задач 5, крышка 2 — счет уперся в крышку, а не упал
    }

    private static string NumberConfig(DecimalMark mark) =>
        JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(
            NumberFormat.Default with { DecimalSeparator = mark }));
}
