// Стенд служебных таблиц (этап 6а плана drawn-grid.md): «Диагностика» (SettingsView), «История
// изменений», «Пользователи» и журнал безопасности (ProfileView) — настоящие экраны на временных
// данных (Fixtures.cs). Для каждой таблицы при масштабе 1 и 2 снимаются кадры сцен: базовая,
// наведение на заголовок, наведение на кнопку фильтра, наведение на строку, выбранная строка с
// панелью деталей (у «Пользователей» вместо нее — прокрутка вбок с закрепленной колонкой). Кадры
// сравниваются попиксельно с эталонами в папке --ref (по умолчанию ~/TestData/regress); нет эталона —
// кадр принимается за эталон. Так эталонами стали кадры таблиц на DataGrid, снятые до перевода на
// рисованную сетку, а после перевода тот же стенд сверяет с ними рисованные.
// Затем (масштаб 1) — поведение: стрелка вниз двигает выделение и панель деталей, кнопка заголовка
// открывает окно фильтра, прокрутка к концу подгружает страницы, галочка права меняет маску.
// Таблица ищется по имени, ее устройство стенд не знает: строки, колонки и смещение читаются
// отражением (ItemsSource/Columns у DataGrid, RowCount/ItemAt/ColumnWidths/OffsetX у ServiceGrid).
// Платформа — Avalonia.Headless с настоящим шрифтом (Skia).
//
//   dotnet run -c Release --project Tests/ServiceGrids -- [--data DIR] [--out DIR] [--ref DIR] [--only logs|history|securitylog|users]

using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Logging;
using SmartBase.Tests.ServiceGrids;
using SmartBase.Views.Profile;
using SmartBase.Views.Settings;

var home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dataDir = Arg("--data") ?? Path.Combine(home, "TestData", "servicegrids");
var outDir  = Arg("--out")  ?? Path.Combine(dataDir, "out");
var refDir  = Arg("--ref")  ?? Path.Combine(home, "TestData", "regress");
var only    = Arg("--only");
Directory.CreateDirectory(outDir);
Directory.CreateDirectory(refDir);
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Порядок важен: UseSkia ДО UseHeadless (как в Tests/GridProto).
AppBuilder.Configure<SmartBase.App>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();
SQLitePCL.Batteries_V2.Init();
SmartBase.Utils.FontScale.Init();
Dispatcher.UIThread.UnhandledException += (_, e) => Console.WriteLine($"    [исключение UI] {e.Exception}");

var swFx = Stopwatch.StartNew();
using var fx = Fixtures.Build(dataDir);
Console.WriteLine($"Стенд: {dataDir} ({swFx.ElapsedMilliseconds:N0} мс на данные)");
Console.WriteLine($"Диагностика {Fixtures.LogRows} строк, история {fx.HistoryRows} строк, журнал безопасности {Fixtures.SecurityRows} строк, пользователей {Fixtures.Users.Length + 1}");
Console.WriteLine($"Эталоны: {refDir}; кадры: {outDir}");
Console.WriteLine();

var worst   = 0;
var created = 0;
var failed  = 0;

var tables = new (string Key, string Grid, bool Details)[]
{
    ("logs",        "LogsGrid",    true),
    ("history",     "HistoryGrid", true),
    ("securitylog", "LogGrid",     true),
    ("users",       "UsersGrid",   false),
};

