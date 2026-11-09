using SmartBase.Extensions.PdfRename.Ocr;
using SmartBase.Extensions.PdfRename.Recognition;
using SmartBase.Extensions.PdfRename.Rename;

namespace SmartBase.Tests.Extensions.PdfRename.Recognition;

/// <summary>
/// Группы ТЗ 9–11 без движков: проверка обозначения (не одного формата), нормализация,
/// буквы-двойники только по правилу, разбор отдельных и общей областей, сверка двух
/// движков, недопустимые символы и длинные имена.
/// </summary>
public sealed class TextRulesTests
{
    private static OcrReading Reading(params string[] lines) =>
        new("Тест", lines.Select((t, i) => new OcrLine(t, 0.95, 0, i * 40, 500, i * 40 + 30)).ToList());

    private static string? Designation(string text) => Designations.FindAll(Designations.Prepare(text)).SingleOrDefault()?.Value;

    // ── Обозначения ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("АБВГ.461411.123", "АБВГ.461411.123")]
    [InlineData("АБВГ.461411.123-01", "АБВГ.461411.123-01")]
    [InlineData("АБВГ.461411.1234", "АБВГ.461411.1234")]
    [InlineData("АБВГ.461411.123-01.02", "АБВГ.461411.123-01.02")]
    [InlineData("АБВГ.461411.123СБ", "АБВГ.461411.123СБ")]
    [InlineData("АБВГ.461411.123Э3", "АБВГ.461411.123Э3")]
    [InlineData("АБВГ.461411.123ПЭ3", "АБВГ.461411.123ПЭ3")]
    [InlineData("АБВГ.461411.123-ЛУ", "АБВГ.461411.123-ЛУ")]
    [InlineData("АБВГ.12345-01 34 01", "АБВГ.12345-01 34 01")]          // ЕСПД
    [InlineData("АБВГ.12345-01", "АБВГ.12345-01")]
    [InlineData("АБВГ.12345.12345", "АБВГ.12345.12345")]                // ТД
    [InlineData("ВЛ2.700.004-01", "ВЛ2.700.004-01")]                    // отраслевое
    [InlineData("8А1.234.567", "8А1.234.567")]
    [InlineData("8.112.006", "8.112.006")]
    public void Designation_formats(string text, string expected) => Assert.Equal(expected, Designation(text));

    [Theory]
    [InlineData("АБВГ. 461411 .123", "АБВГ.461411.123")]      // пробелы у точек
    [InlineData("АБВГ.461411.123 – 01", "АБВГ.461411.123-01")] // тире и пробелы у дефиса
    [InlineData("абвг.461411.123", "АБВГ.461411.123")]         // регистр: обозначение прописное
    [InlineData("АБВГ.461411.123 СБ", "АБВГ.461411.123СБ")]    // код документа отделен пробелом
    [InlineData("ВКЕМ.967 747.185", "ВКЕМ.967747.185")]        // пробел внутри группы цифр
    [InlineData("АБВГ.2 70973.767", "АБВГ.270973.767")]
    public void Designation_normalization(string text, string expected) => Assert.Equal(expected, Designation(text));

    [Fact]
    public void Gost_org_code_latin_twins_become_cyrillic()
    {
        // По ГОСТ 2.201 код организации — кириллица: однозначные двойники переводятся.
        Assert.Equal("АБСГ.461411.123", Designation("AБCГ.461411.123"));
        Assert.Equal("ТКЕМ.461411.123", Designation("TKEM.461411.123"));
    }

    [Fact]
    public void Latin_b_stays_until_the_other_engine_confirms()
    {
        // «B» на скане — и «В», и «Б»: правило ее не меняет.
        Assert.Equal("АБBГ.461411.123", Designation("АБBГ.461411.123"));
        Assert.Equal(("АБВГ.461411.123", (string?)null), TextRules.MergeTwins("АБBГ.461411.123", "АБВГ.461411.123"));
        Assert.Null(TextRules.MergeTwins("АBBГ.461411.123", "АБBГ.461411.123").Value);   // B против Б — расхождение
        var both = TextRules.MergeTwins("АBРС.461411.123", "АBРС.461411.123");
        Assert.Null(both.Value);
        Assert.Contains("не подтверждено", both.Problem);
        Assert.Equal(("Board", (string?)null), TextRules.MergeTwins("Board", "Board"));   // латинское слово
    }

    [Fact]
    public void Gost_shaped_text_with_foreign_letters_is_not_a_designation()
    {
        // D и L не похожи на кириллицу: строка вида ГОСТ с ними — ошибка чтения, а не
        // «отраслевое» обозначение.
        Assert.Null(Designation("ADLG.461411.123"));
        Assert.Null(Designation("ABPG.461411.123"));
    }

