// Стенд размещения окон. Проверяет фактические координаты после Show/ShowDialog и
// завершенного layout, а не внутреннюю функцию расчета позиции.
//
// Настройки живут в памяти; главное окно стенда — обычный Window без Workspace и БД.
// Загружается настоящий App с глобальным стилем Window: забытое подключение поведения
// должно уронить проверку так же, как неверная арифметика центрирования.
//
// Ограничения Headless 11.2.3: FrameSize=null, один экран 1920x1280 с Scaling=1,
// BeginMoveDrag ничего не делает. Ручное перемещение эмулируется Position, DPI — тем же
// приемом с RenderScaling, что в Tests/EditGeometry. Рамки ОС и смена настоящих мониторов
// здесь не проверяются. Проверка DialogFit намеренно идет только при DPI=1.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Utils;
using Placement = SmartBase.Controls.Behaviors.WindowPlacement;

namespace SmartBase.Tests.WindowPlacement;

internal static class Program
{
    private static ClassicDesktopStyleApplicationLifetime _lifetime = null!;
    private static int _assertions;
    private static int _failed;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("ПРОВАЛ: стенд размещения окон не завершился за 45 секунд.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);

        _lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };
        // Порядок важен: UseSkia ДО UseHeadless. Lifetime передается ДО инициализации
        // Application штатным API. Наследник сохраняет настоящий App.Initialize со
        // скомпилированным XAML и стилями, но не запускает монтирование рабочих баз.
        AppBuilder.Configure<PlacementTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithLifetime(_lifetime);
        AppSettings.AttachStore(new MemoryStore());
        FontScale.Init();
        Scaling.SetScale(1);

        Run("новые окна не накапливают каскадное смещение", FreshWindows);
        Run("Hide/Show сбрасывает ручную позицию и берет новое положение главного", RepeatedShow);
        Run("вложенный ShowDialog центрируется по главному и сохраняет владельца", NestedDialog);
        Run("Show без владельца использует главное окно приложения", UnownedWindow);
        Run("SizeToContent растет и уменьшается вокруг центра главного", ContentChanges);
        Run("изменение формы до Show не сохраняет исходную позицию", ContentBeforeShow);
        Run("живой зум сохраняет центр окна", LiveZoom);
        Run("DPI 1, 1.25 и 2 учитывается в физических координатах", DisplayScales);
        Run("DialogFit ограничивает размер и не заменяет центр главного центром экрана", FitToScreen);
        Run("ручная позиция сохраняется при layout и изменении содержимого", ManualMove);
        Run("нет главного окна, отказ от поведения и закрытие с ожидающим layout", Lifetimes);
        Run("настоящее окно системного свойства: ошибка, повторный показ, ручная позиция", PropertyDialog);

