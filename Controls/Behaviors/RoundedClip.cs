using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Срезает содержимое контрола по его СКРУГЛЕННЫМ углам. Нужен там, где внутри скругленной
/// рамки живет полоса прокрутки: полоса прямоугольная и без клипа ее углы вылезают за
/// скругление — закрашивают уголок за рамкой и перерезают саму рамку (заметнее всего на
/// синей рамке фокуса многострочного поля).
///
/// <c>ClipToBounds</c> тут не годится: он режет по ПРЯМОУГОЛЬНИКУ Bounds, скругление ему
/// неизвестно. Скругленный клип умеет только <see cref="Border"/> со своим CornerRadius
/// (так сделаны карточки списков — фильтры, «Мультисписок», «Связь»), но в шаблоне TextBox
/// содержимое лежит РЯДОМ с Border рамки, а не внутри него, поэтому клип вешается на сам
/// контрол — через стиль:
/// <code>&lt;Setter Property="behaviors:RoundedClip.Radius" Value="{StaticResource RadiusMedium}" /&gt;</code>
/// </summary>
public static class RoundedClip
{
    public static readonly AttachedProperty<CornerRadius> RadiusProperty =
        AvaloniaProperty.RegisterAttached<Control, CornerRadius>("Radius", typeof(RoundedClip));

    public static void SetRadius(Control control, CornerRadius value) => control.SetValue(RadiusProperty, value);
    public static CornerRadius GetRadius(Control control) => control.GetValue(RadiusProperty);

    static RoundedClip()
    {
        // Клип пересчитывается при смене радиуса и при каждом изменении размера контрола.
        RadiusProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            Apply(control);
            control.PropertyChanged -= OnControlPropertyChanged;
            control.PropertyChanged += OnControlPropertyChanged;
        });
    }

    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && sender is Control control) Apply(control);
    }

    private static void Apply(Control control)
    {
        var size = control.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) { control.Clip = null; return; }

        // Углы у наших контейнеров всегда одинаковые — берем верхний левый как радиус.
        var radius = GetRadius(control).TopLeft;
        control.Clip = radius > 0
            ? new RectangleGeometry(new Rect(size), radius, radius)
            : null;
    }
}
