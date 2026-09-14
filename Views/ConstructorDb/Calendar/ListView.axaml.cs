using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb.Calendar;

public partial class ListView : UserControl
{
    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;
    public event EventHandler<DateTime>? CreateRequested;

    // Геометрия строки (Border Height=36 + Margin 0,1 → 38) и Padding списка сверху —
    // позиция строки по индексу считается без обхода визуального дерева (как в «Журнале»).
    private const double RowHeight = 38.0;
    private const double PadTop    = 4.0;

    // Полная виртуализация: список знает ПОЛНОЕ число событий дня (полоса прокрутки на весь объем,
    // можно перетащить в самый низ), а страницы грузятся по требованию (см. CalendarDayVirtualList).
    private CalendarDayVirtualList? _list;
    private DateTime _currentDate = DateTime.Today;

    public ListView()
    {
        InitializeComponent();
    }

    private EventHandler<KeyEventArgs>? _windowKeyHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        top?.AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        if (e.Root is Avalonia.Controls.Window win)
        {
            _windowKeyHandler = (_, ke) =>
            {
                if (!IsVisible) return;
                if (ke.Source is TextBox or AutoCompleteBox) return;
                var selected = SelectedRecords();
                if (selected.Count == 0) return;

                if (ShortcutSettings.Matches("calendar.deleteSelection", ke))
                {
                    // Выделение НЕ снимаем: пока открыт диалог подтверждения, видно, ЧТО
                    // удаляется (после «Отмена» строки останутся подсвеченными).
                    if (selected.Count == 1) RecordDeleteRequested?.Invoke(this, selected[0]);
                    else                     RecordsDeleteRequested?.Invoke(this, selected);
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("calendar.duplicateSelection", ke))
                {
                    RecordsDuplicateRequested?.Invoke(this, selected);
                    ke.Handled = true;
                }
                else if (ShortcutSettings.Matches("calendar.openEvent", ke))
                {
                    RecordClicked?.Invoke(this, selected[0]);
                    ke.Handled = true;
                }
            };
            win.AddHandler(KeyDownEvent, _windowKeyHandler, handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TopLevel.GetTopLevel(this)?.RemoveHandler(
            PointerPressedEvent, OnGlobalPointerPressed);
        if (e.Root is Avalonia.Controls.Window win && _windowKeyHandler is not null)
        {
            win.RemoveHandler(KeyDownEvent, _windowKeyHandler);
            _windowKeyHandler = null;
        }
    }

    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Правый клик не сбрасывает выделение — иначе контекстное меню откроется
        // над уже снятым выделением (как в режимах «Месяц»/«Неделя»/«День»).
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) return;

