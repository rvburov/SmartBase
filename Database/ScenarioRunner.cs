using System.Diagnostics;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>Итог одного шага: удался ли и что о нем сказать человеку.</summary>
/// <param name="Ok">Шаг сделал свое дело.</param>
/// <param name="Line">Строка для отчета и журнала; при неудаче — причина.</param>
/// <param name="Rows">Сколько записей прошло через шаг.</param>
public sealed record StepOutcome(bool Ok, string Line, int Rows = 0)
{
    public static StepOutcome Bad(string reason) => new(false, reason);
}

/// <summary>
/// Итог сценария целиком.
/// </summary>
/// <param name="Ok">Все шаги сделаны.</param>
/// <param name="Problem">Почему остановились; пусто — дошли до конца.</param>
/// <param name="Done">Сколько шагов сделано.</param>
/// <param name="Total">Сколько шагов было.</param>
/// <param name="Rows">Сколько записей прошло суммарно.</param>
/// <param name="Elapsed">Сколько заняло.</param>
/// <param name="Lines">Построчно — что было на каждом шаге.</param>
/// <param name="Started">Дело дошло хотя бы до первого шага. Отличать это от «первый шаг не
/// удался» обязательно: причины у них разные — там не сложилось окружение, здесь не сработал
/// сам обмен, — и человек по этим двум случаям идет в разные места.</param>
public sealed record ScenarioOutcome(
    bool                  Ok,
    string                Problem,
    int                   Done,
    int                   Total,
    int                   Rows,
    TimeSpan              Elapsed,
    IReadOnlyList<string> Lines,
    bool                  Started = true)
{
    /// <summary>Сценарий не запускался вовсе — не сложилось до первого шага.</summary>
    public static ScenarioOutcome Blocked(string problem, int total) =>
        new(false, problem, 0, total, 0, TimeSpan.Zero, [], Started: false);

    /// <summary>Итог одной строкой — для журнала, списка и карточки.</summary>
    public string Summary() =>
        Problem.Length > 0
            ? (Started ? $"остановлен на шаге {Done + 1} из {Total}: " : "не запускался: ") + Problem
            : $"шагов выполнено {Done}, записей {Rows}";
}

/// <summary>
/// Выполнение сценария — шаги по порядку, без единого окна (docs/ROADMAP-API.md, раздел 4).
///
/// Отдельно от службы-таймера, как <c>InboxReceiver</c> отдельно от <c>InboxService</c>: здесь
/// «что делать», там «когда». Ради этого же разделения сюда приходят параметрами и открытые
/// базы, и момент, и способ показать сообщение — на часы, окна и глобальные списки этот класс
/// не смотрит и проверяется тестами целиком.
///
/// <b>Первый неудавшийся шаг останавливает сценарий.</b> Правило, а не настройка: шаги идут
/// цепочкой, и следующий работал бы с данными, которых не пришло. Отказ виден в журнале и в
/// отчете, а сообщать о нем — дело того, кто сценарий завел.
///
/// <b>Вызывать нужно с UI-потока.</b> Реестр (<see cref="IntegrationsRegistry"/>) живет на общем
/// контексте app.db, который принадлежит интерфейсу; тяжелое — разбор ответа и запись в
/// коллекцию — уходит в <c>Task.Run</c> на отдельном подключении, как и в ручном обмене.
/// </summary>
public static class ScenarioRunner
{
    /// <summary>
    /// Что мешает сценарию отработать прямо сейчас; <c>null</c> — ничто.
    ///
    /// Ответ один, а последствия разные, и в этом весь смысл отдельного метода: службе он
    /// говорит «пропусти этот тик молча» (база откроется — сценарий догонит пропущенное), а
    /// человеку, нажавшему «Запустить сейчас», — почему ничего не произошло. Молчаливое
    /// бездействие в ответ на нажатие хуже любого отказа.
    /// </summary>
    public static string? Blocker(Scenario                       scenario,
                                  IntegrationsRegistry           registry,
                                  IReadOnlyList<DatabaseManager> databases)
    {
        if (scenario.Steps.Count == 0) return "в сценарии нет шагов";

        foreach (var step in scenario.Steps)
        {
            if (step.Kind != ScenarioStepKind.Request) continue;

            if (registry.FindRequest(step.RequestId) is not { } request)
                return $"запрос шага не найден (№{step.RequestId})";

            if (registry.Find(request.ConnectionId) is null)
                return $"у запроса «{request.Name}» не найдено соединение";

            if (registry.Binding(request.Id) is not { } binding)
                return NoBinding(request);

            if (databases.All(d => d.DbId != binding.DbId))
                return $"база данных для запроса «{request.Name}» сейчас не открыта";
        }

        return null;
    }

