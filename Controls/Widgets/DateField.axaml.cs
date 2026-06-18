using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace DataBase.Controls.Widgets;

/// <summary>
/// Поле ввода даты: ручной ввод (дд.мм.гггг) + выпадающий <see cref="Calendar"/>.
/// Сделано по образцу <see cref="TimeField"/> — со своим <c>Popup</c>. Calendar
/// создаётся ЗАНОВО на каждое открытие: один и тот же экземпляр сохраняет «зажатое»
/// состояние указателя между открытиями и начинает выбирать дату по наведению.
/// </summary>
public partial class DateField : UserControl
{
    private DateTime? _date;

    /// <summary>Поднимается, когда пользователь меняет дату (ввод или выбор в календаре).</summary>
    public event EventHandler? DateChanged;

    public DateField()
    {
        InitializeComponent();

        Arrow.Click += (_, _) =>
        {
            var wasOpen = Pop.IsOpen;
            Pop.IsOpen = false;
            if (wasOpen) return;
            OpenCalendar();
        };

        Input.KeyDown   += (_, e) => { if (e.Key == Key.Enter) CommitManual(); };
        Input.LostFocus += (_, _) => CommitManual();
    }

    /// <summary>Текущая дата. Установка не поднимает <see cref="DateChanged"/>.</summary>
    public DateTime? Date
    {
        get => _date;
        set
        {
            _date      = value?.Date;
            Input.Text = _date?.ToString("dd.MM.yyyy") ?? "";
        }
    }

    /// <summary>Открыт ли выпадающий календарь (для внешней выпадашки — не закрываться, пока открыт).</summary>
    public bool IsPopupOpen => Pop.IsOpen;

    public void ClosePopup() => Pop.IsOpen = false;

    private void OpenCalendar()
    {
        // Свежий Calendar — без залипшего состояния от прошлого выбора.
        var cal = new Avalonia.Controls.Calendar
        {
            SelectionMode = CalendarSelectionMode.SingleDate,
            DisplayDate   = _date ?? DateTime.Today,
        };
        if (_date is { } d0) cal.SelectedDate = d0;

        // Подписываемся ПОСЛЕ установки даты, чтобы программная установка не сработала как выбор.
        cal.SelectedDatesChanged += (_, _) =>
        {
            if (cal.SelectedDate is { } d)
            {
                _date      = d.Date;
                Input.Text = d.ToString("dd.MM.yyyy");
                DateChanged?.Invoke(this, EventArgs.Empty);
            }
            // Отложенно — дать Calendar завершить жест указателя перед закрытием.
            Dispatcher.UIThread.Post(() => Pop.IsOpen = false, DispatcherPriority.Background);
        };

        PopHost.Child = cal;
        Pop.IsOpen    = true;
    }

    private void CommitManual()
    {
        var text = Input.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            if (_date is not null) { _date = null; DateChanged?.Invoke(this, EventArgs.Empty); }
            return;
        }

        if (DateTime.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var d))
        {
            _date = d.Date;
            DateChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            // невалидный ввод — откат к последнему корректному значению
            Input.Text = _date?.ToString("dd.MM.yyyy") ?? "";
        }
    }
}
