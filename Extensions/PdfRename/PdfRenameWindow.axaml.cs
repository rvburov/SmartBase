using System.Collections.Concurrent;
using System.Text;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Extensions.PdfRename.Ocr;
using SmartBase.Extensions.PdfRename.Recognition;
using SmartBase.Extensions.PdfRename.Rename;
using SmartBase.Utils;

namespace SmartBase.Extensions.PdfRename;

/// <summary>
/// Окно «Переименование PDF». Два режима:
/// <list type="bullet">
/// <item><b>По содержимому</b> — шаблоны с цветными рамками, сопоставление, Tesseract и
/// PaddleOCR (<see cref="OcrRenameRun"/>);</item>
/// <item><b>По имени</b> — прежнее правило «все до первого пробела» (<see cref="PdfRenamer"/>).</item>
/// </list>
/// Тяжелая работа идет в фоне; журнал и счетчики обновляются пачкой по таймеру
/// (<see cref="UiIntervalMs"/>), а не на каждый файл. Журнал — виртуализированный список:
/// полный результат прогона на тысячи файлов листается без задержек.
/// </summary>
public partial class PdfRenameWindow : Window
{
    private const int ModeContent = 0;
    private const int ModeName = 1;
    private const int UiIntervalMs = 200;

    private const string KeyMode = "mode";
    private const string KeyTemplates = "templates";
    private const string KeySource = "source";
    private const string KeyRecursive = "recursive";

    private readonly AvaloniaList<string> _lines = [];
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _restoring;

    // Ход прогона: пишут фоновые потоки (Interlocked), читает таймер.
    private int _found, _processed, _renamed, _already, _skipped;
    private string _phase = "";

