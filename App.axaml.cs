using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyPlanner.Database;
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

            var categories  = new CategoryRepository(db);
            var events      = new EventRepository(db);
            var constructor = new ConstructorRepository(db);

            categories.SeedDefaults();

            desktop.MainWindow = new MainWindow(events, categories, constructor);

            desktop.Exit += (_, _) => db.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
