using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DataBase.Controls.Dialogs;

namespace DataBase.Extensions.PdfRename;

/// <summary>
/// Окно расширения «Переименование PDF» (в основном стиле приложения): выбор папки,
/// «Проверить» (предпросмотр без изменений), «Запустить» (с подтверждением) и
/// «Остановить». Вся логика — в <see cref="PdfRenamer"/>; прогон идёт в фоне с
/// прогрессом и отменой, окно лишь рисует журнал и состояние.
/// </summary>
public partial class PdfRenameWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _running;

    public PdfRenameWindow()
    {
        InitializeComponent();
        UpdateActionState();
    }

    // ── Выбор папки ───────────────────────────────────────────────────────────

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? start = null;
        var current = FolderBox.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            start = await StorageProvider.TryGetFolderFromPathAsync(current);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Выберите папку с PDF-файлами",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });

        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        FolderBox.Text = folder.Path.LocalPath;   // TextChanged обновит состояние кнопок
        ReportFolderContents(folder.Path.LocalPath);
    }

    private void OnFolderTextChanged(object? sender, TextChangedEventArgs e) => UpdateActionState();

    // Кратко показать, что в папке: число PDF и первые имена.
    private void ReportFolderContents(string folder)
    {
        ClearLog();
        AppendLog($"📂 Папка: {folder}");

        if (!Directory.Exists(folder)) { AppendLog("❌ Папка не существует"); return; }

        List<string> pdfs;
        try { pdfs = PdfRenamer.ListPdfFiles(folder); }
        catch (Exception ex) { AppendLog($"❌ Нет доступа к папке: {ex.Message}"); return; }

        if (pdfs.Count == 0) { AppendLog("⚠️ В папке нет PDF-файлов"); return; }

        AppendLog($"📄 Найдено PDF-файлов: {pdfs.Count}");
        foreach (var name in pdfs.Take(5)) AppendLog($"  • {name}");
        if (pdfs.Count > 5) AppendLog($"  … и ещё {pdfs.Count - 5}");
        AppendLog("💡 Нажмите «Проверить» для предпросмотра изменений");
    }

    // ── Проверка (dry-run) ────────────────────────────────────────────────────

    private void OnPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetReadyFolder(out var folder)) return;

        AppendLog("");
        AppendLog("🔍 Проверка (предпросмотр) — изменения НЕ применяются");
        _ = RunAsync(folder, dryRun: true);
    }

    // ── Реальное переименование (с подтверждением) ────────────────────────────

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetReadyFolder(out var folder)) return;

        var willRename = PdfRenamer.BuildPlan(folder).Count(p => p.Outcome == RenameOutcome.WillRename);
        if (willRename == 0)
        {
            AppendLog("");
            AppendLog("ℹ️ Переименовывать нечего (нет подходящих файлов или все конфликтуют)");
            return;
        }

        var confirmed = await new ConfirmDialog(
            $"Будет переименовано файлов: {willRename}.\n\nФайлы не удаляются, только переименовываются. Продолжить?",
            confirmText: "Переименовать",
            cancelText:  "Отмена").ShowDialog<bool>(this);

        if (!confirmed)
        {
            AppendLog("");
            AppendLog("⏹️ Операция отменена");
            return;
        }

        AppendLog("");
        AppendLog("🚀 Переименование…");
        await RunAsync(folder, dryRun: false);
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
    }

    // ── Прогон в фоне ─────────────────────────────────────────────────────────

    private async Task RunAsync(string folder, bool dryRun)
    {
        SetRunning(true);
        Bar.Value = 0;
        PercentText.Text = "0%";

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Progress<T> создан на UI-потоке → колбэки приходят на UI-поток.
        var progress = new Progress<RenameProgress>(p =>
        {
            Bar.Maximum      = p.Total;
            Bar.Value        = p.Current;
            PercentText.Text = p.Total > 0 ? $"{p.Current * 100 / p.Total}%" : "0%";
            AppendLog(FormatLine(p.Item));
        });

        RenameSummary summary;
        try
        {
            summary = await Task.Run(() => PdfRenamer.Run(folder, dryRun, progress, ct), ct);
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Ошибка: {ex.Message}");
            SetRunning(false);
            return;
        }

        AppendLog("");
        if (summary.Cancelled)
            AppendLog("⏹️ Остановлено пользователем");
        else if (dryRun)
            AppendLog($"🔍 Проверка завершена. Будет переименовано: {summary.Planned} из {summary.Total} " +
                      $"(без изменений: {summary.Unchanged}, конфликтов: {summary.Conflicts})");
        else
            AppendLog($"📊 Готово. Переименовано: {summary.Renamed} из {summary.Total} " +
                      $"(без изменений: {summary.Unchanged}, конфликтов: {summary.Conflicts}, ошибок: {summary.Errors})");

        SetRunning(false);
    }

    // ── Вспомогательное ───────────────────────────────────────────────────────

    private bool TryGetReadyFolder(out string folder)
    {
        folder = FolderBox.Text?.Trim() ?? "";
        if (_running) return false;
        if (string.IsNullOrEmpty(folder)) { AppendLog("❌ Папка не выбрана"); return false; }
        if (!Directory.Exists(folder))    { AppendLog("❌ Папка не существует"); return false; }
        return true;
    }

    private static string FormatLine(RenameItem item) => item.Outcome switch
    {
        RenameOutcome.WillRename => $"🔍 {item.OldName} → {item.NewName}",
        RenameOutcome.Renamed    => $"✅ {item.OldName} → {item.NewName}",
        RenameOutcome.Unchanged  => $"⏭️ {item.OldName} ({item.Note})",
        RenameOutcome.Conflict   => $"⚠️ {item.OldName} → {item.NewName} ({item.Note})",
        RenameOutcome.Error      => $"❌ {item.OldName}: {item.Note}",
        _                        => item.OldName,
    };

    private void SetRunning(bool running)
    {
        _running = running;
        StopBtn.IsEnabled = running;
        UpdateActionState();
    }

    // Кнопки действий доступны, когда указана папка и прогон не идёт; «Обзор» — когда не идёт.
    private void UpdateActionState()
    {
        var hasFolder = !string.IsNullOrWhiteSpace(FolderBox.Text);
        BrowseBtn.IsEnabled  = !_running;
        PreviewBtn.IsEnabled = !_running && hasFolder;
        RunBtn.IsEnabled     = !_running && hasFolder;
    }

    private void ClearLog() => LogBox.Text = "";

    private void AppendLog(string line)
    {
        LogBox.Text = string.IsNullOrEmpty(LogBox.Text) ? line : $"{LogBox.Text}\n{line}";
        LogBox.CaretIndex = LogBox.Text.Length;   // прокрутка к последней строке
    }
}
