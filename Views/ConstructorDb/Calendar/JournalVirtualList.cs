using System.Collections;
using System.Collections.Specialized;
using Avalonia.Threading;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Views.ConstructorDb.Calendar;

/// <summary>
/// Виртуальный ПЛОСКИЙ список «Журнала»: ВСЯ история данных (без ограничений по датам),
/// строка = заголовок дня / событие / подсказка «Нет записей на сегодня» / заглушка «Загрузка…».
/// Реализует <see cref="IList"/> —
/// полоса прокрутки соответствует ПОЛНОМУ числу строк ленты (можно прыгнуть в любой день),
/// при этом события не материализуются: карта окна строится из СЧЕТЧИКОВ по дням
/// (<c>GetCalendarDayCountsByRange</c>, считаются асинхронно до создания списка), а строки
/// событий подгружаются страницами в фоне по требованию — как в «Расписании»
/// (<see cref="CalendarDayVirtualList"/>), тем же порядком внутри дня (all-day первыми, затем
/// по времени; источники-БД конкатенацией по DbId в virtual-режиме).
///
/// Режимы: InMemory (все события окна ≤ <see cref="MemCap"/> — грузятся сразу, слияние по
/// времени между БД) / Virtual (миллионы — страницы offset'ом в фоне) / Search (готовые
/// результаты фонового SQL-поиска по всему окну — см. SearchCalendarEventsAsync).
/// </summary>
internal sealed class JournalVirtualList : IList<JournalRow>, IList, INotifyCollectionChanged
{
    private const int PageSize   = 200;
    private const int MaxPages   = 40;      // 40 × 200 = 8000 строк в кеше
    private const int MemCap     = 10000;   // ≤ этого (без поиска) — весь журнал сразу в памяти

    private sealed class Src
    {
        public int DbId;
        public ConstructorRepository Repo = null!;
        public ISet<int>? Active;
    }

    // День окна: счетчики по источникам + позиция первой строки (заголовка) в плоском списке.
    private sealed class DayEntry
    {
        public DateTime Date;
        public int[] SrcCounts = null!;   // событий дня в каждом источнике (порядок _srcs)
        public int[] SrcCumul  = null!;   // префикс-суммы SrcCounts (конкатенация внутри дня)
        public int Events;                // всего событий дня
        public int FirstRow;              // глобальный индекс строки-заголовка
        public int RowCount;              // 1 (заголовок) + события (или + подсказка пустого сегодня)
        public bool EmptyToday;
    }

    private readonly List<Src>      _srcs;
    private readonly List<DayEntry> _days = new();
    // Мутируются точечной правкой на месте (см. ApplyDeltaAsync).
    private int  _count;
    private int? _todayHeaderRow;

    // InMemory/Search: готовые строки. Virtual: null (страницы по требованию).
    private readonly List<JournalRow>? _all;

    private readonly Dictionary<int, JournalRow[]> _pages = new();
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _needLoad = new();
    private readonly HashSet<int> _pending  = new();
    private readonly System.Threading.SemaphoreSlim _loadGate = new(4);
    private DispatcherTimer? _loadTimer;
    private int _generation;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Границы «без ограничений»: лента покрывает ВСЮ историю данных. Карта строится из
    /// счетчиков по дням, поэтому фактические границы — первый/последний день с событиями;
    /// сентинелы нужны только запросам (счетчики/диапазон/поиск).</summary>
    internal static readonly DateTime RangeStart = new(1, 1, 1);
    internal static readonly DateTime RangeEnd   = new(9999, 1, 1);

    // Выделение, пережившее пересборку (тихое обновление после правки): ключи записей,
    // которые надо снова подсветить — в готовых строках и при фоновом заполнении страниц.
    // Обновляется точечной правкой (ApplyDeltaAsync) актуальным выделением.
    private HashSet<(int DbId, int RecId, int FieldId)>? _preselect;

    public JournalVirtualList(
        IReadOnlyList<(int dbId, ConstructorRepository repo, ISet<int>? active)> dbs,
        IReadOnlyList<List<(string Day, int Count)>> dayCounts,
        HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection = null)
    {
        _preselect = preserveSelection is { Count: > 0 } ? preserveSelection : null;
        _srcs = dbs.Select(d => new Src { DbId = d.dbId, Repo = d.repo, Active = d.active }).ToList();

        // Карта окна: день → счетчики по источникам (dayCounts[i] позиционно к dbs[i]).
        var byDay = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        for (var i = 0; i < dayCounts.Count; i++)
            foreach (var (day, cnt) in dayCounts[i])
            {
                if (!byDay.TryGetValue(day, out var arr))
                    byDay[day] = arr = new int[_srcs.Count];
                arr[i] += cnt;
            }

        var totalEvents = byDay.Values.Sum(a => a.Sum());

        if (totalEvents <= MemCap) _all = BuildInMemory();
        else                       BuildDayMap(byDay);

        (_count, _todayHeaderRow) = FinishInit();
    }

    /// <summary>Search-режим: строки из ГОТОВЫХ результатов фонового поиска по окну
    /// (SearchCalendarEventsAsync; DbId у событий уже проставлен).</summary>
    public JournalVirtualList(List<CalendarRecord> found,
                              HashSet<(int DbId, int RecId, int FieldId)>? preserveSelection = null)
    {
        _preselect = preserveSelection is { Count: > 0 } ? preserveSelection : null;
        _srcs = new List<Src>();
        var groups = found
            .OrderBy(e => e.StartDt.Date)
            .ThenByDescending(e => e.AllDay)
            .ThenBy(e => e.StartDt).ThenBy(e => e.DbId).ThenBy(e => e.Id ?? 0).ThenBy(e => e.FieldId)
            .GroupBy(e => e.StartDt.Date)
            .Select(g => (Date: g.Key, Events: g.ToList()));
        _all = FlattenGroups(groups);
        (_count, _todayHeaderRow) = FinishInit();
    }

