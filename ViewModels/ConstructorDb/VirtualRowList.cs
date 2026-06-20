using System.Collections;
using System.Collections.Specialized;
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
    private readonly int _dataCount;

    // Постоянный экземпляр add-row — DataGrid сравнивает по ссылке при ScrollIntoView.
    public TableRowVm AddRowItem { get; } = TableRowVm.AddRow();

    public int Count => _dataCount + 1;  // +1 под add-row в конце

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public VirtualRowList(ConstructorRepository repo, int collectionId, List<DbField> fields,
                          bool excludeHidden = false)
    {
        _repo          = repo;
        _collectionId  = collectionId;
        _fields        = fields;
        _excludeHidden = excludeHidden;
        _dataCount     = repo.CountRecords(collectionId, excludeHidden);
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

    /// <summary>Загружает страницу из БД (один запрос записей + один пакетный для значений).</summary>
    private TableRowVm[] LoadPage(int page)
    {
        EvictOldestIfFull();

        var skip = page * PageSize;
        var take = Math.Min(PageSize, _dataCount - skip);

        // Keyset, если предыдущая страница уже грузилась (последовательный скролл): запрос
        // ПОСЛЕ её последней строки — O(take), не зависит от глубины. Иначе offset (прыжок по
        // скроллбару) — O(offset), но по составному индексу (CollectionId, Position) это быстро.
        var records = page > 0 && _pageEnd.TryGetValue(page - 1, out var cur)
            ? _repo.GetRecordsPageAfter(_collectionId, cur.Pos, cur.Id, take, _excludeHidden)
            : _repo.GetRecordsPage(_collectionId, skip, take, _excludeHidden);

        var ids    = records.Select(r => r.Id!.Value).ToList();
        var valMap = _repo.GetFieldValuesBatch(ids);

        var rows = new TableRowVm[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            var rec    = records[i];
            var id     = rec.Id!.Value;
            var values = valMap.TryGetValue(id, out var v) ? v : [];
            rows[i]    = new TableRowVm(_repo, id, skip + i + 1, values,
                                        rec.Title ?? "", rec.Description);
        }

        if (records.Count > 0)
        {
            var last = records[^1];
            _pageEnd[page] = (last.Position, last.Id!.Value);
        }

        _pages[page] = rows;
        _lru.AddLast(page);
        return rows;
    }

    private void EvictOldestIfFull()
    {
        if (_pages.Count < MaxPages) return;
        var oldest = _lru.First!.Value;
        _lru.RemoveFirst();
        _pages.Remove(oldest);
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
        _pages.Clear();
        _lru.Clear();
        _pageEnd.Clear();
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
