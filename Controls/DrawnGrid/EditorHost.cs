using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>Поверхность, над которой стоит накладной редактор: таблица (<see cref="GridSurface"/>) или
/// список свойств страницы записи (<see cref="PropertySurface"/>). Дает геометрию правящейся ячейки при
/// текущей прокрутке и область, в которой редактор виден, и сообщает, когда все это сдвинулось.</summary>
public interface IEditorSurface
{
    /// <summary>Сменилось что-то, от чего зависит положение ячеек: прокрутка, раскладки, размер, токены.</summary>
    event EventHandler? ViewChanged;

    /// <summary>Область содержимого правящейся ячейки в координатах поверхности; null — ячейки нет.</summary>
    Rect? EditorRect(int row, string key);

    /// <summary>Область, в которой редактор показывается (у таблицы — тело под шапкой).</summary>
    Rect EditorClip { get; }

    /// <summary>Ячейка правится: поверхность рисует ее без значения — его показывает редактор.</summary>
    void SetEditingCell(int row, string key);

    void ClearEditingCell();

    /// <summary>Заливка под редактором на всю ячейку (цвет ячейки таблицы); null — без заливки.</summary>
    IBrush? EditorFill(int row, string key) => null;

    /// <summary>Отступы редактора внутри ячейки (текст, прижатый к верху или низу высокой строки);
    /// <paramref name="lines"/> — строк текста в редакторе. Пусто — редактор во всю ячейку.</summary>
    Thickness EditorInset(int row, string key, int lines) => default;

    /// <summary>Куда раскрыть редактор под текст размера <paramref name="need"/>: область содержимого
    /// от ячейки вправо и вниз целыми ячейками, но не дальше видимой области. null — поверхность
    /// раскрытия не умеет (редактор остается в ячейке).</summary>
    Rect? EditorGrowRect(int row, string key, Size need) => null;

    /// <summary>Непрозрачная подложка раскрытого редактора — фон таблицы: соседние ячейки под ним не
    /// должны просвечивать.</summary>
    IBrush? EditorGrowBackground => null;

    /// <summary>Редактор раскрыт за пределы ячейки (область в координатах поверхности) или вернулся
    /// в нее (null): рамка выделения обводит раскрытую область.</summary>
    void SetEditorOverflow(Rect? area) { }
}

/// <summary>
/// Один накладной редактор на всю сетку (этап 4 плана drawn-grid.md). Лежит над поверхностью в той
/// же клетке панели и держит редактор ячейки — настоящий контрол (поле, список, дата, мультисписок,
/// связь, путь файла), который строит фасад по типу поля теми же правилами, что шаблон правки
/// нынешней таблицы.
///
/// Геометрия — область содержимого ячейки (<see cref="GridSurface.EditorRect"/>): редактор кладется
/// в рамку ее размера и выравнивается в ней своими правилами — как в <c>DataGridCell</c>, где
/// содержимое растянуто по обеим осям, поэтому текст правки стоит там же, где текст показа.
/// Прокрутка, ширины, высоты и ресайз двигают редактор вместе с ячейкой
/// (<see cref="GridSurface.ViewChanged"/>); над шапкой он не показывается — обрезка по области строк.
///
/// Текст, прижатый к верху или низу высокой строки, редактор держит не во всю ячейку, а в полосе у
/// края (<see cref="IEditorSurface.EditorInset"/>): каждый редактор центрует свое содержимое, и в
/// полосе оно встает туда же, где нарисован текст, вместе с кнопкой редактора. Цвет ячейки лежит
/// под редактором на всю ячейку (<see cref="IEditorSurface.EditorFill"/>), сами редакторы прозрачные.
///
/// Жизненный цикл правки (когда открыть, что записать, когда закрыть) ведет фасад — здесь только
/// где стоит редактор и какую ячейку он правит. Класс «cellEditor» — для стилей редакторов в ячейке
/// (TableView.axaml): без скруглений и своей рамки, прозрачный фон, целая высота строки текста.
/// </summary>
public sealed class EditorHost : Canvas
{
    private readonly IEditorSurface _surface;
    private readonly Border         _cell = new() { IsVisible = false };
    // Подложка раскрытого редактора: лежит под ним на той же области и закрывает соседние ячейки.
    private readonly Border         _back = new() { IsVisible = false, IsHitTestVisible = false };
    private Rect _clip;

