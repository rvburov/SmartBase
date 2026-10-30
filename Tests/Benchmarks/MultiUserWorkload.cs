using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Benchmarks;

internal sealed record MultiUserFixture(MultiUserOptions Options, string Connection,
    int SourceCollection, int TargetCollection, int SourceText, int SourceDate,
    int TargetText, int TargetDate, int HotRecord, long InitialLogCount,
    string DatabaseName, int? PostgresMaxConnections, string DataDirectory)
{
    internal static string UserKey(int client) => $"client-{client:D4}@load-test";

    internal static MultiUserFixture Create(MultiUserOptions options, string directory)
    {
        var databaseName = options.Provider == "postgres" ? $"sb_multiuser_{Guid.NewGuid():N}" : "load.db";
        var connection = Path.Combine(directory, databaseName);
        int? maxConnections = null;
        if (options.Provider == "postgres")
        {
            // Только локальный тестовый сервер проекта. Свое уникальное имя; DROP/RESET
            // существующих баз и ввод чужой строки подключения у этого стенда отсутствуют.
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1", Port = 5455, Username = "postgres", Password = "postgres",
                Database = "postgres", Timeout = 30, CommandTimeout = 30,
                MaxPoolSize = options.Clients + 16, ApplicationName = "SmartBase multiuser",
            };
            using (var admin = new NpgsqlConnection(builder.ConnectionString))
            {
                admin.Open();
                using var command = admin.CreateCommand();
                command.CommandText = "SHOW max_connections";
                maxConnections = Convert.ToInt32(command.ExecuteScalar());
                command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                command.ExecuteNonQuery();
            }
            builder.Database = databaseName;
            connection = builder.ConnectionString;
        }
        else if (options.Profile == "shared") NetworkPath.ForceNetwork(connection);

        using var manager = DatabaseManager.Open(901, "Многопользовательский стенд", connection,
            provider: options.Provider == "postgres" ? "postgres" : "sqlite");
        var repo = manager.Constructor;
        var source = repo.AddCollection(new DbCollection { Name = "Источник", Position = 1000 }).Id!.Value;
        var target = repo.AddCollection(new DbCollection { Name = "Назначение", Position = 2000 }).Id!.Value;
        int Field(int collection, string name, string type, bool date = false) => repo.AddField(new DbField
        {
            CollectionId = collection, Name = name, FieldType = type, IsCalStart = date,
            Position = date ? 2000 : 1000,
        }).Id!.Value;
        var text = Field(source, "Текст", "text");
        var date = Field(source, "Дата", "date", true);
        var targetText = Field(target, "Текст", "text");
        var targetDate = Field(target, "Дата", "date", true);
        var now = DateTime.UtcNow.ToString("o");
        manager.Context.DbUsers.AddRange(Enumerable.Range(0, options.Clients).Select(client => new DbUserEntity
        {
            UserKey = UserKey(client), DisplayName = $"Участник {client:D4}",
            IsApproved = true, AccessDecided = true, CreatedAt = now, LastSeenAt = now,
        }));
        manager.Context.SaveChanges();
        var hot = options.Scenario == "hotcell"
            ? repo.AddRecord(new DbRecord { CollectionId = source, Title = "Общая ячейка", Position = 1000 }).Id!.Value
            : 0;
        var initialLog = manager.Context.ChangeLog.LongCount();
        return new(options, connection, source, target, text, date, targetText, targetDate, hot,
            initialLog, databaseName, maxConnections, directory);
    }
}

