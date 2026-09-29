using System.Text;
using System.Text.Json;
using SmartBase.Core.Importers.TableSources;

namespace SmartBase.Integrations;

/// <summary>
/// То, что ходит по потоку событий: кадры подписки и разбор пришедшего в строки таблицы. Ни
/// одного сокета — только текст; соединяется дверь наружу (<see cref="EgressHttp"/>).
///
/// <b>Событие становится строками по тем же правилам, что ответ запроса.</b> Имена колонок — имена
/// полей, вложенные объекты разворачиваются точкой, значения переводятся в текст
/// (<see cref="SourceHelpers.JsonLeaves"/>). Один и тот же набор данных, пришедший ответом и
/// событием, обязан выглядеть в сценарии одинаково — иначе шаг, настроенный на одно, не
/// подошел бы к другому.
///
/// О содержании событий здесь не знают ничего: какие поля что означают, решает человек, когда
/// называет колонки в шагах сценария.
/// </summary>
public static class StreamEvents
{
    private const char Nul = '\0';

    // ── Подписка ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Кадры, которые уходят сразу после соединения. У простого WebSocket их нет: события идут
    /// сами. У STOMP — представиться и подписаться на каждую тему карточки.
    ///
    /// Сердцебиение STOMP выключено: живость соединения проверяет сам WebSocket своими пингами, и
    /// второй механизм поверх первого только добавил бы способов разойтись.
    /// </summary>
    public static IReadOnlyList<string> Greeting(IntegrationStream stream, string host)
    {
        if (stream.Protocol != StreamProtocol.Stomp) return [];

        var frames = new List<string>
        {
            Frame("CONNECT", ("accept-version", "1.2,1.1,1.0"), ("host", host), ("heart-beat", "0,0")),
        };

        var number = 0;

        foreach (var topic in stream.Subscriptions)
            frames.Add(Frame("SUBSCRIBE", ("id", "sub-" + number++), ("destination", topic),
                             ("ack", "auto")));

        return frames;
    }

    /// <summary>Кадр STOMP без тела.</summary>
    public static string Frame(string command, params (string Name, string Value)[] headers)
    {
        var text = new StringBuilder(command).Append('\n');

        foreach (var (name, value) in headers)
            text.Append(name).Append(':').Append(value).Append('\n');

        return text.Append('\n').Append(Nul).ToString();
    }

    // ── Пришедшее ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Тела событий из одного сообщения WebSocket. У простого протокола сообщение и есть
    /// событие; у STOMP событие — тело кадра <c>MESSAGE</c>, а кадров в сообщении бывает
    /// несколько.
    /// </summary>
    /// <param name="trouble">Та сторона сообщила об ошибке (кадр <c>ERROR</c>) — ее слова; пусто —
    /// ошибок не было.</param>
    public static IReadOnlyList<string> Bodies(IntegrationStream stream, string? text, out string trouble)
    {
        trouble = "";

        if (stream.Protocol != StreamProtocol.Stomp)
            return string.IsNullOrWhiteSpace(text) ? [] : [text];

        var bodies = new List<string>();

        foreach (var frame in Frames(text))
        {
            if (frame.Command.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                trouble = frame.Headers.TryGetValue("message", out var said) && said.Length > 0
                    ? said
                    : frame.Body.Trim();
                continue;
            }

            if (frame.Command.Equals("MESSAGE", StringComparison.OrdinalIgnoreCase)
                && frame.Body.Trim().Length > 0)
                bodies.Add(frame.Body);
        }

        return bodies;
    }

    /// <summary>Разобранный кадр STOMP: команда, заголовки, тело.</summary>
    public sealed record StompFrame(string Command, IReadOnlyDictionary<string, string> Headers,
                                    string Body);

