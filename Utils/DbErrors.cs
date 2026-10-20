using Avalonia.Threading;
using Serilog;
using SmartBase.Database;
using SmartBase.Logging;

namespace SmartBase.Utils;

/// <summary>
/// Единая обработка ошибок записи в БД на пользовательских действиях (правка ячейки,
/// сохранение записи, CRUD сайдбара). В многопользовательском режиме файл может быть
/// занят чужим писателем дольше busy_timeout или пропасть на ходу — это не повод падать:
///  • BUSY/LOCKED — уведомление «база занята, изменение не сохранено» (штатная конкуренция);
///  • CANTOPEN/IOERR/NOTADB/CORRUPT и файловые ошибки — сигнал монитору доступности
///    (DbHealthMonitor: размонтирование, статус «недоступна», авто-переподключение) + уведомление;
///  • прерванная массовая копия (перенос не закончил удаление исходника, копию забрала уборка
///    соседа) — уведомление с текстом исключения;
///  • все остальное — не наша ошибка, пробрасывается дальше (баг должен быть виден).
///
/// Использование: <c>catch (Exception ex) when (DbErrors.Handle(ex, repo.DbPath)) { }</c> —
/// фильтр либо гасит известные ошибки БД, либо пропускает исключение нетронутым.
/// Хуки ставит MainWindow при старте (уведомления и монитор живут там).
/// </summary>
public static class DbErrors
{
    /// <summary>Показ уведомления (MainWindow.Notify). Вызывается уже на UI-потоке.</summary>
    public static Action<string>? Notify;

    /// <summary>Показ окна «действие запрещено правами» (MainWindow → PermissionNotice).
    /// Отдельно от тоста: отказ по правам — ответ на осознанное действие человека,
    /// его нужно прочитать, а не заметить краем глаза.</summary>
    public static Action<string>? NotifyDenied;

    /// <summary>Сигнал монитору доступности (DbHealthMonitor.ReportFailureByPath).</summary>
    public static Action<string, Exception>? ReportFailure;

    /// <summary>Сигнал монитору «сетевая база ответила BUSY» (DbHealthMonitor.ReportBusyByPath):
    /// после перезагрузки сервера SQLite отвечает BUSY на мертвые хендлы, и только свежая
    /// проба отличает это от штатной конкуренции писателей.</summary>
    public static Action<string>? ReportBusy;

    /// <summary>true — ошибка известного класса, обработана (уведомление показано,
    /// монитор оповещен), пробрасывать не нужно. false — чужое исключение.</summary>
    public static bool Handle(Exception ex, string? dbPath)
    {
        if (ex is Core.Models.PermissionDeniedException)
        {
            // Действие запрещено правами: интерфейс обычно объясняет это еще до вызова
            // (PermissionNotice в меню), сюда доходят обходные пути и гонки — право
            // отозвали, пока меню/окно были открыты. Не ошибка приложения → Warning.
            Log.Warning(ex, "Действие запрещено правами: {Path:l}", Display(dbPath));
            Dispatcher.UIThread.Post(() => (NotifyDenied ?? Notify)?.Invoke(ex.Message));
            return true;
        }
        if (DbHealthMonitor.IsBusyError(ex))
        {
            // Штатная конкуренция — не ошибка приложения, поэтому Warning, не Error. Но на
            // сетевой базе BUSY бывает и признаком мертвых хендлов после перезагрузки сервера —
            // монитор проверит файл свежим соединением (см. ReportBusy).
            Log.Warning(ex, "БД занята при записи: {Path:l}", Display(dbPath));
            if (dbPath is not null) ReportBusy?.Invoke(dbPath);
            Post("База данных занята другим пользователем — изменение не сохранено, попробуйте еще раз.");
            return true;
        }
        if (DbHealthMonitor.IsUnavailableError(ex))
        {
            // Повреждение файла зовем своим именем: это не сеть и не занятость, а данные.
            if (DbHealthMonitor.IsCorruptionError(ex))
                Log.Error(ex, "Файл базы поврежден (запись): {Path:l} — нужна проверка целостности или резервная копия", Display(dbPath));
            else
                Log.Error(ex, "БД недоступна при записи: {Path:l}", Display(dbPath));
            if (dbPath is not null) ReportFailure?.Invoke(dbPath, ex);
            Post("База данных недоступна — изменение не сохранено.");
            return true;
        }
        if (ex is Database.Repositories.ConstructorRepository.MoveIncompleteException
                  or Database.Repositories.ConstructorRepository.PendingLostException)
        {
            // Массовая копия (перенос, дубль) прервана с исходом, который пользователю нужно знать:
            // копия готова, а исходник доудалится при следующем подключении; копию сочли брошенной
            // и убрала уборка соседа. Текст исключения и есть сообщение.
            Log.Warning(ex, "Массовая копия не закончена: {Path:l}", Display(dbPath));
            Post(ex.Message);
            return true;
        }
        if (ex is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Строку, которую мы правили/удаляли, уже изменил или удалил другой писатель (фантомные
            // удаления гасятся раньше — в AppDbContext.SaveChanges; сюда доходит правка чужой строки).
            // Не баг приложения → Warning, а не Error; изменение не применилось, вид переоткроют.
            Log.Warning(ex, "Конкурентная правка при записи: {Path:l}", Display(dbPath));
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
        if (dbPath is null) return;
        if (DbHealthMonitor.IsUnavailableError(ex))
        {
            // Серия отказов чтения (каждый скролл при пропавшей базе) — одна запись в минуту
            // на базу: первый отказ виден, остальные не забивают журнал.
            if (Diag.Throttle("read-unavailable:" + dbPath, ReadLogPeriod))
            {
                if (DbHealthMonitor.IsCorruptionError(ex))
                    Log.Error(ex, "Файл базы поврежден (чтение): {Path:l}", Display(dbPath));
                else
                    Log.Warning(ex, "БД недоступна при чтении: {Path:l}", Display(dbPath));
            }
            ReportFailure?.Invoke(dbPath, ex);
        }
        else if (DbHealthMonitor.IsBusyError(ex))
        {
            // Чтение уперлось в BUSY: чужой коммит держит файл дольше busy_timeout (редкость)
            // или хендлы мертвы после перезагрузки сервера — пусть монитор разберется.
            if (Diag.Throttle("read-busy:" + dbPath, ReadLogPeriod))
                Log.Warning(ex, "Чтение уперлось в занятую базу: {Path:l}", Display(dbPath));
            ReportBusy?.Invoke(dbPath);
        }
    }

    private static readonly TimeSpan ReadLogPeriod = TimeSpan.FromMinutes(1);

    /// <summary>Путь для журнала: строка подключения PostgreSQL содержит пароль — ее не пишем.</summary>
    public static string Display(string? dbPath) =>
        dbPath is null ? "(нет пути)"
        : PgConnectionInfo.IsPostgresConnectionString(dbPath) ? "PostgreSQL"
        : dbPath;

    // Ошибки прилетают и с фоновых потоков — уведомление маршалим на UI.
    private static void Post(string message) =>
        Dispatcher.UIThread.Post(() => Notify?.Invoke(message));
}
