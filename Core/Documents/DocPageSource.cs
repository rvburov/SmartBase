using System.Threading;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using SkiaSharp;
using UglyToad.PdfPig;

namespace SmartBase.Core.Documents;

/// <summary>Слово на странице PDF: текст и место в пунктах.</summary>
public readonly record struct DocWord(string Text, DocPageBox Box);

/// <summary>Размер страницы в пунктах.</summary>
public readonly record struct DocPageSize(double Width, double Height)
{
    public double Aspect => Height > 0 ? Width / Height : 1;
}

/// <summary>
/// Страницы PDF для панели чтения: размеры, картинка страницы и слова с их местами.
///
/// Сеанс, а не набор статических функций, ровно потому, что документ открывается ДОРОГО, а
/// страницы нужны по одной и по мере прокрутки: открывать файл заново на каждую страницу
/// значило бы разбирать его сотню раз подряд. Сеанс живет, пока панель показывает документ.
///
/// Внутри два разных разборщика, и это не дубль:
/// <list type="bullet">
/// <item><b>PDFium</b> (через <c>Docnet</c>) рисует страницу в пиксели — тот же движок, что у
///       миниатюр ленты и у расширения «Водяной знак»;</item>
/// <item><b>PdfPig</b> отдает слова с их прямоугольниками — рисовать он не умеет, зато только
///       он знает, ГДЕ на странице лежит слово. Без этого подсветку в режиме «как в документе»
///       ставить не на что.</item>
/// </list>
///
/// <b>Потоки.</b> Методы зовутся из фоновых задач панели, поэтому сеанс сериализует себя сам:
/// PdfPig своим замком, PDFium — общим <see cref="PdfGate"/> (он один на процесс, см. его
/// описание). Исключений методы не бросают: панель чтения открывают на чужих файлах, и любой
/// из них может оказаться битым.
/// </summary>
public sealed class DocPageSource : IDisposable
{
    /// <summary>Сколько страниц отдаем панели — тот же потолок, что у разбора текста.</summary>
    public static int MaxPages => DocTextExtractor.MaxPdfPages;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<int, IReadOnlyList<DocWord>> _words = [];
    private readonly DocPageSize[] _sizes;

    private PdfDocument? _pig;
    private Docnet.Core.Readers.IDocReader? _reader;
    private int _readerWidth;
    private bool _closed;

    public int PageCount => _sizes.Length;

    private DocPageSource(string path, PdfDocument pig, DocPageSize[] sizes)
    {
        _path  = path;
        _pig   = pig;
        _sizes = sizes;
    }

    /// <summary>
    /// Открыть документ. <c>null</c> — файл не PDF, не открылся или в нем нет страниц; для
    /// панели это не сбой, а повод остаться в режиме текста.
    /// </summary>
    public static DocPageSource? Open(string path)
    {
        if (DocTextExtractor.ExtensionOf(path) != "pdf") return null;

        try
        {
            var pig   = PdfDocument.Open(path);
            var count = Math.Min(pig.NumberOfPages, MaxPages);
            if (count <= 0) { pig.Dispose(); return null; }

            var sizes = new DocPageSize[count];
            for (var i = 0; i < count; i++)
            {
                var page = pig.GetPage(i + 1);
                sizes[i] = new DocPageSize(page.Width, page.Height);
            }
            return new DocPageSource(path, pig, sizes);
        }
        catch { return null; }
    }

    /// <summary>Размер страницы (0-индексная) в пунктах.</summary>
    public DocPageSize Size(int page) =>
        page >= 0 && page < _sizes.Length ? _sizes[page] : new DocPageSize(595, 842);

    /// <summary>
    /// Картинка страницы в PNG шириной <paramref name="pixelWidth"/>; <c>null</c> — не вышло.
    /// Открытый читатель переживает вызовы и пересоздается только при смене ширины: панель
    /// рисует страницы по мере прокрутки, и переоткрывать документ на каждую нельзя.
    /// </summary>
    public byte[]? Render(int page, int pixelWidth, CancellationToken ct = default)
    {
        if (page < 0 || page >= _sizes.Length) return null;

        var size   = _sizes[page];
        var width  = Math.Clamp(pixelWidth, 120, 2400);
        var height = Math.Max(1, (int)Math.Round(width * size.Height / Math.Max(1, size.Width)));

        try
        {
            lock (_lock)
            {
                if (_closed) return null;
                ct.ThrowIfCancellationRequested();

                return PdfGate.Run(() =>
                {
                    if (_reader is null || _readerWidth != width)
                    {
                        _reader?.Dispose();
                        // Рамка вписывания не принимает первое число больше второго, а у
                        // альбомной страницы высота по пропорции меньше ширины — тот же случай,
                        // что у миниатюр (DocThumbnail.Render).
                        _reader      = DocLib.Instance.GetDocReader(
                                           _path, new PageDimensions(width, Math.Max(width, height)));
                        _readerWidth = width;
                    }

                    using var reader = _reader.GetPageReader(page);
                    return Encode(reader);
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Битая страница не должна ронять чтение всего документа — панель покажет пустой
            // лист на ее месте.
            return null;
        }
    }

    /// <summary>
    /// Слова страницы с их местами. Считаются один раз на страницу и запоминаются: по ним
    /// панель и подсвечивает, и считает совпадения, а разбор страницы заметно дороже поиска
    /// по готовому списку.
    /// </summary>
    public IReadOnlyList<DocWord> Words(int page)
    {
        if (page < 0 || page >= _sizes.Length) return [];

        lock (_lock)
        {
            if (_closed) return [];
            if (_words.TryGetValue(page, out var ready)) return ready;

            var words = new List<DocWord>();
            try
            {
                if (_pig is { } pig)
                    foreach (var word in pig.GetPage(page + 1).GetWords())
                    {
                        var box = word.BoundingBox;
                        words.Add(new DocWord(word.Text, new DocPageBox(
                            Math.Min(box.Left, box.Right),  Math.Min(box.Bottom, box.Top),
                            Math.Max(box.Left, box.Right),  Math.Max(box.Bottom, box.Top))));
                    }
            }
            catch { /* страница без текстового слоя или битая — слов просто нет */ }

            _words[page] = words;
            return words;
        }
    }

    private static byte[]? Encode(Docnet.Core.Readers.IPageReader page)
    {
        // Размеры берутся У СТРАНИЦЫ, а не те, что просили: PDFium округляет вписывание, и
        // холст на «запрошенную» ширину разъезжает на пиксель в каждой строке — картинка уходит
        // по диагонали (тот же урок, что у миниатюр, см. DocThumbnail.Encode).
        var width  = page.GetPageWidth();
        var height = page.GetPageHeight();
        if (width <= 0 || height <= 0) return null;

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var raw = page.GetImage(new NaiveTransparencyRemover(255, 255, 255));
        if (raw.Length == 0) return null;

        System.Runtime.InteropServices.Marshal.Copy(raw, 0, bitmap.GetPixels(),
                                                    Math.Min(raw.Length, bitmap.ByteCount));

        using var image = SKImage.FromBitmap(bitmap);
        using var data  = image.Encode(SKEncodedImageFormat.Png, 90);
        return data?.ToArray();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;

            PdfGate.Run(() => { _reader?.Dispose(); _reader = null; });
            _pig?.Dispose();
            _pig = null;
            _words.Clear();
        }
    }
}
