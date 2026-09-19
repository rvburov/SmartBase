using SmartBase.Core.Documents;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Опознание формата документа (<see cref="DocFormats"/>) — подпись и цвет ленты на плитке.
///
/// Правило сведено в одно место именно затем, чтобы подпись и цвет не разъехались: если бы
/// карточка выбирала слово сама, а кисть — по расширению, добавление «.docm» однажды дало бы
/// синюю ленту с надписью «DOCM».
/// </summary>
public sealed class DocFormatsTests
{
    [Theory]
    [InlineData("pdf",  DocFormatFamily.Pdf)]
    [InlineData("PDF",  DocFormatFamily.Pdf)]
    [InlineData(".pdf", DocFormatFamily.Pdf)]
    [InlineData("doc",  DocFormatFamily.Word)]
    [InlineData("docx", DocFormatFamily.Word)]
    [InlineData("xlsx", DocFormatFamily.Excel)]
    [InlineData("xlsm", DocFormatFamily.Excel)]
    [InlineData("txt",  DocFormatFamily.Text)]
    [InlineData("md",   DocFormatFamily.Text)]
    [InlineData("csv",  DocFormatFamily.Text)]
    [InlineData("dwg",  DocFormatFamily.Other)]
    [InlineData("",     DocFormatFamily.Other)]
    public void Extensions_map_to_families(string ext, DocFormatFamily expected) =>
        Assert.Equal(expected, DocFormats.FamilyOf(ext));

    [Theory]
    [InlineData("pdf",  "PDF")]
    [InlineData("docx", "WORD")]
    [InlineData("xlsx", "EXCEL")]
    [InlineData("md",   "ТЕКСТ")]
    public void Known_families_are_labelled_by_family_not_by_extension(string ext, string label) =>
        Assert.Equal(label, DocFormats.LabelOf(ext));

    // «DWG» полезнее безликого «ФАЙЛ»: держать список всех расширений мира незачем, а сказать,
    // что это за файл, можно и так.
    [Fact]
    public void An_unknown_extension_is_its_own_label() =>
        Assert.Equal("DWG", DocFormats.LabelOf("dwg"));

    [Fact]
    public void A_file_without_an_extension_says_so() =>
        Assert.Equal(DocFormats.NoExtensionLabel, DocFormats.LabelOf(""));

    // Цвет и подпись идут от ОДНОГО семейства — это и есть защита от разъезда.
    [Theory]
    [InlineData("pdf",  "BrushFormatPdf",   "BrushFormatPdfLight")]
    [InlineData("docx", "BrushFormatWord",  "BrushFormatWordLight")]
    [InlineData("xlsx", "BrushFormatExcel", "BrushFormatExcelLight")]
    [InlineData("txt",  "BrushFormatText",  "BrushFormatTextLight")]
    [InlineData("zip",  "BrushFormatOther", "BrushFormatOtherLight")]
    public void Brush_keys_follow_the_family(string ext, string stroke, string fill)
    {
        var family = DocFormats.FamilyOf(ext);
        Assert.Equal(stroke, DocFormats.StrokeBrushKey(family));
        Assert.Equal(fill,   DocFormats.FillBrushKey(family));
    }

    // Каждому семейству — своя пара кистей: две одинаковых означали бы, что два формата на
    // экране неотличимы.
    [Fact]
    public void Every_family_has_its_own_pair_of_brushes()
    {
        var families = Enum.GetValues<DocFormatFamily>();
        var strokes  = families.Select(DocFormats.StrokeBrushKey).ToList();
        var fills    = families.Select(DocFormats.FillBrushKey).ToList();

        Assert.Equal(strokes.Count, strokes.Distinct().Count());
        Assert.Equal(fills.Count,   fills.Distinct().Count());
    }
}
