using SmartBase.Extensions;
using SmartBase.Extensions.PdfWatermark;

namespace SmartBase.Tests.Extensions.PdfWatermark;

/// <summary>
/// Контракт ИМЕННО этого расширения: прибитый Id, объявленные настройки и текст предупреждения.
/// Общие правила живут в Tests/Extensions/Registry — здесь только то, что уходит вместе с
/// расширением при удалении.
///
/// CreateWindow() здесь не зовется и звать нельзя: Avalonia в тест-хосте не поднята.
/// </summary>
public sealed class PdfWatermarkExtensionTests
{
    [Fact]
    public void Extension_id_is_frozen() =>
        Assert.Equal("pdf-watermark", new PdfWatermarkExtension().Id);

    [Fact]
    public void Extension_is_registered()
    {
        var found = ExtensionRegistry.FindById("pdf-watermark");

        Assert.NotNull(found);
        Assert.IsType<PdfWatermarkExtension>(found);
    }

    /// <summary>
    /// Четыре настройки, и порядок значим — так они и лягут полями на экране: сперва то, что
    /// вводят руками (текст предупреждения), затем то, что ведет само окно.
    /// </summary>
    [Fact]
    public void Settings_are_the_warning_and_the_target()
    {
        var keys = ((IExtension)new PdfWatermarkExtension()).Settings.Select(s => s.Key).ToList();

        Assert.Equal([
            PdfWatermarkExtension.WarningKey,
            PdfWatermarkExtension.OutputKey,
            PdfWatermarkExtension.DatabaseKey,
            PdfWatermarkExtension.CollectionKey,
        ], keys);
    }

    /// <summary>ФИО получателя настройкой быть НЕ должно: получатель меняется на каждую выдачу,
    /// и подставленная из настроек чужая фамилия уехала бы на документ.</summary>
    [Fact]
    public void Holder_is_not_a_setting()
    {
        var labels = ((IExtension)new PdfWatermarkExtension()).Settings.Select(s => s.Label);

        Assert.DoesNotContain(labels, l => l.Contains("Фамилия", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ключи уходят в app.db приставкой «ext.pdf-watermark.&lt;Key&gt;» и должны быть
    /// разными: тезки читали бы чужое значение.</summary>
    [Fact]
    public void Setting_keys_are_unique_and_labelled()
    {
        var settings = ((IExtension)new PdfWatermarkExtension()).Settings;

        Assert.Equal(settings.Count, settings.Select(s => s.Key).Distinct().Count());
        Assert.All(settings, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_warning_falls_back_to_the_default(string? raw) =>
        Assert.Equal(PdfWatermarkExtension.DefaultWarning, PdfWatermarkExtension.Warning(raw));

    [Fact]
    public void Own_warning_is_taken_trimmed() =>
        Assert.Equal("Только для служебного пользования",
                     PdfWatermarkExtension.Warning("  Только для служебного пользования  "));

    [Fact]
    public void Description_names_the_journal_columns()
    {
        // Описание — единственное место, где человек видит требуемую структуру журнала, не
        // открывая окно. Разъедется с JournalSchema — люди будут заводить не те коллекции.
        var text = new PdfWatermarkExtension().MarkdownDescription;

        Assert.All(JournalSchema.Columns, column => Assert.Contains(column, text, StringComparison.Ordinal));
    }
}
