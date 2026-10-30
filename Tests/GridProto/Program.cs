// Стенд этапов 0–6 плана drawn-grid.md: рисованная сетка (GridPanel/GridSurface) на настоящей
// базе. Меряет то, что план называет критериями готовности:
//   открытие без COUNT (первый кадр, кадр с данными, приход точного счетчика), теплые шаги
//   колесом вниз и вверх, шаги вбок и дочитка колонок, прыжок ползунком в конец, сортировка по
//   индексу и в фоне, отбор в фоне (UI-поток при этом), память сетки; кадры сохраняются в PNG.
//   В конце — счетчики GridTimings: сколько стоила каждая операция на UI-потоке и в фоне.
// Режимы этапа 2: --snap — кадры сетки при масштабе 1 и 2 против эталонных (папка --ref, по
// умолчанию --out; нет эталона — кадр сохраняется как эталон), попиксельное сравнение с разбивкой
// различий по строкам и колонкам, карты и вырезки в PNG (с этапа 3 — и сцены выделения);
// --gestures — безголовая мышь на копии базы: наведение, полоски ширины и высоты, ресайз окна,
// режимы, навигация нижней панели. Режим этапа 3: --keys — клавиши и мышь на настоящем TableView
// с рисованной сеткой (выделение, протягивание с автопрокруткой, меню, буфер, отмена, расположение
// текста: «Форматирование текста» в меню и Ctrl+L/E/R, выставленное последним видно во всей строке и
// столбце; контекстные меню: открыто одно, нажатие мимо и уход из программы закрывают). Режим этапа 4:
// --edit — правка на том же фасаде (набор с паузами, коммит и отмена, уход фокуса, редакторы по типам,
// правка набором на выделенной ячейке); с --pg — то же на базе PostgreSQL (стенд sb-pg, порт 5455).
// Режим этапа 5: --ops — операции на нем же (окно фильтра у кнопки заголовка, отбор и сортировка в
// фоне, память отбора, колонки и записи набором, скрытие записи, счет «№» из фона; OpsStand.cs).
// Платформа — Avalonia.Headless с НАСТОЯЩИМ шрифтом (Skia): цена раскладки текста честная,
// а кадр можно сохранить и сравнить с эталоном.
// Оговорка: Pump() выгребает все, что успело прийти, — «первый кадр» включает заполнения страниц.
//
//   dotnet run -c Release --project Tests/GridProto -- --db PATH [--collection NAME] [--steps 100] [--out DIR] [--ref DIR] [--probe] [--snap] [--gestures] [--keys] [--edit [--pg]] [--ops]
//   dotnet run -c Release --project Tests/GridProto -- --files [--out DIR] — ссылки файлов, без базы стенда

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Logging;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Views.ConstructorDb;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Views.ConstructorDb.DrawnGrid;
using SmartBase.Utils;

var path = Arg("--db") ?? "";
if (!args.Contains("--files") && !File.Exists(path)) { Console.WriteLine("Нужен --db PATH к файлу базы."); return 1; }
var steps  = int.TryParse(Arg("--steps"), out var s) ? Math.Max(1, s) : 100;
var outDir = Arg("--out") ?? Path.Combine(Path.GetTempPath(), "gridproto");
Directory.CreateDirectory(outDir);

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Порядок важен: UseSkia ДО UseHeadless (как в Tests/EditGeometry).
AppBuilder.Configure<SmartBase.App>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();
SmartBase.Utils.FontScale.Init();
GridTimings.Enabled = true;

if (args.Contains("--files")) return FileLinksChecks.Run(outDir);

using var mgr = DatabaseManager.Open(9401, "gridproto", path);
var repo = mgr.Constructor;
var all  = repo.GetAllCollections();
var col  = Arg("--collection") is { } name ? all.First(c => c.Name == name) : all.First();
var cid  = col.Id!.Value;
var swSnapExact = Stopwatch.StartNew();
var snap = repo.ReadOpenSnapshot(cid);
var tSnapExact = swSnapExact.Elapsed;
// Снимок без COUNT (этап 1): оценка числа записей, точный счет — в фоне после первого кадра.
var swSnapDefer  = Stopwatch.StartNew();
var snapDeferred = repo.ReadOpenSnapshot(cid, deferCount: true);
var tSnapDefer   = swSnapDefer.Elapsed;
var tag  = Path.GetFileNameWithoutExtension(path);

Console.WriteLine($"Стенд: {path}");
Console.WriteLine($"Коллекция «{col.Name}»: {snap.Fields.Count:N0} свойств, {snap.RecordCount:N0} записей");
Console.WriteLine($"Снимок открытия: с точным COUNT {Ms(tSnapExact)}, без COUNT {Ms(tSnapDefer)} (оценка {snapDeferred.RecordCount:N0})");
Console.WriteLine();

// Стенд снимков (этап 2): кадры сетки при масштабе 1 и 2 против эталонных, попиксельное
// сравнение. Отдельный режим — окна и ожидания свои.
if (args.Contains("--snap")) return RunSnapshots();
// Стенд жестов и навигации (этап 2): наведение, полоски-захваты ширины и высоты, ресайз окна,
// кнопки нижней панели — безголовой мышью на копии базы (режимы и ширины пишутся в нее).
if (args.Contains("--gestures")) return RunGestures();
// Стенд клавиш и мыши на фасаде (этап 3): выделение, буфер, отмена, меню — на копии базы.
if (args.Contains("--keys")) return RunKeys();
// Стенд правки (этап 4): набор с паузами, коммит и отмена, уход фокуса, редакторы по типам.
if (args.Contains("--edit")) return RunEdit();
// Стенд операций (этап 5): фильтр и сортировка из шапки, память отбора, операции с колонками и
// записями.
if (args.Contains("--ops")) return OpsStand.Run(path, outDir, col.Name);

var window = new Window { Width = 1600, Height = 900 };

var panel = new GridPanel();
window.Content = panel;
window.Show();
Settle(window);
var surface = panel.Surface;

// Пустое окно — базовая цена самого захвата кадра (копия 1600×900 в bitmap), чтобы вычесть ее.
Capture(window, out _);
var baseline = Median(Enumerable.Range(0, 10).Select(_ => { Capture(window, out var ms); return ms.TotalMilliseconds; }));

// ── Открытие ──────────────────────────────────────────────────────────────────
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var heapBefore = GC.GetTotalMemory(true);
var a0 = GC.GetTotalAllocatedBytes(true);
// Разовая цена шрифта в процессе: первая раскладка текста поднимает шрифт (в программе к моменту
// открытия таблицы он давно поднят другими экранами) — меряем отдельно, до открытия.
var swFont = Stopwatch.StartNew();
var fontFamily = Application.Current!.TryFindResource("FontFamilyBase", out var ffRes) && ffRes is Avalonia.Media.FontFamily ffam ? ffam : Avalonia.Media.FontFamily.Default;
_ = new Avalonia.Media.TextFormatting.TextLayout("Ag", new Avalonia.Media.Typeface(fontFamily), 12, Avalonia.Media.Brushes.Black).Height;
var tFont = swFont.Elapsed;

var swOpen = Stopwatch.StartNew();
panel.Load(repo, cid, snapDeferred);
var tLoad = swOpen.Elapsed;
// Первое обращение к странице заводит фоновую загрузку; ее синхронная часть до первого
// настоящего await идет на UI-потоке — меряем отдельно, до первого кадра.
_ = surface.Rows![0];
var tKick = swOpen.Elapsed;
panel.UpdateLayout();
var tLayout = swOpen.Elapsed;
Pump();
var tPump = swOpen.Elapsed;
var firstFrame = Capture(window, out var firstFrameMs);
var tFirst     = swOpen.Elapsed;
var openAlloc  = (GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024;

var gotData = WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
var dataFrame = Capture(window, out _);
var tData     = swOpen.Elapsed;
Save(dataFrame, Path.Combine(outDir, $"new-{tag}-open.png"));
var rowsAtOpen = panel.Rows!;
var gotCount   = WaitUntil(() => !rowsAtOpen.CountIsEstimate, TimeSpan.FromSeconds(60));
var tCount     = swOpen.Elapsed;

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var heapOpen = GC.GetTotalMemory(true);

Console.WriteLine($"{"Показатель",-46}{"значение",14}  примечание");
Console.WriteLine(new string('─', 100));
Row("Захват кадра (базовая цена стенда)", $"{baseline:N1} мс", "пустое окно, медиана 10");
Row("Шрифт: первая раскладка текста в процессе", Ms(tFont), "разовая цена, до открытия таблицы");
Row("Открытие: колонки + список строк", Ms(tLoad), "GridPanel.Load без кадра");
Row("Открытие: первое обращение к странице", Ms(tKick - tLoad), "синхронная часть запуска фоновой загрузки");
Row("Открытие: раскладка панели", Ms(tLayout - tKick), "первая раскладка полос прокрутки и поверхности");
Row("Открытие: диспетчер до кадра", Ms(tPump - tLayout), $"первое рисование {Ms(surface.LastRender)} внутри");
Row("Открытие: первый кадр", Ms(tFirst), $"колонки + список строк + кадр; аллокаций {openAlloc:N1} МБ; кадр {Ms(firstFrameMs)}");
Row("Открытие: кадр с данными", Ms(tData), gotData ? $"первая страница пришла из фона; страниц заполнено {rowsAtOpen.PagesFilled}, сбросов длины {rowsAtOpen.Resets}" : "ДАННЫЕ НЕ ПРИШЛИ за 20 с");
Row("Открытие: точный счетчик", Ms(tCount), gotCount ? $"полоса уточнена: {surface.RowCount - 1:N0} строк (оценка была {snapDeferred.RecordCount:N0}); страниц {rowsAtOpen.PagesFilled}, сбросов {rowsAtOpen.Resets}" : "СЧЕТЧИК НЕ ПРИШЕЛ за 60 с");
{
    // Цена пустого прохода диспетчера в безголовом стенде (тик рендера на UI-потоке) — чтобы
    // отличать ее от работы самой сетки в строках «UI-поток за вызов».
    var idle = new List<double>();
    for (var i = 0; i < 20; i++) { var p = Stopwatch.StartNew(); Pump(); idle.Add(p.Elapsed.TotalMilliseconds); Thread.Sleep(1); }
    Row("Пустой проход диспетчера", $"{Median(idle):N1} мс", $"медиана по 20, макс {idle.Max():N1} мс — цена стенда, не сетки");
}
Row("Ячеек в кадре", surface.LastCellsDrawn.ToString("N0"), $"колонок в раскладке {surface.Columns.Count:N0}, строк {surface.RowCount:N0}");
Row("Куча после открытия", $"{(heapOpen - heapBefore) / 1024.0 / 1024:N1} МБ", "прирост управляемой кучи от пустого окна");

// Повторное открытие той же коллекции: разовые цены процесса (шрифт, глифы, геометрии иконок,
// шаблоны полос) уже оплачены — так открывается коллекция в работающей программе.
{
    var swRe = Stopwatch.StartNew();
    panel.Load(repo, cid, snapDeferred);
    var tReLoad = swRe.Elapsed;
    Pump();
    Capture(window, out _);
    var tReFrame = swRe.Elapsed;
    var reData = WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Row("Повторное открытие: первый кадр", Ms(tReFrame), $"Load {Ms(tReLoad)}, рисование {Ms(surface.LastRender)}");
    Row("Повторное открытие: кадр с данными", Ms(swRe.Elapsed), reData ? "страница пришла" : "ДАННЫЕ НЕ ПРИШЛИ");
}

// ── Прогрев: прокрутить вниз на steps шагов, дождаться страниц ────────────────
var rowH = surface.RowPitch;
var coldSteps = new List<double>();
for (var i = 0; i < steps; i++)
{
    var sw = Stopwatch.StartNew();
    surface.OffsetY += rowH * 3;
    Pump();
    Capture(window, out _);
    coldSteps.Add(sw.Elapsed.TotalMilliseconds);
}
var warmed = WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
// Обратно наверх — теперь все страницы диапазона в кеше.
surface.OffsetY = 0;
Pump();
WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
Row("Холодные шаги вниз (страницы грузятся)", $"{Median(coldSteps):N1} мс", $"медиана по {steps}, макс {coldSteps.Max():N1} мс{(warmed ? "" : "; страницы НЕ дошли")}");

// ── Теплые шаги вниз и вверх ──────────────────────────────────────────────────
var down = MeasureSteps(() => surface.OffsetY += rowH * 3);
var up   = MeasureSteps(() => surface.OffsetY -= rowH * 3);
var px   = MeasureSteps(() => surface.OffsetY += 1);
GridSurface.DebugSkipText = true;
var noText = MeasureSteps(() => surface.OffsetY += rowH * 3);
GridSurface.DebugSkipText = false;
surface.OffsetY = 0; Pump();
Row("Шаг вниз БЕЗ ТЕКСТА (Render поверхности)", $"{noText.Render:N2} мс", "линии, заливки, галочки и иконки без раскладок текста — цена текста видна по разнице");
Row("Шаг вниз на 1 пиксель (кадр)", $"{px.Frame:N1} мс", $"те же строки, только сдвиг; Render {px.Render:N2}; диспетчер {px.Pump:N1}; захват {px.Capture:N1}");
Row("Шаг вниз (кадр)", $"{down.Frame:N1} мс", $"медиана по {steps}; p95 {down.P95:N1}; макс {down.Max:N1}; за вычетом захвата {Math.Max(0, down.Frame - baseline):N1}");
Row("Шаг вниз (Render поверхности)", $"{down.Render:N2} мс", $"только рисование, медиана; диспетчер {down.Pump:N1} мс, захват {down.Capture:N1} мс");
Row("Шаг вверх (кадр)", $"{up.Frame:N1} мс", $"p95 {up.P95:N1}; макс {up.Max:N1}");
Row("Шаг вверх (Render поверхности)", $"{up.Render:N2} мс", $"медиана; диспетчер {up.Pump:N1} мс, захват {up.Capture:N1} мс");

// ── Шаги вбок ─────────────────────────────────────────────────────────────────
var side = MeasureSteps(() => surface.OffsetX += 160);
var sideFrame = Capture(window, out _);
Save(sideFrame, Path.Combine(outDir, $"new-{tag}-side.png"));
Row("Шаг вбок (кадр)", $"{side.Frame:N1} мс", $"медиана по {steps}; p95 {side.P95:N1}; макс {side.Max:N1}; прокрутка дошла до {surface.OffsetX:N0} из {Math.Max(0, surface.ExtentWidth - surface.ViewportWidth):N0}");
Row("Шаг вбок (Render поверхности)", $"{side.Render:N2} мс", $"медиана; диспетчер {side.Pump:N1} мс, захват {side.Capture:N1} мс");
// Прыжок вбок к последним колонкам: их значений у страницы нет (окно колонок) — дочитка в фоне.
{
    var swSide = Stopwatch.StartNew();
    surface.OffsetX = double.MaxValue;
    Pump();
    Capture(window, out _);
    var tSideFrame = swSide.Elapsed;
    var gotSide = WaitUntil(surface.VisibleCellsLoaded, TimeSpan.FromSeconds(20));
    Row("Прыжок вбок: кадр", Ms(tSideFrame), $"колонки с {surface.Columns.VisibleRange(surface.OffsetX, surface.ViewportWidth).First + 1:N0}; окно полей у списка {panel.Rows!.FieldWindow?.Count.ToString("N0") ?? "все"}");
    Row("Прыжок вбок: дочитка колонок", Ms(swSide.Elapsed), gotSide ? "значения дальних колонок пришли" : "ЗНАЧЕНИЯ НЕ ПРИШЛИ за 20 с");
}
surface.OffsetX = 0;

// ── Прыжок в конец ────────────────────────────────────────────────────────────
var swJump = Stopwatch.StartNew();
surface.OffsetY = double.MaxValue;
Pump();
Capture(window, out _);
var tJumpFrame = swJump.Elapsed;
var jumped     = WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(30));
var tJumpData  = swJump.Elapsed;
var endFrame   = Capture(window, out _);
Save(endFrame, Path.Combine(outDir, $"new-{tag}-end.png"));
Row("Прыжок в конец: кадр с заглушками", Ms(tJumpFrame), $"строка {surface.VisibleRows().First + 1:N0} из {surface.RowCount - 1:N0}");
Row("Прыжок в конец: данные", Ms(tJumpData), jumped ? "страница глубины пришла" : "ДАННЫЕ НЕ ПРИШЛИ за 30 с");

// ── Этап 1: сортировка и отбор в фоне ─────────────────────────────────────────
// Источник строк считает планировщик в фоне; UI-поток при этом занят только своими кадрами —
// меряем самый долгий вызов диспетчера за время расчета. После подключения: первый кадр, кадр
// с данными и прыжок в конец в этом же виде.
Console.WriteLine();
Console.WriteLine("Этап 1: сортировка и отбор");
{
    var textField = snap.Fields.FirstOrDefault(f => f.FieldType == "text")
                    ?? snap.Fields.FirstOrDefault(f => SmartBase.Core.Models.ColumnSort.IsIndexSortable(f.FieldType));
    var dateField = snap.Fields.FirstOrDefault(f => f.FieldType == "date");
    var numField  = snap.Fields.FirstOrDefault(f => f.FieldType == "number");
    var selField  = snap.Fields.FirstOrDefault(f => f.FieldType is "select" or "status" or "checkbox")
                    ?? dateField;

    if (textField is not null) MeasureQuery($"Сортировка по индексу «{textField.Name}» (Я→А)", new(), textField.Id!.Value.ToString(), false, "sort-text");
    if (dateField is not null) MeasureQuery($"Сортировка по индексу «{dateField.Name}» (А→Я)", new(), dateField.Id!.Value.ToString(), true, "sort-date");
    if (numField  is not null) MeasureQuery($"Сортировка числа в фоне «{numField.Name}»", new(), numField.Id!.Value.ToString(), true, "sort-number");
    if (selField is not null)
    {
        // Самое частое непустое значение колонки — отдельной функцией, чтобы список значений
        // (миллионы на стенде 10 млн) не пережил замер и не попал в «кучу после прогона».
        var pick = MostFrequentValue(selField.Id!.Value);
        if (pick.Value is { Length: > 0 })
            MeasureQuery($"Фильтр «{selField.Name}» = «{pick.Value}» ({pick.Count:N0} записей)",
                         new() { [selField.Id!.Value.ToString()] = [pick.Value] }, null, true, "filter");
    }
    // Обратно к порядку таблицы — мгновенно, без фона.
    var swBack = Stopwatch.StartNew();
    panel.SetQuery(new Dictionary<string, HashSet<string>>(), null, true);
    Pump();
    Row("Сброс отбора и сортировки", Ms(swBack.Elapsed), $"порядок таблицы, {surface.RowCount - 1:N0} строк");
}

(string Value, int Count) MostFrequentValue(int fieldId)
{
    var before = GC.GetTotalMemory(true);
    var pick = repo.GetFieldValueCounts(cid, fieldId).Where(c => c.Value.Length > 0).OrderByDescending(c => c.Count).FirstOrDefault();
    var after = GC.GetTotalMemory(true);
    Row("Список значений колонки для выбора фильтра", $"{(after - before) / 1024.0 / 1024:N1} МБ", "прирост кучи после сбора — цена стенда, не сетки");
    return pick;
}

void MeasureQuery(string label, Dictionary<string, HashSet<string>> filters, string? sortKey, bool asc, string file)
{
    var sw   = Stopwatch.StartNew();
    var pause0 = GC.GetTotalPauseDuration();
    var traceBefore = GridTimings.Trace().Count;
    var task = panel.SetQuery(filters, sortKey, asc);
    double maxPump = 0;
    var pumps = 0;
    while (!task.IsCompleted)
    {
        var p = Stopwatch.StartNew();
        Pump();
        maxPump = Math.Max(maxPump, p.Elapsed.TotalMilliseconds);
        pumps++;
        Thread.Sleep(1);
    }
    Pump();
    var tSource = sw.Elapsed;
    var gcPause = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
    var slowJobs = string.Join("; ", GridTimings.Trace().Skip(traceBefore)
        .Where(t => t.Name.EndsWith("(UI)") && t.Ms >= 5).Select(t => $"{t.Name} {t.Ms:N0} мс"));
    Capture(window, out _);
    var tFrame = sw.Elapsed;
    var got    = WaitUntil(surface.VisibleCellsLoaded, TimeSpan.FromSeconds(120));
    var tData  = sw.Elapsed;
    Save(Capture(window, out _), Path.Combine(outDir, $"new-{tag}-{file}.png"));

    var swJ = Stopwatch.StartNew();
    surface.OffsetY = double.MaxValue;
    Pump();
    var gotEnd = WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(120));
    var tEnd   = swJ.Elapsed;
    var source = panel.Rows!.Source switch
    {
        SortedRowSource s => $"индекс, голова {panel.Rows.HeadCount?.ToString("N0") ?? "?"}",
        IdListRowSource l => $"список id ({l.Ids.Count:N0})",
        _                 => "порядок таблицы",
    };
    Row(label, Ms(tSource), $"источник ({source}); кадр {Ms(tFrame)}; данные {(got ? Ms(tData) : "НЕ ПРИШЛИ")}; " +
                            $"UI-поток за вызов ≤ {maxPump:N1} мс ({pumps} вызовов, паузы GC {gcPause:N0} мс{(slowJobs.Length > 0 ? ", долгие: " + slowJobs : "")}); " +
                            $"прыжок в конец {(gotEnd ? Ms(tEnd) : "НЕ ПРИШЕЛ")}");
    surface.OffsetY = 0;
    Pump();
}


