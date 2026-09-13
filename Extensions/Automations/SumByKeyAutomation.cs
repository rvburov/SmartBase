namespace SmartBase.Extensions.Automations;

/// <summary>
/// «Свести строки по ключу»: одна строка на каждое значение ключевой колонки — сколько строк и,
/// если названа числовая колонка, их сумма.
///
/// Автоматизация-образец и одновременно рабочий инструмент: свести выгрузку заказов по проектам
/// или трудоемкость по исполнителям — самая частая просьба к «маленькому скрипту». Шагом
/// «преобразовать строки» (шаг 12) это не делается принципиально: выражение работает НАД ОДНОЙ
/// строкой и ничего не знает об остальных, а сведение — это вся таблица разом.
///
/// Устройство ровно такое, каким шаг 13 обещал автоматизацию: один класс, один метод, никаких
/// зависимостей. Ни базы, ни сети, ни файлов ей не нужно — только строки и две настройки.
/// </summary>
public sealed class SumByKeyAutomation : IAutomation
{
    /// <summary>Ключи настроек — их же видит шаг сценария.</summary>
    public const string KeySetting = "key";

    /// <inheritdoc cref="KeySetting"/>
    public const string SumSetting = "sum";

    /// <summary>Колонка, которая появляется в ответе всегда.</summary>
    public const string CountColumn = "Строк";

    public string Key  => "sum-by-key";
    public string Name => "Свести строки по ключу";

    public string What => "Одна строка на каждое значение ключевой колонки: сколько строк было и "
                        + "сумма числовой колонки, если она названа.";

    public IReadOnlyList<ExtensionActionField> Settings =>
    [
        new(KeySetting, "Колонка-ключ", Watermark: "Проект"),
        new(SumSetting, "Колонка с числом", Required: false, Watermark: "Сумма"),
    ];

    public IReadOnlyList<AutomationRow> Run(AutomationInput input)
    {
        var key = input.Setting(KeySetting);
        var sum = input.Setting(SumSetting);

        if (key.Length == 0) throw new AutomationRefusal("не названа колонка-ключ");

        if (input.Rows.Count == 0) return [];

        // Колонки проверяются ДО первой суммы и по всем строкам разом: опечатка в имени иначе
        // дала бы одну строку с пустым ключом и никакого объяснения.
        Demand(key, input.Rows, "колонка-ключ");
        if (sum.Length > 0) Demand(sum, input.Rows, "колонка с числом");

        // Порядок групп — по первому появлению: так свод читается рядом с исходной выгрузкой.
        var order  = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in input.Rows)
        {
            var value = row[key].Trim();

            if (!counts.ContainsKey(value))
            {
                order.Add(value);
                counts[value] = 0;
                totals[value] = 0;
            }

            counts[value]++;

            if (sum.Length == 0) continue;

            var raw = row[sum].Trim();

            // Пусто — это ноль: строка без суммы в выгрузке означает «не заполнено», и
            // останавливать из-за нее свод было бы слишком строго. А вот «около трех» —
            // отказ: молча пропущенная строка исказила бы итог, и заметить это было бы нечем.
            if (raw.Length == 0) continue;

            if (row.Number(sum) is not { } number)
                throw new AutomationRefusal(
                    $"в колонке «{sum}» не число: «{raw}» (строка с ключом «{value}»)");

            totals[value] += number;
        }

        var made = new List<AutomationRow>(order.Count);

        foreach (var value in order)
        {
            var row = AutomationRow.Of((key, value)).With(CountColumn, counts[value]);

            made.Add(sum.Length > 0 ? row.With("Сумма " + sum, totals[value]) : row);
        }

        return made;
    }

    /// <summary>
    /// Есть ли такая колонка хоть в одной строке; нет — отказ, называющий, какие есть. Считать
    /// отсутствующую колонку пустой значило бы свести всю выгрузку в одну строку и не сказать
    /// почему.
    /// </summary>
    private static void Demand(string column, IReadOnlyList<AutomationRow> rows, string what)
    {
        foreach (var row in rows)
            if (row.Has(column)) return;

        var known = rows[0].Columns;

        throw new AutomationRefusal(
            $"{what} «{column}» в строках не нашлась; в строке есть: " + string.Join(", ", known));
    }
}
