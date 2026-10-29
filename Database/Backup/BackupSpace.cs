using System.ComponentModel;
using System.Runtime.InteropServices;
using SmartBase.Logging;

namespace SmartBase.Database.Backup;

/// <summary>Предварительная оценка места именно в папке назначения. Она не резервирует
/// диск: другие процессы и рост исходной базы могут занять место уже во время записи.</summary>
internal static class BackupSpace
{
    // Изолированная подмена для тестов без заполнения диска. AsyncLocal не меняет
    // проверку другой одновременно работающей службы или фонового контекста.
    internal static AsyncLocal<Func<string, long?>?> AvailableBytesOverride { get; } = new();

    internal static long RequiredBytes(long estimatedBytes) =>
        checked(Math.Max(0, estimatedBytes) + Math.Max(64L << 20, estimatedBytes / 20));

    internal static void RequireAvailable(string folder, long estimatedBytes)
    {
        var full = Path.GetFullPath(folder);
        var available = AvailableBytes(full);
        if (available is null)
            throw new IOException($"Не удалось проверить свободное место в папке резервных копий: {full}");
        var required = RequiredBytes(estimatedBytes);
        if (available.Value < required)
            throw new IOException($"Недостаточно свободного места в папке резервных копий «{full}». " +
                $"Нужно {Diag.Size(required)} с запасом, свободно {Diag.Size(available.Value)}.");
    }

    internal static long? AvailableBytes(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (AvailableBytesOverride.Value is { } probe) return probe(full);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // DriveInfo перечисляет буквы дисков, но не произвольные UNC-папки.
                // Этот вызов учитывает также квоту пользователя на SMB-ресурсе.
                if (!GetDiskFreeSpaceEx(full, out var available, out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return (long)Math.Min(available, (ulong)long.MaxValue);
            }
            // На Unix DriveInfo принимает путь папки и получает statvfs именно для
            // ее файловой системы, включая отдельный mount и сетевой ресурс.
            return new DriveInfo(full).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            Serilog.Log.Warning(ex, "Резервное копирование: не удалось проверить место в {Folder:l}", full);
            return null;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
}
