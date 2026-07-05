using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DataBase.Core.Models;

namespace DataBase.Controls.Components;

public partial class AllDayBar : UserControl
{
    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьём при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;

    private static readonly FontWeight WeightBold   = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;
    private static readonly FontWeight WeightNormal = (FontWeight)Application.Current!.FindResource("FontWeightNormal")!;
    // Кисти из общей палитры Colors.axaml (запасной hex — на случай отсутствия ресурса).
    private static readonly IBrush Separator   = Brush("BrushSeparator",   "#E5E5EA");
    private static readonly IBrush SecondaryBg = Brush("BrushSecondaryBg", "#F2F2F7");

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    // Лимит отрисовки: панель строит контрол на КАЖДОЕ событие (плюс поиск свободной строки —
    // квадратичный), тысячи all-day в окне повесили бы UI. Скрытое сверх лимита — строкой
    // «+N ещё» внизу; больше и не проскроллить осмысленно. Сами данные окна уже ограничены
    // чтением (GetCalendarRecordsByRangeCapped), лимит здесь — вторая линия обороны.
    private const int MaxRenderedEvents = 300;

    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordMoved;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<CalendarRecord>?    RecordDuplicateRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDuplicateRequested;
    public event EventHandler<DateOnly>? RecordCreationRequested;

    // Сигнал «здесь что-то выделили». Аргумент = additive (Shift): при обычном
    // выделении родитель (Week/DayView) сбрасывает выделение в часовой сетке
    // (одно на экран); при additive — НЕ сбрасывает, чтобы Shift копил общий
    // набор через обе зоны.
    public event EventHandler<bool>? SelectionRequested;

    // Хуки для общих (кросс-зонных) массовых операций. Родитель задаёт:
    //  • ExternalSelectedCount — сколько выделено ВНЕ бара (в часовой сетке);
    //  • DeleteAllSelected     — удалить общий набор обеих зон разом;
    //  • DuplicateAllSelected  — дублировать общий набор обеих зон разом.
    // Нужны, чтобы контекстные «Удалить»/«Дублировать» в баре действовали на весь набор.
    public Func<int>? ExternalSelectedCount { get; set; }
    public Action?    DeleteAllSelected     { get; set; }
    public Action?    DuplicateAllSelected  { get; set; }

    private const double MinH          = 40;
    private const double MaxH          = 300;
    private const double RowH          = 26.0; // block height (24) + bottom margin (2)
    private const double PanelTopMargin = 2.0;
    private const double ResizeHandleH = 6.0; // запас снизу под «пилюлю»-ручку (overlay)
    private const int    MaxAutoRows   = 4;

    // resize (ручка вынесена в родительский вид — Week/DayView — по аналогии
    // с ресайз-ручкой сайдбара в MainWindow; здесь только сама логика высоты)
    private bool      _isResizing;
    private double    _resizeStartY;
    private double    _resizeStartHeight;

    // selection (set of selected events → deselect-callback for each)
    private readonly Dictionary<CalendarRecord, Action> _selections = new();
    private int     _pressClickCount;

    // drag
    private CalendarRecord?         _dragEvent;
    private Border?        _dragGhost;
    private Control?       _dragSource;
    private Point          _dragStartPos;
    private bool           _isDragging;
    private int            _dragSourceCol;
    private int            _dragSourceSpan;
    private int            _dragSourceRow;



    // data
    private List<DateOnly> _currentDays = [];
    private Dictionary<(int col, int row), bool> _occupied = [];

    private sealed class AllDayEntry(CalendarRecord ev, int col, int span, int row, Control root, Border colorBorder)
    {
        public CalendarRecord Ev          = ev;
        public int     Col         = col;
        public int     Span        = span;
        public int     Row         = row;
        public Control Root        = root;        // what goes in the Grid
        public Border  ColorBorder = colorBorder; // what gets background/hover
        public Action? Select;
        public Action? Deselect;
    }
    private readonly List<AllDayEntry> _allDayEntries = [];

