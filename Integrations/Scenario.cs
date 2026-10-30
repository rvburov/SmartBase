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

    /// <summary>
    /// За N минут до начала календарного события в выбранной коллекции (docs/ROADMAP-API.md,
    /// шаг 11). Ждал своих исполнителей так же, как «изменение данных»: пока сценарий не умел
    /// ни создать запись, ни выгрузить выборку, напоминать ему было нечем. Наступившие события
    /// сценарий получает СТРОКАМИ — как будто их принес первый шаг.
    /// </summary>
    Calendar,

    /// <summary>
    /// Один раз при запуске программы (docs/ROADMAP-API.md, раздел 2).
    ///
    /// Для чего он нужен: забрать то, что накопилось у сторонней программы, пока этот компьютер был
    /// выключен. Расписанием это не выражается — «каждые 30 минут» после включения ждет
    /// полчаса, а «ежедневно в 07:00» не сработает вовсе, если в семь машина еще не работала.
    ///
    /// <b>Парного «при остановке» нет и не будет.</b> Раздел 2 обещал оба, но обещание
    /// невыполнимо по существу: выключение питания, закрытие сеанса и снятие процесса не дают
    /// программе ни одного шанса отработать, а закрытие окна пришлось бы задерживать на время
    /// сетевого обмена — до полуминуты на каждый шаг. Триггер, который срабатывает «обычно, но
    /// не всегда», хуже отсутствующего: на него нельзя опереться, а выглядит он как гарантия.
    /// Сказано об этом в разделе 13 роадмапа.
    /// </summary>
    Startup,

    /// <summary>
    /// По событию потока (<see cref="IntegrationStream"/>): сторонняя программа сама сообщила о
    /// случившемся. Каждая запись события приходит первому шагу СТРОКОЙ — как будто ее принес
    /// нулевой шаг, — и сценарий идет на каждую запись отдельно: у каждой свой отправитель и
    /// свой ответ.
    ///
    /// Расписания у этого вида нет: он заводится событием, а не часами, и служба сценариев его не
    /// трогает — поток слушает своя служба (<c>StreamService</c>).
    ///
    /// <b>Ответ — по итогу всего сценария</b>, как квитанция у файла из папки: карточка запроса
    /// <see cref="ScenarioTrigger.RequestId"/> уходит после последнего шага и несет слова,
    /// сказанные действием, либо <see cref="ScenarioTrigger.FailText"/>, когда сценарий не
    /// отработал по причине, которая не в самом событии.
    /// </summary>
    Stream,
}

