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
    // Мутируется точечной правкой на месте (см. ApplyDeltaAsync).
    private int _total;
    private readonly Dictionary<int, EventListItem[]> _pages = new();
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _needLoad = new();   // запрошены, но фон ещё не стартовал (ждут дебаунса)
    private readonly HashSet<int> _pending  = new();   // фон в полёте (ждут заполнения)
    private readonly HashSet<int> _settled  = new();   // загрузка завершена — заглушки не перечитываем по кругу
    private readonly System.Threading.SemaphoreSlim _loadGate = new(4);
    private DispatcherTimer? _loadTimer;               // дебаунс: грузим после паузы скролла
    private int _generation;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    // Выделение, пережившее перезагрузку (тихое обновление после правки/дублирования): ключи
    // записей, которые надо снова подсветить — в готовых строках и при фоновом заполнении.
    // Обновляется точечной правкой (ApplyDeltaAsync) актуальным выделением.
    private HashSet<(int DbId, int RecId, int FieldId)>? _preselect;

    // Search-режим: список построен из результатов поиска — точечная правка не применима.
    private readonly bool _searchMode;

    /// <param name="counts">Число событий дня по источникам, позиционно к <paramref name="dbs"/>.
    /// Считаются АСИНХРОННО до построения списка (см. ListView.LoadFrom): холодный COUNT по дню
    /// с миллионами событий занимает секунды и не должен блокировать UI-поток (например, старт
    /// приложения выглядел зависшим на «Запуске интерфейса…»).</param>
    public CalendarDayVirtualList(
        IReadOnlyList<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        IReadOnlyList<int> counts,
        DateTime dayStart, DateTime dayEnd, bool isPast,
        HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection = null)
    {
        _preselect = preserveSelection is { Count: > 0 } ? preserveSelection : null;
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
    public CalendarDayVirtualList(List<CalendarRecord> found, bool isPast,
                                  HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection = null)
    {
        _preselect  = preserveSelection is { Count: > 0 } ? preserveSelection : null;
        _searchMode = true;
        _isPast = isPast;
        _all = found
            .OrderByDescending(e => e.AllDay)
            .ThenBy(e => e.StartDt).ThenBy(e => e.DbId).ThenBy(e => e.Id ?? 0).ThenBy(e => e.FieldId)
            .Select(e =>
            {
                var item = new EventListItem(e, isPast);
                ApplyPreselect(item, e);
                return item;
            })
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
            {
                var item = new EventListItem(ev, _isPast);
                ApplyPreselect(item, ev);
                items.Add(item);
            }
        }
        return items;
    }

    /// <summary>Индекс строки записи — для прокрутки к созданной/изменённой записи или последней
    /// копии после дублирования. InMemory/Search — точный; Offset-режим гигантского дня — только
    /// если строка уже реализована (позиция глубокого события без запроса неизвестна — тогда
    /// null, лента остаётся на месте).</summary>
    public int? RowOfEvent(int dbId, int recId, int fieldId)
    {
        if (_all is not null)
        {
            var idx = _all.FindIndex(i => i.OriginalEvent is { } ev &&
                                          ev.DbId == dbId && ev.Id == recId && ev.FieldId == fieldId);
            return idx >= 0 ? idx : null;
        }
        foreach (var (page, rows) in _pages)
            for (var s = 0; s < rows.Length; s++)
                if (rows[s].OriginalEvent is { } ev &&
                    ev.DbId == dbId && ev.Id == recId && ev.FieldId == fieldId)
                    return page * PageSize + s;
        return null;
    }

    // ── Точечная правка НА МЕСТЕ (без пересборки списка) — как в JournalVirtualList ────────

    private static (int DbId, int RecId, int FieldId) Key(CalendarRecord e) =>
        (e.DbId, e.Id ?? 0, e.FieldId);

    // Порядок InMemory-режима (слияние по времени между БД): all-day первыми, затем
    // время/DbId/Id/FieldId. В offset-режиме порядок другой (конкатенация источников).
    private static readonly Comparer<CalendarRecord> DayComparer =
        Comparer<CalendarRecord>.Create((a, b) =>
        {
            var c = b.AllDay.CompareTo(a.AllDay);   if (c != 0) return c;
            c = a.StartDt.CompareTo(b.StartDt);     if (c != 0) return c;
            c = a.DbId.CompareTo(b.DbId);           if (c != 0) return c;
            c = (a.Id ?? 0).CompareTo(b.Id ?? 0);   if (c != 0) return c;
            return a.FieldId.CompareTo(b.FieldId);
        });

    /// <summary>
    /// Применяет создание/изменение/удаление/дублирование записей к ТЕКУЩЕМУ списку дня без
    /// пересоздания: реализованные строки переживают правку — день обновляется в том же кадре,
    /// без волны «Загрузка…» (пересоздание offset-режима на гигантском дне превращало весь
    /// экран в заглушки и теряло выделение/якорь). Записи вне окна дня игнорируются (перенос
    /// на другую дату). false — мутация невозможна (поиск, удаляемая строка не в кеше, много
    /// вставок) — вызывающий делает тихую пересборку.
    /// </summary>
    public async Task<bool> ApplyDeltaAsync(IReadOnlyList<CalendarRecord> removed,
                                            IReadOnlyList<CalendarRecord> added,
                                            bool selectAddedOnly = false)
    {
        if (_searchMode) return false;
        bool InDay(CalendarRecord r) => r.StartDt >= _dayStart && r.StartDt < _dayEnd;
        var removedIn = removed.Where(InDay).ToList();
        var addedIn   = added.Where(InDay).ToList();
        foreach (var r in removedIn.Concat(addedIn))
            if (r.Id is null) return false;

        // InMemory: правим готовый список напрямую (без БД).
        if (_all is not null)
        {
            foreach (var r in removedIn)
            {
                var key = Key(r);
                var i = _all.FindIndex(x => x.OriginalEvent is { } ev && Key(ev) == key);
                if (i >= 0) _all.RemoveAt(i);
            }
            foreach (var r in addedIn)
            {
                // Копия при дублировании — сразу после последней строки с теми же временем и
                // названием (прямо под оригиналом); иначе — по порядку времени.
                var hint = -1;
                for (var i = 0; i < _all.Count; i++)
                    if (_all[i].OriginalEvent is { } ev && ev.DbId == r.DbId &&
                        ev.AllDay == r.AllDay && ev.StartDt == r.StartDt && ev.Title == r.Title)
                        hint = i;
                _all.Insert(hint >= 0 ? hint + 1 : LowerBound(r),
                            new EventListItem(r, _isPast) { IsSelected = true });
            }
            FinishSelection(addedIn, selectAddedOnly);
            CollectionChanged?.Invoke(this,
                new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            return true;
        }

        // Offset-режим (гигантский день).
        foreach (var r in removedIn.Concat(addedIn))
            if (!_srcIndexByDbId.ContainsKey(r.DbId)) return false;

        // Позиции удаляемых строк — по кешу страниц (их выделяли — они реализованы).
        var realized = new Dictionary<(int DbId, int RecId, int FieldId), int>();
        foreach (var (page, rows) in _pages)
            for (var s = 0; s < rows.Length; s++)
                if (rows[s].OriginalEvent is { } ev)
                    realized[Key(ev)] = page * PageSize + s;

        var removedIdx = new List<int>(removedIn.Count);
        foreach (var r in removedIn)
        {
            if (!realized.TryGetValue(Key(r), out var idx)) return false;
            removedIdx.Add(idx);
        }
        removedIdx.Sort();

        // Посекундная карта мультибазного дня строится ПРЯМО СЕЙЧАС — её состояние (до/после
        // правки в БД) неизвестно, надёжнее пересборка.
        if (_buckets is null && _bucketsTask is not null) return false;

        ++_generation;   // заливки страниц по СТАРЫМ позициям дальше не нужны

        // Карта секунд правится точечно (полная пересборка — секунды сканирования).
        if (_buckets is not null) AdjustBuckets(_buckets, removedIn, addedIn);

        // Снимок заполненных строк → новые позиции: сдвиг на число удалённых ДО строки.
        var mapping = new SortedDictionary<int, EventListItem>();
        var removedKeys = removedIn.Select(Key).ToHashSet();
        foreach (var (page, rows) in _pages)
            for (var s = 0; s < rows.Length; s++)
            {
                var row = rows[s];
                if (row.OriginalEvent is not { } ev) continue;
                if (removedKeys.Contains(Key(ev))) continue;
                var idx = page * PageSize + s;
                mapping[idx - CountBefore(removedIdx, idx)] = row;
            }
        var total = _total - removedIdx.Count;

        // Вставки по одной, позиция по приоритету:
        //  1) правка записи без смены времени — ТА ЖЕ позиция, что у удаляемой строки (ключ
        //     порядка не изменился): без БД и без сдвигов — страницы остаются полными, даже
        //     если визуальная позиция строки (копия под оригиналом) отличается от порядка БД;
        //  2) дубль — СРАЗУ ПОСЛЕ последней строки с теми же временем и названием (копия
        //     видимой записи появляется прямо под ней, а не в конце группы одинаковых секунд;
        //     истинный порядок БД восстановится при следующем открытии дня);
        //  3) иначе — позиция из БД (ранг в дне / в бакете её секунды — COUNT по индексу);
        //     таких вставок за раз не больше 8 — массовые быстрее пересборкой.
        var rankBudget = 8;
        foreach (var rec in addedIn.OrderBy(r => r.StartDt).ThenBy(r => r.Id ?? 0))
        {
            int g;
            var twin = removedIn.FirstOrDefault(x => x.DbId == rec.DbId && x.Id == rec.Id &&
                                                     x.AllDay == rec.AllDay && x.StartDt == rec.StartDt);
            if (twin is not null && realized.TryGetValue(Key(twin), out var oldIdx))
                g = Math.Clamp(oldIdx - CountBefore(removedIdx, oldIdx), 0, total);
            else
            {
                var hint = -1;
                foreach (var (gi, row) in mapping)
                    if (row.OriginalEvent is { } ev && ev.DbId == rec.DbId &&
                        ev.AllDay == rec.AllDay && ev.StartDt == rec.StartDt && ev.Title == rec.Title)
                        hint = gi;
                if (hint >= 0) g = hint + 1;
                else
                {
                    if (--rankBudget < 0) return false;
                    g = Math.Clamp(await RankOfAsync(rec), 0, total);
                }
            }
            foreach (var key in mapping.Keys.Where(k => k >= g).OrderByDescending(k => k).ToList())
            {
                mapping[key + 1] = mapping[key];
                mapping.Remove(key);
            }
            mapping[g] = new EventListItem(rec, _isPast) { IsSelected = true };
            total += 1;
        }

        // Счётчики источников (нужны offset-срезам одиночного источника) и общий объём.
        foreach (var r in removedIn) _srcs![_srcIndexByDbId[r.DbId]].Count -= 1;
        foreach (var r in addedIn)   _srcs![_srcIndexByDbId[r.DbId]].Count += 1;
        var cum = 0;
        foreach (var s in _srcs!) { s.Cumulative = cum; cum += s.Count; }
        _total = total;

        // Страницы заново из пересаженных строк: полные остаются как были; дыры (сдвиги на
        // границах кешированных диапазонов) дочитываются ПО ТРЕБОВАНИЮ — когда заглушка
        // реально показана (см. индексатор). Жадная перечитка всех дырявых страниц занимала
        // фоновую очередь вне экрана, и страница у якоря ждала её секунды.
        _pages.Clear(); _lru.Clear(); _needLoad.Clear(); _pending.Clear(); _settled.Clear();
        foreach (var group in mapping.GroupBy(kv => kv.Key / PageSize))
        {
            var page  = group.Key;
            var start = page * PageSize;
            var take  = Math.Max(0, Math.Min(PageSize, _total - start));
            if (take <= 0) continue;
            var rows = new EventListItem[take];
            foreach (var kv in group)
                if (kv.Key - start < take) rows[kv.Key - start] = kv.Value;
            for (var s = 0; s < take; s++)
                rows[s] ??= EventListItem.Loading();
            _pages[page] = rows;
            _lru.AddLast(page);
        }

        FinishSelection(addedIn, selectAddedOnly);

        // Ключи подсветки для дозагружаемых строк (Fill → ApplyPreselect).
        _preselect = RealizedItems
            .Where(i => i.IsEvent && i.IsSelected && i.OriginalEvent is { Id: not null })
            .Select(i => Key(i.OriginalEvent!))
            .Concat(addedIn.Select(Key))
            .ToHashSet();
        if (_preselect.Count == 0) _preselect = null;

        if (_needLoad.Count > 0) ScheduleLoad();
        CollectionChanged?.Invoke(this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }

    // Позиция вставки в InMemory-список (порядок DayComparer).
    private int LowerBound(CalendarRecord rec)
    {
        int lo = 0, hi = _all!.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_all[mid].OriginalEvent is { } ev && DayComparer.Compare(ev, rec) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // Дублирование: выделение ПЕРЕХОДИТ с оригиналов на копии (снимаем со всех, кроме added).
    private void FinishSelection(List<CalendarRecord> added, bool selectAddedOnly)
    {
        if (!selectAddedOnly) return;
        var keys = added.Select(Key).ToHashSet();
        foreach (var it in RealizedItems)
            if (it.IsSelected && (it.OriginalEvent is not { } ev || !keys.Contains(Key(ev))))
                it.IsSelected = false;
    }

    /// <summary>Выделить строки диапазона индексов [a, b] (Ctrl+Shift+клик). Помечаются
    /// строки из кеша страниц, включая заглушки «Загрузка…» (заполнившись, останутся
    /// выделенными); страницы вне кеша не трогаем — их выборка означала бы чтение всего
    /// диапазона из БД.</summary>
    public void SelectRange(int a, int b)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        if (_all is not null)
        {
            for (var i = Math.Max(0, lo); i <= hi && i < _all.Count; i++)
                _all[i].IsSelected = true;
            return;
        }
        foreach (var (page, rows) in _pages)
            for (var s = 0; s < rows.Length; s++)
            {
                var idx = page * PageSize + s;
                if (idx >= lo && idx <= hi) rows[s].IsSelected = true;
            }
    }

    // Восстановить выделение записи, пережившее перезагрузку списка (см. _preselect).
    private void ApplyPreselect(EventListItem row, CalendarRecord ev)
    {
        if (_preselect is not null && ev.Id is int id &&
            _preselect.Contains((ev.DbId, id, ev.FieldId)))
            row.IsSelected = true;
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
            var row = slot < rows.Length ? rows[slot] : EventListItem.Loading();
            // Дыра после точечной правки (страница в кеше, но строка — заглушка): дочитываем
            // ПО ТРЕБОВАНИЮ — только страницы, чьи заглушки реально показываются. Жадная
            // перечитка всех дырявых страниц забивала фоновую очередь глубокими offset-чтениями
            // вне экрана, и видимая страница ждала их секунды.
            if (row.IsLoadingPlaceholder && !_pending.Contains(page) &&
                !_settled.Contains(page) && _needLoad.Add(page))
                ScheduleLoad();
            return row;
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
            // Несколько БД — порядок по ВРЕМЕНИ через посекундные бакеты (конкатенация источников
            // ломала порядок между базами). Одна БД — keyset-продолжение или offset-прыжок
            // (у одного источника конкатенация и есть порядок времени).
            var records = _srcs!.Count > 1
                ? await ReadBucketRangeAsync(skip, take)
                : cursor is { } c
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

    // ── Мультибазный гигантский день: ПОСЕКУНДНАЯ карта (как «Журнал») ────────────
    // Конкатенация источников ломала порядок времени между БД. Бакет = секунда: счётчики по
    // источникам + префикс-суммы; события идут строго по секундам, конкатенация остаётся
    // только внутри одной и той же секунды — визуально неотличимо от полного порядка.

    private sealed class SecondBucket
    {
        public TimeSpan Time;
        public int[] SrcCounts = null!;
        public int[] SrcCumul  = null!;
        public int Total;
        public int CumStart;   // глобальное смещение первого события бакета
    }

    private List<SecondBucket>? _buckets;
    private Task<List<SecondBucket>>? _bucketsTask;

    private Task<List<SecondBucket>> BucketsAsync()
    {
        if (_buckets is not null) return Task.FromResult(_buckets);
        return _bucketsTask ??= BuildBucketsAsync();
    }

    // Гистограммы секунд от каждого источника (фоновые сканы, один раз на список — на это
    // время строки «Загрузка…») сливаются в упорядоченные бакеты с префикс-суммами.
    private async Task<List<SecondBucket>> BuildBucketsAsync()
    {
        var byTime = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        for (var i = 0; i < _srcs!.Count; i++)
        {
            if (_srcs[i].Count == 0) continue;
            var h = await _srcs[i].Repo.LoadCalendarDayTimeHistogramAsync(
                _dayStart, _dayEnd, _srcs[i].Active);
            foreach (var (t, c) in h)
            {
                if (!byTime.TryGetValue(t, out var arr))
                    byTime[t] = arr = new int[_srcs.Count];
                arr[i] += c;
            }
        }
        var buckets = new List<SecondBucket>(byTime.Count);
        var cum = 0;
        foreach (var (t, counts) in byTime)
        {
            var cumul = new int[counts.Length];
            var total = 0;
            for (var i = 0; i < counts.Length; i++) { cumul[i] = total; total += counts[i]; }
            buckets.Add(new SecondBucket
            {
                Time = TimeSpan.Parse(t), SrcCounts = counts, SrcCumul = cumul,
                Total = total, CumStart = cum,
            });
            cum += total;
        }
        _buckets = buckets;   // продолжение await — на UI-потоке
        return buckets;
    }

    // Срез [skip, skip+take) в порядке по времени — по посекундным бакетам.
    private async Task<List<CalendarRecord>> ReadBucketRangeAsync(int skip, int take)
    {
        var buckets = await BucketsAsync();
        var outp = new List<CalendarRecord>(take);
        var bi = FindBucket(buckets, skip);
        var pos = skip;
        var remaining = take;
        while (bi >= 0 && bi < buckets.Count && remaining > 0)
        {
            var b = buckets[bi];
            var from = pos - b.CumStart;
            if (from >= b.Total) { bi++; continue; }
            var n = Math.Min(remaining, b.Total - from);
            var part = await BucketSliceAsync(b, from, n);
            if (part.Count == 0) break;   // данные изменились под ногами — отдадим что есть
            outp.AddRange(part);
            pos += part.Count;
            remaining -= part.Count;
            if (part.Count < n) break;
        }
        return outp;
    }

    // Последний бакет с CumStart <= evOffset (бакеты непрерывны по CumStart).
    private static int FindBucket(List<SecondBucket> buckets, int evOffset)
    {
        int lo = 0, hi = buckets.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (buckets[mid].CumStart <= evOffset) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    // Срез одного бакета [from, from+count): внутри секунды — конкатенация источников,
    // выборка offset'ом в границах секунды (изолированные подключения).
    private async Task<List<CalendarRecord>> BucketSliceAsync(SecondBucket b, int from, int count)
    {
        var outp = new List<CalendarRecord>(count);
        var pos = from;
        var remaining = count;
        var bs = _dayStart + b.Time;
        var be = bs + TimeSpan.FromSeconds(1);
        for (var i = 0; i < _srcs!.Count && remaining > 0; i++)
        {
            var end = b.SrcCumul[i] + b.SrcCounts[i];
            if (pos >= end) continue;
            var localSkip = pos - b.SrcCumul[i];
            if (localSkip < 0) break;
            var localTake = Math.Min(remaining, b.SrcCounts[i] - localSkip);
            if (localTake <= 0) continue;
            var part = await _srcs[i].Repo.LoadCalendarDayPageAsync(bs, be, _srcs[i].Active, localSkip, localTake);
            foreach (var ev in part) ev.DbId = _srcs[i].DbId;
            outp.AddRange(part);
            pos += part.Count;
            remaining -= part.Count;
            if (part.Count < localTake) break;
        }
        return outp;
    }

    // Точечная правка карты: счётчик секунды записи ± по её источнику, затем пересчёт
    // префикс-сумм (полная пересборка карты стоила бы секунды сканирования).
    private void AdjustBuckets(List<SecondBucket> buckets,
                               List<CalendarRecord> removed, List<CalendarRecord> added)
    {
        void Apply(CalendarRecord rec, int delta)
        {
            var sec = new TimeSpan(rec.StartDt.Hour, rec.StartDt.Minute, rec.StartDt.Second);
            var si  = _srcIndexByDbId[rec.DbId];
            var bi  = buckets.FindIndex(b => b.Time == sec);
            if (bi < 0)
            {
                if (delta <= 0) return;                     // рассинхрон — не ломаемся
                var at = buckets.FindIndex(b => b.Time > sec);
                var nb = new SecondBucket { Time = sec, SrcCounts = new int[_srcs!.Count],
                                            SrcCumul = new int[_srcs.Count] };
                buckets.Insert(at < 0 ? buckets.Count : at, nb);
                bi = at < 0 ? buckets.Count - 1 : at;
            }
            var b = buckets[bi];
            b.SrcCounts[si] = Math.Max(0, b.SrcCounts[si] + delta);
        }
        foreach (var r in removed) Apply(r, -1);
        foreach (var r in added)   Apply(r, +1);
        buckets.RemoveAll(b => b.SrcCounts.Sum() == 0);
        var cum = 0;
        foreach (var b in buckets)
        {
            var t = 0;
            for (var i = 0; i < b.SrcCounts.Length; i++) { b.SrcCumul[i] = t; t += b.SrcCounts[i]; }
            b.Total = t;
            b.CumStart = cum;
            cum += t;
        }
    }

    // Глобальная позиция записи из БД: одна БД — ранг в дне; несколько — начало бакета её
    // секунды + смещение источника внутри бакета + ранг в секундном окне (COUNT дёшев —
    // окно в одну секунду).
    private async Task<int> RankOfAsync(CalendarRecord rec)
    {
        var si  = _srcIndexByDbId[rec.DbId];
        var src = _srcs![si];
        if (_srcs.Count == 1)
            return await src.Repo.CountCalendarDayEventsBeforeAsync(
                _dayStart, _dayEnd, src.Active, rec.AllDay, rec.StartDt, rec.Id!.Value, rec.FieldId);

        var buckets = await BucketsAsync();
        var sec = new TimeSpan(rec.StartDt.Hour, rec.StartDt.Minute, rec.StartDt.Second);
        var b = buckets.FirstOrDefault(x => x.Time == sec);
        if (b is null)
        {
            // Бакета нет (рассинхрон) — к началу следующей секунды; вокруг перечитается по требованию.
            var next = buckets.FirstOrDefault(x => x.Time > sec);
            return next?.CumStart ?? _total;
        }
        var bs = _dayStart + sec;
        var rankIn = await src.Repo.CountCalendarDayEventsBeforeAsync(
            bs, bs + TimeSpan.FromSeconds(1), src.Active, rec.AllDay, rec.StartDt, rec.Id!.Value, rec.FieldId);
        return b.CumStart + b.SrcCumul[si] + rankIn;
    }

    // Число элементов отсортированного списка СТРОГО МЕНЬШЕ значения (значения уникальны).
    private static int CountBefore(List<int> sorted, int value)
    {
        var i = sorted.BinarySearch(value);
        return i >= 0 ? i : ~i;
    }

    // Заполняет ранее созданные заглушки реальными данными НА МЕСТЕ (UI-поток) — без сброса скролла.
    private void Fill(int gen, int page, List<CalendarRecord> records)
    {
        if (gen != _generation) return;
        if (!_pending.Remove(page)) return;
        if (!_pages.TryGetValue(page, out var rows)) return;
        _settled.Add(page);   // повторную перечитку оставшихся заглушек не заказываем
        for (var i = 0; i < records.Count && i < rows.Length; i++)
        {
            rows[i].Fill(records[i], _isPast);
            ApplyPreselect(rows[i], records[i]);
        }
    }

    private void EvictOldestIfFull()
    {
        if (_pages.Count < MaxPages) return;
        var oldest = _lru.First!.Value;
        _lru.RemoveFirst();
        _pages.Remove(oldest);
        _pending.Remove(oldest);
        _needLoad.Remove(oldest);
        _settled.Remove(oldest);
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
