using Avalonia.Threading;
using Serilog;
using SmartBase.Database;

namespace SmartBase.Utils;

/// <summary>
/// Единая обработка ошибок записи в БД на пользовательских действиях (правка ячейки,
/// сохранение записи, CRUD сайдбара). В многопользовательском режиме файл может быть
/// занят чужим писателем дольше busy_timeout или пропасть на ходу — это не повод падать:
///  • BUSY/LOCKED — уведомление «база занята, изменение не сохранено» (штатная конкуренция);
///  • CANTOPEN/IOERR/NOTADB/CORRUPT и файловые ошибки — сигнал монитору доступности
///    (DbHealthMonitor: размонтирование, статус «недоступна», авто-переподключение) + уведомление;
///  • всё остальное — не наша ошибка, пробрасывается дальше (баг должен быть виден).
///
/// Использование: <c>catch (Exception ex) when (DbErrors.Handle(ex, repo.DbPath)) { }</c> —
/// фильтр либо гасит известные ошибки БД, либо пропускает исключение нетронутым.
/// Хуки ставит MainWindow при старте (уведомления и монитор живут там).
/// </summary>
public static class DbErrors
{
    /// <summary>Показ уведомления (MainWindow.Notify). Вызывается уже на UI-потоке.</summary>
    public static Action<string>? Notify;

    /// <summary>Сигнал монитору доступности (DbHealthMonitor.ReportFailureByPath).</summary>
    public static Action<string, Exception>? ReportFailure;

    /// <summary>true — ошибка известного класса, обработана (уведомление показано,
    /// монитор оповещён), пробрасывать не нужно. false — чужое исключение.</summary>
    public static bool Handle(Exception ex, string? dbPath)
    {
        if (DbHealthMonitor.IsBusyError(ex))
        {
            // Штатная конкуренция — не ошибка приложения, поэтому Warning, не Error.
            Log.Warning(ex, "БД занята при записи: {Path:l}", dbPath);
            Post("База данных занята другим пользователем — изменение не сохранено, попробуйте ещё раз.");
            return true;
        }
        if (DbHealthMonitor.IsUnavailableError(ex))
        {
            Log.Error(ex, "БД недоступна при записи: {Path:l}", dbPath);
            if (dbPath is not null) ReportFailure?.Invoke(dbPath, ex);
            Post("База данных недоступна — изменение не сохранено.");
            return true;
        }
        if (ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Строку, которую мы правили/удаляли, уже изменил или удалил другой писатель (фантомные
            // удаления гасятся раньше — в AppDbContext.SaveChanges; сюда доходит правка чужой строки).
            // Не баг приложения → Warning, а не Error; изменение не применилось, вид переоткроют.
            Log.Warning(ex, "Конкурентная правка при записи: {Path:l}", dbPath);
            Post("Запись изменил другой пользователь — изменение не сохранено, откройте заново.");
            return true;
        }
        return false;
    }

    /// <summary>Ошибка ЧТЕНИЯ (фоновая подгрузка страниц/сеток): без тостов — вид просто
    /// оставляет прежние данные; но «база пропала» сигналит монитору доступности, иначе
    /// UI молча показывал бы устаревшее до следующей плановой пробы.</summary>
    public static void HandleRead(Exception ex, string? dbPath)
    {
        if (dbPath is not null && DbHealthMonitor.IsUnavailableError(ex))
        {
            Log.Warning(ex, "БД недоступна при чтении: {Path:l}", dbPath);
            ReportFailure?.Invoke(dbPath, ex);
        }
    }

    // Ошибки прилетают и с фоновых потоков — уведомление маршалим на UI.
    private static void Post(string message) =>
        Dispatcher.UIThread.Post(() => Notify?.Invoke(message));
}
