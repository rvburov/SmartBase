using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

/// <summary>
/// Подсказка сочетания в пункте контекстного меню. Берется из действующей привязки
/// («Настройки → Сочетания клавиш»), а не пишется числом рядом с пунктом: подпись и нажатие
/// расходиться не должны, иначе переназначенная клавиша врет в каждом меню.
///
/// Командный модификатор подставляется платформенный — на macOS в меню видно Cmd, на Windows
/// Ctrl, ровно как жмут.
/// </summary>
public static class MenuGestures
{
    /// <summary>Поставить пункту подсказку сочетания действия. Нет жеста — подсказки нет.</summary>
    public static MenuItem WithGesture(this MenuItem item, string actionId, Visual owner)
    {
        item.InputGesture = For(actionId, owner);
        return item;
    }

    /// <summary>Действующее сочетание действия как <see cref="KeyGesture"/>; null — жеста нет.</summary>
    public static KeyGesture? For(string actionId, Visual owner) =>
        ShortcutSettings.PrimaryGesture(actionId)?.ToKeyGesture(CommandModifiers(owner));

    /// <summary>Модификатор команд платформы: Ctrl, а на macOS — Cmd.</summary>
    public static KeyModifiers CommandModifiers(Visual owner) =>
        TopLevel.GetTopLevel(owner)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers
        ?? KeyModifiers.Control;
}
