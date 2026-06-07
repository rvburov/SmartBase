using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyPlanner.Database;
using MyPlanner.Database.Entities;
using MyPlanner.Database.Repositories;
using MyPlanner.Utils;
using MyPlanner.ViewModels;
using MyPlanner.Views;

namespace MyPlanner;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dbPath = AppSettings.DbPath;
            var db     = new AppDbContext(dbPath);
            db.Database.EnsureCreated();

            MigrateDescriptionToMarkdown(db);

            var categories  = new CategoryRepository(db);
            var events      = new EventRepository(db);
            var constructor = new ConstructorRepository(db);

            categories.SeedDefaults();

            desktop.MainWindow = new MainWindow(events, categories, constructor);

            desktop.Exit += (_, _) => db.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Однократная миграция: все «Описание» переводим в markdown и
    // добиваем «Описание» в коллекциях, где его ещё нет.
    private static void MigrateDescriptionToMarkdown(AppDbContext db)
    {
        var changed = false;

        foreach (var f in db.DbFields.Where(f => f.Name == "Описание" && f.FieldType != "markdown"))
        {
            f.FieldType = "markdown";
            f.IsSystem  = true;
            changed = true;
        }

        var collectionIds = db.DbCollections.Select(c => c.Id).ToList();
        foreach (var colId in collectionIds)
        {
            var hasDesc = db.DbFields.Any(f => f.CollectionId == colId && f.Name == "Описание");
            if (hasDesc) continue;

            var maxPos = db.DbFields
                .Where(f => f.CollectionId == colId)
                .Select(f => (int?)f.Position)
                .Max() ?? 0;

            db.DbFields.Add(new DbFieldEntity
            {
                CollectionId = colId,
                Name         = "Описание",
                FieldType    = "markdown",
                Position     = Math.Max(maxPos + 1000, 2000),
                IsSystem     = true,
                IsRequired   = false,
            });
            changed = true;
        }

        if (changed) db.SaveChanges();
    }
}
