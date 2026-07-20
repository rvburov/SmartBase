using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Npgsql;
using Serilog;
using SmartBase.Database;

namespace SmartBase.Controls.Dialogs;

/// <summary>Результат диалога: имя для списка + параметры подключения.</summary>
public sealed record PgConnectResult(string Name, PgConnectionInfo Info);

/// <summary>
/// Окно «Подключение к PostgreSQL»: сервер/порт/база/пользователь/пароль/SSL, проверка
/// соединения (короткая проба с таймаутом 5 с, см. PgConnectionInfo) и русские тексты
/// типовых ошибок сервера. Кнопка «Подключить» прогоняет ту же пробу — в реестр не
/// попадают заведомо нерабочие параметры. Базу на сервере приложение НЕ создаёт
/// (создаёт администратор командой CREATE DATABASE); схему внутри пустой базы
/// приложение построит само при монтировании. Возвращает <see cref="PgConnectResult"/>
/// или null при отмене.
/// </summary>
public partial class PgConnectDialog : Window
{
    public PgConnectDialog()
    {
        InitializeComponent();

        TestBtn.Click    += async (_, _) => await TestAsync();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += async (_, _) => await ConfirmAsync();
        KeyDown          += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        foreach (var box in new[] { HostBox, PortBox, DatabaseBox, UserBox, PasswordBox, NameBox })
            box.TextChanged += (_, _) => { HideError(); HideStatus(); };

        Opened += (_, _) => HostBox.Focus();
    }

    // Собрать параметры из полей; при ошибке валидации — баннер + null.
    private PgConnectionInfo? TryBuildInfo()
    {
        var host = HostBox.Text?.Trim() ?? "";
        var db   = DatabaseBox.Text?.Trim() ?? "";
        var user = UserBox.Text?.Trim() ?? "";

        if (host.Length == 0) { ShowError("Укажите адрес сервера"); HostBox.Focus(); return null; }
        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        { ShowError("Порт — число от 1 до 65535"); PortBox.Focus(); return null; }
        if (db.Length == 0) { ShowError("Укажите имя базы данных"); DatabaseBox.Focus(); return null; }
        if (user.Length == 0) { ShowError("Укажите имя пользователя"); UserBox.Focus(); return null; }

        var ssl = (SslCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? PgConnectionInfo.DefaultSslMode;
        return new PgConnectionInfo(host, port, db, user, PasswordBox.Text ?? "", ssl);
    }

    private async Task TestAsync()
    {
        if (TryBuildInfo() is not { } info) return;
        var version = await ProbeAsync(info);
        if (version is not null)
            ShowStatus($"Соединение установлено ({version}).", ok: true);
    }

    private async Task ConfirmAsync()
    {
        if (TryBuildInfo() is not { } info) return;
        // Та же проба, что и «Проверить соединение»: не подключаем заведомо нерабочее.
        if (await ProbeAsync(info) is null) return;

        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) name = info.Database;
        Close(new PgConnectResult(name, info));
    }

    // Короткая проба сервера в фоне: открыть соединение + SELECT version().
    // Возвращает строку версии («PostgreSQL 16.3») или null (ошибка показана баннером).
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
            Log.Warning(ex, "Не удалось подключиться к PostgreSQL {Host:l}:{Port}/{Db:l}",
                info.Host, info.Port, info.Database);
            HideStatus();
            ShowError(DescribeError(ex));
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

    // Русские тексты типовых ошибок подключения; незнакомое — как есть.
    private static string DescribeError(Exception ex)
    {
        if (ex is PostgresException pe)
            return pe.SqlState switch
            {
                "28P01" => "Неверный пароль или пользователь.",
                "28000" => "Сервер отклонил подключение — проверьте настройки доступа (pg_hba.conf).",
                "3D000" => "База данных не найдена на сервере. Создайте её (CREATE DATABASE) и повторите.",
                _       => $"Ошибка сервера: {pe.MessageText}",
            };
        if (ex is System.Security.Authentication.AuthenticationException
            || ex.InnerException is System.Security.Authentication.AuthenticationException)
            return "Не удалось установить защищённое соединение — попробуйте другой режим SSL.";
        if (ex is TimeoutException or System.Net.Sockets.SocketException
            || ex.InnerException is TimeoutException or System.Net.Sockets.SocketException)
            return "Сервер недоступен — проверьте адрес, порт и сеть.";
        return ex.Message;
    }

    private void SetBusy(bool busy)
    {
        TestBtn.IsEnabled    = !busy;
        ConfirmBtn.IsEnabled = !busy;
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

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    // Держим позицию окна при пересчёте высоты (SizeToContent), чтобы оно не «прыгало».
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
