using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Tests.Integration;

public sealed class ConcurrentAutoRelationParentTests
{
    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void A_target_deleted_after_auto_preview_cannot_receive_a_new_dangling_local_edge(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var targets = SampleData.AddCollection(fixture.Repo, "Цели").Id!.Value;
        var sources = SampleData.AddCollection(fixture.Repo, "Источники").Id!.Value;
        var target = SampleData.AddRecord(fixture.Repo, targets, "Совпадающий ключ").Id!.Value;
        var source = SampleData.AddRecord(fixture.Repo, sources, "Совпадающий ключ").Id!.Value;
        var relation = SampleData.AddField(fixture.Repo, sources, "Связь", "relation");
        relation.ConfigJson = new RelationConfig(targets, false).ToConfigJson();
        fixture.Repo.UpdateField(relation);
        var proposed = new DbField
        {
            Id = relation.Id, CollectionId = sources, Name = relation.Name,
            FieldType = "relation", Position = relation.Position,
            ConfigJson = new RelationConfig(targets, false,
                Auto: new(RelationConfig.TitleField, RelationConfig.TitleField)).ToConfigJson(),
        };
        var preview = fixture.Repo.PreviewAutoRelation(proposed);
        Assert.True(preview.CanApply, preview.Message);
        var change = Assert.Single(preview.Changes);
        Assert.Equal(source, change.RecordId);
        Assert.Equal(new RelationTarget(0, target), Assert.Single(change.NewTargets));
        using var metadataStaged = new ManualResetEventSlim(false);
        using var deletionFinished = new ManualResetEventSlim(false);
        var expectedConflicts = new ConcurrentQueue<Exception>();
        var saved = false;

        MultiUserTestDatabase.RunClients(fixture, 2, (repository, context, client) =>
        {
            if (client == 0)
            {
                try
                {
                    Assert.True(metadataStaged.Wait(TimeSpan.FromSeconds(20)), "Сохранение правила не дошло до записи настройки");
                    context.Database.OpenConnection();
                    context.Database.SetCommandTimeout(1);
                    if (context.IsPostgres) context.Database.ExecuteSqlRaw("SET lock_timeout = '1000ms'");
                    else ((SqliteConnection)context.Database.GetDbConnection()).DefaultTimeout = 1;
                    var error = Record.Exception(() => repository.DeleteRecord(target));
                    if (error is not null)
                    {
                        Assert.True(IsExpectedConflict(error), $"Непредусмотренный отказ удаления: {error}");
                        expectedConflicts.Enqueue(error);
                    }
                }
                finally { deletionFinished.Set(); }
                return;
            }

            // SaveAutoRelationField должен использовать этот контекст, а не новый фоновый.
            // На PG это ReadCommitted: чужое удаление после fresh preview не исправит SSI.
            using var transaction = context.Database.BeginTransaction();
            var intercepted = 0;
            context.ChangeTracker.StateChanged += (_, args) =>
            {
                if (args.NewState != EntityState.Modified
                    || args.Entry.Entity is not DbFieldEntity field || field.Id != relation.Id
                    || Interlocked.Exchange(ref intercepted, 1) != 0) return;
                metadataStaged.Set();
                Assert.True(deletionFinished.Wait(TimeSpan.FromSeconds(45)), "Удаление цели не освободило сохранение правила");
            };
            var saveError = Record.Exception(() => repository.SaveAutoRelationField(proposed, preview));
            if (saveError is not null)
            {
                Assert.True(IsExpectedConflict(saveError), $"Непредусмотренный отказ автосвязи: {saveError}");
                expectedConflicts.Enqueue(saveError);
                transaction.Rollback();
            }
            else
            {
                transaction.Commit();
                saved = true;
            }
        });

        Assert.True(metadataStaged.IsSet);
        using var check = fixture.OpenContext();
        MultiUserTestDatabase.AssertHealthy(check);
        Assert.True(check.DbRecords.Any(record => record.Id == source));
        var targetExists = check.DbRecords.Any(record => record.Id == target);
        var edges = check.DbRecordLinks.AsNoTracking().Where(edge => edge.FieldId == relation.Id).ToList();
        Assert.True(targetExists || edges.Count == 0,
            "Автоподбор вставил локальную ссылку на цель, удаленную после проверки совпадения");
        Assert.All(edges, edge =>
        {
            Assert.Equal(source, edge.FromRecordId);
            Assert.Equal(target, edge.ToRecordId);
            Assert.Equal(0, edge.TargetDbId);
        });
        Assert.True(edges.Count <= 1);
        var storedRule = RelationConfig.FromConfig(check.DbFields.AsNoTracking()
            .Single(field => field.Id == relation.Id).ConfigJson)!.Auto;
        if (saved)
        {
            Assert.NotNull(storedRule);
            Assert.Single(edges);
        }
        else Assert.Null(storedRule);
        Assert.NotEmpty(expectedConflicts);
    }

    private static bool IsExpectedConflict(Exception error)
    {
        var root = error.GetBaseException();
        return error is RelationAutoApplyException { Status: RelationAutoStatus.Conflict }
            || root is PostgresException { SqlState: "40001" or "40P01" or "55P03" or "57014" }
            or SqliteException { SqliteErrorCode: 5 or 6 }
            or TimeoutException;
    }
}
