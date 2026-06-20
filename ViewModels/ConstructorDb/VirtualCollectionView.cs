using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Collections;
using Avalonia.Controls;

namespace DataBase.ViewModels.ConstructorDb;

/// <summary>
/// Виртуализирующее представление строк для <c>DataGrid</c>.
///
/// Ключевой момент производительности: если в <c>DataGrid.ItemsSource</c> положить «сырой»
/// список (даже ленивый <see cref="VirtualRowList"/>), DataGrid оборачивает его в свой
/// <c>DataGridCollectionView</c>, а тот в конструкторе **перебирает источник целиком** —
/// то есть тянет из БД все страницы (для 100k+ это минуты и зависание). Но в
/// <c>OnItemsSourcePropertyChanged</c> DataGrid делает проверку <c>is IDataGridCollectionView</c>
/// и такой источник берёт **как есть, без копирования**, а строки достаёт по индексу по
/// требованию (<c>GetDataItem → IList[index]</c>) и только для **видимых** строк (презентер
/// строк UI-виртуализирован). Поэтому это представление реализует ОБА контракта:
///   • <see cref="IDataGridCollectionView"/> — чтобы DataGrid не материализовал источник;
///   • <see cref="IList"/> — чтобы доступ к строке шёл по индексу лениво (через
///     <see cref="VirtualRowList"/>, который грузит нужную страницу из БД).
///
/// Сортировки/фильтры/группировки здесь не объявляем (<see cref="CanSort"/> и т.п. = false):
/// в приложении свои фильтры в заголовках; перевод фильтра/сортировки в SQL — отдельный шаг.
/// </summary>
internal sealed class VirtualCollectionView : IDataGridCollectionView, IList, INotifyCollectionChanged
{
    private readonly VirtualRowList _rows;
    private int _currentPosition = -1;

    public VirtualCollectionView(VirtualRowList rows)
    {
        _rows = rows;
        // Пробрасываем Reset/изменения из постраничного списка в DataGrid.
        _rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) _currentPosition = -1;
            CollectionChanged?.Invoke(this, e);
        };
    }

    /// <summary>Внутренний постраничный список (его же использует TableView напрямую).</summary>
    public VirtualRowList Rows => _rows;

    // ── IList: ленивый доступ по индексу — ядро виртуализации ──────────────────
    public object? this[int index]
    {
        get => _rows[index];
        set => throw new NotSupportedException();
    }

    public int  Count          => _rows.Count;
    public bool IsReadOnly     => true;
    public bool IsFixedSize    => false;
    public bool IsSynchronized => false;
    public object SyncRoot     => this;

    // IndexOf по абсолютному номеру строки: VirtualRowList проставляет RowNumber = индекс+1
    // при загрузке страницы, поэтому для уже материализованной строки индекс = RowNumber-1
    // (O(1), без перебора 10M). Add-row — в самом конце. Иначе -1 (DataGrid это допускает).
    public int IndexOf(object? value)
    {
        if (value is not TableRowVm row) return -1;
        if (row.IsAddRow) return _rows.Count - 1;
        var idx = row.RowNumber - 1;
        return idx >= 0 && idx < _rows.Count ? idx : -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator() => _rows.GetEnumerator();

    int  IList.Add(object? value)          => throw new NotSupportedException();
    void IList.Insert(int index, object? v) => throw new NotSupportedException();
    void IList.Remove(object? value)        => throw new NotSupportedException();
    void IList.RemoveAt(int index)          => throw new NotSupportedException();
    void IList.Clear()                      => throw new NotSupportedException();
    void ICollection.CopyTo(Array array, int index) => throw new NotSupportedException();

    // ── IDataGridCollectionView: данные ───────────────────────────────────────
    public IEnumerable SourceCollection => this;
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;
    public bool IsEmpty => _rows.Count == 0;

    // Сортировка/фильтр/группировка средствами view не используются (см. док-комментарий).
    public Func<object, bool>? Filter { get; set; }
    public bool CanFilter => false;
    public DataGridSortDescriptionCollection SortDescriptions { get; } = new();
    public bool CanSort  => false;
    public bool CanGroup => false;
    public bool IsGrouping => false;
    public int  GroupingDepth => 0;
    public IAvaloniaReadOnlyList<object>? Groups => null;
    public string GetGroupingPropertyNameAtDepth(int level) => string.Empty;

    public void Refresh() => _rows.Reset();
    public IDisposable DeferRefresh() => new NoopScope();

    // ── IDataGridCollectionView: «текущая» позиция (курсор для навигации) ──────
    public object? CurrentItem =>
        _currentPosition >= 0 && _currentPosition < _rows.Count ? _rows[_currentPosition] : null;
    public int  CurrentPosition      => _currentPosition;
    public bool IsCurrentBeforeFirst => _currentPosition < 0;
    public bool IsCurrentAfterLast   => _currentPosition >= _rows.Count;

    public bool MoveCurrentToFirst()    => MoveCurrentToPosition(0);
    public bool MoveCurrentToLast()     => MoveCurrentToPosition(_rows.Count - 1);
    public bool MoveCurrentToNext()     => MoveCurrentToPosition(_currentPosition + 1);
    public bool MoveCurrentToPrevious() => MoveCurrentToPosition(_currentPosition - 1);

    public bool MoveCurrentTo(object? item)
    {
        var idx = IndexOf(item);
        return idx >= 0 ? MoveCurrentToPosition(idx) : MoveCurrentToPosition(-1);
    }

    public bool MoveCurrentToPosition(int position)
    {
        if (position < -1 || position > _rows.Count) return false;
        if (position != _currentPosition)
        {
            _currentPosition = position;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
        return _currentPosition >= 0 && _currentPosition < _rows.Count;
    }

    public event EventHandler? CurrentChanged;
    // Требуется интерфейсом; не поднимаем — у DataGridCurrentChangingEventArgs нет публичного
    // конструктора, а отмена смены «текущего» при редактировании для нас несущественна.
#pragma warning disable CS0067
    public event EventHandler<DataGridCurrentChangingEventArgs>? CurrentChanging;
#pragma warning restore CS0067
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private sealed class NoopScope : IDisposable { public void Dispose() { } }
}
