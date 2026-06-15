using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Windows.Input;

namespace DataBase.Controls.Widgets;

public partial class AppButton : UserControl
{
    public static readonly StyledProperty<string>  TextProperty     = AvaloniaProperty.Register<AppButton, string>(nameof(Text));
    public static readonly StyledProperty<bool>    IsAccentProperty = AvaloniaProperty.Register<AppButton, bool>(nameof(IsAccent));
    public static readonly StyledProperty<bool>    IsDangerProperty = AvaloniaProperty.Register<AppButton, bool>(nameof(IsDanger));
    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<AppButton, ICommand?>(nameof(Command));

    public string   Text     { get => GetValue(TextProperty);     set => SetValue(TextProperty, value); }
    public bool     IsAccent { get => GetValue(IsAccentProperty); set => SetValue(IsAccentProperty, value); }
    public bool     IsDanger { get => GetValue(IsDangerProperty); set => SetValue(IsDangerProperty, value); }
    public ICommand? Command { get => GetValue(CommandProperty);  set => SetValue(CommandProperty, value); }

    public AppButton() => InitializeComponent();
}
