using Microsoft.Win32;
using SmartBase.Database.Backup;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Установленный на этой машине PostgreSQL глазами программы (<see cref="PgWindowsInstalls"/>):
/// резервная копия находит pg_dump, а окно-помощник — службу сервера, где бы сервер ни стоял.
/// Пока поиск смотрел только в «Program Files», сервер в другой папке не находился вовсе, а
/// тесты резервной копии (<c>PgBackupTests</c>) молча уходили в Skipped — их пропуск и прятал
/// дефект. Эти тесты сверяют поиск с записями установщика, прочитанными здесь независимо.
///
/// Только Windows и только на машине с PostgreSQL; иначе Skipped. Чистые части поиска
/// проверяются модульными тестами на любой системе.
/// </summary>
public sealed class PgLocalInstallTests
{
    /// <summary>Установки, записанные установщиком: папка → версия.</summary>
    private static Dictionary<string, string> InstallerRecords()
    {
        var records = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return records;

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var root    = machine.OpenSubKey(@"SOFTWARE\PostgreSQL\Installations");
        if (root is null) return records;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("Base Directory") is string { Length: > 0 } dir && Directory.Exists(Path.Combine(dir, "bin")))
                records[dir.TrimEnd('\\')] = key.GetValue("Version") as string ?? "";
        }
        return records;
    }

    /// <summary>
    /// Каждая установка, которую записал установщик, найдена — с его версией: ее дает только
    /// запись установщика, сам сервер называет версию короче («18.6» против «18.6-3»).
    /// </summary>
    [SkippableFact]
    public void Every_install_recorded_by_the_installer_is_found()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "установки так ищутся только на Windows");
        var records = InstallerRecords();
        Skip.If(records.Count == 0, "установщик PostgreSQL на этой машине ничего не записал");

        var found = PgWindowsInstalls.Find();
        foreach (var (dir, version) in records)
        {
            var install = Assert.Single(found, i => string.Equals(i.BaseDir, dir, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(version, install.Version);
        }
    }

    /// <summary>
    /// pg_dump резервной копии берется из найденной установки — самой новой, — а не из PATH и
    /// не только из «Program Files».
    /// </summary>
    [SkippableFact]
    public void Backup_takes_pg_dump_from_the_newest_install()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "установки так ищутся только на Windows");
        var installs = PgWindowsInstalls.Find();
        Skip.If(installs.Count == 0, "PostgreSQL на этой машине не установлен");

        PgDump.ResetForTests();
        try
        {
            Assert.Equal(Path.Combine(installs[0].BaseDir, "bin", "pg_dump.exe"), PgDump.FindBinary(),
                         StringComparer.OrdinalIgnoreCase);
        }
        finally { PgDump.ResetForTests(); }
    }

    /// <summary>Кнопки «Запустить» и «Остановить сервер» управляют службой найденной установки.</summary>
    [SkippableFact]
    public void Server_buttons_drive_the_service_of_the_found_install()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "установки так ищутся только на Windows");
        var service = PgWindowsInstalls.Find().Select(i => i.Service).FirstOrDefault(s => s is not null);
        Skip.If(service is null, "службы PostgreSQL на этой машине нет");

        var plan = PgServerControl.DetectLocal();
        Assert.NotNull(plan);
        Assert.Contains(service, plan!.StartCommand, StringComparison.Ordinal);
        Assert.Contains(service, plan.StopCommand,   StringComparison.Ordinal);
        Assert.True(PgServerProbe.DetectInstallation());
    }
}
