using System.Collections;
using System.Collections.Specialized;
using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.Tasks;

/// <summary>
/// Виртуальный список раздела «Задачи»: строка = заголовок коллекции, карточка задачи или
/// строка «+N ещё». Реализует <see cref="IList"/>, поэтому полоса прокрутки соответствует
/// ПОЛНОМУ числу задач всех отмеченных коллекций — прыгнуть можно в любое место, — а сами
/// карточки не материализуются: список знает лишь РАЗМЕРЫ групп
/// (<see cref="ConstructorRepository.GetTaskCollectionStats"/>, считаются в фоне до его
/// создания), а задачи подтягивает страницами по требованию.
///
/// Прямой аналог «Журнала» календаря (<c>JournalVirtualList</c>) с одним упрощением: там день
/// собирается из НЕСКОЛЬКИХ баз и внутри дня их приходится сливать по времени (посекундные
/// карты, слитые дни, ~200 строк машинерии). Здесь группа — это пара (база, коллекция), то
/// есть всегда ровно один источник, и слияние не нужно вовсе.
///
/// Порядок внутри группы — тот же, что в памяти (срок ↑, «без даты» в конец, тай-брейк по Id);
/// как он собирается двумя потоками по индексам, описано в
/// <c>ConstructorRepository.TasksPaging.cs</c>. Совпадение режимов закреплено тестами
/// <c>TaskPagingTests.Pages_reproduce_the_in_memory_order_exactly</c>.
/// </summary>
internal sealed class TaskVirtualList : IList<object>, IList, INotifyCollectionChanged
{
    private const int PageSize = 200;
    private const int MaxPages = 40;    // 40 × 200 = 8000 строк в кеше

    /// <summary>Группа списка: коллекция, её размеры и место в плоской нумерации строк.</summary>
    internal sealed class Entry
    {
        public required TaskGroup Group;
        public required ConstructorRepository Repo;
        /// <summary>Форма коллекции — по ней репозиторий выбирает путь чтения.</summary>
        public required ConstructorRepository.TaskCollectionStats Stats;
        /// <summary>Вид раздела (фильтр + поиск), в котором собрана группа.</summary>
        public required ConstructorRepository.TaskView View;
        public int  Dated;      // длина первого потока («со сроком») В ЭТОМ виде
        public int  Tasks;      // всего задач группы В ЭТОМ виде
        public bool Collapsed;
        public int  FirstRow;   // индекс строки-заголовка
        public int  RowCount;   // 1 (заголовок) + задачи

        public TaskGroupHeaderVm Header = null!;
    }

    private readonly List<Entry> _entries;
    private readonly TaskRowMap  _map;
    private readonly DateTime _now;

    private readonly Dictionary<int, object[]> _pages = new();
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _needLoad = new();
    private readonly HashSet<int> _pending  = new();
    private readonly SemaphoreSlim _loadGate = new(4);
    private DispatcherTimer? _loadTimer;
    private int _generation;

    /// <summary>Выделение, пережившее перезагрузку: ключи задач, которые надо снова подсветить —
    /// в том числе в карточках, которые ещё не приехали.</summary>
    private readonly HashSet<TaskKey> _preselect;

    // Список НИКОГДА не меняет форму на месте: любая правка данных, сворачивание группы, поиск
    // или смена фильтра пересобирают его целиком и заново отдают в ItemsSource. Поэтому
    // INotifyCollectionChanged реализован пустышкой — интерфейс нужен только затем, чтобы
    // VirtualizingStackPanel вообще согласилась работать с источником как с виртуальным.
    // Оживление заглушки идёт не через него, а привязками карточки (TaskCardVm.Fill).
    event NotifyCollectionChangedEventHandler? INotifyCollectionChanged.CollectionChanged
    {
        add { } remove { }
    }

    /// <summary>
    /// Отменить фоновые страницы: список выброшен и его строки больше никто не покажет.
    /// Без этого догрузка осиротевшего списка продолжала бы занимать подключения и раскладывать
    /// данные по массивам, которых уже нет на экране.
    /// </summary>
    public void Cancel() => _generation++;

