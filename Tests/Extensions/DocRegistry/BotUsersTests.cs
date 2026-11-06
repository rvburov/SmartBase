using SmartBase.Extensions;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Extensions.DocRegistry;

public sealed class BotUsersTests
{
    public const string Profile = """{"userId":1001,"family":"Иванов","fname":"Иван","sname":"Иванович","description":"Псевдоним","useDescription":true,"division":"Бюро"}""";

    [Fact]
    public void Full_name_comes_from_profile_fields_even_when_the_display_name_is_an_alias()
    {
        var user = Assert.Single(BotUsers.Read(Profile));
        Assert.Equal("Иванов Иван Иванович", user.FullName);
        Assert.Equal("1001", user.Id);
        Assert.Equal("Бюро", user.Division);
        Assert.True(user.Complete);
    }

    [Fact]
    public void Equal_names_are_distinguished_without_using_ids_as_the_main_label()
    {
        var users = new BotDirectory(Users: [new("1", "Иванов Иван Иванович", "Бюро 1"),
            new("2", "Иванов Иван Иванович", "Бюро 2")]);
        var choices = BotUsers.Choices(users);
        Assert.Equal(["Иванов Иван Иванович · Бюро 1", "Иванов Иван Иванович · Бюро 2"], choices.Select(c => c.Label));
        Assert.Equal(["1", "2"], choices.Select(c => c.Id));
    }

    [Fact]
    public void Identical_names_and_departments_still_have_distinct_rows()
    {
        var choices = BotUsers.Choices(new BotDirectory(Users: [new("1", "Иванов Иван Иванович"), new("2", "Иванов Иван Иванович")]));
        Assert.Equal(2, choices.Select(c => c.Label).Distinct().Count());
        Assert.All(choices, c => Assert.StartsWith("Иванов Иван Иванович", c.Label, StringComparison.Ordinal));
    }

    [Fact]
    public void Refreshing_the_directory_does_not_remove_an_existing_access_id()
    {
        var choices = BotUsers.Choices(new(), "1001 Старое имя");
        Assert.Equal("1001", Assert.Single(choices).Id);
        Assert.Contains("недоступно", choices[0].Label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1002")]
    public async Task An_unlisted_sender_is_refused_without_querying_the_api(string access)
    {
        var requests = new FakeRequests();
        var result = await new RegisterMessageAction().PrepareAsync(Input(), Settings(access), requests);
        Assert.NotNull(result.Failure);
        Assert.Contains("нет доступа", ReplyMarkup.Text(result.Failure.Answer), StringComparison.Ordinal);
        Assert.Equal(0, requests.Calls);
    }

    [Fact]
    public async Task A_manual_name_and_a_cached_name_cannot_replace_the_fresh_profile()
    {
        var settings = Settings("1001");
        settings[BotUsers.DirectoryKey] = BotUsers.Save(new(1, 7, [new("1001", "Другое Имя Отчество")]));
        var input = Input("Действие: Регистрация\nФИО: Поддельное Имя Отчество");
        var requests = new FakeRequests();
        var result = await new RegisterMessageAction().PrepareAsync(input, settings, requests);
        Assert.Null(result.Failure);
        Assert.Equal(new ActionIdentity("1001", BotStand.FullName), result.Input.Identity);
        Assert.Equal("1001", requests.Sender);
    }

    [Theory]
    [InlineData("{\"userId\":1002,\"family\":\"Петров\",\"fname\":\"Петр\",\"sname\":\"Петрович\"}")]
    [InlineData("{\"userId\":1001,\"family\":\"Иванов\",\"fname\":\"Иван\",\"description\":\"Иванов Иван Иванович\"}")]
    [InlineData("{\"userId\":1001,\"senderName\":\"Иванов Иван Иванович\"}")]
    [InlineData("not-json")]
    public async Task Another_user_or_an_incomplete_card_does_not_prepare_a_registration(string json)
    {
        var result = await new RegisterMessageAction().PrepareAsync(Input(), Settings("1001"), new FakeRequests { Json = json });
        Assert.NotNull(result.Failure);
        Assert.Null(result.Input.Identity);
    }

    [Fact]
    public async Task An_api_failure_cannot_fall_back_to_the_message_or_cached_name()
    {
        var result = await new RegisterMessageAction().PrepareAsync(Input(), Settings("1001"), new FakeRequests { Ok = false });
        Assert.NotNull(result.Failure);
        Assert.Null(result.Input.Identity);
    }

    [Fact]
    public async Task A_directory_from_another_source_cannot_name_the_sender()
    {
        var settings = Settings("1001");
        settings[BotUsers.DirectoryKey] = BotUsers.Save(new(1, 7));
        var requests = new FakeRequests { ListConnectionId = 2 };
        var result = await new RegisterMessageAction().PrepareAsync(Input(), settings, requests);
        Assert.NotNull(result.Failure);
        Assert.Null(result.Input.Identity);
        Assert.Equal(0, requests.Calls);
    }

    [Fact]
    public async Task Cancellation_is_propagated_instead_of_preparing_an_identity()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RegisterMessageAction().PrepareAsync(
            Input(), Settings("1001"), new FakeRequests(), stop.Token));
    }

    public static Dictionary<string, string> Settings(string access) => new()
    {
        [DocRegistryExtension.SendersKey] = access,
        [BotUsers.DirectoryKey] = BotUsers.Save(new(ProfileRequestId: 7)),
    };
    private static ActionInput Input(string text = "Действие: Регистрация") => new(new Dictionary<string, string>
    { [RegisterMessageAction.SenderField] = "1001", [RegisterMessageAction.TextField] = text });

    private sealed class FakeRequests : IExtensionRequests
    {
        public int ListConnectionId { get; init; } = 1;
        public IReadOnlyList<ExtensionReadRequest> Requests => [new(1, "Список", ListConnectionId), new(7, "Карточка", 1)];
        public string Json { get; init; } = Profile;
        public bool Ok { get; init; } = true;
        public int Calls { get; private set; }
        public string? Sender { get; private set; }
        public Task<ExtensionReadResult> ReadAsync(int requestId, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal(7, requestId);
            Sender = fields["sender"];
            return Task.FromResult(new ExtensionReadResult(Ok, Ok ? "" : "Ошибка API", Json));
        }
    }
}
