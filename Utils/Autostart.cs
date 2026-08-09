using System.Runtime.Versioning;
using SmartBase.Setup;

namespace SmartBase.Utils;

/// <summary>
/// Автозапуск приложения при входе в систему. Пер-ОС запись, ссылающаяся на текущий
/// установленный бинарь/бандл; при включённом фоновом режиме добавляет <c>--background</c>
/// (тихий старт, см. Program/App):
///   • Windows — ключ реестра HKCU\…\Run\SmartBase;
///   • macOS   — LaunchAgent ~/Library/LaunchAgents/com.romanburov.smartbase.plist;
///   • Linux   — ~/.config/autostart/smartbase.desktop.
///
/// Построение записи (путь + содержимое) отделено от записи на диск — чистые методы
/// <c>Build*</c> покрываются юнит-тестом. Вне установленного приложения (dev <c>dotnet run</c>,
/// на macOS — нет .app-бандла) <see cref="IsSupported"/> = false и всё — no-op.
/// </summary>
public static class Autostart
{
    public const string BackgroundArg = "--background";
    internal const string BundleId      = "com.romanburov.smartbase";
    private const string WindowsRunKey  = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValue   = AppSettings.AppName;   // "SmartBase"

    /// <summary>Путь, который нужно запускать при входе в систему (внутренний бинарь .app на
    /// macOS, exe на Windows/Linux), или null — если приложение не установлено (dev-режим).</summary>
    public static string? LaunchTarget()
    {
        if (InstallService.IsMac)
        {
            var bundle = InstallService.BundleRoot(Environment.ProcessPath);
            return bundle is null ? null : InstallService.MacInnerBinary(bundle);
        }
        var exe = Environment.ProcessPath;
        return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
    }

    /// <summary>Доступен ли автозапуск (установленное приложение). В dev-режиме — false.</summary>
    public static bool IsSupported => LaunchTarget() is not null;

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsEnabled();
            if (OperatingSystem.IsMacOS())   return File.Exists(MacPlistPath);
            if (OperatingSystem.IsLinux())   return File.Exists(LinuxDesktopPath);
        }
        catch { /* нет прав/доступа — считаем выключенным */ }
        return false;
    }

    /// <summary>Включить/выключить автозапуск. <paramref name="background"/> — стартовать в фоне
    /// (добавляет <see cref="BackgroundArg"/> в команду). Вне установки — no-op.</summary>
    public static void Set(bool enabled, bool background)
    {
        var target = LaunchTarget();
        if (target is null) return;
        try
        {
            if (enabled) Write(target, background);
            else         Remove();
        }
        catch { /* сбой записи автозапуска не критичен для работы приложения */ }
    }

    // ── Запись/удаление ────────────────────────────────────────────────────────

    private static void Write(string target, bool background)
    {
        if (OperatingSystem.IsWindows())
            WindowsWrite(BuildWindowsCommand(target, background));
        else if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
            File.WriteAllText(MacPlistPath, BuildMacPlist(target, background));
        }
        else if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopPath)!);
            File.WriteAllText(LinuxDesktopPath, BuildLinuxDesktop(target, background));
        }
    }

    /// <summary>
    /// Снять автозапуск безусловно — для удаления программы. В отличие от <see cref="Set"/>
    /// не смотрит на <see cref="LaunchTarget"/>: при удалении бинарь может быть уже недоступен,
    /// а запись автозапуска убрать всё равно надо, иначе система будет пытаться стартовать
    /// снесённое приложение.
    /// </summary>
    public static void Disable()
    {
        try { Remove(); } catch { /* нечего снимать или нет доступа */ }
    }

    private static void Remove()
    {
        if (OperatingSystem.IsWindows()) WindowsRemove();
        else if (OperatingSystem.IsMacOS()   && File.Exists(MacPlistPath))    File.Delete(MacPlistPath);
        else if (OperatingSystem.IsLinux()   && File.Exists(LinuxDesktopPath)) File.Delete(LinuxDesktopPath);
    }

    // ── Чистое построение содержимого (тестируется) ─────────────────────────────

    /// <summary>Значение реестра HKCU\…\Run: команда запуска exe (+ --background).</summary>
    public static string BuildWindowsCommand(string exe, bool background) =>
        background ? $"\"{exe}\" {BackgroundArg}" : $"\"{exe}\"";

    /// <summary>LaunchAgent-plist для macOS: RunAtLoad + ProgramArguments (бинарь [+--background]).</summary>
    public static string BuildMacPlist(string innerBinary, bool background)
    {
        var args = background
            ? $"    <string>{innerBinary}</string>\n    <string>{BackgroundArg}</string>"
            : $"    <string>{innerBinary}</string>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
               "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
               "<plist version=\"1.0\">\n<dict>\n" +
               $"  <key>Label</key>\n  <string>{BundleId}</string>\n" +
               "  <key>ProgramArguments</key>\n  <array>\n" + args + "\n  </array>\n" +
               "  <key>RunAtLoad</key>\n  <true/>\n" +
               "</dict>\n</plist>\n";
    }

    /// <summary>autostart .desktop для Linux: Exec = бинарь (+--background), автозапуск включён.</summary>
    public static string BuildLinuxDesktop(string exec, bool background)
    {
        var cmd = background ? $"\"{exec}\" {BackgroundArg}" : $"\"{exec}\"";
        return "[Desktop Entry]\n" +
               "Type=Application\n" +
               $"Name={AppSettings.DisplayName}\n" +
               $"Exec={cmd}\n" +
               "Terminal=false\n" +
               "X-GNOME-Autostart-enabled=true\n";
    }

    // ── Пути ────────────────────────────────────────────────────────────────────

    private static string MacPlistPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "Library", "LaunchAgents", $"{BundleId}.plist");

    private static string LinuxDesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".config", "autostart", "smartbase.desktop");

    // ── Windows реестр (изолировано под платформу) ──────────────────────────────

    [SupportedOSPlatform("windows")]
    private static bool WindowsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsRunKey);
        return key?.GetValue(WindowsValue) is not null;
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsWrite(string command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(WindowsRunKey);
        key.SetValue(WindowsValue, command);
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsRemove()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true);
        if (key?.GetValue(WindowsValue) is not null) key.DeleteValue(WindowsValue, throwOnMissingValue: false);
    }
}
