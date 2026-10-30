using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Npgsql;
using Serilog;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Utils;
using SmartBase.Views.Settings;

namespace SmartBase.Controls.Dialogs;

/// <summary>Что изменить после закрытия: null-поле — «не менять».</summary>
public sealed record DbSettingsResult(string? NewName, string? NewPgPassword);

/// <summary>
/// Окно «Настройки базы данных» (шестеренка в строке базы): общее — переименование в
/// списке; у SQLite — сведения о файле и «Сжать файл» (переехал сюда из строки списка);
/// у PostgreSQL — живой статус сервера (TCP-проба) со ссылкой на помощник
/// установки/автозапуска, «Данные подключения» и смена сохраненного пароля (с пробой
/// нового пароля до записи — нерабочий в реестр не попадает). Резервная копия живет в
/// разделе «Настройки → Резервное копирование».
/// Сжатие выполняется сразу через колбэк (окно закрывается — ход операции виден в
/// списке баз, как раньше); имя и пароль возвращаются результатом по «Сохранить»,
/// запись в реестр делает вызывающий (SettingsView). Возвращает
/// <see cref="DbSettingsResult"/> или null.
/// </summary>
public partial class DbSettingsDialog : Window
{
    private readonly DatabaseRefEntity _entry;
    private readonly Action? _compact;

    public DbSettingsDialog(DatabaseRefEntity entry, DatabaseManager.StorageStats? storage,
                            bool canMaintain, Action? onCompact)
    {
        InitializeComponent();
        _entry     = entry;
        _compact   = onCompact;

        // Движок назван в заголовке: окно у файловой и серверной базы разное внутри, и
        // человек должен видеть, о какой из них речь, не вчитываясь в содержимое.
        Title = $"Настройки базы данных ({DatabaseRow.EngineName(entry.IsPostgres)})";

        NameBox.Text  = entry.Name;
        PathText.Text = entry.Path;

        SqliteSection.IsVisible = !entry.IsPostgres;
        PgSection.IsVisible     = entry.IsPostgres;

        if (entry.IsPostgres)
        {
            HelpBtn.Click += async (_, _) => await new PgServerHelpDialog(
                entry.PgHost, entry.PgPort ?? PgConnectionInfo.DefaultPort).ShowDialog(this);
            DetailsBtn.Click += async (_, _) => await new PgDetailsDialog(
                entry.PgHost ?? "", entry.PgPort ?? PgConnectionInfo.DefaultPort,
                entry.PgDatabase ?? "", entry.PgUser ?? "",
                entry.PgSslMode ?? PgConnectionInfo.DefaultSslMode).ShowDialog(this);
            Opened += async (_, _) => await ProbeServerAsync();
        }
        else
        {
            // Размер файла — снимок, снятый при монтировании; та же строка, что в списке баз.
            if (storage is not null)
            {
                // Без слов «размер файла»: они уже стоят заголовком над этой строкой.
                StorageText.Text = DatabaseRow.StorageValue(storage);
                StorageText.Foreground = Brush(
                    storage.NeedsCompaction ? "BrushRed" : "BrushSecondaryText",
                    storage.NeedsCompaction ? "#FF3B30" : "#8E8E93");
            }
            else
            {
                StorageText.Text = "База не подключена — сведения о файле недоступны.";
                StorageText.Foreground = Brush("BrushSecondaryText", "#8E8E93");
            }
            // Кнопка видна всегда, серая — когда сжать нельзя, и причина написана под ней.
            // Прежнее «видна, только когда есть что возвращать» оставляло пользователя
            // искать глазами кнопку, которой на экране не было.
            CompactBtn.IsEnabled = canMaintain;
            if (!canMaintain)
            {
                CompactHint.Text = storage is null
                    ? "Сжать файл можно, когда база подключена."
                    : "База временно недоступна для сжатия.";
                CompactHint.IsVisible = true;
            }
            else if (entry.IsDefault)
            {
                // Основная база подключена все время работы программы — отцепить ее на время
                // сжатия нельзя, поэтому сжатие откладывается на следующий запуск.
                CompactHint.Text = "Основная база подключена все время работы программы, поэтому ее файл " +
                                   "сжимается при следующем запуске — ход сжатия будет виден на заставке.";
                CompactHint.IsVisible = true;
            }

            CompactBtn.Click += (_, _) => { _compact?.Invoke(); Close(null); };
        }

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += async (_, _) => await SaveAsync();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
        NameBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SaveAsync(); };

        foreach (var box in new[] { NameBox, PgPasswordBox })
            box.TextChanged += (_, _) => { HideError(); HideStatus(); };

        Opened += (_, _) => NameBox.Focus();
    }

    // Живой статус сервера PG: та же TCP-проба, что у помощника — без пароля и прав.
    private async Task ProbeServerAsync()
    {
        var host = _entry.PgHost ?? "";
        var port = _entry.PgPort ?? PgConnectionInfo.DefaultPort;
        ServerStatusText.Text       = "Проверяю сервер…";
        ServerStatusText.Foreground = Brush("BrushSecondaryText", "#8E8E93");

        var running = await PgServerProbe.ProbeTcpAsync(host, port, TimeSpan.FromSeconds(2));
        ServerStatusText.Text = running
            ? $"Сервер работает — порт {port} отвечает."
            : $"Сервер {host}:{port} не отвечает — запустите его или проверьте адрес.";
        ServerStatusText.Foreground = Brush(
            running ? "BrushGreen" : "BrushRed",
            running ? "#34C759"    : "#FF3B30");
    }

    private async Task SaveAsync()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            ShowError("Укажите название базы данных");
            NameBox.Focus();
            return;
        }
        var newName = name == _entry.Name ? null : name;

        string? newPwd = null;
        if (_entry.IsPostgres && PgPasswordBox.Text is { Length: > 0 } pwd)
        {
            // Проба новым паролем ДО записи: роль с другим паролем не пройдет, и в
            // реестре останется прежний секрет.
            if (_entry.ToPgInfo() is not { } info)
            {
                ShowError("Запись подключения повреждена — переподключите базу заново.");
                return;
            }
            var probe = info with { Password = pwd };
            SetBusy(true);
            ShowStatus("Проверяю новый пароль…");
            try
            {
                await Task.Run(() =>
                {
                    using var conn = new NpgsqlConnection(probe.ToConnectionString());
                    conn.Open();
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Новый пароль PG не подошел: {Host:l}:{Port}/{Db:l}",
                    info.Host, info.Port, info.Database);
                HideStatus();
                ShowError(PgErrors.Describe(ex));
                return;
            }
            finally
            {
                SetBusy(false);
                HideStatus();
            }
            newPwd = pwd;
        }

        if (newName is null && newPwd is null) { Close(null); return; }
        Close(new DbSettingsResult(newName, newPwd));
    }

    private void SetBusy(bool busy) => SaveBtn.IsEnabled = !busy;

    private void ShowStatus(string text)
    {
        var pos = Position;
        StatusText.Text       = text;
        StatusText.Foreground = Brush("BrushSecondaryText", "#8E8E93");
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
