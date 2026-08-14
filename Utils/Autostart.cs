using System.Runtime.Versioning;
using Serilog;
using SmartBase.Setup;

namespace SmartBase.Utils;

/// <summary>
/// Автозапуск приложения при входе в систему. Пер-ОС запись, ссылающаяся на текущий
/// установленный бинарь/бандл:
///   • Windows — ключ реестра HKCU\…\Run\SmartBase;
///   • macOS   — LaunchAgent ~/Library/LaunchAgents/com.romanburov.smartbase.plist;
///   • Linux   — ~/.config/autostart/smartbase.desktop.
///
/// Запись НЕ несёт аргументов: автозапуск всегда открывает окно — что с фоновым режимом,
/// что без него. Фоновый режим отвечает только за «крестик» окна (скрыть вместо выхода)
/// и значок в трее, но не за то, как приложение стартует при входе в систему.
///
/// Построение и разбор записи отделены от работы с диском — чистые <c>Build*</c>,
/// <c>Parse*</c> и <see cref="DecideSync"/> покрываются юнит-тестом. Вне установленного
/// приложения (dev <c>dotnet run</c>, на macOS — нет .app-бандла) <see cref="IsSupported"/>
/// = false и всё — no-op.
///
/// Источник правды о состоянии — САМА ОС, а не настройка в app.db: запись могут снести
/// мимо приложения (переустановка, чистилка автозагрузки, правка вручную), и тогда тумблер
/// в настройках показывал бы несуществующий автозапуск. Настройка и запись сверяются на
/// каждом старте — <see cref="Sync"/>.
/// </summary>
public static class Autostart
{
    internal const string BundleId      = "com.romanburov.smartbase";
    private const string WindowsRunKey  = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValue   = AppSettings.AppName;   // "SmartBase"
    // Сюда Диспетчер задач кладёт свой «выключено» для записей автозагрузки — см. DisabledInSystemUi.
    private const string WindowsApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Что сделать с записью ОС, чтобы она отвечала настройке (см. <see cref="DecideSync"/>).</summary>
    public enum SyncAction { None, Write, Remove }

    /// <summary>
    /// Путь, который нужно запускать при входе в систему (внутренний бинарь .app на macOS,
    /// exe на Windows/Linux), или null — если распоряжаться автозапуском нам не положено.
    ///
    /// Автозапуском заведует ТОЛЬКО установленная копия. Настройки лежат в общей папке данных
    /// (`app.db` один на пользователя), поэтому dev-сборка или копия программы, запущенная из
    /// другой папки, иначе перетягивала бы запись автозапуска на себя — и при входе в систему
    /// поднималась бы она, а не установленная программа.
    /// </summary>
    public static string? LaunchTarget()
    {
#if DEBUG
        return null;   // dev-сборка (dotnet run) — не установленное приложение
#else
        // Портативная/скопированная куда-то копия: installs.json знает настоящие папки установки.
        // Список пуст (установка до 1.0.5, стёртые данные) — не запрещаем, работаем как раньше.
        if (InstallService.IsRunningInstalledCopy() == false) return null;

        if (InstallService.IsMac)
        {
            var bundle = InstallService.BundleRoot(Environment.ProcessPath);
            return bundle is null ? null : InstallService.MacInnerBinary(bundle);
        }
        var exe = Environment.ProcessPath;
        return !string.IsNullOrEmpty(exe) && File.Exists(exe) ? exe : null;
#endif
    }

    /// <summary>Доступен ли автозапуск (установленное приложение). В dev-режиме — false.</summary>
    public static bool IsSupported => LaunchTarget() is not null;

    /// <summary>Стоит ли запись автозапуска в системе прямо сейчас (не то, что записано в настройках).</summary>
    public static bool IsEnabled() => Record() is not null;

