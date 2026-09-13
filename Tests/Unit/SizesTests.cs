using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Размер и перечень словами (<see cref="Sizes"/>).
///
/// Оба выражения жили копиями по всему решению: размер — в пяти местах, перечень — в двух.
/// Пороги и разрядность совпадали не потому, что их согласовали, а потому что каждую следующую
/// копию писали с предыдущей. Тесты держат единственный оставшийся вариант и, главное,
/// закрепляют ЕДИНСТВЕННОЕ настоящее различие между прежними копиями — байты у мелочи.
/// </summary>
public sealed class SizesTests
{
    /// <summary>Единица растет вместе с числом, разрядность — вместе с единицей.</summary>
    [Theory]
    [InlineData(700,                    "1 КБ")]     // мелочь в интерфейсе округляется до КБ
    [InlineData(1024,                   "1 КБ")]
    [InlineData(1024L * 700,            "700 КБ")]
    [InlineData(1024L * 1024,           "1,0 МБ")]
    [InlineData(1024L * 1024 * 3 + 500_000, "3,5 МБ")]
    [InlineData(1024L * 1024 * 1024,    "1,00 ГБ")]
    public void A_size_grows_its_unit_together_with_its_precision(long bytes, string expected) =>
        Assert.Equal(expected, Sizes.Text(bytes).Replace(' ', ' '));

    /// <summary>
    /// В интерфейсе мелочь — это «0 КБ», в «Диагностике» — «96 Б». Это и есть то единственное,
    /// чем прежние пять копий отличались друг от друга, и теперь оно названо параметром.
    /// </summary>
    [Fact]
    public void Tiny_sizes_show_bytes_only_where_bytes_matter()
    {
        Assert.Equal("0 КБ", Sizes.Text(96).Replace(' ', ' '));
        Assert.Equal("96 Б", Sizes.Text(96, tiny: true));

        // Выше килобайта обе стороны отвечают одинаково — иначе «тот же файл» весил бы
        // по-разному на двух экранах.
        Assert.Equal(Sizes.Text(1024L * 1024), Sizes.Text(1024L * 1024, tiny: true));
    }

    /// <summary>Пустой перечень — «не заданы», а не пустая строка: пустое место не читается.</summary>
    [Fact]
    public void An_empty_list_says_so()
    {
        Assert.Equal("не заданы", Sizes.List([]));
        Assert.Equal("api.zavod.local", Sizes.List(["api.zavod.local"]));
        Assert.Equal("один, два", Sizes.List(["один", "два"]));
    }
}