    public TaskVirtualList(IReadOnlyList<Entry> entries, DateTime now, IEnumerable<TaskKey>? preselect = null)
    {
        _entries   = entries.ToList();
        _now       = now;
        _preselect = preselect is null ? [] : [.. preselect];

        var first = true;
        foreach (var e in _entries)
        {
            e.Header = new TaskGroupHeaderVm(e.Group, e.Collapsed, first);
            first    = false;
        }

        _map = new TaskRowMap([.. _entries.Select(e => new TaskRowMap.Group(e.Tasks, e.Collapsed))]);

        for (var i = 0; i < _entries.Count; i++)
        {
            _entries[i].FirstRow = _map[i].FirstRow;
            _entries[i].RowCount = _map[i].RowCount;
        }
    }

    // ── IList: доступ по индексу ─────────────────────────────────────────────

    public int Count => _map.Count;

    public object this[int index]
    {
        get
        {
            var page = index / PageSize;
            if (!_pages.TryGetValue(page, out var rows)) rows = LoadPage(page);
            var slot = index % PageSize;
            return slot < rows.Length ? rows[slot] : TaskCardVm.Loading();
        }
        set => throw new NotSupportedException();
    }

    // Строки, которые СЕЙЧАС лежат в кеше страниц. Всё, что список может отдать выделению:
    // задач за пределами кеша он не знает.
    private IEnumerable<object> RealizedItems => _pages.Values.SelectMany(p => p);

    /// <summary>Реализованные карточки в порядке показа — по ним считается диапазон Ctrl+Shift
    /// и состав выделения.</summary>
    public List<TaskRecord> RealizedTasks()
    {
        var result = new List<TaskRecord>();
        foreach (var page in _pages.Keys.Order())
            foreach (var row in _pages[page])
                if (row is TaskCardVm { IsPlaceholder: false } card) result.Add(card.Task);
        return result;
    }

    /// <summary>Перекрасить реализованные карточки под текущее выделение и запомнить его,
    /// чтобы карточки, которые приедут позже, тоже пришли подсвеченными.</summary>
    public void SyncSelection(TaskSelection selection, IEnumerable<TaskKey> keys)
    {
        _preselect.Clear();
        foreach (var k in keys) _preselect.Add(k);

        foreach (var row in RealizedItems)
            if (row is TaskCardVm { IsPlaceholder: false } card)
                card.IsSelected = selection.Contains(card.Task);
    }

    // ── Страницы с фоновой загрузкой ─────────────────────────────────────────

    // Заголовки и «+N ещё» строятся сразу (данных не нужно), карточки — заглушками до фоновой
    // загрузки. Страница создаётся мгновенно и без единого запроса: прокрутка не ждёт БД.
    private object[] LoadPage(int page)
    {
        EvictOldestIfFull();
        var start = page * PageSize;
        var take  = Math.Max(0, Math.Min(PageSize, Count - start));
        var rows  = new object[take];

        for (var k = 0; k < take; k++) rows[k] = RowAt(start + k);

        _pages[page] = rows;
        _lru.AddLast(page);
        _needLoad.Add(page);
        ScheduleLoad();
        return rows;
    }

    // Что стоит в строке: заголовок коллекции либо карточка — пока заглушкой, до страницы.
    private object RowAt(int row)
    {
        var (gi, kind, _) = _map.Resolve(row);
        if (gi < 0) return TaskCardVm.Loading();
        return kind == TaskRowKind.Header ? _entries[gi].Header : TaskCardVm.Loading();
    }

    private TaskCardVm Card(TaskRecord t) =>
        new(t, _now) { IsSelected = TaskSelection.KeyOf(t) is { } k && _preselect.Contains(k) };

    // Дебаунс: транзитные страницы при быстром перетаскивании ползунка вытесняются и запроса
    // не тратят — то же правило и с тем же интервалом, что в календаре.
    private void ScheduleLoad()
    {
        if (_loadTimer is null)
        {
            _loadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _loadTimer.Tick += (_, _) => FlushPendingLoads();
        }
        _loadTimer.Stop();
        _loadTimer.Start();
    }

    private void FlushPendingLoads()
    {
        _loadTimer?.Stop();
        if (_needLoad.Count == 0) return;
        foreach (var page in _lru.Reverse().ToList())   // newest-first: где человек остановился
        {
            if (!_needLoad.Remove(page)) continue;
            if (!_pages.ContainsKey(page)) continue;
            _pending.Add(page);
            _ = LoadPageBackgroundAsync(_generation, page);
        }
    }

