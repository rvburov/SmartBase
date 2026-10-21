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
/// итог обновляет дату успеха через OnBackupFinished; ход и ошибки живут в фоновых работах.
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
        // Метка та же, что в «Управлении базой данных»: движок у каждой строки, у базы по
        // умолчанию — пометка в скобках.
        header.Children.Add(BadgeText(
            DatabaseRow.EngineName(entry.IsPostgres)
            + (entry.IsDefault ? " (По умолчанию)" : "")));

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

        var yearDay = new ComboBox { Width = 70 };
        var yearMonth = new ComboBox
        {
            Width         = 120,
            ItemsSource   = BackupMonthLabels,
            SelectedIndex = schedule.Month - 1,
        };

        // Список дней следует за месяцем, как сетка месяца в календаре: у февраля максимум
        // 29 (правило годовое, без года — 29-е существует в високосные годы, в обычные
        // копия снимется 28-го, см. BackupPlanner), у апреля 30. «31 февраля» выбрать
        // нельзя физически. Выбранный день при смене месяца сохраняется, лишний — поджимается.
        void SyncYearDays(int selectedDay)
        {
            var month = Math.Max(1, yearMonth.SelectedIndex + 1);
            var max   = DateTime.DaysInMonth(2000, month);   // 2000 високосный: февраль → 29
            if (yearDay.Items.Count != max)
                yearDay.ItemsSource = Enumerable.Range(1, max).Select(d => d.ToString()).ToList();
            yearDay.SelectedIndex = Math.Min(Math.Max(1, selectedDay), max) - 1;
        }
        SyncYearDays(schedule.Day);

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
        var status = new TextBlock { Classes = { "desc" }, Text = BackupStatusText(entry.Id) };
        _backupStatus[entry.Id] = status;

        // ── Правая часть: «Копировать сейчас» + тумблер ───────────────────────
        // Текстовые кнопки настроек — общий стиль «dialog» (как «Обзор…» и «Проверить»);
        // «auto» снимает фиксированную ширину — подпись длиннее стандартных 110px.
        var runNow = new Button
        {
            Classes           = { "dialog", "auto" },
            Content           = "Копировать сейчас",
            VerticalAlignment = VerticalAlignment.Center,
        };
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
            RefreshBackupStatus(entry.Id);   // дата следующей копии следует за расписанием
        }

        ApplyVisibility();
        freq.SelectionChanged       += (_, _) => { ApplyVisibility(); Save(); };
        days.HiddenChanged          += (_, _) => Save();
        dayOfMonth.SelectionChanged += (_, _) => Save();
        yearDay.SelectionChanged    += (_, _) => Save();
        yearMonth.SelectionChanged  += (_, _) => { SyncYearDays(yearDay.SelectedIndex + 1); Save(); };
        time.TimeChanged            += (_, _) => Save();

        toggle.IsCheckedChanged += (_, _) =>
        {
            if (_backupBuilding) return;
            var on = toggle.IsChecked == true;
            editors.IsEnabled = on;
            AppSettings.SaveBackupEnabled(entry.Id, on);
            RefreshBackupStatus(entry.Id);   // «Следующая копия» видна только при включенном
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

    // «Последняя копия: … · Следующая копия: …». Дата следующей считается тем же
    // планировщиком и от тех же системных часов, что у службы, — показывается фактический
    // день с учетом длины месяцев и високосности конкретного года (правило «31-е» в
    // коротком месяце здесь честно превращается в его последний день). До первого тика
    // службы якорь — «сейчас», как в BackupService; просроченный запуск (догон после
    // выключенного приложения) показывается словами, а не датой в прошлом.
    internal static string BackupStatusText(int dbId)
    {
        var last = AppSettings.LoadBackupLastRun(dbId);
        var success = AppSettings.LoadBackupRunState(dbId).LastSuccess;
        var text = success is { } l ? $"Последняя успешная копия: {l:dd.MM.yyyy HH:mm}"
            : last is null ? "Успешных копий пока нет" : "Нет сведений об успешных копиях";
        if (!AppSettings.LoadBackupEnabled(dbId)) return text;

        var now  = DateTime.Now;
        var next = BackupPlanner.NextRun(
            BackupSchedule.Parse(AppSettings.LoadBackupSchedule(dbId)), last ?? now);
        return next <= now
            ? $"{text} · Следующая копия: в ближайшую минуту"
            : $"{text} · Следующая копия: {next:dd.MM.yyyy HH:mm}";
    }

    private void RefreshBackupStatus(int dbId)
    {
        if (_backupStatus.TryGetValue(dbId, out var status)) status.Text = BackupStatusText(dbId);
    }

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

    // ── Результат копии (событие BackupService через MainWindow) ─────────────

    /// <summary>Ход и ошибки видны в «Фоновых работах»; здесь остаются расписание
    /// и подтвержденная последняя копия.</summary>
    public void OnBackupFinished(int dbId) => RefreshBackupStatus(dbId);

}
