using Avalonia.Controls;
using Avalonia.Controls.Documents;
using SmartBase.Controls.Widgets;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Свой рендер markdown (<see cref="AppMarkdown"/> поверх Controls/Markup): проверяется дерево
/// контролов, а не картинка.
///
/// Экранных тестов в проекте нет, и до появления этих регрессии рендера ловились только глазом
/// по четырем экранам. Тут проверяется ровно то, что глазом не увидишь: что блоков столько,
/// сколько блоков в тексте, и что смещения символов, на которых держатся коробка кода и клик по
/// ссылке, совпадают с текстом раскладки.
///
/// Контролы строятся БЕЗ запущенного приложения: <c>Application.Current</c> здесь null, и вид
/// приходит запасными значениями из <c>MarkupTheme</c>. Для устройства дерева это неважно — а
/// цена такой проверки нулевая, тогда как поднимать headless-приложение пришлось бы ради каждого
/// теста.
/// </summary>
public sealed class MarkupRenderTests
{
    private static IReadOnlyList<Control> Render(string markdown) =>
        new AppMarkdown { Markdown = markdown }.Children;

    /// <summary>Текст блока — тот же, по которому раскладка считает смещения символов.</summary>
    private static string TextOf(Control block) =>
        block is TextBlock { Inlines: { } inlines } ? inlines.Text ?? "" : "";

    [Fact]
    public void Empty_Text_Gives_Nothing()
    {
        Assert.Empty(Render(""));
        Assert.Empty(new AppMarkdown().Children);
    }

    [Fact]
    public void Every_Block_Becomes_One_Control()
    {
        var blocks = Render("""
            # Заголовок

            Абзац.

            - пункт
            - пункт

            > цитата

            ```
            код
            ```

            ---
            """);

        Assert.Equal(6, blocks.Count);
    }

    [Fact]
    public void Markers_Do_Not_Reach_The_Screen()
    {
        var text = TextOf(Render("**жирный**, *курсив*, ~~зачеркнутый~~ и ==подсветка==")[0]);
        Assert.Equal("жирный, курсив, зачеркнутый и подсветка", text);
    }

    [Fact]
    public void Emphasis_Splits_Text_Into_Runs()
    {
        var block = (TextBlock)Render("обычный **жирный** обычный")[0];
        Assert.Equal(3, block.Inlines!.Count);
        Assert.Equal("жирный", ((Run)block.Inlines[1]).Text);
    }

    /// <summary>
    /// Смещения в тексте раскладки — фундамент всего показа: по ним рисуются коробка кода и
    /// подсветка и по ним же ищется ссылка под курсором. Достаточно одного элемента, который
    /// занимает место в раскладке, но не дает символов (встроенный контрол, элемент переноса),
    /// и все, что после него, съезжает. Поэтому проверяется совпадение до символа.
    /// </summary>
    [Fact]
    public void Inline_Code_Keeps_Text_Offsets_Exact()
    {
        var text = TextOf(Render("до `код` после")[0]);

        // Поля коробки — настоящие неразрывные пробелы: они и раздвигают раскладку.
        Assert.Equal("до  код  после", text);
    }

    [Fact]
    public void Hard_Line_Break_Is_A_Real_Newline()
    {
        // Иначе смещения после переноса разъехались бы с раскладкой, а на Windows — еще и
        // на два символа вместо одного.
        Assert.Equal("первая\nвторая", TextOf(Render("первая  \nвторая")[0]));
    }

    [Fact]
    public void Link_Shows_Its_Label_And_Bare_Address_Shows_Itself()
    {
        Assert.Equal("подпись", TextOf(Render("[подпись](https://example.com)")[0]));
        Assert.Equal("https://example.com", TextOf(Render("<https://example.com>")[0]));
    }

    [Fact]
    public void Callout_Tag_Is_Eaten_And_Unknown_One_Is_Not()
    {
        // Знакомую метку съедаем — на экране остается зачин.
        var callout = (Border)Render("> [!note] Зачин\n> Продолжение.")[0];
        var body    = (StackPanel)callout.Child!;
        var header  = (StackPanel)body.Children[0];
        Assert.Equal("Зачин", TextOf((Control)header.Children[1]));

        // Незнакомую не трогаем: пусть человек увидит опечатку, а не пустое место.
        var quote = (Border)Render("> [!нетакой] Текст")[0];
        Assert.Contains("[!нетакой]", TextOf(((StackPanel)quote.Child!).Children[0]));
    }