    /// <summary>
    /// Выполнить сценарий. <paramref name="notice"/> — как показать сообщение человеку;
    /// <paramref name="now"/> — момент для журнала.
    ///
    /// Отмену бросает наружу: остановил человек — итога у такого запуска нет, и притворяться,
    /// что сценарий чем-то кончился, незачем.
    /// </summary>
    public static async Task<ScenarioOutcome> RunAsync(Scenario                       scenario,
                                                       IntegrationsRegistry           registry,
                                                       IReadOnlyList<DatabaseManager> databases,
                                                       DateTimeOffset                 now,
                                                       Action<string>                 notice,
                                                       CancellationToken              ct = default)
    {
        var clock = Stopwatch.StartNew();
        var total = scenario.Steps.Count;

        if (Blocker(scenario, registry, databases) is { } blocker)
        {
            Journal(scenario, ok: false, blocker, rows: 0, clock.Elapsed, DbOf(scenario, registry), now);
            return ScenarioOutcome.Blocked(blocker, total);
        }

        var lines   = new List<string>();
        var rows    = 0;
        var done    = 0;
        var problem = "";

        foreach (var step in scenario.Steps)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = step.Kind == ScenarioStepKind.Request
                ? await RequestStepAsync(step, registry, databases, ct)
                : NoticeStep(step, notice);

            lines.Add(outcome.Line);
            rows += outcome.Rows;

            // Первый неудавшийся шаг останавливает цепочку: следующий работал бы с данными,
            // которых не пришло.
            if (!outcome.Ok) { problem = outcome.Line; break; }

            done++;
        }

        var ok = problem.Length == 0;
        var result = new ScenarioOutcome(ok, problem, done, total, rows, clock.Elapsed, lines);

        Journal(scenario, ok, ok ? result.Summary() : problem, rows, clock.Elapsed,
                DbOf(scenario, registry), now);

