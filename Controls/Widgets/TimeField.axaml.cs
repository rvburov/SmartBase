using Avalonia.Controls;
using SmartBase.Utils;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Поле ввода времени: ручной ввод + выпадающий список с шагом 15 минут.
/// Вынесено из RecordDialog, чтобы переиспользоваться в DateEditor.
/// </summary>
public partial class TimeField : UserControl
{
    private TimeSpan _time = new(9, 0, 0);

    /// <summary>Поднимается, когда пользователь меняет время (ввод или выбор из списка).</summary>
    public event EventHandler? TimeChanged;

    public TimeField()
    {
        InitializeComponent();

        List.ItemsSource = TimeText.Options;
        Input.Text       = TimeText.Format(_time);

        Input.TextChanged += (_, _) =>
        {
            if (TimeText.TryParse(Input.Text, out var t) && t != _time)
            {
                _time = t;
                TimeChanged?.Invoke(this, EventArgs.Empty);
            }
        };

        Arrow.Click += (_, _) =>
        {
            var wasOpen = Pop.IsOpen;
            Pop.IsOpen = false;
            if (wasOpen) return;
            SelectInList(_time);
            Pop.IsOpen = true;
            // SelectInList мог ничего не подобрать (произвольное время в поле) —
            // тогда SelectedItem пуст, и прокручивать не к чему.
            if (List.SelectedItem is { } selected) List.ScrollIntoView(selected);
        };

        List.SelectionChanged += (_, _) =>
        {
            if (!Pop.IsOpen) return;
            if (List.SelectedItem is string s && TimeText.TryParse(s, out var t))
            {
                _time      = t;
                Input.Text = s;
                TimeChanged?.Invoke(this, EventArgs.Empty);
            }
            Pop.IsOpen = false;
        };
    }

    /// <summary>Текущее время. Установка не поднимает <see cref="TimeChanged"/>.</summary>
    public TimeSpan Time
    {
        get => TimeText.TryParse(Input.Text, out var t) ? t : _time;
        set
        {
            _time      = value;
            Input.Text = TimeText.Format(value);
        }
    }

    /// <summary>
    /// Время, если поле читается; <c>null</c> — пусто или мусор. Нужно там, где пустое
    /// время значит «весь день» (строка быстрого добавления задачи).
    /// </summary>
    public TimeSpan? TimeOrNull => TimeText.TryParse(Input.Text, out var t) ? t : null;

    /// <summary>Открыт ли выпадающий список (для внешней выпадашки — не закрываться, пока открыт).</summary>
    public bool IsPopupOpen => Pop.IsOpen;

    public void ClosePopup() => Pop.IsOpen = false;

    private void SelectInList(TimeSpan time)
    {
        var idx = TimeText.Options.ToList().IndexOf(TimeText.Format(time));
        if (idx < 0)
        {
            var quarter = (time.Hours * 60 + time.Minutes) / 15;
            idx = Math.Clamp(quarter, 0, TimeText.Options.Count - 1);
        }
        List.SelectedIndex = idx;
    }
}
