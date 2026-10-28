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

using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Utils;
using SmartBase.Core.Documents;
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
var sections = new List<(string Name, int Failures)>();
TaskCompletionSource? textRelease = null;
try
{
    Run();
}
finally
{
    try { Directory.Delete(dir, recursive: true); } catch { /* занят — уберет система */ }
}

// Сводка по разделам: по одному итоговому числу не понять, что именно покраснело.
Console.WriteLine();
Console.WriteLine("Разделы:");
foreach (var (name, count) in sections)
    Console.WriteLine($"  {name}: {(count == 0 ? "в порядке" : $"расхождений {count}")}");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "Готово: расхождений нет." : $"Расхождений: {failures}");
return failures == 0 ? 0 : 1;

// Разделы независимы: каждый открывает свой документ, и несбывшееся ожидание обрывает только
// свой раздел. Раньше разделы звали друг друга по цепочке, и один провал уносил из отчета все,
// что шло после, — проверки книги целиком. Исключение в разделе — тоже расхождение, и остальные
// разделы оно не останавливает.
void Run()
{
    var reader = new DocReaderView(async (path, maxMb, ct) =>
    {
        var release = textRelease;
        if (release is not null) await release.Task.WaitAsync(ct);
        return await DocReadText.ReadAsync(path, maxMb, ct);
    });
    var host = new Grid { Children = { reader } };
    var window = new Window { Width = 520, Height = 700, Content = host };
    window.Show();

    if (args.Contains("--only-page-layout", StringComparer.Ordinal))
    {
        window.Close();
        Section("переходы страниц при смене раскладки", () => PageLayoutNavigationChecks.Run(dir, Check));
        return;
    }

    Section("текстовый файл",            () => TextFile(reader));
    Section("текст без текстового слоя", () => TextScan(reader));
    Section("подсказки кнопки режима",   () => ModeHints(reader));
    Section("скан PDF в двух режимах",   () => ScanModes(reader));
    Section("как в документе",           () => Pages(reader, window));
    Section("переходы страниц при смене раскладки", () => PageLayoutNavigationChecks.Run(dir, Check));
    Section("текст дочитывается позже",  () => LateText(reader));
    Section("лист книги таблицей",       () => Sheets(reader));
    Section("закрепленные области",      () => FrozenPanes(reader));
    Section("клавиши библиотеки",        () => LibraryKeys(reader, host));
    Section("стрелка панели документа",  () => ReaderArrow(reader));
    Section("большая книга",             () => BigBook(reader));
    Section("закрытие панели",           () => DetachReader(reader, host));
    window.Close();
    Section("подписки панелей",          PanelSubscriptions);
    Section("отмена подкачки ленты",      CancelFeed);
    Section("сортировка страниц двух баз", SortedPages);
    Section("отмена общей сортировки",    CancelOrder);
}

