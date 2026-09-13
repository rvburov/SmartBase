using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Serilog;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Utils;
using SmartBase.Views;

namespace SmartBase;

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

            // Необработанное исключение на UI-потоке (обработчик клика, диспетчерская задача,
            // продолжение async void — например, тайм-аут буфера обмена внутри TextBox.Copy)
            // раньше выходило из цикла сообщений и завершало процесс (crash.log «ui-loop»).
            // Ловим здесь: след остается в crash.log и logs.db, человеку — карточка, работа
            // продолжается. Точечные catch в UI (DbErrors и др.) остаются основной защитой,
            // это последний рубеж внутри процесса.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Program.LogCrash("ui-handled", e.Exception);
                e.Handled = true;
                Controls.Widgets.AppNotifications.Error(
                    $"Непредвиденная ошибка: {e.Exception.Message} " +
                    "Подробности записаны в журнал (Настройки → Диагностика).");
            };

            // Прибрать залежавшийся temp-артефакт обновления (~122 МБ) — фоном, чтобы не
            // задерживать старт. См. docs/guides/auto-update.md.
            _ = Task.Run(Setup.InstallService.CleanStaleUpdateArtifacts);

            // Системный реестр БД (app.db). Регистрирует текущую БД как дефолтную.
            DatabaseRegistry registry;
            try { registry = DatabaseRegistry.Open(AppSettings.DbPath); }
            catch (Exception ex)
            {
                // Без app.db нет ни реестра баз, ни настроек — старт невозможен. След в журнале
                // с путем: обычно это права на папку данных или поврежденный файл.
                Log.Error(ex, "Системная база app.db не открылась: {Path:l}", AppSettings.SystemDbPath);
                throw;
            }

            // Настройки приложения хранятся в app.db.
            AppSettings.AttachStore(registry);

            // Политика организации для обмена с чужими программами — сразу после AttachStore
            // и раньше всего остального: снимок последней известной политики лежит в app.db,
            // а шлюз наружу обязан быть заряжен до появления первого желающего выйти. До
            // этого вызова дверь закрыта (состояние EgressGate по умолчанию — самое строгое),
            // поэтому сбой загрузки ничего не открывает.
            // См. docs/developer/features/integrations/overview.md.
            Integrations.EgressSetup.Init();

            // Журнал запусков обмена — сразу за политикой: он же считает накопительные квоты
            // (запусков в час, байт в сутки), и без него потолки политики не с чем сверять.
            // Сбой открытия журнала обмен не останавливает: шлюз при отсутствии счетчика
            // считает расход нулевым, а про сбой узнает «Диагностика».
            try
            {
                var journal = new Database.RunJournal(AppSettings.RunJournalPath);
                journal.Trim();   // уборка раз в запуск: расти файлу незачем
                Database.RunJournal.Attach(journal);
                Integrations.EgressGate.AttachUsage(journal.AsUsage());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Журнал обмена не открылся: {Path:l}", AppSettings.RunJournalPath);
            }

            // Журнал безопасности — тот, который читает проверяющий. В отличие от рабочего, его
            // сбой не «переживается»: правило «не записалось — не отправляем» опирается на то,
            // что журнал есть. Не открылся — исходящего просто не будет, и сказать об этом надо
            // громче, чем про рабочий.
            try
            {
                Database.SecurityJournal.Attach(
                    new Database.SecurityJournal(AppSettings.SecurityJournalPath));

                // Уборка по сроку из политики — только ЗДЕСЬ, после того как журнал открыт.
                // Политика читается раньше (строка выше), и звать уборку оттуда нельзя: журнала
                // в тот момент еще нет. Срок при этом по-прежнему живет в одном месте — в файле
                // политики, и программа лишь исполняет его.
                Integrations.EgressSetup.TidyJournal();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Журнал безопасности НЕ открылся: {Path:l}. Исходящий обмен "
                            + "работать не будет — отправка без следа не допускается",
                          AppSettings.SecurityJournalPath);
            }

            // Память о повторах входящего канала — рядом с рабочим журналом, в том же файле.
            // Ее сбой прием не останавливает: повтор в худшем случае обновит те же записи теми
            // же значениями, а отказать из-за недоступной памяти значило бы остановить обмен.
            try
            {
                var replies = new Database.IngressReplies(AppSettings.RunJournalPath);
                replies.Forget(DateTimeOffset.Now);   // уборка раз в запуск, как у журнала
                Database.IngressReplies.Attach(replies);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Память о повторах приема не открылась: {Path:l}",
                            AppSettings.RunJournalPath);
            }

            // Расширения: Id — ключ и строки сайдбара, и группы настроек «ext.<Id>.…»,
            // поэтому тезки недопустимы (проверка падает с явным сообщением).
            Extensions.ExtensionRegistry.EnsureUniqueIds();

            // Автоматизации находятся по сборке, а их ключ хранится в шаге сценария: тезки
            // означали бы шаг, выполняющий не то, что настроили.
            Extensions.Automations.AutomationRegistry.EnsureUniqueKeys();

            // Убрать настройки расширений, которых в сборке больше нет: удалили папку
            // расширения и строку реестра — при первом же запуске уходят и его строки из
            // app.db, лишнего не остается. См. docs/features/extensions.md.
            var orphans = AppSettings.RemoveOrphanExtensionSettings(
                Extensions.ExtensionRegistry.All.Select(e => e.Id));
            if (orphans > 0)
                Log.Information("Настройки удаленных расширений убраны из app.db: {Count}", orphans);

            // Настройки резервного копирования отключенных баз: расписание и отметки базы,
            // которой больше нет в реестре, не должны жить в app.db вечно.
            var backupOrphans = AppSettings.RemoveOrphanBackupSettings(
                registry.GetAll().Select(d => d.Id));
            if (backupOrphans > 0)
                Log.Information("Настройки резервного копирования отключенных баз убраны из app.db: {Count}",
                                backupOrphans);

            // Там же — галочки «Расширения → Отображение раздела расширений»: список пунктов
            // динамический (Id реестра), поэтому Id удаленного расширения не должен остаться
            // в «скрытых». Иначе вернувшееся расширение молча оказалось бы скрытым.
            var hiddenOrphans = AppSettings.PruneHiddenViews(
                Settings.ViewCatalog.ScopeExtensions,
                Extensions.ExtensionRegistry.All.Select(e => e.Id));
            if (hiddenOrphans > 0)
                Log.Information("Скрытые расширения, которых больше нет, убраны из app.db: {Count}",
                                hiddenOrphans);

            // Автозапуск: сверяем запись в системе с настройкой — вернуть пропавшую (переустановка,
            // чистилка автозагрузки), переписать устаревшую (сменилась папка установки; у версий до
            // 1.0.5 в команде оставался --background), снять лишнюю. Именно здесь, а не в Program:
            // настройка читается только после AttachStore.
            Autostart.Sync(AppSettings.LoadAutostartEnabled());

            // Профиль пользователя (ФИО для модуля «Профиль») — тоже в app.db.
            CurrentUser.AttachStore(registry);

            // Размер шрифта: применить сохраненную дельту до создания окна (потребители на
            // DynamicResource подхватят актуальные размеры сразу при загрузке).
            FontScale.Init();

            // Настройки календаря: считать кэш до создания окна — календарь строится по ним.
            CalendarSettings.Init();
            // Настройки раздела «Задачи» — по той же причине (стартовый фильтр, правила показа).
            TaskSettings.Init();

            // Уровень масштабирования: прочитать сохраненный масштаб до создания окна
            // (MainWindow применит его к корню при инициализации).
            Scaling.Load();

            // Сплэш открывает ТОЛЬКО дефолтную БД (после миграции — мгновенно), остальные
            // базы реестра монтирует фоновая очередь уже после показа главного окна (см.
            // MountQueue и статусы в сайдбаре) — старт всегда быстрый. Миграция самой
            // дефолтной БД может быть долгой, поэтому ее делаем в фоне под сплэшем. Пока
            // идет монтирование, приложение не должно закрыться от отсутствия главного
            // окна — держим OnExplicitShutdown, вернем после.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Старт всегда одинаковый — со сплэшем и показанным окном, в том числе из
            // автозапуска: фоновый режим отвечает за «крестик» и трей, а не за то, каким
            // приложение приходит при входе в систему (см. features/autostart-background).
            var splash = new SplashWindow();
            splash.Show();

            // Шкала: монтирование дефолтной БД — 5–85% (живая доля этапа из stage-колбэков
            // DatabaseManager: миграция/сжатие/индексация), «Запуск интерфейса…» — 85→100%
            // (добег в Finish).
            var progress = new Progress<(double Fraction, string Message)>(t =>
                splash.SetStage(0.05 + 0.80 * t.Fraction, t.Message));

            _ = Task.Run(() =>
            {
                try
                {
                    var workspace = Workspace.Open(registry, progress);

                    // Первый запуск на чистой машине (ни одной базы данных не было) —
                    // вводный чек-лист по разделам программы. Здесь, а не в модуле
                    // календаря: решает состояние установки, а не содержимое базы, и
                    // данные готовы до построения окна. Ошибку глотает сам IntroSeed.
                    IntroSeed.RunOnFreshInstall(registry, workspace.Default);

                    Dispatcher.UIThread.Post(() =>
                    {
                        void Build()
                        {
                            var main = new MainWindow(workspace);
                            desktop.MainWindow = main;
                            main.Show();
                            splash.Close();
                            // Норма восстановлена: закрытие главного окна завершает приложение
                            // (в фоновом режиме окно не закрывается, а скрывается — см. MainWindow).
                            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

                            // Самозамена при обновлении не удалась (файл был занят) — версия
                            // осталась прежней. Разовое уведомление, чтобы пользователь не думал,
                            // что обновился. Подробности — docs/guides/auto-update.md.
                            if (Program.UpdateFailedReason is { } failedReason)
                            {
                                Log.Warning("Обновление не применилось ({Reason:l}) — версия прежняя: {Version:l}",
                                            failedReason, AppSettings.AppVersion);
                                _ = ConfirmDialog.Info(Setup.InstallService.UpdateFailedMessage(failedReason)).ShowDialog(main);
                            }

                            // Повторный запуск (второй экземпляр) просит показать окно.
                            if (Program.Instance is { } instance)
                            {
                                instance.StartListening();
                                instance.ShowRequested += () => Dispatcher.UIThread.Post(() =>
                                {
                                    Log.Information("Повторный запуск: второй экземпляр отклонен, окно работающего показано");
                                    main.Show();
                                    main.Activate();
                                });
                            }

                            desktop.Exit += (_, _) =>
                            {
                                var mounted = workspace.Mounted.Count;
                                // Файл единственного экземпляра — первым делом: если закрытие сетевой
                                // базы зависнет, повторный запуск (и перезапуск апдейтером) не должен
                                // молча выходить из-за занятого instance.lock.
                                Program.Instance?.Dispose();
                                // Базы — с пределом времени и в фоне: закрытие файла на зависшей
                                // папке не возвращается, а выход из-за этого не должен стоять
                                // (см. Workspace.CloseAll).
                                var closed = workspace.CloseAll(Workspace.ShutdownBudget);
                                registry.Dispose();
                                Program.NotificationManager?.Dispose();
                                // Запись о штатном выходе: ее отсутствие после «Запуск SmartBase»
                                // выдает убитый процесс или выключение питания.
                                Log.Information("Завершение SmartBase ({Reason:l}): работало {Uptime:l}, баз закрыто {Count}{Tail:l}",
                                    Program.ExitReason ?? "закрытие окна",
                                    Logging.Diag.Duration(DateTime.Now - Program.StartedAt), closed,
                                    closed < mounted ? $" из {mounted} — остальные не ответили за {Logging.Diag.Duration(Workspace.ShutdownBudget)}" : "");
                            };
                        }

                        // Плавно добегаем до 100%, и ТОЛЬКО потом строим/показываем главное окно.
                        splash.Finish("Запуск интерфейса…", Build);
                    });
                }
                catch (Exception ex)
                {
                    // Сбой открытия дефолтной БД (сбои остальных проглатывает Workspace.Mount):
                    // показываем причину на сплэше и корректно завершаемся, а не висим пустым окном.
                    Log.Error(ex, "Не удалось открыть дефолтную базу данных на старте");
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
