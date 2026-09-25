// Стенд геометрии правки: проверяет, что текст НЕ СДВИГАЕТСЯ, когда контрол переходит в
// активное (редактируемое) состояние.
//
// Зачем отдельный стенд. Смещение рождается не в коде, а в РАСКЛАДКЕ: Border сдвигает
// содержимое ровно на толщину своей рамки, а толщина приходит динамическим токеном
// (LineMetrics) и зависит от окна. Без настоящего окна, живого фокуса и разрешенного каскада
// стилей проверять нечего — модульные тесты (Tests/Unit) работают без платформы.
//
// Правило, которое стенд стережет, — в docs/developer/ui/theming.md, раздел «Текст не
// двигается при смене состояния»: меняешь толщину рамки по состоянию — компенсируй отступ
// на ту же величину.
//
// Рисованная сетка таблицы (этап 4 плана drawn-grid.md) проверяется отдельным процессом с
// ключом --drawn: переключатель разработчика читается раз на процесс, а нынешняя таблица выше
// уже прочитала его выключенным. Ее ячейка сверяется по ПИКСЕЛЯМ: показ рисует сама сетка, у
// него нет контрола, чью точку можно было бы взять.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Avalonia.Media;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Widgets;

// НАСТОЯЩИЙ шрифт (Skia/CoreText), не подставной: у подставного высоты строк целые, и дефект
// разных сеток округления невидим. Порядок важен: UseSkia ДО UseHeadless.
AppBuilder.Configure<Application>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();

// Каскад стилей поднимаем такой же, как в App.axaml: проверяется именно РАЗРЕШЕННОЕ значение
// отступа и рамки, а не то, что написано в одном отдельно взятом сеттере.
var app     = Application.Current!;
var baseUri = new Uri("avares://SmartBase/");
app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
foreach (var name in new[] { "Colors", "Icons", "ComboBoxTheme" })
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
Console.OutputEncoding = System.Text.Encoding.UTF8;

var failed = 0;

if (args.Contains("--drawn"))
{
    Environment.SetEnvironmentVariable("SMARTBASE_DRAWN_GRID", "1");
    DrawnCellNoJump();
    Console.WriteLine(failed == 0 ? "\nРисованная сетка: все проверки пройдены." : $"\nРисованная сетка: не пройдено проверок: {failed}.");
    return failed == 0 ? 0 : 1;
}

// ── Поля даты и времени: фокус не двигает текст ─────────────────────────────────
//
// Бокс (Border.timeField) в фокусе рисует двойную рамку. Если отступ не уменьшить на ту же
// линию, текст уезжает вправо, а иконка справа — влево.

NoJump("поле даты: фокус не двигает текст", () => new DateField { Width = 220 });
NoJump("поле времени: фокус не двигает текст", () => new TimeField { Width = 120 });
NoJump("редактор даты (карточка/ячейка): фокус не двигает текст", () => new DateEditor { Width = 220 });

// ── Заголовок: показ (TextBlock) и правка (TextBox) стоят в одной точке ─────────
//
// Название записи/коллекции/заметки живет двумя контролами, которые подменяют друг друга по
// клику. Меряем, где НА САМОМ ДЕЛЕ начинается глиф в каждом из них.

TitleNoJump("заголовок записи", new SmartBase.Controls.Components.NavbarPage());
TitleNoJump("заголовок коллекции", new SmartBase.Controls.Components.NavbarConstructor());

// ── Ячейка таблицы: выделение и правка стоят в одном месте ──────────────────────
//
// Настоящая таблица на временной базе, настоящие клики: первый выделяет ячейку, второй по той
// же ячейке открывает правку. Меряем и рамку выделения, и начало текста в обоих состояниях.

CellNoJump();

// ── Плоский режим: высоту задает контейнер, а не виджет ─────────────────────────
//
// Жесткая высота 34 внутри строки карточки (40) делала «Дату» на 2px выше соседей и сдвигала
// вниз все, что под ней. Flat обязан ее снять — как это делает FileEditor.

FlatHasNoOwnHeight("редактор даты в плоском режиме не держит своей высоты",
                   () => new DateEditor { Flat = true });
