using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>Оформление знака. Записью, а не константами: правится в одном месте и подставляется
/// в тестах.</summary>
/// <param name="Alpha">Непрозрачность ЗАЛИВКИ букв, 0-255. 71 — примерно 28%: знак хорошо виден,
/// но текст документа под ним читается.</param>
/// <param name="OutlineAlpha">Непрозрачность КОНТУРА букв. Контур — это стойкость знака: светлую
/// заливку сгоняет одна операция «уровни» в любом бесплатном редакторе, а контур по плотности
/// сравним с текстом документа, и порог, который уберет его, испортит и сам документ. Тонкая
/// линия читать не мешает.</param>
/// <param name="OutlineShare">Толщина контура в долях кегля. 0.022 — примерно 1 пиксель на
/// каждые 45 пикселей высоты буквы.</param>
/// <param name="DiagonalShare">Какую долю диагонали листа занимает БЛОК надписи целиком. 0.86 —
/// чтобы не упирался в углы.</param>
/// <param name="LineSpacing">Межстрочный шаг в долях кегля.</param>
/// <param name="MinFontPt">Нижний упор кегля: длинная надпись на A5 не должна стать нечитаемой.</param>
/// <param name="MaxFontPt">Верхний упор — защита от вырожденного случая (все строки пустые), а
/// не рабочее ограничение: на больших форматах знак обязан расти вместе с листом.</param>
public sealed record StampLook(
    byte   Alpha         = 71,
    byte   OutlineAlpha  = 168,
    double OutlineShare  = 0.022,
    byte   R             = 90,
    byte   G             = 90,
    byte   B             = 90,
    double DiagonalShare = 0.86,
    double LineSpacing   = 1.3,
    double MinFontPt     = 5,
    double MaxFontPt     = 160);

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
                                   CancellationToken ct, StampLook? look = null)
    {
        if (lines.Count == 0) throw new ArgumentException("Знак без текста", nameof(lines));

        var style = look ?? Default;
        var scale = PageRaster.ScaleFor(doc);

        using var output = new PdfSharp.Pdf.PdfDocument();

        // Потоки JPEG живут до самого Save: PDFsharp читает картинку не в момент DrawImage, а
        // при записи документа. Отпустить их раньше — получить пустые страницы.
        var pages = new List<MemoryStream>(doc.PageCount);

        try
        {
            // DocLib.Instance — синглтон PDFium на процесс, и он НЕ потокобезопасен. Прогон
            // однопоточный, поэтому хватает того, что мы не открываем два документа сразу.
            using (var reader = DocLib.Instance.GetDocReader(source, new PageDimensions(scale)))
            {
                var count = reader.GetPageCount();

                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var page = reader.GetPageReader(i);
                    var jpeg = StampPage(page, lines, style);

                    pages.Add(jpeg);

                    var sheet = output.AddPage();
                    // Размер листа берется у ИСХОДНОЙ страницы, а не у растра: печать и поля
                    // должны остаться теми же, сколько бы пикселей ни было в картинке.
                    sheet.Width  = XUnit.FromPoint(page.GetPageWidth()  / scale);
                    sheet.Height = XUnit.FromPoint(page.GetPageHeight() / scale);

                    using var gfx    = XGraphics.FromPdfPage(sheet);
                    using var picture = XImage.FromStream(jpeg);
                    gfx.DrawImage(picture, 0, 0, sheet.Width.Point, sheet.Height.Point);
                }
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
                                          IReadOnlyList<string> lines, StampLook look)
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
            Draw(canvas, w, h, lines, look);

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
                            IReadOnlyList<string> lines, StampLook look)
    {
        var size = (float)FontSizeFor(lines, width, height, look);

        using var fill = new SKPaint
        {
            Color       = new SKColor(look.R, look.G, look.B, look.Alpha),
            IsAntialias = true,
            Typeface    = WatermarkFont.Typeface,
            TextSize    = size,
        };

        // Контур рисуется ПОВЕРХ заливки и держит знак при чистке: заливку в 28% сгоняет одна
        // операция «уровни», а эту линию — уже нет, она по плотности как текст документа.
        using var outline = new SKPaint
        {
            Color       = new SKColor(look.R, look.G, look.B, look.OutlineAlpha),
            IsAntialias = true,
            Typeface    = WatermarkFont.Typeface,
            TextSize    = size,
            Style       = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, size * (float)look.OutlineShare),
        };

        canvas.Save();
        canvas.Translate(width / 2f, height / 2f);
        canvas.RotateDegrees((float)AngleFor(width, height));

        var step = size * (float)look.LineSpacing;
        var top  = -step * lines.Count / 2f;

        for (var i = 0; i < lines.Count; i++)
        {
            // Skia ставит текст по БАЗОВОЙ линии, поэтому строка опускается внутри своей полосы
            // на 0.7 шага — так блок оказывается зрительно посередине.
            var line = fill.MeasureText(lines[i]);
            var x    = -line / 2f;
            var y    = top + i * step + step * 0.7f;

            canvas.DrawText(lines[i], x, y, fill);
            canvas.DrawText(lines[i], x, y, outline);
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

    /// <summary>
    /// Кегль, при котором БЛОК строк, положенный по диагонали, занимает
    /// <see cref="StampLook.DiagonalShare"/> листа.
    ///
    /// Блок шириной W и высотой H, повернутый на угол диагонали, вписан в лист w×h ровно при
    /// <c>W + H·max(w/h, h/w) ≤ d</c> (габарит повернутого прямоугольника, где косинус и синус
    /// угла — это w/d и h/d).
    ///
    /// И ширина, и высота блока строго ЛИНЕЙНЫ по кеглю, поэтому подбор — одно измерение на
    /// пробном кегле и деление, а не итерации.
    /// </summary>
    public static double FontSizeFor(IReadOnlyList<string> lines, double width, double height,
                                     StampLook look)
    {
        const float probe = 20;

        using var ruler = new SKPaint { Typeface = WatermarkFont.Typeface, TextSize = probe };

        var wide = lines.Count == 0 ? 0 : lines.Max(line => ruler.MeasureText(line));
        var tall = probe * look.LineSpacing * lines.Count;

        var slim = Math.Max(width / height, height / width);
        var room = wide + tall * slim;

        return room > 0
            ? Math.Clamp(probe * look.DiagonalShare * Diagonal(width, height) / room,
                         look.MinFontPt, look.MaxFontPt)
            : look.MinFontPt;
    }

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
