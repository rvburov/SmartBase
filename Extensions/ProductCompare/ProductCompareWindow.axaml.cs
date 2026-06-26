using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DataBase.Controls.Dialogs;

namespace DataBase.Extensions.ProductCompare;

/// <summary>
/// Окно расширения «Сравнение составов изделий» (в основном стиле приложения): выбор
/// шаблона, старого и нового файлов и папки результата, затем «Сравнить файлы».
/// Логика — в <see cref="ProductCompareEngine"/>; сам прогон идёт в фоне (<see cref="Task.Run"/>)
/// с прогрессом и отменой при закрытии окна.
/// </summary>
public partial class ProductCompareWindow : Window
{
    // Путь к шаблону по умолчанию (сетевой). Подставляется, только если файл доступен.
    private const string DefaultTemplate =
        @"P:\20016000 Технологический отдел\_Макросы\Программы Python\Формирование файла «Сравнение составов изделия»\Шаблон сравнения файлов изделий.xlsm";

    private static readonly FilePickerFileType ExcelType =
        new("Excel (*.xlsx, *.xlsm, *.xls)") { Patterns = ["*.xlsx", "*.xlsm", "*.xls"] };

    private string? _template, _old, _new, _output;
    private CancellationTokenSource? _cts;
    private bool _running;

    public ProductCompareWindow()
    {
        InitializeComponent();

        // Путь к шаблону по умолчанию показываем всегда (он фиксирован для рабочих машин,
        // где смонтирован диск P:). Доступность файла проверяется при запуске сравнения —
        // если недоступен, движок вернёт «Файл-шаблон не найден».
        _template = DefaultTemplate;
        TemplateBox.Text = DefaultTemplate;
        ToolTip.SetTip(TemplateBox, DefaultTemplate);

        UpdateState();
    }

    // ── Выбор файлов / папки ──────────────────────────────────────────────────

    private async void OnBrowseTemplate(object? sender, RoutedEventArgs e)
    {
        var path = await PickFile("Выберите файл-шаблон");
        if (path is null) return;
        _template = path; TemplateBox.Text = path; ToolTip.SetTip(TemplateBox, path);
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

    // ── Сравнение ─────────────────────────────────────────────────────────────

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_running || !AllReady()) return;

        SetRunning(true);
        SetStatus("Сравнение…", "BrushAccent");
        Bar.Value = 0;
        PercentText.Text = "0%";

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<int>(p => { Bar.Value = p; PercentText.Text = $"{p}%"; });

        try
        {
            var result = await Task.Run(
                () => ProductCompareEngine.Run(_template!, _old!, _new!, _output!, progress, ct), ct);

            Bar.Value = 100;
            PercentText.Text = "100%";
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

    private bool AllReady() =>
        !string.IsNullOrEmpty(_template) && !string.IsNullOrEmpty(_old) &&
        !string.IsNullOrEmpty(_new)      && !string.IsNullOrEmpty(_output);

    private void UpdateState()
    {
        ExecuteBtn.IsEnabled = !_running && AllReady();
        if (_running) return;

        if (AllReady())
        {
            SetStatus("Всё готово к сравнению", "BrushAccent");
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrEmpty(_template)) missing.Add("шаблон");
        if (string.IsNullOrEmpty(_old))      missing.Add("старый файл");
        if (string.IsNullOrEmpty(_new))      missing.Add("новый файл");
        if (string.IsNullOrEmpty(_output))   missing.Add("папка для сохранения");
        SetStatus($"Ожидание: {string.Join(", ", missing)}", "BrushSecondaryText");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        TemplateBtn.IsEnabled = OldBtn.IsEnabled = NewBtn.IsEnabled = OutputBtn.IsEnabled = !running;
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
