using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.Tasks;

/// <summary>Коллекция-цель в строке быстрого добавления. <c>ColId</c> = null — коллекций
/// нет вовсе, репозиторий заведёт «Без названия» (прежнее поведение).</summary>
public sealed record QuickCollectionOption(int DbId, int? ColId, string Label)
{
    // ComboBox без ItemTemplate печатает ToString() — отдаём готовую подпись.
    public override string ToString() => Label;
}

/// <param name="Target">Выбранная коллекция; null — цель определяет контроллер сам.</param>
public record QuickAddRequest(string Title, DateValue? Due, string Executor, QuickCollectionOption? Target);

public partial class TaskListView : UserControl
{
    public event EventHandler<TaskRecord>?      TaskActivated;
    public event EventHandler<QuickAddRequest>? QuickAddRequested;
    /// <summary>Сменилась коллекция-цель быстрого добавления: у неё может быть своя база,
    /// а значит и свой список пользователей для «Исполнителя».</summary>
    public event EventHandler<QuickCollectionOption>? QuickTargetChanged;
    /// <summary>Удалить выделенные задачи (меню или Delete) — подтверждение спрашивает контроллер.</summary>
    public event EventHandler<IReadOnlyList<TaskRecord>>? TasksDeleteRequested;
    /// <summary>Дублировать выделенные задачи (меню).</summary>
    public event EventHandler<IReadOnlyList<TaskRecord>>? TasksDuplicateRequested;

    // Гасит QuickTargetChanged, пока список коллекций пересобирается (смена ItemsSource
    // сбрасывает выбор — это не выбор пользователя).
    private bool _suppressTargetChange;

    // Показанные группы и свёрнутые коллекции (ключ (DbId, ColId) — как в сайдбаре).
    // Свёрнутость живёт в самом списке: это состояние показа, база о нём ничего не знает.
    private IReadOnlyList<TaskGroup> _groups = [];
    private readonly HashSet<(int DbId, int ColId)> _collapsed = [];
    private string _emptyHint = "Нет задач";

    // Строки списка (заголовки групп + карточки) и отдельно — только показанные задачи
    // в порядке показа: по нему считается диапазон Ctrl+Shift и состав выделения.
    private List<object>     _items   = [];
    private List<TaskRecord> _visible = [];
    private readonly TaskSelection _selection = new();

