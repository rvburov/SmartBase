using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

public sealed class ConcurrentEngineTests
{
    [Fact]
    public void Eight_independent_registrations_keep_their_input_fields_and_numbering()
    {
        var databases = ConcurrentTasks.Run(8, index => new Fixture(index), (f, index, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var context = new AppDbContext(f.Database.Path);
            var repo = new ConstructorRepository(context);
            var done = DocRegistrar.Register(repo, f.Shape, f.Input, new DateTime(2026, 10, 26, 10, index, 0));
            Assert.Equal(1, done.Number);
            Assert.Equal("АБВГ.301121.001", done.Designation);
            using var check = new AppDbContext(f.Database.Path);
            var verify = new ConstructorRepository(check);
            var record = Assert.Single(verify.GetRecords(f.Shape.CollectionId));
            var values = verify.GetFieldValues(done.RecordId);
            Assert.Equal(done.RecordId, record.Id);
            Assert.Equal($"Изделие {index}", record.Title);
            Assert.Equal($"Исполнитель {index}", values[f.Shape.FioFieldId]);
            Assert.Equal($"Проект {index}", values[f.Shape.ProjectFieldId]);
            Assert.Equal(done.Designation, values[f.Shape.DesignationFieldId]);
            Assert.True(DateValue.TryParseStorage(values[f.Shape.DateFieldId], out var date));
            Assert.Equal(new DateTime(2026, 10, 26, 10, index, 0), date.Start);
            Assert.Equal(f.InputHash, ConcurrentTasks.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(f.Input)));
            return f.Database.Path;
        });
        Assert.Equal(8, databases.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Eight_registrars_of_one_shared_series_receive_eight_distinct_numbers()
    {
        var journal = new SharedJournal();
        ConcurrentTasks.Run(8, index => new RegistrationJob(index), (job, index, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var context = new AppDbContext(journal.Database.Path);
            var repo = new ConstructorRepository(context);
            return DocRegistrar.Register(repo, journal.Shape, job.Input, new DateTime(2026, 10, 26, 11, index, 0));
        }, verify: results =>
        {
            using var context = new AppDbContext(journal.Database.Path);
            var repo = new ConstructorRepository(context);
            var rows = repo.GetRecords(journal.Shape.CollectionId);
            Assert.Equal(8, rows.Count);
            var values = repo.GetFieldValuesBatch(rows.Select(r => r.Id!.Value).ToList());
            var designations = rows.Select(r => values[r.Id!.Value][journal.Shape.DesignationFieldId]).Order().ToArray();
            Assert.Equal(Enumerable.Range(1, 8).Select(n => $"АБВГ.301121.{n:D3}").ToArray(), designations);
            Assert.All(results, result => Assert.NotNull(result));
            Assert.Equal(8, results.Select(r => r.RecordId).Distinct().Count());
            for (var index = 0; index < results.Length; index++)
            {
                var done = results[index];
                var record = Assert.Single(rows, r => r.Id == done.RecordId);
                Assert.Equal($"Общая регистрация {index}", record.Title);
                Assert.Equal(done.Designation, values[done.RecordId][journal.Shape.DesignationFieldId]);
                Assert.Equal($"Регистратор {index}", values[done.RecordId][journal.Shape.FioFieldId]);
            }
            Assert.Equal(0, context.DbFieldValues.Count(v => !context.DbRecords.Any(r => r.Id == v.RecordId)));
        }, shared: journal);
    }

    [Fact]
    public void The_same_explicit_execution_key_is_published_once_without_confirmation()
    {
        var journal = new SharedJournal();
        ConcurrentTasks.Run(8, index => new RegistrationJob(index), (job, index, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var context = new AppDbContext(journal.Database.Path);
            var repo = new ConstructorRepository(context);
            try
            {
                var done = DocRegistrar.Register(repo, journal.Shape,
                    job.Input with { Number = 1, Execution = 1, ConfirmedDuplicate = false }, new DateTime(2026, 10, 26, 12, index, 0));
                return new Attempt(index, done, false);
            }
            catch (DocRegistryException ex) when (ex.Message.Contains("АБВГ.301121.001-01", StringComparison.Ordinal)
                && ex.Message.Contains("уже зарегистрировано", StringComparison.Ordinal))
            {
                // Занятое исполнение без подтверждения — штатный отказ, а не транспортная
                // ошибка. BUSY, timeout и прочие ошибки этим фильтром не скрываются.
                return new Attempt(index, null, true);
            }
        }, verify: attempts =>
        {
            Assert.All(attempts, attempt => Assert.NotNull(attempt));
            var winner = Assert.Single(attempts, a => a.Done is not null);
            Assert.Equal(7, attempts.Count(a => a.Refused));
            using var context = new AppDbContext(journal.Database.Path);
            var repo = new ConstructorRepository(context);
            var row = Assert.Single(repo.GetRecords(journal.Shape.CollectionId));
            Assert.Equal(winner.Done!.RecordId, row.Id);
            Assert.Equal($"Общая регистрация {winner.Index}", row.Title);
            Assert.Equal("АБВГ.301121.001-01", repo.GetFieldValues(row.Id!.Value)[journal.Shape.DesignationFieldId]);

            // Повтор уже занятого ПОЛНОГО обозначения не должен создать ни запись, ни
            // журнал. Обычный повтор регистрации без Number/Execution по контракту иной:
            // это новая заявка, а не идемпотентный запрос.
            var before = Snapshot(context);
            var denied = Assert.Throws<DocRegistryException>(() => DocRegistrar.Register(repo, journal.Shape,
                new RegistrationJob(99).Input with { Number = 1, Execution = 1, ConfirmedDuplicate = false },
                new DateTime(2026, 10, 26, 13, 0, 0)));
            Assert.Contains("уже зарегистрировано", denied.Message);
            using var check = new AppDbContext(journal.Database.Path);
            Assert.Equal(before, Snapshot(check));
        }, shared: journal);
    }

    private static string Snapshot(AppDbContext context) => JsonSerializer.Serialize(new
    {
        Records = context.DbRecords.AsNoTracking().OrderBy(r => r.Id).Select(r => new
            { r.Id, r.CollectionId, r.Title, r.CreatedAt, r.UpdatedAt, r.CreatedBy, r.UpdatedBy, r.Position }).ToList(),
        Values = context.DbFieldValues.AsNoTracking().OrderBy(v => v.RecordId).ThenBy(v => v.FieldId)
            .Select(v => new { v.RecordId, v.FieldId, v.Value }).ToList(),
        Log = context.ChangeLog.AsNoTracking().OrderBy(l => l.Id).ToList(),
    });

    private sealed record Attempt(int Index, Registration? Done, bool Refused);

    private sealed class SharedJournal : IDisposable
    {
        public TempDatabase Database { get; } = new("shared-registry");
        public RegistryShape Shape { get; }
        public SharedJournal()
        {
            var collection = JournalStand.Create(Database.Constructor);
            Shape = JournalStand.Shape(Database.Constructor, collection);
        }
        public void Dispose() => Database.Dispose();
    }

    private sealed class RegistrationJob(int index) : IDisposable
    {
        public RegistrationInput Input { get; } = new($"Общая регистрация {index}", "АБВГ", "301121",
            $"Регистратор {index}", $"Общий проект {index}");
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new("concurrent-registry");
        public RegistryShape Shape { get; }
        public RegistrationInput Input { get; }
        public string InputHash { get; }
        public Fixture(int index)
        {
            var collection = JournalStand.Create(Database.Constructor, $"Регистрация {index}");
            Shape = JournalStand.Shape(Database.Constructor, collection, $"Регистрация {index}");
            Input = new($"Изделие {index}", "АБВГ", "301121", $"Исполнитель {index}", $"Проект {index}");
            InputHash = ConcurrentTasks.Fingerprint(JsonSerializer.SerializeToUtf8Bytes(Input));
        }
        public void Dispose() => Database.Dispose();
    }
}
