using Docnet.Core;
using Docnet.Core.Models;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using UglyToad.PdfPig.Writer;

// Читатель у нас PdfPig, а ключ /Rotate ставит PDFsharp — типы с одним именем приходят из
// обоих пакетов, поэтому читающий назван явно.
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>
/// Настоящие PDF для сквозных тестов и чтение результата обратно. Бинарных фикстур в
/// репозитории нет — все собирается в тесте.
///
/// Исходные документы пишет PdfPig (<see cref="PdfDocumentBuilder"/>), а читает результат тоже
/// он: это ВТОРОЙ, независимый разборщик. Проверять работу PDFsharp тем же PDFsharp — значит
/// проверять, что библиотека согласна сама с собой; знак же обязан быть виден любому читателю.
///
/// Стенд — копия приема из Tests/Extensions/PdfAnalyzer/PdfStand.cs, а не общий файл: стенд
/// знает про свое расширение и обязан уехать вместе с ним (см. testing/extension-tests.md).
/// </summary>
internal static class PdfStand
{
    // Форматы по ГОСТ 2.301 в миллиметрах (ширина × высота).
    public static readonly (double W, double H) A4     = (210, 297);
    public static readonly (double W, double H) A3     = (297, 420);
    public static readonly (double W, double H) A1     = (594, 841);
    public static readonly (double W, double H) A4Land = (297, 210);   // альбомный A4
    public static readonly (double W, double H) Square = (300, 300);   // квадрат: угол ровно 45°

    private const double MmToPt = 72.0 / 25.4;

    /// <summary>Записать PDF с заданными страницами.</summary>
    public static void Write(string path, params (double W, double H)[] pages)
    {
        using var builder = new PdfDocumentBuilder();
        foreach (var (w, h) in pages)
            builder.AddPage(w * MmToPt, h * MmToPt);
        File.WriteAllBytes(path, builder.Build());
    }

    /// <summary>Одностраничный A4 — самый частый случай в тестах.</summary>
    public static void WriteA4(string path) => Write(path, A4);

    /// <summary>
    /// Страницы с ключом <c>/Rotate</c>: просмотрщик доворачивает такую страницу при показе.
    /// Ключ ставится уже PDFsharp — у писателя PdfPig этого в API нет.
    /// </summary>
    public static void WriteRotated(string path, int rotate, params (double W, double H)[] pages)
    {
        Write(path, pages);

        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        foreach (var page in doc.Pages) page.Rotate = rotate;
        doc.Save(path);
    }

    /// <summary>
    /// Документ СТАРОЙ версии PDF. 1.3 — не экзотика: столько ставят сканеры и «Файлы» на iOS, а
    /// прозрачности в 1.3 еще не существует, и знак на таком документе получался глухим.
    /// </summary>
    public static void WriteOldVersion(string path, int version = 13, params (double W, double H)[] pages)
    {
        Write(path, pages.Length > 0 ? pages : [A4]);

        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        doc.Version = version;
        doc.Save(path);
    }

    /// <summary>Версия PDF из заголовка файла: «1.3», «1.4».</summary>
    public static string VersionOf(string path)
    {
        using var file = File.OpenRead(path);
        var head = new byte[8];   // «%PDF-1.3»
        file.ReadExactly(head);
        return System.Text.Encoding.ASCII.GetString(head)[5..];
    }

    /// <summary>Документ, закрытый паролем на открытие.</summary>
    public static void WriteProtected(string path, string password = "секрет")
    {
        Write(path, A4);

        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = password;
        doc.Save(path);
    }

    /// <summary>Файл с расширением .pdf, который PDF не является.</summary>
    public static void WriteBroken(string path) => File.WriteAllText(path, "%PDF-1.4 это не документ");

    /// <summary>Число страниц готового документа — глазами PdfPig.</summary>
    public static int PageCount(string path)
    {
        using var doc = PigDocument.Open(path);
        return doc.NumberOfPages;
    }

