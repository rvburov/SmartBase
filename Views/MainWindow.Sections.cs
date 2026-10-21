using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Database;
using SmartBase.Utils;
using SmartBase.Views.ConstructorDb.Calendar;
using SmartBase.Views.Extensions;
using SmartBase.Views.Library;
using SmartBase.Views.Notes;
using SmartBase.Views.Profile;
using SmartBase.Views.Settings;
using SmartBase.Views.Tasks;

namespace SmartBase.Views;

// Разделы, которые строит первый показ. На старте виден один раздел — конструктор; собирать все
// значило держать сплэш и первый кадр окна лишние доли секунды (календарь, настройки и заметки —
// самые тяжелые разметки, 160–190 и 90 мс холодно; задачи, профиль, расширения и библиотека
// вместе — еще 0,1–0,2 с с разметкой и стилями). Раздел строит показ (ShowModule) или обращение,
// которому без него не обойтись (обновление нашлось — окну «Обновить» нужен раздел настроек;
// уведомление о задаче открывает задачу). До того сообщения для него — состав баз, галочки
// коллекций, ход копирования, чужие правки — не нужны: построенный раздел читает все сам.
// Статусы баз («занята», «снова доступна») копятся и отдаются при построении.
public partial class MainWindow
{
    private CalendarControllerView? _calendarView;
    private SettingsView?          _settingsView;
    private NotesControllerView?   _notesView;
    private TasksControllerView?   _tasksView;
    private LibraryControllerView? _libraryView;
    private ProfileView?           _profileView;
    private ExtensionsView?        _extensionsView;
    private readonly Dictionary<int, string> _dbStatuses = [];

    private CalendarControllerView CalendarModule   => _calendarView   ?? CreateCalendarModule();
    private SettingsView           SettingsModule   => _settingsView   ?? CreateSettingsModule();
    private NotesControllerView    NotesModule      => _notesView      ?? CreateNotesModule();
    private TasksControllerView    TasksModule      => _tasksView      ?? CreateTasksModule();
    private LibraryControllerView  LibraryModule    => _libraryView    ?? CreateLibraryModule();
    private ProfileView            ProfileModule    => _profileView    ?? CreateProfileModule();
    private ExtensionsView         ExtensionsModule => _extensionsView ?? CreateExtensionsModule();

    // Календарь и «Задачи» окно строит заранее, в простое после старта: построенный при показе
    // раздел отдает первому кадру свое построение, стили и первую раскладку, и первое открытие
    // стоило 250–400 мс против 200–230 у построенного заранее (стенд Tests/Sections). Скрытым
    // раздел строится за 30–35 мс и ничего не читает — читает показ. Ждем, пока опустеет очередь
    // подключения баз (не дольше минуты), и строим по одному разделу, когда UI-потоку нечего
    // делать. Остальные разделы открываются быстро и так.
    private bool _prebuildStarted;

    private async Task PrebuildSectionsWhenIdleAsync()
    {
        if (_prebuildStarted) return;
        _prebuildStarted = true;
        try
        {
            for (var i = 0; i < 240 && _mountQueue.Pending().Any(p => p.State is MountQueue.ItemState.Queued
                                                                            or MountQueue.ItemState.Mounting); i++)
                await Task.Delay(250);
            await Task.Delay(500);
            // Раздел, его первая раскладка, затем строки его боковой панели. Скрытый элемент шаблонов не
            // применяет и строк списка не строит — это делал первый показ раздела (шаблоны раздела и
            // пустой «Месяц» — 50–100 мс, строки боковой панели — 70–100 мс). Прогрев: элемент на
            // один синхронный проход раскладки становится видимым и прячется обратно — до отрисовки,
            // так что на экране ничего не мелькает. Построенное остается: показ только раскладывает
            // готовое, а строки боковой панели переносит на новые узлы (SidebarTree.RebuildRows).
            // Раздел в этот миг не читает: отложенное чтение он ставит постом, а пост видит его уже
            // скрытым (см. OnPropertyChanged разделов).
            foreach (var step in new Action[]
                     {
                         () => Warm(CalendarModule), () => Warm(CalendarSidebar),
                         () => Warm(TasksModule),    () => Warm(TasksSidebar),
                     })
            {
                await IdleStep(step);
                await Task.Delay(100);
            }
        }
        catch (Exception ex)
        {
            // Не построился заранее — построится при показе, как прежде.
            Serilog.Log.Warning(ex, "Разделы не построены заранее");
        }
    }

