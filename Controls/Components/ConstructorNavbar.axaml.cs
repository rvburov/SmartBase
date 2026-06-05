using Avalonia.Controls;

namespace MyPlanner.Controls.Components;

public partial class ConstructorNavbar : UserControl
{
    private static readonly string[] ViewNames = ["Таблица", "Список"];
    private static readonly string[] ViewModes = ["table", "list"];

    public event EventHandler<string>? ViewChanged;

    public ConstructorNavbar()
    {
        InitializeComponent();

        ViewSelector.ItemsSource   = ViewNames;
        ViewSelector.SelectedIndex = 0;

        ViewSelector.SelectionChanged += (_, _) =>
        {
            var idx = ViewSelector.SelectedIndex;
            if (idx >= 0 && idx < ViewModes.Length)
                ViewChanged?.Invoke(this, ViewModes[idx]);
        };
    }

    public void SetTitle(string text) => TitleLabel.Text = text;

    public string CurrentViewMode =>
        ViewModes[Math.Max(0, Math.Min(ViewSelector.SelectedIndex, ViewModes.Length - 1))];

    /// <summary>Выбрать вид (table/list) без подсветки кнопок — те живут в футере.</summary>
    public void SetViewMode(string viewMode)
    {
        var idx = Array.IndexOf(ViewModes, viewMode);
        if (idx >= 0) ViewSelector.SelectedIndex = idx;
    }
}
