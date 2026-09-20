// Стенд панели чтения документа: живая Avalonia, настоящая разметка панели, настоящие файлы.
//
// Зачем отдельный стенд, если есть модульные тесты. Модульные проверяют разбор документа
// (Tests/Integration/DocReadTextTests) и правила подсветки (Tests/Unit/DocHighlightTests) — то
// есть числа и строки. А половина того, что может сломаться в панели, ломается НЕ в числах:
//
//   * ссылка на ресурс в разметке (иконка, кисть, токен размера) разрешается при ЗАГРУЗКЕ, и
//     опечатка в ключе роняет панель в тот момент, когда человек нажал «Прочитать». Сборка
//     такого не видит — StaticResource компилятор не проверяет;
//   * подсветка собирается из Inlines кодом, и «собралась ли она» видно только по дереву
//     готового TextBlock, а не по списку совпадений;
//   * переход к следующему совпадению — это ScrollIntoView по виртуализованному списку, и
//     его работоспособность тоже проверяется только на живом контроле.
//
// Стенд гоняет эти три вещи на временных файлах и печатает отчет. Отрисовка headless'ная
// (UseHeadlessDrawing = true): пиксели здесь не нужны, нужна раскладка и разрешение ресурсов.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
// Пространство имен таблиц вводится ПСЕВДОНИМОМ: в нем есть свои Control и Run, и без
// псевдонима они спорят с одноименными типами Avalonia.
using X = DocumentFormat.OpenXml.Spreadsheet;
using SmartBase.Core.Models;
using SmartBase.Views.Library;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

AppBuilder.Configure<Application>()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
          .SetupWithoutStarting();

// Каскад стилей — как в App.axaml: панель берет из него и иконки, и кисти подсветки, и размеры
// текста. Без него разметка не загрузится вовсе, и это первая проверка стенда.
var app     = Application.Current!;
var baseUri = new Uri("avares://SmartBase/");
app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
foreach (var name in new[] { "Colors", "Icons" })
    app.Resources.MergedDictionaries.Add(new ResourceInclude(baseUri)
    {
        Source = new Uri($"avares://SmartBase/Assets/Styles/{name}.axaml"),
    });
foreach (var name in new[] { "Typography", "WindowMetrics", "Controls" })
    app.Styles.Add(new StyleInclude(baseUri)
    {
        Source = new Uri($"avares://SmartBase/Assets/Styles/{name}.axaml"),
    });
SmartBase.Utils.FontScale.Init();

