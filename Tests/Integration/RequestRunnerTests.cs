using System.Text.Json;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Запуск запросов на локальном сервере: подстановки, страницы, конец данных, ошибки и сохранность результата.</summary>
[Collection(EgressCollection.Name)]
public sealed class RequestRunnerTests
{

    private static IntegrationConnection Connection(TinyServer server) =>
        IntegrationConnection.New() with
        {
            Id             = 1,
            Name           = "Учетная система",
            Address        = $"http://127.0.0.1:{server.Port}",
            TimeoutSeconds = 10,
            Retries        = 0,
        };

    private static IntegrationRequest Request(
        string path = "/orders",
        RequestPaging paging = RequestPaging.None,
        string pagingField = "",
        string itemsPath = "") =>
        IntegrationRequest.New() with
        {
            Name         = "Заказы",
            ConnectionId = 1,
            Path         = path,
            Paging       = paging,
            PagingField  = pagingField,
            ItemsPath    = itemsPath,
        };

    private static VariableContext Context(DateTimeOffset? lastRun = null) =>
        new(new DateOnly(2026, 9, 9), lastRun);

    /// <summary>
    /// Обычный запуск. Замок исходящего направления здесь снят намеренно: этот набор проверяет
    /// страницы, разбор ответа и потолки, а не согласование — у согласования свой набор.
    /// </summary>
    private static Task<RunOutcome> Run(
        TinyServer server, IntegrationRequest request, VariableContext? context = null) =>
        RequestRunner.RunAsync(Connection(server), request, secret: "", context ?? Context(),
                               payload: null);

