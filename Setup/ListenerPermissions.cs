using System.Diagnostics;
using SmartBase.Integrations;

namespace SmartBase.Setup;

/// <summary>
/// Разрешения операционной системы для сетевого режима приема (docs/ROADMAP-API.md, 6.2 и шаг 8).
///
/// Петлевой адрес <c>http://127.0.0.1:порт/</c> открывается без прав администратора — в этом и
/// смысл первого шага. Адрес машины (<c>http://+:порт/</c>) на Windows требует двух разрешений, и
/// оба выдаются ОДИН раз:
///
/// <list type="number">
///   <item><b>Право занять адрес</b> (<c>netsh http add urlacl</c>). Без него слушатель падает
///   на самом старте с «Отказано в доступе».</item>
///   <item><b>Правило брандмауэра</b>. Без него слушатель поднимется, а обращения не дойдут —
///   и это худший из двух отказов: он выглядит как «программа игнорирует».</item>
/// </list>
///
/// <b>Живет в Setup/, а не в Integrations/.</b> На пути обмена запуск постороннего процесса
/// запрещен (10.2, за этим следит тест): сценарий приезжает файлом из папки, и <c>Process.Start</c>
/// рядом с ним означал бы «выполнить команду с той стороны». Выдача разрешений — работа
/// установщика, ее место здесь.
///
/// <b>Программа не делает этого сама и молча.</b> Команды показываются человеку, копируются в
/// буфер одним нажатием и могут быть выполнены по кнопке — с окном контроля учетных записей.
/// Заводить правило брандмауэра без спроса значило бы менять настройки машины за спиной у того,
/// кто за нее отвечает.
/// </summary>
public static class ListenerPermissions
{
    /// <summary>
    /// Нужны ли разрешения на этой системе. На Linux и macOS их не требуется: слушать
    /// непривилегированный порт там может любой процесс, а брандмауэр настраивает организация
    /// своими средствами.
    /// </summary>
    public static bool Needed => OperatingSystem.IsWindows();

    /// <summary>Имя правила брандмауэра. Постоянное: по нему его находят и убирают.</summary>
    public const string RuleName = "SmartBase — прием данных";

    /// <summary>Право занять адрес машины. Выдается учетной записи, под которой работает человек.</summary>
    public static string UrlAclCommand(int port, string? account = null) =>
        $"netsh http add urlacl url=http://+:{port}/ user=\"{account ?? CurrentAccount()}\"";

    /// <summary>Правило брандмауэра для входящих обращений на этот порт.</summary>
    public static string FirewallCommand(int port) =>
        $"netsh advfirewall firewall add rule name=\"{RuleName}\" "
      + $"dir=in action=allow protocol=TCP localport={port}";

    /// <summary>Обе команды разом — то, что кладется в буфер и уходит в заявку в службу ИТ.</summary>
    public static string Script(int port) => string.Join(Environment.NewLine,
    [
        $"REM Разрешения для приема данных SmartBase на порту {port}.",
        "REM Выполнить ОДИН раз от имени администратора на машине обмена.",
        UrlAclCommand(port),
        FirewallCommand(port),
    ]);

    /// <summary>Учетная запись, которой выдается право занять адрес: «ДОМЕН\\пользователь».</summary>
    public static string CurrentAccount()
    {
        var domain = Environment.UserDomainName;
        var user   = Environment.UserName;

        return domain.Length > 0 ? $"{domain}\\{user}" : user;
    }

    /// <summary>
    /// Выполнить обе команды с правами администратора. Возвращает причину отказа; <c>null</c> —
    /// команды отработали.
    ///
    /// Итог НЕ считается доказательством: подтвердит его только сам слушатель, поднявшийся на
    /// сетевом адресе. Поэтому здесь нет ни разбора вывода <c>netsh</c>, ни попытки угадать
    /// «получилось ли» — экран просто пробует открыть порт заново.
    /// </summary>
    public static async Task<string?> GrantAsync(int port)
    {
        if (!Needed) return "на этой системе разрешения не требуются";
        if (!IngressGate.ValidPort(port)) return "номер порта не годится";

        // Обе команды одним окном контроля учетных записей: два подряд человек воспримет как
        // сбой первого.
        var script = $"/c {UrlAclCommand(port)} && {FirewallCommand(port)}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", script)
            {
                Verb            = "runas",
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Hidden,
            });

            if (process is null) return "команда не запустилась";

            await process.WaitForExitAsync();

            return process.ExitCode == 0
                ? null
                : $"команда вернула код {process.ExitCode}";
        }
        catch (System.ComponentModel.Win32Exception denied) when (denied.NativeErrorCode == 1223)
        {
            return "отменено в окне контроля учетных записей";
        }
        catch (Exception failure)
        {
            return failure.Message;
        }
    }
}
