using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartBase.Utils;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Единая визуальная толщина линий (~1 логический пиксель) при любом зуме приложения
/// (<see cref="Scaling"/>, LayoutTransform 0.7–1.3) и любом DPI монитора (FullHD 1x /
/// 2K 1.25–1.5x / Retina 2x). Проблема: layout rounding в Avalonia работает в
/// ПРЕ-трансформ координатах и знает только RenderScaling окна — 1 DIP × zoom × DPI
/// даёт дробные физические пиксели, и линии рендерятся то в 1px, то в 2px, то размыто.
///
/// Решение: в Resources каждого окна пишутся динамические токены толщины,
/// скомпенсированные так, чтобы после зума линия занимала целое число физических
/// пикселей, визуально равное 1 DIP:
///   physTarget = max(1, round(RenderScaling))   — целевая толщина в физ. пикселях;
///   t    = physTarget / RenderScaling / zoom    — для контента под зумом;
///   tPop = physTarget / RenderScaling           — для контента ВНЕ зума (legacy).
/// Зум применяется КО ВСЕМ окнам (MainWindow — ScaleHost, остальные — WindowZoom) и ко
/// всем попапам (LayoutTransformControl-обёртки с ресурсом "AppZoomTransform"), поэтому
/// токен t действует везде; tPop оставлен на случай контента вне трансформа.
/// Токены (DynamicResource): SeparatorThickness, SeparatorThicknessPopup (double);
/// BorderLine, BorderLineFocus (=2×), BorderLineTop/Bottom/Left/Right, BorderLinePopup
/// (Thickness); LineUseLayoutRounding (bool) — при zoom≠1 layout rounding пере-округлил
/// бы компенсированную толщину, поэтому линии-потребители отключают его динамически.
/// Дефолты токенов (1/2/True) объявлены в Assets/Styles/Controls.axaml.
///
/// Подключение: глобальный стиль Window ставит IsManaged=True (пересчёт на Opened,
/// ScalingChanged — переезд между мониторами, Scaling.ScaleChanged); MainWindow
/// дополнительно помечен FollowsAppZoom=True — только его контент масштабируется зумом.
/// </summary>
public static class LineMetrics
{
    /// <summary>Контент окна масштабируется зумом приложения (только MainWindow).</summary>
    public static readonly AttachedProperty<bool> FollowsAppZoomProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("FollowsAppZoom", typeof(LineMetrics));

    /// <summary>Окно получает динамические токены толщины. Ставится стилем Window.</summary>
    public static readonly AttachedProperty<bool> IsManagedProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("IsManaged", typeof(LineMetrics));

    /// <summary>Мост к RenderOptions.EdgeMode=Aliased для СТИЛЕЙ: у RenderOptions нет
    /// публичных полей AttachedProperty, и сеттер стиля не может адресовать EdgeMode
    /// напрямую (в атрибутах элементов — можно). Ставить только на чисто-линейные
    /// элементы: на скруглённых рамках выключенное сглаживание даёт зазубрины.</summary>
    public static readonly AttachedProperty<bool> AliasedProperty =
        AvaloniaProperty.RegisterAttached<Visual, bool>("Aliased", typeof(LineMetrics));

    public static bool GetFollowsAppZoom(Window w)          => w.GetValue(FollowsAppZoomProperty);
    public static void SetFollowsAppZoom(Window w, bool v)  => w.SetValue(FollowsAppZoomProperty, v);
    public static bool GetIsManaged(Window w)               => w.GetValue(IsManagedProperty);
    public static void SetIsManaged(Window w, bool v)       => w.SetValue(IsManagedProperty, v);
    public static bool GetAliased(Visual v)                 => v.GetValue(AliasedProperty);
    public static void SetAliased(Visual v, bool value)     => v.SetValue(AliasedProperty, value);

    /// <summary>Токены какого-то окна пересчитаны (зум/монитор). Для C#-построенных
    /// потребителей, которым мало DynamicResource (пересборка разметки, снап колонок).</summary>
    public static event Action? Changed;

