using System.Diagnostics;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Таблица, которую шаг передает следующему: имена колонок и строки, разобранные по ним.
///
/// Это и есть то, что течет между шагами. Понадобилось оно ровно с появлением шага «действие»:
/// действию-операции нужна заполненная запись, а взять ее неоткуда, кроме как у предыдущего
/// шага. Раньше этого не было — и заводить поток данных «на будущее» было бы гаданием.
///
/// Строка — словарь «колонка → значение», а не список по позиции: шаг ссылается на колонку по
/// ИМЕНИ, и порядок в чужой выгрузке меняется без предупреждения.
/// </summary>
/// <param name="Columns">Имена колонок в порядке источника — для объяснений человеку.</param>
/// <param name="Rows">Строки; поиск колонки без учета регистра.</param>
public sealed record StepTable(IReadOnlyList<string>                             Columns,
                               IReadOnlyList<IReadOnlyDictionary<string, string>> Rows)
{
    /// <summary>
    /// Сколько строк один шаг передает дальше.
    ///
    /// Ограничение не про память — строки маленькие. Оно про то, что действие расширения над
    /// каждой строкой ЗАВОДИТ ЗАПИСЬ: ошибка в настройке обмена, приносящем десять тысяч строк,
    /// иначе означала бы десять тысяч документов в журнале регистрации, и разгребать их пришлось
    /// бы руками. Пятисот хватает любому разумному обмену, а неразумный лучше остановить до
    /// первой записи, чем после последней.
    /// </summary>
    public const int MaxRows = 500;

    /// <summary>Таблица из разобранного источника: колонка «Название» плюс свойства схемы.</summary>
    public static StepTable Of(ImportSchema schema, IReadOnlyList<ImportRow> rows)
    {
        var title   = string.IsNullOrWhiteSpace(schema.TitleHeader) ? "Название" : schema.TitleHeader;
        var columns = new List<string> { title };
        columns.AddRange(schema.FieldNames);

        var table = new List<IReadOnlyDictionary<string, string>>(rows.Count);

        foreach (var row in rows)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [title] = row.Title,
            };

            for (var i = 0; i < schema.FieldNames.Count && i < row.FieldValues.Count; i++)
                values[schema.FieldNames[i]] = row.FieldValues[i];

            table.Add(values);
        }

        return new StepTable(columns, table);
    }

    /// <summary>Одна пустая строка — вход для шага, которому предыдущий ничего не передал.</summary>
    public static StepTable Empty { get; } =
        new([], [new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)]);
}

/// <summary>Что делать со сценарием после шага.</summary>
public enum StepFlow
{
    /// <summary>Дальше по списку.</summary>
    Continue = 0,

    /// <summary>
    /// Дальше незачем, и это не неудача: отбор не нашел подходящих строк. Запуск состоялся —
    /// строка журнала пишется, ответ отправителю тоже.
    /// </summary>
    Stop,

    /// <summary>
    /// Делать было нечего, и сценарий кончается ТИХО: ни строки журнала, ни записи в
    /// «Диагностику». Пустая папка обмена, проверяемая каждые пять минут, иначе давала бы три
    /// сотни одинаковых строк в сутки, и настоящую среди них потом не найти.
    /// </summary>
    Silent,
}