// Сначала масштаб 2, потом 1: проверки поведения (только при масштабе 1) пишут в базу стенда
// строки с настоящим временем, и кадры после них уже не воспроизводимы.
foreach (var scale in new[] { 2.0, 1.0 })
{
    Console.WriteLine($"Масштаб {scale:N0}:");
    foreach (var (key, gridName, details) in tables)
    {
        if (only is not null && only != key) continue;
        var (w, view) = Open(key, scale);
        var grid = view.FindControl<Control>(gridName) ?? throw new InvalidOperationException($"нет контрола {gridName}");
        var geo  = Geometry(w, grid);
        Console.WriteLine($"  «{Title(key)}»: строк {RowCount(grid)}, колонки {string.Join(", ", geo.Widths.Select(x => x.ToString("N0")))}");
        if (args.Contains("--geometry"))
        {
            // Точная геометрия таблицы: ширины колонок и прямоугольники ячеек первой строки после
            // округления раскладки. По этим числам прежней таблицы выведено правило дележа окна
            // рисованной сетки (ColumnLayout.BuildRounded); у сетки ячеек-контролов нет, и список
            // ячеек пуст.
            Console.WriteLine($"    ширины: {string.Join(", ", geo.Widths.Select(x => x.ToString("R")))}");
            var cells = grid.GetVisualDescendants().Where(v => v.GetType().Name == "DataGridCell").Take(geo.Widths.Length)
                            .Select(v => $"[{v.Bounds.X:R}+{v.Bounds.Width:R}]");
            Console.WriteLine($"    ячейки: {string.Join(" ", cells)}");
            var heads = grid.GetVisualDescendants().Where(v => v.GetType().Name == "DataGridColumnHeader").Take(geo.Widths.Length)
                            .Select(v => $"[{v.Bounds.X:R}+{v.Bounds.Width:R}]");
            Console.WriteLine($"    шапка:  {string.Join(" ", heads)}");
            Console.WriteLine($"    округление раскладки: 396.5→{Avalonia.Layout.LayoutHelper.RoundLayoutValue(396.5, scale):R}, 264.333→{Avalonia.Layout.LayoutHelper.RoundLayoutValue(264.3333333, scale):R}, 226.571→{Avalonia.Layout.LayoutHelper.RoundLayoutValue(226.5714285, scale):R}, 453.14→{Avalonia.Layout.LayoutHelper.RoundLayoutValue(453.142857, scale):R}");
            var vbar = grid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical);
            Console.WriteLine($"    полоса: {(vbar is null ? "нет" : $"{vbar.Bounds.Width:R} видима={vbar.IsVisible}")}, таблица {grid.Bounds.Width:R}×{grid.Bounds.Height:R}");
        }

        // Сцены: каждая следующая не отменяет предыдущую наведением — указатель паркуется над навбаром.
        Scene(w, grid, key, scale, geo, "base",    () => w.MouseMove(geo.Park));
        Scene(w, grid, key, scale, geo, "header",  () => w.MouseMove(geo.HeaderAt(1, 40)));
        Scene(w, grid, key, scale, geo, "chevron", () => w.MouseMove(geo.Chevron(1)));
        Scene(w, grid, key, scale, geo, "hover",   () => w.MouseMove(geo.RowCenter(2, 160)));
        if (details)
        {
            Scene(w, grid, key, scale, geo, "selected", () => { Click(w, geo.RowCenter(2, 160)); w.MouseMove(geo.Park); });
            // Панель деталей сузила таблицу — колонки другой ширины, геометрия считается заново.
            if (scale == 1) BehaveDetails(w, view, grid, key, Geometry(w, grid));
        }
        else
        {
            if (scale == 1) BehaveUsers(w, view, grid, geo);
            Scene(w, grid, key, scale, geo, "scrolled", () => { ScrollRight(w, grid, key, scale, "scrolled", 1); w.MouseMove(geo.Park); });
            // Правый край: даты, кнопка удаления (в том числе недоступная у своей строки).
            Scene(w, grid, key, scale, geo, "end",      () => { ScrollRight(w, grid, key, scale, "end", 3); w.MouseMove(geo.Park); });
        }
        w.Close();
        Pump();
    }
}

Console.WriteLine();
if (created > 0) Console.WriteLine($"Эталонов принято: {created} (сравнивать было не с чем — следующий прогон сравнит с ними).");
Console.WriteLine(worst == 0 ? "Кадры совпадают с эталоном." : $"Кадры расходятся с эталоном: до {worst:N0} пикс. с допуском. Карты различий: {outDir}");
Console.WriteLine(failed == 0 ? "Проверки поведения пройдены." : $"ПРОВАЛЕНО проверок поведения: {failed}.");
return worst == 0 && failed == 0 ? 0 : 2;

// ── Открытие экранов ──────────────────────────────────────────────────────────

(Window Window, Control View) Open(string key, double scale)
{
    var w = new Window { Width = 1600, Height = 900 };
    SetScale(w, scale);
    if (key == "logs")
    {
        var view = new SettingsView();
        // Журнал — из фикстуры, а не с машины: поле читателя подменяется до показа раздела.
        typeof(SettingsView).GetField("_logReader", BindingFlags.NonPublic | BindingFlags.Instance)!
                            .SetValue(view, new LogReader(fx.LogsPath));
        w.Content = view;
        w.Show();
        Settle(w);
        view.ShowCategory("logs");
        Pump();
        WaitRows(view, "LogsGrid");
        return (w, view);
    }
    var profile = new ProfileView();
    profile.Initialize(fx.Workspace);
    w.Content = profile;
    w.Show();
    Settle(w);
    if (key == "users")
    {
        profile.ShowCategory("admin");
        Pump();
        var open = profile.FindControl<Button>("UsersOpenBtn") ?? throw new InvalidOperationException("нет кнопки UsersOpenBtn");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        WaitRows(profile, "UsersGrid");
        return (w, profile);
    }
    profile.ShowCategory(key);
    Pump();
    WaitRows(profile, key == "history" ? "HistoryGrid" : "LogGrid");
    return (w, profile);
}

