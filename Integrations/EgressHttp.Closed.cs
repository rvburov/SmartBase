namespace SmartBase.Integrations;

/// <summary>
/// Отправляющий слой сборки для закрытого контура — заглушка вместо сетевого клиента.
///
/// Этот файл компилируется ТОЛЬКО при <c>-p:ClosedLoop=true</c>, и вместо него в обычной сборке
/// берется <c>EgressHttp.Open.cs</c> (подмена — в SmartBase.csproj). Смысл подмены в том, что
/// утверждение «программа ничего никуда не отправляет» подтверждается отсутствием сетевого кода
/// в дистрибутиве, а не чтением настроек на каждой машине: в закрытой сборке нет ни сетевого
/// клиента, ни сокета, ни разрешения имен — проверяющему нечего искать.
///
/// Подмена сделана файлом, а не <c>#if</c>, намеренно: условная компиляция живет ровно в одном
/// месте — в шлюзе (<c>Tests/Unit/OneDoorOutwardTests.cs</c> следит за этим), — иначе две сборки
/// начнут расходиться поведением в местах, о которых никто не помнит.
///
/// Все остальное — интерфейс, автоматизация, папка-приемник, расширения — в закрытой сборке
/// работает без изъятий (docs/ROADMAP-API.md, 10.16.1).
/// </summary>
public static class EgressHttp
{
    /// <summary>Потолок ответа. Здесь только ради одинакового вида обеих сборок.</summary>
    public const long MaxResponseBytes = 32L * 1024 * 1024;

    /// <inheritdoc cref="SendAsync(EgressPlan, CancellationToken)"/>
    public static Task<EgressResponse> SendAsync(
        IntegrationConnection connection,
        IntegrationRequest    request,
        string                secret,
        CancellationToken     ct = default)
    {
        _ = connection; _ = request; _ = secret; _ = ct;
        return Task.FromResult(Refusal());
    }

    /// <inheritdoc cref="SendAsync(EgressPlan, CancellationToken)"/>
    public static Task<EgressResponse> ProbeAsync(
        IntegrationConnection connection,
        string                secret,
        CancellationToken     ct = default)
    {
        _ = connection; _ = secret; _ = ct;
        return Task.FromResult(Refusal());
    }

    /// <summary>
    /// Отказ вместо отправки. Тот же отказ дает и сам шлюз в этой сборке — так что порядок
    /// проверок для вызывающего кода не меняется, меняется только то, что за отказом ничего нет.
    /// </summary>
    public static Task<EgressResponse> SendAsync(
        EgressPlan plan, CancellationToken ct = default, long maxResponseBytes = MaxResponseBytes)
    {
        _ = ct; _ = maxResponseBytes;
        return Task.FromResult(Refusal(plan.SafeAddress));
    }

    private static EgressResponse Refusal(string address = "") =>
        EgressResponse.Denied(EgressDenyReason.ClosedLoopBuild,
            "сборка для закрытого контура: сетевого клиента в ней нет", address);
}