    // Раскрытие поля ввода под длинный текст (настройка «Раскрывать ячейку при вводе»): разрешено
    // ли оно у этой правки и как поле стояло до раскрытия — к этому оно возвращается, когда текст
    // снова помещается в ячейку.
    private bool              _grow;
    private TextWrapping      _boxWrap;
    private VerticalAlignment _boxVAlign;

    // Запас под курсор в конце строки: без него последняя буква упиралась бы в край поля.
    private const double CaretRoom = 2;

    // Стили ищут «Canvas.cellEditor»: селектор типа в Avalonia совпадает с точным типом, а не с
    // наследником, — без подмены ключа стилей ни один из них до редактора не дошел бы.
    protected override Type StyleKeyOverride => typeof(Canvas);

    /// <param name="cellEditorStyle">Класс «cellEditor» для стилей редакторов ячейки таблицы; у списка
    /// свойств редакторы несут свои классы (propValue), и класс не ставится.</param>
    public EditorHost(IEditorSurface surface, bool cellEditorStyle = true)
    {
        _surface = surface;
        if (cellEditorStyle) Classes.Add("cellEditor");
        Children.Add(_back);
        Children.Add(_cell);
        surface.ViewChanged += (_, _) => Place();
    }

    /// <summary>Открытый редактор; null — правки нет.</summary>
    public Control? Editor => _cell.Child;

    public bool IsOpen => _cell.Child is not null;

    /// <summary>Правящаяся ячейка: строка среза и ключ колонки.</summary>
    public int     Row { get; private set; } = -1;
    public string? Key { get; private set; }

    /// <summary>Где стоит редактор (координаты поверхности); null — закрыт или колонки нет в раскладке.</summary>
    public Rect? EditorBounds =>
        IsOpen && _cell.IsVisible ? new Rect(GetLeft(_cell), GetTop(_cell), _cell.Width, _cell.Height) : null;

    /// <summary>Поставить редактор на ячейку. Прежний, если был, снимается без записи — записывает фасад.
    /// <paramref name="lines"/> — сколько строк текста в редакторе (у поля ввода — по набранному;
    /// после набора переводов строки фасад зовет <see cref="Relayout"/>); null — одна строка.
    /// <paramref name="grow"/> — поле ввода раскрывается под длинный текст: вправо по соседним
    /// ячейкам до края видимой области, дальше переносит строки и растет вниз (как в табличных
    /// редакторах); набранное фасад сообщает тем же <see cref="Relayout"/>.</summary>
    public void Open(int row, string key, Control editor, Func<int>? lines = null, bool grow = false)
    {
        Close();
        Row    = row;
        Key    = key;
        _lines = lines;
        _grow  = grow && editor is TextBox;
        if (editor is TextBox box)
        {
            _boxWrap   = box.TextWrapping;
            _boxVAlign = box.VerticalContentAlignment;
        }
        _cell.Child = editor;
        _surface.SetEditingCell(row, key);
        Place();
    }

    private Func<int>? _lines;

    /// <summary>Переставить редактор по текущему тексту (полоса текста сверху или снизу растет вместе
    /// с числом строк).</summary>
    public void Relayout() => Place();

    /// <summary>Запись правящейся ячейки уехала на другую строку среза (структурное обновление) —
    /// редактор едет за ней.</summary>
    public void MoveTo(int row)
    {
        if (Key is null || row == Row) return;
        Row = row;
        _surface.SetEditingCell(row, Key);
        Place();
    }

    public void Close()
    {
        if (_cell.Child is null && Key is null) return;
        _cell.Child      = null;
        _cell.IsVisible  = false;
        _cell.Background = null;
        _cell.Padding    = default;
        _back.IsVisible  = false;
        _lines = null;
        _grow  = false;
        _surface.SetEditorOverflow(null);
        Row = -1;
        Key = null;
        _surface.ClearEditingCell();
    }

