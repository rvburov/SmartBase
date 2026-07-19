using System.Diagnostics;
using Avalonia;
using SmartBase.Setup;
using SmartBase.Utils;

namespace SmartBase;

internal sealed class Program
{
    // Режим запуска (вариант A): приложение / установщик / удаление. App читает это в
    // OnFrameworkInitializationCompleted и открывает нужное окно. Подробности — docs/guides/auto-update.md.
    internal static SetupMode Mode { get; private set; } = SetupMode.App;

    // Самозамена при обновлении не удалась (файл был занят) — апдейтер перезапустил нас с этим
    // флагом. App покажет разовое уведомление, что версия осталась прежней.
    internal static bool UpdateFailed { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Глобальные предохранители: НЕобработанные исключения пишутся в crash.log рядом с
        // данными приложения (иначе у конечного пользователя вылет бесследный, диагностировать
        // нечего). Основная защита — точечные catch в UI (Utils/DbErrors и др.), это последний рубеж.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("unhandled", e.ExceptionObject as Exception);
        // Упавшие fire-and-forget задачи (паттерн `_ = Task.Run(...)`): фиксируем в лог и
        // помечаем наблюдёнными — иначе финализатор может завершить процесс.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("unobserved-task", e.Exception);
            e.SetObserved();
        };

        // Legacy-кодировки (Windows-1251 для CSV, кодировки старых .xls): в .NET их нет из
        // коробки, провайдер регистрируется один раз на процесс. Нужен импорту таблиц.
        Core.Importers.TableSources.EncodingSetup.Ensure();

        // Режим самозамены: новый .exe (из %TEMP%) запущен с --apply-update, чтобы
        // дождаться выхода старого приложения, заменить его собой и перезапустить.
        if (args.Length > 0 && args[0] == "--apply-update")
        {
            ApplyUpdate(args);
            return;
        }

        // Перезапуск после НЕудавшейся самозамены — App-режим, но с уведомлением пользователю.
        UpdateFailed = Array.Exists(args, a => a == "--update-failed");

        Mode = ResolveMode(args);
        if (Mode == SetupMode.App) AppSettings.EnsureCreated();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // В Avalonia 11 нет публичного dispatcher-хука: исключение из UI-обработчика
            // валит цикл и приходит сюда. Восстановить сеанс нельзя — но причина останется
            // в crash.log для диагностики.
            LogCrash("ui-loop", ex);
            throw;
        }
    }

    // Дозапись в crash.log (best-effort: сбой логирования не должен маскировать исходную ошибку).
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppSettings.AppDataDir, "crash.log");
            Directory.CreateDirectory(AppSettings.AppDataDir);
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} v{AppSettings.AppVersion}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    // Режим установщика включается аргументом (--install/--uninstall) или именем раздачи
    // «…-Setup»: на Windows/Linux — имя файла бинаря, на macOS — имя `.app`-бандла
    // (SmartBase-Setup.app, внутренний бинарь остаётся SmartBase). Иначе → приложение.
    private static SetupMode ResolveMode(string[] args)
    {
        if (Array.Exists(args, a => a == "--uninstall")) return SetupMode.Uninstall;
        if (Array.Exists(args, a => a == "--install"))   return SetupMode.Install;

        string name;
        if (OperatingSystem.IsMacOS() && InstallService.BundleRoot(Environment.ProcessPath) is { } bundle)
            name = Path.GetFileNameWithoutExtension(bundle);   // SmartBase-Setup(.app)
        else
            name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "";

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

        // Источник замены: на macOS — корень нашего .app-бандла (целую папку), иначе — сам файл.
        var src = OperatingSystem.IsMacOS() ? (InstallService.BundleRoot(self) ?? self) : self;

        // Замена с повторами внутри ReplaceArtifact: на Windows блокировки могут держать файл
        // пару секунд; на macOS заменяется весь .app-бандл (publish под mac не single-file).
        // Если так и не удалось (файл занят) — перезапустим старую версию с --update-failed,
        // чтобы приложение сообщило: обновление не применилось, версия прежняя.
        var replaced = InstallService.ReplaceArtifact(src, target);
        var flag     = replaced ? "" : "--update-failed";

        // Запустить обновлённое приложение.
        try
        {
            if (OperatingSystem.IsMacOS())
                // target — это .app-бандл; правильный запуск GUI-приложения на macOS — через open.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = flag.Length > 0 ? $"\"{target}\" --args {flag}" : $"\"{target}\"",
                    UseShellExecute = false,
                });
            else
                // На Linux запускаем бинарь напрямую (UseShellExecute=true ушёл бы в xdg-open).
                Process.Start(new ProcessStartInfo { FileName = target, Arguments = flag, UseShellExecute = OperatingSystem.IsWindows() });
        }
        catch { /* не удалось перезапустить — пользователь откроет вручную */ }
    }
}
