using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Serilog;
using SmartBase.Controls.Dialogs;
using SmartBase.Utils;

namespace SmartBase.Setup;

/// <summary>
/// Единственное окно установщика: установка, обновление и удаление в одном месте.
/// Отдельного деинсталлятора нет — эта же программа кладётся рядом с приложением как
/// `SmartBase-Setup`, и двойной клик по ней открывает это окно.
///
/// При открытии окно само ищет установленные копии на машине (см. FindInstallations),
/// поэтому папку установки не нужно помнить и вводить руками. Состояний три: копий нет,
/// найдена одна, найдено несколько.
/// </summary>
public partial class InstallerWindow : Window
{
    private readonly bool _preselectCurrent;   // запуск из ярлыка «Удалить»: показать свою копию
    private List<Installation> _found = [];
    private bool _manualPath;                  // пользователь ушёл в «другую папку»
    private bool _busy;
    private readonly string _myVersion;

    public InstallerWindow() : this(false) { }

    public InstallerWindow(bool uninstallMode)
    {
        InitializeComponent();
        _preselectCurrent = uninstallMode;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        _myVersion       = v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        VersionText.Text = v is null ? "" : $"Версия {_myVersion}";

        // На macOS ярлыков нет (приложение лежит в ~/Applications, видно в Launchpad/Finder).
        ShortcutCheck.IsVisible = !InstallService.IsMac;
        PathBox.Text            = InstallService.DefaultInstallDir;
        PathBox.TextChanged    += (_, _) => RefreshState();

        _ = LoadInstallationsAsync();
    }

    // ── Поиск копий ────────────────────────────────────────────────────────

    // Spotlight и реестр отвечают не мгновенно — ищем в фоне, чтобы окно открылось сразу.
    private async Task LoadInstallationsAsync()
    {
        SetBusyControls(true);
        try   { _found = await Task.Run(InstallService.FindInstallations); }
        catch (Exception ex) { Log.Error(ex, "Поиск установленных копий не удался"); _found = []; }
        SetBusyControls(false);

        InstallsList.ItemsSource = _found;
        if (_found.Count > 1)
        {
            // Из ярлыка «Удалить» пришли ради конкретной копии — она и должна быть выбрана.
            var own = _found.FirstOrDefault(f =>
                string.Equals(f.Dir, InstallService.CurrentDir, StringComparison.OrdinalIgnoreCase));
            InstallsList.SelectedItem = (_preselectCurrent ? own : null) ?? _found[0];
        }
        RefreshState();
    }

    /// <summary>Копия, к которой относятся кнопки: единственная найденная либо выбранная в списке.</summary>
    private Installation? Selected =>
        _manualPath      ? null
        : _found.Count == 1 ? _found[0]
        : InstallsList.SelectedItem as Installation;

    /// <summary>Папка, с которой работают кнопки, как её задал человек.</summary>
    private string TargetDir =>
        Selected?.Dir ?? (PathBox.Text ?? "").Trim();

    /// <summary>
    /// Папка, куда установка ляжет на самом деле. Путь, вписанный руками, приводится к
    /// «выделенному» так же, как выбранный кнопкой «Обзор»: на Windows/Linux дописывается
    /// подпапка SmartBase. Иначе бинарь лёг бы прямо в «Документы», а удаление потом искало
    /// бы свои файлы посреди чужих. Для уже установленной копии и на macOS — без изменений.
    /// </summary>
    private string EffectiveDir => InstallService.EnsureDedicatedDir(TargetDir);

    // ── Состояние окна ─────────────────────────────────────────────────────

    private void RefreshState()
    {
        if (_busy) return;

        var showFound  = !_manualPath && _found.Count > 0;
        var showList   = showFound && _found.Count > 1;
        var showSingle = showFound && _found.Count == 1;

        SingleBlock.IsVisible     = showSingle;
        InstallsList.IsVisible    = showList;
        PathBlock.IsVisible       = !showFound;
        OtherFolderBtn.IsVisible  = _found.Count > 0;
        OtherFolderBtn.Content    = _manualPath ? "← К найденным копиям" : "Установить в другую папку…";

        if (showSingle)
        {
            SinglePathText.Text    = _found[0].Dir;
            SingleVersionText.Text = _found[0].VersionText;
        }

        SubtitleText.Text =
            _manualPath        ? "Выберите папку для установки приложения"
            : _found.Count == 0 ? "Установленных копий не найдено"
            : _found.Count == 1 ? "Найдена установленная копия"
                                : "Найдено несколько установленных копий";

        var dir      = EffectiveDir;
        var isUpdate = InstallService.IsAppPresent(dir);

        // Подсказка появляется, только когда путь вписали руками и он будет изменён.
        var typed = TargetDir;
        EffectivePathText.IsVisible = !showFound && typed.Length > 0
                                      && !string.Equals(dir, typed, StringComparison.Ordinal);
        EffectivePathText.Text      = $"Будет установлено в: {dir}";

        UninstallBtn.IsVisible    = isUpdate;
        RemoveDataCheck.IsVisible = isUpdate;
        PrimaryBtn.IsEnabled      = true;

        if (isUpdate)
        {
            // Та же версия — это переустановка поверх, а не обновление: называем честно.
            var same = Selected?.Version is { } sv && sv == _myVersion;
            PrimaryBtn.Content = same ? "Переустановить" : "Обновить";
            WarnText.Text      = "Приложение уже установлено в этой папке — файлы будут обновлены, "
                               + "данные пользователя сохранятся.";
            WarnBox.IsVisible  = true;
            return;
        }

        PrimaryBtn.Content = "Установить";

        // Нет прав на запись (напр. Program Files без администратора) → предупреждаем и не даём ставить.
        if (dir.Length > 0 && !InstallService.CanCreateOrWrite(dir))
        {
            WarnText.Text        = "В эту папку нет прав на запись. Выберите другую (например, предложенную "
                                 + "по умолчанию). Установка в «Program Files» требует прав администратора.";
            WarnBox.IsVisible    = true;
            PrimaryBtn.IsEnabled = false;
        }
        else
        {
            WarnBox.IsVisible = false;
        }
    }

