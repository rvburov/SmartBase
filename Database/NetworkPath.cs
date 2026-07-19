using System.Collections.Concurrent;

namespace SmartBase.Database;

/// <summary>
/// Определение «сетевой ли путь» для файла БД. От этого зависит режим журнала и профиль
/// прагм: на сетевых ФС (SMB/NFS) WAL опасен — его разделяемая память (-shm) не когерентна
/// между машинами, одновременная работа с разных компьютеров молча портит файл. Эвристика
/// консервативна: сомнение трактуем как «сеть» — DELETE-журнал всегда БЕЗОПАСЕН, лишь
/// медленнее. Результат кешируется по пути (проверка mount-таблицы не бесплатна).
/// </summary>
public static class NetworkPath
{
    private static readonly ConcurrentDictionary<string, bool> Cache  = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> Forced = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsNetwork(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { return true; }   // кривой путь — консервативно
        if (Forced.ContainsKey(full)) return true;
        return Cache.GetOrAdd(full, Detect);
    }

    /// <summary>Принудительно пометить путь сетевым — для стендов/харнессов, где настоящей
    /// шары нет, но нужно проверить сетевой профиль (журнал DELETE, прагмы).</summary>
    public static void ForceNetwork(string path) =>
        Forced[System.IO.Path.GetFullPath(path)] = true;

    private static bool Detect(string full)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // UNC-путь или подключённый сетевой диск.
                if (full.StartsWith(@"\\") || full.StartsWith("//")) return true;
                var root = System.IO.Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return true;
                return new DriveInfo(root).DriveType == DriveType.Network;
            }

            if (OperatingSystem.IsMacOS())
                // Всё под /Volumes/ — сетевые шары И внешние диски: у обоих одна модель
                // отказа («том пропал на ходу»), обоим положен DELETE-журнал. Проще и
                // надёжнее, чем P/Invoke statfs с разбором типа ФС.
                return full.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase);

            if (OperatingSystem.IsLinux())
                return IsLinuxNetworkMount(full);

            return true;   // неизвестная ОС — консервативно
        }
        catch
        {
            return true;   // не смогли определить — считаем сетью (безопасный режим)
        }
    }

    // /proc/mounts: «device mountpoint fstype opts …». Ищем точку монтирования с самым
    // длинным префиксом пути и смотрим тип её ФС.
    private static bool IsLinuxNetworkMount(string full)
    {
        var netFs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "cifs", "smb3", "smbfs", "nfs", "nfs4", "fuse.sshfs", "davfs", "afpfs", "9p" };

        var bestLen  = -1;
        var bestType = "";
        foreach (var line in File.ReadLines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;
            var mount = parts[1].Replace("\\040", " ");   // пробелы экранированы октально
            if (full.StartsWith(mount, StringComparison.Ordinal) && mount.Length > bestLen)
            {
                bestLen  = mount.Length;
                bestType = parts[2];
            }
        }
        return netFs.Contains(bestType);
    }
}
