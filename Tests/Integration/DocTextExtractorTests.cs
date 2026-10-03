using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SmartBase.Core.Documents;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Извлечение текста документов (<c>Core/Documents/DocTextExtractor.cs</c>) — то, чем питается
/// поиск по содержимому.
///
/// Тесты интеграционные, а не юнит: разборщики открывают настоящие файлы настоящими
/// библиотеками (OpenXml для DOCX), и подменять это нечем — именно на чужих файлах и ломается
/// разбор. Главное правило проверяется отдельно: исключений метод не бросает НИКОГДА, потому
/// что очередь обходит тысячи чужих файлов и любой из них может оказаться битым.
/// </summary>
public sealed class DocTextExtractorTests : IDisposable
{
    private readonly string _dir;

    public DocTextExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-doctext-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private string WriteText(string name, string text, Encoding? encoding = null)
    {
        var path = Path_(name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private string WriteDocx(string name, string text)
    {
        var path = Path_(name);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body(new Paragraph(new Run(new Text(text)))));
        main.Document.Save();
        return path;
    }

    // ── Книга: числа так, как их показывает панель ───────────────────────────

    /// <summary>
    /// Числа книги индекс пишет так, как их показывает панель: дата — «05.03.2024», а не номер
    /// дня, деньги — с разрядами, проценты — процентами, логическое значение — словом. Пока индекс
    /// хранил числа как они лежат в файле, по тому, что человек видит, книга не находилась.
    /// </summary>
    [Fact]
    public void Book_numbers_are_written_the_way_the_panel_shows_them()
    {
        var text = DocTextExtractor.Extract(DocNumbersBook.Write(_dir)).Text;

        Assert.Contains("05.03.2024", text, StringComparison.Ordinal);
        Assert.DoesNotContain(((int)DocNumbersBook.Day.ToOADate()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                              text, StringComparison.Ordinal);
        Assert.Contains(DocTextExtractor.Normalize(DocNumbersBook.Shown(DocNumbersBook.Money)), text, StringComparison.Ordinal);
        Assert.Contains("15%",    text, StringComparison.Ordinal);
        Assert.Contains("ИСТИНА", text, StringComparison.Ordinal);
        // Строка из формулы — текст: нули впереди остаются, хотя у ячейки запись «0.00».
        Assert.Contains("00123",  text, StringComparison.Ordinal);
    }

    /// <summary>Книга со счетом дней от 1904 года: дата в индексе та же, что в книге.</summary>
    [Fact]
    public void A_1904_book_keeps_its_dates_in_the_index()
    {
        var text = DocTextExtractor.Extract(DocNumbersBook.Write(_dir, date1904: true)).Text;

        Assert.Contains("05.03.2024", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Скрытый лист поиск читает — сознательно: находки в служебных листах и свернутых группах не
    /// теряются. Число там записано так же, как записала бы его панель.
    /// </summary>
    [Fact]
    public void Hidden_sheets_are_still_searched()
    {
        var text = DocTextExtractor.Extract(DocNumbersBook.Write(_dir)).Text;

        Assert.Contains(DocTextExtractor.Normalize(DocNumbersBook.Shown(DocNumbersBook.HiddenMoney)),
                        text, StringComparison.Ordinal);
    }

    /// <summary>Главный договор: всякое значение, которое показывает панель, есть в тексте индекса.</summary>
    [Fact]
    public void Every_value_the_panel_shows_is_in_the_index()
    {
        var path  = DocNumbersBook.Write(_dir);
        var text  = DocTextExtractor.Extract(path).Text;
        var shown = DocSheetSource.Open(path).SelectMany(s => s.Rows).SelectMany(r => r.Cells).ToList();

        Assert.True(shown.Count >= 8, $"панель показала {shown.Count} ячеек");
        foreach (var cell in shown)
            Assert.Contains(DocTextExtractor.Normalize(cell.Text), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// У разбора книги своя редакция: подняв ее, программа перечитывает прочитанные книги, а
    /// документы прочих форматов не трогает.
    /// </summary>
    [Fact]
    public void Books_have_their_own_revision_of_the_reader()
    {
        Assert.Equal(1, DocTextExtractor.RevisionOf("смета.xlsx"));
        Assert.Equal(1, DocTextExtractor.RevisionOf(@"C:папкасмета.XLSM"));
        Assert.Equal(0, DocTextExtractor.RevisionOf("договор.pdf"));
        Assert.Equal(0, DocTextExtractor.RevisionOf("заметки.txt"));
    }

    // ── Расширения ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/docs/gost.pdf",  "pdf")]
    [InlineData("C:\\docs\\a.DOCX", "docx")]
    [InlineData("readme",           "")]
    [InlineData("archive.tar.gz",   "gz")]
    public void Extension_is_read_from_the_file_name(string path, string expected)
    {
        Assert.Equal(expected, DocTextExtractor.ExtensionOf(path));
    }

    [Fact]
    public void Only_listed_formats_are_supported()
    {
        Assert.True(DocTextExtractor.IsSupported("/a/b.pdf"));
        Assert.True(DocTextExtractor.IsSupported("/a/b.DOCX"));
        Assert.False(DocTextExtractor.IsSupported("/a/b.dwg"));
        Assert.False(DocTextExtractor.IsSupported("/a/b"));
    }

    // ── Текстовые файлы ──────────────────────────────────────────────────────

    [Fact]
    public void A_utf8_text_file_is_read()
    {
        var result = DocTextExtractor.Extract(WriteText("a.txt", "Автоматизированные системы"));

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Contains("Автоматизированные", result.Text);
    }

    /// <summary>
    /// Русский txt из старых систем лежит в windows-1251. Без отката он раскодировался бы в
    /// вопросительные знаки и в поиск не попал бы вовсе.
    /// </summary>
    [Fact]
    public void A_windows_1251_text_file_falls_back_to_the_right_encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = Path_("cp1251.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes("Регламент разработки"));

        var result = DocTextExtractor.Extract(path);

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Contains("Регламент", result.Text);
    }

    /// <summary>
    /// INI, JSON, XML, YML и YAML читаются как текст — и для поиска, и для панели. На плитке у них
    /// лента «ТЕКСТ», и она обещает, что внутри ищется; раньше поиск в них не заглядывал.
    /// </summary>
    [Theory]
    [InlineData("настройки.ini", "[Смета]\nВалюта=рубль")]
    [InlineData("выгрузка.json", "{\"смета\": \"рубль\"}")]
    [InlineData("смета.xml",     "<?xml version=\"1.0\" encoding=\"utf-8\"?><Смета Валюта=\"рубль\"/>")]
    [InlineData("сборка.yml",    "смета:\n  валюта: рубль")]
    [InlineData("сборка.yaml",   "смета:\n  валюта: рубль")]
    public void Config_and_data_files_are_read_as_text(string name, string text)
    {
        var path = WriteText(name, text);

        var result = DocTextExtractor.Extract(path);
        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Contains("рубль", result.Text, StringComparison.Ordinal);

        Assert.Equal(DocTextStatus.Ok, DocReadText.Read(path).Status);
        Assert.Equal(1, DocTextExtractor.RevisionOf(path));
    }

    [Fact]
    public void Markdown_and_csv_are_read_as_plain_text()
    {
        Assert.Equal(DocTextStatus.Ok, DocTextExtractor.Extract(WriteText("a.md", "# Заголовок")).Status);
        Assert.Equal(DocTextStatus.Ok, DocTextExtractor.Extract(WriteText("a.csv", "имя;значение")).Status);
    }

    /// <summary>Пустой файл — это «текста нет», а не ошибка: повторно его не тревожат.</summary>
    [Fact]
    public void An_empty_file_reports_no_text()
    {
        Assert.Equal(DocTextStatus.NoText, DocTextExtractor.Extract(WriteText("empty.txt", "")).Status);
        Assert.Equal(DocTextStatus.NoText, DocTextExtractor.Extract(WriteText("spaces.txt", "   \n\t ")).Status);
    }

    // ── Office ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_docx_file_is_read()
    {
        var result = DocTextExtractor.Extract(WriteDocx("a.docx", "Ведомость технологической документации"));

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Contains("технологической", result.Text);
    }

    // ── PDF ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Лист, повернутый без поправки содержимого, и надпись штампа, идущая снизу вверх, попадают
    /// в индекс целыми словами: разбор идет в порядке потока содержимого, и направление строки
    /// ему безразлично. Страница панели чтения находит то же самое — см.
    /// <c>DocPageSourceTests.Sideways_text_comes_in_whole_words</c>.
    /// </summary>
    [Fact]
    public void Turned_and_sideways_pdf_text_is_indexed_in_whole_words()
    {
        var path = HandPdf.Write(_dir, "боком.pdf",
            new HandPdf.Sheet(HandPdf.Plain(40, 782, "Dogovor postavki 21.501"), 90),
            new HandPdf.Sheet(HandPdf.Upright(270, HandPdf.Full, 40, 60, "Vedomost listov"), 270),
            new HandPdf.Sheet(HandPdf.Rising(30, 100, "Inv podl 4711")));

        var result = DocTextExtractor.Extract(path);

        Assert.Equal(DocTextStatus.Ok, result.Status);
        Assert.Contains("Dogovor postavki 21.501", result.Text, StringComparison.Ordinal);
        Assert.Contains("Vedomost listov",         result.Text, StringComparison.Ordinal);
        Assert.Contains("Inv podl 4711",           result.Text, StringComparison.Ordinal);
    }

    // ── Отказы ───────────────────────────────────────────────────────────────

    [Fact]
    public void An_unsupported_extension_is_reported_without_touching_the_file()
    {
        // Файла на диске нет вовсе — расширение отсекается до всякого ввода-вывода.
        Assert.Equal(DocTextStatus.Unsupported,
                     DocTextExtractor.Extract(Path_("chertezh.dwg")).Status);
    }

    [Fact]
    public void A_missing_file_fails_without_throwing()
    {
        Assert.Equal(DocTextStatus.Failed, DocTextExtractor.Extract(Path_("нет-такого.txt")).Status);
    }

    [Fact]
    public void An_empty_path_fails_without_throwing()
    {
        Assert.Equal(DocTextStatus.Failed, DocTextExtractor.Extract("").Status);
        Assert.Equal(DocTextStatus.Failed, DocTextExtractor.Extract("   ").Status);
    }

    /// <summary>Битый файл поддержанного формата — обычный исход обхода чужой папки.</summary>
    [Fact]
    public void A_broken_document_fails_without_throwing()
    {
        var path = Path_("broken.docx");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04]);

        Assert.Equal(DocTextStatus.Failed, DocTextExtractor.Extract(path).Status);
    }

    [Fact]
    public void A_file_over_the_threshold_is_skipped()
    {
        var path = Path_("big.txt");
        File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);

        Assert.Equal(DocTextStatus.TooBig, DocTextExtractor.Extract(path, maxFileMb: 1).Status);
        // Порог 0 означает «без порога» — файл разбирается.
        Assert.NotEqual(DocTextStatus.TooBig, DocTextExtractor.Extract(path, maxFileMb: 0).Status);
    }

    // ── Нормализация ─────────────────────────────────────────────────────────

    [Fact]
    public void Whitespace_is_collapsed_into_single_spaces()
    {
        Assert.Equal("а б в", DocTextExtractor.Normalize("  а\n\n\tб    в  "));
    }

    /// <summary>
    /// Управляющие символы вычищаются — в том числе те, которыми сниппет помечает совпадения.
    /// Иначе содержимое документа могло бы подделать маркер и разъехаться с разбором.
    /// </summary>
    [Fact]
    public void Control_characters_are_stripped()
    {
        var raw = "текст" + '\u0002' + "подделка" + '\u0003' + "конец";

        var clean = DocTextExtractor.Normalize(raw);

        Assert.DoesNotContain('\u0002', clean);
        Assert.DoesNotContain('\u0003', clean);
        Assert.Equal("текст подделка конец", clean);
    }

    [Fact]
    public void Normalize_handles_empty_input()
    {
        Assert.Equal("", DocTextExtractor.Normalize(null));
        Assert.Equal("", DocTextExtractor.Normalize(""));
        Assert.Equal("", DocTextExtractor.Normalize("   "));
    }

    [Fact]
    public void Normalize_caps_the_text_length()
    {
        var huge = new string('а', DocTextExtractor.MaxChars + 1000);

        Assert.Equal(DocTextExtractor.MaxChars, DocTextExtractor.Normalize(huge).Length);
    }
}
