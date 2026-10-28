using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Controls.Components;

/// <summary>
/// Подменю выбора цвета из <see cref="AppPalette"/> — «Без цвета», черта, девять
/// цветных пунктов. Единая фабрика на все приложение: цвет коллекции в сайдбаре,
/// цвет записи, фон строки/ячейки/колонки и текст в таблице строятся ею же, поэтому
/// палитра и разметка пунктов не могут разъехаться между экранами.
/// </summary>
public static class ColorMenu
{
    /// Пункт-подменю с иконкой палитры и заданным текстом.
    /// <paramref name="current"/> отмечен цветным чекбоксом; <c>onPick(null)</c> — «Без цвета».
    public static MenuItem Build(string text, string? current, Action<string?> onPick) =>
        Build(MenuIcons.PaletteHeader(text), current, onPick);

    /// То же с готовым заголовком (когда нужна другая иконка).
    public static MenuItem Build(Control header, string? current, Action<string?> onPick)
    {
        var item = new MenuItem { Header = header };
        MenuIcons.FixSubmenuGap(item);

        var none = new MenuItem
        {
            Header = "Без цвета",
            Icon   = MenuCheckBox(null, !AppPalette.IsHex(current)),
        };
        none.Click += (_, _) => onPick(null);
        item.Items.Add(none);
        item.Items.Add(new Separator());

        foreach (var (hex, name) in AppPalette.Colors)
        {
            var sub = new MenuItem
            {
                Header = name,
                Icon   = MenuCheckBox(hex, string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)),
            };
            sub.Click += (_, _) => onPick(hex);
            item.Items.Add(sub);
        }
        return item;
    }

    /// Цветной чекбокс по стандарту сайдбара календаря и задач. Без выбора — цветная
    /// рамка, с выбором — заливка тем же цветом и белая галочка. «Без цвета» — серый.
    /// Шаблон, размеры и галочка — общие с обычным CheckBox (AppCheckBox.axaml).
    public static CheckBox CreateCheckBox(string? hex, object? content = null, bool isChecked = false)
    {
        var color = ColorTools.Solid(hex) ?? ColorTools.Solid(EventColors.Grey)!;
        var check = new CheckBox
        {
            Content     = content,
            IsChecked   = isChecked,
            Background  = color,
            BorderBrush = color,
        };
        check.Classes.Add("colorChoice");
        return check;
    }

    // Нажатия и клавиатуру обрабатывает сам MenuItem: чекбокс в слоте иконки только
    // показывает текущий цвет, не перехватывает фокус и не создает второй выбор.
    private static CheckBox MenuCheckBox(string? hex, bool isChecked)
    {
        var check = CreateCheckBox(hex, isChecked: isChecked);
        check.Padding = new Avalonia.Thickness(0);
        check.HorizontalAlignment = HorizontalAlignment.Center;
        check.IsHitTestVisible = false;
        check.Focusable = false;
        return check;
    }
}
