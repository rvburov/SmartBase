using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    private const double SwatchSize = 14;
    private static readonly CornerRadius SwatchRadius = new(3);

    /// Пункт-подменю с иконкой палитры и заданным текстом.
    /// <paramref name="current"/> обводится рамкой; <c>onPick(null)</c> — «Без цвета».
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
            Icon   = Swatch(null, isCurrent: !AppPalette.IsHex(current)),
        };
        none.Click += (_, _) => onPick(null);
        item.Items.Add(none);
        item.Items.Add(new Separator());

        foreach (var (hex, name) in AppPalette.Colors)
        {
            var sub = new MenuItem
            {
                Header = name,
                Icon   = Swatch(hex, isCurrent: string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)),
            };
            sub.Click += (_, _) => onPick(hex);
            item.Items.Add(sub);
        }
        return item;
    }

    /// Квадратик цвета в слоте иконки пункта. «Без цвета» — пустой квадрат с серой
    /// обводкой (как чекбокс коллекции в сайдбаре); текущий выбор — темная рамка. Им же
    /// показан цвет фона в подменю «Фильтр по цвету» окна фильтра.
    public static Border Swatch(string? hex, bool isCurrent = false)
    {
        var ring = isCurrent
            ? Brush("BrushPrimaryText", "#1C1C1E")
            : hex is null ? Brush("BrushSecondaryText", EventColors.Grey) : null;

        return new Border
        {
            Width           = SwatchSize,
            Height          = SwatchSize,
            CornerRadius    = SwatchRadius,
            Background      = ColorTools.Solid(hex) ?? Brushes.Transparent,
            BorderBrush     = ring,
            BorderThickness = new Thickness(ring is null ? 0 : 1.5),
        };
    }

    /// Образец цвета текста — буква «А» этим цветом, как текст в ячейке (ColorTools.Text);
    /// стандартный цвет — обычным цветом текста.
    public static TextBlock TextSample(string? hex) => new()
    {
        Text                = "А",
        Width               = SwatchSize,
        TextAlignment       = TextAlignment.Center,
        FontWeight          = FontWeight.SemiBold,
        VerticalAlignment   = Avalonia.Layout.VerticalAlignment.Center,
        Foreground          = ColorTools.Text(hex) ?? Brush("BrushPrimaryText", "#1C1C1E"),
    };

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
