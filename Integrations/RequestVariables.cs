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
/// на запуск, и подставить в него запись можно только тогда, когда она одна.</param>
public sealed record VariableContext(
    DateOnly        Today,
    DateTimeOffset? LastRun,
    string          ConnectionAddress = "",
    IReadOnlyDictionary<string, string>? Record = null)
{
    /// <summary>Контекст «сейчас» — для боевого запуска.</summary>
    public static VariableContext Now(DateTimeOffset? lastRun, string address = "",
                                      IReadOnlyDictionary<string, string>? record = null) =>
        new(DateOnly.FromDateTime(DateTime.Today), lastRun, address, record);
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
/// есть — и она уедет в чужую программу буквально, а ответ будет пустым по причине, которую
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
    /// <b>Зачем она понадобилась.</b> Чужие программы правят запись по адресу вида
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
    /// Первый запуск: подставляется начало отсчета времени, то есть «отдай все». Пустая строка
    /// тут не годится — половина чужих программ ответит на <c>since=</c> отказом, а не полным
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
                            + "/orders/{{поле.Номер}}"),
    ];

    /// <summary>
    /// Подставить значения. <paramref name="unknown"/> — имена, которых программа не знает;
    /// пустой список означает, что текст готов к отправке.
    /// </summary>
    public static string Apply(string? text, VariableContext context, out IReadOnlyList<string> unknown)
    {
        var missing = new List<string>();
        unknown = missing;

        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal)) return text ?? "";

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
            var value = ValueOf(name, context);

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
    private static string? ValueOf(string name, VariableContext context)
    {
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
        // «/orders/» ушел бы к чужой программе как обращение ко всему списку, а не к одной
        // записи, и ответ на него выглядел бы удачным.
        if (name.StartsWith(FieldPrefix, StringComparison.OrdinalIgnoreCase))
            return context.Record is { } record
                && record.TryGetValue(name[FieldPrefix.Length..].Trim(), out var value)
                    ? value
                    : null;

        return null;
    }

    /// <summary>
    /// Имена сравниваются без учета регистра: человек пишет их руками, и отказывать из-за
    /// заглавной буквы значило бы придираться.
    /// </summary>
    private static bool Same(string name, string known) =>
        string.Equals(name, known, StringComparison.OrdinalIgnoreCase);
}
