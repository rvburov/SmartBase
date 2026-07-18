using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartBase.Controls.Components;

public partial class NavbarTasks : UserControl
{
    private static readonly string[] ViewNames = ["Список"];

    public event EventHandler<int>? ViewChanged;
    public event EventHandler<string>? SearchChanged;

    public NavbarTasks()
    {
        InitializeComponent();

        ViewSelector.ItemsSource   = ViewNames;
        ViewSelector.SelectedIndex = 0;

        ViewSelector.SelectionChanged += (_, _) =>
            ViewChanged?.Invoke(this, ViewSelector.SelectedIndex);

        SearchBox.TextChanged += (_, _) =>
            SearchChanged?.Invoke(this, SearchBox.Text ?? "");
    }

    public void SetTitle(string text, IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }
}
