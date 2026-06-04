using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Controls.Dialogs;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Components;

public partial class SidebarTree : UserControl
{
    public event EventHandler<int>? CollectionSelected;
    public event EventHandler<int>? CollectionDeleted;
    public event EventHandler?      RequestRefresh;

    private ConstructorRepository? _repository;
    private SortState _sortState = SortState.None;
    private bool _expandedAll;

    private List<SidebarTreeNode> _roots = [];
    private readonly ObservableCollection<FlatRow> _rows = [];
    private SidebarTreeNode? _selected;

    public SidebarTree()
    {
        InitializeComponent();

        RowsHost.ItemsSource = _rows;

        CreateBtn.Click += (_, _) => _ = CreateItem("collection", parentId: null);
        SortBtn.Click   += (_, _) => CycleSort();
        ExpandBtn.IsCheckedChanged += (_, _) => ToggleExpandAll(ExpandBtn.IsChecked == true);
    }

    public void Load(ConstructorRepository repository)
    {
        _repository = repository;
        Reload();
    }

    public void Reload()
    {
        if (_repository is null) return;

        var expandedIds = GetExpandedGroupIds();
        var selectedId  = _selected?.Id;

        _roots = BuildTree();
        RestoreExpanded(_roots, expandedIds);
        _selected = selectedId.HasValue ? FindNode(_roots, "collection", selectedId.Value) : null;
        if (_selected is not null) ExpandAncestors(_roots, _selected);

        RebuildRows();
    }

    public void SelectCollection(int collectionId)
    {
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

    private void Flatten(IEnumerable<SidebarTreeNode> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            _rows.Add(new FlatRow(n, depth, n == _selected));
            if (n.NodeType == "group" && n.IsExpanded)
                Flatten(n.Children, depth + 1);
        }
    }

    // ── Построение дерева ──────────────────────────────────────────────────────

