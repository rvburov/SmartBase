using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Platform;
using SmartBase.Utils;

namespace SmartBase.Setup;

/// <summary>Режим запуска бинаря: приложение / установщик / удаление (вариант A).</summary>
public enum SetupMode { App, Install, Uninstall }

/// <summary>
/// Установка / обновление / удаление приложения (вариант A: тот же бинарь под именем
/// `…-Setup` работает установщиком и копирует приложение в папку установки). ОС-зависимые
/// куски разведены по Windows / Linux / macOS:
///   • Windows/Linux — single-file бинарь (`SmartBase[.exe]`), ярлыки .lnk/.desktop;
///   • macOS — `.app`-бандл (папка): установка/обновление копируют бандл целиком,
///     режим определяется по имени бандла `SmartBase-Setup.app`.
/// Подробности — docs/guides/auto-update.md.
/// </summary>
public static partial class InstallService
{
    public static bool IsMac => OperatingSystem.IsMacOS();

    /// <summary>Имя бинаря приложения: Windows — `SmartBase.exe`, иначе — `SmartBase`.</summary>
    public static string AppExeName =>
        OperatingSystem.IsWindows() ? AppSettings.AppName + ".exe" : AppSettings.AppName;

    /// <summary>Имя установщика/файла обновления (single-file ОС): `…-Setup[.exe]`.</summary>
    public static string SetupExeName =>
        OperatingSystem.IsWindows() ? AppSettings.AppName + "-Setup.exe" : AppSettings.AppName + "-Setup";

    /// <summary>Имя бандла приложения/установщика на macOS.</summary>
    public static string AppBundleName   => AppSettings.AppName + ".app";          // SmartBase.app
    public static string SetupBundleName => AppSettings.AppName + "-Setup.app";     // SmartBase-Setup.app

