using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Разбор заметки для живого просмотра. Здесь прибивается главная тонкость модели Markdig:
/// <c>SourceSpan.End</c> включительный, а спан эмфазы покрывает вместе с текстом сами звездочки.
/// Ошибка на единицу тут не падает — она молча уводит оформление на символ вбок, а спрятанный
/// маркер съедает соседнюю букву.
///
/// Второй инвариант, на котором стоит весь двоичный поиск: спан НИКОГДА не пересекает перевод
/// строки.
/// </summary>
public sealed class NoteMarkupScannerTests
{
    private static NoteMarkupLayout Scan(string text) => NoteMarkupScanner.Scan(text);

    // Кусок текста, накрытый спаном данного вида, — самый честный способ проверить смещения.
    private static string[] Styled(string text, NoteStyle style) =>
        Scan(text).Spans.Where(s => s.Style == style)
                        .Select(s => text.Substring(s.Start, s.Length))
                        .ToArray();

    private static string[] Hidden(string text) =>
        Scan(text).Markers.Select(m => text.Substring(m.Start, m.Length)).ToArray();

    // ── Эмфаза ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Bold_Covers_Only_The_Words_Not_The_Stars() =>
        Assert.Equal(["жирный"], Styled("до **жирный** после", NoteStyle.Bold));

    [Fact]
    public void Bold_Hides_Both_Pairs_Of_Stars() =>
        Assert.Equal(["**", "**"], Hidden("до **жирный** после"));

    [Fact]
    public void Italic_Is_Single_Star() =>
        Assert.Equal(["курсив"], Styled("а *курсив* б", NoteStyle.Italic));

    [Fact]
    public void Underscore_Is_Italic_Too() =>
        Assert.Equal(["курсив"], Styled("а _курсив_ б", NoteStyle.Italic));

    [Fact]
    public void Strikethrough_Needs_Two_Tildes() =>
        Assert.Equal(["долой"], Styled("вот ~~долой~~ вот", NoteStyle.Strike));

    [Fact]
    public void Single_Tilde_Is_Plain_Text()
    {
        // Подстрочный текст намеренно не включен: одиночная «~» слишком часто встречается
        // в обычном письме, и человек получил бы оформление там, где его не просил.
        var l = Scan("примерно ~5 минут");
        Assert.Empty(l.Markers);
    }

    [Fact]
    public void Bold_And_Italic_Nest_Without_Losing_Either()
    {
        // Ловушка Markdig: у вложенной эмфазы он отдает смещенные спаны — курсив [2..8] и
        // жирный [0..7]. Арифметика «start + DelimiterCount» дала бы содержимым «оба**»
        // и «*оба», то есть оформление уехало бы на звездочки.
        const string text = "***оба***";
        Assert.Equal(["оба"], Styled(text, NoteStyle.Italic));
        Assert.Equal(["оба"], Styled(text, NoteStyle.Bold));
    }

    [Fact]
    public void Nested_Emphasis_Hides_Each_Run_Once()
    {
        // Внешний и внутренний узлы дают маркеры на одних и тех же звездочках. Прятать текст
        // внахлест нельзя — отрисовке нужны непересекающиеся куски, поэтому наложения склеены.
        Assert.Equal(["***", "***"], Hidden("***оба***"));
    }

    [Fact]
    public void Emphasis_Inside_A_Word_Is_Not_Markup() =>
        // CommonMark не считает подчеркивания внутри слова разметкой — иначе имена файлов
        // вроде «отчет_за_март» рассыпались бы прямо при наборе.
        Assert.Empty(Styled("отчет_за_март", NoteStyle.Italic));

    // ── Код ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Inline_Code_Covers_Only_The_Content() =>
        Assert.Equal(["dotnet build"], Styled("зовем `dotnet build` из корня", NoteStyle.Code));

