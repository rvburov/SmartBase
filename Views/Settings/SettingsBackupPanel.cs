using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Widgets;
using SmartBase.Database.Backup;
using SmartBase.Database.Entities;
using SmartBase.Settings;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Резервное копирование» (partial SettingsView, без ViewModel — как
/// «Диагностика»). Общие строки (папка, глубина ротации) — в разметке; список баз строится
/// здесь: у каждой базы тумблер автозапуска, редактор расписания (состав зависит от
/// периодичности), кнопка «Копировать сейчас» и строка статуса. Сами копии снимает
/// BackupService в MainWindow: кнопка уходит туда событием <see cref="BackupNowRequested"/>,
/// а ход и итог возвращаются методами OnBackup* (прогресс MainWindow маршалит в UI-поток).
/// </summary>
public partial class SettingsView
{
    /// <summary>«Копировать сейчас» у базы: MainWindow передает запрос BackupService.</summary>
    public event EventHandler<int>? BackupNowRequested;

    // Живые части строк списка по dbId — статус обновляется по событиям службы без
    // перестройки списка (перестройка закрывала бы открытые выпадающие списки).
    private readonly Dictionary<int, TextBlock> _backupStatus = [];

    // Глубина ротации: закрытый список вариантов вместо свободного ввода.
    private static readonly int[] BackupKeepOptions = [1, 2, 3, 5, 10, 20, 30, 50, 100];

    // Подписи периодичностей — индексы совпадают с BackupFrequency.
    private static readonly string[] BackupFreqLabels =
        ["Каждый день", "Каждую неделю", "Каждый месяц", "Каждый год"];

    private static readonly (string Key, string Label)[] BackupDayItems =
    [
        ("mon", "Пн"), ("tue", "Вт"), ("wed", "Ср"), ("thu", "Чт"),
        ("fri", "Пт"), ("sat", "Сб"), ("sun", "Вс"),
    ];

    private static readonly string[] BackupMonthLabels =
        ["января", "февраля", "марта", "апреля", "мая", "июня",
         "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    // Гасит обработчики, пока строки наполняются кодом (как _generalLoaded у «Общего»).
    private bool _backupBuilding;

    // ── Построение раздела ────────────────────────────────────────────────────

    private void RebuildBackupPanel()
    {
        _backupBuilding = true;

        // Папка: показываем фактическую (настроенную или стандартную), «Сбросить» — только
        // когда выбрана своя.
        BackupFolderText.Text          = AppSettings.ResolveBackupFolder();
        BackupFolderResetBtn.IsVisible = AppSettings.LoadBackupFolder().Length > 0;

        // Глубина ротации: закрытый список; значение вне списка (правка app.db) добавляем
        // отдельным пунктом, чтобы честно показать текущее состояние.
        var keep = AppSettings.LoadBackupKeep();
        int[] options = BackupKeepOptions.Contains(keep)
            ? BackupKeepOptions
            : [.. BackupKeepOptions.Append(keep).Order()];
        BackupKeepCombo.ItemsSource   = options.Select(n => n.ToString()).ToList();
        BackupKeepCombo.SelectedIndex = Array.IndexOf(options, keep);
        BackupKeepCombo.Tag           = options;   // для обработчика выбора

        RefreshBackupList();
        _backupBuilding = false;
    }

    /// <summary>Перестроить список баз (вход в раздел; MainWindow дергает после изменений
    /// реестра, когда раздел открыт).</summary>
    public void RefreshBackupList()
    {
        _backupStatus.Clear();
        BackupList.Children.Clear();
        if (_registry is null) return;

        var building = _backupBuilding;
        _backupBuilding = true;
        foreach (var entry in _registry.GetAll())
        {
            BackupList.Children.Add(BuildBackupRow(entry));
            BackupList.Children.Add(new Border { Classes = { "separator", "h" } });
        }
        _backupBuilding = building;
    }

    private Control BuildBackupRow(DatabaseRefEntity entry)
    {
        var enabled  = AppSettings.LoadBackupEnabled(entry.Id);
        var schedule = BackupSchedule.Parse(AppSettings.LoadBackupSchedule(entry.Id));

        // ── Шапка строки: имя + метки, как в «Управлении базой данных» ────────
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new TextBlock { Classes = { "title" }, Text = entry.Name });
        if (entry.IsPostgres)
            header.Children.Add(BadgeText("PostgreSQL"));
        if (entry.IsDefault)
            header.Children.Add(BadgeText("По умолчанию"));

        // ── Редактор расписания ───────────────────────────────────────────────
        var freq = new ComboBox
        {
            Width         = 160,
            ItemsSource   = BackupFreqLabels,
            SelectedIndex = (int)schedule.Frequency,
        };

        var days = new CheckListDropdown { Width = 170 };
        days.SetItems(
            [.. BackupDayItems.Select(d => new ViewItem(d.Key, d.Label))],
            hidden: [.. BackupDayItems.Select(d => d.Key)
                                      .Where(k => !schedule.Days.Contains(TokenToDay(k)))]);

        var dayOfMonth = new ComboBox
        {
            Width         = 110,
            ItemsSource   = Enumerable.Range(1, 31).Select(d => $"{d}-го").ToList(),
            SelectedIndex = schedule.DayOfMonth - 1,
        };

        var yearDay = new ComboBox
        {
            Width         = 70,
            ItemsSource   = Enumerable.Range(1, 31).Select(d => d.ToString()).ToList(),
            SelectedIndex = schedule.Day - 1,
        };
        var yearMonth = new ComboBox
        {
            Width         = 120,
            ItemsSource   = BackupMonthLabels,
            SelectedIndex = schedule.Month - 1,
        };

        var time = new TimeField { Width = 96, Time = schedule.Time.ToTimeSpan() };

        var editors = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 8,
            IsEnabled   = enabled,
        };
        editors.Children.Add(freq);
        editors.Children.Add(days);
        editors.Children.Add(dayOfMonth);
        editors.Children.Add(yearDay);
        editors.Children.Add(yearMonth);
        editors.Children.Add(new TextBlock { Text = "в", VerticalAlignment = VerticalAlignment.Center });
        editors.Children.Add(time);

        // ── Статус ────────────────────────────────────────────────────────────
        var status = new TextBlock { Classes = { "desc" }, Text = BackupLastRunText(entry.Id) };
        _backupStatus[entry.Id] = status;

        // ── Правая часть: «Копировать сейчас» + тумблер ───────────────────────
        var runNow = new Button { Content = "Копировать сейчас", VerticalAlignment = VerticalAlignment.Center };
        runNow.Click += (_, _) => BackupNowRequested?.Invoke(this, entry.Id);

        var toggle = new ToggleSwitch
        {
            OnContent         = "",
            OffContent        = "",
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked         = enabled,
        };

        // ── Сохранение изменений ──────────────────────────────────────────────
        // Видимость редакторов — по периодичности; расписание собирается из контролов.
        void ApplyVisibility()
        {
            var f = (BackupFrequency)Math.Max(0, freq.SelectedIndex);
            days.IsVisible       = f == BackupFrequency.Weekly;
            dayOfMonth.IsVisible = f == BackupFrequency.Monthly;
            yearDay.IsVisible    = f == BackupFrequency.Yearly;
            yearMonth.IsVisible  = f == BackupFrequency.Yearly;
        }

        void Save()
        {
            if (_backupBuilding) return;
            AppSettings.SaveBackupSchedule(entry.Id, ComposeBackupSchedule(
                freq, days, dayOfMonth, yearDay, yearMonth, time).ToString());
        }

        ApplyVisibility();
        freq.SelectionChanged       += (_, _) => { ApplyVisibility(); Save(); };
        days.HiddenChanged          += (_, _) => Save();
        dayOfMonth.SelectionChanged += (_, _) => Save();
        yearDay.SelectionChanged    += (_, _) => Save();
        yearMonth.SelectionChanged  += (_, _) => Save();
        time.TimeChanged            += (_, _) => Save();

        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_backupBuilding) return;
            var on = toggle.IsChecked == true;
            editors.IsEnabled = on;
            AppSettings.SaveBackupEnabled(entry.Id, on);
        };

        // ── Сборка строки ─────────────────────────────────────────────────────
        var left = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 32, 0) };
        left.Children.Add(header);
        left.Children.Add(editors);
        left.Children.Add(status);

        var right = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(runNow);
        right.Children.Add(toggle);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        return new Border { Classes = { "settingRow" }, Child = grid };
    }

    // Расписание из состояния контролов строки. Выпадающие списки не дают невалидных
    // значений; дни недели гарантирует минимум один сам CheckListDropdown; невозможный
    // день года (30 февраля) чинит min() при разборе даты запуска в планировщике —
    // здесь достаточно поджать день под месяц.
    private static BackupSchedule ComposeBackupSchedule(
        ComboBox freq, CheckListDropdown days, ComboBox dayOfMonth,
        ComboBox yearDay, ComboBox yearMonth, TimeField time)
    {
        var f = (BackupFrequency)Math.Max(0, freq.SelectedIndex);
        var t = TimeOnly.FromTimeSpan(time.Time);
        return f switch
        {
            BackupFrequency.Weekly => new BackupSchedule
            {
                Frequency = f, Time = t,
                Days = [.. BackupDayItems.Select(d => TokenToDay(d.Key))
                                         .Where(d => !HiddenDays(days).Contains(d))],
            },
            BackupFrequency.Monthly => new BackupSchedule
            {
                Frequency = f, Time = t,
                DayOfMonth = Math.Max(1, dayOfMonth.SelectedIndex + 1),
            },
            BackupFrequency.Yearly => new BackupSchedule
            {
                Frequency = f, Time = t,
                Month = Math.Max(1, yearMonth.SelectedIndex + 1),
                Day   = Math.Min(Math.Max(1, yearDay.SelectedIndex + 1),
                                 DateTime.DaysInMonth(2000, Math.Max(1, yearMonth.SelectedIndex + 1))),
            },
            _ => new BackupSchedule { Frequency = BackupFrequency.Daily, Time = t },
        };
    }

    // Скрытые (не отмеченные) дни строки — прямо из пунктов выпадающего списка.
    private static HashSet<DayOfWeek> HiddenDays(CheckListDropdown days) =>
        [.. days.HiddenKeys.Select(TokenToDay)];

    private static DayOfWeek TokenToDay(string token) => token switch
    {
        "tue" => DayOfWeek.Tuesday, "wed" => DayOfWeek.Wednesday, "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday, "sat" => DayOfWeek.Saturday, "sun" => DayOfWeek.Sunday,
        _     => DayOfWeek.Monday,
    };

    private static TextBlock BadgeText(string text) => new()
    {
        Text              = text,
        Classes           = { "desc" },
        VerticalAlignment = VerticalAlignment.Center,
    };

    private string BackupLastRunText(int dbId) =>
        AppSettings.LoadBackupLastRun(dbId) is { } last
            ? $"Последняя копия: {last:dd.MM.yyyy HH:mm}"
            : "Копий еще не было";

    // ── Общие настройки раздела ───────────────────────────────────────────────

    private async void OnBackupFolderPickClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var current = AppSettings.ResolveBackupFolder();
        var start   = Directory.Exists(current)
            ? await top.StorageProvider.TryGetFolderFromPathAsync(current)
            : null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Папка резервных копий",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });

        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(folder)) return;

        AppSettings.SaveBackupFolder(folder);
        BackupFolderText.Text          = AppSettings.ResolveBackupFolder();
        BackupFolderResetBtn.IsVisible = true;
    }

    private void OnBackupFolderResetClick(object? sender, RoutedEventArgs e)
    {
        AppSettings.SaveBackupFolder("");
        BackupFolderText.Text          = AppSettings.ResolveBackupFolder();
        BackupFolderResetBtn.IsVisible = false;
    }

    private void OnBackupKeepChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_backupBuilding) return;
        if (BackupKeepCombo.Tag is int[] options
            && BackupKeepCombo.SelectedIndex >= 0 && BackupKeepCombo.SelectedIndex < options.Length)
            AppSettings.SaveBackupKeep(options[BackupKeepCombo.SelectedIndex]);
    }

    // ── Ход и итог копии (события BackupService через MainWindow) ─────────────

    /// <summary>Копия начата: строка статуса — «Копирую…». UI-поток.</summary>
    public void OnBackupStarted(int dbId) => SetBackupStatus(dbId, "Копирую…", error: false);

    /// <summary>Ход копии (сообщение колбэка BackupTo, доля 0..1). Звать с UI-потока —
    /// событие службы приходит из фонового, MainWindow маршалит.</summary>
    public void OnBackupProgress(int dbId, string message, double? fraction)
    {
        // BackupTo шлет «Копирую "X"\nN из M МБ» — для однострочного статуса хватает фазы.
        var nl    = message.IndexOf('\n');
        var phase = nl < 0 ? message : message[..nl];
        SetBackupStatus(dbId, fraction is { } f ? $"{phase} · {f * 100:N0} %" : phase, error: false);
    }

    /// <summary>Итог: успех — свежая отметка «Последняя копия…», сбой — текст ошибки красным.</summary>
    public void OnBackupFinished(int dbId, bool ok, string message) =>
        SetBackupStatus(dbId, ok ? BackupLastRunText(dbId) : $"Ошибка: {message}", error: !ok);

    private void SetBackupStatus(int dbId, string text, bool error)
    {
        if (!_backupStatus.TryGetValue(dbId, out var status)) return;
        status.Text = text;
        // Ошибка — красным поверх серого desc-стиля; успех возвращает стандартный цвет.
        if (error)
            status.Foreground = Application.Current?.TryFindResource("BrushRed", out var res) == true
                && res is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#FF3B30"));
        else
            status.ClearValue(TextBlock.ForegroundProperty);
    }
}
