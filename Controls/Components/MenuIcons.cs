using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace DataBase.Controls.Components;

// Общие иконки и заголовки для контекстных меню (единый стиль во всём приложении).
// Иконка — обводочная, в стандарте сайдбара: Viewbox 18×18 поверх сетки 24×24.
public static class MenuIcons
{
    // Геометрии иконок — из общего словаря Assets/Styles/Icons.axaml (единый
    // источник на всё приложение). Ключ — x:Key соответствующей <StreamGeometry>.
    private static Geometry Icon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    // Размер иконки (сторона Viewbox) — из тех же токенов Icons.axaml.
    private static double IconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 18;

    private static Geometry Trash          => Icon("IconTrash");
    private static Geometry Pencil         => Icon("IconPencil");
    private static Geometry Copy           => Icon("IconCopy");
    private static Geometry ArrowLeft      => Icon("IconArrowLeft");
    private static Geometry ArrowRight     => Icon("IconArrowRight");
    private static Geometry ArrowUp        => Icon("IconArrowUp");
    private static Geometry ArrowDown      => Icon("IconArrowDown");
    private static Geometry Plus           => Icon("IconPlus");
    private static Geometry MoveHorizontal => Icon("IconMoveHorizontal");
    private static Geometry MoveVertical   => Icon("IconMoveVertical");
    private static Geometry CheckCheck     => Icon("IconCheckCheck");
    private static Geometry FolderTree     => Icon("IconFolderTree");
    private static Geometry FolderPlus     => Icon("IconFolderPlus");
    private static Geometry CollectionPlus => Icon("IconCollectionPlus");
    private static Geometry Palette        => Icon("IconPalette");

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
        Width  = IconSize("IconSizeMedium"),
        Height = IconSize("IconSizeMedium"),
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

    // Заголовок пункта «Расположение»: иконка дерева папок + стандартный текст.
    public static Control LocationHeader() => Header(FolderTree, "Расположение");

    // Заголовок пункта «Создать группу»: иконка папки с плюсом + стандартный текст.
    public static Control CreateGroupHeader() => Header(FolderPlus, "Создать группу");

    // Заголовок пункта «Создать коллекцию»: иконка таблицы с плюсом + стандартный текст.
    public static Control CreateCollectionHeader() => Header(CollectionPlus, "Создать коллекцию");

    // Заголовок пункта «Изменить цвет»: иконка палитры + стандартный текст.
    public static Control ColorHeader() => Header(Palette, "Изменить цвет");

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

    // Заголовок пункта «Отображение свойств даты»: та же иконка check-check + текст.
    public static Control DateFieldsHeader() => Header(CheckCheck, "Отображение свойств даты");

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

        var fontBase  = (double)Application.Current!.FindResource("FontSizeBase")!;
        var fontSmall = (double)Application.Current!.FindResource("FontSizeSmall")!;
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
                Width  = IconSize("IconSizeMedium"),
                Height = IconSize("IconSizeMedium"),
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
            FontWeight   = (FontWeight)Application.Current!.FindResource("FontWeightBold")!,
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