    /// <summary>
    /// Windows: запись автозапуска на месте, но пользователь выключил её в Диспетчере задач
    /// (вкладка «Автозагрузка»). Тогда программа при входе в систему не стартует, а по реестру
    /// этого не видно — отсюда «включено, но не работает». Только показываем: отменять решение
    /// человека приложение не должно. На других ОС и при любой неясности — false.
    /// </summary>
    public static bool DisabledInSystemUi()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return IsDisabledFlag(WindowsApprovedFlag());
        }
        catch { /* нет ключа/прав — считаем, что не отключено */ }
        return false;
    }

    /// <summary>
    /// Разбор флага из <c>StartupApproved\Run</c>: 12 байт, значимый — первый. Чётный (0x02, 0x06)
    /// — запись включена, нечётный (0x03, 0x07, встречается и 0x01) — выключена пользователем.
    /// Данных нет или они короче — не выдумываем, считаем включённой.
    /// </summary>
    public static bool IsDisabledFlag(byte[]? data) => data is { Length: > 0 } && (data[0] & 1) == 1;

    /// <summary>Содержимое записи автозапуска глазами ОС: значение реестра / plist / .desktop.
    /// null — записи нет либо её не прочитать (нет прав).</summary>
    public static string? Record()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsRecord();
            if (OperatingSystem.IsMacOS())   return File.Exists(MacPlistPath)     ? File.ReadAllText(MacPlistPath)     : null;
            if (OperatingSystem.IsLinux())   return File.Exists(LinuxDesktopPath) ? File.ReadAllText(LinuxDesktopPath) : null;
        }
        catch { /* нет прав/доступа — считаем, что записи нет */ }
        return null;
    }

    /// <summary>
    /// Включить/выключить автозапуск. Возвращает <c>false</c>, если ОС запись не приняла:
    /// нет прав, политика домена, откат антивирусом, приложение не установлено. Раньше сбой
    /// проглатывался молча — тумблер оставался включённым, а автозапуска не было и следов в
    /// логе тоже. Поэтому: пишем, СВЕРЯЕМ факт и докладываем вызывающему.
    /// </summary>
    public static bool Set(bool enabled)
    {
        var target = LaunchTarget();
        if (target is null)
        {
            Log.Warning("Автозапуск: приложение не установлено (dev-режим) — запись не меняется");
            return false;
        }
        try
        {
            if (enabled) Write(target);
            else         Remove();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Автозапуск: не удалось {Action:l} запись", enabled ? "создать" : "снять");
            return false;
        }

        // Исключения мало: запись могла не появиться и без ошибки (политика, откат антивирусом).
        if (IsEnabled() == enabled) return true;
        Log.Warning("Автозапуск: система не приняла запись (просили {Enabled}, в системе её {State:l})",
                    enabled, enabled ? "нет" : "по-прежнему есть");
        return false;
    }

    /// <summary>
    /// Самолечение при старте: привести запись ОС в соответствие с настройкой — вернуть
    /// пропавшую (переустановка, чистилка автозагрузки), переписать устаревшую (сменилась
    /// папка установки, остались аргументы прежних версий), убрать лишнюю. Чужую копию
    /// не трогаем: у неё своя запись, и перехватывать автозапуск на себя приложение не должно.
    /// </summary>
    public static void Sync(bool enabled)
    {
        var target = LaunchTarget();
        if (target is null) return;

        var record         = Record();
        var recordedTarget = record is null ? null : ParseTarget(record);
        var exists         = recordedTarget is not null && File.Exists(recordedTarget);

        switch (DecideSync(enabled, record, recordedTarget, exists, target, Expected(target)))
        {
            case SyncAction.Write:
                if (Set(true))
                    Log.Information("Автозапуск: запись приведена в порядок — {Target:l}", target);
                break;
            case SyncAction.Remove:
                if (Set(false))
                    Log.Information("Автозапуск: лишняя запись снята (в настройках выключен)");
                break;
        }
    }

    /// <summary>
    /// Чистое решение для <see cref="Sync"/>: что сделать с записью ОС. Вынесено отдельно,
    /// потому что вся соль здесь, а работа с реестром/файлами в тесте недоступна.
    /// </summary>
    /// <param name="enabled">настройка «Автозапуск» из app.db</param>
    /// <param name="record">содержимое записи ОС (null — записи нет)</param>
    /// <param name="recordedTarget">путь к бинарю, вынутый из записи</param>
    /// <param name="recordedTargetExists">этот бинарь ещё на месте</param>
    /// <param name="currentTarget">бинарь, из которого мы сейчас запущены</param>
    /// <param name="expected">каким содержимое записи должно быть для <paramref name="currentTarget"/></param>
    public static SyncAction DecideSync(bool enabled, string? record, string? recordedTarget,
                                        bool recordedTargetExists, string currentTarget, string expected)
    {
        var ours = SamePath(recordedTarget, currentTarget);

        // Выключен: снимаем только СВОЮ запись. Чужая (другая установка, портативная копия)
        // живёт по своим настройкам — не нам её трогать.
        if (!enabled) return record is not null && ours ? SyncAction.Remove : SyncAction.None;

        if (record is null)            return SyncAction.Write;   // запись пропала — вернуть
        if (!recordedTargetExists)     return SyncAction.Write;   // указывает на снесённый бинарь
        if (!ours)                     return SyncAction.None;    // работает чужая копия — пусть
        return record == expected ? SyncAction.None : SyncAction.Write;   // наш путь, но команда устарела
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), cmp); }
        catch { return false; }
    }

    // Каким содержимое записи должно быть для указанного бинаря — обратная сторона Parse*.
    private static string Expected(string target) =>
        OperatingSystem.IsWindows() ? BuildWindowsCommand(target)
      : OperatingSystem.IsMacOS()   ? BuildMacPlist(target)
      :                               BuildLinuxDesktop(target);

    private static string? ParseTarget(string record) =>
        OperatingSystem.IsWindows() ? ParseWindowsTarget(record)
      : OperatingSystem.IsMacOS()   ? ParseMacTarget(record)
      :                               ParseLinuxDesktopTarget(record);

    // ── Запись/удаление ────────────────────────────────────────────────────────

    private static void Write(string target)
    {
        if (OperatingSystem.IsWindows())
            WindowsWrite(BuildWindowsCommand(target));
        else if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacPlistPath)!);
            File.WriteAllText(MacPlistPath, BuildMacPlist(target));
        }
        else if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopPath)!);
            File.WriteAllText(LinuxDesktopPath, BuildLinuxDesktop(target));
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

    /// <summary>Значение реестра HKCU\…\Run: команда запуска exe (без аргументов).</summary>
    public static string BuildWindowsCommand(string exe) => $"\"{exe}\"";

    /// <summary>LaunchAgent-plist для macOS: RunAtLoad + ProgramArguments (только бинарь).</summary>
    public static string BuildMacPlist(string innerBinary) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n<dict>\n" +
        $"  <key>Label</key>\n  <string>{BundleId}</string>\n" +
        "  <key>ProgramArguments</key>\n  <array>\n" +
        $"    <string>{innerBinary}</string>\n  </array>\n" +
        "  <key>RunAtLoad</key>\n  <true/>\n" +
        "</dict>\n</plist>\n";

    // ── Чистый разбор записи (тестируется) ─────────────────────────────────────
    //
    // Обратная сторона Build*: достаём путь к бинарю из того, что реально лежит в системе.
    // Нужно, чтобы отличить свою запись от чужой и заметить устаревшую (старый путь или
    // аргументы прежних версий). Записи старше 1.0.5 несли ещё и --background, поэтому
    // разбор обязан переживать любые аргументы после пути.

    /// <summary>Путь из значения реестра: <c>"C:\App\SmartBase.exe" [аргументы]</c>.</summary>
    public static string? ParseWindowsTarget(string command) => FirstToken(command);

    /// <summary>Путь из LaunchAgent-plist: первый <c>&lt;string&gt;</c> в ProgramArguments.</summary>
    public static string? ParseMacTarget(string plist)
    {
        var args = plist.IndexOf("<key>ProgramArguments</key>", StringComparison.Ordinal);
        if (args < 0) return null;
        var open = plist.IndexOf("<string>", args, StringComparison.Ordinal);
        if (open < 0) return null;
        open += "<string>".Length;
        var close = plist.IndexOf("</string>", open, StringComparison.Ordinal);
        return close > open ? plist[open..close].Trim() is { Length: > 0 } v ? v : null : null;
    }

    /// <summary>Путь из .desktop: значение поля <c>Exec</c> до первого пробела вне кавычек.</summary>
    public static string? ParseLinuxDesktopTarget(string desktop)
    {
        foreach (var raw in desktop.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Exec=", StringComparison.Ordinal)) return FirstToken(line[5..]);
        }
        return null;
    }

    // Путь — либо в кавычках, либо до первого пробела (пути с пробелами мы всегда пишем в кавычках).
    private static string? FirstToken(string value)
    {
        var s = value.Trim();
        if (s.Length == 0) return null;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var space = s.IndexOf(' ');
        return space < 0 ? s : space > 0 ? s[..space] : null;
    }

    /// <summary>autostart .desktop для Linux: Exec = бинарь, автозапуск включён.</summary>
    public static string BuildLinuxDesktop(string exec) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        $"Name={AppSettings.DisplayName}\n" +
        $"Exec=\"{exec}\"\n" +
        "Terminal=false\n" +
        "X-GNOME-Autostart-enabled=true\n";

    // ── Пути ────────────────────────────────────────────────────────────────────

    private static string MacPlistPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "Library", "LaunchAgents", $"{BundleId}.plist");

    private static string LinuxDesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".config", "autostart", "smartbase.desktop");

    // ── Windows реестр (изолировано под платформу) ──────────────────────────────

    [SupportedOSPlatform("windows")]
    private static string? WindowsRecord()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsRunKey);
        return key?.GetValue(WindowsValue) as string;
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? WindowsApprovedFlag()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(WindowsApprovedKey);
        return key?.GetValue(WindowsValue) as byte[];
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
