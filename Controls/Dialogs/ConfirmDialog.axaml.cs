using Avalonia.Controls;

namespace MyPlanner.Controls.Dialogs;

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
}
