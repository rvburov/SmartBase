using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Пароль базы данных, два режима.
/// «Установить/сменить» (<see cref="ForSet"/>): два поля, возвращает введенный пароль
/// (ShowDialog&lt;string?&gt;) — хеширует и сохраняет вызывающий (PermissionService.SetPassword).
/// «Разблокировать» (<see cref="ForUnlock"/>): одно поле, проверка через переданный
/// колбэк (PermissionService.Unlock) — он возвращает причину отказа словами, баннер показывает
/// ее как есть, окно не закрывается; успех возвращает true (ShowDialog&lt;bool&gt;).
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

        // Имя базы — в заголовке, а пояснения нет вовсе. При разблокировке окно объясняет
        // последствие шага (открыть запертую базу), и там текст на месте. Здесь объяснять нечего: пароль задают осознанно, а правило про тумблер
        // доступа живет в панели администратора, рядом с самим тумблером.
        dlg.InfoText.IsVisible = false;

        // Красная плашка про то, чего от пароля не ждут: он открывает базу и тому, кому допуск
        // отозвали, — пароль не знает о решениях администратора. Стоит здесь, у самого пароля,
        // потому что решение «ставить ли пароль вообще» принимается в этот момент.
        dlg.WarnBanner.Show(
            "Пока выбран вход по паролю, он пускает в базу любого, кто его знает, — в том числе "
            + "того, кому вы отозвали допуск.\n"
            + "Чтобы так не было: выберите способ входа «Подтверждение администратора» или "
            + "смените пароль сразу после отзыва.");

        dlg.Title = (hasPassword ? "Сменить пароль базы" : "Установить пароль базы")
                    + $" «{dbName}»";
        dlg.PasswordLabel.Text = hasPassword ? "Новый пароль" : "Пароль";
        dlg.ConfirmBtn.Content = "Сохранить";
        return dlg;
    }

    /// <summary>
    /// Разблокировка запертой базы. ShowDialog&lt;bool&gt; → true при верном пароле. Колбэк
    /// (PermissionService.Unlock) возвращает причину отказа словами: «неверный пароль» и «вход по
    /// паролю выключен администратором» лечатся по-разному, и второе, названное первым, отправило
    /// бы человека искать опечатку.
    /// </summary>
    public static PasswordDialog ForUnlock(string dbName, Func<string, string> unlock)
    {
        var dlg = new PasswordDialog(unlock);
        dlg.Title = "База данных защищена";
        dlg.InfoText.Text =
            $"База данных «{dbName}» доступна после подтверждения администратором или " +
            "по паролю. Введите пароль, чтобы открыть ее сейчас.";
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
    private void KeepWindowPosition(PixelPoint pos) =>
        SmartBase.Controls.Behaviors.WindowPlacement.KeepPositionAfterLayout(this, pos);
}
