using DocumentFormat.OpenXml.Spreadsheet;
using SmartBase.Core.Documents;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Цвета книги Excel (<see cref="DocPalette"/>).
///
/// Цвет в книге почти никогда не лежит цветом: это номер в палитре или в теме, да еще и с
/// осветлением. Ошибка здесь тихая вдвойне — таблица рисуется, ничего не падает, просто линии
/// оказываются не того цвета, а то и белым по белому.
/// </summary>
public sealed class DocPaletteTests
{
    // ── Запись цвета ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FFFF0000", 0xFFFF0000u)]
    [InlineData("FF0000",   0xFFFF0000u)]   // без непрозрачности — значит полная
    [InlineData("00FF00",   0xFF00FF00u)]
    [InlineData("не цвет",  0u)]
    [InlineData("",         0u)]
    public void Color_is_read_from_its_record(string hex, uint argb)
    {
        Assert.Equal(argb, DocPalette.FromHex(hex));
    }

    [Fact]
    public void Plain_color_is_taken_as_it_is()
    {
        Assert.Equal(0xFF123456u, DocPalette.Empty.Resolve(new Color { Rgb = "FF123456" }));
    }

    [Fact]
    public void No_color_at_all_means_the_default_one()
    {
        Assert.Equal(DocPalette.None, DocPalette.Empty.Resolve(null));
        Assert.Equal(DocPalette.None, DocPalette.Empty.Resolve(new Color { Auto = true }));
    }

    // ── Палитра по номерам ────────────────────────────────────────────────────

    [Fact]
    public void Indexed_color_comes_from_the_standard_palette()
    {
        Assert.Equal(0xFF000000u, DocPalette.Empty.Resolve(new Color { Indexed = 8 }));
        Assert.Equal(0xFFFF0000u, DocPalette.Empty.Resolve(new Color { Indexed = 10 }));
        Assert.Equal(0xFFC0C0C0u, DocPalette.Empty.Resolve(new Color { Indexed = 22 }));
    }

    /// <summary>
    /// Номера 64 и 65 — «цвет системы»: текст и фон окна. Своего цвета у них нет, и подставлять
    /// черный с белым нельзя: кисти у нас свои, и в темной теме вышел бы белый по белому.
    /// </summary>
    [Theory]
    [InlineData(64u)]
    [InlineData(65u)]
    [InlineData(200u)]
    public void System_and_unknown_numbers_have_no_color_of_their_own(uint index)
    {
        Assert.Equal(DocPalette.None, DocPalette.Empty.Resolve(new Color { Indexed = index }));
    }

    // ── Осветление ────────────────────────────────────────────────────────────

    /// <summary>Оттенок больше нуля ведет цвет к белому, меньше нуля — к черному.</summary>
    [Fact]
    public void Tint_lightens_and_darkens()
    {
        const uint grey = 0xFF808080;

        var lighter = DocPalette.Tinted(grey, 0.5);
        var darker  = DocPalette.Tinted(grey, -0.5);

        Assert.True((lighter & 0xFF) > (grey & 0xFF), "осветление не осветлило");
        Assert.True((darker  & 0xFF) < (grey & 0xFF), "затемнение не затемнило");
    }

    [Fact]
    public void Tint_keeps_the_color_opaque()
    {
        Assert.Equal(0xFFu, DocPalette.Tinted(0xFF3366CC, 0.4) >> 24);
    }

    /// <summary>Крайние оттенки упираются в белый и черный, а не уходят за них.</summary>
    [Fact]
    public void Extreme_tints_stop_at_white_and_black()
    {
        Assert.Equal(0xFFFFFFFFu, DocPalette.Tinted(0xFF3366CC,  1));
        Assert.Equal(0xFF000000u, DocPalette.Tinted(0xFF3366CC, -1));
    }

    /// <summary>
    /// Оттенок считается по ЯРКОСТИ, а не по каждой составляющей отдельно: иначе цвет уходит в
    /// серый, и бледно-синяя шапка становится бледно-никакой.
    /// </summary>
    [Fact]
    public void Tint_keeps_the_hue()
    {
        var blue    = 0xFF3366CCu;
        var lighter = DocPalette.Tinted(blue, 0.6);

        var (r, g, b) = (lighter >> 16 & 0xFF, lighter >> 8 & 0xFF, lighter & 0xFF);
        Assert.True(b > g && g > r, $"оттенок потерян: {lighter:X8}");
    }

    [Fact]
    public void Tint_of_zero_changes_nothing()
    {
        Assert.Equal(0xFF3366CCu, DocPalette.Tinted(0xFF3366CC, 0));
    }
}
