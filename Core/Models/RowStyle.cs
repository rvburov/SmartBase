namespace SmartBase.Core.Models;

/// Оформление одного уровня таблицы — ячейки, строки или колонки: фон, цвет текста и
/// расположение текста по горизонтали и вертикали. Аспект `null` = «не задан»: строка в БД
/// живет, пока задан хоть один (тот же принцип, что у column_meta — см. RemoveColumnMetaIfEmpty).
///
/// Цвета — hex «#RRGGBB» из [AppPalette]; НАСЫЩЕННЫЕ. Приглушение фона и
/// затемнение текста — забота отрисовки (Utils/ColorTools), а не хранения: так один
/// и тот же цвет одинаково выглядит в таблице, календаре и задачах.
///
/// Расположение текста хранится словами (<see cref="CellAlign.Store(CellHAlign?)"/>), а не
/// числами: строку базы читает и человек, и сторонний разбор.
public readonly record struct RowStyle(string? Bg, string? Fg, CellHAlign? HAlign = null, CellVAlign? VAlign = null)
{
    public static readonly RowStyle None = default;

    public bool IsEmpty => Bg is null && Fg is null && HAlign is null && VAlign is null;

    /// Отбрасывает мусор из БД (пустые строки, битый hex, незнакомое слово) — иначе одно кривое
    /// значение роняло бы отрисовку всей строки таблицы.
    public static RowStyle Clean(string? bg, string? fg, string? hAlign = null, string? vAlign = null) =>
        new(AppPalette.IsHex(bg) ? bg : null, AppPalette.IsHex(fg) ? fg : null,
            CellAlign.ParseH(hAlign), CellAlign.ParseV(vAlign));

    /// ЕДИНСТВЕННОЕ место, где описан приоритет оформления. Точечное перебивает общее:
    /// ячейка → строка → колонка. Все аспекты считаются одинаково и независимо друг от друга:
    /// колонка «по центру» и ячейка в ней «сверху» дают текст по центру и сверху.
    ///
    /// Все три уровня — оформление ЭТОЙ таблицы. Был еще сквозной цвет записи
    /// (db_records.Color), который подмешивался в заливку строки и всплывал в календаре,
    /// задачах и «Списке»; он убран — цвет строки в таблице читается как оформление
    /// таблицы, а не как свойство записи.
    ///
    /// Сюда же позже встанет слой правил («Статус = Готово → зеленый») — между
    /// ручными уровнями и дефолтом; ни схема, ни вызывающий код не изменятся.
    public static RowStyle Resolve(RowStyle cell, RowStyle row, RowStyle column) =>
        new(Bg:     cell.Bg     ?? row.Bg     ?? column.Bg,
            Fg:     cell.Fg     ?? row.Fg     ?? column.Fg,
            HAlign: cell.HAlign ?? row.HAlign ?? column.HAlign,
            VAlign: cell.VAlign ?? row.VAlign ?? column.VAlign);

    /// Тот же стиль с одним замененным аспектом. <paramref name="value"/> — в формате хранения
    /// (hex, «left», «top»…); null или мусор — аспект сброшен.
    public RowStyle With(StyleAspect aspect, string? value) => aspect switch
    {
        StyleAspect.Background => this with { Bg = Stored(aspect, value) },
        StyleAspect.Text       => this with { Fg = Stored(aspect, value) },
        StyleAspect.HAlign     => this with { HAlign = CellAlign.ParseH(value) },
        StyleAspect.VAlign     => this with { VAlign = CellAlign.ParseV(value) },
        _                      => this,
    };

    /// Значение аспекта, годное к записи в базу: цвет — hex, расположение — свое слово;
    /// все остальное — null («не задан»).
    public static string? Stored(StyleAspect aspect, string? value) => aspect switch
    {
        StyleAspect.Background or StyleAspect.Text => AppPalette.IsHex(value) ? value : null,
        StyleAspect.HAlign                         => CellAlign.Store(CellAlign.ParseH(value)),
        StyleAspect.VAlign                         => CellAlign.Store(CellAlign.ParseV(value)),
        _                                          => null,
    };

    /// Столбец аспекта — одинаковый у cell_style, record_display и column_meta (сырые пути
    /// записи собирают по нему SQL).
    public static string Column(StyleAspect aspect) => aspect switch
    {
        StyleAspect.Background => "Bg",
        StyleAspect.Text       => "Fg",
        StyleAspect.HAlign     => "HAlign",
        StyleAspect.VAlign     => "VAlign",
        _                      => throw new ArgumentOutOfRangeException(nameof(aspect), aspect, null),
    };
}