    // Шаг — только в простое: UI-поток свободен и не идет замеряемая работа. Пока раздел читает
    // базы, UI-поток ждет их, и шаг в этом окне встал бы в первый показ, которому должен помогать
    // (стенд: 470–540 мс вместо 250). Занято — шаг ждет (до 5 с), а если работа началась к самому
    // запуску — пропускается: раздел построится при показе, как без этого.
    private static async Task IdleStep(Action step)
    {
        for (var i = 0; i < 50 && Logging.Diag.Busy; i++) await Task.Delay(100);
        await Dispatcher.UIThread.InvokeAsync(() => { if (!Logging.Diag.Busy) step(); },
                                              DispatcherPriority.ApplicationIdle);
    }

    private void Warm(Control control)
    {
        if (control.IsVisible) return;   // показан — прогревать нечего
        control.IsVisible = true;
        UpdateLayout();
        control.IsVisible = false;
    }

    // Показ раздела: видимому — построить; скрываемому — только если уже построен.
    private static void ShowSection<T>(T? built, Func<T> build, bool visible) where T : Control
    {
        if (visible) build().IsVisible = true;
        else if (built is not null) built.IsVisible = false;
    }

    // Раздел — в область контента, скрытым (видимость ставит ShowModule).
    private T AddSection<T>(T view, string name) where T : Control
    {
        view.Name      = name;
        view.IsVisible = false;
        ModulesHost.Children.Add(view);
        return view;
    }

    private CalendarControllerView CreateCalendarModule()
    {
        using var slow = Logging.Diag.Measure("построение раздела «Календарь»", Logging.Diag.UiOpen);
        var view = _calendarView = AddSection(new CalendarControllerView(), "CalendarModule");
        // Календарь агрегирует события только из ДОПУЩЕННЫХ БД (без допуска — данные не видны).
        view.Initialize(GrantedDbs(), _categories);
        // После создания/изменения события обновляем Sidebar (могла появиться новая коллекция)
        view.CollectionsMayHaveChanged += (_, _) =>
        {
            CalendarSidebar.Refresh();
            view.SetActiveIds(CalendarSidebar.GetActiveIds());
        };
        view.SetActiveIds(CalendarSidebar.GetActiveIds());
        return view;
    }

    private TasksControllerView CreateTasksModule()
    {
        using var slow = Logging.Diag.Measure("построение раздела «Задачи»", Logging.Diag.UiOpen);
        var view = _tasksView = AddSection(new TasksControllerView(), "TasksModule");
        view.Initialize(GrantedDbs());
        // Создание/правка задачи могли создать коллекцию или авто-поля («Назначил»/«Статус»).
        view.CollectionsMayHaveChanged += (_, _) =>
        {
            TasksSidebar.Refresh();
            // Задача могла подключить коллекцию, которую прятала настройка «Только коллекции с
            // исполнителем»: теперь она в дереве, и раздел должен читать ее вместе с остальными.
            // Набор тот же — вызов ничего не делает; иначе его перекроет тихое перечитывание,
            // которое раздел запускает следом.
            view.SetActiveIds(TasksSidebar.GetActiveIds());
            CalendarSidebar.Refresh();
            _calendarView?.SetActiveIds(CalendarSidebar.GetActiveIds());
            ConstructorSidebar.Refresh();
            ConstructorModule.Refresh();
        };
        return view;
    }

    private LibraryControllerView CreateLibraryModule()
    {
        using var slow = Logging.Diag.Measure("построение раздела «Библиотека документов»", Logging.Diag.UiOpen);
        var view = _libraryView = AddSection(new LibraryControllerView(), "LibraryModule");
        view.Initialize(GrantedDbs());

        // ── Панель чтения документа ──
        // Открывается «Прочитать» в карточке и двойным кликом по ней; Escape и toggle навбара
        // ее закрывают. Стрелка в шапке скрывает список. Toggle возвращает последний читанный
        // документ — открывать пустую панель нечем и незачем.
        view.ReadRequested      += (_, doc) => OpenLibraryReader(doc);
        view.SearchQueryChanged += (_, q)   => LibraryReader.SetQuery(q);
        view.ReaderToggled      += (_, open) =>
        {
            // Сюда приходит и живое нажатие, и эхо от SetReaderOpen, которым мы сами догоняли
            // кнопку после открытия панели с карточки. Различает их только состояние панели —
            // почему именно оно, а не флаг подавления, написано в DocLoadPlan.
            switch (Library.DocLoadPlan.ReaderToggle(open, _readerOpen, _lastRead is not null))
            {
                case Library.DocReaderAction.Open when _lastRead is { } doc: OpenLibraryReader(doc); break;
                case Library.DocReaderAction.Close:                          CloseLibraryReader();  break;
                case Library.DocReaderAction.Rollback:                       view.SetReaderOpen(false); break;
            }
        };
        return view;
    }

