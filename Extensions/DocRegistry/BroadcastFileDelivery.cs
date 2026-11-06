using System.Globalization;
using System.Text.Json;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Протокол вложений: описание в сообщении, затем последовательные двоичные блоки.
/// Формат API остается в расширении; запросы и сеть предоставляет IActionReplies.
/// </summary>
public static class BroadcastFileDelivery
{
    public const int BlockBytes = 1024 * 1024;
    public const string UploadRequestKey = "message-upload-request";

    public static async Task<ReplyResult> SendAsync(IReplyFileExchange exchange,
        string recipient, string text, IReadOnlyList<BroadcastAttachment> files,
        CancellationToken ct = default)
    {
        BroadcastAttachments.Validate(files);
        // Файлы открываются до создания сообщения: обычная ошибка доступа не должна
        // оставлять на той стороне сообщение, ожидающее недоступное вложение.
        var streams = new List<FileStream>();
        try
        {
            foreach (var file in files)
                streams.Add(File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read));

            var ids = files.Select(_ => Guid.NewGuid().ToString()).ToArray();
            var properties = JsonSerializer.Serialize(new
            {
                files = files.Select((file, i) => new
                {
                    fileUuid = ids[i], length = file.Length, name = file.Name, directory = false,
                }),
            });
            var created = await exchange.CreateAsync(recipient, text, properties, ct);
            if (!created.Ok) return new(false, created.Explanation);
            string? message;
            try
            {
                using var document = JsonDocument.Parse(created.Body);
                message = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("uuid", out var uuid)
                    && uuid.ValueKind == JsonValueKind.String ? uuid.GetString() : null;
            }
            catch (JsonException) { message = null; }
            if (string.IsNullOrWhiteSpace(message))
                return new(false, "API не вернул идентификатор созданного сообщения; "
                    + "загрузка не начата. Проверьте результат перед повторной отправкой.");

            for (var i = 0; i < files.Count; i++)
            {
                long offset = 0;
                while (offset < files[i].Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var data = new byte[(int)Math.Min(BlockBytes, files[i].Length - offset)];
                    await streams[i].ReadExactlyAsync(data, ct);
                    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["message"] = message,
                        ["file"] = ids[i],
                        ["offset"] = offset.ToString(CultureInfo.InvariantCulture),
                    };
                    var sent = await exchange.UploadAsync(fields, data, ct);
                    if (!sent.Ok) return new(false, $"Файл «{files[i].Name}», смещение {offset}: "
                        + sent.Explanation + ". Загрузка не завершена; проверьте результат перед повтором.");
                    offset += data.Length;
                }
            }
            return new(true, "");
        }
        finally
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }
}
