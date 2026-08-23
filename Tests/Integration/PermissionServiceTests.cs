using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Права и допуск (docs/architecture/users-permissions.md): первый пользователь —
/// администратор; неодобренному все запрещено; маска коллекции переопределяет базовую;
/// пароль базы (PBKDF2) выдает допуск.
/// </summary>
public sealed class PermissionServiceTests
{
    // Понижает текущего пользователя до заданной роли и перечитывает снапшот прав.
    private static void Demote(TempDatabase db, bool admin, bool approved)
    {
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = admin;
        me.IsApproved = approved;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
    }

    [Fact]
    public void First_user_is_admin_with_full_access()
    {
        using var db = new TempDatabase();
        Assert.True(db.Permissions.IsAdmin);
        Assert.True(db.Permissions.AccessGranted);
        Assert.True(db.Permissions.Can(PermAction.DeleteCollection));
        Assert.True(db.Permissions.Can(PermAction.EditRecord, collectionId: 123));
    }

    [Fact]
    public void Unapproved_non_admin_is_denied()
    {
        using var db = new TempDatabase();
        Demote(db, admin: false, approved: false);

        Assert.False(db.Permissions.AccessGranted);
        Assert.False(db.Permissions.Can(PermAction.ViewCollection));
        Assert.Throws<PermissionDeniedException>(() => db.Permissions.Demand(PermAction.EditRecord));
    }

    [Fact]
    public void Per_collection_mask_overrides_base_mask()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var userId = db.Permissions.CurrentUserId;

        // Пока администратор: пишем маски. База — только просмотр; коллекция — все.
        db.Permissions.SetMask(userId, null, (int)PermAction.ViewCollection);
        db.Permissions.SetMask(userId, col.Id!.Value, (int)PermAction.All);

        // Понижаем до одобренного не-админа: теперь маски действуют.
        Demote(db, admin: false, approved: true);

