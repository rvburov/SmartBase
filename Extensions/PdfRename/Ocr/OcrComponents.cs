using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SmartBase.Extensions.PdfRename.Ocr;

/// <summary>
/// Где лежат компоненты распознавания и готовы ли они. Все свое расширение держит в собственной
/// папке (папка плагина): модели PaddleOCR — <c>ocr/paddle</c>, данные Tesseract —
/// <c>ocr/tessdata</c>, Tesseract для Windows — <c>ocr/tesseract</c>. Чего в папке нет, ищется
/// в системе — так на Linux и macOS Tesseract берется из пакетного менеджера (на этих ОС
/// готовых библиотек для поставки с расширением нет).
///
/// Ничего не скачивается: распознавание идет на этой машине, сканы никуда не уходят.
/// </summary>
internal static class OcrComponents
{
    /// <summary>Языки Tesseract: русский и английский (латиница в обозначениях и наименованиях).</summary>
    public const string TesseractLanguages = "rus+eng";

    /// <summary>Модели PaddleOCR v5: многоязычный детектор строк и распознавание
    /// восточнославянских языков с латиницей и цифрами.</summary>
    public const string PaddleDetector   = "mobile_zh_det";
    public const string PaddleRecognizer = "eslav_PP_OCRv5_mobile_rec";

    /// <summary>Папка плагина — там, где лежит его сборка.</summary>
    public static string PluginDir { get; } =
        Path.GetDirectoryName(typeof(OcrComponents).Assembly.Location) ?? AppContext.BaseDirectory;

    public static string PaddleDir => Path.Combine(PluginDir, "ocr", "paddle");

    private static int _resolverSet;

    /// <summary>
    /// Подсказать загрузчику, где искать Tesseract: в папке плагина (Windows) или в системе
    /// (Linux, macOS). Остальные нативные библиотеки находит загрузчик плагина по его
    /// <c>.deps.json</c>.
    /// </summary>
    public static void RegisterNativeResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(OcrComponents).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Уже назначен (повторная загрузка в тестах) — тот же самый.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != TesseractApi.Library) return IntPtr.Zero;
        foreach (var candidate in TesseractCandidates())
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return IntPtr.Zero;
    }

    /// <summary>Где искать библиотеку Tesseract на этой ОС, в порядке предпочтения.</summary>
    public static IEnumerable<string> TesseractCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(PluginDir, "ocr", "tesseract", "tesseract55.dll");
            yield break;
        }
        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/lib/libtesseract.5.dylib";
            yield return "/usr/local/lib/libtesseract.5.dylib";
            yield return "libtesseract.5.dylib";
            yield break;
        }
        yield return Path.Combine(PluginDir, "ocr", "tesseract", "libtesseract.so.5");
        yield return "libtesseract.so.5";
        yield return "libtesseract.so";
    }

    /// <summary>Папка с данными Tesseract, где есть оба языка; null — нет нигде.</summary>
    public static string? FindTessdata()
    {
        foreach (var dir in TessdataCandidates())
            if (File.Exists(Path.Combine(dir, "rus.traineddata")) && File.Exists(Path.Combine(dir, "eng.traineddata")))
                return dir;
        return null;
    }

    private static IEnumerable<string> TessdataCandidates()
    {
        yield return Path.Combine(PluginDir, "ocr", "tessdata");
        if (Environment.GetEnvironmentVariable("TESSDATA_PREFIX") is { Length: > 0 } prefix)
        {
            yield return prefix;
            yield return Path.Combine(prefix, "tessdata");
        }
        if (OperatingSystem.IsWindows()) yield break;
        yield return "/usr/share/tesseract-ocr/5/tessdata";
        yield return "/usr/share/tesseract-ocr/4.00/tessdata";
        yield return "/usr/share/tessdata";
        yield return "/opt/homebrew/share/tessdata";
        yield return "/usr/local/share/tessdata";
    }

    /// <summary>
    /// Проверить, что ОБА движка готовы: библиотеки грузятся, модели и языковые данные на месте.
    /// Возвращает список проблем понятным текстом; пустой — можно распознавать. Проверка честная:
    /// движки по-настоящему создаются (и сразу освобождаются), а не только ищутся файлы.
    /// </summary>
    public static IReadOnlyList<string> CheckReady()
    {
        RegisterNativeResolver();
        var problems = new List<string>();

        var tessdata = FindTessdata();
        if (tessdata is null)
            problems.Add("Tesseract: нет языковых данных rus.traineddata и eng.traineddata " +
                         "(папка ocr/tessdata расширения или системная папка tessdata).");
        else
        {
            try { using var engine = new TesseractEngine(tessdata); }
            catch (Exception ex) { problems.Add($"Tesseract не запускается: {Explain(ex)}"); }
        }

        try { using var engine = new PaddleEngine(PaddleDir); }
        catch (Exception ex) { problems.Add($"PaddleOCR не запускается: {Explain(ex)}"); }

        return problems;
    }

    internal static string Explain(Exception ex) => ex switch
    {
        DllNotFoundException when OperatingSystem.IsLinux()
            => "не найдена системная библиотека (на Linux: sudo apt install tesseract-ocr)",
        DllNotFoundException when OperatingSystem.IsMacOS()
            => "не найдена системная библиотека (на macOS: brew install tesseract)",
        DllNotFoundException when OperatingSystem.IsWindows()
            => "не загружается библиотека — нужен Microsoft Visual C++ 2015–2022 x64 " +
               "(обычно уже есть в системе) или переустановите расширение",
        DllNotFoundException d => d.Message,
        TypeInitializationException { InnerException: { } inner } => Explain(inner),
        _ => ex.Message,
    };
}