var dir = Path.Combine(Path.GetTempPath(), "sb-readerstand-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);

var failures = 0;
try
{
    Run();
}
finally
{
    try { Directory.Delete(dir, recursive: true); } catch { /* занят — уберет система */ }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "Готово: расхождений нет." : $"Расхождений: {failures}");
return failures == 0 ? 0 : 1;

void Run()
{
    var path = Path.Combine(dir, "договор.txt");
    File.WriteAllText(path,
        """
        ДОГОВОР ПОДРЯДА № 001/2024
        город Санкт-Петербург

        1. ПРЕДМЕТ ДОГОВОРА
        Подрядчик обязуется выполнить работы по разработке программного обеспечения.
        Заказчик обязуется принять результаты работ и оплатить их.
        """);

    var doc = new DocRecord
    {
        Title          = "Договор подряда",
        Path           = path,
        CollectionName = "Договоры",
        DbName         = "Основная",
    };

    var reader = new DocReaderView();
    var window = new Window { Width = 520, Height = 700, Content = reader };
    window.Show();

    // Панель читает документ в фоне — ждем, пока строки появятся в дереве.
    reader.Show(doc, "договор");
    if (!Wait(() => Lines(reader).Count > 0, "панель не показала ни одной строки")) return;

    var lines = Lines(reader);
    Console.WriteLine($"Строк в панели: {lines.Count}");
    Check(lines.Count >= 5, "строки документа разошлись: ожидалось не меньше пяти");

    // Переводы строк дожили до экрана: одна строка файла — одна строка панели.
    Check(lines.Any(l => l.Contains("ПРЕДМЕТ ДОГОВОРА", StringComparison.Ordinal)),
          "строка «1. ПРЕДМЕТ ДОГОВОРА» не найдена отдельной строкой");

    // Подсветка собрана. Слов «договор» в тексте два: «ДОГОВОР» в заголовке и «ДОГОВОРА» в
    // названии раздела — второе ловится по началу слова (последнему слову запроса приписан «*»).
    // Считаются ПОКАЗАННЫЕ строки: список виртуализован, и в окне такого размера видны все.
    var highlighted = Highlighted(reader);
    Console.WriteLine("Подсвечено слов: " + highlighted.Count + " — "
                      + string.Join(", ", highlighted.Select(h => h.Text)));
    Check(highlighted.Count == 2, $"подсвечено {highlighted.Count} слов вместо двух");
    Check(highlighted.All(h => h.Text.StartsWith("ДОГОВОР", StringComparison.OrdinalIgnoreCase)),
          "подсвечено не то слово");

    // Текущее совпадение отличается цветом от прочих — иначе переходы «дальше» не видно.
    Check(highlighted.Select(h => h.Fill).Distinct().Count() == 2,
          "текущее совпадение покрашено так же, как остальные");

    // Счетчик совпадений говорит правду и ведет к первому.
    var counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Console.WriteLine("Счетчик: " + counter);
    Check(counter == "1 из 2", $"счетчик показывает «{counter}» вместо «1 из 2»");

    // Переход к следующему совпадению: счетчик сдвинулся, панель не упала.
    Find<Button>(reader, "NextBtn")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Pump();
    counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Console.WriteLine("Счетчик после «дальше»: " + counter);
    Check(counter == "2 из 2", $"после перехода счетчик показывает «{counter}» вместо «2 из 2»");

    // У текстового файла настоящего вида нет и быть не может — кнопка режима погашена, но
    // видна: пропади она, полоса меняла бы ширину от документа к документу.
    var pagesBtn = Find<ToggleButton>(reader, "DocModeBtn");
    Check(pagesBtn is { IsEnabled: false }, "у текстового файла режим «как в документе» доступен");

    // Скан без текстового слоя: панель обязана объяснить это словами, а не пустотой.
    var empty = Path.Combine(dir, "скан.txt");
    File.WriteAllText(empty, "   \n\n  ");
    reader.Show(new DocRecord { Title = "Скан", Path = empty }, "договор");
    if (!Wait(() => (Find<TextBlock>(reader, "StateLabel")?.IsVisible ?? false), "панель не показала состояние"))
        return;

    var state = Find<TextBlock>(reader, "StateLabel")?.Text ?? "";
    Console.WriteLine("Состояние на скане: " + state);
    Check(state.Contains("скан", StringComparison.OrdinalIgnoreCase),
          "про скан без текстового слоя ничего не сказано");

    Pages(reader, window);
}

// ── Режим «как в документе» ─────────────────────────────────────────────────────

void Pages(DocReaderView reader, Window window)
{
    Console.WriteLine();
    Console.WriteLine("Режим «как в документе»:");

    var pdf = Path.Combine(dir, "договор.pdf");
    using (var builder = new PdfDocumentBuilder())
    {
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var p = 1; p <= 3; p++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText("Dogovor podryada", 14, new PdfPoint(40, 780), font);
            page.AddText($"Stranica {p}",     12, new PdfPoint(40, 750), font);
        }
        File.WriteAllBytes(pdf, builder.Build());
    }

    reader.Show(new DocRecord { Title = "Договор", Path = pdf }, "dogovor");

    var pagesBtn = Find<ToggleButton>(reader, "DocModeBtn");
    if (!Wait(() => pagesBtn is { IsEnabled: true }, "режим «как в документе» так и не включился"))
        return;

    Console.WriteLine("Кнопка режима доступна у PDF: да");

    // Переключаемся: список страниц виден, список строк спрятан.
    pagesBtn!.IsChecked = true;
    Pump();
    var pagesList = Find<ListBox>(reader, "PagesList");
    var linesList = Find<ListBox>(reader, "LinesList");
    Check(pagesList is { IsVisible: true }, "список страниц не показан");
    Check(linesList is { IsVisible: false }, "список строк остался на экране");
    Check(pagesList?.ItemCount == 3, $"страниц в списке {pagesList?.ItemCount}, а в документе 3");

    // Совпадения ищутся по словам страниц: «Dogovor» стоит на каждой из трех.
    if (!Wait(() => (Find<TextBlock>(reader, "CounterLabel")?.Text ?? "").Contains("из 3", StringComparison.Ordinal),
              "совпадения на страницах не сосчитаны"))
        return;

    var counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Console.WriteLine("Счетчик по страницам: " + counter);
    Check(counter.StartsWith("1 из 3", StringComparison.Ordinal),
          $"счетчик показывает «{counter}» вместо «1 из 3»");

    // Возврат в текст ничего не ломает, и счетчик снова считает по строкам.
    Find<ToggleButton>(reader, "TextModeBtn")!.IsChecked = true;
    Pump();
    Check(Find<ListBox>(reader, "LinesList") is { IsVisible: true }, "по возврату не показан текст");

    Sheets(reader);
}

// ── Лист книги таблицей ─────────────────────────────────────────────────────────

