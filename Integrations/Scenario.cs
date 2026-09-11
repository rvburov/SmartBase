using System.Globalization;
using System.Text.Json.Serialization;

namespace SmartBase.Integrations;

/// <summary>Что заводит сценарий.</summary>
public enum ScenarioTriggerKind
{
    /// <summary>Только нажатием человека.</summary>
    Manual = 0,

    /// <summary>Каждые N минут.</summary>
    Every,

    /// <summary>Ежедневно в назначенное время.</summary>
    Daily,

    /// <summary>
    /// Когда поменялись данные в выбранной коллекции — и правки утихли.
    ///
    /// Появился этот вид последним и намеренно: исполнителя у него не было. Единственный шаг,
    /// которому «данные изменились» по-настоящему нужно, — отправка наружу, а она стоит под
    /// замком из шага 5 (docs/ROADMAP-API.md). Заведи триггер раньше — и человек получил бы
    /// настройку, которая ничего не делает.
    /// </summary>
    Changed,
}

/// <summary>
/// Триггер сценария — «когда». Чистая логика без часов и ввода-вывода: момент приходит
/// параметром, как у <c>BackupPlanner</c> и <c>NotificationScheduler</c>, — иначе расписание
/// нельзя проверить, не подводя системное время.
///
/// Видов четыре: «календарное событие» из плана (docs/ROADMAP-API.md, раздел 2) появится вместе
/// со своим исполнителем — заводить его здесь раньше значило бы показать человеку настройку,
/// которая ничего не делает.
///
/// Календарные периодичности (по дням недели, месяцам) сюда не переехали намеренно: они уже
/// описаны в <c>BackupSchedule</c>, и обмену нужен другой набор — «каждые полчаса» там как раз
/// нет. Тащить тип резервного копирования в интеграции ради двух общих строк значило бы связать
/// две подсистемы, которые ничем больше не связаны.
/// </summary>
/// <param name="Kind">Вид триггера.</param>
/// <param name="Minutes">Через сколько минут повторять (<see cref="ScenarioTriggerKind.Every"/>)
/// либо сколько ждать тишины после последней правки (<see cref="ScenarioTriggerKind.Changed"/>).</param>
/// <param name="Time">Время суток (только <see cref="ScenarioTriggerKind.Daily"/>).</param>
/// <param name="DbId">База, за коллекцией которой следим (только <see cref="ScenarioTriggerKind.Changed"/>).</param>
/// <param name="CollectionId">Коллекция, правки в которой заводят сценарий.</param>
public sealed record ScenarioTrigger(
    ScenarioTriggerKind Kind,
    int                 Minutes,
    TimeOnly            Time,
    int                 DbId         = 0,
    int                 CollectionId = 0)
{
    /// <summary>
    /// Чаще пяти минут — это уже опрос, а не расписание. Каждый запуск стоит места в часовом
    /// потолке политики, и обмен раз в минуту выбрал бы его до обеда, ничего не успев.
    ///
    /// Для «изменения данных» это же число означает другое — сколько ждать тишины, — но предел у
    /// него тот же и по той же причине: правка каждой строки таблицы дала бы по обмену на строку.
    /// </summary>
    public const int MinMinutes = 5;

    /// <summary>Реже суток — это уже «ежедневно в такое-то время», и для него есть свой вид.</summary>
    public const int MaxMinutes = 24 * 60;

    public static ScenarioTrigger Manual => new(ScenarioTriggerKind.Manual, 0, default);

    public static ScenarioTrigger EveryMinutes(int minutes) =>
        new(ScenarioTriggerKind.Every, minutes, default);

    public static ScenarioTrigger DailyAt(TimeOnly time) =>
        new(ScenarioTriggerKind.Daily, 0, time);

    /// <summary>Следить за правками в коллекции и ждать <paramref name="settle"/> минут тишины.</summary>
    public static ScenarioTrigger OnChange(int dbId, int collectionId, int settle) =>
        new(ScenarioTriggerKind.Changed, settle, default, dbId, collectionId);

    /// <summary>Следит ли триггер за правками в коллекции.</summary>
    public bool WatchesData => Kind == ScenarioTriggerKind.Changed;

    /// <summary>Что не так с триггером; пустой список — годится.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Kind == ScenarioTriggerKind.Every && (Minutes < MinMinutes || Minutes > MaxMinutes))
            problems.Add($"Повторять можно не чаще {MinMinutes} минут и не реже раза в сутки");

        if (Kind == ScenarioTriggerKind.Changed)
        {
            if (CollectionId <= 0 || DbId <= 0)
                problems.Add("Не выбрана коллекция, за изменениями которой следить");

            if (Minutes < MinMinutes || Minutes > MaxMinutes)
                problems.Add($"Ждать тишины можно не меньше {MinMinutes} минут и не больше суток");
        }

        return problems;
    }

    /// <summary>
    /// Ближайший запуск СТРОГО ПОСЛЕ указанного момента; <c>null</c> — сценарий по расписанию не
    /// ходит вовсе.
    /// </summary>
    public DateTimeOffset? NextRun(DateTimeOffset after) => Kind switch
    {
        ScenarioTriggerKind.Every => after.AddMinutes(Math.Max(MinMinutes, Minutes)),

        ScenarioTriggerKind.Daily => Today(after) > after ? Today(after) : Today(after).AddDays(1),

        _ => null,
    };

    /// <summary>
    /// Назначенное время в тот же местный день. Считается по МЕСТНОМУ времени: «в 07:00» человек
    /// понимает по своим часам, а момент прошлого запуска хранится в UTC и приходит сюда с любым
    /// сдвигом.
    /// </summary>
    private DateTimeOffset Today(DateTimeOffset after)
    {
        var local = after.ToLocalTime();
        return new DateTimeOffset(DateOnly.FromDateTime(local.DateTime), Time, local.Offset);
    }

    /// <summary>
    /// Пора ли запускать. <paramref name="lastRun"/> — когда сценарий отработал в прошлый раз;
    /// <c>null</c> означает «еще ни разу», и тогда НЕ пора: первый запуск ставится от момента
    /// включения, который проставляет служба. Иначе включенный вечером сценарий «ежедневно в
    /// 07:00» сработал бы немедленно — ровно того, чего человек не просил.
    ///
    /// Пропущенное догоняется: программа была выключена в назначенный час — ближайший запуск
    /// остается в прошлом, и первый же тик после запуска его выполнит. Один раз, а не по разу за
    /// каждый пропущенный день: обмен нужен свежий, а не задним числом.
    /// </summary>
    public bool IsDue(DateTimeOffset? lastRun, DateTimeOffset now) =>
        lastRun is { } last && NextRun(last) is { } next && now >= next;

    /// <summary>
    /// Одной строкой для экрана и списка. Имя коллекции приходит извне: триггер знает только
    /// ключ, а таскать ради подписи целую базу в модель незачем.
    /// </summary>
    public string Describe(string collection = "") => Kind switch
    {
        ScenarioTriggerKind.Every => Minutes % 60 == 0 && Minutes >= 60
            ? $"каждые {Minutes / 60} ч"
            : $"каждые {Minutes} мин",

        ScenarioTriggerKind.Daily => "ежедневно в " + Time.ToString("HH:mm", CultureInfo.InvariantCulture),

        ScenarioTriggerKind.Changed =>
            $"при правках в «{(collection.Length > 0 ? collection : "коллекции №" + CollectionId)}»"
            + $", через {Minutes} мин тишины",

        _ => "вручную",
    };

    /// <summary>
    /// Канонический вид для хранения одной строкой: <c>manual</c> · <c>every 30</c> ·
    /// <c>daily 07:00</c>. Прием тот же, что у <c>BackupSchedule</c>: настройка переменной формы
    /// в одной колонке читается глазами прямо в файле, а разбор строгий.
    /// </summary>
    public override string ToString() => Kind switch
    {
        ScenarioTriggerKind.Every => "every " + Minutes.ToString(CultureInfo.InvariantCulture),
        ScenarioTriggerKind.Daily => "daily " + Time.ToString("HH:mm", CultureInfo.InvariantCulture),

        ScenarioTriggerKind.Changed =>
            $"changed {Minutes.ToString(CultureInfo.InvariantCulture)} "
            + $"{DbId.ToString(CultureInfo.InvariantCulture)} "
            + $"{CollectionId.ToString(CultureInfo.InvariantCulture)}",

        _ => "manual",
    };

    /// <summary>
    /// Разобрать строку хранения; мусор — <see cref="Manual"/>. Молча и без исключения: строку
    /// мог записать более новый выпуск программы, и падать на открытии экрана из-за одного
    /// сценария незачем. «Вручную» — самое безобидное из возможных прочтений: сценарий не
    /// станет ходить сам по непонятному расписанию.
    /// </summary>
    public static ScenarioTrigger Parse(string? text)
    {
        var tokens = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return tokens switch
        {
            ["every", var minutes] when int.TryParse(minutes, NumberStyles.Integer,
                                                     CultureInfo.InvariantCulture, out var value)
                                       && value >= MinMinutes && value <= MaxMinutes =>
                EveryMinutes(value),

            ["daily", var time] when TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture,
                                                            DateTimeStyles.None, out var moment) =>
                DailyAt(moment),

            ["changed", var settle, var db, var collection]
                when int.TryParse(settle, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wait)
                  && int.TryParse(db, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dbId)
                  && int.TryParse(collection, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                  && wait >= MinMinutes && wait <= MaxMinutes && dbId > 0 && id > 0 =>
                OnChange(dbId, id, wait),

            _ => Manual,
        };
    }
}

