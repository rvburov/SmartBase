using Avalonia.Controls;
using Avalonia.Threading;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Переиспользуемое всплывающее уведомление, привязанное к контролу — всплывает рядом с ним,
/// как tooltip, но показывается ПРОГРАММНО (например, при ошибке валидации) и само закрывается.
///
/// Применение (из любого места UI):
/// <code>Toast.Error(anchorControl, "Текст ошибки");</code>
/// Для произвольного содержимого — <see cref="Show"/> (тот же механизм: Flyout у якоря + автоскрытие).
/// </summary>
public static class Toast
{
    /// <summary>Всплывашка-ошибка у <paramref name="anchor"/> — тем же баннером
    /// <see cref="ErrorBanner"/>, что и в диалогах «Создать»/«Изменить» (единый стиль).</summary>
    public static void Error(Control anchor, string message,
                             PlacementMode placement = PlacementMode.BottomEdgeAlignedLeft,
                             double seconds = 3.5)
    {
        if (anchor is null || string.IsNullOrEmpty(message)) return;
        var banner = new ErrorBanner();
        banner.Show(message);
        Show(anchor, banner, placement, seconds);
    }

    /// <summary>Базовый показ произвольного <paramref name="content"/> всплывашкой у якоря, с
    /// автоскрытием через <paramref name="seconds"/>. Внешняя «плашка» — стиль FlyoutPresenter.toast.</summary>
    public static void Show(Control anchor, Control content,
                            PlacementMode placement = PlacementMode.BottomEdgeAlignedLeft,
                            double seconds = 3.5)
    {
        if (anchor is null || content is null) return;

        var flyout = new Flyout
        {
            // Всплывашка рендерится вне зума приложения — контент в зум-обёртке.
            Content = WindowZoom.WrapPopupContent(content),
            Placement = placement,
            ShowMode = FlyoutShowMode.Transient,   // не забирает фокус
        };
        flyout.FlyoutPresenterClasses.Add("toast");
        flyout.ShowAt(anchor);

        // Самозакрытие: отдельный таймер на всплывашку (инлайн-якорь уже не следит за вводом).
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); flyout.Hide(); };
        timer.Start();
    }
}
