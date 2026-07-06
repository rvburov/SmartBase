using Avalonia.Controls;

namespace DataBase.Controls.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        CancelBtn.Click  += (_, _) => Close(false);
        ConfirmBtn.Click += (_, _) => Close(true);
    }

    public ConfirmDialog(string message, string confirmText = "Удалить", string cancelText = "Отмена")
        : this()
    {
        MessageLabel.Text  = message;
        ConfirmBtn.Content = confirmText;
        CancelBtn.Content  = cancelText;
    }

    /// <summary>Информационное окно с ОДНОЙ кнопкой (без «Отмена») — для уведомлений,
    /// которые нужно просто подтвердить прочтение.</summary>
    public static ConfirmDialog Info(string message, string okText = "Понятно")
    {
        var d = new ConfirmDialog(message, okText);
        d.CancelBtn.IsVisible = false;
        return d;
    }
}
