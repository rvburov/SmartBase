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
/// высоту справа (тот же приём, что панель «Детали» в «Настройки → Диагностика»).
/// Строка — пользователь базы, колонки — роль, доступ к базе, действия <see cref="PermAction"/>
/// с чекбоксами, даты и удаление из базы. Область действия прав (вся база или коллекция)
/// выбирается в шапке панели.
///
/// Вид таблицы — как у таблицы конструктора и журналов: иконка типа + подпись + шеврон
/// фильтра в шапке, те же линии, высота строки RowHeightStandard. Фильтр и сортировка
/// считаются в памяти (пользователей десятки), но окно — то же самое, что в «Диагностике»
/// и «Истории изменений».
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
    ];

    // Все прочитанные строки и то, что показано после фильтра и сортировки.
    private readonly List<AdminUserRow>        _allUserRows = [];
    private readonly AvaloniaList<AdminUserRow> _userRows   = [];

    private readonly Dictionary<string, HashSet<string>> _userFilters  = [];
    private readonly Dictionary<string, Path>            _userChevrons = [];
    private string? _userSortKey;
    private bool    _userSortAsc = true;
    private Flyout? _userFlyout;

    // Области прав в порядке ScopeCombo: null = вся база, иначе id коллекции.
    private List<int?> _scopes = [];

    // Колонки строятся один раз при первом открытии.
    private bool _usersHooked;

    private int? CurrentScope =>
        ScopeCombo.SelectedIndex >= 0 && ScopeCombo.SelectedIndex < _scopes.Count
            ? _scopes[ScopeCombo.SelectedIndex]
            : null;

    // ── Открытие и закрытие ───────────────────────────────────────────────────

    // Список пользователей открывается ВМЕСТО панели администратора и занимает её место
    // целиком: своя шапка (56) встаёт на место навбара модуля, тот скрывается — иначе
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
        // Возвращаемся в панель администратора, только если раздел ещё открыт: при уходе
        // в «Общие данные» или «Историю» видимость панелей уже расставил ShowCategory.
        AdminPanel.IsVisible = _categoryKey == "admin";

        _userRows.Clear();
        _allUserRows.Clear();

        // Панель открывается свежей: фильтры и сортировка не переезжают на другую базу
        // (так же ведёт себя «История изменений» при смене базы).
        _userFilters.Clear();
        _userSortKey = null;
        UpdateUsersChevrons();
    }

    /// <summary>Панель открыта (её данные нужно перечитывать после правок).</summary>
    private bool UsersPanelOpen => UsersPanel.IsVisible;

    // ── Область действия прав ─────────────────────────────────────────────────

    // «Вся база данных» + коллекции выбранной базы. Настройки коллекции приоритетнее
    // настроек базы — так же считает PermissionService.GetMask.
    private void RebuildScopes()
    {
        if (_adminDb is null) return;

        // Сохранить выбранную область, если она ещё существует (перечитывание после
        // чужих правок не должно сбрасывать администратора на «всю базу»).
        var keep = CurrentScope;

        var collections = _adminDb.Constructor.GetAllCollections();
        _scopes = [null];
        var labels = new List<string> { "Вся база данных" };
        foreach (var c in collections.Where(c => c.Id.HasValue))
        {
            _scopes.Add(c.Id);
            labels.Add(c.Name);
        }

        ScopeCombo.ItemsSource = labels;
        var idx = keep is int cid ? _scopes.IndexOf(cid) : 0;
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

        var scope = CurrentScope;
        var perms = _adminDb.Permissions;
        foreach (var user in perms.GetUsers())
            _allUserRows.Add(new AdminUserRow(user, perms.GetMask(user.Id, scope), scope is null)
            {
                RightChanged    = OnUserRightChanged,
                ApprovalChanged = OnUserApprovalChanged,
                RoleChanged     = OnUserRoleChanged,
            });

        ApplyUserFilters();
    }

    // Фильтр и сортировка считаются здесь же: пользователей десятки, ходить в БД незачем.
    // Порядок по умолчанию — по имени (стабильный): иначе галка доступа переставляла бы
    // строку прямо под курсором.
    private void ApplyUserFilters()
    {
        IEnumerable<AdminUserRow> rows = _allUserRows;

        foreach (var (key, allowed) in _userFilters)
            rows = rows.Where(r => allowed.Contains(UsersCellValue(key, r)));

        var sortKey = _userSortKey ?? UsersKeyName;
        var asc     = _userSortKey is null || _userSortAsc;
        rows = asc
            ? rows.OrderBy(r => UsersCellValue(sortKey, r), StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderByDescending(r => UsersCellValue(sortKey, r), StringComparer.CurrentCultureIgnoreCase);

        _userRows.Clear();
        _userRows.AddRange(rows);
    }

    // Значение колонки строкой — общий источник для фильтра, сортировки и окна значений.
    private static string UsersCellValue(string key, AdminUserRow row) => key switch
    {
        UsersKeyName     => row.DisplayName,
        UsersKeyId       => row.UserKey,
        UsersKeyRole     => row.Role,
        UsersKeyApproved => row.IsApproved ? "Есть" : "Нет",
        UsersKeyCreated  => row.CreatedAtText,
        UsersKeySeen     => row.LastSeenText,
        _ when key.StartsWith("act", StringComparison.Ordinal) &&
               int.TryParse(key.AsSpan(3), out var bit)
                         => row[bit] ? "Разрешено" : "Запрещено",
        _                => "",
    };

    // Правки пишутся сразу; после них строки перечитываются — роль и допуск меняют
    // доступность соседних ячеек, а сосед мог тем временем поправить своё.
    private void OnUserRightChanged(AdminUserRow row, PermAction action, bool allowed)
    {
        if (_adminDb is null) return;
        var mask = allowed ? row.Mask | (int)action : row.Mask & ~(int)action;
        if (!RunAdminAction(() => _adminDb.Permissions.SetMask(row.Id, CurrentScope, mask))) return;
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
                                          "Ключ «учётная запись@компьютер» — по нему база узнаёт человека " +
                                          "при следующем открытии, даже если он сменит имя в профиле."));
        AddUsersColumn(UsersKeyRole, "Роль", "IconShieldUser", 170, BuildUserRoleCell(),
                       tip: BuildUsersTip("Роль",
                                          "Администратор управляет доступом и не подчиняется галкам — " +
                                          "ему разрешено всё. Чтобы ограничить его, сначала переведите " +
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
            // «Создавать коллекции» имеет смысл только на уровне базы: у несозданной
            // коллекции переопределений нет.
            var enabledPath = action == PermAction.CreateCollection
                ? nameof(AdminUserRow.CreateCollectionEnabled)
                : nameof(AdminUserRow.RightsEnabled);
            // Ширина под самую длинную подпись («Дублирование коллекции») — колонки действий
            // одинаковые, иначе шапка выглядит рваной; лишнее уходит в прокрутку.
            AddUsersColumn($"act{(int)action}", PermActionLabels.Title(action), iconKey, 210,
                           BuildUsersCheckCell($"[{(int)action}]", enabledPath),
                           tip: BuildUsersActionTip(group, action));
        }

        AddUsersColumn(UsersKeyCreated, "Подключён", "IconClock", 150,
                       BuildUsersTextCell(nameof(AdminUserRow.CreatedAtText), secondary: true),
                       tip: BuildUsersTip("Подключён",
                                          "Когда пользователь впервые открыл эту базу данных."));
        AddUsersColumn(UsersKeySeen, "Последняя активность", "IconHistory", 190,
                       BuildUsersTextCell(nameof(AdminUserRow.LastSeenText), secondary: true),
                       tip: BuildUsersTip("Последняя активность",
                                          "Когда пользователь в последний раз открывал эту базу данных."));
        // Колонка действия: фильтровать нечего — шеврона нет. Последняя колонка правой линии
        // заголовка не рисует: её край и так очерчен краем таблицы (как в диагностике).
        AddUsersColumn("", "Удаление пользователя", "IconTrash", 210, BuildUserRemoveCell(),
                       tip: BuildUsersTip("Удаление пользователя",
                                          "Убрать пользователя из базы данных вместе с выданными правами. " +
                                          "При следующем открытии базы он вернётся в список как ожидающий допуска."),
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

    // Для колонки действия заголовок подсказки несёт ещё и группу: «Записи · Удаление
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

    // ── Фильтр и сортировка колонки (то же окно, что в «Диагностике») ──────────

    private void ShowUsersFilterFlyout(Control anchor, string key)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        flyout.Content = WindowZoom.WrapPopupContent(BuildUsersFilterPanel(key, () => flyout.Hide()));
        flyout.Closed += (_, _) => { if (ReferenceEquals(_userFlyout, flyout)) _userFlyout = null; };
        _userFlyout = flyout;
        flyout.ShowAt(anchor);
    }

    private Control BuildUsersFilterPanel(string key, Action close)
    {
        var secondary = Brush("BrushSecondaryText", "#8E8E93");
        var fontBase  = FontSizeToken("FontSizeBase", 13);
        var radius    = this.TryFindResource("RadiusBase", out var r) && r is CornerRadius cr
            ? cr : new CornerRadius(6);
        var family    = this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null;
        var metrics   = new FilterValueMetrics();

        var panel = new StackPanel
        {
            Width   = 240,
            Margin  = new Thickness(12),
            Spacing = 2,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        // Значения колонки со счётчиками — по всем строкам базы (фильтры других колонок
        // список значений не режут: так же ведёт себя фильтр в диагностике).
        var counts = _allUserRows
            .GroupBy(row => UsersCellValue(key, row))
            .Select(g => (Value: g.Key, Count: g.Count()))
            .ToList();
        var domain = counts.Select(c => c.Value).ToHashSet();

        var (ascLabel, descLabel) = key is UsersKeyCreated or UsersKeySeen
            ? ("Сначала старые", "Сначала новые")
            : ("От А до Я", "От Я до А");
        bool? pendingSort = _userSortKey == key ? _userSortAsc : null;

        var sortSection = new StackPanel { Spacing = 2 };
        panel.Children.Add(sortSection);

        Action rebuildListRef = () => { };
        var ordered = SortUsersValueCounts(counts, pendingSort);

        void ReloadCounts() => ordered = SortUsersValueCounts(counts, pendingSort);

        void RenderSort()
        {
            sortSection.Children.Clear();
            sortSection.Children.Add(MakeUsersFlatItem(ascLabel,
                () => { pendingSort = true;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                UsersIcon("IconSortAz"), active: pendingSort == true));
            sortSection.Children.Add(MakeUsersFlatItem(descLabel,
                () => { pendingSort = false; RenderSort(); ReloadCounts(); rebuildListRef(); },
                UsersIcon("IconSortZa"), active: pendingSort == false));
            sortSection.Children.Add(MakeUsersFlatItem("Снять сортировку",
                () => { pendingSort = null;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                UsersIcon("IconClose")));
        }
        RenderSort();

        panel.Children.Add(MakeUsersSeparator());

        var search = new TextBox
        {
            Watermark    = "Поиск значений...",
            Height       = 30,
            CornerRadius = radius,
            Background   = Brushes.White,
            Margin       = new Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        var checkedSet = new HashSet<string>(
            _userFilters.TryGetValue(key, out var saved) ? saved : domain);
        var syncingSelectAll = false;

        var selectAll = new CheckBox
        {
            Content   = "(Выделить всё)",
            FontSize  = fontBase,
            IsChecked = !_userFilters.ContainsKey(key),
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        var shown   = new AvaloniaList<UsersFilterValueVm>();
        var vmCache = new Dictionary<string, UsersFilterValueVm>();

        var valuesList = new ListBox
        {
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ItemsSource     = shown,
            ItemTemplate    = BuildUsersFilterValueTemplate(fontBase, secondary, metrics),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        void FlatRow(params string[] pseudo) =>
            valuesList.Styles.Add(new Style(x =>
            {
                Selector s = x.OfType<ListBoxItem>();
                foreach (var p in pseudo) s = s.Class(p);
                return s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        FlatRow();
        FlatRow(":pointerover");
        FlatRow(":selected");
        FlatRow(":selected", ":pointerover");

        var valuesBox = ScrollBarLink.HeaderedList(selectAll, valuesList, gap: 2,
                                                   scroller: metrics.Track);

        var valuesHost = new Border
        {
            Background   = Brushes.White,
            BorderBrush  = Brush("BrushSeparator", "#E5E5EA"),
            CornerRadius = radius,
            Padding      = new Thickness(0),
            ClipToBounds = true,
            Height       = 232,
            Child        = EmptyListHint.Wrap(valuesList, search, secondary, fontBase, content: valuesBox),
        };
        valuesHost.Bind(Border.BorderThicknessProperty, valuesHost.GetResourceObservable("BorderLine"));
        panel.Children.Add(valuesHost);

        List<(string Value, int Count)> Matched()
        {
            var q = search.Text?.Trim() ?? "";
            return q.Length == 0
                ? ordered
                : ordered.Where(c => UsersDisplayValue(c.Value)
                    .Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        void SyncSelectAll()
        {
            syncingSelectAll    = true;
            selectAll.IsChecked = Matched().All(c => checkedSet.Contains(c.Value));
            syncingSelectAll    = false;
        }

        UsersFilterValueVm Vm(string raw, int count) =>
            vmCache.TryGetValue(raw, out var v) ? v
                : vmCache[raw] = new UsersFilterValueVm(raw, UsersDisplayValue(raw), count,
                                                        checkedSet, SyncSelectAll);

        void RebuildList()
        {
            shown.Clear();
            shown.AddRange(Matched().Select(c => Vm(c.Value, c.Count)));
            metrics.Measure(shown.Select(v => v.Display), shown.Select(v => v.CountText),
                            fontBase, family);
        }
        rebuildListRef = RebuildList;
        RebuildList();
        SyncSelectAll();

        search.TextChanged += (_, _) => { RebuildList(); SyncSelectAll(); };
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncingSelectAll) return;
            var on = selectAll.IsChecked == true;
            foreach (var (value, _) in Matched())
            {
                if (on) checkedSet.Add(value);
                else    checkedSet.Remove(value);
            }
            RebuildList();
        };

        panel.Children.Add(MakeUsersSeparator());

        var okBtn = new Button
        {
            Content             = "Готово",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        okBtn.Classes.Add("dialog");
        okBtn.Click += (_, _) =>
        {
            if (pendingSort is bool asc) { _userSortKey = key; _userSortAsc = asc; }
            else if (_userSortKey == key) _userSortKey = null;

            var hasSearch = !string.IsNullOrWhiteSpace(search.Text);
            var effective = hasSearch
                ? Matched().Select(c => c.Value).Where(checkedSet.Contains).ToHashSet()
                : [.. checkedSet];

            if (domain.SetEquals(effective)) _userFilters.Remove(key);
            else                             _userFilters[key] = effective;

            UpdateUsersChevrons();
            ApplyUserFilters();
            close();
        };
        panel.Children.Add(okBtn);

        return panel;
    }

    // Порядок значений в окне фильтра: выбранная сортировка, «(Пусто)» в конце.
    private static List<(string Value, int Count)> SortUsersValueCounts(
        List<(string Value, int Count)> src, bool? asc)
    {
        var items = src.Where(c => c.Value.Length > 0).ToList();
        var empty = src.Where(c => c.Value.Length == 0).ToList();

        items = asc == false
            ? items.OrderByDescending(c => c.Value, StringComparer.CurrentCultureIgnoreCase).ToList()
            : items.OrderBy(c => c.Value, StringComparer.CurrentCultureIgnoreCase).ToList();

        items.AddRange(empty);
        return items;
    }

    private static string UsersDisplayValue(string raw) => raw.Length == 0 ? "(Пусто)" : raw;

    private Viewbox MakeUsersStrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width  = FontSizeToken("IconSizeMedium", 18),
        Height = FontSizeToken("IconSizeMedium", 18),
        VerticalAlignment = VerticalAlignment.Center,
        Child  = new Canvas
        {
            Width  = 24,
            Height = 24,
            Children =
            {
                new Path
                {
                    Data            = data,
                    Stroke          = stroke,
                    StrokeThickness = FontSizeToken("IconStrokeMedium", 1.2),
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    private Button MakeUsersFlatItem(string text, Action onClick, Geometry? icon = null, bool active = false)
    {
        var fg = Brush(active ? "BrushAccent" : "BrushPrimaryText", active ? "#007AFF" : "#1C1C1E");

        object content = text;
        if (icon is not null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var iconBox = MakeUsersStrokeIcon(icon, fg);
            Grid.SetColumn(iconBox, 0);
            row.Children.Add(iconBox);

            var label = new TextBlock
            {
                Text                = text,
                Foreground          = fg,
                Margin              = new Thickness(8, 0, 0, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            if (active)
            {
                var check = MakeUsersStrokeIcon(UsersIcon("IconCheck"), fg);
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }

            content = row;
        }

        var btn = new Button
        {
            Content         = content,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(8, 6),
            FontSize        = FontSizeToken("FontSizeBase", 13),
            Foreground      = fg,
            CornerRadius    = this.TryFindResource("RadiusBase", out var r) && r is CornerRadius cr
                ? cr : new CornerRadius(6),
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = icon is not null
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Left,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Border MakeUsersSeparator()
    {
        var sep = new Border
        {
            Background = Brush("BrushSeparator", "#E5E5EA"),
            Margin     = new Thickness(0, 6),
        };
        sep.Bind(HeightProperty, sep.GetResourceObservable("SeparatorThickness"));
        sep.Bind(UseLayoutRoundingProperty, sep.GetResourceObservable("LineUseLayoutRounding"));
        RenderOptions.SetEdgeMode(sep, EdgeMode.Aliased);
        return sep;
    }

    private static IDataTemplate BuildUsersFilterValueTemplate(double fontBase, IBrush secondary,
                                                               FilterValueMetrics metrics) =>
        new FuncDataTemplate<UsersFilterValueVm>((_, _) =>
        {
            var name = new TextBlock
            {
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(UsersFilterValueVm.Display)));
            name.Bind(Layoutable.MinWidthProperty,
                      new Binding(nameof(FilterValueMetrics.TextMinWidth)) { Source = metrics });

            var cb = new CheckBox
            {
                Content   = name,
                FontSize  = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin    = new Thickness(0),
                Cursor    = new Cursor(StandardCursorType.Hand),
            };
            cb.Bind(ToggleButton.IsCheckedProperty,
                    new Binding(nameof(UsersFilterValueVm.Checked)) { Mode = BindingMode.TwoWay });

            var cnt = new TextBlock { Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(UsersFilterValueVm.CountText)));

            var pin = new TranslateTransform();
            pin.Bind(TranslateTransform.XProperty,
                     new Binding(nameof(FilterValueMetrics.Pin)) { Source = metrics });
            var cntHost = new Border
            {
                Background          = Brushes.White,
                Padding             = new Thickness(FilterValueMetrics.Gap, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child               = cnt,
                RenderTransform     = pin,
            };

            var cntSlot = new Border { Child = cntHost };
            cntSlot.Bind(Layoutable.MinWidthProperty,
                         new Binding(nameof(FilterValueMetrics.CountMinWidth)) { Source = metrics });

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);      row.Children.Add(cb);
            Grid.SetColumn(cntSlot, 1); row.Children.Add(cntSlot);
            return row;
        });

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

    // Имя пользователя + пометка «ждёт допуска» (у допущенных и админов её нет).
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
                Text              = "ждёт допуска",
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
            // DataContext ячейки — строка таблицы; берём его в момент клика, а не из
            // замыкания шаблона: контейнер ячейки переиспользуется при прокрутке.
            button.Click += (s, _) =>
            {
                if ((s as Control)?.DataContext is AdminUserRow row) OnUserRemove(row);
            };
            return button;
        });

    /// <summary>Строка окна фильтра: значение колонки, счётчик и галка (зеркало
    /// LogsFilterValueVm «Диагностики» — окно фильтра во всех таблицах одинаковое).</summary>
    private sealed class UsersFilterValueVm : INotifyPropertyChanged
    {
        private readonly HashSet<string> _checked;
        private readonly Action _onToggle;

        public UsersFilterValueVm(string raw, string display, int count,
                                  HashSet<string> checkedSet, Action onToggle)
        {
            Raw       = raw;
            Display   = display;
            CountText = count.ToString("N0");
            _checked  = checkedSet;
            _onToggle = onToggle;
        }

        public string Raw       { get; }
        public string Display   { get; }
        public string CountText { get; }

        public bool Checked
        {
            get => _checked.Contains(Raw);
            set
            {
                if (value) _checked.Add(Raw);
                else       _checked.Remove(Raw);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
                _onToggle();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

/// <summary>
/// Строка таблицы пользователей: снапшот <see cref="DbUserEntity"/> + действующая маска прав
/// в выбранной области. Правки уходят в базу через колбэки панели (RightChanged/ApprovalChanged/
/// RoleChanged) прямо из сеттеров — как строка таблицы конструктора (TableRowVm).
///
/// Правила доступности (панель их только отображает, сервис проверяет ещё раз):
/// свою строку не правит никто; у администратора все действия разрешены и заблокированы
/// (роль даёт полный доступ — чтобы ограничить, другой администратор сначала меняет роль);
/// у ожидающего допуска чекбоксы действий выключены, пока допуск не выдан.
/// </summary>
public sealed class AdminUserRow : INotifyPropertyChanged
{
    private int  _mask;
    private readonly bool _isAdmin;
    private readonly bool _isApproved;
    private readonly bool _scopeIsDatabase;

    public AdminUserRow(DbUserEntity user, int mask, bool scopeIsDatabase)
    {
        Id          = user.Id;
        UserKey     = user.UserKey;
        DisplayName = user.DisplayName;
        IsSelf      = user.UserKey == CurrentUser.Key;
        CreatedAtText = FormatTs(user.CreatedAt);
        LastSeenText  = FormatTs(user.LastSeenAt);
        _mask       = mask;
        _isAdmin    = user.IsAdmin;
        _isApproved = user.IsApproved;
        _scopeIsDatabase = scopeIsDatabase;
    }

    public int    Id          { get; }
    public string UserKey     { get; }
    public string DisplayName { get; }
    public bool   IsSelf      { get; }
    public string CreatedAtText { get; }
    public string LastSeenText  { get; }

    /// <summary>Колбэки панели: пишут правку в базу (сервис проверяет права ещё раз).</summary>
    public Action<AdminUserRow, PermAction, bool>? RightChanged    { get; init; }
    public Action<AdminUserRow, bool>?             ApprovalChanged { get; init; }
    public Action<AdminUserRow, bool>?             RoleChanged     { get; init; }

    public string Title => IsSelf ? $"{DisplayName} (вы)" : DisplayName;

    /// <summary>Ждёт допуска (у администратора допуск есть всегда).</summary>
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

    /// <summary>Галка действия. Ключ — бит <see cref="PermAction"/> (путь биндинга «[64]»).
    /// У администратора разрешено всё — галки стоят и не редактируются.</summary>
    public bool this[int bit]
    {
        get => _isAdmin || (_mask & bit) == bit;
        set
        {
            if (this[bit] == value) return;
            RightChanged?.Invoke(this, (PermAction)bit, value);
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

    /// <summary>Действия: администратору можно всё (галки заблокированы), ожидающему
    /// допуска настраивать нечего, пока допуск не выдан.</summary>
    public bool RightsEnabled => !IsSelf && !_isAdmin && _isApproved;

    /// <summary>«Создавать коллекции» — только на уровне базы.</summary>
    public bool CreateCollectionEnabled => RightsEnabled && _scopeIsDatabase;

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
