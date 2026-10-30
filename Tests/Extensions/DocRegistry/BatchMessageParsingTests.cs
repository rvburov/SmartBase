using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

/// <summary>Список номеров — одна заявка с общими полями и всеми ошибками сразу.</summary>
public sealed class BatchMessageParsingTests
{
    private static readonly string[] Orgs = ["АБВГ", "ГМБВ"];

    private static string Message(string numbers, string name = "Кронштейн",
                                  string project = "Машина", string fio = "Иванов Иван Иванович",
                                  string action = "Регистрация") =>
        $"Действие: {action}\nНомер: {numbers}\nНаименование: {name}\nПроект: {project}\nФИО: {fio}";

    [Fact]
    public void Mixed_series_and_repeated_masks_keep_order_and_common_fields()
    {
        var parsed = MessageRegistration.ReadBatch(Message(
            "АБВГ.301121.ХХХ, ГМБВ.806121.ХХХ, АБВГ.301121.ХХХ"), Orgs);

        Assert.True(parsed.Ok);
        Assert.Empty(parsed.Problems);
        Assert.Equal([1, 2, 3], parsed.Items.Select(i => i.Position));
        Assert.Equal(["301121", "806121", "301121"], parsed.Items.Select(i => i.Request!.Eskd));
        Assert.Equal("ГМБВ", parsed.Items[1].Request!.Org);
        Assert.Equal(parsed.Items[0].Request, parsed.Items[2].Request);
        Assert.All(parsed.Items, item =>
        {
            Assert.Empty(item.Problems);
            Assert.Equal("Кронштейн", item.Request!.Name);
            Assert.Equal("Машина", item.Request.Project);
            Assert.Equal("Иванов Иван Иванович", item.Request.Fio);
            Assert.Null(item.Request.Number);
        });
    }

