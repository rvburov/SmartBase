using Microsoft.EntityFrameworkCore;
using DataBase.Database.Entities;

namespace DataBase.Database;

/// <summary>
/// Системная база данных (app.db): реестр пользовательских БД (<c>databases</c>) и
/// настройки приложения (<c>app_settings</c>, ключ-значение).
/// Это независимое хранилище — оно никогда не показывается в сайдбаре и не удаляется.
/// Схема создаётся через EnsureCreated() (отдельный файл, конфликта с миграциями
/// контекста данных нет). Будущие изменения схемы — только аддитивно (CREATE TABLE IF
/// NOT EXISTS / ALTER ADD), без drop/recreate, чтобы не терять реестр и настройки
/// (EnsureCreated() не достраивает таблицы в уже существующей БД — см. DatabaseRegistry).
/// </summary>
public class SystemDbContext : DbContext
{
    public DbSet<DatabaseRefEntity> Databases { get; set; } = null!;
    public DbSet<AppSettingEntity>  Settings  { get; set; } = null!;

    private readonly string _path;

    public SystemDbContext(string path) => _path = path;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite($"Data Source={_path}");

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
    }
}
