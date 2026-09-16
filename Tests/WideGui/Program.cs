// Стенд: сколько стоит ШИРОКАЯ таблица (много свойств) в настоящем UI-дереве.
// Платформа — Avalonia.Headless (как Tests/MarkupLayout), окно с TableView, загрузка коллекции
// из готового стенда Tests/DbGenerator (профиль wide). Меряем время и аллокации на построение
// колонок и раскладку, а также память, которую держит открытая таблица.
//
//   dotnet run -c Release --project Tests/WideGui -- --db PATH

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

AppBuilder.Configure<SmartBase.App>()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
          .SetupWithoutStarting();

SmartBase.Utils.FontScale.Init();

using var mgr  = DatabaseManager.Open(9400, "widegui", path);
var repo = mgr.Constructor;
var col  = repo.GetAllCollections().First(c => c.Name == "Широкая");
var cid  = col.Id!.Value;
var snap = repo.ReadOpenSnapshot(cid);
Console.WriteLine($"Стенд: {path}");
Console.WriteLine($"Коллекция «{col.Name}»: {snap.Fields.Count:N0} свойств, {snap.RecordCount:N0} записей");
Console.WriteLine();

// Скрыть все колонки, кроме первых N (окно «Отображение колонок») — чтобы отделить цену
// СОЗДАНИЯ объектов колонок от цены их РАСКЛАДКИ: скрытая колонка строится, но не раскладывается.
if (Arg("--hide") is { } hideArg && int.TryParse(hideArg, out var keep))
{
    var all = snap.Fields.Select(f => f.Id!.Value.ToString()).ToList();
    repo.SaveColumnDisplay(cid, all.Select((k, i) => (k, i < keep, 1)));
    snap = repo.ReadOpenSnapshot(cid);
    Console.WriteLine($"Скрыты все колонки, кроме первых {keep}.");
}

var window = new Window { Width = 1600, Height = 900 };
var table  = new TableView();
window.Content = table;
window.Show();
Pump();

var a0 = GC.GetTotalAllocatedBytes(true);
var sw = Stopwatch.StartNew();
table.Load(repo, cid, snap);
sw.Stop();
var loadMs = sw.Elapsed.TotalMilliseconds;
var loadMb = (GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024;

a0 = GC.GetTotalAllocatedBytes(true);
sw.Restart();
window.Measure(new Size(1600, 900));
window.Arrange(new Rect(0, 0, 1600, 900));
Pump();
sw.Stop();
var layoutMs = sw.Elapsed.TotalMilliseconds;
var layoutMb = (GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024;

GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
var heap = GC.GetTotalMemory(true) / 1024.0 / 1024;

Console.WriteLine($"TableView.Load (колонки + первая страница): {loadMs:N0} мс, {loadMb:N1} МБ аллокаций");
Console.WriteLine($"Первая раскладка окна:                      {layoutMs:N0} мс, {layoutMb:N1} МБ аллокаций");
Console.WriteLine($"Управляемая куча после открытия:            {heap:N0} МБ");
Console.WriteLine($"Рабочий набор процесса:                     {Process.GetCurrentProcess().WorkingSet64 / 1024.0 / 1024:N0} МБ");
Console.WriteLine($"Аллокаций всего:                            {GC.GetTotalAllocatedBytes(true) / 1024.0 / 1024:N0} МБ");

// Что реально построено в дереве: объекты визуального дерева, ячейки и меню шапок.
var visuals = window.GetVisualDescendants().ToList();
var cells   = visuals.Count(v => v.GetType().Name == "DataGridCell");
var rows    = visuals.Count(v => v.GetType().Name == "DataGridRow");
var menus   = visuals.OfType<Control>().Count(c => c.ContextMenu is not null);
var menuItems = visuals.OfType<Control>().Where(c => c.ContextMenu is not null)
                       .Sum(c => c.ContextMenu!.Items.Count);
Console.WriteLine($"Визуальное дерево окна: {visuals.Count:N0} объектов " +
                  $"(ячеек DataGridCell {cells:N0}, строк {rows:N0}); " +
                  $"контролов с контекстным меню {menus:N0}, пунктов в них {menuItems:N0}");

// Прокрутка: прыжок вглубь и обратно — то, что в приложении делает человек колесом/полосой.
// Меряем UI-поток: ScrollIntoView + раскладка + прокачка очереди диспетчера (страница
// подгружается в фоне, но ЗАПОЛНЕНИЕ строк и перечитывание всех привязок идет здесь).
if (table.GetVisualDescendants().OfType<DataGrid>().FirstOrDefault() is { } grid
    && grid.ItemsSource is System.Collections.IList items)
{
    foreach (var target in new[] { 500, 5000, 0 })
    {
        var b0 = GC.GetTotalAllocatedBytes(true);
        var s2 = Stopwatch.StartNew();
        grid.ScrollIntoView(items[Math.Min(target, items.Count - 1)], null);
        window.Measure(new Size(1600, 900));
        window.Arrange(new Rect(0, 0, 1600, 900));
        for (var i = 0; i < 40; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        window.Measure(new Size(1600, 900));
        window.Arrange(new Rect(0, 0, 1600, 900));
        s2.Stop();
        Console.WriteLine($"Прокрутка к строке {target,6:N0}:                   {s2.Elapsed.TotalMilliseconds,8:N0} мс, " +
                          $"{(GC.GetTotalAllocatedBytes(true) - b0) / 1024.0 / 1024:N1} МБ аллокаций");
    }
}

// Панель «Страница записи» — строит редакторы ВСЕХ свойств записи (без виртуализации).
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
    Console.WriteLine($"«Страница записи» (все свойства):           {s3.Elapsed.TotalMilliseconds,8:N0} мс, " +
                      $"{(GC.GetTotalAllocatedBytes(true) - b0) / 1024.0 / 1024:N1} МБ аллокаций, " +
                      $"объектов в дереве {host.GetVisualDescendants().Count():N0}");
}

// Повторное открытие той же коллекции (обновление после правки свойства) — полная пересборка колонок.
a0 = GC.GetTotalAllocatedBytes(true);
sw.Restart();
table.Load(repo, cid, repo.ReadOpenSnapshot(cid));
window.Measure(new Size(1600, 900));
window.Arrange(new Rect(0, 0, 1600, 900));
Pump();
sw.Stop();
Console.WriteLine($"Повторный Load + раскладка:                 {sw.Elapsed.TotalMilliseconds:N0} мс, " +
                  $"{(GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024:N1} МБ аллокаций");
return 0;

static void Pump()
{
    for (var i = 0; i < 5; i++)
        Dispatcher.UIThread.RunJobs();
}

static string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
