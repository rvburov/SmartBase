using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Core.Models;
using Serilog;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.Components;

public enum SidebarMode { Calendar, Constructor, Tasks }

public partial class SidebarTree : UserControl
{
    // Календарь: активные id — по БД (DbId → набор id коллекций), т.к. id не уникальны между БД.
    public event EventHandler<Dictionary<int, HashSet<int>>>? ActiveIdsChanged;
    public event EventHandler?                                CalendarsChanged;

    // Конструктор: выбор несёт (DbId, CollectionId) — при мульти-маунте id не уникален между БД.
    public event EventHandler<EntityRef>? CollectionSelected;
    public event EventHandler<int>?       CollectionDeleted;
    public event EventHandler?            RequestRefresh;
    // Клик по запертой базе (без допуска) — MainWindow предлагает ввести пароль.
    public event EventHandler<int>?       DatabaseUnlockRequested;
    // Клик по ещё не смонтированной базе (в очереди/обновляется/недоступна) —
    // MainWindow показывает подсказку с текущим статусом.
    public event EventHandler<int>?       PendingDatabaseTapped;
    // Клик по коллекции, просмотр которой запрещён правами (замок) — MainWindow
    // поясняет, что доступ закрыт администратором. Аргумент — имя коллекции.
    public event EventHandler<string>?    CollectionAccessDenied;

    // Смонтированные БД сайдбара. В режиме календаря — одна (дефолт). В конструкторе —
    // все из рабочего пространства, и каждая показывается корневым узлом (_showRoots).
    // Locked — база смонтирована, но текущий пользователь не допущен (нет IsApproved и
    // не админ): корень показывается с замком, дети не строятся, клик — запрос доступа.
    private sealed record MountedDb(int DbId, string Name, ConstructorRepository Repo, bool Locked = false);
    private readonly List<MountedDb> _dbs = [];
    private bool _showRoots;

    // Ещё не смонтированные БД (фоновая очередь): узел-заглушка с именем и статусной
    // строкой («в очереди…» / «Обновление · 45% · ≈ 2 мин» / «недоступна…»). Содержимое
    // не строится — до готовности ни один модуль в базу не лезет. Статус обновляется
    // живьём через UpdatePendingStatus без перестройки дерева.
    private readonly List<PendingDb> _pending = [];

    private SidebarMode _mode = SidebarMode.Calendar;
    private SortState _sortState = SortState.None;
    private bool _expandedAll;

    // Режимы с чекбоксами видимости коллекций (календарь и задачи ведут себя одинаково).
    private bool ChecksMode => _mode is SidebarMode.Calendar or SidebarMode.Tasks;

    // Активны все коллекции, кроме явно отключённых пользователем.
    // Ключ — (DbId, CollectionId): id коллекций не уникальны между БД.
    // Набор переживает перезапуск: снятые галочки лежат в app.db (см. RestoreChecks).
    private readonly HashSet<(int Db, int Id)> _uncheckedIds = [];
    private bool _checksRestored;
    // Свёрнутые корни-БД (по DbId) — чтобы состояние раскрытия переживало Refresh.
    private readonly HashSet<int> _collapsedDbRoots = [];

    private List<SidebarNode> _roots = [];
    private readonly ObservableCollection<SidebarRow> _rows = [];

    private ConstructorRepository? RepoFor(int dbId) => _dbs.FirstOrDefault(d => d.DbId == dbId)?.Repo;
    private ConstructorRepository? RepoOf(SidebarNode node) => RepoFor(node.DbId);
    private int DefaultDbId => _dbs.Count > 0 ? _dbs[0].DbId : 0;

    // Описатели смонтированных БД для диалогов (выбор целевой БД + перенос).
    // Запертые базы (без допуска) целями не предлагаются.
    private List<DbOption> DbOptions() =>
        _dbs.Where(d => !d.Locked)
            .Select(d => new DbOption(
            d.DbId,
            string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name,
            d.Repo)).ToList();

    // Выделение: подсветка + цель удаления. Обычный клик — одна запись; Shift/Ctrl/Cmd-клик
    // — точечный toggle (группы и коллекции вместе) для пакетного удаления.
    private readonly HashSet<SidebarNode> _selectedNodes = [];
    // Открытая в основном окне коллекция — для подсветки и восстановления после Refresh.
    private EntityRef? _open;
    // Был ли последний клик мышью внутри сайдбара. Delete удаляет выделение сайдбара
    // только в этом случае — иначе пользователь работает в таблице/поле справа, и
    // клавиша не должна трогать выделение (в т.ч. пассивно подсвеченную коллекцию).
    private bool _lastPressInSidebar;

    public SidebarTree()
    {
        InitializeComponent();
        RowsHost.ItemsSource = _rows;

        CreateBtn.Click            += (_, _) => _ = CreateItem("collection", DefaultDbId, parentId: null);
        ExpandBtn.IsCheckedChanged += (_, _) => ToggleExpandAll(ExpandBtn.IsChecked == true);
        SortBtn.Click              += (_, _) => CycleSort();
        ToggleAllBtn.Click         += (_, _) => ToggleAllChecked();
        ImportExportBtn.Click      += (_, _) => _ = OpenImportExport();
    }

    // Календарь: одна БД, без корневых узлов (вид как прежде). DbId не важен — узлы
    // получают DbId = 0, RepoFor(0) указывает на единственный репозиторий.
    public void Initialize(ConstructorRepository repository, SidebarMode mode)
    {
        _dbs.Clear();
        _dbs.Add(new MountedDb(0, "", repository));
        _showRoots = false;
        _mode = mode;
        ToggleAllBtn.IsVisible = ChecksMode;
        RestoreChecks();
        Refresh();
    }

    // Конструктор: несколько БД из рабочего пространства, каждая — корневой узел.
    // База без допуска (Permissions.AccessGranted=false) отображается замком без содержимого.
    // pending — ещё не смонтированные БД фоновой очереди: узлы-заглушки со статусом.
    public void InitializeWorkspace(IReadOnlyList<DatabaseManager> databases, SidebarMode mode,
                                    IReadOnlyList<PendingDb>? pending = null)
    {
        _dbs.Clear();
        foreach (var d in databases)
            _dbs.Add(new MountedDb(d.DbId, d.Name, d.Constructor, !d.Permissions.AccessGranted));
        _pending.Clear();
        if (pending is not null) _pending.AddRange(pending);
        _showRoots = true;
        _mode = mode;
        ToggleAllBtn.IsVisible = ChecksMode;
        RestoreChecks();
        Refresh();
    }

    // Живое обновление статуса заглушки (каждые полсекунды во время миграции): меняем только
    // текст строки, без перестройки дерева — иначе сайдбар мигал бы и терял раскрытие/выделение.
    public void UpdatePendingStatus(int dbId, string status, bool failed)
    {
        var p = _pending.FirstOrDefault(p => p.DbId == dbId);
        if (p is null) return;
        p.Status = status;
        p.Failed = failed;

        foreach (var row in _rows)
            if (row.Node.IsPending && row.Node.DbId == dbId)
                row.SetStatus(status, failed);
    }

    public void Refresh()
    {
        if (_dbs.Count == 0 && _pending.Count == 0) return;

        _renderedFontDelta = FontScale.Delta;   // дерево отрисовано с текущим размером

        var expandedIds = GetExpandedGroupIds();
        CaptureDbRootStates();

        _roots = BuildTree();
        RestoreExpanded(_roots, expandedIds);

        // Мультивыделение не переживает перестройку дерева; восстанавливаем только
        // подсветку открытой коллекции по (DbId, Id) (режим конструктора — в календаре и
        // задачах открытой коллекции нет, там всё решают галочки).
        _selectedNodes.Clear();
        if (_mode is SidebarMode.Constructor && _open is { } open)
        {
            var node = FindNode(_roots, "collection", open.DbId, open.Id);
            if (node is not null) { _selectedNodes.Add(node); ExpandAncestors(_roots, node); }
            else _open = null;
        }

        RebuildRows();
        UpdateToggleAllIcon();
    }

    // Запоминаем, какие корни-БД свёрнуты, чтобы состояние пережило перестройку дерева.
    // Заглушки очереди не считаются: они всегда «свёрнуты», но после монтирования база
    // должна открыться раскрытой.
    private void CaptureDbRootStates()
    {
        foreach (var n in _roots.Where(n => n.IsDatabase && !n.IsPending))
        {
            if (n.IsExpanded) _collapsedDbRoots.Remove(n.DbId);
            else              _collapsedDbRoots.Add(n.DbId);
        }
    }

    // Активные коллекции по каждой смонтированной БД (для фильтра событий в календаре).
    public Dictionary<int, HashSet<int>> GetActiveIds()
    {
        var result = new Dictionary<int, HashSet<int>>();
        foreach (var db in _dbs)
        {
            // Запертая база событий не даёт (календарь получает только допущенные, но страхуемся).
            if (db.Locked) { result[db.DbId] = []; continue; }
            var ids = db.Repo.GetAllCollections()
                .Where(c => c.Id.HasValue).Select(c => c.Id!.Value)
                .Where(id => !_uncheckedIds.Contains((db.DbId, id)))
                .ToHashSet();
            result[db.DbId] = ids;
        }
        return result;
    }

    public void SelectCollection(int dbId, int collectionId)
    {
        if (_mode != SidebarMode.Constructor) return;
        var target = FindNode(_roots, "collection", dbId, collectionId);
        if (target is null) return;
        ExpandAncestors(_roots, target);
        _selectedNodes.Clear();
        _selectedNodes.Add(target);
        _open = new EntityRef(dbId, collectionId);
        RebuildRows();
    }

    // ── Плоский список видимых строк ─────────────────────────────────────────────

    private void RebuildRows()
    {
        _rows.Clear();
        Flatten(_roots, 0);
        // Первая строка стоит на месте шапки колонок таблицы (высота 36), остальные — на месте
        // строк данных (37). См. SidebarRow.RowHeight.
        if (_rows.Count > 0) _rows[0].IsFirstRow = true;
        MeasureRowWidth();
    }

    // Мерить каждую строку нельзя — узлов бывают тысячи. Сначала отбираем кандидатов дёшево
    // (отступ + число символов), потом честно меряем только их: шрифт пропорциональный, и
    // самое длинное имя не обязательно самое широкое.
    private const int    WidthCandidates = 16;
    private const double WidthSlack      = 4;    // промах даже на пиксель вернул бы дрожание экстента

    /// <summary>
    /// Общая ширина строк — по самой широкой из них. Ставится КАЖДОЙ строке, поэтому экстент
    /// горизонтальной прокрутки не зависит от того, какие строки сейчас реализованы
    /// (виртуализация), и полоса внизу не дёргается при вертикальной прокрутке.
    /// </summary>
    private void MeasureRowWidth()
    {
        if (_rows.Count == 0) return;

        var fontSize  = Metric("FontSizeBase",   14);
        var smallSize = Metric("FontSizeSmall",  12);
        var iconSize  = Metric("IconSizeMedium", 20);
        var family    = this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null;

        // Всё, что стоит СЛЕВА от текста, по шаблону строки: Padding строки (10), отступ
        // вложенности, слоты стрелки и чекбокса, иконка с её отступом (2) и поля текста (6 и 8).
        double Lead(SidebarRow r) =>
            10 + r.IndentMargin.Left + r.ChevronSlot + r.CheckboxSlot + 2 + iconSize + 6 + 8;

        var max = 0.0;
        foreach (var r in _rows
                     .OrderByDescending(r => Lead(r) + Math.Max(r.Name.Length, r.Status?.Length ?? 0))
                     .Take(WidthCandidates))
        {
            max = Math.Max(max, Lead(r) + Probe(r.Name, fontSize, family));
            // У заглушек очереди под именем есть статусная строка мелким шрифтом — она бывает длиннее.
            if (r.HasStatus) max = Math.Max(max, Lead(r) + Probe(r.Status!, smallSize, family));
        }

        var width = Math.Ceiling(max) + WidthSlack;
        foreach (var r in _rows) r.MinRowWidth = width;
    }

    private double Metric(string key, double fallback) =>
        this.TryFindResource(key, out var v) && v is double d ? d : fallback;

    private static double Probe(string text, double fontSize, FontFamily? family)
    {
        var probe = new TextBlock { Text = text, FontSize = fontSize };
        if (family is not null) probe.FontFamily = family;
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    private void Flatten(IEnumerable<SidebarNode> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            _rows.Add(new SidebarRow(n, depth, _mode, IsChecked(n), _selectedNodes.Contains(n)));
            if ((n.IsGroup || n.IsDatabase) && n.IsExpanded)
                Flatten(n.Children, depth + 1);
        }
    }

    private bool IsChecked(SidebarNode n) =>
        !n.IsGroup && !n.IsDatabase && n.Id.HasValue && !_uncheckedIds.Contains((n.DbId, n.Id.Value));

