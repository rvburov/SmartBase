using System.Collections;
using System.Collections.Specialized;
using Avalonia.Threading;
using SmartBase.Core.Documents;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.Library;

/// <summary>
/// Виртуальный список раздела «Библиотека документов». Реализует <see cref="IList"/>, поэтому
/// полоса прокрутки соответствует ПОЛНОМУ числу документов всех отмеченных коллекций — прыгнуть
/// можно в любое место, — а сами карточки не материализуются: список знает лишь РАЗМЕРЫ потоков
/// (<see cref="ConstructorRepository.GetDocCollectionStats"/>, считаются в фоне до его
/// создания), а документы подтягивает страницами по требованию.
///
/// Прямой аналог <c>TaskVirtualList</c> с одним упрощением: там строка бывает трех видов
/// (заголовок группы, карточка, «+N еще»), здесь лента плоская и строка всегда одна — карточка.
/// Потому и раскладка проще: <see cref="DocRowMap"/> без строк-заголовков.
///
/// Порядок внутри потока — порядок самой коллекции (<c>Position, Id</c>), а разные файловые
/// столбцы одной записи идут подряд; как это ложится на индексы, описано в
/// <c>ConstructorRepository.DocsPaging.cs</c>.
/// </summary>
internal sealed class DocVirtualList : IList<object>, IList, INotifyCollectionChanged
{
    private const int PageSize = 200;
    private const int MaxPages = 40;    // 40 × 200 = 8000 строк в кеше

    /// <summary>Поток списка: коллекция, ее размеры и место в плоской нумерации строк.</summary>
    internal sealed class Entry
    {
        public required int    DbId;
        public required string DbName;
        public required int    ColId;
        public required ConstructorRepository Repo;
        /// <summary>Поле «Тип документа» коллекции; 0 — его нет.</summary>
        public required int TypeFieldId;
        /// <summary>Вид раздела (фильтр типов), в котором собран поток.</summary>
        public required ConstructorRepository.DocView View;
        public int Docs;        // всего документов потока В ЭТОМ виде
        public int FirstRow;
    }

    private readonly List<Entry> _entries;
    private readonly DocRowMap   _map;

    private readonly Dictionary<int, object[]> _pages = new();
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _needLoad = new();
    private readonly HashSet<int> _pending  = new();
    private readonly SemaphoreSlim _loadGate = new(4);
    private DispatcherTimer? _loadTimer;
    private int _generation;

    /// <summary>Выделение, пережившее перезагрузку: ключи документов, которые надо снова
    /// подсветить — в том числе в карточках, которые еще не приехали.</summary>
    private readonly HashSet<DocKey> _preselect = [];

    /// <summary>Карточка приехала — контроллер может заказать ей миниатюру.</summary>
    public event Action<DocCardVm>? CardRealized;

    /// <summary>
    /// Проставить документу то, что кеш знает про его текст, ДО того как карточка соберется.
    /// Ставит контроллер: кеш знает он, а список — нет. Null — перепись еще не готова.
    /// </summary>
    public Action<DocRecord>? TextState { get; set; }

    // Список НИКОГДА не меняет форму на месте: любая правка данных, поиск или смена фильтра
    // пересобирают его целиком и заново отдают в ItemsSource. Поэтому INotifyCollectionChanged
    // реализован пустышкой — интерфейс нужен только затем, чтобы VirtualizingStackPanel вообще
    // согласилась работать с источником как с виртуальным.
    event NotifyCollectionChangedEventHandler? INotifyCollectionChanged.CollectionChanged
    {
        add { } remove { }
    }

    /// <summary>
    /// Отменить фоновые страницы: список выброшен и его строки больше никто не покажет.
    /// Без этого догрузка осиротевшего списка продолжала бы занимать подключения.
    /// </summary>
    public void Cancel() => _generation++;

    /// <summary>
    /// Догнать УЖЕ приехавшие карточки, когда перепись кеша досчиталась. Новые страницы
    /// возьмут состояние сами через <see cref="TextState"/>; этот проход — только для тех, что
    /// успели собраться раньше. Ходит по кешу страниц, то есть по паре тысяч строк максимум, и
    /// ничего не подгружает.
    /// </summary>
    public void ReapplyTextState(Action<DocRecord> apply)
    {
        foreach (var rows in _pages.Values)
            foreach (var row in rows)
                if (row is DocCardVm { IsPlaceholder: false } card)
                {
                    apply(card.Doc);
                    card.RefreshTextState();
                }
    }

