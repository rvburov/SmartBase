using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.Profile;

/// <summary>
/// Панель «Пользователи» панели администратора: таблица «пользователь × действия» во всю
/// высоту справа (тот же прием, что панель «Детали» в «Настройки → Диагностика»).
/// Строка — пользователь базы, колонки — роль, доступ, действия <see cref="PermAction"/>
/// с галочками, даты и удаление из базы. В шапке панели выбираются база и область действия
/// прав: вся эта база или одна ее коллекция. Правка прав сразу в нескольких базах не делается —
/// права лежат внутри файла каждой базы, и «сразу везде» значило бы «во всех, что открыты
/// прямо сейчас»: обещание, которого экран сдержать не может.
///
/// Вид таблицы — как у таблицы конструктора и журналов: рисованная сетка служебных таблиц
/// (<see cref="ServiceGrid"/>) с иконкой типа, подписью и шевроном фильтра в шапке, теми же
/// линиями и высотой строки RowHeightStandard; первая колонка закреплена. Списки роли и доступа
/// рисуются сеткой как закрытые списки, по клику над ячейкой встает настоящий список; галочки и
/// корзина — рисованные, действие по клику. Фильтр и сортировка считаются в памяти (пользователей
/// десятки), а окно — общее с «Диагностикой» и «Историей изменений» (<see cref="ColumnFilterPanel"/>)
/// и ведет себя как в конструкторе: каскад фильтров, автоотметка найденного, Enter; даты — деревом
/// «год → месяц → день».
///
/// Значения ячеек сетка читает из строки-модели (<see cref="AdminUserRow"/>) при каждом кадре, а
/// правка уходит в базу через ее колбэки; каждая правка пишется сразу, без кнопки «Сохранить»
/// (как было в старом блоке прав).
/// </summary>
public partial class ProfileView
{
    // Ключи служебных колонок (совпадают с ключами фильтров); действия — "act{бит}".
    private const string UsersKeyName     = "name";
    private const string UsersKeyId       = "id";
    private const string UsersKeyRole     = "role";
    private const string UsersKeyApproved = "approved";
    private const string UsersKeyCreated  = "created";
    private const string UsersKeySeen     = "seen";
    private const string UsersKeyRemove   = "remove";

    // Действия в порядке колонок: группа (иконка + первая строка подсказки). Подпись
    // и описание берутся из PermActionLabels — единый источник для панели и документации.
    private static readonly (PermAction Action, string Group, string IconKey)[] UsersActionColumns =
    [
        (PermAction.ViewCollection,      "Коллекции", "IconCollection"),
        (PermAction.EditCollection,      "Коллекции", "IconCollection"),
        (PermAction.DeleteCollection,    "Коллекции", "IconCollection"),
        (PermAction.DuplicateCollection, "Коллекции", "IconCollection"),
        (PermAction.CreateCollection,    "Коллекции", "IconCollection"),
        (PermAction.CreateRecord,        "Записи",    "IconBlocks"),
        (PermAction.EditRecord,          "Записи",    "IconBlocks"),
        (PermAction.DeleteRecord,        "Записи",    "IconBlocks"),
        (PermAction.DuplicateRecord,     "Записи",    "IconBlocks"),
        (PermAction.CreateField,         "Свойства",  "IconSliders"),
        (PermAction.EditField,           "Свойства",  "IconSliders"),
        (PermAction.DeleteField,         "Свойства",  "IconSliders"),
        (PermAction.DuplicateField,      "Свойства",  "IconSliders"),
        (PermAction.IntegrationsSetup,   "Интеграции", "IconWorkflow"),
        (PermAction.IntegrationsOutward, "Интеграции", "IconWorkflow"),
    ];

    // Все прочитанные строки и то, что показано после фильтра и сортировки.
    private readonly List<AdminUserRow> _allUserRows = [];
    private readonly List<AdminUserRow> _userRows    = [];

    private readonly Dictionary<string, HashSet<string>> _userFilters = [];
    private string? _userSortKey;
    private bool    _userSortAsc = true;

    // Колонки строятся один раз при первом открытии.
    private bool _usersHooked;

    // Программное наполнение списков шапки не должно перечитывать таблицу дважды.
    private bool _scopeSyncing;
    private bool _usersDbSyncing;

