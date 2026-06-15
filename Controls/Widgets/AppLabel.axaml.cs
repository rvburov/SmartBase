using Avalonia;
using Avalonia.Controls;

namespace DataBase.Controls.Widgets;

public partial class AppLabel : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<AppLabel, string>(nameof(Text));

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public AppLabel() => InitializeComponent();
}
