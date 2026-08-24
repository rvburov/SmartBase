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
    private TaskFilterKind _filter = TaskFilterKind.All;
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
        FontScale.Changed += OnFontScaleChanged;
        if (_dbs.Count > 0 && _renderedFontDelta != FontScale.Delta) ApplyToList();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FontScale.Changed -= OnFontScaleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFontScaleChanged()
    {
        if (_dbs.Count > 0) ApplyToList();
    }

    public void Initialize(IReadOnlyList<DatabaseManager> databases)
    {
        SetDatabaseList(databases);

        NavbarControl.SearchChanged += (_, q) => { _searchQuery = q; ApplyToList(); };
        NavbarControl.FilterChanged += (_, kind) => { _filter = kind; ApplyToList(); };

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

    public void SetActiveIds(Dictionary<int, HashSet<int>> activeByDb)
    {
        // Тот же набор галочек — перечитывать нечего. Сайдбар присылает его и когда ничего не
        // менял (возврат в раздел, перемонтирование базы), а на большой базе каждый лишний
        // заход — это лишний счет по всем коллекциям и волна «Загрузка…» на ровном месте.
        if (TaskLoadPlan.SameActiveIds(_activeByDb, activeByDb)) return;

        _activeByDb = activeByDb;
        RefreshCurrent();
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

    // ── Загрузка ─────────────────────────────────────────────────────────────

    /// <param name="quiet">
    /// Тихое обновление: список остается на экране и возвращается на прежнее место, оверлей
    /// «Загрузка…» не показывается. Так перечитываются данные ПОСЛЕ действия над задачей —
    /// человек только что нажал «Сохранить» и смотрит на карточку, а не начинает все заново;
    /// волна «Загрузка…» на ее месте выглядит так, будто раздел открылся заново. То же правило
    /// в «Расписании» календаря: оно зовет это «пересборкой без свежести».
    /// </param>
    private void RefreshCurrent(bool quiet = false) => _ = LoadAsync(quiet);

    private async Task LoadAsync(bool quiet = false)
    {
        if (_dbs.Count == 0) { _loaded = []; ApplyToList(); return; }

        var gen       = ++_gen;
        var oldOffset = TaskList.ScrollOffset;
        var work      = LoadAllAsync();

        // Оверлей — только если загрузка дольше 400 мс (как сетка календаря) И показывать
        // нечего: при правке задачи на экране уже есть верный список.
        if (!quiet && await Task.WhenAny(work, Task.Delay(400)) != work && gen == _gen)
            LoadingOverlay.IsVisible = true;

        List<TaskRecord> list;
        Dictionary<(int DbId, int ColId), ConstructorRepository.TaskCollectionStats> stats;
        bool tasksRead;
        try { (list, stats, tasksRead) = await work; }
        catch { (list, stats, tasksRead) = ([], [], true); }   // фон: недоступная БД не валит UI

        if (gen != _gen) return;
        LoadingOverlay.IsVisible = false;
        _loaded      = list;
        _stats       = stats;
        _loadedStale = !tasksRead;
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
        LoadAllAsync()
    {
        var stats = new Dictionary<(int DbId, int ColId), ConstructorRepository.TaskCollectionStats>();
        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            foreach (var (colId, one) in await repo.GetTaskCollectionStatsAsync(active))
                stats[(dbId, colId)] = one;
        }

        var needTasks = TaskLoadPlan.NeedsTaskRead(stats.Values.Sum(s => s.Total), TaskLoadCap);
        if (!needTasks) return ([], stats, false);

        var result = new List<TaskRecord>();
        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            var part = await repo.LoadTasksAsync(active, TaskLoadCap);
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

        // Цель по умолчанию — та же, что была до появления выбора: первая отмеченная.
        var (targetDbId, targetColId) = ResolveQuickAddTarget();
        var fallback = new QuickCollectionOption(targetDbId, targetColId, "");
        TaskList.SetCollectionOptions(options, fallback);

        UpdateQuickUserOptions(TaskList.SelectedCollection?.DbId ?? targetDbId);
    }

    // «Исполнитель» строки быстрого добавления — пользователи базы, в которой заведется задача.
    private void UpdateQuickUserOptions(int dbId) =>
        TaskList.SetUserOptions(RepoFor(dbId)?.GetUserOptions() ?? [], CurrentUser.DisplayName);

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

        var shown = found.Where(t => TaskFilters.Matches(t, _filter, now, me));

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
            NavbarControl.SetFilterCounts(TaskFilters.Counts(found, now, me), _filter);
            return;
        }
        _ = RefreshFilterCountsFromDbAsync(++_countGen, now, me);
    }

    private async Task RefreshFilterCountsFromDbAsync(long gen, DateTime now, string me)
    {
        var kinds  = TaskFilters.All.Select(k => k.ToString()).ToList();
        var totals = TaskFilters.All.ToDictionary(k => k, _ => 0);
        // Считать точнее потолка пилюли незачем: на единицу больше — уже «999+».
        var cap    = Controls.Components.TaskFilterItem.CountCap + 1;
        var search = (_searchQuery ?? "").Trim();

        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            var byCol = StatsOfDb(dbId);
            if (byCol.Count == 0) continue;

            Dictionary<string, int> part;
            try { part = await repo.CountTaskViewsAsync(byCol, kinds, search, MeVariants(repo, me), now, cap); }
            catch { continue; }   // фон: недоступная БД не валит счетчики остальных

            if (gen != _countGen) return;
            foreach (var k in TaskFilters.All)
                totals[k] += part.GetValueOrDefault(k.ToString());
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

        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            var byCol = StatsOfDb(dbId);
            if (byCol.Count == 0) continue;

            // Вид у каждой базы свой: варианты написания текущего пользователя берутся из ее
            // списка пользователей.
            var view = new ConstructorRepository.TaskView(
                _filter.ToString(), search, MeVariants(repo, me), now);

            Dictionary<int, ConstructorRepository.TaskViewSize> sizes;
            try { sizes = await repo.CountTaskViewAsync(byCol, view); }
            catch { continue; }   // фон: недоступная БД не валит остальные

            if (gen != _viewGen) return;

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
        if (_filter != TaskFilterKind.All) return $"Нет задач: {TaskFilters.Label(_filter).ToLowerInvariant()}";
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

        var executor = req.Executor.Length > 0 ? req.Executor : CurrentUser.DisplayName;
        // Назначил — сам пользователь: он ставит задачу (по умолчанию — себе же).
        if (!TryWrite(repo, () => repo.AddTaskRecord(colId, req.Title.Trim(), executor,
                                                     CurrentUser.DisplayName,
                                                     ConstructorRepository.TaskStatusPlanned, req.Due)))
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
        var hasLinks = false;
        foreach (var g in targets.GroupBy(t => t.DbId))
        {
            if (RepoFor(g.Key) is not { } repo) continue;
            if (RelationDeleteImpact.ForRecords(repo, g.Key, g.Select(t => t.Id).ToList()))
            {
                hasLinks = true;
                break;
            }
        }
        var linkWarn = hasLinks ? " На них ссылаются связи из других записей — они будут удалены." : "";

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
                // Не удалилось (нет прав, база занята) — стоп: удаленное остается удаленным,
                // список ниже перечитается и покажет фактическое состояние.
                var stopped = false;
                foreach (var id in ids)
                    if (!TryWrite(repo, () => repo.DeleteCalendarRecord(id))) { stopped = true; break; }
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
            && RelationDeleteImpact.ForRecords(own, t.DbId, [rid]))
        {
            var warn = res.Deleted
                ? new ConfirmDialog("Удалить задачу? На нее ссылаются связи из других записей — "
                                  + "они будут удалены.", "Удалить")
                : new ConfirmDialog("Перенос пересоздает задачу: связи из других записей на нее "
                                  + "оборвутся (станут «связи нет»). Перенести?", "Перенести");
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
