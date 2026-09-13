using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Сухой прогон (<see cref="RequestRunner.Preview"/>) — «Показать, что уйдет»
/// (docs/ROADMAP-API.md, 10.9).
///
/// Главное свойство, ради которого он и заведен: показанное собирается ТЕМ ЖЕ кодом, что и
/// настоящая отправка. Проверяется это единственным способом, каким его вообще можно проверить, —
/// сравнением с тем, что дошло до сервера при настоящем запуске.
///
/// И второе: сухой прогон не говорит наружу НИЧЕГО. Ни одного сокета, ни одного обращения к DNS.
///
/// Набор идет в общей очереди шлюза: политика одна на приложение.
/// </summary>
[Collection(EgressCollection.Name)]
public sealed class RequestPreviewTests : IDisposable
{
    public void Dispose() => EgressGate.Close();

    private static void AllowLoopback() =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal,
            Hosts:           ["127.0.0.1"],
            Networks:        ["127.0.0.0/8"],
            Folders:         [],
            MaxRowsPerRun:   1000,
            MaxBytesPerDay:  8 * 1024 * 1024,
            MaxRunsPerHour:  100,
            ValidUntil:      null,
            UserMayAddHosts: false,
            Source:          "тест")));

    private static IntegrationConnection Connection(int port) =>
        IntegrationConnection.New() with
        {
            Id             = 1,
            Name           = "Учетная система",
            Address        = $"http://127.0.0.1:{port}",
            Auth           = ConnectionAuth.Header,
            AuthName       = "X-Token",
            TimeoutSeconds = 30,
            Retries        = 0,
        };

    private static IntegrationRequest Push() =>
        IntegrationRequest.New() with
        {
            Id           = 7,
            Name         = "Отдать итог",
            ConnectionId = 1,
            Method       = RequestMethod.Post,
            Path         = "/result?день={{сегодня}}",
            Headers      = [new IntegrationHeader("X-Department", "KB")],
            Body         = "{\"день\":\"{{сегодня}}\"}",
        };

    private static VariableContext Context() =>
        new(new DateOnly(2026, 9, 11), null, "http://127.0.0.1");

    // ── Что показано ──────────────────────────────────────────────────────────

    /// <summary>
    /// Показанное — это собранный запрос с подставленными переменными, а не карточка. Человек
    /// смотрит на то, что уйдет, и подстановка <c>{{сегодня}}</c> в теле должна быть уже
    /// раскрыта: иначе он смотрит на шаблон и ничего о содержимом не узнает.
    /// </summary>
    [Fact]
    public void The_preview_shows_the_assembled_request()
    {
        AllowLoopback();

        var preview = RequestRunner.Preview(Connection(8080), Push(), "token", Context(), refusal: null, payload: null);

        Assert.True(preview.Ok, preview.Explanation);
        Assert.Equal(RequestMethod.Post, preview.Method);
        Assert.Contains("/result?%D0%B4",     preview.SafeAddress, StringComparison.Ordinal);
        Assert.Contains("2026-09-11",         preview.SafeAddress, StringComparison.Ordinal);
        Assert.Equal("{\"день\":\"2026-09-11\"}", preview.Body);
        Assert.True(preview.Outward);
    }

    /// <summary>
    /// Секрет не показывается. Сухой прогон копируют в переписку с той стороной — тем же
    /// движением, каким пересылают журнал, и токен в нем это утечка, которую не отозвать.
    /// </summary>
    [Fact]
    public void The_secret_is_never_shown()
    {
        AllowLoopback();

        var preview = RequestRunner.Preview(Connection(8080), Push(), "very-secret-token",
                                            Context(), refusal: null, payload: null);

        Assert.DoesNotContain("very-secret-token", preview.SafeAddress, StringComparison.Ordinal);

        var token = preview.Headers.FirstOrDefault(h => h.Name == "X-Token");

        Assert.NotNull(token);
        Assert.Equal(EgressPlan.Mask, token!.Value);
    }

    /// <summary>Свой заголовок запроса виден как есть: секрет тут только один, и он помечен.</summary>
    [Fact]
    public void Plain_headers_are_shown_as_they_are()
    {
        AllowLoopback();

        var preview = RequestRunner.Preview(Connection(8080), Push(), "token", Context(), refusal: null, payload: null);

        Assert.Contains(preview.Headers, h => h.Name == "X-Department" && h.Value == "KB");
    }

    // ── Что уйдет — то и показано ─────────────────────────────────────────────

    /// <summary>
    /// Сравнение с настоящим запуском. Тело, дошедшее до сервера, обязано совпасть с показанным
    /// до знака: разойдись они — и сухому прогону нельзя верить вовсе, а верят ему именно тогда,
    /// когда проверяют, не уходит ли лишнее.
    /// </summary>
    [Fact]
    public async Task What_the_server_receives_is_what_was_shown()
    {
        AllowLoopback();

        var seen = "";
        using var server = new TinyServer(request =>
        {
            seen = request.Body;
            return TinyServer.Reply(200, "[]");
        });

        var connection = Connection(server.Port);
        var request    = Push();

        var preview = RequestRunner.Preview(connection, request, "token", Context(), refusal: null, payload: null);

        var outcome = await RequestRunner.RunAsync(connection, request, "token", Context(),
                                                   refusal: null, payload: null);

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(preview.Body, seen);
    }

    // ── Что мешает ────────────────────────────────────────────────────────────

    /// <summary>
    /// Неподтвержденное направление останавливает сухой прогон так же, как остановит отправку:
    /// показывать «вот что уйдет» там, где не уйдет ничего, значит вводить в заблуждение.
    /// </summary>
    [Fact]
    public void An_unconfirmed_direction_stops_the_preview()
    {
        AllowLoopback();

        var preview = RequestRunner.Preview(
            Connection(8080), Push(), "token", Context(),
            refusal: "исходящее направление: не подтверждено мастер-паролем", payload: null);

        Assert.False(preview.Ok);
        Assert.Contains("не подтверждено", preview.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Адрес вне белого списка виден как отказ — и виден ДО отправки.</summary>
    [Fact]
    public void An_address_outside_the_whitelist_is_refused_in_the_preview()
    {
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, ["api.zavod.local"], ["10.0.0.0/8"], [],
            1000, 1024 * 1024, 100, null, false, "тест")));

        var preview = RequestRunner.Preview(Connection(8080), Push(), "token", Context(), refusal: null, payload: null);

        Assert.False(preview.Ok);
        Assert.Contains("политикой", preview.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Опечатка в подстановке останавливает прогон и называет саму опечатку.</summary>
    [Fact]
    public void An_unknown_substitution_is_named()
    {
        AllowLoopback();

        var preview = RequestRunner.Preview(
            Connection(8080), Push() with { Body = "{{опечатка}}" }, "token", Context(), refusal: null, payload: null);

        Assert.False(preview.Ok);
        Assert.Contains("{{опечатка}}", preview.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Секрет, набранный кириллицей, — отказ СЛОВАМИ, а не исключение. Заголовок из такого
    /// секрета не уйдет по стандарту HTTP, и узнать об этом надо здесь, а не ночью из фонового
    /// сценария: карточка секрета не содержит, и проверить его при сохранении негде.
    /// </summary>
    [Fact]
    public void A_secret_that_is_not_latin_is_refused_in_words()
    {
        AllowLoopback();

        var preview = RequestRunner.Preview(Connection(8080), Push(), "секрет-кириллицей",
                                            Context(), refusal: null, payload: null);

        Assert.False(preview.Ok);
        Assert.Contains("латиницей", preview.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сухой прогон проверяет те же рамки и в том же порядке, что и запуск, — значит, и объем
    /// отправляемого. Иначе экран говорит «уйдет вот это», а нажатие «В коллекцию» отвечает
    /// отказом по потолку: ради ответа на этот вопрос до отправки прогон и заведен.
    /// </summary>
    [Fact]
    public void A_body_that_does_not_fit_the_daily_volume_is_refused_in_the_preview()
    {
        AllowLoopback();

        // Суточное право почти выбрано: на сам запуск хватает, на тело — нет.
        EgressGate.AttachUsage(new Spent(8 * 1024 * 1024 - 10));

        var preview = RequestRunner.Preview(
            Connection(8080), Push() with { Body = new string('x', 500) }, "token", Context(), refusal: null, payload: null);

        Assert.False(preview.Ok);
        Assert.Contains("суточный потолок объема", preview.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Счетчик расхода с заданным числом: расход считает журнал, решает шлюз.</summary>
    private sealed class Spent(long bytes) : IEgressUsage
    {
        public int  RunsSince(DateTimeOffset moment)  => 0;
        public long BytesSince(DateTimeOffset moment, int direction) => bytes;

        public DateTimeOffset? FreesAt(DateTimeOffset now, int direction, TimeSpan window) => null;
    }

    /// <summary>Читающий запрос исходящим не считается — и подтверждения не ждет.</summary>
    [Fact]
    public void A_reading_request_is_not_marked_outward()
    {
        AllowLoopback();

        var preview = RequestRunner.Preview(
            Connection(8080),
            IntegrationRequest.New() with
            {
                Id = 8, Name = "Заказы", ConnectionId = 1, Path = "/orders",
            },
            "token", Context(), refusal: null, payload: null);

        Assert.True(preview.Ok, preview.Explanation);
        Assert.False(preview.Outward);
    }
}
