using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Serilog;
using SmartBase.Controls.Dialogs;
using SmartBase.Utils;

namespace SmartBase.Extensions.AssemblyCalc;

/// <summary>
/// Окно расширения «Расчет сборочных единиц в изделии» (в основном стиле приложения): выбор
/// исходного файла, шаблона и папки результата, затем «Запустить». Логика — в
/// <see cref="AssemblyCalcEngine"/>; прогон идет в фоне (<see cref="Task.Run"/>) с прогрессом
/// и отменой при закрытии окна.
/// </summary>
public partial class AssemblyCalcWindow : Window
{
    private static readonly FilePickerFileType ExcelType =
        new("Excel (*.xlsx, *.xlsm, *.xls)") { Patterns = ["*.xlsx", "*.xlsm", "*.xls"] };

    private string? _source, _template, _output;
    private CancellationTokenSource? _cts;
    private bool _running;

    public AssemblyCalcWindow()
    {
        InitializeComponent();

        // Путь к шаблону — из настроек расширения (объявлены в AssemblyCalcExtension, экран —
        // «Настройки → Расширения»): у каждой машины свой смонтированный диск, в коде пути
        // нет. Не задан — поле пустое, шаблон выбирают «Обзором»; доступность проверяет
        // движок при запуске.
        var template = AppSettings.LoadExtensionSetting(
            AssemblyCalcExtension.ExtensionId, AssemblyCalcExtension.TemplateKey);
        if (template.Length > 0)
        {
            _template = template;
            TemplateBox.Text = template;
            ToolTip.SetTip(TemplateBox, template);
        }

        UpdateState();
    }

    // ── Выбор файлов / папки ──────────────────────────────────────────────────

    private async void OnBrowseSource(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Выберите исходный файл «Список КД и ТД»");
        if (path is null) return;
        _source = path; SourceBox.Text = path; ToolTip.SetTip(SourceBox, path);
        UpdateState();
    }

    private async void OnBrowseTemplate(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Выберите файл-шаблон");
        if (path is null) return;
        _template = path; TemplateBox.Text = path; ToolTip.SetTip(TemplateBox, path);
        // Запоминаем выбор: следующий запуск подставит шаблон сам.
        AppSettings.SaveExtensionSetting(
            AssemblyCalcExtension.ExtensionId, AssemblyCalcExtension.TemplateKey, path);
        UpdateState();
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Выберите папку для сохранения результата",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        _output = folder.Path.LocalPath; OutputBox.Text = _output; ToolTip.SetTip(OutputBox, _output);
        UpdateState();
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

    // ── Формирование ──────────────────────────────────────────────────────────

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_running || !AllReady()) return;

        SetRunning(true);
        SetStatus("Формирование…", "BrushAccent");
        Progress.SetProgress(0);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<int>(p => Progress.SetProgress(p / 100.0));

        try
        {
            var result = await Task.Run(
                () => AssemblyCalcEngine.Run(_source!, _template!, _output!, progress, ct), ct);

            Progress.SetProgress(1);
            SetStatus($"Готово: {Path.GetFileName(result.OutputPath)} (строк: {result.RowCount})", "BrushAccent");
            await OfferOpenFolder(result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Отменено", "BrushSecondaryText");
        }
        catch (AssemblyCalcException ex)
        {
            Log.Warning(ex, "Расчет сборочных единиц не выполнен");
            SetStatus(ex.Message, "BrushRed");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Расчет сборочных единиц: непредвиденная ошибка");
            SetStatus($"Ошибка: {ex.Message}", "BrushRed");
        }
        finally
        {
            SetRunning(false);
        }
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

    // ── Состояние ─────────────────────────────────────────────────────────────

    private bool AllReady() =>
        !string.IsNullOrEmpty(_source) && !string.IsNullOrEmpty(_template) && !string.IsNullOrEmpty(_output);

    private void UpdateState()
    {
        ExecuteBtn.IsEnabled = !_running && AllReady();
        if (_running) return;

        if (AllReady())
        {
            SetStatus("Все готово к формированию", "BrushAccent");
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrEmpty(_source))   missing.Add("исходный файл");
        if (string.IsNullOrEmpty(_template)) missing.Add("шаблон");
        if (string.IsNullOrEmpty(_output))   missing.Add("папка для сохранения");
        SetStatus($"Ожидание: {string.Join(", ", missing)}", "BrushSecondaryText");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        SourceBtn.IsEnabled = TemplateBtn.IsEnabled = OutputBtn.IsEnabled = !running;
        ExecuteBtn.IsEnabled = !running && AllReady();
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
