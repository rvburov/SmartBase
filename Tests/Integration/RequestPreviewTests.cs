using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>Предварительный просмотр совпадает с отправленным запросом и не открывает сетевое соединение.</summary>
[Collection(EgressCollection.Name)]
public sealed class RequestPreviewTests
{

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

        var preview = RequestRunner.Preview(Connection(8080), Push(), "token", Context(), payload: null);

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

        var preview = RequestRunner.Preview(Connection(8080), Push(), "very-secret-token",
                                            Context(), payload: null);

        Assert.DoesNotContain("very-secret-token", preview.SafeAddress, StringComparison.Ordinal);

        var token = preview.Headers.FirstOrDefault(h => h.Name == "X-Token");

        Assert.NotNull(token);
        Assert.Equal(EgressPlan.Mask, token!.Value);
    }

    /// <summary>Свой заголовок запроса виден как есть: секрет тут только один, и он помечен.</summary>
    [Fact]
    public void Plain_headers_are_shown_as_they_are()
    {

        var preview = RequestRunner.Preview(Connection(8080), Push(), "token", Context(), payload: null);

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

        var seen = "";
        using var server = new TinyServer(request =>
        {
            seen = request.Body;
            return TinyServer.Reply(200, "[]");
        });

        var connection = Connection(server.Port);
        var request    = Push();

        var preview = RequestRunner.Preview(connection, request, "token", Context(), payload: null);

        var outcome = await RequestRunner.RunAsync(connection, request, "token", Context(),
                                                   payload: null);

        Assert.True(outcome.Ok, outcome.Explanation);
        Assert.Equal(preview.Body, seen);
    }

    /// <summary>Опечатка в подстановке останавливает прогон и называет саму опечатку.</summary>
    [Fact]
    public void An_unknown_substitution_is_named()
    {

        var preview = RequestRunner.Preview(
            Connection(8080), Push() with { Body = "{{опечатка}}" }, "token", Context(), payload: null);

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

        var preview = RequestRunner.Preview(Connection(8080), Push(), "секрет-кириллицей",
                                            Context(), payload: null);

        Assert.False(preview.Ok);
        Assert.Contains("латиницей", preview.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Читающий запрос исходящим не считается — и подтверждения не ждет.</summary>
    [Fact]
    public void A_reading_request_is_not_marked_outward()
    {

        var preview = RequestRunner.Preview(
            Connection(8080),
            IntegrationRequest.New() with
            {
                Id = 8, Name = "Заказы", ConnectionId = 1, Path = "/orders",
            },
            "token", Context(), payload: null);

        Assert.True(preview.Ok, preview.Explanation);
        Assert.False(preview.Outward);
    }
}