    private void Place()
    {
        if (Key is null) return;
        if (_surface.EditorRect(Row, Key) is not { } r) { _cell.IsVisible = false; return; }

        var inset = _surface.EditorInset(Row, Key, _lines?.Invoke() ?? 1);
        var (area, padding) = _grow && _cell.Child is TextBox box ? Grow(box, r, inset) : (r, inset);

        SetLeft(_cell, area.X);
        SetTop(_cell, area.Y);
        _cell.Width      = area.Width;
        _cell.Height     = area.Height;
        _cell.Background = _surface.EditorFill(Row, Key);
        _cell.Padding    = padding;
        _cell.IsVisible  = true;

        var grown = area != r;
        _back.IsVisible = grown;
        if (grown)
        {
            SetLeft(_back, area.X);
            SetTop(_back, area.Y);
            _back.Width      = area.Width;
            _back.Height     = area.Height;
            _back.Background = _surface.EditorGrowBackground;
        }
        _surface.SetEditorOverflow(grown ? area : null);

        var body = _surface.EditorClip;
        if (body == _clip) return;
        _clip = body;
        Clip  = new RectangleGeometry(body);
    }

    /// <summary>
    /// Область раскрытого поля ввода и отступ в ней. Текст помещается в ячейку — поле стоит как
    /// обычно. Не помещается по ширине — поле тянется вправо целыми ячейками до края видимой
    /// области; дальше переносит строки и растет вниз. Первая строка при этом остается там же, где
    /// стояла в ячейке: текст под курсором не прыгает. Текст у края высокой строки и прижатый не
    /// влево не раскрывается — он вставал бы не там, где нарисован.
    /// </summary>
    private (Rect Area, Thickness Padding) Grow(TextBox box, Rect cell, Thickness inset)
    {
        var text = box.Text ?? "";
        if (text.Length == 0 || inset != default
            || box.TextAlignment is not (TextAlignment.Left or TextAlignment.Start or TextAlignment.Justify))
            return Fit(box, cell, inset);

        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight);
        var lineH    = double.IsNaN(box.LineHeight) || box.LineHeight <= 0 ? box.FontSize * 1.3 : box.LineHeight;
        var padX     = box.Padding.Left + box.Padding.Right + CaretRoom;
        // Первая строка — по центру ячейки, как без раскрытия; столько же воздуха снизу.
        var top      = Math.Max(0, (cell.Height - lineH) / 2);

        TextLayout Layout(double width) => new(text, typeface, box.FontSize, null,
            textWrapping: double.IsPositiveInfinity(width) ? TextWrapping.NoWrap : TextWrapping.Wrap,
            maxWidth: width, lineHeight: lineH);

        var line  = Layout(double.PositiveInfinity);
        var needW = Math.Ceiling(line.WidthIncludingTrailingWhitespace + padX);
        var needH = line.Height + 2 * top;
        if (needW <= cell.Width && needH <= cell.Height + 0.5) return Fit(box, cell, inset);

        if (_surface.EditorGrowRect(Row, Key!, new Size(needW, Math.Max(needH, cell.Height))) is not { } area)
            return Fit(box, cell, inset);

        // Шире видимой области — переносим строки по ее ширине и растем вниз.
        var wrap = needW > area.Width + 0.5;
        if (wrap)
        {
            needH = Layout(Math.Max(1, area.Width - padX)).Height + 2 * top;
            area  = _surface.EditorGrowRect(Row, Key!, new Size(area.Width, Math.Max(needH, cell.Height))) ?? area;
        }

        var taller = area.Height > cell.Height + 0.5;
        SetBox(box, wrap ? TextWrapping.Wrap : _boxWrap, taller ? VerticalAlignment.Top : _boxVAlign);
        return (area, taller ? new Thickness(0, top, 0, 0) : default);
    }

    // Текст помещается в ячейку: поле стоит в ней так, как его построил фасад.
    private (Rect Area, Thickness Padding) Fit(TextBox box, Rect cell, Thickness inset)
    {
        SetBox(box, _boxWrap, _boxVAlign);
        return (cell, inset);
    }

    // Свойства поля меняются, только когда отличаются: каждая смена — новая раскладка поля, а
    // переставляется оно на каждую букву и прокрутку.
    private static void SetBox(TextBox box, TextWrapping wrap, VerticalAlignment valign)
    {
        if (box.TextWrapping != wrap) box.TextWrapping = wrap;
        if (box.VerticalContentAlignment != valign) box.VerticalContentAlignment = valign;
    }
}