    public AllDayBar()
    {
        InitializeComponent();

        EventsScroll.AddHandler(PointerPressedEvent,  OnEventsPressed,  RoutingStrategies.Bubble, handledEventsToo: true);
        EventsScroll.AddHandler(PointerMovedEvent,    OnEventsMoved,    RoutingStrategies.Bubble, handledEventsToo: true);
        EventsScroll.AddHandler(PointerReleasedEvent, OnEventsReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // Запоминаем позицию правого клика в координатах EventsPanel (там лежат
        // плашки и считается HitTest/колонка), как _gridContextPos в WeekView.
        EventsScroll.AddHandler(PointerPressedEvent, (_, pe) =>
        {
            if (pe.GetCurrentPoint(EventsScroll).Properties.IsRightButtonPressed)
                _menuPos = pe.GetPosition(EventsPanel);
        }, RoutingStrategies.Tunnel);

        // Единое контекстное меню вешаем на ScrollViewer (он растянут на всю зону
        // колонок и хит-тестируется даже на пустых днях; сам EventsPanel сжат по
        // содержимому и без фона). Фреймворк сам открывает меню по правому клику и
        // закрывает предыдущее — дублей «Создать запись» не будет. Состав пунктов
        // зависит от того, попал ли клик на событие — наполняем в Opening.
        EventsScroll.ContextMenu = _BuildContextMenu();
    }

    private Point _menuPos;

    // Delete больше не обрабатывается здесь: родитель (Week/DayView) собирает общий
    // набор выделенных из обеих зон и удаляет разом — см. SelectedRecords/ClearSelection.

    // ── resize ────────────────────────────────────────────────────────────────
    // Вызывается из родительского вида (ручка-«пилюля» живёт там, чтобы не
    // обрезалась границами этого контрола). pointerY — координата Y указателя
    // в системе координат вида, единая между Begin/Update.

    public void ResizeBegin(double pointerY)
    {
        _isResizing        = true;
        _resizeStartY      = pointerY;
        _resizeStartHeight = Bounds.Height;
    }

    public void ResizeUpdate(double pointerY)
    {
        if (!_isResizing) return;
        Height = Math.Clamp(_resizeStartHeight + (pointerY - _resizeStartY), MinH, MaxH);
    }

    public void ResizeEnd() => _isResizing = false;

    // ── pointer events ────────────────────────────────────────────────────────

    private void OnEventsPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(EventsPanel).Properties;
        var pos   = e.GetPosition(EventsPanel);
        var entry = HitTest(pos);

        // Правый клик: выделяем событие под курсором (если оно вне текущей группы),
        // чтобы пункт меню действовал на видимо выделенное — как в режиме «Месяц».
        // Само меню открывает фреймворк (EventsPanel.ContextMenu), наполнение — в Opening.
        if (props.IsRightButtonPressed)
        {
            if (entry is null)
            {
                _DeselectAll();
            }
            else if (!_selections.ContainsKey(entry.Ev))
            {
                _DeselectAll();
                _SelectEntry(entry);
                SelectionRequested?.Invoke(this, false);
            }
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        if (entry is null)
        {
            _DeselectAll();

            if (e.ClickCount >= 2)
            {
                var colW = ColW();
                if (colW > 0)
                {
                    var col = Math.Clamp((int)(pos.X / colW), 0, _currentDays.Count - 1);
                    RecordCreationRequested?.Invoke(this, _currentDays[col]);
                }
                e.Handled = true;
            }
            return;
        }

        _dragEvent      = entry.Ev;
        _dragSource     = entry.Root;
        _dragStartPos   = pos;
        _isDragging     = false;
        _pressClickCount = e.ClickCount;
        _dragSourceCol  = entry.Col;
        _dragSourceSpan = entry.Span;
        _dragSourceRow  = entry.Row;

        // Shift+Click — добавление/снятие в группу (общий набор копится через обе
        // зоны: сетку не сбрасываем). Без Shift на не выделенном — сбрасываем
        // группу и выделяем одиночно (сетка тоже сбрасывается).
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (additive)
        {
            if (_selections.ContainsKey(entry.Ev)) _DeselectOne(entry.Ev);
            else                                    _SelectEntry(entry);
            SelectionRequested?.Invoke(this, true);
        }
        else if (!_selections.ContainsKey(entry.Ev))
        {
            _DeselectAll();
            _SelectEntry(entry);
            SelectionRequested?.Invoke(this, false);
        }

        e.Pointer.Capture(EventsScroll);
        e.Handled = true;
    }

    private void _SelectEntry(AllDayEntry entry)
    {
        entry.Select?.Invoke();
        _selections[entry.Ev] = entry.Deselect ?? (() => { });
    }

    private void _DeselectAll()
    {
        foreach (var d in _selections.Values.ToList()) d();
        _selections.Clear();
    }

    // Внешний сброс выделения (родитель зовёт при выделении в часовой сетке).
    public void ClearSelection() => _DeselectAll();

    // Текущий набор выделенных в баре — родитель объединяет его с выделением
    // часовой сетки, чтобы Delete/«Удалить» сносили общий набор разом.
    public IReadOnlyList<CalendarRecord> SelectedRecords => _selections.Keys.ToList();

    private void _DeselectOne(CalendarRecord ev)
    {
        if (_selections.TryGetValue(ev, out var d))
        {
            d();
            _selections.Remove(ev);
        }
    }

    private void OnEventsMoved(object? sender, PointerEventArgs e)
    {
        if (_dragEvent is null || _dragSource is null) return;

        var pos = e.GetPosition(EventsPanel);

        if (!_isDragging)
        {
            if (Math.Abs(pos.X - _dragStartPos.X) + Math.Abs(pos.Y - _dragStartPos.Y) < 5) return;

            _isDragging = true;

            // drag отменяет multi-select на всех событиях
            _DeselectAll();
            // Оригинал делаем полупрозрачным (видно, откуда тащим). Дубля не будет:
            // в исходной колонке ghost ложится ровно на строку события (см. ниже).
            _dragSource.Opacity = 0.45;

            _dragGhost = CreateGhost(_dragEvent, 24);
            Grid.SetColumn(_dragGhost, _dragSourceCol);
            Grid.SetColumnSpan(_dragGhost, _dragSourceSpan);
            Grid.SetRow(_dragGhost, _dragSourceRow);
            EventsPanel.Children.Add(_dragGhost);
        }

        if (_dragGhost is not null)
        {
            var colW   = ColW();
            var maxCol = Math.Max(0, _currentDays.Count - _dragSourceSpan);
            var col    = Math.Clamp((int)(pos.X / colW), 0, maxCol);

            // В исходной колонке событие остаётся на своей строке (это «то же
            // событие на месте» — без перестановки вниз). В другой колонке ищем
            // первую свободную строку для целевых дней — так же, как при первичной
            // раскладке: ghost занимает дыру выше, а не всегда падает в самый низ.
            int targetRow;
            if (col == _dragSourceCol)
                targetRow = _dragSourceRow;
            else
                targetRow = FirstFreeRow(col, col + _dragSourceSpan - 1);

            while (EventsPanel.RowDefinitions.Count <= targetRow)
                EventsPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            Grid.SetColumn(_dragGhost, col);
            Grid.SetRow(_dragGhost, targetRow);

            // auto-scroll so ghost row is visible
            var ghostContentY = PanelTopMargin + targetRow * RowH;
            var viewH = EventsScroll.Bounds.Height;
            if (viewH > 0)
            {
                if (ghostContentY + RowH > EventsScroll.Offset.Y + viewH)
                    EventsScroll.Offset = new Avalonia.Vector(0, ghostContentY + RowH - viewH);
                else if (ghostContentY < EventsScroll.Offset.Y)
                    EventsScroll.Offset = new Avalonia.Vector(0, ghostContentY);
            }
        }
    }

    private void OnEventsReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragEvent is null) return;

