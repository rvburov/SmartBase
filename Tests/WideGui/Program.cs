// Стенд: сколько стоит ШИРОКАЯ таблица (сотни и тысячи свойств) через настоящий TableView.
// Платформа — Avalonia.Headless с НАСТОЯЩИМ шрифтом (Skia): цена раскладки текста честная. Окно с
// TableView (фасад над рисованной сеткой, план drawn-grid.md), коллекция из стенда Tests/DbGenerator
// (профиль wide). Меряем то, что план называет «Критериями готовности»: открытие, первый кадр,
// кадр с данными, шаги прокрутки по строкам и вбок, память, повторное открытие; отдельно — панель
// «Страница записи» (этап 7 плана). Целевое: открытие и шаг прокрутки за один кадр при любом числе
// колонок.
//
//   dotnet run -c Release --project Tests/WideGui -- --db PATH [--collection NAME] [--hide N]

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Database;
using SmartBase.Views.ConstructorDb;

var path = Arg("--db");
if (path is null || !File.Exists(path)) { Console.WriteLine("Нужен --db PATH к файлу стенда."); return 1; }

// Порядок важен: UseSkia ДО UseHeadless (как в Tests/GridProto).
AppBuilder.Configure<SmartBase.App>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();
SmartBase.Utils.FontScale.Init();
Console.OutputEncoding = System.Text.Encoding.UTF8;

using var mgr  = DatabaseManager.Open(9400, "widegui", path);
var repo = mgr.Constructor;
var all  = repo.GetAllCollections();
var col  = Arg("--collection") is { } name ? all.First(c => c.Name == name)
         : all.FirstOrDefault(c => c.Name == "Широкая") ?? all.First();
var cid  = col.Id!.Value;
// Как открывает раздел: снимок без COUNT, точное число записей сетка уточнит в фоне.
var snap = repo.ReadOpenSnapshot(cid, deferCount: true);
Console.WriteLine($"Стенд: {path}");
Console.WriteLine($"Коллекция «{col.Name}»: {snap.Fields.Count:N0} свойств, {repo.CountRecords(cid):N0} записей");
Console.WriteLine();

// Скрыть все колонки, кроме первых N (окно «Отображение колонок»). У рисованной сетки скрытая
// колонка не имеет ни ширины, ни ячеек — цена открытия от числа скрытых не зависит; проверяем.
if (Arg("--hide") is { } hideArg && int.TryParse(hideArg, out var keep))
{
    var allKeys = snap.Fields.Select(f => f.Id!.Value.ToString()).ToList();
    repo.SaveColumnDisplay(cid, allKeys.Select((k, i) => (k, i < keep, 1)));
    snap = repo.ReadOpenSnapshot(cid, deferCount: true);
    Console.WriteLine($"Скрыты все колонки, кроме первых {keep}.");
}

var window = new Window { Width = 1600, Height = 900 };
var table  = new TableView();
window.Content = table;
window.Show();
Settle(window);
var panel = table.DrawnPanel;