FlatHasNoOwnHeight("редактор файла в плоском режиме не держит своей высоты",
                   () => new FileEditor { Flat = true });

// ── Рисованная сетка — тем же стендом, отдельным процессом ─────────────────────

RunDrawnChild();

Console.WriteLine(failed == 0 ? "\nВсе проверки пройдены." : $"\nНе пройдено проверок: {failed}.");
return failed == 0 ? 0 : 1;

// Положение поля ввода внутри виджета до и после прихода фокуса. Меряем сам TextBox, а не глиф:
// его собственный Padding в обоих состояниях один, поэтому горизонтальный сдвиг поля — это ровно
// сдвиг текста. По вертикали требование строже фактического: текст в поле центрован, и пока рамка
// растет симметрично, он бы устоял и при съехавшем вниз боксе. Оставлено намеренно — правильно
// скомпенсированный бокс не двигается вовсе, и лишняя строгость ловит перекос рамки.
void NoJump(string name, Func<Control> make)
{
    Console.WriteLine($"\n{name}");

    var widget = make();
    var window = new Window { Width = 400, Height = 200, Content = widget };
    LineMetrics.SetIsManaged(window, true);   // токены BorderLine/BorderLineFocus живут на окне
    SetRetinaScale(window);
    window.Show();
    window.UpdateLayout();

    var input = widget.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
    if (input is null) { Expect(false, "поле ввода не найдено"); return; }

    var box = widget.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("timeField"));
    if (box is null) { Expect(false, "бокс timeField не найден"); return; }

    var rest     = Origin(input, window);
    var restLine = box.BorderThickness.Left;
    var restPad  = box.Padding.Left;

    input.Focus();
    window.UpdateLayout();

    var focused  = Origin(input, window);
    var focusLine = box.BorderThickness.Left;
    var focusPad  = box.Padding.Left;

    Console.WriteLine($"    рамка+отступ: покой {restLine:F2}+{restPad:F2}={restLine + restPad:F2}"
                      + $" → фокус {focusLine:F2}+{focusPad:F2}={focusLine + focusPad:F2}");
    Console.WriteLine($"    поле: покой ({rest.X:F2}, {rest.Y:F2}) → фокус ({focused.X:F2}, {focused.Y:F2})");

    // Без этой проверки стенд прошел бы вхолостую: не сработай :focus-within, геометрия
    // совпала бы просто потому, что состояние не менялось.
    Expect(focusLine > restLine, "фокус не утолщил рамку — состояние не сменилось, мерить нечего");
    Expect(Near(restLine + restPad, focusLine + focusPad), "рамка выросла, а отступ не уменьшился на столько же");
    Expect(Near(rest.X, focused.X), $"по горизонтали уехал на {focused.X - rest.X:F2}px");
    Expect(Near(rest.Y, focused.Y), $"по вертикали уехал на {focused.Y - rest.Y:F2}px");
}