    private List<SidebarTreeNode> BuildTree()
    {
        if (_repository is null) return [];

        var groups      = _repository.GetAllGroups();
        var collections = _repository.GetAllCollections();
        var root        = new List<SidebarTreeNode>();

        foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.Position))
            root.Add(BuildGroupNode(g, groups, collections));

        foreach (var c in collections.Where(c => c.GroupId is null).OrderBy(c => c.Position))
            root.Add(MakeCollectionNode(c));

        return SortNodes(root);
    }

    private SidebarTreeNode BuildGroupNode(DbGroup group, List<DbGroup> allGroups, List<DbCollection> allCollections)
    {
        var node = new SidebarTreeNode { NodeType = "group", Id = group.Id, Name = group.Name };

        var children = new List<SidebarTreeNode>();
        foreach (var child in allGroups.Where(g => g.ParentId == group.Id).OrderBy(g => g.Position))
            children.Add(BuildGroupNode(child, allGroups, allCollections));
        foreach (var col in allCollections.Where(c => c.GroupId == group.Id).OrderBy(c => c.Position))
            children.Add(MakeCollectionNode(col));

        foreach (var c in SortNodes(children))
            node.Children.Add(c);

        return node;
    }

    private static SidebarTreeNode MakeCollectionNode(DbCollection c) =>
        new() { NodeType = "collection", Id = c.Id, Name = c.Name };

    private List<SidebarTreeNode> SortNodes(List<SidebarTreeNode> nodes)
    {
        // Сначала группы, затем коллекции — как в файловом дереве
        var groups = nodes.Where(n => n.NodeType == "group").ToList();
        var colls  = nodes.Where(n => n.NodeType != "group").ToList();

        if (_sortState != SortState.None)
        {
            var desc = _sortState == SortState.Desc;
            groups = ApplyOrder(groups, desc);
            colls  = ApplyOrder(colls, desc);
        }

        return [.. groups, .. colls];
    }

    private static List<SidebarTreeNode> ApplyOrder(List<SidebarTreeNode> nodes, bool desc) =>
        desc
            ? nodes.OrderByDescending(n => NameKey(n.Name)).ToList()
            : nodes.OrderBy(n => NameKey(n.Name)).ToList();

    // Цифры идут раньше текста, затем регистронезависимо
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

    private static void SetExpandedRecursive(IEnumerable<SidebarTreeNode> nodes, bool expand)
    {
        foreach (var n in nodes)
        {
            if (n.NodeType == "group") n.IsExpanded = expand;
            SetExpandedRecursive(n.Children, expand);
        }
    }

    private HashSet<int> GetExpandedGroupIds()
    {
        var ids = new HashSet<int>();
        Collect(_roots);
        return ids;

        void Collect(IEnumerable<SidebarTreeNode> ns)
        {
            foreach (var n in ns)
            {
                if (n.NodeType == "group" && n.IsExpanded && n.Id.HasValue) ids.Add(n.Id.Value);
                Collect(n.Children);
            }
        }
    }

    private void RestoreExpanded(IEnumerable<SidebarTreeNode> nodes, HashSet<int> ids)
    {
        foreach (var n in nodes)
        {
            if (n.NodeType == "group" && n.Id.HasValue)
                n.IsExpanded = _expandedAll || ids.Contains(n.Id.Value);
            RestoreExpanded(n.Children, ids);
        }
    }

    private static bool ExpandAncestors(IEnumerable<SidebarTreeNode> nodes, SidebarTreeNode target)
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

    private static SidebarTreeNode? FindNode(IEnumerable<SidebarTreeNode> nodes, string type, int id)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == type && node.Id == id) return node;
            var found = FindNode(node.Children, type, id);
            if (found is not null) return found;
        }
        return null;
    }

    // ── Взаимодействие со строками ───────────────────────────────────────────────

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: FlatRow row }) return;
        var node = row.Node;

        if (node.NodeType == "group")
        {
            node.IsExpanded = !node.IsExpanded;
            RebuildRows();
        }
        else if (node.NodeType == "collection")
        {
            _selected = node;
            RebuildRows();
            if (node.Id.HasValue) CollectionSelected?.Invoke(this, node.Id.Value);
        }
    }

    private void OnChevronTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: FlatRow row }) return;
        row.Node.IsExpanded = !row.Node.IsExpanded;
        RebuildRows();
        e.Handled = true;
    }

    private void OnRowEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: FlatRow row }) row.SetHovered(true);
    }

    private void OnRowExit(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: FlatRow row }) row.SetHovered(false);
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: FlatRow row } anchor) return;
        OnRowContext(row.Node, anchor);
        e.Handled = true;
    }

    // Правый клик по пустому месту дерева → создание на корневом уровне
    private void OnEmptyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control anchor) return;

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem_("Создать группу",    () => _ = CreateItem("group", parentId: null)));
        menu.Items.Add(MenuItem_("Создать коллекцию", () => _ = CreateItem("collection", parentId: null)));
        menu.Open(anchor);
        e.Handled = true;
    }

    // ── Контекстное меню ──────────────────────────────────────────────────────────

    private void OnRowContext(SidebarTreeNode node, Control anchor)
    {
        var menu = new ContextMenu();

        if (node.NodeType == "group" && node.Id.HasValue)
        {
            var gid = node.Id.Value;
            menu.Items.Add(MenuItem_("Создать группу",    () => _ = CreateItem("group", parentId: gid)));
            menu.Items.Add(MenuItem_("Создать коллекцию", () => _ = CreateItem("collection", parentId: gid)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem_("Редактировать", () => _ = EditGroup(gid)));
            menu.Items.Add(MenuItem_("Удалить",       () => _ = DeleteGroup(gid)));
        }
        else if (node.NodeType == "collection" && node.Id.HasValue)
        {
            var cid = node.Id.Value;
            menu.Items.Add(MenuItem_("Редактировать", () => _ = EditCollection(cid)));
            menu.Items.Add(MenuItem_("Удалить",       () => _ = DeleteCollection(cid)));
        }

        menu.Open(anchor);
    }

    private static MenuItem MenuItem_(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // ── CRUD ────────────────────────────────────────────────────────────────────

    private Window? OwnerWindow => VisualRoot as Window;

    // Единый диалог создания: тип (коллекция/группа) выбирается внутри окна.
    private async Task CreateItem(string presetType, int? parentId)
    {
        if (_repository is null || OwnerWindow is null) return;

        var dlg = new CreateItemDialog(_repository, presetType, parentId);
        var res = await dlg.ShowDialog<CreateItemResult?>(OwnerWindow);
        if (res is null) return;

        if (res.Type == "group")
        {
            var pos = _repository.GetAllGroups().Select(g => g.Position).DefaultIfEmpty(0).Max() + 1000;
            _repository.AddGroup(new DbGroup { Name = res.Name, ParentId = res.ParentId, Position = pos });
        }
        else
        {
            var pos = _repository.GetAllCollections().Select(c => c.Position).DefaultIfEmpty(0).Max() + 1000;
            var col = _repository.AddCollection(new DbCollection { Name = res.Name, GroupId = res.ParentId, Position = pos });
            if (col.Id.HasValue)
                _repository.AddRecord(new DbRecord { CollectionId = col.Id.Value });
        }
        ReloadAndNotify();
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

    private async Task DeleteGroup(int groupId)
    {
        if (_repository is null || OwnerWindow is null) return;

        var dlg = new ConfirmDialog(
            "Удалить группу? Коллекции внутри будут перемещены в корневой уровень.");
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
        Reload();
        RequestRefresh?.Invoke(this, EventArgs.Empty);
    }

    private void CycleSort()
    {
        _sortState = _sortState switch
        {
            SortState.None => SortState.Asc,
            SortState.Asc  => SortState.Desc,
            SortState.Desc => SortState.None,
            _              => SortState.None,
        };
        ToolTip.SetTip(SortBtn, _sortState switch
        {
            SortState.Asc  => "А → Я  (нажмите для Я → А)",
            SortState.Desc => "Я → А  (нажмите для сброса)",
            _              => "Сортировка по имени",
        });
        Reload();
    }

    private enum SortState { None, Asc, Desc }
}

// ── Узел дерева (доменная модель) ────────────────────────────────────────────────
public class SidebarTreeNode
{
    public string NodeType { get; init; } = "";  // "group" | "collection"
    public int?   Id       { get; init; }
    public string Name     { get; init; } = "";
    public bool   IsExpanded { get; set; }
    public bool   IsGroup => NodeType == "group";

    public ObservableCollection<SidebarTreeNode> Children { get; } = [];
}

// ── Плоская строка для отрисовки ──────────────────────────────────────────────────
public partial class FlatRow : ObservableObject
{
    private const double IndentStep = 22;  // ширина зоны одного уровня

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
    // Lucide chevron-right / chevron-down
    private const string ChevronRightGeo = "M9 18 L15 12 L9 6";
    private const string ChevronDownGeo  = "M6 9 L12 15 L18 9";

    public SidebarTreeNode Node { get; }

    [ObservableProperty] private bool _hovered;

    public FlatRow(SidebarTreeNode node, int depth, bool selected)
    {
        Node      = node;
        Depth     = depth;
        _selected = selected;
    }

    public int    Depth { get; }
    public string Name  => Node.Name;

    [ObservableProperty] private bool _selected;

    public void SetHovered(bool v) => Hovered = v;

    // Отступ уровня. Слот стрелки фикс (22px у групп, 0 у коллекций) → иконка
    // коллекции встаёт на место стрелки своего уровня (как в PyQt):
    //   коллекция корня — под стрелкой; вложенная — под иконкой папки родителя.
    public Thickness IndentMargin => new(Depth * IndentStep, 0, 0, 0);
    public double    ChevronSlot  => Node.IsGroup ? IndentStep : 0;

    public bool   ShowChevron     => Node.IsGroup;
    public string ChevronGeometry => Node.IsExpanded ? ChevronDownGeo : ChevronRightGeo;
    public string IconGeometry    => Node.IsGroup ? FolderGeo : CollectionGeo;

    public IBrush IconBrush => Selected ? Accent : Secondary;
    public IBrush NameBrush => Selected ? Accent : Primary;

    public IBrush RowBackground => Selected ? AccentLight : Hovered ? Hover : Brushes.Transparent;

    partial void OnHoveredChanged(bool value)  => OnPropertyChanged(nameof(RowBackground));
    partial void OnSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(NameBrush));
    }
}
