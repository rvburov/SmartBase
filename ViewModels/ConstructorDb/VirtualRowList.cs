using System.Collections;
using System.Collections.Specialized;
using Avalonia.Threading;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.ViewModels.ConstructorDb;

/// <summary>
/// Виртуальная коллекция строк таблицы с LRU-кешем страниц.
/// DataGrid обращается к элементам по индексу через IList, при этом
/// данные загружаются из БД постранично — в памяти хранится не более
/// <see cref="MaxPages"/> × <see cref="PageSize"/> строк одновременно.
/// </summary>
internal sealed class VirtualRowList : IList<TableRowVm>, IList, INotifyCollectionChanged
{
    private const int PageSize = 200;
    private const int MaxPages = 50;   // 50 × 200 = 10 000 строк в кеше

    private readonly ConstructorRepository _repo;
    private readonly int                   _collectionId;
    private readonly List<DbField>         _fields;
    private readonly bool                  _excludeHidden;   // исключать скрытые записи (окно «Отображение записей»)
    private readonly Dictionary<int, TableRowVm[]> _pages = [];
    private readonly LinkedList<int> _lru = new();
    // page → (Position, Id) последней строки страницы: курсор для keyset-загрузки
    // следующей страницы при последовательном скролле. Храним даже для вытесненных из
    // _pages страниц (это всего пара int) — курсоры дёшевы и ускоряют скролл по глубине.
    private readonly Dictionary<int, (int Pos, int Id)> _pageEnd = [];
    // Страницы, ожидающие отложенного заполнения (показывают «Загрузка…»).
    private readonly HashSet<int> _pending = [];
    // Поколение кеша: растёт при Reset; фоновые загрузки со старым поколением отбрасываются.
    private int _generation;
    // Ограничитель параллельных фоновых чтений (защита от всплеска при быстром драге скролла).
    private readonly System.Threading.SemaphoreSlim _loadGate = new(4);
    // Режим списка id (фильтр/сортировка): если задан — страницы берутся срезом этого списка
    // и грузятся по id в его порядке, а не keyset/offset по Position. null — обычный режим.
    private readonly IReadOnlyList<int>? _recordIds;
    private readonly int _dataCount;
    // Режим «скелет»: коллекция дублируется, записи ещё копируются в фоне. Все строки —
    // «Загрузка…» без обращения к БД; реальные данные появятся при перезагрузке таблицы.
    private readonly bool _skeleton;

    // Постоянный экземпляр add-row — DataGrid сравнивает по ссылке при ScrollIntoView.
    public TableRowVm AddRowItem { get; } = TableRowVm.AddRow();

    public int Count => _dataCount + 1;  // +1 под add-row в конце

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    // Отложенная подгрузка с заглушками «Загрузка…» (флаг настроек). По умолчанию выкл —
    // тогда страницы грузятся синхронно (проверенный путь). Вкл — эффект «Загрузка…» при скролле.
    private readonly bool _asyncLoad;

    public VirtualRowList(ConstructorRepository repo, int collectionId, List<DbField> fields,
                          bool excludeHidden = false, IReadOnlyList<int>? recordIds = null,
                          int? skeletonCount = null)
    {
        _repo          = repo;
        _collectionId  = collectionId;
        _fields        = fields;
        _excludeHidden = excludeHidden;
        _recordIds     = recordIds;
        _asyncLoad     = Utils.AppSettings.LoadAsyncRowLoad();
        _skeleton      = skeletonCount is not null;
        // Скелет (дубль в процессе): число строк = записей источника. Режим списка id: длина
        // списка. Иначе — считаем из БД.
        _dataCount     = skeletonCount ?? recordIds?.Count ?? repo.CountRecords(collectionId, excludeHidden);
    }

