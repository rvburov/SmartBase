using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DataBase.Controls.Components;

public partial class NavbarCalendar : UserControl
{
    private static readonly string[] ViewNames = ["Расписание", "Журнал", "День", "Неделя", "Месяц", "Год"];

    public event EventHandler? TodayClicked;
    public event EventHandler? PrevClicked;
    public event EventHandler? NextClicked;
    public event EventHandler? AddClicked;
    public event EventHandler<int>? ViewChanged;
    public event EventHandler<string>? SearchChanged;

    public NavbarCalendar()
    {
        InitializeComponent();

        ViewSelector.ItemsSource  = ViewNames;
        ViewSelector.SelectedIndex = 4; // Месяц по умолчанию

        TodayBtn.Click += (_, _) => TodayClicked?.Invoke(this, EventArgs.Empty);
        PrevBtn.Click  += (_, _) => PrevClicked?.Invoke(this, EventArgs.Empty);
        NextBtn.Click  += (_, _) => NextClicked?.Invoke(this, EventArgs.Empty);
        AddBtn.Click   += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);

        ViewSelector.SelectionChanged += (_, _) =>
            ViewChanged?.Invoke(this, ViewSelector.SelectedIndex);

        SearchBox.TextChanged += (_, _) =>
            SearchChanged?.Invoke(this, SearchBox.Text ?? "");
    }

    public void SetTitle(string text, Avalonia.Media.IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }
    public void SetView(int idx)        => ViewSelector.SelectedIndex = idx;
    public int  CurrentViewIndex        => ViewSelector.SelectedIndex;
}