// ── Проба: откуда лишние миллисекунды шага вниз ──────────────────────────────
// Та же поверхность без полос прокрутки в отдельном окне и три способа получить кадр:
// только диспетчер, диспетчер + тик рендера без копии кадра, диспетчер + захват.
if (args.Contains("--probe"))
{
    var bareWin = new Window { Width = 1600, Height = 900 };
    var bare    = new GridSurface();
    bareWin.Content = bare;
    bareWin.Show();
    Settle(bareWin);
    bare.Attach(surface.Rows!, surface.Columns, null, null, null);
    Pump();
    bareWin.CaptureRenderedFrame();
    WaitUntil(bare.VisibleRowsLoaded, TimeSpan.FromSeconds(20));

    Console.WriteLine();
    Console.WriteLine("Проба шага вниз на 3 строки (медианы по 60):");
    surface.OffsetY = 0; Pump(); WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Probe("панель: вниз, тик",                       () => surface.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), window);
    Probe("панель: вбок, тик",                       () => surface.OffsetX += 160, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), window);
    GridPanel.DebugSkipBars = true;
    Probe("панель БЕЗ ПОЛОС: вниз, тик",             () => surface.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), window);
    Probe("панель БЕЗ ПОЛОС: вбок, тик",             () => surface.OffsetX += 160, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), window);
    GridPanel.DebugSkipBars = false;
    GridPanel.DebugBarDivisor = 100; surface.OffsetY += 1; surface.OffsetY -= 1;
    Probe("панель, полоса в сотнях пикселей: вниз",   () => surface.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), window);
    GridPanel.DebugBarDivisor = 1;
    surface.OffsetY = 0; Pump(); WaitUntil(surface.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Probe("панель: вниз ПОВТОРНО (полоса в пикселях)", () => surface.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), window);
    Probe("панель с полосами: диспетчер",            () => surface.OffsetY += rowH * 3, () => { }, window);
    Probe("панель с полосами: диспетчер + тик",      () => surface.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), window);
    Probe("панель с полосами: диспетчер + захват",   () => surface.OffsetY += rowH * 3, () => window.CaptureRenderedFrame(), window);
    Probe("голая поверхность: диспетчер",            () => bare.OffsetY += rowH * 3, () => { }, bareWin);
    Probe("голая поверхность: диспетчер + тик",      () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    Probe("голая поверхность: диспетчер + захват",   () => bare.OffsetY += rowH * 3, () => bareWin.CaptureRenderedFrame(), bareWin);
    Probe("голая поверхность вбок: диспетчер + тик", () => bare.OffsetX += 160, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    Probe("ничего не меняем: диспетчер + тик",       () => { }, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    Probe("голая: вниз на 1 пиксель, тик",          () => bare.OffsetY += 1, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    GridSurface.DebugSkipText = true;
    Probe("голая: вниз БЕЗ ТЕКСТА, тик",             () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    Probe("голая: вбок БЕЗ ТЕКСТА, тик",             () => bare.OffsetX += 160, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    GridSurface.DebugSkipText = false;
    GridSurface.DebugSkipCells = true;
    Probe("голая: вниз БЕЗ ЯЧЕЕК, тик",              () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    GridSurface.DebugSkipCells = false;
    bare.OffsetY = 0; Pump(); WaitUntil(bare.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Probe("голая: вниз ПОВТОРНО по тем же строкам",  () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    // Строки, которых голая поверхность еще не рисовала (глубже прогретого окна стенда):
    // сначала без текста, потом тот же диапазон с текстом впервые, потом повторно.
    var fresh = rowH * 600;
    bare.OffsetY = fresh; Pump(); WaitUntil(bare.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    var m0 = bare.TextCacheMisses;
    GridSurface.DebugSkipText = true;
    Probe("голая: НОВЫЕ строки без текста",            () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    GridSurface.DebugSkipText = false;
    bare.OffsetY = fresh; Pump();
    Probe("голая: те же НОВЫЕ строки, текст впервые",  () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    var m1 = bare.TextCacheMisses;
    bare.OffsetY = fresh; Pump();
    Probe("голая: те же строки повторно",              () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    Console.WriteLine($"  новых раскладок текста за проход «впервые»: {m1 - m0:N0}, за проход «повторно»: {bare.TextCacheMisses - m1:N0}");
    // Гипотеза: ресурсы нарисованного текста отпускаются, когда его долго не рисуют, и при
    // возврате создаются заново. Прогреваем строки 0..180, уводим поверхность на другие строки
    // на 60 шагов, возвращаемся; затем то же после простоя в 3 с без рисования.
    bare.OffsetY = 0; Pump(); WaitUntil(bare.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Probe("голая: 0..180 прогрев",                       () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    bare.OffsetY = rowH * 2000; Pump(); WaitUntil(bare.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Probe("голая: другие строки 2000.. (60 шагов)",       () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    bare.OffsetY = 0; Pump();
    Probe("голая: возврат к 0..180 после других строк",   () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
    bare.OffsetY = 0; Pump();
    var idle = Stopwatch.StartNew();
    while (idle.Elapsed < TimeSpan.FromSeconds(3)) { Pump(); Thread.Sleep(20); }
    Probe("голая: 0..180 после простоя 3 с",               () => bare.OffsetY += rowH * 3, () => AvaloniaHeadlessPlatform.ForceRenderTimerTick(), bareWin);
}

void Probe(string label, Action step, Action present, Window w)
{
    var pumps = new List<double>(steps);
    var shows = new List<double>(steps);
    var gcs   = new List<int>(steps);
    var pause0 = GC.GetTotalPauseDuration();
    var g1 = GC.CollectionCount(1);
    var g2 = GC.CollectionCount(2);
    var alloc0 = GC.GetTotalAllocatedBytes(true);
    for (var i = 0; i < steps; i++)
    {
        var g0 = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();
        step();
        Pump();
        var t0 = sw.Elapsed.TotalMilliseconds;
        present();
        pumps.Add(t0);
        shows.Add(sw.Elapsed.TotalMilliseconds - t0);
        gcs.Add(GC.CollectionCount(0) - g0);
    }
    var pauseMs = (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds;
    Console.WriteLine($"  {label,-46} диспетчер {Median(pumps),6:N1} мс   показ {Median(shows),6:N1} мс   Render {(w.Content is GridSurface gs ? gs : surface).LastRender.TotalMilliseconds,5:N2} мс   GC {gcs.Sum(),3} (gen1 {GC.CollectionCount(1) - g1}, gen2 {GC.CollectionCount(2) - g2}), паузы {pauseMs,5:N0} мс, выделено {(GC.GetTotalAllocatedBytes(true) - alloc0) / 1024.0 / 1024,6:N1} МБ");
}

// ── Память ────────────────────────────────────────────────────────────────────
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var heapEnd = GC.GetTotalMemory(true);
Row("Куча после прогона", $"{(heapEnd - heapBefore) / 1024.0 / 1024:N1} МБ", $"кеш раскладок текста: {surface.TextCacheCount:N0} записей, попаданий {surface.TextCacheHits:N0}, промахов {surface.TextCacheMisses:N0}");
Row("Рабочий набор процесса", $"{Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024:N0} МБ", "включая базу и шрифты");

// ── Счетчики UI-потока за весь прогон ────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("Операции сетки за прогон (сумма и максимум, мс):");
foreach (var (opName, count, total, max, note) in GridTimings.Report())
    Console.WriteLine($"  {opName,-34} {count,6} × {total / Math.Max(1, count),7:N2} = {total,9:N1}   макс {max,8:N2}  {note}");
Console.WriteLine();
Console.WriteLine("Страницы и дочитки по порядку (фон и UI):");
foreach (var (opName, ms, note) in GridTimings.Trace().Where(t => t.Name.StartsWith("страница") || t.Name.StartsWith("дочитка") || t.Name.StartsWith("заполнение") || t.Name.StartsWith("смена")))
    Console.WriteLine($"  {opName,-34} {ms,8:N2}  {note}");
Console.WriteLine();
Console.WriteLine($"Кадры: {outDir}");
return 0;

// ── Вспомогательное ───────────────────────────────────────────────────────────

(double Frame, double Render, double P95, double Max, double Pump, double Capture) MeasureSteps(Action step)
{
    var frames   = new List<double>(steps);
    var renders  = new List<double>(steps);
    var pumps    = new List<double>(steps);
    var captures = new List<double>(steps);
    for (var i = 0; i < steps; i++)
    {
        var sw = Stopwatch.StartNew();
        step();
        var t0 = sw.Elapsed.TotalMilliseconds;
        Pump();
        var t1 = sw.Elapsed.TotalMilliseconds;
        Capture(window, out _);
        frames.Add(sw.Elapsed.TotalMilliseconds);
        renders.Add(surface.LastRender.TotalMilliseconds);
        pumps.Add(t1 - t0);
        captures.Add(sw.Elapsed.TotalMilliseconds - t1);
    }
    frames.Sort();
    return (Median(frames), Median(renders), frames[(int)(frames.Count * 0.95) is var p && p < frames.Count ? p : frames.Count - 1], frames[^1], Median(pumps), Median(captures));
}

void Row(string label, string value, string note) =>
    Console.WriteLine($"{label,-46}{value,14}  {note}");

static string Ms(TimeSpan t) => $"{t.TotalMilliseconds:N1} мс";

static double Median(IEnumerable<double> values)
{
    var list = values.OrderBy(v => v).ToList();
    if (list.Count == 0) return 0;
    var mid = list.Count / 2;
    return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
}


// ── Стенд жестов и навигации ─────────────────────────────────────────────────
//
// Безголовая мышь (Avalonia.Headless) на копии базы: наведение на ячейку, заголовок и кнопку
// фильтра; ручная ширина — рост уводит соседей и включает горизонтальную прокрутку, сужение
// раздает место справа, пол 48 и пол «таблица ровно заполнена» у последней; ресайз окна не
// трогает модель; выключение режима стирает ширины. Ручная высота — строка растет, строки ниже
// сдвигаются, индикатор считает по настоящим высотам, высота переживает перезагрузку строк.
// Навигация нижней панели: индикатор, экран вперед и назад, края, восстановление смещения,
// прокрутка к записи. Проверки — те же пункты, что в чеклистах table-columns (1–5) и
// table-navigation (1–4, 6), только без глаз.
int RunGestures()
{
    var tmp = Path.Combine(outDir, "gestures-" + Path.GetFileName(path));
    File.Copy(path, tmp, overwrite: true);
    using var mgr2 = DatabaseManager.Open(9402, "gridgestures", tmp);
    var repo2 = mgr2.Constructor;
    var cid2  = repo2.GetAllCollections().First(c => c.Name == col.Name).Id!.Value;

    var failed = 0;
    void Expect(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {what}");
        if (!ok) failed++;
    }
    static bool Near(double a, double b, double eps = 0.51) => Math.Abs(a - b) <= eps;

    // «Средняя» коллекция чеклиста: таблица помещается в окно — прячем в «Отображении» все
    // поля, кроме первых пяти (сужение колонок раздает место соседям только когда есть куда).
    var snap0 = repo2.ReadOpenSnapshot(cid2);
    var keep  = snap0.Fields.Take(5).Select(f => f.Id!.Value.ToString()).ToHashSet();
    repo2.SaveColumnDisplay(cid2, snap0.Fields.Select(f => (f.Id!.Value.ToString(), keep.Contains(f.Id!.Value.ToString()), 1)));

    var w = new Window { Width = 1600, Height = 900 };
    var p = new GridPanel();
    w.Content = p;
    w.Show();
    Settle(w);
    p.Load(repo2, cid2, repo2.ReadOpenSnapshot(cid2));
    Pump();
    WaitUntil(p.Surface.VisibleCellsLoaded, TimeSpan.FromSeconds(20));
    var s = p.Surface;
    var rows = p.Rows!;

    Console.WriteLine("Стандартная ширина:");
    var L0 = s.Columns;
    var movable = Enumerable.Range(0, L0.Count).Where(i => L0[i].IsStarWidth).ToList();
    // Поровну с точностью до пикселя (остаток деления — первым колонкам, как снап нынешней), и
    // вместе ровно во всю ширину окна — без полосы справа от «+».
    Expect(movable.Max(i => L0.Width(i)) - movable.Min(i => L0.Width(i)) <= 1, $"движимые колонки одной ширины ({L0.Width(movable[0]):N0})");
    Expect(Near(L0.TotalWidth, s.ViewportWidth), $"вместе во всю ширину окна: {L0.TotalWidth:N0} = {s.ViewportWidth:N0}");
    Expect(L0.Width(movable[0]) >= SmartBase.Utils.ConstructorSettings.ColWidth, "не уже стандартной");
    Expect(s.HitTest(new Point(L0.Right(movable[1]) - s.OffsetX, 10)).Kind == GridHitKind.Header, "у границы заголовка захвата нет");

    Console.WriteLine("Наведение:");
    var c2 = movable[1];
    var hx = L0.Left(c2) + 40;
    w.MouseMove(new Point(hx, 10)); Pump();
    Expect(s.HitTest(new Point(hx, 10)) is { Kind: GridHitKind.Header } && ToolTip.GetTip(s) is null, "заголовок: подсказки нет");
    var chevron = new Point(L0.Right(c2) - s.LineWidth - GridSurface.CellPad - 10, s.HeaderHeight / 2);
    w.MouseMove(chevron); Pump();
    Expect(s.HitTest(chevron).Kind == GridHitKind.HeaderFilter, "кнопка фильтра под курсором");
    Expect(Equals(ToolTip.GetTip(s), "Фильтр и сортировка"), "подсказка «Фильтр и сортировка»");
    var cell = new Point(L0.Left(c2) + 20, s.HeaderHeight + s.RowPitch * 2 + 10);
    w.MouseMove(cell); Pump();
    var hit = s.HitTest(cell);
    Expect(hit.Kind == GridHitKind.Cell && hit.Row == 2 && hit.Column == c2 && ToolTip.GetTip(s) is null, "ячейка (строка 3): подсказки нет");
    var plus = new Point(L0.Left(L0.Count - 1) + 10, 10);
    Expect(s.HitTest(plus).Kind == GridHitKind.HeaderAdd, "«+» шапки");
    var addRowY = s.HeaderHeight + s.RowLayout.Top(rows.Count - 1) - s.OffsetY;
    s.OffsetY = double.MaxValue; Pump();
    Expect(s.HitTest(new Point(cell.X, s.HeaderHeight + s.RowLayout.Top(rows.Count - 1) - s.OffsetY + 5)).Kind == GridHitKind.AddRow, "строка «+» в конце");
    s.OffsetY = 0; Pump();

    Console.WriteLine("Ручная ширина: увеличение");
    p.SetColResizeMode(true); Pump();
    var L1 = s.Columns;
    Expect(Enumerable.Range(0, L1.Count).All(i => Near(L1.Width(i), L0.Width(i))), "включение режима вид не изменило");
    var gripX = L1.Right(c2) - s.OffsetX;
    Expect(s.HitTest(new Point(gripX, 10)).Kind == GridHitKind.ColumnGrip, "у правой границы — захват");
    Expect(s.HitTest(new Point(gripX - 6, 10)).Kind != GridHitKind.ColumnGrip, "в 6 px от границы захвата уже нет");
    w.MouseMove(new Point(gripX, 10)); Pump();
    Expect(s.Cursor is not null, "курсор «↔» у границы");
    var before = Enumerable.Range(0, L1.Count).Select(i => L1.Width(i)).ToArray();
    w.MouseDown(new Point(gripX, 10), MouseButton.Left); Pump();
    w.MouseMove(new Point(gripX + 50, 12)); Pump();
    w.MouseMove(new Point(gripX + 100, 12)); Pump();
    var mid = s.Columns;
    Expect(Near(mid.Width(c2), before[c2] + 100), $"во время жеста ширина {mid.Width(c2):N0} = {before[c2]:N0} + 100");
    w.MouseUp(new Point(gripX + 100, 12), MouseButton.Left); Pump();
    var L2 = s.Columns;
    Expect(Near(L2.Width(c2), before[c2] + 100), "после отпускания ширина осталась");
    Expect(Enumerable.Range(0, L2.Count).Where(i => i != c2).All(i => Near(L2.Width(i), before[i])), "соседи справа и слева не изменились");
    Expect(Near(L2.Right(L2.Count - 1), L0.TotalWidth + 100), "«+» уехала за край на 100");
    Expect(s.ExtentWidth > s.ViewportWidth + 1, "горизонтальная прокрутка появилась");
    var saved = repo2.GetColumnWidths(cid2);
    Expect(saved.TryGetValue(L2[c2].Key, out var savedW) && Near(savedW, before[c2] + 100), "ширина сохранена в базе одним upsert'ом");
    Expect(saved.Count == movable.Count, $"в базе ширины всех движимых колонок ({saved.Count})");

    Console.WriteLine("Ручная ширина: уменьшение");
    gripX = L2.Right(c2) - s.OffsetX;
    w.MouseDown(new Point(gripX, 10), MouseButton.Left); Pump();
    w.MouseMove(new Point(gripX - 150, 12)); Pump();
    w.MouseUp(new Point(gripX - 150, 12), MouseButton.Left); Pump();
    var L3 = s.Columns;
    Expect(Near(L3.Width(c2), before[c2] - 50), $"ширина {L3.Width(c2):N0} = исходная − 50");
    Expect(Enumerable.Range(0, c2).All(i => Near(L3.Width(i), before[i])), "колонки слева не изменились");
    Expect(Enumerable.Range(c2 + 1, L3.Count - c2 - 2).All(i => L3.Width(i) > before[i]), "колонки справа расширились");
    Expect(Near(L3.TotalWidth, s.ViewportWidth, 1), $"пустоты справа нет: {L3.TotalWidth:N0} = окно {s.ViewportWidth:N0}");
    Expect(s.ExtentWidth <= s.ViewportWidth + 0.5, "горизонтальная прокрутка пропала");

    gripX = L3.Right(c2) - s.OffsetX;
    w.MouseDown(new Point(gripX, 10), MouseButton.Left); Pump();
    w.MouseMove(new Point(gripX - 1000, 12)); Pump();
    w.MouseUp(new Point(gripX - 1000, 12), MouseButton.Left); Pump();
    Expect(Near(s.Columns.Width(c2), ColumnLayout.MinManualWidth), $"уменьшение до упора: {s.Columns.Width(c2):N0} = 48");

    var last = movable[^1];
    var L4 = s.Columns;
    gripX = L4.Right(last) - s.OffsetX;
    var lastBefore = L4.Width(last);
    w.MouseDown(new Point(gripX, 10), MouseButton.Left); Pump();
    w.MouseMove(new Point(gripX - 300, 12)); Pump();
    w.MouseUp(new Point(gripX - 300, 12), MouseButton.Left); Pump();
    Expect(Near(s.Columns.Width(last), lastBefore), "последняя движимая влево не идет: таблица ровно заполнена");
    gripX = s.Columns.Right(last) - s.OffsetX;
    w.MouseDown(new Point(gripX, 10), MouseButton.Left); Pump();
    w.MouseMove(new Point(gripX + 80, 12)); Pump();
    w.MouseUp(new Point(gripX + 80, 12), MouseButton.Left); Pump();
    Expect(Near(s.Columns.Width(last), lastBefore + 80), "последнюю движимую вправо тянуть можно");

    Console.WriteLine("Ресайз окна:");
    var model = repo2.GetColumnWidths(cid2);
    w.Width = 1100; Settle(w); Pump();
    var narrow = s.Columns;
    Expect(Enumerable.Range(0, narrow.Count).Where(i => narrow[i].IsStarWidth).All(i => narrow.Width(i) >= model[narrow[i].Key] - 0.5), "сужение окна: колонки не уже заданных");
    Expect(s.ExtentWidth > s.ViewportWidth, "при узком окне — горизонтальная прокрутка");
    w.Width = 1600; Settle(w); Pump();
    var wide = s.Columns;
    Expect(Enumerable.Range(0, wide.Count).All(i => Near(wide.Width(i), s.Columns.Width(i))) && repo2.GetColumnWidths(cid2).SequenceEqual(model), "обратно: та же картинка, модель не тронута");

    p.SetColResizeMode(false); Pump();
    var L5 = s.Columns;
    Expect(Enumerable.Range(0, L5.Count).All(i => Near(L5.Width(i), L0.Width(i))) && repo2.GetColumnWidths(cid2).Count == 0, "стандартная ширина: вид как до жестов, ширины стерты");
    Expect(!repo2.GetUiSettings(cid2).ColResize, "режим сохранен в базе");

    Console.WriteLine("Ручная высота строк:");
    p.SetRowResizeMode(true); Pump();
    Expect(repo2.GetUiSettings(cid2).RowResize, "режим высоты сохранен");
    var pitch0  = s.RowPitch;
    var range0  = p.VisibleRowRange();
    var rowIdx  = 2;
    var gripY   = s.HeaderHeight + s.RowLayout.Bottom(rowIdx) - s.OffsetY;
    Expect(s.HitTest(new Point(200, gripY)).Kind == GridHitKind.RowGrip, "у нижней границы строки — захват");
    w.MouseMove(new Point(200, gripY)); Pump();
    w.MouseDown(new Point(200, gripY), MouseButton.Left); Pump();
    w.MouseMove(new Point(200, gripY + 40)); Pump();
    Expect(Near(s.RowLayout.Height(rowIdx), pitch0 + 40), $"во время жеста шаг строки {s.RowLayout.Height(rowIdx):N0} = {pitch0:N0} + 40");
    w.MouseUp(new Point(200, gripY + 40), MouseButton.Left); Pump();
    Expect(Near(s.RowLayout.Height(rowIdx), pitch0 + 40), "высота осталась после отпускания");
    Expect(Near(s.RowLayout.Top(rowIdx + 1), pitch0 * (rowIdx + 1) + 40), "строки ниже сдвинулись на 40");
    Expect(Near(s.RowLayout.Top(rowIdx), pitch0 * rowIdx), "строки выше на месте");
    var range1 = p.VisibleRowRange();
    Expect(range1.Last == range0.Last - 1, $"индикатор: было {range0.Label}, стало {range1.Label}");
    var rid = rows[rowIdx].RecordId;
    var heights = repo2.GetRowHeights(cid2);
    Expect(heights.TryGetValue(rid, out var hSaved) && hSaved == (int)Math.Round(s.RowHeight + 40), $"высота записи сохранена: {hSaved}");
    w.MouseDown(new Point(200, s.HeaderHeight + s.RowLayout.Bottom(rowIdx) - s.OffsetY), MouseButton.Left); Pump();
    w.MouseMove(new Point(200, s.HeaderHeight + s.RowLayout.Bottom(rowIdx) - s.OffsetY - 500)); Pump();
    w.MouseUp(new Point(200, 0), MouseButton.Left); Pump();
    Expect(Near(s.RowLayout.Height(rowIdx), pitch0), "ниже стандартной высоты строка не опускается");
    w.MouseDown(new Point(200, s.HeaderHeight + s.RowLayout.Bottom(rowIdx) - s.OffsetY), MouseButton.Left); Pump();
    w.MouseMove(new Point(200, s.HeaderHeight + s.RowLayout.Bottom(rowIdx) - s.OffsetY + 64)); Pump();
    w.MouseUp(new Point(200, 0), MouseButton.Left); Pump();
    Expect(Near(s.RowLayout.Height(rowIdx), pitch0 + 64), "снова растянута на 64");

    p.ReloadRows(); Pump();
    WaitUntil(() => p.QueryTask.IsCompleted && p.RanksTask.IsCompleted, TimeSpan.FromSeconds(20)); Pump();
    Expect(Near(s.RowLayout.Height(rowIdx), pitch0 + 64), "после перезагрузки строк высота на месте (индекс найден в фоне)");
    s.OffsetY = double.MaxValue; Pump();
    WaitUntil(s.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    s.OffsetY = 0; Pump();
    Expect(Near(s.RowLayout.Height(rowIdx), pitch0 + 64), "после прыжка в конец и обратно — та же высота");
    p.SetRowResizeMode(false); Pump();
    Expect(!s.RowLayout.HasManual && repo2.GetRowHeights(cid2).Count == 0, "стандартная высота: высоты стерты");

    Console.WriteLine("Навигация нижней панели:");
    var total = rows.Count - 1;
    var r0 = p.VisibleRowRange();
    Expect(r0.First == 1 && r0.Total == total && r0.AtStart && !r0.AtEnd, $"вверху: {r0.Label}");
    Expect(r0.Last == (int)Math.Ceiling(s.ViewportHeight / s.RowPitch), $"последняя видимая по высоте окна: {r0.Last}");
    p.ScrollRowsPageForward(); Pump();
    var r1 = p.VisibleRowRange();
    Expect(r1.First == r0.Last || r1.First == r0.Last + 1, $"«›»: {r1.Label} (первая — следующая за последней)");
    p.ScrollRowsPageBack(); Pump();
    Expect(p.VisibleRowRange() == r0, "«‹» вернул исходный диапазон");
    p.ScrollRowsToEnd(); Pump();
    var rEnd = p.VisibleRowRange();
    Expect(rEnd.Last == total && rEnd.AtEnd, $"«››»: {rEnd.Label}");
    Expect(s.HitTest(new Point(cell.X, s.HeaderHeight + s.RowLayout.Top(rows.Count - 1) - s.OffsetY + 5)).Kind == GridHitKind.AddRow, "в конце видна строка «+»");
    var gotEnd = WaitUntil(s.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Expect(gotEnd, "строки в конце прогружены");
    for (var i = 0; i < 5; i++) { p.ScrollRowsToStart(); Pump(); p.ScrollRowsToEnd(); Pump(); }
    Expect(p.VisibleRowRange().Last == total, "5 прыжков подряд: индикатор верный");
    p.ScrollRowsToStart(); Pump();
    Expect(p.VisibleRowRange() == r0, "«‹‹»: снова начало");
    var changes = 0;
    p.VisibleRangeChanged += (_, _) => changes++;
    s.OffsetY += 1; Pump();
    var c1 = changes;
    s.OffsetY += 0; Pump();
    Expect(changes == c1, "событие диапазона только на изменение");
    p.RestoreVerticalOffset(1234); Pump();
    Expect(Near(s.OffsetY, 1234), $"восстановление смещения: {s.OffsetY:N0}");
    var lastId = rows[total - 1].RecordId;
    s.OffsetY = 0; Pump();
    p.ScrollToRecord(lastId);
    WaitUntil(() => p.VisibleRowRange().Last == total, TimeSpan.FromSeconds(10)); Pump();
    Expect(p.VisibleRowRange().Last == total, "прокрутка к последней записи (в конец)");
    // Запись из середины берется у базы: обращение к строке списка по индексу вернуло бы
    // заглушку (страница еще не загружена) с RecordId = 0.
    var midId = repo2.GetRecordsPage(cid2, total / 2, 1)[0].Id!.Value;
    s.OffsetY = 0; Pump();
    p.ScrollToRecord(midId);
    WaitUntil(() => { var r = p.VisibleRowRange(); return r.First <= total / 2 + 1 && r.Last >= total / 2 + 1; }, TimeSpan.FromSeconds(10)); Pump();
    var rMid = p.VisibleRowRange();
    Expect(rMid.First <= total / 2 + 1 && rMid.Last >= total / 2 + 1, $"прокрутка к записи из середины: {rMid.Label}");
    w.Height = 600; Settle(w); Pump();
    Expect(p.VisibleRowRange().Total == total && p.VisibleRowRange().Last - p.VisibleRowRange().First < r0.Last - r0.First, "ресайз окна по высоте: диапазон уже, всего то же");

    w.Close();
    Console.WriteLine();
    Console.WriteLine(failed == 0 ? "Все проверки прошли." : $"ПРОВАЛОВ: {failed}");
    return failed == 0 ? 0 : 3;
}

// ── Стенд клавиш и мыши на фасаде (этап 3) ───────────────────────────────────
//
// Настоящий TableView на копии базы. Безголовая мышь и клавиатура проходят чеклист
// table-cells (разделы 1–3, 5–6 частично, 9–10, 12) и блок «Таблица» чеклиста shortcuts: клик,
// протягивание с автопрокруткой, Shift-клик, «№» и «+», правый клик внутри и вне выделения,
// стрелки и Shift/Ctrl, Home/End, PageUp/PageDown, Tab, Ctrl+A, Escape, перешагивание скрытой
// колонки, выделение записей и колонок, Ctrl+C/V/X, Delete, Ctrl+Z, выделение после перезагрузки.
// Буфер обмена — заглушка безголовой платформы (хранит текст), след копирования — временный
// журнал безопасности (без него копирование отказывает).
int RunKeys()
{
    var tmp = Path.Combine(outDir, "keys-" + Path.GetFileName(path));
    File.Copy(path, tmp, overwrite: true);
    using var mgr3 = DatabaseManager.Open(9403, "gridkeys", tmp);
    var repo3 = mgr3.Constructor;
    var cid3  = repo3.GetAllCollections().First(c => c.Name == col.Name).Id!.Value;




    var failed = 0;
    void Expect(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {what}");
        if (!ok) failed++;
    }
    static bool Near(double a, double b, double eps = 0.51) => Math.Abs(a - b) <= eps;

    // «Средняя» коллекция чеклиста: видимы пять полей (среди них текстовое — для буфера), третье
    // из них скрыто в «Отображении» — стрелки и Tab обязаны его перешагивать.
    var snap0     = repo3.ReadOpenSnapshot(cid3);
    var textField = snap0.Fields.First(f => f.FieldType == "text");
    var keep      = new List<DbField> { textField };
    keep.AddRange(snap0.Fields.Where(f => f.Id != textField.Id).Take(4));
    keep = keep.OrderBy(f => snap0.Fields.IndexOf(f)).ToList();
    var hidden    = keep.First(f => f.Id != textField.Id && keep.IndexOf(f) is >= 1 and <= 3);
    var shown     = keep.Where(f => f.Id != hidden.Id).Select(f => f.Id!.Value.ToString()).ToList();
    repo3.SaveColumnDisplay(cid3, snap0.Fields.Select(f => (f.Id!.Value.ToString(), shown.Contains(f.Id!.Value.ToString()), 1)));

    var w     = new Window { Width = 1600, Height = 900 };
    var table = new TableView();
    w.Content = table;
    w.Show();
    Settle(w);

    string? label = null;
    var opened = new List<int>();
    IReadOnlyList<TableColumnDef> defs = [];
    table.CellSelectionChanged += (_, l) => label = l;
    table.RecordClicked        += (_, id) => opened.Add(id);
    table.ColumnsBuilt         += (_, d) => defs = d;
    table.Load(repo3, cid3, repo3.ReadOpenSnapshot(cid3));
    Pump();
    var p = table.DrawnPanel!;
    var s = p.Surface;
    WaitUntil(s.VisibleCellsLoaded, TimeSpan.FromSeconds(20));
    var rows  = p.Rows!;
    var total = rows.Count - 1;
    var L     = s.Columns;
    var num   = TableColumnDef.NumKey;
    var plus  = TableColumnDef.AddPropKey;
    var title = TableRowVm.TitleKey;
    var textKey  = textField.Id!.Value.ToString();
    var hiddenKey = hidden.Id!.Value.ToString();
    var visibleKeys = new List<string> { title };
    visibleKeys.AddRange(shown);
    // Позиции в _colDefs (координаты CellRange) и в раскладке (координаты экрана).
    int Col(string key) => defs.ToList().FindIndex(d => d.Key == key);
    Point Center(int row, string key)
    {
        var c = L.IndexOf(key);
        return new Point(L.Left(c) - s.OffsetX + L.Width(c) / 2, s.HeaderHeight + s.RowLayout.Top(row) - s.OffsetY + s.RowPitch / 2);
    }
    Point HeaderAt(string key) => new(L.Left(L.IndexOf(key)) - s.OffsetX + 20, s.HeaderHeight / 2);
    void Click(Point pt, RawInputModifiers m = RawInputModifiers.None, MouseButton button = MouseButton.Left)
    {
        w.MouseMove(pt, m); w.MouseDown(pt, button, m); w.MouseUp(pt, button, m); Pump();
    }
    // Движения с зажатой кнопкой несут ее в модификаторах — как настоящая платформа; без этого
    // протягивание считает кнопку отпущенной и останавливается.
    const RawInputModifiers Held = RawInputModifiers.LeftMouseButton;
    void Drag(Point from, Point to)
    {
        w.MouseMove(from); w.MouseDown(from, MouseButton.Left); Pump();
        w.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), Held); Pump();
        w.MouseMove(to, Held); Pump();
        w.MouseUp(to, MouseButton.Left); Pump();
    }
    void Key(Key k, RawInputModifiers m = RawInputModifiers.None) { w.KeyPress(k, m); w.KeyRelease(k, m); Pump(); }
    CellRange? Range() => table.CurrentCellRange;
    string R(CellRange? r) => r is { } x ? $"({x.AnchorRow},{x.AnchorColumn})→({x.FocusRow},{x.FocusColumn})" : "нет";
    var f = visibleKeys;   // title, затем видимые поля в порядке колонок

    Console.WriteLine($"Колонки: {string.Join(", ", visibleKeys.Select(k => defs.First(d => d.Key == k).Header))}; скрыта «{hidden.Name}»; строк {total}");
    Expect(L.IndexOf(hiddenKey) < 0 && Col(hiddenKey) > 0, "скрытая колонка есть в определениях, но не в раскладке");
    Expect(shown.Count == 4 && visibleKeys.Count == 5, "пять видимых колонок значений");

    Console.WriteLine("Выделение мышью:");
    Click(Center(2, f[1]));
    Expect(Range() == CellRange.Single(2, Col(f[1])), $"клик: одна ячейка {R(Range())}");
    Expect(label is null, "счетчика нет");
    var b1 = s.CellSelectionBounds();
    Expect(b1 is { } bb && Near(bb.X, L.Left(L.IndexOf(f[1]))) && Near(bb.Width, L.Width(L.IndexOf(f[1])))
           && Near(bb.Y, s.HeaderHeight + s.RowPitch * 2) && Near(bb.Height, s.RowPitch - s.LineWidth),
           "рамка ровно по границам ячейки (до линии справа, без линии снизу)");
    Expect(table.SelectedRecordIds().Count == 0, "строка не выделена");
    Expect(s.IsKeyboardFocusWithin || s.IsFocused, "фокус в сетке");

    Drag(Center(2, f[1]), Center(4, f[2]));
    Expect(Range() == new CellRange(2, Col(f[1]), 4, Col(f[2])), $"протягивание вниз-вправо: {R(Range())}");
    Expect(label == "Выделено: 3 × 2", $"счетчик «{label}»");
    var b2 = s.CellSelectionBounds();
    Expect(b2 is { } b2v && Near(b2v.Height, 3 * s.RowPitch - s.LineWidth) && Near(b2v.Width, L.Width(L.IndexOf(f[1])) + L.Width(L.IndexOf(f[2]))), "рамка одна на весь прямоугольник");
    w.MouseMove(Center(7, f[3])); Pump();
    Expect(Range() == new CellRange(2, Col(f[1]), 4, Col(f[2])), "после отпускания выделение не тянется");

    Drag(Center(5, f[3]), Center(3, f[1]));
    Expect(Range() is { Top: 3, Bottom: 5 } r2 && r2.Left == Col(f[1]) && r2.Right == Col(f[3]), $"протягивание вверх-влево: {R(Range())}");
    Expect(label == "Выделено: 3 × 3", $"скрытая колонка внутри не считается: «{label}»");

    Click(Center(2, f[1]));
    Click(Center(6, f[4]), RawInputModifiers.Shift);
    Expect(Range() == new CellRange(2, Col(f[1]), 6, Col(f[4])), $"Shift+клик: от первой ячейки {R(Range())}");
    Expect(label == "Выделено: 5 × 4", $"счетчик «{label}»");
    Click(Center(4, f[2]), RawInputModifiers.Shift);
    Expect(Range() == new CellRange(2, Col(f[1]), 4, Col(f[2])), $"второй Shift+клик — от той же первой {R(Range())}");

    Click(Center(3, plus));
    Expect(Range() == new CellRange(2, Col(f[1]), 4, Col(f[2])), "клик по «+» ячейки не выделяет");
    Click(Center(3, num));
    Expect(Range() is null && label is null, "клик по «№» снимает ячейки");
    Expect(table.SelectedRecordIds().SequenceEqual([rows[3].RecordId]), "«№»: запись выделена");
    Expect(opened.Count == 1 && opened[0] == rows[3].RecordId, "«№»: запись открыта одним кликом (настройка по умолчанию)");

    Console.WriteLine("Правый клик:");
    Drag(Center(2, f[1]), Center(4, f[2]));
    Expect(table.SelectedRecordIds().Count == 0, "выделение ячеек гасит выделение записи");
    Click(Center(3, f[2]), button: MouseButton.Right);
    Expect(Range() == new CellRange(2, Col(f[1]), 4, Col(f[2])), "правый клик внутри выделения его сохраняет");
    Expect(table.LastOpenedMenu is { } m1 && m1.Items.Count >= 4, $"меню ячейки открыто ({table.LastOpenedMenu?.Items.Count} пунктов)");
    table.LastOpenedMenu?.Close(); Pump();
    Click(Center(6, f[3]), button: MouseButton.Right);
    Expect(Range() == CellRange.Single(6, Col(f[3])), $"правый клик вне выделения переносит его {R(Range())}");
    table.LastOpenedMenu?.Close(); Pump();
    Click(Center(1, num), button: MouseButton.Right);
    Expect(Range() is null && table.SelectedRecordIds().SequenceEqual([rows[1].RecordId]), "правый клик по «№» выделяет запись");
    Expect(table.LastOpenedMenu is { } m2 && m2.Items.Count > 6, $"меню записи открыто ({table.LastOpenedMenu?.Items.Count} пунктов)");
    table.LastOpenedMenu?.Close(); Pump();
    Click(HeaderAt(f[2]), button: MouseButton.Right);
    Expect(table.LastOpenedMenu is { } m3 && m3.Items.Count > 4, $"меню шапки открыто ({table.LastOpenedMenu?.Items.Count} пунктов)");
    // Заголовок — как «№»: столбец вне выделения выделяется один, внутри набор сохраняется.
    string Cols() => $"[{string.Join(", ", table.SelectedColumnKeys)}]";
    Expect(table.SelectedColumnKeys.SequenceEqual([f[2]]), $"правый клик по заголовку выделяет столбец {Cols()}");
    table.LastOpenedMenu?.Close(); Pump();
    Expect(table.SelectedColumnKeys.SequenceEqual([f[2]]), "после закрытия меню столбец остается выделенным");
    Click(HeaderAt(f[3]), RawInputModifiers.Shift);
    Click(HeaderAt(f[3]), button: MouseButton.Right);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[2], f[3]]), $"правый клик внутри выделения столбцов его сохраняет {Cols()}");
    table.LastOpenedMenu?.Close(); Pump();
    Click(HeaderAt(f[4]), button: MouseButton.Right);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[4]]), $"правый клик вне выделения столбцов выделяет один {Cols()}");
    table.LastOpenedMenu?.Close(); Pump();
    var chevron = s.FilterButtonRect(f[1])!.Value.Center;
    Click(s.TranslatePoint(chevron, w) ?? chevron, button: MouseButton.Right);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[1]]) && table.LastOpenedMenu is { IsOpen: true },
           $"правый клик по кнопке фильтра — то же меню и тот же столбец {Cols()}");
    table.LastOpenedMenu?.Close(); Pump();
    Click(HeaderAt(f[0]), button: MouseButton.Right);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[0]]), $"правый клик по «Названию» выделяет его столбец {Cols()}");
    table.LastOpenedMenu?.Close(); Pump();
    Click(Center(2, f[1]));
    Click(HeaderAt(f[2]), button: MouseButton.Right);
    Expect(Range() is null && table.SelectedColumnKeys.SequenceEqual([f[2]]), $"правый клик по заголовку снимает выделение ячеек {R(Range())}");
    table.LastOpenedMenu?.Close(); Pump();

    Console.WriteLine("Клавиатура:");
    Click(Center(2, f[1]));
    Key(Avalonia.Input.Key.Right);
    Expect(Range() == CellRange.Single(2, Col(f[2])), $"→ {R(Range())}");
    Key(Avalonia.Input.Key.Right);
    Expect(Range() == CellRange.Single(2, Col(f[3])), $"→ перешагнула скрытую «{hidden.Name}»: {R(Range())}");
    Key(Avalonia.Input.Key.Left);
    Expect(Range() == CellRange.Single(2, Col(f[2])), $"← назад через скрытую: {R(Range())}");
    Key(Avalonia.Input.Key.Down, RawInputModifiers.Shift);
    Key(Avalonia.Input.Key.Right, RawInputModifiers.Shift);
    Expect(Range() == new CellRange(2, Col(f[2]), 3, Col(f[3])), $"Shift+↓, Shift+→ растягивают тот же прямоугольник: {R(Range())}");
    Expect(label == "Выделено: 2 × 2", $"счетчик «{label}»");
    Key(Avalonia.Input.Key.Escape);
    Expect(Range() is null && label is null, "Escape снимает выделение и счетчик");
    Key(Avalonia.Input.Key.Right);
    Expect(Range() is null, "без выделения стрелка ничего не выделяет");

    Click(Center(2, f[1]));
    Key(Avalonia.Input.Key.End);
    Expect(Range() == CellRange.Single(2, Col(f[4])), $"End — к правому краю строки: {R(Range())}");
    Key(Avalonia.Input.Key.Home);
    Expect(Range() == CellRange.Single(2, Col(title)), $"Home — к левому: {R(Range())}");
    Key(Avalonia.Input.Key.Up);
    Key(Avalonia.Input.Key.Up);
    Key(Avalonia.Input.Key.Up);
    Expect(Range() == CellRange.Single(0, Col(title)), "у верхнего края стоит");
    Key(Avalonia.Input.Key.Down, RawInputModifiers.Control);
    Expect(Range() == CellRange.Single(total - 1, Col(title)), $"Ctrl+↓ — к последней строке: {R(Range())}");
    Expect(p.VisibleRowRange().Last == total, $"таблица прокрутилась к концу: {p.VisibleRowRange().Label}");
    var loadedEnd = WaitUntil(s.VisibleRowsLoaded, TimeSpan.FromSeconds(20));
    Expect(loadedEnd && rows.TryGetCached(total - 1) is not null, "последняя строка прогружена, а не «Загрузка…»");
    Key(Avalonia.Input.Key.Down);
    Expect(Range() == CellRange.Single(total - 1, Col(title)), "у нижнего края стоит");
    Key(Avalonia.Input.Key.Home, RawInputModifiers.Control);
    Expect(Range() == CellRange.Single(0, Col(title)) && Near(s.OffsetY, 0), "Ctrl+Home — в левый верхний угол, прокрутка в начало");
    Key(Avalonia.Input.Key.End, RawInputModifiers.Control);
    Expect(Range() == CellRange.Single(total - 1, Col(f[4])), $"Ctrl+End — в правый нижний: {R(Range())}");
    var page = p.PageRows();
    Key(Avalonia.Input.Key.PageUp);
    Expect(Range() == CellRange.Single(total - 1 - page, Col(f[4])), $"PageUp — на экран ({page}) вверх: {R(Range())}");
    Key(Avalonia.Input.Key.Up, RawInputModifiers.Control);
    Expect(Range() == CellRange.Single(0, Col(f[4])), "Ctrl+↑ — к первой строке");
    Key(Avalonia.Input.Key.PageDown);
    Expect(Range() == CellRange.Single(page, Col(f[4])), $"PageDown — на экран вниз: {R(Range())}");
    Key(Avalonia.Input.Key.Tab);
    Expect(Range() == CellRange.Single(page + 1, Col(title)), $"Tab с конца строки — на первую колонку следующей: {R(Range())}");
    Key(Avalonia.Input.Key.Tab, RawInputModifiers.Shift);
    Expect(Range() == CellRange.Single(page, Col(f[4])), $"Shift+Tab — обратно: {R(Range())}");
    Expect(table.SelectedRecordIds().Count == 0, "после Tab ни одна строка не выделена");
    Key(Avalonia.Input.Key.Left);
    Key(Avalonia.Input.Key.Left);
    Expect(Range() == CellRange.Single(page, Col(f[2])), $"←← через скрытую: {R(Range())}");
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Expect(Range() == new CellRange(0, Col(title), total - 1, Col(f[4])), $"Ctrl+A — вся таблица: {R(Range())}");
    Expect(label == $"Выделено: {total:N0} × 5", $"счетчик «{label}»");
    Key(Avalonia.Input.Key.Escape);

    Console.WriteLine("Записи и колонки:");
    Click(Center(3, num));
    Click(Center(5, num), RawInputModifiers.Control);
    Expect(table.SelectedRecordIds().SequenceEqual([rows[3].RecordId, rows[5].RecordId]), "Ctrl+клик по «№» добавляет запись");
    Click(Center(3, num), RawInputModifiers.Shift);
    Expect(table.SelectedRecordIds().SequenceEqual([rows[5].RecordId]), "Shift+клик по «№» снимает одну");
    Expect(Range() is null, "ячейки при этом не выделены");
    Click(Center(2, f[1]));
    Expect(table.SelectedRecordIds().Count == 0 && Range() == CellRange.Single(2, Col(f[1])), "клик по ячейке гасит записи");
    Click(HeaderAt(f[2]));
    Expect(table.SelectedColumnKeys.SequenceEqual([f[2]]) && Range() is null, "клик по заголовку выделяет колонку, ячейки сняты");
    Click(HeaderAt(f[4]), RawInputModifiers.Shift);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[2], f[4]]), "Shift+клик добавляет колонку (в порядке колонок)");
    Click(HeaderAt(f[2]), RawInputModifiers.Shift);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[4]]), "Shift+клик по выделенной снимает ее");
    Click(HeaderAt(num));
    Expect(table.SelectedColumnKeys.SequenceEqual([f[4]]), "заголовок «№» не выделяется");
    Click(Center(2, f[1]));
    Expect(table.SelectedColumnKeys.Count == 0, "клик по ячейке гасит колонки");

    // Ctrl+Shift+клик — диапазон от последней выделенной кликом, как в задачах; добавляется к
    // выделенному, якорь не сдвигается.
    var CtrlShift = RawInputModifiers.Control | RawInputModifiers.Shift;
    s.OffsetY = 0; Pump();
    Click(Center(2, num));
    var openedBefore = opened.Count;
    Click(Center(6, num), CtrlShift);
    Expect(table.SelectedRecordIds().OrderBy(x => x).SequenceEqual(Enumerable.Range(2, 5).Select(i => rows[i].RecordId).OrderBy(x => x)),
           $"Ctrl+Shift+клик по «№» — записи со 2-й по 6-ю ({table.SelectedRecordIds().Count})");
    Click(Center(9, num), RawInputModifiers.Control);
    Click(Center(11, num), CtrlShift);
    Expect(table.SelectedRecordIds().Count == 8 && table.SelectedRecordIds().Contains(rows[10].RecordId),
           $"второй диапазон — от последней добавленной, прежние остаются ({table.SelectedRecordIds().Count})");
    Click(Center(0, num), CtrlShift);
    Expect(table.SelectedRecordIds().Count == 12 && table.SelectedRecordIds().Contains(rows[0].RecordId),
           $"диапазон вверх от того же якоря — с 1-й по 12-ю ({table.SelectedRecordIds().Count})");
    Expect(Range() is null && opened.Count == openedBefore, "диапазон ничего не открывает и ячеек не выделяет");
    Click(HeaderAt(f[1]));
    Click(HeaderAt(f[4]), CtrlShift);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[1], f[2], f[3], f[4]]),
           $"Ctrl+Shift+клик по заголовку — колонки подряд, скрытая не входит: {string.Join(", ", table.SelectedColumnKeys)}");
    Click(HeaderAt(f[2]), RawInputModifiers.Control);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[1], f[3], f[4]]), "Ctrl+клик по заголовку снимает одну, как Shift+клик");
    // Снятие якорь не двигает: диапазон влево считается от первой выделенной кликом.
    Click(HeaderAt(f[0]), CtrlShift);
    Expect(table.SelectedColumnKeys.SequenceEqual([f[0], f[1], f[3], f[4]]),
           $"Ctrl+Shift+клик влево — от якоря до «Названия»: {string.Join(", ", table.SelectedColumnKeys)}");
    Click(Center(2, f[1]));

    Console.WriteLine("Буфер обмена и отмена:");
    var textId = textField.Id!.Value;
    var rid2   = rows[2].RecordId;
    var rid3   = rows[3].RecordId;
    if (string.IsNullOrEmpty(repo3.GetFieldValues(rid2).GetValueOrDefault(textId)))
    {
        repo3.SetFieldValue(rid2, textId, "проба буфера");
        table.ReloadRows(); Pump();
        WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20));
    }
    var value2 = repo3.GetFieldValues(rid2)[textId];
    var value3 = repo3.GetFieldValues(rid3).GetValueOrDefault(textId, "");
    var clipboard = TopLevel.GetTopLevel(table)!.Clipboard!;
    string? Clip()
    {
        var t = clipboard.GetTextAsync();
        WaitUntil(() => t.IsCompleted, TimeSpan.FromSeconds(2));
        return t.IsCompletedSuccessfully ? t.Result : null;
    }
    // Ячейка уже выделена прошлым кликом: второй клик по ней открыл бы правку (этап 4) — снимаем.
    Key(Avalonia.Input.Key.Escape);
    Click(Center(2, textKey));
    Key(Avalonia.Input.Key.C, RawInputModifiers.Control);
    Expect(WaitUntil(() => Clip() == value2, TimeSpan.FromSeconds(5)), $"Ctrl+C: в буфере «{value2}»");

    Click(Center(3, textKey));
    Key(Avalonia.Input.Key.V, RawInputModifiers.Control);
    Expect(WaitUntil(() => repo3.GetFieldValues(rid3).GetValueOrDefault(textId) == value2, TimeSpan.FromSeconds(10)), "Ctrl+V: значение записано в базу");
    WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump();
    Expect(Range() == CellRange.Single(3, Col(textKey)), $"после перезагрузки строк выделение на месте: {R(Range())}");
    Expect(p.Rows!.TryGetCached(3)?[textKey] == value2, "ячейка показывает вставленное");
    Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
    Expect(WaitUntil(() => repo3.GetFieldValues(rid3).GetValueOrDefault(textId, "") == value3, TimeSpan.FromSeconds(10)), "Ctrl+Z: вставка отменена");
    WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump();
    Click(Center(2, textKey));
    Key(Avalonia.Input.Key.Delete);
    Expect(WaitUntil(() => repo3.GetFieldValues(rid2).GetValueOrDefault(textId, "") == "", TimeSpan.FromSeconds(10)), "Delete: значение стерто без вопроса");
    WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump();
    Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
    Expect(WaitUntil(() => repo3.GetFieldValues(rid2).GetValueOrDefault(textId) == value2, TimeSpan.FromSeconds(10)), "Ctrl+Z: очистка отменена");
    WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump();
    Key(Avalonia.Input.Key.Escape);   // та же ячейка выделена — второй клик открыл бы правку
    Click(Center(2, textKey));
    Key(Avalonia.Input.Key.X, RawInputModifiers.Control);
    Expect(WaitUntil(() => repo3.GetFieldValues(rid2).GetValueOrDefault(textId, "") == "" && Clip() == value2, TimeSpan.FromSeconds(10)), "Ctrl+X: значение в буфере и стерто");
    WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump();
    Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
    Expect(WaitUntil(() => repo3.GetFieldValues(rid2).GetValueOrDefault(textId) == value2, TimeSpan.FromSeconds(10)), "Ctrl+Z: вырезание отменено");
    WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump();

    RunRectangle();

    // Прямоугольник из нескольких строк и столбцов: Delete, Ctrl+X и Ctrl+V обязаны дойти до
    // КАЖДОЙ ячейки, в том числе до левой верхней, с которой начато выделение, — как бы его ни
    // поставили: протягиванием, Shift+кликом, клавишами или после открытой на ней правки.
    // Протягивание ОТ ячейки в правке выделяет текст в поле, а не ячейки (как в табличном
    // редакторе), — такого сценария здесь нет.
    void RunRectangle()
    {
        Console.WriteLine("Прямоугольник 3 × 2:");
        string kText = f[1], kDate = f[2];
        int fText = int.Parse(kText), fDate = int.Parse(kDate);
        var ids = Enumerable.Range(10, 3).Select(r => p.Rows!.TryGetCached(r)!.RecordId).ToList();
        string Val(int i, int field) => repo3.GetFieldValues(ids[i]).GetValueOrDefault(field, "");
        bool AllEmpty() => Enumerable.Range(0, 3).All(i => Val(i, fText) == "" && Val(i, fDate) == "");
        bool AllFilled() => Enumerable.Range(0, 3).All(i => Val(i, fText) != "" && Val(i, fDate) != "");
        string Dump() => string.Join(" | ", Enumerable.Range(0, 3).Select(i => $"«{Val(i, fText)}» «{Val(i, fDate)}»"));
        void Settled() { WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump(); }
        void Fill()
        {
            for (var i = 0; i < 3; i++)
            {
                repo3.SetFieldValue(ids[i], fText, $"строка {i + 1}");
                repo3.SetFieldValue(ids[i], fDate, $"2026-03-0{i + 1}");
            }
            table.ReloadRows(); Pump(); Settled();
        }
        void Fresh() { Key(Avalonia.Input.Key.Escape); Click(Center(20, f[3])); Key(Avalonia.Input.Key.Escape); }
        var whole = new CellRange(10, Col(kText), 12, Col(kDate));

        void Clear(string how, Action select)
        {
            Fill(); Fresh();
            select();
            Expect(Range() is { } got && got.Top == whole.Top && got.Bottom == whole.Bottom && got.Left == whole.Left
                   && got.Right == whole.Right && !table.IsCellEditing, $"{how}: выделено 3 × 2 без правки {R(Range())}, правка {table.IsCellEditing}");
            Key(Avalonia.Input.Key.Delete);
            Expect(WaitUntil(AllEmpty, TimeSpan.FromSeconds(10)), $"{how}: Delete стер все шесть, левую верхнюю тоже: {Dump()}");
            Settled();
        }

        Clear("протягивание", () => Drag(Center(10, kText), Center(12, kDate)));
        Clear("протягивание снизу вверх", () => Drag(Center(12, kDate), Center(10, kText)));
        Clear("клик и Shift+клик", () => { Click(Center(10, kText)); Click(Center(12, kDate), RawInputModifiers.Shift); });
        Clear("Shift+стрелки", () =>
        {
            Click(Center(10, kText));
            Key(Avalonia.Input.Key.Down, RawInputModifiers.Shift);
            Key(Avalonia.Input.Key.Down, RawInputModifiers.Shift);
            Key(Avalonia.Input.Key.Right, RawInputModifiers.Shift);
        });
        Clear("правка на левой верхней, затем Shift+клик", () =>
        {
            Click(Center(10, kText));
            Click(Center(10, kText));            // второй клик открывает правку
            Click(Center(12, kDate), RawInputModifiers.Shift);
        });
        Clear("клик, затем протягивание от той же ячейки", () =>
        {
            Click(Center(10, kText));
            Drag(Center(10, kText), Center(12, kDate));
        });
        Clear("правка на левой верхней, протягивание к ней снизу справа", () =>
        {
            Click(Center(10, kText));
            Click(Center(10, kText));
            Drag(Center(12, kDate), Center(10, kText));
        });
        Clear("набор на левой верхней, Shift+клик", () =>
        {
            Click(Center(10, kText));
            w.KeyTextInput("ж"); Pump();
            Click(Center(12, kDate), RawInputModifiers.Shift);
        });
        Clear("набор на левой верхней, Enter, Shift+клик", () =>
        {
            Click(Center(10, kText));
            w.KeyTextInput("ж"); Pump();
            Key(Avalonia.Input.Key.Enter);
            Click(Center(12, kDate), RawInputModifiers.Shift);
        });
        Clear("набор на левой верхней, Shift+стрелки", () =>
        {
            Click(Center(10, kText));
            w.KeyTextInput("ж"); Pump();
            Key(Avalonia.Input.Key.Down, RawInputModifiers.Shift);
            Key(Avalonia.Input.Key.Down, RawInputModifiers.Shift);
            Key(Avalonia.Input.Key.Right, RawInputModifiers.Shift);
        });
        Clear("набор на левой верхней, Escape, Shift+клик", () =>
        {
            Click(Center(10, kText));
            w.KeyTextInput("ж"); Pump();
            Key(Avalonia.Input.Key.Escape);
            Click(Center(12, kDate), RawInputModifiers.Shift);
        });

        // С «Названием» в левом верхнем углу: стертое название хранится «Без названия» (TitleText).
        Fill(); Fresh();
        Drag(Center(10, title), Center(12, kText));
        Key(Avalonia.Input.Key.Delete);
        Expect(WaitUntil(() => Enumerable.Range(0, 3).All(i => Val(i, fText) == ""), TimeSpan.FromSeconds(10)), $"с «Названием»: текст стерт: {Dump()}");
        Settled();
        var titles = Enumerable.Range(0, 3).Select(i => repo3.GetRecord(ids[i], refresh: true)?.Title ?? "?").ToList();
        var shownTitles = Enumerable.Range(10, 3).Select(r => p.Rows!.TryGetCached(r)?[title] ?? "?").ToList();
        Expect(titles.All(t => t == TitleText.Placeholder) && shownTitles.All(t => t == TitleText.Placeholder),
               $"с «Названием»: стертое название — «{TitleText.Placeholder}», как в любом месте правки: {string.Join(" | ", shownTitles)}");
        Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Settled();

        Fill(); Fresh();
        Drag(Center(10, kText), Center(12, kDate));
        Key(Avalonia.Input.Key.X, RawInputModifiers.Control);
        Expect(WaitUntil(() => AllEmpty() && Clip()?.Split('\n').Length >= 3, TimeSpan.FromSeconds(10)),
               $"Ctrl+X: все шесть в буфере и стерты: {Dump()}");
        Settled();
        Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Expect(WaitUntil(AllFilled, TimeSpan.FromSeconds(10)), $"Ctrl+Z вернул все шесть: {Dump()}");
        Settled();

        // Блок 3 × 2 в одну ячейку — разливается вниз и вправо.
        Fresh();
        Expect(clipboard.SetTextAsync("а1\t01.04.2026\nа2\t02.04.2026\nа3\t03.04.2026").Wait(2000), "буфер: блок 3 × 2");
        Click(Center(10, kText));
        Key(Avalonia.Input.Key.V, RawInputModifiers.Control);
        Expect(WaitUntil(() => Val(0, fText) == "а1" && Val(2, fText) == "а3" && Val(0, fDate) == "2026-04-01" && Val(2, fDate) == "2026-04-03",
                         TimeSpan.FromSeconds(10)), $"Ctrl+V блока в одну ячейку: {Dump()}");
        Settled();

        // Замена значений прямоугольника одним значением строки.
        Fresh();
        Expect(clipboard.SetTextAsync("з\t05.05.2026").Wait(2000), "буфер: строка из двух значений");
        Drag(Center(10, kText), Center(12, kDate));
        Key(Avalonia.Input.Key.V, RawInputModifiers.Control);
        Expect(WaitUntil(() => Enumerable.Range(0, 3).All(i => Val(i, fText) == "з" && Val(i, fDate) == "2026-05-05"), TimeSpan.FromSeconds(10)),
               $"Ctrl+V строки в 3 × 2 заменил все шесть, левую верхнюю тоже: {Dump()}");
        Settled();
        Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Expect(WaitUntil(() => Val(0, fText) == "а1" && Val(2, fDate) == "2026-04-03", TimeSpan.FromSeconds(10)), $"Ctrl+Z вернул блок: {Dump()}");
        Settled();
        Key(Avalonia.Input.Key.Escape);
    }

    Console.WriteLine("Автопрокрутка при протягивании:");
    Key(Avalonia.Input.Key.Escape);   // ячейка выделена прошлым шагом — второй клик открыл бы правку
    Click(Center(2, f[1]));
    var start = Center(2, f[1]);
    w.MouseMove(start); w.MouseDown(start, MouseButton.Left); Pump();
    var below = new Point(start.X, w.Height + 30);   // за нижним краем окна
    for (var i = 0; i < 6; i++) { w.MouseMove(below, Held); Pump(); }
    var scrolled = s.OffsetY;
    var focusRow = Range()?.FocusRow ?? -1;
    Expect(scrolled > 0, $"таблица едет за указателем: смещение {scrolled:N0}");
    Expect(focusRow > 2 && Range()?.AnchorRow == 2, $"выделение дотянуто до нижней видимой строки: {R(Range())}");
    Expect(focusRow == p.VisibleRowRange().Last - 1 || focusRow == p.VisibleRowRange().Last - 2, $"фокус на краю окна ({focusRow + 1} при {p.VisibleRowRange().Label})");
    // Меряется от отпускания: тик автопрокрутки мог прийти в тот же прогон диспетчера, что и оно.
    w.MouseUp(below, MouseButton.Left); Pump();
    var after       = Range();
    var afterOffset = s.OffsetY;
    w.MouseMove(new Point(start.X, 500));
    WaitUntil(() => false, TimeSpan.FromMilliseconds(300));   // таймер, будь он жив, успел бы тикнуть
    Expect(Range() == after && Near(s.OffsetY, afterOffset),
           $"после отпускания за краем — не тянется и не едет ({R(after)} → {R(Range())}, {afterOffset:N0} → {s.OffsetY:N0})");

    Console.WriteLine("Выделение следует за данными:");
    s.OffsetY = 0; Pump();
    Click(Center(2, f[1]));
    var ridSel = rows[2].RecordId;
    table.ReloadRows(); Pump();
    WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump();
    Expect(Range() == CellRange.Single(2, Col(f[1])) && p.Rows!.TryGetCached(2)?.RecordId == ridSel, "после перезагрузки строк рамка на той же записи");
    repo3.SaveColumnDisplay(cid3, snap0.Fields.Select(fl => (fl.Id!.Value.ToString(), shown.Contains(fl.Id!.Value.ToString()) && fl.Id != hidden.Id && fl.Id!.Value.ToString() != f[2], 1)));
    table.ReloadColumns(); Pump();
    Expect(Range() == CellRange.Single(2, Col(f[1])) && s.CellSelectionBounds() is { } b3 && Near(b3.X, s.Columns.Left(s.Columns.IndexOf(f[1]))), "скрытие соседней колонки: рамка на той же колонке");
    Key(Avalonia.Input.Key.Right);
    Expect(Range() == CellRange.Single(2, Col(f[3])), $"→ перешагивает обе скрытые: {R(Range())}");

    Console.WriteLine("Расположение текста:");
    // Пункты подменю «Форматирование текста» → «Расположение текста» ищутся по подписи; галочка
    // выбранного — иконка перед подписью (MenuIcons.ChoiceHeader), у прочих на ее месте распорка.
    MenuItem? Find(ItemsControl? parent, string text) =>
        parent?.Items.OfType<MenuItem>().FirstOrDefault(i =>
            i.Header is Control c && Avalonia.LogicalTree.LogicalExtensions.GetSelfAndLogicalDescendants(c)
                                         .OfType<TextBlock>().Any(t => t.Text == text));
    MenuItem? Placement(string text) =>
        Find(Find(Find(table.LastOpenedMenu, "Форматирование текста"), "Расположение текста"), text);
    void Choose(MenuItem? item)
    {
        item?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent, item));
        Pump();
        table.LastOpenedMenu?.Close();
        Pump();
    }
    static bool Checked(MenuItem? item) => item?.Header is StackPanel sp && sp.Children.FirstOrDefault() is Viewbox;
    RowStyle CellAt(int row, string key) =>
        repo3.GetCellStylesBatch(cid3, [rows[row].RecordId]).GetValueOrDefault(rows[row].RecordId)?.GetValueOrDefault(key) ?? RowStyle.None;
    // Координаты — по текущей раскладке: выше стенд прятал столбец и перестраивал колонки.
    Point CellNow(int row, string key)
    {
        var lc = s.Columns;
        var c  = lc.IndexOf(key);
        return new(lc.Left(c) - s.OffsetX + lc.Width(c) / 2, s.HeaderHeight + s.RowLayout.Top(row) - s.OffsetY + s.RowPitch / 2);
    }
    Point HeaderNow(string key)
    {
        var lc = s.Columns;
        return new(lc.Left(lc.IndexOf(key)) - s.OffsetX + 20, s.HeaderHeight / 2);
    }
    var placementItems = new[] { "Горизонтально слева", "Горизонтально по центру", "Горизонтально справа",
                                 "Вертикально сверху", "Вертикально по центру", "Вертикально снизу", "По умолчанию" };

    s.OffsetY = 0; Pump();
    Key(Avalonia.Input.Key.Escape);
    // Ячейки: от «Названия» до текстовой колонки в двух строках; галочки между ними пропускаются.
    Drag(CellNow(1, title), CellNow(2, textKey));
    var alignCols = visibleKeys.Take(visibleKeys.IndexOf(textKey) + 1).ToList();
    Click(CellNow(1, title), button: MouseButton.Right);
    Expect(placementItems.All(t => Placement(t) is not null), "меню ячейки: «Форматирование текста» → «Расположение текста», семь пунктов");
    Expect(Checked(Placement("Горизонтально слева")) && Checked(Placement("Вертикально по центру"))
           && !Checked(Placement("Горизонтально по центру")),
           "галочки у того, что видно в ячейке: слева и по центру");
    Expect(Placement("Горизонтально по центру")?.InputGesture?.ToString() == "Ctrl+E", $"у пункта — его клавиша: {Placement("Горизонтально по центру")?.InputGesture}");
    Choose(Placement("Горизонтально по центру"));
    var textCells = alignCols.Where(k => defs.First(d => d.Key == k).FieldType != "checkbox").ToList();
    Expect(Enumerable.Range(1, 2).All(r => textCells.All(k => CellAt(r, k).HAlign == CellHAlign.Center)),
           $"по центру — все выделенные ячейки ({textCells.Count} столбца × 2 строки)");
    Expect(alignCols.Except(textCells).All(k => CellAt(1, k).IsEmpty), "ячейки галочек пропущены");
    Expect(Range() == new CellRange(1, Col(title), 2, Col(textKey)), "выделение ячеек на месте");

    s.Focus(); Pump();
    Key(Avalonia.Input.Key.R, RawInputModifiers.Control);
    Expect(WaitUntil(() => CellAt(2, textKey).HAlign == CellHAlign.Right, TimeSpan.FromSeconds(5))
           && CellAt(1, title).HAlign == CellHAlign.Right, "Ctrl+R — выделенные ячейки справа");
    Click(CellNow(1, title), button: MouseButton.Right);
    Expect(Checked(Placement("Горизонтально справа")) && !Checked(Placement("Горизонтально по центру")), "галочка переехала на «справа»");
    Choose(Placement("По умолчанию"));
    Expect(Enumerable.Range(1, 2).All(r => textCells.All(k => CellAt(r, k).IsEmpty)), "«По умолчанию» снимает расположение ячеек");

    // Столбец: Ctrl+E по выделенному заголовку — аспект column_meta, выделение столбца остается.
    string Styles() =>
        $"столбцы [{string.Join(", ", table.SelectedColumnKeys)}], ячейки {R(Range())}, записи [{string.Join(", ", table.SelectedRecordIds())}]; "
        + $"оформление столбцов: {string.Join("; ", repo3.GetColumnStyles(cid3).Select(kv => $"{kv.Key}={kv.Value}"))}";
    Click(HeaderNow(textKey));
    var picked = table.SelectedColumnKeys.SequenceEqual([textKey]);
    Expect(picked, picked ? "клик по заголовку выделил столбец" : $"клик по заголовку выделил столбец: {Styles()}");
    Key(Avalonia.Input.Key.E, RawInputModifiers.Control);
    var centered = repo3.GetColumnStyles(cid3).GetValueOrDefault(textKey).HAlign == CellHAlign.Center;
    Expect(centered, centered ? "Ctrl+E по столбцу — столбец по центру" : $"Ctrl+E по столбцу — столбец по центру: {Styles()}");
    Expect(table.SelectedColumnKeys.SequenceEqual([textKey]), "выделение столбца после расположения не снимается");
    Click(HeaderNow(textKey), button: MouseButton.Right);
    Expect(Checked(Placement("Горизонтально по центру")) && Checked(Placement("Вертикально по центру")), "меню столбца: галочки у его расположения");
    Choose(Placement("Вертикально сверху"));
    Expect(repo3.GetColumnStyles(cid3).GetValueOrDefault(textKey) is { HAlign: CellHAlign.Center, VAlign: CellVAlign.Top },
           "«Вертикально сверху» — вертикаль столбца, горизонталь та же");
    Click(HeaderNow(textKey), button: MouseButton.Right);
    Choose(Placement("По умолчанию"));
    Expect(!repo3.GetColumnStyles(cid3).ContainsKey(textKey), "«По умолчанию» снимает оба расположения столбца");
    if (defs.FirstOrDefault(d => d.FieldType == "checkbox" && s.Columns.IndexOf(d.Key) >= 0) is { } check)
    {
        Click(HeaderNow(check.Key), button: MouseButton.Right);
        Expect(Find(table.LastOpenedMenu, "Форматирование текста") is null, $"у галочки «{check.Header}» пункта нет");
        table.LastOpenedMenu?.Close(); Pump();
    }

    // Строка: в меню «№» галочек нет, пока строке ничего не задано; Ctrl+L — по выделенной записи.
    // Клик по ячейке гасит выделение столбца, Escape — ячейку: адресат клавиши — только запись.
    Click(CellNow(2, textKey));
    Key(Avalonia.Input.Key.Escape);
    Click(CellNow(4, num), button: MouseButton.Right);
    Expect(placementItems.Take(6).All(t => !Checked(Placement(t))), "меню строки без своего расположения — без галочек");
    Choose(Placement("Вертикально снизу"));
    var rid4 = rows[4].RecordId;
    Expect(repo3.GetRowStyles(cid3).GetValueOrDefault(rid4).VAlign == CellVAlign.Bottom, "«Вертикально снизу» — строка");
    Expect(table.SelectedRecordIds().SequenceEqual([rid4]) && Range() is null, "выделена запись, ячеек нет");
    Key(Avalonia.Input.Key.L, RawInputModifiers.Control);
    Expect(repo3.GetRowStyles(cid3).GetValueOrDefault(rid4) is { HAlign: CellHAlign.Left, VAlign: CellVAlign.Bottom },
           "Ctrl+L — по выделенной записи, вертикаль на месте");
    Click(CellNow(4, num), button: MouseButton.Right);
    Expect(Checked(Placement("Горизонтально слева")) && Checked(Placement("Вертикально снизу")), "меню строки: галочки у заданного строке");
    Choose(Placement("По умолчанию"));
    Expect(!repo3.GetRowStyles(cid3).ContainsKey(rid4), "«По умолчанию» снимает расположение строки");

    // Выставленное последним видно во всей строке и колонке: их смена снимает этот аспект у ячеек —
    // в базе по всем записям и сразу в загруженных строках, без перечитывания; на пересечении строки
    // и колонки побеждает выставленное позже, в том числе «По умолчанию».
    Console.WriteLine("Выставленное последним:");
    // Строки — из текущего списка сетки: выше стенд перестраивал колонки, и прежние объекты строк
    // устарели (то, что рисуется, живет в новых).
    RowStyle Cached(int row, string key) => p.Rows![row].CellStyle(key);
    RowStyle SeenAt(int row, string key) =>
        RowStyle.Resolve(p.Rows![row].CellStyle(key), p.RowStyleOf(p.Rows![row].RecordId), p.ColumnStyleOf(key));
    var otherKey = visibleKeys.First(k => k != textKey && k != title && defs.First(d => d.Key == k).FieldType != "checkbox");

    Drag(CellNow(5, textKey), CellNow(6, textKey));
    s.Focus(); Pump();
    Key(Avalonia.Input.Key.R, RawInputModifiers.Control);
    Click(CellNow(5, textKey), button: MouseButton.Right);
    Choose(Placement("Вертикально снизу"));
    // Ячейки пишутся асинхронно (выделение собирается в фоне) — ждем, пока дойдут до строк.
    Expect(WaitUntil(() => Cached(5, textKey) is { HAlign: CellHAlign.Right, VAlign: CellVAlign.Bottom }
                           && Cached(6, textKey).HAlign == CellHAlign.Right, TimeSpan.FromSeconds(5)),
           "ячейки столбца: справа и снизу");
    Click(HeaderNow(textKey));
    Key(Avalonia.Input.Key.E, RawInputModifiers.Control);
    Expect(CellAt(5, textKey) is { HAlign: null, VAlign: CellVAlign.Bottom } && CellAt(6, textKey).HAlign is null,
           "столбец по центру снимает горизонталь его ячеек в базе, вертикаль остается");
    Expect(Cached(5, textKey) is { HAlign: null, VAlign: CellVAlign.Bottom } && Cached(6, textKey).HAlign is null,
           "и в загруженных строках, без перечитывания");
    Expect(SeenAt(5, textKey) is { HAlign: CellHAlign.Center, VAlign: CellVAlign.Bottom },
           $"в ячейке видно: по центру от столбца и снизу свое ({SeenAt(5, textKey).HAlign}, {SeenAt(5, textKey).VAlign})");

    Click(CellNow(7, num), button: MouseButton.Right);
    Choose(Placement("Горизонтально справа"));
    Expect(SeenAt(7, textKey).HAlign == CellHAlign.Right && SeenAt(8, textKey).HAlign == CellHAlign.Center,
           "строка справа позже столбца: на пересечении строка, в других строках столбец");
    Click(HeaderNow(textKey), button: MouseButton.Right);
    Choose(Placement("По умолчанию"));
    Expect(SeenAt(7, textKey).HAlign is null && SeenAt(7, otherKey).HAlign == CellHAlign.Right,
           "«По умолчанию» столбцу позже строки: в нем обычный вид, остальная строка справа");
    Click(CellNow(7, num), button: MouseButton.Right);
    Choose(Placement("По умолчанию"));
    Drag(CellNow(5, textKey), CellNow(6, textKey));
    Click(CellNow(5, textKey), button: MouseButton.Right);
    Choose(Placement("По умолчанию"));
    Expect(!repo3.GetRowStyles(cid3).ContainsKey(rows[7].RecordId) && CellAt(5, textKey).IsEmpty && SeenAt(7, textKey).HAlign is null,
           "уборка: строка и ячейки по умолчанию");

    // Контекстные меню (ContextMenuDismiss): открыто одно, гаснет от нажатия не в нем и от ухода
    // из программы. На Windows и macOS меню — отдельное попап-окно, и правый щелчок мимо открытого
    // меню оставлял новое меню без подложки: клик мимо его не закрывал, следующие щелчки клали меню
    // поверх. Здесь меню живет в слое окна, и сбоя не видно — его воспроизводит второе окно ниже.
    Console.WriteLine("Контекстные меню:");
    Click(CellNow(3, textKey), button: MouseButton.Right);
    var cellMenu = table.LastOpenedMenu;
    Click(CellNow(6, num), button: MouseButton.Right);
    var rowMenu = table.LastOpenedMenu;
    Click(HeaderNow(textKey), button: MouseButton.Right);
    var headMenu = table.LastOpenedMenu;
    Expect(cellMenu is { IsOpen: false } && rowMenu is { IsOpen: false } && headMenu is { IsOpen: true }
           && ContextMenuDismiss.Count == 1, $"правый щелчок мимо меню: прежнее закрыто, открыто одно ({ContextMenuDismiss.Count})");
    Click(CellNow(8, textKey));
    Expect(headMenu is { IsOpen: false } && ContextMenuDismiss.Count == 0, "клик мимо меню закрывает его");

    // Нажатие внутри меню и в его подменю меню не закрывает; разделитель — место без действия.
    Point MiddleOf(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), w) ?? default;
    bool OpenAfterPress(Control? target, ContextMenu menu)
    {
        if (target is null) return false;
        var at = MiddleOf(target);
        w.MouseMove(at); w.MouseDown(at, MouseButton.Left); Pump();
        var open = menu.IsOpen;
        w.MouseUp(at, MouseButton.Left); Pump();
        return open && menu.IsOpen;
    }
    Click(CellNow(3, textKey), button: MouseButton.Right);
    var openMenu = table.LastOpenedMenu!;
    Expect(OpenAfterPress(openMenu.Items.OfType<Separator>().FirstOrDefault(), openMenu), "нажатие внутри меню его не закрывает");
    var formatItem = Find(openMenu, "Форматирование текста")!;
    formatItem.IsSubMenuOpen = true; Pump();
    var placeItem = Find(formatItem, "Расположение текста")!;
    placeItem.IsSubMenuOpen = true; Pump();
    Expect(OpenAfterPress(placeItem.Items.OfType<Separator>().FirstOrDefault(), openMenu) && placeItem.IsSubMenuOpen,
           "нажатие в подменю меню не закрывает");
    // Уход в другую программу — событие платформы окна (в Avalonia 11.2 оно внутреннее).
    void Platform(string name) =>
        (typeof(Avalonia.Platform.IWindowBaseImpl).GetProperty(name, System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(w.PlatformImpl) as Action)?.Invoke();
    Platform("Activated"); Pump();
    Platform("Deactivated"); Pump();
    Expect(!openMenu.IsOpen && ContextMenuDismiss.Count == 0, "уход в другую программу закрывает меню");
    Platform("Activated"); Pump();

    // Меню как на Windows и macOS (OwnWindowMenuInput): перехват нажатий меню не видит окна
    // программы, и меню открывается на нажатии правой кнопки, как в таблице.
    var pad  = new Border { Background = Avalonia.Media.Brushes.White };
    var w2   = new Window { Width = 640, Height = 420, Content = pad };
    w2.Show();
    Settle(w2);
    var made = new List<ContextMenu>();
    pad.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
    {
        if (!e.GetCurrentPoint(pad).Properties.IsRightButtonPressed) return;
        var m = new ContextMenu(new OwnWindowMenuInput()) { ItemsSource = new[] { "Первый", "Второй" } };
        made.Add(m);
        m.Open(pad);
    });
    void ClickPad(Point at, MouseButton button = MouseButton.Left)
    {
        w2.MouseMove(at); w2.MouseDown(at, button); w2.MouseUp(at, button); Pump();
    }
    int OpenMade() => made.Count(m => m.IsOpen);
    ClickPad(new Point(40, 40), MouseButton.Right);
    ClickPad(new Point(320, 260), MouseButton.Right);
    Expect(made.Count == 2 && !made[0].IsOpen && made[1].IsOpen, $"меню отдельным окном: правый щелчок мимо — открыто одно новое ({OpenMade()})");
    ClickPad(new Point(560, 60));
    Expect(OpenMade() == 0, $"меню отдельным окном: клик мимо закрывает и новое меню ({OpenMade()})");
    ClickPad(new Point(40, 40), MouseButton.Right);
    ClickPad(new Point(320, 260), MouseButton.Right);
    ClickPad(new Point(500, 120), MouseButton.Right);
    Expect(OpenMade() == 1, $"меню отдельным окном: три правых щелчка подряд — открыто одно ({OpenMade()})");
    ContextMenuDismiss.CloseAll();
    w2.Close();
    Pump();

    // Боковая панель: тот же Ctrl+Shift+клик — диапазон видимых строк одной базы от якоря.
    Console.WriteLine("Боковая панель:");
    {
        var names = Enumerable.Range(1, 5).Select(i => $"Диапазон {i}").ToList();
        foreach (var n in names) repo3.AddCollection(new DbCollection { Name = n });
        var tree = new SmartBase.Controls.Components.SidebarTree();
        var ws   = new Window { Width = 320, Height = 900, Content = tree };
        ws.Show();
        tree.Initialize(repo3, SmartBase.Controls.Components.SidebarMode.Constructor);
        Settle(ws);
        Border? RowOf(string name) => tree.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.DataContext is SmartBase.Controls.Components.SidebarRow r && r.Node.Name == name
                                 && b.Classes.Count >= 0 && b.IsVisible && b.Bounds.Height > 0
                                 && b.GetVisualParent() is not Border { DataContext: SmartBase.Controls.Components.SidebarRow });
        void ClickRow(string name, RawInputModifiers m = RawInputModifiers.None)
        {
            if (RowOf(name) is not { } row) { Expect(false, $"строка «{name}» на экране"); return; }
            var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), ws)!.Value;
            ws.MouseMove(at, m); ws.MouseDown(at, MouseButton.Left, m); ws.MouseUp(at, MouseButton.Left, m); Pump();
        }
        List<string> Picked() => tree.GetVisualDescendants().OfType<Control>()
            .Select(c => c.DataContext).OfType<SmartBase.Controls.Components.SidebarRow>().Distinct()
            .Where(r => r.Selected).Select(r => r.Node.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        ClickRow(names[0], RawInputModifiers.Control);
        ClickRow(names[3], RawInputModifiers.Control | RawInputModifiers.Shift);
        Expect(Picked().SequenceEqual(names.Take(4)), $"Ctrl+клик и Ctrl+Shift+клик — с 1-й по 4-ю: {string.Join(", ", Picked())}");
        ClickRow(names[1], RawInputModifiers.Shift);
        Expect(Picked().SequenceEqual([names[0], names[2], names[3]]), $"Shift+клик снимает одну: {string.Join(", ", Picked())}");
        ClickRow(names[4], RawInputModifiers.Control | RawInputModifiers.Shift);
        Expect(Picked().SequenceEqual(names),
               $"диапазон — от якоря (1-й) до 5-й, снятая внутри добавляется снова, как в задачах: {string.Join(", ", Picked())}");
        ws.Close();
        Pump();
    }

    // Вставка за край таблицы — последней: она заводит записи и столбцы. Блок 2 × 3 от последней
    // строки в последнем видимом столбце: одна строка и два столбца не помещаются.
    Console.WriteLine("Вставка за край таблицы:");
    w.Activate(); Pump();
    int Records() => repo3.GetRecords(cid3).Count;
    List<DbField> PasteFields() => repo3.GetFields(cid3).Where(fl => fl.Name.StartsWith("Колонка ", StringComparison.Ordinal)).ToList();
    void Settle2() { WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump(); }
    // Уведомления без окна копятся в очереди AppNotifications — по ним видно, что действие кончилось.
    System.Collections.IList NoticeList() => (System.Collections.IList)typeof(AppNotifications)
        .GetField("_pending", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
    List<string> Notices() => NoticeList().Cast<object>().Select(o => o.ToString() ?? "").Select(t => t.Split(", ", 2)[^1].TrimEnd(')')).ToList();
    void PasteAtCorner()
    {
        Key(Avalonia.Input.Key.Escape);
        s.OffsetY = 0; s.OffsetX = 0; Pump();
        Click(CellNow(0, title));
        Key(Avalonia.Input.Key.End, RawInputModifiers.Control);
        Expect(clipboard.SetTextAsync("x1\tx2\tx3\ny1\ty2\ty3").Wait(2000), "буфер: блок 2 × 3");
        NoticeList().Clear();
        Key(Avalonia.Input.Key.V, RawInputModifiers.Control);
        // Вставка асинхронна (буфер читается с ожиданием): ждем ее итог — уведомление «Вставлено…».
        Expect(WaitUntil(() => Notices().Any(n => n.StartsWith("Вставл", StringComparison.Ordinal)), TimeSpan.FromSeconds(10)),
               $"вставка закончилась: {string.Join(" | ", Notices())}");
        Settle2();
    }
    var recordsBefore = Records();
    PasteAtCorner();
    Expect(Records() == recordsBefore && PasteFields().Count == 0, "настройка выключена (стандарт): ни записей, ни столбцов не заведено");

    ConstructorSettings.SetPasteGrowsTable(true);
    try
    {
        PasteAtCorner();
        var grown = PasteFields();
        Expect(Records() == recordsBefore + 1, $"досоздана одна запись: {recordsBefore} → {Records()}");
        Expect(grown.Select(fl => fl.Name).SequenceEqual(["Колонка 1", "Колонка 2"]) && grown.All(fl => fl.FieldType == "text"),
               $"досозданы текстовые «Колонка 1», «Колонка 2»: {string.Join(", ", grown.Select(fl => $"{fl.Name} ({fl.FieldType})"))}");
        var newRecord = repo3.GetRecords(cid3).Last().Id!.Value;
        var lastOld   = repo3.GetRecords(cid3)[^2].Id!.Value;
        Expect(grown.Count == 2 && repo3.GetFieldValues(lastOld).GetValueOrDefault(grown[0].Id!.Value) == "x2"
               && repo3.GetFieldValues(newRecord).GetValueOrDefault(grown[1].Id!.Value) == "y3",
               "значения легли в новые строку и столбцы");
        Expect(s.Columns.IndexOf(grown.Last().Id!.Value.ToString()) >= 0 && p.Rows!.Count - 1 == recordsBefore + 1,
               "таблица показывает новые столбцы и строку без перезахода");

        NoticeList().Clear();
        Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
        WaitUntil(() => Notices().Any(n => n.StartsWith("Вставка отменена", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));
        Settle2();
        Expect(Records() == recordsBefore && PasteFields().Count == 0, $"Ctrl+Z убрал досозданное: записей {Records()}, столбцов «Колонка» {PasteFields().Count}");
        Expect(s.Columns.Count > 0 && grown.All(fl => s.Columns.IndexOf(fl.Id!.Value.ToString()) < 0) && p.Rows!.Count - 1 == recordsBefore,
               "столбцы и строка ушли и из таблицы на экране");
    }
    finally { ConstructorSettings.SetPasteGrowsTable(false); }

    w.Close();


    Console.WriteLine();
    Console.WriteLine(failed == 0 ? "Все проверки прошли." : $"ПРОВАЛОВ: {failed}");
    return failed == 0 ? 0 : 3;
}

// ── Стенд правки (этап 4): набор, коммит и отмена на настоящем TableView ──────
//
// Копия базы, видимы поля всех
// правящихся типов. Пункты — раздел 4 чеклиста table-cells: правило второго клика, фокус и каретка
// как у нынешней таблицы, набор с паузами (редактор не закрывается, база не меняется до выхода),
// Enter / Tab / Shift+Tab / Escape / Alt+Enter, уход фокуса (пишет, не закрывая), клавиши таблицы в
// редакторе молчат, число в формате показа, «Без названия», списки (Enter в раскрытом), дата и
// связь с фокусом в таблице, галочка кликом и пробелом, ручная правка пути файла, закрытие правки
// обновлением записи из панели и подменой строк, прокрутка с открытым редактором, правый клик.
int RunEdit()
{
    // База правки: копия файла --db или, с --pg, база PostgreSQL на стенде sb-pg с коллекцией того
    // же вида (OpenPgLikeWide). Путь существующего файла — для ручной правки пути в «Файле».
    (DatabaseManager, string) OpenSqliteCopy()
    {
        var copy = Path.Combine(outDir, "edit-" + Path.GetFileName(path));
        File.Copy(path, copy, overwrite: true);
        return (DatabaseManager.Open(9404, "gridedit", copy), copy);
    }
    var onPg = args.Contains("--pg");
    var (mgr4Opened, pickedFile) = onPg ? OpenPgLikeWide(col.Name, outDir) : OpenSqliteCopy();
    using var mgr4 = mgr4Opened;
    Console.WriteLine(onPg ? "База правки: PostgreSQL (стенд sb-pg, база gridproto_edit)" : "База правки: копия файла --db");
    var repo4 = mgr4.Constructor;
    var cid4  = repo4.GetAllCollections().First(c => c.Name == col.Name).Id!.Value;

    var failed = 0;
    void Expect(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {what}");
        if (!ok) failed++;
    }

    // Видимы поля по одному на тип: текст, число, дата, список, статус, галочка, ссылка, файл, связь.
    var snap0  = repo4.ReadOpenSnapshot(cid4);
    DbField F(string type) => snap0.Fields.First(f => f.FieldType == type);
    var fText = F("text"); var fNum = F("number"); var fDate = F("date"); var fSel = F("select");
    var fStat = F("status"); var fCheck = F("checkbox"); var fUrl = F("url"); var fFile = F("file"); var fRel = F("relation");
    var fMulti = F("multiselect");
    var shownFields = new[] { fText, fNum, fDate, fSel, fStat, fMulti, fCheck, fUrl, fFile, fRel };
    repo4.SaveColumnDisplay(cid4, snap0.Fields.Select(f => (f.Id!.Value.ToString(), shownFields.Contains(f), 1)));

    // Рядом с таблицей — постороннее поле ввода: в него уводится фокус (как клик в панель записи).
    var outside = new TextBox { Width = 160 };
    var table   = new TableView();
    var dock    = new DockPanel();
    DockPanel.SetDock(outside, Dock.Left);
    dock.Children.Add(outside);
    dock.Children.Add(table);
    var w = new Window { Width = 2200, Height = 900, Content = dock };
    w.Show();
    Settle(w);

    IReadOnlyList<TableColumnDef> defs = [];
    var edited = new List<int>();
    table.ColumnsBuilt      += (_, d) => defs = d;
    table.RecordValueEdited += (_, rid) => edited.Add(rid);
    table.Load(repo4, cid4, repo4.ReadOpenSnapshot(cid4));
    Pump();
    var p = table.DrawnPanel!;
    var s = p.Surface;
    WaitUntil(s.VisibleCellsLoaded, TimeSpan.FromSeconds(20));
    var L = s.Columns;
    var sOrigin = s.TranslatePoint(new Point(0, 0), w)!.Value;

    int Col(string key) => defs.ToList().FindIndex(d => d.Key == key);
    string K(DbField f) => f.Id!.Value.ToString();
    var title = TableRowVm.TitleKey;
    Point Center(int row, string key)
    {
        var c = L.IndexOf(key);
        return new Point(sOrigin.X + L.Left(c) - s.OffsetX + L.Width(c) / 2,
                         sOrigin.Y + s.HeaderHeight + s.RowLayout.Top(row) - s.OffsetY + s.RowPitch / 2);
    }
    Point ToWindow(Point surfacePoint) => new(sOrigin.X + surfacePoint.X, sOrigin.Y + surfacePoint.Y);
    void Click(Point pt, RawInputModifiers m = RawInputModifiers.None, MouseButton button = MouseButton.Left)
    {
        w.MouseMove(pt, m); w.MouseDown(pt, button, m); w.MouseUp(pt, button, m); Pump();
    }
    void DoubleClick(Point pt)
    {
        w.MouseMove(pt); w.MouseDown(pt, MouseButton.Left); w.MouseUp(pt, MouseButton.Left);
        w.MouseDown(pt, MouseButton.Left); w.MouseUp(pt, MouseButton.Left); Pump();
    }
    void Key(Key k, RawInputModifiers m = RawInputModifiers.None) { w.KeyPress(k, m); w.KeyRelease(k, m); Pump(); }
    void Type(string text) { w.KeyTextInput(text); Pump(); }
    // Пауза посреди набора: таймеры и фоновые задачи отрабатывают, как у живого человека.
    void Pause(int ms) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Pump(); Thread.Sleep(10); } }
    // Второй клик по той же ячейке через паузу больше двойного — «медленный» второй клик.
    void OpenBySecondClick(int row, string key) { Click(Center(row, key)); Pause(600); Click(Center(row, key)); }
    TextBox? Box() => p.Editor.Editor as TextBox;
    string Db(int rid, DbField f) => repo4.GetFieldValues(rid).GetValueOrDefault(f.Id!.Value, "");
    CellRange? Range() => table.CurrentCellRange;
    TableRowVm Row(int i) => p.Rows!.TryGetCached(i)!;
    void Reload() { WaitUntil(() => p.QueryTask.IsCompleted && s.VisibleCellsLoaded(), TimeSpan.FromSeconds(20)); Pump(); }

    Console.WriteLine($"Колонки: {string.Join(", ", Enumerable.Range(0, L.Count).Select(i => L[i].Header))}");

    Console.WriteLine("Открытие правки:");
    var rid2 = Row(2).RecordId;
    var text2 = Db(rid2, fText);
    Click(Center(2, K(fText)));
    Expect(!table.IsCellEditing, "первый клик только выделяет");
    Click(Center(3, K(fText)));
    Click(Center(2, K(fText)));
    Expect(!table.IsCellEditing, "клик по ячейке, выделенной раньше другой, — снова только выделение");
    Pause(600);
    Click(Center(2, K(fText)));
    Expect(table.IsCellEditing && Box() is { } b0 && b0.IsFocused, "медленный второй клик по той же ячейке — правка, фокус в поле");
    Expect(Box() is { CaretIndex: 0, SelectionStart: 0, SelectionEnd: 0 }, "каретка в начале, без выделения — как у нынешней таблицы");
    Expect(Range() == CellRange.Single(2, Col(K(fText))) && s.CellSelectionBounds() is not null, "рамка выделения на правящейся ячейке");
    // Длинное значение поле раскрывает сразу (см. «Раскрытие ячейки при вводе» ниже) — от той же точки.
    Expect(p.Editor.EditorBounds is { } eb && s.EditorRect(2, K(fText)) is { } er && eb.Position == er.Position
           && (eb == er || eb.Width > er.Width), "редактор стоит в области ячейки (или раскрыт от нее под длинный текст)");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && Db(rid2, fText) == text2 && (s.IsFocused || s.IsKeyboardFocusWithin), "Escape без набора — правка закрыта, фокус в таблице");
    DoubleClick(Center(4, K(fText)));
    Expect(table.IsCellEditing && p.Editor.Row == 4, "двойной клик по новой ячейке — правка");
    Key(Avalonia.Input.Key.Escape);
    Key(Avalonia.Input.Key.Enter);
    Expect(table.IsCellEditing && p.Editor.Row == 4, "Enter на выделенной ячейке — правка");
    Key(Avalonia.Input.Key.Escape);
    Key(Avalonia.Input.Key.F2);
    Expect(table.IsCellEditing, "F2 — правка");
    Key(Avalonia.Input.Key.Escape);

    // Раскрытие поля под длинный текст (настройка «Раскрывать ячейку при вводе», стандартно
    // включена): вправо целыми колонками до края окна, дальше перенос строк и рост вниз; первая
    // строка не сдвигается. Выключено — поле в границах ячейки.
    Console.WriteLine("Раскрытие ячейки при вводе:");
    {
        var key5 = K(fText);
        var cell = s.EditorRect(5, key5)!.Value;
        OpenBySecondClick(5, key5);
        Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
        Type("кот");
        Expect(p.Editor.EditorBounds == cell, "короткий текст — поле в границах ячейки");
        var caretTop = Box()!.TranslatePoint(new Point(0, 0), s)!.Value.Y;
        Type(" — длинная строка, которая в ширину ячейки никак не помещается");
        Pump();
        var wide = p.Editor.EditorBounds;
        var cols = Enumerable.Range(0, L.Count).Select(i => L.Right(i) - s.OffsetX).ToList();
        Expect(wide is { } wb && wb.X == cell.X && wb.Y == cell.Y && wb.Width > cell.Width && wb.Height == cell.Height,
               $"длинный текст — поле тянется вправо в одну строку: {cell.Width:N0} → {wide?.Width:N0}");
        Expect(wide is { } wr && cols.Any(r => Math.Abs(r - (wr.Right + s.LineWidth)) < 0.6), "правый край поля — на границе колонки");
        Expect(Box()!.TextWrapping == Avalonia.Media.TextWrapping.NoWrap, "в одну строку — без переноса");
        Type(string.Concat(Enumerable.Repeat(" и еще немного слов, чтобы не поместилось в окно", 40)));
        Pump();
        var tall = p.Editor.EditorBounds;
        Expect(tall is { } tb && tb.Height > cell.Height && tb.Right <= s.ViewportWidth + 0.5 && tb.X == cell.X,
               $"текст шире окна — перенос строк и рост вниз: {tall?.Width:N0} × {tall?.Height:N0}, окно {s.ViewportWidth:N0}");
        Expect(Box()!.TextWrapping == Avalonia.Media.TextWrapping.Wrap, "перенос строк включен");
        Expect(Math.Abs(Box()!.TranslatePoint(new Point(0, 0), s)!.Value.Y + Box()!.Padding.Top - caretTop - Box()!.Padding.Top) < 40,
               "поле начинается у той же строки, что и ячейка");
        Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
        Type("снова коротко");
        Pump();
        Expect(p.Editor.EditorBounds == cell && Box()!.TextWrapping == Avalonia.Media.TextWrapping.NoWrap,
               "текст снова помещается — поле вернулось в ячейку");
        Key(Avalonia.Input.Key.Escape);
        Expect(!table.IsCellEditing && p.Editor.EditorBounds is null, "Escape — правка закрыта");

        ConstructorSettings.SetExpandCellEditor(false);
        try
        {
            OpenBySecondClick(5, key5);
            Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
            Type("длинная строка, которая в ширину ячейки никак не помещается, и еще слова");
            Pump();
            Expect(p.Editor.EditorBounds == cell, "настройка выключена — поле в границах ячейки");
            Key(Avalonia.Input.Key.Escape);
        }
        finally { ConstructorSettings.SetExpandCellEditor(true); }

        // Число не раскрывается: оно прижато вправо, и раскрытое поле увело бы его от показа.
        var numCell = s.EditorRect(5, K(fNum))!.Value;
        OpenBySecondClick(5, K(fNum));
        Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
        Type("12345678901234567890123456789012345");
        Pump();
        Expect(p.Editor.EditorBounds == numCell, "число — поле в границах ячейки");
        Key(Avalonia.Input.Key.Escape);
    }

    Console.WriteLine("Набор с паузами и запись при выходе:");
    OpenBySecondClick(2, K(fText));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);   // выделить весь текст поля, а не таблицу
    Expect(table.CurrentCellRange == CellRange.Single(2, Col(K(fText))), "Ctrl+A в редакторе выделяет текст, а не таблицу");
    var phrase = new[] { "Первое", " слово.", " Второе", " слово." };
    var stayed = true; var untouched = true;
    foreach (var word in phrase)
    {
        Type(word);
        Pause(700);
        stayed    &= table.IsCellEditing;
        untouched &= Db(rid2, fText) == text2;
    }
    Expect(stayed, "редактор не закрылся ни на пробеле, ни на точке, ни на паузе");
    Expect(untouched, "пока печатают, в базу ничего не пишется");
    var edits = edited.Count;
    Key(Avalonia.Input.Key.Enter);
    Expect(Db(rid2, fText) == "Первое слово. Второе слово.", $"Enter: записано целиком «{Db(rid2, fText)}»");
    Expect(edited.Count == edits + 1 && edited[^1] == rid2, "одно событие правки записи — наверх, в панель");
    Expect(!table.IsCellEditing && Range() == CellRange.Single(2, Col(K(fText))), "рамка на той же ячейке, а не ниже");
    Expect(s.IsFocused || s.IsKeyboardFocusWithin, "фокус вернулся таблице");
    Expect(Row(2)[K(fText)] == "Первое слово. Второе слово.", "ячейка показывает записанное");
    Key(Avalonia.Input.Key.Delete);
    Expect(WaitUntil(() => Db(rid2, fText) == "", TimeSpan.FromSeconds(10)), "Delete сразу после — стирает значение этой ячейки");
    Reload();
    Key(Avalonia.Input.Key.Z, RawInputModifiers.Control);
    Expect(WaitUntil(() => Db(rid2, fText) == "Первое слово. Второе слово.", TimeSpan.FromSeconds(10)), "Ctrl+Z возвращает");
    Reload();

    Console.WriteLine("Escape, Tab, Alt+Enter, клавиши в редакторе:");
    OpenBySecondClick(2, K(fText));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type("не должно записаться");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && Db(rid2, fText) == "Первое слово. Второе слово.", "Escape после набора — значение прежнее");
    OpenBySecondClick(2, K(fText));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Key(Avalonia.Input.Key.Delete);
    Expect(table.IsCellEditing && Box()?.Text == "" && Db(rid2, fText) != "", "Delete в редакторе стирает текст поля, а не значения таблицы");
    Type("строка 1");
    Key(Avalonia.Input.Key.Enter, RawInputModifiers.Alt);
    Type("строка 2");
    Expect(table.IsCellEditing && Box()?.Text == "строка 1\nстрока 2", "Alt+Enter в тексте — перевод строки, правка открыта");
    Key(Avalonia.Input.Key.Tab);
    Expect(Db(rid2, fText) == "строка 1\nстрока 2", "Tab — значение записано");
    Expect(!table.IsCellEditing && Range() == CellRange.Single(2, Col(K(fNum))), "Tab — редактор закрыт, рамка на ячейке правее");
    OpenBySecondClick(2, K(fNum));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type("12,5");
    Key(Avalonia.Input.Key.Enter, RawInputModifiers.Alt);
    Expect(table.IsCellEditing && Box()?.Text == "12,5", "Alt+Enter в числе ничего не делает, правка открыта");
    Type("x");
    Expect(Box()?.Text == "12,5", "в число буква не вводится");
    Key(Avalonia.Input.Key.Tab, RawInputModifiers.Shift);
    Expect(Db(rid2, fNum) == "12,5" && Row(2)[K(fNum)] == "12,5", $"число записано: «{Db(rid2, fNum)}»");
    Expect(Range() == CellRange.Single(2, Col(K(fText))), "Shift+Tab — рамка левее");
    OpenBySecondClick(3, K(fUrl));
    Key(Avalonia.Input.Key.End);
    Key(Avalonia.Input.Key.Enter, RawInputModifiers.Alt);
    Expect(table.IsCellEditing && Box()?.Text?.EndsWith('\n') == true, "Alt+Enter в ссылке — перевод строки");
    Key(Avalonia.Input.Key.Escape);

    Console.WriteLine("Уход фокуса:");
    var rid5 = Row(5).RecordId;
    OpenBySecondClick(5, K(fText));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type("набрано до ухода");
    outside.Focus(); Pump();
    Expect(Db(rid5, fText) == "набрано до ухода", "фокус ушел из редактора — значение записано");
    Expect(table.IsCellEditing && p.Editor.IsOpen, "редактор остался открыт");
    Box()!.Focus(); Pump();
    Key(Avalonia.Input.Key.End);
    Type(", и после");
    Key(Avalonia.Input.Key.Enter);
    Expect(Db(rid5, fText) == "набрано до ухода, и после", "вернулся, дописал, Enter — записано целиком");
    OpenBySecondClick(5, K(fText));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type("клик мимо");
    Click(Center(6, K(fNum)));
    Expect(!table.IsCellEditing && Db(rid5, fText) == "клик мимо" && Range() == CellRange.Single(6, Col(K(fNum))), "клик в другую ячейку — записано, выделена она");
    Key(Avalonia.Input.Key.Delete);
    Expect(!table.IsCellEditing, "одиночный клик после правки не открыл редактор: Delete — по значению");
    Reload();

    Console.WriteLine("«Без названия»:");
    var rid7 = Row(7).RecordId;
    OpenBySecondClick(7, title);
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Key(Avalonia.Input.Key.Delete);
    Key(Avalonia.Input.Key.Enter);
    Expect(repo4.GetRecord(rid7)?.Title == TitleText.Placeholder, $"стертое название — «{TitleText.Placeholder}»");
    OpenBySecondClick(7, title);
    Expect(Box() is { Text: "" } tb7 && tb7.Watermark as string == TitleText.Placeholder, "в правке пустое поле с серой подсказкой");
    Type("Новое название");
    Key(Avalonia.Input.Key.Enter);
    Expect(repo4.GetRecord(rid7)?.Title == "Новое название", "набрано и записано");

    Console.WriteLine("Списки, дата, связь:");
    var rid8  = Row(8).RecordId;
    var stat8 = Db(rid8, fStat);
    OpenBySecondClick(8, K(fStat));
    var combo = p.Editor.Editor as ComboBox;
    Expect(combo is { IsFocused: true }, "статус: редактор — выпадающий список, фокус в нем");
    if (combo is not null)
    {
        // Одно правило для всех списков: выбор пишется при записи правки, Escape уходит без записи,
        // уход фокуса пишет, не закрывая, — как у текста.
        var others = combo.Items.Cast<object>().Select(o => o.ToString()!).Where(o => o != stat8).ToList();
        combo.IsDropDownOpen = true; Pump();
        combo.SelectedItem = others[0]; Pump();
        Expect(Db(rid8, fStat) == stat8, "выбор не пишется до записи правки");
        Key(Avalonia.Input.Key.Enter);
        Expect(!table.IsCellEditing && Db(rid8, fStat) == others[0], "Enter в раскрытом списке записал пункт и закрыл правку");
        Key(Avalonia.Input.Key.Enter);
        combo = p.Editor.Editor as ComboBox;
        combo!.SelectedItem = others[^1]; Pump();
        Key(Avalonia.Input.Key.Escape);
        Expect(!table.IsCellEditing && Db(rid8, fStat) == others[0], "Escape после выбора — без записи, значение прежнее");
        Key(Avalonia.Input.Key.Enter);
        combo = p.Editor.Editor as ComboBox;
        combo!.SelectedItem = others[^1]; Pump();
        outside.Focus(); Pump();
        Expect(table.IsCellEditing && Db(rid8, fStat) == others[^1], "фокус ушел из списка (клик в панель) — выбор записан, правка открыта");
        combo.Focus(); Pump();
        Key(Avalonia.Input.Key.Escape);
        Expect(!table.IsCellEditing && Db(rid8, fStat) == others[^1], "Escape после этого закрыл правку, записанное осталось");
    }
    OpenBySecondClick(8, K(fDate));
    Expect(p.Editor.Editor is DateEditor && (s.IsFocused || s.IsKeyboardFocusWithin), "дата: редактор даты, фокус остался в таблице");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing, "Escape с таблицы закрывает правку даты");
    OpenBySecondClick(8, K(fRel));
    Expect(p.Editor.Editor is RelationSelectBox, "связь: редактор связи");
    Key(Avalonia.Input.Key.Tab);
    Expect(!table.IsCellEditing && Range() != CellRange.Single(8, Col(K(fRel))), "Tab с таблицы закрывает правку связи и переходит");

    Console.WriteLine("Галочка и файл:");
    var rid9  = Row(9).RecordId;
    var check = Db(rid9, fCheck) is "1" or "true" or "True";
    var cell9 = s.CellRect(9, L.IndexOf(K(fCheck)));
    var box9  = s.CheckboxRect(cell9);
    Click(ToWindow(box9.Center));
    Expect(WaitUntil(() => (Db(rid9, fCheck) is "1" or "true" or "True") != check, TimeSpan.FromSeconds(5)), "клик по квадрату переключает галочку");
    Click(ToWindow(new Point(cell9.Right - 6, cell9.Center.Y)));
    Expect((Db(rid9, fCheck) is "1" or "true" or "True") != check && !table.IsCellEditing, "клик мимо квадрата — только выделение");
    Key(Avalonia.Input.Key.Space);
    Expect((Db(rid9, fCheck) is "1" or "true" or "True") == check, "пробел на выделенной галочке переключает");
    Key(Avalonia.Input.Key.Enter);
    Expect(!table.IsCellEditing, "Enter на галочке правку не открывает");

    var rid10 = Row(10).RecordId;
    var missing = Path.Combine(outDir, "нет-такого-файла.txt");
    repo4.SetFieldValue(rid10, fFile.Id!.Value, missing);
    table.ReloadRows(); Reload();
    var cell10 = s.CellRect(10, L.IndexOf(K(fFile)));
    Click(ToWindow(new Point(cell10.X + 30, cell10.Center.Y)));
    Expect(WaitUntil(() => p.Editor.Editor is FileEditor, TimeSpan.FromSeconds(5)), "клик по имени отсутствующего файла — ручная правка пути");
    Expect(table.IsCellEditing && w.FocusManager?.GetFocusedElement() is TextBox, $"поле пути в фокусе ({w.FocusManager?.GetFocusedElement()?.GetType().Name})");
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type(pickedFile);
    Key(Avalonia.Input.Key.Enter);
    Expect(Db(rid10, fFile) == pickedFile && !table.IsCellEditing, "путь набран, Enter — записан, правка закрыта");
    Reload();
    DoubleClick(ToWindow(new Point(cell10.X + 30, cell10.Center.Y)));
    Expect(WaitUntil(() => p.Editor.Editor is FileEditor, TimeSpan.FromSeconds(5)), "двойной клик по имени — ручная правка пути");
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type("мусор");
    Key(Avalonia.Input.Key.Escape);
    Expect(Db(rid10, fFile) == pickedFile && !table.IsCellEditing, "Escape — путь прежний");
    Expect(s.FileButtonRect(cell10, defs.First(d => d.Key == K(fFile))) is { Width: > 0 }, "у ручного файла есть скрепка");

    // Авто-привязка к недоступной папке: клик по ячейке — плашка «Папка недоступна», скрепки нет.
    var fAuto = repo4.AddField(new DbField
    {
        CollectionId = cid4, Name = "Авто-файл", FieldType = "file", Position = 5000,
        ConfigJson   = new FileAutoRule(FileAutoRule.TitleSource, Path.Combine(outDir, "нет-такой-папки"), "pdf").ToConfigJson(),
    });
    var shownIds = shownFields.Select(f => f.Id).Append(fAuto.Id).ToHashSet();
    repo4.SaveColumnDisplay(cid4, repo4.ReadOpenSnapshot(cid4).Fields.Select(f => (f.Id!.Value.ToString(), shownIds.Contains(f.Id), 1)));
    table.ReloadColumns(); Pump();
    L = s.Columns;
    var cellAuto = s.CellRect(1, L.IndexOf(K(fAuto)));
    Expect(s.FileButtonRect(cellAuto, defs.First(d => d.Key == K(fAuto))) is null, "у авто-привязки скрепки нет");
    Click(ToWindow(new Point(cellAuto.X + 30, cellAuto.Center.Y)));
    Expect(WaitUntil(() => table.DrawnNoticeText == "Папка недоступна", TimeSpan.FromSeconds(5)), $"клик — плашка «{table.DrawnNoticeText}»");
    Expect(!table.IsCellEditing, "ручной правки у авто-привязки нет");

    Console.WriteLine("Синхронизация и подмена строк:");
    var rid11 = Row(11).RecordId;
    OpenBySecondClick(11, K(fDate));
    table.RefreshRecordRow(Row(12).RecordId); Pump();
    Expect(table.IsCellEditing, "обновление ДРУГОЙ записи из панели правку не закрывает");
    table.RefreshRecordRow(rid11); Pump();
    Expect(!table.IsCellEditing, "обновление ЭТОЙ записи из панели закрывает правку (отмена)");
    OpenBySecondClick(11, K(fText));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type("до перезагрузки");
    table.ReloadRows(); Reload();
    Expect(Db(rid11, fText) == "до перезагрузки" && !table.IsCellEditing, "подмена строк — набранное записано, редактор закрыт");
    OpenBySecondClick(11, K(fText));
    Key(Avalonia.Input.Key.A, RawInputModifiers.Control);
    Type("перед закрытием окна");
    table.CommitCellEdit(); Pump();
    Expect(Db(rid11, fText) == "перед закрытием окна" && !table.IsCellEditing, "закрытие окна — правка записана");

    Console.WriteLine("Прокрутка, цвет, линия ячейки, правый клик:");
    OpenBySecondClick(11, K(fText));
    s.OffsetY += s.RowPitch; Pump();
    Expect(p.Editor.EditorBounds is { } moved && s.EditorRect(11, K(fText)) is { } want && moved == want, "прокрутка — редактор едет вместе с ячейкой");
    var before = s.OffsetY;
    w.MouseWheel(Center(11, K(fText)), new Vector(0, -1), RawInputModifiers.None); Pump();
    Expect(s.OffsetY > before && table.IsCellEditing, "колесо над редактором крутит таблицу, правка открыта");
    Key(Avalonia.Input.Key.Escape);
    s.OffsetY = 0; Pump();

    // Окрашенная ячейка: поверхность под редактором цвет не рисует, заливку на всю ячейку кладет
    // держатель правки (EditorHost ← GridSurface.EditorFill), а редактор прозрачный — иначе
    // полупрозрачная заливка легла бы дважды, а у редактора в полосе у края (текст сверху или снизу)
    // цвет был бы не во всю ячейку.
    var rid13 = Row(13).RecordId;
    repo4.BulkSetCellStyle(cid4, [(rid13, K(fText))], StyleAspect.Background, "#FF3B30", CancellationToken.None);
    table.ReloadRows(); Reload();
    OpenBySecondClick(13, K(fText));
    var red = ((Avalonia.Media.ISolidColorBrush)SmartBase.Utils.ColorTools.Fill("#FF3B30")!).Color;
    Expect(Box()?.Parent is Border holder && holder.Background is Avalonia.Media.ISolidColorBrush fill && fill.Color == red
           && Box()?.Background is not Avalonia.Media.ISolidColorBrush { Color.A: > 0 },
           "редактор окрашенной ячейки — на ее заливке, сам прозрачный");
    Key(Avalonia.Input.Key.Escape);
    OpenBySecondClick(11, K(fText));
    var line = s.CellRect(11, L.IndexOf(K(fText)));
    Click(ToWindow(new Point(line.Right - s.LineWidth / 2, line.Center.Y)));
    Expect(table.IsCellEditing, "клик по линии правящейся ячейки правку не закрывает");
    Click(Center(12, K(fNum)), button: MouseButton.Right);
    Expect(!table.IsCellEditing && table.LastOpenedMenu is not null && Range() == CellRange.Single(12, Col(K(fNum))),
           "правый клик по другой ячейке — правка закрыта, меню открыто");
    table.LastOpenedMenu?.Close(); Pump();

    // Правка набором, как в электронных таблицах (TableView.CellTyping.cs): ячейка выделена — набор
    // открывает правку с набранным вместо прежнего значения, Enter только записывает.
    Console.WriteLine("Набор на выделенной ячейке:");
    // Посимвольно и без пауз между символами: первый открывает правку, следующие идут уже в
    // редактор — как быстрый набор живого человека.
    void TypeChars(string text) { foreach (var ch in text) w.KeyTextInput(ch.ToString()); Pump(); }
    TextBox? Focused() => w.FocusManager?.GetFocusedElement() as TextBox;
    bool GridFocused() => s.IsFocused || s.IsKeyboardFocusWithin;
    Point HeaderOf(string key) => new(sOrigin.X + L.Left(L.IndexOf(key)) - s.OffsetX + 20, sOrigin.Y + s.HeaderHeight / 2);

    var rid14 = Row(14).RecordId;
    var rid15 = Row(15).RecordId;
    var rid16 = Row(16).RecordId;
    Click(Center(14, K(fText)));
    var before14 = Db(rid14, fText);
    TypeChars("Н");
    Expect(table.IsCellEditing && Box() is { Text: "Н", CaretIndex: 1, SelectionStart: 1, SelectionEnd: 1, IsFocused: true },
           $"буква на выделенной ячейке — правка с ней вместо прежнего значения, каретка после («{Box()?.Text}»)");
    Expect(table.IsEnterMode && Range() == CellRange.Single(14, Col(K(fText))) && Db(rid14, fText) == before14,
           "режим «ввод», рамка на месте, в базе прежнее");
    TypeChars("абор слов");
    Expect(Box()?.Text == "Набор слов", $"следующие буквы — в редактор: «{Box()?.Text}»");
    edits = edited.Count;
    Key(Avalonia.Input.Key.Enter);
    Expect(Db(rid14, fText) == "Набор слов" && edited.Count == edits + 1, "Enter записал набранное, одно событие правки");
    Expect(!table.IsCellEditing && Range() == CellRange.Single(14, Col(K(fText))) && GridFocused(),
           "Enter только записывает: рамка на той же ячейке, фокус в таблице");
    TypeChars("стереть");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && Db(rid14, fText) == "Набор слов" && Row(14)[K(fText)] == "Набор слов",
           "Escape — прежнее значение в базе и в ячейке");
    TypeChars("быстро");
    Expect(Box()?.Text == "быстро", $"быстрый набор не теряет и не удваивает буквы: «{Box()?.Text}»");
    Key(Avalonia.Input.Key.Escape);
    Type(((char)127).ToString());
    Expect(!table.IsCellEditing, "символ DEL (так приходит Ctrl+Backspace) правку не открывает");
    Type(" ");
    Expect(table.IsCellEditing && Box()?.Text == " ", "пробел на текстовой ячейке — правка с пробелом");
    Key(Avalonia.Input.Key.Escape);

    Console.WriteLine("Стрелки и F2 в правке набором:");
    TypeChars("вправо");
    Key(Avalonia.Input.Key.Right);
    Expect(Db(rid14, fText) == "вправо" && !table.IsCellEditing && Range() == CellRange.Single(14, Col(K(fNum))),
           "«ввод»: стрелка вправо записала и перевела рамку правее");
    Key(Avalonia.Input.Key.Left);
    TypeChars("вниз");
    Key(Avalonia.Input.Key.Down);
    Expect(Db(rid14, fText) == "вниз" && Range() == CellRange.Single(15, Col(K(fText))) && GridFocused(),
           "стрелка вниз — записано, рамка ниже, фокус в таблице");
    TypeChars("ш");
    Key(Avalonia.Input.Key.Right, RawInputModifiers.Shift);
    Expect(Db(rid15, fText) == "ш" && Range() == new CellRange(15, Col(K(fText)), 15, Col(K(fNum))),
           "Shift+стрелка — записано, выделение растянуто");
    Click(Center(16, K(fText)));
    TypeChars("ab");
    Key(Avalonia.Input.Key.F2);
    Expect(table.IsCellEditing && !table.IsEnterMode, "F2 — режим «правка»");
    Key(Avalonia.Input.Key.Left);
    TypeChars("X");
    Expect(table.IsCellEditing && Box()?.Text == "aXb", $"в «правке» стрелка ходит по тексту: «{Box()?.Text}»");
    Key(Avalonia.Input.Key.F2);
    Key(Avalonia.Input.Key.Up);
    Expect(Db(rid16, fText) == "aXb" && Range() == CellRange.Single(15, Col(K(fText))),
           "второй F2 — снова «ввод»: стрелка вверх записала и перешла");
    Key(Avalonia.Input.Key.Down);
    Key(Avalonia.Input.Key.Enter);
    Expect(table.IsCellEditing && !table.IsEnterMode, "Enter на выделенной ячейке открывает правку сразу в «правке»");
    Key(Avalonia.Input.Key.Right);
    Expect(table.IsCellEditing && Range() == CellRange.Single(16, Col(K(fText))), "в ней стрелка ходит по тексту, правка открыта");
    Key(Avalonia.Input.Key.Escape);
    TypeChars("клик");
    Click(Center(16, K(fText)));
    Expect(table.IsCellEditing && !table.IsEnterMode, "клик в поле правки, начатой набором, — режим «правка»");
    Key(Avalonia.Input.Key.Left);
    Expect(table.IsCellEditing, "и стрелка уже ходит по тексту");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && Db(rid16, fText) == "aXb", "Escape — прежнее значение");

    Console.WriteLine("Число, «Название», выделение прямоугольником:");
    Click(Center(14, K(fNum)));
    var num14 = Db(rid14, fNum);
    TypeChars("x");
    Expect(!table.IsCellEditing && Db(rid14, fNum) == num14, "буква на числе правку не открывает");
    TypeChars("-7,5");
    Expect(table.IsCellEditing && Box()?.Text == "-7,5", $"цифры и знаки числа — правка: «{Box()?.Text}»");
    TypeChars("q");
    Expect(Box()?.Text == "-7,5", "буква в число и дальше не вводится");
    Key(Avalonia.Input.Key.Enter);
    Expect(Db(rid14, fNum) == "-7,5" && !table.IsCellEditing, $"число записано: «{Db(rid14, fNum)}»");
    var rid17 = Row(17).RecordId;
    Click(Center(17, title));
    TypeChars("Заголовок набором");
    Key(Avalonia.Input.Key.Enter);
    Expect(repo4.GetRecord(rid17)?.Title == "Заголовок набором", "«Название» набором — записано");
    var rid18 = Row(18).RecordId;
    Click(Center(18, K(fText)));
    Click(Center(19, K(fNum)), RawInputModifiers.Shift);
    var rect = Range();
    TypeChars("в угол");
    Expect(table.IsCellEditing && p.Editor.Row == 18 && p.Editor.Key == K(fText) && Range() == rect && rect is { IsSingleCell: false },
           "выделен прямоугольник — набор в ячейку, с которой начато выделение, прямоугольник остается");
    Key(Avalonia.Input.Key.Enter);
    Expect(Db(rid18, fText) == "в угол" && Range() == rect, "Enter записал, прямоугольник на месте");

    Console.WriteLine("Список набором:");
    var opts = defs.First(d => d.Key == K(fStat)).Options.ToList();
    string? Pick(params string?[] except) => opts.FirstOrDefault(o => o.Length > 0 && !except.Contains(o)
                                                                 && CellTyping.IndexOf(opts, o[..1]) == opts.IndexOf(o));
    var st0 = Db(rid14, fStat);
    var stA = Pick(st0);
    var stB = Pick(st0, stA);
    if (stA is null || stB is null) Expect(false, $"у статуса мало пунктов для проверки: {string.Join(", ", opts)}");
    else
    {
        Click(Center(14, K(fStat)));
        TypeChars(stA[..1].ToLowerInvariant());
        var cb = p.Editor.Editor as ComboBox;
        Expect(table.IsCellEditing && cb is { IsDropDownOpen: true } && cb.SelectedItem as string == stA,
               $"буква на статусе — список раскрыт на «{cb?.SelectedItem}» (ждали «{stA}»)");
        Expect(Db(rid14, fStat) == st0, "пока список открыт набором, в базу ничего не пишется");
        Key(Avalonia.Input.Key.Enter);
        Expect(!table.IsCellEditing && Db(rid14, fStat) == stA && Range() == CellRange.Single(14, Col(K(fStat))) && GridFocused(),
               "Enter записал пункт одним нажатием, рамка на той же ячейке");
        TypeChars(stB[..1]);
        Expect((p.Editor.Editor as ComboBox)?.SelectedItem as string == stB, $"заглавная буква тоже находит «{stB}»");
        Key(Avalonia.Input.Key.Escape);
        Expect(!table.IsCellEditing && Db(rid14, fStat) == stA && Row(14)[K(fStat)] == stA, "Escape — одним нажатием и без записи");
        var ia   = opts.IndexOf(stA);
        var next = ia + 1 < opts.Count ? opts[ia + 1] : stA;
        TypeChars(stA[..1]);
        Key(Avalonia.Input.Key.Down);
        Key(Avalonia.Input.Key.Enter);
        Expect(Db(rid14, fStat) == next, $"стрелка вниз и Enter — пункт под фокусом: «{Db(rid14, fStat)}» (ждали «{next}»)");
        TypeChars(stB[..1]);
        Key(Avalonia.Input.Key.Tab);
        Expect(Db(rid14, fStat) == stB && Range() is { AnchorRow: 14 } afterTab && afterTab.AnchorColumn != Col(K(fStat)),
               "Tab записал пункт и перевел рамку правее");
        Key(Avalonia.Input.Key.Tab, RawInputModifiers.Shift);
        if (CellTyping.IndexOf(opts, "щ") < 0)
        {
            TypeChars("щ");
            Expect(table.IsCellEditing && (p.Editor.Editor as ComboBox)?.SelectedItem as string == stB, "буква без пункта — список открыт, выбор прежний");
            Key(Avalonia.Input.Key.Enter);
            Expect(!table.IsCellEditing && Db(rid14, fStat) == stB, "Enter без смены выбора значение не трогает");
        }
        Key(Avalonia.Input.Key.Enter);
        var cb2 = p.Editor.Editor as ComboBox;
        Expect(cb2 is { IsDropDownOpen: false, IsFocused: true } && !table.IsEnterMode, "Enter открыл список: свернут, фокус в нем");
        var stBefore = Db(rid14, fStat);
        TypeChars(stA[..1]);
        Expect(cb2?.SelectedItem as string == stA && Db(rid14, fStat) == stBefore,
               "буква в списке, открытом Enter, выбирает пункт, а пишется он при записи правки");
        Key(Avalonia.Input.Key.Enter);
        Expect(!table.IsCellEditing && Db(rid14, fStat) == stA, "Enter записал пункт и закрыл правку");
    }
    var selOpts = defs.First(d => d.Key == K(fSel)).Options.Where(o => o.Length > 0).ToList();
    if (selOpts.Count > 0)
    {
        var sel15 = Db(rid15, fSel);
        Click(Center(15, K(fSel)));
        TypeChars(selOpts[^1][..1]);
        var wantSel = selOpts[CellTyping.IndexOf(selOpts, selOpts[^1][..1])];
        Expect(p.Editor.Editor is ComboBox { IsDropDownOpen: true } sc && sc.SelectedItem as string == wantSel,
               $"список (не статус) тоже открывается набором на «{wantSel}»");
        Key(Avalonia.Input.Key.Escape);
        Expect(!table.IsCellEditing && Db(rid15, fSel) == sel15, "и Escape его отменяет");
    }

    Console.WriteLine("Мультисписок и связь набором:");
    var multiOpts = defs.First(d => d.Key == K(fMulti)).Options.Where(o => o.Length > 1).ToList();
    var multi16   = Db(rid16, fMulti);
    Click(Center(16, K(fMulti)));
    var multiTyped = multiOpts.Count > 0 ? multiOpts[0][..2] : "ди";
    TypeChars(multiTyped);
    Expect(table.IsCellEditing && p.Editor.Editor is MultiSelectBox
           && Focused() is { Watermark: "Поиск значений..." } ms && ms.Text == multiTyped && ms.CaretIndex == multiTyped.Length,
           $"буквы на мультисписке — чеклист раскрыт, набранное в поиске («{Focused()?.Text}»)");
    Expect(Db(rid16, fMulti) == multi16, "значение мультисписка не тронуто");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && Db(rid16, fMulti) == multi16 && GridFocused(), "Escape закрыл чеклист и правку одним нажатием");
    Click(Center(16, K(fRel)));
    TypeChars("За");
    Expect(table.IsCellEditing && p.Editor.Editor is RelationSelectBox && Focused() is { Watermark: "Поиск по названию…", Text: "За" },
           $"буквы на связи — выбор записей раскрыт, набранное в поиске («{Focused()?.Text}»)");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && GridFocused(), "Escape закрыл выбор связи");
    Key(Avalonia.Input.Key.Enter);
    Expect(p.Editor.Editor is RelationSelectBox && GridFocused(), "Enter на связи — редактор, фокус в таблице");
    TypeChars("З");
    Expect(Focused() is { Watermark: "Поиск по названию…", Text: "З" }, "символ в нем — выбор записей с набранным в поиске");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing, "Escape закрыл правку");

    Console.WriteLine("Дата набором:");
    var rid20 = Row(20).RecordId;
    Click(Center(20, K(fDate)));
    var date20 = Db(rid20, fDate);
    TypeChars("x");
    Expect(!table.IsCellEditing, "буква на дате правку не открывает");
    TypeChars("1");
    Expect(table.IsCellEditing && p.Editor.Editor is DateEditor && Focused() is { Text: "1", CaretIndex: 1 } && table.IsEnterMode,
           $"цифра на дате — ручной ввод с ней, каретка после («{Focused()?.Text}»)");
    TypeChars("2.03.2026");
    Expect(Focused()?.Text == "12.03.2026" && Db(rid20, fDate) == date20, "дата набирается в поле, в базе прежнее");
    Key(Avalonia.Input.Key.Enter);
    Expect(!table.IsCellEditing && Db(rid20, fDate) == "2026-03-12" && Range() == CellRange.Single(20, Col(K(fDate))) && GridFocused(),
           $"Enter записал дату: «{Db(rid20, fDate)}»");
    TypeChars("5");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && Db(rid20, fDate) == "2026-03-12", "Escape после набора даты — без записи");
    TypeChars("01.01.2027");
    Key(Avalonia.Input.Key.Left);
    Expect(Db(rid20, fDate) == "2027-01-01" && Range() is { AnchorRow: 20 } r20 && r20.AnchorColumn != Col(K(fDate)),
           "«ввод» в дате: стрелка записала и перешла");
    Key(Avalonia.Input.Key.Right);
    Key(Avalonia.Input.Key.Enter);
    Expect(table.IsCellEditing && p.Editor.Editor is DateEditor && GridFocused() && !table.IsEnterMode, "Enter на дате — редактор, фокус в таблице");
    TypeChars("3");
    Expect(Focused() is { Text: "3" }, "цифра в нем начинает ручной ввод");
    TypeChars("0.04.2026");
    Key(Avalonia.Input.Key.Escape);
    Expect(!table.IsCellEditing && Db(rid20, fDate) == "2027-01-01", "Escape — дата прежняя (раньше уход фокуса записывал набранное)");
    Key(Avalonia.Input.Key.Enter);
    TypeChars("30.04.2026");
    Key(Avalonia.Input.Key.Enter);
    Expect(!table.IsCellEditing && Db(rid20, fDate) == "2026-04-30", "ручной ввод в дате, открытой Enter, — Enter записал");

    Console.WriteLine("Типы, которые набором не правятся:");
    var rid19   = Row(19).RecordId;
    var check19 = Db(rid19, fCheck);
    var cell19  = s.CellRect(19, L.IndexOf(K(fCheck)));
    Click(ToWindow(new Point(cell19.Right - 6, cell19.Center.Y)));
    TypeChars("a");
    Type(" ");
    Expect(!table.IsCellEditing && Db(rid19, fCheck) == check19, "галочка: буква и пробел текстом правку не открывают");
    Click(HeaderOf(K(fText)));
    TypeChars("a");
    Expect(!table.IsCellEditing && table.SelectedColumnKeys.Count > 0 && Range() is null, "выделена колонка — набор правку не открывает");
    Click(Center(14, K(fText)));

    w.Close();
    Console.WriteLine();
    Console.WriteLine(failed == 0 ? "Все проверки прошли." : $"ПРОВАЛОВ: {failed}");
    return failed == 0 ? 0 : 3;
}

