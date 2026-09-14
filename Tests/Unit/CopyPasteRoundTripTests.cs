using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Круг «показали → скопировали → вставили» по каждому типу столбца: значение, скопированное из
/// своей же таблицы, возвращается в базу ровно тем, чем было. Идет через те же функции, что и
/// программа: показ ячейки (<see cref="CellDisplayText"/>), формат буфера
/// (<see cref="TableClipboardFormat"/>) и разбор вставки (<see cref="TablePastePlanner"/>).
///
/// Связи здесь не проверяются — их названия находит база (<c>CellLinksWriteTests</c>); файлы и
/// вычисляемые столбцы вставкой не заполняются вовсе.
/// </summary>
public sealed class CopyPasteRoundTripTests
{
    private sealed record Case(string Name, PasteColumn Column, string Stored);

    private static readonly NumberFormat Money = new(NumberFormatKind.Currency, 2, true, "₽", true, DecimalMark.Comma);
    private static readonly NumberFormat Share = new(NumberFormatKind.Percent, 1, false, "₽", true, DecimalMark.Dot);

    private static readonly string[] Streets = ["Ул. Ленина", "Ул. Ленина, 5", "Синий"];

    private static IEnumerable<Case> Cases()
    {
        yield return new("название с запятой", new("__title__", "text", IsTitle: true), "Иванов, Петров и партнеры");
        yield return new("текст с пробелами, табуляцией и переводом строки",
                         new("1", "text"), "  строка 1\tи\nстрока 2  ");
        yield return new("ссылка", new("2", "url"), "https://example.org/a?b=1&c=\"2\"");
        yield return new("деньги с разрядами", new("3", "number", DecimalSeparator: DecimalMark.Comma, NumberFormat: Money), "1234567,25");
        yield return new("отрицательные деньги", new("3", "number", DecimalSeparator: DecimalMark.Comma, NumberFormat: Money), "-1200,75");
        yield return new("проценты с точкой", new("4", "number", DecimalSeparator: DecimalMark.Dot, NumberFormat: Share), "12.5");
        yield return new("снятая галочка", new("5", "checkbox"), "");
        yield return new("дата", new("6", "date"), "2026-02-01");
        yield return new("диапазон со временем", new("6", "date"), "2026-02-01T09:00/2026-02-03T18:00");
        yield return new("галочка", new("5", "checkbox"), "1");
        yield return new("статус", new("7", "status", Options: ["Запланировано", "В работе"]), "В работе");
        yield return new("мультисписок с запятой внутри значения",
                         new("8", "multiselect", Options: Streets, Separator: ", "),
                         MultiSelectValue.Serialize(["Ул. Ленина, 5", "Синий"]));
        yield return new("мультисписок через свой разделитель",
                         new("8", "multiselect", Options: Streets, Separator: " / "),
                         MultiSelectValue.Serialize(["Синий", "Ул. Ленина"]));
        yield return new("пользователь", new("9", "user", Options: ["Иванов И.", "Петрова А."]), "Петрова А.");
    }

    [Fact]
    public void Every_type_pastes_back_what_was_copied()
    {
        var cases = Cases().ToList();
        var shown = cases
            .Select(c => CellDisplayText.Of(c.Column.FieldType, c.Stored, c.Column.NumberFormat ?? NumberFormat.Default,
                                            c.Column.Separator ?? ", "))
            .ToList();

        // Одна строка буфера на все типы — как копирование строки таблицы целиком.
        var pasted = TableClipboardFormat.Parse(TableClipboardFormat.Build([shown]));
        Assert.Single(pasted);
        Assert.Equal(cases.Count, pasted[0].Count);

        var plan = TablePastePlanner.Plan(pasted, startRow: 0, startColumn: 0,
                                          [.. cases.Select(c => c.Column)], rowCount: 1);

        Assert.Equal(0, plan.SkippedCount);
        Assert.Equal(cases.Count, plan.Cells.Count);
        for (var i = 0; i < cases.Count; i++)
            Assert.True(cases[i].Stored == plan.Cells[i].StorageValue,
                        $"{cases[i].Name}: было «{cases[i].Stored}», показано «{shown[i]}», вернулось «{plan.Cells[i].StorageValue}»");
    }
}
