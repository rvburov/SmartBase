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

    /// <summary>
    /// Копилка своих правок для триггера «изменение данных». Принадлежит службе: подписка на
    /// шину живет ровно столько же, сколько таймер, и забыть отписаться нельзя. Открыта наружу
    /// ради тестов — так триггер проверяется без настоящих коммитов.
    /// </summary>
    public ChangeWatch Changes { get; } = new();

    private readonly IntegrationsRegistry                 _cards;
    private readonly Func<IReadOnlyList<DatabaseManager>> _databases;
    private readonly IReadOnlyList<IExtension>            _extensions;
    private readonly Action<string>                       _notice;

    private DispatcherTimer? _timer;
    private bool             _ticking;   // запуски не накладываются; все входы с UI-потока

    // Сценарии «при запуске программы», которым этот заход еще не отработал. Набор берется ОДИН
    // раз, при старте службы: заведенный позже сценарий сработает в следующий заход, а не
    // немедленно — «при запуске» с тех пор не случалось.
    //
    // Набор, а не флаг «уже отработали», потому что сценарию может что-то мешать: база не
    // смонтирована, соединение не поднялось. Такой тик пропускается молча, и сценарий остается в
    // наборе — догон работает так же, как у расписания.
    private readonly HashSet<int> _startupLeft = [];

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

        // Подписка на шину своих правок — вместе с таймером: решает-то по ним все равно тик, и
        // копить правки, пока служба не работает, значило бы завести сценарий пачкой при первом
        // же возвращении окна из трея.
        Changes.Attach();

        PrimeStartup();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(TickSeconds) };
        _timer.Tick += (_, _) => _ = TickAsync(DateTimeOffset.Now);
        _timer.Start();

        var scenarios = _cards.Scenarios();
        var automatic = scenarios.Count(s => s.Automatic);
        var watching  = scenarios.Count(s => s.Automatic && s.Trigger.WatchesData);

        Log.Information("Сценарии: служба запущена, сами ходят {Count} (из них по правкам {Watching})",
                        automatic, watching);
    }

    /// <summary>
    /// Запомнить сценарии «при запуске программы» — те, что должны отработать за этот заход.
    ///
    /// Отдельным методом, а не строкой внутри <see cref="Start"/>, потому что <c>Start</c>
    /// заводит таймер на UI-потоке, а проверять триггер надо и без него.
    /// </summary>
    public void PrimeStartup()
    {
        _startupLeft.Clear();

        foreach (var scenario in _cards.Scenarios())
            if (scenario.Automatic && scenario.Trigger.OnStart)
                _startupLeft.Add(scenario.Id);
    }

    public void Dispose()
    {
        Changes.Dispose();

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
                if (!Due(scenario, now))  continue;

                if (!await AutomaticAsync(scenario, now, ct)) continue;

                ran++;

                // Отработал — из набора «при запуске» уходит: второй раз за один заход
                // программы этот триггер не срабатывает.
                if (scenario.Trigger.OnStart) _startupLeft.Remove(scenario.Id);
            }
        }
        finally
        {
            _ticking = false;
        }

        return ran;
    }

    // ── Пора ли ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Пора ли заводить сценарий. У расписания ответ дает сам триггер, у «изменения данных» —
    /// копилка правок, у календаря — наступившие события: расписание считается от часов, а
    /// правки и события приходят когда придут.
    /// </summary>
    private bool Due(Scenario scenario, DateTimeOffset now) =>
        scenario.Trigger.OnStart         ? _startupLeft.Contains(scenario.Id)
        : scenario.Trigger.WatchesData     ? Stirred(scenario, now)
        : scenario.Trigger.WatchesCalendar ? UpcomingEvents(scenario, now).Count > 0
        : scenario.Trigger.IsDue(_cards.ScenarioLastRun(scenario.Id), now);

    /// <summary>
    /// События, до начала которых осталось упреждение триггера, — те, чей момент
    /// «начало − упреждение» попал в окно (прошлый запуск, сейчас]. Окно считается от отметки
    /// о запуске, поэтому событие заводит сценарий ОДИН раз, а пропущенное за выключенной
    /// программой догоняется одним запуском со всеми накопившимися событиями.
    ///
    /// Календарь живет в местном времени без смещения — тем же, что у службы уведомлений.
    /// </summary>
    private List<Core.Models.CalendarRecord> UpcomingEvents(Scenario scenario, DateTimeOffset now)
    {
        var trigger = scenario.Trigger;

        if (_cards.ScenarioLastRun(scenario.Id) is not { } last) return [];

        if (_databases().FirstOrDefault(d => d.DbId == trigger.DbId) is not { } database)
            return [];

        var lead = TimeSpan.FromMinutes(Math.Clamp(trigger.Minutes, 0, ScenarioTrigger.MaxMinutes));
        var from = last.ToLocalTime().DateTime + lead;
        var to   = now.ToLocalTime().DateTime + lead;

        if (to <= from) return [];

        return database.Constructor
            .GetCalendarRecordsByRange(from, to, new HashSet<int> { trigger.CollectionId })
            .Where(r => r.StartDt > from && r.StartDt <= to)
            // Одна запись дает событие на каждое видимое поле-дату — ключ (запись, поле).
            .GroupBy(r => (r.Id, r.FieldId))
            .Select(g => g.First())
            .OrderBy(r => r.StartDt)
            .Take(StepTable.MaxRows)
            .ToList();
    }

    /// <summary>
    /// Наступившие события — строками для первого шага: название, время и коллекция, как их
    /// видит человек в календаре. Даты — местные, «дд.ММ.гггг чч:мм»; у событий на весь день —
    /// без времени.
    /// </summary>
    private static StepTable EventsTable(IReadOnlyList<Core.Models.CalendarRecord> events)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>(events.Count);

        foreach (var record in events)
            rows.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Название"]  = record.DisplayTitle,
                ["Начало"]    = Stamp(record.StartDt, record.AllDay),
                ["Конец"]     = Stamp(record.EndDt, record.AllDay),
                ["Весь день"] = record.AllDay ? "да" : "нет",
                ["Коллекция"] = record.CollectionName,
            });

        return new StepTable(["Название", "Начало", "Конец", "Весь день", "Коллекция"], rows);
    }

    private static string Stamp(DateTime moment, bool allDay) =>
        moment == default
            ? ""
            : moment.ToString(allDay ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm",
                              System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Менялись ли данные — и утихли ли правки.
    ///
    /// <b>Ждем ТИШИНЫ, а не просто паузы.</b> Отсчет идет от ПОСЛЕДНЕЙ правки, поэтому правка
    /// десяти строк подряд дает один обмен, а не десять, и загрузка на тысячу записей уходит
    /// одним куском после того, как закончится. Плата за это названа честно: пока правки идут
    /// непрерывно, обмен ждет. Это и задумано — отправлять посреди загрузки значит отправлять
    /// половину.
    /// </summary>
    private bool Stirred(Scenario scenario, DateTimeOffset now)
    {
        if (Watched(scenario) is not { } spot)                                 return false;
        if (Changes.LastChange(spot.Path, spot.Collection) is not { } last)     return false;

        var settle = Math.Clamp(scenario.Trigger.Minutes,
                                ScenarioTrigger.MinMinutes, ScenarioTrigger.MaxMinutes);

        return now - last >= TimeSpan.FromMinutes(settle);
    }

    /// <summary>
    /// За чем следим: путь базы и имя коллекции. <c>null</c> — база закрыта или коллекции больше
    /// нет; тогда сценарий просто не заводится, как и всякий другой с закрытой базой.
    ///
    /// Имя, а не ключ, потому что шина изменений говорит именами: так их пишет журнал изменений.
    /// Переименовали коллекцию — триггер поедет за ней сам, ключ-то остался прежним.
    /// </summary>
    private (string Path, string Collection)? Watched(Scenario scenario)
    {
        var trigger = scenario.Trigger;

        if (!trigger.WatchesData) return null;

        if (_databases().FirstOrDefault(d => d.DbId == trigger.DbId) is not { } database)
            return null;

        var collection = database.Constructor.CollectionName(trigger.CollectionId);

        return collection.Length > 0 ? (database.Path, collection) : null;
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

        // События календарного триггера считаются ДО отметки о запуске: отметка сдвигает
        // окно, из которого они взяты, и посчитанное после нее было бы пустым.
        StepTable? initial = null;

        if (scenario.Trigger.WatchesCalendar)
        {
            var due = UpcomingEvents(scenario, now);
            if (due.Count == 0) return false;   // событие исчезло между «пора» и запуском

            initial = EventsTable(due);
        }

        // Отметка — до работы, моментом начала: период считается от старта, а не от финиша.
        _cards.MarkScenarioRun(scenario.Id, now);

        // Правки отработаны — забываем их тоже В НАЧАЛЕ. Правка, пришедшая во время работы,
        // обязана завести сценарий заново: данные-то стали другими уже после того, как их
        // прочитали.
        if (Watched(scenario) is { } spot) Changes.Forget(spot.Path, spot.Collection);

        ScenarioOutcome outcome;
        try
        {
            outcome = await ScenarioRunner.RunAsync(scenario, _cards, databases, _extensions,
                                                    now, _notice, ct, initial);
        }
        catch (Exception failure)
        {
            // Непредвиденная беда одного сценария не должна уносить с собой остальные и службу.
            Complain(scenario, failure.Message);
            return true;
        }

        // Работы не нашлось — папка обмена пуста. Ни строки, ни события: экрану нечего
        // обновлять, а «Диагностика» иначе к утру состояла бы из одного этого сценария.
        if (outcome.Idle) return true;

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
