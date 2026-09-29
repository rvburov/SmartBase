using System.Globalization;
using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Что подставляется в путь и тело запроса вместо подстановок.
/// </summary>
/// <param name="Today">Сегодняшний день. Передается явно: тест, зависящий от системных часов,
/// доверия не стоит.</param>
/// <param name="LastRun">Когда этот запрос последний раз отработал успешно; <c>null</c> — еще
/// ни разу.</param>
/// <param name="ConnectionAddress">Адрес соединения — иногда нужен внутри тела запроса.</param>
/// <param name="Record">Значения ЕДИНСТВЕННОЙ уходящей записи «имя поля → текст»; <c>null</c> —
/// записей не одна. Ими живет подстановка <c>{{поле.Имя}}</c>: путь запроса собирается один раз
/// на запуск, и подставить в него запись можно только тогда, когда она одна. У запроса,
/// которым сценарий отвечает на событие потока, запись — строка этого события.</param>
/// <param name="Reply">Текст ответа на событие потока — итог сценария; <c>null</c> — запрос
/// запущен не ответом, и подстановки <c>{{ответ.текст}}</c> у него нет.</param>
public sealed record VariableContext(
    DateOnly        Today,
    DateTimeOffset? LastRun,
    string          ConnectionAddress = "",
    IReadOnlyDictionary<string, string>? Record = null,
    string?         Reply = null)
{
    /// <summary>Контекст «сейчас» — для боевого запуска.</summary>
    public static VariableContext Now(DateTimeOffset? lastRun, string address = "",
                                      IReadOnlyDictionary<string, string>? record = null,
                                      string? reply = null) =>
        new(DateOnly.FromDateTime(DateTime.Today), lastRun, address, record, reply);
}

/// <summary>Где стоит подстановка — от этого зависит, как экранируется текст ответа.</summary>
public enum VariablePlace
{
    /// <summary>Путь и параметры адреса.</summary>
    Path = 0,

    /// <summary>Тело запроса.</summary>
    Body,
}

/// <summary>
/// Подстановки в пути и теле запроса: <c>/orders?since={{последний_запуск}}</c>.
///
/// Их намеренно мало. Стоит начать добавлять — и получится язык, который придется описывать,
/// отлаживать и объяснять; а нужен он ради одного: спросить «отдай изменения с прошлого раза»
/// вместо перекачки всего заново каждый час. На медленной сети это разница между рабочим
/// обменом и нерабочим.
///
/// <b>Незнакомая подстановка — ошибка, а не текст.</b> Оставь <c>{{опечатка}}</c> в адресе как
/// есть — и она уедет в стороннюю программу буквально, а ответ будет пустым по причине, которую
/// никто не найдет. Поэтому список неизвестных возвращается наружу, и отправка не начинается.
/// </summary>
public static class RequestVariables
{
    /// <summary>Сегодняшний день: <c>2026-09-09</c>.</summary>
    public const string Today = "сегодня";

    /// <summary>Вчерашний день — обычная граница для суточной выгрузки.</summary>
    public const string Yesterday = "вчера";

    /// <summary>Момент последнего удачного запуска в виде <c>2026-09-09T08:30:00Z</c>.</summary>
    public const string LastRun = "последний_запуск";

    /// <summary>Адрес соединения — для тела запроса, где иногда нужен обратный адрес.</summary>
    public const string ConnectionAddress = "соединение.адрес";

    /// <summary>
    /// Приставка подстановки поля уходящей записи: <c>{{поле.Номер}}</c>
    /// (docs/ROADMAP-API.md, раздел 2).
    ///
    /// <b>Зачем она понадобилась.</b> Сторонние программы правят запись по адресу вида
    /// <c>PUT /orders/А-1043</c> — номер лежит В ПУТИ, а не в теле. Исходящая привязка собирает
    /// тело и на путь не влияет никак, поэтому без этой подстановки такой обмен не выражался
    /// вовсе: единственный способ был завести отдельную карточку запроса на каждую запись.
    ///
    /// <b>Работает только когда запись ОДНА.</b> Путь собирается один раз на запуск; подставь в
    /// него запись из трех — и две оставшиеся уйдут по чужому адресу. Форма привязки «одна
    /// запись» для того и существует.
    /// </summary>
    public const string FieldPrefix = "поле.";

    /// <summary>
    /// Текст ответа на событие потока — итог сценария: слова, сказанные действием, либо текст
    /// ответа при сбое. Есть только у запроса, которым сценарий отвечает на событие.
    ///
    /// <b>Экранируется по месту.</b> Текст набирал человек или собирало расширение, и в нем
    /// бывает что угодно: кавычки, переводы строк. В теле вида JSON он ложится содержимым строки
    /// (кавычки вокруг человек пишет в карточке сам: <c>"message": "{{ответ.текст}}"</c>) — сырой
    /// перевод строки внутри кавычек сделал бы тело негодным, и та сторона отвергла бы его. В
    /// пути и в теле-форме — параметром адреса. Остальные подстановки ведут себя по-прежнему.
    /// </summary>
    public const string ReplyText = "ответ.текст";

