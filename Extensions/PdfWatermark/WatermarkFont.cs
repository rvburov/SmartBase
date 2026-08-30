using System.IO.Compression;
using SkiaSharp;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>
/// Шрифт водяного знака: PT Sans, встроенный в сборку.
///
/// Шрифт лежит В РЕПОЗИТОРИИ, а не берется с машины: на Windows нет DejaVu, на голом Linux нет
/// Arial, а знак обязан выглядеть одинаково везде, где выдают документы. PT Sans выбран за
/// полную кириллицу и свободную лицензию (SIL OFL, ParaType).
///
/// Знак рисуется по пикселям страницы, поэтому шрифт нужен ровно один раз — как очертания для
/// Skia. Внутрь выданного документа он не попадает вовсе: там уже готовая картинка. Отсюда и
/// простота этого класса — до перехода на впечатывание здесь жил еще резолвер для PDFsharp с
/// оговорками про глобальную статику, и вся эта конструкция ушла вместе с текстовым слоем.
/// </summary>
public static class WatermarkFont
{
    /// <summary>Окончание имени встроенного ресурса. Полное имя собирается из RootNamespace и
    /// пути файла, поэтому ищем по окончанию — как <c>EskdCatalog</c>.</summary>
    private const string ResourceSuffix = "ptsans-regular.ttf.gz";

    private static readonly Lazy<byte[]> Unpacked = new(Unpack, isThreadSafe: true);

    private static readonly Lazy<SKTypeface> Shared = new(
        () => SKTypeface.FromData(SKData.CreateCopy(Bytes))
              ?? throw new PdfWatermarkException("Встроенный шрифт знака не читается как TrueType."),
        isThreadSafe: true);

    /// <summary>Распакованные очертания PT Sans. Распаковка одна на процесс.</summary>
    public static byte[] Bytes => Unpacked.Value;

    /// <summary>
    /// Начертание для Skia. Один экземпляр на процесс: <see cref="SKTypeface"/> потокобезопасен
    /// на чтение, а разбор 400 КБ очертаний на каждую страницу был бы заметен на пачке.
    /// </summary>
    public static SKTypeface Typeface => Shared.Value;

    private static byte[] Unpack()
    {
        var assembly = typeof(WatermarkFont).Assembly;
        var name = assembly.GetManifestResourceNames()
                           .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));

        using var packed = name is not null && assembly.GetManifestResourceStream(name) is { } stream
            ? stream
            : throw new PdfWatermarkException(
                $"Встроенный шрифт знака не найден (ресурс «…{ResourceSuffix}»). " +
                "Проверьте правило EmbeddedResource для Extensions/**/*.gz в SmartBase.csproj.");

        using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        using var raw  = new MemoryStream(capacity: 512 * 1024);
        gzip.CopyTo(raw);
        return raw.ToArray();
    }
}
