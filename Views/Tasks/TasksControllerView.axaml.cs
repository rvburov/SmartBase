using Avalonia;
using Avalonia.Controls;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.Tasks;

// Оркестратор раздела «Задачи» (упрощённый CalendarControllerView): агрегат задач по
// ОТМЕЧЕННЫМ коллекциям всех допущенных БД, сгруппированный по коллекциям (заголовок
// коллекции + её задачи); поиск и сортировка — в памяти по загруженному (v1 читает не
// более TaskLimit на БД).
//
// «Открытой коллекции» здесь нет: клик по строке сайдбара — та же галочка, что и в
// календаре. Раньше клик сужал список до одной коллекции, и галочки остальных ни на что
// не влияли — увидеть задачи двух коллекций разом было нельзя.
public partial class TasksControllerView : UserControl
{
    // Создание задачи могло создать коллекцию/авто-поля — сайдбары и таблицы обновляются снаружи.
    public event EventHandler? CollectionsMayHaveChanged;

    private const int TaskLimit = 1000;

    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _dbs = [];
    private Dictionary<int, HashSet<int>>? _activeByDb;
    private string _searchQuery = "";
    private TaskFilterKind _filter = TaskFilterKind.All;
    private List<TaskRecord> _loaded = [];
    // Коллекции для заголовков групп: (DbId, ColId) → место в сайдбаре и цвет. Снимается
    // при загрузке — ApplyToList зовётся и на каждый символ поиска.
    private Dictionary<(int DbId, int ColId), (int Order, string Color)> _collections = [];
    private long _gen;   // поколение загрузки: устаревшие результаты не применяются

    private ConstructorRepository? RepoFor(int dbId)
    {
        foreach (var d in _dbs) if (d.DbId == dbId) return d.Repo;
        return null;
    }

    public TasksControllerView() { InitializeComponent(); }

    // Размер шрифта меняется живьём: карточки строятся в C# с размерами из ресурсов.
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

        TaskList.TaskActivated     += (_, t)   => _ = OpenEditDialog(t);
        TaskList.QuickAddRequested += (_, req) => _ = QuickAdd(req);
        TaskList.TasksDeleteRequested    += (_, ts) => _ = DeleteTasksWithConfirm(ts);
        TaskList.TasksDuplicateRequested += (_, ts) => _ = DuplicateTasksWithConfirm(ts);
        // Коллекция может жить в другой базе — «Исполнитель» показывает её пользователей.
        TaskList.QuickTargetChanged += (_, target) => UpdateQuickUserOptions(target.DbId);

