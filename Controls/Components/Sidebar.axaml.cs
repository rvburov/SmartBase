using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Components;

public partial class Sidebar : UserControl
{
    public event EventHandler<HashSet<int>>? ActiveIdsChanged;

    private ConstructorRepository? _constructor;

    public Sidebar()
    {
        InitializeComponent();
    }

    public Sidebar(ConstructorRepository constructor) : this()
    {
        _constructor = constructor;
        Refresh();
    }

    public void Initialize(ConstructorRepository constructor)
    {
        _constructor = constructor;
    }

    public void Refresh()
    {
        if (_constructor is null) return;

        var collections = _constructor.GetAllCollections();
        var nodes = collections.Select(c => new SidebarNode(c)).ToList();
        CollectionTree.ItemsSource = nodes;

        foreach (var node in nodes)
            node.VisibilityChanged += _ => RaiseActiveIds();
    }

    public HashSet<int> GetActiveIds()
    {
        var nodes = CollectionTree.ItemsSource as IEnumerable<SidebarNode> ?? [];
        return nodes.Where(n => n.IsVisible && n.Collection.Id.HasValue)
                    .Select(n => n.Collection.Id!.Value)
                    .ToHashSet();
    }

    private void RaiseActiveIds() => ActiveIdsChanged?.Invoke(this, GetActiveIds());
}

public partial class SidebarNode : ObservableObject
{
    public DbCollection Collection { get; }
    public string Name  => Collection.Name;
    public string Color => Collection.Color;

    [ObservableProperty] private bool _isVisible = true;

    public event Action<bool>? VisibilityChanged;

    public SidebarNode(DbCollection collection)
    {
        Collection = collection;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsVisible))
                VisibilityChanged?.Invoke(IsVisible);
        };
    }
}
