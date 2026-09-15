using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.Profile;

/// <summary>
/// Панель «Пользователи» панели администратора: таблица «пользователь × действия» во всю
/// высоту справа (тот же прием, что панель «Детали» в «Настройки → Диагностика»).
/// Строка — пользователь базы, колонки — роль, доступ к базе, действия <see cref="PermAction"/>
/// с чекбоксами, даты и удаление из базы. Область действия прав выбирается в шапке панели: все
/// базы данных, вся выбранная база или коллекция в ней. Сводный вид «все базы» живет в
/// <c>ProfileAdminAcross</c> — там же объяснено, почему «все базы» значит «все открытые».
///
/// Вид таблицы — как у таблицы конструктора и журналов: иконка типа + подпись + шеврон
/// фильтра в шапке, те же линии, высота строки RowHeightStandard. Фильтр и сортировка
/// считаются в памяти (пользователей десятки), а окно — общее с «Диагностикой» и «Историей
/// изменений» (<see cref="ColumnFilterPanel"/>) и ведет себя как в конструкторе: каскад
/// фильтров, автоотметка найденного, Enter; даты — деревом «год → месяц → день».
///
/// Ячейки биндятся к строке-VM (<see cref="AdminUserRow"/>), а не выставляются императивно:
/// DataGrid переиспользует контейнеры ячеек при прокрутке, и обработчик из замыкания шаблона
/// записал бы галку в чужую строку. Каждая правка пишется в базу сразу, без кнопки
/// «Сохранить» (как было в старом блоке прав).
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
    private readonly List<AdminUserRow>        _allUserRows = [];
    private readonly AvaloniaList<AdminUserRow> _userRows   = [];

    private readonly Dictionary<string, HashSet<string>> _userFilters  = [];
    private readonly Dictionary<string, Path>            _userChevrons = [];
    private string? _userSortKey;
    private bool    _userSortAsc = true;

    // Области прав в порядке ScopeCombo.
    private List<AdminScope> _scopes = [];

    // Колонки строятся один раз при первом открытии.
    private bool _usersHooked;

    private AdminScope CurrentScope =>
        ScopeCombo.SelectedIndex >= 0 && ScopeCombo.SelectedIndex < _scopes.Count
            ? _scopes[ScopeCombo.SelectedIndex]
            : AdminScope.WholeDatabase;

    /// <summary>Правим права сразу во всех открытых базах, а не в выбранной.</summary>
    private bool AcrossDatabases => CurrentScope.AllDatabases;

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
            UsersGrid.ItemsSource = _userRows;
            _usersHooked = true;
        }

        RebuildScopes();          // область → ReloadUserRows
        AdminPanel.IsVisible  = false;
        HeaderBar.IsVisible   = false;
        UsersPanel.IsVisible  = true;
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

        // Панель открывается свежей: фильтры и сортировка не переезжают на другую базу
        // (так же ведет себя «История изменений» при смене базы).
        _userFilters.Clear();
        _userSortKey = null;
        UpdateUsersChevrons();
    }

    /// <summary>Панель открыта (ее данные нужно перечитывать после правок).</summary>
    private bool UsersPanelOpen => UsersPanel.IsVisible;

    // ── Область действия прав ─────────────────────────────────────────────────

    // «Все базы данных» + «Вся база данных» + коллекции выбранной базы. Настройки коллекции
    // приоритетнее настроек базы — так же считает PermissionService.GetMask.
    private void RebuildScopes()
    {
        if (_adminDb is null) return;

        // Сохранить выбранную область, если она еще существует (перечитывание после
        // чужих правок не должно сбрасывать администратора на «всю базу»).
        var keep = CurrentScope;

        var collections = _adminDb.Constructor.GetAllCollections();

        _scopes = [];
        var labels = new List<string>();

        // «Все базы данных» — только когда баз больше одной: при единственной базе этот пункт
        // означал бы ровно то же, что следующий, и выбор без разницы читается как поломка.
        if (_adminDbs.Count > 1)
        {
            _scopes.Add(AdminScope.Everywhere);
            labels.Add("Все базы данных");
        }

        _scopes.Add(AdminScope.WholeDatabase);
        labels.Add("Вся база данных");

        foreach (var c in collections.Where(c => c.Id.HasValue))
        {
            _scopes.Add(AdminScope.Of(c.Id!.Value));
            labels.Add(c.Name);
        }

        ScopeCombo.ItemsSource = labels;
        var idx = _scopes.IndexOf(keep);
        ScopeCombo.SelectedIndex = idx >= 0 ? idx : 0;   // SelectionChanged → ReloadUserRows
        ReloadUserRows();
    }

    private void OnScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UsersPanelOpen) ReloadUserRows();
    }

    // ── Строки ────────────────────────────────────────────────────────────────

    private void ReloadUserRows()
    {
        _allUserRows.Clear();
        _userRows.Clear();
        if (_adminDb is null) return;

        ShowScopeColumns();

        if (AcrossDatabases) LoadRowsAcrossDatabases();
        else                 LoadRowsOfDatabase();

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
    }

    private static bool IsUsersDateKey(string key) => key is UsersKeyCreated or UsersKeySeen;

    // Сырое значение колонки — общий источник для фильтра и окна значений. Даты — день
    // «yyyy-MM-dd» по местному времени (тот же день, что в ячейке): из него строится дерево
    // «год → месяц → день».
    private static string UsersFilterValue(string key, AdminUserRow row) => key switch
    {
        UsersKeyName     => row.DisplayName,
        UsersKeyId       => row.UserKey,
        UsersKeyRole     => row.Role,
        UsersKeyApproved => row.IsApproved ? "Есть" : "Нет",
        UsersKeyCreated  => LocalDay(row.CreatedAt),
        UsersKeySeen     => LocalDay(row.LastSeenAt),
        _ when key.StartsWith("act", StringComparison.Ordinal) &&
               int.TryParse(key.AsSpan(3), out var bit)
                         => row[bit] switch
                            {
                                true  => "Разрешено",
                                false => "Запрещено",
                                null  => "По-разному",
                            },
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

    // Правки пишутся сразу; после них строки перечитываются — роль и допуск меняют
    // доступность соседних ячеек, а сосед мог тем временем поправить свое.
    private void OnUserRightChanged(AdminUserRow row, PermAction action, bool allowed)
    {
        if (AcrossDatabases) { ApplyRightEverywhere(row, action, allowed); return; }

        if (_adminDb is null) return;
        var mask = allowed ? row.Mask | (int)action : row.Mask & ~(int)action;
        if (!RunAdminAction(() => _adminDb.Permissions.SetMask(row.Id, CurrentScope.Collection, mask)))
            return;
        row.Mask = mask;
        // Фильтр по этой же колонке мог перестать пропускать строку — пересобираем.
        if (_userFilters.Count > 0) ApplyUserFilters();
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

    private void BuildUsersColumns()
    {
        _userChevrons.Clear();

        AddUsersColumn(UsersKeyName, "Пользователь", "IconUser", 220, BuildUserNameCell(),
                       tip: BuildUsersTip("Пользователь",
                                          "Имя из профиля человека, открывавшего эту базу данных. " +
                                          "Пометка «(вы)» — ваша строка: свои роль, допуск и права " +
                                          "не меняет никто, кроме другого администратора."));
        AddUsersColumn(UsersKeyId, "Идентификатор", "IconFieldText", 200,
                       BuildUsersTextCell(nameof(AdminUserRow.UserKey), secondary: true),
                       tip: BuildUsersTip("Идентификатор",
                                          "Ключ «учетная запись@компьютер» — по нему база узнает человека " +
                                          "при следующем открытии, даже если он сменит имя в профиле."));
        AddUsersColumn(UsersKeyRole, "Роль", "IconShieldUser", 170, BuildUserRoleCell(),
                       tip: BuildUsersTip("Роль",
                                          "Администратор управляет доступом и не подчиняется галкам — " +
                                          "ему разрешено все. Чтобы ограничить его, сначала переведите " +
                                          "в «Пользователя». Свою роль изменить нельзя."));
        AddUsersColumn(UsersKeyApproved, "Доступ к базе данных", "IconLock", 210,
                       BuildUsersCheckCell(nameof(AdminUserRow.IsApproved),
                                           nameof(AdminUserRow.ApprovalEnabled)),
                       tip: BuildUsersTip("Доступ к базе данных",
                                          "Разрешение работать с базой данных. Без допуска она показана " +
                                          "замком: содержимое скрыто, права не действуют. " +
                                          "У администратора допуск снять нельзя."));

        foreach (var (action, group, iconKey) in UsersActionColumns)
        {
            // Часть прав имеет смысл только на уровне базы — у отдельной коллекции
            // переопределять их нечем (PermActionLabels.DatabaseOnly).
            var enabledPath = PermActionLabels.DatabaseOnly(action)
                ? nameof(AdminUserRow.DatabaseOnlyEnabled)
                : nameof(AdminUserRow.RightsEnabled);
            // Ширина под самую длинную подпись («Дублирование коллекции») — колонки действий
            // одинаковые, иначе шапка выглядит рваной; лишнее уходит в прокрутку.
            AddUsersColumn($"act{(int)action}", PermActionLabels.Title(action), iconKey, 210,
                           BuildUsersCheckCell($"[{(int)action}]", enabledPath),
                           tip: BuildUsersActionTip(group, action));
        }

        AddUsersColumn(UsersKeyCreated, "Подключен", "IconClock", 150,
                       BuildUsersTextCell(nameof(AdminUserRow.CreatedAtText), secondary: true),
                       tip: BuildUsersTip("Подключен",
                                          "Когда пользователь впервые открыл эту базу данных."));
        AddUsersColumn(UsersKeySeen, "Последняя активность", "IconHistory", 190,
                       BuildUsersTextCell(nameof(AdminUserRow.LastSeenText), secondary: true),
                       tip: BuildUsersTip("Последняя активность",
                                          "Когда пользователь в последний раз открывал эту базу данных."));
        // Колонка действия: фильтровать нечего — шеврона нет. Последняя колонка правой линии
        // заголовка не рисует: ее край и так очерчен краем таблицы (как в диагностике).
        AddUsersColumn(UsersKeyRemove, "Удаление пользователя", "IconTrash", 210, BuildUserRemoveCell(),
                       tip: BuildUsersTip("Удаление пользователя",
                                          "Убрать пользователя из базы данных вместе с выданными правами. " +
                                          "При следующем открытии базы он вернется в список как ожидающий допуска."),
                       filterable: false, addRightLine: false);
    }

    private void AddUsersColumn(string key, string header, string iconKey, double width,
                                IDataTemplate cell, object? tip = null,
                                bool filterable = true, bool addRightLine = true) =>
        UsersGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header       = BuildUsersHeader(key, header, iconKey, tip, filterable, addRightLine),
            Width        = new DataGridLength(width, DataGridLengthUnitType.Pixel),
            CellTemplate = cell,
        });

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

    // Шапка колонки 1-в-1 как в таблице конструктора и журналов: [иконка типа][подпись]
    // [шеврон фильтра] + правая линия-разделитель.
    private Control BuildUsersHeader(string key, string header, string iconKey, object? tip,
                                     bool filterable, bool addRightLine)
    {
        var text = new TextBlock
        {
            Text              = header,
            FontSize          = FontSizeToken("FontSizeBase", 13),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = Brush("BrushSecondaryText", "#8E8E93"),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var icon = BuildUsersHeaderIcon(iconKey);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (filterable)
        {
            var chevron = BuildUsersChevron(key);
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        var border = new Border
        {
            Background          = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Padding             = new Thickness(6, 0),
            Child               = grid,
        };
        border.Classes.Add("colHeader");
        ToolTip.SetTip(border, tip ?? header);

        var host = new Grid
        {
            Background          = Brushes.Transparent,
            ColumnDefinitions   = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            // Ключ колонки — по нему панель находит ее, когда прячет в области «все базы».
            Tag                 = key,
        };
        Grid.SetColumn(border, 0);
        host.Children.Add(border);

        if (addRightLine)
        {
            var line = new Rectangle
            {
                Fill              = Brush("BrushSeparator", "#E5E5EA"),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            line.Bind(Rectangle.WidthProperty,              line.GetResourceObservable("SeparatorThickness"));
            line.Bind(Layoutable.UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
            RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
            Grid.SetColumn(line, 1);
            host.Children.Add(line);
        }
        return host;
    }

    // Иконка типа колонки — как в таблице конструктора (Viewbox IconSizeSmall, отступ 6px).
    private Control BuildUsersHeaderIcon(string iconKey) => new Viewbox
    {
        Width             = FontSizeToken("IconSizeSmall", 16),
        Height            = FontSizeToken("IconSizeSmall", 16),
        VerticalAlignment = VerticalAlignment.Center,
        Margin            = new Thickness(0, 0, 6, 0),
        Child = new Canvas
        {
            Width  = 24,
            Height = 24,
            Children =
            {
                new Path
                {
                    Data            = UsersIcon(iconKey),
                    Stroke          = Brush("BrushSecondaryText", "#8E8E93"),
                    StrokeThickness = FontSizeToken("IconStrokeSmall", 1.2),
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    private Control BuildUsersChevron(string key)
    {
        var path = new Path
        {
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        _userChevrons[key] = path;
        ApplyUsersChevronState(key, path);

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);

        var btn = new Button
        {
            Width             = 20,
            Height            = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Content           = new Viewbox
            {
                Width  = FontSizeToken("IconSizeSmall", 16),
                Height = FontSizeToken("IconSizeSmall", 16),
                Child  = canvas,
            },
        };
        btn.Classes.Add("icon");
        btn.Classes.Add("filterChevron");
        ToolTip.SetTip(btn, "Фильтр и сортировка");
        btn.Click += (_, _) => ShowUsersFilterFlyout(btn, key);
        return btn;
    }

    // Активная колонка помечается синей воронкой — как в таблицах конструктора и журналов.
    private void ApplyUsersChevronState(string key, Path path)
    {
        var active  = _userFilters.ContainsKey(key) || _userSortKey == key;
        path.Data   = UsersIcon(active ? "IconFilter" : "IconChevronDown");
        path.Stroke = active
            ? Brush("BrushAccent", "#007AFF")
            : Brush("BrushSecondaryText", "#8E8E93");
    }

    private void UpdateUsersChevrons()
    {
        foreach (var (key, path) in _userChevrons)
            ApplyUsersChevronState(key, path);
    }

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
                UpdateUsersChevrons();
                ApplyUserFilters();
            },
        });
    }

    // ── Ячейки (только биндинги: контейнеры переиспользуются) ──────────────────

    private IDataTemplate BuildUsersTextCell(string path, bool secondary = false) =>
        new FuncDataTemplate<AdminUserRow>((_, _) =>
        {
            var text = new TextBlock
            {
                Margin            = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            if (secondary) text.Foreground = Brush("BrushSecondaryText", "#8E8E93");
            text.Bind(TextBlock.TextProperty, new Binding(path));
            return text;
        });

    // Имя пользователя + пометка «ждет допуска» (у допущенных и админов ее нет).
    private IDataTemplate BuildUserNameCell() =>
        new FuncDataTemplate<AdminUserRow>((_, _) =>
        {
            var name = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(AdminUserRow.Title)));

            var pending = new TextBlock
            {
                Text              = "ждет допуска",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize          = FontSizeToken("FontSizeSmall", 11),
                Foreground        = Brush("BrushRed", "#FF3B30"),
            };
            pending.Bind(Visual.IsVisibleProperty, new Binding(nameof(AdminUserRow.IsPending)));

            var row = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                Spacing           = 6,
                Margin            = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(name);
            row.Children.Add(pending);
            return row;
        });

    private IDataTemplate BuildUsersCheckCell(string valuePath, string enabledPath) =>
        new FuncDataTemplate<AdminUserRow>((_, _) =>
        {
            var box = new CheckBox
            {
                Padding             = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            box.Bind(ToggleButton.IsCheckedProperty,
                     new Binding(valuePath) { Mode = BindingMode.TwoWay });
            box.Bind(InputElement.IsEnabledProperty, new Binding(enabledPath));
            return box;
        });

    // Роль: «Пользователь» / «Администратор». Свою роль не меняет никто — у своей
    // строки список выключен (правит только другой администратор).
    private IDataTemplate BuildUserRoleCell() =>
        new FuncDataTemplate<AdminUserRow>((_, _) =>
        {
            var combo = new ComboBox
            {
                ItemsSource         = new[] { PermissionService.RoleUser, PermissionService.RoleAdmin },
                Margin              = new Thickness(6, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Center,
                Height              = 26,
                MinHeight           = 26,
            };
            combo.Bind(SelectingItemsControl.SelectedItemProperty,
                       new Binding(nameof(AdminUserRow.Role)) { Mode = BindingMode.TwoWay });
            combo.Bind(InputElement.IsEnabledProperty, new Binding(nameof(AdminUserRow.RoleEnabled)));
            return combo;
        });

    private IDataTemplate BuildUserRemoveCell() =>
        new FuncDataTemplate<AdminUserRow>((_, _) =>
        {
            var button = new Button
            {
                Width               = 28,
                Height              = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Content = new Viewbox
                {
                    Width  = FontSizeToken("IconSizeSmall", 16),
                    Height = FontSizeToken("IconSizeSmall", 16),
                    Child  = new Canvas
                    {
                        Width  = 24,
                        Height = 24,
                        Children =
                        {
                            new Path
                            {
                                Data            = UsersIcon("IconTrash"),
                                Stroke          = Brush("BrushRed", "#FF3B30"),
                                StrokeThickness = FontSizeToken("IconStrokeSmall", 1.2),
                                StrokeLineCap   = PenLineCap.Round,
                                StrokeJoin      = PenLineJoin.Round,
                            },
                        },
                    },
                },
            };
            button.Classes.Add("icon");
            ToolTip.SetTip(button, "Убрать пользователя из базы данных");
            button.Bind(InputElement.IsEnabledProperty, new Binding(nameof(AdminUserRow.RemoveEnabled)));
            // DataContext ячейки — строка таблицы; берем его в момент клика, а не из
            // замыкания шаблона: контейнер ячейки переиспользуется при прокрутке.
            button.Click += (s, _) =>
            {
                if ((s as Control)?.DataContext is AdminUserRow row) OnUserRemove(row);
            };
            return button;
        });
}

/// <summary>
/// Один человек в одной базе данных: где он есть, под каким ключом и с какими правами. Нужен
/// сводному виду «все базы данных»: права лежат внутри файла каждой базы, и строка сводного вида
/// собирается из нескольких таких мест.
/// </summary>
public sealed record AdminUserPlace(int    DbId,
                                    string DbName,
                                    int    UserId,
                                    int    Mask,
                                    bool   IsAdmin,
                                    bool   IsApproved,
                                    string CreatedAt,
                                    string LastSeenAt);

/// <summary>
/// Строка таблицы пользователей: снапшот <see cref="DbUserEntity"/> + действующая маска прав
/// в выбранной области. Правки уходят в базу через колбэки панели (RightChanged/ApprovalChanged/
/// RoleChanged) прямо из сеттеров — как строка таблицы конструктора (TableRowVm).
///
/// Правила доступности (панель их только отображает, сервис проверяет еще раз):
/// свою строку не правит никто; у администратора все действия разрешены и заблокированы
/// (роль дает полный доступ — чтобы ограничить, другой администратор сначала меняет роль);
/// у ожидающего допуска чекбоксы действий выключены, пока допуск не выдан.
///
/// <b>Сводный вид «все базы данных».</b> Второй конструктор собирает строку из нескольких баз
/// сразу: человек тот же (ключ «учетная запись@компьютер» один на машину), а прав у него столько
/// же, сколько баз он открывал. Галка тогда трехсостоянная: разрешено везде, запрещено везде или
/// «по-разному» — и последнее состояние не выдумано ради красоты, а единственно честное, когда
/// базы разошлись. Щелчок по ней применяет решение во ВСЕ базы сразу.
/// </summary>
public sealed class AdminUserRow : INotifyPropertyChanged
{
    private int  _mask;
    private readonly bool _isAdmin;
    private readonly bool _isApproved;
    private readonly bool _scopeIsDatabase;

    // Биты, по которым базы разошлись; всегда 0 вне сводного вида.
    private int _mixed;

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
        _mask       = mask;
        _isAdmin    = user.IsAdmin;
        _isApproved = user.IsApproved;
        _scopeIsDatabase = scopeIsDatabase;
    }

    /// <summary>
    /// Сводная строка: один человек и его права во всех открытых базах разом.
    ///
    /// Право считается разрешенным, только если оно разрешено ВЕЗДЕ. Иначе бы галка обещала
    /// больше, чем есть: «разрешено» рядом с базой, где запрещено, — это неправда, и узнают о
    /// ней по отказу. Базы, где человек администратор, в расхождение не идут: там разрешено все
    /// по роли, и считать это «расхождением прав» значило бы звать администратора чинить то, что
    /// не сломано.
    /// </summary>
    public AdminUserRow(string userKey, string displayName, IReadOnlyList<AdminUserPlace> places)
    {
        Id          = 0;                   // ключа строки во «всех базах» нет: их столько же, сколько баз
        UserKey     = userKey;
        DisplayName = displayName;
        IsSelf      = userKey == CurrentUser.Key;
        Places      = places;

        // Даты сводим краями: «подключен» — когда впервые открыл хоть одну базу, «последняя
        // активность» — когда в последний раз открывал хоть какую-нибудь.
        CreatedAt  = places.Select(p => p.CreatedAt).Where(s => s.Length > 0)
                           .OrderBy(s => s, StringComparer.Ordinal).FirstOrDefault() ?? "";
        LastSeenAt = places.Select(p => p.LastSeenAt).Where(s => s.Length > 0)
                           .OrderByDescending(s => s, StringComparer.Ordinal).FirstOrDefault() ?? "";
        CreatedAtText = FormatTs(CreatedAt);
        LastSeenText  = FormatTs(LastSeenAt);

        // Маска, действующая в каждой базе: у администратора — все биты, у остальных своя.
        var masks = places.Select(p => p.IsAdmin ? AllBits : p.Mask).ToList();

        _mask  = masks.Aggregate(AllBits, (all, m) => all & m);          // разрешено везде
        _mixed = masks.Aggregate(0,       (any, m) => any | m) & ~_mask; // где-то да, где-то нет

        _isAdmin    = places.All(p => p.IsAdmin);
        _isApproved = places.Any(p => p.IsApproved);
        _scopeIsDatabase = true;   // сводный вид — это всегда «вся база», в каждой из баз
    }

    /// <summary>Все биты <see cref="PermAction"/> разом — «разрешено все».</summary>
    private static readonly int AllBits =
        Enum.GetValues<PermAction>().Aggregate(0, (all, a) => all | (int)a);

    /// <summary>Где этот человек есть; пусто вне сводного вида.</summary>
    public IReadOnlyList<AdminUserPlace> Places { get; } = [];

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

    /// <summary>Ждет допуска (у администратора допуск есть всегда).</summary>
    public bool IsPending => !_isAdmin && !_isApproved;

    /// <summary>Действующая маска прав в выбранной области.</summary>
    public int Mask
    {
        get => _mask;
        set
        {
            if (_mask == value) return;
            _mask = value;
            Notify(nameof(Mask));
            NotifyRights();
        }
    }

    /// <summary>
    /// Галка действия. Ключ — бит <see cref="PermAction"/> (путь биндинга «[64]»).
    /// У администратора разрешено все — галки стоят и не редактируются.
    ///
    /// <c>null</c> бывает только в сводном виде и значит «по-разному»: в одних базах разрешено,
    /// в других нет. Снять галку из этого состояния нельзя одним щелчком — он ставит ее, и это
    /// правильно: «выровнять по разрешению» и «выровнять по запрету» должны различаться
    /// действием, а не тем, с какой стороны человек начал.
    /// </summary>
    public bool? this[int bit]
    {
        get => _isAdmin                 ? true
             : (_mixed & bit) == bit    ? null
             : (_mask  & bit) == bit;
        set
        {
            if (value is not { } wanted || this[bit] == value) return;
            RightChanged?.Invoke(this, (PermAction)bit, wanted);
        }
    }

    public string Role
    {
        get => _isAdmin ? PermissionService.RoleAdmin : PermissionService.RoleUser;
        set
        {
            var isAdmin = value == PermissionService.RoleAdmin;
            if (isAdmin == _isAdmin) return;
            RoleChanged?.Invoke(this, isAdmin);
        }
    }

    public bool IsApproved
    {
        get => _isAdmin || _isApproved;
        set
        {
            if (IsApproved == value) return;
            ApprovalChanged?.Invoke(this, value);
        }
    }

    // ── Доступность ячеек ─────────────────────────────────────────────────────

    /// <summary>Роль и удаление правит только ДРУГОЙ администратор.</summary>
    public bool RoleEnabled   => !IsSelf;
    public bool RemoveEnabled => !IsSelf;

    /// <summary>Допуск администратора не снимается (у него доступ по роли).</summary>
    public bool ApprovalEnabled => !IsSelf && !_isAdmin;

    /// <summary>Действия: администратору можно все (галки заблокированы), ожидающему
    /// допуска настраивать нечего, пока допуск не выдан.</summary>
    public bool RightsEnabled => !IsSelf && !_isAdmin && _isApproved;

    /// <summary>Права уровня базы («Создавать коллекции», оба права интеграций) — только
    /// при выбранной области «вся база».</summary>
    public bool DatabaseOnlyEnabled => RightsEnabled && _scopeIsDatabase;

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // Привязка индексатора обновляется по имени «Item» (не «Item[key]») — правило Avalonia.
    private void NotifyRights() => Notify("Item");

    private static string FormatTs(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "—";
}
