using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Database.Repositories;
using SmartBase.Settings;
using SmartBase.Utils;
using SmartBase.Views.Tasks;

namespace SmartBase.Controls.Components;

public partial class NavbarTasks : UserControl
{
    // Только виды, не скрытые в «Оформлении»; ключи и подписи — в ViewCatalog.
    private readonly ViewSwitcher _views;

    /// <summary>Человек выбрал вид (ключ каталога). Подписчиков пока нет: вид у раздела один.
    /// Ключ, а не позиция в списке: позиция сдвигается, когда вид перед ней скрывают.</summary>
    public event EventHandler<string>? ViewChanged;
    public event EventHandler<string>? SearchChanged;
    /// <summary>Выбран быстрый фильтр (пункт выпадающего списка рядом с поиском).</summary>
    public event EventHandler<TaskFilter>? FilterChanged;

    // Гасит FilterChanged, когда выбор ставит код, а не человек (рассинхрон с контроллером).
    private bool _suppressFilterChange;

    // Пункты фильтра живут долго: счетчики меняются в самих объектах (они наблюдаемые).
    // Раньше SetFilterCounts пересобирал список и заново назначал ItemsSource — а зовется он
    // в ответ на выбор пункта, то есть коллекция менялась под руками ComboBox прямо в момент
    // обработки клика. Список после этого показывал верные числа, а свернутый бокс застревал
    // на промежуточном состоянии: выбрав пустой фильтр и вернувшись ко «Всем задачам», человек
    // видел там «0».
    //
    // Список из двух групп через черту: стандартные пункты стоят с рождения панели, хвост
    // стадий подменяется в SetStages — и только когда набор стадий сменился (см. там же).
    private readonly ObservableCollection<TaskFilterItem> _filterItems =
        new(TaskFilters.Standard.Select(f => new TaskFilterItem(f, 0)));

    public NavbarTasks()
    {
        InitializeComponent();

        // Стартовый вид из настроек; если он скрыт — показываем первый видимый.
        _views = new ViewSwitcher(ViewSelector, ViewCatalog.ScopeTasks, AppSettings.LoadTasksView);
        _views.Changed += key => ViewChanged?.Invoke(this, key);

        SearchBox.TextChanged += (_, _) =>
            SearchChanged?.Invoke(this, SearchBox.Text ?? "");

        FilterSelector.ItemsSource   = _filterItems;
        FilterSelector.SelectedIndex = 0;

        FilterSelector.SelectionChanged += (_, _) =>
        {
            if (_suppressFilterChange) return;
            if (FilterSelector.SelectedItem is TaskFilterItem item) FilterChanged?.Invoke(this, item.Filter);
        };
    }

    public void SetTitle(string text, IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }

    /// <summary>Перечитать «Отображаемые виды» (их сменили в настройках).</summary>
    public void ApplyViewVisibility() => _views.Refresh();

    /// <summary>Стадии, показанные сейчас в группе стадий (в порядке списка).</summary>
    public IReadOnlyList<string> Stages =>
        _filterItems.Where(i => i.Filter.IsStage).Select(i => i.Filter.StageName).ToList();

