using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Пароль базы данных, два режима.
/// «Установить/сменить» (<see cref="ForSet"/>): два поля, возвращает введенный пароль
/// (ShowDialog&lt;string?&gt;) — хеширует и сохраняет вызывающий (PermissionService.SetPassword).
/// «Разблокировать» (<see cref="ForUnlock"/>): одно поле, проверка через переданный
/// колбэк (PermissionService.TryUnlock) — неверный пароль показывает баннер, окно не
/// закрывается; успех возвращает true (ShowDialog&lt;bool&gt;).
/// «Мастер-пароль» (<see cref="ForMaster"/>): то же одно поле, но колбэк возвращает ПРИЧИНУ
/// отказа словами, а не «да/нет». Причин тут несколько и они разные — пароль не подошел, пароля
/// нет ни в одном источнике политики, след не записался в журнал, — и «Неверный пароль» вместо
/// любой из них отправил бы человека искать опечатку там, где ее нет.
/// </summary>
public partial class PasswordDialog : Window
{
    /// <summary>Проверка пароля: пусто в ответе — подошел, иначе причина отказа для баннера.</summary>
    private readonly Func<string, string>? _tryUnlock;

    private PasswordDialog(Func<string, string>? tryUnlock)
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
        var dlg = new PasswordDialog(password => tryUnlock(password) ? "" : "Неверный пароль");
        dlg.Title = "База данных защищена";
        dlg.InfoText.Text =
            $"База данных «{dbName}» доступна после подтверждения администратором или " +
            "по паролю. Введите пароль, чтобы открыть ее сейчас.";
        dlg.RepeatBlock.IsVisible = false;
        dlg.ConfirmBtn.Content = "Открыть";
        return dlg;
    }

    /// <summary>
    /// Мастер-пароль политики: понижение метки конфиденциальности (docs/ROADMAP-API.md, 10.6).
    /// Окно спрашивает пароль, а делает понижение сам вызывающий — колбэк возвращает причину
    /// отказа либо пусто. Так проверка и действие остаются в одном месте (репозиторий), а окно
    /// не знает про метки ничего: неверный пароль, отсутствие пароля в политике и незаписавшийся
    /// след отвечают разными словами, и все они приезжают готовой фразой.
    ///
    /// ShowDialog&lt;bool&gt; → true, когда понижение выполнено.
    /// </summary>
    /// <param name="what">О чем речь: «базы «Заказы»», «свойства «Сумма»».</param>
    /// <param name="from">Метка до.</param>
    /// <param name="to">Метка после.</param>
    /// <param name="lower">Понизить метку этим паролем; возвращает причину отказа или пусто.</param>
    public static PasswordDialog ForMaster(string what, string from, string to,
                                           Func<string, string> lower)
    {
        var dlg = new PasswordDialog(lower);
        dlg.Title = "Понижение метки конфиденциальности";
        dlg.InfoText.Text =
            $"Метка {what} понижается: «{from}» → «{to}». Понижение разрешает ответственный за "
            + "интеграции — введите мастер-пароль политики организации. Событие попадет в журнал "
            + "безопасности.";
        dlg.PasswordLabel.Text    = "Мастер-пароль политики";
        dlg.RepeatBlock.IsVisible = false;
        dlg.ConfirmBtn.Content    = "Понизить";
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

        // Режим разблокировки: проверяем пароль на месте, окно живет до успеха/отмены.
        if (_tryUnlock is not null)
        {
            var trouble = _tryUnlock(password);

            if (trouble.Length == 0) Close(true);
            else
            {
                ShowError(trouble);
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
