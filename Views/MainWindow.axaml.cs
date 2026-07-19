using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Extensions;
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
    // уведомлением, монитор сам ретраит и возвращает её, когда файл снова доступен.
    private readonly DbHealthMonitor _health;
    // Фоновая проверка чужих правок (~3 с): другие пользователи общей базы (сетевая папка)
    // или другая копия приложения меняют данные — сайдбары/таблица/календарь обновляются сами.
    private readonly ChangePoller _changes;
    // БД, потерянные на ходу — чтобы при возврате показать «снова доступна».
    private readonly HashSet<int> _lostDbIds = [];
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
    // _recordPanelLogicalOpen — правая панель записи (col 5), объявлено ниже.

    public MainWindow(Workspace workspace)
    {
        InitializeComponent();

        _workspace   = workspace;
        _mountQueue  = new MountQueue(workspace);
        _health      = new DbHealthMonitor(workspace, _mountQueue);
        _changes     = new ChangePoller(workspace, _health);

        // Ошибки записи на пользовательских действиях (правка ячейки, CRUD): «занята» —
        // тост, «недоступна» — тост + сигнал монитору. См. Utils/DbErrors.
        DbErrors.Notify        = Notify;
        DbErrors.ReportFailure = _health.ReportFailureByPath;
        // Календарь и панель записи пока работают с дефолтной БД (мульти-БД календаря — Этап 3.3).
        _categories  = workspace.Default.Categories;
        _constructor = workspace.Default.Constructor;
        _currentConstructorDbId = workspace.Default.DbId;

        // Живой статус очереди: процент/оценка времени — только текст заглушки в сайдбарах
        // (без перестройки дерева), смена состояния — ещё и список БД в настройках.
        // События очереди приходят из фонового потока — маршалим в UI.
        _mountQueue.Changed += (dbId, stateChanged) => Dispatcher.UIThread.Post(() =>
        {
            if (_mountQueue.StatusOf(dbId) is { } st)
            {
                var failed = st.State == MountQueue.ItemState.Failed;
                ConstructorSidebar.UpdatePendingStatus(dbId, st.Text, failed);
                CalendarSidebar.UpdatePendingStatus(dbId, st.Text, failed);
                TasksSidebar.UpdatePendingStatus(dbId, st.Text, failed);
                // Список БД в настройках живёт теми же процентами/временем, что и сайдбар.
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
            if (manager is null)
            {
                // Не смонтировалась (нет сети/файла) — отдаём монитору под фоновые ретраи:
                // сервер включится — база подключится сама, без действий пользователя.
                var entry = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
                if (entry is not null) _health.TrackFailed(entry);
            }
            else if (_lostDbIds.Remove(dbId))
                Notify($"База данных «{manager.Name}» снова доступна.");
            SyncMountedToUi();   // узел «оживает»: календарь/задачи подхватывают данные
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
                : null);

        // Live mount/unmount: добавление/удаление БД в настройках сразу отражается в
        // сайдбаре конструктора без перезапуска. Новая база встаёт в хвост очереди —
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

        // Клик по заглушке немонтированной базы — подсказка с текущим статусом.
        ConstructorSidebar.PendingDatabaseTapped += async (_, dbId) => await ShowPendingDatabaseInfo(dbId);
        CalendarSidebar.PendingDatabaseTapped    += async (_, dbId) => await ShowPendingDatabaseInfo(dbId);
        TasksSidebar.PendingDatabaseTapped       += async (_, dbId) => await ShowPendingDatabaseInfo(dbId);

        // Начальное состояние: раздел при запуске берём из настроек (стандарт — конструктор).
        var startupModule = AppSettings.LoadStartupModule();
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

        // Сайдбар расширений: пункты из ExtensionRegistry (одна иконка на все); выбор открывает блок.
        ExtensionsSidebar.Title = "Расширения";
        ExtensionsSidebar.SetItems(ExtensionRegistry.All.Select(e => new SidebarListItem(e.Id, e.Name, ExtensionsIcon)));
        ExtensionsSidebar.CategorySelected += (_, key) => ExtensionsModule.ShowExtension(key);

        // Сайдбар профиля: пункты из ProfileRegistry; выбор раздела открывает его в основной области.
        ProfileSidebar.Title = "Профиль";
        ProfileSidebar.SetItems(ProfileRegistry.All.Select(c => new SidebarListItem(c.Key, c.Label, c.Icon)));
        ProfileSidebar.CategorySelected += (_, key) => ProfileModule.ShowCategory(key);
        // Панели администратора и истории работают со смонтированными БД рабочего пространства.
        ProfileModule.Initialize(workspace);

        // Sidebar коллекций передаёт активные id в CalendarModule
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

        // Сайдбар задач: чекбоксы фильтруют агрегат, клик по коллекции открывает/закрывает её,
        // тумблеры user-полей и смена цвета приходят событием CalendarsChanged.
        TasksSidebar.ActiveIdsChanged += (_, ids) => TasksModule.SetActiveIds(ids);
        TasksSidebar.CalendarsChanged += (_, _) =>
        {
            TasksModule.SetActiveIds(TasksSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
        };
        TasksSidebar.CollectionOpened += (_, sel) => TasksModule.SetOpenCollection(sel);
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

        // Дерево конструктора: выбор коллекции открывает её в ConstructorModule из её БД.
        // Если центральный блок был скрыт (свёрнут из записи) — снова показываем его.
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
        // «Добавить свойство» из панели записи — тем же путём, что и «+» в таблице:
        // новое поле создаётся, колонка появляется в таблице, а FieldsChanged ниже
        // перестраивает список свойств панели (новое поле сразу видно и там, и там).
        RecordPanel.AddPropertyRequested      += (_, _)          => ConstructorModule.CreatePropertyFromPanel();
        // Обратная синхронизация: правка ячейки в таблице → обновить открытую панель записи,
        // если это та же запись (значения свойств, имя и описание перечитываются из БД).
        ConstructorModule.RecordValueEdited   += (_, rid) =>
        {
            if (_recordPanelLogicalOpen && rid == _lastOpenedRecordId)
                RecordPanel.ReloadFromDb();
        };
        // Состав/имена свойств изменились в таблице (добавили/удалили/переименовали/дублировали поле) —
        // открытая панель записи перестраивает список свойств, чтобы новое поле сразу появилось.
        ConstructorModule.FieldsChanged       += (_, _) =>
        {
            if (_recordPanelLogicalOpen) RecordPanel.ReloadFromDb();
        };

        // Toggle правой панели записи из навбара коллекции.
        // Если запись ранее была открыта (_lastOpenedRecordId есть) — переоткрываем её;
        // иначе берём первую запись текущей коллекции (на первом запуске
        // _lastOpenedRecordId ещё null, но коллекция уже выбрана).
        ConstructorModule.RecordPanelToggled += (_, isOpen) =>
        {
            if (isOpen)
            {
                var rid = _lastOpenedRecordId ?? FirstRecordIdOfCurrentCollection();
                if (rid is int id) OpenRecordPanel(id);
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
        };

        // Уровень масштабирования: применяем сохранённый и реагируем на изменения из настроек.
        ApplyScale(Scaling.Current);
        Scaling.ScaleChanged += ApplyScale;
        Closed += (_, _) => Scaling.ScaleChanged -= ApplyScale;

        // Автообновление: тихая проверка после открытия окна (уже показанное окно —
        // владелец модалки «Обновить/Отмена»). Проверка папки идёт в фоне, UI не блокирует.
        // В фоне (окно неактивно) частота проверки чужих правок снижается.
        Activated += (_, _) => _changes.SetForeground(true);

        Opened += async (_, _) =>
        {
            // Остальные БД реестра — в фоновую очередь (мелкие первыми): окно уже показано,
            // пользователь работает с дефолтной базой, остальные оживают по мере готовности.
            // Затем — монитор доступности (пропажа сервера/файла) и синхронизация чужих правок.
            StartBackgroundMounts();
            _health.Start();
            _changes.Start();

            if (AppSettings.LoadAutoUpdate())
                await SettingsModule.CheckForUpdatesOnStartupAsync();
        };
        // Выход во время фонового обновления базы: прогресс перекладки сохраняется
        // (возобновляемая миграция — см. DatabaseManager.SchemaCompact), но честно
        // предупреждаем, что обновление продолжится при следующем запуске.
        Closing += async (_, e) =>
        {
            if (_closeConfirmed) return;
            var busy = _mountQueue.Pending().FirstOrDefault(p => p.State == MountQueue.ItemState.Mounting);
            if (busy is null) return;

            e.Cancel = true;
            var ok = await new ConfirmDialog(
                $"База данных «{busy.Name}» сейчас обновляется ({busy.Text}). " +
                "Можно выйти: прогресс сохранён, обновление продолжится с этого места " +
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
        };
    }

    // Выход уже подтверждён диалогом «база обновляется» — повторно не спрашиваем.
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

        // Flyout фильтра/сортировки в таблице — он живёт отдельным popup.
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
        var text = st.State switch
        {
            MountQueue.ItemState.Queued =>
                $"База данных «{st.Name}» в очереди на подключение — она откроется автоматически, " +
                "работать с остальными базами можно уже сейчас.",
            MountQueue.ItemState.Mounting =>
                $"База данных «{st.Name}» подключается: {st.Text}. Она откроется автоматически, " +
                "работать с остальными базами можно уже сейчас.",
            _ =>
                $"База данных «{st.Name}» {st.Text}. Приложение периодически пытается " +
                "переподключиться — как только файл снова станет доступен, база откроется сама. " +
                "Можно также подключить её заново вручную в настройках («Управление базой данных»).",
        };
        await ConfirmDialog.Info(text).ShowDialog(this);
    }

    // Потеря смонтированной БД на ходу (сервер выключился, файл удалили): зеркало
    // отключения из настроек, но запись реестра остаётся — монитор будет ретраить,
    // при возврате файла база оживёт сама (очередь → Completed → SyncMountedToUi).
    private void OnDatabaseLost(int dbId, string reason)
    {
        var db = _workspace.Get(dbId);
        if (db is null) return;   // уже размонтирована (гонка с отключением из настроек)
        var name = db.Name;

        if (_currentConstructorDbId == dbId)
        {
            _currentConstructorDbId = _workspace.Default.DbId;
            ConstructorModule.DetachDatabase(_workspace.Default.Constructor);
        }
        _workspace.Unmount(dbId);
        _mountQueue.MarkFailed(dbId, name, $"недоступна ({reason})");
        _lostDbIds.Add(dbId);
        SyncMountedToUi();
        Notify($"База данных «{name}» недоступна ({reason}) — приложение переподключится автоматически.");
    }

    // Чужие правки в БД (другой пользователь общей базы / другая копия приложения):
    // обновить только затронутое. Невидимые модули не трогаем — они и так перечитывают
    // данные при переключении на них (ShowModule → Refresh).
    private void OnRemoteChanges(int dbId, IReadOnlyList<ChangePoller.RemoteChange> changes)
    {
        if (_workspace.Get(dbId) is null) return;   // база уже размонтирована/потеряна

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

        // Открытая панель записи могла показывать изменённую запись — перечитать значения.
        if (_recordPanelLogicalOpen && _currentConstructorDbId == dbId)
            RecordPanel.ReloadFromDb();
    }

    // Ненавязчивое уведомление в углу окна (якорь — панель модулей: стабильна и видима
    // в любом разделе). Тем же тостом пользуются ошибки записи (DbErrors).
    private void Notify(string message) =>
        Controls.Widgets.Toast.Show(ActivityBarControl, new TextBlock
        {
            Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 340,
        }, PlacementMode.RightEdgeAlignedBottom, seconds: 6);

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

    // Пересобрать боковые панели и обновить набор БД календаря после монтирования/
    // размонтирования/получения допуска. Немонтированные БД очереди показываются
    // заглушками со статусом во всех трёх сайдбарах.
    private void SyncMountedToUi()
    {
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

    // Клик по запертой базе в сайдбаре: с паролем — диалог ввода (верный пароль даёт
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
                "Запрос на доступ уже отправлен — администратор увидит его в своей панели " +
                "администратора при следующем открытии этой базы.")
                .ShowDialog(this);
        }
    }

    private void ShowModule(string module)
    {
        _activeModule = module;

        CalendarModule.IsVisible   = module == "calendar";
        TasksModule.IsVisible      = module == "tasks";
        SettingsModule.IsVisible   = module == "settings";
        ExtensionsModule.IsVisible = module == "extensions";
        ProfileModule.IsVisible    = module == "profile";
        // Видимость ConstructorModule зависит ещё и от _collectionOpen — её ставит ApplyColumns.

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
        else // extensions — собственный сайдбар со списком расширений, без панели записи
        {
            CalendarSidebar.IsVisible    = false;
            ConstructorSidebar.IsVisible = false;
            ExtensionsSidebar.IsVisible  = _sidebarOpen;
            ExtensionsModule.ShowExtension(ExtensionsSidebar.SelectedKey);
        }

        // Сайдбары мета-разделов и задач видны только в своём режиме.
        if (module != "tasks")      TasksSidebar.IsVisible      = false;
        if (module != "settings")   SettingsSidebar.IsVisible   = false;
        if (module != "extensions") ExtensionsSidebar.IsVisible = false;
        if (module != "profile")    ProfileSidebar.IsVisible    = false;

        ApplyColumns();
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
        else // extensions
            ExtensionsSidebar.IsVisible = isOpen;

        ApplyColumns();
    }

    // Запоминаем последнюю ширину сайдбара, чтобы вернуть её при повторном открытии
    private double _lastSidebarWidth = 220;

    private const double SidebarMin = 180;
    private const double SidebarMax = 500;

    // Единая точка раскладки трёх колонок (сайдбар | контент | запись) и обеих ручек
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
    private int? _lastOpenedRecordId;

    private int? FirstRecordIdOfCurrentCollection()
    {
        var cid = ConstructorModule.CurrentCollectionId;
        if (cid is null) return null;
        var first = CurrentConstructorRepo.GetRecords(cid.Value).FirstOrDefault();
        return first?.Id;
    }

    private void OpenRecordPanel(int recordId)
    {
        var collectionId = ConstructorModule.CurrentCollectionId;
        if (collectionId is null) return;

        // Панель записи открывается из репозитория той БД, чья коллекция сейчас активна.
        RecordPanel.Show(CurrentConstructorRepo, recordId, collectionId.Value);
        _recordPanelLogicalOpen = true;
        _lastOpenedRecordId     = recordId;
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
    // Панель записи при этом остаётся открытой (кнопка живёт внутри неё).
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
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
