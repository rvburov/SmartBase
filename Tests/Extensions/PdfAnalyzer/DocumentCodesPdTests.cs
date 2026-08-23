using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Разбор программных обозначений (ЕСПД, ГОСТ 19.101/19.103): КОДО.РРРРР-ИИ [ВВ ДД][-Ч][-ЛУ|-УЛ],
/// где ВВ — код ВИДА документа. Тип программного документа берется только отсюда, и ошибка здесь
/// уводит документ в чужую ведомость: ПД и КД разведены по разным выгрузкам целиком.
/// </summary>
public sealed class DocumentCodesPdTests
{
    // ── Тип по коду вида ──────────────────────────────────────────────────────

    // Номер без кода вида — это сама программа, ее строка в ведомости называется «Спецификация».
    [Fact]
    public void Number_without_a_kind_code_is_a_specification() =>
        Assert.Equal("Спецификация", DocumentCodes.ResolvePdType("АБВГ.12345-01"));

    [Theory]
    [InlineData("05", "Ведомость держателей подлинников")]
    [InlineData("12", "Текст программы")]
    [InlineData("13", "Описание программы")]
    [InlineData("20", "Ведомость эксплуатационных документов")]
    [InlineData("30", "Формуляр")]
    [InlineData("31", "Описание применения")]
    [InlineData("32", "Руководство системного программиста")]
    [InlineData("33", "Руководство программиста")]
    [InlineData("34", "Руководство оператора")]
    [InlineData("35", "Описание языка")]
    [InlineData("46", "Руководство по техническому обслуживанию")]
    [InlineData("51", "Программа и методика испытаний")]
    [InlineData("81", "Пояснительная записка")]
    public void Known_kind_code_gives_its_name(string vid, string expected) =>
        Assert.Equal(expected, DocumentCodes.ResolvePdType($"АБВГ.12345-01 {vid} 01"));

    // Диапазон 90–99 по ГОСТ 19.101 — «прочие документы», перечислять их поименно не нужно.
    [Theory]
    [InlineData("90")]
    [InlineData("95")]
    [InlineData("99")]
    public void Kind_codes_from_ninety_to_ninety_nine_are_other_documents(string vid) =>
        Assert.Equal("Прочие документы", DocumentCodes.ResolvePdType($"АБВГ.12345-01 {vid} 01"));

    [Fact]
    public void Unknown_kind_code_is_not_a_program_document() =>
        Assert.Equal("", DocumentCodes.ResolvePdType("АБВГ.12345-01 77 01"));

    /// <summary>Конструкторский номер не должен разбираться как программный: иначе один документ
    /// попал бы и в ведомость КД, и в ведомость ПД.</summary>
    [Fact]
    public void Kd_number_is_not_taken_for_a_program_document() =>
        Assert.Equal("", DocumentCodes.ResolvePdType("АБВГ.301234.001СБ"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_designation_is_not_a_program_document(string? designation) =>
        Assert.Equal("", DocumentCodes.ResolvePdType(designation));

    // ── База программы (изделие) ──────────────────────────────────────────────

    /// <summary>Редакция «-ИИ» — часть изделия, а не документа: программы разных редакций
    /// называются по-разному, и имя изделия обязано подтягиваться с учетом редакции.</summary>
    [Fact]
    public void Program_base_keeps_the_edition()
    {
        Assert.Equal("АБВГ.12345-01", DocumentCodes.GetPdProgramBase("АБВГ.12345-01 34 01"));
        Assert.Equal("АБВГ.12345-02", DocumentCodes.GetPdProgramBase("АБВГ.12345-02 34 01"));
    }

    /// <summary>Носители программы (НД — текст на носителе, НДЗ — загрузочный диск) делят изделие
    /// с самой программой: их база — тот же программный номер, иначе имя изделия к ним не
    /// подтянется.</summary>
    [Theory]
    [InlineData("АБВГ.12345-01НД")]
    [InlineData("АБВГ.12345-01НДЗ")]
    [InlineData("АБВГ.12345-01НД-УЛ")]
    [InlineData("АБВГ.12345-01НДЗ-УЛ")]
    public void Media_carriers_share_the_program_base(string designation) =>
        Assert.Equal("АБВГ.12345-01", DocumentCodes.GetPdProgramBase(designation));

    /// <summary>При этом по ВИДУ носители не разрешаются: их тип задает код НД/НДЗ из таблицы
    /// КД, а не код вида ЕСПД. Две разные ветки разбора не должны спорить.</summary>
    [Theory]
    [InlineData("АБВГ.12345-01НД")]
    [InlineData("АБВГ.12345-01НДЗ")]
    public void Media_carriers_are_not_resolved_as_a_kind(string designation) =>
        Assert.Equal("", DocumentCodes.ResolvePdType(designation));

    [Theory]
    [InlineData("АБВГ.12345-01 34 01-1")]
    [InlineData("АБВГ.12345-01 34 01-ЛУ")]
    [InlineData("АБВГ.12345-01-ЛУ")]
    [InlineData("АБВГ.12345-01-УЛ")]
    public void Part_and_sheet_suffixes_do_not_break_the_base(string designation) =>
        Assert.Equal("АБВГ.12345-01", DocumentCodes.GetPdProgramBase(designation));

    [Fact]
    public void Kd_number_has_no_program_base() =>
        Assert.Equal("", DocumentCodes.GetPdProgramBase("АБВГ.301234.001СБ"));

    // ── Извлечение номера из свободного текста ────────────────────────────────

    [Theory]
    [InlineData("АБВГ.12345-01 34 01 Руководство оператора", "АБВГ.12345-01 34 01")]
    [InlineData("АБВГ.12345-01 Спецификация",                "АБВГ.12345-01")]
    [InlineData("АБВГ.12345-01 34 01-ЛУ Лист",               "АБВГ.12345-01 34 01-ЛУ")]
    public void Program_number_is_extracted_from_free_text(string text, string expected) =>
        Assert.Equal(expected, DocumentCodes.ExtractNumbersPd(text));

    [Fact]
    public void Text_without_a_pd_number_gives_empty() =>
        Assert.Equal("", DocumentCodes.ExtractNumbersPd("Просто описание без номера"));
}
