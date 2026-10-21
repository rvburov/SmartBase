using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Utils;

namespace SmartBase.Extensions.ProductCompare;

/// <summary>
/// Окно расширения «Сравнение составов изделий» (в основном стиле приложения). Два режима:
/// «заключение» — шаблон, старый и новый файлы, папка результата
/// (<see cref="ProductCompareEngine"/>); «расшифровка» — свой шаблон, папка с расшифровками и
/// папка результата (<see cref="BreakdownCompareEngine"/>), с журналом внизу окна. Прогон идет
/// в фоне (<see cref="Task.Run"/>) с прогрессом и отменой при закрытии окна.
/// </summary>
public partial class ProductCompareWindow : Window
{
    private static readonly FilePickerFileType ExcelType =
        new("Excel (*.xlsx, *.xlsm, *.xls)") { Patterns = ["*.xlsx", "*.xlsm", "*.xls"] };

    /// <summary>Пункты списка режимов (индекс пункта = режим).</summary>
    private const int ModeConclusion = 0;
    private const int ModeBreakdown  = 1;

    /// <summary>Значение памяти о режиме для «расшифровки» (пусто — «заключение»).</summary>
    private const string BreakdownModeValue = "breakdown";

    private string? _template, _breakdownTemplate, _old, _new, _folder, _output;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public ProductCompareWindow()
    {
        InitializeComponent();

        // Пути — из настроек расширения (объявлены в ProductCompareExtension, экран —
        // «Настройки → Расширения»): у каждой машины свой смонтированный диск, поэтому в коде
        // путей нет. Не задан — поле пустое, файл выбирают «Обзором». Доступность проверяется
        // при запуске сравнения: недоступен шаблон — движок вернет «Файл-шаблон не найден».
        _template          = Load(ProductCompareExtension.TemplateKey);
        _breakdownTemplate = Load(ProductCompareExtension.BreakdownTemplateKey);
        _folder            = Load(ProductCompareExtension.BreakdownFolderKey);
        Display(FolderBox, _folder);

        ModeCombo.ItemsSource   = new[] { "Сравнение составов изделий заключение",
                                          "Сравнение составов изделий расшифровка" };
        ModeCombo.SelectedIndex = Load(ProductCompareExtension.ModeKey) == BreakdownModeValue
            ? ModeBreakdown : ModeConclusion;

        ApplyMode();
    }

    private static string? Load(string key)
    {
        var value = AppSettings.LoadExtensionSetting(ProductCompareExtension.ExtensionId, key);
        return value.Length > 0 ? value : null;
    }

    private static void Remember(string key, string value) =>
        AppSettings.SaveExtensionSetting(ProductCompareExtension.ExtensionId, key, value);

    private static void Display(TextBox box, string? path)
    {
        box.Text = path ?? "";
        ToolTip.SetTip(box, string.IsNullOrEmpty(path) ? null : path);
    }

    /// <summary>Выбран режим «расшифровка».</summary>
    private bool Breakdown => ModeCombo.SelectedIndex == ModeBreakdown;

    /// <summary>Шаблон выбранного режима.</summary>
    private string? ModeTemplate => Breakdown ? _breakdownTemplate : _template;

