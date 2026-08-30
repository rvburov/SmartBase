// Стенд раскладки markdown: собирает документ виджетом AppMarkdown, меряет его настоящим
// шрифтом и проверяет то, что видно только после раскладки.
//
// Зачем отдельный стенд. Дерево контролов проверяют модульные тесты
// (Tests/Unit/MarkupRenderTests), но они работают БЕЗ платформы: текст там не меряется и не
// переносится. А самые дорогие ошибки живут именно в раскладке — на этом стенде пойманы две:
//
//   * `LineSpacing` входит в измеренную высоту, но не входит в раскладку отрисовки, и на
//     настоящем шрифте ПОСЛЕДНЯЯ СТРОКА абзаца переставала рисоваться. Лечится высотой строки
//     (`LineHeight`) вместо просвета;
//   * просвет между блоками обязан равняться пустым строкам исходника — иначе «Просмотр»
//     расходится с «Динамическим просмотром», где просвет дают настоящие пустые строки файла.
//
// Стиль приложения сюда не поднимается: без него размеры приходят запасными значениями
// MarkupTheme, а проверяется устройство раскладки, а не палитра.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using SmartBase.Controls.Widgets;

AppBuilder.Configure<Application>()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
          .SetupWithoutStarting();

// Стенду нужны настоящие ресурсы приложения: редактор заметок собирает контекстное меню из
// реестра иконок, а размеры и цвета берутся из общих токенов. Тема Fluent — под саму AvaloniaEdit.
var app     = Application.Current!;
var baseUri = new Uri("avares://SmartBase/");
app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
foreach (var name in new[] { "Colors", "Icons" })
    app.Resources.MergedDictionaries.Add(
        new Avalonia.Markup.Xaml.Styling.ResourceInclude(baseUri)
        {
            Source = new Uri($"avares://SmartBase/Assets/Styles/{name}.axaml"),
        });
app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri)
{
    Source = new Uri("avares://SmartBase/Assets/Styles/Typography.axaml"),
});
SmartBase.Utils.FontScale.Init();

var failed = 0;

// Сколько занимает заголовок вместе с воздухом в «Просмотре». Считается там, сверяется с
// редактором: это и есть число, из-за которого режимы расходятся или совпадают.
double HeadingBlockHeight = 0;

Check("абзац не теряет перенесенные строки", """
    Длинный абзац, который обязан перенестись на несколько строк, чтобы стало видно, теряется ли последняя из них при отрисовке.
    """,
    blocks =>
    {
        var block = (TextBlock)blocks[0];
        Expect(block.TextLayout.TextLines.Count > 1, "абзац должен переноситься");
        // Раскладка отрисовки обязана совпадать с измеренной высотой: если она ниже, разница
        // и есть потерянные строки.
        Expect(Math.Abs(block.TextLayout.Height - block.Bounds.Height) <= 1,
               $"высота раскладки {block.TextLayout.Height:F1} против измеренной {block.Bounds.Height:F1}");
    });

Check("просвет равен пустым строкам исходника", """
    вплотную

    через одну


    через две
    """,
    blocks =>
    {
        var line = ((TextBlock)blocks[0]).TextLayout.Height;   // высота одной строки текста
        Expect(Near(blocks[0].Margin.Bottom, line),     "одна пустая строка — один просвет");
        Expect(Near(blocks[1].Margin.Bottom, line * 2), "две пустые строки — два просвета");
        Expect(blocks[2].Margin.Bottom == 0,            "последний блок без просвета");
    });

Check("заголовок отбит воздухом и не обрезан", """
    Текст до.
    # Заголовок
    Текст после.
    """,
    blocks =>
    {
        var heading = (TextBlock)blocks[1];
        // Строка заголовка ТА ЖЕ, что у текста: в редакторе высота строки одна на все. Глифы в
        // нее не влезают и выступают, поэтому обрезка по границам блока обязана быть снята.
        Expect(Near(heading.Bounds.Height, blocks[0].Bounds.Height),
               $"строка заголовка {heading.Bounds.Height:F1} равна строке текста {blocks[0].Bounds.Height:F1}");
        Expect(!heading.ClipToBounds, "заголовку разрешено выступать за строку, иначе он обрезан");

        // Пустых строк вокруг заголовка в исходнике нет, а просвет быть обязан — и ровно
        // такой, какой дала бы пустая строка: тогда с редактором совпадает до пикселя.
        var line = ((TextBlock)blocks[0]).TextLayout.Height;
        Expect(Near(blocks[0].Margin.Bottom, line), "перед заголовком просвет в одну строку");
        Expect(Near(blocks[1].Margin.Bottom, line), "после заголовка просвет в одну строку");

        // Главное число: сколько занимает заголовок вместе с воздухом. Оно обязано совпасть
        // с редактором — там это три текстовые строки (пустая, заголовок, пустая).
        HeadingBlockHeight = blocks[0].Margin.Bottom + heading.Bounds.Height + blocks[1].Margin.Bottom;
        Console.WriteLine($"    заголовок с воздухом занимает {HeadingBlockHeight:F1}");
    });

