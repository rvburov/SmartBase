using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Готовая заявка на разрешение адреса (<see cref="AddressRequestNote"/>, docs/ROADMAP-API.md,
/// 10.18).
///
/// Отказ «адрес не входит в белый список» оставляет человека с задачей, которую он решать не
/// умеет: к кому идти, что писать, какие сведения приложить. Ответственный за интеграции все
/// равно спросит то же самое — адрес, зачем, какая коллекция, какие поля, — и спросит не сразу, а
/// через день.
///
/// Главная проверка здесь — не «складывается ли текст», а <see cref="No_secrets_travel_in_the_note"/>:
/// заявку вставляют в письмо и пересылают дальше, и это ровно тот путь, на котором секрет
/// теряется навсегда.
/// </summary>
public sealed class AddressRequestNoteTests : IDisposable
{
    private readonly string           _dir;
    private readonly DatabaseRegistry _registry;

    public AddressRequestNoteTests()
    {
        _dir      = Directory.CreateTempSubdirectory("sbnote_").FullName;
        _registry = DatabaseRegistry.Open(Path.Combine(_dir, "database.db"),
                                          Path.Combine(_dir, "app.db"));
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private IntegrationsRegistry Cards => _registry.Integrations;

    private static readonly DateOnly Today = new(2026, 9, 13);

    private static EgressPolicySet Policy() =>
        EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, ["*.zavod.local"], ["10.0.0.0/8"], [],
            1000, 1024, 10, null, false, "реестр HKLM"));

    /// <summary>Соединение с чужим адресом и один запрос, укладывающий ответ в коллекцию.</summary>
    private IntegrationConnection Ready(bool withRequest = true, bool withBinding = true)
    {
        var connection = Cards.Save(IntegrationConnection.New() with
        {
            Name    = "Поставщик",
            Address = "https://api.postavshik.example/v2",
        });

        if (!withRequest) return connection;

        var request = Cards.SaveRequest(IntegrationRequest.New() with
        {
            Name         = "Заказы",
            ConnectionId = connection.Id,
            Path         = "/orders",
        });

        if (withBinding)
            Cards.SaveBinding(request.Id, new CollectionBinding(1, 42,
            [
                new BindingColumn("Название", null),
                new BindingColumn("Номер",    7),
                new BindingColumn("Статус",   8),
            ]));

        return connection;
    }

    private AddressRequest Gather(IntegrationConnection connection, string refusal =
        "адрес «api.postavshik.example» не входит в белый список организации") =>
        AddressRequestNote.Gather(Cards, connection, refusal,
                                  collections: (_, _) => "Заказы",
                                  who: "Иванов И. И.", machine: "ПК17");

    // ── Что в заявке ──────────────────────────────────────────────────────────

    /// <summary>
    /// В заявке есть все, что ответственный все равно спросил бы: адрес, отказ дословно, запрос,
    /// коллекция и поля.
    /// </summary>
    [Fact]
    public void The_note_answers_what_the_officer_would_ask()
    {
        var text = AddressRequestNote.Text(Gather(Ready()), Policy(), Today);

        Assert.Contains("https://api.postavshik.example/v2", text, StringComparison.Ordinal);
        Assert.Contains("Поставщик",  text, StringComparison.Ordinal);
        Assert.Contains("не входит в белый список", text, StringComparison.Ordinal);
        Assert.Contains("«Заказы» GET /orders",     text, StringComparison.Ordinal);
        Assert.Contains("коллекция «Заказы»",       text, StringComparison.Ordinal);
        Assert.Contains("Название, Номер, Статус",  text, StringComparison.Ordinal);
        Assert.Contains("Иванов И. И.", text, StringComparison.Ordinal);
        Assert.Contains("ПК17",         text, StringComparison.Ordinal);
        Assert.Contains("13.09.2026",   text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Что разрешено СЕЙЧАС — тоже в заявке: ответственный правит файл и должен видеть, к чему
    /// дописывает, а не идти за этим на чужой экран.
    /// </summary>
    [Fact]
    public void The_note_shows_what_is_allowed_today()
    {
        var text = AddressRequestNote.Text(Gather(Ready()), Policy(), Today);

        Assert.Contains("*.zavod.local", text, StringComparison.Ordinal);
        Assert.Contains("10.0.0.0/8",    text, StringComparison.Ordinal);
        Assert.Contains("реестр HKLM",   text, StringComparison.Ordinal);
        Assert.Contains("внутренний",    text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Секретов в заявке нет.</b> Она уходит в переписку и пересылается дальше — ровно тот
    /// путь, на котором токен теряется навсегда и отозвать его уже некому.
    /// </summary>
    [Fact]
    public void No_secrets_travel_in_the_note()
    {
        var connection = Ready();
        Cards.Save(connection, "токен-поставщика-который-нельзя-показывать");

        var text = AddressRequestNote.Text(Gather(connection), Policy(), Today);

        Assert.DoesNotContain("токен-поставщика", text, StringComparison.Ordinal);
        Assert.Contains("секретов в этой заявке нет", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Запросов еще нет — соединение заводят первым, и это обычное дело. Молчать об этом нельзя:
    /// ответственный решит, что сведения потерялись по дороге.
    /// </summary>
    [Fact]
    public void A_connection_without_requests_says_so_instead_of_staying_silent()
    {
        var text = AddressRequestNote.Text(Gather(Ready(withRequest: false)), Policy(), Today);

        Assert.Contains("запросы еще не заведены", text, StringComparison.Ordinal);
    }

    /// <summary>Запрос есть, а укладки нет — тоже сказано прямо, а не пропущено.</summary>
    [Fact]
    public void A_request_without_a_binding_is_named_as_such()
    {
        var text = AddressRequestNote.Text(Gather(Ready(withBinding: false)), Policy(), Today);

        Assert.Contains("«Заказы» GET /orders", text, StringComparison.Ordinal);
        Assert.Contains("еще не настроена",     text, StringComparison.Ordinal);
    }

    /// <summary>Политики нет вовсе — заявка все равно собирается: именно тогда она и нужнее всего.</summary>
    [Fact]
    public void Without_a_policy_the_note_is_still_made()
    {
        var text = AddressRequestNote.Text(
            Gather(Ready(), refusal: "политика организации не найдена: исходящие обращения запрещены"),
            EgressPolicySet.Closed, Today);

        Assert.Contains("политика организации не найдена", text, StringComparison.Ordinal);
        Assert.Contains("не заданы", text, StringComparison.Ordinal);   // списки пусты
    }
}
