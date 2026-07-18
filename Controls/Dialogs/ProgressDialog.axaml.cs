using Avalonia.Controls;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно загрузки с процентами (импорт, массовые операции, загрузка обновления).
/// Начинка — единый <see cref="Components.ProgressIndicator"/>; здесь только окно и
/// запрет ручного закрытия: показывается на время работы, закрывается программно (<see cref="Done"/>).
/// </summary>
public partial class ProgressDialog : Window
{
    private bool    _allowClose;
    private bool    _cancelling;
    private Action? _onCancel;

    public ProgressDialog()
    {
        InitializeComponent();
        CancelBtn.Click += (_, _) =>
        {
            if (_cancelling) return;
            _cancelling = true;
            CancelBtn.IsEnabled = false;
            Indicator.SetIndeterminate("Отмена…");
            var cb = _onCancel;
            _onCancel = null;
            cb?.Invoke();
        };
    }

    /// <summary>
    /// Включить отмену (opt-in; по умолчанию окна прогресса без неё — существующие операции не
    /// меняются). По клику кнопка гаснет, полоса переходит в «Отмена…», колбэк вызывается один
    /// раз (обычно CancellationTokenSource.Cancel). Окно, как и прежде, закрывает только Done() —
    /// вызывающий делает это после отката/очистки.
    /// </summary>
    public void EnableCancel(Action onCancel)
    {
        _onCancel = onCancel;
        CancelBtn.IsVisible = true;
    }

    /// <summary>Обновить прогресс. fraction — 0..1. Вызывается на UI-потоке (через Progress&lt;T&gt;).
    /// После нажатия «Отмена» игнорируется (полоса остаётся в «Отмена…»).</summary>
    public void SetProgress(double fraction, string phase)
    {
        if (_cancelling) return;
        Indicator.SetProgress(fraction, phase);
    }

    /// <summary>Финальная фаза без процентов (запись на диск/коммит большой транзакции): бегущая
    /// полоса + текст фазы — чтобы 100% не выглядели «зависшими».</summary>
    public void SetSaving(string phase)
    {
        if (_cancelling) return;
        Indicator.SetIndeterminate(phase);
    }

    /// <summary>Доп. строка под заголовком (напр. «Количество записей: 235000»). Пусто — скрыта.</summary>
    public void SetInfo(string text)
    {
        if (_cancelling) return;
        Indicator.SetInfo(text);
    }

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
