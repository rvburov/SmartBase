using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using DataBase.Core.Models;
using DataBase.Database.Repositories;
using DataBase.Utils;

namespace DataBase.Views.ConstructorDb.Calendar;

public partial class ListView : UserControl
{
    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<DateTime>? CreateRequested;

    // Полная виртуализация: список знает ПОЛНОЕ число событий дня (полоса прокрутки на весь объём,
    // можно перетащить в самый низ), а страницы грузятся по требованию (см. CalendarDayVirtualList).
    private CalendarDayVirtualList? _list;
    private DateTime _currentDate = DateTime.Today;

    public ListView()
    {
        InitializeComponent();
    }

    private EventHandler<KeyEventArgs>? _windowKeyHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        top?.AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        if (e.Root is Avalonia.Controls.Window win)
        {
            _windowKeyHandler = (_, ke) =>
            {
                if (ke.Key != Key.Delete && ke.Key != Key.Back) return;
                if (!IsVisible) return;
                if (ke.Source is TextBox or AutoCompleteBox) return;
                var selected = SelectedRecords();
                if (selected.Count == 0) return;
                DeselectAll();
                if (selected.Count == 1) RecordDeleteRequested?.Invoke(this, selected[0]);
                else                     RecordsDeleteRequested?.Invoke(this, selected);
                ke.Handled = true;
            };
            win.AddHandler(KeyDownEvent, _windowKeyHandler, handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TopLevel.GetTopLevel(this)?.RemoveHandler(
            PointerPressedEvent, OnGlobalPointerPressed);
        if (e.Root is Avalonia.Controls.Window win && _windowKeyHandler is not null)
        {
            win.RemoveHandler(KeyDownEvent, _windowKeyHandler);
            _windowKeyHandler = null;
        }
    }

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Правый клик не сбрасывает выделение — иначе контекстное меню откроется
        // над уже снятым выделением (как в режимах «Месяц»/«Неделя»/«День»).
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) return;

