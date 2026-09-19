// Стенд геометрии выпадающих панелей: где ОКАЗЫВАЕТСЯ раскрытая панель относительно своего
// якоря — зазор сверху и совпадение левого/правого краев.
//
// Зачем отдельный стенд. Место попапа считает не разметка, а позиционер платформы
// (ManagedPopupPositioner) по РАЗМЕРУ попап-окна и прямоугольнику якоря. Размер попап-окна —
// это измеренный размер содержимого ВМЕСТЕ с внешними полями (Margin), поэтому отступ,
// поставленный ради зазора, участвует и в выравнивании краев. Без настоящего окна, живого
// попап-хоста и разрешенного каскада стилей проверять нечего: модульные тесты (Tests/Unit)
// работают без платформы, а числа из разметки не равны числам на экране.
//
// ВАЖНО про платформу. Headless НЕ УМЕЕТ отдельных попап-окон: HeadlessWindowImpl.CreatePopup()
// возвращает null, поэтому Avalonia кладет попап в OverlayLayer того же окна
// (OverlayPopupHost). Математика места при этом ТА ЖЕ САМАЯ: OverlayPopupHost сам создает
// ManagedPopupPositioner, и ровно его же создает macOS (Avalonia.Native.PopupImpl —
// ManagedPopupPositioner над ManagedPopupPositionerPopupImplHelper). Отличия ровно три:
//   * Scaling у overlay-хоста жестко 1, у настоящего попап-окна = DesktopScaling (Retina = 2),
//     и позиция настоящего окна огрубляется до ЦЕЛОГО ФИЗИЧЕСКОГО пикселя ВНИЗ-ВЛЕВО
//     (ManagedPopupPositionerPopupImplHelper: (int)devicePoint.X) — это до 0.5 логического;
//   * «экран» для overlay-хоста — клиентская область окна, а не рабочая область монитора:
//     срабатывание ограничений (FlipX/SlideX/ResizeX) у края монитора здесь не воспроизводится;
//   * тени настоящего попап-окна здесь нет вовсе (и выключить ее на macOS нельзя: у
//     Avalonia.Native метод SetWindowManagerAddShadowHint ПУСТОЙ, флаг попапа ни на что не
//     влияет).
// Все, что НЕ про эти три пункта (размер плашки, вклад Margin/рамки/обертки/MinWidth,
// выравнивание краев, поведение ограничений), стенд мерит честно.
//
// Сценарий прогоняется дважды: при масштабе окна 1 и при Retina-масштабе 2 — раскладка
// округляется по сетке 1/RenderScaling, и дробный край, если он есть, виден только на 2.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Behaviors;

// Настоящий шрифт (Skia/CoreText): ширина плашки зависит от измеренного текста.
// Порядок важен: UseSkia ДО UseHeadless.
AppBuilder.Configure<Application>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .SetupWithoutStarting();

// Каскад стилей — такой же и в том же порядке, как в App.axaml: проверяются РАЗРЕШЕННЫЕ
// значения отступов и рамок, а не то, что написано в одном отдельно взятом сеттере.
var app     = Application.Current!;
var baseUri = new Uri("avares://SmartBase/");
app.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
foreach (var name in new[] { "Colors", "Icons", "ZoomPopupThemes", "ComboBoxTheme" })
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

// Зазор, сделанный отступом плашки (Margin), живет ВНУТРИ попап-окна: окно становится на этот
// отступ выше, а плашка внутри него съезжает вниз. Видно такой зазор только если попап-окно в
// этой полосе прозрачное — поэтому смотрим, красит ли тема фон попап-окна.
if (app.TryFindResource(typeof(PopupRoot), out var popupRootTheme)
    && popupRootTheme is Avalonia.Styling.ControlTheme theme)
{
    var bg = theme.Setters.OfType<Avalonia.Styling.Setter>()
                  .FirstOrDefault(s => s.Property == TemplatedControl.BackgroundProperty);
    Console.WriteLine("Тема попап-окна (PopupRoot): Background = "
                      + (bg is null
                             ? "сеттера нет (по умолчанию прозрачно)"
                             : $"[{bg.Value?.GetType().Name ?? "null"}] «{bg.Value}»"));
}
else
{
    Console.WriteLine("Тема попап-окна (PopupRoot): ControlTheme не найдена");
}