// Где начинается ТЕКСТ в надписи показа и в поле правки. У надписи начало глифа = ее
// собственное начало (Padding у TextBlock нет), у поля — начало презентера текста внутри
// шаблона: именно он несет и отступ поля, и внутреннюю прокрутку.
void TitleNoJump(string name, Control navbar)
{
    Console.WriteLine($"\n{name}");

    var window = new Window { Width = 800, Height = 200, Content = navbar };
    LineMetrics.SetIsManaged(window, true);
    SetRetinaScale(window);
    window.Show();
    window.UpdateLayout();

    var display = navbar.GetVisualDescendants().OfType<TextBlock>()
                        .FirstOrDefault(t => t.Name == "TitleDisplay");
    var box     = navbar.GetVisualDescendants().OfType<TextBox>()
                        .FirstOrDefault(t => t.Name == "TitleBox");
    if (display is null || box is null) { Expect(false, "пара надпись/поле не найдена"); return; }

    const string sample = "Типы данных";
    display.Text = sample;
    box.Text     = sample;
    box.IsEnabled = true;

    display.IsVisible = true;
    box.IsVisible     = false;
    window.UpdateLayout();
    var shown = Origin(display, window);

    display.IsVisible = false;
    box.IsVisible     = true;
    window.UpdateLayout();
    box.Focus();
    window.UpdateLayout();

    var presenter = box.GetVisualDescendants()
                       .OfType<Avalonia.Controls.Presenters.TextPresenter>()
                       .FirstOrDefault();
    if (presenter is null) { Expect(false, "презентер текста поля не найден"); return; }
    var edited = Origin(presenter, window);

    Console.WriteLine($"    кегль надписи {display.FontSize:F1}/{display.FontWeight}, поля {box.FontSize:F1}/{box.FontWeight}"
                      + $", масштаб окна {window.RenderScaling:F1}, высота поля {box.Bounds.Height:F2}");
    Console.WriteLine($"    надпись: ({shown.X:F2}, {shown.Y:F2}) высота {display.Bounds.Height:F2}");
    Console.WriteLine($"    поле:    ({edited.X:F2}, {edited.Y:F2}) высота {presenter.Bounds.Height:F2}"
                      + $", отступ поля {box.Padding}, рамка {box.BorderThickness}");

    Expect(Near(shown.X, edited.X), $"по горизонтали текст уезжает на {edited.X - shown.X:F2}px");
    Expect(Near(shown.Y, edited.Y), $"по вертикали текст уезжает на {edited.Y - shown.Y:F2}px");
    // Совпадения координат мало: они сходятся и при разной высоте, просто потому, что числа
    // делятся нацело. Разной высоте нельзя быть в принципе — каждый контрол центрируется в
    // шапке отдельно, и на дробной высоте строки округления разъезжаются на пол-точки.
    Expect(Near(display.Bounds.Height, box.Bounds.Height),
           $"надпись {display.Bounds.Height:F2} и поле {box.Bounds.Height:F2} разной высоты — "
           + "на дробном кегле округление их разведет");
}

void CellNoJump()
{
    Console.WriteLine("\nячейка таблицы: выделение и правка в одном месте");

    var dir = Path.Combine(Path.GetTempPath(), "sb-editgeometry-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        using var mgr = SmartBase.Database.DatabaseManager.Open(9401, "editgeometry", Path.Combine(dir, "test.db"));
        var repo = mgr.Constructor;
        var col  = repo.AddCollection(new SmartBase.Core.Models.DbCollection { Name = "Проверка" });
        var cid  = col.Id!.Value;
        repo.AddField(new SmartBase.Core.Models.DbField
        {
            CollectionId = cid, Name = "Текст", FieldType = "text", Position = 1000,
        });
        repo.AddRecord(new SmartBase.Core.Models.DbRecord { CollectionId = cid, Title = "Список", Position = 10 });

        var window = new Window { Width = 900, Height = 500 };
        SetRetinaScale(window);
        var table  = new SmartBase.Views.ConstructorDb.TableView();
        window.Content = table;
        window.Show();
        Pump();
        table.Load(repo, cid, repo.ReadOpenSnapshot(cid));
        Pump();

        // Ячейка «Название» первой строки: нулевая колонка служебная («№»).
        var cell = table.GetVisualDescendants().OfType<DataGridCell>()
                        .Where(c => c.Bounds.Width > 40).OrderBy(c => Origin(c, window).Y).ThenBy(c => Origin(c, window).X)
                        .Skip(1).FirstOrDefault();   // нулевая колонка служебная («№»)
        if (cell is null) { Expect(false, "ячейка не найдена — таблица не построилась"); return; }

        var cellAt = Origin(cell, window);
        var point  = new Point(cellAt.X + cell.Bounds.Width / 2, cellAt.Y + cell.Bounds.Height / 2);

        Click(window, point);   // первый клик — выделение
        var selFrame = FrameRect(table);
        var selText  = TextOrigin(cell, window);

        Click(window, point);   // второй клик по той же ячейке — правка
        var editFrame = FrameRect(table);
        var editText  = TextOrigin(cell, window);
        var ownFrames = OwnFrames(cell, window);

        Console.WriteLine($"    ячейка: ({cellAt.X:F2}, {cellAt.Y:F2}) {cell.Bounds.Width:F2}×{cell.Bounds.Height:F2}");
        foreach (var line in ownFrames) Console.WriteLine($"      чужая рамка: {line}");
        Console.WriteLine($"    рамка:  выделение {ShowRect(selFrame)} → правка {ShowRect(editFrame)}");
        Console.WriteLine($"    текст:  выделение {ShowPoint(selText)} → правка {ShowPoint(editText)}");

        Expect(selText is not null && editText is not null, "текст ячейки не найден в одном из состояний");
        if (selText is { } a && editText is { } b)
        {
            Expect(Near(a.X, b.X), $"текст уезжает по горизонтали на {b.X - a.X:F2}px");
            Expect(Near(a.Y, b.Y), $"текст уезжает по вертикали на {b.Y - a.Y:F2}px");
        }
        Expect(selFrame is not null, "на выделенной ячейке нет рамки");
        Expect(editFrame is not null, "в правке рамка выделения пропала — вместо нее нарисует свою тема");
        // Рамка вокруг ячейки обязана быть ровно одна — наша. Тема Fluent рисует
        // сфокусированному полю свою, на линию сетки уже, и квадрат «съезжает» на втором клике.
        Expect(ownFrames.Count == 0, $"в правке рисуется еще {ownFrames.Count} рамка(и) помимо выделения");
        if (selFrame is { } f1 && editFrame is { } f2)
        {
            Expect(Near(f1.X, f2.X) && Near(f1.Y, f2.Y), $"рамка уезжает на ({f2.X - f1.X:F2}, {f2.Y - f1.Y:F2})");
            Expect(Near(f1.Width, f2.Width) && Near(f1.Height, f2.Height),
                   $"рамка меняет размер: {f1.Width:F2}×{f1.Height:F2} → {f2.Width:F2}×{f2.Height:F2}");
        }
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
    }
}

