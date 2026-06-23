using Avalonia.Controls;
using DataBase.Controls.Dialogs;
using DataBase.Core.Models;
using DataBase.Database;
using DataBase.Database.Repositories;

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

    // Репозиторий БД по умолчанию (новые события, сидинг) и маршрутизация по записи.
    private ConstructorRepository DefaultRepo => _dbs[0].Repo;
    private ConstructorRepository RepoOf(CalendarRecord r)
    {
        foreach (var d in _dbs) if (d.DbId == r.DbId) return d.Repo;
        return DefaultRepo;
    }

    public CalendarControllerView() { InitializeComponent(); }

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

        ListViewCtrl.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        ListViewCtrl.CreateRequested        += (_, d) => _ = OpenAddDialog(d);
        ListViewCtrl.RecordDeleteRequested  += (_, r) => { if (r.Id.HasValue) { RepoOf(r).DeleteCalendarRecord(r.Id.Value); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); } };
        ListViewCtrl.RecordsDeleteRequested += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        JournalViewCtrl.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        JournalViewCtrl.CreateRequested        += (_, d) => _ = OpenAddDialog(d);
        JournalViewCtrl.RecordDeleteRequested  += (_, r) => { if (r.Id.HasValue) { RepoOf(r).DeleteCalendarRecord(r.Id.Value); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); } };
        JournalViewCtrl.RecordsDeleteRequested += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        MonthViewCtrl.RecordClicked          += (_, r) => _ = OpenEditDialog(r);
        MonthViewCtrl.RecordMoved            += (_, r) => { RepoOf(r).UpdateCalendarRecord(r); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); };
        MonthViewCtrl.RecordDeleteRequested    += (_, r) => { if (r.Id.HasValue) { RepoOf(r).DeleteCalendarRecord(r.Id.Value); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); } };
        MonthViewCtrl.RecordsDeleteRequested   += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        MonthViewCtrl.RecordDuplicateRequested += (_, r) => DuplicateCalendarRecord(r);
        DayViewCtrl.RecordClicked              += (_, r) => _ = OpenEditDialog(r);
        DayViewCtrl.RecordMoved                += (_, r) => { RepoOf(r).UpdateCalendarRecord(r); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); };
        DayViewCtrl.RecordDeleteRequested      += (_, r) => { if (r.Id.HasValue) { RepoOf(r).DeleteCalendarRecord(r.Id.Value); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); } };
        DayViewCtrl.RecordsDeleteRequested     += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        DayViewCtrl.RecordDuplicateRequested   += (_, r) => DuplicateCalendarRecord(r);
        DayViewCtrl.AllDayCreationRequested    += (_, d)  => _ = OpenAddAllDayDialog(d);
        DayViewCtrl.TimeSlotCreationRequested  += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);
        WeekViewCtrl.RecordClicked             += (_, r) => _ = OpenEditDialog(r);
        WeekViewCtrl.RecordMoved               += (_, r) => { RepoOf(r).UpdateCalendarRecord(r); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); };
        WeekViewCtrl.RecordDeleteRequested     += (_, r) => { if (r.Id.HasValue) { RepoOf(r).DeleteCalendarRecord(r.Id.Value); CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty); RefreshCurrent(); } };
        WeekViewCtrl.RecordsDeleteRequested    += (_, rs) => _ = DeleteRecordsWithConfirm(rs);
        WeekViewCtrl.RecordDuplicateRequested  += (_, r) => DuplicateCalendarRecord(r);
        WeekViewCtrl.AllDayCreationRequested   += (_, d)  => _ = OpenAddAllDayDialog(d);
        WeekViewCtrl.TimeSlotCreationRequested += (_, dt) => _ = OpenAddDialog(dt.Date, dt.TimeOfDay);

        SwitchView(4);
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
            _ => _currentDate.AddDays(1),
        };
    }

    private void RefreshCurrent(bool scrollToNow = false)
    {
        var (startDt, endDt) = GetDateRange();

        // Агрегируем события из всех смонтированных БД, помечая каждое его DbId,
        // и фильтруем каждую БД по её активным коллекциям.
        var records = new List<CalendarRecord>();
        foreach (var (dbId, _, repo) in _dbs)
        {
            ISet<int>? active = _activeByDb is not null && _activeByDb.TryGetValue(dbId, out var s) ? s : null;
            var part = repo.GetCalendarRecordsByRange(startDt, endDt, active);
            foreach (var r in part) r.DbId = dbId;
            records.AddRange(part);
        }

        if (!string.IsNullOrEmpty(_searchQuery))
            records = records.Where(r => r.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        UpdateTitle();

        switch (_currentViewIndex)
        {
            case 0: ListViewCtrl.Refresh(records, _currentDate); break;
            case 1: JournalViewCtrl.Refresh(records, _currentDate); break;
            case 2: DayViewCtrl.Refresh(records, DateOnly.FromDateTime(_currentDate)); break;
            case 3: WeekViewCtrl.Refresh(records, _currentDate, scrollToNow); break;
            case 4: MonthViewCtrl.Refresh(records, _currentDate); break;
            case 5: YearViewCtrl.Refresh(records, _currentDate.Year); break;
        }
    }

    private (DateTime start, DateTime end) GetDateRange() => _currentViewIndex switch
    {
        2 => (_currentDate.Date, _currentDate.Date.AddDays(1)),
        3 => (StartOfWeek(_currentDate), StartOfWeek(_currentDate).AddDays(7)),
        4 => (StartOfWeek(new DateTime(_currentDate.Year, _currentDate.Month, 1)),
              StartOfWeek(new DateTime(_currentDate.Year, _currentDate.Month, 1)).AddDays(42)),
        5 => (new DateTime(_currentDate.Year, 1, 1), new DateTime(_currentDate.Year + 1, 1, 1)),
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
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
            RefreshCurrent();
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
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
            RefreshCurrent();
        }
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
        }
        else if (result.Record is not null)
        {
            var rec = result.Record;
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
                // БД сменили — переносим запись: создаём в целевой базе, удаляем из исходной.
                RepoOf(rec).AddCalendarRecord(rec);
                RepoOf(r).DeleteCalendarRecord(r.Id!.Value);
            }
            CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        }
        RefreshCurrent();
    }

    private void DuplicateCalendarRecord(CalendarRecord r)
    {
        var copy = new CalendarRecord
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
        RepoOf(r).AddCalendarRecord(copy);
        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        RefreshCurrent();
    }

    private async Task DeleteRecordsWithConfirm(IReadOnlyList<CalendarRecord> records)
    {
        if (VisualRoot is not Avalonia.Controls.Window win) return;
        var targets = records.Where(r => r.Id.HasValue).ToList();
        if (targets.Count == 0) return;

        var dlg = new ConfirmDialog($"Вы удаляете {targets.Count} {RecordsPlural(targets.Count)}. Подтвердить?");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        // Каждую запись удаляем в её БД.
        foreach (var r in targets) RepoOf(r).DeleteCalendarRecord(r.Id!.Value);
        CollectionsMayHaveChanged?.Invoke(this, EventArgs.Empty);
        RefreshCurrent();
    }

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

    private void SeedIfEmpty()
    {
        if (DefaultRepo.CountCalendarRecords() > 0) return;

        var today = DateTime.Today;
        DefaultRepo.AddCalendarRecord(new CalendarRecord { Title = "Стэндап", StartDt = today.AddHours(10), EndDt = today.AddHours(10.5), Color = "#007AFF" });
        DefaultRepo.AddCalendarRecord(new CalendarRecord { Title = "Обед", StartDt = today.AddHours(13), EndDt = today.AddHours(14), Color = "#34C759" });
        DefaultRepo.AddCalendarRecord(new CalendarRecord { Title = "Ревью", StartDt = today.AddDays(1).AddHours(15), EndDt = today.AddDays(1).AddHours(16), Color = "#FF9500" });
    }
}
