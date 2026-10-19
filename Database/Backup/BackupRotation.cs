using System.Globalization;

namespace SmartBase.Database.Backup;

/// <summary>
/// Имена файлов-копий и ротация «хранить N последних». Логика чистая (списки путей и
/// дат приходят параметрами, диск не трогается) — сама уборка файлов на диске живет
/// в BackupService. Копии друг от друга отличает штамп времени в имени:
/// «&lt;имя базы&gt; (копия 2026-09-05_03-00).db» — тот же вид, что у прежней ручной кнопки.
/// Чужие файлы в папке (не подходящие под шаблон ИМЕННО этой базы) ротация не трогает:
/// пользователь мог сложить туда что угодно.
/// </summary>
public static class BackupRotation
{
    private const string CopyMarker = " (копия ";
    private const string StampFormat = "yyyy-MM-dd_HH-mm";

    /// <summary>Расширение копии по провайдеру записи реестра: SQLite — файл базы,
    /// PostgreSQL — сжатый дамп pg_dump (формат custom, восстанавливается pg_restore).</summary>
    public static string Extension(bool isPostgres) => isPostgres ? "dump" : "db";

    /// <summary>Имя базы, пригодное для файловой системы: недопустимые символы → «_»,
    /// пустое имя → «база» (без имени файл не собрать).</summary>
    public static string SafeName(string dbName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(dbName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "база" : safe;
    }

    /// <summary>Имя файла копии: «&lt;имя&gt; (копия yyyy-MM-dd_HH-mm).&lt;ext&gt;».</summary>
    public static string CopyFileName(string dbName, DateTime at, string extension) =>
        $"{SafeName(dbName)}{CopyMarker}{at.ToString(StampFormat, CultureInfo.InvariantCulture)}).{extension}";

    /// <summary>Файл — копия именно этой базы: точный префикс с именем, валидный штамп
    /// времени и нужное расширение. «base» не совпадает с копиями «base2»: у той после
    /// имени идет не « (копия ».</summary>
    public static bool IsCopyOf(string fileName, string dbName, string extension)
    {
        var prefix = SafeName(dbName) + CopyMarker;
        var suffix = ")." + extension;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

        var stamp = fileName[prefix.Length..^suffix.Length];
        return DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out _);
    }

    /// <summary>Недописанная копия этой базы: имя копии с суффиксом
    /// <see cref="DatabaseManager.BackupPartSuffix"/> (так копия лежит, пока не проверена).</summary>
    public static bool IsPartOf(string fileName, string dbName, string extension) =>
        fileName.EndsWith(DatabaseManager.BackupPartSuffix, StringComparison.Ordinal)
        && IsCopyOf(fileName[..^DatabaseManager.BackupPartSuffix.Length], dbName, extension);

    /// <summary>Что удалить, чтобы осталось не больше <paramref name="keep"/> новейших копий.
    /// Новизна — по штампу в имени, а не по mtime файла: копирование/перенос папки сбивает
    /// файловые даты, а штамп — нет. Список на входе — уже отфильтрованные
    /// <see cref="IsCopyOf"/> имена файлов одной базы.</summary>
    public static List<string> SelectForDeletion(IEnumerable<string> copyFileNames, int keep) =>
        [.. copyFileNames
            .OrderByDescending(n => n, StringComparer.Ordinal)   // штамп сортируется как строка
            .Skip(Math.Max(1, keep))];
}
