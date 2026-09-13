using SmartBase.Extensions.Automations;

namespace SmartBase.Tests.Extensions.Automations;

/// <summary>
/// Автоматизация «Свести строки по ключу» (<see cref="SumByKeyAutomation"/>) — образец того, как
/// пишется автоматизация, и рабочий инструмент.
///
/// Проверяется не сложение — оно очевидно, — а решения: порядок групп повторяет выгрузку, пустая
/// сумма считается нулем, а «около трех» останавливает свод, потому что молча пропущенная строка
/// исказила бы итог незаметно.
/// </summary>
public sealed class SumByKeyTests
{
    private static readonly SumByKeyAutomation Automation = new();

    private static AutomationInput Input(IReadOnlyList<AutomationRow> rows,
                                         string key = "Проект", string sum = "")
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SumByKeyAutomation.KeySetting] = key,
        };

        if (sum.Length > 0) settings[SumByKeyAutomation.SumSetting] = sum;

        return new AutomationInput(rows, settings);
    }

    private static readonly AutomationRow[] Orders =
    [
        AutomationRow.Of(("Проект", "Молоток"), ("Сумма", "10")),
        AutomationRow.Of(("Проект", "Клещи"),   ("Сумма", "2,5")),
        AutomationRow.Of(("Проект", "Молоток"), ("Сумма", "5")),
    ];

    [Fact]
    public void Rows_are_counted_by_key()
    {
        var made = Automation.Run(Input(Orders));

        Assert.Equal(2, made.Count);
        Assert.Equal("Молоток", made[0]["Проект"]);
        Assert.Equal("2",       made[0][SumByKeyAutomation.CountColumn]);
        Assert.Equal("Клещи",   made[1]["Проект"]);
        Assert.Equal("1",       made[1][SumByKeyAutomation.CountColumn]);
    }

    /// <summary>Запятая в числе — обычное дело для выгрузки: в базе числа лежат сырым вводом.</summary>
    [Fact]
    public void Numbers_are_summed_with_a_comma_too()
    {
        var made = Automation.Run(Input(Orders, sum: "Сумма"));

        Assert.Equal("15",  made[0]["Сумма Сумма"]);
        Assert.Equal("2.5", made[1]["Сумма Сумма"]);
    }

    /// <summary>Порядок групп повторяет выгрузку: свод читают рядом с исходными строками.</summary>
    [Fact]
    public void Groups_keep_the_order_of_first_appearance()
    {
        var made = Automation.Run(Input(Orders, sum: "Сумма"));

        Assert.Equal(["Молоток", "Клещи"], made.Select(m => m["Проект"]).ToArray());
    }

    /// <summary>Незаполненная сумма — ноль: останавливать свод из-за пустой ячейки слишком строго.</summary>
    [Fact]
    public void An_empty_number_counts_as_zero()
    {
        var made = Automation.Run(Input(
        [
            AutomationRow.Of(("Проект", "Молоток"), ("Сумма", "10")),
            AutomationRow.Of(("Проект", "Молоток"), ("Сумма", "")),
        ], sum: "Сумма"));

        Assert.Equal("2",  Assert.Single(made)[SumByKeyAutomation.CountColumn]);
        Assert.Equal("10", made[0]["Сумма Сумма"]);
    }

    /// <summary>А вот текст вместо числа — отказ: молча пропущенная строка исказила бы итог.</summary>
    [Fact]
    public void Text_instead_of_a_number_stops_the_automation()
    {
        var trouble = Assert.Throws<AutomationRefusal>(() => Automation.Run(Input(
        [
            AutomationRow.Of(("Проект", "Молоток"), ("Сумма", "около трех")),
        ], sum: "Сумма")));

        Assert.Contains("около трех", trouble.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Колонки нет — отказ, называющий, какие есть. Считать ее пустой значило бы свести всю
    /// выгрузку в одну строку и не сказать почему.
    /// </summary>
    [Fact]
    public void An_unknown_column_is_refused_with_the_list_of_known_ones()
    {
        var trouble = Assert.Throws<AutomationRefusal>(
            () => Automation.Run(Input(Orders, key: "Заказчик")));

        Assert.Contains("Заказчик", trouble.Message, StringComparison.Ordinal);
        Assert.Contains("Проект",   trouble.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_key_it_refuses()
    {
        Assert.Throws<AutomationRefusal>(() => Automation.Run(Input(Orders, key: "")));
    }

    /// <summary>Строк не пришло — и сводить нечего; это не отказ.</summary>
    [Fact]
    public void No_rows_means_no_answer()
    {
        Assert.Empty(Automation.Run(Input([])));
    }
}
