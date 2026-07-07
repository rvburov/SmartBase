using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DataBase.Controls.Dialogs;
using DataBase.Core.Models;
using DataBase.Database;
using DataBase.Database.Repositories;
using DataBase.Utils;

namespace DataBase.Views.ConstructorDb.Calendar;

public partial class CalendarControllerView : UserControl
{
    public event EventHandler? CollectionsMayHaveChanged;

    // Все смонтированные БД: события агрегируются из всех, CRUD маршрутизируется по DbId.
    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _dbs = [];
    private CategoryRepository _categories = null!;

    private DateTime _currentDate = DateTime.Today;
    private int _currentViewIndex = 4;   // 4 = Месяц
    // Активные коллекции по БД (DbId → набор id). null до первого SetActiveIds.
    private Dictionary<int, HashSet<int>>? _activeByDb;
    private string _searchQuery = "";
    // Созданная/изменённая запись (или последняя копия после дублирования): Журнал и Расписание
    // после обновления остаются на месте, а к САМОЙ записи (с центрированием) едут, только если
    // её строка сейчас не видна. Одноразовая — сбрасывается в RefreshCurrent.
    private CalendarRecord? _journalAnchor;
    // Точечное изменение для Журнала (запись, −1/+1): лента правится НА МЕСТЕ (страницы и кеши
    // дней сохраняются — правка видна в том же кадре) либо мгновенно пересобирается по кешу
    // счётчиков, без полного пересчёта по всем БД (секунды на больших базах, в течение которых
    // была бы видна устаревшая запись). Одноразовое — сбрасывается в RefreshCurrent.
    private List<(CalendarRecord Rec, int Delta)>? _journalDelta;
    // Дублирование (Журнал/Расписание): выделение ПЕРЕХОДИТ с оригиналов на копии. Одноразовый флаг.
    private bool _journalSelectAddedOnly;

    // Сетка (День/Неделя/Месяц/Год): окно читается В ФОНЕ (изолированные подключения) с
    // ограничением GridDayCap timed-событий на день — день-гигант (миллионы записей) не морозит
    // UI и не съедает память, ячейкам больше всё равно не отрисовать; недоставленное показывает
    // честный «+N ещё» по карте счётчиков дней. Поколение отменяет устаревшие загрузки.
    private const int GridDayCap = 2000;
    private long _gridGen;          // текущее поколение загрузки сетки
    private long _gridAppliedGen;   // последнее применённое поколение (для оверлея «Загрузка…»)

    // Снимок последней ПРИМЕНЁННОЙ загрузки сетки — базис точечной правки НА МЕСТЕ
    // (TryApplyGridDelta): создание/изменение/удаление/дубль и перенос/растяжение в Дне/Неделе
    // правят его и перерисовывают вид без чтения окна из БД (полная загрузка окна с
    // днём-гигантом — секунды фоном с оверлеем «Загрузка…», правка же видна в том же кадре).
    // null — базиса нет (был поиск: список отфильтрован, к нему дельта неприменима).
    private List<CalendarRecord>? _gridRecords;
    private Dictionary<DateOnly, int>? _gridTotals;   // карта счётчиков окна (только Месяц/Год)
    // Точные агрегаты групп Диаграммы (фаза 2): счётчик/охват по всем событиям окна.
    private Dictionary<(int DbId, int ColId), DiagramGroupStat>? _diagramStats;
    private DateTime _gridStart, _gridEnd;            // окно снимка
    private int _gridViewApplied = -1;                // вид снимка

    // Каркас групп Диаграммы — отмеченные коллекции всех БД (порядок сайдбара):
    // коллекция видна строкой даже без событий в месяце. Обновляется при каждой
    // полной загрузке вида 6; дельта-путь переиспользует последний список.
    private List<(int DbId, DbCollection Col)>? _diagramCollections;

    // Репозиторий БД по умолчанию (новые события, сидинг) и маршрутизация по записи.
    private ConstructorRepository DefaultRepo => _dbs[0].Repo;
    private ConstructorRepository RepoOf(CalendarRecord r)
    {
        foreach (var d in _dbs) if (d.DbId == r.DbId) return d.Repo;
        return DefaultRepo;
    }

    public CalendarControllerView() { InitializeComponent(); }

    // Размер шрифта меняется живьём: C#-виды строят ячейки с размером из ресурса, поэтому
    // активный вид пересобираем. Дельта последнего рендера — чтобы перерисовать при возврате
    // к календарю (изменение могло произойти, пока был открыт другой раздел).
    private int _renderedFontDelta = FontScale.Delta;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FontScale.Changed += OnFontScaleChanged;
        if (_dbs.Count > 0 && _renderedFontDelta != FontScale.Delta) RefreshCurrent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FontScale.Changed -= OnFontScaleChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFontScaleChanged()
    {
        if (_dbs.Count > 0) RefreshCurrent();
    }

