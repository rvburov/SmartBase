using SmartBase.Core.Notes;

namespace SmartBase.Tests.Unit;

/// <summary>
/// Команды форматирования заметки. Сложность здесь не в кнопках меню, а в краевых случаях: пустое
/// выделение посреди слова, выделение через несколько строк, повторное применение, выделение
/// ВНУТРИ уже обернутого куска. Каждый из них однажды портил чужой текст в любом редакторе, где
/// его не проверили.
/// </summary>
public sealed class NoteFormatTests
{
    // ── Обертка ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Selected_Word_Gets_Wrapped_And_Stays_Selected()
    {
        var r = NoteFormat.ToggleWrap("до слово после", 3, 5, "**");

        Assert.Equal("до **слово** после", r.Text);
        // Выделено само слово, без звездочек: человек продолжает печатать по делу.
        Assert.Equal("слово", r.Text.Substring(r.Start, r.Length));
    }

    [Fact]
    public void Wrapping_Twice_Returns_The_Text_Exactly()
    {
        const string text = "до слово после";
        var once  = NoteFormat.ToggleWrap(text, 3, 5, "**");
        var twice = NoteFormat.ToggleWrap(once.Text, once.Start, once.Length, "**");

        Assert.Equal(text, twice.Text);
    }

    [Fact]
    public void Selection_Inside_An_Already_Wrapped_Word_Unwraps_It()
    {
        // Самый частый промах: человек выделил слово ВНУТРИ «**слово**». Наивная реализация
        // добавила бы вторую пару и получила «****слово****».
        var r = NoteFormat.ToggleWrap("до **слово** после", 5, 5, "**");

        Assert.Equal("до слово после", r.Text);
    }

    [Fact]
    public void Selection_With_The_Markers_Unwraps_Too() =>
        Assert.Equal("до слово после", NoteFormat.ToggleWrap("до **слово** после", 3, 9, "**").Text);

    [Fact]
    public void Empty_Selection_Inside_A_Word_Wraps_The_Whole_Word() =>
        // Иначе вышло бы «жир****ный».
        Assert.Equal("**жирный**", NoteFormat.ToggleWrap("жирный", 3, 0, "**").Text);

    [Fact]
    public void Caret_At_The_End_Of_A_Word_Still_Wraps_That_Word() =>
        // Каретка сразу за словом — человек только что его дописал и хочет выделить именно его.
        Assert.Equal("**раз** два", NoteFormat.ToggleWrap("раз два", 3, 0, "**").Text);

    [Fact]
    public void Caret_On_Empty_Space_Leaves_A_Place_To_Type()
    {
        var r = NoteFormat.ToggleWrap("раз  два", 4, 0, "**");

        Assert.Equal("раз **** два", r.Text);
        Assert.Equal(0, r.Length);
        Assert.Equal(6, r.Start);   // ровно между парами
    }

    [Fact]
    public void Spaces_Stay_Outside_The_Markers()
    {
        // CommonMark не считает «** х **» разметкой — знаки обязаны прилегать к тексту.
        var r = NoteFormat.ToggleWrap("до слово после", 2, 7, "**");

        Assert.Equal("до **слово** после", r.Text);
    }

    [Fact]
    public void Multiline_Selection_Wraps_Each_Line_On_Its_Own()
    {
        // Через перевод строки пара знаков не работает: «**раз\nдва**» разметкой не является.
        var r = NoteFormat.ToggleWrap("раз\nдва", 0, 7, "**");

        Assert.Equal("**раз**\n**два**", r.Text);
    }

    [Fact]
    public void Multiline_Wrapping_Skips_Empty_Lines() =>
        Assert.Equal("**раз**\n\n**два**", NoteFormat.ToggleWrap("раз\n\nдва", 0, 8, "**").Text);

    [Theory]
    [InlineData("*",  "*слово*")]
    [InlineData("~~", "~~слово~~")]
    [InlineData("`",  "`слово`")]
    public void Every_Marker_Works_The_Same(string marker, string expected) =>
        Assert.Equal(expected, NoteFormat.ToggleWrap("слово", 0, 5, marker).Text);

