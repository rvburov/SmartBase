using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartBase.Utils;

namespace SmartBase.Setup;

/// <summary>Найденная на компьютере копия: папка установки и её версия (null — прочитать не удалось).</summary>
public sealed record Installation(string Dir, string? Version)
{
    /// <summary>Подпись версии для окна: на Linux у бинаря нет ресурса версии, и её не прочитать.</summary>
    public string VersionText => Version is null ? "версия неизвестна" : $"версия {Version}";
}

/// <summary>
/// Поиск установленных копий. Установщик обязан находить их сам, где бы они ни лежали —
/// иначе человек вспоминает путь руками, а забыв его, ставит вторую копию рядом с первой.
///
/// Главный источник — <c>installs.json</c> в папке данных: он один на машину, не зависит от
/// места установки и читается мгновенно. Остальные источники запасные, на случай стёртых
/// данных. Полного обхода диска нет намеренно: на большом томе это минуты, и окно выглядит
/// зависшим.
/// </summary>
public static partial class InstallService
{
    private static string InstallsFile => Path.Combine(DataDir, "installs.json");

    // ── Список установок ─────────────────────────────────────────────────────

    /// <summary>Запомнить папку установки (вызывается после успешной установки/обновления).</summary>
    public static void RegisterInstallPath(string installDir)
    {
        var full = Normalize(installDir);
        if (full is null) return;
        var paths = ReadRegisteredPaths();
        if (!paths.Contains(full, StringComparer.OrdinalIgnoreCase)) paths.Add(full);
        WriteRegisteredPaths(paths);
    }

