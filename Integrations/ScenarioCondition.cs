using System.Text.Json.Serialization;

namespace SmartBase.Integrations;

/// <summary>
/// Что условие спрашивает у значения.
///
/// В хранении — СЛОВОМ, как и вид шага: условия лежат строкой JSON, и «Filled» переживет любую
/// перестановку значений, а «0» молча сменила бы смысл при вставке нового вида в середину.
///
/// Список намеренно короткий. Сравнений «больше», «меньше» и «между» здесь нет: в таблице шага
/// все значения — ТЕКСТ, пришедший из чужой выгрузки, и «10» там меньше «9». Сравнивать их как
/// числа означало бы угадывать тип за человека, а угаданный неверно тип отбирает не те строки
/// молча. Появятся вместе с типами колонок, если понадобятся.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScenarioTest>))]
public enum ScenarioTest
{
    /// <summary>Значение есть и оно не из одних пробелов.</summary>
    Filled = 0,

    /// <summary>Значения нет или оно пустое.</summary>
    Empty,

    /// <summary>Совпадает с образцом.</summary>
    Equals,

    /// <summary>Не совпадает с образцом.</summary>
    NotEquals,

    /// <summary>Содержит образец.</summary>
    Contains,

    /// <summary>Подходит под маску: <c>*</c> — любое число знаков, <c>?</c> — ровно один.</summary>
    Matches,
}

/// <summary>
/// Одно условие отбора: колонка, вопрос к ней и образец (docs/ROADMAP-API.md, раздел 2 —
/// «событие → условие → действие»).
///
/// Регистр не учитывается нигде. Данные приходят из чужой программы, где «Оплачен», «оплачен» и
/// «ОПЛАЧЕН» — одно и то же слово, и требовать от человека угадать написание значило бы отбирать
/// не те строки по причине, которую в отчете не видно.
/// </summary>
/// <param name="Column">Имя колонки в таблице предыдущего шага.</param>
/// <param name="Test">Что спрашиваем.</param>
/// <param name="Value">Образец; у «заполнено» и «пусто» не нужен.</param>
public sealed record ScenarioCondition(string Column, ScenarioTest Test, string Value = "")
{
    /// <summary>Сколько знаков умещается в образце, не превращая карточку в простыню.</summary>
    public const int MaxValueLength = 200;

    /// <summary>Нужен ли этому вопросу образец.</summary>
    public bool NeedsValue => Test is not (ScenarioTest.Filled or ScenarioTest.Empty);

    /// <summary>Что не так с условием; пустой список — годится.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Column)) problems.Add("не названа колонка");

        if (NeedsValue)
        {
            if (string.IsNullOrWhiteSpace(Value))      problems.Add("не заполнен образец");
            else if (Value.Length > MaxValueLength)    problems.Add("образец длиннее "
                                                                 + $"{MaxValueLength} знаков");
        }

        return problems;
    }

    /// <summary>
    /// Выполняется ли условие на строке. Колонки в строке ищутся без учета регистра; ЕСТЬ ли
    /// такая колонка вообще — вопрос не сюда: об этом говорит шаг, и говорит словами, а молча
    /// считать отсутствующую колонку пустой значило бы отобрать ноль строк и не сказать почему.
    /// </summary>
    public bool Holds(IReadOnlyDictionary<string, string> row)
    {
        var value = row.TryGetValue(Column, out var found) ? (found ?? "").Trim() : "";
        var sample = Value.Trim();

        return Test switch
        {
            ScenarioTest.Filled    => value.Length > 0,
            ScenarioTest.Empty     => value.Length == 0,
            ScenarioTest.Equals    => value.Equals(sample, StringComparison.OrdinalIgnoreCase),
            ScenarioTest.NotEquals => !value.Equals(sample, StringComparison.OrdinalIgnoreCase),
            ScenarioTest.Contains  => value.Contains(sample, StringComparison.OrdinalIgnoreCase),
            ScenarioTest.Matches   => MaskMatches(value, sample),
            _                      => false,
        };
    }

    /// <summary>Название вопроса для экрана.</summary>
    public static string Name(ScenarioTest test) => test switch
    {
        ScenarioTest.Filled    => "заполнено",
        ScenarioTest.Empty     => "пусто",
        ScenarioTest.Equals    => "равно",
        ScenarioTest.NotEquals => "не равно",
        ScenarioTest.Contains  => "содержит",
        ScenarioTest.Matches   => "по маске",
        _                      => "неизвестно",
    };

    /// <summary>Одной строкой: «Проект заполнено» · «Статус равно Оплачен».</summary>
    public override string ToString() =>
        NeedsValue ? $"{Column} {Name(Test)} {Value}" : $"{Column} {Name(Test)}";

    /// <summary>
    /// Подходит ли текст под маску: <c>*</c> — любое число знаков (включая ноль), <c>?</c> —
    /// ровно один.
    ///
    /// <b>Это не регулярное выражение, и намеренно.</b> Маска из двух знаков читается человеком,
    /// который ее написал, и через год тоже; регулярное выражение в поле настройки — это язык
    /// программирования, попавший в интерфейс обмена данными, а язык требует отладчика.
    /// </summary>
    public static bool MaskMatches(string text, string mask)
    {
        int at = 0, m = 0, star = -1, mark = 0;

        while (at < text.Length)
        {
            if (m < mask.Length && (mask[m] == '?' || Same(mask[m], text[at])))
            {
                at++;
                m++;
            }
            else if (m < mask.Length && mask[m] == '*')
            {
                // Запомнили звездочку и место, с которого можно откатиться: дальше она
                // «съедает» по знаку, пока не сойдется остаток.
                star = m++;
                mark = at;
            }
            else if (star >= 0)
            {
                m  = star + 1;
                at = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (m < mask.Length && mask[m] == '*') m++;

        return m == mask.Length;
    }

    private static bool Same(char a, char b) =>
        char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