    // Область живет в Tag пункта списка, а не в индексе: между пунктами стоит разделитель,
    // и нумерация пунктов разошлась бы с нумерацией областей.
    private AdminScope CurrentScope =>
        (ScopeCombo.SelectedItem as ComboBoxItem)?.Tag as AdminScope ?? AdminScope.WholeDatabase;

    // ── Открытие и закрытие ───────────────────────────────────────────────────

    // Список пользователей открывается ВМЕСТО панели администратора и занимает ее место
    // целиком: своя шапка (56) встает на место навбара модуля, тот скрывается — иначе
    // заголовка было бы два. Крестик (и повторный выбор раздела в сайдбаре) возвращает
    // панель администратора.
    private void OnOpenUsersPanel(object? sender, RoutedEventArgs e)
    {
        if (_adminDb is null) return;

        if (!_usersHooked)
        {
            BuildUsersColumns();
            UsersGrid.FrozenColumns    = 1;   // имя видно при прокрутке вбок
            UsersGrid.FilterRequested += (_, ev) => ShowUsersFilterFlyout(ev.Anchor, ev.Key);
            UsersGrid.CheckToggled    += OnUsersCheckToggled;
            UsersGrid.ChoicePicked    += OnUsersChoicePicked;
            UsersGrid.ButtonClicked   += OnUsersButtonClicked;
            _usersHooked = true;
        }

        RebuildUsersDbs();        // база — тем же списком, что в панели администратора
        RebuildScopes();          // область → ReloadUserRows
        AdminPanel.IsVisible  = false;
        HeaderBar.IsVisible   = false;
        UsersPanel.IsVisible  = true;
    }

    // ── База в шапке таблицы ──────────────────────────────────────────────────

    // Тот же список баз, что в панели администратора, и с теми же иконками: базу меняют,
    // не закрывая таблицу. Выбор переезжает и в навбар — вернувшись в панель, человек
    // увидит ту базу, с которой только что работал.
    private void RebuildUsersDbs()
    {
        _usersDbSyncing = true;
        UsersDbCombo.ItemsSource   = _adminDbs.Select(DbChoice).ToList();
        UsersDbCombo.SelectedIndex = _adminDb is null ? -1 : _adminDbs.IndexOf(_adminDb);
        _usersDbSyncing = false;
    }

