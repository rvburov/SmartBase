using System.Diagnostics;

namespace SmartBase.Utils;

/// <summary>План управления локальным сервером PostgreSQL: как его запускать/останавливать
/// на этой машине. <see cref="CanExecute"/> — приложение может выполнить команду само
/// (macOS: brew/Postgres.app без прав; Windows: с запросом UAC; Linux: через polkit);
/// иначе окна показывают <see cref="StartCommand"/>/<see cref="StopCommand"/> с кнопкой
/// «Скопировать» — вставить в терминал.</summary>
public sealed record PgControlPlan(
    bool   CanExecute,
    string Description,
    string StartCommand,
    string StopCommand);

/// <summary>
/// Запуск и остановка локального сервера PostgreSQL из окна-помощника. Приложение
/// НЕ становится менеджером служб: одна команда штатного инструмента ОС на действие,
/// без демонов и фоновых процессов. Детект способа — best-effort по типовым путям
/// установщиков (как в PgServerProbe), на Windows — по записи установщика в реестре и службам
/// (PgWindowsInstalls); экзотика дает CanExecute=false и честный
/// фолбэк «скопируйте команду». Успех запуска/остановки подтверждает не код возврата,
/// а повторная TCP-проба порта — ее делает вызывающее окно.
/// </summary>
public static class PgServerControl
{
    /// <summary>Определить, как управлять сервером на этой машине; null — установка
    /// не найдена (кнопки управления не показываются, работает инструкция установки).</summary>
    public static PgControlPlan? DetectLocal()
    {
        try
        {
            if (OperatingSystem.IsMacOS())   return DetectMac();
            if (OperatingSystem.IsWindows()) return DetectWindows();
            if (OperatingSystem.IsLinux())   return DetectLinux();
        }
        catch
        {
            // Детект — украшение: сломался — кнопок просто нет.
        }
        return null;
    }

    /// <summary>Выполнить запуск по плану. null — команда отработала (порт проверяет
    /// вызывающий), иначе русский текст ошибки.</summary>
    public static Task<string?> StartAsync(PgControlPlan plan) => RunAsync(plan, start: true);

    /// <summary>Выполнить остановку по плану; контракт как у <see cref="StartAsync"/>.</summary>
    public static Task<string?> StopAsync(PgControlPlan plan) => RunAsync(plan, start: false);

    // ── Детект по ОС ─────────────────────────────────────────────────────────

    private static PgControlPlan? DetectMac()
    {
        // Homebrew: формула postgresql@N в /opt/homebrew/opt (Apple Silicon) или
        // /usr/local/opt (Intel). brew services работает от пользователя, без прав.
        foreach (var optDir in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
        {
            if (!Directory.Exists(optDir)) continue;
            var formula = Directory.EnumerateDirectories(optDir, "postgresql*")
                                   .Select(Path.GetFileName)
                                   .OrderDescending()   // при нескольких — самая новая
                                   .FirstOrDefault();
            if (formula is null) continue;
            var brew = File.Exists("/opt/homebrew/bin/brew") ? "/opt/homebrew/bin/brew"
                     : File.Exists("/usr/local/bin/brew")    ? "/usr/local/bin/brew" : null;
            return new PgControlPlan(
                CanExecute:   brew is not null,
                Description:  $"Homebrew ({formula})",
                StartCommand: $"brew services start {formula}",
                StopCommand:  $"brew services stop {formula}");
        }

        // Postgres.app: запуск — открыть приложение; остановка — выйти из него
        // (osascript может разово спросить разрешение «Автоматизация»).
        if (Directory.Exists("/Applications/Postgres.app"))
            return new PgControlPlan(
                CanExecute:   true,
                Description:  "Postgres.app",
                StartCommand: "open -a Postgres",
                StopCommand:  "osascript -e 'quit app \"Postgres\"'");

        return null;   // установщик EDB и прочее: управляют своими средствами
    }

    private static PgControlPlan? DetectWindows()
    {
        // Служба самой новой установки, у которой служба известна: реестр установщика, службы
        // Windows, «Program Files» (PgWindowsInstalls). Управление службой требует повышения (UAC).
        var service = PgWindowsInstalls.Find().Select(i => i.Service).FirstOrDefault(s => s is not null);
        if (service is null) return null;

        // Имя службы, заведенной руками, может быть с пробелом — net получит его в кавычках.
        var name = service.Contains(' ') ? $"\"{service}\"" : service;
        return new PgControlPlan(
            CanExecute:   true,
            Description:  $"служба Windows ({service})",
            StartCommand: $"net start {name}",
            StopCommand:  $"net stop {name}");
    }

    private static PgControlPlan? DetectLinux()
    {
        var installed = Directory.Exists("/usr/lib/postgresql")
                     || Directory.Exists("/usr/pgsql-16")
                     || Directory.Exists("/var/lib/pgsql");
        if (!installed) return null;

        // Сам systemctl без root службу не тронет; графический запрос пароля дает polkit.
        var pkexec = File.Exists("/usr/bin/pkexec") || File.Exists("/usr/local/bin/pkexec");
        return new PgControlPlan(
            CanExecute:   pkexec,
            Description:  "служба systemd (postgresql)",
            StartCommand: "sudo systemctl start postgresql",
            StopCommand:  "sudo systemctl stop postgresql");
    }

    // ── Выполнение ───────────────────────────────────────────────────────────

    private static async Task<string?> RunAsync(PgControlPlan plan, bool start)
    {
        if (!plan.CanExecute)
            return "Выполнить из приложения здесь нельзя — скопируйте команду в терминал.";

        try
        {
            if (OperatingSystem.IsMacOS())
                return await RunMacAsync(plan, start);
            if (OperatingSystem.IsWindows())
                return await RunElevatedAsync(start ? plan.StartCommand : plan.StopCommand);
            if (OperatingSystem.IsLinux())
                return await RunSilentAsync("pkexec",
                    "systemctl", start ? "start" : "stop", "postgresql");
            return "Неизвестная операционная система.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static async Task<string?> RunMacAsync(PgControlPlan plan, bool start)
    {
        if (plan.Description.StartsWith("Homebrew"))
        {
            var brew = File.Exists("/opt/homebrew/bin/brew") ? "/opt/homebrew/bin/brew"
                                                             : "/usr/local/bin/brew";
            // «brew services start x» из команды-фолбэка: аргументы после «brew ».
            var args = (start ? plan.StartCommand : plan.StopCommand)["brew ".Length..].Split(' ');
            return await RunSilentAsync(brew, args);
        }
        // Postgres.app: без шелла кавычки не разбираются — аргументы передаем списком.
        return start
            ? await RunSilentAsync("open", "-a", "Postgres")
            : await RunSilentAsync("osascript", "-e", "quit app \"Postgres\"");
    }

    // Windows: команда службы под UAC. Отказ в окне UAC — Win32Exception 1223.
    private static async Task<string?> RunElevatedAsync(string command)
    {
        try
        {
            var parts = command.Split(' ', 2);
            using var p = Process.Start(new ProcessStartInfo(parts[0], parts[1])
            {
                Verb            = "runas",
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
            if (p is null) return "Команда не запустилась.";
            await p.WaitForExitAsync();
            return null;   // итог подтвердит проба порта
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "Отменено в окне контроля учетных записей (UAC).";
        }
    }

    private static async Task<string?> RunSilentAsync(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info);
        if (p is null) return "Команда не запустилась.";
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode == 0) return null;
        var reason = stderr.Trim();
        return reason.Length > 0 ? reason : $"Команда завершилась с кодом {p.ExitCode}.";
    }
}
