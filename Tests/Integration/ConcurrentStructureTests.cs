using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using Xunit.Abstractions;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Конкурентные изменения структуры и составных команд. Разные контексты имеют общий
/// старт; опасные промежутки дополнительно задаются барьером, без задержек по времени.
/// Допустимый отказ потерянного источника не объявляется выполненным дублированием.
/// </summary>
public sealed class ConcurrentStructureTests(ITestOutputHelper output)
{
    private const int Clients = 8;

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_creation_of_distinct_fields_keeps_every_definition_and_value(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Общая схема").Id!.Value;
        var records = SampleData.AddRecords(f.Repo, collection, Clients).Select(r => r.Id!.Value).ToArray();
        var fieldIds = new int[Clients];

        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var field = SampleData.AddField(repo, collection, $"Свойство {client}", position: (client + 1) * 1000);
            fieldIds[client] = field.Id!.Value;
            repo.SetFieldValue(records[client], field.Id.Value, $"Значение {client}");
        });

        using var check = f.OpenContext();
        var fields = check.DbFields.AsNoTracking().Where(field => field.CollectionId == collection).ToList();
        Assert.Equal(Clients, fields.Count);
        Assert.Equal(Clients, fieldIds.Distinct().Count());
        Assert.Equal(Clients, check.DbFieldValues.Count());
        for (var client = 0; client < Clients; client++)
        {
            var fieldId = fieldIds[client];
            Assert.Contains(fields, field => field.Id == fieldId && field.Name == $"Свойство {client}");
            var value = Assert.Single(check.DbFieldValues.AsNoTracking().Where(v => v.RecordId == records[client]));
            Assert.Equal(fieldId, value.FieldId);
            Assert.Equal($"Значение {client}", value.Value);
        }
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void A_deleted_field_is_not_recreated_by_editors_with_cached_values(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo).Id!.Value;
        var field = SampleData.AddCalendarDateField(f.Repo, collection).Id!.Value;
        var records = SampleData.AddRecords(f.Repo, collection, Clients).Select(r => r.Id!.Value).ToArray();
        foreach (var id in records) f.Repo.SetFieldValue(id, field, "2026-10-26");
        var auditAfterDelete = new Dictionary<int, string>();
        long logAfterDelete = 0;

        var error = Record.Exception(() => MultiUserTestDatabase.RunClients(f, Clients,
            (repo, _, client) => repo.SetFieldValue(records[client], field, "2026-10-27"),
            beforeStart: (_, context, client) =>
            {
                Assert.NotNull(context.DbFields.Find(field));
                Assert.Single(context.DbFieldValues.Where(v => v.RecordId == records[client] && v.FieldId == field));
            },
            whenReady: () =>
            {
                using var context = f.OpenContext();
                new ConstructorRepository(context).DeleteField(field);
                auditAfterDelete = context.DbRecords.AsNoTracking().ToDictionary(record => record.Id, record => record.UpdatedAt);
                logAfterDelete = context.ChangeLog.LongCount();
            }));

        using var check = f.OpenContext();
        Assert.False(check.DbFields.Any(value => value.Id == field));
        Assert.Empty(check.DbFieldValues);
        Assert.Empty(check.CalendarIndex);
        Assert.Equal(Clients, check.DbRecords.Count());
        MultiUserTestDatabase.AssertHealthy(check);
        Assert.Equal(logAfterDelete, check.ChangeLog.LongCount());
        Assert.All(check.DbRecords.AsNoTracking().ToList(), record => Assert.Equal(auditAfterDelete[record.Id], record.UpdatedAt));
        output.WriteLine($"{profile}: удаленное свойство и его значения не восстановлены; ошибка API: {error is not null}.");
        Assert.Null(error);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Creation_using_a_deleted_collection_is_refused_without_orphans(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Удаляемая").Id!.Value;
        SampleData.AddField(f.Repo, collection, "Поле");
        var refused = 0;
        long logAfterDelete = 0;
        MultiUserTestDatabase.RunClients(f, Clients,
            (repo, _, client) =>
            {
                DbRecord? created = null;
                var error = Record.Exception(() => created = repo.AddRecord(new DbRecord
                {
                    CollectionId = collection, Title = $"Участник {client}", Position = (client + 1) * 1000,
                }));
                if (error is not null)
                {
                    Assert.True(IsMissingCollection(error), $"Непредусмотренный отказ: {error}");
                    Interlocked.Increment(ref refused);
                }
                else Assert.Null(created?.Id); // Будущий безопасный no-op тоже не создает запись.
            },
            beforeStart: (_, context, _) => Assert.NotNull(context.DbCollections.Find(collection)),
            whenReady: () =>
            {
                using var context = f.OpenContext();
                new ConstructorRepository(context).DeleteCollection(collection);
                logAfterDelete = context.ChangeLog.LongCount();
            });

        using var check = f.OpenContext();
        Assert.Empty(check.DbCollections);
        Assert.Empty(check.DbRecords);
        Assert.Empty(check.DbFields);
        Assert.Empty(check.DbFieldValues);
        Assert.Equal(logAfterDelete, check.ChangeLog.LongCount());
        MultiUserTestDatabase.AssertHealthy(check);
        output.WriteLine($"{profile}: попыток {Clients}, явно отклонены {refused}; успешное создание не заявляется.");
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Two_moves_of_one_source_cannot_publish_two_logical_copies(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var source = SampleData.AddCollection(f.Repo, "Источник").Id!.Value;
        var target = SampleData.AddCollection(f.Repo, "Назначение").Id!.Value;
        var sourceField = SampleData.AddField(f.Repo, source, "Текст").Id!.Value;
        var targetField = SampleData.AddField(f.Repo, target, "Текст").Id!.Value;
        var id = SampleData.AddRecord(f.Repo, source, "Единственный источник").Id!.Value;
        f.Repo.SetFieldValue(id, sourceField, "Значение до гонки");
        var results = new List<int>[2];

        MultiUserTestDatabase.RunClients(f, 2, (repo, _, client) =>
            results[client] = repo.MoveRecordsToCollectionIsolated(f.Path, source, [id], target,
                createMissingFields: false, sourceDbId: 1, targetDbId: 1));

        using var check = f.OpenContext();
        Assert.False(check.DbRecords.Any(record => record.Id == id));
        var moved = Assert.Single(check.DbRecords.AsNoTracking().Where(record => record.CollectionId == target));
        Assert.Equal("Единственный источник", moved.Title);
        Assert.Equal(moved.Id, Assert.Single(results.SelectMany(ids => ids)));
        var value = Assert.Single(check.DbFieldValues.AsNoTracking());
        Assert.Equal(moved.Id, value.RecordId);
        Assert.Equal(targetField, value.FieldId);
        Assert.Equal("Значение до гонки", value.Value);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_moves_create_missing_target_fields_once_and_keep_every_record(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var source = SampleData.AddCollection(f.Repo, "Источник").Id!.Value;
        var target = SampleData.AddCollection(f.Repo, "Пустая схема").Id!.Value;
        var text = SampleData.AddField(f.Repo, source, "Общий текст").Id!.Value;
        var date = SampleData.AddCalendarDateField(f.Repo, source, "Общая дата").Id!.Value;
        var records = SampleData.AddRecords(f.Repo, source, Clients).Select(r => r.Id!.Value).ToArray();
        for (var client = 0; client < Clients; client++)
        {
            f.Repo.SetFieldValue(records[client], text, $"Значение {client}");
            f.Repo.SetFieldValue(records[client], date, "2026-10-26");
        }

        var error = Record.Exception(() => MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var ids = repo.MoveRecordsToCollectionIsolated(f.Path, source, [records[client]], target,
                createMissingFields: true, sourceDbId: 1, targetDbId: 1);
            Assert.Single(ids);
        }));

        using var check = f.OpenContext();
        MultiUserTestDatabase.AssertHealthy(check);
        var rows = check.DbRecords.AsNoTracking().ToList();
        Assert.Equal(Clients, rows.Count);
        Assert.Equal(Clients, rows.Select(record => record.Title).Distinct().Count());
        var fields = check.DbFields.AsNoTracking().Where(field => field.CollectionId == target).ToList();
        Assert.Single(fields.Where(field => field.Name == "Общий текст"));
        Assert.Single(fields.Where(field => field.Name == "Общая дата"));
        Assert.Equal(2, fields.Count);
        foreach (var row in rows)
        {
            var values = check.DbFieldValues.AsNoTracking().Where(value => value.RecordId == row.Id).ToList();
            Assert.Equal(2, values.Count);
            var ownFields = check.DbFields.AsNoTracking().Where(field => field.CollectionId == row.CollectionId)
                .ToDictionary(field => field.Name, field => field.Id);
            var client = Array.IndexOf(records, row.Id);
            // При отказе часть исходников может остаться: значение должно сохраниться и там.
            if (client < 0) client = int.Parse(row.Title.AsSpan(1)); // SampleData: R0001 ... R0008.
            else client++;
            Assert.Equal($"Значение {client - 1}", values.Single(value => value.FieldId == ownFields["Общий текст"]).Value);
            Assert.Equal("2026-10-26", values.Single(value => value.FieldId == ownFields["Общая дата"]).Value);
        }
        output.WriteLine($"{profile}: данные проверены независимо от ошибки API: {error is not null}.");
        Assert.Null(error);
        Assert.All(rows, row => Assert.Equal(target, row.CollectionId));
    }

    [SkippableTheory]
    [InlineData("sqlite", false)]
    [InlineData("sqlite-shared", false)]
    [InlineData("postgres", false)]
    [InlineData("sqlite", true)]
    [InlineData("sqlite-shared", true)]
    [InlineData("postgres", true)]
    public void A_duplicate_requested_after_neighbour_delete_cannot_use_a_cached_source(string profile, bool isolated)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo).Id!.Value;
        var field = SampleData.AddField(f.Repo, collection, "Текст").Id!.Value;
        var records = SampleData.AddRecords(f.Repo, collection, Clients).Select(r => r.Id!.Value).ToArray();
        foreach (var id in records) f.Repo.SetFieldValue(id, field, $"Значение {id}");
        var refused = 0;

        MultiUserTestDatabase.RunClients(f, Clients,
            (repo, _, client) =>
            {
                var error = Record.Exception(() =>
                {
                    if (isolated) repo.DuplicateRecordIsolated(records[client]);
                    else repo.DuplicateRecord(records[client]);
                });
                if (error is InvalidOperationException && error.Message == $"Record {records[client]} not found")
                    Interlocked.Increment(ref refused);
                else if (error is not null) throw error;
            },
            beforeStart: (repo, _, client) => Assert.NotNull(repo.GetRecord(records[client])),
            whenReady: () =>
            {
                using var context = f.OpenContext();
                new ConstructorRepository(context).DeleteRecordsData(records);
            });

        using var check = f.OpenContext();
        MultiUserTestDatabase.AssertHealthy(check);
        Assert.Empty(check.DbRecords);
        Assert.Empty(check.DbFieldValues);
        Assert.Equal(Clients, refused);
        output.WriteLine($"{profile}, isolated={isolated}: {refused} запросов отклонены из-за отсутствия исходника; копий нет.");
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Bulk_delete_does_not_break_page_value_and_filter_readers(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo).Id!.Value;
        var text = SampleData.AddField(f.Repo, collection, "Группа").Id!.Value;
        var date = SampleData.AddCalendarDateField(f.Repo, collection).Id!.Value;
        var records = SampleData.AddRecords(f.Repo, collection, 48).Select(r => r.Id!.Value).ToArray();
        var known = records.ToHashSet();
        for (var i = 0; i < records.Length; i++)
        {
            f.Repo.SetFieldValue(records[i], text, i % 2 == 0 ? "Первая" : "Вторая");
            f.Repo.SetFieldValue(records[i], date, "2026-10-26");
        }
        using var firstPagesRead = new Barrier(Clients);
        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var first = repo.GetRecordsPageAfter(collection, null, null, take: 12);
            Assert.Equal(12, first.Count);
            Assert.True(firstPagesRead.SignalAndWait(TimeSpan.FromSeconds(20)), "Не прочитаны исходные страницы");
            if (client == 0) repo.DeleteRecordsData(records, batchSize: 4);
            else
            {
                var filters = new Dictionary<string, HashSet<string>> { [text.ToString()] = ["Первая"] };
                for (var iteration = 0; iteration < 8; iteration++)
                {
                    var page = repo.GetRecordsPageAfter(collection, null, null, take: 12);
                    Assert.Equal(page.Count, page.Select(record => record.Id).Distinct().Count());
                    Assert.All(page, record =>
                    {
                        Assert.Equal(collection, record.CollectionId);
                        Assert.Contains(record.Id!.Value, known);
                    });
                    var ids = page.Select(record => record.Id!.Value).ToList();
                    var values = repo.GetFieldValuesBatch(ids);
                    Assert.All(values.Keys, id => Assert.Contains(id, ids));
                    var filtered = repo.GetFilteredRecordIds(collection, filters);
                    Assert.Equal(filtered.Count, filtered.Distinct().Count());
                    Assert.All(filtered, id => Assert.Contains(id, known));
                }
            }
        });

        using var check = f.OpenContext();
        Assert.Empty(check.DbRecords);
        Assert.Empty(check.DbFieldValues);
        Assert.Empty(check.CalendarIndex);
        Assert.Empty(new ConstructorRepository(check).GetFilteredRecordIds(collection,
            new Dictionary<string, HashSet<string>> { [text.ToString()] = ["Первая"] }));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Deleting_a_target_after_link_validation_cannot_leave_a_new_dangling_local_edge(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var targets = SampleData.AddCollection(f.Repo, "Цели").Id!.Value;
        var sources = SampleData.AddCollection(f.Repo, "Источники").Id!.Value;
        var target = SampleData.AddRecord(f.Repo, targets, "Цель").Id!.Value;
        var source = SampleData.AddRecord(f.Repo, sources, "Источник").Id!.Value;
        var relation = SampleData.AddField(f.Repo, sources, "Связь", "relation");
        relation.ConfigJson = new RelationConfig(targets, false, RestrictDelete: true).ToConfigJson();
        f.Repo.UpdateField(relation);
        using var staged = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var expectedConflicts = new ConcurrentQueue<Exception>();

        MultiUserTestDatabase.RunClients(f, 2, (repo, context, client) =>
        {
            if (client == 0)
            {
                try
                {
                    Assert.True(staged.Wait(TimeSpan.FromSeconds(20)), "Новая связь не дошла до сохранения");
                    // Если реализация защищает всю операцию блокировкой, удаление вправе
                    // получить отказ. Не держим писателя на барьере дольше команды удаления.
                    context.Database.OpenConnection();
                    context.Database.SetCommandTimeout(1);
                    if (context.IsPostgres) context.Database.ExecuteSqlRaw("SET lock_timeout = '1000ms'");
                    else ((SqliteConnection)context.Database.GetDbConnection()).DefaultTimeout = 1;
                    var error = Record.Exception(() => repo.DeleteRecord(target));
                    if (error is not null)
                    {
                        Assert.True(IsExpectedRaceConflict(error), $"Непредусмотренный отказ удаления: {error}");
                        expectedConflicts.Enqueue(error);
                    }
                }
                finally { release.Set(); }
            }
            else
            {
                context.ChangeTracker.Tracked += (_, args) =>
                {
                    if (args.Entry is not { State: EntityState.Added, Entity: DbRecordLinkEntity edge }
                        || edge.FieldId != relation.Id || edge.FromRecordId != source) return;
                    staged.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(45)), "Удаление цели не освободило писателя");
                };
                var error = Record.Exception(() => repo.SetLinks(relation.Id!.Value, source, [target]));
                if (error is not null)
                {
                    Assert.True(IsExpectedRaceConflict(error), $"Непредусмотренный отказ записи связи: {error}");
                    expectedConflicts.Enqueue(error);
                }
            }
        });

        using var check = f.OpenContext();
        MultiUserTestDatabase.AssertHealthy(check);
        Assert.True(check.DbRecords.Any(record => record.Id == source));
        var edges = check.DbRecordLinks.AsNoTracking().Where(edge => edge.FieldId == relation.Id).ToList();
        Assert.All(edges, edge =>
        {
            Assert.Equal(source, edge.FromRecordId);
            Assert.Equal(0, edge.TargetDbId);
            Assert.Equal(target, edge.ToRecordId);
        });
        Assert.True(check.DbRecords.Any(record => record.Id == target) || edges.Count == 0,
            "После удаления цели появилась новая локальная ссылка на уже отсутствующую запись");
        Assert.True(edges.Count <= 1);
        output.WriteLine($"{profile}: безопасные отказы пересекающихся операций {expectedConflicts.Count}; это не подтверждение выполнения обеих команд.");
    }

    private static bool IsMissingCollection(Exception error)
    {
        var root = error.GetBaseException();
        return root is PostgresException { SqlState: "23503" }
            or SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 787 }
            || error is InvalidOperationException && (error.Message.Contains("collection", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("коллекц", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExpectedRaceConflict(Exception error)
    {
        var root = error.GetBaseException();
        return error is RelationDeleteRestrictedException
            || root is PostgresException { SqlState: "23503" or "40001" or "40P01" or "55P03" or "57014" }
            or SqliteException { SqliteErrorCode: 5 or 6 }
            or SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 787 }
            or TimeoutException
            || error is InvalidOperationException && (error.Message.Contains("целевая", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("удален", StringComparison.OrdinalIgnoreCase));
    }
}
