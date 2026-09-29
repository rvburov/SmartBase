using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Подстановки в пути и теле запроса (<see cref="RequestVariables"/>).
///
/// Главная проверка — незнакомая подстановка не превращается в текст. Оставь
/// <c>{{опечатка}}</c> в адресе как есть, и она уедет в стороннюю программу буквально, а пустой
/// ответ потом будут искать неделю.
///
/// Второе по важности — первый запуск. Пустая строка вместо даты не годится: половина чужих
/// программ отвечает на <c>since=</c> отказом, и первый же обмен встанет.
/// </summary>
public sealed class RequestVariablesTests
{
    private static readonly DateOnly Day = new(2026, 9, 9);

    private static VariableContext Context(DateTimeOffset? lastRun = null, string address = "",
                                           IReadOnlyDictionary<string, string>? record = null,
                                           string? reply = null) =>
        new(Day, lastRun, address, record, reply);

    /// <summary>Единственная уходящая запись — то, чем живет подстановка «поле.Имя».</summary>
    private static IReadOnlyDictionary<string, string> One =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Номер"]  = "А-1043",
            ["Статус"] = "отгружен",
        };

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
    ///
    /// Контекст здесь с записью: подстановка поля в подсказке названа образцом «поле.Имя», и
    /// без записи она честно ответила бы «не знаю такого поля». И с текстом ответа — по той же
    /// причине: вне ответа такой подстановки нет.
    /// </summary>
    [Fact]
    public void Every_listed_substitution_actually_works()
    {
        var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Имя"] = "А-1" };

        foreach (var (name, meaning) in RequestVariables.All)
        {
            RequestVariables.Apply("{{" + name + "}}", Context(record: record, reply: "Готово"),
                                   out var unknown);

            Assert.Empty(unknown);
            Assert.False(string.IsNullOrWhiteSpace(meaning));
        }
    }

    // ── Текст ответа на событие ───────────────────────────────────────────────

    /// <summary>
    /// В теле вида JSON текст ответа ложится содержимым строки: перевод строки и кавычки
    /// экранируются, иначе тело стало бы негодным и та сторона отвергла бы ответ. Кириллица
    /// остается кириллицей — журнал и сухой прогон читаются глазами.
    /// </summary>
    [Fact]
    public void The_reply_text_is_escaped_inside_a_json_body()
    {
        const string said = "Номер: неправильный\nПроект: «не указан» \"совсем\"";

        var result = Apply("""{"message": "{{ответ.текст}}"}""", Context(reply: said));

        Assert.DoesNotContain('\n', result);
        Assert.Contains("Номер: неправильный", result, StringComparison.Ordinal);

        // И главное — получившееся тело разбирается, а текст в нем тот же, что был.
        using var parsed = System.Text.Json.JsonDocument.Parse(result);
        Assert.Equal(said, parsed.RootElement.GetProperty("message").GetString());
    }

    /// <summary>В пути и в теле-форме текст ответа идет параметром адреса.</summary>
    [Fact]
    public void The_reply_text_is_url_escaped_outside_json()
    {
        var context = Context(reply: "Готово: 1 & 2");

        RequestVariables.Apply("/reply?text={{ответ.текст}}", context, out var inPath,
                               VariablePlace.Path);
        var form = RequestVariables.Apply("text={{ответ.текст}}", context, out var inForm,
                                          VariablePlace.Body);

        Assert.Empty(inPath);
        Assert.Empty(inForm);
        Assert.Equal("text=" + Uri.EscapeDataString("Готово: 1 & 2"), form);
    }

    /// <summary>
    /// Вне ответа подстановки нет вовсе — ни пустоты, ни образца: запрос ответа, запущенный
    /// кнопкой, ушел бы той стороне с пустым текстом.
    /// </summary>
    [Fact]
    public void Outside_a_reply_the_reply_text_is_unknown()
    {
        RequestVariables.Apply("""{"message": "{{ответ.текст}}"}""", Context(), out var unknown);

        Assert.Equal([RequestVariables.ReplyText], unknown);
        Assert.Contains("событие потока", RequestVariables.ReplyOnlyNote(unknown),
                        StringComparison.Ordinal);
    }

    /// <summary>Отказ по другой подстановке добавки про ответ не получает.</summary>
    [Fact]
    public void The_reply_note_is_silent_about_other_names() =>
        Assert.Equal("", RequestVariables.ReplyOnlyNote(["опечатка"]));

    /// <summary>
    /// В запросе ответа запись — строка события: <c>{{поле.имя}}</c> берет ее колонку. Так ответ
    /// находит получателя, не зная заранее, как та сторона его называет.
    /// </summary>
    [Fact]
    public void A_reply_takes_the_recipient_from_the_event_row()
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["senderId"] = "1001",
        };

        var result = Apply("""{"to": {{поле.senderId}}, "message": "{{ответ.текст}}"}""",
                           Context(record: row, reply: "Готово"));

        Assert.Equal("""{"to": 1001, "message": "Готово"}""", result);
    }

    // ── Поле уходящей записи ──────────────────────────────────────────────────

    /// <summary>
    /// Поле единственной уходящей записи подставляется В ПУТЬ: сторонние программы правят запись по
    /// адресу вида <c>PUT /orders/А-1043</c>, где номер лежит в пути, а не в теле.
    ///
    /// Без этой подстановки такой обмен не выражался вовсе: исходящая привязка собирает тело и на
    /// путь не влияет никак, и единственным способом было завести отдельную карточку запроса на
    /// каждую запись.
    /// </summary>
    [Fact]
    public void A_field_of_the_single_record_goes_into_the_path()
    {
        Assert.Equal("/orders/А-1043",
                     Apply("/orders/{{поле.Номер}}", Context(record: One)));

        // Регистр имени поля не важен — как и везде, где поле называют по имени.
        Assert.Equal("/orders/А-1043/отгружен",
                     Apply("/orders/{{поле.номер}}/{{поле.СТАТУС}}", Context(record: One)));
    }

    /// <summary>
    /// Записей не одна — подстановки нет, и это ОТКАЗ, а не пустое место. Путь собирается один
    /// раз на запуск; подставь в него запись из трех — две оставшиеся ушли бы по чужому адресу.
    /// </summary>
    [Fact]
    public void Without_a_single_record_the_substitution_refuses()
    {
        RequestVariables.Apply("/orders/{{поле.Номер}}", Context(), out var unknown);

        Assert.Equal(["поле.Номер"], unknown);
    }

    /// <summary>
    /// Неизвестное имя поля — тоже отказ, а не пустота. Пустой хвост в «/orders/» ушел бы к чужой
    /// программе как обращение ко ВСЕМУ списку, а не к одной записи, и ответ на него выглядел бы
    /// удачным.
    /// </summary>
    [Fact]
    public void An_unknown_field_name_refuses_instead_of_emptying_the_path()
    {
        RequestVariables.Apply("/orders/{{поле.Артикул}}", Context(record: One), out var unknown);

        Assert.Equal(["поле.Артикул"], unknown);
    }
}
