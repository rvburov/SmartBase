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
            // Режим установщика/удаления (вариант A): открываем окно установки вместо
            // приложения, БД не трогаем. Подробности — docs/guides/auto-update.md.
            if (Program.Mode != Setup.SetupMode.App)
            {
                desktop.MainWindow = new Setup.InstallerWindow(Program.Mode == Setup.SetupMode.Uninstall);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Системный реестр БД (app.db). Регистрирует текущую БД как дефолтную.
            var registry = DatabaseRegistry.Open(AppSettings.DbPath);

            // Настройки приложения хранятся в app.db.
            AppSettings.AttachStore(registry);

            // Размер шрифта: применить сохранённую дельту до создания окна (потребители на
            // DynamicResource подхватят актуальные размеры сразу при загрузке).
            FontScale.Init();

            // Уровень масштабирования: прочитать сохранённый масштаб до создания окна
            // (MainWindow применит его к корню при инициализации).
            Scaling.Load();

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
