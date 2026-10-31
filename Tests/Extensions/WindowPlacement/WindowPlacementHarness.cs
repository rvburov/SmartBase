// Общий геометрический стенд без зависимости от конкретного расширения.
// Каждый extension-owned exe передает настоящее окно и его варианты интерфейса.
// Движки, файлы пользователя, рабочие базы и внешние отправки не запускаются.
using System.Reflection;
using WorkspaceResizeShift = SmartBase.Tests.Support.NativeResizeShift;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using SmartBase.Controls.Behaviors;
using SmartBase.Utils;

namespace SmartBase.Tests.Extensions.WindowPlacement;

internal sealed record WindowStateCase(string Name, Action<Window> Apply);

internal static class WindowPlacementHarness
{
    private static ClassicDesktopStyleApplicationLifetime _lifetime = null!;
    private static int _assertions;
    private static int _failed;

    public static int Run(string name, Func<Window> create, params WindowStateCase[] states)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("ПРОВАЛ: геометрический стенд расширения не завершился за 45 секунд.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);

        _lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppBuilder.Configure<PlacementTestApp>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithLifetime(_lifetime);
        AppSettings.AttachStore(new MemoryStore());
        FontScale.Init();

        foreach (var before in new[] { false, true })
        foreach (var (zoom, dpi) in new[] { (1.0, 1.0), (1.25, 1.0), (1.0, 1.25) })
        {
            try
            {
                Scaling.SetScale(zoom);
                CheckStates(name, create, states, zoom, dpi, before);
                Console.WriteLine($"  OK: {name}, зум {zoom}, DPI {dpi}, Move до Resize: {before}, повторные состояния и ручная позиция.");
            }
            catch (Exception ex)
            {
                _failed++;
                Console.Error.WriteLine($"  ПРОВАЛ: {name}, зум {zoom}, DPI {dpi}: {ex}");
            }
            finally { Scaling.SetScale(1); }
        }

        Console.WriteLine($"Проверок: {_assertions}; не пройдено сценариев: {_failed}.");
        Console.WriteLine("Настоящие окна в Headless; нативный сдвиг при resize смоделирован, проверка в ОС нужна отдельно.");
        return _failed == 0 ? 0 : 1;
    }

    public static T Required<T>(Window window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"Не найден контрол {name}.");

    private static void CheckStates(string name, Func<Window> create, WindowStateCase[] states, double zoom, double dpi, bool before)
    {
        Check(states.Length >= 2, "Стенду нужны разные состояния настоящего окна.");
        using var fixture = new Fixture(dpi);
        var dialog = fixture.Track(create());
        SetDpi(dialog, dpi);
        dialog.Show(fixture.Main);
        Settle(fixture.Main, dialog);
        Check(SmartBase.Controls.Behaviors.WindowPlacement.GetCenterOnMainWindow(dialog),
            "Настоящее окно не получило глобальное поведение размещения.");
        Check(ReferenceEquals(dialog.Owner, fixture.Main), "Сменился владелец окна расширения.");
        Centered(dialog, fixture.Main);
        var width = dialog.ClientSize.Width;
        var heights = new HashSet<double>();
        using var native = new WorkspaceResizeShift(dialog, before);

        foreach (var manual in new[] { false, true })
        {
            if (manual)
            {
                dialog.Position += new PixelVector(53, 27);
                Settle(fixture.Main, dialog);
            }
            var anchor = dialog.Position;
            var canonical = new Dictionary<string, PixelPoint>();
            for (var cycle = 0; cycle < 3; cycle++)
            foreach (var state in states)
            {
                state.Apply(dialog);
                Settle(fixture.Main, dialog);
                heights.Add(Math.Round(dialog.ClientSize.Height));
                Check(Math.Abs(dialog.ClientSize.Width - width) <= 1,
                    $"{state.Name}: изменилась фиксированная ширина окна.");
                if (manual)
                    Check(dialog.Position == anchor,
                        $"{name}, {state.Name}, обход {cycle + 1}: ручная позиция {anchor} стала {dialog.Position}.");
                else
                {
                    Centered(dialog, fixture.Main);
                    if (canonical.TryGetValue(state.Name, out var position))
                        Check(dialog.Position == position,
                            $"{state.Name}: повторный выбор накопил сдвиг {position} → {dialog.Position}.");
                    else canonical.Add(state.Name, dialog.Position);
                }
            }
        }

        Check(heights.Count >= 2, "Варианты не изменили настоящий ClientSize: путь автоматической высоты не проверен.");
        Check(native.Count >= 6, $"Воспроизведено только {native.Count} нативных перемещений вместо повторных resize.");
        Check(fixture.Main.Position == fixture.InitialPosition, "Изменение окна расширения передвинуло главное окно.");
    }

    private static void Centered(Window child, Window main)
    {
        var mainSize = PixelSize.FromSize(main.FrameSize ?? main.ClientSize, main.DesktopScaling);
        var childSize = PixelSize.FromSize(child.FrameSize ?? child.ClientSize, child.DesktopScaling);
        var expected = new PixelRect(main.Position, mainSize).CenterRect(new PixelRect(childSize)).Position;
        // Большое окно ограничивается рабочей областью так же, как при первом показе:
        // у верхней/левой границы физический центр закономерно не совпадает с главным.
        var screen = child.Screens.ScreenFromWindow(main) ?? child.Screens.ScreenFromPoint(main.Position);
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            expected = new PixelPoint(
                Math.Clamp(expected.X, area.X, Math.Max(area.X, area.Right - childSize.Width)),
                Math.Clamp(expected.Y, area.Y, Math.Max(area.Y, area.Bottom - childSize.Height)));
        }
        Check(Math.Abs(child.Position.X - expected.X) <= 1 && Math.Abs(child.Position.Y - expected.Y) <= 1,
            $"Окно потеряло центр: ожидается {expected}, получено {child.Position}; размер {child.ClientSize}, DPI {child.DesktopScaling}.");
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
            var current = string.Join(";", windows.Select(w => $"{w.IsVisible}:{w.Position}:{w.ClientSize}:{w.RenderScaling}"));
            stable = current == previous ? stable + 1 : 0;
            if (stable >= 3) return;
            previous = current;
        }
        throw new InvalidOperationException("Размещение не стабилизировалось за 60 проходов.");
    }

    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void SetDpi(Window window, double scale)
    {
        if (scale == 1) return;
        var impl = window.PlatformImpl!;
        var field = impl.GetType().GetField("<RenderScaling>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба — обновите стенд.");
        field.SetValue(impl, scale);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<Window> _windows = [];
        public PixelPoint InitialPosition { get; } = new(120, 80);
        public Window Main { get; }

        public Fixture(double dpi)
        {
            Main = Track(new Window { Width = 1000, Height = 700, Position = InitialPosition,
                WindowStartupLocation = WindowStartupLocation.Manual, Content = new Border() });
            LineMetrics.SetFollowsAppZoom(Main, true);
            SetDpi(Main, dpi);
            _lifetime.MainWindow = Main;
            Main.Show();
            Settle(Main);
        }

        public T Track<T>(T window) where T : Window { _windows.Add(window); return window; }

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
        public override void OnFrameworkInitializationCompleted() { }
    }

    private sealed class MemoryStore : ISettingsDatabase
    {
        private readonly Dictionary<string, string> _values = new();
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string value) => _values[key] = value;
        public void Remove(string key) => _values.Remove(key);
        public IReadOnlyList<string> KeysWithPrefix(string prefix) =>
            [.. _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))];
    }
}