    private void OnUsersDbChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_usersDbSyncing || !UsersPanelOpen) return;

        var i = UsersDbCombo.SelectedIndex;
        if (i < 0 || i >= _adminDbs.Count) return;

        _adminDb = _adminDbs[i];
        HeaderBar.AdminDbIndex = i;

        // Панель администратора стоит за таблицей и относится к той же базе — ее строки
        // готовим сразу, чтобы крестик возвращал не устаревший экран.
        RefreshPasswordRow();
        RefreshRetentionRow();
        RefreshUsersRow();

        // Отбор и сортировка не переезжают на другую базу: они собраны по ее людям
        // (так же ведет себя «История изменений» при смене базы).
        _userFilters.Clear();
        _userSortKey = null;
        UsersGrid.RefreshHeader();

        RebuildScopes();   // коллекции у каждой базы свои → область и строки пересобираются
    }

    private void OnCloseUsersPanel(object? sender, RoutedEventArgs e) => CloseUsersPanel();

    private void CloseUsersPanel()
    {
        if (!UsersPanel.IsVisible) return;

        UsersPanel.IsVisible = false;
        HeaderBar.IsVisible  = true;
        // Возвращаемся в панель администратора, только если раздел еще открыт: при уходе
        // в «Общие данные» или «Историю» видимость панелей уже расставил ShowCategory.
        AdminPanel.IsVisible = _categoryKey == "admin";

        _userRows.Clear();
        _allUserRows.Clear();
        UsersGrid.Clear();

        // Панель открывается свежей: фильтры и сортировка не переезжают на другую базу
        // (так же ведет себя «История изменений» при смене базы).
        _userFilters.Clear();
        _userSortKey = null;
        UsersGrid.RefreshHeader();
    }

    /// <summary>Панель открыта (ее данные нужно перечитывать после правок).</summary>
    private bool UsersPanelOpen => UsersPanel.IsVisible;

    // ── Область действия прав ─────────────────────────────────────────────────

    // «Вся база данных», линия, коллекции этой базы. Ничего больше: права правятся в одной
    // базе — либо на всю, либо на одну ее коллекцию. Настройки коллекции приоритетнее
    // настроек базы — так же считает PermissionService.GetMask, и линия говорит именно об
    // этом: ниже нее не соседние пункты того же ряда, а то, что базу перекрывает.
    private void RebuildScopes()
    {
        if (_adminDb is null) return;

        // Сохранить выбранную область, если она еще существует (перечитывание после
        // чужих правок не должно сбрасывать администратора на «всю базу»).
        var keep = CurrentScope;

        var items = new List<ComboBoxItem> { ScopeItem("Вся база данных", AdminScope.WholeDatabase) };

        var collections = _adminDb.Constructor.GetAllCollections().Where(c => c.Id.HasValue).ToList();
        if (collections.Count > 0) items.Add(ScopeSeparator());
        foreach (var c in collections)
            items.Add(ScopeItem(c.Name, AdminScope.Of(c.Id!.Value)));

        // Наполнение — программное: перечитываем строки один раз сами, а не событием списка.
        _scopeSyncing = true;
        ScopeCombo.ItemsSource = items;
        var idx = items.FindIndex(i => keep.Equals(i.Tag));
        ScopeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        _scopeSyncing = false;

        ReloadUserRows();
    }

    private static ComboBoxItem ScopeItem(string label, AdminScope scope) =>
        new() { Content = label, Tag = scope };

    // Разделитель: линия вместо пункта. Выбрать его нельзя (выключенные пункты список не
    // выделяет), места он занимает столько же, сколько линия с отступами, — и оглавлений
    // («База», «Коллекции») в списке не нужно: два уровня различает сама линия.
    private static ComboBoxItem ScopeSeparator() => new()
    {
        IsEnabled = false,
        Focusable = false,
        MinHeight = 0,
        Padding   = new Thickness(0),
        Margin    = new Thickness(0, 4),
        // Растяжку задаем в обе стороны: у пункта списка содержимое по умолчанию прижато
        // влево, и линия шириной в свое содержимое (то есть нулевой) не нарисовалась бы.
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = new Border
        {
            Classes             = { "separator", "h" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        },
    };

    private void OnScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_scopeSyncing && UsersPanelOpen) ReloadUserRows();
    }

    // ── Строки ────────────────────────────────────────────────────────────────

    private void ReloadUserRows()
    {
        _allUserRows.Clear();
        _userRows.Clear();
        if (_adminDb is null) { UsersGrid.Clear(); return; }

        LoadRowsOfDatabase();
        ApplyUserFilters();
    }

    private void LoadRowsOfDatabase()
    {
        if (_adminDb is null) return;

        var scope = CurrentScope.Collection;
        var perms = _adminDb.Permissions;

        foreach (var user in perms.GetUsers())
            _allUserRows.Add(new AdminUserRow(user, perms.GetMask(user.Id, scope), scope is null)
            {
                RightChanged    = OnUserRightChanged,
                ApprovalChanged = OnUserApprovalChanged,
                RoleChanged     = OnUserRoleChanged,
            });
    }

    // Фильтр и сортировка считаются здесь же: пользователей десятки, ходить в БД незачем.
    // Порядок по умолчанию — по имени (стабильный): иначе галка доступа переставляла бы
    // строку прямо под курсором.
    private void ApplyUserFilters()
    {
        var rows = ColumnCascade.Pass(_allUserRows, _userFilters, UsersFilterValue);

        var sortKey = _userSortKey ?? UsersKeyName;
        var asc     = _userSortKey is null || _userSortAsc;
        // Даты сравниваются как моменты времени, а не как текст «дд.ММ.гггг»: иначе «Сначала
        // старые» шло бы по числу месяца.
        var comparer = IsUsersDateKey(sortKey) ? StringComparer.Ordinal : StringComparer.CurrentCultureIgnoreCase;
        rows = asc
            ? rows.OrderBy(r => UsersSortValue(sortKey, r), comparer)
            : rows.OrderByDescending(r => UsersSortValue(sortKey, r), comparer);

        _userRows.Clear();
        _userRows.AddRange(rows);
        // Строка сетки читает значения из модели при каждом кадре: правка прав видна перерисовкой.
        UsersGrid.SetRows(_userRows.Select((row, i) => TableRowVm.Static(i + 1, key => UsersCellValue(key, row), row)));
    }

    private static bool IsUsersDateKey(string key) => key is UsersKeyCreated or UsersKeySeen;

    // Значение ячейки по ключу колонки: имя с пометкой «(вы)», ключ, роль, допуск (пусто — ждет
    // допуска, подсказку рисует сетка), галочки прав как «1»/«0», даты текстом.
    private static string UsersCellValue(string key, AdminUserRow row) => key switch
    {
        UsersKeyName     => row.Title,
        UsersKeyId       => row.UserKey,
        UsersKeyRole     => row.Role,
        UsersKeyApproved => row.Access ?? "",
        UsersKeyCreated  => row.CreatedAtText,
        UsersKeySeen     => row.LastSeenText,
        _ when UsersActionBit(key) is int bit => row[bit] ? "1" : "0",
        _                => "",
    };

    private static int? UsersActionBit(string key) =>
        key.StartsWith("act", StringComparison.Ordinal) && int.TryParse(key.AsSpan(3), out var bit) ? bit : null;

    // Сырое значение колонки — общий источник для фильтра и окна значений. Даты — день
    // «yyyy-MM-dd» по местному времени (тот же день, что в ячейке): из него строится дерево
    // «год → месяц → день».
    private static string UsersFilterValue(string key, AdminUserRow row) => key switch
    {
        UsersKeyName     => row.DisplayName,
        UsersKeyId       => row.UserKey,
        UsersKeyRole     => row.Role,
        UsersKeyApproved => row.Access ?? AdminUserRow.AccessPending,
        UsersKeyCreated  => LocalDay(row.CreatedAt),
        UsersKeySeen     => LocalDay(row.LastSeenAt),
        _ when UsersActionBit(key) is int bit => row[bit] ? "Разрешено" : "Запрещено",
        _                => "",
    };

    // Значение для сортировки: у дат — момент времени в UTC (ISO сравнивается как текст по
    // порядку), у остальных колонок — то же, что в фильтре.
    private static string UsersSortValue(string key, AdminUserRow row) => key switch
    {
        UsersKeyCreated => UtcMoment(row.CreatedAt),
        UsersKeySeen    => UtcMoment(row.LastSeenAt),
        _               => UsersFilterValue(key, row),
    };

    private static string LocalDay(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : "";

    private static string UtcMoment(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            : "";

    // ── Действия в ячейках ────────────────────────────────────────────────────

    // Галочка права: доступность проверила сетка (колонка знает, у кого права правятся).
    private void OnUsersCheckToggled(object? sender, ServiceCellEventArgs e)
    {
        if (e.Source is not AdminUserRow row || UsersActionBit(e.Def.Key) is not int bit) return;
        row[bit] = !row[bit];
    }

    // Список роли или доступа выбрал значение — сеттер модели зовет колбэк, тот пишет в базу.
    private void OnUsersChoicePicked(object? sender, ServiceCellEventArgs e)
    {
        if (e.Source is not AdminUserRow row) return;
        if (e.Def.Key == UsersKeyRole)          row.Role   = e.Value;
        else if (e.Def.Key == UsersKeyApproved) row.Access = e.Value;
    }

    private void OnUsersButtonClicked(object? sender, ServiceCellEventArgs e)
    {
        if (e.Source is AdminUserRow row && e.Def.Key == UsersKeyRemove) OnUserRemove(row);
    }

    // Правки пишутся сразу; после них строки перечитываются — роль и допуск меняют
    // доступность соседних ячеек, а сосед мог тем временем поправить свое.
    private void OnUserRightChanged(AdminUserRow row, PermAction action, bool allowed)
    {
        if (_adminDb is null) return;
        var mask = allowed ? row.Mask | (int)action : row.Mask & ~(int)action;
        if (!RunAdminAction(() => _adminDb.Permissions.SetMask(row.Id, CurrentScope.Collection, mask)))
            return;
        row.Mask = mask;
        // Фильтр по этой же колонке мог перестать пропускать строку — пересобираем; без фильтра
        // достаточно перерисовать: значение сетка читает из модели.
        if (_userFilters.Count > 0) ApplyUserFilters();
        else UsersGrid.RefreshRows();
    }

    private void OnUserApprovalChanged(AdminUserRow row, bool approved)
    {
        if (_adminDb is null) return;
        if (RunAdminAction(() => _adminDb.Permissions.SetApproved(row.Id, approved)))
            ReloadUserRows();
    }

    private void OnUserRoleChanged(AdminUserRow row, bool isAdmin)
    {
        if (_adminDb is null) return;
        if (RunAdminAction(() => _adminDb.Permissions.SetAdmin(row.Id, isAdmin)))
            ReloadUserRows();
    }

    private async void OnUserRemove(AdminUserRow row)
    {
        if (_adminDb is null || TopLevel.GetTopLevel(this) is not Window owner) return;

        var confirmed = await new ConfirmDialog(
                $"Убрать пользователя «{row.DisplayName}» из базы данных? Его права будут удалены. " +
                "При следующем открытии базы он снова появится в списке и будет ждать допуска.",
                confirmText: "Убрать")
            .ShowDialog<bool>(owner);
        if (!confirmed) return;

        if (RunAdminAction(() => _adminDb.Permissions.RemoveUser(row.Id)))
            ReloadUserRows();
    }

    // Действие панели: админство могли отозвать, пока панель была открыта (сосед понизил).
    // Тогда сервис бросает исключение — показываем его текст и пересобираем раздел.
    private bool RunAdminAction(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
                _ = ConfirmDialog.Info(ex.Message).ShowDialog(owner);
            CloseUsersPanel();
            RebuildAdminPanel();
            return false;
        }
    }

    /// <summary>Обновить открытую панель после чужих правок (поллер) или собственных.</summary>
    private void RefreshUsersPanel()
    {
        if (!UsersPanelOpen || _adminDb is null) return;

        // Нас самих понизили или удалили с другой машины — управлять доступом больше нечем.
        if (!_adminDb.Permissions.IsAdmin)
        {
            CloseUsersPanel();
            RebuildAdminPanel();
            return;
        }
        RebuildScopes();   // коллекции могли появиться/исчезнуть; выбор сохраняется
    }

    // ── Колонки ───────────────────────────────────────────────────────────────

    // Колонки пиксельной ширины (лишнее уходит в прокрутку вбок); в шапке — иконка, подпись с
    // подсказкой и кнопка фильтра. Что в ячейке, решает тип: текст, список, галочка, кнопка.
    private void BuildUsersColumns()
    {
        var defs = new List<TableColumnDef>
        {
            UsersColumn(UsersKeyName, "Пользователь", "IconUser", 250,
                        tip: BuildUsersTip("Пользователь",
                                           "Имя из профиля человека, открывавшего эту базу данных. " +
                                           "Пометка «(вы)» — ваша строка: свои роль, допуск и права " +
                                           "не меняет никто, кроме другого администратора.")),
            UsersColumn(UsersKeyId, "Идентификатор", "IconFieldText", 200,
                        tip: BuildUsersTip("Идентификатор",
                                           "Ключ «учетная запись@компьютер» — по нему база узнает человека " +
                                           "при следующем открытии, даже если он сменит имя в профиле."),
                        tone: _ => CellTone.Secondary),
            // Роль: «Пользователь» / «Администратор». Свою роль не меняет никто — у своей строки
            // список недоступен (правит только другой администратор). У робота в списке одно
            // значение — «Робот», и оно заблокировано: токен администратором не бывает.
            UsersColumn(UsersKeyRole, "Роль", "IconShieldUser", 170,
                        tip: BuildUsersTip("Роль",
                                           "Администратор управляет доступом и не подчиняется галкам — " +
                                           "ему разрешено все. Чтобы ограничить его, сначала переведите " +
                                           "в «Пользователя». Свою роль изменить нельзя."),
                        kind: "choice", enabled: r => r.RoleEnabled, options: r => r.RoleOptions),
            // Допуск — список, а не галочка: состояний три, и «ждет допуска» надо назвать словом.
            // Пока решения не было, значения нет, а на его месте стоит красная подсказка
            // «Ждет допуска»: выбрать ее обратно нельзя — «еще не решали» не возвращают решением.
            UsersColumn(UsersKeyApproved, "Доступ", "IconLock", 170,
                        tip: BuildUsersTip("Доступ",
                                           "Разрешение работать с базой данных. «Выдан» — человек видит " +
                                           "содержимое, и действуют галки прав. «Отозван» — база показана " +
                                           "замком: содержимое скрыто, права не действуют. «Ждет допуска» — " +
                                           "человек открыл базу, а решения по нему еще не принимали; " +
                                           "вернуть это состояние нельзя. У администратора допуск не снимается."),
                        kind: "choice", enabled: r => r.ApprovalEnabled,
                        options: _ => [AdminUserRow.AccessGranted, AdminUserRow.AccessRevoked],
                        placeholder: AdminUserRow.AccessPending,
                        tone: r => r.IsPending ? CellTone.Red : CellTone.Primary),
        };

        foreach (var (action, group, iconKey) in UsersActionColumns)
        {
            // Часть прав имеет смысл только на уровне базы — у отдельной коллекции
            // переопределять их нечем (PermActionLabels.DatabaseOnly).
            var databaseOnly = PermActionLabels.DatabaseOnly(action);
            // Ширина под самую длинную подпись («Дублирование коллекции») — колонки действий
            // одинаковые, иначе шапка выглядит рваной; лишнее уходит в прокрутку.
            defs.Add(UsersColumn($"act{(int)action}", PermActionLabels.Title(action), iconKey, 210,
                                 tip: BuildUsersActionTip(group, action),
                                 kind: "checkbox",
                                 enabled: databaseOnly ? r => r.DatabaseOnlyEnabled : r => r.RightsEnabled));
        }

        defs.Add(UsersColumn(UsersKeyCreated, "Подключен", "IconClock", 150,
                             tip: BuildUsersTip("Подключен",
                                                "Когда пользователь впервые открыл эту базу данных."),
                             tone: _ => CellTone.Secondary));
        defs.Add(UsersColumn(UsersKeySeen, "Последняя активность", "IconHistory", 190,
                             tip: BuildUsersTip("Последняя активность",
                                                "Когда пользователь в последний раз открывал эту базу данных."),
                             tone: _ => CellTone.Secondary));
        // Колонка действия: фильтровать нечего — шеврона нет. Последняя колонка правой линии
        // заголовка не рисует: ее край и так очерчен краем таблицы (как в диагностике). Корзина
        // красная; свою строку не убирают.
        defs.Add(UsersColumn(UsersKeyRemove, "Удаление пользователя", "IconTrash", 210,
                             tip: BuildUsersTip("Удаление пользователя",
                                                "Убрать пользователя из базы данных вместе с выданными правами. " +
                                                "При следующем открытии базы он вернется в список как ожидающий допуска."),
                             kind: "button", filterable: false, headerLine: false, enabled: r => r.RemoveEnabled,
                             tone: _ => CellTone.Red, iconKey: "IconTrash"));

        UsersGrid.SetColumns(defs);
        // Активная колонка помечается синей воронкой — как в таблицах конструктора и журналов.
        UsersGrid.IsColumnFiltered = key => _userFilters.ContainsKey(key) || _userSortKey == key;
    }

    private static TableColumnDef UsersColumn(string key, string header, string headerIconKey, double width,
                                              Control tip, string kind = "text", bool filterable = true,
                                              bool headerLine = true,
                                              Func<AdminUserRow, bool>? enabled = null,
                                              Func<AdminUserRow, CellTone>? tone = null,
                                              Func<AdminUserRow, IReadOnlyList<string>>? options = null,
                                              string placeholder = "", string? iconKey = null) =>
        new()
        {
            Key           = key,
            Header        = header,
            FieldType     = kind,
            IsStarWidth   = false,
            FixedWidth    = width,
            HeaderIconKey = headerIconKey,
            HeaderTip       = tip,
            Filterable      = filterable,
            HeaderRightLine = headerLine,
            CellEnabled     = enabled is null ? null : vm => vm.Source is AdminUserRow r && enabled(r),
            CellTone      = tone    is null ? null : vm => vm.Source is AdminUserRow r ? tone(r) : CellTone.Primary,
            CellOptions   = options is null ? null : vm => vm.Source is AdminUserRow r ? options(r) : [],
            Placeholder   = placeholder,
            CellIconKey   = iconKey,
        };

    // Подсказка колонки: жирный заголовок + описание с переносом строк. Простая строка
    // здесь не годится — описания в два-три предложения вытянулись бы в одну линию
    // шире экрана.
    private Control BuildUsersTip(string title, string body)
    {
        var head = new TextBlock
        {
            Text         = title,
            FontWeight   = WeightToken("FontWeightBold", FontWeight.SemiBold),
            TextWrapping = TextWrapping.Wrap,
        };
        var text = new TextBlock
        {
            Text         = body,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };
        return new StackPanel { MaxWidth = 320, Children = { head, text } };
    }

    // Для колонки действия заголовок подсказки несет еще и группу: «Записи · Удаление
    // записи» — видно, к чему право применяется, когда иконка группы уже прокручена.
    private Control BuildUsersActionTip(string group, PermAction action) =>
        BuildUsersTip($"{group} · {PermActionLabels.Title(action)}",
                      PermActionLabels.Description(action));

    private static Geometry UsersIcon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    // ── Фильтр и сортировка колонки (общее окно, как в таблице конструктора) ──────

    private void ShowUsersFilterFlyout(Control anchor, string key)
    {
        // Каскад в памяти: значения колонки — по строкам, прошедшим фильтры остальных колонок.
        var counts = ColumnCascade.Counts(_allUserRows, key, _userFilters, UsersFilterValue);
        var date   = IsUsersDateKey(key);

        ColumnFilterPanel.Show(this, anchor, new ColumnFilterSpec
        {
            Key        = key,
            AscLabel   = date ? "Сначала старые" : "От А до Я",
            DescLabel  = date ? "Сначала новые"  : "От Я до А",
            Sort       = _userSortKey == key ? _userSortAsc : null,
            Filter     = _userFilters.GetValueOrDefault(key),
            Display    = raw => raw.Length == 0 ? "(Пусто)" : raw,
            IsDate     = date,
            LoadValues = () => Task.FromResult(counts),
            Apply      = result =>
            {
                result.ApplyTo(_userFilters, ref _userSortKey, ref _userSortAsc);
                UsersGrid.RefreshHeader();
                ApplyUserFilters();
            },
        });
    }
}

