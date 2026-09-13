using ClosedXML.Excel;
using SmartBase.Extensions;
using SmartBase.Extensions.Automations;

namespace SmartBase.Tests.Extensions.Automations;

/// <summary>
/// Действие «Выгрузить строки в книгу Excel» (<see cref="SheetAction"/>).
///
/// Главная проверка здесь — не разметка книги, а граница: <b>действие не касается диска</b>.
/// Оно возвращает байты и имя, а кладет файл оболочка — через папку из политики и со следом в
/// журнале безопасности. Проверяется и то, ради чего выгрузку делают: число в книге лежит
/// числом, иначе в ней не сложить итог.
/// </summary>
public sealed class SheetActionTests
{
    private static readonly SheetAction Action = new();

    private static ActionInput Input(IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
                                     params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in settings) values[key] = value;

        return new ActionInput(values, Rows: rows);
    }

    private static Dictionary<string, string> Row(params (string Column, string Value)[] cells)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in cells) row[column] = value;
        return row;
    }

    private static readonly ActionContext Context =
        new(null, 0, 0, new DateTimeOffset(2026, 9, 13, 8, 30, 0, TimeSpan.Zero));

    private static IXLWorksheet Sheet(byte[] bytes)
    {
        var book = new XLWorkbook(new MemoryStream(bytes));
        return book.Worksheets.First();
    }

    [Fact]
    public void It_is_an_automation_that_makes_a_file()
    {
        Assert.Equal(ExtensionActionKind.Rows, Action.Kind);
        Assert.True(Action.MakesFile);
    }

    [Fact]
    public void Rows_become_a_sheet_with_a_header()
    {
        var result = Action.Run(Context, Input(
        [
            Row(("Проект", "Молоток"), ("Сумма", "10")),
            Row(("Проект", "Клещи"),   ("Сумма", "2,5")),
        ]));

        Assert.True(result.Ok, result.Explanation);
        Assert.NotNull(result.File);

        var sheet = Sheet(result.File!);

        Assert.Equal("Проект",  sheet.Cell(1, 1).GetString());
        Assert.Equal("Сумма",   sheet.Cell(1, 2).GetString());
        Assert.Equal("Молоток", sheet.Cell(2, 1).GetString());
        Assert.Equal("Клещи",   sheet.Cell(3, 1).GetString());
    }

    /// <summary>Число ложится числом: ради итога в книге выгрузку чаще всего и делают.</summary>
    [Fact]
    public void Numbers_are_numbers_in_the_book()
    {
        var result = Action.Run(Context, Input([Row(("Сумма", "2,5"))]));

        var cell = Sheet(result.File!).Cell(2, 1);

        Assert.Equal(XLDataType.Number, cell.DataType);
        Assert.Equal(2.5, cell.GetDouble());
    }

    /// <summary>
    /// Строки одной выгрузки бывают разного состава: колонка, встретившаяся только в последней,
    /// теряться не должна.
    /// </summary>
    [Fact]
    public void Columns_are_collected_from_all_rows()
    {
        var result = Action.Run(Context, Input(
        [
            Row(("Проект", "Молоток")),
            Row(("Проект", "Клещи"), ("Заказчик", "Цех 3")),
        ]));

        var sheet = Sheet(result.File!);

        Assert.Equal("Заказчик", sheet.Cell(1, 2).GetString());
        Assert.Equal("",         sheet.Cell(2, 2).GetString());
        Assert.Equal("Цех 3",    sheet.Cell(3, 2).GetString());
    }

    /// <summary>Имя файла предлагает действие, а момент к нему добавит оболочка.</summary>
    [Fact]
    public void The_file_is_named_but_not_placed()
    {
        var result = Action.Run(Context, Input([Row(("Проект", "Молоток"))],
                                               (SheetAction.FileSetting, "Заказы")));

        Assert.Equal("Заказы.xlsx", result.FileName);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, result.FileName);
    }

    /// <summary>Двоеточие в имени листа Excel не примет — поправить дешевле, чем объяснять.</summary>
    [Fact]
    public void A_forbidden_sheet_name_is_fixed_not_refused()
    {
        var result = Action.Run(Context, Input([Row(("Проект", "Молоток"))],
                                               (SheetAction.SheetSetting, "Заказы: сентябрь")));

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal("Заказы  сентябрь", Sheet(result.File!).Name);
    }

    [Fact]
    public void Without_rows_it_refuses_with_words()
    {
        var result = Action.Run(Context, Input([]));

        Assert.False(result.Ok);
        Assert.Contains("не передал ни одной строки", result.Explanation, StringComparison.Ordinal);
    }
}
