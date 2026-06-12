using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace MyPlanner.Controls.Components;

// Общие иконки и заголовки для контекстных меню (единый стиль во всём приложении).
// Иконка — обводочная, в стандарте сайдбара: Viewbox 18×18 поверх сетки 24×24.
public static class MenuIcons
{
    // lucide trash-2
    private static readonly Geometry Trash = Geometry.Parse(
        "M10 11v6 M14 11v6 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M3 6h18 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2");

    // lucide pencil-line
    private static readonly Geometry Pencil = Geometry.Parse(
        "M13 21h8 M15 5l4 4 M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z");

    // lucide copy
    private static readonly Geometry Copy = Geometry.Parse(
        "M8 8 H20 A2 2 0 0 1 22 10 V20 A2 2 0 0 1 20 22 H8 A2 2 0 0 1 6 20 V10 A2 2 0 0 1 8 8 Z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2");

    // lucide arrow-left / arrow-right
    private static readonly Geometry ArrowLeft  = Geometry.Parse("M12 19l-7-7 7-7 M19 12H5");
    private static readonly Geometry ArrowRight = Geometry.Parse("M5 12h14 M12 5l7 7-7 7");

    // lucide plus
    private static readonly Geometry Plus = Geometry.Parse("M5 12h14 M12 5v14");

    // lucide move-horizontal
    private static readonly Geometry MoveHorizontal = Geometry.Parse("M18 8l4 4-4 4 M2 12h20 M6 8l-4 4 4 4");

    private static SolidColorBrush Red => new(Color.Parse("#FF3B30"));

    // Стандартный цвет текста/иконки меню (BrushPrimaryText, fallback — чёрный).
    private static IBrush PrimaryText =>
        Application.Current!.TryFindResource("BrushPrimaryText", out var res) && res is IBrush b ? b : Brushes.Black;

    // Заголовок: обводочная иконка стандартного цвета + текст.
    private static Control Header(Geometry icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(StrokeIcon(icon, PrimaryText));
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    public static Viewbox StrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width  = 18,
        Height = 18,
        VerticalAlignment = VerticalAlignment.Center,
        Child  = new Canvas
        {
            Width  = 24,
            Height = 24,
            Children =
            {
                new Path
                {
                    Data            = data,
                    Stroke          = stroke,
                    StrokeThickness = 1.6,
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    // Заголовок пункта «Дублировать»: иконка copy + стандартный текст.
    public static Control DuplicateHeader() => Header(Copy, "Дублировать");

    // Заголовок пункта «Изменить»: иконка карандаша + стандартный текст.
    public static Control EditHeader() => Header(Pencil, "Изменить");

    // Заголовки пунктов перемещения колонки.
    public static Control MoveLeftHeader()  => Header(ArrowLeft,  "Сдвинуть влево");
    public static Control MoveRightHeader() => Header(ArrowRight, "Сдвинуть вправо");

    // Заголовок пункта «Создать новое свойство»: иконка plus + стандартный текст.
    public static Control CreatePropertyHeader() => Header(Plus, "Создать новое свойство");

    // Заголовок пункта-переключателя режима ширины столбцов: иконка move-horizontal
    // + текст. Текст задаёт вызывающий (показывает режим, в который переключит клик).
    public static Control WidthModeHeader(string text) => Header(MoveHorizontal, text);

    // Заголовок пункта «Удалить»: иконка корзины + красный текст.
    public static Control DeleteHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(StrokeIcon(Trash, Red));
        row.Children.Add(new TextBlock
        {
            Text              = "Удалить",
            Foreground        = Red,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }
}
