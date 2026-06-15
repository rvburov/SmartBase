using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.EntityFrameworkCore;
using DataBase.Database;
using DataBase.Database.Repositories;
using DataBase.Utils;
using DataBase.Views;

namespace DataBase;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dbPath = AppSettings.DbPath;
            var db     = new AppDbContext(dbPath);

            // Все изменения схемы применяются автоматически через EF Core Migrations.
            // Любая будущая правка сущностей → `dotnet ef migrations add ...` →
            // при следующем запуске Migrate() накатит её, не трогая существующие данные.
            ApplySchemaMigrations(db);

            var categories  = new CategoryRepository(db);
            var constructor = new ConstructorRepository(db);

            categories.SeedDefaults();

            desktop.MainWindow = new MainWindow(categories, constructor);

            desktop.Exit += (_, _) => db.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Мост со старого EnsureCreated на EF Migrations.
    //
    // У ранних пользователей БД создавалась через EnsureCreated() — там нет таблицы
    // __EFMigrationsHistory, поэтому Migrate() попытается выполнить InitialCreate
    // и упадёт на «таблица уже существует». Чтобы этого избежать, перед Migrate()
    // вручную создаём таблицу истории и фиксируем в ней InitialCreate как
    // «уже применённую». После этого Migrate() накатит только последующие миграции.
    private static void ApplySchemaMigrations(AppDbContext db)
    {
        var dbFileExists = File.Exists(AppSettings.DbPath);

        // Случай 1: БД ещё не существует — Migrate() сам создаст её и применит все миграции по порядку.
        if (!dbFileExists)
        {
            db.Database.Migrate();
            return;
        }

        // Случай 2: БД есть. Проверяем, есть ли в ней таблица __EFMigrationsHistory
        // и реальные таблицы схемы (например, db_records — создаётся InitialCreate).
        var hasMigrationsTable = HasMigrationsHistoryTable(db);
        var hasSchemaTables    = HasTable(db, "db_records");

        if (!hasMigrationsTable && hasSchemaTables)
        {
            // Старая БД из времён EnsureCreated. Считаем, что её схема соответствует
            // InitialCreate, и помечаем эту миграцию как применённую — чтобы Migrate()
            // не пытался создавать таблицы повторно.
            db.Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId""    TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                    ""ProductVersion"" TEXT NOT NULL
                );
            ");
            db.Database.ExecuteSqlRaw(@"
                INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ('20260607161850_InitialCreate', '9.0.0');
            ");
        }

        // Теперь EF корректно увидит, какие миграции применены, и накатит недостающие.
        db.Database.Migrate();
    }

    private static bool HasMigrationsHistoryTable(AppDbContext db) =>
        HasTable(db, "__EFMigrationsHistory");

    private static bool HasTable(AppDbContext db, string tableName)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$name";
            p.Value = tableName;
            cmd.Parameters.Add(p);
            using var r = cmd.ExecuteReader();
            return r.Read();
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
