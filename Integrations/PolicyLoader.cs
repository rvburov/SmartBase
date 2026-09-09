namespace SmartBase.Integrations;

/// <summary>
/// Найденный источник политики: откуда взят, что прочитано и нужна ли ему подпись.
/// Чтение с диска и из реестра сюда не входит — сюда приезжает уже прочитанный текст,
/// поэтому весь разбор проверяется тестами без файлов и без реестра.
/// </summary>
/// <param name="Source">Имя источника для экрана и журнала: «реестр HKLM», «папка раздачи».</param>
/// <param name="Json">Текст политики; <c>null</c> — источник существует, но недоступен.</param>
/// <param name="Signature">Подпись из файла рядом; для источников без подписи — <c>null</c>.</param>
/// <param name="SignatureRequired">Обязательна ли подпись: да для мест, куда можно записать
/// без прав администратора (сетевая папка раздачи).</param>
public sealed record PolicyCandidate(
    string  Source,
    string? Json,
    string? Signature = null,
    bool    SignatureRequired = false);

/// <summary>Что вышло с одним источником — строка для экрана «Безопасность».</summary>
/// <param name="Source">Имя источника.</param>
/// <param name="Accepted">Принят ли источник.</param>
/// <param name="Note">Пояснение: «подпись верна» либо причина отказа.</param>
public sealed record PolicyReadResult(string Source, bool Accepted, string Note);

/// <summary>Итог загрузки: действующая политика и судьба каждого источника.</summary>
/// <param name="Set">Набор для <see cref="EgressGate.AttachPolicy"/>.</param>
/// <param name="Reads">По строке на каждый найденный источник, в порядке чтения.</param>
public sealed record PolicyLoadResult(EgressPolicySet Set, IReadOnlyList<PolicyReadResult> Reads);

/// <summary>
/// Сборка действующей политики из найденных источников. Чистая логика: ни файлов, ни реестра,
/// ни сети — их приносит вызывающий (см. docs/developer/features/integrations/overview.md).
///
/// Два правила, которые здесь и живут:
///
/// <list type="number">
///   <item><b>Ничего не нашли — ничего нельзя.</b> Пустой список источников дает
///   <see cref="EgressPolicySet.Closed"/>.</item>
///   <item><b>Источник есть, но негодный — запрет, а не пропуск.</b> Битый файл, несошедшаяся
///   подпись, незнакомый режим — источник входит в набор как <see cref="EgressPolicy.Closed"/>
///   и запирает контур целиком. Просто выбросить его нельзя: разрешено то, что разрешают ВСЕ
///   источники, поэтому выброшенный источник РАСШИРЯЕТ права — и сломать самый строгий файл
///   стало бы способом открыть дверь.</item>
/// </list>
///
/// Недоступный источник (<c>Json = null</c>) — случай третий и не такой же: сетевая папка
/// пропадает штатно, и выключать из-за этого обмен нельзя. Вызывающий обязан подставить
/// последнюю известную копию этого источника; если копии нет, источник запирает контур на
/// общих основаниях — «источник был, а что в нем, мы не знаем» безопасно трактовать только так.
/// </summary>
public static class PolicyLoader
{
    public static PolicyLoadResult Load(IEnumerable<PolicyCandidate> candidates)
    {
        var policies = new List<EgressPolicy>();
        var reads    = new List<PolicyReadResult>();

        foreach (var candidate in candidates)
        {
            var (policy, note) = Read(candidate);

            policies.Add(policy ?? EgressPolicy.Closed with { Source = candidate.Source });
            reads.Add(new PolicyReadResult(candidate.Source, policy is not null, note));
        }

        return new PolicyLoadResult(new EgressPolicySet(policies), reads);
    }

    private static (EgressPolicy? Policy, string Note) Read(PolicyCandidate candidate)
    {
        if (candidate.Json is null)
            return (null, "источник недоступен: действует запрет, пока не прочитается");

        if (candidate.SignatureRequired && !PolicySignature.Verify(candidate.Json, candidate.Signature, out var signatureError))
            return (null, signatureError);

        var policy = PolicyJson.Parse(candidate.Json, candidate.Source, out var parseError);
        if (policy is null)
            return (null, parseError);

        return (policy, candidate.SignatureRequired ? "подпись верна" : "принят");
    }
}