// Рисованная сетка в дочернем процессе (--drawn): переключатель разработчика включается на весь
// процесс, а этот уже построил нынешнюю таблицу с выключенным.
void RunDrawnChild()
{
    var exe  = Environment.ProcessPath!;
    var self = typeof(Program).Assembly.Location;
    var psi  = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? new System.Diagnostics.ProcessStartInfo(exe, $"\"{self}\" --drawn")
        : new System.Diagnostics.ProcessStartInfo(exe, "--drawn");
    psi.UseShellExecute        = false;
    psi.RedirectStandardOutput = true;
    psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
    psi.Environment["SMARTBASE_DRAWN_GRID"] = "1";
    using var child = System.Diagnostics.Process.Start(psi)!;
    Console.Write(child.StandardOutput.ReadToEnd());
    child.WaitForExit();
    if (child.ExitCode != 0) failed++;
}

// Ячейка рисованной сетки: первый клик выделяет, второй по той же ячейке открывает накладной
// редактор. Кадр снимается в обоих состояниях, и в области ячейки сравниваются «чернила» — темные
// серые пиксели текста: их рамка (левый, верхний, правый, нижний край) обязана совпасть до
// физического пикселя. Рамка выделения (синяя) и подсветка наведения (светлая) в чернила не
// попадают; фон под текстом и синий край рамки сверяются отдельно — рамка стоит над редактором,
// наведение видно сквозь него. У списка и даты справа в правке появляется кнопка — эта полоса
// из сравнения исключена.
void DrawnCellNoJump()
{
    Console.WriteLine("\nрисованная сетка: показ и правка ячейки в одном месте (масштаб 2)");

    var dir = Path.Combine(Path.GetTempPath(), "sb-editgeometry-drawn-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        using var mgr = SmartBase.Database.DatabaseManager.Open(9402, "editgeometry-drawn", Path.Combine(dir, "test.db"));
        var repo = mgr.Constructor;
        var col  = repo.AddCollection(new SmartBase.Core.Models.DbCollection { Name = "Проверка" });
        var cid  = col.Id!.Value;
        int Field(string name, string type, int pos, string config = "{}") => repo.AddField(new SmartBase.Core.Models.DbField
        {
            CollectionId = cid, Name = name, FieldType = type, Position = pos, ConfigJson = config,
        }).Id!.Value;
        var fText   = Field("Текст", "text", 1000);
        var fNumber = Field("Число", "number", 1010);
        var fSelect = Field("Список", "select", 1020, "{\"options\":[\"Первая\",\"Вторая\"]}");
        var fDate   = Field("Дата", "date", 1030);
        var rec = repo.AddRecord(new SmartBase.Core.Models.DbRecord { CollectionId = cid, Title = "Список", Position = 10 });
        var rid = rec.Id!.Value;
        repo.SetFieldValue(rid, fText, "Проба текста");
        repo.SetFieldValue(rid, fNumber, "1234.5");
        repo.SetFieldValue(rid, fSelect, "Вторая");
        repo.SetFieldValue(rid, fDate, "2026-09-25");

        var window = new Window { Width = 1200, Height = 400 };
        SetRetinaScale(window);
        var table  = new SmartBase.Views.ConstructorDb.TableView();
        window.Content = table;
        window.Show();
        Pump();
        table.Load(repo, cid, repo.ReadOpenSnapshot(cid));
        Pump();

        var panel = table.DrawnPanel;
        if (panel is null) { Expect(false, "рисованная сетка не включилась — переключатель прочитан без переменной"); return; }
        var surface = panel.Surface;
        for (var i = 0; i < 100 && !surface.VisibleCellsLoaded(); i++) Pump();

        foreach (var (key, name, rightPad) in new[]
                 {
                     (SmartBase.ViewModels.ConstructorDb.TableRowVm.TitleKey, "«Название»", 0.0),
                     (fText.ToString(),   "текст",  0.0),
                     (fNumber.ToString(), "число",  0.0),
                     (fSelect.ToString(), "список", 32.0),
                     (fDate.ToString(),   "дата",   32.0),
                 })
        {
            var c = surface.Columns.IndexOf(key);
            if (c < 0) { Expect(false, $"{name}: колонки нет в раскладке"); continue; }
            var cell   = surface.CellRect(0, c);
            var origin = surface.TranslatePoint(new Point(0, 0), window)!.Value;
            var at     = new Point(origin.X + cell.X + cell.Width / 2, origin.Y + cell.Y + cell.Height / 2);
            // Область сравнения: ячейка без рамки выделения (два пикселя экрана внутрь) и без линий.
            var inner  = new Rect(origin.X + cell.X + 2, origin.Y + cell.Y + 2,
                                  cell.Width - surface.LineWidth - 4 - rightPad, cell.Height - surface.LineWidth - 4);

            Click(window, at);
            var shown = window.CaptureRenderedFrame()!;
            Click(window, at);
            var editor = panel.Editor.Editor;
            if (!table.IsCellEditing || editor is null) { Expect(false, $"{name}: второй клик не открыл правку"); continue; }
            // Каретка мигает, а поле в фокусе ее рисует: красим в красный — в «чернила» он не попадает
            // (прозрачную кисть поле подменяет своей), сравнивается только текст.
            foreach (var box in editor.GetVisualDescendants().OfType<TextBox>().Prepend(editor as TextBox).OfType<TextBox>())
            {
                box.CaretBrush = Brushes.Red;
                box.SelectionStart = box.SelectionEnd = 0;
            }
            // Новая кисть доходит до кадра со следующим миганием каретки — ждем пару циклов.
            for (var i = 0; i < 25; i++) { Pump(); window.CaptureRenderedFrame(); }
            var edited = window.CaptureRenderedFrame()!;

            if (Environment.GetEnvironmentVariable("EDITGEOM_DUMP") is { Length: > 0 } dump)
            {
                using (var f1 = File.Create(Path.Combine(dump, $"{name}-shown.png"))) shown.Save(f1);
                using (var f2 = File.Create(Path.Combine(dump, $"{name}-edited.png"))) edited.Save(f2);
                if (editor is TextBox tbx)
                {
                    var pres = tbx.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
                    Console.WriteLine($"      поле {tbx.TranslatePoint(default, window)} {tbx.Bounds.Size}, презентер {pres.TranslatePoint(default, window)} {pres.Bounds.Size}"
                                      + $", ячейка {origin.X + cell.X} ширина {cell.Width}, текст {pres.TextLayout.Width:F3}/{pres.TextLayout.WidthIncludingTrailingWhitespace:F3}, предел {pres.TextLayout.MaxWidth:F3}");
                }
            }
            var a = InkBounds(shown, inner, 2.0);
            var b = InkBounds(edited, inner, 2.0);
            var frames = OwnFrames(editor, window);
            Console.WriteLine($"    {name}: редактор {editor.GetType().Name}, чернила показ {ShowPx(a)} → правка {ShowPx(b)}");
            foreach (var line in frames) Console.WriteLine($"      чужая рамка: {line}");
            Expect(a is not null && b is not null, $"{name}: текст не найден в одном из состояний");
            if (a is { } ia && b is { } ib)
                Expect(ia == ib, $"{name}: текст сдвигается: {ShowPx(a)} → {ShowPx(b)}");
            Expect(frames.Count == 0, $"{name}: у редактора своя рамка ({frames.Count})");

            // Край рамки выделения — синий в обоих состояниях: рамка лежит над редактором.
            var edge = new Point(origin.X + cell.X + 0.25, origin.Y + cell.Y + cell.Height / 2);
            Expect(IsAccent(Pixel(shown, edge, 2.0)) && IsAccent(Pixel(edited, edge, 2.0)),
                   $"{name}: рамка выделения не видна в одном из состояний");
            // Фон под текстом (наведение) одинаков: редактор прозрачный, подсветка видна сквозь него.
            var bg = new Point(origin.X + cell.X + 3, origin.Y + cell.Y + 3);
            Expect(Pixel(shown, bg, 2.0) == Pixel(edited, bg, 2.0),
                   $"{name}: фон ячейки в правке другой: #{Pixel(shown, bg, 2.0):X6} → #{Pixel(edited, bg, 2.0):X6}");

            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None);
            window.KeyRelease(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None);
            Pump();
            Expect(!table.IsCellEditing, $"{name}: Escape не закрыл правку");
        }
        window.Close();
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* временная папка */ }
    }
}