    [Theory]
    [InlineData("АБВГ.46141.123")]        // ЕСКД без цифры — не ЕСКД; и не дата
    [InlineData("12.05.2020")]            // дата — не обозначение
    [InlineData("Корпус")]
    [InlineData("АБВГ.461411.123Корпус")] // слиплось с наименованием
    [InlineData("АБВГ.46I411.123")]       // латинская I вместо 1 — не заменяется
    [InlineData("АБВГ.4614l1.123")]
    public void Not_a_designation(string text)
    {
        var found = Designations.FindAll(Designations.Prepare(text));
        Assert.DoesNotContain(found, f => f.Value.StartsWith("АБВГ.461411", StringComparison.Ordinal) || f.Value == "12.05.2020");
        if (text is "12.05.2020" or "Корпус" or "АБВГ.461411.123Корпус") Assert.Empty(found);
    }

    [Fact]
    public void Letters_and_digits_are_never_swapped()
    {
        // «О» вместо нуля в цифровой группе — это не обозначение, а не «исправленный» 0.
        Assert.Null(Designation("АБВГ.461О11.123"));
        Assert.Null(Designation("АБВГ.461411.12З"));
    }

    // ── Текст ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ЦОC-2", "ЦОС-2")]           // в слове есть «Ц» — двойник C кириллический
    [InlineData("Plata", "Plata")]
    [InlineData("Рlаtа", "Plata")]          // в слове есть «l», «t» — двойники латинские
    [InlineData("ТКС", "ТКС")]               // только двойники — не трогаем
    [InlineData("TKC", "TKC")]
    [InlineData("Блок ПCУ-24", "Блок ПСУ-24")]
    [InlineData("Bал", "Bал")]               // «B» — «В» или «Б»: слово не меняется
    [InlineData("Вoard", "Board")]           // кириллическая «В» в латинском слове — только «B»
    public void Mixed_words_resolve_only_when_confirmed(string input, string expected) =>
        Assert.Equal(expected, TextRules.ResolveMixedWords(input));

    [Fact]
    public void Lines_join_with_spaces_and_keep_hyphenated_compounds()
    {
        Assert.Equal("Крышка верхняя", TextRules.JoinLines(["Крышка", "верхняя"]));
        Assert.Equal("Плата-адаптер", TextRules.JoinLines(["Плата-", "адаптер"]));
        Assert.Equal("Плата-адаптер", TextRules.JoinLines(["Плата — адаптер"]));
        Assert.Equal("Корпус сварной", TextRules.JoinLines(["|  Корпус   сварной _"]));
    }

    // ── Отдельные области ─────────────────────────────────────────────────────

    [Fact]
    public void Designation_area_must_hold_only_the_designation()
    {
        Assert.Equal("АБВГ.461411.123", FieldParser.ParseDesignation(Reading("АБВГ.461411.123"), 0.5).Value);
        Assert.Null(FieldParser.ParseDesignation(Reading("АБВГ.461411.123 -0l"), 0.5).Value);
        Assert.Null(FieldParser.ParseDesignation(Reading("АБВГ.461411.123", "АБВГ.461411.124"), 0.5).Value);
        Assert.Null(FieldParser.ParseDesignation(Reading(), 0.5).Value);
    }

    [Fact]
    public void Low_confidence_is_rejected()
    {
        var low = new OcrReading("Тест", [new OcrLine("АБВГ.461411.123", 0.2, 0, 0, 10, 10)]);
        Assert.Contains("уверенность", FieldParser.ParseDesignation(low, 0.5).Problem);
    }

    [Fact]
    public void Name_area_rules()
    {
        Assert.Equal("Крышка верхняя", FieldParser.ParseName(Reading("Крышка", "верхняя"), 0.5).Value);
        Assert.Null(FieldParser.ParseName(Reading(), 0.5).Value);
        Assert.Null(FieldParser.ParseName(Reading("12"), 0.5).Value);                          // нет слов
        Assert.Null(FieldParser.ParseName(Reading("1", "2", "3", "4", "5", "6"), 0.5).Value);   // таблица
        Assert.Null(FieldParser.ParseName(Reading("Корпус АБВГ.461411.123"), 0.5).Value);       // обозначение внутри
        Assert.Null(FieldParser.ParseName(Reading(new string('а', 200)), 0.5).Value);
    }

    // ── Общая область ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("АБВГ.461411.123|Корпус", "Корпус")]
    [InlineData("АБВГ.461411.123|Крышка|верхняя", "Крышка верхняя")]
    [InlineData("Корпус|АБВГ.461411.123", "Корпус")]
    [InlineData("АБВГ.461411.123 Корпус", "Корпус")]
    [InlineData("Плата управления АБВГ.461411.123", "Плата управления")]
    public void Combined_area_splits(string lines, string name)
    {
        var (d, n) = FieldParser.ParseCombined(Reading(lines.Split('|')), 0.5);
        Assert.Equal("АБВГ.461411.123", d.Value);
        Assert.Equal(name, n.Value);
    }

