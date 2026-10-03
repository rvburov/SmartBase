using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

public partial class NavbarCalendar : UserControl
{
    // Показываем только виды, не скрытые в «Оформлении». НАРУЖУ при этом отдаем и
    // принимаем ГЛОБАЛЬНЫЙ номер вида (позиция в ViewCatalog): на него завязаны
    // CalendarControllerView.SwitchView и сохраненный calendarView, и скрытие пунктов не
    // должно менять смысл этих чисел. Переключатель живет ключами каталога; перевод
    // «ключ ↔ глобальный номер» держится здесь (Index, KeyOf).
    private readonly ViewSwitcher _views;

    public event EventHandler? TodayClicked;
    public event EventHandler? PrevClicked;
    public event EventHandler? NextClicked;
    public event EventHandler? AddClicked;
    public event EventHandler<int>? ViewChanged;
    public event EventHandler<string>? SearchChanged;

    public NavbarCalendar()
    {
        InitializeComponent();

        // Вид по умолчанию — стартовый (calendarView): на него раздел уходит со скрытого вида.
        // Настоящий стартовый вид ставит контроллер (SetView).
        _views = new ViewSwitcher(ViewSelector, ViewCatalog.ScopeCalendar,
                                  () => KeyOf(AppSettings.LoadCalendarView()) ?? "");
        _views.Changed += key => ViewChanged?.Invoke(this, Index(key));

        TodayBtn.Click += (_, _) => TodayClicked?.Invoke(this, EventArgs.Empty);
        PrevBtn.Click  += (_, _) => PrevClicked?.Invoke(this, EventArgs.Empty);
        NextBtn.Click  += (_, _) => NextClicked?.Invoke(this, EventArgs.Empty);
        AddBtn.Click   += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);

        SearchBox.TextChanged += (_, _) =>
            SearchChanged?.Invoke(this, SearchBox.Text ?? "");
    }

    public void SetTitle(string text, Avalonia.Media.IBrush? color = null)
    {
        TitleLabel.Text       = text;
        TitleLabel.Foreground = color ?? (IBrush)Application.Current!.FindResource("BrushPrimaryText")!;
    }

    /// <summary>
    /// Показать вид по ГЛОБАЛЬНОМУ номеру, без ViewChanged: вид открывает сам раздел. Скрытый
    /// не показывается — вместо него вид по умолчанию, а скрыт и тот — первый видимый.
    /// Возвращает номер показанного: его раздел и открывает.
    /// </summary>
    public int SetView(int idx) => Index(_views.Show(KeyOf(idx)));

    /// <summary>Есть ли вид в переключателе — не скрыт ли он в «Отображаемых видах».</summary>
    public bool HasView(int idx) => KeyOf(idx) is { } key && _views.Has(key);

    /// <summary>Перечитать «Отображаемые виды» (их сменили в настройках). Скрыли открытый
    /// вид — переключатель уходит на вид по умолчанию и сообщает об этом ViewChanged.</summary>
    public void ApplyViewVisibility() => _views.Refresh();

    /// <summary>Глобальный номер выбранного вида (позиция в ViewCatalog).</summary>
    public int CurrentViewIndex => Index(_views.Current);

    private static int Index(string key) =>
        Math.Max(0, ViewCatalog.IndexOf(ViewCatalog.ScopeCalendar, key));

    private static string? KeyOf(int idx) =>
        idx >= 0 && idx < ViewCatalog.Calendar.Count ? ViewCatalog.Calendar[idx].Key : null;
}
