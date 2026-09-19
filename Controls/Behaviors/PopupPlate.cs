using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Плашка выпадающей панели — одна на все чеклисты приложения: фильтр колонки таблицы, фильтры
/// журналов и истории, пикер коллекций, «Тип документа» библиотеки.
///
/// Заведена ради ОДНОГО места для чисел подгонки. Раньше связка «флайаут + класс плашки»
/// повторялась в четырех файлах, и правка вида означала четыре одинаковые правки — ровно тот
/// способ, которым панели в этом проекте уже расходились.
///
/// Вертикальный зазор задает не отсюда, а стиль <c>FlyoutPresenter.filterFlyout</c>
/// (Assets/Styles/Controls.axaml): он часть вида плашки, а не ее позиции.
/// </summary>
public static class PopupPlate
{
    /// <summary>
    /// Сдвиг вправо относительно якоря. Раскладка выравнивает правые края ТОЧНО — замер
    /// Tests/PopupGeometry дает 0,00 на всех масштабах и зумах. Но попап-окно на macOS всегда
    /// получает системную тень (отключить нельзя, <c>SetWindowManagerAddShadowHint</c> там
    /// пустой), и на глаз она уводит край плашки внутрь. Число подобрано по живому экрану;
    /// менять его — здесь, одной правкой на все панели.
    /// </summary>
    public const double Nudge = 2;

    /// <summary>Пустой флайаут-плашка у нижнего края якоря, выровненный по его правому краю.</summary>
    public static Flyout Filter()
    {
        var flyout = new Flyout
        {
            Placement        = PlacementMode.BottomEdgeAlignedRight,
            HorizontalOffset = Nudge,
        };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        return flyout;
    }
}
