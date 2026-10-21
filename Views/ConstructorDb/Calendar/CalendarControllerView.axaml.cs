using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Calendar;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Views.ConstructorDb.Calendar;

public partial class CalendarControllerView : UserControl
{
    public event EventHandler? CollectionsMayHaveChanged;

    // Все смонтированные БД: события агрегируются из всех, CRUD маршрутизируется по DbId.
    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _dbs = [];
    private CategoryRepository _categories = null!;

    private DateTime _currentDate = DateTime.Today;
    private int _currentViewIndex = 4;   // 4 = Месяц
    // Окно «Недели» центрировано на сегодня (режим прокрутки «с остановкой на „Сегодня"»):
    // выставляется StepWeekAnchor/кнопкой «Сегодня», сбивается навигацией в других видах.
    private bool _weekCentered;
    // Активные коллекции по БД (DbId → набор id). null до первого SetActiveIds.
    private Dictionary<int, HashSet<int>>? _activeByDb;
    private string _searchQuery = "";
    // Созданная/измененная запись (или последняя копия после дублирования): Журнал и Расписание
    // после обновления остаются на месте, а к САМОЙ записи (с центрированием) едут, только если
    // ее строка сейчас не видна. Одноразовая — сбрасывается в RefreshCurrent.
    private CalendarRecord? _journalAnchor;
    // Точечное изменение для Журнала (запись, −1/+1): лента правится НА МЕСТЕ (страницы и кеши
    // дней сохраняются — правка видна в том же кадре) либо мгновенно пересобирается по кешу
    // счетчиков, без полного пересчета по всем БД (секунды на больших базах, в течение которых
    // была бы видна устаревшая запись). Одноразовое — сбрасывается в RefreshCurrent.
    private List<(CalendarRecord Rec, int Delta)>? _journalDelta;
    // Дублирование (Журнал/Расписание): выделение ПЕРЕХОДИТ с оригиналов на копии. Одноразовый флаг.
    private bool _journalSelectAddedOnly;
    // Сетка (День/Неделя/Месяц/Диаграмма): что выделить после правки — созданную запись, копии
    // (выделение переходит с оригиналов), перенесенную или измененную, как после нажатия по ней.
    // Одноразовое, как дельта.
    private List<CalendarRecord>? _selectAfter;

    // Сетка (День/Неделя/Месяц/Год): окно читается В ФОНЕ (изолированные подключения) с
    // ограничением GridDayCap timed-событий на день — день-гигант (миллионы записей) не морозит
    // UI и не съедает память, ячейкам больше все равно не отрисовать; недоставленное показывает
    // честный «+N еще» по карте счетчиков дней. Поколение отменяет устаревшие загрузки.
    private const int GridDayCap = 2000;

    // Месяцу timed-событий дня нужно не больше, чем влезает в ячейку (строк 13–14 даже на
    // высоком окне), остальное считает «+N еще» по карте счетчиков. Читать по 2000 на день
    // значило тянуть в окно месяца до 84 тысяч событий на днях-гигантах и строить по ним
    // раскладку. Столько же на день читаются многодневные timed, начинающиеся в этот день
    // (отдельным проходом по концам): раньше они шли вместе с однодневными и в переполненном
    // дне, начавшись поздно, отсекались тем же cap. Записи «весь день» — так же по дням: с общим
    // cap на окно месяц с тысячами таких записей в день отдавал их двум-трем первым дням, а в
    // остальных ячейках оставалось одно «+N еще». Общий cap — только начатым до окна.
    private const int MonthDayCap = 24;
    private long _gridGen;          // текущее поколение загрузки сетки
    private long _gridAppliedGen;   // последнее примененное поколение (для оверлея «Загрузка…»)

    // Снимок последней ПРИМЕНЕННОЙ загрузки сетки — базис точечной правки НА МЕСТЕ
    // (TryApplyGridDelta): создание/изменение/удаление/дубль и перенос/растяжение в Дне/Неделе
    // правят его и перерисовывают вид без чтения окна из БД (полная загрузка окна с
    // днем-гигантом — секунды фоном с оверлеем «Загрузка…», правка же видна в том же кадре).
    // null — базиса нет (был поиск: список отфильтрован, к нему дельта неприменима).
    private List<CalendarRecord>? _gridRecords;
    private Dictionary<DateOnly, int>? _gridTotals;   // карта счетчиков окна (только Месяц/Год)
    // Точные агрегаты групп Диаграммы (фаза 2): счетчик/охват по всем событиям окна.
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

    // Запись в БД календаря с обработкой ошибок многопользовательского режима: файл занят
    // чужим писателем дольше busy_timeout или пропал → тост (Utils/DbErrors), возврат false —
    // вызывающий перечитывает вид, чтобы событие «отпрыгнуло» к фактическому состоянию БД.
    private static bool TryWrite(ConstructorRepository repo, Action write)
    {
        try { write(); return true; }
        catch (Exception ex) when (SmartBase.Utils.DbErrors.Handle(ex, repo.DbPath)) { return false; }
    }

    // То же в фоне: запись идет своим подключением (методы …Isolated репозитория), UI-поток ее не
    // ждет; ошибка разбирается уже на нем, как у TryWrite.
    private static async Task<bool> TryWriteAsync(ConstructorRepository repo, Action write)
    {
        try { await Task.Run(write); return true; }
        catch (Exception ex) when (SmartBase.Utils.DbErrors.Handle(ex, repo.DbPath)) { return false; }
    }

    // Виды строятся при первом переходе в них: раздел показывает один вид, а семь сразу стоили
    // первому показу календаря около 0,1 с на построение, стили и привязки невидимых («День»,
    // «Год», «Диаграмма» — по 30–40 мс). Подписки вида — при его построении (Wire).
    private ListView?    _listView;
    private JournalView? _journalView;
    private DayView?     _dayView;
    private WeekView?    _weekView;
    private MonthView?   _monthView;
    private YearView?    _yearView;
    private DiagramView? _diagramView;

    private ListView    ListViewCtrl    => _listView    ?? (_listView    = AddView(new ListView(),    nameof(ListViewCtrl),    Wire));
    private JournalView JournalViewCtrl => _journalView ?? (_journalView = AddView(new JournalView(), nameof(JournalViewCtrl), Wire));
    private DayView     DayViewCtrl     => _dayView     ?? (_dayView     = AddView(new DayView(),     nameof(DayViewCtrl),     Wire));
    private WeekView    WeekViewCtrl    => _weekView    ?? (_weekView    = AddView(new WeekView(),    nameof(WeekViewCtrl),    Wire));
    private MonthView   MonthViewCtrl   => _monthView   ?? (_monthView   = AddView(new MonthView(),   nameof(MonthViewCtrl),   Wire));
    private YearView    YearViewCtrl    => _yearView    ?? (_yearView    = AddView(new YearView(),    nameof(YearViewCtrl),    Wire));
    private DiagramView DiagramViewCtrl => _diagramView ?? (_diagramView = AddView(new DiagramView(), nameof(DiagramViewCtrl), Wire));

