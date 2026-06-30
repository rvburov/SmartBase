using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Platform;
using DataBase.Utils;

namespace DataBase.Setup;

/// <summary>Режим запуска бинаря: приложение / установщик / удаление (вариант A).</summary>
public enum SetupMode { App, Install, Uninstall }

/// <summary>
/// Установка / обновление / удаление приложения (вариант A: тот же бинарь под именем
/// `…-Setup` работает установщиком и копирует приложение в папку установки). ОС-зависимые
/// куски разведены по Windows / Linux / macOS:
///   • Windows/Linux — single-file бинарь (`DataBase[.exe]`), ярлыки .lnk/.desktop;
///   • macOS — `.app`-бандл (папка): установка/обновление копируют бандл целиком,
///     режим определяется по имени бандла `DataBase-Setup.app`.
/// Подробности — docs/guides/auto-update.md.
/// </summary>
public static class InstallService
{
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>Имя бинаря приложения: Windows — `DataBase.exe`, иначе — `DataBase`.</summary>
    public static string AppExeName =>
        OperatingSystem.IsWindows() ? AppSettings.AppName + ".exe" : AppSettings.AppName;

    /// <summary>Имя установщика/файла обновления (single-file ОС): `…-Setup[.exe]`.</summary>
    public static string SetupExeName =>
        OperatingSystem.IsWindows() ? AppSettings.AppName + "-Setup.exe" : AppSettings.AppName + "-Setup";

    /// <summary>Имя бандла приложения/установщика на macOS.</summary>
    public static string AppBundleName   => AppSettings.AppName + ".app";          // DataBase.app
    public static string SetupBundleName => AppSettings.AppName + "-Setup.app";     // DataBase-Setup.app

