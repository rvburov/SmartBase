using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartBase.Views.Tasks;

namespace SmartBase.Controls.Components;

public partial class NavbarTasks : UserControl
{
    private static readonly string[] ViewNames = ["Список"];

    public event EventHandler<int>? ViewChanged;
    public event EventHandler<string>? SearchChanged;
    /// <summary>Выбран быстрый фильтр (пункт выпадающего списка рядом с поиском).</summary>
    public event EventHandler<TaskFilterKind>? FilterChanged;

    // Гасит FilterChanged, пока список пересобирается со свежими счётчиками: смена
    // ItemsSource сбрасывает выбор, и без флага это выглядело бы как выбор пользователя.
    private bool _suppressFilterChange;

    public NavbarTasks()
    {
        InitializeComponent();

        ViewSelector.ItemsSource   = ViewNames;
        ViewSelector.SelectedIndex = 0;

        ViewSelector.SelectionChanged += (_, _) =>
            ViewChanged?.Invoke(this, ViewSelector.SelectedIndex);

        SearchBox.TextChanged += (_, _) =>
            SearchChanged?.Invoke(this, SearchBox.Text ?? "");

        FilterSelector.SelectionChanged += (_, _) =>
        {
            if (_suppressFilterChange) return;
            if (FilterSelector.SelectedItem is TaskFilterItem item) FilterChanged?.Invoke(this, item.Kind);
        };

        SetFilterCounts(TaskFilters.All.ToDictionary(k => k, _ => 0), TaskFilterKind.All);
    }

    public void SetTitle(string text, IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }

    /// <summary>Обновить счётчики пунктов и вернуть выбор на <paramref name="selected"/>.</summary>
    public void SetFilterCounts(IReadOnlyDictionary<TaskFilterKind, int> counts, TaskFilterKind selected)
    {
        var items = TaskFilters.All
            .Select(k => new TaskFilterItem(k, counts.TryGetValue(k, out var n) ? n : 0))
            .ToList();

        _suppressFilterChange = true;
        FilterSelector.ItemsSource   = items;
        FilterSelector.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Kind == selected));
        _suppressFilterChange = false;
    }
}

/// <summary>Пункт выпадающего фильтра: иконка, подпись и пилюля со счётчиком.</summary>
public sealed class TaskFilterItem(TaskFilterKind kind, int count)
{
    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    public TaskFilterKind Kind  => kind;
    public string         Label => TaskFilters.Label(kind);

    /// <summary>Есть что показывать. Ноль пилюли не получает вовсе: пустой пункт читается
    /// как «нечего смотреть», а «0» в рамке выглядел бы значением, на которое стоит нажать.</summary>
    public bool HasCount => count > 0;

    /// <summary>Счётчик с разделителем разрядов («1 000»), как остальные количества в
    /// приложении: четырёхзначное число сплошняком читается заметно хуже.</summary>
    public string CountText => count.ToString("N0");

    public Geometry? Icon =>
        Application.Current!.TryFindResource(TaskFilters.IconKey(kind), out var res) ? res as Geometry : null;

    /// <summary>Заливка пилюли — та же пара «светлая подложка + насыщенный текст», что у
    /// чипов статуса на карточках задач: просрочка красная, выполненные зелёные, остальные
    /// серые цветом наведения (`BrushHover`) — тем же, каким подсвечивается строка под
    /// курсором, поэтому пилюля читается на сером фоне списка и не спорит с ним.</summary>
    public IBrush CountBg => kind switch
    {
        TaskFilterKind.Overdue => Brush("BrushRedLight"),
        TaskFilterKind.Done    => Brush("BrushGreenLight"),
        _                      => Brush("BrushHover"),
    };

    /// <summary>Число — цвета обычного текста; цветным остаётся там, где цвет и есть смысл:
    /// просрочка и выполненные.</summary>
    public IBrush CountFg => kind switch
    {
        TaskFilterKind.Overdue => Brush("BrushRed"),
        TaskFilterKind.Done    => Brush("BrushGreen"),
        _                      => Brush("BrushPrimaryText"),
    };

    /// <summary>Пустой пункт приглушён целиком — и подпись, и иконка: по бледной строке
    /// видно, что смотреть там нечего, ещё до того как искать глазами счётчик.</summary>
    public IBrush LabelBrush => HasCount ? Brush("BrushPrimaryText") : Brush("BrushSecondaryText");

    /// <summary>Иконка непустого пункта — того же цвета, что число (у просрочки и выполненных
    /// свой); пустого — приглушена вместе с подписью.</summary>
    public IBrush IconBrush => HasCount ? CountFg : Brush("BrushSecondaryText");
}
