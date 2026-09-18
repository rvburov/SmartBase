using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Views.Profile;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Строка таблицы пользователей панели администратора: правила доступности ячеек и
/// маршрутизация правок в колбэки панели. Это единственный слой панели, который можно
/// проверить без живого окна, — а именно в нем живут договоренности «свою строку не
/// правит никто», «администратору разрешено все» и «ожидающему допуска настраивать нечего».
/// </summary>
public sealed class AdminUserRowTests
{
    private static DbUserEntity User(string key, bool admin = false, bool approved = true,
                                     bool? decided = null) => new()
    {
        Id          = 7,
        UserKey     = key,
        DisplayName = "Иванов Иван",
        IsAdmin     = admin,
        IsApproved  = approved,
        // По умолчанию решение совпадает с допуском: допущен — значит решали, не допущен —
        // значит ждет. «Отозван» — это approved: false при decided: true.
        AccessDecided = decided ?? approved,
        CreatedAt   = "2026-08-01T09:00:00.0000000Z",
        LastSeenAt  = "2026-08-14T18:20:00.0000000Z",
    };

    // Чужая строка (ключ заведомо не совпадает с текущим пользователем).
    private static AdminUserRow Other(bool admin = false, bool approved = true, bool scopeIsDb = true,
                                      bool? decided = null) =>
        new(User("ivanov@pc2", admin, approved, decided), (int)PermAction.ViewCollection, scopeIsDb);

    private static AdminUserRow Self() =>
        new(User(SmartBase.Utils.CurrentUser.Key), (int)PermAction.All, true);

    [Fact]
    public void Own_row_is_completely_locked()
    {
        var row = Self();

        Assert.True(row.IsSelf);
        Assert.False(row.RoleEnabled);
        Assert.False(row.ApprovalEnabled);
        Assert.False(row.RightsEnabled);
        Assert.False(row.RemoveEnabled);
        Assert.EndsWith("(вы)", row.Title);
    }

    [Fact]
    public void Admin_row_shows_every_action_checked_and_locked()
    {
        var row = Other(admin: true);

        foreach (PermAction a in Enum.GetValues<PermAction>())
        {
            if (a == PermAction.None || a == PermAction.All) continue;
            Assert.True(row[(int)a]);           // роль дает полный доступ
        }
        Assert.False(row.RightsEnabled);        // ограничить можно, только сменив роль
        Assert.True(row.IsApproved);            // допуск у администратора есть всегда
        Assert.False(row.ApprovalEnabled);
        Assert.True(row.RoleEnabled);           // роль меняет другой администратор
        Assert.Equal(PermissionService.RoleAdmin, row.Role);
    }

    [Fact]
    public void Pending_user_cannot_be_given_rights_until_approved()
    {
        var pending  = Other(approved: false);
        var approved = Other();

        Assert.True(pending.IsPending);
        Assert.False(pending.RightsEnabled);
        Assert.True(pending.ApprovalEnabled);

        Assert.False(approved.IsPending);
        Assert.True(approved.RightsEnabled);
    }

    /// <summary>
    /// Права уровня базы — создание коллекций и оба права интеграций: у отдельной коллекции
    /// переопределять их нечем, и галка в области «коллекция» обязана быть недоступной.
    /// </summary>
    [Fact]
    public void Database_level_rights_are_editable_only_for_whole_database()
    {
        Assert.True(Other(scopeIsDb: true).DatabaseOnlyEnabled);
        Assert.False(Other(scopeIsDb: false).DatabaseOnlyEnabled);

        Assert.True(PermActionLabels.DatabaseOnly(PermAction.CreateCollection));
        Assert.True(PermActionLabels.DatabaseOnly(PermAction.IntegrationsSetup));
        Assert.True(PermActionLabels.DatabaseOnly(PermAction.IntegrationsOutward));
        Assert.False(PermActionLabels.DatabaseOnly(PermAction.EditRecord));
    }

    [Fact]
    public void Checkbox_reflects_mask_and_routes_change_to_panel()
    {
        (PermAction Action, bool Allowed)? change = null;
        var row = new AdminUserRow(User("ivanov@pc2"), (int)PermAction.ViewCollection, true)
        {
            RightChanged = (_, action, allowed) => change = (action, allowed),
        };

        Assert.True(row[(int)PermAction.ViewCollection]);
        Assert.False(row[(int)PermAction.DeleteRecord]);

        // Правка не пишется в саму строку: панель отправляет ее в базу и обновляет маску,
        // только если сервис принял изменение.
        row[(int)PermAction.DeleteRecord] = true;
        Assert.Equal((PermAction.DeleteRecord, true), change);
        Assert.False(row[(int)PermAction.DeleteRecord]);

        row.Mask |= (int)PermAction.DeleteRecord;
        Assert.True(row[(int)PermAction.DeleteRecord]);
    }

    [Fact]
    public void Role_and_approval_route_changes_to_panel()
    {
        bool? role = null, approval = null;
        var row = new AdminUserRow(User("ivanov@pc2"), (int)PermAction.All, true)
        {
            RoleChanged     = (_, admin)    => role = admin,
            ApprovalChanged = (_, approved) => approval = approved,
        };

        row.Role = PermissionService.RoleAdmin;
        Assert.True(role);

        row.Access = AdminUserRow.AccessRevoked;
        Assert.False(approval);
    }

    /// <summary>
    /// У допуска три состояния, и «ждет» — не то же самое, что «отозван»: по первому решения
    /// не было, по второму оно было отказом. В базе их различает отдельный признак; строка
    /// обязана называть их разными словами, иначе колонка врет.
    /// </summary>
    [Fact]
    public void Access_names_three_states()
    {
        Assert.Equal(AdminUserRow.AccessGranted, Other().Access);
        Assert.Equal(AdminUserRow.AccessRevoked, Other(approved: false, decided: true).Access);
        Assert.Null(Other(approved: false, decided: false).Access);   // «Ждет допуска» — подсказка списка

        Assert.True(Other(approved: false, decided: false).IsPending);
        Assert.False(Other(approved: false, decided: true).IsPending);
        Assert.False(Other(admin: true).IsPending);                   // у администратора допуск по роли
    }

    /// <summary>
    /// «Ждет допуска» руками не выставляют: решение нельзя отдать назад. Список знает только
    /// два значения, и пустое значение из него не выбирается.
    /// </summary>
    [Fact]
    public void Pending_cannot_be_chosen_back()
    {
        bool? approval = null;
        var row = new AdminUserRow(User("ivanov@pc2", approved: false, decided: false),
                                   (int)PermAction.None, true)
        {
            ApprovalChanged = (_, approved) => approval = approved,
        };

        row.Access = null;
        Assert.Null(approval);

        row.Access = AdminUserRow.AccessGranted;
        Assert.True(approval);
    }

    [Fact]
    public void Dates_are_local_and_missing_activity_shows_dash()
    {
        var row = Other();
        Assert.NotEqual("—", row.CreatedAtText);
        Assert.Contains(".", row.CreatedAtText);          // dd.MM.yyyy HH:mm

        var never = new AdminUserRow(new DbUserEntity { UserKey = "petrov@pc7" }, 0, true);
        Assert.Equal("—", never.LastSeenText);
    }
}
