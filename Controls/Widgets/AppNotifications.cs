using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Сообщения приложения человеку — карточки в правом верхнем углу главного окна, стопкой:
/// новые встают под старыми, друг друга не перекрывают, сами гаснут, длинный текст
/// переносится. Механизм — встроенный <see cref="WindowNotificationManager"/> Avalonia;
/// содержимое каждой карточки — обычный <see cref="NoticeBanner"/> (красный для ошибок,
/// синий для сведений), поэтому вид тот же, что у баннеров в диалогах. Сама карточка
/// (NotificationCard) своего оформления не имеет — стиль в Assets/Styles/Controls.axaml.
///
/// Раньше те же сообщения показывались всплывашками (Toast) у панели разделов в левом
/// нижнем углу: каждая — отдельный Flyout на одном якоре, стопки нет, два сообщения подряд
/// ложились друг на друга и резались по краю окна.
///
/// Кто пользуется: ошибки записи в базу (<see cref="Utils.DbErrors"/>), допуск администратора,
/// переходы по чипам связи, непредвиденные ошибки UI-потока (хук диспетчера в App). Статус
/// доступности базы сюда НЕ идет — он живет надписью под именем базы в сайдбаре и в
/// «Управление базой данных».
///
/// Вызывать можно с любого потока: показ маршалится на UI. До установки в окно сообщения
/// копятся и показываются, как только окно готово.
/// </summary>
public static class AppNotifications
{
    private static WindowNotificationManager? _manager;
    private static readonly List<(NoticeBanner.BannerKind Kind, string Text)> _pending = [];

    /// <summary>Сколько карточка висит; MaxVisible — сколько их видно разом (старшие уходят).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    public const int MaxVisible = 4;

    /// <summary>Установить менеджер в окно (один раз, из MainWindow). Слой оверлея окна есть
    /// только после применения шаблона, поэтому ждем Opened.</summary>
    public static void Install(Window window)
    {
        if (_manager is not null) return;
        if (window.IsLoaded) Attach(window);
        else window.Opened += (_, _) => Attach(window);
    }

    private static void Attach(Window window)
    {
        if (_manager is not null) return;
        var adorner = window.FindDescendantOfType<VisualLayerManager>()?.AdornerLayer;
        if (adorner is null) return;

        var manager = new WindowNotificationManager
        {
            Position = NotificationPosition.TopRight,
            MaxItems = MaxVisible,
        };
        // Слой оверлея лежит ВНЕ зума приложения (ScaleHost — содержимое окна), поэтому
        // менеджер, как и попапы, живет в обертке с живым трансформом зума: карточки
        // масштабируются вместе с остальным интерфейсом.
        var host = Behaviors.WindowZoom.WrapPopupContent(manager);
        adorner.Children.Add(host);
        AdornerLayer.SetAdornedElement(host, adorner);
        _manager = manager;

        foreach (var (kind, text) in _pending) Show(kind, text);
        _pending.Clear();
    }

    /// <summary>Красная карточка: ошибка, несохраненное изменение, отказ.</summary>
    public static void Error(string message) => Post(NoticeBanner.BannerKind.Error, message);

    /// <summary>Синяя карточка: сведение, которое нужно заметить (допуск выдан и т.п.).</summary>
    public static void Info(string message) => Post(NoticeBanner.BannerKind.Info, message);

    private static void Post(NoticeBanner.BannerKind kind, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_manager is null) _pending.Add((kind, message));
            else Show(kind, message);
        });
    }

    private static void Show(NoticeBanner.BannerKind kind, string text)
    {
        var banner = new NoticeBanner { Kind = kind };
        banner.Show(text);
        _manager!.Show(banner,
                       kind == NoticeBanner.BannerKind.Error ? NotificationType.Error : NotificationType.Information,
                       Lifetime);
    }
}
