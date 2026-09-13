using Avalonia.Controls;
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
/// Отсюда два следствия, и оба видны человеку.
///
/// <b>Первое — сводная галка трехсостоянная.</b> Разрешено везде, запрещено везде или
/// «по-разному». Показывать «разрешено», когда в одной базе запрещено, значило бы соврать — и
/// узнали бы об этом по отказу в работе.
///
/// <b>Второе — строка «куда уйдут правки».</b> Она стоит НАД таблицей и до первого щелчка:
/// человек должен знать область действия заранее, а не узнавать ее из отчета о сделанном.
/// Базы, которых правка не коснется, названы по имени и с причиной — «не открыта» и «вы не
/// администратор» лечатся по-разному.
///
/// Роль, допуск и удаление в этой области не правятся, и колонок их тут нет: все три — про
/// конкретный файл базы (кого туда пускать и кто там главный), и выдавать их пачкой одной галкой
/// — не то действие, которое стоит делать мимоходом. Права же наоборот: их и заводят одинаковыми
/// по всем базам, и сверять их порознь — работа на полдня.
/// </summary>
public partial class ProfileView
{
    // Итог последней массовой правки — вторая строка над таблицей. Живет до следующей правки
    // или смены области: это отчет о действии, а не состояние экрана.
    private string _acrossOutcome = "";

    // ── Строки ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Собрать сводные строки: один человек — все базы, где он есть. Люди сводятся по ключу
    /// «учетная запись@компьютер»: он один на машину, а номер пользователя у каждой базы свой.
    /// </summary>
    private void LoadRowsAcrossDatabases()
    {
        var places = new Dictionary<string, List<AdminUserPlace>>(StringComparer.Ordinal);
        var names  = new Dictionary<string, string>(StringComparer.Ordinal);

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

            foreach (var user in users)
            {
                if (!places.TryGetValue(user.UserKey, out var list))
                    places[user.UserKey] = list = [];

                list.Add(new AdminUserPlace(
                    db.DbId, db.Name, user.Id, perms.GetMask(user.Id, null),
                    user.IsAdmin, user.IsApproved, user.CreatedAt, user.LastSeenAt));

                // Имя берем самое свежее: человек мог поправить профиль, а в старую базу
                // новое имя еще не заезжало.
                if (!names.TryGetValue(user.UserKey, out var known)
                    || string.CompareOrdinal(user.LastSeenAt, known) > 0)
                    names[user.UserKey] = user.DisplayName;
            }
        }

        foreach (var (key, list) in places)
            _allUserRows.Add(new AdminUserRow(key, names.GetValueOrDefault(key, key), list)
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

        _acrossOutcome = AcrossReport.Outcome(action, allowed, done, failed, skipped);

        // Перечитываем ВСЕ строки, а не правим маску на месте: соседние базы могли ответить
        // по-разному, и сводная галка должна показать то, что получилось, а не то, что просили.
        ReloadUserRows();
    }

    // ── Строка «куда уйдут правки» ────────────────────────────────────────────

    /// <summary>
    /// Показать или спрятать пояснение над таблицей. В сводной области оно обязательно: без него
    /// галка выглядит как «право во всей программе», а правка кладется только в открытые файлы.
    /// </summary>
    private void ShowScopeNote()
    {
        if (!AcrossDatabases)
        {
            AcrossNote.IsVisible = false;
            _acrossOutcome       = "";
            return;
        }

        AcrossNote.IsVisible  = true;
        AcrossNoteText.Text   = ScopeNoteText();

        AcrossOutcomeText.Text      = _acrossOutcome;
        AcrossOutcomeText.IsVisible = _acrossOutcome.Length > 0;
    }

    /// <summary>Куда уйдут правки и куда не уйдут — собрано из того, что открыто сейчас.</summary>
    private string ScopeNoteText()
    {
        // Открыта, но администратором там не вы: править чужие права нельзя, и молчать об этом
        // тоже — иначе база просто «пропала» из сводного вида.
        var foreign = (_workspace?.Mounted ?? [])
            .Where(db => !db.Permissions.IsAdmin)
            .Select(db => db.Name)
            .ToList();

        // Заведена в реестре, но не открыта: права лежат в ее файле, а файл сейчас не читается.
        var openIds = (_workspace?.Mounted ?? []).Select(d => d.DbId).ToHashSet();

        var closed = (_workspace?.Registry.GetAll() ?? [])
            .Where(entry => !openIds.Contains(entry.Id))
            .Select(entry => entry.Name)
            .ToList();

        return AcrossReport.Where([.. _adminDbs.Select(d => d.Name)], foreign, closed);
    }

    // ── Колонки, которых в сводном виде нет ───────────────────────────────────

    /// <summary>
    /// Спрятать в сводной области колонки, которые правятся только в своей базе: роль, допуск и
    /// удаление. Показывать их выключенными было бы честнее по букве, но хуже по делу: ряд серых
    /// ячеек читается как «сломалось», а не как «здесь этого не делают».
    /// </summary>
    private void ShowScopeColumns()
    {
        ShowScopeNote();

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