/// <summary>Область, в которой правятся права: вся выбранная база или одна ее коллекция.</summary>
/// <param name="Collection">Ключ коллекции; <c>null</c> — вся база.</param>
public sealed record AdminScope(int? Collection)
{
    public static readonly AdminScope WholeDatabase = new((int?)null);

    public static AdminScope Of(int collectionId) => new(collectionId);
}

/// <summary>
/// Строка таблицы пользователей: снапшот <see cref="DbUserEntity"/> + действующая маска прав
/// в выбранной области. Правки уходят в базу через колбэки панели (RightChanged/ApprovalChanged/
/// RoleChanged) прямо из сеттеров — как строка таблицы конструктора (TableRowVm); сетка читает
/// значения при каждом кадре, поэтому уведомлений об изменении не нужно.
///
/// Правила доступности (панель их только отображает, сервис проверяет еще раз):
/// свою строку не правит никто; у администратора все действия разрешены и заблокированы
/// (роль дает полный доступ — чтобы ограничить, другой администратор сначала меняет роль);
/// у ожидающего допуска чекбоксы действий выключены, пока допуск не выдан.
/// </summary>
public sealed class AdminUserRow
{
    /// <summary>Состояния допуска словами — общие для ячейки, отбора и подсказки колонки.</summary>
    public const string AccessGranted = "Выдан";
    public const string AccessRevoked = "Отозван";
    public const string AccessPending = "Ждет допуска";

