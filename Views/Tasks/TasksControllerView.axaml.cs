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
    private List<TaskRecord> _loaded = [];
    // Порядок коллекций (как в сайдбаре) для групп списка: (DbId, ColId) → место. Снимается
    // при загрузке — ApplyToList зовётся и на каждый символ поиска.
    private Dictionary<(int DbId, int ColId), int> _collectionOrder = [];
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

        TaskList.TaskActivated     += (_, t)   => _ = OpenEditDialog(t);
        TaskList.QuickAddRequested += (_, req) => _ = QuickAdd(req);

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

    // Заголовок навбара, порядок коллекций для групп и опции исполнителя быстрого добавления.
    private void UpdateChrome()
    {
        NavbarControl.SetTitle("Задачи");

        // Коллекции — тем же порядком, что в сайдбаре (GetAllCollections отдаёт их по
        // Position), чтобы группы списка шли как строки дерева.
        var order = new Dictionary<(int DbId, int ColId), int>();
        foreach (var (dbId, _, repo) in _dbs)
            foreach (var c in repo.GetAllCollections())
                if (c.Id is int cid)
                    order[(dbId, cid)] = order.Count;
        _collectionOrder = order;

        var (targetDbId, _) = ResolveQuickAddTarget();
        var options = RepoFor(targetDbId)?.GetUserOptions() ?? [];
        TaskList.SetUserOptions(options, CurrentUser.DisplayName);
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

        // Базу в подзаголовке показываем, только когда их несколько (иначе шум).
        var groups = TaskGrouping.Build(items, _collectionOrder, _dbs.Count > 1 ? NameOfDb : null);

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
        var noneChecked = _activeByDb is not null && _activeByDb.Count > 0 &&
                          _activeByDb.Values.All(ids => ids.Count == 0);
        return noneChecked ? "Отметьте коллекции в боковой панели" : "Нет задач";
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    // Цель быстрого добавления: первая отмеченная чекбоксом коллекция, иначе (коллекций нет
    // вовсе) — null в БД по умолчанию (репозиторий создаст «Без названия»).
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

        var (dbId, colId) = ResolveQuickAddTarget();
        if (RepoFor(dbId) is not { } repo) return;

        // Задача доводит коллекцию до полного набора столбцов («Исполнитель», «Назначил»,
        // «Статус», «Дата», «Описание»), а цель здесь выбирается автоматически — тем более
        // нельзя менять чужую коллекцию молча. Ничего не изменится — окна не будет.
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
