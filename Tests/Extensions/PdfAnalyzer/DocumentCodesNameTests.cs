using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Сборка «Наименования документа» — колонки, ради которой расширение и заводили: изделие берётся
/// из соседней спецификации, тип документа — из кода обозначения, лист утверждения — отдельной
/// частью. Однострочный и многострочный варианты обязаны собираться из ОДНИХ И ТЕХ ЖЕ частей,
/// иначе включение галочки «в несколько строк» меняло бы содержимое, а не только вид.
/// </summary>
public sealed class DocumentCodesNameTests
{
    private static Dictionary<string, string> NewCache() => new(StringComparer.Ordinal);

    private static Dictionary<string, string> Spec(string designation, string product) =>
        new(StringComparer.Ordinal) { [designation] = product };

    // ── Склейка частей ────────────────────────────────────────────────────────

    [Fact]
    public void Full_name_is_product_plus_document_type()
    {
        var name = DocumentCodes.GetDocumentFullName(
            "АБВГ.301234.001СБ", "АБВГ.301234.001СБ.pdf",
            Spec("АБВГ.301234.001", "Блок питания"), NewCache());

        Assert.Equal("Блок питания Сборочный чертеж", name);
    }

    [Fact]
    public void Multiline_name_puts_every_part_on_its_own_line()
    {
        var name = DocumentCodes.GetDocumentFullNameMultiline(
            "АБВГ.301234.001СБ", "АБВГ.301234.001СБ.pdf",
            Spec("АБВГ.301234.001", "Блок питания"), NewCache());

        Assert.Equal("Блок питания\nСборочный чертеж", name);
    }

    // Однострочный и многострочный — одни и те же части, отличается только разделитель.
    [Fact]
    public void Single_line_and_multiline_carry_the_same_parts()
    {
        var spec  = Spec("АБВГ.301234.001", "Блок питания");
        const string designation = "АБВГ.301234.001СБ-ЛУ";
        const string file        = "АБВГ.301234.001СБ-ЛУ.pdf";

        var single = DocumentCodes.GetDocumentFullName(designation, file, spec, NewCache());
        var multi  = DocumentCodes.GetDocumentFullNameMultiline(designation, file, spec, NewCache());

        Assert.Equal(single, multi.Replace('\n', ' '));
    }

    [Fact]
    public void Empty_designation_gives_the_file_name()
    {
        var name = DocumentCodes.GetDocumentFullName(null, "Просто файл.pdf", null, NewCache());

        Assert.Equal("Просто файл.pdf", name);
    }

    // ── Имя изделия ───────────────────────────────────────────────────────────

    [Fact]
    public void Product_name_comes_from_the_specification_map()
    {
        var parts = DocumentCodes.GetDocumentFullNameParts(
            "АБВГ.301234.001СБ", "АБВГ.301234.001СБ.pdf",
            Spec("АБВГ.301234.001", "Блок питания"), NewCache());

        Assert.Equal("Блок питания",     parts.Product);
        Assert.Equal("Сборочный чертеж", parts.DocType);
        Assert.Equal("",                 parts.Suffix);
    }

    /// <summary>Найденное имя кладётся в кэш: на папке из тысяч файлов поиск по карте
    /// обозначений повторяется для каждого документа одного изделия.</summary>
    [Fact]
    public void Product_name_is_cached_between_calls()
    {
        var cache = NewCache();
        DocumentCodes.GetDocumentFullName("АБВГ.301234.001СБ", "АБВГ.301234.001СБ.pdf",
                                          Spec("АБВГ.301234.001", "Блок питания"), cache);

        // Второй вызов уже БЕЗ карты обозначений — имя обязано прийти из кэша.
        var name = DocumentCodes.GetDocumentFullName("АБВГ.301234.001ПЗ", "АБВГ.301234.001ПЗ.pdf",
                                                    null, cache);

        Assert.Equal("Блок питания Пояснительная записка", name);
    }

