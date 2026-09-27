using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Database.Backup;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Logging;
using SmartBase.Notifications;
using SmartBase.Settings;
using SmartBase.Utils;
using SmartBase.Core.Models;
using SmartBase.ViewModels;

namespace SmartBase.Views;

public partial class MainWindow : Window
{
    private readonly Workspace _workspace;
    // Фоновая очередь монтирования: старт открывает только дефолтную БД, остальные
    // догружаются здесь по одной; сайдбар показывает их заглушками со статусом.
    private readonly MountQueue _mountQueue;
    // Монитор доступности: общая база в сетевой папке может пропасть на ходу (сервер
    // выключили, файл удалили) — база размонтируется со статусом «недоступна» и
    // уведомлением, монитор сам ретраит и возвращает ее, когда файл снова доступен.
    private readonly DbHealthMonitor _health;
    // Фоновая проверка чужих правок (~3 с): другие пользователи общей базы (сетевая папка)
    // или другая копия приложения меняют данные — сайдбары/таблица/календарь обновляются сами.
    private readonly ChangePoller _changes;
    // БД, потерянные на ходу — чтобы при возврате показать «снова доступна».
    private readonly HashSet<int> _lostDbIds = [];
    // Служба уведомлений: пока окно открыто, показывает нативные баннеры о наступивших
    // событиях календаря и ведет значок-счетчик на иконке приложения.
    private readonly NotificationService _notifications;

    // Фоновый режим: значок в трее/меню-баре + скрытие окна вместо выхода. Значок виден, когда
    // фон включен; _explicitQuit — явный выход (трей «Выход» / ⌘Q), чтобы не скрывать окно.
    private TrayIcon? _tray;
    private bool _backgroundEnabled;
    private bool _explicitQuit;
    // Автопроверка обновления уже выполнялась в этой сессии (Opened приходит повторно при
    // возврате окна из трея — см. CheckUpdatesOnceAsync).
    private bool _updateChecked;
    private readonly CategoryRepository _categories;
    private readonly ConstructorRepository _constructor;
    private string _activeModule = "calendar";

    // Какая БД сейчас открыта в модуле конструктора (для панели записи и первой записи).
    private int _currentConstructorDbId;

    // Репозиторий конструктора текущей открытой БД (откат на дефолт, если не найдена).
    private ConstructorRepository CurrentConstructorRepo =>
        _workspace.Get(_currentConstructorDbId)?.Constructor ?? _constructor;

    // Три независимых состояния панелей. Физические ширины колонок и видимость ручек
    // ресайза вычисляются из них в одном месте — ApplyColumns().
    private bool _sidebarOpen    = true;   // левый сайдбар (col 2)
    private bool _collectionOpen = true;   // центральный блок коллекции/контента (col 3)

    // Иконка пунктов сайдбара расширений (lucide blocks, ключ из Icons.axaml) — одна
    // на все расширения, т.к. в ExtensionRegistry своей иконки у расширений нет.
    private const string ExtensionsIcon = "IconBlocks";

    // Состав сайдбара расширений на текущий момент — чтобы не перерисовывать список,
    // когда галочки видимости не трогали (см. SyncExtensionsSidebar).
    private IReadOnlyList<string> _extensionKeys = [];

    // Ключи разделов профиля, показанных сайдбару: по ним видно, менялся ли состав (SyncProfileSidebar).
    private IReadOnlyList<string> _profileKeys = [];
    // _recordPanelLogicalOpen — правая панель записи (col 5), объявлено ниже.

    public MainWindow(Workspace workspace)
    {
        InitializeComponent();

        _workspace   = workspace;
        _mountQueue  = new MountQueue(workspace);
        _health      = new DbHealthMonitor(workspace, _mountQueue);
        _changes     = new ChangePoller(workspace, _health);
        // Резервное копирование по расписанию (раздел «Настройки → Резервное копирование»).
        _backup      = new BackupService(workspace.Registry);
        // Прием файлов из папки обмена по расписанию («Настройки → Интеграции → Прием»).
        // Список баз передается вызываемым: потерянную базу монитор доступности размонтирует,
        // и правило «база недоступна — не запускаемся» держится само.
        _inbox       = new InboxService(GrantedDbs);
        // Прием по сети («Настройки → Интеграции → Доступ извне»). Порт открывается только
        // после того, как о нем легла строка в журнал безопасности: слушатель, о котором не
        // осталось записи, — ровно то, чего требование службы безопасности не допускает.
        _ingress     = new IngressService(GrantedDbs, AppSettings.SystemDbPath);
        // Сценарии по расписанию («Настройки → Интеграции»). Реестр сценариев лежит в app.db,
        // базы передаются вызываемым списком, сообщения шага идут карточками в углу окна.
        _scenarios   = new ScenarioService(workspace.Registry.Integrations, GrantedDbs,
                                           SmartBase.Extensions.ExtensionRegistry.All,
                                           Controls.Widgets.AppNotifications.Info);
        // Резолвер чужих баз для межбазовых связей (тип «relation» с TargetDbId>0).
        RelationDbResolver.Configure(workspace);

        // Сообщения человеку — карточки в правом верхнем углу окна (стопкой, сами гаснут).
        Controls.Widgets.AppNotifications.Install(this);

        // Сторож UI-потока: зависания интерфейса попадают в журнал диагностики с длительностью
        // и списком операций, которые шли в этот момент (см. Logging/UiWatchdog).
        _watchdog = new UiWatchdog();

        // Ошибки записи на пользовательских действиях (правка ячейки, CRUD): «занята» —
        // карточка + сигнал монитору (на сетевой базе BUSY бывает признаком мертвых хендлов
        // после перезагрузки сервера), «недоступна» — карточка + сигнал монитору. См. Utils/DbErrors.
        DbErrors.Notify        = Notify;
        DbErrors.ReportFailure = _health.ReportFailureByPath;
        DbErrors.ReportBusy    = _health.ReportBusyByPath;
        // Строки, которые мы правили, уже удалил другой пользователь общей базы: сохранение
        // прошло без них (AppDbContext.SaveReconcilingPhantomDeletes) — человек должен знать.
        AppDbContext.PhantomRowsDropped = _ => Notify(
            "Объект уже удален другим пользователем — часть изменений не применена. " +
            "Данные на экране обновятся через несколько секунд.");
        // Отказ по правам — окном с причиной (тост тут легко пропустить): так ведут себя
        // и запрещенные пункты меню, и замок у коллекции без права просмотра.
        DbErrors.NotifyDenied  = message => Controls.Dialogs.PermissionNotice.Show(this, message);
        // Календарь и панель записи пока работают с дефолтной БД (мульти-БД календаря — Этап 3.3).
        _categories  = workspace.Default.Categories;
        _constructor = workspace.Default.Constructor;
        _currentConstructorDbId = workspace.Default.DbId;

        // Служба уведомлений: события собираем по видимым БД (CollectDueEvents), баннеры и
        // значок — нативные (фабрика по ОС; хэндл окна нужен только Windows для overlay-иконки).
        _notifications = new NotificationService(
            PlatformNotifications.CreateNotifier(Program.NotificationManager),
            PlatformNotifications.CreateBadge(() => TryGetPlatformHandle()?.Handle ?? nint.Zero),
            CollectDueEvents,
            AppSettings.SaveNotifyWatermark,
            taskSource: new TaskEventReader(GrantedDbs));
        // Клик по баннеру (может прийти из фонового потока библиотеки/делегата) → активировать
        // окно и показать запись с кнопкой «Принято». Маршалим в UI-поток.
        _notifications.RecordActivated += r => Dispatcher.UIThread.Post(() => OnNotificationClicked(r));
        // Клик по баннеру задачи → раздел «Задачи» и карточка этой задачи.
        _notifications.TaskActivated   += e => Dispatcher.UIThread.Post(() => OnTaskNotificationClicked(e));

        // Фоновый режим и значок в трее/меню-баре (по настройке «Фоновый режим»).
        InitBackgroundMode();

        // Живой статус очереди: процент/оценка времени — только текст заглушки в сайдбарах
        // (без перестройки дерева), смена состояния — еще и список БД в настройках.
        // События очереди приходят из фонового потока — маршалим в UI.
        _mountQueue.Changed += (dbId, stateChanged) => Dispatcher.UIThread.Post(() =>
        {
            if (_mountQueue.StatusOf(dbId) is { } st)
            {
                var failed = st.State == MountQueue.ItemState.Failed;
                ConstructorSidebar.UpdatePendingStatus(dbId, st.Text, failed);
                CalendarSidebar.UpdatePendingStatus(dbId, st.Text, failed);
                TasksSidebar.UpdatePendingStatus(dbId, st.Text, failed);
                // Список БД в настройках живет теми же процентами/временем, что и сайдбар.
                _settingsView?.UpdateQueueStatus(dbId, st.Text, failed);
            }
            if (stateChanged) _settingsView?.RefreshDatabases();
        });
        _mountQueue.Completed += (dbId, manager) => Dispatcher.UIThread.Post(() =>
        {
            // Базу отключили в настройках, пока она монтировалась, — запись из реестра
            // уже удалена: тихо размонтируем, чтобы не осталось «призрака».
            if (manager is not null && _workspace.Registry.GetAll().All(d => d.Id != dbId))
            {
                _workspace.Unmount(dbId);
                return;
            }
            var returned = false;
            if (manager is null)
            {
                // Не смонтировалась (нет сети/файла) — отдаем монитору под фоновые ретраи:
                // сервер включится — база подключится сама, без действий пользователя.
                var entry = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
                if (entry is not null)
                    _health.TrackFailed(entry, _mountQueue.StatusOf(dbId)?.Reason ?? "не удалось подключиться");
            }
            else
            {
                _health.Forget(dbId);   // база живая — ретраи и счетчики ей больше не нужны
                returned = _lostDbIds.Remove(dbId);
            }
            SyncMountedToUi();   // узел «оживает»: календарь/задачи подхватывают данные
            // Вернувшаяся база: вместо всплывашки — надпись «снова доступна» под именем базы
            // в сайдбарах и в «Управление базой данных» (там же, где висело «недоступна…»).
            if (returned) ShowReturnedDbStatus(dbId);
        });

        // Потеря БД на ходу: размонтировать (как при отключении, но без удаления из
        // реестра), показать заглушку «недоступна» и уведомление; ретраит монитор.
        _health.DatabaseLost += (dbId, reason) =>
            Dispatcher.UIThread.Post(() => OnDatabaseLost(dbId, reason));

        // Файл общей базы держит другой процесс (серия BUSY, которую сброс пула не лечит):
        // надпись «занята другим компьютером» под именем базы, снимается, когда занятость прошла.
        _health.DatabaseBusy += (dbId, text) =>
            Dispatcher.UIThread.Post(() => SetDbStatus(dbId, text));

        // Чужие правки из другой копии приложения → обновить затронутые части UI.
        _changes.RemoteChanges += (dbId, rows) =>
            Dispatcher.UIThread.Post(() => OnRemoteChanges(dbId, rows));
        // Свои коммиты из любого места процесса (разделы, панель записи, окна расширений,
        // фоновые операции) → обновить те части UI, что читали данные раньше (см. FlushLocalChanges).
        ChangeBus.Committed += OnLocalCommit;

        // Службы → раздел настроек (ход копирования, обход папки обмена, порт, сценарии): только
        // построенному разделу — строящийся читает состояние служб сам.
        _backup.BackupStarted  += dbId => _settingsView?.OnBackupStarted(dbId);
        _backup.BackupProgress += (dbId, msg, frac) =>
            Dispatcher.UIThread.Post(() => _settingsView?.OnBackupProgress(dbId, msg, frac));
        _backup.BackupFinished += (dbId, ok, msg) => _settingsView?.OnBackupFinished(dbId, ok, msg);
        _inbox.Swept += run => _settingsView?.OnInboxSwept(run);
        _ingress.Changed += () => Dispatcher.UIThread.Post(() => _settingsView?.OnIngressChanged());
        _scenarios.Ran += (_, _) => _settingsView?.OnScenarioRan();
        _mountQueue.MaintenanceFinished += (dbId, error) => Dispatcher.UIThread.Post(() =>
        {
            var name = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId)?.Name ?? "";
            if (error is not null)
                Notify($"Сжать файл базы «{name}» не удалось: {error}");
            else if (_compactSizes.Remove(dbId, out var sizes))
                NotifyInfo($"Файл базы «{name}» сжат: {DatabaseRowSize(sizes.Before)} → {DatabaseRowSize(sizes.After)}.");
        });

