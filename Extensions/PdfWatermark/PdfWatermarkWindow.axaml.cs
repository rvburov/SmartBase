using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>
/// Окно расширения: выбрать документы, получателя, дату и журнал — и наложить знак.
///
/// Разделение труда то же, что у соседей: окно ведет ввод, проверяет приемник и показывает ход,
/// а работа идет в <see cref="PdfWatermarkEngine"/> в фоне на СВОЕМ подключении к базе. Общий
/// <c>AppDbContext</c> принадлежит UI-потоку, и в <c>Task.Run</c> он не уходит никогда —
/// туда передается только путь к файлу базы.
/// </summary>
public partial class PdfWatermarkWindow : Window
{
    private static readonly FilePickerFileType PdfType = new("PDF (*.pdf)") { Patterns = ["*.pdf"] };

    /// <summary>Потолок строк журнала окна. Пачка в тысячи файлов не должна превращать поле
    /// ввода в тормоз.</summary>
    private const int MaxLogLines = 1000;

    /// <summary>Как часто журнал и индикатор перерисовываются на прогоне — примерно 20 раз в
    /// секунду. Чаще человек все равно не увидит, а перерисовка стоит дорого.</summary>
    private const int UiIntervalMs = 50;

    private readonly StringBuilder _log      = new();
    private readonly Stopwatch     _throttle = new();

    private IReadOnlyList<(int DbId, string Name)> _databases   = [];
    private List<DbCollection>                     _collections = [];

    private ConstructorRepository? _repo;
    private JournalShape?          _shape;
    private string?                _schemaProblem;

    private CancellationTokenSource? _cts;
    private int  _shownLines;
    private bool _truncated;
    private bool _busy;

    public PdfWatermarkWindow()
    {
        InitializeComponent();

        _databases = RelationDbResolver.RegistryDatabases();
        DbCombo.ItemsSource = _databases.Select(d => d.Name).ToList();
        DbCombo.IsEnabled   = _databases.Count > 1;

        Restore(OutputBox, PdfWatermarkExtension.OutputKey);

        // Предупреждение: последнее использованное, а пусто — значение по умолчанию. Пустым это
        // поле не остается никогда: знак без предупреждения теряет смысл.
        WarningBox.Text = PdfWatermarkExtension.Warning(
            AppSettings.LoadExtensionSetting(PdfWatermarkExtension.ExtensionId,
                                             PdfWatermarkExtension.WarningKey));

        // Дата по умолчанию — сегодня; человек правит ее руками, если выдача задним числом.
        DateBox.Date        = DateTime.Today;
        DateBox.DateChanged += (_, _) => UpdateState();

        RestoreDatabase();
        UpdateState();
    }

