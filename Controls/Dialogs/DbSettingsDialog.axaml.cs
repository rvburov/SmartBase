using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Npgsql;
using Serilog;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
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

    /// <summary>Правка названия и пароля разрешена (администратор базы либо база не подключена).</summary>
    private readonly bool _canManage;

    /// <summary>Репозиторий подключенной базы; <c>null</c> — база не смонтирована.</summary>
    private readonly ConstructorRepository? _repo;

    /// <summary>Метка, стоящая сейчас. Нужна, чтобы вернуть список на место после отказа.</summary>
    private ConfidentialityLabel _label;

    /// <summary>Идет установка метки — событие списка не должно уйти на второй круг.</summary>
    private bool _labelBusy;

    /// <param name="canManage">Правка названия и пароля: см. <see cref="DatabaseRow.CanManage"/>.</param>
    public DbSettingsDialog(DatabaseRefEntity entry, DatabaseManager.StorageStats? storage,
                            bool canMaintain, bool canManage, Action? onCompact,
                            ConstructorRepository? repo = null)
    {
        InitializeComponent();
        _entry     = entry;
        _compact   = onCompact;
        _repo      = repo;
        _canManage = canManage;

        // Движок назван в заголовке: окно у файловой и серверной базы разное внутри, и
        // человек должен видеть, о какой из них речь, не вчитываясь в содержимое.
        Title = $"Настройки базы данных ({DatabaseRow.EngineName(entry.IsPostgres)})";

        NameBox.Text  = entry.Name;
        PathText.Text = entry.Path;

        // Название в списке и пароль подключения — дело администратора базы. Остальным поля
        // показываются серыми, а не прячутся: спрятанное поле рождает вопрос «а где это
        // меняется», серое отвечает на него сразу.
        NameBox.IsEnabled = canManage;
        if (!canManage)
        {
            NameLockHint.Text      = "Название в списке меняет администратор базы.";
            NameLockHint.IsVisible = true;
        }

        SqliteSection.IsVisible = !entry.IsPostgres;
        PgSection.IsVisible     = entry.IsPostgres;

        if (entry.IsPostgres)
        {
            PgPasswordBox.IsEnabled = canManage;
            if (!canManage)
                PgPasswordHint.Text = "Пароль подключения меняет администратор базы.";

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
            // Прежнее «видна, только когда есть что возвращать» оставляло администратора
            // искать глазами кнопку, которой на экране не было.
            CompactBtn.IsEnabled = canMaintain;
            if (!canMaintain)
            {
                CompactHint.Text = storage is null
                    ? "Сжать файл можно, когда база подключена."
                    : "Сжимать файл может администратор базы.";
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

        SetupLabel();

        Opened += (_, _) => NameBox.Focus();
    }

    // ── Конфиденциальность ───────────────────────────────────────────────────

    /// <summary>
    /// Метка базы (docs/ROADMAP-API.md, 10.6). Показывается ТОЛЬКО у подключенной базы, и это не
    /// придирка: метка лежит в <c>db_meta</c>, то есть внутри самого файла — именно затем, чтобы
    /// уезжать вместе с ним. Файл не открыт — читать и писать нечего.
    ///
    /// Метка применяется СРАЗУ, а не по «Сохранить» вместе с названием: понижение спрашивает
    /// мастер-пароль и пишет в журнал безопасности, и складывать такое действие в общую кнопку
    /// значило бы прятать его среди переименований.
    /// </summary>
    private void SetupLabel()
    {
        if (_repo is null) return;

        _label = _repo.GetDatabaseLabel();

        LabelSection.IsVisible = true;
        LabelCombo.ItemsSource = Labels.Select(Confidentiality.Title).ToList();
        LabelCombo.SelectedIndex = Array.IndexOf(Labels, _label);
        ShowLabelHint();

        LabelCombo.SelectionChanged += async (_, _) => await ApplyLabelAsync();
    }

    /// <summary>Порядок в списке — от свободного к строгому, как читается шкала.</summary>
    private static readonly ConfidentialityLabel[] Labels =
    [
        ConfidentialityLabel.Open,
        ConfidentialityLabel.Restricted,
        ConfidentialityLabel.Closed,
    ];

    private async Task ApplyLabelAsync()
    {
        if (_repo is null || _labelBusy) return;
        if (LabelCombo.SelectedIndex < 0) return;

        var chosen = Labels[LabelCombo.SelectedIndex];
        if (chosen == _label) return;

        // Повышение идет без вопросов: строже — значит безопаснее. Понижение спрашивает
        // мастер-пароль политики, и спрашивает его окно, а делает понижение репозиторий:
        // проверка и действие обязаны жить в одном месте.
        bool done;

        if (chosen > _label)
        {
            done = Trouble(_repo.SetDatabaseLabel(chosen));
        }
        else
        {
            var ask = PasswordDialog.ForMaster(
                $"базы «{_entry.Name}»",
                Confidentiality.Title(_label), Confidentiality.Title(chosen),
                password => _repo.SetDatabaseLabel(chosen, password));

            done = await ask.ShowDialog<bool>(this);
        }

        if (done)
        {
            _label = chosen;
            ShowLabelHint();
            HideError();
            return;
        }

        // Отказались или не вышло — список возвращается на прежнюю метку. Показывать
        // выбранное там, где оно не применилось, нельзя: человек уйдет уверенный, что
        // база помечена.
        _labelBusy = true;
        LabelCombo.SelectedIndex = Array.IndexOf(Labels, _label);
        _labelBusy = false;
    }

    /// <summary>Поставить метку и показать причину, если не вышло.</summary>
    private bool Trouble(string trouble)
    {
        if (trouble.Length == 0) return true;

        ShowError(char.ToUpperInvariant(trouble[0]) + trouble[1..]);
        return false;
    }

    private void ShowLabelHint() => LabelHint.Text = _label switch
    {
        ConfidentialityLabel.Closed =>
            "Закрытый контур: исходящих у этой базы нет вовсе, и перенос данных из нее в менее "
            + "строгую базу запрещен. Метка уезжает вместе с файлом базы.",
        ConfidentialityLabel.Restricted =>
            "Для служебного пользования: пометка для людей и журнала, обмен она не запрещает. "
            + "Точный запрет ставится меткой на свойстве.",
        _ =>
            "Открыто: обычное состояние базы. Понизить метку обратно можно только по "
            + "мастер-паролю политики организации, и понижение попадет в журнал безопасности.",
    };

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
        // Поля серые — менять нечему, но пусть и сохранение не пропустит: разметку правят
        // чаще, чем перечитывают, и забытый IsEnabled не должен становиться дырой.
        var newName = !_canManage || name == _entry.Name ? null : name;

        string? newPwd = null;
        if (_entry.IsPostgres && _canManage && PgPasswordBox.Text is { Length: > 0 } pwd)
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