void Section(string name, Action body)
{
    var before = failures;
    try { body(); }
    catch (Exception ex) { Check(false, $"раздел упал: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    sections.Add((name, failures - before));
}

void ReaderArrow(DocReaderView reader)
{
    var toggle = Find<ToggleButton>(reader, "ListPanelBtn")!;
    var changes = new List<bool>();
    void Changed(object? sender, bool open) => changes.Add(open);
    reader.ListPanelToggled += Changed;
    try
    {
        reader.SetListPanelOpen(true);
        Check(Find<Control>(reader, "CloseBtn") is null, "крестик заменен стрелкой");
        Check(toggle.IsChecked == true && Find<Control>(reader, "ListCloseIcon")!.IsVisible,
            "при открытом списке стрелка указывает на раскрытие документа");
        toggle.IsChecked = false;
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(changes.SequenceEqual(new[] { false }) && Find<Control>(reader, "ListOpenIcon")!.IsVisible,
            "нажатие передает скрытие списка и разворачивает стрелку");
        Check(ToolTip.GetTip(toggle)?.ToString() == "Показать список документов", "подсказка раскрытой панели");
        reader.SetListPanelOpen(true);
        Check(changes.Count == 1 && ToolTip.GetTip(toggle)?.ToString() == "Развернуть документ",
            "синхронизация состояния не отправляет повторное действие");
    }
    finally { reader.ListPanelToggled -= Changed; reader.SetListPanelOpen(true); }
}

// ── Текстовый файл ──────────────────────────────────────────────────────────────

void TextFile(DocReaderView reader)
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
}

// ── Текст без текстового слоя ───────────────────────────────────────────────────

void TextScan(DocReaderView reader)
{
    // Скан без текстового слоя: панель обязана объяснить это словами, а не пустотой.
    var pagesBtn = Find<ToggleButton>(reader, "DocModeBtn");
    var empty = Path.Combine(dir, "скан.txt");
    File.WriteAllText(empty, "   \n\n  ");
    reader.Show(new DocRecord { Title = "Скан", Path = empty }, "договор");
    // Ждем не ВИДИМОСТИ метки, а самого объяснения: той же меткой панель говорит «Читаю
    // документ…», пока читает, и проверка ловила эту заглушку — стенд краснел на ровном месте,
    // сравнивая слово «скан» с текстом про чтение.
    if (!Wait(() => Find<TextBlock>(reader, "StateLabel") is { IsVisible: true } label
                    && !(label.Text ?? "").StartsWith("Читаю", StringComparison.Ordinal),
              "панель так и не объяснила, что с документом"))
        return;

    var state = Find<TextBlock>(reader, "StateLabel")?.Text ?? "";
    Console.WriteLine("Состояние на скане: " + state);
    Check(state.Contains("скан", StringComparison.OrdinalIgnoreCase),
          "про скан без текстового слоя ничего не сказано");

    // Почему кнопка режима погашена, говорит подсказка, и она ВИДНА: у погашенной кнопки
    // подсказку включает ShowOnDisabled — без него все объяснения молчали.
    Check(ToolTip.GetShowOnDisabled(pagesBtn!), "у погашенной кнопки режима подсказка не показывается");
    var txtHint = ToolTip.GetTip(pagesBtn!) as string ?? "";
    Console.WriteLine("Подсказка у текстового файла: " + txtHint);
    Check(txtHint.Contains("текстового файла", StringComparison.Ordinal),
          $"у текстового файла подсказка «{txtHint}»");
}

// ── Подсказки кнопки режима ─────────────────────────────────────────────────────

void ModeHints(DocReaderView reader)
{
    Console.WriteLine();
    Console.WriteLine("Подсказки кнопки режима:");
    var btn = Find<ToggleButton>(reader, "DocModeBtn")!;

    // Старая книга XLS таблицей не показывается, и подсказка называет ее формат. Раньше она
    // говорила «страницами пока показывается только PDF» — для книги это неправда.
    var xls = Path.Combine(dir, "смета.xls");
    File.WriteAllBytes(xls, [0xD0, 0xCF, 0x11, 0xE0]);
    reader.Show(new DocRecord { Title = "Смета", Path = xls }, "");
    var hint = ToolTip.GetTip(btn) as string ?? "";
    Console.WriteLine("У книги XLS: " + hint);
    Check(hint.Contains("XLS — нет", StringComparison.Ordinal), $"у книги XLS подсказка «{hint}»");

    // Книга, которая не открылась, — не пустая книга: прежде подсказка говорила «в книге нет
    // листов с данными».
    var broken = Path.Combine(dir, "битая.xlsx");
    File.WriteAllText(broken, "это не книга");
    reader.Show(new DocRecord { Title = "Битая", Path = broken }, "");
    if (!Wait(() => ToolTip.GetTip(btn) as string == DocModeHint.BookFailed,
              "у книги, которая не открылась, подсказка не говорит об этом"))
        return;
    Console.WriteLine("У битой книги: " + ToolTip.GetTip(btn));

    // Книга тяжелее порога чтения таблицей не разбирается — как и текстом. Раньше «Как в
    // документе» разбирал ее целиком, хотя «Текст» от нее отказывался. Порог на время проверки —
    // мегабайт, и файл в мегабайт с байтом не книга: содержимое не важно, до разбора дело не
    // доходит, а без порога подделка честно «не открылась» бы.
    var limit = SmartBase.Utils.LibrarySettings.IndexMaxMb;
    SmartBase.Utils.LibrarySettings.SetIndexMaxMb(1);
    try
    {
        var heavy = Path.Combine(dir, "тяжелая.xlsx");
        using (var file = File.Create(heavy)) file.SetLength(1024 * 1024 + 1);
        reader.Show(new DocRecord { Title = "Тяжелая", Path = heavy }, "");
        var label = Find<TextBlock>(reader, "StateLabel")!;
        if (!Wait(() => ToolTip.GetTip(btn) as string == DocModeHint.BookTooBig(1) &&
                        (label.Text ?? "").Contains("тяжелее порога", StringComparison.Ordinal),
                  "у книги тяжелее порога подсказка кнопки или надпись «Текста» не называют порог"))
            return;
        Console.WriteLine("У тяжелой книги: " + ToolTip.GetTip(btn));
        Console.WriteLine("Текст тяжелой книги: " + label.Text);
        Check(!btn.IsEnabled, "у книги тяжелее порога кнопка «Как в документе» включена");
    }
    finally { SmartBase.Utils.LibrarySettings.SetIndexMaxMb(limit); }
}

// ── Скан PDF в двух режимах ─────────────────────────────────────────────────────

void ScanModes(DocReaderView reader)
{
    Console.WriteLine();
    Console.WriteLine("Скан PDF в двух режимах:");

    // Страница без текстового слоя: у текста — объяснение, у страниц — сама страница.
    var scan = Path.Combine(dir, "скан.pdf");
    using (var builder = new PdfDocumentBuilder())
    {
        builder.AddPage(595, 842);
        File.WriteAllBytes(scan, builder.Build());
    }
    reader.Show(new DocRecord { Title = "Скан", Path = scan }, "договор");

    var btn   = Find<ToggleButton>(reader, "DocModeBtn")!;
    var label = Find<TextBlock>(reader, "StateLabel")!;
    if (!Wait(() => btn.IsEnabled && (label.Text ?? "").Contains("скан", StringComparison.OrdinalIgnoreCase),
              "скан PDF не объяснен или его страницы не открылись"))
        return;

    btn.IsChecked = true;
    Pump();
    Check(!label.IsVisible, "в режиме страниц надпись о скане закрыла страницы");
    Check(Find<ListBox>(reader, "PagesList") is { IsVisible: true }, "страницы скана не показаны");

    // Возврат в «Текст»: объяснение на месте. Раньше режим страниц затирал надпись, и текст
    // скана показывался пустым списком.
    Find<ToggleButton>(reader, "TextModeBtn")!.IsChecked = true;
    Pump();
    var state = label.Text ?? "";
    Console.WriteLine("После возврата в «Текст»: " + state);
    Check(label.IsVisible && state.Contains("скан", StringComparison.OrdinalIgnoreCase),
          "после возврата в «Текст» объяснение про скан пропало");
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
            // На первой странице ВТОРОЕ совпадение — у самого низа листа. Страница при ширине
            // панели выше окна, и до этого слова надо прокрутить саму страницу: ровно тот
            // случай, на котором стрелка «дальше» когда-то не двигала вид вовсе.
            if (p == 1) page.AddText("Dogovor v podvale", 12, new PdfPoint(40, 20), font);
            // Сумма с разрядами — на странице это два слова, «30» и «772,48».
            if (p == 2) page.AddText("Itogo 30 772,48 rub", 12, new PdfPoint(40, 720), font);
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

    // Совпадения ищутся по словам страниц: «Dogovor» стоит на каждой из трех, а на первой
    // еще и в подвале — всего четыре.
    if (!Wait(() => (Find<TextBlock>(reader, "CounterLabel")?.Text ?? "").Contains("из 4", StringComparison.Ordinal),
              "совпадения на страницах не сосчитаны"))
        return;

    var counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Console.WriteLine("Счетчик по страницам: " + counter);
    Check(counter.StartsWith("1 из 4", StringComparison.Ordinal),
          $"счетчик показывает «{counter}» вместо «1 из 4»");

    // Стрелка обязана подвезти слово к глазам ВНУТРИ страницы, а не просто довезти список до
    // нее. Пока она везла к странице, два совпадения на одной странице не двигали вид вовсе, и
    // на глаз это выглядело сломанной стрелкой: счетчик шел, а на экране ничего не менялось.
    var pagesScroll = pagesList!.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    if (pagesScroll is null) { Check(false, "у ленты страниц не нашлось прокрутки"); return; }

    Console.WriteLine($"Лента страниц: окно {pagesScroll.Viewport.Height:0}, " +
                      $"весь размах {pagesScroll.Extent.Height:0}");
    Check(pagesScroll.Extent.Height > pagesScroll.Viewport.Height + 1,
          "страница целиком влезла в окно — проверять прокрутку не на чем");

    var before = pagesScroll.Offset.Y;
    Find<Button>(reader, "NextBtn")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    if (!Wait(() => pagesScroll.Offset.Y > before + 1,
              "стрелка не подвезла совпадение, стоящее в подвале той же страницы"))
        return;

    Console.WriteLine($"Сдвиг к совпадению в подвале страницы: {before:0} → {pagesScroll.Offset.Y:0}");

    counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Check(counter.StartsWith("2 из 4", StringComparison.Ordinal),
          $"после перехода счетчик показывает «{counter}» вместо «2 из 4»");

    // Позиция обязана устоять и через паузу пересчета (200 мс). Show вписывает запрос в строку
    // сам, а событие о правке строки приходит отложенно — и прежде запускало второй пересчет,
    // который через паузу отбрасывал к первому совпадению. Ждем втрое дольше паузы.
    var settle = DateTime.UtcNow.AddMilliseconds(600);
    while (DateTime.UtcNow < settle) { Pump(); Thread.Sleep(10); }
    counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Check(counter.StartsWith("2 из 4", StringComparison.Ordinal),
          $"через паузу пересчета счетчик откатился к «{counter}»");

    RenderWidthChange(reader, window);

    // Сумму набирают подряд, без разрядов. Индекс находит по «30772» документ, где она стоит
    // как «30 772,48», и страница обязана подсветить совпадение: пока слова страницы сверялись
    // поодиночке, ни «30», ни «772,48» с запросом не совпадали, и счетчик говорил «нет».
    reader.SetQuery("30772");
    if (!Wait(() => (Find<TextBlock>(reader, "CounterLabel")?.Text ?? "").StartsWith("1 из 1", StringComparison.Ordinal),
              "сумма из двух слов страницы не найдена по числу без разрядов"))
        return;

    Console.WriteLine("Сумма из двух слов страницы по «30772»: " + Find<TextBlock>(reader, "CounterLabel")?.Text);

    // Возврат в текст ничего не ломает, и счетчик снова считает по строкам.
    Find<ToggleButton>(reader, "TextModeBtn")!.IsChecked = true;
    Pump();
    Check(Find<ListBox>(reader, "LinesList") is { IsVisible: true }, "по возврату не показан текст");
}

// ── Смена ширины, пока страница рисуется ────────────────────────────────────────

// Панель сузили, пока страница рисовалась под прежнюю ширину. Картинка под прежнюю ширину
// приходит уже после смены и раньше ложилась на страницу насовсем: страница, которая «уже
// рисуется», второй раз не просилась, а потом у нее была картинка — и она так и оставалась
// растянутой. Порядок здесь задан руками, без гонки.
//
// Пикселей картинки стенд не видит — отрисовка безголовая, картинка в ней заглушка, — поэтому
// сверяется не ширина картинки, а то, чья она. Путей два:
//   * страница дорисовалась под прежнюю ширину, и итог ждет очереди. Пока он приходит,
//     страница уведена с экрана: просить ее заново некому, и картинка на ней может быть только
//     от прежней отрисовки — ее быть не должно;
//   * отрисовка еще ждала замка источника — ее бросают. Кадры между сменой ширины и отменой
//     съедают просьбу самой смены: страница в эту минуту «рисуется» и второй раз не просится,
//     так что заново ее просит только брошенная отрисовка.
void RenderWidthChange(DocReaderView reader, Window window)
{
    Console.WriteLine();
    Console.WriteLine("Смена ширины, пока страница рисуется:");

    var list  = Find<ListBox>(reader, "PagesList")!;
    var pages = (System.Collections.IList)Get(reader, "_pages")!;
    var page  = pages[0]!;
    int  Width()   => (int)Get(reader, "_pageWidth")!;
    bool Drawn()   => Prop(page, "Bitmap") is not null;
    bool Loading() => Prop(page, "Loading") is true;

    list.ScrollIntoView(0);
    if (!WaitFrames(Drawn, "первая страница так и не нарисовалась")) return;

    // Перерисовать страницу заново: картинку снять и запустить отрисовку, как это делает просьба
    // страницы, — но без очереди, чтобы порядок задавал стенд.
    Task Redraw()
    {
        Call(page, "SetBitmap", new object?[] { null });
        SetProp(page, "Loading", true);
        return (Task)Call(reader, "RenderPageAsync", page, (int)Get(reader, "_gen")!)!;
    }

    // Сменить ширину на заметную долю: мелкую смену панель не отрабатывает.
    bool Narrow(double share)
    {
        var before = Width();
        list.Width = list.Bounds.Width * share;
        window.UpdateLayout();
        Call(reader, "ReflowPages");
        Console.WriteLine($"Ширина страниц: {before} → {Width()}");
        if (Width() != before) return true;
        Check(false, "ширина страниц не сменилась — проверять нечего");
        return false;
    }

    try
    {
        // Первый путь: страница дорисовалась до смены, ее итог ждет очереди.
        var drawing = Redraw();
        Thread.Sleep(300);
        list.ScrollIntoView(pages.Count - 1);
        window.UpdateLayout();
        if (!Narrow(0.6)) return;
        if (!Wait(() => drawing.IsCompleted && !Loading(), "прежняя отрисовка страницы так и не кончилась")) return;
        Console.WriteLine($"Итог отрисовки под прежнюю ширину: {(Drawn() ? "лег на страницу" : "брошен")}");
        Check(!Drawn(), "картинка под прежнюю ширину легла на страницу");

        list.ScrollIntoView(0);
        if (!WaitFrames(Drawn, "страница после смены ширины так и не нарисовалась")) return;

        // Второй путь: отрисовка ждет замка источника и попадает под отмену.
        var gate = Get(Get(reader, "_source")!, "_lock")!;
        Monitor.Enter(gate);
        try
        {
            drawing = Redraw();
            if (!Narrow(1.4)) return;
            Frames(5);
        }
        finally { Monitor.Exit(gate); }
        if (!Wait(() => drawing.IsCompleted, "брошенная отрисовка страницы так и не кончилась")) return;
        var redrawn = WaitFrames(Drawn, "брошенную страницу не попросили заново");
        Console.WriteLine($"Брошенная отрисовка: страница {(redrawn ? "попрошена заново и нарисована" : "осталась пустой")}");
    }
    finally
    {
        list.Width = double.NaN;
        window.UpdateLayout();
    }
}

// ── Текст дочитывается позже страниц ────────────────────────────────────────────

// Том на триста страниц (потолок панели): страницы открываются сразу, а текст дочитывается заметно позже. Человек
// успевает перейти к страницам и шагнуть по совпадениям — и дочитавшийся текст раньше отбрасывал
// его к первому совпадению, перезапуская обход страниц.
void LateText(DocReaderView reader)
{
    Console.WriteLine();
    Console.WriteLine("Текст дочитывается позже страниц:");

    var tome = Path.Combine(dir, "том.pdf");
    using (var builder = new PdfDocumentBuilder())
    {
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var p = 1; p <= 300; p++)
        {
            var page = builder.AddPage(595, 842);
            page.AddText($"Dogovor, stranica {p}", 12, new PdfPoint(40, 780), font);
            for (var line = 0; line < 60; line++)
                page.AddText($"Punkt {p}.{line}: tekst toma dlya chteniya", 10, new PdfPoint(40, 740 - line * 12), font);
        }
        File.WriteAllBytes(tome, builder.Build());
    }

    textRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
        reader.Show(new DocRecord { Title = "Том", Path = tome }, "dogovor");

        var btn   = Find<ToggleButton>(reader, "DocModeBtn")!;
        var lines = Find<ListBox>(reader, "LinesList")!;
        string Counter() => Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
        // Текст дочитан — у списка строк есть источник. ItemCount для этого не годится: после
        // сброса источника он держит число строк прошлого документа.
        bool TextLoaded() => lines.ItemsSource is System.Collections.ICollection { Count: > 0 };

        if (!Wait(() => btn.IsEnabled, "страницы тома не открылись", attempts: 3000)) return;
        btn.IsChecked = true;
        Pump();

        // Два совпадения на страницах — и шаг ко второму.
        if (!Wait(() => HitsAtLeast(Counter(), 2), "на страницах тома не нашлось и двух совпадений")) return;
        Find<Button>(reader, "NextBtn")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump();

        Console.WriteLine($"Шаг по страницам: {Counter()}, текст к этой минуте {(TextLoaded() ? "дочитан" : "еще читается")}");
        Check(!TextLoaded(), "текст показан до разрешения разбора");
        textRelease.SetResult();

        if (!Wait(TextLoaded, "текст тома так и не дочитался", attempts: 1500)) return;
        var settle = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < settle) { Pump(); Thread.Sleep(10); }

        var counter = Counter();
        Console.WriteLine("Когда текст дочитался: " + counter);
        Check(counter.StartsWith("2 из", StringComparison.Ordinal),
              $"дочитавшийся текст отбросил к «{counter}» вместо второго совпадения");

        // Совпадения только в самом конце тома: «290» стоит лишь на двести девяностой странице —
        // в ее заголовке и в шестидесяти пунктах. Пока обход страниц до нее не дошел, счетчик не
        // говорит ни «нет» — совпадение еще найдется, — ни число прежнего запроса: раньше «0 из 300»
        // по прошлому слову стояло, пока обход не находил первое новое.
        reader.SetQuery("290");
        var midway = Counter();
        Console.WriteLine($"Сразу после смены запроса: «{midway}»");
        Check(midway.Length == 0, $"пока обход страниц не кончился, счетчик показывает «{midway}»");
        var found = Wait(() => Counter() == "1 из 61", "совпадения в конце тома не сосчитаны", attempts: 1500);
        Console.WriteLine("Совпадения в конце тома: " + Counter());
        if (!found) return;
    }
    finally
    {
        textRelease.TrySetResult();
        textRelease = null;
    }
}

