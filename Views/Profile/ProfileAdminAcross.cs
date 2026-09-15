using Avalonia.Controls;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Views.Profile;

/// <summary>Область, в которой правятся права: все открытые базы, вся выбранная база или
/// коллекция в ней.</summary>
/// <param name="AllDatabases">Правки уходят во все открытые базы разом.</param>
/// <param name="Collection">Ключ коллекции; <c>null</c> — вся база.</param>
public sealed record AdminScope(bool AllDatabases, int? Collection)
{
    public static readonly AdminScope Everywhere    = new(true, null);
    public static readonly AdminScope WholeDatabase = new(false, null);

    public static AdminScope Of(int collectionId) => new(false, collectionId);
}

/// <summary>
/// Область «все базы данных» в панели пользователей (docs/ROADMAP-API.md, шаг 15).
///
/// <b>Почему «все базы» — это не одно место, а много.</b> Права лежат внутри файла каждой базы:
/// файл ездит по сетевой папке, копируется на другую машину и несет права с собой. Это свойство,
/// а не недосмотр — общий сервер прав означал бы, что база без сервера не открывается. Поэтому
/// «все базы» здесь честно значит «все базы, открытые прямо сейчас и там, где вы
/// администратор», а не «вообще все, что было заведено»: в закрытый файл правку положить некуда.
///
/// <b>Кто в таблице.</b> Люди базы, выбранной в панели администратора, — с их правами во всех
/// открытых базах, где вы администратор и где они есть (<see cref="AcrossPeople"/>). Человек, ни
/// разу не подключавшийся к выбранной базе, в таблице и в фильтрах не появляется.
///
/// <b>Сводная галка трехсостоянная.</b> Разрешено везде, запрещено везде или «по-разному».
/// Показывать «разрешено», когда в одной базе запрещено, значило бы соврать — и узнали бы об этом
/// по отказу в работе.
///
/// <b>Над таблицей ничего нет</b> — только таблица. Итог массовой правки появляется окном и лишь
/// тогда, когда какая-то база правку не приняла (<see cref="AcrossReport"/>).
///
/// Роль, допуск и удаление в этой области не правятся, и колонок их тут нет: все три — про
/// конкретный файл базы (кого туда пускать и кто там главный), и выдавать их пачкой одной галкой
/// — не то действие, которое стоит делать мимоходом. Права же наоборот: их и заводят одинаковыми
/// по всем базам, и сверять их порознь — работа на полдня.
/// </summary>
public partial class ProfileView
{
    // ── Строки ────────────────────────────────────────────────────────────────

    /// <summary>Собрать сводные строки: люди выбранной базы и все их места среди админских баз.</summary>
    private void LoadRowsAcrossDatabases()
    {
        if (_adminDb is null) return;

        var members = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<AcrossPeople.Entry>();

        foreach (var db in _adminDbs)
        {
            var perms = db.Permissions;

            List<DbUserEntity> users;
            try { users = perms.GetUsers(); }
            catch (Exception error)
            {
                // База открыта, но ее файл сейчас недоступен (сетевую папку отключили). Это не
                // повод не показать остальные: сводный вид тем и нужен, что баз много.
                Serilog.Log.Warning(error, "Пользователи базы {Db:l} не прочитались", db.Name);
                continue;
            }

            if (ReferenceEquals(db, _adminDb))
                members.UnionWith(users.Select(u => u.UserKey));

            foreach (var user in users)
                entries.Add(new AcrossPeople.Entry(user.UserKey, user.DisplayName, user.LastSeenAt,
                    new AdminUserPlace(db.DbId, db.Name, user.Id, perms.GetMask(user.Id, null),
                                       user.IsAdmin, user.IsApproved, user.CreatedAt, user.LastSeenAt)));
        }

        foreach (var person in AcrossPeople.Collect(members, entries))
            _allUserRows.Add(new AdminUserRow(person.UserKey, person.DisplayName, person.Places)
            {
                RightChanged = OnUserRightChanged,
            });
    }

    // ── Правка во все базы разом ──────────────────────────────────────────────

    /// <summary>
    /// Выставить право во всех базах, где этот человек есть.
    ///
    /// Пропускаются базы, где право не действует: у администратора разрешено все по роли, а
    /// ожидающему допуска права выдавать некуда — запись туда ничего бы не изменила, зато в
    /// отчете выглядела бы сделанной работой.
    ///
    /// Ошибка в одной базе не останавливает остальные: администратора могли понизить в одной из
    /// них, пока панель была открыта, и бросать из-за этого правку по остальным значило бы
    /// оставить их в разнобое.
    /// </summary>
    private void ApplyRightEverywhere(AdminUserRow row, PermAction action, bool allowed)
    {
        var bit = (int)action;

        var done    = new List<string>();
        var failed  = new List<string>();
        var skipped = new List<string>();

        foreach (var place in row.Places)
        {
            if (place.IsAdmin)     { skipped.Add($"{place.DbName} — администратор"); continue; }
            if (!place.IsApproved) { skipped.Add($"{place.DbName} — ждет допуска");  continue; }

            var db = _adminDbs.FirstOrDefault(d => d.DbId == place.DbId);
            if (db is null) { skipped.Add($"{place.DbName} — база закрыта"); continue; }

            var mask = allowed ? place.Mask | bit : place.Mask & ~bit;

            if (mask == place.Mask) { done.Add(place.DbName); continue; }

            try
            {
                db.Permissions.SetMask(place.UserId, null, mask);
                done.Add(place.DbName);
            }
            catch (Exception error)
            {
                Serilog.Log.Warning(error, "Право {Action} не записалось в базу {Db:l}",
                                    action, place.DbName);
                failed.Add($"{place.DbName} — {error.Message}");
            }
        }

        // Нормальный исход видно по самой галке; о базе, которая правку не приняла, говорим окном —
        // иначе разнобой прав остался бы незамеченным.
        if (failed.Count > 0 && TopLevel.GetTopLevel(this) is Window owner)
            _ = ConfirmDialog.Info(AcrossReport.Outcome(action, allowed, done, failed, skipped)).ShowDialog(owner);

        // Перечитываем ВСЕ строки, а не правим маску на месте: соседние базы могли ответить
        // по-разному, и сводная галка должна показать то, что получилось, а не то, что просили.
        ReloadUserRows();
    }

    // ── Колонки, которых в сводном виде нет ───────────────────────────────────

    /// <summary>
    /// Спрятать в сводной области колонки, которые правятся только в своей базе: роль, допуск и
    /// удаление. Показывать их выключенными было бы честнее по букве, но хуже по делу: ряд серых
    /// ячеек читается как «сломалось», а не как «здесь этого не делают».
    /// </summary>
    private void ShowScopeColumns()
    {
        if (!_usersHooked) return;

        var perDatabase = !AcrossDatabases;

        foreach (var column in UsersGrid.Columns)
            if (column.Header is Control head && head.Tag is string key && HiddenAcross(key))
                column.IsVisible = perDatabase;

        if (perDatabase) return;

        // Отбор и сортировка по спрятанной колонке остались бы действовать вслепую: строки
        // пропадали бы из таблицы, а причины на экране уже нет.
        foreach (var key in _userFilters.Keys.Where(HiddenAcross).ToList())
            _userFilters.Remove(key);

        if (_userSortKey is { } sort && HiddenAcross(sort)) _userSortKey = null;

        UpdateUsersChevrons();
    }

    private static bool HiddenAcross(string key) =>
        key is UsersKeyRole or UsersKeyApproved or UsersKeyRemove;
}
