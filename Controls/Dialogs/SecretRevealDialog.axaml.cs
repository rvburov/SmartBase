using Avalonia.Controls;
using Avalonia.Input;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Показ секрета соединения из менеджера паролей (docs/ROADMAP-API.md, шаг 16).
///
/// <b>Почему отдельное окно, а не строка в списке.</b> Раньше показанный секрет оставался в
/// раскрытой строке, пока человек не уйдет из раздела, — и экран, к которому вернулись через час,
/// ничем не отличался от записки с паролем на столе. Окно держит значение ровно столько, сколько
/// открыто: закрыли — ни на экране, ни в вызывающем контроле его больше нет, и чтобы увидеть
/// секрет снова, придется еще раз ввести мастер-пароль (и оставить еще одну строку в журнале).
///
/// Окно само ничего не проверяет и не пишет: мастер-пароль и строку журнала уже спросил и записал
/// тот, кто его открыл.
/// </summary>
public partial class SecretRevealDialog : Window
{
    private string _secret;

    /// <param name="connection">Имя соединения.</param>
    /// <param name="secret">Сам секрет — уже расшифрованный и уже записанный в журнал показ.</param>
    public SecretRevealDialog(string connection, string secret)
    {
        InitializeComponent();

        _secret = secret;

        InfoText.Text =
            $"Секрет соединения «{connection}». Показ записан в журнал безопасности. Перенесите "
            + "значение туда, где оно нужно, и закройте окно: после закрытия программа его нигде "
            + "не держит, и чтобы увидеть секрет снова, придется еще раз ввести мастер-пароль.";

        SecretText.Text = secret;

        CopyBtn.Click  += async (_, _) => await CopyAsync();
        CloseBtn.Click += (_, _) => Close();
        KeyDown        += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // Закрытое окно не должно держать значение ни в поле, ни в себе.
        Closed += (_, _) =>
        {
            SecretText.Text = "";
            _secret         = "";
        };
    }

    private async Task CopyAsync()
    {
        if (_secret.Length == 0) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(_secret);

        // Ответ на самой кнопке, как в окне мастер-хэша: отдельное окно ради слова «скопировано»
        // человек закрывает не читая.
        CopyBtn.Content = "Скопировано";
    }
}