// «2 из 37» — совпадений на страницах уже не меньше двух.
static bool HitsAtLeast(string counter, int count)
{
    var at = counter.IndexOf(" из ", StringComparison.Ordinal);
    return at > 0 && int.TryParse(counter[(at + 4)..].Split(' ')[0], out var total) && total >= count;
}

// ── Лист книги таблицей ─────────────────────────────────────────────────────────

void FrozenPanes(DocReaderView reader)
{
    var path = Path.Combine(dir, "frozen.xlsx");
    WriteBigBook(path, 1, 120, 16);
    using (var doc = SpreadsheetDocument.Open(path, true))
    {
        var sheet = doc.WorkbookPart!.WorksheetParts.First().Worksheet;
        sheet.PrependChild(new X.SheetViews(new X.SheetView(new X.Pane
            { State = X.PaneStateValues.Frozen, VerticalSplit = 2, HorizontalSplit = 1 }) { WorkbookViewId = 0 }));
        sheet.Save();
    }
    reader.Show(new DocRecord { Title = "Закрепление", Path = path }, "");
    var mode = Find<ToggleButton>(reader, "DocModeBtn")!;
    if (!Wait(() => mode.IsEnabled, "книга с закреплением не открылась")) return;
    mode.IsChecked = true;
    Frames(4);
    var pinned = Find<StackPanel>(reader, "FrozenRows")!;
    var rows = Find<ListBox>(reader, "SheetRows")!;
    Check(pinned.Children.Count == 2 && pinned.Bounds.Height > 0, "закрепленные строки не показаны отдельно");
    Check((int)Get(reader, "_frozenColumns")! == 1, "закрепленная колонка потеряна");
    var before = pinned.TranslatePoint(default, reader);
    var scroll = rows.GetVisualDescendants().OfType<ScrollViewer>().First();
    scroll.Offset = new Vector(400, 700);
    Frames(4);
    Check(scroll.Offset.X > 0 && scroll.Offset.Y > 0, "контрольная прокрутка не сработала");
    Check(pinned.TranslatePoint(default, reader) == before, "закрепленные строки уехали при прокрутке");
    Check(Math.Abs((double)Get(reader, "_scrollX")! - scroll.Offset.X) < 1, "закрепленная шапка не получила горизонтальный сдвиг");
    Check(pinned.Children.All(c => c.Bounds.Height > 0), "закрепленные строки имеют нулевую высоту");
}

