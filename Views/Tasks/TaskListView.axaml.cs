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
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.Tasks;

/// <summary>Коллекция-цель в строке быстрого добавления. <c>ColId</c> = null — коллекций
/// нет вовсе, репозиторий заведет «Без названия» (прежнее поведение).</summary>
public sealed record QuickCollectionOption(int DbId, int? ColId, string Label)
{
    // ComboBox без ItemTemplate печатает ToString() — отдаем готовую подпись.
    public override string ToString() => Label;
}

/// <param name="Target">Выбранная коллекция; null — цель определяет контроллер сам.</param>
public record QuickAddRequest(string Title, DateValue? Due, string Executor, QuickCollectionOption? Target);

public partial class TaskListView : UserControl
{
    public event EventHandler<TaskRecord>?      TaskActivated;
    public event EventHandler<QuickAddRequest>? QuickAddRequested;
    /// <summary>Сменилась коллекция-цель быстрого добавления: у нее может быть своя база,
    /// а значит и свой список пользователей для «Исполнителя».</summary>
    public event EventHandler<QuickCollectionOption>? QuickTargetChanged;
    /// <summary>Удалить выделенные задачи (меню или Delete) — подтверждение спрашивает контроллер.</summary>
    public event EventHandler<IReadOnlyList<TaskRecord>>? TasksDeleteRequested;
    /// <summary>Дублировать выделенные задачи (меню).</summary>
    public event EventHandler<IReadOnlyList<TaskRecord>>? TasksDuplicateRequested;

    // Гасит QuickTargetChanged, пока список коллекций пересобирается (смена ItemsSource
    // сбрасывает выбор — это не выбор пользователя).
    private bool _suppressTargetChange;

    // Показанные группы и свернутые коллекции (ключ (DbId, ColId) — как в сайдбаре).
    // Свернутость живет в самом списке: это состояние показа, база о нем ничего не знает.
    private IReadOnlyList<TaskGroup> _groups = [];
    private readonly HashSet<(int DbId, int ColId)> _collapsed = [];
    private string _emptyHint = "Нет задач";

    // Строки списка (заголовки групп + карточки) и отдельно — только показанные задачи
    // в порядке показа: по нему считается диапазон Ctrl+Shift и состав выделения.
    private List<object>     _items   = [];
    private List<TaskRecord> _visible = [];
    private readonly TaskSelection _selection = new();

    // Виртуальный источник — на базах, которые в память целиком не берут (см. TaskVirtualList).
    // Не null ⇒ _items/_visible не используются: строки живут в самом списке, и знать он может
    // только те, что сейчас в кеше страниц.
    private TaskVirtualList? _virtual;

    // Задачи в порядке показа, доступные ПРЯМО СЕЙЧАС. В виртуальном режиме это реализованные
    // строки: диапазон Ctrl+Shift и «Удалить» действуют на то, что человек видит, — за пределы
    // кеша страниц список не заглядывает и обещать там ничего не может.
    private List<TaskRecord> VisibleTasks => _virtual?.RealizedTasks() ?? _visible;