Scenario(1.0);
Scenario(2.0);

Console.WriteLine();
Console.WriteLine("Готово. Числа выше сняты в headless (OverlayPopupHost, Scaling позиционера = 1):");
Console.WriteLine("размеры плашки и выравнивание ее краев честные, огрубление позиции до "
                  + "физического пикселя");
Console.WriteLine("и тень попап-окна macOS здесь не воспроизводятся.");
return 0;

// ── Сценарий ────────────────────────────────────────────────────────────────────

void Scenario(double renderScaling)
{
    // Окно стенда: справа ComboBox (эталон) и ниже кнопка-якорь навбара. Оба у правого края,
    // но с запасом 240 px до края окна: у самого края включились бы ограничения позиционера.
    var combo = new ComboBox
    {
        Width               = 170,
        Height              = 30,
        HorizontalAlignment = HorizontalAlignment.Right,
        ItemsSource         = new[] { "Первый пункт", "Второй пункт", "Третий пункт", "Четвертый" },
        SelectedIndex       = 0,
    };
    var button = MakeNavdrop();
    var stack  = new StackPanel
    {
        Spacing             = 24,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin              = new Thickness(0, 40, 240, 0),
        Children            = { combo, button },
    };
    var window = new Window { Width = 1000, Height = 600, Content = stack };
    if (renderScaling != 1.0) SetScale(window, renderScaling);
    window.Show();
    Pump(window);

    Console.WriteLine();
    Console.WriteLine(new string('═', 92));
    Console.WriteLine($"МАСШТАБ ОКНА RenderScaling = {window.RenderScaling:F1}   "
                      + $"(зум приложения {SmartBase.Utils.Scaling.Current})");
    Console.WriteLine(new string('═', 92));
    Console.WriteLine($"Токены линий: BorderLinePopup = {window.FindResource("BorderLinePopup")}, "
                      + $"BorderLine = {window.FindResource("BorderLine")}");
    Console.WriteLine($"Якорь-кнопка: {Fmt(RectIn(button, window))}");
    Console.WriteLine($"ComboBox:     {Fmt(RectIn(combo, window))}");

    // ── 1. Эталон: выпадашка ComboBox ───────────────────────────────────────────
    Head("1. ComboBox — эталон (Placement=Bottom по умолчанию, MinWidth=MaxWidth=ширина поля)");

    combo.IsDropDownOpen = true;
    Pump(window);

    var comboHost = Hosts(window).FirstOrDefault();
    if (comboHost is null)
    {
        Console.WriteLine("  ПРОВАЛ: попап-хост не найден");
    }
    else
    {
        var plate = comboHost.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PopupBorder");
        Console.WriteLine($"  попап-хост: {comboHost.GetType().Name}, размер {FmtS(comboHost.Bounds.Size)}, "
                          + $"MinWidth={comboHost.MinWidth} MaxWidth={comboHost.MaxWidth}");
        Console.WriteLine($"  плашка (Border#PopupBorder): Margin={plate.Margin} "
                          + $"BorderThickness={plate.BorderThickness} CornerRadius={plate.CornerRadius} "
                          + $"Padding={plate.Padding}");
        Report(window, comboHost, plate, combo);
    }

    combo.IsDropDownOpen = false;
    Pump(window);

    // ── 2. Флайаут «Тип документа» как есть ─────────────────────────────────────
    Head("2. Flyout: Placement=BottomEdgeAlignedRight + презентер filterFlyout + контент в "
         + "WrapPopupContent");
    Flyout(window, button, "как в приложении", withClass: true, wrap: true);

    // ── 3. Разбор версий ────────────────────────────────────────────────────────
    Head("3. Разбор версий: что именно двигает края");

    Flyout(window, button, "версия «виноват Margin 0,1,0,0»: Margin презентера снят",
           withClass: true, wrap: true, tweak: p => p.Margin = new Thickness(0));

    Flyout(window, button, "версия «виноват BorderLinePopup»: BorderThickness презентера снят",
           withClass: true, wrap: true, tweak: p => p.BorderThickness = new Thickness(0));

    Flyout(window, button, "версия «виноват CornerRadius»: скругление снято",
           withClass: true, wrap: true, tweak: p => p.CornerRadius = new CornerRadius(0));

    Flyout(window, button, "версия «виновата обертка WrapPopupContent»: контент БЕЗ "
           + "LayoutTransformControl", withClass: true, wrap: false);

    Flyout(window, button, "версия «виноваты MinWidth/MaxWidth презентера из темы Fluent»: "
           + "голый презентер Fluent, без класса filterFlyout", withClass: false, wrap: true);

    Flyout(window, button, "контроль: Placement=BottomEdgeAlignedLeft (выравнивание по ЛЕВОМУ краю)",
           withClass: true, wrap: true, placement: PlacementMode.BottomEdgeAlignedLeft);

    Flyout(window, button, "контроль: Placement=Bottom (плашка центрируется по якорю)",
           withClass: true, wrap: true, placement: PlacementMode.Bottom);

    // ── 4. Зум приложения ≠ 100 % ───────────────────────────────────────────────
    //
    // Обертка контента — LayoutTransformControl: при нецелом зуме измеренный размер контента
    // умножается на зум, а плашка-презентер остается ВНЕ зума. Проверяем, не родится ли на
    // этом дробный край.
    Head("4. Тот же флайаут при зуме приложения 110 % (контент под зумом, плашка вне зума)");
    app.Resources["AppZoomTransform"] = new ScaleTransform(1.1, 1.1);
    Pump(window);
    Flyout(window, button, "зум 110 %", withClass: true, wrap: true);
    app.Resources["AppZoomTransform"] = new ScaleTransform(1.0, 1.0);
    Pump(window);

    // ── 5. Якорь у правого края ─────────────────────────────────────────────────
    Head("5. Якорь прижат к ПРАВОМУ краю: ограничения позиционера (SlideX/ResizeX)");
    stack.Margin = new Thickness(0, 40, 4, 0);
    Pump(window);
    Console.WriteLine($"  якорь-кнопка теперь: {Fmt(RectIn(button, window))}");
    Flyout(window, button, "якорь в 4 px от правого края окна", withClass: true, wrap: true);

    // ── 6. Единственный механизм, который двигает плашку ВПРАВО ─────────────────
    //
    // Вправо позиционер уводит плашку ровно в одном случае: она не влезла ЛЕВЫМ краем в
    // «экран» (для overlay-хоста экран = клиентская область окна, для macOS — рабочая область
    // монитора). И тогда это НЕ маленькая поправка: первым срабатывает FlipX — выравнивание
    // переворачивается на противоположный край, и правый край уезжает сразу на всю разницу
    // ширин (96 px). Мелкого сдвига «на пару пикселей вправо» у позиционера нет вовсе.
    Head("6. Якорь у ЛЕВОГО края: FlipX — единственный механизм, уводящий плашку ВПРАВО");
    stack.HorizontalAlignment = HorizontalAlignment.Left;
    stack.Margin = new Thickness(91, 40, 0, 0);   // слева не влезает 96 − 91 = 5 px
    Pump(window);
    Console.WriteLine($"  якорь-кнопка теперь: {Fmt(RectIn(button, window))}");
    Flyout(window, button, "слева не влезает всего 5 px, а правый край уезжает на 96",
           withClass: true, wrap: true);

    window.Close();
    Pump(window);
}