void LibraryKeys(DocReaderView reader, Grid host)
{
    var list = new DocListView();
    host.Children.Add(list);
    Frames(3);
    var refresh = 0;
    list.RefreshRequested += (_, _) => refresh++;
    void Key(Control control, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
    try
    {
        Key(list, Avalonia.Input.Key.F5);
        Check(refresh == 1, "F5 не обновил ленту ровно один раз");
        ShortcutSettings.SetGesture("library.refresh", ShortcutGesture.Parse("F6")!.Value);
        Key(list, Avalonia.Input.Key.F5);
        Check(refresh == 1, "старая клавиша работает после переназначения");
        Key(list, Avalonia.Input.Key.F6);
        Check(refresh == 2, "новая клавиша не обновила ленту");
        Key(list, Avalonia.Input.Key.F, KeyModifiers.Control);
        Check(Find<TextBox>(list, "SearchBox")!.IsFocused, "Ctrl+F не перевел фокус в поиск ленты");
        var path = Path.Combine(dir, "keys.txt");
        File.WriteAllText(path, "ключ один\nключ два\nключ три");
        var document = new DocRecord { Id = 37, DbId = 1, FileFieldId = 1, Title = "Клавиши", Path = path };
        var card = new DocCardVm(document);
        list.Show(new List<object> { card });
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var released = new PointerEventArgs(InputElement.PointerReleasedEvent, list, pointer, list,
            new Point(1, 1), 0, PointerPointProperties.None, KeyModifiers.None);
        Call(list, "OnCardTapped", card, new TappedEventArgs(InputElement.TappedEvent, released));
        Check(list.IsFocused && list.Selection.Contains(document), "выбор карточки оставил фокус в поиске");
        var read = 0;
        var opened = 0;
        list.DocReadRequested += (_, _) => read++;
        list.DocActivated += (_, _) => opened++;
        Key(list, Avalonia.Input.Key.Enter);
        Key(list, Avalonia.Input.Key.Enter, KeyModifiers.Control);
        Check(read == 1 && opened == 1, "Enter и Ctrl+Enter не различают чтение и открытие выбранного");
        reader.Show(document, "ключ");
        if (Wait(() => Find<TextBlock>(reader, "CounterLabel")?.Text == "1 из 3", "нет совпадений для проверки F3"))
        {
            Key(reader, Avalonia.Input.Key.F3);
            Check(Find<TextBlock>(reader, "CounterLabel")?.Text == "2 из 3", "F3 не перешел к следующему совпадению");
            Key(reader, Avalonia.Input.Key.F3, KeyModifiers.Shift);
            Check(Find<TextBlock>(reader, "CounterLabel")?.Text == "1 из 3", "Shift+F3 не вернулся к предыдущему совпадению");
        }
        var closed = 0;
        void Close(object? sender, EventArgs e) => closed++;
        reader.CloseRequested += Close;
        try
        {
            Key(reader, Avalonia.Input.Key.Escape);
            Check(closed == 1, "Escape не закрыл панель чтения");
        }
        finally { reader.CloseRequested -= Close; }
    }
    finally
    {
        ShortcutSettings.Reset("library.refresh");
        host.Children.Remove(list);
    }
}

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

    // Ключи ресурсов, которыми панель красит таблицу. Промах в ключе тут ТИХИЙ: у кисти есть
    // запасной цвет, и таблица просто станет не той, не сообщив об этом ни строчкой.
    foreach (var key in new[] { "BrushRed", "BrushScrollbarTrack", "BrushSecondaryBg",
                                "BrushSeparator", "BrushPrimaryText", "BrushSecondaryText" })
        Check(app.TryGetResource(key, null, out _), $"кисть «{key}» не найдена");

    // Оформление доехало от книги до листа: заливка и жирная шапка — то, по чему таблицу
    // узнают с первого взгляда.
    var painted = SmartBase.Core.Documents.DocSheetSource.Open(book)[0];
    var crown   = painted.PaintOf(painted.Rows[1].Cells[0]);
    Console.WriteLine($"Шапка листа: заливка {crown.Fill:X8}, жирная {crown.Bold}");
    Check(crown.Fill != 0 && crown.Bold, "оформление шапки не доехало до листа");

    // Объединение знает СТРОКА, а не только ячейка: линии сетки ведет строка, и без этого
    // между слитыми клетками остается линия, которой в книге нет.
    var span = painted.Rows[0].Merges;
    Console.WriteLine("Объединений в первой строке: " + span.Count);
    Check(span.Count == 1 && span[0].From == 0 && span[0].To == 1,
          "объединение шапки не доехало до строки");

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

    // Корешки листов: их два, между ними линия, и второй переключает лист.
    var strip  = Find<StackPanel>(reader, "SheetTabs");
    var tabs   = strip?.Children.OfType<ToggleButton>().ToList() ?? [];
    var splits = strip?.Children.OfType<Border>().ToList() ?? [];
    Console.WriteLine($"Корешков листов: {tabs.Count}, линий между ними: {splits.Count}");
    Check(tabs.Count == 2,   "корешки листов не собрались");
    Check(splits.Count == 1, "между корешками листов нет разделительной линии");

    // Линия короче полосы: она делит подписи, а не режет полосу на клетки.
    var strong = splits.Count == 1 && strip is not null
                 && splits[0].Bounds.Height > 0
                 && splits[0].Bounds.Height < strip.Bounds.Height;
    Check(strong, "линия между корешками идет во всю высоту полосы");

    // Полоса корешков прижата к краям блока: полей вокруг нее нет.
    var tabsBox = Find<Border>(reader, "SheetTabsBox");
    Console.WriteLine($"Полоса корешков: {tabsBox?.Bounds}, поля {tabsBox?.Margin}");
    Check(tabsBox is { IsVisible: true, Margin.Left: 0, Margin.Top: 0, Margin.Right: 0, Margin.Bottom: 0 },
          "у полосы корешков остались поля");

    // Стрелки листают книгу и гаснут на ее краях.
    var prevSheet = Find<Button>(reader, "SheetPrevBtn");
    var nextSheet = Find<Button>(reader, "SheetNextBtn");
    Console.WriteLine($"Стрелки листов: назад {prevSheet?.IsEnabled}, вперед {nextSheet?.IsEnabled}");
    Check(prevSheet is { IsEnabled: false } && nextSheet is { IsEnabled: true },
          "стрелки листов не знают, что открыт первый лист");

    nextSheet?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Pump();
    Check(tabs[1].IsChecked == true, "стрелка не открыла следующий лист");
    Check(prevSheet is { IsEnabled: true } && nextSheet is { IsEnabled: false },
          "стрелки листов не знают, что открыт последний лист");

    // Возвращаемся на первый лист: дальше стенд считает совпадения от начала книги.
    prevSheet?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Pump();

    // Счет идет по ВСЕЙ книге: «труба» есть и на первом листе, и на втором. Книгу обходят в
    // фоне — ждем, пока обход кончится.
    Wait(() => (Find<TextBlock>(reader, "CounterLabel")?.Text ?? "") == "1 из 2", "совпадения книги не сосчитаны");
    var counter = Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";
    Console.WriteLine("Счетчик по книге: " + counter);
    Check(counter == "1 из 2", $"счетчик показывает «{counter}» вместо «1 из 2»");

    // Переход ко второму совпадению уводит на ДРУГОЙ лист — иначе счет по книге обманывал бы.
    Find<Button>(reader, "NextBtn")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Pump();
    var second = tabs.ElementAtOrDefault(1);
    Check(second?.IsChecked == true, "переход к совпадению не открыл второй лист");
    Console.WriteLine("Лист после «дальше»: " + (second?.Content as string));

    // Совпадение на втором листе стоит далеко вправо: панель обязана довезти таблицу до него.
    // Подсвеченная ячейка за краем окна — то же самое, что ненайденная.
    var sheetScroll = Find<ListBox>(reader, "SheetRows")?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    for (var i = 0; i < 20; i++) { Pump(); Thread.Sleep(10); }
    Console.WriteLine($"Сдвиг таблицы к совпадению: {sheetScroll?.Offset.X:F0} " +
                      $"из {sheetScroll?.Extent.Width:F0}, окно {sheetScroll?.Viewport.Width:F0}");
    Check(sheetScroll is { Extent.Width: > 0 } && sheetScroll.Extent.Width > sheetScroll.Viewport.Width,
          "лист стенда уместился в окно — сдвиг вбок проверять не на чем");
    Check(sheetScroll is { Offset.X: > 0 }, "панель не увезла таблицу к найденной ячейке");

    // Возвращаемся на первый лист — он КОРОЧЕ второго, и именно на таком переходе видно
    // остатки: строк на экране больше, чем в листе.
    Find<Button>(reader, "NextBtn")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    for (var i = 0; i < 20; i++) { Pump(); Thread.Sleep(10); }

    // Строк на экране ровно столько, сколько в открытом листе, и каждая на своем месте. Смена
    // листа умеет оставлять на экране контролы ПРОШЛОГО листа: они продолжают рисовать старые
    // данные поверх новых, и книга выглядит перемешанной.
    var list = Find<ListBox>(reader, "SheetRows");
    var live = list?.GetVisualDescendants().OfType<ListBoxItem>()
                    .Where(i => i.IsEffectivelyVisible).ToList() ?? [];
    var deck = live.GroupBy(i => Math.Round(i.Bounds.Top, 1)).Where(g => g.Count() > 1).ToList();
    Console.WriteLine($"Строк на экране: {live.Count} при {list?.ItemCount} в листе «{tabs[0].Content}»");
    Check(deck.Count == 0, $"строки наложились друг на друга в {deck.Count} местах — листы перемешались");
    Check(live.Count <= (list?.ItemCount ?? 0),
          "на экране строк больше, чем в открытом листе — остались строки прошлого");
}

