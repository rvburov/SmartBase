using System.IO;
using SkiaSharp;
using SmartBase.Core.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Миниатюра документа (<c>Core/Documents/DocThumbnail.cs</c>) — первая страница PDF, нарисованная
/// PDFium и подрезанная под окошко плитки.
///
/// Тесты смотрят на ПИКСЕЛИ готовой картинки, и по-другому тут нельзя: оба дефекта, которые эти
/// проверки сторожат, не видны ни в одном исключении и ни в одной строке журнала.
///
/// 1. <b>Перекос.</b> PDFium вписывает страницу в заданную рамку с сохранением пропорций и
///    округляет — на части размеров страницы он отдает 335 пикселей ширины там, где просили 336.
///    Холст на 336 при таких данных сдвигает КАЖДУЮ следующую строку на пиксель: картинка уходит
///    по диагонали. На тексте это почти незаметно, на сплошном скане — сразу.
/// 2. <b>Альбомная страница.</b> Рамка требует, чтобы первое число не превышало второго, а у
///    альбомного листа высота по пропорции меньше ширины. Отрисовка падала ArgumentException,
///    исключение глотал <c>catch</c>, и чертежи просто никогда не получали миниатюру.
///
/// Размеры страниц в теориях подобраны перебором (см. историю правки): 594×842 перекос
/// воспроизводит, 595×842 — нет. Разница в один пункт, и это ровно тот разброс, с которым
/// сканеры выдают А4.
/// </summary>
public sealed class DocThumbnailTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cache;

    public DocThumbnailTests()
    {
        _dir   = Path.Combine(Path.GetTempPath(), "sb-thumb-" + Guid.NewGuid().ToString("N")[..8]);
        _cache = Path.Combine(_dir, "thumbs");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* занят — уберет система */ }
    }

    /// <summary>
    /// Страница с УЗКОЙ колонкой текста у левого поля. Колонка выбрана не для красоты: перекос
    /// сдвигает каждую строку картинки по горизонтали, и столбик превращается в лесенку, а вот
    /// полоса во всю ширину от такого сдвига не изменилась бы вовсе.
    /// </summary>
    private string WritePdf(string name, double widthPt, double heightPt)
    {
        var path = Path.Combine(_dir, name);

        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(widthPt, heightPt);

        // Standard14 не знает кириллицы; сплошные «I» дают плотный столбик темных пикселей.
        for (var y = heightPt - 24; y > 24; y -= 7)
            page.AddText("IIIIIIIIIIIIIIII", 8, new PdfPoint(24, y), font);

        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>
    /// Средний x темных пикселей в полосе строк; -1 — темных нет вовсе. Порог мягкий: буквы 8
    /// пунктов при ширине картинки 336 занимают по вертикали четыре пикселя и приходят серыми от
    /// сглаживания, а не черными (самый темный пиксель такой строки — около 146 по каналу).
    /// </summary>
    private static double DarkCenter(SKBitmap bitmap, int fromRow, int rows)
    {
        double sum = 0;
        var count = 0;

        for (var y = Math.Max(0, fromRow); y < Math.Min(bitmap.Height, fromRow + rows); y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red + c.Green + c.Blue >= 600) continue;
            sum += x;
            count++;
        }

        return count == 0 ? -1 : sum / count;
    }

    [Theory]
    [InlineData(594, 842)]   // А4 со сканера: перекос воспроизводится
    [InlineData(595, 842)]   // А4 точный
    [InlineData(612, 792)]   // Letter
    [InlineData(420, 595)]   // А5
    public void Page_is_not_skewed(double widthPt, double heightPt)
    {
        var pdf  = WritePdf($"p{widthPt}x{heightPt}.pdf", widthPt, heightPt);
        var file = DocThumbnail.Render(_cache, pdf);
        Assert.NotNull(file);

        using var image = SKBitmap.Decode(file);
        // Полосы берутся с отступом от краев: сверху у страницы поле, снизу картинка обрезана.
        var top    = DarkCenter(image, 10, 24);
        var bottom = DarkCenter(image, image.Height - 34, 24);

        // Текст обязан быть в обеих полосах — иначе сравнивать нечего и тест ничего не значит.
        Assert.True(top    >= 0, "в верхней полосе картинки нет текста");
        Assert.True(bottom >= 0, "в нижней полосе картинки нет текста");

        // Перекос на пиксель в строке уводит столбик на сотню пикселей к низу картинки. Допуск
        // в четыре пикселя — на сглаживание букв, а не на наклон.
        Assert.True(Math.Abs(top - bottom) <= 4,
            $"столбик текста уехал: сверху {top:F1}, снизу {bottom:F1} — картинка перекошена");
    }

    /// <summary>
    /// Альбомная страница (чертеж, разворот) обязана получить миниатюру. До починки отрисовка
    /// падала на самой рамке PageDimensions, и карточка молча оставалась с серым окошком.
    /// </summary>
    [Fact]
    public void Landscape_page_gets_a_thumbnail()
    {
        var pdf  = WritePdf("land.pdf", 842, 595);
        var file = DocThumbnail.Render(_cache, pdf);

        Assert.NotNull(file);
        using var image = SKBitmap.Decode(file);
        Assert.True(image.Width > image.Height, "альбомная страница нарисована как книжная");
    }

    /// <summary>
    /// Картинка подрезана СВЕРХУ под пропорции окошка плитки: показывается шапка страницы. Книжный
    /// лист всегда выше окошка, поэтому подрезка обязана сработать.
    /// </summary>
    [Fact]
    public void Portrait_page_is_cut_to_the_tile_box()
    {
        var pdf  = WritePdf("a4.pdf", 595, 842);
        var file = DocThumbnail.Render(_cache, pdf);
        Assert.NotNull(file);

        using var image = SKBitmap.Decode(file);
        var expected = (double)DocThumbnail.BoxWidth / DocThumbnail.BoxHeight;
        Assert.Equal(expected, (double)image.Width / image.Height, 1);
    }

    /// <summary>
    /// Второй заход отдает ту же картинку из кеша и ничего не рисует заново — на этом держится
    /// прокрутка ленты: миниатюра запрашивается на каждом появлении карточки.
    /// </summary>
    [Fact]
    public void Second_render_comes_from_the_cache()
    {
        var pdf   = WritePdf("cached.pdf", 595, 842);
        var first = DocThumbnail.Render(_cache, pdf);
        Assert.NotNull(first);

        var stamp  = File.GetLastWriteTimeUtc(first);
        var second = DocThumbnail.Render(_cache, pdf);

        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second!));
        Assert.Equal(first, DocThumbnail.Cached(_cache, pdf));
    }
}
