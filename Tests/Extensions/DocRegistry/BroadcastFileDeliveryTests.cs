using System.Text.Json;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

public sealed class BroadcastFileDeliveryTests
{
    [Fact]
    public async Task Metadata_precedes_sequential_blocks_and_preserves_every_binary_byte()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "Чертеж {{сегодня}}.bin");
        var data = Enumerable.Range(0, BroadcastFileDelivery.BlockBytes + 7).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(path, data);
        File.WriteAllBytes(Path.Combine(dir.Path, "second.bin"), [255, 0, 128]);
        var files = BroadcastAttachments.FromFolder(dir.Path);
        var exchange = new Exchange();

        var result = await BroadcastFileDelivery.SendAsync(exchange, "1001", "Текст", files);

        Assert.True(result.Ok, result.Explanation);
        using var json = JsonDocument.Parse(Assert.Single(exchange.Created).Properties);
        var descriptions = json.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(files.Select(f => f.Name), descriptions.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal(files.Select(f => f.Length), descriptions.Select(f => f.GetProperty("length").GetInt64()));
        Assert.All(descriptions, f => Assert.False(f.GetProperty("directory").GetBoolean()));
        Assert.Equal(files.Count, descriptions.Select(f => f.GetProperty("fileUuid").GetString()).Distinct().Count());
        for (var i = 0; i < descriptions.Length; i++)
        {
            var blocks = exchange.Blocks.Where(b => b.Fields["file"] == descriptions[i].GetProperty("fileUuid").GetString()).ToArray();
            Assert.Equal(File.ReadAllBytes(files[i].Path), blocks.SelectMany(b => b.Data).ToArray());
            long offset = 0;
            foreach (var block in blocks)
            {
                Assert.Equal("message-id", block.Fields["message"]);
                Assert.Equal(offset.ToString(), block.Fields["offset"]);
                Assert.InRange(block.Data.Length, 1, BroadcastFileDelivery.BlockBytes);
                offset += block.Data.Length;
            }
        }
        Assert.Equal(3, exchange.Blocks.Count);
    }

    [Fact]
    public async Task A_failed_block_stops_the_message_without_retry_or_the_next_file()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Touch("first.bin"), [1, 2]);
        File.WriteAllBytes(dir.Touch("second.bin"), [3, 4]);
        var exchange = new Exchange { Upload = (_, _) => Task.FromResult(new ReplyExchangeResult(false, "HTTP 403", "")) };

        var result = await BroadcastFileDelivery.SendAsync(exchange, "1001", "Текст", BroadcastAttachments.FromFolder(dir.Path));

        Assert.False(result.Ok);
        Assert.Contains("HTTP 403", result.Explanation, StringComparison.Ordinal);
        Assert.Single(exchange.Created);
        Assert.Single(exchange.Blocks);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"uuid\":\"\"}")]
    public async Task Missing_message_id_never_starts_uploading(string body)
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Touch("first.bin"), [1]);
        var exchange = new Exchange { Answer = body };

        var result = await BroadcastFileDelivery.SendAsync(exchange, "1001", "Текст", BroadcastAttachments.FromFolder(dir.Path));

        Assert.False(result.Ok);
        Assert.Empty(exchange.Blocks);
    }

    [Fact]
    public async Task Cancellation_during_upload_stops_subsequent_blocks()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Touch("file.bin"), new byte[BroadcastFileDelivery.BlockBytes + 1]);
        using var stop = new CancellationTokenSource();
        var exchange = new Exchange { Upload = (_, token) => { stop.Cancel(); return Task.FromCanceled<ReplyExchangeResult>(token); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BroadcastFileDelivery.SendAsync(
            exchange, "1001", "Текст", BroadcastAttachments.FromFolder(dir.Path), stop.Token));
        Assert.Single(exchange.Blocks);
    }

    [Fact]
    public async Task Each_recipient_gets_a_complete_message_with_new_client_file_ids()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Touch("file.bin"), [1, 255]);
        var exchange = new Exchange();
        var replies = new FileReplies(exchange);

        var report = await ParticipantBroadcast.SendAsync(replies,
            [new("1001", "Первый"), new("1002", "Второй")], "<текст>",
            attachments: BroadcastAttachments.FromFolder(dir.Path), uploadRequestId: 7);

        Assert.Equal(2, report.Sent);
        Assert.Empty(report.Failed);
        Assert.Equal(["1001", "1002"], exchange.Created.Select(c => c.Recipient));
        Assert.All(exchange.Created, c => Assert.Equal("&lt;текст&gt;", c.Text));
        Assert.Equal(2, exchange.Blocks.Select(b => b.Fields["file"]).Distinct().Count());
    }

    [Fact]
    public async Task A_zero_length_file_is_rejected_before_creating_any_message()
    {
        using var dir = new TempDir();
        var file = dir.Touch("empty.bin");
        var exchange = new Exchange();
        var report = await ParticipantBroadcast.SendAsync(new FileReplies(exchange), [new("1001", "Первый")],
            "Текст", attachments: BroadcastAttachments.FromFile(file), uploadRequestId: 7);

        Assert.Equal(0, report.Attempted);
        Assert.Contains("Пустые файлы", report.Problem, StringComparison.Ordinal);
        Assert.Empty(exchange.Created);
    }

    private sealed class FileReplies(Exchange exchange) : IActionReplies
    {
        public string? Route(string extensionId, IExtensionAction action, string senderField, out string problem)
        { problem = ""; return "Сценарий"; }
        public IReplyFileExchange? FilesRoute(string extensionId, IExtensionAction action, string senderField,
            int uploadRequestId, out string problem)
        { Assert.Equal(7, uploadRequestId); problem = ""; return exchange; }
        public Task<ReplyResult> SendAsync(string extensionId, IExtensionAction action, string senderField,
            string recipient, string text, CancellationToken ct = default) => throw new InvalidOperationException("Вложение потеряно");
    }

    private sealed class Exchange : IReplyFileExchange
    {
        public string Answer { get; init; } = "{\"uuid\":\"message-id\"}";
        public List<(string Recipient, string Text, string Properties)> Created { get; } = [];
        public List<(IReadOnlyDictionary<string, string> Fields, byte[] Data)> Blocks { get; } = [];
        public Func<byte[], CancellationToken, Task<ReplyExchangeResult>>? Upload { get; init; }
        public Task<ReplyExchangeResult> CreateAsync(string recipient, string text, string properties, CancellationToken ct = default)
        { Created.Add((recipient, text, properties)); return Task.FromResult(new ReplyExchangeResult(true, "", Answer)); }
        public Task<ReplyExchangeResult> UploadAsync(IReadOnlyDictionary<string, string> fields, byte[] data, CancellationToken ct = default)
        {
            Assert.NotEmpty(Created);
            Blocks.Add((fields, data));
            return Upload?.Invoke(data, ct) ?? Task.FromResult(new ReplyExchangeResult(true, "", ""));
        }
    }
}
