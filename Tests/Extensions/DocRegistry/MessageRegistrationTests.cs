using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>
/// Разбор и проверка сообщения о регистрации (<see cref="MessageRegistration"/>) — без базы:
/// только текст и списки из настроек.
///
/// Главное здесь — ответ отправителю. Ошибки называются ВСЕ СРАЗУ, по строке на поле и словами,
/// по которым сообщение можно исправить с первого раза: ответ «не указан проект», за которым
/// после исправления приходит «неправильный номер», — это лишний круг переписки.
/// </summary>
public sealed class MessageRegistrationTests
{
    private static readonly string[] Orgs = ["АБВГ", "ГМБВ"];

    private const string Good = """
        Действие: Регистрация
        Номер: АБВГ.301121.ХХХ
        Наименование: Кронштейн
        Проект: Машина
        ФИО: Иванов Иван Иванович
        """;

    private static string Message(string number = "АБВГ.301121.ХХХ", string name = "Кронштейн",
                                  string project = "Машина", string fio = "Иванов Иван Иванович",
                                  string action = "Регистрация") =>
        $"Действие: {action}\nНомер: {number}\nНаименование: {name}\nПроект: {project}\nФИО: {fio}";

    // ── Годное сообщение ──────────────────────────────────────────────────────

    [Fact]
    public void A_good_message_becomes_a_request()
    {
        var (request, problems) = MessageRegistration.Read(Good, Orgs);

        Assert.Empty(problems);
        Assert.NotNull(request);
        Assert.Equal("АБВГ",   request!.Org);
        Assert.Equal("301121", request.Eskd);
        Assert.Null(request.Number);
        Assert.False(request.NewExecution);
        Assert.Equal("Кронштейн", request.Name);
        Assert.Equal("Машина",    request.Project);
        Assert.Equal("Иванов Иван Иванович", request.Fio);
    }

    /// <summary>Номер с меткой исполнения — просьба о следующем исполнении названного документа.</summary>
    [Fact]
    public void A_numbered_mask_asks_for_an_execution()
    {
        var (request, problems) = MessageRegistration.Read(Message("АБВГ.301121.001-ХХ"), Orgs);

        Assert.Empty(problems);
        Assert.Equal(1, request!.Number);
        Assert.True(request.NewExecution);
    }

    /// <summary>Порядок строк и регистр ключей не важны: сообщение набирает человек.</summary>
    [Fact]
    public void Line_order_and_key_case_do_not_matter()
    {
        var (request, problems) = MessageRegistration.Read(
            "фио: Иванов Иван Иванович\nПРОЕКТ: Машина\nнаименование: Кронштейн\n"
            + "номер: АБВГ.301121.ХХХ\nдействие: регистрация", Orgs);

        Assert.Empty(problems);
        Assert.NotNull(request);
    }

    /// <summary>
    /// Латинские буквы, неотличимые на экране от русских, принимаются: иначе «ABBG» латиницей
    /// завело бы вторую серию, и номера в ней пошли бы по второму кругу.
    /// </summary>
    [Theory]
    [InlineData("АБВГ.301121.XXX")]      // метка латиницей
    [InlineData("абвг.301121.ххх")]      // строчные
    [InlineData("АБВГ. 301121. ХХХ")]    // пробелы от спешки
    public void Look_alike_letters_and_spaces_are_forgiven(string number)
    {
        var (request, problems) = MessageRegistration.Read(Message(number), Orgs);

        Assert.Empty(problems);
        Assert.Equal("АБВГ", request!.Org);
    }

    /// <summary>Код организации записывается в написании из настроек — журнал выглядит одинаково.</summary>
    [Fact]
    public void The_org_code_takes_the_spelling_from_settings()
    {
        // В настройках код с латинской «B», в сообщении — с русской «В»: это одна серия.
        var (request, _) = MessageRegistration.Read(Message("АБВГ.301121.ХХХ"), ["АБBГ"]);

        Assert.Equal("АБBГ", request!.Org);
    }

    // ── Ошибки ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Все ошибки разом, по строке на поле — ровно теми словами, которые получит отправитель.
    /// </summary>
    [Fact]
    public void All_problems_come_at_once()
    {
        var (request, problems) = MessageRegistration.Read(
            "Действие: Регистрация\nНомер: АБВГ.301121.001\nНаименование: Кронштейн\nФИО: Иванов И.И.",
            Orgs);

        Assert.Null(request);
        Assert.Equal(
        [
            "Номер: неправильный номер (Пример: АААА.000000.ХХХ или АААА.000000.001-ХХ)",
            "Проект: не указан проект",
            "ФИО: не полное ФИО (Пример: Иванов Иван Иванович)",
        ], problems);
    }

