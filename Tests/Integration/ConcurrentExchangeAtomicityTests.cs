using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Отказ последней колонки/журнала не оставляет неполную строку; общий ключ захватывается атомарно.</summary>
public sealed class ConcurrentExchangeAtomicityTests
{
    public static IEnumerable<object[]> FaultCases() =>
        from profile in new[] { "sqlite", "sqlite-shared", "postgres" }
        from route in new[] { "binding-field", "binding-log", "scenario-field" }
        from update in new[] { false, true }
        select new object[] { profile, route, update };

    [SkippableTheory]
    [MemberData(nameof(FaultCases))]
    public void A_late_row_failure_rolls_back_record_values_calendar_and_history_then_allows_retry(
        string profile, string route, bool update)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var schema = Setup(fixture.Repo);
        if (update)
        {
            var record = SampleData.AddRecord(fixture.Repo, schema.Collection, "До отказа").Id!.Value;
            fixture.Repo.SetFieldValue(record, schema.Key, "same-key");
            fixture.Repo.SetFieldValue(record, schema.Date, "2026-10-26");
            fixture.Repo.SetFieldValue(record, schema.Value, "Прежнее значение");
        }
        fixture.Manager.Context.ChangeTracker.Clear();
        var before = Snapshot(fixture);
        const string marker = "exchange-atomic-row-failure";
        var logFailure = route == "binding-log";
        InstallFault(fixture.Manager.Context, schema.Value, logFailure, marker);

        var error = Record.Exception(() => Write(fixture.Repo, schema, route, "После отказа", "same-key", "FAIL"));
        Assert.NotNull(error);
        Assert.Contains(marker, error.ToString());
        Assert.Equal(before, Snapshot(fixture));
        using (var check = fixture.OpenContext()) MultiUserTestDatabase.AssertHealthy(check);

