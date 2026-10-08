using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;

namespace SmartBase.Database;

/// <summary>
/// Системная база данных (app.db): реестр пользовательских БД (<c>databases</c>) и
/// настройки приложения (<c>app_settings</c>, ключ-значение).
/// Это независимое хранилище — оно никогда не показывается в сайдбаре и не удаляется.
/// Схема создается через EnsureCreated() (отдельный файл, конфликта с миграциями
/// контекста данных нет). Будущие изменения схемы — только аддитивно (CREATE TABLE IF
/// NOT EXISTS / ALTER ADD), без drop/recreate, чтобы не терять реестр и настройки
/// (EnsureCreated() не достраивает таблицы в уже существующей БД — см. DatabaseRegistry).
/// </summary>
public class SystemDbContext : DbContext
{
    public DbSet<DatabaseRefEntity> Databases { get; set; } = null!;
    public DbSet<AppSettingEntity>  Settings  { get; set; } = null!;

    /// <summary>Соединения интеграций — локальные, как и все настройки обмена (см. сущность).</summary>
    public DbSet<IntegrationConnectionEntity> IntegrationConnections { get; set; } = null!;

    /// <summary>Запросы интеграций — лежат рядом с соединениями, поодиночке они бессмысленны.</summary>
    public DbSet<IntegrationRequestEntity> IntegrationRequests { get; set; } = null!;

    /// <summary>Сценарии — там же: сценарий в файле базы сработал бы на каждой открытой копии.</summary>
    public DbSet<IntegrationScenarioEntity> IntegrationScenarios { get; set; } = null!;

    public DbSet<IntegrationDirectionEntity> IntegrationDirections { get; set; } = null!;

    /// <summary>Потоки событий — там же: поток в файле базы открыла бы каждая копия программы.</summary>
    public DbSet<IntegrationStreamEntity> IntegrationStreams { get; set; } = null!;

    /// <summary>Токены входящего канала — тоже локальные: порт слушает одна машина.</summary>
    public DbSet<IntegrationTokenEntity> IntegrationTokens { get; set; } = null!;

    /// <summary>Выдачи чтения — рядом с токенами: читает порт этой же машины.</summary>
    public DbSet<IntegrationGrantEntity> IntegrationGrants { get; set; } = null!;

    private readonly string _path;

    public SystemDbContext(string path) => _path = path;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite(SqliteGate.NewConnection($"Data Source={_path}"), contextOwnsConnection: true);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DatabaseRefEntity>(e =>
        {
            e.ToTable("databases");
            e.HasKey(x => x.Id);
        });

        model.Entity<AppSettingEntity>(e =>
        {
            e.ToTable("app_settings");
            e.HasKey(x => x.Key);
        });

        model.Entity<IntegrationConnectionEntity>(e =>
        {
            e.ToTable("integration_connections");
            e.HasKey(x => x.Id);
        });

        model.Entity<IntegrationRequestEntity>(e =>
        {
            e.ToTable("integration_requests");
            e.HasKey(x => x.Id);
        });

        model.Entity<IntegrationScenarioEntity>(e =>
        {
            e.ToTable("integration_scenarios");
            e.HasKey(x => x.Id);
        });

        model.Entity<IntegrationDirectionEntity>(e =>
        {
            e.ToTable("integration_directions");
            e.HasKey(x => x.RequestId);
        });

        model.Entity<IntegrationStreamEntity>(e =>
        {
            e.ToTable("integration_streams");
            e.HasKey(x => x.Id);
        });

        model.Entity<IntegrationTokenEntity>(e =>
        {
            e.ToTable("integration_tokens");
            e.HasKey(x => x.Id);
        });

        model.Entity<IntegrationGrantEntity>(e =>
        {
            e.ToTable("integration_grants");
            e.HasKey(x => x.Id);
        });
    }
}