    private readonly bool _isAdmin;
    private readonly bool _isApproved;
    private readonly bool _decided;
    private readonly bool _scopeIsDatabase;

    public AdminUserRow(DbUserEntity user, int mask, bool scopeIsDatabase)
    {
        Id          = user.Id;
        UserKey     = user.UserKey;
        DisplayName = user.DisplayName;
        IsSelf      = user.UserKey == CurrentUser.Key;
        CreatedAt   = user.CreatedAt;
        LastSeenAt  = user.LastSeenAt;
        CreatedAtText = FormatTs(CreatedAt);
        LastSeenText  = FormatTs(LastSeenAt);
        Mask        = mask;
        _isAdmin    = user.IsAdmin;
        _isApproved = user.IsApproved;
        _decided    = user.AccessDecided;
        IsRobot     = SmartBase.Integrations.AccessToken.IsRobotKey(user.UserKey);
        _scopeIsDatabase = scopeIsDatabase;
    }

    public int    Id          { get; }
    public string UserKey     { get; }
    public string DisplayName { get; }
    public bool   IsSelf      { get; }

    /// <summary>Когда впервые открыл базу и когда в последний раз — ISO, как в db_users. По ним
    /// считаются сортировка по времени и дерево дат в фильтре; ячейка показывает текст ниже.</summary>
    public string CreatedAt   { get; }
    public string LastSeenAt  { get; }