    private ProfileView CreateProfileModule()
    {
        using var slow = Logging.Diag.Measure("построение раздела «Профиль»", Logging.Diag.UiOpen);
        var view = _profileView = AddSection(new ProfileView(), "ProfileModule");
        // Модуль сам просит открыть свой раздел — переход «В журнал» из контура безопасности.
        // Выделяет пункт сайдбар (и он же зовет ShowCategory): переключи раздел молча, и сайдбар
        // показывал бы один раздел, а экран — другой.
        view.CategoryRequested += (_, key) => ProfileSidebar.Select(key);
        // Менеджер паролей переименовал базу тем же окном настроек базы, что и «Настройки», —
        // и обрабатывается это тем же кодом.
        view.DatabaseRenamed += OnDatabaseRenamed;
        // Панели администратора и истории работают со смонтированными БД рабочего пространства.
        view.Initialize(_workspace);
        return view;
    }

    private ExtensionsView CreateExtensionsModule()
    {
        using var slow = Logging.Diag.Measure("построение раздела «Расширения»", Logging.Diag.UiOpen);
        return _extensionsView = AddSection(new ExtensionsView(), "ExtensionsModule");
    }

    private NotesControllerView CreateNotesModule()
    {
        using var slow = Logging.Diag.Measure("построение раздела «Заметки»", Logging.Diag.UiOpen);
        var view = _notesView = AddSection(new NotesControllerView(), "NotesModule");
        // Раздел самостоятельный: из общего — только путь к папке-хранилищу в app.db.
        view.SetVault(AppSettings.LoadNotesVault());
        // Переименование из шапки раздела — та же операция, что и из дерева: файл трогает
        // только сайдбар, а раздел узнает о новом пути событием NoteMoved.
        view.NoteRenamed += (_, r) =>
        {
            // Имя не подошло (занято, запрещенные символы, отказ ФС) — заголовок откатывается
            // и объясняет причину прямо у себя, а не всплывашкой над деревом.
            if (NotesSidebar.RenameFromHeader(r.RelativePath, r.NewName) is { } error)
                view.RejectRename(error);
        };

        // Кнопка «Открыть настройки» из пустого состояния. Переключаем ровно тем же путем, что и
        // панель активности, иначе подсветка кнопки разъедется с показанным разделом.
        view.OpenSettingsRequested += (_, _) =>
        {
            ActivityBarControl.SetModuleActive("settings");
            ShowModule("settings");
            SettingsSidebar.Select("notes");
        };

        return view;
    }

