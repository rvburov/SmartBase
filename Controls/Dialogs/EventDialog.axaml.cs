using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    // Варианты времени с шагом 15 минут: 00:00, 00:15, … 23:45
    private static readonly List<string> TimeOptions = BuildTimeOptions();

    public EventDialog(
        CategoryRepository categories,
        ConstructorRepository constructor,
        Event? editEvent       = null,
        DateTime? presetDate   = null,
        TimeSpan? presetTime   = null,
        bool presetAllDay      = false)
    {
        InitializeComponent();

        var cols = constructor.GetAllCollections();
        _collections = [("Без коллекции", null, "#8E8E93"),
                        ..cols.Select(c => (c.Name, (int?)c.Id, c.Color ?? "#8E8E93"))];

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

        // Прячем баннер ошибки, как только пользователь правит данные
        TitleBox.TextChanged += (_, _) => HideError();

        AllDayCheck.IsCheckedChanged += (_, _) =>
        {
            HideError();
            // Фиксируем левый-верхний угол: высота окна меняется (SizeToContent),
            // иначе окно перецентрируется и «прыгает».
            var pos = Position;
            UpdateTimeVisibility();
            KeepWindowPosition(pos);
        };

        WireTimeField(StartTimeInput, StartTimeArrow, StartTimePopup, StartTimeList,
                      () => _savedStartTime, t => _savedStartTime = t);
        WireTimeField(EndTimeInput, EndTimeArrow, EndTimePopup, EndTimeList,
                      () => _savedEndTime, t => _savedEndTime = t);

        DeleteBtn.Click += (_, _) => Close(new EventDialogResult(null, Deleted: true));
        SaveBtn.Click   += (_, _) => TrySave(editEvent?.Id);

        // Закрываем выпадающие списки при деактивации/закрытии окна,
        // иначе popup может «зависнуть» поверх других окон.
        Deactivated += (_, _) => CloseTimePopups();
        Closing     += (_, _) => CloseTimePopups();

        UpdateTimeVisibility();
    }

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

    // Связывает поле ввода, стрелку, popup и список в единый editable-combobox
    private void WireTimeField(
        TextBox input, Button arrow, Popup popup, ListBox list,
        Func<TimeSpan> getSaved, Action<TimeSpan> setSaved)
    {
        // Ручной ввод -> сохранённое время
        input.TextChanged += (_, _) =>
        {
            if (AllDayCheck.IsChecked != true && TryParseTime(input.Text, out var t))
                setSaved(t);
        };

        // Клик по стрелке открывает/закрывает список
        arrow.Click += (_, _) =>
        {
            var wasOpen = popup.IsOpen;
            CloseTimePopups();              // закрываем оба, чтобы не висели вместе
            if (wasOpen) return;            // повторный клик по той же стрелке = закрыть
            SelectInList(list, getSaved());
            popup.IsOpen = true;
            list.ScrollIntoView(list.SelectedItem);
        };

        // Выбор пункта в списке -> заполняет поле, закрывает popup
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
        // Если точного совпадения нет (ручной ввод), ближайшее снизу
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

    // Принимает "9:30", "09:30", "9.30", "930", "9" и т.п.
    private static bool TryParseTime(string? text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().Replace('.', ':').Replace(',', ':').Replace(' ', ':');

        // Чистые цифры без разделителя: "930" -> 9:30, "9" -> 9:00
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

        // Весь день: дата занимает все колонки (на месте времени).
        // Обычный режим: дата в первой колонке, время справа.
        var span = isAllDay ? 3 : 1;
        Grid.SetColumnSpan(StartDatePicker, span);
        Grid.SetColumnSpan(EndDatePicker, span);

        if (!isAllDay)
        {
            StartTimeInput.Text = FormatTime(_savedStartTime);
            EndTimeInput.Text   = FormatTime(_savedEndTime);
        }
    }

    private void TrySave(int? existingId)
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

        Close(new EventDialogResult(candidate));
    }

    private void ShowError(string message)
    {
        var pos = Position;            // баннер увеличит высоту — держим позицию
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
