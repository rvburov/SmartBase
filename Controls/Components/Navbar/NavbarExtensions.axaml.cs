using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SmartBase.Controls.Components;

/// <summary>Навбар раздела «Расширения»: имя расширения слева + кнопка «Запустить» справа.
/// Высота/отступы — как у остальных навбаров (56 / 16). Логику запуска решает родитель
/// через событие <see cref="RunClicked"/>.</summary>
public partial class NavbarExtensions : UserControl
{
    /// <summary>Нажата кнопка «Запустить».</summary>
    public event EventHandler? RunClicked;

    public NavbarExtensions() => InitializeComponent();

    /// <summary>Заголовок (имя расширения).</summary>
    public string Title { set => TitleText.Text = value; }

    /// <summary>Доступность кнопки запуска + подсказка к ней.</summary>
    public void SetRunState(bool canRun, string tip)
    {
        RunBtn.IsEnabled = canRun;
        ToolTip.SetTip(RunBtn, tip);
    }

    private void OnRunClick(object? sender, RoutedEventArgs e)
        => RunClicked?.Invoke(this, EventArgs.Empty);
}
