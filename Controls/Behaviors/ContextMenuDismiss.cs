using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Контекстные меню программы: открыто не больше одного, и любое нажатие не в самом меню — в любом
/// окне программы — или уход в другую программу его закрывает.
///
/// <b>Зачем.</b> Клик мимо меню ловит прозрачная подложка окна (<c>LightDismissOverlayLayer</c>) —
/// одна на окно для всех всплывающих панелей: открытое меню ее показывает, закрытое прячет. Нажатие
/// подложка сначала передает тому, что под ней, и только потом закрывает меню. Таблица открывает
/// меню на нажатии правой кнопки, поэтому правый щелчок мимо открытого меню успевает открыть новое,
/// а закрывающееся старое следом прячет подложку: новое остается без нее, клик мимо его уже не
/// закрывает, а каждый следующий правый щелчок кладет еще одно меню поверх. Свой перехват нажатий
/// (закрыть меню раньше подложки) Avalonia вешает на окно, где нарисовано меню, — на Windows и macOS
/// это отдельное попап-окно, и нажатий в окне программы он не видит. Безголовый режим попап-окон не
/// умеет, меню живет в слое самого окна, и там сбоя нет — стенд воспроизводит его, снимая перехват.
///
/// <b>Как.</b> Открытые меню на учете. Нажатие в любом окне программы слушается туннелем раньше всех
/// обработчиков: меню, мимо которого нажали, закрывается до того, как подложка передаст нажатие
/// дальше, — новое меню открывается уже после и подложку сохраняет. Открытие меню закрывает
/// остальные, потеря окном активности (уход в другую программу, свое модальное окно) — все.
///
/// Все вызовы — с UI-потока, синхронизация не нужна.
/// </summary>
public static class ContextMenuDismiss
{
    private static readonly List<ContextMenu> Open = [];
    private static bool _installed;

    /// <summary>Подключить правило. Зовет приложение при старте; повторный вызов ничего не делает.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        MenuBase.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => Opened(menu));
        MenuBase.ClosedEvent.AddClassHandler<ContextMenu>((menu, _) => Open.Remove(menu));
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, e) => Pressed(e),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        WindowBase.IsActiveProperty.Changed.AddClassHandler<Window>((_, e) =>
        {
            if (e.NewValue is false) CloseAll();
        });
    }

    /// <summary>Закрыть все открытые меню.</summary>
    public static void CloseAll()
    {
        // Идем по копии: Close() поднимает Closed, а тот правит сам список.
        foreach (var menu in Open.ToList()) menu.Close();
        Forget();
    }

    /// <summary>Сколько меню сейчас открыто. Для стендов.</summary>
    public static int Count => Open.Count(m => m.IsOpen);

    private static void Opened(ContextMenu menu)
    {
        foreach (var other in Open.ToList())
            if (!ReferenceEquals(other, menu)) other.Close();
        Forget();
        if (!Open.Contains(menu)) Open.Add(menu);
    }

    private static void Pressed(PointerPressedEventArgs e)
    {
        if (Open.Count == 0) return;
        foreach (var menu in Open.ToList())
            if (!Inside(menu, e.Source as ILogical)) menu.Close();
        Forget();
    }

    // Реестр не копит меню, которые закрылись без Closed (закрытие отменено и т. п.).
    private static void Forget() => Open.RemoveAll(m => !m.IsOpen);

    // Внутри меню — оно само и все, что под ним в логическом дереве: пункты, их шаблоны и подменю
    // (попап-окно подменю логически висит на своем пункте). То же правило у самой Avalonia.
    private static bool Inside(ContextMenu menu, ILogical? source) =>
        source is not null && (ReferenceEquals(source, menu) || menu.IsLogicalAncestorOf(source));
}