    private async Task LoadPageBackgroundAsync(int gen, int page)
    {
        await _loadGate.WaitAsync();
        try
        {
            if (gen != _generation) return;
            if (!_pending.Contains(page) || !_pages.ContainsKey(page)) return;

            foreach (var (e, local, n, slot) in Segments(page))
            {
                var part = await LoadSliceAsync(e, local, n);
                if (gen != _generation) return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _generation) return;
                    if (!_pages.TryGetValue(page, out var rows)) return;
                    for (var i = 0; i < part.Count && slot + i < rows.Length; i++)
                        if (rows[slot + i] is TaskCardVm card)
                        {
                            card.Fill(part[i], _now);
                            card.IsSelected = TaskSelection.KeyOf(part[i]) is { } k && _preselect.Contains(k);
                        }
                });
            }
            _pending.Remove(page);
        }
        catch
        {
            // Фоновая ошибка (база занята, пропали права) — строки останутся «Загрузка…»
            // до следующего запроса той же страницы. Валить раздел из-за одной страницы нельзя.
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // Разбор страницы на куски по группам: (группа, номер первой карточки в ней, сколько, слот).
    // Заголовки и «+N ещё» пропускаются — их страница уже содержит.
    private List<(Entry Entry, int Local, int Take, int Slot)> Segments(int page)
    {
        var segs  = new List<(Entry, int, int, int)>();
        var start = page * PageSize;
        var take  = Math.Max(0, Math.Min(PageSize, Count - start));
        var k     = 0;

        while (k < take)
        {
            var (gi, kind, local) = _map.Resolve(start + k);
            if (gi < 0) break;

            // Заголовок данных не ждёт — он готов сразу.
            if (kind != TaskRowKind.Task) { k++; continue; }

            var e = _entries[gi];
            var n = Math.Min(take - k, e.Tasks - local);
            if (n <= 0) { k++; continue; }

            segs.Add((e, local, n, k));
            k += n;
        }
        return segs;
    }

    // Кусок группы из БД. Границу потоков кусок не уважает, поэтому сперва раскладываем его на
    // запросы (TaskStreams.Split — чистая арифметика, проверенная отдельно), потом читаем.
    private async Task<List<TaskRecord>> LoadSliceAsync(Entry e, int local, int take)
    {
        var result = new List<TaskRecord>(take);

        foreach (var (dated, skip, n) in TaskStreams.Split(e.Dated, local, take))
        {
            var part = await e.Repo.LoadTaskPageAsync(e.Group.ColId, e.Stats, e.View, dated, skip, n);
            foreach (var t in part) t.DbId = e.Group.DbId;
            result.AddRange(part);

            // Прочиталось меньше, чем просили: база изменилась между счётом и чтением.
            // Дальше по этому куску идти незачем — оставшиеся слоты честнее оставить заглушками,
            // чем заполнить сдвинутыми строками.
            if (part.Count < n) break;
        }
        return result;
    }

    private void EvictOldestIfFull()
    {
        if (_pages.Count < MaxPages) return;
        var oldest = _lru.First!.Value;
        _lru.RemoveFirst();
        _pages.Remove(oldest);
        _pending.Remove(oldest);
        _needLoad.Remove(oldest);
    }

    // ── IList<T> / IList (нужны ItemsControl и VirtualizingStackPanel) ────────
    //
    // IndexOf/Contains намеренно отвечают «нет»: панель зовёт их при пересборке, а честный
    // ответ означал бы материализацию всего списка — ровно того, чего виртуализация и избегает.

    public IEnumerator<object> GetEnumerator() =>
        Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsReadOnly     => true;
    public bool IsFixedSize    => false;
    public bool IsSynchronized => false;
    public object SyncRoot     => this;

    public bool Contains(object item) => false;
    public int  IndexOf(object item)  => -1;
    public void CopyTo(object[] array, int idx) => throw new NotSupportedException();
    public void Add(object item)           => throw new NotSupportedException();
    public void Clear()                    => throw new NotSupportedException();
    public void Insert(int i, object item) => throw new NotSupportedException();
    public void RemoveAt(int i)            => throw new NotSupportedException();
    public bool Remove(object item)        => throw new NotSupportedException();

    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    int  IList.Add(object? v)           => throw new NotSupportedException();
    bool IList.Contains(object? v)      => false;
    int  IList.IndexOf(object? v)       => -1;
    void IList.Insert(int i, object? v) => throw new NotSupportedException();
    void IList.Remove(object? v)        => throw new NotSupportedException();
    void IList.RemoveAt(int i)          => throw new NotSupportedException();
    void IList.Clear()                  => throw new NotSupportedException();
    void ICollection.CopyTo(Array arr, int idx) => throw new NotSupportedException();
}
