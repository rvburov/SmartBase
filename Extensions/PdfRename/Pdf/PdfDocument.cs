using System.Runtime.InteropServices;
using OpenCvSharp;

namespace SmartBase.Extensions.PdfRename.Pdf;

/// <summary>Страница PDF в пунктах (1/72 дюйма) с учетом поворота /Rotate — как ее видно на экране.</summary>
internal readonly record struct PageSize(double Width, double Height)
{
    public double WidthMm => Width * 25.4 / 72;
    public double HeightMm => Height * 25.4 / 72;
    public bool IsLandscape => Width > Height;
}

/// <summary>Ошибка открытия PDF с понятной причиной для журнала.</summary>
internal sealed class PdfOpenException(string message) : Exception(message);

/// <summary>
/// PDF, открытый только на чтение. Файл читается целиком в неуправляемую память (сканы — от
/// сотен килобайт до десятков мегабайт) и сразу закрывается: исходный документ не держится
/// открытым и не меняется. Рисование — целой страницей в заданном масштабе или фрагментом.
/// </summary>
internal sealed class PdfDocument : IDisposable
{
    /// <summary>Общий замок PDFium: библиотека не потокобезопасна.</summary>
    public static readonly Lock Gate = new();

    /// <summary>Больше этого PDF не читается в память — пропуск с причиной.</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    private static bool _initialized;
    private IntPtr _buffer;
    private IntPtr _doc;

    public int PageCount { get; }

    private PdfDocument(IntPtr buffer, IntPtr doc, int pageCount)
    {
        _buffer = buffer;
        _doc = doc;
        PageCount = pageCount;
    }

    public static PdfDocument Open(string path)
    {
        long length;
        IntPtr buffer;
        try
        {
            // FileShare.ReadWrite: прочитать можно и файл, открытый в просмотрщике; запись в него
            // не ведется. Заблокированный на чтение файл дает IOException — это причина пропуска.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.SequentialScan);
            length = fs.Length;
            if (length == 0) throw new PdfOpenException("файл пустой");
            if (length > MaxFileBytes) throw new PdfOpenException($"файл больше {MaxFileBytes / 1024 / 1024} МБ");
            buffer = Marshal.AllocHGlobal((nint)length);
            try
            {
                unsafe
                {
                    var span = new Span<byte>((void*)buffer, (int)length);
                    fs.ReadExactly(span);
                }
            }
            catch
            {
                Marshal.FreeHGlobal(buffer);
                throw;
            }
        }
        catch (UnauthorizedAccessException) { throw new PdfOpenException("нет прав на чтение файла"); }
        catch (FileNotFoundException) { throw new PdfOpenException("файл не найден (удален или перемещен)"); }
        catch (DirectoryNotFoundException) { throw new PdfOpenException("папка файла недоступна"); }
        catch (IOException ex) { throw new PdfOpenException($"файл недоступен или заблокирован: {ex.Message}"); }

        lock (Gate)
        {
            if (!_initialized)
            {
                PdfiumApi.FPDF_InitLibrary();
                _initialized = true;
            }
            var doc = PdfiumApi.FPDF_LoadMemDocument(buffer, (int)length, null);
            if (doc == IntPtr.Zero)
            {
                var error = PdfiumApi.FPDF_GetLastError();
                Marshal.FreeHGlobal(buffer);
                throw new PdfOpenException(error switch
                {
                    3 => "файл не является PDF или поврежден",
                    4 => "PDF защищен паролем",
                    5 => "PDF защищен (не поддерживаемая защита)",
                    _ => "PDF не открывается (поврежден)",
                });
            }
            var count = PdfiumApi.FPDF_GetPageCount(doc);
            if (count <= 0)
            {
                PdfiumApi.FPDF_CloseDocument(doc);
                Marshal.FreeHGlobal(buffer);
                throw new PdfOpenException("в PDF нет страниц");
            }
            return new PdfDocument(buffer, doc, count);
        }
    }

    public PageSize GetPageSize(int index)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_doc == IntPtr.Zero, this);
            if (PdfiumApi.FPDF_GetPageSizeByIndex(_doc, index, out var w, out var h) == 0 || w <= 0 || h <= 0)
                throw new PdfOpenException($"не читается размер страницы {index + 1}");
            return new PageSize(w, h);
        }
    }

    /// <summary>
    /// Нарисовать страницу целиком так, чтобы длинная сторона была <paramref name="longSide"/>
    /// точек. Результат — BGR на белом фоне.
    /// </summary>
    public Mat RenderPage(int index, int longSide)
    {
        var size = GetPageSize(index);
        var scale = longSide / Math.Max(size.Width, size.Height);
        return RenderRegion(index, scale, 0, 0, size.Width, size.Height);
    }

    /// <summary>
    /// Нарисовать фрагмент страницы (в пунктах от левого верхнего угла) в масштабе
    /// <paramref name="scale"/> точек на пункт. Результат — BGR на белом фоне.
    /// </summary>
    public Mat RenderRegion(int index, double scale, double x, double y, double width, double height)
    {
        var size = GetPageSize(index);
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        if ((long)w * h > 120_000_000) throw new PdfOpenException("страница слишком большая для отрисовки");

        using var bgra = new Mat(h, w, MatType.CV_8UC4, Scalar.All(255));
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_doc == IntPtr.Zero, this);
            var page = PdfiumApi.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new PdfOpenException($"не открывается страница {index + 1}");
            try
            {
                var bitmap = PdfiumApi.FPDFBitmap_CreateEx(w, h, PdfiumApi.BitmapBgra, bgra.Data, (int)bgra.Step());
                if (bitmap == IntPtr.Zero) throw new PdfOpenException("не хватает памяти на отрисовку страницы");
                try
                {
                    PdfiumApi.FPDF_RenderPageBitmap(bitmap, page,
                        -(int)Math.Round(x * scale), -(int)Math.Round(y * scale),
                        (int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale),
                        0, PdfiumApi.RenderAnnotations);
                }
                finally
                {
                    PdfiumApi.FPDFBitmap_Destroy(bitmap);
                }
            }
            finally
            {
                PdfiumApi.FPDF_ClosePage(page);
            }
        }
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_doc != IntPtr.Zero) PdfiumApi.FPDF_CloseDocument(_doc);
            _doc = IntPtr.Zero;
        }
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
