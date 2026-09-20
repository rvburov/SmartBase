using SmartBase.Core.Documents;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Перевод координат страницы PDF в координаты показанной страницы
/// (<see cref="DocPageGeometry"/>).
///
/// Проверка арифметики выглядит мелочью ровно до первой ошибки: у PDF ось Y идет СНИЗУ ВВЕРХ, у
/// экрана — сверху вниз, и перепутанный переворот ставит подсветку не на то слово. Заметить это
/// глазами можно только зная, что искать: желтый прямоугольник честно лежит на строке, просто не
/// на той. Отсюда же второе правило — прямоугольник считается от ВЕРХА слова, а не от низа,
/// иначе подсветка уезжает ровно на высоту буквы.
/// </summary>
public sealed class DocPageGeometryTests
{
    // Лист А4 в пунктах.
    private const double PageHeight = 842;

    [Fact]
    public void Top_of_the_page_stays_at_the_top_of_the_screen()
    {
        // Слово у самой шапки страницы: Top почти равен высоте листа.
        var box    = new DocPageBox(Left: 50, Bottom: 800, Right: 150, Top: 820);
        var screen = DocPageGeometry.ToScreen(box, PageHeight, scale: 1);

        Assert.Equal(22, screen.Y, 3);        // 842 − 820
        Assert.Equal(50, screen.X, 3);
        Assert.Equal(100, screen.Width, 3);
        Assert.Equal(20,  screen.Height, 3);
    }

    [Fact]
    public void Bottom_of_the_page_stays_at_the_bottom()
    {
        var box    = new DocPageBox(Left: 50, Bottom: 20, Right: 150, Top: 40);
        var screen = DocPageGeometry.ToScreen(box, PageHeight, scale: 1);

        Assert.Equal(802, screen.Y, 3);       // 842 − 40
    }

    /// <summary>Масштаб растягивает все четыре числа, а не только размеры.</summary>
    [Fact]
    public void Scale_applies_to_position_too()
    {
        var box  = new DocPageBox(50, 800, 150, 820);
        var one  = DocPageGeometry.ToScreen(box, PageHeight, scale: 1);
        var two  = DocPageGeometry.ToScreen(box, PageHeight, scale: 2);

        Assert.Equal(one.X      * 2, two.X, 3);
        Assert.Equal(one.Y      * 2, two.Y, 3);
        Assert.Equal(one.Width  * 2, two.Width, 3);
        Assert.Equal(one.Height * 2, two.Height, 3);
    }

    [Fact]
    public void Padding_grows_the_box_in_all_four_directions()
    {
        var box   = new DocPageBox(50, 800, 150, 820);
        var tight = DocPageGeometry.ToScreen(box, PageHeight, scale: 1);
        var loose = DocPageGeometry.ToScreen(box, PageHeight, scale: 1, padding: 2);

        Assert.Equal(tight.X - 2,      loose.X, 3);
        Assert.Equal(tight.Y - 2,      loose.Y, 3);
        Assert.Equal(tight.Width  + 4, loose.Width, 3);
        Assert.Equal(tight.Height + 4, loose.Height, 3);
    }

    [Fact]
    public void Scale_comes_from_the_width()
    {
        Assert.Equal(2, DocPageGeometry.ScaleFor(displayWidth: 1190, pageWidth: 595), 3);
        // Страница нулевой ширины — не повод делить на ноль.
        Assert.Equal(1, DocPageGeometry.ScaleFor(displayWidth: 500, pageWidth: 0), 3);
    }

    /// <summary>
    /// Слово, нарисованное в верхней трети листа, и на экране обязано оказаться в верхней трети.
    /// Проверка «от смысла»: она падает при любой путанице с переворотом, даже если отдельные
    /// числа сойдутся.
    /// </summary>
    [Theory]
    [InlineData(700, 0.15)]   // близко к шапке
    [InlineData(421, 0.50)]   // середина листа
    [InlineData(100, 0.88)]   // низ листа
    public void Word_keeps_its_place_on_the_sheet(double bottomPt, double expectedShare)
    {
        var box    = new DocPageBox(50, bottomPt, 150, bottomPt + 12);
        var screen = DocPageGeometry.ToScreen(box, PageHeight, scale: 1);

        Assert.Equal(expectedShare, screen.Y / PageHeight, 1);
    }
}
