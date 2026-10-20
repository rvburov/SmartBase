using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>
/// Панель списка свойств страницы записи (этап 7 плана drawn-grid.md): рисованная поверхность
/// карточек (<see cref="PropertySurface"/>) и накладной редактор (<see cref="EditorHost"/>) над ней.
/// Живет внутри обычного <c>ScrollViewer</c> как логически прокручиваемое содержимое
/// (<see cref="ILogicalScrollable"/>): полоса, колесо и смещение — у прежнего ScrollViewer, панель
/// лишь сообщает протяженность (16 сверху, по 46 на карточку, 10 снизу) и рисует окно.
///
/// Редакторы — настоящие контролы, те же, что стояли в карточках (их строит фабрика панели записи,
/// <see cref="EditorFactory"/>), но живут только под указателем и при фокусе: указатель вошел в
/// полосу контрола значения — редактор кладется над карточкой, и дальше нажатия, каретка,
/// выпадающие списки и подсказки работают у самого контрола, как раньше; указатель ушел с карточки,
/// фокуса и открытого списка нет — редактор снимается, карточка снова рисуется поверхностью.
/// Нажатие без предварительного наведения (сенсорный ввод, клик после прокрутки колесом) ставит
/// редактор на месте, отдает ему фокус и ставит каретку по месту нажатия. Пока редактор открыт,
/// нажатия под ним поднимаются панели записи так же, как нажатия на самой карточке
/// (<see cref="CardPressed"/> с зоной значения), — выделение карточки и нижний блок ведут себя
/// одинаково, стоит ли в ней контрол или рисунок.
/// </summary>
public sealed class PropertyList : Panel, ILogicalScrollable
{
    private const string ValueKey = "value";

    private int      _editing = -1;
    private Control? _editor;
    private bool     _releaseScheduled;
    private bool     _canH;
    private bool     _canV = true;

    public PropertySurface Surface { get; } = new();

    /// <summary>Накладной редактор значения — один на список.</summary>
    public EditorHost Editor { get; }

    /// <summary>Фабрика редактора по индексу карточки (тот же контрол, что стоял в карточке); null —
    /// у карточки нет редактора («Добавить свойство»).</summary>
    public Func<int, Control?>? EditorFactory { get; set; }

    /// <summary>Редактор поставлен над карточкой: индекс и контрол — панель записи привязывает его к
    /// цели нижнего блока и к файл-ячейкам.</summary>
    public event EventHandler<(int Index, Control Editor)>? EditorOpened;

    /// <summary>Редактор снят с карточки — ссылки на него больше держать нельзя.</summary>
    public event EventHandler<(int Index, Control Editor)>? EditorClosed;

    /// <summary>Нажатие на карточке или под ее редактором (зона значения); событие не помечается —
    /// редактор получит его следом.</summary>
    public event EventHandler<PropertyPointerEventArgs>? CardPressed;

    /// <summary>Ручку ⋮ отпустили: новая ширина колонки названий — пора сохранять.</summary>
    public event EventHandler<double>? NameColumnResizeEnded;

    public event EventHandler? ScrollInvalidated;

