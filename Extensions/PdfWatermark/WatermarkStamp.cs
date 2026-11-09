using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using SmartBase.Core.Documents;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>Оформление знака. Записью, а не константами: правится в одном месте и подставляется
/// в тестах.</summary>
/// <param name="Alpha">Непрозрачность заливки букв, 0-255. 125 — примерно 49%: узкие
/// полужирные буквы сплошные и читаемые, без светлой пустоты внутри контура.</param>
/// <param name="OutlineAlpha">Непрозрачность КОНТУРА букв. Контур — это стойкость знака: светлую
/// заливку сгоняет одна операция «уровни» в любом бесплатном редакторе, а контур по плотности
/// сравним с текстом документа, и порог, который уберет его, испортит и сам документ. Тонкая
/// линия читать не мешает.</param>
/// <param name="OutlineShare">Толщина контура в долях кегля. 0.035 держит компактные буквы
/// после уменьшения страницы и осветления изображения.</param>
/// <param name="LineSpacing">Межстрочный шаг в долях кегля.</param>
/// <param name="MinFontPt">Минимальный читаемый кегль. При переполнении блока нужен явный отказ.</param>
/// <param name="MaxFontPt">Обычный кегль компактного блока. Заголовок «КОПИЯ» крупнее в 1.35 раза.</param>
public sealed record StampLook(
    byte   Alpha         = 125,
    byte   OutlineAlpha  = 168,
    double OutlineShare  = 0.035,
    byte   R             = 90,
    byte   G             = 90,
    byte   B             = 90,
    double LineSpacing   = 1.15,
    double MinFontPt     = 10,
    double MaxFontPt     = 18);

/// <summary>
/// Наложение диагональной надписи на готовый PDF: блок строк по центру листа, снизу вверх вдоль
/// диагонали, серым полупрозрачным — знак виден, а документ под ним читается.
///
/// <b>Знак ВПЕЧАТЫВАЕТСЯ в пиксели страницы.</b> Страница переводится в растр (PDFium через
/// Docnet.Core), надпись рисуется по пикселям (Skia), и результат кладется в новый документ
/// картинкой. Так сделано ради единственного свойства: знак перестает быть отдельным объектом.
/// Прежний способ — дописать надпись в поток содержимого — снимался одной операцией: у страницы
/// становилось два потока /Contents, и достаточно было выбросить последний. Текст знака вдобавок
/// доставался обычным копированием.
///
/// Цена честная и сказана пользователю прямым текстом: у выданного документа НЕ ОСТАЕТСЯ
/// текстового слоя — ни поиска, ни выделения, — а электронная подпись, если она была,
/// недействительна (документ пересобирается заново).
///
/// Полностью неудаляемым знак не становится: пиксели можно заретушировать. Но ретушь идет по
/// тем же пикселям, где лежит текст документа, поэтому следы видны, а пакетно на сотню файлов
/// это уже не делается за секунды.
/// </summary>
public static class WatermarkStamp
{
    public static readonly StampLook Default = new();

