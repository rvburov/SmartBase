using System.Diagnostics;
using System.Reflection;
using System.Text;
using DataBase.Utils;

namespace DataBase.Setup;

/// <summary>Режим запуска бинаря: приложение / установщик / удаление (вариант A).</summary>
public enum SetupMode { App, Install, Uninstall }

/// <summary>
/// Установка / обновление / удаление приложения. Вариант A: тот же `.exe` под именем
/// `DataBase-Setup.exe` работает как установщик и копирует сам себя в папку установки
/// под именем `DataBase.exe`. Без реестра — запись об установке лежит в самой папке.
/// Подробности — docs/guides/auto-update.md.
/// </summary>
public static class InstallService
{
    public const string AppExeName = AppSettings.AppName + ".exe"; // DataBase.exe

    /// <summary>Папка установки по умолчанию: %LOCALAPPDATA%\DataBase (без прав админа).</summary>
    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppName);

    /// <summary>Каталог запущенного `.exe` — для режима удаления (ярлык ведёт на установленный файл).</summary>
    public static string CurrentDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? DefaultInstallDir;

    /// <summary>В папке уже установлено приложение?</summary>
    public static bool IsAppPresent(string installDir) =>
        !string.IsNullOrWhiteSpace(installDir) && File.Exists(Path.Combine(installDir, AppExeName));

    // ── Установка / обновление ──────────────────────────────────────────────
    public static async Task InstallAsync(string installDir, bool desktopShortcut,
                                          IProgress<(double Percent, string Status)> progress)
    {
        Directory.CreateDirectory(installDir);
        var self      = Environment.ProcessPath
                        ?? throw new InvalidOperationException("Не удалось определить путь к установщику.");
        var targetExe = Path.Combine(installDir, AppExeName);

        progress.Report((0, "Копирование файлов…"));
        await CopyWithProgressAsync(self, targetExe, p => progress.Report((p * 0.9, "Копирование файлов…")));

        progress.Report((0.92, "Создание ярлыков…"));
        CreateShortcuts(installDir, desktopShortcut);

        progress.Report((0.97, "Регистрация…"));
        RegisterUninstall(installDir);   // запись в «Параметры → Приложения»

        progress.Report((1.0, "Готово"));
    }

    // ── Удаление ────────────────────────────────────────────────────────────
    // Ярлыки и (опц.) данные удаляем сразу. Папку установки удаляем напрямую, если
    // запущены НЕ из неё (режим установщика), либо отложенным bat-скриптом, если
    // удаляем сами себя (ярлык «Удалить» ведёт на установленный `.exe`).
    public static void Uninstall(string installDir, bool removeData)
    {
        UnregisterUninstall();   // убрать из «Параметры → Приложения»
        TryDeleteFile(Path.Combine(DesktopDir, $"{AppSettings.AppName}.lnk"));
        TryDeleteDir(StartMenuDir);
        if (removeData) TryDeleteDir(DataDir);

        var running = Environment.ProcessPath ?? "";
        var full    = Path.GetFullPath(installDir);
        if (running.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            ScheduleDirDelete(full);   // мы внутри удаляемой папки → отложенно
        else
            TryDeleteDir(full);        // чужой каталог (режим установщика) → сразу
    }

    // ── Внутреннее ───────────────────────────────────────────────────────────
    private static string DesktopDir   => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppSettings.AppName);
    private static string DataDir      => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppSettings.AppName);

    private static async Task CopyWithProgressAsync(string src, string dst, Action<double> onProgress)
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

    private static void CreateShortcuts(string installDir, bool desktop)
    {
        var exe = Path.Combine(installDir, AppExeName);
        Directory.CreateDirectory(StartMenuDir);
        CreateShortcut(Path.Combine(StartMenuDir, $"{AppSettings.AppName}.lnk"),           exe);
        CreateShortcut(Path.Combine(StartMenuDir, $"Uninstall {AppSettings.AppName}.lnk"), exe, "--uninstall");
        // Ярлык удаления рядом с приложением — в самой папке установки.
        CreateShortcut(Path.Combine(installDir,   $"Uninstall {AppSettings.AppName}.lnk"), exe, "--uninstall");
        if (desktop)
            CreateShortcut(Path.Combine(DesktopDir, $"{AppSettings.AppName}.lnk"), exe);
    }

    // Ярлык .lnk создаём через WScript.Shell из PowerShell (без COM-interop в проекте).
    private static void CreateShortcut(string lnkPath, string targetExe, string arguments = "")
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

    // ── «Параметры → Приложения» (HKCU\…\Uninstall) ──────────────────────────
    // Регистрируем программу в стандартном списке Windows с кнопкой «Удалить»
    // (= DataBase.exe --uninstall). Через PowerShell, чтобы не тянуть пакет реестра.
    private const string UninstallKey =
        @"HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppSettings.AppName;

    private static void RegisterUninstall(string installDir)
    {
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

    private static void UnregisterUninstall() =>
        RunHidden("powershell",
            $"-NoProfile -NonInteractive -Command \"Remove-Item -Path '{UninstallKey}' -Recurse -Force -ErrorAction SilentlyContinue\"",
            wait: true);

    // Отложенное удаление папки: bat повторяет rmdir, пока не исчезнет САМ каталог
    // (ждёт освобождения запущенного `.exe`; гасит гонку «содержимое удалено, а пустой
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
        catch { /* среда без cmd/powershell — пропускаем */ }
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
