// Стенд этапа 8 плана drawn-grid.md: вся программа без экрана. Настоящее главное окно (MainWindow)
// на наборе баз, повторяющем рабочий: копии «Основной» и «Второй» (4 коллекции по 456 тыс. записей)
// из --data/src, test_database (10 млн записей, Tests/test_database.db) и — при --dbs full — wide1000
// (1000 свойств). Сценарий: старт (окно, фоновое подключение баз, загрузки разделов), затем обход
// разделов (--only start — только старт). Все время UI-поток опрашивается пробой: задержка ответа
// дольше --stall мс — пауза; в отчете ее длительность, шаг сценария и методы программы, на которых
// стоял UI-поток (выборки стеков EventPipe; --no-trace — без них). Отдельно — все замеры Diag с
// длительностью (не только медленные) и записи журнала. Папка данных стенда — --data/run: реестр
// и настройки создаются заново при каждом прогоне, копия «Второй» переиспользуется (--fresh —
// скопировать заново). Настоящая папка данных программы не читается и не пишется.
//
//   dotnet run -c Release --project Tests/Sections -- [--data DIR] [--dbs real|full] [--only start|tour]
//                                                     [--stall MS] [--report MS] [--ops MS] [--no-trace] [--fresh] [--info]
//   --only start | tour | группы через запятую: modules, calendar, tasks, constructor, settings, profile
//   --only app — настоящий старт App (сплэш, подготовка, окно) в отладочной сборке, положенной в
//   ~/TestData/sections/apptest/bin с меткой SmartBase.csproj в apptest: время до сплэша и до окна.
//   --only readbench [--big путь] — чтение окон календаря без интерфейса (по пять раз).
//   --repeat N — обход видов календаря N раз (первый показ отдельно от повторных).
//   --under Класс.Метод[,…] — в разборе медленных замеров Diag еще и что внутри этих методов
//   (кадры библиотек тоже), например --under WeekView.Refresh,AllDayBar.SetData.
//   --only frames [--scale 1|2] [--dbs main] [--ref DIR] — кадры недели и дня календаря (обычный,
//   выделение, наведение) на неделе, которую стенд заводит в копию «Основной», против эталонов в
//   --ref (по умолчанию ~/TestData/regress; нет эталона — кадр им становится); --dump — ширины блоков
//   событий и поле их текста (сверка геометрии блока с прежней раскладкой).
//   --only calops — операции календаря на базах 10 млн и wide1000: дублирование, удаление, перенос,
//   растяжение, создание в «Неделе», «Дне», «Месяце», Диаграмме и «Журнале»; ожидание результата,
//   паузы UI, полные перезагрузки вида, прокрутка и выделение после операции.
//   --only tableops [--table ПУТЬ] [--set columns,filter,records] — операции таблицы на широкой базе
//   (по умолчанию ~/TestData/wide10m.db): столбцы, список значений фильтра и поиск, записи, окна
//   «Расположение» и «Отображение»; время до результата, паузы UI, память.
//   --only sidebarops — раскладка строк боковой панели после сворачивания баз, подключения базы,
//   переноса коллекции в группу и обратно, удаления коллекции.

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Serilog;
using SmartBase.Database;
using SmartBase.Database.DocIndex;
using SmartBase.Logging;
using SmartBase.Tests.Sections;
using SmartBase.Tests.ServiceGrids;
using SmartBase.Utils;
using SmartBase.Views;
using SmartBase.Views.ConstructorDb.Calendar;

var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dataDir  = Arg("--data") ?? Path.Combine(home, "TestData", "sections");
var dbSet    = Arg("--dbs") ?? "full";
var only     = Arg("--only");
var stallMs  = double.Parse(Arg("--stall") ?? "50");
var reportMs = double.Parse(Arg("--report") ?? "100");
var opsMs    = double.Parse(Arg("--ops") ?? "300");
var trace    = !args.Contains("--no-trace");
var fresh    = args.Contains("--fresh");
var echoInfo = args.Contains("--info");
var scale    = double.Parse(Arg("--scale") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
var repeat   = int.Parse(Arg("--repeat") ?? "1");
var detailMs = double.Parse(Arg("--detail") ?? "1000");   // пауза не короче — разбор по методам и всем кадрам
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Настоящий старт App (сплэш, фоновая подготовка, окно) без экрана — --only app. Папку данных
// отладочная сборка берет рядом с ближайшей меткой SmartBase.csproj вверх от программы
// (AppSettings.FindProjectRoot): стенд кладется в ~/TestData/sections/apptest/bin, метка — в
// apptest, и программа работает с apptest/.localdata, а не с рабочими данными. Отчет: когда
// показан сплэш и когда главное окно.
if (only == "app")
{
#if DEBUG
    var appClock = Stopwatch.StartNew();
    Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
        .WriteTo.Sink(new MemorySink(appClock, echoInfo: true)).CreateLogger();
    Console.WriteLine($"Папка данных: {AppSettings.AppDataDir}");
    if (!AppSettings.AppDataDir.Contains("apptest", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Отказ: папка данных не стендовая — положите стенд в ~/TestData/sections/apptest/bin и метку SmartBase.csproj в apptest");
        return 1;
    }
    double? splashAt = null, mainAt = null;
    var watcher = new Thread(() =>
    {
        while (appClock.Elapsed < TimeSpan.FromSeconds(120))
        {
            Thread.Sleep(10);
            var (splash, mainShown) = Dispatcher.UIThread.Invoke(() =>
            {
                if (Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt)
                    return (false, false);
                return (lt.Windows.Any(w => w is SplashWindow && w.IsVisible), lt.MainWindow is MainWindow m && m.IsVisible);
            });
            if (splash && splashAt is null) splashAt = appClock.Elapsed.TotalMilliseconds;
            if (mainShown) { mainAt = appClock.Elapsed.TotalMilliseconds; break; }
        }
        Console.WriteLine($"Сплэш показан: {splashAt:N0} мс; главное окно: {mainAt:N0} мс");
        Thread.Sleep(1500);
        Dispatcher.UIThread.Post(() =>
            (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown());
    }) { IsBackground = true };
    // Наблюдатель — только после платформенных служб: первое обращение к диспетчеру из чужого
    // потока до них создало бы диспетчер без цикла сообщений.
    AppBuilder.Configure<SmartBase.App>()
              .UseSkia()
              .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
              .AfterPlatformServicesSetup(_ => watcher.Start())
              .StartWithClassicDesktopLifetime(args);
    return mainAt is null ? 1 : 0;
#else
    Console.WriteLine("--only app работает только в отладочной сборке стенда (папка данных — .localdata у метки)");
    return 1;
#endif
}

// Чтение окон календаря без интерфейса — --only readbench [--big путь]: сколько стоит само
// чтение (недели, дня, месяца, диаграммы) на базе 10 млн, по пять раз подряд.
if (only == "readbench")
{
    SQLitePCL.Batteries_V2.Init();
    var benchPath = Arg("--big") ?? Path.Combine(FindRoot(), "Tests", "test_database.db");
    using var benchCtx = new SmartBase.Database.AppDbContext(benchPath);
    benchCtx.ApplyPerformancePragmas();
    var benchRepo = new SmartBase.Database.Repositories.ConstructorRepository(benchCtx);

    // Чтение «Месяца» всеми базами стенда сразу, как при первом показе календаря (у каждой базы
    // свое подключение, фоном): первый проход в процессе платит разовые цены (сборка запросов,
    // подготовка команд), следующие — только чтение.
    var monthDbs = new[] { Path.Combine(dataDir, "run", "main.db"), Path.Combine(dataDir, "run", "second.db"), benchPath,
                           Arg("--wide") ?? Path.Combine(home, "TestData", "wide1000.db") }
                   .Where(File.Exists)
                   .Select(p =>
                   {
                       var ctx = new SmartBase.Database.AppDbContext(p);
                       var repo = new SmartBase.Database.Repositories.ConstructorRepository(ctx);
                       // Как в программе к показу раздела: база подключена (уборка сводки дней) и
                       // дерево коллекций прочитано — EF и статика репозитория уже прогреты.
                       repo.TidyCalendarDayCounts();
                       repo.GetAllCollections();
                       return (Name: Path.GetFileName(p), Repo: repo);
                   })
                   .ToList();
    if (args.Contains("--steps"))
    {
        // Первое чтение одной базой по шагам: подключение, прагмы, чтение окна.
        var probePath = Path.Combine(dataDir, "run", "main.db");
        for (var pass = 1; pass <= 2; pass++)
        {
            var sw = Stopwatch.StartNew();
            using var pctx = new SmartBase.Database.AppDbContext(probePath);
            var tCtx = sw.Elapsed.TotalMilliseconds;
            pctx.ApplyPerformancePragmas();
            Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.OpenConnection(pctx.Database);
            var tOpen = sw.Elapsed.TotalMilliseconds;
            using (var c = Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(pctx.Database).CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM db_fields";
                c.ExecuteScalar();
            }
            var tSql = sw.Elapsed.TotalMilliseconds;
            var tPragmas = sw.Elapsed.TotalMilliseconds;
            var prepo = new SmartBase.Database.Repositories.ConstructorRepository(pctx);
            if (pass == 1) new List<JitMarkBegin>().Add(default);
            prepo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 8, 31), new DateTime(2026, 10, 12), null, 24, 2000, 24);
            if (pass == 1) new List<JitMarkEnd>().Add(default);
            var tRead = sw.Elapsed.TotalMilliseconds;
            prepo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 8, 31), new DateTime(2026, 10, 12), null, 24, 2000, 24);
            Console.WriteLine($"  шаги, проход {pass}: контекст {tCtx:0}, открытие {tOpen - tCtx:0}, первый SQL {tSql - tOpen:0}, чтение {tRead - tPragmas:0}, " +
                              $"повтор {sw.Elapsed.TotalMilliseconds - tRead:0} мс");
        }
    }
    for (var pass = 1; pass <= 4; pass++)
    {
        var wall = Stopwatch.StartNew();
        var jobs = monthDbs.Select(d =>
        {
            var sw = Stopwatch.StartNew();
            return (d.Name, Task: d.Repo.LoadCalendarGridRangeAsync(new DateTime(2026, 8, 31), new DateTime(2026, 10, 12), null, 24,
                                                                    allDayCap: 2000, multiDayPerDay: 24)
                                        .ContinueWith(t => (Rows: t.Result.Count, Ms: sw.Elapsed.TotalMilliseconds)));
        }).ToList();
        Task.WaitAll(jobs.Select(j => (Task)j.Task).ToArray());
        Console.WriteLine($"  месяц всеми базами, проход {pass}: {wall.Elapsed.TotalMilliseconds:0} мс  (" +
                          string.Join(", ", jobs.Select(j => $"{j.Name} {j.Task.Result.Ms:0} мс / {j.Task.Result.Rows}")) + ")");
    }

    // То же для раздела «Задачи»: размеры коллекций всеми базами (фон) и заголовок раздела
    // (стадии, коллекции, пользователи — на общем контексте, как UpdateChrome на UI-потоке).
    for (var pass = 1; pass <= 3; pass++)
    {
        var wall = Stopwatch.StartNew();
        var jobs = monthDbs.Select(d =>
        {
            var sw = Stopwatch.StartNew();
            return (d.Name, Task: d.Repo.GetTaskCollectionStatsAsync(null)
                                        .ContinueWith(t => (Rows: t.Result.Count, Ms: sw.Elapsed.TotalMilliseconds)));
        }).ToList();
        Task.WaitAll(jobs.Select(j => (Task)j.Task).ToArray());
        var statsMs = wall.Elapsed.TotalMilliseconds;
        var chrome = Stopwatch.StartNew();
        foreach (var d in monthDbs)
        {
            d.Repo.GetAllCollections();
            d.Repo.GetTaskStages(null);
            d.Repo.GetUserOptions();
        }
        Console.WriteLine($"  задачи всеми базами, проход {pass}: размеры {statsMs:0} мс (" +
                          string.Join(", ", jobs.Select(j => $"{j.Name} {j.Task.Result.Ms:0}")) +
                          $"), заголовок {chrome.Elapsed.TotalMilliseconds:0} мс");
    }

    var benchCases = new (string Name, Func<int> Run)[]
    {
        ("неделя 21–28.09.2026, 2000 на день", () => benchRepo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 9, 21), new DateTime(2026, 9, 28), null, 2000).Count),
        ("день 26.09.2026, 2000",              () => benchRepo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 9, 26), new DateTime(2026, 9, 27), null, 2000).Count),
        ("месяц 31.08–12.10.2026, 24 на день", () => benchRepo.GetCalendarRecordsByRangeCapped(new DateTime(2026, 8, 31), new DateTime(2026, 10, 12), null, 24, 2000, 24).Count),
        ("диаграмма сентябрь 2026",            () => benchRepo.GetDiagramRecordsCapped(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), null, 200).Count),
        ("агрегаты диаграммы сентябрь 2026",   () => benchRepo.GetDiagramStats(new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), null).Count),
        ("многодневные 2026 года",             () => benchRepo.GetCalendarSpanningRecords(new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), null, 2000).Count),
    };
    foreach (var (name, run) in benchCases)
    {
        var times = new List<double>();
        var n = 0;
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            n = run();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"  {name,-40} строк {n,6}: {string.Join(" / ", times.Select(t => t.ToString("0")))} мс");
    }
    return 0;
}

var srcDir  = Path.Combine(dataDir, "src");
var runDir  = Path.Combine(dataDir, "run");
var bigPath  = Arg("--big")  ?? Path.Combine(FindRoot(), "Tests", "test_database.db");
var widePath = Arg("--wide") ?? Path.Combine(home, "TestData", "wide1000.db");
// --only tableops: база операций таблицы — подключается как есть, по пути (не копией).
var tablePath = Arg("--table") ?? Path.Combine(home, "TestData", "wide10m.db");
var tableName = Path.GetFileNameWithoutExtension(tablePath);
if (only == "tableops" && !File.Exists(tablePath)) { Console.WriteLine($"Нет базы стенда: {tablePath}"); return 1; }
foreach (var need in new[] { Path.Combine(srcDir, "main.db"), Path.Combine(srcDir, "second.db"), bigPath })
    if (!File.Exists(need)) { Console.WriteLine($"Нет базы стенда: {need}"); return 1; }
