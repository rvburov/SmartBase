using System.Text.Json;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

[Collection(EgressCollection.Name)]
public sealed class ExtensionRequestsTests
{
    [Fact]
    public async Task A_read_query_uses_the_saved_connection_and_does_not_journal_personal_data()
    {
        var dir = Directory.CreateTempSubdirectory("extension-read-").FullName;
        using var registry = DatabaseRegistry.Open(Path.Combine(dir, "database.db"), Path.Combine(dir, "app.db"));
        var journal = new RunJournal(Path.Combine(dir, "runs.db"));
        RunJournal.Attach(journal);
        try
        {
            using var server = new TinyServer(_ => TinyServer.Reply(200, "{\"label\":\"Личные данные\"}"));
            var connection = registry.Integrations.Save(IntegrationConnection.New() with
                { Name = "Стенд", Address = $"http://127.0.0.1:{server.Port}" });
            var request = registry.Integrations.SaveRequest(IntegrationRequest.New() with
                { Name = "Карточка", ConnectionId = connection.Id, Path = "/items/{{поле.id}}" });
            var reader = new ExtensionRequests(registry.Integrations, connection.Id);
            var result = await reader.ReadAsync(request.Id, new Dictionary<string, string> { ["id"] = "42" });
            Assert.True(result.Ok, result.Problem);
            Assert.Equal("/items/42", Assert.Single(server.Seen).Path);
            using var json = JsonDocument.Parse(result.Json);
            Assert.Equal("Личные данные", json.RootElement[0].GetProperty("label").GetString());
            var entry = Assert.Single(journal.Page(new RunFilter(Channel: RunChannel.Request)));
            Assert.Equal("", entry.GotBody);
            Assert.Equal("", entry.SentBody);
        }
        finally { RunJournal.Attach(null); }
    }

    [Fact]
    public async Task A_read_cannot_use_a_write_request_or_another_source_connection()
    {
        var dir = Directory.CreateTempSubdirectory("extension-read-").FullName;
        using var registry = DatabaseRegistry.Open(Path.Combine(dir, "database.db"), Path.Combine(dir, "app.db"));
        using var server = new TinyServer(_ => TinyServer.Reply(200, "{}"));
        var a = registry.Integrations.Save(IntegrationConnection.New() with { Name = "A", Address = $"http://127.0.0.1:{server.Port}" });
        var b = registry.Integrations.Save(IntegrationConnection.New() with { Name = "B", Address = $"http://127.0.0.1:{server.Port}" });
        var write = registry.Integrations.SaveRequest(IntegrationRequest.New() with
            { Name = "Запись", ConnectionId = a.Id, Method = RequestMethod.Post, Path = "/items" });
        var foreign = registry.Integrations.SaveRequest(IntegrationRequest.New() with
            { Name = "Другой источник", ConnectionId = b.Id, Path = "/items" });
        var reader = new ExtensionRequests(registry.Integrations, a.Id);
        Assert.False((await reader.ReadAsync(write.Id, new Dictionary<string, string>())).Ok);
        Assert.False((await reader.ReadAsync(foreign.Id, new Dictionary<string, string>())).Ok);
        Assert.DoesNotContain(reader.Requests, r => r.Id == write.Id);
        Assert.Empty(server.Seen);
    }
}
