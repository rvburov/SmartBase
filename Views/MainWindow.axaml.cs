using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
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
                SettingsModule.UpdateQueueStatus(dbId, st.Text, failed);
            }
            if (stateChanged) SettingsModule.RefreshDatabases();
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

        // Чужие правки из другой копии приложения → обновить затронутые части UI.
        _changes.RemoteChanges += (dbId, rows) =>
            Dispatcher.UIThread.Post(() => OnRemoteChanges(dbId, rows));

        // Раздел настроек «Управление базой данных»: реестр + статус монтирования/очереди.
        SettingsModule.Initialize(workspace.Registry,
            dbId => _workspace.Get(dbId) is not null,
            dbId => _mountQueue.StatusOf(dbId) is { } st
                ? (st.Text, st.State == MountQueue.ItemState.Failed)
                : null,
            // Сведения о файле — снимок, снятый при монтировании (без обращения к файлу с UI-потока).
            dbId => _workspace.Get(dbId)?.Storage,
            // Обслуживать файл (сжать/копия) может администратор базы.
            dbId => _workspace.Get(dbId) is { Provider: "sqlite" } m && m.Permissions.IsAdmin);

        // Live mount/unmount: добавление/удаление БД в настройках сразу отражается в
        // сайдбаре конструктора без перезапуска. Новая база встает в хвост очереди —
        // даже если сейчас мигрирует другая, ничего не блокируется.
        SettingsModule.DatabaseAdded += (_, entry) =>
        {
            _mountQueue.Enqueue(entry);
            SyncMountedToUi();   // заглушка «в очереди…» появляется сразу
        };
        SettingsModule.DatabaseRemoved += (_, dbId) =>
        {
            // Если открыта коллекция удаляемой БД — отвязываем модуль на дефолтную БД.
            if (_currentConstructorDbId == dbId)
            {
                _currentConstructorDbId = _workspace.Default.DbId;
                ConstructorModule.DetachDatabase(_workspace.Default.Constructor);
            }
            _mountQueue.Forget(dbId);
            _health.Forget(dbId);
            _lostDbIds.Remove(dbId);
            _workspace.Unmount(dbId);
            SyncMountedToUi();
        };
        SettingsModule.DatabaseReconnectRequested += (_, dbId) =>
        {
            // Повторная попытка монтирования (например, сетевая папка снова доступна).
            var entry = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
            if (entry is null) return;
            _mountQueue.Enqueue(entry);
            SyncMountedToUi();
        };
        // Обслуживание файла базы из «Управления базой данных»: сжатие через очередь,
        // с отключением базы на время VACUUM.
        SettingsModule.DatabaseCompactRequested += (_, dbId) => _ = CompactDatabaseAsync(dbId);
        // Раздел «Резервное копирование»: ручной запуск — службе; ход и итог — обратно в
        // раздел (прогресс приходит из фонового потока — маршалим, остальное уже на UI).
        SettingsModule.BackupNowRequested += (_, dbId) => _ = _backup.RunNowAsync(dbId);
        _backup.BackupStarted  += dbId => SettingsModule.OnBackupStarted(dbId);
        _backup.BackupProgress += (dbId, msg, frac) =>
            Dispatcher.UIThread.Post(() => SettingsModule.OnBackupProgress(dbId, msg, frac));
        _backup.BackupFinished += (dbId, ok, msg) => SettingsModule.OnBackupFinished(dbId, ok, msg);
        // Переименование в окне настроек базы: реестр уже обновлен — меняем имя
        // смонтированной БД на месте и перечитываем сайдбары, без ремаунта.
        SettingsModule.DatabaseRenamed += (_, e) =>
        {
            if (_workspace.Get(e.Id) is { } renamed) renamed.Name = e.Name;
            SyncMountedToUi();
        };
        _mountQueue.MaintenanceFinished += (dbId, error) => Dispatcher.UIThread.Post(() =>
        {
            var name = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId)?.Name ?? "";
            if (error is not null)
                Notify($"Сжать файл базы «{name}» не удалось: {error}");
            else if (_compactSizes.Remove(dbId, out var sizes))
                NotifyInfo($"Файл базы «{name}» сжат: {DatabaseRowSize(sizes.Before)} → {DatabaseRowSize(sizes.After)}.");
        });

        // Тумблер «Общее → Уведомления»: включаем/выключаем службу на лету.
        SettingsModule.NotificationsEnabledChanged += (_, on) => _notifications.SetEnabled(on);
        // Тумблеры уведомлений по задачам — тем же путем, каждый сам по себе.
        SettingsModule.TaskAssignedNotificationsChanged += (_, on) => _notifications.SetTaskAssignedEnabled(on);
        SettingsModule.TaskStatusNotificationsChanged   += (_, on) => _notifications.SetTaskStatusEnabled(on);
        // Тумблер «Общее → Фоновый режим»: показать/скрыть значок в трее (и вернуть окно при выкл).
        SettingsModule.BackgroundEnabledChanged += (_, on) => OnBackgroundChanged(on);

        // Инициализируем модули через AXAML-конструкторы.
        // Календарь агрегирует события только из ДОПУЩЕННЫХ БД (без допуска — данные не видны).
        CalendarModule.Initialize(GrantedDbs(), _categories);
        TasksModule.Initialize(GrantedDbs());
        ConstructorModule.Initialize(_constructor);
        // Допущенные БД — для окна «Расположение» записи (перенос в другую коллекцию/БД).
        ConstructorModule.SetDatabases(GrantedDbs());

        // Боковые панели: календарь — только допущенные БД; конструктор — все смонтированные
        // (недопущенные показываются замком, клик по замку — запрос доступа).
        CalendarSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Calendar);
        TasksSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Tasks);
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
        var notesVault = AppSettings.LoadNotesVault();
        NotesSidebar.SetVault(notesVault);
        NotesModule.SetVault(notesVault);

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
        SettingsSidebar.CategorySelected += (_, key) => SettingsModule.ShowCategory(key);

        // Сайдбар расширений: пункты из каталога — то есть из реестра расширений, но уже без
        // скрытых («Настройки → Расширения → Отображение раздела расширений»). Одна иконка на
        // все, выбор открывает блок; список пересобирается при заходе (SyncExtensionsSidebar).
        ExtensionsSidebar.Title = "Расширения";
        SyncExtensionsSidebar();
        ExtensionsSidebar.CategorySelected += (_, key) => ExtensionsModule.ShowExtension(key);

        // Сайдбар профиля: пункты из ProfileRegistry; выбор раздела открывает его в основной области.
        ProfileSidebar.Title = "Профиль";
        ProfileSidebar.SetItems(ProfileRegistry.All.Select(c => new SidebarListItem(c.Key, c.Label, c.Icon)));
        ProfileSidebar.CategorySelected += (_, key) => ProfileModule.ShowCategory(key);
        // Панели администратора и истории работают со смонтированными БД рабочего пространства.
        ProfileModule.Initialize(workspace);

        NotesSidebar.NoteSelected        += (_, rel)  => NotesModule.OpenNote(rel);
        NotesSidebar.UnsupportedSelected += (_, name) => NotesModule.ShowUnsupported(name);
        NotesSidebar.NoteMoved           += (_, m)    => NotesModule.NotePathChanged(m.OldPath, m.NewPath);
        NotesSidebar.NoteRemoved         += (_, rel)  => NotesModule.NoteRemoved(rel);
        NotesSidebar.VaultChanged        += (_, _)    => NotesModule.Refresh();

        // Переименование из шапки раздела — та же операция, что и из дерева: файл трогает
        // только сайдбар, а раздел узнает о новом пути событием NoteMoved.
        NotesModule.NoteRenamed += (_, r) =>
        {
            // Имя не подошло (занято, запрещенные символы, отказ ФС) — заголовок откатывается
            // и объясняет причину прямо у себя, а не всплывашкой над деревом.
            if (NotesSidebar.RenameFromHeader(r.RelativePath, r.NewName) is { } error)
                NotesModule.RejectRename(error);
        };

        // Кнопка «Открыть настройки» из пустого состояния. Переключаем ровно тем же путем, что и
        // панель активности, иначе подсветка кнопки разъедется с показанным разделом.
        NotesModule.OpenSettingsRequested += (_, _) =>
        {
            ActivityBarControl.SetModuleActive("settings");
            ShowModule("settings");
            SettingsSidebar.Select("notes");
        };

        // Папку хранилища сменили в настройках — раздать новый путь дереву и разделу.
        SettingsModule.NotesVaultChanged += (_, path) =>
        {
            NotesSidebar.SetVault(path);
            NotesModule.SetVault(path);
        };

        // Sidebar коллекций передает активные id в CalendarModule
        CalendarSidebar.ActiveIdsChanged += (_, ids) =>
            CalendarModule.SetActiveIds(ids);

        // Смена цвета коллекции в сайдбаре → перерисовать календарь и дерево конструктора
        CalendarSidebar.CalendarsChanged += (_, _) =>
        {
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
        };

        // После создания/изменения события обновляем Sidebar (могла появиться новая коллекция)
        CalendarModule.CollectionsMayHaveChanged += (_, _) =>
        {
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
        };

        // Сайдбар задач: галочки решают, какие коллекции показывать (клик по строке —
        // та же галочка, как в календаре); тумблеры user-полей и смена цвета приходят
        // событием CalendarsChanged.
        TasksSidebar.ActiveIdsChanged += (_, ids) => TasksModule.SetActiveIds(ids);
        TasksSidebar.CalendarsChanged += (_, _) =>
        {
            TasksModule.SetActiveIds(TasksSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
        };
        // CRUD коллекций из сайдбара задач → обновить календарь и конструктор.
        TasksSidebar.RequestRefresh += (_, _) =>
        {
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
            ConstructorModule.Refresh();
        };
        // Создание/правка задачи могли создать коллекцию или авто-поля («Назначил»/«Статус»).
        TasksModule.CollectionsMayHaveChanged += (_, _) =>
        {
            TasksSidebar.Refresh();
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
            ConstructorModule.Refresh();
        };

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
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorModule.Refresh();
        };
        ConstructorSidebar.CollectionDeleted += (_, _) => ConstructorModule.Refresh();

        // При изменении коллекций из конструктора — обновить sidebar
        ConstructorModule.CollectionsChanged += (_, _) =>
        {
            CalendarSidebar.Refresh();
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
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

        // Ширину панели записи восстанавливаем; сама панель при запуске закрыта
        // (_recordPanelLogicalOpen = false по умолчанию) — без выбранной записи
        // открытая панель показывала бы пустой белый блок.
        _recordPanelWidth = AppSettings.LoadRecordPanelWidth();

        // Первоначальная загрузка фильтра
        CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());

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
            NotesModule.FlushPendingSave();
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
            // Первым делом — черновик заметки на диск: дальше окно может уйти в фоновый режим
            // или закрыться совсем, и несохраненный текст пропал бы.
            NotesModule.FlushPendingSave();

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

        Closed += (_, _) =>
        {
            _changes.Dispose();
            _health.Dispose();
            _notifications.Dispose();
            _backup.Dispose();
            _watchdog.Dispose();
        };
    }

    private readonly UiWatchdog _watchdog;
    private readonly BackupService _backup;

    // Человеческое имя раздела для записей о медленном открытии.
    private static string ModuleTitle(string module) => module switch
    {
        "calendar"    => "Календарь",
        "tasks"       => "Задачи",
        "constructor" => "Конструктор",
        "settings"    => "Настройки",
        "profile"     => "Профиль",
        "notes"       => "Заметки",
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

        // Flyout фильтра/сортировки в таблице — он живет отдельным popup.
        foreach (var table in controls.OfType<ConstructorDb.TableView>())
            table.CloseTransientPopups();
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

    private static string DatabaseRowSize(long bytes) => bytes switch
    {
        < 1024L * 1024        => $"{bytes / 1024.0:N0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:N1} МБ",
        _                     => $"{bytes / 1024.0 / 1024 / 1024:N2} ГБ",
    };

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
        SettingsModule.SetDatabaseStatus(dbId, text);
    }

    // Чужие правки в БД (другой пользователь общей базы / другая копия приложения):
    // обновить только затронутое. Невидимые модули не трогаем — они и так перечитывают
    // данные при переключении на них (ShowModule → Refresh).
    private void OnRemoteChanges(int dbId, IReadOnlyList<ChangePoller.RemoteChange> changes)
    {
        var changedDb = _workspace.Get(dbId);
        if (changedDb is null) return;   // база уже размонтирована/потеряна

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
            ProfileModule.OnRemoteUserChanges(dbId);
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

        // Видимый агрегатный модуль перечитывает события/задачи затронутой БД.
        if (_activeModule == "calendar")
            CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
        else if (_activeModule == "tasks")
            TasksModule.SetActiveIds(TasksSidebar.GetActiveIds());

        // Открытая панель записи могла показывать измененную запись — перечитать значения.
        if (_recordPanelLogicalOpen && _currentConstructorDbId == dbId)
            RecordPanel.ReloadFromDb();
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

        // ⌘Q (macOS — через штатное меню Avalonia, CloseReason=ApplicationShutdown) уже завершает
        // приложение; для Windows/Linux добавляем Ctrl+Q как явный выход, когда окно в фокусе.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Q && e.KeyModifiers.HasFlag(KeyModifiers.Control)) QuitExplicit();
        };
    }

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
        await SettingsModule.CheckForUpdatesOnStartupAsync();   // не бросает — см. SettingsView
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
        CalendarModule.SetDatabases(GrantedDbs());
        TasksModule.SetDatabases(GrantedDbs());
        ConstructorModule.SetDatabases(GrantedDbs());
        CalendarModule.SetActiveIds(CalendarSidebar.GetActiveIds());
        TasksModule.SetActiveIds(TasksSidebar.GetActiveIds());
        SettingsModule.RefreshDatabases();   // обновить статус «подключена/не подключена»
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

        if (_activeModule == "tasks") TasksModule.Refresh();
        if (_recordPanelLogicalOpen && _currentConstructorDbId == dbId) RecordPanel.ReloadFromDb();
    }

    // Клик по запертой базе в сайдбаре: с паролем — диалог ввода (верный пароль дает
    // допуск сразу), без пароля — пояснение про подтверждение администратором (запрос
    // на доступ уже создан при монтировании).
    private async Task RequestDatabaseUnlock(int dbId)
    {
        var db = _workspace.Get(dbId);
        if (db is null) return;
        if (db.Permissions.AccessGranted) { SyncMountedToUi(); return; }

        if (db.Permissions.HasPassword)
        {
            var unlocked = await PasswordDialog
                .ForUnlock(db.Name, db.Permissions.TryUnlock)
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
        if (_activeModule == "notes" && module != "notes") NotesModule.FlushPendingSave();

        _activeModule = module;

        CalendarModule.IsVisible   = module == "calendar";
        TasksModule.IsVisible      = module == "tasks";
        SettingsModule.IsVisible   = module == "settings";
        ExtensionsModule.IsVisible = module == "extensions";
        ProfileModule.IsVisible    = module == "profile";
        NotesModule.IsVisible      = module == "notes";
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
            TasksModule.SetActiveIds(TasksSidebar.GetActiveIds());
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
        else // extensions
            ExtensionsSidebar.IsVisible = isOpen;

        ApplyColumns();
    }

    // Запоминаем последнюю ширину сайдбара, чтобы вернуть ее при повторном открытии
    private double _lastSidebarWidth = 220;

    private const double SidebarMin = 180;
    private const double SidebarMax = 500;

    // Единая точка раскладки трех колонок (сайдбар | контент | запись) и обеих ручек
    // ресайза. Вызывается при любом изменении состояния панелей. Колонки Grid сами
    // учитывают ширину открытого сайдбара: контент/запись растягиваются на остаток.
    //
    //   сайдбар (col2)   — пиксельная ширина при открытии, 0 при закрытии
    //   контент  (col3)  — Star (растягивается), либо 0 когда коллекция скрыта
    //   разделитель(col4)— виден только когда коллекция и запись открыты одновременно
    //   запись   (col5)  — пиксельная при открытой коллекции; Star когда коллекция скрыта; 0 когда закрыта
    private void ApplyColumns()
    {
        var cols = RootGrid.ColumnDefinitions;
        var inConstructor = _activeModule == "constructor";

        // Центральный блок скрывается только в конструкторе; в календаре/настройках он всегда «открыт».
        var collection = !inConstructor || _collectionOpen;
        // Панель записи существует только в конструкторе.
        var record     = inConstructor && _recordPanelLogicalOpen;

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

        // ── Контент (col 3) | разделитель (col 4) | запись (col 5) ──
        if (collection && record)            // обе панели открыты
        {
            cols[3].Width    = new GridLength(1, GridUnitType.Star);
            cols[4].Width    = GridLength.Auto;
            cols[5].MinWidth = AppSettings.RecordPanelWidthMin;
            cols[5].MaxWidth = AppSettings.RecordPanelWidthMax;
            cols[5].Width    = new GridLength(_recordPanelWidth, GridUnitType.Pixel);
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
        RecordPanelSeparator.IsVisible = collection && record;

        // ── Ручки ресайза ──
        // Сайдбара — когда сайдбар открыт (висит на его правом крае поверх коллекции/записи).
        ResizeGrip.IsVisible       = _sidebarOpen;
        // Записи — только когда коллекция и запись открыты одновременно (граница между ними).
        RecordResizeGrip.IsVisible = collection && record;
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
        _collectionOpen = isOpen;
        RecordPanel.SetCollectionPanelOpen(isOpen);
        ApplyColumns();
    }

    // ── Перетаскивание ручки панели записи ─────────────────────────────────────

    private bool   _recordGripDragging;
    private double _recordGripStartX;
    private double _recordGripStartWidth;

    private void OnRecordGripPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var col = RootGrid.ColumnDefinitions[5];
        _recordGripDragging   = true;
        _recordGripStartX     = e.GetPosition(RootGrid).X;
        _recordGripStartWidth = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;
        // Текст описания на время перетаскивания не переверстывается: иначе каждое движение
        // мыши стоит 20–50 мс раскладки, и ручка идет рывками (см. FreezeContentWidth).
        RecordPanel.FreezeContentWidth(true);
        e.Pointer.Capture((Avalonia.Input.IInputElement?)sender);
        e.Handled = true;
    }

    private void OnRecordGripMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (!_recordGripDragging) return;

        // Тянем за левый край — движение мыши вправо уменьшает ширину панели.
        var dx       = e.GetPosition(RootGrid).X - _recordGripStartX;
        var newWidth = Math.Clamp(
            _recordGripStartWidth - dx,
            AppSettings.RecordPanelWidthMin,
            AppSettings.RecordPanelWidthMax);

        var col = RootGrid.ColumnDefinitions[5];
        col.Width = new Avalonia.Controls.GridLength(newWidth, Avalonia.Controls.GridUnitType.Pixel);
        _recordPanelWidth = (int)newWidth;
    }

    private void OnRecordGripReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_recordGripDragging) AppSettings.SaveRecordPanelWidth(_recordPanelWidth);
        _recordGripDragging = false;
        RecordPanel.FreezeContentWidth(false);   // текст встает по новой ширине за один проход
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