    /// <summary>Записи из итогового массива — по названию.</summary>
    private static List<string> Names(RunOutcome outcome) =>
        [.. JsonDocument.Parse(outcome.Json).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString() ?? "")];

    private static string Items(params string[] names) =>
        "[" + string.Join(",", names.Select(n => $"{{\"name\":\"{n}\"}}")) + "]";

    // ── Обычный запуск ────────────────────────────────────────────────────────

    [Fact]
    public async Task Single_answer_becomes_the_collected_array()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А", "Б")));

        var outcome = await Run(server, Request());

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(2, outcome.Rows);
        Assert.Equal(1, outcome.Pages);
        Assert.Equal(["А", "Б"], Names(outcome));
        Assert.False(outcome.Capped);
    }

    [Fact]
    public async Task Items_are_taken_by_the_path()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200,
            "{\"ok\":true,\"data\":{\"items\":" + Items("А") + "}}"));

        var outcome = await Run(server, Request(itemsPath: "data.items"));

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(["А"], Names(outcome));
    }

    /// <summary>Ответ одиночным объектом — это одна запись, а не ошибка.</summary>
    [Fact]
    public async Task Single_object_is_one_record()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, """{"name":"А"}"""));

        var outcome = await Run(server, Request());

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(1, outcome.Rows);
    }

    // ── Подстановки ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Last_run_reaches_the_other_program()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var moment = new DateTimeOffset(2026, 9, 9, 8, 30, 0, TimeSpan.Zero);
        await Run(server, Request("/orders?since={{последний_запуск}}"), Context(moment));

        Assert.Equal("/orders?since=2026-09-09T08:30:00Z", Assert.Single(server.Seen).Path);
    }

    /// <summary>
    /// Неизвестная подстановка не дает запросу уйти: буквальная <c>{{опечатка}}</c> в адресе
    /// дала бы пустой ответ по причине, которую потом ищут неделю.
    /// </summary>
    [Fact]
    public async Task Unknown_substitution_stops_the_run_before_the_socket()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request("/orders?who={{опечатка}}"));

        Assert.False(outcome.Ok);
        Assert.Contains("{{опечатка}}", outcome.Explanation, StringComparison.Ordinal);
        Assert.Empty(server.Seen);
    }

    // ── Страницы ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Первая порция идет БЕЗ параметра страницы: так сторонние программы описывают свой первый
    /// вызов, а лишний «?page=1» некоторые из них считают ошибкой.
    /// </summary>
    [Fact]
    public async Task Pages_are_walked_until_the_empty_one()
    {

        using var server = new TinyServer(request => TinyServer.Reply(200, request.Path switch
        {
            "/orders"        => Items("А", "Б"),
            "/orders?page=2" => Items("В"),
            _                => "[]",
        }));

        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(["А", "Б", "В"], Names(outcome));
        Assert.Equal(3, outcome.Pages);
        Assert.Equal(["/orders", "/orders?page=2", "/orders?page=3"], server.Seen.Select(s => s.Path));
    }

    [Fact]
    public async Task Offset_grows_by_the_number_of_records_taken()
    {

        using var server = new TinyServer(request => TinyServer.Reply(200, request.Path switch
        {
            "/orders"          => Items("А", "Б"),
            "/orders?offset=2" => Items("В"),
            _                  => "[]",
        }));

        var outcome = await Run(server, Request(paging: RequestPaging.Offset, pagingField: "offset"));

        Assert.Equal(["А", "Б", "В"], Names(outcome));
        Assert.Equal(["/orders", "/orders?offset=2", "/orders?offset=3"], server.Seen.Select(s => s.Path));
    }

    /// <summary>Метка следующей порции кончилась — список кончился, лишнего запроса нет.</summary>
    [Fact]
    public async Task Cursor_stops_when_the_mark_runs_out()
    {

        using var server = new TinyServer(request => TinyServer.Reply(200, request.Path switch
        {
            "/orders" => "{\"items\":" + Items("А") + ",\"next\":\"c2\"}",
            _         => "{\"items\":" + Items("Б") + ",\"next\":null}",
        }));

        var outcome = await Run(server,
            Request(paging: RequestPaging.Cursor, pagingField: "next", itemsPath: "items"));

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(["А", "Б"], Names(outcome));
        Assert.Equal(["/orders", "/orders?next=c2"], server.Seen.Select(s => s.Path));
    }

    /// <summary>
    /// Сторонняя программа, которая на любой номер страницы отвечает одним и тем же непустым
    /// списком, не должна крутить обмен вечно.
    /// </summary>
    [Fact]
    public async Task Endless_pagination_stops_at_the_page_limit()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(RequestRunner.MaxPages, outcome.Pages);
        Assert.Contains("порций", outcome.Explanation, StringComparison.Ordinal);
    }

    // ── Что считается неудачей ────────────────────────────────────────────────

    [Fact]
    public async Task Error_code_is_a_failure_with_the_code_in_the_reason()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(500, "beda", "text/plain"));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Contains("500", outcome.Explanation, StringComparison.Ordinal);
        Assert.Equal("[]", outcome.Json);
    }

    [Fact]
    public async Task Answer_that_is_not_json_is_a_failure()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, "не json", "text/plain"));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Contains("не является JSON", outcome.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_items_path_says_which_part_is_missing()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, """{"data":{"items":[]}}"""));

        var outcome = await Run(server, Request(itemsPath: "data.список"));

        Assert.False(outcome.Ok);
        Assert.Contains("список", outcome.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Половина выгрузки, уложенная в коллекцию, хуже, чем ничего: недостачу потом не заметить.
    /// </summary>
    [Fact]
    public async Task Failure_in_the_middle_returns_no_records_at_all()
    {

        using var server = new TinyServer(request => TinyServer.Reply(
            request.Path == "/orders" ? 200 : 500,
            request.Path == "/orders" ? Items("А") : "beda"));

        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));

        Assert.False(outcome.Ok);
        Assert.Equal("[]", outcome.Json);
        Assert.Equal(1, outcome.Rows);   // успели собрать одну — это видно в отчете
    }

    // ── Тела для журнала ──────────────────────────────────────────────────────

    /// <summary>
    /// Тела запроса и ответа возвращаются итогом и уходят в журнал: без них панель подробностей
    /// отвечает «не получилось» и не отвечает «почему».
    /// </summary>
    [Fact]
    public async Task Bodies_come_back_with_the_outcome()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request() with
        {
            Method = RequestMethod.Post,
            Body   = """{"since":"2026-09-09"}""",
        });

        Assert.Contains("since", outcome.SentBody, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"А\"", outcome.GotBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Тела берутся от ПЕРВОЙ порции: неудачу разбирают по ней, а показывать последнюю из сорока
    /// значило бы отвечать не на тот вопрос — человек ищет, что ушло и что пришло в ответ, а не
    /// что было в конце выгрузки.
    /// </summary>
    [Fact]
    public async Task Bodies_are_taken_from_the_first_page()
    {

        var page = 0;

        using var server = new TinyServer(_ =>
            TinyServer.Reply(200, ++page == 1 ? Items("первая") : "[]"));

        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Contains("первая", outcome.GotBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Секрет вырезается из тела тем же знаком, что и из заголовков. В теле ему взяться неоткуда
    /// — он живет в заголовке или в адресе, — но «неоткуда» здесь значит «пока никто не вписал
    /// его руками», и полагаться на это нельзя: журнал выгружают и пересылают.
    /// </summary>
    [Fact]
    public async Task A_secret_typed_into_the_body_does_not_reach_the_journal()
    {

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await RequestRunner.RunAsync(
            Connection(server),
            Request() with { Method = RequestMethod.Post, Body = """{"token":"очень-секретно"}""" },
            secret: "очень-секретно", context: Context(), payload: null);

        Assert.DoesNotContain("очень-секретно", outcome.SentBody, StringComparison.Ordinal);
        Assert.Contains("***", outcome.SentBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_during_an_http_body_remains_cancellation_for_the_caller()
    {
        using var server = new TinyServer(_ => null, stallAfterHeaders: true);
        using var cancel = new CancellationTokenSource();
        var task = RequestRunner.RunAsync(Connection(server), Request(), "", Context(), payload: null, ct: cancel.Token);
        using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.Seen.Count == 0) await Task.Delay(10, waiting.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Collected_pages_above_the_technical_message_limit_return_no_partial_json()
    {
        var pageBody = "[{\"text\":\"" + new string('x', 6 * 1024 * 1024) + "\"}]";
        using var server = new TinyServer(_ => TinyServer.Reply(200, pageBody));
        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));
        Assert.False(outcome.Ok);
        Assert.Contains("32 МБ", outcome.Explanation, StringComparison.Ordinal);
        Assert.Equal("[]", outcome.Json);
        Assert.Equal(6, outcome.Pages);
        Assert.Equal(6, server.Seen.Count);
    }
}
