using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MyPlanner.Core.Models;

namespace MyPlanner.Views.Calendar;

public partial class JournalView : UserControl
{
    public event EventHandler<Event>?    EventClicked;
    public event EventHandler<DateTime>? CreateRequested;

    private List<JournalItem>  _allItems = new();
    private List<JournalGroup> _groups   = new();
    private ScrollViewer       _scroll   = null!;

    private static readonly string[] Months =
        ["января", "февраля", "марта", "апреля", "мая", "июня",
         "июля",   "августа", "сентября", "октября", "ноября", "декабря"];

    private static readonly string[] WeekDays =
        ["воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота"];

    public JournalView()
    {
        InitializeComponent();
        _scroll = this.FindControl<ScrollViewer>("JournalScroll")!;
    }

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
        foreach (var i in _allItems)
            i.IsSelected = false;
    }

    public void Refresh(IEnumerable<Event> events, DateTime currentDate)
    {
        var today = DateTime.Today;

        var groups = events
            .OrderBy(e => e.StartDt)
            .GroupBy(e => e.StartDt.Date)
            .Select(g =>
            {
                var isPast = g.Key < today;
                var items  = g.Select(e => new JournalItem(e, isPast)).ToList();
                return new JournalGroup(g.Key, items, Months, WeekDays);
            })
            .ToList();

        // Сегодня всегда присутствует, даже если нет событий
        if (!groups.Any(g => g.Date.Date == today))
        {
            var emptyToday = new JournalGroup(today, new List<JournalItem>(), Months, WeekDays);
            var insertIdx  = groups.FindIndex(g => g.Date > today);
            if (insertIdx < 0) groups.Add(emptyToday);
            else groups.Insert(insertIdx, emptyToday);
        }

        _groups   = groups;
        _allItems = groups.SelectMany(g => g.Events).ToList();
        GroupsList.ItemsSource = groups;

        Dispatcher.UIThread.Post(() =>
        {
            var idx = _groups.FindIndex(g => g.Date.Date == DateTime.Today);
            if (idx < 0) return;
            var panel = GroupsList.ItemsPanelRoot;
            if (panel == null || idx >= panel.Children.Count) return;
            var pt = panel.Children[idx].TranslatePoint(new Point(0, 0), GroupsList);
            if (pt.HasValue) _scroll.Offset = new Vector(0, pt.Value.Y);
        }, DispatcherPriority.Background);
    }

    private void OnEventPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: JournalItem item } && !item.IsSelected)
            item.IsHovered = true;
    }

    private void OnEventPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: JournalItem item })
            item.IsHovered = false;
    }

    private void OnEventTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: JournalItem item })
        {
            foreach (var i in _allItems)
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
        if (sender is Border { Tag: JournalItem item })
        {
            e.Handled = true;
            EventClicked?.Invoke(this, item.OriginalEvent);
        }
    }

    private void OnBackgroundDoubleTapped(object? sender, TappedEventArgs e)
    {
        var date = GetDateForY(e.GetPosition(GroupsList).Y);
        CreateRequested?.Invoke(this, date);
    }

    private DateTime GetDateForY(double y)
    {
        var panel = GroupsList.ItemsPanelRoot;
        if (panel == null || _groups.Count == 0) return DateTime.Today;
        var panelOffset = panel.Bounds.Y;
        var result = _groups[0].Date;
        for (int i = 0; i < _groups.Count && i < panel.Children.Count; i++)
        {
            if (panelOffset + panel.Children[i].Bounds.Y <= y)
                result = _groups[i].Date;
            else
                break;
        }
        return result.Date;
    }

    // ── Навигация по датам ────────────────────────────────────────────────────

    private List<double> GetGroupPositions()
    {
        var positions = new List<double>();
        var panel = GroupsList.ItemsPanelRoot;
        if (panel == null) return positions;
        foreach (var child in panel.Children)
        {
            var pt = child.TranslatePoint(new Point(0, 0), GroupsList);
            if (pt.HasValue) positions.Add(pt.Value.Y);
        }
        return positions;
    }

    private void AnimateScroll(double targetY)
    {
        var startY     = _scroll.Offset.Y;
        var startTime  = DateTime.Now;
        var timer      = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t      = Math.Min((DateTime.Now - startTime).TotalMilliseconds / 300.0, 1.0);
            var eased  = 1 - Math.Pow(1 - t, 3);
            _scroll.Offset = new Vector(0, startY + (targetY - startY) * eased);
            if (t >= 1.0) timer.Stop();
        };
        timer.Start();
    }

    public void GoNext()
    {
        var positions = GetGroupPositions().Where(y => y > _scroll.Offset.Y + 5).ToList();
        if (positions.Count > 0) AnimateScroll(positions[0]);
    }

    public void GoPrev()
    {
        var positions = GetGroupPositions().Where(y => y < _scroll.Offset.Y - 5).ToList();
        if (positions.Count > 0) AnimateScroll(positions[^1]);
    }

    public void GoToday()
    {
        var idx = _groups.FindIndex(g => g.Date.Date == DateTime.Today);
        if (idx < 0) return;
        var panel = GroupsList.ItemsPanelRoot;
        if (panel == null || idx >= panel.Children.Count) return;
        var pt = panel.Children[idx].TranslatePoint(new Point(0, 0), GroupsList);
        if (pt.HasValue) AnimateScroll(pt.Value.Y);
    }
}

public class JournalGroup
{
    public DateTime          Date          { get; }
    public string            DateHeader    { get; }
    public IBrush            HeaderBrush   { get; }
    public List<JournalItem> Events        { get; }
    public bool              ShowEmptyHint { get; }

    public JournalGroup(DateTime date, List<JournalItem> events,
                        string[] months, string[] weekDays)
    {
        Date = date;
        var today   = DateTime.Today;
        var isToday = date.Date == today;
        var isPast  = date.Date < today;
        ShowEmptyHint = isToday && events.Count == 0;

        DateHeader = $"{weekDays[(int)date.DayOfWeek]} – {date.Day} {months[date.Month - 1]}";

        HeaderBrush = isToday
            ? new SolidColorBrush(Color.Parse("#FF3B30"))
            : isPast
                ? new SolidColorBrush(Color.Parse("#8E8E93"))
                : new SolidColorBrush(Color.Parse("#1C1C1E"));

        Events = events;
    }
}

public partial class JournalItem : ObservableObject
{
    public Event  OriginalEvent { get; }
    public string Title         { get; }
    public string TimeText      { get; }
    public IBrush ColorBrush    { get; }
    public IBrush TitleBrush    { get; }

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

    public JournalItem(Event ev, bool isPast = false)
    {
        OriginalEvent = ev;
        Title         = ev.Title;
        TimeText      = ev.AllDay ? "Весь день" : $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
        ColorBrush    = new SolidColorBrush(Color.Parse(EventColors.Display(ev.Color)));
        TitleBrush    = isPast
            ? new SolidColorBrush(Color.Parse("#8E8E93"))
            : new SolidColorBrush(Color.Parse("#1C1C1E"));
    }
}
