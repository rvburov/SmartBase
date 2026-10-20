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
///
/// Страница читается от соседа: края каждого приехавшего куска запоминаются
/// (<see cref="Entry.Known"/>), и следующая страница вниз или вверх идет от них keyset-курсором
/// — одинаково быстро на любой глубине. Путь выбирает <see cref="DocSeekPlan"/>.
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

        /// <summary>
        /// Документы потока, чье место в порядке уже известно: номер в потоке → курсор. Края
        /// каждого приехавшего куска; хранятся и для вытесненных страниц — это пара чисел, а
        /// возврат к ним без курсора стоил бы смещения от края. Живет, пока жив список: любая
        /// правка данных собирает ленту заново, и старые курсоры уходят вместе с ним.
        /// </summary>
        public readonly Dictionary<int, DocCursor> Known = [];
    }

    private readonly List<Entry> _entries;
    private readonly DocRowMap   _map;

    private readonly Dictionary<int, object[]> _pages = new();
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _needLoad = new();
    private readonly HashSet<int> _pending  = new();
    private readonly DocPageRetry _retry    = new();
    private readonly SemaphoreSlim _loadGate = new(4);
    private DispatcherTimer? _loadTimer;
    private int _generation;
    private readonly DocOrderKey[]? _order;
    public event Action? OrderInvalidated;
    private bool _orderInvalidated;

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
    public void Cancel()
    {
        _generation++;
        _loadTimer?.Stop();
        _needLoad.Clear();
    }

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

    public DocVirtualList(IReadOnlyList<Entry> entries, IEnumerable<DocKey>? preselect = null, DocOrderKey[]? order = null)
    {
        _entries = entries.ToList();
        _order = order;
        if (preselect is not null) foreach (var k in preselect) _preselect.Add(k);

        _map = new DocRowMap([.. _entries.Select(e => e.Docs)]);
        for (var i = 0; i < _entries.Count; i++) _entries[i].FirstRow = _map[i].FirstRow;
    }

    // ── IList: доступ по индексу ─────────────────────────────────────────────

    public int Count => _order?.Length ?? _map.Count;

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

    public static Task<DocOrderKey[]> BuildOrderAsync(IReadOnlyList<Entry> entries, DocSort sort,
                                                     CancellationToken ct = default) =>
        BuildOrderAsync(entries, sort, (entry, token) => entry.Repo.GetDocOrderKeysAsync(
            entry.ColId, entry.TypeFieldId, entry.View, sort, token), ct);

    internal static async Task<DocOrderKey[]> BuildOrderAsync(IReadOnlyList<Entry> entries, DocSort sort,
        Func<Entry, CancellationToken, Task<List<DocOrderItem>>> readKeys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var streams = new List<DocOrderStream>(entries.Count);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var keys = await readKeys(entry, ct);
            ct.ThrowIfCancellationRequested();
            streams.Add(new DocOrderStream(entry.DbId, entry.ColId, keys));
        }
        return await Task.Run(() => DocOrderPlan.Merge(streams, sort, ct), ct);
    }

    private async Task LoadOrderedPageAsync(int gen, int page)
    {
        var start = page * PageSize;
        var keys = _order!.Skip(start).Take(PageSize).ToArray();
        var records = new Dictionary<(int Entry, int Record, int Field, int File), DocRecord>();
        foreach (var group in keys.GroupBy(k => k.Entry))
        {
            if (gen != _generation) return;
            var entry = _entries[group.Key];
            var docs = await entry.Repo.GetDocsByOrderKeysAsync(entry.ColId, entry.TypeFieldId, group.ToArray(), entry.View);
            await Task.Run(() => DocStamps.Probe(docs));
            foreach (var doc in docs)
            {
                doc.DbId = entry.DbId;
                doc.DbName = entry.DbName;
                records[(group.Key, doc.Id!.Value, doc.FileFieldId, doc.FileIndex)] = doc;
            }
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (gen != _generation || !_pages.TryGetValue(page, out var rows)) return;
            for (var i = 0; i < keys.Length && i < rows.Length; i++)
            {
                var key = keys[i];
                if (!records.TryGetValue((key.Entry, key.RecordId, key.FieldId, key.FileIndex), out var doc))
                {
                    if (!_orderInvalidated) { _orderInvalidated = true; OrderInvalidated?.Invoke(); }
                    continue;
                }
                TextState?.Invoke(doc);
                if (rows[i] is not DocCardVm card) continue;
                card.Fill(doc);
                card.IsSelected = DocSelection.KeyOf(doc) is { } selected && _preselect.Contains(selected);
                CardRealized?.Invoke(card);
            }
        });
    }

    private async Task LoadPageBackgroundAsync(int gen, int page)
    {
        await _loadGate.WaitAsync();
        var failed = false;
        try
        {
            if (gen != _generation) return;
            if (!_pending.Contains(page) || !_pages.ContainsKey(page)) return;

            if (_order is not null) await LoadOrderedPageAsync(gen, page);
            else foreach (var (e, local, n, slot) in Segments(page))
            {
                var seek = DocSeekPlan.Choose(local, n, e.Docs, e.Known);
                var part = await e.Repo.GetDocPageAsync(e.ColId, e.TypeFieldId, e.View, seek, n);
                if (gen != _generation) return;
                // Края куска — курсоры для соседних страниц. Только у полного куска: неполный
                // значит, что данные успели поменяться, и номера в потоке уже не те.
                if (n > 0 && part.Count == n)
                {
                    e.Known[local]         = part[0].Cursor;
                    e.Known[local + n - 1] = part[^1].Cursor;
                }
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
            _retry.Done(page);
        }
        catch (Exception ex)
        {
            // Фоновая ошибка (база занята, пропали права). Валить раздел из-за одной страницы
            // нельзя, но и оставлять ее «Загрузка…» навсегда тоже: страница уйдет на повтор.
            failed = true;
            if (Logging.Diag.Throttle("library-page-fail", TimeSpan.FromMinutes(1)))
                Serilog.Log.Warning(ex, "Библиотека: страница ленты {Page} не прочитана", page);
        }
        finally
        {
            _loadGate.Release();
        }
        if (failed) await RetryLaterAsync(gen, page);
    }

    // Упавшая страница идет на повторную попытку с растущей паузой (DocPageRetry), а не
    // остается «Загрузка…» до конца жизни списка: раньше она числилась и среди ожидающих, и
    // среди загруженных, и индексатор ее больше не запрашивал. Попытки кончились — страница
    // снимается с учета, и следующая прокрутка запросит ее заново, как новую.
    private async Task RetryLaterAsync(int gen, int page)
    {
        _pending.Remove(page);
        if (_retry.Fail(page) is not { } delay) { Forget(page); return; }

        await Task.Delay(delay);
        if (gen != _generation || !_pages.ContainsKey(page) || _pending.Contains(page)) return;
        _needLoad.Add(page);
        ScheduleLoad();
    }

    // Снять страницу с учета целиком: ее заглушки остаются у панели, пока та не спросит эти
    // строки снова, — тогда индексатор создаст страницу заново и закажет загрузку.
    private void Forget(int page)
    {
        _pages.Remove(page);
        _lru.Remove(page);
        _pending.Remove(page);
        _needLoad.Remove(page);
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
