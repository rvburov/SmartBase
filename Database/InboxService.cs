using Avalonia.Threading;
using Serilog;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Database;

/// <summary>
/// Служба приема: раз в полминуты заглядывает в папку обмена и разбирает то, что там появилось.
///
/// Без нее канал остается ручным, а его смысл — в обратном: файл кладут, когда удобно
/// отправителю, а разбирается он сам. Расписание тут не украшение, а половина ценности.
///
/// Тик идет на UI-потоке (<see cref="DispatcherTimer"/>, как у службы уведомлений и
/// резервного копирования): настройки живут в общем контексте <c>app.db</c>, а его трогают
/// только оттуда. Сам обход — в <c>Task.Run</c>, потому что он ходит на сетевой диск и пишет
/// записи; обходы строго по одному.
///
/// <b>Три правила боевой среды</b>, без которых служба вредит больше, чем помогает:
///
/// <list type="number">
///   <item><b>База недоступна — не запускаемся.</b> Целевая база берется из списка открытых:
///   потерянную базу монитор доступности размонтирует, и она из списка пропадает сама. Ходить
///   в папку, не имея куда положить, незачем — файлы только зря уедут в «Ошибки».</item>
///   <item><b>После сбоя — пауза.</b> Диск <c>P:</c> пропал на ночь; без паузы к утру в журнале
///   будет две тысячи одинаковых строк, среди которых не найти ничего другого.</item>
///   <item><b>Одна и та же беда пишется в журнал один раз.</b> Повторяется — молчим до тех пор,
///   пока не изменится причина или не пройдет обмен.</item>
/// </list>
/// </summary>
public sealed class InboxService : IDisposable
{
    /// <summary>Как часто заглядывать. Полминуты — компромисс из плана: обмен файлами не про
    /// мгновенность, а лишний обход мертвого сетевого диска стоит дорого.</summary>
    public const int TickSeconds = 30;

    /// <summary>
    /// Пауза после неудачного обхода. Пять минут: сетевой диск возвращается не мгновенно, а
    /// долбиться в него каждые полминуты — верный способ засорить журнал и подвесить поток
    /// на неотменяемом вводе-выводе.
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Обход закончен и что-то тронул. UI-поток; экран обновляет строку итога.</summary>
    public event Action<InboxRun>? Swept;

    private readonly Func<IReadOnlyList<DatabaseManager>> _databases;

    private DispatcherTimer? _timer;
    private bool             _ticking;      // обходы не накладываются; все входы с UI-потока
    private DateTime         _pausedUntil;  // бэкоф после сбоя
    private string           _lastProblem = "";   // о чем уже сказали в журнал

    public InboxService(Func<IReadOnlyList<DatabaseManager>> databases) => _databases = databases;

    /// <summary>
    /// Запуск из UI-потока. Идемпотентен: окно возвращается из трея снова и снова, и второй
    /// таймер тут не нужен.
    /// </summary>
    public void Start()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => _ = TickAsync();
        _timer.Start();

        Log.Information("Прием файлов: служба запущена, автоматический разбор {State:l}",
                        AppSettings.LoadInboxAuto() ? "включен" : "выключен");
    }

    public void Dispose()
    {
        if (_timer is null) return;

        _timer.Stop();
        _timer = null;
        Log.Information("Прием файлов: служба остановлена");
    }

    /// <summary>
    /// Разобрать папку немедленно — кнопка «Проверить папку». Пауза после сбоя не действует:
    /// человек попросил явно, и отвечать ему молчанием потому, что полчаса назад отвалился
    /// диск, неправильно.
    /// </summary>
    public Task<InboxRun> RunNowAsync(CancellationToken ct = default) =>
        SweepAsync(manual: true, ct);

    // ── Тик ───────────────────────────────────────────────────────────────────

    private async Task TickAsync()
    {
        if (_ticking || !AppSettings.LoadInboxAuto()) return;
        if (DateTime.UtcNow < _pausedUntil) return;

        _ticking = true;
        try
        {
            var run = await SweepAsync(manual: false, CancellationToken.None);

            // Молчим, когда ничего не нашли: за сутки это две с половиной тысячи строк
            // «новых файлов нет», в которых потом не найти настоящую.
            if (run.Files > 0 || run.Reclaimed > 0)
                Log.Information("Прием файлов: {Summary:l}", run.Summary());
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>
    /// Один обход. Настройки читаются на UI-потоке (общий контекст <c>app.db</c>), а работа —
    /// в фоне: и папка, и записи живут на диске, до которого с UI-потока ходить нельзя.
    /// </summary>
    private async Task<InboxRun> SweepAsync(bool manual, CancellationToken ct)
    {
        var folder = AppSettings.LoadInboxFolder();
        var dbId   = AppSettings.LoadInboxDb();

        if (folder.Length == 0) return InboxRun.Failed("папка обмена не выбрана");
        if (dbId <= 0)          return InboxRun.Failed("база данных для приема не выбрана");

        // Целевая база — из живого списка. Потерянную монитор доступности размонтирует, и она
        // отсюда пропадает: правило «база недоступна — не запускаемся» держится само.
        var target = _databases().FirstOrDefault(d => d.DbId == dbId);

        if (target is null)
        {
            var problem = "база данных для приема сейчас недоступна";
            Pause(manual, problem);
            return InboxRun.Failed(problem);
        }

        var repo    = target.Constructor;
        var machine = Environment.MachineName;
        var now     = DateTimeOffset.Now;

        InboxRun run;
        try
        {
            run = await Task.Run(() =>
            {
                // Открытие папки идет в фоне намеренно: на пропавшем сетевом диске даже
                // проверка существования висит секундами, а UI-поток этого не переживает.
                var layout = InboxFolder.Open(folder, out var explanation);

                if (layout is null) throw new InboxException(explanation);

                return InboxReceiver.Run(layout, repo, dbId, machine, now, ct);
            });
        }
        catch (Exception failure)
        {
            Pause(manual, failure.Message);
            return InboxRun.Failed(failure.Message);
        }

        _pausedUntil = default;
        _lastProblem = "";

        if (run.Files > 0 || run.Reclaimed > 0) Swept?.Invoke(run);

        return run;
    }

    /// <summary>
    /// Отложить следующий обход и сказать о причине — но только если она новая. Ночь без
    /// сетевого диска иначе оставляет к утру тысячу одинаковых строк.
    /// </summary>
    private void Pause(bool manual, string problem)
    {
        if (!manual) _pausedUntil = DateTime.UtcNow + RetryDelay;

        if (problem == _lastProblem) return;

        _lastProblem = problem;
        Log.Warning("Прием файлов отложен: {Reason:l}", problem);
    }
}