    public PropertyList()
    {
        Editor = new EditorHost(Surface, cellEditorStyle: false);
        Children.Add(Surface);
        Children.Add(Editor);

        Surface.Pressed        += (_, e) => CardPressed?.Invoke(this, e);
        Surface.HoverChanged   += (_, hit) => OnHover(hit);
        Surface.OffsetChanged  += (_, _) => RaiseScrollInvalidated(EventArgs.Empty);
        Surface.ExtentChanged  += (_, _) => RaiseScrollInvalidated(EventArgs.Empty);
        Surface.NameColumnResizeEnded += (_, _) => NameColumnResizeEnded?.Invoke(this, Surface.NameColumnWidth);

        // Редактор лежит над поверхностью и забирает указатель: наведение — ей (подсветка карточки
        // видна сквозь прозрачный редактор), нажатие под ним — наверх туннелем, до самого редактора,
        // как туннельный обработчик прежней карточки.
        Editor.AddHandler(PointerMovedEvent, (_, e) => Surface.HoverAt(e.GetPosition(Surface)),
                          RoutingStrategies.Bubble, handledEventsToo: true);
        Editor.PointerEntered += (_, e) => Surface.HoverAt(e.GetPosition(Surface));
        // Уход указателя с поверхности или с редактора: наведение снимается, только если его нет ни
        // там, ни там (переход между ними — та же карточка); проверка отложена, потому что уход и
        // вход приходят по очереди.
        Editor.PointerExited  += (_, _) => ScheduleHoverCheck();
        Surface.PointerLeft   += (_, _) => ScheduleHoverCheck();
        Editor.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_editing < 0) return;
            if (e.Source is Visual v && v.GetVisualRoot() is PopupRoot) return;   // клик в выпадающем списке
            CardPressed?.Invoke(this, new PropertyPointerEventArgs(new PropertyHit(_editing, PropertyZone.Value, Surface.CardRect(_editing)), e));
        }, RoutingStrategies.Tunnel);
    }

    // ── Карточки ──────────────────────────────────────────────────────────────

    public IReadOnlyList<PropertyCard> Cards => Surface.Cards;
    public int Count => Surface.Count;

    /// <summary>Заменить карточки: редактор снимается, прокрутка держится.</summary>
    public void SetCards(IReadOnlyList<PropertyCard> cards)
    {
        CloseEditor();
        Surface.SetCards(cards);
    }

    public double NameColumnWidth
    {
        get => Surface.NameColumnWidth;
        set => Surface.NameColumnWidth = value;
    }

    /// <summary>Прокрутить так, чтобы карточка была в окне целиком.</summary>
    public void ScrollIntoView(int index) => Surface.ScrollIntoView(index);

    /// <summary>Прямоугольник карточки в координатах панели.</summary>
    public Rect CardRect(int index) => Surface.CardRect(index);

    /// <summary>Полоса контрола значения в координатах панели.</summary>
    public Rect ValueControlRect(int index) => Surface.ValueControlRect(index);

    /// <summary>Значения перечитываются из модели при кадре — после правки достаточно перерисовать.</summary>
    public void RefreshValues() => Surface.RefreshValues();

    // ── Редактор ──────────────────────────────────────────────────────────────

    /// <summary>Индекс карточки с редактором; −1 — редактора нет.</summary>
    public int EditingIndex => _editing;

    /// <summary>Открытый редактор; null — нет.</summary>
    public Control? CurrentEditor => _editor;

    /// <summary>В редакторе идет ввод или открыт список; одно наведение не считается правкой.</summary>
    public bool IsEditorBusy => _editor is not null && IsBusy(_editor);

    /// <summary>Поставить редактор над карточкой (если его там еще нет) и вернуть его. Занятый
    /// редактор другой карточки (фокус, раскрытый список) не снимается — тогда null.</summary>
    public Control? OpenEditor(int index)
    {
        if (index < 0 || index >= Count) return null;
        if (_editing == index) return _editor;
        if (_editor is not null)
        {
            if (IsBusy(_editor)) return null;
            CloseEditor();
        }
        if (EditorFactory?.Invoke(index) is not { } editor) return null;
        _editing = index;
        _editor  = editor;
        editor.PropertyChanged += OnEditorPropertyChanged;
        Editor.Open(index, ValueKey, editor);
        EditorOpened?.Invoke(this, (index, editor));
        return editor;
    }

    /// <summary>Снять редактор с карточки (без записи — редакторы пишут сами по каждому изменению).</summary>
    public void CloseEditor()
    {
        if (_editor is null) return;
        var (index, editor) = (_editing, _editor);
        editor.PropertyChanged -= OnEditorPropertyChanged;
        _editing = -1;
        _editor  = null;
        Editor.Close();
        EditorClosed?.Invoke(this, (index, editor));
    }

    /// <summary>Нажатие пришло на поверхность в полосу значения без наведения: поставить редактор,
    /// отдать ему фокус и каретку по месту нажатия (поле) или раскрыть список.</summary>
    public Control? OpenEditorAt(int index, Point surfacePoint)
    {
        if (OpenEditor(index) is not { } editor) return null;
        editor.UpdateLayout();
        switch (editor)
        {
            case TextBox box:
                box.Focus();
                PlaceCaret(box, surfacePoint);
                break;
            case ComboBox combo:
                combo.Focus();
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_editor, combo)) combo.IsDropDownOpen = true; }, DispatcherPriority.Loaded);
                break;
            default:
                editor.Focus();
                break;
        }
        return editor;
    }

    // Каретка по точке нажатия — как у поля, получившего клик само: презентер текста знает, какой
    // символ под точкой.
    private void PlaceCaret(TextBox box, Point surfacePoint)
    {
        var presenter = box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault();
        if (presenter is null || presenter.TextLayout is null) { box.CaretIndex = box.Text?.Length ?? 0; return; }
        var local = Surface.TranslatePoint(surfacePoint, presenter) ?? new Point(0, 0);
        var hit   = presenter.TextLayout.HitTestPoint(local);
        box.CaretIndex = Math.Clamp(hit.TextPosition, 0, box.Text?.Length ?? 0);
    }

    // Указатель вошел в полосу контрола значения — редактор ставится на карточку; ушел с карточки —
    // снимается, если не занят.
    private void OnHover(PropertyHit hit)
    {
        if (hit.Zone == PropertyZone.Value && hit.Index != _editing) OpenEditor(hit.Index);
        else if (_editor is not null && hit.Index != _editing) ScheduleRelease();
    }

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == InputElement.IsKeyboardFocusWithinProperty && e.NewValue is false) ScheduleRelease();
    }

    // Проверка «пора ли снять редактор» — отложенно: сразу после ухода фокуса или закрытия списка
    // состояние еще меняется (фокус едет в попап, указатель — в него же).
    private void ScheduleRelease()
    {
        if (_releaseScheduled) return;
        _releaseScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _releaseScheduled = false;
            if (_editor is null || IsBusy(_editor) || PointerOverEditingCard()) return;
            CloseEditor();
        }, DispatcherPriority.Input);
    }

    private bool _hoverCheckScheduled;

    private void ScheduleHoverCheck()
    {
        if (_hoverCheckScheduled) return;
        _hoverCheckScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _hoverCheckScheduled = false;
            if (Surface.IsPointerOver || Editor.IsPointerOver) return;
            Surface.HoverAt(null);
            ScheduleRelease();
        }, DispatcherPriority.Input);
    }

    private bool PointerOverEditingCard() => _editing >= 0 && (Surface.IsPointerOver || Editor.IsPointerOver) && HoverIndex() == _editing;

    private int HoverIndex()
    {
        // Наведение поверхности после ухода указателя в редактор ведется через HoverAt — ее _hover
        // точен; здесь достаточно спросить попадание по последней точке.
        return Surface.LastHit.Index;
    }

    // Занятый редактор: фокус внутри него или раскрытый список (свой попап или окно ComboBox).
    private static bool IsBusy(Control editor)
    {
        if (editor.IsKeyboardFocusWithin) return true;
        if (editor is ComboBox { IsDropDownOpen: true }) return true;
        foreach (var popup in editor.GetVisualDescendants().OfType<Popup>())
            if (popup.IsOpen) return true;
        foreach (var popup in editor.GetLogicalDescendants().OfType<Popup>())
            if (popup.IsOpen) return true;
        return false;
    }

    // ── ILogicalScrollable ────────────────────────────────────────────────────

    public Vector Offset
    {
        get => new(0, Surface.OffsetY);
        set => Surface.OffsetY = value.Y;
    }

    public Size Extent   => new(Bounds.Width, Surface.ExtentHeight);
    public Size Viewport => new(Bounds.Width, Bounds.Height);

    public bool CanHorizontallyScroll { get => _canH; set => _canH = value; }
    public bool CanVerticallyScroll   { get => _canV; set => _canV = value; }
    public bool IsLogicalScrollEnabled => true;

    /// <summary>Шаг колеса — 50, как у обычной (пиксельной) прокрутки ScrollViewer.</summary>
    public Size ScrollSize     => new(0, 50);
    public Size PageScrollSize => new(0, Math.Max(1, Bounds.Height));

    public bool BringIntoView(Control target, Rect targetRect) => false;
    public Control? GetControlInDirection(NavigationDirection direction, Control? from) => null;
    public void RaiseScrollInvalidated(EventArgs e) => ScrollInvalidated?.Invoke(this, e);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(availableSize);
        // Окно — сколько дали (ScrollViewer дает свой видимый размер); протяженность отдаем через Extent.
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                        double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children) child.Arrange(new Rect(finalSize));
        return finalSize;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RaiseScrollInvalidated(EventArgs.Empty);
    }
}
