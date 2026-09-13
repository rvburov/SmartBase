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
    private static DbUserEntity User(string key, bool admin = false, bool approved = true) => new()
    {
        Id          = 7,
        UserKey     = key,
        DisplayName = "Иванов Иван",
        IsAdmin     = admin,
        IsApproved  = approved,
        CreatedAt   = "2026-08-01T09:00:00.0000000Z",
        LastSeenAt  = "2026-08-14T18:20:00.0000000Z",
    };

    // Чужая строка (ключ заведомо не совпадает с текущим пользователем).
    private static AdminUserRow Other(bool admin = false, bool approved = true, bool scopeIsDb = true) =>
        new(User("ivanov@pc2", admin, approved), (int)PermAction.ViewCollection, scopeIsDb);

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

        row.IsApproved = false;
        Assert.False(approval);
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

    // ── Область «все базы данных» ─────────────────────────────────────────────

    private static AdminUserPlace Place(string db, int mask,
                                        bool admin = false, bool approved = true,
                                        string created = "2026-08-01T09:00:00.0000000Z",
                                        string seen    = "2026-08-14T18:20:00.0000000Z") =>
        new(db.GetHashCode(), db, 7, mask, admin, approved, created, seen);

    private static AdminUserRow Across(params AdminUserPlace[] places) =>
        new("ivanov@pc2", "Иванов Иван", places);

    /// <summary>
    /// Право показывается разрешенным, только когда оно разрешено ВЕЗДЕ: галка «разрешено»
    /// рядом с базой, где запрещено, — неправда, и узнали бы о ней по отказу в работе.
    /// </summary>
    [Fact]
    public void A_right_is_allowed_only_when_every_database_allows_it()
    {
        var row = Across(Place("Продажи", (int)PermAction.ViewCollection | (int)PermAction.EditRecord),
                         Place("Склад",   (int)PermAction.ViewCollection));

        Assert.True(row[(int)PermAction.ViewCollection]);    // разрешено в обеих
        Assert.Null(row[(int)PermAction.EditRecord]);        // по-разному
        Assert.False(row[(int)PermAction.DeleteRecord]);     // не разрешено нигде
    }

    /// <summary>
    /// База, где человек администратор, в расхождение не идет: там разрешено все по роли, и
    /// звать администратора выравнивать то, что не сломано, незачем.
    /// </summary>
    [Fact]
    public void An_admin_database_does_not_make_a_right_look_mixed()
    {
        var row = Across(Place("Продажи", 0, admin: true),
                         Place("Склад",   (int)PermAction.EditRecord));

        Assert.True(row[(int)PermAction.EditRecord]);        // разрешено в обеих: там по роли
        Assert.Null(row[(int)PermAction.DeleteRecord]);      // у админа есть, у второй нет
    }

    /// <summary>Администратор ВЕЗДЕ — та же строка, что и в одной базе: все стоит и не правится.</summary>
    [Fact]
    public void Admin_everywhere_is_locked_like_a_single_database_admin()
    {
        var row = Across(Place("Продажи", 0, admin: true), Place("Склад", 0, admin: true));

        Assert.True(row[(int)PermAction.DeleteCollection]);
        Assert.False(row.RightsEnabled);
    }

    /// <summary>Нигде не допущен — настраивать нечего, пока допуск не выдадут в самой базе.</summary>
    [Fact]
    public void Pending_everywhere_cannot_be_given_rights()
    {
        var row = Across(Place("Продажи", 0, approved: false), Place("Склад", 0, approved: false));

        Assert.True(row.IsPending);
        Assert.False(row.RightsEnabled);
    }

    /// <summary>Допущен хоть где-то — правим: туда правка и уйдет, остальные пропустятся.</summary>
    [Fact]
    public void Approved_in_one_database_is_enough_to_edit()
    {
        var row = Across(Place("Продажи", 0, approved: false), Place("Склад", 0));

        Assert.True(row.RightsEnabled);
        Assert.True(row.DatabaseOnlyEnabled);   // сводный вид — это «вся база» в каждой базе
    }

    /// <summary>
    /// Из состояния «по-разному» щелчок только ставит галку. Снять ее оттуда одним щелчком
    /// нельзя: «выровнять по разрешению» и «выровнять по запрету» должны различаться действием,
    /// а не тем, с какой стороны человек начал.
    /// </summary>
    [Fact]
    public void A_mixed_checkbox_routes_only_a_definite_answer()
    {
        (PermAction Action, bool Allowed)? change = null;

        var row = new AdminUserRow("ivanov@pc2", "Иванов Иван",
            [Place("Продажи", (int)PermAction.EditRecord), Place("Склад", 0)])
        {
            RightChanged = (_, action, allowed) => change = (action, allowed),
        };

        Assert.Null(row[(int)PermAction.EditRecord]);

        row[(int)PermAction.EditRecord] = null;              // «по-разному» руками не выставляют
        Assert.Null(change);

        row[(int)PermAction.EditRecord] = true;
        Assert.Equal((PermAction.EditRecord, true), change);
    }

    /// <summary>
    /// Даты сводятся краями: «подключен» — когда впервые открыл хоть одну базу, «последняя
    /// активность» — когда в последний раз открывал хоть какую-нибудь.
    /// </summary>
    [Fact]
    public void Dates_across_databases_take_the_outer_edges()
    {
        var row = Across(
            Place("Продажи", 0, created: "2026-03-01T09:00:00.0000000Z",
                                seen:    "2026-08-01T09:00:00.0000000Z"),
            Place("Склад",   0, created: "2026-01-15T09:00:00.0000000Z",
                                seen:    "2026-09-10T09:00:00.0000000Z"));

        Assert.StartsWith("15.01.2026", row.CreatedAtText);
        Assert.StartsWith("10.09.2026", row.LastSeenText);
    }

    /// <summary>Где человек есть — знает сама строка: по этому списку панель и разносит правку.</summary>
    [Fact]
    public void A_row_knows_every_database_the_person_is_in()
    {
        var row = Across(Place("Продажи", 0), Place("Склад", 0));

        Assert.Equal(["Продажи", "Склад"], row.Places.Select(p => p.DbName));
        Assert.Empty(Other().Places);   // вне сводного вида список пуст
    }
}