    /// <summary>
    /// Подменить хвост стадий. Пересобирается ТОЛЬКО он и только при смене набора: стандартные
    /// пункты и их счетчики остаются на месте, выбранный пункт переживает пересборку по ключу.
    /// Выбранная стадия пропала (ее убрали из поля «Статус» или сняли галочку с коллекции) —
    /// выбор уходит на «Все задачи» и об этом сообщается контроллеру.
    ///
    /// Звать из загрузки раздела, а не из обработчика выбора: коллекцию под руками ComboBox в
    /// момент клика менять нельзя (см. комментарий к <see cref="_filterItems"/>).
    /// </summary>
    public void SetStages(IReadOnlyList<string> stages)
    {
        if (Stages.SequenceEqual(stages, StringComparer.Ordinal)) return;

        var selectedKey = (FilterSelector.SelectedItem as TaskFilterItem)?.Filter.Key;

        _suppressFilterChange = true;
        for (var i = _filterItems.Count - 1; i >= 0; i--)
            if (_filterItems[i].Filter.IsStage) _filterItems.RemoveAt(i);

        var first = true;
        foreach (var stage in stages)
        {
            _filterItems.Add(new TaskFilterItem(TaskFilter.ForStage(stage), 0, startsGroup: first));
            first = false;
        }

        var index = selectedKey is null ? -1 : IndexOfKey(selectedKey);
        FilterSelector.SelectedIndex = index >= 0 ? index : 0;
        _suppressFilterChange = false;

        if (index < 0 && selectedKey is not null && selectedKey != TaskFilter.All.Key)
            FilterChanged?.Invoke(this, TaskFilter.All);

        RefreshSelectionBox();
    }

    /// <summary>Выбрать пункт программно (стартовый фильтр из настроек) — без FilterChanged.
    /// false — такого пункта нет (стадия еще не известна или пропала).</summary>
    public bool SelectFilter(TaskFilter filter)
    {
        var index = IndexOfKey(filter.Key);
        if (index < 0) return false;

        if (FilterSelector.SelectedIndex != index)
        {
            _suppressFilterChange = true;
            FilterSelector.SelectedIndex = index;
            _suppressFilterChange = false;
        }
        RefreshSelectionBox();
        return true;
    }

    private int IndexOfKey(string key)
    {
        for (var i = 0; i < _filterItems.Count; i++)
            if (_filterItems[i].Filter.Key == key) return i;
        return -1;
    }

    /// <summary>Обновить счетчики пунктов и вернуть выбор на <paramref name="selected"/>.</summary>
    public void SetFilterCounts(IReadOnlyDictionary<TaskFilter, int> counts, TaskFilter selected)
    {
        // Правим счетчики В САМИХ пунктах: список и его выбор не трогаем вовсе.
        foreach (var item in _filterItems)
            item.Count = counts.TryGetValue(item.Filter, out var n) ? n : 0;

        // Выбор двигаем, только если он и правда разошелся с контроллером: обычно этот метод
        // зовется как раз в ответ на выбор пункта, и трогать ComboBox в такой момент нельзя.
        var index = IndexOfKey(selected.Key);
        if (index >= 0 && FilterSelector.SelectedIndex != index)
        {
            _suppressFilterChange = true;
            FilterSelector.SelectedIndex = index;
            _suppressFilterChange = false;
        }

        RefreshSelectionBox();
    }

    // Заставляет ComboBox перерисовать СВЕРНУТЫЙ бокс (то, что видно с закрытым списком).
    //
    // Содержимое бокса — снимок выбранного пункта, который контрол делает в момент смены
    // выбора; обновления самого пункта до него доходят не всегда, и бокс застревает на
    // прежнем значении, пока раскрытый список показывает верное («Мне назначено 5» в
    // списке и «0» в боксе после захода на пустой пункт). Снимаем и возвращаем выбор —
    // контрол перечитывает пункт заново.
    //
    // Отложенно: метод зовется из обработки выбора, и дергать контрол, пока он не закончил
    // с кликом, нельзя. К отрисовке кадра все уже на месте, поэтому мигания не видно.
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
/// Пункт выпадающего фильтра: иконка, подпись и пилюля со счетчиком. Счетчик наблюдаемый —
/// пункты живут все время работы панели, и обновление числа обязано доходить до привязок
/// само, без пересоздания списка (см. <see cref="NavbarTasks.SetFilterCounts"/>).
/// </summary>
public sealed partial class TaskFilterItem : ObservableObject
{
    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;

