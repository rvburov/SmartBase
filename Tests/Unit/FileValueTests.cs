using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Показ имени файла для типа «файл»: абсолютный путь сворачивается до базового имени;
/// значение без разделителей пути (в т.ч. доставшееся от прежнего типа поля) — как есть.
/// Единый источник и для ячейки/страницы записи, и для списка значений фильтра.
/// </summary>
public sealed class FileValueTests
{
    [Theory]
    [InlineData("/Users/roman/Документы/Договор.pdf", "Договор.pdf")]
    [InlineData(@"C:\Users\roman\Отчет.xlsx",         "Отчет.xlsx")]
    [InlineData("Проценты_по_месяцам-2.xlsx",         "Проценты_по_месяцам-2.xlsx")]
    public void DisplayName_returns_basename_for_paths(string raw, string expected) =>
        Assert.Equal(expected, FileValue.DisplayName(raw));

    [Theory]
    [InlineData("romanburov@macbook-pro-m4")]   // осталось от типа «пользователь»
    [InlineData("лолоолтлотлот")]               // произвольный текст
    [InlineData("42,5")]                        // осталось от «числа»
    public void DisplayName_keeps_plain_values_as_is(string raw) =>
        Assert.Equal(raw, FileValue.DisplayName(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void DisplayName_empty_is_empty(string? raw) =>
        Assert.Equal("", FileValue.DisplayName(raw));
}
