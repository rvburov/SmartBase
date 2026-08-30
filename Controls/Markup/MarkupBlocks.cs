using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Image = Avalonia.Controls.Image;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Controls.Markup;

/// <summary>
/// Блочная разметка: дерево Markdig превращается в контролы Avalonia внутри вертикальной стопки.
///
/// Просвет между блоками равен ПУСТЫМ СТРОКАМ ИСХОДНИКА: одна пустая строка — один просвет
/// высотой строки, две — два, ни одной — ничего. Отбивать каждый блок одинаково нельзя: в
/// редакторе заметок просвет дают настоящие пустые строки файла, и «Заголовок» с идущим сразу
/// за ним абзацем стоят там вплотную. Ставя блокам отбивку «за то, что они блоки», мы разводили
/// два режима показа на ровном месте.
///
/// Отступ всегда СНИЗУ и никогда сверху — иначе отступы складываются попарно, — и левый отступ
/// при этом не трогается: его задает сам блок (уровень вложенного списка).
/// </summary>
internal sealed class MarkupBlocks
{
    private readonly bool                 _selectable;
    private readonly EventHandler<string> _onLink;
    private readonly string?              _basePath;

    /// <summary>Начала строк исходника: по ним считается просвет между блоками.</summary>
    private readonly int[] _lineStarts;

    /// <summary>Глубина вложенности списка: от нее зависят и отступ уровня, и вид маркера.
    /// Держится полем, потому что вложенный список приходит обычным блоком содержимого пункта
    /// и по дереву Markdig своей глубины не знает.</summary>
    private int _listDepth;

