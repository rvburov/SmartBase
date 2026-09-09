using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Подстановки в пути и теле запроса (<see cref="RequestVariables"/>).
///
/// Главная проверка — незнакомая подстановка не превращается в текст. Оставь
/// <c>{{опечатка}}</c> в адресе как есть, и она уедет в чужую программу буквально, а пустой
/// ответ потом будут искать неделю.
///
/// Второе по важности — первый запуск. Пустая строка вместо даты не годится: половина чужих
/// программ отвечает на <c>since=</c> отказом, и первый же обмен встанет.
/// </summary>
public sealed class RequestVariablesTests
{
    private static readonly DateOnly Day = new(2026, 9, 9);

    private static VariableContext Context(DateTimeOffset? lastRun = null, string address = "") =>
        new(Day, lastRun, address);

    private static string Apply(string text, VariableContext context)
    {
        var result = RequestVariables.Apply(text, context, out var unknown);
        Assert.Empty(unknown);
        return result;
    }

    // ── Известные подстановки ─────────────────────────────────────────────────

    [Fact]
    public void Today_and_yesterday_are_plain_dates()
    {
        Assert.Equal("/orders?from=2026-09-09", Apply("/orders?from={{сегодня}}", Context()));
        Assert.Equal("/orders?from=2026-09-08", Apply("/orders?from={{вчера}}", Context()));
    }

    [Fact]
    public void Last_run_is_a_moment_in_utc()
    {
        var moment = new DateTimeOffset(2026, 9, 9, 11, 30, 0, TimeSpan.FromHours(3));

        Assert.Equal("/orders?since=2026-09-09T08:30:00Z",
            Apply("/orders?since={{последний_запуск}}", Context(moment)));
    }

    /// <summary>Первый запуск — начало отсчета, то есть «отдай все».</summary>
    [Fact]
    public void First_run_asks_for_everything()
    {
        Assert.Equal("/orders?since=1970-01-01T00:00:00Z",
            Apply("/orders?since={{последний_запуск}}", Context(lastRun: null)));
    }

    [Fact]
    public void Connection_address_is_available_for_the_body()
    {
        Assert.Equal("""{"callback":"https://srv.zavod.local"}""",
            Apply("""{"callback":"{{соединение.адрес}}"}""", Context(address: "https://srv.zavod.local")));
    }

    /// <summary>Имя пишут руками — придираться к регистру и пробелам незачем.</summary>
    [Theory]
    [InlineData("{{сегодня}}")]
    [InlineData("{{ сегодня }}")]
    [InlineData("{{СЕГОДНЯ}}")]
    public void Name_is_read_loosely(string text)
    {
        Assert.Equal("2026-09-09", Apply(text, Context()));
    }

    [Fact]
    public void Several_substitutions_in_one_string()
    {
        Assert.Equal("/orders?from=2026-09-08&to=2026-09-09",
            Apply("/orders?from={{вчера}}&to={{сегодня}}", Context()));
    }

    [Fact]
    public void Text_without_substitutions_is_untouched()
    {
        Assert.Equal("/orders?since=1", Apply("/orders?since=1", Context()));
        Assert.Equal("", Apply("", Context()));
    }

    // ── Незнакомое ────────────────────────────────────────────────────────────

    /// <summary>
    /// Незнакомая подстановка перечисляется наружу и остается в тексте нетронутой — так человек
    /// увидит в отчете именно то, что написал.
    /// </summary>
    [Fact]
    public void Unknown_substitution_is_reported_and_left_alone()
    {
        var result = RequestVariables.Apply(
            "/orders?since={{последний_запуск}}&who={{опечатка}}", Context(), out var unknown);

        Assert.Equal(["опечатка"], unknown);
        Assert.Contains("{{опечатка}}", result, StringComparison.Ordinal);
        Assert.Contains("since=1970-01-01T00:00:00Z", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_unknown_name_is_reported_once()
    {
        RequestVariables.Apply("{{нет}}/{{нет}}/{{тоже нет}}", Context(), out var unknown);

        Assert.Equal(["нет", "тоже нет"], unknown);
    }

    /// <summary>Незакрытая скобка — просто текст: ронять из-за нее запуск не за что.</summary>
    [Fact]
    public void Unclosed_braces_are_plain_text()
    {
        var result = RequestVariables.Apply("/orders?q={{сегодня", Context(), out var unknown);

        Assert.Empty(unknown);
        Assert.Equal("/orders?q={{сегодня", result);
    }

    // ── Подсказка на экране ───────────────────────────────────────────────────

    /// <summary>
    /// Список для подсказки и сам разбор не должны разъезжаться: подстановка, попавшая в
    /// подсказку, но не разбираемая, — прямой путь к отчету «неизвестная подстановка».
    /// </summary>
    [Fact]
    public void Every_listed_substitution_actually_works()
    {
        foreach (var (name, meaning) in RequestVariables.All)
        {
            RequestVariables.Apply("{{" + name + "}}", Context(), out var unknown);

            Assert.Empty(unknown);
            Assert.False(string.IsNullOrWhiteSpace(meaning));
        }
    }
}