// ── Инструменты ─────────────────────────────────────────────────────────────────

// Кнопка-якорь: копия TypeBtn из NavbarLibrary.axaml (класс navdrop, ширина 170,
// текст слева + шеврон справа).
static Button MakeNavdrop()
{
    var grid    = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    var label   = new TextBlock { Text = "Тип документа", VerticalAlignment = VerticalAlignment.Center };
    var chevron = new PathIcon
    {
        Width  = 12, Height = 8,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
    };
    Grid.SetColumn(chevron, 1);
    grid.Children.Add(label);
    grid.Children.Add(chevron);

    return new Button
    {
        Classes             = { "navdrop" },
        Width               = 170,
        Height              = 30,
        VerticalAlignment   = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Content             = grid,
    };
}

// Содержимое панели — как у ColumnFilterPanel.Build: StackPanel фиксированной ширины
// (PopupContentWidth) с полями 12 по кругу, внутри карточка поиска и карточка списка.
static Control MakeContent(Control host)
{
    var panel = new StackPanel
    {
        Width   = (double)host.FindResource("PopupContentWidth")!,
        Margin  = new Thickness(12),
        Spacing = 2,
    };
    panel.Children.Add(new Border
    {
        Height     = 30,
        Background = Brushes.White,
        Child      = new TextBlock { Text = "Поиск", VerticalAlignment = VerticalAlignment.Center },
    });
    panel.Children.Add(new Border
    {
        Height     = 120,
        Background = Brushes.White,
        Child      = new TextBlock { Text = "Список значений" },
    });
    return panel;
}

