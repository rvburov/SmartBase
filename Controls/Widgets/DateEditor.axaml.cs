using Avalonia.Controls;
using Avalonia.Input;
using DataBase.Core.Models;

namespace DataBase.Controls.Widgets;

/// <summary>
/// Редактор свойства типа «дата». Ручной ввод (с подсказкой) + выпадашка с
/// месячным календарём, переключателем «Весь день» и полями времени начала/конца.
/// Хранит/отдаёт значение в каноне <see cref="DateValue"/> через <see cref="Storage"/>.
/// </summary>
public partial class DateEditor : UserControl
{
    private DateValue? _value;
    private bool       _suppress;

    /// <summary>Поднимается при любом изменении значения пользователем.</summary>
    public event EventHandler? Changed;

    public DateEditor()
    {
        InitializeComponent();

        StartTime.Time = new TimeSpan(9, 0, 0);
        EndTime.Time   = new TimeSpan(10, 0, 0);

        Arrow.Click += (_, _) =>
        {
            var wasOpen = Pop.IsOpen;
            if (wasOpen) { Pop.IsOpen = false; return; }
            PushToUi(_value);
            Pop.IsOpen = true;
        };

        Cal.SelectedDatesChanged    += (_, _) => { if (!_suppress) RecomputeFromUi(); };
        StartTime.TimeChanged       += (_, _) => { if (!_suppress) RecomputeFromUi(); };
        EndTime.TimeChanged         += (_, _) => { if (!_suppress) RecomputeFromUi(); };
        AllDayCheck.IsCheckedChanged += (_, _) =>
        {
            UpdateTimeVisibility();
            if (!_suppress) RecomputeFromUi();
        };

        ClearBtn.Click += (_, _) =>
        {
            SetValue(null, raise: true);
            Pop.IsOpen = false;
        };

        Input.KeyDown   += (_, e) => { if (e.Key == Key.Enter) CommitManual(); };
        Input.LostFocus += (_, _) => CommitManual();

        UpdateTimeVisibility();
    }

    /// <summary>Каноническое значение (ISO) для db_field_values. null/"" — дата не задана.</summary>
    public string? Storage
    {
        get => _value?.ToStorage();
        set => SetValue(DateValue.Parse(value), raise: false);
    }

    // ── Внутреннее ────────────────────────────────────────────────────────────

    private void SetValue(DateValue? v, bool raise)
    {
        _value     = v;
        Input.Text = v?.ToDisplay() ?? "";
        if (raise) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CommitManual()
    {
        var text = Input.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            if (_value is not null) SetValue(null, raise: true);
            return;
        }

        if (DateValue.TryParseDisplay(text, out var v))
        {
            SetValue(v, raise: true);
            PushToUi(v);
        }
        else
        {
            // невалидный ввод — откатываем к последнему корректному значению
            Input.Text = _value?.ToDisplay() ?? "";
        }
    }

    // Считать значение из календаря/чекбокса/времени и поднять Changed.
    private void RecomputeFromUi()
    {
        if (!TryGetSelectedRange(out var sDate, out var eDate)) return;

        var allDay = AllDayCheck.IsChecked == true;
        var v = allDay
            ? new DateValue(sDate, eDate + DateValue.EndOfDay, true)
            : new DateValue(sDate + StartTime.Time, eDate + EndTime.Time, false);

        _value     = v;
        Input.Text = v.ToDisplay();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Разложить значение в элементы выпадашки (без поднятия Changed).
    private void PushToUi(DateValue? v)
    {
        _suppress = true;
        try
        {
            AllDayCheck.IsChecked = v?.AllDay ?? false;

            if (!(v?.AllDay ?? true))
            {
                StartTime.Time = v?.Start.TimeOfDay ?? new TimeSpan(9, 0, 0);
                EndTime.Time   = v?.End.TimeOfDay   ?? new TimeSpan(10, 0, 0);
            }

            Cal.SelectedDates.Clear();
            if (v is { } val)
            {
                Cal.DisplayDate = val.Start.Date;
                for (var d = val.Start.Date; d <= val.End.Date; d = d.AddDays(1))
                    Cal.SelectedDates.Add(d);
            }
        }
        finally
        {
            _suppress = false;
        }

        UpdateTimeVisibility();
    }

    private bool TryGetSelectedRange(out DateTime start, out DateTime end)
    {
        if (Cal.SelectedDates.Count > 0)
        {
            start = Cal.SelectedDates.Min().Date;
            end   = Cal.SelectedDates.Max().Date;
            return true;
        }
        if (Cal.SelectedDate is { } d)
        {
            start = end = d.Date;
            return true;
        }
        start = end = default;
        return false;
    }

    private void UpdateTimeVisibility() =>
        TimeRow.IsVisible = AllDayCheck.IsChecked != true;
}
