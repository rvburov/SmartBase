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

    /// <summary>
    /// Самая вытянутая пропорция среди КНИЖНЫХ листов (высота к ширине, не меньше единицы).
    /// По ней строится рамка книжного читателя: не хватит высоты — PDFium начнет вписывать
    /// лист по ней, и тот выйдет уже запрошенной ширины.
    /// </summary>
    private readonly double _uprightAspect;

    /// <summary>
    /// Самая «высокая» пропорция среди АЛЬБОМНЫХ листов (меньше единицы; ноль — таких листов
    /// в документе нет). У альбомного листа короткая сторона — высота, и в рамке она идет
    /// первым числом.
    /// </summary>
    private readonly double _wideAspect;

    private PdfDocument? _pig;

    // Читателей ДВА — под книжные листы и под альбомные. Почему не один, написано у ReaderFor.
    private Docnet.Core.Readers.IDocReader? _upright;
    private Docnet.Core.Readers.IDocReader? _wide;
    private int _readerWidth;
    private bool _closed;

    /// <summary>Сколько страниц панель показывает — не больше <see cref="MaxPages"/>.</summary>
    public int PageCount => _sizes.Length;

    /// <summary>
    /// Сколько страниц в документе ВСЕГО. Больше <see cref="PageCount"/> — значит, том показан не
    /// целиком, и панель обязана сказать об этом, а не оборвать его молча.
    /// </summary>
    public int TotalPages { get; }

    private DocPageSource(string path, PdfDocument pig, DocPageSize[] sizes)
    {
        _path  = path;
        _pig   = pig;
        _sizes = sizes;
        TotalPages = Math.Max(sizes.Length, pig.NumberOfPages);

        _uprightAspect = 1.0;
        _wideAspect    = 0.0;
        foreach (var s in sizes)
        {
            if (s.Width <= 0) continue;
            var aspect = s.Height / s.Width;
            if (aspect >= 1) _uprightAspect = Math.Max(_uprightAspect, aspect);
            else             _wideAspect    = Math.Max(_wideAspect,    aspect);
        }
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

                // Размер берем ВИДИМЫЙ: с обрезкой листа и с поворотом /Rotate. `page.Width` и
                // `page.Height` описывают неповернутый лист, а PDFium рисует страницу уже
                // повернутой — на развороте, положенном боком, пропорции расходились, лист
                // ложился в чужую рамку и выходил сплющенным с обрезанным низом.
                var visible = page.CropBox.GetVisibleBounds(page.Rotation);
                sizes[i] = new DocPageSize(visible.Width, visible.Height);
            }
            return new DocPageSource(path, pig, sizes);
        }
        catch { return null; }
    }

    /// <summary>
    /// Читатель под форму листа. Рамка вписывания у Docnet — это не «ширина и высота», а
    /// «короткая сторона и длинная»: одна и та же рамка (400, 942) для книжного листа означает
    /// ширину 400, а для альбомного — ВЫСОТУ 400, и тот выходит шире запрошенного. Поэтому
    /// читателей два, книжный и альбомный.
    ///
    /// В каждом рамка взята С ЗАПАСОМ по длинной стороне — по самому вытянутому листу своей
    /// формы. Запас и делает узким местом ШИРИНУ: любой лист выходит ровно в запрошенную, то
    /// есть «по ширине окна», как в любом просмотрщике. Пока рамка строилась по ЗАПРОШЕННОЙ
    /// странице и переиспользовалась дальше, она оставалась от той страницы, которую нарисовали
    /// первой: соседние листы вписывались в чужую пропорцию и выходили мельче, а какой лист
    /// задаст рамку — решал порядок прокрутки.
    ///
    /// Читатель создается лениво: у документа из листов одной формы второй не заводится вовсе.
    /// Зовется под <see cref="PdfGate"/> и под своим замком.
    /// </summary>
    private Docnet.Core.Readers.IDocReader? ReaderFor(bool upright, int width)
    {
        if (_readerWidth != width)
        {
            _upright?.Dispose(); _upright = null;
            _wide?.Dispose();    _wide    = null;
            _readerWidth = width;
        }

        if (upright)
            return _upright ??= DocLib.Instance.GetDocReader(
                _path, new PageDimensions(width, Math.Max(width, (int)Math.Ceiling(width * _uprightAspect))));

        // У альбомного листа короткая сторона — высота, и она идет первым числом. Ноль вместо
        // пропорции означал бы рамку в один пиксель, поэтому у него свой пол.
        var shorter = Math.Max(1, (int)Math.Ceiling(width * (_wideAspect > 0 ? _wideAspect : 1)));
        return _wide ??= DocLib.Instance.GetDocReader(
            _path, new PageDimensions(Math.Min(shorter, width), width));
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

        var size    = _sizes[page];
        var width   = Math.Clamp(pixelWidth, 120, 2400);
        var upright = size.Width <= 0 || size.Height >= size.Width;

        try
        {
            lock (_lock)
            {
                if (_closed) return null;
                ct.ThrowIfCancellationRequested();

                return PdfGate.Run(() =>
                {
                    var reader = ReaderFor(upright, width);
                    if (reader is null) return null;

                    using var pageReader = reader.GetPageReader(page);
                    return Encode(pageReader);
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

        // Холст должен быть закрашен ЦЕЛИКОМ. Придет байт меньше, чем на него нужно, — хвост
        // останется нетронутым, и лист выйдет обрезанным по высоте. Молча такое проходить не
        // должно: на глаз это «страница обрезалась наполовину», а в журнале — ни строки.
        if (raw.Length < bitmap.ByteCount)
        {
            bitmap.Erase(SKColors.White);
            Serilog.Log.Warning(
                "Библиотека: страница пришла неполной — {Got} байт из {Need} на холст {Width}×{Height}",
                raw.Length, bitmap.ByteCount, width, height);
        }

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

            PdfGate.Run(() =>
            {
                _upright?.Dispose(); _upright = null;
                _wide?.Dispose();    _wide    = null;
            });
            _pig?.Dispose();
            _pig = null;
            _words.Clear();
        }
    }
}