    /// <summary>
    /// Открыть документ. Отдельно от <see cref="StampAndSave"/> затем, что прогон делает этим
    /// ПРЕДПОЛЕТ: файл, который не открывается, не должен оставить за собой заведенную и тут же
    /// удаленную строку журнала. Открытый документ нужен и дальше — по нему считаются размеры
    /// страниц и родное разрешение сканов.
    /// </summary>
    /// <exception cref="PdfWatermarkException">Файл защищен паролем, поврежден, занят или
    /// недоступен.</exception>
    public static PdfSharp.Pdf.PdfDocument Open(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex)
        {
            // Пароль называем отдельно — его хотя бы можно снять в исходной программе. Все
            // прочее сводится к одному тексту: различие «битый» и «не PDF» человеку ничего не
            // подсказывает, делать с файлом на этом прогоне все равно нечего.
            throw new PdfWatermarkException(
                ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                    ? "документ защищен паролем"
                    : "файл не является PDF или поврежден", ex);
        }
        catch (UnauthorizedAccessException ex) { throw new PdfWatermarkException("нет доступа к файлу", ex); }
        catch (IOException ex)                 { throw new PdfWatermarkException("файл занят другой программой", ex); }
        catch (Exception ex)                   { throw new PdfWatermarkException("файл не является PDF или поврежден", ex); }
    }

    /// <summary>
    /// Впечатать знак в каждую страницу документа и записать результат в
    /// <paramref name="outputPath"/>. Возвращает число страниц.
    ///
    /// <paramref name="source"/> — путь исходного файла: растеризатор читает файл сам, по пути, а
    /// не по уже открытому документу. <paramref name="doc"/> — тот же файл, открытый предполетом:
    /// из него берутся размеры страниц и родное разрешение картинок.
    ///
    /// Исходный файл не изменяется. Недописанный результат при сбое или отмене удаляется — иначе
    /// на диске остался бы обрезанный PDF, выглядящий как готовый документ.
    /// </summary>
    /// <exception cref="PdfWatermarkException">Документ не растеризуется или результат не
    /// записался.</exception>
    /// <exception cref="OperationCanceledException">Прогон отменен.</exception>
    /// <exception cref="ArgumentException">Строк надписи нет — документ без знака выдавать
    /// нельзя, а молча сохранить копию значило бы именно это.</exception>
    public static int StampAndSave(string source, PdfSharp.Pdf.PdfDocument doc, string outputPath,
                                   IReadOnlyList<string> lines,
                                   CancellationToken ct, StampLook? look = null, StampLayout? layout = null)
    {
        if (lines.Count == 0 || lines.All(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Знак без текста", nameof(lines));

        var style = look ?? Default;
        var placement = layout ?? StampLayout.Default;
        placement.Validate();
        var scale = PageRaster.ScaleFor(doc);

        using var output = new PdfSharp.Pdf.PdfDocument();

        // Потоки JPEG живут до самого Save: PDFsharp читает картинку не в момент DrawImage, а
        // при записи документа. Отпустить их раньше — получить пустые страницы.
        var pages = new List<MemoryStream>(doc.PageCount);

        try
        {
            // DocLib.Instance — синглтон PDFium на процесс, и он НЕ потокобезопасен. Сам по себе
            // прогон однопоточный, но в программе теперь рисуют страницы еще двое — миниатюры
            // ленты библиотеки и ее панель чтения, — поэтому каждый вызов в PDFium идет через
            // общую очередь PdfGate. Очередь берется на КОРОТКИЕ операции, а не на весь прогон:
            // документ на сотню страниц иначе заморозил бы ленту на все время штамповки.
            //
            // Закрытие документа и страницы — такие же вызовы в PDFium. Пока их закрывал using,
            // они шли уже после замка, мимо очереди, и сталкивались с отрисовкой миниатюры в
            // другом потоке. Поэтому страница целиком — открыть, нарисовать, измерить, закрыть —
            // одна короткая операция под замком, а документ закрывается через очередь в finally.
            var reader = PdfGate.Run(() => DocLib.Instance.GetDocReader(source, new PageDimensions(scale)));
            try
            {
                var count = PdfGate.Run(reader.GetPageCount);

                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var index = i;
                    var (jpeg, width, height) = PdfGate.Run(() =>
                    {
                        using var page = reader.GetPageReader(index);
                        return (StampPage(page, lines, style, placement, scale * 72 / 25.4),
                                page.GetPageWidth(), page.GetPageHeight());
                    });

                    pages.Add(jpeg);

                    var sheet = output.AddPage();
                    // Размер листа берется у ИСХОДНОЙ страницы, а не у растра: печать и поля
                    // должны остаться теми же, сколько бы пикселей ни было в картинке.
                    sheet.Width  = XUnit.FromPoint(width  / scale);
                    sheet.Height = XUnit.FromPoint(height / scale);

                    using var gfx    = XGraphics.FromPdfPage(sheet);
                    using var picture = XImage.FromStream(jpeg);
                    gfx.DrawImage(picture, 0, 0, sheet.Width.Point, sheet.Height.Point);
                }
            }
            finally
            {
                PdfGate.Run(reader.Dispose);
            }

            // Число страниц берется ДО записи: после Save документ PDFsharp считает закрытым и
            // на обращение к своим свойствам отвечает исключением.
            var written = output.PageCount;
            Save(output, outputPath);
            return written;
        }
        catch (OperationCanceledException)
        {
            TryRemove(outputPath);
            throw;
        }
        catch (PdfWatermarkException)
        {
            TryRemove(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            TryRemove(outputPath);
            throw new PdfWatermarkException("документ не удалось перевести в изображение", ex);
        }
        finally
        {
            foreach (var stream in pages) stream.Dispose();
        }
    }

    /// <summary>Отрисовать знак на одной странице и вернуть готовый JPEG.</summary>
    private static MemoryStream StampPage(Docnet.Core.Readers.IPageReader page,
                                          IReadOnlyList<string> lines, StampLook look,
                                          StampLayout layout, double pixelsPerMm)
    {
        var w = page.GetPageWidth();
        var h = page.GetPageHeight();

        // PDFium отдает страницу как BGRA-массив, и незакрашенные места в нем ПРОЗРАЧНЫ, а не
        // белы. Без подмены фона обычный текстовый лист (где закрашены только буквы) уехал бы в
        // JPEG черным прямоугольником: прозрачности этот формат не знает.
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var raw = page.GetImage(new NaiveTransparencyRemover(255, 255, 255));
        System.Runtime.InteropServices.Marshal.Copy(raw, 0, bitmap.GetPixels(), raw.Length);

        using (var canvas = new SKCanvas(bitmap))
            Draw(canvas, w, h, lines, look, layout, pixelsPerMm);

        using var image = SKImage.FromBitmap(bitmap);
        using var data  = image.Encode(SKEncodedImageFormat.Jpeg, PageRaster.JpegQuality)
                          ?? throw new PdfWatermarkException("страница не кодируется в изображение");

        var jpeg = new MemoryStream((int)data.Size);
        data.SaveTo(jpeg);
        jpeg.Position = 0;
        return jpeg;
    }

    /// <summary>
    /// Нарисовать знак на холсте <paramref name="width"/>×<paramref name="height"/>. Отдельным
    /// методом — чтобы тесты проверяли саму отрисовку, без PDF вокруг нее.
    ///
    /// Поворот страницы (<c>/Rotate</c>) здесь уже учтен: PDFium растеризует страницу такой,
    /// какой ее видит читатель, поэтому холст всегда «как на экране», и доворачивать ничего не
    /// нужно — в отличие от прежнего пути через PDFsharp, где холст был сырым MediaBox.
    /// </summary>
    public static void Draw(SKCanvas canvas, int width, int height,
                            IReadOnlyList<string> lines, StampLook look,
                            StampLayout? layout = null, double? pixelsPerMm = null)
    {
        // Для отдельного холста без PDF короткая сторона условно равна A4. Настоящий прогон
        // всегда передает масштаб растеризатора: миллиметры не зависят от dpi и формата листа.
        var scale = pixelsPerMm ?? Math.Min(width, height) / 210d;
        var grid = (layout ?? StampLayout.Default).Plan(width, height, scale);
        var block = StampTextLayout.Compose(lines, grid.Width, grid.Height, scale, look);

        using var fill = new SKPaint
        {
            Color       = new SKColor(look.R, look.G, look.B, look.Alpha),
            IsAntialias = true,
            Typeface    = WatermarkFont.Typeface,
            BlendMode   = SKBlendMode.Multiply,
        };

        // Умножение оставляет черные штрихи документа черными: светлая надпись не выбеливает
        // исходный текст. Контур дополняет плотную заливку при осветлении картинки.
        using var outline = new SKPaint
        {
            Color       = new SKColor(look.R, look.G, look.B, look.OutlineAlpha),
            IsAntialias = true,
            Typeface    = WatermarkFont.Typeface,
            BlendMode   = SKBlendMode.Multiply,
            Style       = SKPaintStyle.Stroke,
        };

        canvas.Save();
        canvas.ClipRect(new SKRect((float)grid.Margin, (float)grid.Margin,
                                   width - (float)grid.Margin, height - (float)grid.Margin));
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees((float)grid.Angle);
        foreach (var center in grid.Centers)
        {
            canvas.Save();
            canvas.Translate((float)center.X, (float)center.Y);
            foreach (var line in block.Lines)
            {
                fill.TextSize = outline.TextSize = line.Size;
                outline.StrokeWidth = Math.Max((float)(scale * 0.05), line.Size * (float)look.OutlineShare);
                var x = -fill.MeasureText(line.Text) / 2;
                var y = line.Baseline - (float)block.Height / 2;
                canvas.DrawText(line.Text, x, y, fill);
                canvas.DrawText(line.Text, x, y, outline);
            }
            canvas.Restore();
        }

        canvas.Restore();
    }

    /// <summary>Длина диагонали листа.</summary>
    public static double Diagonal(double width, double height) =>
        Math.Sqrt(width * width + height * height);

    /// <summary>
    /// Угол надписи в градусах. Ось Y холста направлена ВНИЗ, поэтому положительный угол — по
    /// часовой стрелке, а надпись «снизу вверх слева направо» получается отрицательным углом
    /// диагонали. Чистая функция — покрывается тестом без единой картинки.
    /// </summary>
    public static double AngleFor(double width, double height) =>
        -Math.Atan2(height, width) * 180 / Math.PI;

    private static void Save(PdfSharp.Pdf.PdfDocument doc, string path)
    {
        try
        {
            doc.Save(path);
        }
        catch (UnauthorizedAccessException ex) { throw new PdfWatermarkException("нет доступа к папке результата", ex); }
        catch (PathTooLongException ex)        { throw new PdfWatermarkException("слишком длинный путь результата", ex); }
        catch (IOException ex)                 { throw new PdfWatermarkException($"не удалось записать результат: {ex.Message}", ex); }
    }

    private static void TryRemove(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
