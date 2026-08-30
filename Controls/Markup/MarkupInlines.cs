using Avalonia.Controls.Documents;
using Avalonia.Media;
using Markdig.Syntax.Inlines;
// Inline есть и в Avalonia (прогон текста), и в Markdig (узел разметки). Псевдоним разводит
// их по именам — без него в этом файле не написать ни одной сигнатуры.
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace SmartBase.Controls.Markup;

/// <summary>Начертание куска текста. Наследуется вглубь и меняется по одной оси за раз —
/// иначе «**_жирный курсив_**» терял бы одно из двух.</summary>
internal readonly record struct MarkupFont(
    FontFamily Family,
    double     Size,
    FontWeight Weight,
    FontStyle  Style,
    IBrush     Brush,
    bool       Strike = false)
{
    /// <summary>Начертание обычного текста приложения.</summary>
    public static MarkupFont Base => new(MarkupTheme.FontBase, MarkupTheme.SizeBase,
                                         FontWeight.Normal, FontStyle.Normal, MarkupTheme.TextPrimary);
}

/// <summary>
/// Строчная разметка: узлы Markdig внутри абзаца превращаются в прогоны текста
/// (<see cref="Run"/>) одного <see cref="MarkupTextBlock"/>.
///
/// Все, что видно, — прогоны, и только прогоны. Ни одного вложенного контрола: их нельзя
/// перенести по словам, и смещения символов после них перестают совпадать с раскладкой, а на
/// смещениях держатся и коробки кода, и клик по ссылке. Поэтому коробка и подсветка
/// записываются диапазоном (<see cref="MarkupTextBlock.AddBox"/>), а не подкладываются рамкой.
/// </summary>
internal sealed class MarkupInlines
{
    /// <summary>Неразрывный пробел — поле внутри коробки кода (см. <see cref="Code"/>).</summary>
    private const string Nbsp = "\u00A0";

    private readonly MarkupTextBlock  _block;
    private readonly InlineCollection _into;

    /// <summary>Сколько символов уже выдано. Это же — смещение в тексте раскладки: прогоны
    /// склеиваются подряд, и других участников у текста нет.</summary>
    private int _offset;

    /// <summary>Прогоны текущей ссылки — их гасят на время нажатия. Вне ссылки null.</summary>
    private List<Run>? _collecting;

    private MarkupInlines(MarkupTextBlock block)
    {
        _block = block;
        _into  = block.Inlines ??= [];
    }

    /// <summary>Заполнить блок содержимым узла-контейнера Markdig.</summary>
    public static void Fill(MarkupTextBlock block, ContainerInline? source, MarkupFont font) =>
        Fill(block, source?.FirstChild, font);

    /// <summary>
    /// Заполнить блок цепочкой узлов, начиная с указанного.
    /// </summary>
    /// <param name="untilBreak">
    /// Остановиться на первом переводе строки. Нужно выносным блокам: у <c>&gt; [!note] Зачин</c>
    /// с продолжением Markdig держит зачин и текст ОДНИМ абзацем, разделенным мягким переносом,
    /// — иначе заголовок выносного блока пришлось бы искать по сырому тексту.
    /// </param>
    /// <returns>Узел, на котором остановились (следующий за переносом), либо null.</returns>
    public static MdInline? Fill(MarkupTextBlock block, MdInline? from, MarkupFont font, bool untilBreak = false) =>
        new MarkupInlines(block).Walk(from, font, untilBreak);

    /// <summary>Простой текст без разметки — тем же путем, чтобы вид совпадал до пикселя.</summary>
    public static void Fill(MarkupTextBlock block, string text, MarkupFont font) =>
        new MarkupInlines(block).Emit(text, font);

    private MdInline? Walk(MdInline? from, MarkupFont font, bool untilBreak = false)
    {
        for (var node = from; node is not null; node = node.NextSibling)
        {
            if (untilBreak && node is LineBreakInline) return node.NextSibling;
            Node(node, font);
        }
        return null;
    }

