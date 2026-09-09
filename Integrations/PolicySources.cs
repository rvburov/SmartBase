using Microsoft.Win32;
using SmartBase.Utils;

namespace SmartBase.Integrations;

/// <summary>
/// Откуда берется политика организации: защищенное правами операционной системы место
/// (реестр на Windows, <c>/etc</c> на остальных), папка установки и сетевая папка раздачи.
/// Здесь и только здесь подсистема интеграций трогает диск и реестр — разбор, проверка
/// подписи и сборка набора живут отдельно и тестируются без ввода-вывода
/// (<see cref="PolicyJson"/>, <see cref="PolicySignature"/>, <see cref="PolicyLoader"/>).
///
/// Правило про пропажу источника. Сетевая папка отваливается штатно — это будни, а не
/// происшествие. Поэтому различаются два разных «файла нет»:
///
/// <list type="bullet">
///   <item><b>Папка на месте, файла в ней нет</b> — источник отменили. Снимок забывается,
///   источник исчезает. Иначе снятую политику нельзя было бы снять никогда.</item>
///   <item><b>Папки не видно</b> — источник цел, но недоступен. Действует последняя известная
///   копия. Ограничения не смягчаются от того, что выключили сервер.</item>
/// </list>
///
/// Снимок хранится сырым текстом в <c>app.db</c> (<see cref="AppSettings.LoadPolicySnapshot"/>),
/// вместе с подписью: источник, которому подпись обязательна, обязан пройти проверку и из
/// снимка тоже.
/// </summary>
/// <summary>Место, где программа ищет политику, — строка экрана «Безопасность».</summary>
/// <param name="Name">Имя источника, то же, что в отчете о чтении.</param>
/// <param name="Where">Путь или ветка реестра.</param>
/// <param name="SignatureRequired">Обязательна ли здесь подпись.</param>
public sealed record PolicyPlace(string Name, string Where, bool SignatureRequired);

public static class PolicySources
{
    /// <summary>Имя файла политики во всех папках — одно.</summary>
    public const string FileName = "policy.json";

    /// <summary>Подпись лежит рядом с файлом: <c>policy.json.sig</c>.</summary>
    public const string SignatureExtension = ".sig";

    /// <summary>Ветка реестра с политикой; пишется администратором и раздается через GPO.</summary>
    public const string RegistrySubKey = @"SOFTWARE\SmartBase\Policy";

    /// <summary>Значение с текстом политики прямо в реестре.</summary>
    public const string RegistryJsonValue = "PolicyJson";

    /// <summary>Значение с путем к файлу политики — когда держать текст в реестре неудобно.</summary>
    public const string RegistryPathValue = "PolicyPath";

    /// <summary>Папка политики на macOS и Linux; права на нее — у root, как у ветки реестра.</summary>
    public const string SystemFolderUnix = "/etc/smartbase";

    // Ключи снимков в app.db. Меняются вместе с приставкой в AppSettings.
    private const string SnapshotSystem  = "system";
    private const string SnapshotInstall = "install";
    private const string SnapshotShare   = "share";

    // Имена источников — их видит человек на экране «Безопасность» и в журнале.
    private const string NameRegistry = "реестр HKLM";
    private const string NameUnix     = "/etc/smartbase";
    private const string NameInstall  = "папка установки";
    private const string NameShare    = "папка раздачи";

    /// <summary>Пометка к имени источника, когда он прочитан не с места, а из снимка.</summary>
    public const string LastKnownSuffix = " (последняя известная)";

    /// <summary>
    /// Где программа ищет политику — для экрана «Безопасность». Место в списке есть всегда,
    /// даже если файла там нет: администратору нужно знать, куда положить, а не гадать.
    /// </summary>
    public static IReadOnlyList<PolicyPlace> Places() =>
    [
        OperatingSystem.IsWindows()
            ? new PolicyPlace(NameRegistry, $@"HKLM\{RegistrySubKey}", false)
            : new PolicyPlace(NameUnix, Path.Combine(SystemFolderUnix, FileName), false),

        new(NameInstall, Path.Combine(AppContext.BaseDirectory, FileName), false),

        // Папке раздачи подпись обязательна: в нее пишут без прав администратора.
        new(NameShare, string.IsNullOrWhiteSpace(AppSettings.LoadUpdatePath())
                ? "не задана в настройках обновления"
                : Path.Combine(AppSettings.LoadUpdatePath(), FileName),
            true),
    ];

    /// <summary>
    /// Собрать источники так, как это делает работающая программа: системное место, папка
    /// установки и папка раздачи из настроек обновления.
    /// </summary>
    public static IReadOnlyList<PolicyCandidate> Collect() =>
        Collect(AppContext.BaseDirectory, AppSettings.LoadUpdatePath());

    /// <summary>
    /// То же, но папки передаются явно — так источники проверяются тестами на временных
    /// папках, без установленной программы и без сетевого диска.
    /// </summary>
    public static IReadOnlyList<PolicyCandidate> Collect(string? installFolder, string? shareFolder)
    {
        var candidates = new List<PolicyCandidate>();

        if (FromSystem() is { } system) candidates.Add(system);
        if (FromFolder(NameInstall, SnapshotInstall, installFolder, signatureRequired: false) is { } install)
            candidates.Add(install);
        // Папке раздачи подпись обязательна: в нее пишут без прав администратора, и без
        // подписи любой, кто может туда писать, переписал бы белый список.
        if (FromFolder(NameShare, SnapshotShare, shareFolder, signatureRequired: true) is { } share)
            candidates.Add(share);

        return candidates;
    }

