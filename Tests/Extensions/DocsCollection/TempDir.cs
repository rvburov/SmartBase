namespace SmartBase.Tests.Extensions.DocsCollection;

/// <summary>Одноразовая временная папка с документами. Локальная копия обвязки ядра — чтобы
/// удаление расширения уносило и её.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbext_docs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Создать пустой файл (содержимое расширению не важно — оно берёт только имя,
    /// папку, формат и путь). <paramref name="relative"/> может содержать подпапки.</summary>
    public string Touch(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort */ }
    }
}

/// <summary>Синхронный приёмник прогресса: штатный <see cref="Progress{T}"/> публикует колбэк
/// в пул потоков, и к возврату из метода часть отчётов может быть ещё не доставлена.</summary>
internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