    private T AddView<T>(T view, string name, Action<T> wire) where T : Control
    {
        view.Name      = name;
        view.IsVisible = false;
        ViewsHost.Children.Add(view);
        wire(view);
        return view;
    }

    // Вид по индексу навбара (строит его при первом обращении).
    private Control ViewAt(int idx) => idx switch
    {
        0 => ListViewCtrl,
        1 => JournalViewCtrl,
        2 => DayViewCtrl,
        3 => WeekViewCtrl,
        5 => YearViewCtrl,
        6 => DiagramViewCtrl,
        _ => MonthViewCtrl,
    };

    public CalendarControllerView() { InitializeComponent(); }

    // Размер шрифта меняется живьем: C#-виды строят ячейки с размером из ресурса, поэтому
    // активный вид пересобираем. Дельта последнего рендера — чтобы перерисовать при возврате
    // к календарю (изменение могло произойти, пока был открыт другой раздел).
    private int _renderedFontDelta = FontScale.Delta;
    // То же для настроек календаря (дней в неделе, рабочие часы, прокрутка недели).
    private long _renderedCalendarStamp = CalendarSettings.Stamp;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FontScale.Changed        += OnFontScaleChanged;
        CalendarSettings.Changed += OnCalendarSettingsChanged;
        if (_dbs.Count > 0 && (_renderedFontDelta != FontScale.Delta
                               || _renderedCalendarStamp != CalendarSettings.Stamp))
            RefreshCurrent();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        FontScale.Changed        -= OnFontScaleChanged;
        CalendarSettings.Changed -= OnCalendarSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFontScaleChanged()
    {
        if (_dbs.Count > 0) RefreshCurrent();
    }

    private void OnCalendarSettingsChanged()
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
            _currentDate = DateTime.Today;
            // «С остановкой на „Сегодня"»: кнопка тоже центрирует окно недели.
            _weekCentered = _currentViewIndex == 3
                && CalendarSettings.WeekScrollMode == CalendarWeekScrollMode.ByWeekStopToday;
            RefreshCurrent(scrollToNow: true);
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

