using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBase.Database;
using DataBase.Database.Repositories;
using DataBase.ViewModels.Calendar;
using DataBase.ViewModels.ConstructorDb;

namespace DataBase.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppDbContext        _db;
    private readonly CategoryRepository  _categories;
    private readonly ConstructorRepository _constructor;

    [ObservableProperty] private ViewModelBase _currentView;
    [ObservableProperty] private bool          _isSidebarOpen = true;
    [ObservableProperty] private string        _activeModule  = "calendar";

    public CalendarControllerViewModel  CalendarVm     { get; }
    public ConstructorControllerViewModel ConstructorVm { get; }

    public MainWindowViewModel(
        AppDbContext db,
        CategoryRepository categories,
        ConstructorRepository constructor)
    {
        _db          = db;
        _categories  = categories;
        _constructor = constructor;

        CalendarVm     = new CalendarControllerViewModel(_constructor, _categories);
        ConstructorVm  = new ConstructorControllerViewModel(_constructor);
        _currentView   = CalendarVm;
    }

    [RelayCommand]
    private void SwitchModule(string module)
    {
        ActiveModule = module;
        CurrentView  = module switch
        {
            "calendar"    => CalendarVm,
            "constructor" => ConstructorVm,
            _             => CalendarVm,
        };
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    public void OnCollectionsChanged()
    {
        CalendarVm.RefreshCollections();
    }
}
