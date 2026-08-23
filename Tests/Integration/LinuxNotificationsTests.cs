using DesktopNotifications.FreeDesktop;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Связка с D-Bus для нативных уведомлений Linux. Тест закрывает конкретную дыру: пакеты
/// DesktopNotifications.FreeDesktop 1.3.1 и Tmds.DBus разъезжаются по версиям, и разъезд
/// виден только в момент вызова — сборка проходит, а на машине пользователя менеджер падает
/// с MissingMethodException или TypeLoadException.
///
/// Поймать это глазами нельзя: Program.CreateNotificationManager ловит любое исключение
/// и пишет предупреждение в лог. Приложение стартует как ни в чем не бывало, значок-счетчик
/// растет, а баннеров нет. Ровно так уже пряталась поломка Windows-тостов.
///
/// Требует шину сеанса D-Bus, поэтому в обычном прогоне уходит в Skipped. По-настоящему
/// выполняется на Linux-раннере под `dbus-run-session` — см. шаг «Уведомления Linux»
/// в рабочем процессе сборки на GitHub. Путь к нему здесь намеренно не назван: тот модуль
/// удаляется отдельно, и ссылка на его файл осталась бы висеть.
/// </summary>
public sealed class LinuxNotificationsTests
{
    private static bool HasSessionBus =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));

    [SkippableFact]
    public void Notification_manager_binds_to_dbus()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Бэкенд FreeDesktop есть только на Linux");
        Skip.IfNot(HasSessionBus, "Нет шины сеанса D-Bus (запускать под dbus-run-session)");

        // Те же две строки, что в Program.CreateNotificationManager, — проверяем ровно тот путь,
        // по которому приложение поднимает баннеры.
        var context = FreeDesktopApplicationContext.FromCurrentProcess();
        using var manager = new FreeDesktopNotificationManager(context);

        // Ошибки связки версий — это MissingMethodException, TypeLoadException и подобные:
        // метод или тип, на который скомпилирован DesktopNotifications, в Tmds.DBus исчез.
        // Любая другая ошибка (демон уведомлений не запущен, шина отказала) к разъезду версий
        // отношения не имеет: на раннере графического сеанса нет, и это нормально.
        var error = Record.Exception(() => manager.Initialize().GetAwaiter().GetResult());

        Assert.False(
            error is MissingMethodException or MissingFieldException or TypeLoadException
                  or BadImageFormatException or FileNotFoundException,
            $"Версии DesktopNotifications.FreeDesktop и Tmds.DBus разъехались: {error}");
    }
}
