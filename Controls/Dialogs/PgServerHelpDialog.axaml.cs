using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно-помощник «Сервер PostgreSQL»: показывает, работает ли сервер (TCP-проба порта,
/// без пароля и прав администратора), установлен ли он на этой машине (поиск типовых
/// каталогов — см. PgServerProbe), и пошаговую инструкцию установки для текущей ОС.
/// Само приложение ничего не устанавливает: кнопка «Скачать PostgreSQL» открывает
/// официальную страницу загрузки, установку запускает пользователь (на Windows
/// установщик запросит права администратора — приложению они не нужны).
/// Открывается ссылкой «Нет сервера? Установка…» из окон подключения и создания базы.
/// Возвращает null.
/// </summary>
public partial class PgServerHelpDialog : Window
{
    public PgServerHelpDialog()
    {
        InitializeComponent();

        CheckBtn.Click    += async (_, _) => await CheckAsync();
        CloseBtn.Click    += (_, _) => Close(null);
        DownloadBtn.Click += (_, _) => OpenUrl(PgServerProbe.DownloadUrl);
        KeyDown           += (_, e) => { if (e.Key == Key.Escape) Close(null); };

        foreach (var box in new[] { HostBox, PortBox })
            box.TextChanged += (_, _) => HideStatus();

        ApplyOsGuide();

        // Первую пробу запускаем сами: окно сразу отвечает на главный вопрос «а сервер есть?».
        Opened += async (_, _) => await CheckAsync();
    }

    private async Task CheckAsync()
    {
        var host = HostBox.Text?.Trim() ?? "";
        if (host.Length == 0) { ShowStatus("Укажите адрес сервера", ok: false); return; }
        if (!int.TryParse(PortBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        { ShowStatus("Порт — число от 1 до 65535", ok: false); return; }

        SetBusy(true);
        ShowStatus("Проверяю…", ok: false);
        try
        {
            var local = IsLocalHost(host);
            var running = await PgServerProbe.ProbeTcpAsync(host, port, TimeSpan.FromSeconds(2));
            if (running)
            {
                ShowStatus($"Сервер работает — порт {port} отвечает.", ok: true);
                return;
            }
            if (!local)
            {
                ShowStatus($"Сервер {host}:{port} не отвечает — проверьте адрес, порт и сеть.", ok: false);
                return;
            }
            // Свой компьютер: различаем «установлен, но не запущен» и «не найден».
            var installed = await Task.Run(() =>
                PgServerProbe.DetectInstallation() || PgServerProbe.IsSystemdServiceActive());
            ShowStatus(installed
                ? "Сервер установлен, но не запущен — запустите его по инструкции ниже."
                : "Сервер не найден — установите его по инструкции ниже.", ok: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host is "127.0.0.1" or "::1";

    // Инструкция — только для текущей ОС: человеку не нужно выбирать свою систему из трех.
    private void ApplyOsGuide()
    {
        if (OperatingSystem.IsWindows())
        {
            GuideTitle.Text = "Как установить (Windows)";
            GuideText.Text =
                "1. Нажмите «Скачать PostgreSQL» и запустите установщик (или в PowerShell: " +
                "winget install PostgreSQL.PostgreSQL.16).\n" +
                "2. Установщик запросит права администратора (окно UAC) — сервер ставится " +
                "как служба Windows. Самой программе права администратора не нужны.\n" +
                "3. Во время установки задайте пароль пользователя postgres — он понадобится " +
                "при создании базы.\n" +
                "4. Готово: служба postgresql-x64-16 работает и стартует вместе с Windows.";
        }
        else if (OperatingSystem.IsMacOS())
        {
            GuideTitle.Text = "Как установить (macOS)";
            GuideText.Text =
                "1. Через Homebrew: brew install postgresql@16, затем " +
                "brew services start postgresql@16 (автозапуск включится сам).\n" +
                "2. Без терминала: нажмите «Скачать PostgreSQL» и установите Postgres.app — " +
                "сервер запускается как обычное приложение.\n" +
                "3. Пользователь по умолчанию: postgres (в Postgres.app — имя вашей учетной записи).";
        }
        else
        {
            GuideTitle.Text = "Как установить (Linux)";
            GuideText.Text =
                "1. Debian/Ubuntu: sudo apt install postgresql\n" +
                "2. Fedora/RHEL: sudo dnf install postgresql-server, затем " +
                "sudo postgresql-setup --initdb\n" +
                "3. Запуск и автозапуск: sudo systemctl enable --now postgresql\n" +
                "Команды требуют sudo (права администратора системы).";
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть страницу загрузки PostgreSQL");
        }
    }

    private void SetBusy(bool busy) => CheckBtn.IsEnabled = !busy;

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

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    // Держим позицию окна при пересчете высоты (SizeToContent), чтобы оно не «прыгало».
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
