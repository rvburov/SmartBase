using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// ШАБЛОН уведомления на все приложение: иконка-восклицание, рамка, светлая подложка, текст
/// в цвет рамки. Красный (<see cref="BannerKind.Error"/>, стандарт) — ошибка; синий
/// (<see cref="BannerKind.Info"/>, акцентная палитра) — сведение. Любая «плашка» делается
/// этим виджетом, своей верстки для них не заводим — иначе вид расходится от окна к окну.
/// Сейчас им показываются: ошибки валидации в диалогах, постоянные пояснения в диалоге
/// свойства («базовое свойство», «привязано к разделу»), всплывашки <see cref="Toast"/> и
/// карточки <see cref="AppNotifications"/>.
///
/// Скрыт, пока нет текста: <c>Show(msg)</c> показывает, <c>Hide()</c> прячет. Длинный текст
/// переносится по словам, высота идет по содержимому. Размер текста — стандартный
/// (FontSizeBase), одинаковый во всех уведомлениях. Внешний отступ (Margin) и
/// DockPanel.Dock задаются на месте использования.
/// </summary>
public partial class NoticeBanner : UserControl
{
    public enum BannerKind { Error, Info }

    public NoticeBanner() => InitializeComponent();

    /// <summary>Текст уведомления. Пустая строка/null прячет баннер.</summary>
    public string? Message
    {
        get => MessageText.Text;
        set { MessageText.Text = value; IsVisible = !string.IsNullOrEmpty(value); }
    }

    private BannerKind _kind = BannerKind.Error;

    /// <summary>Ошибка (красный, стандарт) или сведение (синий). Кисти — из палитры Colors.axaml:
    /// подложка светлая, рамка/иконка/текст — насыщенный цвет того же тона.</summary>
    public BannerKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            var (light, strong) = value == BannerKind.Info
                ? (Brush("BrushAccentLight", "#E5F0FF"), Brush("BrushAccent", "#007AFF"))
                : (Brush("BrushRedLight",    "#FFE5E3"), Brush("BrushRed",    "#FF3B30"));
            Frame.Background       = light;
            Frame.BorderBrush      = strong;
            IconCircle.Fill        = strong;
            MessageText.Foreground = strong;
        }
    }

    public void Show(string message) => Message = message;
    public void Hide()               => Message = null;

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
