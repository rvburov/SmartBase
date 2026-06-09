using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;
using MyPlanner.Utils;

namespace MyPlanner.Views.ConstructorDb.Calendar;

public partial class ListView : UserControl
{
    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<DateTime>? CreateRequested;

    private List<EventListItem> _items       = new();
    private DateTime            _currentDate = DateTime.Today;

    public ListView() { InitializeComponent(); }

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
        foreach (var i in _items)
            i.IsSelected = false;
    }

    private List<CalendarRecord> SelectedRecords()
        => _items.Where(i => i.IsSelected).Select(i => i.OriginalEvent).ToList();

    public void Refresh(IEnumerable<CalendarRecord> events, DateTime currentDate)
    {
        _currentDate = currentDate;
        var isPast = currentDate.Date < DateTime.Today;
        _items = events
            .Where(e => e.StartDt.Date == currentDate.Date)
            .OrderBy(e => e.AllDay ? 0 : 1)
            .ThenBy(e => e.StartDt)
            .Select(e => new EventListItem(e, isPast))
            .ToList();
        EventsList.ItemsSource = _items;
        EmptyHint.IsVisible    = _items.Count == 0;
    }

    private void OnEventPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: EventListItem item } && !item.IsSelected)
            item.IsHovered = true;
    }

    private void OnEventPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: EventListItem item })
            item.IsHovered = false;
    }

    private void OnEventTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem item })
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
                foreach (var i in _items)
                {
                    i.IsSelected = false;
                    i.IsHovered  = false;
                }
                item.IsSelected = true;
            }
            e.Handled = true;
        }
    }

    // Правый клик по событию: если оно ещё не выделено — выделяем его одно
    // (как в других режимах), чтобы меню действовало на видимо выделенное.
    private void OnEventPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem item }
            && e.GetCurrentPoint(this).Properties.IsRightButtonPressed
            && !item.IsSelected)
        {
            foreach (var i in _items)
            {
                i.IsSelected = false;
                i.IsHovered  = false;
            }
            item.IsSelected = true;
        }
    }

    private void OnEventContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border { Tag: EventListItem item } border) return;

        var menu = new ContextMenu();
        var selected = SelectedRecords();

        if (selected.Count > 1 && item.IsSelected)
        {
            menu.Items.Add(MakeMenuItem("Удалить", () =>
            {
                DeselectAll();
                RecordsDeleteRequested?.Invoke(this, selected);
            }));
        }
        else
        {
            menu.Items.Add(MakeMenuItem("Редактировать", () =>
            {
                DeselectAll();
                RecordClicked?.Invoke(this, item.OriginalEvent);
            }));
            menu.Items.Add(MakeMenuItem("Удалить", () =>
            {
                DeselectAll();
                RecordDeleteRequested?.Invoke(this, item.OriginalEvent);
            }));
        }

        menu.Open(border);
        e.Handled = true;
    }

    private void OnBackgroundContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Меню фона открывается, только если клик не по событию (по событию
        // сработает OnEventContextRequested и пометит e.Handled).
        DeselectAll();
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("Создать запись", () => CreateRequested?.Invoke(this, _currentDate)));
        if (sender is Control c) menu.Open(c);
        e.Handled = true;
    }

    private void OnEventDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem item })
        {
            e.Handled = true;
            RecordClicked?.Invoke(this, item.OriginalEvent);
        }
    }

    private void OnBackgroundDoubleTapped(object? sender, TappedEventArgs e)
    {
        CreateRequested?.Invoke(this, _currentDate);
    }

    private static MenuItem MakeMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(onClick);
        return item;
    }
}

public partial class EventListItem : ObservableObject
{
    public CalendarRecord  OriginalEvent  { get; }
    public string Title          { get; }
    public string DateTimeText   { get; }
    public string Description    { get; }
    public bool   HasDescription { get; }
    public IBrush ColorBrush     { get; }
    public IBrush TitleBrush     { get; }

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

    private static readonly string[] Months =
        ["янв", "фев", "мар", "апр", "май", "июн",
         "июл", "авг", "сен", "окт", "ноя", "дек"];

    public EventListItem(CalendarRecord ev, bool isPast = false)
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

        DateTimeText = ev.AllDay
            ? "Весь день"
            : $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
    }
}