    public string CreatedAtText { get; }
    public string LastSeenText  { get; }

    /// <summary>Колбэки панели: пишут правку в базу (сервис проверяет права еще раз).</summary>
    public Action<AdminUserRow, PermAction, bool>? RightChanged    { get; init; }
    public Action<AdminUserRow, bool>?             ApprovalChanged { get; init; }
    public Action<AdminUserRow, bool>?             RoleChanged     { get; init; }

    public string Title => IsSelf ? $"{DisplayName} (вы)" : DisplayName;

    /// <summary>Ждет допуска: решения по человеку еще не принимали (у администратора допуск
    /// есть всегда). Отозванный допуск — это уже решение, и «ждет» он не считается.</summary>
    public bool IsPending => !_isAdmin && !_isApproved && !_decided;

    /// <summary>Действующая маска прав в выбранной области.</summary>
    public int Mask { get; set; }

    /// <summary>
    /// Галка действия. Ключ — бит <see cref="PermAction"/>.
    /// У администратора разрешено все — галки стоят и не редактируются.
    /// </summary>
    public bool this[int bit]
    {
        get => _isAdmin || (Mask & bit) == bit;
        set
        {
            if (this[bit] == value) return;
            RightChanged?.Invoke(this, (PermAction)bit, value);
        }
    }

