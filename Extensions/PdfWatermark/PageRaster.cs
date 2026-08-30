using PdfSharp.Pdf;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>
/// Разрешение, в котором страницы переводятся в растр.
///
/// Знак ВПЕЧАТЫВАЕТСЯ в пиксели, значит документ пересобирается из картинок, и разрешение — это
/// сразу и читаемость выданного документа, и размер файла. Одного числа тут мало: 200 dpi для
/// текстового A4 дает четкий лист в полтора мегабайта, а для скана телефона, где страница
/// объявлена в 26 дюймов шириной, те же 200 dpi раздули бы картинку впятеро против снимка,
/// ничего к ней не добавив.
///
/// Отсюда три упора, и берется наименьший из них.
/// </summary>
public static class PageRaster
{
    /// <summary>К чему стремимся: 200 dpi — предел, за которым мелкий шрифт документа уже не
    /// становится читаемее, а файл продолжает расти.</summary>
    public const int TargetDpi = 200;

    /// <summary>Потолок по длинной стороне. Держит память: 5000×3535 в BGRA — это 70 МБ на
    /// страницу, и это уже крупный формат вроде A1.</summary>
    public const int MaxLongSidePx = 5000;

    /// <summary>С какого размера картинка на странице считается СКАНОМ, а не логотипом. Ниже
    /// этого порога родное разрешение игнорируется — иначе герб в 60 пикселей задал бы
    /// разрешение всему листу.</summary>
    public const int NativeFloorPx = 1000;

    /// <summary>Качество JPEG. 82 — та точка, где на сканах и тексте не видно артефактов, а
    /// файл вдвое меньше, чем на 95.</summary>
    public const int JpegQuality = 82;

    private const double PointsPerInch = 72.0;

    /// <summary>
    /// Масштаб рендера для ВСЕГО документа: PDFium держит один масштаб на открытый документ, а
    /// открывать его заново на каждую страницу — значит каждый раз заново разбирать файл.
    /// Поэтому берется масштаб по самой требовательной странице, а мелкие страницы получают
    /// разрешение с запасом (это дешевле, чем обратное).
    /// </summary>
    public static double ScaleFor(PdfDocument doc)
    {
        var scale = double.MaxValue;

        foreach (var page in doc.Pages)
        {
            var w = page.Width.Point;
            var h = page.Height.Point;
            if (w <= 0 || h <= 0) continue;

            scale = Math.Min(scale, ScaleForPage(w, h, NativeWidthPx(page)));
        }

        return scale is double.MaxValue ? TargetDpi / PointsPerInch : scale;
    }

    /// <summary>Масштаб одной страницы. Вынесен отдельно — это чистая арифметика, и она
    /// проверяется тестом без единого PDF.</summary>
    public static double ScaleForPage(double widthPt, double heightPt, int nativeWidthPx)
    {
        var byDpi = TargetDpi / PointsPerInch;

        // Скан уже содержит все свои детали: рисовать его крупнее снимка — только раздувать
        // файл. Но родным разрешением считается не любая картинка: порог в
        // NativeFloorPx отсекает логотипы и печати. Иначе страница договора с гербом в 60
        // пикселей отрисовалась бы В ШЕСТЬДЕСЯТ пикселей шириной — и документ пропал бы.
        var byNative = nativeWidthPx >= NativeFloorPx ? nativeWidthPx / widthPt : byDpi;

        var scale = Math.Min(byDpi, byNative);

        // Потолок памяти. Нижнего упора по разрешению здесь СОЗНАТЕЛЬНО нет: он спорил бы с
        // родным разрешением скана. Скан в 300 пикселей на лист A4 плох сам по себе, но
        // растянуть его до 200 dpi — значит не улучшить документ, а только раздуть файл втрое.
        var longSide = Math.Max(widthPt, heightPt);

        return Math.Min(scale, MaxLongSidePx / longSide);
    }

    /// <summary>
    /// Ширина самого крупного изображения страницы в пикселях; 0 — изображений нет (то есть
    /// страница векторная или текстовая, и родного разрешения у нее не существует).
    ///
    /// Смотрим только прямые ресурсы страницы, без захода внутрь вложенных форм: сканы — а ради
    /// них проверка и заведена — кладут снимок именно так, одним XObject на странице.
    /// </summary>
    public static int NativeWidthPx(PdfPage page)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        var xobjects  = resources?.Elements.GetDictionary("/XObject");
        if (xobjects is null) return 0;

        var widest = 0;

        foreach (var key in xobjects.Elements.Keys)
        {
            if (xobjects.Elements.GetObject(key) is not PdfDictionary image) continue;
            if (image.Elements.GetName("/Subtype") != "/Image") continue;

            widest = Math.Max(widest, image.Elements.GetInteger("/Width"));
        }

        return widest;
    }
}