        // Инициализируем модули через AXAML-конструкторы (остальные разделы — при первом показе,
        // MainWindow.Sections.cs).
        ConstructorModule.Initialize(_constructor);
        // Допущенные БД — для окна «Расположение» записи (перенос в другую коллекцию/БД).
        ConstructorModule.SetDatabases(GrantedDbs());

        // Точки вызова обмена в разделе данных. Охват задает МЕСТО: меню строки таблицы —
        // выделенные записи, меню коллекции в сайдбаре — вся коллекция, кнопка в нижней полосе
        // открытой записи — она одна.
        ConstructorModule.AttachIntegrations(workspace.Registry.Integrations, GrantedDbs);

        // Сайдбар: вся коллекция целиком, и потому здесь же ручные сценарии. Запускает их ТА ЖЕ
        // служба, что ходит по расписанию, — вторая дорога не отвечала бы на вопрос «а что будет
        // само».
        ConstructorSidebar.DataActions = (dbId, collectionId) =>
            Views.ConstructorDb.IntegrationActions.Build(
                this, workspace.Registry.Integrations, GrantedDbs(),
                (scenario, ct) => _scenarios.RunNowAsync(scenario, ct),
                dbId, collectionId, scenarios: true);

        // Панель записи: те же действия, но над одной показанной записью. Ноль вместо базы
        // означает «текущая» — панель могла быть открыта и межбазовым переходом, и тогда база
        // записи и база приложения РАЗНЫЕ.
        RecordPanel.DataActions = (dbId, collectionId) =>
            Views.ConstructorDb.IntegrationActions.Build(
                this, workspace.Registry.Integrations, GrantedDbs(), scenarioRun: null,
                dbId > 0 ? dbId : _currentConstructorDbId, collectionId, scenarios: false);

