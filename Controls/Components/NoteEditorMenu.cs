using Avalonia;
using Avalonia.Controls;
using SmartBase.Core.Notes;

namespace SmartBase.Controls.Components;

/// <summary>Что человек выбрал в меню редактора заметки.</summary>
public enum NoteCommand
{
    Cut, Copy, Paste, SelectAll,
    Bold, Italic, Strike, InlineCode, Link, ClearFormat,
    BulletList, OrderedList, Quote,
    Heading1, Heading2, Heading3, Heading4, Heading5, Heading6, PlainText,
    Table, CodeBlock, Rule,
}

/// <summary>
/// Контекстное меню редактора заметки: те же команды, что в Obsidian, чтобы разметку не набирать
/// руками. Состав и слова взяты оттуда же.
///
/// Собирается в коде, как все меню приложения. Отдельно от <see cref="Widgets.NoteEditorBox"/>,
/// потому что меню — это набор надписей и порядок, а не поведение редактора: править состав
/// удобнее там, где не мешает работа с текстом.
///
/// Свой набор «Вырезать / Копировать / Вставить» здесь не роскошь: русское меню из
/// Assets/Styles/Controls.axaml объявлено для <c>TextBox</c> и на редактор заметки не
/// распространяется.
/// </summary>
public static class NoteEditorMenu
{
    public static ContextMenu Build(Action<NoteCommand> run) => Build(run, owner: null);

    /// <param name="owner">Контрол, у которого спрашивается платформенный модификатор для
    /// подсказок сочетаний; null — подсказок нет (меню строят и вне визуального дерева).</param>
    public static ContextMenu Build(Action<NoteCommand> run, Visual? owner)
    {
        var menu = new ContextMenu();

        // Иконок у этих четырех нет намеренно — как и в Obsidian: иконка ничего не добавляет
        // к слову «Копировать», зато отступ под нее держит текст в одну линию с остальными.
        menu.Items.Add(Item(MenuIcons.PlainHeader("Вырезать"),     () => run(NoteCommand.Cut),       "windows.ref.textCut",   owner));
        menu.Items.Add(Item(MenuIcons.PlainHeader("Копировать"),   () => run(NoteCommand.Copy),      "windows.ref.textCopy",  owner));
        menu.Items.Add(Item(MenuIcons.PlainHeader("Вставить"),     () => run(NoteCommand.Paste),     "windows.ref.textPaste", owner));
        menu.Items.Add(Item(MenuIcons.PlainHeader("Выделить все"), () => run(NoteCommand.SelectAll)));
        menu.Items.Add(new Separator());

        menu.Items.Add(Sub("Форматирование",
        [
            Item(MenuIcons.BoldHeader(),        () => run(NoteCommand.Bold),        "notes.bold",        owner),
            Item(MenuIcons.ItalicHeader(),      () => run(NoteCommand.Italic),      "notes.italic",      owner),
            Item(MenuIcons.StrikeHeader(),      () => run(NoteCommand.Strike),      "notes.strike",      owner),
            Item(MenuIcons.InlineCodeHeader(),  () => run(NoteCommand.InlineCode),  "notes.inlineCode",  owner),
            Item(MenuIcons.LinkHeader(),        () => run(NoteCommand.Link),        "notes.link",        owner),
            new Separator(),
            Item(MenuIcons.ClearFormatHeader(), () => run(NoteCommand.ClearFormat), "notes.clearFormat", owner),
        ]));

        menu.Items.Add(Sub("Абзац",
        [
            Item(MenuIcons.BulletListHeader(),  () => run(NoteCommand.BulletList),  "notes.bulletList",  owner),
            Item(MenuIcons.OrderedListHeader(), () => run(NoteCommand.OrderedList), "notes.orderedList", owner),
            new Separator(),
            Item(MenuIcons.HeadingHeader("Заголовок 1"), () => run(NoteCommand.Heading1), "notes.heading1", owner),
            Item(MenuIcons.HeadingHeader("Заголовок 2"), () => run(NoteCommand.Heading2), "notes.heading2", owner),
            Item(MenuIcons.HeadingHeader("Заголовок 3"), () => run(NoteCommand.Heading3), "notes.heading3", owner),
            Item(MenuIcons.HeadingHeader("Заголовок 4"), () => run(NoteCommand.Heading4), "notes.heading4", owner),
            Item(MenuIcons.HeadingHeader("Заголовок 5"), () => run(NoteCommand.Heading5), "notes.heading5", owner),
            Item(MenuIcons.HeadingHeader("Заголовок 6"), () => run(NoteCommand.Heading6), "notes.heading6", owner),
            Item(MenuIcons.PlainHeader("Обычный текст"), () => run(NoteCommand.PlainText), "notes.plainText", owner),
            new Separator(),
            Item(MenuIcons.QuoteHeader(),       () => run(NoteCommand.Quote),       "notes.quote",       owner),
        ]));

        menu.Items.Add(Sub("Вставка",
        [
            Item(MenuIcons.TableHeader(),     () => run(NoteCommand.Table),     "notes.table",     owner),
            Item(MenuIcons.CodeBlockHeader(), () => run(NoteCommand.CodeBlock), "notes.codeBlock", owner),
            Item(MenuIcons.RuleHeader(),      () => run(NoteCommand.Rule),      "notes.rule",      owner),
        ]));

        return menu;
    }

