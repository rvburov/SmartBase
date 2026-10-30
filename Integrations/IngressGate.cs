namespace SmartBase.Integrations;

/// <summary>Где слушать входящие HTTP-обращения.</summary>
public enum IngressMode
{
    /// <summary>Прием выключен.</summary>
    Off = 0,
    /// <summary>Только программы этого компьютера.</summary>
    Loopback = 1,
    /// <summary>Все сетевые адреса компьютера.</summary>
    Network = 2,
}

/// <summary>Почему слушатель не может запуститься.</summary>
public enum IngressDenyReason
{
    None = 0,
    Disabled,
    NoListenerBuild,
    BadPort,
    BadMode,
}

/// <summary>Результат проверки настроек слушателя.</summary>
public sealed record IngressDecision(bool Allowed, IngressDenyReason Reason, string Explanation)
{
    private static readonly IngressDecision Ok = new(true, IngressDenyReason.None, "готово");
    public static IngressDecision Allow() => Ok;
    public static IngressDecision Deny(IngressDenyReason reason, string explanation) =>
        new(false, reason, explanation);
}

/// <summary>Проверка настроек и построение адреса HTTP-слушателя.</summary>
public static class IngressGate
{
    public const int DefaultPort = 8787;
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>Технический предел тела одного HTTP-обращения.</summary>
    public const long MaxBodyBytes = InboxFolder.MaxFileBytes;

    /// <summary>Включен ли слушатель в этот вариант сборки.</summary>
    public static bool ListenerBuild =>
#if NO_LISTENER
        false;
#else
        true;
#endif

    public static IngressDecision Check(IngressMode mode, int port)
    {
        if (!ListenerBuild)
            return IngressDecision.Deny(IngressDenyReason.NoListenerBuild,
                "сборка без слушателя: принимать обращения по сети она не умеет");

        if (mode == IngressMode.Off)
            return IngressDecision.Deny(IngressDenyReason.Disabled, "прием по сети выключен");

        if (mode is not (IngressMode.Loopback or IngressMode.Network))
            return IngressDecision.Deny(IngressDenyReason.BadMode, "неизвестный режим приема по сети");

        if (!ValidPort(port))
            return IngressDecision.Deny(IngressDenyReason.BadPort,
                $"номер порта годится от {MinPort} до {MaxPort}");

        return IngressDecision.Allow();
    }

    public static bool ValidPort(int port) => port is >= MinPort and <= MaxPort;

    /// <summary>Адрес для отправителя; пусто, если слушатель выключен или настройки неверны.</summary>
    public static string Address(IngressMode mode, int port)
    {
        if (!Check(mode, port).Allowed) return "";
        if (mode != IngressMode.Network) return $"http://{LoopbackHost}:{port}";

        var machine = MachineAddress.Local();
        return machine.Length > 0 ? $"http://{machine}:{port}" : $"http://<адрес машины>:{port}";
    }

    /// <summary>Префикс для привязки слушателя, с обязательной косой чертой на конце.</summary>
    public static string Prefix(IngressMode mode, int port) => mode == IngressMode.Network
        ? $"http://+:{port}/"
        : $"http://{LoopbackHost}:{port}/";
}
