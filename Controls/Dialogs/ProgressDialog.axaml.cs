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
        Bar.IsIndeterminate = false;
        Bar.Value        = pct;
        PercentText.Text = pct + "%";
        PhaseText.Text   = phase;
    }

    /// <summary>Финальная фаза без процентов (запись на диск/коммит большой транзакции): бегущая
    /// полоса + текст фазы — чтобы 100% не выглядели «зависшими».</summary>
    public void SetSaving(string phase)
    {
        Bar.IsIndeterminate = true;
        PercentText.Text    = "";
        PhaseText.Text      = phase;
    }

    /// <summary>Доп. строка под заголовком (напр. «Количество записей: 235000»). Пусто — скрыта.</summary>
    public void SetInfo(string text)
    {
        InfoText.Text      = text;
        InfoText.IsVisible = !string.IsNullOrEmpty(text);
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