internal static class MultiUserWorkload
{
    internal static void Execute(MultiUserFixture f, AppDbContext context, ConstructorRepository repo,
        MultiUserClient client, int round)
    {
        var tag = $"client-{client.Id:D4}-round-{round:D4}";
        if (f.Options.Scenario == "hotcell")
        {
            client.Measure("hot-write", () => repo.SetFieldValue(f.HotRecord, f.SourceText, tag));
            return;
        }
        if (f.Options.Scenario == "rollback")
        {
            client.Measure("rollback", () =>
            {
                using var transaction = context.Database.BeginTransaction();
                var transient = repo.AddRecord(new DbRecord { CollectionId = f.SourceCollection, Title = tag, Position = 1000 });
                repo.SetFieldValue(transient.Id!.Value, f.SourceText, tag);
                repo.SetFieldValue(transient.Id.Value, f.SourceDate, "2026-10-25");
                transaction.Rollback();
                context.ChangeTracker.Clear();
            });
            return;
        }

        var record = client.Measure("create", () => repo.AddRecord(new DbRecord
        {
            CollectionId = f.SourceCollection, Title = tag, Position = checked((client.Id + 1) * 1000),
        }));
        var id = record.Id!.Value;
        var expected = new MultiUserExpectedRecord(id, f.SourceCollection, tag, "", "", $"Участник {client.Id:D4}");
        client.Records.Add(id, expected);
        client.Measure("write-text", () => repo.SetFieldValue(id, f.SourceText, tag + "-initial"));
        client.Records[id] = expected = expected with { Text = tag + "-initial" };
        client.Measure("write-date", () => repo.SetFieldValue(id, f.SourceDate, "2026-10-25"));
        client.Records[id] = expected = expected with { Date = "2026-10-25" };
        client.Measure("edit", () => repo.SetFieldValue(id, f.SourceText, tag + "-edited"));
        client.Records[id] = expected = expected with { Text = tag + "-edited" };
        client.Measure("read-page", () =>
        {
            var page = repo.GetRecordsPageAfter(f.SourceCollection, null, null, 20);
            if (page.Count == 0 || page.Count > 20 || page.Any(r => r.CollectionId != f.SourceCollection)
                || page.Select(r => r.Id).Distinct().Count() != page.Count)
                throw new InvalidOperationException("Страница таблицы пуста или содержит неправильные записи");
            var ids = page.Select(r => r.Id!.Value).ToList();
            var values = repo.GetFieldValuesBatch(ids);
            if (values.Keys.Any(key => !ids.Contains(key)))
                throw new InvalidOperationException("Значения страницы содержат постороннюю запись");
            var collections = repo.GetAllCollections();
            if (!collections.Any(c => c.Id == f.SourceCollection) || !collections.Any(c => c.Id == f.TargetCollection))
                throw new InvalidOperationException("В дереве пропала коллекция стенда");
        });
        var duplicate = client.Measure("duplicate", () => repo.DuplicateRecord(id)).Id!.Value;
        client.Records.Add(duplicate, expected with { Id = duplicate });
        var moved = client.Measure("move", () => repo.MoveRecordsToCollectionIsolated(
            f.Connection, f.SourceCollection, [id], f.TargetCollection, false, 901, 901));
        if (moved.Count != 1) throw new InvalidOperationException("Перенос не вернул ровно одну запись");
        client.Records.Remove(id);
        client.Records.Add(moved[0], expected with { Id = moved[0], CollectionId = f.TargetCollection });
        // После изолированного переноса исходный контекст еще помнит оригинал. Именно
        // этот путь удаления соседней копии используется приложением после фоновой работы.
        client.Measure("delete", () => repo.DeleteRecord(duplicate));
        client.Records.Remove(duplicate);
        context.ChangeTracker.Clear();
    }

    internal static List<MultiUserCheck> Verify(MultiUserFixture f, MultiUserClient[] clients)
    {
        using var context = new AppDbContext(f.Connection);
        context.ApplyPerformancePragmas();
        var checks = new List<MultiUserCheck>();
        var records = context.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == f.SourceCollection || r.CollectionId == f.TargetCollection).ToList();
        var values = context.DbFieldValues.AsNoTracking().ToList();
        if (f.Options.Scenario == "lifecycle")
        {
            var expected = clients.SelectMany(c => c.Records.Values).ToDictionary(r => r.Id);
            var actualIds = records.Select(r => r.Id).ToHashSet();
            checks.Add(new("Набор записей точно совпадает с подтвержденными операциями",
                actualIds.SetEquals(expected.Keys), $"факт {records.Count}, подтверждено {expected.Count}; " +
                $"лишние id: {string.Join(',', actualIds.Except(expected.Keys).Take(20))}; " +
                $"пропавшие id: {string.Join(',', expected.Keys.Except(actualIds).Take(20))}"));
            var valid = true;
            foreach (var actual in records)
            {
                if (!expected.TryGetValue(actual.Id, out var wanted)) { valid = false; continue; }
                var text = actual.CollectionId == f.SourceCollection ? f.SourceText : f.TargetText;
                var date = actual.CollectionId == f.SourceCollection ? f.SourceDate : f.TargetDate;
                string Value(int field) => values.FirstOrDefault(v => v.RecordId == actual.Id && v.FieldId == field)?.Value ?? "";
                valid &= actual.Title == wanted.Title && actual.CollectionId == wanted.CollectionId
                         && Value(text) == wanted.Text && Value(date) == wanted.Date
                         && actual.CreatedBy == wanted.Author && actual.UpdatedBy == wanted.Author;
            }
            checks.Add(new("Названия, коллекции, тексты, даты и авторы сохранены без потерь", valid));
            checks.Add(new("Число ячеек совпадает с подтвержденными значениями",
                values.Count == expected.Values.Sum(r => (r.Text.Length > 0 ? 1 : 0) + (r.Date.Length > 0 ? 1 : 0)),
                $"факт {values.Count}, подтверждено " + expected.Values.Sum(r => (r.Text.Length > 0 ? 1 : 0) + (r.Date.Length > 0 ? 1 : 0))));
            if (clients.All(c => c.Completed))
                checks.Add(new("После полных циклов остались только перенесенные записи",
                    records.Count == f.Options.Clients * f.Options.Rounds
                    && records.All(r => r.CollectionId == f.TargetCollection)));
            var dates = context.CalendarIndex.AsNoTracking().ToList();
            checks.Add(new("Календарная проекция совпадает с оставшимися датами",
                dates.Count == expected.Values.Count(r => r.Date.Length > 0)
                && dates.All(d => expected.TryGetValue(d.RecordId, out var r) && r.Date.Length > 0
                    && d.CollectionId == r.CollectionId && d.StartIso.StartsWith(r.Date, StringComparison.Ordinal)
                    && d.FieldId == (r.CollectionId == f.SourceCollection ? f.SourceDate : f.TargetDate)),
                $"факт {dates.Count}, подтверждено дат {expected.Values.Count(r => r.Date.Length > 0)}"));
        }
        else if (f.Options.Scenario == "hotcell")
        {
            var hot = values.Where(v => v.RecordId == f.HotRecord && v.FieldId == f.SourceText).ToList();
            var attempted = clients.SelectMany(c => Enumerable.Range(0, f.Options.Rounds)
                .Select(r => $"client-{c.Id:D4}-round-{r:D4}")).ToHashSet();
            checks.Add(new("Общая ячейка содержит одно целое значение участника",
                records.Count == 1 && hot.Count == 1 && attempted.Contains(hot[0].Value)));
        }
        else
        {
            checks.Add(new("Откат не оставил записей, значений или календарной проекции",
                records.Count == 0 && values.Count == 0 && !context.CalendarIndex.Any()));
            checks.Add(new("Откат не оставил записей журнала",
                context.ChangeLog.LongCount() == f.InitialLogCount));
        }