    /// <summary>Толщина линии контента главного окна (под зумом) — для мест без доступа
    /// к визуалу (конвертеры) и разметки, пересобираемой при Refresh (календарь).
    /// Живое значение при наличии визуала — <see cref="ThicknessFor"/>.</summary>
    public static double MainThickness { get; private set; } = 1.0;

    static LineMetrics()
    {
        IsManagedProperty.Changed.AddClassHandler<Window>((w, e) =>
        {
            if (e.NewValue is true) Attach(w);
        });
        // FollowsAppZoom может примениться ПОЗЖЕ IsManaged (локальное свойство в XAML
        // против сеттера стиля) — пересчитать с учётом зума.
        FollowsAppZoomProperty.Changed.AddClassHandler<Window>((w, _) => Apply(w));

        AliasedProperty.Changed.AddClassHandler<Visual>((v, e) =>
        {
            if (e.NewValue is true) RenderOptions.SetEdgeMode(v, EdgeMode.Aliased);
        });
    }

    // ── Расчёт (общий для ресурсов и C#-кода) ─────────────────────────────────

    /// <summary>Целевая толщина линии в физических пикселях для данного DPI (≈1 DIP).</summary>
    public static double PhysTarget(double renderScaling) =>
        Math.Max(1.0, Math.Round(renderScaling));

    /// <summary>Толщина линии в локальных DIP для визуала (учитывает DPI его TopLevel и
    /// зум приложения — зумируются все окна и попапы).</summary>
    public static double ThicknessFor(Visual v)
    {
        var (rs, zoom) = ScalesFor(v);
        return PhysTarget(rs) / rs / zoom;
    }

    /// <summary>Итоговый масштаб визуала (RenderScaling × зум) — перевод DIP в физические
    /// пиксели, например для снапа ширин колонок к физической сетке.</summary>
    public static double EffectiveScale(Visual v)
    {
        var (rs, zoom) = ScalesFor(v);
        return rs * zoom;
    }

    private static (double Rs, double Zoom) ScalesFor(Visual v)
    {
        var tl = TopLevel.GetTopLevel(v);
        var rs = tl?.RenderScaling ?? 1.0;
        // Зум приложения действует везде: главное окно (ScaleHost), отдельные окна
        // (WindowZoom) и попапы (обёртки AppZoomTransform).
        return (rs, Scaling.Current);
    }

    // ── Подключение окна ──────────────────────────────────────────────────────

    private static void Attach(Window w)
    {
        Action<double> onZoom = _ => Apply(w);
        Scaling.ScaleChanged += onZoom;                    // окна без зума пересчитают то же — дёшево
        w.Opened         += (_, _) => Apply(w);
        w.ScalingChanged += (_, _) => Apply(w);            // переезд между мониторами с разным DPI
        w.Closed         += (_, _) => Scaling.ScaleChanged -= onZoom;
        Apply(w);
    }

    private static void Apply(Window w)
    {
        var rs   = w.RenderScaling;
        var zoom = Scaling.Current;   // зумируются ВСЕ окна (MainWindow — ScaleHost, остальные — WindowZoom)
        var phys = PhysTarget(rs);
        var t    = phys / rs / zoom;
        var tPop = phys / rs;

        var r = w.Resources;
        r["SeparatorThickness"]      = t;
        r["SeparatorThicknessPopup"] = tPop;
        r["BorderLine"]              = new Thickness(t);
        r["BorderLineFocus"]         = new Thickness(2 * t);
        r["BorderLineTop"]           = new Thickness(0, t, 0, 0);
        r["BorderLineBottom"]        = new Thickness(0, 0, 0, t);
        r["BorderLineLeft"]          = new Thickness(t, 0, 0, 0);
        r["BorderLineRight"]         = new Thickness(0, 0, t, 0);
        r["BorderLineBottomRight"]   = new Thickness(0, 0, t, t);
        r["BorderLineTopLeft"]       = new Thickness(t, t, 0, 0);
        r["BorderLinePopup"]         = new Thickness(tPop);
        r["LineUseLayoutRounding"]   = Math.Abs(zoom - 1.0) < 0.001;

        if (w.GetValue(FollowsAppZoomProperty)) MainThickness = t;

        Changed?.Invoke();
    }
}
