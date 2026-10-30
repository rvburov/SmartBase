using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Шаг получения файла переносит свой запрос и не теряет его при удалении карточек.</summary>
public sealed class ScenarioDownloadTransferTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("sb-download-transfer-").FullName;

    [Fact]
    public void Download_travels_with_the_request_and_connection_without_a_collection_binding()
    {
        using var source = Machine("source");
        var original = Setup(source.Integrations);
        var exported = ScenarioTransfer.Export(source.Integrations, [original], [], "Источник", Now);
        Assert.Equal(3, exported.Version);
        Assert.Equal("download", Assert.Single(Assert.Single(exported.Scenarios).Steps).Kind);
        Assert.Single(exported.Requests);
        Assert.Single(exported.Connections);

        var parsed = ScenarioBundle.TryParse(exported.ToJson(), out var problem);
        Assert.NotNull(parsed);
        Assert.Empty(problem);

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
        Assert.NotEqual(original.Steps[0].RequestId, step.RequestId);
        var request = target.Integrations.FindRequest(step.RequestId);
        Assert.NotNull(request);
        Assert.Equal("Получить вложение", request.Name);
        Assert.Equal(RequestMethod.Get, request.Method);
        Assert.Equal("/files/42", request.Path);
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

    private DatabaseRegistry Machine(string name) =>
        DatabaseRegistry.Open(Path.Combine(_dir, name + ".db"), Path.Combine(_dir, name + "-app.db"));

    private static Scenario Setup(IntegrationsRegistry registry, string name = "Получение документов",
                                  string requestName = "Получить вложение", string connectionName = "Файловый сервис")
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
            Method = RequestMethod.Get,
            Path = "/files/42",
        });
        return registry.SaveScenario(Scenario.New() with
        {
            Name = name,
            Steps = [ScenarioStep.OfDownload(request.Id, "{{Вложение.Имя}}")],
        });
    }

    public void Dispose()
    {
        SqliteGate.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
