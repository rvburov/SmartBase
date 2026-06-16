using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DataBase.Core.Models;

namespace DataBase.Controls.Widgets;

/// <summary>
/// Редактор свойства типа «дата». Ручной ввод (с подсказкой) + выпадашка с
/// раздельными «Начало»/«Конец» (дата + время) и переключателем «Весь день» —
/// так явно задаются все формы: день, диапазон дней, дата+время, диапазон с временем.
/// Хранит/отдаёт значение в каноне <see cref="DateValue"/> через <see cref="Storage"/>.
/// </summary>
public partial class DateEditor : UserControl
{
    private DateValue? _value;
    private bool       _suppress;
    private Window?    _window;

    /// <summary>Поднимается при любом изменении значения пользователем.</summary>
    public event EventHandler? Changed;

    public DateEditor()
    {
        InitializeComponent();

        StartTime.Time = new TimeSpan(9, 0, 0);
        EndTime.Time   = new TimeSpan(10, 0, 0);

        Arrow.Click += (_, _) =>
        {
            if (Pop.IsOpen) { Pop.IsOpen = false; return; }
            PushToUi(_value);
            Pop.IsOpen = true;
        };

        StartDate.SelectedDateChanged += (_, _) =>
        {
            if (_suppress) return;
            // Конец не задан или раньше начала — подтягиваем к началу.
            if (StartDate.SelectedDate is { } s &&
                (EndDate.SelectedDate is null || EndDate.SelectedDate < s))
            {
                _suppress = true;
                EndDate.SelectedDate = s;
                _suppress = false;
            }
            RecomputeFromUi();
        };
        EndDate.SelectedDateChanged += (_, _) => { if (!_suppress) RecomputeFromUi(); };
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
        DoneBtn.Click += (_, _) => Pop.IsOpen = false;

        Input.KeyDown   += (_, e) => { if (e.Key == Key.Enter) CommitManual(); };
        Input.LostFocus += (_, _) => CommitManual();

        // Закрываем выпадашку, когда:
        //  • окно теряет фокус (переход в другую программу) — Deactivated;
        //  • в самом окне жмут где угодно вне поля даты (переключение блоков/видов,
        //    клик по другой ячейке) — PointerPressed на окне. Клики во вложенных
        //    выпадашках (календарь дат, список времени) — отдельные popup-окна,
        //    до главного окна не доходят, поэтому их этот обработчик не заденет;
        //  • контрол убирают из дерева (конец редактирования ячейки) — Detached.
        // (light-dismiss выключен, чтобы вложенный календарь не закрывал попап.)
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is null) return;
            _window.Deactivated += OnWindowDeactivated;
            _window.AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CloseDropdown();
            if (_window is null) return;
            _window.Deactivated -= OnWindowDeactivated;
            _window.RemoveHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed);
            _window = null;
        };
        Pop.Closed += (_, _) =>
        {
            StartTime.ClosePopup();
            EndTime.ClosePopup();
            StartDate.IsDropDownOpen = false;
            EndDate.IsDropDownOpen   = false;
        };

        UpdateTimeVisibility();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => CloseDropdown();

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Pop.IsOpen) return;
        // Клик по самому полю (стрелка/поле ввода) — не закрываем, это обработается отдельно.
        if (e.Source is Visual v && this.IsVisualAncestorOf(v)) return;
        CloseDropdown();
    }

    private void CloseDropdown() => Pop.IsOpen = false;

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

    // Считать значение из «Начало»/«Конец»/времени и поднять Changed.
    private void RecomputeFromUi()
    {
        if (StartDate.SelectedDate is not { } sDate) return;
        var eDate = EndDate.SelectedDate ?? sDate;
        if (eDate < sDate) eDate = sDate;

        var allDay = AllDayCheck.IsChecked == true;
        var v = allDay
            ? new DateValue(sDate.Date, eDate.Date + DateValue.EndOfDay, true)
            : new DateValue(sDate.Date + StartTime.Time, eDate.Date + EndTime.Time, false);

        _value     = v;
        Input.Text = v.ToDisplay();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Разложить значение по элементам выпадашки (без поднятия Changed).
    private void PushToUi(DateValue? v)
    {
        _suppress = true;
        try
        {
            AllDayCheck.IsChecked  = v?.AllDay ?? false;
            StartDate.SelectedDate = v?.Start.Date;
            EndDate.SelectedDate   = v?.End.Date;

            if (!(v?.AllDay ?? true))
            {
                StartTime.Time = v?.Start.TimeOfDay ?? new TimeSpan(9, 0, 0);
                EndTime.Time   = v?.End.TimeOfDay   ?? new TimeSpan(10, 0, 0);
            }
        }
        finally
        {
            _suppress = false;
        }

        UpdateTimeVisibility();
    }

    // «Весь день» — прячем поля времени, дата занимает всю ширину строки.
    private void UpdateTimeVisibility()
    {
        var allDay = AllDayCheck.IsChecked == true;
        StartTime.IsVisible = !allDay;
        EndTime.IsVisible   = !allDay;
        Grid.SetColumnSpan(StartDate, allDay ? 3 : 1);
        Grid.SetColumnSpan(EndDate,   allDay ? 3 : 1);
    }
}
