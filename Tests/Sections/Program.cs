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
//   --only frames [--scale 1|2] [--dbs main] [--ref DIR] — кадры недели и дня календаря (обычный,
//   выделение, наведение) на неделе, которую стенд заводит в копию «Основной», против эталонов в
//   --ref (по умолчанию ~/TestData/regress; нет эталона — кадр им становится); --dump — ширины блоков
//   событий и поле их текста (сверка геометрии блока с прежней раскладкой).

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Serilog;
using SmartBase.Database;
using SmartBase.Database.DocIndex;
using SmartBase.Logging;
using SmartBase.Tests.Sections;
using SmartBase.Tests.ServiceGrids;
using SmartBase.Utils;
using SmartBase.Views;

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

var srcDir  = Path.Combine(dataDir, "src");
var runDir  = Path.Combine(dataDir, "run");
var bigPath  = Arg("--big")  ?? Path.Combine(FindRoot(), "Tests", "test_database.db");
var widePath = Arg("--wide") ?? Path.Combine(home, "TestData", "wide1000.db");
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

    if (Want("modules"))
    foreach (var module in new[] { "calendar", "tasks", "library", "notes", "profile", "settings", "extensions", "constructor" })
    {
        Step($"раздел «{module}»");
        ShowModule(module);
        await Settle(quietSec: 1.5, capSec: 90);
    }

    // Календарь: все виды (0 — день … 6 — диаграмма), затем обратно к месяцу.
    if (Want("calendar")) ShowModule("calendar");
    var calendar = main!.FindControl<Control>("CalendarModule")!;
    if (Want("calendar"))
    foreach (var view in new[] { 0, 1, 2, 3, 5, 6, 4 })
    {
        Step($"календарь: вид {view}");
        Call(calendar, "SwitchView", view);
        await Settle(quietSec: 1.5, capSec: 90);
    }

    // Задачи: пункты фильтра навбара по очереди — как выбор пункта человеком.
    if (Want("tasks")) { Step("раздел «tasks»"); ShowModule("tasks"); await Settle(quietSec: 1.5, capSec: 90); }
    var taskFilters = main.FindControl<Control>("TasksModule")!.FindControl<Control>("NavbarControl")!
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
    var calendar = main!.FindControl<Control>("CalendarModule")!;
    SetField(calendar, "_currentDate", new DateTime(2026, 7, 8));
    SetField(calendar, "_weekCentered", false);
    foreach (var (view, name, ctrl) in new[] { (3, "week", "WeekViewCtrl"), (2, "day", "DayViewCtrl") })
    {
        Step($"кадры: {name}");
        Call(calendar, "SwitchView", view);
        await Settle(quietSec: 1, capSec: 30);
        var viewCtrl = calendar.FindControl<Control>(ctrl)!;
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
    }
    Console.WriteLine($"  Кадры календаря: наибольшее различие с допуском {worst} пикселей");

    async Task Frame(string name)
    {
        for (var i = 0; i < 4; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); await Task.Delay(30); }
        var file = Path.Combine(outDir, name + ".png");
        Frames.Save(main!.CaptureRenderedFrame(), file);
        var reference = Path.Combine(refDir, name + ".png");
        if (!File.Exists(reference)) { File.Copy(file, reference); Console.WriteLine($"  {name}: эталон создан"); return; }
        var (strict, loose, diff, _) = Frames.Compare(Frames.Load(reference), Frames.Load(file), 16, (_, _) => "кадр");
        Frames.Save(diff, Path.Combine(outDir, name + "-diff.png"));
        // Увеличенные вырезки блока «Планерка отдела» в среду — глазами видно, в чем различие.
        if (name.StartsWith("cal-week", StringComparison.Ordinal) && loose > 0)
        {
            Frames.SaveCrop(Frames.Load(reference), 698, 180, 180, 66, scale, Path.Combine(outDir, name + "-crop-ref.png"));
            Frames.SaveCrop(Frames.Load(file), 698, 180, 180, 66, scale, Path.Combine(outDir, name + "-crop-new.png"));
        }
        worst = Math.Max(worst, loose);
        Console.WriteLine($"  {name}: различий строго {strict}, с допуском {loose}");
    }
}

// Ширины всех блоков вида и поле их текста (прежний блок: StackPanel) — правило округления.
void DumpAll(Control view)
{
    var entries = (System.Collections.IEnumerable)Get(view, "_eventEntries")!;
    foreach (var entry in entries)
    {
        var btn = (Control)Get(entry, "Btn")!;
        var stack = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(btn).OfType<StackPanel>().FirstOrDefault();
        var at = stack?.TranslatePoint(new Point(0, 0), btn) ?? default;
        Console.WriteLine($"    W {btn.Width:0.####} L {Canvas.GetLeft(btn):0.####} → {btn.Bounds.Width:0.##}; текст x {at.X:0.##} w {stack?.Bounds.Width:0.##} (справа {btn.Bounds.Width - at.X - (stack?.Bounds.Width ?? 0):0.##})");
    }
}

// Середина блока события по названию — через записи вида (_eventEntries: Ev + Btn), одинаковые
// в прежней и нынешней версии блока.
Point EntryCenter(Control view, string title)
{
    var entries = (System.Collections.IEnumerable)Get(view, "_eventEntries")!;
    foreach (var entry in entries)
    {
        var ev = (SmartBase.Core.Models.CalendarRecord)Get(entry, "Ev")!;
        if (ev.Title != title) continue;
        var btn = (Control)Get(entry, "Btn")!;
        return btn.TranslatePoint(new Point(btn.Bounds.Width / 2, btn.Bounds.Height / 2), main!) ?? default;
    }
    throw new InvalidOperationException($"Нет события «{title}» в виде");
}

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
    Console.WriteLine($"  … не затихло за {capSec} с: идут «{Diag.ActiveOperations}», в очереди баз {MountsPending()}");
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
    var stalls = probe.Stalls.ToList();
    double Rel(DateTime utc) => (utc - t0Utc).TotalSeconds;

    Console.WriteLine();
    Console.WriteLine($"== Паузы UI-потока ≥ {stallMs} мс: {stalls.Count} (≥100 мс: {stalls.Count(s => s.Ms >= 100)}, " +
                      $"≥250: {stalls.Count(s => s.Ms >= 250)}, ≥1000: {stalls.Count(s => s.Ms >= 1000)}), " +
                      $"сумма {stalls.Sum(s => s.Ms):N0} мс, самая длинная {(stalls.Count > 0 ? stalls.Max(s => s.Ms) : 0):N0} мс");
    foreach (var (name, start, end) in steps)
    {
        var mine = stalls.Where(s => s.Step == name).ToList();
        Console.WriteLine($"  {name,-50} {end - start,6:0.0} с  пауз {mine.Count,3}  " +
                          $"сумма {mine.Sum(s => s.Ms),7:N0} мс  макс {(mine.Count > 0 ? mine.Max(s => s.Ms) : 0),6:N0} мс");
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
        if (span.EndUtc - span.StartUtc >= TimeSpan.FromSeconds(1) && span.Samples.Count > 0)
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
    foreach (var e in sink.Entries.Where(e => e.Text.Contains("открыта за", StringComparison.Ordinal)))
        Console.WriteLine($"  {e.AtSec,6:0.0} с  {MemorySink.Trim(e.Text, 160)}");
}

// ── Отражение ────────────────────────────────────────────────────────────────

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
