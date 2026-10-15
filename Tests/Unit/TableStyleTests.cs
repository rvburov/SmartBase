using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Оформление таблицы конструктора: фон, цвет текста и расположение текста на трех уровнях
/// (ячейка, строка, колонка), их хранение строками и арифметика вертикального расположения,
/// общая у рисования ячейки и у ее редактора.
/// </summary>
public sealed class TableStyleTests
{
    private const string Green = "#34C759";
    private const string Red   = "#FF3B30";

    // ── Каскад ────────────────────────────────────────────────────────────────

    /// <summary>Каждый аспект считается сам по себе: точечное перебивает общее, а незаданный
    /// аспект уровня пропускает слово следующему.</summary>
    [Fact]
    public void Resolve_takes_each_aspect_from_the_most_specific_level()
    {
        var cell   = new RowStyle(null, Red, VAlign: CellVAlign.Top);
        var row    = new RowStyle(Green, null, HAlign: CellHAlign.Center);
        var column = new RowStyle(Red, Green, CellHAlign.Right, CellVAlign.Bottom);

        var style = RowStyle.Resolve(cell, row, column);

        Assert.Equal(Green, style.Bg);                    // строка
        Assert.Equal(Red, style.Fg);                      // ячейка
        Assert.Equal(CellHAlign.Center, style.HAlign);    // строка сильнее колонки
        Assert.Equal(CellVAlign.Top, style.VAlign);       // ячейка
    }

    [Fact]
    public void Resolve_of_unset_levels_is_unset()
    {
        Assert.Equal(RowStyle.None, RowStyle.Resolve(RowStyle.None, RowStyle.None, RowStyle.None));
        Assert.True(RowStyle.None.IsEmpty);
    }

    // ── Выставленное последним ────────────────────────────────────────────────

    /// <summary>Между строкой и колонкой видно выставленное позже — по отметкам выставления.</summary>
    [Fact]
    public void The_later_of_row_and_column_wins()
    {
        var row    = new RowStyle(Green, null, HAlign: CellHAlign.Right, At: new StyleStamps(Bg: 30, HAlign: 10));
        var column = new RowStyle(Red,   null, HAlign: CellHAlign.Center, At: new StyleStamps(Bg: 20, HAlign: 20));

        var style = RowStyle.Resolve(RowStyle.None, row, column);

        Assert.Equal(Green, style.Bg);                    // фон строке выставлен позже
        Assert.Equal(CellHAlign.Center, style.HAlign);    // расположение колонке — позже
    }

    /// <summary>Отметка без значения — выставленное «По умолчанию»: оно перебивает более раннее
    /// значение другого уровня, а более позднее значение перебивает его.</summary>
    [Fact]
    public void A_later_default_beats_an_earlier_value()
    {
        var row    = new RowStyle(null, null, HAlign: CellHAlign.Right, At: new StyleStamps(HAlign: 10));
        var reset  = new RowStyle(null, null, At: new StyleStamps(HAlign: 20));

        Assert.Null(RowStyle.Resolve(RowStyle.None, row, reset).HAlign);
        Assert.Equal(CellHAlign.Right,
                     RowStyle.Resolve(RowStyle.None, row with { At = new StyleStamps(HAlign: 30) }, reset).HAlign);
        Assert.False(reset.IsEmpty);   // отметка — тоже оформление: строка хранения живет ради нее
    }

    /// <summary>Ячейка — всегда последняя в своей строке и колонке (их смена снимает аспект у
    /// ячеек), поэтому ее значение видно при любых отметках.</summary>
    [Fact]
    public void The_cell_always_wins()
    {
        var cell   = new RowStyle(null, null, VAlign: CellVAlign.Bottom);
        var column = new RowStyle(null, null, VAlign: CellVAlign.Top, At: new StyleStamps(VAlign: long.MaxValue));

        Assert.Equal(CellVAlign.Bottom, RowStyle.Resolve(cell, RowStyle.None, column).VAlign);
    }

    /// <summary>Оформление до этого правила отметок не несет — там строка сильнее колонки, как
    /// было; выставленное после правила перебивает его.</summary>
    [Fact]
    public void Styles_without_stamps_keep_the_old_order()
    {
        var oldRow    = new RowStyle(null, null, HAlign: CellHAlign.Right);
        var oldColumn = new RowStyle(null, null, HAlign: CellHAlign.Center);
        var newColumn = oldColumn with { At = new StyleStamps(HAlign: 1) };

        Assert.Equal(CellHAlign.Right,  RowStyle.Resolve(RowStyle.None, oldRow, oldColumn).HAlign);
        Assert.Equal(CellHAlign.Center, RowStyle.Resolve(RowStyle.None, oldRow, newColumn).HAlign);
    }

