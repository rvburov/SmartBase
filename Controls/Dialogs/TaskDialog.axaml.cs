using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DataBase.Core.Models;
using DataBase.Database.Repositories;
using DataBase.Utils;

namespace DataBase.Controls.Dialogs;

// Кандидат-задача собирается окном, персистит её контроллер (AddTaskRecord /
// UpdateTaskRecord / DeleteCalendarRecord) — как RecordDialog в календаре.
public record TaskDialogResult(TaskRecord? Task, bool Deleted = false);

public partial class TaskDialog : Window
{
    private readonly TaskRecord? _editTask;
    private readonly List<(string Name, int? Id, string Color)> _collections = [];
    // Смонтированные БД и выбранная цель. Смена БД доступна только при создании:
    // перенос записи между БД из окна задач в v1 не поддерживается.
    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _databases = [];
    private int _selectedDbId;
    private TimeSpan _savedStartTime = new(9, 0, 0);
    private TimeSpan _savedEndTime   = new(10, 0, 0);

    public TaskDialog(
        IReadOnlyList<(int DbId, string Name, ConstructorRepository Repo)> databases,
        int selectedDbId,
        TaskRecord? editTask   = null,
        int? presetCollectionId = null,
        string? presetTitle     = null)
    {
        InitializeComponent();

        _databases.AddRange(databases);
        _selectedDbId = _databases.Any(d => d.DbId == selectedDbId) ? selectedDbId : _databases[0].DbId;
        _editTask     = editTask;

        // Выбор БД — только при создании и когда смонтировано несколько баз.
        var showDbPicker = _databases.Count > 1 && editTask is null;
        DbSection.IsVisible = showDbPicker;
        if (showDbPicker)
        {
            DbCombo.ItemsSource   = _databases.Select(d => d.Name).ToList();
            DbCombo.SelectedIndex = _databases.FindIndex(d => d.DbId == _selectedDbId);
            DbCombo.SelectionChanged += (_, _) =>
            {
                var idx = DbCombo.SelectedIndex;
                if (idx < 0 || idx >= _databases.Count) return;
                _selectedDbId = _databases[idx].DbId;
                LoadCollections(CurrentRepo);
                LoadUserOptions();
            };
        }

        LoadCollections(CurrentRepo);
        LoadUserOptions();

        StatusCombo.ItemsSource   = ConstructorRepository.TaskStatusOptions.ToList();
        StatusCombo.SelectedIndex = 0;

        StartTimeList.ItemsSource = TimeText.Options;
        EndTimeList.ItemsSource   = TimeText.Options;

        StartDatePicker.SelectedDate = DateTime.Today;
        EndDatePicker.SelectedDate   = DateTime.Today;
        StartTimeInput.Text = TimeText.Format(_savedStartTime);
        EndTimeInput.Text   = TimeText.Format(_savedEndTime);

        if (editTask is not null)
        {
            Title               = "Изменить задачу";
            DeleteBtn.IsVisible = true;
            FillFromTask(editTask);
        }
        else
        {
            if (presetCollectionId is int pc)
            {
                var idx = _collections.FindIndex(c => c.Id == pc);
                if (idx >= 0) CollectionCombo.SelectedIndex = idx;
            }
            if (!string.IsNullOrEmpty(presetTitle)) TitleBox.Text = presetTitle;
        }

        TitleBox.TextChanged += (_, _) => HideError();

        NoDateCheck.IsCheckedChanged += (_, _) =>
        {
            HideError();
            var pos = Position;
            UpdateDateVisibility();
            KeepWindowPosition(pos);
        };
        AllDayCheck.IsCheckedChanged += (_, _) =>
        {
            HideError();
            var pos = Position;
            UpdateDateVisibility();
            KeepWindowPosition(pos);
        };

        WireTimeField(StartTimeInput, StartTimeArrow, StartTimePopup, StartTimeList,
                      () => _savedStartTime, t => _savedStartTime = t);
        WireTimeField(EndTimeInput, EndTimeArrow, EndTimePopup, EndTimeList,
                      () => _savedEndTime, t => _savedEndTime = t);

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += (_, _) => Close(new TaskDialogResult(null, Deleted: true));
        SaveBtn.Click   += (_, _) => TrySave();

        Deactivated += (_, _) => CloseTimePopups();
        Closing     += (_, _) => CloseTimePopups();

        UpdateDateVisibility();
    }