        // Shift+клик — аддитивное выделение: глобальный сброс пропускаем,
        // toggle делается в OnEventTapped.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        DeselectAll();
    }

    private void DeselectAll()
    {
        if (_list is null) return;
        foreach (var i in _list.RealizedItems)
            i.IsSelected = false;
    }

    private List<CalendarRecord> SelectedRecords()
        => _list is null
            ? new List<CalendarRecord>()
            : _list.RealizedItems.Where(i => i.IsEvent && i.IsSelected).Select(i => i.OriginalEvent!).ToList();

    // Якорь диапазона — последняя выделенная кликом запись: Ctrl+Shift+клик выделяет все
    // строки между якорем и кликнутой (включительно).
    private (int DbId, int RecId, int FieldId)? _rangeAnchor;

    private void SetRangeAnchor(CalendarRecord? ev)
    {
        if (ev is { Id: int id }) _rangeAnchor = (ev.DbId, id, ev.FieldId);
    }

    // Ctrl+Shift+клик: выделить диапазон от якоря до кликнутой строки. true — диапазон применен.
    private bool TrySelectRange(KeyModifiers modifiers, CalendarRecord? clicked)
    {
        if (!modifiers.HasFlag(KeyModifiers.Shift) || !modifiers.HasFlag(KeyModifiers.Control))
            return false;
        if (_rangeAnchor is not { } a || clicked is not { Id: int cid }) return false;
        if (_list?.RowOfEvent(a.DbId, a.RecId, a.FieldId) is not int ai) return false;
        if (_list?.RowOfEvent(clicked.DbId, cid, clicked.FieldId) is not int bi) return false;
        _list!.SelectRange(ai, bi);
        return true;
    }

    // Поколение загрузки дня: быстрое переключение дат/поиска отменяет устаревший результат.
    private int _loadGeneration;
    private DateTime? _lastDay;
    private string    _lastSearch = "";

    // Кеш счетчиков дня (день → счетчики по источникам, позиционно к отсортированным dbs):
    // COUNT дня-гиганта — секунды холодным, и без кеша платится при КАЖДОМ визите дня
    // (навигация Prev/Next/Сегодня, возврат из поиска). Дельта правит кеш точечно по дню
    // НАЧАЛА записи (та же семантика, что у CountCalendarDayEvents и InDay в ApplyDeltaAsync) —
    // БД уже изменена, поэтому правленый кеш остается источником правды. Сбрасывается целиком
    // при смене набора источников/активных коллекций (сигнатура) и при пересборке БЕЗ дельты
    // (внешнее изменение — что поменялось, неизвестно).
    private readonly Dictionary<DateTime, int[]> _dayCountsCache = [];
    private string _dayCountsSig = "";

    /// <summary>
    /// Открывает день в «Расписании» через полную виртуализацию: события дня из
    /// <paramref name="dbs"/> (несколько БД) в окне [<paramref name="dayStart"/>,
    /// <paramref name="dayEnd"/>). Полоса прокрутки — на все число событий, страницы грузятся по
    /// требованию (в т.ч. прыжок в самый низ). <paramref name="search"/> фильтрует по заголовку.
    /// Подсчет числа событий (размер полосы) идет В ФОНЕ на изолированных подключениях: холодный
    /// COUNT по дню с миллионами событий занимает секунды и не должен блокировать UI-поток
    /// (иначе старт приложения выглядел зависшим на «Запуске интерфейса…»).
    ///
    /// Первый показ / другой день / смена поиска — «свежая» загрузка с прокруткой наверх. Иначе —
    /// ТИХОЕ обновление (правка/дублирование/смена коллекций), как в «Журнале»: старый список
    /// остается на экране, пока строится новый, позиция сохраняется, выделение переживает
    /// перезагрузку; к <paramref name="anchorRecord"/> (созданная/измененная запись или последняя
    /// копия) лента едет по центру, только если ее строка сейчас не видна;
    /// <paramref name="selectRecords"/> (копии после дублирования) ЗАМЕНЯЮТ старое выделение.
    /// </summary>
    public void LoadFrom(IReadOnlyList<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
                         DateTime dayStart, DateTime dayEnd, DateTime currentDate, string search,
                         CalendarRecord? anchorRecord = null,
                         IReadOnlyList<(CalendarRecord Rec, int Delta)>? delta = null,
                         bool selectAddedOnly = false)
    {
        _currentDate = currentDate;
        var isPast = currentDate.Date < DateTime.Today;
        var gen    = ++_loadGeneration;
        search ??= "";

        var fresh = _list is null || _lastDay != dayStart ||
                    !string.Equals(search, _lastSearch, StringComparison.Ordinal);
        _lastDay = dayStart; _lastSearch = search;
        var oldOffset = ListScroll.Offset.Y;

        // Выделение переживает перезагрузку: ключи выделенных записей восстанавливаются на
        // строках нового списка (пользователь видит, какую запись менял).
        var preserveSelection = fresh ? null : _list?.RealizedItems
            .Where(i => i.IsEvent && i.IsSelected && i.OriginalEvent is { Id: not null })
            .Select(i => (i.OriginalEvent!.DbId, i.OriginalEvent!.Id!.Value, i.OriginalEvent!.FieldId))
            .ToHashSet();
        if (!fresh && anchorRecord is { Id: int anchorId })
            (preserveSelection ??= new HashSet<(int, int, int)>())
                .Add((anchorRecord.DbId, anchorId, anchorRecord.FieldId));
        // Дублирование: выделение ПЕРЕХОДИТ с оригиналов на копии (только добавленные записи).
        if (selectAddedOnly && delta is not null)
            preserveSelection = delta
                .Where(t => t.Delta > 0 && t.Rec.Id is not null)
                .Select(t => (t.Rec.DbId, t.Rec.Id!.Value, t.Rec.FieldId))
                .ToHashSet();

        // Порядок источников фиксируем по DbId — он же порядок конкатенации в виртуальном списке.
        var sorted = dbs.OrderBy(d => d.dbId).ToList();

        // Кеш счетчиков дня: другой набор источников/активных коллекций — все записи кеша не о том.
        var sig = string.Join("|", sorted.Select(d =>
            $"{d.dbId}:{(d.active is null ? "*" : string.Join(",", d.active.OrderBy(x => x)))}"));
        if (sig != _dayCountsSig) { _dayCountsCache.Clear(); _dayCountsSig = sig; }

        // Дельта правит кеш точечно — по дню НАЧАЛА записи (в т.ч. чужих дней: перенос даты
        // правит и старый, и новый день). Делается ДО выбора пути загрузки: правка при активном
        // поиске тоже меняет счетчики, хотя сама идет пересборкой поиска.
        if (delta is { Count: > 0 })
            foreach (var (rec, d) in delta)
                if (_dayCountsCache.TryGetValue(rec.StartDt.Date, out var c))
                {
                    var i = sorted.FindIndex(x => x.dbId == rec.DbId);
                    if (i >= 0 && i < c.Length) c[i] = Math.Max(0, c[i] + d);
                }

        // Точечная правка (создание/изменение/удаление/дублирование): сперва пробуем мутировать
        // ТЕКУЩИЙ список НА МЕСТЕ — offset-режим гигантского дня не превращается в «Загрузка…»
        // и не теряет выделение/якорь; если мутация невозможна — тихая пересборка.
        if (!fresh && search.Length == 0 && delta is { Count: > 0 } && _list is not null)
        {
            _ = ApplyDeltaOrReloadAsync(gen, sorted, dayStart, dayEnd, search, isPast, delta,
                                        preserveSelection, anchorRecord, oldOffset, selectAddedOnly);
            return;
        }

        // Пересборка БЕЗ дельты того же дня (смена коллекций, возврат в вид, внешняя правка):
        // что изменилось — неизвестно, кеш счетчиков недействителен целиком.
        if (!fresh && delta is null) _dayCountsCache.Clear();

        if (fresh)
        {
            _list = null;
            EventsList.ItemsSource = null;
            EmptyHint.Text      = "Загрузка…";
            EmptyHint.IsVisible = true;
        }
        _ = LoadCountsThenBuildAsync(gen, sorted, dayStart, dayEnd, search, isPast,
                                     fresh, anchorRecord, oldOffset, preserveSelection);
    }

    // Точечная правка: мутация текущего списка на месте (страницы переживают правку — без
    // волны «Загрузка…»); не вышло (поиск, строка не в кеше, много вставок) — тихая пересборка.
    private async Task ApplyDeltaOrReloadAsync(
        int gen, List<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        DateTime dayStart, DateTime dayEnd, string search, bool isPast,
        IReadOnlyList<(CalendarRecord Rec, int Delta)> delta,
        HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection,
        CalendarRecord? anchorRecord, double oldOffset, bool selectAddedOnly)
    {
        var removed = delta.Where(t => t.Delta < 0).Select(t => t.Rec).ToList();
        var added   = delta.Where(t => t.Delta > 0).Select(t => t.Rec).ToList();

        if (_list is { } cur)
        {
            bool mutated;
            try { mutated = await cur.ApplyDeltaAsync(removed, added, selectAddedOnly); }
            catch { mutated = false; }
            if (gen != _loadGeneration) return;
            if (mutated)
            {
                EmptyHint.IsVisible = cur.Count == 0;
                ApplyPosition(gen, cur, fresh: false, anchorRecord, oldOffset);
                return;
            }
        }
        await LoadCountsThenBuildAsync(gen, dbs, dayStart, dayEnd, search, isPast,
                                       fresh: false, anchorRecord, oldOffset, preserveSelection);
    }

    private async Task LoadCountsThenBuildAsync(
        int gen, List<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        DateTime dayStart, DateTime dayEnd, string search, bool isPast,
        bool fresh, CalendarRecord? anchorRecord, double oldOffset,
        HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection)
    {
        CalendarDayVirtualList list;
        try
        {
            if (search.Length > 0)
            {
                // Поиск — фоновым SQL по всему дню (LIKE на изолированных подключениях):
                // счетчики не нужны, потолок «от начала» отсутствует — находит в любом месте дня.
                var found = new List<CalendarRecord>();
                foreach (var (dbId, repo, active) in dbs)
                {
                    var part = await repo.SearchCalendarEventsAsync(dayStart, dayEnd, active, search, 1000);
                    foreach (var ev in part) ev.DbId = dbId;
                    found.AddRange(part);
                }
                if (gen != _loadGeneration) return;
                list = new CalendarDayVirtualList(found, isPast, preserveSelection);
            }
            else
            {
                // Счетчики дня — из кеша (визит дня уже был, дельты держали его актуальным);
                // промах — фоновый COUNT по источникам с сохранением в кеш.
                if (!_dayCountsCache.TryGetValue(dayStart, out var counts))
                {
                    counts = new int[dbs.Count];
                    for (var i = 0; i < dbs.Count; i++)
                        counts[i] = await dbs[i].repo.CountCalendarDayEventsAsync(dayStart, dayEnd, dbs[i].active);
                    if (gen != _loadGeneration) return;   // пока считали — открыли другой день/поиск
                    _dayCountsCache[dayStart] = counts;
                }
                // Построение списка (для обычных дней — синхронная загрузка в память) — на UI-потоке.
                list = new CalendarDayVirtualList(dbs, counts, dayStart, dayEnd, isPast, preserveSelection);
            }
        }
        catch
        {
            // БД недоступна/повреждена — день показываем пустым (как ведет себя Workspace.Mount).
            if (gen == _loadGeneration) EmptyHint.Text = "Нет записей";
            return;
        }
        if (gen != _loadGeneration) return;
        _list = list;
        EventsList.ItemsSource = list;
        EmptyHint.Text      = "Нет записей";
        EmptyHint.IsVisible = list.Count == 0;
        ApplyPosition(gen, list, fresh, anchorRecord, oldOffset);
    }

    // Позиция после подмены списка (после отрисовки — нужен Extent): свежая загрузка — наверх;
    // тихое обновление — остаемся на месте, а к созданной/измененной записи или последней копии
    // едем (плавно, по центру окна), только если ее строка сейчас не видна. В Offset-режиме
    // гигантского дня позиция нереализованной строки без запроса неизвестна — остаемся на месте.
    private void ApplyPosition(int gen, CalendarDayVirtualList list, bool fresh,
                               CalendarRecord? anchorRecord, double oldOffset)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (gen != _loadGeneration) return;
            if (fresh) { ListScroll.Offset = new Vector(0, 0); return; }

            ListScroll.Offset = new Vector(0, oldOffset);   // по умолчанию остаемся на месте
            if (anchorRecord is not { Id: int rid }) return;
            if (list.RowOfEvent(anchorRecord.DbId, rid, anchorRecord.FieldId) is not int r) return;

            // Хоть ЧАСТИЧНО видна — список не дергаем (правка видимой записи не должна двигать
            // экран, даже если ее строка наполовину срезана краем); полностью вне — центрируем.
            var y  = PadTop + r * RowHeight;
            var vh = ListScroll.Viewport.Height;
            if (y + RowHeight > ListScroll.Offset.Y && y < ListScroll.Offset.Y + vh) return;
            AnimateScroll(Math.Max(0, y - vh / 2 + RowHeight / 2));
        }, DispatcherPriority.Background);
    }

    private void AnimateScroll(double targetY)
    {
        var startY    = ListScroll.Offset.Y;
        var startTime = DateTime.Now;
        var timer     = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t     = Math.Min((DateTime.Now - startTime).TotalMilliseconds / 300.0, 1.0);
            var eased = 1 - Math.Pow(1 - t, 3);
            ListScroll.Offset = new Vector(0, startY + (targetY - startY) * eased);
            if (t >= 1.0) timer.Stop();
        };
        timer.Start();
    }

    private void OnEventPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item } && !item.IsSelected)
            item.IsHovered = true;
    }

    private void OnEventPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: EventListItem item })
            item.IsHovered = false;
    }

    private void OnEventTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item })
        {
            // Ctrl+Shift+клик — диапазон от якоря до кликнутой строки.
            if (TrySelectRange(e.KeyModifiers, item.OriginalEvent))
            {
                e.Handled = true;
                return;
            }
            var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (additive)
            {
                // toggle: повторный Shift+клик по выделенному — снять
                item.IsHovered  = false;
                item.IsSelected = !item.IsSelected;
                if (item.IsSelected) SetRangeAnchor(item.OriginalEvent);
            }
            else
            {
                DeselectAll();
                foreach (var i in _list?.RealizedItems ?? [])
                    i.IsHovered = false;
                item.IsSelected = true;
                SetRangeAnchor(item.OriginalEvent);
            }
            e.Handled = true;
        }
    }

    // Правый клик по событию: если оно еще не выделено — выделяем его одно
    // (как в других режимах), чтобы меню действовало на видимо выделенное.
    private void OnEventPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item }
            && e.GetCurrentPoint(this).Properties.IsRightButtonPressed
            && !item.IsSelected)
        {
            DeselectAll();
            foreach (var i in _list?.RealizedItems ?? [])
                i.IsHovered = false;
            item.IsSelected = true;
            SetRangeAnchor(item.OriginalEvent);
        }
    }

    private void OnEventContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border { Tag: EventListItem { IsEvent: true } item } border) return;

        var selected = SelectedRecords();
        ContextMenu menu;
        if (selected.Count > 1 && item.IsSelected)
        {
            // Выделение НЕ снимаем: во время диалога подтверждения видно, ЧТО удаляется/дублируется.
            menu = SmartBase.Controls.Components.EventContextMenu.Multi(
                () => RecordsDeleteRequested?.Invoke(this, selected),
                onDuplicateAll: () => RecordsDuplicateRequested?.Invoke(this, selected), keys: Controls.Components.EventContextMenu.Keys.Calendar, owner: this);
        }
        else
        {
            // «Редактировать»/«Дублировать»/«Удалить» выделение НЕ снимают: после сохранения/
            // отмены запись остается подсвеченной — видно, какую строку меняли/удаляли
            // (выделение переживает и перезагрузку — см. preserveSelection в LoadFrom).
            menu = SmartBase.Controls.Components.EventContextMenu.Single(
                onEdit:      () => RecordClicked?.Invoke(this, item.OriginalEvent!),
                onDelete:    () => RecordDeleteRequested?.Invoke(this, item.OriginalEvent!),
                onDuplicate: () => RecordsDuplicateRequested?.Invoke(this, [item.OriginalEvent!]), keys: Controls.Components.EventContextMenu.Keys.Calendar, owner: this);
        }
        menu.Open(border);
        e.Handled = true;
    }

    private void OnBackgroundContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Меню фона открывается, только если клик не по событию (по событию
        // сработает OnEventContextRequested и пометит e.Handled).
        DeselectAll();
        var menu = SmartBase.Controls.Components.EventContextMenu.Background(
            () => CreateRequested?.Invoke(this, _currentDate), keys: Controls.Components.EventContextMenu.Keys.Calendar, owner: this);
        if (sender is Control c) menu.Open(c);
        e.Handled = true;
    }

    private void OnEventDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: EventListItem { IsEvent: true } item })
        {
            e.Handled = true;
            // Двойной клик тоже ВЫДЕЛЯЕТ запись (глобальный сброс по второму клику снял
            // выделение первого): после «Сохранить»/«Отмена» видно, какую запись меняли.
            foreach (var i in _list?.RealizedItems ?? [])
            {
                i.IsSelected = false;
                i.IsHovered  = false;
            }
            item.IsSelected = true;
            SetRangeAnchor(item.OriginalEvent);
            RecordClicked?.Invoke(this, item.OriginalEvent!);
        }
    }

    private void OnBackgroundDoubleTapped(object? sender, TappedEventArgs e)
    {
        CreateRequested?.Invoke(this, _currentDate);
    }
}