    /// <summary>Забыть папку установки (вызывается при удалении).</summary>
    public static void UnregisterInstallPath(string installDir)
    {
        var full = Normalize(installDir);
        if (full is null) return;
        WriteRegisteredPaths(ReadRegisteredPaths()
            .Where(p => !string.Equals(p, full, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> ReadRegisteredPaths()
    {
        try
        {
            if (!File.Exists(InstallsFile)) return [];
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(InstallsFile)) ?? [];
        }
        catch { return []; }   // битый файл не должен ломать установщик
    }

    private static void WriteRegisteredPaths(IEnumerable<string> paths)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(InstallsFile,
                JsonSerializer.Serialize(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                                         new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не смогли запомнить — поиск обойдётся запасными источниками */ }
    }

    // ── Поиск ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Все установленные копии на этой машине. Список дедуплицирован, пути проверены на
    /// существование; исчезнувшие заодно вычищаются из installs.json.
    /// </summary>
    public static List<Installation> FindInstallations()
    {
        var candidates = new List<string>(ReadRegisteredPaths()) { CurrentDir, DefaultInstallDir };

        if (IsMac)
        {
            candidates.Add(Path.Combine(HomeDir, "Applications"));
            candidates.Add("/Applications");
            candidates.AddRange(SpotlightDirs());
        }
        else if (OperatingSystem.IsWindows())
        {
            candidates.AddRange(WindowsRegistryDirs());
        }
        else
        {
            candidates.AddRange(LinuxDesktopDirs());
        }

        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<Installation>();
        foreach (var c in candidates)
        {
            if (Normalize(c) is not { } full) continue;
            if (!seen.Add(full)) continue;
            if (!IsAppPresent(full)) continue;
            found.Add(new Installation(full, InstalledVersion(full)));
        }

        // Список чинится сам: пропавшие записи уходят, найденные запасными источниками — добавляются.
        WriteRegisteredPaths(found.Select(f => f.Dir));
        return found;
    }

    /// <summary>Версия установленной копии: macOS — из Info.plist, иначе — из ресурса версии бинаря.</summary>
    public static string? InstalledVersion(string installDir)
    {
        try
        {
            if (IsMac)
            {
                var plist = Path.Combine(InstalledPath(installDir), "Contents", "Info.plist");
                if (!File.Exists(plist)) return null;
                var m = Regex.Match(File.ReadAllText(plist),
                    @"<key>CFBundleShortVersionString</key>\s*<string>([^<]*)</string>");
                return m.Success ? m.Groups[1].Value : null;
            }

            var exe = InstalledPath(installDir);
            if (!File.Exists(exe)) return null;
            // ProductVersion у сборок .NET несёт суффикс «+хеш-коммита» — он тут лишний.
            // На Linux ресурса версии нет вовсе, вернётся null: окно скажет «версия неизвестна».
            var v = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
            if (string.IsNullOrWhiteSpace(v)) return null;
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
        catch { return null; }
    }

    // ── Запасные источники по ОС ─────────────────────────────────────────────

    // macOS: Spotlight знает все бандлы по идентификатору. Оба наших бандла (приложение и
    // установщик) несут один CFBundleIdentifier, поэтому берём только SmartBase.app.
    private static IEnumerable<string> SpotlightDirs()
    {
        var outp = RunCapture("/usr/bin/mdfind",
                              $"kMDItemCFBundleIdentifier == '{Autostart.BundleId}'", 3000);
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.EndsWith(Path.DirectorySeparatorChar + AppBundleName, StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(line) is { Length: > 0 } dir
                && !LooksLikeNotInstalled(dir))
                yield return dir;
    }

    // Spotlight находит бандл везде, где он лежит на диске, — включая места, которые установкой
    // не являются. Проверено на живой машине: выдача содержала dist/Version 1.0.3/osx-arm64 из двух
    // рабочих копий репозитория. Показать их как «установленные копии» значило бы предложить
    // «удалить» результат сборки. Смонтированный образ и Корзина — из той же серии.
    private static bool LooksLikeNotInstalled(string dir)
    {
        if (dir.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase)) return true;   // подключённый .dmg
        foreach (var part in dir.Split(Path.DirectorySeparatorChar))
            if (part.Equals("dist", StringComparison.OrdinalIgnoreCase)          // вывод сборки проекта
                || part.Equals(".Trash", StringComparison.OrdinalIgnoreCase))    // Корзина
                return true;
        return false;
    }

    // Windows: путь лежит в той же записи «Параметры → Приложения», которую пишет RegisterUninstall.
    // Через PowerShell, чтобы не тянуть пакет реестра — как и запись.
    private static IEnumerable<string> WindowsRegistryDirs()
    {
        var outp = RunCapture("powershell",
            $"-NoProfile -NonInteractive -Command \"(Get-ItemProperty -Path '{UninstallKey}' " +
            "-ErrorAction SilentlyContinue).InstallLocation\"", 5000);
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return line;
    }

    // Linux: путь к бинарю записан в поле Exec ярлыка меню приложений.
    private static IEnumerable<string> LinuxDesktopDirs()
    {
        var file = Path.Combine(AppsDir, $"{AppSettings.AppName}.desktop");
        if (!File.Exists(file)) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(file); } catch { yield break; }

        foreach (var line in lines)
        {
            if (!line.StartsWith("Exec=", StringComparison.Ordinal)) continue;
            // Exec="/путь/к/SmartBase" --background → нужен только путь до первого пробела вне кавычек.
            var value = line[5..].Trim();
            var exe   = value.StartsWith('"')
                ? value[1..(value.IndexOf('"', 1) is var q && q > 0 ? q : value.Length)]
                : value.Split(' ')[0];
            if (Path.GetDirectoryName(exe.Replace("\\", "")) is { Length: > 0 } dir) yield return dir;
        }
    }

    // ── Мелочи ───────────────────────────────────────────────────────────────

    private static string? Normalize(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            return Path.GetFullPath(dir.Trim())
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }
    }

    // Запуск с чтением stdout. stderr не перенаправляем намеренно: читать оба потока синхронно
    // нельзя (взаимная блокировка при заполнении буфера), а ошибки нам тут не нужны.
    private static string RunCapture(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
            if (p is null) return "";
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return ""; }
            return text;
        }
        catch { return ""; }   // нет утилиты / запрещено политикой — просто нет этого источника
    }
}