    // Репозиторий выбранной БД.
    private ConstructorRepository CurrentRepo
    {
        get
        {
            foreach (var d in _databases) if (d.DbId == _selectedDbId) return d.Repo;
            return _databases[0].Repo;
        }
    }

    // Перезагружает список коллекций из указанной БД. Если ни одной нет — плейсхолдер
    // «Без названия» (id=null): при сохранении репозиторий создаст эту коллекцию автоматически.
    private void LoadCollections(ConstructorRepository repo)
    {
        _collections.Clear();
        var cols = repo.GetAllCollections();
        if (cols.Count == 0)
            _collections.Add(("Без названия", null, "#007AFF"));
        foreach (var c in cols)
            _collections.Add((c.Name, c.Id, c.Color ?? "#8E8E93"));

        CollectionCombo.ItemsSource   = _collections.Select(c => c.Name).ToList();
        CollectionCombo.SelectedIndex = 0;
    }

    // Опции исполнителя/назначившего — допущенные пользователи выбранной БД, по умолчанию
    // текущий. Историческое значение редактируемой задачи добавляется сырым (пользователь
    // мог быть удалён/переименован) — как редактор записи с типом «пользователь».
    private void LoadUserOptions()
    {
        var opts = CurrentRepo.GetUserOptions();
        if (opts.Count == 0) opts = [CurrentUser.DisplayName];

        FillUserCombo(ExecutorCombo, opts, _editTask?.Executor ?? CurrentUser.DisplayName);
        FillUserCombo(AssignerCombo, opts, _editTask?.Assigner ?? CurrentUser.DisplayName);
    }

    private static void FillUserCombo(ComboBox combo, List<string> options, string selected)
    {
        var opts = options.ToList();
        if (selected.Length > 0 && !opts.Contains(selected)) opts.Add(selected);
        combo.ItemsSource = opts;
        var idx = selected.Length > 0 ? opts.IndexOf(selected) : -1;
        combo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void FillFromTask(TaskRecord t)
    {
        TitleBox.Text = t.Title;
        DescBox.Text  = t.Description;

        var statuses = ConstructorRepository.TaskStatusOptions.ToList();
        if (t.Status.Length > 0 && !statuses.Contains(t.Status)) statuses.Add(t.Status);
        StatusCombo.ItemsSource   = statuses;
        StatusCombo.SelectedIndex = t.Status.Length > 0 ? statuses.IndexOf(t.Status) : 0;

        if (t.DueStart is DateTime start)
        {
            var end = t.DueEnd ?? start;
            AllDayCheck.IsChecked        = t.AllDay;
            StartDatePicker.SelectedDate = start.Date;
            EndDatePicker.SelectedDate   = end.Date;
            if (!t.AllDay)
            {
                _savedStartTime = start.TimeOfDay;
                _savedEndTime   = end.TimeOfDay;
            }
            StartTimeInput.Text = TimeText.Format(_savedStartTime);
            EndTimeInput.Text   = TimeText.Format(_savedEndTime);
        }
        else NoDateCheck.IsChecked = true;

        if (t.CollectionId.HasValue)
        {
            var idx = _collections.FindIndex(c => c.Id == t.CollectionId);
            if (idx >= 0) CollectionCombo.SelectedIndex = idx;
        }
    }

    // «Без даты» прячет блоки срока; «Весь день» — только поля времени. Смена коллекции
    // у задачи без даты не поддерживается (перенос идёт календарным путём, которому нужна
    // дата) — комбо гасится.
    private void UpdateDateVisibility()
    {
        var noDate   = NoDateCheck.IsChecked == true;
        var isAllDay = AllDayCheck.IsChecked == true;

        AllDayCheck.IsEnabled  = !noDate;
        StartSection.IsVisible = !noDate;
        EndSection.IsVisible   = !noDate;

        StartLabel.IsVisible      = !isAllDay;
        StartTimeBorder.IsVisible = !isAllDay;
        EndLabel.IsVisible        = !isAllDay;
        EndTimeBorder.IsVisible   = !isAllDay;

        var span = isAllDay ? 3 : 1;
        Grid.SetColumnSpan(StartDatePicker, span);
        Grid.SetColumnSpan(EndDatePicker, span);

        if (!isAllDay)
        {
            StartTimeInput.Text = TimeText.Format(_savedStartTime);
            EndTimeInput.Text   = TimeText.Format(_savedEndTime);
        }

        CollectionCombo.IsEnabled = _editTask is null || !noDate;
    }

    private void TrySave()
    {
        HideError();

        var title = TitleBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(title))
        {
            ShowError("Введите название задачи");
            return;
        }

        var colIdx = CollectionCombo.SelectedIndex;
        var colId  = colIdx >= 0 ? _collections[colIdx].Id : null;

        var noDate = NoDateCheck.IsChecked == true;
        DateTime? dueStart = null, dueEnd = null;
        var isAllDay = false;
        if (!noDate)
        {
            var startDate = StartDatePicker.SelectedDate ?? DateTime.Today;
            var endDate   = EndDatePicker.SelectedDate   ?? DateTime.Today;
            var startTime = TimeText.TryParse(StartTimeInput.Text, out var st) ? st : _savedStartTime;
            var endTime   = TimeText.TryParse(EndTimeInput.Text,   out var et) ? et : _savedEndTime;

            isAllDay = AllDayCheck.IsChecked == true;
            dueStart = startDate.Date + (isAllDay ? TimeSpan.Zero : startTime);
            dueEnd   = endDate.Date   + (isAllDay ? new TimeSpan(23, 59, 0) : endTime);

            if (dueEnd <= dueStart)
            {
                ShowError("Время конца должно быть позже начала");
                return;
            }
        }

        var candidate = new TaskRecord
        {
            Id           = _editTask?.Id,
            DbId         = _selectedDbId,
            ExecFieldId  = _editTask?.ExecFieldId ?? 0,
            DueFieldId   = _editTask?.DueFieldId,
            Title        = title,
            Description  = DescBox.Text ?? "",
            Status       = StatusCombo.SelectedItem?.ToString() ?? ConstructorRepository.TaskStatusPlanned,
            Executor     = ExecutorCombo.SelectedItem?.ToString() ?? "",
            Assigner     = AssignerCombo.SelectedItem?.ToString() ?? "",
            DueStart     = dueStart,
            DueEnd       = dueEnd,
            AllDay       = isAllDay,
            CollectionId = colId,
            Color        = colId.HasValue ? _collections[colIdx].Color : "#007AFF",
        };

        Close(new TaskDialogResult(candidate));
    }

