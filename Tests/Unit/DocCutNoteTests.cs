using SmartBase.Core.Documents;
using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Строка «показано не все» панели чтения (<c>Views/Library/DocCutNote.cs</c>).
///
/// Потолки панели нужны, но раньше они обрывали документ молча: том показывал треть, лист —
/// первые две тысячи строк, и человек читал обрезанное как целое. Здесь закрепляется, что строка
/// говорит ровно о том, что обрезано, — не молчит при обрезе и не пугает, когда показано все.
/// </summary>
public sealed class DocCutNoteTests
{
    private const string Nbsp = " ";

    private static DocReadResult Read(int pages = 0, DocReadCut cut = default) =>
        new(DocTextStatus.Ok, [new DocPart("", "текст")], pages, cut);

    private static DocSheet Sheet(string name, DocSheetCut cut = default) =>
        new(name, [new DocSheetRow(0, [new DocCell(0, "x")])], [], cut: cut);

    // ── Показано все ──────────────────────────────────────────────────────────

    [Fact]
    public void A_whole_document_has_no_note()
    {
        Assert.Equal("", DocCutNote.ForText(Read(pages: 12, cut: new DocReadCut(PagesShown: 12))));
        Assert.Equal("", DocCutNote.ForText(Read()));
        Assert.Equal("", DocCutNote.ForPages(12, 12));
        Assert.Equal("", DocCutNote.ForSheet(Sheet("Смета"), hiddenSheets: 0));
        Assert.Equal("", DocCutNote.ForSheet(null, hiddenSheets: 0));
    }

    // ── Обрезано потолком ─────────────────────────────────────────────────────

    [Fact]
    public void A_tome_says_which_pages_are_shown_and_where_the_rest_is()
    {
        var note = DocCutNote.ForText(Read(pages: 900, cut: new DocReadCut(PagesShown: 300)));

        Assert.Contains("Показаны страницы 1–300 из 900.", note, StringComparison.Ordinal);
        Assert.Contains("«Открыть»", note, StringComparison.Ordinal);
        Assert.Equal(note, DocCutNote.ForPages(300, 900));
    }

    /// <summary>Страницы — главнее знаков: «1–120 из 900» объясняет обрез точнее, чем «4 млн знаков».</summary>
    [Fact]
    public void Pages_win_over_the_char_cap()
    {
        var note = DocCutNote.ForText(Read(pages: 900, cut: new DocReadCut(PagesShown: 120, Text: true)));

        Assert.Contains("страницы 1–120 из 900", note, StringComparison.Ordinal);
        Assert.DoesNotContain("миллион", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_past_the_char_cap_says_so()
    {
        var note = DocCutNote.ForText(Read(cut: new DocReadCut(Text: true)));

        Assert.Contains("не целиком", note, StringComparison.Ordinal);
        Assert.Contains("четыре миллиона знаков", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_sheet_says_how_much_of_it_is_shown()
    {
        var cut  = new DocSheetCut(Rows: true, RowsShown: 2000, RowsTotal: 5400,
                                   Columns: true, ColumnsShown: 128, ColumnsTotal: 140);
        var note = DocCutNote.ForSheet(Sheet("Смета", cut), hiddenSheets: 0);

        Assert.Contains($"Лист «Смета» показан не целиком: строки 1–2{Nbsp}000 из 5{Nbsp}400, " +
                        "первые 128 колонок из 140.", note, StringComparison.Ordinal);
        Assert.Contains("«Открыть»", note, StringComparison.Ordinal);
    }

    /// <summary>Сколько строк всего, неизвестно — числа нет, выдумывать его нельзя.</summary>
    [Fact]
    public void An_unknown_total_is_not_made_up()
    {
        var note = DocCutNote.ForSheet(Sheet("Смета", new DocSheetCut(Rows: true, RowsShown: 2000)), 0);

        Assert.Contains($"строки 1–2{Nbsp}000.", note, StringComparison.Ordinal);
        Assert.DoesNotContain(" из ", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_cut_sheets_are_named()
    {
        var cut  = new DocSheetCut(Rows: true, RowsShown: 2000);
        var note = DocCutNote.ForText(Read(cut: new DocReadCut(
            Sheets: [("Смета", cut), ("Ресурсы", cut), ("Итоги", cut)])));

        Assert.Contains("листы «Смета», «Ресурсы» и «Итоги»", note, StringComparison.Ordinal);

        var many = DocCutNote.ForText(Read(cut: new DocReadCut(
            Sheets: [("А", cut), ("Б", cut), ("В", cut), ("Г", cut), ("Д", cut)])));
        Assert.Contains("листы «А», «Б» и еще 3", many, StringComparison.Ordinal);
    }

    // ── Скрытые листы ─────────────────────────────────────────────────────────

    /// <summary>
    /// Скрытый лист — не обрез: за ним некуда идти, его не показывает и сам Excel. Но о нем надо
    /// сказать, потому что поиск ищет и в нем, и найденное там слово в панели не подсветится.
    /// </summary>
    [Fact]
    public void Hidden_sheets_are_named_without_sending_anyone_to_open_the_file()
    {
        var one   = DocCutNote.ForSheet(Sheet("Смета"), hiddenSheets: 1);
        var three = DocCutNote.ForText(Read(cut: new DocReadCut(HiddenSheets: 3)));

        Assert.Equal("В книге 1 скрытый лист: панель его не показывает, как и Excel, а поиск по " +
                     "содержимому ищет и в нем.", one);
        Assert.Contains("3 скрытых листа: панель их не показывает", three, StringComparison.Ordinal);
        Assert.DoesNotContain("«Открыть»", three, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_and_hidden_sheets_come_together_cut_first()
    {
        var note = DocCutNote.ForSheet(Sheet("Смета", new DocSheetCut(Rows: true, RowsShown: 2000)), 2);

        Assert.True(note.IndexOf("показан не целиком", StringComparison.Ordinal) <
                    note.IndexOf("скрытых листа", StringComparison.Ordinal));
    }

    // ── Числа ─────────────────────────────────────────────────────────────────

    /// <summary>Разряды делит неразрывный пробел — число не переносится посередине строки.</summary>
    [Theory]
    [InlineData(7,       "7")]
    [InlineData(2000,    "2 000")]
    [InlineData(1234567, "1 234 567")]
    public void Numbers_are_grouped_with_a_non_breaking_space(int n, string expected)
    {
        Assert.Equal(expected, DocCutNote.N(n));
    }
}