    [Fact]
    public void With_stamps_only_the_given_aspect()
    {
        var style = RowStyle.None.With(StyleAspect.VAlign, "top", 42).With(StyleAspect.Background, null, 7);

        Assert.Equal(CellVAlign.Top, style.VAlign);
        Assert.Equal(new StyleStamps(Bg: 7, VAlign: 42), style.At);
        Assert.Equal(7, style.At.Of(StyleAspect.Background));
    }

    [Fact]
    public void Stamps_strictly_grow()
    {
        var stamps = Enumerable.Range(0, 1000).Select(_ => StyleStamps.Next()).ToList();

        Assert.True(stamps.Zip(stamps.Skip(1)).All(p => p.Second > p.First));
    }

    /// <summary>Строка хранения живет, пока задан хоть один аспект, — расположение текста
    /// считается наравне с цветом.</summary>
    [Fact]
    public void Alignment_alone_makes_style_non_empty()
    {
        Assert.False(new RowStyle(null, null, HAlign: CellHAlign.Left).IsEmpty);
        Assert.False(new RowStyle(null, null, VAlign: CellVAlign.Center).IsEmpty);
    }

    // ── Хранение ──────────────────────────────────────────────────────────────

    [Fact]
    public void Clean_drops_junk_from_the_database()
    {
        var style = RowStyle.Clean("зеленый", Red, "middle", "bottom");

        Assert.Null(style.Bg);
        Assert.Equal(Red, style.Fg);
        Assert.Null(style.HAlign);
        Assert.Equal(CellVAlign.Bottom, style.VAlign);
    }

    [Theory]
    [InlineData(StyleAspect.Background, Green, Green)]
    [InlineData(StyleAspect.Background, "center", null)]
    [InlineData(StyleAspect.Text, Red, Red)]
    [InlineData(StyleAspect.HAlign, "right", "right")]
    [InlineData(StyleAspect.HAlign, "top", null)]
    [InlineData(StyleAspect.HAlign, Green, null)]
    [InlineData(StyleAspect.VAlign, "top", "top")]
    [InlineData(StyleAspect.VAlign, "left", null)]
    [InlineData(StyleAspect.VAlign, null, null)]
    public void Stored_keeps_only_values_valid_for_the_aspect(StyleAspect aspect, string? value, string? expected) =>
        Assert.Equal(expected, RowStyle.Stored(aspect, value));

    [Fact]
    public void With_sets_and_clears_one_aspect()
    {
        var style = RowStyle.None
            .With(StyleAspect.Background, Green)
            .With(StyleAspect.HAlign, "center")
            .With(StyleAspect.VAlign, "bottom");

        Assert.Equal(new RowStyle(Green, null, CellHAlign.Center, CellVAlign.Bottom), style);

        var cleared = style.With(StyleAspect.HAlign, null).With(StyleAspect.Background, null);
        Assert.Equal(new RowStyle(null, null, VAlign: CellVAlign.Bottom), cleared);
        // Мусор не задает аспект, а сбрасывает его — как «без цвета».
        Assert.Null(style.With(StyleAspect.VAlign, "middle").VAlign);
    }

    [Fact]
    public void Each_aspect_has_its_own_column()
    {
        Assert.Equal("Bg", RowStyle.Column(StyleAspect.Background));
        Assert.Equal("Fg", RowStyle.Column(StyleAspect.Text));
        Assert.Equal("HAlign", RowStyle.Column(StyleAspect.HAlign));
        Assert.Equal("VAlign", RowStyle.Column(StyleAspect.VAlign));
    }

    [Fact]
    public void Alignment_round_trips_through_storage()
    {
        foreach (var h in Enum.GetValues<CellHAlign>())
            Assert.Equal(h, CellAlign.ParseH(CellAlign.Store(h)));
        foreach (var v in Enum.GetValues<CellVAlign>())
            Assert.Equal(v, CellAlign.ParseV(CellAlign.Store(v)));
        Assert.Null(CellAlign.Store((CellHAlign?)null));
        Assert.Null(CellAlign.Store((CellVAlign?)null));
    }

    // ── Правило типа ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("number", CellHAlign.Right)]
    [InlineData("text", CellHAlign.Left)]
    [InlineData("date", CellHAlign.Left)]
    [InlineData(null, CellHAlign.Left)]   // «Название»
    public void Number_goes_right_by_default_everything_else_left(string? fieldType, CellHAlign expected) =>
        Assert.Equal(expected, CellAlign.DefaultH(fieldType));