// ── Большая книга ───────────────────────────────────────────────────────────────

// Три листа по две тысячи строк и тридцать колонок. Совпадения книги считаются по каждой
// ячейке, и прежде этот обход шел на UI-потоке целиком: правка запроса держала панель, пока он
// не кончится.
void BigBook(DocReaderView reader)
{
    Console.WriteLine();
    Console.WriteLine("Большая книга:");

    var path = Path.Combine(dir, "реестр.xlsx");
    var sw   = Stopwatch.StartNew();
    WriteBigBook(path, sheets: 3, rows: 2000, cols: 30);
    Console.WriteLine($"Книга 3 × 2000 × 30 записана за {sw.ElapsedMilliseconds} мс");

    reader.Show(new DocRecord { Title = "Реестр", Path = path }, "");
    var btn = Find<ToggleButton>(reader, "DocModeBtn")!;
    if (!Wait(() => btn.IsEnabled, "большая книга не открылась таблицей", attempts: 6000)) return;
    btn.IsChecked = true;
    Pump();
    string Counter() => Find<TextBlock>(reader, "CounterLabel")?.Text ?? "";

    if (!Wait(() => Find<ListBox>(reader, "LinesList")?.ItemsSource is System.Collections.ICollection { Count: > 0 },
              "текст большой книги не дочитался", attempts: 6000)) return;
    var textHits = (System.Collections.ICollection)Get(reader, "_hits")!;
    Check(textHits.Count == 0, "у книги без запроса уже есть совпадения текста");

    // Правка запроса не ждет обхода книги: панель свободна сразу, а счетчик до конца обхода
    // молчит — не «нет» и не прежнее число. Сравнивается с самим обходом: пока он шел на
    // UI-потоке, правка запроса держала панель ровно столько, сколько он длился.
    sw.Restart();
    reader.SetQuery("задвижка");
    var held   = sw.Elapsed.TotalMilliseconds;
    var midway = Counter();
    Console.WriteLine($"Смена запроса держала панель {held:0} мс, счетчик сразу после: «{midway}»");
    Check(midway.Length == 0, $"пока книга обходится, счетчик показывает «{midway}»");
    var counted = Wait(() => Counter() == "1 из 12", "совпадения большой книги не сосчитаны", attempts: 3000);
    var total   = sw.Elapsed.TotalMilliseconds;
    Console.WriteLine($"Обход книги кончился через {total:0} мс: «{Counter()}»");
    if (!counted) return;
    // Проверяем причину задержки, а не отношение двух времен под нагрузкой машины:
    // скрытый текст не обходится; его подсветка догоняет запрос при показе.
    Check(textHits.Count == 0, "смена запроса пересчитала скрытый текст книги");

    // Запрос сменили, пока прежний обход шел: «позиция» есть почти в каждой ячейке, и ее обход
    // долгий. Его совпадения не попадают в новый счет — ни сразу, ни через паузу.
    reader.SetQuery("позиция");
    reader.SetQuery("задвижка");
    var recount = Wait(() => Counter() == "1 из 12", "после смены запроса посреди обхода счет не сошелся", attempts: 3000);
    var settle  = DateTime.UtcNow.AddMilliseconds(800);
    while (DateTime.UtcNow < settle) { Pump(); Thread.Sleep(10); }
    Console.WriteLine("После смены запроса посреди обхода: " + Counter());
    if (recount) Check(Counter() == "1 из 12", $"прежний обход дописал свое: «{Counter()}»");
    Find<ToggleButton>(reader, "TextModeBtn")!.IsChecked = true;
    Pump();
    Check(textHits.Count == 12, $"после возврата в текст найдено {textHits.Count} вместо 12");

}

