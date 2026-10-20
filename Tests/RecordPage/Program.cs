// Стенд этапа 7 плана drawn-grid.md: страница записи (RecordPageView) без экрана, на копии базы
// wide20 (20 свойств всех типов). Кадры панели при масштабе 1 и 2 сравниваются с эталонами в
// --ref (по умолчанию ~/TestData/regress; нет эталона — кадр принимается за эталон: эталоны сняты
// с панели, где карточки были контролами, — рисованный список обязан совпасть с ними). Затем
// поведение (масштаб 1): набор в карточке и в нижнем блоке с синхронизацией и записью в базу, набор
// по Shift и Delete, «Название» снизу, галочка, число, перечитывание при фокусе в редакторе, ручка
// ширины названий, «Добавить свойство», клик по карточке после прокрутки, состояние карточки вне
// окна, редактор под указателем. В конце — замер на 1000 свойствах (wide1000): открытие холодное и
// повторное, объекты в дереве, шаг прокрутки, память; с --gate числа сверяются с ориентирами этапа.
// Карточки — индексы рисованного списка (PropertyList): их прямоугольники считает поверхность,
// редактор над карточкой появляется под указателем.
//
//   dotnet run -c Release --project Tests/RecordPage -- [--db PATH] [--wide PATH] [--out DIR] [--ref DIR] [--only frames|behave|perf|empty|fileauto] [--gate]

using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Tests.ServiceGrids;
using SmartBase.Utils;
using SmartBase.Views.ConstructorDb;

var home    = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dbPath  = Arg("--db")   ?? Path.Combine(home, "TestData", "wide20.db");
var widePath = Arg("--wide") ?? Path.Combine(home, "TestData", "wide1000.db");
var outDir  = Arg("--out")  ?? Path.Combine(home, "TestData", "recordpage");
var refDir  = Arg("--ref")  ?? Path.Combine(home, "TestData", "regress");
var only    = Arg("--only");
var gate    = args.Contains("--gate");
Directory.CreateDirectory(outDir);
Directory.CreateDirectory(refDir);
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (only is not ("empty" or "fileauto") && !File.Exists(dbPath)) { Console.WriteLine($"Нет базы стенда: {dbPath}"); return 1; }

// Порядок важен: UseSkia ДО UseHeadless (как в Tests/GridProto).
AppBuilder.Configure<SmartBase.App>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();
SQLitePCL.Batteries_V2.Init();
SmartBase.Utils.FontScale.Init();
AppSettings.AttachStore(new MemoryStore());
Dispatcher.UIThread.UnhandledException += (_, e) => Console.WriteLine($"    [исключение UI] {e.Exception}");

// Самодостаточная проверка фильтра пустых свойств: собственная малая база, wide20 не требуется.
if (only == "empty") return EmptyPropertiesChecks.Run(outDir);
if (only == "fileauto") return FileAutoChecks.Run(outDir);

// Копия базы: стенд пишет значения свойств.
var copy = Path.Combine(outDir, "page-" + Path.GetFileName(dbPath));
foreach (var suffix in new[] { "", "-wal", "-shm" })
{
    if (File.Exists(dbPath + suffix)) File.Copy(dbPath + suffix, copy + suffix, overwrite: true);
    else if (File.Exists(copy + suffix)) File.Delete(copy + suffix);
}
using var mgr = DatabaseManager.Open(9406, "recordpage", copy);
var repo   = mgr.Constructor;
var col    = repo.GetAllCollections().First();
var cid    = col.Id!.Value;
var rid    = repo.GetRecordsPageAfter(cid, null, null, 1)[0].Id!.Value;
var fields = repo.GetFields(cid).ToDictionary(f => f.Name);
Console.WriteLine($"Стенд: {copy}, коллекция «{col.Name}» ({fields.Count} свойств), запись {rid}");
Console.WriteLine($"Эталоны: {refDir}; кадры: {outDir}");
Console.WriteLine();

var worst   = 0;
var created = 0;
var failed  = 0;