    public void Initialize(
        IReadOnlyList<DatabaseManager> databases,
        CategoryRepository categories)
    {
        SetDatabaseList(databases);
        _categories = categories;

        NavbarControl.TodayClicked  += (_, _) =>
        {
            if (_currentViewIndex == 1) { JournalViewCtrl.GoToday(); return; }
            _currentDate = DateTime.Today; RefreshCurrent(scrollToNow: true);
        };
        NavbarControl.PrevClicked   += (_, _) =>
        {
            if (_currentViewIndex == 1) { JournalViewCtrl.GoPrev(); return; }
            NavigatePrev(); RefreshCurrent();
        };
        NavbarControl.NextClicked   += (_, _) =>
        {
            if (_currentViewIndex == 1) { JournalViewCtrl.GoNext(); return; }
            NavigateNext(); RefreshCurrent();
        };
        NavbarControl.AddClicked    += (_, _) => _ = OpenAddDialog(_currentDate);
        NavbarControl.ViewChanged   += (_, idx) => SwitchView(idx);
        NavbarControl.SearchChanged += (_, q) => { _searchQuery = q; RefreshCurrent(); };

        MonthViewCtrl.DaySelected   += (_, d)  => { _currentDate = d; SwitchView(2); };
        MonthViewCtrl.DayAddClicked += (_, d)  => _ = OpenAddDialog(d);
        YearViewCtrl.MonthSelected += (_, d)  => { _currentDate = d; SwitchView(4); };
        // Поиск в попапе дня Года — фоновый SQL LIKE по ВСЕМУ дню (как в «Расписании»):
        // попап показывает лишь первые записи дня-гиганта, локальный фильтр по ним неполон.
        YearViewCtrl.DaySearchProvider = async (day, q) =>
        {
            var found = new List<CalendarRecord>();
            foreach (var (dbId, _, repo) in _dbs.ToList())
            {
                ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
                var part = await repo.SearchCalendarEventsAsync(day, day.AddDays(1), active, q, 200);
                foreach (var ev in part) ev.DbId = dbId;
                found.AddRange(part);
            }
            return found;
        };
        // Список дня для попапа Года — ПО ТРЕБОВАНИЮ (Год события окна не читает, см.
        // LoadGridAsync): per-day capped-чтение сиками — миллисекунды даже на дне-гиганте.
        YearViewCtrl.DayEventsProvider = async day =>
        {
            var found = new List<CalendarRecord>();
            foreach (var (dbId, _, repo) in _dbs.ToList())
            {
                ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
                var part = await repo.LoadCalendarGridRangeAsync(day, day.AddDays(1), active, 200);
                foreach (var ev in part) ev.DbId = dbId;
                found.AddRange(part);
            }
            return found;
        };

        ListViewCtrl.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        ListViewCtrl.CreateRequested        += (_, d) => _ = OpenAddDialog(d);
        // Удаление ОДНОЙ записи идёт через то же подтверждение, что и удаление нескольких.
        ListViewCtrl.RecordDeleteRequested  += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        ListViewCtrl.RecordsDeleteRequested += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        ListViewCtrl.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        JournalViewCtrl.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        JournalViewCtrl.CreateRequested        += (_, d) => _ = OpenAddDialog(d);
        JournalViewCtrl.RecordDeleteRequested  += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        JournalViewCtrl.RecordsDeleteRequested += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        JournalViewCtrl.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        MonthViewCtrl.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        // Перенос в Месяце — дельтой с ИСХОДНЫМИ датами (само событие уже мутировано drag'ом):
        // −1 старому дню, +1 новому — сетка и карта «+N ещё» правятся на месте, без перезагрузки.
        MonthViewCtrl.RecordMoved            += (_, m) =>
        {
            RepoOf(m.Rec).UpdateCalendarRecord(m.Rec);
            var old = new CalendarRecord
            {
                DbId    = m.Rec.DbId,    Id      = m.Rec.Id,    FieldId = m.Rec.FieldId,
                CollectionId = m.Rec.CollectionId, AllDay = m.Rec.AllDay,
                StartDt = m.OldStart,    EndDt   = m.OldEnd,
                Title   = m.Rec.Title,   Color   = m.Rec.Color,
            };
            _journalDelta = [(old, -1), (m.Rec, +1)];
            NotifyChangedAndRefresh();
        };
        // Дублирование ВЕЗДЕ идёт через подтверждение «Вы дублируете N записей» — единый путь
        // DuplicateRecordsWithConfirm, как в Расписании/Журнале (и для одной записи тоже).
        MonthViewCtrl.RecordDeleteRequested    += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        MonthViewCtrl.RecordsDeleteRequested   += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        MonthViewCtrl.RecordDuplicateRequested += (_, r) => _ = DuplicateRecordsWithConfirm([r]);
        MonthViewCtrl.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        DayViewCtrl.RecordClicked              += (_, r) => _ = OpenEditDialog(r);
        // Перенос/растяжение времени в Дне/Неделе — дельтой (запись та же, ключ (DbId,Id,FieldId)
        // не меняется): сетка правится НА МЕСТЕ без чтения окна. Месяц — полной загрузкой:
        // при переносе между днями старый день записи уже неизвестен (r мутирован drag'ом),
        // карту счётчиков «+N ещё» точно не поправить.
        DayViewCtrl.RecordMoved                += (_, r) => { RepoOf(r).UpdateCalendarRecord(r); _journalDelta = [(r, -1), (r, +1)]; NotifyChangedAndRefresh(); };
        DayViewCtrl.RecordDeleteRequested      += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        DayViewCtrl.RecordsDeleteRequested     += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        DayViewCtrl.RecordDuplicateRequested   += (_, r) => _ = DuplicateRecordsWithConfirm([r]);
        DayViewCtrl.RecordsDuplicateRequested  += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        DayViewCtrl.AllDayCreationRequested    += (_, d)  => _ = OpenAddAllDayDialog(d);
        DayViewCtrl.TimeSlotCreationRequested  += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);
        WeekViewCtrl.RecordClicked             += (_, r) => _ = OpenEditDialog(r);
        WeekViewCtrl.RecordMoved               += (_, r) => { RepoOf(r).UpdateCalendarRecord(r); _journalDelta = [(r, -1), (r, +1)]; NotifyChangedAndRefresh(); };
        WeekViewCtrl.RecordDeleteRequested     += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        WeekViewCtrl.RecordsDeleteRequested    += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        WeekViewCtrl.RecordDuplicateRequested  += (_, r) => _ = DuplicateRecordsWithConfirm([r]);
        WeekViewCtrl.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        WeekViewCtrl.AllDayCreationRequested   += (_, d)  => _ = OpenAddAllDayDialog(d);
        WeekViewCtrl.TimeSlotCreationRequested += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);
        DiagramViewCtrl.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        // Перенос/растяжение дат в Диаграмме — дельтой, как в Дне/Неделе (ключ не меняется).
        DiagramViewCtrl.RecordMoved            += (_, r) => { RepoOf(r).UpdateCalendarRecord(r); _journalDelta = [(r, -1), (r, +1)]; NotifyChangedAndRefresh(); };
        DiagramViewCtrl.RecordDeleteRequested     += (_, r)  => _ = DeleteRecordsWithConfirm([r]);
        DiagramViewCtrl.RecordsDeleteRequested    += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        DiagramViewCtrl.RecordDuplicateRequested  += (_, r)  => _ = DuplicateRecordsWithConfirm([r]);
        DiagramViewCtrl.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        // Смена масштаба Диаграммы (месяц…10 лет) меняет окно данных — полная перезагрузка.
        DiagramViewCtrl.ScaleChanged              += (_, _) => RefreshCurrent();

        SwitchView(AppSettings.LoadCalendarView());   // стартовый вид из настроек (стандарт — Месяц)
        SeedIfEmpty();
    }

    public void SetActiveIds(Dictionary<int, HashSet<int>> activeByDb)
    {
        _activeByDb = activeByDb;
        RefreshCurrent();
    }

    // Обновить набор смонтированных БД (live add/remove) без переподписки обработчиков.
    public void SetDatabases(IReadOnlyList<DatabaseManager> databases)
    {
        SetDatabaseList(databases);
        RefreshCurrent();
    }

    private void SetDatabaseList(IReadOnlyList<DatabaseManager> databases)
    {
        _dbs.Clear();
        foreach (var d in databases) _dbs.Add((d.DbId, d.Name, d.Constructor));
    }

    private void SwitchView(int idx)
    {
        _currentViewIndex = idx;
        ListViewCtrl.IsVisible    = idx == 0;
        JournalViewCtrl.IsVisible = idx == 1;
        DayViewCtrl.IsVisible     = idx == 2;
        WeekViewCtrl.IsVisible    = idx == 3;
        MonthViewCtrl.IsVisible   = idx == 4;
        YearViewCtrl.IsVisible    = idx == 5;
        DiagramViewCtrl.IsVisible = idx == 6;

        NavbarControl.SetView(idx);
        RefreshCurrent(scrollToNow: true);
    }

    private void NavigatePrev()
    {
        _currentDate = _currentViewIndex switch
        {
            0 => _currentDate.AddDays(-1),
            2 => _currentDate.AddDays(-1),
            3 => _currentDate.AddDays(-7),
            4 => _currentDate.AddMonths(-1),
            5 => _currentDate.AddYears(-1),
            6 => DiagramViewCtrl.ScaleYears > 0 ? _currentDate.AddYears(-DiagramViewCtrl.ScaleYears)
                                                : _currentDate.AddMonths(-1),
            _ => _currentDate.AddDays(-1),
        };
    }

    private void NavigateNext()
    {
        _currentDate = _currentViewIndex switch
        {
            0 => _currentDate.AddDays(1),
            2 => _currentDate.AddDays(1),
            3 => _currentDate.AddDays(7),
            4 => _currentDate.AddMonths(1),
            5 => _currentDate.AddYears(1),
            6 => DiagramViewCtrl.ScaleYears > 0 ? _currentDate.AddYears(DiagramViewCtrl.ScaleYears)
                                                : _currentDate.AddMonths(1),
            _ => _currentDate.AddDays(1),
        };
    }

    private void RefreshCurrent(bool scrollToNow = false)
    {
        _renderedFontDelta = FontScale.Delta;   // активный вид отрисован с текущим размером

        var (startDt, endDt) = GetDateRange();

        // Якорь и дельта Журнала — одноразовые: снимаем их при ЛЮБОМ обновлении (даже если
        // активен другой вид), чтобы устаревшая дельта не применилась к ленте позже.
        var anchor = _journalAnchor; _journalAnchor = null;
        var delta  = _journalDelta;  _journalDelta  = null;
        var selectAdded = _journalSelectAddedOnly; _journalSelectAddedOnly = false;

        // Расписание и Журнал — полная виртуализация: событий в окне могут быть миллионы, поэтому
        // список знает лишь их ЧИСЛО (полоса прокрутки на весь объём), а страницы тянет по
        // требованию (см. CalendarDayVirtualList / JournalVirtualList) — прыжок в любое место без фриза.
        if (_currentViewIndex is 0 or 1)
        {
            // Отменяем незавершённую загрузку сетки (применять её данные уже некуда) и оверлей.
            _gridGen++;
            GridLoadingOverlay.IsVisible = false;
            UpdateTitle();
            var sources = _dbs.Select(d =>
                (d.DbId, d.Repo,
                 (ISet<int>?)(_activeByDb is not null && _activeByDb.TryGetValue(d.DbId, out var s) ? s : null)))
                .ToList();
            if (_currentViewIndex == 0)
                ListViewCtrl.LoadFrom(sources, startDt, endDt, _currentDate, _searchQuery,
                                      anchor, delta, selectAdded);
            else
                JournalViewCtrl.LoadFrom(sources, _searchQuery, anchor, delta, selectAdded);   // вся история, без окна дат
            return;
        }

        // Сетка (День/Неделя/Месяц/Год): точечная правка (создание/изменение/удаление/дубль,
        // перенос/растяжение времени в Дне/Неделе) правится НА МЕСТЕ из снимка последней
        // загрузки — без обращения к БД и без оверлея «Загрузка…», как Расписание/Журнал.
        UpdateTitle();
        if (delta is { Count: > 0 } && TryApplyGridDelta(delta, startDt, endDt))
            return;

        // Полная фоновая загрузка окна (навигация/переключение вида/внешнее изменение).
        // Оверлей «Загрузка…» — только если чтение затянулось (>400 мс, холодный день-гигант —
        // секунды); тёплые окна применяются без мигания.
        var gen = ++_gridGen;
        _ = ShowGridLoadingLater(gen);
        _ = LoadGridAsync(gen, _currentViewIndex, startDt, endDt, _currentDate, _searchQuery, scrollToNow);
    }

    // Точечная правка сетки НА МЕСТЕ: мутирует снимок последней загрузки и перерисовывает
    // текущий вид без чтения окна из БД. false — снимок не соответствует текущему окну/виду
    // (навигация, загрузка в полёте, поиск) — вызывающий делает обычную фоновую загрузку.
    private bool TryApplyGridDelta(IReadOnlyList<(CalendarRecord Rec, int Delta)> delta,
                                   DateTime startDt, DateTime endDt)
    {
        if (_gridRecords is null || _gridAppliedGen != _gridGen) return false;
        if (_gridViewApplied != _currentViewIndex || _gridStart != startDt || _gridEnd != endDt) return false;
        if (!string.IsNullOrEmpty(_searchQuery)) return false;

        foreach (var (rec, d) in delta)
        {
            if (d < 0)
            {
                var i = _gridRecords.FindIndex(x =>
                    x.DbId == rec.DbId && x.Id == rec.Id && x.FieldId == rec.FieldId);
                if (i >= 0) _gridRecords.RemoveAt(i);
            }
            else if (rec.StartDt < endDt && rec.EndDt >= startDt)   // пересекает окно (и мультидень)
            {
                // Вставка по порядку StartDt (как отдаёт репозиторий): виды сортируют сами,
                // но снимок остаётся корректным базисом для следующих правок.
                var at = _gridRecords.FindIndex(x => x.StartDt > rec.StartDt);
                _gridRecords.Insert(at < 0 ? _gridRecords.Count : at, rec);
            }

            // Карта счётчиков (честный «+N ещё» Месяца/Года) — по дню НАЧАЛА записи;
            // правится и для записей сверх cap (их нет в снимке, но счётчик дня — о них).
            if (_gridTotals is not null && rec.StartDt >= startDt && rec.StartDt < endDt)
            {
                var day = DateOnly.FromDateTime(rec.StartDt.Date);
                _gridTotals[day] = Math.Max(0, _gridTotals.GetValueOrDefault(day) + d);
            }

            // Агрегаты Диаграммы: счётчик группы ±1; охват на вставке расширяется,
            // на удалении не сжимается (точное сжатие требует пересчёта — устаканится
            // следующей полной загрузкой, бар лишь временно шире).
            if (_diagramStats is not null && rec.CollectionId is int cid
                && rec.StartDt < endDt && rec.EndDt >= startDt)
            {
                var key = (rec.DbId, cid);
                if (!_diagramStats.TryGetValue(key, out var stat))
                {
                    if (d <= 0) continue;
                    _diagramStats[key] = stat = new DiagramGroupStat { MinStart = rec.StartDt };
                }
                stat.Count = Math.Max(0, stat.Count + d);
                if (rec.AllDay) stat.AllDayCount = Math.Max(0, stat.AllDayCount + d);
                if (d > 0)
                {
                    if (rec.StartDt < stat.MinStart) stat.MinStart = rec.StartDt;
                    if (rec.AllDay)  stat.MaxEndAllDay = Max(stat.MaxEndAllDay, rec.EndDt);
                    else             stat.MaxEndTimed  = Max(stat.MaxEndTimed,  rec.EndDt);
                }
                if (stat.Count == 0) _diagramStats.Remove(key);

                static DateTime? Max(DateTime? a, DateTime b) => a is null || b > a ? b : a;
            }
        }

        ApplyGridData(_currentViewIndex, _gridRecords, _gridTotals, _currentDate, scrollToNow: false);
        return true;
    }

    // Фоновая загрузка окна сетки поколения gen; применяется, только если поколение не устарело.
    // ДВЕ фазы: (1) события окна — индексные сики, миллисекунды, сетка показывается сразу
    // (оверлей «Загрузка…» не успевает появиться); (2) для Месяца/Года — карта счётчиков дней
    // («+N ещё»/числа на днях): её GROUP BY на окне с днём-гигантом — секунды, поэтому она
    // доезжает фоном и ТИХО уточняет уже показанную сетку.
    private async Task LoadGridAsync(long gen, int viewIdx, DateTime startDt, DateTime endDt,
                                     DateTime currentDate, string query, bool scrollToNow)
    {
        var records   = new List<CalendarRecord>();
        var hasSearch = !string.IsNullOrEmpty(query);
        // Год без поиска события окна НЕ читает вовсе: числам достаточно карты счётчиков
        // (фаза 2), а список дня попап грузит ПО ТРЕБОВАНИЮ (DayEventsProvider). Чтение всех
        // событий года (сотни тысяч строк, ~0.7–1.5 с) было последним местом сетки, где
        // оверлей «Загрузка…» ещё успевал появиться при листании.
        var skipEvents = viewIdx == 5 && !hasSearch;
        try
        {
            if (!skipEvents)
                foreach (var (dbId, _, repo) in _dbs.ToList())
                {
                    ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
                    // Диаграмма — свой загрузчик «по коллекциям» (cap на коллекцию, а не на
                    // день): её окно бывает десятилетием, и per-day чтение сеток стоило бы
                    // тысяч запросов и миллионов строк при потолке отрисовки ~200 на группу.
                    var part = viewIdx == 6
                        ? await repo.LoadDiagramWindowAsync(startDt, endDt, active, DiagramView.MaxRecordsPerCollection)
                        : await repo.LoadCalendarGridRangeAsync(startDt, endDt, active, GridDayCap);
                    if (gen != _gridGen) return;           // вид/дата уже другие — не применяем
                    foreach (var r in part) r.DbId = dbId;
                    records.AddRange(part);
                }
        }
        catch
        {
            // БД недоступна/занята — оставляем прежнее содержимое вида, оверлей снимаем.
            if (gen == _gridGen) { _gridAppliedGen = gen; GridLoadingOverlay.IsVisible = false; }
            return;
        }

        // Поиск фильтрует прочитанное (как раньше); карта счётчиков к отфильтрованному
        // неприменима — «+N ещё» при поиске считается от фактически показанного.
        if (hasSearch)
            records = records.Where(r => r.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _gridAppliedGen = gen;
        GridLoadingOverlay.IsVisible = false;

        // Снимок — базис точечных правок (TryApplyGridDelta); при поиске список отфильтрован
        // и базисом не годится (правка при активном поиске пойдёт полной загрузкой).
        _gridRecords     = hasSearch ? null : records;
        _gridTotals      = null;
        _diagramStats    = null;
        _gridStart       = startDt;
        _gridEnd         = endDt;
        _gridViewApplied = viewIdx;

        // Каркас групп Диаграммы: коллекции — крошечная таблица, читается на месте.
        if (viewIdx == 6) _diagramCollections = LoadDiagramCollections();

        ApplyGridData(viewIdx, records, null, currentDate, scrollToNow);

        // Фаза 2 Диаграммы: точные агрегаты групп (счётчик, «+N ещё», охват суммарного
        // бара по ВСЕМ событиям окна, а не по обрезанным cap'ом) — индексные сканы,
        // на больших окнах секунды, поэтому фоном и тихо, как карта счётчиков Месяца/Года.
        // При поиске не считаются — счётчики идут от фактически показанного.
        if (viewIdx == 6)
        {
            if (hasSearch) return;
            var stats = new Dictionary<(int DbId, int ColId), DiagramGroupStat>();
            try
            {
                foreach (var (dbId, _, repo) in _dbs.ToList())
                {
                    ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
                    foreach (var (colId, stat) in await repo.LoadDiagramStatsAsync(startDt, endDt, active))
                        stats[(dbId, colId)] = stat;
                    if (gen != _gridGen) return;
                }
            }
            catch { return; }   // агрегаты не критичны — счётчики останутся от полученного

            _diagramStats = stats;
            ApplyGridData(viewIdx, _gridRecords ?? records, null, currentDate, scrollToNow: false);
            return;
        }

        // Фаза 2: карта счётчиков — только Месяц/Год и не при поиске. До её прихода «+N ещё»
        // считается от фактически полученного; по приходе — тихое повторное применение.
        if (hasSearch || viewIdx is not (4 or 5)) return;
        var totals = new Dictionary<DateOnly, int>();
        try
        {
            foreach (var (dbId, _, repo) in _dbs.ToList())
            {
                ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
                foreach (var (dayStr, count) in await repo.LoadCalendarDayCountsAsync(startDt, endDt, active))
                    if (DateOnly.TryParseExact(dayStr, "yyyy-MM-dd", out var d))
                        totals[d] = totals.GetValueOrDefault(d) + count;
                if (gen != _gridGen) return;
            }
        }
        catch { return; }   // карта не критична — «+N ещё» останется от полученного

        _gridTotals = totals;
        // Между фазами могла пройти точечная правка (TryApplyGridDelta мутирует _gridRecords,
        // поколение не меняя) — применяем АКТУАЛЬНЫЙ снимок, а не локальный records.
        ApplyGridData(viewIdx, _gridRecords ?? records, totals, currentDate, scrollToNow: false);
    }

    // Отдать данные видимому виду сетки — общий финал полной загрузки и точечной правки.
    private void ApplyGridData(int viewIdx, List<CalendarRecord> records,
                               Dictionary<DateOnly, int>? totals, DateTime currentDate, bool scrollToNow)
    {
        switch (viewIdx)
        {
            case 2: DayViewCtrl.Refresh(records, DateOnly.FromDateTime(currentDate)); break;
            case 3: WeekViewCtrl.Refresh(records, currentDate, scrollToNow); break;
            case 4: MonthViewCtrl.Refresh(records, currentDate, totals); break;
            case 5: YearViewCtrl.Refresh(records, currentDate.Year, totals); break;
            case 6: DiagramViewCtrl.Refresh(records, currentDate, scrollToNow,
                                            _diagramCollections,
                                            hideEmptyGroups: !string.IsNullOrEmpty(_searchQuery),
                                            stats: _diagramStats); break;
        }
    }

    // Отмеченные коллекции всех БД — каркас групп Диаграммы (пустой набор активных = ни одной,
    // как у фильтра чтения событий).
    private List<(int DbId, DbCollection Col)> LoadDiagramCollections()
    {
        var result = new List<(int, DbCollection)>();
        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            foreach (var col in repo.GetAllCollections())
                if (active is null || (col.Id.HasValue && active.Contains(col.Id.Value)))
                    result.Add((dbId, col));
        }
        return result;
    }

    // Оверлей «Загрузка…» с задержкой: быстрые (тёплые) загрузки сетки им не мигают.
    private async Task ShowGridLoadingLater(long gen)
    {
        await Task.Delay(400);
        if (gen == _gridGen && gen != _gridAppliedGen)
            GridLoadingOverlay.IsVisible = true;
    }

    private (DateTime start, DateTime end) GetDateRange() => _currentViewIndex switch
    {
        2 => (_currentDate.Date, _currentDate.Date.AddDays(1)),
        3 => (StartOfWeek(_currentDate), StartOfWeek(_currentDate).AddDays(7)),
        4 => (StartOfWeek(new DateTime(_currentDate.Year, _currentDate.Month, 1)),
              StartOfWeek(new DateTime(_currentDate.Year, _currentDate.Month, 1)).AddDays(42)),
        5 => (new DateTime(_currentDate.Year, 1, 1), new DateTime(_currentDate.Year + 1, 1, 1)),
        // Диаграмма — окно по масштабу (RangeFor): календарный месяц (не
        // 42-дневная сетка Месяца) либо 1/2/5/10 лет, выровненных по календарю.
        // В отличие от вида «Год», события многолетнего окна ЧИТАЮТСЯ (бары нужны
        // целиком) — на больших стендах это сотни тысяч строк и секунды фоном
        // под оверлеем «Загрузка…».
        6 => DiagramViewCtrl.RangeFor(_currentDate),
        0 => (_currentDate.Date, _currentDate.Date.AddDays(1)),
        _ => (DateTime.Today.AddDays(-365), DateTime.Today.AddDays(365)),
    };

    private static DateTime StartOfWeek(DateTime d)
    {
        var diff = (int)d.DayOfWeek - (int)DayOfWeek.Monday;
        if (diff < 0) diff += 7;
        return d.AddDays(-diff).Date;
    }

    private static readonly Avalonia.Media.IBrush BrushRed =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF3B30"));
    private static readonly Avalonia.Media.IBrush BrushSecondary =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8E8E93"));
    private static readonly Avalonia.Media.IBrush BrushPrimary =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1C1C1E"));

    private void UpdateTitle()
    {
        var ru    = new System.Globalization.CultureInfo("ru-RU");
        var today = DateTime.Today;

        if (_currentViewIndex == 0)
        {
            var (text, brush) = _currentDate.Date switch
            {
                var d when d == today => (_currentDate.ToString("dddd, d MMMM yyyy", ru), BrushRed),
                var d when d < today  => (_currentDate.ToString("dddd, d MMMM yyyy", ru), BrushSecondary),
                _                     => (_currentDate.ToString("dddd, d MMMM yyyy", ru), BrushPrimary),
            };
            NavbarControl.SetTitle(text, brush);
            return;
        }

        NavbarControl.SetTitle(_currentViewIndex switch
        {
            2 => _currentDate.ToString("dddd, d MMMM yyyy", ru),
            3 => $"{StartOfWeek(_currentDate):d MMM} – {StartOfWeek(_currentDate).AddDays(6):d MMM yyyy}",
            4 => _currentDate.ToString("MMMM yyyy", ru),
            5 => _currentDate.Year.ToString(),
            6 => DiagramViewCtrl.TitleFor(_currentDate),   // «июль 2026» / «2026» / «2020–2029»
            _ => "Все записи",
        });
    }

    private async Task OpenAddAllDayDialog(DateOnly date)
    {
        if (VisualRoot is not Avalonia.Controls.Window win || _dbs.Count == 0) return;

        // Выбор целевой БД встроен в само окно (показывается при нескольких БД).
        var dlg = new RecordDialog(_dbs, _dbs[0].DbId,
                                   presetDate: date.ToDateTime(TimeOnly.MinValue),
                                   presetAllDay: true);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result?.Record is not null)
        {
            RepoOf(result.Record).AddCalendarRecord(result.Record);
            _journalAnchor = result.Record;   // Журнал: к созданной записи
            _journalDelta  = [(result.Record, +1)];
            NotifyChangedAndRefresh();
        }
    }

    private async Task OpenAddDialog(DateTime presetDate, TimeSpan? presetTime = null)
    {
        if (VisualRoot is not Avalonia.Controls.Window win || _dbs.Count == 0) return;

        var dlg = new RecordDialog(_dbs, _dbs[0].DbId, presetDate: presetDate, presetTime: presetTime);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result?.Record is not null)
        {
            RepoOf(result.Record).AddCalendarRecord(result.Record);
            _journalAnchor = result.Record;   // Журнал: к созданной записи
            _journalDelta  = [(result.Record, +1)];
            NotifyChangedAndRefresh();
        }
    }

    // ОДИН refresh на операцию: дельта ставится ДО CollectionsMayHaveChanged — его обработчик
    // (MainWindow: сайдбар → SetActiveIds → RefreshCurrent) потребляет её и правит ленту на
    // месте; страховочный RefreshCurrent срабатывает, только если дельту никто не съел (нет
    // подписчика). Прежний порядок (Invoke ДО дельты) запускал холостую пересборку без дельты —
    // её отменяло поколение, но фоновый пересчёт дня (COUNT дня-гиганта — секунды) уходил
    // впустую на каждую правку, а кеш счётчиков дня было бы невозможно держать тёплым.
    private void NotifyChangedAndRefresh()
    {
        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        if (_journalDelta is not null) RefreshCurrent();
    }

    private async Task OpenEditDialog(CalendarRecord r)
    {
        if (VisualRoot is not Avalonia.Controls.Window win || _dbs.Count == 0) return;

        // Выбор БД встроен в окно (при нескольких БД); запись с целевым DbId возвращается.
        var dlg = new RecordDialog(_dbs, r.DbId, editRecord: r);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result is null) return;

        if (result.Deleted)
        {
            RepoOf(r).DeleteCalendarRecord(r.Id!.Value);
            _journalDelta = [(r, -1)];
        }
        else if (result.Record is not null)
        {
            var rec = result.Record;
            // Диалог оперирует МИНУТАМИ — у времени с секундами (импорт/массовые данные) секунды
            // терялись при каждом сохранении: правка «только названия» молча меняла время
            // (14:48:37 → 14:48:00), и запись реально переезжала внутри дня — первая правка
            // «прыгала» с прогрузкой. Минуты не менялись — возвращаем исходные секунды.
            if (rec.StartDt == MinuteFloor(r.StartDt)) rec.StartDt = r.StartDt;
            if (rec.EndDt   == MinuteFloor(r.EndDt))   rec.EndDt   = r.EndDt;
            if (rec.DbId == r.DbId)
            {
                var repo = RepoOf(rec);
                // Перенос даты в другое поле той же записи (та же коллекция): очищаем старое
                // поле, чтобы дата не осталась в обоих — событие переезжает целиком.
                if (rec.CollectionId == r.CollectionId && r.FieldId > 0 &&
                    rec.FieldId != r.FieldId && r.Id is int rid)
                    repo.SetFieldValue(rid, r.FieldId, "");
                repo.UpdateCalendarRecord(rec);
            }
            else
            {
                // БД сменили — переносим запись: создаём в целевой базе, грамотно переносим значения
                // свойств (по имени, с приведением типов), удаляем из исходной. Поле-дату исключаем,
                // чтобы не затереть выставленную AddCalendarRecord дату.
                var sourceRepo = RepoOf(r);
                var targetRepo = RepoOf(rec);
                targetRepo.AddCalendarRecord(rec);
                targetRepo.CopyFieldValuesByName(sourceRepo, r.Id!.Value, r.CollectionId ?? 0,
                                                 rec.Id!.Value, rec.CollectionId ?? 0,
                                                 createMissingFields: false, excludeTargetFieldId: rec.FieldId);
                sourceRepo.DeleteCalendarRecord(r.Id!.Value);
            }
            _journalAnchor = result.Record;   // Журнал: к записи на её новой дате
            _journalDelta  = [(r, -1), (result.Record, +1)];
            NotifyChangedAndRefresh();
            return;
        }
        if (_journalDelta is not null) RefreshCurrent();   // удаление из диалога (Deleted выше)
    }

    private static CalendarRecord CopyOf(CalendarRecord r) => new()
    {
        DbId         = r.DbId,
        CollectionId = r.CollectionId,
        FieldId      = r.FieldId,   // дубль кладём в то же поле-дату, что и оригинал
        Title        = r.Title,
        Description  = r.Description,
        StartDt      = r.StartDt,
        EndDt        = r.EndDt,
        AllDay       = r.AllDay,
        Color        = r.Color,
    };

    // Дублирование из Журнала/Расписания — с подтверждением, как удаление. Копии встают в ленту
    // по порядку времени (тот же ключ сортировки, тай-брейк по новому Id — сразу после оригинала),
    // выделение ПЕРЕХОДИТ с оригиналов на копии, а лента едет к последней по дате/времени
    // копии, только если её строка сейчас не видна.
    private async Task DuplicateRecordsWithConfirm(IReadOnlyList<CalendarRecord> records)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var targets = records.Where(r => r.Id.HasValue).ToList();
        if (targets.Count == 0) return;

        var dlg = new ConfirmDialog($"Вы дублируете {targets.Count} {RecordsPlural(targets.Count)}. Подтвердить?",
                                    confirmText: "Дублировать");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        // Немного записей — прежний мгновенный путь (AddCalendarRecord по одной). Много —
        // bulk-вставки одной транзакцией в фоне с окном прогресса (как массовое удаление):
        // цикл с двумя SaveChanges и MAX(Position) на КАЖДУЮ запись вешал UI на сотнях/тысячах.
        List<CalendarRecord> copies;
        if (targets.Count <= SmallOpCap)
        {
            copies = new List<CalendarRecord>(targets.Count);
            foreach (var r in targets)
            {
                var copy = CopyOf(r);
                RepoOf(r).AddCalendarRecord(copy);   // проставляет Id/FieldId — ключ строки копии
                copies.Add(copy);
            }
        }
        else
            copies = await BulkDuplicateWithProgress(win, targets);

        if (copies.Count > 0)
        {
            _journalAnchor = copies.OrderBy(c => c.StartDt).ThenBy(c => c.Id ?? 0).Last();
            _journalDelta  = copies.Select(c => (c, +1)).ToList();
            _journalSelectAddedOnly = true;
        }
        NotifyChangedAndRefresh();
    }

    private async Task DeleteRecordsWithConfirm(IReadOnlyList<CalendarRecord> records)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var targets = records.Where(r => r.Id.HasValue).ToList();
        if (targets.Count == 0) return;

        var dlg = new ConfirmDialog($"Вы удаляете {targets.Count} {RecordsPlural(targets.Count)}. Подтвердить?");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        // Немного записей — прежний мгновенный путь (каждую в её БД). Много — bulk-SQL в фоне
        // на изолированных подключениях с окном прогресса (как удаление коллекции в сайдбаре):
        // цикл DeleteCalendarRecord с SaveChanges на КАЖДУЮ запись вешал UI на сотнях/тысячах.
        if (targets.Count <= SmallOpCap)
            foreach (var r in targets) RepoOf(r).DeleteCalendarRecord(r.Id!.Value);
        else
            await BulkDeleteWithProgress(win, targets);

        _journalDelta = targets.Select(r => (r, -1)).ToList();
        NotifyChangedAndRefresh();
    }

    // Порог «мелкой» операции: до этого числа записи удаляются/дублируются мгновенно без окна.
    private const int SmallOpCap = 100;

    // Массовое удаление: по каждой БД — DeleteRecordsIsolated (bulk-SQL пакетами, мимо EF;
    // значения и calendar_index уходят каскадом по FK) в фоне с окном прогресса.
    private Task BulkDeleteWithProgress(Avalonia.Controls.Window win, List<CalendarRecord> targets) =>
        RunCalendarBulk(win, "Удаление", "Удаление записей…", targets.Count, report =>
        {
            long done = 0;
            foreach (var g in targets.GroupBy(r => r.DbId))
            {
                var repo = RepoOf(g.First());
                var ids  = g.Select(r => r.Id!.Value).ToList();
                var baseDone = done;
                repo.DeleteRecordsIsolated(ids, new IntProgress(v => report(baseDone + v)));
                done += ids.Count;
            }
            report(targets.Count);
        });

    // Массовое дублирование: по каждой БД — DuplicateCalendarRecordsIsolated (одна транзакция,
    // батчевые вставки шапки/поля-даты/индекса) в фоне с окном прогресса. Возвращает копии
    // (для выделения, дельты Журнала и якоря прокрутки).
    private async Task<List<CalendarRecord>> BulkDuplicateWithProgress(
        Avalonia.Controls.Window win, List<CalendarRecord> targets)
    {
        var copies = new List<CalendarRecord>();   // наполняется в фоне, читается после await
        await RunCalendarBulk(win, "Дублирование", "Дублирование записей…", targets.Count, report =>
        {
            long done = 0;
            foreach (var g in targets.GroupBy(r => r.DbId))
            {
                var repo = RepoOf(g.First());
                var baseDone = done;
                var part = repo.DuplicateCalendarRecordsIsolated(
                    g.ToList(), new IntProgress(v => report(baseDone + v)));
                copies.AddRange(part);
                done += g.Count();
            }
            report(targets.Count);
        });
        return copies;
    }

    // Общий помощник массовых операций календаря: bulk-работа в фоне; окно прогресса
    // (заголовок, фаза, «Количество записей: N», проценты — как удаление коллекции в сайдбаре)
    // появляется, только если операция не уложилась в задержку; средние объёмы проходят без
    // окна. В конце — «Сохранение…» (коммит транзакции). report зовётся из фона — маршалим сами.
    private async Task RunCalendarBulk(Avalonia.Controls.Window win, string title, string phase,
                                       int total, Action<Action<long>> work)
    {
        ProgressDialog? dlg = null;
        var lastPct = -1;

        void Update(long done)
        {
            if (dlg is null) return;
            var pct = (int)Math.Min(100, done * 100L / total);
            if (pct == lastPct) return;
            lastPct = pct;
            if (pct >= 100) dlg.SetSaving("Сохранение…");
            else dlg.SetProgress(pct / 100.0, phase);
        }

        var bg = Task.Run(() =>
        {
            try
            {
                work(done => Dispatcher.UIThread.Post(() => Update(done)));
            }
            catch
            {
                // Фон: ошибку не валим в UI — фактическое состояние покажет обновление ленты.
            }
        });

        // Успело быстро — окна не показываем вовсе.
        if (await Task.WhenAny(bg, Task.Delay(400)) == bg) { await bg; return; }

        dlg = new ProgressDialog { Title = title };
        dlg.SetInfo($"Количество записей: {total}");
        dlg.SetProgress(0, phase);
        var closed = dlg.ShowDialog(win);
        await bg;
        dlg.Done();
        await closed;
    }

    // Адаптер IProgress<int> для прогресса с фонового потока (передаётся в *Isolated-методы).
    private sealed class IntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    // Начало минуты — для сравнения времени из диалога (минутная точность) с исходным.
    private static DateTime MinuteFloor(DateTime d) =>
        new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Kind);

    // Русское склонование: 1 запись, 2–4 записи, 5+ записей (с учётом 11–14).
    private static string RecordsPlural(int n)
    {
        var mod100 = n % 100;
        var mod10  = n % 10;
        if (mod100 is >= 11 and <= 14) return "записей";
        return mod10 switch
        {
            1 => "запись",
            >= 2 and <= 4 => "записи",
            _ => "записей",
        };
    }

    // Первый запуск (в БД нет ни одного календарного события) — заводим вводный чек-лист
    // из трёх задач-инструкций в коллекции «Задачи» основной БД. Цвет события берётся из
    // цвета коллекции (см. ReadCalendarEvents), поэтому синяя коллекция даёт синие события.
    private void SeedIfEmpty()
    {
        if (DefaultRepo.CountCalendarRecords() > 0) return;

        var colId = (DefaultRepo.GetAllCollections().FirstOrDefault(c => c.Name == "Задачи")
                     ?? DefaultRepo.AddCollection(new DbCollection { Name = "Задачи", Color = "#007AFF" }))
                    .Id!.Value;

        var today = DateTime.Today;
        DefaultRepo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = colId,
            Title        = "Изучить раздел «Календарь»",
            Description  = "Откройте раздел «Календарь» на боковой панели. Переключайте виды: " +
                           "Расписание, Журнал, День, Неделя, Месяц, Год. Создайте событие кнопкой «+», " +
                           "перетащите его на другое время и откройте для редактирования. Слева отмечайте " +
                           "коллекции, чтобы фильтровать события, и попробуйте поиск.",
            StartDt      = today.AddHours(10),
            EndDt        = today.AddHours(11),
        });
        DefaultRepo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = colId,
            Title        = "Изучить раздел «Конструктор БД»",
            Description  = "Откройте «Конструктор БД». Создайте коллекцию и добавьте свойства " +
                           "(текст, число, дата, список). Заполните несколько записей, переключайте вид " +
                           "Таблица/Список, настройте ширину и порядок колонок, сгруппируйте коллекции " +
                           "в дереве слева.",
            StartDt      = today.AddDays(1).AddHours(10),
            EndDt        = today.AddDays(1).AddHours(11),
        });
        DefaultRepo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = colId,
            Title        = "Изучить раздел «Расширения»",
            Description  = "Откройте раздел «Расширения». Ознакомьтесь с доступными инструментами — " +
                           "анализ PDF и переименование PDF. Выберите расширение, укажите файлы и запустите " +
                           "обработку, чтобы увидеть результат.",
            StartDt      = today.AddDays(2).AddHours(10),
            EndDt        = today.AddDays(2).AddHours(11),
        });
    }
}
