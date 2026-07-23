using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Core.Validators;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

// Record.FieldId — выбранное поле-дата (куда кладётся дата события). Контроллер при
// изменении сравнивает его с прежним полем и при смене переносит дату (очищает старое).
public record RecordDialogResult(CalendarRecord? Record, bool Deleted = false);

public partial class RecordDialog : Window
{
    // ── Общие зависимости ─────────────────────────────────────────────────────
    private readonly ConstructorRepository _repository;

    // ── Схема-режим (конструктор) ─────────────────────────────────────────────
    private readonly int  _collectionId;
    private int?          _recordId;
    private readonly Dictionary<int, (DbField Field, Control Input)> _fieldWidgets = [];

    // ── Календарь-режим ───────────────────────────────────────────────────────
    private readonly CalendarRecord? _editRecord;
    private readonly List<(string Name, int? Id, string Color)> _collections = [];
    // Смонтированные БД и выбранная цель (выбор перенесён сюда из отдельного окна).
    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _databases = [];
    private int _selectedDbId;
    // Поля-даты выбранной коллекции (для списка «Дата события» при 2+ датах).
    private List<DbField> _eventDateFields = [];
    private TimeSpan _savedStartTime = new(9, 0, 0);
    private TimeSpan _savedEndTime   = new(10, 0, 0);

    // ── Конструктор: схема-режим ──────────────────────────────────────────────
    public RecordDialog(ConstructorRepository repository, int collectionId, int? recordId = null)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _recordId     = recordId;

        FieldsPanel.IsVisible    = true;
        CalendarSection.IsVisible = false;

        if (recordId.HasValue)
        {
            Title = "Изменить запись";
            DeleteBtn.IsVisible = true;
        }

