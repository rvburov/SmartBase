using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правило авто-привязки файлов: разбор/сериализация ConfigJson и построение искомого имени
/// «значение.формат». Неполное правило (без папки/формата/источника) не применяется (null).
/// </summary>
public sealed class FileAutoRuleTests
{
    [Fact]
    public void Roundtrips_through_config_json()
    {
        var rule = new FileAutoRule(42, "/data/files", "pdf");
        var back = FileAutoRule.FromConfig(rule.ToConfigJson());
        Assert.Equal(rule, back);
    }

    [Fact]
    public void Normalizes_leading_dot_and_spaces_in_format()
    {
        var rule = FileAutoRule.FromConfig("{\"auto\":{\"sourceFieldId\":1,\"folder\":\" /d \",\"format\":\" .PDF \"}}");
        Assert.NotNull(rule);
        Assert.Equal("/d", rule!.Folder);
        Assert.Equal("PDF", rule.Format);   // точку и пробелы срезаем, регистр НЕ трогаем
    }

    [Theory]
    [InlineData("{}")]                                                             // нет auto
    [InlineData("{\"auto\":{\"sourceFieldId\":0,\"folder\":\"/d\",\"format\":\"pdf\"}}")] // нет источника
    [InlineData("{\"auto\":{\"sourceFieldId\":1,\"folder\":\"\",\"format\":\"pdf\"}}")]   // нет папки
    [InlineData("{\"auto\":{\"sourceFieldId\":1,\"folder\":\"/d\",\"format\":\"\"}}")]    // нет формата
    [InlineData("не json")]
    [InlineData(null)]
    public void Incomplete_rule_is_null(string? config) =>
        Assert.Null(FileAutoRule.FromConfig(config));

    [Theory]
    [InlineData("Отчёт", "Отчёт.pdf")]
    [InlineData("Проценты_по_месяцам-2", "Проценты_по_месяцам-2.pdf")]
    [InlineData("", "")]     // пустое значение источника → нет искомого имени → «файл не найден»
    public void Target_file_name_is_value_dot_format(string sourceValue, string expected) =>
        Assert.Equal(expected, new FileAutoRule(1, "/d", "pdf").TargetFileName(sourceValue));
}
