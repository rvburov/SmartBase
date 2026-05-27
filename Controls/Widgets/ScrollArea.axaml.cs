using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace MyPlanner.Controls.Widgets;

public partial class ScrollArea : UserControl
{
    public static readonly StyledProperty<object?> InnerContentProperty =
        AvaloniaProperty.Register<ScrollArea, object?>(nameof(InnerContent));

    public static readonly StyledProperty<ScrollBarVisibility> VScrollVisibilityProperty =
        AvaloniaProperty.Register<ScrollArea, ScrollBarVisibility>(nameof(VScrollVisibility), ScrollBarVisibility.Auto);

    public static readonly StyledProperty<ScrollBarVisibility> HScrollVisibilityProperty =
        AvaloniaProperty.Register<ScrollArea, ScrollBarVisibility>(nameof(HScrollVisibility), ScrollBarVisibility.Disabled);

    public object?            InnerContent      { get => GetValue(InnerContentProperty);      set => SetValue(InnerContentProperty, value); }
    public ScrollBarVisibility VScrollVisibility { get => GetValue(VScrollVisibilityProperty); set => SetValue(VScrollVisibilityProperty, value); }
    public ScrollBarVisibility HScrollVisibility { get => GetValue(HScrollVisibilityProperty); set => SetValue(HScrollVisibilityProperty, value); }

    public ScrollArea() => InitializeComponent();
}
