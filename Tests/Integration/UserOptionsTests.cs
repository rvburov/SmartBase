using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Живой состав пользователей базы (docs/architecture/users-permissions.md): опции типа
/// свойства «пользователь» — ДОПУЩЕННЫЕ пользователи db_users, и они меняются, пока
/// приложение открыто (сосед присоединился, администратор допустил/отозвал, сменилось ФИО).
/// Отдельное подключение здесь играет роль второй машины общей базы: её правки обязаны
/// доезжать без перезапуска — общий контекст живёт весь сеанс, и любой кеш EF на этих
/// строках означал бы «пользователь есть в базе, но его нет в выпадающем списке».
/// </summary>
public sealed class UserOptionsTests
{
    // Регистрация соседа в базе — как PermissionService при монтировании на его машине.
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

    [Fact]
    public void Approved_neighbour_appears_in_options_and_leaves_after_revoke()
    {
        using var db = new TempDatabase();
        Assert.Contains(CurrentUser.DisplayName, db.Constructor.GetUserOptions());

        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван");

        // Ждёт допуска — в списке исполнителей его быть не должно.
        Assert.DoesNotContain("Иванов Иван", db.Constructor.GetUserOptions());

        db.Permissions.SetApproved(id, true);
        Assert.Contains("Иванов Иван", db.Constructor.GetUserOptions());

        // Ушёл из базы (допуск отозван): выбрать нельзя, но старые значения — снапшот строкой.
        db.Permissions.SetApproved(id, false);
        Assert.DoesNotContain("Иванов Иван", db.Constructor.GetUserOptions());
        Assert.Contains("Иванов Иван", db.Constructor.GetUserOptions("Иванов Иван"));
    }

    [Fact]
    public void Options_follow_neighbour_approval_without_restart()
    {
        using var db = new TempDatabase();
        var id = JoinUser(db.Path, "petrov@pc3", "Петров Пётр");

        // Панель администратора уже посмотрела на пользователей — строки в контексте.
        Assert.Contains(db.Permissions.GetUsers(), u => u.UserKey == "petrov@pc3" && !u.IsApproved);

        // Допуск выдан другой машиной (второй администратор общей базы).
        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.First(u => u.UserKey == "petrov@pc3").IsApproved = true;
            neighbour.SaveChanges();
        }

        Assert.Contains("Петров Пётр", db.Constructor.GetUserOptions());
        Assert.True(db.Permissions.GetUsers().First(u => u.UserKey == "petrov@pc3").IsApproved,
                    "панель администратора обязана показывать актуальный допуск");
        Assert.Equal((int)SmartBase.Core.Models.PermAction.All, db.Permissions.GetMask(id, null));
    }

    [Fact]
    public void Renamed_neighbour_shows_new_name_in_options()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "sidorov@pc4", "Сидоров С.", approved: true);
        Assert.Contains("Сидоров С.", db.Constructor.GetUserOptions());

        // Сосед заполнил ФИО в профиле — снапшот имени в db_users обновился с его стороны.
        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.First(u => u.UserKey == "sidorov@pc4").DisplayName = "Сидоров Семён Семёнович";
            neighbour.SaveChanges();
        }

        var options = db.Constructor.GetUserOptions();
        Assert.Contains("Сидоров Семён Семёнович", options);
        Assert.DoesNotContain("Сидоров С.", options);
    }

    [Fact]
    public void Own_approval_by_neighbour_unlocks_database_without_restart()
    {
        using var db = new TempDatabase();

        // Понижаем себя до ожидающего допуска (базу создал кто-то другой).
        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = false;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);
        Assert.False(db.Permissions.AccessGranted);

        // Администратор с другой машины подтвердил доступ; поллер разбудит Reload.
        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.First(u => u.UserKey == CurrentUser.Key).IsApproved = true;
            neighbour.SaveChanges();
        }

        db.Permissions.Reload(notify: false);
        Assert.True(db.Permissions.AccessGranted);
    }

    [Fact]
    public void Profile_rename_does_not_roll_back_neighbour_approval()
    {
        using var db = new TempDatabase();

        var me = db.Context.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin    = false;
        me.IsApproved = false;
        db.Context.SaveChanges();
        db.Permissions.Reload(notify: false);

        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.First(u => u.UserKey == CurrentUser.Key).IsApproved = true;
            neighbour.SaveChanges();
        }

        // Правка ФИО в профиле пишет свою строку db_users — она не должна утащить
        // за собой устаревший снапшот допуска.
        var original = CurrentUser.LastName;
        try
        {
            CurrentUser.LastName = "Тестов";
            db.Permissions.SyncDisplayName();
        }
        finally { CurrentUser.LastName = original; }

        using var check = new AppDbContext(db.Path);
        Assert.True(check.DbUsers.First(u => u.UserKey == CurrentUser.Key).IsApproved);
    }
}