// Прямоугольник «чернил» (темных серых пикселей текста) в области (DIP) — в физических пикселях.
static (int L, int T, int R, int B)? InkBounds(Avalonia.Media.Imaging.WriteableBitmap bmp, Rect area, double scale)
{
    using var fb = bmp.Lock();
    var x0 = (int)Math.Floor(area.X * scale); var y0 = (int)Math.Floor(area.Y * scale);
    var x1 = (int)Math.Ceiling(area.Right * scale); var y1 = (int)Math.Ceiling(area.Bottom * scale);
    x1 = Math.Min(x1, fb.Size.Width); y1 = Math.Min(y1, fb.Size.Height);
    int l = int.MaxValue, t = int.MaxValue, r = -1, bt = -1;
    var row = new byte[fb.RowBytes];
    for (var y = Math.Max(0, y0); y < y1; y++)
    {
        System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, fb.RowBytes);
        for (var x = Math.Max(0, x0); x < x1; x++)
        {
            int bl = row[x * 4], g = row[x * 4 + 1], rd = row[x * 4 + 2];
            var hi = Math.Max(rd, Math.Max(g, bl)); var lo = Math.Min(rd, Math.Min(g, bl));
            if (hi >= 170 || hi - lo >= 40) continue;
            l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); bt = Math.Max(bt, y);
        }
    }
    return r < 0 ? null : (l, t, r, bt);
}

