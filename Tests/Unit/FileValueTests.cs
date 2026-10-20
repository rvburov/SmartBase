using System.Text.Json;
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

    [Theory]
    [InlineData("/data/Акт, приложение.pdf")]
    [InlineData(@"C:\Документы\Акт, приложение.pdf")]
    [InlineData("42,5")]
    public void ReadPaths_keeps_legacy_values_whole_including_commas(string raw) =>
        Assert.Equal(new[] { raw }, FileValue.ReadPaths(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ReadPaths_empty_has_no_files(string? raw) =>
        Assert.Empty(FileValue.ReadPaths(raw));

    [Fact]
    public void Serialize_keeps_empty_and_single_values_in_legacy_format()
    {
        Assert.Equal("", FileValue.Serialize([]));
        Assert.Equal("", FileValue.Serialize(["", ""]));
        const string path = "/data/Акт, приложение.pdf";
        Assert.Equal(path, FileValue.Serialize([path]));
        Assert.Equal(path, FileValue.Serialize(["", path, path, ""]));
    }

    [Fact]
    public void Multiple_paths_roundtrip_separately_when_a_file_name_contains_a_comma()
    {
        string[] paths = ["/data/Акт, приложение.pdf", @"C:\Документы\Отчет.xlsx"];
        var raw = FileValue.Serialize(paths);

        using var json = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.Equal(paths, json.RootElement.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!));
        Assert.Equal(paths, FileValue.ReadPaths(raw));
        Assert.Equal("Акт, приложение.pdf, Отчет.xlsx", FileValue.DisplayName(raw));
    }

    [Fact]
    public void Serialize_removes_empty_and_duplicate_paths_with_ordinal_comparison()
    {
        var raw = FileValue.Serialize(["", "/data/Отчет.pdf", "/data/Отчет.pdf", "/data/отчет.pdf", ""]);
        Assert.Equal(new[] { "/data/Отчет.pdf", "/data/отчет.pdf" }, FileValue.ReadPaths(raw));
        Assert.Empty(FileValue.ReadPaths("{\"paths\":[]}"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"paths\":[\"\\ud800\"]}")]
    [InlineData("{\"paths\":\"/data/Акт.pdf\"}")]
    [InlineData("{\"paths\":[\"/data/Акт.pdf\",7]}")]
    [InlineData("{\"paths\":[null]}")]
    [InlineData("{\"paths\":null}")]
    [InlineData("{\"other\":[\"/data/Акт.pdf\"]}")]
    [InlineData("[\"/data/Акт.pdf\",\"/data/Отчет.pdf\"]")]
    public void Malformed_or_unrelated_json_remains_a_legacy_value(string raw) =>
        Assert.Equal(new[] { raw }, FileValue.ReadPaths(raw));
}
