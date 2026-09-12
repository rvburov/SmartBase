using SmartBase.Integrations;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Дверь внутрь: что она разрешает и чего не разрешает никогда
/// (docs/ROADMAP-API.md, 6.1 и 10.12).
///
/// Проверяется именно РЕШЕНИЕ, без единого порта, — ради этого дверь и отделена от слушателя.
/// Три вещи, за которыми тут следят, стоят дороже остальных: состояние по умолчанию закрытое,
/// петлевой адрес политики не спрашивает, а сетевой режим пользователю недоступен.
/// </summary>
public sealed class IngressGateTests : IDisposable
{
    // Политика — статика на процесс; возвращаем самое строгое состояние, чтобы соседний набор
    // не унаследовал разрешение, выданное здесь.
    public void Dispose() => EgressGate.Close();

    /// <summary>Политика, разрешающая или запрещающая слушать адрес машины.</summary>
    private static void Policy(bool listen, DateOnly? until = null) =>
        EgressGate.AttachPolicy(EgressPolicySet.Of(new EgressPolicy(
            EgressMode.Internal, [], [], [], 1000, 1024, 60, until, false, "тест", "", listen)));

    [Fact]
    public void By_default_nothing_is_listening()
    {
        var decision = IngressGate.Check(IngressMode.Off, IngressGate.DefaultPort);

        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.Disabled, decision.Reason);
    }

    /// <summary>
    /// Петлевой адрес разрешен САМ ПО СЕБЕ, без политики организации. Это не послабление:
    /// обратиться к 127.0.0.1 может только программа, уже работающая на этой машине, то есть
    /// та, которая и так читает файл базы.
    /// </summary>
    [Fact]
    public void Loopback_needs_no_policy()
    {
        EgressGate.Close();   // политики нет вовсе — самое строгое состояние исходящей двери

        var decision = IngressGate.Check(IngressMode.Loopback, IngressGate.DefaultPort);

        Assert.True(decision.Allowed);
    }

    /// <summary>
    /// Сетевой режим разрешает организация, а не пользователь: без политики — отказ, и это
    /// состояние по умолчанию на любой машине, куда программу только что поставили.
    /// </summary>
    [Fact]
    public void Network_mode_is_not_the_users_to_switch_on()
    {
        EgressGate.Close();

        var decision = IngressGate.Check(IngressMode.Network, IngressGate.DefaultPort);

        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.NetworkNotAllowed, decision.Reason);
    }

    /// <summary>Политика есть, но слушать сеть она не разрешает — отказ тот же.</summary>
    [Fact]
    public void A_policy_that_says_nothing_about_listening_forbids_it()
    {
        Policy(listen: false);

        Assert.False(IngressGate.Check(IngressMode.Network, IngressGate.DefaultPort).Allowed);
    }

    [Fact]
    public void A_policy_may_allow_listening_on_the_machine_address()
    {
        Policy(listen: true);

        Assert.True(IngressGate.Check(IngressMode.Network, IngressGate.DefaultPort).Allowed);
    }

    /// <summary>Истекшая политика не разрешает ничего — включая прием по сети.</summary>
    [Fact]
    public void An_expired_policy_takes_the_network_mode_back()
    {
        Policy(listen: true, until: DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));

        var decision = IngressGate.Check(IngressMode.Network, IngressGate.DefaultPort);

        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.NetworkNotAllowed, decision.Reason);
    }

    /// <summary>
    /// Разрешить обязан КАЖДЫЙ источник: «хоть один разрешил» означало бы, что более строгий
    /// источник обходится наличием более мягкого.
    /// </summary>
    [Fact]
    public void Every_policy_source_must_allow_listening()
    {
        EgressGate.AttachPolicy(EgressPolicySet.Of(
            new EgressPolicy(EgressMode.Internal, [], [], [], 1, 1, 1, null, false, "первый",  "", true),
            new EgressPolicy(EgressMode.Internal, [], [], [], 1, 1, 1, null, false, "второй", "", false)));

        Assert.False(IngressGate.Check(IngressMode.Network, IngressGate.DefaultPort).Allowed);
    }

    /// <summary>
    /// Слушаем ВСЕ адреса машины, а называем человеку один: плат на машине бывает несколько, и
    /// отправитель, пришедший со второй, иначе не достучался бы, а причину не узнал бы никто.
    /// </summary>
    [Fact]
    public void The_listener_takes_every_address_while_the_person_is_told_one()
    {
        Assert.Equal($"http://+:{IngressGate.DefaultPort}/",
                     IngressGate.Prefix(IngressMode.Network, IngressGate.DefaultPort));

        Assert.Equal($"http://{IngressGate.LoopbackHost}:{IngressGate.DefaultPort}/",
                     IngressGate.Prefix(IngressMode.Loopback, IngressGate.DefaultPort));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void System_and_impossible_ports_are_refused(int port)
    {
        var decision = IngressGate.Check(IngressMode.Loopback, port);

        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.BadPort, decision.Reason);
        Assert.False(IngressGate.ValidPort(port));
    }

    [Fact]
    public void The_address_is_shown_only_when_it_answers()
    {
        Assert.Equal($"http://{IngressGate.LoopbackHost}:{IngressGate.DefaultPort}",
                     IngressGate.Address(IngressMode.Loopback, IngressGate.DefaultPort));

        // Выключенный прием адреса не называет: показать адрес, по которому никто не ответит,
        // значит объяснять отказ молчанием.
        Assert.Equal("", IngressGate.Address(IngressMode.Off, IngressGate.DefaultPort));
        Assert.Equal("", IngressGate.Address(IngressMode.Network, IngressGate.DefaultPort));
    }

    /// <summary>
    /// Потолок тела — тот же, что у файла в папке: транспорта два, а правило одно. Разойдись
    /// они, и сообщение, прошедшее папкой, отказывалось бы проходить по сети.
    /// </summary>
    [Fact]
    public void The_body_cap_is_the_same_as_the_folder_cap()
    {
        Assert.Equal(InboxFolder.MaxFileBytes, IngressGate.MaxBodyBytes);
    }

    /// <summary>Обычная сборка слушателя не лишена — иначе шаг 7 не значил бы ничего.</summary>
    [Fact]
    public void The_ordinary_build_has_a_listener()
    {
        Assert.True(IngressGate.ListenerBuild);
    }
}