    /// <param name="startsGroup">Первый пункт группы стадий — над ним рисуется разделительная
    /// черта (шаблон контейнера в NavbarTasks.axaml, как у типов свойств).</param>
    public TaskFilterItem(TaskFilter filter, int count, bool startsGroup = false)
    {
        Filter      = filter;
        StartsGroup = startsGroup;
        _count      = count;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount))]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(LabelBrush))]
    [NotifyPropertyChangedFor(nameof(IconBrush))]
    private int _count;

    public TaskFilter     Filter      { get; }
    public TaskFilterKind Kind        => Filter.Kind;
    public bool           StartsGroup { get; }
    public string         Label       => Filter.Label;

    /// <summary>Есть что показывать. Ноль пилюли не получает вовсе: пустой пункт читается
    /// как «нечего смотреть», а «0» в рамке выглядел бы значением, на которое стоит нажать.</summary>
    public bool HasCount => Count > 0;

    /// <summary>Потолок показа: больше него счетчик печатается как «999+».</summary>
    public const int CountCap = 999;

    /// <summary>
    /// Счетчик пункта. Свыше <see cref="CountCap"/> — «999+»: пилюля стоит в свернутом боксе
    /// навбара рядом с поиском, и точное четырех-пятизначное число там только распирает строку,
    /// ничего не сообщая — по нему все равно не считают, по нему решают, стоит ли заходить.
    /// Разделитель разрядов после этого не нужен: до потолка число всегда трехзначное.
    ///
    /// Точное количество не пропадает: заголовок коллекции в списке называет его полностью
    /// (<c>TaskGroupHeaderVm.ProgressText</c>) — там для этого есть место.
    /// </summary>
    public string CountText => Count > CountCap ? $"{CountCap}+" : Count.ToString();

    public Geometry? Icon =>
        Application.Current!.TryFindResource(Filter.IconKey, out var res) ? res as Geometry : null;

    // Цвет пункта: просрочка красная, стадия «Выполнено» зеленая, «В работе» акцентная —
    // ровно как чипы стадий на карточках; остальное серое.
    private enum Tone { Plain, Red, Green, Accent }

    private Tone ToneOf => Filter.Kind == TaskFilterKind.Overdue
        ? Tone.Red
        : Filter.IsStage
            ? Filter.StageName switch
            {
                ConstructorRepository.TaskStatusDone   => Tone.Green,
                ConstructorRepository.TaskStatusActive => Tone.Accent,
                _                                      => Tone.Plain,
            }
            : Tone.Plain;

    /// <summary>Заливка пилюли — та же пара «светлая подложка + насыщенный текст», что у
    /// чипов статуса на карточках задач: просрочка красная, «Выполнено» зеленое, «В работе»
    /// акцентное, остальные серые цветом наведения (`BrushHover`) — тем же, каким подсвечивается
    /// строка под курсором, поэтому пилюля читается на сером фоне списка и не спорит с ним.</summary>
    public IBrush CountBg => ToneOf switch
    {
        Tone.Red    => Brush("BrushRedLight"),
        Tone.Green  => Brush("BrushGreenLight"),
        Tone.Accent => Brush("BrushAccentLight"),
        _           => Brush("BrushHover"),
    };

    /// <summary>Число — цвета обычного текста; цветным остается там, где цвет и есть смысл.</summary>
    public IBrush CountFg => ToneOf switch
    {
        Tone.Red    => Brush("BrushRed"),
        Tone.Green  => Brush("BrushGreen"),
        Tone.Accent => Brush("BrushAccent"),
        _           => Brush("BrushPrimaryText"),
    };

    /// <summary>Пустой пункт приглушен целиком — и подпись, и иконка: по бледной строке
    /// видно, что смотреть там нечего, еще до того как искать глазами счетчик.</summary>
    public IBrush LabelBrush => HasCount ? Brush("BrushPrimaryText") : Brush("BrushSecondaryText");

    /// <summary>Иконка непустого пункта — того же цвета, что число (у просрочки и стадий
    /// свой); пустого — приглушена вместе с подписью.</summary>
    public IBrush IconBrush => HasCount ? CountFg : Brush("BrushSecondaryText");
}