        checks.AddRange(VerifyIntegrity(f, clients));
        return checks;
    }

    internal static List<MultiUserCheck> VerifyIntegrity(MultiUserFixture f, MultiUserClient[] clients)
    {
        using var context = new AppDbContext(f.Connection);
        context.ApplyPerformancePragmas();
        var checks = new List<MultiUserCheck>();
        checks.Add(new("Нет осиротевших значений",
            !context.DbFieldValues.Any(v => !context.DbRecords.Any(r => r.Id == v.RecordId)
                || !context.DbFields.Any(field => field.Id == v.FieldId))));
        checks.Add(new("Значения принадлежат полям своей коллекции",
            !context.DbFieldValues.Any(v => context.DbRecords.Any(r => r.Id == v.RecordId
                && context.DbFields.Any(field => field.Id == v.FieldId && field.CollectionId != r.CollectionId)))));
        checks.Add(new("Нет осиротевших календарных строк",
            !context.CalendarIndex.Any(d => !context.DbRecords.Any(r => r.Id == d.RecordId)
                || !context.DbFields.Any(field => field.Id == d.FieldId))));
        checks.Add(new("Нет дубликатов значений одной ячейки",
            !context.DbFieldValues.GroupBy(v => new { v.RecordId, v.FieldId }).Any(g => g.Count() > 1)));
        context.Database.OpenConnection();
        long Scalar(string sql)
        {
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        // EXCEPT в обе стороны отличает пропавшую сводку от лишней; нулевые строки
        // допустимы до обслуживания, отрицательный счетчик не допустим никогда.
        checks.Add(new("Сводки дней календаря совпадают с проекцией",
            Scalar(@"SELECT COUNT(*) FROM (
                SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*) FROM calendar_index GROUP BY 1, 2, 3
                EXCEPT SELECT collectionid, startday, fieldid, events FROM calendar_day_counts WHERE events > 0
            ) missing") == 0
            && Scalar(@"SELECT COUNT(*) FROM (
                SELECT collectionid, startday, fieldid, events FROM calendar_day_counts WHERE events > 0
                EXCEPT SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*) FROM calendar_index GROUP BY 1, 2, 3
            ) extra") == 0
            && Scalar("SELECT COUNT(*) FROM calendar_day_counts WHERE events < 0") == 0));
        if (f.Options.Scenario is not ("rollback" or "people"))
        {
            checks.Add(new("Подтвержденные записи отражены в журнале", context.ChangeLog.LongCount() > f.InitialLogCount));
            var actualUsers = context.ChangeLog.Where(l => l.UserKey.StartsWith("client-"))
                .Select(l => l.UserKey).Distinct().ToHashSet();
            checks.Add(new("Журнал различает всех участников с подтвержденными операциями",
                clients.Where(c => c.Samples.Any(s => s.Success))
                    .All(c => actualUsers.Contains(MultiUserFixture.UserKey(c.Id)))));
        }
        if (!context.IsPostgres)
        {
            context.Database.OpenConnection();
            using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            checks.Add(new("SQLite integrity_check = ok", Convert.ToString(command.ExecuteScalar()) == "ok"));
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = command.ExecuteReader();
            checks.Add(new("SQLite foreign_key_check не нашел нарушений", !reader.Read()));
        }
        return checks;
    }
}