    /// <summary>
    /// Текст страницы (1-индексная) в порядке потока содержимого. Именно так проверяется шрифт:
    /// чтобы кириллица вернулась, PDFsharp обязан положить в документ и подмножество очертаний,
    /// и карту ToUnicode.
    ///
    /// Берутся <c>Letters</c>, а не <c>GetWords()</c>: разбиение на слова у PdfPig geometric —
    /// оно сортирует буквы по координатам и на ПОВЕРНУТОЙ строке выдает их задом наперед и
    /// поодиночке. Нам же нужно, что именно записано, а не как оно разложено на листе.
    /// </summary>
    public static string TextOf(string path, int pageNumber = 1)
    {
        using var doc = PigDocument.Open(path);
        return string.Concat(doc.GetPage(pageNumber).Letters.Select(l => l.Value));
    }

    /// <summary>Размер страницы в пунктах — печать и поля обязаны пережить растеризацию.</summary>
    public static (double W, double H) PageSize(string path, int pageNumber = 1)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
        var page = doc.Pages[pageNumber - 1];
        return (page.Width.Point, page.Height.Point);
    }

    /// <summary>
    /// Страница в пикселях — тем же движком, которым знак и накладывается.
    ///
    /// После перехода на впечатывание проверять содержимое иначе нечем: текста в выданном
    /// документе не остается вовсе, есть только картинка. Поэтому тесты смотрят на ПИКСЕЛИ, а не
    /// на объекты PDF, — и это проверка ровно того, что увидит человек.
    /// </summary>
    public static SKBitmap Render(string path, int pageNumber = 1, double scale = 0.35)
    {
        using var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(scale));
        using var page   = reader.GetPageReader(pageNumber - 1);

        var bitmap = new SKBitmap(new SKImageInfo(page.GetPageWidth(), page.GetPageHeight(),
                                                  SKColorType.Bgra8888, SKAlphaType.Premul));
        // Тот же прием, что и в расширении: PDFium отдает незакрашенные места ПРОЗРАЧНЫМИ, и
        // без подмены фона чистая страница пришла бы черной.
        var raw = page.GetImage(new Docnet.Core.Converters.NaiveTransparencyRemover(255, 255, 255));
        System.Runtime.InteropServices.Marshal.Copy(raw, 0, bitmap.GetPixels(), raw.Length);
        return bitmap;
    }

    /// <summary>
    /// Доля пикселей (в процентах), которыми две картинки одного размера расходятся сильнее
    /// <paramref name="tolerance"/> по яркости. Так измеряется и наличие знака (сравнение с
    /// исходной страницей), и его стойкость (сравнение после чистки).
    /// </summary>
    public static double DiffShare(SKBitmap a, SKBitmap b, int tolerance = 25)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            throw new InvalidOperationException($"размеры не совпали: {a.Width}×{a.Height} и {b.Width}×{b.Height}");

        var differing = 0;

        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var left  = a.GetPixel(x, y);
            var right = b.GetPixel(x, y);
            if (Math.Abs(Grey(left) - Grey(right)) > tolerance) differing++;
        }

        return differing * 100.0 / (a.Width * a.Height);
    }

    /// <summary>Средний цвет пикселей, отличающихся от эталона, — им проверяется, что знак серый,
    /// а не цветной.</summary>
    public static (double R, double G, double B) AverageDiffColor(SKBitmap stamped, SKBitmap clean,
                                                                 int tolerance = 25)
    {
        double r = 0, g = 0, b = 0;
        var count = 0;

        for (var y = 0; y < stamped.Height; y++)
        for (var x = 0; x < stamped.Width; x++)
        {
            var pixel = stamped.GetPixel(x, y);
            if (Math.Abs(Grey(pixel) - Grey(clean.GetPixel(x, y))) <= tolerance) continue;

            r += pixel.Red; g += pixel.Green; b += pixel.Blue; count++;
        }

        return count == 0 ? (0, 0, 0) : (r / count, g / count, b / count);
    }

    /// <summary>
    /// Самая дешевая атака на растровый знак: «уровни» с белой точкой
    /// <paramref name="white"/> — все, что светлее, становится белым.
    /// </summary>
    public static SKBitmap Levels(SKBitmap source, byte white)
    {
        var result = new SKBitmap(source.Info);

        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var pixel = source.GetPixel(x, y);
            result.SetPixel(x, y, new SKColor(Lift(pixel.Red), Lift(pixel.Green), Lift(pixel.Blue)));
        }

        return result;

        byte Lift(byte value) => (byte)Math.Min(255, value * 255 / white);
    }

    private static int Grey(SKColor color) => (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
}