public partial class EventListItem : ObservableObject
{
    public CalendarRecord? OriginalEvent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEvent))]
    private bool _isLoadingPlaceholder;

    public bool IsEvent => !IsLoadingPlaceholder;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _dateTimeText = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool   _hasDescription;
    [ObservableProperty] private IBrush _colorBrush = Brushes.Transparent;
    [ObservableProperty] private IBrush _titleBrush = Brushes.Gray;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundBrush))]
    private bool _isHovered;

    public IBrush BackgroundBrush => IsSelected
        ? new SolidColorBrush(Color.FromArgb(50, 128, 128, 128))
        : IsHovered
            ? new SolidColorBrush(Color.FromArgb(25, 128, 128, 128))
            : Brushes.Transparent;

    // Заглушка «Загрузка…» — заполняется реальным событием позже через Fill (в Offset-режиме).
    private EventListItem() => _isLoadingPlaceholder = true;
    public static EventListItem Loading() => new();

    public EventListItem(CalendarRecord ev, bool isPast = false) => Fill(ev, isPast);

    /// <summary>Заполняет строку данными события (заглушка → событие) с уведомлением привязок.</summary>
    public void Fill(CalendarRecord ev, bool isPast)
    {
        OriginalEvent  = ev;
        Title          = ev.DisplayTitle;
        // В превью разметка не нужна — снимаем markdown-форматирование
        Description    = MarkdownUtils.StripMarkdown(ev.Description);
        HasDescription = !string.IsNullOrWhiteSpace(Description);
        ColorBrush     = new SolidColorBrush(Color.Parse(EventColors.Display(ev.Color)));
        TitleBrush     = isPast
            ? new SolidColorBrush(Color.Parse("#8E8E93"))
            : new SolidColorBrush(Color.Parse("#1C1C1E"));
        DateTimeText   = ev.AllDay
            ? "Весь день"
            : $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
        IsLoadingPlaceholder = false;
    }
}
