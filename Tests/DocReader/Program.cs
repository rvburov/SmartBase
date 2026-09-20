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
using SmartBase.Core.Models;
using SmartBase.Views.Library;

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
}

// ── Инструменты стенда ──────────────────────────────────────────────────────────

void Check(bool ok, string complaint)
{
    if (ok) return;
    failures++;
    Console.WriteLine("  РАСХОЖДЕНИЕ: " + complaint);
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

static List<string> Lines(Control reader) =>
    [.. reader.GetVisualDescendants().OfType<TextBlock>()
              .Where(t => t.Name is null)                 // строки документа — безымянные
              .Select(Text)
              .Where(s => s.Length > 0)];

static string Text(TextBlock block) =>
    block.Inlines is { Count: > 0 } inlines
        ? string.Concat(inlines.OfType<Run>().Select(r => r.Text))
        : block.Text ?? "";

// Подсвеченные куски: те Run, которым назначен фон. Именно так панель и рисует совпадение;
// цвет фона возвращается вместе с текстом — по нему видно текущее совпадение.
static List<(string Text, string Fill)> Highlighted(Control reader) =>
    [.. reader.GetVisualDescendants().OfType<TextBlock>()
              .SelectMany(t => t.Inlines?.OfType<Run>() ?? [])
              .Where(r => r.Background is not null)
              .Select(r => (r.Text ?? "", r.Background!.ToString() ?? ""))];

static T? Find<T>(Control root, string name) where T : Control =>
    root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