/// <summary>
/// Что делает шаг сценария.
///
/// В хранении — СЛОВОМ, а не числом (<see cref="JsonStringEnumConverter"/>): шаги лежат строкой
/// JSON, и строка «Request» переживет любую перестановку значений, а «0» молча сменила бы смысл
/// при вставке нового вида в середину списка. Заодно строку видно глазами прямо в файле.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScenarioStepKind>))]
public enum ScenarioStepKind
{
    /// <summary>Запустить сохраненный запрос и уложить ответ в коллекцию по его привязке.</summary>
    Request = 0,

    /// <summary>Показать сообщение человеку.</summary>
    Notify,

    /// <summary>
    /// Выполнить действие расширения над строками, которые принес предыдущий шаг.
    /// </summary>
    Action,

    /// <summary>
    /// Взять из папки обмена один файл и передать его текст дальше. Точка входа принадлежит
    /// СЦЕНАРИЮ, а не расширению: одну папку можно разбирать разными сценариями, а одно
    /// расширение повесить на несколько папок (docs/ROADMAP-API.md, раздел 9).
    /// </summary>
    Intake,

    /// <summary>
    /// Отбор строк по условиям — то самое «ЕСЛИ» из плана. Дальше идут только строки, которые
    /// подошли; не подошла ни одна — сценарий на этом кончается, и это не неудача.
    /// </summary>
    Filter,
}