void WaitRows(Control view, string gridName)
{
    var grid = view.FindControl<Control>(gridName)!;
    if (!WaitUntil(() => RowCount(grid) > 0, TimeSpan.FromSeconds(30)))
        Console.WriteLine($"  ВНИМАНИЕ: строки {gridName} не пришли");
    // Дать таблице построить строки и раскладку.
    for (var i = 0; i < 8; i++) { Pump(); view.UpdateLayout(); Thread.Sleep(10); }
}

static string Title(string key) => key switch
{
    "logs"        => "Диагностика",
    "history"     => "История изменений",
    "securitylog" => "Журнал безопасности",
    _             => "Пользователи",
};

// ── Геометрия таблицы в окне ──────────────────────────────────────────────────

Geo Geometry(Window w, Control grid)
{
    grid.UpdateLayout();
    var origin = grid.TranslatePoint(new Point(0, 0), w) ?? new Point(0, 56);
    return new Geo(origin, ColumnWidths(grid));
}

// ── Сцены и сравнение ─────────────────────────────────────────────────────────

void Scene(Window w, Control grid, string key, double scale, Geo geo, string name, Action apply)
{
    // Каждая сцена начинается с парковки указателя: переход с заголовка сразу на его кнопку менял бы
    // содержимое уже показанной подсказки, а подсказки в кадрах не сравниваются (их таймер в
    // безголовом режиме не тикает).
    w.MouseMove(geo.Park); Pump();
    apply();
    for (var i = 0; i < 6; i++) { Pump(); w.CaptureRenderedFrame(); Thread.Sleep(5); }
    var captured = w.CaptureRenderedFrame();
    if (captured is null) { Console.WriteLine($"    сцена «{name}»: кадр не снят"); worst = int.MaxValue; return; }
    var newPath = Path.Combine(outDir, $"service-new-{key}-x{scale:N0}-{name}.png");
    Frames.Save(captured, newPath);

    var refPath = Path.Combine(refDir, $"service-ref-{key}-x{scale:N0}-{name}.png");
    if (!File.Exists(refPath))
    {
        File.Copy(newPath, refPath);
        created++;
        Console.WriteLine($"    сцена «{name}»: эталона не было — кадр принят за эталон");
        return;
    }
    var refFrame = Frames.Load(refPath);
    var newFrame = Frames.Load(newPath);
    if (refFrame.PixelSize != newFrame.PixelSize)
    {
        Console.WriteLine($"    сцена «{name}»: РАЗМЕРЫ КАДРОВ РАЗНЫЕ — эталон {refFrame.PixelSize}, кадр {newFrame.PixelSize}");
        worst = int.MaxValue;
        return;
    }
    string Band(int x, int y)
    {
        var dx = x / scale - geo.Origin.X;
        var dy = y / scale - geo.Origin.Y;
        if (dx < 0 || dy < 0) return "вне таблицы";
        var row = dy < Geo.Head ? "шапка" : $"строка {(int)((dy - Geo.Head) / Geo.Pitch) + 1}";
        var c   = geo.ColumnAt(dx);
        return c < 0 ? $"{row}, за колонками" : $"{row}, колонка {c + 1}";
    }
    var (strict, loose, diff, bands) = Frames.Compare(refFrame, newFrame, tolerance: 24, Band);
    var total = refFrame.PixelSize.Width * (long)refFrame.PixelSize.Height;
    Console.WriteLine($"    сцена «{name}»: различий строго {strict:N0} пикс. ({100.0 * strict / total:N3} %), с допуском 24 — {loose:N0} ({100.0 * loose / total:N3} %)");
    foreach (var (band, count) in bands.OrderByDescending(b => b.Count).Take(12))
        Console.WriteLine($"      {band,-40} {count,8:N0} пикс.");
    if (loose > 0)
    {
        Frames.Save(diff, Path.Combine(outDir, $"service-diff-{key}-x{scale:N0}-{name}.png"));
        var (cx, cy) = ((int)geo.Origin.X, (int)geo.Origin.Y);
        Frames.SaveCrop(refFrame, cx, cy, 160, 80, scale, Path.Combine(outDir, $"service-ref-{key}-x{scale:N0}-{name}-corner.png"));
        Frames.SaveCrop(newFrame, cx, cy, 160, 80, scale, Path.Combine(outDir, $"service-new-{key}-x{scale:N0}-{name}-corner.png"));
    }
    worst = Math.Max(worst, loose);
}