    /// <summary>Папка установки по умолчанию (без прав админа):
    /// Windows — %LOCALAPPDATA%\DataBase, Linux — ~/.local/share/DataBase, macOS — ~/Applications.</summary>
    public static string DefaultInstallDir =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppName)
            : IsMac
                ? Path.Combine(HomeDir, "Applications")
                : Path.Combine(XdgDataHome, AppSettings.AppName);

    /// <summary>Установленное приложение внутри папки: файл (Win/Linux) или `.app`-бандл (macOS).</summary>
    public static string InstalledPath(string installDir) =>
        Path.Combine(installDir, IsMac ? AppBundleName : AppExeName);

    /// <summary>Папка, из которой запущено приложение (для самоудаления):
    /// каталог бинаря, а на macOS — папка над `.app`-бандлом (напр. ~/Applications).</summary>
    public static string CurrentDir =>
        (IsMac
            ? Path.GetDirectoryName(BundleRoot(Environment.ProcessPath) ?? "")
            : Path.GetDirectoryName(Environment.ProcessPath))
        ?? DefaultInstallDir;

    /// <summary>В папке уже установлено приложение?</summary>
    public static bool IsAppPresent(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return false;
        var p = InstalledPath(installDir);
        return IsMac ? Directory.Exists(p) : File.Exists(p);
    }

    /// <summary>Корень `.app`-бандла для пути к внутреннему бинарю (…/X.app/Contents/MacOS/bin → …/X.app).</summary>
    public static string? BundleRoot(string? innerBinary)
    {
        if (string.IsNullOrEmpty(innerBinary)) return null;
        var app = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(innerBinary)));   // .../X.app
        return app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
    }

    /// <summary>Внутренний бинарь `.app`-бандла: …/X.app/Contents/MacOS/DataBase.</summary>
    public static string MacInnerBinary(string bundle) =>
        Path.Combine(bundle, "Contents", "MacOS", AppSettings.AppName);

    /// <summary>Выставить бит запуска на Unix (на Windows не требуется).</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { /* ФС без unix-режимов — пропускаем */ }
    }

    // ── Установка / обновление ──────────────────────────────────────────────
    public static async Task InstallAsync(string installDir, bool desktopShortcut,
                                          IProgress<(double Percent, string Status)> progress)
    {
        Directory.CreateDirectory(installDir);
        if (IsMac) { await InstallMacAsync(installDir, progress); return; }

        var self      = Environment.ProcessPath
                        ?? throw new InvalidOperationException("Не удалось определить путь к установщику.");
        var targetExe = InstalledPath(installDir);

        progress.Report((0, "Копирование файлов…"));
        await CopyFileWithProgressAsync(self, targetExe, p => progress.Report((p * 0.9, "Копирование файлов…")));
        MakeExecutable(targetExe);

        progress.Report((0.92, "Создание ярлыков…"));
        CreateShortcuts(installDir, desktopShortcut);

        progress.Report((0.97, "Регистрация…"));
        RegisterUninstall(installDir);   // запись в «Параметры → Приложения» (только Windows)

        progress.Report((1.0, "Готово"));
    }

    // macOS: копируем `.app`-бандл целиком (publish под mac не single-file — внутри DLL),
    // переименовывая верхний каталог DataBase-Setup.app → DataBase.app (внутренний бинарь и
    // Info.plist уже зовутся DataBase, патчить не нужно).
    private static async Task InstallMacAsync(string installDir, IProgress<(double, string)> progress)
    {
        var srcBundle = BundleRoot(Environment.ProcessPath)
                        ?? throw new InvalidOperationException("Установщик macOS должен запускаться из .app-бандла.");
        var targetBundle = InstalledPath(installDir);   // <dir>/DataBase.app

        progress.Report((0, "Копирование приложения…"));
        if (Directory.Exists(targetBundle)) DeleteRecursive(targetBundle);
        await CopyTreeAsync(srcBundle, targetBundle, p => progress.Report((p * 0.9, "Копирование приложения…")));
        MakeExecutable(MacInnerBinary(targetBundle));

        progress.Report((0.93, "Регистрация…"));
        RegisterLaunchServices(targetBundle);
        CreateUninstallCommandMac(installDir, targetBundle);

        progress.Report((1.0, "Готово"));
    }

    // ── Удаление ────────────────────────────────────────────────────────────
    public static void Uninstall(string installDir, bool removeData)
    {
        if (IsMac) { UninstallMac(installDir, removeData); return; }

        UnregisterUninstall();   // убрать из «Параметры → Приложения» (Windows)
        RemoveShortcuts();
        if (removeData) TryDeleteDir(DataDir);

        var running = Environment.ProcessPath ?? "";
        var full    = Path.GetFullPath(installDir);
        // На Windows нельзя удалить каталог запущенного .exe → отложенный bat.
        // На Linux файл запущенного бинаря можно unlink на ходу → удаляем сразу.
        if (OperatingSystem.IsWindows() && running.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            ScheduleDirDelete(full);
        else
            TryDeleteDir(full);
    }

    // macOS: удаляем сам бандл (а не папку установки ~/Applications!). Файл запущенного
    // бинаря на macOS можно unlink на ходу → сносим каталог бандла сразу.
    private static void UninstallMac(string installDir, bool removeData)
    {
        var bundle = InstalledPath(installDir);   // <dir>/DataBase.app
        UnregisterLaunchServices(bundle);
        TryDeleteFile(Path.Combine(installDir, $"Uninstall {AppSettings.AppName}.command"));
        if (removeData) TryDeleteDir(DataDir);
        TryDeleteDir(bundle);
    }

    // ── Пути ─────────────────────────────────────────────────────────────────
    private static string HomeDir      => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string DesktopDir   => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppSettings.AppName);
    private static string DataDir      => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppSettings.AppName);

    // ~/.local/share (XDG_DATA_HOME) и каталог .desktop-ярлыков меню приложений Linux.
    private static string XdgDataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(HomeDir, ".local", "share");
    private static string AppsDir => Path.Combine(XdgDataHome, "applications");

    // ── Копирование артефакта (файл или каталог-бандл) ─────────────────────────
    private static async Task CopyFileWithProgressAsync(string src, string dst, Action<double> onProgress)
    {
        const int buf = 1 << 20; // 1 МБ
        await using var s = new FileStream(src, FileMode.Open,   FileAccess.Read,  FileShare.Read, buf, useAsync: true);
        await using var d = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, buf, useAsync: true);
        var total = s.Length;
        var buffer = new byte[buf];
        long done = 0;
        int n;
        while ((n = await s.ReadAsync(buffer)) > 0)
        {
            await d.WriteAsync(buffer.AsMemory(0, n));
            done += n;
            if (total > 0) onProgress((double)done / total);
        }
        await d.FlushAsync();
    }

    /// <summary>Рекурсивно копирует каталог (для `.app`-бандла macOS) с прогрессом по байтам.</summary>
    public static async Task CopyTreeAsync(string srcDir, string dstDir, Action<double> onProgress)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var d in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dstDir, Path.GetRelativePath(srcDir, d)));

        var files = Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories);
        long total = 0;
        foreach (var f in files) total += SafeLength(f);

        const int bufSize = 1 << 20;
        var buffer = new byte[bufSize];
        long done = 0;
        foreach (var f in files)
        {
            var dst = Path.Combine(dstDir, Path.GetRelativePath(srcDir, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await using var s = new FileStream(f,   FileMode.Open,   FileAccess.Read,  FileShare.Read, bufSize, useAsync: true);
            await using var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, bufSize, useAsync: true);
            int n;
            while ((n = await s.ReadAsync(buffer)) > 0)
            {
                await o.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) onProgress((double)done / total);
            }
            await o.FlushAsync();
        }
        if (total == 0) onProgress(1);
    }

    /// <summary>Синхронная замена артефакта (для самозамены без UI): файл или каталог-бандл.</summary>
    public static void ReplaceArtifact(string src, string target)
    {
        var isDir = Directory.Exists(src) || Directory.Exists(target);   // macOS .app
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (isDir)
                {
                    if (Directory.Exists(target)) DeleteRecursive(target);
                    CopyTreeSync(src, target);
                    if (IsMac) MakeExecutable(MacInnerBinary(target));
                }
                else
                {
                    File.Copy(src, target, overwrite: true);
                    MakeExecutable(target);
                }
                return;
            }
            catch { Thread.Sleep(500); }
        }
    }

    private static void CopyTreeSync(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (var d in Directory.GetDirectories(srcDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dstDir, Path.GetRelativePath(srcDir, d)));
        foreach (var f in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(dstDir, Path.GetRelativePath(srcDir, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    /// <summary>Рекурсивно удалить каталог (для замены/удаления `.app`-бандла).</summary>
    public static void DeleteRecursive(string path) => TryDeleteDir(path);

    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }

    // ── Ярлыки ─────────────────────────────────────────────────────────────────
    private static void CreateShortcuts(string installDir, bool desktop)
    {
        if (OperatingSystem.IsWindows())    CreateShortcutsWindows(installDir, desktop);
        else if (OperatingSystem.IsLinux()) CreateShortcutsLinux(installDir, desktop);
        // macOS — ярлыки не нужны: приложение лежит в ~/Applications (Launchpad/Finder),
        // регистрацию делает lsregister в InstallMacAsync.
    }

    private static void RemoveShortcuts()
    {
        if (OperatingSystem.IsWindows())
        {
            TryDeleteFile(Path.Combine(DesktopDir, $"{AppSettings.AppName}.lnk"));
            TryDeleteDir(StartMenuDir);
        }
        else if (OperatingSystem.IsLinux())
        {
            TryDeleteFile(Path.Combine(AppsDir,    $"{AppSettings.AppName}.desktop"));
            TryDeleteFile(Path.Combine(AppsDir,    $"{AppSettings.AppName}-uninstall.desktop"));
            TryDeleteFile(Path.Combine(DesktopDir, $"{AppSettings.AppName}.desktop"));
            RefreshDesktopDb();
        }
    }

    // ── Windows: ярлыки .lnk через WScript.Shell ───────────────────────────────
    private static void CreateShortcutsWindows(string installDir, bool desktop)
    {
        var exe = Path.Combine(installDir, AppExeName);
        Directory.CreateDirectory(StartMenuDir);
        CreateShortcutWindows(Path.Combine(StartMenuDir, $"{AppSettings.AppName}.lnk"),           exe);
        CreateShortcutWindows(Path.Combine(StartMenuDir, $"Uninstall {AppSettings.AppName}.lnk"), exe, "--uninstall");
        // Ярлык удаления рядом с приложением — в самой папке установки.
        CreateShortcutWindows(Path.Combine(installDir,   $"Uninstall {AppSettings.AppName}.lnk"), exe, "--uninstall");
        if (desktop)
            CreateShortcutWindows(Path.Combine(DesktopDir, $"{AppSettings.AppName}.lnk"), exe);
    }

    // Ярлык .lnk создаём через WScript.Shell из PowerShell (без COM-interop в проекте).
    private static void CreateShortcutWindows(string lnkPath, string targetExe, string arguments = "")
    {
        var workDir = Path.GetDirectoryName(targetExe) ?? "";
        var script =
            "$w = New-Object -ComObject WScript.Shell; " +
            $"$s = $w.CreateShortcut('{lnkPath}'); " +
            $"$s.TargetPath = '{targetExe}'; " +
            (arguments.Length > 0 ? $"$s.Arguments = '{arguments}'; " : "") +
            $"$s.WorkingDirectory = '{workDir}'; " +
            "$s.Save()";
        RunHidden("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"", wait: true);
    }

    // ── Linux: ярлыки .desktop по XDG ──────────────────────────────────────────
    // Меню приложений = регистрация (аналог реестра на Windows). Иконку достаём из вшитого
    // ресурса в папку установки и ссылаемся на неё абсолютным путём.
    private static void CreateShortcutsLinux(string installDir, bool desktop)
    {
        var exe  = Path.Combine(installDir, AppExeName);
        var icon = Path.Combine(installDir, "icon.png");
        ExtractIcon(icon);

        Directory.CreateDirectory(AppsDir);
        WriteDesktopEntry(Path.Combine(AppsDir, $"{AppSettings.AppName}.desktop"),
            AppSettings.AppName, Quote(exe), icon);
        WriteDesktopEntry(Path.Combine(AppsDir, $"{AppSettings.AppName}-uninstall.desktop"),
            $"Uninstall {AppSettings.AppName}", $"{Quote(exe)} --uninstall", icon);

        // Скрипт удаления в самой папке установки (аналог ярлыка «Uninstall» в папке на Windows).
        var uninstallSh = Path.Combine(installDir, "uninstall.sh");
        File.WriteAllText(uninstallSh, $"#!/usr/bin/env bash\nexec {Quote(exe)} --uninstall\n");
        MakeExecutable(uninstallSh);

        if (desktop)
        {
            Directory.CreateDirectory(DesktopDir);
            var lnk = Path.Combine(DesktopDir, $"{AppSettings.AppName}.desktop");
            WriteDesktopEntry(lnk, AppSettings.AppName, Quote(exe), icon);
            MakeExecutable(lnk);   // GNOME требует +x у запускаемого ярлыка на рабочем столе
        }

        RefreshDesktopDb();
    }

    private static void WriteDesktopEntry(string path, string name, string exec, string icon)
    {
        var content =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            $"Name={name}\n" +
            $"Exec={exec}\n" +
            $"Icon={icon}\n" +
            "Terminal=false\n" +
            "Categories=Office;Utility;Database;\n" +
            $"StartupWMClass={AppSettings.AppName}\n";
        File.WriteAllText(path, content);
    }

    // Иконку приложения достаём из вшитого Avalonia-ресурса (avares://) в файл .png.
    private static void ExtractIcon(string destPng)
    {
        try
        {
            using var s = AssetLoader.Open(new Uri($"avares://{AppSettings.AppName}/Assets/Branding/icon.png"));
            using var f = File.Create(destPng);
            s.CopyTo(f);
        }
        catch { /* ресурс недоступен — ярлык будет без иконки */ }
    }

    private static void RefreshDesktopDb() =>
        RunHidden("update-desktop-database", Quote(AppsDir), wait: false);

    private static string Quote(string s) => "\"" + s + "\"";

    // ── macOS: LaunchServices + .command-удалятор ──────────────────────────────
    private const string Lsregister =
        "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    private static void RegisterLaunchServices(string bundle)
    {
        if (!IsMac) return;   // зарегистрировать бандл в Launchpad/Finder (best-effort)
        RunHidden(Lsregister, $"-f {Quote(bundle)}", wait: false);
    }

    private static void UnregisterLaunchServices(string bundle)
    {
        if (!IsMac) return;
        RunHidden(Lsregister, $"-u {Quote(bundle)}", wait: false);
    }

    // Удалятор рядом с приложением: «Uninstall DataBase.command» открывается двойным кликом
    // (Terminal) и запускает DataBase.app --uninstall (аналог ярлыка «Uninstall» на Win/Linux).
    private static void CreateUninstallCommandMac(string installDir, string bundle)
    {
        var cmd = Path.Combine(installDir, $"Uninstall {AppSettings.AppName}.command");
        File.WriteAllText(cmd, $"#!/bin/bash\nexec {Quote(MacInnerBinary(bundle))} --uninstall\n");
        MakeExecutable(cmd);
    }

    // ── «Параметры → Приложения» (HKCU\…\Uninstall), только Windows ───────────
    // Регистрируем программу в стандартном списке Windows с кнопкой «Удалить»
    // (= DataBase.exe --uninstall). Через PowerShell, чтобы не тянуть пакет реестра.
    private const string UninstallKey =
        @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppSettings.AppName;

    private static void RegisterUninstall(string installDir)
    {
        if (!OperatingSystem.IsWindows()) return;   // Linux — .desktop в меню, macOS — lsregister

        var exe     = Path.Combine(installDir, AppExeName);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        static string Q(string s) => "'" + s.Replace("'", "''") + "'";   // одинарные кавычки PowerShell

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        sb.AppendLine($"$k={Q(UninstallKey)}");
        sb.AppendLine("New-Item -Path $k -Force | Out-Null");
        void Set(string name, string value, string type) =>
            sb.AppendLine($"New-ItemProperty -Path $k -Name {name} -Value {value} -PropertyType {type} -Force | Out-Null");
        Set("DisplayName",     Q(AppSettings.AppName),      "String");
        Set("DisplayVersion",  Q(version),                  "String");
        Set("UninstallString", Q($"\"{exe}\" --uninstall"), "String");
        Set("InstallLocation", Q(installDir),               "String");
        Set("DisplayIcon",     Q(exe),                      "String");
        Set("Publisher",       Q(AppSettings.AppName),      "String");
        Set("NoModify",        "1",                         "DWord");
        Set("NoRepair",        "1",                         "DWord");

        var ps1 = Path.Combine(Path.GetTempPath(), $"{AppSettings.AppName}.reg.ps1");
        File.WriteAllText(ps1, sb.ToString());
        RunHidden("powershell", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ps1}\"", wait: true);
        TryDeleteFile(ps1);
    }

    private static void UnregisterUninstall()
    {
        if (!OperatingSystem.IsWindows()) return;   // Linux: меню чистится в RemoveShortcuts
        RunHidden("powershell",
            $"-NoProfile -NonInteractive -Command \"Remove-Item -Path '{UninstallKey}' -Recurse -Force -ErrorAction SilentlyContinue\"",
            wait: true);
    }

    // Отложенное удаление папки (только Windows): bat повторяет rmdir, пока не исчезнет САМ
    // каталог (ждёт освобождения запущенного .exe; гасит гонку «содержимое удалено, а пустой
    // каталог с первого раза не снёсся»). До 30 попыток (~30 с), затем самоудаляется.
    private static void ScheduleDirDelete(string dir)
    {
        var bat = Path.Combine(Path.GetTempPath(), $"{AppSettings.AppName}.uninstall.bat");
        var content =
            "@echo off\r\n" +
            "cd /d \"%~dp0\"\r\n" +   // уйти из удаляемой папки: нельзя снести каталог, что является текущим
            "set n=0\r\n" +
            ":loop\r\n" +
            "ping -n 2 127.0.0.1 >nul\r\n" +
            $"rmdir /S /Q \"{dir}\" 2>nul\r\n" +
            $"if not exist \"{dir}\" goto done\r\n" +
            "set /a n+=1\r\n" +
            "if %n% lss 30 goto loop\r\n" +
            ":done\r\n" +
            "del /F /Q \"%~f0\"\r\n";
        File.WriteAllText(bat, content);
        RunHidden("cmd.exe", $"/c \"{bat}\"", wait: false);
    }

    private static void RunHidden(string file, string args, bool wait)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName         = file,
                Arguments        = args,
                UseShellExecute  = false,
                CreateNoWindow   = true,
                WindowStyle      = ProcessWindowStyle.Hidden,
                // Рабочая папка — %TEMP%, а не папка установки: иначе дочерний процесс
                // наследует её как текущий каталог и блокирует удаление.
                WorkingDirectory = Path.GetTempPath(),
            });
            if (wait) p?.WaitForExit();
        }
        catch { /* среда без нужной утилиты — пропускаем */ }
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* нет файла/занят */ } }

    // Удаление каталога с повторами: удаление файлов на Windows асинхронно, и пустой
    // каталог иногда не сносится с первого раза — повторяем несколько раз.
    private static void TryDeleteDir(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                Directory.Delete(path, recursive: true);
                return;
            }
            catch { Thread.Sleep(200); }
        }
    }
}
