using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using DataBase.Controls.Dialogs;
using DataBase.Core.Importers;
using DataBase.Core.Models;
using DataBase.Database;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Components;

public enum SidebarMode { Calendar, Constructor }

public partial class Sidebar : UserControl
{
    // Календарь: активные id — по БД (DbId → набор id коллекций), т.к. id не уникальны между БД.
    public event EventHandler<Dictionary<int, HashSet<int>>>? ActiveIdsChanged;
    public event EventHandler?                                CalendarsChanged;

    // Конструктор: выбор несёт (DbId, CollectionId) — при мульти-маунте id не уникален между БД.
    public event EventHandler<EntityRef>? CollectionSelected;
    public event EventHandler<int>?       CollectionDeleted;
    public event EventHandler?            RequestRefresh;

    // Смонтированные БД сайдбара. В режиме календаря — одна (дефолт). В конструкторе —
    // все из рабочего пространства, и каждая показывается корневым узлом (_showRoots).
    private sealed record MountedDb(int DbId, string Name, ConstructorRepository Repo);
    private readonly List<MountedDb> _dbs = [];
    private bool _showRoots;

    private SidebarMode _mode = SidebarMode.Calendar;
    private SortState _sortState = SortState.None;
    private bool _expandedAll;

    // Логика как в PyQt: активны все коллекции, кроме явно отключённых пользователем.
    // Ключ — (DbId, CollectionId): id коллекций не уникальны между БД.
    private readonly HashSet<(int Db, int Id)> _uncheckedIds = [];
    // Свёрнутые корни-БД (по DbId) — чтобы состояние раскрытия переживало Refresh.
    private readonly HashSet<int> _collapsedDbRoots = [];

    private List<SidebarNode> _roots = [];
    private readonly ObservableCollection<SidebarRow> _rows = [];

    private ConstructorRepository? RepoFor(int dbId) => _dbs.FirstOrDefault(d => d.DbId == dbId)?.Repo;
    private ConstructorRepository? RepoOf(SidebarNode node) => RepoFor(node.DbId);
    private int DefaultDbId => _dbs.Count > 0 ? _dbs[0].DbId : 0;

    // Описатели всех смонтированных БД для диалогов (выбор целевой БД + перенос).
    private List<DbOption> DbOptions() =>
        _dbs.Select(d => new DbOption(
            d.DbId,
            string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name,
            d.Repo)).ToList();

    // Выделение: подсветка + цель удаления. Обычный клик — одна запись; Shift/Ctrl/Cmd-клик
    // — точечный toggle (группы и коллекции вместе) для пакетного удаления.
    private readonly HashSet<SidebarNode> _selectedNodes = [];
    // Открытая в основном окне коллекция — для подсветки и восстановления после Refresh.
    private EntityRef? _open;
    // Был ли последний клик мышью внутри сайдбара. Delete удаляет выделение сайдбара
    // только в этом случае — иначе пользователь работает в таблице/поле справа, и
    // клавиша не должна трогать выделение (в т.ч. пассивно подсвеченную коллекцию).
    private bool _lastPressInSidebar;

    // Синий #007AFF трактуется как «без цвета» (серый) → отдельный пункт меню.
    private static readonly (string Hex, string Name)[] PredefinedColors =
    [
        ("#34C759", "Зеленый"),    ("#FF3B30", "Красный"),
        ("#FF9500", "Оранжевый"), ("#FF2D55", "Розовый"), ("#AF52DE", "Фиолетовый"),
        ("#FFCC00", "Желтый"),    ("#5E5CE6", "Индиго"),  ("#64D2FF", "Голубой"),
        ("#FF6B6B", "Коралловый"),
    ];

    public Sidebar()
    {
        InitializeComponent();
        RowsHost.ItemsSource = _rows;

        CreateBtn.Click            += (_, _) => _ = CreateItem("collection", DefaultDbId, parentId: null);
        ExpandBtn.IsCheckedChanged += (_, _) => ToggleExpandAll(ExpandBtn.IsChecked == true);
        SortBtn.Click              += (_, _) => CycleSort();
        ToggleAllBtn.Click         += (_, _) => ToggleAllChecked();
        ImportExportBtn.Click      += (_, _) => _ = OpenImportExport();
    }

    // Календарь: одна БД, без корневых узлов (вид как прежде). DbId не важен — узлы
    // получают DbId = 0, RepoFor(0) указывает на единственный репозиторий.
    public void Initialize(ConstructorRepository repository, SidebarMode mode)
    {
        _dbs.Clear();
        _dbs.Add(new MountedDb(0, "", repository));
        _showRoots = false;
        _mode = mode;
        ToggleAllBtn.IsVisible = mode == SidebarMode.Calendar;
        Refresh();
    }

    // Конструктор: несколько БД из рабочего пространства, каждая — корневой узел.
    public void InitializeWorkspace(IReadOnlyList<DatabaseManager> databases, SidebarMode mode)
    {
        _dbs.Clear();
        foreach (var d in databases)
            _dbs.Add(new MountedDb(d.DbId, d.Name, d.Constructor));
        _showRoots = true;
        _mode = mode;
        ToggleAllBtn.IsVisible = mode == SidebarMode.Calendar;
        Refresh();
    }