// Цвет пикселя как 0xRRGGBB: кадр безголовой платформы бывает и RGBA, и BGRA.
static uint Pixel(Avalonia.Media.Imaging.WriteableBitmap bmp, Point p, double scale)
{
    using var fb = bmp.Lock();
    var x = (int)(p.X * scale); var y = (int)(p.Y * scale);
    var px = new byte[4];
    System.Runtime.InteropServices.Marshal.Copy(fb.Address + y * fb.RowBytes + x * 4, px, 0, 4);
    var rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
    var (r, g, bl) = rgba ? (px[0], px[1], px[2]) : (px[2], px[1], px[0]);
    return (uint)(r << 16 | g << 8 | bl);
}

// Синий акцент: синего заметно больше, чем красного.
static bool IsAccent(uint rgb) => (int)(rgb & 0xFF) - (int)((rgb >> 16) & 0xFF) > 80;

static string ShowPx((int L, int T, int R, int B)? r) => r is { } v ? $"[{v.L}..{v.R}]×[{v.T}..{v.B}]" : "нет";

// Что вообще рисует видимую рамку внутри ячейки в режиме правки: ищем все, у чего есть
// ненулевая обводка, — так видно, не спорят ли за одно место две рамки сразу.
static List<string> OwnFrames(Control cell, Visual root)
{
    var found = new List<string>();
    foreach (var v in cell.GetVisualDescendants().OfType<Visual>())
    {
        var (brush, thickness, kind) = v switch
        {
            Border b       => ((IBrush?)b.BorderBrush, b.BorderThickness.Left, "Border"),
            Avalonia.Controls.Shapes.Rectangle r => (r.Stroke, r.StrokeThickness, "Rectangle"),
            _              => (null, 0.0, ""),
        };
        if (brush is null || thickness <= 0 || !v.IsVisible) continue;
        var at = v.TranslatePoint(new Point(0, 0), root);
        found.Add($"{kind} «{(v as Control)?.Name ?? v.GetType().Name}» {ShowPoint(at)}"
                  + $" {v.Bounds.Width:F2}×{v.Bounds.Height:F2} толщина {thickness:F2} кисть {brush}");
    }
    return found;
}