void DetachReader(DocReaderView reader, Grid host)
{
    Find<ToggleButton>(reader, "DocModeBtn")!.IsChecked = true;
    Pump();
    var find = (DispatcherTimer)Get(reader, "_findDebounce")!;
    var width = (DispatcherTimer)Get(reader, "_widthDebounce")!;
    find.Start();
    width.Start();
    var token = ((CancellationTokenSource)Get(reader, "_docCts")!).Token;
    host.Children.Remove(reader);
    Pump();
    Check(!find.IsEnabled && !width.IsEnabled, "после снятия панели таймеры продолжают работать");
    Check(token.IsCancellationRequested, "после снятия панели разбор не отменен");
    Check(reader.Document is null && Get(reader, "_source") is null, "снятая панель держит документ");
    host.Children.Add(reader);
    var path = Path.Combine(dir, "после-снятия.txt");
    File.WriteAllText(path, "Проверка повторного открытия");
    reader.Show(new DocRecord { Title = "Проверка", Path = path }, "Проверка");
    Wait(() => Find<TextBlock>(reader, "CounterLabel")?.Text == "1 из 1", "панель не работает после повторного присоединения");
}

void PanelSubscriptions()
{
    var service = new SmartBase.Database.DocIndex.DocIndexService(
        new SmartBase.Database.DocIndex.DocTextIndex(Path.Combine(dir, "lifecycle-index.db")));
    SmartBase.Database.DocIndex.DocIndexService.Attach(service);
    var library = new LibraryControllerView();
    var bar = new SmartBase.Controls.Components.ActivityBar();
    var barSubscriptions = new HashSet<Delegate>();
    var sidebar = new SmartBase.Controls.Components.SidebarTree();
    var host = new StackPanel { Children = { library, bar, sidebar } };
    var root = new Grid();
    var window = new Window { Content = root };
    try
    {
        Check(Subscriptions(typeof(SmartBase.Utils.LibrarySettings), "Changed", library) == 0,
              "не показанная библиотека уже удерживается настройками");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            root.Children.Add(host);
            window.Show();
            Pump();
            Check(Subscriptions(typeof(SmartBase.Utils.LibrarySettings), "Changed", library) == 1,
                  "показанная библиотека не слушает настройки ровно один раз");
            Check(Subscriptions(typeof(SmartBase.Utils.LibrarySettings), "Changed", sidebar) == 1,
                  "сайдбар не слушает настройки библиотеки ровно один раз");
            Check(Subscriptions(service.GetType(), "Progress", library, service) == 1,
                  "показанная библиотека не слушает прогресс ровно один раз");
            // Объединение обновлений подписывает closure конкретного attach, а не метод
            // с Target == bar. Сохраняем сами delegate: после detach поле уже обнулено,
            // но прежний обработчик в invocation list все равно должен быть обнаружен.
            var barSubscription = Get(bar, "_backgroundJobsChanged") as Delegate;
            if (barSubscription is not null) barSubscriptions.Add(barSubscription);
            Check(barSubscription is not null &&
                  DelegateSubscriptions(typeof(SmartBase.Core.BackgroundJobs), "Changed", bar, barSubscriptions) == 1,
                  "показанная панель разделов не слушает фоновые работы ровно один раз");
            root.Children.Remove(host);
            Pump();
            Check(Subscriptions(typeof(SmartBase.Utils.LibrarySettings), "Changed", library) == 0 &&
                  Subscriptions(typeof(SmartBase.Utils.FontScale), "Changed", library) == 0,
                  "снятая библиотека удерживается настройками");
            Check(Subscriptions(service.GetType(), "Progress", library, service) == 0 &&
                  Subscriptions(service.GetType(), "Finished", library, service) == 0,
                  "снятая библиотека удерживается очередью чтения");
            Check(DelegateSubscriptions(typeof(SmartBase.Core.BackgroundJobs), "Changed", bar, barSubscriptions) == 0,
                  "снятая панель разделов удерживается реестром работ");
            Check(Subscriptions(typeof(SmartBase.Utils.LibrarySettings), "Changed", sidebar) == 0,
                  "снятый сайдбар удерживается настройками библиотеки");
        }
    }
    finally
    {
        window.Close();
        SmartBase.Database.DocIndex.DocIndexService.DetachAsync().AsTask().GetAwaiter().GetResult();
    }
}