    /// <summary>Папка установки по умолчанию (без прав админа):
    /// Windows — %LOCALAPPDATA%\SmartBase, Linux — ~/.local/share/SmartBase, macOS — ~/Applications.</summary>
    public static string DefaultInstallDir =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppName)
            : IsMac
                ? Path.Combine(HomeDir, "Applications")
                : Path.Combine(XdgDataHome, AppSettings.AppName);

    /// <summary>Установленное приложение внутри папки: файл (Win/Linux) или `.app`-бандл (macOS).</summary>
    public static string InstalledPath(string installDir) =>
        Path.Combine(installDir, IsMac ? AppBundleName : AppExeName);

    /// <summary>
    /// Установщик рядом с приложением, в той же папке. Кладётся при установке и служит
    /// единственной точкой входа для обновления и удаления: отдельного деинсталлятора нет.
    /// </summary>
    public static string SetupInstalledPath(string installDir) =>
        Path.Combine(installDir, IsMac ? SetupBundleName : SetupExeName);

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

    /// <summary>
    /// Приводит выбранную пользователем папку к «выделенной»: дописывает подпапку с именем
    /// приложения, если её там ещё нет. Нужно, чтобы удаление не задело чужое содержимое общей
    /// папки — удаление чистит папку установки. На macOS не применяется: единица установки —
    /// `.app`-бандл внутри `~/Applications`, отдельная подпапка не нужна.
    /// </summary>
    public static string EnsureDedicatedDir(string chosenDir)
    {
        if (IsMac || string.IsNullOrWhiteSpace(chosenDir)) return chosenDir;
        var trimmed = chosenDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Уже наша выделенная папка (по имени) или уже содержит установленное приложение — не двоим.
        if (string.Equals(Path.GetFileName(trimmed), AppSettings.AppName, StringComparison.OrdinalIgnoreCase)
            || IsAppPresent(trimmed))
            return trimmed;
        return Path.Combine(trimmed, AppSettings.AppName);
    }

    /// <summary>
    /// Есть ли права на запись в папку (можно ли установить/обновить туда приложение). Саму
    /// папку НЕ создаёт — пробует записать временный файл в ближайший существующий каталог-предок
    /// (проверка вызывается на каждый ввод пути). Ловит защищённые пути без прав: Program Files,
    /// корень диска, чужие каталоги.
    /// </summary>
    public static bool CanCreateOrWrite(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            var probeDir = Path.GetFullPath(dir);
            while (!Directory.Exists(probeDir))
            {
                var parent = Path.GetDirectoryName(probeDir);
                if (string.IsNullOrEmpty(parent) || parent == probeDir) return false;
                probeDir = parent;
            }
            var probe = Path.Combine(probeDir, $"{AppSettings.AppName}.wtest.{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Корень `.app`-бандла для пути к внутреннему бинарю (…/X.app/Contents/MacOS/bin → …/X.app).</summary>
    public static string? BundleRoot(string? innerBinary)
    {
        if (string.IsNullOrEmpty(innerBinary)) return null;
        var app = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(innerBinary)));   // .../X.app
        return app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
    }

    /// <summary>Внутренний бинарь `.app`-бандла: …/X.app/Contents/MacOS/SmartBase.</summary>
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

        progress.Report((0.90, "Копирование установщика…"));
        CopySetupBesideApp(installDir, self);

        progress.Report((0.94, "Создание ярлыков…"));
        CreateShortcuts(installDir, desktopShortcut);

        progress.Report((0.97, "Регистрация…"));
        RegisterUninstall(installDir);   // запись в «Параметры → Приложения» (только Windows)
        RegisterInstallPath(installDir);

        progress.Report((1.0, "Готово"));
    }

    // Установщик кладётся рядом с приложением: одна программа на установку, обновление и
    // удаление, отдельного деинсталлятора нет. Пропускаем, если запущены уже оттуда —
    // копировать файл сам в себя нельзя.
    private static void CopySetupBesideApp(string installDir, string self)
    {
        var target = SetupInstalledPath(installDir);
        if (PathsEqual(self, target)) return;
        try
        {
            File.Copy(self, target, overwrite: true);
            MakeExecutable(target);
        }
        catch { /* не легло — приложение всё равно установлено, удаление доступно из ярлыка */ }
    }

    private static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // macOS: копируем `.app`-бандл целиком (publish под mac не single-file — внутри DLL),
    // переименовывая верхний каталог SmartBase-Setup.app → SmartBase.app (внутренний бинарь и
    // Info.plist уже зовутся SmartBase, патчить не нужно).
    private static async Task InstallMacAsync(string installDir, IProgress<(double, string)> progress)
    {
        var srcBundle = BundleRoot(Environment.ProcessPath)
                        ?? throw new InvalidOperationException("Установщик macOS должен запускаться из .app-бандла.");
        var targetBundle = InstalledPath(installDir);   // <dir>/SmartBase.app

        progress.Report((0, "Копирование приложения…"));
        if (Directory.Exists(targetBundle)) DeleteRecursive(targetBundle);
        await CopyTreeAsync(srcBundle, targetBundle, p => progress.Report((p * 0.9, "Копирование приложения…")));
        MakeExecutable(MacInnerBinary(targetBundle));

        progress.Report((0.90, "Копирование установщика…"));
        await CopySetupBundleBesideAppAsync(installDir, srcBundle);

        progress.Report((0.95, "Регистрация…"));
        ClearQuarantine(targetBundle);   // строго до lsregister, см. комментарий у метода
        RegisterLaunchServices(targetBundle);
        RegisterInstallPath(installDir);

        progress.Report((1.0, "Готово"));
    }

    // macOS: рядом с SmartBase.app кладём SmartBase-Setup.app — ту же программу под именем
    // установщика (режим выбирается по имени бандла, см. Program.ResolveMode). Двойной клик
    // по нему открывает окно обновления и удаления. Пропускаем, если установщик уже запущен
    // из этой самой папки: копировать каталог сам в себя нельзя.
    private static async Task CopySetupBundleBesideAppAsync(string installDir, string srcBundle)
    {
        var target = SetupInstalledPath(installDir);
        if (PathsEqual(srcBundle, target)) return;
        try
        {
            if (Directory.Exists(target)) DeleteRecursive(target);
            await CopyTreeAsync(srcBundle, target, _ => { });
            MakeExecutable(MacInnerBinary(target));
            ClearQuarantine(target);
        }
        catch { /* не легло — приложение всё равно установлено */ }
    }

    // ── Удаление ────────────────────────────────────────────────────────────
    public static void Uninstall(string installDir, bool removeData)
    {
        if (IsMac) { UninstallMac(installDir, removeData); return; }

        Autostart.Disable();     // иначе система будет пытаться стартовать снесённый бинарь
        UnregisterUninstall();   // убрать из «Параметры → Приложения» (Windows)
        RemoveShortcuts();
        UnregisterInstallPath(installDir);
        if (removeData) TryDeleteDir(DataDir);

        var running = Environment.ProcessPath ?? "";
        var full    = Path.GetFullPath(installDir);
        // На Windows нельзя удалить каталог запущенного .exe → отложенный bat.
        // На Linux файл запущенного бинаря можно unlink на ходу → удаляем сразу.
        // В обоих случаях сносим только СВОИ файлы и папку лишь если она осталась пустой —
        // если приложение поставили в общую папку, чужое содержимое не пострадает.
        if (OperatingSystem.IsWindows() && running.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            ScheduleDirDelete(full, running);
        else
            RemoveOwnedFilesAndPrune(full);
    }

    // macOS: удаляем сам бандл (а не папку установки ~/Applications!). Файл запущенного
    // бинаря на macOS можно unlink на ходу → сносим каталог бандла сразу.
    private static void UninstallMac(string installDir, bool removeData)
    {
        var bundle = InstalledPath(installDir);   // <dir>/SmartBase.app
        Autostart.Disable();                      // LaunchAgent указывал бы на снесённый бинарь
        UnregisterLaunchServices(bundle);
        UnregisterInstallPath(installDir);
        // Хвост версий до 1.0.4: тогда рядом клался .command-удалятор, теперь его роль
        // исполняет SmartBase-Setup.app. При обновлении со старой версии файл надо убрать.
        TryDeleteFile(Path.Combine(installDir, $"Uninstall {AppSettings.AppName}.command"));
        if (removeData) TryDeleteDir(DataDir);
        TryDeleteDir(bundle);
        // Установщик сносим последним и только если удаляем не из него самого: каталог
        // запущенного бандла на macOS отвязывается на ходу, но окно ещё живёт до Close().
        var setup = SetupInstalledPath(installDir);
        if (!PathsEqual(setup, BundleRoot(Environment.ProcessPath) ?? "")) TryDeleteDir(setup);
        else ScheduleSelfBundleDelete(setup);
    }

    // macOS: снести собственный бандл после выхода процесса. Каталог запущенного приложения
    // отвязывается на лету, но пока окно не закрыто, внутри него ещё читаются ресурсы —
    // поэтому ждём завершения процесса отдельной командой и только потом удаляем.
    private static void ScheduleSelfBundleDelete(string bundle)
    {
        try
        {
            var pid = Environment.ProcessId;
            RunHidden("/bin/sh",
                $"-c {ShQuote($"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done; rm -rf {ShQuote(bundle)}")}",
                wait: false);
        }
        catch { /* не удалось — останется один бандл установщика, приложение уже удалено */ }
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

    /// <summary>Синхронная замена артефакта (для самозамены без UI): файл или каталог-бандл.
    /// Возвращает true, если замена удалась в пределах попыток; false — если файл всё время
    /// занят (антивирус/старый процесс не отпустил): вызывающий покажет это пользователю.</summary>
    public static bool ReplaceArtifact(string src, string target)
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
                return true;
            }
            catch { Thread.Sleep(500); }
        }
        return false;
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

    /// <summary>Убрать залежавшийся temp-артефакт обновления (best-effort, при старте приложения).
    /// Апдейтер копирует новую версию в <c>%TEMP%\SmartBase.update.exe</c> (или <c>.update.app</c>) и
    /// после перезапуска его не удаляет. Если апдейтер ещё выполняется — файл занят, молча
    /// пропустим, уберётся при следующем старте.</summary>
    public static void CleanStaleUpdateArtifacts()
    {
        var temp = Path.GetTempPath();
        TryDeleteFile(Path.Combine(temp, $"{AppSettings.AppName}.update.exe"));
        TryDeleteDir(Path.Combine(temp,  $"{AppSettings.AppName}.update.app"));   // macOS-бандл (папка)
    }

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
            $"$s = $w.CreateShortcut({PsQuote(lnkPath)}); " +
            $"$s.TargetPath = {PsQuote(targetExe)}; " +
            (arguments.Length > 0 ? $"$s.Arguments = {PsQuote(arguments)}; " : "") +
            $"$s.WorkingDirectory = {PsQuote(workDir)}; " +
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
            AppSettings.AppName, DesktopExecQuote(exe), icon);
        WriteDesktopEntry(Path.Combine(AppsDir, $"{AppSettings.AppName}-uninstall.desktop"),
            $"Uninstall {AppSettings.AppName}", $"{DesktopExecQuote(exe)} --uninstall", icon);

        // Скрипт удаления в самой папке установки (аналог ярлыка «Uninstall» в папке на Windows).
        var uninstallSh = Path.Combine(installDir, "uninstall.sh");
        File.WriteAllText(uninstallSh, $"#!/usr/bin/env bash\nexec {ShQuote(exe)} --uninstall\n");
        MakeExecutable(uninstallSh);

        if (desktop)
        {
            Directory.CreateDirectory(DesktopDir);
            var lnk = Path.Combine(DesktopDir, $"{AppSettings.AppName}.desktop");
            WriteDesktopEntry(lnk, AppSettings.AppName, DesktopExecQuote(exe), icon);
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

    // Экранирование пути под конкретную «границу», куда он подставляется КАК ТЕКСТ скрипта.
    // (Для аргументов процесса — RunHidden/lsregister/update-desktop-database — остаётся Quote:
    //  там нет интерпретатора, экранировать $/` было бы ошибкой.)
    //   • PsQuote          — строковый литерал PowerShell в одинарных кавычках (' → ''); ярлыки .lnk,
    //                        запись в реестр. Внутри '…' спецсимвол только сам '; " в путях Windows невозможен.
    //   • BatEscape        — путь в .bat: % → %% (иначе cmd примет %…% за переменную); кавычки ставит вызывающий.
    //   • ShQuote          — аргумент bash в одинарных кавычках (' → '\''); экранирует всё: $, `, ", \, пробел.
    //   • DesktopExecQuote — поле Exec в .desktop (freedesktop): двойные кавычки, внутри \ ` $ " с обратным слэшем.
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
    private static string BatEscape(string s) => s.Replace("%", "%%");
    private static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
    private static string DesktopExecQuote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("`", "\\`").Replace("$", "\\$").Replace("\"", "\\\"") + "\"";

    // ── macOS: LaunchServices + .command-удалятор ──────────────────────────────
    private const string Lsregister =
        "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";

    // Скачанный из интернета установщик несёт метку карантина, и macOS переносит её на всё,
    // что он записывает, — установленное приложение тоже оказывается заблокированным. Без
    // этого человек проходит «Настройки → Конфиденциальность и безопасность → Все равно
    // открыть» дважды: сначала для установщика, потом для самой программы.
    //
    // Ждём завершения: снять метку надо до того, как бандл увидит LaunchServices, иначе
    // блокировка успевает закешироваться. Ошибку глотаем — установку она не отменяет,
    // в худшем случае вернётся лишнее предупреждение (при установке из сетевой папки
    // метки нет вовсе и снимать нечего).
    private static void ClearQuarantine(string bundle)
    {
        if (!IsMac) return;
        RunHidden("/usr/bin/xattr", $"-dr com.apple.quarantine {Quote(bundle)}", wait: true);
    }

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

    // ── «Параметры → Приложения» (HKCU\…\Uninstall), только Windows ───────────
    // Регистрируем программу в стандартном списке Windows с кнопкой «Удалить»
    // (= SmartBase.exe --uninstall). Через PowerShell, чтобы не тянуть пакет реестра.
    private const string UninstallKey =
        @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppSettings.AppName;

    private static void RegisterUninstall(string installDir)
    {
        if (!OperatingSystem.IsWindows()) return;   // Linux — .desktop в меню, macOS — lsregister

        var exe     = Path.Combine(installDir, AppExeName);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        static string Q(string s) => PsQuote(s);   // строковый литерал PowerShell (' → '')

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

    // Файлы, которые установщик создаёт В САМОЙ папке установки (Win/Linux). Нужны для точечного
    // удаления: сносим только своё, а папку — лишь если она осталась пустой. Ярлыки Пуск/рабочего
    // стола и .desktop лежат вне папки установки — их убирает RemoveShortcuts.
    private static string[] OwnedInstallFiles(string installDir) =>
        OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(installDir, AppExeName),                               // SmartBase.exe
                Path.Combine(installDir, SetupExeName),                             // SmartBase-Setup.exe
                Path.Combine(installDir, $"Uninstall {AppSettings.AppName}.lnk"),
            }
            : new[]
            {
                Path.Combine(installDir, AppExeName),                               // SmartBase
                Path.Combine(installDir, SetupExeName),                             // SmartBase-Setup
                Path.Combine(installDir, "icon.png"),
                Path.Combine(installDir, "uninstall.sh"),
            };

    // Linux (и Windows, когда удаляют не запущенную копию): убираем свои файлы и сносим папку
    // ТОЛЬКО если после этого она пуста (rmdir без рекурсии) — чужое содержимое общей папки цело.
    private static void RemoveOwnedFilesAndPrune(string installDir)
    {
        foreach (var f in OwnedInstallFiles(installDir)) TryDeleteFile(f);
        try
        {
            if (Directory.Exists(installDir) && Directory.GetFileSystemEntries(installDir).Length == 0)
                Directory.Delete(installDir, recursive: false);
        }
        catch { /* не пусто (чужие файлы) или занято — папку оставляем */ }
    }

    // Отложенное удаление (только Windows): запущенный .exe нельзя снести самому себе → bat ждёт
    // его освобождения (до 30 попыток, ~30 с). Удаляем ТОЛЬКО свои файлы, затем rmdir БЕЗ /S —
    // папка исчезнет, лишь если пуста; при установке в общую папку чужие файлы уцелеют. Затем
    // bat самоудаляется. Последний rmdir с парой повторов — гасит гонку с асинхронным удалением.
    // runningExe — файл, из которого удаление и запущено: с 1.0.5 это обычно SmartBase-Setup.exe
    // (установщик лежит рядом с приложением), а при запуске из ярлыка «Uninstall» — SmartBase.exe.
    // Ждать надо именно его: остальные наши файлы не заняты и удалятся сразу.
    private static void ScheduleDirDelete(string dir, string runningExe)
    {
        var exe  = runningExe;
        var exeB = BatEscape(exe);   // % → %% для .bat (в имени папки % легален и иначе съест cmd)
        var dirB = BatEscape(dir);

        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("cd /d \"%~dp0\"\r\n");                 // уйти из удаляемой папки: нельзя снести текущий каталог
        sb.Append("set n=0\r\n");
        sb.Append(":loop\r\n");
        sb.Append("ping -n 2 127.0.0.1 >nul\r\n");
        sb.Append($"del /F /Q \"{exeB}\" 2>nul\r\n");     // ждём освобождения запущенного .exe
        sb.Append($"if not exist \"{exeB}\" goto cleanup\r\n");
        sb.Append("set /a n+=1\r\n");
        sb.Append("if %n% lss 30 goto loop\r\n");
        sb.Append(":cleanup\r\n");
        foreach (var f in OwnedInstallFiles(dir))
        {
            if (string.Equals(f, exe, StringComparison.OrdinalIgnoreCase)) continue;   // .exe уже удалён в цикле
            sb.Append($"del /F /Q \"{BatEscape(f)}\" 2>nul\r\n");
        }
        sb.Append("set m=0\r\n");
        sb.Append(":rmloop\r\n");
        sb.Append($"rmdir \"{dirB}\" 2>nul\r\n");         // без /S — только пустую папку; чужое не тронет
        sb.Append($"if not exist \"{dirB}\" goto done\r\n");
        sb.Append("set /a m+=1\r\n");
        sb.Append("if %m% geq 3 goto done\r\n");
        sb.Append("ping -n 2 127.0.0.1 >nul\r\n");
        sb.Append("goto rmloop\r\n");
        sb.Append(":done\r\n");
        sb.Append("del /F /Q \"%~f0\"\r\n");

        var bat = Path.Combine(Path.GetTempPath(), $"{AppSettings.AppName}.uninstall.bat");
        File.WriteAllText(bat, sb.ToString());
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
