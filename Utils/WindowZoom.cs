using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DataBase.Utils;

/// <summary>
/// Масштабирует ОТДЕЛЬНЫЕ окна (диалоги, окна расширений, сплэш, установщик) под общий
/// зум приложения (<see cref="Scaling"/>): содержимое окна оборачивается в
/// LayoutTransformControl со ScaleTransform, а конечные габариты окна (Width/Height и
/// Min/Max) умножаются на зум — SizeToContent-высота пересчитывается сама от уже
/// масштабированного контента. Главное окно исключено: у него собственный ScaleHost
/// (помечено <see cref="LineMetrics.FollowsAppZoomProperty"/>).
///
/// Подключение — глобальным стилем Window в Controls.axaml (Enabled=True), как у
/// LineMetrics.IsManaged. Немодальные окна (расширения) живут при смене зума —
/// пересчёт по Scaling.ScaleChanged, отписка на Closed.
/// </summary>
public static class WindowZoom
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("Enabled", typeof(WindowZoom));

    // Зум, уже применённый к габаритам окна (0 = ещё не применялся): при пересчёте
    // размеры умножаются на отношение нового зума к применённому.
    private static readonly AttachedProperty<double> AppliedProperty =
        AvaloniaProperty.RegisterAttached<Window, double>("Applied", typeof(WindowZoom));

    public static bool GetEnabled(Window w)          => w.GetValue(EnabledProperty);
    public static void SetEnabled(Window w, bool v)  => w.SetValue(EnabledProperty, v);

    static WindowZoom()
    {
        EnabledProperty.Changed.AddClassHandler<Window>((w, e) =>
        {
            if (e.NewValue is true) Attach(w);
        });
    }

    private static void Attach(Window w)
    {
        // MainWindow (FollowsAppZoom) проверяется в Apply: сеттер стиля может сработать
        // РАНЬШЕ локального атрибута FollowsAppZoom из XAML — на момент Attach флаг
        // ещё не выставлен, а на момент Initialized/Opened — уже да.
        if (LineMetrics.GetFollowsAppZoom(w)) return;

        Action<double> onZoom = _ => Apply(w);
        Scaling.ScaleChanged += onZoom;
        // До показа окна: габариты должны вырасти ДО позиционирования (CenterOwner/Screen),
        // иначе окно центрируется по немасштабированному размеру. Opened — страховка.
        if (w.IsInitialized) Apply(w);
        else w.Initialized += (_, _) => Apply(w);
        w.Opened += (_, _) => Apply(w);
        w.Closed += (_, _) => Scaling.ScaleChanged -= onZoom;
    }

    private static void Apply(Window w)
    {
        if (LineMetrics.GetFollowsAppZoom(w)) return;   // MainWindow — свой ScaleHost
        if (w.Content is not Control content) return;

        // Обёртка контента — один раз; дальше меняется только трансформ.
        if (content is not LayoutTransformControl { Tag: nameof(WindowZoom) } host)
        {
            w.Content = null;   // отцепить от окна ДО пере-parent'а (иначе «already has a parent»)
            host = new LayoutTransformControl { Tag = nameof(WindowZoom), Child = content };
            w.Content = host;
        }

        var zoom = Scaling.Current;
        host.LayoutTransform = new ScaleTransform(zoom, zoom);

        // Габариты окна — на отношение зумов (первый раз applied=0 → от исходных из XAML).
        var applied = w.GetValue(AppliedProperty);
        var ratio   = zoom / (applied > 0 ? applied : 1.0);
        if (Math.Abs(ratio - 1.0) > 0.001)
        {
            ScaleSize(w, Layoutable.WidthProperty,     ratio);
            ScaleSize(w, Layoutable.HeightProperty,    ratio);
            ScaleSize(w, Layoutable.MinWidthProperty,  ratio);
            ScaleSize(w, Layoutable.MinHeightProperty, ratio);
            ScaleSize(w, Layoutable.MaxWidthProperty,  ratio);
            ScaleSize(w, Layoutable.MaxHeightProperty, ratio);
        }
        w.SetValue(AppliedProperty, zoom);
    }

    // Умножает габарит, только если он задан конечным положительным числом
    // (NaN = авторазмер, Infinity = без ограничения — не трогаем).
    private static void ScaleSize(Window w, StyledProperty<double> prop, double ratio)
    {
        var v = w.GetValue(prop);
        if (double.IsFinite(v) && v > 0) w.SetValue(prop, v * ratio);
    }

    /// <summary>Оборачивает контент попапа/флайаута, созданного в коде, в
    /// LayoutTransformControl с живым зумом приложения (ресурс "AppZoomTransform").
    /// Для XAML-попапов вместо этого ставится Popup.InheritsTransform="True".</summary>
    public static Control WrapPopupContent(Control content)
    {
        var host = new LayoutTransformControl { Child = content };
        host.Bind(LayoutTransformControl.LayoutTransformProperty,
                  host.GetResourceObservable("AppZoomTransform"));
        return host;
    }
}
