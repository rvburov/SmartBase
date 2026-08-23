using Npgsql;

namespace SmartBase.Database;

/// <summary>
/// Параметры подключения к серверу PostgreSQL (вторая ветка провайдера рядом с файловым SQLite).
/// Единственное место, где собираются строки подключения Npgsql: благодаря этому
/// <see cref="IsPostgresConnectionString"/> — надежный признак «строка подключения, а не путь к файлу»
/// (пути к файлам БД не содержат «Host=»), и все таймауты/пул задаются в одной точке.
/// </summary>
public sealed record PgConnectionInfo(
    string Host,
    int    Port,
    string Database,
    string User,
    string Password,
    string SslMode)
{
    public const int    DefaultPort    = 5432;
    public const string DefaultSslMode = "Prefer"; // Disable | Prefer | Require

    /// <summary>Строка подключения Npgsql. Таймаут открытия 5 с — чтобы недоступный сервер
    /// не завешивал очередь монтирования и пробы монитора; Keepalive держит соединение
    /// живым через NAT/файрволы; пул ограничен — тысячи клиентов × большой пул исчерпали бы
    /// max_connections сервера.</summary>
    public string ToConnectionString()
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host            = Host,
            Port            = Port,
            Database        = Database,
            Username        = User,
            Password        = Password,
            Timeout         = 5,
            CommandTimeout  = 60,
            KeepAlive       = 30,
            MaxPoolSize     = 16,
            ApplicationName = "SmartBase",
        };
        if (Enum.TryParse<SslMode>(SslMode, ignoreCase: true, out var ssl))
            b.SslMode = ssl;
        return b.ConnectionString;
    }

    /// <summary>Отображаемый идентификатор без пароля — идет в Path записи реестра
    /// (дедупликация) и во вторую строку карточки БД в настройках.</summary>
    public string ToDisplayString() => $"postgres://{Host}:{Port}/{Database}";

    /// <summary>Строка подключения Npgsql, а не путь к файлу SQLite. Пути к файлам
    /// не могут содержать «Host=» — все PG-строки собираются только этим классом.</summary>
    public static bool IsPostgresConnectionString(string pathOrConnString)
        => pathOrConnString.Contains("Host=", StringComparison.OrdinalIgnoreCase);
}
