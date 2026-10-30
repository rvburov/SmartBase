using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Справочник людей обновляется без перезапуска и сохраняет прежние значения ячеек.</summary>
public sealed class UserOptionsTests
{
    private static int JoinUser(string path, string key, string name)
    {
        using var neighbour = new AppDbContext(path);
        var user = new DbUserEntity
        {
            UserKey = key, DisplayName = name, CreatedAt = DateTime.UtcNow.ToString("o"),
            IsApproved = false, IsAdmin = false, AccessDecided = true,
        };
        neighbour.DbUsers.Add(user);
        neighbour.SaveChanges();
        return user.Id;
    }

    [Fact]
    public void A_registered_person_is_available_without_approval_and_historical_values_survive_removal()
    {
        using var db = new TempDatabase();
        Assert.Contains(CurrentUser.DisplayName, db.Constructor.GetUserOptions());
        var id = JoinUser(db.Path, "ivanov@pc2", "Иванов Иван");
        Assert.Contains("Иванов Иван", db.Constructor.GetUserOptions());
        Assert.Contains(db.Users.GetUsers(), u => u.Id == id);
        db.Users.RemoveUser(id);
        Assert.DoesNotContain("Иванов Иван", db.Constructor.GetUserOptions());
        Assert.Contains("Иванов Иван", db.Constructor.GetUserOptions("Иванов Иван"));
    }

    [Fact]
    public void A_person_added_by_another_connection_appears_without_restart()
    {
        using var db = new TempDatabase();
        _ = db.Users.GetUsers();
        var id = JoinUser(db.Path, "petrov@pc3", "Петров Петр");
        Assert.Contains("Петров Петр", db.Constructor.GetUserOptions());
        Assert.Contains(db.Users.GetUsers(), u => u.Id == id && u.DisplayName == "Петров Петр");
    }

    [Fact]
    public void Renamed_neighbour_shows_new_name_in_options()
    {
        using var db = new TempDatabase();
        JoinUser(db.Path, "sidorov@pc4", "Сидоров С.");
        Assert.Contains("Сидоров С.", db.Constructor.GetUserOptions());
        using (var neighbour = new AppDbContext(db.Path))
        {
            neighbour.DbUsers.First(u => u.UserKey == "sidorov@pc4").DisplayName = "Сидоров Семен Семенович";
            neighbour.SaveChanges();
        }
        var options = db.Constructor.GetUserOptions();
        Assert.Contains("Сидоров Семен Семенович", options);
        Assert.DoesNotContain("Сидоров С.", options);
    }

    [Fact]
    public void Profile_rename_does_not_overwrite_legacy_user_metadata()
    {
        using var db = new TempDatabase();
        _ = db.Users.GetUsers();
        using (var neighbour = new AppDbContext(db.Path))
        {
            var user = neighbour.DbUsers.First(u => u.UserKey == CurrentUser.Key);
            user.IsAdmin = false;
            user.IsApproved = false;
            user.AccessDecided = true;
            neighbour.SaveChanges();
        }
        var original = CurrentUser.LastName;
        try
        {
            CurrentUser.LastName = "Тестов";
            db.Users.SyncDisplayName();
        }
        finally { CurrentUser.LastName = original; }
        using var check = new AppDbContext(db.Path);
        var saved = check.DbUsers.First(u => u.UserKey == CurrentUser.Key);
        Assert.False(saved.IsAdmin);
        Assert.False(saved.IsApproved);
        Assert.True(saved.AccessDecided);
    }
}