    private SettingsView CreateSettingsModule()
    {
        using var slow = Logging.Diag.Measure("построение раздела «Настройки»", Logging.Diag.UiOpen);
        var view = _settingsView = AddSection(new SettingsView(), "SettingsModule");
        // Раздел настроек «Управление базой данных»: реестр + статус монтирования/очереди.
        view.Initialize(_workspace.Registry,
            dbId => _workspace.Get(dbId) is not null,
            dbId => _mountQueue.StatusOf(dbId) is { } st
                ? (st.Text, st.State == MountQueue.ItemState.Failed)
                : null,
            // Сведения о файле — снимок, снятый при монтировании (без обращения к файлу с UI-потока).
            dbId => _workspace.Get(dbId)?.Storage,
            // Администратор базы: он обслуживает файл (сжать/копия) и правит в окне настроек
            // название с паролем подключения. Провайдер здесь не при чем — где нужен именно
            // SQLite, это проверяется на месте.
            dbId => _workspace.Get(dbId) is { } m && m.Permissions.IsAdmin,
            // Стадии задач всех допущенных баз — для списка «Стадия новой задачи».
            taskStages: () => GrantedDbs().SelectMany(m => m.Constructor.GetTaskStages()).Distinct().ToList(),
            // Открытые базы — «Интеграциям»: ответ сторонней программы укладывается в коллекцию
            // выбранной базы, и список должен быть тем, что есть на экране прямо сейчас.
            databases: GrantedDbs,
            // Кнопка «Проверить папку» ведет ту же службу, что ходит по расписанию: две
            // реализации одного обхода разошлись бы на первой же правке.
            inboxSweep: ct => _inbox.RunNowAsync(ct),
            // Служба приема по сети: экран включает ее и показывает, слушает она или нет.
            ingress: _ingress,
            // «Запустить» у сценария ведет ту же службу, что ходит по расписанию: проверка,
            // идущая другой дорогой, не отвечает на вопрос «а что будет само».
            scenarioRun: (scenario, ct) => _scenarios.RunNowAsync(scenario, ct),
            // Служба потоков событий: экран показывает, слушается ли поток, и зовет сверку
            // после правки карточек.
            streams: _streams);

        // Live mount/unmount: добавление/удаление БД в настройках сразу отражается в
        // сайдбаре конструктора без перезапуска. Новая база встает в хвост очереди —
        // даже если сейчас мигрирует другая, ничего не блокируется.
        view.DatabaseAdded += (_, entry) =>
        {
            _mountQueue.Enqueue(entry);
            SyncMountedToUi();   // заглушка «в очереди…» появляется сразу
        };
        view.DatabaseRemoved += (_, dbId) =>
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
        view.DatabaseReconnectRequested += (_, dbId) =>
        {
            // Повторная попытка монтирования (например, сетевая папка снова доступна).
            var entry = _workspace.Registry.GetAll().FirstOrDefault(d => d.Id == dbId);
            if (entry is null) return;
            _mountQueue.Enqueue(entry);
            SyncMountedToUi();
        };
        // Обслуживание файла базы из «Управления базой данных»: сжатие через очередь,
        // с отключением базы на время VACUUM.
        view.DatabaseCompactRequested += (_, dbId) => _ = CompactDatabaseAsync(dbId);
        // Раздел «Резервное копирование»: ручной запуск — службе; ход и итог — обратно в
        // раздел (прогресс приходит из фонового потока — маршалим, остальное уже на UI).
        view.BackupNowRequested += (_, dbId) => _ = _backup.RunNowAsync(dbId);
        // Переименование в окне настроек базы (см. OnDatabaseRenamed).
        view.DatabaseRenamed += OnDatabaseRenamed;

        // Тумблер «Общее → Уведомления»: включаем/выключаем службу на лету.
        view.NotificationsEnabledChanged += (_, on) => _notifications.SetEnabled(on);
        // Тумблеры уведомлений по задачам — тем же путем, каждый сам по себе.
        view.TaskAssignedNotificationsChanged += (_, on) => _notifications.SetTaskAssignedEnabled(on);
        view.TaskStatusNotificationsChanged   += (_, on) => _notifications.SetTaskStatusEnabled(on);
        view.BackupNotificationsChanged       += (_, on) => _notifications.SetBackupEnabled(on);
        // Тумблер «Общее → Фоновый режим»: показать/скрыть значок в трее (и вернуть окно при выкл).
        view.BackgroundEnabledChanged += (_, on) => OnBackgroundChanged(on);

        // «Показать политику» с карточки соединения: отказ адреса объясняет себя строкой, а
        // правила целиком живут в контуре безопасности — в другом модуле. Переключаем тем же
        // путем, что и панель активности, иначе подсветка кнопки разъедется с показанным
        // разделом.
        view.PolicyRequested += (_, _) =>
        {
            ActivityBarControl.SetModuleActive("profile");
            ShowModule("profile");
            ProfileSidebar.Select("security");
        };

        // Папку хранилища сменили в настройках — раздать новый путь дереву и разделу.
        view.NotesVaultChanged += (_, path) =>
        {
            NotesSidebar.SetVault(path);
            _notesView?.SetVault(path);
        };

        // «Отображаемые виды» и «Отображение раздела навигации»: набор скрытых применяется сразу.
        // Панель разделов видна и из настроек; переключатели видов пересобираются, пока их не
        // видно, и раздел, у которого скрыли открытый вид, уходит на вид по умолчанию. Еще не
        // построенный раздел прочитает набор сам, при построении. У библиотеки переключателя
        // нет — вид у нее один.
        view.ViewVisibilityChanged += (_, scope) =>
        {
            switch (scope)
            {
                case SmartBase.Settings.ViewCatalog.ScopeNavigation:  ActivityBarControl.ApplyModuleVisibility(); break;
                case SmartBase.Settings.ViewCatalog.ScopeCalendar:    _calendarView?.ApplyViewVisibility();       break;
                case SmartBase.Settings.ViewCatalog.ScopeTasks:       _tasksView?.ApplyViewVisibility();          break;
                case SmartBase.Settings.ViewCatalog.ScopeConstructor: ConstructorModule.ApplyViewVisibility();    break;
            }
        };

        foreach (var (dbId, text) in _dbStatuses) view.SetDatabaseStatus(dbId, text);
        return view;
    }
}
