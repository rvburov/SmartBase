using System.IO;
using SkiaSharp;
using SmartBase.Core.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Страницы PDF для панели чтения (<see cref="DocPageSource"/>): размеры, картинка страницы и
/// слова с их местами.
///
/// Тесты интеграционные: работают два настоящих разборщика чужого формата — PDFium рисует, PdfPig
/// отдает слова, — и подменять их нечем. Главное, что сторожат проверки, — СОГЛАСИЕ этих двоих:
/// подсветка ставится по координатам от PdfPig поверх картинки от PDFium, и разъехаться им
/// нельзя. Разъезд был бы тихим: желтый прямоугольник честно лег бы на страницу, просто не на то
/// слово.
/// </summary>
public sealed class DocPageSourceTests : IDisposable
{
    private readonly string _dir;

    public DocPageSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sb-docpage-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    /// <summary>
    /// Документ, где на каждой странице ОДНО слово в известном месте: сверху слева. По нему
    /// проверяется и разбор, и перевод координат.
    /// </summary>
    private string WritePdf(string name, int pages = 1, double width = 595, double height = 842)
    {
        var path = Path.Combine(_dir, name);
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        for (var p = 1; p <= pages; p++)
        {
            var page = builder.AddPage(width, height);
            // 40 пунктов от левого края и 60 от верхнего.
            page.AddText($"Dogovor{p}", 14, new PdfPoint(40, height - 60), font);
        }
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>
    /// Документ из листов РАЗНОГО размера: книжный, вытянутый вдвое и альбомный. Аспекты у них
    /// разные намеренно — на одинаковых (А4 и А3 отличаются только величиной) расхождение не
    /// вылезло бы вовсе.
    /// </summary>
    private string WriteMixedPdf(string name)
    {
        var path = Path.Combine(_dir, name);
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var (width, height) in new[] { (595.0, 842.0), (595.0, 1400.0), (1191.0, 842.0) })
        {
            var page = builder.AddPage(width, height);
            page.AddText("Dogovor", 14, new PdfPoint(40, height - 60), font);
        }
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    // ── Открытие ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Листы разного размера выходят в ОДНУ запрошенную ширину — то самое «по ширине окна»,
    /// что и в любом просмотрщике, — а высота у каждого своя, по его собственной пропорции.
    ///
    /// Пока высота рамки вписывания считалась по ЗАПРОШЕННОЙ странице, рамка оставалась от той,
    /// что нарисовали первой, и лист с более вытянутой пропорцией PDFium вписывал уже по
    /// ВЫСОТЕ: тот выходил вдвое уже соседей, мельче текстом и с пустым местом по бокам. Какой
    /// именно лист задаст рамку, решал порядок прокрутки, поэтому один и тот же документ
    /// выглядел по-разному в зависимости от того, с какого места его открыли.
    /// </summary>
    [Fact]
    public void Pages_of_different_shapes_render_at_one_width()
    {
        using var source = DocPageSource.Open(WriteMixedPdf("разный.pdf"));
        Assert.NotNull(source);
        Assert.Equal(3, source!.PageCount);

        // Первым рисуем книжный лист — именно он раньше и задавал рамку всем остальным.
        var widths  = new int[3];
        var heights = new int[3];
        foreach (var i in new[] { 0, 1, 2 })
        {
            var png = source.Render(i, 400);
            Assert.NotNull(png);

            using var bitmap = SKBitmap.Decode(png);
            widths[i]  = bitmap.Width;
            heights[i] = bitmap.Height;
        }

        // Ширина одна на все листы. PDFium округляет вписывание, поэтому допуск в пиксель.
        Assert.InRange(widths[0], 399, 400);
        Assert.True(widths.Max() - widths.Min() <= 1,
                    $"ширины разошлись: {string.Join(", ", widths)}; высоты: {string.Join(", ", heights)}");

        // Высота — от пропорции САМОГО листа, а не от соседнего.
        foreach (var i in new[] { 0, 1, 2 })
        {
            var size     = source.Size(i);
            var expected = (int)Math.Round(widths[i] * size.Height / size.Width);
            Assert.InRange(heights[i], expected - 2, expected + 2);
        }

        // Вытянутый лист обязан выйти заметно выше книжного, а альбомный — ниже.
        Assert.True(heights[1] > heights[0], "вытянутый лист не выше книжного");
        Assert.True(heights[2] < heights[0], "альбомный лист не ниже книжного");
    }

    /// <summary>
    /// Картинка страницы закрашена целиком: снизу не остается незаполненной полосы. Так
    /// выглядел лист, «обрезанный наполовину», — холст строился по размерам страницы, а байт
    /// приходило меньше, и хвост оставался нетронутым.
    /// </summary>
    [Fact]
    public void A_rendered_page_has_no_untouched_tail()
    {
        using var source = DocPageSource.Open(WritePdf("целый.pdf"));
        Assert.NotNull(source);

        var png = source!.Render(0, 400);
        Assert.NotNull(png);

        using var bitmap = SKBitmap.Decode(png);
        Assert.True(bitmap.Height > 10, "страница вышла слишком низкой для проверки");

        // Нижняя строка листа должна быть непрозрачной: незакрашенный холст — прозрачный.
        for (var x = 0; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 20))
            Assert.True(bitmap.GetPixel(x, bitmap.Height - 1).Alpha > 0,
                        $"низ страницы не закрашен в точке {x}");
    }

