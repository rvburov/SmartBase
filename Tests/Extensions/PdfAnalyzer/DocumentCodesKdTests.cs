using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Разбор конструкторских обозначений (ЕСКД, ГОСТ 2.201): АААА.000000.000[-исполн.][КОД][-ЛУ|-УЛ].
/// От этого разбора зависит и тип документа, и порядок строк в ведомости, и подтягивание имени
/// изделия — то есть почти весь результат расширения. Ошибка тиха: ведомость соберется, просто
/// не та.
/// </summary>
public sealed class DocumentCodesKdTests
{
    // ── Базовый номер ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("АБВГ.301234.001СБ",  "АБВГ.301234.001")]   // код без дефиса
    [InlineData("АБВГ.301234.001-СБ", "АБВГ.301234.001")]   // код через дефис
    [InlineData("АБВГ.301234.001ПЗ",  "АБВГ.301234.001")]
    [InlineData("АБВГ.301234.001",    "АБВГ.301234.001")]   // кода нет — номер уже базовый
    public void Base_designation_drops_the_document_code(string designation, string expected) =>
        Assert.Equal(expected, DocumentCodes.GetBaseDesignation(designation));

    [Fact]
    public void Base_designation_drops_the_execution_suffix() =>
        Assert.Equal("АБВГ.301234.001", DocumentCodes.GetBaseDesignation("АБВГ.301234.001-02"));

    // «-ЛУ»/«-УЛ» — лист утверждения/удостоверяющий лист: у них та же база, что у основного документа.
    [Theory]
    [InlineData("АБВГ.301234.001ПЗ-ЛУ")]
    [InlineData("АБВГ.301234.001ПЗ-УЛ")]
    public void Base_designation_drops_the_approval_sheet(string designation) =>
        Assert.Equal("АБВГ.301234.001", DocumentCodes.GetBaseDesignation(designation));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Base_of_an_empty_designation_is_empty(string? designation) =>
        Assert.Equal("", DocumentCodes.GetBaseDesignation(designation));

    // ── Базовый номер + код ───────────────────────────────────────────────────

    [Theory]
    [InlineData("АБВГ.301234.001СБ",  "АБВГ.301234.001", "СБ")]
    [InlineData("АБВГ.301234.001-СБ", "АБВГ.301234.001", "СБ")]
    public void Base_and_code_are_extracted_together(string designation, string expectedBase, string expectedCode)
    {
        var (baseNumber, code) = DocumentCodes.ExtractBaseAndCode(designation);

        Assert.Equal(expectedBase, baseNumber);
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void Designation_without_a_code_yields_no_code()
    {
        var (baseNumber, code) = DocumentCodes.ExtractBaseAndCode("АБВГ.301234.001");

        Assert.Equal("АБВГ.301234.001", baseNumber);
        Assert.Null(code);
    }

    /// <summary>Коды перебираются от длинного к короткому, иначе «ТУ1» разобралось бы как «ТУ»
    /// плюс мусор, а «РЭ10» — как «РЭ1»: части документа получили бы чужой тип.</summary>
    [Theory]
    [InlineData("АБВГ.301234.001ТУ1",  "ТУ1")]
    [InlineData("АБВГ.301234.001ТУ",   "ТУ")]
    [InlineData("АБВГ.301234.001РЭ10", "РЭ10")]
    [InlineData("АБВГ.301234.001РЭ1",  "РЭ1")]
    public void Longer_code_wins_over_its_prefix(string designation, string expectedCode)
    {
        var (_, code) = DocumentCodes.ExtractBaseAndCode(designation);

        Assert.Equal(expectedCode, code);
    }

    // ── Порядок в ведомости ───────────────────────────────────────────────────

    [Fact]
    public void Kd_order_puts_specification_first()
    {
        Assert.Equal(0, DocumentCodes.KdOrder["СПЕЦИФИКАЦИЯ"]);
        Assert.True(DocumentCodes.KdOrder["ПЗ"] < DocumentCodes.KdOrder["СБ"]);
    }

    /// <summary>Производные листы идут сразу за своим основным документом: лист утверждения —
    /// +0.1, удостоверяющий — +0.2. Иначе они уезжали бы в конец ведомости, оторвавшись от
    /// документа, к которому относятся.</summary>
    [Fact]
    public void Derived_sheets_follow_their_base_code()
    {
        var sb = DocumentCodes.KdOrder["СБ"];

        Assert.Equal(sb + 0.1, DocumentCodes.KdOrder["СБ-ЛУ"], precision: 6);
        Assert.Equal(sb + 0.2, DocumentCodes.KdOrder["СБ-УЛ"], precision: 6);
        Assert.True(DocumentCodes.KdOrder["СБ-УЛ"] < DocumentCodes.KdOrder["ГЧ"]);
    }

    // У самих суффиксов производных быть не может — иначе появились бы «ЛУ-ЛУ».
    [Theory]
    [InlineData("ЛУ-ЛУ")]
    [InlineData("УЛ-УЛ")]
    [InlineData("-ЛУ-ЛУ")]
    public void Suffix_codes_have_no_derivatives(string code) =>
        Assert.False(DocumentCodes.KdOrder.ContainsKey(code));

    // ── Извлечение номера из свободного текста ────────────────────────────────

    [Theory]
    [InlineData("АБВГ.301234.001 Сборочный чертеж",    "АБВГ.301234.001")]
    [InlineData("АБВГ.301234.001СБ Блок питания",      "АБВГ.301234.001СБ")]
    [InlineData("АБВГ.301234.001-02 Исполнение",       "АБВГ.301234.001-02")]
    [InlineData("АБВГ.301234.001ПЗ-ЛУ Записка",        "АБВГ.301234.001ПЗ-ЛУ")]
    public void Kd_number_is_extracted_from_free_text(string text, string expected) =>
        Assert.Equal(expected, DocumentCodes.ExtractNumbersKd(text));

    [Fact]
    public void Text_without_a_kd_number_gives_empty() =>
        Assert.Equal("", DocumentCodes.ExtractNumbersKd("Просто описание без номера"));

    // ── ТД: строгий формат ────────────────────────────────────────────────────

    [Fact]
    public void Td_number_is_extracted_from_free_text() =>
        Assert.Equal("АБВГ.12345.67890", DocumentCodes.ExtractNumbersTd("АБВГ.12345.67890 Маршрутная карта"));

    /// <summary>У ТД строгий формат АААА.00000.00000 — конструкторский номер (шесть цифр в
    /// середине) в технологические попадать не должен, иначе один документ уедет сразу в две
    /// ведомости.</summary>
    [Fact]
    public void Kd_number_is_not_taken_for_a_td_number() =>
        Assert.Equal("", DocumentCodes.ExtractNumbersTd("АБВГ.301234.001СБ"));
}
