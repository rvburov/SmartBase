using Avalonia.Controls;

namespace DataBase.Controls.Dialogs;

/// <summary>
/// Окно загрузки с процентами. Показывается на время импорта, закрывается только
/// программно (<see cref="Done"/>) — вручную пользователь его не закрывает.
/// </summary>
public partial class ProgressDialog : Window
{
    private bool _allowClose;

    public ProgressDialog() => InitializeComponent();

    /// <summary>Обновить прогресс. fraction — 0..1. Вызывается на UI-потоке (через Progress&lt;T&gt;).</summary>
    public void SetProgress(double fraction, string phase)
    {
        var pct = Math.Clamp((int)Math.Round(fraction * 100), 0, 100);
        Bar.Value        = pct;
        PercentText.Text = pct + "%";
        PhaseText.Text   = phase;
    }

    /// <summary>Закрыть окно загрузки (после завершения или ошибки импорта).</summary>
    public void Done()
    {
        _allowClose = true;
        Close();
    }

    // Пока идёт импорт, окно нельзя закрыть вручную (крестиком/Alt+F4).
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