    // ── Построение дерева ──────────────────────────────────────────────────────

    private List<SidebarNode> BuildTree()
    {
        if (_dbs.Count == 0 && _pending.Count == 0) return [];

        // Конструктор: каждая БД — корневой узел, под ним её дерево.
        if (_showRoots)
        {
            var roots = new List<SidebarNode>();
            foreach (var db in _dbs)
            {
                var rootNode = new SidebarNode
                {
                    NodeType   = "database",
                    DbId       = db.DbId,
                    Id         = db.DbId,
                    Name       = db.Name,
                    IsLocked   = db.Locked,
                    IsExpanded = !db.Locked && !_collapsedDbRoots.Contains(db.DbId),
                };
                // Содержимое запертой базы не строится — данные недоступны до допуска.
                if (!db.Locked)
                    foreach (var child in BuildDbChildren(db))
                        rootNode.Children.Add(child);
                roots.Add(rootNode);
            }

            // Немонтированные БД — узлы-заглушки: имя приглушено, под ним статусная строка,
            // содержимого нет (модули не видят базу до готовности). Клик — подсказка о статусе.
            foreach (var p in _pending)
                roots.Add(new SidebarNode
                {
                    NodeType  = "database",
                    DbId      = p.DbId,
                    Id        = p.DbId,
                    Name      = p.Name,
                    IsPending = true,
                    Status    = p.Status,
                    Failed    = p.Failed,
                });

            // Порядок реестра (дефолтная первой, дальше по Id), а не порядок завершения
            // монтирования: базы не прыгают по списку, когда очередь их догружает.
            var defaultDbId = _dbs.Count > 0 ? _dbs[0].DbId : -1;
            return roots.OrderBy(r => r.DbId == defaultDbId ? -1 : r.DbId).ToList();
        }

        // Календарь: одна БД без корня.
        return _dbs.Count > 0 ? BuildDbChildren(_dbs[0]) : [];
    }

    private List<SidebarNode> BuildDbChildren(MountedDb db)
    {
        var groups      = db.Repo.GetAllGroups();
        var collections = db.Repo.GetAllCollections();
        var root        = new List<SidebarNode>();

        foreach (var g in groups.Where(g => g.ParentId is null).OrderBy(g => g.Position))
            root.Add(BuildGroupNode(db, g, groups, collections));
        foreach (var c in collections.Where(c => c.GroupId is null).OrderBy(c => c.Position))
            root.Add(MakeCollectionNode(db, c));

        return SortNodes(root);
    }

    private SidebarNode BuildGroupNode(MountedDb db, DbGroup group, List<DbGroup> allGroups, List<DbCollection> allCollections)
    {
        var node = new SidebarNode { NodeType = "group", DbId = db.DbId, Id = group.Id, Name = group.Name };

        var children = new List<SidebarNode>();
        foreach (var child in allGroups.Where(g => g.ParentId == group.Id).OrderBy(g => g.Position))
            children.Add(BuildGroupNode(db, child, allGroups, allCollections));
        foreach (var col in allCollections.Where(c => c.GroupId == group.Id).OrderBy(c => c.Position))
            children.Add(MakeCollectionNode(db, col));

        foreach (var c in SortNodes(children))
            node.Children.Add(c);

        return node;
    }

    // Коллекция без права просмотра выглядит как база без допуска: замок вместо иконки,
    // содержимое не открывается, контекстного меню нет (Locked гасит его на общих
    // основаниях). Замок ставим только в конструкторе: там клик по строке ОТКРЫВАЕТ
    // коллекцию, а в календаре и задачах строка — это переключатель набора.
    private SidebarNode MakeCollectionNode(MountedDb db, DbCollection c) =>
        new()
        {
            NodeType = "collection",
            DbId     = db.DbId,
            Id       = c.Id,
            Name     = c.Name,
            Color    = c.Color,
            IsLocked = _mode == SidebarMode.Constructor && c.Id is int cid &&
                       !(db.Repo.Permissions?.Can(PermAction.ViewCollection, cid) ?? true),
        };

    private List<SidebarNode> SortNodes(List<SidebarNode> nodes)
    {
        var groups = nodes.Where(n => n.IsGroup).ToList();
        var colls  = nodes.Where(n => !n.IsGroup).ToList();

        if (_sortState != SortState.None)
        {
            var desc = _sortState == SortState.Desc;
            groups = ApplyOrder(groups, desc);
            colls  = ApplyOrder(colls, desc);
        }
        return [.. groups, .. colls];
    }

    private static List<SidebarNode> ApplyOrder(List<SidebarNode> nodes, bool desc) =>
        desc ? nodes.OrderByDescending(n => NameKey(n.Name)).ToList()
             : nodes.OrderBy(n => NameKey(n.Name)).ToList();

    private static (int, string) NameKey(string name)
    {
        var s = name.TrimStart();
        return s.Length > 0 && char.IsDigit(s[0]) ? (0, s.ToLowerInvariant()) : (1, s.ToLowerInvariant());
    }

    // ── Раскрытие/сворачивание ──────────────────────────────────────────────────

    private void ToggleExpandAll(bool expand)
    {
        _expandedAll = expand;
        ToolTip.SetTip(ExpandBtn, expand ? "Свернуть все" : "Развернуть все");
        ExpandIcon.IsVisible   = !expand;
        CollapseIcon.IsVisible = expand;

        SetExpandedRecursive(_roots, expand);
        RebuildRows();
    }