void Sheets(DocReaderView reader)
{
    Console.WriteLine();
    Console.WriteLine("Книга Excel таблицей:");

    var book = Path.Combine(dir, "смета.xlsx");
    WriteBook(book);

    reader.Show(new DocRecord { Title = "Смета", Path = book }, "труба");

    var modeBtn = Find<ToggleButton>(reader, "DocModeBtn");
    if (!Wait(() => modeBtn is { IsEnabled: true }, "режим «как в документе» у книги не включился"))
        return;

    modeBtn!.IsChecked = true;
    Pump();

    // Подпись выбранного режима обязана быть ВИДНА. Проверка не на пустом месте: тема Fluent
    // красит подпись выбранного ToggleButton белым прямо в подаче содержимого, и без нашей
    // поправки кнопка выглядела пустой — белым по белому.
    CheckCaption(modeBtn);

    var host = Find<Grid>(reader, "SheetHost");
    var rows = Find<ListBox>(reader, "SheetRows");
    Check(host is { IsVisible: true },  "таблица не показана");
    Check(Find<ListBox>(reader, "PagesList") is { IsVisible: false }, "вместо таблицы показаны страницы");
    // Строк в списке БОЛЬШЕ, чем в листе: снизу дописаны пустые — лист занимает блок целиком,
    // как в Excel, и пустого места под таблицей не остается.
    Console.WriteLine("Строк таблицы: " + rows?.ItemCount);
    Check(rows?.ItemCount >= 3, $"строк в таблице {rows?.ItemCount}, а в листе 3");

    // Полоса с буквами колонок — по ней человек понимает, что видит лист, а не просто сетку.
    var head = Find<Decorator>(reader, "SheetHead");
    Check(head?.Child is { Bounds.Height: > 0 }, "полоса с буквами колонок не показана");

    // Высота строки берется из книги: у третьей строки она своя, и на экране она обязана быть
    // выше обычной. Именно этого не было, когда ячейка с переносом резалась посередине букв.
    var heights = rows?.GetVisualDescendants().OfType<ListBoxItem>()
                       .Select(i => Math.Round(i.Bounds.Height)).ToList() ?? [];
    Console.WriteLine("Высоты строк: " + string.Join(", ", heights));
    Check(heights.Count >= 3 && heights[2] > heights[1] + 10,
          "строка со своей высотой нарисована такой же, как обычная");

    // Заполнитель добивает лист до нижнего края блока, и последняя строка при этом НЕ ужата:
    // она такая же, как обычная, и просто уходит за край под полосу прокрутки.
    var filled = heights.Sum();
    Console.WriteLine($"Клетки занимают {filled} из {Math.Round(rows?.Bounds.Height ?? 0)} по высоте");
    Check(rows is not null && filled >= rows.Bounds.Height - 8,
          "под таблицей осталось пустое место");
    Check(heights.Count > 3 && heights[^1] >= heights[1],
          $"последняя строка ужата: {heights.LastOrDefault()} против обычной {heights.ElementAtOrDefault(1)}");

    // Обе полосы прокрутки и квадрат на их пересечении: без квадрата в углу белое пятно.
    var vbar   = Find<ScrollBar>(reader, "SheetBar");
    var hbar   = Find<ScrollBar>(reader, "SheetHBar");
    var corner = Find<Border>(reader, "SheetCorner");
    Console.WriteLine($"Полосы листа: вертикальная {vbar?.IsVisible}, горизонтальная {hbar?.IsVisible}, "
                      + $"угол {Math.Round(corner?.Bounds.Width ?? 0)}×{Math.Round(corner?.Bounds.Height ?? 0)}");
    Check(vbar is { IsVisible: true } && hbar is { IsVisible: true },
          "у заполненного листа показана не всякая полоса прокрутки");
    Check(corner is { Bounds.Width: > 0, Bounds.Height: > 0 },
          "на пересечении полос прокрутки нет квадрата");

    // Вкладки листов: их две, и вторая переключает лист.
    var tabs = Find<StackPanel>(reader, "SheetTabs");
    Console.WriteLine("Вкладок листов: " + tabs?.Children.Count);
    Check(tabs?.Children.Count == 2, "вкладки листов не собрались");

    // Счет идет по ВСЕЙ книге: «труба» есть и на первом листе, и на втором.
    var counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Console.WriteLine("Счетчик по книге: " + counter);
    Check(counter == "1 из 2", $"счетчик показывает «{counter}» вместо «1 из 2»");

    // Переход ко второму совпадению уводит на ДРУГОЙ лист — иначе счет по книге обманывал бы.
    Find<Button>(reader, "NextBtn")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Pump();
    var second = tabs?.Children.ElementAtOrDefault(1) as ToggleButton;
    Check(second?.IsChecked == true, "переход к совпадению не открыл второй лист");
    Console.WriteLine("Лист после «дальше»: " + (second?.Content as string));
}

