using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DataBase.Core.Importers;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

/// <summary>Введённые значения формы — чтобы переоткрыть окно с теми же данными после ошибки.</summary>
public record ImportDraft(string Path, string Name, int DbId, int? ParentId,
                          bool Manual, string Sheet, string HeaderRow, string DataRow, string TitleCol);

/// <summary>Готовая заявка на импорт: спецификация чтения + куда создавать коллекцию + черновик формы.</summary>
public record ImportRequest(ExcelImporter.Spec Spec, int DbId, int? ParentId, string Name, ImportDraft Draft);

/// <summary>
/// Окно «Импорт/Экспорт». Реализован импорт таблицы (.xlsx/.xlsm/.csv) в новую коллекцию;
/// режим «Экспорт» виден, но отключён. Поля «название / база данных / расположение» работают
/// как в <see cref="CreateItemDialog"/>. Сам импорт окно не выполняет: по «Импортировать» оно
/// валидирует ввод и возвращает <see cref="ImportRequest"/> — чтение/запись делает вызывающий
/// (с окном загрузки). При ошибке окно открывается заново с теми же значениями (prefill) и текстом.
/// </summary>
public partial class ImportExportDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private List<(string Label, int? Id)> _groups = [];

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    public ImportExportDialog(IReadOnlyList<DbOption> dbs, int currentDbId,
                              ImportDraft? prefill = null, string? error = null)
    {
        InitializeComponent();
        _dbs = dbs;

        ModeCombo.SelectedIndex = 0;   // Импорт (Экспорт отключён)

        TypeCombo.ItemsSource   = new[] { "Стандартный", "Ручной" };
        TypeCombo.SelectedIndex = prefill?.Manual == true ? 1 : 0;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(prefill?.DbId ?? currentDbId);

        RebuildLocation();
        if (prefill is not null) SelectLocation(prefill.ParentId);
        DbCombo.SelectionChanged += (_, _) => RebuildLocation();

        UpdateTypeVisibility(keepPosition: false);
        TypeCombo.SelectionChanged += (_, _) => UpdateTypeVisibility(keepPosition: true);

        // Восстанавливаем введённые значения ДО подписки на TextChanged (чтобы не сбросить ошибку).
        if (prefill is not null)
        {
            PathBox.Text      = prefill.Path;
            NameBox.Text      = prefill.Name;
            SheetBox.Text     = prefill.Sheet;
            HeaderRowBox.Text = prefill.HeaderRow;
            DataRowBox.Text   = prefill.DataRow;
            TitleColBox.Text  = prefill.TitleCol;
        }

        BrowseBtn.Click  += async (_, _) => await PickFile();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => TryConfirm();

        NameBox.TextChanged += (_, _) => HideError();
        PathBox.TextChanged += (_, _) => HideError();

        if (!string.IsNullOrEmpty(error)) ErrorBanner.Show(error);
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

    private void SelectLocation(int? parentId)
    {
        var idx = _groups.FindIndex(g => g.Id == parentId);
        LocationCombo.SelectedIndex = idx >= 0 ? idx : 0;
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
            Title          = "Выберите файл",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Таблицы (Excel, CSV)") { Patterns = ["*.xlsx", "*.xlsm", "*.csv"] },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is not null) PathBox.Text = file.Path.LocalPath;
    }

    // Валидируем ввод и возвращаем заявку на импорт. Ошибки ввода показываем в баннере (окно остаётся).
    private void TryConfirm()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) { ShowError("Укажите название коллекции"); NameBox.Focus(); return; }

        var parentId = _groups.Count > 0 ? _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id : null;
        var dbId     = CurrentDbId;

        if (CurrentRepo.CollectionNameExists(name, parentId))
        {
            ShowError("Коллекция с таким названием уже существует");
            NameBox.Focus();
            return;
        }

        var path = PathBox.Text?.Trim() ?? "";
        if (path.Length == 0)        { ShowError("Укажите файл для импорта"); return; }
        if (!File.Exists(path))      { ShowError("Файл не найден"); return; }

        var manual        = TypeCombo.SelectedIndex == 1;
        var sheet         = "";
        var headerRowText = "";
        var dataRowText   = "";
        var titleColText  = "1";
        ExcelImporter.Spec spec;

        if (manual)
        {
            sheet         = SheetBox.Text?.Trim() ?? "";
            headerRowText = HeaderRowBox.Text?.Trim() ?? "";
            dataRowText   = DataRowBox.Text?.Trim() ?? "";
            titleColText  = TitleColBox.Text?.Trim() ?? "";

            if (!int.TryParse(headerRowText, out var headerRow) || headerRow < 1)
            { ShowError("Строка заголовков: укажите число ≥ 1"); return; }
            if (!int.TryParse(dataRowText, out var dataRow) || dataRow < 1)
            { ShowError("Строка данных: укажите число ≥ 1"); return; }

            spec = new ExcelImporter.Spec(path,
                string.IsNullOrWhiteSpace(sheet) ? null : sheet,
                headerRow, dataRow, titleColText);
        }
        else
        {
            spec = new ExcelImporter.Spec(path, SheetName: null, HeaderRow: 1, DataRow: 2, TitleColumn: "1");
        }

        var draft = new ImportDraft(path, name, dbId, parentId, manual, sheet, headerRowText, dataRowText, titleColText);
        Close(new ImportRequest(spec, dbId, parentId, name, draft));
    }

    private void ShowError(string message)
    {
        var pos = Position;
        ErrorBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.Hide();
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
