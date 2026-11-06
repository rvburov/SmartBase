using System.Text.Json;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

[Collection(EgressCollection.Name)]
public sealed class RequestExchangeTests
{
    private static IntegrationConnection Connection(TinyServer server) => IntegrationConnection.New() with
    { Id = 1, Name = "Стенд", Address = $"http://127.0.0.1:{server.Port}", TimeoutSeconds = 2, Retries = 3 };

    [Fact]
    public async Task Binary_blocks_are_not_reencoded_and_use_their_actual_content_length()
    {
        using var server = new TinyServer(_ => TinyServer.Reply(200, ""));
        var data = new byte[] { 255, 0, 128, 13, 10, 42 };
        var request = IntegrationRequest.New() with
        {
            Name = "Передача", ConnectionId = 1, Method = RequestMethod.Post,
            Path = "/upload?item={{поле.id}}",
            Headers = [new("Upload-Offset", "{{поле.offset}}"), new("Content-Length", "999")],
        };
        var answer = await RequestRunner.ExchangeAsync(Connection(server), request, "",
            VariableContext.Now(null, record: new Dictionary<string, string> { ["id"] = "a&b", ["offset"] = "1024" }),
            properties: null, data);

        Assert.True(answer.Ok, answer.Explanation);
        var seen = Assert.Single(server.Seen);
        Assert.Equal(data, seen.Content);
        Assert.Equal("/upload?item=a%26b", seen.Path);
        Assert.Equal("1024", seen.Header("Upload-Offset"));
        Assert.Equal("6", seen.Header("Content-Length"));
        Assert.Equal("application/octet-stream", seen.Header("Content-Type"));
    }

    [Fact]
    public async Task Literal_properties_are_merged_after_substitutions_without_changing_the_recipient_or_reply()
    {
        using var server = new TinyServer(_ => TinyServer.Reply(200, "{\"id\":\"ok\"}"));
        var request = IntegrationRequest.New() with
        {
            Name = "Описание", ConnectionId = 1, Method = RequestMethod.Put, Path = "/reply",
            Body = """{"to":"{{поле.recipient}}","text":"{{ответ.текст}}"}""",
        };
        var answer = await RequestRunner.ExchangeAsync(Connection(server), request, "",
            VariableContext.Now(null, record: new Dictionary<string, string> { ["recipient"] = "1001" }, reply: "Текст \"с кавычкой\""),
            properties: """{"attachments":[{"name":"{{сегодня}}.txt"}]}""", data: null);

        Assert.True(answer.Ok, answer.Explanation);
        using var body = JsonDocument.Parse(Assert.Single(server.Seen).Body);
        Assert.Equal("1001", body.RootElement.GetProperty("to").GetString());
        Assert.Equal("Текст \"с кавычкой\"", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("{{сегодня}}.txt", body.RootElement.GetProperty("attachments")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_interrupted_create_request_is_not_retried_even_if_the_saved_method_is_put()
    {
        using var server = new TinyServer(_ => TinyServer.Reply(200, "{}"), truncateFirst: true);
        var request = IntegrationRequest.New() with
        { Name = "Создание", ConnectionId = 1, Method = RequestMethod.Put, Path = "/reply", Body = "{}" };

        var answer = await RequestRunner.ExchangeAsync(Connection(server), request, "", VariableContext.Now(null), "{}", null);

        Assert.False(answer.Ok);
        Assert.Equal(1, answer.Attempts);
        Assert.Single(server.Seen);
    }

    [Fact]
    public async Task An_invalid_json_template_does_not_send_any_request()
    {
        using var server = new TinyServer(_ => TinyServer.Reply(200, "{}"));
        var request = IntegrationRequest.New() with
        { Name = "Создание", ConnectionId = 1, Method = RequestMethod.Put, Path = "/reply", Body = "not-json" };

        var answer = await RequestRunner.ExchangeAsync(Connection(server), request, "", VariableContext.Now(null), "{}", null);

        Assert.False(answer.Ok);
        Assert.Empty(server.Seen);
    }
}
