using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace SmartBase.Core.Notes;

/// <summary>
/// Разбор текста заметки в разметку для живого просмотра: где что оформить и какие служебные
/// символы спрятать.
///
/// Считает Markdig — тот же разбор, что у любого редактора markdown, вместо самодельных регулярок.
/// Главная тонкость его модели: <c>SourceSpan.End</c> ВКЛЮЧИТЕЛЬНЫЙ (длина = End - Start + 1), и
/// спан эмфазы покрывает вместе с текстом сами звездочки. Вся арифметика отделения маркеров от
/// содержимого живет здесь и покрыта тестами — в отрисовке ей делать нечего.
///
/// Набор расширений намеренно узкий: только то, что одинаково выглядит и в живом просмотре, и в
/// режиме «Просмотр» (он собран на другом движке — Markdown.Avalonia). Сноски, формулы, выносные
/// блоки и <c>==подсветка==</c> не включены: они начали бы прятать символы, которые человек набрал
/// как обычный текст, а в готовом виде остались бы сырыми.
/// </summary>
public static class NoteMarkupScanner
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .UsePreciseSourceLocation()
        .Build();

    public static NoteMarkupLayout Scan(string? text)
    {
        if (string.IsNullOrEmpty(text)) return NoteMarkupLayout.Empty;

        MarkdownDocument doc;
        try
        {
            // Полное имя: короткое «Markdown» перехватывает пространство имен Markdown.Avalonia.
            doc = Markdig.Markdown.Parse(text, Pipeline);
        }
        catch
        {
            // Разбор — украшение, а не работа с файлом: сорваться он может только на чем-то
            // невиданном, и тогда заметка обязана открыться просто без оформления.
            return NoteMarkupLayout.Empty;
        }

        var spans   = new List<NoteSpan>();
        var markers = new List<NoteMarker>();

        foreach (var node in doc.Descendants())
        {
            switch (node)
            {
                case HeadingBlock { IsSetext: false } h: Heading(text, h, spans, markers); break;
                case EmphasisInline e:                   Emphasis(text, e, spans, markers); break;
                case CodeInline c:                       Code(text, c, spans, markers);     break;
                case LinkInline l:                       Link(text, l, spans, markers);     break;
                case ListItemBlock li:                   ListItem(text, li, spans, markers); break;
                case QuoteBlock q:                       Quote(text, q, spans, markers);    break;
                case FencedCodeBlock f:                  Fence(text, f, spans, markers);    break;
                case ThematicBreakBlock t:               Split(spans, text, t.Span.Start, EndOf(t.Span, text), NoteStyle.Faint); break;
                case Table tb:                           TableRule(text, tb, spans);        break;
            }
        }

        // Начало — по возрастанию, при равном начале широкий спан идет раньше узкого: оформление
        // накладывается слоями, и уточнение (жирный внутри заголовка) обязано лечь последним.
        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
                                                      : b.Length.CompareTo(a.Length));

        return new NoteMarkupLayout(spans.ToArray(), Merge(markers));
    }

    /// <summary>
    /// Упорядочить маркеры и склеить наложения. Наложения неизбежны: у «***оба***» внешний и
    /// внутренний узлы дают маркеры на одних и тех же знаках, у вложенной цитаты — на одних и тех
    /// же «&gt;». Прятать текст внахлест нельзя: отрисовка требует непересекающихся кусков.
    /// </summary>
    private static NoteMarker[] Merge(List<NoteMarker> markers)
    {
        if (markers.Count == 0) return [];

        markers.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
                                                         : b.Length.CompareTo(a.Length));

        var merged = new List<NoteMarker>(markers.Count);
        foreach (var m in markers)
        {
            if (merged.Count > 0 && m.Start < merged[^1].End)
            {
                var last = merged[^1];
                // Подмену (точку списка) не растягиваем на чужие знаки — тогда пропускаем.
                if (m.End > last.End && m.Replacement == last.Replacement)
                    merged[^1] = last with { Length = m.End - last.Start };
                continue;
            }
            merged.Add(m);
        }
        return merged.ToArray();
    }

    // ── Узлы ────────────────────────────────────────────────────────────────────

    private static void Heading(string text, HeadingBlock h, List<NoteSpan> spans, List<NoteMarker> markers)
    {
        var start = h.Span.Start;
        var end   = EndOf(h.Span, text);
        if (end <= start) return;

        Split(spans, text, start, end, HeadingStyle(h.Level));

        // Решетки вместе с пробелами после них: заголовок должен начинаться с буквы.
        var p = start;
        while (p < end && text[p] == '#') p++;
        while (p < end && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p > start) markers.Add(new NoteMarker(start, p - start));
    }

    private static void Emphasis(string text, EmphasisInline e, List<NoteSpan> spans, List<NoteMarker> markers)
    {
        var n     = e.DelimiterCount;
        var start = e.Span.Start;
        var end   = EndOf(e.Span, text);
        if (n <= 0 || end - start <= 2 * n) return;

        // Границы содержимого считаем по самим цепочкам знаков, а не как «start + n»: у вложенной
        // эмфазы Markdig отдает смещенные спаны. Для «***оба***» он дает курсив [2..8] и жирный
        // [0..7] — то есть арифметика по DelimiterCount дала бы содержимым «оба**» и «*оба».
        // Отступ от края до конца цепочки одинаково верен и для простого «**жирный**».
        var content    = SkipRun(text, start, end, e.DelimiterChar);
        var contentEnd = SkipRunBack(text, end, content, e.DelimiterChar);
        if (contentEnd <= content) return;

        // Тройные знаки Markdig разбирает вложенными узлами, поэтому здесь ровно два случая.
        var style = e.DelimiterChar == '~' ? NoteStyle.Strike
                  : n >= 2                 ? NoteStyle.Bold
                                           : NoteStyle.Italic;

        Split(spans, text, content, contentEnd, style);
        markers.Add(new NoteMarker(start, content - start));
        markers.Add(new NoteMarker(contentEnd, end - contentEnd));
    }

    private static void Code(string text, CodeInline c, List<NoteSpan> spans, List<NoteMarker> markers)
    {
        var n     = c.DelimiterCount;
        var start = c.Span.Start;
        var end   = EndOf(c.Span, text);
        if (n <= 0 || end - start <= 2 * n) return;

        Split(spans, text, start + n, end - n, NoteStyle.Code);
        markers.Add(new NoteMarker(start, n));
        markers.Add(new NoteMarker(end - n, n));
    }

    private static void Link(string text, LinkInline l, List<NoteSpan> spans, List<NoteMarker> markers)
    {
        // Картинки не трогаем: без настоящей отрисовки прятать их адрес незачем, а показывать
        // «![]» оформленным — только путать.
        if (l.IsImage || l.IsAutoLink || l.LabelSpan.Length <= 0) return;

        var labelStart = l.LabelSpan.Start;
        var labelEnd   = EndOf(l.LabelSpan, text);
        var linkStart  = l.Span.Start;
        var linkEnd    = EndOf(l.Span, text);
        if (labelStart < linkStart || labelEnd > linkEnd) return;

        Split(spans, text, labelStart, labelEnd, NoteStyle.Link);
        if (labelStart > linkStart) markers.Add(new NoteMarker(linkStart, labelStart - linkStart));
        if (linkEnd > labelEnd)     markers.Add(new NoteMarker(labelEnd, linkEnd - labelEnd));
    }

    private static void ListItem(string text, ListItemBlock li, List<NoteSpan> spans, List<NoteMarker> markers)
    {
        var p = li.Span.Start;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p >= text.Length) return;

        if (li.Parent is ListBlock { IsOrdered: true })
        {
            // Номер оставляем: без него нумерованный список перестает быть нумерованным.
            var q = p;
            while (q < text.Length && char.IsDigit(text[q])) q++;
            if (q < text.Length && (text[q] == '.' || text[q] == ')')) q++;
            if (q > p) spans.Add(new NoteSpan(p, q - p, NoteStyle.Faint));
        }
        else if (text[p] is '-' or '*' or '+')
        {
            // Не прячем, а подменяем точкой: спрятать «-» совсем значило бы сдвинуть строку
            // влево и потерять сам вид списка.
            markers.Add(new NoteMarker(p, 1, "•"));
        }
    }

    private static void Quote(string text, QuoteBlock q, List<NoteSpan> spans, List<NoteMarker> markers)
    {
        var end = EndOf(q.Span, text);
        for (var i = q.Span.Start; i < end;)
        {
            var stop = LineEnd(text, i, end);

            var p = i;
            while (p < stop && (text[p] == ' ' || text[p] == '\t')) p++;
            var mark = p;
            while (p < stop && text[p] == '>') p++;
            if (p > mark)
            {
                if (p < stop && text[p] == ' ') p++;   // пробел после «>» тоже служебный
                markers.Add(new NoteMarker(mark, p - mark));
            }

            if (stop > p) spans.Add(new NoteSpan(p, stop - p, NoteStyle.Quote));

            var nl = text.IndexOf('\n', i);
            if (nl < 0 || nl >= end) break;
            i = nl + 1;
        }
    }

    private static void Fence(string text, FencedCodeBlock f, List<NoteSpan> spans, List<NoteMarker> markers)
    {
        var start = f.Span.Start;
        var end   = EndOf(f.Span, text);
        if (end <= start) return;

        Split(spans, text, start, end, NoteStyle.CodeBlock);

        // Ограждения прячем целиком. Строка остается на месте пустой — она и читается как поле
        // сверху и снизу закрашенного блока.
        var firstEnd = LineEnd(text, start, end);
        if (firstEnd > start) markers.Add(new NoteMarker(start, firstEnd - start));

        var lastStart = LineStart(text, end - 1, start);
        var lastEnd   = LineEnd(text, lastStart, end);
        // lastStart > start отсекает случай незакрытого ограждения: там первая строка и есть
        // последняя, и маркер получился бы вторым на том же месте.
        if (lastStart > start && lastEnd > lastStart)
            markers.Add(new NoteMarker(lastStart, lastEnd - lastStart));
    }

    // Строка выравнивания «|---|---|» в дереве Markdig не представлена отдельным узлом, поэтому
    // ищем ее по виду: только палки, дефисы и двоеточия.
    private static void TableRule(string text, Table tb, List<NoteSpan> spans)
    {
        var end = EndOf(tb.Span, text);
        for (var i = tb.Span.Start; i < end;)
        {
            var stop = LineEnd(text, i, end);
            if (stop > i && IsRuleLine(text, i, stop)) spans.Add(new NoteSpan(i, stop - i, NoteStyle.Faint));

            var nl = text.IndexOf('\n', i);
            if (nl < 0 || nl >= end) break;
            i = nl + 1;
        }
    }

    private static bool IsRuleLine(string text, int start, int end)
    {
        var dash = false;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c == '-') dash = true;
            else if (c is not ('|' or ':' or ' ' or '\t')) return false;
        }
        return dash;
    }

    // ── Мелочи ──────────────────────────────────────────────────────────────────

    private static NoteStyle HeadingStyle(int level) => level switch
    {
        1 => NoteStyle.Heading1,
        2 => NoteStyle.Heading2,
        3 => NoteStyle.Heading3,
        4 => NoteStyle.Heading4,
        5 => NoteStyle.Heading5,
        _ => NoteStyle.Heading6,
    };

    /// <summary>Граница спана Markdig в наших полуинтервалах: End у него включительный.</summary>
    private static int EndOf(Markdig.Syntax.SourceSpan span, string text) =>
        Math.Min(span.End + 1, text.Length);

    /// <summary>
    /// Разложить <c>[start, end)</c> по строкам: спан не имеет права пересекать перевод строки —
    /// на этом стоит весь двоичный поиск в <see cref="NoteMarkupLayout"/>.
    /// </summary>
    private static void Split(List<NoteSpan> into, string text, int start, int end, NoteStyle style)
    {
        if (end > text.Length) end = text.Length;
        for (var i = start; i < end;)
        {
            var stop = LineEnd(text, i, end);
            if (stop > i) into.Add(new NoteSpan(i, stop - i, style));

            var nl = text.IndexOf('\n', i);
            if (nl < 0 || nl >= end) break;
            i = nl + 1;
        }
    }

    // Конец строки, начинающейся на i, но не дальше limit; перевод строки и \r не входят.
    private static int LineEnd(string text, int i, int limit)
    {
        var nl   = text.IndexOf('\n', i);
        var stop = nl < 0 || nl > limit ? limit : nl;
        if (stop > i && text[stop - 1] == '\r') stop--;
        return stop;
    }

    // Пропустить цепочку знака c вперед от start.
    private static int SkipRun(string text, int start, int limit, char c)
    {
        var i = start;
        while (i < limit && text[i] == c) i++;
        return i;
    }

    // Пропустить цепочку знака c назад от end (не заходя за floor).
    private static int SkipRunBack(string text, int end, int floor, char c)
    {
        var i = end;
        while (i > floor && text[i - 1] == c) i--;
        return i;
    }

    // Начало строки, содержащей at, но не раньше floor.
    private static int LineStart(string text, int at, int floor)
    {
        if (at >= text.Length) at = text.Length - 1;
        var nl = at < 0 ? -1 : text.LastIndexOf('\n', at);
        return nl < 0 || nl + 1 < floor ? floor : nl + 1;
    }
}
