using Avalonia.Controls;
using Avalonia.VisualTree;
using DataBase.Controls.Components;
using DataBase.Controls.Dialogs;
using DataBase.Database;
using DataBase.Database.Entities;
using DataBase.Database.Repositories;
using DataBase.Extensions;
using DataBase.Settings;
using DataBase.Utils;
using DataBase.ViewModels;

namespace DataBase.Views;

public partial class MainWindow : Window
{
    private readonly Workspace _workspace;
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
        // Календарь и панель записи пока работают с дефолтной БД (мульти-БД календаря — Этап 3.3).
        _categories  = workspace.Default.Categories;
        _constructor = workspace.Default.Constructor;
        _currentConstructorDbId = workspace.Default.DbId;

        // Раздел настроек «Управление базой данных»: реестр + статус монтирования.
        SettingsModule.Initialize(workspace.Registry, dbId => _workspace.Get(dbId) is not null);

        // Live mount/unmount: добавление/удаление БД в настройках сразу отражается в
        // сайдбаре конструктора без перезапуска.
        SettingsModule.DatabaseAdded += async (_, entry) => await MountWithProgressAsync(entry);
        SettingsModule.DatabaseRemoved += (_, dbId) =>
        {
            // Если открыта коллекция удаляемой БД — отвязываем модуль на дефолтную БД.
            if (_currentConstructorDbId == dbId)
            {
                _currentConstructorDbId = _workspace.Default.DbId;
                ConstructorModule.DetachDatabase(_workspace.Default.Constructor);
            }
            _workspace.Unmount(dbId);
            SyncMountedToUi();
        };
        SettingsModule.DatabaseReconnectRequested += async (_, dbId) =>
        {
            // Повторная попытка монтирования (например, сетевая папка снова доступна).
            var entry = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
            if (entry is not null) await MountWithProgressAsync(entry);
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
        Deactivated += (_, _) => CloseOpenPopups();

        // Уровень масштабирования: применяем сохранённый и реагируем на изменения из настроек.
        ApplyScale(Scaling.Current);
        Scaling.ScaleChanged += ApplyScale;
        Closed += (_, _) => Scaling.ScaleChanged -= ApplyScale;

        // Автообновление: тихая проверка после открытия окна (уже показанное окно —
        // владелец модалки «Обновить/Отмена»). Проверка папки идёт в фоне, UI не блокирует.
        Opened += async (_, _) =>
        {
            if (AppSettings.LoadAutoUpdate())
                await SettingsModule.CheckForUpdatesOnStartupAsync();
        };
    }

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

    // Смонтировать БД В ФОНЕ: открытие большого файла — секунды и более (миграции, WAL,
    // самолечение раздутого файла, бэкафилл при первом открытии старой БД), UI-поток не
    // блокируем. Окно «Подключение…» появляется, только если монтирование дольше 400 мс —
    // как у массовых операций (быстрое подключение маленькой БД не мигает окном).
    private async Task MountWithProgressAsync(DatabaseRefEntity entry)
    {
        var bg = Task.Run(() => _workspace.Mount(entry));

        if (await Task.WhenAny(bg, Task.Delay(400)) != bg)
        {
            var dlg = new ProgressDialog { Title = "Подключение базы данных" };
            dlg.SetInfo($"База данных: {entry.Name}");
            dlg.SetSaving("Подключение…");
            var closed = dlg.ShowDialog(this);
            await bg;
            dlg.Done();
            await closed;
        }

        // Ошибку монтирования Mount() гасит сам (вернёт null) — статус покажет список БД.
        SyncMountedToUi();
    }

    // Допущенные БД: календарь, цели переноса и списки диалогов видят только их.
    // Недопущенные (замок) показывает лишь сайдбар конструктора.
    private List<DatabaseManager> GrantedDbs() =>
        _workspace.Mounted.Where(m => m.Permissions.AccessGranted).ToList();

    // Пересобрать боковые панели и обновить набор БД календаря после монтирования/
    // размонтирования/получения допуска.
    private void SyncMountedToUi()
    {
        ConstructorSidebar.InitializeWorkspace(_workspace.Mounted, Controls.Components.SidebarMode.Constructor);
        CalendarSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Calendar);
        TasksSidebar.InitializeWorkspace(GrantedDbs(), Controls.Components.SidebarMode.Tasks);
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
