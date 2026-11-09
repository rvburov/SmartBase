using System.Runtime.InteropServices;

namespace SmartBase.Tests.Extensions.PdfRename.Samples;

/// <summary>
/// Где плагин поставляется с OCR и OpenCV: Windows x64 и Linux x64. На macOS и ARM готовых
/// библиотек OpenCV/Paddle для поставки нет — там тесты распознавания и обработки картинок
/// пропускаются с этой причиной (ограничение первой версии, описано в документации).
/// </summary>
public static class OcrPlatform
{
    public static bool Supported =>
        RuntimeInformation.OSArchitecture == Architecture.X64 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    public const string Reason = "OCR-часть плагина в первой версии поставляется только для Windows x64 и Linux x64";
}

/// <summary>Тест обработки картинок и OCR: пропускается там, где плагин без OCR.</summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        if (!OcrPlatform.Supported) Skip = OcrPlatform.Reason;
    }
}

/// <summary>То же для теорий.</summary>
public sealed class OcrTheoryAttribute : TheoryAttribute
{
    public OcrTheoryAttribute()
    {
        if (!OcrPlatform.Supported) Skip = OcrPlatform.Reason;
    }
}
