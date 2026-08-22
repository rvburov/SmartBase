namespace SmartBase.Tests.Extensions.TimeCollector;

/// <summary>Одноразовая временная папка теста (уникализация путей). Локальная копия — чтобы
/// тесты расширения не тянули обвязку ядра: удаление расширения уносит и эту копию.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbext_timecollector_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort */ }
    }
}
