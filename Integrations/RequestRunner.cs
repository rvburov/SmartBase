using System.Text;
using System.Text.Json;

namespace SmartBase.Integrations;

/// <summary>Результат запроса и счетчики передачи.</summary>
public sealed record RunOutcome(
    bool     Ok,
    string   Explanation,
    string   Json,
    int      Rows,
    int      Pages,
    long     Bytes,
    TimeSpan Elapsed,
    string   SafeAddress,
    int      StatusCode,
    bool     Capped,
    bool     Sent = false,
    string   SentBody = "",
    string   GotBody  = "")
{
    /// <summary>Неудачный запуск с уже полученными счетчиками.</summary>
    internal static RunOutcome Failed(
        string explanation, EgressResponse answer, int pages, int rows, long bytes, bool sent,
        string sentBody = "", string gotBody = "") =>
        new(false, explanation, "[]", rows, pages, bytes, answer.Elapsed,
            answer.SafeAddress, answer.StatusCode, false, sent, sentBody, gotBody);
}

public sealed record RunPreview(
    bool                             Ok,
    string                           Explanation,
    RequestMethod                    Method,
    string                           SafeAddress,
    IReadOnlyList<IntegrationHeader> Headers,
    string                           Body,
    bool                             Outward)
{
    /// <summary>Отказ до сборки вызова: показывать нечего, причина одна.</summary>
    public static RunPreview Blocked(string explanation, bool outward) =>
        new(false, explanation, RequestMethod.Get, "", [], "", outward);
}

public static class RequestRunner
{
    /// <summary>Техническая защита от бесконечной разбивки сторонней программы на страницы.</summary>
    public const int MaxPages = 200;

    /// <summary>Собрать первую порцию запроса, не открывая сетевое соединение.</summary>
    public static RunPreview Preview(
        IntegrationConnection connection,
        IntegrationRequest    request,
        string                secret,
        VariableContext       context,
        OutgoingPayload?      payload)
    {
        var page = Page(request, context, payload, cursor: null, number: 1, offset: 0, out var unknown);

        if (unknown.Count > 0)
            return RunPreview.Blocked(Unknown(unknown), request.Outward);

        var plan = EgressPlan.Build(connection, page, secret, out var error);
        return plan is null
            ? RunPreview.Blocked(error, request.Outward)
            : new RunPreview(true, "", plan.Method, plan.SafeAddress, plan.SafeHeaders,
                             plan.Body, request.Outward);
    }

