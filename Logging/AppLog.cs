using Serilog;
using Serilog.Events;
using SmartBase.Utils;

namespace SmartBase.Logging;

/// <summary>
/// Диагностический лог приложения: Serilog пишет структурированные записи (уровень,
/// сообщение, исключение) в отдельную базу <c>logs.db</c> рядом с app.db (см.
/// <see cref="AppSettings.LogsDbPath"/>). Единый файл на приложение, не per-database —
/// поэтому логгер статический (как AppSettings/DatabaseRegistry), DI не используется.
///
/// Читает логи разработчик: внутри приложения — раздел «Профиль → Логи» (ProfileLogsTable),
/// снаружи — открыв logs.db в DB Browser for SQLite. Существующий crash.log (Program.cs)
/// остается независимым резервом на случай сбоя до/после инициализации Serilog.
///
/// Таблица <c>logs</c> создается приемником автоматически при первой записи; столбцы —
/// Timestamp/Level/RenderedMessage/Exception/Properties (схему задает Serilog.Sinks.SQLite,
/// ее же читает Logging/LogReader).
/// </summary>
public static class AppLog
{
    /// <summary>Настроить и запустить глобальный логгер. Вызывать один раз на старте
    /// (Program.Main), после создания папки данных (AppSettings.EnsureCreated).</summary>
    public static void Init()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.SQLite(
                sqliteDbPath: AppSettings.LogsDbPath,
                tableName: "logs",
                restrictedToMinimumLevel: LogEventLevel.Information,
                storeTimestampInUtc: true,
                // batchSize=1: писать каждую запись сразу (~0.1 c), а не копить пачкой по
                // 100 или ждать таймер сброса ~10 c. Для диагностики объем мал — эффективность
                // пакетов не нужна, зато свежие ошибки видны в разделе «Логи» мгновенно и
                // почти не теряются при аварийном завершении (окно до сброса ничтожно).
                batchSize: 1,
                // Автоочистка: записи старше 30 дней приемник удаляет сам — logs.db не разрастется.
                retentionPeriod: TimeSpan.FromDays(30))
            .CreateLogger();
    }

    /// <summary>Дописать буфер и закрыть приемник при выходе (Program.Main, finally).</summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
