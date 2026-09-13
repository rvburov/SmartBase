using System.Net;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Решения единственной двери наружу (<see cref="EgressGate"/>): что она пускает, что нет и
/// с какой причиной. Причина проверяется наравне с самим запретом — по коду
/// <see cref="EgressDenyReason"/> человек в журнале понимает, к кому идти: к ответственному за
/// политику, к администратору базы или к тому, кто настраивал сценарий.
///
/// Политика подставляется тестами через <see cref="EgressGate.AttachPolicy"/>, как
/// <c>AppSettings.AttachStore</c> в остальных тестах; параллельный прогон в этой сборке
/// выключен намеренно (см. AssemblyInfo.cs), поэтому статика тестам не мешает.
/// </summary>
public sealed class EgressGateTests
{
    private static readonly DateOnly Today = new(2026, 9, 9);

    private static EgressPolicy Policy(
        EgressMode mode = EgressMode.External,
        string[]?  hosts = null,
        string[]?  networks = null,
        string[]?  folders = null,
        DateOnly?  until = null) =>
        new(mode, hosts ?? [], networks ?? [], folders ?? [], 1000, 1024, 10, until, false, "тест");

    private static void Attach(params EgressPolicy[] sources) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(sources));

    private static Uri Address(string url) => new(url);

    // ── Состояние по умолчанию ────────────────────────────────────────────────

    /// <summary>
    /// Пока политику не принесли, дверь закрыта. Это главный тест файла: окно между стартом
    /// программы и загрузкой политики не должно быть дырой.
    /// </summary>
    [Fact]
    public void Gate_is_closed_until_a_policy_is_attached()
    {
        EgressGate.Close();

        var decision = EgressGate.Check(Address("https://api.zavod.local/v2"), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.NoPolicy, decision.Reason);
    }

    /// <summary>
    /// Обычная сборка — не закрытая. Тест пришпиливает состояние по умолчанию: константа
    /// <c>CLOSED_LOOP</c> включается только ключом <c>-p:ClosedLoop=true</c>, и включенной
    /// в обычном дистрибутиве оказаться не должна — иначе интеграции молча перестанут
    /// работать у всех.
    ///
    /// Саму закрытую сборку эта сборка тестов не проверяет и не может: там шлюз отвечает
    /// отказом на все, и половина тестов файла обязана падать. Закрытая сборка проверяется
    /// отдельно — сборкой с ключом и разбором манифеста (docs/ROADMAP-API.md, 10.16.1).
    /// </summary>
    [Fact]
    public void Regular_build_is_not_the_closed_loop_one() =>
        Assert.False(EgressGate.ClosedLoopBuild);

    [Fact]
    public void Isolated_mode_denies_even_a_listed_host()
    {
        Attach(Policy(mode: EgressMode.Isolated, hosts: ["api.zavod.local"]));

        var decision = EgressGate.Check(Address("https://api.zavod.local/v2"), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.IsolatedMode, decision.Reason);
    }

    [Fact]
    public void Expired_policy_denies()
    {
        Attach(Policy(hosts: ["api.zavod.local"], until: new DateOnly(2026, 9, 8)));

        var decision = EgressGate.Check(Address("https://api.zavod.local/v2"), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.PolicyExpired, decision.Reason);
    }

    // ── Проверка по имени ─────────────────────────────────────────────────────

    [Fact]
    public void Listed_host_over_http_or_https_is_allowed()
    {
        Attach(Policy(hosts: ["*.zavod.local"]));

        Assert.True(EgressGate.Check(Address("https://api.zavod.local/v2"), Today).Allowed);
        Assert.True(EgressGate.Check(Address("http://srv-1c.zavod.local:8080/exchange"), Today).Allowed);
    }

    [Fact]
    public void Host_outside_the_list_is_denied()
    {
        Attach(Policy(hosts: ["*.zavod.local"]));

        var decision = EgressGate.Check(Address("https://api.attacker.example/hook"), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.HostNotAllowed, decision.Reason);
    }

    /// <summary>Схемы, кроме http и https, не обсуждаются: через file и ftp данные уходят так же.</summary>
    [Theory]
    [InlineData("ftp://api.zavod.local/upload")]
    [InlineData("ws://api.zavod.local/socket")]
    public void Only_http_and_https_are_allowed(string url)
    {
        Attach(Policy(hosts: ["*.zavod.local"]));

        var decision = EgressGate.Check(Address(url), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.SchemeNotAllowed, decision.Reason);
    }

    [Fact]
    public void Unparsable_address_is_denied()
    {
        Attach(Policy(hosts: ["*.zavod.local"]));

        var decision = EgressGate.Check(null, Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.BadAddress, decision.Reason);
    }

    // ── Проверка по фактическому адресу ───────────────────────────────────────

    /// <summary>
    /// Имя из белого списка, разрешившееся в чужой адрес, дальше не проходит. Ради этого
    /// проверка и сделана второй: между сверкой имени и соединением запись DNS может смениться.
    /// </summary>
    [Fact]
    public void Resolved_address_outside_allowed_networks_is_denied()
    {
        Attach(Policy(hosts: ["*.zavod.local"], networks: ["10.0.0.0/8"]));

        Assert.True(EgressGate.Check(Address("https://api.zavod.local/v2"), Today).Allowed);

        var decision = EgressGate.CheckResolved(IPAddress.Parse("203.0.113.7"), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.AddressNotAllowed, decision.Reason);
    }

    [Fact]
    public void Resolved_address_inside_allowed_networks_is_allowed()
    {
        Attach(Policy(hosts: ["*.zavod.local"], networks: ["10.0.0.0/8"]));

        Assert.True(EgressGate.CheckResolved(IPAddress.Parse("10.0.7.15"), Today).Allowed);
    }

    /// <summary>
    /// Внутренний режим держится на приватности адреса, а не на списке сетей: публичный адрес
    /// не проходит, даже если сеть в политике перечислена.
    /// </summary>
    [Fact]
    public void Internal_mode_denies_a_public_resolved_address()
    {
        Attach(Policy(mode: EgressMode.Internal, hosts: ["*.zavod.local"], networks: ["0.0.0.0/0"]));

        var decision = EgressGate.CheckResolved(IPAddress.Parse("203.0.113.7"), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.PublicAddressInInternalMode, decision.Reason);
    }

    [Fact]
    public void Internal_mode_allows_a_private_resolved_address()
    {
        Attach(Policy(mode: EgressMode.Internal, hosts: ["*.zavod.local"], networks: ["10.0.0.0/8"]));

        Assert.True(EgressGate.CheckResolved(IPAddress.Parse("10.0.7.15"), Today).Allowed);
    }

    [Fact]
    public void Name_that_did_not_resolve_is_denied()
    {
        Attach(Policy(hosts: ["*.zavod.local"], networks: ["10.0.0.0/8"]));

        var decision = EgressGate.CheckResolved(null, Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.BadAddress, decision.Reason);
    }

    // ── Метки конфиденциальности ──────────────────────────────────────────────

    /// <summary>
    /// Закрытый контур у базы запрещает исходящие независимо от политики: это самый грубый и
    /// самый понятный проверяющему рубеж — у секретной базы исходящих путей нет вовсе.
    /// </summary>
    [Fact]
    public void Closed_database_denies_outgoing_even_with_a_permissive_policy()
    {
        Attach(Policy(hosts: ["*.zavod.local"], networks: ["10.0.0.0/8"]));

        var decision = EgressGate.CheckDatabase(ConfidentialityLabel.Closed, Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.DatabaseIsClosed, decision.Reason);
    }

    /// <summary>
    /// «Для служебного пользования» обмен НЕ запрещает. Иначе метку не поставят ни на одну
    /// рабочую базу — она мешала бы работе, — и весь механизм окажется бумажным.
    /// </summary>
    [Fact]
    public void Restricted_database_may_still_exchange()
    {
        Attach(Policy(hosts: ["*.zavod.local"]));

        Assert.True(EgressGate.CheckDatabase(ConfidentialityLabel.Restricted, Today).Allowed);
        Assert.True(EgressGate.CheckDatabase(ConfidentialityLabel.Open, Today).Allowed);
    }

    /// <summary>Открытая база без политики все равно закрыта: метка не заменяет политику.</summary>
    [Fact]
    public void Open_database_without_a_policy_is_still_closed()
    {
        EgressGate.Close();

        var decision = EgressGate.CheckDatabase(ConfidentialityLabel.Open, Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.NoPolicy, decision.Reason);
    }

    [Fact]
    public void Labeled_field_never_leaves()
    {
        Assert.True(EgressGate.CheckField(ConfidentialityLabel.Open).Allowed);

        foreach (var label in new[] { ConfidentialityLabel.Restricted, ConfidentialityLabel.Closed })
        {
            var decision = EgressGate.CheckField(label);
            Assert.False(decision.Allowed);
            Assert.Equal(EgressDenyReason.FieldIsLabeled, decision.Reason);
            Assert.Contains(Confidentiality.Title(label), decision.Explanation, StringComparison.Ordinal);
        }
    }

    // ── Папки ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Folder_inside_an_allowed_root_is_allowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "obmen");
        Attach(Policy(folders: [root]));

        Assert.True(EgressGate.CheckFolder(Path.Combine(root, "ishodyaschie"), Today).Allowed);
    }

    [Fact]
    public void Folder_outside_allowed_roots_is_denied()
    {
        var root = Path.Combine(Path.GetTempPath(), "obmen");
        Attach(Policy(folders: [root]));

        var decision = EgressGate.CheckFolder(Path.Combine(Path.GetTempPath(), "fleshka"), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.FolderNotAllowed, decision.Reason);
    }

    /// <summary>Без политики закрыты и папки: корни называет она, а называть их некому.</summary>
    [Fact]
    public void Folders_are_closed_together_with_the_gate()
    {
        EgressGate.Close();

        var decision = EgressGate.CheckFolder(Path.GetTempPath(), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.NoPolicy, decision.Reason);
    }

    // ── Изолированный режим: сеть закрыта, папки работают ─────────────────────

    /// <summary>
    /// Главное про изолированный режим (10.4): он запрещает СЕТЕВЫЕ обращения, а папка обмена в
    /// нем работает — «работают локальные шаги: папка-приемник … экспорт в разрешенные папки».
    ///
    /// Пока режим запирал и папки, изолированное рабочее место — то самое, под которое режим и
    /// назначен, — оставалось без единственного доступного ему транспорта: файл не клался и не
    /// читался, хотя папка была перечислена в политике.
    /// </summary>
    [Fact]
    public void Isolated_mode_closes_the_network_but_not_the_folders()
    {
        var folder = Path.GetTempPath();

        EgressGate.AttachPolicy(EgressPolicySet.Of(EgressPolicy.Closed with
        {
            Source  = "тест",
            Mode    = EgressMode.Isolated,
            Folders = [folder],
        }));

        var toFolder = EgressGate.CheckFolder(folder, Today);
        Assert.True(toFolder.Allowed);

        var toNetwork = EgressGate.Check(Address("https://api.zavod.local/orders"), Today);
        Assert.False(toNetwork.Allowed);
        Assert.Equal(EgressDenyReason.IsolatedMode, toNetwork.Reason);
    }

    /// <summary>
    /// Рамку папкам ставит список <c>folders</c>, и он остается ЕДИНСТВЕННОЙ их рамкой: режим
    /// папки больше не запирает, поэтому пустой список обязан запирать сам. Иначе «изолированный
    /// режим» стал бы способом писать куда угодно.
    /// </summary>
    [Fact]
    public void Isolated_mode_does_not_open_folders_the_policy_never_named()
    {
        EgressGate.AttachPolicy(EgressPolicySet.Of(EgressPolicy.Closed with
        {
            Source  = "тест",
            Mode    = EgressMode.Isolated,
            Folders = [],
        }));

        var decision = EgressGate.CheckFolder(Path.GetTempPath(), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.FolderNotAllowed, decision.Reason);
    }

    /// <summary>
    /// Истекшая политика запирает и папки: спискам файла, у которого вышел срок, верить больше
    /// нельзя — это тот же вопрос, что и у сетевой двери, и ответ на него общий.
    /// </summary>
    [Fact]
    public void An_expired_policy_closes_the_folders_too()
    {
        EgressGate.AttachPolicy(EgressPolicySet.Of(EgressPolicy.Closed with
        {
            Source     = "тест",
            Mode       = EgressMode.Isolated,
            Folders    = [Path.GetTempPath()],
            ValidUntil = Today.AddDays(-1),
        }));

        var decision = EgressGate.CheckFolder(Path.GetTempPath(), Today);

        Assert.False(decision.Allowed);
        Assert.Equal(EgressDenyReason.PolicyExpired, decision.Reason);
    }
}
