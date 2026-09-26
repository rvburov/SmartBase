using Avalonia.Controls;
using SmartBase.Database;
using SmartBase.Utils;
using SmartBase.Views.ConstructorDb.Calendar;
using SmartBase.Views.Notes;
using SmartBase.Views.Settings;

namespace SmartBase.Views;

// Разделы, которые строит первый показ. Календарь, настройки и заметки — самые тяжелые разметки
// окна (холодная сборка 160–190 и 90 мс), а на старте виден один раздел: собирать их все значило
// держать сплэш лишние доли секунды. Раздел строит показ (ShowModule) или обращение, которому
// без него не обойтись (обновление нашлось — окну «Обновить» нужен раздел настроек). До того
// сообщения для него — состав баз, галочки коллекций, ход копирования — не нужны: построенный
// раздел читает все сам. Статусы баз («занята», «снова доступна») копятся и отдаются при
// построении.
public partial class MainWindow
{
    private CalendarControllerView? _calendarView;
    private SettingsView?          _settingsView;
    private NotesControllerView?   _notesView;
    private readonly Dictionary<int, string> _dbStatuses = [];

    private CalendarControllerView CalendarModule => _calendarView ?? CreateCalendarModule();
    private SettingsView           SettingsModule => _settingsView ?? CreateSettingsModule();
    private NotesControllerView    NotesModule    => _notesView    ?? CreateNotesModule();

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
            scenarioRun: (scenario, ct) => _scenarios.RunNowAsync(scenario, ct));

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

        foreach (var (dbId, text) in _dbStatuses) view.SetDatabaseStatus(dbId, text);
        return view;
    }
}
