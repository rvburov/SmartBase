using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Пароль базы данных, два режима.
/// «Установить/сменить» (<see cref="ForSet"/>): два поля, возвращает введённый пароль
/// (ShowDialog&lt;string?&gt;) — хеширует и сохраняет вызывающий (PermissionService.SetPassword).
/// «Разблокировать» (<see cref="ForUnlock"/>): одно поле, проверка через переданный
/// колбэк (PermissionService.TryUnlock) — неверный пароль показывает баннер, окно не
/// закрывается; успех возвращает true (ShowDialog&lt;bool&gt;).
/// </summary>
public partial class PasswordDialog : Window
{
    private readonly Func<string, bool>? _tryUnlock;

    private PasswordDialog(Func<string, bool>? tryUnlock)
    {
        InitializeComponent();
        _tryUnlock = tryUnlock;

        CancelBtn.Click  += (_, _) => Close(_tryUnlock is null ? null : (object?)false);
        ConfirmBtn.Click += (_, _) => TryConfirm();
        PasswordBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
        RepeatBox.KeyDown   += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
        PasswordBox.TextChanged += (_, _) => HideError();
        RepeatBox.TextChanged   += (_, _) => HideError();

        Opened += (_, _) => PasswordBox.Focus();
    }

    /// <summary>Установка/смена пароля базы. ShowDialog&lt;string?&gt; → пароль или null.</summary>
    public static PasswordDialog ForSet(string dbName, bool hasPassword)
    {
        var dlg = new PasswordDialog(tryUnlock: null);
        dlg.Title = hasPassword ? "Сменить пароль" : "Установить пароль";
        dlg.InfoText.Text =
            $"База данных: {dbName}. Пароль позволяет новому пользователю получить доступ " +
            "к базе без подтверждения администратором.";
        dlg.PasswordLabel.Text = hasPassword ? "Новый пароль" : "Пароль";
        dlg.ConfirmBtn.Content = "Сохранить";
        return dlg;
    }

    /// <summary>Разблокировка запертой базы. ShowDialog&lt;bool&gt; → true при верном пароле.</summary>
    public static PasswordDialog ForUnlock(string dbName, Func<string, bool> tryUnlock)
    {
        var dlg = new PasswordDialog(tryUnlock);
        dlg.Title = "База данных защищена";
        dlg.InfoText.Text =
            $"База данных «{dbName}» доступна после подтверждения администратором или " +
            "по паролю. Введите пароль, чтобы открыть её сейчас.";
        dlg.RepeatBlock.IsVisible = false;
        dlg.ConfirmBtn.Content = "Открыть";
        return dlg;
    }

    private void TryConfirm()
    {
        var password = PasswordBox.Text ?? "";
        if (password.Length == 0)
        {
            ShowError("Введите пароль");
            PasswordBox.Focus();
            return;
        }

        // Режим разблокировки: проверяем пароль на месте, окно живёт до успеха/отмены.
        if (_tryUnlock is not null)
        {
            if (_tryUnlock(password)) Close(true);
            else
            {
                ShowError("Неверный пароль");
                PasswordBox.Focus();
                PasswordBox.SelectAll();
            }
            return;
        }

        // Режим установки: пароли должны совпасть.
        if (password != (RepeatBox.Text ?? ""))
        {
            ShowError("Пароли не совпадают");
            RepeatBox.Focus();
            return;
        }
        Close(password);
    }

    private void ShowError(string message)
    {
        var pos = Position;
        ErrorBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.Hide();
        KeepWindowPosition(pos);
    }

    // SizeToContent=Height: показ/скрытие баннера меняет высоту, окно не должно «прыгать».
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
