using Avalonia;
using Avalonia.Controls;

namespace MyPlanner.Controls.Widgets;

public partial class AppCheckBox : UserControl
{
    public static readonly StyledProperty<string> LabelProperty     = AvaloniaProperty.Register<AppCheckBox, string>(nameof(Label));
    public static readonly StyledProperty<bool>   IsCheckedProperty = AvaloniaProperty.Register<AppCheckBox, bool>(nameof(IsChecked));

    public string Label     { get => GetValue(LabelProperty);     set => SetValue(LabelProperty, value); }
    public bool   IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }

    public AppCheckBox() => InitializeComponent();
}