        RemoveFault(fixture.Manager.Context, logFailure);
        var result = Write(fixture.Repo, schema, route, "После повтора", "same-key", "Готово");
        Assert.Equal(update ? (0, 1) : (1, 0), result);
        using var recovered = fixture.OpenContext();
        var actual = Assert.Single(recovered.DbRecords.AsNoTracking().Where(record => record.CollectionId == schema.Collection));
        Assert.Equal("После повтора", actual.Title);
        var values = recovered.DbFieldValues.AsNoTracking().Where(value => value.RecordId == actual.Id)
            .ToDictionary(value => value.FieldId, value => value.Value);
        Assert.Equal(3, values.Count);
        Assert.Equal("same-key", values[schema.Key]);
        Assert.Equal("2026-11-02", values[schema.Date]);
        Assert.Equal("Готово", values[schema.Value]);
        Assert.Equal("2026-11-02T00:00:00.0000000", Assert.Single(recovered.CalendarIndex.Where(row => row.RecordId == actual.Id)).StartIso);
        if (route.StartsWith("binding", StringComparison.Ordinal))
            Assert.Equal(1, Assert.Single(recovered.ChangeLog.Where(log => log.Action == "import")).Count);
        MultiUserTestDatabase.AssertHealthy(recovered);
    }

    [SkippableTheory]
    [InlineData("sqlite", "binding")]
    [InlineData("sqlite-shared", "binding")]
    [InlineData("postgres", "binding")]
    [InlineData("sqlite", "scenario")]
    [InlineData("sqlite-shared", "scenario")]
    [InlineData("postgres", "scenario")]
    public void Competing_upserts_of_one_case_insensitive_literal_key_create_one_coherent_row(string profile, string route)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var schema = Setup(fixture.Repo);
        const int clients = 8;
        var results = new ConcurrentBag<(int Created, int Updated)>();
        MultiUserTestDatabase.RunClients(fixture, clients, (repo, _, client) =>
        {
            // Знаки шаблона и регистр кириллицы не должны превращать один ключ в несколько.
            var key = client % 2 == 0 ? "\tОБЩИЙ_%\\КЛЮЧ\u00a0" : " \u2007общий_%\\ключ \r\n";
            results.Add(Write(repo, schema, route, $"Участник {client}", key, $"Итог {client}"));
        });
        Assert.Equal(1, results.Sum(result => result.Created));
        Assert.Equal(clients - 1, results.Sum(result => result.Updated));
        using var check = fixture.OpenContext();
        var record = Assert.Single(check.DbRecords.AsNoTracking().Where(record => record.CollectionId == schema.Collection));
        var values = check.DbFieldValues.AsNoTracking().Where(value => value.RecordId == record.Id)
            .ToDictionary(value => value.FieldId, value => value.Value);
        var clientNumber = int.Parse(record.Title["Участник ".Length..]);
        Assert.Equal($"Итог {clientNumber}", values[schema.Value]);
        Assert.Equal(clientNumber % 2 == 0 ? "\tОБЩИЙ_%\\КЛЮЧ\u00a0" : " \u2007общий_%\\ключ \r\n", values[schema.Key]);
        Assert.Equal(3, values.Count);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    private static Schema Setup(ConstructorRepository repo)
    {
        var collection = SampleData.AddCollection(repo, "Атомарная строка обмена").Id!.Value;
        var key = SampleData.AddField(repo, collection, "Ключ", position: 1000).Id!.Value;
        var date = SampleData.AddCalendarDateField(repo, collection, "Дата").Id!.Value;
        var value = SampleData.AddField(repo, collection, "Значение", position: 3000).Id!.Value;
        var binding = new CollectionBinding(1, collection,
            [new BindingColumn("Название", null), new BindingColumn("Ключ", key),
                new BindingColumn("Дата", date), new BindingColumn("Значение", value)], MatchColumn: "Ключ");
        return new(collection, key, date, value, binding, new ImportSchema("Название", ["Ключ", "Дата", "Значение"], 1, 0));
    }

    private static (int Created, int Updated) Write(ConstructorRepository repo, Schema schema,
        string route, string title, string key, string value)
    {
        if (route.StartsWith("binding", StringComparison.Ordinal))
        {
            var result = repo.ApplyRowsIsolated(schema.Binding, schema.Source,
                [new ImportRow(title, [key, "2026-11-02", value])], CancellationToken.None);
            return (result.Created, result.Updated);
        }
        var made = repo.WriteRecordsIsolated(schema.Collection,
            [new Dictionary<string, string>
            {
                ["Название"] = title, ["Ключ"] = key, ["Дата"] = "2026-11-02", ["Значение"] = value,
            }], CancellationToken.None, matchField: "Ключ");
        Assert.Null(made.Trouble);
        return (made.Created, made.Updated);
    }

    private static string Snapshot(MultiUserTestDatabase fixture)
    {
        using var context = fixture.OpenContext();
        var repo = new ConstructorRepository(context);
        return JsonSerializer.Serialize(new
        {
            Records = context.DbRecords.AsNoTracking().OrderBy(record => record.Id).ToList(),
            Values = context.DbFieldValues.AsNoTracking().OrderBy(value => value.RecordId).ThenBy(value => value.FieldId).ToList(),
            Calendar = context.CalendarIndex.AsNoTracking().OrderBy(row => row.RecordId).ThenBy(row => row.FieldId).ToList(),
            Days = repo.GetCalendarDayCountsByRange(new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), null)
                .Select(day => new { day.Day, day.Count }).ToList(),
            History = context.ChangeLog.AsNoTracking().OrderBy(log => log.Id).ToList(),
            Events = context.TaskEvents.AsNoTracking().OrderBy(row => row.Id).ToList(),
        });
    }

    private static void InstallFault(AppDbContext context, int field, bool log, string marker)
    {
        if (context.IsPostgres)
        {
            var condition = log ? "NEW.action = 'import'" : $"NEW.fieldid = {field} AND NEW.value = 'FAIL'";
            var table = log ? "change_log" : "db_field_values";
            context.Database.ExecuteSqlRaw($"""
                CREATE FUNCTION exchange_atomic_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF {condition} THEN RAISE EXCEPTION '{marker}'; END IF; RETURN NEW; END; $$;
                CREATE TRIGGER exchange_atomic_failure BEFORE INSERT OR UPDATE ON {table}
                FOR EACH ROW EXECUTE FUNCTION exchange_atomic_failure();
                """);
        }
        else
        {
            var condition = log ? "NEW.Action = 'import'" : $"NEW.FieldId = {field} AND NEW.Value = 'FAIL'";
            var table = log ? "change_log" : "db_field_values";
            foreach (var operation in new[] { "INSERT", "UPDATE" })
                context.Database.ExecuteSqlRaw($"""
                    CREATE TRIGGER exchange_atomic_failure_{operation} BEFORE {operation} ON {table}
                    WHEN {condition} BEGIN SELECT RAISE(ABORT, '{marker}'); END;
                    """);
        }
    }

    private static void RemoveFault(AppDbContext context, bool log)
    {
        context.Database.ExecuteSqlRaw(context.IsPostgres
            ? $"DROP TRIGGER exchange_atomic_failure ON {(log ? "change_log" : "db_field_values")}; DROP FUNCTION exchange_atomic_failure();"
            : "DROP TRIGGER exchange_atomic_failure_INSERT; DROP TRIGGER exchange_atomic_failure_UPDATE;");
    }

    private sealed record Schema(int Collection, int Key, int Date, int Value, CollectionBinding Binding, ImportSchema Source);
}
