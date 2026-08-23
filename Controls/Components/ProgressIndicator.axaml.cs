using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SmartBase.Controls.Components;

/// <summary>
/// Единый индикатор загрузки приложения: строка фазы + доп. строка + полоса с процентами.
/// Один контрол на все места с прогрессом (окно импорта, сплэш старта, установщик, окно
/// обновления, окна расширений) — вид и поведение меняются в одном месте. Режимы:
///  • <see cref="SetProgress"/> — определенный прогресс (доля 0..1 → полоса + N%);
///  • <see cref="SetIndeterminate"/> — бегущая полоса без процентов (фаза без известного объема,
///    например коммит большой транзакции), чтобы 100% не выглядели «зависшими»;
///  • <see cref="StartSmooth"/>/<see cref="FinishSmooth"/> — плавная самодвижущаяся полоса
///    (замедляющееся приближение к 97% по таймеру, затем быстрый добег до 100% и колбэк) —
///    для работ без общего процента (монтирование БД на старте): полоса никогда не застывает.
/// Все методы вызываются на UI-потоке (из Progress&lt;T&gt; или напрямую).
/// </summary>
public partial class ProgressIndicator : UserControl
{
    private DispatcherTimer? _ticker;   // создается только для smooth-режима
    private double  _value;             // показанное значение 0..1 (smooth)
    private double  _cap = 0.97;        // асимптота текущей ступени «докручивания»
    private bool    _finishing;         // режим быстрого добега до 100%
    private Action? _onFinished;        // вызвать по достижении 100% (smooth)

    public ProgressIndicator() => InitializeComponent();

    /// <summary>Определенный прогресс: доля 0..1 + необязательный текст фазы.</summary>
    public void SetProgress(double fraction, string? phase = null)
    {
        StopSmooth();
        if (fraction < 0) fraction = 0;
        if (fraction > 1) fraction = 1;
        Bar.IsIndeterminate = false;
        Bar.Value           = fraction;
        PercentText.Text    = $"{(int)Math.Round(fraction * 100)}%";
        if (phase is not null) SetPhase(phase);
    }

    /// <summary>Бегущая полоса без процентов — для фаз с неизвестным объемом работы.</summary>
    public void SetIndeterminate(string? phase = null)
    {
        StopSmooth();
        Bar.IsIndeterminate = true;
        PercentText.Text    = "";
        if (phase is not null) SetPhase(phase);
    }

    /// <summary>Запустить плавную самодвижущуюся полосу (0 → cap, никогда не застывает).</summary>
    public void StartSmooth(string? phase = null, double cap = 0.97)
    {
        if (phase is not null) SetPhase(phase);
        Bar.IsIndeterminate = false;
        _cap   = Math.Clamp(cap, 0.01, 0.99);
        _value = 0; _finishing = false; _onFinished = null;
        Ticker().Start();
    }

    /// <summary>Ступень smooth-режима: поднять асимптоту до <paramref name="cap"/> и сменить фазу —
    /// каждому этапу работы отводится свой сегмент шкалы (проценты отражают, докуда дошли).</summary>
    public void SetStage(double cap, string phase)
    {
        SetPhase(phase);
        _cap = Math.Clamp(cap, _cap, 0.99);   // ступени только вперед
        if (!_finishing) Ticker().Start();
    }

    /// <summary>Сменить текст фазы (в любом режиме; smooth-полосу не трогает).</summary>
    public void SetStatus(string phase) => SetPhase(phase);

    /// <summary>Smooth-работа завершена: быстро добежать до 100% и вызвать <paramref name="onDone"/>.
    /// Работает и без предварительного StartSmooth (мгновенная работа) — просто добежит с текущего значения.</summary>
    public void FinishSmooth(string phase, Action onDone)
    {
        SetPhase(phase);
        Bar.IsIndeterminate = false;
        _onFinished = onDone;
        _finishing  = true;                    // тики доведут до 100% и вызовут колбэк
        Ticker().Start();
    }

    /// <summary>Ошибка: остановить движение, показать сообщение (проценты скрываются).</summary>
    public void SetError(string message)
    {
        StopSmooth();
        Bar.IsIndeterminate = false;
        PercentText.Text    = "";
        SetPhase(message);
    }

    /// <summary>Доп. строка под фазой (например «Количество записей: 235000»). Пусто — скрыта.</summary>
    public void SetInfo(string text)
    {
        InfoText.Text      = text;
        InfoText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetPhase(string text)
    {
        PhaseText.Text      = text;
        PhaseText.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void Tick()
    {
        // Обычный ход: замедляющееся приближение к текущей ступени _cap — непрерывно, без остановки.
        // Финал: быстрый добег к 1.0, затем колбэк (показ следующего окна) — чтобы 100% был виден.
        _value += _finishing ? (1.0 - _value) * 0.5 : (_cap - _value) * 0.004;
        if (_finishing && _value >= 0.995)
        {
            _value = 1.0;
            Bar.Value        = 1.0;
            PercentText.Text = "100%";
            _ticker!.Stop();
            var done = _onFinished; _onFinished = null;
            // Колбэк — отложенно (после отрисовки кадра): тяжелое продолжение (например,
            // построение главного окна) блокирует UI-поток, и без отложки «100%» не успевал
            // отрисоваться — полоса визуально застревала на 99% на все время построения.
            if (done is not null)
                Dispatcher.UIThread.Post(done, DispatcherPriority.Background);
            return;
        }
        Bar.Value        = _value;
        PercentText.Text = $"{(int)Math.Round(_value * 100)}%";
    }

    // Таймер smooth-режима: создается лениво, подписка на Tick — ровно одна.
    private DispatcherTimer Ticker()
    {
        if (_ticker is null)
        {
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _ticker.Tick += (_, _) => Tick();
        }
        return _ticker;
    }

    private void StopSmooth()
    {
        _ticker?.Stop();
        _finishing  = false;
        _onFinished = null;
    }
}
