using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DataBase.Database;
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
            // Системный реестр БД (app.db). Регистрирует текущую БД как дефолтную.
            var registry = DatabaseRegistry.Open(AppSettings.DbPath);

            // Рабочее пространство: монтирует дефолтную БД (миграции/сидинг внутри DatabaseManager).
            var workspace = Workspace.Open(registry);

            desktop.MainWindow = new MainWindow(workspace);

            desktop.Exit += (_, _) =>
            {
                workspace.Dispose();
                registry.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
