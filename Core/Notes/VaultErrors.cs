using Serilog;

namespace SmartBase.Core.Notes;

/// <summary>
/// Перевод системных исключений в человеческий текст — зеркало <see cref="Exporters.ExportErrors"/>,
/// с логом в той же точке. Раздел заметок работает с файлами на диске пользователя, поэтому
/// сломаться может что угодно: диск отключили, файл занят синхронизатором, прав нет.
/// </summary>
public static class VaultErrors
{
    public static string Describe(Exception ex, string path)
    {
        // Единая точка перевода файловых ошибок раздела — сюда же и единый лог.
        Log.Warning(ex, "Ошибка файловой операции заметок {Path:l}", path);

        var name = string.IsNullOrEmpty(path)
            ? ""
            : $" «{System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path))}»";

        return ex switch
        {
            DirectoryNotFoundException =>
                "Папка не найдена — возможно, ее удалили или отключили диск.",
            FileNotFoundException =>
                $"Файл{name} не найден — возможно, его удалила другая программа.",
            PathTooLongException =>
                "Слишком длинный путь к файлу. Перенесите хранилище ближе к корню диска.",
            UnauthorizedAccessException =>
                "Нет прав на изменение. Проверьте доступ к папке хранилища.",
            IOException io when (io.HResult & 0xFFFF) is 0x27 or 0x70 =>   // handle/disk full
                "Недостаточно места на диске.",
            IOException io when (io.HResult & 0xFFFF) is 0x20 or 0x21 or 0x50 =>   // sharing/lock/exists
                $"Файл{name} занят другой программой или уже существует. Закройте его и повторите.",
            IOException io =>
                $"Не удалось выполнить действие{name}: {io.Message}",
            ArgumentException or NotSupportedException =>
                $"Недопустимое имя{name}.",
            OutOfMemoryException =>
                "Недостаточно памяти — файл слишком большой.",
            _ => $"Не удалось выполнить действие{name}: {ex.Message}",
        };
    }
}
