using System.Collections;
using System.Collections.Specialized;
using Avalonia.Threading;
using Serilog;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Logging;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Виртуальная коллекция строк таблицы с LRU-кешем страниц. Сетка обращается к строкам по
/// индексу, данные грузятся из базы постранично в фоне: в памяти не более <c>_maxPages</c> ×
/// <see cref="PageSize"/> строк, предел страниц — от ширины ОКНА КОЛОНОК (см. <see cref="RowPaging.MaxPages"/>).
///
/// Источник строк — <see cref="RowSource"/>: порядок таблицы (keyset и смещение по индексу
/// коллекции), сортировка по индексу значений поля (голова непустых значений плюс хвост без
/// значения) или готовый список id (фильтр, типизированная сортировка) — см. drawn-grid.md, этап 1.
///
/// Значения ячеек читаются только для окна колонок (<see cref="SetFieldWindow"/>); при прокрутке
/// вбок недостающие поля видимых страниц дочитываются на месте (<see cref="TableRowVm.MergeValues"/>).
/// <see cref="EnsureRange"/> заводит страницы видимого окна, дочитку и одну упреждающую страницу
/// в сторону прокрутки. Число строк может быть оценкой (снимок без COUNT) — оно уточняется краем
/// прочитанных страниц и точным счетом (<see cref="SetCount"/>).
/// </summary>
internal sealed class VirtualRowList : IList<TableRowVm>, IList, INotifyCollectionChanged
{
    private const int PageSize = RowPaging.PageSize;
    // До какой глубины смещение в голове сортировки пробуется без ее длины (скан индекса до этой
    // глубины дешевле ожидания счета).
    private const int ShallowSkip = 2_000;

    /// <summary>До скольких строк выделение диапазона читается СИНХРОННО, без фона
    /// (<c>TableView.CellSelection</c> берет значение отсюда). Кеш обязан вмещать такой диапазон,
    /// иначе синхронный путь пойдет читать строки из базы прямо на UI-потоке.</summary>
    internal const int SyncRangeRows = RowPaging.SyncRangeRows;

    private int _maxPages;

    private readonly ConstructorRepository _repo;
    private readonly int                   _collectionId;
    private List<DbField>                  _fields;          // меняется SetFields (свойство создано, удалено, сменило тип)
    private readonly bool                  _excludeHidden;   // исключать скрытые записи (окно «Отображение записей»)
    private readonly RowSource             _source;
    private readonly IReadOnlyList<int>?   _recordIds;       // режим списка id
    private readonly SortedRowSource?      _sorted;          // режим сортировки по индексу

    private readonly Dictionary<int, TableRowVm[]> _pages = [];
    private readonly LinkedList<int> _lru = new();
    // page → курсор последней/первой строки страницы: keyset-загрузка соседних страниц при
    // последовательной прокрутке (конец предыдущей → вперед; начало следующей → назад). Живут и
    // для вытесненных страниц — курсоры дешевы и ускоряют прокрутку по глубине.
    private readonly Dictionary<int, PageCursor> _pageEnd   = [];
    private readonly Dictionary<int, PageCursor> _pageStart = [];
    // page → поля, значения которых у страницы есть (null — все поля коллекции).
    private readonly Dictionary<int, int[]?> _pageFields = [];
    // Страницы, ожидающие отложенного заполнения (показывают «Загрузка…»).
    private readonly HashSet<int> _pending = [];
    // page → поля, которые сейчас дочитываются (чтобы не запрашивать дважды).
    private readonly Dictionary<int, HashSet<int>> _topping = [];
    // Поколение кеша: растет при Reset; фоновые загрузки со старым поколением отбрасываются.
    private int _generation;
    // Ограничитель параллельных фоновых чтений (защита от всплеска при быстром драге скролла).
    private readonly System.Threading.SemaphoreSlim _loadGate = new(4);

    private int  _dataCount;        // меняется при структурном обновлении и уточнении счетчика
    private bool _countIsEstimate;  // число строк — оценка: край уточняется прочитанными страницами

    // Окно колонок: id полей, значения которых читаются со страницей (null — все).
    private int[]? _window;
    private int    _lastFirstRow = -1;   // прошлое видимое окно — направление упреждающей подгрузки

    // Сортировка по индексу: длина головы (записей с непустым значением) и ее фоновый счет.
    private int?       _headCount;
    private Task<int>? _headCountTask;

    // Кеш связей-relation (батч-префетч видимой страницы вместо N точечных GetLinks) и id полей-связей
    // коллекции. null-кеш → префетч выключен (ячейки читают связи сами).
    private readonly RelationLinksCache? _relLinks;
    private int[] _relationFieldIds;

    // Курсор keyset: в порядке таблицы и в хвосте сортировки — (Position, Id); в голове сортировки —
    // (Value, Id) по индексу значений.
    private readonly record struct PageCursor(string? Value, int Pos, int Id, bool Tail);

    // Постоянный экземпляр add-row — сравнивается по ссылке при ScrollIntoView.
    public TableRowVm AddRowItem { get; } = TableRowVm.AddRow();

    public int Count => _dataCount + 1;  // +1 под add-row в конце

    /// <summary>Число записей без строки «+».</summary>
    public int DataCount => _dataCount;

    /// <summary>Число записей пока оценочное (уточняется в фоне).</summary>
    public bool CountIsEstimate => _countIsEstimate;

    /// <summary>Источник строк, по которому листается список.</summary>
    public RowSource Source => _source;

    /// <summary>Длина головы сортированного вида, если уже известна.</summary>
    public int? HeadCount => _headCount;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Пользователь отредактировал ячейку какой-то строки прямо в таблице (аргумент — RecordId).
    /// Ретранслируется из <see cref="TableRowVm.Edited"/> всех строк списка — для живой синхронизации
    /// открытой панели «Страница записи».</summary>
    public event EventHandler<int>? RecordEdited;

    // Подписать строку: ее правки (из сеттера ячейки) ретранслируем наружу как RecordEdited.
    // Заглушки «Загрузка…» тоже подписываем — после Fill это тот же экземпляр, подписка сохраняется.
    private TableRowVm Track(TableRowVm row)
    {
        row.Edited += (_, rid) => RecordEdited?.Invoke(this, rid);
        return row;
    }

