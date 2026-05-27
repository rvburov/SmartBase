using Avalonia.Controls;
using MyPlanner.Database.Repositories;
using MyPlanner.ViewModels;

namespace MyPlanner.Views;

public partial class MainWindow : Window
{
    private readonly EventRepository _events;
    private readonly CategoryRepository _categories;
    private readonly ConstructorRepository _constructor;
    private string _activeModule = "calendar";
    private bool _sidebarOpen = true;

    public MainWindow(
        EventRepository events,
        CategoryRepository categories,
        ConstructorRepository constructor)
    {
        InitializeComponent();

        _events      = events;
        _categories  = categories;
        _constructor = constructor;

        // Инициализируем модули через AXAML-конструкторы
        CalendarModule.Initialize(_events, _categories, _constructor);
        ConstructorModule.Initialize(_constructor);

        // Инициализируем данные боковых панелей
        CalendarSidebar.Initialize(_constructor);
        CalendarSidebar.Refresh();
        ConstructorSidebar.Load(constructor);

        // Начальное состояние: модуль Календарь
        ActivityBarControl.SetModuleActive("calendar");
        ActivityBarControl.SetSidebarOpen(true);
        ShowModule("calendar");

        // Связываем ActivityBar
        ActivityBarControl.SidebarToggled += (_, isOpen) => { _sidebarOpen = isOpen; OnSidebarToggled(isOpen); };
        ActivityBarControl.ModuleChanged  += (_, module) => ShowModule(module);

        // Sidebar коллекций передаёт активные id в CalendarModule
        CalendarSidebar.ActiveIdsChanged += (_, ids) =>
            CalendarModule.SetActiveIds(ids);

        // После создания/изменения события обновляем Sidebar (могла появиться новая коллекция)
        CalendarModule.CollectionsMayHaveChanged += (_, _) =>
        {
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
        };

        // Дерево конструктора: выбор коллекции открывает её в ConstructorModule
        ConstructorSidebar.CollectionSelected += (_, id) =>
            ConstructorModule.SelectCollection(id);

        // При изменении коллекций из конструктора — обновить sidebar
        ConstructorModule.CollectionsChanged += (_, _) =>
        {
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Reload();
        };

        // Первоначальная загрузка фильтра
        CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
    }

    private void ShowModule(string module)
    {
        _activeModule = module;

        CalendarModule.IsVisible    = module == "calendar";
        ConstructorModule.IsVisible = module == "constructor";

        var sidebarOpen = _sidebarOpen;

        if (module == "calendar")
        {
            CalendarSidebar.IsVisible    = sidebarOpen;
            ConstructorSidebar.IsVisible = false;
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
        }
        else
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = sidebarOpen;
            ConstructorSidebar.Reload();
            ConstructorModule.Refresh();
        }
    }

    private void OnSidebarToggled(bool isOpen)
    {
        if (_activeModule == "calendar")
            CalendarSidebar.IsVisible = isOpen;
        else
            ConstructorSidebar.IsVisible = isOpen;
    }
}
