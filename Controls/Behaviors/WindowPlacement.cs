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
            window.Resized += (_, args) => placement.Resized(args.Reason);
            window.SizeChanged += (_, _) => placement.Queue();
            window.ScalingChanged += (_, _) => placement.ScalingChanged();
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
            placement.AcceptMove();
            if (!placement.UserMoved)
            {
                placement.Queue();
                return;
            }
        }

        RestorePositionAfterLayout(window, position);
    }

    /// <summary>Удержать точку при явном переключении формы. Нативное изменение
    /// размера может также передвинуть окно; это не ручное перемещение.</summary>
    internal static void RestorePositionAfterLayout(Window window, PixelPoint position)
    {
        if (!window.IsVisible) return;
        if (window.GetValue(PlacementProperty) is { } placement)
        {
            placement.Restore(position);
            return;
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
        private bool _resizing;
        private int _show;
        private int _layout;
        private int _restoredLayout = -1;
        private PixelPoint _position;
        private PixelPoint? _pendingMove;
        private bool _moveQueued;
        private PixelPoint? _restorePosition;
        private bool _restoreQueued;

        public bool HasPlacement { get; private set; }
        public bool UserMoved { get; private set; }
        public int Show => _show;

        public void Open()
        {
            EndRestore();
            _show++;
            _layout++;
            _queued = false;
            _opened = true;
            _resizing = false;
            _position = window.Position;
            _pendingMove = null;
            _moveQueued = false;
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
            if (!_placing && !_resizing && _restorePosition is null && _opened && window.IsVisible
                && GetCenterOnMainWindow(window) && window.Position != _position)
            {
                // Win32 может сначала сообщить Move, а затем Resize. Решение о ручном
                // переносе принимается после обоих уведомлений, сохраняя прежнюю опору.
                _pendingMove = window.Position;
                if (_moveQueued) return;
                _moveQueued = true;
                var show = _show;
                Dispatcher.UIThread.Post(() =>
                {
                    if (show != _show) return;
                    _moveQueued = false;
                    AcceptMove();
                }, DispatcherPriority.Loaded);
            }
        }

        public void AcceptMove()
        {
            if (_pendingMove is not { } position) return;
            _pendingMove = null;
            if (_resizing || _restorePosition is not null || !_opened || !window.IsVisible) return;
            UserMoved = true;
            _position = position;
        }

        public void ScalingChanged()
        {
            // При переносе на другой монитор Move может еще ожидать Loaded.
            // В Win32 смена масштаба приходит до перемещения из WM_DPICHANGED:
            // принимаем предшествующий перенос до защиты самого DPI-пересчета.
            AcceptMove();
            Resized(WindowResizeReason.DpiChange);
        }

        public void Resized(WindowResizeReason reason)
        {
            if (!_opened || !window.IsVisible || !GetCenterOnMainWindow(window)) return;
            if (reason == WindowResizeReason.User)
            {
                // Рамку меняет человек: новая точка и размер принадлежат ему.
                // Отложенная коррекция предыдущей формы не должна отменить это действие.
                EndRestore();
                _layout++;
                _queued = false;
                _resizing = false;
                UserMoved = true;
                _position = window.Position;
                _pendingMove = null;
                return;
            }
            if (reason is not (WindowResizeReason.Application or WindowResizeReason.Layout or WindowResizeReason.DpiChange)
                || window.WindowState != WindowState.Normal) return;

            // Resized приходит от платформы до раскладки содержимого. В частности,
            // Win32 при программном resize может прислать и Move: экранные координаты
            // попадают в WINDOWPLACEMENT, который ожидает координаты рабочей области.
            // Такой Move нельзя считать ручным и отменять из-за него центрирование.
            _resizing = true;
            _pendingMove = null;
            if (_restorePosition is not null)
            {
                // Некоторые платформы подтверждают Resize отложенно. Раскладка,
                // выполненная до этого подтверждения, еще не завершает фиксацию.
                _layout++;
                _restoredLayout = -1;
                window.InvalidateMeasure();
                return;
            }
            if (UserMoved || FindMainWindow(window) is null) Restore(_position);
            else Queue();
        }

        public void Restore(PixelPoint position)
        {
            if (!_opened || !window.IsVisible) return;
            AcceptMove();
            // Отменяем прежнее отложенное центрирование: у явного переключения формы
            // есть собственная точка. Повторные изменения до раскладки объединяются.
            _layout++;
            _queued = false;
            if (_restorePosition is null) window.LayoutUpdated += RestoreLayout;
            // Новый выбор может прийти между нативным Move и следующей раскладкой.
            // Координаты окна в этот момент уже сдвинуты; опорой остается начало серии.
            _restorePosition ??= position;
            // Нужна раскладка и при переключении между двумя формами одной высоты.
            window.InvalidateMeasure();
        }

        private void RestoreLayout(object? sender, EventArgs args)
        {
            if (!window.IsVisible) { EndRestore(); return; }
            if (_restorePosition is not { } position) return;
            var show = _show;
            var layout = _layout;
            _restoredLayout = layout;
            Place(position);
            if (show != _show || layout != _layout || _restorePosition is null) return;
            if (_restoreQueued) return;
            _restoreQueued = true;
            // Ждем именно раскладку, а не просто ближайший Loaded: нативный resize
            // мог еще не начаться. Последняя коррекция идет после его уведомлений.
            Dispatcher.UIThread.Post(() =>
            {
                if (show != _show) return;
                _restoreQueued = false;
                // Между раскладкой и Loaded мог прийти еще один выбор типа. Его
                // размер сначала должен пройти собственную нативную раскладку.
                if (_restoredLayout != _layout) return;
                var finalLayout = _layout;
                if (window.IsVisible && _restorePosition is { } final) Place(final);
                if (show != _show || finalLayout != _layout) return;
                EndRestore();
            }, DispatcherPriority.Loaded);
        }

        private void EndRestore()
        {
            window.LayoutUpdated -= RestoreLayout;
            _restorePosition = null;
            _restoreQueued = false;
            _restoredLayout = -1;
            _resizing = false;
        }

        public void Queue()
        {
            if (_queued || !_opened || _restorePosition is not null || UserMoved || !window.IsVisible || !GetCenterOnMainWindow(window)) return;
            _queued = true;
            var show = _show;
            var layout = _layout;
            Dispatcher.UIThread.Post(() =>
            {
                if (show != _show || layout != _layout) return;
                _queued = false;
                try
                {
                    AcceptMove();
                    if (_opened && !UserMoved && window.IsVisible && GetCenterOnMainWindow(window))
                        HasPlacement = Center(window);
                }
                finally { _resizing = false; }
            }, DispatcherPriority.Loaded);
        }

        public void Close()
        {
            EndRestore();
            _show++;
            _layout++;
            _queued = false;
            _opened = false;
            HasPlacement = false;
            _pendingMove = null;
            _moveQueued = false;
        }
    }
}