if (only is null or "frames")
{
    foreach (var scale in new[] { 2.0, 1.0 })
    {
        Console.WriteLine($"Масштаб {scale:N0}:");
        var (w, page) = OpenPage(scale, repo, rid, cid);
        var park = new Point(300, 20);   // шапка: наведение там ничего не красит
        Scene(w, page, scale, "base",     () => w.MouseMove(park));
        Scene(w, page, scale, "hover",    () => w.MouseMove(CardCenter(w, Cards(page)[2])));   // центр карточки «Число» — над значением
        Scene(w, page, scale, "select",   () => { Click(w, NameZone(w, CardNamed(page, "Текст"))); w.MouseMove(park); });
        Scene(w, page, scale, "multi",    () => { Click(w, NameZone(w, CardNamed(page, "Число")), RawInputModifiers.Shift); w.MouseMove(park); });
        Scene(w, page, scale, "title",    () => { Click(w, NameZone(w, CardNamed(page, "Название"))); w.MouseMove(park); });
        Scene(w, page, scale, "date",     () => { Click(w, NameZone(w, CardNamed(page, "Дата"))); w.MouseMove(park); });
        Scene(w, page, scale, "grip",     () => { var c = CardNamed(page, "Текст"); Drag(w, GripPoint(w, c), GripPoint(w, c) + new Vector(40, 0)); w.MouseMove(park); });
        Scene(w, page, scale, "props",    () =>
        {
            var grip = page.FindControl<Border>("PropsResizeGrip")!;
            var p = Center(w, grip);
            Drag(w, p, p + new Vector(0, -100));
            w.MouseMove(park);
        });
        Scene(w, page, scale, "preview",  () => { page.FindControl<FooterPage>("FooterBar")!.IsPreview = true; w.MouseMove(park); });
        Scene(w, page, scale, "scrolled", () =>
        {
            var scroll = page.FindControl<ScrollViewer>("PropertiesScroll")!;
            scroll.Offset = new Vector(0, 200);
            w.MouseMove(park);
        });
        w.Close();
        Pump();
    }
    Console.WriteLine();
}

if (only is null or "behave")
{
    Console.WriteLine("Поведение:");
    Behave(repo, rid, cid, fields);
    Console.WriteLine();
}

if (only is null && EmptyPropertiesChecks.Run(outDir) != 0) failed++;
if (only is null && FileAutoChecks.Run(outDir) != 0) failed++;

if (only is null or "perf")
{
    Console.WriteLine("Тысяча свойств:");
    if (File.Exists(widePath)) Perf(widePath);
    else Console.WriteLine($"  нет базы {widePath} — замер пропущен");
    Console.WriteLine();
}


if (created > 0) Console.WriteLine($"Эталонов принято: {created} (сравнивать было не с чем — следующий прогон сравнит с ними).");
if (only is null or "frames")
    Console.WriteLine(worst == 0 ? "Кадры совпадают с эталоном." : $"Кадры расходятся с эталоном: до {worst:N0} пикс. с допуском. Карты различий: {outDir}");
Console.WriteLine(failed == 0 ? "Проверки пройдены." : $"ПРОВАЛЕНО проверок: {failed}.");
return worst == 0 && failed == 0 ? 0 : 2;

// ── Страница ──────────────────────────────────────────────────────────────────

(Window Window, RecordPageView Page) OpenPage(double scale, SmartBase.Database.Repositories.ConstructorRepository r, int recordId, int collectionId)
{
    var w = new Window { Width = 600, Height = 900 };
    SetScale(w, scale);
    var page = new RecordPageView();
    w.Content = page;
    w.Show();
    Settle(w);
    page.Show(r, recordId, collectionId);
    Pump();
    for (var i = 0; i < 8; i++) { Pump(); w.UpdateLayout(); w.CaptureRenderedFrame(); Thread.Sleep(5); }
    return (w, page);
}

// Карточки списка в порядке панели: свойства, «Название» и «Добавить свойство». Список рисованный
// (PropertyList): карточка — индекс в панели, ее прямоугольник считает поверхность.
static PropertyList List(Control page) => page.FindControl<PropertyList>("PropertiesHost")!;

static List<Card> Cards(Control page) =>
    List(page).Cards.Select((c, i) => new Card((RecordPageView)page, i, c.Name)).ToList();

