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

    // UNC-запись (\\server\share или //server/share) — сеть на любой ОС. Проверяется по СЫРОЙ
    // строке, до GetFullPath: на Linux и macOS обратный слеш — обычный символ имени, и
    // GetFullPath приклеил бы к такому пути текущую папку, спрятав префикс. Такая строка в
    // реестре баз бывает у файла, скопированного с Windows-машины, — считать ее локальной нельзя.
    private static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    // Ключ кеша и пометок: UNC-строка остается как есть, остальное — полный путь.
    private static string Full(string path) => IsUnc(path) ? path : System.IO.Path.GetFullPath(path);

    public static bool IsNetwork(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (IsUnc(path)) return true;
        string full;
        try { full = Full(path); }
        catch { return true; }   // кривой путь — консервативно
        if (Forced.ContainsKey(full)) return true;
        if (Cache.TryGetValue(full, out var cached)) return cached;
        // Кешируется только уверенный ответ. Пока подключенный диск P: еще не восстановлен после
        // входа в систему, Windows отвечает NoRootDirectory — это не «локальный диск», а «пока не
        // знаю»: ответ «локально», запомненный в этот момент, при следующем монтировании переводил
        // бы общую базу в WAL (см. Detect). Такой путь считается сетевым и переспрашивается.
        var (network, definitive) = DetectDetailed(full);
        if (definitive) Cache[full] = network;
        return network;
    }

    /// <summary>Принудительно пометить путь сетевым: стенды и тесты, где настоящей шары нет, и
    /// файл с признаком общей базы внутри (см. DatabaseManager — <c>storage.shared</c> в db_meta),
    /// открытый по пути, который система определила как локальный.</summary>
    public static void ForceNetwork(string path) =>
        Forced[Full(path)] = true;

    /// <summary>Забыть все, что известно о пути (принудительная пометка и кеш определения) — для
    /// тестов, которые проверяют повторное определение с чистого листа.</summary>
    public static void Forget(string path)
    {
        var full = Full(path);
        Forced.TryRemove(full, out _);
        Cache.TryRemove(full, out _);
        ForcedRoots.TryRemove(full, out _);
    }

    /// <summary>Стенды: считать сетевым «сервером» для группировки баз (см. <see cref="ServerRoot"/>)
    /// заданную папку, а не корень диска — чтобы два временных файла в одной папке выглядели
    /// как две базы одной шары, а файл в другой папке — как база другого сервера.</summary>
    public static void ForceServerRoot(string path, string root) =>
        ForcedRoots[Full(path)] = root;

    private static readonly ConcurrentDictionary<string, string> ForcedRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// «Сервер» сетевого пути — ключ, по которому базы одной шары считаются соседями:
    /// <c>\\server\share</c> для UNC, <c>P:\</c> для подключенного диска, <c>/Volumes/имя</c>
    /// на macOS, точка монтирования на Linux. Нужен монитору доступности: сервер перезагружают
    /// целиком, и потеря одной базы почти наверняка означает потерю остальных на том же
    /// пути — их выгоднее проверить сразу, чем дать UI-потоку упереться в каждую по очереди.
    /// Не сетевой путь → null (сравнение без учета регистра).
    /// </summary>
    public static string? ServerRoot(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string full;
        try { full = Full(path); }
        catch { return null; }
        if (ForcedRoots.TryGetValue(full, out var forced)) return forced.ToLowerInvariant();
        if (!IsNetwork(full)) return null;
        try
        {
            if (IsUnc(full))
            {
                // \\server\share\папка\файл → \\server\share
                var parts = full.TrimStart('\\', '/').Split(['\\', '/'], 3, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}".ToLowerInvariant() : full.ToLowerInvariant();
            }
            if (OperatingSystem.IsWindows())
                return (System.IO.Path.GetPathRoot(full) ?? full).ToLowerInvariant();
            if (OperatingSystem.IsMacOS())
            {
                // /Volumes/имя/… → /Volumes/имя
                var parts = full.Split('/', 4, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $"/{parts[0]}/{parts[1]}".ToLowerInvariant() : full.ToLowerInvariant();
            }
            if (OperatingSystem.IsLinux())
                return LinuxMountPoint(full)?.ToLowerInvariant() ?? full.ToLowerInvariant();
        }
        catch { /* ниже — путь целиком как ключ */ }
        return full.ToLowerInvariant();
    }

    // (сетевой ли путь, уверенный ли это ответ). Неуверенный ответ всегда «сеть» и не кешируется.
    private static (bool Network, bool Definitive) DetectDetailed(string full)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // UNC-путь или подключенный сетевой диск.
                if (IsUnc(full)) return (true, true);
                var root = System.IO.Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return (true, true);
                return ClassifyDrive(new DriveInfo(root).DriveType, IsPersistentMapping(root));
            }

            if (OperatingSystem.IsMacOS())
                // Все под /Volumes/ — сетевые шары И внешние диски: у обоих одна модель
                // отказа («том пропал на ходу»), обоим положен DELETE-журнал. Проще и
                // надежнее, чем P/Invoke statfs с разбором типа ФС.
                return (full.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase), true);

            if (OperatingSystem.IsLinux())
                return (IsLinuxNetworkMount(full), true);

            return (true, true);   // неизвестная ОС — консервативно
        }
        catch
        {
            return (true, false);   // не смогли определить — считаем сетью и переспросим
        }
    }

    /// <summary>
    /// Тип диска Windows → (сетевой, уверенно). <see cref="DriveType.Network"/> — сеть; локальные
    /// типы — не сеть; <see cref="DriveType.NoRootDirectory"/> и <see cref="DriveType.Unknown"/> —
    /// диска сейчас нет: так выглядит подключенный сетевой диск до восстановления после входа в
    /// систему. Если буква числится в постоянных подключениях пользователя
    /// (<paramref name="persistentMapping"/>), это уверенно сеть; иначе — сеть «до выяснения»,
    /// без кеша. Чистая функция — под юнит-тестом.
    /// </summary>
    public static (bool Network, bool Definitive) ClassifyDrive(DriveType type, bool persistentMapping) => type switch
    {
        DriveType.Network => (true, true),
        DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram => (false, true),
        _ => persistentMapping ? (true, true) : (true, false),
    };

    // Постоянные подключения сетевых дисков Windows («восстанавливать при входе») лежат в
    // HKCU\Network\<буква> — запись есть и тогда, когда сам диск еще не восстановлен.
    private static bool IsPersistentMapping(string root)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var letter = root.TrimEnd('\\', '/').TrimEnd(':');
        if (letter.Length != 1 || !char.IsLetter(letter[0])) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Network\" + letter);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    // /proc/mounts: «device mountpoint fstype opts …». Ищем точку монтирования с самым
    // длинным префиксом пути и смотрим тип ее ФС.
    private static bool IsLinuxNetworkMount(string full)
    {
        var netFs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "cifs", "smb3", "smbfs", "nfs", "nfs4", "fuse.sshfs", "davfs", "afpfs", "9p" };
        return netFs.Contains(LinuxMount(full).Type);
    }

    private static string? LinuxMountPoint(string full) => LinuxMount(full).Point;

    private static (string? Point, string Type) LinuxMount(string full)
    {
        var bestLen  = -1;
        string? bestMount = null;
        var bestType = "";
        foreach (var line in File.ReadLines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;
            var mount = parts[1].Replace("\\040", " ");   // пробелы экранированы октально
            if (full.StartsWith(mount, StringComparison.Ordinal) && mount.Length > bestLen)
            {
                bestLen   = mount.Length;
                bestMount = mount;
                bestType  = parts[2];
            }
        }
        return (bestMount, bestType);
    }
}
