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

    // ── Лента и чтение ────────────────────────────────────────────────────────

    /// <summary>
    /// «ТЕКСТ» на плитке обещает, что внутри ищется, — и обещание держится: файл с такой лентой
    /// читается. Раньше у INI, JSON, XML, YML и YAML лента стояла, а поиск в них не заглядывал.
    /// </summary>
    [Theory]
    [InlineData("ini")]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("yml")]
    [InlineData("yaml")]
    [InlineData("log")]
    public void A_text_ribbon_promises_text_that_is_read(string ext)
    {
        Assert.Equal("ТЕКСТ", DocFormats.LabelOf(ext));
        Assert.True(DocTextExtractor.IsSupported("файл." + ext));
    }

    /// <summary>У всякого читаемого формата своя лента, а не безликое «прочее».</summary>
    [Fact]
    public void Every_readable_format_has_a_family_of_its_own()
    {
        foreach (var ext in DocTextExtractor.SupportedExtensions)
            Assert.NotEqual(DocFormatFamily.Other, DocFormats.FamilyOf(ext));
    }

    /// <summary>
    /// Строка форматов, которую человек видит в трех местах, называет КАЖДЫЙ читаемый формат.
    /// Написанная руками, она отстала от списка на XLSM и LOG, и выходило, что их текст не ищется.
    /// </summary>
    [Fact]
    public void The_shown_list_of_formats_names_every_readable_one()
    {
        var shown = DocTextExtractor.SupportedText.Split(", ");

        Assert.Equal(DocTextExtractor.SupportedExtensions.Length, shown.Length);
        foreach (var ext in DocTextExtractor.SupportedExtensions)
            Assert.Contains(ext.ToUpperInvariant(), shown);
    }
}