// Карточка по подписи, доведенная в окно целиком (клик мимо окна пришелся бы в нижний блок).
static Card CardNamed(Control page, string name)
{
    var card = Cards(page).FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"карточки «{name}» нет в списке");
    List(page).ScrollIntoView(card.Index);
    Pump(); page.UpdateLayout(); Pump();
    return card;
}

static bool IsSelected(Card card) => List(card.Page).Surface.IsSelected(card.Index);

static bool IsShown(Card card)
{
    var (first, last) = List(card.Page).Surface.VisibleRange();
    return card.Index >= first && card.Index <= last;
}

static double NameColumnWidth(Card card) => List(card.Page).NameColumnWidth;

// Значение карточки, как его рисует список (из модели).
static string CardValue(Card card) => List(card.Page).Cards[card.Index].Value();

static Point Origin(Window w, Control c) => c.TranslatePoint(new Point(0, 0), w) ?? default;
static Point Center(Window w, Control c) { var o = Origin(w, c); return new Point(o.X + c.Bounds.Width / 2, o.Y + c.Bounds.Height / 2); }

static Rect CardRect(Window w, Card card)
{
    var list = List(card.Page);
    var r    = list.CardRect(card.Index);
    var o    = list.Surface.TranslatePoint(r.TopLeft, w) ?? default;
    return new Rect(o, r.Size);
}
static Point CardCenter(Window w, Card card) { var r = CardRect(w, card); return new Point(r.X + r.Width / 2, r.Y + r.Height / 2); }

// Зоны карточки: название (иконка и подпись — выделение), ручка ⋮, значение (правка), сам контрол значения.
static Point NameZone(Window w, Card card)  { var r = CardRect(w, card); return new Point(r.X + 30, r.Y + r.Height / 2); }
static Point GripPoint(Window w, Card card) { var r = CardRect(w, card); return new Point(r.X + 10 + NameColumnWidth(card) + 9, r.Y + r.Height / 2); }
static Point ValueZone(Window w, Card card) { var r = CardRect(w, card); return new Point(r.X + 10 + NameColumnWidth(card) + 18 + 24, r.Y + r.Height / 2); }
static Point ControlCenter(Window w, Card card)
{
    var list = List(card.Page);
    var r    = list.ValueControlRect(card.Index);
    var o    = list.Surface.TranslatePoint(r.TopLeft, w) ?? default;
    return new Point(o.X + r.Width / 2, o.Y + r.Height / 2);
}

// Редактор, стоящий сейчас над карточкой (ставится под указателем и при нажатии); null — его нет.
static TextBox? InlineBox(Card card) => List(card.Page).EditingIndex == card.Index ? List(card.Page).CurrentEditor as TextBox : null;

// ── Сцены ─────────────────────────────────────────────────────────────────────

void Scene(Window w, RecordPageView page, double scale, string name, Action apply)
{
    apply();
    for (var i = 0; i < 6; i++) { Pump(); w.CaptureRenderedFrame(); Thread.Sleep(5); }
    var captured = w.CaptureRenderedFrame();
    if (captured is null) { Console.WriteLine($"  сцена «{name}»: кадр не снят"); worst = int.MaxValue; return; }
    var newPath = Path.Combine(outDir, $"page-new-{name}-x{scale:N0}.png");
    Frames.Save(captured, newPath);

    var refPath = Path.Combine(refDir, $"page-ref-{name}-x{scale:N0}.png");
    if (!File.Exists(refPath))
    {
        File.Copy(newPath, refPath);
        created++;
        Console.WriteLine($"  сцена «{name}»: эталона не было — кадр принят за эталон");
        return;
    }
    var refFrame = Frames.Load(refPath);
    var newFrame = Frames.Load(newPath);
    if (refFrame.PixelSize != newFrame.PixelSize)
    {
        Console.WriteLine($"  сцена «{name}»: РАЗМЕРЫ КАДРОВ РАЗНЫЕ — эталон {refFrame.PixelSize}, кадр {newFrame.PixelSize}");
        worst = int.MaxValue;
        return;
    }
    // Полосы различий — по карточкам, показанным сейчас (их границы в окне).
    var bands = Cards(page).Where(IsShown).Select(c => (c.Name, Top: CardRect(w, c).Y, Bottom: CardRect(w, c).Bottom)).ToList();
    string Band(int x, int y)
    {
        var dy = y / scale;
        foreach (var b in bands) if (dy >= b.Top && dy < b.Bottom) return $"карточка «{b.Name}»";
        return dy < 56 ? "шапка" : "вне карточек";
    }
    var (strict, loose, diff, groups) = Frames.Compare(refFrame, newFrame, tolerance: 24, Band);
    var total = refFrame.PixelSize.Width * (long)refFrame.PixelSize.Height;
    Console.WriteLine($"  сцена «{name}»: различий строго {strict:N0} пикс. ({100.0 * strict / total:N3} %), с допуском 24 — {loose:N0} ({100.0 * loose / total:N3} %)");
    foreach (var (band, count) in groups.OrderByDescending(g => g.Count).Take(8))
        Console.WriteLine($"    {band,-40} {count,8:N0} пикс.");
    if (loose > 0) Frames.Save(diff, Path.Combine(outDir, $"page-diff-{name}-x{scale:N0}.png"));
    worst = Math.Max(worst, loose);
}

