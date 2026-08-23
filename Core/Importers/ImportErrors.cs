using Serilog;

namespace SmartBase.Core.Importers;

/// <summary>
/// Открытие файлов импорта и перевод системных исключений в дружелюбные <see cref="ImportException"/>
/// (показываются в баннере диалога). Здесь — только файловые ошибки; ошибки записи в БД переводит
/// репозиторий (слой Database). Mark-of-the-Web (файл «из интернета») чтению данных не мешает и
/// специально не обрабатывается — см. docs/features/import-export.md.
/// </summary>
public static class ImportErrors
{
    /// <summary>
    /// FileStream для чтения с FileShare.ReadWrite: файл, открытый в Excel, читается без
    /// конфликта; ошибка «занят» — только при эксклюзивной блокировке другим процессом.
    /// </summary>
    public static FileStream OpenRead(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is not ImportException) { throw Map(ex, path); }
    }

    /// <summary>Перевод исключения в ImportException. Уже дружелюбные возвращаются как есть.</summary>
    public static ImportException Map(Exception ex, string path)
    {
        // Уже дружелюбное — прошло логирование при первом переводе, не дублируем.
        if (ex is ImportException ie) return ie;
        // Единая точка перевода файловых ошибок импорта → сюда же и единый лог.
        Log.Warning(ex, "Ошибка импорта файла {Path:l}", path);

        var name = string.IsNullOrEmpty(path)
            ? ""
            : $" «{System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path))}»";

        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                new ImportException($"Файл{name} не найден или был удален во время импорта."),
            PathTooLongException =>
                new ImportException("Слишком длинный путь к файлу."),
            UnauthorizedAccessException =>
                new ImportException($"Нет прав доступа к{name}. Проверьте разрешения и повторите."),
            IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 =>   // sharing/lock violation
                new ImportException($"Файл{name} занят другой программой. Закройте его и повторите."),
            IOException io =>
                new ImportException($"Не удалось прочитать файл{name}: {io.Message}"),
            OutOfMemoryException =>
                new ImportException("Недостаточно памяти для импорта файла."),
            _ => new ImportException($"Не удалось импортировать данные{name}: {ex.Message}"),
        };
    }
}