void WriteBook(string path)
{
    using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
    var book      = doc.AddWorkbookPart();
    book.Workbook = new X.Workbook();

    var first = book.AddNewPart<WorksheetPart>();
    first.Worksheet = new X.Worksheet(
        new X.SheetData(
            new X.Row(new X.Cell { CellReference = "A1", DataType = X.CellValues.String, CellValue = new X.CellValue("Смета работ") }) { RowIndex = 1 },
            new X.Row(
                new X.Cell { CellReference = "A2", DataType = X.CellValues.String, CellValue = new X.CellValue("Наименование") },
                new X.Cell { CellReference = "B2", DataType = X.CellValues.String, CellValue = new X.CellValue("Количество") }) { RowIndex = 2 },
            // Третья строка выше обычной — так ее задали в книге.
            new X.Row(
                new X.Cell { CellReference = "A3", DataType = X.CellValues.String, CellValue = new X.CellValue("Труба стальная") },
                new X.Cell { CellReference = "B3", CellValue = new X.CellValue("12") })
                { RowIndex = 3, Height = 60, CustomHeight = true }),
        new X.MergeCells(new X.MergeCell { Reference = "A1:B1" }));
    first.Worksheet.Save();

    var second = book.AddNewPart<WorksheetPart>();
    second.Worksheet = new X.Worksheet(new X.SheetData(
        new X.Row(new X.Cell { CellReference = "A1", DataType = X.CellValues.String, CellValue = new X.CellValue("Труба запасная") }) { RowIndex = 1 }));
    second.Worksheet.Save();

    book.Workbook.AppendChild(new X.Sheets(
        new X.Sheet { Id = book.GetIdOfPart(first),  SheetId = 1, Name = "Смета" },
        new X.Sheet { Id = book.GetIdOfPart(second), SheetId = 2, Name = "Запас" }));
    book.Workbook.Save();
}

// ── Инструменты стенда ──────────────────────────────────────────────────────────

void Check(bool ok, string complaint)
{
    if (ok) return;
    failures++;
    Console.WriteLine("  РАСХОЖДЕНИЕ: " + complaint);
}

// Подпись кнопки видна, только если ее цвет отличается от цвета подложки. Сравниваются именно
// подача содержимого и текст внутри нее: цвет, назначенный самой кнопке, до текста может и не
// дойти — тема вправе назначить свой прямо в подаче.
void CheckCaption(ToggleButton button)
{
    var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
    var label     = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
    var ink       = (label?.Foreground as ISolidColorBrush)?.Color;
    var paper     = (presenter?.Background as ISolidColorBrush)?.Color;

    Console.WriteLine($"Подпись выбранного режима: «{label?.Text}» цветом {ink} на {paper}");
    Check(ink is not null && ink != paper, "подпись выбранного режима слилась с его подложкой");
}

// Ждем условия, прокручивая очередь диспетчера: чтение документа идет в фоне и возвращается
// на UI-поток.
bool Wait(Func<bool> until, string complaint, int attempts = 200)
{
    for (var i = 0; i < attempts; i++)
    {
        Pump();
        if (until()) return true;
        Thread.Sleep(10);
    }
    Check(false, complaint);
    return false;
}

static void Pump() => Dispatcher.UIThread.RunJobs();

// Строки ищем ВНУТРИ списка строк, а не по всей панели: подписи кнопок режима — тоже
// TextBlock, и по всей панели они попали бы в «строки документа».
static List<string> Lines(Control reader) =>
    Find<ListBox>(reader, "LinesList") is { } list
        ? [.. list.GetVisualDescendants().OfType<TextBlock>().Select(Text).Where(s => s.Length > 0)]
        : [];

static string Text(TextBlock block) =>
    block.Inlines is { Count: > 0 } inlines
        ? string.Concat(inlines.OfType<Run>().Select(r => r.Text))
        : block.Text ?? "";

// Подсвеченные куски: те Run, которым назначен фон. Именно так панель и рисует совпадение;
// цвет фона возвращается вместе с текстом — по нему видно текущее совпадение.
static List<(string Text, string Fill)> Highlighted(Control reader) =>
    [.. (Find<ListBox>(reader, "LinesList") as Control ?? reader).GetVisualDescendants().OfType<TextBlock>()
              .SelectMany(t => t.Inlines?.OfType<Run>() ?? [])
              .Where(r => r.Background is not null)
              .Select(r => (r.Text ?? "", r.Background!.ToString() ?? ""))];

static T? Find<T>(Control root, string name) where T : Control =>
    root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