// ── Поведение ─────────────────────────────────────────────────────────────────

void Expect(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {what}");
    if (!ok) failed++;
}

void Behave(SmartBase.Database.Repositories.ConstructorRepository r, int recordId, int collectionId, Dictionary<string, DbField> f)
{
    var (w, page) = OpenPage(1, r, recordId, collectionId);
    var changed = 0;
    page.RecordChanged += (_, _) => changed++;
    IReadOnlyList<int>? deleteAsked = null;
    page.PropertiesDeleteRequested += (_, ids) => deleteAsked = ids;
    var addAsked = 0;
    page.AddPropertyRequested += (_, _) => addAsked++;
    var editor = page.FindControl<TextBox>("MarkdownEditor")!;
    var crumb  = page.FindControl<StackPanel>("BottomTitleHost")!;
    string Crumb() => crumb.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
    string Value(string name) => r.GetFieldValues(recordId).GetValueOrDefault(f[name].Id!.Value, "");

    // 1. Набор в карточке текстового свойства: значение в базе, нижний блок открыт на нем и синхронен.
    var textBefore = Value("Текст");
    Click(w, ValueZone(w, CardNamed(page, "Текст")));
    Expect(InlineBox(CardNamed(page, "Текст"))?.IsFocused == true, "клик по значению «Текст» ставит каретку в поле карточки");
    Expect(Crumb() == "Текст", $"нижний блок открылся на «Текст» (крошка: «{Crumb()}»)");
    Key(w, Avalonia.Input.Key.End);   // каретка встала туда, куда кликнули, — печатаем в конец
    Type(w, " abc");
    Expect(Value("Текст") == textBefore + " abc", $"набор в карточке записан в базу: «{Value("Текст")}»");
    Expect(editor.Text == Value("Текст"), "нижний редактор повторяет карточку");
    Expect(changed >= 1, $"правка поднимает RecordChanged ({changed})");

    // 2. Набор снизу: карточка и база следуют.
    Click(w, Center(w, editor));
    Expect(editor.IsFocused, "клик в нижний блок ставит каретку в редактор");
    Key(w, Avalonia.Input.Key.End, RawInputModifiers.Control);
    Type(w, " xyz");
    Expect(Value("Текст") == textBefore + " abc xyz", $"набор снизу записан в базу: «{Value("Текст")}»");
    Expect(CardValue(CardNamed(page, "Текст")) == Value("Текст"), "карточка повторяет нижний редактор");

    // 3. Набор по Shift и Delete.
    Click(w, NameZone(w, CardNamed(page, "Число")), RawInputModifiers.Shift);
    Expect(IsSelected(CardNamed(page, "Текст")) && IsSelected(CardNamed(page, "Число")), "Shift+клик по «Число» добавил его к набору с «Текст»");
    Expect(Crumb() == "Текст", "Shift не переключил нижний блок");
    Key(w, Avalonia.Input.Key.Delete);
    Expect(deleteAsked is not null && deleteAsked.SequenceEqual([f["Текст"].Id!.Value, f["Число"].Id!.Value]),
           $"Delete просит удалить набор в порядке панели: {(deleteAsked is null ? "нет" : string.Join(",", deleteAsked))}");

    // 4. «Название»: карточка выделена, снизу название; набор снизу виден в базе и в карточке.
    var titleBefore = r.GetRecord(recordId)!.Title;
    Click(w, NameZone(w, CardNamed(page, "Название")));
    Expect(IsSelected(CardNamed(page, "Название")) && !IsSelected(CardNamed(page, "Текст")), "клик по «Название» выделяет только его");
    Expect(Crumb() == "Название" && editor.Text == titleBefore, $"снизу — «Название» с текстом записи («{editor.Text}»)");
    Click(w, Center(w, editor));
    Key(w, Avalonia.Input.Key.End, RawInputModifiers.Control);
    Type(w, " tail");
    Expect(r.GetRecord(recordId)!.Title == titleBefore + " tail", $"название снизу записано: «{r.GetRecord(recordId)!.Title}»");
    Expect(CardValue(CardNamed(page, "Название")) == titleBefore + " tail", "карточка «Название» повторяет нижний блок");

    // 5. Галочка.
    var checkBefore = Value("Чекбокс");
    Click(w, ControlCenter(w, CardNamed(page, "Чекбокс")));
    Expect(Value("Чекбокс") != checkBefore, $"клик по галочке пишет значение: «{checkBefore}» → «{Value("Чекбокс")}»");

    // 6. Число: набор в поле, уход фокуса — значение в базе, показ по формату.
    var numBefore = Value("Число");
    Click(w, ValueZone(w, CardNamed(page, "Число")));
    Key(w, Avalonia.Input.Key.End);
    Type(w, "7");
    Click(w, NameZone(w, CardNamed(page, "Дата")));   // уход фокуса
    Expect(Value("Число") != numBefore && Value("Число").EndsWith('7'), $"число записано: «{numBefore}» → «{Value("Число")}»");
    Expect(IsSelected(CardNamed(page, "Дата")) && Crumb() == "Название", "клик по подписи даты выделяет ее, нижний блок не меняет");

    // 7. Перечитывание при фокусе в редакторе не трогает набираемое.
    Click(w, NameZone(w, CardNamed(page, "Текст 1")));   // карточка ниже сгиба — CardNamed прокрутит к ней
    Expect(Crumb() == "Текст 1", $"клик по подписи «Текст 1» открыл его снизу (крошка: «{Crumb()}»)");
    Click(w, Center(w, editor));
    Key(w, Avalonia.Input.Key.End, RawInputModifiers.Control);
    Type(w, "Q");
    page.ReloadFromDb();
    Pump();
    Expect(editor.IsFocused && (editor.Text ?? "").EndsWith('Q') && Crumb() == "Текст 1",
           $"ReloadFromDb при фокусе снизу: редактор в фокусе, текст «{editor.Text}», крошка «{Crumb()}»");
    Expect(Value("Текст 1").EndsWith('Q'), "набранное до перечитывания — в базе");

    // 8. Ручка ⋮: общая ширина колонки названий у всех карточек.
    var wBefore = NameColumnWidth(CardNamed(page, "Текст"));
    var card    = CardNamed(page, "Текст");
    Drag(w, GripPoint(w, card), GripPoint(w, card) + new Vector(40, 0));
    var wAfter = NameColumnWidth(card);
    Expect(Math.Abs(wAfter - (wBefore + 40)) < 1, $"ручка ⋮ сдвинула ширину названий у всех карточек: {wBefore} → {wAfter}");

    // 9. «Добавить свойство» — последний элемент списка (показанных карточек может быть меньше).
    Click(w, NameZone(w, CardNamed(page, "Добавить свойство")));
    Expect(addAsked == 1, "клик по «Добавить свойство» поднимает AddPropertyRequested");

    // 10. Прокрутка вниз и клик по нижней карточке.
    var scroll = page.FindControl<ScrollViewer>("PropertiesScroll")!;
    scroll.Offset = new Vector(0, scroll.Extent.Height);
    Pump(); w.UpdateLayout(); Pump();
    var last = Cards(page).First(c => c.Name == "Текст 5");
    Expect(IsShown(last), "после прокрутки вниз карточка «Текст 5» показана");
    Click(w, NameZone(w, last));
    Expect(IsSelected(CardNamed(page, "Текст 5")) && Crumb() == "Текст 5", "клик по нижней карточке выделяет ее и открывает снизу");

    // 11. Состояние — в модели: карточка, ушедшая из окна и вернувшаяся, выглядит так же. «Текст 5»
    //     выделен и открыт снизу (п. 10); уводим его из окна наверх, печатаем снизу, возвращаем.
    scroll.Offset = new Vector(0, 0);
    Pump(); w.UpdateLayout(); Pump();
    Expect(!IsShown(last), $"после прокрутки наверх карточка «Текст 5» вне окна (показаны {List(page).Surface.VisibleRange()})");
    Expect(List(page).EditingIndex < 0, "редактора над карточками нет (указатель в нижнем блоке, фокус там же)");
    Click(w, Center(w, editor));
    Key(w, Avalonia.Input.Key.End, RawInputModifiers.Control);
    Type(w, "W");
    var back = CardNamed(page, "Текст 5");   // прокрутит к ней
    Expect(IsSelected(back), "выделение «Текст 5» пережило уход карточки из окна");
    Expect(CardValue(back).EndsWith('W') && Value("Текст 5").EndsWith('W'),
           $"набранное снизу, пока карточка была вне окна, видно в ней («{CardValue(back)}») и в базе");
    Expect(Math.Abs(NameColumnWidth(back) - (wBefore + 40)) < 1,
           $"ширина названий у карточек в окне — из модели: {NameColumnWidth(back)}");

    // 12. Редактор под указателем: наведение на значение ставит настоящее поле, уход снимает его.
    w.MouseMove(ValueZone(w, back)); Pump();
    Expect(InlineBox(back) is not null, "наведение на значение ставит над карточкой настоящее поле");
    w.MouseMove(new Point(300, 20)); Pump(); Pump();
    Expect(List(page).EditingIndex < 0, "уход указателя с карточки без фокуса снимает поле");

    w.Close();
    Pump();
}

