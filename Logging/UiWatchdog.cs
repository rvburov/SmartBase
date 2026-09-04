using System.Diagnostics;
using Avalonia.Threading;
using Serilog;

namespace SmartBase.Logging;

/// <summary>
/// Сторож UI-потока: ловит зависания интерфейса и после оттаивания пишет в журнал, сколько
/// он не отвечал и что в это время выполнялось. Пороги медленности (<see cref="Diag"/>)
/// ловят «долго, но закончилось», сторож — «не отвечало вовсе»: обработчик, который держит
/// UI-поток секунды, ни одним замером может быть не обернут.
///
/// Устройство: <see cref="DispatcherTimer"/> на UI-потоке ставит отметку каждые полсекунды,
/// фоновый таймер раз в секунду сверяет ее с часами (<see cref="FreezeDetector"/>). Уровень
/// записи зависит от того, известно ли, что выполнялось: при активных операциях —
/// предупреждение; без них — сведения, потому что паузу без операций дают и перетаскивание
/// окна за заголовок (Win32 крутит собственный цикл сообщений), и сон компьютера.
///
/// Запускается один раз из MainWindow, живет весь сеанс.
/// </summary>
public sealed class UiWatchdog : IDisposable
{
    /// <summary>Интерфейс не отвечал дольше этого — зависание.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan BeatPeriod  = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(1);
    // Дольше этого без единой операции — почти наверняка не зависание, а сон компьютера.
    private static readonly TimeSpan LikelySleep = TimeSpan.FromMinutes(5);

    private readonly FreezeDetector _detector = new(Threshold);
    private readonly Stopwatch      _clock    = Stopwatch.StartNew();
    private readonly DispatcherTimer _beat;
    private readonly Timer           _check;

    public UiWatchdog()
    {
        _detector.Beat(_clock.Elapsed);
        _beat = new DispatcherTimer(BeatPeriod, DispatcherPriority.Normal, (_, _) => _detector.Beat(_clock.Elapsed));
        _beat.Start();
        _check = new Timer(_ => Check(), null, CheckPeriod, CheckPeriod);
    }

    private void Check()
    {
        if (_detector.Check(_clock.Elapsed, () => Diag.ActiveOperations) is not { } report) return;
        var seconds = report.Duration.TotalSeconds;
        if (report.Operations.Length > 0)
            Log.Warning("Интерфейс не отвечал {Seconds:N1} с — выполнялось: {Operations:l}", seconds, report.Operations);
        else if (report.Duration > LikelySleep)
            Log.Information("Интерфейс не отвечал {Duration:l} без операций — похоже на сон компьютера или паузу процесса",
                            Diag.Duration(report.Duration));
        else
            Log.Information("Интерфейс не отвечал {Seconds:N1} с без замеряемых операций (обработчик на UI-потоке, перетаскивание окна или системная пауза)",
                            seconds);
    }

    public void Dispose()
    {
        _beat.Stop();
        _check.Dispose();
    }
}