    private void OnInstallSelected(object? sender, SelectionChangedEventArgs e) => RefreshState();

    private void OnOtherFolder(object? sender, RoutedEventArgs e)
    {
        _manualPath = !_manualPath;
        RefreshState();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Папка установки", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } p)
            // Дописываем выделенную подпапку (…\SmartBase), чтобы удаление не задело чужие файлы
            // общей папки — удаление чистит папку установки. На macOS путь остаётся как есть.
            PathBox.Text = InstallService.EnsureDedicatedDir(p);
    }

    // ── Установка / обновление ─────────────────────────────────────────────

    private async Task DoInstallAsync()
    {
        var dir = EffectiveDir;   // вписанный руками путь приводится к «выделенной» подпапке
        if (dir.Length == 0) return;

        SetBusy(true);
        SubtitleText.Text = "Идёт установка, пожалуйста подождите…";
        try
        {
            // Проценты рисует сам индикатор — в тексте фазы их не дублируем.
            var progress = new Progress<(double Percent, string Status)>(t =>
                ProgressBlock.SetProgress(t.Percent, t.Status));
            await InstallService.InstallAsync(dir, ShortcutCheck.IsChecked == true, progress);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка установки в папку {Dir:l}", dir);
            SetBusy(false);
            SubtitleText.Text = $"Ошибка установки: {ex.Message}";
            return;
        }

        var launch = await new ConfirmDialog(
            "Установка завершена. Запустить приложение?", "Запустить", "Закрыть").ShowDialog<bool>(this);
        if (launch)
            TryStart(InstallService.InstalledPath(dir));   // macOS — .app-бандл, иначе бинарь
        Close();
    }

    // ── Удаление ───────────────────────────────────────────────────────────

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        var dir = EffectiveDir;
        if (!InstallService.IsAppPresent(dir)) return;

        var withData = RemoveDataCheck.IsChecked == true;
        var message  = $"Удалить {AppSettings.DisplayName} из:\n{dir}"
                     + (withData
                        ? "\n\nВместе с данными пользователя: коллекции, записи, настройки и журнал "
                          + "будут стёрты безвозвратно. Базы, подключённые из других папок и с сервера "
                          + "PostgreSQL, не затрагиваются."
                        : "\n\nДанные пользователя сохранятся.");
        if (await new ConfirmDialog(message, "Удалить").ShowDialog<bool>(this) != true) return;

        SetBusy(true);
        SubtitleText.Text = "Удаление…";
        try { InstallService.Uninstall(dir, withData); }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка удаления из папки {Dir:l}", dir);
            SetBusy(false);
            SubtitleText.Text = $"Ошибка удаления: {ex.Message}";
            return;
        }

        // Снесли копию, из которой сами и запущены → закрываемся: остальное доделает
        // отложенное удаление. Чужую копию удалили — остаёмся и обновляем список.
        if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(InstallService.CurrentDir),
                          StringComparison.OrdinalIgnoreCase))
        {
            Close();
            return;
        }

        SetBusy(false);
        _manualPath = false;
        await LoadInstallationsAsync();
    }

    // ── Общее ──────────────────────────────────────────────────────────────

    private async void OnPrimary(object? sender, RoutedEventArgs e) => await DoInstallAsync();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SetBusyControls(busy);
        ProgressBlock.IsVisible = busy;
    }

    private void SetBusyControls(bool busy)
    {
        PrimaryBtn.IsEnabled     = !busy;
        CancelBtn.IsEnabled      = !busy;
        UninstallBtn.IsEnabled   = !busy;
        PathBlock.IsEnabled      = !busy;
        InstallsList.IsEnabled   = !busy;
        OtherFolderBtn.IsEnabled = !busy;
        ShortcutCheck.IsEnabled  = !busy;
        RemoveDataCheck.IsEnabled = !busy;
    }

    private static void TryStart(string exe)
    {
        try { Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
        catch { /* запустят вручную */ }
    }
}