/// <summary>Итог одного шага: удался ли и что о нем сказать человеку.</summary>
/// <param name="Ok">Шаг сделал свое дело.</param>
/// <param name="Line">Строка для отчета и журнала; при неудаче — причина.</param>
/// <param name="Rows">Сколько записей прошло через шаг.</param>
/// <param name="Table">Что шаг передает следующему; <c>null</c> — ничего, и дальше идет то, что
/// было. Шаг, который таблицы не делает (сообщение), поток не обрывает: между запросом и
/// действием можно поставить уведомление, не потеряв строки.</param>
/// <param name="Text">Текст, который шаг передает следующему, — файл, взятый из папки. Его ждет
/// преобразователь формата.</param>
/// <param name="Flow">Идти ли дальше по списку шагов.</param>
/// <param name="Reply">Что шаг говорит ОТПРАВИТЕЛЮ — слова действия расширения
/// (<see cref="ActionResult.Answer"/>), без приставок оболочки. Пусто — шаг отправителю ничего не
/// говорит.</param>
public sealed record StepOutcome(bool       Ok,
                                 string     Line,
                                 int        Rows  = 0,
                                 StepTable? Table = null,
                                 string     Text  = "",
                                 StepFlow   Flow  = StepFlow.Continue,
                                 string     Reply = "",
                                 byte[]?    File = null,
                                 string     FileName = "")
{
    public static StepOutcome Bad(string reason) => new(false, reason);

    /// <summary>Делать нечего — сценарий кончается молча.</summary>
    public static StepOutcome Nothing => new(true, "", Flow: StepFlow.Silent);

    /// <summary>Дальше незачем, но запуск состоялся и о нем надо рассказать.</summary>
    public static StepOutcome Halt(string line, StepTable? table = null) =>
        new(true, line, Table: table, Flow: StepFlow.Stop);
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
/// <param name="Idle">Работы не нашлось: папка обмена пуста или в ней лежал уже принятый повтор.
/// Такой запуск в журнал не пишется вовсе.</param>
/// <param name="Reply">Что сказать отправителю — последние слова, которые ему адресовало
/// действие (<see cref="StepOutcome.Reply"/>); пусто — ни одно действие ему ничего не сказало.</param>
public sealed record ScenarioOutcome(
    bool                  Ok,
    string                Problem,
    int                   Done,
    int                   Total,
    int                   Rows,
    TimeSpan              Elapsed,
    IReadOnlyList<string> Lines,
    bool                  Started = true,
    bool                  Idle    = false,
    string                Reply   = "")
{
    /// <summary>Сценарий не запускался вовсе — не сложилось до первого шага.</summary>
    public static ScenarioOutcome Blocked(string problem, int total) =>
        new(false, problem, 0, total, 0, TimeSpan.Zero, [], Started: false);

    /// <summary>Итог одной строкой — для журнала, списка и карточки.</summary>
    public string Summary() =>
        Idle
            ? "делать было нечего"
            : Problem.Length > 0
                ? (Started ? $"остановлен на шаге {Done + 1} из {Total}: " : "не запускался: ") + Problem
                : $"шагов выполнено {Done}, записей {Rows}";
}

/// <summary>
/// Выполнение сценария — шаги по порядку, без единого окна (docs/ROADMAP-API.md, раздел 4).
///
/// Отдельно от службы-таймера, как <c>InboxReceiver</c> отдельно от <c>InboxService</c>: здесь
/// «что делать», там «когда». Ради этого же разделения сюда приходят параметрами и открытые
/// базы, и список расширений, и момент, и способ показать сообщение — на часы, окна и
/// глобальные списки этот класс не смотрит и проверяется тестами целиком.
///
/// <b>Первый неудавшийся шаг останавливает сценарий.</b> Правило, а не настройка: шаги идут
/// цепочкой, и следующий работал бы с данными, которых не пришло. Отказ виден в журнале и в
/// отчете, а сообщать о нем — дело того, кто сценарий завел.
///
/// <b>Вызывать нужно с UI-потока.</b> Реестр (<see cref="IntegrationsRegistry"/>) живет на общем
/// контексте app.db, который принадлежит интерфейсу; тяжелое — разбор ответа, запись в
/// коллекцию, работа расширения — уходит в <c>Task.Run</c> на отдельном подключении, как и в
/// ручном обмене.
/// </summary>
public static partial class ScenarioRunner
{
    /// <summary>Пустой набор значений — вход преобразователя, которому поля не нужны.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoValues =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Что мешает сценарию отработать прямо сейчас; <c>null</c> — ничто.
    ///
    /// Ответ один, а последствия разные, и в этом весь смысл отдельного метода: службе он
    /// говорит «пропусти этот тик молча» (база откроется — сценарий догонит пропущенное), а
    /// человеку, нажавшему «Запустить сейчас», — почему ничего не произошло. Молчаливое
    /// бездействие в ответ на нажатие хуже любого отказа.
    ///
    /// Диска этот метод не касается: его спрашивают на каждую перерисовку списка сценариев, а
    /// папка обмена лежит на сетевом диске, который бывает выключен. Здесь проверяется форма
    /// настройки, а существование папки выяснит сам шаг.
    /// </summary>
    /// <param name="initial">Строки, которые принесли сценарию ИЗВНЕ — их кладет календарный
    /// триггер, и ими же запускают сценарий вручную с готовой таблицей. Шаг, которому нужны
    /// строки, с ними законен и первым в списке.</param>
    public static string? Blocker(Scenario                       scenario,
                                  IntegrationsRegistry           registry,
                                  IReadOnlyList<DatabaseManager> databases,
                                  IReadOnlyList<IExtension>      extensions,
                                  StepTable?                     initial = null)
    {
        if (scenario.Steps.Count == 0) return "в сценарии нет шагов";

        // Порядок шагов тут и проверяется. Текст приносит папка, и нужен он преобразователю;
        // строки приносят запрос и преобразователь, и нужны они отбору. Поставленный первым,
        // такой шаг разбирать и отбирать будет нечего. Календарный триггер строки ПРИНОСИТ
        // САМ — наступившие события, как будто их принес нулевой шаг.
        // Триггер потока — так же: каждая запись события приходит первому шагу строкой.
        var text = false;
        var file = false;
        var rows = scenario.Trigger.WatchesCalendar || scenario.Trigger.WatchesStream
                || initial is { Rows.Count: > 0 };

        if (scenario.Trigger.WatchesStream && StreamTrouble(scenario, registry) is { } stream)
            return stream;

        foreach (var step in scenario.Steps)
        {
            if (step.Kind == ScenarioStepKind.Download)
            {
                if (DownloadTrouble(step, registry) is { } download) return download;
                file = true;
                continue;
            }

            if (step.Kind == ScenarioStepKind.Filter)
            {
                if (!rows) return NoRows();
                continue;
            }

            if (step.Kind == ScenarioStepKind.Compute)
            {
                if (step.Formulas.Count == 0) return "в шаге преобразования не задано ни одной колонки";

                // Строк не прибавляет и не убавляет — только колонки, поэтому признак остается
                // как был: после преобразования отбор работает, до него — нечего преобразовывать.
                if (!rows) return NoComputeRows();

                continue;
            }

            if (step.Kind == ScenarioStepKind.CreateRecord)
            {
                if (step.CollectionId <= 0) return "в шаге не выбрана коллекция для записи";

                if (databases.All(d => d.DbId != step.DbId))
                    return "база данных для создания записи сейчас не открыта";

                continue;
            }

            if (step.Kind == ScenarioStepKind.Export)
            {
                if (step.CollectionId <= 0) return "в шаге не выбрана коллекция для выгрузки";

                if (databases.All(d => d.DbId != step.DbId))
                    return "база данных для выгрузки сейчас не открыта";

                if (string.IsNullOrWhiteSpace(step.Folder)) return "в шаге не выбрана папка выгрузки";

                continue;
            }

            if (step.Kind == ScenarioStepKind.Intake)
            {
                if (string.IsNullOrWhiteSpace(step.Folder)) return "в шаге не выбрана папка обмена";

                text = true;
                continue;
            }

            if (step.Kind == ScenarioStepKind.Action)
            {
                if (ActionOf(step, extensions, out var trouble) is not { } action) return trouble;

                if (action.Kind == ExtensionActionKind.Transformer)
                {
                    if (!text && !(file && action.AcceptsFile)) return NoInput(action);
                    file = false;
                    rows = true;
                    continue;
                }

                if (action.Kind == ExtensionActionKind.Rows)
                {
                    if (!rows) return NoTable(action);

                    // Файловому действию нужна выбранная папка назначения.
                    if (action.MakesFile)
                    {
                        if (string.IsNullOrWhiteSpace(step.Folder))
                            return $"у действия «{action.Name}» не выбрана папка для файла";

                    }

                    if (Unfilled(step, action) is { } missing) return missing;

                    // Строк не прибавляет и не убавляет — только меняет их состав.
                    file = false;
                    continue;
                }

                if (step.CollectionId <= 0)
                    return $"у действия «{action.Name}» не выбрана коллекция";

                if (databases.All(d => d.DbId != step.DbId))
                    return $"база данных для действия «{action.Name}» сейчас не открыта";

                if (Unfilled(step, action) is { } unfilled) return unfilled;

                file = false;
                continue;
            }

            if (step.Kind != ScenarioStepKind.Request) continue;

            if (registry.FindRequest(step.RequestId) is not { } request)
                return $"запрос шага не найден (№{step.RequestId})";

            if (registry.Find(request.ConnectionId) is null)
                return $"у запроса «{request.Name}» не найдено соединение";

            if (registry.Binding(request.Id) is not { } binding)
                return NoBinding(request);

            if (databases.All(d => d.DbId != binding.DbId))
                return $"база данных для запроса «{request.Name}» сейчас не открыта";

            // Круг: сценарий заводится правками в коллекции и сам же в нее пишет. Модель такой
            // круг ловит у шага «действие» — там коллекция записана прямо в шаге, — а здесь она
            // приезжает из привязки карточки запроса, о которой модель не знает. Календарный
            // триггер под тем же правилом: запись с датой — новое событие.
            if (scenario.Trigger.WatchesCollection
             && binding.DbId == scenario.Trigger.DbId
             && binding.CollectionId == scenario.Trigger.CollectionId)
                return $"запрос «{request.Name}» пишет в ту же коллекцию, изменения которой "
                     + "заводят сценарий: он запускал бы сам себя без конца";

            rows = true;
        }

        return null;
    }

    /// <summary>
    /// Выполнить сценарий. <paramref name="notice"/> — как показать сообщение человеку;
    /// <paramref name="now"/> — момент для журнала и для действий.
    ///
    /// <paramref name="initial"/> — строки, которые принес ТРИГГЕР: календарный кладет сюда
    /// наступившие события, и первый же шаг работает с ними, как со строками запроса. Запуск
    /// кнопкой таблицы не несет — шаг, которому нужны строки, честно скажет, каких колонок нет.
    ///
    /// Отмену бросает наружу: остановил человек — итога у такого запуска нет, и притворяться,
    /// что сценарий чем-то кончился, незачем.
    /// </summary>
    public static async Task<ScenarioOutcome> RunAsync(Scenario                       scenario,
                                                       IntegrationsRegistry           registry,
                                                       IReadOnlyList<DatabaseManager> databases,
                                                       IReadOnlyList<IExtension>      extensions,
                                                       DateTimeOffset                 now,
                                                       Action<string>                 notice,
                                                       CancellationToken              ct      = default,
                                                       StepTable?                     initial = null)
    {
        var clock = Stopwatch.StartNew();
        var total = scenario.Steps.Count;

        // Сценарий потока, запущенный кнопкой: строки ему приносит событие, а события нет.
        // Сказать об этом надо здесь и словами — иначе первый же шаг ответил бы «колонки нет», и
        // человек искал бы опечатку в имени колонки.
        if (scenario.Trigger.WatchesStream && initial is not { Rows.Count: > 0 })
            return ScenarioOutcome.Blocked(
                "сценарий заводится событием потока: строку ему приносит событие, и кнопкой "
                + "его не запустить", total);

        if (Blocker(scenario, registry, databases, extensions, initial) is { } blocker)
        {
            Journal(scenario, ok: false, blocker, rows: 0, clock.Elapsed, DbOf(scenario, registry),
                    now, "");
            return ScenarioOutcome.Blocked(blocker, total);
        }

        // Строки запроса поднимаются в память ТОЛЬКО когда есть кому их принять: обычный обмен
        // укладывает ответ потоком, и превращать его в список ради шага, которого в сценарии
        // нет, значило бы платить памятью за неиспользуемую возможность.
        var carry = scenario.Steps.Any(
            s => s.Kind is ScenarioStepKind.Action
                        or ScenarioStepKind.Filter
                        or ScenarioStepKind.Compute
                        or ScenarioStepKind.Download);

        var lines   = new List<string>();
        var rows    = 0;
        var done    = 0;
        var problem = "";
        var idle    = false;

        StepTable? table = initial;
        var        text  = "";
        var        reply = "";
        byte[]?    file  = null;
        var        fileName = "";
        Intake?    taken = null;

        try
        {
            for (var number = 1; number <= scenario.Steps.Count; number++)
            {
                var step = scenario.Steps[number - 1];

                ct.ThrowIfCancellationRequested();

                // Пометка шага уезжает в журнал вместе со строкой: без нее разбор сценария из
                // пяти шагов начинается с пересчета строк по времени, а два шага, запускающих
                // один и тот же запрос, не различить вовсе.
                var mark = StepMark(number, scenario.Steps.Count, step.Kind);

                if (step.Kind == ScenarioStepKind.Download)
                {
                    file = null;
                    fileName = "";
                }

                var outcome = step.Kind switch
                {
                    ScenarioStepKind.Download => await DownloadStepAsync(step, registry, table,
                                                                         scenario.Name, mark, now, ct),
                    ScenarioStepKind.Request => await RequestStepAsync(step, registry, databases,
                                                                       carry, scenario.Name, mark, now, ct),
                    ScenarioStepKind.Action  => await ActionStepAsync(step, databases, extensions,
                                                                      table, text, file, fileName, scenario.Name,
                                                                      $"{scenario.Id}:{scenario.Trigger.StreamId}", now, ct),
                    ScenarioStepKind.Intake  => IntakeStep(step, scenario.Name, mark, now, ref taken),
                    ScenarioStepKind.Filter  => FilterStep(step, table),
                    ScenarioStepKind.Compute => ComputeStep(step, table, now),
                    ScenarioStepKind.CreateRecord => await RecordStepAsync(step, databases, table, ct),
                    ScenarioStepKind.Export  => await ExportStepAsync(step, databases,
                                                                      scenario.Name, now, ct),
                    _                        => NoticeStep(step, notice),
                };

                // Событие потока, не прошедшее отбор, — не запуск: по одному потоку ходят
                // события разного вида, и сценарию нужны не все. Строка журнала на каждое чужое
                // событие похоронила бы под собой настоящие, а ответ на него ушел бы тому, кто
                // ничего не спрашивал.
                if (scenario.Trigger.WatchesStream
                    && step.Kind == ScenarioStepKind.Filter
                    && outcome is { Ok: true, Flow: StepFlow.Stop })
                    outcome = StepOutcome.Nothing;

                // Делать нечего — расходимся молча, не оставляя следа ни в журнале, ни в отчете.
                if (outcome.Flow == StepFlow.Silent) { idle = true; break; }

                if (outcome.Line.Length > 0) lines.Add(outcome.Line);
                rows += outcome.Rows;

                if (outcome.Table is not null)  table = outcome.Table;
                if (outcome.Text.Length  > 0)   text  = outcome.Text;
                if (outcome.Reply.Length > 0)   reply = outcome.Reply;
                if (outcome.File is not null)
                {
                    file = outcome.File;
                    fileName = outcome.FileName;
                }
                // Вложение принадлежит одному действию. Следующий шаг не должен случайно
                // зарегистрировать ту же книгу еще раз либо получить прежнее вложение.
                if (step.Kind == ScenarioStepKind.Action)
                {
                    file = null;
                    fileName = "";
                }

                // Первый неудавшийся шаг останавливает цепочку: следующий работал бы с данными,
                // которых не пришло.
                if (!outcome.Ok) { problem = outcome.Line; break; }

                done++;

                // Отбор не нашел подходящих строк: остальные шаги не к чему применять, но
                // запуск состоялся, и рассказать о нем надо — в том числе отправителю.
                if (outcome.Flow == StepFlow.Stop) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Остановил человек. Взятый файл возвращается во «Входящие»: держать чужой захват
            // до срока незачем, а разобран он не был.
            Abandon(taken);
            throw;
        }

        // Тихий запуск — не неудача: делать было нечего, и жаловаться не на что.
        var ok = problem.Length == 0;

        // Ответ отправителю и уборка его файла — последним делом и по итогу ВСЕГО сценария:
        // единица работы здесь не шаг, а сообщение целиком.
        if (taken is { } claimedFile)
        {
            var answer = ok ? string.Join("; ", lines) : problem;
            if (Close(claimedFile, ok, rows, answer, now) is { } trouble && problem.Length == 0)
            {
                problem = trouble;
                ok      = false;
            }
        }

        var result = new ScenarioOutcome(ok, problem, done, total, rows, clock.Elapsed, lines,
                                         Started: true, Idle: idle, Reply: reply);

        // Тихий запуск в журнал не идет: пустая папка, проверяемая каждые пять минут, иначе
        // оставила бы к утру три сотни строк «делать было нечего».
        if (!idle)
            Journal(scenario, ok, ok ? result.Summary() : problem, rows, clock.Elapsed,
                    DbOf(scenario, registry), now, taken?.File.Name ?? "");

        return result;
    }

    // ── Шаг «запрос» ──────────────────────────────────────────────────────────

    /// <summary>
    /// Запрос в коллекцию — то же самое, что делает кнопка «В коллекцию», только без окон.
    ///
    /// Раскладку берет привязка КАРТОЧКИ ЗАПРОСА: та, которую человек однажды настроил и
    /// проверил глазами. Автомат не выдумывает соответствие колонок — ошибку в угаданном имени
    /// поля видно только по пустой коллекции через неделю.
    /// </summary>
    /// <param name="carry">Дальше по сценарию есть шаг, которому нужны эти строки.</param>
    /// <param name="scenario">Имя сценария для рабочего журнала.</param>
    /// <param name="now">Момент запуска.</param>
    private static async Task<StepOutcome> RequestStepAsync(ScenarioStep                   step,
                                                            IntegrationsRegistry           registry,
                                                            IReadOnlyList<DatabaseManager> databases,
                                                            bool                           carry,
                                                            string                         scenario,
                                                            string                         mark,
                                                            DateTimeOffset                 now,
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
        // изменившиеся в сторонней программе, пока шел обмен, иначе оказались бы позади границы.
        var startedAt = DateTimeOffset.UtcNow;

        // Тело из коллекции собирается один раз на запуск.
        var (payload, cannot) = OutgoingSource.Build(registry, request, databases, ct: ct);

        if (cannot is { Length: > 0 })
            return StepOutcome.Bad($"запрос «{request.Name}»: {cannot}");

        // Отправлять пустой массив незачем: запуск занял бы место в часовом потолке и оставил бы
        // в журнале строку «ушло 0 записей». Пустая коллекция — это не неудача, а «сегодня
        // нечего слать», и сценарий на этом кончается так же, как на пустом отборе.
        if (payload is { Rows: 0 })
            return StepOutcome.Halt($"в коллекции «{payload.Collection}» нет записей — "
                                  + $"запросу «{request.Name}» нечего отправлять");

        // Отправка и рабочий журнал общие у сценариев и ручного запуска.
        var outcome = await RequestLauncher.SendAsync(
            registry, connection, request, payload, binding.DbId, startedAt,
            scenario, mark, ct);

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

        // Отказ ДО первой записи, а не после последней: следующий шаг — действие расширения,
        // которое заводит запись на каждую строку, и разгребать лишнее пришлось бы руками.
        if (carry && schema.RowCount > StepTable.MaxRows)
            return StepOutcome.Bad(
                $"ответ запроса «{request.Name}»: строк {schema.RowCount}, а следующий шаг "
                + $"выполняет действие над каждой — за один запуск их бывает не больше "
                + $"{StepTable.MaxRows}. Сузьте выборку запроса или уберите шаг действия");

        // Строки в список — только ради следующего шага; иначе укладка идет потоком, как и
        // в ручном обмене, и память не зависит от размера ответа.
        IReadOnlyList<ImportRow>? carried = null;
        if (carry) carried = await Task.Run(() => source.ReadRows(ct).ToList(), ct);

        BindingResult result;
        try
        {
            result = await Task.Run(
                () => target.Constructor.ApplyRowsIsolated(
                    binding, schema, carried ?? source.ReadRows(ct), ct), ct);
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

        return new StepOutcome(true, line, result.Created + result.Updated,
                               carried is null ? null : StepTable.Of(schema, carried));
    }

    // ── Шаг «действие расширения» ─────────────────────────────────────────────

    /// <summary>
    /// Действие расширения над строками предыдущего шага (docs/ROADMAP-API.md, раздел 9).
    ///
    /// Оболочка делает здесь ровно три вещи и ни одной сверх того: подставляет значения полей,
    /// открывает отдельное подключение и обходит строки. Что означает «зарегистрировать
    /// документ», знает только расширение, и знать это оболочке незачем — иначе правило,
    /// поправленное в расширении, разошлось бы со вторым его прочтением здесь.
    ///
    /// <b>Все подстановки считаются ДО первой записи.</b> Опечатка в имени колонки, найденная на
    /// сотой строке, оставила бы девяносто девять готовых записей и никакого способа понять,
    /// какие из них лишние.
    ///
    /// <b>Первая отказавшая строка останавливает шаг.</b> Продолжить значило бы решить за
    /// человека, что отказ несущественный. Уже сделанное при этом остается сделанным: операция
    /// расширения — не транзакция обмена, и откатывать чужую работу оболочка не берется. Сколько
    /// прошло и на чем встали, говорит строка отчета.
    /// </summary>
    /// <param name="scenario">Имя сценария для рабочего журнала.</param>
    private static async Task<StepOutcome> ActionStepAsync(ScenarioStep                   step,
                                                           IReadOnlyList<DatabaseManager> databases,
                                                           IReadOnlyList<IExtension>      extensions,
                                                           StepTable?                     incoming,
                                                           string                         text,
                                                           byte[]?                        file,
                                                           string                         fileName,
                                                           string                         scenario,
                                                           string                         channel,
                                                           DateTimeOffset                 now,
                                                           CancellationToken              ct)
    {
        if (ActionOf(step, extensions, out var trouble) is not { } action)
            return StepOutcome.Bad(trouble);

        // Настройки расширения читаются ЗДЕСЬ, на UI-потоке, до ухода в фон: хранилище настроек
        // живет на общем контексте app.db, и действие, прочитавшее их само из фона, однажды
        // уронило бы его.
        var settings = SettingsOf(step, extensions);

        if (file is not null && (incoming ?? StepTable.Empty).Rows.Count != 1)
            return StepOutcome.Bad("Действие с файлом требует ровно одну строку события");
        if (file is not null && !action.AcceptsFile)
            return StepOutcome.Bad($"действие «{action.Name}» не принимает файлы");

        if (action.Kind == ExtensionActionKind.Transformer)
            return await TransformStepAsync(action, text, file, fileName, now, settings, ct);

        if (action.Kind == ExtensionActionKind.Rows)
            return await AutomationStepAsync(step, action, incoming, file, fileName, scenario, now, ct);

        if (step.CollectionId <= 0)
            return StepOutcome.Bad($"у действия «{action.Name}» не выбрана коллекция");

        if (databases.FirstOrDefault(d => d.DbId == step.DbId) is not { } target)
            return StepOutcome.Bad($"база данных для действия «{action.Name}» сейчас не открыта");

        var table = incoming ?? StepTable.Empty;

        if (table.Rows.Count > StepTable.MaxRows)
            return StepOutcome.Bad(
                $"действие «{action.Name}»: строк {table.Rows.Count}, а за один запуск их "
                + $"выполняется не больше {StepTable.MaxRows}");

        var inputs = new List<ActionInput>(table.Rows.Count);

        foreach (var row in table.Rows)
        {
            if (Input(step, action, row, table, out var problem) is not { } input)
                return StepOutcome.Bad($"действие «{action.Name}»: {problem}");

            inputs.Add(input with { Bytes = file, FileName = fileName });
        }

        var ran     = 0;
        var rows    = 0;
        var failure = "";
        var said    = "";   // что сказало действие в последний раз
        var answer  = "";   // что оно сказало ОТПРАВИТЕЛЮ

        try
        {
            await Task.Run(() => target.Constructor.UseIsolated(step.CollectionId, repo =>
            {
                var context = new ActionContext(repo, step.DbId, step.CollectionId, now, settings,
                    target.Constructor, channel, CancellationToken: ct);

                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();

                    var result = action.Run(context, input);

                    if (!result.Ok)
                    {
                        failure = inputs.Count > 1
                            ? $"строка {ran + 1} из {inputs.Count}: {result.Explanation}"
                            : result.Explanation;

                        answer = result.Answer;
                        return false;
                    }

                    ran++;
                    rows  += result.Rows;
                    said   = result.Explanation;
                    answer = result.Answer;
                }

                return true;
            }), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // Расширение обязано возвращать отказ результатом, но обязанность — не гарантия:
            // упавшее расширение не должно уносить с собой службу сценариев.
            return StepOutcome.Bad($"действие «{action.Name}» сорвалось: {error.Message}");
        }

        if (failure.Length > 0)
        {
            var made = ran > 0 ? $" (до этого выполнено {ran})" : "";

            // Отправителю — только то, что действие адресовало ему само: причину, которую он
            // может исправить. Остальные отказы для него — сбой, и отвечает на них оболочка.
            return new StepOutcome(false, $"действие «{action.Name}»: {failure}{made}",
                                   Reply: answer);
        }

        // Одна строка — говорит само действие: «зарегистрировано АБВГ.301121.001» и есть тот
        // ответ, которого ждет отправитель. Много строк — счет: полсотни объяснений подряд в
        // отчете читаются хуже, чем одно число, а место для построчной сводки — квитанция.
        var line = ran == 1 && said.Length > 0
            ? $"Действие «{action.Name}»: {said}"
            : $"Действие «{action.Name}»: выполнено {ran}, записей {rows}";

        return new StepOutcome(true, line, rows, Reply: ran == 1 ? answer : "");
    }

    /// <summary>
    /// Преобразователь формата: текст файла → строки таблицы. В базу он не пишет и подключения
    /// не получает — иначе за разбор текста пришлось бы спрашивать права на запись.
    ///
    /// Строки читает ТОТ ЖЕ источник, что и ответ сторонней программы (<see cref="JsonTextSource"/>):
    /// один и тот же набор данных, пришедший файлом и по сети, обязан ложиться в коллекцию
    /// одинаково — и дальше работает существующее сопоставление колонок без единой правки.
    /// </summary>
    private static async Task<StepOutcome> TransformStepAsync(IExtensionAction  action,
                                                              string            text,
                                                              byte[]?           file,
                                                              string            fileName,
                                                              DateTimeOffset    now,
                                                              IReadOnlyDictionary<string, string> settings,
                                                              CancellationToken ct)
    {
        if (text.Length == 0 && file is null)
            return StepOutcome.Bad(NoInput(action));

        ActionResult result;
        try
        {
            result = await Task.Run(
                () => action.Run(new ActionContext(null, 0, 0, now, settings, CancellationToken: ct),
                                 new ActionInput(NoValues, text, Bytes: file, FileName: fileName)), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return StepOutcome.Bad($"«{action.Name}» сорвался: {error.Message}");
        }

        if (!result.Ok)
            return new StepOutcome(false, $"«{action.Name}»: {result.Explanation}",
                                   Reply: result.Answer);

        var source = new JsonTextSource(result.Json);

        ImportSchema schema;
        try
        {
            schema = await Task.Run(() => source.Analyze(null, ct), ct);
        }
        catch (ImportException error)
        {
            return StepOutcome.Bad($"«{action.Name}» вернул строки, которые не разобрались: "
                                 + error.Message);
        }

        if (schema.RowCount > StepTable.MaxRows)
            return StepOutcome.Bad(
                $"«{action.Name}»: строк {schema.RowCount}, а за один запуск их обрабатывается не "
                + $"больше {StepTable.MaxRows}");

        var rows = await Task.Run(() => source.ReadRows(ct).ToList(), ct);

        // Записей этот шаг не заводит, поэтому в счет сценария не идет: посчитать разобранные
        // строки здесь и заведенные записи на следующем шаге значило бы удвоить итог.
        return new StepOutcome(true, $"Разбор «{action.Name}»: {result.Explanation}", 0,
                               StepTable.Of(schema, rows));
    }

    /// <summary>
    /// Собрать вход действия для одной строки: подставить колонки и убедиться, что обязательные
    /// поля заполнены. <c>null</c> — не собрался, причина в <paramref name="problem"/>.
    /// </summary>
    private static ActionInput? Input(ScenarioStep                        step,
                                      IExtensionAction                    action,
                                      IReadOnlyDictionary<string, string> row,
                                      StepTable                           table,
                                      out string                          problem)
    {
        problem = "";

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, expression) in step.Fields)
        {
            var value = ScenarioValues.Apply(expression, row, out var unknown);

            if (unknown.Count > 0)
            {
                if (action.Fields.FirstOrDefault(field => field.Key == key)
                    is { Required: false, MissingIsEmpty: true })
                {
                    values[key] = "";
                    continue;
                }
                var known = table.Columns.Count > 0
                    ? "; в строке есть: " + string.Join(", ", table.Columns)
                    : "; предыдущий шаг строк не передал";

                problem = $"в поле «{Label(action, key)}» названы колонки, которых нет: "
                        + string.Join(", ", unknown) + known;

                return null;
            }

            values[key] = value;
        }

        foreach (var field in action.Fields)
        {
            if (!field.Required) continue;

            if (!values.TryGetValue(field.Key, out var value) || value.Trim().Length == 0)
            {
                problem = $"поле «{field.Label}» не заполнено, а без него действие не выполняется";
                return null;
            }
        }

        return new ActionInput(values);
    }

    // ── Шаг «отбор» ───────────────────────────────────────────────────────────

    /// <summary>
    /// Отбор строк по условиям — «ЕСЛИ» из плана (docs/ROADMAP-API.md, раздел 2). Дальше идут
    /// только строки, на которых выполнились ВСЕ условия.
    ///
    /// Условием он работает сам собой: у сообщения из папки строка одна, и «отобрано ноль» там
    /// читается как «условие не выполнилось». У выгрузки из сторонней программы строк много, и
    /// полезнее отобрать подходящие, чем остановить обмен целиком из-за одной строки без
    /// проекта.
    ///
    /// <b>Отброшенные строки названы числом в отчете.</b> Отбор, молча теряющий половину
    /// выгрузки, неотличим от поломки — а разбираются с этим через неделю и по пустой коллекции.
    ///
    /// Колонка, которой в строках нет, ОСТАНАВЛИВАЕТ шаг. Считать ее пустой значило бы отобрать
    /// ноль строк и не сказать почему: опечатка в имени колонки выглядела бы точно так же, как
    /// честно не подошедшие данные.
    /// </summary>
    private static StepOutcome FilterStep(ScenarioStep step, StepTable? incoming)
    {
        if (step.Tests.Count == 0)
            return StepOutcome.Bad("в отборе нет ни одного условия");

        if (incoming is not { } table)
            return StepOutcome.Bad(NoRows());

        var unknown = step.Tests.Select(t => t.Column)
                          .Where(c => !table.Columns.Contains(c, StringComparer.OrdinalIgnoreCase))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        if (unknown.Count > 0)
            return StepOutcome.Bad(
                "в отборе названы колонки, которых нет: " + string.Join(", ", unknown)
                + (table.Columns.Count > 0
                    ? "; в строке есть: " + string.Join(", ", table.Columns)
                    : "; предыдущий шаг строк не передал"));

        var kept = table.Rows.Where(row => step.Tests.All(test => test.Holds(row))).ToList();
        var lost = table.Rows.Count - kept.Count;

        var what = string.Join(" и ", step.Tests);

        if (kept.Count == 0)
            return StepOutcome.Halt($"Отбор ({what}): не подошла ни одна строка из "
                                  + $"{table.Rows.Count}, дальше незачем");

        var line = lost > 0
            ? $"Отбор ({what}): подошло {kept.Count} из {table.Rows.Count}"
            : $"Отбор ({what}): подошли все {kept.Count}";

        return new StepOutcome(true, line, 0, table with { Rows = kept });
    }

    /// <summary>Отбирать нечего — строки в сценарий приносит запрос или преобразователь.</summary>
    private static string NoRows() =>
        "отбирать нечего: перед отбором нужен шаг, который приносит строки, — запрос в коллекцию "
        + "или разбор файла";

    // ── Шаг «преобразовать строки» ────────────────────────────────────────────

    /// <summary>
    /// Новые колонки, посчитанные выражениями над строками предыдущего шага
    /// (docs/ROADMAP-API.md, шаг 12). Ни файлов, ни сети, ни базы: шаг берет таблицу и отдает ее
    /// же, только шире.
    ///
    /// <b>Колонки считаются ПО ПОРЯДКУ, и каждая следующая видит уже посчитанные.</b> Так
    /// длинный расчет раскладывается на несколько понятных строк вместо одной неразбираемой:
    /// «Сумма» из цены и количества, «С НДС» из суммы. Ссылка назад — единственная возможная,
    /// поэтому круга не получится даже случайно.
    ///
    /// <b>Выражения разбираются до первой строки, имена колонок проверяются там же.</b> Опечатка
    /// в имени, найденная на сотой строке, оставила бы девяносто девять посчитанных и никакого
    /// способа понять, какие из них верные.
    ///
    /// <b>Первая отказавшая строка останавливает шаг.</b> Считать дальше значило бы решить за
    /// человека, что пустая цена в одной строке — мелочь; в отчете сказано, на какой строке и в
    /// какой колонке встали.
    /// </summary>
    private static StepOutcome ComputeStep(ScenarioStep step, StepTable? incoming, DateTimeOffset now)
    {
        if (step.Formulas.Count == 0)
            return StepOutcome.Bad("в преобразовании нет ни одной колонки");

        if (incoming is not { } table)
            return StepOutcome.Bad(NoComputeRows());

        // Разбор — один раз на шаг, а не на каждую строку: пятьсот строк по десять выражений
        // означали бы пять тысяч разборов одного и того же текста.
        var ready = new List<(string Column, Formula Formula)>(step.Formulas.Count);

        foreach (var formula in step.Formulas)
        {
            var name = (formula.Column ?? "").Trim();

            if (name.Length == 0)
                return StepOutcome.Bad("преобразование: у одного из выражений не названа колонка");

            if (Formula.TryParse(formula.Expression, out var trouble) is not { } parsed)
                return StepOutcome.Bad($"преобразование, колонка «{name}»: {trouble}");

            ready.Add((name, parsed));
        }

        // Известные имена растут по ходу списка: колонка, посчитанная выше, — законный источник
        // для той, что ниже.
        var known   = new HashSet<string>(table.Columns, StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();

        foreach (var (name, formula) in ready)
        {
            foreach (var column in formula.Columns)
                if (!known.Contains(column)
                    && !unknown.Contains(column, StringComparer.OrdinalIgnoreCase))
                    unknown.Add(column);

            known.Add(name);
        }

        if (unknown.Count > 0)
            return StepOutcome.Bad(
                "в выражениях названы колонки, которых нет: " + string.Join(", ", unknown)
                + (table.Columns.Count > 0
                    ? "; в строке есть: " + string.Join(", ", table.Columns)
                    : "; предыдущий шаг строк не передал"));

        if (table.Rows.Count > StepTable.MaxRows)
            return StepOutcome.Bad(
                $"преобразование: строк {table.Rows.Count}, а за один запуск их считается не "
                + $"больше {StepTable.MaxRows}");

        var rows   = new List<IReadOnlyDictionary<string, string>>(table.Rows.Count);
        var number = 0;

        foreach (var row in table.Rows)
        {
            number++;

            var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, value) in row) made[column] = value;

            foreach (var (name, formula) in ready)
            {
                var value = formula.Run(made, now, out var trouble);

                if (trouble.Length > 0)
                    return StepOutcome.Bad(table.Rows.Count > 1
                        ? $"преобразование, строка {number} из {table.Rows.Count}, "
                          + $"колонка «{name}»: {trouble}"
                        : $"преобразование, колонка «{name}»: {trouble}");

                made[name] = value;
            }

            rows.Add(made);
        }

        // Имя, совпавшее с существующей колонкой, заменяет ее значение — так обрезают пробелы и
        // приводят к одному написанию. Замена названа числом: молча переписанная колонка
        // неотличима от посчитанной новой.
        var columns  = new List<string>(table.Columns);
        var replaced = 0;

        foreach (var (name, _) in ready)
            if (columns.Contains(name, StringComparer.OrdinalIgnoreCase)) replaced++;
            else columns.Add(name);

        var line = $"Преобразование ({string.Join(", ", ready.Select(r => r.Column))}): "
                 + $"строк {rows.Count}"
                 + (replaced > 0 ? $", колонок заменено {replaced}" : "");

        // Записей шаг не заводит, поэтому в счет сценария не идет: посчитанные здесь строки и
        // заведенные из них записи на следующем шаге — это одни и те же строки, а не двойной итог.
        return new StepOutcome(true, line, 0, new StepTable(columns, rows));
    }

    /// <summary>Преобразовывать нечего — строки приносят запрос, разбор файла или триггер.</summary>
    private static string NoComputeRows() =>
        "преобразовывать нечего: перед шагом нужен тот, который приносит строки, — запрос в "
        + "коллекцию или разбор файла";

    // ── Шаг «сообщение» ───────────────────────────────────────────────────────

    /// <summary>Сообщение человеку. Единственный шаг, который ничего не меняет.</summary>
    private static StepOutcome NoticeStep(ScenarioStep step, Action<string> notice)
    {
        notice(step.Text);
        return new StepOutcome(true, "Сообщение: " + step.Text);
    }

    // ── Мелочи ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Найти действие шага; <c>null</c> — не нашлось, и причина в <paramref name="trouble"/>.
    /// Ответ нужен в двух местах — <see cref="Blocker"/> и самому шагу, — и текст обязан быть
    /// одинаковым: человек видит его сначала в списке сценариев, потом в отчете о запуске.
    /// </summary>
    private static IExtensionAction? ActionOf(ScenarioStep              step,
                                              IReadOnlyList<IExtension> extensions,
                                              out string                trouble)
    {
        trouble = "";

        if (extensions.FirstOrDefault(e => e.Id == step.Extension) is not { } extension)
        {
            trouble = $"расширение шага не найдено ({step.Extension})";
            return null;
        }

        if (extension.Actions.FirstOrDefault(a => a.Key == step.Action) is not { } action)
        {
            trouble = $"у расширения «{extension.Name}» нет действия «{step.Action}»";
            return null;
        }

        return action;
    }

    /// <summary>
    /// Значения настроек расширения шага — по ключам, которые расширение объявило. Читаются на
    /// UI-потоке; расширения нет — пустой набор (отказ скажет сам шаг).
    /// </summary>
    private static IReadOnlyDictionary<string, string> SettingsOf(ScenarioStep              step,
                                                                  IReadOnlyList<IExtension> extensions)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (extensions.FirstOrDefault(e => e.Id == step.Extension) is not { } extension) return values;

        foreach (var setting in extension.Settings)
            values[setting.Key] = Utils.AppSettings.LoadExtensionSetting(extension.Id, setting.Key);

        return values;
    }

    /// <summary>
    /// Что не так с потоком и карточкой ответа у сценария потока; <c>null</c> — все на месте.
    ///
    /// Ответ передается телом запроса, поэтому для него нужен метод, отличный от GET.
    /// </summary>
    public static string? StreamTrouble(Scenario scenario, IntegrationsRegistry registry)
    {
        if (registry.FindStream(scenario.Trigger.StreamId) is not { } stream)
            return $"поток событий не найден (№{scenario.Trigger.StreamId})";

        if (registry.Find(stream.ConnectionId) is null)
            return $"у потока «{stream.Name}» не найдено соединение";

        if (!scenario.Trigger.Replies) return null;

        if (registry.FindRequest(scenario.Trigger.RequestId) is not { } request)
            return $"запрос ответа не найден (№{scenario.Trigger.RequestId})";

        if (registry.Find(request.ConnectionId) is null)
            return $"у запроса ответа «{request.Name}» не найдено соединение";

        if (!request.Outward)
            return $"запрос ответа «{request.Name}» только читает (GET), а ответ надо отправить: "
                 + "выберите запрос, который отправляет данные";

        return null;
    }

    /// <summary>Преобразователю нечего разбирать — текст в сценарий приносит папка обмена.</summary>
    private static string NoInput(IExtensionAction action) =>
        $"«{action.Name}» разбирает то, что пришло текстом, а текста нет: поставьте перед ним "
        + "шаг «файл из папки»";

    /// <summary>
    /// Обязательное поле действия, для которого шаг не задал выражения; <c>null</c> — все
    /// заполнено. Проверка ранняя: пустое обязательное поле не зависит от того, что придет в
    /// строке, и знать о нем надо из списка сценариев, а не из отчета через сутки.
    /// </summary>
    private static string? Unfilled(ScenarioStep step, IExtensionAction action)
    {
        foreach (var field in action.Fields)
        {
            if (!field.Required) continue;

            if (!step.Fields.TryGetValue(field.Key, out var expression)
                || string.IsNullOrWhiteSpace(expression))
                return $"у действия «{action.Name}» не заполнено поле «{field.Label}»";
        }

        return null;
    }

    /// <summary>Подпись поля действия; ключ — на случай поля, которого у действия уже нет.</summary>
    private static string Label(IExtensionAction action, string key) =>
        action.Fields.FirstOrDefault(f => f.Key == key)?.Label ?? key;

    /// <summary>
    /// Почему сценарий не тронет запрос без привязки. Текст один на все места: и служба, и
    /// экран объясняют это одинаково, а объяснение здесь — половина дела.
    /// </summary>
    private static string NoBinding(IntegrationRequest request) =>
        $"у запроса «{request.Name}» не настроена привязка к коллекции: откройте раздел "
        + "«Интеграции», нажмите у него «В коллекцию» и разложите колонки — сценарий повторяет "
        + "разложенное, но не придумывает его сам";

    /// <summary>
    /// Какую базу задел сценарий — по первому шагу, который вообще называет базу. Нужно колонке
    /// журнала: разбор «что пришло в эту базу» иначе не сведется.
    /// </summary>
    private static int DbOf(Scenario scenario, IntegrationsRegistry registry)
    {
        foreach (var step in scenario.Steps)
        {
            if (step.Kind is ScenarioStepKind.Action
                          or ScenarioStepKind.CreateRecord
                          or ScenarioStepKind.Export
                && step.DbId > 0) return step.DbId;

            if (step.Kind == ScenarioStepKind.Request
                && registry.Binding(step.RequestId) is { } binding) return binding.DbId;
        }

        return 0;
    }

    /// <summary>
    /// Как шаг называется в журнале: «шаг 2 из 5 · запрос».
    ///
    /// Номер и род, а не настройки шага. Сценарий с тех пор могли поправить — переставить шаги,
    /// сменить запрос, — и строка журнала, хранящая подробности, через неделю врала бы. Номер
    /// тоже может разойтись с нынешним сценарием, но он хотя бы честно говорит, каким шаг был в
    /// момент запуска, и различает два шага, дергающих один и тот же запрос.
    /// </summary>
    private static string StepMark(int number, int total, ScenarioStepKind kind) =>
        $"шаг {number} из {total} · " + kind switch
        {
            ScenarioStepKind.Request      => "запрос",
            ScenarioStepKind.Download     => "получение файла",
            ScenarioStepKind.Notify       => "уведомление",
            ScenarioStepKind.Action       => "действие",
            ScenarioStepKind.Intake       => "файл из папки",
            ScenarioStepKind.Filter       => "отбор",
            ScenarioStepKind.CreateRecord => "создание записи",
            ScenarioStepKind.Export       => "экспорт выборки",
            ScenarioStepKind.Compute      => "преобразование",
            _                             => kind.ToString(),
        };

    /// <summary>Отметка ответа на событие — на месте номера шага: ответ идет после последнего.</summary>
    public const string ReplyMark = "ответ на событие";

    /// <summary>
    /// Итоговая строка сценария в журнале. Пишется ВСЕГДА, даже когда до первого шага не дошло:
    /// у такой неудачи своей строки нет вовсе, и без этой причина не видна нигде.
    ///
    /// В накопительный расход не идет: его уже заняли строки шагов.
    /// </summary>
    private static void Journal(Scenario scenario, bool ok, string explanation, int rows,
                                TimeSpan elapsed, int dbId, DateTimeOffset now, string file) =>
        RunJournal.Add(RunEntry.New(RunChannel.Scenario, scenario.Name, file, ok, explanation,
                                    rows, 0, elapsed, dbId, now, counts: false,
                                    // Итоговая строка не связана с отдельным запросом.
                                    direction: 0,
                                    // Имя сценария дублируется полем намеренно: отбор по
                                    // сценарию должен доставать и итог, и все его шаги разом.
                                    scenario: scenario.Name,
                                    // Шага у итога нет: он про сценарий целиком.
                                    step: ""));
}
