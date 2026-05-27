using Avalonia.Controls;

namespace MyPlanner.Controls.Components;

public partial class ConstructorNavbar : UserControl
{
    private static readonly string[] ViewNames = ["Таблица", "Список"];
    private static readonly string[] ViewModes = ["table", "list"];

    public event EventHandler?        AddClicked;
    public event EventHandler?        AddPropertyClicked;
    public event EventHandler?        FilterClicked;
    public event EventHandler?        SortClicked;
    public event EventHandler<string>? ViewChanged;
    public event EventHandler<bool>?  ColResizeModeChanged;
    public event EventHandler<bool>?  RowResizeModeChanged;

    public ConstructorNavbar()
    {
        InitializeComponent();

        ViewSelector.ItemsSource  = ViewNames;
        ViewSelector.SelectedIndex = 0;

        AddBtn.Click         += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);
        AddPropertyBtn.Click += (_, _) => AddPropertyClicked?.Invoke(this, EventArgs.Empty);
        FilterBtn.Click      += (_, _) => FilterClicked?.Invoke(this, EventArgs.Empty);
        SortBtn.Click        += (_, _) => SortClicked?.Invoke(this, EventArgs.Empty);

        ViewSelector.SelectionChanged += (_, _) =>
        {
            var idx = ViewSelector.SelectedIndex;
            if (idx >= 0 && idx < ViewModes.Length)
                ViewChanged?.Invoke(this, ViewModes[idx]);
        };

        ColResizeBtn.IsCheckedChanged += (_, _) =>
            ColResizeModeChanged?.Invoke(this, ColResizeBtn.IsChecked == true);

        RowResizeBtn.IsCheckedChanged += (_, _) =>
            RowResizeModeChanged?.Invoke(this, RowResizeBtn.IsChecked == true);
    }

    public void SetTitle(string text) => TitleLabel.Text = text;

    public string CurrentViewMode =>
        ViewModes[Math.Max(0, Math.Min(ViewSelector.SelectedIndex, ViewModes.Length - 1))];

    public void LoadCollectionState(bool colResize, bool rowResize, string viewMode)
    {
        ColResizeBtn.IsChecked = colResize;
        RowResizeBtn.IsChecked = rowResize;

        var idx = Array.IndexOf(ViewModes, viewMode);
        if (idx >= 0) ViewSelector.SelectedIndex = idx;
    }
}
