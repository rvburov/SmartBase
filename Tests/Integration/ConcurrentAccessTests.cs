using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Одновременная регистрация, отзыв прав и раздельная доставка событий задач.
/// Контексты и кеши прав принадлежат своим участникам; общим остается только файл/сервер.</summary>
public sealed class ConcurrentAccessTests
{
    private const int Clients = 8;
    private static string Key(int client) => $"access-{client:D2}@concurrent-test";
    private static string Name(int client) => $"Участник доступа {client:D2}";

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Simultaneous_registration_of_one_identity_keeps_one_pending_user_and_one_join_event(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Доступ").Id!.Value;
        const string key = "same-identity@concurrent-test";

        MultiUserTestDatabase.RunClients(f, Clients, (_, context, _) =>
        {
            using var actor = CurrentUser.ActAs(key, "Один пользователь");
            var permissions = new PermissionService(context);
            permissions.EnsureCurrentUserRegistered();
            Assert.False(permissions.IsAdmin);
            Assert.False(permissions.AccessGranted);
            var repo = new ConstructorRepository(context) { Permissions = permissions };
            Assert.Throws<PermissionDeniedException>(() =>
                repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Недопущенный" }));
        });

        using var check = f.OpenContext();
        var user = Assert.Single(check.DbUsers.AsNoTracking().Where(u => u.UserKey == key));
        Assert.False(user.IsAdmin);
        Assert.False(user.IsApproved);
        Assert.Equal(1, check.ChangeLog.Count(l => l.UserKey == key && l.Action == "user.join"));
        Assert.Empty(check.DbRecords.Where(r => r.CollectionId == collection));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Distinct_concurrent_users_remain_pending_until_an_administrator_approves_them(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Ожидают допуска").Id!.Value;
        var keys = Enumerable.Range(0, Clients).Select(Key).ToHashSet();
        MultiUserTestDatabase.RunClients(f, Clients, (_, context, client) =>
        {
            using var actor = CurrentUser.ActAs(Key(client), Name(client));
            var permissions = new PermissionService(context);
            permissions.EnsureCurrentUserRegistered();
            Assert.False(permissions.Can(PermAction.CreateRecord, collection));
            var repo = new ConstructorRepository(context) { Permissions = permissions };
            Assert.Throws<PermissionDeniedException>(() =>
                repo.AddRecord(new DbRecord { CollectionId = collection, Title = Name(client) }));
        });

        using var check = f.OpenContext();
        var users = check.DbUsers.AsNoTracking().Where(u => keys.Contains(u.UserKey)).ToList();
        Assert.Equal(Clients, users.Count);
        Assert.All(users, u => { Assert.False(u.IsAdmin); Assert.False(u.IsApproved); });
        Assert.Equal(Clients, check.ChangeLog.Count(l => keys.Contains(l.UserKey) && l.Action == "user.join"));
        Assert.Empty(check.DbRecords.Where(r => r.CollectionId == collection));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    // Права являются кеш-снапшотом приложения. Проверяется именно принятие чужого отзыва
    // после Reload, как после тика поллера; не придумывается мгновенная инвалидизация кешей.
    [SkippableTheory]
    [InlineData("sqlite", "bulk")]
    [InlineData("sqlite-shared", "bulk")]
    [InlineData("postgres", "bulk")]
    [InlineData("sqlite", "binding")]
    [InlineData("sqlite-shared", "binding")]
    [InlineData("postgres", "binding")]
    [InlineData("sqlite", "scenario")]
    [InlineData("sqlite-shared", "scenario")]
    [InlineData("postgres", "scenario")]
    public void Reloaded_remote_revocation_cannot_be_bypassed_by_isolated_writes(string profile, string route)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Запрет записи").Id!.Value;
        var text = SampleData.AddField(f.Repo, collection, "Значение").Id!.Value;
        var records = new int[Clients];
        var users = new int[Clients];
        for (var client = 0; client < Clients; client++)
        {
            var user = new DbUserEntity
            {
                UserKey = Key(client), DisplayName = Name(client), IsApproved = true,
                AccessDecided = true, CreatedAt = DateTime.UtcNow.ToString("o"),
            };
            f.Manager.Context.DbUsers.Add(user);
            f.Manager.Context.SaveChanges();
            users[client] = user.Id;
            records[client] = SampleData.AddRecord(f.Repo, collection, Name(client)).Id!.Value;
            f.Repo.SetFieldValue(records[client], text, "Сохраненное");
        }
        f.Manager.Context.ChangeTracker.Clear();
        var recordLogCount = f.Manager.Context.ChangeLog.Count(l => l.TargetType == "record");
        var permissions = new PermissionService[Clients];
        var binding = new CollectionBinding(1, collection,
            [new BindingColumn("Название", null), new BindingColumn("Значение", text)],
            MatchColumn: "Название", CreateMissing: false);
        var schema = new ImportSchema("Название", ["Значение"], 1, 0);

        MultiUserTestDatabase.RunClients(f, Clients, (_, context, client) =>
        {
            using var actor = CurrentUser.ActAs(Key(client), Name(client));
            permissions[client].Reload(notify: false);
            Assert.False(permissions[client].Can(PermAction.EditRecord, collection));
            var repo = new ConstructorRepository(context) { Permissions = permissions[client] };
            Assert.Throws<PermissionDeniedException>(() =>
            {
                if (route == "bulk")
                    repo.SetCellsBulkIsolatedAsync(collection, [new(records[client], text, "Запрещенное")],
                        null, "Вставка").GetAwaiter().GetResult();
                else if (route == "binding")
                    repo.ApplyRowsIsolated(binding, schema,
                        [new ImportRow(Name(client), ["Запрещенное"])], CancellationToken.None);
                else
                    repo.WriteRecordsIsolated(collection,
                        [new Dictionary<string, string> { ["Название"] = Name(client), ["Значение"] = "Запрещенное" }],
                        CancellationToken.None, matchField: "Название", createMissing: false);
            });
        }, beforeStart: (_, context, client) =>
        {
            using var actor = CurrentUser.ActAs(Key(client), Name(client));
            permissions[client] = new PermissionService(context);
            permissions[client].Reload(notify: false);
            Assert.True(permissions[client].Can(PermAction.EditRecord, collection));
        }, whenReady: () =>
        {
            foreach (var user in users)
                f.Manager.Permissions.SetMask(user, collection, (int)PermAction.ViewCollection);
        });

        using var check = f.OpenContext();
        var recordSet = records.ToHashSet();
        var values = check.DbFieldValues.AsNoTracking()
            .Where(v => recordSet.Contains(v.RecordId) && v.FieldId == text).ToList();
        Assert.Equal(Clients, values.Count);
        Assert.All(values, v => Assert.Equal("Сохраненное", v.Value));
        Assert.Equal(Clients, check.DbRecords.Count(r => r.CollectionId == collection));
        Assert.Equal(recordLogCount, check.ChangeLog.Count(l => l.TargetType == "record"));
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Concurrent_assignment_and_independent_recipient_cursors_neither_mix_nor_replay_task_events(string profile)
    {
        using var f = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(f.Repo, "Задачи получателей").Id!.Value;
        var executor = SampleData.AddField(f.Repo, collection, "Исполнитель", "user").Id!.Value;
        f.Repo.SetUserFieldTaskExec(executor, true);
        var records = new int[Clients];
        for (var client = 0; client < Clients; client++)
        {
            f.Manager.Context.DbUsers.Add(new DbUserEntity
            {
                UserKey = Key(client), DisplayName = Name(client), IsApproved = true,
                CreatedAt = DateTime.UtcNow.ToString("o"),
            });
            f.Manager.Context.SaveChanges();
            records[client] = SampleData.AddRecord(f.Repo, collection, $"Задача {client}").Id!.Value;
        }
        f.Manager.Context.ChangeTracker.Clear();

        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            using var actor = CurrentUser.ActAs($"writer-{client}@concurrent-test", $"Назначивший {client}");
            repo.SetFieldValue(records[client], executor, Name(client));
        });
        using (var check = f.OpenContext())
        {
            Assert.Equal(Clients, check.TaskEvents.Count());
            Assert.Equal(Clients, check.TaskEvents.Select(e => e.RecipientKey).Distinct().Count());
        }

        MultiUserTestDatabase.RunClients(f, Clients, (repo, _, client) =>
        {
            var tail = repo.MaxTaskEventIdIsolated();
            var cursor = 0L;
            var received = new List<TaskEvent>();
            while (cursor < tail)
            {
                var page = repo.ReadTaskEventsAfterIsolated(cursor, Key(client), Name(client), limit: 1);
                Assert.True(page.Cursor > cursor);
                received.AddRange(page.Events);
                cursor = page.Cursor;
            }
            var own = Assert.Single(received);
            Assert.Equal(records[client], own.RecordId);
            Assert.Equal(TaskEventKinds.Assigned, own.Kind);
            Assert.Empty(repo.ReadTaskEventsAfterIsolated(cursor, Key(client), Name(client), limit: 1).Events);
        });
        using var healthy = f.OpenContext();
        MultiUserTestDatabase.AssertHealthy(healthy);
    }
}
