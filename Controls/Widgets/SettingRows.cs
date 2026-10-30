using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Utils;

namespace SmartBase.Controls.Widgets;

/// <summary>
/// Общий вид строки настройки: заголовок с пояснением, кнопка, выпадающий список, поле, тумблер,
/// ячейка с данными и строка таблицы. Стили к ним — <c>Assets/Styles/SettingRows.axaml</c>.
///
/// <b>Зачем отдельный набор.</b> Экраны, у которых список строк заранее неизвестен, собираются в
/// code-behind — там, где остальные разделы настроек описаны разметкой: вкладки «Интеграций»,
/// строки расширений по <c>IExtension.Settings</c>. Пока каждый экран заводил контролы сам, они разъезжались: у одной
/// высоты поля 34, у другой 30, кнопки то с рамкой, то без. Вид задается здесь одним набором и
/// берется ТОЛЬКО отсюда — числа в самих экранах не пишутся.
///
/// <b>Почему класс общий, а не partial внутри одного экрана.</b> Пока он был частью
/// <c>SettingsView</c>, взять его в другом разделе можно было только копией — и одна такая копия
/// уже завелась в <c>ProfileView.axaml</c>. Копия расходится с оригиналом при первой же правке
/// отступа, и расхождение видно человеку, а не программисту.
///
/// <b>Откуда взяты числа.</b> Из соседних разделов настроек, а не придуманы заново:
///
/// <list type="bullet">
///   <item>кнопка — общий стиль <c>dialog auto</c>, как «Копировать сейчас» в «Резервном
///   копировании» и «Проверить» в «О программе»;</item>
///   <item>поле и список — высота <c>ControlHeightStandard</c>, серый фон, скругление
///   <c>RadiusBase</c>, как поле пути обновления и списки «Оформления»;</item>
///   <item>тумблер — без подписей «On/Off», как все тумблеры настроек;</item>
///   <item>строка таблицы — высота <c>RowHeightStandard</c> и линии <c>separator h</c>, как
///   таблица «Диагностики» и таблица конструктора.</item>
/// </list>
///
/// Ресурсы читаются через <see cref="Application.TryFindResource(object, out object?)"/> с
/// запасным значением: то же правило, что у прочих code-behind панелей (типографика в C# —
/// docs/developer/ui/theming.md).
/// </summary>
public static class SettingRows
{
    /// <summary>Ширина выпадающего списка в строке настройки — как у списков «Оформления».</summary>
    public const double PickerWidth = 240;

    // ── Ресурсы ───────────────────────────────────────────────────────────────

