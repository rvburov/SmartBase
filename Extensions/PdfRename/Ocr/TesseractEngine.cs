using System.Runtime.InteropServices;
using OpenCvSharp;

namespace SmartBase.Extensions.PdfRename.Ocr;

/// <summary>
/// Tesseract 5 (LSTM), языки rus+eng. Область читается как один блок текста (PSM 6), по строкам:
/// у каждой строки свой текст, уверенность и рамка — по ним общая красно-оранжевая область
/// делится на обозначение и наименование.
/// </summary>
internal sealed class TesseractEngine : IOcrEngine
{
    private IntPtr _handle;

    public string Name => "Tesseract";

    public TesseractEngine(string tessdata, string languages = OcrComponents.TesseractLanguages)
    {
        OcrComponents.RegisterNativeResolver();
        _handle = TesseractApi.TessBaseAPICreate();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("не создается экземпляр Tesseract");
        if (TesseractApi.TessBaseAPIInit3(_handle, tessdata, languages) != 0)
        {
            TesseractApi.TessBaseAPIDelete(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException($"не загружаются языковые данные {languages} из {tessdata}");
        }
        // Служебные сообщения Tesseract (о разрешении и т. п.) не нужны в консоли программы.
        TesseractApi.TessBaseAPISetVariable(_handle, "debug_file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
    }

    /// <summary>Версия библиотеки, например «5.3.4».</summary>
    public static string Version()
    {
        OcrComponents.RegisterNativeResolver();
        return Marshal.PtrToStringUTF8(TesseractApi.TessVersion()) ?? "";
    }

    public OcrReading Read(Mat gray)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (gray.Type() != MatType.CV_8UC1) throw new ArgumentException("нужна картинка 8 бит в оттенках серого");
        using var copy = gray.IsContinuous() ? null : gray.Clone();
        var image = copy ?? gray;

        var lines = new List<OcrLine>();
        TesseractApi.TessBaseAPISetPageSegMode(_handle, TesseractApi.PsmSingleBlock);
        TesseractApi.TessBaseAPISetImage(_handle, image.Data, image.Width, image.Height, 1, (int)image.Step());
        TesseractApi.TessBaseAPISetSourceResolution(_handle, 300);
        try
        {
            if (TesseractApi.TessBaseAPIRecognize(_handle, IntPtr.Zero) != 0)
                return new OcrReading(Name, lines);

            var it = TesseractApi.TessBaseAPIGetIterator(_handle);
            if (it == IntPtr.Zero) return new OcrReading(Name, lines);
            try
            {
                var page = TesseractApi.TessResultIteratorGetPageIterator(it);
                const int level = TesseractApi.LevelTextLine;
                do
                {
                    var ptr = TesseractApi.TessResultIteratorGetUTF8Text(it, level);
                    if (ptr == IntPtr.Zero) continue;
                    string text;
                    try { text = (Marshal.PtrToStringUTF8(ptr) ?? "").Trim(); }
                    finally { TesseractApi.TessDeleteText(ptr); }
                    if (text.Length == 0) continue;

                    var conf = TesseractApi.TessResultIteratorConfidence(it, level) / 100.0;
                    TesseractApi.TessPageIteratorBoundingBox(page, level, out var l, out var t, out var r, out var b);
                    lines.Add(new OcrLine(text, Math.Clamp(conf, 0, 1), l, t, r, b));
                }
                while (TesseractApi.TessPageIteratorNext(page, level) != 0);
            }
            finally
            {
                TesseractApi.TessResultIteratorDelete(it);
            }
        }
        finally
        {
            TesseractApi.TessBaseAPIClear(_handle);
        }
        return new OcrReading(Name, lines.OrderBy(l => l.Top).ToList());
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        TesseractApi.TessBaseAPIDelete(_handle);
        _handle = IntPtr.Zero;
    }
}
