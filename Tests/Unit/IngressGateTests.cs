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
public sealed class IngressGateTests
{
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
    /// Сетевой режим разрешает организация, а не пользователь. Пока в политике нет поля,
    /// которым это разрешают, ответ один — отказ.
    /// </summary>
    [Fact]
    public void Network_mode_is_not_the_users_to_switch_on()
    {
        var decision = IngressGate.Check(IngressMode.Network, IngressGate.DefaultPort);

        Assert.False(decision.Allowed);
        Assert.Equal(IngressDenyReason.NetworkNotAllowed, decision.Reason);
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