    // Общий финал конструкторов: полное число строк + строка заголовка «сегодня» (автопрокрутка).
    private (int Count, int? TodayRow) FinishInit()
    {
        if (_all is not null)
        {
            var idx = _all.FindIndex(r => r.IsHeader && r.HeaderDate == DateTime.Today);
            return (_all.Count, idx >= 0 ? idx : null);
        }
        var count = _days.Count == 0 ? 0 : _days[^1].FirstRow + _days[^1].RowCount;
        var d = _days.FirstOrDefault(x => x.Date == DateTime.Today);
        return (count, d?.FirstRow);
    }

    // ── Построение карт/строк ─────────────────────────────────────────────────

    // Virtual-режим: только карта дней (события подгрузятся страницами). Лента без ограничений
    // по датам, «сегодня» всегда присутствует (даже пустой).
    private void BuildDayMap(SortedDictionary<string, int[]> byDay)
    {
        var today = DateTime.Today;
        var row = 0;
        void Add(DateTime date, int[]? counts)
        {
            var c = counts ?? new int[_srcs.Count];
            var cum = new int[_srcs.Count];
            var t = 0;
            for (var i = 0; i < c.Length; i++) { cum[i] = t; t += c[i]; }
            var empty = t == 0;
            var e = new DayEntry
            {
                Date = date, SrcCounts = c, SrcCumul = cum, Events = t,
                FirstRow = row, RowCount = 1 + (empty ? 1 : t), EmptyToday = empty,
            };
            _days.Add(e);
            row += e.RowCount;
        }

        var todayAdded = false;
        foreach (var (day, counts) in byDay)
        {
            var date = DateTime.ParseExact(day, "yyyy-MM-dd", null);
            if (!todayAdded && date > today) { Add(today, null); todayAdded = true; }
            if (date == today) todayAdded = true;
            Add(date, counts);
        }
        if (!todayAdded) Add(today, null);
    }

    // InMemory-режим: все события целиком (диапазонный запрос на источник), слияние по времени.
    private List<JournalRow> BuildInMemory()
    {
        var events = new List<CalendarRecord>();
        foreach (var s in _srcs)
        {
            var part = s.Repo.GetCalendarRecordsByRange(RangeStart, RangeEnd, s.Active);
            foreach (var r in part)
            {
                r.DbId = s.DbId;
                events.Add(r);
            }
        }
        var groups = events
            .OrderBy(e => e.StartDt.Date)
            .ThenByDescending(e => e.AllDay)
            .ThenBy(e => e.StartDt).ThenBy(e => e.DbId).ThenBy(e => e.Id ?? 0).ThenBy(e => e.FieldId)
            .GroupBy(e => e.StartDt.Date)
            .Select(g => (Date: g.Key, Events: g.ToList()));
        return FlattenGroups(groups);
    }

    // Группы «день → события» → плоские строки (+ обязательный «сегодня», даже пустой).
    private List<JournalRow> FlattenGroups(IEnumerable<(DateTime Date, List<CalendarRecord> Events)> groups)
    {
        var today = DateTime.Today;
        var rows  = new List<JournalRow>();
        var row   = 0;
        var todayAdded = false;

        void AddDay(DateTime date, List<CalendarRecord>? events)
        {
            var isPast = date < today;
            var n = events?.Count ?? 0;
            _days.Add(new DayEntry
            {
                Date = date, SrcCounts = [], SrcCumul = [], Events = n,
                FirstRow = row, RowCount = 1 + Math.Max(n, date == today && n == 0 ? 1 : 0),
                EmptyToday = date == today && n == 0,
            });
            rows.Add(JournalRow.Header(date));
            row++;
            if (n == 0 && date == today) { rows.Add(JournalRow.Hint()); row++; }
            if (events is null) return;
            foreach (var ev in events)
            {
                var r = new JournalRow(ev, isPast);
                ApplyPreselect(r, ev);
                rows.Add(r);
                row++;
            }
        }

        foreach (var (date, events) in groups)
        {
            if (!todayAdded && date > today) { AddDay(today, null); todayAdded = true; }
            if (date == today) todayAdded = true;
            AddDay(date, events);
        }
        if (!todayAdded) AddDay(today, null);
        return rows;
    }

    // ── IList: доступ по индексу ──────────────────────────────────────────────

    public int Count => _count;

    public JournalRow this[int index]
    {
        get
        {
            if (_all is not null) return _all[index];
            var page = index / PageSize;
            if (!_pages.TryGetValue(page, out var rows)) rows = LoadPage(page);
            var slot = index % PageSize;
            return slot < rows.Length ? rows[slot] : JournalRow.Loading();
        }
        set => throw new NotSupportedException();
    }

    /// <summary>Реализованные строки (для выделения по видимым).</summary>
    public IEnumerable<JournalRow> RealizedItems =>
        _all ?? _pages.Values.SelectMany(p => p);

    /// <summary>Индекс строки-заголовка сегодняшнего дня (для автопрокрутки), если он в окне.</summary>
    public int? TodayHeaderRow => _todayHeaderRow;

