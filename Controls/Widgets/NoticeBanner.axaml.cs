using Avalonia.Controls;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// ШАБЛОН красного уведомления на все приложение: иконка-восклицание, красная рамка,
/// светло-красная подложка, красный текст. Любая «красная плашка» делается этим виджетом,
/// своей верстки для них не заводим — иначе вид расходится от окна к окну. Сейчас им
/// показываются: ошибки валидации в диалогах, постоянные пояснения в диалоге свойства
/// («базовое свойство», «привязано к разделу»), всплывашки <see cref="Toast"/>.
///
/// Скрыт, пока нет текста: <c>Show(msg)</c> показывает, <c>Hide()</c> прячет. Длинный текст
/// переносится по словам, высота идет по содержимому. Размер текста — стандартный
/// (FontSizeBase), одинаковый во всех уведомлениях. Внешний отступ (Margin) и
/// DockPanel.Dock задаются на месте использования.
/// </summary>
public partial class NoticeBanner : UserControl
{
    public NoticeBanner() => InitializeComponent();

    /// <summary>Текст уведомления. Пустая строка/null прячет баннер.</summary>
    public string? Message
    {
        get => MessageText.Text;
        set { MessageText.Text = value; IsVisible = !string.IsNullOrEmpty(value); }
    }

    public void Show(string message) => Message = message;
    public void Hide()               => Message = null;
}
