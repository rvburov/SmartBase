using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace SmartBase.Controls.Behaviors;

/// <summary>
/// Открывает дополнительные окна по центру главного окна, независимо от владельца
/// модальности. Подключается глобальным стилем Window; главное окно и заставку не двигает.
/// Размер берется после раскладки, вместе с рамкой и масштабом экрана. Пока человек не
/// передвинул окно, авторазмер и зум сохраняют центр; новый показ снова начинает с центра.
/// </summary>
public static class WindowPlacement
{
    public static readonly AttachedProperty<bool> CenterOnMainWindowProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("CenterOnMainWindow", typeof(WindowPlacement));

    private static readonly AttachedProperty<Placement?> PlacementProperty =
        AvaloniaProperty.RegisterAttached<Window, Placement?>("Placement", typeof(WindowPlacement));

    public static bool GetCenterOnMainWindow(Window window) => window.GetValue(CenterOnMainWindowProperty);
    public static void SetCenterOnMainWindow(Window window, bool value) => window.SetValue(CenterOnMainWindowProperty, value);

    static WindowPlacement()
    {
        CenterOnMainWindowProperty.Changed.AddClassHandler<Window>((window, args) =>
        {
            if (args.NewValue is not true || window.GetValue(PlacementProperty) is not null) return;

            var placement = new Placement(window);
            window.SetValue(PlacementProperty, placement);
            window.Opened += (_, _) => placement.Open();
            window.SizeChanged += (_, _) => placement.Queue();
            window.ScalingChanged += (_, _) => placement.Queue();
            window.PropertyChanged += (_, change) =>
            {
                if (change.Property == TopLevel.FrameSizeProperty || change.Property == TopLevel.ClientSizeProperty)
                    placement.Queue();
            };
            window.PositionChanged += (_, _) => placement.Moved();
            window.Closed += (_, _) => placement.Close();
        });
    }

    // Владелец ShowDialog остается прежним: он отвечает за блокировку родителя, а не
    // за геометрию. В стендах без desktop lifetime опорой служит корень цепочки Owner.
    internal static Window? FindMainWindow(Window window)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } main } && main.IsVisible && main.WindowState != WindowState.Minimized)
            return ReferenceEquals(window, main) ? null : main;

        Window? root = null;
        for (var owner = window.Owner as Window; owner is not null; owner = owner.Owner as Window)
            if (owner.IsVisible && owner.WindowState != WindowState.Minimized) root = owner;
        return root;
    }

    internal static bool Center(Window window)
    {
        if (!window.IsVisible || FindMainWindow(window) is not { } main) return false;

        // Position и WorkingArea — физические пиксели. FrameSize/ClientSize — DIP:
        // у главного и дополнительного окна масштаб может отличаться.
        var mainSize = PixelSize.FromSize(main.FrameSize ?? main.ClientSize, main.DesktopScaling);
        var size = PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.DesktopScaling);
        if (mainSize.Width <= 0 || mainSize.Height <= 0 || size.Width <= 0 || size.Height <= 0) return false;

        var position = new PixelRect(main.Position, mainSize).CenterRect(new PixelRect(size)).Position;
        var screen = window.Screens.ScreenFromWindow(main) ?? window.Screens.ScreenFromPoint(main.Position);
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            position = new PixelPoint(
                Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - size.Width)),
                Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height)));
        }

        if (window.GetValue(PlacementProperty) is { } placement) placement.Place(position);
        else window.Position = position;
        return true;
    }

    /// <summary>
    /// Баннер или переключение формы не должны возвращать координаты, снятые до Show.
    /// В режиме автоцентрирования размер обработает общее поведение; после ручного
    /// перемещения сохраняем выбранное человеком место.
    /// </summary>
    internal static void KeepPositionAfterLayout(Window window, PixelPoint position)
    {
        if (!window.IsVisible) return;
        if (GetCenterOnMainWindow(window))
        {
            if (window.GetValue(PlacementProperty) is not { HasPlacement: true } placement) return;
            if (!placement.UserMoved)
            {
                placement.Queue();
                return;
            }
        }

        var current = window.GetValue(PlacementProperty);
        var show = current?.Show;
        void Restore(object? sender, EventArgs args)
        {
            window.LayoutUpdated -= Restore;
            if (window.IsVisible && current?.Show == show && window.Position != position) window.Position = position;
        }
        window.LayoutUpdated += Restore;
    }

    private sealed class Placement(Window window)
    {
        private bool _placing;
        private bool _queued;
        private bool _opened;
        private int _show;
        private PixelPoint _position;

        public bool HasPlacement { get; private set; }
        public bool UserMoved { get; private set; }
        public int Show => _show;

        public void Open()
        {
            _show++;
            _queued = false;
            _opened = true;
            HasPlacement = false;
            UserMoved = false;
            if (!GetCenterOnMainWindow(window)) return;

            // CLR Opened приходит после назначения Owner и первой раскладки. Еще одна
            // коррекция после layout учитывает WindowZoom, DialogFit и нативную рамку.
            HasPlacement = Center(window);
            Queue();
        }

        public void Place(PixelPoint position)
        {
            _placing = true;
            try
            {
                _position = position;
                if (window.Position != position) window.Position = position;
                // Платформа может подтвердить Move позже: до PositionChanged getter
                // еще возвращает старую точку. Ожидаем запрошенную, не считаем ее drag.
            }
            finally { _placing = false; }
        }

        public void Moved()
        {
            if (!_placing && HasPlacement && window.IsVisible && window.Position != _position)
                UserMoved = true;
        }

        public void Queue()
        {
            if (_queued || !_opened || UserMoved || !window.IsVisible || !GetCenterOnMainWindow(window)) return;
            _queued = true;
            var show = _show;
            Dispatcher.UIThread.Post(() =>
            {
                if (show != _show) return;
                _queued = false;
                if (_opened && !UserMoved && window.IsVisible && GetCenterOnMainWindow(window))
                    HasPlacement = Center(window);
            }, DispatcherPriority.Loaded);
        }

        public void Close()
        {
            _show++;
            _queued = false;
            _opened = false;
            HasPlacement = false;
        }
    }
}