    // Карты нет — имя вычищается из имени файла (номер и хвостовые разделители убираются).
    [Fact]
    public void Product_name_falls_back_to_the_file_name()
    {
        var name = DocumentCodes.GetDocumentFullName(
            "АБВГ.301234.001СБ", "АБВГ.301234.001СБ Блок питания.pdf", null, NewCache());

        Assert.Equal("Блок питания Сборочный чертеж", name);
    }

    /// <summary>Тип документа не должен «протекать» в слот изделия. Частый случай для ПД: в имени
    /// файла и номер, и словами тот же тип — без проверки IsDocTypeName получалось бы
    /// «Текст программы Текст программы».</summary>
    [Fact]
    public void Document_type_never_leaks_into_the_product_slot()
    {
        var parts = DocumentCodes.GetDocumentFullNameParts(
            "АБВГ.12345-01 12 01", "АБВГ.12345-01 12 01 Текст программы.pdf", null, NewCache());

        Assert.Equal("",                parts.Product);
        Assert.Equal("Текст программы", parts.DocType);
    }

    // ── Листы утверждения ─────────────────────────────────────────────────────

    /// <summary>Лист утверждения — ОТДЕЛЬНАЯ часть: тип основного документа обязан сохраниться,
    /// иначе «СБ-ЛУ» превратился бы просто в «Лист утверждения» без указания, к чему он.</summary>
    [Fact]
    public void Approval_sheet_is_a_separate_part()
    {
        var parts = DocumentCodes.GetDocumentFullNameParts(
            "АБВГ.301234.001СБ-ЛУ", "АБВГ.301234.001СБ-ЛУ.pdf",
            Spec("АБВГ.301234.001", "Блок питания"), NewCache());

        Assert.Equal("Блок питания",       parts.Product);
        Assert.Equal("Сборочный чертеж",   parts.DocType);
        Assert.Equal("Лист утверждения",   parts.Suffix);
    }

    [Fact]
    public void Certification_sheet_is_a_separate_part()
    {
        var parts = DocumentCodes.GetDocumentFullNameParts(
            "АБВГ.301234.001СБ-УЛ", "АБВГ.301234.001СБ-УЛ.pdf",
            Spec("АБВГ.301234.001", "Блок питания"), NewCache());

        Assert.Equal("Сборочный чертеж", parts.DocType);
        Assert.Equal("Информационно-удостоверяющий лист", parts.Suffix);
    }

    /// <summary>Конкретные комбинации «код + лист» (Д42-УЛ и подобные) — приоритетные
    /// переопределения: у них своё имя, которое общая ветка «-УЛ» затёрла бы.</summary>
    [Fact]
    public void Special_code_and_sheet_combination_wins()
    {
        var parts = DocumentCodes.GetDocumentFullNameParts(
            "АБВГ.301234.001Д42-УЛ", "АБВГ.301234.001Д42-УЛ.pdf",
            Spec("АБВГ.301234.001", "Блок питания"), NewCache());

        Assert.Equal("Блок питания",             parts.Product);
        Assert.Equal("Данные программирования",  parts.DocType);
        Assert.Equal("Информационно-удостоверяющий лист", parts.Suffix);
    }

    // ── Реестр названий видов ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Сборочный чертеж",     true)]   // из таблицы КД
    [InlineData("Руководство оператора", true)]  // из кодов вида ПД
    [InlineData("Спецификация",         true)]   // ПД без кода вида
    [InlineData("Прочие документы",     true)]   // диапазон 90–99
    [InlineData("Блок питания",         false)]  // имя изделия
    [InlineData("",                     false)]
    public void Known_document_type_names_are_recognised(string name, bool expected) =>
        Assert.Equal(expected, DocumentCodes.IsDocTypeName(name));

    [Fact]
    public void Null_is_not_a_document_type_name() =>
        Assert.False(DocumentCodes.IsDocTypeName(null));
}