// Синяя рамка выделения ячейки: единственный Border в слое CellSelectionLayer.
static Rect? FrameRect(Control table)
{
    var layer = table.GetVisualDescendants().OfType<Canvas>().FirstOrDefault(c => c.Name == "CellSelectionLayer");
    var box   = layer?.Children.OfType<Border>().FirstOrDefault();
    if (layer is null || box is null) return null;
    var at = box.TranslatePoint(new Point(0, 0), layer);
    return at is { } p ? new Rect(p, box.Bounds.Size) : null;
}

// Начало текста ячейки: надпись показа или презентер поля правки — что сейчас в дереве.
static Point? TextOrigin(Control cell, Visual root)
{
    Visual? text = cell.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault();
    text ??= cell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
    return text?.TranslatePoint(new Point(0, 0), root);
}

static string ShowRect(Rect? r) => r is { } v ? $"({v.X:F2}, {v.Y:F2}) {v.Width:F2}×{v.Height:F2}" : "нет";
static string ShowPoint(Point? p) => p is { } v ? $"({v.X:F2}, {v.Y:F2})" : "нет";

static void Click(Window w, Point at)
{
    w.MouseMove(at);
    w.MouseDown(at, Avalonia.Input.MouseButton.Left);
    w.MouseUp(at, Avalonia.Input.MouseButton.Left);
    Pump();
}

static void Pump()
{
    for (var i = 0; i < 40; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
}

void FlatHasNoOwnHeight(string name, Func<Control> make)
{
    Console.WriteLine($"\n{name}");

    var widget = make();
    var window = new Window { Width = 400, Height = 200 };
    SetRetinaScale(window);
    var host   = new Border { Height = 40, Child = widget };   // строка карточки свойства
    window.Content = host;
    LineMetrics.SetIsManaged(window, true);
    window.Show();
    window.UpdateLayout();

    var box = widget.GetVisualDescendants().OfType<Border>().FirstOrDefault();
    Console.WriteLine($"    высота бокса {box?.Bounds.Height ?? -1:F1} в строке {host.Bounds.Height:F1}");
    Expect(box is not null && box.Bounds.Height <= host.Bounds.Height,
           "бокс выше строки-контейнера — карточка будет выше соседних");
}

static Point Origin(Visual v, Visual root) => v.TranslatePoint(new Point(0, 0), root) ?? new Point(double.NaN, double.NaN);

// Масштаб 2 (Retina): раскладка округляется по сетке 1/RenderScaling, и именно на ней
// показ и правка расходились на полпикселя — при масштабе 1 обе ветки округляются до целого
// и дефект математически невозможен. У headless-окна масштаб зашит единицей и снаружи не
// задается — выставляем рефлексией ДО Show(), чтобы вся раскладка шла по честной сетке.
static void SetRetinaScale(Window w)
{
    var impl  = w.PlatformImpl!;
    var field = impl.GetType().GetField("<RenderScaling>k__BackingField",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    if (field is null) throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба — обнови стенд");
    field.SetValue(impl, 2.0);
}

static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

void Expect(bool ok, string message)
{
    if (ok) return;
    failed++;
    Console.WriteLine($"    ПРОВАЛ: {message}");
}