    private void Walk(ContainerInline container, MarkupFont font) => Walk(container.FirstChild, font);

    private void Node(MdInline node, MarkupFont font)
    {
        switch (node)
        {
            case LiteralInline literal:
                Emit(literal.Content.ToString(), font);
                break;

            case EmphasisInline emphasis:
                Emphasis(emphasis, font);
                break;

            case CodeInline code:
                Code(code.Content ?? "", font);
                break;

            // Картинка внутри абзаца остается подписью: показывает ее блок, а тут от нее
            // осталось бы пустое место посреди строки.
            case LinkInline { IsImage: true } image:
                Walk(image, font);
                break;

            case LinkInline link:
                Link(link, link.GetDynamicUrl?.Invoke() ?? link.Url ?? "", font);
                break;

            case AutolinkInline auto:
                Link(null, auto.Url, font);
                break;

            // Жесткий перенос — настоящий символ строки, а не элемент LineBreak: элемент не
            // добавляет текста, и все смещения после него разъехались бы с раскладкой.
            case LineBreakInline line:
                Emit(line.IsHard ? "\n" : " ", font);
                break;

            case HtmlEntityInline entity:
                Emit(entity.Transcoded.ToString(), font);
                break;

            // Сырой HTML не показываем и не печатаем: в наших текстах его нет, а показанный
            // как текст он читался бы мусором.
            case HtmlInline:
                break;

            case ContainerInline other:
                Walk(other, font);
                break;
        }
    }

    private void Emphasis(EmphasisInline emphasis, MarkupFont font)
    {
        switch (emphasis.DelimiterChar)
        {
            case '~':
                Walk(emphasis, font with { Strike = true });
                break;

            // ==подсветка==: маркер поверх текста, начертание не меняется.
            case '=':
                var start = _offset;
                Walk(emphasis, font);
                _block.AddBox(start, _offset - start, MarkupTheme.HighlightBg, MarkupTheme.RadiusXS, 2, 1);
                break;

            default:
                Walk(emphasis, emphasis.DelimiterCount >= 2
                    ? font with { Weight = MarkupTheme.WeightBold }
                    : font with { Style  = FontStyle.Italic });
                break;
        }
    }

    /// <summary>
    /// Код внутри строки. Воздух внутри коробки — настоящие неразрывные пробелы, а не раздутый
    /// прямоугольник: раздутый залез бы на соседнюю букву, а пробел раздвигает саму раскладку.
    /// Неразрывный — чтобы перенос не случился сразу за открывающим полем, оставив ободок.
    /// </summary>
    private void Code(string content, MarkupFont font)
    {
        var start = _offset;
        Emit(Nbsp + content + Nbsp,
             font with { Family = MarkupTheme.FontMono, Size = MarkupTheme.SizeMono, Brush = MarkupTheme.CodeText });
        _block.AddBox(start, _offset - start, MarkupTheme.CardBg, MarkupTheme.RadiusXS, 0, 1);
    }

    private void Link(ContainerInline? content, string url, MarkupFont font)
    {
        var start = _offset;
        var runs  = new List<Run>();

        var outer = _collecting;
        _collecting = runs;

        // Подписи может не быть (голый адрес в тексте) — тогда показываем сам адрес.
        if (content?.FirstChild is not null) Walk(content, font with { Brush = MarkupTheme.Accent });
        else Emit(url, font with { Brush = MarkupTheme.Accent });

        _collecting = outer;
        _block.AddLink(start, _offset - start, url, runs);
    }

    private void Emit(string text, MarkupFont font)
    {
        if (text.Length == 0) return;

        var run = new Run(text)
        {
            FontFamily = font.Family,
            FontSize   = font.Size,
            FontWeight = font.Weight,
            FontStyle  = font.Style,
            Foreground = font.Brush,
        };
        if (font.Strike) run.TextDecorations = TextDecorations.Strikethrough;

        _into.Add(run);
        _collecting?.Add(run);
        _offset += text.Length;
    }
}
