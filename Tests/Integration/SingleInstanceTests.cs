using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Единственный экземпляр на папку данных (нужен фоновому режиму): второй захват лока не
/// удается, пока жив первый; освобождение лока дает захватить снова; второй экземпляр умеет
/// оставить сигнал «показать окно». На temp-папке — реальный лок-файл (FileShare.None).
/// </summary>
public sealed class SingleInstanceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sbinst_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Second_acquire_fails_while_first_alive_then_succeeds_after_release()
    {
        var dir = NewTempDir();
        try
        {
            var first = SingleInstance.TryAcquire(dir);
            Assert.NotNull(first);

            // Пока первый держит лок — второй не захватит.
            Assert.Null(SingleInstance.TryAcquire(dir));

            first!.Dispose();

            // Лок освобожден — снова можно стать единственным.
            var again = SingleInstance.TryAcquire(dir);
            Assert.NotNull(again);
            again!.Dispose();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void SignalShow_writes_the_signal_file()
    {
        var dir = NewTempDir();
        try
        {
            SingleInstance.SignalShow(dir);
            Assert.True(File.Exists(Path.Combine(dir, "show.signal")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