    [Fact]
    public void Callout_Without_A_Title_Falls_Back_To_The_Kind_Name()
    {
        var callout = (Border)Render("> [!warning]\n> Текст.")[0];
        var header  = (StackPanel)((StackPanel)callout.Child!).Children[0];
        Assert.Equal("Внимание", TextOf((Control)header.Children[1]));
    }

    [Fact]
    public void Task_List_Loses_The_Brackets_But_Keeps_The_Text()
    {
        var list = (StackPanel)Render("- [x] сделано\n- [ ] не сделано")[0];
        Assert.Equal(2, list.Children.Count);

        var first = (Grid)list.Children[0];
        var text  = (StackPanel)first.Children[1];
        Assert.Equal("сделано", TextOf(text.Children[0]));
    }

    [Fact]
    public void Table_Becomes_A_Grid_Of_Cells()
    {
        var grid = (Grid)Render("""
            | Где | Что |
            |---|---|
            | тут | это |
            | там | то  |
            """)[0];

        Assert.Equal(2, grid.ColumnDefinitions.Count);
        Assert.Equal(3, grid.RowDefinitions.Count);
        Assert.Equal(6, grid.Children.Count);
    }

    [Fact]
    public void Code_Block_Keeps_Its_Lines()
    {
        var card = (Border)Render("```bash\nпервая\nвторая\n```")[0];
        var body = (Control)((Grid)card.Child!).Children[0];
        Assert.Equal("первая\nвторая", TextOf(body));
    }

    [Fact]
    public void Nested_List_Is_A_Block_Inside_Its_Item()
    {
        var list  = (StackPanel)Render("- верх\n    - низ")[0];
        var item  = (Grid)list.Children[0];
        var inner = (StackPanel)item.Children[1];

        // Абзац пункта и вложенный список — два блока в колонке содержимого.
        Assert.Equal(2, inner.Children.Count);
        Assert.IsType<StackPanel>(inner.Children[1]);
    }

    /// <summary>
    /// Отбивка — снизу и только снизу, и последний блок ее не получает: воздух под текстом
    /// задает тот, кто поставил виджет. Иначе внизу выходит двойной просвет.
    /// </summary>
    [Fact]
    public void Only_The_Last_Block_Has_No_Bottom_Space()
    {
        var blocks = Render("первый\n\nвторой\n\nтретий");

        Assert.All(blocks, b => Assert.Equal(0, b.Margin.Top));
        Assert.True(blocks[0].Margin.Bottom > 0);
        Assert.True(blocks[1].Margin.Bottom > 0);
        Assert.Equal(0, blocks[^1].Margin.Bottom);
    }

    /// <summary>
    /// Просвет считается по пустым строкам ИСХОДНИКА, а не «за то, что блок кончился». В
    /// редакторе заметок просвет дают настоящие пустые строки файла — «Просмотр» обязан вести
    /// себя так же.
    /// </summary>
    [Fact]
    public void Space_Between_Blocks_Counts_Blank_Source_Lines()
    {
        var blocks = Render("первый\n\nчерез одну\n\n\nчерез две\n");

        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[0].Margin.Bottom > 0);
        Assert.Equal(blocks[0].Margin.Bottom * 2, blocks[1].Margin.Bottom, 3);
        Assert.Equal(0, blocks[^1].Margin.Bottom);
    }

    /// <summary>
    /// Заголовок отбивается воздухом ВСЕГДА, даже когда пустой строки в файле нет: иначе он
    /// сливается с текстом и перестает делить его на части. Сверху и снизу поровну — ровно
    /// столько, сколько дала бы пустая строка, поэтому с редактором заметок совпадает.
    /// </summary>
    [Fact]
    public void Heading_Is_Spaced_Even_Without_Blank_Lines()
    {
        var glued = Render("текст до\n# Заголовок\nтекст после");
        Assert.True(glued[0].Margin.Bottom > 0);
        Assert.Equal(glued[0].Margin.Bottom, glued[1].Margin.Bottom, 3);

        // Пустые строки вокруг заголовка ничего не меняют: минимум им и равен.
        var spaced = Render("текст до\n\n# Заголовок\n\nтекст после");
        Assert.Equal(glued[0].Margin.Bottom, spaced[0].Margin.Bottom, 3);
        Assert.Equal(glued[1].Margin.Bottom, spaced[1].Margin.Bottom, 3);
    }

    [Fact]
    public void Broken_Markup_Still_Shows_Something()
    {
        // Текст приходит и от человека, и из описаний расширений: несбалансированная разметка
        // обязана показаться как текст, а не уронить экран.
        Assert.NotEmpty(Render("**незакрытый и `тоже незакрытый"));
        Assert.NotEmpty(Render("| битая | таблица\n|---"));
    }
}
