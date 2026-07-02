using Avalonia.Controls;

namespace DataBase.Controls.Dialogs;

/// <summary>
/// Окно загрузки с процентами (импорт, массовые операции, загрузка обновления).
/// Начинка — единый <see cref="Components.ProgressIndicator"/>; здесь только окно и
/// запрет ручного закрытия: показывается на время работы, закрывается программно (<see cref="Done"/>).
/// </summary>
public partial class ProgressDialog : Window
{
    private bool _allowClose;

    public ProgressDialog() => InitializeComponent();

    /// <summary>Обновить прогресс. fraction — 0..1. Вызывается на UI-потоке (через Progress&lt;T&gt;).</summary>
    public void SetProgress(double fraction, string phase) => Indicator.SetProgress(fraction, phase);

    /// <summary>Финальная фаза без процентов (запись на диск/коммит большой транзакции): бегущая
    /// полоса + текст фазы — чтобы 100% не выглядели «зависшими».</summary>
    public void SetSaving(string phase) => Indicator.SetIndeterminate(phase);

    /// <summary>Доп. строка под заголовком (напр. «Количество записей: 235000»). Пусто — скрыта.</summary>
    public void SetInfo(string text) => Indicator.SetInfo(text);

    /// <summary>Закрыть окно загрузки (после завершения или ошибки операции).</summary>
    public void Done()
    {
        _allowClose = true;
        Close();
    }

    // Пока идёт работа, окно нельзя закрыть вручную (крестиком/Alt+F4).
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose) e.Cancel = true;
        base.OnClosing(e);
    }
}
