namespace SmartBase.Tests.Extensions.DocsCollection;

/// <summary>Одноразовая временная папка с документами. Локальная копия обвязки ядра — чтобы
/// удаление расширения уносило и ее.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sbext_docs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Создать пустой файл (содержимое расширению не важно — оно берет только имя,
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

/// <summary>Синхронный приемник прогресса: штатный <see cref="Progress{T}"/> публикует колбэк
/// в пул потоков, и к возврату из метода часть отчетов может быть еще не доставлена.</summary>
internal sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