    /// <summary>Подпись роли робота: строка токена, а не человека.</summary>
    public const string RoleRobot = "Робот";

    /// <summary>Строка заведена токеном доступа, а не человеком.</summary>
    public bool IsRobot { get; }

    public string Role
    {
        get => IsRobot  ? RoleRobot
             : _isAdmin ? PermissionService.RoleAdmin
                        : PermissionService.RoleUser;
        set
        {
            if (IsRobot) return;   // роль робота не меняют — и служба прав этого не примет
            var isAdmin = value == PermissionService.RoleAdmin;
            if (isAdmin == _isAdmin) return;
            RoleChanged?.Invoke(this, isAdmin);
        }
    }

    /// <summary>
    /// Что предлагает список роли. У робота одно значение — его же: показать ему «Пользователь»
    /// и «Администратор» значило бы предложить то, что служба прав все равно отвергнет.
    /// </summary>
    public IReadOnlyList<string> RoleOptions => IsRobot
        ? [RoleRobot]
        : [PermissionService.RoleUser, PermissionService.RoleAdmin];

    public bool IsApproved => _isAdmin || _isApproved;

    /// <summary>
    /// Значение списка «Доступ»: «Выдан», «Отозван» или <c>null</c> — решения еще не было
    /// («Ждет допуска» стоит подсказкой). Обратно в <c>null</c> список не переводится: отдать
    /// решение назад нельзя, а «ждет» — это именно его отсутствие.
    /// </summary>
    public string? Access
    {
        get => IsPending ? null : IsApproved ? AccessGranted : AccessRevoked;
        set
        {
            if (value is null || value == Access) return;
            ApprovalChanged?.Invoke(this, value == AccessGranted);
        }
    }

    // ── Доступность ячеек ─────────────────────────────────────────────────────

    /// <summary>Роль и удаление правит только ДРУГОЙ администратор; роль робота не правится
    /// вовсе — токен администратором не бывает, а «Пользователь» у него и так.</summary>
    public bool RoleEnabled   => !IsSelf && !IsRobot;
    public bool RemoveEnabled => !IsSelf;

    /// <summary>Допуск администратора не снимается (у него доступ по роли).</summary>
    public bool ApprovalEnabled => !IsSelf && !_isAdmin;

    /// <summary>Действия: администратору можно все (галки заблокированы), ожидающему
    /// допуска настраивать нечего, пока допуск не выдан.</summary>
    public bool RightsEnabled => !IsSelf && !_isAdmin && _isApproved;

    /// <summary>Права уровня базы («Создавать коллекции», оба права интеграций) — только
    /// при выбранной области «вся база».</summary>
    public bool DatabaseOnlyEnabled => RightsEnabled && _scopeIsDatabase;

    private static string FormatTs(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "—";
}
