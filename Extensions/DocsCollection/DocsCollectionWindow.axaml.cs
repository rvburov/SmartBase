using System.IO;
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

namespace SmartBase.Extensions.DocsCollection;

/// <summary>
/// Окно расширения «Документы в коллекцию» (в основном стиле приложения): папка с документами,
/// рекурсивный поиск, фильтр расширений и целевая база. По «Запустить» процедура выполняется
/// один раз: обход папки и запись коллекции. Логика — в <see cref="DocsCollectionBuilder"/>;
/// диск и вставка записей идут в фоне (<see cref="Task.Run"/>) с прогрессом и отменой.
/// </summary>
public partial class DocsCollectionWindow : Window
{
    // Базы реестра в порядке пунктов DbCombo (индекс пункта → DbId).
    private readonly IReadOnlyList<(int DbId, string Name)> _databases;

    private CancellationTokenSource? _cts;
    private bool _running;

    public DocsCollectionWindow()
    {
        InitializeComponent();

        // Доступ к базам — через ту же точку, что и межбазовые связи: расширению не нужно
        // знать ни о Workspace, ни о главном окне.
        _databases = RelationDbResolver.Databases();
        DbCombo.ItemsSource = _databases.Select(d => d.Name).ToList();
        if (_databases.Count > 0) DbCombo.SelectedIndex = 0;
        DbCombo.IsEnabled = _databases.Count > 1;

        UpdateState();
    }

    // ── Выбор папки ───────────────────────────────────────────────────────────

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? start = null;
        var current = FolderBox.Text?.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            start = await StorageProvider.TryGetFolderFromPathAsync(current);

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Выберите папку с документами",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        var path = folder.Path.LocalPath;
        FolderBox.Text = path;
        ToolTip.SetTip(FolderBox, path);
        UpdateState();
    }

    private void OnPathChanged(object? sender, TextChangedEventArgs e) => UpdateState();

    // ── Запуск ────────────────────────────────────────────────────────────────

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        if (_running) return;

        var folder = FolderBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(folder)) { SetStatus("Укажите папку с документами", "BrushRed"); return; }
        if (!Directory.Exists(folder))    { SetStatus("Папка не существует", "BrushRed"); return; }

        if (DbCombo.SelectedIndex < 0 || DbCombo.SelectedIndex >= _databases.Count)
        {
            SetStatus("Нет доступной базы данных", "BrushRed");
            return;
        }

        var dbId = _databases[DbCombo.SelectedIndex].DbId;
        var repo = RelationDbResolver.RepoFor(dbId);
        if (repo is null) { SetStatus("База данных недоступна", "BrushRed"); return; }

        var recursive  = RecursiveCheck.IsChecked == true;
        var extensions = DocsCollectionBuilder.ParseExtensions(ExtBox.Text);

        SetRunning(true);
        SetStatus("Поиск документов…", "BrushAccent");
        Progress.SetProgress(0);
        HideCurrentFile();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Каркас создается ниже — до этого момента откатывать нечего.
        DocsShell? shell = null;

        try
        {
            // 1. Диск — в фоне: сетевая папка с тысячами файлов обходится не мгновенно.
            var docs = await Task.Run(() => DocsCollectionBuilder.Scan(folder, recursive, extensions, ct), ct);
            if (docs.Count == 0)
            {
                SetStatus("В папке нет подходящих файлов — коллекция не создана", "BrushRed");
                return;
            }

            // 2. Коллекция и колонки — на общем репозитории (UI-поток): здесь же сработает
            //    проверка прав на создание коллекции.
            SetStatus($"Создаю коллекцию ({docs.Count} документов)…", "BrushAccent");
            shell = DocsCollectionBuilder.CreateShell(repo, DocsCollectionBuilder.DefaultCollectionName(folder));

            // 3. Записи — на отдельном подключении в фоне.
            var progress = new Progress<DocsProgress>(p =>
            {
                Progress.SetProgress(p.Total > 0 ? (double)p.Current / p.Total : 0);
                CurrentFileText.Text      = $"{p.Current} из {p.Total}: {p.File}";
                CurrentFileText.IsVisible = true;
            });

            var dbPath  = repo.DbPath;
            var target  = shell;
            var created = await Task.Run(
                () => DocsCollectionBuilder.FillRecords(dbPath, target, docs, progress, ct), ct);

            Progress.SetProgress(1);
            HideCurrentFile();
            RefreshSidebars();

            SetStatus($"Готово: коллекция «{shell.CollectionName}» — записей {created}", "BrushAccent");
            await ConfirmDialog
                .Info($"Коллекция «{shell.CollectionName}» создана.\nЗаписей: {created}.")
                .ShowDialog<bool>(this);
        }
        catch (OperationCanceledException)
        {
            Rollback(repo, shell);
            SetStatus("Отменено — созданная коллекция удалена", "BrushSecondaryText");
            HideCurrentFile();
        }
        catch (PermissionDeniedException ex)
        {
            Rollback(repo, shell);
            SetStatus(ex.Message, "BrushRed");
            HideCurrentFile();
        }
        catch (DocsCollectionException ex)
        {
            Rollback(repo, shell);
            Log.Warning(ex, "Документы в коллекцию: не выполнено");
            SetStatus(ex.Message, "BrushRed");
            HideCurrentFile();
        }
        catch (Exception ex)
        {
            Rollback(repo, shell);
            Log.Error(ex, "Документы в коллекцию: непредвиденная ошибка");
            SetStatus($"Ошибка: {ex.Message}", "BrushRed");
            HideCurrentFile();
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
        SetStatus("Останавливаю…", "BrushSecondaryText");
        StopBtn.IsEnabled = false;
    }

    // Полупустая коллекция после отмены/ошибки не нужна — убираем и обновляем сайдбар.
    private void Rollback(ConstructorRepository repo, DocsShell? shell)
    {
        if (shell is null) return;
        DocsCollectionBuilder.TryDelete(repo, shell.CollectionId);
        RefreshSidebars();
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
            Log.Warning(ex, "Документы в коллекцию: не удалось обновить сайдбар");
        }
    }

    // ── Состояние ─────────────────────────────────────────────────────────────

    private void UpdateState()
    {
        var ready = !string.IsNullOrWhiteSpace(FolderBox.Text) && _databases.Count > 0;
        ExecuteBtn.IsEnabled = ready && !_running;

        if (_running) return;
        if (_databases.Count == 0)                             SetStatus("Нет доступной базы данных", "BrushRed");
        else if (string.IsNullOrWhiteSpace(FolderBox.Text))    SetStatus("Укажите папку с документами", "BrushSecondaryText");
        else                                                   SetStatus("Готов к работе", "BrushAccent");
    }

    private void SetRunning(bool running)
    {
        _running = running;
        BrowseBtn.IsEnabled      = !running;
        FolderBox.IsEnabled      = !running;
        ExtBox.IsEnabled         = !running;
        RecursiveCheck.IsEnabled = !running;
        DbCombo.IsEnabled        = !running && _databases.Count > 1;
        StopBtn.IsEnabled        = running;
        ExecuteBtn.IsEnabled     = !running && !string.IsNullOrWhiteSpace(FolderBox.Text) && _databases.Count > 0;
    }

    private void SetStatus(string text, string brushKey)
    {
        StatusText.Text = text;
        if (this.TryFindResource(brushKey, out var v) && v is IBrush brush)
            StatusText.Foreground = brush;
    }

    private void HideCurrentFile()
    {
        CurrentFileText.Text      = "";
        CurrentFileText.IsVisible = false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _cts?.Cancel();
        base.OnClosing(e);
    }
}