// ── Редактор заметок: тот же воздух вокруг заголовка ────────────────────────────
//
// Высота ОДНОЙ текстовой строки в AvaloniaEdit жестко фиксирована, но строка документа состоит
// из скольких угодно текстовых строк — на этом стоит перенос по словам. Пустая строка вокруг
// заголовка и делается лишней текстовой строкой, поэтому проверяется их число.

// Пустая строка принадлежит строке НАД ней: иначе каретка следующей строки рисуется на воздухе.
Editor("заголовок вплотную к тексту — воздух добавлен", "текст до\n# Заголовок\nтекст после",
       [1, 2, 2]);

Editor("заголовок уже отбит пустыми строками — второго воздуха нет",
       "текст до\n\n# Заголовок\n\nтекст после", [1, 1, 1, 1, 1]);

Editor("без заголовков воздуха нет вовсе", "первый\nвторой\nтретий", [1, 1, 1]);

Console.WriteLine(failed == 0 ? "\nВсе проверки пройдены." : $"\nНе пройдено проверок: {failed}.");
return failed == 0 ? 0 : 1;

void Check(string name, string markdown, Action<IReadOnlyList<Control>> assert)
{
    Console.WriteLine($"\n{name}");

    var view = new AppMarkdown { Markdown = markdown };
    view.Measure(new Size(320, double.PositiveInfinity));
    view.Arrange(new Rect(0, 0, 320, view.DesiredSize.Height));

    foreach (var child in view.Children)
    {
        var text  = child is TextBlock t ? t.Inlines?.Text ?? "" : child.GetType().Name;
        var lines = child is TextBlock tb ? tb.TextLayout.TextLines.Count : 0;
        Console.WriteLine($"    высота={child.Bounds.Height,6:F1} просвет={child.Margin.Bottom,6:F1} "
                          + $"строк={lines} | {text[..Math.Min(34, text.Length)]}");
    }
    assert(view.Children);
}

// Сколько текстовых строк заняла каждая строка документа. Единица — обычная строка, двойка —
// строка с добавленной пустой.
void Editor(string name, string text, int[] expected)
{
    Console.WriteLine($"\n{name}");

    var box    = new NoteEditorBox { Text = text, LiveMarkup = true };
    var window = new Window { Width = 400, Height = 300, Content = box };
    window.Show();
    window.UpdateLayout();

    var view = box.GetVisualDescendants().OfType<TextView>().FirstOrDefault();
    if (view is null) { Expect(false, "редактор не построил разметку"); return; }

    var actual = view.Document.Lines.Select(l => view.GetOrConstructVisualLine(l).TextLines.Count).ToArray();
    Console.WriteLine($"    текстовых строк по строкам документа: {string.Join(", ", actual)}");
    Expect(actual.SequenceEqual(expected), $"ожидалось {string.Join(", ", expected)}");

    // Заголовок вместе с воздухом обязан занимать столько же, сколько в «Просмотре». В
    // редакторе это: пустая строка + строка заголовка (обе в его визуальной строке) плюс пустая
    // строка, приписанная к следующей строке документа.
    if (expected.SequenceEqual([1, 2, 2]) && HeadingBlockHeight > 0)
    {
        var heights = view.Document.Lines.Select(l => view.GetOrConstructVisualLine(l).Height).ToArray();
        var line    = heights[0];                        // обычная строка без воздуха
        var total   = heights[1] + (heights[2] - line);  // заголовок с воздухом сверху и снизу

        // Допуск в пиксель: «Просмотр» округляет высоту блока до целых устройства.
        Expect(Math.Abs(total - HeadingBlockHeight) <= 1,
               $"заголовок с воздухом занимает {total:F1}, в «Просмотре» {HeadingBlockHeight:F1}");
    }
}

void Expect(bool ok, string what)
{
    Console.WriteLine(ok ? $"    ок:     {what}" : $"    ПРОВАЛ: {what}");
    if (!ok) failed++;
}

static bool Near(double a, double b) => Math.Abs(a - b) <= 0.5;
