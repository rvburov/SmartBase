using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Serilog;
using SmartBase.Controls.Dialogs;

namespace SmartBase.Extensions.PdfRename;

/// <summary>
/// Окно расширения «Переименование PDF» (в основном стиле приложения): выбор папки,
/// «Проверить» (предпросмотр без изменений), «Запустить» (с подтверждением) и
/// «Остановить». Логика — в <see cref="PdfRenamer"/>.
///
/// Масштаб (тысячи файлов): план строится один раз и в фоне; сам прогон идет в
/// <see cref="Task.Run"/>, а UI обновляется не на каждый файл, а с троттлингом
/// (~20 раз/сек) — журнал копится в <see cref="StringBuilder"/> и рендерится пачкой,
/// логируются только проблемные файлы (успехи отражает счетчик/полоса), число строк
/// журнала ограничено. Так окно остается отзывчивым на десятках тысяч файлов.
/// </summary>
public partial class PdfRenameWindow : Window
{
    private const int MaxLogLines  = 1000;   // потолок строк журнала (итог всегда полный)
    private const int UiIntervalMs = 50;     // как часто обновлять UI во время прогона (~20/сек)

    private readonly StringBuilder _log = new();
    private readonly Stopwatch _throttle = new();

    private CancellationTokenSource? _cts;
    private bool _running;
    private int  _curr, _total;       // последний прогресс (обновляется на UI-потоке)
    private int  _shownLines;         // сколько строк уже выведено в журнал за прогон
    private bool _truncated;          // достигнут ли потолок строк

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
        await ReportFolderContents(folder.Path.LocalPath);
    }

    private void OnFolderTextChanged(object? sender, TextChangedEventArgs e) => UpdateActionState();

    // Кратко показать, что в папке: число PDF и первые имена (листинг — в фоне).
    private async Task ReportFolderContents(string folder)
    {
        ClearLog();
        LogLineNow($"📂 Папка: {folder}");

        if (!Directory.Exists(folder)) { LogLineNow("❌ Папка не существует"); return; }

        List<string> pdfs;
        try { pdfs = await Task.Run(() => PdfRenamer.ListPdfFiles(folder)); }
        catch (Exception ex) { Log.Warning(ex, "Переименование PDF: нет доступа к папке {Folder:l}", folder); LogLineNow($"❌ Нет доступа к папке: {ex.Message}"); return; }

        if (pdfs.Count == 0) { LogLineNow("⚠️ В папке нет PDF-файлов"); return; }

        LogLineNow($"📄 Найдено PDF-файлов: {pdfs.Count}");
        foreach (var name in pdfs.Take(5)) LogLineNow($"  • {name}");
        if (pdfs.Count > 5) LogLineNow($"  … и еще {pdfs.Count - 5}");
        LogLineNow("💡 Нажмите «Проверить» для предпросмотра изменений");
    }

    // ── Проверка / запуск ─────────────────────────────────────────────────────

    private async void OnPreviewClick(object? sender, RoutedEventArgs e) => await ExecuteAsync(dryRun: true);
    private async void OnRunClick(object? sender, RoutedEventArgs e)     => await ExecuteAsync(dryRun: false);

    private async Task ExecuteAsync(bool dryRun)
    {
        if (!TryGetReadyFolder(out var folder)) return;

        SetRunning(true);
        ResetRunState();
        var ct = _cts!.Token;

        // 1) План — один раз и в фоне (на сетевой шаре BuildPlan делает File.Exists на каждый файл).
        List<RenameItem> plan;
        try
        {
            plan = await Task.Run(() => PdfRenamer.BuildPlan(folder), ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Log.Warning(ex, "Переименование PDF: не удалось построить план для {Folder:l}", folder);
            LogLineNow($"❌ Ошибка: {ex.Message}");
            SetRunning(false);
            return;
        }

        // 2) Реальный запуск — подтверждение.
        if (!dryRun)
        {
            var willRename = plan.Count(p => p.Outcome == RenameOutcome.WillRename);
            if (willRename == 0)
            {
                LogLineNow("");
                LogLineNow("ℹ️ Переименовывать нечего (нет подходящих файлов или все конфликтуют)");
                SetRunning(false);
                return;
            }

            var confirmed = await new ConfirmDialog(
                $"Будет переименовано файлов: {willRename}.\n\nФайлы не удаляются, только переименовываются. Продолжить?",
                confirmText: "Переименовать",
                cancelText:  "Отмена").ShowDialog<bool>(this);

            if (!confirmed)
            {
                LogLineNow("");
                LogLineNow("⏹️ Операция отменена");
                SetRunning(false);
                return;
            }
        }

        LogLineNow("");
        LogLineNow(dryRun
            ? "🔍 Проверка — изменения НЕ применяются (показаны переименования и конфликты)"
            : "🚀 Переименование… (в журнале — только конфликты и ошибки)");

        // 3) Прогон в фоне; UI обновляется с троттлингом через OnProgress.
        _throttle.Restart();
        var progress = new Progress<RenameProgress>(OnProgress);

        RenameSummary summary;
        try
        {
            summary = await Task.Run(() => PdfRenamer.Process(folder, plan, dryRun, progress, ct), ct);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Log.Error(ex, "Переименование PDF: сбой при обработке папки {Folder:l}", folder);
            RenderLog();
            LogLineNow($"❌ Ошибка: {ex.Message}");
            SetRunning(false);
            return;
        }

        // Финальный сброс журнала и сводка (continuation идет после всех колбэков прогресса).
        UpdateBar();
        RenderLog();
        LogLineNow("");
        LogLineNow(SummaryLine(summary, dryRun));
        SetRunning(false);
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
    }

    // Колбэк прогресса (на UI-потоке): копит журнал, но рендерит/двигает полосу не чаще UiIntervalMs.
    private void OnProgress(RenameProgress p)
    {
        _curr  = p.Current;
        _total = p.Total;

        if (!_truncated && ShouldLog(p.Item.Outcome))
        {
            if (_shownLines < MaxLogLines) { LogLine(FormatLine(p.Item)); _shownLines++; }
            else { LogLine($"… показаны первые {MaxLogLines} строк (полный итог ниже)"); _truncated = true; }
        }

        if (_throttle.ElapsedMilliseconds >= UiIntervalMs)
        {
            UpdateBar();
            RenderLog();
            _throttle.Restart();
        }
    }

    // ── Журнал и прогресс ─────────────────────────────────────────────────────

    // На реальном прогоне успехи (Renamed) и пропуски (Unchanged) в журнал не пишем —
    // их отражают счетчик/полоса и итоговая сводка. Логируем только то, что требует внимания.
    private static bool ShouldLog(RenameOutcome o) =>
        o is RenameOutcome.WillRename or RenameOutcome.Conflict or RenameOutcome.Error;

    private static string FormatLine(RenameItem item) => item.Outcome switch
    {
        RenameOutcome.WillRename => $"🔍 {item.OldName} → {item.NewName}",
        RenameOutcome.Renamed    => $"✅ {item.OldName} → {item.NewName}",
        RenameOutcome.Unchanged  => $"⏭️ {item.OldName} ({item.Note})",
        RenameOutcome.Conflict   => $"⚠️ {item.OldName} → {item.NewName} ({item.Note})",
        RenameOutcome.Error      => $"❌ {item.OldName}: {item.Note}",
        _                        => item.OldName,
    };

    private static string SummaryLine(RenameSummary s, bool dryRun)
    {
        if (s.Cancelled)
            return $"⏹️ Остановлено. Успели переименовать: {s.Renamed} из {s.Total}";
        return dryRun
            ? $"🔍 Проверка завершена. Будет переименовано: {s.Planned} из {s.Total} " +
              $"(без изменений: {s.Unchanged}, конфликтов: {s.Conflicts})"
            : $"📊 Готово. Переименовано: {s.Renamed} из {s.Total} " +
              $"(без изменений: {s.Unchanged}, конфликтов: {s.Conflicts}, ошибок: {s.Errors})";
    }

    private void UpdateBar() =>
        Progress.SetProgress(_total > 0 ? (double)_curr / _total : 0);

    private void LogLine(string s)            // только в модель (дешево)
    {
        if (_log.Length > 0) _log.Append('\n');
        _log.Append(s);
    }

    private void RenderLog()                  // модель → TextBox (дорого; зовем с троттлингом)
    {
        LogBox.Text       = _log.ToString();
        LogBox.CaretIndex = LogBox.Text.Length;   // прокрутка к последней строке
    }

    private void LogLineNow(string s) { LogLine(s); RenderLog(); }

    private void ClearLog()
    {
        _log.Clear();
        _shownLines = 0;
        _truncated  = false;
        LogBox.Text = "";
    }

    // ── Состояние ─────────────────────────────────────────────────────────────

    private bool TryGetReadyFolder(out string folder)
    {
        folder = FolderBox.Text?.Trim() ?? "";
        if (_running) return false;
        if (string.IsNullOrEmpty(folder)) { LogLineNow("❌ Папка не выбрана"); return false; }
        if (!Directory.Exists(folder))    { LogLineNow("❌ Папка не существует"); return false; }
        return true;
    }

    private void ResetRunState()
    {
        _cts        = new CancellationTokenSource();
        _curr       = 0;
        _total      = 0;
        _shownLines = 0;
        _truncated  = false;
        Progress.SetProgress(0);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        StopBtn.IsEnabled = running;
        UpdateActionState();
    }

    // Кнопки действий доступны, когда указана папка и прогон не идет; «Обзор» — когда не идет.
    private void UpdateActionState()
    {
        var hasFolder = !string.IsNullOrWhiteSpace(FolderBox.Text);
        BrowseBtn.IsEnabled  = !_running;
        PreviewBtn.IsEnabled = !_running && hasFolder;
        RunBtn.IsEnabled     = !_running && hasFolder;
    }
}
