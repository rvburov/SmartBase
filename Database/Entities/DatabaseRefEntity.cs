using System.ComponentModel.DataAnnotations;

namespace SmartBase.Database.Entities;

/// <summary>
/// Запись реестра баз данных (системная БД app.db, таблица «databases»).
/// Provider: "sqlite" — файловая БД, Path = путь к файлу; "postgres" — серверная БД,
/// параметры в Pg*-колонках, а Path хранит отображаемую строку «postgres://host:port/db»
/// (дедупликация и вторая строка карточки в настройках, без пароля).
/// Pg*-колонки добавлены аддитивно (см. DatabaseRegistry.EnsurePgColumns) — app.db
/// никогда не пересоздаётся. PgPassword хранится в защищённом виде через
/// <see cref="SmartBase.Utils.SecretProtector"/> (macOS — Связка ключей, Windows — DPAPI;
/// Linux/сбой хранилища — открытый текст); в PgConnectionInfo пароль расшифровывает
/// <see cref="ToPgInfo"/> — единственная точка чтения.
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
                Utils.SecretProtector.Unprotect(PgPassword ?? ""),
                PgSslMode ?? PgConnectionInfo.DefaultSslMode);
}