    public void Refresh()
    {
        if (_dbs.Count == 0) return;

        var expandedIds = GetExpandedGroupIds();
        CaptureDbRootStates();

        _roots = BuildTree();
        RestoreExpanded(_roots, expandedIds);

        // Мультивыделение не переживает перестройку дерева; восстанавливаем только
        // подсветку открытой коллекции по (DbId, Id) (в режиме конструктора).
        _selectedNodes.Clear();
        if (_mode == SidebarMode.Constructor && _open is { } open)
        {
            var node = FindNode(_roots, "collection", open.DbId, open.Id);
            if (node is not null) { _selectedNodes.Add(node); ExpandAncestors(_roots, node); }
            else _open = null;
        }

        RebuildRows();
        UpdateToggleAllIcon();
    }

    // Запоминаем, какие корни-БД свёрнуты, чтобы состояние пережило перестройку дерева.
    private void CaptureDbRootStates()
    {
        foreach (var n in _roots.Where(n => n.IsDatabase))
        {
            if (n.IsExpanded) _collapsedDbRoots.Remove(n.DbId);
            else              _collapsedDbRoots.Add(n.DbId);
        }
    }

    // Активные коллекции по каждой смонтированной БД (для фильтра событий в календаре).
    public Dictionary<int, HashSet<int>> GetActiveIds()
    {
        var result = new Dictionary<int, HashSet<int>>();
        foreach (var db in _dbs)
        {
            var ids = db.Repo.GetAllCollections()
                .Where(c => c.Id.HasValue).Select(c => c.Id!.Value)
                .Where(id => !_uncheckedIds.Contains((db.DbId, id)))
                .ToHashSet();
            result[db.DbId] = ids;
        }
        return result;
    }

    public void SelectCollection(int dbId, int collectionId)
    {
        if (_mode != SidebarMode.Constructor) return;
        var target = FindNode(_roots, "collection", dbId, collectionId);
        if (target is null) return;
        ExpandAncestors(_roots, target);
        _selectedNodes.Clear();
        _selectedNodes.Add(target);
        _open = new EntityRef(dbId, collectionId);
        RebuildRows();
    }

    // ── Плоский список видимых строк ─────────────────────────────────────────────

    private void RebuildRows()
    {
        _rows.Clear();
        Flatten(_roots, 0);
    }