    public TaskListView()
    {
        InitializeComponent();

        // Список смешанный: заголовок коллекции + её карточки, поэтому шаблон один на оба
        // типа строки (виртуализация строит контейнеры по общему ItemTemplate).
        CardsHost.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            TaskGroupHeaderVm header => BuildGroupHeader(header),
            TaskCardVm card          => BuildCard(card),
            _                        => new Control(),
        }, supportsRecycling: false);

        // Срок по умолчанию — вечер текущего дня; сама дата пуста («без даты»).
        QuickTimeField.Time = new TimeSpan(18, 0, 0);

        QuickTitle.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            FireQuickAdd();
        };
        QuickAddBtn.Click += (_, _) => FireQuickAdd();

        QuickCollection.SelectionChanged += (_, _) =>
        {
            if (_suppressTargetChange) return;
            if (QuickCollection.SelectedItem is QuickCollectionOption o) QuickTargetChanged?.Invoke(this, o);
        };
    }

    // ── Публичный API (для контроллера) ──────────────────────────────────────

    /// <summary>
    /// Показать задачи, разложенные по коллекциям: над каждой пачкой — строка с названием
    /// коллекции и числом задач. Порядок групп задаёт контроллер (как в сайдбаре).
    /// <paramref name="emptyHint"/> — что написать, когда показывать нечего.
    /// </summary>
    public void SetGroups(IReadOnlyList<TaskGroup> groups, string emptyHint = "Нет задач")
    {
        _groups    = groups;
        _emptyHint = emptyHint;

        // Свёрнутость помним по (DbId, ColId), а не по индексу: список перезагружается на
        // каждый символ поиска и после любой правки — группы не должны раскрываться сами.
        // Ключи исчезнувших коллекций чистим, иначе набор растёт всё время работы.
        var alive = groups.Select(g => (g.DbId, g.ColId)).ToHashSet();
        _collapsed.RemoveWhere(k => !alive.Contains(k));

        // Выделение переживает перезагрузку (правка задачи, поиск), но задачи, ушедшие из
        // набора, из него выпадают: «Удалить» должно действовать на видимое.
        _selection.Retain(groups.SelectMany(g => g.Tasks));

        RebuildItems();
    }

    private void RebuildItems()
    {
        var now     = DateTime.Now;
        var items   = new List<object>();
        var visible = new List<TaskRecord>();
        foreach (var g in _groups)
        {
            var collapsed = _collapsed.Contains((g.DbId, g.ColId));
            items.Add(new TaskGroupHeaderVm(g, collapsed, first: items.Count == 0));
            if (collapsed) continue;
            foreach (var t in g.Tasks)
            {
                items.Add(new TaskCardVm(t, now) { IsSelected = _selection.Contains(t) });
                visible.Add(t);
            }
        }

        _items   = items;
        _visible = visible;

        CardsHost.ItemsSource = items;
        EmptyLabel.Text       = _emptyHint;
        // Свёрнутая группа — не пустой список: подсказку показываем, только когда групп нет.
        EmptyLabel.IsVisible  = _groups.Count == 0;
    }

    // ── Выделение карточек ───────────────────────────────────────────────────

    // Перекрасить карточки под текущее выделение. Идём по моделям строк, а не по контролам:
    // список виртуализирован, и карточки за краем экрана контролов сейчас не имеют — новые
    // построятся уже с нужным IsSelected.
    private void SyncSelection()
    {
        foreach (var item in _items)
            if (item is TaskCardVm card) card.IsSelected = _selection.Contains(card.Task);
    }

    private void OnCardTapped(TaskCardVm vm, TappedEventArgs e)
    {
        e.Handled = true;

        // Ctrl+Shift+клик — диапазон от якоря; якоря нет (первый клик после загрузки) —
        // ведём себя как обычный клик.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && _selection.SelectRange(_visible, vm.Task))
        {
            SyncSelection();
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _selection.Toggle(vm.Task);
        else                                            _selection.SelectOnly(vm.Task);
        SyncSelection();
    }

    // Правый клик по НЕвыделенной карточке выделяет её одну: меню всегда действует на то,
    // что видно выделенным (как в календаре).
    private void OnCardPointerPressed(TaskCardVm vm, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (_selection.Contains(vm.Task)) return;
        _selection.SelectOnly(vm.Task);
        SyncSelection();
    }

    private void OnCardContextRequested(Control card, TaskCardVm vm, ContextRequestedEventArgs e)
    {
        var selected = _selection.In(_visible);

        // Выделение НЕ снимаем: пока открыт диалог подтверждения, видно, ЧТО удаляется или
        // дублируется (после «Отмена» карточки остаются подсвеченными).
        var menu = selected.Count > 1 && _selection.Contains(vm.Task)
            ? Controls.Components.EventContextMenu.Multi(
                () => TasksDeleteRequested?.Invoke(this, selected),
                onDuplicateAll: () => TasksDuplicateRequested?.Invoke(this, selected))
            : Controls.Components.EventContextMenu.Single(
                onEdit:      () => TaskActivated?.Invoke(this, vm.Task),
                onDelete:    () => TasksDeleteRequested?.Invoke(this, [vm.Task]),
                onDuplicate: () => TasksDuplicateRequested?.Invoke(this, [vm.Task]));

        menu.Open(card);
        e.Handled = true;
    }

    // ── Сброс выделения и Delete ─────────────────────────────────────────────

    private EventHandler<KeyEventArgs>? _windowKeyHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Обработчики вешаем на КОРЕНЬ из события, а не на GetTopLevel(this): при отсоединении
        // контрол уже вне дерева и снять их с найденного «на лету» окна не вышло бы.
        if (e.Root is TopLevel top)
            top.AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        if (e.Root is Window win)
        {
            _windowKeyHandler = (_, ke) =>
            {
                if (ke.Key is not (Key.Delete or Key.Back)) return;
                // Раздел скрыт (открыт календарь/конструктор) — клавиша не наша.
                if (!IsEffectivelyVisible) return;
                // Ввод: название новой задачи, поиск в навбаре, поля срока — там Delete стирает текст.
                // ComboBox тоже пропускаем: клавиша в списке коллекций/исполнителя не про задачи.
                if (ke.Source is TextBox or AutoCompleteBox or ComboBox) return;

                var selected = _selection.In(_visible);
                if (selected.Count == 0) return;
                TasksDeleteRequested?.Invoke(this, selected);
                ke.Handled = true;
            };
            win.AddHandler(KeyDownEvent, _windowKeyHandler, handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is TopLevel top)
            top.RemoveHandler(PointerPressedEvent, OnGlobalPointerPressed);
        if (e.Root is Window win && _windowKeyHandler is not null)
        {
            win.RemoveHandler(KeyDownEvent, _windowKeyHandler);
            _windowKeyHandler = null;
        }
    }

    // Клик мимо карточек (в т.ч. по заголовку группы и вне списка) снимает выделение.
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Правый клик не сбрасывает: иначе меню открылось бы над снятым выделением.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        // Shift+клик — аддитивное выделение, его собирает сама карточка.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        if (_selection.Clear()) SyncSelection();
    }

    private void ToggleGroup(TaskGroup group)
    {
        var key = (group.DbId, group.ColId);
        if (!_collapsed.Remove(key)) _collapsed.Add(key);
        RebuildItems();
    }

    /// <summary>
    /// Коллекции строки быстрого добавления. Выбор пользователя сохраняется между
    /// обновлениями (список пересобирается на каждую загрузку), пока выбранная коллекция
    /// жива; исчезла — возвращаемся к <paramref name="fallback"/>, цели по умолчанию.
    /// </summary>
    public void SetCollectionOptions(IReadOnlyList<QuickCollectionOption> options,
                                     QuickCollectionOption? fallback)
    {
        var keep = QuickCollection.SelectedItem as QuickCollectionOption;

        _suppressTargetChange = true;
        QuickCollection.ItemsSource = options;

        var idx = keep is not null
            ? options.ToList().FindIndex(o => o.DbId == keep.DbId && o.ColId == keep.ColId)
            : -1;
        if (idx < 0 && fallback is not null)
            idx = options.ToList().FindIndex(o => o.DbId == fallback.DbId && o.ColId == fallback.ColId);

        QuickCollection.SelectedIndex = idx >= 0 ? idx : (options.Count > 0 ? 0 : -1);
        _suppressTargetChange = false;
    }

    /// <summary>Коллекция, выбранная в строке быстрого добавления (null — список пуст).</summary>
    public QuickCollectionOption? SelectedCollection => QuickCollection.SelectedItem as QuickCollectionOption;

    // Опции исполнителя строки быстрого добавления (пользователи целевой БД).
    public void SetUserOptions(IReadOnlyList<string> options, string selected)
    {
        var opts = options.Count > 0 ? options.ToList() : [CurrentUser.DisplayName];
        if (selected.Length > 0 && !opts.Contains(selected)) opts.Add(selected);
        var keep = QuickExecutor.SelectedItem as string;
        QuickExecutor.ItemsSource = opts;
        var idx = keep is not null ? opts.IndexOf(keep) : -1;
        if (idx < 0) idx = opts.IndexOf(selected);
        QuickExecutor.SelectedIndex = idx >= 0 ? idx : 0;
    }

    public void ShowError(string message) => NoticeBanner.Show(message);
    public void HideError()               { if (NoticeBanner.IsVisible) NoticeBanner.Hide(); }

    // ── Быстрое добавление ───────────────────────────────────────────────────

    private void FireQuickAdd()
    {
        HideError();
        var title = QuickTitle.Text?.Trim() ?? "";
        if (title.Length == 0) return;

        // Дата не выбрана — задача «без даты». Время пустое/нечитаемое — весь день.
        DateValue? due = null;
        if (QuickDateField.Date is DateTime day)
        {
            due = QuickTimeField.TimeOrNull is TimeSpan t
                ? new DateValue(day.Date + t, day.Date + t + TimeSpan.FromHours(1), false)
                : new DateValue(day.Date, day.Date + new TimeSpan(23, 59, 0), true);
        }

        var executor = QuickExecutor.SelectedItem as string ?? CurrentUser.DisplayName;
        QuickAddRequested?.Invoke(this, new QuickAddRequest(title, due, executor, SelectedCollection));
        QuickTitle.Text = "";
    }

    // ── Заголовок группы (коллекция) ─────────────────────────────────────────

    private Control BuildGroupHeader(TaskGroupHeaderVm vm)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var large     = (double)this.FindResource("FontSizeLarge")!;

        var row = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Стрелка — та же, что у групп и баз в сайдбаре (Viewbox 24×24 + Path обводкой):
        // раскрытая коллекция смотрит вниз, свёрнутая — вправо.
        row.Children.Add(Chevron(vm.IsCollapsed ? "IconChevronRight" : "IconChevronDown", secondary));

        row.Children.Add(new TextBlock
        {
            Text              = vm.Title,
            FontSize          = large,
            FontWeight        = (FontWeight)this.FindResource("FontWeightBold")!,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // База показывается, только когда их несколько (иначе шум): «· Основная».
        if (vm.Subtitle.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = $"· {vm.Subtitle}", Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // Индикатор выполненного: полоска цветом коллекции + «2 из 7». Голое число задач
        // не отвечало на главный вопрос к списку дел — сколько из них уже закрыто.
        row.Children.Add(ProgressBar(vm.DoneFraction, vm.Color));
        row.Children.Add(new TextBlock
        {
            Text = vm.ProgressText, Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Кликом сворачивается вся строка, а не одна стрелка, — как строка группы в сайдбаре.
        // Первая группа стоит вплотную к строке быстрого добавления, следующие отбиваются
        // воздухом сверху: видно, где кончаются задачи предыдущей коллекции.
        var header = new Border
        {
            Background = Brushes.Transparent,
            Padding    = new Thickness(0, 2),
            Margin     = new Thickness(0, vm.IsFirst ? 0 : 12, 0, 8),
            Cursor     = new Cursor(StandardCursorType.Hand),
            Child      = row,
        };
        header.Tapped += (_, e) => { e.Handled = true; ToggleGroup(vm.Group); };
        return header;
    }

    // Полоска выполненного: серый трек фиксированной ширины и заливка цветом коллекции.
    // Ширина в DIP — общий зум приложения масштабирует её вместе с остальным.
    private Control ProgressBar(double fraction, string color)
    {
        const double width = 96, height = 8;

        var fill = new Border
        {
            // Ненулевой прогресс всегда виден: доля в пару процентов иначе схлопывалась
            // бы в невидимую полоску и читалась как «ничего не сделано».
            Width               = fraction <= 0 ? 0 : Math.Max(height, Math.Round(width * fraction)),
            Height              = height,
            CornerRadius        = new CornerRadius(99),
            Background          = new SolidColorBrush(Color.Parse(color)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        return new Border
        {
            Width             = width,
            Height            = height,
            CornerRadius      = new CornerRadius(99),
            Background        = (IBrush)this.FindResource("BrushSecondaryBg")!,
            VerticalAlignment = VerticalAlignment.Center,
            Child             = fill,
        };
    }

    // Стрелка сайдбара: геометрия из общего реестра иконок, обводка вторичным цветом.
    private static Control Chevron(string iconKey, IBrush stroke)
    {
        var app       = Application.Current!;
        var geometry  = app.TryFindResource(iconKey, out var res) ? res as Geometry : null;
        var size      = app.TryFindResource("IconSizeSmall",   out var s) && s is double d  ? d  : 16;
        var thickness = app.TryFindResource("IconStrokeSmall", out var t) && t is double th ? th : 1.2;

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data            = geometry,
            Stroke          = stroke,
            StrokeThickness = thickness,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        });

        return new Viewbox
        {
            Width = size, Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas,
        };
    }

    // ── Карточка задачи ──────────────────────────────────────────────────────

    private Control BuildCard(TaskCardVm vm)
    {
        // При переиспользовании контейнера виртуализацией контент временно = null
        // (ContentPresenter строит по явному ItemTemplate без проверки типа) — отдаём пустую карточку.
        if (vm is null) return new Control();

        var bold      = (FontWeight)this.FindResource("FontWeightBold")!;
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var primary   = (IBrush)this.FindResource("BrushPrimaryText")!;
        var radius    = (CornerRadius)this.FindResource("RadiusMedium")!;

        // Левая колонка: название + «Исполнитель X · Назначил Y».
        var title = new TextBlock
        {
            Text         = vm.Task.DisplayTitle,
            FontWeight   = bold,
            Foreground   = vm.IsDone ? secondary : primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (vm.IsDone) title.TextDecorations = TextDecorations.Strikethrough;

        // Обе строки показываются ВСЕГДА, пустое значение — прочерком: в разделе теперь живут и
        // нераспределённые записи (запись есть, исполнитель не проставлен), и именно их надо
        // раздать. Пропуск «Назначил» при пустом значении сдвигал бы строку и делал незаполненное
        // невидимым — а «—» прямо говорит, чего не хватает.
        var people = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        people.Children.Add(new TextBlock { Text = "Исполнитель", Foreground = secondary });
        people.Children.Add(new TextBlock { Text = vm.ExecutorText, Foreground = secondary, FontWeight = bold });
        people.Children.Add(new TextBlock { Text = "·", Foreground = secondary, Margin = new Thickness(4, 0) });
        people.Children.Add(new TextBlock { Text = "Назначил", Foreground = secondary });
        people.Children.Add(new TextBlock { Text = vm.AssignerText, Foreground = secondary, FontWeight = bold });

        var left = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        left.Children.Add(people);

        // Правая колонка: срок сверху, чипы (статус + остаток) снизу.
        var due = new TextBlock
        {
            Text                = vm.DueText,
            Foreground          = vm.Task.DueStart is null ? secondary : primary,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var chips = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        chips.Children.Add(Chip(vm.StatusText, vm.StatusBg, vm.StatusFg));
        if (vm.DueChipText is not null)
            chips.Children.Add(Chip(vm.DueChipText, vm.DueChipBg!, vm.DueChipFg!));

        var right = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(due);
        right.Children.Add(chips);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*,12,Auto") };
        // Цветовая полоска коллекции — как в списке календаря.
        var stripe = new Border
        {
            Width = 4, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.Parse(
                string.IsNullOrWhiteSpace(vm.Task.Color) ? "#8E8E93" :
                vm.Task.Color == "#007AFF" ? "#8E8E93" : vm.Task.Color)),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(stripe, 0);
        Grid.SetColumn(left, 2);
        Grid.SetColumn(right, 4);
        grid.Children.Add(stripe);
        grid.Children.Add(left);
        grid.Children.Add(right);

        var card = new Border
        {
            CornerRadius = radius,
            Padding      = new Thickness(14, 10),
            Margin       = new Thickness(0, 0, 0, 8),
            Child        = grid,
        };
        card.Bind(Border.BorderThicknessProperty, card.GetResourceObservable("BorderLine"));

        // Фон и рамка — от модели строки: наведение и выделение меняются без пересборки
        // списка, а вернувшаяся из-за края экрана карточка сразу рисуется выделенной.
        card.Bind(Border.BackgroundProperty,
                  new Binding(nameof(TaskCardVm.BackgroundBrush)) { Source = vm });
        card.Bind(Border.BorderBrushProperty,
                  new Binding(nameof(TaskCardVm.BorderBrushValue)) { Source = vm });

        card.PointerEntered += (_, _) => vm.IsHovered = true;
        card.PointerExited  += (_, _) => vm.IsHovered = false;
        card.PointerPressed += (_, e) => OnCardPointerPressed(vm, e);
        card.Tapped         += (_, e) => OnCardTapped(vm, e);
        card.ContextRequested += (_, e) => OnCardContextRequested(card, vm, e);
        card.DoubleTapped   += (_, e) =>
        {
            e.Handled = true;
            // Двойной клик тоже оставляет задачу выделенной: после «Сохранить»/«Отмена»
            // видно, какую карточку правили (одиночный клик уже выделил её — здесь ничего
            // не сбрасываем).
            TaskActivated?.Invoke(this, vm.Task);
        };
        return card;
    }

    private static Border Chip(string text, IBrush bg, IBrush fg) => new()
    {
        Background   = bg,
        CornerRadius = new CornerRadius(99),
        Padding      = new Thickness(10, 3),
        Child        = new TextBlock { Text = text, Foreground = fg, FontWeight = FontWeight.SemiBold },
    };
}

/// <summary>Группа списка: коллекция и её задачи (порядок задаёт контроллер).</summary>
/// <param name="DbId">База коллекции — вместе с <paramref name="ColId"/> ключ свёрнутости
/// (id коллекций не уникальны между БД).</param>
/// <param name="Title">Название коллекции (одной строкой).</param>
/// <param name="Subtitle">Имя базы — только при нескольких смонтированных БД, иначе "".</param>
/// <param name="Color">Цвет коллекции (правило «дефолтный синий → серый» уже применено) —
/// им заливается индикатор выполненных задач в заголовке.</param>
public sealed record TaskGroup(int DbId, int ColId, string Title, string Subtitle, string Color,
                               IReadOnlyList<TaskRecord> Tasks);

// ── Модель строки-заголовка группы ───────────────────────────────────────────
public sealed class TaskGroupHeaderVm(TaskGroup group, bool collapsed, bool first)
{
    public TaskGroup Group       => group;
    public string    Title       => group.Title;
    public string    Subtitle    => group.Subtitle;
    public string    Color       => group.Color;
    public bool      IsCollapsed => collapsed;
    public bool      IsFirst     => first;

    /// <summary>Сколько задач коллекции в статусе «Выполнено».</summary>
    public int DoneCount { get; } =
        group.Tasks.Count(t => t.Status == ConstructorRepository.TaskStatusDone);

    public int TotalCount => group.Tasks.Count;

    /// <summary>«2 из 7» — выполнено из всего (с разделителем разрядов, как счётчики фильтра).</summary>
    public string ProgressText => $"{DoneCount:N0} из {TotalCount:N0}";

    /// <summary>Доля выполненных 0..1 — длина заливки индикатора.</summary>
    public double DoneFraction => TotalCount > 0 ? (double)DoneCount / TotalCount : 0;
}

// ── Модель карточки: тексты и кисти чипов считаются один раз при загрузке ────
// Наведение и выделение — наблюдаемые: карточка привязывает к ним фон и рамку.
public partial class TaskCardVm : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    [NotifyPropertyChangedFor(nameof(BorderBrushValue))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private bool _isHovered;

    /// <summary>Фон карточки: выделение заметнее наведения (та же серая полупрозрачная
    /// заливка, что у выделенных строк календаря).</summary>
    public IBrush BackgroundBrush => IsSelected
        ? new SolidColorBrush(Color.FromArgb(50, 128, 128, 128))
        : IsHovered ? Brush("BrushInactiveBg", "#FAFAFA") : Brush("BrushBg", "#FFFFFF");

    /// <summary>Рамка: у выделенной карточки — акцентная (одной заливки мало, карточки и так
    /// стоят на светлом фоне).</summary>
    public IBrush BorderBrushValue => IsSelected
        ? Brush("BrushAccent", "#007AFF")
        : Brush("BrushSeparator", "#E5E5EA");

    private static IBrush Brush(string key, string fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b : new SolidColorBrush(Color.Parse(fallback));

    private static readonly string[] Months =
        ["янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    /// <summary>Прочерк вместо пустого значения — раздел показывает и нераспределённые записи.</summary>
    public const string Dash = "—";

    public TaskRecord Task { get; }
    public bool   IsDone      { get; }
    public string Executor    => Task.Executor;
    public string ExecutorText => Task.Executor.Length > 0 ? Task.Executor : Dash;
    public string AssignerText => Task.Assigner.Length > 0 ? Task.Assigner : Dash;
    public string StatusText  { get; }
    public IBrush StatusBg    { get; }
    public IBrush StatusFg    { get; }
    public string DueText     { get; }
    public string? DueChipText { get; }
    public IBrush? DueChipBg   { get; }
    public IBrush? DueChipFg   { get; }

    public TaskCardVm(TaskRecord task, DateTime now)
    {
        Task   = task;
        IsDone = task.Status == ConstructorRepository.TaskStatusDone;

        StatusText = task.Status.Length > 0 ? task.Status : ConstructorRepository.TaskStatusPlanned;
        (StatusBg, StatusFg) = StatusText switch
        {
            ConstructorRepository.TaskStatusActive => (Brush("BrushAccentLight", "#E5F0FF"), Brush("BrushAccent", "#007AFF")),
            ConstructorRepository.TaskStatusDone   => (Brush("BrushGreenLight", "#E3F7E9"), Brush("BrushGreen", "#34C759")),
            _ => (Brush("BrushSecondaryBg", "#F2F2F7"), Brush("BrushSecondaryText", "#8E8E93")),
        };

        DueText = FormatDue(task);

        var chip = DueChip(task, now);
        if (chip is { } c)
        {
            DueChipText = c.Text;
            DueChipBg   = c.Overdue ? Brush("BrushRedLight", "#FFE9E7") : Brush("BrushSecondaryBg", "#F2F2F7");
            DueChipFg   = c.Overdue ? Brush("BrushRed", "#FF3B30")      : Brush("BrushSecondaryText", "#8E8E93");
        }
    }

    private static string FormatDay(DateTime d) => $"{d.Day} {Months[d.Month - 1]}";

    // «5 июл, 15:30» / «5 июл» (весь день) / «5 июл – 7 июл» (диапазон) / «без даты».
    private static string FormatDue(TaskRecord t)
    {
        if (t.DueStart is not DateTime s) return "без даты";
        var e = t.DueEnd ?? s;
        if (s.Date != e.Date) return $"{FormatDay(s)} – {FormatDay(e)}";
        return t.AllDay ? FormatDay(s) : $"{FormatDay(s)}, {s.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    // Чип остатка: просрочка (красный) / «Сегодня» / «Осталось N ч|дн.». У выполненных —
    // только нейтральное «Сегодня», без просрочки и обратного отсчёта.
    private static (string Text, bool Overdue)? DueChip(TaskRecord t, DateTime now)
    {
        if (t.DueStart is not DateTime s) return null;
        var end = t.DueEnd ?? s;

        if (t.Status == ConstructorRepository.TaskStatusDone)
            return end.Date == now.Date ? ("Сегодня", false) : null;

        if (end <= now)
        {
            var days = (int)(now.Date - end.Date).TotalDays;
            if (days >= 1) return ($"Просрочено на {days} дн.", true);
            var hours = Math.Max(1, (int)Math.Ceiling((now - end).TotalHours));
            return ($"Просрочено на {hours} ч", true);
        }

        if (t.AllDay && end.Date == now.Date) return ("Сегодня", false);

        var left = end - now;
        if (left.TotalHours < 24)
            return ($"Осталось {Math.Max(1, (int)Math.Ceiling(left.TotalHours))} ч", false);
        return ($"Осталось {(int)Math.Ceiling((end.Date - now.Date).TotalDays)} дн.", false);
    }
}
