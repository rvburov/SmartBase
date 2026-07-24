using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правило авто-привязки файлов: разбор/сериализация ConfigJson и построение искомого имени
/// «значение.формат». Источник — строковый ключ колонки (число = текст-поле; «__title__»/
/// «__desc__» = системные «Название»/«Описание»). Неполное правило (без папки/формата/источника)
/// не применяется (null). Читается и старый формат «sourceFieldId» (совместимость).
/// </summary>
public sealed class FileAutoRuleTests
{
    [Fact]
    public void Roundtrips_through_config_json()
    {
        var rule = new FileAutoRule("42", "/data/files", "pdf");
        var back = FileAutoRule.FromConfig(rule.ToConfigJson());
        Assert.Equal(rule, back);
        Assert.Equal(42, back!.SourceFieldId);
    }

    [Theory]
    [InlineData("__title__")]
    [InlineData("__desc__")]
    public void Roundtrips_system_column_source(string key)
    {
        var back = FileAutoRule.FromConfig(new FileAutoRule(key, "/d", "pdf").ToConfigJson());
        Assert.NotNull(back);
        Assert.Equal(key, back!.SourceKey);
        Assert.Equal(0, back.SourceFieldId);   // системная колонка — не числовое поле
        Assert.Equal(key == FileAutoRule.TitleSource, back.SourceIsTitle);
        Assert.Equal(key == FileAutoRule.DescSource,  back.SourceIsDesc);
    }

    [Fact]
    public void Reads_legacy_source_field_id_format()
    {
        var rule = FileAutoRule.FromConfig("{\"auto\":{\"sourceFieldId\":7,\"folder\":\"/d\",\"format\":\"pdf\"}}");
        Assert.NotNull(rule);
        Assert.Equal("7", rule!.SourceKey);
        Assert.Equal(7, rule.SourceFieldId);
    }

    [Fact]
    public void Normalizes_leading_dot_and_spaces_in_format()
    {
        var rule = FileAutoRule.FromConfig("{\"auto\":{\"source\":\"1\",\"folder\":\" /d \",\"format\":\" .PDF \"}}");
        Assert.NotNull(rule);
        Assert.Equal("/d", rule!.Folder);
        Assert.Equal("PDF", rule.Format);   // точку и пробелы срезаем, регистр НЕ трогаем
    }

    [Theory]
    [InlineData("{}")]                                                             // нет auto
    [InlineData("{\"auto\":{\"source\":\"\",\"folder\":\"/d\",\"format\":\"pdf\"}}")]     // нет источника
    [InlineData("{\"auto\":{\"sourceFieldId\":0,\"folder\":\"/d\",\"format\":\"pdf\"}}")] // источник 0 (старый формат)
    [InlineData("{\"auto\":{\"source\":\"1\",\"folder\":\"\",\"format\":\"pdf\"}}")]      // нет папки
    [InlineData("{\"auto\":{\"source\":\"1\",\"folder\":\"/d\",\"format\":\"\"}}")]       // нет формата
    [InlineData("не json")]
    [InlineData(null)]
    public void Incomplete_rule_is_null(string? config) =>
        Assert.Null(FileAutoRule.FromConfig(config));

    [Theory]
    [InlineData("Отчёт", "Отчёт.pdf")]
    [InlineData("Проценты_по_месяцам-2", "Проценты_по_месяцам-2.pdf")]
    [InlineData("", "")]     // пустое значение источника → нет искомого имени → «файл не найден»
    public void Target_file_name_is_value_dot_format(string sourceValue, string expected) =>
        Assert.Equal(expected, new FileAutoRule("1", "/d", "pdf").TargetFileName(sourceValue));
}
