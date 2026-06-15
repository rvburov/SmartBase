using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DataBase.Controls.Dialogs;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Components;

public enum SidebarMode { Calendar, Constructor }

public partial class Sidebar : UserControl
{
    // Календарь
    public event EventHandler<HashSet<int>>? ActiveIdsChanged;
    public event EventHandler?               CalendarsChanged;

    // Конструктор
    public event EventHandler<int>? CollectionSelected;
    public event EventHandler<int>? CollectionDeleted;
    public event EventHandler?      RequestRefresh;

    private ConstructorRepository? _repository;
    private SidebarMode _mode = SidebarMode.Calendar;
    private SortState _sortState = SortState.None;
    private bool _expandedAll;

    // Логика как в PyQt: активны все коллекции, кроме явно отключённых пользователем.
    private readonly HashSet<int> _uncheckedIds = [];

    private List<SidebarNode> _roots = [];
    private readonly ObservableCollection<SidebarRow> _rows = [];
    private SidebarNode? _selected;

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

        CreateBtn.Click            += (_, _) => _ = CreateItem("collection", parentId: null);
        ExpandBtn.IsCheckedChanged += (_, _) => ToggleExpandAll(ExpandBtn.IsChecked == true);
        SortBtn.Click              += (_, _) => CycleSort();
        ToggleAllBtn.Click         += (_, _) => ToggleAllChecked();
    }

    public void Initialize(ConstructorRepository repository, SidebarMode mode)
    {
        _repository = repository;
        _mode = mode;
        ToggleAllBtn.IsVisible = mode == SidebarMode.Calendar;
        Refresh();
    }

    public void Refresh()
    {
        if (_repository is null) return;

        var expandedIds = GetExpandedGroupIds();
        var selectedId  = _selected?.Id;

        _roots = BuildTree();
        RestoreExpanded(_roots, expandedIds);

        _selected = _mode == SidebarMode.Constructor && selectedId.HasValue
            ? FindNode(_roots, "collection", selectedId.Value)
            : null;
        if (_selected is not null) ExpandAncestors(_roots, _selected);

        RebuildRows();
        UpdateToggleAllIcon();
    }

    public HashSet<int> GetActiveIds()
    {
        if (_repository is null) return [];
        var all = _repository.GetAllCollections()
            .Where(c => c.Id.HasValue).Select(c => c.Id!.Value).ToHashSet();
        all.ExceptWith(_uncheckedIds);
        return all;
    }

    public void SelectCollection(int collectionId)
    {
        if (_mode != SidebarMode.Constructor) return;
        var target = FindNode(_roots, "collection", collectionId);
        if (target is null) return;
        ExpandAncestors(_roots, target);
        _selected = target;
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
            _rows.Add(new SidebarRow(n, depth, _mode, IsChecked(n), n == _selected));
            if (n.IsGroup && n.IsExpanded)
                Flatten(n.Children, depth + 1);
        }
    }

    private bool IsChecked(SidebarNode n) =>
        !n.IsGroup && n.Id.HasValue && !_uncheckedIds.Contains(n.Id.Value);

    // ── Построение дерева ──────────────────────────────────────────────────────

    private List<SidebarNode> BuildTree()
    {
        if (_repository is null) return [];

        var groups      = _repository.GetAllGroups();
        var collections = _repository.GetAllCollections();
        var root        = new List<SidebarNode>();

        foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.Position))
            root.Add(BuildGroupNode(g, groups, collections));
        foreach (var c in collections.Where(c => c.GroupId is null).OrderBy(c => c.Position))
            root.Add(MakeCollectionNode(c));

        return SortNodes(root);
    }

    private SidebarNode BuildGroupNode(DbGroup group, List<DbGroup> allGroups, List<DbCollection> allCollections)
    {
        var node = new SidebarNode { NodeType = "group", Id = group.Id, Name = group.Name };

        var children = new List<SidebarNode>();
        foreach (var child in allGroups.Where(g => g.ParentId == group.Id).OrderBy(g => g.Position))
            children.Add(BuildGroupNode(child, allGroups, allCollections));
        foreach (var col in allCollections.Where(c => c.GroupId == group.Id).OrderBy(c => c.Position))
            children.Add(MakeCollectionNode(col));

        foreach (var c in SortNodes(children))
            node.Children.Add(c);

        return node;
    }

    private static SidebarNode MakeCollectionNode(DbCollection c) =>
        new() { NodeType = "collection", Id = c.Id, Name = c.Name, Color = c.Color };

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
            if (n.IsGroup) n.IsExpanded = expand;
            SetExpandedRecursive(n.Children, expand);
        }
    }

    private HashSet<int> GetExpandedGroupIds()
    {
        var ids = new HashSet<int>();
        Collect(_roots);
        return ids;

        void Collect(IEnumerable<SidebarNode> ns)
        {
            foreach (var n in ns)
            {
                if (n.IsGroup && n.IsExpanded && n.Id.HasValue) ids.Add(n.Id.Value);
                Collect(n.Children);
            }
        }
    }

    private void RestoreExpanded(IEnumerable<SidebarNode> nodes, HashSet<int> ids)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup && n.Id.HasValue)
                n.IsExpanded = _expandedAll || ids.Contains(n.Id.Value);
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

    private static SidebarNode? FindNode(IEnumerable<SidebarNode> nodes, string type, int id)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == type && node.Id == id) return node;
            var found = FindNode(node.Children, type, id);
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
        var id = node.Id.Value;

        if (_uncheckedIds.Contains(id)) _uncheckedIds.Remove(id);
        else                            _uncheckedIds.Add(id);

        RebuildRows();
        UpdateToggleAllIcon();
        ActiveIdsChanged?.Invoke(this, GetActiveIds());
    }

    private bool AllChecked()
    {
        if (_repository is null) return false;
        var all = _repository.GetAllCollections().Where(c => c.Id.HasValue).Select(c => c.Id!.Value);
        return all.All(id => !_uncheckedIds.Contains(id));
    }

    private void ToggleAllChecked()
    {
        if (_repository is null) return;
        var allIds = _repository.GetAllCollections()
            .Where(c => c.Id.HasValue).Select(c => c.Id!.Value).ToList();

        if (AllChecked())
            foreach (var id in allIds) _uncheckedIds.Add(id);
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

        if (node.IsGroup)
        {
            node.IsExpanded = !node.IsExpanded;
            RebuildRows();
            return;
        }

        if (_mode == SidebarMode.Calendar)
        {
            ToggleCollection(node);
        }
        else
        {
            _selected = node;
            RebuildRows();
            if (node.Id.HasValue) CollectionSelected?.Invoke(this, node.Id.Value);
        }
    }

    private void OnChevronTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarRow row } || !row.Node.IsGroup) return;
        row.Node.IsExpanded = !row.Node.IsExpanded;
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
        var id   = node.Id.Value;

        if (node.IsGroup)
        {
            menu.Items.Add(MenuItemSimple("Создать группу",    () => _ = CreateItem("group", parentId: id)));
            menu.Items.Add(MenuItemSimple("Создать коллекцию", () => _ = CreateItem("collection", parentId: id)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemEdit(() => _ = EditGroup(id)));
            menu.Items.Add(MenuItemDuplicate(() => DuplicateGroup(id)));
            menu.Items.Add(MenuItemDelete(() => _ = DeleteGroup(id)));
        }
        else
        {
            if (_mode == SidebarMode.Calendar)
            {
                menu.Items.Add(BuildColorMenu(id));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MenuItemEdit(() => _ = EditCollection(id)));
            menu.Items.Add(MenuItemDuplicate(() => DuplicateCollection(id)));
            menu.Items.Add(MenuItemDelete(() => _ = DeleteCollection(id)));
        }

        menu.Open(anchor);
        e.Handled = true;
    }

    private void OnEmptyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control anchor) return;

        var menu = new ContextMenu();
        menu.Items.Add(MenuItemSimple("Создать группу",    () => _ = CreateItem("group", parentId: null)));
        menu.Items.Add(MenuItemSimple("Создать коллекцию", () => _ = CreateItem("collection", parentId: null)));
        menu.Open(anchor);
        e.Handled = true;
    }

    private MenuItem BuildColorMenu(int collectionId)
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
        noColor.Click += (_, _) => ChangeColor(collectionId, "#007AFF");
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
            sub.Click += (_, _) => ChangeColor(collectionId, hex);
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

    private void ChangeColor(int collectionId, string hex)
    {
        if (_repository is null) return;
        var col = _repository.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        if (col is null) return;

        col.Color = hex;
        _repository.UpdateCollection(col);
        Refresh();
        CalendarsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── CRUD ────────────────────────────────────────────────────────────────────

    private Window? OwnerWindow => VisualRoot as Window;

    private async Task CreateItem(string presetType, int? parentId)
    {
        if (_repository is null || OwnerWindow is null) return;

        var dlg = new CreateItemDialog(_repository, presetType, parentId);
        var res = await dlg.ShowDialog<CreateItemResult?>(OwnerWindow);
        if (res is null) return;

        if (res.Type == "group")
        {
            var pos = _repository.GetAllGroups().Select(g => g.Position).DefaultIfEmpty(0).Max() + 1000;
            var group = _repository.AddGroup(new DbGroup { Name = res.Name, ParentId = res.ParentId, Position = pos });
            ReloadAndNotify();
            if (group.Id.HasValue) ExpandGroup(group.Id.Value);
        }
        else
        {
            var pos = _repository.GetAllCollections().Select(c => c.Position).DefaultIfEmpty(0).Max() + 1000;
            var col = _repository.AddCollection(new DbCollection { Name = res.Name, GroupId = res.ParentId, Position = pos });
            if (col.Id.HasValue)
                _repository.AddRecord(new DbRecord { CollectionId = col.Id.Value });
            ReloadAndNotify();
            if (col.Id.HasValue && _mode == SidebarMode.Constructor) OpenCollection(col.Id.Value);
        }
    }

    private void ExpandGroup(int groupId)
    {
        var node = FindNode(_roots, "group", groupId);
        if (node is null) return;
        ExpandAncestors(_roots, node);
        node.IsExpanded = true;
        RebuildRows();
    }

    private void OpenCollection(int collectionId)
    {
        SelectCollection(collectionId);
        CollectionSelected?.Invoke(this, collectionId);
    }

    private async Task EditGroup(int groupId)
    {
        if (_repository is null || OwnerWindow is null) return;

        var group = _repository.GetAllGroups().FirstOrDefault(g => g.Id == groupId);
        if (group is null) return;

        var dlg = new GroupDialog(_repository, editNode: group);
        var res = await dlg.ShowDialog<GroupDialogResult?>(OwnerWindow);
        if (res is null) return;

        group.Name     = res.Name;
        group.ParentId = res.ParentId;
        _repository.UpdateGroup(group);
        ReloadAndNotify();
    }

    private async Task EditCollection(int collectionId)
    {
        if (_repository is null || OwnerWindow is null) return;

        var col = _repository.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        if (col is null) return;

        var dlg = new CollectionDialog(_repository, editNode: col);
        var res = await dlg.ShowDialog<CollectionDialogResult?>(OwnerWindow);
        if (res is null) return;

        col.Name    = res.Name;
        col.GroupId = res.GroupId;
        _repository.UpdateCollection(col);
        ReloadAndNotify();
    }

    private void DuplicateGroup(int groupId)
    {
        if (_repository is null) return;
        var copy = _repository.DuplicateGroup(groupId);
        ReloadAndNotify();
        if (copy.Id.HasValue) ExpandGroup(copy.Id.Value);
    }

    private void DuplicateCollection(int collectionId)
    {
        if (_repository is null) return;
        var copy = _repository.DuplicateCollection(collectionId);
        ReloadAndNotify();
        if (copy.Id.HasValue && _mode == SidebarMode.Constructor) OpenCollection(copy.Id.Value);
    }

    private async Task DeleteGroup(int groupId)
    {
        if (_repository is null || OwnerWindow is null) return;

        var dlg = new ConfirmDialog(
            "Удалить группу? Все вложенные группы и коллекции будут удалены без возможности восстановления.");
        if (await dlg.ShowDialog<bool>(OwnerWindow) != true) return;

        _repository.DeleteGroup(groupId);
        ReloadAndNotify();
    }

    private async Task DeleteCollection(int collectionId)
    {
        if (_repository is null || OwnerWindow is null) return;

        var dlg = new ConfirmDialog(
            "Удалить коллекцию? Все данные будут удалены без возможности восстановления.");
        if (await dlg.ShowDialog<bool>(OwnerWindow) != true) return;

        _repository.DeleteCollection(collectionId);
        ReloadAndNotify();
        CollectionDeleted?.Invoke(this, collectionId);
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
    public string NodeType { get; init; } = "";  // "group" | "collection"
    public int?    Id       { get; init; }
    public string  Name     { get; init; } = "";
    public string  Color    { get; init; } = "#007AFF";
    public bool    IsExpanded { get; set; }
    public bool    IsGroup => NodeType == "group";

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

    // Lucide: folder / grid-2x2-check
    private const string FolderGeo =
        "M20 20 A2 2 0 0 0 22 18 V8 A2 2 0 0 0 20 6 H12.1 A2 2 0 0 1 10.41 5.1 L9.6 3.9 A2 2 0 0 0 7.93 3 H4 A2 2 0 0 0 2 5 V18 A2 2 0 0 0 4 20 Z";
    private const string CollectionGeo =
        "M12 3 V20 A1 1 0 0 1 11 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 H19 A2 2 0 0 1 21 5 V11 A1 1 0 0 1 20 12 H3 M16 19 L18 21 L22 17";
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

    public Thickness IndentMargin => new(Depth * IndentStep, 0, 0, 0);
    public double    ChevronSlot  => Node.IsGroup ? IndentStep : 0;

    // Чекбокс есть только в режиме календаря и только у коллекций.
    public double CheckboxSlot => Mode == SidebarMode.Calendar && !Node.IsGroup ? IndentStep : 0;
    public bool   ShowCheckbox => Mode == SidebarMode.Calendar && !Node.IsGroup;

    public bool   ShowChevron     => Node.IsGroup;
    public string ChevronGeometry => Node.IsExpanded ? ChevronDownGeo : ChevronRightGeo;
    public string IconGeometry    => Node.IsGroup ? FolderGeo : CollectionGeo;

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