if (dbSet == "full" && !File.Exists(widePath)) { Console.WriteLine($"Нет базы стенда: {widePath}"); return 1; }
if (only == "frames") dbSet = "main";

// ── Папка данных стенда ──────────────────────────────────────────────────────
Directory.CreateDirectory(runDir);
foreach (var name in new[] { "app.db", "main.db", "security.db", "integrations.db", DocTextIndex.FileName })
    foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        if (File.Exists(Path.Combine(runDir, name + suffix))) File.Delete(Path.Combine(runDir, name + suffix));
var mainPath   = Path.Combine(runDir, "main.db");
var secondPath = Path.Combine(runDir, "second.db");
File.Copy(Path.Combine(srcDir, "main.db"), mainPath);
if (fresh || !File.Exists(secondPath))
{
    Console.WriteLine("Копия «Второй»…");
    foreach (var suffix in new[] { "-wal", "-shm" })
        if (File.Exists(secondPath + suffix)) File.Delete(secondPath + suffix);
    File.Copy(Path.Combine(srcDir, "second.db"), secondPath, overwrite: true);
}

var clock   = Stopwatch.StartNew();
var t0Utc   = DateTime.UtcNow;
var probe   = new StallProbe(stallMs);
var diagLog = new DiagLog(clock, probe);
var sink    = new MemorySink(clock, echoInfo);
Log.Logger  = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
Diag.Observer = diagLog.Observe;

// Этапы запуска до окна (как у App до сплэша): платформа и стили, реестр app.db, настройки,
// дефолтная база — холодная цена каждого видна в отчете.
var bootSw = Stopwatch.StartNew();
var boot = new List<(string Name, double Ms)>();
void Boot(string name) { boot.Add((name, bootSw.Elapsed.TotalMilliseconds)); bootSw.Restart(); }

// Порядок важен: UseSkia ДО UseHeadless (как в Tests/GridProto).
AppBuilder.Configure<SmartBase.App>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();
SQLitePCL.Batteries_V2.Init();
Dispatcher.UIThread.UnhandledException += (_, e) =>
{
    Console.WriteLine($"    [исключение UI] {e.Exception.GetType().Name}: {e.Exception.Message}");
    e.Handled = true;
};
var uiThread = Native.GetCurrentThreadId();
Boot("платформа Avalonia и стили приложения");

// Реестр и настройки — в папке стенда (как у App, но без путей AppSettings: они смотрят в
// настоящую папку данных). Настройки — как у рабочей копии: сохраненный фильтр «Задач»,
// уведомления и автопроверка обновления выключены.
var registry = DatabaseRegistry.Open(mainPath, Path.Combine(runDir, "app.db"));
Boot("реестр app.db (EF, первая модель)");
AppSettings.AttachStore(registry);
CurrentUser.AttachStore(registry);
if (dbSet != "main")
{
    registry.Add("Вторая", secondPath);
    registry.Add("test_database", bigPath);
}
if (dbSet == "full") registry.Add("wide1000", widePath);
if (only == "tableops") registry.Add(tableName, tablePath);
registry.Set("tasksLastFilter", "Stage:Выполнено");
registry.Set("notificationsEnabled", "False");
registry.Set("autoUpdate", "False");

FontScale.Init();
CalendarSettings.Init();
TaskSettings.Init();
LibrarySettings.Init();
ConstructorSettings.Init();
ShortcutSettings.Init();
DocIndexService.Attach(new DocIndexService(new DocTextIndex(Path.Combine(runDir, DocTextIndex.FileName)))
{
    MaxFileMb = LibrarySettings.IndexMaxMb,
});
SecurityJournal.Attach(new SecurityJournal(Path.Combine(runDir, "security.db")));
Scaling.Load();
Boot("настройки, кеш документов, журнал безопасности");

// Дефолтная база — как в Workspace.Open, но по пути стенда.
var defEntry = registry.GetAll().First(d => d.IsDefault);
var openSw   = Stopwatch.StartNew();
var def      = DatabaseManager.Open(defEntry.Id, defEntry.Name, mainPath);
if (only == "frames") SeedCalendarWeek(def.Constructor);
var workspace = Workspace.Attach(registry, def);
Boot("дефолтная база (миграции, если нужны)");
Console.WriteLine($"Стенд: {runDir}; баз в реестре {registry.GetAll().Count} ({dbSet}); дефолтная открыта за {openSw.ElapsedMilliseconds} мс");
Console.WriteLine($"Порог паузы {stallMs} мс, разбор пауз от {reportMs} мс, выборки стеков: {(trace ? "да" : "нет")}");
Console.WriteLine("Этапы до окна: " + string.Join("; ", boot.Select(b => $"{b.Name} {b.Ms:N0} мс")));
Console.WriteLine();

Sampler? sampler = trace ? new Sampler(Path.Combine(dataDir, "trace.nettrace")) : null;
probe.Start();

// Кадры: безголовая платформа рисует по явному тику — даем его с частотой экрана.
var frames = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render,
                                 (_, _) => AvaloniaHeadlessPlatform.ForceRenderTimerTick());
frames.Start();

