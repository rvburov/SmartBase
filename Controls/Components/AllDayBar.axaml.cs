using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using System.Windows.Input;

namespace MyPlanner.Controls.Components;

public partial class AllDayBar : UserControl
{
    public event EventHandler<Event>? EventClicked;

    public AllDayBar()
    {
        InitializeComponent();
    }

    public void SetData(IEnumerable<DateOnly> days, IEnumerable<Event> events)
    {
        var daySet = days.ToHashSet();
        var chips  = events
            .Where(e => e.AllDay || (e.EndDt - e.StartDt).TotalSeconds >= 86400)
            .Where(e => daySet.Any(d =>
                DateOnly.FromDateTime(e.StartDt) <= d && d <= DateOnly.FromDateTime(e.EndDt)))
            .DistinctBy(e => e.Id)
            .OrderBy(e => e.StartDt)
            .Select(e => new AllDayEventChip(e, () => EventClicked?.Invoke(this, e)))
            .ToList();

        EventsList.ItemsSource = chips;
    }

    public void SetData(DateOnly day, IEnumerable<Event> events) =>
        SetData([day], events);

    public void ClearData() => EventsList.ItemsSource = null;
}

public class AllDayEventChip
{
    public string   Title          { get; }
    public IBrush   ChipBackground { get; }
    public IBrush   ChipForeground { get; }
    public ICommand ClickCommand   { get; }

    public AllDayEventChip(Event ev, Action onClick)
    {
        Title        = ev.Title;
        ClickCommand = new RelayCommand(onClick);

        var color   = Color.Parse(ev.Color);
        ChipBackground = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B));
        ChipForeground = new SolidColorBrush(color);
    }
}