    [Fact]
    public void Bold_Over_Italic_Nests_Instead_Of_Replacing() =>
        Assert.Equal("***слово***", NoteFormat.ToggleWrap("*слово*", 1, 5, "**").Text);

    // ── Очистка ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clearing_Strips_Every_Inline_Marker() =>
        Assert.Equal("раз два три",
                     NoteFormat.ClearFormatting("**раз** *два* `три`", 0, 19).Text);

    [Fact]
    public void Clearing_Nothing_Selected_Changes_Nothing() =>
        Assert.Equal("**раз**", NoteFormat.ClearFormatting("**раз**", 3, 0).Text);

    // ── Ссылка ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Link_Wraps_The_Selection_And_Points_At_The_Address()
    {
        var r = NoteFormat.Link("см. сюда", 4, 4);

        Assert.Equal("см. [сюда](url)", r.Text);
        Assert.Equal("url", r.Text.Substring(r.Start, r.Length));
    }

    // ── Признак строки ──────────────────────────────────────────────────────────

    [Fact]
    public void Bullet_Is_Added_To_Every_Touched_Line() =>
        Assert.Equal("- раз\n- два", NoteFormat.TogglePrefix("раз\nдва", 0, 7, NotePrefix.Bullet).Text);

    [Fact]
    public void Bullet_Is_Removed_When_All_Lines_Already_Have_It() =>
        Assert.Equal("раз\nдва", NoteFormat.TogglePrefix("- раз\n- два", 0, 11, NotePrefix.Bullet).Text);

    [Fact]
    public void Bullet_Is_Added_When_Only_Some_Lines_Have_It() =>
        // Иначе одно нажатие и снимало бы, и ставило — результат зависел бы от того, где начали.
        Assert.Equal("- раз\n- два", NoteFormat.TogglePrefix("- раз\nдва", 0, 9, NotePrefix.Bullet).Text);

    [Fact]
    public void Ordered_List_Is_Numbered_From_One() =>
        Assert.Equal("1. раз\n2. два\n3. три",
                     NoteFormat.TogglePrefix("раз\nдва\nтри", 0, 11, NotePrefix.Ordered).Text);

    [Fact]
    public void Bullets_Turn_Into_A_Numbered_List_Counting_From_One() =>
        Assert.Equal("1. раз\n2. два",
                     NoteFormat.TogglePrefix("- раз\n- два", 0, 11, NotePrefix.Ordered).Text);

    [Fact]
    public void Numbered_List_Applied_To_Itself_Is_Removed() =>
        // Как и у пунктов: повторное нажатие возвращает текст к прежнему виду.
        Assert.Equal("раз\nдва", NoteFormat.TogglePrefix("1. раз\n2. два", 0, 13, NotePrefix.Ordered).Text);

    [Fact]
    public void Prefixes_Replace_Each_Other_Instead_Of_Stacking() =>
        // «- > текст» не бывает: признаки абзаца взаимоисключающие.
        Assert.Equal("> раз", NoteFormat.TogglePrefix("- раз", 0, 5, NotePrefix.Quote).Text);

    [Fact]
    public void Indent_Of_A_Nested_Item_Survives() =>
        Assert.Equal("    - вложенный", NoteFormat.TogglePrefix("    вложенный", 0, 13, NotePrefix.Bullet).Text);

    [Fact]
    public void Empty_Lines_Keep_Their_Emptiness() =>
        Assert.Equal("- раз\n\n- два", NoteFormat.TogglePrefix("раз\n\nдва", 0, 8, NotePrefix.Bullet).Text);

    // ── Заголовок ───────────────────────────────────────────────────────────────

    [Fact]
    public void Heading_Is_Set_On_The_Caret_Line() =>
        Assert.Equal("## Заголовок", NoteFormat.SetHeading("Заголовок", 3, 0, 2).Text);

    [Fact]
    public void Heading_Level_Is_Replaced_Not_Added() =>
        Assert.Equal("#### Текст", NoteFormat.SetHeading("## Текст", 0, 8, 4).Text);

    [Fact]
    public void Same_Level_Twice_Removes_The_Heading() =>
        Assert.Equal("Текст", NoteFormat.SetHeading("## Текст", 0, 8, 2).Text);

