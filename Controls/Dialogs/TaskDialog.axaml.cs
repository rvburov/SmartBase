using Avalonia;
using Avalonia.Controls;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

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

        StartDateField.Date = DateTime.Today;
        EndDateField.Date   = DateTime.Today;
        StartTimeField.Time = _savedStartTime;
        EndTimeField.Time   = _savedEndTime;

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

        WireTimeField(StartTimeField, t => _savedStartTime = t);
        WireTimeField(EndTimeField,   t => _savedEndTime   = t);

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += (_, _) => Close(new TaskDialogResult(null, Deleted: true));
        SaveBtn.Click   += (_, _) => TrySave();

        Deactivated += (_, _) => ClosePopups();
        Closing     += (_, _) => ClosePopups();

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

        // Пункт списка — одна строка (TitleText), выбор идёт по индексу.
        CollectionCombo.ItemsSource   = _collections.Select(c => TitleText.FirstLine(c.Name)).ToList();
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
            AllDayCheck.IsChecked = t.AllDay;
            StartDateField.Date   = start.Date;
            EndDateField.Date     = end.Date;
            if (!t.AllDay)
            {
                _savedStartTime = start.TimeOfDay;
                _savedEndTime   = end.TimeOfDay;
            }
            StartTimeField.Time = _savedStartTime;
            EndTimeField.Time   = _savedEndTime;
        }
        else NoDateCheck.IsChecked = true;

        if (t.CollectionId.HasValue)
        {
            var idx = _collections.FindIndex(c => c.Id == t.CollectionId);
            if (idx >= 0) CollectionCombo.SelectedIndex = idx;
        }
    }

    // «Без даты» прячет блоки срока; «Весь день» — только поля времени. Смена коллекции
    // у существующей задачи без даты не поддерживается (перенос идёт календарным путём,
    // которому нужна дата) — комбо гасится, выбор ВОЗВРАЩАЕТСЯ к своей коллекции и рядом
    // появляется подсказка. Раньше комбо только гасилось: пользователь выбирал коллекцию,
    // снимал срок — и сохранение проходило «успешно», ничего не перенеся.
    private void UpdateDateVisibility()
    {
        var noDate   = NoDateCheck.IsChecked == true;
        var isAllDay = AllDayCheck.IsChecked == true;

        AllDayCheck.IsEnabled  = !noDate;
        StartSection.IsVisible = !noDate;
        EndSection.IsVisible   = !noDate;

        StartLabel.IsVisible     = !isAllDay;
        StartTimeField.IsVisible = !isAllDay;
        EndLabel.IsVisible       = !isAllDay;
        EndTimeField.IsVisible   = !isAllDay;

        var span = isAllDay ? 3 : 1;
        Grid.SetColumnSpan(StartDateField, span);
        Grid.SetColumnSpan(EndDateField, span);

        if (!isAllDay)
        {
            StartTimeField.Time = _savedStartTime;
            EndTimeField.Time   = _savedEndTime;
        }

        var lockCollection = _editTask is not null && noDate;
        CollectionCombo.IsEnabled    = !lockCollection;
        CollectionLockHint.IsVisible = lockCollection;
        if (lockCollection) SelectTaskCollection();
    }

    // Возвращает комбо к коллекции редактируемой задачи. Нужен там, где перенос невозможен:
    // показать выбор, который реально уйдёт в сохранение, а не тот, что пользователь успел
    // сделать до снятия срока.
    private void SelectTaskCollection()
    {
        if (_editTask?.CollectionId is not int own) return;
        var idx = _collections.FindIndex(c => c.Id == own);
        if (idx >= 0 && idx != CollectionCombo.SelectedIndex) CollectionCombo.SelectedIndex = idx;
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

        var noDate = NoDateCheck.IsChecked == true;

        // Задачу без срока переносить нечем — календарного пути нет, и репозиторий такой
        // выбор осознанно игнорирует. Берём её собственную коллекцию, чтобы сохранённое
        // совпало с показанным: комбо в этом режиме и так возвращено к ней (см.
        // UpdateDateVisibility), это защита от рассинхрона состояния окна.
        var colIdx = CollectionCombo.SelectedIndex;
        var colId  = _editTask is not null && noDate
            ? _editTask.CollectionId
            : colIdx >= 0 ? _collections[colIdx].Id : null;

        DateTime? dueStart = null, dueEnd = null;
        var isAllDay = false;
        if (!noDate)
        {
            var startDate = StartDateField.Date ?? DateTime.Today;
            var endDate   = EndDateField.Date   ?? DateTime.Today;
            var startTime = StartTimeField.Time;
            var endTime   = EndTimeField.Time;

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

    // ── Обвязка полей срока (те же виджеты, что в календарном RecordDialog) ────

    // Выпадашки полей даты и времени — свои Popup'ы виджетов; закрываем их вместе,
    // когда окно теряет фокус или закрывается.
    private void ClosePopups()
    {
        StartDateField.ClosePopup();
        EndDateField.ClosePopup();
        StartTimeField.ClosePopup();
        EndTimeField.ClosePopup();
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

    // Запоминаем последнее осмысленное время: при «Весь день» поля времени скрыты,
    // и при возврате переключателя их надо восстановить.
    private void WireTimeField(TimeField field, Action<TimeSpan> setSaved)
    {
        field.TimeChanged += (_, _) =>
        {
            if (AllDayCheck.IsChecked != true) setSaved(field.Time);
        };
    }

    private void ShowError(string message)
    {
        var pos = Position;
        NoticeBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!NoticeBanner.IsVisible) return;
        var pos = Position;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }
}
