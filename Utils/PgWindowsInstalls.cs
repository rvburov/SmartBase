using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SmartBase.Utils;

/// <summary>Установка PostgreSQL на Windows: папка сервера (в ней bin), версия и служба.</summary>
/// <param name="Service">Имя службы Windows; null — служба неизвестна.</param>
public sealed record PgWindowsInstall(string BaseDir, string Version, string? Service);

/// <summary>
/// Установки PostgreSQL на этой машине Windows. Ими пользуются трое: поиск pg_dump для резервной
/// копии (<c>PgDump</c>), проба «сервер установлен» (<see cref="PgServerProbe"/>) и кнопки
/// «Запустить» и «Остановить сервер» (<see cref="PgServerControl"/>).
///
/// Раньше все трое смотрели только в «Program Files\PostgreSQL\&lt;версия&gt;», а сервер ставят и в
/// другие папки — на рабочей станции он стоит в C:\Programs\PostgreSQL\18. Тогда резервная копия
/// базы отвечала «pg_dump не найден», а окно-помощник не предлагало кнопок сервера. Настоящую
/// папку установщик записывает сам — в ключ реестра HKLM\SOFTWARE\PostgreSQL\Installations\&lt;…&gt;
/// со значениями «Base Directory», «Version» и «Service ID». Сервер, которого сделали службой
/// руками (<c>pg_ctl register</c>, без установщика), виден по пути запуска службы. «Program Files»
/// остается последним источником — для установок, не оставивших ни того, ни другого.
///
/// Одна папка — одна установка, сколькими бы путями ее ни нашли; первой идет самая новая версия:
/// при нескольких серверах помощник, как и раньше, берет новейший.
/// </summary>
public static class PgWindowsInstalls
{
    private const string InstallationsKey = @"SOFTWARE\PostgreSQL\Installations";
    private const string ServicesKey      = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Установки PostgreSQL на этой машине, самая новая — первой. Не на Windows — пусто.</summary>
    public static IReadOnlyList<PgWindowsInstall> Find()
    {
        if (!OperatingSystem.IsWindows()) return [];

        // Каждый источник — сам по себе: сломанный (ветка реестра закрыта правами) не должен
        // прятать находки остальных.
        var found = new List<PgWindowsInstall>();
        try { found.AddRange(FromInstaller()); }    catch { /* ветки нет или прочитать не дали */ }
        try { found.AddRange(FromServices()); }     catch { /* то же */ }
        try { found.AddRange(FromProgramFiles()); } catch { /* папка недоступна */ }

        // Запись в реестре переживает удаленную руками папку — такая установка не в счет.
        return Merge(found.Where(i => Directory.Exists(Path.Combine(i.BaseDir, "bin"))));
    }

    [SupportedOSPlatform("windows")]
    private static List<PgWindowsInstall> FromInstaller()
    {
        // Ветка 64-разрядного представления — явно: под 32-разрядным процессом Windows молча
        // подставила бы WOW6432Node, а установщик пишет в 64-разрядную.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root    = machine.OpenSubKey(InstallationsKey);
        var result = new List<PgWindowsInstall>();
        if (root is null) return result;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("Base Directory") is not string { Length: > 0 } dir) continue;
            result.Add(new PgWindowsInstall(dir.TrimEnd('\\', '/'),
                                            key.GetValue("Version") as string ?? "",
                                            key.GetValue("Service ID") is string { Length: > 0 } service ? service : null));
        }
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static List<PgWindowsInstall> FromServices()
    {
        using var machine  = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = machine.OpenSubKey(ServicesKey);
        var result = new List<PgWindowsInstall>();
        if (services is null) return result;

        // Служб в системе сотни, и читать путь каждой незачем: сервер называет службу по себе —
        // «postgresql-x64-18» у установщика, «PostgreSQL» у pg_ctl register.
        foreach (var name in services.GetSubKeyNames())
        {
            if (!name.Contains("postgres", StringComparison.OrdinalIgnoreCase)) continue;
            using var key = services.OpenSubKey(name);
            if (BaseDirOfService(key?.GetValue("ImagePath") as string) is { } dir)
                result.Add(new PgWindowsInstall(dir, VersionOfBinaries(dir), name));
        }
        return result;
    }

    private static List<PgWindowsInstall> FromProgramFiles()
    {
        var result = new List<PgWindowsInstall>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (pf.Length == 0) return result;
        var root = Path.Combine(pf, "PostgreSQL");
        if (!Directory.Exists(root)) return result;

        // Установщик называет папку номером версии, а службу — postgresql-x64-<версия>.
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (Path.GetFileName(dir) is { } version && int.TryParse(version, out _))
                result.Add(new PgWindowsInstall(dir, version, $"postgresql-x64-{version}"));
        return result;
    }

    // Версия по самому серверу: у службы, заведенной руками, другого источника нет. Нет файла
    // или сведений в нем — имя папки (у установщика это и есть номер версии).
    private static string VersionOfBinaries(string dir)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(Path.Combine(dir, "bin", "postgres.exe"));
            if (!string.IsNullOrWhiteSpace(info.ProductVersion)) return info.ProductVersion;
        }
        catch { /* файла нет */ }
        return Path.GetFileName(dir);
    }

    // ── Чистые части — под модульные тесты ──────────────────────────────────

    /// <summary>
    /// Папка сервера по пути запуска службы:
    /// «"C:\…\18\bin\pg_ctl.exe" runservice -N "…" -D "…"» — папка над bin. <c>null</c> — служба
    /// запускает не сервер PostgreSQL. Разделители разбираются вручную, а не через
    /// <see cref="Path"/>: на Linux обратная косая черта — не разделитель, и тесты там считали бы
    /// весь путь одним именем.
    /// </summary>
    internal static string? BaseDirOfService(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var text = imagePath.Trim();

        // В кавычках путь — до закрывающей. Без кавычек пробел в пути от аргумента не отличить,
        // поэтому путь — до «.exe».
        string exe;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            exe = text[1..end];
        }
        else
        {
            var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            exe = text[..(end + ".exe".Length)];
        }

        var parts = exe.Split('\\', '/');
        if (parts.Length < 3 || !parts[^2].Equals("bin", StringComparison.OrdinalIgnoreCase)) return null;
        if (!parts[^1].Equals("pg_ctl.exe", StringComparison.OrdinalIgnoreCase) &&
            !parts[^1].Equals("postgres.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return string.Join('\\', parts[..^2]);
    }

    /// <summary>Номер версии для порядка: «18.6-3» — 18.6.3, «16» — 16.0, непонятное — 0.0.</summary>
    internal static Version VersionKey(string? version)
    {
        var numbers = Regex.Matches(version ?? "", @"\d+")
                           .Select(m => int.TryParse(m.Value, out var n) ? n : 0)
                           .Take(4).ToList();
        while (numbers.Count < 2) numbers.Add(0);
        return numbers.Count switch
        {
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
    }

    /// <summary>
    /// Одна папка — одна установка. Описание берется из самого надежного источника (он идет
    /// первым), а чего в нем нет — версии или службы — из следующих. Самая новая версия — первой.
    /// </summary>
    internal static IReadOnlyList<PgWindowsInstall> Merge(IEnumerable<PgWindowsInstall> found) =>
        [.. found.GroupBy(i => i.BaseDir.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
                 .Select(g => g.First() with
                 {
                     Version = g.Select(i => i.Version).FirstOrDefault(v => v.Length > 0) ?? "",
                     Service = g.Select(i => i.Service).FirstOrDefault(s => s is not null),
                 })
                 .OrderByDescending(i => VersionKey(i.Version))];
}
