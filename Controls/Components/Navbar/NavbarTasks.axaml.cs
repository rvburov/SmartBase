using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Views.Tasks;

namespace SmartBase.Controls.Components;

public partial class NavbarTasks : UserControl
{
    private static readonly string[] ViewNames = ["Список"];

    public event EventHandler<int>? ViewChanged;
    public event EventHandler<string>? SearchChanged;
    /// <summary>Выбран быстрый фильтр (пункт выпадающего списка рядом с поиском).</summary>
    public event EventHandler<TaskFilterKind>? FilterChanged;

    // Гасит FilterChanged, когда выбор ставит код, а не человек (рассинхрон с контроллером).
    private bool _suppressFilterChange;

    // Пункты фильтра создаются ОДИН раз и живут до конца: счётчики меняются в самих
    // объектах (они наблюдаемые). Раньше SetFilterCounts пересобирал список и заново
    // назначал ItemsSource — а зовётся он в ответ на выбор пункта, то есть коллекция
    // менялась под руками ComboBox прямо в момент обработки клика. Список после этого
    // показывал верные числа, а свёрнутый бокс застревал на промежуточном состоянии:
    // выбрав пустой фильтр и вернувшись ко «Всем задачам», человек видел там «0».
    private readonly List<TaskFilterItem> _filterItems =
        TaskFilters.All.Select(k => new TaskFilterItem(k, 0)).ToList();

    public NavbarTasks()
    {
        InitializeComponent();

        ViewSelector.ItemsSource   = ViewNames;
        ViewSelector.SelectedIndex = 0;

        ViewSelector.SelectionChanged += (_, _) =>
            ViewChanged?.Invoke(this, ViewSelector.SelectedIndex);

        SearchBox.TextChanged += (_, _) =>
            SearchChanged?.Invoke(this, SearchBox.Text ?? "");

        FilterSelector.ItemsSource   = _filterItems;
        FilterSelector.SelectedIndex = 0;

        FilterSelector.SelectionChanged += (_, _) =>
        {
            if (_suppressFilterChange) return;
            if (FilterSelector.SelectedItem is TaskFilterItem item) FilterChanged?.Invoke(this, item.Kind);
        };
    }

    public void SetTitle(string text, IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }

    /// <summary>Обновить счётчики пунктов и вернуть выбор на <paramref name="selected"/>.</summary>
    public void SetFilterCounts(IReadOnlyDictionary<TaskFilterKind, int> counts, TaskFilterKind selected)
    {
        // Правим счётчики В САМИХ пунктах: список и его выбор не трогаем вовсе.
        foreach (var item in _filterItems)
            item.Count = counts.TryGetValue(item.Kind, out var n) ? n : 0;

        // Выбор двигаем, только если он и правда разошёлся с контроллером: обычно этот метод
        // зовётся как раз в ответ на выбор пункта, и трогать ComboBox в такой момент нельзя.
        var index = _filterItems.FindIndex(i => i.Kind == selected);
        if (index >= 0 && FilterSelector.SelectedIndex != index)
        {
            _suppressFilterChange = true;
            FilterSelector.SelectedIndex = index;
            _suppressFilterChange = false;
        }

        RefreshSelectionBox();
    }

    // Заставляет ComboBox перерисовать СВЁРНУТЫЙ бокс (то, что видно с закрытым списком).
    //
    // Содержимое бокса — снимок выбранного пункта, который контрол делает в момент смены
    // выбора; обновления самого пункта до него доходят не всегда, и бокс застревает на
    // прежнем значении, пока раскрытый список показывает верное («Мне назначено 5» в
    // списке и «0» в боксе после захода на пустой пункт). Снимаем и возвращаем выбор —
    // контрол перечитывает пункт заново.
    //
    // Отложенно: метод зовётся из обработки выбора, и дёргать контрол, пока он не закончил
    // с кликом, нельзя. К отрисовке кадра всё уже на месте, поэтому мигания не видно.
    private void RefreshSelectionBox() => Dispatcher.UIThread.Post(() =>
    {
        if (FilterSelector.SelectedItem is not { } item) return;

        _suppressFilterChange = true;
        FilterSelector.SelectedItem = null;
        FilterSelector.SelectedItem = item;
        _suppressFilterChange = false;
    }, DispatcherPriority.Render);
}

/// <summary>
/// Пункт выпадающего фильтра: иконка, подпись и пилюля со счётчиком. Счётчик наблюдаемый —
/// пункты живут всё время работы панели, и обновление числа обязано доходить до привязок
/// само, без пересоздания списка (см. <see cref="NavbarTasks.SetFilterCounts"/>).
/// </summary>
public sealed partial class TaskFilterItem : ObservableObject
{
    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    private readonly TaskFilterKind _kind;

    public TaskFilterItem(TaskFilterKind kind, int count)
    {
        _kind  = kind;
        _count = count;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(LabelBrush))]
    [NotifyPropertyChangedFor(nameof(IconBrush))]
    private int _count;

    public TaskFilterKind Kind  => _kind;
    public string         Label => TaskFilters.Label(_kind);

    /// <summary>Есть что показывать. Ноль пилюли не получает вовсе: пустой пункт читается
    /// как «нечего смотреть», а «0» в рамке выглядел бы значением, на которое стоит нажать.</summary>
    public bool HasCount => Count > 0;

    /// <summary>Счётчик с разделителем разрядов («1 000»), как остальные количества в
    /// приложении: четырёхзначное число сплошняком читается заметно хуже.</summary>
    public string CountText => Count.ToString("N0");

    public Geometry? Icon =>
        Application.Current!.TryFindResource(TaskFilters.IconKey(_kind), out var res) ? res as Geometry : null;

    /// <summary>Заливка пилюли — та же пара «светлая подложка + насыщенный текст», что у
    /// чипов статуса на карточках задач: просрочка красная, выполненные зелёные, остальные
    /// серые цветом наведения (`BrushHover`) — тем же, каким подсвечивается строка под
    /// курсором, поэтому пилюля читается на сером фоне списка и не спорит с ним.</summary>
    public IBrush CountBg => _kind switch
    {
        TaskFilterKind.Overdue => Brush("BrushRedLight"),
        TaskFilterKind.Done    => Brush("BrushGreenLight"),
        _                      => Brush("BrushHover"),
    };

    /// <summary>Число — цвета обычного текста; цветным остаётся там, где цвет и есть смысл:
    /// просрочка и выполненные.</summary>
    public IBrush CountFg => _kind switch
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
