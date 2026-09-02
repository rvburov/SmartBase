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

    /// <summary>
    /// Цифр в номере РОВНО ТРИ. Четыре («0011») — не номер по ГОСТ: иначе он занял бы номер 11
    /// по ошибке разбора. Две («01») — не номер тем более: это с равным правом и 001, и обрубок
    /// 011 или 014, а гадать за человека нельзя. Обе строки пропускаются целиком: незанятый
    /// номер виден проверкой журнала, а угаданный неверно уходит второй раз молча.
    /// </summary>
    [Theory]
    [InlineData("АБВГ.301121.0011")]   // четыре цифры
    [InlineData("АБВГ.301121.01")]     // две — 001? 011? 014?
    [InlineData("АБВГ.301121.1")]      // одна
    public void Number_must_be_exactly_three_digits(string designation) =>
        Assert.Null(Designation.NumberIn(designation, "АБВГ", "301121"));

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

    // ── Исполнения ────────────────────────────────────────────────────────────

    [Fact]
    public void Execution_is_formatted_with_two_digits() =>
        Assert.Equal("АБВГ.301121.002-04", Designation.FormatExecution("АБВГ", "301121", 2, 4));

    [Theory]
    [InlineData("АБВГ.301121.002-04", 2, 4)]      // исполнение
    [InlineData("АБВГ.301121.002-04СБ", 2, 4)]    // исполнение с кодом документа
    [InlineData("АБВГ.301121.002", 2, null)]      // сам документ
    [InlineData("АБВГ.301121.002СБ", 2, null)]    // документ изделия — исполнения нет
    public void Series_parse_reads_number_and_execution(string designation, int number, int? execution)
    {
        var parsed = Designation.ParseInSeries(designation, "АБВГ", "301121");

        Assert.NotNull(parsed);
        Assert.Equal(number, parsed!.Value.Number);
        Assert.Equal(execution, parsed.Value.Execution);
    }

    /// <summary>Исполнение читается только в каноне «-NN». Иначе строка остается занятым НОМЕРОМ,
    /// но исполнения не занимает: угадывать, что имелось в виду, нельзя — по угаданному неверно
    /// исполнение выдали бы второй раз.</summary>
    [Theory]
    [InlineData("АБВГ.301121.002-4")]      // одна цифра
    [InlineData("АБВГ.301121.002-004")]    // три цифры
    [InlineData("АБВГ.301121.002-00")]     // исполнения 00 не бывает
    public void Sloppy_execution_is_not_read(string designation)
    {
        var parsed = Designation.ParseInSeries(designation, "АБВГ", "301121");

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Value.Number);
        Assert.Null(parsed.Value.Execution);
    }

    [Fact]
    public void Executions_of_a_number_are_listed_in_order()
    {
        string[] journal =
        [
            "АБВГ.301121.002", "АБВГ.301121.002-03", "АБВГ.301121.002-01",
            "АБВГ.301121.003-05",                        // чужой номер
            "ГМБВ.301121.002-09",                        // чужая организация
        ];

        Assert.Equal([1, 3], Designation.ExecutionsOf(journal, "АБВГ", "301121", 2));
    }

    /// <summary>Дырки в исполнениях заполняются так же, как в номерах.</summary>
    [Fact]
    public void First_free_execution_fills_the_gap()
    {
        string[] journal = ["АБВГ.301121.002-01", "АБВГ.301121.002-03"];

        Assert.Equal(2, Designation.FirstFreeExecution(journal, "АБВГ", "301121", 2));
    }

    [Fact]
    public void First_execution_of_a_document_is_01() =>
        Assert.Equal(1, Designation.FirstFreeExecution(["АБВГ.301121.002"], "АБВГ", "301121", 2));

    [Fact]
    public void Exhausted_executions_have_no_free_number()
    {
        var journal = Enumerable.Range(Designation.MinExecution, Designation.MaxExecution)
                                .Select(e => Designation.FormatExecution("АБВГ", "301121", 2, e))
                                .ToList();

        Assert.Null(Designation.FirstFreeExecution(journal, "АБВГ", "301121", 2));
    }

    /// <summary>Исполнение занимает и НОМЕР целиком: «002-01» в журнале означает, что документ
    /// 002 уже выдан, и новый документ его не получит.</summary>
    [Fact]
    public void Execution_takes_the_whole_number_too() =>
        Assert.Equal(3, Designation.FirstFree(["АБВГ.301121.001", "АБВГ.301121.002-01"], "АБВГ", "301121"));

    // ── Действующий номер ─────────────────────────────────────────────────────

    /// <summary>Последний выданный — НАИБОЛЬШИЙ из занятых, а не первый свободный: к нему
    /// приписывают исполнение, и дырка в середине серии тут ни при чем.</summary>
    [Fact]
    public void Last_issued_is_the_greatest_taken_number() =>
        Assert.Equal(14, Designation.LastIssued(
            ["АБВГ.301121.001", "АБВГ.301121.014", "АБВГ.301121.002"], "АБВГ", "301121"));

    [Fact]
    public void Execution_counts_as_the_last_issued() =>
        Assert.Equal(14, Designation.LastIssued(["АБВГ.301121.014-01"], "АБВГ", "301121"));

    [Fact]
    public void Empty_series_has_no_last_issued() =>
        Assert.Null(Designation.LastIssued(["АБВГ.723611.005"], "АБВГ", "301121"));
}
