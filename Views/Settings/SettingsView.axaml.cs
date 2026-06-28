using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataBase.Controls.Dialogs;
using DataBase.Database;
using DataBase.Database.Entities;
using DataBase.Settings;
using DataBase.Utils;

namespace DataBase.Views.Settings;

public partial class SettingsView : UserControl
{
    private DatabaseRegistry? _registry;
    // Признак «БД сейчас смонтирована» (даёт MainWindow по рабочему пространству).
    private Func<int, bool>?  _isMounted;

    // Реестр изменён — MainWindow монтирует/размонтирует БД и обновляет сайдбар.
    public event EventHandler<DatabaseRefEntity>? DatabaseAdded;
    public event EventHandler<int>?               DatabaseRemoved;
    // Попытка переподключить недоступную БД (по DbId).
    public event EventHandler<int>?               DatabaseReconnectRequested;

    public SettingsView()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
            VersionText.Text = $"Версия {version.Major}.{version.Minor}.{version.Build}";

        AsyncRowLoadCheck.IsChecked = AppSettings.LoadAsyncRowLoad();

        // Слайдер размера шрифта отражает текущую дельту (стандарт = 0, диапазон -3…+3).
        FontSizeSlider.Value = FontScale.Delta;

        // Слайдер масштаба отражает текущий уровень в процентах (стандарт = 100, диапазон 70…130).
        ScaleSlider.Value = Scaling.Current * 100;

        // Стартовый раздел: выделить сохранённый пункт (по Tag).
        SelectStartupModule(AppSettings.LoadStartupModule());

        // Стартовый вид календаря: выделить по индексу (Tag = "0".."5").
        CalendarViewCombo.SelectedIndex = AppSettings.LoadCalendarView();

