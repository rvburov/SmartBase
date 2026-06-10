using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyPlanner.Core.Models;

namespace MyPlanner.Controls.Components;

public partial class AllDayBar : UserControl
{
    private static readonly double FontSizeBase =
        Application.Current!.Resources["FontSizeBase"] is double v ? v : 12;

    public event EventHandler<CalendarRecord>?    RecordClicked;
    public event EventHandler<CalendarRecord>?    RecordMoved;
    public event EventHandler<CalendarRecord>?    RecordDeleteRequested;
    public event EventHandler<IReadOnlyList<CalendarRecord>>? RecordsDeleteRequested;
    public event EventHandler<DateOnly>? RecordCreationRequested;

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

    private EventHandler<KeyEventArgs>? _windowKeyHandler;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win)
        {
            _windowKeyHandler = (_, ke) =>
            {
                if (ke.Key != Key.Delete && ke.Key != Key.Back) return;
                if (!IsVisible) return;
                if (ke.Source is TextBox or AutoCompleteBox) return;
                if (TryDeleteSelected()) ke.Handled = true;
            };
            win.AddHandler(KeyDownEvent, _windowKeyHandler, handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (e.Root is Avalonia.Controls.Window win && _windowKeyHandler is not null)
        {
            win.RemoveHandler(KeyDownEvent, _windowKeyHandler);
            _windowKeyHandler = null;
        }
    }

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

        // Shift+Click — добавление/снятие в группу. Без Shift на не выделенном —
        // сбрасываем группу и выделяем одиночно.
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (additive)
        {
            if (_selections.ContainsKey(entry.Ev)) _DeselectOne(entry.Ev);
            else                                    _SelectEntry(entry);
        }
        else if (!_selections.ContainsKey(entry.Ev))
        {
            _DeselectAll();
            _SelectEntry(entry);
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

    // Возвращает true, если что-то было выделено и инициировано удаление —
    // родительский Week/DayView использует это для обработки Delete на окне.
    public bool TryDeleteSelected()
    {
        if (_selections.Count == 0) return false;
        if (_selections.Count == 1)
        {
            var only = _selections.Keys.First();
            _DeselectAll();
            RecordDeleteRequested?.Invoke(this, only);
        }
        else
        {
            var batch = _selections.Keys.ToList();
            _DeselectAll();
            RecordsDeleteRequested?.Invoke(this, batch);
        }
        return true;
    }

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

            var targetEnd = col + _dragSourceSpan - 1;
            var bottomRow = _allDayEntries
                .Where(en => en.Ev.Id != _dragEvent?.Id
                          && en.Col <= targetEnd
                          && en.Col + en.Span - 1 >= col)
                .Select(en => en.Row)
                .DefaultIfEmpty(-1)
                .Max() + 1;

            while (EventsPanel.RowDefinitions.Count <= bottomRow)
                EventsPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            Grid.SetColumn(_dragGhost, col);
            Grid.SetRow(_dragGhost, bottomRow);

            // auto-scroll so ghost row is visible
            var ghostContentY = PanelTopMargin + bottomRow * RowH;
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
            DividersPanel.Children.Add(new Border
            {
                BorderBrush     = new SolidColorBrush(Color.Parse("#E8E8E8")),
                BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
            });
        }

        var allDayEvents = events
            .Where(e => e.AllDay || (e.EndDt - e.StartDt).TotalSeconds >= 86400)
            .Where(e => _currentDays.Any(d =>
                DateOnly.FromDateTime(e.StartDt) <= d && d <= DateOnly.FromDateTime(e.EndDt)))
            .DistinctBy(e => e.Id)
            .OrderBy(e => (e.EndDt.Date - e.StartDt.Date).TotalDays < 1 ? 1 : 0)
            .ThenBy(e => e.Title)
            .ToList();

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

            var (root, block, titleBlock, timeBlock, stripBlock) = BuildBlock(ev, span);
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
                    stripBlock.IsVisible  = true;
                },
            };
            _allDayEntries.Add(entry);

            var hoverBg = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
            block.PointerEntered += (_, _) => { if (!isSelected) block.Background = hoverBg; };
            block.PointerExited  += (_, _) => { if (!isSelected) block.Background = normalBg; };
        }

        UpdateAutoHeight();
    }

    private static (Control root, Border colorBorder, TextBlock titleBlock, TextBlock? timeBlock, Border stripBorder) BuildBlock(CalendarRecord ev, int span)
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
        };

        var title = new TextBlock
        {
            Text              = ev.DisplayTitle,
            FontSize          = FontSizeBase,
            FontWeight        = FontWeight.SemiBold,
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
                FontWeight        = FontWeight.Normal,
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

        // Отступы как у событий часовой сетки: слева 1px, справа 2px (см. WeekView
        // LayoutDayEntries) — чтобы плашки «весь день» стояли на тех же позициях.
        var block = new Border
        {
            Background   = normalBg,
            CornerRadius = new CornerRadius(3),
            Height       = 24,
            Margin       = new Thickness(1, 0, 2, 2),
            Cursor       = new Cursor(StandardCursorType.Hand),
            Child        = inner,
        };

        if (span <= 1) return (block, block, title, timeBlock, strip);

        // white opaque wrapper covers internal divider lines
        var wrapper = new Grid { Height = 24, Margin = new Thickness(1, 0, 2, 2) };
        wrapper.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#F2F2F7")), CornerRadius = new CornerRadius(3) });
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
            FontWeight        = FontWeight.SemiBold,
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

    private static Color Darken(Color c, double f = 0.6)
        => Color.FromArgb(c.A, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    // ── context menu ──────────────────────────────────────────────────────────

    // Единое меню области событий. Наполнение зависит от того, попал ли последний
    // правый клик на событие: на событии — «Редактировать/Удалить» (или массовое
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

                // Множественное выделение → только массовое удаление с подтверждением.
                if (_selections.Count > 1 && _selections.ContainsKey(ev))
                {
                    var batch = _selections.Keys.ToList();
                    menu.Items.Add(EventContextMenu.MakeItem(
                        "Удалить", () => { _DeselectAll(); RecordsDeleteRequested?.Invoke(this, batch); }));
                    return;
                }

                menu.Items.Add(EventContextMenu.MakeItem(
                    "Редактировать", () => { _DeselectAll(); RecordClicked?.Invoke(this, ev); }));
                menu.Items.Add(EventContextMenu.MakeItem(
                    "Удалить", () => { _DeselectAll(); RecordDeleteRequested?.Invoke(this, ev); }));
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
            menu.Items.Add(EventContextMenu.MakeItem(
                "Создать запись", () => RecordCreationRequested?.Invoke(this, day)));
        };
        return menu;
    }
}