    [Fact]
    public void Inline_Code_With_Double_Backticks_Hides_Both() =>
        Assert.Equal(["``", "``"], Hidden("вот ``код с ` внутри`` вот"));

    [Fact]
    public void Fenced_Block_Styles_Every_Line_Including_Fences()
    {
        const string text = "```\nраз\nдва\n```";
        var lines = Styled(text, NoteStyle.CodeBlock);
        Assert.Contains("раз", lines);
        Assert.Contains("два", lines);
    }

    [Fact]
    public void Fenced_Block_Hides_Opening_And_Closing_Fences()
    {
        var hidden = Hidden("```csharp\nкод\n```");
        Assert.Contains("```csharp", hidden);
        Assert.Contains("```", hidden);
    }

    [Fact]
    public void Unclosed_Fence_Does_Not_Hide_The_Same_Line_Twice() =>
        Assert.Single(Hidden("```\nкод без закрытия"));

    // ── Заголовки ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("# Раз",       NoteStyle.Heading1)]
    [InlineData("## Два",      NoteStyle.Heading2)]
    [InlineData("### Три",     NoteStyle.Heading3)]
    [InlineData("###### Шесть", NoteStyle.Heading6)]
    public void Heading_Levels_Map_One_To_One(string text, NoteStyle expected) =>
        Assert.NotEmpty(Styled(text, expected));

    [Fact]
    public void Heading_Hides_Hashes_Together_With_The_Space() =>
        // Прячем и пробел: иначе заголовок начнется с отступа в один символ.
        Assert.Equal(["## "], Hidden("## Заголовок"));

    [Fact]
    public void Heading_Without_Text_Does_Not_Break() =>
        Assert.Equal(["## "], Hidden("## "));

    [Fact]
    public void Underlined_Heading_Keeps_Its_Underline()
    {
        // У setext-заголовка «===» — самостоятельная строка; спрятать ее нечем, и это верно:
        // иначе она пропала бы, а заголовок остался бы без объяснения, почему он крупный.
        var hidden = Hidden("Заголовок\n=========");
        Assert.DoesNotContain("=========", hidden);
    }

    // ── Списки и цитаты ─────────────────────────────────────────────────────────

    [Fact]
    public void Bullet_Is_Replaced_Not_Hidden()
    {
        var m = Scan("- пункт").Markers.Single();
        Assert.Equal("•", m.Replacement);
        Assert.Equal(1, m.Length);   // подменяем сам знак, пробел после него остается на месте
    }

    [Theory]
    [InlineData("* пункт")]
    [InlineData("+ пункт")]
    [InlineData("  - вложенный")]
    public void Every_Bullet_Char_Is_Recognized(string text) =>
        Assert.Equal("•", Scan(text).Markers.Single().Replacement);

    [Fact]
    public void Ordered_List_Keeps_Its_Number()
    {
        // Номер не прячем и не подменяем — без него нумерованный список перестает быть таковым.
        Assert.Empty(Scan("1. раз").Markers);
        Assert.Equal(["1."], Styled("1. раз", NoteStyle.Faint));
    }

    [Fact]
    public void Quote_Hides_The_Angle_With_Its_Space() =>
        Assert.Equal(["> "], Hidden("> цитата"));

    [Fact]
    public void Quote_Styles_Every_Line_Separately()
    {
        var lines = Styled("> первая\n> вторая", NoteStyle.Quote);
        Assert.Equal(["первая", "вторая"], lines);
    }

    // ── Ссылки, линия, таблица ──────────────────────────────────────────────────

    [Fact]
    public void Link_Shows_The_Label_And_Hides_The_Rest()
    {
        const string text = "см. [документацию](https://example.com) тут";
        Assert.Equal(["документацию"], Styled(text, NoteStyle.Link));
        Assert.Equal(["[", "](https://example.com)"], Hidden(text));
    }

