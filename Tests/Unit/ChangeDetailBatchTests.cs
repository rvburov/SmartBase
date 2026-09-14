using SmartBase.Utils;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Строка журнала пакетной правки ячеек: операция, «до → после» первых изменений и счет
/// остальных — и все это в пределах одной короткой строки журнала.
/// </summary>
public sealed class ChangeDetailBatchTests
{
    [Fact]
    public void Few_changes_are_listed_in_full()
    {
        var text = ChangeDetail.Batch("Вставка", 2,
        [
            ("Договор 12", "Телефон", "123", "456"),
            ("Договор 13", "Телефон", "", "789"),
        ]);

        Assert.Equal("Вставка: Договор 12 (Телефон: «123» → «456»); Договор 13 (Телефон: (пусто) → «789»)", text);
    }

    /// <summary>Большой пакет: в строку попадает сколько влезает, остальное — счетом, и строка не
    /// раздувается.</summary>
    [Fact]
    public void The_rest_is_counted_and_the_line_stays_short()
    {
        var sample = Enumerable.Range(1, 5)
            .Select(i => ($"Запись с длинным названием {i}", "Описание", new string('а', 50), new string('б', 50)))
            .ToList();

        var text = ChangeDetail.Batch("Очистка", 1200, sample);

        Assert.True(text.Length <= 200);
        Assert.StartsWith("Очистка: Запись с длинным названием 1", text);
        Assert.Contains("и еще 1", text);
    }
}
