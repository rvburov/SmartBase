using System.Text.Json;
using System.Text.Json.Nodes;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Шаг получения файла переносит свой запрос и не теряет его при удалении карточек.</summary>
public sealed class ScenarioDownloadTransferTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("sb-download-transfer-").FullName;

    [Fact]
    public void Download_wait_is_opt_in_for_existing_step_constructors()
    {
        Assert.Equal(300, ScenarioStep.MaxDownloadWaitSeconds);
        Assert.Equal(0, new ScenarioStep(ScenarioStepKind.Download, 42, "Документы.xlsx").DownloadWaitSeconds);
        Assert.Equal(0, ScenarioStep.OfDownload(42, "Документы.xlsx").DownloadWaitSeconds);
        Assert.Equal(0, new BundleStep("download", "Получить вложение", "Документы.xlsx",
            "", "", "", "", null, "", null).DownloadWaitSeconds);
    }

    [Fact]
    public void Download_travels_with_the_request_and_connection_without_a_collection_binding()
    {
        using var source = Machine("source");
        var original = Setup(source.Integrations);
        Assert.Equal(0, Assert.Single(original.Steps).DownloadWaitSeconds);
        var exported = ScenarioTransfer.Export(source.Integrations, [original], [], "Источник", Now);
        Assert.Equal(3, exported.Version);
        var bundledStep = Assert.Single(Assert.Single(exported.Scenarios).Steps);
        Assert.Equal("download", bundledStep.Kind);
        Assert.Equal(0, bundledStep.DownloadWaitSeconds);
        Assert.Single(exported.Requests);
        Assert.Single(exported.Connections);

        // Прежний выпуск вообще не писал поле ожидания; такой файл по-прежнему означает 0.
        var legacyJson = JsonNode.Parse(exported.ToJson())!;
        var legacyStep = legacyJson["scenarios"]![0]!["steps"]![0]!.AsObject();
        legacyStep.Remove("downloadWaitSeconds");
        Assert.False(legacyStep.ContainsKey("downloadWaitSeconds"));
        var parsed = ScenarioBundle.TryParse(legacyJson.ToJsonString(), out var problem);
        Assert.NotNull(parsed);
        Assert.Empty(problem);
        Assert.Equal(3, parsed.Version);
        Assert.Equal(0, Assert.Single(Assert.Single(parsed.Scenarios).Steps).DownloadWaitSeconds);

        using var target = Machine("target");
        // На другой машине такой же числовой ключ уже занят другим запросом.
        Setup(target.Integrations, "Прежний сценарий", "Прежний запрос", "Прежнее соединение");
        var report = ScenarioTransfer.Import(parsed!, target.Integrations, [], Now);
        Assert.Equal(1, report.Scenarios);
        var imported = Assert.Single(target.Integrations.Scenarios(), s => s.Name == original.Name);
        Assert.False(imported.Enabled);
        var step = Assert.Single(imported.Steps);
        Assert.Equal(ScenarioStepKind.Download, step.Kind);
        Assert.Equal("{{Вложение.Имя}}", step.Text);
        Assert.Equal(0, step.DownloadWaitSeconds);
        Assert.NotEqual(original.Steps[0].RequestId, step.RequestId);
        var request = target.Integrations.FindRequest(step.RequestId);
        Assert.NotNull(request);
        Assert.Equal("Получить вложение", request.Name);
        Assert.Equal(RequestMethod.Get, request.Method);
        Assert.Equal("/files/42", request.Path);
        Assert.Null(target.Integrations.Binding(request.Id));
        Assert.Equal("Файловый сервис", target.Integrations.Find(request.ConnectionId)!.Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Post_download_wait_method_and_event_body_survive_bundle_round_trip_with_remapped_request_keys(
        int waitSeconds)
    {
        const string body = """{"messageKey":{{поле.message-id}},"assetKey":{{поле.attachment-id}}}""";
        using var source = Machine("post-source");
        var original = Setup(source.Integrations, method: RequestMethod.Post, body: body,
            waitSeconds: waitSeconds);
        Assert.Equal(waitSeconds, Assert.Single(original.Steps).DownloadWaitSeconds);
        var exported = ScenarioTransfer.Export(source.Integrations, [original], [], "Источник", Now);
        Assert.Equal(4, exported.Version);
        Assert.Equal(waitSeconds, Assert.Single(Assert.Single(exported.Scenarios).Steps).DownloadWaitSeconds);
        var bundledRequest = Assert.Single(exported.Requests);
        Assert.Equal("post", bundledRequest.Method);
        Assert.Equal(body, bundledRequest.Body);
        var json = exported.ToJson();
        using var document = JsonDocument.Parse(json);
        Assert.Equal(waitSeconds, document.RootElement.GetProperty("scenarios")[0]
            .GetProperty("steps")[0].GetProperty("downloadWaitSeconds").GetInt32());
        var parsed = ScenarioBundle.TryParse(json, out var problem);
        Assert.NotNull(parsed);
        Assert.Empty(problem);
        Assert.Equal(waitSeconds, Assert.Single(Assert.Single(parsed.Scenarios).Steps).DownloadWaitSeconds);

        using var target = Machine("post-target");
        Setup(target.Integrations, "Прежний сценарий", "Прежний запрос", "Прежнее соединение");
        var report = ScenarioTransfer.Import(parsed!, target.Integrations, [], Now);

        Assert.Equal(1, report.Scenarios);
        var imported = Assert.Single(target.Integrations.Scenarios(), scenario => scenario.Name == original.Name);
        Assert.False(imported.Enabled);
        var step = Assert.Single(imported.Steps);
        Assert.Equal(ScenarioStepKind.Download, step.Kind);
        Assert.Equal("{{Вложение.Имя}}", step.Text);
        Assert.Equal(waitSeconds, step.DownloadWaitSeconds);
        Assert.NotEqual(original.Steps[0].RequestId, step.RequestId);
        var request = target.Integrations.FindRequest(step.RequestId);
        Assert.NotNull(request);
        Assert.Equal(RequestMethod.Post, request.Method);
        Assert.Equal(body, request.Body);
        Assert.Equal("/files/42", request.Path);
        Assert.Equal(RequestPaging.None, request.Paging);
        Assert.Null(target.Integrations.Binding(request.Id));
        Assert.Equal("Файловый сервис", target.Integrations.Find(request.ConnectionId)!.Name);
    }

    [Fact]
    public void A_request_used_to_download_a_file_cannot_be_deleted()
    {
        using var machine = Machine("used");
        var scenario = Setup(machine.Integrations);
        var requestId = scenario.Steps[0].RequestId;
        Assert.Equal(scenario.Id, Assert.Single(machine.Integrations.ScenariosUsing(requestId)).Id);
        Assert.Throws<InvalidOperationException>(() => machine.Integrations.DeleteRequest(requestId));
        Assert.NotNull(machine.Integrations.FindRequest(requestId));
    }

    [Fact]
    public void A_download_cannot_be_saved_with_a_missing_request()
    {
        using var machine = Machine("missing");
        var scenario = Scenario.New() with
        {
            Name = "Скачать файл",
            Steps = [ScenarioStep.OfDownload(4321, "Документы.xlsx")],
        };
        var error = Assert.Throws<InvalidOperationException>(() => machine.Integrations.SaveScenario(scenario));
        Assert.Contains("запрос шага не найден", error.Message, StringComparison.Ordinal);
        Assert.Empty(machine.Integrations.Scenarios());
    }

    [Fact]
    public void A_bundle_without_download_keeps_the_original_plain_version()
    {
        using var machine = Machine("plain");
        var scenario = machine.Integrations.SaveScenario(Scenario.New() with
        {
            Name = "Сообщение",
            Steps = [ScenarioStep.OfNotice("Готово")],
        });
        var bundle = ScenarioTransfer.Export(machine.Integrations, [scenario], [], "Источник", Now);
        Assert.Equal(1, bundle.Version);
    }

    [Theory]
    [InlineData(false, 0, 2)]
    [InlineData(true, 0, 3)]
    [InlineData(true, 45, 4)]
    public void A_stream_bundle_requires_only_the_version_of_its_most_recent_feature(
        bool download, int waitSeconds, int expectedVersion)
    {
        using var machine = Machine("stream");
        var cards = machine.Integrations;
        var original = Setup(cards, waitSeconds: waitSeconds);
        var request = cards.FindRequest(Assert.Single(original.Steps).RequestId)!;
        var stream = cards.SaveStream(IntegrationStream.New() with
        {
            Name = "События о файлах",
            ConnectionId = request.ConnectionId,
            Path = "/events",
        });
        var scenario = cards.SaveScenario(original with
        {
            Trigger = ScenarioTrigger.OnStream(stream.Id),
            Steps = download ? original.Steps : [ScenarioStep.OfNotice("Пришло событие")],
        });

        var bundle = ScenarioTransfer.Export(cards, [scenario], [], "Источник", Now);

        Assert.Equal(expectedVersion, bundle.Version);
        Assert.Equal(stream.Name, Assert.Single(bundle.StreamList).Name);
        if (download)
            Assert.Equal(waitSeconds, Assert.Single(Assert.Single(bundle.Scenarios).Steps).DownloadWaitSeconds);
        else
            Assert.Empty(bundle.Requests);
    }

    private DatabaseRegistry Machine(string name) =>
        DatabaseRegistry.Open(Path.Combine(_dir, name + ".db"), Path.Combine(_dir, name + "-app.db"));

    private static Scenario Setup(IntegrationsRegistry registry, string name = "Получение документов",
                                  string requestName = "Получить вложение", string connectionName = "Файловый сервис",
                                  RequestMethod method = RequestMethod.Get, string body = "", int waitSeconds = 0)
    {
        var connection = registry.Save(IntegrationConnection.New() with
        {
            Name = connectionName,
            Address = "https://files.example.invalid",
        });
        var request = registry.SaveRequest(IntegrationRequest.New() with
        {
            Name = requestName,
            ConnectionId = connection.Id,
            Method = method,
            Path = "/files/42",
            Body = body,
        });
        return registry.SaveScenario(Scenario.New() with
        {
            Name = name,
            Steps = [ScenarioStep.OfDownload(request.Id, "{{Вложение.Имя}}", waitSeconds)],
        });
    }

    public void Dispose()
    {
        SqliteGate.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
