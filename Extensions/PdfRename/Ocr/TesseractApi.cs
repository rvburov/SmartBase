using System.Runtime.InteropServices;

namespace SmartBase.Extensions.PdfRename.Ocr;

/// <summary>
/// Вызовы C API Tesseract (tesseract/capi.h). Обертки из NuGet не используются: они тянут свои
/// копии нативных библиотек под Windows и не работают с системным Tesseract на Linux, а здесь
/// нужен ровно десяток функций. Где взять библиотеку, решает <see cref="OcrComponents"/>.
/// </summary>
internal static class TesseractApi
{
    /// <summary>Условное имя библиотеки; реальный путь подставляет загрузчик плагина.</summary>
    public const string Library = "tesseract";

    // Режимы разбиения страницы (TessPageSegMode).
    public const int PsmSingleBlock = 6;
    public const int PsmSingleLine = 7;

    // Уровни обхода результата (TessPageIteratorLevel).
    public const int LevelTextLine = 2;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessVersion();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessBaseAPICreate();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPIDelete(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TessBaseAPIInit3(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dataPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string language);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TessBaseAPISetVariable(IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPISetPageSegMode(IntPtr handle, int mode);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPISetImage(IntPtr handle, IntPtr data, int width, int height,
        int bytesPerPixel, int bytesPerLine);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPISetSourceResolution(IntPtr handle, int ppi);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TessBaseAPIRecognize(IntPtr handle, IntPtr monitor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessBaseAPIClear(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessBaseAPIGetIterator(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessResultIteratorDelete(IntPtr iterator);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessResultIteratorGetPageIterator(IntPtr iterator);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TessResultIteratorGetUTF8Text(IntPtr iterator, int level);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern float TessResultIteratorConfidence(IntPtr iterator, int level);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TessPageIteratorNext(IntPtr iterator, int level);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TessPageIteratorBoundingBox(IntPtr iterator, int level,
        out int left, out int top, out int right, out int bottom);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TessDeleteText(IntPtr text);
}
