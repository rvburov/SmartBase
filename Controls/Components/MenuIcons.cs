using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Controls.Components;

// Общие иконки и заголовки для контекстных меню (единый стиль во всем приложении).
// Иконка — обводочная, в стандарте сайдбара: Viewbox 18×18 поверх сетки 24×24.
public static class MenuIcons
{
    // Геометрии иконок — из общего словаря Assets/Styles/Icons.axaml (единый
    // источник на все приложение). Ключ — x:Key соответствующей <StreamGeometry>.
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
    private static Geometry Check          => Icon("IconCheck");
    private static Geometry FolderTree     => Icon("IconFolderTree");
    private static Geometry FolderPlus     => Icon("IconFolderPlus");
    private static Geometry CollectionPlus => Icon("IconCollectionPlus");
    private static Geometry FilePlus       => Icon("IconFilePlus");
    private static Geometry Palette        => Icon("IconPalette");
    private static Geometry Workflow       => Icon("IconWorkflow");
    private static Geometry Play           => Icon("IconPlay");
    private static Geometry ClipboardCopy  => Icon("IconClipboardCopy");
    private static Geometry ClipboardPaste => Icon("IconClipboardPaste");
    private static Geometry Eraser         => Icon("IconEraser");

    private static SolidColorBrush Red => new(Color.Parse("#FF3B30"));

    // Стандартный цвет текста/иконки меню (BrushPrimaryText, fallback — черный).
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
                    StrokeThickness = IconSize("IconStrokeMedium"),
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    // Пункт выбора одного варианта из набора: у выбранного — галочка, у остальных на ее месте
    // пустое место той же ширины. Без распорки тексты пунктов съезжали бы друг относительно
    // друга, и список читался бы как рваный.
    public static Control ChoiceHeader(string text, bool selected)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(selected
            ? StrokeIcon(Check, PrimaryText)
            : new Border { Width = IconSize("IconSizeMedium") });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    /// <summary>
    /// Заголовок подменю «Действия» — зонтик, под которым живут и обмен, и (по плану) действия
    /// расширений. Поэтому у него иконка запуска, а не обмена: своя иконка стоит у каждого
    /// ПУНКТА и говорит, откуда он взялся.
    /// </summary>
    public static Control ActionsHeader() => Header(Play, "Действия");

    /// <summary>
    /// Пункт, приехавший из интеграций, — та же иконка, что у раздела «Интеграции» в настройках:
    /// человек должен узнать, где это настраивается, не читая текста.
    /// </summary>
    public static Control IntegrationHeader(string text) => Header(Workflow, text);

    /// <summary>
    /// Пункт без иконки, но с ее отступом — чтобы текст стоял в одну линию с соседями по меню.
    /// Нужен там, где иконка ничего не добавляет: «Вырезать», «Копировать», названия подменю.
    /// </summary>
    public static Control PlainHeader(string text) => ChoiceHeader(text, selected: false);

    // ── Меню редактора заметки ──────────────────────────────────────────────────

    public static Control BoldHeader()          => Header(Icon("IconBold"),              "Жирный");
    public static Control ItalicHeader()        => Header(Icon("IconItalic"),            "Курсив");
    public static Control StrikeHeader()        => Header(Icon("IconStrikethrough"),     "Зачеркнутый");
    public static Control InlineCodeHeader()    => Header(Icon("IconCode"),              "Код");
    public static Control LinkHeader()          => Header(Icon("IconLink"),              "Ссылка");
    public static Control ClearFormatHeader()   => Header(Icon("IconRemoveFormatting"),  "Очистить форматирование");

    public static Control BulletListHeader()    => Header(Icon("IconList"),              "Маркированный список");
    public static Control OrderedListHeader()   => Header(Icon("IconListOrdered"),       "Нумерованный список");
    public static Control QuoteHeader()         => Header(Icon("IconQuote"),             "Цитата");
    public static Control HeadingHeader(string text) => Header(Icon("IconHeading"), text);

    public static Control TableHeader()         => Header(Icon("IconTable"),             "Таблица");
    public static Control CodeBlockHeader()     => Header(Icon("IconCode"),              "Блок кода");
    public static Control RuleHeader()          => Header(Icon("IconMinus"),             "Горизонтальная линия");

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

    // Заголовок пункта «Создать папку» (раздел «Заметки»): та же иконка folder-plus, что у
    // «Создать группу» в конструкторе — для человека это одно и то же действие.
    public static Control CreateFolderHeader() => Header(FolderPlus, "Создать папку");

    // Заголовок пункта «Создать заметку»: иконка файла с плюсом + стандартный текст.
    public static Control CreateNoteHeader() => Header(FilePlus, "Создать заметку");

    // Заголовок пункта «Переименовать» (раздел «Заметки»): тот же карандаш, что у «Изменить»,
    // но текст точнее — у файла правится ровно имя.
    public static Control RenameHeader() => Header(Pencil, "Переименовать");

    // Заголовок пункта «Изменить цвет»: иконка палитры + стандартный текст.
    public static Control ColorHeader() => PaletteHeader("Изменить цвет");

    // Тот же заголовок с произвольным текстом: пунктов выбора цвета в таблице несколько
    // («Цвет записи», «Цвет текста строки», «Фон ячейки», «Фон колонки»).
    public static Control PaletteHeader(string text) => Header(Palette, text);

    // Сабменю по умолчанию (тема Fluent) ставит HorizontalOffset попапа ≈ -1 (локальное
    // значение шаблона, стилем не перебить) → блок наезжает на меню. После применения
    // шаблона находим Popup сабменю и сдвигаем вправо, чтобы он стоял отдельно (как в VSCode).
    public static void FixSubmenuGap(MenuItem item) =>
        item.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Avalonia.Controls.Primitives.Popup>("PART_Popup") is { } popup)
                popup.HorizontalOffset = 6;
        };

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

    // Заголовок пункта «Отображение свойств исполнителя» (раздел задач): та же иконка.
    public static Control UserFieldsHeader() => Header(CheckCheck, "Отображение свойств исполнителя");

    // Заголовок пункта «Создать запись»: иконка plus + стандартный текст.
    public static Control CreateRecordHeader() => Header(Plus, "Создать запись");

    // Заголовок пункта-переключателя режима ширины столбцов: иконка move-horizontal
    // + текст. Текст задает вызывающий (показывает режим, в который переключит клик).
    public static Control WidthModeHeader(string text) => Header(MoveHorizontal, text);

    // Заголовок пункта-переключателя режима высоты строк: иконка move-vertical + текст.
    // Текст задает вызывающий (показывает режим, в который переключит клик).
    public static Control HeightModeHeader(string text) => Header(MoveVertical, text);

    // Русская подпись типа поля — для шапки контекстного меню свойства (единый источник в Core).
    private static string TypeLabel(string? fieldType) => Core.Models.FieldTypeInfo.Label(fieldType);

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
                            StrokeThickness = IconSize("IconStrokeMedium"),
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

    // Работа со значениями выделенных ячеек. Три пункта идут своей группой — отдельно от
    // «Удалить», чтобы очистку значений не путали с удалением записи или столбца. «Значения», а
    // не «текст»: те же пункты работают с галочками, датами и списками.
    public static Control CopyValuesHeader()  => Header(ClipboardCopy,  "Копировать значения");
    public static Control PasteValuesHeader() => Header(ClipboardPaste, "Вставить значения");
    public static Control ClearValuesHeader() => Header(Eraser,         "Очистить значения");

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
