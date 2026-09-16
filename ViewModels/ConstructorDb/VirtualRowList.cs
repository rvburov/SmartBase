using System.Collections;
using System.Collections.Specialized;
using Avalonia.Threading;
using Serilog;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Logging;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Виртуальная коллекция строк таблицы с LRU-кешем страниц.
/// DataGrid обращается к элементам по индексу через IList, при этом
/// данные загружаются из БД постранично — в памяти хранится не более
/// <c>_maxPages</c> × <see cref="PageSize"/> строк одновременно, а сам предел страниц
/// зависит от числа колонок (см. <see cref="CellBudget"/>).
/// </summary>
internal sealed class VirtualRowList : IList<TableRowVm>, IList, INotifyCollectionChanged
{
    private const int PageSize = 200;

    // Сколько страниц держим в кеше. Считается от ШИРИНЫ коллекции, а не константой: строка
    // несет значения ВСЕХ своих колонок (GetFieldValuesBatch не фильтрует по видимым), поэтому
    // при 1000 свойствах прежние жесткие 50 страниц = 10 000 строк — это до миллионов записей
    // словаря в памяти, в сотню раз больше, чем на обычной таблице в десяток колонок. Бюджет
    // считаем в ЯЧЕЙКАХ: CellBudget / (колонок × PageSize), но не больше прежних 50 страниц
    // (узкие таблицы ведут себя ровно как раньше) и не меньше MinPages.
    //
    // Важно понимать предел этой меры: MinPages сам по себе стоит примерно 180 колонок, и на
    // более широких таблицах бюджет в ячейках уже не соблюдается — объем снова растет линейно
    // по числу колонок. Это сознательный размен: урезать кеш ниже SyncRangeRows нельзя, иначе
    // выделение диапазона пойдет читать строки на UI-потоке, то есть фриз просто переедет из
    // памяти в задержку. Настоящее лечение широких таблиц — не в кеше, а в UI (см. ROADMAP,
    // «Что ломается на 1000 полей»).
    private const int CellBudget = 400_000;
    private const int MaxPagesCap = 50;

    /// <summary>До скольких строк выделение диапазона читается СИНХРОННО, без фона
    /// (<c>TableView.CellSelection</c> берет значение отсюда). Живет рядом с кешем, потому что
    /// связан с ним жестко: кеш обязан вмещать такой диапазон, иначе синхронный путь пойдет
    /// читать строки из базы прямо на UI-потоке — на широкой таблице это секунды фриза.</summary>
    internal const int SyncRangeRows = 2_000;

    // Нижняя граница числа страниц — из SyncRangeRows: диапазон не выровнен по границе
    // страницы, поэтому нужен еще один запас сверху от деления.
    private const int MinPages = SyncRangeRows / PageSize + 1;

    private readonly int _maxPages;

    private readonly ConstructorRepository _repo;
    private readonly int                   _collectionId;
    private readonly List<DbField>         _fields;
    private readonly bool                  _excludeHidden;   // исключать скрытые записи (окно «Отображение записей»)
    private readonly Dictionary<int, TableRowVm[]> _pages = [];
    private readonly LinkedList<int> _lru = new();
    // page → (Position, Id) последней/первой строки страницы: курсоры keyset-загрузки соседних
    // страниц при последовательном скролле (конец предыдущей → вперед; начало следующей → назад,
    // скролл ВВЕРХ, в т.ч. после прыжка вглубь — иначе каждая страница вверх шла бы offset'ом
    // O(глубины)). Храним даже для вытесненных из _pages страниц (это всего пара int) —
    // курсоры дешевы и ускоряют скролл по глубине.
    private readonly Dictionary<int, (int Pos, int Id)> _pageEnd   = [];
    private readonly Dictionary<int, (int Pos, int Id)> _pageStart = [];
    // Страницы, ожидающие отложенного заполнения (показывают «Загрузка…»).
    private readonly HashSet<int> _pending = [];
    // Поколение кеша: растет при Reset; фоновые загрузки со старым поколением отбрасываются.
    private int _generation;
    // Ограничитель параллельных фоновых чтений (защита от всплеска при быстром драге скролла).
    private readonly System.Threading.SemaphoreSlim _loadGate = new(4);
    // Режим списка id (фильтр/сортировка): если задан — страницы берутся срезом этого списка
    // и грузятся по id в его порядке, а не keyset/offset по Position. null — обычный режим.
    private readonly IReadOnlyList<int>? _recordIds;
    private int _dataCount;   // меняется при структурном обновлении (создание/удаление/дубль записей)
    // Кеш связей-relation (батч-префетч видимой страницы вместо N точечных GetLinks) и id полей-связей
    // коллекции. null-кеш → префетч выключен (ячейки читают связи сами).
    private readonly RelationLinksCache? _relLinks;
    private readonly int[] _relationFieldIds;

