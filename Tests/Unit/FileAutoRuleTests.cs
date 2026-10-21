using System.Text.Json;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Правило авто-привязки файлов: разбор/сериализация ConfigJson и построение искомого имени
/// «значение.формат». Источник — строковый ключ колонки (число = текст-поле, в том числе
/// «Описание»; «__title__» = системное «Название»). Неполное правило (без папки/формата/
/// источника) и нераспознанный ключ (легаси «__desc__») не применяются (null). Читается
/// и старый формат «sourceFieldId» (совместимость).
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

    [Fact]
    public void Roundtrips_system_column_source()
    {
        var key  = FileAutoRule.TitleSource;
        var back = FileAutoRule.FromConfig(new FileAutoRule(key, "/d", "pdf").ToConfigJson());
        Assert.NotNull(back);
        Assert.Equal(key, back!.SourceKey);
        Assert.Equal(0, back.SourceFieldId);   // системная колонка — не числовое поле
        Assert.True(back.SourceIsTitle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enabled_flag_roundtrips_without_losing_settings(bool enabled)
    {
        var rule = new FileAutoRule("42", "/data/files", "tar.gz", Recursive: true,
            PartialMatch: true, Enabled: enabled);
        var json = rule.ToConfigJson();
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(enabled, parsed.RootElement.GetProperty("auto").GetProperty("enabled").GetBoolean());
        Assert.Equal(rule, FileAutoRule.ReadConfigured(json));
        Assert.Equal(enabled ? rule : null, FileAutoRule.FromConfig(json));

        var restored = FileAutoRule.ReadConfigured(json)! with { Enabled = true };
        Assert.Equal(rule with { Enabled = true }, FileAutoRule.FromConfig(restored.ToConfigJson()));
    }

    [Theory]
    [InlineData("", "/data/files", "pdf")]
    [InlineData("42", "", "pdf")]
    [InlineData("__title__", "/data/files", "")]
    public void Disabled_incomplete_settings_are_kept_but_cannot_be_applied(string source, string folder, string format)
    {
        var draft = new FileAutoRule(source, folder, format, Recursive: true, PartialMatch: true, Enabled: false);
        Assert.Equal(draft, FileAutoRule.ReadConfigured(draft.ToConfigJson()));
        Assert.Null(FileAutoRule.FromConfig(draft.ToConfigJson()));
        Assert.Null(FileAutoRule.FromConfig((draft with { Enabled = true }).ToConfigJson()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"auto\":true}")]
    [InlineData("{\"auto\":{\"source\":\"42\",\"folder\":\"/d\",\"format\":\"pdf\",\"enabled\":\"false\"}}")]
    public void Invalid_structure_and_non_boolean_enabled_flags_do_not_activate_rules(string json)
    {
        Assert.Null(FileAutoRule.ReadConfigured(json));
        Assert.Null(FileAutoRule.FromConfig(json));
    }

    /// <summary>
    /// Легаси-ключ «__desc__» (описание было системной колонкой) больше не источник: правило
    /// отбрасывается целиком. Подставлять вместо него «Название» нельзя — авто-привязка молча
    /// переписала бы файл-столбец значениями чужой колонки.
    /// </summary>
    [Fact]
    public void Legacy_description_source_is_rejected()
    {
        Assert.Null(FileAutoRule.FromConfig(
            "{\"auto\":{\"source\":\"__desc__\",\"folder\":\"/d\",\"format\":\"pdf\"}}"));
    }

    /// <summary>
    /// Вложенные папки: флаг переживает сериализацию, а правило БЕЗ ключа «recursive»
    /// (создано до появления настройки) остается поиском по верхнему уровню — иначе у
    /// существующих полей молча поменялись бы значения.
    /// </summary>
    [Fact]
    public void Roundtrips_recursive_flag()
    {
        var back = FileAutoRule.FromConfig(new FileAutoRule("1", "/d", "pdf", Recursive: true).ToConfigJson());
        Assert.NotNull(back);
        Assert.True(back!.Recursive);
    }

    [Fact]
    public void Rule_without_recursive_key_is_top_level_only()
    {
        var rule = FileAutoRule.FromConfig("{\"auto\":{\"source\":\"1\",\"folder\":\"/d\",\"format\":\"pdf\"}}");
        Assert.NotNull(rule);
        Assert.False(rule!.Recursive);
        Assert.True(rule.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Roundtrips_partial_match_flag_without_changing_recursive_search(bool partialMatch)
    {
        var rule = new FileAutoRule("1", "/d", "pdf", Recursive: true, PartialMatch: partialMatch);
        var back = FileAutoRule.FromConfig(rule.ToConfigJson());
        Assert.Equal(rule, back);
        Assert.Equal(partialMatch, back!.PartialMatch);
        Assert.True(back.Recursive);
    }

    [Fact]
    public void Rule_without_partial_match_key_keeps_exact_matching()
    {
        var rule = FileAutoRule.FromConfig("{\"auto\":{\"source\":\"1\",\"folder\":\"/d\",\"format\":\"pdf\"}}");
        Assert.NotNull(rule);
        Assert.False(rule!.PartialMatch);
        Assert.True(rule.MatchesFileName("Отчет", "Отчет.pdf"));
        Assert.False(rule.MatchesFileName("Отчет", "Готовый Отчет.pdf"));
        Assert.False(rule.MatchesFileName("отчет", "Отчет.pdf"));
    }

    [Fact]
    public void Reads_legacy_source_field_id_format()
    {
        var rule = FileAutoRule.FromConfig("{\"auto\":{\"sourceFieldId\":7,\"folder\":\"/d\",\"format\":\"pdf\"}}");
        Assert.NotNull(rule);
        Assert.Equal("7", rule!.SourceKey);
        Assert.Equal(7, rule.SourceFieldId);
        Assert.False(rule.PartialMatch);
        Assert.True(rule.Enabled);
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
    [InlineData("Отчет", "Отчет.pdf")]
    [InlineData("Проценты_по_месяцам-2", "Проценты_по_месяцам-2.pdf")]
    [InlineData("", "")]     // пустое значение источника → нет искомого имени → «файл не найден»
    public void Target_file_name_is_value_dot_format(string sourceValue, string expected) =>
        Assert.Equal(expected, new FileAutoRule("1", "/d", "pdf").TargetFileName(sourceValue));

    [Theory]
    [InlineData("Отчет", "2026-Отчет-финал.pdf", "pdf", true)]
    [InlineData("Отчет", "Отчет.pdf", "pdf", true)]
    [InlineData("отчет", "Отчет.pdf", "pdf", false)]
    [InlineData("Отчет", "Отчет.PDF", "pdf", false)]
    [InlineData("Отчет", "Отчет.xlsx", "pdf", false)]
    [InlineData("Отчет", "Отчет.pdf.bak", "pdf", false)]
    [InlineData("pdf", "Отчет.pdf", "pdf", false)]
    [InlineData("Отчет", "Пакет-Отчет.tar.gz", "tar.gz", true)]
    [InlineData("tar", "Отчет.tar.gz", "tar.gz", false)]
    [InlineData("Отчет", "Пакет-Отчет.TAR.GZ", "tar.gz", false)]
    [InlineData("", "Отчет.pdf", "pdf", false)]
    [InlineData(null, "Отчет.pdf", "pdf", false)]
    [InlineData(" \t ", "Отчет.pdf", "pdf", false)]
    [InlineData("*", "Отчет.pdf", "pdf", false)]
    [InlineData("Отчет.1", "ОтчетX1.pdf", "pdf", false)]
    [InlineData("Отчет.1", "Пакет Отчет.1 готов.pdf", "pdf", true)]
    public void Partial_match_uses_literal_case_sensitive_name_without_the_configured_extension(
        string? sourceValue, string fileName, string format, bool expected)
    {
        var rule = new FileAutoRule("1", "/d", format, PartialMatch: true);
        Assert.Equal(expected, rule.MatchesFileName(sourceValue, fileName));
    }
}