// Стенд правки на PostgreSQL (--edit --pg): отдельная база gridproto_edit на стенде sb-pg (порт
// 5455, логин postgres — как у Tests/Integration/PgTestServer.cs). База заводится при первом
// прогоне, схема сбрасывается каждый раз. Коллекция того же вида, что «Широкая» у wide20
// (Tests/DbGenerator/WideProfile.cs): поля всех типов, те же пункты списка и статуса, связь на саму
// коллекцию, 60 записей со значениями. Строится через API программы: генератор пишет прямо в SQLite.
static (DatabaseManager Manager, string PickedFile) OpenPgLikeWide(string collectionName, string outDir)
{
    const string server = "Host=localhost;Port=5455;Username=postgres;Password=postgres";
    const string dbName = "gridproto_edit";
    using (var admin = new Npgsql.NpgsqlConnection(server + ";Database=postgres"))
    {
        admin.Open();
        using var exists = admin.CreateCommand();
        exists.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{dbName}'";
        if (exists.ExecuteScalar() is null)
        {
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE {dbName}";
            create.ExecuteNonQuery();
        }
    }
    var cs = $"{server};Database={dbName}";
    using (var conn = new Npgsql.NpgsqlConnection(cs))
    {
        conn.Open();
        using var reset = conn.CreateCommand();
        reset.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        reset.ExecuteNonQuery();
    }
    Npgsql.NpgsqlConnection.ClearAllPools();

    var manager = DatabaseManager.Open(9404, "gridedit-pg", cs, provider: "postgres");
    var repo    = manager.Constructor;
    var colId   = repo.AddCollection(new DbCollection { Name = collectionName }).Id!.Value;
    var pos     = 1000;
    DbField F(string name, string type, string config = "{}", bool calStart = false) =>
        repo.AddField(new DbField { CollectionId = colId, Name = name, FieldType = type, ConfigJson = config,
                                    Position = pos += 10, IsCalStart = calStart });
    static string Options(string[] options) => System.Text.Json.JsonSerializer.Serialize(new { options });

    string[] select = ["Новая", "В работе", "Готово", "Отложена"];
    string[] status = ["К выполнению", "В работе", "Готово"];
    var fText  = F("Текст",  "text");
    var fNum   = F("Число",  "number");
    var fDate  = F("Дата",   "date", calStart: true);
    var fSel   = F("Список", "select", Options(select));
    var fStat  = F("Статус", "status", Options(status));
    F("Мультисписок", "multiselect", Options(["Дизайн", "Backend", "Frontend", "Тесты", "Документы"]));
    var fCheck = F("Чекбокс", "checkbox");
    var fUrl   = F("Ссылка",  "url");
    F("Файл",         "file");
    F("Пользователь", "user");
    F("Связь",        "relation", new RelationConfig(colId, Single: true).ToConfigJson());
    F("Создано", "created_at"); F("Изменено", "updated_at"); F("Автор", "created_by"); F("Правил", "updated_by");

    for (var i = 1; i <= 60; i++)
    {
        var rid = repo.AddRecord(new DbRecord { CollectionId = colId, Title = $"Запись {i}", Position = i * 1000 }).Id!.Value;
        repo.SetFieldValue(rid, fText.Id!.Value,  $"Текст записи {i}");
        repo.SetFieldValue(rid, fNum.Id!.Value,   (i * 7).ToString());
        repo.SetFieldValue(rid, fDate.Id!.Value,  new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd"));
        repo.SetFieldValue(rid, fSel.Id!.Value,   select[i % select.Length]);
        repo.SetFieldValue(rid, fStat.Id!.Value,  status[i % status.Length]);
        repo.SetFieldValue(rid, fCheck.Id!.Value, i % 2 == 0 ? "1" : "0");
        repo.SetFieldValue(rid, fUrl.Id!.Value,   $"https://example.com/{i}");
    }

    var picked = Path.Combine(outDir, "edit-pg-picked.txt");
    File.WriteAllText(picked, "существующий файл для ручной правки пути");
    return (manager, picked);
}

// ── Стенд снимков: сетка против эталонных кадров ─────────────────────────────
//
// Эталоны — кадры сетки, принятые за верные (первые из них сняты с сетки, попиксельно сведенной с
// таблицей на DataGrid перед ее удалением на этапе 6). Таблица открывает коллекцию в окне 1600×900
// при масштабе окна 1 и 2 (RenderScaling выставляется рефлексией, как в Tests/EditGeometry),
// дожидается данных и снимает кадр; сцены выделения ставятся состоянием. Кадр сравнивается с
// эталоном попиксельно: строго и с допуском на сглаживание; различия раскладываются по строкам и
// колонкам, чтобы было видно, ЧТО именно не совпало. Сохраняются кадр и карта различий (эталон,
// выбеленный наполовину, различия — красным). Эталоны лежат в папке --ref (по умолчанию — --out);
// нет эталона — кадр сохраняется как эталон, следующий прогон сравнивает с ним. Оба кадра читаются
// из PNG одним декодером — порядок каналов у них один. Критерий: различий нет.
int RunSnapshots()
{
    // Ответы «есть ли файл» — синхронно: иначе кадр зависел бы от того, успела ли фоновая проверка.
    FileProbe.Synchronous = true;
    var refDir  = Arg("--ref") ?? outDir;
    Directory.CreateDirectory(refDir);
    var worst   = 0;
    var created = 0;
    foreach (var scale in new[] { 1.0, 2.0 })
    {
        Console.WriteLine($"Масштаб {scale:N0}:");
        var (wNew, panel, newMs) = OpenNew(scale);
        var surface = panel.Surface;
        var columns = surface.Columns;
        var headH   = surface.HeaderHeight;
        var pitch   = surface.RowPitch;
        var offsetX = surface.OffsetX;
        Console.WriteLine($"  кадр с данными {newMs:N0} мс");
        Console.WriteLine($"  колонки: {string.Join(", ", Enumerable.Range(0, columns.Count).Select(i => $"{columns[i].Header}={columns.Width(i):N2}"))}");

        // Сцены выделения (этап 3) — состоянием; порядок таков, что каждая сцена сама снимает
        // предыдущее выделение: колонка → запись → ячейка → прямоугольник. Указатель уводится в угол
        // «№» шапки — там наведение не красится.
        var data = Enumerable.Range(0, columns.Count).Where(i => columns[i].IsStarWidth).ToList();
        var cA   = data[1];
        var cB   = data[2];
        var rid  = panel.Rows![3].RecordId;
        var scenes = new (string Name, Action Apply)[]
        {
            ("base",   () => { }),
            ("column", () => panel.SetColumnSelection([columns[cA].Key])),
            ("row",    () => { panel.SetColumnSelection([]); panel.SetRecordSelection([rid]); }),
            ("cell",   () => { panel.ClearRecordSelection(); panel.SetCellSelection(3, 3, columns[cA].Key, columns[cA].Key, tinted: false); }),
            ("range",  () => panel.SetCellSelection(2, 4, columns[cA].Key, columns[cB].Key, tinted: true)),
        };

        foreach (var (name, apply) in scenes)
        {
            apply();
            wNew.MouseMove(new Point(10, 10));
            for (var i = 0; i < 6; i++) { Pump(); wNew.CaptureRenderedFrame(); Thread.Sleep(5); }
            var captured = wNew.CaptureRenderedFrame();
            var suffix   = name == "base" ? "" : $"-{name}";
            if (captured is null) { Console.WriteLine($"  сцена «{name}»: кадр не снят"); worst = int.MaxValue; continue; }
            var newPath = Path.Combine(outDir, $"snap-new-{tag}-x{scale:N0}{suffix}.png");
            Save(captured, newPath);

            var refPath = Path.Combine(refDir, $"snap-ref-{tag}-x{scale:N0}{suffix}.png");
            if (!File.Exists(refPath))
            {
                File.Copy(newPath, refPath);
                created++;
                Console.WriteLine($"  сцена «{name}»: эталона не было — кадр принят за эталон: {refPath}");
                continue;
            }
            var refFrame = LoadFrame(refPath);
            var newFrame = LoadFrame(newPath);

            if (refFrame.PixelSize != newFrame.PixelSize)
            {
                Console.WriteLine($"  РАЗМЕРЫ КАДРОВ РАЗНЫЕ: эталон {refFrame.PixelSize}, кадр {newFrame.PixelSize}");
                worst = int.MaxValue;
                continue;
            }

            // Увеличенные вырезки углов и характерных ячеек — глазами видно, что именно разошлось;
            // у сцен выделения — вырезка самого выделения.
            var crops = name == "base"
                ? new[] { ("corner", 0, 0, 120, 60), ("row11", 0, 400, 60, 44), ("check", 1170, 36, 160, 80), ("file", 1480, 0, 120, 80), ("multi", 1010, 36, 160, 44) }
                : [("sel", (int)(columns.Left(cA) - offsetX) - 8, (int)(headH + pitch * 2) - 8, 120, 60)];
            foreach (var (crop, x, y, cw, ch) in crops)
            {
                SaveCrop(refFrame, x, y, cw, ch, scale, Path.Combine(outDir, $"snap-ref-{tag}-x{scale:N0}{suffix}-{crop}.png"));
                SaveCrop(newFrame, x, y, cw, ch, scale, Path.Combine(outDir, $"snap-new-{tag}-x{scale:N0}{suffix}-{crop}.png"));
            }

            var (strict, loose, diff, bands) = CompareFrames(refFrame, newFrame, tolerance: 24, scale, columns, headH, pitch, offsetX);
            var total = refFrame.PixelSize.Width * (long)refFrame.PixelSize.Height;
            Console.WriteLine($"  сцена «{name}»: различий строго {strict:N0} пикс. ({100.0 * strict / total:N3} %), с допуском 24 — {loose:N0} ({100.0 * loose / total:N3} %)");
            foreach (var (band, count) in bands.OrderByDescending(b => b.Count).Take(name == "base" ? 24 : 10))
                Console.WriteLine($"    {band,-46} {count,8:N0} пикс.");
            Save(diff, Path.Combine(outDir, $"snap-diff-{tag}-x{scale:N0}{suffix}.png"));
            worst = Math.Max(worst, loose);
        }
        wNew.Close();
    }
    Console.WriteLine();
    if (created > 0) Console.WriteLine($"Эталонов принято: {created} (сравнивать было не с чем — следующий прогон сравнит с ними).");
    Console.WriteLine(worst == 0 ? "Кадры совпадают с эталоном." : $"Кадры расходятся с эталоном: до {worst:N0} пикс. с допуском. Карты различий: {outDir}");
    return worst == 0 ? 0 : 2;
}

// Кадр из PNG — тем же декодером для эталона и снятого кадра, чтобы порядок каналов совпал.
static Avalonia.Media.Imaging.WriteableBitmap LoadFrame(string file)
{
    using var fs = File.OpenRead(file);
    return Avalonia.Media.Imaging.WriteableBitmap.Decode(fs);
}

// Вырезка кадра, увеличенная в 6 раз без сглаживания (DIP-координаты × масштаб окна).
static void SaveCrop(Avalonia.Media.Imaging.WriteableBitmap src, int x, int y, int w, int h, double scale, string file)
{
    const int zoom = 6;
    var px = Math.Max(0, (int)(x * scale)); var py = Math.Max(0, (int)(y * scale));
    var pw = (int)(w * scale); var ph = (int)(h * scale);
    pw = Math.Min(pw, src.PixelSize.Width - px); ph = Math.Min(ph, src.PixelSize.Height - py);
    if (pw <= 0 || ph <= 0) return;
    var dst = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(pw * zoom, ph * zoom), src.Dpi,
                                                          Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
    using var fs = src.Lock();
    using var fd = dst.Lock();
    var bufS = new byte[fs.RowBytes * src.PixelSize.Height];
    var bufD = new byte[fd.RowBytes * dst.PixelSize.Height];
    System.Runtime.InteropServices.Marshal.Copy(fs.Address, bufS, 0, bufS.Length);
    for (var yy = 0; yy < ph * zoom; yy++)
        for (var xx = 0; xx < pw * zoom; xx++)
        {
            var s = (py + yy / zoom) * fs.RowBytes + (px + xx / zoom) * 4;
            var d = yy * fd.RowBytes + xx * 4;
            bufD[d] = bufS[s]; bufD[d + 1] = bufS[s + 1]; bufD[d + 2] = bufS[s + 2]; bufD[d + 3] = 255;
        }
    System.Runtime.InteropServices.Marshal.Copy(bufD, 0, fd.Address, bufD.Length);
    using var f = File.Create(file);
    dst.Save(f);
}

// Сетка в окне 1600×900 при заданном масштабе — открыта по точному снимку (эталоны сняты с точным
// числом записей, от него ширина «№»), видимые ячейки с данными.
(Window Window, GridPanel Panel, double Ms) OpenNew(double scale)
{
    var w = new Window { Width = 1600, Height = 900 };
    SetScale(w, scale);
    var p = new GridPanel();
    w.Content = p;
    w.Show();
    Settle(w);
    var sw = Stopwatch.StartNew();
    p.Load(repo, cid, snap);
    Pump();
    WaitUntil(p.Surface.VisibleCellsLoaded, TimeSpan.FromSeconds(30));
    for (var i = 0; i < 4; i++) { Pump(); w.CaptureRenderedFrame(); Thread.Sleep(5); }
    return (w, p, sw.Elapsed.TotalMilliseconds);
}

// Масштаб окна (DPI): у headless-окна он зашит единицей и снаружи не задается — рефлексией ДО Show().
static void SetScale(Window w, double scale)
{
    if (Math.Abs(scale - 1.0) < 0.001) return;
    var impl  = w.PlatformImpl!;
    var field = impl.GetType().GetField("<RenderScaling>k__BackingField",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (field is null) throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба — обнови стенд");
    field.SetValue(impl, scale);
}

// Сравнение кадров (a — эталон, b — снятый): число различий строго и с допуском на канал; карта
// различий; разбивка по строкам и колонкам сетки по ее раскладке.
static (int Strict, int Loose, Avalonia.Media.Imaging.WriteableBitmap Diff, List<(string Name, int Count)> Bands)
    CompareFrames(Avalonia.Media.Imaging.WriteableBitmap a, Avalonia.Media.Imaging.WriteableBitmap b, int tolerance,
                  double scale, ColumnLayout columns, double headH, double pitch, double offsetX)
{
    var size = a.PixelSize;
    var diff = new Avalonia.Media.Imaging.WriteableBitmap(size, a.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
    using var fa = a.Lock();
    using var fb = b.Lock();
    using var fd = diff.Lock();
    var rowBytes = fa.RowBytes;
    var bufA = new byte[rowBytes * size.Height];
    var bufB = new byte[fb.RowBytes * size.Height];
    var bufD = new byte[fd.RowBytes * size.Height];
    System.Runtime.InteropServices.Marshal.Copy(fa.Address, bufA, 0, bufA.Length);
    System.Runtime.InteropServices.Marshal.Copy(fb.Address, bufB, 0, bufB.Length);

    var strict = 0;
    var loose  = 0;
    var bands  = new Dictionary<string, int>();
    for (var y = 0; y < size.Height; y++)
    {
        for (var x = 0; x < size.Width; x++)
        {
            var ia = y * rowBytes + x * 4;
            var ib = y * fb.RowBytes + x * 4;
            var id = y * fd.RowBytes + x * 4;
            var d = Math.Max(Math.Max(Math.Abs(bufA[ia] - bufB[ib]), Math.Abs(bufA[ia + 1] - bufB[ib + 1])),
                             Math.Abs(bufA[ia + 2] - bufB[ib + 2]));
            // Карта: эталон, выбеленный наполовину; различия — красным.
            if (d == 0)
            {
                bufD[id]     = (byte)(128 + bufA[ia] / 2);
                bufD[id + 1] = (byte)(128 + bufA[ia + 1] / 2);
                bufD[id + 2] = (byte)(128 + bufA[ia + 2] / 2);
                bufD[id + 3] = 255;
                continue;
            }
            strict++;
            var hard = d > tolerance;
            if (hard) loose++;
            bufD[id]     = hard ? (byte)0   : (byte)200;
            bufD[id + 1] = hard ? (byte)0   : (byte)200;
            bufD[id + 2] = hard ? (byte)255 : (byte)255;
            bufD[id + 3] = 255;
            if (!hard) continue;

            var dipX = x / scale + offsetX;
            var dipY = y / scale;
            var c    = columns.Count > 0 ? columns.IndexAt(dipX) : -1;
            var colName = c < 0 ? "за колонками" : $"«{columns[c].Header}»";
            var rowName = dipY < headH ? "шапка" : $"строка {(int)((dipY - headH) / pitch) + 1}";
            var key = $"{rowName}, {colName}";
            bands[key] = bands.GetValueOrDefault(key) + 1;
        }
    }
    System.Runtime.InteropServices.Marshal.Copy(bufD, 0, fd.Address, bufD.Length);
    return (strict, loose, diff, bands.Select(kv => (kv.Key, kv.Value)).ToList());
}

static void Pump() => Dispatcher.UIThread.RunJobs();

// Кадр: принудительный тик рендера + копия последнего кадра. Время — вся операция.
static Avalonia.Media.Imaging.WriteableBitmap? Capture(Window w, out TimeSpan elapsed)
{
    var sw = Stopwatch.StartNew();
    var bmp = w.CaptureRenderedFrame();
    elapsed = sw.Elapsed;
    return bmp;
}

static void Save(Avalonia.Media.Imaging.WriteableBitmap? bmp, string file)
{
    if (bmp is null) { Console.WriteLine($"  кадр не снят: {file}"); return; }
    using var fs = File.Create(file);
    bmp.Save(fs);
}

// Безголовое окно применяет размер отложенно — ждем, пока ClientSize устоится.
static void Settle(Window w)
{
    for (var i = 0; i < 50; i++)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (Math.Abs(w.ClientSize.Width - w.Width) < 1 && Math.Abs(w.ClientSize.Height - w.Height) < 1) break;
    }
}

static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
{
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < timeout)
    {
        Dispatcher.UIThread.RunJobs();
        if (condition()) return true;
        Thread.Sleep(5);
    }
    Dispatcher.UIThread.RunJobs();
    return condition();
}

string? Arg(string key)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// Перехват нажатий меню, как на Windows и macOS: Avalonia вешает его на окно, где нарисовано меню, —
// там это отдельное попап-окно, и нажатий в окне программы он не видит. В безголовом режиме меню
// живет в слое окна программы, и перехват закрывал бы меню раньше подложки, пряча сбой; здесь снят.
sealed class OwnWindowMenuInput() : Avalonia.Controls.Platform.DefaultMenuInteractionHandler(true)
{
    protected override void RootPointerPressed(object? sender, PointerPressedEventArgs e) { }
}
