using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;

namespace SmartBase.Tests.Support;

/// <summary>Добавляет системный перенос к настоящему уведомлению платформы об изменении
/// размера. Оба порядка событий нужны: перемещение может опередить Resized.</summary>
internal sealed class NativeResizeShift : IDisposable
{
    private readonly Window _window;
    private readonly object _platform;
    private readonly PropertyInfo _resized;
    private readonly MethodInfo _move;
    private readonly Action<Size, WindowResizeReason>? _previous;
    private readonly Action<Size, WindowResizeReason> _callback;
    private readonly bool _moveBeforeResize;
    private Size _lastSize;
    private bool _handling;
    private bool _disposed;

    public bool Enabled { get; set; } = true;
    public int Count { get; private set; }

    public NativeResizeShift(Window window, bool moveBeforeResize = false)
    {
        _window = window;
        _platform = window.PlatformImpl
            ?? throw new InvalidOperationException("У окна нет платформы для проверки изменения размера.");
        _lastSize = window.ClientSize;
        _moveBeforeResize = moveBeforeResize;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _resized = _platform.GetType().GetProperties(flags).SingleOrDefault(property =>
            (property.Name == "Resized" || property.Name.EndsWith(".Resized", StringComparison.Ordinal))
            && property.PropertyType == typeof(Action<Size, WindowResizeReason>))
            ?? throw new InvalidOperationException("Платформа не предоставляет ожидаемое уведомление Resized.");
        _move = _platform.GetType().GetMethods(flags).SingleOrDefault(method =>
            (method.Name == "Move" || method.Name.EndsWith(".Move", StringComparison.Ordinal))
            && method.GetParameters() is [var parameter] && parameter.ParameterType == typeof(PixelPoint))
            ?? throw new InvalidOperationException("Платформа не предоставляет ожидаемое перемещение окна.");

        _previous = (Action<Size, WindowResizeReason>?)_resized.GetValue(_platform);
        _callback = OnResized;
        _resized.SetValue(_platform, _callback);
    }

    private void OnResized(Size size, WindowResizeReason reason)
    {
        var changed = size != _lastSize;
        _lastSize = size;
        if (_disposed || !Enabled || !changed || _handling || !_window.IsVisible)
        {
            _previous?.Invoke(size, reason);
            return;
        }

        _handling = true;
        try
        {
            Count++;
            if (_moveBeforeResize) Move();
            _previous?.Invoke(size, reason);
            if (!_moveBeforeResize) Move();
        }
        finally { _handling = false; }
    }

    private void Move()
    {
        // Исходный обработчик может закрыть окно. Такой callback не должен переносить
        // закрытую или уже замененную платформу.
        if (_disposed || !_window.IsVisible || !ReferenceEquals(_window.PlatformImpl, _platform)) return;
        _move.Invoke(_platform, [_window.Position + new PixelVector(48, 0)]);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Не стираем чужой обработчик, если поверх проверки поставлен другой перехват.
        if (Equals(_resized.GetValue(_platform), _callback))
            _resized.SetValue(_platform, _previous);
    }
}