    public PdfRenameWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _lines;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(UiIntervalMs), DispatcherPriority.Background, (_, _) => Flush());

        ModeCombo.ItemsSource = new[] { "По содержимому (шаблоны и OCR)", "По имени (до первого пробела)" };
        _restoring = true;
        var mode = AppSettings.LoadExtensionSetting(PdfRenameExtension.ExtensionId, KeyMode);
        ModeCombo.SelectedIndex = mode == "name" ? ModeName : ModeContent;
        TemplatesBox.Text = AppSettings.LoadExtensionSetting(PdfRenameExtension.ExtensionId, KeyTemplates);
        SourceBox.Text = AppSettings.LoadExtensionSetting(PdfRenameExtension.ExtensionId, KeySource);
        RecursiveCheck.IsChecked = AppSettings.LoadExtensionSetting(PdfRenameExtension.ExtensionId, KeyRecursive) == "1";
        _restoring = false;
        ApplyMode();
        // Закрыли окно посреди прогона — та же остановка, что и «Стоп»: переименованное
        // остается, остальное не трогается.
        Closing += (_, _) => _cts?.Cancel();
        Closed += (_, _) => _timer.Stop();
    }

    private bool ContentMode => ModeCombo.SelectedIndex != ModeName;

    private static void Remember(string key, string value) =>
        AppSettings.SaveExtensionSetting(PdfRenameExtension.ExtensionId, key, value);

    // ── Режим и поля ──────────────────────────────────────────────────────────

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoring) return;
        Remember(KeyMode, ContentMode ? "content" : "name");
        ApplyMode();
    }

    private void ApplyMode()
    {
        var content = ContentMode;
        TemplatesPanel.IsVisible = content;
        RecursiveCheck.IsVisible = content;
        PreviewBtn.IsVisible = !content;
        SourceTitle.Text = content ? "2. Папка с PDF" : "Папка с PDF";
        ActionsGrid.ColumnDefinitions = new ColumnDefinitions(content ? "0,0,*,10,*" : "*,10,*,10,*");
        if (!_running) StatusText.Text = "Готов к работе";
        UpdateActionState();
    }

    private void OnPathChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_restoring && sender is TextBox box)
            Remember(box == TemplatesBox ? KeyTemplates : KeySource, box.Text?.Trim() ?? "");
        UpdateActionState();
    }

    private void OnRecursiveChanged(object? sender, RoutedEventArgs e)
    {
        if (!_restoring) Remember(KeyRecursive, RecursiveCheck.IsChecked == true ? "1" : "0");
    }

    private async void OnBrowseTemplates(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Выберите папку с шаблонами", TemplatesBox.Text);
        if (path is not null) TemplatesBox.Text = path;
    }

    private async void OnBrowseSource(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Выберите папку с PDF-файлами", SourceBox.Text);
        if (path is null) return;
        SourceBox.Text = path;
        if (!ContentMode) await ReportFolderContents(path);
    }

    private async Task<string?> PickFolder(string title, string? current)
    {
        IStorageFolder? start = null;
        current = current?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            start = await StorageProvider.TryGetFolderFromPathAsync(current);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    // ── Запуск ────────────────────────────────────────────────────────────────

    private async void OnRunClick(object? sender, RoutedEventArgs e)
    {
        if (ContentMode) await RunContentAsync();
        else await RunByNameAsync(dryRun: false);
    }

    private async void OnPreviewClick(object? sender, RoutedEventArgs e) => await RunByNameAsync(dryRun: true);

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        _phase = "Останавливается… файлы в работе дочитываются и не переименовываются";
    }

    /// <summary>Режим «по содержимому».</summary>
    private async Task RunContentAsync()
    {
        var templates = TemplatesBox.Text?.Trim() ?? "";
        var source = SourceBox.Text?.Trim() ?? "";
        if (_running) return;
        ClearLog();
        if (!Directory.Exists(templates)) { Log("❌ Папка шаблонов не выбрана или не существует"); Flush(); return; }
        if (!Directory.Exists(source)) { Log("❌ Папка с PDF не выбрана или не существует"); Flush(); return; }

        var recursive = RecursiveCheck.IsChecked == true;
        var confirmed = await new ConfirmDialog(
            "Файлы в папке с PDF" + (recursive ? " и ее подпапках" : "") + " будут переименованы на месте " +
            "по распознанным обозначению и наименованию.\n\nСодержимое файлов не меняется, ничего не удаляется; " +
            "сомнительные и конфликтующие файлы пропускаются. Продолжить?",
            confirmText: "Запустить",
            cancelText: "Отмена").ShowDialog<bool>(this);
        if (!confirmed) return;

        StartRun();
        Log($"📁 Шаблоны: {templates}");
        Log($"📂 Папка с PDF: {source}{(recursive ? "  (с подпапками)" : "")}");
        _phase = "Проверка OCR-компонентов…";
        var ct = _cts!.Token;

        RunSummary? summary = null;
        try
        {
            summary = await Task.Run(() =>
            {
                var problems = OcrComponents.CheckReady();
                if (problems.Count > 0)
                {
                    Log("❌ Распознавание недоступно — проверка двумя движками невозможна:");
                    foreach (var p in problems) Log("   • " + p);
                    return null;
                }
                Log($"🔤 OCR: Tesseract {TesseractEngine.Version()} (rus+eng) и PaddleOCR v5 — оба готовы");
                var tessdata = OcrComponents.FindTessdata()!;
                _phase = "Чтение шаблонов…";
                var settings = new RunSettings(templates, source, recursive);
                Log($"⚙️ Потоков распознавания: {settings.EffectiveWorkers}");
                return OcrRenameRun.Execute(settings, () => OcrPair.CreateReal(tessdata), new Sink(this), ct);
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"❌ Ошибка: {ex.Message}");
        }

        if (summary is { Fatal: null })
        {
            Log("");
            Log(summary.Stopped
                ? $"⏹️ Остановлено пользователем. Переименовано: {summary.Renamed}, пропущено: {summary.Skipped}" +
                  (summary.AlreadyNamed > 0 ? $", уже с верным именем: {summary.AlreadyNamed}" : "") +
                  $"; не обработано (остались без изменений): {summary.NotProcessed}"
                : $"📊 Завершено. Найдено: {summary.Found}, переименовано: {summary.Renamed}, пропущено: {summary.Skipped}" +
                  (summary.AlreadyNamed > 0 ? $", уже с верным именем: {summary.AlreadyNamed}" : ""));
        }
        _phase = summary is null || summary.Fatal is not null ? "Не выполнено — причина в журнале"
            : summary.Stopped ? "Остановлено пользователем" : "Завершено";
        StopRun();
    }

    /// <summary>Получатель хода прогона: копит строки и счетчики для таймера окна.</summary>
    private sealed class Sink(PdfRenameWindow w) : IRunSink
    {
        public void Message(string text) => w.Log("ℹ️ " + text);

        public void Found(int count)
        {
            Volatile.Write(ref w._found, count);
            w._phase = "Распознавание и переименование…";
            w.Log($"📄 Найдено PDF-файлов: {count}");
        }

        public void File(JournalEntry entry)
        {
            Interlocked.Increment(ref w._processed);
            switch (entry.Status)
            {
                case FileStatus.Renamed: Interlocked.Increment(ref w._renamed); break;
                case FileStatus.AlreadyNamed: Interlocked.Increment(ref w._already); break;
                default: Interlocked.Increment(ref w._skipped); break;
            }
            w.Log(Format(entry));
        }
    }

    internal static string Format(JournalEntry e) => e.Status switch
    {
        FileStatus.Renamed => $"✅ {e.RelativePath} → {e.NewName}" + (e.Template is null ? "" : $"  (шаблон «{e.Template}»)"),
        FileStatus.AlreadyNamed => $"☑️ {e.RelativePath} — {e.Reason}",
        FileStatus.Error => $"❌ {e.RelativePath} — {e.Reason}",
        _ => $"⏭️ {e.RelativePath} — {e.Reason}",
    };

    /// <summary>Режим «по имени» (прежнее правило), с предпросмотром.</summary>
    private async Task RunByNameAsync(bool dryRun)
    {
        var folder = SourceBox.Text?.Trim() ?? "";
        if (_running) return;
        if (!Directory.Exists(folder)) { ClearLog(); Log("❌ Папка не выбрана или не существует"); Flush(); return; }

        List<RenameItem> plan;
        try { plan = await Task.Run(() => PdfRenamer.BuildPlan(folder)); }
        catch (Exception ex) { Log($"❌ Ошибка: {ex.Message}"); Flush(); return; }

        if (!dryRun)
        {
            var willRename = plan.Count(p => p.Outcome == RenameOutcome.WillRename);
            if (willRename == 0)
            {
                Log("ℹ️ Переименовывать нечего (нет подходящих файлов или все конфликтуют)");
                Flush();
                return;
            }
            var confirmed = await new ConfirmDialog(
                $"Будет переименовано файлов: {willRename}.\n\nФайлы не удаляются, только переименовываются. Продолжить?",
                confirmText: "Переименовать",
                cancelText: "Отмена").ShowDialog<bool>(this);
            if (!confirmed) return;
        }

        StartRun();
        Log($"📂 Папка: {folder}");
        Log(dryRun
            ? "🔍 Проверка — изменения НЕ применяются (показаны переименования и конфликты)"
            : "🚀 Переименование… (в журнале — только конфликты и ошибки)");
        _found = plan.Count;
        _phase = dryRun ? "Проверка…" : "Переименование…";
        var ct = _cts!.Token;
        var progress = new Progress<RenameProgress>(p =>
        {
            _processed = p.Current;
            if (p.Item.Outcome is RenameOutcome.WillRename or RenameOutcome.Conflict or RenameOutcome.Error)
                Log(FormatByName(p.Item));
        });

        try
        {
            var s = await Task.Run(() => PdfRenamer.Process(folder, plan, dryRun, progress, ct), ct);
            _renamed = s.Renamed;
            _skipped = s.Conflicts + s.Errors;
            Log("");
            Log(s.Cancelled
                ? $"⏹️ Остановлено. Успели переименовать: {s.Renamed} из {s.Total}"
                : dryRun
                    ? $"🔍 Проверка завершена. Будет переименовано: {s.Planned} из {s.Total} (без изменений: {s.Unchanged}, конфликтов: {s.Conflicts})"
                    : $"📊 Готово. Переименовано: {s.Renamed} из {s.Total} (без изменений: {s.Unchanged}, конфликтов: {s.Conflicts}, ошибок: {s.Errors})");
            _phase = s.Cancelled ? "Остановлено пользователем" : "Завершено";
        }
        catch (Exception ex)
        {
            Log($"❌ Ошибка: {ex.Message}");
            _phase = "Не выполнено — причина в журнале";
        }
        StopRun();
    }

    private static string FormatByName(RenameItem item) => item.Outcome switch
    {
        RenameOutcome.WillRename => $"🔍 {item.OldName} → {item.NewName}",
        RenameOutcome.Renamed => $"✅ {item.OldName} → {item.NewName}",
        RenameOutcome.Conflict => $"⚠️ {item.OldName} → {item.NewName} ({item.Note})",
        RenameOutcome.Error => $"❌ {item.OldName}: {item.Note}",
        _ => $"⏭️ {item.OldName} ({item.Note})",
    };

    private async Task ReportFolderContents(string folder)
    {
        ClearLog();
        Log($"📂 Папка: {folder}");
        try
        {
            var pdfs = await Task.Run(() => PdfRenamer.ListPdfFiles(folder));
            Log(pdfs.Count == 0 ? "⚠️ В папке нет PDF-файлов" : $"📄 Найдено PDF-файлов: {pdfs.Count}");
            if (pdfs.Count > 0) Log("💡 Нажмите «Проверить» для предпросмотра изменений");
        }
        catch (Exception ex) { Log($"❌ Нет доступа к папке: {ex.Message}"); }
        Flush();
    }

    // ── Журнал и состояние ────────────────────────────────────────────────────

    private void Log(string line) => _pending.Enqueue(line);

    private void ClearLog()
    {
        while (_pending.TryDequeue(out _)) { }
        _lines.Clear();
        SaveLogBtn.IsEnabled = false;
    }

    /// <summary>Перенести накопленные строки в список и обновить счетчики (UI-поток).</summary>
    private void Flush()
    {
        if (!_pending.IsEmpty)
        {
            var batch = new List<string>();
            while (_pending.TryDequeue(out var line)) batch.Add(line);
            _lines.AddRange(batch);
            if (_lines.Count > 0) LogList.ScrollIntoView(_lines.Count - 1);
        }
        SaveLogBtn.IsEnabled = !_running && _lines.Count > 0;

        int found = Volatile.Read(ref _found), processed = Volatile.Read(ref _processed);
        if (_running || processed > 0)
        {
            var counters = $"Найдено: {found} · Обработано: {processed} · Переименовано: {Volatile.Read(ref _renamed)} · " +
                           $"Пропущено: {Volatile.Read(ref _skipped)}" +
                           (_already > 0 ? $" · Уже с верным именем: {_already}" : "");
            StatusText.Text = _phase.Length > 0 ? $"{_phase}\n{counters}" : counters;
            Progress.SetProgress(found > 0 ? (double)processed / found : 0);
        }
    }

    private void StartRun()
    {
        _cts = new CancellationTokenSource();
        _found = _processed = _renamed = _already = _skipped = 0;
        _phase = "";
        Progress.SetProgress(0);
        _running = true;
        StopBtn.IsEnabled = true;
        UpdateActionState();
        _timer.Start();
    }

    private void StopRun()
    {
        _running = false;
        _timer.Stop();
        Flush();
        StopBtn.IsEnabled = false;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        var hasSource = !string.IsNullOrWhiteSpace(SourceBox.Text);
        var ready = hasSource && (!ContentMode || !string.IsNullOrWhiteSpace(TemplatesBox.Text));
        ModeCombo.IsEnabled = !_running;
        TemplatesBtn.IsEnabled = SourceBtn.IsEnabled = !_running;
        TemplatesBox.IsEnabled = SourceBox.IsEnabled = RecursiveCheck.IsEnabled = !_running;
        PreviewBtn.IsEnabled = !_running && hasSource;
        RunBtn.IsEnabled = !_running && ready;
    }

    // ── Копирование и сохранение журнала ──────────────────────────────────────

    private async void OnLogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        var indexes = LogList.Selection.SelectedIndexes.Where(i => i >= 0 && i < _lines.Count).Order().ToList();
        if (indexes.Count == 0 || Clipboard is null) return;
        await Clipboard.SetTextAsync(string.Join(Environment.NewLine, indexes.Select(i => _lines[i])));
        e.Handled = true;
    }

    private async void OnSaveLog(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить журнал",
            SuggestedFileName = $"Журнал переименования {DateTime.Now:yyyy-MM-dd HH-mm}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Текст") { Patterns = ["*.txt"] }],
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
            foreach (var line in _lines) await writer.WriteLineAsync(line);
        }
        catch (Exception ex)
        {
            Log($"❌ Журнал не сохранен: {ex.Message}");
            Flush();
        }
    }
}