        // Стартовый вид конструктора: Таблица (0) или Список (1).
        ConstructorViewCombo.SelectedIndex = AppSettings.LoadConstructorView() == "list" ? 1 : 0;
    }

    // ── Стартовый раздел ─────────────────────────────────────────────────────
    //
    // Какой раздел навигации (ActivityBar) открывается при запуске. Применяется при
    // следующем запуске — MainWindow читает AppSettings.LoadStartupModule() на старте.
    private void SelectStartupModule(string module)
    {
        foreach (var obj in StartupModuleCombo.Items)
            if (obj is ComboBoxItem { Tag: string tag } item && tag == module)
            {
                StartupModuleCombo.SelectedItem = item;
                return;
            }
    }

    private void OnStartupModuleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (StartupModuleCombo.SelectedItem is ComboBoxItem { Tag: string module })
            AppSettings.SaveStartupModule(module);
    }

    // ── Стартовый вид календаря ──────────────────────────────────────────────
    //
    // Какой вид (Расписание/Журнал/День/Неделя/Месяц/Год) открывается при переходе в
    // Календарь. Индекс совпадает с порядком пунктов и ViewSelector навбара (0..5).
    // Применяется при следующем запуске — календарь читает AppSettings.LoadCalendarView().
    private void OnCalendarViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CalendarViewCombo.SelectedIndex >= 0)
            AppSettings.SaveCalendarView(CalendarViewCombo.SelectedIndex);
    }

    // ── Стартовый вид конструктора ───────────────────────────────────────────
    //
    // Вид коллекции (Таблица/Список) для коллекций без своего сохранённого режима.
    // Применяется при следующем открытии коллекции (репозиторий читает его как дефолт).
    private void OnConstructorViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConstructorViewCombo.SelectedItem is ComboBoxItem { Tag: string mode })
            AppSettings.SaveConstructorView(mode);
    }

    // ── Уровень масштабирования ──────────────────────────────────────────────
    //
    // Масштаб применяется ко всему окну вживую: MainWindow слушает Scaling.ScaleChanged
    // и ставит LayoutTransform на корень. Слайдер — в процентах, Scaling хранит множитель.
    private void OnScaleChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        Scaling.SetScale(Math.Round(e.NewValue) / 100.0);
        UpdateScaleBadge();
    }

    private void OnScaleSliderEntered(object? sender, PointerEventArgs e)
    {
        UpdateScaleBadge();
        ScaleValuePopup.IsOpen = true;
    }

    private void OnScaleSliderExited(object? sender, PointerEventArgs e) => ScaleValuePopup.IsOpen = false;

    // Текст = текущий масштаб в процентах; смещение пузырька — под текущий ползунок.
    private void UpdateScaleBadge()
    {
        ScaleValueText.Text = $"{(int)Math.Round(ScaleSlider.Value)} %";

        var range = ScaleSlider.Maximum - ScaleSlider.Minimum;
        var w     = ScaleSlider.Bounds.Width;
        if (range <= 0 || w <= 0) return;

        var frac = (ScaleSlider.Value - ScaleSlider.Minimum) / range;
        const double thumbW = 26;
        // Ползунок масштабируется LayoutTransform'ом окна, а попап живёт в немасштабированном
        // оверлее → смещение домножаем на текущий масштаб, иначе плашка «уезжает» от ползунка.
        ScaleValuePopup.HorizontalOffset = (frac * (w - thumbW) + thumbW / 2 - w / 2) * Scaling.Current;
    }

    // Кнопка-стрелка: подтверждение в стандартном диалоге → возврат к 100 %.
    private async void OnScaleReset(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog("Вернуть уровень масштабирования по умолчанию?", "Сбросить");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        ScaleSlider.Value = AppSettings.ScaleDefault * 100;   // → OnScaleChanged → SetScale(1.0)
    }

    // Размер шрифта меняется вживую: дельта пишется в Application.Resources (DynamicResource),
    // C#-виды пересобираются по событию FontScale.Changed.
    private void OnFontSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        FontScale.SetDelta((int)Math.Round(e.NewValue));
        UpdateFontBadge();
    }

    // Пузырёк со значением показываем при наведении/перетаскивании ползунка.
    private void OnFontSliderEntered(object? sender, PointerEventArgs e)
    {
        UpdateFontBadge();
        FontValuePopup.IsOpen = true;
    }

    private void OnFontSliderExited(object? sender, PointerEventArgs e) => FontValuePopup.IsOpen = false;

    // Текст = итоговый базовый размер (стандарт ± дельта); смещение пузырька — под текущий ползунок.
    private void UpdateFontBadge()
    {
        var delta     = (int)Math.Round(FontSizeSlider.Value);
        var effective = FontScale.BaseFontSize + delta;            // итоговый размер шрифта
        FontValueText.Text = ((int)Math.Round(effective)).ToString();

        // Квадрат: сторона растёт/уменьшается вместе с размером шрифта.
        var side = Math.Round(effective * 2.0);
        FontBadgeBorder.Width  = side;
        FontBadgeBorder.Height = side;

        var range = FontSizeSlider.Maximum - FontSizeSlider.Minimum;
        var w     = FontSizeSlider.Bounds.Width;
        if (range <= 0 || w <= 0) return;

        var frac = (FontSizeSlider.Value - FontSizeSlider.Minimum) / range;
        const double thumbW = 26;
        // Тот же учёт масштаба окна, что и у плашки масштабирования (попап — в оверлее).
        FontValuePopup.HorizontalOffset = (frac * (w - thumbW) + thumbW / 2 - w / 2) * Scaling.Current;
    }

    // Кнопка-стрелка: подтверждение в стандартном диалоге → возврат к стандарту (дельта 0).
    private async void OnFontSizeReset(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog("Вернуть размер текста по умолчанию?", "Сбросить");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        FontSizeSlider.Value = AppSettings.FontDeltaDefault;   // → OnFontSizeChanged → SetDelta(0)
    }

    // Переключатель фоновой подгрузки строк («Загрузка…»). Применяется при следующем
    // открытии коллекции (VirtualRowList читает флаг в конструкторе).
    private void OnAsyncRowLoadChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.IsChecked is bool on)
            AppSettings.SaveAsyncRowLoad(on);
    }

    // Вызывается из MainWindow: реестр + способ узнать, смонтирована ли БД сейчас.
    public void Initialize(DatabaseRegistry registry, Func<int, bool> isMounted)
    {
        _registry  = registry;
        _isMounted = isMounted;
        RefreshDatabaseList();
    }

    // Перестроить список (статус монтирования мог измениться). Вызывается из MainWindow.
    public void RefreshDatabases() => RefreshDatabaseList();

    // Переключение раздела: заголовок + видимость соответствующей панели.
    public void ShowCategory(string key)
    {
        GeneralPanel.IsVisible    = key == "general";
        AppearancePanel.IsVisible = key == "appearance";
        DatabasePanel.IsVisible   = key == "database";
        AboutPanel.IsVisible      = key == "about";

        // Заголовок берём из реестра по ключу — единственный источник подписей разделов.
        HeaderBar.Title = (SettingsRegistry.FindByKey(key) ?? SettingsRegistry.All[0]).Label;
    }

    // ── Управление базой данных ──────────────────────────────────────────────

    private void RefreshDatabaseList()
    {
        if (_registry is null) { DatabaseList.ItemsSource = null; return; }

        DatabaseList.ItemsSource = _registry.GetAll().Select(d => new DatabaseRow
        {
            Id        = d.Id,
            Name      = d.Name,
            Path      = d.Path,
            IsDefault = d.IsDefault,
            IsMounted = _isMounted?.Invoke(d.Id) ?? true,
        }).ToList();
    }

    // Создать новую БД: выбрать имя/расположение файла (схема создаётся при монтировании).
    private async void OnCreateDatabaseClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (_registry is null || top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Создать базу данных",
            SuggestedFileName = "database",
            DefaultExtension  = "db",
            FileTypeChoices   = [new FilePickerFileType("База данных SQLite") { Patterns = ["*.db"] }],
        });
        if (file is null) return;

        RegisterDatabase(file.Path.LocalPath);
    }

    // Подключить существующий .db-файл (например, общий в сетевой папке).
    private async void OnConnectDatabaseClick(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (_registry is null || top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Подключить базу данных",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("База данных SQLite") { Patterns = ["*.db"] }],
        });
        var file = files.FirstOrDefault();
        if (file is null) return;

        RegisterDatabase(file.Path.LocalPath);
    }

    private void RegisterDatabase(string path)
    {
        if (_registry is null) return;
        var name  = System.IO.Path.GetFileNameWithoutExtension(path);
        var entry = _registry.Add(name, path);
        // Сначала монтируем (синхронно), затем строим список — чтобы статус был актуален.
        DatabaseAdded?.Invoke(this, entry);
        RefreshDatabaseList();
    }

    private async void OnRemoveDatabaseClick(object? sender, RoutedEventArgs e)
    {
        if (_registry is null) return;
        if (sender is not Button { Tag: int id }) return;

        var entry = _registry.GetAll().FirstOrDefault(d => d.Id == id);
        if (entry is null || entry.IsDefault) return;   // дефолт не отключаем

        if (VisualRoot is not Window win) return;
        var dlg = new ConfirmDialog(
            $"Отключить базу данных «{entry.Name}»? Файл на диске не удаляется — её можно подключить снова.");
        if (await dlg.ShowDialog<bool>(win) != true) return;

        _registry.Remove(id);
        DatabaseRemoved?.Invoke(this, id);
        RefreshDatabaseList();
    }

    private void OnReconnectDatabaseClick(object? sender, RoutedEventArgs e)
    {
        // MainWindow попробует смонтировать БД заново и обновит список (статус).
        if (sender is Button { Tag: int id })
            DatabaseReconnectRequested?.Invoke(this, id);
    }
}
