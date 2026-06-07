using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MyPlanner.Core.Models;
using MyPlanner.Core.Validators;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

public record RecordDialogResult(Event? Event, bool Deleted = false);

public partial class RecordDialog : Window
{
    // ── Общие зависимости ─────────────────────────────────────────────────────
    private readonly ConstructorRepository _repository;
    private readonly EventRepository?      _eventRepo;

    // ── Схема-режим (конструктор) ─────────────────────────────────────────────
    private readonly int  _collectionId;
    private int?          _recordId;
    private readonly Dictionary<int, (DbField Field, Control Input)> _fieldWidgets = [];

    // ── Календарь-режим ───────────────────────────────────────────────────────
    private readonly Event? _editEvent;
    private readonly List<(string Name, int? Id, string Color)> _collections = [];
    private TimeSpan _savedStartTime = new(9, 0, 0);
    private TimeSpan _savedEndTime   = new(10, 0, 0);

    // Варианты времени с шагом 15 минут: 00:00, 00:15, … 23:45
    private static readonly List<string> TimeOptions = BuildTimeOptions();

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

    // ── Конструктор: календарь-режим ──────────────────────────────────────────
    public RecordDialog(
        EventRepository events,
        ConstructorRepository constructor,
        Event? editEvent     = null,
        DateTime? presetDate = null,
        TimeSpan? presetTime = null,
        bool presetAllDay    = false)
    {
        InitializeComponent();

        _repository   = constructor;
        _eventRepo    = events;
        _editEvent    = editEvent;
        _collectionId = 0;

        FieldsPanel.IsVisible     = false;
        CalendarSection.IsVisible = true;

        // Заполняем список коллекций
        var cols = constructor.GetAllCollections();
        _collections.Add(("Без коллекции", null, "#8E8E93"));
        foreach (var c in cols)
            _collections.Add((c.Name, c.Id, c.Color ?? "#8E8E93"));

        CollectionCombo.ItemsSource   = _collections.Select(c => c.Name).ToList();
        CollectionCombo.SelectedIndex = 0;

        StartTimeList.ItemsSource = TimeOptions;
        EndTimeList.ItemsSource   = TimeOptions;

        var now = presetDate ?? DateTime.Today;
        StartDatePicker.SelectedDate = now;
        EndDatePicker.SelectedDate   = now;

        if (presetTime.HasValue)
        {
            _savedStartTime = presetTime.Value;
            _savedEndTime   = presetTime.Value + TimeSpan.FromHours(1);
        }
        StartTimeInput.Text = FormatTime(_savedStartTime);
        EndTimeInput.Text   = FormatTime(_savedEndTime);

        if (editEvent is not null)
        {
            Title               = "Изменить запись";
            DeleteBtn.IsVisible = true;
            FillFromEvent(editEvent);
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
        SaveBtn.Click   += (_, _) => TrySaveCalendar(editEvent?.Id);

        Deactivated += (_, _) => CloseTimePopups();
        Closing     += (_, _) => CloseTimePopups();

        UpdateTimeVisibility();
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
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize   = 13,
                Foreground = Avalonia.Media.Brushes.Gray,
            };
            FieldsPanel.Children.Add(label);

            var input = MakeInput(field);
            _fieldWidgets[field.Id!.Value] = (field, input);
            FieldsPanel.Children.Add(input);
        }
    }

    private static Control MakeInput(DbField field)
    {
        Dictionary<string, object> config = [];
        try { config = JsonSerializer.Deserialize<Dictionary<string, object>>(field.ConfigJson ?? "{}") ?? []; }
        catch { /* ignore bad json */ }

        return field.FieldType switch
        {
            "number" => new NumericUpDown { Minimum = decimal.MinValue, Maximum = decimal.MaxValue, Height = 36 },
            "date"   => new CalendarDatePicker { SelectedDate = DateTime.Today, Height = 36, HorizontalAlignment = HorizontalAlignment.Stretch },
            "select" => BuildSelectCombo(config),
            "checkbox" => new CheckBox { VerticalAlignment = VerticalAlignment.Center },
            "url"    => new TextBox { Watermark = "https://", Height = 36 },
            _        => new TextBox { Height = 36 },  // text, default
        };
    }

    private static ComboBox BuildSelectCombo(Dictionary<string, object> config)
    {
        var opts = new List<string>();
        if (config.TryGetValue("options", out var raw) && raw is JsonElement elem && elem.ValueKind == JsonValueKind.Array)
            opts = elem.EnumerateArray().Select(o => o.ToString()).ToList();

        return new ComboBox
        {
            ItemsSource    = opts.Count > 0 ? opts : ["—"],
            SelectedIndex  = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height         = 36,
        };
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
                    if (DateTime.TryParseExact(val, "dd.MM.yyyy", null, DateTimeStyles.None, out var dt))
                        ((CalendarDatePicker)input).SelectedDate = dt;
                    break;
                case "select":
                    var cb = (ComboBox)input;
                    var idx = (cb.ItemsSource as List<string>)?.IndexOf(val) ?? -1;
                    if (idx >= 0) cb.SelectedIndex = idx;
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
        "date"     => ((CalendarDatePicker)input).SelectedDate?.ToString("dd.MM.yyyy") ?? "",
        "select"   => ((ComboBox)input).SelectedItem?.ToString() ?? "",
        "number"   => ((NumericUpDown)input).Value?.ToString() ?? "",
        _          => ((TextBox)input).Text ?? "",
    };

    private void TrySaveSchema()
    {
        if (_recordId is null)
        {
            var record = _repository.AddRecord(new DbRecord { CollectionId = _collectionId });
            _recordId = record.Id;
        }

        foreach (var (fieldId, (field, input)) in _fieldWidgets)
            _repository.SetFieldValue(_recordId!.Value, fieldId, GetValue(field, input));

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
            if (AllDayCheck.IsChecked != true && TryParseTime(input.Text, out var t))
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
            if (list.SelectedItem is string s && TryParseTime(s, out var t))
            {
                setSaved(t);
                input.Text = s;
            }
            popup.IsOpen = false;
        };
    }

    private static void SelectInList(ListBox list, TimeSpan time)
    {
        var text = FormatTime(time);
        var idx  = TimeOptions.IndexOf(text);
        if (idx < 0)
        {
            var quarter = (time.Hours * 60 + time.Minutes) / 15;
            idx = Math.Clamp(quarter, 0, TimeOptions.Count - 1);
        }
        list.SelectedIndex = idx;
    }

    private static List<string> BuildTimeOptions()
    {
        var list = new List<string>(96);
        for (var t = TimeSpan.Zero; t < TimeSpan.FromDays(1); t += TimeSpan.FromMinutes(15))
            list.Add(FormatTime(t));
        return list;
    }

    private static string FormatTime(TimeSpan t) =>
        $"{t.Hours:D2}:{t.Minutes:D2}";

    private static bool TryParseTime(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().Replace('.', ':').Replace(',', ':').Replace(' ', ':');

        if (!text.Contains(':') && int.TryParse(text, out var digits))
        {
            text = digits switch
            {
                <= 23   => $"{digits}:00",
                >= 100  => $"{digits / 100}:{digits % 100:D2}",
                _       => text,
            };
        }

        var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            return false;
        var m = 0;
        if (parts.Length > 1 &&
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
            return false;

        if (h is < 0 or > 23 || m is < 0 or > 59) return false;

        result = new TimeSpan(h, m, 0);
        return true;
    }

    private void FillFromEvent(Event ev)
    {
        TitleBox.Text                = ev.Title;
        DescBox.Text                 = ev.Description;
        AllDayCheck.IsChecked        = ev.AllDay;
        StartDatePicker.SelectedDate = ev.StartDt.Date;
        EndDatePicker.SelectedDate   = ev.EndDt.Date;

        if (!ev.AllDay)
        {
            _savedStartTime = ev.StartDt.TimeOfDay;
            _savedEndTime   = ev.EndDt.TimeOfDay;
        }
        StartTimeInput.Text = FormatTime(_savedStartTime);
        EndTimeInput.Text   = FormatTime(_savedEndTime);

        if (ev.CollectionId.HasValue)
        {
            var idx = _collections.FindIndex(c => c.Id == ev.CollectionId);
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
            StartTimeInput.Text = FormatTime(_savedStartTime);
            EndTimeInput.Text   = FormatTime(_savedEndTime);
        }
    }

    private void TrySaveCalendar(int? existingId)
    {
        HideError();

        var title = TitleBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(title))
        {
            ShowError("Укажите название записи");
            TitleBox.Focus();
            return;
        }

        var startDate = StartDatePicker.SelectedDate ?? DateTime.Today;
        var endDate   = EndDatePicker.SelectedDate   ?? DateTime.Today;
        var startTime = TryParseTime(StartTimeInput.Text, out var st) ? st : _savedStartTime;
        var endTime   = TryParseTime(EndTimeInput.Text,   out var et) ? et : _savedEndTime;

        var isAllDay = AllDayCheck.IsChecked == true;
        var startDt  = startDate.Date + (isAllDay ? TimeSpan.Zero : startTime);
        var endDt    = endDate.Date   + (isAllDay ? new TimeSpan(23, 59, 0) : endTime);

        var colIdx = CollectionCombo.SelectedIndex;
        var colId  = colIdx >= 0 ? _collections[colIdx].Id : null;

        var candidate = new Event
        {
            Id           = existingId,
            Title        = title,
            Description  = DescBox.Text ?? "",
            StartDt      = startDt,
            EndDt        = endDt,
            AllDay       = isAllDay,
            CollectionId = colId,
            Color        = colId.HasValue ? _collections[colIdx].Color : "#007AFF",
        };

        var error = EventValidator.Validate(candidate);
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
        ErrorText.Text        = message;
        ErrorBanner.IsVisible = true;
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.IsVisible = false;
        KeepWindowPosition(pos);
    }
}