// Прокрутка вбок «Пользователей». У сетки с настраиваемым смещением (OffsetX) оно ставится из
// файла рядом с эталоном — тем самым, что записал прогон на DataGrid, где смещение получилось
// щелчком по дорожке полосы; так кадры сравниваются при одном и том же смещении.
void ScrollRight(Window w, Control grid, string key, double scale, string scene, int clicks)
{
    var sidecar = Path.Combine(refDir, $"service-ref-{key}-x{scale:N0}-{scene}.offset");
    var prop    = grid.GetType().GetProperty("OffsetX");
    if (prop is { CanWrite: true } && File.Exists(sidecar))
    {
        var target = double.Parse(File.ReadAllText(sidecar).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        prop.SetValue(grid, target);
        Pump();
        Console.WriteLine($"    прокрутка вбок: смещение {ReadOffsetX(grid):N1} (из эталона {target:N1})");
        return;
    }
    var bar = grid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(s => s.Orientation == Orientation.Horizontal && s.IsVisible);
    if (bar is null) { Console.WriteLine("    прокрутка вбок: горизонтальной полосы нет"); return; }
    var origin = bar.TranslatePoint(new Point(0, 0), w) ?? default;
    var pt = new Point(origin.X + bar.Bounds.Width - 40, origin.Y + bar.Bounds.Height / 2);
    for (var i = 0; i < clicks; i++) { Click(w, pt); Pump(); }
    var offset = ReadOffsetX(grid);
    Console.WriteLine($"    прокрутка вбок: смещение {offset:N1} (щелчок по дорожке полосы)");
    if (!File.Exists(sidecar))
        File.WriteAllText(sidecar, offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

// ── Поведение ─────────────────────────────────────────────────────────────────

void Expect(bool ok, string what)
{
    Console.WriteLine($"    {(ok ? "ок    " : "ПРОВАЛ")} {what}");
    if (!ok) failed++;
}

// Таблицы с панелью деталей: выбранная строка (сцена «selected» — строка 3) уже открыта.
void BehaveDetails(Window w, Control view, Control grid, string key, Geo geo)
{
    // Стрелка вниз — выделение и детали переезжают на четвертую строку.
    var before = Detail(view, key);
    w.KeyPress(Key.Down, RawInputModifiers.None); w.KeyRelease(Key.Down, RawInputModifiers.None); Pump();
    for (var i = 0; i < 4; i++) { Pump(); Thread.Sleep(5); }
    var after = Detail(view, key);
    Expect(before == Signature(grid, key, 2), $"панель деталей показывает строку 3 после клика: «{Short(before)}»");
    Expect(after == Signature(grid, key, 3) && after != before, $"стрелка вниз переводит детали на строку 4: «{Short(after)}»");

    // Кнопка заголовка — окно фильтра (плавающее окно, учитывается TransientPopups).
    var popups = TransientPopups.Count;
    Click(w, geo.Chevron(1));
    var opened = WaitUntil(() => TransientPopups.Count > popups, TimeSpan.FromSeconds(5));
    Expect(opened, "кнопка заголовка открывает окно фильтра");
    TransientPopups.CloseAll();
    Pump();

    // Прокрутка к концу подгружает страницы, пока не покажет все строки фикстуры.
    var expected = key switch { "logs" => Fixtures.LogRows, "history" => (int)fx.HistoryRows, _ => Fixtures.SecurityRows };
    var first    = RowCount(grid);
    var sw = Stopwatch.StartNew();
    while (RowCount(grid) < expected && sw.Elapsed < TimeSpan.FromSeconds(40))
    {
        var was = RowCount(grid);
        for (var i = 0; i < 40; i++) { w.MouseWheel(geo.RowCenter(5, 160), new Vector(0, -1), RawInputModifiers.None); Pump(); }
        WaitUntil(() => RowCount(grid) > was, TimeSpan.FromSeconds(3));
    }
    Expect(RowCount(grid) == expected, $"подгрузка при прокрутке: было {first}, стало {RowCount(grid)} из {expected}");
}

// «Пользователи»: галочка права меняет маску в базе и возвращается вторым кликом; окно фильтра.
void BehaveUsers(Window w, Control view, Control grid, Geo geo)
{
    var petr = -1;
    for (var i = 0; i < RowCount(grid); i++)
        if (ItemAt(grid, i) is AdminUserRow { DisplayName: var n } && n == Fixtures.Users[1]) petr = i;
    Expect(petr >= 0, $"строка «{Fixtures.Users[1]}» есть в таблице (индекс {petr})");
    if (petr >= 0)
    {
        // Колонка «Изменение коллекции» — вторая колонка прав: имя, ключ, роль, доступ, просмотр, изменение.
        const int col = 5;
        var pt   = geo.RowCenter(petr, geo.Left(col) + geo.Widths[col] / 2);
        var mask = fx.MaskOf(Fixtures.Users[1]);
        Click(w, pt);
        var on = fx.MaskOf(Fixtures.Users[1]);
        Expect(on == (mask | (int)PermAction.EditCollection), $"клик по галочке дает право: маска {mask} → {on}");
        Click(w, pt);
        var off = fx.MaskOf(Fixtures.Users[1]);
        Expect(off == mask, $"второй клик снимает право: маска {on} → {off}");
        w.MouseMove(geo.Park); Pump();
    }

    var popups = TransientPopups.Count;
    Click(w, geo.Chevron(1));
    var opened = WaitUntil(() => TransientPopups.Count > popups, TimeSpan.FromSeconds(5));
    Expect(opened, "кнопка заголовка открывает окно фильтра");
    TransientPopups.CloseAll();
    Pump();
}

// Что показывает панель деталей: у «Диагностики» и истории — полный текст поля по умолчанию
// (сообщение, объект), у журнала безопасности — первая карточка (момент строки).
static string Detail(Control view, string key)
{
    switch (key)
    {
        case "logs":    return TextOf(view.FindControl<Control>("LogsDetailFullText"));
        case "history": return TextOf(view.FindControl<Control>("HistoryDetailFullText"));
        default:
        {
            var host = view.FindControl<StackPanel>("LogDetailCardsHost");
            var card = host?.Children.OfType<Border>().FirstOrDefault()?.Child as StackPanel;
            return card is { Children.Count: > 1 } ? TextOf(card.Children[1]) : "";
        }
    }
}

// То же, посчитанное из строки данных с индексом i — с чем детали обязаны совпасть.
static string Signature(Control grid, string key, int i) => (key, ItemAt(grid, i)) switch
{
    ("logs",    LogReader.LogRow row)                => row.Message,
    ("history", ConstructorRepository.ChangeLogRow r) => ChangeLogLabels.ObjectText(r.TargetType, r.TargetName, r.Count, r.Detail),
    (_,         SecurityEntry e)                      => e.Moment is { } m ? m.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : e.At,
    _                                                 => "?",
};

static string Short(string s) => s.Length > 48 ? s[..48] + "…" : s;

// ── Таблица через отражение ───────────────────────────────────────────────────

static object? Prop(object o, string name) =>
    o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o);

static int RowCount(Control grid)
{
    if (Prop(grid, "RowCount") is int n) return n;
    if (Prop(grid, "ItemsSource") is System.Collections.ICollection c) return c.Count;
    return 0;
}

static object? ItemAt(Control grid, int i)
{
    if (grid.GetType().GetMethod("ItemAt") is { } m) return m.Invoke(grid, [i]);
    if (Prop(grid, "ItemsSource") is System.Collections.IList l && i >= 0 && i < l.Count) return l[i];
    return null;
}

static double[] ColumnWidths(Control grid)
{
    if (Prop(grid, "ColumnWidths") is double[] w) return w;
    if (Prop(grid, "Columns") is System.Collections.IEnumerable cols)
        return cols.Cast<object>().Select(c => Prop(c, "ActualWidth") is double d ? d : 0).ToArray();
    return [];
}

static double ReadOffsetX(Control grid) =>
    Prop(grid, "OffsetX") is double o ? o : Prop(grid, "HorizontalOffset") is double h ? h : 0;

static string TextOf(Control? c) => c is null ? "" : Prop(c, "Text") as string ?? "";

// ── Безголовая платформа ──────────────────────────────────────────────────────

static void Pump() => Dispatcher.UIThread.RunJobs();

static void Click(Window w, Point pt)
{
    w.MouseMove(pt); w.MouseDown(pt, MouseButton.Left); w.MouseUp(pt, MouseButton.Left); Pump();
}

// Масштаб окна (DPI): у headless-окна он зашит единицей и снаружи не задается — отражением до Show().
static void SetScale(Window w, double scale)
{
    if (Math.Abs(scale - 1.0) < 0.001) return;
    var impl  = w.PlatformImpl!;
    var field = impl.GetType().GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
    if (field is null) throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба — обнови стенд");
    field.SetValue(impl, scale);
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
