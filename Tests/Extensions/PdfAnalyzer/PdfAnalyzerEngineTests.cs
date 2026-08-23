using SmartBase.Extensions.PdfAnalyzer;

namespace SmartBase.Tests.Extensions.PdfAnalyzer;

/// <summary>
/// Точки отказа расширения до чтения файлов: каждая обязана быть понятной строкой в статусе окна.
/// PDF здесь не нужны — проверяется, что расширение отказывается ПОНЯТНО, а не падает.
/// </summary>
public sealed class PdfAnalyzerEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sbext_pa_err_{Guid.NewGuid():N}");

    public PdfAnalyzerEngineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private PdfAnalyzeException RunExpectingFailure(string folder) =>
        Assert.Throws<PdfAnalyzeException>(() => PdfAnalyzerEngine.Run(
            folder, savePath: Path.Combine(_root, "отчет.xlsx"), ExportType.All,
            recursive: false, preserveOrder: false, splitName: false, progress: null,
            ct: CancellationToken.None));

    [Fact]
    public void Missing_folder_is_a_friendly_error()
    {
        var missing = Path.Combine(_root, "нет-такой-папки");

        var ex = RunExpectingFailure(missing);

        Assert.Contains("Папка не существует", ex.Message);
        Assert.Contains(missing, ex.Message);   // путь виден — понятно, что именно поправить
    }

    [Fact]
    public void Folder_without_pdf_files_is_a_friendly_error()
    {
        File.WriteAllText(Path.Combine(_root, "записка.txt"), "не pdf");

        var ex = RunExpectingFailure(_root);

        Assert.Equal("В указанной папке не найдено PDF файлов.", ex.Message);
    }

    [Fact]
    public void Empty_folder_is_a_friendly_error()
    {
        var empty = Path.Combine(_root, "пустая");
        Directory.CreateDirectory(empty);

        var ex = RunExpectingFailure(empty);

        Assert.Equal("В указанной папке не найдено PDF файлов.", ex.Message);
    }
}