    public DocVirtualList(IReadOnlyList<Entry> entries, IEnumerable<DocKey>? preselect = null)
    {
        _entries = entries.ToList();
        if (preselect is not null) foreach (var k in preselect) _preselect.Add(k);

        _map = new DocRowMap([.. _entries.Select(e => e.Docs)]);
        for (var i = 0; i < _entries.Count; i++) _entries[i].FirstRow = _map[i].FirstRow;
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
            return slot < rows.Length ? rows[slot] : DocCardVm.Loading();
        }
        set => throw new NotSupportedException();
    }

    /// <summary>Реализованные карточки в порядке показа — по ним считается диапазон Ctrl+Shift.</summary>
    public List<DocRecord> RealizedDocs()
    {
        var result = new List<DocRecord>();
        foreach (var page in _pages.Keys.Order())
            foreach (var row in _pages[page])
                if (row is DocCardVm { IsPlaceholder: false } card) result.Add(card.Doc);
        return result;
    }

    /// <summary>Перекрасить реализованные карточки под текущее выделение и запомнить его, чтобы
    /// карточки, которые приедут позже, тоже пришли подсвеченными.</summary>
    public void SyncSelection(DocSelection selection)
    {
        _preselect.Clear();
        foreach (var k in selection.Keys) _preselect.Add(k);

        foreach (var page in _pages.Values)
            foreach (var row in page)
                if (row is DocCardVm { IsPlaceholder: false } card)
                    card.IsSelected = selection.Contains(card.Doc);
    }

    // ── Страницы с фоновой загрузкой ─────────────────────────────────────────

    // Страница создается мгновенно и без единого запроса: карточки рождаются заглушками,
    // прокрутка не ждет базу.
    private object[] LoadPage(int page)
    {
        EvictOldestIfFull();
        var start = page * PageSize;
        var take  = Math.Max(0, Math.Min(PageSize, Count - start));
        var rows  = new object[take];

        for (var k = 0; k < take; k++) rows[k] = DocCardVm.Loading();

        _pages[page] = rows;
        _lru.AddLast(page);
        _needLoad.Add(page);
        ScheduleLoad();
        return rows;
    }

    // Дебаунс: транзитные страницы при быстром перетаскивании ползунка вытесняются и запроса
    // не тратят — то же правило и с тем же интервалом, что в календаре и задачах.
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
                var part = await e.Repo.GetDocPageAsync(e.ColId, e.TypeFieldId, e.View, local, n);
                if (gen != _generation) return;
                // Файлы страницы спрашиваются здесь, в пуле: продолжение после await идет в
                // UI-потоке, а карточка без готового ответа спросила бы диск сама — по разу на
                // каждую из двухсот, и на сетевой папке прокрутка вставала бы на каждой странице.
                await Task.Run(() => DocStamps.Probe(part));
                if (gen != _generation) return;
                foreach (var d in part) { d.DbId = e.DbId; d.DbName = e.DbName; }

                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _generation) return;
                    if (!_pages.TryGetValue(page, out var rows)) return;
                    for (var i = 0; i < part.Count && slot + i < rows.Length; i++)
                        if (rows[slot + i] is DocCardVm card)
                        {
                            TextState?.Invoke(part[i]);
                            card.Fill(part[i]);
                            card.IsSelected = DocSelection.KeyOf(part[i]) is { } k && _preselect.Contains(k);
                            CardRealized?.Invoke(card);
                        }
                });
            }
            _pending.Remove(page);
        }
        catch
        {
            // Фоновая ошибка (база занята, пропали права) — строки останутся «Загрузка…» до
            // следующего запроса той же страницы. Валить раздел из-за одной страницы нельзя.
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // Разбор страницы на куски по потокам: (поток, номер первой карточки в нем, сколько, слот).
    private List<(Entry Entry, int Local, int Take, int Slot)> Segments(int page)
    {
        var segs  = new List<(Entry, int, int, int)>();
        var start = page * PageSize;
        var take  = Math.Max(0, Math.Min(PageSize, Count - start));
        var k     = 0;

        while (k < take)
        {
            var (si, local) = _map.Resolve(start + k);
            if (si < 0) break;

            var e = _entries[si];
            var n = Math.Min(take - k, e.Docs - local);
            if (n <= 0) { k++; continue; }

            segs.Add((e, local, n, k));
            k += n;
        }
        return segs;
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
    // IndexOf/Contains намеренно отвечают «нет»: панель зовет их при пересборке, а честный
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
