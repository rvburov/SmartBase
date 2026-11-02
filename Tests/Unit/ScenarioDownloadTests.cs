using System.Text.Json;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>Настройка получения файла переживает хранение и не требует коллекции.</summary>
public sealed class ScenarioDownloadTests
{
    [Fact]
    public void Download_configuration_keeps_request_and_filename_expression_without_a_binding()
    {
        var step = ScenarioStep.OfDownload(17, "{{Вложение.Имя}}");
        Assert.Empty(step.Problems());
        var restored = JsonSerializer.Deserialize<ScenarioStep>(JsonSerializer.Serialize(step));
        Assert.NotNull(restored);
        Assert.Equal(ScenarioStepKind.Download, restored.Kind);
        Assert.Equal(17, restored.RequestId);
        Assert.Equal("{{Вложение.Имя}}", restored.Text);
        Assert.Equal(0, restored.CollectionId);
        Assert.Equal(0, restored.DownloadWaitSeconds);
        Assert.Empty(restored.Problems());
    }

    [Fact]
    public void Stored_download_without_the_new_field_keeps_the_original_single_attempt_behavior()
    {
        var step = JsonSerializer.Deserialize<ScenarioStep>(
            """{"Kind":"Download","RequestId":17,"Text":"{{Вложение.Имя}}"}""");

        Assert.NotNull(step);
        Assert.Equal(0, step.DownloadWaitSeconds);
        Assert.Empty(step.Problems());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(300)]
    public void Download_wait_is_explicit_and_survives_stored_step_round_trip(int seconds)
    {
        Assert.Equal(300, ScenarioStep.MaxDownloadWaitSeconds);
        var step = ScenarioStep.OfDownload(17, "{{Вложение.Имя}}", seconds);

        Assert.Equal(seconds, step.DownloadWaitSeconds);
        Assert.Empty(step.Problems());
        var restored = JsonSerializer.Deserialize<ScenarioStep>(JsonSerializer.Serialize(step));
        Assert.NotNull(restored);
        Assert.Equal(seconds, restored.DownloadWaitSeconds);
        Assert.Equal(step.RequestId, restored.RequestId);
        Assert.Equal(step.Text, restored.Text);
        Assert.Empty(restored.Problems());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void Download_wait_outside_the_supported_budget_cannot_be_saved(int seconds) =>
        Assert.NotEmpty(ScenarioStep.OfDownload(17, "Вложение.xlsx", seconds).Problems());

    [Theory]
    [InlineData(0, "Вложение.xlsx", "запрос")]
    [InlineData(-1, "Вложение.xlsx", "запрос")]
    [InlineData(1, "", "имя файла")]
    [InlineData(1, "   ", "имя файла")]
    public void Incomplete_download_cannot_be_saved(int requestId, string name, string problem) =>
        Assert.Contains(ScenarioStep.OfDownload(requestId, name).Problems(),
            text => text.Contains(problem, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Long_filename_expression_is_rejected() =>
        Assert.Contains(ScenarioStep.OfDownload(1, new string('x', ScenarioStep.MaxTextLength + 1)).Problems(),
            text => text.Contains("длиннее", StringComparison.Ordinal));

    [Fact]
    public void Download_summary_names_the_request_and_filename_expression()
    {
        var text = ScenarioStep.OfDownload(23, "{{Вложение.Имя}}").Describe("Файл сообщения");
        Assert.Contains("Получить файл", text);
        Assert.Contains("Файл сообщения", text);
        Assert.Contains("{{Вложение.Имя}}", text);
        Assert.DoesNotContain("коллекцию", text);
    }
}
