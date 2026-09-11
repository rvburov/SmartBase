using System.Text.Json;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Запуск запроса (<see cref="RequestRunner"/>) на настоящем сокете: подстановки, проход по
/// страницам и потолок строк.
///
/// Проверяются решения, которые легко потерять правкой: первая порция идет БЕЗ параметра
/// разбивки, сбор останавливается на конце данных и на потолке политики, а неизвестная
/// подстановка не дает запросу уйти вовсе.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class RequestRunnerTests : IDisposable
{
    public void Dispose() => EgressGate.Close();

    private static void AllowLoopback(int maxRows = 1000) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["127.0.0.1"],
            Networks:        ["127.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   maxRows,
            MaxBytesPerDay:  1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

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
                               refusal: null);

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
        AllowLoopback();
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
        AllowLoopback();
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
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, """{"name":"А"}"""));

        var outcome = await Run(server, Request());

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(1, outcome.Rows);
    }

    // ── Подстановки ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Last_run_reaches_the_other_program()
    {
        AllowLoopback();
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
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request("/orders?who={{опечатка}}"));

        Assert.False(outcome.Ok);
        Assert.Contains("{{опечатка}}", outcome.Explanation, StringComparison.Ordinal);
        Assert.Empty(server.Seen);
    }

    // ── Страницы ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Первая порция идет БЕЗ параметра страницы: так чужие программы описывают свой первый
    /// вызов, а лишний «?page=1» некоторые из них считают ошибкой.
    /// </summary>
    [Fact]
    public async Task Pages_are_walked_until_the_empty_one()
    {
        AllowLoopback();

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
        AllowLoopback();

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
        AllowLoopback();

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
    /// Чужая программа, которая на любой номер страницы отвечает одним и тем же непустым
    /// списком, не должна крутить обмен вечно.
    /// </summary>
    [Fact]
    public async Task Endless_pagination_stops_at_the_page_limit()
    {
        AllowLoopback(maxRows: 100_000);
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(RequestRunner.MaxPages, outcome.Pages);
        Assert.Contains("порций", outcome.Explanation, StringComparison.Ordinal);
    }

    // ── Потолок строк ─────────────────────────────────────────────────────────

    /// <summary>
    /// Потолок берется из политики организации, а не из настроек человека: настройка, которую
    /// можно поднять, — это не потолок.
    /// </summary>
    [Fact]
    public async Task Row_ceiling_of_the_policy_stops_the_collection()
    {
        AllowLoopback(maxRows: 3);
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А", "Б", "В", "Г", "Д")));

        var outcome = await Run(server, Request());

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(3, outcome.Rows);
        Assert.Equal(["А", "Б", "В"], Names(outcome));
    }

    /// <summary>Потолок в ноль — обмен запрещен, и до сокета дело не доходит.</summary>
    [Fact]
    public async Task Zero_ceiling_refuses_before_the_socket()
    {
        AllowLoopback(maxRows: 0);
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Contains("потолок строк", outcome.Explanation, StringComparison.Ordinal);
        Assert.Empty(server.Seen);
    }

    // ── Что считается неудачей ────────────────────────────────────────────────

    [Fact]
    public async Task Error_code_is_a_failure_with_the_code_in_the_reason()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(500, "beda", "text/plain"));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Contains("500", outcome.Explanation, StringComparison.Ordinal);
        Assert.Equal("[]", outcome.Json);
    }

    [Fact]
    public async Task Answer_that_is_not_json_is_a_failure()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, "не json", "text/plain"));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Contains("не является JSON", outcome.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_items_path_says_which_part_is_missing()
    {
        AllowLoopback();
        using var server = new TinyServer(_ => TinyServer.Reply(200, """{"data":{"items":[]}}"""));

        var outcome = await Run(server, Request(itemsPath: "data.список"));

        Assert.False(outcome.Ok);
        Assert.Contains("список", outcome.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Без политики запуск не начинается — состояние по умолчанию.</summary>
    [Fact]
    public async Task Without_a_policy_nothing_runs()
    {
        EgressGate.Close();
        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Половина выгрузки, уложенная в коллекцию, хуже, чем ничего: недостачу потом не заметить.
    /// </summary>
    [Fact]
    public async Task Failure_in_the_middle_returns_no_records_at_all()
    {
        AllowLoopback();

        using var server = new TinyServer(request => TinyServer.Reply(
            request.Path == "/orders" ? 200 : 500,
            request.Path == "/orders" ? Items("А") : "beda"));

        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));

        Assert.False(outcome.Ok);
        Assert.Equal("[]", outcome.Json);
        Assert.Equal(1, outcome.Rows);   // успели собрать одну — это видно в отчете
    }

    // ── Накопительные потолки ─────────────────────────────────────────────────

    /// <summary>Счетчик расхода, отдающий заданные числа: расход считает журнал, решает шлюз.</summary>
    private sealed class Spent(int runs, long bytes) : IEgressUsage
    {
        public int  RunsSince(DateTimeOffset moment)  => runs;
        public long BytesSince(DateTimeOffset moment, int direction) => bytes;
    }

    /// <summary>
    /// Потолок спрашивается ДО первого сокета: потолок, о котором узнают задним числом, ничего
    /// не ограничивает. Сервер при этом не должен увидеть ни одного обращения.
    /// </summary>
    [Fact]
    public async Task Spent_hourly_ceiling_stops_the_run_before_the_socket()
    {
        AllowLoopback();
        EgressGate.AttachUsage(new Spent(runs: 100, bytes: 0));

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Contains("запусков в час", outcome.Explanation, StringComparison.Ordinal);
        Assert.Empty(server.Seen);
    }

    [Fact]
    public async Task Spent_daily_budget_stops_the_run_before_the_socket()
    {
        AllowLoopback();
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 1024 * 1024));

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А")));

        var outcome = await Run(server, Request());

        Assert.False(outcome.Ok);
        Assert.Contains("суточный потолок объема этого направления", outcome.Explanation,
                        StringComparison.Ordinal);
        Assert.Empty(server.Seen);
    }

    /// <summary>
    /// Спросить квоту один раз перед запуском мало: выгрузка на много порций перешагнула бы
    /// суточный потолок, ни разу его не спросив. Собранное при этом отбрасывается целиком —
    /// как при любой другой неудаче посреди выгрузки.
    /// </summary>
    [Fact]
    public async Task Paging_stops_when_the_daily_budget_runs_out()
    {
        AllowLoopback();

        // Суточное право почти выбрано: на запуск хватает, на первую же порцию — нет.
        EgressGate.AttachUsage(new Spent(runs: 0, bytes: 1024 * 1024 - 10));

        using var server = new TinyServer(_ => TinyServer.Reply(200, Items("А", "Б", "В")));

        var outcome = await Run(server, Request(paging: RequestPaging.Page, pagingField: "page"));

        Assert.False(outcome.Ok);
        Assert.Contains("суточный потолок объема этого направления", outcome.Explanation,
                        StringComparison.Ordinal);
        Assert.Equal("[]", outcome.Json);
        Assert.Single(server.Seen);   // вторая порция уже не ушла
    }
}
