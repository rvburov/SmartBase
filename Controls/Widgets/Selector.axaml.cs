using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;

namespace MyPlanner.Controls.Widgets;

public record SelectorItem(string Label, object? Data = null);

public partial class Selector : UserControl
{
    public static readonly StyledProperty<ObservableCollection<SelectorItem>> ItemsProperty =
        AvaloniaProperty.Register<Selector, ObservableCollection<SelectorItem>>(nameof(Items), []);

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<Selector, int>(nameof(SelectedIndex), 0);

    public ObservableCollection<SelectorItem> Items         { get => GetValue(ItemsProperty);         set => SetValue(ItemsProperty, value); }
    public int                                SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    public SelectorItem? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    public Selector() => InitializeComponent();
}
