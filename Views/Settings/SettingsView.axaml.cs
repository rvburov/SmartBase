using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataBase.Controls.Dialogs;
using DataBase.Database;
using DataBase.Database.Entities;
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

        VirtualizedGridCheck.IsChecked = AppSettings.LoadVirtualizedGrid();
        AsyncRowLoadCheck.IsChecked    = AppSettings.LoadAsyncRowLoad();
    }

    // Переключатель виртуализации таблиц: сохраняем в настройки. Применяется при
    // следующем открытии коллекции (TableView читает флаг в Load).
    private void OnVirtualizedGridChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.IsChecked is bool on)
            AppSettings.SaveVirtualizedGrid(on);
    }

    // Переключатель заглушек «Загрузка…» при прокрутке. Применяется при следующем
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

        TitleText.Text = key switch
        {
            "appearance" => "Оформление",
            "database"   => "Управление базой данных",
            "about"      => "О программе",
            _            => "Общие",
        };
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
