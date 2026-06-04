using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Components;

public partial class Sidebar : UserControl
{
    public event EventHandler<HashSet<int>>? ActiveIdsChanged;
    public event EventHandler?               CalendarsChanged;

    private ConstructorRepository? _constructor;
    private SortState _sortState = SortState.None;
    private bool _expandedAll;

    // Логика как в PyQt: активны все коллекции, кроме явно отключённых пользователем.
    private readonly HashSet<int> _uncheckedIds = [];

    private List<CalNode> _roots = [];
    private readonly ObservableCollection<CalRow> _rows = [];

    // Синий #007AFF трактуется как «без цвета» (серый), поэтому его нет в списке —
    // вместо него отдельный пункт «Без цвета».
    private static readonly (string Hex, string Name)[] PredefinedColors =
    [
        ("#34C759", "Зеленый"),    ("#FF3B30", "Красный"),
        ("#FF9500", "Оранжевый"), ("#FF2D55", "Розовый"), ("#AF52DE", "Фиолетовый"),
        ("#FFCC00", "Желтый"), ("#5E5CE6", "Индиго"),     ("#64D2FF", "Голубой"),
        ("#FF6B6B", "Коралловый"),
    ];

    public Sidebar()
    {
        InitializeComponent();

        RowsHost.ItemsSource = _rows;

        ExpandBtn.IsCheckedChanged += (_, _) => ToggleExpandAll(ExpandBtn.IsChecked == true);
        SortBtn.Click      += (_, _) => CycleSort();
        ToggleAllBtn.Click += (_, _) => ToggleAllChecked();
    }

    public Sidebar(ConstructorRepository constructor) : this()
    {
        _constructor = constructor;
        Refresh();
    }

    public void Initialize(ConstructorRepository constructor) => _constructor = constructor;

    public void Refresh()
    {
        if (_constructor is null) return;

        var expandedIds = GetExpandedGroupIds();

        _roots = BuildTree();
        RestoreExpanded(_roots, expandedIds);
        RebuildRows();
        UpdateToggleAllIcon();
    }

    public HashSet<int> GetActiveIds()
    {
        if (_constructor is null) return [];
        var all = _constructor.GetAllCollections()
            .Where(c => c.Id.HasValue).Select(c => c.Id!.Value).ToHashSet();
        all.ExceptWith(_uncheckedIds);
        return all;
    }

    // ── Плоский список видимых строк ─────────────────────────────────────────────

    private void RebuildRows()
    {
        _rows.Clear();
        Flatten(_roots, 0);
    }

    private void Flatten(IEnumerable<CalNode> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            _rows.Add(new CalRow(n, depth, IsChecked(n)));
            if (n.IsGroup && n.IsExpanded)
                Flatten(n.Children, depth + 1);
        }
    }

    private bool IsChecked(CalNode n) =>
        n.IsGroup ? false : n.Id.HasValue && !_uncheckedIds.Contains(n.Id.Value);

    // ── Построение дерева (группы + коллекции) ───────────────────────────────────

    private List<CalNode> BuildTree()
    {
        if (_constructor is null) return [];

        var groups      = _constructor.GetAllGroups();
        var collections = _constructor.GetAllCollections();
        var root        = new List<CalNode>();

        foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.Position))
            root.Add(BuildGroupNode(g, groups, collections));
        foreach (var c in collections.Where(c => c.GroupId is null).OrderBy(c => c.Position))
            root.Add(MakeCollectionNode(c));

        return SortNodes(root);
    }

    private CalNode BuildGroupNode(DbGroup group, List<DbGroup> allGroups, List<DbCollection> allCollections)
    {
        var node = new CalNode { NodeType = "group", Id = group.Id, Name = group.Name };

        var children = new List<CalNode>();
        foreach (var child in allGroups.Where(g => g.ParentId == group.Id).OrderBy(g => g.Position))
            children.Add(BuildGroupNode(child, allGroups, allCollections));
        foreach (var col in allCollections.Where(c => c.GroupId == group.Id).OrderBy(c => c.Position))
            children.Add(MakeCollectionNode(col));

        foreach (var c in SortNodes(children))
            node.Children.Add(c);

        return node;
    }

    private static CalNode MakeCollectionNode(DbCollection c) =>
        new() { NodeType = "collection", Id = c.Id, Name = c.Name, Color = c.Color };

    private List<CalNode> SortNodes(List<CalNode> nodes)
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

    private static List<CalNode> ApplyOrder(List<CalNode> nodes, bool desc) =>
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

    private static void SetExpandedRecursive(IEnumerable<CalNode> nodes, bool expand)
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

        void Collect(IEnumerable<CalNode> ns)
        {
            foreach (var n in ns)
            {
                if (n.IsGroup && n.IsExpanded && n.Id.HasValue) ids.Add(n.Id.Value);
                Collect(n.Children);
            }
        }
    }

    private void RestoreExpanded(IEnumerable<CalNode> nodes, HashSet<int> ids)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup && n.Id.HasValue)
                n.IsExpanded = _expandedAll || ids.Contains(n.Id.Value);
            RestoreExpanded(n.Children, ids);
        }
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

    // ── Чекбоксы видимости ────────────────────────────────────────────────────────

    private void ToggleCollection(CalNode node)
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
        if (_constructor is null) return false;
        var all = _constructor.GetAllCollections().Where(c => c.Id.HasValue).Select(c => c.Id!.Value);
        return all.All(id => !_uncheckedIds.Contains(id));
    }

    private void ToggleAllChecked()
    {
        if (_constructor is null) return;
        var allIds = _constructor.GetAllCollections()
            .Where(c => c.Id.HasValue).Select(c => c.Id!.Value).ToList();

        if (AllChecked())
            foreach (var id in allIds) _uncheckedIds.Add(id);   // выключить все
        else
            _uncheckedIds.Clear();                              // включить все

        RebuildRows();
        UpdateToggleAllIcon();
        ActiveIdsChanged?.Invoke(this, GetActiveIds());
    }

    private void UpdateToggleAllIcon()
    {
        // Иконка всегда серая; синий — только при наведении (через стиль navbtn)
        var on = AllChecked();
        ToolTip.SetTip(ToggleAllBtn, on ? "Выключить все" : "Включить все");
    }

    // ── Взаимодействие со строками ───────────────────────────────────────────────

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CalRow row }) return;
        if (row.Node.IsGroup)
        {
            row.Node.IsExpanded = !row.Node.IsExpanded;
            RebuildRows();
        }
        else
        {
            ToggleCollection(row.Node);
        }
    }

    private void OnChevronTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CalRow row } || !row.Node.IsGroup) return;
        row.Node.IsExpanded = !row.Node.IsExpanded;
        RebuildRows();
        e.Handled = true;
    }

    private void OnCheckboxTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CalRow row } || row.Node.IsGroup) return;
        ToggleCollection(row.Node);
        e.Handled = true;
    }

    private void OnRowEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: CalRow row }) row.SetHovered(true);
    }

    private void OnRowExit(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: CalRow row }) row.SetHovered(false);
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: CalRow row } anchor) return;
        if (row.Node.IsGroup || !row.Node.Id.HasValue) return;

        var nodeId = row.Node.Id.Value;
        var menu = new ContextMenu();
        var colorItem = new MenuItem { Header = "Изменить цвет" };

        // «Без цвета» → серый (дефолтный)
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
        noColor.Click += (_, _) => ChangeColor(nodeId, "#007AFF");  // дефолт = серый
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
            sub.Click += (_, _) => ChangeColor(nodeId, hex);
            colorItem.Items.Add(sub);
        }
        menu.Items.Add(colorItem);
        menu.Open(anchor);
        e.Handled = true;
    }

    private void ChangeColor(int collectionId, string hex)
    {
        if (_constructor is null) return;
        var col = _constructor.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        if (col is null) return;

        col.Color = hex;
        _constructor.UpdateCollection(col);
        Refresh();
        CalendarsChanged?.Invoke(this, EventArgs.Empty);
    }

    private enum SortState { None, Asc, Desc }
}

