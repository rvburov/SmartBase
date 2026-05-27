using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Components;

public partial class SidebarTree : UserControl
{
    public event EventHandler<int>? CollectionSelected;

    private ConstructorRepository? _repository;
    private SortState _sortState = SortState.None;

    public SidebarTree()
    {
        InitializeComponent();

        NewCollectionBtn.Click += (_, _) => OnNewCollectionClicked();
        NewGroupBtn.Click      += (_, _) => OnNewGroupClicked();
        SortBtn.Click          += (_, _) => CycleSort();
        ExpandBtn.IsCheckedChanged += (_, _) => ApplyExpand(ExpandBtn.IsChecked == true);

        CollectionTree.SelectionChanged += (_, _) =>
        {
            if (CollectionTree.SelectedItem is SidebarTreeNode { NodeType: "collection" } node && node.Id.HasValue)
                CollectionSelected?.Invoke(this, node.Id.Value);
        };
    }

    public void Load(ConstructorRepository repository)
    {
        _repository = repository;
        Reload();
    }

    public void Reload()
    {
        if (_repository is null) return;
        CollectionTree.ItemsSource = BuildTree();
    }

    public void SelectCollection(int collectionId)
    {
        var nodes = CollectionTree.ItemsSource as IEnumerable<SidebarTreeNode> ?? [];
        var target = FindNode(nodes, collectionId);
        if (target is not null)
            CollectionTree.SelectedItem = target;
    }

    private List<SidebarTreeNode> BuildTree()
    {
        if (_repository is null) return [];

        var groups      = _repository.GetAllGroups();
        var collections = _repository.GetAllCollections();
        var root        = new List<SidebarTreeNode>();

        foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.Position))
            root.Add(BuildGroupNode(g, groups, collections));

        foreach (var c in collections.Where(c => c.GroupId is null).OrderBy(c => c.Position))
            root.Add(new SidebarTreeNode { NodeType = "collection", Id = c.Id, Name = c.Name, Icon = "○" });

        return _sortState switch
        {
            SortState.Asc  => root.OrderBy(n => n.Name).ToList(),
            SortState.Desc => root.OrderByDescending(n => n.Name).ToList(),
            _              => root,
        };
    }

    private static SidebarTreeNode BuildGroupNode(DbGroup group, List<DbGroup> allGroups, List<DbCollection> allCollections)
    {
        var node = new SidebarTreeNode { NodeType = "group", Id = group.Id, Name = group.Name, Icon = "▸" };

        foreach (var child in allGroups.Where(g => g.ParentId == group.Id).OrderBy(g => g.Position))
            node.Children.Add(BuildGroupNode(child, allGroups, allCollections));

        foreach (var col in allCollections.Where(c => c.GroupId == group.Id).OrderBy(c => c.Position))
            node.Children.Add(new SidebarTreeNode { NodeType = "collection", Id = col.Id, Name = col.Name, Icon = "○" });

        return node;
    }

    private static SidebarTreeNode? FindNode(IEnumerable<SidebarTreeNode> nodes, int collectionId)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == "collection" && node.Id == collectionId) return node;
            var found = FindNode(node.Children, collectionId);
            if (found is not null) return found;
        }
        return null;
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
        Reload();
    }

    private void ApplyExpand(bool expand)
    {
        ToolTip.SetTip(ExpandBtn, expand ? "Свернуть все" : "Развернуть все");
        // TreeView в Avalonia не имеет ExpandAll/CollapseAll;
        // полное раскрытие реализуется через IsExpanded на узлах при необходимости
    }

    private void OnNewCollectionClicked() { /* диалог открывается из родительского View */ }
    private void OnNewGroupClicked()      { /* диалог открывается из родительского View */ }

    private enum SortState { None, Asc, Desc }
}

public partial class SidebarTreeNode : ObservableObject
{
    public string NodeType { get; init; } = "";  // "group" | "collection"
    public int?   Id       { get; init; }
    public string Name     { get; init; } = "";
    public string Icon     { get; init; } = "";
    public ObservableCollection<SidebarTreeNode> Children { get; } = [];
}