    public TaskListView()
    {
        InitializeComponent();

        // Список смешанный: заголовок коллекции + ее карточки, поэтому шаблон один на оба
        // типа строки (виртуализация строит контейнеры по общему ItemTemplate).
        CardsHost.ItemTemplate = new FuncDataTemplate<object>((item, _) => item switch
        {
            TaskGroupHeaderVm header => BuildGroupHeader(header),
            TaskCardVm card          => BuildCard(card),
            _                        => new Control(),
        }, supportsRecycling: false);

        // Время по умолчанию — вечер текущего дня; дата — по настройке «Срок в быстром
        // добавлении» (стандарт — пусто, «без даты»).
        QuickTimeField.Time = new TimeSpan(18, 0, 0);
        QuickDateField.Date = TaskSettings.QuickDueDate(DateTime.Today);

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
    /// коллекции и числом задач. Порядок групп задает контроллер (как в сайдбаре).
    /// <paramref name="emptyHint"/> — что написать, когда показывать нечего.
    /// </summary>
    public void SetGroups(IReadOnlyList<TaskGroup> groups, string emptyHint = "Нет задач")
    {
        _virtual?.Cancel();
        _virtual   = null;
        _entries   = [];
        _groups    = groups;
        _emptyHint = emptyHint;

        // Свернутость помним по (DbId, ColId), а не по индексу: список перезагружается на
        // каждый символ поиска и после любой правки — группы не должны раскрываться сами.
        // Ключи исчезнувших коллекций чистим, иначе набор растет все время работы.
        var alive = groups.Select(g => (g.DbId, g.ColId)).ToHashSet();
        _collapsed.RemoveWhere(k => !alive.Contains(k));

        // Выделение переживает перезагрузку (правка задачи, поиск), но задачи, ушедшие из
        // набора, из него выпадают: «Удалить» должно действовать на видимое.
        _selection.Retain(groups.SelectMany(g => g.Tasks));

        RebuildItems();
    }

    /// <summary>
    /// Показать раздел ВИРТУАЛЬНО: строки живут в <see cref="TaskVirtualList"/>, карточки
    /// подтягиваются страницами по требованию. Так работают базы, которые целиком в память не
    /// берут; порог и сборку групп решает контроллер.
    ///
    /// Выделение здесь НЕ прореживается по живым задачам (<c>Retain</c>): полного набора у нас
    /// нет, а вычистить ключи по одному кешу страниц значило бы терять выделение просто оттого,
    /// что человек отлистал далеко. Ключи остаются, а действуют меню и «Удалить» все равно
    /// только на реализованные строки (<see cref="VisibleTasks"/>).
    /// </summary>
    internal void SetVirtual(IReadOnlyList<TaskVirtualList.Entry> entries, string emptyHint = "Нет задач")
    {
        _entries   = entries;
        _groups    = entries.Select(e => e.Group).ToList();
        _emptyHint = emptyHint;

        var alive = _groups.Select(g => (g.DbId, g.ColId)).ToHashSet();
        _collapsed.RemoveWhere(k => !alive.Contains(k));

        RebuildVirtual();
    }

    private IReadOnlyList<TaskVirtualList.Entry> _entries = [];

    // Пересобрать виртуальный список. Форму он на месте не меняет (свернули группу — сместились
    // ВСЕ последующие строки), поэтому проще и честнее собрать заново: карточки при этом все
    // равно не материализуются, а страницы перечитаются по мере показа.
    private void RebuildVirtual()
    {
        foreach (var e in _entries)
            e.Collapsed = _collapsed.Contains((e.Group.DbId, e.Group.ColId));

        _virtual?.Cancel();
        _virtual = new TaskVirtualList(_entries, DateTime.Now, _selection.Keys);
        _items   = [];
        _visible = [];

        CardsHost.ItemsSource = _virtual;
        EmptyLabel.Text       = _emptyHint;
        EmptyLabel.IsVisible  = _entries.Count == 0;
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
        // Свернутая группа — не пустой список: подсказку показываем, только когда групп нет.
        EmptyLabel.IsVisible  = _groups.Count == 0;
    }

    // ── Выделение карточек ───────────────────────────────────────────────────

    // Перекрасить карточки под текущее выделение. Идем по моделям строк, а не по контролам:
    // список виртуализирован, и карточки за краем экрана контролов сейчас не имеют — новые
    // построятся уже с нужным IsSelected.
    private void SyncSelection()
    {
        if (_virtual is { } v)
        {
            // Виртуальному списку выделение отдается ключами: карточки, которые приедут позже,
            // должны прийти уже подсвеченными, а самих задач он в этот момент не знает.
            v.SyncSelection(_selection, _selection.Keys);
            return;
        }

        foreach (var item in _items)
            if (item is TaskCardVm card) card.IsSelected = _selection.Contains(card.Task);
    }

    private void OnCardTapped(TaskCardVm vm, TappedEventArgs e)
    {
        e.Handled = true;
        // Заглушка еще не задача: выделять и открывать нечего.
        if (vm.IsPlaceholder) return;

        // Ctrl+Shift+клик — диапазон от якоря; якоря нет (первый клик после загрузки) —
        // ведем себя как обычный клик.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && _selection.SelectRange(VisibleTasks, vm.Task))
        {
            SyncSelection();
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _selection.Toggle(vm.Task);
        else                                            _selection.SelectOnly(vm.Task);
        SyncSelection();
    }

    // Правый клик по НЕвыделенной карточке выделяет ее одну: меню всегда действует на то,
    // что видно выделенным (как в календаре).
    private void OnCardPointerPressed(TaskCardVm vm, PointerPressedEventArgs e)
    {
        if (vm.IsPlaceholder) return;
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (_selection.Contains(vm.Task)) return;
        _selection.SelectOnly(vm.Task);
        SyncSelection();
    }

    private void OnCardContextRequested(Control card, TaskCardVm vm, ContextRequestedEventArgs e)
    {
        if (vm.IsPlaceholder) return;
        var selected = _selection.In(VisibleTasks);

        // Выделение НЕ снимаем: пока открыт диалог подтверждения, видно, ЧТО удаляется или
        // дублируется (после «Отмена» карточки остаются подсвеченными).
        var menu = selected.Count > 1 && _selection.Contains(vm.Task)
            ? Controls.Components.EventContextMenu.Multi(
                () => TasksDeleteRequested?.Invoke(this, selected),
                onDuplicateAll: () => TasksDuplicateRequested?.Invoke(this, selected), keys: Controls.Components.EventContextMenu.Keys.Tasks, owner: this)
            : Controls.Components.EventContextMenu.Single(
                onEdit:      () => TaskActivated?.Invoke(this, vm.Task),
                onDelete:    () => TasksDeleteRequested?.Invoke(this, [vm.Task]),
                onDuplicate: () => TasksDuplicateRequested?.Invoke(this, [vm.Task]), keys: Controls.Components.EventContextMenu.Keys.Tasks, owner: this);

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
                // Раздел скрыт (открыт календарь/конструктор) — клавиша не наша.
                if (!IsEffectivelyVisible) return;
                // Клавишу уже забрала боковая панель (удаление коллекции) — второй раз не наша:
                // обработчик висит на окне с handledEventsToo и видит даже обработанное.
                if (KeyClaim.Claimed(ke)) return;
                // Ввод: название новой задачи, поиск в навбаре, поля срока — там Delete стирает текст.
                // ComboBox тоже пропускаем: клавиша в списке коллекций/исполнителя не про задачи.
                if (ke.Source is TextBox or AutoCompleteBox or ComboBox) return;

                var selected = _selection.In(VisibleTasks);
                if (selected.Count == 0) return;

                // Пункты меню карточки с клавиатуры — по выделению, тем же путем.
                if (ShortcutSettings.Matches("tasks.deleteSelection", ke))
                {
                    TasksDeleteRequested?.Invoke(this, selected);
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("tasks.duplicateSelection", ke))
                {
                    TasksDuplicateRequested?.Invoke(this, selected);
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("tasks.openTask", ke))
                {
                    TaskActivated?.Invoke(this, selected[0]);
                    ke.Handled = true;
                }
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
        if (_virtual is not null) RebuildVirtual();
        else                      RebuildItems();
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
    /// <summary>Первый пункт списка исполнителей строки добавления: задача заводится
    /// нераспределенной — раздел показывает такие прочерком, и именно их предстоит раздать.</summary>
    public const string UnassignedLabel = "Не назначен";

    /// <param name="selected">Кого выбрать: имя либо пусто — «Не назначен» (настройка
    /// «Исполнитель в быстром добавлении»). Выбор, сделанный человеком в строке, переживает
    /// перечитывание списка.</param>
    public void SetUserOptions(IReadOnlyList<string> options, string selected)
    {
        var opts = new List<string> { UnassignedLabel };
        opts.AddRange(options.Count > 0 ? options : [CurrentUser.DisplayName]);
        if (selected.Length > 0 && !opts.Contains(selected)) opts.Add(selected);
        var keep = QuickExecutor.SelectedItem as string;
        QuickExecutor.ItemsSource = opts;
        var idx = keep is not null ? opts.IndexOf(keep) : -1;
        if (idx < 0) idx = selected.Length > 0 ? opts.IndexOf(selected) : 0;
        QuickExecutor.SelectedIndex = idx >= 0 ? idx : 0;
    }

    /// <summary>
    /// Переставить поля строки добавления по настройкам раздела: дата — по «Срок в быстром
    /// добавлении», исполнитель — по «Исполнитель в быстром добавлении». Зовется при открытии
    /// раздела, при смене настроек и после каждого добавления.
    /// </summary>
    public void ApplyQuickDefaults()
    {
        QuickDateField.Date = TaskSettings.QuickDueDate(DateTime.Today);

        if (QuickExecutor.ItemsSource is not IList<string> opts || opts.Count == 0) return;
        var wanted = TaskSettings.QuickExecutorMe ? CurrentUser.DisplayName : UnassignedLabel;
        var idx    = opts.IndexOf(wanted);
        QuickExecutor.SelectedIndex = idx >= 0 ? idx : 0;
    }

    /// <summary>Текущее положение прокрутки — снимается ПЕРЕД тихим обновлением.</summary>
    public double ScrollOffset => CardsScroll.Offset.Y;

    /// <summary>
    /// Вернуть список туда, где он стоял. Отложенно: контейнеры строятся уже после подмены
    /// источника, и до отрисовки у ScrollViewer еще нет высоты содержимого — присвоенное
    /// сейчас смещение он просто обрежет нулем. Тот же прием, что у «Расписания»
    /// (<c>ApplyPosition</c>).
    /// </summary>
    public void RestoreScroll(double y) => Dispatcher.UIThread.Post(
        () => CardsScroll.Offset = new Vector(0, y), DispatcherPriority.Background);

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

        // «Не назначен» уходит пустым исполнителем — задача заводится нераспределенной.
        var executor = QuickExecutor.SelectedItem as string ?? "";
        if (executor == UnassignedLabel) executor = "";
        QuickAddRequested?.Invoke(this, new QuickAddRequest(title, due, executor, SelectedCollection));
        QuickTitle.Text = "";
        // Дата — снова по настройке; исполнитель остается: несколько задач подряд одному человеку.
        QuickDateField.Date = TaskSettings.QuickDueDate(DateTime.Today);
    }

    // ── Заголовок группы (коллекция) ─────────────────────────────────────────

    private Control BuildGroupHeader(TaskGroupHeaderVm vm)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var large     = (double)this.FindResource("FontSizeLarge")!;
        var bold      = (FontWeight)this.FindResource("FontWeightBold")!;

        // Название держится левого края, мелкий блок уходит к правому. Между ними тянущаяся
        // колонка: длинное название обрезается многоточием, а блок не съезжает влево.
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Стрелка — та же, что у групп и баз в сайдбаре (Viewbox 24×24 + Path обводкой):
        // раскрытая коллекция смотрит вниз, свернутая — вправо.
        row.Children.Add(Chevron(vm.IsCollapsed ? "IconChevronRight" : "IconChevronDown", secondary));

        var title = new TextBlock
        {
            Text              = vm.Title,
            FontSize          = large,
            FontWeight        = bold,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin            = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);

        // Мелкое (база, полоска, счетчик) стоит на ОДНОЙ базовой линии с названием коллекции,
        // а не по середине его высоты. Выравнивание по низу само по себе базовую линию не дает —
        // у крупного текста выносной элемент («р», «у») длиннее, поэтому блок опускается на
        // разницу выносных.
        var meta = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Bottom,
            Margin              = new Thickness(16, 0, 0, DescenderGap(large, bold)),
        };

        // База показывается, только когда их несколько (иначе шум): «база данных «Основная»».
        if (vm.Subtitle.Length > 0)
            meta.Children.Add(new TextBlock
            {
                Text = $"база данных «{vm.Subtitle}»", Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // Индикатор выполненного: полоска цветом коллекции + «2 из 7». Голое число задач
        // не отвечало на главный вопрос к списку дел — сколько из них уже закрыто.
        meta.Children.Add(ProgressBar(vm.DoneFraction, vm.Color));
        meta.Children.Add(new TextBlock
        {
            Text = vm.ProgressText, Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(meta, 2);
        row.Children.Add(meta);

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

    // На сколько опустить мелкий блок, чтобы он сел на базовую линию названия коллекции.
    // Считается по метрикам шрифта, а не константой: размеры берутся из ресурсов и меняются
    // живьем (настройка размера текста), а зум масштабирует результат вместе со строкой.
    private double DescenderGap(double titleSize, FontWeight titleWeight)
    {
        var family = this.FindResource("FontFamilyBase") as FontFamily ?? FontFamily.Default;
        var body   = (double)this.FindResource("FontSizeBase")!;

        // Height − Baseline = «низ строки минус базовая линия», то есть выносной вниз.
        double Descender(double size, FontWeight weight)
        {
            var layout = new TextLayout("Ар", new Typeface(family, FontStyle.Normal, weight),
                                        size, Brushes.Black);
            return layout.Height - layout.Baseline;
        }

        return Math.Max(0, Descender(titleSize, titleWeight) - Descender(body, FontWeight.Normal));
    }

    // Полоска выполненного: серый трек фиксированной ширины и заливка цветом коллекции.
    // Ширина в DIP — общий зум приложения масштабирует ее вместе с остальным.
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
        // (ContentPresenter строит по явному ItemTemplate без проверки типа) — отдаем пустую карточку.
        if (vm is null) return new Control();

        var bold      = (FontWeight)this.FindResource("FontWeightBold")!;
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var radius    = (CornerRadius)this.FindResource("RadiusMedium")!;

        // Все, что зависит от задачи, идет ПРИВЯЗКОЙ к модели строки, а не читается здесь:
        // в виртуальном режиме карточка строится заглушкой и оживает на месте, когда приедет
        // фоновая страница (TaskCardVm.Fill). Прочитанное при постройке так и осталось бы
        // «Загрузка…».
        Control Bound<T>(T c, params (AvaloniaProperty Prop, string Name)[] binds) where T : Control
        {
            foreach (var (prop, name) in binds) c.Bind(prop, new Binding(name) { Source = vm });
            return c;
        }

        // Левая колонка: название + «Исполнитель X · Назначил Y». Название обычным начертанием:
        // жирным оно спорило с заголовком коллекции и именами людей, а выделяет карточку и так
        // ее рамка; жирными остаются исполнитель и «Назначил» — то, что в строке ищут глазами.
        var title = (TextBlock)Bound(new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
        },
        (TextBlock.TextProperty,             nameof(TaskCardVm.TitleValue)),
        (TextBlock.ForegroundProperty,       nameof(TaskCardVm.TitleColor)),
        (TextBlock.TextDecorationsProperty,  nameof(TaskCardVm.TitleDecorations)));

        // Обе строки показываются ВСЕГДА, пустое значение — прочерком: в разделе теперь живут и
        // нераспределенные записи (запись есть, исполнитель не проставлен), и именно их надо
        // раздать. Пропуск «Назначил» при пустом значении сдвигал бы строку и делал незаполненное
        // невидимым — а «—» прямо говорит, чего не хватает.
        var people = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        people.Children.Add(new TextBlock { Text = "Исполнитель", Foreground = secondary });
        people.Children.Add(Bound(new TextBlock { Foreground = secondary, FontWeight = bold },
                                  (TextBlock.TextProperty, nameof(TaskCardVm.ExecutorText))));
        people.Children.Add(new TextBlock { Text = "·", Foreground = secondary, Margin = new Thickness(4, 0) });
        people.Children.Add(new TextBlock { Text = "Назначил", Foreground = secondary });
        people.Children.Add(Bound(new TextBlock { Foreground = secondary, FontWeight = bold },
                                  (TextBlock.TextProperty, nameof(TaskCardVm.AssignerText))));

        var left = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        left.Children.Add(people);

        // Правая колонка: срок сверху, чипы (статус + остаток) снизу.
        var due = Bound(new TextBlock { HorizontalAlignment = HorizontalAlignment.Right },
                        (TextBlock.TextProperty,       nameof(TaskCardVm.DueText)),
                        (TextBlock.ForegroundProperty, nameof(TaskCardVm.DueColor)));

        var chips = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        // Чип остатка строится ВСЕГДА и прячется привязкой: пересобирать панель под ожившую
        // заглушку было бы нечем — контейнер строки к тому моменту уже создан.
        chips.Children.Add(BoundChip(vm, nameof(TaskCardVm.StatusText),
                                         nameof(TaskCardVm.StatusBg),
                                         nameof(TaskCardVm.StatusFg)));
        chips.Children.Add(BoundChip(vm, nameof(TaskCardVm.DueChipText),
                                         nameof(TaskCardVm.DueChipBg),
                                         nameof(TaskCardVm.DueChipFg),
                                         nameof(TaskCardVm.HasDueChip)));

        var right = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(due);
        right.Children.Add(chips);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*,12,Auto") };
        // Цветовая полоска коллекции — как в списке календаря.
        var stripe = (Border)Bound(new Border
        {
            Width = 4, CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Stretch,
        },
        (Border.BackgroundProperty, nameof(TaskCardVm.StripeBrush)));
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
            if (vm.IsPlaceholder) return;
            // Двойной клик тоже оставляет задачу выделенной: после «Сохранить»/«Отмена»
            // видно, какую карточку правили (одиночный клик уже выделил ее — здесь ничего
            // не сбрасываем).
            TaskActivated?.Invoke(this, vm.Task);
        };
        return card;
    }

    // Чип карточки: пилюля с текстом. Все привязано к модели строки — и текст, и обе кисти, и
    // видимость (у задачи без срока чипа остатка нет). Строится один раз на всю жизнь контейнера.
    private static Border BoundChip(TaskCardVm vm, string textProp, string bgProp, string fgProp,
                                    string? visibleProp = null)
    {
        var label = new TextBlock { FontWeight = FontWeight.SemiBold };
        label.Bind(TextBlock.TextProperty,       new Binding(textProp) { Source = vm });
        label.Bind(TextBlock.ForegroundProperty, new Binding(fgProp)   { Source = vm });

        var chip = new Border
        {
            CornerRadius = new CornerRadius(99),
            Padding      = new Thickness(10, 3),
            Child        = label,
        };
        chip.Bind(Border.BackgroundProperty, new Binding(bgProp) { Source = vm });
        if (visibleProp is not null)
            chip.Bind(Visual.IsVisibleProperty, new Binding(visibleProp) { Source = vm });
        return chip;
    }
}

/// <summary>Группа списка: коллекция и ее задачи (порядок задает контроллер).</summary>
/// <param name="DbId">База коллекции — вместе с <paramref name="ColId"/> ключ свернутости
/// (id коллекций не уникальны между БД).</param>
/// <param name="Title">Название коллекции (одной строкой).</param>
/// <param name="Subtitle">Имя базы — только при нескольких смонтированных БД, иначе "".</param>
/// <param name="Color">Цвет коллекции (правило «дефолтный синий → серый» уже применено) —
/// им заливается индикатор выполненных задач в заголовке.</param>
/// <param name="Exact">
/// Размер группы ПО БАЗЕ — (всего задач, из них выполненных), <c>null</c> — считать по
/// показанному. Нужен виртуальному списку: там <paramref name="Tasks"/> пуст (карточки
/// подтягиваются страницами), а заголовок обязан назвать настоящее число дел. В памяти набор
/// показан целиком, и считать по нему — то же самое.
/// </param>
public sealed record TaskGroup(int DbId, int ColId, string Title, string Subtitle, string Color,
                               IReadOnlyList<TaskRecord> Tasks, (int Total, int Done)? Exact = null);

// ── Модель строки-заголовка группы ───────────────────────────────────────────
public sealed class TaskGroupHeaderVm(TaskGroup group, bool collapsed, bool first)
{
    public TaskGroup Group       => group;
    public string    Title       => group.Title;
    public string    Subtitle    => group.Subtitle;
    public string    Color       => group.Color;
    public bool      IsCollapsed => collapsed;
    public bool      IsFirst     => first;

    /// <summary>Сколько задач коллекции в статусе «Выполнено». Точное число приходит из базы
    /// (<see cref="TaskGroup.Exact"/>) и не зависит от крышки чтения; его нет — считаем по
    /// показанному, как и раньше.</summary>
    public int DoneCount { get; } = group.Exact?.Done ??
        group.Tasks.Count(t => t.Status == ConstructorRepository.TaskStatusDone);

    /// <summary>Сколько задач в коллекции ВСЕГО. Прочитанной частью это число не ограничено:
    /// заголовок обязан называть размер коллекции, иначе он повторял бы то, что и так на
    /// экране, и молчал бы о недочитанном хвосте.</summary>
    public int TotalCount => group.Exact?.Total ?? group.Tasks.Count;

    /// <summary>«2 из 7» — выполнено из всего, с разделителем разрядов. Здесь, в отличие от
    /// пилюли навбара, число печатается целиком: в строке заголовка для него есть место, и
    /// именно тут человек узнает настоящий размер коллекции.</summary>
    public string ProgressText => $"{DoneCount:N0} из {TotalCount:N0}";

    /// <summary>Доля выполненных 0..1 — длина заливки индикатора.</summary>
    public double DoneFraction => TotalCount > 0 ? (double)DoneCount / TotalCount : 0;
}

// ── Модель карточки ─────────────────────────────────────────────────────────
//
// ВСЕ видимые значения карточка берет привязками к этой модели, а не читает при постройке.
// Так строка виртуального списка рождается ЗАГЛУШКОЙ и превращается в задачу НА МЕСТЕ
// (<see cref="TaskCardVm.Fill"/>), когда придет фоновая страница, — тем же приемом, каким
// оживает строка «Журнала» календаря. Пересобирать список для этого не нужно, а высота
// заглушки совпадает с высотой задачи (разметка одна и та же), поэтому прокрутка не дергается.
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

    /// <summary>Прочерк вместо пустого значения — раздел показывает и нераспределенные записи.</summary>
    public const string Dash = "—";

    /// <summary>Правила показа из «Настройки → Задачи» (момент просрочки): ставит контроллер
    /// перед каждой раскладкой списка, чтобы чип срока и фильтр «Просрочено» считали одно и
    /// то же (<see cref="TaskFilters.IsOverdue"/>).</summary>
    public static TaskRules Rules { get; set; } = TaskRules.Default;

    /// <summary>Задача строки. У заглушки — пустая заготовка: настоящей еще нет.</summary>
    public TaskRecord Task { get; private set; } = new();

    /// <summary>
    /// Строка еще не заполнена — место под задачу, которую тянет фоновая страница. Такая
    /// карточка не выделяется, не открывается двойным кликом и в меню не участвует: под ней
    /// пока нет записи, и любое действие было бы действием над пустотой.
    /// </summary>
    public bool IsPlaceholder { get; private set; }

    public bool   IsDone       { get; private set; }
    public string Executor     => Task.Executor;
    public string TitleValue   { get; private set; } = "";
    public IBrush TitleColor   { get; private set; } = Brushes.Gray;
    public TextDecorationCollection? TitleDecorations { get; private set; }
    public string ExecutorText { get; private set; } = Dash;
    public string AssignerText { get; private set; } = Dash;
    public string StatusText   { get; private set; } = "";
    public IBrush StatusBg     { get; private set; } = Brushes.Transparent;
    public IBrush StatusFg     { get; private set; } = Brushes.Gray;
    public string DueText      { get; private set; } = "";
    public IBrush DueColor     { get; private set; } = Brushes.Gray;
    public bool   HasDueChip   { get; private set; }
    public string DueChipText  { get; private set; } = "";
    public IBrush DueChipBg    { get; private set; } = Brushes.Transparent;
    public IBrush DueChipFg    { get; private set; } = Brushes.Gray;
    public IBrush StripeBrush  { get; private set; } = Brushes.Transparent;

    public TaskCardVm(TaskRecord task, DateTime now) => Apply(task, now);

    private TaskCardVm() { }

    /// <summary>
    /// Пустое место под задачу: разметка та же, поэтому строка занимает столько же высоты,
    /// сколько займет настоящая карточка, и прокрутка не прыгает, когда страница приедет.
    /// </summary>
    public static TaskCardVm Loading() => new()
    {
        IsPlaceholder = true,
        TitleValue    = "Загрузка…",
        TitleColor    = Brush("BrushSecondaryText", "#8E8E93"),
        ExecutorText  = Dash,
        AssignerText  = Dash,
        StatusText    = ConstructorRepository.TaskStatusPlanned,
        StatusBg      = Brush("BrushSecondaryBg", "#F2F2F7"),
        StatusFg      = Brush("BrushSecondaryText", "#8E8E93"),
        DueText       = "",
        DueColor      = Brush("BrushSecondaryText", "#8E8E93"),
        StripeBrush   = Brushes.Transparent,
    };

    /// <summary>Заглушка → задача: значения меняются, привязки узнают об этом сами.</summary>
    public void Fill(TaskRecord task, DateTime now)
    {
        Apply(task, now);
        foreach (var name in Notified) OnPropertyChanged(name);
    }

    // Что обязано доехать до привязок при оживлении заглушки. Список явный, а не пустое имя
    // «обновилось все»: так видно, что именно показывает карточка, и новое поле не потеряется.
    private static readonly string[] Notified =
    [
        nameof(IsPlaceholder), nameof(IsDone), nameof(TitleValue), nameof(TitleColor),
        nameof(TitleDecorations), nameof(ExecutorText), nameof(AssignerText),
        nameof(StatusText), nameof(StatusBg), nameof(StatusFg),
        nameof(DueText), nameof(DueColor),
        nameof(HasDueChip), nameof(DueChipText), nameof(DueChipBg), nameof(DueChipFg),
        nameof(StripeBrush),
    ];

    private void Apply(TaskRecord task, DateTime now)
    {
        Task          = task;
        IsPlaceholder = false;
        IsDone        = task.Status == ConstructorRepository.TaskStatusDone;

        TitleValue       = task.DisplayTitle;
        TitleColor       = IsDone ? Brush("BrushSecondaryText", "#8E8E93") : Brush("BrushPrimaryText", "#1C1C1E");
        TitleDecorations = IsDone ? TextDecorations.Strikethrough : null;

        // Обе строки показываются ВСЕГДА, пустое значение — прочерком: в разделе живут и
        // нераспределенные записи, и именно их надо раздать.
        ExecutorText = task.Executor.Length > 0 ? task.Executor : Dash;
        AssignerText = task.Assigner.Length > 0 ? task.Assigner : Dash;

        StatusText = task.Status.Length > 0 ? task.Status : ConstructorRepository.TaskStatusPlanned;
        (StatusBg, StatusFg) = StatusText switch
        {
            ConstructorRepository.TaskStatusActive => (Brush("BrushAccentLight", "#E5F0FF"), Brush("BrushAccent", "#007AFF")),
            ConstructorRepository.TaskStatusDone   => (Brush("BrushGreenLight", "#E3F7E9"), Brush("BrushGreen", "#34C759")),
            _ => (Brush("BrushSecondaryBg", "#F2F2F7"), Brush("BrushSecondaryText", "#8E8E93")),
        };

        DueText  = FormatDue(task);
        DueColor = task.DueStart is null
            ? Brush("BrushSecondaryText", "#8E8E93")
            : Brush("BrushPrimaryText", "#1C1C1E");

        var chip = DueChip(task, now);
        HasDueChip  = chip is not null;
        DueChipText = chip?.Text ?? "";
        DueChipBg   = chip is { Overdue: true } ? Brush("BrushRedLight", "#FFE9E7") : Brush("BrushSecondaryBg", "#F2F2F7");
        DueChipFg   = chip is { Overdue: true } ? Brush("BrushRed", "#FF3B30")      : Brush("BrushSecondaryText", "#8E8E93");

        // Цветовая полоска коллекции — как в списке календаря; правило «дефолтный синий =
        // без цвета → серый» живет в EventColors, кисти кешируются (карточек на экране много).
        StripeBrush = ColorTools.Solid(EventColors.Display(task.Color)) ?? Brushes.Transparent;
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
    // только нейтральное «Сегодня», без просрочки и обратного отсчета. Момент просрочки —
    // общий с фильтром (TaskFilters.IsOverdue, правило из настроек раздела).
    private static (string Text, bool Overdue)? DueChip(TaskRecord t, DateTime now)
    {
        if (t.DueStart is not DateTime s) return null;
        var end = t.DueEnd ?? s;

        if (t.Status == ConstructorRepository.TaskStatusDone)
            return end.Date == now.Date ? ("Сегодня", false) : null;

        if (TaskFilters.IsOverdue(t, now, Rules))
        {
            var days = (int)(now.Date - end.Date).TotalDays;
            if (days >= 1) return ($"Просрочено на {days} дн.", true);
            var hours = Math.Max(1, (int)Math.Ceiling((now - end).TotalHours));
            return ($"Просрочено на {hours} ч", true);
        }

        if (t.AllDay && end.Date == now.Date) return ("Сегодня", false);
        // Просрочка «со следующего дня»: срок сегодня уже прошел, но день еще идет — «Сегодня».
        if (end <= now) return ("Сегодня", false);

        var left = end - now;
        if (left.TotalHours < 24)
            return ($"Осталось {Math.Max(1, (int)Math.Ceiling(left.TotalHours))} ч", false);
        return ($"Осталось {(int)Math.Ceiling((end.Date - now.Date).TotalDays)} дн.", false);
    }
}
