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
    public event EventHandler<DateTime>? CreateRequested;

    private List<EventListItem> _items       = new();
    private DateTime            _currentDate = DateTime.Today;

    public ListView() { InitializeComponent(); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TopLevel.GetTopLevel(this)?.AddHandler(
            PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TopLevel.GetTopLevel(this)?.RemoveHandler(
            PointerPressedEvent, OnGlobalPointerPressed);
    }

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        foreach (var i in _items)
            i.IsSelected = false;
    }

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
            foreach (var i in _items)
            {
                i.IsSelected = false;
                i.IsHovered  = false;
            }
            item.IsSelected = true;
            e.Handled = true;
        }
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