    [Fact]
    public void Image_Is_Left_Alone() =>
        // Картинку не рисуем, значит прятать ее адрес нечем — «![кот](кот.png)» осталось бы
        // обрубком «кот» без всякого изображения.
        Assert.Empty(Hidden("![кот](кот.png)"));

    [Fact]
    public void Thematic_Break_Is_Faint() =>
        Assert.Equal(["---"], Styled("текст\n\n---\n\nеще", NoteStyle.Faint));

    [Fact]
    public void Table_Alignment_Row_Is_Faint()
    {
        const string text = "| А | Б |\n|---|---|\n| 1 | 2 |";
        Assert.Equal(["|---|---|"], Styled(text, NoteStyle.Faint));
    }

    // ── Инварианты ──────────────────────────────────────────────────────────────

    [Fact]
    public void No_Span_Ever_Crosses_A_Line_Break()
    {
        // На этом стоит двоичный поиск: спан, задевающий строку, обязан в ней и начинаться.
        const string text = "# Заголовок\n\n> цитата\n> вторая\n\n```\nкод\n```\n\n- пункт\n\n| А | Б |\n|---|---|\n| 1 | 2 |";
        foreach (var s in Scan(text).Spans)
            Assert.DoesNotContain('\n', text.AsSpan(s.Start, s.Length).ToString());
    }

    [Fact]
    public void Spans_And_Markers_Stay_Inside_The_Text()
    {
        const string text = "**жирный** и `код` и [ссылка](url)\n## Заголовок\n> цитата";
        var l = Scan(text);
        Assert.All(l.Spans,   s => Assert.True(s.Start >= 0 && s.End <= text.Length));
        Assert.All(l.Markers, m => Assert.True(m.Start >= 0 && m.End <= text.Length));
    }

    [Fact]
    public void Both_Sets_Are_Sorted_By_Start()
    {
        var l = Scan("# Раз\n**два** *три* `четыре`\n> пять");
        Assert.Equal(l.Spans.Select(s => s.Start).Order(), l.Spans.Select(s => s.Start));
        Assert.Equal(l.Markers.Select(m => m.Start).Order(), l.Markers.Select(m => m.Start));
    }

    [Fact]
    public void Windows_Line_Endings_Do_Not_Leak_Into_Spans()
    {
        // Файл мог прийти из Windows: \r обязан остаться за границей спана, иначе он попадет
        // в оформление и строка станет на символ длиннее, чем видно.
        const string text = "# Заголовок\r\n\r\n> цитата\r\n> вторая\r\n";
        foreach (var s in Scan(text).Spans)
            Assert.DoesNotContain('\r', text.Substring(s.Start, s.Length));
    }

    [Fact]
    public void Empty_And_Plain_Text_Give_Nothing()
    {
        Assert.Empty(Scan("").Spans);
        Assert.Empty(Scan("Просто текст без разметки.").Markers);
    }

    // ── Выборка по строке ───────────────────────────────────────────────────────

    [Fact]
    public void SpansIn_Returns_Only_The_Asked_Line()
    {
        const string text = "**раз**\n**два**";
        var l = Scan(text);
        var into = new List<NoteSpan>();
        l.SpansIn(0, 7, into);   // первая строка

        Assert.All(into, s => Assert.True(s.End <= 7));
        Assert.Contains(into, s => s.Style == NoteStyle.Bold);
    }

    [Fact]
    public void Markers_Are_Found_By_Their_Exact_Start()
    {
        var l = Scan("**жирный**");
        Assert.True(l.TryMarkerAt(0, out var first));
        Assert.Equal(2, first.Length);
        Assert.False(l.TryMarkerAt(1, out _));   // середина маркера — не начало
    }

    [Fact]
    public void FirstMarkerFrom_Respects_The_Limit()
    {
        var l = Scan("текст **жирный**");
        Assert.Equal(6,  l.FirstMarkerFrom(0, 100));
        Assert.Equal(-1, l.FirstMarkerFrom(0, 6));    // маркер начинается ровно на границе
    }
}