// Разовая цена шрифта в процессе — до открытия, чтобы не приписать ее таблице.
Frame(window);

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var heapBefore = GC.GetTotalMemory(true) / 1024.0 / 1024;
var a0 = GC.GetTotalAllocatedBytes(true);
var sw = Stopwatch.StartNew();
table.Load(repo, cid, snap);
var loadMs = sw.Elapsed.TotalMilliseconds;
Pump();
Frame(window);
var firstFrameMs = sw.Elapsed.TotalMilliseconds;
var loaded = WaitUntil(() => panel.Surface.VisibleCellsLoaded(), TimeSpan.FromSeconds(30));
Frame(window);
var dataMs = sw.Elapsed.TotalMilliseconds;
var loadMb = (GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024;

Console.WriteLine($"TableView.Load (колонки, запуск первой страницы): {loadMs,8:N1} мс");
Console.WriteLine($"Первый кадр (шапка, заглушки строк):             {firstFrameMs,8:N1} мс");
Console.WriteLine($"Кадр с данными:                                  {dataMs,8:N0} мс{(loaded ? "" : " — данные НЕ пришли")}, {loadMb:N1} МБ аллокаций");
Console.WriteLine($"Раскладка: колонок в кадре {panel.Surface.Columns.Count:N0} из {panel.Columns.Count:N0}, ячеек нарисовано {panel.Surface.LastCellsDrawn:N0}");

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var heap = GC.GetTotalMemory(true) / 1024.0 / 1024;
Console.WriteLine($"Управляемая куча после открытия:                 {heap,8:N0} МБ (до открытия {heapBefore:N0})");
Console.WriteLine($"Рабочий набор процесса:                          {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024,8:N0} МБ");
Console.WriteLine($"Визуальное дерево окна:                          {window.GetVisualDescendants().Count(),8:N0} объектов (ячеек-контролов нет — кадр рисует сетка)");
Console.WriteLine();

// Прокрутка — то, что в программе делает человек колесом, кнопками нижней панели и полосой:
// время до кадра (UI-поток) и до кадра с данными (страница подгружается в фоне).
Console.WriteLine("Прокрутка (кадр / кадр с данными):");
Step("экран вниз",       table.ScrollRowsPageForward);
Step("еще экран вниз",   table.ScrollRowsPageForward);
Step("экран вверх",      table.ScrollRowsPageBack);
Step("в конец",          table.ScrollRowsToEnd);
Step("в начало",         table.ScrollRowsToStart);
Step("вбок к «+»",       table.ScrollToAddPropertyColumn);
Step("вбок к началу",    () => panel.Surface.OffsetX = 0);
Console.WriteLine();

// Панель «Страница записи» — строит редакторы ВСЕХ свойств записи (без виртуализации, этап 7 плана).
{
    var rec  = repo.GetRecordsPageAfter(cid, null, null, 1)[0].Id!.Value;
    var page = new RecordPageView();
    var host = new Window { Width = 600, Height = 900 };
    host.Content = page;
    host.Show();
    var b0 = GC.GetTotalAllocatedBytes(true);
    var s3 = Stopwatch.StartNew();
    page.Show(repo, rec, cid);
    host.Measure(new Size(600, 900));
    host.Arrange(new Rect(0, 0, 600, 900));
    Pump();
    s3.Stop();
    Console.WriteLine($"«Страница записи» (все свойства):                {s3.Elapsed.TotalMilliseconds,8:N0} мс, " +
                      $"{(GC.GetTotalAllocatedBytes(true) - b0) / 1024.0 / 1024:N1} МБ аллокаций, " +
                      $"объектов в дереве {host.GetVisualDescendants().Count():N0}");
    host.Close();
}

// Повторное открытие той же коллекции (обновление после правки свойства): колонки строятся заново,
// строки и прокрутка остаются.
a0 = GC.GetTotalAllocatedBytes(true);
sw.Restart();
table.Load(repo, cid, repo.ReadOpenSnapshot(cid, deferCount: true));
Pump();
Frame(window);
Console.WriteLine($"Повторный Load + кадр:                           {sw.Elapsed.TotalMilliseconds,8:N1} мс, " +
                  $"{(GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024:N1} МБ аллокаций");
return 0;

// Один шаг: действие, кадр, ожидание данных видимых ячеек, кадр.
void Step(string label, Action act)
{
    var s = Stopwatch.StartNew();
    act();
    Pump();
    Frame(window);
    var frameMs = s.Elapsed.TotalMilliseconds;
    var ok = WaitUntil(() => panel.Surface.VisibleCellsLoaded(), TimeSpan.FromSeconds(30));
    Frame(window);
    Console.WriteLine($"  {label,-16} {frameMs,7:N1} мс / {s.Elapsed.TotalMilliseconds,6:N0} мс{(ok ? "" : " — данные НЕ пришли")}, {panel.VisibleRowRange().Label}");
}

// Кадр: принудительный тик рендера + копия последнего кадра.
static void Frame(Window w) => w.CaptureRenderedFrame();

static void Pump() => Dispatcher.UIThread.RunJobs();

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
    var s = Stopwatch.StartNew();
    while (s.Elapsed < timeout)
    {
        Dispatcher.UIThread.RunJobs();
        if (condition()) return true;
        Thread.Sleep(5);
    }
    Dispatcher.UIThread.RunJobs();
    return condition();
}

static string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
