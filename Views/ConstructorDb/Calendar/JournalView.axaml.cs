using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Views.ConstructorDb.Calendar;

public partial class JournalView : UserControl
{
    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;
    public event EventHandler<DateTime>? CreateRequested;

    // Геометрия строки (Border Height=36 + Margin 0,1 → 38) и Padding списка сверху —
    // все позиции (скролл к дню, дата по клику) считаются по индексам, без обхода
    // визуального дерева (реализованы только видимые строки).
    private const double RowHeight = 38.0;
    private const double PadTop    = 4.0;

    private JournalVirtualList? _list;
    private ScrollViewer        _scroll = null!;
    private int _loadGeneration;

    // Y последнего правого клика (в координатах RowsList) — для даты «Создать запись».
    private double _lastContextY;

    public JournalView()
    {
        InitializeComponent();
        _scroll = this.FindControl<ScrollViewer>("JournalScroll")!;
    }

    private EventHandler<KeyEventArgs>? _windowKeyHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TopLevel.GetTopLevel(this)?.AddHandler(
            PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);

        if (e.Root is Avalonia.Controls.Window win)
        {
            _windowKeyHandler = (_, ke) =>
            {
                if (ke.Key != Key.Delete && ke.Key != Key.Back) return;
                if (!IsVisible) return;
                if (ke.Source is TextBox or AutoCompleteBox) return;
                var selected = SelectedRecords();
                if (selected.Count == 0) return;
                // Выделение НЕ снимаем: пока открыт диалог подтверждения, видно, ЧТО удаляется
                // (после удаления строки исчезнут, после «Отмена» — останутся подсвеченными).
                if (selected.Count == 1) RecordDeleteRequested?.Invoke(this, selected[0]);
                else                     RecordsDeleteRequested?.Invoke(this, selected);
                ke.Handled = true;
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
        // Правый клик не сбрасывает выделение — иначе меню откроется над снятым.
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            _lastContextY = e.GetPosition(RowsList).Y;
            return;
        }

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
    // строки-события между якорем и кликнутой (включительно), заголовки дней пропускаются.
    private (int DbId, int RecId, int FieldId)? _rangeAnchor;

    private void SetRangeAnchor(CalendarRecord? ev)
    {
        if (ev is { Id: int id }) _rangeAnchor = (ev.DbId, id, ev.FieldId);
    }

    // Ctrl+Shift+клик: выделить диапазон от якоря до кликнутой строки. true — диапазон применён.
    private bool TrySelectRange(KeyModifiers modifiers, CalendarRecord? clicked)
    {
        if (!modifiers.HasFlag(KeyModifiers.Shift) || !modifiers.HasFlag(KeyModifiers.Control))
            return false;
        if (_rangeAnchor is not { } a || clicked is not { Id: int cid }) return false;
        if (_list?.RealizedRowOfKey(a.DbId, a.RecId, a.FieldId) is not int ai) return false;
        if (_list?.RealizedRowOfKey(clicked.DbId, cid, clicked.FieldId) is not int bi) return false;
        _list!.SelectRange(ai, bi);
        return true;
    }

    private string _lastSearch = "";

    /// <summary>
    /// Открывает «Журнал» (ВСЯ история данных, без ограничений по датам) через полную
    /// виртуализацию: карта ленты строится из счётчиков по дням (В ФОНЕ на изолированных
    /// подключениях — на больших БД GROUP BY занимает секунды), полоса прокрутки соответствует
    /// всем строкам ленты, события подгружаются страницами по требованию с «Загрузка…»
    /// (см. <see cref="JournalVirtualList"/>).
    ///
    /// Первый показ / смена поиска — «свежая» загрузка: видна «Загрузка…», после построения —
    /// прокрутка к сегодня. Иначе — ТИХОЕ обновление (создание/правка/удаление события,
    /// переключение коллекций): старая лента остаётся на экране, пока строится новая, позиция
    /// прокрутки сохраняется; к дате созданной/изменённой записи (<paramref name="anchorDate"/>)
    /// лента едет, только если этот день сейчас НЕ виден.
    /// </summary>
    // Кеш счётчиков дней последней полной загрузки (по источникам, в порядке dbs) — позволяет
    // применять точечные правки МГНОВЕННО (дельтой), без полного GROUP BY по всем БД.
    private List<Dictionary<string, int>>? _countsCache;
    private string _countsKey = "";

    public void LoadFrom(IReadOnlyList<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
                         string search, CalendarRecord? anchorRecord = null,
                         IReadOnlyList<(CalendarRecord Rec, int Delta)>? delta = null,
                         bool selectAddedOnly = false)
    {
        var gen = ++_loadGeneration;
        search ??= "";
        var sorted = dbs.OrderBy(d => d.dbId).ToList();
        var key    = string.Join(",", sorted.Select(d => d.dbId));
        var fresh  = _list is null || !string.Equals(search, _lastSearch, StringComparison.Ordinal);
        _lastSearch = search;

        var oldOffset = _scroll.Offset.Y;

        // Выделение переживает пересборку: запоминаем ключи выделенных записей и
        // восстанавливаем на строках нового списка (пользователь видит, какую запись менял).
        var preserveSelection = fresh ? null : _list?.RealizedItems
            .Where(i => i.IsEvent && i.IsSelected && i.OriginalEvent is { Id: not null })
            .Select(i => (i.OriginalEvent!.DbId, i.OriginalEvent!.Id!.Value, i.OriginalEvent!.FieldId))
            .ToHashSet();

        // Созданная/изменённая запись тоже выделяется — визуально видно её в списке
        // (при создании она ещё не была выделена, добавляем её ключ явно).
        if (!fresh && anchorRecord is { Id: int anchorId })
            (preserveSelection ??= new HashSet<(int, int, int)>())
                .Add((anchorRecord.DbId, anchorId, anchorRecord.FieldId));

        // Дублирование: выделение ПЕРЕХОДИТ с оригиналов на копии (только добавленные записи).
        if (selectAddedOnly && delta is not null)
            preserveSelection = delta
                .Where(t => t.Delta > 0 && t.Rec.Id is not null)
                .Select(t => (t.Rec.DbId, t.Rec.Id!.Value, t.Rec.FieldId))
                .ToHashSet();

        // МГНОВЕННЫЙ путь: правка/создание/удаление записи. Полный пересчёт (GROUP BY по всем БД)
        // на больших базах занимает секунды, в течение которых лента показывала бы УСТАРЕВШУЮ
        // запись (её можно было снова открыть и править). Вместо этого корректируем кешированные
        // счётчики дней дельтой (−1 старый день, +1 новый) и правим ленту в этом же кадре.
        if (!fresh && search.Length == 0 && delta is not null &&
            _countsCache is not null && _countsKey == key)
        {
            foreach (var (rec, d) in delta)
            {
                var idx = sorted.FindIndex(x => x.dbId == rec.DbId);
                if (idx < 0) { _countsCache = null; break; }   // незнакомая БД — полный пересчёт
                var dict = _countsCache[idx];
                var k = rec.StartDt.Date.ToString("yyyy-MM-dd");
                var v = dict.GetValueOrDefault(k) + d;
                if (v > 0) dict[k] = v; else dict.Remove(k);
            }
            if (_countsCache is not null)
            {
                _ = ApplyDeltaOrRebuildAsync(gen, sorted, delta, preserveSelection,
                                             anchorRecord, oldOffset, selectAddedOnly);
                return;
            }
        }

        if (fresh)
        {
            _list = null;
            RowsList.ItemsSource   = null;
            LoadingHint.IsVisible  = true;
        }
        _ = LoadCountsThenBuildAsync(gen, sorted, search,
                                     fresh, anchorRecord, oldOffset, preserveSelection);
    }

    // Точечная правка: сперва пробуем мутировать ТЕКУЩИЙ список НА МЕСТЕ (страницы и кеши
    // дней переживают правку — она видна в том же кадре, без волны «Загрузка…»: пересоздание
    // списка на разреженной истории перечитывало десятки видимых дней по одному — секунды после
    // каждого удаления). Если мутация невозможна (InMemory, день пересёк порог слияния и т.п.) —
    // мгновенная пересборка по кешу счётчиков, как раньше.
    private async Task ApplyDeltaOrRebuildAsync(
        int gen, List<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        IReadOnlyList<(CalendarRecord Rec, int Delta)> delta,
        HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection,
        CalendarRecord? anchorRecord, double oldOffset, bool selectAddedOnly = false)
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
                ApplyPosition(gen, cur, fresh: false, anchorRecord, oldOffset);
                return;
            }
        }

        if (_countsCache is null) return;
        var shaped = _countsCache.Select(m => m.Select(kv => (kv.Key, kv.Value)).ToList()).ToList();
        var list = new JournalVirtualList(dbs, shaped, preserveSelection);
        _list = list;
        RowsList.ItemsSource  = list;
        LoadingHint.IsVisible = false;
        ApplyPosition(gen, list, fresh: false, anchorRecord, oldOffset);
    }

    private async Task LoadCountsThenBuildAsync(
        int gen, List<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs, string search,
        bool fresh, CalendarRecord? anchorRecord, double oldOffset,
        HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection)
    {
        var rangeStart = JournalVirtualList.RangeStart;
        var rangeEnd   = JournalVirtualList.RangeEnd;
        JournalVirtualList list;
        try
        {
            if (search.Length > 0)
            {
                // Поиск — фоновым SQL по ВСЕЙ истории (LIKE на изолированных подключениях):
                // счётчики дней не нужны, находит в любом году.
                var found = new List<CalendarRecord>();
                foreach (var (dbId, repo, active) in dbs)
                {
                    var part = await repo.SearchCalendarEventsAsync(rangeStart, rangeEnd, active, search, 1000);
                    foreach (var ev in part) ev.DbId = dbId;
                    found.AddRange(part);
                }
                if (gen != _loadGeneration) return;
                list = new JournalVirtualList(found, preserveSelection);
            }
            else
            {
                var counts = new List<List<(string Day, int Count)>>(dbs.Count);
                foreach (var (_, repo, active) in dbs)
                    counts.Add(await repo.LoadCalendarDayCountsAsync(rangeStart, rangeEnd, active));
                if (gen != _loadGeneration) return;   // пока считали — сменили поиск/коллекции
                // Кеш для мгновенных точечных обновлений (см. дельта-путь в LoadFrom).
                _countsCache = counts.Select(l => l.ToDictionary(t => t.Day, t => t.Count)).ToList();
                _countsKey   = string.Join(",", dbs.Select(d => d.dbId));
                // Построение списка (InMemory-режим читает события синхронно) — на UI-потоке.
                list = new JournalVirtualList(dbs, counts, preserveSelection);
            }
        }
        catch
        {
            // БД недоступна/повреждена — журнал показываем пустым (как ведёт себя Workspace.Mount).
            if (gen == _loadGeneration) LoadingHint.IsVisible = false;
            return;
        }
        if (gen != _loadGeneration) return;
        _list = list;
        RowsList.ItemsSource  = list;
        LoadingHint.IsVisible = false;
        ApplyPosition(gen, list, fresh, anchorRecord, oldOffset);
    }

    // Позиция после подмены списка (после отрисовки — нужен Extent): свежая загрузка — к сегодня;
    // тихое обновление — остаёмся на месте, а если создали/изменили запись — прокрутка к САМОЙ
    // записи (не к заголовку дня: в большом дне она может быть далеко ниже) с центрированием,
    // и только когда её строка сейчас не видна.
    private void ApplyPosition(int gen, JournalVirtualList list, bool fresh,
                               CalendarRecord? anchorRecord, double oldOffset)
    {
        Dispatcher.UIThread.Post(async void () =>
        {
            if (gen != _loadGeneration) return;

            if (fresh)
            {
                if (list.TodayHeaderRow is int today) _scroll.Offset = new Vector(0, OffsetOfRow(today));
                return;
            }

            _scroll.Offset = new Vector(0, oldOffset);   // по умолчанию остаёмся на месте
            if (anchorRecord is null) return;

            try
            {
                // Строка самой записи; если найти не удалось — заголовок её дня.
                int? row = anchorRecord.Id is int rid
                    ? await list.RowOfEventAsync(anchorRecord.DbId, rid, anchorRecord.FieldId,
                                                 anchorRecord.StartDt)
                    : null;
                row ??= list.DayRowsOf(anchorRecord.StartDt.Date)?.FirstRow;
                if (gen != _loadGeneration || row is not int r) return;

                // Хоть ЧАСТИЧНО видна — ленту не дёргаем (правка видимой записи не должна
                // двигать экран, даже если строка наполовину срезана краем окна).
                var y  = OffsetOfRow(r);
                var vh = _scroll.Viewport.Height;
                if (y + RowHeight > _scroll.Offset.Y && y < _scroll.Offset.Y + vh) return;

                // Центрируем строку записи в окне списка.
                AnimateScroll(Math.Max(0, y - vh / 2 + RowHeight / 2));
            }
            catch
            {
                // Поиск строки не удался (БД недоступна) — остаёмся на месте.
            }
        }, DispatcherPriority.Background);
    }

    // Вертикальная позиция строки по индексу (все строки одной высоты).
    private static double OffsetOfRow(int row) => PadTop + row * RowHeight;

    private void OnEventPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: JournalRow { IsEvent: true } item } && !item.IsSelected)
            item.IsHovered = true;
    }

    private void OnEventPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: JournalRow item })
            item.IsHovered = false;
    }

    private void OnEventTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: JournalRow { IsEvent: true } item })
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
                foreach (var i in _list?.RealizedItems ?? [])
                {
                    i.IsSelected = false;
                    i.IsHovered  = false;
                }
                item.IsSelected = true;
                SetRangeAnchor(item.OriginalEvent);
            }
            e.Handled = true;
        }
    }

    // Правый клик по событию: если ещё не выделено — выделяем его одно.
    private void OnEventPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: JournalRow { IsEvent: true } item }
            && e.GetCurrentPoint(this).Properties.IsRightButtonPressed
            && !item.IsSelected)
        {
            foreach (var i in _list?.RealizedItems ?? [])
            {
                i.IsSelected = false;
                i.IsHovered  = false;
            }
            item.IsSelected = true;
            SetRangeAnchor(item.OriginalEvent);
        }
    }

    private void OnEventContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border { Tag: JournalRow { IsEvent: true } item } border) return;

        var selected = SelectedRecords();
        ContextMenu menu;
        if (selected.Count > 1 && item.IsSelected)
        {
            // Выделение НЕ снимаем: во время диалога подтверждения видно, ЧТО удаляется/дублируется.
            menu = DataBase.Controls.Components.EventContextMenu.Multi(
                () => RecordsDeleteRequested?.Invoke(this, selected),
                onDuplicateAll: () => RecordsDuplicateRequested?.Invoke(this, selected));
        }
        else
        {
            // «Редактировать»/«Дублировать»/«Удалить» выделение НЕ снимают: после сохранения/
            // отмены запись остаётся подсвеченной — видно, какую строку меняли/удаляли
            // (выделение переживает и пересборку — см. preserveSelection в LoadFrom).
            menu = DataBase.Controls.Components.EventContextMenu.Single(
                onEdit:      () => RecordClicked?.Invoke(this, item.OriginalEvent!),
                onDelete:    () => RecordDeleteRequested?.Invoke(this, item.OriginalEvent!),
                onDuplicate: () => RecordsDuplicateRequested?.Invoke(this, [item.OriginalEvent!]));
        }
        menu.Open(border);
        e.Handled = true;
    }

    private void OnBackgroundContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Меню фона открывается, только если клик не по событию (по событию
        // сработает OnEventContextRequested и пометит e.Handled).
        DeselectAll();
        var date = GetDateForY(_lastContextY);
        var menu = DataBase.Controls.Components.EventContextMenu.Background(
            () => CreateRequested?.Invoke(this, date));
        if (sender is Control c) menu.Open(c);
        e.Handled = true;
    }

    private void OnEventDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { Tag: JournalRow { IsEvent: true } item })
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
        var date = GetDateForY(e.GetPosition(RowsList).Y);
        CreateRequested?.Invoke(this, date);
    }

    // Дата по вертикальной позиции клика: индекс строки → день из карты списка.
    private DateTime GetDateForY(double y)
    {
        if (_list is null || _list.Count == 0) return DateTime.Today;
        var row = (int)((y - PadTop) / RowHeight);
        return _list.DateForRow(row);
    }

    // ── Навигация по датам ────────────────────────────────────────────────────

    private void AnimateScroll(double targetY)
    {
        var startY     = _scroll.Offset.Y;
        var startTime  = DateTime.Now;
        var timer      = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var t      = Math.Min((DateTime.Now - startTime).TotalMilliseconds / 300.0, 1.0);
            var eased  = 1 - Math.Pow(1 - t, 3);
            _scroll.Offset = new Vector(0, startY + (targetY - startY) * eased);
            if (t >= 1.0) timer.Stop();
        };
        timer.Start();
    }

    // Индекс строки у верхнего края видимой области.
    private int TopVisibleRow() => Math.Max(0, (int)((_scroll.Offset.Y - PadTop) / RowHeight));

    public void GoNext()
    {
        if (_list?.NextHeaderRow(TopVisibleRow()) is int row) AnimateScroll(OffsetOfRow(row));
    }

    public void GoPrev()
    {
        // Стоим на заголовке — к предыдущему дню; иначе к заголовку текущего.
        if (_list?.PrevHeaderRow(TopVisibleRow()) is int row) AnimateScroll(OffsetOfRow(row));
    }

    public void GoToday()
    {
        if (_list?.TodayHeaderRow is int row) AnimateScroll(OffsetOfRow(row));
    }
}

