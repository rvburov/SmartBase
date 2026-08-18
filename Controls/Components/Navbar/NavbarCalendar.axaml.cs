using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SmartBase.Settings;

namespace SmartBase.Controls.Components;

public partial class NavbarCalendar : UserControl
{
    // Показываем только виды, не скрытые в «Оформлении». НАРУЖУ при этом отдаём и
    // принимаем ГЛОБАЛЬНЫЙ номер вида (позиция в ViewCatalog): на него завязаны
    // CalendarControllerView.SwitchView и сохранённый calendarView, и скрытие пунктов не
    // должно менять смысл этих чисел. Перевод «позиция в списке ↔ глобальный номер»
    // держится здесь, в _viewIndexes.
    private int[] _viewIndexes = [];

    public event EventHandler? TodayClicked;
    public event EventHandler? PrevClicked;
    public event EventHandler? NextClicked;
    public event EventHandler? AddClicked;
    public event EventHandler<int>? ViewChanged;
    public event EventHandler<string>? SearchChanged;

    public NavbarCalendar()
    {
        InitializeComponent();

        var visible  = ViewCatalog.Visible(ViewCatalog.ScopeCalendar);
        _viewIndexes = [.. visible.Select(v => ViewCatalog.IndexOf(ViewCatalog.ScopeCalendar, v.Key))];

        ViewSelector.ItemsSource   = visible.Select(v => v.Label).ToList();
        ViewSelector.SelectedIndex = 0;   // настоящий стартовый вид ставит контроллер (SetView)

        TodayBtn.Click += (_, _) => TodayClicked?.Invoke(this, EventArgs.Empty);
        PrevBtn.Click  += (_, _) => PrevClicked?.Invoke(this, EventArgs.Empty);
        NextBtn.Click  += (_, _) => NextClicked?.Invoke(this, EventArgs.Empty);
        AddBtn.Click   += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);

        ViewSelector.SelectionChanged += (_, _) =>
            ViewChanged?.Invoke(this, CurrentViewIndex);

        SearchBox.TextChanged += (_, _) =>
            SearchChanged?.Invoke(this, SearchBox.Text ?? "");
    }

    public void SetTitle(string text, Avalonia.Media.IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }
    /// <summary>Показать вид по ГЛОБАЛЬНОМУ номеру; скрытый вид — показываем первый видимый.</summary>
    public void SetView(int idx)
    {
        var pos = Array.IndexOf(_viewIndexes, idx);
        ViewSelector.SelectedIndex = pos >= 0 ? pos : 0;
    }

    /// <summary>Глобальный номер выбранного вида (позиция в ViewCatalog).</summary>
    public int CurrentViewIndex
    {
        get
        {
            var pos = ViewSelector.SelectedIndex;
            return pos >= 0 && pos < _viewIndexes.Length ? _viewIndexes[pos] : 0;
        }
    }
}