    public static IBrush Ink(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var found) && found is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));

    public static double Metric(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var found) && found is double value
            ? value
            : fallback;

    public static CornerRadius Radius(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var found) && found is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);

    // ── Строка настройки ──────────────────────────────────────────────────────

    public static TextBlock Note(string text) => new()
    {
        Classes      = { "desc" },
        Text         = text,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>
    /// Пояснение со сдвигом — подчиненная строка: пояснение под переключателем, подробность
    /// направления под его именем. Сдвиг отступом, а не пробелами в начале строки: пробелы
    /// уезжают при переносе длинной фразы, и второй ее строки сдвиг уже не касается.
    /// </summary>
    public static TextBlock SubNote(string text)
    {
        var note = Note(text);
        note.Margin = new Thickness(16, 0, 0, 0);

        return note;
    }

    /// <summary>
    /// Строка раздела: заголовок, пояснение и, если нужно, содержимое под ними. Так устроены
    /// строки, у которых под пояснением лежит список или несколько контролов сразу (как строка
    /// «Обновления» в «О программе»).
    /// </summary>
    public static Border Row(string title, string description, Control? content = null)
    {
        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new TextBlock { Classes = { "title" }, Text = title });

        if (description.Length > 0) block.Children.Add(Note(description));
        if (content is not null)
        {
            content.Margin = new Thickness(0, 4, 0, 0);
            block.Children.Add(content);
        }

        return new Border { Classes = { "settingRow" }, Child = block };
    }

    /// <summary>
    /// Строка с одним управляющим элементом справа — основная раскладка настроек: слева
    /// заголовок и пояснение, справа тумблер, список или кнопка. Отступ 32 слева от контрола и
    /// выравнивание по центру — те же, что в разметке остальных разделов.
    /// </summary>
    public static Border SideRow(string title, string description, Control side,
                                 Control? under = null)
    {
        var text = new StackPanel
        {
            Spacing           = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 32, 0),
        };

        text.Children.Add(new TextBlock { Classes = { "title" }, Text = title });
        if (description.Length > 0) text.Children.Add(Note(description));
        if (under is not null)
        {
            under.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(under);
        }

        side.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(side, 1);
        grid.Children.Add(text);
        grid.Children.Add(side);

        return new Border { Classes = { "settingRow" }, Child = grid };
    }

    /// <summary>Строка и разделитель под ней — как в разметке остальных разделов.</summary>
    public static void Add(Avalonia.Controls.Controls rows, Border row, bool last = false)
    {
        rows.Add(row);
        if (!last) rows.Add(Separator());
    }

    public static Border Separator() => new() { Classes = { "separator", "h" } };

    // ── Управляющие элементы ──────────────────────────────────────────────────

    /// <summary>
    /// Кнопка настроек. Общий стиль «dialog»; «auto» снимает фиксированную ширину 110,
    /// чтобы длинные подписи помещались целиком.
    /// </summary>
    public static Button RowButton(string text, Action click)
    {
        var button = new Button
        {
            Classes           = { "dialog", "auto" },
            Content           = text,
            VerticalAlignment = VerticalAlignment.Center,
        };

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>
    /// Ряд кнопок и мелких контролов (список отбора, галочка). Переносится, когда их больше,
    /// чем влезает в ширину: у карточки запроса действий до шести, и в узком окне они иначе
    /// съели бы описание целиком.
    ///
    /// Зазор висит СЛЕВА у всех, кроме первого, а не справа у всех: ряд бывает прижат и к
    /// левому краю строки, и к правому, и висящий с краю отступ в обоих случаях виден.
    /// </summary>
    public static WrapPanel ButtonLine(params Control[] items)
    {
        // Нижний зазор нужен только перенесенной строке; у единственной он превратился бы в
        // лишний воздух под рядом — снимаем его отрицательным отступом самой панели.
        var line = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Thickness(0, 0, 0, -6),
        };

        for (var i = 0; i < items.Length; i++)
        {
            items[i].Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 6);
            line.Children.Add(items[i]);
        }

        return line;
    }

    /// <summary>
    /// Выпадающий список настроек. Высота, фон и скругление — из общих токенов; ширина по
    /// умолчанию та же, что у списков «Оформления», чтобы столбец контролов был ровным.
    /// </summary>
    public static ComboBox Picker(IEnumerable<string> items, int selected,
                                  double width = PickerWidth)
    {
        var picker = new ComboBox
        {
            ItemsSource              = items.ToList(),
            SelectedIndex            = selected,
            Width                    = width,
            Height                   = Metric("ControlHeightStandard", 30),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background               = Ink("BrushSecondaryBg", "#F2F2F7"),
            CornerRadius             = Radius("RadiusBase", 6),
        };

        return picker;
    }

    /// <summary>Поле ввода настроек — как поле пути обновления в «О программе».</summary>
    public static TextBox Field(string text, string watermark = "",
                                double width = double.NaN, bool readOnly = false)
    {
        var box = new TextBox
        {
            Text                     = text,
            Watermark                = watermark,
            Width                    = width,
            Height                   = Metric("ControlHeightStandard", 30),
            IsReadOnly               = readOnly,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background               = Ink("BrushSecondaryBg", "#F2F2F7"),
            CornerRadius             = Radius("RadiusBase", 6),
        };

        return box;
    }

    /// <summary>
    /// Тумблер настроек. Подписи пустые: «On»/«Off» латиницей рядом с русским заголовком —
    /// след стандартной темы, и во всех остальных разделах их снимают.
    /// </summary>
    public static ToggleSwitch Toggle(bool on, bool enabled = true) => new()
    {
        IsChecked         = on,
        IsEnabled         = enabled,
        OnContent         = "",
        OffContent        = "",
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ── Ячейка с данными ──────────────────────────────────────────────────────

    /// <summary>
    /// Ячейка с данными: то, что программа показывает, — в такой же рамке, что и поле ввода, но
    /// серым и без нажатия. Сюда идут состояние приема, подробности строки
    /// журнала — все, что человек читает, а не правит.
    /// </summary>
    public static Border DataBox(Control content) => new()
    {
        Classes = { "dataBox" },
        Child   = content,
    };

    /// <summary>Ячейка из готовых строк — самый частый случай.</summary>
    public static Border DataBox(IEnumerable<Control> lines)
    {
        var list = new StackPanel { Spacing = 4 };

        foreach (var line in lines) list.Children.Add(line);

        return DataBox(list);
    }

    /// <summary>Ячейка из одной фразы.</summary>
    public static Border DataBox(string text) => DataBox(new[] { Note(text) });

    /// <summary>
    /// Моноширинная ячейка: пример файла, тело запроса и ответа. Высота ограничена, текст не
    /// переносится — ответ сторонней программы бывает на восемь тысяч знаков, и перенос по словам
    /// врет о том, что в нем написано.
    ///
    /// Раскладка по стандарту прокрутки (docs/developer/ui/scrolling.md): у рамки отступа НЕТ,
    /// воздух висит на тексте ВНУТРИ прокрутки. Иначе полоса отходит от краев ячейки и идет не
    /// во всю ее высоту, а сам воздух остается стоять, когда текст под ним уже уехал.
    /// <c>ClipToBounds</c> срезает полосы скруглением рамки.
    /// </summary>
    public static Border CodeBox(string text, bool selectable = true, double maxHeight = 180)
    {
        Control content = selectable
            ? new SelectableTextBlock { Classes = { "mono" }, Text = text, Margin = BoxPadding }
            : new TextBlock           { Classes = { "mono" }, Text = text, Margin = BoxPadding };

        return new Border
        {
            Classes      = { "dataBox" },
            Padding      = new Thickness(0),
            ClipToBounds = true,
            Child        = new ScrollViewer
            {
                MaxHeight                     = maxHeight,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content                       = content,
            },
        };
    }

    /// <summary>
    /// Воздух ячейки с данными. Тот же, что в стиле <c>Border.dataBox</c>: у прокручиваемой
    /// ячейки он переезжает с рамки на содержимое и обязан остаться прежним — иначе соседние
    /// ячейки на одном экране разойдутся по отступу.
    /// </summary>
    public static readonly Thickness BoxPadding = new(10, 8);

    // ── Таблица ───────────────────────────────────────────────────────────────

    public static TextBlock Head(string text) => new()
    {
        Classes           = { "desc" },
        Text              = text,
        FontWeight        = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Cell(string text, bool secondary = false)
    {
        var cell = new TextBlock
        {
            Text              = text,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (secondary) cell.Classes.Add("desc");

        return cell;
    }

    public static void Put(Grid grid, int column, Control cell)
    {
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    /// <summary>Шапка таблицы: высота строки и линия под ней — как у шапки рисованной сетки.</summary>
    public static Control TableHead(Grid columns)
    {
        var block = new StackPanel { Spacing = 0 };

        block.Children.Add(new Border { Classes = { "tableHead" }, Child = columns });
        block.Children.Add(Separator());

        return block;
    }

    /// <summary>
    /// Строка таблицы: сама строка, под ней — раскрытые подробности, под ними — линия. Нажатие
    /// ловит строка, а не весь блок: щелчок по подробностям закрывал бы их ровно в тот момент,
    /// когда человек в них вчитывается.
    /// </summary>
    public static Control TableRow(Grid columns, Control? details, bool open, Action click)
    {
        var row = new Border { Classes = { "tableRow" }, Child = columns };
        if (open) row.Classes.Add("open");

        row.PointerPressed += (_, _) => click();

        var block = new StackPanel { Spacing = 0, Children = { row } };

        if (details is not null)
        {
            details.Margin = new Thickness(0, 0, 0, 8);
            block.Children.Add(details);
        }

        block.Children.Add(Separator());

        return block;
    }

    // ── Списки карточек (соединения, запросы, сценарии) ───────────────────────

    /// <summary>Строка со списком действий сверху вкладки.</summary>
    public static Border ActionsRow(string first, Action onFirst,
                                    string? second = null, Action? onSecond = null,
                                    Control? third = null)
    {
        var buttons = new List<Control> { RowButton(first, onFirst) };

        if (second is not null && onSecond is not null) buttons.Add(RowButton(second, onSecond));
        if (third is not null) buttons.Add(third);

        return new Border { Classes = { "settingRow" }, Child = ButtonLine([.. buttons]) };
    }

    /// <summary>
    /// Строка карточки: имя, пояснение, вторая строка мелким и кнопки справа.
    /// <paramref name="first"/> — главное действие карточки, если оно есть; стоит перед
    /// «Изменить», потому что нажимают его чаще, чем правят саму карточку.
    /// <paramref name="lead"/> — управление слева от имени (тумблер сценария): выключить обмен
    /// нужно чаще, чем править его, и ради этого не должно открываться окно.
    /// </summary>
    public static Border CardRow(string title, string description, string extra,
                                 Action edit, Action delete,
                                 (string Text, Action Click)? first = null,
                                 Control? lead = null,
                                 IReadOnlyList<(string Text, Action Click)>? more = null)
    {
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Classes = { "title" }, Text = title });
        text.Children.Add(Note(description));
        if (extra.Length > 0) text.Children.Add(Note(extra));

        Control left = text;

        if (lead is not null)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
            Grid.SetColumn(lead, 0);
            Grid.SetColumn(text, 2);
            line.Children.Add(lead);
            line.Children.Add(text);
            left = line;
        }

        // Кнопок у карточки бывает до шести (исходящий запрос: «В коллекцию», «Что уйдет»,
        // «Что уносит», «Подтвердить», «Изменить», «Удалить»), и в узком окне они съели бы
        // описание целиком. ButtonLine переносит лишние на вторую строку, а MinWidth левой
        // колонки не дает описанию исчезнуть: строка, из которой не видно, о чем она, бесполезна.
        var buttons = new List<Control>();

        if (first is { } main) buttons.Add(RowButton(main.Text, main.Click));

        foreach (var action in more ?? []) buttons.Add(RowButton(action.Text, action.Click));

        buttons.Add(RowButton("Изменить", edit));
        buttons.Add(RowButton("Удалить", delete));

        var line2 = ButtonLine([.. buttons]);
        line2.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.ColumnDefinitions[0].MinWidth = 220;

        left.Margin = new Thickness(0, 0, 24, 0);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(line2, 1);
        grid.Children.Add(left);
        grid.Children.Add(line2);

        return new Border { Classes = { "settingRow" }, Child = grid };
    }

    // ── Строка списка (как список баз в «Управлении базой данных») ─────────────

    /// <summary>
    /// Сторона иконки-кнопки строки списка — как шестеренка и корзина у базы в «Управлении базой
    /// данных» (<c>SettingsView.axaml</c>).
    /// </summary>
    public const double IconButtonSize = 28;

    /// <summary>Геометрия из общего набора иконок (<c>Assets/Styles/Icons.axaml</c>); нет такой —
    /// пустая иконка, а не падение экрана.</summary>
    public static Geometry? IconData(string key) =>
        Application.Current!.TryFindResource(key, out var found) && found is Geometry geometry
            ? geometry
            : null;

    /// <summary>
    /// Иконка набора: рисунок 24×24 в рамке размера <paramref name="size"/> и штрихом
    /// <paramref name="stroke"/> — те же пары токенов, что в разметке остальных разделов.
    /// </summary>
    public static Viewbox Icon(string key, IBrush brush,
                               string size = "IconSizeMedium", string stroke = "IconStrokeMedium")
    {
        var path = IconPath(key, stroke);
        path.Stroke = brush;

        return IconBox(path, size);
    }

    /// <summary>
    /// Иконка-кнопка строки списка: 28×28, иконка <c>IconSizeSmall</c>, подсказка обязательна —
    /// кнопка без подписи иначе ничего о себе не говорит. <paramref name="red"/> — разрушительное
    /// действие (корзина): под курсором кнопка краснеет, а не синеет.
    /// </summary>
    public static Button IconButton(string icon, string tip, Action click, bool red = false)
    {
        var path = IconPath(icon, "IconStrokeSmall");

        var button = new Button
        {
            Classes           = { "icon" },
            Width             = IconButtonSize,
            Height            = IconButtonSize,
            VerticalAlignment = VerticalAlignment.Center,
            Content           = IconBox(path, "IconSizeSmall"),
        };

        if (red) button.Classes.Add("red");

        // Цвет штриха — цвет самой кнопки: стиль Button.icon перекрашивает ее под курсором, и
        // иконка обязана перекраситься вместе с ней (в разметке — $parent[Button].Foreground).
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
                  button.GetObservable(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty));

        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();

        return button;
    }

    /// <summary>
    /// Строка списка: слева иконка, посередине строки текста одна под другой, справа кнопки,
    /// прижатые к ПРАВОМУ краю. Кнопок кладется ровно столько, сколько передано: пустых мест под
    /// отсутствующие нет, иначе кнопки соседних строк стояли бы не у края, а где придется.
    /// Отступы — как у строки базы в «Управлении базой данных».
    /// </summary>
    public static Border ListRow(Control lead, IReadOnlyList<Control> lines,
                                 IReadOnlyList<Control> actions)
    {
        lead.VerticalAlignment = VerticalAlignment.Center;
        lead.Margin            = new Thickness(0, 0, 12, 0);

        var text = new StackPanel
        {
            Spacing           = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, actions.Count > 0 ? 16 : 0, 0),
        };

        foreach (var line in lines) text.Children.Add(line);

        var side = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        foreach (var action in actions) side.Children.Add(action);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Put(grid, 0, lead);
        Put(grid, 1, text);
        Put(grid, 2, side);

        return new Border { Classes = { "settingRow" }, Child = grid };
    }

    private static Avalonia.Controls.Shapes.Path IconPath(string key, string stroke) => new()
    {
        Data            = IconData(key),
        StrokeThickness = Metric(stroke, 1.6),
        StrokeLineCap   = PenLineCap.Round,
        StrokeJoin      = PenLineJoin.Round,
    };

    // Рисунки набора иконок нарисованы в квадрате 24×24 (lucide) — это система координат
    // геометрии, а не размер на экране: размер задает рамка.
    private static Viewbox IconBox(Control path, string size) => new()
    {
        Width  = Metric(size, 16),
        Height = Metric(size, 16),
        Child  = new Canvas { Width = 24, Height = 24, Children = { path } },
    };

    // ── Числа ─────────────────────────────────────────────────────────────────

    public static string Size(long bytes) => Sizes.Text(bytes);

    /// <inheritdoc cref="Sizes.List"/>
    public static string Join(IReadOnlyList<string> values) => Sizes.List(values);
}
