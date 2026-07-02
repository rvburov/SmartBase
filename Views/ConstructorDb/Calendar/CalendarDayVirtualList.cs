using System.Collections;
using System.Collections.Specialized;
using Avalonia.Threading;
using DataBase.Core.Models;
using DataBase.Database.Repositories;

namespace DataBase.Views.ConstructorDb.Calendar;

/// <summary>
/// Виртуальный список событий ОДНОГО дня для «Расписания». Реализует <see cref="IList"/>, поэтому
/// полоса прокрутки соответствует ПОЛНОМУ числу событий дня (можно перетащить в самый низ), а
/// страницы грузятся по требованию — в память попадает лишь несколько страниц. Аналог
/// <c>VirtualRowList</c> таблицы, но для календаря и с мультибазой.
///
/// Два режима (выбор по суммарному числу событий дня, считается в конструкторе):
///  • <b>InMemory</b> (≤ <see cref="MemCap"/> или активен поиск): события дня грузятся сразу
///    (быстро) и сливаются по времени между БД — точный порядок «Расписания», как прежде.
///    Покрывает все реальные дни. Поиск фильтрует по заголовку с ограничением скана.
///  • <b>Offset</b> (&gt; <see cref="MemCap"/>): экстремальный день (миллионы). Источники идут
///    КОНКАТЕНАЦИЕЙ (по dbId, с префикс-суммами), страница грузится offset'ом на изолированном
///    подключении в фоне; до прихода данных — заглушки «Загрузка…». Позволяет мгновенно прыгнуть
///    в любой конец без фриза (глубокий offset по покрывающему индексу — доли секунды).
/// </summary>
internal sealed class CalendarDayVirtualList : IList<EventListItem>, IList, INotifyCollectionChanged
{
    private const int PageSize = 200;
    private const int MaxPages = 40;      // 40 × 200 = 8000 строк в кеше
    private const int MemCap   = 10000;   // ≤ этого (без поиска) — грузим весь день сразу (слияние по времени)

    private sealed class Src
    {
        public int DbId;
        public ConstructorRepository Repo = null!;
        public ISet<int>? Active;
        public int Count;        // событий дня у источника
        public int Cumulative;   // префикс-сумма (событий во всех предыдущих источниках)
    }

    // Позиция в общем (конкатенация источников) порядке — для keyset-продолжения при
    // последовательной прокрутке: индекс источника, фаза (1=all-day, 0=timed) и ключ строки.
    private readonly record struct FwdCursor(int SourceIndex, int Phase, string StartIso, int RecordId, int FieldId);

    private readonly DateTime _dayStart, _dayEnd;
    private readonly bool _isPast;

    // InMemory-режим: весь список сразу.
    private readonly List<EventListItem>? _all;

    // Offset-режим: постраничный кеш с фоновой загрузкой.
    private readonly List<Src>? _srcs;
    private readonly Dictionary<int, int> _srcIndexByDbId = new();   // DbId → индекс в _srcs
    private readonly int _total;
    private readonly Dictionary<int, EventListItem[]> _pages = new();
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _needLoad = new();   // запрошены, но фон ещё не стартовал (ждут дебаунса)
    private readonly HashSet<int> _pending  = new();   // фон в полёте (ждут заполнения)
    private readonly System.Threading.SemaphoreSlim _loadGate = new(4);
    private DispatcherTimer? _loadTimer;               // дебаунс: грузим после паузы скролла
    private int _generation;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <param name="counts">Число событий дня по источникам, позиционно к <paramref name="dbs"/>.
    /// Считаются АСИНХРОННО до построения списка (см. ListView.LoadFrom): холодный COUNT по дню
    /// с миллионами событий занимает секунды и не должен блокировать UI-поток (например, старт
    /// приложения выглядел зависшим на «Запуске интерфейса…»).</param>
    public CalendarDayVirtualList(
        IReadOnlyList<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        IReadOnlyList<int> counts,
        DateTime dayStart, DateTime dayEnd, bool isPast)
    {
        _dayStart = dayStart; _dayEnd = dayEnd; _isPast = isPast;

        var srcs  = new List<Src>();
        var total = 0;
        for (var i = 0; i < dbs.Count; i++)
        {
            var (dbId, repo, active) = dbs[i];
            srcs.Add(new Src { DbId = dbId, Repo = repo, Active = active, Count = counts[i], Cumulative = total });
            total += counts[i];
        }
        _total = total;

        if (total <= MemCap)
        {
            _all = LoadAllInMemory(dbs, dayStart, dayEnd);   // слияние по времени между БД
        }
        else
        {
            _srcs = srcs;                                    // огромный день → offset-режим
            for (var i = 0; i < srcs.Count; i++) _srcIndexByDbId[srcs[i].DbId] = i;
        }
    }