    private static void SetExpandedRecursive(IEnumerable<SidebarNode> nodes, bool expand)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup || n.IsDatabase) n.IsExpanded = expand;
            SetExpandedRecursive(n.Children, expand);
        }
    }

    // Ключ раскрытия — (DbId, GroupId): id групп не уникальны между БД.
    private HashSet<(int Db, int Id)> GetExpandedGroupIds()
    {
        var ids = new HashSet<(int, int)>();
        Collect(_roots);
        return ids;

        void Collect(IEnumerable<SidebarNode> ns)
        {
            foreach (var n in ns)
            {
                if (n.IsGroup && n.IsExpanded && n.Id.HasValue) ids.Add((n.DbId, n.Id.Value));
                Collect(n.Children);
            }
        }
    }

    private void RestoreExpanded(IEnumerable<SidebarNode> nodes, HashSet<(int Db, int Id)> ids)
    {
        foreach (var n in nodes)
        {
            if (n.IsGroup && n.Id.HasValue)
                n.IsExpanded = _expandedAll || ids.Contains((n.DbId, n.Id.Value));
            RestoreExpanded(n.Children, ids);
        }
    }

    private static bool ExpandAncestors(IEnumerable<SidebarNode> nodes, SidebarNode target)
    {
        foreach (var n in nodes)
        {
            if (n == target) return true;
            if (ExpandAncestors(n.Children, target))
            {
                n.IsExpanded = true;
                return true;
            }
        }
        return false;
    }

    private static SidebarNode? FindNode(IEnumerable<SidebarNode> nodes, string type, int dbId, int id)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == type && node.DbId == dbId && node.Id == id) return node;
            var found = FindNode(node.Children, type, dbId, id);
            if (found is not null) return found;
        }
        return null;
    }

    // ── Сортировка ───────────────────────────────────────────────────────────────

    private void CycleSort()
    {
        _sortState = _sortState switch
        {
            SortState.None => SortState.Asc,
            SortState.Asc  => SortState.Desc,
            _              => SortState.None,
        };
        ToolTip.SetTip(SortBtn, _sortState switch
        {
            SortState.Asc  => "А → Я",
            SortState.Desc => "Я → А",
            _              => "Сортировка по имени",
        });
        Refresh();
    }

    // ── Чекбоксы видимости (только календарь) ────────────────────────────────────

    private void ToggleCollection(SidebarNode node)
    {
        if (!node.Id.HasValue) return;
        var key = (node.DbId, node.Id.Value);

        if (!_uncheckedIds.Remove(key)) _uncheckedIds.Add(key);

        RebuildRows();
        UpdateToggleAllIcon();
        PersistChecks();
        ActiveIdsChanged?.Invoke(this, GetActiveIds());
    }

    // ── Галочки между запусками (app.db) ─────────────────────────────────────
    //
    // У календаря и задач наборы РАЗНЫЕ: в задачах человек держит отмеченными одни
    // коллекции, в календаре — другие, и общий набор превратил бы каждое переключение
    // раздела в переделку галочек.
    private string ChecksScope => _mode == SidebarMode.Tasks ? "tasks" : "calendar";

    /// <summary>
    /// Поднять снятые галочки из настроек. Один раз за жизнь панели: сайдбар
    /// переинициализируется на каждое монтирование базы, и повторное чтение затирало бы
    /// то, что человек нащёлкал в этой сессии.
    /// </summary>
    private void RestoreChecks()
    {
        if (!ChecksMode || _checksRestored) return;
        _checksRestored = true;
        foreach (var key in AppSettings.LoadHiddenCollections(ChecksScope)) _uncheckedIds.Add(key);
    }

    private void PersistChecks()
    {
        if (!ChecksMode) return;

        // Ключи удалённых коллекций иначе копились бы всё время жизни установки. Чистим
        // только по базам, которые сейчас смонтированы и читаемы: у запертой (нет допуска)
        // список коллекций пуст, и её галочки стёрлись бы за компанию. Ключи баз, которых
        // сейчас нет вовсе (отключена, недоступна), не трогаем — база вернётся с ними.
        var readable = _dbs.Where(d => !d.Locked).ToList();
        var known    = readable.Select(d => d.DbId).ToHashSet();
        var alive    = readable
            .SelectMany(db => db.Repo.GetAllCollections()
                                     .Where(c => c.Id.HasValue)
                                     .Select(c => (Db: db.DbId, Id: c.Id!.Value)))
            .ToHashSet();
        _uncheckedIds.RemoveWhere(k => known.Contains(k.Db) && !alive.Contains(k));

        AppSettings.SaveHiddenCollections(ChecksScope, _uncheckedIds);
    }

    // Ключи (DbId, CollectionId) всех коллекций всех смонтированных БД.
    private IEnumerable<(int Db, int Id)> AllCollectionKeys() =>
        _dbs.SelectMany(db => db.Repo.GetAllCollections()
            .Where(c => c.Id.HasValue)
            .Select(c => (db.DbId, c.Id!.Value)));

    private bool AllChecked()
    {
        if (_dbs.Count == 0) return false;
        return AllCollectionKeys().All(k => !_uncheckedIds.Contains(k));
    }

    private void ToggleAllChecked()
    {
        if (_dbs.Count == 0) return;

        if (AllChecked())
            foreach (var k in AllCollectionKeys()) _uncheckedIds.Add(k);
        else
            _uncheckedIds.Clear();

        RebuildRows();
        UpdateToggleAllIcon();
        PersistChecks();
        ActiveIdsChanged?.Invoke(this, GetActiveIds());
    }

    private void UpdateToggleAllIcon()
    {
        if (!ChecksMode) return;
        var on = AllChecked();
        ToolTip.SetTip(ToggleAllBtn, on ? "Выключить все" : "Включить все");
    }

    // ── Взаимодействие со строками ───────────────────────────────────────────────

    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarRow row }) return;
        var node = row.Node;

        // Shift/Ctrl/Cmd + клик — точечный toggle узла в мультивыделение (группы и
        // коллекции вместе), без раскрытия/открытия. Так набираются элементы для
        // пакетного удаления через контекстное меню.
        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                    || e.KeyModifiers.HasFlag(KeyModifiers.Control)
                    || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        // Корень-БД в мультивыделение не добавляем (БД удаляются только из настроек).
        if (additive && node.Id.HasValue && !node.IsDatabase)
        {
            if (!_selectedNodes.Remove(node)) _selectedNodes.Add(node);
            RebuildRows();
            return;
        }

        // Немонтированная база (очередь/обновление/недоступна): вместо раскрытия —
        // подсказка с текущим статусом (раскрывать нечего, содержимое ещё не готово).
        if (node.IsPending)
        {
            PendingDatabaseTapped?.Invoke(this, node.DbId);
            return;
        }

        // Запертая база: вместо раскрытия — запрос доступа (пароль/ожидание допуска).
        if (node.IsDatabase && node.IsLocked)
        {
            DatabaseUnlockRequested?.Invoke(this, node.DbId);
            return;
        }

        // Коллекция без права просмотра: не открываем — поясняем, что доступ закрыт
        // администратором (иначе замок молчит и выглядит поломкой).
        if (node.IsLocked && !node.IsDatabase)
        {
            CollectionAccessDenied?.Invoke(this, node.Name);
            return;
        }

        // Обычный клик по раскрываемому узлу (группа или корень-БД) — раскрыть/свернуть.
        if (node.IsGroup || node.IsDatabase)
        {
            node.IsExpanded = !node.IsExpanded;
            if (node.IsDatabase)
            {
                if (node.IsExpanded) _collapsedDbRoots.Remove(node.DbId);
                else                 _collapsedDbRoots.Add(node.DbId);
            }
            ResetSelectionToOpen();   // открытая коллекция остаётся подсвеченной
            RebuildRows();
            return;
        }

        // Календарь и задачи — списки-агрегаты по ОТМЕЧЕННЫМ коллекциям: клик по строке
        // переключает её галочку. Раньше задачи вместо этого «открывали» коллекцию —
        // список сужался до одной, и галочки остальных ни на что не влияли.
        if (_mode is SidebarMode.Calendar or SidebarMode.Tasks)
        {
            _selectedNodes.Clear();
            ToggleCollection(node);   // RebuildRows внутри
        }
        else
        {
            _selectedNodes.Clear();
            _selectedNodes.Add(node);
            if (node.Id.HasValue)
            {
                _open = new EntityRef(node.DbId, node.Id.Value);
                RebuildRows();
                CollectionSelected?.Invoke(this, _open.Value);
            }
            else RebuildRows();
        }
    }

    // Сброс выделения к одной открытой коллекции (она остаётся подсвеченной при
    // обычном клике по группе), как было до мультивыделения.
    private void ResetSelectionToOpen()
    {
        _selectedNodes.Clear();
        if (_mode is SidebarMode.Constructor && _open is { } open)
        {
            var node = FindNode(_roots, "collection", open.DbId, open.Id);
            if (node is not null) _selectedNodes.Add(node);
        }
    }

    // Окно, на котором висит обработчик Delete (см. OnTopLevelKeyDown). Как в таблице:
    // ловим клавишу на уровне окна — фокус UserControl ненадёжен, и собственный OnKeyDown
    // часто не получает клавишу.
    private TopLevel? _keyHookTopLevel;

    // Дельта последнего рендера дерева — чтобы перерисовать при возврате, если размер
    // меняли, пока сайдбар был не виден.
    private int _renderedFontDelta = FontScale.Delta;
    private void OnFontScaleChanged() => Refresh();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyHookTopLevel = TopLevel.GetTopLevel(this);
        // Клавиши и клики ловим на уровне окна (туннель): фокус UserControl ненадёжен.
        _keyHookTopLevel?.AddHandler(KeyDownEvent,        OnTopLevelKeyDown,        RoutingStrategies.Tunnel);
        _keyHookTopLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);

        FontScale.Changed += OnFontScaleChanged;
        if (_dbs.Count > 0 && _renderedFontDelta != FontScale.Delta) Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHookTopLevel?.RemoveHandler(KeyDownEvent,        OnTopLevelKeyDown);
        _keyHookTopLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        _keyHookTopLevel = null;
        FontScale.Changed -= OnFontScaleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // Запоминаем, был ли клик внутри сайдбара — это и есть «пользователь работает здесь».
    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _lastPressInSidebar = e.Source is Visual v && (ReferenceEquals(v, this) || this.IsVisualAncestorOf(v));

    // Delete — удалить выделенные узлы (как пункт «Удалить» в меню), с тем же диалогом и
    // подсчётом. Срабатывает, только если последний клик был в сайдбаре (иначе клавиша
    // принадлежит таблице/полю справа).
    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Delete) return;
        if (_selectedNodes.Count == 0) return;
        if (IsTextInputFocused(e)) return;       // печатают в поле — Delete правит текст
        if (!_lastPressInSidebar) return;        // пользователь работает не в сайдбаре

        _ = DeleteNodes(_selectedNodes.ToList());
        e.Handled = true;
    }

    private static bool IsTextInputFocused(KeyEventArgs e) =>
        e.Source is TextBox || (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is not null);

    private void OnChevronTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarRow row }) return;
        var node = row.Node;
        if (!node.IsGroup && !node.IsDatabase) return;
        if (node.IsPending) return;   // у заглушки раскрывать нечего
        node.IsExpanded = !node.IsExpanded;
        if (node.IsDatabase)
        {
            if (node.IsExpanded) _collapsedDbRoots.Remove(node.DbId);
            else                 _collapsedDbRoots.Add(node.DbId);
        }
        RebuildRows();
        e.Handled = true;
    }

    private void OnCheckboxTapped(object? sender, TappedEventArgs e)
    {
        if (!ChecksMode) return;
        if (sender is not Control { DataContext: SidebarRow row } || row.Node.IsGroup) return;
        ToggleCollection(row.Node);
        e.Handled = true;
    }

    private void OnRowEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: SidebarRow row }) row.SetHovered(true);
    }

    private void OnRowExit(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: SidebarRow row }) row.SetHovered(false);
    }

    // ── Контекстное меню ────────────────────────────────────────────────────────

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SidebarRow row } anchor) return;
        if (!row.Node.Id.HasValue) return;
        // У запертой базы меню нет — содержимое недоступно до допуска. У коллекции без
        // права просмотра (тоже замок) — тем более: она закрыта целиком.
        // У немонтированной (очередь/обновление/недоступна) — тоже: база ещё не готова.
        if (row.Node.IsLocked || row.Node.IsPending) { e.Handled = true; return; }

        var menu = new ContextMenu();
        var node = row.Node;
        var db   = node.DbId;
        var id   = node.Id.Value;

        // Мультивыделение (клик по узлу из выделенного набора > 1): «Расположение»/«Дублировать»/
        // «Удалить» применяются ко ВСЕМУ набору, а «Изменить» — лишь к одному узлу, поэтому при
        // множественном выборе его скрываем (иначе пункт некорректен — правил бы только этот узел).
        var multi = _selectedNodes.Contains(node) && _selectedNodes.Count > 1;

        // Права текущего пользователя в БД узла: запрещённые пункты видны, но недоступны
        // (пользователь понимает, что действие существует, но закрыто администратором).
        var perms = RepoOf(node)?.Permissions;
        bool Can(PermAction a, int? cid = null) => perms?.Can(a, cid) ?? true;
        // Запрещённый пункт остаётся кликабельным и объясняет причину окном — как замок
        // у коллекции без права просмотра. Серый пункт молчал: непонятно, кто запретил.
        Action Guard(PermAction a, int? cid, Action run) =>
            () => PermissionNotice.Run(this, Can(a, cid), a, run);

        if (node.IsDatabase)
        {
            // Корень-БД: создание группы/коллекции в этой БД. Сама БД управляется в настройках.
            menu.Items.Add(MenuItemCreateGroup(Guard(PermAction.CreateCollection, null,
                () => _ = CreateItem("group", db, parentId: null))));
            menu.Items.Add(MenuItemCreateCollection(Guard(PermAction.CreateCollection, null,
                () => _ = CreateItem("collection", db, parentId: null))));
        }
        else if (node.IsGroup)
        {
            menu.Items.Add(MenuItemCreateGroup(Guard(PermAction.CreateCollection, null,
                () => _ = CreateItem("group", db, parentId: id))));
            menu.Items.Add(MenuItemCreateCollection(Guard(PermAction.CreateCollection, null,
                () => _ = CreateItem("collection", db, parentId: id))));
            menu.Items.Add(new Separator());
            if (!multi) menu.Items.Add(MenuItemEdit(Guard(PermAction.EditCollection, null,
                () => _ = EditGroup(db, id))));
            // Перенос = изъятие из текущего места: право как у удаления.
            menu.Items.Add(MenuItemLocation(Guard(PermAction.DeleteCollection, null,
                () => RelocateSelectionOrNode(node))));
            menu.Items.Add(MenuItemDuplicate(Guard(PermAction.DuplicateCollection, null,
                () => DuplicateSelectionOrNode(node))));
            menu.Items.Add(MenuItemDelete(Guard(PermAction.DeleteCollection, null,
                () => _ = DeleteSelectionOrNode(node))));
        }
        else
        {
            if (ChecksMode)
            {
                // Подменю (цвет, поля) — по клику по пункту меню не запускается действие,
                // поэтому запрет здесь честнее показать недоступностью самого подменю.
                var color = BuildColorMenu(db, id, node.Color);
                color.IsEnabled = Can(PermAction.EditCollection, id);
                menu.Items.Add(color);
                var fields = _mode == SidebarMode.Calendar
                    ? BuildDateFieldsMenu(db, id)
                    : BuildUserFieldsMenu(db, id);
                fields.IsEnabled = Can(PermAction.EditField, id);
                menu.Items.Add(fields);
                menu.Items.Add(new Separator());
            }
            if (!multi) menu.Items.Add(MenuItemEdit(Guard(PermAction.EditCollection, id,
                () => _ = EditCollection(db, id))));
            menu.Items.Add(MenuItemLocation(Guard(PermAction.DeleteCollection, id,
                () => RelocateSelectionOrNode(node))));
            menu.Items.Add(MenuItemDuplicate(Guard(PermAction.DuplicateCollection, id,
                () => DuplicateSelectionOrNode(node))));
            menu.Items.Add(MenuItemDelete(Guard(PermAction.DeleteCollection, id,
                () => _ = DeleteSelectionOrNode(node))));
        }

        menu.Open(anchor);
        e.Handled = true;
    }

    private void OnEmptyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control anchor) return;

        // Создание из пустой области идёт в БД по умолчанию.
        var canCreate = RepoFor(DefaultDbId)?.Permissions?.Can(PermAction.CreateCollection) ?? true;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemCreateGroup(() => PermissionNotice.Run(
            this, canCreate, PermAction.CreateCollection,
            () => _ = CreateItem("group", DefaultDbId, parentId: null))));
        menu.Items.Add(MenuItemCreateCollection(() => PermissionNotice.Run(
            this, canCreate, PermAction.CreateCollection,
            () => _ = CreateItem("collection", DefaultDbId, parentId: null))));
        menu.Open(anchor);
        e.Handled = true;
    }

    // Сдвиг попапа сабменю — общий для всех подменю сайдбара (цвет, поля-даты, исполнители).
    private static void FixSubmenuGap(MenuItem item) => MenuIcons.FixSubmenuGap(item);

    // Палитра и разметка пунктов — общие (ColorMenu). У коллекции «без цвета» хранится
    // не как NULL, а дефолтным синим (EventColors.Default) — это её исторический контракт.
    private MenuItem BuildColorMenu(int dbId, int collectionId, string? current) =>
        ColorMenu.Build(
            MenuIcons.ColorHeader(),
            current == EventColors.Default ? null : current,
            hex => ChangeColor(dbId, collectionId, hex ?? EventColors.Default));

    // Пункт «Создать группу» с иконкой папки с плюсом.
    private static MenuItem MenuItemCreateGroup(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.CreateGroupHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Пункт «Создать коллекцию» с иконкой таблицы с плюсом.
    private static MenuItem MenuItemCreateCollection(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.CreateCollectionHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Пункт «Изменить» с иконкой карандаша.
    private static MenuItem MenuItemEdit(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.EditHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Пункт «Расположение» с иконкой дерева папок.
    private static MenuItem MenuItemLocation(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.LocationHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Пункт «Дублировать» с иконкой копирования.
    private static MenuItem MenuItemDuplicate(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.DuplicateHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Красный пункт «Удалить» с иконкой корзины.
    private static MenuItem MenuItemDelete(Action onClick)
    {
        var item = new MenuItem { Header = MenuIcons.DeleteHeader() };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ChangeColor(int dbId, int collectionId, string hex)
    {
        var repo = RepoFor(dbId);
        if (repo is null) return;
        try
        {
            var col = repo.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
            if (col is null) return;

            col.Color = hex;
            repo.UpdateCollection(col);
        }
        catch (Exception ex) when (DbErrors.Handle(ex, repo.DbPath)) { return; }
        Refresh();
        CalendarsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── «Отображение свойств даты» ────────────────────────────────────────────────

    // Подменю (как «Изменить цвет»): открывается справа от контекстного меню, со стрелкой
    // и тем же стилем, контекстное меню остаётся открытым. Внутри — панель выбора, какие
    // поля-даты коллекции показывать в календаре (вкл/выкл IsCalStart): поиск + чеклист
    // с «Выделить всё», как у фильтра колонок таблицы. Применяется сразу по клику чекбокса.
    private MenuItem BuildDateFieldsMenu(int dbId, int collectionId)
    {
        var dateItem = new MenuItem { Header = MenuIcons.DateFieldsHeader() };
        FixSubmenuGap(dateItem);

        // Контент в дочернем пункте со StaysOpenOnClick — клики по чекбоксам не закрывают меню.
        // Класс dateFieldsHost убирает chrome пункта меню (паддинг/подсветку наведения),
        // чтобы внутри была чистая панель фильтра, а не подсвеченная строка меню.
        var host = new MenuItem { StaysOpenOnClick = true, Classes = { "dateFieldsHost" } };
        var repo = RepoFor(dbId);
        if (repo is not null)
        {
            var dateFields = repo.GetFields(collectionId).Where(f => f.FieldType == "date").ToList();
            host.Header = BuildDateFieldsPanel(repo, dateFields);
        }
        dateItem.Items.Add(host);
        return dateItem;
    }

    // Разовая фоновая заливка проекции включённого поля-даты (наследие старого поведения,
    // когда выключение удаляло строки calendar_index): изолированное подключение, окно
    // прогресса при >400 мс — как массовые операции; по завершении календарь обновляется.
    private async Task FillDateFieldProjectionAsync(ConstructorRepository repo, int fieldId)
    {
        var total = 0;
        try { total = await repo.CountFieldValuesAsync(fieldId); } catch { }

        ProgressDialog? dlg = null;
        var lastPct = -1;
        IProgress<int> p = new IntProgress(done =>
        {
            if (dlg is null || total == 0) return;
            var pct = (int)Math.Min(100, done * 100L / total);
            if (pct == lastPct) return;
            lastPct = pct;
            Dispatcher.UIThread.Post(() => dlg.SetProgress(pct / 100.0, "Заполнение календаря…"));
        });

        var bg = repo.FillCalendarIndexForFieldAsync(fieldId, p);
        Task? dlgClosed = null;
        if (await Task.WhenAny(bg, Task.Delay(400)) != bg && OwnerWindow is not null)
        {
            dlg = new ProgressDialog { Title = "Отображение свойств даты" };
            dlg.SetInfo($"Количество значений: {total}");
            dlg.SetProgress(0, "Заполнение календаря…");
            dlgClosed = dlg.ShowDialog(OwnerWindow);
        }
        try { await bg; } catch { /* фон: недоступная БД не валит UI, состояние покажет refresh */ }
        if (dlg is not null) { dlg.Done(); if (dlgClosed is not null) await dlgClosed; }

        CalendarsChanged?.Invoke(this, EventArgs.Empty);
    }

    private Control BuildDateFieldsPanel(ConstructorRepository repo, List<DbField> dateFields)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;
        var radius    = (CornerRadius)this.FindResource("RadiusBase")!;

        var panel = new StackPanel
        {
            Width = 240, Margin = new Thickness(10, 8), Spacing = 6,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };

        var search = new TextBox
        {
            Watermark = "Поиск свойств...",
            Height = 30, CornerRadius = radius,
            Background = Brushes.White,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        if (dateFields.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Нет свойств с датой", Foreground = secondary, FontSize = fontBase,
                Margin = new Thickness(2, 2, 0, 0),
            });
            return panel;
        }

        var syncing   = false;
        var selectAll = new CheckBox
        {
            Content = "(Выделить всё)", FontSize = fontBase,
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        var list = new StackPanel { Spacing = 4 };
        var rows = new List<(DbField Field, CheckBox Box)>();

        bool Matches(DbField f) =>
            string.IsNullOrWhiteSpace(search.Text) ||
            f.Name.Contains(search.Text!.Trim(), StringComparison.OrdinalIgnoreCase);

        void SyncSelectAll()
        {
            syncing = true;
            var vis = rows.Where(r => Matches(r.Field)).ToList();
            selectAll.IsChecked = vis.Count > 0 && vis.All(r => r.Box.IsChecked == true);
            syncing = false;
        }

        foreach (var f in dateFields)
        {
            var fid = f.Id!.Value;
            var cb = new CheckBox
            {
                Content = f.Name, FontSize = fontBase, IsChecked = f.IsCalStart,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (syncing) return;
                // Мгновенно: видимость поля — фильтр чтения, проекция не пересобирается.
                // true — включили поле, чью проекцию удалила старая версия: разовая
                // фоновая заливка с окном прогресса, calendar обновится по завершении.
                var needsFill = repo.SetDateFieldVisible(fid, cb.IsChecked == true);
                if (needsFill) _ = FillDateFieldProjectionAsync(repo, fid);
                else CalendarsChanged?.Invoke(this, EventArgs.Empty);
                SyncSelectAll();
            };
            rows.Add((f, cb));
            list.Children.Add(cb);
        }

        // «Выделить всё» действует на видимое (после поиска) подмножество.
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            var on = selectAll.IsChecked == true;
            foreach (var (f, box) in rows.Where(r => Matches(r.Field)))
                if ((box.IsChecked == true) != on) box.IsChecked = on;   // дёргает обработчик чекбокса
        };

        search.TextChanged += (_, _) =>
        {
            foreach (var (f, box) in rows) box.IsVisible = Matches(f);
            SyncSelectAll();
        };

        SyncSelectAll();

        var listBox = new Border
        {
            Background       = Brushes.White,
            BorderBrush     = (IBrush)this.FindResource("BrushSeparator")!,
            CornerRadius    = radius,
            // Padding НУЛЕВОЙ: полоса прокрутки прилегает ко всем краям карточки без белого
            // зазора, воздух вокруг содержимого задаёт HeaderedList.
            // ClipToBounds — квадратные углы полосы срезаются скруглением карточки.
            Padding         = new Thickness(0),
            ClipToBounds    = true,
            // «Выделить всё» — закреплённая шапка; полоса стоит на её уровне.
            Child = ScrollBarLink.HeaderedList(
                selectAll, new ScrollViewer { MaxHeight = 240, Content = list }),
        };
        // Карточка живёт в попапе сабменю — попап наследует зум (InheritsTransform
        // ставит стиль MenuItem в Controls.axaml), токен обычный.
        listBox.Bind(Border.BorderThicknessProperty, listBox.GetResourceObservable("BorderLine"));
        panel.Children.Add(listBox);

        return panel;
    }

    // ── «Отображение свойств исполнителя» (режим задач) ──────────────────────────

    // Зеркало BuildDateFieldsMenu для раздела задач: какие user-поля коллекции дают
    // задачи (вкл/выкл IsTaskExec). Первое включение авто-создаёт «Назначил»/«Статус» —
    // тогда обновляем и другие сайдбары/таблицу (RequestRefresh).
    private MenuItem BuildUserFieldsMenu(int dbId, int collectionId)
    {
        var userItem = new MenuItem { Header = MenuIcons.UserFieldsHeader() };
        FixSubmenuGap(userItem);

        var host = new MenuItem { StaysOpenOnClick = true, Classes = { "dateFieldsHost" } };
        var repo = RepoFor(dbId);
        if (repo is not null)
        {
            var userFields = repo.GetFields(collectionId).Where(f => f.FieldType == "user").ToList();
            host.Header = BuildUserFieldsPanel(repo, userFields);
        }
        userItem.Items.Add(host);
        return userItem;
    }

    private Control BuildUserFieldsPanel(ConstructorRepository repo, List<DbField> userFields)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;
        var radius    = (CornerRadius)this.FindResource("RadiusBase")!;

        var panel = new StackPanel
        {
            Width = 240, Margin = new Thickness(10, 8), Spacing = 6,
            Cursor = new Cursor(StandardCursorType.Arrow),
        };

        var search = new TextBox
        {
            Watermark = "Поиск свойств...",
            Height = 30, CornerRadius = radius,
            Background = Brushes.White,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        if (userFields.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Нет свойств типа «Пользователь»", Foreground = secondary, FontSize = fontBase,
                Margin = new Thickness(2, 2, 0, 0),
            });
            return panel;
        }

        var syncing = false;
        var list    = new StackPanel { Spacing = 4 };
        var rows    = new List<(DbField Field, CheckBox Box)>();

        bool Matches(DbField f) =>
            string.IsNullOrWhiteSpace(search.Text) ||
            f.Name.Contains(search.Text!.Trim(), StringComparison.OrdinalIgnoreCase);

        // Исполнитель у коллекции ОДИН, поэтому «(Выделить всё)» здесь нет, а остальные пункты
        // гаснут, пока занят один: список читается как выбор одного носителя, а не как набор
        // фильтров (в отличие от полей-дат календаря, которых может быть сколько угодно).
        void SyncEnabled()
        {
            var taken = rows.Any(r => r.Box.IsChecked == true);
            foreach (var (_, box) in rows)
            {
                var self = box.IsChecked == true;
                box.IsEnabled = self || !taken;
                ToolTip.SetTip(box, taken && !self
                    ? "Исполнитель может быть только один — снимите переключатель с текущего свойства"
                    : null);
            }
        }

        foreach (var f in userFields)
        {
            var fid = f.Id!.Value;
            var cb = new CheckBox
            {
                Content = f.Name, FontSize = fontBase, IsChecked = f.IsTaskExec,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (syncing) return;
                bool fieldsCreated;
                try { fieldsCreated = repo.SetUserFieldTaskExec(fid, cb.IsChecked == true); }
                catch (InvalidOperationException)
                {
                    // Нет права создать авто-поля («Назначил»/«Статус») либо исполнитель уже
                    // занят другим свойством — откатываем галку.
                    syncing = true;
                    cb.IsChecked = !cb.IsChecked;
                    syncing = false;
                    SyncEnabled();
                    return;
                }
                CalendarsChanged?.Invoke(this, EventArgs.Empty);
                if (fieldsCreated) RequestRefresh?.Invoke(this, EventArgs.Empty);
                SyncEnabled();
            };
            rows.Add((f, cb));
            list.Children.Add(cb);
        }

        search.TextChanged += (_, _) =>
        {
            foreach (var (f, box) in rows) box.IsVisible = Matches(f);
        };

        SyncEnabled();

        var listBox = new Border
        {
            Background       = Brushes.White,
            BorderBrush     = (IBrush)this.FindResource("BrushSeparator")!,
            CornerRadius    = radius,
            // Padding НУЛЕВОЙ: полоса прокрутки прилегает ко всем краям карточки без белого
            // зазора, воздух вокруг содержимого задаёт HeaderedList.
            // ClipToBounds — квадратные углы полосы срезаются скруглением карточки.
            Padding         = new Thickness(0),
            ClipToBounds    = true,
            Child           = new ScrollViewer { MaxHeight = 240, Content = list },
        };
        listBox.Bind(Border.BorderThicknessProperty, listBox.GetResourceObservable("BorderLine"));
        panel.Children.Add(listBox);

        return panel;
    }

    // ── CRUD ────────────────────────────────────────────────────────────────────

    private Window? OwnerWindow => VisualRoot as Window;

    private async Task CreateItem(string presetType, int dbId, int? parentId)
    {
        if (OwnerWindow is null || RepoFor(dbId) is null) return;

        // Тултип «Создать» кнопки «+» не должен висеть и мерцать поверх модального
        // окна: закрываем его и отключаем сервис тултипа на кнопке на время диалога
        // (при открытой модалке окно-владелец «полуактивно», и тултип переоткрывается
        // от событий курсора над диалогом — отсюда мигание).
        ToolTip.SetIsOpen(CreateBtn, false);
        ToolTip.SetServiceEnabled(CreateBtn, false);
        CreateItemResult? res;
        try
        {
            var dlg = new CreateItemDialog(DbOptions(), dbId, presetType, parentId);
            res = await dlg.ShowDialog<CreateItemResult?>(OwnerWindow);
        }
        finally
        {
            ToolTip.SetServiceEnabled(CreateBtn, true);
        }
        if (res is null) return;

        // Целевая БД — выбранная в диалоге (может отличаться от исходной).
        var repo = RepoFor(res.DbId);
        if (repo is null) return;

        // Файл занят чужим писателем/пропал — тост (DbErrors), создание не применяется.
        try
        {
            if (res.Type == "group")
            {
                var pos = repo.GetAllGroups().Select(g => g.Position).DefaultIfEmpty(0).Max() + 1000;
                var group = repo.AddGroup(new DbGroup { Name = res.Name, ParentId = res.ParentId, Position = pos });
                ReloadAndNotify();
                if (group.Id.HasValue) ExpandGroup(res.DbId, group.Id.Value);
            }
            else
            {
                var pos = repo.GetAllCollections().Select(c => c.Position).DefaultIfEmpty(0).Max() + 1000;
                var col = repo.AddCollection(new DbCollection { Name = res.Name, GroupId = res.ParentId, Position = pos });
                if (col.Id.HasValue)
                    // Title задаём реальным текстом «Без названия» (как кнопка «+»),
                    // а не оставляем пустым с одним плейсхолдером в ячейке.
                    repo.AddRecord(new DbRecord { CollectionId = col.Id.Value, Title = "Без названия" });
                ReloadAndNotify();
                if (col.Id.HasValue && _mode == SidebarMode.Constructor) OpenCollection(res.DbId, col.Id.Value);
            }
        }
        catch (Exception ex) when (DbErrors.Handle(ex, repo.DbPath)) { }
    }

    // Импорт/экспорт таблиц. Окно собирает заявку (ImportRequest или ExportRequest), здесь —
    // общий retry-цикл: показываем окно, выполняем заявку с окном загрузки (проценты + отмена),
    // при ошибке открываем окно заново с теми же значениями и текстом ошибки, при отмене — выходим.
    private async Task OpenImportExport()
    {
        if (OwnerWindow is null || RepoFor(DefaultDbId) is null) return;

        ImportDraft? prefill       = null;
        ExportDraft? exportPrefill = null;
        string?      error         = null;

        while (true)
        {
            // Тултип кнопки не должен мерцать поверх модального окна (как в CreateItem).
            ToolTip.SetIsOpen(ImportExportBtn, false);
            ToolTip.SetServiceEnabled(ImportExportBtn, false);
            object? req;
            try
            {
                var dlg = new ImportExportDialog(DbOptions(), DefaultDbId, prefill, error, exportPrefill);
                req = await dlg.ShowDialog<object?>(OwnerWindow);
            }
            finally
            {
                ToolTip.SetServiceEnabled(ImportExportBtn, true);
            }

            switch (req)
            {
                case ImportRequest ir:
                    (prefill, error) = await RunImport(ir);
                    if (error is null) return;
                    exportPrefill = null;
                    break;
                case ExportRequest er:
                    (exportPrefill, error) = await RunExport(er);
                    if (error is null) return;
                    prefill = null;
                    break;
                default:
                    return;   // отмена
            }
        }
    }

    // Импорт таблиц в новые коллекции. Планировщик разворачивает заявку в юниты «одна
    // коллекция — один источник» (файл/лист/файлы папки), юниты выполняются по очереди в фоне
    // (проход-анализ 0–30 % юнита, потоковая запись 30–100 %). Ошибка → (черновик, текст) для
    // переоткрытия окна (готовые коллекции остаются, сбойная удалена репозиторием);
    // успех или отмена пользователем → (null, null).
    private async Task<(ImportDraft? Prefill, string? Error)> RunImport(ImportRequest req)
    {
        var repo = RepoFor(req.DbId);
        if (repo is null) return (null, null);

        // OwnerWindow — вычисляемое свойство (VisualRoot as Window), каждое обращение читает
        // визуальное дерево заново. Берём значение один раз: и владелец гарантированно тот же,
        // и ShowDialog ниже не получит null, если панель успели отцепить от окна.
        var owner = OwnerWindow;
        if (owner is null) return (null, null);

        // Окно загрузки: показываем модально. Задачу ShowDialog запоминаем и дожидаемся
        // её завершения после закрытия — иначе повторный показ окна импорта (при ошибке)
        // стартует поверх ещё не закрытого модального окна и падает.
        var progress = new ProgressDialog();
        using var cts = new CancellationTokenSource();
        progress.EnableCancel(() => cts.Cancel());
        IProgress<(double F, string Phase, string Info)> ui =
            new Progress<(double F, string Phase, string Info)>(t =>
            {
                progress.SetProgress(t.F, t.Phase);
                progress.SetInfo(t.Info);
            });
        progress.SetProgress(0, "Анализ файла…");   // фаза до показа — окно не меняет высоту
        var progressClosed = progress.ShowDialog(owner);

        var created = new List<int>();
        try
        {
            await Task.Run(() =>
            {
                var units = ImportPlanner.Plan(req.Spec);
                for (var u = 0; u < units.Count; u++)
                {
                    var unit = units[u];
                    var info = units.Count > 1
                        ? $"Файл {u + 1} из {units.Count}: {unit.CollectionName}" : "";
                    var baseF = (double)u / units.Count;
                    var slice = 1.0 / units.Count;

                    try
                    {
                        var src = unit.OpenSource();
                        var schema = src.Analyze(
                            new Progress<double>(p =>
                                ui.Report((baseF + slice * p * 0.3, "Анализ файла…", info))),
                            cts.Token);

                        var rows = SourceHelpers.WithTitles(src.ReadRows(cts.Token), schema.EmptyTitleCount);

                        // Импорт на ОТДЕЛЬНОМ контексте: с фонового потока нельзя трогать общий
                        // Context (его параллельно использует UI) — иначе EF «second operation».
                        var colId = repo.ImportStreamedCollectionIsolated(
                            unit.CollectionName, req.ParentId, schema, rows, cts.Token,
                            new Progress<long>(n => ui.Report((
                                baseF + slice * (0.3 + 0.7 * Math.Min(1.0,
                                    schema.RowCount == 0 ? 1.0 : n / (double)schema.RowCount)),
                                "Импорт записей…", info))));
                        created.Add(colId);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Дружелюбный текст + в мульти-режиме имя файла и сколько успели.
                        var friendly = ImportErrors.Map(ex, unit.FilePath);
                        throw units.Count > 1
                            ? new ImportException(
                                $"{System.IO.Path.GetFileName(unit.FilePath)}: {friendly.Message} " +
                                $"Импортировано коллекций до ошибки: {created.Count}.")
                            : friendly;
                    }
                }
            });

            progress.Done();
            await progressClosed;
            ReloadAndNotify();
            if (created.Count == 1 && _mode == SidebarMode.Constructor)
                OpenCollection(req.DbId, created[0]);
            else if (created.Count > 1 && req.ParentId is int gid)
                ExpandGroup(req.DbId, gid);
            return (null, null);
        }
        catch (OperationCanceledException)
        {
            // Отмена пользователем: сбойный юнит уже удалён репозиторием, готовые остаются.
            progress.Done();
            await progressClosed;
            if (created.Count > 0) ReloadAndNotify();
            return (null, null);
        }
        catch (ImportException ex)
        {
            progress.Done();
            await progressClosed;
            if (created.Count > 0) ReloadAndNotify();
            return (req.Draft, ex.Message);
        }
        catch (Exception ex)
        {
            progress.Done();
            await progressClosed;
            if (created.Count > 0) ReloadAndNotify();
            return (req.Draft, ImportErrors.Map(ex, req.Spec.Path).Message);
        }
    }

    // Экспорт коллекций в файлы. Планировщик разворачивает заявку в юниты «один файл»
    // (обычно коллекция на файл; «в один файл» — книга с листом на коллекцию). Чтение —
    // keyset-страницами на отдельных подключениях, запись — потоковыми писателями
    // (Core/Exporters); подсчёт записей — первые 5 % слайса таблицы, строки — остальные 95 %.
    // Ошибка/отмена: недописанный файл удаляется, готовые остаются. Успех — открываем папку
    // выгрузки в проводнике; БД не менялась, сайдбар не перечитываем.
    private async Task<(ExportDraft? Prefill, string? Error)> RunExport(ExportRequest req)
    {
        // См. комментарий в RunImport: OwnerWindow вычисляется при каждом обращении.
        var owner = OwnerWindow;
        if (owner is null) return (null, null);

        var progress = new ProgressDialog();
        using var cts = new CancellationTokenSource();
        progress.EnableCancel(() => cts.Cancel());
        IProgress<(double F, string Phase, string Info)> ui =
            new Progress<(double F, string Phase, string Info)>(t =>
            {
                progress.SetProgress(t.F, t.Phase);
                progress.SetInfo(t.Info);
            });
        progress.SetProgress(0, "Подготовка…");   // фаза до показа — окно не меняет высоту
        var progressClosed = progress.ShowDialog(owner);

        var completed = 0;
        try
        {
            await Task.Run(() =>
            {
                var units = ExportPlanner.Plan(req.Spec);   // уникализация путей — здесь (трогает диск)
                var excl  = ExportPlanner.ParseColumnExclusions(req.Spec.ExcludeColumns);
                for (var u = 0; u < units.Count; u++)
                {
                    var unit = units[u];
                    var info = units.Count > 1
                        ? $"Файл {u + 1} из {units.Count}: {System.IO.Path.GetFileName(unit.FilePath)}" : "";
                    var baseF = (double)u / units.Count;
                    var slice = 1.0 / units.Count;

                    try
                    {
                        var fileCreated = false;
                        try
                        {
                            // Создание файла — первым: права/диск падают до чтения данных.
                            using var writer = unit.OpenWriter();
                            fileCreated = true;
                            for (var i = 0; i < unit.Tables.Count; i++)
                            {
                                var tbl    = unit.Tables[i];
                                var tBase  = baseF + slice * i / unit.Tables.Count;
                                var tSlice = slice / unit.Tables.Count;

                                // Юниты могут охватывать РАЗНЫЕ базы — репозиторий по коллекции.
                                var repo = RepoFor(tbl.Collection.DbId)
                                    ?? throw new ExportException(
                                        $"База данных коллекции «{tbl.Collection.CollectionName}» недоступна.");
                                // Страховка к фильтру пикера: права могли отозвать после открытия окна.
                                if (!(repo.Permissions?.Can(PermAction.ViewCollection, tbl.Collection.CollectionId) ?? true))
                                    throw new ExportException(
                                        $"Недостаточно прав: просмотр коллекции «{tbl.Collection.CollectionName}» " +
                                        "запрещён администратором базы данных.");

                                ui.Report((tBase, "Подготовка…", info));
                                var cols = repo.GetExportColumnsIsolated(
                                        tbl.Collection.CollectionId, req.Spec.IncludeHidden)
                                    .Where(c => !excl.Contains(c.Header)).ToList();
                                if (cols.Count == 0)
                                    throw new ExportException(
                                        $"Все столбцы коллекции «{tbl.Collection.CollectionName}» исключены — " +
                                        "нет данных для экспорта.");
                                var total = repo.CountRecordsIsolated(
                                    tbl.Collection.CollectionId, excludeHidden: !req.Spec.IncludeHidden);

                                writer.BeginTable(tbl.TableName, cols, !req.Spec.NoHeaderRow);
                                foreach (var row in repo.ReadExportRowsIsolated(
                                             tbl.Collection.CollectionId, cols, !req.Spec.IncludeHidden, cts.Token,
                                             new Progress<long>(n => ui.Report((
                                                 tBase + tSlice * (0.05 + 0.95 * Math.Min(1.0,
                                                     total == 0 ? 1.0 : n / (double)total)),
                                                 "Экспорт записей…", info)))))
                                    writer.WriteRow(row);
                                writer.EndTable();
                                repo.LogExportIsolated(tbl.Collection.CollectionName, total);
                            }
                            writer.Finish();
                            completed++;
                        }
                        catch
                        {
                            // Писатель уже закрыт (using) — недописанный файл удаляем; готовые
                            // остаются. Удаляем ТОЛЬКО свой файл: если упало само создание
                            // (файл появился извне между Plan и CreateNew), чужое не трогаем.
                            if (fileCreated)
                                try { File.Delete(unit.FilePath); } catch { /* best-effort */ }
                            throw;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Дружелюбный текст + в мульти-режиме имя файла и сколько успели.
                        var friendly = ExportErrors.Map(ex, unit.FilePath);
                        throw units.Count > 1
                            ? new ExportException(
                                $"{System.IO.Path.GetFileName(unit.FilePath)}: {friendly.Message} " +
                                $"Экспортировано файлов до ошибки: {completed}.")
                            : friendly;
                    }
                }
            });

            progress.Done();
            await progressClosed;
            OpenExportFolder(req.Spec.Folder);
            return (null, null);
        }
        catch (OperationCanceledException)
        {
            // Отмена пользователем: недописанный файл удалён в фоне, готовые остаются.
            progress.Done();
            await progressClosed;
            return (null, null);
        }
        catch (ExportException ex)
        {
            progress.Done();
            await progressClosed;
            return (req.Draft, ex.Message);
        }
        catch (Exception ex)
        {
            progress.Done();
            await progressClosed;
            return (req.Draft, ExportErrors.Map(ex, req.Spec.Folder).Message);
        }
    }

    // Показ результата экспорта: открываем папку в проводнике (как SettingsView открывает
    // папку данных приложения). Сбой открытия не критичен — файлы уже на диске.
    private static void OpenExportFolder(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = folder,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    private void ExpandGroup(int dbId, int groupId)
    {
        var node = FindNode(_roots, "group", dbId, groupId);
        if (node is null) return;
        ExpandAncestors(_roots, node);
        node.IsExpanded = true;
        RebuildRows();
    }

    private void OpenCollection(int dbId, int collectionId)
    {
        SelectCollection(dbId, collectionId);
        CollectionSelected?.Invoke(this, new EntityRef(dbId, collectionId));
    }

    private async Task EditGroup(int dbId, int groupId)
    {
        var repo = RepoFor(dbId);
        if (repo is null || OwnerWindow is null) return;

        var group = repo.GetAllGroups().FirstOrDefault(g => g.Id == groupId);
        if (group is null) return;

        var dlg = new GroupDialog(DbOptions(), dbId, editNode: group);
        var res = await dlg.ShowDialog<GroupDialogResult?>(OwnerWindow);
        if (res is null) return;

        // Смена БД → перенос группы со всей веткой в целевую БД быстрым путём (ATTACH + bulk, окно
        // прогресса для больших), как пункт «Расположение». Имя задаётся отдельно (быстрый путь
        // копирует его как есть) — применяем выбранное после переноса.
        if (res.DbId != dbId && RepoFor(res.DbId) is { } target)
        {
            var (gcols, grecs) = await repo.CountGroupSubtreeAsync(groupId);
            var move  = new CrossMove(dbId, repo, IsGroup: true, groupId, grecs, gcols);
            // Отказ отменяет всю правку (в т.ч. переименование) — ничего не тронуто.
            if (!await ConfirmCrossDbLinkBreak(target, [move])) return;
            var moved = await MoveNodesCrossDb(target, res.DbId, res.ParentId, [move], "Перемещение");
            if (moved is { } m)
            {
                // Имя из диалога (уникальность уже проверена диалогом) применяем на цели.
                if (target.GetAllGroups().FirstOrDefault(g => g.Id == m.Id) is { } movedGroup && movedGroup.Name != res.Name)
                {
                    movedGroup.Name = res.Name;
                    target.UpdateGroup(movedGroup);
                }
                ReloadAndNotify();
                ExpandGroup(res.DbId, m.Id);
            }
            else
            {
                ReloadAndNotify();
                if (OwnerWindow is not null)
                    await new ConfirmDialog("Не удалось перенести группу в другую базу. Данные остались в исходной базе.")
                        .ShowDialog<bool>(OwnerWindow);
            }
            return;
        }

        group.Name     = res.Name;
        group.ParentId = res.ParentId;
        repo.UpdateGroup(group);
        ReloadAndNotify();
    }

    private async Task EditCollection(int dbId, int collectionId)
    {
        var repo = RepoFor(dbId);
        if (repo is null || OwnerWindow is null) return;

        var col = repo.GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        if (col is null) return;

        var dlg = new CollectionDialog(DbOptions(), dbId, editNode: col);
        var res = await dlg.ShowDialog<CollectionDialogResult?>(OwnerWindow);
        if (res is null) return;

        // Смена БД → перенос коллекции в целевую БД быстрым путём (ATTACH + bulk, окно прогресса для
        // больших), как пункт «Расположение». Имя задаётся отдельно (быстрый путь копирует его как
        // есть) — применяем выбранное после переноса.
        if (res.DbId != dbId && RepoFor(res.DbId) is { } target)
        {
            var move  = new CrossMove(dbId, repo, IsGroup: false, collectionId, await repo.CountRecordsAsync(collectionId), Collections: 1);
            // Отказ отменяет всю правку (в т.ч. переименование) — ничего не тронуто.
            if (!await ConfirmCrossDbLinkBreak(target, [move])) return;
            var moved = await MoveNodesCrossDb(target, res.DbId, res.GroupId, [move], "Перемещение");
            if (moved is { } m)
            {
                // Имя из диалога (уникальность уже проверена диалогом) применяем на цели.
                if (target.GetAllCollections().FirstOrDefault(c => c.Id == m.Id) is { } movedCol && movedCol.Name != res.Name)
                {
                    movedCol.Name = res.Name;
                    target.UpdateCollection(movedCol);
                }
                ReloadAndNotify();
                if (_mode == SidebarMode.Constructor) OpenCollection(res.DbId, m.Id);
            }
            else
            {
                ReloadAndNotify();   // вернёт исходное состояние в дерево
                if (OwnerWindow is not null)
                    await new ConfirmDialog("Не удалось перенести коллекцию в другую базу. Данные остались в исходной базе.")
                        .ShowDialog<bool>(OwnerWindow);
            }
            return;
        }

        col.Name    = res.Name;
        col.GroupId = res.GroupId;
        repo.UpdateCollection(col);
        ReloadAndNotify();
    }

    // Порог записей (суммарно по выделению) для дублирования/удаления/переноса: меньше — операция
    // идёт мгновенно синхронно без окна; больше — окно прогресса с % (как импорт) и работа в фоне.
    private const int BulkProgressThreshold = 20000;

    // Записей в узле и сколько коллекций он несёт: коллекция — её записи и 1; группа — сумма по всей
    // ветке (подгруппы + коллекции) и число коллекций в ветке. Для порога, % и текста индикатора.
    // Асинхронно на ОТДЕЛЬНОМ подключении: COUNT по коллекции в 10 млн — сотни мс, на UI-потоке это
    // был фриз между кликом по меню и окном прогресса (умножался при мультивыборе).
    private async Task<(long Records, int Collections)> CountNodeAsync(SidebarNode n)
    {
        var r = RepoFor(n.DbId);
        if (r is null || !n.Id.HasValue || n.IsDatabase) return (0, 0);
        if (n.IsGroup) { var (c, rec) = await r.CountGroupSubtreeAsync(n.Id.Value); return (rec, c); }
        return (await r.CountRecordsAsync(n.Id.Value), 1);
    }

    // Текст индикатора: для операций с группами — «Коллекций: X · Записей: Y»; иначе только записи.
    private static string BulkInfo(int collections, long records, bool withCollections) =>
        withCollections ? $"Коллекций: {collections} · Записей: {records}" : $"Количество записей: {records}";

    // Дублирование через контекстное меню: если клик по узлу из мультивыделения — дублируем ВЕСЬ
    // набор (коллекции И группы со всей веткой); иначе только этот узел. Большие (суммарно много
    // записей) — с окном прогресса и копированием в фоне на отдельных подключениях; малые — мгновенно.
    private async void DuplicateSelectionOrNode(SidebarNode node)
    {
        // Репозитории резолвим здесь, на UI-потоке (общий список _dbs не трогаем из Task.Run);
        // счётчики — в фоне на отдельных подключениях (CountNodeAsync), UI не фризится.
        var sel     = _selectedNodes.Contains(node) ? _selectedNodes.ToList() : [node];
        var targets = new List<(int DbId, ConstructorRepository Repo, bool IsGroup, int Id, long Records)>();
        long total = 0; int totalCollections = 0; bool anyGroup = false;
        foreach (var n in sel.Where(n => n.Id.HasValue && !n.IsDatabase))
        {
            var r = RepoFor(n.DbId);
            if (r is null) continue;
            var (rec, cols) = await CountNodeAsync(n);
            targets.Add((n.DbId, r, n.IsGroup, n.Id!.Value, rec));
            total += rec; totalCollections += cols; anyGroup |= n.IsGroup;
        }
        if (targets.Count == 0) return;
        var single = targets.Count == 1;

        // Малый объём — мгновенно, синхронно (без окна): bulk-копия быстрая.
        if (total <= BulkProgressThreshold)
        {
            (int DbId, int Id, bool IsGroup)? made = null;
            foreach (var t in targets)
            {
                if (t.IsGroup) made = (t.DbId, t.Repo.DuplicateGroupCore(t.Id), true);
                else { var copy = t.Repo.DuplicateCollection(t.Id); if (copy.Id is int nid) made = (t.DbId, nid, false); }
            }
            ReloadAndNotify();
            RevealDuplicate(single, made);
            return;
        }

        // Большой объём — окно прогресса. Копии идут ПО ОЧЕРЕДИ в фоне на отдельных подключениях
        // (DuplicateCollectionIsolated/DuplicateGroupIsolated) — не трогаем общий Context с фона.
        if (OwnerWindow is null) return;
        var dlg = new ProgressDialog { Title = "Дублирование" };
        dlg.SetInfo(BulkInfo(totalCollections, total, anyGroup));
        // Фаза — ДО показа, тем же текстом, что даст первый процент: иначе жирная строка появлялась
        // с первым колбэком, окно с SizeToContent меняло высоту и выглядело «вторым окном».
        dlg.SetProgress(0, targets.Count > 1
            ? $"Дублирование… (1/{targets.Count})"
            : targets[0].IsGroup ? "Дублирование группы…" : "Дублирование коллекции…");
        var dlgClosed = dlg.ShowDialog(OwnerWindow);

        (int DbId, int Id, bool IsGroup)? madeBig = null;
        var lastPct = -1;
        try
        {
            await Task.Run(() =>
            {
                long basis = 0;
                for (var i = 0; i < targets.Count; i++)
                {
                    var t    = targets[i];
                    var b    = basis;
                    var step = i + 1;
                    var text = targets.Count > 1
                        ? $"Дублирование… ({step}/{targets.Count})"
                        : t.IsGroup ? "Дублирование группы…" : "Дублирование коллекции…";
                    IProgress<int> p = new IntProgress(copied =>
                    {
                        var pct = total == 0 ? 100 : (int)((b + copied) * 100 / total);
                        if (pct == lastPct) return;
                        lastPct = pct;
                        Dispatcher.UIThread.Post(() => dlg.SetProgress(pct / 100.0, text));
                    });
                    if (t.IsGroup) madeBig = (t.DbId, t.Repo.DuplicateGroupIsolated(t.Id, p), true);
                    else { var nid = t.Repo.DuplicateCollectionIsolated(t.Id, p); if (nid != 0) madeBig = (t.DbId, nid, false); }
                    basis += t.Records;
                }
            });
        }
        catch (Exception ex)
        {
            // Ошибку фона проглатываем: узел либо скопирован, либо нет — UI не валим.
            Log.Warning(ex, "Сбой фонового копирования узла (дублирование)");
        }

        dlg.Done();
        await dlgClosed;

        ReloadAndNotify();
        RevealDuplicate(single, madeBig);
    }

    // Показать результат единичного дубля: коллекцию открыть, группу — раскрыть (в конструкторе).
    private void RevealDuplicate(bool single, (int DbId, int Id, bool IsGroup)? made)
    {
        if (_mode != SidebarMode.Constructor || !single || made is not { } m) return;
        if (m.IsGroup) ExpandGroup(m.DbId, m.Id); else OpenCollection(m.DbId, m.Id);
    }

    // Адаптер IProgress<int> для сырого прогресса копии (вызывается с фонового потока, сам
    // маршалит на UI через Dispatcher) — Progress<T> здесь не годится (создаётся в Task.Run,
    // захватил бы фоновый контекст синхронизации).
    private sealed class IntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    // Одна цель кросс-БД переноса: репозиторий-ИСТОЧНИК, что переносим (группа/коллекция), его id,
    // и счётчики (записей в ветке/коллекции и число коллекций — для порога, % и текста индикатора).
    private sealed record CrossMove(int SrcDbId, ConstructorRepository SrcRepo, bool IsGroup, int Id, long Records, int Collections);

    // Межбазовый перенос = новые id у записей → ВХОДЯЩИЕ связи на переносимое обрываются (за объектом
    // не следуют). Для КОЛЛЕКЦИИ теряются лишь ВНЕШНИЕ входящие (само-связи внутри переписываются);
    // для ГРУППЫ (v1) отбрасываются ВСЕ связи ветки. Внутрибазовый перенос id не меняет — потерь нет,
    // туда не зовём. Возвращает false, если пользователь отказался (переносить нельзя).
    //
    // ВАЖНО: звать перед КАЖДЫМ вызовом MoveNodesCrossDb и ДО любых изменений. Точек входа в
    // межбазовый перенос три — «Расположение» (RelocateSelectionOrNode), «Изменение» коллекции
    // (EditCollection) и «Изменение» группы (EditGroup); раньше проверка стояла только в первой, и
    // смена базы через «Изменение» рвала связи молча.
    private async Task<bool> ConfirmCrossDbLinkBreak(ConstructorRepository targetRepo, IReadOnlyList<CrossMove> crossDb)
    {
        if (crossDb.Count == 0 || OwnerWindow is null) return true;

        // Два пути НЕ копируют рёбра вовсе: любой перенос с участием PostgreSQL (generic вместо ATTACH)
        // и перенос ВЕТКИ ГРУППЫ даже на SQLite (v1). Там теряются и связи ВНУТРИ переносимого, и
        // исходящие наружу — обычная проверка смотрит только внешние ВХОДЯЩИЕ и их не видит.
        var pg = targetRepo.IsPostgres || crossDb.Any(t => t.SrcRepo.IsPostgres);

        var hasLinks   = false;
        string? severe = null;   // непустое → рёбра не переедут вовсе (текст строже)
        foreach (var t in crossDb)
        {
            var dropsAll = pg || t.IsGroup;
            bool hit;
            if (dropsAll)
            {
                IReadOnlyCollection<int> cols = t.IsGroup ? t.SrcRepo.CollectionIdsInGroup(t.Id) : [t.Id];
                hit = RelationDeleteImpact.ForCrossMoveDroppingAllLinks(t.SrcRepo, t.SrcDbId, cols);
            }
            else hit = RelationDeleteImpact.ForCollectionExternalCrossMove(t.SrcRepo, t.SrcDbId, t.Id);
            if (!hit) continue;

            hasLinks = true;
            if (!dropsAll) continue;
            severe = pg ? "Перенос с участием PostgreSQL" : "Перенос ветки группы между базами";
            break;   // строгий текст уже определён, остальные узлы не важны
        }
        if (!hasLinks) return true;

        var warn = new ConfirmDialog(
            severe is not null
                ? $"{severe} связи НЕ переносит: будут потеряны и связи ВНУТРИ переносимого, и ссылки на него. Продолжить?"
                : "Межбазовый перенос оборвёт связи, ссылающиеся на переносимое (станут «связи нет»). Продолжить?",
            confirmText: "Перенести");
        return await warn.ShowDialog<bool>(OwnerWindow) == true;
    }

    // Перенести набор узлов в ДРУГУЮ БД быстрым путём (Move*Isolated: ATTACH + bulk INSERT…SELECT на
    // отдельных подключениях, не трогая общий Context). targetLocation — расположение в целевой БД:
    // для коллекции это её группа, для группы — родитель. Малый суммарный объём (≤ порога) — синхронно
    // мгновенно без окна; больше — окно прогресса (title) + перенос по очереди в фоне с общим %. Ошибки
    // переноса не валят UI (как дубль/удаление). Возвращает (целевая БД, новый id, IsGroup) последнего
    // успешно перенесённого узла (для reveal) либо null, если ничего не перенеслось/была ошибка.
    private async Task<(int DbId, int Id, bool IsGroup)?> MoveNodesCrossDb(
        ConstructorRepository targetRepo, int targetDbId, int? targetLocation,
        IReadOnlyList<CrossMove> crossDb, string title)
    {
        if (crossDb.Count == 0 || OwnerWindow is null) return null;

        long total       = crossDb.Sum(t => t.Records);
        int  collections = crossDb.Sum(t => t.Collections);
        bool anyGroup    = crossDb.Any(t => t.IsGroup);

        int MoveOne(CrossMove t, IProgress<int>? p) => t.IsGroup
            ? targetRepo.MoveGroupIsolated(t.SrcRepo.DbPath, t.Id, targetLocation, p, sourceDbId: t.SrcDbId, targetDbId: targetDbId)
            : targetRepo.MoveCollectionIsolated(t.SrcRepo.DbPath, t.Id, targetLocation, p, sourceDbId: t.SrcDbId, targetDbId: targetDbId);

        (int DbId, int Id, bool IsGroup)? reveal = null;

        // Малый объём — перенос синхронно, мгновенно (bulk-копия быстрая).
        if (total <= BulkProgressThreshold)
        {
            try
            {
                foreach (var t in crossDb)
                {
                    var nid = MoveOne(t, null);
                    if (nid != 0) reveal = (targetDbId, nid, t.IsGroup);
                }
            }
            catch { /* узел либо перенесён, либо нет — UI не валим */ }
            return reveal;
        }

        // Большой объём — окно прогресса. Перенос ПО ОЧЕРЕДИ в фоне на отдельных подключениях;
        // проценты — общий прогресс по всем целям.
        var dlg = new ProgressDialog { Title = title };
        dlg.SetInfo(BulkInfo(collections, total, anyGroup));
        // Фаза — ДО показа (см. дубль): окно не меняет высоту с первым процентом.
        dlg.SetProgress(0, crossDb.Count > 1
            ? $"{title}… (1/{crossDb.Count})"
            : crossDb[0].IsGroup ? $"{title} группы…" : $"{title} коллекции…");
        var closed = dlg.ShowDialog(OwnerWindow);

        // Прогресс покрывает И копию, И удаление исходника: на каждую цель 2× записей (копия — первая
        // половина 0..Records, удаление исходной БД — вторая Records..2·Records). Иначе бар замирал на
        // 100% на всё время удаления (каскад записей/значений — минуты на больших коллекциях/ветках).
        long workTotal = 2 * total;
        var lastPct = -1;
        var lastText = "";
        try
        {
            await Task.Run(() =>
            {
                long basis = 0;
                for (var i = 0; i < crossDb.Count; i++)
                {
                    var t    = crossDb[i];
                    var b    = basis;
                    var step = i + 1;
                    var moving = crossDb.Count > 1
                        ? $"{title}… ({step}/{crossDb.Count})"
                        : t.IsGroup ? $"{title} группы…" : $"{title} коллекции…";
                    var deleting = crossDb.Count > 1
                        ? $"Удаление из исходной базы… ({step}/{crossDb.Count})"
                        : "Удаление из исходной базы…";
                    IProgress<int> p = new IntProgress(done =>
                    {
                        // done: 0..Records — копия, Records..2·Records — удаление исходника.
                        var text = done < t.Records ? moving : deleting;
                        var pct  = workTotal == 0 ? 100 : (int)((b + done) * 100 / workTotal);
                        if (pct == lastPct && text == lastText) return;   // фаза могла смениться при том же %
                        lastPct = pct; lastText = text;
                        Dispatcher.UIThread.Post(() => dlg.SetProgress(pct / 100.0, text));
                    });
                    var nid = MoveOne(t, p);
                    if (nid != 0) reveal = (targetDbId, nid, t.IsGroup);
                    basis += 2 * t.Records;
                }
            });
        }
        catch (Exception ex)
        {
            // Ошибку фона проглатываем: узел либо перенесён, либо нет — UI не валим.
            Log.Warning(ex, "Сбой фонового переноса узла");
        }

        dlg.Done();
        await closed;
        return reveal;
    }

    // «Расположение» через контекстное меню: если клик по узлу из мультивыделения — перемещаем ВЕСЬ
    // набор (коллекции И группы со всей веткой); иначе только этот узел. Окно (как «Изменение», но без
    // названия) выбирает целевую БД и расположение (для коллекции — её группа, для группы — родитель).
    // В той же БД — мгновенно (смена GroupId/ParentId, данные на месте); в другую БД — bulk-перенос
    // (как дубль): малый объём синхронно, большой — окно прогресса и фон (Move*Isolated).
    private async void RelocateSelectionOrNode(SidebarNode node)
    {
        if (OwnerWindow is null) return;

        var sel    = _selectedNodes.Contains(node) ? _selectedNodes.ToList() : [node];
        var picked = sel.Where(n => n.Id.HasValue && !n.IsDatabase).ToList();
        if (picked.Count == 0) return;

        // Префилл: для группы — её родитель, для коллекции — её группа (узел, по которому кликнули).
        int? prefill = node.IsGroup
            ? RepoFor(node.DbId)?.GetAllGroups().FirstOrDefault(g => g.Id == node.Id)?.ParentId
            : RepoFor(node.DbId)?.GetAllCollections().FirstOrDefault(c => c.Id == node.Id)?.GroupId;

        // Нельзя вложить перемещаемую группу в саму себя/потомка — исключаем их ветки из назначения.
        var exclude = picked.Where(n => n.IsGroup).Select(n => n.Id!.Value).ToHashSet();

        var dlg = new LocationDialog(DbOptions(), node.DbId, prefill, exclude.Count > 0 ? exclude : null);
        var res = await dlg.ShowDialog<LocationDialogResult?>(OwnerWindow);
        if (res is null) return;

        var targetRepo = RepoFor(res.DbId);
        if (targetRepo is null) return;

        // Разделяем: та же БД (мгновенно: метаданные) vs перенос в другую БД (bulk + прогресс).
        var sameDb  = new List<(ConstructorRepository Repo, bool IsGroup, int Id)>();
        var crossDb = new List<CrossMove>();
        foreach (var n in picked)
        {
            var r = RepoFor(n.DbId);
            if (r is null) continue;
            if (n.DbId == res.DbId) sameDb.Add((r, n.IsGroup, n.Id!.Value));
            else
            {
                var (rec, cols) = await CountNodeAsync(n);
                crossDb.Add(new CrossMove(n.DbId, r, n.IsGroup, n.Id!.Value, rec, cols));
            }
        }

        // Спрашиваем ДО переносов внутри той же БД ниже: отказ должен оставить всё как было.
        if (!await ConfirmCrossDbLinkBreak(targetRepo, crossDb)) return;

        // Та же БД — только метаданные: группа меняет ParentId, коллекция — GroupId (ветка/записи на
        // месте). Если в целевой группе/уровне уже есть одноимённый узел — даём «(копия)» (как дубль).
        foreach (var (r, isGroup, id) in sameDb)
        {
            if (isGroup)
            {
                var g = r.GetAllGroups().FirstOrDefault(x => x.Id == id);
                if (g is null) continue;
                g.ParentId = res.GroupId;
                g.Name     = r.UniqueGroupName(g.Name, res.GroupId, excludeId: id);
                r.UpdateGroup(g);
            }
            else
            {
                var col = r.GetAllCollections().FirstOrDefault(x => x.Id == id);
                if (col is null) continue;
                col.GroupId = res.GroupId;
                col.Name    = r.UniqueCollectionName(col.Name, res.GroupId, excludeId: id);
                r.UpdateCollection(col);
            }
        }

        var single = picked.Count == 1;
        // Что показать после: единственный перемещённый узел (коллекцию открыть, группу раскрыть).
        (int DbId, int Id, bool IsGroup)? reveal =
            single && sameDb.Count == 1 ? (res.DbId, sameDb[0].Id, sameDb[0].IsGroup) : null;

        if (crossDb.Count > 0)
        {
            var moved = await MoveNodesCrossDb(targetRepo, res.DbId, res.GroupId, crossDb, "Перемещение");
            if (single && moved is { } m) reveal = m;
        }

        _selectedNodes.Clear();
        ReloadAndNotify();
        if (_mode == SidebarMode.Constructor && reveal is { } rv)
        {
            if (rv.IsGroup) ExpandGroup(rv.DbId, rv.Id); else OpenCollection(rv.DbId, rv.Id);
        }
    }

    // Удаление через контекстное меню: если клик по узлу из мультивыделения — удаляем
    // весь набор; иначе только этот узел.
    private Task DeleteSelectionOrNode(SidebarNode node)
    {
        List<SidebarNode> targets = _selectedNodes.Contains(node) ? _selectedNodes.ToList() : [node];
        return DeleteNodes(targets);
    }

    private async Task DeleteNodes(IReadOnlyCollection<SidebarNode> nodes)
    {
        if (OwnerWindow is null) return;

        // Корни-БД не удаляются отсюда (управление БД — в настройках).
        var groups = nodes.Where(n => n.IsGroup  && n.Id.HasValue).ToList();
        var colls  = nodes.Where(n => !n.IsGroup && !n.IsDatabase && n.Id.HasValue).ToList();
        if (groups.Count == 0 && colls.Count == 0) return;

        // Есть ли входящие связи, которые оборвёт удаление (best-effort: внутрибазовые + межбазовые из
        // ОТКРЫТЫХ баз; из закрытых — не проверяем, вычистятся лениво). Разворачиваем группы в их ветки
        // и объединяем с одиночными коллекциями по базе (одну коллекцию не проверяем дважды), обрываемся
        // на первой найденной связи.
        var hasLinks = false;
        foreach (var byDb in groups.Concat(colls).Where(n => n.Id.HasValue && RepoOf(n) is not null)
                                    .GroupBy(n => n.DbId))
        {
            if (RepoOf(byDb.First()) is not { } r) continue;
            var cols = new HashSet<int>();
            foreach (var n in byDb)
                if (n.IsGroup) cols.UnionWith(r.CollectionIdsInGroup(n.Id!.Value));
                else           cols.Add(n.Id!.Value);
            if (RelationDeleteImpact.ForCollections(r, byDb.Key, cols)) { hasLinks = true; break; }
        }

        var confirm = new ConfirmDialog(BuildDeleteMessage(groups.Count, colls.Count, hasLinks));
        if (await confirm.ShowDialog<bool>(OwnerWindow) != true) return;

        // Цели: группа (удаляем ВСЮ ветку) или коллекция. Репозитории резолвим здесь, на UI-потоке
        // (общий список не трогаем из Task.Run); счётчики (для группы — число коллекций и записей по
        // всей ветке) — в фоне на отдельных подключениях (CountNodeAsync). Порог и % — по суммарному
        // числу записей; в индикаторе при группах показываем и число коллекций.
        var targets = new List<(ConstructorRepository Repo, bool IsGroup, int Id, long Records)>();
        long total = 0; int totalCollections = 0;
        foreach (var n in groups.Concat(colls))
        {
            var r = RepoOf(n);
            if (r is null) continue;
            var (rec, cols) = await CountNodeAsync(n);
            targets.Add((r, n.IsGroup, n.Id!.Value, rec));
            total += rec; totalCollections += cols;
        }

        if (total <= BulkProgressThreshold)
        {
            // Малый объём — мгновенно, синхронно (без окна): каскадный DELETE быстрый.
            // Файл занят чужим писателем/пропал — тост (DbErrors), UI не валим.
            try
            {
                foreach (var t in targets)
                    if (t.IsGroup) t.Repo.DeleteGroup(t.Id); else t.Repo.DeleteCollection(t.Id);
            }
            catch (Exception ex) when (DbErrors.Handle(ex, targets.FirstOrDefault().Repo?.DbPath)) { }
        }
        else
        {
            // Большой объём — окно прогресса (как дубль/импорт). Удаление ПО ОЧЕРЕДИ в фоне на
            // отдельных подключениях (Delete*Isolated), проценты — общий прогресс по всем целям.
            var dlg = new ProgressDialog { Title = "Удаление" };
            dlg.SetInfo(BulkInfo(totalCollections, total, groups.Count > 0));
            // Фаза — ДО показа (см. дубль): окно не меняет высоту с первым процентом.
            dlg.SetProgress(0, targets.Count > 1
                ? $"Удаление… (1/{targets.Count})"
                : targets[0].IsGroup ? "Удаление группы…" : "Удаление коллекции…");
            var dlgClosed = dlg.ShowDialog(OwnerWindow);

            var lastPct = -1;
            try
            {
                await Task.Run(() =>
                {
                    long basis = 0;
                    for (var i = 0; i < targets.Count; i++)
                    {
                        var t    = targets[i];
                        var b    = basis;
                        var step = i + 1;
                        var text = targets.Count > 1
                            ? $"Удаление… ({step}/{targets.Count})"
                            : t.IsGroup ? "Удаление группы…" : "Удаление коллекции…";
                        IProgress<int> p = new IntProgress(done =>
                        {
                            var pct = total == 0 ? 100 : (int)((b + done) * 100 / total);
                            if (pct == lastPct) return;
                            lastPct = pct;
                            Dispatcher.UIThread.Post(() => dlg.SetProgress(pct / 100.0, text));
                        });
                        if (t.IsGroup) t.Repo.DeleteGroupIsolated(t.Id, p); else t.Repo.DeleteCollectionIsolated(t.Id, p);
                        basis += t.Records;
                    }
                });
            }
            catch (Exception ex)
            {
                // Ошибку фона проглатываем: узел либо удалён, либо нет — UI не валим.
                // Известные ошибки БД (занята/пропала) дают тост, сигнал монитору и лог внутри
                // Handle; непредвиденное (Handle вернёт false) логируем здесь, иначе — тишина.
                if (!DbErrors.Handle(ex, targets.FirstOrDefault().Repo?.DbPath))
                    Log.Warning(ex, "Сбой фонового удаления узла");
            }

            dlg.Done();
            await dlgClosed;
        }

        _selectedNodes.Clear();
        ReloadAndNotify();   // RequestRefresh обновит и конструктор, и календарь
        var firstColl = colls.FirstOrDefault();
        if (firstColl is not null) CollectionDeleted?.Invoke(this, firstColl.Id!.Value);
    }

    // Текст подтверждения с подсчётом: только группы / только коллекции / и то и другое.
    private static string BuildDeleteMessage(int groups, int colls, bool hasLinks)
    {
        var parts = new List<string>();
        if (groups > 0) parts.Add($"{groups} {GroupsPlural(groups)}");
        if (colls  > 0) parts.Add($"{colls} {CollectionsPlural(colls)}");
        var warn = groups > 0
            ? "Вложенные группы и коллекции также будут удалены без возможности восстановления."
            : "Данные будут удалены без возможности восстановления.";
        var linkWarn = hasLinks ? " Также будут удалены связи, ссылающиеся на удаляемое." : "";
        return $"Вы удаляете {string.Join(" и ", parts)}. {warn}{linkWarn} Подтвердить?";
    }

    // Винительный падеж: 1 группу, 2–4 группы, 5+ групп (11–14 → групп).
    private static string GroupsPlural(int n)
    {
        var m100 = n % 100; var m10 = n % 10;
        if (m100 is >= 11 and <= 14) return "групп";
        return m10 switch { 1 => "группу", >= 2 and <= 4 => "группы", _ => "групп" };
    }

    // 1 коллекцию, 2–4 коллекции, 5+ коллекций (11–14 → коллекций).
    private static string CollectionsPlural(int n)
    {
        var m100 = n % 100; var m10 = n % 10;
        if (m100 is >= 11 and <= 14) return "коллекций";
        return m10 switch { 1 => "коллекцию", >= 2 and <= 4 => "коллекции", _ => "коллекций" };
    }

    private void ReloadAndNotify()
    {
        Refresh();
        RequestRefresh?.Invoke(this, EventArgs.Empty);
        if (ChecksMode)
        {
            CalendarsChanged?.Invoke(this, EventArgs.Empty);
            ActiveIdsChanged?.Invoke(this, GetActiveIds());
        }
    }

    private enum SortState { None, Asc, Desc }
}

// ── Немонтированная БД фоновой очереди (для узла-заглушки) ────────────────────
public sealed class PendingDb(int dbId, string name, string status, bool failed)
{
    public int    DbId   { get; } = dbId;
    public string Name   { get; } = name;
    public string Status { get; set; } = status;
    public bool   Failed { get; set; } = failed;
}

// ── Узел дерева ────────────────────────────────────────────────────────────────
public class SidebarNode
{
    public string NodeType { get; init; } = "";  // "database" | "group" | "collection"
    public int     DbId     { get; init; }        // владелец-БД (для мульти-маунта)
    public int?    Id       { get; init; }
    public string  Name     { get; init; } = "";
    public string  Color    { get; init; } = "#007AFF";
    public bool    IsLocked { get; init; }        // база без допуска: замок, без содержимого
    public bool    IsPending { get; init; }       // база ещё не смонтирована (очередь/ошибка)
    public string? Status    { get; init; }       // статусная строка заглушки
    public bool    Failed    { get; init; }       // статус — ошибка (красный)
    public bool    IsExpanded { get; set; }
    public bool    IsGroup    => NodeType == "group";
    public bool    IsDatabase => NodeType == "database";

    public ObservableCollection<SidebarNode> Children { get; } = [];
}

// ── Плоская строка для отрисовки ──────────────────────────────────────────────
public partial class SidebarRow : ObservableObject
{
    private const double IndentStep = 22;

    // Кисти из общей палитры Colors.axaml (запасной hex — на случай отсутствия ресурса).
    private static readonly IBrush Accent      = Brush("BrushAccent",        "#007AFF");
    private static readonly IBrush AccentLight = Brush("BrushAccentLight",   "#E5F0FF");
    private static readonly IBrush Hover       = Brush("BrushSecondaryBg",   "#F2F2F7");
    private static readonly IBrush Secondary   = Brush("BrushSecondaryText", "#8E8E93");
    private static readonly IBrush Primary     = Brush("BrushPrimaryText",   "#1C1C1E");
    private static readonly IBrush Red         = Brush("BrushRed",           "#FF3B30");

    // Геометрия иконки из общего словаря Assets/Styles/Icons.axaml по ключу x:Key.
    private static Geometry? Icon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g ? g : null;

    // Кисть из общей палитры Colors.axaml по ключу x:Key.
    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    public SidebarNode Node { get; }
    public int         Depth { get; }
    public SidebarMode Mode { get; }
    // Строка дерева — одна строка: показываем первую строку имени (TitleText). Полное имя
    // живёт в Node.Name — по нему идут поиск, сортировка и переименование.
    public string      Name => TitleText.FirstLine(Node.Name);

    [ObservableProperty] private bool _hovered;
    [ObservableProperty] private bool _checked;
    [ObservableProperty] private bool _selected;

    // Ширина, ОДИНАКОВАЯ у всех строк списка (её считает SidebarTree.MeasureRowWidth).
    // Нужна из-за виртуализации: экстент горизонтальной прокрутки панель считает по
    // РЕАЛИЗОВАННЫМ строкам, и без общей ширины полоса дёргалась бы при вертикальной
    // прокрутке — как было в списке значений фильтра (см. FilterValueMetrics).
    // Заодно подсветка строки идёт во всю ширину содержимого, а не до края видимой части.
    [ObservableProperty] private double _minRowWidth;

    // Статусная строка заглушки очереди («в очереди…» / «Обновление · 45% · ≈ 2 мин»):
    // обновляется живьём (раз в полсекунды при миграции) без перестройки дерева.
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool    _statusFailed;

    public SidebarRow(SidebarNode node, int depth, SidebarMode mode, bool isChecked, bool selected)
    {
        Node     = node;
        Depth    = depth;
        Mode     = mode;
        _checked = isChecked;
        _selected = selected;
        _status       = node.Status;
        _statusFailed = node.Failed;
    }

    public void SetHovered(bool v) => Hovered = v;

    public void SetStatus(string status, bool failed)
    {
        Status       = status;
        StatusFailed = failed;
    }

    public bool   HasStatus   => !string.IsNullOrEmpty(Status);
    public IBrush StatusBrush => StatusFailed ? Red : Secondary;

    // Строка повторяет СТРУКТУРУ строки таблицы: контент = высота ячейки (RowHeightStandard = 36),
    // а линия-разделитель ДОБАВЛЯЕТСЯ снизу отдельным элементом толщиной SeparatorThickness (тот же
    // зум-компенсированный токен, что у линий сетки таблицы = ровно 1 физ. пиксель). Итог: шаг строки
    // данных = 36 + линия, ровно как у DataGrid (RowHeight задаёт только ячейки, линия сетки — отдельная
    // Auto-строка сверх них), поэтому белые линии сайдбара садятся на линии сетки при ЛЮБОМ зуме.
    // ПЕРВАЯ строка сайдбара стоит на месте ШАПКИ колонок (высота ровно 36, линия НЕ добавляется).
    private bool _isFirstRow;
    public bool IsFirstRow
    {
        get => _isFirstRow;
        set { if (_isFirstRow == value) return; _isFirstRow = value;
              OnPropertyChanged(nameof(LineGridRow)); OnPropertyChanged(nameof(LineRowHeight)); }
    }
    // Контент строки = высота ячейки таблицы (RowHeightStandard = 36). Заглушки очереди со статусом
    // растут: NaN → работает MinHeight.
    public double RowContentHeight => HasStatus ? double.NaN : CellHeight;
    // Линия-разделитель (толщина = компенсированный SeparatorThickness = 1 физ. пиксель, как линия сетки
    // DataGrid). У строк ДАННЫХ она в своей Auto-строке (Row 1) → ДОБАВЛЯЕТСЯ к высоте: 36 + линия, ровно
    // как строка данных таблицы. У ПЕРВОЙ строки (стоит на месте ШАПКИ колонок) строка линии схлопнута в 0,
    // а линия лежит ПОВЕРХ контента (Row 0, снизу) → высота ровно 36 (как шапка), но линия видна.
    public int       LineGridRow  => IsFirstRow ? 0 : 1;
    public GridLength LineRowHeight => IsFirstRow ? new GridLength(0) : GridLength.Auto;

    private static double CellHeight =>
        Application.Current!.TryFindResource("RowHeightStandard", out var v) && v is double d ? d : 36;

    // Раскрываемые узлы — группы и корни-БД (запертая и немонтированная не раскрываются).
    private bool Expandable => Node.IsGroup || (Node.IsDatabase && !Node.IsLocked && !Node.IsPending);

    public Thickness IndentMargin => new(Depth * IndentStep, 0, 0, 0);
    public double    ChevronSlot  => Expandable ? IndentStep : 0;

    // Чекбокс есть только в режимах календаря/задач и только у коллекций.
    public double CheckboxSlot => Mode is SidebarMode.Calendar or SidebarMode.Tasks && !Node.IsGroup && !Node.IsDatabase ? IndentStep : 0;
    public bool   ShowCheckbox => Mode is SidebarMode.Calendar or SidebarMode.Tasks && !Node.IsGroup && !Node.IsDatabase;

    public bool      ShowChevron     => Expandable;
    public Geometry? ChevronGeometry => Node.IsExpanded ? Icon("IconChevronDown") : Icon("IconChevronRight");
    public Geometry? IconGeometry    => Node.IsLocked ? Icon("IconLock") : Node.IsDatabase ? Icon("IconDatabase") : Node.IsGroup ? Icon("IconFolder") : Icon("IconCollection");

    // «Без цвета» → серый: единое правило EventColors, кисти кешируются в ColorTools
    // (свойство читается на каждой перерисовке строки дерева).
    private IBrush ColorBrush => ColorTools.Solid(EventColors.Display(Node.Color)) ?? Brushes.Transparent;

    public IBrush CheckFill   => Checked ? ColorBrush : Brushes.Transparent;
    public IBrush CheckBorder => ColorBrush;

    public IBrush IconBrush => Selected ? Accent : Secondary;
    // Имя немонтированной базы приглушено — узел «неживой», пока очередь её не догрузит.
    public IBrush NameBrush => Node.IsPending ? Secondary : Selected ? Accent : Primary;

    public IBrush RowBackground =>
        Selected ? AccentLight :
        Hovered  ? Hover       : Brushes.Transparent;

    partial void OnHoveredChanged(bool value)  => OnPropertyChanged(nameof(RowBackground));
    partial void OnStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(RowContentHeight));   // появился/исчез статус → контент растёт/зажимается
    }
    partial void OnStatusFailedChanged(bool value)  => OnPropertyChanged(nameof(StatusBrush));
    partial void OnCheckedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckFill));
        OnPropertyChanged(nameof(CheckBorder));
    }
    partial void OnSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(IconBrush));
        OnPropertyChanged(nameof(NameBrush));
    }
}
