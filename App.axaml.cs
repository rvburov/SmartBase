using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

            // Монтирование БД (миграции/бэкафилл) на больших базах может занять секунды-десятки
            // секунд, поэтому НЕ блокируем поток UI пустым окном: показываем сплэш с прогрессом,
            // а тяжёлую работу делаем в фоне. Пока идёт монтирование, приложение не должно
            // закрыться от отсутствия главного окна — держим OnExplicitShutdown, вернём после.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splash = new SplashWindow();
            splash.Show();

            // Прогресс шлёт текст этапа (какая база); полосу двигает сам сплэш (плавная анимация).
            var progress = new Progress<(double Fraction, string Message)>(t => splash.SetStatus(t.Message));

            _ = Task.Run(() =>
            {
                try
                {
                    var workspace = Workspace.Open(registry, progress);
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Плавно добегаем до 100%, и ТОЛЬКО потом строим/показываем главное окно.
                        splash.Finish("Запуск интерфейса…", () =>
                        {
                            var main = new MainWindow(workspace);
                            desktop.MainWindow = main;
                            main.Show();
                            splash.Close();
                            // Норма восстановлена: закрытие главного окна завершает приложение.
                            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

                            desktop.Exit += (_, _) =>
                            {
                                workspace.Dispose();
                                registry.Dispose();
                            };
                        });
                    });
                }
                catch (Exception ex)
                {
                    // Сбой открытия дефолтной БД (сбои остальных проглатывает Workspace.Mount):
                    // показываем причину на сплэше и корректно завершаемся, а не висим пустым окном.
                    Dispatcher.UIThread.Post(() =>
                    {
                        splash.SetError($"Не удалось открыть базу данных:\n{ex.Message}");
                        DispatcherTimer.RunOnce(() => desktop.Shutdown(1), TimeSpan.FromSeconds(6));
                    });
                }
            });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
