// Стенд этапа 0 плана drawn-grid.md: прототип рисованной сетки (GridPanel/GridSurface) на
// настоящей базе. Меряет то, что план называет критериями готовности:
//   открытие (первый кадр и кадр с данными), теплые шаги колесом вниз и вверх, шаги вбок,
//   прыжок ползунком в конец, память сетки; кадры сохраняются в PNG.
// Платформа — Avalonia.Headless с НАСТОЯЩИМ шрифтом (Skia): цена раскладки текста честная,
// а кадр можно сохранить и сравнить с нынешней таблицей (--old рисует ее на той же базе).
//
//   dotnet run -c Release --project Tests/GridProto -- --db PATH [--collection NAME] [--steps 100] [--out DIR] [--old]

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Database;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Views.ConstructorDb;
using SmartBase.Views.ConstructorDb.DrawnGrid;

var path = Arg("--db");
if (path is null || !File.Exists(path)) { Console.WriteLine("Нужен --db PATH к файлу базы."); return 1; }
var steps  = int.TryParse(Arg("--steps"), out var s) ? Math.Max(1, s) : 100;
var outDir = Arg("--out") ?? Path.Combine(Path.GetTempPath(), "gridproto");
var old    = args.Contains("--old");
Directory.CreateDirectory(outDir);

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Порядок важен: UseSkia ДО UseHeadless (как в Tests/EditGeometry).
AppBuilder.Configure<SmartBase.App>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();
SmartBase.Utils.FontScale.Init();

using var mgr = DatabaseManager.Open(9401, "gridproto", path);
var repo = mgr.Constructor;
var all  = repo.GetAllCollections();
var col  = Arg("--collection") is { } name ? all.First(c => c.Name == name) : all.First();
var cid  = col.Id!.Value;
var snap = repo.ReadOpenSnapshot(cid);
var tag  = Path.GetFileNameWithoutExtension(path);

Console.WriteLine($"Стенд: {path}");
Console.WriteLine($"Коллекция «{col.Name}»: {snap.Fields.Count:N0} свойств, {snap.RecordCount:N0} записей");
Console.WriteLine();

var window = new Window { Width = 1600, Height = 900 };

if (old)
{
    // Нынешняя таблица — только кадр для сравнения глаз в глаз (числа по ней есть в WideGui).
    var table = new TableView();
    window.Content = table;
    window.Show();
    Settle(window);
    var sw = Stopwatch.StartNew();
    table.Load(repo, cid, snap);
    Pump();
    var first = sw.Elapsed;
    var loaded = WaitUntil(() =>
    {
        var rows = window.GetVisualDescendants().OfType<DataGridRow>()
                         .Select(r => r.DataContext as TableRowVm).Where(r => r is not null).ToList();
        return rows.Count > 0 && rows.All(r => !r!.IsLoading);
    }, TimeSpan.FromSeconds(20));
    var frame = Capture(window, out _);
    Console.WriteLine($"Нынешняя таблица: первый кадр {Ms(first)}, данные {(loaded ? "пришли" : "НЕ пришли")} за {Ms(sw.Elapsed)}");
    Save(frame, Path.Combine(outDir, $"old-{tag}.png"));
    return 0;
}

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
panel.Load(repo, cid, snap);
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
Row("Открытие: кадр с данными", Ms(tData), gotData ? "первая страница пришла из фона" : "ДАННЫЕ НЕ ПРИШЛИ за 20 с");
Row("Ячеек в кадре", surface.LastCellsDrawn.ToString("N0"), $"колонок в раскладке {surface.Columns.Count:N0}, строк {surface.RowCount:N0}");
Row("Куча после открытия", $"{(heapOpen - heapBefore) / 1024.0 / 1024:N1} МБ", "прирост управляемой кучи от пустого окна");

// Повторное открытие той же коллекции: разовые цены процесса (шрифт, глифы, геометрии иконок,
// шаблоны полос) уже оплачены — так открывается коллекция в работающей программе.
{
    var swRe = Stopwatch.StartNew();
    panel.Load(repo, cid, snap);
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