        Console.WriteLine($"\nПроверок: {_assertions}; не пройдено сценариев: {_failed}.");
        Console.WriteLine("Проверено в Headless; рамки окон и перенос между настоящими мониторами требуют проверки в ОС.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action check)
    {
        Scaling.SetScale(1);
        try
        {
            check();
            Console.WriteLine($"  OK: {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.Error.WriteLine($"  ПРОВАЛ: {name}\n    {ex}");
        }
        finally
        {
            Scaling.SetScale(1);
        }
    }

    private static void FreshWindows()
    {
        using var f = new Fixture();
        PixelPoint? previous = null;
        for (var i = 0; i < 4; i++)
        {
            var child = f.New();
            child.Show(f.Main);
            Settle(f.Main, child);
            Check(Placement.GetCenterOnMainWindow(child), "Глобальный стиль не включает центрирование Window.");
            Centered(child, f.Main);
            if (previous is { } p) Check(child.Position == p, "Следующее окно сместилось относительно предыдущего.");
            previous = child.Position;
            child.Close();
        }
        Check(f.Main.Position == f.InitialPosition, "Показ дочернего окна передвинул главное.");
    }

    private static void RepeatedShow()
    {
        using var f = new Fixture();
        var child = f.New();
        child.Show(f.Main);
        Settle(f.Main, child);
        for (var i = 0; i < 3; i++)
        {
            child.Position += new PixelVector(67, 43);
            child.Hide();
            f.Main.Position += new PixelVector(31, 19);
            child.Show(f.Main);
            Settle(f.Main, child);
            Centered(child, f.Main);
        }
    }

    private static void NestedDialog()
    {
        using var f = new Fixture();
        var parent = f.New(520, 360);
        parent.Show(f.Main);
        Settle(f.Main, parent);
        parent.Position += new PixelVector(200, 90);
        var nested = f.New(300, 180);
        var result = nested.ShowDialog<bool>(parent);
        Settle(f.Main, parent, nested);
        Check(ReferenceEquals(nested.Owner, parent), "Сменился владелец модального окна.");
        Centered(nested, f.Main);
        Check(CenterDistance(nested, parent) > 100, "Стенд не различает центр родительского и главного окна.");
        nested.Close(true);
        Settle(f.Main, parent);
        Check(result.IsCompletedSuccessfully && result.Result, "После закрытия не вернулся результат модального окна.");
    }

    private static void UnownedWindow()
    {
        using var f = new Fixture();
        var child = f.New();
        child.Show();
        Settle(f.Main, child);
        Check(child.Owner is null, "Центрирование само назначило владельца немодальному окну.");
        Centered(child, f.Main);
    }

    private static void ContentChanges()
    {
        using var f = new Fixture();
        var content = new Border { Height = 100 };
        var child = f.AutoHeight(content);
        child.Show(f.Main);
        Settle(f.Main, child);
        Centered(child, f.Main);
        var shortHeight = child.ClientSize.Height;
        content.Height = 420;
        Settle(f.Main, child);
        Check(child.ClientSize.Height > shortHeight + 200, "Изменение содержимого не увеличило высоту окна.");
        Centered(child, f.Main);
        content.Height = 100;
        Settle(f.Main, child);
        Check(Math.Abs(child.ClientSize.Height - shortHeight) <= 1, "Автовысота не вернулась к исходной.");
        Centered(child, f.Main);

        var autoContent = new Border { Width = 180, Height = 90 };
        var both = f.Track(new Window
        {
            SizeToContent = SizeToContent.WidthAndHeight, Content = autoContent,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        });
        both.Show(f.Main);
        Settle(f.Main, both);
        var narrow = both.ClientSize.Width;
        autoContent.Width = 480;
        Settle(f.Main, both);
        Check(both.ClientSize.Width > narrow + 200, "Изменение содержимого не увеличило ширину окна.");
        Centered(both, f.Main);
    }

    private static void ContentBeforeShow()
    {
        using var f = new Fixture();
        var content = new Border { Height = 90 };
        var child = f.AutoHeight(content);
        child.Position = new PixelPoint(5, 7);
        content.Height = 300;
        content.Height = 160;
        child.Show(f.Main);
        Settle(f.Main, child);
        Centered(child, f.Main);
        Check(child.Position != new PixelPoint(5, 7), "Восстановилась позиция, сохраненная до открытия.");

        // При пустом содержимом первый расчет еще не может разместить окно. Поздний
        // размер должен повторить попытку, даже если на Opened центр еще не был найден.
        var delayedContent = new Border { Height = 0 };
        var delayed = f.AutoHeight(delayedContent);
        delayed.Show(f.Main);
        Settle(f.Main, delayed);
        Check(delayed.ClientSize.Height <= 1, "Стенд не создал окно с еще не заданной высотой.");
        delayedContent.Height = 180;
        Settle(f.Main, delayed);
        Centered(delayed, f.Main);
    }

    private static void LiveZoom()
    {
        using var f = new Fixture();
        var child = f.New();
        child.Show(f.Main);
        Settle(f.Main, child);
        var initial = child.ClientSize;
        foreach (var zoom in new[] { 1.3, 0.7, 1.0 })
        {
            Scaling.SetScale(zoom);
            Settle(f.Main, child);
            Check(Math.Abs(child.ClientSize.Width - initial.Width * zoom) <= 2,
                "Габариты окна не прошли через настоящий WindowZoom.");
            Centered(child, f.Main);
        }
        Check(f.Main.Position == f.InitialPosition, "Изменение зума передвинуло главное окно.");
    }

    private static void DisplayScales()
    {
        foreach (var scale in new[] { 1.0, 1.25, 2.0 })
        {
            using var f = new Fixture(scale);
            var parent = f.New(420, 240, scale);
            parent.Show(f.Main);
            Settle(f.Main, parent);
            Check(Math.Abs(parent.DesktopScaling - scale) < 0.001, "Headless не применил заданный DPI.");
            Centered(parent, f.Main);
            parent.Position += new PixelVector(80, 50);
            var child = f.New(300, 180, scale);
            var result = child.ShowDialog<bool>(parent);
            Settle(f.Main, parent, child);
            Centered(child, f.Main);
            child.Close(false);
            Check(result.IsCompletedSuccessfully, "Закрытие вложенного окна при заданном DPI не завершило задачу.");
        }

        // Масштаб может смениться без изменения DIP-размера окна. Одной подписки на
        // SizeChanged недостаточно: физическая рамка уже другая, центр надо пересчитать.
        using var live = new Fixture();
        var existing = live.New();
        existing.Show(live.Main);
        Settle(live.Main, existing);
        var initialSize = existing.ClientSize;
        foreach (var scale in new[] { 1.25, 2.0, 1.0 })
        {
            SetScale(existing, scale, notify: true);
            Settle(live.Main, existing);
            Check(existing.ClientSize == initialSize, "Проверка чистой смены DPI неожиданно изменила DIP-размер.");
            Check(Math.Abs(existing.DesktopScaling - scale) < 0.001, "Платформа не изменила масштаб открытого окна.");
            Centered(existing, live.Main);
        }
    }

    private static void FitToScreen()
    {
        using var f = new Fixture();
        // Центр main по Y совпадает с экраном, по X — намеренно нет. После ограничения
        // высокой формы точный центр main помещается в рабочую область без зажима.
        var area = f.Main.Screens.Primary!.WorkingArea;
        f.Main.Position = new PixelPoint(120,
            area.Y + (area.Height - (int)f.Main.ClientSize.Height) / 2);
        var child = f.New(400, 1800);
        DialogFit.SetLimitToScreen(child, true);
        child.Show(f.Main);
        Settle(f.Main, child);
        Check(child.ClientSize.Height < 1800 && child.ClientSize.Height <= area.Height,
            "DialogFit не ограничил высоту формы рабочей областью.");
        Centered(child, f.Main);
        Check(child.Position.X >= area.X && child.Position.Y >= area.Y,
            "Окно после DialogFit начинается за рабочей областью.");
    }

    private static void ManualMove()
    {
        using var f = new Fixture();
        var content = new Border { Height = 100 };
        var child = f.AutoHeight(content);
        child.Show(f.Main);
        Settle(f.Main, child);
        var manual = child.Position + new PixelVector(91, 57);
        child.Position = manual;
        var positionEvents = 0;
        child.PositionChanged += (_, _) => positionEvents++;
        for (var i = 0; i < 20; i++) Settle(f.Main, child);
        Check(child.Position == manual && positionEvents == 0,
            "Холостой layout возвращает вручную перемещенное окно в центр.");
        content.Height = 430;
        Settle(f.Main, child);
        Check(child.ClientSize.Height > 300, "Стенд не изменил авторазмер после ручного перемещения.");
        Check(child.Position == manual, "Изменение содержимого стерло ручное положение окна.");

        child.Hide();
        child.Show(f.Main);
        Settle(f.Main, child);
        Centered(child, f.Main);
        content.Height = 150;
        Settle(f.Main, child);
        Centered(child, f.Main);
    }

    private static void Lifetimes()
    {
        using (var f = new Fixture(registerMain: false))
        {
            var parent = f.New(500, 300);
            parent.Show(f.Main);
            Settle(f.Main, parent);
            parent.Position += new PixelVector(150, 70);
            var child = f.New(280, 160);
            child.Show(parent);
            Settle(f.Main, parent, child);
            Centered(child, f.Main);
            Check(f.Main.Position == f.InitialPosition, "Корневое окно без desktop main передвинулось само.");
        }

        using (var f = new Fixture())
        {
            var disabled = f.New();
            Placement.SetCenterOnMainWindow(disabled, false);
            disabled.WindowStartupLocation = WindowStartupLocation.Manual;
            disabled.Position = new PixelPoint(33, 44);
            disabled.Show(f.Main);
            Settle(f.Main, disabled);
            Check(disabled.Position == new PixelPoint(33, 44), "Явно отключенное поведение продолжает центрировать окно.");

            var content = new Border { Height = 100 };
            var closing = f.AutoHeight(content);
            closing.Show(f.Main);
            Settle(f.Main, closing);
            content.Height = 400;
            closing.Close();
            Settle(f.Main, closing);
            Check(!closing.IsVisible, "Ожидающий callback вновь показал закрытое окно.");
            Scaling.SetScale(1.1);
            Settle(f.Main);
            Check(f.Main.Position == f.InitialPosition, "Закрытие дочернего окна испортило размещение главного.");
        }

        Check(_lifetime.MainWindow is null, "Стенд сохранил закрытое главное окно в lifetime.");
        var standalone = new Window { Width = 300, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        try
        {
            standalone.Show();
            Settle(standalone);
            Check(standalone.IsVisible, "Окно запуска без владельца и main не открылось.");
        }
        finally { standalone.Close(); }
    }

    private static void PropertyDialog()
    {
        using var f = new Fixture();
        PixelPoint? normalPosition = null;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var dialog = f.Track(new PropertyEditorDialog("Название"));
            var name = Required<TextBox>(dialog, "NameBox");
            var save = Required<Button>(dialog, "SaveBtn");
            var banner = Required<NoticeBanner>(dialog, "NoticeBanner");
            if (cycle == 0)
            {
                // Реальный ShowError вызван ДО открытия. Старый локальный помощник
                // подписывал LayoutUpdated и потом возвращал окно к исходному (0,0).
                name.Text = "";
                Dispatcher.UIThread.RunJobs();
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(banner.IsVisible, "Системный конструктор не показал ошибку без обращения к БД.");
            }

            var result = dialog.ShowDialog<bool>(f.Main);
            Settle(f.Main, dialog);
            Centered(dialog, f.Main);
            name.Text = "Название";
            Settle(f.Main, dialog);
            Check(!banner.IsVisible, "Изменение названия не скрыло ошибку.");
            Centered(dialog, f.Main);
            if (normalPosition is { } previous)
                Check(dialog.Position == previous, "Следующее окно свойства открылось с накопленным сдвигом.");
            normalPosition = dialog.Position;

            var normalHeight = dialog.ClientSize.Height;
            name.Text = "";
            Settle(f.Main, dialog);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(f.Main, dialog);
            Check(banner.IsVisible && dialog.ClientSize.Height > normalHeight,
                "Ошибка в настоящей форме не изменила авторазмер окна.");
            Centered(dialog, f.Main);

            var manual = dialog.Position + new PixelVector(67, 41);
            dialog.Position = manual;
            name.Text = "Переименованное свойство";
            Settle(f.Main, dialog);
            Check(dialog.Position == manual, "Скрытие ошибки в настоящей форме стерло ручное положение.");
            name.Text = "";
            Settle(f.Main, dialog);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(f.Main, dialog);
            Check(dialog.Position == manual, "Повторная ошибка в настоящей форме стерла ручное положение.");
            dialog.Close(false);
            Settle(f.Main);
            Check(result.IsCompletedSuccessfully && !result.Result, "Не вернулся результат закрытия настоящего диалога.");
        }
    }

    private static T Required<T>(Window window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"Не найден контрол {name}.");

    private static Point PhysicalCenter(Window window)
    {
        var size = window.FrameSize ?? window.ClientSize;
        var scale = window.DesktopScaling;
        return new Point(window.Position.X + size.Width * scale / 2,
                         window.Position.Y + size.Height * scale / 2);
    }

    private static double CenterDistance(Window left, Window right)
    {
        var a = PhysicalCenter(left);
        var b = PhysicalCenter(right);
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static void Centered(Window child, Window main)
    {
        var error = CenterDistance(child, main);
        Check(error <= 1.0,
            $"Центр окна {child.GetType().Name} смещен на {error:F2} px: "
            + $"позиция {child.Position}, размер {child.ClientSize}, DPI {child.DesktopScaling}; "
            + $"центры {PhysicalCenter(child)} и {PhysicalCenter(main)}.");
    }

    private static void Settle(params Window[] windows)
    {
        string? previous = null;
        var stable = 0;
        for (var pass = 0; pass < 60; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            foreach (var window in windows)
                if (window.IsVisible) window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var current = string.Join(";", windows.Select(w =>
                $"{w.IsVisible}:{w.Position}:{w.ClientSize}:{w.RenderScaling}"));
            stable = current == previous ? stable + 1 : 0;
            if (stable >= 3) return;
            previous = current;
        }
        throw new InvalidOperationException("Размещение/раскладка окна не стабилизировались за 60 проходов.");
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void SetScale(Window window, double scale, bool notify = false)
    {
        if (scale == 1 && !notify) return;
        var impl = window.PlatformImpl!;
        var field = impl.GetType().GetField("<RenderScaling>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба — обновите стенд.");
        field.SetValue(impl, scale);
        if (notify)
        {
            var changed = impl.GetType().GetProperty("ScalingChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(impl) as Action<double>
                ?? throw new InvalidOperationException("HeadlessWindowImpl сменил callback масштаба — обновите стенд.");
            changed(scale);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<Window> _windows = [];
        public Window Main { get; }
        public PixelPoint InitialPosition { get; } = new(120, 80);

        public Fixture(double scale = 1, bool registerMain = true)
        {
            Main = Track(new Window
            {
                Width = 1000, Height = 700, Position = InitialPosition,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = new Border(),
            });
            LineMetrics.SetFollowsAppZoom(Main, true);
            SetScale(Main, scale);
            _lifetime.MainWindow = registerMain ? Main : null;
            Main.Show();
            Settle(Main);
            Check(Main.Position == InitialPosition, "Главное окно не сохранило заданную позицию.");
        }

        public T Track<T>(T window) where T : Window
        {
            _windows.Add(window);
            return window;
        }

        public Window New(double width = 420, double height = 240, double scale = 1)
        {
            var window = Track(new Window
            {
                Width = width, Height = height, Content = new Border(),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            });
            SetScale(window, scale);
            return window;
        }

        public Window AutoHeight(Control content) => Track(new Window
        {
            Width = 420, MinHeight = 0, SizeToContent = SizeToContent.Height,
            Content = content, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        });

        public void Dispose()
        {
            _lifetime.MainWindow = null;
            foreach (var window in _windows.AsEnumerable().Reverse())
                if (window.PlatformImpl is not null) window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class PlacementTestApp : SmartBase.App
    {
        public PlacementTestApp() { }

        // App.Initialize остается настоящим: его AvaloniaXamlLoader.Load(this)
        // заменен компилятором XAML на загрузку разметки базового SmartBase.App.
        // Пропускается только прикладной запуск с реестром, сплэшем и Workspace.
        public override void OnFrameworkInitializationCompleted() { }
    }

    private sealed class MemoryStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];
    }
}
