using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Npgsql;
using Serilog;
using SmartBase.Database;

namespace SmartBase.Controls.Dialogs;

/// <summary>Результат диалога: имя для списка + параметры подключения РОЛИ-ВЛАДЕЛЬЦА
/// (не администратора) — именно они уходят в реестр и раздаются коллегам.</summary>
public sealed record PgCreateDatabaseResult(string Name, PgConnectionInfo Info);

/// <summary>
/// Окно «Создание базы на сервере PostgreSQL»: администратор (роль с правом
/// CREATEDB/CREATEROLE, обычно postgres) вводит параметры сервера, имя новой базы и
/// роль-владельца с паролем — окно создает роль и базу (PgDatabaseCreator), проверяет,
/// что новой ролью реально можно подключиться, и возвращает готовые параметры
/// подключения. Терминал не нужен вообще: это UI-замена ручного
/// «CREATE ROLE … + CREATE DATABASE … OWNER …» из руководства администратора.
/// Пароль роли показывается открыто — сгенерированный нужно скопировать и передать
/// коллегам. Схему внутри пустой базы приложение построит само при монтировании.
/// Возвращает <see cref="PgCreateDatabaseResult"/> или null при отмене.
/// </summary>
public partial class PgCreateDatabaseDialog : Window
{
    // Роль-владелец зеркалит имя базы, пока человек не начал править ее руками.
    private bool _roleEdited;

    public PgCreateDatabaseDialog()
    {
        InitializeComponent();

        TestBtn.Click    += async (_, _) => await TestAsync();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += async (_, _) => await ConfirmAsync();
        HelpBtn.Click    += async (_, _) => await new PgServerHelpDialog().ShowDialog(this);
        GenBtn.Click     += (_, _) => RolePasswordBox.Text = GeneratePassword();
        KeyDown          += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        foreach (var box in new[] { HostBox, PortBox, AdminBox, AdminPasswordBox,
                                    DbNameBox, RoleBox, RolePasswordBox, NameBox })
            box.TextChanged += (_, _) => { HideError(); HideStatus(); };

        DbNameBox.TextChanged += (_, _) => { if (!_roleEdited) RoleBox.Text = DbNameBox.Text; };
        // Правка самим человеком (бокс в фокусе) — зеркалирование выключается; программное
        // обновление из DbNameBox фокуса не имеет и флаг не трогает.
        RoleBox.TextChanged   += (_, _) => { if (RoleBox.IsFocused) _roleEdited = true; };

        Opened += (_, _) => HostBox.Focus();
    }

