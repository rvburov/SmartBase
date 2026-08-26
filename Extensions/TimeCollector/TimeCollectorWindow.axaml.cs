using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Serilog;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Extensions.TimeCollector;

/// <summary>
/// Окно расширения «Сводная трудоемкость» (в основном стиле приложения): папка с расчетами и
/// рекурсия, приемник свода, индикатор по фазам и журнал во всю оставшуюся высоту.
///
/// Приемников два, выбор — верхним списком: <b>файл Excel</b> (шаблон .xlsm + папка результата,
/// порт макроса) и <b>коллекция программы</b> (база из реестра + название коллекции). Блоки 2 и 3
/// принадлежат выбранному приемнику и переключаются вместе с ним; общая часть — чтение расчетов.
/// Логика — в <see cref="TimeCollectorEngine"/> (чтение и сборка свода),
/// <see cref="TimeSheetWriter"/> (Excel) и <see cref="TimeCollectionWriter"/> (коллекция);
/// прогон идет в фоне (<see cref="Task.Run"/>) и отменяется кнопкой «Остановить» либо
/// закрытием окна.
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

    /// <summary>Пункты списка приемников (индекс пункта = значение <see cref="ToCollection"/>).</summary>
    private const int ModeExcel      = 0;
    private const int ModeCollection = 1;

    // Границы фаз выгрузки В КОЛЛЕКЦИЮ на шкале прогресса: чтение расчетов, запись записей,
    // остаток — раскраска. У выгрузки в Excel свои (см. TimeCollectorEngine).
    private const double CollectionReadTo  = 0.70;
    private const double CollectionWriteTo = 0.95;

    private readonly StringBuilder _log      = new();
    private readonly Stopwatch     _throttle = new();

    // Базы реестра в порядке пунктов DbCombo (индекс пункта → DbId).
    private readonly IReadOnlyList<(int DbId, string Name)> _databases;

    private CancellationTokenSource? _cts;
    private bool   _running;
    private string _phase = "";

    // Приемник выгрузки в программу на время прогона: каркас коллекции создается посреди
    // прогона, а убирать его при отмене и ошибке — общему обработчику (см. Rollback).
    private ConstructorRepository? _repo;
    private TimeShell?             _shell;

    public TimeCollectorWindow()
    {
        InitializeComponent();

        // Доступ к базам — через ту же точку, что и межбазовые связи: расширению не нужно
        // знать ни о Workspace, ни о главном окне (тот же прием, что в «Документах в коллекцию»).
        // Список заполняется ДО выбора приемника: смена приемника тут же спрашивает про базы.
        _databases = RelationDbResolver.Databases();
        DbCombo.ItemsSource = _databases.Select(d => d.Name).ToList();
        if (_databases.Count > 0) DbCombo.SelectedIndex = 0;
        DbCombo.IsEnabled = _databases.Count > 1;   // выбирать не из чего — список заблокирован

        ModeCombo.ItemsSource   = new[] { "Файл Excel по шаблону", "Коллекция в программе" };
        ModeCombo.SelectedIndex = ModeExcel;

        // Пути — из настроек расширения (объявлены в TimeCollectorExtension, экран —
        // «Настройки → Расширения»): и расчеты, и шаблон лежат на файловом сервере, у каждой
        // машины своя буква диска. Существование проверяет движок при запуске.
        Restore(FolderBox,   TimeCollectorExtension.FolderKey);
        Restore(TemplateBox, TimeCollectorExtension.TemplateKey);
        Restore(OutputBox,   TimeCollectorExtension.OutputKey);

        ApplyMode();
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

    /// <summary>Выбран приемник «коллекция в программе».</summary>
    private bool ToCollection => ModeCombo.SelectedIndex == ModeCollection;

    // ── Приемник ──────────────────────────────────────────────────────────────

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;   // событие прилетает и во время построения окна
        ApplyMode();
    }

    // Блоки 2 и 3 принадлежат приемнику: у файла это шаблон и папка результата, у коллекции —
    // база и название. Видна всегда одна пара, поэтому нумерация подписей совпадает.
    private void ApplyMode()
    {
        var toCollection = ToCollection;
        TemplatePanel.IsVisible = !toCollection;
        OutputPanel.IsVisible   = !toCollection;
        DbPanel.IsVisible       = toCollection;
        NamePanel.IsVisible     = toCollection;
        UpdateState();
    }

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
        var recursive = RecursiveCheck.IsChecked == true;
        if (!Directory.Exists(folder)) { SetStatus("Папка с расчетами не существует", "BrushRed"); return; }

        _repo  = null;
        _shell = null;

        if (ToCollection)
        {
            if (DbCombo.SelectedIndex < 0 || DbCombo.SelectedIndex >= _databases.Count)
            {
                SetStatus("Нет доступной базы данных", "BrushRed");
                return;
            }
            _repo = RelationDbResolver.RepoFor(_databases[DbCombo.SelectedIndex].DbId);
            if (_repo is null) { SetStatus("База данных недоступна", "BrushRed"); return; }
        }
        else
        {
            if (!File.Exists(TemplateBox.Text!.Trim())) { SetStatus("Файл-шаблон не найден", "BrushRed"); return; }
            if (!Directory.Exists(OutputBox.Text!.Trim())) { SetStatus("Папка для сохранения не существует", "BrushRed"); return; }
        }

        SetRunning(true);
        ClearLog();
        LogLine($"📂 Папка: {folder}{(recursive ? "  (рекурсивно)" : "")}");
        LogLine(ToCollection
            ? $"🗂️ База: {_databases[DbCombo.SelectedIndex].Name}"
            : $"📄 Шаблон: {TemplateBox.Text!.Trim()}");
        LogLine("🚀 Чтение… ход работы виден на индикаторе; замечания по файлам появятся здесь по окончании.");
        RenderLog();

        SetStatus("Сборка свода…", "BrushAccent");
        _phase = "";
        Progress.SetProgress(0, "Подготовка…");
        _throttle.Restart();

        _cts = new CancellationTokenSource();

        try
        {
            if (ToCollection) await RunToCollection(folder, recursive);
            else              await RunToExcel(folder, recursive);
        }
        catch (OperationCanceledException)
        {
            var removed = Rollback();
            Progress.SetInfo("");
            LogLine("");
            LogLine(CancelNote(removed));
            RenderLog();
            SetStatus("Отменено", "BrushSecondaryText");
        }
        catch (PermissionDeniedException ex)
        {
            Rollback();
            Fail(ex.Message);
        }
        catch (TimeCollectException ex)
        {
            Rollback();
            Log.Warning(ex, "Сводная трудоемкость: не выполнено");
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Rollback();
            Log.Error(ex, "Сводная трудоемкость: непредвиденная ошибка");
            Fail($"Ошибка: {ex.Message}");
        }
        finally
        {
            SetRunning(false);
        }
    }

    /// <summary>Сборка свода и запись в .xlsm-шаблон — порт макроса целиком.</summary>
    private async Task RunToExcel(string folder, bool recursive)
    {
        var ct       = _cts!.Token;
        var progress = new Progress<CollectProgress>(OnProgress);
        var template = TemplateBox.Text!.Trim();
        var output   = OutputBox.Text!.Trim();

        var result = await Task.Run(
            () => TimeCollectorEngine.Run(folder, recursive, template, output, progress, ct), ct);

        Progress.SetProgress(1, "Готово");
        Progress.SetInfo("");

        AppendIssues(result.ProcessedFiles, result.TotalFiles, result.RowCount, result.Issues);
        LogLine("");
        LogLine($"💾 Результат: {result.OutputPath}");
        RenderLog();

        SetStatus(SummaryLine(result), "BrushAccent");
        await OfferOpenFolder(result.OutputPath);
    }

    /// <summary>
    /// Сборка свода и запись его коллекцией программы. Три шага разнесены так же, как в
    /// «Документах в коллекцию»: диск — в фоне, каркас коллекции — на общем репозитории
    /// (UI-поток, там же проверка прав), записи и раскраска — на отдельном подключении в фоне.
    /// </summary>
    private async Task RunToCollection(string folder, bool recursive)
    {
        var ct       = _cts!.Token;
        var progress = new Progress<CollectProgress>(OnProgress);
        var repo     = _repo!;

        var table = await Task.Run(
            () => TimeCollectorEngine.Collect(folder, recursive, progress, ct, CollectionReadTo), ct);

        ct.ThrowIfCancellationRequested();
        SetStatus($"Создаю коллекцию ({table.Rows.Count} строк)…", "BrushAccent");
        Progress.SetIndeterminate("Создаю коллекцию…");

        var typed = NameBox.Text?.Trim() ?? "";
        var shell = TimeCollectionWriter.CreateShell(
            repo, typed.Length > 0 ? typed : TimeCollectionWriter.DefaultCollectionName());
        _shell = shell;

        var dbPath  = repo.DbPath;
        var rows    = table.Rows;
        var paints  = table.Paints;
        var created = await Task.Run(
            () => TimeCollectionWriter.FillRecords(dbPath, shell, rows, paints, progress, ct,
                                                   CollectionReadTo, CollectionWriteTo), ct);

        // Коллекция собрана целиком — откатывать больше нечего.
        _shell = null;

        Progress.SetProgress(1, "Готово");
        Progress.SetInfo("");
        RefreshSidebars();

        AppendIssues(table.ProcessedFiles, table.TotalFiles, rows.Count, table.Issues);
        LogLine("");
        LogLine($"🗂️ Коллекция «{shell.CollectionName}» — записей: {created}");
        RenderLog();

        SetStatus($"Готово: коллекция «{shell.CollectionName}» — записей {created}, " +
                  $"файлов {table.ProcessedFiles} из {table.TotalFiles}", "BrushAccent");

        await ConfirmDialog
            .Info($"Коллекция «{shell.CollectionName}» создана.\nЗаписей: {created}.")
            .ShowDialog<bool>(this);
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (!_running) return;
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
        SetStatus("Останавливаю…", "BrushSecondaryText");
    }

    // Чем кончилась отмена: файл пишется в самом конце, коллекция — по записи, поэтому
    // недособранный каркас удаляется (removed), а если каркаса еще не было — создавать нечего.
    private string CancelNote(bool removed) => ToCollection
        ? (removed ? "⏹️ Остановлено — недособранная коллекция удалена."
                   : "⏹️ Остановлено — коллекция не создана.")
        : "⏹️ Остановлено — файл результата не создан.";

    // Полупустая коллекция после отмены или ошибки не нужна — убираем и обновляем сайдбар.
    // true — каркас был и удален (у выгрузки в Excel откатывать нечего: файл пишется в конце).
    private bool Rollback()
    {
        if (_repo is not { } repo || _shell is not { } shell) return false;
        _shell = null;
        TimeCollectionWriter.TryDelete(repo, shell.CollectionId);
        RefreshSidebars();
        return true;
    }

    // Список коллекций в сайдбаре строится главным окном; события «коллекции изменились»
    // расширению недоступны, поэтому просто просим перестроиться все деревья окна-владельца.
    // Не нашли — не беда: коллекция появится при следующем обновлении списка.
    private void RefreshSidebars()
    {
        try
        {
            if (Owner is not Visual owner) return;
            foreach (var tree in owner.GetVisualDescendants().OfType<SidebarTree>())
                tree.Refresh();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Сводная трудоемкость: не удалось обновить сайдбар");
        }
    }

    private void Fail(string message)
    {
        Progress.SetInfo("");
        LogLine("");
        LogLine($"❌ {message}");
        RenderLog();

        // В статус — только первая строка: подробности (пути, что проверить) уже целиком
        // в журнале, а узкая полоска статуса на многострочном тексте расползается.
        SetStatus(message.Split('\n')[0], "BrushRed");
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

    // Общая часть отчета обоих приемников: счетчики прочитанного и замечания по файлам.
    // Чем кончился прогон (файл или коллекция) — дописывает вызывающий.
    private void AppendIssues(int processed, int total, int rowCount, IReadOnlyList<FileIssue> issues)
    {
        LogLine("");
        LogLine($"📊 Готово. Обработано файлов: {processed} из {total}, собрано строк: {rowCount}");

        Section("⚠️ Открыты другим пользователем", issues, FileStatus.OpenedByOther);
        StaleNote(issues);
        Section("❌ Не открылись", issues,
                FileStatus.Locked, FileStatus.NoAccess, FileStatus.Corrupted);
        Section("⏭️ Пропущены", issues,
                FileStatus.NoSheet, FileStatus.BadName, FileStatus.NoRows);

        if (!issues.Any(i => i.IsProblem()))
        {
            LogLine("");
            LogLine("✅ Все файлы прочитаны без замечаний.");
        }
    }

    /// <summary>
    /// Осиротевшие «~$» — одной строкой со счетчиком, без перечисления имен. Данные таких
    /// файлов полны и свежи (книга не открыта, см. <c>CalcFileReader</c>), а список из полусотни
    /// имен только прятал бы настоящие замечания — ради этого их и вынесли из общей секции.
    /// </summary>
    private void StaleNote(IReadOnlyList<FileIssue> issues)
    {
        var count = issues.Count(i => i.Status == FileStatus.StaleLock);
        if (count == 0) return;

        LogLine("");
        LogLine($"ℹ️ Остались служебные файлы «~$» от прошлых сеансов: {count}. " +
                "На данные не влияют: книги не открыты, строки собраны полностью. " +
                "Файлы скрытые, их можно удалить.");
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
        var problems = result.Issues.Count(i => i.IsProblem());
        return problems > 0 ? $"{text}, с замечаниями {problems} (см. журнал)" : text;
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
        (ToCollection
            ? _databases.Count > 0
            : !string.IsNullOrWhiteSpace(TemplateBox.Text) && !string.IsNullOrWhiteSpace(OutputBox.Text));

    private void UpdateState()
    {
        ExecuteBtn.IsEnabled = Ready() && !_running;
        if (_running) return;

        if (Ready())
        {
            SetStatus(ToCollection ? "Все готово к сборке коллекции" : "Все готово к сборке свода",
                      "BrushAccent");
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(FolderBox.Text)) missing.Add("папка с расчетами");
        if (ToCollection)
        {
            if (_databases.Count == 0) missing.Add("доступная база данных");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(TemplateBox.Text)) missing.Add("файл-шаблон");
            if (string.IsNullOrWhiteSpace(OutputBox.Text))   missing.Add("папка для сохранения");
        }
        SetStatus($"Ожидание: {string.Join(", ", missing)}", "BrushSecondaryText");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        ModeCombo.IsEnabled      = !running;
        FolderBtn.IsEnabled      = !running;
        TemplateBtn.IsEnabled    = !running;
        OutputBtn.IsEnabled      = !running;
        FolderBox.IsEnabled      = !running;
        TemplateBox.IsEnabled    = !running;
        OutputBox.IsEnabled      = !running;
        DbCombo.IsEnabled        = !running && _databases.Count > 1;
        NameBox.IsEnabled        = !running;
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