    [Theory]
    [InlineData("text", true)]
    [InlineData("number", true)]
    [InlineData("date", true)]
    [InlineData("select", true)]
    [InlineData("status", true)]
    [InlineData("multiselect", true)]
    [InlineData("url", true)]
    [InlineData("user", true)]
    [InlineData("relation", true)]
    [InlineData("file", true)]
    [InlineData("created_at", true)]
    [InlineData("updated_by", true)]
    [InlineData(null, true)]
    [InlineData("checkbox", false)]   // у галочки нет текста
    public void Alignment_applies_where_the_value_is_text(string? fieldType, bool expected) =>
        Assert.Equal(expected, CellAlign.Applies(fieldType));

    // ── Вертикальное расположение ─────────────────────────────────────────────

    // Обычная строка таблицы: высота 36, строка текста 16 — центр стоит на 10 от верха.
    private const double RowH  = 36;
    private const double LineH = 16;
    private const double Edge  = (RowH - LineH) / 2;

    /// <summary>По центру — прежнее правило, кадры без выравнивания не меняются.</summary>
    [Theory]
    [InlineData(36, 16, 10)]
    [InlineData(100, 16, 42)]
    [InlineData(100, 32, 34)]
    [InlineData(20, 32, 0)]   // текст выше ячейки — от верхнего края, как раньше
    public void Center_matches_the_old_rule(double area, double text, double expected) =>
        Assert.Equal(expected, CellAlign.TextTop(CellVAlign.Center, area, text, Edge));

    /// <summary>В высокой строке текст «сверху» стоит там же, где в обычной строке, а «снизу» —
    /// на том же отступе от низа: соседние столбцы держат одну линию.</summary>
    [Fact]
    public void Top_and_bottom_keep_the_standard_margin_in_a_tall_row()
    {
        Assert.Equal(Edge, CellAlign.TextTop(CellVAlign.Top, 100, LineH, Edge));
        Assert.Equal(100 - LineH - Edge, CellAlign.TextTop(CellVAlign.Bottom, 100, LineH, Edge));
    }

    /// <summary>В строке обычной высоты все три положения совпадают — разницы нет и не должно быть.</summary>
    [Fact]
    public void Standard_row_puts_text_in_the_same_place_for_any_alignment()
    {
        foreach (var v in Enum.GetValues<CellVAlign>())
            Assert.Equal(Edge, CellAlign.TextTop(v, RowH, LineH, Edge));
    }

    /// <summary>«Сверху» не опускает текст ниже центра, «снизу» не поднимает выше: две строки
    /// текста в обычной строке стоят по центру при любом расположении.</summary>
    [Fact]
    public void Top_and_bottom_never_cross_the_center()
    {
        foreach (var v in Enum.GetValues<CellVAlign>())
            Assert.Equal(2, CellAlign.TextTop(v, RowH, 2 * LineH, Edge));
    }

    [Fact]
    public void Text_taller_than_the_cell_starts_at_the_top()
    {
        foreach (var v in Enum.GetValues<CellVAlign>())
            Assert.Equal(0, CellAlign.TextTop(v, 30, 3 * LineH, Edge));
    }

    /// <summary>Редактор ставится в полосу у края ячейки и центрует в ней свое содержимое — оно
    /// обязано встать ровно туда, где нарисован текст показа.</summary>
    [Theory]
    [InlineData(36, 16)]
    [InlineData(60, 16)]
    [InlineData(100, 16)]
    [InlineData(100, 32)]
    [InlineData(36, 32)]
    [InlineData(30, 48)]
    public void Editor_band_centers_content_where_the_text_is_drawn(double area, double text)
    {
        foreach (var v in Enum.GetValues<CellVAlign>())
        {
            var (top, bottom) = CellAlign.EditorInset(v, area, text, Edge);
            var band = area - top - bottom;

            Assert.True(top >= 0 && bottom >= 0, $"{v}: отступы полосы отрицательные");
            Assert.Equal(CellAlign.TextTop(v, area, text, Edge), top + Math.Max(0, (band - text) / 2));
        }
    }

    [Fact]
    public void Centered_text_needs_no_band()
    {
        Assert.Equal((0.0, 0.0), CellAlign.EditorInset(CellVAlign.Center, 100, LineH, Edge));
        Assert.Equal((0.0, 0.0), CellAlign.EditorInset(CellVAlign.Top, RowH, LineH, Edge));
    }
}
