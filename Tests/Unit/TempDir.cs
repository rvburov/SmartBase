namespace SmartBase.Tests.Unit;

/// <summary>Одноразовая временная папка для тестов планировщиков (уникализация путей).
/// Локальная копия — чтобы unit-проект не тянул интеграционную обвязку.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbunit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort */ }
    }
}
