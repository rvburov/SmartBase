using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DataBase.Views;

/// <summary>
/// Стартовый сплэш-экран (в стиле установщика: название + версия). Показывается сразу, пока в
/// фоне монтируются базы данных. Полоса прогресса идёт ПЛАВНО и непрерывно: значение
/// «докручивается» по таймеру к асимптоте (никогда не застывает), а в статусе видно текущий этап
/// (какая база открывается). По завершении — быстрый добег до 100% и запуск главного окна.
///
/// Почему анимация корректна: тяжёлая работа (миграции/бэкафилл) идёт в ФОНОВОМ потоке, поэтому
/// UI-поток свободен и таймер плавно двигает полосу. Реальный процент EF-миграции недоступен,
/// поэтому показываем плавное движение, а не рывки 0→60→100.
/// </summary>
public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _ticker;
    private double _value;                 // показанное значение 0..1
    private bool   _finishing;             // режим быстрого добега до 100%
    private Action? _onFinished;           // вызвать, когда добежали до 100% (запуск главного окна)
    private const double Cap = 0.97;       // асимптота обычного «докручивания»

    public SplashWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"Версия {v.Major}.{v.Minor}.{v.Build}";

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    private void Tick()
    {
        // Обычный ход: замедляющееся приближение к Cap (τ≈12 с) — непрерывно, без остановки.
        // Финал: быстрый добег к 1.0, затем колбэк (запуск главного окна) — чтобы 100% было видно.
        _value += _finishing ? (1.0 - _value) * 0.5 : (Cap - _value) * 0.004;
        if (_finishing && _value >= 0.995)
        {
            _value = 1.0;
            Progress.Value = 1.0;
            PercentText.Text = "100%";
            _ticker.Stop();
            var done = _onFinished; _onFinished = null;
            done?.Invoke();
            return;
        }
        Progress.Value   = _value;
        PercentText.Text = $"{(int)Math.Round(_value * 100)}%";
    }

    /// <summary>Текущий этап запуска (какая база открывается/обновляется). Полосу двигает таймер.</summary>
    public void SetStatus(string message) => StatusText.Text = message;

    /// <summary>Работа завершена: плавно добежать до 100% и затем вызвать <paramref name="onDone"/>
    /// (там показывается главное окно и закрывается сплэш).</summary>
    public void Finish(string message, Action onDone)
    {
        StatusText.Text = message;
        _onFinished = onDone;
        _finishing  = true;   // следующие тики быстро доведут до 100% и вызовут onDone
    }

    /// <summary>Показать ошибку запуска вместо прогресса (процент скрываем).</summary>
    public void SetError(string message)
    {
        _ticker.Stop();
        StatusText.Text  = message;
        PercentText.Text = "";
    }
}