/// Какой аспект оформления правит вызов: заливка, цвет текста или расположение текста. Один и
/// тот же набор уровней (строка, ячейка, колонка) несет все четыре, поэтому аспект — параметр,
/// а не пара близнецов-методов на каждый уровень.
public enum StyleAspect
{
    Background,
    Text,
    HAlign,
    VAlign,
}

/// Где стоит текст ячейки по горизонтали.
public enum CellHAlign { Left, Center, Right }

/// Где стоит текст ячейки по вертикали.
public enum CellVAlign { Top, Center, Bottom }

/// <summary>
/// Расположение текста в ячейке таблицы: хранение словами, правило типа по умолчанию и
/// арифметика вертикали, общая у рисования ячейки и у ее редактора — иначе текст сдвигался
/// бы при входе в правку.
/// </summary>
public static class CellAlign
{
    public static CellHAlign? ParseH(string? stored) => stored switch
    {
        "left"   => CellHAlign.Left,
        "center" => CellHAlign.Center,
        "right"  => CellHAlign.Right,
        _        => null,
    };

    public static CellVAlign? ParseV(string? stored) => stored switch
    {
        "top"    => CellVAlign.Top,
        "center" => CellVAlign.Center,
        "bottom" => CellVAlign.Bottom,
        _        => null,
    };

    public static string? Store(CellHAlign? align) => align switch
    {
        CellHAlign.Left   => "left",
        CellHAlign.Center => "center",
        CellHAlign.Right  => "right",
        _                 => null,
    };

    public static string? Store(CellVAlign? align) => align switch
    {
        CellVAlign.Top    => "top",
        CellVAlign.Center => "center",
        CellVAlign.Bottom => "bottom",
        _                 => null,
    };

    /// <summary>Горизонталь, когда ни один уровень ее не задал: число — вправо, остальное —
    /// влево. «По умолчанию» в меню возвращает именно сюда. По вертикали правило одно —
    /// по центру.</summary>
    public static CellHAlign DefaultH(string? fieldType) =>
        fieldType == "number" ? CellHAlign.Right : CellHAlign.Left;

    /// <summary>Двигается ли содержимое ячейки этого типа: у галочки нет текста, она стоит на
    /// месте, и пункта в меню у нее нет.</summary>
    public static bool Applies(string? fieldType) => fieldType != "checkbox";

    /// <summary>
    /// Верх текста в области ячейки высотой <paramref name="areaHeight"/>. <paramref name="edge"/> —
    /// отступ текста от края в строке обычной высоты (половина разницы высот строки таблицы и
    /// строки текста): «сверху» в высокой строке ставит текст туда же, где он стоит в обычной,
    /// «снизу» — на тот же отступ от низа, поэтому соседние столбцы держат одну линию. Ближе к
    /// центру, чем сам центр, край не заходит: в строке обычной высоты все три положения
    /// совпадают. Текст выше ячейки начинается от ее верха — как и раньше.
    /// </summary>
    public static double TextTop(CellVAlign align, double areaHeight, double textHeight, double edge)
    {
        var center = Math.Max(0, (areaHeight - textHeight) / 2);
        var margin = Math.Min(Math.Max(0, edge), center);
        return align switch
        {
            CellVAlign.Top    => margin,
            CellVAlign.Bottom => Math.Max(0, areaHeight - textHeight - margin),
            _                 => center,
        };
    }

    /// <summary>
    /// Отступы полосы, в которую ставится редактор ячейки: у текста сверху и снизу это полоса у
    /// края высотой «текст плюс два отступа края» — редактор центрует в ней свое содержимое, и
    /// оно встает ровно туда, где нарисован текст (<see cref="TextTop"/>). По центру полоса не
    /// нужна: редактор занимает всю ячейку, как раньше.
    /// </summary>
    public static (double Top, double Bottom) EditorInset(CellVAlign align, double areaHeight, double textHeight, double edge)
    {
        if (align == CellVAlign.Center) return (0, 0);
        var center = Math.Max(0, (areaHeight - textHeight) / 2);
        var margin = Math.Min(Math.Max(0, edge), center);
        var rest   = Math.Max(0, areaHeight - textHeight - 2 * margin);
        return align == CellVAlign.Top ? (0, rest) : (rest, 0);
    }
}
