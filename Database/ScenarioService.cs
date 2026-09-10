using Avalonia.Threading;
using Serilog;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Служба сценариев: раз в полминуты смотрит, кому пора, и заводит их.
///
/// Разделение то же, что у приема: <see cref="ScenarioRunner"/> знает «что делать», эта служба —
/// «когда». Тик идет на UI-потоке (<see cref="DispatcherTimer"/>, как у резервного копирования и
/// приема файлов): реестр сценариев живет в общем контексте <c>app.db</c>, а его трогают только
/// оттуда; сеть и запись в коллекцию исполнитель уводит в фон сам.
///
/// <b>Отметка о запуске ставится в НАЧАЛЕ, а не в конце.</b> Иначе долгий обмен растягивал бы
/// период («каждые 30 минут» превратилось бы в «через 30 минут после конца»), а падение посреди
/// работы повторяло бы запуск немедленно.
///
/// <b>Сценарий, которому что-то мешает, тик пропускает молча.</b> База закрыта, привязки нет,
/// соединение потеряно — журнал не заполняется одинаковыми строками каждые полминуты, а в
/// «Диагностику» уходит одна строка на каждую новую беду. Причина при этом не теряется: ее
/// показывает экран сценария, спрашивая <see cref="ScenarioRunner.Blocker"/> вживую, и она же
/// придет ответом на «Запустить сейчас».
///
/// Пропущенное догоняется однократно — это свойство самого расписания
/// (<see cref="ScenarioTrigger.IsDue"/>), а не службы.
/// </summary>
public sealed class ScenarioService : IDisposable
{
    /// <summary>
    /// Как часто смотреть на часы. Полминуты: расписание задается минутами, и опаздывать на
    /// целую минуту к «ежедневно в 07:00» незачем.
    /// </summary>
    public const int TickSeconds = 30;

    /// <summary>Сценарий отработал. UI-поток; экран обновляет журнал и список.</summary>
    public event Action<Scenario, ScenarioOutcome>? Ran;

    private readonly IntegrationsRegistry                 _cards;
    private readonly Func<IReadOnlyList<DatabaseManager>> _databases;
    private readonly IReadOnlyList<IExtension>            _extensions;
    private readonly Action<string>                       _notice;

    private DispatcherTimer? _timer;
    private bool             _ticking;   // запуски не накладываются; все входы с UI-потока

    // О чем уже сказали в «Диагностику», по сценарию. Ночь без сетевой базы иначе оставляет к
    // утру тысячу одинаковых строк.
    private readonly Dictionary<int, string> _said = [];

    /// <param name="cards">Реестр интеграций — сценарии, запросы, соединения.</param>
    /// <param name="databases">Открытые базы на момент тика.</param>
    /// <param name="extensions">Расширения, чьи действия можно поставить шагом. Список приходит
    /// параметром, а не берется из глобального реестра: службу проверяют тестами, а тест с
    /// зашитым списком расширений проверял бы заодно и его состав.</param>
    /// <param name="notice">Как показать сообщение человеку.</param>
    public ScenarioService(IntegrationsRegistry                 cards,
                           Func<IReadOnlyList<DatabaseManager>> databases,
                           IReadOnlyList<IExtension>            extensions,
                           Action<string>                       notice)
    {
        _cards      = cards;
        _databases  = databases;
        _extensions = extensions;
        _notice     = notice;
    }

    /// <summary>Запуск из UI-потока. Идемпотентен: окно возвращается из трея снова и снова.</summary>
    public void Start()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => _ = TickAsync(DateTimeOffset.Now);
        _timer.Start();

        var automatic = _cards.Scenarios().Count(s => s.Automatic);

