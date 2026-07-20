namespace SmartBase.Tests.Integration;

/// <summary>Одноразовая временная папка для файловых фикстур (CSV/XLSX/JSON, экспорт).
/// Рекурсивно удаляется при <see cref="Dispose"/>.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbtestdir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Полный путь к файлу с указанным именем внутри папки.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort */ }
    }
}
