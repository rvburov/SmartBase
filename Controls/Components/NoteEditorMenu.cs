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
    public static ContextMenu Build(Action<NoteCommand> run)
    {
        var menu = new ContextMenu();

        // Иконок у этих четырех нет намеренно — как и в Obsidian: иконка ничего не добавляет
        // к слову «Копировать», зато отступ под нее держит текст в одну линию с остальными.
        menu.Items.Add(Item(MenuIcons.PlainHeader("Вырезать"),     () => run(NoteCommand.Cut)));
        menu.Items.Add(Item(MenuIcons.PlainHeader("Копировать"),   () => run(NoteCommand.Copy)));
        menu.Items.Add(Item(MenuIcons.PlainHeader("Вставить"),     () => run(NoteCommand.Paste)));
        menu.Items.Add(Item(MenuIcons.PlainHeader("Выделить все"), () => run(NoteCommand.SelectAll)));
        menu.Items.Add(new Separator());

        menu.Items.Add(Sub("Форматирование",
        [
            Item(MenuIcons.BoldHeader(),        () => run(NoteCommand.Bold)),
            Item(MenuIcons.ItalicHeader(),      () => run(NoteCommand.Italic)),
            Item(MenuIcons.StrikeHeader(),      () => run(NoteCommand.Strike)),
            Item(MenuIcons.InlineCodeHeader(),  () => run(NoteCommand.InlineCode)),
            Item(MenuIcons.LinkHeader(),        () => run(NoteCommand.Link)),
            new Separator(),
            Item(MenuIcons.ClearFormatHeader(), () => run(NoteCommand.ClearFormat)),
        ]));

        menu.Items.Add(Sub("Абзац",
        [
            Item(MenuIcons.BulletListHeader(),  () => run(NoteCommand.BulletList)),
            Item(MenuIcons.OrderedListHeader(), () => run(NoteCommand.OrderedList)),
            new Separator(),
            Item(MenuIcons.HeadingHeader("Заголовок 1"), () => run(NoteCommand.Heading1)),
            Item(MenuIcons.HeadingHeader("Заголовок 2"), () => run(NoteCommand.Heading2)),
            Item(MenuIcons.HeadingHeader("Заголовок 3"), () => run(NoteCommand.Heading3)),
            Item(MenuIcons.HeadingHeader("Заголовок 4"), () => run(NoteCommand.Heading4)),
            Item(MenuIcons.HeadingHeader("Заголовок 5"), () => run(NoteCommand.Heading5)),
            Item(MenuIcons.HeadingHeader("Заголовок 6"), () => run(NoteCommand.Heading6)),
            Item(MenuIcons.PlainHeader("Обычный текст"), () => run(NoteCommand.PlainText)),
            new Separator(),
            Item(MenuIcons.QuoteHeader(),       () => run(NoteCommand.Quote)),
        ]));

        menu.Items.Add(Sub("Вставка",
        [
            Item(MenuIcons.TableHeader(),     () => run(NoteCommand.Table)),
            Item(MenuIcons.CodeBlockHeader(), () => run(NoteCommand.CodeBlock)),
            Item(MenuIcons.RuleHeader(),      () => run(NoteCommand.Rule)),
        ]));

        return menu;
    }

    private static MenuItem Item(Control header, Action run)
    {
        var item = new MenuItem { Header = header };
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