        Assert.True(db.Permissions.Can(PermAction.ViewCollection));                     // база
        Assert.False(db.Permissions.Can(PermAction.EditRecord));                        // база: запрещено
        Assert.True(db.Permissions.Can(PermAction.EditRecord, col.Id!.Value));          // коллекция: разрешено
    }

    [Fact]
    public void Password_unlock_flow()
    {
        using var db = new TempDatabase();
        Assert.False(db.Permissions.HasPassword);

        db.Permissions.SetPassword("секрет");
        Assert.True(db.Permissions.HasPassword);

        Assert.False(db.Permissions.TryUnlock("неверный"));
        Assert.True(db.Permissions.TryUnlock("секрет"));

        db.Permissions.RemovePassword();
        Assert.False(db.Permissions.HasPassword);
    }

    [Fact]
    public void Retention_policy_is_stored()
    {
        using var db = new TempDatabase();
        db.Permissions.SetChangeLogRetention(enabled: true, months: 6);
        Assert.True(db.Permissions.RetentionEnabled);
        Assert.Equal(6, db.Permissions.RetentionMonths);
    }

    // ── Роли и удаление пользователей (панель администратора) ────────────────

    // «Подселяет» соседа в базу — так выглядит машина второго сотрудника после
    // монтирования той же базы (см. DbAccessLifecycleTests.JoinUser).
    private static int JoinUser(string path, string key, string name, bool approved = false)
    {
        using var neighbour = new AppDbContext(path);
        var user = new DbUserEntity
        {
            UserKey     = key,
            DisplayName = name,
            IsApproved  = approved,
            CreatedAt   = DateTime.UtcNow.ToString("o"),
        };
        neighbour.DbUsers.Add(user);
        neighbour.SaveChanges();
        return user.Id;
    }

    private static DbUserEntity Read(TempDatabase db, int userId)
    {
        using var check = new AppDbContext(db.Path);
        return check.DbUsers.Single(u => u.Id == userId);
    }

    [Fact]
    public void Admin_can_promote_and_demote_another_user()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван");

        db.Permissions.SetAdmin(id, true);
        var promoted = Read(db, id);
        Assert.True(promoted.IsAdmin);
        Assert.True(promoted.IsApproved);   // администратор допущен всегда

        db.Permissions.SetAdmin(id, false);
        Assert.False(Read(db, id).IsAdmin);
    }

    [Fact]
    public void Promotion_keeps_saved_rights()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван", approved: true);
        db.Permissions.SetMask(id, null, (int)PermAction.ViewCollection);

        // Повышение и возврат в «Пользователя» не должны стирать настроенные права:
        // пока человек админ, Can() и так разрешает все.
        db.Permissions.SetAdmin(id, true);
        db.Permissions.SetAdmin(id, false);

        Assert.Equal((int)PermAction.ViewCollection, db.Permissions.GetMask(id, null));
    }

    [Fact]
    public void Own_role_approval_and_removal_are_no_ops()
    {
        using var db = new TempDatabase();
        var myId = db.Permissions.CurrentUserId;

        db.Permissions.SetAdmin(myId, false);     // разжаловать себя нельзя
        db.Permissions.RemoveUser(myId);          // и удалить себя тоже

        var me = Read(db, myId);
        Assert.True(me.IsAdmin);
        Assert.True(me.IsApproved);
    }

    [Fact]
    public void Database_never_loses_its_last_admin()
    {
        using var db = new TempDatabase();
        // Второй админ есть, но он же и единственный после того, как мою строку снес сосед.
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван");
        db.Permissions.SetAdmin(id, true);

        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.Remove(neighbour.DbUsers.Single(u => u.UserKey == CurrentUser.Key));
            neighbour.SaveChanges();
        }

        db.Permissions.SetAdmin(id, false);   // снимать роль с последнего админа нельзя
        Assert.True(Read(db, id).IsAdmin);
    }

    [Fact]
    public void Non_admin_cannot_change_roles_or_remove_users()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван", approved: true);
        Demote(db, admin: false, approved: true);

        Assert.Throws<InvalidOperationException>(() => db.Permissions.SetAdmin(id, true));
        Assert.Throws<InvalidOperationException>(() => db.Permissions.RemoveUser(id));
    }

    [Fact]
    public void Removed_user_loses_rights_and_leaves_journal_entry()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван", approved: true);
        db.Permissions.SetMask(id, null, (int)PermAction.ViewCollection);

        db.Permissions.RemoveUser(id);

        using var check = new AppDbContext(db.Path);
        Assert.DoesNotContain(check.DbUsers.ToList(), u => u.Id == id);
        Assert.DoesNotContain(check.DbPermissions.ToList(), p => p.UserId == id);

        // Событие в журнале — транспорт живого обновления: поллер соседа его увидит.
        var removed = Assert.Single(check.ChangeLog.Where(e => e.Action == "user.remove").ToList());
        Assert.Equal("user", removed.TargetType);
        Assert.Equal("Иванов Иван", removed.TargetName);
    }

    [Fact]
    public void Role_change_is_written_to_change_log_with_detail()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван");

        db.Permissions.SetAdmin(id, true);

        using var check = new AppDbContext(db.Path);
        var role = Assert.Single(check.ChangeLog.Where(e => e.Action == "user.role").ToList());
        Assert.Equal("user", role.TargetType);
        Assert.Equal(CurrentUser.Key, role.UserKey);
        Assert.NotNull(role.Detail);
        Assert.Contains("Администратор", role.Detail);
    }

    [Fact]
    public void Mounting_marks_last_seen_without_journal_rows()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            string first;
            int logRows;
            using (var manager = DatabaseManager.Open(1, "test", path))
            {
                using var ctx = new AppDbContext(path);
                first   = ctx.DbUsers.Single(u => u.UserKey == CurrentUser.Key).LastSeenAt;
                logRows = ctx.ChangeLog.Count();
                Assert.NotEqual("", first);
            }

            using (var again = DatabaseManager.Open(1, "test", path))
            {
                using var ctx = new AppDbContext(path);
                var second = ctx.DbUsers.Single(u => u.UserKey == CurrentUser.Key).LastSeenAt;
                Assert.True(string.CompareOrdinal(second, first) >= 0);
                // Отметка активности журнал не засоряет — иначе каждый запуск программы
                // писал бы строку в «Историю изменений» и будил поллеры соседей.
                Assert.Equal(logRows, ctx.ChangeLog.Count());
            }
        }
        finally { TempDatabase.Cleanup(path); }
    }
}