// ── Тысяча свойств ────────────────────────────────────────────────────────────

void Perf(string path)
{
    using var wide = DatabaseManager.Open(9407, "recordpage-wide", path);
    var r    = wide.Constructor;
    var c    = r.GetAllCollections().First();
    var wcid = c.Id!.Value;
    var wrid = r.GetRecordsPageAfter(wcid, null, null, 1)[0].Id!.Value;
    Console.WriteLine($"  коллекция «{c.Name}»: {r.GetFields(wcid).Count:N0} свойств");

    var w = new Window { Width = 600, Height = 900 };
    var page = new RecordPageView();
    w.Content = page;
    w.Show();
    Settle(w);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var heap0 = GC.GetTotalMemory(true);
    var a0    = GC.GetTotalAllocatedBytes(true);
    var sw    = Stopwatch.StartNew();
    page.Show(r, wrid, wcid);
    var tShow = sw.Elapsed;
    w.UpdateLayout();
    Pump();
    var tLayout = sw.Elapsed;
    w.CaptureRenderedFrame();
    var tFrame  = sw.Elapsed;
    var alloc   = (GC.GetTotalAllocatedBytes(true) - a0) / 1024.0 / 1024;
    var visuals = w.GetVisualDescendants().Count();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var heap = (GC.GetTotalMemory(true) - heap0) / 1024.0 / 1024;
    Console.WriteLine($"  открытие: Show {tShow.TotalMilliseconds:N0} мс, раскладка {(tLayout - tShow).TotalMilliseconds:N0} мс, кадр {(tFrame - tLayout).TotalMilliseconds:N0} мс, итого {tFrame.TotalMilliseconds:N0} мс");
    Console.WriteLine($"  аллокаций {alloc:N1} МБ, куча +{heap:N1} МБ, объектов в дереве {visuals:N0}, карточек в окне {Shown(page)}");

    // Повторное открытие той же записи — теплое: без JIT первого раза, только чтение записи и кадр.
    var sw2 = Stopwatch.StartNew();
    page.Show(r, wrid, wcid);
    var tShow2 = sw2.Elapsed;
    w.UpdateLayout(); Pump(); w.CaptureRenderedFrame();
    var tWarm = sw2.Elapsed;
    Console.WriteLine($"  повторное открытие: Show {tShow2.TotalMilliseconds:N0} мс, до кадра {tWarm.TotalMilliseconds:N0} мс");

    var scroll = page.FindControl<ScrollViewer>("PropertiesScroll")!;
    var steps  = new List<double>();
    for (var i = 0; i < 10; i++)
    {
        var s = Stopwatch.StartNew();
        scroll.Offset = scroll.Offset + new Vector(0, 400);
        Pump(); w.UpdateLayout(); w.CaptureRenderedFrame();
        steps.Add(s.Elapsed.TotalMilliseconds);
    }
    steps.Sort();
    Console.WriteLine($"  шаг прокрутки на экран: медиана {steps[steps.Count / 2]:N1} мс, максимум {steps[^1]:N1} мс, карточек в окне {Shown(page)}");
    w.Close();
    Pump();

    if (gate)
    {
        // Холодное открытие несет JIT первого раза (около 200 мс), поэтому порог у него щадящий;
        // ориентир этапа — повторное открытие и шаг прокрутки за один кадр.
        Expect(tFrame.TotalMilliseconds <= 600, "холодное открытие с кадром не дольше 600 мс");
        Expect(tWarm.TotalMilliseconds <= 100, "повторное открытие с кадром не дольше 100 мс");
        Expect(visuals <= 300, "объектов в дереве не больше 300");
        Expect(steps[steps.Count / 2] <= 16, "шаг прокрутки на экран не дольше кадра (16 мс)");
    }
}