    // Собрать параметры администратора (подключение — к служебной базе postgres);
    // при ошибке валидации — баннер + null.
    private PgConnectionInfo? TryBuildAdmin()
    {
        var host = HostBox.Text?.Trim() ?? "";
        var user = AdminBox.Text?.Trim() ?? "";

        if (host.Length == 0) { ShowError("Укажите адрес сервера"); HostBox.Focus(); return null; }
        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        { ShowError("Порт — число от 1 до 65535"); PortBox.Focus(); return null; }
        if (user.Length == 0) { ShowError("Укажите администратора сервера"); AdminBox.Focus(); return null; }

        var ssl = (SslCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? PgConnectionInfo.DefaultSslMode;
        return new PgConnectionInfo(host, port, PgDatabaseCreator.MaintenanceDb,
                                    user, AdminPasswordBox.Text ?? "", ssl);
    }

    private async Task TestAsync()
    {
        if (TryBuildAdmin() is not { } admin) return;
        var version = await ProbeAsync(admin);
        if (version is not null)
            ShowStatus($"Соединение установлено ({version}).", ok: true);
    }

    private async Task ConfirmAsync()
    {
        if (TryBuildAdmin() is not { } admin) return;

        var db   = DbNameBox.Text?.Trim() ?? "";
        var role = RoleBox.Text?.Trim() ?? "";
        var pwd  = RolePasswordBox.Text ?? "";

        if (PgDatabaseCreator.ValidateName(db) is { } dbErr)
        { ShowError($"Имя базы: {dbErr}"); DbNameBox.Focus(); return; }
        if (PgDatabaseCreator.ValidateName(role) is { } roleErr)
        { ShowError($"Роль-владелец: {roleErr}"); RoleBox.Focus(); return; }
        if (pwd.Length == 0)
        { ShowError("Задайте пароль роли — по нему будут подключаться к базе"); RolePasswordBox.Focus(); return; }

        HideError();
        SetBusy(true);
        try
        {
            ShowStatus("Создаю базу…", ok: false);
            try
            {
                await Task.Run(() => PgDatabaseCreator.CreateAsync(admin, db, role, pwd));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Не удалось создать базу {Db:l} на {Host:l}:{Port}",
                    db, admin.Host, admin.Port);
                HideStatus();
                ShowError(PgErrors.Describe(ex));
                return;
            }

            // Контрольная проба РОЛЬЮ: если роль существовала раньше с другим паролем,
            // в реестр не должна попасть нерабочая связка база+логин+пароль.
            var info = new PgConnectionInfo(admin.Host, admin.Port, db, role, pwd, admin.SslMode);
            try
            {
                await Task.Run(() =>
                {
                    using var conn = new NpgsqlConnection(info.ToConnectionString());
                    conn.Open();
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "База {Db:l} создана, но проба ролью {Role:l} не прошла", db, role);
                HideStatus();
                ShowError("База создана, но подключиться ролью не удалось: "
                    + PgErrors.Describe(ex)
                    + " Обычно это значит, что роль существовала раньше с другим паролем. "
                    + "Укажите ее действующий пароль или другое имя роли; база уже на сервере — "
                    + "ее можно подключить через «Подключить сервер PostgreSQL».");
                return;
            }

            var name = NameBox.Text?.Trim() ?? "";
            if (name.Length == 0) name = db;
            Close(new PgCreateDatabaseResult(name, info));
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Короткая проба сервера администратором: соединение + SELECT version().
    private async Task<string?> ProbeAsync(PgConnectionInfo info)
    {
        HideError();
        ShowStatus("Проверяю соединение…", ok: false);
        SetBusy(true);
        try
        {
            var raw = await Task.Run(() =>
            {
                using var conn = new NpgsqlConnection(info.ToConnectionString());
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT version();";
                return (string?)cmd.ExecuteScalar();
            });
            return ShortVersion(raw);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось подключиться к PostgreSQL {Host:l}:{Port} администратором",
                info.Host, info.Port);
            HideStatus();
            ShowError(PgErrors.Describe(ex));
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // «PostgreSQL 16.3 (Debian …) on x86_64…» → «PostgreSQL 16.3».
    private static string ShortVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "PostgreSQL";
        var cut = raw.IndexOfAny(['(', ',']);
        var on  = raw.IndexOf(" on ", StringComparison.Ordinal);
        if (on >= 0 && (cut < 0 || on < cut)) cut = on;
        return (cut > 0 ? raw[..cut] : raw).Trim();
    }

    // 16 знаков из букв/цифр без похожих друг на друга (l/1, O/0): пароль передают
    // коллегам голосом и мессенджером, читаемость важна. Без спецсимволов — чтобы не
    // ломать копирование и ручной ввод.
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 16)
            .Select(_ => alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)])
            .ToArray());
    }

    private void SetBusy(bool busy)
    {
        TestBtn.IsEnabled    = !busy;
        ConfirmBtn.IsEnabled = !busy;
        GenBtn.IsEnabled     = !busy;
    }

    private void ShowStatus(string text, bool ok)
    {
        var pos = Position;
        StatusText.Text       = text;
        StatusText.Foreground = Brush(ok ? "BrushGreen" : "BrushSecondaryText",
                                      ok ? "#34C759"    : "#8E8E93");
        StatusText.IsVisible  = true;
        KeepWindowPosition(pos);
    }

    private void HideStatus()
    {
        if (!StatusText.IsVisible) return;
        var pos = Position;
        StatusText.IsVisible = false;
        KeepWindowPosition(pos);
    }

    private void ShowError(string message)
    {
        var pos = Position;            // баннер увеличит высоту — держим позицию
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

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    // Держим позицию окна при пересчете высоты (SizeToContent), чтобы оно не «прыгало».
    private void KeepWindowPosition(PixelPoint pos) =>
        SmartBase.Controls.Behaviors.WindowPlacement.KeepPositionAfterLayout(this, pos);
}