/// <summary>
/// Триггер сценария — «когда». Чистая логика без часов и ввода-вывода: момент приходит
/// параметром, как у <c>BackupPlanner</c> и <c>NotificationScheduler</c>, — иначе расписание
/// нельзя проверить, не подводя системное время.
///
/// Календарные периодичности (по дням недели, месяцам) сюда не переехали намеренно: они уже
/// описаны в <c>BackupSchedule</c>, и обмену нужен другой набор — «каждые полчаса» там как раз
/// нет. Тащить тип резервного копирования в интеграции ради двух общих строк значило бы связать
/// две подсистемы, которые ничем больше не связаны.
/// </summary>
/// <param name="Kind">Вид триггера.</param>
/// <param name="Minutes">Через сколько минут повторять (<see cref="ScenarioTriggerKind.Every"/>),
/// сколько ждать тишины после последней правки (<see cref="ScenarioTriggerKind.Changed"/>) либо
/// за сколько минут до начала события заводить (<see cref="ScenarioTriggerKind.Calendar"/>).</param>
/// <param name="Time">Время суток (только <see cref="ScenarioTriggerKind.Daily"/>).</param>
/// <param name="DbId">База, за коллекцией которой следим (правки и календарь).</param>
/// <param name="CollectionId">Коллекция, правки или события которой заводят сценарий.</param>
/// <param name="StreamId">Поток, события которого заводят сценарий (только
/// <see cref="ScenarioTriggerKind.Stream"/>).</param>
/// <param name="RequestId">Карточка запроса, которой уходит ответ на событие; 0 — сценарий
/// отвечать не должен (только <see cref="ScenarioTriggerKind.Stream"/>).</param>
/// <param name="FailText">Что ответить, когда сценарий не отработал по причине не в самом
/// событии — база недоступна, шаг не сложился; пусто — в этом случае не отвечать вовсе. Текст
/// вводит человек: что именно сказать той стороне, программа за него не решает.</param>
/// <param name="ReplyBreak">Чем в ответе отделять строку от строки; пусто — обычным переводом
/// строки. Нужен, когда та сторона показывает текст разметкой и перевода строки не видит: какой
/// у нее перенос, знает только человек, читавший ее документацию.</param>
public sealed record ScenarioTrigger(
    ScenarioTriggerKind Kind,
    int                 Minutes,
    TimeOnly            Time,
    int                 DbId         = 0,
    int                 CollectionId = 0,
    int                 StreamId     = 0,
    int                 RequestId    = 0,
    string              FailText     = "",
    string              ReplyBreak   = "")
{
    /// <summary>Сколько знаков в тексте ответа при сбое — как у сообщения человеку.</summary>
    public const int MaxFailTextLength = 200;

    /// <summary>Сколько знаков в переносе строки ответа: это знак или тег, а не текст.</summary>
    public const int MaxReplyBreakLength = 20;

    /// <summary>Метка переноса строки в строке хранения.</summary>
    private const string BreakMark = "break=";

    /// <summary>
    /// Минимальный интервал расписания не дает одному сценарию непрерывно запускаться.
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

    /// <summary>Заводить за <paramref name="lead"/> минут до начала события в коллекции.</summary>
    public static ScenarioTrigger BeforeEvent(int dbId, int collectionId, int lead) =>
        new(ScenarioTriggerKind.Calendar, lead, default, dbId, collectionId);

    /// <summary>Один раз при запуске программы.</summary>
    public static ScenarioTrigger OnStartup => new(ScenarioTriggerKind.Startup, 0, default);

    /// <summary>
    /// По событию потока. <paramref name="replyRequestId"/> — чем отвечать (0 — не отвечать),
    /// <paramref name="failText"/> — что ответить при сбое, <paramref name="replyBreak"/> — чем
    /// в ответе отделять строку от строки.
    /// </summary>
    public static ScenarioTrigger OnStream(int streamId, int replyRequestId = 0, string failText = "",
                                           string replyBreak = "") =>
        new(ScenarioTriggerKind.Stream, 0, default, StreamId: streamId,
            RequestId: Math.Max(0, replyRequestId), FailText: OneLine(failText),
            ReplyBreak: NoLineBreaks(replyBreak));

    /// <summary>
    /// Текст ответа с тем переносом строки, который понимает та сторона. Перенос не задан —
    /// текст уходит как есть.
    /// </summary>
    public string WithBreaks(string? text)
    {
        if (ReplyBreak.Length == 0) return text ?? "";

        return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", ReplyBreak);
    }

    /// <summary>Заводится ли триггер событием потока.</summary>
    public bool WatchesStream => Kind == ScenarioTriggerKind.Stream;

    /// <summary>Отвечает ли сценарий на событие.</summary>
    public bool Replies => WatchesStream && RequestId > 0;

    /// <summary>Следит ли триггер за правками в коллекции.</summary>
    public bool WatchesData => Kind == ScenarioTriggerKind.Changed;

    /// <summary>Заводится ли триггер календарными событиями.</summary>
    public bool WatchesCalendar => Kind == ScenarioTriggerKind.Calendar;

    /// <summary>Заводится ли триггер запуском программы.</summary>
    public bool OnStart => Kind == ScenarioTriggerKind.Startup;

    /// <summary>
    /// Привязан ли триггер к коллекции. Для таких сценарий не должен САМ писать в эту
    /// коллекцию: правка заводила бы его заново, и круг не кончился бы никогда.
    /// </summary>
    public bool WatchesCollection => WatchesData || WatchesCalendar;

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

        if (Kind == ScenarioTriggerKind.Calendar)
        {
            if (CollectionId <= 0 || DbId <= 0)
                problems.Add("Не выбрана коллекция, события которой заводят сценарий");

            // Ноль минут — «к началу события» — годится: это не частота опроса, а упреждение.
            if (Minutes < 0 || Minutes > MaxMinutes)
                problems.Add("Упреждение — от нуля минут до суток");
        }

        if (Kind == ScenarioTriggerKind.Stream)
        {
            if (StreamId <= 0)
                problems.Add("Не выбран поток, события которого заводят сценарий");

            if (FailText.Length > MaxFailTextLength)
                problems.Add($"Текст ответа при сбое длиннее {MaxFailTextLength} знаков");

            // Текст без карточки ответа не уйдет никуда, а человек считал бы, что ответ настроен.
            if (FailText.Length > 0 && RequestId <= 0)
                problems.Add("Текст ответа при сбое задан, а запрос ответа не выбран");

            if (ReplyBreak.Length > MaxReplyBreakLength)
                problems.Add($"Перенос строки в ответе длиннее {MaxReplyBreakLength} знаков");

            if (ReplyBreak.Length > 0 && RequestId <= 0)
                problems.Add("Перенос строки в ответе задан, а запрос ответа не выбран");
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
    /// ключ, а таскать ради подписи целую базу в модель незачем. У триггера потока на этом месте
    /// приходит имя потока.
    /// </summary>
    public string Describe(string collection = "") => Kind switch
    {
        ScenarioTriggerKind.Stream =>
            $"по событию потока «{(collection.Length > 0 ? collection : "№" + StreamId)}»"
            + (Replies ? ", с ответом" : ""),

        ScenarioTriggerKind.Every => Minutes % 60 == 0 && Minutes >= 60
            ? $"каждые {Minutes / 60} ч"
            : $"каждые {Minutes} мин",

        ScenarioTriggerKind.Daily => "ежедневно в " + Time.ToString("HH:mm", CultureInfo.InvariantCulture),

        ScenarioTriggerKind.Changed =>
            $"при правках в «{(collection.Length > 0 ? collection : "коллекции №" + CollectionId)}»"
            + $", через {Minutes} мин тишины",

        ScenarioTriggerKind.Calendar => (Minutes > 0 ? $"за {Minutes} мин до события" : "к началу события")
            + $" в «{(collection.Length > 0 ? collection : "коллекции №" + CollectionId)}»",

        ScenarioTriggerKind.Startup => "при запуске программы",

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

        ScenarioTriggerKind.Calendar =>
            $"event {Minutes.ToString(CultureInfo.InvariantCulture)} "
            + $"{DbId.ToString(CultureInfo.InvariantCulture)} "
            + $"{CollectionId.ToString(CultureInfo.InvariantCulture)}",

        ScenarioTriggerKind.Startup => "startup",

        // Текст ответа стоит ПОСЛЕДНИМ и занимает хвост строки целиком: в нем бывают пробелы, а
        // остальные части — числа. Перенос строки — одним словом перед ним.
        ScenarioTriggerKind.Stream =>
            ($"stream {StreamId.ToString(CultureInfo.InvariantCulture)} "
             + $"{RequestId.ToString(CultureInfo.InvariantCulture)} "
             + BreakWord()
             + OneLine(FailText)).TrimEnd(),

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
        if (ParseStream(text) is { } stream) return stream;

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

            ["event", var lead, var db, var collection]
                when int.TryParse(lead, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ahead)
                  && int.TryParse(db, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dbId)
                  && int.TryParse(collection, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                  && ahead >= 0 && ahead <= MaxMinutes && dbId > 0 && id > 0 =>
                BeforeEvent(dbId, id, ahead),

            ["startup"] => OnStartup,

            _ => Manual,
        };
    }

    /// <summary>
    /// Триггер потока из строки хранения; <c>null</c> — строка не про поток. Разбирается
    /// отдельно от остальных: у него хвост строки — текст с пробелами, и делить его на слова
    /// нельзя.
    /// </summary>
    private static ScenarioTrigger? ParseStream(string? text)
    {
        var parts = (text ?? "").Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3 || parts[0] != "stream") return null;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stream)
            || stream <= 0) return null;

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var reply)
            || reply < 0) return null;

        var tail = parts.Length > 3 ? parts[3] : "";
        var mark = "";

        if (tail.StartsWith(BreakMark, StringComparison.Ordinal))
        {
            var end  = tail.IndexOf(' ');
            var word = end < 0 ? tail : tail[..end];

            mark = Uri.UnescapeDataString(word[BreakMark.Length..]);
            tail = end < 0 ? "" : tail[(end + 1)..];
        }

        return OnStream(stream, reply, tail, mark);
    }

    /// <summary>
    /// Перенос строки в строке хранения — одним словом без пробелов: <c>break=%3Cbr%3E</c>.
    /// Пишется, только когда перенос задан, — строки без него остаются прежними. Пустая метка
    /// ставится, когда с этих же букв начинается сам текст ответа: иначе при чтении его начало
    /// сошло бы за перенос.
    /// </summary>
    private string BreakWord() =>
        ReplyBreak.Length > 0 || OneLine(FailText).StartsWith(BreakMark, StringComparison.Ordinal)
            ? BreakMark + Uri.EscapeDataString(ReplyBreak) + " "
            : "";

    /// <summary>Текст одной строкой: переводы строк в хранении развалили бы разбор.</summary>
    private static string OneLine(string? text) =>
        string.Join(" ", (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Перенос как его ввели, но без переводов строк. Пробелы остаются: строки ответа можно
    /// отделять и ими.
    /// </summary>
    private static string NoLineBreaks(string? text) =>
        (text ?? "").Replace("\r", "").Replace("\n", "");
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

    /// <summary>
    /// Создать запись: коллекция и таблица «поле → выражение» с подстановками из строки
    /// предыдущего шага (docs/ROADMAP-API.md, шаг 11). Запись на каждую пришедшую строку;
    /// шагу без строк — одна запись из постоянных значений.
    /// </summary>
    CreateRecord,

    /// <summary>
    /// Выгрузить выборку коллекции в файл: формат, выбранная папка, отбор
    /// строк условиями (шаг 11). Читает КОЛЛЕКЦИЮ, а не поток шагов: выборка существует и
    /// тогда, когда сценарию никто ничего не принес.
    /// </summary>
    Export,

    /// <summary>
    /// Посчитать новые колонки выражениями над строками предыдущего шага (шаг 12) — то самое
    /// «данные пришли, их пересчитали, они ушли дальше», ради которого обычно и заводят
    /// маленький скрипт. Скрипта тут нет и не будет: у выражения нет ни файлов, ни сети, ни
    /// циклов, и запрещать ему нечего (<see cref="Formula"/>).
    /// </summary>
    Compute,

    /// <summary>
    /// Получить двоичный файл сохраненным запросом GET и передать следующему действию.
    /// Имя файла задается выражением над строкой события; пустое значение пропускает шаг.
    /// </summary>
    Download,
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
/// Шаг «преобразовать строки» ничего не читает и никуда не пишет: он считает новые колонки
/// выражениями над тем, что принес предыдущий шаг. Ввода-вывода у него нет по построению —
/// поэтому такой шаг можно выполнить без открытой базы данных.
/// </summary>
/// <param name="Kind">Вид шага.</param>
/// <param name="RequestId">Запрос (<see cref="ScenarioStepKind.Request"/> или
/// <see cref="ScenarioStepKind.Download"/>).</param>
/// <param name="Text">Текст сообщения (<see cref="ScenarioStepKind.Notify"/>) либо выражение
/// имени получаемого файла (<see cref="ScenarioStepKind.Download"/>).</param>
/// <param name="Extension">Ключ расширения (только <see cref="ScenarioStepKind.Action"/>).</param>
/// <param name="Action">Ключ действия внутри расширения.</param>
/// <param name="DbId">База, в которой лежит коллекция шага.</param>
/// <param name="CollectionId">Коллекция, с которой работает действие.</param>
/// <param name="FieldValues">Выражения для полей действия по их ключам; <c>null</c> — пусто.
/// Словарь намеренно не участвует в сравнении записей (у него ссылочное равенство) — сравнивать
/// шаги целиком в программе негде, а хранение идет через JSON.</param>
/// <param name="Folder">Корень папки обмена (<see cref="ScenarioStepKind.Intake"/>) либо папка,
/// в которую ложится файл выгрузки (<see cref="ScenarioStepKind.Export"/>).</param>
/// <param name="Conditions">Условия отбора (<see cref="ScenarioStepKind.Filter"/> — над потоком
/// строк, <see cref="ScenarioStepKind.Export"/> — над строками коллекции); <c>null</c> — пусто.
/// Список, как и словарь полей, не участвует в сравнении записей.</param>
/// <param name="Format">Формат файла выгрузки (только <see cref="ScenarioStepKind.Export"/>):
/// «csv», «json» либо «xlsx».</param>
/// <param name="MatchField">Поле-ключ шага «создать/обновить запись»; пусто — только создание.
/// Раздел 2 обещал «создать/обновить запись», а шаг умел одно создание, и «разобрать сообщение →
/// обновить статус» заводило дубли при каждом запуске.</param>
/// <param name="CreateMissing">Заводить ли запись, которой не нашлось по ключу. Запрет нужен
/// обмену, где запись обязана уже существовать: там новая означает, что ключ разошелся.</param>
/// <param name="ColumnFormulas">Новые колонки и выражения для них
/// (<see cref="ScenarioStepKind.Compute"/>) — по порядку: каждая следующая видит уже
/// посчитанные. Список, как и словарь полей, не участвует в сравнении записей.</param>
public sealed record ScenarioStep(
    ScenarioStepKind                     Kind,
    int                                  RequestId,
    string                               Text,
    string                               Extension      = "",
    string                               Action         = "",
    int                                  DbId           = 0,
    int                                  CollectionId   = 0,
    IReadOnlyDictionary<string, string>? FieldValues    = null,
    string                               Folder         = "",
    IReadOnlyList<ScenarioCondition>?    Conditions     = null,
    string                               Format         = "",
    IReadOnlyList<ScenarioFormula>?      ColumnFormulas = null,
    string                               MatchField     = "",
    bool                                 CreateMissing  = true)
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

    /// <summary>Ищет ли шаг записи по ключу, а не только заводит новые.</summary>
    public bool Matching => !string.IsNullOrWhiteSpace(MatchField);

    /// <summary>
    /// Сколько колонок считает один шаг преобразования. Предел тот же по духу, что у условий:
    /// десяток выражений в одном шаге еще читается сверху вниз, а два десятка проверяются уже
    /// не глазами. Нужно больше — это два шага подряд, и в отчете видно, где что посчиталось.
    /// </summary>
    public const int MaxFormulas = 10;

    /// <summary>Новые колонки и выражения к ним; у остальных видов шага пусто.</summary>
    public IReadOnlyList<ScenarioFormula> Formulas => ColumnFormulas ?? [];

    public static ScenarioStep OfRequest(int requestId) =>
        new(ScenarioStepKind.Request, requestId, "");

    /// <summary>Получить файл без записи ответа в коллекцию.</summary>
    public static ScenarioStep OfDownload(int requestId, string fileName) =>
        new(ScenarioStepKind.Download, requestId, fileName);

    public static ScenarioStep OfNotice(string text) =>
        new(ScenarioStepKind.Notify, 0, text);

    public static ScenarioStep OfAction(string extension, string action, int dbId, int collectionId,
                                        IReadOnlyDictionary<string, string>? fields = null) =>
        new(ScenarioStepKind.Action, 0, "", extension, action, dbId, collectionId, fields);

    public static ScenarioStep OfIntake(string folder) =>
        new(ScenarioStepKind.Intake, 0, "", Folder: folder);

    public static ScenarioStep OfFilter(params ScenarioCondition[] conditions) =>
        new(ScenarioStepKind.Filter, 0, "", Conditions: conditions);

    public static ScenarioStep OfRecord(int dbId, int collectionId,
                                        IReadOnlyDictionary<string, string> fields,
                                        string matchField = "", bool createMissing = true) =>
        new(ScenarioStepKind.CreateRecord, 0, "", DbId: dbId, CollectionId: collectionId,
            FieldValues: fields, MatchField: matchField, CreateMissing: createMissing);

    public static ScenarioStep OfExport(int dbId, int collectionId, string folder, string format,
                                        IReadOnlyList<ScenarioCondition>? conditions = null) =>
        new(ScenarioStepKind.Export, 0, "", DbId: dbId, CollectionId: collectionId,
            Folder: folder, Conditions: conditions, Format: format);

    public static ScenarioStep OfCompute(params ScenarioFormula[] formulas) =>
        new(ScenarioStepKind.Compute, 0, "", ColumnFormulas: formulas);

    /// <summary>Форматы выгрузки, которые умеет шаг «экспорт выборки». Ровно те, что пишут
    /// готовые писатели экспорта; «xlsm» намеренно не предлагается — macro-enabled контейнер
    /// автомату ни к чему.</summary>
    public static readonly IReadOnlyList<string> ExportFormats = ["csv", "json", "xlsx"];

    /// <summary>Что не так с шагом; пустой список — годится.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if ((Kind is ScenarioStepKind.Request or ScenarioStepKind.Download) && RequestId <= 0)
            problems.Add("В шаге не выбран запрос");

        if (Kind == ScenarioStepKind.Download)
        {
            if (string.IsNullOrWhiteSpace(Text))
                problems.Add("В шаге не заполнено имя файла");
            else if (Text.Length > MaxTextLength)
                problems.Add($"Выражение имени файла длиннее {MaxTextLength} знаков");
        }

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

        if (Kind == ScenarioStepKind.CreateRecord)
        {
            if (CollectionId <= 0 || DbId <= 0)
                problems.Add("В шаге не выбрана коллекция");

            // Запись без единого значения заводить можно, но незачем: такой шаг почти
            // наверняка забытая заготовка, и молча плодить пустые записи по расписанию хуже,
            // чем спросить.
            if (Fields.All(f => string.IsNullOrWhiteSpace(f.Value)))
                problems.Add("В шаге не заполнено ни одного поля записи");

            // «Ни обновлять найденные, ни заводить новые» — шаг, который не делает ничего.
            // Такое сочетание набирается в два щелчка и молча ничего не пишет неделями.
            if (!Matching && !CreateMissing)
                problems.Add("Шаг не заводит новые записи и не ищет их по ключу — "
                           + "делать ему нечего");
        }

        if (Kind == ScenarioStepKind.Export)
        {
            if (CollectionId <= 0 || DbId <= 0)
                problems.Add("В шаге не выбрана коллекция");

            if (string.IsNullOrWhiteSpace(Folder))
                problems.Add("В шаге не выбрана папка для файла выгрузки");

            if (!ExportFormats.Contains(Format.Trim().ToLowerInvariant()))
                problems.Add("Формат выгрузки — " + string.Join(", ", ExportFormats));

            // Отбор у выгрузки необязателен: пустой список условий означает «вся коллекция».
            if (Tests.Count > MaxConditions)
                problems.Add($"Условий больше {MaxConditions}: отберите в два приема");

            for (var i = 0; i < Tests.Count; i++)
                foreach (var problem in Tests[i].Problems())
                    problems.Add($"Условие {i + 1}: {problem}");
        }

        if (Kind == ScenarioStepKind.Compute)
        {
            if (Formulas.Count == 0)
                problems.Add("В преобразовании нет ни одной колонки");
            else if (Formulas.Count > MaxFormulas)
                problems.Add($"Колонок больше {MaxFormulas}: посчитайте в два приема");

            for (var i = 0; i < Formulas.Count; i++)
                foreach (var problem in Formulas[i].Problems())
                    problems.Add($"Колонка {i + 1}: {problem}");

            // Две строки с одним именем — это спор, чье значение останется, и решать его
            // порядком строк незачем: вторая просто переписала бы первую, а человек считал бы,
            // что посчиталось и то и другое.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var formula in Formulas)
            {
                var name = (formula.Column ?? "").Trim();

                if (name.Length > 0 && !seen.Add(name))
                    problems.Add($"Колонка «{name}» названа дважды");
            }
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

        ScenarioStepKind.Download => "Получить файл: "
            + (name.Length > 0 ? name : $"№{RequestId}")
            + (Text.Length > 0 ? " → " + Text : " — имя не задано"),

        ScenarioStepKind.Notify => "Сообщение: " + Text,

        ScenarioStepKind.Action => "Действие расширения: "
            + (name.Length > 0 ? name : Action),

        ScenarioStepKind.Intake => "Файл из папки: "
            + (Folder.Length > 0 ? Folder : "папка не выбрана"),

        // Условия соединяются союзом «и»: «или» пришлось бы рисовать скобками, а скобки в
        // строке настройки читаются хуже, чем два отбора подряд.
        ScenarioStepKind.Filter => "Отбор: "
            + (Tests.Count > 0 ? string.Join(" и ", Tests) : "условия не заданы"),

        ScenarioStepKind.CreateRecord => (Matching ? "Обновить запись: " : "Создать запись: ")
            + (name.Length > 0 ? name : $"коллекция №{CollectionId}")
            + $" ({Fields.Count(f => !string.IsNullOrWhiteSpace(f.Value))} полей)"
            + (Matching
                ? $", ключ «{MatchField.Trim()}»" + (CreateMissing ? "" : ", только найденные")
                : ""),

        ScenarioStepKind.Export => "Экспорт выборки: "
            + (name.Length > 0 ? name : $"коллекция №{CollectionId}")
            + $" → {Format}"
            + (Tests.Count > 0 ? $", условий: {Tests.Count}" : ""),

        ScenarioStepKind.Compute => "Преобразовать строки: "
            + (Formulas.Count > 0
                ? string.Join(", ", Formulas.Select(f => f.Column))
                : "колонки не заданы"),

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
/// проверка. Между шагами течет таблица, и над ней работают два шага без всякого доступа наружу:
/// отбор («ЕСЛИ» из плана) и преобразование строк. Язык сравнения у них общий и нарочно бедный —
/// его границы описаны в <see cref="Formula"/>.
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
    /// Календарный триггер под тем же правилом: созданная запись с датой — это новое событие,
    /// и круг тот же, только медленнее.
    ///
    /// Проверяется при сохранении, чтобы цикл не запускал обмен непрерывно на рабочей машине.
    ///
    /// Здесь проверяются шаги «действие» и «создать запись» — те, у которых коллекция записана
    /// прямо в шаге. Шаг «запрос» берет коллекцию у привязки своей карточки, и о ней модель не
    /// знает; его ловит <c>ScenarioRunner.Blocker</c>, у которого реестр под рукой.
    /// </summary>
    private IEnumerable<string> SelfLoop()
    {
        if (!Trigger.WatchesCollection) yield break;

        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];

            if (step.Kind is not (ScenarioStepKind.Action or ScenarioStepKind.CreateRecord))
                continue;

            if (step.DbId != Trigger.DbId)                 continue;
            if (step.CollectionId != Trigger.CollectionId) continue;

            yield return $"Шаг {i + 1} пишет в ту же коллекцию, изменения которой заводят "
                       + "сценарий: он запускал бы сам себя без конца";
        }
    }

    /// <summary>Ходит ли сценарий сам — и включен, и есть чем заводить.</summary>
    public bool Automatic => Enabled && Trigger.Kind != ScenarioTriggerKind.Manual;
}
