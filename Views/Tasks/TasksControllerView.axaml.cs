using Avalonia;
using Avalonia.Controls;
using Serilog;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.Tasks;

// Оркестратор раздела «Задачи» (упрощенный CalendarControllerView): агрегат задач по
// ОТМЕЧЕННЫМ коллекциям всех допущенных БД, сгруппированный по коллекциям (заголовок
// коллекции + ее задачи); поиск и сортировка — в памяти по загруженному (читается не
// более TaskLoadCap на БД).
//
// «Открытой коллекции» здесь нет: клик по строке сайдбара — та же галочка, что и в
// календаре. Раньше клик сужал список до одной коллекции, и галочки остальных ни на что
// не влияли — увидеть задачи двух коллекций разом было нельзя.
public partial class TasksControllerView : UserControl
{
    // Создание задачи могло создать коллекцию/авто-поля — сайдбары и таблицы обновляются снаружи.
    public event EventHandler? CollectionsMayHaveChanged;

    /// <summary>
    /// Сколько задач раздел читает с одной БД. Тот же порог, что у «Расписания» и «Журнала»
    /// календаря (<c>MemCap</c>): до него набор держится в памяти целиком — а значит порядок по
    /// сроку, поиск и все шесть счетчиков фильтра остаются точными и считаются одним проходом.
    ///
    /// Прежняя тысяча была здесь не порогом памяти, а произволом: задачей стала КАЖДАЯ запись
    /// коллекции с полем-исполнителем, и крышка уперлась в размер коллекции, а не в число дел.
    /// Раздел молча показывал первую тысячу по позиции, а счетчик в навбаре повторял ту же
    /// тысячу и выглядел полным ответом. Теперь недочитанный хвост виден: сколько задач в
    /// коллекции на самом деле, спрашивается отдельным COUNT'ом
    /// (<see cref="ConstructorRepository.CountTasksByCollection"/>), и разница показывается
    /// строкой «+N еще» в конце группы — как календарь говорит о недоставленных событиях дня.
    /// </summary>
    private const int TaskLoadCap = 10_000;

    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _dbs = [];
    private Dictionary<int, HashSet<int>>? _activeByDb;
    private string _searchQuery = "";
    private TaskFilter _filter = TaskFilter.All;
    // Стадии группы фильтра — объединение пунктов поля «Статус» отмеченных коллекций всех баз;
    // снимаются в UpdateChrome вместе с коллекциями.
    private List<string> _stages = [];

    // Правила показа из «Настройки → Задачи» (живой сервис TaskSettings).
    private static TaskRules Rules => new(TaskSettings.OverdueNextDay, TaskSettings.ShowDone);

    // Фильтр, который нужно поставить при входе (настройка «Фильтр при открытии раздела»).
    // Стадия ставится позже — когда стадии известны после первой загрузки (SetStages).
    private TaskFilter? _startFilter;

    // Полный список пунктов навбара: стандартные плюс стадии.
    private List<TaskFilter> Filters => TaskFilters.WithStages(_stages);

    // Вид репозитория для пункта фильтра: правила показа уходят в SQL теми же флагами, какими
    // их читает проход по памяти (TaskFilters.Matches).
    private static ConstructorRepository.TaskView ViewFor(TaskFilter f, string search,
                                                          IReadOnlyCollection<string> me, DateTime now) =>
        new(f.Kind.ToString(), search, me, now, f.StageName,
            HideDone: !Rules.ShowDone, OverdueNextDay: Rules.OverdueNextDay);
    private List<TaskRecord> _loaded = [];
    // Коллекции для заголовков групп: (DbId, ColId) → место в сайдбаре, цвет и название.
    // Снимается при загрузке — ApplyToList зовется и на каждый символ поиска.
    private Dictionary<(int DbId, int ColId), (int Order, string Color, string Name)> _collections = [];
    // Размеры и форма коллекций ПО БАЗЕ: сколько задач, сколько выполнено, сколько со сроком и
    // можно ли читать коллекцию страницами. Снимается тем же заходом, что и сам список.
    // Заголовок группы называет эти числа, а не пересчитывает показанное (но только при пустом
    // поиске и фильтре «Все задачи»), и по ним же собирается виртуальный список.
    private Dictionary<(int DbId, int ColId), ConstructorRepository.TaskCollectionStats> _stats = [];
    private long _gen;   // поколение загрузки: устаревшие результаты не применяются

    private ConstructorRepository? RepoFor(int dbId)
    {
        foreach (var d in _dbs) if (d.DbId == dbId) return d.Repo;
        return null;
    }

    public TasksControllerView() { InitializeComponent(); }

    // Размер шрифта меняется живьем: карточки строятся в C# с размерами из ресурсов.
    private int _renderedFontDelta = FontScale.Delta;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FontScale.Changed    += OnFontScaleChanged;
        TaskSettings.Changed += OnTaskSettingsChanged;
        if (_dbs.Count > 0 && _renderedFontDelta != FontScale.Delta) ApplyToList();
        if (_dbs.Count > 0 && _renderedTaskSettingsStamp != TaskSettings.Stamp) OnTaskSettingsChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FontScale.Changed    -= OnFontScaleChanged;
        TaskSettings.Changed -= OnTaskSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // Настройки раздела меняются живьем: правила показа (просрочка, выполненные) требуют
    // перечитать список и счетчики, умолчания строки добавления — переставить ее поля.
    private long _renderedTaskSettingsStamp = TaskSettings.Stamp;

