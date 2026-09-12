using SmartBase.Setup;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Разрешения системы для сетевого режима приема (<see cref="ListenerPermissions"/>,
/// docs/ROADMAP-API.md, 6.2 и шаг 8).
///
/// Выполнить эти команды в тесте нельзя — они требуют прав администратора и меняют настройки
/// машины. Проверяется другое, и оно важнее: команды называют ТОТ ЖЕ порт, что слушает
/// программа, и обе попадают в текст, который человек передает в службу ИТ. Ошибка здесь
/// кончается заявкой на чужой порт, а разбираться с ней будут неделю.
/// </summary>
public sealed class ListenerPermissionsTests
{
    [Fact]
    public void Both_commands_name_the_port_that_will_be_listened_to()
    {
        const int port = 9001;

        Assert.Contains($":{port}/", ListenerPermissions.UrlAclCommand(port),  StringComparison.Ordinal);
        Assert.Contains($"={port}",  ListenerPermissions.FirewallCommand(port), StringComparison.Ordinal);
    }

    /// <summary>
    /// Право занять адрес выдается КОНКРЕТНОЙ учетной записи: выдать его «всем» — это открытый
    /// порт для любого процесса машины, а такое разрешение выдают один раз и забывают навсегда.
    /// </summary>
    [Fact]
    public void The_address_right_is_given_to_a_named_account()
    {
        var command = ListenerPermissions.UrlAclCommand(8787, "ЗАВОД\\Петров");

        Assert.Contains("user=\"ЗАВОД\\Петров\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void The_account_is_taken_from_the_machine_when_not_given()
    {
        Assert.Contains(Environment.UserName, ListenerPermissions.UrlAclCommand(8787),
                        StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Скрипт для службы ИТ несет обе команды: одной из них недостаточно никогда.</summary>
    [Fact]
    public void The_script_carries_both_commands()
    {
        var script = ListenerPermissions.Script(8787);

        Assert.Contains("urlacl",   script, StringComparison.Ordinal);
        Assert.Contains("advfirewall", script, StringComparison.Ordinal);
        Assert.Contains(ListenerPermissions.RuleName, script, StringComparison.Ordinal);
    }

    /// <summary>Разрешения нужны только там, где их спрашивает система.</summary>
    [Fact]
    public void Permissions_are_a_windows_matter()
    {
        Assert.Equal(OperatingSystem.IsWindows(), ListenerPermissions.Needed);
    }
}
