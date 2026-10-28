using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Не дает окну с ФИКСИРОВАННЫМИ габаритами вылезти за рабочую область экрана.
///
/// Диалоги стандарта Wide (800 × 800, см. Assets/Styles/WindowMetrics.axaml) заданы
/// жестко: Height = MinHeight = MaxHeight и CanResize="False". На зуме приложения эти
/// габариты умножаются (<see cref="WindowZoom"/>): 800 → 1000 при 125%, 1200 при 150%.
/// На ноутбучном экране такое окно уходит нижним краем за предел, а уменьшить его
/// пользователь не может — ресайз запрещен. Здесь окно ужимается до рабочей области
/// (без панели задач) и центрируется относительно главного окна; содержимое добирает
/// внутренний скролл.
///
/// Ставится атрибутом в разметке окна: b:DialogFit.LimitToScreen="True".
/// Окна с авто-высотой (SizeToContent) в этом не нуждаются — их высота и так идет за
/// содержимым, а не за числом из разметки.
/// </summary>
public static class DialogFit
{
    // Запас от краев рабочей области, чтобы окно не липло к ним вплотную.
    private const double EdgeGap = 48;

    public static readonly AttachedProperty<bool> LimitToScreenProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("LimitToScreen", typeof(DialogFit));

    public static bool GetLimitToScreen(Window w)         => w.GetValue(LimitToScreenProperty);
    public static void SetLimitToScreen(Window w, bool v) => w.SetValue(LimitToScreenProperty, v);

    static DialogFit()
    {
        LimitToScreenProperty.Changed.AddClassHandler<Window>((w, e) =>
        {
            // Только на Opened: до показа окно не привязано к экрану, и ScreenFromWindow
            // не знает, на каком мониторе оно окажется. К этому моменту WindowZoom уже
            // домножил габариты на зум (он это делает на Initialized) — ужимаем итог.
            if (e.NewValue is true) w.Opened += (_, _) => Apply(w);
        });
    }

    private static void Apply(Window w)
    {
        var main = WindowPlacement.FindMainWindow(w) ?? w;
        var screen = w.Screens.ScreenFromWindow(main) ?? w.Screens.ScreenFromPoint(main.Position) ?? w.Screens.Primary;
        if (screen is null) return;

        // WorkingArea — в физических пикселях, габариты окна — в логических.
        var area   = screen.WorkingArea;
        var scale  = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var maxDip = new Size(area.Width / scale - EdgeGap, area.Height / scale - EdgeGap);

        var shrunk = Clamp(w, Layoutable.HeightProperty, Layoutable.MinHeightProperty,
                           Layoutable.MaxHeightProperty, maxDip.Height)
                   | Clamp(w, Layoutable.WidthProperty, Layoutable.MinWidthProperty,
                           Layoutable.MaxWidthProperty, maxDip.Width);

        // Окончательный размер попадет в отложенную коррекцию WindowPlacement. Сразу
        // после сжатия тоже используем главный центр, а не центр другого монитора.
        if (!shrunk) return;
        if (WindowPlacement.Center(w)) return;
        var size = new PixelSize((int)(w.Width * scale), (int)(w.Height * scale));
        w.Position = new PixelPoint(area.X + (area.Width  - size.Width)  / 2,
                                    area.Y + (area.Height - size.Height) / 2);
    }

    // Ужимает габарит до предела. MinHeight/MinWidth снимаются первыми: у окон Wide они
    // равны Height/Width, и без этого окно не сожмется.
    private static bool Clamp(Window w, StyledProperty<double> size,
                              StyledProperty<double> min, StyledProperty<double> max,
                              double limit)
    {
        var current = w.GetValue(size);
        if (!double.IsFinite(current) || current <= limit) return false;

        w.SetValue(min, Math.Min(w.GetValue(min), limit));
        w.SetValue(max, limit);
        w.SetValue(size, limit);
        return true;
    }
}