    private void Flatten(IEnumerable<SidebarNode> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            _rows.Add(new SidebarRow(n, depth, _mode, IsChecked(n), _selectedNodes.Contains(n)));
            if ((n.IsGroup || n.IsDatabase) && n.IsExpanded)
                Flatten(n.Children, depth + 1);
        }
    }

    private bool IsChecked(SidebarNode n) =>
        !n.IsGroup && !n.IsDatabase && n.Id.HasValue && !_uncheckedIds.Contains((n.DbId, n.Id.Value));

    // ── Построение дерева ──────────────────────────────────────────────────────

    private List<SidebarNode> BuildTree()
    {
        if (_dbs.Count == 0) return [];

        // Конструктор: каждая БД — корневой узел, под ним её дерево.
        if (_showRoots)
        {
            var roots = new List<SidebarNode>();
            foreach (var db in _dbs)
            {
                var rootNode = new SidebarNode
                {
                    NodeType   = "database",
                    DbId       = db.DbId,
                    Id         = db.DbId,
                    Name       = db.Name,
                    IsExpanded = !_collapsedDbRoots.Contains(db.DbId),
                };
                foreach (var child in BuildDbChildren(db))
                    rootNode.Children.Add(child);
                roots.Add(rootNode);
            }
            return roots;
        }

        // Календарь: одна БД без корня.
        return BuildDbChildren(_dbs[0]);
    }

    private List<SidebarNode> BuildDbChildren(MountedDb db)
    {
        var groups      = db.Repo.GetAllGroups();
        var collections = db.Repo.GetAllCollections();
        var root        = new List<SidebarNode>();

        foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.Position))
            root.Add(BuildGroupNode(db.DbId, g, groups, collections));
        foreach (var c in collections.Where(c => c.GroupId is null).OrderBy(c => c.Position))
            root.Add(MakeCollectionNode(db.DbId, c));

        return SortNodes(root);
    }

    private SidebarNode BuildGroupNode(int dbId, DbGroup group, List<DbGroup> allGroups, List<DbCollection> allCollections)
    {
        var node = new SidebarNode { NodeType = "group", DbId = dbId, Id = group.Id, Name = group.Name };

        var children = new List<SidebarNode>();
        foreach (var child in allGroups.Where(g => g.ParentId == group.Id).OrderBy(g => g.Position))
            children.Add(BuildGroupNode(dbId, child, allGroups, allCollections));
        foreach (var col in allCollections.Where(c => c.GroupId == group.Id).OrderBy(c => c.Position))
            children.Add(MakeCollectionNode(dbId, col));

        foreach (var c in SortNodes(children))
            node.Children.Add(c);

        return node;
    }

    private static SidebarNode MakeCollectionNode(int dbId, DbCollection c) =>
        new() { NodeType = "collection", DbId = dbId, Id = c.Id, Name = c.Name, Color = c.Color };

    private List<SidebarNode> SortNodes(List<SidebarNode> nodes)
    {
        var groups = nodes.Where(n => n.IsGroup).ToList();
        var colls  = nodes.Where(n => !n.IsGroup).ToList();

        if (_sortState != SortState.None)
        {
            var desc = _sortState == SortState.Desc;
            groups = ApplyOrder(groups, desc);
            colls  = ApplyOrder(colls, desc);
        }
        return [.. groups, .. colls];
    }

    private static List<SidebarNode> ApplyOrder(List<SidebarNode> nodes, bool desc) =>
        desc ? nodes.OrderByDescending(n => NameKey(n.Name)).ToList()
             : nodes.OrderBy(n => NameKey(n.Name)).ToList();

    private static (int, string) NameKey(string name)
    {
        var s = name.TrimStart();
        return s.Length > 0 && char.IsDigit(s[0]) ? (0, s.ToLowerInvariant()) : (1, s.ToLowerInvariant());
    }

    // ── Раскрытие/сворачивание ──────────────────────────────────────────────────

    private void ToggleExpandAll(bool expand)
    {
        _expandedAll = expand;
        ToolTip.SetTip(ExpandBtn, expand ? "Свернуть все" : "Развернуть все");
        ExpandIcon.IsVisible   = !expand;
        CollapseIcon.IsVisible = expand;

        SetExpandedRecursive(_roots, expand);
        RebuildRows();
    }

    private static void SetExpandedRecursive(IEnumerable<SidebarNode> nodes, bool expand)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup || n.IsDatabase) n.IsExpanded = expand;
            SetExpandedRecursive(n.Children, expand);
        }
    }

    // Ключ раскрытия — (DbId, GroupId): id групп не уникальны между БД.
    private HashSet<(int Db, int Id)> GetExpandedGroupIds()
    {
        var ids = new HashSet<(int, int)>();
        Collect(_roots);
        return ids;

        void Collect(IEnumerable<SidebarNode> ns)
        {
            foreach (var n in ns)
            {
                if (n.IsGroup && n.IsExpanded && n.Id.HasValue) ids.Add((n.DbId, n.Id.Value));
                Collect(n.Children);
            }
        }
    }

    private void RestoreExpanded(IEnumerable<SidebarNode> nodes, HashSet<(int Db, int Id)> ids)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup && n.Id.HasValue)
                n.IsExpanded = _expandedAll || ids.Contains((n.DbId, n.Id.Value));
            RestoreExpanded(n.Children, ids);
        }
    }

    private static bool ExpandAncestors(IEnumerable<SidebarNode> nodes, SidebarNode target)
    {
        foreach (var n in nodes)
        {
            if (n == target) return true;
            if (ExpandAncestors(n.Children, target))
            {
                n.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    private static SidebarNode? FindNode(IEnumerable<SidebarNode> nodes, string type, int dbId, int id)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == type && node.DbId == dbId && node.Id == id) return node;
            var found = FindNode(node.Children, type, dbId, id);
            if (found is not null) return found;
        }
        return null;
    }

    // ── Сортировка ───────────────────────────────────────────────────────────────

    private void CycleSort()
    {
        _sortState = _sortState switch
        {
            SortState.None => SortState.Asc,
            SortState.Asc  => SortState.Desc,
            _              => SortState.None,
        };
        ToolTip.SetTip(SortBtn, _sortState switch
        {
            SortState.Asc  => "А → Я",
            SortState.Desc => "Я → А",
            _              => "Сортировка по имени",
        });
        Refresh();
    }

    // ── Чекбоксы видимости (только календарь) ────────────────────────────────────

    private void ToggleCollection(SidebarNode node)
    {
        if (!node.Id.HasValue) return;
        var key = (node.DbId, node.Id.Value);

        if (!_uncheckedIds.Remove(key)) _uncheckedIds.Add(key);

        RebuildRows();
        UpdateToggleAllIcon();
        ActiveIdsChanged?.Invoke(this, GetActiveIds());
    }

    // Ключи (DbId, CollectionId) всех коллекций всех смонтированных БД.
    private IEnumerable<(int Db, int Id)> AllCollectionKeys() =>
        _dbs.SelectMany(db => db.Repo.GetAllCollections()
            .Where(c => c.Id.HasValue)
            .Select(c => (db.DbId, c.Id!.Value)));

    private bool AllChecked()
    {
        if (_dbs.Count == 0) return false;
        return AllCollectionKeys().All(k => !_uncheckedIds.Contains(k));
    }

    private void ToggleAllChecked()
    {
        if (_dbs.Count == 0) return;

        if (AllChecked())
            foreach (var k in AllCollectionKeys()) _uncheckedIds.Add(k);
        else
            _uncheckedIds.Clear();

        RebuildRows();
        UpdateToggleAllIcon();
        ActiveIdsChanged?.Invoke(this, GetActiveIds());
    }

    private void UpdateToggleAllIcon()
    {
        if (_mode != SidebarMode.Calendar) return;
        var on = AllChecked();
        ToolTip.SetTip(ToggleAllBtn, on ? "Выключить все" : "Включить все");
    }

    // ── Взаимодействие со строками ───────────────────────────────────────────────

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarRow row }) return;
        var node = row.Node;

        // Shift/Ctrl/Cmd + клик — точечный toggle узла в мультивыделение (группы и
        // коллекции вместе), без раскрытия/открытия. Так набираются элементы для
        // пакетного удаления через контекстное меню.
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                    || e.KeyModifiers.HasFlag(KeyModifiers.Control)
                    || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        // Корень-БД в мультивыделение не добавляем (БД удаляются только из настроек).
        if (additive && node.Id.HasValue && !node.IsDatabase)
        {
            if (!_selectedNodes.Remove(node)) _selectedNodes.Add(node);
            RebuildRows();
            return;
        }

        // Обычный клик по раскрываемому узлу (группа или корень-БД) — раскрыть/свернуть.
        if (node.IsGroup || node.IsDatabase)
        {
            node.IsExpanded = !node.IsExpanded;
            if (node.IsDatabase)
            {
                if (node.IsExpanded) _collapsedDbRoots.Remove(node.DbId);
                else                 _collapsedDbRoots.Add(node.DbId);
            }
            ResetSelectionToOpen();   // открытая коллекция остаётся подсвеченной
            RebuildRows();
            return;
        }

        if (_mode == SidebarMode.Calendar)
        {
            _selectedNodes.Clear();
            ToggleCollection(node);   // RebuildRows внутри
        }
        else
        {
            _selectedNodes.Clear();
            _selectedNodes.Add(node);
            if (node.Id.HasValue)
            {
                _open = new EntityRef(node.DbId, node.Id.Value);
                RebuildRows();
                CollectionSelected?.Invoke(this, _open.Value);
            }
            else RebuildRows();
        }
    }

    // Сброс выделения к одной открытой коллекции (она остаётся подсвеченной при
    // обычном клике по группе), как было до мультивыделения.
    private void ResetSelectionToOpen()
    {
        _selectedNodes.Clear();
        if (_mode == SidebarMode.Constructor && _open is { } open)
        {
            var node = FindNode(_roots, "collection", open.DbId, open.Id);
            if (node is not null) _selectedNodes.Add(node);
        }
    }

    // Окно, на котором висит обработчик Delete (см. OnTopLevelKeyDown). Как в таблице:
    // ловим клавишу на уровне окна — фокус UserControl ненадёжен, и собственный OnKeyDown
    // часто не получает клавишу.
    private TopLevel? _keyHookTopLevel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyHookTopLevel = TopLevel.GetTopLevel(this);
        // Клавиши и клики ловим на уровне окна (туннель): фокус UserControl ненадёжен.
        _keyHookTopLevel?.AddHandler(KeyDownEvent,        OnTopLevelKeyDown,        RoutingStrategies.Tunnel);
        _keyHookTopLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHookTopLevel?.RemoveHandler(KeyDownEvent,        OnTopLevelKeyDown);
        _keyHookTopLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _keyHookTopLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Запоминаем, был ли клик внутри сайдбара — это и есть «пользователь работает здесь».
    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _lastPressInSidebar = e.Source is Visual v && (ReferenceEquals(v, this) || this.IsVisualAncestorOf(v));

    // Delete — удалить выделенные узлы (как пункт «Удалить» в меню), с тем же диалогом и
    // подсчётом. Срабатывает, только если последний клик был в сайдбаре (иначе клавиша
    // принадлежит таблице/полю справа).
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Delete) return;
        if (_selectedNodes.Count == 0) return;
        if (IsTextInputFocused(e)) return;       // печатают в поле — Delete правит текст
        if (!_lastPressInSidebar) return;        // пользователь работает не в сайдбаре

        _ = DeleteNodes(_selectedNodes.ToList());
        e.Handled = true;
    }

    private static bool IsTextInputFocused(KeyEventArgs e) =>
        e.Source is TextBox || (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is not null);

    private void OnChevronTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarRow row }) return;
        var node = row.Node;
        if (!node.IsGroup && !node.IsDatabase) return;
        node.IsExpanded = !node.IsExpanded;
        if (node.IsDatabase)
        {
            if (node.IsExpanded) _collapsedDbRoots.Remove(node.DbId);
            else                 _collapsedDbRoots.Add(node.DbId);
        }
        RebuildRows();
        e.Handled = true;
    }

    private void OnCheckboxTapped(object? sender, TappedEventArgs e)
    {
        if (_mode != SidebarMode.Calendar) return;
        if (sender is not Control { DataContext: SidebarRow row } || row.Node.IsGroup) return;
        ToggleCollection(row.Node);
        e.Handled = true;
    }

    private void OnRowEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: SidebarRow row }) row.SetHovered(true);
    }

    private void OnRowExit(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: SidebarRow row }) row.SetHovered(false);
    }

    // ── Контекстное меню ────────────────────────────────────────────────────────

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarRow row } anchor) return;
        if (!row.Node.Id.HasValue) return;

        var menu = new ContextMenu();
        var node = row.Node;
        var db   = node.DbId;
        var id   = node.Id.Value;

        if (node.IsDatabase)
        {
            // Корень-БД: создание группы/коллекции в этой БД. Сама БД управляется в настройках.
            menu.Items.Add(MenuItemSimple("Создать группу",    () => _ = CreateItem("group", db, parentId: null)));
            menu.Items.Add(MenuItemSimple("Создать коллекцию", () => _ = CreateItem("collection", db, parentId: null)));
        }
        else if (node.IsGroup)
        {
            menu.Items.Add(MenuItemSimple("Создать группу",    () => _ = CreateItem("group", db, parentId: id)));
            menu.Items.Add(MenuItemSimple("Создать коллекцию", () => _ = CreateItem("collection", db, parentId: id)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemEdit(() => _ = EditGroup(db, id)));
            menu.Items.Add(MenuItemDuplicate(() => DuplicateGroup(db, id)));
            menu.Items.Add(MenuItemDelete(() => _ = DeleteSelectionOrNode(node)));
        }
        else
        {
            if (_mode == SidebarMode.Calendar)
            {
                menu.Items.Add(BuildColorMenu(db, id));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MenuItemEdit(() => _ = EditCollection(db, id)));
            menu.Items.Add(MenuItemDuplicate(() => DuplicateCollection(db, id)));
            menu.Items.Add(MenuItemDelete(() => _ = DeleteSelectionOrNode(node)));
        }

        menu.Open(anchor);
        e.Handled = true;
    }

    private void OnEmptyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control anchor) return;

        // Создание из пустой области идёт в БД по умолчанию.
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemSimple("Создать группу",    () => _ = CreateItem("group", DefaultDbId, parentId: null)));
        menu.Items.Add(MenuItemSimple("Создать коллекцию", () => _ = CreateItem("collection", DefaultDbId, parentId: null)));
        menu.Open(anchor);
        e.Handled = true;
    }

    private MenuItem BuildColorMenu(int dbId, int collectionId)
    {
        var colorItem = new MenuItem { Header = "Изменить цвет" };

        var noColor = new MenuItem
        {
            Header = "Без цвета",
            Icon   = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                Background  = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.Parse("#8E8E93")),
                BorderThickness = new Thickness(1.5),
            },
        };
        noColor.Click += (_, _) => ChangeColor(dbId, collectionId, "#007AFF");
        colorItem.Items.Add(noColor);
        colorItem.Items.Add(new Separator());

        foreach (var (hex, name) in PredefinedColors)
        {
            var sub = new MenuItem
            {
                Header = name,
                Icon   = new Border
                {
                    Width = 14, Height = 14, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse(hex)),
                },
            };
                sub.Click += (_, _) => ChangeColor(dbId, collectionId, hex);
            colorItem.Items.Add(sub);
        }
        return colorItem;
    }

    private static MenuItem MenuItemSimple(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Пункт «Изменить» с иконкой карандаша.
    private static MenuItem MenuItemEdit(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.EditHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Пункт «Дублировать» с иконкой копирования.
    private static MenuItem MenuItemDuplicate(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.DuplicateHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Красный пункт «Удалить» с иконкой корзины.
    private static MenuItem MenuItemDelete(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.DeleteHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ChangeColor(int dbId, int collectionId, string hex)
    {
        var repo = RepoFor(dbId);
        if (repo is null) return;
        var col = repo.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        if (col is null) return;

        col.Color = hex;
        repo.UpdateCollection(col);
        Refresh();
        CalendarsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── CRUD ────────────────────────────────────────────────────────────────────

    private Window? OwnerWindow => VisualRoot as Window;

    private async Task CreateItem(string presetType, int dbId, int? parentId)
    {
        if (OwnerWindow is null || RepoFor(dbId) is null) return;

        // Тултип «Создать» кнопки «+» не должен висеть и мерцать поверх модального
        // окна: закрываем его и отключаем сервис тултипа на кнопке на время диалога
        // (при открытой модалке окно-владелец «полуактивно», и тултип переоткрывается
        // от событий курсора над диалогом — отсюда мигание).
        ToolTip.SetIsOpen(CreateBtn, false);
        ToolTip.SetServiceEnabled(CreateBtn, false);
        CreateItemResult? res;
        try
        {
            var dlg = new CreateItemDialog(DbOptions(), dbId, presetType, parentId);
            res = await dlg.ShowDialog<CreateItemResult?>(OwnerWindow);
        }
        finally
        {
            ToolTip.SetServiceEnabled(CreateBtn, true);
        }
        if (res is null) return;

        // Целевая БД — выбранная в диалоге (может отличаться от исходной).
        var repo = RepoFor(res.DbId);
        if (repo is null) return;

        if (res.Type == "group")
        {
            var pos = repo.GetAllGroups().Select(g => g.Position).DefaultIfEmpty(0).Max() + 1000;
            var group = repo.AddGroup(new DbGroup { Name = res.Name, ParentId = res.ParentId, Position = pos });
            ReloadAndNotify();
            if (group.Id.HasValue) ExpandGroup(res.DbId, group.Id.Value);
        }
        else
        {
            var pos = repo.GetAllCollections().Select(c => c.Position).DefaultIfEmpty(0).Max() + 1000;
            var col = repo.AddCollection(new DbCollection { Name = res.Name, GroupId = res.ParentId, Position = pos });
            if (col.Id.HasValue)
                // Title задаём реальным текстом «Без названия» (как кнопка «+»),
                // а не оставляем пустым с одним плейсхолдером в ячейке.
                repo.AddRecord(new DbRecord { CollectionId = col.Id.Value, Title = "Без названия" });
            ReloadAndNotify();
            if (col.Id.HasValue && _mode == SidebarMode.Constructor) OpenCollection(res.DbId, col.Id.Value);
        }
    }

    // Импорт таблицы в новую коллекцию. Окно собирает заявку (ImportRequest), затем здесь
    // показываем окно загрузки с процентами и выполняем чтение+запись в фоне. При ошибке —
    // снова открываем окно с теми же значениями и текстом ошибки; при отмене — выходим.
    private async Task OpenImportExport()
    {
        if (OwnerWindow is null || RepoFor(DefaultDbId) is null) return;

        ImportDraft? prefill = null;
        string?      error   = null;

        while (true)
        {
            // Тултип кнопки не должен мерцать поверх модального окна (как в CreateItem).
            ToolTip.SetIsOpen(ImportExportBtn, false);
            ToolTip.SetServiceEnabled(ImportExportBtn, false);
            ImportRequest? req;
            try
            {
                var dlg = new ImportExportDialog(DbOptions(), DefaultDbId, prefill, error);
                req = await dlg.ShowDialog<ImportRequest?>(OwnerWindow);
            }
            finally
            {
                ToolTip.SetServiceEnabled(ImportExportBtn, true);
            }

            if (req is null) return;   // отмена

            var repo = RepoFor(req.DbId);
            if (repo is null) return;

            // Окно загрузки: показываем модально (не ждём — закроем сами после импорта).
            var progress = new ProgressDialog();
            IProgress<double> readP  = new Progress<double>(p => progress.SetProgress(p * 0.5,       "Чтение файла…"));
            IProgress<double> writeP = new Progress<double>(p => progress.SetProgress(0.5 + p * 0.5, "Сохранение записей…"));
            _ = progress.ShowDialog(OwnerWindow);

            try
            {
                var colId = await Task.Run(() =>
                {
                    var data = ExcelImporter.Parse(req.Spec, readP);
                    return repo.ImportExcelCollection(req.Name, req.ParentId, data, writeP);
                });

                progress.Done();
                ReloadAndNotify();
                if (_mode == SidebarMode.Constructor) OpenCollection(req.DbId, colId);
                return;
            }
            catch (ExcelImportException ex)
            {
                progress.Done();
                prefill = req.Draft;
                error   = ex.Message;
            }
            catch
            {
                progress.Done();
                prefill = req.Draft;
                error   = "Не удалось импортировать данные.";
            }
        }
    }

    private void ExpandGroup(int dbId, int groupId)
    {
        var node = FindNode(_roots, "group", dbId, groupId);
        if (node is null) return;
        ExpandAncestors(_roots, node);
        node.IsExpanded = true;
        RebuildRows();
    }

    private void OpenCollection(int dbId, int collectionId)
    {
        SelectCollection(dbId, collectionId);
        CollectionSelected?.Invoke(this, new EntityRef(dbId, collectionId));
    }

    private async Task EditGroup(int dbId, int groupId)
    {
        var repo = RepoFor(dbId);
        if (repo is null || OwnerWindow is null) return;

        var group = repo.GetAllGroups().FirstOrDefault(g => g.Id == groupId);
        if (group is null) return;

        var dlg = new GroupDialog(DbOptions(), dbId, editNode: group);
        var res = await dlg.ShowDialog<GroupDialogResult?>(OwnerWindow);
        if (res is null) return;

        // Смена БД → перенос группы со всей веткой в целевую БД (атомарный импорт + откат при сбое).
        if (res.DbId != dbId && RepoFor(res.DbId) is { } target)
        {
            try
            {
                var moved = ConstructorRepository.MoveGroup(repo, target, groupId, res.ParentId, res.Name);
                ReloadAndNotify();
                if (moved.Id.HasValue) ExpandGroup(res.DbId, moved.Id.Value);
            }
            catch
            {
                ReloadAndNotify();
                if (OwnerWindow is not null)
                    await new ConfirmDialog("Не удалось перенести группу в другую базу. Данные остались в исходной базе.")
                        .ShowDialog<bool>(OwnerWindow);
            }
            return;
        }

        group.Name     = res.Name;
        group.ParentId = res.ParentId;
        repo.UpdateGroup(group);
        ReloadAndNotify();
    }

    private async Task EditCollection(int dbId, int collectionId)
    {
        var repo = RepoFor(dbId);
        if (repo is null || OwnerWindow is null) return;

        var col = repo.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        if (col is null) return;

        var dlg = new CollectionDialog(DbOptions(), dbId, editNode: col);
        var res = await dlg.ShowDialog<CollectionDialogResult?>(OwnerWindow);
        if (res is null) return;

        // Смена БД → перенос коллекции в целевую БД (атомарный импорт + откат копии при сбое).
        if (res.DbId != dbId && RepoFor(res.DbId) is { } target)
        {
            try
            {
                var moved = ConstructorRepository.MoveCollection(repo, target, collectionId, res.GroupId, res.Name);
                ReloadAndNotify();
                if (_mode == SidebarMode.Constructor && moved.Id.HasValue)
                    OpenCollection(res.DbId, moved.Id.Value);
            }
            catch
            {
                ReloadAndNotify();   // вернёт исходное состояние в дерево
                if (OwnerWindow is not null)
                    await new ConfirmDialog("Не удалось перенести коллекцию в другую базу. Данные остались в исходной базе.")
                        .ShowDialog<bool>(OwnerWindow);
            }
            return;
        }

        col.Name    = res.Name;
        col.GroupId = res.GroupId;
        repo.UpdateCollection(col);
        ReloadAndNotify();
    }

    private void DuplicateGroup(int dbId, int groupId)
    {
        var repo = RepoFor(dbId);
        if (repo is null) return;
        var copy = repo.DuplicateGroup(groupId);
        ReloadAndNotify();
        if (copy.Id.HasValue) ExpandGroup(dbId, copy.Id.Value);
    }

    private void DuplicateCollection(int dbId, int collectionId)
    {
        var repo = RepoFor(dbId);
        if (repo is null) return;
        var copy = repo.DuplicateCollection(collectionId);
        ReloadAndNotify();
        if (copy.Id.HasValue && _mode == SidebarMode.Constructor) OpenCollection(dbId, copy.Id.Value);
    }

    // Удаление через контекстное меню: если клик по узлу из мультивыделения — удаляем
    // весь набор; иначе только этот узел.
    private Task DeleteSelectionOrNode(SidebarNode node)
    {
        List<SidebarNode> targets = _selectedNodes.Contains(node) ? _selectedNodes.ToList() : [node];
        return DeleteNodes(targets);
    }

    private async Task DeleteNodes(IReadOnlyCollection<SidebarNode> nodes)
    {
        if (OwnerWindow is null) return;

        // Корни-БД не удаляются отсюда (управление БД — в настройках).
        var groups = nodes.Where(n => n.IsGroup  && n.Id.HasValue).ToList();
        var colls  = nodes.Where(n => !n.IsGroup && !n.IsDatabase && n.Id.HasValue).ToList();
        if (groups.Count == 0 && colls.Count == 0) return;

        var dlg = new ConfirmDialog(BuildDeleteMessage(groups.Count, colls.Count));
        if (await dlg.ShowDialog<bool>(OwnerWindow) != true) return;

        // Сначала группы (каскадно удаляют вложенное), затем коллекции; каждая — через
        // репозиторий своей БД. Коллекции, уже удалённые каскадом, — безопасный no-op.
        foreach (var g in groups) RepoOf(g)?.DeleteGroup(g.Id!.Value);
        foreach (var c in colls)  RepoOf(c)?.DeleteCollection(c.Id!.Value);

        _selectedNodes.Clear();
        ReloadAndNotify();   // RequestRefresh обновит и конструктор, и календарь
        var firstColl = colls.FirstOrDefault();
        if (firstColl is not null) CollectionDeleted?.Invoke(this, firstColl.Id!.Value);
    }

    // Текст подтверждения с подсчётом: только группы / только коллекции / и то и другое.
    private static string BuildDeleteMessage(int groups, int colls)
    {
        var parts = new List<string>();
        if (groups > 0) parts.Add($"{groups} {GroupsPlural(groups)}");
        if (colls  > 0) parts.Add($"{colls} {CollectionsPlural(colls)}");
        var warn = groups > 0
            ? "Вложенные группы и коллекции также будут удалены без возможности восстановления."
            : "Данные будут удалены без возможности восстановления.";
        return $"Вы удаляете {string.Join(" и ", parts)}. {warn} Подтвердить?";
    }

    // Винительный падеж: 1 группу, 2–4 группы, 5+ групп (11–14 → групп).
    private static string GroupsPlural(int n)
    {
        var m100 = n % 100; var m10 = n % 10;
        if (m100 is >= 11 and <= 14) return "групп";
        return m10 switch { 1 => "группу", >= 2 and <= 4 => "группы", _ => "групп" };
    }

    // 1 коллекцию, 2–4 коллекции, 5+ коллекций (11–14 → коллекций).
    private static string CollectionsPlural(int n)
    {
        var m100 = n % 100; var m10 = n % 10;
        if (m100 is >= 11 and <= 14) return "коллекций";
        return m10 switch { 1 => "коллекцию", >= 2 and <= 4 => "коллекции", _ => "коллекций" };
    }

    private void ReloadAndNotify()
    {
        Refresh();
        RequestRefresh?.Invoke(this, EventArgs.Empty);
        if (_mode == SidebarMode.Calendar)
        {
            CalendarsChanged?.Invoke(this, EventArgs.Empty);
            ActiveIdsChanged?.Invoke(this, GetActiveIds());
        }
    }

    private enum SortState { None, Asc, Desc }
}

// ── Узел дерева ────────────────────────────────────────────────────────────────
public class SidebarNode
{
    public string NodeType { get; init; } = "";  // "database" | "group" | "collection"
    public int     DbId     { get; init; }        // владелец-БД (для мульти-маунта)
    public int?    Id       { get; init; }
    public string  Name     { get; init; } = "";
    public string  Color    { get; init; } = "#007AFF";
    public bool    IsExpanded { get; set; }
    public bool    IsGroup    => NodeType == "group";
    public bool    IsDatabase => NodeType == "database";

    public ObservableCollection<SidebarNode> Children { get; } = [];
}

// ── Плоская строка для отрисовки ──────────────────────────────────────────────
public partial class SidebarRow : ObservableObject
{
    private const double IndentStep = 22;

    private static readonly IBrush Accent      = new SolidColorBrush(Color.Parse("#007AFF"));
    private static readonly IBrush AccentLight = new SolidColorBrush(Color.Parse("#E5F0FF"));
    private static readonly IBrush Hover       = new SolidColorBrush(Color.Parse("#F2F2F7"));
    private static readonly IBrush Secondary   = new SolidColorBrush(Color.Parse("#8E8E93"));
    private static readonly IBrush Primary     = new SolidColorBrush(Color.Parse("#1C1C1E"));

    // Lucide: folder / grid-2x2-check / database
    private const string FolderGeo =
        "M20 20 A2 2 0 0 0 22 18 V8 A2 2 0 0 0 20 6 H12.1 A2 2 0 0 1 10.41 5.1 L9.6 3.9 A2 2 0 0 0 7.93 3 H4 A2 2 0 0 0 2 5 V18 A2 2 0 0 0 4 20 Z";
    private const string CollectionGeo =
        "M12 3 V20 A1 1 0 0 1 11 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 H19 A2 2 0 0 1 21 5 V11 A1 1 0 0 1 20 12 H3 M16 19 L18 21 L22 17";
    private const string DatabaseGeo =
        "M3 5 A9 3 0 1 0 21 5 A9 3 0 1 0 3 5 Z M3 5 V19 A9 3 0 0 0 21 19 V5 M3 12 A9 3 0 0 0 21 12";
    private const string ChevronRightGeo = "M9 18 L15 12 L9 6";
    private const string ChevronDownGeo  = "M6 9 L12 15 L18 9";

    // «Без цвета»: дефолтный синий → серый чекбокс.
    private const string DefaultColor = "#007AFF";
    private const string GreyColor    = "#8E8E93";

    public SidebarNode Node { get; }
    public int         Depth { get; }
    public SidebarMode Mode { get; }
    public string      Name => Node.Name;

    [ObservableProperty] private bool _hovered;
    [ObservableProperty] private bool _checked;
    [ObservableProperty] private bool _selected;

    public SidebarRow(SidebarNode node, int depth, SidebarMode mode, bool isChecked, bool selected)
    {
        Node     = node;
        Depth    = depth;
        Mode     = mode;
        _checked = isChecked;
        _selected = selected;
    }

    public void SetHovered(bool v) => Hovered = v;

    // Раскрываемые узлы — группы и корни-БД.
    private bool Expandable => Node.IsGroup || Node.IsDatabase;

    public Thickness IndentMargin => new(Depth * IndentStep, 0, 0, 0);
    public double    ChevronSlot  => Expandable ? IndentStep : 0;

    // Чекбокс есть только в режиме календаря и только у коллекций.
    public double CheckboxSlot => Mode == SidebarMode.Calendar && !Node.IsGroup && !Node.IsDatabase ? IndentStep : 0;
    public bool   ShowCheckbox => Mode == SidebarMode.Calendar && !Node.IsGroup && !Node.IsDatabase;

    public bool   ShowChevron     => Expandable;
    public string ChevronGeometry => Node.IsExpanded ? ChevronDownGeo : ChevronRightGeo;
    public string IconGeometry    => Node.IsDatabase ? DatabaseGeo : Node.IsGroup ? FolderGeo : CollectionGeo;

    private SolidColorBrush ColorBrush
    {
        get
        {
            var c = string.IsNullOrWhiteSpace(Node.Color) || Node.Color == DefaultColor
                ? GreyColor
                : Node.Color;
            return new SolidColorBrush(Color.Parse(c));
        }
    }

    public IBrush CheckFill   => Checked ? ColorBrush : Brushes.Transparent;
    public IBrush CheckBorder => ColorBrush;

    public IBrush IconBrush => Selected ? Accent : Secondary;
    public IBrush NameBrush => Selected ? Accent : Primary;

    public IBrush RowBackground =>
        Selected ? AccentLight :
        Hovered  ? Hover       : Brushes.Transparent;

    partial void OnHoveredChanged(bool value)  => OnPropertyChanged(nameof(RowBackground));
    partial void OnCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckFill));
        OnPropertyChanged(nameof(CheckBorder));
    }
    partial void OnSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(NameBrush));
    }
}
