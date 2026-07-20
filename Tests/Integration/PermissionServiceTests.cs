using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Права и допуск (docs/architecture/users-permissions.md): первый пользователь —
/// администратор; неодобренному всё запрещено; маска коллекции переопределяет базовую;
/// пароль базы (PBKDF2) выдаёт допуск.
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

        // Пока администратор: пишем маски. База — только просмотр; коллекция — всё.
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
}
