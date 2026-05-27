using Avalonia.Controls;
using MyPlanner.Core.Models;
using MyPlanner.Core.Validators;
using MyPlanner.Database.Repositories;

namespace MyPlanner.Controls.Dialogs;

public record EventDialogResult(Event? Event, bool Deleted = false);

public partial class EventDialog : Window
{
    private readonly List<(string Name, int? Id, string Color)> _collections;
    private TimeSpan _savedStartTime = new(9, 0, 0);
    private TimeSpan _savedEndTime   = new(10, 0, 0);

    public EventDialog(
        CategoryRepository categories,
        ConstructorRepository constructor,
        Event? editEvent       = null,
        DateTime? presetDate   = null,
        TimeSpan? presetTime   = null)
    {
        InitializeComponent();

        var cols = constructor.GetAllCollections();
        _collections = [("Без коллекции", null, "#8E8E93"),
                        ..cols.Select(c => (c.Name, (int?)c.Id, c.Color ?? "#8E8E93"))];

        CollectionCombo.ItemsSource   = _collections.Select(c => c.Name).ToList();
        CollectionCombo.SelectedIndex = 0;

        var now = presetDate ?? DateTime.Today;
        StartDatePicker.SelectedDate = now;
        EndDatePicker.SelectedDate   = now;

        if (presetTime.HasValue)
        {
            _savedStartTime = presetTime.Value;
            _savedEndTime   = presetTime.Value + TimeSpan.FromHours(1);
        }
        StartTimePicker.SelectedTime = _savedStartTime;
        EndTimePicker.SelectedTime   = _savedEndTime;

        if (editEvent is not null)
        {
            Title           = "Изменить событие";
            DeleteBtn.IsVisible = true;
            FillFromEvent(editEvent);
        }

        AllDayCheck.IsCheckedChanged += (_, _) => UpdateTimeVisibility();
        StartTimePicker.SelectedTimeChanged += (_, _) =>
        {
            if (AllDayCheck.IsChecked != true)
                _savedStartTime = StartTimePicker.SelectedTime ?? _savedStartTime;
        };
        EndTimePicker.SelectedTimeChanged += (_, _) =>
        {
            if (AllDayCheck.IsChecked != true)
                _savedEndTime = EndTimePicker.SelectedTime ?? _savedEndTime;
        };

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += (_, _) => Close(new EventDialogResult(null, Deleted: true));
        SaveBtn.Click   += (_, _) => TrySave(editEvent?.Id);

        UpdateTimeVisibility();
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
        StartTimePicker.SelectedTime = _savedStartTime;
        EndTimePicker.SelectedTime   = _savedEndTime;

        if (ev.CollectionId.HasValue)
        {
            var idx = _collections.FindIndex(c => c.Id == ev.CollectionId);
            if (idx >= 0) CollectionCombo.SelectedIndex = idx;
        }
    }

    private void UpdateTimeVisibility()
    {
        var isAllDay = AllDayCheck.IsChecked == true;
        StartLabel.IsVisible = !isAllDay;
        StartTimePicker.IsVisible = !isAllDay;
        EndLabel.IsVisible   = !isAllDay;
        EndTimePicker.IsVisible   = !isAllDay;

        if (isAllDay)
        {
            StartTimePicker.SelectedTime = TimeSpan.Zero;
            EndTimePicker.SelectedTime   = new TimeSpan(23, 59, 0);
        }
        else
        {
            StartTimePicker.SelectedTime = _savedStartTime;
            EndTimePicker.SelectedTime   = _savedEndTime;
        }
    }

    private void TrySave(int? existingId)
    {
        var title = TitleBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(title)) { TitleBox.Focus(); return; }

        var startDate = StartDatePicker.SelectedDate ?? DateTime.Today;
        var endDate   = EndDatePicker.SelectedDate   ?? DateTime.Today;
        var startTime = StartTimePicker.SelectedTime ?? _savedStartTime;
        var endTime   = EndTimePicker.SelectedTime   ?? _savedEndTime;

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
        if (error is not null) { TitleBox.Focus(); return; }

        Close(new EventDialogResult(candidate));
    }
}