    private void OnTaskSettingsChanged()
    {
        _renderedTaskSettingsStamp = TaskSettings.Stamp;
        if (_dbs.Count == 0) return;
        TaskList.ApplyQuickDefaults();
        UpdateQuickUserOptions(TaskList.SelectedCollection?.DbId ?? ResolveQuickAddTarget().DbId);
        RefreshCurrent(quiet: true);
    }

    private void OnFontScaleChanged()
    {
        if (_dbs.Count > 0) ApplyToList();
    }

    public void Initialize(IReadOnlyList<DatabaseManager> databases)
    {
        SetDatabaseList(databases);

        NavbarControl.SearchChanged += (_, q) => { _searchQuery = q; ApplyToList(); };
        NavbarControl.FilterChanged += (_, f) =>
        {
            _filter = f;
            TaskSettings.RememberFilter(f.Key);   // для режима «последний выбранный»
            ApplyToList();
        };

        // Стартовый фильтр из настроек: стандартный ставится сразу, стадия — когда стадии
        // известны (после первой загрузки, см. UpdateChrome).
        _startFilter = TaskFilter.ParseKey(TaskSettings.InitialFilterKey);
        if (_startFilter is { IsStage: false } start)
        {
            if (NavbarControl.SelectFilter(start)) _filter = start;
            _startFilter = null;
        }

        // Обработчики событий синхронные, поэтому Task не ждем — но и не теряем (см. Fire):
        // раньше `_ = QuickAdd(req)` превращал любую не пойманную внутри ошибку в unobserved
        // task exception, который финализатор добивал уже без всякого контекста.
        TaskList.TaskActivated     += (_, t)   => Fire(OpenEditDialog(t));
        TaskList.QuickAddRequested += (_, req) => Fire(QuickAdd(req));
        TaskList.TasksDeleteRequested    += (_, ts) => Fire(DeleteTasksWithConfirm(ts));
        TaskList.TasksDuplicateRequested += (_, ts) => Fire(DuplicateTasksWithConfirm(ts));
        // Коллекция может жить в другой базе — «Исполнитель» показывает ее пользователей.
        TaskList.QuickTargetChanged += (_, target) => UpdateQuickUserOptions(target.DbId);

        RefreshCurrent();
    }

    /// <summary>Момент последнего чтения данных (<see cref="ChangeBus.Now"/>): главное окно
    /// сверяет его с моментом последнего коммита в базы — своего или чужого — и решает,
    /// перечитывать ли раздел при возврате в него и на лету.</summary>
    public long DataStamp { get; private set; }

    /// <param name="force">
    /// Перечитать, даже если набор галочек тот же: с прошлого чтения в базах что-то поменялось
    /// (правка в конструкторе или календаре, запись расширения, чужая правка). Без этого раздел
    /// показывал старые карточки, пока человек не переключит галочку. Перечитывается тихо: список
    /// уже на экране, и волна «Загрузка…» на нем выглядела бы как открытие заново.
    /// </param>
    public void SetActiveIds(Dictionary<int, HashSet<int>> activeByDb, bool force = false)
    {
        // Тот же набор галочек — перечитывать нечего. Сайдбар присылает его и когда ничего не
        // менял (возврат в раздел, перемонтирование базы), а на большой базе каждый лишний
        // заход — это лишний счет по всем коллекциям и волна «Загрузка…» на ровном месте.
        if (!force && TaskLoadPlan.SameActiveIds(_activeByDb, activeByDb)) return;

        var quiet = force && TaskLoadPlan.SameActiveIds(_activeByDb, activeByDb);
        _activeByDb = activeByDb;
        RefreshCurrent(quiet);
    }

    // Обновить набор смонтированных БД (live add/remove) без переподписки обработчиков.
    public void SetDatabases(IReadOnlyList<DatabaseManager> databases)
    {
        SetDatabaseList(databases);
        RefreshCurrent();
    }

    public void Refresh() => RefreshCurrent();

    /// <summary>
    /// Открыть карточку задачи по клику на уведомлении. Задачу перечитываем из базы, а не
    /// берем из показанного списка: раздел мог быть еще не загружен, задача — не попасть в
    /// текущий фильтр или крышку чтения, а название и стадия за время показа баннера уже
    /// поменяться. Дальше — обычный путь правки (OpenEditDialog), со всеми подтверждениями.
    /// </summary>
    public async Task OpenTaskAsync(int dbId, int recordId, int collectionId, int execFieldId)
    {
        if (RepoFor(dbId) is not { } repo) return;

        TaskRecord? task = null;
        try
        {
            task = repo.GetTaskRecordsByIds(collectionId, [recordId])
                       .FirstOrDefault(t => execFieldId == 0 || t.ExecFieldId == execFieldId);
        }
        catch { /* база могла отвалиться — ниже покажем то же сообщение, что и об удалении */ }

        if (task is null)
        {
            TaskList.ShowError("Задача не найдена: возможно, ее удалили или перенесли.");
            return;
        }

        task.DbId = dbId;
        await OpenEditDialog(task);
    }

