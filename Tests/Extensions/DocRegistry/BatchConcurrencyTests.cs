using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Общая коллекция, отдельные подключения: пакет не делится между регистраторами.</summary>
public sealed class BatchConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Sqlite_competing_batches_keep_every_document_and_its_common_fields() =>
        CompetingWriters(postgres: false, mixed: false);

    [PgBatchFact]
    public void Postgres_competing_batches_keep_every_document_and_its_common_fields() =>
        CompetingWriters(postgres: true, mixed: false);

    [Fact]
    public void Sqlite_batch_single_and_import_share_the_same_numbering_lock() =>
        CompetingWriters(postgres: false, mixed: true);

    [PgBatchFact]
    public void Postgres_batch_single_and_import_share_the_same_numbering_lock() =>
        CompetingWriters(postgres: true, mixed: true);

    [PgBatchFact]
    public void Postgres_confirmation_rechecks_existing_series_even_without_a_number_collision()
    {
        using var database = new Fixture(postgres: true);
        using var context = database.OpenContext();
        var repo = new ConstructorRepository(context);
        const string text = "Действие: Регистрация\nНомер: АБВГ.301121.ХХХ, АБВГ.806121.ХХХ\n"
            + "Наименование: Подтверждаемый пакет\nПроект: Стенд\nФИО: Иванов Иван Иванович";
        var preview = Call(repo, database.Shape, text);
        Assert.True(preview.Ok, preview.Explanation);
        Assert.Equal(0, preview.Rows);
        Assert.Contains("АБВГ.301121.002", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Contains("АБВГ.806121.001", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);
        Assert.Contains("Код классификации не найден", ReplyMarkup.Text(preview.Answer), StringComparison.Ordinal);

        string before;
        using (var otherContext = database.OpenContext())
        {
            var other = new ConstructorRepository(otherContext);
            DocRegistrar.Register(other, database.Shape,
                new RegistrationInput("Другой регистратор", "АБВГ", "301121", "Петров Петр Петрович", "Другой проект", Number: 20),
                Now.LocalDateTime);
            before = Snapshot(otherContext);
        }

        // 020 не занял показанный номер 002. Отказ все равно нужен: подтверждался снимок
        // всего пакета, включая существующую серию, а не только отсутствие коллизии.
        var refused = Call(repo, database.Shape, "Да");
        Assert.False(refused.Ok);
        Assert.Equal(0, refused.Rows);
        Assert.Contains("Зарегистрировано: 0 из 2.", ReplyMarkup.Text(refused.Answer), StringComparison.Ordinal);
        Assert.Contains("Серия изменилась", ReplyMarkup.Text(refused.Answer), StringComparison.Ordinal);
        using (var check = database.OpenContext())
        {
            Assert.Equal(before, Snapshot(check));
            Assert.DoesNotContain(DocRegistrar.Designations(new ConstructorRepository(check), database.Shape),
                d => Designation.NumberIn(d, "АБВГ", "806121") is not null);
        }
        Assert.False(Call(repo, database.Shape, "Да").Ok);

        Assert.True(Call(repo, database.Shape, text).Ok);
        var done = Call(repo, database.Shape, "Да");
        Assert.True(done.Ok, done.Explanation);
        Assert.Equal(2, done.Rows);
        Assert.Contains("Зарегистрировано: 2 из 2.", ReplyMarkup.Text(done.Answer), StringComparison.Ordinal);
        using var finalContext = database.OpenContext();
        var finalRepo = new ConstructorRepository(finalContext);
        Assert.Equal(5, finalRepo.GetRecords(database.Shape.CollectionId).Count);
        var designations = DocRegistrar.Designations(finalRepo, database.Shape);
        Assert.Contains("АБВГ.301121.002", designations);
        Assert.Contains("АБВГ.806121.001", designations);
    }

    [PgBatchFact]
    public void Postgres_failure_on_the_second_document_rolls_back_the_complete_batch_and_history()
    {
        using var database = new Fixture(postgres: true);
        using var context = database.OpenContext();
        var repo = new ConstructorRepository(context);
        var before = Snapshot(context);
        context.Database.ExecuteSqlRaw("""
            CREATE FUNCTION sb_batch_reject_second() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.value = 'АБВГ.301121.003' THEN
                    RAISE EXCEPTION 'batch fixture failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER sb_batch_reject_second BEFORE INSERT ON db_field_values
                FOR EACH ROW EXECUTE FUNCTION sb_batch_reject_second();
            """);
        ActionResult? result = null;
        var error = Record.Exception(() =>
        {
            result = Call(repo, database.Shape,
                "Действие: Регистрация\nНомер: АБВГ.301121.ХХХ, АБВГ.301121.ХХХ, АБВГ.301122.ХХХ\n"
                + "Наименование: Сбойный пакет\nПроект: Стенд\nФИО: Иванов Иван Иванович");
        });
        Assert.True(error is not null || result is { Ok: false }, "Сбой второй строки не должен давать успешный ответ");
        using var check = database.OpenContext();
        Assert.Equal(before, Snapshot(check));
        Assert.False(Call(repo, database.Shape, "Да").Ok);
    }

    private static string Snapshot(AppDbContext context) => JsonSerializer.Serialize(new
    {
        Records = context.DbRecords.AsNoTracking().OrderBy(r => r.Id).Select(r => new
            { r.Id, r.CollectionId, r.Title, r.CreatedAt, r.UpdatedAt, r.CreatedBy, r.UpdatedBy, r.Position }).ToList(),
        Values = context.DbFieldValues.AsNoTracking().OrderBy(v => v.RecordId).ThenBy(v => v.FieldId)
            .Select(v => new { v.RecordId, v.FieldId, v.Value }).ToList(),
        Log = context.ChangeLog.AsNoTracking().OrderBy(l => l.Id).ToList(),
    });

    private static void CompetingWriters(bool postgres, bool mixed)
    {
        var database = new Fixture(postgres);
        ConcurrentTasks.Run(4, index => new Client(index), (_, index, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            using var context = database.OpenContext();
            var repo = new ConstructorRepository(context);
            if (!mixed || index < 2) return RegisterBatch(repo, database.Shape, index);
            if (index == 2)
            {
                var result = Call(repo, database.Shape,
                    "Действие: Регистрация\nНомер: АБВГ.301121.ХХХ\nНаименование: Одиночный\n"
                    + "Проект: Проект одиночного\nФИО: Иванов Иван Иванович", index);
                Assert.True(result.Ok, result.Explanation);
                Assert.Equal(1, result.Rows);
                Assert.StartsWith("Зарегистрировано\nНомер: АБВГ.301121.", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
                var row = Assert.Single(repo.GetRecords(database.Shape.CollectionId), r => r.Title == "Одиночный");
                return new[] { repo.GetFieldValues(row.Id!.Value)[database.Shape.DesignationFieldId] };
            }

            // Импорт получает уже подготовленные номера, но повторно проверяет их под тем же
            // замком коллекции. 050 заведомо дальше диапазона, нужного четырем участникам.
            ImportRow[] imported =
            [
                new(2, "Импорт", "АБВГ.301121.050", "Петров Петр Петрович", "Проект импорта", "", "", ""),
                new(3, "Импорт", "АБВГ.301122.050", "Петров Петр Петрович", "Проект импорта", "", "", ""),
            ];
            Assert.Equal(2, DocRegistrar.RegisterAll(repo, database.Shape, imported, Now.LocalDateTime));
            return imported.Select(r => r.Designation).ToArray();
        }, verify: results =>
        {
            using var context = database.OpenContext();
            var repo = new ConstructorRepository(context);
            var rows = repo.GetRecords(database.Shape.CollectionId);
            var expected = mixed ? 9 : 12;
            Assert.Equal(expected, results.Sum(r => r.Length));
            Assert.Equal(expected, results.SelectMany(r => r).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(expected + 2, rows.Count);
            var values = repo.GetFieldValuesBatch(rows.Select(r => r.Id!.Value).ToList());
            var actual = rows.Select(r => values[r.Id!.Value][database.Shape.DesignationFieldId]).ToArray();
            Assert.Equal(actual.Length, actual.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(results.SelectMany(r => r).Append("АБВГ.301121.001").Append("АБВГ.301122.001").Order(),
                actual.Order());

            for (var index = 0; index < (mixed ? 2 : 4); index++)
            {
                var group = rows.Where(r => r.Title == $"Пакет {index}").ToArray();
                Assert.Equal(3, group.Length);
                Assert.All(group, row =>
                {
                    var fields = values[row.Id!.Value];
                    Assert.Equal($"Проект {index}", fields[database.Shape.ProjectFieldId]);
                    Assert.Equal("Иванов Иван Иванович", fields[database.Shape.FioFieldId]);
                    Assert.True(DateValue.TryParseStorage(fields[database.Shape.DateFieldId], out var date));
                    Assert.Equal(Now.LocalDateTime, date.Start);
                });
                // Оба одинаковых шаблона одного пакета получили разные последовательные
                // номера, третий шаблон относится к другой серии того же проекта.
                var numbers = results[index].Select(d => Designation.NumberIn(d, "АБВГ", "301121"))
                    .OfType<int>().Order().ToArray();
                Assert.Equal(2, numbers.Length);
                Assert.Equal(numbers[0] + 1, numbers[1]);
                Assert.Single(results[index], d => Designation.NumberIn(d, "АБВГ", "301122") is not null);
            }

            Assert.Equal(0, context.DbFieldValues.Count(v => !context.DbRecords.Any(r => r.Id == v.RecordId)));
            Assert.Equal(0, context.DbFieldValues.Count(v => !context.DbFields.Any(f => f.Id == v.FieldId)));
            Assert.Equal(0, context.DbFieldValues.GroupBy(v => new { v.RecordId, v.FieldId }).Count(g => g.Count() > 1));
            if (!postgres)
            {
                context.Database.OpenConnection();
                using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA integrity_check";
                Assert.Equal("ok", command.ExecuteScalar());
                command.CommandText = "PRAGMA foreign_key_check";
                using var reader = command.ExecuteReader();
                Assert.False(reader.Read());
            }
        }, shared: database);
    }

    private static string[] RegisterBatch(ConstructorRepository repo, RegistryShape shape, int index)
    {
        var text = "Действие: Регистрация\n"
            + "Номер: АБВГ.301121.ХХХ, АБВГ.301121.ХХХ, АБВГ.301122.ХХХ\n"
            + $"Наименование: Пакет {index}\nПроект: Проект {index}\nФИО: Иванов Иван Иванович";
        var result = Call(repo, shape, text, index);
        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(3, result.Rows);
        Assert.Contains("Зарегистрировано: 3 из 3.", ReplyMarkup.Text(result.Answer), StringComparison.Ordinal);
        var records = repo.GetRecords(shape.CollectionId).Where(r => r.Title == $"Пакет {index}").ToArray();
        Assert.Equal(3, records.Length);
        return records.Select(r => repo.GetFieldValues(r.Id!.Value)[shape.DesignationFieldId]).ToArray();
    }

    private static ActionResult Call(ConstructorRepository repo, RegistryShape shape, string text, int index = 0) =>
        new RegisterMessageAction().Run(new ActionContext(repo, 1, shape.CollectionId, Now,
            new Dictionary<string, string> { [DocRegistryExtension.OrgCodesKey] = "АБВГ" },
            Channel: $"batch-{index}"), new ActionInput(new Dictionary<string, string>
            {
                [RegisterMessageAction.TextField] = text,
                [RegisterMessageAction.SenderField] = $"sender-{index}",
            }));

    private sealed record Client(int Index) : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>Не очищает общие базы smartbase/smartbase2 и не зависит от сборки Integration.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase? _sqlite;
        private readonly string? _postgresDatabase;
        private DatabaseManager? _manager;
        private bool _created;
        public string Path { get; }
        public RegistryShape Shape { get; }

        public Fixture(bool postgres)
        {
            if (!postgres)
            {
                _sqlite = new TempDatabase("batch-concurrency");
                Path = _sqlite.Path;
                _manager = _sqlite.Manager;
            }
            else
            {
                _postgresDatabase = $"sb_batch_{Guid.NewGuid():N}";
                Path = PgBatchFactAttribute.ConnectionString(_postgresDatabase);
            }
            try
            {
                if (_postgresDatabase is not null)
                {
                    Admin($"CREATE DATABASE \"{_postgresDatabase}\"");
                    _created = true;
                    _manager = DatabaseManager.Open(1, "batch-concurrency", Path, provider: "postgres");
                }
                var repo = _manager!.Constructor;
                Shape = JournalStand.Shape(repo, JournalStand.Create(repo));
                foreach (var code in new[] { "301121", "301122" })
                    DocRegistrar.Register(repo, Shape,
                        new RegistrationInput("Существующий", "АБВГ", code, "Иванов Иван Иванович", "Исходный проект", Number: 1),
                        Now.LocalDateTime);
            }
            catch { Dispose(); throw; }
        }

        public AppDbContext OpenContext()
        {
            var context = new AppDbContext(Path);
            try { context.Database.SetCommandTimeout(20); context.ApplyPerformancePragmas(); return context; }
            catch { context.Dispose(); throw; }
        }

        private static void Admin(string sql)
        {
            using var connection = new NpgsqlConnection(PgBatchFactAttribute.ConnectionString("postgres"));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (_sqlite is not null) { _sqlite.Dispose(); return; }
            try { _manager?.Dispose(); }
            finally
            {
                if (_created)
                {
                    Admin($"DROP DATABASE \"{_postgresDatabase}\" WITH (FORCE)");
                    _created = false;
                }
            }
        }
    }
}

/// <summary>Без локального тестового сервера сценарии PG видны как пропущенные.</summary>
public sealed class PgBatchFactAttribute : FactAttribute
{
    // Это одноразовый стенд с заведомо тестовыми учетными данными, как PgTestServer.
    internal static string ConnectionString(string database) => new NpgsqlConnectionStringBuilder
    {
        Host = "localhost", Port = 5455, Username = "postgres", Password = "postgres", Database = database,
        Pooling = false, Timeout = 3, CommandTimeout = 20,
    }.ConnectionString;

    public PgBatchFactAttribute()
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString("postgres"));
            connection.Open();
        }
        catch { Skip = "Локальный тестовый PostgreSQL на 5455 недоступен"; }
    }
}
