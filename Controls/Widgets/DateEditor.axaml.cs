using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Редактор свойства типа «дата». Ручной ввод (с подсказкой) + выпадашка с
/// раздельными «Начало»/«Конец» (дата + время) и переключателем «Весь день» —
/// так явно задаются все формы: день, диапазон дней, дата+время, диапазон с временем.
/// Хранит/отдает значение в каноне <see cref="DateValue"/> через <see cref="Storage"/>.
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
            HideError();
            Pop.IsOpen = true;
        };

        StartDate.DateChanged += (_, _) =>
        {
            if (_suppress) return;
            // Конец не задан или раньше начала — подтягиваем к началу.
            if (StartDate.Date is { } s &&
                (EndDate.Date is null || EndDate.Date < s))
            {
                _suppress = true;
                EndDate.Date = s;
                _suppress = false;
            }
            RecomputeFromUi();
        };
        EndDate.DateChanged += (_, _) => { if (!_suppress) RecomputeFromUi(); };
        StartTime.TimeChanged       += (_, _) => { if (!_suppress) RecomputeFromUi(); };
        EndTime.TimeChanged         += (_, _) => { if (!_suppress) RecomputeFromUi(); };
        AllDayCheck.IsCheckedChanged += (_, _) =>
        {
            UpdateTimeVisibility();
            if (!_suppress) RecomputeFromUi();
        };

        ClearBtn.Click += (_, _) =>
        {
            HideError();
            SetValue(null, raise: true);
            Pop.IsOpen = false;
        };
        // «Готово» применяет текущее состояние выпадашки (на случай, если изменение еще не
        // зафиксировалось через RecomputeFromUi — например, фокус не ушел из поля), затем закрывает.
        // RecomputeFromUi идемпотентна (Changed поднимается только при реальной смене значения).
        // Как в диалоге события: при ошибке валидации не закрываем (баннер виден).
        DoneBtn.Click += (_, _) =>
        {
            RecomputeFromUi();
            if (!NoticeBanner.IsVisible) Pop.IsOpen = false;
        };

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
            StartDate.ClosePopup();
            EndDate.ClosePopup();
        };

        UpdateTimeVisibility();
    }

    // Вложенный календарь даты и список времени — overlay-попапы главного окна, поэтому
    // клик по дню/времени доходит до оконных обработчиков и НЕ виден как «внутри поля».
    // Пока любой из них открыт, внешнюю выпадашку не закрываем.
    private bool AnyChildPopupOpen =>
        StartDate.IsPopupOpen || EndDate.IsPopupOpen || StartTime.IsPopupOpen || EndTime.IsPopupOpen;

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (AnyChildPopupOpen) return;
        CloseDropdown();
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Pop.IsOpen) return;
        if (AnyChildPopupOpen) return;
        // Клик внутри любого попапа (сама выпадашка + вложенные календарь/список времени)
        // не должен ее закрывать. Вложенные попапы — overlay в главном окне и в визуальном
        // дереве отделены от this, поэтому узнаем их по предку PopupRoot/OverlayPopupHost.
        if (e.Source is Visual v &&
            v.GetSelfAndVisualAncestors().Any(a => a.GetType().Name is "PopupRoot" or "OverlayPopupHost"))
            return;
        CloseDropdown();
    }

    private void CloseDropdown() => Pop.IsOpen = false;

    /// <summary>Каноническое значение (ISO) для db_field_values. null/"" — дата не задана.</summary>
    public string? Storage
    {
        get => _value?.ToStorage();
        set => SetValue(DateValue.Parse(value), raise: false);
    }

    /// <summary>
    /// Плоский режим: убирает собственную рамку/фон бокса (timeField), чтобы редактор даты
    /// встраивался в карточку свойства (RecordPageView) без «бокса в боксе». Иконка-календарь
    /// и поле ввода остаются; левый отступ ввода обнуляется для выравнивания с другими значениями.
    /// </summary>
    public bool Flat
    {
        set
        {
            if (!value) return;
            Box.Classes.Remove("timeField");
            Box.Background      = Avalonia.Media.Brushes.Transparent;
            Box.BorderThickness = default;
            Box.Padding         = default;
            // Высота — от того, во что встроен редактор (строка карточки или ячейка таблицы),
            // а не собственные 34: иначе карточка «Дата» на 2px выше остальных и сдвигает вниз
            // все, что под ней. Та же строка есть в FileEditor.Flat.
            Box.Height          = double.NaN;
            Input.Padding       = default;
            // Поле растянуто на всю карточку — выпадашку привязываем к ПРАВОМУ краю (к иконке
            // календаря), чтобы она появлялась рядом с кнопкой даты, а не у левого края.
            Pop.Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight;
        }
    }

    /// <summary>Левый отступ значения. В ячейке таблицы = 6px, чтобы дата выравнивалась с
    /// текстом остальных колонок; на карточке записи — 0 (отступ задает карточка).</summary>
    public double ContentLeftPadding
    {
        set => Input.Padding = new Avalonia.Thickness(value, 0, 0, 0);
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
        if (StartDate.Date is not { } sDate) return;
        var eDate = EndDate.Date ?? sDate;
        if (eDate < sDate) eDate = sDate;

        var allDay = AllDayCheck.IsChecked == true;
        var v = allDay
            ? new DateValue(sDate.Date, eDate.Date + DateValue.EndOfDay, true)
            : new DateValue(sDate.Date + StartTime.Time, eDate.Date + EndTime.Time, false);

        // Как в диалоге события: конец должен быть позже начала. Некорректный диапазон
        // не коммитим — показываем баннер, пока пользователь не поправит время.
        if (!allDay && v.End <= v.Start)
        {
            ShowError("Время конца должно быть позже начала");
            return;
        }
        HideError();

        // Поднимаем Changed только при реальной смене значения — чтобы повторный вызов
        // (например, из «Готово» поверх уже зафиксированного live-выбора) не плодил лишних сохранений.
        var changed = _value?.ToStorage() != v.ToStorage();
        _value     = v;
        Input.Text = v.ToDisplay();
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowError(string message) => NoticeBanner.Show(message);

    private void HideError() => NoticeBanner.Hide();

    // Разложить значение по элементам выпадашки (без поднятия Changed).
    private void PushToUi(DateValue? v)
    {
        _suppress = true;
        try
        {
            AllDayCheck.IsChecked  = v?.AllDay ?? false;
            StartDate.Date = v?.Start.Date;
            EndDate.Date   = v?.End.Date;

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
