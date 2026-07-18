using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Utils;

namespace SmartBase.Setup;

/// <summary>
/// Окно установщика (вариант A). В обычном режиме — установка/обновление в выбранную
/// папку; в режиме удаления (`--uninstall`) — подтверждение и снос установленной копии.
/// </summary>
public partial class InstallerWindow : Window
{
    private readonly bool _uninstallMode;

    public InstallerWindow() : this(false) { }

    public InstallerWindow(bool uninstallMode)
    {
        InitializeComponent();
        _uninstallMode = uninstallMode;

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? "" : $"Версия {v.Major}.{v.Minor}.{v.Build}";

        if (_uninstallMode) SetupUninstallUi();
        else                SetupInstallUi();
    }

    // ── Установка ──────────────────────────────────────────────────────────
    private void SetupInstallUi()
    {
        PathBox.Text = InstallService.DefaultInstallDir;
        // На macOS ярлыков нет (приложение лежит в ~/Applications, видно в Launchpad/Finder).
        if (InstallService.IsMac) ShortcutCheck.IsVisible = false;
        RefreshInstallState();
        PathBox.TextChanged += (_, _) => RefreshInstallState();
    }

    private void RefreshInstallState()
    {
        var dir = (PathBox.Text ?? "").Trim();

        // Уже установлено сюда → режим обновления (данные пользователя сохранятся).
        if (InstallService.IsAppPresent(dir))
        {
            WarnText.Text          = "Приложение уже установлено в этой папке — файлы будут обновлены, данные пользователя сохранятся.";
            WarnBox.IsVisible      = true;
            PrimaryBtn.Content     = "Обновить";
            PrimaryBtn.IsEnabled   = true;
            UninstallBtn.IsVisible = true;
            return;
        }

        PrimaryBtn.Content     = "Установить";
        UninstallBtn.IsVisible = false;

        // Нет прав на запись (напр. Program Files без администратора) → предупреждаем и не даём ставить.
        if (dir.Length > 0 && !InstallService.CanCreateOrWrite(dir))
        {
            WarnText.Text        = "В эту папку нет прав на запись. Выберите другую (например, предложенную по умолчанию). Установка в «Program Files» требует прав администратора.";
            WarnBox.IsVisible    = true;
            PrimaryBtn.IsEnabled = false;
        }
        else
        {
            WarnBox.IsVisible    = false;
            PrimaryBtn.IsEnabled = true;
        }
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

    private async Task DoInstallAsync()
    {
        var dir = (PathBox.Text ?? "").Trim();
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
            SetBusy(false);
            SubtitleText.Text = $"Ошибка установки: {ex.Message}";
            return;
        }

        var launch = await new ConfirmDialog(
            "Установка завершена. Запустить приложение?", "Запустить", "Закрыть").ShowDialog<bool>(this);
        if (launch)
            TryStart(Path.Combine(dir, InstallService.AppExeName));
        Close();
    }

    // ── Удаление ───────────────────────────────────────────────────────────
    private void SetupUninstallUi()
    {
        Title                     = $"{AppSettings.DisplayName} — Удаление";
        SubtitleText.Text         = $"Удалить {AppSettings.DisplayName} из:\n{InstallService.CurrentDir}";
        PathBlock.IsVisible       = false;
        ShortcutCheck.IsVisible   = false;
        RemoveDataCheck.IsVisible = true;
        PrimaryBtn.Content        = "Удалить";
    }

    // Кнопка «Удалить» в режиме установки: сносит установленную копию по выбранному пути.
    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        var dir = (PathBox.Text ?? "").Trim();
        if (!InstallService.IsAppPresent(dir)) return;
        if (await Confirm($"Удалить {AppSettings.DisplayName} из:\n{dir}") != true) return;

        InstallService.Uninstall(dir, removeData: false);
        Close();
    }

    private async Task DoUninstallAsync()
    {
        var dir = InstallService.CurrentDir;
        if (await Confirm($"Удалить {AppSettings.DisplayName} из:\n{dir}") != true) return;

        SetBusy(true);
        SubtitleText.Text = "Удаление…";
        InstallService.Uninstall(dir, RemoveDataCheck.IsChecked == true);
        Close(); // приложение закроется → отложенный bat удалит папку
    }

    // ── Общее ──────────────────────────────────────────────────────────────
    private async void OnPrimary(object? sender, RoutedEventArgs e)
    {
        if (_uninstallMode) await DoUninstallAsync();
        else                await DoInstallAsync();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private Task<bool> Confirm(string message) =>
        new ConfirmDialog(message, "Удалить").ShowDialog<bool>(this);

    private void SetBusy(bool busy)
    {
        PrimaryBtn.IsEnabled    = !busy;
        CancelBtn.IsEnabled     = !busy;
        UninstallBtn.IsEnabled  = !busy;
        PathBlock.IsEnabled     = !busy;
        ShortcutCheck.IsEnabled = !busy;
        ProgressBlock.IsVisible = busy;
    }

    private static void TryStart(string exe)
    {
        try { Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
        catch { /* запустят вручную */ }
    }
}