    private static MenuItem Item(Control header, Action run, string? actionId = null, Visual? owner = null)
    {
        var item = new MenuItem { Header = header };
        // Подсказка — из действующей привязки: переназначили сочетание, и меню говорит новое.
        if (actionId is not null && owner is not null) item.WithGesture(actionId, owner);
        item.Click += (_, _) => run();
        return item;
    }

    private static MenuItem Sub(string title, IEnumerable<object> children)
    {
        var item = new MenuItem { Header = MenuIcons.PlainHeader(title) };
        // Без этого попап подменю наезжает на родителя: тема Fluent задает ему отрицательный
        // сдвиг локальным значением, которое стилем не перебить.
        MenuIcons.FixSubmenuGap(item);
        foreach (var child in children) item.Items.Add(child);
        return item;
    }

    /// <summary>Команда меню в правку текста. Возвращает null, если команда не про текст.</summary>
    public static NoteEdit? Apply(NoteCommand command, string text, int start, int length) => command switch
    {
        NoteCommand.Bold        => NoteFormat.ToggleWrap(text, start, length, "**"),
        NoteCommand.Italic      => NoteFormat.ToggleWrap(text, start, length, "*"),
        NoteCommand.Strike      => NoteFormat.ToggleWrap(text, start, length, "~~"),
        NoteCommand.InlineCode  => NoteFormat.ToggleWrap(text, start, length, "`"),
        NoteCommand.Link        => NoteFormat.Link(text, start, length),
        NoteCommand.ClearFormat => NoteFormat.ClearFormatting(text, start, length),

        NoteCommand.BulletList  => NoteFormat.TogglePrefix(text, start, length, NotePrefix.Bullet),
        NoteCommand.OrderedList => NoteFormat.TogglePrefix(text, start, length, NotePrefix.Ordered),
        NoteCommand.Quote       => NoteFormat.TogglePrefix(text, start, length, NotePrefix.Quote),

        NoteCommand.Heading1  => NoteFormat.SetHeading(text, start, length, 1),
        NoteCommand.Heading2  => NoteFormat.SetHeading(text, start, length, 2),
        NoteCommand.Heading3  => NoteFormat.SetHeading(text, start, length, 3),
        NoteCommand.Heading4  => NoteFormat.SetHeading(text, start, length, 4),
        NoteCommand.Heading5  => NoteFormat.SetHeading(text, start, length, 5),
        NoteCommand.Heading6  => NoteFormat.SetHeading(text, start, length, 6),
        NoteCommand.PlainText => NoteFormat.SetHeading(text, start, length, 0),

        NoteCommand.Table     => NoteFormat.InsertBlock(text, start, length, NoteBlock.Table),
        NoteCommand.CodeBlock => NoteFormat.InsertBlock(text, start, length, NoteBlock.CodeFence),
        NoteCommand.Rule      => NoteFormat.InsertBlock(text, start, length, NoteBlock.Rule),

        _ => null,
    };
}
