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

        // Смена цвета коллекции в сайдбаре → перерисовать календарь и дерево конструктора
        CalendarSidebar.CalendarsChanged += (_, _) =>
        {
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Reload();
        };

        // После создания/изменения события обновляем Sidebar (могла появиться новая коллекция)
        CalendarModule.CollectionsMayHaveChanged += (_, _) =>
        {
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
        };

        // Дерево конструктора: выбор коллекции открывает её в ConstructorModule
        ConstructorSidebar.CollectionSelected += (_, id) =>
            ConstructorModule.SelectCollection(id);

        // CRUD в дереве конструктора (создание/переименование/удаление) →
        // обновить календарь и заголовок/данные открытой коллекции в конструкторе
        ConstructorSidebar.RequestRefresh += (_, _) =>
        {
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorModule.Refresh();
        };
        ConstructorSidebar.CollectionDeleted += (_, _) => ConstructorModule.Refresh();

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

        ApplySidebarColumn(sidebarOpen);
    }

    private void OnSidebarToggled(bool isOpen)
    {
        if (_activeModule == "calendar")
            CalendarSidebar.IsVisible = isOpen;
        else
            ConstructorSidebar.IsVisible = isOpen;

        ApplySidebarColumn(isOpen);
    }

    // Запоминаем последнюю ширину сайдбара, чтобы вернуть её при повторном открытии
    private double _lastSidebarWidth = 220;

    private const double SidebarMin = 180;
    private const double SidebarMax = 500;

    // Колонка сайдбара схлопывается при скрытии, чтобы контент занимал всё окно
    // (split-view как в Obsidian).
    private void ApplySidebarColumn(bool isOpen)
    {
        var col = RootGrid.ColumnDefinitions[2];

        if (isOpen)
        {
            col.MinWidth = SidebarMin;
            col.MaxWidth = SidebarMax;
            col.Width    = new Avalonia.Controls.GridLength(_lastSidebarWidth, Avalonia.Controls.GridUnitType.Pixel);
            ResizeGrip.IsVisible = true;
        }
        else
        {
            if (col.Width.IsAbsolute && col.Width.Value > 0)
                _lastSidebarWidth = col.Width.Value;
            col.MinWidth = 0;
            col.MaxWidth = 0;
            col.Width    = new Avalonia.Controls.GridLength(0);
            ResizeGrip.IsVisible = false;
        }
    }

    // ── Перетаскивание ручки-блока для изменения ширины сайдбара ─────────────────

    private bool   _gripDragging;
    private double _gripStartX;
    private double _gripStartWidth;

    private void OnGripPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var col = RootGrid.ColumnDefinitions[2];
        _gripDragging   = true;
        _gripStartX     = e.GetPosition(this).X;
        _gripStartWidth = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        e.Pointer.Capture((Avalonia.Input.IInputElement?)sender);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_gripDragging) return;

        var dx       = e.GetPosition(this).X - _gripStartX;
        var newWidth = Math.Clamp(_gripStartWidth + dx, SidebarMin, SidebarMax);

        var col = RootGrid.ColumnDefinitions[2];
        col.Width = new Avalonia.Controls.GridLength(newWidth, Avalonia.Controls.GridUnitType.Pixel);
        _lastSidebarWidth = newWidth;
    }

    private void OnGripReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _gripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
