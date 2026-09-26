using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

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
/// Жизненный цикл правки (когда открыть, что записать, когда закрыть) ведет фасад — здесь только
/// где стоит редактор и какую ячейку он правит. Класс «cellEditor» — для стилей редакторов в ячейке
/// (TableView.axaml): без скруглений и своей рамки, прозрачный фон, целая высота строки текста.
/// </summary>
public sealed class EditorHost : Canvas
{
    private readonly IEditorSurface _surface;
    private readonly Border         _cell = new() { IsVisible = false };
    private Rect _clip;

    // Стили ищут «Canvas.cellEditor»: селектор типа в Avalonia совпадает с точным типом, а не с
    // наследником, — без подмены ключа стилей ни один из них до редактора не дошел бы.
    protected override Type StyleKeyOverride => typeof(Canvas);

    /// <param name="cellEditorStyle">Класс «cellEditor» для стилей редакторов ячейки таблицы; у списка
    /// свойств редакторы несут свои классы (propValue), и класс не ставится.</param>
    public EditorHost(IEditorSurface surface, bool cellEditorStyle = true)
    {
        _surface = surface;
        if (cellEditorStyle) Classes.Add("cellEditor");
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

    /// <summary>Поставить редактор на ячейку. Прежний, если был, снимается без записи — записывает фасад.</summary>
    public void Open(int row, string key, Control editor)
    {
        Close();
        Row = row;
        Key = key;
        _cell.Child = editor;
        _surface.SetEditingCell(row, key);
        Place();
    }

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
        _cell.Child     = null;
        _cell.IsVisible = false;
        Row = -1;
        Key = null;
        _surface.ClearEditingCell();
    }

    private void Place()
    {
        if (Key is null) return;
        if (_surface.EditorRect(Row, Key) is not { } r) { _cell.IsVisible = false; return; }

        SetLeft(_cell, r.X);
        SetTop(_cell, r.Y);
        _cell.Width     = r.Width;
        _cell.Height    = r.Height;
        _cell.IsVisible = true;

        var body = _surface.EditorClip;
        if (body == _clip) return;
        _clip = body;
        Clip  = new RectangleGeometry(body);
    }
}
