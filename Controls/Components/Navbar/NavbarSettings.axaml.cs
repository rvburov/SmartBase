using Avalonia.Controls;

namespace DataBase.Controls.Components;

/// <summary>Навбар раздела «Настройки»: заголовок выбранного раздела. Высота/отступы — как у
/// остальных навбаров (56 / 16).</summary>
public partial class NavbarSettings : UserControl
{
    public NavbarSettings() => InitializeComponent();

    /// <summary>Заголовок раздела настроек.</summary>
    public string Title { set => TitleText.Text = value; }
}