        RefreshCurrent();
    }

    public void SetActiveIds(Dictionary<int, HashSet<int>> activeByDb)
    {
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

    private void SetDatabaseList(IReadOnlyList<DatabaseManager> databases)
    {
        _dbs.Clear();
        foreach (var d in databases) _dbs.Add((d.DbId, d.Name, d.Constructor));
    }

    // ── Загрузка ─────────────────────────────────────────────────────────────

    private void RefreshCurrent() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_dbs.Count == 0) { _loaded = []; ApplyToList(); return; }

        var gen  = ++_gen;
        var work = LoadAllAsync();

        // Оверлей — только если загрузка дольше 400 мс (как сетка календаря).
        if (await Task.WhenAny(work, Task.Delay(400)) != work && gen == _gen)
            LoadingOverlay.IsVisible = true;

        List<TaskRecord> list;
        try { list = await work; }
        catch { list = []; }   // фон: недоступная БД не валит UI

        if (gen != _gen) return;
        LoadingOverlay.IsVisible = false;
        _loaded = list;
        UpdateChrome();
        ApplyToList();
    }

    private async Task<List<TaskRecord>> LoadAllAsync()
    {
        var result = new List<TaskRecord>();
        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            var part = await repo.LoadTasksAsync(active, TaskLimit);
            foreach (var t in part) t.DbId = dbId;
            result.AddRange(part);
        }
        return result;
    }

    // Заголовок навбара, коллекции (группы списка + строка быстрого добавления) и опции
    // исполнителя.
    private void UpdateChrome()
    {
        NavbarControl.SetTitle("Задачи");

        // Коллекции — тем же порядком, что в сайдбаре (GetAllCollections отдаёт их по
        // Position), чтобы группы списка шли как строки дерева; цвет идёт на индикатор.
        var collections = new Dictionary<(int DbId, int ColId), (int Order, string Color)>();
        var options     = new List<QuickCollectionOption>();
        foreach (var (dbId, dbName, repo) in _dbs)
            foreach (var c in repo.GetAllCollections())
                if (c.Id is int cid)
                {
                    collections[(dbId, cid)] = (collections.Count, c.Color ?? EventColors.Default);
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

    // «Исполнитель» строки быстрого добавления — пользователи базы, в которой заведётся задача.
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

        // Счётчики фильтра — по набору ПОСЛЕ поиска, но ДО самого фильтра: цифра рядом с
        // пунктом равна тому, что покажет его выбор, и остальные пункты видны как есть.
        var found = items.ToList();
        var now   = DateTime.Now;
        var me    = CurrentUser.DisplayName;
        NavbarControl.SetFilterCounts(TaskFilters.Counts(found, now, me), _filter);

        var shown = found.Where(t => TaskFilters.Matches(t, _filter, now, me));

        // Базу в подзаголовке показываем, только когда их несколько (иначе шум).
        var groups = TaskGrouping.Build(shown, _collections, _dbs.Count > 1 ? NameOfDb : null);

        // Пусто по разным причинам — подсказка разная: снятые галочки исправляются в сайдбаре.
        TaskList.SetGroups(groups, NoTasksHint());
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

    // Цель по умолчанию (её же подставляет список коллекций строки быстрого добавления):
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

        // Коллекцию задаёт список строки добавления; его нет (коллекций вовсе нет) —
        // возвращаемся к цели по умолчанию.
        var (dbId, colId) = req.Target is { } t ? (t.DbId, t.ColId) : ResolveQuickAddTarget();
        if (RepoFor(dbId) is not { } repo) return;

        // Задача доводит коллекцию до полного набора столбцов («Исполнитель», «Назначил»,
        // «Статус», «Дата», «Описание») — менять чужую коллекцию молча нельзя.
        // Ничего не изменится — окна не будет.
        if (VisualRoot is Window owner && !await SectionSetupConfirm.ForTasks(owner, repo, colId)) return;

        try
        {
            var executor = req.Executor.Length > 0 ? req.Executor : CurrentUser.DisplayName;
            // Назначил — сам пользователь: он ставит задачу (по умолчанию — себе же).
            repo.AddTaskRecord(colId, req.Title.Trim(), executor,
                               CurrentUser.DisplayName, ConstructorRepository.TaskStatusPlanned, req.Due);
        }
        catch (InvalidOperationException ex)
        {
            TaskList.ShowError(ex.Message);
            return;
        }

        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        RefreshCurrent();
    }

    // ── Массовые операции (контекстное меню карточек и Delete) ───────────────

    // Порог «мелкой» операции: до этого числа записи удаляются/дублируются сразу на общем
    // контексте, дальше — bulk-SQL на изолированных подключениях в фоне (как в календаре):
    // цикл с SaveChanges на КАЖДУЮ запись вешает интерфейс на сотнях.
    private const int SmallOpCap = 100;

    // Задача — это запись: одна запись с двумя полями-исполнителями даёт две карточки, но
    // удаляется/дублируется всё равно одна запись. Поэтому и считаем по записям.
    private static List<(int DbId, int Id, int? ColId)> ByRecord(IReadOnlyList<TaskRecord> tasks) =>
        tasks.Where(t => t.Id.HasValue)
             .GroupBy(t => (t.DbId, Id: t.Id!.Value))
             .Select(g => (g.Key.DbId, g.Key.Id, g.First().CollectionId))
             .ToList();

    // Запись в БД с обработкой ошибок: нет прав / база занята чужим писателем → тост
    // (Utils/DbErrors), прочие отказы репозитория → баннер списка. false — дальше не идём.
    private bool TryWrite(ConstructorRepository repo, Action write)
    {
        try { write(); return true; }
        catch (Exception ex) when (DbErrors.Handle(ex, repo.DbPath)) { return false; }
        catch (InvalidOperationException ex) { TaskList.ShowError(ex.Message); return false; }
    }

    // Bulk-путь: работа идёт на изолированных подключениях в фоне, поверх списка — тот же
    // оверлей, что при долгой загрузке (процентов нет: объём здесь ограничен TaskLimit).
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

        // Предупреждаем, что удаление оборвёт ВХОДЯЩИЕ связи на эти записи — как в таблице и
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
                // Не удалилось (нет прав, база занята) — стоп: удалённое остаётся удалённым,
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
        RefreshCurrent();
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
        // группировка по (база, коллекция); задача без коллекции (её быть не должно) в такую
        // группировку не ложится — весь набор тогда идёт мелким путём.
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
        RefreshCurrent();
    }

    // Русское склонение: 1 задачу, 2–4 задачи, 5+ задач (с учётом 11–14).
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

        // Входящие связи на задачу: удаление их убивает, смена коллекции пересоздаёт задачу с новым Id
        // (см. UpdateCalendarRecord) — рёбра на старый Id умирают. Раньше и то и другое проходило молча,
        // тогда как те же действия в таблице спрашивают. Смены БД тут нет: в окне задачи выбор базы при
        // редактировании скрыт, а DbId ниже принудительно возвращается исходный.
        var movingCollection = res is { Deleted: false, Task: { } m } && m.CollectionId != t.CollectionId;
        if ((res.Deleted || movingCollection) && t.Id is int rid && RepoFor(t.DbId) is { } own
            && RelationDeleteImpact.ForRecords(own, t.DbId, [rid]))
        {
            var warn = res.Deleted
                ? new ConfirmDialog("Удалить задачу? На неё ссылаются связи из других записей — "
                                  + "они будут удалены.", "Удалить")
                : new ConfirmDialog("Перенос пересоздаёт задачу: связи из других записей на неё "
                                  + "оборвутся (станут «связи нет»). Перенести?", "Перенести");
            if (await warn.ShowDialog<bool>(win) != true) return;
        }

        // Правка тоже готовит коллекцию: перенос — целевую целиком, а правка на месте вернёт
        // удалённые роли. Без этого введённые «Назначил»/«Статус» молча пропадали: писать их
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
        RefreshCurrent();
    }
}
