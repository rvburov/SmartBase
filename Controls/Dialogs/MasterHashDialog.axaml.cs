using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Подсчет проверочного хэша мастер-пароля для политики организации
/// (docs/ROADMAP-API.md, 10.8).
///
/// <b>Зачем окно вообще есть.</b> Исходящее направление подтверждается мастер-паролем, а
/// проверочный хэш этого пароля лежит в политике — файле, который составляет ответственный за
/// интеграции. Формат хэша свой (PBKDF2 с солью, <see cref="MasterPassword"/>), и посчитать его
/// было НЕЧЕМ: программа умела только проверять. Без строки <c>masterHash</c> в политике
/// подтвердить направление нельзя ни одним способом — то есть весь исходящий обмен упирался в
/// число, которое программа считает, но не дает получить.
///
/// <b>Почему это не «установка пароля».</b> Окно ничего не сохраняет и не меняет: оно считает
/// строку и отдает ее человеку. Пароль отсюда не уходит никуда — ни в базу, ни в настройки, ни в
/// журнал. Политику правит администратор вне программы, и это остается верным: программа лишь
/// перестала требовать от него воспроизводить свой формат хэша сторонним скриптом.
///
/// <b>Почему пароль спрашивается дважды.</b> Опечатку здесь нечем поймать потом: хэш посчитается
/// и от опечатки, ляжет в политику, и обнаружится это в тот день, когда пароль перестанет
/// подходить, — а подходить он не будет ни у кого.
/// </summary>
public partial class MasterHashDialog : Window
{
    private string _hash = "";

    public MasterHashDialog()
    {
        InitializeComponent();

        ComputeBtn.Click += (_, _) => Compute();
        CopyBtn.Click    += async (_, _) => await CopyAsync();
        CloseBtn.Click   += (_, _) => Close();

        PasswordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Compute(); };
        RepeatBox.KeyDown   += (_, e) => { if (e.Key == Key.Enter) Compute(); };

        // Правка пароля убирает и ошибку, и прежний ответ: строка, посчитанная от другого
        // пароля, — самое опасное, что здесь может остаться на экране.
        PasswordBox.TextChanged += (_, _) => Reset();
        RepeatBox.TextChanged   += (_, _) => Reset();

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opened  += (_, _) => PasswordBox.Focus();
    }

    private void Compute()
    {
        var password = PasswordBox.Text ?? "";

        if (password.Length == 0)
        {
            ShowError("Введите пароль");
            PasswordBox.Focus();
            return;
        }

        if (password != (RepeatBox.Text ?? ""))
        {
            ShowError("Пароли не совпадают");
            RepeatBox.Focus();
            return;
        }

        _hash = MasterPassword.Hash(password);

        var pos = Position;
        HashText.Text        = _hash;
        ResultBlock.IsVisible = true;
        CopyBtn.IsVisible     = true;
        CopyBtn.Content       = "Скопировать";
        KeepWindowPosition(pos);
    }

    private async Task CopyAsync()
    {
        if (_hash.Length == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(_hash);

        // Ответ на самой кнопке, как в окне описания API: отдельное окно ради слова
        // «скопировано» человек закрывает не читая.
        CopyBtn.Content = "Скопировано";
    }

    /// <summary>Убрать прежний ответ: пароль изменился, и посчитанная строка к нему не относится.</summary>
    private void Reset()
    {
        HideError();

        if (!ResultBlock.IsVisible) return;

        var pos = Position;
        _hash                 = "";
        HashText.Text         = "";
        ResultBlock.IsVisible = false;
        CopyBtn.IsVisible     = false;
        KeepWindowPosition(pos);
    }

    private void ShowError(string message)
    {
        var pos = Position;
        NoticeBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!NoticeBanner.IsVisible) return;
        var pos = Position;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }

    // SizeToContent=Height: появление баннера и блока ответа меняет высоту, окно не должно
    // «прыгать» — то же, что в PasswordDialog.
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }
}
