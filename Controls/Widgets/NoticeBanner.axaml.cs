using Avalonia.Controls;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Красный баннер с уведомлением об ошибке валидации. Скрыт, пока нет текста.
/// Внешний отступ (Margin) и DockPanel.Dock задаются на месте использования.
/// </summary>
public partial class ErrorBanner : UserControl
{
    public ErrorBanner() => InitializeComponent();

    /// <summary>Текст ошибки. Пустая строка/null прячет баннер.</summary>
    public string? Message
    {
        get => MessageText.Text;
        set { MessageText.Text = value; IsVisible = !string.IsNullOrEmpty(value); }
    }

    public void Show(string message) => Message = message;
    public void Hide()               => Message = null;
}
