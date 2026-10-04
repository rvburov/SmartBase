using SmartBase.Core.Documents;
using SmartBase.Views.Library;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Подсказка у погашенной кнопки «Как в документе» (<c>Views/Library/DocModeHint.cs</c>).
///
/// Раньше ее для любого не-PDF давала ветка PDF — «Страницами пока показывается только PDF», —
/// и у книги XLS или у книги, разбор которой сорвался, она говорила неправду. Здесь закрепляется,
/// что подсказка называет настоящую причину.
/// </summary>
public sealed class DocModeHintTests
{
    /// <summary>У PDF и книги настоящий вид есть — подсказку решит итог открытия.</summary>
    [Theory]
    [InlineData("договор.pdf")]
    [InlineData("смета.xlsx")]
    [InlineData(@"C:\папка\смета.XLSM")]
    public void Viewable_formats_have_no_hint_before_opening(string path)
    {
        Assert.Null(DocModeHint.ForFormat(path));
    }

    /// <summary>У старой книги, документа Word и чертежа подсказка называет их формат.</summary>
    [Theory]
    [InlineData("смета.xls",   "XLS")]
    [InlineData("письмо.docx", "DOCX")]
    [InlineData("чертеж.dwg",  "DWG")]
    public void Other_formats_are_named_in_the_hint(string path, string format)
    {
        var hint = DocModeHint.ForFormat(path) ?? "";

        Assert.Contains($"а {format} — нет", hint, StringComparison.Ordinal);
        Assert.Contains("XLSX", hint, StringComparison.Ordinal);
        Assert.Contains("«Открыть»", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("только PDF", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_an_extension_gets_the_general_hint()
    {
        var hint = DocModeHint.ForFormat("readme") ?? "";

        Assert.StartsWith("В режиме «Как в документе» показываются PDF", hint, StringComparison.Ordinal);
        Assert.DoesNotContain(" — нет", hint, StringComparison.Ordinal);
    }

    /// <summary>У текстового файла другого вида нет — так и сказано.</summary>
    [Theory]
    [InlineData("заметки.txt")]
    [InlineData("настройки.json")]
    [InlineData("выгрузка.xml")]
    public void Text_files_say_they_have_no_other_view(string path)
    {
        Assert.Contains("текстового файла", DocModeHint.ForFormat(path) ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Итог разбора книги — три разных ответа: книга не открылась, в ней нет листов с данными,
    /// все листы с данными скрыты. Раньше книга, которая не открылась, называлась пустой.
    /// </summary>
    [Fact]
    public void A_book_explains_why_its_table_is_not_shown()
    {
        var sheet = new DocSheet("Смета", [new DocSheetRow(0, [new DocCell(0, "x")])], []);

        Assert.Equal(DocModeHint.BookFailed, DocModeHint.ForBook(DocBookView.Failed));
        Assert.Equal(DocModeHint.BookEmpty,  DocModeHint.ForBook(DocBookView.Empty));
        Assert.Equal(DocModeHint.BookHidden, DocModeHint.ForBook(new DocBookView([], HiddenSheets: 2)));
        Assert.Null(DocModeHint.ForBook(new DocBookView([sheet], 0)));
    }

    /// <summary>
    /// Книга тяжелее порога — четвертый ответ, и не «пустая» и не «не открылась»: ее не
    /// разбирали вовсе. Подсказка называет порог и место, где он меняется, — как и надпись режима
    /// «Текст» о том же документе.
    /// </summary>
    [Fact]
    public void A_book_heavier_than_the_threshold_names_the_threshold()
    {
        var hint = DocModeHint.ForBook(DocBookView.TooBig(64)) ?? "";

        Assert.Contains("тяжелее порога чтения (64 МБ)", hint, StringComparison.Ordinal);
        Assert.Contains("в настройках библиотеки", hint, StringComparison.Ordinal);
    }
}