    // ── Системное место ───────────────────────────────────────────────────────

    /// <summary>
    /// Политика из места, которое защищает сама операционная система: ветка <c>HKLM</c> на
    /// Windows, папка <c>/etc/smartbase</c> на остальных. Подпись здесь не нужна — записать
    /// туда без прав администратора нельзя.
    /// </summary>
    private static PolicyCandidate? FromSystem() =>
        OperatingSystem.IsWindows()
            ? FromRegistry()
            : FromFolder(NameUnix, SnapshotSystem, SystemFolderUnix, signatureRequired: false);

    private static PolicyCandidate? FromRegistry()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            // Ветка 64-разрядного представления берется явно: под 32-разрядным процессом
            // Windows молча подставляет WOW6432Node, и политика, разложенная групповой
            // политикой, просто не нашлась бы.
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key     = machine.OpenSubKey(RegistrySubKey);

            if (key is null) return Vanished(NameRegistry, SnapshotSystem);

            if (key.GetValue(RegistryJsonValue) is string json && !string.IsNullOrWhiteSpace(json))
                return Remember(NameRegistry, SnapshotSystem, json, null, signatureRequired: false);

            if (key.GetValue(RegistryPathValue) is string path && !string.IsNullOrWhiteSpace(path))
                return FromFile(NameRegistry, SnapshotSystem, path, signatureRequired: false);

            return Vanished(NameRegistry, SnapshotSystem);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Ветка есть, а прочитать не дали. Это НЕ «политики нет»: источник существует,
            // и трактовать его отсутствием значило бы расширить права.
            return Unreadable(NameRegistry, SnapshotSystem);
        }
    }

    // ── Папки ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Политика из папки. <c>null</c> — источника нет: папка не задана и снимка от нее не
    /// осталось, либо папка на месте, а файла в ней уже нет.
    /// </summary>
    public static PolicyCandidate? FromFolder(string name, string snapshotKey, string? folder, bool signatureRequired)
    {
        // Папку могли и не задать (путь обновления пуст) — но если снимок от нее остался,
        // источник считается недоступным, а не отмененным: очистка пути в настройках не
        // должна снимать ограничения организации.
        if (string.IsNullOrWhiteSpace(folder))
            return Snapshot(name, snapshotKey, signatureRequired) ?? null;

        try
        {
            if (!Directory.Exists(folder))
                return Snapshot(name, snapshotKey, signatureRequired);   // папки не видно — берем последнюю известную

            return FromFile(name, snapshotKey, Path.Combine(folder, FileName), signatureRequired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(name, snapshotKey);
        }
    }

    private static PolicyCandidate? FromFile(string name, string snapshotKey, string path, bool signatureRequired)
    {
        try
        {
            if (!File.Exists(path)) return Vanished(name, snapshotKey);

            var json      = File.ReadAllText(path);
            var signature = ReadSignature(path);

            return Remember(name, snapshotKey, json, signature, signatureRequired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable(name, snapshotKey);
        }
    }

    private static string? ReadSignature(string policyPath)
    {
        var path = policyPath + SignatureExtension;
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ── Снимок последней известной политики ───────────────────────────────────

    /// <summary>Источник прочитан: запоминаем текст и подпись, отдаем кандидата.</summary>
    private static PolicyCandidate Remember(
        string name, string snapshotKey, string json, string? signature, bool signatureRequired)
    {
        AppSettings.SavePolicySnapshot(snapshotKey, json);
        AppSettings.SavePolicySnapshot(snapshotKey + SignatureExtension, signature ?? "");

        return new PolicyCandidate(name, json, signature, signatureRequired);
    }

    /// <summary>
    /// Источник отменили: место доступно, файла или ветки в нем больше нет. Снимок
    /// забывается, источник исчезает из набора.
    /// </summary>
    private static PolicyCandidate? Vanished(string name, string snapshotKey)
    {
        _ = name;
        AppSettings.ForgetPolicySnapshot(snapshotKey);
        AppSettings.ForgetPolicySnapshot(snapshotKey + SignatureExtension);
        return null;
    }

    /// <summary>
    /// Источник есть, но не читается. Снимка нет — отдаем кандидата без текста: загрузчик
    /// запрет контур, и это верно. «Источник был, а что в нем, мы не знаем» безопасно
    /// трактовать только так.
    /// </summary>
    private static PolicyCandidate Unreadable(string name, string snapshotKey) =>
        Snapshot(name, snapshotKey, signatureRequired: false) ?? new PolicyCandidate(name, null);

    /// <summary>Последняя известная копия источника; <c>null</c> — копии нет.</summary>
    private static PolicyCandidate? Snapshot(string name, string snapshotKey, bool signatureRequired)
    {
        var json = AppSettings.LoadPolicySnapshot(snapshotKey);
        if (string.IsNullOrWhiteSpace(json)) return null;

        var signature = AppSettings.LoadPolicySnapshot(snapshotKey + SignatureExtension);

        return new PolicyCandidate(
            name + LastKnownSuffix,
            json,
            string.IsNullOrWhiteSpace(signature) ? null : signature,
            signatureRequired);
    }
}
