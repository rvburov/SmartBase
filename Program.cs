using System.Diagnostics;
using Avalonia;
using Serilog;
using SmartBase.Logging;
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

    // Тихий старт из автозапуска (--background): App создаёт окно скрытым — работают только
    // служба уведомлений и значок в трее (см. настройку «Фоновый режим»).
    internal static bool LaunchBackground { get; private set; }

    // Единственный экземпляр на локальную папку данных: фоновый режим не должен поднять второй
    // процесс (двойные уведомления). В режимах установщика/удаления не используется.
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Глобальные предохранители: НЕобработанные исключения пишутся в crash.log рядом с
        // данными приложения (иначе у конечного пользователя вылет бесследный, диагностировать
        // нечего). Основная защита — точечные catch в UI (Utils/DbErrors и др.), это последний рубеж.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash("unhandled", e.ExceptionObject as Exception);
            // Процесс сейчас завершится, и finally в Main может не выполниться (падение с
            // фонового потока). Форсируем сброс буфера Serilog в logs.db, чтобы фатальная
            // запись не потерялась. crash.log уже записан синхронно внутри LogCrash.
            AppLog.Shutdown();
        };
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
        if (Mode == SetupMode.App)
        {
            AppSettings.EnsureCreated();

            // Один экземпляр на папку данных: второй запуск просит работающий показать окно и
            // выходит (иначе фоновый режим поднял бы второй процесс — двойные уведомления).
            LaunchBackground = Array.Exists(args, a => a == Autostart.BackgroundArg);
            Instance = SingleInstance.TryAcquire(AppSettings.AppDataDir);
            if (Instance is null)
            {
                SingleInstance.SignalShow(AppSettings.AppDataDir);
                return;
            }

            // Диагностический лог в logs.db. Только App-режим: установщику/удалению он не нужен.
            AppLog.Init();
            // Точка отсчёта сессии: в logs.db всегда есть версия и старт, даже без ошибок.
            // :l — литеральный вывод строк без кавычек (иначе Serilog пишет v"1.0.3").
            Log.Information("Запуск SmartBase v{Version:l} ({OS:l})",
                AppSettings.AppVersion, System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        }
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
        finally
        {
            // Дописать буфер приёмника при любом выходе (штатном или по исключению).
            // Если Init не вызывали (установщик) — no-op на «тихом» логгере по умолчанию.
            AppLog.Shutdown();
        }
    }

    // Дозапись в crash.log (best-effort: сбой логирования не должен маскировать исходную ошибку).
    // Дублируем в Serilog (logs.db) — но crash.log остаётся независимым резервом: он работает,
    // даже если Serilog ещё не инициализирован (сбой на старте) или его приёмник недоступен.
    private static void LogCrash(string source, Exception? ex)
    {
        // На «тихом» логгере по умолчанию (до AppLog.Init) — безопасный no-op.
        try { Log.Fatal(ex, "Crash: {Source:l} v{Version:l}", source, AppSettings.AppVersion); }
        catch { }

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

    // Менеджер библиотеки DesktopNotifications для Windows/Linux (баннеры с иконкой приложения
    // и колбэком клика). macOS его не использует — там свой бэкенд (MacNotifier). Создаётся один
    // раз на процесс; MainWindow берёт его через фабрику PlatformNotifications.CreateNotifier.
    internal static DesktopNotifications.INotificationManager? NotificationManager { get; private set; }

    public static AppBuilder BuildAvaloniaApp()
    {
        NotificationManager = CreateNotificationManager();
        return AppBuilder.Configure<App>()
                         .UsePlatformDetect()
                         .WithInterFont()
                         .LogToTrace();
    }

    // Платформенный менеджер: Windows — WinRT-тосты (нужен ярлык/AUMID, создаётся контекстом),
    // Linux — freedesktop D-Bus. macOS/прочее — null (используется MacNotifier / no-op).
    private static DesktopNotifications.INotificationManager? CreateNotificationManager()
    {
        try
        {
            DesktopNotifications.INotificationManager? m = null;
            if (OperatingSystem.IsWindows())
                m = new DesktopNotifications.Windows.WindowsNotificationManager(
                        DesktopNotifications.Windows.WindowsApplicationContext.FromCurrentProcess());
            else if (OperatingSystem.IsLinux())
                m = new DesktopNotifications.FreeDesktop.FreeDesktopNotificationManager(
                        DesktopNotifications.FreeDesktop.FreeDesktopApplicationContext.FromCurrentProcess());
            m?.Initialize().GetAwaiter().GetResult();
            return m;
        }
        catch
        {
            // Нет D-Bus / WinRT / ярлыка — уведомления Windows/Linux просто не показываются
            // (фабрика вернёт no-op). Приложение стартует как обычно.
            return null;
        }
    }

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