    [Fact]
    public void Every_invalid_number_is_reported_without_losing_valid_items()
    {
        var parsed = MessageRegistration.ReadBatch(Message(
            "АБВГ.30112.ХХХ, АБВГ.301121.ХХХ, АБВГ.301121.ХХХ-ХХ, АБВГ.301121.000-ХХ"), Orgs);

        Assert.False(parsed.Ok);
        Assert.Empty(parsed.Problems);
        Assert.Equal(4, parsed.Items.Count);
        Assert.Null(parsed.Items[0].Request);
        Assert.NotNull(parsed.Items[1].Request);
        Assert.Null(parsed.Items[2].Request);
        Assert.Null(parsed.Items[3].Request);
        Assert.All(parsed.Items.Where(i => i.Position != 2), item => Assert.Single(item.Problems));
        Assert.Contains("шесть цифр", parsed.Items[0].Problems.Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("30112")]
    [InlineData("3011210")]
    [InlineData("30112О")]
    [InlineData("３０１１２１")]
    public void Middle_code_requires_exactly_six_ascii_digits(string code)
    {
        var parsed = MessageRegistration.ReadBatch(Message($"АБВГ.{code}.ХХХ, АБВГ.301121.ХХХ"), Orgs);

        Assert.False(parsed.Ok);
        Assert.Contains("шесть цифр", Assert.Single(parsed.Items[0].Problems), StringComparison.Ordinal);
        Assert.NotNull(parsed.Items[1].Request);
    }

    [Fact]
    public void Catalog_absence_is_not_a_syntax_error()
    {
        var parsed = MessageRegistration.ReadBatch(Message(
            "АБВГ.301127.ХХХ, АБВГ.806121.ХХХ, АБВГ.000000.ХХХ"), Orgs);

        Assert.True(parsed.Ok);
        Assert.All(parsed.Items, item => Assert.Empty(item.Problems));
    }

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData(",АБВГ.301121.ХХХ", 2, 1)]
    [InlineData("АБВГ.301121.ХХХ,", 2, 2)]
    [InlineData("АБВГ.301121.ХХХ, ,АБВГ.301121.ХХХ", 3, 2)]
    public void Empty_items_are_errors_in_their_original_positions(string numbers, int count, int invalid)
    {
        var parsed = MessageRegistration.ReadBatch(Message(numbers), Orgs);

        Assert.False(parsed.Ok);
        Assert.Equal(count, parsed.Items.Count);
        var empty = parsed.Items[invalid - 1];
        Assert.Equal(invalid, empty.Position);
        Assert.Equal("", empty.Raw);
        Assert.Null(empty.Request);
        Assert.StartsWith("Номер: не указан номер", Assert.Single(empty.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Common_errors_appear_once_and_all_number_errors_are_retained()
    {
        var parsed = MessageRegistration.ReadBatch(Message("ошибка, АБВГ.301121.ХХХ, неверный",
            name: "", project: "", fio: "Иванов И.И.", action: "Удаление"), Orgs);

        Assert.False(parsed.Ok);
        Assert.Equal(4, parsed.Problems.Count);
        Assert.Contains(parsed.Problems, p => p.StartsWith("Действие:", StringComparison.Ordinal));
        Assert.Contains(parsed.Problems, p => p.StartsWith("Наименование:", StringComparison.Ordinal));
        Assert.Contains(parsed.Problems, p => p.StartsWith("Проект:", StringComparison.Ordinal));
        Assert.Contains(parsed.Problems, p => p.StartsWith("ФИО:", StringComparison.Ordinal));
        Assert.Single(parsed.Items[0].Problems);
        Assert.Empty(parsed.Items[1].Problems);
        Assert.NotNull(parsed.Items[1].Request);
        Assert.Single(parsed.Items[2].Problems);
    }

    [Fact]
    public void Unknown_organizations_are_checked_separately()
    {
        var parsed = MessageRegistration.ReadBatch(Message(
            "ЖЗИК.301121.ХХХ, АБВГ.301121.ХХХ, ПРСТ.806121.ХХХ"), Orgs);

        Assert.False(parsed.Ok);
        Assert.Contains("ЖЗИК", Assert.Single(parsed.Items[0].Problems), StringComparison.Ordinal);
        Assert.Empty(parsed.Items[1].Problems);
        Assert.Contains("ПРСТ", Assert.Single(parsed.Items[2].Problems), StringComparison.Ordinal);
        Assert.All(parsed.Items.Where(i => i.Problems.Count > 0), item =>
            Assert.Contains("АБВГ, ГМБВ", item.Problems[0], StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_number_formats_remain_available_together()
    {
        var parsed = MessageRegistration.ReadBatch(Message(
            "АБВГ.301121.ХХХ, АБВГ.301121.014-ХХ, АБВГ.806121.015"), Orgs);

        Assert.True(parsed.Ok);
        Assert.False(parsed.Items[0].Request!.Exact);
        Assert.True(parsed.Items[1].Request!.NewExecution);
        Assert.Equal(14, parsed.Items[1].Request!.Number);
        Assert.True(parsed.Items[2].Request!.Exact);
        Assert.Equal(15, parsed.Items[2].Request!.Number);
    }

    [Fact]
    public void Markup_spaces_case_and_settings_spelling_follow_single_registration()
    {
        var text = "<blockquote>" + Message("абвг. 301121. xxx , АБВГ.806121.ХХХ",
            fio: " Иванов   Иван  Иванович ").Replace("\n", "<br>") + "</blockquote>";

        var parsed = MessageRegistration.ReadBatch(text, ["АБBГ"]);

        Assert.True(parsed.Ok);
        Assert.All(parsed.Items, item =>
        {
            Assert.Equal("АБBГ", item.Request!.Org);
            Assert.Equal("Иванов Иван Иванович", item.Request.Fio);
        });
        Assert.Equal("абвг. 301121. xxx", parsed.Items[0].Raw);
    }

    [Fact]
    public void Commas_in_common_fields_do_not_split_the_list()
    {
        var parsed = MessageRegistration.ReadBatch(Message("АБВГ.301121.ХХХ, АБВГ.806121.ХХХ",
            name: "Вал, опора", project: "Машина, узел"), Orgs);

        Assert.True(parsed.Ok);
        Assert.Equal(2, parsed.Items.Count);
        Assert.All(parsed.Items, item =>
        {
            Assert.Equal("Вал, опора", item.Request!.Name);
            Assert.Equal("Машина, узел", item.Request.Project);
        });
    }

    [Fact]
    public void Duplicate_field_uses_the_first_value_like_single_registration()
    {
        var parsed = MessageRegistration.ReadBatch(Message("АБВГ.301121.ХХХ, АБВГ.806121.ХХХ")
            + "\nНомер: ошибка\nПроект: Другой", Orgs);

        Assert.True(parsed.Ok);
        Assert.Equal(2, parsed.Items.Count);
        Assert.All(parsed.Items, item => Assert.Equal("Машина", item.Request!.Project));
    }

    [Fact]
    public void Batch_limit_is_inclusive_and_excess_is_a_whole_request_error()
    {
        var permitted = string.Join(", ", Enumerable.Repeat("АБВГ.301121.ХХХ", MessageRegistration.MaxBatch));

        var parsed = MessageRegistration.ReadBatch(Message(permitted), Orgs);
        var excessive = MessageRegistration.ReadBatch(Message(permitted + ", АБВГ.301121.ХХХ"), Orgs);

        Assert.True(parsed.Ok);
        Assert.Equal(MessageRegistration.MaxBatch, parsed.Items.Count);
        Assert.False(excessive.Ok);
        Assert.Empty(excessive.Items);
        Assert.Contains(MessageRegistration.MaxBatch.ToString(), Assert.Single(excessive.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_everything_reports_common_fields_and_missing_number()
    {
        var parsed = MessageRegistration.ReadBatch("", Orgs);

        Assert.False(parsed.Ok);
        Assert.Equal(4, parsed.Problems.Count);
        Assert.Null(Assert.Single(parsed.Items).Request);
        Assert.StartsWith("Номер: не указан номер", Assert.Single(parsed.Items[0].Problems), StringComparison.Ordinal);
    }
}
