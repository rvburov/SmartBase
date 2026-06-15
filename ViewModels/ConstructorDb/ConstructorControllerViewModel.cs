using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.ViewModels.ConstructorDb;

public partial class ConstructorControllerViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;

    [ObservableProperty] private ObservableCollection<DbCollection> _collections = [];
    [ObservableProperty] private ObservableCollection<DbGroup>      _groups       = [];
    [ObservableProperty] private DbCollection?                       _selectedCollection;
    [ObservableProperty] private bool                                _isSidebarOpen = true;
    [ObservableProperty] private string                              _viewMode      = "table";

    public TableViewModel TableVm { get; }
    public ListViewModel  ListVm  { get; }

    public ConstructorControllerViewModel(ConstructorRepository constructor)
    {
        _constructor = constructor;

        TableVm = new TableViewModel(constructor);
        ListVm  = new ListViewModel(constructor);

        Refresh();
    }

    public void Refresh()
    {
        Groups      = new(_constructor.GetAllGroups());
        Collections = new(_constructor.GetAllCollections());
    }

    [RelayCommand]
    private void SelectCollection(DbCollection col)
    {
        SelectedCollection = col;
        TableVm.LoadCollection(col.Id!.Value);
        ListVm.LoadCollection(col.Id!.Value);
        var (_, _, mode) = _constructor.GetUiSettings(col.Id!.Value);
        ViewMode = mode;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    public event Action? CollectionsChanged;
    private void RaiseCollectionsChanged() => CollectionsChanged?.Invoke();
}
