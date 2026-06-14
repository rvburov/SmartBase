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

    // lucide arrow-up / arrow-down
    private static readonly Geometry ArrowUp   = Geometry.Parse("M5 12l7-7 7 7 M12 19V5");
    private static readonly Geometry ArrowDown = Geometry.Parse("M12 5v14 M19 12l-7 7-7-7");

    // lucide plus
    private static readonly Geometry Plus = Geometry.Parse("M5 12h14 M12 5v14");

    // lucide move-horizontal
    private static readonly Geometry MoveHorizontal = Geometry.Parse("M18 8l4 4-4 4 M2 12h20 M6 8l-4 4 4 4");

    // lucide move-vertical
    private static readonly Geometry MoveVertical = Geometry.Parse("M12 2v20 M8 18l4 4 4-4 M8 6l4-4 4 4");

    // lucide check-check
    private static readonly Geometry CheckCheck = Geometry.Parse("M18 6 7 17l-5-5 M22 10l-7.5 7.5L13 16");

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

    // Заголовки пунктов перемещения записи (строки).
    public static Control MoveUpHeader()   => Header(ArrowUp,   "Сдвинуть вверх");
    public static Control MoveDownHeader() => Header(ArrowDown, "Сдвинуть вниз");

    // Заголовок пункта «Создать новое свойство»: иконка plus + стандартный текст.
    public static Control CreatePropertyHeader() => Header(Plus, "Создать новое свойство");

    // Заголовок пункта «Отображение»: иконка check-check + стандартный текст.
    public static Control DisplayHeader() => Header(CheckCheck, "Отображение");

    // Заголовок пункта «Создать запись»: иконка plus + стандартный текст.
    public static Control CreateRecordHeader() => Header(Plus, "Создать запись");

    // Заголовок пункта-переключателя режима ширины столбцов: иконка move-horizontal
    // + текст. Текст задаёт вызывающий (показывает режим, в который переключит клик).
    public static Control WidthModeHeader(string text) => Header(MoveHorizontal, text);

    // Заголовок пункта-переключателя режима высоты строк: иконка move-vertical + текст.
    // Текст задаёт вызывающий (показывает режим, в который переключит клик).
    public static Control HeightModeHeader(string text) => Header(MoveVertical, text);

    // Русская подпись типа поля — для шапки контекстного меню свойства.
    private static string TypeLabel(string? fieldType) => fieldType switch
    {
        "text"     => "Текст",
        "number"   => "Число",
        "date"     => "Дата",
        "select"   => "Список",
        "checkbox" => "Чекбокс",
        "url"      => "Ссылка",
        _          => "Текст",
    };

    private static IBrush Brush(string key, IBrush fallback) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b ? b : fallback;

    // Шапка контекстного меню свойства: слева иконка типа в светло-синем квадрате,
    // справа — название свойства (жирное) и под ним тип данных (серым). Некликабельная.
    public static Control PropertyMenuHeader(string name, string? fieldType)
    {
        var accent       = Brush("BrushAccent", Brushes.RoyalBlue);
        var accentLight  = Brush("BrushAccentLight", Brushes.LightSteelBlue);
        var primary      = Brush("BrushPrimaryText", Brushes.Black);
        var secondary    = Brush("BrushSecondaryText", Brushes.Gray);

        var fontBase  = Application.Current!.TryFindResource("FontSizeBase", out var fb) && fb is double d1 ? d1 : 12d;
        var fontSmall = Application.Current!.TryFindResource("FontSizeSmall", out var fs) && fs is double d2 ? d2 : 11d;
        var radius    = Application.Current!.TryFindResource("RadiusSmall", out var r) && r is CornerRadius cr ? cr : new CornerRadius(5);

        // Иконка типа в светло-синем квадрате (как на карточке свойства).
        var geo  = FieldTypeIcons.ForType(fieldType);
        var icon = new Border
        {
            Width             = 30,
            Height            = 30,
            CornerRadius      = radius,
            Background        = accentLight,
            VerticalAlignment = VerticalAlignment.Center,
            Child = geo is null ? null : new Viewbox
            {
                Width  = 18,
                Height = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Child = new Canvas
                {
                    Width  = 24,
                    Height = 24,
                    Children =
                    {
                        new Path
                        {
                            Data            = geo,
                            Stroke          = accent,
                            StrokeThickness = 1.8,
                            StrokeLineCap   = PenLineCap.Round,
                            StrokeJoin      = PenLineJoin.Round,
                        },
                    },
                },
            },
        };

        // Ограничиваем ширину текстов — длинное название обрезается многоточием,
        // меню не растягивается на весь экран.
        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing           = 1,
            MaxWidth          = 160,
        };
        texts.Children.Add(new TextBlock
        {
            Text         = string.IsNullOrWhiteSpace(name) ? "Без названия" : name,
            FontSize     = fontBase,
            FontWeight   = FontWeight.SemiBold,
            Foreground   = primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        texts.Children.Add(new TextBlock
        {
            Text         = TypeLabel(fieldType),
            FontSize     = fontSmall,
            Foreground   = secondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(icon);
        row.Children.Add(texts);
        return row;
    }

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
