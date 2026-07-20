using Npgsql;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Доступ к локальному стенду PostgreSQL (docker-контейнер <c>sb-pg</c>, порт 5455 —
/// см. заметки проекта про PG-стенд). Тесты провайдера PG — <c>SkippableFact</c>:
/// если стенд не поднят, <see cref="IsAvailable"/> = false и тесты помечаются Skipped,
/// а не падают. Базы данных стенда: <c>smartbase</c> и <c>smartbase2</c>.
/// </summary>
public static class PgTestServer
{
    public const string Host     = "localhost";
    public const int    Port     = 5455;
    public const string User     = "postgres";
    public const string Password = "postgres";

    public const string Database1 = "smartbase";
    public const string Database2 = "smartbase2";

    /// <summary>Строка подключения Npgsql к указанной базе стенда (начинается с «Host=»,
    /// что и распознаёт <c>AppDbContext.IsPostgres</c>).</summary>
    public static string ConnectionString(string database) =>
        $"Host={Host};Port={Port};Username={User};Password={Password};Database={database}";

    private static bool? _available;

    /// <summary>Проба доступности стенда (кешируется на процесс). Любая ошибка соединения
    /// трактуется как «стенд не поднят».</summary>
    public static bool IsAvailable => _available ??= Probe();

    private static bool Probe()
    {
        try
        {
            using var conn = new NpgsqlConnection(
                $"Host={Host};Port={Port};Username={User};Password={Password};" +
                "Database=postgres;Timeout=3;Command Timeout=3");
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
