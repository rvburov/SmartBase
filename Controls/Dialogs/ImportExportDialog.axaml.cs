using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DataBase.Core.Importers;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

/// <summary>Результат успешного импорта: id целевой БД и созданной коллекции.</summary>
public record ImportExportResult(int DbId, int CollectionId);

/// <summary>
/// Окно «Импорт/Экспорт». Сейчас реализован только импорт листа Excel (.xlsx/.xlsm) в новую
/// коллекцию: режим «Экспорт» виден, но отключён. Поля «название / база данных / расположение»
/// работают как в <see cref="CreateItemDialog"/>.
/// </summary>
public partial class ImportExportDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private List<(string Label, int? Id)> _groups = [];

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    public ImportExportDialog(IReadOnlyList<DbOption> dbs, int currentDbId)
    {
        InitializeComponent();
        _dbs = dbs;

        ModeCombo.SelectedIndex = 0;   // Импорт (Экспорт отключён)

        TypeCombo.ItemsSource   = new[] { "Стандартный", "Ручной" };
        TypeCombo.SelectedIndex = 0;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(currentDbId);

        RebuildLocation();
        DbCombo.SelectionChanged += (_, _) => RebuildLocation();

        UpdateTypeVisibility(keepPosition: false);
        TypeCombo.SelectionChanged += (_, _) => UpdateTypeVisibility(keepPosition: true);

        BrowseBtn.Click  += async (_, _) => await PickFile();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += async (_, _) => await TryImport();

        NameBox.TextChanged += (_, _) => HideError();
        PathBox.TextChanged += (_, _) => HideError();
    }

    private int IndexOfDb(int dbId)
    {
        for (var i = 0; i < _dbs.Count; i++)
            if (_dbs[i].Id == dbId) return i;
        return 0;
    }

    private void RebuildLocation()
    {
        _groups = GroupLocationOptions.Build(CurrentRepo.GetAllGroups());
        LocationCombo.ItemsSource   = _groups.Select(g => g.Label).ToList();
        LocationCombo.SelectedIndex = 0;
    }

    // Переключаем пояснение (стандартный) и панель параметров (ручной); держим позицию окна.
    private void UpdateTypeVisibility(bool keepPosition)
    {
        var pos    = Position;
        var manual = TypeCombo.SelectedIndex == 1;
        ManualPanel.IsVisible  = manual;
        StandardHelp.IsVisible = !manual;
        if (keepPosition) KeepWindowPosition(pos);
    }

    private async Task PickFile()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Выберите файл Excel",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("Excel") { Patterns = ["*.xlsx", "*.xlsm"] }],
        });
        var file = files.FirstOrDefault();
        if (file is not null) PathBox.Text = file.Path.LocalPath;
    }

    private async Task TryImport()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { ShowError("Укажите название коллекции"); NameBox.Focus(); return; }

        var parentId = _groups.Count > 0 ? _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id : null;
        var repo     = CurrentRepo;
        var dbId     = CurrentDbId;

        if (repo.CollectionNameExists(name, parentId))
        {
            ShowError("Коллекция с таким названием уже существует");
            NameBox.Focus();
            return;
        }

        var path = PathBox.Text?.Trim() ?? "";
        if (path.Length == 0) { ShowError("Укажите файл для импорта"); return; }

        ExcelImporter.Spec spec;
        if (TypeCombo.SelectedIndex == 1)   // Ручной
        {
            if (!int.TryParse(HeaderRowBox.Text?.Trim(), out var headerRow) || headerRow < 1)
            {
                ShowError("Строка заголовков: укажите число ≥ 1");
                return;
            }
            var sheet = string.IsNullOrWhiteSpace(SheetBox.Text) ? null : SheetBox.Text!.Trim();
            spec = new ExcelImporter.Spec(path, sheet, headerRow,
                TitleColBox.Text?.Trim() ?? "", DescColBox.Text?.Trim() ?? "");
        }
        else                                 // Стандартный
        {
            spec = new ExcelImporter.Spec(path, SheetName: null, HeaderRow: 1,
                TitleColumn: "1", DescColumn: "2");
        }

        SetBusy(true);

        ExcelImportData data;
        try
        {
            data = await Task.Run(() => ExcelImporter.Parse(spec));
        }
        catch (ExcelImportException ex) { SetBusy(false); ShowError(ex.Message); return; }
        catch                           { SetBusy(false); ShowError("Не удалось прочитать файл."); return; }

        int collectionId;
        try
        {
            collectionId = repo.ImportExcelCollection(name, parentId, data);
        }
        catch { SetBusy(false); ShowError("Не удалось импортировать данные."); return; }

        Close(new ImportExportResult(dbId, collectionId));
    }

    private void SetBusy(bool busy)
    {
        ConfirmBtn.IsEnabled = !busy;
        CancelBtn.IsEnabled  = !busy;
        BrowseBtn.IsEnabled  = !busy;
    }

    private void ShowError(string message)
    {
        var pos = Position;
        ErrorText.Text        = message;
        ErrorBanner.IsVisible = true;
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.IsVisible = false;
        KeepWindowPosition(pos);
    }

    // Держим позицию окна при пересчёте высоты (SizeToContent), чтобы оно не «прыгало».
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }
}