    /// <summary>
    /// Кадры STOMP из текста. Пустые строки между кадрами — сердцебиение, они пропускаются.
    /// </summary>
    public static IReadOnlyList<StompFrame> Frames(string? text)
    {
        var frames = new List<StompFrame>();

        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split(Nul))
        {
            var chunk = raw.TrimStart('\n');
            if (chunk.Length == 0) continue;

            var split   = chunk.IndexOf("\n\n", StringComparison.Ordinal);
            var head    = split < 0 ? chunk : chunk[..split];
            var body    = split < 0 ? "" : chunk[(split + 2)..];
            var lines   = head.Split('\n');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                // Повтор заголовка — по правилам STOMP действует первый.
                headers.TryAdd(line[..colon], line[(colon + 1)..]);
            }

            frames.Add(new StompFrame(lines[0].Trim(), headers, body));
        }

        return frames;
    }

    // ── Строки ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Строки таблицы из одного события.
    ///
    /// Записи лежат по пути <paramref name="itemsPath"/>: список дает строку на элемент, объект —
    /// одну строку. <b>Поля самого события дописываются к каждой записи</b> — те, что лежат рядом
    /// с записями, а не внутри них: вид события, его время. Без этого отбор «вид события равно …»
    /// было бы не на чем строить — вид лежит снаружи записи. Поле записи с тем же именем старше:
    /// запись знает о себе лучше, чем конверт.
    ///
    /// Путь пуст — записью считается само событие, и дописывать к нему нечего.
    /// </summary>
    /// <param name="trouble">Почему строк нет: событие не JSON, по пути лежит не запись. Пусто —
    /// разобралось (в том числе когда записей в событии просто нет).</param>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Rows(
        string? body, string? itemsPath, out string trouble)
    {
        trouble = "";

        if (string.IsNullOrWhiteSpace(body)) return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling     = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException failure)
        {
            // Текст события в сообщение не попадает: в нем может оказаться что угодно, а
            // сообщение уходит в журнал.
            trouble = "событие не является JSON: " + failure.Message;
            return [];
        }

        using (document)
        {
            var root  = document.RootElement;
            var node  = root;
            var steps = (itemsPath ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);

            foreach (var step in steps)
            {
                // Пути в этом событии нет — значит, оно другого вида, и записей для нас в нем
                // нет. Это не ошибка: по одному потоку ходят события разной формы.
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out var next))
                    return [];

                node = next;
            }

            var envelope = steps.Length > 0 && root.ValueKind == JsonValueKind.Object
                ? Envelope(root, steps[0])
                : [];

            var rows = new List<IReadOnlyDictionary<string, string>>();

            switch (node.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Object) rows.Add(Row(item, envelope));
                    break;

                case JsonValueKind.Object:
                    rows.Add(Row(node, envelope));
                    break;

                case JsonValueKind.Null or JsonValueKind.Undefined:
                    break;

                default:
                    trouble = "по пути к записям в событии лежит одиночное значение, а не запись";
                    break;
            }

            return rows;
        }
    }

    /// <summary>
    /// Поля конверта: простые значения верхнего уровня события, кроме ветки, в которой лежат
    /// записи. Вложенные объекты и списки конверта в строку не идут — это чужие данные рядом с
    /// записями, а не сведения о них.
    /// </summary>
    private static List<(string Name, string Text)> Envelope(JsonElement root, string itemsBranch)
    {
        var fields = new List<(string, string)>();

        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals(itemsBranch)) continue;

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;

            fields.Add((property.Name, SourceHelpers.FormatJsonValue(property.Value)));
        }

        return fields;
    }

    private static Dictionary<string, string> Row(JsonElement item,
                                                  List<(string Name, string Text)> envelope)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Первое упоминание побеждает: повтор ключа внутри одного объекта JSON допускает, а
        // решать, какое из двух значений верное, программа не берется.
        foreach (var (name, text) in SourceHelpers.JsonLeaves(item)) row.TryAdd(name, text);
        foreach (var (name, text) in envelope)                        row.TryAdd(name, text);

        return row;
    }
}