    private MarkupBlocks(string text, bool selectable, EventHandler<string> onLink, string? basePath)
    {
        _selectable = selectable;
        _onLink     = onLink;
        _basePath   = basePath;

        var starts = new List<int> { 0 };
        for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1)) starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }

    /// <summary>Разложить документ по детям панели. Панель предварительно чистится.</summary>
    public static void Fill(Panel host, string text, MarkdownObject? document, bool selectable,
                            EventHandler<string> onLink, string? basePath)
    {
        host.Children.Clear();
        if (document is not ContainerBlock root) return;

        new MarkupBlocks(text, selectable, onLink, basePath).AddAll(host.Children, root, MarkupFont.Base);
    }

    // ── Разбор блока ────────────────────────────────────────────────────────────

    /// <summary>Разложить подряд идущие блоки, отбивая каждый по пустым строкам исходника.</summary>
    private void AddAll(Avalonia.Controls.Controls into, IList<Block> blocks, MarkupFont font)
    {
        for (var i = 0; i < blocks.Count; i++)
            Add(into, blocks[i], font, Gap(blocks[i], i + 1 < blocks.Count ? blocks[i + 1] : null));
    }

    private void Add(Avalonia.Controls.Controls into, Block block, MarkupFont font, double gap)
    {
        var control = Build(block, font);
        if (control is null) return;

        Space(control, gap);
        into.Add(control);
    }

    /// <summary>Отбивка снизу с сохранением левого отступа блока.</summary>
    private static void Space(Control control, double bottom) =>
        control.Margin = new Thickness(control.Margin.Left, 0, 0, bottom);

    /// <summary>
    /// Просвет до следующего блока: столько строк, сколько пустых строк между ними в файле.
    /// Последний блок стопки просвета не получает — воздух снизу задает тот, кто поставил виджет.
    ///
    /// Заголовок — исключение: вокруг него просвет есть всегда, даже когда в файле пустой строки
    /// нет. Иначе он сливается с текстом и перестает делить его на части.
    /// </summary>
    private double Gap(Block current, Block? next)
    {
        if (next is null) return 0;

        var blank = Math.Max(0, LineOf(next.Span.Start) - LineOf(current.Span.End) - 1) * MarkupTheme.LineHeight;

        var least = current is HeadingBlock || next is HeadingBlock ? MarkupTheme.HeadingSpace : 0;

        return Math.Max(blank, least);
    }

    /// <summary>Номер строки, в которой лежит смещение. Двоичный поиск по началам строк.</summary>
    private int LineOf(int offset)
    {
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private Control? Build(Block block, MarkupFont font) => block switch
    {
        ParagraphBlock p => Paragraph(p, font),
        HeadingBlock h   => Heading(h),
        ListBlock l      => List(l, font),
        QuoteBlock q     => Quote(q, font),
        CodeBlock c      => Code(c),
        ThematicBreakBlock => Rule(),
        Table t          => TableGrid(t, font),

        // Сырой HTML в наших текстах не встречается, а показанный как есть читался бы мусором.
        // Определения ссылок ([метка]: адрес) — служебные, их место в тексте пустое.
        HtmlBlock                    => null,
        LinkReferenceDefinitionGroup => null,
        LinkReferenceDefinition      => null,

        ContainerBlock other => Stack(other, font),
        _                    => null,
    };

    /// <summary>Контейнер без своего вида (например, содержимое ячейки таблицы).</summary>
    private Control Stack(ContainerBlock container, MarkupFont font)
    {
        var panel = new StackPanel();
        AddAll(panel.Children, container, font);
        return panel;
    }

    // ── Текстовые блоки ─────────────────────────────────────────────────────────

    private MarkupTextBlock Text()
    {
        var block = new MarkupTextBlock { IsSelectable = _selectable };
        block.LinkActivated += _onLink;
        return block;
    }

    private Control Paragraph(ParagraphBlock paragraph, MarkupFont font)
    {
        // Абзац из одной картинки — это картинка, а не текст: так ее в разметке и ставят.
        if (paragraph.Inline?.FirstChild is LinkInline { IsImage: true } image && image.NextSibling is null)
            return Picture(image, font);

        var block = Text();
        MarkupInlines.Fill(block, paragraph.Inline, font);
        return block;
    }

    private Control Heading(HeadingBlock heading)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var size  = MarkupTheme.HeadingSize(level);

        var block = Text();
        // Высота строки у заголовка ТА ЖЕ, что у обычного текста: в редакторе заметок она одна
        // на все строки и не меняется ничем, а режимы обязаны совпадать. Крупные глифы в нее не
        // влезают и выступают — ровно как в редакторе, — поэтому обрезку по границам блока
        // приходится снимать: иначе у заголовка пропадают верх и низ. Выступать есть куда:
        // воздух в строку вокруг заголовка есть всегда (см. Gap).
        block.ClipToBounds = false;
        MarkupInlines.Fill(block, heading.Inline, MarkupFont.Base with
        {
            Size   = size,
            Weight = MarkupTheme.WeightBold,
            Brush  = MarkupTheme.HeadingBrush(level),
        });
        return block;
    }

    // ── Горизонтальная черта ────────────────────────────────────────────────────

    /// <summary>
    /// Линия — пустая рамка без содержимого: <c>EdgeMode.Aliased</c> наследуется на все поддерево
    /// и отключает сглаживание ТЕКСТА, поэтому вешать линию на контейнер, в котором живет текст,
    /// нельзя (правило проекта, см. ui/theming.md). Толщина — из динамических токенов, иначе при
    /// зуме и на Retina линия то пропадает, то удваивается.
    /// </summary>
    private static Control Rule()
    {
        var line = new Border
        {
            BorderBrush         = MarkupTheme.Separator,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        line.Bind(Border.BorderThicknessProperty,       line.GetResourceObservable("BorderLineBottom"));
        line.Bind(Layoutable.UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
        RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
        return line;
    }

    // ── Блок кода ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Карточка кода. Строки переносятся, а не уезжают под горизонтальную полосу: своя полоса
    /// внутри блока спорила бы со стандартом прокрутки (полоса одна и идет во всю высоту блока),
    /// а обрезанная строка кода — это потерянный текст.
    /// </summary>
    private Control Code(CodeBlock code)
    {
        var text = Lines(code);

        var body = Text();
        MarkupInlines.Fill(body, text, MarkupFont.Base with
        {
            Family = MarkupTheme.FontMono,
            Size   = MarkupTheme.SizeMono,
        });

        var corner = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
        };

        // Язык — подписью в углу: он часть содержимого («это bash, а не C#»), но не текст кода,
        // поэтому мелким вторичным.
        if (Language(code) is { Length: > 0 } language)
            corner.Children.Add(new TextBlock
            {
                Text              = language,
                FontSize          = MarkupTheme.SizeSmall,
                Foreground        = MarkupTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            });

        // Кнопка лежит ПОВЕРХ кода и проявляется под курсором. Своей строкой она добавила бы
        // каждому блоку лишние двадцать пикселей высоты, а блоки в инструкции сплошь
        // однострочные — команда, путь, ключ.
        var copy = CopyButton(text);
        copy.Opacity = 0;
        corner.Children.Add(copy);

        var grid = new Grid();
        grid.Children.Add(body);
        grid.Children.Add(corner);

        var card = new Border
        {
            Background   = MarkupTheme.CardBg,
            CornerRadius = MarkupTheme.RadiusBase,
            Padding      = MarkupTheme.CodeBlockPadding,
            Child        = grid,
        };
        card.PointerEntered += (_, _) => copy.Opacity = 1;
        card.PointerExited  += (_, _) => copy.Opacity = 0;
        return card;
    }

    private static string Lines(CodeBlock code)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < code.Lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(code.Lines.Lines[i].Slice.ToString());
        }
        return builder.ToString();
    }

    private static string? Language(CodeBlock code) => (code as FencedCodeBlock)?.Info;

    private static Control CopyButton(string text)
    {
        var button = new Button
        {
            Classes = { "icon" },
            Padding = new Thickness(2),
            Content = Icon("IconClipboard", MarkupTheme.TextSecondary, MarkupTheme.CalloutIconSize),
        };
        ToolTip.SetTip(button, "Скопировать");
        button.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        };
        return button;
    }

    // ── Цитата и выносные блоки ─────────────────────────────────────────────────

    /// <summary>Вид выносного блока по метке <c>[!note]</c>: цвет, знак и подпись по умолчанию.</summary>
    private static (IBrush Brush, string Icon, string Title)? CalloutKind(string tag) => tag switch
    {
        "note" or "info"             => (MarkupTheme.Accent,                   "IconInfo",          "Заметка"),
        "tip" or "hint" or "success" => (Brush("BrushGreen",  Brushes.Green),  "IconLightbulb",     "Совет"),
        "warning" or "caution"       => (Brush("BrushOrange", Brushes.Orange), "IconTriangleAlert", "Внимание"),
        "danger" or "error" or "bug" => (Brush("BrushRed",    Brushes.Red),    "IconOctagonAlert",  "Опасность"),
        _                            => null,
    };

    private Control Quote(QuoteBlock quote, MarkupFont font)
    {
        if (quote.FirstOrDefault() is ParagraphBlock head && TakeCalloutTag(head) is { } kind)
            return Callout(quote, head, kind, font);

        var body = new StackPanel();
        AddAll(body.Children, quote, font);

        return new Border
        {
            Background      = MarkupTheme.CardBg,
            BorderBrush     = MarkupTheme.QuoteBar,
            BorderThickness = new Thickness(MarkupTheme.QuoteBarWidth, 0, 0, 0),
            // Скругление только справа: слева граница — полоса, и круглый угол оторвал бы ее
            // от края карточки.
            CornerRadius    = new CornerRadius(0, MarkupTheme.RadiusBase.TopRight,
                                               MarkupTheme.RadiusBase.BottomRight, 0),
            Padding         = MarkupTheme.QuotePadding,
            Child           = body,
        };
    }

    private Control Callout(QuoteBlock quote, ParagraphBlock head,
                            (IBrush Brush, string Icon, string Title) kind, MarkupFont font)
    {
        var body = new StackPanel();

        // Зачин — жирным рядом со знаком. Пустой зачин заменяем названием вида: «> [!note]»
        // без текста иначе дал бы карточку с одним значком.
        var title = Text();
        var rest  = MarkupInlines.Fill(title, head.Inline?.FirstChild,
                                       font with { Weight = MarkupTheme.WeightBold }, untilBreak: true);
        if (title.Inlines is null or { Count: 0 })
            MarkupInlines.Fill(title, kind.Title, font with { Weight = MarkupTheme.WeightBold });

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(Icon(kind.Icon, kind.Brush, MarkupTheme.CalloutIconSize));
        header.Children.Add(title);
        body.Children.Add(header);

        // Остаток первого абзаца — уже обычным текстом, отдельной строкой.
        if (rest is not null)
        {
            var tail = Text();
            MarkupInlines.Fill(tail, rest, font);
            body.Children.Add(tail);
        }

        // Зачин от текста отбивается всегда: в исходнике они одна строка, и без просвета
        // карточка читалась бы сплошняком.
        for (var i = 0; i < body.Children.Count; i++) Space(body.Children[i], MarkupTheme.LineHeight);

        for (var i = 1; i < quote.Count; i++)
            Add(body.Children, quote[i], font, Gap(quote[i], i + 1 < quote.Count ? quote[i + 1] : null));

        if (body.Children.Count > 0) Space(body.Children[^1], 0);

        return new Border
        {
            Background   = Tint(kind.Brush),
            CornerRadius = MarkupTheme.RadiusMedium,
            Padding      = MarkupTheme.CalloutPadding,
            Child        = body,
        };
    }

    /// <summary>Подложка выносного блока — цвет вида, разбавленный до фона.</summary>
    private static IBrush Tint(IBrush source) =>
        source is ISolidColorBrush s ? new SolidColorBrush(s.Color, MarkupTheme.CalloutTint) : source;

    private static readonly Regex CalloutPattern = new(@"^\[!([A-Za-z]+)\]\s*", RegexOptions.Compiled);

    /// <summary>
    /// Метка вида в начале цитаты. Найденную и знакомую — СЪЕДАЕТ: сдвигает начало литералов,
    /// чтобы «[!note]» не осталось на экране. Незнакомую не трогает — тогда блок покажется
    /// обычной цитатой вместе с меткой, и человек увидит опечатку, а не пустое место.
    /// </summary>
    private static (IBrush, string, string)? TakeCalloutTag(ParagraphBlock paragraph)
    {
        var literals = new List<LiteralInline>();
        var text     = new StringBuilder();

        // Markdig рвет «[!note]» на несколько литералов: открывающая скобка без пары становится
        // отдельным узлом. Поэтому смотрим на цепочку, а не на первый узел.
        for (var node = paragraph.Inline?.FirstChild; node is LiteralInline literal && text.Length < 32;
             node = node.NextSibling)
        {
            literals.Add(literal);
            text.Append(literal.Content.ToString());
        }

        var match = CalloutPattern.Match(text.ToString());
        if (!match.Success) return null;

        var kind = CalloutKind(match.Groups[1].Value.ToLowerInvariant());
        if (kind is null) return null;

        var eat = match.Length;
        foreach (var literal in literals)
        {
            var slice = literal.Content;
            if (eat >= slice.Length)
            {
                eat -= slice.Length;
                literal.Content = new StringSlice(slice.Text, slice.End + 1, slice.End);   // пустой
            }
            else
            {
                literal.Content = new StringSlice(slice.Text, slice.Start + eat, slice.End);
                break;
            }
        }
        return kind;
    }

    // ── Списки ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Список. Каждый пункт — сетка из двух колонок: маркер в первой, содержимое во второй.
    /// Ширина колонки маркера и есть «висячий» отступ: вторая строка пункта встает под первую,
    /// а не под маркер.
    ///
    /// Уровень вложенности сдвигает пункт на <see cref="MarkupTheme.ListIndent"/>, а не на всю
    /// колонку маркера: иначе каждый уровень уезжал бы на 24 пикселя и третий упирался бы в
    /// середину строки. Сдвиг отрицательный, потому что вложенный список и так уже лежит в
    /// колонке содержимого родителя.
    /// </summary>
    private Control List(ListBlock list, MarkupFont font)
    {
        var depth  = _listDepth++;
        var stack  = new StackPanel();
        var number = list.IsOrdered ? Start(list) : 0;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not ListItemBlock block) continue;

            var row = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition(MarkupTheme.ListIndent, GridUnitType.Pixel),
                    new ColumnDefinition(GridLength.Star),
                ],
            };

            var content = new StackPanel
            {
                Margin = new Thickness(MarkupTheme.ListMarkerColumn - MarkupTheme.ListIndent, 0, 0, 0),
            };
            Grid.SetColumn(content, 1);

            var check = TaskState(block);
            AddAll(content.Children, block, check == true ? font with { Brush = MarkupTheme.TaskDoneText } : font);

            var marker = check is { } state ? CheckBox(state)
                       : list.IsOrdered     ? Number(number++, list.OrderedDelimiter, font)
                                            : Bullet(depth);
            Grid.SetColumn(marker, 0);

            row.Children.Add(marker);
            row.Children.Add(content);

            row.Margin = new Thickness(0, 0, 0, Gap(block, i + 1 < list.Count ? list[i + 1] : null));
            stack.Children.Add(row);
        }

        if (stack.Children.Count > 0) Space(stack.Children[^1], 0);
        _listDepth--;

        if (depth > 0)
            stack.Margin = new Thickness(MarkupTheme.ListIndent - MarkupTheme.ListMarkerColumn, 0, 0, 0);
        return stack;
    }

    private static int Start(ListBlock list) =>
        int.TryParse(list.OrderedStart, out var value) ? value : 1;

    /// <summary>Высота первой строки пункта, по центру которой встает маркер.</summary>
    private static double FirstLine => MarkupTheme.LineHeight;

    /// <summary>
    /// Первый уровень — залитый кружок, второй — колечко того же диаметра, третий и дальше —
    /// черточка. Круглых форм на три уровня не хватает, а разводить их размером нельзя: список
    /// стал бы выглядеть как список разного.
    /// </summary>
    private static Control Bullet(int depth) => depth switch
    {
        0 => Marker(new Ellipse
        {
            Width  = MarkupTheme.BulletSize,
            Height = MarkupTheme.BulletSize,
            Fill   = MarkupTheme.ListMarker,
        }, MarkupTheme.BulletSize),

        1 => Marker(new Ellipse
        {
            Width           = MarkupTheme.BulletSize,
            Height          = MarkupTheme.BulletSize,
            Stroke          = MarkupTheme.ListMarker,
            StrokeThickness = 1,
        }, MarkupTheme.BulletSize),

        _ => Marker(new Rectangle
        {
            Width  = MarkupTheme.DashWidth,
            Height = MarkupTheme.DashHeight,
            Fill   = MarkupTheme.ListMarker,
        }, MarkupTheme.DashHeight),
    };

    /// <summary>Маркер по центру колонки и по центру первой строки текста.</summary>
    private static Control Marker(Control shape, double height)
    {
        shape.HorizontalAlignment = HorizontalAlignment.Center;
        shape.VerticalAlignment   = VerticalAlignment.Top;
        shape.Margin              = new Thickness(0, Math.Max(0, (FirstLine - height) / 2), 0, 0);
        return shape;
    }

    /// <summary>Номер прижат вправо: так у длинного списка единицы стоят под единицами, а не
    /// разъезжаются от «9.» к «10.».</summary>
    private static Control Number(int value, char delimiter, MarkupFont font) => new TextBlock
    {
        Text                = $"{value}{delimiter}",
        FontFamily          = font.Family,
        FontSize            = font.Size,
        Foreground          = MarkupTheme.ListMarker,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment   = VerticalAlignment.Top,
    };

    // ── Список задач ────────────────────────────────────────────────────────────

    /// <summary>Состояние флажка пункта, либо null — обычный пункт списка.</summary>
    private static bool? TaskState(ListItemBlock item)
    {
        if (item.FirstOrDefault() is not ParagraphBlock paragraph) return null;
        if (paragraph.Inline?.FirstChild is not TaskList task) return null;

        // Пробел после «[x]» — часть разметки, а не текста: без него пункт начинался бы отступом.
        if (task.NextSibling is LiteralInline literal)
        {
            var slice = literal.Content;
            if (slice.Length > 0 && slice.Text[slice.Start] == ' ')
                literal.Content = new StringSlice(slice.Text, slice.Start + 1, slice.End);
        }
        return task.Checked;
    }

    /// <summary>
    /// Флажок — своя фигура, а не <c>CheckBox</c> приложения: тот заведомо крупнее строки текста
    /// и тянет за собой поведение (фокус, наведение, отступы), которого в тексте быть не должно.
    /// Сделанный пункт при этом только гаснет, но не зачеркивается: зачеркнутый текст нечитаем,
    /// а список задач перечитывают.
    /// </summary>
    private static Control CheckBox(bool state)
    {
        var box = new Border
        {
            Width               = MarkupTheme.CheckBoxSize,
            Height              = MarkupTheme.CheckBoxSize,
            CornerRadius        = new CornerRadius(MarkupTheme.CheckBoxRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, Math.Max(0, (FirstLine - MarkupTheme.CheckBoxSize) / 2), 0, 0),
        };

        if (state)
        {
            box.Background = MarkupTheme.Accent;
            // Галочка — та же геометрия, что у чекбокса приложения: знак «сделано» в программе
            // один, и рисовать его вторым путем нельзя.
            box.Child = new Path
            {
                Data            = Glyph("CheckBoxGlyphGeometry"),
                Stroke          = Brushes.White,
                StrokeThickness = MarkupTheme.CheckTickWidth,
                StrokeLineCap   = PenLineCap.Round,
                StrokeJoin      = PenLineJoin.Round,
                Stretch         = Stretch.Uniform,
                Margin          = new Thickness(3),
            };
        }
        else
        {
            box.BorderBrush     = MarkupTheme.CheckBoxLine;
            box.BorderThickness = new Thickness(1);
        }
        return box;
    }

    // ── Картинка ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Картинка из файла рядом с документом или из ресурсов приложения. Из сети НЕ грузим:
    /// показ текста не должен ходить в интернет и ждать ответа — вместо картинки остается ее
    /// подпись, как и у неудачной загрузки с диска.
    /// </summary>
    private Control Picture(LinkInline image, MarkupFont font)
    {
        var bitmap = Load(image.Url);
        if (bitmap is null)
        {
            var caption = Text();
            MarkupInlines.Fill(caption, image, font with { Brush = MarkupTheme.TextSecondary });
            return caption;
        }

        return new Border
        {
            CornerRadius        = MarkupTheme.RadiusBase,
            ClipToBounds        = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Image
            {
                Source  = bitmap,
                // Растягивать вверх нельзя: мелкая картинка размылась бы во всю ширину блока.
                Stretch = Stretch.Uniform,
                MaxWidth = bitmap.Size.Width,
            },
        };
    }

    private Bitmap? Load(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            if (url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
                return new Bitmap(AssetLoader.Open(new Uri(url)));

            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

            var path = System.IO.Path.IsPathRooted(url) || _basePath is null
                ? url
                : System.IO.Path.Combine(_basePath, url);
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch
        {
            // Картинка — украшение: битый файл или чужой формат не должны валить показ текста.
            return null;
        }
    }

    // ── Таблица ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Таблица без внешней рамки и без вертикальных линий: колонки разделяет воздух, строки —
    /// тонкая линия. Так же устроены таблицы приложения, и на длинном справочном тексте это
    /// читается спокойнее сетки.
    ///
    /// Колонки делят ширину поровну. Ширина по содержимому (Auto) оставляла бы таблицу узкой
    /// полосой посреди широкого окна, а угадать «главную» колонку разметка не дает.
    /// </summary>
    private Control TableGrid(Table table, MarkupFont font)
    {
        var rows    = table.OfType<TableRow>().ToList();
        var columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        if (columns == 0) return new StackPanel();

        var grid = new Grid();
        for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            for (var c = 0; c < row.Count; c++)
            {
                if (row[c] is not TableCell cell) continue;

                var content = new StackPanel();
                AddAll(content.Children, cell, row.IsHeader ? font with { Weight = MarkupTheme.WeightBold } : font);

                foreach (var block in content.Children.OfType<MarkupTextBlock>())
                    block.TextAlignment = Alignment(table, c);

                var box = new Border
                {
                    Padding     = MarkupTheme.TableCellPadding,
                    BorderBrush = MarkupTheme.Separator,
                    Background  = row.IsHeader ? MarkupTheme.TableHeaderBg : null,
                    Child       = content,
                };
                // Линию рисует только нижняя граница ячейки — одна линия, один источник.
                box.Bind(Border.BorderThicknessProperty,       box.GetResourceObservable("BorderLineBottom"));
                box.Bind(Layoutable.UseLayoutRoundingProperty, box.GetResourceObservable("LineUseLayoutRounding"));

                // Скругление — только по верхним углам шапки: подложка шапки должна читаться
                // карточкой, а не полосой, упирающейся в края.
                if (row.IsHeader)
                    box.CornerRadius = new CornerRadius(
                        c == 0           ? MarkupTheme.RadiusBase.TopLeft  : 0,
                        c == columns - 1 ? MarkupTheme.RadiusBase.TopRight : 0, 0, 0);

                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                Grid.SetColumnSpan(box, Math.Max(1, cell.ColumnSpan));
                grid.Children.Add(box);
            }
        }
        return grid;
    }

    private static TextAlignment Alignment(Table table, int column) =>
        column < table.ColumnDefinitions.Count
            ? table.ColumnDefinitions[column].Alignment switch
            {
                TableColumnAlign.Center => TextAlignment.Center,
                TableColumnAlign.Right  => TextAlignment.Right,
                _                       => TextAlignment.Left,
            }
            : TextAlignment.Left;

    // ── Мелочи ──────────────────────────────────────────────────────────────────

    private static IBrush Brush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : fallback;

    /// <summary>Обводочная иконка по канону приложения: Viewbox поверх сетки 24×24
    /// (см. <c>Controls/Components/MenuIcons.cs</c>), выровненная по первой строке текста.</summary>
    /// <summary>
    /// Геометрия из общего реестра (<c>Assets/Styles/Icons.axaml</c>). Приложения может не быть
    /// вовсе (тесты рендера) — тогда знак просто не рисуется. А вот промах ключа при живом
    /// приложении — опечатка, и молчать о ней нельзя: на экране от нее остается пустое место
    /// (правило реестра, см. Controls/Components/MenuIcons.cs).
    /// </summary>
    private static Geometry? Glyph(string key) =>
        Application.Current is not { } app ? null
        : app.TryFindResource(key, out var res) && res is Geometry geometry ? geometry
        : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    private static Control Icon(string key, IBrush stroke, double size)
    {
        var data = Glyph(key);

        return new Viewbox
        {
            Width             = size,
            Height            = size,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(0, Math.Max(0, (FirstLine - size) / 2), 0, 0),
            Child = new Canvas
            {
                Width  = 24,
                Height = 24,
                Children =
                {
                    new Path
                    {
                        Data            = data,
                        Stroke          = stroke,
                        StrokeThickness = Stroke("IconStrokeSmall", 1.8),
                        StrokeLineCap   = PenLineCap.Round,
                        StrokeJoin      = PenLineJoin.Round,
                    },
                },
            },
        };
    }

    private static double Stroke(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is double d ? d : fallback;
}