    // Постоянный экземпляр add-row — DataGrid сравнивает по ссылке при ScrollIntoView.
    public TableRowVm AddRowItem { get; } = TableRowVm.AddRow();

    public int Count => _dataCount + 1;  // +1 под add-row в конце

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

    public VirtualRowList(ConstructorRepository repo, int collectionId, List<DbField> fields,
                          bool excludeHidden = false, IReadOnlyList<int>? recordIds = null,
                          RelationLinksCache? relLinks = null, int? knownCount = null)
    {
        _repo          = repo;
        _collectionId  = collectionId;
        _fields        = fields;
        _excludeHidden = excludeHidden;
        _recordIds     = recordIds;
        _relLinks      = relLinks;
        _relationFieldIds = fields.Where(f => f.FieldType == "relation" && f.Id is not null)
                                  .Select(f => f.Id!.Value).ToArray();
        _maxPages      = Math.Clamp(CellBudget / (Math.Max(1, fields.Count) * PageSize),
                                    MinPages, MaxPagesCap);
        // Режим списка id (фильтр/сортировка): длина списка. Иначе — из снимка открытия
        // коллекции (посчитан в фоне), а без него — считаем из БД.
        _dataCount     = recordIds?.Count ?? knownCount ?? repo.CountRecords(collectionId, excludeHidden);
    }

    // Батч-префетч связей видимой страницы: по одному GetLinksBatch на каждое поле-связь вместо N
    // точечных GetLinks (по строке). Только UI-поток (общий _repo) — зовем из путей наполнения строк.
    private void PrimeRelationLinks(IReadOnlyList<int> recordIds)
    {
        if (_relLinks is null || _relationFieldIds.Length == 0 || recordIds.Count == 0) return;
        foreach (var fid in _relationFieldIds)
            _relLinks.Prime(fid, recordIds, _repo.GetLinksBatch(fid, recordIds));
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
            return rows[slot];
        }
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Загружает страницу: создает строки-заглушки «Загрузка…» и грузит данные в ФОНОВОМ потоке
    /// (отдельное read-only подключение, WAL), затем на UI-потоке заполняет строки. UI-поток
    /// не трогает БД при скролле → ноль микроподвисаний даже при экстремально быстром скролле.
    /// </summary>
    private TableRowVm[] LoadPage(int page)
    {
        EvictOldestIfFull();

        var skip = page * PageSize;
        var take = Math.Min(PageSize, _dataCount - skip);

        var placeholders = MakePlaceholders(page, skip, take);
        BeginBackgroundLoad(page, skip, take);
        return placeholders;
    }

    // Создает страницу-заглушку «Загрузка…», кладет в кеш и помечает ожидающей заполнения.
    private TableRowVm[] MakePlaceholders(int page, int skip, int take)
    {
        var ph = new TableRowVm[take];
        for (var i = 0; i < take; i++) ph[i] = Track(TableRowVm.Loading(skip + i + 1));
        _pages[page] = ph;
        _lru.AddLast(page);
        _pending.Add(page);
        return ph;
    }

    // Стартует фоновую загрузку уже созданной страницы-заглушки: параметры запроса вычисляем
    // на UI-потоке (без гонки с _pageEnd/_recordIds). Поколение фиксируем сейчас — если до прихода
    // данных был Reset (правка/перезагрузка), результат отбрасывается (иначе заполнили бы НОВЫЕ
    // заглушки СТАРЫМИ данными). Общий путь для обычной async-подгрузки и дозагрузки дубля.
    private void BeginBackgroundLoad(int page, int skip, int take)
    {
        List<int>? idSlice = null;
        int? afterPos = null, afterId = null, beforePos = null, beforeId = null;
        if (_recordIds is not null)
        {
            idSlice = new List<int>(take);
            for (var k = 0; k < take; k++) idSlice.Add(_recordIds[skip + k]);
        }
        else if (page > 0 && _pageEnd.TryGetValue(page - 1, out var cur))
        {
            afterPos = cur.Pos; afterId = cur.Id;
        }
        else if (_pageStart.TryGetValue(page + 1, out var nxt))
        {
            // Скролл вверх: сик назад от первой строки страницы ниже — O(страницы) на любой глубине.
            beforePos = nxt.Pos; beforeId = nxt.Id;
        }
        _ = LoadPageBackgroundAsync(_generation, page, idSlice, skip, take, afterPos, afterId, beforePos, beforeId);
    }

