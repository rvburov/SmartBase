using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Обозначение по ГОСТ 2.201-80 и подбор номера — чистая логика, без базы и без UI.
///
/// Здесь же прибиты два решения, из-за которых номер мог бы уйти не туда: нумерация идет
/// В ПРЕДЕЛАХ СЕРИИ (пары «организация + код ЕСКД»), а исполнение вида <c>…001-01</c> считается
/// занятым номером 001 — это тот же документ, и выдавать 001 второй раз нельзя.
/// </summary>
public sealed class DesignationTests
{
    [Fact]
    public void Number_is_padded_to_three_digits()
    {
        Assert.Equal("АБВГ.301121.001", Designation.Format("АБВГ", "301121", 1));
        Assert.Equal("АБВГ.301121.042", Designation.Format("АБВГ", "301121", 42));
        Assert.Equal("АБВГ.301121.999", Designation.Format("АБВГ", "301121", 999));
    }

    [Fact]
    public void Prefix_ends_with_a_dot() =>
        Assert.Equal("АБВГ.301121.", Designation.Prefix("АБВГ", "301121"));

    // ── Разбор чужих обозначений ──────────────────────────────────────────────

    [Fact]
    public void Number_is_read_from_its_own_series() =>
        Assert.Equal(7, Designation.NumberIn("АБВГ.301121.007", "АБВГ", "301121"));

    [Fact]
    public void Other_organisation_does_not_occupy_a_number() =>
        Assert.Null(Designation.NumberIn("ГМБВ.301121.007", "АБВГ", "301121"));

    [Fact]
    public void Other_eskd_code_does_not_occupy_a_number() =>
        Assert.Null(Designation.NumberIn("АБВГ.723611.007", "АБВГ", "301121"));

    [Fact]
    public void Case_of_the_organisation_code_is_ignored() =>
        Assert.Equal(7, Designation.NumberIn("абвг.301121.007", "АБВГ", "301121"));

    [Fact]
    public void Surrounding_spaces_are_ignored() =>
        Assert.Equal(7, Designation.NumberIn("  АБВГ.301121.007  ", "АБВГ", "301121"));

    /// <summary>Исполнение — тот же документ: базовый номер занят и повторно не выдается.</summary>
    [Theory]
    [InlineData("АБВГ.301121.001-01")]
    [InlineData("АБВГ.301121.001 (копия)")]
    public void Execution_and_notes_still_occupy_the_base_number(string designation) =>
        Assert.Equal(1, Designation.NumberIn(designation, "АБВГ", "301121"));

    /// <summary>Больше трех цифр — это уже не номер по ГОСТ. Такую строку пропускаем целиком,
    /// иначе «0011» занял бы номер 11 по ошибке разбора.</summary>
    [Fact]
    public void More_than_three_digits_is_not_a_number() =>
        Assert.Null(Designation.NumberIn("АБВГ.301121.0011", "АБВГ", "301121"));

    [Theory]
    [InlineData("АБВГ.301121.000")]     // номера 000 не бывает
    [InlineData("АБВГ.301121.")]        // номера нет вовсе
    [InlineData("АБВГ.301121.abc")]     // не цифры
    [InlineData("")]
    [InlineData(null)]
    public void Junk_does_not_occupy_a_number(string? designation) =>
        Assert.Null(Designation.NumberIn(designation, "АБВГ", "301121"));

    // ── Подбор первого свободного ─────────────────────────────────────────────

    [Fact]
    public void Empty_collection_starts_from_one() =>
        Assert.Equal(1, Designation.FirstFree([], "АБВГ", "301121"));

    /// <summary>Пример из задания: заняты 001 и 003 — новый документ получает 002.</summary>
    [Fact]
    public void Gap_is_filled_before_the_tail()
    {
        string[] taken = ["АБВГ.301121.001", "АБВГ.301121.003"];

        Assert.Equal(2, Designation.FirstFree(taken, "АБВГ", "301121"));
    }

    [Fact]
    public void Numbers_run_on_after_a_solid_range()
    {
        string[] taken = ["АБВГ.301121.001", "АБВГ.301121.002", "АБВГ.301121.003"];

        Assert.Equal(4, Designation.FirstFree(taken, "АБВГ", "301121"));
    }

    /// <summary>Своя серия у каждого кода ЕСКД: занятое у соседа счет не сдвигает.</summary>
    [Fact]
    public void Each_series_counts_on_its_own()
    {
        string[] taken = ["АБВГ.301121.001", "АБВГ.301121.002", "ГМБВ.723611.001"];

        Assert.Equal(1, Designation.FirstFree(taken, "АБВГ", "723611"));
        Assert.Equal(3, Designation.FirstFree(taken, "АБВГ", "301121"));
    }

    [Fact]
    public void Exhausted_series_has_no_free_number()
    {
        var taken = Enumerable.Range(Designation.MinNumber, Designation.MaxNumber)
                              .Select(n => Designation.Format("АБВГ", "301121", n))
                              .ToList();

        Assert.Null(Designation.FirstFree(taken, "АБВГ", "301121"));
    }

    /// <summary>Пустые значения столбца (записи без обозначения) разбор просто пропускает.</summary>
    [Fact]
    public void Empty_values_are_skipped()
    {
        string?[] taken = ["", null, "АБВГ.301121.001"];

        Assert.Equal(2, Designation.FirstFree(taken, "АБВГ", "301121"));
    }
}