    // ── Источник и папка результата ───────────────────────────────────────────

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Выберите PDF-документ",
            AllowMultiple  = false,
            FileTypeFilter = [PdfType],
        });

        if (files.FirstOrDefault()?.Path.LocalPath is { } path) Assign(SourceBox, path, key: null);
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Выберите папку с документами", SourceBox.Text) is { } path)
            Assign(SourceBox, path, key: null);
    }

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
    {
        if (await PickFolder("Выберите папку для сохранения результата", OutputBox.Text) is { } path)
            Assign(OutputBox, path, PdfWatermarkExtension.OutputKey);
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

    // Источник между запусками НЕ запоминается, в отличие от папки результата: приемник у человека
    // один и тот же, а выдают каждый раз другое.
    private void Assign(TextBox box, string path, string? key)
    {
        box.Text = path;
        ToolTip.SetTip(box, path);
        if (key is not null)
            AppSettings.SaveExtensionSetting(PdfWatermarkExtension.ExtensionId, key, path);
        UpdateState();
    }

    /// <summary>Запомнить текст предупреждения — им же откроется окно в следующий раз.</summary>
    private static void RememberWarning(string warning) =>
        AppSettings.SaveExtensionSetting(PdfWatermarkExtension.ExtensionId,
                                         PdfWatermarkExtension.WarningKey,
                                         warning == PdfWatermarkExtension.DefaultWarning ? "" : warning);

    private static void Restore(TextBox box, string key)
    {
        var value = AppSettings.LoadExtensionSetting(PdfWatermarkExtension.ExtensionId, key);
        if (value.Length == 0) return;
        box.Text = value;
        ToolTip.SetTip(box, value);
    }

    private void OnSourceChanged(object? sender, TextChangedEventArgs e)
    {
        // Рекурсия имеет смысл только для папки — на одиночном файле обходить нечего.
        var isFolder = Directory.Exists(SourceBox.Text?.Trim() ?? "");
        RecursiveCheck.IsEnabled = isFolder && !_busy;
        if (!isFolder) RecursiveCheck.IsChecked = false;

        UpdateState();
    }

    private void OnFormChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    // ── Куда пишем: база и коллекция ──────────────────────────────────────────

    private void OnDbChanged(object? sender, SelectionChangedEventArgs e)
    {
        _repo        = null;
        _collections = [];
        CollectionCombo.ItemsSource = null;

        var index = DbCombo.SelectedIndex;
        if (index >= 0 && index < _databases.Count)
        {
            try
            {
                _repo = RelationDbResolver.RegistryRepoFor(_databases[index].DbId);
                if (_repo is not null)
                {
                    _collections = _repo.GetAllCollections();
                    CollectionCombo.ItemsSource = _collections.Select(c => c.Name).ToList();
                }
            }
            catch (Exception)
            {
                _repo = null;
            }
        }

        CollectionCombo.IsEnabled = _collections.Count > 0;
        RestoreLastCollection();

        // Ничего не выбралось — SelectionChanged не поднимется, раскладку сбрасываем сами.
        if (CollectionCombo.SelectedIndex < 0) RefreshShape();
    }

    // База журнала у человека одна и та же изо дня в день — возвращаем прошлую по ИМЕНИ. Не по
    // DbId: он живет в реестре и после переустановки или перемонтирования баз может значить
    // другое, а имя человек и видит в списке.
    private void RestoreDatabase()
    {
        if (_databases.Count == 0) return;

        var last = AppSettings.LoadExtensionSetting(PdfWatermarkExtension.ExtensionId,
                                                    PdfWatermarkExtension.DatabaseKey);

        var at = -1;
        if (last.Length > 0)
            for (var i = 0; i < _databases.Count && at < 0; i++)
                if (string.Equals(_databases[i].Name, last, StringComparison.CurrentCultureIgnoreCase))
                    at = i;

        DbCombo.SelectedIndex = at >= 0 ? at : 0;
    }

    // Коллекция — так же по имени. Не нашлась (переименовали, другая база) — не выбираем НИЧЕГО:
    // наугад взятая первая тут же отвечала бы «Вы выбрали неправильную коллекцию» на пустом месте.
    private void RestoreLastCollection()
    {
        if (_collections.Count == 0) { CollectionCombo.SelectedIndex = -1; return; }

        var last = AppSettings.LoadExtensionSetting(PdfWatermarkExtension.ExtensionId,
                                                    PdfWatermarkExtension.CollectionKey);

        CollectionCombo.SelectedIndex = last.Length == 0
            ? -1
            : _collections.FindIndex(c => string.Equals(c.Name, last, StringComparison.CurrentCultureIgnoreCase));
    }

    private void OnCollectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshShape();

    /// <summary>Перечитать выбранную коллекцию: сошлась ли структура журнала.</summary>
    private void RefreshShape()
    {
        _shape         = null;
        _schemaProblem = null;

        var index = CollectionCombo.SelectedIndex;
        if (_repo is not null && index >= 0 && index < _collections.Count)
        {
            var collection = _collections[index];
            try
            {
                var check = JournalSchema.Check(_repo, collection.Id!.Value, collection.Name);
                if (check.Shape is { } shape)
                {
                    _shape = shape;
                    RememberTarget(collection.Name);
                }
                else
                {
                    _schemaProblem = check.Problem;
                }
            }
            catch (Exception ex)
            {
                _schemaProblem = ex.Message;
            }
        }

        UpdateState();
    }

    /// <summary>Запомнить приемник. Пишется ТОЛЬКО когда структура сошлась: незачем возвращать
    /// в следующий раз выбор, на котором работать все равно нельзя.</summary>
    private void RememberTarget(string collectionName)
    {
        var index = DbCombo.SelectedIndex;
        if (index >= 0 && index < _databases.Count)
            AppSettings.SaveExtensionSetting(PdfWatermarkExtension.ExtensionId,
                                             PdfWatermarkExtension.DatabaseKey, _databases[index].Name);

        AppSettings.SaveExtensionSetting(PdfWatermarkExtension.ExtensionId,
                                         PdfWatermarkExtension.CollectionKey, collectionName);
    }

    // ── Прогон ────────────────────────────────────────────────────────────────

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _shape is null || _repo is null) return;

        var source = SourceBox.Text!.Trim();
        var output = OutputBox.Text!.Trim();
        var holder = HolderBox.Text!.Trim();
        var date   = DateBox.Date!.Value;
        var shape  = _shape;

        var dbPath = _repo.DbPath;

        SetBusy(true);
        ClearLog();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            Progress.SetIndeterminate("Поиск документов");
            var recursive = RecursiveCheck.IsChecked == true;
            var files = await Task.Run(() => PdfWatermarkEngine.ListPdfFiles(source, recursive, ct), ct);

            if (files.Count == 0)
            {
                Fail("В указанном месте не найдено PDF-документов.");
                return;
            }

            LogLineNow($"📂 Источник: {source}");
            LogLineNow($"📄 Документов: {files.Count}");
            LogLineNow($"🖊️ Кому выдан: {holder}, дата {date:dd.MM.yyyy}");
            LogLineNow("🚀 Наложение знака…");

            // Предупреждение берется ИЗ ОКНА и тут же уходит в настройки: человек правит его
            // под конкретную выдачу, а следующее открытие окна подставит последнее значение.
            var warning = PdfWatermarkExtension.Warning(WarningBox.Text);
            RememberWarning(warning);

            var request  = new StampRequest(source, recursive, output, holder, date,
                                            warning, dbPath, shape);
            var progress = new Progress<StampProgress>(OnProgress);

            _throttle.Restart();
            var summary = await Task.Run(() => PdfWatermarkEngine.Run(request, files, progress, ct), ct);

            await Report(summary);
        }
        catch (OperationCanceledException)
        {
            LogLineNow("⏹️ Остановлено.");
            Progress.SetInfo("Остановлено");
            SetStatus("Остановлено", "BrushSecondaryText");
        }
        catch (PdfWatermarkException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            Fail("Прогон не удался.", ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopBtn.IsEnabled = false;
    }

    private void OnProgress(StampProgress p)
    {
        // Дросселируем: на пачке в тысячи файлов перерисовка чаще 20 раз в секунду только
        // отнимает время у самой работы.
        if (_throttle.ElapsedMilliseconds < UiIntervalMs && p.Current != p.Total) return;
        _throttle.Restart();

        Progress.SetProgress(p.Fraction, $"{p.Current} из {p.Total}");
        SetStatus($"Обработка: {p.FileName}", "BrushSecondaryText");
    }

    /// <summary>Итог прогона: по строке на файл, затем сводка. Успехи ТОЖЕ пишутся — выданные
    /// номера и есть то, ради чего прогон делается.</summary>
    private async Task Report(StampSummary summary)
    {
        foreach (var item in summary.Items)
            LogLine(item.Status switch
            {
                StampStatus.Done    => $"✅ {item.FileName} → {item.OutputName} " +
                                       $"(экз. {item.Number}, страниц {item.Pages})",
                StampStatus.Skipped => $"⏭️ {item.FileName} — {item.Note}",
                _                   => $"❌ {item.FileName} — {item.Note}",
            });

        LogLine("");
        LogLine(summary.Cancelled
            ? $"⏹️ Остановлено. Обработано: {summary.Done} из {summary.Total} — " +
              "знак наложен и строки заведены только для них."
            : $"📊 Готово. Обработано: {summary.Done} из {summary.Total}.");

        if (summary.Skipped > 0) LogLine($"⏭️ Пропущено (пароль или повреждение): {summary.Skipped}");
        if (summary.Failed  > 0) LogLine($"❌ Сбоев: {summary.Failed}");
        RenderLog();

        if (summary.Cancelled)
        {
            Progress.SetInfo("Остановлено");
            SetStatus($"Остановлено. Обработано: {summary.Done} из {summary.Total}", "BrushSecondaryText");
            return;
        }

        Progress.SetProgress(1, "Готово");
        SetStatus($"Готово: {summary.Done} из {summary.Total}", "BrushAccent");

        await ConfirmDialog
            .Info($"Обработано документов: {summary.Done} из {summary.Total}.")
            .ShowDialog<bool>(this);
    }

    private void Fail(string message, Exception? technical = null)
    {
        LogLineNow($"❌ {message}");
        if (technical is not null)
        {
            LogLine("Подробности сбоя (для разработчика):");
            LogLine(technical.ToString());
            RenderLog();
        }

        Progress.SetError(message);
        SetStatus(message, "BrushRed");
    }

    // ── Состояние окна ────────────────────────────────────────────────────────

    private void SetBusy(bool busy)
    {
        _busy = busy;

        SourceBox.IsEnabled       = !busy;
        FileBtn.IsEnabled         = !busy;
        FolderBtn.IsEnabled       = !busy;
        OutputBox.IsEnabled       = !busy;
        OutputBtn.IsEnabled       = !busy;
        HolderBox.IsEnabled       = !busy;
        DateBox.IsEnabled         = !busy;
        WarningBox.IsEnabled      = !busy;
        DbCombo.IsEnabled         = !busy && _databases.Count > 1;
        CollectionCombo.IsEnabled = !busy && _collections.Count > 0;
        RecursiveCheck.IsEnabled  = !busy && Directory.Exists(SourceBox.Text?.Trim() ?? "");
        StopBtn.IsEnabled         = busy;

        if (!busy) UpdateState(); else ExecuteBtn.IsEnabled = false;
    }

    /// <summary>
    /// Каскад «что заполнить дальше»: первый незакрытый пункт и становится подсказкой. Кнопка
    /// включается, только когда закрыты все.
    /// </summary>
    private void UpdateState()
    {
        if (_busy) return;

        var source = SourceBox.Text?.Trim() ?? "";
        var output = OutputBox.Text?.Trim() ?? "";
        var holder = HolderBox.Text?.Trim() ?? "";

        if (Block(source.Length == 0,                   "Выберите PDF-файл или папку с документами")) return;
        if (Block(!File.Exists(source) && !Directory.Exists(source), "Указанный файл или папка не существует")) return;
        if (Block(output.Length == 0,                   "Выберите папку для сохранения результата")) return;
        if (Block(!Directory.Exists(output),            "Папка для сохранения результата не существует")) return;
        if (Block(holder.Length == 0,                   "Заполните, кому выдан документ")) return;
        if (Block(DateBox.Date is null,                 "Заполните дату выдачи")) return;
        if (Block(_databases.Count == 0,                "Нет доступных баз данных")) return;
        if (Block(_repo is null,                        "База недоступна")) return;
        if (Block(CollectionCombo.SelectedIndex < 0,    "Выберите коллекцию журнала выдачи")) return;
        if (Block(_shape is null, $"Вы выбрали неправильную коллекцию — {_schemaProblem}")) return;

        ExecuteBtn.IsEnabled = true;
        SetStatus("Готов к работе", "BrushSecondaryText");
    }

    private bool Block(bool condition, string message)
    {
        if (!condition) return false;
        ExecuteBtn.IsEnabled = false;
        SetStatus(message, "BrushSecondaryText");
        return true;
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        if (this.TryFindResource(brushKey, out var value) && value is Avalonia.Media.IBrush brush)
            StatusText.Foreground = brush;
    }

    // ── Журнал окна ───────────────────────────────────────────────────────────

    // Дешево: только в модель. Показ — отдельно и пачкой (RenderLog).
    private void LogLine(string text)
    {
        if (_shownLines >= MaxLogLines)
        {
            if (_truncated) return;
            _truncated = true;
            _log.Append($"… показаны первые {MaxLogLines} строк\n");
            return;
        }

        _shownLines++;
        _log.Append(text).Append('\n');
    }

    // Дорого: зовем пачкой, а не на каждую строку.
    private void RenderLog()
    {
        LogBox.Text       = _log.ToString();
        LogBox.CaretIndex = LogBox.Text.Length;   // прокрутка к последней строке
    }

    // Для редких сообщений, которые надо увидеть сразу.
    private void LogLineNow(string text)
    {
        LogLine(text);
        RenderLog();
    }

    private void ClearLog()
    {
        _log.Clear();
        _shownLines = 0;
        _truncated  = false;
        RenderLog();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