    public TableRowVm this[int index]
    {
        get
        {
            if (index == _dataCount) return AddRowItem;

            var page = index / PageSize;
            var slot = index % PageSize;
            if (!_pages.TryGetValue(page, out var rows)) rows = LoadPage(page);
            return rows[slot];
        }
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Загружает страницу. При выключенном async-флаге — синхронно (реальные строки сразу).
    /// При включённом — создаёт строки-заглушки «Загрузка…» и грузит данные в ФОНОВОМ потоке
    /// (отдельное read-only подключение, WAL), затем на UI-потоке заполняет строки. UI-поток
    /// не трогает БД при скролле → ноль микроподвисаний даже при экстремально быстром скролле.
    /// </summary>
    private TableRowVm[] LoadPage(int page)
    {
        EvictOldestIfFull();

        var skip = page * PageSize;
        var take = Math.Min(PageSize, _dataCount - skip);

        if (_skeleton)
        {
            // Дубль ещё копируется: показываем «Загрузка…» по всем строкам без обращения к БД.
            var ph = new TableRowVm[take];
            for (var i = 0; i < take; i++) ph[i] = TableRowVm.Loading(skip + i + 1);
            _pages[page] = ph;
            _lru.AddLast(page);
            return ph;
        }

        if (!_asyncLoad)
        {
            // Синхронный путь: грузим записи и сразу строим реальные строки.
            var records = LoadRecordsForPage(page, skip, take);
            var rows    = BuildRows(skip, records);
            _pages[page] = rows;
            _lru.AddLast(page);
            return rows;
        }

        // Async-путь: заглушки «Загрузка…» сразу, данные — в фоне.
        var placeholders = new TableRowVm[take];
        for (var i = 0; i < take; i++) placeholders[i] = TableRowVm.Loading(skip + i + 1);

        _pages[page] = placeholders;
        _lru.AddLast(page);
        _pending.Add(page);

        // Параметры запроса вычисляем на UI-потоке (без гонки с _pageEnd/_recordIds).
        List<int>? idSlice = null;
        int? afterPos = null, afterId = null;
        if (_recordIds is not null)
        {
            idSlice = new List<int>(take);
            for (var k = 0; k < take; k++) idSlice.Add(_recordIds[skip + k]);
        }
        else if (page > 0 && _pageEnd.TryGetValue(page - 1, out var cur))
        {
            afterPos = cur.Pos; afterId = cur.Id;
        }

        // Поколение фиксируем сейчас: если до прихода данных был Reset (правка/перезагрузка),
        // результат уже неактуален — отбрасываем (иначе заполнили бы НОВЫЕ заглушки СТАРЫМИ данными).
        _ = LoadPageBackgroundAsync(_generation, page, idSlice, skip, take, afterPos, afterId);
        return placeholders;
    }

    // Фоновая загрузка страницы с ограничением параллелизма (семафор): при очень быстром
    // драге скроллбара не плодим сотни подключений/задач. Чтение — в фоне, заполнение — на UI.
    private async Task LoadPageBackgroundAsync(int gen, int page, List<int>? idSlice,
                                               int skip, int take, int? afterPos, int? afterId)
    {
        await _loadGate.WaitAsync();
        try
        {
            if (gen != _generation) return;   // устарело ещё до старта (был Reset)
            // Пока задача ждала слот, пользователь мог проскроллить дальше и страницу вытеснили
            // из кеша (EvictOldestIfFull). Тогда не тратим слот/запрос — освобождаем гейт сразу,
            // чтобы быстрее дошла очередь до СТРАНИЦЫ, на которой пользователь остановился.
            // (Возврат на эту страницу запросит загрузку заново.) Чтение полей — на UI-потоке
            // (продолжение после await идёт в захваченном контексте), гонки нет.
            if (!_pending.Contains(page) || !_pages.ContainsKey(page)) return;
            var data = await _repo.LoadPageAsync(_collectionId, _excludeHidden, idSlice, skip, take, afterPos, afterId);
            Dispatcher.UIThread.Post(() => FillFromData(gen, page, data.Records, data.Values));
        }
        catch
        {
            // Фоновая загрузка: ошибку проглатываем — строки останутся «Загрузка…» до повторного запроса.
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // Запрос записей страницы из БД (режим списка id / keyset / offset) — общий для обоих путей.
    private List<DbRecord> LoadRecordsForPage(int page, int skip, int take)
    {
        if (_recordIds is not null)
        {
            // Режим списка id: страница — срез готового списка, грузим записи по id в его порядке.
            var slice = new List<int>(take);
            for (var k = 0; k < take; k++) slice.Add(_recordIds[skip + k]);
            return _repo.GetRecordsByIdsOrdered(slice);
        }

        // Keyset, если предыдущая страница уже грузилась (последовательный скролл): запрос
        // ПОСЛЕ её последней строки — O(take), не зависит от глубины. Иначе offset (прыжок по
        // скроллбару) — O(offset), но по составному индексу (CollectionId, Position) это быстро.
        return page > 0 && _pageEnd.TryGetValue(page - 1, out var cur)
            ? _repo.GetRecordsPageAfter(_collectionId, cur.Pos, cur.Id, take, _excludeHidden)
            : _repo.GetRecordsPage(_collectionId, skip, take, _excludeHidden);
    }

    // Строит реальные строки из записей + их значений (синхронный путь).
    private TableRowVm[] BuildRows(int skip, List<DbRecord> records)
    {
        var ids    = records.Select(r => r.Id!.Value).ToList();
        var valMap = _repo.GetFieldValuesBatch(ids);

        var rows = new TableRowVm[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            var rec    = records[i];
            var id     = rec.Id!.Value;
            var values = valMap.TryGetValue(id, out var v) ? v : [];
            rows[i]    = new TableRowVm(_repo, id, skip + i + 1, values, rec.Title ?? "", rec.Description);
        }
        RememberCursor(skip / PageSize, records);
        return rows;
    }

    /// <summary>Заполняет ранее созданные строки-заглушки данными, пришедшими из фона (UI-поток).</summary>
    private void FillFromData(int gen, int page, List<DbRecord> records,
                             Dictionary<int, Dictionary<int, string>> valMap)
    {
        if (gen != _generation) return;                         // данные устарели (был Reset)
        if (!_pending.Remove(page)) return;                     // уже заполнена/сброшена
        if (!_pages.TryGetValue(page, out var rows)) return;    // вытеснена из кеша

        // Строки-заглушки заполняем реальными данными; на будущее (редактирование ячеек)
        // отдаём ОСНОВНОЙ репозиторий — фоновый контекст использовался только для чтения.
        for (var i = 0; i < records.Count && i < rows.Length; i++)
        {
            var rec    = records[i];
            var id     = rec.Id!.Value;
            var values = valMap.TryGetValue(id, out var v) ? v : [];
            rows[i].Fill(_repo, id, values, rec.Title ?? "", rec.Description);
        }

        RememberCursor(page, records);
    }

    // Сохраняет (Position, Id) последней строки страницы — курсор для keyset следующей.
    private void RememberCursor(int page, List<DbRecord> records)
    {
        if (records.Count == 0) return;
        var last = records[^1];
        _pageEnd[page] = (last.Position, last.Id!.Value);
    }

    private void EvictOldestIfFull()
    {
        if (_pages.Count < MaxPages) return;
        var oldest = _lru.First!.Value;
        _lru.RemoveFirst();
        _pages.Remove(oldest);
        _pending.Remove(oldest);   // вытесненную страницу её фоновая загрузка пропустит (см. выше)
    }

    /// <summary>Ищет строку по RecordId в уже загруженных страницах (не инициирует загрузку).</summary>
    public TableRowVm? FindCachedByRecordId(int recordId)
    {
        foreach (var rows in _pages.Values)
            foreach (var row in rows)
                if (row.RecordId == recordId) return row;
        return null;
    }

    /// <summary>Сбрасывает кеш и сигнализирует DataGrid о полной перестройке.</summary>
    public void Reset()
    {
        _generation++;          // отбрасываем фоновые загрузки, начатые до сброса
        _pages.Clear();
        _lru.Clear();
        _pageEnd.Clear();
        _pending.Clear();
        CollectionChanged?.Invoke(this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    // ── IList<T> / IList (нужны DataGrid) ─────────────────────────────────────
    public IEnumerator<TableRowVm> GetEnumerator() =>
        Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsReadOnly    => true;
    public bool IsFixedSize   => false;
    public bool IsSynchronized => false;
    public object SyncRoot    => this;

    public bool Contains(TableRowVm item) => false;
    public int  IndexOf(TableRowVm item)  => -1;
    public void CopyTo(TableRowVm[] array, int idx) => throw new NotSupportedException();
    public void Add(TableRowVm item)           => throw new NotSupportedException();
    public void Clear()                        => throw new NotSupportedException();
    public void Insert(int i, TableRowVm item) => throw new NotSupportedException();
    public void RemoveAt(int i)                => throw new NotSupportedException();
    public bool Remove(TableRowVm item)        => throw new NotSupportedException();

    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    int  IList.Add(object? v)         => throw new NotSupportedException();
    bool IList.Contains(object? v)    => false;
    int  IList.IndexOf(object? v)     => -1;
    void IList.Insert(int i, object? v) => throw new NotSupportedException();
    void IList.Remove(object? v)       => throw new NotSupportedException();
    void IList.RemoveAt(int i)         => throw new NotSupportedException();
    void IList.Clear()                 => throw new NotSupportedException();
    void ICollection.CopyTo(Array arr, int idx) => throw new NotSupportedException();
}