        // Shift+клик — аддитивное выделение: глобальный сброс пропускаем,
        // toggle делается в OnEventTapped.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        DeselectAll();
    }

    private void DeselectAll()
    {
        if (_list is null) return;
        foreach (var i in _list.RealizedItems)
            i.IsSelected = false;
    }

    private List<CalendarRecord> SelectedRecords()
        => _list is null
            ? new List<CalendarRecord>()
            : _list.RealizedItems.Where(i => i.IsEvent && i.IsSelected).Select(i => i.OriginalEvent!).ToList();

    // Поколение загрузки дня: быстрое переключение дат/поиска отменяет устаревший результат.
    private int _loadGeneration;

    /// <summary>
    /// Открывает день в «Расписании» через полную виртуализацию: события дня из
    /// <paramref name="dbs"/> (несколько БД) в окне [<paramref name="dayStart"/>,
    /// <paramref name="dayEnd"/>). Полоса прокрутки — на всё число событий, страницы грузятся по
    /// требованию (в т.ч. прыжок в самый низ). <paramref name="search"/> фильтрует по заголовку.
    /// Подсчёт числа событий (размер полосы) идёт В ФОНЕ на изолированных подключениях: холодный
    /// COUNT по дню с миллионами событий занимает секунды и не должен блокировать UI-поток
    /// (иначе старт приложения выглядел зависшим на «Запуске интерфейса…»).
    /// </summary>
    public void LoadFrom(IReadOnlyList<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
                         DateTime dayStart, DateTime dayEnd, DateTime currentDate, string search)
    {
        _currentDate = currentDate;
        var isPast = currentDate.Date < DateTime.Today;
        var gen    = ++_loadGeneration;

        _list = null;
        EventsList.ItemsSource = null;
        EmptyHint.Text      = "Загрузка…";
        EmptyHint.IsVisible = true;
        // Порядок источников фиксируем по DbId — он же порядок конкатенации в виртуальном списке.
        _ = LoadCountsThenBuildAsync(gen, dbs.OrderBy(d => d.dbId).ToList(),
                                     dayStart, dayEnd, search ?? "", isPast);
    }

    private async Task LoadCountsThenBuildAsync(
        int gen, List<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        DateTime dayStart, DateTime dayEnd, string search, bool isPast)
    {
        var counts = new int[dbs.Count];
        try
        {
            for (var i = 0; i < dbs.Count; i++)
                counts[i] = await dbs[i].repo.CountCalendarDayEventsAsync(dayStart, dayEnd, dbs[i].active);
        }
        catch
        {
            // БД недоступна/повреждена — день показываем пустым (как ведёт себя Workspace.Mount).
            if (gen == _loadGeneration) EmptyHint.Text = "Нет записей";
            return;
        }
        if (gen != _loadGeneration) return;   // пока считали — открыли другой день/поиск

        // Построение списка (для обычных дней — синхронная загрузка в память) — на UI-потоке.
        var list = new CalendarDayVirtualList(dbs, counts, dayStart, dayEnd, search, isPast);
        _list = list;
        EventsList.ItemsSource = list;
        ListScroll.Offset   = new Vector(0, 0);
        EmptyHint.Text      = "Нет записей";
        EmptyHint.IsVisible = list.Count == 0;
    }

    private void OnEventPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item } && !item.IsSelected)
            item.IsHovered = true;
    }

    private void OnEventPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: EventListItem item })
            item.IsHovered = false;
    }

    private void OnEventTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item })
        {
            var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (additive)
            {
                // toggle: повторный Shift+клик по выделенному — снять
                item.IsHovered  = false;
                item.IsSelected = !item.IsSelected;
            }
            else
            {
                DeselectAll();
                foreach (var i in _list?.RealizedItems ?? [])
                    i.IsHovered = false;
                item.IsSelected = true;
            }
            e.Handled = true;
        }
    }

    // Правый клик по событию: если оно ещё не выделено — выделяем его одно
    // (как в других режимах), чтобы меню действовало на видимо выделенное.
    private void OnEventPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item }
            && e.GetCurrentPoint(this).Properties.IsRightButtonPressed
            && !item.IsSelected)
        {
            DeselectAll();
            foreach (var i in _list?.RealizedItems ?? [])
                i.IsHovered = false;
            item.IsSelected = true;
        }
    }

    private void OnEventContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border { Tag: EventListItem { IsEvent: true } item } border) return;

        var selected = SelectedRecords();
        ContextMenu menu;
        if (selected.Count > 1 && item.IsSelected)
        {
            menu = DataBase.Controls.Components.EventContextMenu.Multi(
                () => { DeselectAll(); RecordsDeleteRequested?.Invoke(this, selected); });
        }
        else
        {
            menu = DataBase.Controls.Components.EventContextMenu.Single(
                onEdit:   () => { DeselectAll(); RecordClicked?.Invoke(this, item.OriginalEvent!); },
                onDelete: () => { DeselectAll(); RecordDeleteRequested?.Invoke(this, item.OriginalEvent!); });
        }
        menu.Open(border);
        e.Handled = true;
    }

    private void OnBackgroundContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Меню фона открывается, только если клик не по событию (по событию
        // сработает OnEventContextRequested и пометит e.Handled).
        DeselectAll();
        var menu = DataBase.Controls.Components.EventContextMenu.Background(
            () => CreateRequested?.Invoke(this, _currentDate));
        if (sender is Control c) menu.Open(c);
        e.Handled = true;
    }

    private void OnEventDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item })
        {
            e.Handled = true;
            RecordClicked?.Invoke(this, item.OriginalEvent!);
        }
    }

    private void OnBackgroundDoubleTapped(object? sender, TappedEventArgs e)
    {
        CreateRequested?.Invoke(this, _currentDate);
    }
}

public partial class EventListItem : ObservableObject
{
    public CalendarRecord? OriginalEvent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEvent))]
    private bool _isLoadingPlaceholder;

    public bool IsEvent => !IsLoadingPlaceholder;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _dateTimeText = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool   _hasDescription;
    [ObservableProperty] private IBrush _colorBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush _titleBrush = Brushes.Gray;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private bool _isHovered;

    public IBrush BackgroundBrush => IsSelected
        ? new SolidColorBrush(Color.FromArgb(50, 128, 128, 128))
        : IsHovered
            ? new SolidColorBrush(Color.FromArgb(25, 128, 128, 128))
            : Brushes.Transparent;

    // Заглушка «Загрузка…» — заполняется реальным событием позже через Fill (в Offset-режиме).
    private EventListItem() => _isLoadingPlaceholder = true;
    public static EventListItem Loading() => new();

    public EventListItem(CalendarRecord ev, bool isPast = false) => Fill(ev, isPast);

    /// <summary>Заполняет строку данными события (заглушка → событие) с уведомлением привязок.</summary>
    public void Fill(CalendarRecord ev, bool isPast)
    {
        OriginalEvent  = ev;
        Title          = ev.DisplayTitle;
        // В превью разметка не нужна — снимаем markdown-форматирование
        Description    = MarkdownUtils.StripMarkdown(ev.Description);
        HasDescription = !string.IsNullOrWhiteSpace(Description);
        ColorBrush     = new SolidColorBrush(Color.Parse(EventColors.Display(ev.Color)));
        TitleBrush     = isPast
            ? new SolidColorBrush(Color.Parse("#8E8E93"))
            : new SolidColorBrush(Color.Parse("#1C1C1E"));
        DateTimeText   = ev.AllDay
            ? "Весь день"
            : $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
        IsLoadingPlaceholder = false;
    }
}
