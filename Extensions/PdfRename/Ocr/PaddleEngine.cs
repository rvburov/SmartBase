using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;

namespace SmartBase.Extensions.PdfRename.Ocr;

/// <summary>
/// PaddleOCR v5 на движке Paddle Inference (CPU): детектор строк <c>mobile_zh_det</c>
/// (многоязычный) и распознавание <c>eslav_PP_OCRv5_mobile_rec</c> (русский, латиница, цифры).
/// Модели лежат в папке плагина <c>ocr/paddle</c>. Найденные фрагменты собираются в строки
/// по вертикальному перекрытию.
/// </summary>
internal sealed class PaddleEngine : IOcrEngine
{
    private PaddleOcrAll? _ocr;

    /// <summary>Во сколько раз расширять найденную рамку строки перед распознаванием. 2.0 —
    /// значение PaddleOCR по умолчанию; 2.5 и 3.0 на синтетических сканах читали наименования
    /// хуже (замер — в документации расширения).</summary>
    public const float UnclipRatio = 2.0f;

    public string Name => "PaddleOCR";

    public PaddleEngine(string modelsDir)
    {
        var det = Path.Combine(modelsDir, OcrComponents.PaddleDetector);
        var rec = Path.Combine(modelsDir, OcrComponents.PaddleRecognizer);
        foreach (var dir in new[] { det, rec })
            if (!File.Exists(Path.Combine(dir, "inference.pdiparams")))
                throw new FileNotFoundException($"нет модели {Path.GetFileName(dir)} в {modelsDir}");

        var model = new FullOcrModel(
            DetectionModel.FromDirectory(det, ModelVersion.V5),
            RecognizationModel.FromDirectoryV5(rec));
        // Один поток вычислений на движок: параллельность дают рабочие потоки прогона.
        _ocr = new PaddleOcrAll(model, PaddleDevice.Blas(1))
        {
            AllowRotateDetection = false,
            Enable180Classification = false,
        };
        _ocr.Detector.UnclipRatio = UnclipRatio;
    }

    public OcrReading Read(Mat gray)
    {
        ObjectDisposedException.ThrowIf(_ocr is null, this);
        using var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        var result = _ocr.Run(bgr);

        var pieces = result.Regions
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => (Rect: r.Rect.BoundingRect(), Text: r.Text.Trim(), Score: (double)r.Score))
            .OrderBy(p => p.Rect.Top + p.Rect.Height / 2)
            .ToList();

        // Строка — фрагменты, перекрывающиеся по вертикали больше чем наполовину меньшего.
        var rows = new List<List<(Rect Rect, string Text, double Score)>>();
        foreach (var p in pieces)
        {
            var row = rows.FirstOrDefault(r => r.Any(q => VerticalOverlap(q.Rect, p.Rect) > 0.5));
            if (row is null) rows.Add([p]);
            else row.Add(p);
        }

        var lines = rows.Select(row =>
        {
            var ordered = row.OrderBy(q => q.Rect.Left).ToList();
            return new OcrLine(
                string.Join(" ", ordered.Select(q => q.Text)),
                Math.Clamp(ordered.Min(q => q.Score), 0, 1),
                ordered.Min(q => q.Rect.Left), ordered.Min(q => q.Rect.Top),
                ordered.Max(q => q.Rect.Right), ordered.Max(q => q.Rect.Bottom));
        }).OrderBy(l => l.Top).ToList();

        return new OcrReading(Name, lines);
    }

    private static double VerticalOverlap(Rect a, Rect b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var min = Math.Min(a.Height, b.Height);
        return min <= 0 ? 0 : Math.Max(0, overlap) / (double)min;
    }

    public void Dispose()
    {
        _ocr?.Dispose();
        _ocr = null;
    }
}
