using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;

namespace SmartBase.Extensions.PdfAnalyzer;

/// <summary>
/// Окно расширения «Анализ PDF файлов» (в основном стиле приложения): папка с PDF, путь отчёта,
/// тип выгрузки, опции поиска и «Запустить анализ». Логика — в <see cref="PdfAnalyzerEngine"/>;
/// анализ идёт в фоне (<see cref="Task.Run"/>) с прогрессом по файлам и отменой при закрытии окна.
/// </summary>
public partial class PdfAnalyzerWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _syncingChecks;

    public PdfAnalyzerWindow()
    {
        InitializeComponent();
        PreserveCheck.IsEnabled = false;   // при включённом «рекурсивном» (по умолчанию) — недоступно
        UpdateState();
    }

    // ── Выбор папки / файла ───────────────────────────────────────────────────

    private async void OnBrowsePdf(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку с PDF-файлами", AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        var path = folder.Path.LocalPath;
        PdfBox.Text = path;
        ToolTip.SetTip(PdfBox, path);
        if (string.IsNullOrWhiteSpace(SaveBox.Text)) SaveBox.Text = path;   // по умолчанию — та же папка
        UpdateState();
    }

    private async void OnBrowseSave(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? start = null;
        var current = SaveBox.Text?.Trim();
        if (string.IsNullOrEmpty(current)) current = PdfBox.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            start = await StorageProvider.TryGetFolderFromPathAsync(current);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Выберите папку для сохранения результата",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        SaveBox.Text = folder.Path.LocalPath;
        ToolTip.SetTip(SaveBox, SaveBox.Text);
        UpdateState();
    }

    private void OnPathChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    // ── Взаимное исключение опций ─────────────────────────────────────────────

    private void OnRecursiveChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingChecks) return;
        _syncingChecks = true;
        if (RecursiveCheck.IsChecked == true) { PreserveCheck.IsChecked = false; PreserveCheck.IsEnabled = false; }
        else PreserveCheck.IsEnabled = true;
        _syncingChecks = false;
    }

    private void OnPreserveChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncingChecks) return;
        _syncingChecks = true;
        if (PreserveCheck.IsChecked == true) { RecursiveCheck.IsChecked = false; RecursiveCheck.IsEnabled = false; }
        else RecursiveCheck.IsEnabled = true;
        _syncingChecks = false;
    }

    // ── Анализ ────────────────────────────────────────────────────────────────

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_running) return;

        var folder = PdfBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(folder)) { SetStatus("Укажите папку с PDF-файлами", "BrushRed"); return; }
        if (!Directory.Exists(folder))    { SetStatus("Папка не существует", "BrushRed"); return; }

        // Порядок пунктов: сначала все Ведомости (ТД/КД/ПД), затем Описи (КД/ПД), затем «Все данные».
        var exportType = ExportCombo.SelectedIndex switch
        {
            0 => ExportType.Td, 1 => ExportType.Kd, 2 => ExportType.PdRegister,
            3 => ExportType.Inventory, 4 => ExportType.PdInventory, _ => ExportType.All,
        };
        var recursive     = RecursiveCheck.IsChecked == true;
        var preserveOrder = PreserveCheck.IsChecked == true;
        var splitName     = SplitNameCheck.IsChecked == true;
        var savePath      = SaveBox.Text?.Trim() ?? "";

        SetRunning(true);
        SetStatus("Анализ…", "BrushAccent");
        Progress.SetProgress(0);
        CurrentFileText.Text = ""; CurrentFileText.IsVisible = false;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<AnalyzeProgress>(p =>
        {
            Progress.SetProgress(p.Total > 0 ? (double)p.Current / p.Total : 0);
            CurrentFileText.Text = $"{p.Current} из {p.Total}: {p.File}";
            CurrentFileText.IsVisible = true;
        });

        try
        {
            var result = await Task.Run(
                () => PdfAnalyzerEngine.Run(folder, savePath, exportType, recursive, preserveOrder, splitName, progress, ct), ct);

            Progress.SetProgress(1);
            CurrentFileText.Text = ""; CurrentFileText.IsVisible = false;
            SetStatus($"Готово: {Path.GetFileName(result.SavePath)} " +
                      $"(обработано {result.Processed} из {result.TotalFiles}" +
                      (result.Errors > 0 ? $", ошибок: {result.Errors}" : "") + ")", "BrushAccent");
            await OfferResult(result);
        }
        catch (OperationCanceledException) { SetStatus("Отменено", "BrushSecondaryText"); HideCurrentFile(); }
        catch (PdfAnalyzeException ex)     { SetStatus(ex.Message, "BrushRed"); HideCurrentFile(); }
        catch (Exception ex)              { SetStatus($"Ошибка: {ex.Message}", "BrushRed"); HideCurrentFile(); }
        finally { SetRunning(false); }
    }

    private async Task OfferResult(AnalyzeResult result)
    {
        var open = await new ConfirmDialog(result.StatsText, confirmText: "Открыть папку", cancelText: "Закрыть")
            .ShowDialog<bool>(this);
        if (!open) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{result.SavePath}\"",
                UseShellExecute = true,
            });
        }
        catch { /* не критично */ }
    }

    // ── Состояние ─────────────────────────────────────────────────────────────

    private void UpdateState()
    {
        var ready = !string.IsNullOrWhiteSpace(PdfBox.Text);
        ExecuteBtn.IsEnabled = ready && !_running;
        if (!_running && !ready) SetStatus("Укажите папку с PDF-файлами", "BrushSecondaryText");
        else if (!_running)      SetStatus("Готов к анализу", "BrushAccent");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        PdfBtn.IsEnabled = SaveBtn.IsEnabled = ExportCombo.IsEnabled = !running;
        SplitNameCheck.IsEnabled = !running;
        RecursiveCheck.IsEnabled = !running && PreserveCheck.IsChecked != true;
        PreserveCheck.IsEnabled  = !running && RecursiveCheck.IsChecked != true;
        ExecuteBtn.IsEnabled = !running && !string.IsNullOrWhiteSpace(PdfBox.Text);
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        if (this.TryFindResource(brushKey, out var v) && v is IBrush brush)
            StatusText.Foreground = brush;
    }

    private void HideCurrentFile()
    {
        CurrentFileText.Text = "";
        CurrentFileText.IsVisible = false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
