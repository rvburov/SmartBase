using System.Diagnostics;
using Avalonia;
using DataBase.Setup;
using DataBase.Utils;

namespace DataBase;

internal sealed class Program
{
    // Режим запуска (вариант A): приложение / установщик / удаление. App читает это в
    // OnFrameworkInitializationCompleted и открывает нужное окно. Подробности — docs/guides/auto-update.md.
    internal static SetupMode Mode { get; private set; } = SetupMode.App;

    [STAThread]
    public static void Main(string[] args)
    {
        // Режим самозамены: новый .exe (из %TEMP%) запущен с --apply-update, чтобы
        // дождаться выхода старого приложения, заменить его собой и перезапустить.
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            ApplyUpdate(args);
            return;
        }

        Mode = ResolveMode(args);
        if (Mode == SetupMode.App) AppSettings.EnsureCreated();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Режим установщика включается аргументом (--install/--uninstall) или именем файла
    // «…-Setup.exe» (раздаваемый DataBase-Setup.exe). Обычный DataBase.exe → приложение.
    private static SetupMode ResolveMode(string[] args)
    {
        if (Array.Exists(args, a => a == "--uninstall")) return SetupMode.Uninstall;
        if (Array.Exists(args, a => a == "--install"))   return SetupMode.Install;
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";
        return name.EndsWith("-Setup", StringComparison.OrdinalIgnoreCase) ? SetupMode.Install : SetupMode.App;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();

    // Ждёт выхода старого процесса (--pid), копирует себя поверх --target и запускает
    // обновлённое приложение заново. Запущенный .exe нельзя перезаписать собой, поэтому
    // замену делает новый файл, пока старый закрыт.
    private static void ApplyUpdate(string[] args)
    {
        string? target = null;
        var pid = -1;
        for (var i = 1; i + 1 < args.Length; i++)
        {
            if      (args[i] == "--target") target = args[i + 1];
            else if (args[i] == "--pid")    int.TryParse(args[i + 1], out pid);
        }
        if (string.IsNullOrEmpty(target)) return;

        // Дождаться закрытия старого приложения — иначе файл занят.
        if (pid > 0)
        {
            try { Process.GetProcessById(pid).WaitForExit(20_000); }
            catch { /* процесс уже завершился */ }
        }

        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) return;

        // Перезапись с повторами: антивирус/блокировки могут держать файл пару секунд.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { File.Copy(self, target, overwrite: true); break; }
            catch { Thread.Sleep(500); }
        }

        // Запустить обновлённое приложение.
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); }
        catch { /* не удалось перезапустить — пользователь откроет вручную */ }
    }
}