    [Theory]
    [InlineData("Корпус|АБВГ.461411.123|Сборочный чертеж", "однозначно")]   // текст и до, и после
    [InlineData("Корпус АБВГ.461411.123 сварной", "однозначно")]
    [InlineData("АБВГ.461411.123|АБВГ.461411.124|Корпус", "несколько")]
    [InlineData("Корпус|верхний", "не найдено")]
    [InlineData("АБВГ.461411.123", "только обозначение")]
    public void Combined_area_ambiguities_are_skipped(string lines, string reason)
    {
        var (d, n) = FieldParser.ParseCombined(Reading(lines.Split('|')), 0.5);
        Assert.True(d.Value is null || n.Value is null);
        Assert.Contains(reason, (d.Problem ?? "") + " " + (n.Problem ?? ""));
    }

    // ── Сверка двух движков ───────────────────────────────────────────────────

    private static EngineFields Fields(string engine, string? d, string? n) =>
        new(engine, d is null ? Parsed.Fail("не прочитано") : Parsed.Ok(d), n is null ? Parsed.Fail("не прочитано") : Parsed.Ok(n));

    [Fact]
    public void Agreement_requires_both_engines_and_exact_match()
    {
        var ok = EngineAgreement.Combine(Fields("Tesseract", "АБВГ.461411.123", "Корпус"), Fields("PaddleOCR", "АБВГ.461411.123", "Корпус"));
        Assert.Equal(new FieldValues("АБВГ.461411.123", "Корпус"), ok.Values);

        var designationDiffers = EngineAgreement.Combine(Fields("Tesseract", "АБВГ.461411.123", "Корпус"), Fields("PaddleOCR", "АВВГ.461411.123", "Корпус"));
        Assert.Null(designationDiffers.Values);
        Assert.Contains("по-разному", designationDiffers.Problem);

        var nameDiffers = EngineAgreement.Combine(Fields("Tesseract", "АБВГ.461411.123", "Корпус"), Fields("PaddleOCR", "АБВГ.461411.123", "Корпyс"));
        Assert.Null(nameDiffers.Values);

        var caseDiffers = EngineAgreement.Combine(Fields("Tesseract", "АБВГ.461411.123", "Корпус"), Fields("PaddleOCR", "АБВГ.461411.123", "КОРПУС"));
        Assert.Null(caseDiffers.Values);

        var scriptDiffers = EngineAgreement.Combine(Fields("Tesseract", "АБВГ.461411.123", "Блок ТКС"), Fields("PaddleOCR", "АБВГ.461411.123", "Блок TKC"));
        Assert.Null(scriptDiffers.Values);

        // «B» одного движка подтверждается кириллической «В» другого.
        var confirmed = EngineAgreement.Combine(Fields("Tesseract", "АБВГ.461411.123", "Вал"), Fields("PaddleOCR", "АБBГ.461411.123", "Bал"));
        Assert.Equal(new FieldValues("АБВГ.461411.123", "Вал"), confirmed.Values);

        // «B» у обоих — это могла быть «Б»: не подтверждено.
        var unconfirmed = EngineAgreement.Combine(Fields("Tesseract", "АBРС.713656.909", "Корпус"), Fields("PaddleOCR", "АBРС.713656.909", "Корпус"));
        Assert.Null(unconfirmed.Values);
    }

    [Fact]
    public void One_engine_is_never_enough()
    {
        var r = EngineAgreement.Combine(Fields("Tesseract", "АБВГ.461411.123", "Корпус"), Fields("PaddleOCR", null, null));
        Assert.Null(r.Values);
        Assert.Contains("только Tesseract", r.Problem);
    }

    // ── Имя файла ─────────────────────────────────────────────────────────────

    [Fact]
    public void File_name_is_designation_space_name()
    {
        Assert.Equal("АБВГ.123456.001 Корпус.pdf", FileNames.Build(new("АБВГ.123456.001", "Корпус")).Name);
        Assert.Equal("АБВГ.123456.001 Кабель 'USB'.pdf", FileNames.Build(new("АБВГ.123456.001", "Кабель \"USB\"")).Name);
    }

    [Theory]
    [InlineData("Кронштейн 1/2")]
    [InlineData("Плата: верхняя")]
    [InlineData("Что?")]
    [InlineData("Тип <А>")]
    [InlineData("Шкаф|стойка")]
    public void Forbidden_characters_skip_the_file(string name)
    {
        var (file, problem) = FileNames.Build(new("АБВГ.123456.001", name));
        Assert.Null(file);
        Assert.Contains("недопустимый", problem);
    }

    [Fact]
    public void Too_long_names_are_skipped()
    {
        // 130 кириллических букв — 260 байт UTF-8: больше предела имени в ext4/NTFS.
        var (file, problem) = FileNames.Build(new("АБВГ.123456.001", new string('ж', 130)));
        Assert.Null(file);
        Assert.Contains("255", problem);
    }
}