    // ── Режим ─────────────────────────────────────────────────────────────────

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;   // событие прилетает и во время построения окна
        Remember(ProductCompareExtension.ModeKey, Breakdown ? BreakdownModeValue : "");
        ApplyMode();
    }

    // Блоки 2–3 и журнал принадлежат режиму; у каждого режима свой шаблон в блоке 1, поэтому
    // поле шаблона перезаполняется. Нумерация подписей идет подряд в обоих режимах.
    private void ApplyMode()
    {
        var breakdown = Breakdown;
        TemplateLabel.Text    = breakdown ? "1. Файл-шаблон расшифровки (.xlsm)" : "1. Файл-шаблон заключения (.xlsm)";
        OldPanel.IsVisible    = !breakdown;
        NewPanel.IsVisible    = !breakdown;
        FolderPanel.IsVisible = breakdown;
        LogPanel.IsVisible    = breakdown;
        OutputLabel.Text      = breakdown ? "3. Папка для сохранения результата" : "4. Папка для сохранения результата";
        Display(TemplateBox, ModeTemplate);
        UpdateState();
    }

    // ── Выбор файлов / папки ──────────────────────────────────────────────────

    private async void OnBrowseTemplate(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Выберите файл-шаблон");
        if (path is null) return;
        if (Breakdown) _breakdownTemplate = path; else _template = path;
        Display(TemplateBox, path);
        // Запоминаем выбор: следующий запуск подставит шаблон сам.
        Remember(Breakdown ? ProductCompareExtension.BreakdownTemplateKey : ProductCompareExtension.TemplateKey, path);
        UpdateState();
    }

    private async void OnBrowseOld(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Выберите старый файл");
        if (path is null) return;
        _old = path; OldBox.Text = path; ToolTip.SetTip(OldBox, path);
        UpdateState();
    }

    private async void OnBrowseNew(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Выберите новый файл");
        if (path is null) return;
        _new = path; NewBox.Text = path; ToolTip.SetTip(NewBox, path);
        UpdateState();
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Выберите папку с расшифровками");
        if (path is null) return;
        _folder = path; Display(FolderBox, path);
        Remember(ProductCompareExtension.BreakdownFolderKey, path);
        UpdateState();
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolder("Выберите папку для сохранения результата");
        if (path is null) return;
        _output = path; Display(OutputBox, path);
        UpdateState();
    }

    private async Task<string?> PickFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = title,
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task<string?> PickFile(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = title,
            AllowMultiple  = false,
            FileTypeFilter = [ExcelType],
        });
        return files.FirstOrDefault()?.Path.LocalPath;
    }

    // ── Сравнение ─────────────────────────────────────────────────────────────

    private void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_running || !AllReady()) return;
        if (Breakdown) RunBreakdown();
        else           RunConclusion();
    }

    private async void RunConclusion()
    {
        SetRunning(true);
        SetStatus("Сравнение…", "BrushAccent");
        Progress.SetProgress(0);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<int>(p => Progress.SetProgress(p / 100.0));

        try
        {
            var result = await Task.Run(
                () => ProductCompareEngine.Run(_template!, _old!, _new!, _output!, progress, ct), ct);

            Progress.SetProgress(1);
            SetStatus($"Готово: {Path.GetFileName(result.OutputPath)} " +
                      $"(строк: {result.TotalRows}, старый: {result.OldRows}, новый: {result.NewRows})",
                      "BrushAccent");
            await OfferOpenFolder(result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Отменено", "BrushSecondaryText");
        }
        catch (ProductCompareException ex)
        {
            SetStatus(ex.Message, "BrushRed");
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка: {ex.Message}", "BrushRed");
        }
        finally
        {
            SetRunning(false);
        }
    }

    private async void RunBreakdown()
    {
        SetRunning(true);
        SetStatus("Сравнение расшифровок…", "BrushAccent");
        Progress.SetProgress(0);
        ClearLog();
        LogLine($"📂 Папка: {_folder}");
        LogLine($"📄 Шаблон: {_breakdownTemplate}");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<int>(p => Progress.SetProgress(p / 100.0));
        var log      = new Progress<string>(LogLine);

        try
        {
            var result = await Task.Run(
                () => BreakdownCompareEngine.Run(_breakdownTemplate!, _folder!, _output!, progress, log, ct), ct);

            Progress.SetProgress(1);
            LogLine("");
            LogLine($"📊 Строк в сравнении: {result.TotalRows} (изделий {result.Products}, операций {result.Operations})");
            LogLine($"💾 Результат: {result.OutputPath}");
            SetStatus($"Готово: {Path.GetFileName(result.OutputPath)} (дат: {result.Files.Count}, строк: {result.TotalRows})",
                      "BrushAccent");
            await OfferOpenFolder(result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            LogLine("⏹️ Отменено");
            SetStatus("Отменено", "BrushSecondaryText");
        }
        catch (ProductCompareException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            // Непредвиденный сбой: одного сообщения .NET мало, чтобы найти место, — стек в журнал,
            // его можно прислать разработчику снимком (тот же прием, что у «Сводной трудоемкости»).
            Fail($"Ошибка: {ex.Message}");
            LogLine("");
            LogLine("Подробности сбоя (для разработчика):");
            LogLine(ex.ToString());
        }
        finally
        {
            SetRunning(false);
        }
    }

    // Причина отказа бывает списком файлов — целиком она идет в журнал, в статус — первая строка.
    private void Fail(string message)
    {
        LogLine("");
        LogLine($"❌ {message}");
        var first = message.Split('\n')[0].TrimEnd(':');
        SetStatus(message.Contains('\n') ? $"{first} — подробности в журнале" : first, "BrushRed");
    }

    private void LogLine(string text)
    {
        if (_log.Length > 0) _log.Append('\n');
        _log.Append(text);
        LogBox.Text       = _log.ToString();
        LogBox.CaretIndex = LogBox.Text.Length;   // прокрутка к последней строке
    }

    private void ClearLog()
    {
        _log.Clear();
        LogBox.Text = "";
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
                FileName       = "explorer.exe",
                Arguments      = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { /* не критично, если открыть не удалось */ }
    }

    // ── Состояние ─────────────────────────────────────────────────────────────

    private bool AllReady() => Breakdown
        ? !string.IsNullOrEmpty(_breakdownTemplate) && !string.IsNullOrEmpty(_folder) &&
          !string.IsNullOrEmpty(_output)
        : !string.IsNullOrEmpty(_template) && !string.IsNullOrEmpty(_old) &&
          !string.IsNullOrEmpty(_new)      && !string.IsNullOrEmpty(_output);

    private void UpdateState()
    {
        ExecuteBtn.IsEnabled = !_running && AllReady();
        if (_running) return;

        if (AllReady())
        {
            SetStatus("Все готово к сравнению", "BrushAccent");
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrEmpty(ModeTemplate)) missing.Add("шаблон");
        if (Breakdown)
        {
            if (string.IsNullOrEmpty(_folder)) missing.Add("папка с расшифровками");
        }
        else
        {
            if (string.IsNullOrEmpty(_old)) missing.Add("старый файл");
            if (string.IsNullOrEmpty(_new)) missing.Add("новый файл");
        }
        if (string.IsNullOrEmpty(_output))   missing.Add("папка для сохранения");
        SetStatus($"Ожидание: {string.Join(", ", missing)}", "BrushSecondaryText");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        ModeCombo.IsEnabled   = TemplateBtn.IsEnabled = OldBtn.IsEnabled = NewBtn.IsEnabled =
            FolderBtn.IsEnabled = OutputBtn.IsEnabled = !running;
        ExecuteBtn.IsEnabled  = !running && AllReady();
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        if (this.TryFindResource(brushKey, out var v) && v is IBrush brush)
            StatusText.Foreground = brush;
    }

    // Отмена прогона при закрытии окна, чтобы фоновая задача не висела.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
