using System.Text.RegularExpressions;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Сторож правила «любой вызов в PDFium — через очередь <c>PdfGate</c>»
/// (<c>Core/Documents/PdfGate.cs</c>). PDFium не потокобезопасен, а рисуют им трое — миниатюры
/// ленты, панель чтения и расширение «Водяной знак», — и вызов мимо очереди падает из недр
/// нативной библиотеки без единой понятной строки в журнале. Тестом такую гонку не поймать: она
/// случается раз на тысячу прогонов. Поэтому правило проверяется по тексту, как
/// <see cref="OneDoorOutwardTests"/>.
///
/// Проверки две:
/// <list type="bullet">
/// <item>объект PDFium, полученный из <c>PdfGate.Run</c>, не закрывается <c>using</c>: закрытие —
///       тоже вызов в PDFium, а <c>using</c> делает его уже ПОСЛЕ замка. Так было в «Водяном
///       знаке»: документ и каждая его страница закрывались мимо очереди;</item>
/// <item>к PDFium напрямую обращаются только известные файлы. Новое место — повод проверить,
///       что оно идет через очередь, и дописать его сюда осознанно.</item>
/// </list>
/// </summary>
public sealed class PdfGateConventionTests
{
    /// <summary>Папки с кодом приложения. Tests/ не сканируется: стенды там зовут PDFium сами.</summary>
    private static readonly string[] SourceDirs =
    [
        "Controls", "Core", "Database", "Extensions", "Integrations",
        "Logging", "Notifications", "Settings", "Setup", "Utils", "ViewModels", "Views",
    ];

    /// <summary>
    /// Где PDFium зовется напрямую, и все эти места идут через очередь: источник страниц панели
    /// чтения, миниатюры ленты, впечатывание знака. Сама очередь упоминает PDFium в описании.
    /// </summary>
    private static readonly string[] PdfiumFiles =
    [
        Path.Combine("Core", "Documents", "PdfGate.cs"),
        Path.Combine("Core", "Documents", "DocPageSource.cs"),
        Path.Combine("Core", "Documents", "DocThumbnail.cs"),
        Path.Combine("Extensions", "PdfWatermark", "WatermarkStamp.cs"),
    ];

    // «using var x = PdfGate.Run(…)» и «using (var x = PdfGate.Run(…))».
    private static readonly Regex ClosedAfterGate =
        new(@"using\s*\(?\s*var\s+\w+\s*=\s*PdfGate\.Run\(", RegexOptions.Compiled);

    [Fact]
    public void Nothing_taken_from_the_gate_is_closed_after_it()
    {
        var offenders = Sources(RepoRoot())
            .Where(s => ClosedAfterGate.IsMatch(s.Text))
            .Select(s => s.Path)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Объект PDFium взят под замком PdfGate, а закрывается using уже после него — мимо очереди. " +
            "Закрывайте его внутри PdfGate.Run (using внутри лямбды) или через PdfGate.Run(x.Dispose): " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Pdfium_is_called_only_from_known_files()
    {
        var callers = Sources(RepoRoot())
            .Where(s => s.Text.Contains("DocLib.Instance", StringComparison.Ordinal))
            .Select(s => s.Path)
            .Where(p => !PdfiumFiles.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(callers.Count == 0,
            "PDFium зовется из нового места. Проверьте, что каждый вызов, включая закрытие, идет " +
            "через PdfGate, и допишите файл в PdfiumFiles: " + string.Join(", ", callers));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // тест бесполезен, если корень репозитория не найден
        return dir!.FullName;
    }

    private static IEnumerable<(string Path, string Text)> Sources(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
            yield return (Path.GetRelativePath(root, file), File.ReadAllText(file));

        foreach (var dir in SourceDirs)
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                yield return (Path.GetRelativePath(root, file), File.ReadAllText(file));
        }
    }
}
