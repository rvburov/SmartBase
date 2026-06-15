using Avalonia;
using Avalonia.Controls;

namespace DataBase.Controls.Widgets;

public partial class SearchInput : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<SearchInput, string>(nameof(Text));

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public SearchInput() => InitializeComponent();
}