var steps = new List<(string Name, double StartSec, double EndSec)>();
// Сборка мусора по шагам: снимок на начале каждого шага и в конце прогона — паузы GC видны
// отдельно от работы (в выборках стеков они выглядят как Thread.PollGC в любом месте кода).
var gcMarks = new List<(TimeSpan Pause, int G0, int G1, int G2, long HeapMb)>();
(TimeSpan, int, int, int, long) GcNow() =>
    (GC.GetTotalPauseDuration(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
     GC.GetTotalMemory(false) / 1048576);
MainWindow? main = null;
var exit = new CancellationTokenSource();
Dispatcher.UIThread.Post(async () =>
{
    try { await Scenario(); }
    catch (Exception ex) { Console.WriteLine($"  СБОЙ СЦЕНАРИЯ: {ex}"); }
    finally { exit.Cancel(); }
});
Dispatcher.UIThread.MainLoop(exit.Token);
frames.Stop();
probe.Stop();
sampler?.Stop();

Report();
return 0;

// ── Сценарий ─────────────────────────────────────────────────────────────────

async Task Scenario()
{
    // --modules-cost — холодная цена построения каждого раздела по отдельности (до окна).
    if (args.Contains("--modules-cost"))
    {
        foreach (var make in new (string, Func<Control>)[]
                 {
                     ("календарь",  () => new SmartBase.Views.ConstructorDb.Calendar.CalendarControllerView()),
                     ("задачи",     () => new SmartBase.Views.Tasks.TasksControllerView()),
                     ("конструктор", () => new SmartBase.Views.ConstructorDb.ConstructorControllerView()),
                     ("настройки",  () => new SmartBase.Views.Settings.SettingsView()),
                     ("расширения", () => new SmartBase.Views.Extensions.ExtensionsView()),
                     ("профиль",    () => new SmartBase.Views.Profile.ProfileView()),
                     ("заметки",    () => new SmartBase.Views.Notes.NotesControllerView()),
                     ("библиотека", () => new SmartBase.Views.Library.LibraryControllerView()),
                 })
        {
            var msw = Stopwatch.StartNew();
            make.Item2();
            var cold = msw.Elapsed.TotalMilliseconds;
            msw.Restart();
            make.Item2();
            Console.WriteLine($"  раздел {make.Item1}: холодно {cold:N0} мс, повторно {msw.Elapsed.TotalMilliseconds:N0} мс");
        }
    }

    // --views-cost — цена видов календаря при первом показе раздела: построение, вход в окно
    // скрытым (стили и привязки — как у шести невидимых видов раздела) и видимым с раскладкой.
    if (args.Contains("--views-cost"))
    {
        var probeWin = new Window { Width = 1600, Height = 900 };
        var host = new Grid();
        probeWin.Content = host;
        probeWin.Show();
        Dispatcher.UIThread.RunJobs();
        foreach (var (name, make) in new (string, Func<Control>)[]
                 {
                     ("список",    () => new SmartBase.Views.ConstructorDb.Calendar.ListView()),
                     ("журнал",    () => new SmartBase.Views.ConstructorDb.Calendar.JournalView()),
                     ("день",      () => new SmartBase.Views.ConstructorDb.Calendar.DayView()),
                     ("неделя",    () => new SmartBase.Views.ConstructorDb.Calendar.WeekView()),
                     ("месяц",     () => new SmartBase.Views.ConstructorDb.Calendar.MonthView()),
                     ("год",       () => new SmartBase.Views.ConstructorDb.Calendar.YearView()),
                     ("диаграмма", () => new SmartBase.Views.ConstructorDb.Calendar.DiagramView()),
                     ("раздел",    () => new SmartBase.Views.ConstructorDb.Calendar.CalendarControllerView()),
                 })
        {
            var vsw = Stopwatch.StartNew();
            var hidden = make();
            var build = vsw.Elapsed.TotalMilliseconds;
            hidden.IsVisible = false;
            vsw.Restart();
            host.Children.Add(hidden);
            probeWin.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var attachHidden = vsw.Elapsed.TotalMilliseconds;
            vsw.Restart();
            hidden.IsVisible = true;
            probeWin.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var show = vsw.Elapsed.TotalMilliseconds;
            host.Children.Remove(hidden);
            Console.WriteLine($"  вид {name}: построение {build:N0} мс, вход скрытым {attachHidden:N0} мс, показ {show:N0} мс");
        }
        probeWin.Close();
    }

    Step("старт: построение окна");
    var sw = Stopwatch.StartNew();
    main = new MainWindow(workspace) { Width = 1600, Height = 900 };
    var ctorMs = sw.Elapsed.TotalMilliseconds;
    SetScale(main, scale);
    main.Show();
    Console.WriteLine($"  Окно: конструктор {ctorMs:N0} мс, показ {sw.Elapsed.TotalMilliseconds - ctorMs:N0} мс");

    Step("старт: подключение баз и загрузки разделов");
    await Settle(quietSec: 3, capSec: 180);
    Console.WriteLine($"  Старт затих к {clock.Elapsed.TotalSeconds:0.0} с; смонтировано баз {workspace.Mounted.Count} из {registry.GetAll().Count}");
    if (only == "start") return;
    if (only == "frames") { await CalendarFrames(); return; }
    if (only == "calops") { await CalendarOps(); return; }
    if (only == "tableops") { await TableOps(); return; }
    if (only == "sidebarops") { await SidebarOps(); return; }
    if (only == "sidebars")
    {
        // Дерево после старта: базы подключались в фоне, строки заглушек переносились на
        // подключенные базы (SidebarRow.Rebind) — без повторного чтения дерева.
        foreach (var name in new[] { "ConstructorSidebar", "CalendarSidebar" })
        {
            var sidebar = main.FindControl<Control>(name)!;
            sidebar.IsVisible = true;
            main.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var faults = SidebarLayoutFaults(sidebar);
            Console.WriteLine(faults.Count == 0 ? $"  {name} после старта: строки разложены по своим данным"
                                                : $"  {name} после старта: НЕ ПРОШЛО: {string.Join("; ", faults)}");
            sidebar.IsVisible = false;
            main.UpdateLayout();
        }

        // Цена первого показа боковых панелей разделов: перечитать дерево, показать, разложить.
        // После каждого показа — разложены ли строки по своим данным (слот стрелки, отступ).
        foreach (var name in new[] { "ConstructorSidebar", "CalendarSidebar", "TasksSidebar", "LibrarySidebar" })
        {
            var sidebar = main.FindControl<Control>(name)!;
            for (var pass = 1; pass <= 3; pass++)
            {
                Step($"боковая панель: {name}, показ {pass}");
                await Task.Delay(300);
                var ssw = Stopwatch.StartNew();
                sidebar.GetType().GetMethod("Refresh", Type.EmptyTypes)!.Invoke(sidebar, null);
                var refresh = ssw.Elapsed.TotalMilliseconds;
                sidebar.IsVisible = true;
                main.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var show = ssw.Elapsed.TotalMilliseconds - refresh;
                var rows = sidebar.GetVisualDescendants().OfType<ContentPresenter>().Count(c => c.Content is SmartBase.Controls.Components.SidebarRow);
                Console.WriteLine($"  {name}, показ {pass}: перечитать {refresh:N0} мс, показать и разложить {show:N0} мс, строк {rows}");
                var faults = SidebarLayoutFaults(sidebar);
                Console.WriteLine(faults.Count == 0 ? "    строки разложены по своим данным"
                                                    : $"    НЕ ПРОШЛО: {string.Join("; ", faults)}");
                sidebar.IsVisible = false;
                main.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
        }
        return;
    }

    if (Want("modules"))
    foreach (var module in new[] { "calendar", "tasks", "library", "notes", "profile", "settings", "extensions", "constructor" })
    {
        Step($"раздел «{module}»");
        ShowModule(module);
        await Settle(quietSec: 1.5, capSec: 90);
    }

    // Календарь: все виды (0 — день … 6 — диаграмма), затем обратно к месяцу.
    if (Want("calendar")) ShowModule("calendar");
    var calendar = CalendarModule();
    // --repeat N — обход видов N раз: первый показ (построение вида, холодный кеш) отдельно
    // от повторных.
    if (Want("calendar"))
    for (var round = 1; round <= repeat; round++)
    foreach (var view in new[] { 0, 1, 2, 3, 5, 6, 4 })
    {
        Step(repeat > 1 ? $"календарь: вид {view}, проход {round}" : $"календарь: вид {view}");
        Call(calendar, "SwitchView", view);
        await Settle(quietSec: 1.5, capSec: 90);
    }

    // Задачи: пункты фильтра навбара по очереди — как выбор пункта человеком.
    if (Want("tasks")) { Step("раздел «tasks»"); ShowModule("tasks"); await Settle(quietSec: 1.5, capSec: 90); }
    var taskFilters = Module("TasksModule").FindControl<Control>("NavbarControl")!
                          .FindControl<Avalonia.Controls.Primitives.SelectingItemsControl>("FilterSelector")!;
    if (Want("tasks"))
    for (var i = 0; i < taskFilters.ItemCount; i++)
    {
        Step($"задачи: фильтр {i + 1} из {taskFilters.ItemCount}");
        taskFilters.SelectedIndex = i;
        await Settle(quietSec: 1.5, capSec: 90);
    }

    // Конструктор: каждая коллекция каждой базы, затем страница первой записи.
    if (Want("constructor")) ShowModule("constructor");
    if (Want("constructor"))
    foreach (var db in workspace.Mounted.ToList())
        foreach (var col in db.Constructor.GetAllCollections())
        {
            var cid = col.Id!.Value;
            var label = $"«{MemorySink.Trim(col.Name, 24)}» ({db.Name})";
            Step($"коллекция {label}");
            OpenCollection(db, cid);
            await Settle(quietSec: 1.5, capSec: 120);

            var first = db.Constructor.GetRecordsPageAfter(cid, null, null, 1);
            if (first.Count == 0) continue;
            Step($"запись {label}");
            Call(main, "OpenRecordPanel", db.DbId, first[0].Id!.Value);
            await Settle(quietSec: 1.5, capSec: 60);
        }

    // Настройки и профиль: каждая категория через пункт сайдбара.
    if (Want("settings")) ShowModule("settings");
    var settingsSidebar = main.FindControl<Control>("SettingsSidebar")!;
    if (Want("settings"))
    foreach (var c in SmartBase.Settings.SettingsRegistry.All)
    {
        Step($"настройки: {c.Label}");
        Call(settingsSidebar, "Select", c.Key);
        await Settle(quietSec: 1.5, capSec: 60);
    }
    if (Want("profile")) ShowModule("profile");
    var profileSidebar = main.FindControl<Control>("ProfileSidebar")!;
    if (Want("profile"))
    foreach (var c in SmartBase.Settings.ProfileRegistry.All)
    {
        Step($"профиль: {c.Label}");
        Call(profileSidebar, "Select", c.Key);
        await Settle(quietSec: 1.5, capSec: 60);
    }
    Step("конец");
}

// Кадры календаря: неделя и день на заведенной неделе (6–12 июля 2026), прокрутка к 8:00.
async Task CalendarFrames()
{
    var outDir = Arg("--frames") ?? Path.Combine(dataDir, "frames");
    var refDir = Arg("--ref") ?? Path.Combine(home, "TestData", "regress");
    Directory.CreateDirectory(outDir);
    Directory.CreateDirectory(refDir);
    var tag = $"x{scale:0}";
    var worst = 0;

    ShowModule("calendar");
    var calendar = CalendarModule();
    SetField(calendar, "_currentDate", new DateTime(2026, 7, 8));
    SetField(calendar, "_weekCentered", false);
    foreach (var (view, name, ctrl) in new[] { (3, "week", "WeekViewCtrl"), (2, "day", "DayViewCtrl") })
    {
        Step($"кадры: {name}");
        Call(calendar, "SwitchView", view);
        await Settle(quietSec: 1, capSec: 30);
        var viewCtrl = CalView<Control>(calendar, ctrl);
        viewCtrl.FindControl<ScrollViewer>("TimeScroll")!.Offset = new Vector(0, 8 * 60);
        await Frame($"cal-{name}-{tag}");

        if (args.Contains("--dump")) DumpAll(viewCtrl);
        var p = EntryCenter(viewCtrl, "Планерка отдела");
        main.MouseMove(p);
        main.MouseDown(p, MouseButton.Left);
        main.MouseUp(p, MouseButton.Left);
        await Frame($"cal-{name}-selected-{tag}");

        main.MouseMove(EntryCenter(viewCtrl, "Кофе"));
        await Frame($"cal-{name}-hover-{tag}");
        main.MouseMove(new Point(2, 2));

        // Полоса «весь день»: наведение на «Конференцию», выделение «Командировки».
        var bar = viewCtrl.FindControl<SmartBase.Controls.Components.AllDayBar>("AllDayBarCtrl")!;
        main.MouseMove(bar.TranslatePoint(bar.EventCenter("Конференция")!.Value, main)!.Value);
        await Frame($"cal-{name}-allday-hover-{tag}");
        var trip = bar.TranslatePoint(bar.EventCenter("Командировка")!.Value, main)!.Value;
        main.MouseMove(trip);
        main.MouseDown(trip, MouseButton.Left);
        main.MouseUp(trip, MouseButton.Left);
        main.MouseMove(new Point(2, 2));
        await Frame($"cal-{name}-allday-selected-{tag}");
        main.MouseDown(new Point(700, 880), MouseButton.Left);   // снять выделение нажатием мимо
        main.MouseUp(new Point(700, 880), MouseButton.Left);
    }

    // «Месяц»: июль 2026 — плашки, полосы через края недель, наведение и выделение.
    Step("кадры: month");
    SetField(calendar, "_currentDate", new DateTime(2026, 7, 8));
    Call(calendar, "SwitchView", 4);
    await Settle(quietSec: 1, capSec: 30);
    var month = CalView<MonthView>(calendar, "MonthViewCtrl");
    await Frame($"cal-month-{tag}");
    main.MouseMove(month.TranslatePoint(month.EventCenter("Итоги недели")!.Value, main)!.Value);
    await Frame($"cal-month-hover-{tag}");
    main.MouseMove(month.TranslatePoint(month.EventCenter("Конференция")!.Value, main)!.Value);
    await Frame($"cal-month-bar-hover-{tag}");
    var call = month.TranslatePoint(month.EventCenter("Планерка отдела")!.Value, main)!.Value;
    main.MouseMove(call);
    main.MouseDown(call, MouseButton.Left);
    main.MouseUp(call, MouseButton.Left);
    main.MouseMove(new Point(2, 2));
    await Frame($"cal-month-selected-{tag}");
    main.MouseDown(new Point(700, 880), MouseButton.Left);
    main.MouseUp(new Point(700, 880), MouseButton.Left);
    // Диаграмма: июль 2026 — группы свернуты (как по умолчанию), затем раскрыта коллекция с
    // заведенной неделей; выделение записи и наведение на строку.
    Step("кадры: diagram");
    SetField(calendar, "_currentDate", new DateTime(2026, 7, 8));
    Call(calendar, "SwitchView", 6);
    await Settle(quietSec: 1, capSec: 30);
    var diagram = CalView<Control>(calendar, "DiagramViewCtrl");
    await Frame($"cal-diagram-{tag}");
    var seededCol = def.Constructor.GetAllCollections()
                       .First(c => def.Constructor.GetFields(c.Id!.Value).Any(f => f.FieldType == "date" && f.IsCalStart)).Id!.Value;
    ((System.Collections.IDictionary)Get(diagram, "_collapsed")!)[(def.DbId, seededCol)] = false;
    Call(diagram, "BuildGroups");
    Call(diagram, "BuildAll");
    await Settle(quietSec: 0.5, capSec: 10);
    await Frame($"cal-diagram-open-{tag}");
    var rowH = (double)diagram.GetType().GetProperty("RowH", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    var pane = diagram.FindControl<Control>("TimelinePane")!;
    foreach (var b in (System.Collections.IEnumerable)Get(diagram, "_barEntries")!)
    {
        var ev = (SmartBase.Core.Models.CalendarRecord)Get(b, "Ev")!;
        if (ev.Title != "Конференция") continue;
        var barAt = pane.TranslatePoint(new Point(((double)Get(b, "X1")! + (double)Get(b, "X2")!) / 2, (double)Get(b, "Y")! + rowH / 2), main)!.Value;
        main.MouseMove(barAt);
        main.MouseDown(barAt, MouseButton.Left);
        main.MouseUp(barAt, MouseButton.Left);
        break;
    }
    await Frame($"cal-diagram-selected-{tag}");
    main.MouseDown(new Point(700, 880), MouseButton.Left);
    main.MouseUp(new Point(700, 880), MouseButton.Left);
    main.MouseMove(new Point(120, 300));
    await Frame($"cal-diagram-hover-{tag}");
    main.MouseMove(new Point(2, 2));

    // «Год»: 2027-й целиком (без сегодняшнего дня — кадр постоянный).
    Step("кадры: year");
    SetField(calendar, "_currentDate", new DateTime(2027, 3, 1));
    Call(calendar, "SwitchView", 5);
    await Settle(quietSec: 1, capSec: 30);
    await Frame($"cal-year-{tag}");

    // Поведение «Года»: нажатие по числу — список событий дня под ячейкой, двойное — «Месяц».
    // 10 февраля 2027 (12 событий): февраль начинается с понедельника — третья колонка, вторая
    // строка чисел.
    var yearView = CalView<Control>(calendar, "YearViewCtrl");
    var feb = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(yearView)
                  .OfType<SmartBase.Views.ConstructorDb.Calendar.YearMonthCard>().First(c => c.Month == 2);
    var cellCenter = new Point(Math.Round((YearMonthCard.CardW - YearMonthCard.GridW) / 2) + 2.5 * YearMonthCard.CellW,
                               YearMonthCard.TitleH + 2.5 * YearMonthCard.Dch);
    var at = feb.TranslatePoint(cellCenter, main)!.Value;
    main.MouseMove(at);
    var cursorOverDay = feb.Cursor is null ? "обычный" : "рука";
    main.MouseDown(at, MouseButton.Left);
    main.MouseUp(at, MouseButton.Left);
    await Settle(quietSec: 1, capSec: 30);
    var popup = yearView.FindControl<Avalonia.Controls.Primitives.Popup>("DayPopup")!;
    var list  = yearView.FindControl<StackPanel>("DayPopupList")!;
    var title = yearView.FindControl<TextBlock>("DayPopupTitle")!.Text;
    Console.WriteLine($"  проверка «Год»: нажатие по 10.02 — список {(popup.IsOpen ? "открыт" : "НЕ ОТКРЫТ")}, " +
                      $"«{title}», строк {list.Children.Count}, курсор над числом {cursorOverDay}");
    popup.IsOpen = false;
    await Task.Delay(600);   // мимо времени двойного нажатия
    // Двойное — по названию месяца (по числу первое нажатие открывает список дня, как и прежде).
    var titleAt = feb.TranslatePoint(new Point(20, 16), main)!.Value;
    main.MouseMove(titleAt);
    main.MouseDown(titleAt, MouseButton.Left);
    main.MouseUp(titleAt, MouseButton.Left);
    main.MouseDown(titleAt, MouseButton.Left);
    main.MouseUp(titleAt, MouseButton.Left);
    await Settle(quietSec: 1, capSec: 30);
    Console.WriteLine($"  проверка «Год»: двойное нажатие по названию февраля — вид {Get(calendar, "_currentViewIndex")} (4 — «Месяц»), " +
                      $"дата {(DateTime)Get(calendar, "_currentDate")!:dd.MM.yyyy}");

    // Перенос мышью на соседний день: в «Месяце» — плашка «Итоги недели» (пт → чт), в «Неделе» —
    // блок «Кофе» (ср → чт); проверка — дата события в базе стенда.
    DateTime StartOf(string t) => def.Constructor.GetCalendarRecordsByRange(new DateTime(2026, 6, 1), new DateTime(2026, 9, 1))
                                     .First(r => r.Title == t).StartDt;
    async Task DragBy(Point from, double dx)
    {
        main!.MouseMove(from);
        main.MouseDown(from, MouseButton.Left);
        main.MouseMove(new Point(from.X + dx / 3, from.Y));
        main.MouseMove(new Point(from.X + dx, from.Y));
        main.MouseUp(new Point(from.X + dx, from.Y), MouseButton.Left);
        await Settle(quietSec: 1, capSec: 30);
    }
    SetField(calendar, "_currentDate", new DateTime(2026, 7, 8));
    Call(calendar, "SwitchView", 4);
    await Settle(quietSec: 1, capSec: 30);
    var monthView = CalView<MonthView>(calendar, "MonthViewCtrl");
    var before = StartOf("Итоги недели");
    await DragBy(monthView.TranslatePoint(monthView.EventCenter("Итоги недели")!.Value, main)!.Value, -monthView.Bounds.Width / 7);
    Console.WriteLine($"  проверка «Месяц»: перенос «Итоги недели» {before:dd.MM HH:mm} → {StartOf("Итоги недели"):dd.MM HH:mm} (ждали 09.07 16:00)");

    Call(calendar, "SwitchView", 3);
    await Settle(quietSec: 1, capSec: 30);
    var weekView = CalView<Control>(calendar, "WeekViewCtrl");
    weekView.FindControl<ScrollViewer>("TimeScroll")!.Offset = new Vector(0, 8 * 60);
    await Settle(quietSec: 0.5, capSec: 10);
    var colWidth = weekView.FindControl<Control>("WeekColumns")!.Bounds.Width / 7;
    before = StartOf("Кофе");
    await DragBy(EntryCenter(weekView, "Кофе"), colWidth);
    Console.WriteLine($"  проверка «Неделя»: перенос «Кофе» {before:dd.MM HH:mm} → {StartOf("Кофе"):dd.MM HH:mm} (ждали 09.07 08:00)");
    var weekBar = weekView.FindControl<SmartBase.Controls.Components.AllDayBar>("AllDayBarCtrl")!;
    before = StartOf("Отпуск");
    await DragBy(weekBar.TranslatePoint(weekBar.EventCenter("Отпуск")!.Value, main)!.Value, colWidth);
    Console.WriteLine($"  проверка «весь день»: перенос «Отпуск» {before:dd.MM} → {StartOf("Отпуск"):dd.MM} (ждали 10.07)");

    Console.WriteLine($"  Кадры календаря: наибольшее различие с допуском {worst} пикселей");

    async Task Frame(string name)
    {
        for (var i = 0; i < 4; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(30); }
        var file = Path.Combine(outDir, name + ".png");
        Frames.Save(main!.CaptureRenderedFrame(), file);
        var reference = Path.Combine(refDir, name + ".png");
        if (!File.Exists(reference)) { File.Copy(file, reference); Console.WriteLine($"  {name}: эталон создан"); return; }
        // Различия — по квадратам 20 × 20 пикселей кадра: где именно разошлось.
        var (strict, loose, diff, bands) = Frames.Compare(Frames.Load(reference), Frames.Load(file), 16,
                                                          (x, y) => $"x {x / 20 * 20}, y {y / 20 * 20}");
        Frames.Save(diff, Path.Combine(outDir, name + "-diff.png"));
        // Увеличенные вырезки блока «Планерка отдела» в среду — глазами видно, в чем различие.
        if (name.StartsWith("cal-week", StringComparison.Ordinal) && loose > 0)
        {
            Frames.SaveCrop(Frames.Load(reference), 698, 180, 180, 66, scale, Path.Combine(outDir, name + "-crop-ref.png"));
            Frames.SaveCrop(Frames.Load(file), 698, 180, 180, 66, scale, Path.Combine(outDir, name + "-crop-new.png"));
        }
        worst = Math.Max(worst, loose);
        Console.WriteLine($"  {name}: различий строго {strict}, с допуском {loose}" +
                          (strict > 0 ? "; места: " + string.Join("; ", bands.OrderByDescending(b => b.Count).Take(4).Select(b => $"{b.Name} ({b.Count})")) : ""));
    }
}

// Операции календаря на больших базах (--only calops): дублирование, удаление, перенос, растяжение
// и создание в «Неделе», «Дне», «Месяце», Диаграмме и «Журнале» — на базе 10 млн (1270 событий в
// день) и на wide1000 (запись с 1000 свойств). По каждой операции: сколько ждать результата, самая
// длинная пауза UI-потока, была ли полная перезагрузка вида, осталась ли прокрутка на месте и что
// выделено. Окна подтверждения и новой записи стенд заполняет сам. Созданное он удаляет, перенесенное
// возвращает — база стенда после прогона прежняя.
async Task CalendarOps()
{
    var big  = workspace.Mounted.First(d => d.Name == "test_database");
    var wide = workspace.Mounted.FirstOrDefault(d => d.Name == "wide1000");
    ShowModule("calendar");
    await Settle(quietSec: 1, capSec: 60);
    var calendar = CalendarModule();
    var day      = new DateTime(2026, 9, 24);
    var failures = new List<string>();
    var startIds = workspace.Mounted.ToDictionary(d => d.DbId, MaxRecordId);

    Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
    {
        if (w is MainWindow) return;
        Dispatcher.UIThread.Post(() => AnswerDialog(w), DispatcherPriority.Background);
    });

    // ── «Неделя» 21–27.09.2026: часовая сетка на базе 10 млн, полоса «весь день» на wide1000 ──
    SetField(calendar, "_currentDate", day);
    SetField(calendar, "_weekCentered", false);
    Call(calendar, "SwitchView", 3);
    await Settle(1, 60);
    var week = CalView<Control>(calendar, "WeekViewCtrl");
    week.FindControl<ScrollViewer>("TimeScroll")!.Offset = new Vector(0, 10 * 60);
    await Settle(0.5, 10);
    await HourGridOps("неделя", week, big);
    if (wide is not null) await AllDayOps("неделя", week, wide);

    // ── «День» 24.09.2026 ──
    Call(calendar, "SwitchView", 2);
    await Settle(1, 60);
    var dayView = CalView<Control>(calendar, "DayViewCtrl");
    dayView.FindControl<ScrollViewer>("TimeScroll")!.Offset = new Vector(0, 10 * 60);
    await Settle(0.5, 10);
    await HourGridOps("день", dayView, big);

    // ── «Месяц» сентябрь 2026: плашка базы 10 млн ──
    Call(calendar, "SwitchView", 4);
    await Settle(1, 60);
    var month = CalView<MonthView>(calendar, "MonthViewCtrl");
    {
        // Видимая плашка или полоса однодневного события (в переполненном дне часть уходит в
        // «+N еще»; на wide1000 полосы «весь день» занимают все строки ячеек), база 10 млн — первой.
        var ev = ((IEnumerable<SmartBase.Core.Models.CalendarRecord>)Get(calendar, "_gridRecords")!)
                 .Where(r => r.StartDt.Date == r.EndDt.Date && r.StartDt.Day < 20
                             && r.StartDt.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday or DayOfWeek.Friday)
                             && month.EventCenter(r.Title) is not null)
                 .OrderBy(r => r.DbId == big.DbId ? 0 : 1).First();
        var mdb = workspace.Mounted.First(d => d.DbId == ev.DbId);
        Point At() => month.TranslatePoint(month.EventCenter(ev.Title)!.Value, main)!.Value;
        await Click(At());
        Console.WriteLine($"    месяц: цель «{ev.Title}» ({mdb.Name}, {ev.StartDt:dd.MM HH:mm}, весь день {ev.AllDay}) в точке {At()}, " +
                          $"под указателем {main.InputHitTest(At())?.GetType().Name}, выделено {SelectedOf(month).Count}");
        await DupDelete($"месяц ({mdb.Name})", month, ev, mdb);
        var start = DbDates(mdb, ev).Start;
        await Op("месяц: перенос на следующий день", month, () => Drag(At(), month.Bounds.Width / 7, 0),
                 () => DbDates(mdb, ev).Start.Date == start.Date.AddDays(1), ev);
        await Op("месяц: перенос обратно", month, () => Drag(At(), -month.Bounds.Width / 7, 0),
                 () => DbDates(mdb, ev).Start.Date == start.Date, ev);
    }

    // ── Диаграмма сентябрь 2026: группа wide1000 — растяжение за правый край ──
    // Многодневное событие базы 10 млн в начале месяца (первые строки группы) — растягивать бар.
    var bigCol   = big.Constructor.GetAllCollections().First(c => big.Constructor.GetFields(c.Id!.Value).Any(f => f.IsCalStart)).Id!.Value;
    var longEv   = big.Constructor.AddCalendarRecord(new SmartBase.Core.Models.CalendarRecord
    {
        CollectionId = bigCol, Title = "Стенд: многодневное",
        StartDt = new DateTime(2026, 9, 1), EndDt = new DateTime(2026, 9, 3, 12, 0, 0),
    });
    Call(calendar, "SwitchView", 6);
    await Settle(1, 60);
    var diagram = CalView<Control>(calendar, "DiagramViewCtrl");
    foreach (var db in wide is null ? [big] : new[] { big, wide })
    {
        var colId  = db.Constructor.GetAllCollections().First(c => db.Constructor.GetFields(c.Id!.Value).Any(f => f.IsCalStart)).Id!.Value;
        var groups = (System.Collections.IDictionary)Get(diagram, "_collapsed")!;
        groups[(db.DbId, colId)] = false;
        Call(diagram, "BuildGroups");
        Call(diagram, "BuildAll");
        await Settle(0.5, 10);
        var entry = DiagramEntries(diagram).OrderBy(b => b.IsDot ? 1 : 0).First(b => b.Ev.DbId == db.DbId);
        var rowsScroll = diagram.FindControl<ScrollViewer>("RowsScroll")!;
        rowsScroll.Offset = new Vector(0, Math.Max(0, entry.Y - 120));
        await Settle(0.5, 10);
        var ev = entry.Ev;
        await Click(DiagramPoint(diagram, ev, 0.5));
        await DupDelete($"диаграмма ({db.Name})", diagram, ev, db);
        if (!DiagramEntries(diagram).First(b => b.Ev.Id == ev.Id).IsDot)
        {
            // Шкала привязывает конец к границе дня — сверка с допуском, исходные даты (с
            // секундами) в конце возвращаются записью в базу.
            var dayW  = (double)Get(diagram, "_lastTimelineW")! / (int)Get(diagram, "_rangeDays")!;
            var (start, end) = DbDates(db, ev);
            await Op($"диаграмма ({db.Name}): растяжение на день", diagram,
                     () => Drag(DiagramPoint(diagram, ev, 1, -2), dayW, 0), () => DbDates(db, ev).End >= end.AddHours(12), ev);
            var stretched = DbDates(db, ev).End;
            await Op($"диаграмма ({db.Name}): растяжение обратно", diagram,
                     () => Drag(DiagramPoint(diagram, ev, 1, -2), -dayW, 0), () => DbDates(db, ev).End <= stretched.AddHours(-12), ev);
            RestoreDates(db, ev, start, end);
        }
        else Console.WriteLine($"  диаграмма ({db.Name}): событие — точка на шкале месяца, растягивать нечего");
    }
    big.Constructor.DeleteCalendarRecord(longEv.Id!.Value);

    // ── «Журнал»: переход в вид — к сегодня; дублирование и удаление строки базы 10 млн ──
    Call(calendar, "SwitchView", 1);
    await Settle(1, 60);
    var journal = CalView<Control>(calendar, "JournalViewCtrl");
    var jScroll = journal.FindControl<ScrollViewer>("JournalScroll")!;
    jScroll.Offset = new Vector(0, Math.Max(0, jScroll.Offset.Y - 20000));
    await Settle(0.5, 10);
    Call(calendar, "SwitchView", 4);
    await Settle(1, 60);
    Step("операции: журнал — переход в вид");
    Call(calendar, "SwitchView", 1);
    await Settle(1, 60);
    var list     = Get(journal, "_list")!;
    var todayRow = (int?)list.GetType().GetProperty("TodayHeaderRow")!.GetValue(list);
    var todayY   = todayRow is int tr
        ? (double)journal.GetType().GetMethod("OffsetOfRow", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [tr])!
        : -1;
    var onToday  = Math.Abs(jScroll.Offset.Y - Math.Min(todayY, jScroll.Extent.Height - jScroll.Viewport.Height)) < 1;
    Console.WriteLine($"  журнал: после перехода в вид прокрутка {jScroll.Offset.Y:N0}, строка сегодня {todayY:N0} — {(onToday ? "на сегодня" : "НЕ НА СЕГОДНЯ")}");
    if (!onToday) failures.Add("журнал: переход к сегодня");
    {
        // Строки дня заполняются чтением по требованию — ждем, пока появятся события.
        var wait = Stopwatch.StartNew();
        while (JournalRows(journal).Count == 0 && wait.Elapsed.TotalSeconds < 10) await Task.Delay(50);
        var rows = JournalRows(journal);
        var row  = rows.OrderBy(r => r.Ev.DbId == big.DbId ? 0 : 1).First();
        var jdb  = workspace.Mounted.First(d => d.DbId == row.Ev.DbId);
        await Click(row.At);
        await DupDelete($"журнал ({jdb.Name})", journal, row.Ev, jdb);
    }

    // Уборка: записи, созданные стендом и не удаленные (проверка не прошла), — прочь.
    foreach (var db in workspace.Mounted)
    {
        var left = Query(db, $"SELECT Id FROM db_records WHERE Id > {startIds[db.DbId]}").Select(r => Convert.ToInt32(r[0])).ToList();
        if (left.Count == 0) continue;
        db.Constructor.DeleteRecordsData(left, log: false);
        Console.WriteLine($"  уборка: в «{db.Name}» удалено оставшихся записей стенда {left.Count}");
        failures.Add($"остались записи в «{db.Name}»");
    }

    Console.WriteLine();
    Console.WriteLine(failures.Count == 0 ? "  Операции календаря: все проверки прошли"
                                          : $"  Операции календаря: НЕ ПРОШЛИ {failures.Count}: {string.Join("; ", failures)}");

    // ── Сценарии ──

    // Часовая сетка: дублирование и удаление копии, перенос на час и обратно, растяжение на
    // полчаса и обратно, создание кнопкой «+» и удаление созданного.
    async Task HourGridOps(string view, Control ctrl, DatabaseManager db)
    {
        var ev = PickHourEvent(ctrl, db.DbId, day);
        await Click(EventPoint(ctrl, ev, 0.5));
        await DupDelete(view, ctrl, ev, db);

        // Сетка привязывает время к 15 минутам — сверка с допуском, а в конце исходные даты
        // (с секундами) возвращаются записью в базу.
        var (start, end) = DbDates(db, ev);
        static bool Near(DateTime a, DateTime b) => Math.Abs((a - b).TotalMinutes) < 15;
        await Op($"{view}: перенос на час", ctrl, () => Drag(EventPoint(ctrl, ev, 0.5), 0, 60),
                 () => Near(DbDates(db, ev).Start, start.AddHours(1)), ev);
        await Op($"{view}: перенос обратно", ctrl, () => Drag(EventPoint(ctrl, ev, 0.5), 0, -60),
                 () => Near(DbDates(db, ev).Start, start), ev);
        var end2 = DbDates(db, ev).End;
        await Op($"{view}: растяжение на полчаса", ctrl, () => Drag(EventPoint(ctrl, ev, 1, -2), 0, 32),
                 () => DbDates(db, ev).End >= end2.AddMinutes(10), ev);
        var end3 = DbDates(db, ev).End;
        await Op($"{view}: растяжение обратно", ctrl, () => Drag(EventPoint(ctrl, ev, 1, -2), 0, -32),
                 () => DbDates(db, ev).End <= end3.AddMinutes(-10), ev);
        RestoreDates(db, ev, start, end);

        var maxId = MaxRecordId(db);
        SmartBase.Core.Models.CalendarRecord? made = null;
        await Op($"{view}: создание (окно записи)", ctrl,
                 () => { _ = Call(calendar, "OpenAddDialog", day.AddHours(10), (TimeSpan?)TimeSpan.FromHours(10.5)); return Task.CompletedTask; },
                 () => (made = SelectedOf(ctrl).FirstOrDefault(r => r.DbId == db.DbId && r.Id > maxId)) is not null, null);
        if (made is not null)
            await Op($"{view}: удаление созданной", ctrl, () => Key(PhysicalKey.Delete),
                     () => !RecordExists(db, made.Id!.Value), null);
    }

    // Полоса «весь день»: дублирование плашки записи с 1000 свойств и удаление копии, перенос на
    // день и обратно.
    async Task AllDayOps(string view, Control ctrl, DatabaseManager db)
    {
        var bar = ctrl.FindControl<SmartBase.Controls.Components.AllDayBar>("AllDayBarCtrl")!;
        var ev  = ((IEnumerable<SmartBase.Core.Models.CalendarRecord>)Get(calendar, "_gridRecords")!)
                  .First(r => r.DbId == db.DbId && r.StartDt.Date == day
                              && bar.EventCenter(r.Title) is { } c && c.Y > 4 && c.Y < bar.Bounds.Height - 8);
        // Плашка в видимой части полосы: после переноса она может встать в строку ниже края —
        // полоса прокручивается к ней, как прокрутил бы человек.
        Point At()
        {
            var c  = bar.EventCenter(ev.Title)!.Value;
            var sv = bar.FindControl<ScrollViewer>("EventsScroll")!;
            if (c.Y < 4 || c.Y > bar.Bounds.Height - 8)
            {
                sv.Offset = new Vector(0, Math.Max(0, sv.Offset.Y + c.Y - bar.Bounds.Height / 2));
                bar.UpdateLayout();
                c = bar.EventCenter(ev.Title)!.Value;
            }
            return bar.TranslatePoint(c, main)!.Value;
        }
        await Click(At());
        await DupDelete($"{view}, «весь день» (1000 свойств)", ctrl, ev, db, values: true);
        var colW  = ctrl.FindControl<Control>("WeekColumns")!.Bounds.Width / 7;
        var start = DbDates(db, ev).Start;
        await Op($"{view}, «весь день»: перенос на день", ctrl, () => Drag(At(), colW, 0),
                 () => DbDates(db, ev).Start == start.AddDays(1), ev);
        await Op($"{view}, «весь день»: перенос обратно", ctrl, () => Drag(At(), -colW, 0),
                 () => DbDates(db, ev).Start == start, ev);
    }

    // Выделенная запись: Ctrl+D — копия выделена вместо исходной; Delete — копии нет в базе.
    async Task DupDelete(string view, Control ctrl, SmartBase.Core.Models.CalendarRecord ev, DatabaseManager db, bool values = false)
    {
        var maxId = MaxRecordId(db);
        SmartBase.Core.Models.CalendarRecord? copy = null;
        await Op($"{view}: дублирование", ctrl, () => Key(PhysicalKey.D, RawInputModifiers.Control),
                 () => SelectedOf(ctrl) is [var c] && c.DbId == db.DbId && c.Id > maxId && (copy = c) is not null, null);
        if (copy is null) return;
        if (values)
        {
            var n = db.Constructor.GetFieldValuesBatch([copy.Id!.Value]).GetValueOrDefault(copy.Id!.Value)?.Count ?? 0;
            var m = db.Constructor.GetFieldValuesBatch([ev.Id!.Value]).GetValueOrDefault(ev.Id!.Value)?.Count ?? 0;
            Console.WriteLine($"    значений у копии {n} из {m}");
            if (n != m) failures.Add($"{view}: значения копии");
        }
        await Op($"{view}: удаление копии", ctrl, () => Key(PhysicalKey.Delete),
                 () => !RecordExists(db, copy.Id!.Value) && SelectedOf(ctrl).Count == 0, null);
        // Исходную — снова выделить для следующих операций (если видна).
        if (EventPointAny(ctrl, ev) is { } again && again != default) await Click(again);
    }

    // Одна операция: действие, ожидание результата, отчет.
    async Task Op(string name, Control ctrl, Func<Task> act, Func<bool> done,
                  SmartBase.Core.Models.CalendarRecord? keepSelected)
    {
        Step($"операции: {name}");
        var stepName = probe.Step;
        var loads    = FullLoads();
        var before   = ScrollOf(ctrl);
        var sw       = Stopwatch.StartNew();
        await act();
        while (!done() && sw.Elapsed.TotalSeconds < 30) await Task.Delay(10);
        var ms = sw.Elapsed.TotalMilliseconds;
        var ok = done();
        await Settle(0.5, 30);
        var maxStall = probe.Stalls.Where(s => s.Step == stepName).Select(s => s.Ms).DefaultIfEmpty(0).Max();
        var reloads  = FullLoads() - loads;
        var after    = ScrollOf(ctrl);
        var jumped   = before is { } a && after is { } b && Math.Abs(a.Y - b.Y) + Math.Abs(a.X - b.X) > 0.5;
        var selected = SelectedOf(ctrl);
        // «Журнал» едет к копии, только если ее строка не видна (по замыслу) — сдвигом это не
        // считается, когда выделенная строка после операции на экране.
        var revealed = jumped && ctrl.Name == "JournalViewCtrl"
                       && JournalRows(ctrl).Any(r => selected.Any(x => x.Id == r.Ev.Id && x.DbId == r.Ev.DbId));
        if (revealed) jumped = false;
        var keptSel  = keepSelected is null || selected.Any(r => r.Id == keepSelected.Id && r.DbId == keepSelected.DbId);
        Console.WriteLine($"  {name}: {(ok ? "готово" : "НЕ ВЫШЛО")} за {ms:N0} мс; пауза UI макс {maxStall:N0} мс; " +
                          $"полных загрузок {reloads}; прокрутка {(jumped ? $"СДВИНУЛАСЬ {before} → {after}" : revealed ? "к выделенной копии" : "на месте")}; " +
                          $"выделено {selected.Count}{(keptSel ? "" : " — НЕ ТА ЗАПИСЬ")}");
        if (!ok && keepSelected is not null && workspace.Mounted.FirstOrDefault(d => d.DbId == keepSelected.DbId) is { } kdb)
            Console.WriteLine($"    даты в базе: {DbDates(kdb, keepSelected).Start:dd.MM HH:mm:ss} — {DbDates(kdb, keepSelected).End:dd.MM HH:mm:ss}");
        if (!ok || reloads > 0 || jumped || !keptSel) failures.Add(name);
    }

    // ── Помощники ──

    int FullLoads() => diagLog.Ops.ToList().Count(o => o.Name is "загрузка календаря" or "счетчики журнала календаря");

    async Task Click(Point p)
    {
        main!.MouseMove(p);
        main.MouseDown(p, MouseButton.Left);
        main.MouseUp(p, MouseButton.Left);
        main.MouseMove(new Point(2, 2));
        await Task.Delay(400);   // мимо времени двойного нажатия
    }

    Task Drag(Point from, double dx, double dy)
    {
        main!.MouseMove(from);
        main.MouseDown(from, MouseButton.Left);
        main.MouseMove(new Point(from.X + dx / 3, from.Y + dy / 3));
        main.MouseMove(new Point(from.X + dx, from.Y + dy));
        main.MouseUp(new Point(from.X + dx, from.Y + dy), MouseButton.Left);
        main.MouseMove(new Point(2, 2));
        return Task.CompletedTask;
    }

    Task Key(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
    {
        main!.KeyPressQwerty(key, mods);
        main.KeyReleaseQwerty(key, mods);
        return Task.CompletedTask;
    }

    // Окна: подтверждение — «Да»; новая запись — база 10 млн, ее единственная коллекция, название.
    void AnswerDialog(Window w)
    {
        if (w is SmartBase.Controls.Dialogs.ConfirmDialog)
        {
            w.FindControl<Button>("ConfirmBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return;
        }
        if (w is SmartBase.Controls.Dialogs.RecordDialog)
        {
            var dbCombo = w.FindControl<ComboBox>("DbCombo")!;
            dbCombo.SelectedIndex = ((IList<string>)dbCombo.ItemsSource!).IndexOf("test_database");
            w.FindControl<TextBox>("TitleBox")!.Text = "Стенд: созданная запись";
            w.FindControl<Button>("SaveBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return;
        }
        Console.WriteLine($"    [окно без ответа стенда: {w.GetType().Name} «{w.Title}»]");
    }

    // Событие часовой сетки базы в день day, чей блок виден в окне прокрутки.
    SmartBase.Core.Models.CalendarRecord PickHourEvent(Control view, int dbId, DateTime d)
    {
        var scroll = view.FindControl<ScrollViewer>("TimeScroll")!;
        foreach (var entry in (System.Collections.IEnumerable)Get(view, "_eventEntries")!)
        {
            var ev   = (SmartBase.Core.Models.CalendarRecord)Get(entry, "Ev")!;
            var item = (CalendarDayCanvas.Item)Get(entry, "Btn")!;
            if (ev.DbId != dbId || ev.StartDt.Date != d || item.Bounds.Height < 30) continue;
            if (item.Bounds.Y < scroll.Offset.Y + 60 || item.Bounds.Bottom > scroll.Offset.Y + scroll.Viewport.Height - 120) continue;
            return ev;
        }
        throw new InvalidOperationException("Нет подходящего события в часовой сетке");
    }

    // Точка блока события часовой сетки: fy — доля высоты (1 — низ), dy — сдвиг в пикселях.
    Point EventPoint(Control view, SmartBase.Core.Models.CalendarRecord ev, double fy, double dy = 0)
    {
        foreach (var entry in (System.Collections.IEnumerable)Get(view, "_eventEntries")!)
        {
            var e = (SmartBase.Core.Models.CalendarRecord)Get(entry, "Ev")!;
            if (e.Id != ev.Id || e.DbId != ev.DbId) continue;
            var item = (CalendarDayCanvas.Item)Get(entry, "Btn")!;
            var b    = item.Bounds;
            return item.Owner.TranslatePoint(new Point(b.X + b.Width / 2, b.Y + b.Height * fy + dy), main!)!.Value;
        }
        throw new InvalidOperationException($"Нет блока события {ev.Id}");
    }

    Point EventPointAny(Control view, SmartBase.Core.Models.CalendarRecord ev) => view switch
    {
        MonthView m => m.TranslatePoint(m.EventCenter(ev.Title)!.Value, main!)!.Value,
        _ when view.Name == "DiagramViewCtrl" => DiagramPoint(view, ev, 0.5),
        _ when view.Name == "JournalViewCtrl" => JournalRows(view).FirstOrDefault(r => r.Ev.Id == ev.Id).At,
        _ when ev.AllDay => view.FindControl<SmartBase.Controls.Components.AllDayBar>("AllDayBarCtrl") is { } bar
                                ? bar.TranslatePoint(bar.EventCenter(ev.Title)!.Value, main!)!.Value : default,
        _ => EventPoint(view, ev, 0.5),
    };

    List<(SmartBase.Core.Models.CalendarRecord Ev, double X1, double X2, double Y, bool IsDot)> DiagramEntries(Control diagram)
    {
        var list = new List<(SmartBase.Core.Models.CalendarRecord, double, double, double, bool)>();
        foreach (var b in (System.Collections.IEnumerable)Get(diagram, "_barEntries")!)
            list.Add(((SmartBase.Core.Models.CalendarRecord)Get(b, "Ev")!, (double)Get(b, "X1")!, (double)Get(b, "X2")!,
                      (double)Get(b, "Y")!, (bool)Get(b, "IsDot")!));
        return list;
    }

    // Точка бара Диаграммы: fx — доля ширины (1 — правый край), dx — сдвиг.
    Point DiagramPoint(Control diagram, SmartBase.Core.Models.CalendarRecord ev, double fx, double dx = 0)
    {
        var e    = DiagramEntries(diagram).First(b => b.Ev.Id == ev.Id && b.Ev.DbId == ev.DbId);
        var rowH = (double)diagram.GetType().GetProperty("RowH", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var pane = diagram.FindControl<Control>("TimelinePane")!;
        return pane.TranslatePoint(new Point(e.X1 + (e.X2 - e.X1) * fx + dx, e.Y + rowH / 2), main!)!.Value;
    }

    List<(SmartBase.Core.Models.CalendarRecord Ev, Point At)> JournalRows(Control journal) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(journal).OfType<Border>()
            .Where(b => b.Tag is JournalRow { IsEvent: true, OriginalEvent: not null } && b.IsEffectivelyVisible && b.Bounds.Height > 0)
            .Select(b => (((JournalRow)b.Tag!).OriginalEvent!, b.TranslatePoint(b.Bounds.Center - b.Bounds.Position, main!)!.Value))
            .Where(r => r.Item2.Y > 150 && r.Item2.Y < 850)
            .ToList();

    List<SmartBase.Core.Models.CalendarRecord> SelectedOf(Control view)
    {
        switch (view.Name)
        {
            case "WeekViewCtrl" or "DayViewCtrl":
                return (List<SmartBase.Core.Models.CalendarRecord>)Call(view, "_SelectedAll")!;
            case "MonthViewCtrl":
                return ((System.Collections.IDictionary)Get(view, "_selections")!).Keys.Cast<SmartBase.Core.Models.CalendarRecord>().ToList();
            case "DiagramViewCtrl":
                return ((System.Collections.IEnumerable)Get(view, "_selection")!).Cast<object>()
                       .Select(b => (SmartBase.Core.Models.CalendarRecord)Get(b, "Ev")!).ToList();
            default:
                var l = Get(view, "_list");
                if (l is null) return [];
                return ((IEnumerable<JournalRow>)l.GetType().GetProperty("RealizedItems")!.GetValue(l)!)
                       .Where(i => i.IsEvent && i.IsSelected).Select(i => i.OriginalEvent!).ToList();
        }
    }

    Vector? ScrollOf(Control view) => view.Name switch
    {
        "WeekViewCtrl" or "DayViewCtrl" => view.FindControl<ScrollViewer>("TimeScroll")!.Offset,
        "DiagramViewCtrl"               => view.FindControl<ScrollViewer>("RowsScroll")!.Offset,
        "JournalViewCtrl"               => view.FindControl<ScrollViewer>("JournalScroll")!.Offset,
        _                               => null,
    };

    // Даты события в базе — строка проекции calendar_index (что покажет следующее чтение).
    (DateTime Start, DateTime End) DbDates(DatabaseManager db, SmartBase.Core.Models.CalendarRecord ev)
    {
        var rows = Query(db, $"SELECT StartIso, EndIso FROM calendar_index WHERE RecordId = {ev.Id} AND FieldId = {ev.FieldId}");
        return rows.Count == 0 ? default : (DateTime.Parse((string)rows[0][0]), DateTime.Parse((string)rows[0][1]));
    }

    // Исходные даты события — записью в базу (как правка из окна записи).
    void RestoreDates(DatabaseManager db, SmartBase.Core.Models.CalendarRecord ev, DateTime start, DateTime end)
    {
        ev.StartDt = start;
        ev.EndDt   = end;
        db.Constructor.UpdateCalendarRecord(ev);
    }

    bool RecordExists(DatabaseManager db, int id) => Query(db, $"SELECT 1 FROM db_records WHERE Id = {id}").Count > 0;

    int MaxRecordId(DatabaseManager db) => Convert.ToInt32(Query(db, "SELECT MAX(Id) FROM db_records")[0][0]);

    List<object[]> Query(DatabaseManager db, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.Path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        var rows = new List<object[]>();
        while (rd.Read()) { var r = new object[rd.FieldCount]; rd.GetValues(r); rows.Add(r); }
        return rows;
    }
}

// Операции таблицы на широкой базе (--table, по умолчанию ~/TestData/wide10m.db: 10 млн записей ×
// 1000 свойств) — так, как их запускает меню: по каждой время до результата и самая длинная пауза
// UI-потока. Окна подтверждения стенд подтверждает, окна «Отображение» и «Расположение» закрывает
// без изменений (засекая время до их показа). Итог нулевой: копия столбца и записи удаляются,
// сдвиги возвращаются.
async Task TableOps()
{
    var db      = workspace.Mounted.First(d => d.Name == tableName);
    var repo    = db.Constructor;
    var colId   = repo.GetAllCollections().First().Id!.Value;
    var module  = main!.FindControl<Control>("ConstructorModule")!;
    var table   = module.FindControl<SmartBase.Views.ConstructorDb.TableView>("TableViewCtrl")!;
    var surface = table.FindControl<SmartBase.Views.ConstructorDb.DrawnGrid.GridPanel>("RecordsGrid")!.Surface;
    var windows = new List<(string Type, double AtMs)>();
    var opClock = Stopwatch.StartNew();
    var summary = new List<(string Name, bool Ok, double Ms, double Stall)>();
    // Что делать в окне «Отображение записей» (null — закрыть без изменений).
    Func<Window, Task>? displayScript = null;

    Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
    {
        if (w is MainWindow) return;
        windows.Add((w.GetType().Name, opClock.Elapsed.TotalMilliseconds));
        Dispatcher.UIThread.Post(() => Answer(w), DispatcherPriority.Background);
    });

    ShowModule("constructor");
    await Settle(1, 60);

    bool Ready() => surface.Rows is { Count: > 1 } rows && !rows[0].IsLoading && !rows[0].IsAddRow;
    await TOp("открытие коллекции", () => { OpenCollection(db, colId); return Task.CompletedTask; }, Ready);

    var text      = repo.GetFields(colId).First(f => f.Name == "Текст").Id!.Value;
    var listField = repo.GetFields(colId).First(f => f.Name == "Список").Id!.Value;
    var idsBefore = repo.GetFields(colId).Select(f => f.Id!.Value).ToHashSet();
    int copyId    = 0;
    // --set columns,filter,records — какие группы операций (по умолчанию все).
    var opSets = Arg("--set")?.Split(',');
    bool Set(string name) => opSets is null || opSets.Contains(name);

    // ── Уборка: копии «Текст (копия)…», оставшиеся от прерванных прогонов, — удалением столбца ──
    if (Set("cleanup"))
        foreach (var leftover in repo.GetFields(colId).Where(f => f.Name.StartsWith("Текст (копия)")).ToList())
        {
            var n = await ValuesAsync(leftover.Id!.Value);
            await TOp($"удаление столбца «{leftover.Name}» ({n:N0} значений)",
                      () => (Task)Call(module, "DeletePropertiesWithConfirm", new List<int> { leftover.Id!.Value })!,
                      () => repo.GetFields(colId).All(f => f.Id != leftover.Id));
            var wal = new FileInfo(db.Path + "-wal");
            Console.WriteLine($"    журнал после удаления: {(wal.Exists ? wal.Length / 1048576 : 0):N0} МБ");
        }

    // ── Столбцы ──
    if (Set("columns"))
    await TOp("дублирование столбца «Текст»",
              () => (Task)Call(module, "DuplicateProperties", new List<int> { text })!,
              () => (copyId = repo.GetFields(colId).Select(f => f.Id!.Value).Where(id => !idsBefore.Contains(id))
                                  .DefaultIfEmpty(0).Max()) > 0);
    if (Set("columns") && copyId > 0)
    {
        var key = copyId.ToString();
        var n   = await ValuesAsync(copyId);
        // Столбец длиннее предела выделения (100 000 строк) программа не копирует, не чистит и не
        // заполняет вставкой — говорит об этом уведомлением; стенд ждет именно отказа: быстро и
        // без правок в базе.
        var rowsTotal = surface.Rows!.Count - 1;
        var refused   = rowsTotal > 100_000;
        var why       = refused ? $" — {rowsTotal:N0} строк, ждем отказа по пределу выделения" : "";
        await TOp($"копирование значений столбца ({n:N0}){why}",
                  () => { Call(table, "SelectWholeColumn", key); return table.CopyCellsAsync(); }, () => true);
        await TOpAsync($"очистка значений столбца{why}",
                  () => { Call(table, "SelectWholeColumn", key); return table.ClearCellsAsync("Очистка"); },
                  async () => refused ? await ValuesAsync(copyId) == n : await ValuesAsync(copyId) == 0);
        await TOpAsync($"вставка значений в столбец{why}",
                  () => { Call(table, "SelectWholeColumn", key); return table.PasteCellsAsync(); },
                  async () => refused ? await ValuesAsync(copyId) == n : await ValuesAsync(copyId) > 0);
        await TOp("сдвиг столбца вправо", () => { Call(table, "MoveColumn", key, false); return Task.CompletedTask; }, () => true);
        await TOp("сдвиг столбца влево",  () => { Call(table, "MoveColumn", key, true);  return Task.CompletedTask; }, () => true);
        await TOp("окно «Отображение» столбцов", () => (Task)Call(module, "OpenDisplayDialog")!, () => true);
        await TOp("удаление копии столбца",
                  () => (Task)Call(module, "DeletePropertiesWithConfirm", new List<int> { copyId })!,
                  () => repo.GetFields(colId).All(f => f.Id != copyId));
    }

    // ── Фильтр: столбец, где все значения разные, столбец из четырех значений и «Название»;
    //    поиск по базе — запросом, как его делает окно ──
    if (Set("filter"))
    {
        foreach (var (key, label) in new[] { (text.ToString(), "Текст"), (listField.ToString(), "Список"), ("__title__", "Название") })
        {
            var def   = ((List<SmartBase.ViewModels.ConstructorDb.TableColumnDef>)Get(table, "_colDefs")!).First(d => d.Key == key);
            var ready = OpsNamed("список значений фильтра");
            await TOp($"список значений фильтра «{label}»",
                      () => { Call(table, "ShowFilterFlyout", table, def); return Task.CompletedTask; },
                      () => OpsNamed("список значений фильтра") > ready);
            table.LastOpenedFilter?.Hide();
        }
        foreach (var (key, q) in new[] { (text.ToString(), "записи 777"), (text.ToString(), "нет такого"), ("__title__", "запись 4242") })
        {
            SmartBase.Database.Repositories.ConstructorRepository.ColumnValueList? found = null;
            await TOp($"поиск значений фильтра «{q}»",
                      async () => found = await repo.GetColumnValueListAsync(colId, key, search: q), () => true);
            Console.WriteLine($"    найдено {found?.Values.Count ?? 0}{(found?.More == true ? "+" : "")}");
        }
    }

    // ── Записи ──
    if (!Set("records")) { Summary(); return; }
    var first  = surface.Rows![0].RecordId;
    var maxId  = MaxRecordId(db);
    int dupId  = 0;
    await TOp("дублирование записи",
              () => (Task)Call(module, "DuplicateRecords", new List<int> { first })!,
              () => (dupId = MaxRecordId(db)) > maxId);
    if (dupId > maxId)
    {
        await TOp("сдвиг записи вниз",  () => { Call(table, "MoveRecord", dupId, false); return Task.CompletedTask; }, () => true);
        await TOp("сдвиг записи вверх", () => { Call(table, "MoveRecord", dupId, true);  return Task.CompletedTask; }, () => true);
        await TOp("окно «Расположение» записи", () => (Task)Call(module, "RelocateRecords", new List<int> { dupId })!, () => true);
        await TOp("удаление записи",
                  () => (Task)Call(module, "DeleteRecordsWithConfirm", new List<int> { dupId })!,
                  () => !RecordExists(db, dupId));
    }
    await TOp("окно «Отображение» записей", () => (Task)Call(module, "OpenRecordDisplayDialog")!, () => true, capSec: 600);

    // Работа в окне: снять галочку у первой записи, переставить ее вниз, найти запись поиском по
    // базе, сохранить — и проверить базу; затем вернуть как было тем же окном.
    {
        var rec0 = surface.Rows![0].RecordId;
        var rec1 = surface.Rows![1].RecordId;
        double searchMs = 0;
        displayScript = async w =>
        {
            await Task.Delay(300);
            var row0 = Call(w, "RowAt", 0)!;
            row0.GetType().GetProperty("Visible")!.SetValue(row0, false);
            w.FindControl<ListBox>("ListHost")!.SelectedItem = row0;
            Call(w, "MoveSelected", 1);
            var ssw = Stopwatch.StartNew();
            w.FindControl<TextBox>("SearchBox")!.Text = "Запись 4242";
            while (Get(w, "_found") is null && ssw.Elapsed.TotalSeconds < 120) await Task.Delay(20);
            searchMs = ssw.Elapsed.TotalMilliseconds;
            var found = ((int[]?)Get(w, "_found"))?.Length ?? -1;
            Console.WriteLine($"    поиск «Запись 4242» в окне: {searchMs:N0} мс, найдено {found}");
            w.FindControl<TextBox>("SearchBox")!.Text = "";
            w.FindControl<Button>("SaveBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        };
        await TOp("«Отображение» записей: скрыть, переставить, найти, сохранить",
                  () => (Task)Call(module, "OpenRecordDisplayDialog")!,
                  () => true, capSec: 600);
        var hidden = Query(db, $"SELECT Visible FROM record_display WHERE RecordId = {rec0}");
        var p0     = Convert.ToInt64(Query(db, $"SELECT Position FROM db_records WHERE Id = {rec0}")[0][0]);
        var p1     = Convert.ToInt64(Query(db, $"SELECT Position FROM db_records WHERE Id = {rec1}")[0][0]);
        Console.WriteLine(hidden.Count == 1 && Convert.ToInt64(hidden[0][0]) == 0 && p0 > p1
            ? "    в базе: первая запись скрыта и стоит после второй"
            : $"    НЕ ПРОШЛО: в базе видимость {(hidden.Count == 0 ? "по умолчанию" : hidden[0][0])}, позиции {p0} и {p1}");

        displayScript = async w =>
        {
            await Task.Delay(300);
            var row = Call(w, "RowAt", 1)!;
            row.GetType().GetProperty("Visible")!.SetValue(row, true);
            w.FindControl<ListBox>("ListHost")!.SelectedItem = row;
            Call(w, "MoveSelected", -1);
            w.FindControl<Button>("SaveBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        };
        await TOp("«Отображение» записей: вернуть как было",
                  () => (Task)Call(module, "OpenRecordDisplayDialog")!, () => true, capSec: 600);
        displayScript = null;
        var back = Query(db, $"SELECT Visible FROM record_display WHERE RecordId = {rec0}").Count == 0
                   && Convert.ToInt64(Query(db, $"SELECT Position FROM db_records WHERE Id = {rec0}")[0][0])
                    < Convert.ToInt64(Query(db, $"SELECT Position FROM db_records WHERE Id = {rec1}")[0][0]);
        Console.WriteLine(back ? "    в базе: как было" : "    НЕ ПРОШЛО: не вернулось");
    }
    Summary();

    void Summary()
    {
        Console.WriteLine();
        Console.WriteLine($"Операции таблицы на «{tableName}»:");
        foreach (var s in summary)
            Console.WriteLine($"  {(s.Ok ? "  " : "НЕ")} {s.Ms,9:N0} мс  пауза UI {s.Stall,7:N0} мс  {s.Name}");
    }

    // Одна операция: запуск, ожидание конца операции и результата в базе, отчет.
    Task TOp(string name, Func<Task> act, Func<bool> done, double capSec = 3600) =>
        TOpAsync(name, act, () => Task.FromResult(done()), capSec);

    // done — проверка итога; тяжелая (счет значений столбца на 10 млн — секунды) передается
    // асинхронной и считается в фоне. Размер журнала тоже опрашивается фоном: стенд не должен сам
    // держать UI-поток, паузы которого меряет. Время операции — до начала проверки, которая
    // подтвердила итог (сама проверка в него не входит).
    async Task TOpAsync(string name, Func<Task> act, Func<Task<bool>> done, double capSec = 3600)
    {
        Step($"таблица: {name}");
        var stepName = probe.Step;
        windows.Clear();
        opClock.Restart();
        Task task;
        try { task = act(); }
        catch (Exception ex) { task = Task.FromException(ex); }
        long walMax = 0;
        using var walStop = new CancellationTokenSource();
        var walPoll = Task.Run(async () =>
        {
            while (!walStop.IsCancellationRequested)
            {
                var walFile = new FileInfo(db.Path + "-wal");
                if (walFile.Exists) Interlocked.Exchange(ref walMax, Math.Max(Interlocked.Read(ref walMax), walFile.Length));
                try { await Task.Delay(20, walStop.Token); } catch (OperationCanceledException) { }
            }
        });
        var ok = false;
        var ms = 0.0;
        while (opClock.Elapsed.TotalSeconds < capSec)
        {
            if (task.IsCompleted)
            {
                var at = opClock.Elapsed.TotalMilliseconds;
                if (ok = task.IsCompletedSuccessfully && await done()) { ms = at; break; }
                if (task.IsFaulted) break;
            }
            await Task.Delay(20);
        }
        if (!ok) ms = opClock.Elapsed.TotalMilliseconds;
        walStop.Cancel();
        await walPoll;
        if (task.IsFaulted) Console.WriteLine($"    исключение: {task.Exception!.GetBaseException().GetType().Name}: {task.Exception.GetBaseException().Message}");
        await Settle(0.5, 60);
        var stall = probe.Stalls.ToList().Where(s => s.Step == stepName).Select(s => s.Ms).DefaultIfEmpty(0).Max();
        var shown = windows.Count == 0 ? "" : "; окна: " + string.Join(", ", windows.Select(x => $"{x.Type} на {x.AtMs:N0} мс"));
        Console.WriteLine($"  {name}: {(ok ? "готово" : "НЕ ВЫШЛО")} за {ms:N0} мс; пауза UI макс {stall:N0} мс{shown}; " +
                          $"память {GC.GetTotalMemory(false) / 1048576:N0} МБ управляемой, {Environment.WorkingSet / 1048576:N0} МБ процесса " +
                          $"({Process.GetCurrentProcess().PrivateMemorySize64 / 1048576:N0} МБ частной — без страниц файла базы); " +
                          $"журнал базы до {walMax / 1048576:N0} МБ");
        summary.Add((name, ok, ms, stall));
    }

    // Окна: подтверждение — «Да», прогресс закроется сам, остальные — закрыть без изменений,
    // дав им разложиться (время их построения — в паузах UI).
    void Answer(Window w)
    {
        switch (w)
        {
            case SmartBase.Controls.Dialogs.ConfirmDialog:
                w.FindControl<Button>("ConfirmBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                break;
            case SmartBase.Controls.Dialogs.ProgressDialog:
                break;
            case SmartBase.Controls.Dialogs.RecordDisplayDialog when displayScript is { } script:
                _ = script(w);
                break;
            default:
                _ = Task.Delay(300).ContinueWith(_ => Dispatcher.UIThread.Post(w.Close));
                break;
        }
    }

    int OpsNamed(string name) { lock (diagLog.Ops) return diagLog.Ops.Count(o => o.Name == name); }
    // Счет значений столбца — в фоне: на 10 млн записей это секунды чтения индекса.
    Task<long> ValuesAsync(int fid) =>
        Task.Run(() => Convert.ToInt64(Query(db, $"SELECT COUNT(*) FROM db_field_values WHERE FieldId = {fid}")[0][0]));
    int MaxRecordId(DatabaseManager d) => Convert.ToInt32(Query(d, "SELECT MAX(Id) FROM db_records")[0][0]);
    bool RecordExists(DatabaseManager d, int id) => Query(d, $"SELECT 1 FROM db_records WHERE Id = {id}").Count > 0;
    List<object[]> Query(DatabaseManager d, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={d.Path};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        var rows = new List<object[]>();
        while (rd.Read()) { var r = new object[rd.FieldCount]; rd.GetValues(r); rows.Add(r); }
        return rows;
    }
}

// Боковая панель конструктора: после каждого действия строки разложены по своим данным (слот
// стрелки, отступ уровня). Действия: свернуть и развернуть каждую базу и все разом, подключить базу
// в работающую программу (заглушка «в очереди…», затем база), перенести коллекцию в группу и обратно
// окном «Расположение», удалить коллекцию; переходы между разделами.
async Task SidebarOps()
{
    ShowModule("constructor");
    await Settle(1, 60);
    var sidebar   = main!.FindControl<Control>("ConstructorSidebar")!;
    var faults    = 0;
    int? targetGroup = null;   // куда окно «Расположение» переносит коллекцию (null — корень базы)
    var collapsed = (HashSet<int>)Get(sidebar, "_collapsedDbRoots")!;

    Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
    {
        if (w is MainWindow) return;
        Dispatcher.UIThread.Post(() => AnswerSidebar(w), DispatcherPriority.Background);
    });

    Check("после старта");
    foreach (var n in Roots())
    {
        n.IsExpanded = false; collapsed.Add(n.DbId); Call(sidebar, "RebuildRows");
        Check($"свернута «{n.Name}»");
    }
    foreach (var n in Roots())
    {
        n.IsExpanded = true; collapsed.Remove(n.DbId); Call(sidebar, "RebuildRows");
        Check($"развернута «{n.Name}»");
    }
    Call(sidebar, "ToggleExpandAll", false); Check("свернуть все");
    Call(sidebar, "ToggleExpandAll", true);  Check("развернуть все");

    // То же, пока панель скрыта (открыт другой раздел): строки переносятся на новые узлы без
    // раскладки, и после показа должны разложиться заново.
    foreach (var n in Roots())
    {
        ShowModule("calendar"); await Settle(0.5, 30);
        n.IsExpanded = false; collapsed.Add(n.DbId); Call(sidebar, "RebuildRows");
        ShowModule("constructor"); await Settle(0.5, 30);
        Check($"свернута «{n.Name}» при скрытой панели");
        ShowModule("calendar"); await Settle(0.5, 30);
        n.IsExpanded = true; collapsed.Remove(n.DbId); Call(sidebar, "RebuildRows");
        ShowModule("constructor"); await Settle(0.5, 30);
        Check($"развернута «{n.Name}» при скрытой панели");
    }

    // Подключение базы в работающую программу — как «Подключить базу данных» в настройках.
    var extraPath = Path.Combine(runDir, "extra.db");
    foreach (var suffix in new[] { "", "-wal", "-shm" })
        if (File.Exists(extraPath + suffix)) File.Delete(extraPath + suffix);
    File.Copy(Path.Combine(srcDir, "main.db"), extraPath);
    var entry = registry.Add("Подключенная", extraPath);
    Call(Get(main, "_mountQueue")!, "Enqueue", entry);
    Call(main, "SyncMountedToUi");
    Check("подключение: заглушка");
    await Settle(1, 120);
    Check("подключение: база подключена");
    var first = Roots().First();
    first.IsExpanded = false; collapsed.Add(first.DbId); Call(sidebar, "RebuildRows");
    Check($"после подключения свернута «{first.Name}»");
    first.IsExpanded = true; collapsed.Remove(first.DbId); Call(sidebar, "RebuildRows");
    Check($"после подключения развернута «{first.Name}»");

    // Переходы между разделами.
    ShowModule("calendar"); await Settle(1, 30);
    ShowModule("constructor"); await Settle(1, 30);
    Check("после перехода в календарь и обратно");

    // Перенос коллекции в группу и обратно — окном «Расположение», как из меню узла.
    var extra = workspace.Mounted.First(d => d.Name == "Подключенная");
    var repo  = extra.Constructor;
    var group = repo.AddGroup(new SmartBase.Core.Models.DbGroup { Name = "Стенд: группа" });
    var coll  = repo.GetAllCollections().First(c => c.GroupId is null);
    Call(sidebar, "Refresh");
    await Settle(0.5, 30);
    targetGroup = group.Id;
    await SOp($"перенос «{coll.Name}» в группу",
              () => Call(sidebar, "RelocateSelectionOrNode", Node(extra.DbId, coll.Id!.Value)),
              () => repo.GetAllCollections().First(c => c.Id == coll.Id).GroupId == group.Id);
    targetGroup = null;
    await SOp($"перенос «{coll.Name}» обратно",
              () => Call(sidebar, "RelocateSelectionOrNode", Node(extra.DbId, coll.Id!.Value)),
              () => repo.GetAllCollections().First(c => c.Id == coll.Id).GroupId is null);

    // Удаление коллекции (пустой, созданной стендом).
    var temp = repo.AddCollection(new SmartBase.Core.Models.DbCollection { Name = "Стенд: удаляемая" });
    Call(sidebar, "Refresh");
    await Settle(0.5, 30);
    await SOp("удаление коллекции", () => Call(sidebar, "DeleteSelectionOrNode", Node(extra.DbId, temp.Id!.Value)),
              () => repo.GetAllCollections().All(c => c.Id != temp.Id));

    Console.WriteLine(faults == 0 ? "Боковая панель: все проверки прошли" : $"Боковая панель: НЕ ПРОШЛО {faults}");

    List<SmartBase.Controls.Components.SidebarNode> Roots() =>
        ((System.Collections.IEnumerable)Get(sidebar, "_roots")!).Cast<SmartBase.Controls.Components.SidebarNode>()
            .Where(n => n.IsDatabase && !n.IsPending).ToList();

    SmartBase.Controls.Components.SidebarNode Node(int dbId, int id)
    {
        IEnumerable<SmartBase.Controls.Components.SidebarNode> Walk(IEnumerable<SmartBase.Controls.Components.SidebarNode> ns) =>
            ns.SelectMany(n => new[] { n }.Concat(Walk(n.Children)));
        return Walk(((System.Collections.IEnumerable)Get(sidebar, "_roots")!).Cast<SmartBase.Controls.Components.SidebarNode>())
            .First(n => n.DbId == dbId && n.Id == id && !n.IsGroup && !n.IsDatabase);
    }

    void Check(string when)
    {
        main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        main.UpdateLayout();
        var f = SidebarLayoutFaults(sidebar);
        faults += f.Count;
        Console.WriteLine(f.Count == 0 ? $"  {when}: строки разложены по своим данным" : $"  {when}: НЕ ПРОШЛО: {string.Join("; ", f)}");
    }

    async Task SOp(string name, Action act, Func<bool> done)
    {
        Step($"боковая панель: {name}");
        var stepName = probe.Step;
        var sw = Stopwatch.StartNew();
        act();
        while (!done() && sw.Elapsed.TotalSeconds < 30) await Task.Delay(20);
        var ms = sw.Elapsed.TotalMilliseconds;
        var ok = done();
        await Settle(0.5, 30);
        var stall = probe.Stalls.ToList().Where(s => s.Step == stepName).Select(s => s.Ms).DefaultIfEmpty(0).Max();
        Console.WriteLine($"  {name}: {(ok ? "готово" : "НЕ ВЫШЛО")} за {ms:N0} мс; пауза UI макс {stall:N0} мс");
        if (!ok) faults++;
        Check($"после «{name}»");
    }

    void AnswerSidebar(Window w)
    {
        if (w is SmartBase.Controls.Dialogs.ConfirmDialog)
        {
            w.FindControl<Button>("ConfirmBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return;
        }
        if (w is SmartBase.Controls.Dialogs.LocationDialog)
        {
            var groups = (List<(string Label, int? Id)>)Get(w, "_groups")!;
            var index  = groups.FindIndex(g => g.Id == targetGroup);
            w.FindControl<ComboBox>("LocationCombo")!.SelectedIndex = Math.Max(0, index);
            w.FindControl<Button>("ConfirmBtn")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            return;
        }
        Console.WriteLine($"    [окно без ответа стенда: {w.GetType().Name} «{w.Title}»]");
    }
}

// Ширины всех блоков вида: ширина до округления и итоговая (правило округления рисованной колонки).
void DumpAll(Control view)
{
    var entries = (System.Collections.IEnumerable)Get(view, "_eventEntries")!;
    foreach (var entry in entries)
    {
        var item = (CalendarDayCanvas.Item)Get(entry, "Btn")!;
        Console.WriteLine($"    W {item.RawWidth:0.####} → {item.Bounds.Width:0.##}; x {item.Bounds.X:0.##}, y {item.Bounds.Y:0.##}");
    }
}

// Середина блока события по названию — через записи вида (_eventEntries: Ev + Btn — блок
// рисованной колонки дня).
Point EntryCenter(Control view, string title)
{
    var entries = (System.Collections.IEnumerable)Get(view, "_eventEntries")!;
    foreach (var entry in entries)
    {
        var ev = (SmartBase.Core.Models.CalendarRecord)Get(entry, "Ev")!;
        if (ev.Title != title) continue;
        var item = (CalendarDayCanvas.Item)Get(entry, "Btn")!;
        return item.Owner.TranslatePoint(item.Bounds.Center, main!) ?? default;
    }
    throw new InvalidOperationException($"Нет события «{title}» в виде");
}

// Разделы окна (кроме конструктора) строит первый показ, поэтому по имени в разметке их нет —
// берем свойством окна (построит, если еще не построен).
Control Module(string name) =>
    (Control)typeof(MainWindow).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
Control CalendarModule() => Module("CalendarModule");

// Как клик по коллекции в дереве конструктора (обработчик CollectionSelected главного окна).
void OpenCollection(DatabaseManager db, int collectionId)
{
    typeof(MainWindow).GetField("_currentConstructorDbId", BindingFlags.Instance | BindingFlags.NonPublic)!
                      .SetValue(main, db.DbId);
    var module = main!.FindControl<Control>("ConstructorModule")!;
    Call(module, "OpenFrom", db.Constructor, collectionId);
    if (Get(main, "_collectionOpen") is false) Call(main, "ApplyConstructorContentVisibility", true);
}

// Группа сценария включена: --only не задан или «tour» — все; иначе список через запятую
// (modules, calendar, tasks, constructor, settings, profile).
bool Want(string group) =>
    only is null or "tour" || only.Split(',').Contains(group);

void ShowModule(string module)
{
    main!.FindControl<Control>("ActivityBarControl")?.GetType().GetMethod("SetModuleActive")?.Invoke(
        main.FindControl<Control>("ActivityBarControl"), [module]);
    Call(main, "ShowModule", module);
}

void Step(string name)
{
    var now = clock.Elapsed.TotalSeconds;
    if (steps.Count > 0) steps[^1] = steps[^1] with { EndSec = now };
    steps.Add((name, now, now));
    gcMarks.Add(GcNow());
    probe.Step = name;
    Console.WriteLine($"[{now,6:0.0} с] {name}");
}

// Ждать, пока шаг затихнет: нет идущих замеров Diag и очередь подключения баз пуста —
// quietSec подряд; не дольше capSec.
async Task Settle(double quietSec, double capSec)
{
    var sw = Stopwatch.StartNew();
    var quietSince = TimeSpan.Zero;
    while (sw.Elapsed.TotalSeconds < capSec)
    {
        await Task.Delay(100);
        var busy = Diag.ActiveOperations.Length > 0 || MountsPending() > 0;
        if (busy) quietSince = sw.Elapsed;
        else if ((sw.Elapsed - quietSince).TotalSeconds >= quietSec) return;
    }
    Console.WriteLine($"  … не затихло за {capSec} с: идут «{Diag.ActiveOperations}», в очереди баз {MountsPending()}{MountsText()}");
}

// Что с базами в очереди: «подключается…», «недоступна (причина)».
string MountsText()
{
    if (main is null) return "";
    var queue = Get(main, "_mountQueue");
    if (queue?.GetType().GetMethod("Pending")?.Invoke(queue, null) is not System.Collections.IEnumerable items) return "";
    var texts = items.Cast<object>().Select(i => $"{i.GetType().GetProperty("Name")?.GetValue(i)}: {i.GetType().GetProperty("Text")?.GetValue(i)}").ToList();
    return texts.Count == 0 ? "" : " (" + string.Join("; ", texts) + ")";
}

int MountsPending()
{
    if (main is null) return 0;
    var queue = Get(main, "_mountQueue");
    var pending = queue?.GetType().GetMethod("Pending")?.Invoke(queue, null) as System.Collections.ICollection;
    return pending?.Count ?? 0;
}

// ── Отчет ────────────────────────────────────────────────────────────────────

void Report()
{
    if (steps.Count > 0) steps[^1] = steps[^1] with { EndSec = clock.Elapsed.TotalSeconds };
    gcMarks.Add(GcNow());
    var stalls = probe.Stalls.ToList();
    double Rel(DateTime utc) => (utc - t0Utc).TotalSeconds;

    Console.WriteLine();
    Console.WriteLine($"== Паузы UI-потока ≥ {stallMs} мс: {stalls.Count} (≥100 мс: {stalls.Count(s => s.Ms >= 100)}, " +
                      $"≥250: {stalls.Count(s => s.Ms >= 250)}, ≥1000: {stalls.Count(s => s.Ms >= 1000)}), " +
                      $"сумма {stalls.Sum(s => s.Ms):N0} мс, самая длинная {(stalls.Count > 0 ? stalls.Max(s => s.Ms) : 0):N0} мс");
    for (var i = 0; i < steps.Count; i++)
    {
        var (name, start, end) = steps[i];
        var mine = stalls.Where(s => s.Step == name).ToList();
        var (a, b) = (gcMarks[i], gcMarks[i + 1]);
        Console.WriteLine($"  {name,-50} {end - start,6:0.0} с  пауз {mine.Count,3}  " +
                          $"сумма {mine.Sum(s => s.Ms),7:N0} мс  макс {(mine.Count > 0 ? mine.Max(s => s.Ms) : 0),6:N0} мс" +
                          $"   GC {(b.Pause - a.Pause).TotalMilliseconds,5:N0} мс ({b.G0 - a.G0}/{b.G1 - a.G1}/{b.G2 - a.G2}), куча {b.HeapMb} МБ");
    }

    var spans = stalls.Where(s => s.Ms >= reportMs)
                      .Select(s => new Sampler.Span(s.StartUtc, s.StartUtc.AddMilliseconds(s.Ms),
                                                    $"{Rel(s.StartUtc),6:0.00} с  {s.Ms,6:N0} мс  {s.Step}"))
                      .ToList();
    // Медленные замеры Diag — по выборкам ВСЕХ потоков: фоновая работа замера видна там, где
    // она шла, а не только на UI-потоке.
    var opSpans = diagLog.Ops.ToList().Where(o => o.Ms >= opsMs)
                         .Select(o =>
                         {
                             var end = t0Utc.AddSeconds(o.EndSec);
                             return new Sampler.Span(end.AddMilliseconds(-o.Ms), end,
                                                     $"{o.Ms,6:N0} мс  {o.Name}{(string.IsNullOrEmpty(o.Context) ? "" : " · " + MemorySink.Trim(o.Context, 70))}  [{o.Step}]",
                                                     AnyThread: true);
                         })
                         .ToList();
    if (sampler is not null && spans.Count + opSpans.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Разбор трассы ({new FileInfo(sampler.Path).Length / 1048576} МБ)…");
        var sw = Stopwatch.StartNew();
        Sampler.Collect(sampler.Path, (int)uiThread, [.. spans, .. opSpans]);
        Console.WriteLine($"  разобрано за {sw.Elapsed.TotalSeconds:0.0} с");
    }

    Console.WriteLine();
    Console.WriteLine($"== Паузы ≥ {reportMs} мс ({spans.Count})");
    foreach (var span in spans)
    {
        Console.WriteLine($"  {span.Label}   выборок {span.Samples.Count}");
        foreach (var (share, key) in Sampler.Top(span))
            Console.WriteLine($"        {share,5:0}%  {key}");
        // Длинная пауза — еще и по методам: из чего она сложена (включающие доли).
        if (span.EndUtc - span.StartUtc >= TimeSpan.FromMilliseconds(detailMs) && span.Samples.Count > 0)
        {
            foreach (var (n, m) in Sampler.Inclusive([span], top: 14))
                Console.WriteLine($"          · {100.0 * n / span.Samples.Count,5:0}%  {m}");
            Console.WriteLine("          все кадры (библиотеки тоже):");
            foreach (var (n, m) in Sampler.InclusiveAll(span, top: 24))
                Console.WriteLine($"          ◦ {100.0 * n / span.Samples.Count,5:0}%  {m}");
        }
    }
    if (sampler is not null && spans.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("== Методы программы в паузах (выборок через метод, включая вызванные)");
        foreach (var (n, m) in Sampler.Inclusive(spans))
            Console.WriteLine($"  {n,6}  {m}");
    }

    if (sampler is not null && opSpans.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"== Замеры Diag ≥ {opsMs} мс по выборкам всех потоков ({opSpans.Count})");
        foreach (var span in opSpans)
        {
            Console.WriteLine($"  {span.Label}   выборок с кодом программы {span.Samples.Count}");
            foreach (var (n, m) in Sampler.Inclusive([span], top: 10))
                Console.WriteLine($"          · {100.0 * n / Math.Max(1, span.Samples.Count),5:0}%  {m}");
            // --under Метод,Метод — что внутри названных методов (кадры библиотек тоже).
            foreach (var method in (Arg("--under") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (total, inner) = Sampler.Under(span, method);
                if (total == 0) continue;
                Console.WriteLine($"            внутри {method} ({total} выборок):");
                foreach (var (n, m) in inner)
                    Console.WriteLine($"              ◦ {100.0 * n / total,5:0}%  {m}");
                foreach (var (n, chain) in Sampler.Chains(span, method))
                    Console.WriteLine($"              ⇠ {n} выборок: {chain}");
            }
        }
    }

    Console.WriteLine();
    var ops = diagLog.Ops.ToList();
    Console.WriteLine($"== Замеры Diag ({ops.Count}); по убыванию длительности");
    foreach (var op in ops.OrderByDescending(o => o.Ms).Take(60))
        Console.WriteLine($"  {op.Ms,8:N0} мс  {op.Name}{(string.IsNullOrEmpty(op.Context) ? "" : " · " + MemorySink.Trim(op.Context, 90))}  [{op.Step}, {op.EndSec:0.0} с]");
    Console.WriteLine("  по операциям: число, сумма, макс");
    foreach (var g in ops.GroupBy(o => o.Name).OrderByDescending(g => g.Max(o => o.Ms)))
        Console.WriteLine($"  {g.Count(),4}  {g.Sum(o => o.Ms),9:N0} мс  {g.Max(o => o.Ms),8:N0} мс  {g.Key}");

    Console.WriteLine();
    var warnings = sink.Entries.Where(e => e.Level >= Serilog.Events.LogEventLevel.Warning).ToList();
    Console.WriteLine($"== Журнал: предупреждений и ошибок {warnings.Count}");
    // Сами записи — сразу за числом: число без текста не говорит, что именно сломалось.
    foreach (var e in warnings)
        Console.WriteLine($"  {e.AtSec,6:0.0} с  {MemorySink.Trim(e.Text, 240)}");
    foreach (var e in sink.Entries.Where(e => e.Text.Contains("открыта за", StringComparison.Ordinal)))
        Console.WriteLine($"  {e.AtSec,6:0.0} с  {MemorySink.Trim(e.Text, 160)}");
}

// ── Отражение ────────────────────────────────────────────────────────────────

// Вид календаря по имени свойства контроллера (виды строятся при первом обращении, в разметке
// их нет — FindControl их не найдет).
// Строки боковой панели, разложенные не по своим данным: у отрисованной строки ширина слота
// стрелки и отступ уровня — ChevronSlot и IndentMargin ее SidebarRow, слот стрелки стоит в начале
// строки, значок — сразу за слотами стрелки и галочки (его поле слева — 2).
static List<string> SidebarLayoutFaults(Control sidebar)
{
    var faults = new List<string>();
    foreach (var cp in sidebar.GetVisualDescendants().OfType<ContentPresenter>())
    {
        if (cp.Content is not SmartBase.Controls.Components.SidebarRow row) continue;
        var grid    = cp.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "RowRoot")?.Child as Grid;
        var chevron = grid?.Children.OfType<Border>().FirstOrDefault(b => Grid.GetColumn(b) == 0);
        if (grid is null || chevron is null) { faults.Add($"{row.Name}: нет разметки строки"); continue; }
        var icon = grid.Children.OfType<Viewbox>().FirstOrDefault(v => Grid.GetColumn(v) == 2);
        var iconX = icon is null ? double.NaN : icon.Bounds.X - 2;
        if (Math.Abs(chevron.Bounds.Width - row.ChevronSlot) > 0.5 || Math.Abs(chevron.Bounds.X) > 0.5
            || Math.Abs(grid.Margin.Left - row.IndentMargin.Left) > 0.5
            || !(Math.Abs(iconX - row.ChevronSlot - row.CheckboxSlot) <= 0.5))
            faults.Add($"«{row.Name}»: слот стрелки {chevron.Bounds.Width:0} в {chevron.Bounds.X:0} (нужно {row.ChevronSlot:0} в 0), " +
                       $"значок в {iconX:0} (нужно {row.ChevronSlot + row.CheckboxSlot:0}), " +
                       $"отступ {grid.Margin.Left:0} (нужно {row.IndentMargin.Left:0})");
    }
    return faults;
}

static T CalView<T>(Control calendar, string name) where T : Control =>
    (T)calendar.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(calendar)!;

static object? Get(object target, string name)
{
    for (var t = target.GetType(); t is not null; t = t.BaseType)
        if (t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } f)
            return f.GetValue(target);
    return null;
}

static void SetField(object target, string name, object value)
{
    for (var t = target.GetType(); t is not null; t = t.BaseType)
        if (t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } f)
        { f.SetValue(target, value); return; }
    throw new InvalidOperationException($"Нет поля {name}");
}

// Масштаб окна (DPI): у безголового окна он зашит единицей — отражением до Show(), как в Tests/RecordPage.
static void SetScale(Window w, double scale)
{
    if (Math.Abs(scale - 1.0) < 0.001) return;
    var impl  = w.PlatformImpl!;
    var field = impl.GetType().GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба — обнови стенд");
    field.SetValue(impl, scale);
}

// Неделя 6–12 июля 2026 для кадров календаря: короткое, часовые, длинное, три пересекающихся,
// длинное название (перенос и многоточие), «весь день» — в первой коллекции с календарной датой.
static void SeedCalendarWeek(SmartBase.Database.Repositories.ConstructorRepository repo)
{
    var col = repo.GetAllCollections().First(c => repo.GetFields(c.Id!.Value).Any(f => f.FieldType == "date" && f.IsCalStart));
    var cid = col.Id!.Value;
    var due = repo.GetFields(cid).First(f => f.FieldType == "date" && f.IsCalStart).Id!.Value;
    void Ev(string title, DateTime start, DateTime end, bool allDay = false)
    {
        var rec = repo.AddRecord(new SmartBase.Core.Models.DbRecord { CollectionId = cid, Title = title, Position = 100000 });
        repo.SetFieldValue(rec.Id!.Value, due, new SmartBase.Core.Models.DateValue(start, end, allDay).ToStorage());
    }
    var mon = new DateTime(2026, 7, 6);
    Ev("Планерка отдела", mon.AddHours(9), mon.AddHours(10));
    Ev("Разбор заявок от подрядчиков по договору поставки оборудования и сверка актов", mon.AddHours(9.5), mon.AddHours(11.5));
    Ev("Звонок", mon.AddHours(10), mon.AddHours(10).AddMinutes(22));
    Ev("Работа над отчетом", mon.AddDays(1).AddHours(14), mon.AddDays(1).AddHours(18));
    var wed = mon.AddDays(2);
    Ev("Кофе", wed.AddHours(8), wed.AddHours(8.5));
    Ev("Планерка отдела", wed.AddHours(9), wed.AddHours(10));
    Ev("Встреча А", wed.AddHours(12), wed.AddHours(13));
    Ev("Встреча Б", wed.AddHours(12.25), wed.AddHours(13.25));
    Ev("Встреча В с длинным названием", wed.AddHours(12.5), wed.AddHours(14));
    Ev("Отпуск", mon.AddDays(3), mon.AddDays(3), allDay: true);
    Ev("Итоги недели", mon.AddDays(4).AddHours(16), mon.AddDays(4).AddHours(17));
    // Полоса «весь день»: многодневные внутри недели и через ее края, событие со временем
    // длиннее суток (подпись «, ЧЧ:мм»), длинное название (многоточие).
    Ev("Конференция", mon.AddDays(1), mon.AddDays(2), allDay: true);
    Ev("Командировка", mon.AddDays(-3), mon.AddDays(2), allDay: true);
    Ev("Проект", mon.AddDays(4), mon.AddDays(9), allDay: true);
    Ev("Дежурство", mon.AddDays(3).AddHours(18), mon.AddDays(5).AddHours(9));
    Ev("Очень длинное название события на весь день, которое не помещается", mon, mon, allDay: true);

    // 2027 год — для кадров «Года» (в нем нет сегодняшнего дня, кадр не меняется день ото дня):
    // все ступени подсветки чисел (1, 2–5, 6–8, 9 и больше), многодневные, выходной, край года.
    void Many(DateTime day, int n)
    {
        for (var i = 0; i < n; i++) Ev($"Событие {i + 1}", day.AddHours(8 + i * 0.5), day.AddHours(8.5 + i * 0.5));
    }
    Many(new DateTime(2027, 1, 5), 1);
    Many(new DateTime(2027, 1, 12), 3);
    Many(new DateTime(2027, 2, 3), 7);
    Many(new DateTime(2027, 2, 10), 12);
    Ev("Командировка", new DateTime(2027, 3, 1), new DateTime(2027, 3, 4), allDay: true);
    Ev("Субботник", new DateTime(2027, 3, 6).AddHours(10), new DateTime(2027, 3, 6).AddHours(13));
    Ev("Через май", new DateTime(2027, 4, 30).AddHours(18), new DateTime(2027, 5, 2).AddHours(9));
    Ev("Под Новый год", new DateTime(2027, 12, 31).AddHours(20), new DateTime(2027, 12, 31).AddHours(23));
}

static object? Call(object target, string name, params object[] a)
{
    var m = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                  .First(x => x.Name == name && x.GetParameters().Length == a.Length);
    return m.Invoke(target, a);
}

string? Arg(string key)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (dir.GetFiles("SmartBase.csproj").Length > 0) return dir.FullName;
    throw new InvalidOperationException("Не найден корень репозитория (SmartBase.csproj)");
}

static class Native
{
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}

// Метки в журнале компиляции на лету (DOTNET_JitStdOutFile): список над своей структурой
// компилируется при первом обращении, между ними — то, что компилировалось в замеряемом шаге.
struct JitMarkBegin;
struct JitMarkEnd;