void CancelFeed()
{
    using var database = SmartBase.Database.DatabaseManager.Open(73, "Стенд ленты", Path.Combine(dir, "feed.db"));
    var repo = database.Constructor;
    var collection = repo.AddCollection(new DbCollection { Name = "Документы" }).Id!.Value;
    var file = repo.AddField(new DbField { CollectionId = collection, Name = "Файл", FieldType = "file" }).Id!.Value;
    repo.SetFileFieldInLibrary(file, true);
    var record = repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Страница ленты" }).Id!.Value;
    var path = Path.Combine(dir, "feed.txt");
    File.WriteAllText(path, "Документ ленты");
    repo.SetFieldValue(record, file, path);

    DocVirtualList Feed() => new([new DocVirtualList.Entry
    {
        DbId = database.DbId, DbName = database.Name, ColId = collection, Repo = repo,
        TypeFieldId = repo.DocTypeFieldId(collection)!.Value,
        View = SmartBase.Database.Repositories.ConstructorRepository.DocView.All, Docs = 1,
    }]);

    var extra = repo.AddRecord(new DbRecord { CollectionId = collection, Title = "Альфа" }).Id!.Value;
    repo.SetFieldValue(extra, file, path);
    var entries = new List<DocVirtualList.Entry> { new()
    {
        DbId = database.DbId, DbName = database.Name, ColId = collection, Repo = repo,
        TypeFieldId = repo.DocTypeFieldId(collection)!.Value,
        View = SmartBase.Database.Repositories.ConstructorRepository.DocView.All, Docs = 2,
    } };
    var keysTask = DocVirtualList.BuildOrderAsync(entries, DocSort.Title);
    if (!Wait(() => keysTask.IsCompleted, "общая сортировка не построилась")) return;
    var ordered = new DocVirtualList(entries, order: keysTask.GetAwaiter().GetResult());
    var first = (DocCardVm)ordered[0];
    var last = (DocCardVm)ordered[1];
    Call(ordered, "FlushPendingLoads");
    if (!Wait(() => !first.IsPlaceholder && !last.IsPlaceholder, "сортированная страница не загрузилась")) return;
    Check(first.Doc.Id == extra && last.Doc.Id == record, "сортированная страница загрузилась в исходном порядке");
    Check(first.HasDb && first.DbText == database.Name && !first.SubtitleValue.Contains(database.Name),
          "бейдж базы отсутствует или дублируется в подписи");
    ordered.Cancel();
    repo.DeleteRecord(extra);

    // Контроль: тот же настоящий источник загружает карточку, если его не отменять.
    var live = Feed();
    var liveCard = (DocCardVm)live[0];
    Call(live, "FlushPendingLoads");
    if (!Wait(() => !liveCard.IsPlaceholder, "контрольная страница ленты не загрузилась", attempts: 1500)) return;
    Check(liveCard.Doc.Path == path, "контрольная карточка загрузила другой документ");
    live.Cancel();

    var cancelled = Feed();
    var loaded = 0;
    cancelled.CardRealized += _ => loaded++;
    var card = (DocCardVm)cancelled[0];
    cancelled.Cancel();
    // Вручную наступает срок отложенной подкачки: тест не зависит от 90 мс таймера.
    Call(cancelled, "FlushPendingLoads");
    if (!Wait(() => ((HashSet<int>)Get(cancelled, "_pending")!).Count == 0,
              "отмененная подкачка не закончилась")) return;
    Pump();
    Check(card.IsPlaceholder && loaded == 0, "отмененный список все же загрузил документ");
    cancelled.Cancel();
}

void CancelOrder()
{
    using var cts = new CancellationTokenSource();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<List<DocOrderItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var reads = 0;
    using var database = SmartBase.Database.DatabaseManager.Open(93, "Отмена", Path.Combine(dir, "sort-cancel.db"));
    var col = database.Constructor.AddCollection(new DbCollection { Name = "Источник" }).Id!.Value;
    DocVirtualList.Entry Entry(int id) => new() { DbId = id, DbName = "Источник " + id, ColId = col,
        Repo = database.Constructor, TypeFieldId = -1,
        View = SmartBase.Database.Repositories.ConstructorRepository.DocView.All };
    var task = DocVirtualList.BuildOrderAsync([Entry(1), Entry(2)], DocSort.Title,
        (_, token) =>
        {
            Check(token == cts.Token, "запрос источника не получил токен отмены");
            reads++;
            entered.TrySetResult();
            return release.Task;
        }, cts.Token);
    if (!Wait(() => entered.Task.IsCompleted, "первый источник не запрошен")) return;
    cts.Cancel();
    release.SetResult([new(1, 1, "Документ")]);
    if (!Wait(() => task.IsCompleted, "отмена не завершила построение порядка")) return;
    Check(task.IsCanceled && reads == 1, "отмененная сортировка продолжила читать источники или вернула результат");

    var library = new LibraryControllerView();
    var host = new Grid { Children = { library } };
    var window = new Window { Content = host };
    window.Show();
    Pump();
    var field = typeof(LibraryControllerView).GetField("_loadCts", BindingFlags.Instance | BindingFlags.NonPublic)!;
    using var onHide = new CancellationTokenSource();
    using var onReload = new CancellationTokenSource();
    using var onDetach = new CancellationTokenSource();
    try
    {
        field.SetValue(library, onHide);
        library.IsVisible = false;
        Check(onHide.IsCancellationRequested && (bool)Get(library, "_loadDeferred")!,
              "скрытие библиотеки не остановило подготовку с отложенным повтором");
        library.IsVisible = true;
        Pump();
        Check(!(bool)Get(library, "_loadDeferred")!, "возврат в библиотеку не возобновил отложенную загрузку");
        field.SetValue(library, onReload);
        Call(library, "LoadAsync", false, false);
        Check(onReload.IsCancellationRequested, "новая загрузка не отменила прежнюю сортировку");
        field.SetValue(library, onDetach);
        host.Children.Remove(library);
        Pump();
        Check(onDetach.IsCancellationRequested, "снятая библиотека продолжает сортировку");
    }
    finally { field.SetValue(library, null); window.Close(); }
}

void SortedPages()
{
    using var a = SmartBase.Database.DatabaseManager.Open(91, "Первая", Path.Combine(dir, "sort-a.db"));
    using var b = SmartBase.Database.DatabaseManager.Open(92, "Вторая", Path.Combine(dir, "sort-b.db"));
    var path = Path.Combine(dir, "sort.txt");
    File.WriteAllText(path, "Общий файл");
    var entries = new List<DocVirtualList.Entry>();
    var selected = new List<DocKey>();
    foreach (var db in new[] { a, b })
    {
        var repo = db.Constructor;
        var col = repo.AddCollection(new DbCollection { Name = "Порядок" }).Id!.Value;
        var field = repo.AddField(new DbField { CollectionId = col, Name = "Файл", FieldType = "file" }).Id!.Value;
        repo.SetFileFieldInLibrary(field, true);
        var count = db == a ? 300 : 301;
        for (var i = 0; i < count; i++)
        {
            var number = db == a ? 600 - i * 2 : 601 - i * 2;
            var record = repo.AddRecord(new DbRecord { CollectionId = col, Title = number.ToString("D4") }).Id!.Value;
            repo.SetFieldValue(record, field, path);
            if (number is 1 or 301 or 601) selected.Add(new DocKey(db.DbId, record, field, path));
        }
        entries.Add(new DocVirtualList.Entry { DbId = db.DbId, DbName = db.Name, Repo = repo,
            ColId = col, TypeFieldId = repo.DocTypeFieldId(col)!.Value,
            View = SmartBase.Database.Repositories.ConstructorRepository.DocView.All, Docs = count });
    }
    var task = DocVirtualList.BuildOrderAsync(entries, DocSort.Title);
    if (!Wait(() => task.IsCompleted, "каталог двух баз не построен")) return;
    var list = new DocVirtualList(entries, selected, task.GetAwaiter().GetResult());
    var indices = new[] { 0, 300, 600 };
    var cards = indices.Select(i => (DocCardVm)list[i]).ToArray();
    Call(list, "FlushPendingLoads");
    try
    {
        if (!Wait(() => cards.All(c => !c.IsPlaceholder), "начало, середина и конец не загрузились", attempts: 1500)) return;
        Check(list.Count == 601, "общий порядок потерял документы одной из баз");
        Check(cards.Select(c => c.Doc.Title).SequenceEqual(new[] { "0001", "0301", "0601" }),
              "страницы двух баз нарушают общий порядок");
        Check(cards.All(c => c.IsSelected), "сортированная подкачка потеряла выделение");
    }
    finally { list.Cancel(); }
}

