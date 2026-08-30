using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>Номера экземпляров и имена файлов — чистая логика, ни базы, ни PDF.</summary>
public sealed class CopyNumberTests
{
    // --- Документ из пути --------------------------------------------------------------------

    [Theory]
    [InlineData("/дела/АБВГ.301121.001 СБ.pdf", "АБВГ.301121.001 СБ")]
    [InlineData("отчет.PDF",                    "отчет")]
    [InlineData("без расширения",               "без расширения")]
    [InlineData("две.точки.pdf",                "две.точки")]
    public void Document_is_the_file_name_without_extension(string path, string expected) =>
        Assert.Equal(expected, CopyNumber.DocumentOf(path));

    // --- Разбор номера -----------------------------------------------------------------------

    [Theory]
    [InlineData("3",           3)]
    [InlineData("  3  ",       3)]
    [InlineData("003",         3)]
    [InlineData("3,0",         3)]      // число из БД с разделителем
    [InlineData("12 (копия)", 12)]      // приписка не мешает: номер занят
    [InlineData("999999", 999999)]
    public void Leading_digits_make_the_number(string value, int expected) =>
        Assert.Equal(expected, CopyNumber.NumberIn(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("экз")]
    [InlineData("0")]             // нумерация с единицы
    [InlineData("1000000")]       // за пределом
    [InlineData("20260829")]      // дата в чужой колонке — не номер
    [InlineData("№3")]            // цифры не ведущие
    public void Everything_else_occupies_nothing(string? value) =>
        Assert.Null(CopyNumber.NumberIn(value));

    // --- Первый свободный ---------------------------------------------------------------------

    [Fact]
    public void Empty_journal_starts_from_one() =>
        Assert.Equal(1, CopyNumber.FirstFree([]));

    [Fact]
    public void Holes_are_filled_before_the_tail() =>
        Assert.Equal(2, CopyNumber.FirstFree([1, 3, 4]));

    [Fact]
    public void Continuous_run_gets_the_next_one() =>
        Assert.Equal(4, CopyNumber.FirstFree([1, 2, 3]));

    [Fact]
    public void Exhausted_range_gives_nothing() =>
        Assert.Null(CopyNumber.FirstFree([1, 2, 3], max: 3));

    // --- Имя результата -------------------------------------------------------------------------

    [Fact]
    public void Output_name_carries_the_copy_number() =>
        Assert.Equal("АБВГ.301121.001 СБ экз. 3.pdf", CopyNumber.FileNameFor("АБВГ.301121.001 СБ", 3));

    [Fact]
    public void Free_name_is_taken_as_is()
    {
        using var dir = new TempDir("copy");

        var path = CopyNumber.UniquePath(dir.Path, "чертеж экз. 3.pdf");

        Assert.Equal(Path.Combine(dir.Path, "чертеж экз. 3.pdf"), path);
    }

    [Fact]
    public void Taken_name_gets_a_counter()
    {
        using var dir = new TempDir("copy");
        dir.Touch("чертеж экз. 3.pdf");

        var path = CopyNumber.UniquePath(dir.Path, "чертеж экз. 3.pdf");

        Assert.Equal(Path.Combine(dir.Path, "чертеж экз. 3 (2).pdf"), path);
    }

    [Fact]
    public void Counter_keeps_climbing()
    {
        using var dir = new TempDir("copy");
        dir.Touch("чертеж экз. 3.pdf");
        dir.Touch("чертеж экз. 3 (2).pdf");
        dir.Touch("чертеж экз. 3 (3).pdf");

        var path = CopyNumber.UniquePath(dir.Path, "чертеж экз. 3.pdf");

        Assert.Equal(Path.Combine(dir.Path, "чертеж экз. 3 (4).pdf"), path);
    }

    [Fact]
    public void Source_file_is_never_overwritten()
    {
        // Папка вывода совпала с исходной, а исходник уже назван как результат. Писать поверх
        // источника нельзя ни при каких условиях — он единственный.
        using var dir = new TempDir("copy");
        var source = dir.Touch("чертеж экз. 3.pdf");

        var path = CopyNumber.UniquePath(dir.Path, "чертеж экз. 3.pdf", forbidden: source);

        Assert.Equal(Path.Combine(dir.Path, "чертеж экз. 3 (2).pdf"), path);
    }

    // --- Текст знака -----------------------------------------------------------------------------

    [Fact]
    public void Stamp_text_is_three_lines() =>
        Assert.Equal(["Выдан: Иванов Иван Иванович", "Экз. № 3 · 29.08.2026",
                      "Передача третьим лицам запрещена"],
                     CopyNumber.StampText("  Иванов Иван Иванович  ", 3, new DateTime(2026, 8, 29),
                                          "Передача третьим лицам запрещена"));

    [Fact]
    public void Empty_warning_is_dropped_rather_than_left_dangling() =>
        Assert.Equal(["Выдан: Иванов Иван Иванович", "Экз. № 3 · 29.08.2026"],
                     CopyNumber.StampText("Иванов Иван Иванович", 3, new DateTime(2026, 8, 29), "   "));

    [Fact]
    public void Empty_holder_leaves_no_dangling_line() =>
        // Пустая строка забрала бы себе место в блоке и сдвинула знак с середины листа.
        Assert.Equal(["Экз. № 3 · 29.08.2026", "Передача третьим лицам запрещена"],
                     CopyNumber.StampText("   ", 3, new DateTime(2026, 8, 29),
                                          "Передача третьим лицам запрещена"));

    [Fact]
    public void Date_is_written_without_time() =>
        // Время в дате знака не нужно и не помещается: на бумаге важен день выдачи.
        Assert.Equal(["Выдан: Иванов", "Экз. № 1 · 29.08.2026"],
                     CopyNumber.StampText("Иванов", 1, new DateTime(2026, 8, 29, 14, 35, 0), ""));
}
