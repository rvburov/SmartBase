using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DataBase.Core.Importers;
using DataBase.Core.Importers.TableSources;
using DataBase.Database.Repositories;

namespace DataBase.Controls.Dialogs;

/// <summary>Введённые значения формы — чтобы переоткрыть окно с теми же данными после ошибки.</summary>
public record ImportDraft(string Path, string Name, int DbId, int? ParentId,
                          bool Manual, string Sheet, string HeaderRow, string DataRow, string TitleCol,
                          bool MergeSheets, bool SplitSheets, bool FolderMode,
                          string ExcludeSheets, string ExcludeRows, string ExcludeCols);

/// <summary>Готовая заявка на импорт: спецификация чтения + куда создавать коллекцию + черновик формы.</summary>
public record ImportRequest(ImportSpec Spec, int DbId, int? ParentId, string Name, ImportDraft Draft);

/// <summary>
/// Окно «Импорт/Экспорт». Импорт таблиц (.xlsx/.xlsm/.xlsb/.xls/.csv/.json) в новые коллекции;
/// режим «Экспорт» виден, но отключён. Тип «Стандартный» показывает правила импорта под формат
/// выбранного файла; «Ручной» — только для Excel (лист/строки/столбец + чекбоксы: все листы в
/// одну/разные коллекции, все файлы из папки). Сам импорт окно не выполняет: по «Импортировать»
/// оно валидирует ввод и возвращает <see cref="ImportRequest"/> — чтение/запись делает вызывающий
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

        TypeCombo.ItemsSource   = new[] { "Стандартный", "Ручной (только Excel)" };
        TypeCombo.SelectedIndex = prefill?.Manual == true ? 1 : 0;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(prefill?.DbId ?? currentDbId);

        RebuildLocation();
        if (prefill is not null) SelectLocation(prefill.ParentId);
        DbCombo.SelectionChanged += (_, _) => RebuildLocation();

        UpdateTypeVisibility(keepPosition: false);
        TypeCombo.SelectionChanged += (_, _) => UpdateTypeVisibility(keepPosition: true);

        // «В одну» и «в разные» коллекции взаимоисключающие: отметка одного снимает другой.
        MergeSheetsCheck.IsCheckedChanged += (_, _) =>
        { if (MergeSheetsCheck.IsChecked == true) SplitSheetsCheck.IsChecked = false; };
        SplitSheetsCheck.IsCheckedChanged += (_, _) =>
        { if (SplitSheetsCheck.IsChecked == true) MergeSheetsCheck.IsChecked = false; };
        FolderCheck.IsCheckedChanged += (_, _) => UpdateFolderMode();

        // Восстанавливаем введённые значения ДО подписки на TextChanged (чтобы не сбросить ошибку).
        if (prefill is not null)
        {
            PathBox.Text           = prefill.Path;
            NameBox.Text           = prefill.Name;
            SheetBox.Text          = prefill.Sheet;
            HeaderRowBox.Text      = prefill.HeaderRow;
            DataRowBox.Text        = prefill.DataRow;
            TitleColBox.Text       = prefill.TitleCol;
            ExcludeSheetsBox.Text  = prefill.ExcludeSheets;
            ExcludeRowsBox.Text    = prefill.ExcludeRows;
            ExcludeColsBox.Text    = prefill.ExcludeCols;

            MergeSheetsCheck.IsChecked = prefill.MergeSheets;
            SplitSheetsCheck.IsChecked = prefill.SplitSheets;
            FolderCheck.IsChecked      = prefill.FolderMode;
        }
        UpdateStandardHelp();

        BrowseBtn.Click  += async (_, _) =>
        { if (FolderCheck.IsChecked == true) await PickFolder(); else await PickFile(); };
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => TryConfirm();

        NameBox.TextChanged += (_, _) => HideError();
        PathBox.TextChanged += (_, _) => { HideError(); UpdateStandardHelp(); };

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

    // Режим папки: путь — к папке, названия коллекций берутся из имён файлов.
    private void UpdateFolderMode()
    {
        var folder = FolderCheck.IsChecked == true;
        PathBox.Watermark = folder ? "Путь к папке с файлами" : "Путь к файлу (Excel, CSV, JSON)";
        NameBox.IsEnabled = !folder;
        NameBox.Watermark = folder ? "Названия — из имён файлов" : "Введите название";
    }

    // Пояснение стандартного типа — под формат выбранного файла.
    private void UpdateStandardHelp()
    {
        var pos = Position;
        var ext = System.IO.Path.GetExtension(PathBox.Text?.Trim() ?? "").ToLowerInvariant();
        StandardHelp.Text = ext switch
        {
            ".xlsx" or ".xlsm" or ".xlsb" or ".xls" =>
                "Excel: берётся первый лист. Первая строка — заголовки (имена свойств). " +
                "Столбец 1 → название записи, остальные столбцы → текстовые свойства. Данные — " +
                "со второй строки. Скрытые строки, столбцы и листы тоже импортируются. " +
                "Колонка «Описание» создаётся пустой в конце.",
            ".csv" =>
                "CSV: разделитель (запятая, точка с запятой, табуляция) и кодировка " +
                "(UTF-8 / Windows-1251) определяются автоматически. Первая строка — заголовки, " +
                "столбец 1 → название записи, данные — со второй строки. Все значения " +
                "сохраняются как текст.",
            ".json" =>
                "JSON: поддерживаются массив объектов [{…}] и NDJSON (объект в строке). Ключи " +
                "объектов → свойства. Название записи — ключ «Название», name или title, иначе " +
                "первый ключ первого объекта. Вложенные объекты и массивы сохраняются текстом JSON.",
            _ =>
                "Поддерживаются файлы Excel (.xlsx, .xlsm, .xlsb, .xls), CSV и JSON. Укажите " +
                "файл, чтобы увидеть правила импорта для его формата.",
        };
        KeepWindowPosition(pos);   // высота пояснения меняется — окно не должно «прыгать»
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
                new FilePickerFileType("Таблицы (Excel, CSV, JSON)")
                { Patterns = ["*.xlsx", "*.xlsm", "*.xlsb", "*.xls", "*.csv", "*.json"] },
            ],
        });
        var file = files.FirstOrDefault();
        if (file is not null) PathBox.Text = file.Path.LocalPath;
    }

    private async Task PickFolder()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Выберите папку",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null) PathBox.Text = folder.Path.LocalPath;
    }

    // Валидируем ввод и возвращаем заявку на импорт. Ошибки ввода показываем в баннере (окно остаётся).
    private void TryConfirm()
    {
        var manual = TypeCombo.SelectedIndex == 1;
        // Режим папки — общий (живёт при поле «Файл»); листовые чекбоксы — часть ручного
        // типа, в стандартном их состояние игнорируется.
        var folder = FolderCheck.IsChecked == true;
        var merge  = manual && MergeSheetsCheck.IsChecked == true;
        var split  = manual && SplitSheetsCheck.IsChecked == true;

        var parentId = _groups.Count > 0 ? _groups[Math.Max(0, LocationCombo.SelectedIndex)].Id : null;
        var dbId     = CurrentDbId;
        var path     = PathBox.Text?.Trim() ?? "";
        var name     = NameBox.Text?.Trim() ?? "";

        if (folder)
        {
            if (path.Length == 0)         { ShowError("Укажите папку для импорта"); return; }
            if (!Directory.Exists(path))  { ShowError("Папка не найдена"); return; }
        }
        else
        {
            if (name.Length == 0) { ShowError("Укажите название коллекции"); NameBox.Focus(); return; }
            if (CurrentRepo.CollectionNameExists(name, parentId))
            {
                ShowError("Коллекция с таким названием уже существует");
                NameBox.Focus();
                return;
            }

            if (path.Length == 0)   { ShowError("Укажите файл для импорта"); return; }
            if (!File.Exists(path)) { ShowError("Файл не найден"); return; }
            if (!ImportPlanner.IsSupported(path))
            { ShowError($"Формат файла не поддерживается. Доступны: {ImportPlanner.SupportedList}"); return; }
            if (manual && !ImportPlanner.IsExcel(path))
            { ShowError("Ручной тип доступен только для файлов Excel"); return; }
        }

        var sheet             = "";
        var headerRowText     = "";
        var dataRowText       = "";
        var titleColText      = "1";
        var excludeSheetsText = "";
        var excludeRowsText   = "";
        var excludeColsText   = "";
        var headerRow         = 1;
        var dataRow           = 2;

        if (manual)
        {
            sheet             = SheetBox.Text?.Trim() ?? "";
            headerRowText     = HeaderRowBox.Text?.Trim() ?? "";
            dataRowText       = DataRowBox.Text?.Trim() ?? "";
            titleColText      = TitleColBox.Text?.Trim() ?? "";
            excludeSheetsText = ExcludeSheetsBox.Text?.Trim() ?? "";
            excludeRowsText   = ExcludeRowsBox.Text?.Trim() ?? "";
            excludeColsText   = ExcludeColsBox.Text?.Trim() ?? "";

            if (!int.TryParse(headerRowText, out headerRow) || headerRow < 1)
            { ShowError("Строка заголовков: укажите число ≥ 1"); return; }
            if (!int.TryParse(dataRowText, out dataRow) || dataRow < 1)
            { ShowError("Строка данных: укажите число ≥ 1"); return; }

            // Исключения и столбец названия проверяем сразу — без показа окна загрузки.
            try
            {
                var titleColNum = SourceHelpers.ParseColumn(titleColText, "Столбец названия");
                SourceHelpers.ParseRowExclusions(excludeRowsText);
                if (SourceHelpers.ParseColumnExclusions(excludeColsText).Contains(titleColNum))
                { ShowError("Столбец названия нельзя исключать"); return; }
                if (!string.IsNullOrWhiteSpace(sheet)
                    && SourceHelpers.ParseSheetExclusions(excludeSheetsText).Contains(sheet))
                { ShowError("Лист указан и в поле «Лист», и в «Исключить листы» — уберите одно из двух"); return; }
            }
            catch (ImportException ex) { ShowError(ex.Message); return; }
        }

        var sheets = merge ? SheetMode.MergeAll : split ? SheetMode.SplitAll : SheetMode.Single;
        var spec = new ImportSpec(path, folder, manual,
            manual && !string.IsNullOrWhiteSpace(sheet) ? sheet : null,
            headerRow, dataRow, manual ? titleColText : "1",
            sheets, name,
            excludeSheetsText, excludeRowsText, excludeColsText);

        var draft = new ImportDraft(path, name, dbId, parentId, manual,
                                    sheet, headerRowText, dataRowText, titleColText,
                                    merge, split, folder,
                                    excludeSheetsText, excludeRowsText, excludeColsText);
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