    /// <summary>Пустое сообщение — перечень всего, чего в нем нет, а не молчание.</summary>
    [Fact]
    public void An_empty_message_lists_everything_missing()
    {
        var (request, problems) = MessageRegistration.Read("", Orgs);

        Assert.Null(request);
        Assert.Equal(5, problems.Count);
        Assert.StartsWith("Действие:",     problems[0], StringComparison.Ordinal);
        Assert.StartsWith("Номер:",        problems[1], StringComparison.Ordinal);
        Assert.StartsWith("Наименование:", problems[2], StringComparison.Ordinal);
        Assert.StartsWith("Проект:",       problems[3], StringComparison.Ordinal);
        Assert.StartsWith("ФИО:",          problems[4], StringComparison.Ordinal);
    }

    /// <summary>
    /// Готовый номер и готовое исполнение не принимаются: их выдает журнал, и названный руками
    /// номер бывает уже занят.
    /// </summary>
    [Theory]
    [InlineData("АБВГ.301121.014")]
    [InlineData("АБВГ.301121.014-01")]
    [InlineData("АБВГ.301121.ХХХ-ХХ")]
    [InlineData("АБВГ.301121")]
    [InlineData("АБВГ.30112.ХХХ")]
    [InlineData("АБ.301121.ХХХ")]
    [InlineData("АБВГ.301121.000-ХХ")]
    [InlineData("QWER.301121.ХХХ")]
    public void A_number_off_the_mask_is_refused(string number)
    {
        var (request, problems) = MessageRegistration.Read(Message(number), Orgs);

        Assert.Null(request);
        Assert.Contains(problems, p => p.StartsWith("Номер: неправильный номер", StringComparison.Ordinal));
    }

    /// <summary>Код не из списка настроек — отказ с перечнем тех, что можно.</summary>
    [Fact]
    public void An_unknown_org_code_is_named_with_the_allowed_ones()
    {
        var (_, problems) = MessageRegistration.Read(Message("ЖЗИК.301121.ХХХ"), Orgs);

        var problem = Assert.Single(problems);
        Assert.Contains("ЖЗИК", problem, StringComparison.Ordinal);
        Assert.Contains("АБВГ, ГМБВ", problem, StringComparison.Ordinal);
    }

    /// <summary>Кода ЕСКД, которого нет в классификаторе, не бывает — и серии по нему тоже.</summary>
    [Fact]
    public void An_eskd_code_outside_the_catalog_is_refused()
    {
        var (_, problems) = MessageRegistration.Read(Message("АБВГ.000000.ХХХ"), Orgs);

        Assert.Contains("000000", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_action_is_refused()
    {
        var (_, problems) = MessageRegistration.Read(Message(action: "Удаление"), Orgs);

        Assert.StartsWith("Действие: неизвестное действие", Assert.Single(problems),
                          StringComparison.Ordinal);
    }

    /// <summary>Лишние пробелы в ФИО снимаются, а три слова остаются тремя словами.</summary>
    [Fact]
    public void Extra_spaces_in_the_name_are_folded()
    {
        var (request, problems) = MessageRegistration.Read(
            Message(fio: "  Иванов   Иван  Иванович "), Orgs);

        Assert.Empty(problems);
        Assert.Equal("Иванов Иван Иванович", request!.Fio);
    }

    /// <summary>Первое упоминание ключа побеждает: во втором обычно цитата, а не поправка.</summary>
    [Fact]
    public void The_first_mention_of_a_key_wins()
    {
        var (request, _) = MessageRegistration.Read(Good + "\nПроект: Другой", Orgs);

        Assert.Equal("Машина", request!.Project);
    }

    // ── Список регистрирующих ─────────────────────────────────────────────────

    /// <summary>
    /// Идентификатор — первое слово записи; остальное — пометка для того, кто ведет список.
    /// </summary>
    [Fact]
    public void Senders_are_the_first_words_of_the_entries()
    {
        var senders = MessageRegistration.Senders("1001 Иванов; 1002 Петров Петр\n1003,1004");

        Assert.Equal(4, senders.Count);
        Assert.Contains("1001", senders);
        Assert.Contains("1002", senders);
        Assert.Contains("1003", senders);
        Assert.Contains("1004", senders);
        Assert.DoesNotContain("Иванов", senders);
    }

    /// <summary>Идентификатор — строка, а не число: что он означает, расширение не знает.</summary>
    [Fact]
    public void A_sender_is_a_string_and_case_is_ignored()
    {
        var senders = MessageRegistration.Senders("ivanov@завод Иванов");

        Assert.Contains("IVANOV@ЗАВОД", senders);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" ; ; ")]
    public void An_empty_setting_names_nobody(string? setting) =>
        Assert.Empty(MessageRegistration.Senders(setting));
}