static void Flyout(Window window, Control anchor, string name, bool withClass, bool wrap,
                   Action<FlyoutPresenter>? tweak = null,
                   PlacementMode placement = PlacementMode.BottomEdgeAlignedRight)
{
    Console.WriteLine();
    Console.WriteLine($"  {name}");

    // С классом — ровно та плашка, которую показывает приложение: та же фабрика, тот же
    // сдвиг PopupPlate.Nudge. Иначе стенд мерил бы не то, что видит человек.
    var flyout = withClass ? PopupPlate.Filter() : new Avalonia.Controls.Flyout();
    flyout.Placement = placement;
    var content = MakeContent(window);
    flyout.Content = wrap ? WindowZoom.WrapPopupContent(content) : content;
    flyout.ShowAt(anchor);
    Pump(window);

    var host = Hosts(window).FirstOrDefault();
    if (host is null) { Console.WriteLine("    ПРОВАЛ: попап-хост не найден"); return; }

    var presenter = host.GetVisualDescendants().OfType<FlyoutPresenter>().FirstOrDefault();
    if (presenter is null) { Console.WriteLine("    ПРОВАЛ: презентер не найден"); return; }

    if (tweak is not null) { tweak(presenter); Pump(window); }

    Console.WriteLine($"    попап-хост: {host.GetType().Name}, размер {FmtS(host.Bounds.Size)}");
    Console.WriteLine($"    презентер: Margin={presenter.Margin} "
                      + $"BorderThickness={presenter.BorderThickness} Padding={presenter.Padding} "
                      + $"CornerRadius={presenter.CornerRadius}");
    Console.WriteLine($"      MinWidth={presenter.MinWidth} MaxWidth={presenter.MaxWidth} "
                      + $"MinHeight={presenter.MinHeight} MaxHeight={presenter.MaxHeight} "
                      + $"размер {FmtS(presenter.Bounds.Size)}");
    Report(window, host, presenter, anchor, indent: "    ");

    flyout.Hide();
    Pump(window);
}