    private void SetDatabaseList(IReadOnlyList<DatabaseManager> databases)
    {
        _dbs.Clear();
        foreach (var d in databases) _dbs.Add((d.DbId, d.Name, d.Constructor));
    }

    /// <summary>«Отображаемые виды» задач сменились — переключатель пересобирается сразу.</summary>
    public void ApplyViewVisibility() => NavbarControl.ApplyViewVisibility();

    // ── Загрузка ─────────────────────────────────────────────────────────────

    /// <param name="quiet">
    /// Тихое обновление: список остается на экране и возвращается на прежнее место, оверлей
    /// «Загрузка…» не показывается. Так перечитываются данные ПОСЛЕ действия над задачей —
    /// человек только что нажал «Сохранить» и смотрит на карточку, а не начинает все заново;
    /// волна «Загрузка…» на ее месте выглядит так, будто раздел открылся заново. То же правило
    /// в «Расписании» календаря: оно зовет это «пересборкой без свежести».
    /// </param>
    private void RefreshCurrent(bool quiet = false)
    {
        // Скрытый раздел не читает: на старте (раздел по умолчанию — конструктор) каждая
        // подключенная база запускала полное чтение раздела по всем базам, и эти чтения шли
        // параллельно секундами. Отметка остается, раздел перечитается при показе; громкое
        // обновление среди отложенных побеждает тихое.
        if (!IsVisible)
        {
            _loadDeferred   = true;
            _deferredQuiet &= quiet;
            return;
        }
        if (_loadDeferred) quiet &= _deferredQuiet;
        _loadDeferred  = false;
        _deferredQuiet = true;
        _ = LoadAsync(quiet);
    }

    private bool _loadDeferred;
    private bool _deferredQuiet = true;

