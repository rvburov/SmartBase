using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Разбор имени файла на три номера (КД / ПД / ТД) и наименование. Это самая первая операция
/// над каждым PDF: от нее зависит, в какую ведомость попадет документ и что окажется в колонке
/// «Наименование».
/// </summary>
public sealed class DocumentCodesFilenameTests
{
    [Fact]
    public void Kd_file_is_split_into_number_and_name()
    {
        var (kd, pd, td, name) = DocumentCodes.ParseFilename("АБВГ.301234.001СБ Блок питания.pdf");

        Assert.Equal("АБВГ.301234.001СБ", kd);
        Assert.Equal("", pd);
        Assert.Equal("", td);
        Assert.Equal("Блок питания", name);
    }

    [Fact]
    public void Pd_file_is_split_into_number_and_name()
    {
        var (kd, pd, td, name) = DocumentCodes.ParseFilename("АБВГ.12345-01 34 01 Руководство оператора.pdf");

        Assert.Equal("", kd);
        Assert.Equal("АБВГ.12345-01 34 01", pd);
        Assert.Equal("", td);
        Assert.Equal("Руководство оператора", name);
    }

    [Fact]
    public void Td_file_is_split_into_number_and_name()
    {
        var (kd, pd, td, name) = DocumentCodes.ParseFilename("АБВГ.12345.67890 Маршрутная карта.pdf");

        Assert.Equal("", kd);
        Assert.Equal("", pd);
        Assert.Equal("АБВГ.12345.67890", td);
        Assert.Equal("Маршрутная карта", name);
    }

    [Fact]
    public void File_without_a_number_keeps_its_whole_name()
    {
        var (kd, pd, td, name) = DocumentCodes.ParseFilename("Просто документ.pdf");

        Assert.Equal("", kd);
        Assert.Equal("", pd);
        Assert.Equal("", td);
        Assert.Equal("Просто документ", name);
    }

    // Извлеченный номер не должен остаться еще и в наименовании — иначе колонка дублирует соседнюю.
    [Fact]
    public void Numbers_are_cut_out_of_the_name()
    {
        var (_, _, _, name) = DocumentCodes.ParseFilename("АБВГ.301234.001СБ Блок питания.pdf");

        Assert.DoesNotContain("АБВГ", name);
    }

    [Fact]
    public void Sheet_suffixes_are_cut_out_of_the_name()
    {
        var (kd, _, _, name) = DocumentCodes.ParseFilename("АБВГ.301234.001СБ-ЛУ Блок питания.pdf");

        Assert.Equal("АБВГ.301234.001СБ-ЛУ", kd);
        Assert.Equal("Блок питания", name);
    }

    [Fact]
    public void Brackets_and_double_spaces_are_cleaned()
    {
        var (_, _, _, name) = DocumentCodes.ParseFilename("АБВГ.301234.001СБ (копия)  тест.pdf");

        Assert.Equal("копия тест", name);
    }

    // Расширение отсекается до разбора — иначе «.pdf» попал бы в наименование.
    [Fact]
    public void Extension_is_stripped_before_parsing()
    {
        var (_, _, _, name) = DocumentCodes.ParseFilename("Блок питания.pdf");

        Assert.Equal("Блок питания", name);
    }
}
