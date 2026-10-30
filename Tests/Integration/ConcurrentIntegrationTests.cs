using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using Xunit.Abstractions;

namespace SmartBase.Tests.Integration;

/// <summary>Конкурирующие фоновые укладки, сценарии и файловый прием.
/// Отмена потокового пакета проверяется по подтвержденной границе строки, а не как откат всего файла.</summary>
[Collection(EgressCollection.Name)]
public sealed class ConcurrentIntegrationTests
{
    private const int Clients = 8;
    private readonly ITestOutputHelper _output;

    public ConcurrentIntegrationTests(ITestOutputHelper output) => _output = output;

    private static (int Collection, int Key, int Value, CollectionBinding Binding, ImportSchema Schema) Setup(
        ConstructorRepository repo)
    {
        var collection = SampleData.AddCollection(repo, "Обмен").Id!.Value;
        var key = SampleData.AddField(repo, collection, "Ключ").Id!.Value;
        var value = SampleData.AddField(repo, collection, "Значение", position: 2000).Id!.Value;
        return (collection, key, value, new CollectionBinding(1, collection,
            [new BindingColumn("Название", null), new BindingColumn("Ключ", key), new BindingColumn("Значение", value)],
            MatchColumn: "Ключ"), new ImportSchema("Название", ["Ключ", "Значение"], 1, 0));
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_bound_imports_and_retries_keep_one_record_per_independent_key(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var setup = Setup(f.Repo);
        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var title = $"Запись {client}";
            var first = repo.ApplyRowsIsolated(setup.Binding, setup.Schema,
                [new ImportRow(title, [$"key-{client}", "Первое"])], CancellationToken.None);
            Assert.Equal(1, first.Created);
            var second = repo.ApplyRowsIsolated(setup.Binding, setup.Schema,
                [new ImportRow(title, [$"key-{client}", "Последнее"])], CancellationToken.None);
            Assert.Equal(0, second.Created);
            Assert.Equal(1, second.Updated);
        });
        using var check = f.OpenContext();
        Assert.Equal(Clients, check.DbRecords.Count(r => r.CollectionId == setup.Collection));
        var keys = check.DbFieldValues.AsNoTracking().Where(v => v.FieldId == setup.Key).ToList();
        Assert.Equal(Clients, keys.Count);
        Assert.Equal(Clients, keys.Select(v => v.Value).Distinct().Count());
        Assert.All(check.DbFieldValues.Where(v => v.FieldId == setup.Value), v => Assert.Equal("Последнее", v.Value));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_record_steps_retry_their_keys_without_crossing_values_between_clients(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var setup = Setup(f.Repo);
        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            IReadOnlyDictionary<string, string> Row(string value) => new Dictionary<string, string>
            {
                ["Название"] = $"Шаг {client}", ["Ключ"] = $"scenario-{client}", ["Значение"] = value,
            };
            var first = repo.WriteRecordsIsolated(setup.Collection, [Row("Первое")], CancellationToken.None,
                matchField: "Ключ", createMissing: true);
            Assert.Null(first.Trouble);
            Assert.Equal(1, first.Created);
            var second = repo.WriteRecordsIsolated(setup.Collection, [Row($"Результат {client}")], CancellationToken.None,
                matchField: "Ключ", createMissing: true);
            Assert.Null(second.Trouble);
            Assert.Equal(0, second.Created);
            Assert.Equal(1, second.Updated);
        });
        using var check = f.OpenContext();
        var records = check.DbRecords.AsNoTracking().Where(r => r.CollectionId == setup.Collection).ToList();
        Assert.Equal(Clients, records.Count);
        for (var client = 0; client < Clients; client++)
        {
            var record = Assert.Single(records.Where(r => r.Title == $"Шаг {client}"));
            var values = check.DbFieldValues.AsNoTracking().Where(v => v.RecordId == record.Id)
                .ToDictionary(v => v.FieldId, v => v.Value);
            Assert.Equal($"scenario-{client}", values[setup.Key]);
            Assert.Equal($"Результат {client}", values[setup.Value]);
        }
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Cancelling_each_import_after_one_committed_row_keeps_exactly_the_acknowledged_prefix(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var setup = Setup(f.Repo);
        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            using var cancel = new CancellationTokenSource();
            var reported = new List<int>();
            var progress = new InlineProgress(done => { reported.Add(done); cancel.Cancel(); });
            var rows = Enumerable.Range(0, 3).Select(row =>
                new ImportRow($"Пакет {client}/{row}", [$"cancel-{client}-{row}", $"Строка {row}"]));
            Assert.Throws<OperationCanceledException>(() =>
                repo.ApplyRowsIsolated(setup.Binding, setup.Schema with { RowCount = 3 }, rows, cancel.Token, progress));
            Assert.Equal(new[] { 1 }, reported);
        });

        using var check = f.OpenContext();
        var records = check.DbRecords.AsNoTracking().Where(r => r.CollectionId == setup.Collection).ToList();
        Assert.Equal(Clients, records.Count);
        Assert.All(records, r => Assert.EndsWith("/0", r.Title));
        Assert.Equal(Clients * 2, check.DbFieldValues.Count());
        Assert.All(check.DbFieldValues.Where(v => v.FieldId == setup.Value), v => Assert.Equal("Строка 0", v.Value));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void One_incoming_file_is_claimed_once_and_repeated_delivery_does_not_reapply_it(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var setup = Setup(f.Repo);
        var root = Directory.CreateTempSubdirectory("sb_concurrent_intake_").FullName;
        var now = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
        try
        {

            var layout = InboxFolder.Open(root, out var explanation);
            Assert.NotNull(layout);
            Assert.Equal("", explanation);
            const string message = """
                {"collection":"Обмен","mode":"add","id":"single-delivery",
                 "rows":[{"Название":"Единственная","Ключ":"inbox-1","Значение":"Принято"}]}
                """;
            void Put(string name)
            {
                var path = System.IO.Path.Combine(layout!.Incoming, name);
                File.WriteAllText(path, message);
                File.SetLastWriteTimeUtc(path, now.AddMinutes(-1).UtcDateTime);
            }
            Put("данные.json");
            DumpInbox("до первичного обхода", layout!, now);
            Assert.Single(InboxFolder.Ready(layout!, now));
            var outcomes = new InboxRun[Clients];
            MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
            {
                outcomes[client] = InboxReceiver.Run(layout!, repo, 1, $"Участник-{client}", now, clock: () => now);
            });
            DumpOutcomes("первичный обход", outcomes);
            DumpInbox("после первичного обхода", layout!, now);
            Assert.Equal(1, outcomes.Sum(r => r.Accepted));
            Assert.Equal(0, outcomes.Sum(r => r.Rejected));
            Assert.Empty(Directory.EnumerateFiles(layout!.Incoming));
            var receiptPath = System.IO.Path.Combine(layout.Receipts, "single-delivery.json");
            var originalReceipt = File.ReadAllBytes(receiptPath);
            Assert.True(InboxReceipt.TryParse(System.Text.Encoding.UTF8.GetString(originalReceipt))!.Accepted);

            Put("повтор.json");
            DumpInbox("до повторного обхода", layout, now);
            Assert.Single(InboxFolder.Ready(layout, now));
            MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
            {
                outcomes[client] = InboxReceiver.Run(layout, repo, 1, $"Повтор-{client}", now, clock: () => now);
            });
            DumpOutcomes("повторный обход", outcomes);
            DumpInbox("после повторного обхода", layout, now);
            Assert.Equal(0, outcomes.Sum(r => r.Accepted));
            Assert.Equal(1, outcomes.Sum(r => r.Repeats));
            Assert.Equal(originalReceipt, File.ReadAllBytes(receiptPath));
            using var check = f.OpenContext();
            var record = Assert.Single(check.DbRecords.AsNoTracking().Where(r => r.CollectionId == setup.Collection));
            var values = check.DbFieldValues.AsNoTracking().Where(v => v.RecordId == record.Id).ToList();
            Assert.Equal(2, values.Count);
            Assert.Equal("inbox-1", Assert.Single(values.Where(v => v.FieldId == setup.Key)).Value);
            Assert.Equal("Принято", Assert.Single(values.Where(v => v.FieldId == setup.Value)).Value);
            MultiUserTestDatabase.AssertHealthy(check);
        }
        finally
        {

            try { Directory.Delete(root, recursive: true); } catch { /* только своя временная папка */ }
        }
    }

    private void DumpOutcomes(string phase, InboxRun[] outcomes)
    {
        for (var client = 0; client < outcomes.Length; client++)
        {
            var run = outcomes[client];
            _output.WriteLine($"{phase}, участник {client}: {JsonSerializer.Serialize(new
            {
                run.Accepted, run.Rejected, run.Repeats, run.Reclaimed, run.Problem, run.Receipts,
            })}");
        }
    }

    private void DumpInbox(string phase, InboxLayout layout, DateTimeOffset now)
    {
        var files = Directory.EnumerateFiles(layout.Root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Select(file => new
            {
                File = System.IO.Path.GetRelativePath(layout.Root, file.FullName),
                file.Length, file.LastWriteTimeUtc,
            }).OrderBy(file => file.File).ToList();
        _output.WriteLine($"{phase}, now={now:o}, файлы: {JsonSerializer.Serialize(files)}");
        _output.WriteLine($"{phase}, готовые: {JsonSerializer.Serialize(InboxFolder.Ready(layout, now).Select(f => f.Name))}");
        var receiptPath = System.IO.Path.Combine(layout.Receipts, "single-delivery.json");
        if (File.Exists(receiptPath))
            _output.WriteLine($"{phase}, квитанция: {File.ReadAllText(receiptPath)}");
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