        // Боковые панели: календарь — только допущенные БД; конструктор — все смонтированные
        // (недопущенные показываются замком, клик по замку — запрос доступа).
        CalendarSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Calendar);
        TasksSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Tasks);
        LibrarySidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Library);
        ConstructorSidebar.InitializeWorkspace(workspace.Mounted, Controls.Components.SidebarMode.Constructor);
        ConstructorSidebar.DatabaseUnlockRequested += async (_, dbId) => await RequestDatabaseUnlock(dbId);
        // Клик по коллекции с замком (просмотр запрещен правами) — пояснение вместо
        // молчаливого «ничего не происходит».
        ConstructorSidebar.CollectionAccessDenied += async (_, name) =>
            await ConfirmDialog.Info(
                    $"Нет доступа к коллекции «{name}». Просмотр запрещен администратором " +
                    "базы данных — обратитесь к нему, чтобы получить право.")
                .ShowDialog(this);

        // Клик по заглушке немонтированной базы — подсказка с текущим статусом.
        ConstructorSidebar.PendingDatabaseTapped += async (_, dbId) => await ShowPendingDatabaseInfo(dbId);
        CalendarSidebar.PendingDatabaseTapped    += async (_, dbId) => await ShowPendingDatabaseInfo(dbId);
        TasksSidebar.PendingDatabaseTapped       += async (_, dbId) => await ShowPendingDatabaseInfo(dbId);

        // Живой состав пользователей уже смонтированных баз (дальше — из SyncMountedToUi).
        HookPermissionEvents();

        // ── Заметки ──────────────────────────────────────────────────────────────
        //
        // Раздел самостоятельный: базы данных он не касается вовсе, из общего — только путь к
        // папке-хранилищу в app.db. Пока папка не выбрана, дерево пустое, а раздел показывает
        // подсказку с кнопкой в нужный раздел настроек.
        //
        // Хранилище раздается ДО первого ShowModule: иначе при запуске сразу в «Заметках» раздел
        // открылся бы с пустым деревом и без прошлой заметки.
        NotesSidebar.SetVault(AppSettings.LoadNotesVault());

        // Начальное состояние: раздел при запуске берем из настроек (стандарт — конструктор).
        // Его могли скрыть в «Оформлении» уже после выбора — тогда открываем первый видимый,
        // иначе программа стартовала бы на разделе, кнопки которого в панели нет.
        var startupModule = AppSettings.LoadStartupModule();
        if (!ViewCatalog.IsVisible(ViewCatalog.ScopeNavigation, startupModule))
            startupModule = ViewCatalog.FirstVisible(ViewCatalog.ScopeNavigation)?.Key ?? startupModule;
        ActivityBarControl.SetModuleActive(startupModule);
        ActivityBarControl.SetSidebarOpen(true);
        ShowModule(startupModule);

        // Связываем ActivityBar
        ActivityBarControl.SidebarToggled += (_, isOpen) => { _sidebarOpen = isOpen; OnSidebarToggled(isOpen); };
        ActivityBarControl.ModuleChanged  += (_, module) => ShowModule(module);

        // Сайдбар настроек: пункты из SettingsRegistry; выбор раздела открывает его в основной области.
        SettingsSidebar.Title = "Настройки";
        SettingsSidebar.SetItems(SettingsRegistry.All.Select(c => new SidebarListItem(c.Key, c.Label, c.Icon)));
        SettingsSidebar.CategorySelected += (_, key) => _settingsView?.ShowCategory(key);

        // Сайдбар расширений: пункты из каталога — то есть из реестра расширений, но уже без
        // скрытых («Настройки → Расширения → Отображение раздела расширений»). Одна иконка на
        // все, выбор открывает блок; список пересобирается при заходе (SyncExtensionsSidebar).
        ExtensionsSidebar.Title = "Расширения";
        SyncExtensionsSidebar();
        ExtensionsSidebar.CategorySelected += (_, key) => _extensionsView?.ShowExtension(key);

        // Сайдбар профиля: пункты из ProfileRegistry; выбор раздела открывает его в основной области.
        ProfileSidebar.Title = "Профиль";
        SyncProfileSidebar();
        ProfileSidebar.CategorySelected += (_, key) => _profileView?.ShowCategory(key);

        NotesSidebar.NoteSelected        += (_, rel)  => NotesModule.OpenNote(rel);
        NotesSidebar.UnsupportedSelected += (_, name) => NotesModule.ShowUnsupported(name);
        NotesSidebar.NoteMoved           += (_, m)    => _notesView?.NotePathChanged(m.OldPath, m.NewPath);
        NotesSidebar.NoteRemoved         += (_, rel)  => _notesView?.NoteRemoved(rel);
        NotesSidebar.VaultChanged        += (_, _)    => _notesView?.Refresh();

        // Sidebar коллекций передает активные id в CalendarModule
        CalendarSidebar.ActiveIdsChanged += (_, ids) =>
            _calendarView?.SetActiveIds(ids);

        // Смена цвета коллекции в сайдбаре → перерисовать календарь и дерево конструктора
        CalendarSidebar.CalendarsChanged += (_, _) =>
        {
            _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
        };

        // Сайдбар задач: галочки решают, какие коллекции показывать (клик по строке —
        // та же галочка, как в календаре); тумблеры user-полей и смена цвета приходят
        // событием CalendarsChanged.
        TasksSidebar.ActiveIdsChanged += (_, ids) => _tasksView?.SetActiveIds(ids);
        TasksSidebar.CalendarsChanged += (_, _) =>
        {
            _tasksView?.SetActiveIds(TasksSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
        };
        // CRUD коллекций из сайдбара задач → обновить календарь и конструктор.
        TasksSidebar.RequestRefresh += (_, _) =>
        {
            CalendarSidebar.Refresh();
            _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
            ConstructorModule.Refresh();
        };
        // Сайдбар библиотеки: галочки решают, какие коллекции показывать. Тумблеры файловых
        // свойств тоже приходят через ActiveIdsChanged — в отличие от задач, где роль
        // эксклюзивна и о ней сообщает отдельное CalendarsChanged.
        LibrarySidebar.ActiveIdsChanged += (_, ids) => _libraryView?.SetActiveIds(ids);
        // Первое включение тумблера авто-создает «Тип документа», а кнопка «+» при настройке
        // «только подключенные» — еще и файловый столбец: обновить конструктор и его сайдбар.
        LibrarySidebar.RequestRefresh += (_, _) =>
        {
            ConstructorSidebar.Refresh();
            ConstructorModule.Refresh();
        };

        // «Прочитать документы» в меню коллекции: сайдбар только просит, читает раздел — очередь
        // и кеш текста его, а не сайдбара.
        LibrarySidebar.ReadDocumentsRequested += (_, r) =>
            LibraryModule.ReadCollection(r.DbId, r.Id);

        // Дерево конструктора: выбор коллекции открывает ее в ConstructorModule из ее БД.
        // Если центральный блок был скрыт (свернут из записи) — снова показываем его.
        ConstructorSidebar.CollectionSelected += (_, sel) =>
        {
            var db = _workspace.Get(sel.DbId) ?? _workspace.Default;
            _currentConstructorDbId = db.DbId;
            ConstructorModule.OpenFrom(db.Constructor, sel.Id);
            if (!_collectionOpen)
                ApplyConstructorContentVisibility(true);
        };

        // CRUD в дереве конструктора (создание/переименование/удаление) →
        // обновить календарь и заголовок/данные открытой коллекции в конструкторе
        ConstructorSidebar.RequestRefresh += (_, _) =>
        {
            CalendarSidebar.Refresh();
            _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorModule.Refresh();
        };
        ConstructorSidebar.CollectionDeleted += (_, _) => ConstructorModule.Refresh();

        // При изменении коллекций из конструктора — обновить sidebar
        ConstructorModule.CollectionsChanged += (_, _) =>
        {
            CalendarSidebar.Refresh();
            _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
        };

        // Правая панель «Страница записи»
        ConstructorModule.RecordPageRequested += (_, rid)        => OpenRecordPanel(rid);
        ConstructorModule.RecordPageClosed    += (_, _)          => CloseRecordPanel();
        RecordPanel.RecordChanged             += (_, rid)        => ConstructorModule.RefreshRecordRow(rid);
        // Клик по чипу связи в панели записи → открыть связанную запись (ее коллекция резолвится
        // в OpenRecordPanel по самой записи, поэтому переход между коллекциями работает).
        RecordPanel.RelatedRecordRequested    += (_, rid)        => OpenRecordPanel(rid);
        // Переход по чипу МЕЖБАЗОВОЙ связи (из таблицы и из панели записи): открыть запись из ее БД.
        ConstructorModule.CrossDbRecordPageRequested += (_, t)   => OpenRecordPanel(t.DbId, t.RecordId);
        RecordPanel.CrossDbRelatedRecordRequested    += (_, t)   => OpenRecordPanel(t.DbId, t.RecordId);
        // «Добавить свойство» из панели записи — тем же путем, что и «+» в таблице:
        // новое поле создается, колонка появляется в таблице, а FieldsChanged ниже
        // перестраивает список свойств панели (новое поле сразу видно и там, и там).
        RecordPanel.AddPropertyRequested      += (_, _)          => ConstructorModule.CreatePropertyFromPanel();
        // Контекстное меню карточек свойств и Delete по ним — те же обработчики, что у
        // заголовков колонок таблицы (см. ConstructorControllerView.AttachRecordPanel).
        ConstructorModule.AttachRecordPanel(RecordPanel);
        // «Добавить описание» из крошки нижнего блока — тем же путем: поле с ролью «Описание»
        // заводит контроллер, FieldsChanged ниже перестраивает панель и открывает блок на нем.
        RecordPanel.AddDescriptionRequested   += (_, _)          => ConstructorModule.CreateDescriptionFieldFromPanel();
        // Обратная синхронизация: правка ячейки в таблице → обновить открытую панель записи,
        // если это та же запись (значения свойств, имя и описание перечитываются из БД).
        ConstructorModule.RecordValueEdited   += (_, rid) =>
        {
            // Только для записи ТЕКУЩЕЙ базы (DbId=0): правка в таблице к межбазовой записи в панели
            // отношения не имеет, а id в разных базах могут совпасть.
            if (_recordPanelLogicalOpen && _lastOpened is { DbId: 0 } l && rid == l.RecordId)
                RecordPanel.ReloadFromDb();
        };
        // Состав/имена свойств изменились в таблице (добавили/удалили/переименовали/дублировали поле) —
        // открытая панель записи перестраивает список свойств, чтобы новое поле сразу появилось.
        ConstructorModule.FieldsChanged       += (_, _) =>
        {
            if (_recordPanelLogicalOpen) RecordPanel.ReloadFromDb();
        };

        // Toggle правой панели записи из навбара коллекции.
        // Если запись ранее была открыта (_lastOpened есть) — переоткрываем ее;
        // иначе берем первую запись текущей коллекции (на первом запуске
        // _lastOpened еще null, но коллекция уже выбрана).
        ConstructorModule.RecordPanelToggled += (_, isOpen) =>
        {
            if (isOpen)
            {
                // Переоткрываем ИМЕННО ту запись и в ТОЙ базе, что была открыта (межбазовую — в ее
                // БД); если открывать нечего — первая запись текущей коллекции (DbId=0).
                var last = _lastOpened
                           ?? (FirstRecordIdOfCurrentCollection() is int f
                               ? (DbId: 0, RecordId: f)
                               : ((int DbId, int RecordId)?)null);
                if (last is { } l) OpenRecordPanel(l.DbId, l.RecordId);
                else ConstructorModule.SetRecordPanelOpen(false); // нечего открывать → откат
            }
            else CloseRecordPanel();
        };

        RecordPanel.CollectionPanelToggled += (_, isOpen) => ApplyConstructorContentVisibility(isOpen);

        // ── Панель чтения документа (библиотека) ──
        // Открывает ее раздел библиотеки (подписки — при его построении, CreateLibraryModule).
        LibraryReader.CloseRequested     += (_, _)   => CloseLibraryReader();

        // Ширину панели записи восстанавливаем; сама панель при запуске закрыта
        // (_recordPanelLogicalOpen = false по умолчанию) — без выбранной записи
        // открытая панель показывала бы пустой белый блок.
        _recordPanelWidth = AppSettings.LoadRecordPanelWidth();
        _readerWidth      = AppSettings.LoadLibraryReaderWidth();

        // При потере фокуса окном закрываем открытые контекстные меню/flyout —
        // иначе на macOS popup «зависает» поверх других приложений (Avalonia issue).
        // Заодно переводим синхронизацию чужих правок в экономный режим.
        Deactivated += (_, _) =>
        {
            CloseOpenPopups();
            _changes.SetForeground(false);
            // Ушли в другую программу — дописать черновик заметки: пользователь мог уйти
            // править тот же файл снаружи. Вызов синхронный и без диалогов (на macOS
            // Deactivated приходит и при открытии собственной модалки).
            _notesView?.FlushPendingSave();
        };

        // Уровень масштабирования: применяем сохраненный и реагируем на изменения из настроек.
        ApplyScale(Scaling.Current);
        Scaling.ScaleChanged += ApplyScale;
        Closed += (_, _) => Scaling.ScaleChanged -= ApplyScale;

        // Автообновление: тихая проверка после открытия окна (уже показанное окно —
        // владелец модалки «Обновить/Отмена»). Проверка папки идет в фоне, UI не блокирует.
        // В фоне (окно неактивно) частота проверки чужих правок снижается.
        // Активация окна = пользователь «увидел» уведомления → гасим значок-счетчик.
        Activated += (_, _) =>
        {
            _changes.SetForeground(true);
            _notifications.NotifyWindowActivated();
        };

        // ВНИМАНИЕ: Opened приходит не только на старте. В фоновом режиме «крестик» скрывает
        // окно (Hide), а возврат из трея — это снова Window.Show(), который заново поднимает
        // Opened. Поэтому все здесь обязано быть идемпотентным: очередь монтирования сама
        // отсеивает уже смонтированные/ждущие БД (MountQueue.Enqueue), мониторы просто
        // переставляют период таймера, служба уведомлений выходит на повторном Start, а
        // автопроверка обновления держит флаг _updateChecked.
        Opened += async (_, _) =>
        {
            // Остальные БД реестра — в фоновую очередь (мелкие первыми): окно уже показано,
            // пользователь работает с дефолтной базой, остальные оживают по мере готовности.
            // Затем — монитор доступности (пропажа сервера/файла) и синхронизация чужих правок.
            StartBackgroundMounts();
            _health.Start();
            _changes.Start();
            // Служба уведомлений: таймер + догон событий, наступивших при закрытом приложении.
            _notifications.Start();
            // Резервное копирование: таймер расписаний + догон пропущенной копии первым тиком.
            _backup.Start();
            // Прием файлов: таймер обхода папки обмена (сам разбор — только если включен).
            _inbox.Start();
            // Прием по сети: открыть порт, если его включали. Выключенный прием — состояние по
            // умолчанию, и тогда здесь не происходит ничего.
            _ingress.Apply();
            // Сценарии: таймер расписаний. Выключенный сценарий не трогается, а тот, кому что-то
            // мешает, пропускает тик молча и догонит пропущенное, когда помеха уйдет.
            _scenarios.Start();
            // Календарь и «Задачи» — построить скрытыми в простое (MainWindow.Sections.cs).
            _ = PrebuildSectionsWhenIdleAsync();

            // Автообновление: проверяем сразу — окно показано (в том числе при старте из
            // автозапуска) и может быть владельцем модалки «Обновить/Отмена». Со скрытым
            // окном ShowDialog упал бы, поэтому StartUpdateAsync перед модалкой все равно
            // поднимает окно: его могли убрать в трей, пока шла проверка сетевой папки.
            if (AppSettings.LoadAutoUpdate() && !_updateChecked)
                await CheckUpdatesOnceAsync();
        };
        // Выход во время фонового обновления базы: прогресс перекладки сохраняется
        // (возобновляемая миграция — см. DatabaseManager.SchemaCompact), но честно
        // предупреждаем, что обновление продолжится при следующем запуске.
        Closing += async (_, e) =>
        {
            // Первым делом — черновик заметки на диск и открытую правку ячейки в базу: дальше окно
            // может уйти в фоновый режим или закрыться совсем, и несохраненный текст пропал бы.
            _notesView?.FlushPendingSave();
            ConstructorModule.CommitCellEdit();

            // Фоновый режим: нажатие на «крестик» окна не завершает приложение, а СКРЫВАЕТ его
            // (служба уведомлений и трей продолжают работать). Явный выход (трей «Выход», ⌘Q/OS
            // shutdown) идет с другим CloseReason и проходит дальше. Без созданного значка окно
            // не прячем — возвращать его будет нечем (см. BackgroundBehavior).
            if (e.CloseReason == WindowCloseReason.WindowClosing
                && BackgroundBehavior.ShouldHideOnClose(_backgroundEnabled, _explicitQuit, _tray is not null))
            {
                e.Cancel = true;
                Hide();
                return;
            }

            // Предупреждение про идущую перекладку базы — только когда окно закрывает ЧЕЛОВЕК.
            // На выходе по обновлению апдейтер уже запущен и отсчитывает 20 секунд до замены:
            // модалка, которую некому подтвердить, срывала бы обновление. Выключение компьютера
            // ждать ответа тем более не станет, а сама перекладка возобновляемая — при следующем
            // запуске продолжится с того же места.
            if (e.CloseReason != WindowCloseReason.WindowClosing) return;

            if (_closeConfirmed) return;
            var busy = _mountQueue.Pending().FirstOrDefault(p => p.State == MountQueue.ItemState.Mounting);
            if (busy is null) return;

            e.Cancel = true;
            var ok = await new ConfirmDialog(
                $"База данных «{busy.Name}» сейчас обновляется ({busy.Text}). " +
                "Можно выйти: прогресс сохранен, обновление продолжится с этого места " +
                "при следующем запуске.", "Выйти")
                .ShowDialog<bool>(this);
            if (ok)
            {
                _closeConfirmed = true;
                Close();
            }
        };

        // Восстановление открытой записи — последним шагом и отложенно (фоновым приоритетом):
        // к этому моменту раздел уже показан и коллекция загружена, а до первого кадра лезть
        // в базу за записью незачем — окно должно появиться сразу.
        if (ConstructorSettings.RestoreRecord && AppSettings.LoadRecordPanelOpen())
            Avalonia.Threading.Dispatcher.UIThread.Post(RestoreLastRecordPanel,
                                                        Avalonia.Threading.DispatcherPriority.Background);

        Closed += (_, _) =>
        {
            ChangeBus.Committed -= OnLocalCommit;   // событие статическое — не держать окно
            _localFlushTimer?.Stop();
            _changes.Dispose();
            _health.Dispose();
            _notifications.Dispose();
            _backup.Dispose();
            _inbox.Dispose();
            _ingress.Dispose();
            _scenarios.Dispose();
            _watchdog.Dispose();
        };
    }

    private readonly UiWatchdog _watchdog;
    private readonly BackupService _backup;
    private readonly InboxService  _inbox;
    private readonly IngressService _ingress;
    private readonly ScenarioService _scenarios;

    // Человеческое имя раздела для записей о медленном открытии.
    private static string ModuleTitle(string module) => module switch
    {
        "calendar"    => "Календарь",
        "tasks"       => "Задачи",
        "constructor" => "Конструктор",
        "settings"    => "Настройки",
        "profile"     => "Профиль",
        "notes"       => "Заметки",
        "library"     => "Библиотека документов",
        "extensions"  => "Расширения",
        _             => module,
    };

    // Выход уже подтвержден диалогом «база обновляется» — повторно не спрашиваем.
    private bool _closeConfirmed;

    // Масштаб всего интерфейса: LayoutTransform на корне — содержимое перекомпоновывается в
    // логических пикселях и заполняет окно. 100 % → без трансформации (быстрый общий путь).
    private void ApplyScale(double scale) =>
        ScaleHost.LayoutTransform = Math.Abs(scale - 1.0) < 0.001
            ? null
            : new Avalonia.Media.ScaleTransform(scale, scale);

    // Закрывает открытые контекстные меню и flyout. Вызывается при потере фокуса окном —
    // иначе на macOS popup «зависает» поверх других окон (Avalonia issue).
    private void CloseOpenPopups()
    {
        // Материализуем дерево в список ДО закрытия: Close()/Hide() меняют визуальное
        // дерево, а GetVisualDescendants() ленивый — иначе «Collection was modified».
        var controls = this.GetVisualDescendants().OfType<Control>().ToList();

        foreach (var ctrl in controls)
            if (ctrl.ContextMenu is { IsOpen: true } cm)
                cm.Close();

        // Выпадашки на голом Popup (списки даты и времени, «Мультисписок», «Связь», панель
        // фоновых работ) живут в логическом дереве, а не в визуальном: визуально их содержимое
        // уехало в отдельный popup-рут.
        foreach (var pop in this.GetLogicalDescendants().OfType<Popup>().ToList())
            if (pop.IsOpen) pop.IsOpen = false;

        // Панели-флайауты (фильтр колонки таблицы и журналов, пикер коллекций, «Тип документа»
        // библиотеки, меню списков) не ищутся ни в одном из деревьев — они сами отмечаются
        // в реестре при показе.
        TransientPopups.CloseAll();
    }

    // Поставить в фоновую очередь все БД реестра, кроме уже смонтированных (стартовая
    // партия сортируется по размеру файла — мелкие оживают почти сразу). Вызывается после
    // показа окна: пользователь сразу работает с дефолтной базой, остальные догружаются
    // по одной, их узлы в сайдбаре показывают живой статус.
    private void StartBackgroundMounts()
    {
        var rest = _workspace.Registry.GetAll()
            .Where(d => !d.IsDefault && _workspace.Get(d.Id) is null)
            .ToList();
        if (rest.Count == 0) return;
        // Сортировка партии по размеру статит файлы — зависший сетевой путь не должен
        // держать UI-поток, поэтому постановка в очередь тоже в фоне.
        _ = Task.Run(() =>
        {
            _mountQueue.EnqueueStartupBatch(rest);
            Dispatcher.UIThread.Post(SyncMountedToUi);   // заглушки «в очереди…»
        });
    }

    // Клик по заглушке немонтированной базы: короткая подсказка, что с ней происходит.
    private async Task ShowPendingDatabaseInfo(int dbId)
    {
        var st = _mountQueue.StatusOf(dbId);
        if (st is null) return;
        var path = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId)?.Path;
        var text = st.State switch
        {
            MountQueue.ItemState.Queued =>
                $"База данных «{st.Name}» в очереди на подключение — она откроется автоматически, " +
                "работать с остальными базами можно уже сейчас.",
            MountQueue.ItemState.Mounting =>
                $"База данных «{st.Name}» подключается: {st.Text}. Она откроется автоматически, " +
                "работать с остальными базами можно уже сейчас.",
            MountQueue.ItemState.Maintenance =>
                $"Файл базы данных «{st.Name}» сжимается: {st.Text}. По завершении база " +
                "подключится сама; работать с остальными базами можно уже сейчас.",
            _ =>
                $"База данных «{st.Name}» {st.Text}." + (path is null ? "" : $"\n{path}\n") +
                "Приложение периодически пытается переподключиться — как только файл снова станет " +
                "доступен, база откроется сама. Можно также подключить ее заново вручную в настройках " +
                "(«Управление базой данных»).",
        };
        await ConfirmDialog.Info(text).ShowDialog(this);
    }

    // ── Обслуживание файла базы: «Сжать файл» ─────────────────────────────────────

    // Размер файла до/после сжатия — для карточки по завершении (пишет фоновая работа
    // очереди, читает обработчик MaintenanceFinished на UI-потоке уже после нее).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (long Before, long After)> _compactSizes = new();

    private static string DatabaseRowSize(long bytes) => Utils.Sizes.Text(bytes);

    // Сжатие (VACUUM) переписывает файл целиком под эксклюзивным замком, поэтому базу на это
    // время отцепляем от рабочего пространства (как при потере) и ведем через очередь
    // монтирования: узел в сайдбаре и строка в настройках показывают «сжимаю файл · 45 % ·
    // ≈ 2 мин», по завершении база монтируется обратно. Общую базу при монтировании не
    // сжимаем никогда — это единственный путь вернуть ей место (см. DatabaseManager).
    private async Task CompactDatabaseAsync(int dbId)
    {
        var db    = _workspace.Get(dbId);
        var entry = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
        if (db is null || entry is null || entry.IsDefault || db.Provider != "sqlite") return;

        var stats = db.Storage;
        var text  = $"Сжать файл базы данных «{db.Name}»?\n\n" +
                    (stats is null ? "" : $"Сейчас {Views.Settings.DatabaseRow.FormatStorage(stats)}.\n\n") +
                    "Файл будет переписан целиком, данные не меняются. На время сжатия база отключается" +
                    (db.IsShared
                        ? ", а другие пользователи не смогут сохранять изменения, пока оно не закончится — " +
                          "лучше делать это, когда с базой никто не работает. "
                        : ". ") +
                    "По завершении база подключится сама.";
        var ok = await new ConfirmDialog(text, confirmText: "Сжать").ShowDialog<bool>(this);
        if (!ok) return;

        var detached = _workspace.Detach(dbId);
        if (detached is null) return;
        if (_currentConstructorDbId == dbId)
        {
            _currentConstructorDbId = _workspace.Default.DbId;
            ConstructorModule.DetachDatabase(_workspace.Default.Constructor);
        }

        var name = db.Name;
        var path = entry.Path;
        var started = _mountQueue.RunMaintenance(entry, stage =>
        {
            // Закрыть контекст и пул подключений к файлу — иначе они мешают VACUUM. Здесь же,
            // в фоне: Dispose ждет фоновые чистки до 5 с.
            Workspace.Release(detached);
            var before = new FileInfo(path).Length;
            DatabaseManager.CompactFile(path, name, stage);
            _compactSizes[dbId] = (before, new FileInfo(path).Length);
            return null;
        });
        if (!started)
        {
            // База уже в очереди/монтируется (гонка с переподключением) — просто вернем ее.
            _ = Task.Run(() => Workspace.Release(detached));
            _mountQueue.Enqueue(entry);
        }
        SyncMountedToUi();   // узел базы становится заглушкой «сжимаю файл…»
    }

    // Резервная копия переехала в «Настройки → Резервное копирование»: копии снимает
    // BackupService (по расписанию и кнопкой «Копировать сейчас»), прежней модалки с
    // выбором файла больше нет — папку задает настройка раздела.

    // Потеря смонтированной БД на ходу (сервер выключился, файл удалили): зеркало
    // отключения из настроек, но запись реестра остается — монитор будет ретраить,
    // при возврате файла база оживет сама (очередь → Completed → SyncMountedToUi).
    // Сервер перезагружают целиком, и монитор объявляет базы одного сервера пачкой —
    // пересборка сайдбаров одна на всю пачку (RequestSyncMountedToUi) и уже без мертвых баз:
    // иначе сайдбар читал бы соседние базы на UI-потоке и ждал таймаут каждой из них.
    // Всплывашки нет: статус живет надписью под именем базы (сайдбар, «Управление базой данных»).
    private void OnDatabaseLost(int dbId, string reason)
    {
        var db = _workspace.Detach(dbId);
        if (db is null) return;   // уже размонтирована (гонка с отключением из настроек)
        var name = db.Name;

        if (_currentConstructorDbId == dbId)
        {
            _currentConstructorDbId = _workspace.Default.DbId;
            ConstructorModule.DetachDatabase(_workspace.Default.Constructor);
        }
        // Закрытие — в фоне: Dispose ждет фоновые чистки (до 5 с), а на мертвой сетевой папке
        // это ожидание не должно замораживать окно. Пул подключений к файлу сбрасывается там же —
        // повторное монтирование возьмет свежие хендлы, а не мертвые.
        _ = Task.Run(() => Workspace.Release(db));
        _mountQueue.MarkFailed(dbId, name,
            DbHealthMonitor.LostStatus(reason, DateTime.Now, "переподключится сама"), reason);
        _lostDbIds.Add(dbId);
        RequestSyncMountedToUi();
    }

    // Отложенная пересборка сайдбаров: несколько запросов подряд (пачка потерянных баз одного
    // сервера) сливаются в одну — она выполняется, когда очередь UI-потока дойдет до фоновых
    // задач, то есть после всех уже отправленных OnDatabaseLost.
    private bool _syncRequested;

    private void RequestSyncMountedToUi()
    {
        if (_syncRequested) return;
        _syncRequested = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncRequested = false;
            SyncMountedToUi();
        }, DispatcherPriority.Background);
    }

    // Надпись «снова доступна» под именем вернувшейся базы — во всех трех сайдбарах и в
    // «Управление базой данных», на несколько секунд; затем строка статуса убирается.
    private void ShowReturnedDbStatus(int dbId)
    {
        SetDbStatus(dbId, "снова доступна");
        DispatcherTimer.RunOnce(() => SetDbStatus(dbId, null), ReturnedStatusLifetime);
    }

    private static readonly TimeSpan ReturnedStatusLifetime = TimeSpan.FromSeconds(8);

    private void SetDbStatus(int dbId, string? text)
    {
        ConstructorSidebar.SetDatabaseStatus(dbId, text);
        CalendarSidebar.SetDatabaseStatus(dbId, text);
        TasksSidebar.SetDatabaseStatus(dbId, text);
        // Настройкам — если построены; построенные получат накопленные статусы (CreateSettingsModule).
        if (text is null) _dbStatuses.Remove(dbId); else _dbStatuses[dbId] = text;
        _settingsView?.SetDatabaseStatus(dbId, text);
    }

    // Чужие правки в БД (другой пользователь общей базы / другая копия приложения):
    // обновить только затронутое. Невидимые модули не трогаем — они и так перечитывают
    // данные при переключении на них (ShowModule → Refresh).
    private void OnRemoteChanges(int dbId, IReadOnlyList<ChangePoller.RemoteChange> changes)
    {
        var changedDb = _workspace.Get(dbId);
        if (changedDb is null) return;   // база уже размонтирована/потеряна

        // Скрытый раздел перечитает данные при показе, если читал их раньше этого момента
        // (ShowModule → «Задачи»); календарь и конструктор при показе перечитывают всегда.
        _lastChange[dbId] = ChangeBus.Now();

        // События пользователей (присоединение/допуск/права, targetType "user"):
        // перечитать свой снапшот прав и открытую панель администратора — у админа
        // новый пользователь появляется «в моменте», у ожидающего допуск снимает замок
        // без перезапуска. До проверки IsCellEditing: правку ячеек эти события не трогают,
        // а задерживать допуск на ретраи незачем (повтор идемпотентен).
        if (changes.Any(c => c.TargetType == "user"))
        {
            var wasGranted = changedDb.Permissions.AccessGranted;
            changedDb.Permissions.Reload();
            if (changedDb.Permissions.AccessGranted != wasGranted)
            {
                SyncMountedToUi();   // замок в сайдбарах, наборы БД календаря/задач
                if (changedDb.Permissions.AccessGranted)
                    NotifyInfo($"Администратор подтвердил доступ к базе данных «{changedDb.Name}».");
                else
                    Notify($"Доступ к базе данных «{changedDb.Name}» отозван администратором.");
            }
            _profileView?.OnRemoteUserChanges(dbId);
            SyncProfileSidebar();   // роль могли поменять с другой машины — раздел журнала следует за ней
        }

        // Пользователь печатает в ячейке — не затираем ввод, повторяем через пару секунд
        // (refresh идемпотентен, лишний повтор безвреден).
        if (ConstructorModule.IsCellEditing)
        {
            DispatcherTimer.RunOnce(() => OnRemoteChanges(dbId, changes), TimeSpan.FromSeconds(3));
            return;
        }

        // Структурные изменения (группы/коллекции/поля) перестраивают деревья сайдбаров.
        var structural = changes.Any(c => c.TargetType is "collection" or "group" or "field");
        if (structural)
        {
            CalendarSidebar.Refresh();
            TasksSidebar.Refresh();
            ConstructorSidebar.Refresh();
        }

        // Открытая коллекция конструктора из этой БД: структура → полная пересборка,
        // только записи → строки на месте (без пересборки колонок и мелькания).
        if (_currentConstructorDbId == dbId && ConstructorModule.IsVisible)
        {
            if (structural) ConstructorModule.Refresh();
            else            ConstructorModule.RefreshRowsRemote();
        }

        // Видимый агрегатный модуль перечитывает события/задачи затронутой БД. Задачи — принудительно:
        // при том же наборе галочек SetActiveIds чтение пропускает, а чужая правка галочек не меняет.
        if (_activeModule == "calendar")
            _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
        else if (_activeModule == "tasks")
            _tasksView?.SetActiveIds(TasksSidebar.GetActiveIds(), force: true);

        // Открытая панель записи могла показывать измененную запись — перечитать значения.
        if (_recordPanelLogicalOpen && _currentConstructorDbId == dbId)
            RecordPanel.ReloadFromDb();
    }

    // ── Свои изменения: единая шина ───────────────────────────────────────────
    //
    // Раздел обновляет себя после собственного действия сам, а другие разделы узнавали о нем
    // через точечные события (CollectionsChanged, RecordValueEdited, …) — и набор их был
    // неполным: правка ячейки в конструкторе не доходила до «Задач», перенос события в
    // календаре — до «Задач» и конструктора, запись из окна расширения — ни до кого, пока
    // человек не переключит раздел или галочку. Поллер чужих правок тут не помощник: свои
    // строки change_log он отбрасывает по UserKey. Поэтому любой коммит в базу из этого
    // процесса поднимает ChangeBus.Committed (см. AppDbContext.NoteSaved), а здесь все
    // сводится к одному правилу: часть экрана перечитывает данные, если читала их РАНЬШЕ
    // последнего коммита (ее DataStamp против момента коммита). Кто сам правил — уже
    // обновился после записи, его отметка новее, и лишней перезагрузки у него нет.
    //
    // Коммиты копятся LocalFlushDelay после последнего из них: задача — это запись плюс
    // несколько значений, панель записи пишет на каждую букву, массовая операция коммитит
    // порциями, — а обновление нужно одно на пачку и после ее конца (обновление посреди
    // порций читало бы базу под следующим коммитом).
    private readonly ChangeBatch _localChanges = new();
    private DispatcherTimer? _localFlushTimer;
    private static readonly TimeSpan LocalFlushDelay = TimeSpan.FromMilliseconds(300);
    // Пока человек печатает в ячейке таблицы или в панели записи, их перечитывание ждет.
    private static readonly TimeSpan LocalRetryDelay = TimeSpan.FromSeconds(2);
    // Момент последнего коммита по базе — своего (шина) или чужого (поллер). Раздел, читавший
    // данные раньше него, при показе перечитывает их (ShowModule → «Задачи»).
    private readonly Dictionary<int, long> _lastChange = [];

    private long LastChangeStamp() => _lastChange.Count == 0 ? 0 : _lastChange.Values.Max();

    // С потока записи (UI или фонового), синхронно внутри SaveChanges/Commit: запомнить и
    // уйти — любая работа с базой отсюда была бы реентерабельным вызовом контекста.
    private void OnLocalCommit(string dbPath, IReadOnlyList<ChangeBus.Change> changes)
    {
        _localChanges.Add(dbPath, changes, ChangeBus.Now());
        Dispatcher.UIThread.Post(RestartLocalFlush, DispatcherPriority.Background);
    }

    private void RestartLocalFlush()
    {
        _localFlushTimer ??= new DispatcherTimer(LocalFlushDelay, DispatcherPriority.Background,
                                                 (_, _) => FlushLocalChanges());
        _localFlushTimer.Stop();
        _localFlushTimer.Start();
    }

    private void FlushLocalChanges()
    {
        _localFlushTimer?.Stop();
        foreach (var entry in _localChanges.Drain())
        {
            // База еще монтируется (миграции, сиды) или уже отключена — ее покажет SyncMountedToUi,
            // когда она встанет в рабочее пространство.
            var db = _workspace.Mounted.FirstOrDefault(m =>
                string.Equals(m.Path, entry.DbPath, StringComparison.OrdinalIgnoreCase));
            if (db is null) continue;

            _lastChange[db.DbId] = Math.Max(_lastChange.GetValueOrDefault(db.DbId), entry.Stamp);
            ApplyLocalChanges(db.DbId, entry.Structural, entry.Stamp);
        }
    }

    // Перечитать то, что читало данные раньше коммита stamp. Само правило — RefreshPlan (чистое,
    // под тестами), здесь только снимок экрана и выполнение шагов. Повторяемо: пока человек
    // печатает в ячейке таблицы или в панели записи, их перечитывание откладывается и заходит
    // снова — остальные части к тому времени уже свежие (их отметки новее) и не трогаются.
    private void ApplyLocalChanges(int dbId, bool structural, long stamp)
    {
        if (_workspace.Get(dbId) is null) return;

        var steps = RefreshPlan.After(new RefreshPlan.Screen(
            CommitStamp:             stamp,
            Structural:              structural,
            ActiveModule:            _activeModule,
            CalendarSidebarStamp:    CalendarSidebar.DataStamp,
            TasksSidebarStamp:       TasksSidebar.DataStamp,
            ConstructorSidebarStamp: ConstructorSidebar.DataStamp,
            LibrarySidebarStamp:     LibrarySidebar.DataStamp,
            // Раздел еще не построен — читать ему нечего, он прочитает все при показе.
            CalendarStamp:           _calendarView?.DataStamp ?? long.MaxValue,
            TasksStamp:              _tasksView?.DataStamp ?? long.MaxValue,
            LibraryStamp:            _libraryView?.DataStamp ?? long.MaxValue,
            ConstructorShowsDb:      _currentConstructorDbId == dbId && ConstructorModule.IsVisible,
            ConstructorStamp:        ConstructorModule.DataStamp,
            CellEditing:             ConstructorModule.IsCellEditing,
            RecordPanelShowsDb:      _recordPanelLogicalOpen && _currentConstructorDbId == dbId,
            RecordPanelStamp:        RecordPanel.DataStamp,
            RecordPanelFocused:      RecordPanel.IsKeyboardFocusWithin));

        // Сайдбары — раньше разделов: набор галочек читается уже с перестроенного дерева.
        if (steps.Has(RefreshPlan.Steps.CalendarSidebar))    CalendarSidebar.Refresh();
        if (steps.Has(RefreshPlan.Steps.TasksSidebar))       TasksSidebar.Refresh();
        if (steps.Has(RefreshPlan.Steps.ConstructorSidebar)) ConstructorSidebar.Refresh();
        if (steps.Has(RefreshPlan.Steps.LibrarySidebar))     LibrarySidebar.Refresh();

        if (steps.Has(RefreshPlan.Steps.Calendar)) _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
        if (steps.Has(RefreshPlan.Steps.Tasks))    _tasksView?.SetActiveIds(TasksSidebar.GetActiveIds(), force: true);
        if (steps.Has(RefreshPlan.Steps.Library))  _libraryView?.SetActiveIds(LibrarySidebar.GetActiveIds(), force: true);

        if (steps.Has(RefreshPlan.Steps.ConstructorFull))      ConstructorModule.Refresh();
        else if (steps.Has(RefreshPlan.Steps.ConstructorRows)) ConstructorModule.RefreshRowsRemote();

        if (steps.Has(RefreshPlan.Steps.RecordPanel)) RecordPanel.ReloadFromDb();

        if (steps.Has(RefreshPlan.Steps.Retry))
            DispatcherTimer.RunOnce(() => ApplyLocalChanges(dbId, structural, stamp), LocalRetryDelay);
    }

    // Сообщение человеку — карточка в правом верхнем углу окна (AppNotifications), стопкой с
    // остальными, сама гаснет. Тем же путем идут ошибки записи (DbErrors). Статус доступности
    // базы сюда НЕ идет — он живет надписью под именем базы в сайдбаре и в «Управление базой
    // данных» (MountQueue + DbHealthMonitor.LostStatus).
    private static void Notify(string message)     => Controls.Widgets.AppNotifications.Error(message);
    private static void NotifyInfo(string message) => Controls.Widgets.AppNotifications.Info(message);

    // Заглушки немонтированных БД для сайдбаров (очередь/обновление/недоступна).
    private List<Controls.Components.PendingDb> PendingDbs() =>
        _mountQueue.Pending()
            .Select(s => new Controls.Components.PendingDb(
                s.DbId, s.Name, s.Text, s.State == MountQueue.ItemState.Failed))
            .ToList();

    // Допущенные БД: календарь, цели переноса и списки диалогов видят только их.
    // Недопущенные (замок) показывает лишь сайдбар конструктора.
    private List<DatabaseManager> GrantedDbs() =>
        _workspace.Mounted.Where(m => m.Permissions.AccessGranted).ToList();

    // Источник событий для службы уведомлений: события со стартом в окне [from, to) по всем
    // допущенным БД (то же, что показывает календарь: activeIds=null → все видимые поля-даты,
    // без учета временных галок сайдбара). Зовется из ФОНОВОГО потока (NotificationService.
    // TickBackground), поэтому читает КАЖДУЮ БД на свежем подключении
    // (GetCalendarRecordsByRangeIsolated), а не общий однопоточный контекст. GrantedDbs()
    // безопасен вне UI-потока: Workspace.Mounted отдает снимок под локом.
    private IReadOnlyList<Core.Models.CalendarRecord> CollectDueEvents(DateTime from, DateTime to)
    {
        var list = new List<Core.Models.CalendarRecord>();
        foreach (var m in GrantedDbs())
        {
            try { list.AddRange(m.Constructor.GetCalendarRecordsByRangeIsolated(from, to, null)); }
            catch { /* БД могла отвалиться на ходу — пропускаем, монитор переподключит */ }
        }
        return list;
    }

    // Клик по уведомлению: вынести окно на передний план и показать запись с кнопкой «Принято».
    private void OnNotificationClicked(Core.Models.CalendarRecord record)
    {
        Show();   // покажет окно, если оно было скрыто фоновым режимом
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Значок гаснет: активация окна = «увидел» (Activated может не прийти при программном Activate).
        _notifications.NotifyWindowActivated();
        _ = new NotificationRecordDialog(record).ShowDialog(this);
    }

    // Клик по уведомлению задачи: окно на передний план, раздел «Задачи» и карточка этой
    // задачи — тот же диалог, что по двойному клику в списке (правка, перенос, удаление).
    private void OnTaskNotificationClicked(Core.Models.TaskEvent e)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _notifications.NotifyWindowActivated();

        // Переключаем раздел ТЕМ ЖЕ путем, что и кнопка панели активности, иначе подсветка
        // кнопки разъедется с показанным разделом.
        ActivityBarControl.SetModuleActive("tasks");
        ShowModule("tasks");
        _ = TasksModule.OpenTaskAsync(e.DbId, e.RecordId, e.CollectionId, e.ExecFieldId);
    }

    // ── Фоновый режим и значок в трее/меню-баре ──────────────────────────────────

    private void InitBackgroundMode()
    {
        _backgroundEnabled = AppSettings.LoadBackgroundEnabled();
        SetupTray();
        if (_tray is not null) _tray.IsVisible = _backgroundEnabled;
        // Состояние службы без окна — в журнал: вместе с записью о запуске это ответ на вопрос
        // «программа вообще работала в это время и должна ли была прятаться в трей».
        if (_backgroundEnabled)
            Log.Information(_tray is not null
                ? "Фоновый режим включен: значок в трее создан, закрытие окна прячет программу"
                : "Фоновый режим включен в настройках, но значок в трее недоступен — закрытие окна завершит программу");

        InitGlobalShortcuts();
    }

    /// <summary>
    /// Сочетания, действующие во всем окне: выход, масштаб интерфейса и создание коллекций и
    /// групп. Все — из реестра действий, поэтому переназначаются в «Настройки → Сочетания
    /// клавиш». Действия с модификаторами текст не печатают, но создание и дублирование все
    /// равно пропускаем мимо полей ввода: в поле пользователь работает с текстом.
    ///
    /// ⌘Q на macOS приходит штатным меню Avalonia (CloseReason=ApplicationShutdown) и завершает
    /// программу независимо от этой настройки.
    /// </summary>
    private void InitGlobalShortcuts()
    {
        KeyDown += (_, e) =>
        {
            if (e.Handled) return;

            if (ShortcutSettings.Matches("app.quit", e)) { QuitExplicit(); e.Handled = true; return; }

            if (ShortcutSettings.Matches("app.zoomIn", e))  { Scaling.ZoomIn();  e.Handled = true; return; }
            if (ShortcutSettings.Matches("app.zoomOut", e)) { Scaling.ZoomOut(); e.Handled = true; return; }
            if (ShortcutSettings.Matches("app.zoomReset", e))
            {
                Scaling.SetScale(AppSettings.ScaleDefault);
                e.Handled = true;
                return;
            }

            // Дальше — действия дерева коллекций: в том разделе, чье дерево сейчас на экране,
            // и не поверх поля ввода.
            if (ActiveSidebarTree() is not { } tree || IsTextInputFocused(e)) return;

            // Клавишу забираем пометкой: календарь и задачи слушают окно с handledEventsToo и
            // обработанное событие тоже видят.
            if (ShortcutSettings.Matches("sidebar.createCollection", e))
            {
                tree.CreateCollectionShortcut();
                KeyClaim.Claim(e);
            }
            else if (ShortcutSettings.Matches("sidebar.createGroup", e))
            {
                tree.CreateGroupShortcut();
                KeyClaim.Claim(e);
            }
            else if (ShortcutSettings.Matches("sidebar.duplicateCollection", e))
            {
                if (tree.DuplicateSelectionShortcut()) KeyClaim.Claim(e);
            }
        };
    }

    /// <summary>
    /// Дерево коллекций текущего раздела. Их четыре — конструктор, календарь, задачи и
    /// библиотека документов, — и все они показывают одни и те же коллекции с одним и тем же
    /// меню, поэтому клавиши «Создать коллекцию»/«Создать группу»/«Дублировать» работают в
    /// каждом. В разделах со своим сайдбаром (настройки, профиль, заметки) коллекций нет —
    /// там null.
    /// </summary>
    private SidebarTree? ActiveSidebarTree() => _activeModule switch
    {
        "constructor" => ConstructorSidebar,
        "calendar"    => CalendarSidebar,
        "tasks"       => TasksSidebar,
        "library"     => LibrarySidebar,
        _             => null,
    };

    private static bool IsTextInputFocused(KeyEventArgs e) =>
        e.Source is TextBox or AutoCompleteBox
        || (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is not null);

    // Значок в системной области с меню «Открыть»/«Выход». Иконка — та же, что у окна (icon.ico).
    private void SetupTray()
    {
        if (Application.Current is null) return;
        try
        {
            var open = new NativeMenuItem("Открыть SmartBase");
            open.Click += (_, _) => ShowFromTray();
            var quit = new NativeMenuItem("Выход");
            quit.Click += (_, _) => QuitExplicit();
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(quit);

            _tray = new TrayIcon
            {
                Icon        = LoadAppIcon(),
                ToolTipText = AppSettings.DisplayName,
                IsVisible   = false,
                Menu        = menu,
            };
            _tray.Clicked += (_, _) => ShowFromTray();
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _tray });
        }
        catch (Exception ex)
        {
            // Трей недоступен (напр. GNOME без расширения) — остаются запасные пути, но без
            // значка крестик закрывает программу, а не прячет, и напоминания при закрытом окне
            // не придут. Снаружи это ничем не видно — только по этой записи.
            Log.Warning(ex, "Значок в трее не создан — закрытие окна будет завершать программу, уведомления при закрытом окне приходить не будут");
        }
    }

    private static WindowIcon? LoadAppIcon()
    {
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://SmartBase/Assets/Branding/icon.ico"));
            return new WindowIcon(s);
        }
        catch { return null; }
    }

    // Пункт трея «Открыть» / клик по значку: вернуть и активировать окно.
    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _notifications.NotifyWindowActivated();
    }

    // Автопроверка обновления — ОДИН раз за сессию. Показ окна из трея — это повторный
    // Window.Show(), а он снова поднимает Opened, поэтому одного вызова из обработчика мало.
    private async Task CheckUpdatesOnceAsync()
    {
        if (_updateChecked) return;
        _updateChecked = true;   // ставим ДО await — второй вход не должен начать вторую проверку
        // Проверка идет без раздела настроек; он нужен, только если обновление нашлось (окно
        // «Обновить» и загрузка). Итог проверки раздел покажет, когда его откроют.
        if (await Views.Settings.SettingsView.CheckForUpdatesOnStartupAsync())   // не бросает
            await SettingsModule.StartFoundUpdateAsync();
    }

    // Явный выход (трей «Выход», Ctrl+Q): не скрывать окно, а завершить приложение.
    private void QuitExplicit()
    {
        _explicitQuit = true;
        Program.ExitReason = "выход из трея или Ctrl+Q";
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // Тумблер «Фоновый режим»: показать/скрыть значок; при выключении вернуть окно, чтобы оно
    // не осталось скрытым без точки возврата.
    private void OnBackgroundChanged(bool on)
    {
        _backgroundEnabled = on;
        // Редкое действие, меняющее поведение всей программы после закрытия окна, — в журнал.
        Log.Information("Фоновый режим {State:l} в настройках{Tray:l}",
                        on ? "включен" : "выключен",
                        on && _tray is null ? " — значок в трее недоступен, режим не заработает" : "");
        if (_tray is not null) _tray.IsVisible = on;
        // Значок создать не удалось: прятать окно мы не станем (возвращать нечем), и человек
        // должен знать, что тумблер в его системе ничего не даст, — иначе он рассчитывает на
        // напоминания при закрытом окне, а их не будет.
        else if (on)
            _ = ConfirmDialog.Info(
                "Значок в системной области недоступен в этой системе, поэтому фоновый режим не заработает: " +
                "при закрытии окна программа завершится как обычно, и напоминания приходить не будут.")
                .ShowDialog(this);
        if (!on && !IsVisible) ShowFromTray();
    }

    // База переименована окном настроек базы — из «Настроек» или из менеджера паролей
    // (шестеренка у пароля сервера PostgreSQL). Реестр уже обновлен: меняем имя смонтированной
    // БД на месте и перечитываем сайдбары, без ремаунта. SyncMountedToUi заодно перечитывает
    // список баз в «Настройках» из реестра, так что новое имя видно и там. Один обработчик на
    // оба источника: копия разошлась бы с оригиналом при первой правке.
    private void OnDatabaseRenamed(object? sender, (int Id, string Name) e)
    {
        if (_workspace.Get(e.Id) is { } renamed) renamed.Name = e.Name;
        SyncMountedToUi();
    }

    // Пересобрать боковые панели и обновить набор БД календаря после монтирования/
    // размонтирования/получения допуска. Немонтированные БД очереди показываются
    // заглушками со статусом во всех трех сайдбарах.
    private void SyncMountedToUi()
    {
        HookPermissionEvents();
        var pending = PendingDbs();
        ConstructorSidebar.InitializeWorkspace(_workspace.Mounted, Controls.Components.SidebarMode.Constructor, pending);
        CalendarSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Calendar, pending);
        TasksSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Tasks, pending);
        LibrarySidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Library, pending);
        _calendarView?.SetDatabases(GrantedDbs());
        _tasksView?.SetDatabases(GrantedDbs());
        _libraryView?.SetDatabases(GrantedDbs());
        ConstructorModule.SetDatabases(GrantedDbs());
        _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
        _tasksView?.SetActiveIds(TasksSidebar.GetActiveIds());
        _libraryView?.SetActiveIds(LibrarySidebar.GetActiveIds());
        _settingsView?.RefreshDatabases();   // обновить статус «подключена/не подключена»
        _profileView?.OnDatabasesChanged();  // история — только базы с допуском, панель администратора — свои базы
        SyncProfileSidebar();                // админство пропало вместе с базой — «Журнал безопасности» уходит из меню
    }

    // ── Живой состав пользователей базы ───────────────────────────────────────
    //
    // Пользователи базы — источник опций типа свойства «пользователь» (исполнитель
    // задачи, «Назначил», любые user-поля коллекции). Состав меняется на ходу:
    // сосед присоединился к базе, администратор допустил/отозвал допуск, кто-то
    // сменил ФИО. PermissionService.Changed — единый сигнал обеих сторон: у админа
    // он приходит от его же действия в панели, у остальных — из поллера чужих правок
    // (OnRemoteChanges → Reload). По нему перечитываем открытые списки, чтобы
    // выпадающие не приходилось «оживлять» перезапуском.
    private readonly HashSet<int> _permHooked = [];

    private void HookPermissionEvents()
    {
        // База размонтирована (отключили/потеряли) — забываем: при повторном
        // монтировании будет новый DatabaseManager со своим сервисом прав.
        _permHooked.RemoveWhere(id => _workspace.Get(id) is null);

        foreach (var db in _workspace.Mounted)
        {
            if (!_permHooked.Add(db.DbId)) continue;
            var dbId = db.DbId;
            db.Permissions.Changed += (_, _) =>
                Dispatcher.UIThread.Post(() => OnDbUsersChanged(dbId));
        }
    }

    // Состав пользователей/прав базы изменился: обновить то, что уже построено.
    // Таблица конструктора и панель записи перечитывают опции при построении
    // редактора сами (ConstructorRepository.GetUserOptions) — им нужен только
    // повод перерисоваться, а задачи держат список исполнителей быстрого добавления.
    private void OnDbUsersChanged(int dbId)
    {
        if (_workspace.Get(dbId) is null) return;

        // Право просмотра коллекции рисуется замком в сайдбаре конструктора: выдали или
        // отобрали — дерево должно перестроиться без перезапуска.
        ConstructorSidebar.Refresh();

        if (_activeModule == "tasks") _tasksView?.Refresh();
        if (_recordPanelLogicalOpen && _currentConstructorDbId == dbId) RecordPanel.ReloadFromDb();
    }

    // Клик по запертой базе в сайдбаре — это и есть просьба о доступе. Включен доступ по
    // паролю — диалог ввода (верный пароль дает допуск сразу), выключен или пароля нет —
    // пояснение про подтверждение администратором.
    private async Task RequestDatabaseUnlock(int dbId)
    {
        var db = _workspace.Get(dbId);
        if (db is null) return;

        try
        {
            // Допуск и политику читаем свежими: пока висел замок, администратор мог уже пустить
            // или выключить вход паролем.
            db.Permissions.Reload(notify: false);
            // Убрали из базы, пока она была открыта, — запрос заводится заново: иначе администратор
            // не увидел бы ни человека, ни запроса до следующего открытия базы.
            db.Permissions.RequestAccess();
        }
        catch (Exception ex) when (DbErrors.Handle(ex, db.Constructor.DbPath)) { return; }

        if (db.Permissions.AccessGranted) { SyncMountedToUi(); return; }

        if (db.Permissions.PasswordAccess)
        {
            var unlocked = await PasswordDialog
                .ForUnlock(db.Name, db.Permissions.Unlock)
                .ShowDialog<bool>(this);
            if (unlocked) SyncMountedToUi();
        }
        else
        {
            await ConfirmDialog.Info(
                $"База данных «{db.Name}» станет доступна после подтверждения администратором. " +
                "Запрос на доступ уже отправлен и виден администратору в панели администратора. " +
                "Как только доступ подтвердят, база откроется сама.")
                .ShowDialog(this);
        }
    }

    private void ShowModule(string module)
    {
        // Порог медленности: раздел, который открывался дольше Diag.UiOpen, попадает в журнал.
        using var slow = Diag.Measure($"открытие раздела «{ModuleTitle(module)}»", Diag.UiOpen);

        // Уходим из заметок — дописать несохраненный текст: автосохранение работает с паузой,
        // и последние набранные символы иначе остались бы только в редакторе.
        if (_activeModule == "notes" && module != "notes") _notesView?.FlushPendingSave();

        _activeModule = module;

        // Разделы, которые строит первый показ: видимым — построить, прочим — скрыть построенные.
        ShowSection(_calendarView,   () => CalendarModule,   module == "calendar");
        ShowSection(_settingsView,   () => SettingsModule,   module == "settings");
        ShowSection(_notesView,      () => NotesModule,      module == "notes");
        ShowSection(_tasksView,      () => TasksModule,      module == "tasks");
        ShowSection(_extensionsView, () => ExtensionsModule, module == "extensions");
        ShowSection(_profileView,    () => ProfileModule,    module == "profile");
        ShowSection(_libraryView,    () => LibraryModule,    module == "library");
        // Видимость ConstructorModule зависит еще и от _collectionOpen — ее ставит ApplyColumns.

        if (module == "calendar")
        {
            CalendarSidebar.IsVisible    = _sidebarOpen;
            ConstructorSidebar.IsVisible = false;
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
        }
        else if (module == "tasks")
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = false;
            TasksSidebar.IsVisible       = _sidebarOpen;
            TasksSidebar.Refresh();
            // С прошлого чтения раздела в базах что-то коммитили (правка в конструкторе или
            // календаре, запись расширения, чужая правка) — перечитать, даже если галочки те же.
            TasksModule.SetActiveIds(TasksSidebar.GetActiveIds(),
                                     force: RefreshPlan.IsStale(TasksModule.DataStamp, LastChangeStamp()));
        }
        else if (module == "library")
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = false;
            LibrarySidebar.IsVisible     = _sidebarOpen;
            LibrarySidebar.Refresh();
            // С прошлого чтения раздела в базах что-то коммитили — перечитать, даже если
            // галочки те же (то же правило, что у задач).
            LibraryModule.SetActiveIds(LibrarySidebar.GetActiveIds(),
                                       force: RefreshPlan.IsStale(LibraryModule.DataStamp, LastChangeStamp()));
        }
        else if (module == "constructor")
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = _sidebarOpen;
            ConstructorSidebar.Refresh();
            ConstructorModule.Refresh();
            // Синхронизируем кнопки-тогглы с восстановленным состоянием.
            ConstructorModule.SetRecordPanelOpen(_recordPanelLogicalOpen);
            RecordPanel.SetCollectionPanelOpen(_collectionOpen);
        }
        else if (module == "settings") // собственный сайдбар со списком разделов, без панели записи
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = false;
            SettingsSidebar.IsVisible    = _sidebarOpen;
            SettingsModule.ShowCategory(SettingsSidebar.SelectedKey);
        }
        else if (module == "profile") // собственный сайдбар со списком разделов, без панели записи
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = false;
            ProfileSidebar.IsVisible     = _sidebarOpen;
            ProfileModule.ShowCategory(ProfileSidebar.SelectedKey);
        }
        else if (module == "notes") // собственный сайдбар-дерево файлов, без панели записи
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = false;
            NotesSidebar.IsVisible       = _sidebarOpen;
            // Хранилище живет на диске и меняется мимо программы — перечитываем при каждом заходе
            // (тот же прием, что у остальных сайдбаров; следить за папкой watcher'ом не беремся).
            NotesSidebar.Refresh();
            RestoreLastNoteOnce();
            NotesModule.Refresh();
        }
        else // extensions — собственный сайдбар со списком расширений, без панели записи
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = false;
            ExtensionsSidebar.IsVisible  = _sidebarOpen;
            SyncExtensionsSidebar();
            ExtensionsModule.ShowExtension(ExtensionsSidebar.SelectedKey);
        }

        // Сайдбары мета-разделов и задач видны только в своем режиме.
        if (module != "tasks")      TasksSidebar.IsVisible      = false;
        if (module != "library")    LibrarySidebar.IsVisible    = false;
        if (module != "settings")   SettingsSidebar.IsVisible   = false;
        if (module != "extensions") ExtensionsSidebar.IsVisible = false;
        if (module != "profile")    ProfileSidebar.IsVisible    = false;
        if (module != "notes")      NotesSidebar.IsVisible      = false;

        ApplyColumns();
    }

    // Список расширений в сайдбаре = видимые пункты каталога. Зовется при старте и при каждом
    // заходе в раздел: галочки правятся в другом разделе («Настройки → Расширения»), а сайдбар
    // расширений в это время скрыт — значит перестроить его достаточно к моменту показа.
    //
    // Пересобираем ТОЛЬКО при смене состава: SetItems перерисовывает строки и сбрасывает выбор
    // на первый пункт, а заход в раздел — частое действие. Выбранное расширение переживает
    // перестройку, если осталось видимым.
    // Разделы профиля: «Журнал безопасности» виден только администратору хотя бы одной открытой
    // базы (ProfileRegistry.Visible). Права меняются на ходу — базу открыли или закрыли, админство
    // выдали или отозвали с соседней машины, — поэтому список пересобирается тем же приемом, что
    // и список расширений: набор не изменился — сайдбар не трогаем, иначе выбор раздела прыгал бы
    // под руками при каждом обновлении.
    private void SyncProfileSidebar()
    {
        var admin   = _workspace.Mounted.Any(m => m.Permissions.IsAdmin);
        var visible = ProfileRegistry.Visible(admin);
        var keys    = visible.Select(c => c.Key).ToList();
        if (keys.SequenceEqual(_profileKeys, StringComparer.Ordinal)) return;

        var previous = ProfileSidebar.SelectedKey;
        _profileKeys = keys;
        ProfileSidebar.SetItems(visible.Select(c => new SidebarListItem(c.Key, c.Label, c.Icon)));

        // Раздел, в котором человек стоял, мог пропасть вместе с админством: тогда открывается
        // первый (SetItems уже выбрал его), а иначе возвращаемся туда, где были.
        if (keys.Contains(previous, StringComparer.Ordinal)) ProfileSidebar.Select(previous);
        else if (_profileView is { IsVisible: true } profile) profile.ShowCategory(ProfileSidebar.SelectedKey);
    }

    private void SyncExtensionsSidebar()
    {
        var visible = ViewCatalog.Visible(ViewCatalog.ScopeExtensions);
        var keys    = visible.Select(v => v.Key).ToList();
        if (keys.SequenceEqual(_extensionKeys, StringComparer.Ordinal)) return;

        var previous = ExtensionsSidebar.SelectedKey;
        _extensionKeys = keys;
        ExtensionsSidebar.SetItems(visible.Select(v => new SidebarListItem(v.Key, v.Label, ExtensionsIcon)));
        if (keys.Contains(previous, StringComparer.Ordinal)) ExtensionsSidebar.Select(previous);
    }

    // Заметка, открытая в прошлый раз, восстанавливается ОДИН раз за запуск — при первом заходе
    // в раздел, а не при каждом. Иначе возврат в «Заметки» после закрытия файла снова открывал бы
    // его, и закрыть заметку было бы нельзя.
    private bool _lastNoteRestored;

    private void RestoreLastNoteOnce()
    {
        if (_lastNoteRestored) return;
        _lastNoteRestored = true;

        var last = AppSettings.LoadNotesLastFile();
        if (last.Length == 0) return;
        // Файл могли удалить или переименовать между запусками — тогда просто открываем раздел пустым.
        if (!File.Exists(NotesSidebar.FullPathOf(last))) return;

        NotesSidebar.Reveal(last);
        NotesModule.OpenNote(last);
    }

    private void OnSidebarToggled(bool isOpen)
    {
        _sidebarOpen = isOpen;

        if (_activeModule == "calendar")
            CalendarSidebar.IsVisible = isOpen;
        else if (_activeModule == "tasks")
            TasksSidebar.IsVisible = isOpen;
        else if (_activeModule == "constructor")
            ConstructorSidebar.IsVisible = isOpen;
        else if (_activeModule == "settings")
            SettingsSidebar.IsVisible = isOpen;
        else if (_activeModule == "profile")
            ProfileSidebar.IsVisible = isOpen;
        else if (_activeModule == "notes")
            NotesSidebar.IsVisible = isOpen;
        else if (_activeModule == "library")
            LibrarySidebar.IsVisible = isOpen;
        else // extensions
            ExtensionsSidebar.IsVisible = isOpen;

        ApplyColumns();
    }

    // Запоминаем последнюю ширину сайдбара, чтобы вернуть ее при повторном открытии
    private double _lastSidebarWidth = 220;

    private const double SidebarMin = 180;
    private const double SidebarMax = 500;

    // Единая точка раскладки трех колонок (сайдбар | контент | правая панель) и обеих ручек
    // ресайза. Вызывается при любом изменении состояния панелей. Колонки Grid сами
    // учитывают ширину открытого сайдбара: контент/правая панель растягиваются на остаток.
    //
    //   сайдбар (col2)   — пиксельная ширина при открытии, 0 при закрытии
    //   контент  (col3)  — Star (растягивается), либо 0 когда коллекция скрыта
    //   разделитель(col4)— виден только когда контент и правая панель открыты одновременно
    //   панель   (col5)  — пиксельная при открытом контенте; Star когда коллекция скрыта; 0 когда закрыта
    //
    // Жильцов у правой колонки два: страница записи в конструкторе и панель чтения документа
    // в библиотеке. Разделы показываются по одному, поэтому одновременно они не встречаются —
    // но ширина у них РАЗНАЯ (читают шире, чем правят), и пределы колонки берутся у того, кто
    // сейчас в ней живет.
    private void ApplyColumns()
    {
        var cols = RootGrid.ColumnDefinitions;
        var inConstructor = _activeModule == "constructor";
        var inLibrary     = _activeModule == "library";

        // Центральный блок скрывается только в конструкторе; в календаре/настройках он всегда «открыт».
        var collection = !inConstructor || _collectionOpen;
        // Панель записи существует только в конструкторе, панель чтения — только в библиотеке.
        var record     = inConstructor && _recordPanelLogicalOpen;
        var reader     = inLibrary && _readerOpen;
        var right      = record || reader;

        // ── Сайдбар (col 2) ──
        if (_sidebarOpen)
        {
            cols[2].MinWidth = SidebarMin;
            cols[2].MaxWidth = SidebarMax;
            cols[2].Width    = new GridLength(_lastSidebarWidth, GridUnitType.Pixel);
        }
        else
        {
            cols[2].MinWidth = 0;
            cols[2].MaxWidth = 0;
            cols[2].Width    = new GridLength(0);
        }

        // ── Контент (col 3) | разделитель (col 4) | правая панель (col 5) ──
        if (collection && right)             // обе панели открыты
        {
            cols[3].Width    = new GridLength(1, GridUnitType.Star);
            cols[4].Width    = GridLength.Auto;
            cols[5].MinWidth = reader ? AppSettings.LibraryReaderWidthMin : AppSettings.RecordPanelWidthMin;
            cols[5].MaxWidth = reader ? AppSettings.LibraryReaderWidthMax : AppSettings.RecordPanelWidthMax;
            cols[5].Width    = new GridLength(reader ? _readerWidth : _recordPanelWidth, GridUnitType.Pixel);
        }
        else if (collection)                 // только коллекция → во всю ширину
        {
            cols[3].Width    = new GridLength(1, GridUnitType.Star);
            cols[4].Width    = new GridLength(0);
            cols[5].MinWidth = 0;
            cols[5].MaxWidth = double.PositiveInfinity;
            cols[5].Width    = new GridLength(0);
        }
        else                                 // только запись (коллекция скрыта) → во всю ширину
        {
            cols[3].Width    = new GridLength(0);
            cols[4].Width    = new GridLength(0);
            cols[5].MinWidth = 0;
            cols[5].MaxWidth = double.PositiveInfinity;
            cols[5].Width    = new GridLength(1, GridUnitType.Star);
        }

        // ── Видимость блоков ──
        ConstructorModule.IsVisible    = inConstructor && collection;
        RecordPanel.IsVisible          = record;
        LibraryReader.IsVisible        = reader;
        RecordPanelSeparator.IsVisible = collection && right;

        // ── Ручки ресайза ──
        // Сайдбара — когда сайдбар открыт (висит на его правом крае поверх контента/панели).
        ResizeGrip.IsVisible       = _sidebarOpen;
        // Правой панели — только когда контент и панель открыты одновременно (граница между ними).
        RecordResizeGrip.IsVisible = collection && right;
    }

    // ── Перетаскивание ручки-блока для изменения ширины сайдбара ─────────────────

    private bool   _gripDragging;
    private double _gripStartX;
    private double _gripStartWidth;

    private void OnGripPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var col = RootGrid.ColumnDefinitions[2];
        _gripDragging   = true;
        _gripStartX     = e.GetPosition(RootGrid).X;
        _gripStartWidth = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        e.Pointer.Capture((Avalonia.Input.IInputElement?)sender);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_gripDragging) return;

        var dx       = e.GetPosition(RootGrid).X - _gripStartX;
        var newWidth = Math.Clamp(_gripStartWidth + dx, SidebarMin, SidebarMax);

        var col = RootGrid.ColumnDefinitions[2];
        col.Width = new Avalonia.Controls.GridLength(newWidth, Avalonia.Controls.GridUnitType.Pixel);
        _lastSidebarWidth = newWidth;
    }

    private void OnGripReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _gripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Правая панель «Страница записи» ─────────────────────────────────────────

    private int  _recordPanelWidth       = AppSettings.RecordPanelWidthDefault;
    // Логическое состояние панели — относится только к конструктору. В календаре панель
    // всегда физически скрыта, но при возврате в конструктор восстанавливаем это значение.
    private bool _recordPanelLogicalOpen;
    // Последняя открытая запись — нужна toggle-кнопке в навбаре коллекции,
    // чтобы повторно открыть ту же запись после закрытия панели.
    // Последняя открытая в панели запись — ВМЕСТЕ с ее базой (DbId=0 — текущая). База обязательна:
    // переоткрытие по toggle навбара иначе теряло бы ее и искало межбазовую запись в текущей БД.
    private (int DbId, int RecordId)? _lastOpened;

    private int? FirstRecordIdOfCurrentCollection()
    {
        var cid = ConstructorModule.CurrentCollectionId;
        if (cid is null) return null;
        // Одна строка сиком по индексу (CollectionId, Position), а не вся коллекция ради первого
        // Id, как было: на больших коллекциях это секунды и память, а NULL в «Названии» старой
        // записи ронял приложение из EF-проекции (raw-читатель страниц терпит NULL).
        var repo = CurrentConstructorRepo;
        try
        {
            return repo.GetRecordsPageAfter(cid.Value, null, null, 1).FirstOrDefault()?.Id;
        }
        catch (Exception ex) when (DbHealthMonitor.IsBusyError(ex) || DbHealthMonitor.IsUnavailableError(ex))
        {
            DbErrors.HandleRead(ex, repo.DbPath);   // база занята/пропала — панель просто не откроется
            return null;
        }
    }

    // Вернуть панель на записи прошлого сеанса. Молча отступаем во всех сомнительных случаях:
    // запись удалили, ее базу отключили или сменили профиль (прав на нее может уже не быть),
    // пользователь успел сам открыть другую — открытая «не та» запись хуже закрытой панели.
    // Сообщений не показываем: человек не просил ничего открывать, он просто запустил программу.
    private void RestoreLastRecordPanel()
    {
        if (_recordPanelLogicalOpen) return;
        if (AppSettings.ParseConstructorLastRecord(AppSettings.LoadConstructorLastRecord()) is not { } last) return;

        // Идем строго по сохраненной базе (ноль разбор уже отсек): база могла не смонтироваться
        // к этому моменту или вовсе выпасть из реестра — тогда панель просто не откроется.
        var repo = RelationDbResolver.RepoFor(last.DbId);
        if (repo is null) return;

        try
        {
            if (repo.GetRecord(last.RecordId) is null) return;
        }
        catch (Exception ex) when (DbHealthMonitor.IsBusyError(ex) || DbHealthMonitor.IsUnavailableError(ex))
        {
            return;   // база занята или пропала — панель просто не откроется
        }

        OpenRecordPanel(last.DbId, last.RecordId);
    }

    private void OpenRecordPanel(int recordId) => OpenRecordPanel(0, recordId);

    // dbId>0 — межбазовый переход (по чипу связи на запись в ДРУГОЙ .db): панель открывается из
    // репозитория ЦЕЛЕВОЙ БД (ее схема/правки), активную коллекцию НЕ переключаем — панель
    // самодостаточна. dbId=0 — обычный переход в текущей БД.
    private void OpenRecordPanel(int dbId, int recordId)
    {
        var repo = dbId > 0 ? RelationDbResolver.RepoFor(dbId) : CurrentConstructorRepo;
        if (repo is null)
        {
            // Раньше — молчаливый return: клик по «→» выглядел как «кнопка не работает».
            Notify("База связанной записи недоступна — открыть ее нельзя.");
            return;
        }

        // Коллекцию берем У САМОЙ ЗАПИСИ (переход по чипу связи открывает запись ДРУГОЙ
        // коллекции — панель должна показать ее собственную схему полей).
        var ownCollectionId = repo.GetRecord(recordId)?.CollectionId;

        // МЕЖБАЗОВЫЙ переход к несуществующей записи фолбэка иметь НЕ МОЖЕТ: id активной коллекции
        // относится к ДРУГОЙ базе, и панель открывалась бы с чужой схемой и пустыми полями — снаружи
        // это выглядит как «ничего не произошло». Внутри своей базы фолбэк оставляем прежним.
        if (ownCollectionId is null && dbId > 0)
        {
            Notify("Связанная запись не найдена в своей базе — возможно, она удалена.");
            return;
        }

        var collectionId = ownCollectionId ?? ConstructorModule.CurrentCollectionId;
        if (collectionId is null) return;

        // dbId прокидываем в панель: ее внутрибазовые связи должны резолвиться в базе ПОКАЗЫВАЕМОЙ
        // записи, а не в активной (панель могла быть открыта межбазовым переходом).
        RecordPanel.Show(repo, recordId, collectionId.Value, dbId);
        _recordPanelLogicalOpen = true;
        // Запоминаем ВМЕСТЕ С БАЗОЙ: SetRecordPanelOpen ниже переключает кнопку навбара, та поднимает
        // RecordPanelToggled, и его обработчик переоткрывает запись по _lastOpened. Пока хранился
        // голый id, переоткрытие шло через перегрузку с dbId=0 — межбазовая запись искалась в ТЕКУЩЕЙ
        // базе, не находилась и панель перерисовывалась пустой поверх уже правильно открытой.
        _lastOpened             = (dbId, recordId);
        ConstructorModule.SetRecordPanelOpen(true);
        AppSettings.SaveRecordPanelOpen(true);
        // Пишем всегда, а не только при включенном восстановлении: настройку могут включить
        // позже, и тогда программа должна знать, куда возвращаться. Ноль здесь означает «база
        // приложения», и внутри сеанса этого хватает, но значение переживает перезапуск —
        // записываем НАСТОЯЩИЙ id базы (как DataActions выше), иначе после старта с другой
        // активной базой мы открыли бы запись с тем же номером из чужой базы.
        AppSettings.SaveConstructorLastRecord(dbId > 0 ? dbId : _currentConstructorDbId, recordId);
        ApplyColumns();
    }

    private void CloseRecordPanel()
    {
        RecordPanel.Clear();
        _recordPanelLogicalOpen = false;
        // При закрытии записи коллекция всегда должна быть видна —
        // иначе можно остаться с пустым экраном (обе панели скрыты).
        _collectionOpen = true;
        ConstructorModule.SetRecordPanelOpen(false);
        RecordPanel.SetCollectionPanelOpen(true);
        AppSettings.SaveRecordPanelOpen(false);
        ApplyColumns();
    }

    // Скрытие/показ центрального блока коллекции — кнопка в навбаре записи.
    // Панель записи при этом остается открытой (кнопка живет внутри нее).
    private void ApplyConstructorContentVisibility(bool isOpen)
    {
        var reopened = isOpen && !_collectionOpen;
        _collectionOpen = isOpen;
        RecordPanel.SetCollectionPanelOpen(isOpen);
        ApplyColumns();

        // Пока блок коллекции был свернут, таблицу не перечитывали (скрытое не обновляется —
        // см. ApplyLocalChanges); если с ее последнего чтения в базе коммитили — перечитать сейчас.
        if (reopened && RefreshPlan.IsStale(ConstructorModule.DataStamp,
                                            _lastChange.GetValueOrDefault(_currentConstructorDbId)))
            ConstructorModule.Refresh();
    }

    // ── Перетаскивание ручки правой панели ─────────────────────────────────────
    //
    // Ручка одна на обеих жильцов колонки, а пределы и сохраняемая ширина у них разные —
    // поэтому каждый обработчик сперва спрашивает, кто сейчас в колонке.

    private bool   _recordGripDragging;
    private double _recordGripStartX;
    private double _recordGripStartWidth;

    /// <summary>В правой колонке сейчас панель чтения, а не страница записи.</summary>
    private bool ReaderInRightColumn => _activeModule == "library" && _readerOpen;

    private void OnRecordGripPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var col = RootGrid.ColumnDefinitions[5];
        _recordGripDragging   = true;
        _recordGripStartX     = e.GetPosition(RootGrid).X;
        _recordGripStartWidth = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        // Текст описания на время перетаскивания не переверстывается: иначе каждое движение
        // мыши стоит 20–50 мс раскладки, и ручка идет рывками (см. FreezeContentWidth).
        if (!ReaderInRightColumn) RecordPanel.FreezeContentWidth(true);
        e.Pointer.Capture((Avalonia.Input.IInputElement?)sender);
        e.Handled = true;
    }

    private void OnRecordGripMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_recordGripDragging) return;

        var reader = ReaderInRightColumn;

        // Тянем за левый край — движение мыши вправо уменьшает ширину панели.
        var dx       = e.GetPosition(RootGrid).X - _recordGripStartX;
        var newWidth = Math.Clamp(
            _recordGripStartWidth - dx,
            reader ? AppSettings.LibraryReaderWidthMin : AppSettings.RecordPanelWidthMin,
            reader ? AppSettings.LibraryReaderWidthMax : AppSettings.RecordPanelWidthMax);

        var col = RootGrid.ColumnDefinitions[5];
        col.Width = new Avalonia.Controls.GridLength(newWidth, Avalonia.Controls.GridUnitType.Pixel);
        if (reader) _readerWidth       = (int)newWidth;
        else        _recordPanelWidth  = (int)newWidth;
    }

    private void OnRecordGripReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_recordGripDragging)
        {
            if (ReaderInRightColumn) AppSettings.SaveLibraryReaderWidth(_readerWidth);
            else                     AppSettings.SaveRecordPanelWidth(_recordPanelWidth);
        }
        _recordGripDragging = false;
        RecordPanel.FreezeContentWidth(false);   // текст встает по новой ширине за один проход
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Правая панель «Чтение документа» ───────────────────────────────────────

    private int  _readerWidth = AppSettings.LibraryReaderWidthDefault;
    private bool _readerOpen;

    /// <summary>
    /// Последний читанный документ — его возвращает toggle навбара. Между запусками не
    /// сохраняется намеренно: панель чтения открывают ради конкретного документа и конкретного
    /// слова в нем, а восстановленная наутро панель показывала бы вчерашний текст без повода.
    /// </summary>
    private DocRecord? _lastRead;

    private void OpenLibraryReader(DocRecord doc)
    {
        if (doc.Path.Length == 0) return;

        _lastRead   = doc;
        _readerOpen = true;
        LibraryReader.Show(doc, LibraryModule.SearchQuery);
        LibraryModule.SetReaderOpen(true);
        ApplyColumns();
    }

    private void CloseLibraryReader()
    {
        _readerOpen = false;
        LibraryReader.Clear();
        _libraryView?.SetReaderOpen(false);
        ApplyColumns();
    }
}