    /// <param name="recordIds">Готовый список id (фильтр/сортировка) — то же, что <see cref="IdListRowSource"/>.</param>
    /// <param name="knownCount">Число записей из снимка открытия (посчитано в фоне); без него — из базы.</param>
    /// <param name="source">Источник строк; null — порядок таблицы или список <paramref name="recordIds"/>.</param>
    /// <param name="countIsEstimate"><paramref name="knownCount"/> — оценка, край уточняется страницами и <see cref="SetCount"/>.</param>
    public VirtualRowList(ConstructorRepository repo, int collectionId, List<DbField> fields,
                          bool excludeHidden = false, IReadOnlyList<int>? recordIds = null,
                          RelationLinksCache? relLinks = null, int? knownCount = null,
                          RowSource? source = null, bool countIsEstimate = false)
    {
        _repo          = repo;
        _collectionId  = collectionId;
        _fields        = fields;
        _excludeHidden = excludeHidden;
        _source        = source ?? (recordIds is not null ? new IdListRowSource(recordIds) : RowSource.Plain);
        _recordIds     = (_source as IdListRowSource)?.Ids;
        _sorted        = _source as SortedRowSource;
        _relLinks      = relLinks;
        _relationFieldIds = fields.Where(f => f.FieldType == "relation" && f.Id is not null)
                                  .Select(f => f.Id!.Value).ToArray();
        _maxPages      = RowPaging.MaxPages(fields.Count);
        // Режим списка id: длина списка. Иначе — из снимка открытия коллекции, а без него — из базы.
        _dataCount       = _recordIds?.Count ?? knownCount ?? repo.CountRecords(collectionId, excludeHidden);
        _countIsEstimate = _recordIds is null && knownCount is not null && countIsEstimate;
    }

    // ── Окно колонок и видимый диапазон ────────────────────────────────────────

    /// <summary>Окно колонок: id полей, значения которых читаются со страницей (null — все поля).
    /// Уже загруженные страницы дочитываются при следующем <see cref="EnsureRange"/>.</summary>
    public void SetFieldWindow(IReadOnlyList<int>? fieldIds)
    {
        if (fieldIds is null || fieldIds.Count >= _fields.Count)
        {
            _window   = null;
            _maxPages = RowPaging.MaxPages(_fields.Count);
            return;
        }
        var arr = fieldIds.Distinct().ToArray();
        Array.Sort(arr);
        _window   = arr;
        _maxPages = RowPaging.MaxPages(Math.Max(1, arr.Length));
    }

    /// <summary>Текущее окно колонок (null — все поля).</summary>
    public IReadOnlyList<int>? FieldWindow => _window;

    /// <summary>Набор полей коллекции сменился без пересоздания списка (свойство создано, удалено,
    /// сменило тип): окно колонок меряется новым числом полей, а связи новых полей-связей
    /// подчитываются для загруженных строк — ячейка рисованной сетки читает связи только из кеша, и
    /// без этого новая колонка-связь показывала бы «связи нет» даже после правки.</summary>
    public void SetFields(List<DbField> fields)
    {
        var before = _relationFieldIds;
        _fields           = fields;
        _relationFieldIds = fields.Where(f => f.FieldType == "relation" && f.Id is not null)
                                  .Select(f => f.Id!.Value).ToArray();
        SetFieldWindow(_window);

        var added = _relationFieldIds.Except(before).ToList();
        if (added.Count == 0 || _relLinks is null) return;
        var rows = _pages.Where(kv => !_pending.Contains(kv.Key))
                         .SelectMany(kv => kv.Value)
                         .Where(r => r.RecordId > 0)
                         .ToList();
        if (rows.Count == 0) return;
        PrimeRelationLinks(rows.Select(r => r.RecordId).ToList(), added);
        foreach (var row in rows) row.NotifyCellsChanged();
    }

    /// <summary>Видимое окно строк [first, last]: завести загрузку недостающих страниц, дочитать
    /// поля окна у загруженных, подгрузить одну страницу вперед по направлению прокрутки.
    /// Дешево — зовется на каждом сдвиге прокрутки.</summary>
    public void EnsureRange(int first, int last)
    {
        if (_dataCount == 0) return;
        first = Math.Clamp(first, 0, _dataCount - 1);
        last  = Math.Clamp(last, first, _dataCount - 1);
        var p0 = first / PageSize;
        var p1 = last  / PageSize;

        for (var p = p0; p <= p1; p++)
        {
            if (_pages.ContainsKey(p)) { Touch(p); TopUp(p); }
            else LoadPage(p);
        }

        var ahead = RowPaging.PrefetchPage(p0, p1, _lastFirstRow, first, RowPaging.PageCount(_dataCount));
        if (ahead is int a && !_pages.ContainsKey(a)) LoadPage(a);
        _lastFirstRow = first;
    }

    /// <summary>Ячейка (строка по индексу, поле) уже с данными: страница заполнена и поле входит в
    /// прочитанные — для стендов и проверок «все видимое пришло».</summary>
    public bool IsCellLoaded(int index, int fieldId)
    {
        if (index < 0 || index >= _dataCount) return true;
        var page = index / PageSize;
        if (!_pages.TryGetValue(page, out var rows) || _pending.Contains(page)) return false;
        if (index % PageSize >= rows.Length || rows[index % PageSize].IsLoading) return false;
        return !_pageFields.TryGetValue(page, out var loaded) || loaded is null || Array.IndexOf(loaded, fieldId) >= 0;
    }

    // ── Счетчик ───────────────────────────────────────────────────────────────

    /// <summary>Точное число записей пришло из фона: сменить протяженность, отбросить страницы за
    /// краем, укоротить страницу на краю. Заполненные страницы остаются теплыми.</summary>
    public void SetCount(int exact)
    {
        _countIsEstimate = false;
        if (exact == _dataCount) return;
        ResizeTo(exact);
    }

