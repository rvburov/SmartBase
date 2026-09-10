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
}

/// <summary>
/// Триггер сценария — «когда». Чистая логика без часов и ввода-вывода: момент приходит
/// параметром, как у <c>BackupPlanner</c> и <c>NotificationScheduler</c>, — иначе расписание
/// нельзя проверить, не подводя системное время.
///
/// Видов пока три, и это не половина задуманного, а то, что уже есть чем выполнять. Триггеры
/// «изменение данных» и «календарное событие» из плана (docs/ROADMAP-API.md, раздел 2) появятся
/// вместе со своими исполнителями: заводить их здесь раньше значило бы показать человеку
/// настройку, которая ничего не делает.
///
/// Календарные периодичности (по дням недели, месяцам) сюда не переехали намеренно: они уже
/// описаны в <c>BackupSchedule</c>, и обмену нужен другой набор — «каждые полчаса» там как раз
/// нет. Тащить тип резервного копирования в интеграции ради двух общих строк значило бы связать
/// две подсистемы, которые ничем больше не связаны.
/// </summary>
/// <param name="Kind">Вид триггера.</param>
/// <param name="Minutes">Через сколько минут повторять (только <see cref="ScenarioTriggerKind.Every"/>).</param>
/// <param name="Time">Время суток (только <see cref="ScenarioTriggerKind.Daily"/>).</param>
public sealed record ScenarioTrigger(ScenarioTriggerKind Kind, int Minutes, TimeOnly Time)
{
    /// <summary>
    /// Чаще пяти минут — это уже опрос, а не расписание. Каждый запуск стоит места в часовом
    /// потолке политики, и обмен раз в минуту выбрал бы его до обеда, ничего не успев.
    /// </summary>
    public const int MinMinutes = 5;

    /// <summary>Реже суток — это уже «ежедневно в такое-то время», и для него есть свой вид.</summary>
    public const int MaxMinutes = 24 * 60;

    public static ScenarioTrigger Manual => new(ScenarioTriggerKind.Manual, 0, default);

    public static ScenarioTrigger EveryMinutes(int minutes) =>
        new(ScenarioTriggerKind.Every, minutes, default);

    public static ScenarioTrigger DailyAt(TimeOnly time) =>
        new(ScenarioTriggerKind.Daily, 0, time);

    /// <summary>Что не так с триггером; пустой список — годится.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (Kind == ScenarioTriggerKind.Every && (Minutes < MinMinutes || Minutes > MaxMinutes))
            problems.Add($"Повторять можно не чаще {MinMinutes} минут и не реже раза в сутки");

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

    /// <summary>Одной строкой для экрана и списка.</summary>
    public string Describe() => Kind switch
    {
        ScenarioTriggerKind.Every => Minutes % 60 == 0 && Minutes >= 60
            ? $"каждые {Minutes / 60} ч"
            : $"каждые {Minutes} мин",

        ScenarioTriggerKind.Daily => "ежедневно в " + Time.ToString("HH:mm", CultureInfo.InvariantCulture),

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
        _                         => "manual",
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
/// Видов пока два — по той же причине, что и у триггеров: остальные из плана (создать запись,
/// экспорт выборки, шаг-расширение) ждут своих исполнителей и контракта <c>Actions</c>.
/// </summary>
/// <param name="Kind">Вид шага.</param>
/// <param name="RequestId">Запрос (только <see cref="ScenarioStepKind.Request"/>).</param>
/// <param name="Text">Текст сообщения (только <see cref="ScenarioStepKind.Notify"/>).</param>
public sealed record ScenarioStep(ScenarioStepKind Kind, int RequestId, string Text)
{
    /// <summary>Сколько знаков умещается в карточке сообщения, не превращая ее в простыню.</summary>
    public const int MaxTextLength = 200;

    public static ScenarioStep OfRequest(int requestId) =>
        new(ScenarioStepKind.Request, requestId, "");

    public static ScenarioStep OfNotice(string text) =>
        new(ScenarioStepKind.Notify, 0, text);

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

        return problems;
    }

    /// <summary>
    /// Одной строкой для списка шагов. Имя запроса приходит извне: шаг знает только ключ, а
    /// таскать ради подписи весь реестр в модель незачем.
    /// </summary>
    public string Describe(string requestName = "") => Kind switch
    {
        ScenarioStepKind.Request => "Запрос в коллекцию: "
            + (requestName.Length > 0 ? requestName : $"№{RequestId}"),

        ScenarioStepKind.Notify => "Сообщение: " + Text,

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
/// проверка. Условий («ЕСЛИ» из плана) пока нет — им нужно то, что течет между шагами, а это
/// приезжает вместе с контрактом <c>Actions</c>; заводить настройку раньше исполнителя значило
/// бы обещать несбыточное.
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

        return problems;
    }

    /// <summary>Ходит ли сценарий сам — и включен, и есть чем заводить.</summary>
    public bool Automatic => Enabled && Trigger.Kind != ScenarioTriggerKind.Manual;
}