        if (!_isDragging)
        {
            if (_pressClickCount >= 2)
            {
                _DeselectAll();
                RecordClicked?.Invoke(this, _dragEvent);
            }
        }
        else if (_currentDays.Count > 0)
        {
            var pos    = e.GetPosition(EventsPanel);
            var colW   = ColW();
            var maxCol = Math.Max(0, _currentDays.Count - _dragSourceSpan);
            var newCol = Math.Clamp((int)(pos.X / colW), 0, maxCol);
            var delta  = newCol - _dragSourceCol;

            if (delta != 0)
            {
                var duration       = (int)(_dragEvent.EndDt - _dragEvent.StartDt).TotalDays;
                _dragEvent.StartDt = _dragEvent.StartDt.AddDays(delta);
                _dragEvent.EndDt   = _dragEvent.StartDt.AddDays(duration);
                RecordMoved?.Invoke(this, _dragEvent);
            }
        }

        e.Pointer.Capture(null);
        if (_dragGhost is not null) EventsPanel.Children.Remove(_dragGhost);
        if (_dragSource != null) _dragSource.Opacity = 1.0;

        _dragEvent  = null;
        _dragGhost  = null;
        _dragSource = null;
        _isDragging = false;
    }

    // ── data ──────────────────────────────────────────────────────────────────

    public void SetData(IEnumerable<DateOnly> days, IEnumerable<CalendarRecord> events)
    {
        _selections.Clear();
        _allDayEntries.Clear();
        EventsPanel.Children.Clear();
        EventsPanel.ColumnDefinitions.Clear();
        EventsPanel.RowDefinitions.Clear();
        DividersPanel.Children.Clear();

        _currentDays = days.ToList();

        // Делители — UniformGrid с Border-колонками (левая граница, кроме первой), точно
        // как WeekColumns в сетке времени → вертикальные линии совпадают пиксель-в-пиксель.
        DividersPanel.Columns = _currentDays.Count;
        for (var i = 0; i < _currentDays.Count; i++)
        {
            EventsPanel.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            var divider = new Border { BorderBrush = Separator };
            // Толщина левой границы — живой токен LineMetrics (ColumnLines недели привязан
            // так же — линии остаются пиксель-в-пиксель при любом зуме/DPI). Первая колонка
            // без линии. Border пустой (без контента) → Aliased безопасен: линия
            // защёлкивается в целый пиксель, а не размазывается сглаживанием.
            if (i > 0)
            {
                divider.Bind(Border.BorderThicknessProperty, divider.GetResourceObservable("BorderLineLeft"));
                divider.Bind(UseLayoutRoundingProperty, divider.GetResourceObservable("LineUseLayoutRounding"));
                RenderOptions.SetEdgeMode(divider, EdgeMode.Aliased);
            }
            DividersPanel.Children.Add(divider);
        }

        var allDayEvents = events
            .Where(e => e.AllDay || (e.EndDt - e.StartDt).TotalSeconds >= 86400)
            .Where(e => _currentDays.Any(d =>
                DateOnly.FromDateTime(e.StartDt) <= d && d <= DateOnly.FromDateTime(e.EndDt)))
            .DistinctBy(e => (e.Id, e.FieldId))   // событие = (запись, поле-дата): не схлопываем разные даты одной записи
            // Сначала многодневные, затем однодневные; внутри группы — по времени
            // начала (раньше → выше), при равенстве — по названию для стабильности.
            .OrderBy(e => (e.EndDt.Date - e.StartDt.Date).TotalDays < 1 ? 1 : 0)
            .ThenBy(e => e.StartDt)
            .ThenBy(e => e.Title)
            .ToList();

        // Сверх лимита не рисуем — только строка «+N ещё» внизу (см. MaxRenderedEvents).
        var hiddenCount = Math.Max(0, allDayEvents.Count - MaxRenderedEvents);
        if (hiddenCount > 0)
            allDayEvents = allDayEvents.Take(MaxRenderedEvents).ToList();

        var occupied = new Dictionary<(int col, int row), bool>();
        _occupied = occupied;

        foreach (var ev in allDayEvents)
        {
            var evStart  = DateOnly.FromDateTime(ev.StartDt);
            var evEnd    = DateOnly.FromDateTime(ev.EndDt);
            var startCol = _currentDays.FindIndex(d => d >= evStart);
            var endCol   = _currentDays.FindLastIndex(d => d <= evEnd);
            if (startCol < 0) startCol = 0;
            if (endCol   < 0) endCol   = _currentDays.Count - 1;
            var span = endCol - startCol + 1;

            var row = 0;
            while (true)
            {
                var conflict = false;
                for (var c = startCol; c <= endCol; c++)
                    if (occupied.ContainsKey((c, row))) { conflict = true; break; }
                if (!conflict) break;
                row++;
            }
            for (var c = startCol; c <= endCol; c++)
                occupied[(c, row)] = true;

            while (EventsPanel.RowDefinitions.Count <= row)
                EventsPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Есть ли начало/конец события в пределах видимых дней. Если событие
            // тянется за границу недели/дня — с того края cap == false.
            var leftCap  = evStart >= _currentDays[0];
            var rightCap = evEnd   <= _currentDays[^1];

            var (root, block, titleBlock, timeBlock, stripBlock) = BuildBlock(ev, span, leftCap, rightCap);
            Grid.SetColumn(root, startCol);
            Grid.SetColumnSpan(root, span);
            Grid.SetRow(root, row);
            EventsPanel.Children.Add(root);

            var color      = Color.Parse(EventColors.Display(ev.Color));
            var normalBg   = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
            var solidBrush = new SolidColorBrush(color);
            var darkBrush  = new SolidColorBrush(Darken(color));

            var isSelected = false;

            var entry = new AllDayEntry(ev, startCol, span, row, root, block)
            {
                Select = () =>
                {
                    isSelected            = true;
                    block.Background      = solidBrush;
                    titleBlock.Foreground = Brushes.White;
                    if (timeBlock != null) timeBlock.Foreground = Brushes.White;
                    stripBlock.IsVisible  = false;
                },
                Deselect = () =>
                {
                    isSelected            = false;
                    block.Background      = normalBg;
                    titleBlock.Foreground = darkBrush;
                    if (timeBlock != null) timeBlock.Foreground = darkBrush;
                    stripBlock.IsVisible  = leftCap; // без начала — полоски нет
                },
            };
            _allDayEntries.Add(entry);

            var hoverBg = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
            block.PointerEntered += (_, _) => { if (!isSelected) block.Background = hoverBg; };
            block.PointerExited  += (_, _) => { if (!isSelected) block.Background = normalBg; };
        }

        // «+N ещё» — не отрисованные сверх лимита, строкой на всю ширину внизу
        // (как подпись в ячейке «Месяца»).
        if (hiddenCount > 0)
        {
            var moreRow = EventsPanel.RowDefinitions.Count;
            EventsPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var more = new TextBlock
            {
                Text       = $"+{hiddenCount} ещё",
                FontSize   = FontSizeBase,
                Foreground = Brush("BrushSecondaryText", "#8E8E93"),
                Margin     = new Thickness(6, 2, 0, 2),
            };
            Grid.SetColumn(more, 0);
            Grid.SetColumnSpan(more, Math.Max(1, _currentDays.Count));
            Grid.SetRow(more, moreRow);
            EventsPanel.Children.Add(more);
        }

        UpdateAutoHeight();
    }

    // leftCap/rightCap: событие начинается/заканчивается в пределах видимых дней.
    // Где cap == false — событие «тянется» за границу недели/дня: с той стороны нет
    // скругления и бокового отступа (стыкуется встык), а слева без начала — без
    // цветной полоски. По аналогии с многодневным баром в режиме «Месяц».
    private static (Control root, Border colorBorder, TextBlock titleBlock, TextBlock? timeBlock, Border stripBorder) BuildBlock(CalendarRecord ev, int span, bool leftCap = true, bool rightCap = true)
    {
        var color      = Color.Parse(EventColors.Display(ev.Color));
        var normalBg   = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B));
        var darkBrush  = new SolidColorBrush(Darken(color));
        var colorBrush = new SolidColorBrush(color);

        var strip = new Border
        {
            Background        = colorBrush,
            CornerRadius      = new CornerRadius(2),
            Width             = 3,
            Margin            = new Thickness(2, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Stretch,
            IsVisible         = leftCap, // полоска только у начала события
        };

        var title = new TextBlock
        {
            Text              = ev.DisplayTitle,
            FontSize          = FontSizeBase,
            FontWeight        = WeightBold,
            Foreground        = darkBrush,
            TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)));
        inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        inner.Children.Add(strip);

        TextBlock? timeBlock = null;
        if (!ev.AllDay)
        {
            title.Margin = new Thickness(4, 0, 0, 0);

            var timeTb = new TextBlock
            {
                Text              = $", {ev.StartDt:HH:mm}",
                FontSize          = FontSizeBase,
                FontWeight        = WeightNormal,
                Foreground        = darkBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 4, 0),
            };
            timeBlock = timeTb;

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(title);
            stack.Children.Add(timeTb);

            void UpdateMaxWidth()
            {
                var tw = timeTb.Bounds.Width + timeTb.Margin.Right;
                var sw = stack.Bounds.Width;
                if (sw > 0)
                    title.MaxWidth = Math.Max(0, sw - tw);
            }
            stack.SizeChanged += (_, _) => UpdateMaxWidth();
            timeTb.SizeChanged += (_, _) => UpdateMaxWidth();

            Grid.SetColumn(stack, 1);
            inner.Children.Add(stack);
        }
        else
        {
            title.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(title, 1);
            inner.Children.Add(title);
        }

        // Скругления только с того края, где у события есть начало/конец; где
        // событие обрезано границей недели — угол прямой.
        var cr = new CornerRadius(
            leftCap  ? 3 : 0, rightCap ? 3 : 0,
            rightCap ? 3 : 0, leftCap  ? 3 : 0);

        // Отступы как у событий часовой сетки: слева 1px, справа 2px (см. WeekView
        // LayoutDayEntries). С обрезанного края отступ убираем — плашка стыкуется
        // встык через границу недели.
        var margin = new Thickness(leftCap ? 1 : 0, 0, rightCap ? 2 : 0, 2);

        var block = new Border
        {
            Background   = normalBg,
            CornerRadius = cr,
            Height       = 24,
            Margin       = margin,
            Cursor       = new Cursor(StandardCursorType.Hand),
            Child        = inner,
        };

        if (span <= 1) return (block, block, title, timeBlock, strip);

        // white opaque wrapper covers internal divider lines
        var wrapper = new Grid { Height = 24, Margin = margin };
        wrapper.Children.Add(new Border { Background = SecondaryBg, CornerRadius = cr });
        block.Margin = new Thickness(0);
        wrapper.Children.Add(block);
        return (wrapper, block, title, timeBlock, strip);
    }

    private static Border CreateGhost(CalendarRecord ev, double height)
    {
        var color      = Color.Parse(EventColors.Display(ev.Color));
        var colorBrush = new SolidColorBrush(color);

        var strip = new Border
        {
            Background        = colorBrush,
            CornerRadius      = new CornerRadius(2),
            Width             = 3,
            Margin            = new Thickness(2, 2, 0, 2),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var title = new TextBlock
        {
            Text              = ev.DisplayTitle,
            FontSize          = FontSizeBase,
            FontWeight        = WeightBold,
            Foreground        = Brushes.White,
            TextTrimming      = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(4, 0, 0, 0),
        };

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)));
        inner.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        inner.Children.Add(strip);

        if (!ev.AllDay)
        {
            title.Text   = $"{ev.DisplayTitle}, {ev.StartDt:HH:mm}";
            title.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(title, 1);
            inner.Children.Add(title);
        }
        else
        {
            title.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(title, 1);
            inner.Children.Add(title);
        }

        return new Border
        {
            Background       = colorBrush,
            CornerRadius     = new CornerRadius(3),
            Height           = height,
            Margin           = new Thickness(1, 0, 2, 2),
            IsHitTestVisible = false,
            Child            = inner,
        };
    }

    public void SetData(DateOnly day, IEnumerable<CalendarRecord> events) =>
        SetData([day], events);

    public void ClearData()
    {
        _selections.Clear();
        _allDayEntries.Clear();
        _occupied.Clear();
        EventsPanel.Children.Clear();
        EventsPanel.ColumnDefinitions.Clear();
        EventsPanel.RowDefinitions.Clear();
        DividersPanel.Children.Clear();
        UpdateAutoHeight();
    }

    // ── auto height ──────────────────────────────────────────────────────────

    private void UpdateAutoHeight()
    {
        var rows = EventsPanel.RowDefinitions.Count;
        if (rows == 0)
        {
            Height = MinH;
            return;
        }
        var contentH    = PanelTopMargin + rows * RowH;
        var maxContentH = PanelTopMargin + MaxAutoRows * RowH;
        Height = Math.Clamp(contentH + ResizeHandleH, MinH, maxContentH + ResizeHandleH);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private AllDayEntry? HitTest(Point pos)
    {
        var colW = ColW();
        if (colW <= 0) return null;
        var col = (int)(pos.X / colW);
        var row = (int)(pos.Y / RowH);
        return _allDayEntries.FirstOrDefault(h =>
            col >= h.Col && col < h.Col + h.Span && h.Row == row);
    }

    private double ColW() =>
        _currentDays.Count > 0 ? EventsScroll.Bounds.Width / _currentDays.Count : 0;

    // Первая строка, где все колонки [startCol..endCol] свободны от других событий
    // (перетаскиваемое исключаем). Заполняет «дыры», образованные многодневными
    // блоками, вместо того чтобы всегда падать в самый низ списка.
    private int FirstFreeRow(int startCol, int endCol)
    {
        for (var row = 0; ; row++)
        {
            var occupied = _allDayEntries.Any(en =>
                !(_dragEvent is not null && en.Ev.Id == _dragEvent.Id && en.Ev.FieldId == _dragEvent.FieldId)
                && en.Row == row
                && en.Col <= endCol
                && en.Col + en.Span - 1 >= startCol);
            if (!occupied) return row;
        }
    }

    private static Color Darken(Color c, double f = 0.6)
        => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    // ── context menu ──────────────────────────────────────────────────────────

    // Единое меню области событий. Наполнение зависит от того, попал ли последний
    // правый клик на событие: на событии — «Изменить/Удалить» (или массовое
    // удаление при множественном выделении), на пустом месте — «Создать запись».
    // Меню назначается свойством ContextMenu, поэтому фреймворк сам открывает его
    // по правому клику и закрывает предыдущее — дублей «Создать запись» не будет.
    private ContextMenu _BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Opening += (_, args) =>
        {
            menu.Items.Clear();

            var entry = HitTest(_menuPos);
            if (entry is not null)
            {
                var ev = entry.Ev;

                // Общий набор обеих зон: в баре + в часовой сетке (ExternalSelectedCount).
                var totalSelected = _selections.Count + (ExternalSelectedCount?.Invoke() ?? 0);

                // Множественное выделение → массовые «Дублировать»/«Удалить» всего набора
                // разом (как по Delete). Делегируем родителю, т.к. он знает обе зоны;
                // резерв без родителя: набор снимается ДО сброса выделения.
                if (totalSelected > 1 && _selections.ContainsKey(ev))
                {
                    menu.Items.Add(EventContextMenu.MakeDuplicateItem(
                        () => (DuplicateAllSelected ??
                            (() => { var batch = _selections.Keys.ToList(); _DeselectAll(); RecordsDuplicateRequested?.Invoke(this, batch); }))()));
                    menu.Items.Add(EventContextMenu.MakeDeleteItem(
                        () => (DeleteAllSelected ??
                            (() => { var batch = _selections.Keys.ToList(); _DeselectAll(); RecordsDeleteRequested?.Invoke(this, batch); }))()));
                    return;
                }

                menu.Items.Add(EventContextMenu.MakeEditItem(
                    () => { _DeselectAll(); RecordClicked?.Invoke(this, ev); }));
                menu.Items.Add(EventContextMenu.MakeDuplicateItem(
                    () => { _DeselectAll(); RecordDuplicateRequested?.Invoke(this, ev); }));
                menu.Items.Add(EventContextMenu.MakeDeleteItem(
                    () => { _DeselectAll(); RecordDeleteRequested?.Invoke(this, ev); }));
                return;
            }

            // Пустое место — «Создать запись» в день под курсором.
            var colW = ColW();
            if (colW <= 0 || _currentDays.Count == 0)
            {
                args.Cancel = true;
                return;
            }
            var col = Math.Clamp((int)(_menuPos.X / colW), 0, _currentDays.Count - 1);
            var day = _currentDays[col];
            menu.Items.Add(EventContextMenu.MakeCreateItem(
                () => RecordCreationRequested?.Invoke(this, day)));
        };
        return menu;
    }
}