    // Показ раздела: отложенное чтение — сейчас. Постом: главное окно следом за IsVisible
    // отдает свежие галочки (SetActiveIds), и тогда чтение пойдет одно и уже с ними.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _loadDeferred)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_loadDeferred && IsVisible) RefreshCurrent(); },
                                                        Avalonia.Threading.DispatcherPriority.Background);
    }

    private async Task LoadAsync(bool quiet = false)
    {
        DataStamp = ChangeBus.Now();
        if (_dbs.Count == 0) { _loaded = []; ApplyToList(); return; }

        var gen       = ++_gen;
        var oldOffset = TaskList.ScrollOffset;
        // Порог медленности: чтение раздела по всем допущенным базам дольше Diag.UiOpen — в журнал.
        using var slow = Logging.Diag.Measure("загрузка раздела «Задачи»", Logging.Diag.UiOpen, $"баз {_dbs.Count}");
        var work      = LoadAllAsync(gen);

        // Оверлей — только если загрузка дольше 400 мс (как сетка календаря) И показывать
        // нечего: при правке задачи на экране уже есть верный список.
        if (!quiet && await Task.WhenAny(work, Task.Delay(400)) != work && gen == _gen)
            LoadingOverlay.IsVisible = true;

        List<TaskRecord> list;
        Dictionary<(int DbId, int ColId), ConstructorRepository.TaskCollectionStats> stats;
        bool tasksRead;
        try { (list, stats, tasksRead) = await work; }
        catch (OperationCanceledException) when (gen != _gen)
        {
            // Загрузку обогнала новая (подключилась еще база, сменились галочки): бросаем ее
            // на границе базы, замер снимаем без записи.
            slow.Discard();
            return;
        }
        catch (Exception ex)
        {
            // Фон: недоступная БД не валит UI. Но пустой раздел без записи в журнале
            // неотличим от «задач нет» — одна запись в минуту на причину.
            if (Logging.Diag.Throttle("tasks-load-fail", TimeSpan.FromMinutes(1)))
                Log.Warning(ex, "Раздел «Задачи»: список не прочитан (баз {Count}) — показан пустым", _dbs.Count);
            (list, stats, tasksRead) = ([], [], true);
        }

        if (gen != _gen) return;
        LoadingOverlay.IsVisible = false;
        _loaded      = list;
        _stats       = stats;
        _loadedStale = !tasksRead;
        TaskCardVm.Rules = Rules;   // чип срока на карточке считает просрочку тем же правилом, что фильтр
        UpdateChrome();
        ApplyToList();

        // Список подменен целиком, а значит прокрутка уехала в начало. При тихом обновлении
        // возвращаем ее: правка задачи в середине длинного списка не должна выбрасывать наверх.
        if (quiet) TaskList.RestoreScroll(oldOffset);
    }

    // Размеры коллекций читаются ВСЕГДА (счет по индексам — дешево), а сплошной прочит карточек
    // делается, только пока раздел помещается в память. Дальше он не нужен вовсе: страницами
    // читается ЛЮБОЙ вид — и поиск, и каждый из пяти фильтров, — а прочитанные «на всякий
    // случай» десять тысяч строк с коррелированными подзапросами были самым дорогим, что
    // раздел делал при открытии И при каждой правке задачи.
    private async Task<(List<TaskRecord> Tasks,
                        Dictionary<(int DbId, int ColId), ConstructorRepository.TaskCollectionStats> Stats,
                        bool TasksRead)>
        LoadAllAsync(long gen)
    {
        // Размеры всех баз — параллельно: у каждой свое подключение, и ждать приходится самую
        // медленную базу, а не сумму всех (на четырех базах это половина времени открытия).
        var stats = new Dictionary<(int DbId, int ColId), ConstructorRepository.TaskCollectionStats>();
        var perDb = await Task.WhenAll(_dbs.ToList().Select(async d =>
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(d.DbId, out var s) ? s : null;
            return (d.DbId, Stats: await d.Repo.GetTaskCollectionStatsAsync(active));
        }));
        // Устаревшая загрузка дальше не идет: ее результат все равно не покажут.
        if (gen != _gen) throw new OperationCanceledException();
        foreach (var (dbId, part) in perDb)
            foreach (var (colId, one) in part)
                stats[(dbId, colId)] = one;

        var needTasks = TaskLoadPlan.NeedsTaskRead(stats.Values.Sum(s => s.Total), TaskLoadCap);
        if (!needTasks) return ([], stats, false);

        var result = new List<TaskRecord>();
        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            var part = await repo.LoadTasksAsync(active, TaskLoadCap);
            if (gen != _gen) throw new OperationCanceledException();
            foreach (var t in part) t.DbId = dbId;
            result.AddRange(part);
        }
        return (result, stats, true);
    }

    // Карточки не читались: виду они были не нужны. Понадобятся — перезагрузимся (см. ApplyToList).
    private bool _loadedStale;

    // Заголовок навбара, коллекции (группы списка + строка быстрого добавления) и опции
    // исполнителя.
    private void UpdateChrome()
    {
        NavbarControl.SetTitle("Задачи");

        // Коллекции — тем же порядком, что в сайдбаре (GetAllCollections отдает их по
        // Position), чтобы группы списка шли как строки дерева; цвет идет на индикатор.
        var collections = new Dictionary<(int DbId, int ColId), (int Order, string Color, string Name)>();
        var options     = new List<QuickCollectionOption>();
        foreach (var (dbId, dbName, repo) in _dbs)
            foreach (var c in repo.GetAllCollections())
                if (c.Id is int cid)
                {
                    collections[(dbId, cid)] = (collections.Count, c.Color ?? EventColors.Default, c.Name);
                    // В выборе цели — ВСЕ коллекции допущенных баз, а не только отмеченные:
                    // галочки решают, что показано в списке, а создавать можно и в скрытой.
                    // Имя базы в подписи — только при нескольких (иначе шум), как в группах.
                    var label = TitleText.FirstLine(c.Name);
                    options.Add(new QuickCollectionOption(dbId, cid,
                        _dbs.Count > 1 ? $"{label} · {dbName}" : label));
                }
        _collections = collections;

        // Стадии для группы фильтра: пункты поля «Статус» отмеченных коллекций всех баз, без
        // повторов и в порядке полей. Хвост списка навбара пересобирается только при смене набора.
        var stages = new List<string>();
        foreach (var (dbId, _, repo) in _dbs)
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            foreach (var stage in repo.GetTaskStages(active))
                if (!stages.Contains(stage)) stages.Add(stage);
        }
        _stages = stages;
        NavbarControl.SetStages(stages);

        // Стартовая стадия из настроек — одна попытка, когда стадии впервые известны: нет такой
        // стадии в базах — остаемся на «Всех задачах», а не ждем ее появления.
        if (_startFilter is { } pending)
        {
            _startFilter = null;
            if (NavbarControl.SelectFilter(pending)) _filter = pending;
        }

        // Цель по умолчанию — та же, что была до появления выбора: первая отмеченная.
        var (targetDbId, targetColId) = ResolveQuickAddTarget();
        var fallback = new QuickCollectionOption(targetDbId, targetColId, "");
        TaskList.SetCollectionOptions(options, fallback);

        UpdateQuickUserOptions(TaskList.SelectedCollection?.DbId ?? targetDbId);
    }

    // «Исполнитель» строки быстрого добавления — пользователи базы, в которой заведется задача;
    // кто выбран по умолчанию, решает настройка (я / не назначен).
    private void UpdateQuickUserOptions(int dbId) =>
        TaskList.SetUserOptions(RepoFor(dbId)?.GetUserOptions() ?? [],
                                TaskSettings.QuickExecutorMe ? CurrentUser.DisplayName : "");

    // Стадия новой задачи: из настроек, если она есть среди пунктов коллекции; иначе первая из
    // них (у коллекции без поля-статуса — стандартные три, их и заведет EnsureTaskCollection).
    private static string DefaultStageFor(ConstructorRepository repo, int? collectionId)
    {
        var wanted  = TaskSettings.DefaultStage;
        var options = collectionId is int cid
            ? repo.GetTaskStageOptions(cid)
            : [.. ConstructorRepository.TaskStatusOptions];
        return options.Contains(wanted) ? wanted : options[0];
    }

    // Поиск и сортировка — в памяти. Задачи разложены по коллекциям (порядок групп — как в
    // сайдбаре), внутри группы: срок ↑, «без даты» в конец, тай-брейк по Id.
    private void ApplyToList()
    {
        IEnumerable<TaskRecord> items = _loaded;
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            var q = _searchQuery.Trim();
            items = items.Where(t => t.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        }

        // Счетчики фильтра — по набору ПОСЛЕ поиска, но ДО самого фильтра: цифра рядом с
        // пунктом равна тому, что покажет его выбор, и остальные пункты видны как есть.
        var found = items.ToList();
        var now   = DateTime.Now;
        var me    = CurrentUser.DisplayName;
        RefreshFilterCounts(found, now, me);

        var shown = found.Where(t => TaskFilters.Matches(t, _filter, now, me, Rules));

        // Раздел не помещается в память — показываем его страницами. Это касается ЛЮБОГО вида:
        // и поиск, и каждый из пяти фильтров уходят в SQL тем же условием, каким описаны в
        // TaskFilters, поэтому недостижимых задач в разделе не остается.
        if (SectionTotal() > TaskLoadCap) { ShowVirtual(now); return; }

        // Базу в подзаголовке показываем, только когда их несколько (иначе шум).
        var groups = TaskGrouping.Build(shown, _collections, _dbs.Count > 1 ? NameOfDb : null);

        // Пусто по разным причинам — подсказка разная: снятые галочки исправляются в сайдбаре.
        TaskList.SetGroups(groups, NoTasksHint());
    }

    // Поколение счета: устаревший фоновый результат не должен лечь поверх свежего.
    private long _countGen;

    // Счетчики пунктов фильтра. Пока раздел помещается в память — считаются проходом по ней:
    // точно и мгновенно. Дальше карточек в памяти нет вовсе, и счет уходит в БД — по одному
    // запросу на правило, каждый ПОД КРЫШКОЙ ПОКАЗА: больше 999 пилюле не нужно, она печатает
    // «999+», зато цена такого счета не зависит от размера базы. Крышка живет только здесь:
    // размеры групп, по которым строится полоса прокрутки, считаются точно.
    private void RefreshFilterCounts(List<TaskRecord> found, DateTime now, string me)
    {
        if (SectionTotal() <= TaskLoadCap)
        {
            NavbarControl.SetFilterCounts(TaskFilters.Counts(found, Filters, now, me, Rules), _filter);
            return;
        }
        _ = RefreshFilterCountsFromDbAsync(++_countGen, now, me);
    }

    private async Task RefreshFilterCountsFromDbAsync(long gen, DateTime now, string me)
    {
        var filters = Filters;
        var totals  = filters.ToDictionary(f => f, _ => 0);
        // Считать точнее потолка пилюли незачем: на единицу больше — уже «999+».
        var cap     = Controls.Components.TaskFilterItem.CountCap + 1;
        var search  = (_searchQuery ?? "").Trim();
        // Пилюли досчитываются фоном рядом с уже показанным списком — порог фоновый.
        using var slow = Logging.Diag.Measure("счетчики фильтров «Задач»", Logging.Diag.Background, $"баз {_dbs.Count}");

        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            var byCol = StatsOfDb(dbId);
            if (byCol.Count == 0) continue;

            var meVariants = MeVariants(repo, me);
            var views      = filters.Select(f => ViewFor(f, search, meVariants, now)).ToList();
            Dictionary<string, int> part;
            try { part = await repo.CountTaskViewsAsync(byCol, views, cap); }
            catch { continue; }   // фон: недоступная БД не валит счетчики остальных

            if (gen != _countGen) { slow.Discard(); return; }
            foreach (var f in filters)
                totals[f] += part.GetValueOrDefault(f.Key);
        }

        if (gen != _countGen) return;
        NavbarControl.SetFilterCounts(totals, _filter);
    }

    // Размеры коллекций одной базы — репозиторий знает только свои id.
    private Dictionary<int, ConstructorRepository.TaskCollectionStats> StatsOfDb(int dbId)
    {
        var result = new Dictionary<int, ConstructorRepository.TaskCollectionStats>();
        foreach (var (key, s) in _stats)
            if (key.DbId == dbId && s.Total > 0) result[key.ColId] = s;
        return result;
    }

    // Чем текущий пользователь может быть записан в поле-исполнителе. В памяти сравнение идет
    // без учета регистра, а SQLite так кириллицу не сравнивает — поэтому берем имена
    // пользователей базы, совпавшие с текущим без учета регистра, и сверяемся с ними точно.
    private static List<string> MeVariants(ConstructorRepository repo, string me)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { me };
        try
        {
            foreach (var name in repo.GetUserOptions())
                if (name.Equals(me, StringComparison.CurrentCultureIgnoreCase)) set.Add(name);
        }
        catch { /* нет прав на список пользователей — сверяемся с одним точным именем */ }
        return [.. set];
    }

    /// <summary>Сколько задач в разделе на самом деле — сумма по отмеченным коллекциям всех БД.</summary>
    private int SectionTotal()
    {
        var sum = 0;
        foreach (var s in _stats.Values) sum += s.Total;
        return sum;
    }

    // ── Виртуальный показ (раздел не помещается в память) ────────────────────

    // Поколение показа: устаревшая сборка не должна лечь поверх свежей (человек успел
    // переключить фильтр, пока считались размеры групп).
    private long _viewGen;

    private void ShowVirtual(DateTime now) => _ = ShowVirtualAsync(++_viewGen, now);

    // Группы собираются не из задач, а из РАЗМЕРОВ групп в текущем виде: карточки виртуальный
    // список дочитает сам. Для вида «Все задачи» размеры уже известны из общего счета коллекций,
    // и запросов не будет вовсе; поиск и фильтры требуют по запросу на коллекцию — они идут
    // фоном, а прежний список все это время остается на экране.
    private async Task ShowVirtualAsync(long gen, DateTime now)
    {
        var search  = (_searchQuery ?? "").Trim();
        var me      = CurrentUser.DisplayName;
        var entries = new List<(int Order, TaskVirtualList.Entry Entry)>();
        // Порог медленности: размеры групп вида (фильтр, поиск) — человек ждет список.
        using var slow = Logging.Diag.Measure("вид раздела «Задачи»", Logging.Diag.UiQuery,
                                              $"{_filter.Key}{(search.Length > 0 ? ", с поиском" : "")}, баз {_dbs.Count}");

        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            var byCol = StatsOfDb(dbId);
            if (byCol.Count == 0) continue;

            // Вид у каждой базы свой: варианты написания текущего пользователя берутся из ее
            // списка пользователей.
            var view = ViewFor(_filter, search, MeVariants(repo, me), now);

            Dictionary<int, ConstructorRepository.TaskViewSize> sizes;
            try { sizes = await repo.CountTaskViewAsync(byCol, view); }
            catch { continue; }   // фон: недоступная БД не валит остальные

            if (gen != _viewGen) { slow.Discard(); return; }

            foreach (var (colId, size) in sizes)
            {
                if (size.Total == 0) continue;
                if (!_collections.TryGetValue((dbId, colId), out var col)) continue;

                var title = TitleText.FirstLine(col.Name);
                entries.Add((col.Order, new TaskVirtualList.Entry
                {
                    Group = new TaskGroup(
                        DbId:     dbId,
                        ColId:    colId,
                        Title:    title.Length > 0 ? title : "Без коллекции",
                        Subtitle: _dbs.Count > 1 ? NameOfDb(dbId) : "",
                        Color:    EventColors.Display(col.Color),
                        Tasks:    [],
                        Exact:    (size.Total, size.Done)),
                    Repo   = repo,
                    Stats  = byCol[colId],
                    View   = view,
                    Dated  = size.Dated,
                    Tasks  = size.Total,
                }));
            }
        }

        if (gen != _viewGen) return;
        TaskList.SetVirtual([.. entries.OrderBy(x => x.Order).Select(x => x.Entry)], NoTasksHint());
    }

    private string NameOfDb(int dbId)
    {
        foreach (var d in _dbs) if (d.DbId == dbId) return d.Name;
        return "";
    }

    private string NoTasksHint()
    {
        if (!string.IsNullOrWhiteSpace(_searchQuery)) return "Ничего не найдено";
        if (_filter != TaskFilter.All) return $"Нет задач: {_filter.Label.ToLowerInvariant()}";
        var noneChecked = _activeByDb is not null && _activeByDb.Count > 0 &&
                          _activeByDb.Values.All(ids => ids.Count == 0);
        return noneChecked ? "Отметьте коллекции в боковой панели" : "Нет задач";
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    // Цель по умолчанию (ее же подставляет список коллекций строки быстрого добавления):
    // первая отмеченная чекбоксом коллекция, иначе (коллекций нет вовсе) — null в БД по
    // умолчанию, и репозиторий создаст «Без названия».
    private (int DbId, int? ColId) ResolveQuickAddTarget()
    {
        foreach (var (dbId, _, repo) in _dbs)
        {
            HashSet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            foreach (var c in repo.GetAllCollections())
                if (c.Id is int cid && (active is null || active.Contains(cid)))
                    return (dbId, cid);
        }
        return (_dbs.Count > 0 ? _dbs[0].DbId : 0, null);
    }

    private async Task QuickAdd(QuickAddRequest req)
    {
        if (_dbs.Count == 0 || string.IsNullOrWhiteSpace(req.Title)) return;

        // Коллекцию задает список строки добавления; его нет (коллекций вовсе нет) —
        // возвращаемся к цели по умолчанию.
        var (dbId, colId) = req.Target is { } t ? (t.DbId, t.ColId) : ResolveQuickAddTarget();
        if (RepoFor(dbId) is not { } repo) return;

        // Задача доводит коллекцию до полного набора столбцов («Исполнитель», «Назначил»,
        // «Статус», «Дата», «Описание») — менять чужую коллекцию молча нельзя.
        // Ничего не изменится — окна не будет.
        //
        // Окно СЧИТАЕТ план по базе (PlanTaskSetup → CollectionFields), то есть читает ее —
        // и это чтение может упереться в чужого писателя. Раньше вызов стоял вне всякого
        // try, а сам QuickAdd запущен как fire-and-forget: SQLITE_BUSY отсюда становился
        // «unobserved task exception», который добивал финализатор — без тоста и без шанса
        // повторить. Ошибки БД теперь идут тем же путем, что и у остальных операций раздела.
        if (VisualRoot is Window owner)
        {
            bool prepared;
            try { prepared = await SectionSetupConfirm.ForTasks(owner, repo, colId); }
            catch (Exception ex) when (DbErrors.Handle(ex, repo.DbPath)) { return; }
            catch (InvalidOperationException ex) { TaskList.ShowError(ex.Message); return; }
            if (!prepared) return;
        }

        // Исполнитель — ровно как выбран в строке: пустой значит «не назначен», задача заводится
        // нераспределенной (раздел такие показывает прочерком). Назначил — сам пользователь:
        // он ставит задачу. Стадия — из настроек раздела.
        var executor = req.Executor;
        var status   = DefaultStageFor(repo, colId);
        if (!TryWrite(repo, () => repo.AddTaskRecord(colId, req.Title.Trim(), executor,
                                                     CurrentUser.DisplayName, status, req.Due)))
            return;

        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        // Тихо: список остается на экране и на прежнем месте (см. RefreshCurrent).
        RefreshCurrent(quiet: true);
    }

    // ── Массовые операции (контекстное меню карточек и Delete) ───────────────

    // Порог «мелкой» операции: до этого числа записи удаляются/дублируются сразу на общем
    // контексте, дальше — bulk-SQL на изолированных подключениях в фоне (как в календаре):
    // цикл с SaveChanges на КАЖДУЮ запись вешает интерфейс на сотнях.
    private const int SmallOpCap = 100;

    // Задача — это запись: одна запись с двумя полями-исполнителями дает две карточки, но
    // удаляется/дублируется все равно одна запись. Поэтому и считаем по записям.
    private static List<(int DbId, int Id, int? ColId)> ByRecord(IReadOnlyList<TaskRecord> tasks) =>
        tasks.Where(t => t.Id.HasValue)
             .GroupBy(t => (t.DbId, Id: t.Id!.Value))
             .Select(g => (g.Key.DbId, g.Key.Id, g.First().CollectionId))
             .ToList();

    // Запустить операцию раздела «в фоне» и НЕ потерять ее ошибку. Ожидаемые отказы (нет
    // прав, база занята) гасятся внутри самих операций через DbErrors; сюда доходит только
    // то, что мы не предусмотрели — и это должно попасть в лог, а не в финализатор.
    private static void Fire(Task work) => work.ContinueWith(
        t => Log.Error(t.Exception!, "Раздел «Задачи»: необработанная ошибка операции"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    // Запись в БД с обработкой ошибок: нет прав / база занята чужим писателем → тост
    // (Utils/DbErrors), прочие отказы репозитория → баннер списка. false — дальше не идем.
    private bool TryWrite(ConstructorRepository repo, Action write)
    {
        try { write(); return true; }
        catch (Exception ex) when (DbErrors.Handle(ex, repo.DbPath)) { return false; }
        catch (InvalidOperationException ex) { TaskList.ShowError(ex.Message); return false; }
    }

    // Bulk-путь: работа идет на изолированных подключениях в фоне, поверх списка — тот же
    // оверлей, что при долгой загрузке (процентов нет: объем здесь ограничен TaskLoadCap).
    private async Task RunBulkAsync(string dbPath, Action work)
    {
        LoadingOverlay.IsVisible = true;
        try { await Task.Run(work); }
        catch (Exception ex) when (DbErrors.Handle(ex, dbPath)) { }
        catch (InvalidOperationException ex) { TaskList.ShowError(ex.Message); }
        finally { LoadingOverlay.IsVisible = false; }
    }

    private async Task DeleteTasksWithConfirm(IReadOnlyList<TaskRecord> tasks)
    {
        if (VisualRoot is not Window win) return;
        var targets = ByRecord(tasks);
        if (targets.Count == 0) return;

        // Предупреждаем, что удаление оборвет ВХОДЯЩИЕ связи на эти записи — как в таблице и
        // календаре. Задачи собраны из всех допущенных БД, поэтому проверяем по каждой базе.
        var check = await RelationDeleteImpact.CheckRecordsAsync(targets.GroupBy(t => t.DbId)
            .Select(g => (Repo: RepoFor(g.Key), Ids: (IReadOnlyCollection<int>)g.Select(t => t.Id).ToList()))
            .Where(g => g.Repo is not null).Select(g => (g.Repo!, g.Ids)));
        if (check.Blocked is { } blocked) { await ConfirmDialog.Info(blocked).ShowDialog(win); return; }
        var linkWarn = check.Warning(targets.Count == 1 ? "нее" : "них");

        var dlg = new ConfirmDialog($"Вы удаляете {targets.Count} {TasksPlural(targets.Count)}.{linkWarn} Подтвердить?");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        TaskList.HideError();

        var byDb = targets.GroupBy(t => t.DbId)
                          .Select(g => (Repo: RepoFor(g.Key), Ids: g.Select(t => t.Id).ToList()))
                          .Where(x => x.Repo is not null)
                          .Select(x => (Repo: x.Repo!, x.Ids))
                          .ToList();
        if (byDb.Count == 0) return;

        if (targets.Count <= SmallOpCap)
        {
            foreach (var (repo, ids) in byDb)
            {
                // Не удалилось (нет прав, база занята, запрет) — стоп: удаленное остается удаленным,
                // список ниже перечитается и покажет фактическое состояние. Ссылки между задачами
                // одной базы запрету удаления не мешают: весь набор уходит в этой же операции.
                var stopped = false;
                foreach (var id in ids)
                    if (!TryWrite(repo, () => repo.DeleteCalendarRecord(id, ids))) { stopped = true; break; }
                if (stopped) break;
            }
        }
        else
            await RunBulkAsync(byDb[0].Repo.DbPath, () =>
            {
                foreach (var (repo, ids) in byDb) repo.DeleteRecordsIsolated(ids);
            });

        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        // Тихо: список остается на экране и на прежнем месте (см. RefreshCurrent).
        RefreshCurrent(quiet: true);
    }

    private async Task DuplicateTasksWithConfirm(IReadOnlyList<TaskRecord> tasks)
    {
        if (VisualRoot is not Window win) return;
        var targets = ByRecord(tasks);
        if (targets.Count == 0) return;

        var dlg = new ConfirmDialog($"Вы дублируете {targets.Count} {TasksPlural(targets.Count)}. Подтвердить?",
                                    confirmText: "Дублировать");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        TaskList.HideError();

        // Bulk считает позиции копий относительно якоря ОДНОЙ коллекции, поэтому ему нужна
        // группировка по (база, коллекция); задача без коллекции (ее быть не должно) в такую
        // группировку не ложится — весь набор тогда идет мелким путем.
        var groups = targets.GroupBy(t => (t.DbId, t.ColId))
                            .Select(g => (Repo: RepoFor(g.Key.DbId), g.Key.ColId,
                                          Ids: g.Select(t => t.Id).ToList()))
                            .Where(x => x.Repo is not null)
                            .Select(x => (Repo: x.Repo!, x.ColId, x.Ids))
                            .ToList();
        if (groups.Count == 0) return;

        if (targets.Count > SmallOpCap && groups.All(g => g.ColId is not null))
            await RunBulkAsync(groups[0].Repo.DbPath, () =>
            {
                foreach (var (repo, colId, ids) in groups)
                    repo.DuplicateRecordsIsolated(colId!.Value, ids);
            });
        else
        {
            foreach (var (repo, _, ids) in groups)
            {
                var stopped = false;
                foreach (var id in ids)
                    if (!TryWrite(repo, () => repo.DuplicateRecord(id))) { stopped = true; break; }
                if (stopped) break;
            }
        }

        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        // Тихо: список остается на экране и на прежнем месте (см. RefreshCurrent).
        RefreshCurrent(quiet: true);
    }

    // Русское склонение: 1 задачу, 2–4 задачи, 5+ задач (с учетом 11–14).
    private static string TasksPlural(int n)
    {
        var mod100 = n % 100;
        var mod10  = n % 10;
        if (mod100 is >= 11 and <= 14) return "задач";
        return mod10 switch
        {
            1 => "задачу",
            >= 2 and <= 4 => "задачи",
            _ => "задач",
        };
    }

    private async Task OpenEditDialog(TaskRecord t)
    {
        if (VisualRoot is not Window win || _dbs.Count == 0) return;

        var dlg = new TaskDialog(_dbs, t.DbId, editTask: t);
        var res = await dlg.ShowDialog<TaskDialogResult?>(win);
        if (res is null) return;

        var repo = RepoFor(t.DbId) ?? _dbs[0].Repo;

        // Входящие связи на задачу: удаление их убивает, смена коллекции пересоздает задачу с новым Id
        // (см. UpdateCalendarRecord) — ребра на старый Id умирают. Раньше и то и другое проходило молча,
        // тогда как те же действия в таблице спрашивают. Смены БД тут нет: в окне задачи выбор базы при
        // редактировании скрыт, а DbId ниже принудительно возвращается исходный.
        var movingCollection = res is { Deleted: false, Task: { } m } && m.CollectionId != t.CollectionId;
        if ((res.Deleted || movingCollection) && t.Id is int rid && RepoFor(t.DbId) is { } own
            && await RelationDeleteImpact.CheckRecordsAsync(own, [rid], move: !res.Deleted) is { } check
            && (check.Blocked is not null || check.References.Length > 0))
        {
            if (check.Blocked is { } blocked) { await ConfirmDialog.Info(blocked).ShowDialog(win); return; }
            var warn = res.Deleted
                ? new ConfirmDialog($"Удалить задачу?{check.Warning("нее")}", "Удалить")
                : new ConfirmDialog($"Перенос пересоздает задачу.{check.Warning("нее", move: true)} Перенести?", "Перенести");
            if (await warn.ShowDialog<bool>(win) != true) return;
        }

        // Правка тоже готовит коллекцию: перенос — целевую целиком, а правка на месте вернет
        // удаленные роли. Без этого введенные «Назначил»/«Статус» молча пропадали: писать их
        // некуда, и UpdateTaskRecord просто отбрасывал значения. Отказ = правка не сохраняется.
        if (!res.Deleted && res.Task is { } target)
        {
            var cid = target.CollectionId ?? t.CollectionId;
            if (!await SectionSetupConfirm.ForTasks(win, repo, cid,
                    moving: cid != t.CollectionId, adoptExec: false)) return;
            if (cid is int prepared)
            {
                try { repo.EnsureTaskCollection(prepared, adoptExisting: false); }
                catch (InvalidOperationException ex) { TaskList.ShowError(ex.Message); return; }
            }
        }

        try
        {
            if (res.Deleted)
            {
                if (t.Id is int id) repo.DeleteCalendarRecord(id);
            }
            else if (res.Task is { } upd)
            {
                upd.DbId = t.DbId;
                repo.UpdateTaskRecord(upd);
            }
        }
        catch (InvalidOperationException ex)
        {
            TaskList.ShowError(ex.Message);
            return;
        }

        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        // Тихо: список остается на экране и на прежнем месте (см. RefreshCurrent).
        RefreshCurrent(quiet: true);
    }
}