/// <summary>
/// Шаг сценария — «что делать». Список шагов линейный: граф узлов, как в n8n, для обмена
/// данными не нужен (docs/ROADMAP-API.md, раздел 13).
///
/// Шаг «запрос» не несет ни раскладки колонок, ни коллекции: он ссылается на карточку запроса, а
/// раскладку берет ЕЕ привязка — та самая, которую человек однажды настроил кнопкой «В
/// коллекцию». <b>Автомат не выдумывает раскладку.</b> Запрос, который ни разу не укладывали
/// руками, сценарий не выполняет и говорит об этом: угаданное соответствие колонок видно только
/// по пустой коллекции через неделю.
///
/// Шаг «действие» не несет ни строчки логики расширения: он называет расширение, действие и
/// коллекцию, а значения полей задает выражениями — постоянный текст или
/// <c>{{имя колонки}}</c> из строки, которую принес предыдущий шаг. Так одно и то же расширение
/// вешается на разные каналы и разные журналы, ничего о них не зная.
///
/// Видов пока четыре — по той же причине, что и у триггеров: остальные из плана (создать запись,
/// экспорт выборки) ждут своих исполнителей.
/// </summary>
/// <param name="Kind">Вид шага.</param>
/// <param name="RequestId">Запрос (только <see cref="ScenarioStepKind.Request"/>).</param>
/// <param name="Text">Текст сообщения (только <see cref="ScenarioStepKind.Notify"/>).</param>
/// <param name="Extension">Ключ расширения (только <see cref="ScenarioStepKind.Action"/>).</param>
/// <param name="Action">Ключ действия внутри расширения.</param>
/// <param name="DbId">База, в которой лежит коллекция шага.</param>
/// <param name="CollectionId">Коллекция, с которой работает действие.</param>
/// <param name="FieldValues">Выражения для полей действия по их ключам; <c>null</c> — пусто.
/// Словарь намеренно не участвует в сравнении записей (у него ссылочное равенство) — сравнивать
/// шаги целиком в программе негде, а хранение идет через JSON.</param>
/// <param name="Folder">Корень папки обмена (только <see cref="ScenarioStepKind.Intake"/>).</param>
/// <param name="Conditions">Условия отбора (только <see cref="ScenarioStepKind.Filter"/>);
/// <c>null</c> — пусто. Список, как и словарь полей, не участвует в сравнении записей.</param>
public sealed record ScenarioStep(
    ScenarioStepKind                     Kind,
    int                                  RequestId,
    string                               Text,
    string                               Extension    = "",
    string                               Action       = "",
    int                                  DbId         = 0,
    int                                  CollectionId = 0,
    IReadOnlyDictionary<string, string>? FieldValues  = null,
    string                               Folder       = "",
    IReadOnlyList<ScenarioCondition>?    Conditions   = null)
{
    /// <summary>Сколько знаков умещается в карточке сообщения, не превращая ее в простыню.</summary>
    public const int MaxTextLength = 200;

    /// <summary>Пустой набор — чтобы проверка на <c>null</c> не расползлась по всем местам.</summary>
    private static readonly IReadOnlyDictionary<string, string> Nothing =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Выражения для полей действия; у остальных видов шага пусто.</summary>
    public IReadOnlyDictionary<string, string> Fields => FieldValues ?? Nothing;

    /// <summary>
    /// Сколько условий помещается в один отбор. Предохранитель того же рода, что и потолок
    /// шагов: десяток условий в строку еще читается, а полтора десятка уже не проверяются
    /// глазами — и отбор начинает жить своей жизнью.
    /// </summary>
    public const int MaxConditions = 10;

    /// <summary>Условия отбора; у остальных видов шага пусто.</summary>
    public IReadOnlyList<ScenarioCondition> Tests => Conditions ?? [];

    public static ScenarioStep OfRequest(int requestId) =>
        new(ScenarioStepKind.Request, requestId, "");

    public static ScenarioStep OfNotice(string text) =>
        new(ScenarioStepKind.Notify, 0, text);

    public static ScenarioStep OfAction(string extension, string action, int dbId, int collectionId,
                                        IReadOnlyDictionary<string, string>? fields = null) =>
        new(ScenarioStepKind.Action, 0, "", extension, action, dbId, collectionId, fields);

    public static ScenarioStep OfIntake(string folder) =>
        new(ScenarioStepKind.Intake, 0, "", Folder: folder);

    public static ScenarioStep OfFilter(params ScenarioCondition[] conditions) =>
        new(ScenarioStepKind.Filter, 0, "", Conditions: conditions);

    /// <summary>Что не так с шагом; пустой список — годится.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Kind == ScenarioStepKind.Request && RequestId <= 0)
            problems.Add("В шаге не выбран запрос");

        if (Kind == ScenarioStepKind.Notify)
        {
            if (string.IsNullOrWhiteSpace(Text))
                problems.Add("В шаге не заполнен текст сообщения");
            else if (Text.Length > MaxTextLength)
                problems.Add($"Текст сообщения длиннее {MaxTextLength} знаков");
        }

        if (Kind == ScenarioStepKind.Action)
        {
            // Есть ли такое расширение и такое действие — вопрос момента запуска, как и
            // открытая база: список расширений знает оболочка, а не модель. Здесь проверяется
            // только то, что человек вообще сделал выбор.
            //
            // Коллекция тут НЕ спрашивается, хотя операция без нее не выполнится: нужна она или
            // нет, зависит от вида действия, а вид знает расширение. Спросит тот, кто знает, —
            // проверка выполнимости и живая строка «не сработает».
            if (string.IsNullOrWhiteSpace(Extension)) problems.Add("В шаге не выбрано расширение");
            if (string.IsNullOrWhiteSpace(Action))    problems.Add("В шаге не выбрано действие");
        }

        if (Kind == ScenarioStepKind.Intake && string.IsNullOrWhiteSpace(Folder))
            problems.Add("В шаге не выбрана папка обмена");

        if (Kind == ScenarioStepKind.Filter)
        {
            if (Tests.Count == 0)
                problems.Add("В отборе нет ни одного условия");
            else if (Tests.Count > MaxConditions)
                problems.Add($"Условий больше {MaxConditions}: отберите в два приема");

            for (var i = 0; i < Tests.Count; i++)
                foreach (var problem in Tests[i].Problems())
                    problems.Add($"Условие {i + 1}: {problem}");
        }

        return problems;
    }

    /// <summary>
    /// Одной строкой для списка шагов. Имя того, на что шаг ссылается (запроса или действия),
    /// приходит извне: шаг знает только ключ, а таскать ради подписи весь реестр в модель
    /// незачем.
    /// </summary>
    public string Describe(string name = "") => Kind switch
    {
        ScenarioStepKind.Request => "Запрос в коллекцию: "
            + (name.Length > 0 ? name : $"№{RequestId}"),

        ScenarioStepKind.Notify => "Сообщение: " + Text,

        ScenarioStepKind.Action => "Действие расширения: "
            + (name.Length > 0 ? name : Action),

        ScenarioStepKind.Intake => "Файл из папки: "
            + (Folder.Length > 0 ? Folder : "папка не выбрана"),

        // Условия соединяются союзом «и»: «или» пришлось бы рисовать скобками, а скобки в
        // строке настройки читаются хуже, чем два отбора подряд.
        ScenarioStepKind.Filter => "Отбор: "
            + (Tests.Count > 0 ? string.Join(" и ", Tests) : "условия не заданы"),

        _ => "Неизвестный шаг",
    };
}

