using System.ComponentModel.DataAnnotations;

namespace SmartBase.Database.Entities;

/// <summary>
/// Запись реестра баз данных (системная БД app.db, таблица «databases»).
/// Provider: "sqlite" — файловая БД, Path = путь к файлу; "postgres" — серверная БД,
/// параметры в Pg*-колонках, а Path хранит отображаемую строку «postgres://host:port/db»
/// (дедупликация и вторая строка карточки в настройках, без пароля).
/// Pg*-колонки добавлены аддитивно (см. DatabaseRegistry.EnsurePgColumns) — app.db
/// никогда не пересоздаётся. Пароль хранится открытым текстом: app.db лежит в профиле
/// пользователя с теми же правами доступа, что и файлы данных; защита (DPAPI/Keychain) —
/// осознанно отложенная задача.
/// </summary>
public class DatabaseRefEntity
{
    [Key] public int       Id        { get; set; }
    [Required] public string Name     { get; set; } = "";
    [Required] public string Path     { get; set; } = "";
    [Required] public string Provider { get; set; } = "sqlite";
    public bool            IsDefault { get; set; }

    public string? PgHost     { get; set; }
    public int?    PgPort     { get; set; }
    public string? PgDatabase { get; set; }
    public string? PgUser     { get; set; }
    public string? PgPassword { get; set; }
    public string? PgSslMode  { get; set; }

    public bool IsPostgres => Provider == "postgres";

    /// <summary>Параметры подключения PG-записи; null для SQLite-записей.</summary>
    public PgConnectionInfo? ToPgInfo() =>
        !IsPostgres || PgHost is null || PgDatabase is null ? null
            : new PgConnectionInfo(
                PgHost,
                PgPort ?? PgConnectionInfo.DefaultPort,
                PgDatabase,
                PgUser ?? "",
                PgPassword ?? "",
                PgSslMode ?? PgConnectionInfo.DefaultSslMode);
}
