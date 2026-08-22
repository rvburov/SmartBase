namespace SmartBase.Tests.Extensions.PdfRename;

/// <summary>Одноразовая временная папка теста (уникализация путей). Локальная копия — чтобы
/// тесты расширения не тянули обвязку ядра: удаление расширения уносит и эту копию.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbext_pdfrename_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Создать в папке пустой файл с заданным именем (содержимое расширению не важно —
    /// оно работает только с именами).</summary>
    public string Touch(string fileName)
    {
        var full = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(full, "");
        return full;
    }

    /// <summary>Имена файлов в папке, отсортированные — для сравнения «что лежит на диске».</summary>
    public string[] Names() =>
        [.. Directory.EnumerateFiles(Path)
                     .Select(System.IO.Path.GetFileName)
                     .Select(n => n!)
                     .OrderBy(n => n, StringComparer.Ordinal)];

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort */ }
    }
}