    /// <summary>Search-режим: строки из ГОТОВЫХ результатов фонового поиска по дню
    /// (SearchCalendarEventsAsync; DbId у событий уже проставлен) в порядке «Расписания».</summary>
    public CalendarDayVirtualList(List<CalendarRecord> found, bool isPast)
    {
        _isPast = isPast;
        _all = found
            .OrderByDescending(e => e.AllDay)
            .ThenBy(e => e.StartDt).ThenBy(e => e.DbId).ThenBy(e => e.Id ?? 0).ThenBy(e => e.FieldId)
            .Select(e => new EventListItem(e, isPast))
            .ToList();
        _total = _all.Count;
    }

    // Грузит весь день сразу через пейджер (слияние по времени между БД); зовётся только
    // при total ≤ MemCap, поэтому объём ограничен.
    private List<EventListItem> LoadAllInMemory(
        IReadOnlyList<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        DateTime dayStart, DateTime dayEnd)
    {
        var pager = new CalendarDayPager(dbs, dayStart, dayEnd);
        var items = new List<EventListItem>();
        while (pager.HasMore)
        {
            var page = pager.Next(PageSize);
            if (page.Count == 0) break;
            foreach (var ev in page)
                items.Add(new EventListItem(ev, _isPast));
        }
        return items;
    }

    public int Count => _all?.Count ?? _total;

    public EventListItem this[int index]
    {
        get
        {
            if (_all is not null) return _all[index];
            var page = index / PageSize;
            if (!_pages.TryGetValue(page, out var rows)) rows = LoadPage(page);
            var slot = index % PageSize;
            return slot < rows.Length ? rows[slot] : EventListItem.Loading();
        }
        set => throw new NotSupportedException();
    }

    /// <summary>Реализованные (в памяти/кеше) элементы — для выделения/снятия по видимым строкам.</summary>
    public IEnumerable<EventListItem> RealizedItems =>
        _all ?? _pages.Values.SelectMany(p => p);

    // Создаёт страницу-заглушку «Загрузка…»; фактическую offset-загрузку откладываем дебаунсом.
    private EventListItem[] LoadPage(int page)
    {
        EvictOldestIfFull();
        var skip = page * PageSize;
        var take = Math.Max(0, Math.Min(PageSize, _total - skip));
        var ph = new EventListItem[take];
        for (var i = 0; i < take; i++) ph[i] = EventListItem.Loading();
        _pages[page] = ph;
        _lru.AddLast(page);
        _needLoad.Add(page);
        ScheduleLoad();
        return ph;
    }

    // Дебаунс: грузим не сразу, а через паузу после последнего запроса страницы. При быстром
    // скролле промежуточные страницы успевают вытесниться из кеша и запрос не тратят — грузятся
    // только «осевшие» видимые. Так видимая страница не ждёт за очередью «мусорных» загрузок.
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

    // Стартует фоновую загрузку осевших страниц: самые свежие (там, где остановился пользователь) —
    // первыми; уже вытесненные — пропускаем.
    private void FlushPendingLoads()
    {
        _loadTimer?.Stop();
        if (_needLoad.Count == 0) return;
        foreach (var page in _lru.Reverse().ToList())   // newest-first
        {
            if (!_needLoad.Remove(page)) continue;
            if (!_pages.ContainsKey(page)) continue;
            _pending.Add(page);
            var skip = page * PageSize;
            var take = Math.Max(0, Math.Min(PageSize, _total - skip));
            // Если предыдущая страница уже загружена — продолжаем keyset'ом (быстро, O(страницы)),
            // иначе прыжок → offset. Курсор читаем из последней строки предыдущей страницы.
            _ = LoadPageBackgroundAsync(_generation, page, skip, take, SequentialCursor(page));
        }
    }

    // Курсор для keyset-продолжения: позиция ПОСЛЕ последней строки предыдущей (уже загруженной)
    // страницы. null — предыдущей нет/ещё грузится (тогда страница берётся offset'ом).
    private FwdCursor? SequentialCursor(int page)
    {
        if (page == 0) return null;
        if (!_pages.TryGetValue(page - 1, out var prev) || prev.Length == 0) return null;
        var last = prev[^1];
        if (last.IsLoadingPlaceholder || last.OriginalEvent is null) return null;
        var ev = last.OriginalEvent;
        if (!_srcIndexByDbId.TryGetValue(ev.DbId, out var si)) return null;
        return new FwdCursor(si, ev.AllDay ? 1 : 0,
            DateTime.SpecifyKind(ev.StartDt, DateTimeKind.Unspecified).ToString("o"), ev.Id ?? 0, ev.FieldId);
    }

    private async Task LoadPageBackgroundAsync(int gen, int page, int skip, int take, FwdCursor? cursor)
    {
        if (take <= 0) { _pending.Remove(page); return; }
        await _loadGate.WaitAsync();
        try
        {
            if (gen != _generation) return;
            // Пока задача ждала слот, страницу могли вытеснить из кеша — тогда не тратим запрос.
            if (!_pending.Contains(page) || !_pages.ContainsKey(page)) return;
            // keyset (продолжение за предыдущей страницей) или offset (прыжок) — порядок один и тот же.
            var records = cursor is { } c
                ? await ReadForwardAsync(c, take)
                : await LoadOffsetSlices(skip, take);
            Dispatcher.UIThread.Post(() => Fill(gen, page, records));
        }
        catch
        {
            // Фоновая ошибка — строки останутся «Загрузка…» до повторного запроса.
        }
        finally
        {
            _loadGate.Release();
        }
    }