/// <summary>
/// Сценарий — «когда и что»: триггер плюс шаги (docs/ROADMAP-API.md, раздел 2).
///
/// Живет в app.db рядом с соединениями и запросами, и по той же причине: файл базы данных ездит
/// по сетевой папке и открыт на нескольких машинах разом, а сценарий, лежащий в нем, сработал бы
/// на каждой открытой копии программы — и один обмен превратился бы в пять.
///
/// Модель без ввода-вывода: выполняет сценарий отдельная служба, а здесь только форма и ее
/// проверка. Условий («ЕСЛИ» из плана) пока нет. Между шагами уже течет таблица — ее берет шаг
/// «действие», — но условию нужен не поток, а язык сравнения над ним, а всякий такой язык
/// приходится описывать, отлаживать и объяснять. Выбирать его форму раньше, чем станет видно,
/// какие сравнения нужны на деле, значит выбирать вслепую.
/// </summary>
/// <param name="Id">Ключ строки в <c>app.db</c>; 0 — сценарий еще не сохранен.</param>
/// <param name="Name">Имя для человека: «Заказы из учетной системы».</param>
/// <param name="Enabled">Ходит ли сценарий сам. Выключенный запускается только кнопкой.</param>
/// <param name="Trigger">Когда запускать.</param>
/// <param name="Steps">Что делать, по порядку.</param>
public sealed record Scenario(
    int                            Id,
    string                         Name,
    bool                           Enabled,
    ScenarioTrigger                Trigger,
    IReadOnlyList<ScenarioStep>    Steps)
{
    /// <summary>
    /// Сколько шагов помещается в один сценарий. Не ограничение возможностей, а предохранитель:
    /// линейный список из полусотни шагов не читается, а значит, и не проверяется человеком.
    /// </summary>
    public const int MaxSteps = 20;

    /// <summary>Пустой сценарий для экрана «Добавить»: выключен и без расписания.</summary>
    public static Scenario New() => new(0, "", false, ScenarioTrigger.Manual, []);

    /// <summary>
    /// Что не так со сценарием; пустой список — можно сохранять. Проверяется форма, а не
    /// выполнимость: есть ли у запроса привязка и открыта ли нужная база — вопросы момента
    /// запуска, и отвечать на них при сохранении бессмысленно (база откроется завтра).
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            problems.Add("Название не заполнено");

        problems.AddRange(Trigger.Problems());

        if (Steps.Count == 0)
            problems.Add("Не добавлено ни одного шага");
        else if (Steps.Count > MaxSteps)
            problems.Add($"Шагов больше {MaxSteps}: разделите сценарий на несколько");

        for (var i = 0; i < Steps.Count; i++)
            foreach (var problem in Steps[i].Problems())
                problems.Add($"Шаг {i + 1}: {problem}");

        // Включенный сценарий без расписания не сработает никогда, и молчать об этом нельзя:
        // человек будет считать, что обмен идет.
        if (Enabled && Trigger.Kind == ScenarioTriggerKind.Manual)
            problems.Add("Сценарий без расписания включать незачем: он запускается только кнопкой");

        problems.AddRange(SelfLoop());

        return problems;
    }

    /// <summary>
    /// Сценарий, который ПИШЕТ в ту самую коллекцию, правки которой его заводят. Такой запускал
    /// бы сам себя без конца: отработал — записал — снова изменилось — снова отработал.
    ///
    /// Ловится это при сохранении, а не в работе, и намеренно: круг, замеченный на боевой
    /// машине, к тому моменту уже выбрал часовой потолок политики и остановил ВЕСЬ обмен, а не
    /// только себя. Отказать человеку словами, пока он смотрит на настройку, дешевле в тысячу раз.
    ///
    /// Здесь проверяются шаги «действие» — те, у которых коллекция записана прямо в шаге. Шаг
    /// «запрос» берет коллекцию у привязки своей карточки, и о ней модель не знает; его ловит
    /// <c>ScenarioRunner.Blocker</c>, у которого реестр под рукой.
    /// </summary>
    private IEnumerable<string> SelfLoop()
    {
        if (!Trigger.WatchesData) yield break;

        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];

            if (step.Kind != ScenarioStepKind.Action)            continue;
            if (step.DbId != Trigger.DbId)                       continue;
            if (step.CollectionId != Trigger.CollectionId)       continue;

            yield return $"Шаг {i + 1} пишет в ту же коллекцию, изменения которой заводят "
                       + "сценарий: он запускал бы сам себя без конца";
        }
    }

    /// <summary>Ходит ли сценарий сам — и включен, и есть чем заводить.</summary>
    public bool Automatic => Enabled && Trigger.Kind != ScenarioTriggerKind.Manual;
}