/// <summary>
/// Строка плоского списка «Журнала»: заголовок дня, событие, подсказка пустого сегодня или
/// заглушка «Загрузка…» (заполняется реальным событием позже через <see cref="Fill"/> —
/// как <c>EventListItem</c> в «Расписании»).
/// </summary>
public partial class JournalRow : ObservableObject
{
    private static readonly string[] Months =
        ["января", "февраля", "марта", "апреля", "мая", "июня",
         "июля",   "августа", "сентября", "октября", "ноября", "декабря"];

    private static readonly string[] WeekDays =
        ["воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота"];

    public bool     IsHeader   { get; private init; }
    public bool     IsHint     { get; private init; }
    public DateTime HeaderDate { get; private init; }
    public string   DateHeader { get; private init; } = "";
    public IBrush   HeaderBrush { get; private init; } = Brushes.Gray;

    public CalendarRecord? OriginalEvent { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEvent))]
    private bool _isLoadingPlaceholder;

    public bool IsEvent => !IsHeader && !IsHint && !IsLoadingPlaceholder;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _timeText = "";
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

    private JournalRow() { }

    /// <summary>Заголовок дня: «среда – 1 июля 2026» (сегодня красным, прошлое серым).
    /// Год обязателен: лента охватывает ~2 года, без него не понять, где находишься.</summary>
    public static JournalRow Header(DateTime date)
    {
        var today = DateTime.Today;
        return new JournalRow
        {
            IsHeader   = true,
            HeaderDate = date.Date,
            DateHeader = $"{WeekDays[(int)date.DayOfWeek]} – {date.Day} {Months[date.Month - 1]} {date.Year}",
            HeaderBrush = date.Date == today
                ? new SolidColorBrush(Color.Parse("#FF3B30"))
                : date.Date < today
                    ? new SolidColorBrush(Color.Parse("#8E8E93"))
                    : new SolidColorBrush(Color.Parse("#1C1C1E")),
        };
    }

    /// <summary>Подсказка «Нет записей на сегодня» (единственная строка пустого сегодня).</summary>
    public static JournalRow Hint() => new() { IsHint = true };

    /// <summary>Заглушка «Загрузка…» — заполняется через <see cref="Fill"/> из фоновой загрузки.</summary>
    public static JournalRow Loading() => new() { _isLoadingPlaceholder = true };

    public JournalRow(CalendarRecord ev, bool isPast) => Fill(ev, isPast);

    /// <summary>Заполняет строку данными события (заглушка → событие) с уведомлением привязок.</summary>
    public void Fill(CalendarRecord ev, bool isPast)
    {
        OriginalEvent = ev;
        Title         = ev.DisplayTitle;
        TimeText      = ev.AllDay ? "Весь день" : $"{ev.StartDt:HH:mm} – {ev.EndDt:HH:mm}";
        ColorBrush    = new SolidColorBrush(Color.Parse(EventColors.Display(ev.Color)));
        TitleBrush    = isPast
            ? new SolidColorBrush(Color.Parse("#8E8E93"))
            : new SolidColorBrush(Color.Parse("#1C1C1E"));
        IsLoadingPlaceholder = false;
    }
}
