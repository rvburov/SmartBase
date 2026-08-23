using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Serilog;
using SmartBase.Controls.Dialogs;
using SmartBase.Utils;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Окно расширения «Сводная трудоемкость» (в основном стиле приложения): папка с расчетами и
/// рекурсия, файл-шаблон, папка результата, индикатор по фазам и журнал во всю оставшуюся
/// высоту. Логика — в <see cref="TimeCollectorEngine"/>; прогон идет в фоне
/// (<see cref="Task.Run"/>) и отменяется кнопкой «Остановить» либо закрытием окна.
///
/// Во время прогона журнал молчит: счетчики, текущий файл и оценка времени идут в индикатор,
/// а замечания по файлам выводятся в конце — отсортированными по имени (файлы читаются
/// четырьмя потоками, и порядок их обнаружения ничего не значит).
/// </summary>
public partial class TimeCollectorWindow : Window
{
    private static readonly FilePickerFileType TemplateType =
        new("Excel с макросами (*.xlsm)") { Patterns = ["*.xlsm"] };

    /// <summary>Сколько имен показывать в одной секции журнала (счетчик в заголовке — полный).</summary>
    private const int MaxIssueLines = 200;

    /// <summary>Как часто обновлять индикатор во время прогона (~20 раз/сек).</summary>
    private const int UiIntervalMs = 50;

    private readonly StringBuilder _log      = new();
    private readonly Stopwatch     _throttle = new();

    private CancellationTokenSource? _cts;
    private bool   _running;
    private string _phase = "";

    public TimeCollectorWindow()
    {
        InitializeComponent();

        // Пути — из настроек расширения (объявлены в TimeCollectorExtension, экран —
        // «Настройки → Расширения»): и расчеты, и шаблон лежат на файловом сервере, у каждой
        // машины своя буква диска. Существование проверяет движок при запуске.
        Restore(FolderBox,   TimeCollectorExtension.FolderKey);
        Restore(TemplateBox, TimeCollectorExtension.TemplateKey);
        Restore(OutputBox,   TimeCollectorExtension.OutputKey);

        UpdateState();
    }

    private static void Restore(TextBox box, string key)
    {
        var value = AppSettings.LoadExtensionSetting(TimeCollectorExtension.ExtensionId, key);
        if (value.Length == 0) return;
        box.Text = value;
        ToolTip.SetTip(box, value);
    }

    private static void Remember(string key, string value) =>
        AppSettings.SaveExtensionSetting(TimeCollectorExtension.ExtensionId, key, value);