        SwitchView(AppSettings.LoadCalendarView());   // стартовый вид из настроек (стандарт — Месяц)
    }

    private void Wire(ListView v)
    {
        v.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        v.CreateRequested        += (_, d) => _ = OpenAddDialog(d);
        // Удаление ОДНОЙ записи идет через то же подтверждение, что и удаление нескольких.
        v.RecordDeleteRequested  += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        v.RecordsDeleteRequested += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        v.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
    }

    private void Wire(JournalView v)
    {
        v.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        v.CreateRequested        += (_, d) => _ = OpenAddDialog(d);
        v.RecordDeleteRequested  += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        v.RecordsDeleteRequested += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        v.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
    }

    private void Wire(DayView v)
    {
        v.RecordClicked              += (_, r) => _ = OpenEditDialog(r);
        // Перенос/растяжение времени в Дне/Неделе — дельтой (запись та же, ключ (DbId,Id,FieldId)
        // не меняется): сетка правится НА МЕСТЕ без чтения окна. Месяц — полной загрузкой:
        // при переносе между днями старый день записи уже неизвестен (r мутирован drag'ом),
        // карту счетчиков «+N еще» точно не поправить.
        v.RecordMoved                += (_, r) => { if (!TryWrite(RepoOf(r), () => RepoOf(r).UpdateCalendarRecord(r))) { RefreshCurrent(); return; } _journalDelta = [(r, -1), (r, +1)]; _selectAfter = [r]; NotifyChangedAndRefresh(structural: false); };
        v.RecordDeleteRequested      += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        v.RecordsDeleteRequested     += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        v.RecordDuplicateRequested   += (_, r) => _ = DuplicateRecordsWithConfirm([r]);
        v.RecordsDuplicateRequested  += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        v.AllDayCreationRequested    += (_, d)  => _ = OpenAddAllDayDialog(d);
        v.TimeSlotCreationRequested  += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);
    }

    private void Wire(WeekView v)
    {
        v.RecordClicked             += (_, r) => _ = OpenEditDialog(r);
        v.RecordMoved               += (_, r) => { if (!TryWrite(RepoOf(r), () => RepoOf(r).UpdateCalendarRecord(r))) { RefreshCurrent(); return; } _journalDelta = [(r, -1), (r, +1)]; _selectAfter = [r]; NotifyChangedAndRefresh(structural: false); };
        v.RecordDeleteRequested     += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        v.RecordsDeleteRequested    += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        v.RecordDuplicateRequested  += (_, r) => _ = DuplicateRecordsWithConfirm([r]);
        v.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        v.AllDayCreationRequested   += (_, d)  => _ = OpenAddAllDayDialog(d);
        v.TimeSlotCreationRequested += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);
    }

    private void Wire(MonthView v)
    {
        v.DaySelected   += (_, d)  => DrillDown(2, d);
        v.DayAddClicked += (_, d)  => _ = OpenAddDialog(d);
        v.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        // Перенос в Месяце — дельтой с ИСХОДНЫМИ датами (само событие уже мутировано drag'ом):
        // −1 старому дню, +1 новому — сетка и карта «+N еще» правятся на месте, без перезагрузки.
        v.RecordMoved            += (_, m) =>
        {
            // Не записалось (база занята/пропала) — перечитываем вид, событие «отпрыгивает».
            if (!TryWrite(RepoOf(m.Rec), () => RepoOf(m.Rec).UpdateCalendarRecord(m.Rec)))
            { RefreshCurrent(); return; }
            var old = new CalendarRecord
            {
                DbId    = m.Rec.DbId,    Id      = m.Rec.Id,    FieldId = m.Rec.FieldId,
                CollectionId = m.Rec.CollectionId, AllDay = m.Rec.AllDay,
                StartDt = m.OldStart,    EndDt   = m.OldEnd,
                Title   = m.Rec.Title,   Color   = m.Rec.Color,
            };
            _journalDelta = [(old, -1), (m.Rec, +1)];
            _selectAfter  = [m.Rec];
            NotifyChangedAndRefresh(structural: false);
        };
        // Дублирование ВЕЗДЕ идет через подтверждение «Вы дублируете N записей» — единый путь
        // DuplicateRecordsWithConfirm, как в Расписании/Журнале (и для одной записи тоже).
        v.RecordDeleteRequested    += (_, r) => _ = DeleteRecordsWithConfirm([r]);
        v.RecordsDeleteRequested   += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        v.RecordDuplicateRequested += (_, r) => _ = DuplicateRecordsWithConfirm([r]);
        v.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
    }

    private void Wire(YearView v)
    {
        v.MonthSelected += (_, d)  => DrillDown(4, d);
        // Поиск в попапе дня Года — фоновый SQL LIKE по ВСЕМУ дню (как в «Расписании»):
        // попап показывает лишь первые записи дня-гиганта, локальный фильтр по ним неполон.
        v.DaySearchProvider = async (day, q) =>
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
        v.DayEventsProvider = async day =>
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
    }

    private void Wire(DiagramView v)
    {
        v.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        // Перенос/растяжение дат в Диаграмме — дельтой, как в Дне/Неделе (ключ не меняется).
        v.RecordMoved            += (_, r) => { if (!TryWrite(RepoOf(r), () => RepoOf(r).UpdateCalendarRecord(r))) { RefreshCurrent(); return; } _journalDelta = [(r, -1), (r, +1)]; _selectAfter = [r]; NotifyChangedAndRefresh(structural: false); };
        v.RecordDeleteRequested     += (_, r)  => _ = DeleteRecordsWithConfirm([r]);
        v.RecordsDeleteRequested    += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        v.RecordDuplicateRequested  += (_, r)  => _ = DuplicateRecordsWithConfirm([r]);
        v.RecordsDuplicateRequested += (_, rs) => _ = DuplicateRecordsWithConfirm(rs);
        // Смена масштаба Диаграммы (месяц…10 лет) меняет окно данных — полная перезагрузка.
        v.ScaleChanged              += (_, _) => RefreshCurrent();
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
        // Скрытый в «Отображаемых видах» вид не открывается: навбар отдает номер того, что
        // покажет на самом деле (вместо скрытого — вид по умолчанию).
        _currentViewIndex = NavbarControl.SetView(idx);
        var shown = ViewAt(_currentViewIndex);
        foreach (var view in ViewsHost.Children) view.IsVisible = view == shown;

        RefreshCurrent(scrollToNow: true);
    }

    // Двойной клик по дню «Месяца» или по месяцу «Года» — переход в подробный вид на эту дату.
    // Вид, скрытый в «Отображаемых видах», так не открывается: клик ничего не делает. Открыть
    // его значило бы показать вид, которого нет в переключателе, а перейти вместо него в вид
    // по умолчанию — увести клик туда, куда его не направляли.
    private void DrillDown(int idx, DateTime date)
    {
        if (!NavbarControl.HasView(idx)) return;
        _currentDate = date;
        SwitchView(idx);
    }

    /// <summary>
    /// «Отображаемые виды» календаря сменились: переключатель пересобирается сразу, а не после
    /// перезапуска. Скрыли открытый вид — раздел уходит на вид по умолчанию (навбар поднимает
    /// ViewChanged); скрытый раздел при этом не читает — перечитает при показе.
    /// </summary>
    public void ApplyViewVisibility() => NavbarControl.ApplyViewVisibility();

    private void NavigatePrev() => Navigate(-1);
    private void NavigateNext() => Navigate(+1);

    private void Navigate(int direction)
    {
        // Навигация вне «Недели» сбивает центрирование «на сегодня» — при возврате
        // окно строится от даты, а не от центра.
        if (_currentViewIndex != 3) _weekCentered = false;

        var fiveDay = CalendarSettings.FiveDayWeek;
        switch (_currentViewIndex)
        {
            // «Расписание» и «День» при 5-дневке листают только рабочие дни:
            // с пятницы «Вперед» — понедельник, с понедельника «Назад» — пятница.
            case 0:
            case 2: _currentDate = direction > 0
                        ? CalendarMath.NextVisibleDay(_currentDate, fiveDay)
                        : CalendarMath.PrevVisibleDay(_currentDate, fiveDay); break;
            case 3:
                (_currentDate, _weekCentered) = CalendarMath.StepWeekAnchor(
                    _currentDate, _weekCentered, CalendarSettings.WeekScrollMode,
                    fiveDay, DateTime.Today, direction);
                break;
            case 4: _currentDate = _currentDate.AddMonths(direction); break;
            case 5: _currentDate = _currentDate.AddYears(direction); break;
            case 6: _currentDate = DiagramViewCtrl.ScaleYears > 0
                        ? _currentDate.AddYears(direction * DiagramViewCtrl.ScaleYears)
                        : _currentDate.AddMonths(direction); break;
            default: _currentDate = _currentDate.AddDays(direction); break;
        }
    }

    /// <summary>Момент последнего чтения данных (<see cref="ChangeBus.Now"/>): главное окно
    /// перечитывает календарь по своим коммитам, только если они новее этой отметки.</summary>
    public long DataStamp { get; private set; }

    // Раздел скрыт — окно не читается (см. RefreshCurrent): отметка «перечитать при показе» и
    // накопленный запрос прокрутки к «сейчас».
    private bool _refreshDeferred;
    private bool _deferredScrollToNow;

    // Показ раздела: отложенное чтение — сейчас. Постом, а не сразу: главное окно следом за
    // IsVisible отдает разделу свежие галочки (SetActiveIds), и чтение пойдет одно и уже с
    // ними, а пост найдет отметку снятой.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _refreshDeferred)
            Dispatcher.UIThread.Post(() => { if (_refreshDeferred && IsVisible) RefreshCurrent(); },
                                     DispatcherPriority.Background);
    }

    private void RefreshCurrent(bool scrollToNow = false)
    {
        // Скрытый раздел окно не читает: его никто не увидит, а чтение по всем базам шло
        // секундами — на старте (раздел по умолчанию — конструктор) по разу на каждую
        // подключенную базу — и забирало диск у видимого раздела. Одноразовые якорь и дельта
        // Журнала сбрасываются: при показе раздел перечитывается целиком.
        if (!IsVisible)
        {
            _refreshDeferred      = true;
            _deferredScrollToNow |= scrollToNow;
            _journalAnchor = null; _journalDelta = null; _journalSelectAddedOnly = false; _selectAfter = null;
            return;
        }
        scrollToNow         |= _deferredScrollToNow;
        _refreshDeferred     = false;
        _deferredScrollToNow = false;

        DataStamp               = ChangeBus.Now();
        _renderedFontDelta      = FontScale.Delta;   // активный вид отрисован с текущим размером
        _renderedCalendarStamp  = CalendarSettings.Stamp;

        // 5-дневка: выходная дата в «Расписании»/«Дне»/«Неделе» невидима (пришла из
        // «Месяца» или настройка сменилась на лету) — прижимаем вперед, к понедельнику.
        if (CalendarSettings.FiveDayWeek && _currentViewIndex is 0 or 2 or 3
            && CalendarMath.IsWeekend(_currentDate))
            _currentDate = CalendarMath.NextVisibleDay(_currentDate, fiveDay: true);

        UpdateNoCalendarDatesHint();

        var (startDt, endDt) = GetDateRange();

        // Якорь и дельта Журнала — одноразовые: снимаем их при ЛЮБОМ обновлении (даже если
        // активен другой вид), чтобы устаревшая дельта не применилась к ленте позже.
        var anchor = _journalAnchor; _journalAnchor = null;
        var delta  = _journalDelta;  _journalDelta  = null;
        var selectAdded = _journalSelectAddedOnly; _journalSelectAddedOnly = false;
        var selectAfter = _selectAfter; _selectAfter = null;

        // Расписание и Журнал — полная виртуализация: событий в окне могут быть миллионы, поэтому
        // список знает лишь их ЧИСЛО (полоса прокрутки на весь объем), а страницы тянет по
        // требованию (см. CalendarDayVirtualList / JournalVirtualList) — прыжок в любое место без фриза.
        if (_currentViewIndex is 0 or 1)
        {
            // Отменяем незавершенную загрузку сетки (применять ее данные уже некуда) и оверлей.
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
            else   // вся история, без окна дат; при переходе в вид — к сегодня
                JournalViewCtrl.LoadFrom(sources, _searchQuery, anchor, delta, selectAdded, toToday: scrollToNow);
            return;
        }

        // Сетка (День/Неделя/Месяц/Год): точечная правка (создание/изменение/удаление/дубль,
        // перенос/растяжение времени в Дне/Неделе) правится НА МЕСТЕ из снимка последней
        // загрузки — без обращения к БД и без оверлея «Загрузка…», как Расписание/Журнал.
        UpdateTitle();
        if (delta is { Count: > 0 } && TryApplyGridDelta(delta, startDt, endDt, selectAfter))
            return;

        // Полная фоновая загрузка окна (навигация/переключение вида/внешнее изменение).
        // Оверлей «Загрузка…» — только если чтение затянулось (>400 мс, холодный день-гигант —
        // секунды); теплые окна применяются без мигания.
        var gen = ++_gridGen;
        _ = ShowGridLoadingLater(gen);
        _ = LoadGridAsync(gen, _currentViewIndex, startDt, endDt, _currentDate, _searchQuery, scrollToNow, selectAfter);
    }

    // Плашка «в календаре нечего показывать»: ни в одной смонтированной базе нет поля-даты с
    // включенным показом. Считаем по ФЛАГАМ, а не по числу событий в окне: пустой март у
    // размеченной коллекции — это просто пустой март, и подсказка там была бы неправдой.
    // Галочки коллекций в сайдбаре тоже не смотрим — снятая галочка это осознанный фильтр.
    // Запрос дешевый (db_fields — десятки строк) и идет перед чтением окна событий, которое
    // на порядки тяжелее.
    private void UpdateNoCalendarDatesHint()
    {
        var any = false;
        foreach (var d in _dbs)
            if (d.Repo.HasAnyCalendarDateField()) { any = true; break; }

        NoCalendarDatesHint.IsVisible = !any && _dbs.Count > 0;
    }

    // Точечная правка сетки НА МЕСТЕ: мутирует снимок последней загрузки и перерисовывает
    // текущий вид без чтения окна из БД. false — снимок не соответствует текущему окну/виду
    // (навигация, загрузка в полете, поиск) — вызывающий делает обычную фоновую загрузку.
    private bool TryApplyGridDelta(IReadOnlyList<(CalendarRecord Rec, int Delta)> delta,
                                   DateTime startDt, DateTime endDt, List<CalendarRecord>? select)
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
                // Вставка по порядку StartDt (как отдает репозиторий): виды сортируют сами,
                // но снимок остается корректным базисом для следующих правок.
                var at = _gridRecords.FindIndex(x => x.StartDt > rec.StartDt);
                _gridRecords.Insert(at < 0 ? _gridRecords.Count : at, rec);
            }

            // Карта счетчиков (честный «+N еще» Месяца/Года) — по дню НАЧАЛА записи;
            // правится и для записей сверх cap (их нет в снимке, но счетчик дня — о них).
            if (_gridTotals is not null && rec.StartDt >= startDt && rec.StartDt < endDt)
            {
                var day = DateOnly.FromDateTime(rec.StartDt.Date);
                _gridTotals[day] = Math.Max(0, _gridTotals.GetValueOrDefault(day) + d);
            }

            // Агрегаты Диаграммы: счетчик группы ±1; охват на вставке расширяется,
            // на удалении не сжимается (точное сжатие требует пересчета — устаканится
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

        ApplyGridData(_currentViewIndex, _gridRecords, _gridTotals, _currentDate, scrollToNow: false, select);
        return true;
    }

    // Фоновая загрузка окна сетки поколения gen; применяется, только если поколение не устарело.
    // ДВЕ фазы: (1) события окна — индексные сики, миллисекунды, сетка показывается сразу
    // (оверлей «Загрузка…» не успевает появиться); (2) для Месяца/Года — карта счетчиков дней
    // («+N еще»/числа на днях): ее GROUP BY на окне с днем-гигантом — секунды, поэтому она
    // доезжает фоном и ТИХО уточняет уже показанную сетку.
    private async Task LoadGridAsync(long gen, int viewIdx, DateTime startDt, DateTime endDt,
                                     DateTime currentDate, string query, bool scrollToNow,
                                     List<CalendarRecord>? selectAfter = null)
    {
        var records   = new List<CalendarRecord>();
        var hasSearch = !string.IsNullOrEmpty(query);
        // Год без поиска ВСЕ события окна не читает: числам достаточно карты счетчиков
        // (фаза 2), а список дня попап грузит ПО ТРЕБОВАНИЮ (DayEventsProvider). Чтение всех
        // событий года (сотни тысяч строк, ~0.7–1.5 с) было последним местом сетки, где
        // оверлей «Загрузка…» еще успевал появиться при листании. Исключение — МНОГОДНЕВНЫЕ
        // события: карта считает событие по дню НАЧАЛА, поэтому запись на три года
        // подсвечивала одно число вместо всего диапазона. Их читаем отдельно (их мало),
        // вид разложит по дням сам — остальное по-прежнему приходит картой.
        var spanningOnly = viewIdx == 5 && !hasSearch;
        string? loadingPath = null;   // путь читаемой БД — для сигнала монитору при сбое
        // Порог медленности: чтение окна календаря по всем базам дольше Diag.UiOpen — в журнал.
        // Замер — до показа окна (фаза 1); уточнения фазы 2 меряются отдельно. Загрузку, которую
        // обогнала новая (другой вид или дата), не пишем: ее результат выброшен.
        var context = $"вид {viewIdx}, {startDt:yyyy-MM-dd} — {endDt:yyyy-MM-dd}, баз {_dbs.Count}" +
                      (hasSearch ? ", с поиском" : "");
        using var slow = Logging.Diag.Measure("загрузка календаря", Logging.Diag.UiOpen, context);
        var clock = new ReadClock();
        try
        {
            // Диаграмма — свой загрузчик «по коллекциям» (cap на коллекцию, а не на день): ее
            // окно бывает десятилетием, и per-day чтение сеток стоило бы тысяч запросов и
            // миллионов строк при потолке отрисовки ~200 на группу.
            var jobs = StartForAllDbs((repo, active) => spanningOnly
                ? repo.LoadCalendarSpanningAsync(startDt, endDt, active, GridDayCap)
                : viewIdx == 6
                    ? repo.LoadDiagramWindowAsync(startDt, endDt, active, DiagramView.MaxRecordsPerCollection)
                    : viewIdx == 4
                        ? repo.LoadCalendarGridRangeAsync(startDt, endDt, active, MonthDayCap,
                                                          allDayCap: GridDayCap, multiDayPerDay: MonthDayCap,
                                                          allDayPerDay: MonthDayCap)
                        : repo.LoadCalendarGridRangeAsync(startDt, endDt, active, GridDayCap), clock);
            foreach (var (dbId, repo, task) in jobs)
            {
                loadingPath = repo.DbPath;
                var part = await task;
                if (gen != _gridGen) { slow.Discard(); return; }   // вид/дата уже другие — не применяем
                foreach (var r in part) r.DbId = dbId;
                records.AddRange(part);
            }
        }
        catch (Exception ex)
        {
            // БД недоступна/занята — оставляем прежнее содержимое вида, оверлей снимаем.
            // «Файл пропал» дополнительно сигналит монитору доступности (DbErrors) —
            // иначе календарь молча показывал бы устаревшие данные до следующей пробы.
            // Прочее (наш дефект в чтении) без записи оставило бы календарь с устаревшим содержимым.
            SmartBase.Utils.DbErrors.HandleRead(ex, loadingPath);
            if (!DbHealthMonitor.IsUnavailableError(ex) && !DbHealthMonitor.IsBusyError(ex))
                Serilog.Log.Warning(ex, "Календарь: окно не прочитано ({Db:l}, вид {View}, {From:l} — {To:l})",
                                    SmartBase.Utils.DbErrors.Display(loadingPath), viewIdx,
                                    startDt.ToString("yyyy-MM-dd"), endDt.ToString("yyyy-MM-dd"));
            if (gen == _gridGen) { _gridAppliedGen = gen; GridLoadingOverlay.IsVisible = false; }
            return;
        }

        // Поиск фильтрует прочитанное (как раньше); карта счетчиков к отфильтрованному
        // неприменима — «+N еще» при поиске считается от фактически показанного.
        if (hasSearch)
            records = records.Where(r => r.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _gridAppliedGen = gen;
        GridLoadingOverlay.IsVisible = false;

        // Снимок — базис точечных правок (TryApplyGridDelta); при поиске список отфильтрован
        // и базисом не годится (правка при активном поиске пойдет полной загрузкой).
        _gridRecords     = hasSearch ? null : records;
        _gridTotals      = null;
        _diagramStats    = null;
        _gridStart       = startDt;
        _gridEnd         = endDt;
        _gridViewApplied = viewIdx;

        // Каркас групп Диаграммы: коллекции — крошечная таблица, читается на месте.
        if (viewIdx == 6) _diagramCollections = LoadDiagramCollections();

        // В записи о медленной загрузке — и доля чтения: медленно ли база или построение вида.
        slow.Context = $"{context}, чтение {slow.Elapsed.TotalMilliseconds:N0} мс (базы {clock.Ms:N0} мс)";
        ApplyGridData(viewIdx, records, null, currentDate, scrollToNow, selectAfter);
        slow.Dispose();

        // Фаза 2 Диаграммы: точные агрегаты групп (счетчик, «+N еще», охват суммарного
        // бара по ВСЕМ событиям окна, а не по обрезанным cap'ом) — индексные сканы,
        // на больших окнах секунды, поэтому фоном и тихо, как карта счетчиков Месяца/Года.
        // При поиске не считаются — счетчики идут от фактически показанного.
        if (viewIdx == 6)
        {
            if (hasSearch) return;
            using var statsSlow = Logging.Diag.Measure("агрегаты диаграммы", Logging.Diag.UiQuery, context);
            var stats = new Dictionary<(int DbId, int ColId), DiagramGroupStat>();
            try
            {
                foreach (var (dbId, _, task) in StartForAllDbs((repo, active) => repo.LoadDiagramStatsAsync(startDt, endDt, active)))
                {
                    foreach (var (colId, stat) in await task)
                        stats[(dbId, colId)] = stat;
                    if (gen != _gridGen) { statsSlow.Discard(); return; }
                }
            }
            catch { return; }   // агрегаты не критичны — счетчики останутся от полученного

            _diagramStats = stats;
            ApplyGridData(viewIdx, _gridRecords ?? records, null, currentDate, scrollToNow: false);
            return;
        }

        // Фаза 2: карта счетчиков — только Месяц/Год и не при поиске. До ее прихода «+N еще»
        // считается от фактически полученного; по приходе — тихое повторное применение.
        if (hasSearch || viewIdx is not (4 or 5)) return;
        // Карта уточняет уже показанную сетку, но у Года числа дней и есть содержимое — человек
        // их ждет: порог фильтра, а не фоновый.
        using var countsSlow = Logging.Diag.Measure("счетчики дней календаря", Logging.Diag.UiQuery, context);
        var totals = new Dictionary<DateOnly, int>();
        try
        {
            foreach (var (_, _, task) in StartForAllDbs((repo, active) => repo.LoadCalendarDayCountsAsync(startDt, endDt, active)))
            {
                foreach (var (dayStr, count) in await task)
                    if (DateOnly.TryParseExact(dayStr, "yyyy-MM-dd", out var d))
                        totals[d] = totals.GetValueOrDefault(d) + count;
                if (gen != _gridGen) { countsSlow.Discard(); return; }
            }
        }
        catch { return; }   // карта не критична — «+N еще» останется от полученного

        _gridTotals = totals;
        countsSlow.Context = $"{context}, чтение {countsSlow.Elapsed.TotalMilliseconds:N0} мс";
        // Между фазами могла пройти точечная правка (TryApplyGridDelta мутирует _gridRecords,
        // поколение не меняя) — применяем АКТУАЛЬНЫЙ снимок, а не локальный records.
        ApplyGridData(viewIdx, _gridRecords ?? records, totals, currentDate, scrollToNow: false);
    }

    // Чтение окна по всем базам ОДНОВРЕМЕННО: у каждой свое изолированное подключение в фоне, так
    // что загрузка стоит, сколько самая долгая база, а не их сумма. Задания запускаются сразу,
    // ждать их — по порядку баз (результат складывается в прежнем порядке, а сбой называет свою
    // базу). Брошенные ожидающим (устаревшее поколение, сбой другой базы) задания досчитываются
    // впустую; их исключения наблюдаются, чтобы не всплыть необработанными.
    private List<(int DbId, ConstructorRepository Repo, Task<T> Task)> StartForAllDbs<T>(
        Func<ConstructorRepository, ISet<int>?, Task<T>> load, ReadClock? clock = null)
    {
        var jobs = new List<(int, ConstructorRepository, Task<T>)>();
        foreach (var (dbId, _, repo) in _dbs.ToList())
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            var task = load(repo, active);
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            clock?.Track(task);
            jobs.Add((dbId, repo, task));
        }
        return jobs;
    }

    // Когда закончила последняя база — в фоне, без ожидания UI-потока. В записи о медленной
    // загрузке «чтение» считается до применения на UI-потоке, и без этой доли не понять, долго ли
    // читали базы или UI-поток был занят (построение раздела, раскладка).
    private sealed class ReadClock
    {
        private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
        private long _last;

        public void Track(Task task) =>
            task.ContinueWith(_ =>
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                for (var seen = Interlocked.Read(ref _last); now > seen; seen = Interlocked.Read(ref _last))
                    if (Interlocked.CompareExchange(ref _last, now, seen) == seen) break;
            }, TaskContinuationOptions.ExecuteSynchronously);

        public double Ms => Interlocked.Read(ref _last) is var last and > 0
            ? System.Diagnostics.Stopwatch.GetElapsedTime(_start, last).TotalMilliseconds
            : 0;
    }

    // Отдать данные видимому виду сетки — общий финал полной загрузки и точечной правки. select —
    // что выделить после правки, как нажатием (Год событий не рисует — выделять нечего); null —
    // выделение вида переживает пересборку как есть.
    private void ApplyGridData(int viewIdx, List<CalendarRecord> records,
                               Dictionary<DateOnly, int>? totals, DateTime currentDate, bool scrollToNow,
                               List<CalendarRecord>? select = null)
    {
        switch (viewIdx)
        {
            case 2: DayViewCtrl.Refresh(records, DateOnly.FromDateTime(currentDate), select: select); break;
            case 3: WeekViewCtrl.Refresh(records, WeekDays(), scrollToNow, select); break;
            case 4: MonthViewCtrl.Refresh(records, currentDate, totals, select); break;
            case 5: YearViewCtrl.Refresh(records, currentDate.Year, totals); break;
            case 6: DiagramViewCtrl.Refresh(records, currentDate, scrollToNow,
                                            _diagramCollections,
                                            hideEmptyGroups: !string.IsNullOrEmpty(_searchQuery),
                                            stats: _diagramStats, select: select); break;
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

    // Оверлей «Загрузка…» с задержкой: быстрые (теплые) загрузки сетки им не мигают.
    private async Task ShowGridLoadingLater(long gen)
    {
        await Task.Delay(400);
        if (gen == _gridGen && gen != _gridAppliedGen)
            GridLoadingOverlay.IsVisible = true;
    }

    // Дни окна вида «Неделя»: 7 подряд или 5 рабочих, якорь — по режиму прокрутки
    // (понедельник / сама дата / центр на сегодня). Дни диктует контроллер, вид их
    // только рисует — иначе режим «по дню» и 5-дневку не собрать.
    private IReadOnlyList<DateTime> WeekDays()
    {
        var fiveDay = CalendarSettings.FiveDayWeek;
        var anchor  = CalendarMath.WeekAnchor(_currentDate, CalendarSettings.WeekScrollMode,
                                              fiveDay, _weekCentered, DateTime.Today);
        return CalendarMath.WeekWindow(anchor, fiveDay);
    }

    private (DateTime start, DateTime end) GetDateRange() => _currentViewIndex switch
    {
        2 => (_currentDate.Date, _currentDate.Date.AddDays(1)),
        3 => WeekRange(),
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

    // Окно чтения «Недели»: [первый видимый день, последний + 1). При 5-дневке с
    // невыровненным окном («по дню») выходные внутри диапазона читаются, но колонок
    // для них нет — вид их просто не рисует.
    private (DateTime start, DateTime end) WeekRange()
    {
        var days = WeekDays();
        return (days[0], days[^1].AddDays(1));
    }

    private static DateTime StartOfWeek(DateTime d) => CalendarMath.StartOfWeek(d);

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
            3 => WeekTitle(),
            4 => _currentDate.ToString("MMMM yyyy", ru),
            5 => _currentDate.Year.ToString(),
            6 => DiagramViewCtrl.TitleFor(_currentDate),   // «июль 2026» / «2026» / «2020–2029»
            _ => "Все записи",
        });
    }

    // Заголовок «Недели» — по фактическому окну (при «по дню» оно не выровнено на Пн).
    private string WeekTitle()
    {
        var days = WeekDays();
        return $"{days[0]:d MMM} – {days[^1]:d MMM yyyy}";
    }

    // 5-дневка: новые записи на выходные не попадают — предлагаемая дата создания
    // прижимается вперед, к понедельнику (Журнал/Месяц/Год выходные показывают, но
    // двойной клик по субботе предлагает рабочий день; руками в окне дату сменить можно).
    private static DateTime SnapCreationDate(DateTime d) =>
        CalendarMath.SnapToWorkday(d, +1, CalendarSettings.FiveDayWeek);

    private async Task OpenAddAllDayDialog(DateOnly date)
    {
        if (VisualRoot is not Avalonia.Controls.Window win || _dbs.Count == 0) return;

        // Выбор целевой БД встроен в само окно (показывается при нескольких БД).
        var dlg = new RecordDialog(_dbs, _dbs[0].DbId,
                                   presetDate: SnapCreationDate(date.ToDateTime(TimeOnly.MinValue)),
                                   presetAllDay: true);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result?.Record is not null)
        {
            if (!await ConfirmCalendarSetup(win, result.Record)) return;
            if (!TryWrite(RepoOf(result.Record), () => RepoOf(result.Record).AddCalendarRecord(result.Record))) return;
            _journalAnchor = result.Record;   // Журнал: к созданной записи
            _journalDelta  = [(result.Record, +1)];
            _selectAfter   = [result.Record];
            NotifyChangedAndRefresh();
        }
    }

    private async Task OpenAddDialog(DateTime presetDate, TimeSpan? presetTime = null)
    {
        if (VisualRoot is not Avalonia.Controls.Window win || _dbs.Count == 0) return;

        var dlg = new RecordDialog(_dbs, _dbs[0].DbId,
                                   presetDate: SnapCreationDate(presetDate), presetTime: presetTime);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result?.Record is not null)
        {
            if (!await ConfirmCalendarSetup(win, result.Record)) return;
            if (!TryWrite(RepoOf(result.Record), () => RepoOf(result.Record).AddCalendarRecord(result.Record))) return;
            _journalAnchor = result.Record;   // Журнал: к созданной записи
            _journalDelta  = [(result.Record, +1)];
            _selectAfter   = [result.Record];
            NotifyChangedAndRefresh();
        }
    }

    // ОДИН refresh на операцию: дельта ставится ДО CollectionsMayHaveChanged — его обработчик
    // (MainWindow: сайдбар → SetActiveIds → RefreshCurrent) потребляет ее и правит ленту на
    // месте; страховочный RefreshCurrent срабатывает, только если дельту никто не съел (нет
    // подписчика). Прежний порядок (Invoke ДО дельты) запускал холостую пересборку без дельты —
    // ее отменяло поколение, но фоновый пересчет дня (COUNT дня-гиганта — секунды) уходил
    // впустую на каждую правку, а кеш счетчиков дня было бы невозможно держать теплым.
    // structural = false — правка, которая коллекций и полей не меняет (дубль, удаление, перенос):
    // дерево коллекций сайдбара не перестраивается, вид правится сразу.
    private void NotifyChangedAndRefresh(bool structural = true)
    {
        if (structural) CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        if (_journalDelta is not null) RefreshCurrent();
    }

    // Окно «входящие связи оборвутся» для ОДНОЙ записи из окна редактирования — те же проверки, что у
    // DeleteRecordsWithConfirm (удаление списком) и RelocateRecords таблицы, но кнопки «Удалить»/смены
    // коллекции в самом окне записи шли мимо них. Репозиторий берем прямым поиском по _dbs, а не через
    // RepoOf: тот подставляет DefaultRepo и проверил бы чужую базу по коллизии id. Нет связей — окна
    // нет (правка записи не должна каждый раз спрашивать). true — можно продолжать.
    private async Task<bool> ConfirmIncomingLinkBreak(
        Avalonia.Controls.Window win, CalendarRecord r, string message, string confirmText)
    {
        if (r.Id is not int id) return true;
        var repo = _dbs.FirstOrDefault(d => d.DbId == r.DbId).Repo;
        if (repo is null) return true;
        var check = RelationDeleteImpact.CheckRecords(repo, [id]);
        if (check.Blocked is { } blocked) { await ConfirmDialog.Info(blocked).ShowDialog(win); return false; }
        if (check.Warning.Length == 0) return true;
        return await new ConfirmDialog(message + check.Warning, confirmText).ShowDialog<bool>(win) == true;
    }

    // Событие доводит коллекцию до нужных столбцов («Дата», «Описание») — прежде это
    // происходило молча, и чужая таблица обрастала колонками без ведома хозяина. Спрашиваем
    // ДО записи и ТОЛЬКО если коллекция реально изменится; отказ = запись не создается.
    // moving — правка со сменой коллекции или базы (готовится цель переноса).
    private Task<bool> ConfirmCalendarSetup(Avalonia.Controls.Window win, CalendarRecord r, bool moving = false) =>
        SectionSetupConfirm.ForCalendar(win, RepoOf(r), r.CollectionId, r.FieldId, moving);

    private async Task OpenEditDialog(CalendarRecord r)
    {
        if (VisualRoot is not Avalonia.Controls.Window win || _dbs.Count == 0) return;

        // Выбор БД встроен в окно (при нескольких БД); запись с целевым DbId возвращается.
        var dlg = new RecordDialog(_dbs, r.DbId, editRecord: r);
        var result = await dlg.ShowDialog<RecordDialogResult?>(win);
        if (result is null) return;

        if (result.Deleted)
        {
            if (!await ConfirmIncomingLinkBreak(win, r,
                    "Удалить запись? На нее ссылаются связи из других записей — они будут удалены.",
                    "Удалить")) return;
            if (!TryWrite(RepoOf(r), () => RepoOf(r).DeleteCalendarRecord(r.Id!.Value))) return;
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

            // Смена коллекции или базы = перенос: запись ПЕРЕСОЗДАЕТСЯ с новым Id, поэтому входящие
            // связи на нее оборвутся (исходящие переносятся, см. CopyRelationLinksByName). Правка без
            // смены места (дата, название, поле-дата) Id не меняет — там предупреждать не о чем.
            var moving = rec.DbId != r.DbId || rec.CollectionId != r.CollectionId;
            if (moving && !await ConfirmIncomingLinkBreak(win, r,
                        "Перенос пересоздает запись: связи из других записей на нее оборвутся "
                      + "(станут «связи нет»). Перенести?", "Перенести")) return;

            // Правка тоже готовит коллекцию: перенос — целевую целиком, а правка на месте
            // вернет удаленное «Описание». Спрашиваем и здесь — молча столбцы не появляются.
            if (!await ConfirmCalendarSetup(win, rec, moving)) return;

            if (rec.DbId == r.DbId)
            {
                var repo = RepoOf(rec);
                var ok = TryWrite(repo, () =>
                {
                    // Перенос даты в другое поле той же записи (та же коллекция): очищаем старое
                    // поле, чтобы дата не осталась в обоих — событие переезжает целиком.
                    if (rec.CollectionId == r.CollectionId && r.FieldId > 0 &&
                        rec.FieldId != r.FieldId && r.Id is int rid)
                        repo.SetFieldValue(rid, r.FieldId, "");
                    repo.UpdateCalendarRecord(rec);
                });
                if (!ok) return;
            }
            else
            {
                // БД сменили — переносим запись: создаем в целевой базе, грамотно переносим значения
                // свойств (по имени, с приведением типов) и исходящие связи (с пересчетом TargetDbId
                // под целевую базу — как MoveRecordsCore у таблицы), удаляем из исходной. Поле-дату
                // исключаем, чтобы не затереть выставленную AddCalendarRecord дату.
                var sourceRepo = RepoOf(r);
                var targetRepo = RepoOf(rec);
                var ok = TryWrite(targetRepo, () =>
                {
                    targetRepo.AddCalendarRecord(rec);
                    targetRepo.CopyFieldValuesByName(sourceRepo, r.Id!.Value, r.CollectionId ?? 0,
                                                     rec.Id!.Value, rec.CollectionId ?? 0,
                                                     createMissingFields: false, excludeTargetFieldId: rec.FieldId);
                    targetRepo.CopyRelationLinksByName(sourceRepo, r.Id!.Value, r.CollectionId ?? 0,
                                                       rec.Id!.Value, rec.CollectionId ?? 0,
                                                       RelationDbResolver.DbIdFor(sourceRepo), RelationDbResolver.DbIdFor(targetRepo));
                    sourceRepo.DeleteCalendarRecord(r.Id!.Value);
                });
                if (!ok) { RefreshCurrent(); return; }
            }
            _journalAnchor = result.Record;   // Журнал: к записи на ее новой дате
            _journalDelta  = [(r, -1), (result.Record, +1)];
            _selectAfter   = [result.Record];
            NotifyChangedAndRefresh();
            return;
        }
        if (_journalDelta is not null) RefreshCurrent();   // удаление из диалога (Deleted выше)
    }

    private static CalendarRecord CopyOf(CalendarRecord r) => new()
    {
        DbId         = r.DbId,
        CollectionId = r.CollectionId,
        FieldId      = r.FieldId,   // дубль кладем в то же поле-дату, что и оригинал
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
    // копии, только если ее строка сейчас не видна.
    private async Task DuplicateRecordsWithConfirm(IReadOnlyList<CalendarRecord> records)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var targets = records.Where(r => r.Id.HasValue).ToList();
        if (targets.Count == 0) return;

        // Дублируется ЗАПИСЬ, а не событие: запись с двумя видимыми полями-датами дает два события,
        // и раньше выбор обоих создавал две копии. Копия несет все даты оригинала сразу, поэтому
        // группируем по (база, запись) — одна копия на запись, а событий-копий отдаем столько же,
        // сколько было выбрано у оригинала. Счет в окне — тоже по записям.
        var byRecord = targets.GroupBy(r => (r.DbId, Id: r.Id!.Value)).ToList();

        var dlg = new ConfirmDialog($"Вы дублируете {byRecord.Count} {RecordsPlural(byRecord.Count)}. Подтвердить?",
                                    confirmText: "Дублировать");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        // Немного записей — по одной (DuplicateRecord), каждая своим подключением в фоне: копия
        // записи с тысячей свойств — тысяча значений, и UI-поток ее не ждет. Много — bulk-SQL в фоне
        // с окном прогресса (как массовое удаление): цикл с SaveChanges на КАЖДУЮ запись вешал UI на
        // сотнях/тысячах. Оба пути — ОБЩИЕ с таблицей: копия несет все свойства и исходящие связи.
        List<CalendarRecord> copies;
        if (byRecord.Count <= SmallOpCap)
        {
            copies = new List<CalendarRecord>(targets.Count);
            foreach (var g in byRecord)
            {
                var repo = RepoOf(g.First());
                DbRecord? made = null;
                var srcId = g.Key.Id;
                // Не записалось (база занята/пропала) — показываем тост и останавливаемся:
                // уже созданные копии остаются, вид перечитается ниже.
                if (!await TryWriteAsync(repo, () => made = repo.DuplicateRecordIsolated(srcId))) break;
                if (made?.Id is not int newId) continue;
                foreach (var src in g)
                {
                    var copy = CopyOf(src);
                    copy.Id = newId;
                    copies.Add(copy);
                }
            }
        }
        else
            copies = await BulkDuplicateWithProgress(win, targets);

        if (copies.Count > 0)
        {
            _journalAnchor = copies.OrderBy(c => c.StartDt).ThenBy(c => c.Id ?? 0).Last();
            _journalDelta  = copies.Select(c => (c, +1)).ToList();
            _journalSelectAddedOnly = true;
            _selectAfter   = copies;
        }
        NotifyChangedAndRefresh(structural: false);
    }

    private async Task DeleteRecordsWithConfirm(IReadOnlyList<CalendarRecord> records)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var targets = records.Where(r => r.Id.HasValue).ToList();
        if (targets.Count == 0) return;

        // Предупреждаем, что удаление оборвет ВХОДЯЩИЕ связи на эти записи (внутрибазовые + межбазовые
        // из открытых баз) — как в таблице (ConstructorControllerView.DeleteRecordsWithConfirm).
        // Календарь агрегирует события ИЗ ВСЕХ смонтированных БД, поэтому проверяем по каждой базе
        // отдельно; репозиторий берем прямым поиском по _dbs (не RepoOf: он подставляет DefaultRepo и
        // проверил бы чужую базу). Одна запись дает по событию на каждое поле-дату → id дедуплицируем.
        var check = RelationDeleteImpact.CheckRecords(targets.GroupBy(r => r.DbId)
            .Select(g => (Repo: _dbs.FirstOrDefault(d => d.DbId == g.Key).Repo, Ids: (IReadOnlyCollection<int>)g.Select(r => r.Id!.Value).Distinct().ToList()))
            .Where(g => g.Repo is not null).Select(g => (g.Repo!, g.Ids)));
        if (check.Blocked is { } blocked) { await ConfirmDialog.Info(blocked).ShowDialog(win); return; }
        var linkWarn = check.Warning;

        var dlg = new ConfirmDialog($"Вы удаляете {targets.Count} {RecordsPlural(targets.Count)}.{linkWarn} Подтвердить?");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        // Немного записей — прежний мгновенный путь (каждую в ее БД). Много — bulk-SQL в фоне
        // на изолированных подключениях с окном прогресса (как удаление коллекции в сайдбаре):
        // цикл DeleteCalendarRecord с SaveChanges на КАЖДУЮ запись вешал UI на сотнях/тысячах.
        if (targets.Count <= SmallOpCap)
        {
            foreach (var r in targets)
                // Не удалилось (база занята/пропала) — тост и стоп; вид перечитается ниже.
                if (!TryWrite(RepoOf(r), () => RepoOf(r).DeleteCalendarRecord(r.Id!.Value))) break;
        }
        else
            await BulkDeleteWithProgress(win, targets);

        _journalDelta = targets.Select(r => (r, -1)).ToList();
        NotifyChangedAndRefresh(structural: false);
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

    // Массовое дублирование: тот же bulk-SQL, что у таблицы (DuplicateRecordsMappedIsolated —
    // шапка + ВСЕ значения + исходящие связи + проекция календаря), в фоне с окном прогресса.
    // Группируем по (база, коллекция): bulk считает позиции копий относительно якоря ОДНОЙ
    // коллекции. Возвращает события-копии (для выделения, дельты Журнала и якоря прокрутки).
    private async Task<List<CalendarRecord>> BulkDuplicateWithProgress(
        Avalonia.Controls.Window win, List<CalendarRecord> targets)
    {
        var groups = targets
            .GroupBy(r => (r.DbId, ColId: r.CollectionId ?? 0))
            .Select(g => (Events: g.ToList(), SrcIds: g.Select(e => e.Id!.Value).Distinct().ToList(), g.Key.ColId))
            .ToList();
        var total = groups.Sum(g => g.SrcIds.Count);   // единица прогресса — ЗАПИСЬ, не событие

        var copies = new List<CalendarRecord>();   // наполняется в фоне, читается после await
        await RunCalendarBulk(win, "Дублирование", "Дублирование записей…", total, report =>
        {
            long done = 0;
            foreach (var (events, srcIds, colId) in groups)
            {
                var repo = RepoOf(events[0]);
                var baseDone = done;
                var map = repo.DuplicateRecordsMappedIsolated(
                    colId, srcIds, new IntProgress(v => report(baseDone + v)));
                var bySrc = map.ToDictionary(p => p.SrcId, p => p.NewId);
                // Событий у записи может быть несколько (по полю-дате на каждое) — копия несет все
                // даты сразу, поэтому на каждое исходное событие отдаем событие-копию с новым Id.
                foreach (var e in events)
                    if (bySrc.TryGetValue(e.Id!.Value, out var newId))
                    {
                        var copy = CopyOf(e);
                        copy.Id = newId;
                        copies.Add(copy);
                    }
                done += srcIds.Count;
            }
            report(total);
        });
        return copies;
    }

    // Общий помощник массовых операций календаря: bulk-работа в фоне; окно прогресса
    // (заголовок, фаза, «Количество записей: N», проценты — как удаление коллекции в сайдбаре)
    // появляется, только если операция не уложилась в задержку; средние объемы проходят без
    // окна. В конце — «Сохранение…» (коммит транзакции). report зовется из фона — маршалим сами.
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

    // Адаптер IProgress<int> для прогресса с фонового потока (передается в *Isolated-методы).
    private sealed class IntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    // Начало минуты — для сравнения времени из диалога (минутная точность) с исходным.
    private static DateTime MinuteFloor(DateTime d) =>
        new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, d.Kind);

    // Русское склонование: 1 запись, 2–4 записи, 5+ записей (с учетом 11–14).
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

}
