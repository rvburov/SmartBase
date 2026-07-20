using Serilog;

namespace SmartBase.Core.Exporters;

/// <summary>
/// Создание файлов экспорта и перевод системных исключений записи в дружелюбные
/// <see cref="ExportException"/> (показываются в баннере диалога). Здесь — только файловые
/// ошибки; ошибок БД у экспорта нет (чтение идёт через репозиторий, WAL допускает
/// параллельное чтение). Зеркало <see cref="Importers.ImportErrors"/>.
/// </summary>
public static class ExportErrors
{
    /// <summary>
    /// FileStream для записи. FileMode.CreateNew: имя уже уникализировано планировщиком,
    /// существующий файл — гонка с внешним процессом, честно падаем «занят/существует».
    /// FileShare.Read — недописанный файл можно открыть просмотром, но не заблокировать запись.
    /// readWrite=true — для OpenXml-пакета (.xlsx/.xlsm): ZIP-архив перечитывает свой каталог,
    /// на write-only потоке падает «The stream was not opened for reading».
    /// </summary>
    public static FileStream CreateFile(string path, bool readWrite = false)
    {
        try
        {
            return new FileStream(path, FileMode.CreateNew,
                readWrite ? FileAccess.ReadWrite : FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex) when (ex is not ExportException) { throw Map(ex, path); }
    }

    /// <summary>Перевод исключения в ExportException. Уже дружелюбные возвращаются как есть.</summary>
    public static ExportException Map(Exception ex, string path)
    {
        // Уже дружелюбное — прошло логирование при первом переводе, не дублируем.
        if (ex is ExportException ee) return ee;
        // Единая точка перевода файловых ошибок экспорта → сюда же и единый лог.
        Log.Warning(ex, "Ошибка экспорта файла {Path:l}", path);

        var name = string.IsNullOrEmpty(path)
            ? ""
            : $" «{System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path))}»";

        return ex switch
        {
            DirectoryNotFoundException =>
                new ExportException("Папка не найдена или была удалена во время экспорта."),
            PathTooLongException =>
                new ExportException("Слишком длинный путь к файлу."),
            UnauthorizedAccessException =>
                new ExportException("Нет прав на запись в папку. Выберите другую папку и повторите."),
            IOException io when (io.HResult & 0xFFFF) is 0x27 or 0x70 =>   // handle/disk full
                new ExportException("Недостаточно места на диске — экспорт отменён."),
            IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 or 0x50 =>   // sharing/lock/exists
                new ExportException($"Файл{name} занят другой программой или уже существует. Закройте его и повторите."),
            IOException io =>
                new ExportException($"Не удалось записать файл{name}: {io.Message}"),
            ArgumentException or NotSupportedException =>
                new ExportException($"Недопустимое имя файла{name}."),
            OutOfMemoryException =>
                new ExportException("Недостаточно памяти для экспорта."),
            _ => new ExportException($"Не удалось экспортировать данные{name}: {ex.Message}"),
        };
    }
}