    /// <summary>Запустить запрос и собрать страницы ответа до конца списка.</summary>
    public static async Task<RunOutcome> RunAsync(
        IntegrationConnection connection,
        IntegrationRequest    request,
        string                secret,
        VariableContext       context,
        OutgoingPayload?      payload,
        CancellationToken     ct = default)
    {
        var items   = new JsonRows();
        var bytes   = 0L;
        var elapsed = TimeSpan.Zero;
        var pages   = 0;
        var last    = "";
        var status  = 0;
        var sent    = false;
        var receivedBytes = 0L;
        var sentBody = "";
        var gotBody  = "";
        string? cursor = null;
        var number = 1;
        var offset = 0;

        while (pages < MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            var page = Page(request, context, payload, cursor, number, offset, out var unknown);

            if (unknown.Count > 0)
                return new RunOutcome(false, Unknown(unknown), "[]", items.Count, pages,
                                      bytes, elapsed, last, status, false, sent, sentBody, gotBody);

            var answer = await EgressHttp.SendAsync(connection, page, secret, ct);
            ct.ThrowIfCancellationRequested();

            // Рабочий журнал показывает первую порцию, в том числе при неудаче.
            if (pages == 0)
            {
                sentBody = Journalled(page.Body, secret);
                gotBody  = Journalled(answer.Body, secret);
            }

            pages++;
            bytes  += Encoding.UTF8.GetByteCount(page.Body) + answer.Bytes;
            elapsed = answer.Elapsed > elapsed ? answer.Elapsed : elapsed;
            last    = answer.SafeAddress;
            status  = answer.StatusCode;
            sent   |= answer.Sent;
            receivedBytes += answer.Bytes;

            if (receivedBytes > EgressHttp.MaxResponseBytes)
                return RunOutcome.Failed(
                    "суммарный ответ запроса больше технического ограничения 32 МБ; "
                    + "сузьте отбор или разбейте запрос на отдельные запуски",
                    answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            if (!answer.Sent)
                return RunOutcome.Failed(answer.Explanation, answer, pages, items.Count,
                                         bytes, sent, sentBody, gotBody);

            if (!answer.Ok)
                return RunOutcome.Failed(
                    $"сторонняя программа ответила {answer.StatusCode}"
                    + (answer.Explanation.Length > 0 ? "; " + answer.Explanation : ""),
                    answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            if (answer.Truncated)
                return RunOutcome.Failed(
                    "ответ длиннее технического ограничения и обрезан — записи из него взять нельзя, "
                    + "разбейте выгрузку на страницы",
                    answer, pages, items.Count, bytes, sent, sentBody, gotBody);

            if (!TryTake(answer.Body, request, items, out cursor, out var taken, out var error))
                return RunOutcome.Failed(error, answer, pages, items.Count, bytes,
                                         sent, sentBody, gotBody);

            var done = taken == 0
                    || request.Paging == RequestPaging.None
                    || (request.Paging == RequestPaging.Cursor && string.IsNullOrEmpty(cursor));

            if (done)
                return new RunOutcome(true, "", items.Json,
                                      items.Count, pages, bytes, elapsed, last, status,
                                      false, sent, sentBody, gotBody);

            number++;
            offset += taken;
        }

        return new RunOutcome(true,
            $"остановлено после {MaxPages} порций: сторонняя программа не сообщает о конце списка",
            items.Json,
            items.Count, pages, bytes, elapsed, last, status, true, sent, sentBody, gotBody);
    }

    private static string Unknown(IReadOnlyList<string> unknown) =>
        "неизвестные подстановки: " + string.Join(", ", unknown.Select(u => "{{" + u + "}}"))
        + RequestVariables.ReplyOnlyNote(unknown);

    // Много коротких записей не создают отдельный объект строки на каждый элемент ответа.
    private sealed class JsonRows
    {
        private readonly StringBuilder _json = new();
        public int Count { get; private set; }
        public string Json => "[" + _json + "]";

        public void Add(string value)
        {
            if (Count > 0) _json.Append(',');
            _json.Append(value);
            Count++;
        }
    }

    /// <summary>
    /// Потолок тела, попадающего в журнал запусков. Тело нужно, чтобы разобрать неудачу, а не
    /// чтобы хранить выгрузку: ответ бывает и в тридцать мегабайт, а строк в журнале двадцать
    /// тысяч. Показанного куска хватает на то, ради чего тело и смотрят, — увидеть форму ответа
    /// и текст ошибки сторонней программы.
    /// </summary>
    public const int JournalBodyLimit = 8 * 1024;

    /// <summary>
    /// Тело в том виде, в каком его можно положить в журнал: без секрета и не длиннее потолка.
    ///
    /// Секрет вырезается ТЕМ ЖЕ знаком, что и в заголовках (<c>EgressPlan.Mask</c>): человек,
    /// сверяющий журнал с сухим прогоном, не должен гадать, одно это и то же или разное. В теле
    /// секрету взяться неоткуда — он живет в заголовке или в адресе, — но «неоткуда» здесь
    /// значит «пока никто не вписал его руками», и полагаться на это нельзя.
    /// </summary>
    private static string Journalled(string? body, string secret)
    {
        var text = body ?? "";

        if (secret.Length > 0) text = text.Replace(secret, EgressPlan.Mask, StringComparison.Ordinal);

        return text.Length <= JournalBodyLimit
            ? text
            : text[..JournalBodyLimit] + $"\n… показано {JournalBodyLimit:N0} знаков из {text.Length:N0}";
    }

    // ── Одна порция ───────────────────────────────────────────────────────────

    /// <summary>
    /// Запрос за одну порцию: подстановки применены, параметр разбивки дописан к пути. Первая
    /// порция идет БЕЗ параметра разбивки — так сторонние программы и описывают свой первый вызов,
    /// а лишний <c>?page=1</c> некоторые из них считают ошибкой.
    /// </summary>
    private static IntegrationRequest Page(
        IntegrationRequest request,
        VariableContext    context,
        OutgoingPayload?   payload,
        string?            cursor,
        int                number,
        int                offset,
        out IReadOnlyList<string> unknown)
    {
        var path = RequestVariables.Apply(request.Path, context, out var pathUnknown,
                                          VariablePlace.Path);

        string                body;
        IReadOnlyList<string> bodyUnknown;

        if (payload is not null)
        {
            // Тело из коллекции подстановок НЕ проходит: это данные, а не заготовка. Значение
            // поля, в котором человек написал «{{сегодня}}», уехало бы сегодняшней датой вместо
            // самого текста, и заметили бы это на той стороне, а не здесь.
            body        = payload.Body;
            bodyUnknown = [];
        }
        else
        {
            body = RequestVariables.Apply(request.Body, context, out bodyUnknown,
                                          VariablePlace.Body);
        }

        unknown = [.. pathUnknown.Concat(bodyUnknown).Distinct()];

        var mark = request.Paging switch
        {
            RequestPaging.Page   when number > 1 => Parameter(request.PagingField, number.ToString()),
            RequestPaging.Offset when offset > 0 => Parameter(request.PagingField, offset.ToString()),
            RequestPaging.Cursor when !string.IsNullOrEmpty(cursor) =>
                Parameter(request.PagingField, cursor!),
            _ => "",
        };

        if (mark.Length > 0) path += (path.Contains('?') ? "&" : "?") + mark;

        return request with { Path = path, Body = body };
    }

    private static string Parameter(string name, string value) =>
        Uri.EscapeDataString(name.Trim()) + "=" + Uri.EscapeDataString(value);

    /// <summary>
    /// Забрать записи из ответа и метку следующей порции. Записи складываются сырым текстом:
    /// разбирать их значения будет источник импорта, и делать это дважды незачем.
    /// </summary>
    private static bool TryTake(
        string             body,
        IntegrationRequest request,
        JsonRows           items,
        out string?        cursor,
        out int            taken,
        out string         error)
    {
        cursor = null;
        taken  = 0;
        error  = "";

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
            // Тело ответа в сообщение не попадает: в нем может оказаться что угодно, вплоть до
            // секрета, а сообщение уходит в журнал.
            error = "ответ не является JSON: " + failure.Message;
            return false;
        }

        using (document)
        {
            var node = document.RootElement;

            foreach (var step in (request.ItemsPath ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out var next))
                {
                    error = $"в ответе нет пути «{request.ItemsPath}» — не найдена часть «{step}»";
                    return false;
                }

                node = next;
            }

            switch (node.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                    {
                        items.Add(item.GetRawText());
                        taken++;
                    }
                    break;

                // Одиночный объект — обычный ответ справочного вызова: это одна запись.
                case JsonValueKind.Object:
                    items.Add(node.GetRawText());
                    taken++;
                    break;

                default:
                    error = "по указанному пути в ответе лежит не список записей, а одиночное значение";
                    return false;
            }

            if (request.Paging == RequestPaging.Cursor)
                cursor = Cursor(document.RootElement, request.PagingField);
        }

        return true;
    }

    /// <summary>
    /// Метка следующей порции из корня ответа: путь по точкам, как и у массива записей. Метки
    /// нет или она пустая — список кончился.
    /// </summary>
    private static string? Cursor(JsonElement root, string field)
    {
        var node = root;

        foreach (var step in (field ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out var next))
                return null;

            node = next;
        }

        var value = node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number => node.GetRawText(),
            _                    => null,
        };

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