static int Shown(Control page) { var (first, last) = List(page).Surface.VisibleRange(); return last - first + 1; }

// ── Безголовая платформа ──────────────────────────────────────────────────────

static void Pump() => Dispatcher.UIThread.RunJobs();

static void Click(Window w, Point pt, RawInputModifiers mods = RawInputModifiers.None)
{
    w.MouseMove(pt, mods); w.MouseDown(pt, MouseButton.Left, mods); w.MouseUp(pt, MouseButton.Left, mods); Pump();
}

// Движения с зажатой кнопкой несут ее в модификаторах — как настоящая платформа.
static void Drag(Window w, Point from, Point to)
{
    const RawInputModifiers held = RawInputModifiers.LeftMouseButton;
    w.MouseMove(from); w.MouseDown(from, MouseButton.Left); Pump();
    w.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), held); Pump();
    w.MouseMove(to, held); Pump();
    w.MouseUp(to, MouseButton.Left); Pump();
}

static void Key(Window w, Avalonia.Input.Key k, RawInputModifiers m = RawInputModifiers.None) { w.KeyPress(k, m); w.KeyRelease(k, m); Pump(); }

static void Type(Window w, string text) { w.KeyTextInput(text); Pump(); }

// Масштаб окна (DPI): у headless-окна он зашит единицей и снаружи не задается — отражением до Show().
static void SetScale(Window w, double scale)
{
    if (Math.Abs(scale - 1.0) < 0.001) return;
    var impl  = w.PlatformImpl!;
    var field = impl.GetType().GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
    if (field is null) throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба — обнови стенд");
    field.SetValue(impl, scale);
}

static void Settle(Window w)
{
    for (var i = 0; i < 50; i++)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        if (Math.Abs(w.ClientSize.Width - w.Width) < 1 && Math.Abs(w.ClientSize.Height - w.Height) < 1) break;
    }
}

string? Arg(string key)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

/// <summary>Карточка списка свойств в стенде: панель, индекс в списке, подпись.</summary>
sealed record Card(RecordPageView Page, int Index, string Name);

// Настройки процесса — в памяти: раскладка панели и привязки клавиш не читаются с машины.
sealed class MemoryStore : ISettingsDatabase
{
    private readonly Dictionary<string, string> _d = new();
    public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _d[key] = value;
    public void Remove(string key) => _d.Remove(key);
    public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
        [.. _d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
}