        BuildFields();
        if (recordId.HasValue) FillValues(recordId.Value);

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += (_, _) => { _repository.DeleteRecord(_recordId!.Value); Close(false); };
        SaveBtn.Click   += (_, _) => TrySaveSchema();
    }

    // ── Конструктор: календарь-режим, выбор целевой БД в самом окне ────────────
    public RecordDialog(
        IReadOnlyList<(int DbId, string Name, ConstructorRepository Repo)> databases,
        int selectedDbId,
        CalendarRecord? editRecord = null,
        DateTime? presetDate       = null,
        TimeSpan? presetTime       = null,
        bool presetAllDay          = false)
    {
        InitializeComponent();

        _databases.AddRange(databases);
        _selectedDbId = _databases.Any(d => d.DbId == selectedDbId) ? selectedDbId : _databases[0].DbId;
        _repository   = CurrentRepo;
        _editRecord   = editRecord;
        _collectionId = 0;

        FieldsPanel.IsVisible     = false;
        CalendarSection.IsVisible = true;

        // Выбор БД показываем, когда смонтировано несколько баз (и при создании, и при
        // изменении — там смена БД означает перенос записи). При смене БД список коллекций
        // перезагружается из выбранной базы.
        var showDbPicker = _databases.Count > 1;
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
                LoadCollections(_databases[idx].Repo);
            };
        }

        LoadCollections(CurrentRepo);

        StartTimeList.ItemsSource = TimeText.Options;
        EndTimeList.ItemsSource   = TimeText.Options;

        var now = presetDate ?? DateTime.Today;
        StartDatePicker.SelectedDate = now;
        EndDatePicker.SelectedDate   = now;

        if (presetTime.HasValue)
        {
            _savedStartTime = presetTime.Value;
            _savedEndTime   = presetTime.Value + TimeSpan.FromHours(1);
        }
        StartTimeInput.Text = TimeText.Format(_savedStartTime);
        EndTimeInput.Text   = TimeText.Format(_savedEndTime);

        if (editRecord is not null)
        {
            Title               = "Изменить запись";
            DeleteBtn.IsVisible = true;
            FillFromRecord(editRecord);
        }

        if (presetAllDay) AllDayCheck.IsChecked = true;

        TitleBox.TextChanged += (_, _) => HideError();

        AllDayCheck.IsCheckedChanged += (_, _) =>
        {
            HideError();
            var pos = Position;
            UpdateTimeVisibility();
            KeepWindowPosition(pos);
        };

        WireTimeField(StartTimeInput, StartTimeArrow, StartTimePopup, StartTimeList,
                      () => _savedStartTime, t => _savedStartTime = t);
        WireTimeField(EndTimeInput, EndTimeArrow, EndTimePopup, EndTimeList,
                      () => _savedEndTime, t => _savedEndTime = t);

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += (_, _) => Close(new RecordDialogResult(null, Deleted: true));
        SaveBtn.Click   += (_, _) => TrySaveCalendar(editRecord?.Id);

        Deactivated += (_, _) => CloseTimePopups();
        Closing     += (_, _) => CloseTimePopups();

        // Список «Дата события» зависит от выбранной коллекции — обновляем при её смене
        // (смена БД тоже сюда приводит: LoadCollections сбрасывает выбор коллекции).
        CollectionCombo.SelectionChanged += (_, _) => UpdateEventDateField();
        UpdateEventDateField();

        UpdateTimeVisibility();
    }

    // Список «Дата записи»: показываем всегда. Перечисляет видимые в календаре поля-даты
    // коллекции (IsCalStart) — куда положить дату этого события. По умолчанию: при изменении —
    // поле самого события, при создании — первое доступное. Если видимых полей-дат нет —
    // список пуст, и при сохранении дата ляжет в системное «Дата» (создаётся автоматически).
    private void UpdateEventDateField()
    {
        var idx   = CollectionCombo.SelectedIndex;
        var colId = idx >= 0 && idx < _collections.Count ? _collections[idx].Id : null;

        _eventDateFields = colId.HasValue
            ? CurrentRepo.GetFields(colId.Value).Where(f => f.FieldType == "date" && f.IsCalStart).ToList()
            : [];

        EventDateSection.IsVisible = true;
        EventDateCombo.ItemsSource = _eventDateFields.Select(f => f.Name).ToList();

        var sel = _editRecord is not null
            ? _eventDateFields.FindIndex(f => f.Id == _editRecord.FieldId)
            : -1;
        if (sel < 0 && _eventDateFields.Count > 0) sel = 0;
        EventDateCombo.SelectedIndex = sel;
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

    // ══════════════════════════════════════════════════════════════════════════
    // ─── Схема-режим ──────────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private void BuildFields()
    {
        var fields = _repository.GetFields(_collectionId);
        foreach (var field in fields)
        {
            var label = new TextBlock
            {
                Text       = field.Name,
                FontWeight = (Avalonia.Media.FontWeight)this.FindResource("FontWeightBold")!,
                FontSize   = (double)this.FindResource("FontSizeBase")!,
                Foreground = (Avalonia.Media.IBrush)this.FindResource("BrushSecondaryText")!,
            };
            FieldsPanel.Children.Add(label);

            var input = MakeInput(field);
            _fieldWidgets[field.Id!.Value] = (field, input);
            FieldsPanel.Children.Add(input);
        }
    }

    private Control MakeInput(DbField field)
    {
        Dictionary<string, object> config = [];
        try { config = JsonSerializer.Deserialize<Dictionary<string, object>>(field.ConfigJson ?? "{}") ?? []; }
        catch { /* ignore bad json */ }

        return field.FieldType switch
        {
            "number" => new NumericUpDown { Minimum = decimal.MinValue, Maximum = decimal.MaxValue, Height = 36 },
            "date"   => new DateEditor(),
            "select" or "status" => BuildSelectCombo(config),
            "multiselect" => BuildMultiSelect(config),
            "user"   => BuildUserCombo(),
            "checkbox" => new CheckBox { VerticalAlignment = VerticalAlignment.Center },
            "url"    => new TextBox { Watermark = "https://", Height = 36 },
            _        => new TextBox { Height = 36 },  // text, default
        };
    }

    // Тип «пользователь»: опции — допущенные пользователи базы, по умолчанию текущий.
    // В личной базе допущен только владелец — выбрать можно лишь себя.
    private ComboBox BuildUserCombo()
    {
        var opts = _repository.GetUserOptions();
        if (opts.Count == 0) opts = [Utils.CurrentUser.DisplayName];
        var combo = new ComboBox
        {
            ItemsSource         = opts,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height              = 36,
        };
        var me = opts.IndexOf(Utils.CurrentUser.DisplayName);
        combo.SelectedIndex = me >= 0 ? me : 0;
        return combo;
    }

    private static ComboBox BuildSelectCombo(Dictionary<string, object> config)
    {
        var opts = SelectOptions(config);
        return new ComboBox
        {
            ItemsSource    = opts.Count > 0 ? opts : ["—"],
            SelectedIndex  = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height         = 36,
        };
    }

    private static Components.MultiSelectBox BuildMultiSelect(Dictionary<string, object> config) =>
        new(SelectOptions(config), "") { HorizontalAlignment = HorizontalAlignment.Stretch };

    private static List<string> SelectOptions(Dictionary<string, object> config)
    {
        var opts = new List<string>();
        if (config.TryGetValue("options", out var raw) && raw is JsonElement elem && elem.ValueKind == JsonValueKind.Array)
            opts = elem.EnumerateArray().Select(o => o.ToString()).ToList();
        return opts;
    }

    private void FillValues(int recordId)
    {
        var values = _repository.GetFieldValues(recordId);
        foreach (var (fieldId, (field, input)) in _fieldWidgets)
        {
            var val = values.GetValueOrDefault(fieldId, "");
            switch (field.FieldType)
            {
                case "checkbox":
                    ((CheckBox)input).IsChecked = val is "1" or "true" or "True";
                    break;
                case "date":
                    ((DateEditor)input).Storage = val;
                    break;
                case "select":
                case "status":
                    var cb = (ComboBox)input;
                    var idx = (cb.ItemsSource as List<string>)?.IndexOf(val) ?? -1;
                    if (idx >= 0) cb.SelectedIndex = idx;
                    break;
                case "multiselect":
                    ((Components.MultiSelectBox)input).SetStorage(val);
                    break;
                case "user":
                    // Существующая запись показывает СВОЁ значение (в т.ч. пустое или имя
                    // пользователя, которого уже нет в базе, — добавляем его в опции сырым).
                    var ucb  = (ComboBox)input;
                    var opts = ucb.ItemsSource as List<string> ?? [];
                    if (val.Length > 0 && !opts.Contains(val))
                    {
                        opts = [.. opts, val];
                        ucb.ItemsSource = opts;
                    }
                    ucb.SelectedIndex = val.Length > 0 ? opts.IndexOf(val) : -1;
                    break;
                case "number":
                    if (decimal.TryParse(val, out var num))
                        ((NumericUpDown)input).Value = num;
                    break;
                default:
                    ((TextBox)input).Text = val;
                    break;
            }
        }
    }

    private static string GetValue(DbField field, Control input) => field.FieldType switch
    {
        "checkbox" => ((CheckBox)input).IsChecked == true ? "1" : "0",
        "date"     => ((DateEditor)input).Storage ?? "",
        "select" or "status" or "user" => ((ComboBox)input).SelectedItem?.ToString() ?? "",
        "multiselect" => ((Components.MultiSelectBox)input).Storage,
        "number"   => ((NumericUpDown)input).Value?.ToString() ?? "",
        _          => ((TextBox)input).Text ?? "",
    };

    private void TrySaveSchema()
    {
        try
        {
            if (_recordId is null)
            {
                var record = _repository.AddRecord(new DbRecord { CollectionId = _collectionId });
                _recordId = record.Id;
            }

            foreach (var (fieldId, (field, input)) in _fieldWidgets)
                _repository.SetFieldValue(_recordId!.Value, fieldId, GetValue(field, input));
        }
        catch (InvalidOperationException ex)
        {
            // Права отозвали, пока окно было открыто (PermissionDeniedException и т.п.) —
            // причину показываем баннером, ввод пользователя не теряется.
            ShowError(ex.Message);
            return;
        }

        Close(true);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ─── Календарь-режим ──────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private void CloseTimePopups()
    {
        StartTimePopup.IsOpen = false;
        EndTimePopup.IsOpen   = false;
    }

    // Восстанавливает позицию окна после пересчёта высоты (SizeToContent),
    // чтобы левый-верхний угол не сдвигался при переключении «Весь день».
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

    private void FillFromRecord(CalendarRecord r)
    {
        TitleBox.Text                = r.Title;
        DescBox.Text                 = r.Description;
        AllDayCheck.IsChecked        = r.AllDay;
        StartDatePicker.SelectedDate = r.StartDt.Date;
        EndDatePicker.SelectedDate   = r.EndDt.Date;

        if (!r.AllDay)
        {
            _savedStartTime = r.StartDt.TimeOfDay;
            _savedEndTime   = r.EndDt.TimeOfDay;
        }
        StartTimeInput.Text = TimeText.Format(_savedStartTime);
        EndTimeInput.Text   = TimeText.Format(_savedEndTime);

        if (r.CollectionId.HasValue)
        {
            var idx = _collections.FindIndex(c => c.Id == r.CollectionId);
            if (idx >= 0) CollectionCombo.SelectedIndex = idx;
        }
    }

    private void UpdateTimeVisibility()
    {
        var isAllDay = AllDayCheck.IsChecked == true;

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
    }

    private void TrySaveCalendar(int? existingId)
    {
        HideError();

        var title = TitleBox.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(title))
        {
            ShowError("Введите название записи");
            return;
        }

        var startDate = StartDatePicker.SelectedDate ?? DateTime.Today;
        var endDate   = EndDatePicker.SelectedDate   ?? DateTime.Today;
        var startTime = TimeText.TryParse(StartTimeInput.Text, out var st) ? st : _savedStartTime;
        var endTime   = TimeText.TryParse(EndTimeInput.Text,   out var et) ? et : _savedEndTime;

        var isAllDay = AllDayCheck.IsChecked == true;
        var startDt  = startDate.Date + (isAllDay ? TimeSpan.Zero : startTime);
        var endDt    = endDate.Date   + (isAllDay ? new TimeSpan(23, 59, 0) : endTime);

        var colIdx = CollectionCombo.SelectedIndex;
        var colId  = colIdx >= 0 ? _collections[colIdx].Id : null;

        // Поле-дата, куда положить дату события. Если есть видимые поля-даты — берём выбранное;
        // если их нет — 0 (репозиторий положит дату в системное «Дата», создав его).
        var chosenFieldId = 0;
        if (_eventDateFields.Count > 0)
        {
            var di = EventDateCombo.SelectedIndex;
            if (di < 0 || di >= _eventDateFields.Count)
            {
                ShowError("Выберите дату для отображения в календаре");
                return;
            }
            chosenFieldId = _eventDateFields[di].Id ?? 0;
        }

        var candidate = new CalendarRecord
        {
            Id           = existingId,
            DbId         = _selectedDbId,
            FieldId      = chosenFieldId,
            Title        = title,
            Description  = DescBox.Text ?? "",
            StartDt      = startDt,
            EndDt        = endDt,
            AllDay       = isAllDay,
            CollectionId = colId,
            Color        = colId.HasValue ? _collections[colIdx].Color : "#007AFF",
        };

        var error = CalendarRecordValidator.Validate(candidate);
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Close(new RecordDialogResult(candidate));
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