    // Последовательное чтение вперёд от курсора в общем (конкатенация источников) порядке через
    // keyset (O(страницы), не зависит от глубины). Порядок ИДЕНТИЧЕН offset-выборке (тот же ключ
    // (StartIso, RecordId, FieldId), те же фазы all-day→timed и порядок источников), поэтому
    // страницы keyset и offset стыкуются без дублей/пропусков.
    private async Task<List<CalendarRecord>> ReadForwardAsync(FwdCursor cur, int count)
    {
        var outp   = new List<CalendarRecord>(count);
        var si     = cur.SourceIndex;
        var phase  = cur.Phase;                 // 1 = all-day, 0 = timed
        string? aStart = cur.StartIso; int aRec = cur.RecordId, aField = cur.FieldId;
        var resume = true;                      // первый источник — с курсора; следующие — с начала фазы
        while (outp.Count < count && si < _srcs!.Count)
        {
            var s    = _srcs[si];
            var need = count - outp.Count;
            var page = await s.Repo.LoadCalendarDayPageAfterAsync(
                _dayStart, _dayEnd, s.Active, phase,
                resume ? aStart : null, resume ? aRec : 0, resume ? aField : 0, need);
            if (page.Count > 0)
            {
                foreach (var ev in page) ev.DbId = s.DbId;
                outp.AddRange(page);
                var last = page[^1];
                aStart = DateTime.SpecifyKind(last.StartDt, DateTimeKind.Unspecified).ToString("o");
                aRec   = last.Id ?? 0; aField = last.FieldId; resume = true;
                if (page.Count < need)          // фаза исчерпана — дальше следующая фаза/источник
                    Advance(ref si, ref phase, ref aStart, ref aRec, ref aField, ref resume);
            }
            else Advance(ref si, ref phase, ref aStart, ref aRec, ref aField, ref resume);
        }
        return outp;
    }

    // all-day (1) → timed (0) той же БД → all-day следующей БД. Курсор сбрасываем (с начала фазы).
    private static void Advance(ref int si, ref int phase, ref string? aStart,
                                ref int aRec, ref int aField, ref bool resume)
    {
        if (phase == 1) phase = 0;
        else { si++; phase = 1; }
        aStart = null; aRec = 0; aField = 0; resume = false;
    }

    // Отображает глобальное окно [skip, skip+take) на срезы источников (конкатенация по префикс-суммам)
    // и грузит каждый срез offset'ом на изолированном подключении.
    private async Task<List<CalendarRecord>> LoadOffsetSlices(int skip, int take)
    {
        var outp = new List<CalendarRecord>(take);
        var pos = skip;
        var remaining = take;
        foreach (var s in _srcs!)
        {
            if (remaining <= 0) break;
            var srcEnd = s.Cumulative + s.Count;
            if (pos >= srcEnd) continue;            // окно ещё не дошло до источника
            var localSkip = pos - s.Cumulative;
            if (localSkip < 0) break;               // разрыв (не должно быть при корректных суммах)
            var localTake = Math.Min(remaining, s.Count - localSkip);
            if (localTake <= 0) continue;
            var part = await s.Repo.LoadCalendarDayPageAsync(_dayStart, _dayEnd, s.Active, localSkip, localTake);
            foreach (var ev in part) ev.DbId = s.DbId;
            outp.AddRange(part);
            pos += part.Count;
            remaining -= part.Count;
            if (part.Count < localTake) break;      // источник отдал меньше ожидаемого — стоп
        }
        return outp;
    }

    // Заполняет ранее созданные заглушки реальными данными НА МЕСТЕ (UI-поток) — без сброса скролла.
    private void Fill(int gen, int page, List<CalendarRecord> records)
    {
        if (gen != _generation) return;
        if (!_pending.Remove(page)) return;
        if (!_pages.TryGetValue(page, out var rows)) return;
        for (var i = 0; i < records.Count && i < rows.Length; i++)
            rows[i].Fill(records[i], _isPast);
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

    // ── IList<T> / IList (нужны ItemsControl/VirtualizingStackPanel) ──────────────
    public IEnumerator<EventListItem> GetEnumerator() =>
        Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsReadOnly     => true;
    public bool IsFixedSize    => false;
    public bool IsSynchronized => false;
    public object SyncRoot     => this;

    public bool Contains(EventListItem item) => false;
    public int  IndexOf(EventListItem item)  => -1;
    public void CopyTo(EventListItem[] array, int idx) => throw new NotSupportedException();
    public void Add(EventListItem item)           => throw new NotSupportedException();
    public void Clear()                           => throw new NotSupportedException();
    public void Insert(int i, EventListItem item) => throw new NotSupportedException();
    public void RemoveAt(int i)                   => throw new NotSupportedException();
    public bool Remove(EventListItem item)        => throw new NotSupportedException();

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
