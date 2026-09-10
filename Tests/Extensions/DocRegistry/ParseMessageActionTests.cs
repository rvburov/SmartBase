using System.Text.Json;
using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Разбор сообщения о регистрации (<see cref="ParseMessageAction"/>) — первый шаг пути «файл в
/// папке → строка журнала».
///
/// Проверяется главное свойство разбора: он НЕ знает про регистрацию. Любая строка «Ключ:
/// значение» становится колонкой с этим именем, и какая колонка в какое поле ляжет, решает шаг
/// сценария. Единственное знание о предметной области — децимальный номер, слепленный из трех
/// значений точками, — раскладывается на составляющие, потому что действию регистрации они
/// нужны врозь.
/// </summary>
public sealed class ParseMessageActionTests
{
    private static readonly ParseMessageAction Action = new();

    private static readonly ActionContext Nowhere =
        new(null, 0, 0, new DateTimeOffset(new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Local)));

    private static ActionResult Parse(string text) =>
        Action.Run(Nowhere, new ActionInput(new Dictionary<string, string>(), text));

    /// <summary>Колонки разобранной строки по порядку — порядок и есть смысл первой колонки.</summary>
    private static List<(string Column, string Value)> Row(ActionResult result)
    {
        using var document = JsonDocument.Parse(result.Json);

        return [.. document.RootElement[0].EnumerateObject()
                           .Select(p => (p.Name, p.Value.GetString() ?? ""))];
    }

    private static string Value(ActionResult result, string column) =>
        Row(result).FirstOrDefault(c => c.Column == column).Value ?? "";

    private const string Message = """
        Наименование: Кронштейн
        Проект: Молоток
        Децимальный номер: АБВГ.301121
        ФИО регистрирующего: Иванов Иван Иванович
        """;

    // ── Обычное сообщение ─────────────────────────────────────────────────────

    [Fact]
    public void Every_line_becomes_a_column()
    {
        var result = Parse(Message);

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal(1, result.Rows);

        Assert.Equal("Кронштейн",            Value(result, "Наименование"));
        Assert.Equal("Молоток",              Value(result, "Проект"));
        Assert.Equal("Иванов Иван Иванович", Value(result, "ФИО регистрирующего"));
    }

    /// <summary>
    /// Порядок колонок — порядок строк сообщения. Первая становится названием записи: так читает
    /// таблицу источник импорта, и переставить ее местами со второй значило бы завести документ
    /// под именем проекта.
    /// </summary>
    [Fact]
    public void The_first_line_stays_first()
    {
        Assert.Equal("Наименование", Row(Parse(Message))[0].Column);
    }

    /// <summary>Значение с двоеточием внутри режется только по ПЕРВОМУ двоеточию.</summary>
    [Fact]
    public void Only_the_first_colon_separates()
    {
        Assert.Equal("12:30", Value(Parse("Время: 12:30"), "Время"));
    }

    /// <summary>Строка без двоеточия — не пара, и разбор о нее не спотыкается.</summary>
    [Fact]
    public void A_line_without_a_colon_is_skipped()
    {
        var result = Parse("Здравствуйте!\nПроект: Молоток\nСпасибо");

        Assert.True(result.Ok, result.Explanation);
        Assert.Equal("Молоток", Value(result, "Проект"));
    }

    /// <summary>
    /// Повторенный ключ: побеждает первое упоминание. Во втором обычно лежит цитата переписки, а
    /// не поправка, и гадать, какое из двух значений человек имел в виду, нельзя.
    /// </summary>
    [Fact]
    public void The_first_mention_of_a_key_wins()
    {
        Assert.Equal("Молоток", Value(Parse("Проект: Молоток\nПроект: Рубанок"), "Проект"));
    }

    // ── Децимальный номер ─────────────────────────────────────────────────────

    /// <summary>
    /// Обозначение по ГОСТ — три значения, слепленные точками, а действию регистрации они нужны
    /// врозь. Просить человека расписать их по строкам значило бы просить его делать работу
    /// программы.
    /// </summary>
    [Fact]
    public void The_designation_is_split_into_its_parts()
    {
        var result = Parse(Message);

        Assert.Equal("АБВГ",   Value(result, ParseMessageAction.OrgColumn));
        Assert.Equal("301121", Value(result, ParseMessageAction.EskdColumn));
        Assert.Equal("",       Value(result, ParseMessageAction.NumberColumn));
    }

    [Fact]
    public void A_number_and_an_execution_are_read_too()
    {
        var result = Parse("Децимальный номер: АБВГ.301121.005-01");

        Assert.Equal("АБВГ",   Value(result, ParseMessageAction.OrgColumn));
        Assert.Equal("301121", Value(result, ParseMessageAction.EskdColumn));
        Assert.Equal("005",    Value(result, ParseMessageAction.NumberColumn));
        Assert.Equal("01",     Value(result, ParseMessageAction.ExecutionColumn));
    }

    /// <summary>Слово человека старше вычисленного: указанный им код не переписывается.</summary>
    [Fact]
    public void A_column_named_by_hand_is_not_overwritten()
    {
        var result = Parse($"{ParseMessageAction.OrgColumn}: ЖЗИК\nДецимальный номер: АБВГ.301121");

        Assert.Equal("ЖЗИК", Value(result, ParseMessageAction.OrgColumn));
        Assert.Equal("301121", Value(result, ParseMessageAction.EskdColumn));
    }

    /// <summary>
    /// ФИО с инициалами тоже написано через точки, но обозначением не становится: код ЕСКД —
    /// ровно шесть цифр, и это единственная проверка, которая тут спасает.
    /// </summary>
    [Fact]
    public void Initials_are_not_a_designation()
    {
        var result = Parse("ФИО регистрирующего: Иванов И.И.");

        Assert.Equal("", Value(result, ParseMessageAction.OrgColumn));
    }

    /// <summary>
    /// Номер читается только в каноне: ровно три цифры. «АБВГ.301121.5» — не обозначение по
    /// ГОСТ, и прочитать его как 005 значило бы угадать за человека.
    /// </summary>
    [Theory]
    [InlineData("АБВГ.30112.001")]    // код ЕСКД короче шести цифр
    [InlineData("АБВГ.3011210.001")]  // и длиннее
    [InlineData("АБВГ.301121.5")]     // номер не тремя цифрами
    [InlineData("АБВГ.301121.0011")]
    public void A_designation_off_the_standard_is_not_split(string text)
    {
        Assert.Equal("", Value(Parse("Децимальный номер: " + text),
                               ParseMessageAction.OrgColumn));
    }

    // ── Отказы ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Отказ едет словами и результатом, а не исключением: у сообщения нет собеседника, и
    /// причина обязана попасть в квитанцию отправителю.
    /// </summary>
    [Fact]
    public void An_empty_message_is_refused_in_words()
    {
        var result = Parse("");

        Assert.False(result.Ok);
        Assert.Contains("пустое", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_without_pairs_is_refused()
    {
        var result = Parse("Здравствуйте, зарегистрируйте пожалуйста кронштейн");

        Assert.False(result.Ok);
        Assert.Contains("Ключ: значение", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>Файл, положенный в папку по ошибке, не становится таблицей из тысячи колонок.</summary>
    [Fact]
    public void A_file_that_is_not_a_message_is_refused()
    {
        var lines = string.Join("\n", Enumerable.Range(0, ParseMessageAction.MaxLines + 1)
                                                .Select(i => $"Поле {i}: значение"));

        var result = Parse(lines);

        Assert.False(result.Ok);
        Assert.Contains("не сообщение", result.Explanation, StringComparison.Ordinal);
    }

    // ── Договор ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Разбор — преобразователь: он получает текст целиком и полей не объявляет. В базу он не
    /// пишет и подключения не получает вовсе.
    /// </summary>
    [Fact]
    public void The_parser_is_a_transformer_without_fields()
    {
        Assert.Equal(ExtensionActionKind.Transformer, Action.Kind);
        Assert.Empty(((IExtensionAction)Action).Fields);
    }
}
