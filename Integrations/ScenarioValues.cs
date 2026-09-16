using System.Text;

namespace SmartBase.Integrations;

/// <summary>
/// Значения полей действия в шаге сценария: постоянный текст, в который подставляются колонки
/// строки — <c>{{Проект}}</c>, <c>{{Децимальный номер}}</c>.
///
/// Зачем это вообще нужно. Действие расширения ждет заполненную запись, а строка приходит из
/// сторонней программы или из файла, и имена там свои. Раскладка «колонка источника → поле
/// действия» и есть то место, где две стороны сходятся; постоянный текст в том же поле —
/// потому что часть значений в источнике не приходит вовсе (код организации у всех документов
/// один и тот же).
///
/// <b>Почему это не <see cref="RequestVariables"/>.</b> Там словарь закрытый и общий на все
/// запуски: «сегодня», «последний_запуск». Здесь имена приносит источник, и они у каждого
/// обмена свои. Сложи их в один список — и колонка с именем «сегодня» молча подменится датой,
/// а найти такое по пустому полю в журнале нельзя.
///
/// <b>Незнакомая колонка — отказ, а не пустота.</b> Опечатка в имени иначе прошла бы тихо:
/// действие получило бы пустое поле, зарегистрировало бы документ без проекта, и увидели бы это
/// через неделю по журналу.
/// </summary>
public static class ScenarioValues
{
    /// <summary>
    /// Подставить значения колонок. <paramref name="unknown"/> — имена, которых в строке нет;
    /// пустой список означает, что значение готово.
    ///
    /// Имена сравниваются без учета регистра — так же, как колонки источника ложатся на поля
    /// коллекции при импорте: «Номер» и «номер» в чужой выгрузке означают одно и то же.
    /// </summary>
    public static string Apply(string?                             text,
                               IReadOnlyDictionary<string, string> row,
                               out IReadOnlyList<string>           unknown)
    {
        var missing = new List<string>();
        unknown = missing;

        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return text ?? "";

        var result = new StringBuilder(text.Length);
        var at     = 0;

        while (at < text.Length)
        {
            var open = text.IndexOf("{{", at, StringComparison.Ordinal);
            if (open < 0) { result.Append(text, at, text.Length - at); break; }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { result.Append(text, at, text.Length - at); break; }

            result.Append(text, at, open - at);

            var name = text[(open + 2)..close].Trim();

            if (Value(name, row) is { } value)
            {
                result.Append(value);
            }
            else
            {
                // Неизвестное имя остается в тексте как есть: оно попадет в отчет целиком, и
                // человеку будет видно, что именно он написал.
                if (!missing.Contains(name)) missing.Add(name);
                result.Append(text, open, close + 2 - open);
            }

            at = close + 2;
        }

        return result.ToString();
    }

    /// <summary>Есть ли в строке такая колонка и что в ней; <c>null</c> — колонки нет.</summary>
    private static string? Value(string name, IReadOnlyDictionary<string, string> row)
    {
        if (row.TryGetValue(name, out var exact)) return exact ?? "";

        foreach (var (column, value) in row)
            if (string.Equals(column, name, StringComparison.OrdinalIgnoreCase))
                return value ?? "";

        return null;
    }
}