        return result;
    }

    // ── Шаги ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Запрос в коллекцию — то же самое, что делает кнопка «В коллекцию», только без окон.
    ///
    /// Раскладку берет привязка КАРТОЧКИ ЗАПРОСА: та, которую человек однажды настроил и
    /// проверил глазами. Автомат не выдумывает соответствие колонок — ошибку в угаданном имени
    /// поля видно только по пустой коллекции через неделю.
    /// </summary>
    private static async Task<StepOutcome> RequestStepAsync(ScenarioStep                   step,
                                                            IntegrationsRegistry           registry,
                                                            IReadOnlyList<DatabaseManager> databases,
                                                            CancellationToken              ct)
    {
        // Повторная проверка после Blocker: между ними человек мог удалить карточку, а
        // рассчитывать на то, что «только что все было», в фоновой работе нельзя.
        if (registry.FindRequest(step.RequestId) is not { } request)
            return StepOutcome.Bad($"запрос шага не найден (№{step.RequestId})");

        if (registry.Find(request.ConnectionId) is not { } connection)
            return StepOutcome.Bad($"у запроса «{request.Name}» не найдено соединение");

        if (registry.Binding(request.Id) is not { } binding)
            return StepOutcome.Bad(NoBinding(request));

        if (databases.FirstOrDefault(d => d.DbId == binding.DbId) is not { } target)
            return StepOutcome.Bad($"база данных для запроса «{request.Name}» сейчас не открыта");

        // Граница инкрементальной выгрузки — момент НАЧАЛА запроса, а не его конца: записи,
        // изменившиеся в чужой программе, пока шел обмен, иначе оказались бы позади границы.
        var startedAt = DateTimeOffset.UtcNow;

        var outcome = await RequestRunner.RunAsync(
            connection, request, registry.Secret(connection.Id),
            VariableContext.Now(registry.LastRun(request.Id), connection.Address), ct);

        // Строка журнала пишется и для НЕУДАЧНОГО запроса: именно их ищут, разбирая «почему
        // записи не появились». В расход идет только то, что дошло до сети.
        RunJournal.Add(RunEntry.New(
            RunChannel.Request, request.Name, outcome.SafeAddress, outcome.Ok, outcome.Explanation,
            outcome.Rows, outcome.Bytes, outcome.Elapsed, binding.DbId, startedAt,
            counts: outcome.Sent));

        if (!outcome.Ok)
            return StepOutcome.Bad($"запрос «{request.Name}»: {outcome.Explanation}");

        var source = new JsonTextSource(outcome.Json);

        ImportSchema schema;
        try
        {
            // Разбор и укладка — в фоне и на отдельном подключении: общий контекст базы
            // принадлежит интерфейсу, и трогать его из фоновой работы нельзя.
            schema = await Task.Run(() => source.Analyze(null, ct), ct);
        }
        catch (ImportException error)
        {
            return StepOutcome.Bad($"ответ запроса «{request.Name}» не разобрался: {error.Message}");
        }

        BindingResult result;
        try
        {
            result = await Task.Run(
                () => target.Constructor.ApplyRowsIsolated(binding, schema, source.ReadRows(ct), ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return StepOutcome.Bad($"запрос «{request.Name}»: перенести не удалось — {error.Message}");
        }

        // Отметка о последнем удачном запуске — только если ответ пришел ЦЕЛИКОМ. Сбор,
        // остановленный потолком строк, оставляет данные за границей: сдвинь ее — и остаток не
        // придет уже никогда.
        if (!outcome.Capped) registry.MarkRun(request.Id, startedAt);

        var line = $"Запрос «{request.Name}»: {result.Summary()}";

        if (outcome.Capped)
            line += ". Ответ пришел не целиком — отметка о последнем запуске не сдвинута, "
                  + "остаток придет следующим запуском";

        return new StepOutcome(true, line, result.Created + result.Updated);
    }

    /// <summary>Сообщение человеку. Единственный шаг, который ничего не меняет.</summary>
    private static StepOutcome NoticeStep(ScenarioStep step, Action<string> notice)
    {
        notice(step.Text);
        return new StepOutcome(true, "Сообщение: " + step.Text);
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Почему сценарий не тронет запрос без привязки. Текст один на все места: и служба, и
    /// экран объясняют это одинаково, а объяснение здесь — половина дела.
    /// </summary>
    private static string NoBinding(IntegrationRequest request) =>
        $"у запроса «{request.Name}» не настроена привязка к коллекции: откройте раздел "
        + "«Интеграции», нажмите у него «В коллекцию» и разложите колонки — сценарий повторяет "
        + "разложенное, но не придумывает его сам";

    /// <summary>
    /// Какую базу задел сценарий — по первому шагу с привязкой. Нужно колонке журнала: разбор
    /// «что пришло в эту базу» иначе не сведется.
    /// </summary>
    private static int DbOf(Scenario scenario, IntegrationsRegistry registry)
    {
        foreach (var step in scenario.Steps)
        {
            if (step.Kind != ScenarioStepKind.Request) continue;
            if (registry.Binding(step.RequestId) is { } binding) return binding.DbId;
        }

        return 0;
    }

    /// <summary>
    /// Итоговая строка сценария в журнале. Пишется ВСЕГДА, даже когда до первого шага не дошло:
    /// у такой неудачи своей строки нет вовсе, и без этой причина не видна нигде.
    ///
    /// В накопительный расход не идет: его уже заняли строки шагов.
    /// </summary>
    private static void Journal(Scenario scenario, bool ok, string explanation, int rows,
                                TimeSpan elapsed, int dbId, DateTimeOffset now) =>
        RunJournal.Add(RunEntry.New(RunChannel.Scenario, scenario.Name, "", ok, explanation,
                                    rows, 0, elapsed, dbId, now, counts: false));
}