    // ── Обвязка полей времени (копия календарного RecordDialog) ────────────────

    private void CloseTimePopups()
    {
        StartTimePopup.IsOpen = false;
        EndTimePopup.IsOpen   = false;
    }

    // Восстанавливает позицию окна после пересчёта высоты (SizeToContent),
    // чтобы левый-верхний угол не сдвигался при переключении чекбоксов срока.
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }

    private void WireTimeField(
        TextBox input, Button arrow, Popup popup, ListBox list,
        Func<TimeSpan> getSaved, Action<TimeSpan> setSaved)
    {
        input.TextChanged += (_, _) =>
        {
            if (AllDayCheck.IsChecked != true && TimeText.TryParse(input.Text, out var t))
                setSaved(t);
        };

        arrow.Click += (_, _) =>
        {
            var wasOpen = popup.IsOpen;
            CloseTimePopups();
            if (wasOpen) return;
            SelectInList(list, getSaved());
            popup.IsOpen = true;
            list.ScrollIntoView(list.SelectedItem);
        };

        list.SelectionChanged += (_, _) =>
        {
            if (!popup.IsOpen) return;
            if (list.SelectedItem is string s && TimeText.TryParse(s, out var t))
            {
                setSaved(t);
                input.Text = s;
            }
            popup.IsOpen = false;
        };
    }

    private static void SelectInList(ListBox list, TimeSpan time)
    {
        var idx = TimeText.Options.ToList().IndexOf(TimeText.Format(time));
        if (idx < 0)
        {
            var quarter = (time.Hours * 60 + time.Minutes) / 15;
            idx = Math.Clamp(quarter, 0, TimeText.Options.Count - 1);
        }
        list.SelectedIndex = idx;
    }

    private void ShowError(string message)
    {
        var pos = Position;
        ErrorBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.Hide();
        KeepWindowPosition(pos);
    }
}
