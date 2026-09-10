using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Формат входящего файла (<see cref="InboxMessage"/>): что разбирается, что считается годным и
/// почему идентификатор обязателен не всегда.
///
/// Файл собирает ЧУЖАЯ программа, и половина проверок здесь про то, как она ошибется: не тот
/// корень, не тот регистр режима, отсутствующий массив записей, «..» в идентификаторе.
/// </summary>
public sealed class InboxMessageTests
{
    private const string Sample = """
        {
          "collection": "Заказы",
          "mode": "update",
          "key": "Номер",
          "id": "2026-09-10-0042",
          "rows": [ { "Номер": "А-1", "Название": "Молоток" },
                    { "Номер": "А-2", "Название": "Отвертка" } ]
        }
        """;

    // ── Разбор ────────────────────────────────────────────────────────────────

    [Fact]
    public void Sample_file_is_parsed_whole()
    {
        var message = InboxMessage.TryParse(Sample, out var error);

        Assert.NotNull(message);
        Assert.Equal("", error);
        Assert.Equal("Заказы", message!.Collection);
        Assert.Equal(InboxMode.Update, message.Mode);
        Assert.Equal("Номер", message.Key);
        Assert.Equal("2026-09-10-0042", message.MessageId);
        Assert.Equal(2, message.Rows);
        Assert.Empty(message.Problems());
    }

    /// <summary>
    /// Записи отдаются сырым текстом: дальше их читает тот же источник импорта, что и ответ
    /// чужой программы. Разбирать их дважды значило бы завести два понимания одних данных.
    /// </summary>
    [Fact]
    public void Rows_are_handed_over_as_raw_json()
    {
        var message = InboxMessage.TryParse(Sample, out _);

        Assert.NotNull(message);
        Assert.StartsWith("[", message!.RowsJson.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("Молоток", message.RowsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_file_is_refused_with_a_reason()
    {
        Assert.Null(InboxMessage.TryParse("   ", out var error));
        Assert.Contains("пустой", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Broken_json_is_refused_with_a_reason()
    {
        Assert.Null(InboxMessage.TryParse("{ \"collection\": ", out var error));
        Assert.Contains("не является JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_instead_of_an_object_is_refused()
    {
        Assert.Null(InboxMessage.TryParse("""[{"Номер":"А-1"}]""", out var error));
        Assert.Contains("объект", error, StringComparison.Ordinal);
    }

    [Fact]
    public void File_without_rows_is_refused()
    {
        Assert.Null(InboxMessage.TryParse("""{"collection":"Заказы"}""", out var error));
        Assert.Contains("rows", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Незнакомый режим читается как «добавить» — самый безобидный: он ничего не перезаписывает.
    /// Строку мог написать более новый выпуск программы-отправителя.
    /// </summary>
    [Fact]
    public void Unknown_mode_reads_as_add()
    {
        var message = InboxMessage.TryParse(
            """{"collection":"Заказы","mode":"МЕРЖ","id":"x1","rows":[{"а":1}]}""", out _);

        Assert.Equal(InboxMode.Add, message!.Mode);
    }

    [Fact]
    public void Mode_is_read_regardless_of_case_and_spaces()
    {
        var message = InboxMessage.TryParse(
            """{"collection":"Заказы","mode":" Update ","key":"Номер","rows":[{"а":1}]}""", out _);

        Assert.Equal(InboxMode.Update, message!.Mode);
    }

    // ── Что не годится ────────────────────────────────────────────────────────

    [Fact]
    public void Collection_is_required()
    {
        var message = InboxMessage.TryParse("""{"mode":"add","id":"x1","rows":[{"а":1}]}""", out _);

        Assert.Contains(message!.Problems(), p => p.Contains("коллекция", StringComparison.Ordinal));
    }

    [Fact]
    public void Message_without_rows_in_the_array_is_refused()
    {
        var message = InboxMessage.TryParse(
            """{"collection":"Заказы","mode":"add","id":"x1","rows":[]}""", out _);

        Assert.Contains(message!.Problems(), p => p.Contains("нет записей", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_without_a_key_is_refused()
    {
        var message = InboxMessage.TryParse(
            """{"collection":"Заказы","mode":"update","rows":[{"а":1}]}""", out _);

        Assert.Contains(message!.Problems(), p => p.Contains("update", StringComparison.Ordinal));
    }

    /// <summary>
    /// Идентификатор обязателен именно у «добавить»: там от повтора не защищает ничего. В режиме
    /// «обновить по ключу» вторая доставка того же файла перезапишет те же записи теми же
    /// значениями, и требовать идентификатор значило бы отказывать отправителю без причины.
    /// </summary>
    [Fact]
    public void Identifier_is_required_only_where_a_repeat_would_duplicate()
    {
        var add = InboxMessage.TryParse(
            """{"collection":"Заказы","mode":"add","rows":[{"а":1}]}""", out _);

        var update = InboxMessage.TryParse(
            """{"collection":"Заказы","mode":"update","key":"Номер","rows":[{"а":1}]}""", out _);

        Assert.Contains(add!.Problems(), p => p.Contains("идентификатор", StringComparison.Ordinal));
        Assert.Empty(update!.Problems());
    }

    /// <summary>
    /// Идентификатор становится именем файла квитанции, поэтому «..» и разделители пути увели бы
    /// запись из папки обмена куда угодно.
    /// </summary>
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("папка/файл")]
    [InlineData("файл\\другой")]
    [InlineData("с пробелом")]
    [InlineData("двоеточие:тут")]
    public void Identifier_that_is_not_a_safe_file_name_is_refused(string id)
    {
        Assert.False(InboxMessage.ValidId(id));

        var message = InboxMessage.TryParse(
            $$"""{"collection":"Заказы","mode":"add","id":{{System.Text.Json.JsonSerializer.Serialize(id)}},"rows":[{"а":1}]}""",
            out _);

        Assert.Contains(message!.Problems(), p => p.Contains("Идентификатор", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-09-10-0042")]
    [InlineData("order_42")]
    [InlineData("a.b.c")]
    public void Ordinary_identifier_is_accepted(string id) => Assert.True(InboxMessage.ValidId(id));

    [Fact]
    public void Overlong_identifier_is_refused() =>
        Assert.False(InboxMessage.ValidId(new string('a', 101)));
}