// Главные числа: зазор и сдвиг краев ВИДИМОЙ плашки относительно якоря.
static void Report(Window window, OverlayPopupHost host, Visual plate, Control anchor,
                   string indent = "  ")
{
    var anchorRect = RectIn(anchor, window);
    var hostRect   = new Rect(HostPos(host, window), host.Bounds.Size);
    var inHost     = plate.TranslatePoint(new Point(0, 0), host) ?? default;
    var plateRect  = new Rect(hostRect.Position + inHost, plate.Bounds.Size);

    Console.WriteLine($"{indent}якорь          {Fmt(anchorRect)}");
    Console.WriteLine($"{indent}попап-хост     {Fmt(hostRect)}");
    Console.WriteLine($"{indent}видимая плашка {Fmt(plateRect)}");
    Console.WriteLine($"{indent}ЗАЗОР (верх плашки − низ якоря)        = {plateRect.Top - anchorRect.Bottom,7:F2}");
    Console.WriteLine($"{indent}зазор по попап-окну (верх хоста − низ) = {hostRect.Top - anchorRect.Bottom,7:F2}");
    Console.WriteLine($"{indent}ЛЕВЫЙ край:  плашка − якорь            = {plateRect.Left - anchorRect.Left,7:F2}");
    // Ожидаемое значение — не ноль, а PopupPlate.Nudge: плашки приложения намеренно сдвинуты
    // вправо, потому что тень попап-окна macOS зрительно уводит их край внутрь.
    var right = plateRect.Right - anchorRect.Right;
    var mark  = Math.Abs(right) < 0.01                     ? "   (край в край)"
              : Math.Abs(right - PopupPlate.Nudge) < 0.01  ? $"   (сдвиг PopupPlate.Nudge = {PopupPlate.Nudge:F0})"
                                                           : "   (НЕ совпадает)";
    Console.WriteLine($"{indent}ПРАВЫЙ край: плашка − якорь            = {right,7:F2}{mark}");
}

// Место попап-хоста в координатах окна. Canvas.Left/Top хост выставляет в кадре отрисовки
// (MediaContext.BeginInvokeOnRender), а кадра в стенде нет — берем то, что позиционер посчитал
// СИНХРОННО (_lastRequestedPosition), и переводим из координат overlay-слоя в координаты окна.
static Point HostPos(OverlayPopupHost host, Window window)
{
    var field = typeof(OverlayPopupHost)
                .GetField("_lastRequestedPosition", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                       "OverlayPopupHost сменил поле позиции — обнови стенд");
    var local   = (Point)field.GetValue(host)!;
    var overlay = host.GetVisualAncestors().OfType<OverlayLayer>().FirstOrDefault();
    var origin  = overlay?.TranslatePoint(new Point(0, 0), window) ?? default;
    return origin + local;
}

// Масштаб окна (Retina): раскладка округляется по сетке 1/RenderScaling. У headless-окна
// масштаб зашит единицей и снаружи не задается — выставляем рефлексией ДО Show().
// Тот же приемник, что в Tests/EditGeometry.
static void SetScale(Window w, double scale)
{
    var impl  = w.PlatformImpl!;
    var field = impl.GetType().GetField("<RenderScaling>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                       "HeadlessWindowImpl сменил поле масштаба — обнови стенд");
    field.SetValue(impl, scale);
}

static IEnumerable<OverlayPopupHost> Hosts(Window w) =>
    w.GetVisualDescendants().OfType<OverlayPopupHost>();

static Rect RectIn(Control c, Window w) =>
    new(c.TranslatePoint(new Point(0, 0), w) ?? default, c.Bounds.Size);

static string Fmt(Rect r) => $"x={r.X,7:F2} y={r.Y,7:F2} ш={r.Width,7:F2} в={r.Height,7:F2} "
                             + $"(правый край {r.Right,7:F2}, низ {r.Bottom,7:F2})";
static string FmtS(Size s) => $"{s.Width:F2}×{s.Height:F2}";

static void Pump(Window window)
{
    Dispatcher.UIThread.RunJobs();
    window.UpdateLayout();
    Dispatcher.UIThread.RunJobs();
}

static void Head(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('─', 92));
    Console.WriteLine(title);
    Console.WriteLine(new string('─', 92));
}