// ── Узел дерева ────────────────────────────────────────────────────────────────
public class CalNode
{
    public string NodeType { get; init; } = "";  // "group" | "collection"
    public int?    Id       { get; init; }
    public string  Name     { get; init; } = "";
    public string  Color    { get; init; } = "#007AFF";
    public bool    IsExpanded { get; set; }
    public bool    IsGroup => NodeType == "group";

    public ObservableCollection<CalNode> Children { get; } = [];
}

// ── Плоская строка для отрисовки ──────────────────────────────────────────────────
public partial class CalRow : ObservableObject
{
    private const double IndentStep = 22;

    private static readonly IBrush AccentLight = new SolidColorBrush(Color.Parse("#E5F0FF"));
    private static readonly IBrush Hover       = new SolidColorBrush(Color.Parse("#F2F2F7"));

    private const string ChevronRightGeo = "M9 18 L15 12 L9 6";
    private const string ChevronDownGeo  = "M6 9 L12 15 L18 9";

    public CalNode Node { get; }
    public int     Depth { get; }
    public string  Name  => Node.Name;

    [ObservableProperty] private bool _hovered;
    [ObservableProperty] private bool _checked;

    public CalRow(CalNode node, int depth, bool isChecked)
    {
        Node     = node;
        Depth    = depth;
        _checked = isChecked;
    }

    public void SetHovered(bool v) => Hovered = v;

    public Thickness IndentMargin => new(Depth * IndentStep, 0, 0, 0);
    public double    ChevronSlot  => Node.IsGroup ? IndentStep : 0;
    // Зона чекбокса есть только у коллекций; у групп схлопывается → текст вплотную к стрелке
    public double    CheckboxSlot => Node.IsGroup ? 0 : IndentStep;

    public bool   ShowChevron     => Node.IsGroup;
    public bool   ShowCheckbox    => !Node.IsGroup;
    public string ChevronGeometry => Node.IsExpanded ? ChevronDownGeo : ChevronRightGeo;

    // Дефолтный синий трактуем как «без цвета» → серый чекбокс.
    // Цвет появляется только если выбран явно через контекстное меню.
    private const string DefaultColor = "#007AFF";
    private const string GreyColor    = "#8E8E93";

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

    // Checked → залит цветом; Unchecked → прозрачный с цветным контуром
    public IBrush CheckFill   => Checked ? ColorBrush : Brushes.Transparent;
    public IBrush CheckBorder => ColorBrush;

    public IBrush RowBackground => Hovered ? Hover : Brushes.Transparent;

    partial void OnHoveredChanged(bool value) => OnPropertyChanged(nameof(RowBackground));
    partial void OnCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckFill));
        OnPropertyChanged(nameof(CheckBorder));
    }
}