    // ── Выбор путей ───────────────────────────────────────────────────────────

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Выберите папку с расчетами", FolderBox.Text);
        if (path is null) return;
        Assign(FolderBox, path, TimeCollectorExtension.FolderKey);
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Выберите папку для сохранения результата", OutputBox.Text);
        if (path is null) return;
        Assign(OutputBox, path, TimeCollectorExtension.OutputKey);
    }

    private async void OnBrowseTemplate(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Выберите файл-шаблон",
            AllowMultiple  = false,
            FileTypeFilter = [TemplateType],
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        if (path is null) return;
        Assign(TemplateBox, path, TimeCollectorExtension.TemplateKey);
    }

    private async Task<string?> PickFolder(string title, string? current)
    {
        IStorageFolder? start = null;
        var from = current?.Trim();
        if (!string.IsNullOrEmpty(from) && Directory.Exists(from))
            start = await StorageProvider.TryGetFolderFromPathAsync(from);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = title,
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    // Выбор запоминается: следующий запуск подставит путь сам (значение видно и правится
    // в разделе «Настройки → Расширения»).
    private void Assign(TextBox box, string path, string key)
    {
        box.Text = path;
        ToolTip.SetTip(box, path);
        Remember(key, path);
        UpdateState();
    }

    private void OnPathChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    // ── Прогон ────────────────────────────────────────────────────────────────

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_running || !Ready()) return;

        var folder    = FolderBox.Text!.Trim();
        var template  = TemplateBox.Text!.Trim();
        var output    = OutputBox.Text!.Trim();
        var recursive = RecursiveCheck.IsChecked == true;

        if (!Directory.Exists(folder))  { SetStatus("Папка с расчетами не существует", "BrushRed"); return; }
        if (!File.Exists(template))     { SetStatus("Файл-шаблон не найден", "BrushRed"); return; }
        if (!Directory.Exists(output))  { SetStatus("Папка для сохранения не существует", "BrushRed"); return; }

        SetRunning(true);
        ClearLog();
        LogLine($"📂 Папка: {folder}{(recursive ? "  (рекурсивно)" : "")}");
        LogLine($"📄 Шаблон: {template}");
        LogLine("🚀 Чтение… ход работы виден на индикаторе; замечания по файлам появятся здесь по окончании.");
        RenderLog();

        SetStatus("Сборка свода…", "BrushAccent");
        _phase = "";
        Progress.SetProgress(0, "Подготовка…");
        _throttle.Restart();

        _cts = new CancellationTokenSource();
        var ct       = _cts.Token;
        var progress = new Progress<CollectProgress>(OnProgress);

        try
        {
            var result = await Task.Run(
                () => TimeCollectorEngine.Run(folder, recursive, template, output, progress, ct), ct);

            Progress.SetProgress(1, "Готово");
            Progress.SetInfo("");
            AppendResult(result);
            SetStatus(SummaryLine(result), "BrushAccent");
            await OfferOpenFolder(result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            Progress.SetInfo("");
            LogLine("");
            LogLine("⏹️ Остановлено — файл результата не создан.");
            RenderLog();
            SetStatus("Отменено", "BrushSecondaryText");
        }
        catch (TimeCollectException ex)
        {
            Log.Warning(ex, "Сводная трудоемкость: не выполнено");
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Сводная трудоемкость: непредвиденная ошибка");
            Fail($"Ошибка: {ex.Message}");
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (!_running) return;
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        SetStatus("Останавливаю…", "BrushSecondaryText");
    }

    private void Fail(string message)
    {
        Progress.SetInfo("");
        LogLine("");
        LogLine($"❌ {message}");
        RenderLog();
        SetStatus(message, "BrushRed");
    }

    private async Task OfferOpenFolder(string path)
    {
        var open = await new ConfirmDialog(
            $"Файл создан:\n{path}\n\nОткрыть папку с результатом?",
            confirmText: "Открыть папку",
            cancelText:  "Закрыть").ShowDialog<bool>(this);

        if (!open) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { /* не критично, если открыть не удалось */ }
    }

    // ── Индикатор ─────────────────────────────────────────────────────────────

    // Смена фазы показывается сразу, движение внутри фазы — с троттлингом: на тысяче файлов
    // и десятках тысяч строк поток сообщений сам по себе тормозит диспетчер.
    private void OnProgress(CollectProgress p)
    {
        if (p.Phase == _phase && _throttle.ElapsedMilliseconds < UiIntervalMs) return;

        _phase = p.Phase;
        if (p.Fraction is { } fraction) Progress.SetProgress(fraction, p.Phase);
        else                            Progress.SetIndeterminate(p.Phase);
        Progress.SetInfo(p.Info);
        _throttle.Restart();
    }

    // ── Журнал ────────────────────────────────────────────────────────────────

    private void AppendResult(CollectResult result)
    {
        LogLine("");
        LogLine($"📊 Готово. Обработано файлов: {result.ProcessedFiles} из {result.TotalFiles}, " +
                $"собрано строк: {result.RowCount}");

        Section("⚠️ Открыты другим пользователем", result.Issues, FileStatus.OpenedByOther);
        Section("❌ Не открылись", result.Issues,
                FileStatus.Locked, FileStatus.NoAccess, FileStatus.Corrupted);
        Section("⏭️ Пропущены", result.Issues,
                FileStatus.NoSheet, FileStatus.BadName, FileStatus.NoRows);

        if (result.Issues.Count == 0)
        {
            LogLine("");
            LogLine("✅ Все файлы прочитаны без замечаний.");
        }

        LogLine("");
        LogLine($"💾 Результат: {result.OutputPath}");
        RenderLog();
    }

    private void Section(string title, IReadOnlyList<FileIssue> issues, params FileStatus[] statuses)
    {
        var wanted = statuses.ToHashSet();
        var listed = issues.Where(i => wanted.Contains(i.Status))
                           .OrderBy(i => i.FileName, StringComparer.CurrentCultureIgnoreCase)
                           .ToList();
        if (listed.Count == 0) return;

        LogLine("");
        LogLine($"{title} ({listed.Count}):");
        foreach (var issue in listed.Take(MaxIssueLines)) LogLine($"  • {issue.FileName} — {issue.Message}");
        if (listed.Count > MaxIssueLines) LogLine($"  … и еще {listed.Count - MaxIssueLines}");
    }

    private static string SummaryLine(CollectResult result)
    {
        var text = $"Готово: {Path.GetFileName(result.OutputPath)} — строк {result.RowCount}, " +
                   $"файлов {result.ProcessedFiles} из {result.TotalFiles}";
        return result.Issues.Count > 0
            ? $"{text}, с замечаниями {result.Issues.Count} (см. журнал)"
            : text;
    }

    private void LogLine(string text)          // только в модель (дешево)
    {
        if (_log.Length > 0) _log.Append('\n');
        _log.Append(text);
    }

    private void RenderLog()                   // модель → TextBox (дорого; зовем пачкой)
    {
        LogBox.Text       = _log.ToString();
        LogBox.CaretIndex = LogBox.Text.Length;   // прокрутка к последней строке
    }

    private void ClearLog()
    {
        _log.Clear();
        LogBox.Text = "";
    }

    // ── Состояние ─────────────────────────────────────────────────────────────

    private bool Ready() =>
        !string.IsNullOrWhiteSpace(FolderBox.Text) &&
        !string.IsNullOrWhiteSpace(TemplateBox.Text) &&
        !string.IsNullOrWhiteSpace(OutputBox.Text);

    private void UpdateState()
    {
        ExecuteBtn.IsEnabled = Ready() && !_running;
        if (_running) return;

        if (Ready()) { SetStatus("Все готово к сборке свода", "BrushAccent"); return; }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(FolderBox.Text))   missing.Add("папка с расчетами");
        if (string.IsNullOrWhiteSpace(TemplateBox.Text)) missing.Add("файл-шаблон");
        if (string.IsNullOrWhiteSpace(OutputBox.Text))   missing.Add("папка для сохранения");
        SetStatus($"Ожидание: {string.Join(", ", missing)}", "BrushSecondaryText");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        FolderBtn.IsEnabled      = !running;
        TemplateBtn.IsEnabled    = !running;
        OutputBtn.IsEnabled      = !running;
        FolderBox.IsEnabled      = !running;
        TemplateBox.IsEnabled    = !running;
        OutputBox.IsEnabled      = !running;
        RecursiveCheck.IsEnabled = !running;
        StopBtn.IsEnabled        = running;
        ExecuteBtn.IsEnabled     = !running && Ready();
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        if (this.TryFindResource(brushKey, out var value) && value is IBrush brush)
            StatusText.Foreground = brush;
    }

    // Отмена прогона при закрытии окна, чтобы фоновая задача не висела.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