    // Фоновая загрузка страницы с ограничением параллелизма (семафор): при очень быстром
    // драге скроллбара не плодим сотни подключений/задач. Чтение — в фоне, заполнение — на UI.
    private async Task LoadPageBackgroundAsync(int gen, int page, List<int>? idSlice,
                                               int skip, int take, int? afterPos, int? afterId,
                                               int? beforePos, int? beforeId)
    {
        await _loadGate.WaitAsync();
        try
        {
            if (gen != _generation) return;   // устарело еще до старта (был Reset)
            // Пока задача ждала слот, пользователь мог проскроллить дальше и страницу вытеснили
            // из кеша (EvictOldestIfFull). Тогда не тратим слот/запрос — освобождаем гейт сразу,
            // чтобы быстрее дошла очередь до СТРАНИЦЫ, на которой пользователь остановился.
            // (Возврат на эту страницу запросит загрузку заново.) Чтение полей — на UI-потоке
            // (продолжение после await идет в захваченном контексте), гонки нет.
            if (!_pending.Contains(page) || !_pages.ContainsKey(page)) return;
            // Порог медленности страницы: медленная подгрузка при прокрутке — первый признак
            // деградации базы (сеть, раздутый файл), и виден он здесь раньше жалобы.
            using var slow = Diag.Measure("страница таблицы", Diag.UiPage,
                                          $"{SmartBase.Utils.DbErrors.Display(_repo.DbPath)}, коллекция {_collectionId}, страница {page}");
            var data = await _repo.LoadPageAsync(_collectionId, _excludeHidden, idSlice, skip, take,
                                                 afterPos, afterId, beforePos, beforeId);
            Dispatcher.UIThread.Post(() => FillFromData(gen, page, data.Records, data.Values, data.CellStyles));
        }
        catch (Exception ex)
        {
            // Фоновая загрузка: ошибку проглатываем — строки останутся «Загрузка…» до повторного
            // запроса. Но «файл пропал» сигналит монитору доступности (DbErrors.HandleRead) —
            // иначе таблица висела бы в вечной «Загрузке…» вместо честного «база недоступна».
            // Прочие ошибки (не сеть и не занятость — то есть наш дефект) без записи остались бы
            // вечной «Загрузкой…» без объяснения.
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

    // Строит реальные строки из записей + их значений (теплая пересборка страниц, RebuildPageSync).
    private TableRowVm[] BuildRows(int skip, List<DbRecord> records)
    {
        var ids     = records.Select(r => r.Id!.Value).ToList();
        var valMap  = _repo.GetFieldValuesBatch(ids);
        var cellMap = _repo.GetCellStylesBatch(_collectionId, ids);
        PrimeRelationLinks(ids);

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
        RememberCursor(skip / PageSize, records);
        return rows;
    }

    /// <summary>Заполняет ранее созданные строки-заглушки данными, пришедшими из фона (UI-поток).</summary>
    private void FillFromData(int gen, int page, List<DbRecord> records,
                             Dictionary<int, Dictionary<int, string>> valMap,
                             Dictionary<int, Dictionary<string, RowStyle>> cellMap)
    {
        if (gen != _generation) return;                         // данные устарели (был Reset)
        if (!_pending.Remove(page)) return;                     // уже заполнена/сброшена
        if (!_pages.TryGetValue(page, out var rows)) return;    // вытеснена из кеша

        // Строки-заглушки заполняем реальными данными; на будущее (редактирование ячеек)
        // отдаем ОСНОВНОЙ репозиторий — фоновый контекст использовался только для чтения.
        PrimeRelationLinks(records.Select(r => r.Id!.Value).ToList());
        for (var i = 0; i < records.Count && i < rows.Length; i++)
        {
            var rec    = records[i];
            var id     = rec.Id!.Value;
            var values = valMap.TryGetValue(id, out var v) ? v : [];
            rows[i].SetStyle(cellMap.GetValueOrDefault(id), notify: false);
            rows[i].Fill(_repo, id, values, rec.Title ?? "",
                         rec.CreatedAt ?? "", rec.UpdatedAt ?? "", rec.CreatedBy ?? "", rec.UpdatedBy ?? "");
        }

        RememberCursor(page, records);
    }

    // Сохраняет (Position, Id) крайних строк страницы — курсоры keyset соседних страниц
    // (конец → следующая вперед, начало → предыдущая назад при скролле вверх).
    private void RememberCursor(int page, List<DbRecord> records)
    {
        if (records.Count == 0) return;
        var first = records[0];
        var last  = records[^1];
        _pageStart[page] = (first.Position, first.Id!.Value);
        _pageEnd[page]   = (last.Position,  last.Id!.Value);
    }

    // Страница, к которой обратились, уезжает в хвост LRU. Без этого список был FIFO:
    // порядок задавала только загрузка, и страница, на которой пользователь стоит, вылетала
    // из кеша просто потому, что загружена давно. На прежних 50 страницах это оставалось
    // незаметным, но кеш широкой таблицы куда короче — там видимая страница уходила бы в
    // «Загрузка…» после нескольких прыжков по таблице.
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
    public IReadOnlyList<TableRowVm> MaterializeRange(int start, int count)
    {
        var end    = Math.Min((long)start + count, _dataCount);
        var result = new List<TableRowVm>((int)Math.Max(0, end - start));

        var index = Math.Max(0, start);
        while (index < end)
        {
            var page = index / PageSize;
            var skip = page * PageSize;
            var take = Math.Min(PageSize, _dataCount - skip);

            var rows = _pages.TryGetValue(page, out var cached) && !_pending.Contains(page)
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
    /// Порции идут keyset'ом по позиции (в обычном режиме) и срезами списка id (при фильтре или
    /// сортировке): смещение на каждой порции стоило бы O(глубины) и складывалось бы в квадрат.
    /// </summary>
    public async Task<IReadOnlyList<TableRowVm>> MaterializeRangeAsync(int start, int count, IReadOnlyList<int> fieldIds,
                                                                     IProgress<double>? progress, CancellationToken ct)
    {
        const int Chunk = 2000;

        var end    = (int)Math.Min((long)start + count, _dataCount);
        var from   = Math.Max(0, start);
        var result = new List<TableRowVm>(Math.Max(0, end - from));

        int? afterPos = null, afterId = null;
        for (var index = from; index < end;)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(Chunk, end - index);

            List<int>? slice = null;
            if (_recordIds is not null)
            {
                slice = new List<int>(take);
                for (var k = 0; k < take; k++) slice.Add(_recordIds[index + k]);
            }

            var rows = await _repo.ReadCellRowsAsync(_collectionId, _excludeHidden, slice, index, take,
                                                     afterPos, afterId, fieldIds, ct);
            if (rows.Count == 0) break;

            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                result.Add(new TableRowVm(_repo, row.RecordId, index + i + 1, row.Values, row.Title,
                                          row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy));
            }

            if (_recordIds is null) (afterPos, afterId) = (rows[^1].Position, rows[^1].RecordId);
            index += take;
            progress?.Report((double)(index - from) / Math.Max(1, end - from));
        }

        return result;
    }

    // Строки страницы прямо из базы, мимо кеша и без подписок: см. MaterializeRange.
    private TableRowVm[] ReadDetachedRows(int skip, int take)
    {
        var records = LoadRecordsByOffset(skip, take);
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
        PrimeRelationLinks([recordId]);   // связи записи могли измениться (правка в панели записи)
        row.SetStyle(cells.GetValueOrDefault(recordId), notify: false);
        row.Fill(_repo, recordId, values, rec.Title ?? "",
                 rec.CreatedAt ?? "", rec.UpdatedAt ?? "", rec.CreatedBy ?? "", rec.UpdatedBy ?? "");
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

        PrimeRelationLinks(referencing.Select(r => r.RecordId).ToList());   // свежие названия целей
        foreach (var row in referencing) row.NotifyCellsChanged();
    }

    /// <summary>Перечитывает ЗАГРУЖЕННЫЕ страницы из БД и обновляет строки НА МЕСТЕ (Fill), без Reset —
    /// для операций, меняющих ПОРЯДОК записей, но не их число (сдвиг вверх/вниз): нет мелькания
    /// «Загрузка…». Новые страницы не грузим; число записей не меняется. Синхронно (страниц в кеше
    /// немного — видимые + соседние), читаем offset’ом по индексу (CollectionId, Position).</summary>
    public void RefreshLoadedPages()
    {
        foreach (var page in _pages.Keys.ToList())
        {
            if (_pending.Contains(page)) continue;                 // еще грузится — придет свежей
            if (!_pages.TryGetValue(page, out var rows)) continue;

            var skip    = page * PageSize;
            var records = LoadRecordsByOffset(skip, rows.Length);
            var pageIds = records.Select(r => r.Id!.Value).ToList();
            var valMap  = _repo.GetFieldValuesBatch(pageIds);
            var cellMap = _repo.GetCellStylesBatch(_collectionId, pageIds);
            PrimeRelationLinks(pageIds);

            for (var i = 0; i < records.Count && i < rows.Length; i++)
            {
                var rec    = records[i];
                var id     = rec.Id!.Value;
                var values = valMap.TryGetValue(id, out var v) ? v : [];
                rows[i].SetStyle(cellMap.GetValueOrDefault(id), notify: false);
                rows[i].Fill(_repo, id, values, rec.Title ?? "",
                         rec.CreatedAt ?? "", rec.UpdatedAt ?? "", rec.CreatedBy ?? "", rec.UpdatedBy ?? "");
            }
            RememberCursor(page, records);
        }
    }

    // Записи страницы строго по offset (без keyset-курсора) — для точечного пере-Fill (RefreshLoadedPages).
    private List<DbRecord> LoadRecordsByOffset(int skip, int take)
    {
        if (_recordIds is not null)
        {
            var slice = new List<int>(take);
            for (var k = 0; k < take && skip + k < _recordIds.Count; k++) slice.Add(_recordIds[skip + k]);
            return _repo.GetRecordsByIdsOrdered(slice);
        }
        return _repo.GetRecordsPage(_collectionId, skip, take, _excludeHidden);
    }

    /// <summary>Структурное обновление (создание/удаление/дубль записей): пересчитывает число строк и
    /// СИНХРОННО перестраивает уже загруженные страницы реальными данными (+ страницу 0 — после Reset
    /// грид может прокрутиться в начало), затем шлет Reset. Кеш остается ТЕПЛЫМ, поэтому грид
    /// перерисовывается сразу из него, без заглушек «Загрузка…» — это и убирает мелькание. Применяется
    /// только в обычном режиме (без фильтра/сортировки — там список id пересобирает полный ReloadRows).</summary>
    public void RefreshStructural()
    {
        _dataCount = _recordIds?.Count ?? _repo.CountRecords(_collectionId, _excludeHidden);

        // Курсоры keyset устарели (набор/порядок записей сдвинулся) — сбрасываем, чтобы незагруженные
        // страницы грузились offset’ом по индексу; перестроенные ниже заново проставят свои (BuildRows).
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

    // Перестраивает страницу реальными строками синхронно (offset по индексу) — для теплого кеша.
    private void RebuildPageSync(int page)
    {
        var skip = page * PageSize;
        var take = Math.Min(PageSize, _dataCount - skip);
        if (take <= 0) { DropPage(page); return; }

        _pages[page] = BuildRows(skip, LoadRecordsByOffset(skip, take));   // BuildRows ставит и курсор страницы
        _pending.Remove(page);
        if (!_lru.Contains(page)) _lru.AddLast(page);
    }

    private void DropPage(int page)
    {
        _pages.Remove(page);
        _lru.Remove(page);
        _pending.Remove(page);
        _pageEnd.Remove(page);
        _pageStart.Remove(page);
    }

    /// <summary>Сбрасывает кеш и сигнализирует DataGrid о полной перестройке.</summary>
    public void Reset()
    {
        _generation++;          // отбрасываем фоновые загрузки, начатые до сброса
        _pages.Clear();
        _lru.Clear();
        _pageEnd.Clear();
        _pageStart.Clear();
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