static int Subscriptions(Type type, string name, object target, object? owner = null) =>
    (type.GetField(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) as Delegate)?
    .GetInvocationList().Count(d => ReferenceEquals(d.Target, target)) ?? 0;

static int DelegateSubscriptions(Type type, string name, object target, IReadOnlySet<Delegate> handlers) =>
    (type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as Delegate)?
    .GetInvocationList().Count(handler => handlers.Contains(handler) || ReferenceEquals(handler.Target, target)
        || handler.Target is { } closure
        && System.Attribute.IsDefined(closure.GetType(), typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute))
        && closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => ReferenceEquals(field.GetValue(closure), target))) ?? 0;

void WriteBigBook(string path, int sheets, int rows, int cols)
{
    using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
    var book      = doc.AddWorkbookPart();
    book.Workbook = new X.Workbook();
    var entries   = new X.Sheets();

    static string Column(int c) => c < 26 ? ((char)('A' + c)).ToString()
                                          : $"{(char)('A' + c / 26 - 1)}{(char)('A' + c % 26)}";

    for (var s = 0; s < sheets; s++)
    {
        var part = book.AddNewPart<WorksheetPart>();
        using (var writer = OpenXmlWriter.Create(part))
        {
            writer.WriteStartElement(new X.Worksheet());
            writer.WriteStartElement(new X.SheetData());
            for (var r = 1; r <= rows; r++)
            {
                writer.WriteStartElement(new X.Row { RowIndex = (uint)r });
                for (var c = 0; c < cols; c++)
                    writer.WriteElement(new X.Cell
                    {
                        CellReference = $"{Column(c)}{r}",
                        DataType      = X.CellValues.String,
                        // «Задвижка» — раз в пятьсот строк в шестой колонке: по четыре на лист.
                        CellValue     = new X.CellValue(c == 5 && r % 500 == 0 ? $"Задвижка {r}" : $"Позиция {r}.{c}"),
                    });
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        entries.AppendChild(new X.Sheet { Id = book.GetIdOfPart(part), SheetId = (uint)(s + 1), Name = $"Лист {s + 1}" });
    }

    book.Workbook.AppendChild(entries);
    book.Workbook.Save();
}

void WriteBook(string path)
{
    using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
    var book      = doc.AddWorkbookPart();
    book.Workbook = new X.Workbook();

    // Оформление: жирная шапка на заливке — по нему в стенде видно, что цвета и начертания
    // доезжают от книги до листа.
    var styles = book.AddNewPart<WorkbookStylesPart>();
    styles.Stylesheet = new X.Stylesheet(
        new X.Fonts(new X.Font(new X.FontSize { Val = 11 }),
                    new X.Font(new X.Bold(), new X.FontSize { Val = 11 })),
        new X.Fills(new X.Fill(new X.PatternFill { PatternType = X.PatternValues.None }),
                    new X.Fill(new X.PatternFill { PatternType = X.PatternValues.Gray125 }),
                    new X.Fill(new X.PatternFill(new X.ForegroundColor { Rgb = "FFEFEFF4" })
                        { PatternType = X.PatternValues.Solid })),
        new X.Borders(new X.Border()),
        new X.CellStyleFormats(new X.CellFormat()),
        new X.CellFormats(new X.CellFormat(), new X.CellFormat { FontId = 1, FillId = 2 }));
    styles.Stylesheet.Save();

    var first = book.AddNewPart<WorksheetPart>();
    first.Worksheet = new X.Worksheet(
        new X.SheetData(
            new X.Row(new X.Cell { CellReference = "A1", DataType = X.CellValues.String, CellValue = new X.CellValue("Смета работ") }) { RowIndex = 1 },
            new X.Row(
                new X.Cell { CellReference = "A2", StyleIndex = 1, DataType = X.CellValues.String, CellValue = new X.CellValue("Наименование") },
                new X.Cell { CellReference = "B2", StyleIndex = 1, DataType = X.CellValues.String, CellValue = new X.CellValue("Количество") }) { RowIndex = 2 },
            // Третья строка выше обычной — так ее задали в книге.
            new X.Row(
                new X.Cell { CellReference = "A3", DataType = X.CellValues.String, CellValue = new X.CellValue("Труба стальная") },
                new X.Cell { CellReference = "B3", CellValue = new X.CellValue("12") })
                { RowIndex = 3, Height = 60, CustomHeight = true }),
        new X.MergeCells(new X.MergeCell { Reference = "A1:B1" }));
    first.Worksheet.Save();

    // Второе совпадение лежит далеко вправо и на ДРУГОМ листе: по нему видно, что переход к
    // совпадению и открывает нужный лист, и увозит таблицу вбок, а не только вниз.
    var wide = new X.Row { RowIndex = 1 };
    for (var i = 0; i < 13; i++)
        wide.AppendChild(new X.Cell
        {
            CellReference = $"{(char)('A' + i)}1",
            DataType      = X.CellValues.String,
            CellValue     = new X.CellValue($"Колонка {i + 1}"),
        });
    wide.AppendChild(new X.Cell
    {
        CellReference = "N1",
        DataType      = X.CellValues.String,
        CellValue     = new X.CellValue("Труба запасная"),
    });

    var second = book.AddNewPart<WorksheetPart>();
    second.Worksheet = new X.Worksheet(new X.SheetData(wide));
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

// То же, но с кадрами: страница просит свою картинку из отрисовки, а отрисовку безголовой
// платформе дает только явный такт.
bool WaitFrames(Func<bool> until, string complaint, int attempts = 300)
{
    for (var i = 0; i < attempts; i++)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Pump();
        if (until()) return true;
        Thread.Sleep(10);
    }
    Check(false, complaint);
    return false;
}

// Прокрутить несколько кадров без условия.
static void Frames(int count)
{
    for (var i = 0; i < count; i++)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Pump();
        Thread.Sleep(10);
    }
}

// Внутренности панели — поля, свойства и методы по имени: стенду нужно задать порядок событий
// руками, а снаружи этого не сделать.
static object? Get(object target, string name) =>
    target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);

static object? Prop(object target, string name) =>
    target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(target);

static void SetProp(object target, string name, object value) =>
    target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(target, value);

static object? Call(object target, string name, params object?[] args) =>
    target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
          .First(m => m.Name == name && m.GetParameters().Length == args.Length)
          .Invoke(target, args);

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