    /// <summary>
    /// Первый запуск: подставляется начало отсчета времени, то есть «отдай все». Пустая строка
    /// тут не годится — половина сторонних программ ответит на <c>since=</c> отказом, а не полным
    /// списком, и первый же обмен встанет.
    /// </summary>
    public static readonly DateTimeOffset Beginning = DateTimeOffset.UnixEpoch;

    /// <summary>Все подстановки с пояснениями — для подсказки на экране.</summary>
    public static IReadOnlyList<(string Name, string Meaning)> All { get; } =
    [
        (Today,             "сегодняшняя дата, 2026-09-09"),
        (Yesterday,         "вчерашняя дата"),
        (LastRun,           "момент последнего удачного запуска; при первом — начало отсчета"),
        (ConnectionAddress, "адрес соединения"),
        (FieldPrefix + "Имя", "значение поля уходящей записи — когда запись одна, например "
                            + "/orders/{{поле.Номер}}; в запросе ответа — колонка события"),
        (ReplyText,         "текст ответа на событие потока — только в запросе, которым "
                            + "сценарий отвечает; экранируется по месту"),
    ];

    /// <summary>
    /// Подставить значения. <paramref name="unknown"/> — имена, которых программа не знает;
    /// пустой список означает, что текст готов к отправке.
    /// </summary>
    /// <param name="place">Где стоит текст — от этого зависит экранирование текста ответа.</param>
    public static string Apply(string? text, VariableContext context, out IReadOnlyList<string> unknown,
                               VariablePlace place = VariablePlace.Body)
    {
        var missing = new List<string>();
        unknown = missing;

        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal)) return text ?? "";

        // Вид тела определяется тем же признаком, по которому отправляющий слой выбирает
        // Content-Type (EgressPlan): начинается со скобки — JSON, иначе форма.
        var json = place == VariablePlace.Body
                && text.TrimStart() is { Length: > 0 } head && head[0] is '{' or '[';

        var result = new StringBuilder(text.Length);
        var at     = 0;

        while (at < text.Length)
        {
            var open = text.IndexOf("{{", at, StringComparison.Ordinal);
            if (open < 0) { result.Append(text, at, text.Length - at); break; }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { result.Append(text, at, text.Length - at); break; }

            result.Append(text, at, open - at);

            var name  = text[(open + 2)..close].Trim();
            var value = ValueOf(name, context, json);

            if (value is null)
            {
                // Незнакомую подстановку оставляем как есть: она попадет в отчет об ошибке
                // целиком, и человеку будет видно, что именно он написал.
                if (!missing.Contains(name)) missing.Add(name);
                result.Append(text, open, close + 2 - open);
            }
            else
            {
                result.Append(value);
            }

            at = close + 2;
        }

        return result.ToString();
    }

    /// <summary>Значение подстановки; <c>null</c> — такой подстановки нет.</summary>
    /// <param name="json">Текст стоит в теле вида JSON.</param>
    private static string? ValueOf(string name, VariableContext context, bool json)
    {
        // Текст ответа. Вне ответа этой подстановки нет вовсе — ни пустоты, ни образца: запрос
        // ответа, запущенный кнопкой, ушел бы той стороне с пустым текстом.
        if (Same(name, ReplyText))
            return context.Reply is { } reply ? Escape(reply, json) : null;

        if (Same(name, Today))
            return context.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (Same(name, Yesterday))
            return context.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (Same(name, LastRun))
            return (context.LastRun ?? Beginning).ToUniversalTime()
                   .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        if (Same(name, ConnectionAddress))
            return context.ConnectionAddress;

        // Поле уходящей записи. Неизвестное имя поля НЕ подставляется пустотой: пустой хвост в
        // «/orders/» ушел бы к сторонней программе как обращение ко всему списку, а не к одной
        // записи, и ответ на него выглядел бы удачным.
        if (name.StartsWith(FieldPrefix, StringComparison.OrdinalIgnoreCase))
            return context.Record is { } record
                && record.TryGetValue(name[FieldPrefix.Length..].Trim(), out var value)
                    ? value
                    : null;

        return null;
    }

    /// <summary>
    /// Текст ответа по месту подстановки: в теле вида JSON — содержимое строки без кавычек,
    /// иначе — параметр адреса. Кириллица в JSON остается кириллицей: журнал и сухой прогон
    /// читаются глазами.
    /// </summary>
    private static string Escape(string value, bool json)
    {
        if (!json) return Uri.EscapeDataString(value);

        var quoted = System.Text.Json.JsonSerializer.Serialize(value, JsonText);
        return quoted[1..^1];
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonText = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Добавка к отказу «неизвестные подстановки», когда среди них текст ответа. Без нее человек,
    /// нажавший «Что уйдет» у карточки ответа, искал бы опечатку, которой нет.
    /// </summary>
    public static string ReplyOnlyNote(IEnumerable<string> unknown) =>
        unknown.Any(u => Same(u, ReplyText))
            ? $". Подстановка {{{{{ReplyText}}}}} заполняется, только когда запросом отвечает "
              + "сценарий на событие потока, — кнопкой такой запрос не отправить"
            : "";

    /// <summary>
    /// Имена сравниваются без учета регистра: человек пишет их руками, и отказывать из-за
    /// заглавной буквы значило бы придираться.
    /// </summary>
    private static bool Same(string name, string known) =>
        string.Equals(name, known, StringComparison.OrdinalIgnoreCase);
}
