using Avalonia.Controls;

namespace DataBase.Controls.Components;

/// <summary>Навбар раздела «Профиль»: заголовок выбранного раздела. Высота/отступы — как у
/// остальных навбаров (56 / 16).</summary>
public partial class NavbarProfile : UserControl
{
    public NavbarProfile() => InitializeComponent();

    /// <summary>Заголовок раздела профиля.</summary>
    public string Title { set => TitleText.Text = value; }
}