    /// <summary>Диапазон строк дня (заголовок + события), если день есть в ленте — для
    /// проверки видимости и прокрутки к дате созданной/измененной записи.</summary>
    public (int FirstRow, int RowCount)? DayRowsOf(DateTime date) =>
        FindDay(date) is { } d ? (d.FirstRow, d.RowCount) : null;

    // Бинарный поиск дня по дате (дни отсортированы).
    private DayEntry? FindDay(DateTime date)
    {
        int lo = 0, hi = _days.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_days[mid].Date == date.Date) return _days[mid];
            if (_days[mid].Date < date.Date) lo = mid + 1;
            else hi = mid - 1;
        }
        return null;
    }

    /// <summary>Индекс СТРОКИ конкретного события (<paramref name="start"/> — его точное время
    /// начала) — для центрирования ленты на созданной/измененной записи (заголовок дня может
    /// быть далеко от нее в большом дне). Обычный день — по слитому дню (кеш); InMemory/Search —
    /// по готовым строкам; гигантский день — через бакет секунды записи.</summary>
    public async Task<int?> RowOfEventAsync(int dbId, int recId, int fieldId, DateTime start)
    {
        if (_all is not null)
        {
            var idx = _all.FindIndex(r => r.IsEvent && r.OriginalEvent is { } ev &&
                                          ev.DbId == dbId && ev.Id == recId && ev.FieldId == fieldId);
            return idx >= 0 ? idx : null;
        }
        var d = FindDay(start.Date);
        if (d is null || d.EmptyToday) return null;

        if (d.Events <= MergeDayCap)
        {
            var all = await MergedDayAsync(d);
            var k = all.FindIndex(e => e.DbId == dbId && e.Id == recId && e.FieldId == fieldId);
            return k >= 0 ? d.FirstRow + 1 + k : null;
        }

        // Гигантский день: бакет секунды записи → точная позиция внутри бакета.
        var buckets = await HugeDayMapAsync(d);
        var si = _srcs.FindIndex(s => s.DbId == dbId);
        if (si < 0) return null;
        var sec = new TimeSpan(start.Hour, start.Minute, start.Second);
        var b = buckets.FirstOrDefault(x => x.Time == sec);
        if (b is null || b.SrcCounts[si] == 0) return null;
        var slice = await BucketSlice(d, b, b.SrcCumul[si], b.SrcCounts[si]);
        var k2 = slice.FindIndex(e => e.DbId == dbId && e.Id == recId && e.FieldId == fieldId);
        return k2 >= 0 ? d.FirstRow + 1 + b.CumStart + b.SrcCumul[si] + k2 : null;
    }

    /// <summary>Индекс РЕАЛИЗОВАННОЙ строки записи по ключу (для якоря Ctrl+Shift+диапазона) —
    /// без обращения к БД; null, если строка не в кеше страниц.</summary>
    public int? RealizedRowOfKey(int dbId, int recId, int fieldId)
    {
        if (_all is not null)
        {
            var idx = _all.FindIndex(r => r.IsEvent && r.OriginalEvent is { } ev &&
                                          ev.DbId == dbId && ev.Id == recId && ev.FieldId == fieldId);
            return idx >= 0 ? idx : null;
        }
        foreach (var (page, rows) in _pages)
            for (var s = 0; s < rows.Length; s++)
                if (rows[s] is { IsEvent: true, OriginalEvent: { } ev } &&
                    ev.DbId == dbId && ev.Id == recId && ev.FieldId == fieldId)
                    return page * PageSize + s;
        return null;
    }

    /// <summary>Выделить строки-события диапазона индексов [a, b] (Ctrl+Shift+клик); заголовки
    /// и подсказки пропускаются. Помечаются строки из кеша страниц, включая заглушки
    /// «Загрузка…» (заполнившись, останутся выделенными); страницы вне кеша не трогаем —
    /// их выборка означала бы чтение всего диапазона из БД.</summary>
    public void SelectRange(int a, int b)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        void Mark(JournalRow r)
        {
            if (!r.IsHeader && !r.IsHint) r.IsSelected = true;
        }
        if (_all is not null)
        {
            for (var i = Math.Max(0, lo); i <= hi && i < _all.Count; i++) Mark(_all[i]);
            return;
        }
        foreach (var (page, rows) in _pages)
            for (var s = 0; s < rows.Length; s++)
            {
                var idx = page * PageSize + s;
                if (idx >= lo && idx <= hi) Mark(rows[s]);
            }
    }

    /// <summary>День, которому принадлежит строка (для «Создать запись» по месту клика).</summary>
    public DateTime DateForRow(int row)
    {
        var d = LocateDay(Math.Clamp(row, 0, Math.Max(0, _count - 1)));
        return d?.Date ?? DateTime.Today;
    }

    /// <summary>Заголовок следующего дня после строки <paramref name="row"/> (навигация →).</summary>
    public int? NextHeaderRow(int row)
    {
        var i = LocateDayIndex(row);
        return i >= 0 && i + 1 < _days.Count ? _days[i + 1].FirstRow : null;
    }

    /// <summary>Заголовок текущего дня, если строка ниже него, иначе предыдущего (навигация ←).</summary>
    public int? PrevHeaderRow(int row)
    {
        var i = LocateDayIndex(row);
        if (i < 0) return null;
        if (_days[i].FirstRow < row) return _days[i].FirstRow;
        return i > 0 ? _days[i - 1].FirstRow : null;
    }

    // Бинарный поиск дня по глобальному индексу строки.
    private int LocateDayIndex(int row)
    {
        int lo = 0, hi = _days.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_days[mid].FirstRow <= row) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    private DayEntry? LocateDay(int row)
    {
        var i = LocateDayIndex(row);
        return i >= 0 ? _days[i] : null;
    }

    // ── Virtual-режим: страницы с фоновой загрузкой (как в CalendarDayVirtualList) ──

    // Заголовки/подсказки строятся сразу (без БД), события — заглушки до фоновой загрузки.
    private JournalRow[] LoadPage(int page)
    {
        EvictOldestIfFull();
        var start = page * PageSize;
        var take  = Math.Max(0, Math.Min(PageSize, _count - start));
        var rows  = new JournalRow[take];
        var di    = LocateDayIndex(start);
        for (var k = 0; k < take; k++)
        {
            var row = start + k;
            while (di + 1 < _days.Count && _days[di + 1].FirstRow <= row) di++;
            var d = _days[di];
            rows[k] = row == d.FirstRow ? JournalRow.Header(d.Date)
                    : d.EmptyToday      ? JournalRow.Hint()
                    : JournalRow.Loading();
        }
        _pages[page] = rows;
        _lru.AddLast(page);
        _needLoad.Add(page);
        ScheduleLoad();
        return rows;
    }

    // Дебаунс: транзитные страницы при быстром драге вытесняются и запрос не тратят.
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
        foreach (var page in _lru.Reverse().ToList())   // newest-first
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

            // Сегменты страницы по дням: (индекс дня, смещение в событиях дня, сколько, слот страницы).
            var start = page * PageSize;
            var take  = Math.Max(0, Math.Min(PageSize, _count - start));
            var segs  = new List<(DayEntry Day, int EvOffset, int Take, int Slot)>();
            var di    = LocateDayIndex(start);
            var k     = 0;
            while (k < take && di < _days.Count)
            {
                var d = _days[di];
                var row = start + k;
                if (row == d.FirstRow || d.EmptyToday) { k++; if (row - d.FirstRow + 1 >= d.RowCount) di++; continue; }
                var evOffset = row - d.FirstRow - 1;              // событие № в дне
                var n = Math.Min(take - k, d.Events - evOffset);  // до конца дня/страницы
                segs.Add((d, evOffset, n, k));
                k += n;
                if (evOffset + n >= d.Events) di++;
            }

            foreach (var (d, evOffset, n, slot) in segs)
            {
                // После точечной правки страница может быть заполнена частично: сегменты,
                // чьи строки уже готовы, повторно из БД не читаем (дочитываются только дыры).
                if (_pages.TryGetValue(page, out var cur))
                {
                    var allFilled = true;
                    for (var i = 0; i < n && slot + i < cur.Length; i++)
                        if (!cur[slot + i].IsEvent) { allFilled = false; break; }
                    if (allFilled) continue;
                }
                var part = await LoadDaySlice(d, evOffset, n);
                if (gen != _generation) return;
                var isPast = d.Date < DateTime.Today;
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen != _generation) return;
                    if (!_pages.TryGetValue(page, out var rows)) return;
                    for (var i = 0; i < part.Count && slot + i < rows.Length; i++)
                    {
                        rows[slot + i].Fill(part[i], isPast);
                        ApplyPreselect(rows[slot + i], part[i]);
                    }
                });
            }
            _pending.Remove(page);
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

    // Дни ≤ этого сливаются ПО ВРЕМЕНИ между БД целиком (слитый день кешируется). Гигантские
    // дни (миллионы) в память не грузятся — для них порядок по времени дает ПОСЕКУНДНАЯ карта
    // (см. HugeDayMapAsync): блоки по базам остаются только внутри одной и той же секунды.
    private const int MergeDayCap = 10000;
    private readonly Dictionary<DateTime, List<CalendarRecord>> _mergedDays = new();
    private readonly LinkedList<DateTime> _mergedLru = new();

    // Посекундная карта гигантского дня: бакет = секунда, счетчики по источникам + префикс-суммы.
    private sealed class HugeBucket
    {
        public TimeSpan Time;
        public int[] SrcCounts = null!;
        public int[] SrcCumul  = null!;
        public int Total;
        public int CumStart;   // глобальное смещение первого события бакета внутри дня
    }
    private readonly Dictionary<DateTime, List<HugeBucket>> _hugeDayMaps = new();
    private readonly LinkedList<DateTime> _hugeLru = new();

    // События дня [evOffset, evOffset+take) в порядке по времени.
    private async Task<List<CalendarRecord>> LoadDaySlice(DayEntry d, int evOffset, int take)
    {
        if (d.Events <= MergeDayCap)
        {
            var all = await MergedDayAsync(d);
            var n = Math.Max(0, Math.Min(take, all.Count - evOffset));
            return n <= 0 ? new List<CalendarRecord>() : all.GetRange(evOffset, n);
        }

        // Гигантский день: идем по посекундным бакетам от evOffset.
        var buckets = await HugeDayMapAsync(d);
        var outp = new List<CalendarRecord>(take);
        var bi = FindBucket(buckets, evOffset);
        var pos = evOffset;
        var remaining = take;
        while (bi >= 0 && bi < buckets.Count && remaining > 0)
        {
            var b = buckets[bi];
            var from = pos - b.CumStart;
            if (from >= b.Total) { bi++; continue; }
            var n = Math.Min(remaining, b.Total - from);
            var part = await BucketSlice(d, b, from, n);
            if (part.Count == 0) break;   // данные изменились под ногами — отдадим что есть
            outp.AddRange(part);
            pos += part.Count;
            remaining -= part.Count;
            if (part.Count < n) break;
        }
        return outp;
    }

    // Последний бакет с CumStart <= evOffset (бакеты непрерывны по CumStart).
    private static int FindBucket(List<HugeBucket> buckets, int evOffset)
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

    // Срез одного посекундного бакета [from, from+count): внутри секунды — конкатенация
    // источников; выборка — offset'ом в границах этой секунды (изолированные подключения).
    private async Task<List<CalendarRecord>> BucketSlice(DayEntry d, HugeBucket b, int from, int count)
    {
        var outp = new List<CalendarRecord>(count);
        var pos = from;
        var remaining = count;
        var bs = d.Date + b.Time;
        var be = bs + TimeSpan.FromSeconds(1);
        for (var i = 0; i < _srcs.Count && remaining > 0; i++)
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

    // Строит (и кеширует, LRU 2 дня) посекундную карту гигантского дня: гистограммы времени от
    // каждого источника (скан индекса дня в фоне) сливаются в упорядоченные бакеты с префикс-
    // суммами — события дня получают глобальный порядок по времени без материализации.
    private async Task<List<HugeBucket>> HugeDayMapAsync(DayEntry d)
    {
        if (_hugeDayMaps.TryGetValue(d.Date, out var cached)) return cached;

        var byTime = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        for (var i = 0; i < _srcs.Count; i++)
        {
            if (d.SrcCounts[i] == 0) continue;
            var h = await _srcs[i].Repo.LoadCalendarDayTimeHistogramAsync(
                d.Date, d.Date.AddDays(1), _srcs[i].Active);
            foreach (var (t, c) in h)
            {
                if (!byTime.TryGetValue(t, out var arr))
                    byTime[t] = arr = new int[_srcs.Count];
                arr[i] += c;
            }
        }

        var buckets = new List<HugeBucket>(byTime.Count);
        var cum = 0;
        foreach (var (t, counts) in byTime)
        {
            var cumul = new int[counts.Length];
            var total = 0;
            for (var i = 0; i < counts.Length; i++) { cumul[i] = total; total += counts[i]; }
            buckets.Add(new HugeBucket
            {
                Time = TimeSpan.Parse(t), SrcCounts = counts, SrcCumul = cumul,
                Total = total, CumStart = cum,
            });
            cum += total;
        }

        if (_hugeDayMaps.Count >= 2)
        {
            var oldest = _hugeLru.First!.Value;
            _hugeLru.RemoveFirst();
            _hugeDayMaps.Remove(oldest);
        }
        _hugeDayMaps[d.Date] = buckets;
        _hugeLru.AddLast(d.Date);
        return buckets;
    }

    // Весь день из всех источников, слитый по времени (all-day первыми) — с LRU-кешем,
    // чтобы соседние страницы одного дня не перечитывали его заново. Доступ — с UI-потока
    // (продолжения await в LoadPageBackgroundAsync идут в UI-контексте), гонок нет.
    private async Task<List<CalendarRecord>> MergedDayAsync(DayEntry d)
    {
        if (_mergedDays.TryGetValue(d.Date, out var cached)) return cached;

        var all = new List<CalendarRecord>(d.Events);
        for (var i = 0; i < _srcs.Count; i++)
        {
            if (d.SrcCounts[i] == 0) continue;
            var part = await _srcs[i].Repo.LoadCalendarDayPageAsync(
                d.Date, d.Date.AddDays(1), _srcs[i].Active, 0, d.SrcCounts[i]);
            foreach (var ev in part) ev.DbId = _srcs[i].DbId;
            all.AddRange(part);
        }
        all = all
            .OrderByDescending(e => e.AllDay)
            .ThenBy(e => e.StartDt).ThenBy(e => e.DbId).ThenBy(e => e.Id ?? 0).ThenBy(e => e.FieldId)
            .ToList();

        if (_mergedDays.Count >= 8)
        {
            var oldest = _mergedLru.First!.Value;
            _mergedLru.RemoveFirst();
            _mergedDays.Remove(oldest);
        }
        _mergedDays[d.Date] = all;
        _mergedLru.AddLast(d.Date);
        return all;
    }

    // Восстановить выделение записи, пережившее пересборку списка (см. _preselect).
    private void ApplyPreselect(JournalRow row, CalendarRecord ev)
    {
        if (_preselect is not null && ev.Id is int id &&
            _preselect.Contains((ev.DbId, id, ev.FieldId)))
            row.IsSelected = true;
    }

    // ── Точечная правка НА МЕСТЕ (без пересборки списка) ─────────────────────

    // Порядок событий слитого дня — тот же, что в MergedDayAsync/BuildInMemory: all-day
    // первыми, затем время/DbId/Id/FieldId (ключ уникален, стабильность не важна).
    private static readonly Comparer<CalendarRecord> MergedComparer =
        Comparer<CalendarRecord>.Create((a, b) =>
        {
            var c = b.AllDay.CompareTo(a.AllDay);   if (c != 0) return c;
            c = a.StartDt.CompareTo(b.StartDt);     if (c != 0) return c;
            c = a.DbId.CompareTo(b.DbId);           if (c != 0) return c;
            c = (a.Id ?? 0).CompareTo(b.Id ?? 0);   if (c != 0) return c;
            return a.FieldId.CompareTo(b.FieldId);
        });

    private static (int DbId, int RecId, int FieldId) Key(CalendarRecord e) =>
        (e.DbId, e.Id ?? 0, e.FieldId);

    /// <summary>
    /// Применяет создание/изменение/удаление записей к ТЕКУЩЕМУ списку, не пересоздавая его:
    /// реализованные страницы, слитые дни и посекундные карты переживают правку — лента
    /// обновляется в том же кадре, без волны «Загрузка…» и без перечитывания видимых дней
    /// по одному (на разреженной истории пересоздание списка стоило секунды после каждого
    /// удаления). false — мутация невозможна (InMemory/поиск, день пересек порог слияния,
    /// удаляемая строка не реализована) — вызывающий пересоберет список по кешу счетчиков.
    /// </summary>
    public async Task<bool> ApplyDeltaAsync(IReadOnlyList<CalendarRecord> removed,
                                            IReadOnlyList<CalendarRecord> added,
                                            bool selectAddedOnly = false)
    {
        if (_all is not null) return false;   // InMemory/Search: пересборка и так дешевая
        if (removed.Count == 0 && added.Count == 0) return false;
        // Массовые ВСТАВКИ (дублирование сотен строк): каждый затронутый день вне кеша слитых
        // перечитывался бы отдельным запросом — быстрее мгновенная пересборка по счетчикам.
        // Удаления не ограничиваем: они обходятся без БД (индекс реализованных строк).
        if (added.Count > 100) return false;
        foreach (var r in removed.Concat(added))
            if (r.Id is null || _srcs.FindIndex(s => s.DbId == r.DbId) < 0) return false;

        var byDay = new SortedDictionary<DateTime, List<(CalendarRecord Rec, int Delta)>>();
        void Mark(CalendarRecord r, int delta)
        {
            if (!byDay.TryGetValue(r.StartDt.Date, out var l)) byDay[r.StartDt.Date] = l = new();
            l.Add((r, delta));
        }
        foreach (var r in removed) Mark(r, -1);
        foreach (var r in added)   Mark(r, +1);

        // Заливки страниц, летящие по СТАРЫМ позициям, дальше не нужны.
        ++_generation;

        // Индекс реализованных строк по ключу записи — ОДИН проход по кешу страниц вместо
        // сканирования на каждую удаляемую запись (массовое удаление сотен выделенных строк).
        Dictionary<(int DbId, int RecId, int FieldId), int>? realizedIdx = null;
        Dictionary<(int DbId, int RecId, int FieldId), int> RealizedIdx()
        {
            if (realizedIdx is not null) return realizedIdx;
            realizedIdx = new Dictionary<(int DbId, int RecId, int FieldId), int>();
            foreach (var (page, rows) in _pages)
                for (var s = 0; s < rows.Length; s++)
                    if (rows[s] is { IsEvent: true, OriginalEvent: { } ev })
                        realizedIdx[Key(ev)] = page * PageSize + s;
            return realizedIdx;
        }

        // 1. План по каждому затронутому дню (структуры пока не трогаем — при любом отказе
        //    список остается целым, вызывающий уходит на пересборку).
        var removedKeys = removed.Select(Key).ToHashSet();
        var mergedPlan  = new Dictionary<DateTime, List<CalendarRecord>>(); // итоговый слитый день
        var offsetsPlan = new Dictionary<DateTime, List<int>>();            // удаленные evOffset
        var dropDays    = new HashSet<DateTime>();                          // строки дня не переносим

        foreach (var (day, deltas) in byDay)
        {
            var d = FindDay(day);
            var before = d?.Events ?? 0;
            var after  = before + deltas.Sum(x => x.Delta);
            if (after < 0) return false;                        // рассинхрон с картой дней
            if (after == 0) { dropDays.Add(day); continue; }
            // Порог слияния пересечен — порядок ВСЕГО дня меняется (слитый ↔ посекундный).
            if (d is not null && before > MergeDayCap != after > MergeDayCap) return false;

            if (after > MergeDayCap)
            {
                // Гигантский день: точное место добавленной записи требует БД — страницы дня
                // перечитаются (посекундная карта правится на месте, поэтому это быстро);
                // удаления переносятся арифметикой смещений по реализованным строкам.
                if (deltas.Any(x => x.Delta > 0)) { dropDays.Add(day); continue; }
                if (!CollectRemovedOffsets(d!, deltas, RealizedIdx(), out var offs)) return false;
                offsetsPlan[day] = offs;
                continue;
            }

            List<CalendarRecord> final;
            if (_mergedDays.TryGetValue(day, out var cached))
                final = cached.Where(e => !removedKeys.Contains(Key(e))).ToList();
            else if (d is not null && deltas.All(x => x.Delta < 0))
            {
                // День не в кеше слитых, только удаления — обходимся без БД.
                if (!CollectRemovedOffsets(d, deltas, RealizedIdx(), out var offs)) return false;
                offsetsPlan[day] = offs;
                continue;
            }
            else if (d is null)
                final = new List<CalendarRecord>();             // новый день — из самих записей
            else
            {
                // День с добавлением, кеша нет: свежий слитый день из БД (правка там уже есть).
                mergedPlan[day] = await LoadMergedDayFreshAsync(day, deltas);
                continue;
            }
            foreach (var (rec, delta) in deltas)
                if (delta > 0)
                {
                    var at = final.BinarySearch(rec, MergedComparer);
                    final.Insert(at < 0 ? ~at : at, rec);
                }
            mergedPlan[day] = final;
        }

        // 2. Снимок готовых строк-событий с их принадлежностью (день + № события в дне) —
        //    позиции считаем по СТАРОЙ карте, до мутации.
        var oldRows = new List<(DateTime Day, int EvOffset, JournalRow Row)>();
        foreach (var (page, rows) in _pages)
            for (var s = 0; s < rows.Length; s++)
            {
                if (rows[s] is not { IsEvent: true, OriginalEvent: not null }) continue;
                var idx = page * PageSize + s;
                var d = LocateDay(idx);
                if (d is null || idx == d.FirstRow) continue;
                oldRows.Add((d.Date, idx - d.FirstRow - 1, rows[s]));
            }

        // 3. Мутация карты дней: счетчики/строки затронутых дней, затем префиксы FirstRow.
        var today = DateTime.Today;
        foreach (var (day, deltas) in byDay)
        {
            var d = FindDay(day);
            if (d is null)
            {
                d = new DayEntry { Date = day, SrcCounts = new int[_srcs.Count],
                                   SrcCumul = new int[_srcs.Count] };
                var at = _days.FindIndex(x => x.Date > day);
                _days.Insert(at < 0 ? _days.Count : at, d);
            }
            foreach (var (rec, delta) in deltas)
                d.SrcCounts[_srcs.FindIndex(s => s.DbId == rec.DbId)] += delta;
            var cum = 0;
            for (var i = 0; i < d.SrcCounts.Length; i++) { d.SrcCumul[i] = cum; cum += d.SrcCounts[i]; }
            d.Events = cum;
            if (cum == 0 && day != today) { _days.Remove(d); continue; }
            d.EmptyToday = cum == 0;
            d.RowCount   = 1 + Math.Max(cum, d.EmptyToday ? 1 : 0);
        }
        var rowAcc = 0;
        foreach (var d in _days) { d.FirstRow = rowAcc; rowAcc += d.RowCount; }
        _count = rowAcc;
        _todayHeaderRow = FindDay(today)?.FirstRow;

        // 4. Кеши дней: слитые — итоговыми списками, посекундные карты — правкой бакетов
        //    (полное перестроение карты гигантского дня стоило бы секунды сканирования).
        foreach (var (day, deltas) in byDay)
        {
            if (mergedPlan.TryGetValue(day, out var final)) StoreMergedDay(day, final);
            else if (_mergedDays.Remove(day)) _mergedLru.Remove(day);

            if (_hugeDayMaps.TryGetValue(day, out var buckets))
            {
                if (FindDay(day) is { } nd && nd.Events > MergeDayCap) AdjustHugeBuckets(buckets, deltas);
                else { _hugeDayMaps.Remove(day); _hugeLru.Remove(day); }
            }
        }

        // 5. Пересадка готовых строк на новые позиции. Заголовки/подсказки перестроятся по
        //    новой карте; недостающие события — «Загрузка…» с дозагрузкой только их сегментов.
        var mergedIndex = mergedPlan.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select((e, i) => (Key: Key(e), I: i)).ToDictionary(x => x.Key, x => x.I));
        var moved = new Dictionary<int, JournalRow>(oldRows.Count + added.Count);
        foreach (var (day, evOffset, row) in oldRows)
        {
            // Дублирование: выделение переходит на копии — с оригиналов снимаем.
            if (selectAddedOnly && row.IsSelected) row.IsSelected = false;
            if (dropDays.Contains(day)) continue;
            var nd = FindDay(day);
            if (nd is null || nd.EmptyToday) continue;
            int k;
            if (mergedIndex.TryGetValue(day, out var index))
            {
                var keyOf = Key(row.OriginalEvent!);
                if (removedKeys.Contains(keyOf) || !index.TryGetValue(keyOf, out k)) continue;
            }
            else if (offsetsPlan.TryGetValue(day, out var offs))
            {
                if (offs.BinarySearch(evOffset) >= 0) continue;    // сама удаленная строка
                k = evOffset - offs.Count(o => o < evOffset);
            }
            else k = evOffset;
            if (k >= 0 && k < nd.Events) moved[nd.FirstRow + 1 + k] = row;
        }
        // Созданная/измененная запись — готовой строкой, сразу выделенной (видно, ЧТО меняли).
        foreach (var rec in added)
        {
            var day = rec.StartDt.Date;
            if (!mergedIndex.TryGetValue(day, out var index)) continue;  // гигантский день — перечитается
            if (FindDay(day) is not { } nd || !index.TryGetValue(Key(rec), out var k)) continue;
            moved[nd.FirstRow + 1 + k] = new JournalRow(rec, day < today) { IsSelected = true };
        }

        // 6. Страницы заново из пересаженных строк: полные остаются как были, страницы с
        //    дырами дочитывают только недостающие сегменты (см. LoadPageBackgroundAsync).
        _pages.Clear(); _lru.Clear(); _needLoad.Clear(); _pending.Clear();
        foreach (var group in moved.GroupBy(kv => kv.Key / PageSize))
        {
            var page  = group.Key;
            var start = page * PageSize;
            var take  = Math.Max(0, Math.Min(PageSize, _count - start));
            if (take <= 0) continue;
            var rows = new JournalRow[take];
            foreach (var kv in group)
                if (kv.Key - start < take) rows[kv.Key - start] = kv.Value;
            var di = LocateDayIndex(start);
            var full = true;
            for (var s = 0; s < take; s++)
            {
                if (rows[s] is not null) continue;
                var idx = start + s;
                while (di + 1 < _days.Count && _days[di + 1].FirstRow <= idx) di++;
                var d = _days[di];
                if (idx == d.FirstRow) rows[s] = JournalRow.Header(d.Date);
                else if (d.EmptyToday) rows[s] = JournalRow.Hint();
                else { rows[s] = JournalRow.Loading(); full = false; }
            }
            _pages[page] = rows;
            _lru.AddLast(page);
            if (!full) _needLoad.Add(page);
        }

        // Ключи подсветки для дозагружаемых строк (Fill → ApplyPreselect): актуальное выделение
        // + созданные записи (в гигантском дне их строка появится только из дозагрузки).
        _preselect = RealizedItems
            .Where(i => i.IsEvent && i.IsSelected && i.OriginalEvent is { Id: not null })
            .Select(i => Key(i.OriginalEvent!))
            .Concat(added.Select(Key))
            .ToHashSet();
        if (_preselect.Count == 0) _preselect = null;

        if (_needLoad.Count > 0) ScheduleLoad();
        CollectionChanged?.Invoke(this,
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }

    // Смещения удаляемых событий внутри дня — по индексу реализованных строк (удаляемые строки
    // пользователь видел/выделял, поэтому они в кеше страниц; иначе позиции без БД не узнать).
    private static bool CollectRemovedOffsets(DayEntry d, List<(CalendarRecord Rec, int Delta)> deltas,
                                              Dictionary<(int DbId, int RecId, int FieldId), int> realized,
                                              out List<int> offsets)
    {
        offsets = new List<int>();
        foreach (var (rec, _) in deltas)
        {
            if (!realized.TryGetValue(Key(rec), out var idx)) return false;
            offsets.Add(idx - d.FirstRow - 1);
        }
        offsets.Sort();
        return true;
    }

    // Кладет/обновляет слитый день в LRU-кеше.
    private void StoreMergedDay(DateTime day, List<CalendarRecord> all)
    {
        if (!_mergedDays.ContainsKey(day))
        {
            if (_mergedDays.Count >= 8)
            {
                var oldest = _mergedLru.First!.Value;
                _mergedLru.RemoveFirst();
                _mergedDays.Remove(oldest);
            }
            _mergedLru.AddLast(day);
        }
        _mergedDays[day] = all;
    }

    // Свежий слитый день из БД (правка там уже применена): объем чтения — текущие счетчики
    // источников ПЛЮС дельты, потому что карта дней в этот момент еще старая.
    private async Task<List<CalendarRecord>> LoadMergedDayFreshAsync(
        DateTime day, List<(CalendarRecord Rec, int Delta)> deltas)
    {
        var d = FindDay(day);
        var all = new List<CalendarRecord>();
        for (var i = 0; i < _srcs.Count; i++)
        {
            var take = (d?.SrcCounts[i] ?? 0)
                       + deltas.Where(x => x.Rec.DbId == _srcs[i].DbId).Sum(x => x.Delta);
            if (take <= 0) continue;
            var part = await _srcs[i].Repo.LoadCalendarDayPageAsync(
                day, day.AddDays(1), _srcs[i].Active, 0, take);
            foreach (var ev in part) ev.DbId = _srcs[i].DbId;
            all.AddRange(part);
        }
        all.Sort(MergedComparer);
        return all;
    }

    // Точечная правка посекундной карты гигантского дня: счетчик секунды записи ± по ее
    // источнику, затем пересчет префикс-сумм (≤ 86 400 бакетов — мгновенно). Полная пересборка
    // карты потребовала бы повторного сканирования дня в БД (секунды на миллионах).
    private void AdjustHugeBuckets(List<HugeBucket> buckets,
                                   List<(CalendarRecord Rec, int Delta)> deltas)
    {
        foreach (var (rec, delta) in deltas)
        {
            var sec = new TimeSpan(rec.StartDt.Hour, rec.StartDt.Minute, rec.StartDt.Second);
            var si  = _srcs.FindIndex(s => s.DbId == rec.DbId);
            var bi  = buckets.FindIndex(b => b.Time == sec);
            if (bi < 0)
            {
                if (delta <= 0) continue;                     // рассинхрон — не ломаемся
                var at = buckets.FindIndex(b => b.Time > sec);
                var nb = new HugeBucket { Time = sec, SrcCounts = new int[_srcs.Count],
                                          SrcCumul = new int[_srcs.Count] };
                buckets.Insert(at < 0 ? buckets.Count : at, nb);
                bi = at < 0 ? buckets.Count - 1 : at;
            }
            var b = buckets[bi];
            b.SrcCounts[si] = Math.Max(0, b.SrcCounts[si] + delta);
        }
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
    public IEnumerator<JournalRow> GetEnumerator() =>
        Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsReadOnly     => true;
    public bool IsFixedSize    => false;
    public bool IsSynchronized => false;
    public object SyncRoot     => this;

    public bool Contains(JournalRow item) => false;
    public int  IndexOf(JournalRow item)  => -1;
    public void CopyTo(JournalRow[] array, int idx) => throw new NotSupportedException();
    public void Add(JournalRow item)           => throw new NotSupportedException();
    public void Clear()                        => throw new NotSupportedException();
    public void Insert(int i, JournalRow item) => throw new NotSupportedException();
    public void RemoveAt(int i)                => throw new NotSupportedException();
    public bool Remove(JournalRow item)        => throw new NotSupportedException();

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
