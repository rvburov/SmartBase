using System.Runtime.InteropServices;

namespace SmartBase.Extensions.PdfRename.Pdf;

/// <summary>
/// Нужные функции PDFium (fpdfview.h). Библиотеку приносит пакет Docnet.Core
/// (<c>runtimes/&lt;rid&gt;/native/pdfium</c>), но его управляемая обертка не умеет рисовать
/// фрагмент страницы, а для чертежей A1/A0 рисовать лист целиком в 300 dpi — сотни мегабайт.
/// PDFium не потокобезопасен: все вызовы идут под <see cref="PdfDocument.Gate"/>.
/// </summary>
internal static class PdfiumApi
{
    private const string Library = "pdfium";

    /// <summary>Рисовать аннотации: рамки шаблона, нарисованные в редакторе PDF, — это они.</summary>
    public const int RenderAnnotations = 0x01;

    /// <summary>Формат картинки PDFium: 4 байта на точку, порядок B, G, R, A.</summary>
    public const int BitmapBgra = 4;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint FPDF_GetLastError();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int FPDF_GetPageSizeByIndex(IntPtr document, int index, out double width, out double height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY,
        int sizeX, int sizeY, int rotate, int flags);
}