    [Fact]
    public void Pdf_opens_with_its_pages_and_sizes()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf", pages: 3));

        Assert.NotNull(source);
        Assert.Equal(3, source!.PageCount);
        Assert.Equal(595, source.Size(0).Width,  0);
        Assert.Equal(842, source.Size(0).Height, 0);
    }

    [Theory]
    [InlineData("текст.txt")]
    [InlineData("нет-такого.pdf")]
    public void Anything_but_a_readable_pdf_opens_as_null(string name)
    {
        var path = Path.Combine(_dir, name);
        if (name.EndsWith(".txt", StringComparison.Ordinal)) File.WriteAllText(path, "не документ");

        // Для панели это не сбой: режим «как в документе» просто останется недоступным.
        Assert.Null(DocPageSource.Open(path));
    }

    /// <summary>
    /// Том толще потолка показывается не весь, но знает, сколько в нем страниц: иначе панель
    /// не сказала бы «показаны страницы 1–300 из 302» и оборвала бы том молча.
    /// </summary>
    [Fact]
    public void A_tome_past_the_cap_knows_its_full_size()
    {
        using var source = DocPageSource.Open(WritePdf("tome.pdf", DocPageSource.MaxPages + 2));

        Assert.NotNull(source);
        Assert.Equal(DocPageSource.MaxPages, source.PageCount);
        Assert.Equal(DocPageSource.MaxPages + 2, source.TotalPages);
    }

    [Fact]
    public void A_short_document_is_shown_whole()
    {
        using var source = DocPageSource.Open(WritePdf("short.pdf", 3));

        Assert.NotNull(source);
        Assert.Equal(3, source.PageCount);
        Assert.Equal(3, source.TotalPages);
    }

    /// <summary>
    /// Открытие тома отменяется между страницами: панель открыла другой документ, и дочитывать
    /// прежний незачем. Отмененный документ отпускает файл сразу — его могут захотеть
    /// переименовать или удалить.
    /// </summary>
    [Fact]
    public void Cancelled_opening_throws_and_lets_the_file_go()
    {
        var path = WritePdf("tome.pdf", pages: 5);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => DocPageSource.Open(path, cts.Token));

        File.Delete(path);
        Assert.False(File.Exists(path), "отмененное открытие держит файл");
    }

    [Fact]
    public void Broken_pdf_opens_as_null()
    {
        var path = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(path, "%PDF-1.4 это не документ");

        Assert.Null(DocPageSource.Open(path));
    }

    // ── Картинка страницы ─────────────────────────────────────────────────────

    [Fact]
    public void Page_renders_at_the_asked_width()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));
        var png = source!.Render(0, 400);

        Assert.NotNull(png);
        using var image = SKBitmap.Decode(png);
        // PDFium округляет вписывание, поэтому ширина может отличаться на пиксель — но не более.
        Assert.InRange(image.Width, 399, 400);
        // Пропорции листа сохранены.
        Assert.Equal(842.0 / 595, (double)image.Height / image.Width, 2);
    }

    /// <summary>
    /// Альбомная страница обязана нарисоваться. У миниатюр этот случай ронял отрисовку
    /// исключением, которое глотал catch, и документы молча оставались без картинки.
    /// </summary>
    [Fact]
    public void Landscape_page_renders_too()
    {
        using var source = DocPageSource.Open(WritePdf("land.pdf", width: 842, height: 595));
        var png = source!.Render(0, 400);

        Assert.NotNull(png);
        using var image = SKBitmap.Decode(png);
        Assert.True(image.Width > image.Height, "альбомная страница нарисована как книжная");
    }

    /// <summary>
    /// Отрисовка, которую отменили, пока она ждала очереди, не рисует ничего: ширина или
    /// документ уже другие. Сама страница при этом цела и рисуется следующим вызовом.
    /// </summary>
    [Fact]
    public void Cancelled_render_throws_before_drawing()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => source!.Render(0, 400, cts.Token));
        Assert.NotNull(source!.Render(0, 400));
    }

    [Fact]
    public void Render_of_a_page_that_is_not_there_gives_null()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));

        Assert.Null(source!.Render(-1, 400));
        Assert.Null(source.Render(99, 400));
    }

    // ── Слова и их места ──────────────────────────────────────────────────────

    [Fact]
    public void Words_come_with_their_boxes()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf", pages: 2));

        var words = source!.Words(1);
        Assert.Contains(words, w => w.Text.Contains("Dogovor2", StringComparison.Ordinal));

        var word = words.First(w => w.Text.Contains("Dogovor2", StringComparison.Ordinal));
        Assert.True(word.Box.Width  > 0, "у слова нулевая ширина");
        Assert.True(word.Box.Height > 0, "у слова нулевая высота");
    }

    /// <summary>
    /// Слово, написанное в 40 пунктах от левого края и в 60 от ВЕРХНЕГО, обязано оказаться в
    /// левом верхнем углу показанной страницы. Это и есть проверка согласия двух разборщиков:
    /// координаты дает PdfPig, размер картинки — PDFium, а сводит их
    /// <see cref="DocPageGeometry"/>.
    /// </summary>
    [Fact]
    public void Word_box_lands_where_the_word_is_drawn()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));
        var size = source!.Size(0);
        var word = source.Words(0).First(w => w.Text.Contains("Dogovor", StringComparison.Ordinal));

        const double DisplayWidth = 595;   // показываем один к одному с пунктами
        var scale  = DocPageGeometry.ScaleFor(DisplayWidth, size.Width);
        var screen = DocPageGeometry.ToScreen(word.Box, size.Height, scale);

        Assert.InRange(screen.X, 35, 50);   // 40 пунктов от левого края
        Assert.InRange(screen.Y, 45, 75);   // 60 пунктов от верхнего
        Assert.True(screen.Y < size.Height / 3, "слово из шапки уехало в середину листа");
    }

    [Fact]
    public void Words_of_a_page_that_is_not_there_are_empty()
    {
        using var source = DocPageSource.Open(WritePdf("doc.pdf"));

        Assert.Empty(source!.Words(-1));
        Assert.Empty(source.Words(42));
    }

    // ── Повернутый лист ───────────────────────────────────────────────────────

    /// <summary>
    /// Слово на повернутом листе подсвечивается там, где его нарисовал PDFium. Случаев два:
    /// содержимое повернуто навстречу листу (обычный альбомный лист — текст на экране стоит
    /// прямо) и лист повернут без поправки (текст идет боком или вверх ногами).
    ///
    /// Место сверяется с ПИКСЕЛЯМИ: края прямоугольника от PdfPig, переведенного в экран, обязаны
    /// совпасть с краями букв на картинке от PDFium. Поворот, понятый не так, увел бы
    /// прямоугольник на сотни пикселей — на другую сторону листа.
    /// </summary>
    [Theory]
    [InlineData(0,   true)]
    [InlineData(90,  true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    [InlineData(90,  false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    public void Word_on_a_turned_sheet_lands_where_it_is_drawn(int rotate, bool upright)
    {
        var content = upright ? HandPdf.Upright(rotate, HandPdf.Full, 40, 60, "Dogovor")
                              : HandPdf.Plain(40, 782, "Dogovor");
        var path = HandPdf.Write(_dir, $"поворот-{rotate}.pdf", new HandPdf.Sheet(content, rotate));

        AssertLandsOnInk(path, "Dogovor");
    }

    /// <summary>
    /// Лист обрезан не от нуля, да еще и повернут: место слова сдвигается вместе с обрезкой.
    /// Иначе подсветка уехала бы ровно на сдвиг обрезки.
    /// </summary>
    [Fact]
    public void Word_on_a_cropped_turned_sheet_lands_where_it_is_drawn()
    {
        var crop = new HandPdf.Crop(100, 100, 500, 700);
        var path = HandPdf.Write(_dir, "обрезка.pdf",
                                 new HandPdf.Sheet(HandPdf.Upright(90, crop, 40, 60, "Dogovor"), 90, crop));

        AssertLandsOnInk(path, "Dogovor");
    }

    /// <summary>
    /// Текст, идущий боком или вверх ногами, приходит ЦЕЛЫМИ словами и в своем порядке.
    /// Разборщик PdfPig по умолчанию читает лист строками сверху вниз: такой текст он резал на
    /// буквы, а перевернутый еще и переставлял — поиск по странице его не находил, хотя режим
    /// «Текст» и индекс находили.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Sideways_text_comes_in_whole_words(int rotate)
    {
        var path = HandPdf.Write(_dir, $"боком-{rotate}.pdf",
                                 new HandPdf.Sheet(HandPdf.Plain(40, 782, "Dogovor postavki 21.501"), rotate));

        using var source = DocPageSource.Open(path);
        Assert.Equal("Dogovor | postavki | 21.501", string.Join(" | ", source!.Words(0).Select(w => w.Text)));
    }

    /// <summary>
    /// Надписи штампа на полях чертежа идут снизу вверх на обычном, неповернутом листе. Они
    /// находятся поиском по странице, а строки, идущие как обычно, остаются прежними словами.
    /// </summary>
    [Fact]
    public void Stamp_on_the_margin_is_found_on_the_page()
    {
        var path = HandPdf.Write(_dir, "штамп.pdf", new HandPdf.Sheet(
            HandPdf.Plain(40, 782, "Dogovor postavki") + "\n" + HandPdf.Rising(30, 100, "Inv podl 4711")));

        using var source = DocPageSource.Open(path);
        var words = source!.Words(0);
        Assert.Equal("Dogovor | postavki | Inv | podl | 4711", string.Join(" | ", words.Select(w => w.Text)));

        var hit = Assert.Single(DocHighlight.FindInWords([.. words.Select(w => w.Text)], DocHighlight.Tokens("podl")));
        Assert.Equal("podl", words[hit.First].Text);
    }

    /// <summary>
    /// Слово, написанное наискось, — водяной знак «КОПИЯ» поперек листа — подсветка накрывает
    /// целиком. Прямоугольник такого слова у PdfPig повернут, а его Left, Right, Top и Bottom верны
    /// только у неповернутого: по ним подсветка вышла бы вдвое ниже слова и накрыла бы его
    /// половину. Охватывающий прямоугольник наклонного слова выше самих букв — в нем место под
    /// выносные элементы, — поэтому проверка здесь не «впритык», а «накрыто целиком, и лишнее
    /// поле вокруг не шире десяти пикселей».
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Slanted_word_is_covered_whole(int rotate)
    {
        var path = HandPdf.Write(_dir, $"наискось-{rotate}.pdf",
                                 new HandPdf.Sheet(HandPdf.Slanted(200, 400, 30, "Kopiya"), rotate));

        var (word, screen, ink) = Measure(path);
        Assert.Equal("Kopiya", word.Text);

        // Пиксель на сглаженный край буквы.
        var why = Describe(screen, ink);
        Assert.True(screen.X <= ink.Left + 1 && screen.Y <= ink.Top + 1, why);
        Assert.True(screen.X + screen.Width >= ink.Right - 1 && screen.Y + screen.Height >= ink.Bottom - 1, why);

        Assert.True(ink.Left - screen.X <= 10 && ink.Top - screen.Y <= 10, why);
        Assert.True(screen.X + screen.Width - ink.Right <= 10 && screen.Y + screen.Height - ink.Bottom <= 10, why);
    }

    /// <summary>
    /// Единственное слово листа лежит там, где PDFium нарисовал буквы: каждый край прямоугольника
    /// — в трех пикселях от края краски. Допуск нужен потому, что край буквы сглажен, а
    /// прямоугольник PdfPig строится по метрикам шрифта, а не по самой краске.
    /// </summary>
    private static void AssertLandsOnInk(string path, string text)
    {
        var (word, screen, ink) = Measure(path);
        Assert.Equal(text, word.Text);

        var why = Describe(screen, ink);
        Assert.True(Math.Abs(screen.X - ink.Left) <= 3, why);
        Assert.True(Math.Abs(screen.Y - ink.Top)  <= 3, why);
        Assert.True(Math.Abs(screen.X + screen.Width  - ink.Right)  <= 3, why);
        Assert.True(Math.Abs(screen.Y + screen.Height - ink.Bottom) <= 3, why);
    }

    /// <summary>
    /// Единственное слово листа, его прямоугольник на экране и края краски на картинке, которую
    /// нарисовал PDFium.
    /// </summary>
    private static (DocWord Word, DocScreenBox Screen, (int Left, int Top, int Right, int Bottom) Ink) Measure(string path)
    {
        using var source = DocPageSource.Open(path);
        Assert.NotNull(source);

        var word = Assert.Single(source!.Words(0));

        var png = source.Render(0, 400);
        Assert.NotNull(png);
        using var bitmap = SKBitmap.Decode(png);

        var size   = source.Size(0);
        var screen = DocPageGeometry.ToScreen(word.Box, size.Height,
                                              DocPageGeometry.ScaleFor(bitmap.Width, size.Width));
        return (word, screen, Ink(bitmap));
    }

    private static string Describe(DocScreenBox screen, (int Left, int Top, int Right, int Bottom) ink) =>
        $"слово ({screen.X:F0}, {screen.Y:F0}) – ({screen.X + screen.Width:F0}, {screen.Y + screen.Height:F0}), " +
        $"буквы ({ink.Left}, {ink.Top}) – ({ink.Right}, {ink.Bottom})";

    /// <summary>Края краски на картинке: крайние темные пиксели, правый и нижний — не включая.</summary>
    private static (int Left, int Top, int Right, int Bottom) Ink(SKBitmap bitmap)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if ((pixel.Red * 299 + pixel.Green * 587 + pixel.Blue * 114) / 1000 > 128) continue;

            left   = Math.Min(left, x);
            top    = Math.Min(top, y);
            right  = Math.Max(right, x + 1);
            bottom = Math.Max(bottom, y + 1);
        }

        Assert.True(right > 0, "на листе не нарисовано ни одной буквы");
        return (left, top, right, bottom);
    }

    /// <summary>
    /// После закрытия сеанс молчит, а не падает: панель закрывают в любой момент, в том числе
    /// пока фоновая задача рисует страницу.
    /// </summary>
    [Fact]
    public void Closed_source_answers_with_nothing()
    {
        var source = DocPageSource.Open(WritePdf("doc.pdf"));
        source!.Dispose();

        Assert.Null(source.Render(0, 400));
        Assert.Empty(source.Words(0));
        source.Dispose();   // повторное закрытие — тоже не беда
    }
}