    // Новая длина списка: страницы целиком за краем — вон, страница на краю — укорачивается
    // (заглушки за краем никогда не заполнятся, а строк там нет); мягкий Reset для сетки.
    private void ResizeTo(int count)
    {
        Resets++;
        GridTimings.Record("смена длины списка", 0, $"{_dataCount:N0} → {count:N0}");
        _dataCount = Math.Max(0, count);
        foreach (var page in _pages.Keys.ToList())
        {
            var skip = page * PageSize;
            if (skip >= _dataCount) { DropPage(page); continue; }
            var rows = _pages[page];
            var take = Math.Min(PageSize, _dataCount - skip);
            if (rows.Length > take) _pages[page] = rows[..take];
        }
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    // Батч-префетч связей: по одному GetLinksBatch на каждое поле-связь ИЗ ОКНА вместо N точечных
    // GetLinks (по строке). Только UI-поток (общий _repo) — для синхронных путей (пересборка страниц,
    // точечное обновление записи); фоновые загрузки читают связи сами (PageLinks) и кладут их через
    // PrimeFromPage.
    private void PrimeRelationLinks(IReadOnlyList<int> recordIds, IReadOnlyList<int>? onlyFields)
    {
        if (_relLinks is null || _relationFieldIds.Length == 0 || recordIds.Count == 0) return;
        foreach (var fid in _relationFieldIds)
        {
            if (onlyFields is not null && !onlyFields.Contains(fid)) continue;
            _relLinks.Prime(fid, recordIds, _repo.GetLinksBatch(fid, recordIds));
        }
    }

    // Связи, прочитанные фоном вместе со страницей или дочиткой, — в табличный кеш.
    private void PrimeFromPage(IReadOnlyList<int> recordIds, ConstructorRepository.PageLinks links)
    {
        if (_relLinks is null || recordIds.Count == 0) return;
        foreach (var (fid, batch) in links) _relLinks.Prime(fid, recordIds, batch);
    }

    // Поля-связи среди запрошенных полей (null — все поля-связи коллекции); null, если связей нет.
    private int[]? RelationFieldsIn(IReadOnlyList<int>? fields)
    {
        if (_relLinks is null || _relationFieldIds.Length == 0) return null;
        if (fields is null) return _relationFieldIds;
        var hit = _relationFieldIds.Where(fields.Contains).ToArray();
        return hit.Length == 0 ? null : hit;
    }

    public TableRowVm this[int index]
    {
        get
        {
            if (index == _dataCount) return AddRowItem;

            var page = index / PageSize;
            var slot = index % PageSize;
            if (_pages.TryGetValue(page, out var rows)) Touch(page);
            else rows = LoadPage(page);
            return slot < rows.Length ? rows[slot] : AddRowItem;
        }
        set => throw new NotSupportedException();
    }

    // ── Загрузка страниц ──────────────────────────────────────────────────────

    /// <summary>
    /// Загружает страницу: создает строки-заглушки «Загрузка…» и грузит данные в ФОНОВОМ потоке
    /// (отдельное read-only подключение, WAL), затем на UI-потоке заполняет строки. UI-поток
    /// не трогает БД при прокрутке.
    /// </summary>
    private TableRowVm[] LoadPage(int page)
    {
        EvictOldestIfFull();

        var skip = page * PageSize;
        // При оценочной длине страница всегда полная: оценка могла оказаться меньше настоящего
        // числа строк, и укороченная страница уже не доросла бы. Лишние заглушки за настоящим краем
        // срежет ответ базы (ResizeTo в FillFromData).
        var take = _countIsEstimate ? PageSize : Math.Min(PageSize, _dataCount - skip);

        var placeholders = MakePlaceholders(page, skip, take);
        BeginBackgroundLoad(page, skip, take);
        return placeholders;
    }

    /// <summary>Сколько страниц заполнено данными за время жизни списка (для стендов).</summary>
    public int PagesFilled { get; private set; }

    /// <summary>Сколько раз список менял длину и слал Reset (для стендов).</summary>
    public int Resets { get; private set; }

    // Создает страницу-заглушку «Загрузка…», кладет в кеш и помечает ожидающей заполнения.
    private TableRowVm[] MakePlaceholders(int page, int skip, int take)
    {
        var ph = new TableRowVm[Math.Max(0, take)];
        for (var i = 0; i < ph.Length; i++) ph[i] = Track(TableRowVm.Loading(skip + i + 1));
        _pages[page] = ph;
        _pageFields[page] = _window;
        _lru.AddLast(page);
        _pending.Add(page);
        return ph;
    }

    // Стартует фоновую загрузку уже созданной страницы-заглушки: выбор записей собирается на
    // UI-потоке (без гонки с курсорами и списком id) и выполняется на изолированном репозитории.
    // Поколение фиксируем сейчас — если до прихода данных был Reset, результат отбрасывается.
    private void BeginBackgroundLoad(int page, int skip, int take)
    {
        PageCursor? after  = page > 0 && _pageEnd.TryGetValue(page - 1, out var cur) ? cur : null;
        PageCursor? before = after is null && _pageStart.TryGetValue(page + 1, out var nxt) ? nxt : null;
        var query = RecordsQuery(skip, take, after, before);
        _ = LoadPageBackgroundAsync(_generation, page, query, _window, RelationFieldsIn(_window));
    }

    // Фоновая загрузка страницы с ограничением параллелизма (семафор): при очень быстром
    // драге скроллбара не плодим сотни подключений/задач. Чтение — в фоне, заполнение — на UI.
    private async Task LoadPageBackgroundAsync(int gen, int page, Func<ConstructorRepository, ConstructorRepository.RecordPage> query,
                                               int[]? fields, int[]? relationFields)
    {
        await _loadGate.WaitAsync();
        try
        {
            if (gen != _generation) return;   // устарело еще до старта (был Reset)
            // Пока задача ждала слот, пользователь мог проскроллить дальше и страницу вытеснили
            // из кеша. Тогда не тратим слот/запрос — быстрее дойдет очередь до страницы, на которой
            // пользователь остановился. (Возврат на эту страницу запросит загрузку заново.)
            if (!_pending.Contains(page) || !_pages.ContainsKey(page)) return;
            // Порог медленности страницы: медленная подгрузка при прокрутке — первый признак
            // деградации базы (сеть, раздутый файл), и виден он здесь раньше жалобы.
            using var slow = Diag.Measure("страница таблицы", Diag.UiPage,
                                          $"{SmartBase.Utils.DbErrors.Display(_repo.DbPath)}, коллекция {_collectionId}, страница {page}");
            var tq = GridTimings.Start();
            var data = await _repo.LoadPageAsync(_collectionId, query, fields, relationFields);
            GridTimings.Stop(tq, "страница (фон, запрос)", $"страница {page}, {(_sorted is not null ? "сортировка" : _recordIds is not null ? "список id" : "порядок таблицы")}, полей {fields?.Length.ToString() ?? "все"}");
            Dispatcher.UIThread.Post(() => FillFromData(gen, page, data));
        }
        catch (Exception ex)
        {
            // Фоновая загрузка: ошибку проглатываем — строки останутся «Загрузка…» до повторного
            // запроса. Но «файл пропал» сигналит монитору доступности (DbErrors.HandleRead) —
            // иначе таблица висела бы в вечной «Загрузке…» вместо честного «база недоступна».
            SmartBase.Utils.DbErrors.HandleRead(ex, _repo.DbPath);
            if (!Database.DbHealthMonitor.IsUnavailableError(ex) && !Database.DbHealthMonitor.IsBusyError(ex)
                && Diag.Throttle("page-load-fail:" + _repo.DbPath, TimeSpan.FromMinutes(1)))
                Log.Warning(ex, "Страница таблицы не загрузилась: {Db:l}, коллекция {Collection}, страница {Page}",
                            SmartBase.Utils.DbErrors.Display(_repo.DbPath), _collectionId, page);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Заполняет ранее созданные строки-заглушки данными, пришедшими из фона (UI-поток).</summary>
    private void FillFromData(int gen, int page, ConstructorRepository.PageData data)
    {
        if (gen != _generation) return;                         // данные устарели (был Reset)
        if (!_pending.Remove(page)) return;                     // уже заполнена/сброшена
        if (!_pages.TryGetValue(page, out var rows)) return;    // вытеснена из кеша

        var t0 = GridTimings.Start();
        var records = data.Page.Records;
        if (data.Page.HeadCount is int h && _headCount is null) _headCount = h;

        // Оценочная длина: край выясняется прочитанными страницами — короткая страница значит
        // конец списка, полная страница на самом краю — за ней может быть еще одна.
        if (_countIsEstimate)
        {
            var skip = page * PageSize;
            if (records.Count < rows.Length)
            {
                ResizeTo(skip + records.Count);
                if (!_pages.TryGetValue(page, out rows!)) return;
            }
            else if (skip + rows.Length >= _dataCount)
            {
                GridTimings.Record("смена длины списка", 0, $"{_dataCount:N0} → {skip + rows.Length + PageSize:N0} (полная страница на краю)");
                _dataCount = skip + rows.Length + PageSize;
                Resets++;
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }
        PagesFilled++;

        // Строки-заглушки заполняем реальными данными; на будущее (редактирование ячеек)
        // отдаем ОСНОВНОЙ репозиторий — фоновый контекст использовался только для чтения.
        var ids = records.Select(r => r.Id!.Value).ToList();
        PrimeFromPage(ids, data.Links);
        for (var i = 0; i < records.Count && i < rows.Length; i++)
        {
            var rec    = records[i];
            var id     = rec.Id!.Value;
            var values = data.Values.TryGetValue(id, out var v) ? v : [];
            rows[i].SetStyle(data.CellStyles.GetValueOrDefault(id), notify: false);
            rows[i].Fill(_repo, id, values, rec.Title ?? "",
                         rec.CreatedAt ?? "", rec.UpdatedAt ?? "", rec.CreatedBy ?? "", rec.UpdatedBy ?? "");
        }

        RememberCursor(page, data.Page);
        TopUp(page);   // окно колонок могло сдвинуться, пока страница ехала
        GridTimings.Stop(t0, "заполнение страницы (UI)", $"страница {page}, строк {records.Count}");
    }

    // Дочитать поля окна, которых у загруженной страницы нет (прокрутка вбок после загрузки).
    private void TopUp(int page)
    {
        if (_window is null || _pending.Contains(page) || !_pages.TryGetValue(page, out var rows)) return;
        if (!_pageFields.TryGetValue(page, out var loaded) || loaded is null) return;   // все поля уже есть
        var missing = RowPaging.MissingFields(loaded, _window, _topping.GetValueOrDefault(page));
        if (missing is null) return;
        var ids = rows.Where(r => r.RecordId > 0).Select(r => r.RecordId).ToList();
        if (ids.Count == 0) return;

        if (!_topping.TryGetValue(page, out var inFlight)) _topping[page] = inFlight = [];
        inFlight.UnionWith(missing);
        _ = TopUpAsync(_generation, page, ids, missing);
    }

    private async Task TopUpAsync(int gen, int page, List<int> ids, int[] fields)
    {
        try
        {
            var t0 = GridTimings.Start();
            var data = await _repo.LoadValuesAsync(ids, fields, RelationFieldsIn(fields));
            GridTimings.Stop(t0, "дочитка колонок (фон, запрос)", $"страница {page}, полей {fields.Length}");
            Dispatcher.UIThread.Post(() => ApplyTopUp(gen, page, ids, fields, data));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => { if (_topping.TryGetValue(page, out var inFlight)) inFlight.ExceptWith(fields); });
            SmartBase.Utils.DbErrors.HandleRead(ex, _repo.DbPath);
            if (!Database.DbHealthMonitor.IsUnavailableError(ex) && !Database.DbHealthMonitor.IsBusyError(ex)
                && Diag.Throttle("page-topup-fail:" + _repo.DbPath, TimeSpan.FromMinutes(1)))
                Log.Warning(ex, "Дочитка колонок страницы не удалась: {Db:l}, коллекция {Collection}, страница {Page}",
                            SmartBase.Utils.DbErrors.Display(_repo.DbPath), _collectionId, page);
        }
    }

    // Дочитанные значения — в строки страницы (UI-поток).
    private void ApplyTopUp(int gen, int page, List<int> ids, int[] fields, ConstructorRepository.TopUpData data)
    {
        if (gen != _generation) return;
        if (_topping.TryGetValue(page, out var inFlight))
        {
            inFlight.ExceptWith(fields);
            if (inFlight.Count == 0) _topping.Remove(page);
        }
        if (!_pages.TryGetValue(page, out var rows) || _pending.Contains(page)) return;

        var t0 = GridTimings.Start();
        _pageFields[page] = RowPaging.Union(_pageFields.GetValueOrDefault(page), fields);
        PrimeFromPage(ids, data.Links);
        var touched = data.Links.Count > 0;   // связи дочитаны — ячейки-связи перечитают кеш даже без значений
        foreach (var row in rows)
        {
            if (row.RecordId <= 0) continue;
            if (data.Values.TryGetValue(row.RecordId, out var v)) row.MergeValues(v);
            else if (touched) row.NotifyCellsChanged();
        }
        GridTimings.Stop(t0, "дочитка колонок (UI)", $"страница {page}, полей {fields.Length}, значений {data.Values.Count}");
    }

    // ── Выбор записей страницы по режиму ──────────────────────────────────────

    // Выбор записей строк [skip, skip + take) для изолированного репозитория — по источнику и
    // курсорам. Собирается на UI-потоке (срез списка id, известные длины), исполняется в фоне.
    private Func<ConstructorRepository, ConstructorRepository.RecordPage> RecordsQuery(int skip, int take, PageCursor? after, PageCursor? before)
    {
        var cid    = _collectionId;
        var hidden = _excludeHidden;

        if (_recordIds is { } ids)
        {
            var slice = new List<int>(Math.Max(0, take));
            for (var k = 0; k < take && skip + k < ids.Count; k++) slice.Add(ids[skip + k]);
            return repo => new ConstructorRepository.RecordPage(repo.GetRecordsByIdsOrdered(slice));
        }

        if (_sorted is { } sorted)
        {
            var head  = _headCount;
            var total = _countIsEstimate ? (int?)null : _dataCount;
            var task  = head is null ? EnsureHeadCountTask() : null;
            return repo => SortedPage(repo, sorted, cid, hidden, skip, take, after, before, head, task, total);
        }

        var exactTotal = _countIsEstimate ? (int?)null : _dataCount;
        if (after is { } a)
            return repo => new ConstructorRepository.RecordPage(repo.GetRecordsPageAfter(cid, a.Pos, a.Id, take, hidden));
        if (before is { } b)
            return repo => new ConstructorRepository.RecordPage(repo.GetRecordsPageBefore(cid, b.Pos, b.Id, take, hidden));
        return repo => new ConstructorRepository.RecordPage(repo.GetRecordsPage(cid, skip, take, hidden, exactTotal));
    }

    // Фоновый счет длины головы сортированного вида — заводится один раз, страницы, которым он
    // нужен (прыжок смещением), ждут его в фоне.
    private Task<int> EnsureHeadCountTask() =>
        _headCountTask ??= _repo.CountNonEmptyValuesAsync(_collectionId, _sorted!.FieldId, _excludeHidden);

    // Страница сортированного вида: голова (непустые значения по индексу) переходит в хвост (без
    // значения, порядок таблицы). Последовательная прокрутка не ждет счета головы: ее конец
    // выясняется по короткому ответу keyset-запроса. Прыжок смещением ждет счет (в фоне).
    private static ConstructorRepository.RecordPage SortedPage(ConstructorRepository repo, SortedRowSource s, int cid, bool hidden,
                                                               int skip, int take, PageCursor? after, PageCursor? before,
                                                               int? head, Task<int>? headTask, int? total)
    {
        var records = new List<DbRecord>(Math.Max(0, take));
        var values  = new List<string?>(Math.Max(0, take));
        if (take <= 0) return new ConstructorRepository.RecordPage(records, [], head);

        void AddHead(ConstructorRepository.RecordPage p)
        {
            records.AddRange(p.Records);
            for (var i = 0; i < p.Records.Count; i++) values.Add(p.SortValues?[i] ?? "");
        }
        void AddTail(List<DbRecord> rows)
        {
            records.AddRange(rows);
            for (var i = 0; i < rows.Count; i++) values.Add(null);
        }

        if (after is { } a)
        {
            if (!a.Tail)
            {
                var h = repo.GetRecordsSortedHead(cid, s.FieldId, s.Asc, hidden, take, a.Value, a.Id, null, null);
                AddHead(h);
                if (h.Records.Count < take)
                {
                    head = skip + h.Records.Count;   // до этой страницы — только голова, здесь она кончилась
                    AddTail(repo.GetRecordsWithoutValue(cid, s.FieldId, hidden, take - h.Records.Count,
                                                        null, null, null, null, skip: 0, tailCount: null, headCount: head));
                }
            }
            else
                AddTail(repo.GetRecordsWithoutValue(cid, s.FieldId, hidden, take, a.Pos, a.Id, null, null));
        }
        else if (before is { } b)
        {
            if (b.Tail)
            {
                var t = repo.GetRecordsWithoutValue(cid, s.FieldId, hidden, take, null, null, b.Pos, b.Id);
                if (t.Count < take)
                {
                    // Перед началом хвоста — конец головы: последние k ее строк со смещением с конца.
                    var k = take - t.Count;
                    head = skip + k;
                    AddHead(repo.GetRecordsSortedHead(cid, s.FieldId, s.Asc, hidden, k, null, null, null, null,
                                                      skip: head.Value - k, headCount: head));
                }
                AddTail(t);
            }
            else
                AddHead(repo.GetRecordsSortedHead(cid, s.FieldId, s.Asc, hidden, take, null, null, b.Value, b.Id));
        }
        else
        {
            if (head is null && skip <= ShallowSkip)
            {
                // Длина головы еще не сочтена, смещение мелкое: пробуем прямо в голове — полная
                // страница значит, что до счета дело не дошло; короткая выдает конец головы сама.
                // Пустой ответ при skip > 0 (страница целиком в хвосте) требует дождаться счета.
                var h = repo.GetRecordsSortedHead(cid, s.FieldId, s.Asc, hidden, take, null, null, null, null, skip: skip);
                if (h.Records.Count == take) { AddHead(h); return new ConstructorRepository.RecordPage(records, values.ToArray(), null); }
                if (h.Records.Count > 0 || skip == 0)
                {
                    head = skip + h.Records.Count;
                    AddHead(h);
                    AddTail(repo.GetRecordsWithoutValue(cid, s.FieldId, hidden, take - h.Records.Count,
                                                        null, null, null, null, skip: 0, tailCount: null, headCount: head));
                    return new ConstructorRepository.RecordPage(records, values.ToArray(), head);
                }
            }
            // Глубокое смещение без длины головы: ждем счет (в фоне) — иначе пробное смещение вперед
            // прошло бы весь индекс (9 млн записей — 4 с), а с длиной оно идет с ближнего края за мс.
            head ??= headTask is not null ? headTask.GetAwaiter().GetResult()
                                          : repo.CountNonEmptyValues(cid, s.FieldId, hidden);
            var (headTake, tailSkip, tailTake) = RowPaging.SplitSorted(skip, take, head.Value);
            if (headTake > 0)
                AddHead(repo.GetRecordsSortedHead(cid, s.FieldId, s.Asc, hidden, headTake, null, null, null, null,
                                                  skip: skip, headCount: head));
            if (tailTake > 0)
                AddTail(repo.GetRecordsWithoutValue(cid, s.FieldId, hidden, tailTake, null, null, null, null,
                                                    skip: tailSkip, tailCount: total is int n ? n - head : null, headCount: head));
        }

        return new ConstructorRepository.RecordPage(records, values.ToArray(), head);
    }

    // Строит реальные строки из записей + их значений (теплая пересборка страниц, RebuildPageSync).
    private TableRowVm[] BuildRows(int skip, ConstructorRepository.RecordPage page)
    {
        var records = page.Records;
        var ids     = records.Select(r => r.Id!.Value).ToList();
        var valMap  = _repo.GetFieldValuesBatch(ids, _window);
        var cellMap = _repo.GetCellStylesBatch(_collectionId, ids);
        PrimeRelationLinks(ids, _window);

        var rows = new TableRowVm[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            var rec    = records[i];
            var id     = rec.Id!.Value;
            var values = valMap.TryGetValue(id, out var v) ? v : [];
            rows[i]    = Track(new TableRowVm(_repo, id, skip + i + 1, values, rec.Title ?? "",
                                              rec.CreatedAt ?? "", rec.UpdatedAt ?? "", rec.CreatedBy ?? "", rec.UpdatedBy ?? ""));
            rows[i].SetStyle(cellMap.GetValueOrDefault(id), notify: false);
        }
        RememberCursor(skip / PageSize, page);
        return rows;
    }

    // Сохраняет курсоры крайних строк страницы — keyset соседних страниц (конец → следующая вперед,
    // начало → предыдущая назад при прокрутке вверх). В голове сортировки курсор — значение и id.
    private void RememberCursor(int page, ConstructorRepository.RecordPage data)
    {
        var records = data.Records;
        if (records.Count == 0) return;
        _pageStart[page] = CursorOf(records[0],  data.SortValues?[0]);
        _pageEnd[page]   = CursorOf(records[^1], data.SortValues?[^1]);
    }

    private PageCursor CursorOf(DbRecord r, string? sortValue) =>
        _sorted is null
            ? new PageCursor(null, r.Position, r.Id!.Value, false)
            : sortValue is null
                ? new PageCursor(null, r.Position, r.Id!.Value, Tail: true)
                : new PageCursor(sortValue, r.Position, r.Id!.Value, Tail: false);

    // Страница, к которой обратились, уезжает в хвост LRU. Без этого список был FIFO:
    // порядок задавала только загрузка, и страница, на которой пользователь стоит, вылетала
    // из кеша просто потому, что загружена давно.
    private void Touch(int page)
    {
        var node = _lru.Last;
        while (node is not null && node.Value != page) node = node.Previous;
        if (node is null || node == _lru.Last) return;
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    private void EvictOldestIfFull()
    {
        if (_pages.Count < _maxPages) return;
        var oldest = _lru.First!.Value;
        _lru.RemoveFirst();
        _pages.Remove(oldest);
        _pageFields.Remove(oldest);
        _topping.Remove(oldest);
        _pending.Remove(oldest);   // вытесненную страницу ее фоновая загрузка пропустит (см. выше)
    }

    /// <summary>Уведомить ВСЕ загруженные строки, что их ячейки надо перечитать — для правок,
    /// касающихся всей таблицы сразу (цвет колонки). Данных не трогает, загрузку не инициирует.</summary>
    public void NotifyAllCachedRows()
    {
        foreach (var rows in _pages.Values)
            foreach (var row in rows)
                row.NotifyCellsChanged();
    }

    /// <summary>Каждая строка загруженных страниц (заглушки «Загрузка…» тоже) — для правок, которые
    /// уже записаны в базу и должны дойти до кеша без перечитывания (оформление колонки или строки).</summary>
    public void ForEachCachedRow(Action<TableRowVm> action)
    {
        foreach (var rows in _pages.Values)
            foreach (var row in rows)
                action(row);
    }

    /// <summary>Ищет строку по RecordId в уже загруженных страницах (не инициирует загрузку).</summary>
    public TableRowVm? FindCachedByRecordId(int recordId)
    {
        foreach (var rows in _pages.Values)
            foreach (var row in rows)
                if (row.RecordId == recordId) return row;
        return null;
    }

    /// <summary>Строка по позиции, если ее страница уже загружена и заполнена; загрузку не
    /// заводит (в отличие от индексатора) — для проверок, которые идут на каждой раскладке.</summary>
    public TableRowVm? TryGetCached(int index)
    {
        if (index < 0 || index >= _dataCount) return null;
        var slot = index % PageSize;
        return _pages.TryGetValue(index / PageSize, out var rows) && slot < rows.Length
               && rows[slot] is { IsLoading: false } row
            ? row
            : null;
    }

    /// <summary>
    /// Строки с данными в диапазоне позиций [start, start + count) — для операций над выделенными
    /// ячейками (копирование, вставка, очистка), которым нужны значения и id ВСЕХ строк диапазона,
    /// а не только прокрученных. Загруженные страницы берутся из кеша; недостающие читаются из
    /// базы синхронно и в кеш НЕ кладутся: на месте страницы, которая сейчас грузится в фоне, грид
    /// уже держит заглушки «Загрузка…», и подмена массива оставила бы их незаполненными навсегда.
    /// Прочитанные так строки — только для чтения значений и id: правка через них в таблицу не
    /// попадет (после записи таблица перечитывает строки).
    ///
    /// Индексатор для этого не годится: на каждой незагруженной странице он стартует фоновую
    /// загрузку, и обход столбца большой коллекции завел бы тысячи загрузок подряд.
    /// </summary>
    public IReadOnlyList<TableRowVm> MaterializeRange(int start, int count) => MaterializeRange(start, count, null);

    /// <summary>То же, но с проверкой окна колонок: страница из кеша годится, только если у нее
    /// прочитаны все <paramref name="fieldIds"/> (при окне колонок в строках лежат значения лишь
    /// видимых полей) — иначе строки читаются из базы целиком, мимо кеша.</summary>
    public IReadOnlyList<TableRowVm> MaterializeRange(int start, int count, IReadOnlyList<int>? fieldIds)
    {
        var end    = Math.Min((long)start + count, _dataCount);
        var result = new List<TableRowVm>((int)Math.Max(0, end - start));

        var index = Math.Max(0, start);
        while (index < end)
        {
            var page = index / PageSize;
            var skip = page * PageSize;
            var take = Math.Min(PageSize, _dataCount - skip);

            var rows = _pages.TryGetValue(page, out var cached) && !_pending.Contains(page) && PageHasFields(page, fieldIds)
                ? cached
                : ReadDetachedRows(skip, take);

            for (var i = index - skip; i < take && skip + i < end && i < rows.Length; i++)
                result.Add(rows[i]);

            index = skip + take;
        }
        return result;
    }

    /// <summary>
    /// То же, что <see cref="MaterializeRange"/>, но для большого диапазона: чтение идет в фоне
    /// порциями на отдельном подключении, с прогрессом и отменой, и берет значения только
    /// <paramref name="fieldIds"/> — операции над выделением больше не нужно. Кеш не трогается:
    /// строки-результаты отдельные, только для чтения.
    ///
    /// Порции идут keyset'ом от последней строки предыдущей (в порядке таблицы и в сортированном
    /// виде) и срезами списка id (при фильтре): смещение на каждой порции стоило бы O(глубины).
    /// </summary>
    public async Task<IReadOnlyList<TableRowVm>> MaterializeRangeAsync(int start, int count, IReadOnlyList<int> fieldIds,
                                                                     IProgress<double>? progress, CancellationToken ct)
    {
        const int Chunk = 2000;

        var end    = (int)Math.Min((long)start + count, _dataCount);
        var from   = Math.Max(0, start);
        var result = new List<TableRowVm>(Math.Max(0, end - from));

        PageCursor? cursor = null;
        for (var index = from; index < end;)
        {
            ct.ThrowIfCancellationRequested();
            var take  = Math.Min(Chunk, end - index);
            var query = RecordsQuery(index, take, cursor, null);
            var rows  = await _repo.ReadCellRowsAsync(_collectionId, query, fieldIds, ct);
            if (rows.Count == 0) break;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                result.Add(new TableRowVm(_repo, row.RecordId, index + i + 1, row.Values, row.Title,
                                          row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy));
            }

            var last = rows[^1];
            cursor = _recordIds is not null ? null
                : _sorted is null ? new PageCursor(null, last.Position, last.RecordId, false)
                : last.SortValue is null ? new PageCursor(null, last.Position, last.RecordId, Tail: true)
                : new PageCursor(last.SortValue, last.Position, last.RecordId, Tail: false);
            index += take;
            progress?.Report((double)(index - from) / Math.Max(1, end - from));
        }

        return result;
    }

    // Все ли нужные поля у страницы прочитаны (null у страницы — все поля; null в запросе — годится любая).
    private bool PageHasFields(int page, IReadOnlyList<int>? fieldIds)
    {
        if (fieldIds is null || fieldIds.Count == 0) return true;
        if (!_pageFields.TryGetValue(page, out var loaded) || loaded is null) return true;
        foreach (var f in fieldIds)
            if (Array.IndexOf(loaded, f) < 0) return false;
        return true;
    }

    // Строки страницы прямо из базы, мимо кеша и без подписок: см. MaterializeRange.
    private TableRowVm[] ReadDetachedRows(int skip, int take)
    {
        var records = LoadRecordsByOffset(skip, take).Records;
        var valMap  = _repo.GetFieldValuesBatch(records.Select(r => r.Id!.Value).ToList());

        var rows = new TableRowVm[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            var id  = rec.Id!.Value;
            rows[i] = new TableRowVm(_repo, id, skip + i + 1, valMap.TryGetValue(id, out var v) ? v : [],
                                     rec.Title ?? "", rec.CreatedAt ?? "", rec.UpdatedAt ?? "",
                                     rec.CreatedBy ?? "", rec.UpdatedBy ?? "");
        }
        return rows;
    }

    /// <summary>Перечитывает данные ОДНОЙ записи и обновляет ее строку НА МЕСТЕ (если она сейчас в
    /// кеше) — для точечного обновления после правки записи в правой панели, без пересоздания списка
    /// и мелькания «Загрузка…». Строки нет в кеше (прокручена) → ничего не делаем (загрузится свежей).</summary>
    public void RefreshRecord(int recordId)
    {
        var row = FindCachedByRecordId(recordId);
        if (row is null) return;

        var rec = _repo.GetRecord(recordId);
        if (rec is null) return;                       // запись удалена — оставляем как есть до Reset

        var values = _repo.GetFieldValues(recordId);
        var cells  = _repo.GetCellStylesBatch(_collectionId, [recordId]);
        PrimeRelationLinks([recordId], null);   // связи записи могли измениться (правка в панели записи)
        row.SetStyle(cells.GetValueOrDefault(recordId), notify: false);
        row.Fill(_repo, recordId, values, rec.Title ?? "",
                 rec.CreatedAt ?? "", rec.UpdatedAt ?? "", rec.CreatedBy ?? "", rec.UpdatedBy ?? "");
    }

    /// <summary>Связи записи изменены правкой ячейки: редактор связи пишет их сам и выбрасывает запись из
    /// кеша, а рисованная ячейка читает только кеш — перечитываем связи этой записи и перерисовываем
    /// ее строку. Записи нет в кеше строк — ничего (прочитается при загрузке).</summary>
    public void RefreshLinks(int recordId)
    {
        if (_relLinks is null || _relationFieldIds.Length == 0 || FindCachedByRecordId(recordId) is not { } row) return;
        PrimeRelationLinks([recordId], null);
        row.NotifyCellsChanged();
    }

    /// <summary>Запись <paramref name="targetId"/> изменилась (напр., переименована в панели): строки,
    /// чьи связи ССЫЛАЮТСЯ на нее, показывают устаревшее название из батч-кеша. Пере-праймим связи этих
    /// строк свежими названиями и уведомляем — их read-ячейки-связи перечитают показ. Точечно, без Reset
    /// и без мелькания «Загрузка…»; если на targetId никто из загруженных строк не ссылается — ничего.</summary>
    public void RefreshRelationTargets(int targetId)
    {
        if (_relLinks is null || _relationFieldIds.Length == 0) return;

        // Ссылающиеся строки ищем по ТЕКУЩЕМУ кешу: id целей в нем верны, устарел лишь Title.
        var referencing = new List<TableRowVm>();
        foreach (var rows in _pages.Values)
            foreach (var row in rows)
            {
                if (row.RecordId <= 0) continue;
                if (_relationFieldIds.Any(fid => _relLinks.TryGet(fid, row.RecordId, out var links)
                                                 && links.Any(l => l.Id == targetId)))
                    referencing.Add(row);
            }
        if (referencing.Count == 0) return;

        PrimeRelationLinks(referencing.Select(r => r.RecordId).ToList(), null);   // свежие названия целей
        foreach (var row in referencing) row.NotifyCellsChanged();
    }

    /// <summary>Перечитывает ЗАГРУЖЕННЫЕ страницы из БД и обновляет строки НА МЕСТЕ (Fill), без Reset —
    /// для операций, меняющих ПОРЯДОК записей, но не их число (сдвиг вверх/вниз): нет мелькания
    /// «Загрузка…». Новые страницы не грузим; число записей не меняется. Синхронно (страниц в кеше
    /// немного — видимые + соседние), читаем смещением по индексу.</summary>
    public void RefreshLoadedPages()
    {
        foreach (var page in _pages.Keys.ToList())
        {
            if (_pending.Contains(page)) continue;                 // еще грузится — придет свежей
            if (!_pages.TryGetValue(page, out var rows)) continue;

            var skip    = page * PageSize;
            var data    = LoadRecordsByOffset(skip, rows.Length);
            var records = data.Records;
            var pageIds = records.Select(r => r.Id!.Value).ToList();
            var valMap  = _repo.GetFieldValuesBatch(pageIds, _pageFields.GetValueOrDefault(page));
            var cellMap = _repo.GetCellStylesBatch(_collectionId, pageIds);
            PrimeRelationLinks(pageIds, _pageFields.GetValueOrDefault(page));

            for (var i = 0; i < records.Count && i < rows.Length; i++)
            {
                var rec    = records[i];
                var id     = rec.Id!.Value;
                var values = valMap.TryGetValue(id, out var v) ? v : [];
                rows[i].SetStyle(cellMap.GetValueOrDefault(id), notify: false);
                rows[i].Fill(_repo, id, values, rec.Title ?? "",
                         rec.CreatedAt ?? "", rec.UpdatedAt ?? "", rec.CreatedBy ?? "", rec.UpdatedBy ?? "");
            }
            RememberCursor(page, data);
        }
    }

    // Записи страницы строго смещением (без keyset-курсора) на ОБЩЕМ репозитории — для точечного
    // пере-Fill и синхронного чтения диапазона. В сортированном виде смещению нужна длина головы:
    // берем известную, ждем фоновый счет или считаем здесь (общий контекст, UI-поток).
    private ConstructorRepository.RecordPage LoadRecordsByOffset(int skip, int take)
    {
        if (_sorted is not null && _headCount is null)
            _headCount = _headCountTask is { IsCompletedSuccessfully: true } t ? t.Result
                       : _repo.CountNonEmptyValues(_collectionId, _sorted.FieldId, _excludeHidden);
        return RecordsQuery(skip, take, null, null)(_repo);
    }

    /// <summary>Структурное обновление (создание/удаление/дубль записей): ставит число строк и
    /// СИНХРОННО перестраивает уже загруженные страницы реальными данными (+ страницу 0 — после Reset
    /// грид может прокрутиться в начало), затем шлет Reset. Кеш остается ТЕПЛЫМ, поэтому грид
    /// перерисовывается сразу из него, без заглушек «Загрузка…» — это и убирает мелькание. Применяется
    /// только вне режима списка id (там список пересобирает полный ReloadRows).
    /// <paramref name="count"/> — число строк: точное (вызывающий знает, сколько записей добавилось
    /// или ушло) или оценка (<paramref name="estimate"/>), которую уточнит фоновый счет через
    /// <see cref="SetCount"/>. Считать здесь нельзя: COUNT на 10 млн записей — почти секунда UI-потока
    /// после каждой правки.</summary>
    public void RefreshStructural(int count, bool estimate)
    {
        _dataCount       = _recordIds?.Count ?? Math.Max(0, count);
        _countIsEstimate = estimate && _recordIds is null;
        _headCount       = null;   // состав записей сменился — длина головы сортировки тоже
        _headCountTask   = null;

        // Курсоры keyset устарели (набор/порядок записей сдвинулся) — сбрасываем, чтобы незагруженные
        // страницы грузились смещением; перестроенные ниже заново проставят свои (BuildRows).
        _pageEnd.Clear();
        _pageStart.Clear();

        // Страницы целиком вне нового диапазона — выкидываем; остальные перестраиваем реальными строками.
        foreach (var page in _pages.Keys.ToList())
        {
            if (page * PageSize >= _dataCount) DropPage(page);
            else                               RebuildPageSync(page);
        }
        // Прогреваем страницу 0 на случай прокрутки в начало после Reset — чтобы и там были данные.
        if (_dataCount > 0 && !_pages.ContainsKey(0)) RebuildPageSync(0);

        CollectionChanged?.Invoke(this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    // Перестраивает страницу реальными строками синхронно (смещение по индексу) — для теплого кеша.
    private void RebuildPageSync(int page)
    {
        var skip = page * PageSize;
        var take = Math.Min(PageSize, _dataCount - skip);
        if (take <= 0) { DropPage(page); return; }

        _pages[page]      = BuildRows(skip, LoadRecordsByOffset(skip, take));   // BuildRows ставит и курсор страницы
        _pageFields[page] = _window;
        _pending.Remove(page);
        _topping.Remove(page);
        if (!_lru.Contains(page)) _lru.AddLast(page);
    }

    private void DropPage(int page)
    {
        _pages.Remove(page);
        _pageFields.Remove(page);
        _topping.Remove(page);
        _lru.Remove(page);
        _pending.Remove(page);
        _pageEnd.Remove(page);
        _pageStart.Remove(page);
    }

    /// <summary>Сбрасывает кеш и сигнализирует сетке о полной перестройке.</summary>
    public void Reset()
    {
        _generation++;          // отбрасываем фоновые загрузки и дочитки, начатые до сброса
        _pages.Clear();
        _pageFields.Clear();
        _topping.Clear();
        _lru.Clear();
        _pageEnd.Clear();
        _pageStart.Clear();
        _pending.Clear();
        _headCount     = null;  // правка значения в колонке сортировки меняет длину головы
        _headCountTask = null;
        CollectionChanged?.Invoke(this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    // ── IList<T> / IList ──────────────────────────────────────────────────────
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