        Log.Information("Сценарии: служба запущена, по расписанию ходят {Count}", automatic);
    }

    public void Dispose()
    {
        if (_timer is null) return;

        _timer.Stop();
        _timer = null;
        Log.Information("Сценарии: служба остановлена");
    }

    // ── Тик ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Один проход по расписанию: кому пора — тот и идет. Возвращает, сколько сценариев
    /// отработало.
    ///
    /// Момент приходит параметром, а не берется с часов: так этот проход проверяется тестами
    /// без подведения системного времени — тем же приемом, что у <c>BackupPlanner</c>.
    /// </summary>
    public async Task<int> TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (_ticking) return 0;

        _ticking = true;
        var ran = 0;

        try
        {
            foreach (var scenario in _cards.Scenarios())
            {
                if (!scenario.Automatic) continue;
                if (!scenario.Trigger.IsDue(_cards.ScenarioLastRun(scenario.Id), now)) continue;

                if (await AutomaticAsync(scenario, now, ct)) ran++;
            }
        }
        finally
        {
            _ticking = false;
        }

        return ran;
    }

    /// <summary>
    /// Заводится ли сценарий сейчас. <c>false</c> — тик пропущен: помешало то, что назовет
    /// <see cref="ScenarioRunner.Blocker"/>, и отметка о запуске не двигается, чтобы пропущенное
    /// догналось, когда помеха уйдет.
    /// </summary>
    private async Task<bool> AutomaticAsync(Scenario scenario, DateTimeOffset now, CancellationToken ct)
    {
        var databases = _databases();

        if (ScenarioRunner.Blocker(scenario, _cards, databases, _extensions) is { } blocker)
        {
            Complain(scenario, blocker);
            return false;
        }

        // Отметка — до работы, моментом начала: период считается от старта, а не от финиша.
        _cards.MarkScenarioRun(scenario.Id, now);

        ScenarioOutcome outcome;
        try
        {
            outcome = await ScenarioRunner.RunAsync(scenario, _cards, databases, _extensions,
                                                    now, _notice, ct);
        }
        catch (Exception failure)
        {
            // Непредвиденная беда одного сценария не должна уносить с собой остальные и службу.
            Complain(scenario, failure.Message);
            return true;
        }

        if (outcome.Ok)
        {
            _said.Remove(scenario.Id);

            // Молчим о запусках, которые ничего не принесли: обмен раз в полчаса дает полсотни
            // строк в сутки, и настоящую среди них потом не найти.
            if (outcome.Rows > 0)
                Log.Information("Сценарий «{Name:l}»: {Summary:l}", scenario.Name, outcome.Summary());
        }
        else
        {
            Complain(scenario, outcome.Problem);
        }

        Ran?.Invoke(scenario, outcome);
        return true;
    }

    // ── Запуск руками ─────────────────────────────────────────────────────────

    /// <summary>
    /// Выполнить сценарий немедленно — кнопка «Запустить сейчас».
    ///
    /// Помеха здесь не пропускается молча, а объясняется: молчаливое бездействие в ответ на
    /// нажатие хуже любого отказа. Отметка о запуске НЕ двигается — расписание принадлежит
    /// сценарию, а не человеку, нажавшему кнопку: сдвинь ее, и проверка настройки в обед
    /// отменила бы вечерний обмен.
    /// </summary>
    public async Task<ScenarioOutcome> RunNowAsync(Scenario scenario, CancellationToken ct = default)
    {
        var outcome = await ScenarioRunner.RunAsync(
            scenario, _cards, _databases(), _extensions, DateTimeOffset.Now, _notice, ct);

        Ran?.Invoke(scenario, outcome);
        return outcome;
    }

    // ── Жалобы ────────────────────────────────────────────────────────────────

    /// <summary>Сказать о беде — но только если она новая для этого сценария.</summary>
    private void Complain(Scenario scenario, string problem)
    {
        if (_said.TryGetValue(scenario.Id, out var said) && said == problem) return;

        _said[scenario.Id] = problem;
        Log.Warning("Сценарий «{Name:l}» не отработал: {Reason:l}", scenario.Name, problem);
    }
}
