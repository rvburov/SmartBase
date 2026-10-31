using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using SmartBase.Tests.Support;

namespace SmartBase.Tests.WindowPlacement;

/// <summary>Причина изменения размера и порядок нативных уведомлений без изменения форм.</summary>
internal static class NativeEventChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void Run(Window owner, Action<bool, string> check, Action<Window> settle)
    {
        foreach (var before in new[] { false, true })
        {
            foreach (var reason in new[] { WindowResizeReason.User, WindowResizeReason.Unspecified })
                ManualResize(owner, reason, before, check, settle);
            foreach (var manual in new[] { false, true })
                DpiChange(owner, manual, before, check, settle);
            DpiChange(owner, manual: true, before, check, settle, pendingMove: true);
        }
    }

    private static void ManualResize(Window owner, WindowResizeReason reason, bool before,
        Action<bool, string> check, Action<Window> settle)
    {
        var window = NewWindow(owner, settle);
        try
        {
            var position = window.Position + new PixelVector(83, 41);
            var size = new Size(window.ClientSize.Width + 20, window.ClientSize.Height + 35);
            if (before) Move(window, position);
            NotifyResize(window, size, reason);
            if (!before) Move(window, position);
            settle(window);
            var context = $"{reason}, {(before ? "Move перед Resize" : "Resize перед Move")}";
            check(window.Position == position, context + ": системная коррекция отменила выбранное положение.");
            check(window.ClientSize == size, context + ": стенд не применил настоящий размер платформы.");

            // После ручного изменения следующий программный resize должен сохранить
            // новую опору, а не вернуть окно в его старый центр.
            using var shift = new NativeResizeShift(window, before);
            NotifyResize(window, size.WithHeight(size.Height + 25), WindowResizeReason.Layout);
            settle(window);
            check(shift.Count > 0, context + ": не было моделируемого системного переноса.");
            check(window.Position == position, context + ": следующий авторазмер потерял ручную опору.");
        }
        finally { window.Close(); }
    }

    private static void DpiChange(Window owner, bool manual, bool before,
        Action<bool, string> check, Action<Window> settle, bool pendingMove = false)
    {
        var window = NewWindow(owner, settle);
        try
        {
            var anchor = window.Position;
            if (manual)
            {
                anchor += new PixelVector(67, 31);
                Move(window, anchor);
                // При переходе на другой монитор ScalingChanged может прийти прежде,
                // чем Loaded обработает настоящий перенос. Его опора уже должна сохраниться.
                if (!pendingMove) settle(window);
            }

            var platform = window.PlatformImpl!;
            var scaling = platform.GetType().GetField("<RenderScaling>k__BackingField", Flags)
                ?? throw new InvalidOperationException("Платформа сменила поле масштаба; обновите стенд.");
            scaling.SetValue(platform, 1.25);
            var scalingChanged = (Action<double>?)Property(platform, "ScalingChanged").GetValue(platform)
                ?? throw new InvalidOperationException("У платформы нет обработчика смены масштаба.");
            scalingChanged(1.25);

            // WM_DPICHANGED сначала сообщает масштаб. Изменение позиции и уведомление
            // о размере могут прийти в обоих порядках, при неизменном размере в DIP.
            var shifted = anchor + new PixelVector(48, 0);
            if (before) Move(window, shifted);
            NotifyResize(window, window.ClientSize, WindowResizeReason.DpiChange);
            if (!before) Move(window, shifted);
            settle(window);

            var context = $"DPI, {(pendingMove ? "ожидающий ручной перенос" : manual ? "ручная опора" : "центр главного")}, "
                + (before ? "Move перед Resize" : "Resize перед Move");
            check(Math.Abs(window.DesktopScaling - 1.25) < 0.001, context + ": масштаб не изменился.");
            check(manual ? window.Position == anchor : CenterDistance(window, owner) <= 1,
                context + ": автоматическое перемещение испортило опору окна.");

            // Проверяем завершение защитного интервала DPI: следующий настоящий перенос
            // обязан стать новой ручной опорой для дальнейшего авторазмера.
            var moved = window.Position + new PixelVector(19, 13);
            Move(window, moved);
            settle(window);
            using var shift = new NativeResizeShift(window, before);
            NotifyResize(window, window.ClientSize.WithHeight(window.ClientSize.Height + 30),
                WindowResizeReason.Application);
            settle(window);
            check(shift.Count > 0, context + ": повторный авторазмер не изменил платформу.");
            check(window.Position == moved, context + ": защита DPI не завершилась или потеряла новый перенос.");
        }
        finally { window.Close(); }
    }

    private static Window NewWindow(Window owner, Action<Window> settle)
    {
        var window = new Window
        {
            Width = 400, Height = 220, Content = new Border(),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.Show(owner);
        settle(window);
        return window;
    }

    private static void NotifyResize(Window window, Size size, WindowResizeReason reason)
    {
        var platform = window.PlatformImpl!;
        // Headless обычно доставляет Resize через Send. Здесь моделируем уже пришедшее
        // сообщение ОС: сначала ее фактический размер, затем сохраненный callback.
        Property(platform, "ClientSize").SetValue(platform, size);
        var callback = (Action<Size, WindowResizeReason>?)Property(platform, "Resized").GetValue(platform)
            ?? throw new InvalidOperationException("У платформы нет обработчика изменения размера.");
        callback(size, reason);
    }

    private static void Move(Window window, PixelPoint position)
    {
        var platform = window.PlatformImpl!;
        var method = platform.GetType().GetMethods(Flags).SingleOrDefault(candidate =>
            (candidate.Name == "Move" || candidate.Name.EndsWith(".Move", StringComparison.Ordinal))
            && candidate.GetParameters() is [var parameter] && parameter.ParameterType == typeof(PixelPoint))
            ?? throw new InvalidOperationException("Платформа сменила метод перемещения; обновите стенд.");
        method.Invoke(platform, [position]);
    }

    private static PropertyInfo Property(object target, string name) => target.GetType().GetProperties(Flags)
        .SingleOrDefault(property => property.Name == name || property.Name.EndsWith("." + name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("Платформа сменила свойство «" + name + "»; обновите стенд.");

    private static double CenterDistance(Window first, Window second)
    {
        static Point Center(Window window)
        {
            var size = window.FrameSize ?? window.ClientSize;
            return new Point(window.Position.X + size.Width * window.DesktopScaling / 2,
                window.Position.Y + size.Height * window.DesktopScaling / 2);
        }
        var left = Center(first);
        var right = Center(second);
        return Math.Max(Math.Abs(left.X - right.X), Math.Abs(left.Y - right.Y));
    }
}