    [Fact]
    public void Level_Zero_Removes_The_Heading() =>
        Assert.Equal("Текст", NoteFormat.SetHeading("###### Текст", 0, 12, 0).Text);

    [Fact]
    public void Heading_Drops_A_List_Prefix() =>
        // Заголовок-пункт списка — не разметка, а мусор: прежний признак снимаем.
        Assert.Equal("# Текст", NoteFormat.SetHeading("- Текст", 0, 7, 1).Text);

    [Fact]
    public void Heading_Applies_To_Every_Touched_Line() =>
        Assert.Equal("# раз\n# два", NoteFormat.SetHeading("раз\nдва", 0, 7, 1).Text);

    // ── Блоки ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Table_Is_Inserted_As_Whole_Lines()
    {
        var r = NoteFormat.InsertBlock("", 0, 0, NoteBlock.Table);

        Assert.Contains("|---|---|", r.Text);
        Assert.StartsWith("| Колонка | Колонка |", r.Text);
    }

    [Fact]
    public void Block_Breaks_Away_From_A_Non_Empty_Line()
    {
        // Прилипший к абзацу блок перестает быть блоком — markdown его не увидит.
        var r = NoteFormat.InsertBlock("абзац", 5, 0, NoteBlock.Rule);

        Assert.Equal("абзац\n\n---", r.Text);
    }

    [Fact]
    public void Block_On_An_Empty_Document_Adds_Nothing_Extra() =>
        Assert.Equal("---", NoteFormat.InsertBlock("", 0, 0, NoteBlock.Rule).Text);

    [Fact]
    public void Code_Fence_Leaves_The_Caret_Inside()
    {
        var r = NoteFormat.InsertBlock("", 0, 0, NoteBlock.CodeFence);

        Assert.Equal("```\n\n```", r.Text);
        Assert.Equal(0, r.Length);
    }

    // ── Минимальная правка ──────────────────────────────────────────────────────

    [Fact]
    public void Minimize_Finds_The_Single_Changed_Piece()
    {
        // От этого зависит Ctrl+Z: правка должна лечь в историю ОДНИМ шагом, иначе отмена
        // откатит всю заметку целиком.
        // Правка одна и сплошная: две вставки по краям слова свести в один кусок нельзя,
        // поэтому слово входит и в удаляемое, и во вставляемое. Главное — не весь текст.
        var (offset, remove, insert) = NoteFormat.Minimize("до слово после", "до **слово** после");

        Assert.Equal(3, offset);
        Assert.Equal(5, remove);
        Assert.Equal("**слово**", insert);
    }

    [Fact]
    public void Minimize_Handles_Deletion()
    {
        var (offset, remove, insert) = NoteFormat.Minimize("до **слово** после", "до слово после");

        Assert.Equal(3, offset);
        Assert.Equal(9, remove);
        Assert.Equal("слово", insert);
    }

    [Fact]
    public void Minimize_Never_Touches_The_Untouched_Tail()
    {
        // Ради этого все и затевалось: длинная заметка правится в одном месте, а не переписывается.
        var old  = new string('я', 5000) + "слово" + new string('ю', 5000);
        var next = old.Replace("слово", "**слово**");

        var (offset, remove, _) = NoteFormat.Minimize(old, next);

        Assert.Equal(5000, offset);
        Assert.Equal(5, remove);
    }

    [Fact]
    public void Minimize_Of_Equal_Texts_Is_Empty()
    {
        var (_, remove, insert) = NoteFormat.Minimize("одинаково", "одинаково");

        Assert.Equal(0, remove);
        Assert.Equal("", insert);
    }

    // ── Общее ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5, 100)]
    [InlineData(1000, 5)]
    [InlineData(3, -2)]
    public void Nonsense_Selection_Does_Not_Throw(int start, int length)
    {
        // Выделение приходит из редактора и может отстать от текста на один тик таймера.
        NoteFormat.ToggleWrap("текст", start, length, "**");
        NoteFormat.SetHeading("текст", start, length, 2);
        NoteFormat.TogglePrefix("текст", start, length, NotePrefix.Bullet);
        NoteFormat.InsertBlock("текст", start, length, NoteBlock.Rule);
    }
}
