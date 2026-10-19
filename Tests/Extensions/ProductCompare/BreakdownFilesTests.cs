using SmartBase.Extensions.ProductCompare;

namespace SmartBase.Tests.Extensions.ProductCompare;

/// <summary>
/// Отбор файлов режима «расшифровка»: разбор даты в имени и отказы, при которых порядок дат
/// неоднозначен. Каждый отказ обязан назвать мешающие файлы — пользователь наводит порядок в
/// папке по тексту журнала.
/// </summary>
public sealed class BreakdownFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_pcf_{Guid.NewGuid():N}");

    public BreakdownFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_root, name), "");

    private ProductCompareException Refusal() =>
        Assert.Throws<ProductCompareException>(() => BreakdownFiles.Collect(_root, null));

    // ── Имя файла ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("КТС ПД РУАМ АЦИЕ.462715.007-27_2026.06.16_Парфенова", "КТС ПД РУАМ АЦИЕ.462715.007-27", 2026, 6, 16)]
    [InlineData("Состав изделия АААА.000001.003_2026.06.30",          "Состав изделия АААА.000001.003", 2026, 6, 30)]
    [InlineData("Проект_16.06.2026_Иванов",                            "Проект",                         2026, 6, 16)]
    [InlineData("Проект_с_подчеркиванием_2026.01.02_ФИО",              "Проект_с_подчеркиванием",        2026, 1, 2)]
    public void Name_gives_project_and_date(string title, string project, int y, int m, int d)
    {
        var parsed = BreakdownFiles.ParseName(title);

        Assert.NotNull(parsed);
        Assert.Equal(project, parsed.Value.Project);
        Assert.Equal(new DateTime(y, m, d), parsed.Value.Date);
    }

    [Theory]
    [InlineData("Проект без даты")]
    [InlineData("Проект 2026.06.16")]          // дата не отделена подчеркиванием
    [InlineData("Проект_2026.13.40_ФИО")]      // такой даты нет
    [InlineData("_2026.06.16_ФИО")]            // нет проекта
    [InlineData("Проект_2026.06.161")]         // дата не кончается на границе части
    public void Name_without_a_proper_date_is_rejected(string title) =>
        Assert.Null(BreakdownFiles.ParseName(title));

    // ── Отбор папки ───────────────────────────────────────────────────────────

    [Fact]
    public void Files_are_sorted_by_date_and_noise_is_ignored()
    {
        Touch("Изд_2026.03.01_Б.xlsx");
        Touch("Изд_2026.01.01_А.xlsm");
        Touch("~$Изд_2026.01.01_А.xlsm");                  // служебный файл открытой книги
        Touch("заметки.txt");
        Touch("Расшифровка сравнения 19.10.2026_10-00-00.xlsm");   // результат прошлого прогона

        var files = BreakdownFiles.Collect(_root, null);

        Assert.Equal(["Изд_2026.01.01_А", "Изд_2026.03.01_Б"], files.Select(f => f.Title));
    }

    [Fact]
    public void File_without_a_date_stops_the_run_and_is_named()
    {
        Touch("Изд_2026.01.01_А.xlsx");
        Touch("Изд_2026.02.01_А.xlsx");
        Touch("Изд без даты.xlsx");

        var ex = Refusal();

        Assert.Contains("нет даты", ex.Message);
        Assert.Contains("Изд без даты.xlsx", ex.Message);
    }

    [Fact]
    public void Same_date_twice_stops_the_run_and_names_both_files()
    {
        Touch("Изд_2026.01.01_Иванов.xlsx");
        Touch("Изд_2026.01.01_Петров.xlsx");
        Touch("Изд_2026.02.01_Петров.xlsx");

        var ex = Refusal();

        Assert.Contains("одна и та же дата", ex.Message);
        Assert.Contains("Изд_2026.01.01_Иванов.xlsx", ex.Message);
        Assert.Contains("Изд_2026.01.01_Петров.xlsx", ex.Message);
        Assert.DoesNotContain("2026.02.01", ex.Message);
    }

    [Fact]
    public void Different_products_stop_the_run()
    {
        Touch("Изделие А_2026.01.01_Иванов.xlsx");
        Touch("Изделие Б_2026.02.01_Иванов.xlsx");

        var ex = Refusal();

        Assert.Contains("разных изделий", ex.Message);
        Assert.Contains("«Изделие А»", ex.Message);
        Assert.Contains("«Изделие Б»", ex.Message);
    }

    [Fact]
    public void Surname_after_the_date_does_not_matter()
    {
        Touch("Изд_2026.01.01_Иванов.xlsx");
        Touch("Изд_2026.02.01_Петров.xlsx");

        Assert.Equal(2, BreakdownFiles.Collect(_root, null).Count);
    }

    [Fact]
    public void One_file_is_not_enough()
    {
        Touch("Изд_2026.01.01_Иванов.xlsx");

        Assert.Contains("хотя бы два файла", Refusal().Message);
    }

    // ── Отказы движка ─────────────────────────────────────────────────────────

    [Fact]
    public void Missing_template_folder_and_output_are_friendly_errors()
    {
        var template = Path.Combine(_root, "шаблон.xlsm");
        BreakdownStand.WriteTemplate(template);
        var nowhere = Path.Combine(_root, "нет");

        Assert.Equal("Файл-шаблон не найден.", Assert.Throws<ProductCompareException>(
            () => BreakdownCompareEngine.Run(Path.Combine(_root, "нет.xlsm"), _root, _root, null, null, default)).Message);
        Assert.Equal("Папка с расшифровками не найдена.", Assert.Throws<ProductCompareException>(
            () => BreakdownCompareEngine.Run(template, nowhere, _root, null, null, default)).Message);
        Assert.Equal("Папка для сохранения не найдена.", Assert.Throws<ProductCompareException>(
            () => BreakdownCompareEngine.Run(template, _root, nowhere, null, null, default)).Message);
    }

    [Fact]
    public void File_without_the_breakdown_sheet_is_named()
    {
        var template = Path.Combine(_root, "Шаблон", "шаблон.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(template)!);
        BreakdownStand.WriteTemplate(template);
        BreakdownStand.WriteBreakdown(Path.Combine(_root, "Изд_2026.01.01_А.xlsx"), new BRow("Изделие", "К", "А.000"));
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            wb.AddWorksheet("Другое").Cell(1, 1).Value = 1;
            wb.SaveAs(Path.Combine(_root, "Изд_2026.02.01_А.xlsx"));
        }

        var ex = Assert.Throws<ProductCompareException>(
            () => BreakdownCompareEngine.Run(template, _root, _root, null, null, default));

        Assert.Contains("Изд_2026.02.01_А.xlsx", ex.Message);
        Assert.Contains("«Расшифровка»", ex.Message);
    }

    [Fact]
    public void Not_an_excel_book_is_a_friendly_error()
    {
        var template = Path.Combine(_root, "Шаблон", "шаблон.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(template)!);
        BreakdownStand.WriteTemplate(template);
        BreakdownStand.WriteBreakdown(Path.Combine(_root, "Изд_2026.01.01_А.xlsx"), new BRow("Изделие", "К", "А.000"));
        File.WriteAllText(Path.Combine(_root, "Изд_2026.02.01_А.xlsx"), "это не книга");

        var ex = Assert.Throws<ProductCompareException>(
            () => BreakdownCompareEngine.Run(template, _root, _root, null, null, default));

        Assert.Contains("Изд_2026.02.01_А.xlsx", ex.Message);
    }
}
