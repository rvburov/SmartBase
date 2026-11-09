using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Components;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Importers.TableSources;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

/// <summary>Введенные значения формы — чтобы переоткрыть окно с теми же данными после ошибки.</summary>
public record ImportDraft(string Path, string Name, int DbId, int? ParentId,
                          bool Manual, string Sheet, string HeaderRow, string DataRow, string TitleCol,
                          bool MergeSheets, bool SplitSheets, bool FolderMode,
                          string ExcludeSheets, string ExcludeRows, string ExcludeCols);

/// <summary>Готовая заявка на импорт: спецификация чтения + куда создавать коллекцию + черновик формы.</summary>
public record ImportRequest(ImportSpec Spec, int DbId, int? ParentId, string Name, ImportDraft Draft);

/// <summary>Введенные значения формы экспорта — для переоткрытия окна после ошибки.</summary>
public record ExportDraft(string Folder, string FileName, int FormatIndex,
                          IReadOnlyList<(int DbId, int ColId)> Selected,
                          bool Manual, string SheetName, string ExcludeColumns,
                          bool NoHeader, bool IncludeHidden, bool SingleFile);

/// <summary>Готовая заявка на экспорт: спецификация выгрузки + черновик формы.</summary>
public record ExportRequest(ExportSpec Spec, ExportDraft Draft);

/// <summary>
/// Окно «Импорт/Экспорт». Комбобокс «Режим» переключает набор полей. Импорт таблиц
/// (.xlsx/.xlsm/.xlsb/.xls/.csv/.json) в новые коллекции; экспорт коллекций (одной,
/// нескольких или всех — выпадающий выбор с поиском и чекбоксами) в файлы
/// .xlsx/.xlsm/.csv/.json. У обоих режимов тип «Стандартный» показывает правила под
/// формат, а «Ручной» раскрывает правую колонку параметров (импорт — лист/строки/
/// столбец/исключения; экспорт — лист/исключение столбцов/чекбоксы). Само действие окно
/// не выполняет: по кнопке оно валидирует ввод и возвращает <see cref="ImportRequest"/>
/// или <see cref="ExportRequest"/> — чтение/запись делает вызывающий (с окном загрузки).
/// При ошибке окно открывается заново с теми же значениями (prefill) и текстом.
/// </summary>
public partial class ImportExportDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private List<(string Label, int? Id)> _groups = [];

    // Пикер коллекций (экспорт): доступные строки и текущий выбор.
    private readonly List<CollectionPickerFlyout.Item> _pickerItems;
    private readonly HashSet<(int Db, int Col)> _selectedCols = [];

    private ConstructorRepository CurrentRepo => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Repo;
    private int                   CurrentDbId => _dbs[Math.Max(0, DbCombo.SelectedIndex)].Id;

    public ImportExportDialog(IReadOnlyList<DbOption> dbs, int currentDbId,
                              ImportDraft? prefill = null, string? error = null,
                              ExportDraft? exportPrefill = null)
    {
        InitializeComponent();
        _dbs = dbs;

        ModeCombo.SelectedIndex = exportPrefill is not null ? 1 : 0;

        TypeCombo.ItemsSource   = new[] { "Стандартный", "Ручной (только Excel)" };
        TypeCombo.SelectedIndex = prefill?.Manual == true ? 1 : 0;

        ExportTypeCombo.ItemsSource   = new[] { "Стандартный", "Ручная выгрузка Excel" };
        ExportTypeCombo.SelectedIndex = exportPrefill?.Manual == true ? 1 : 0;
        FormatCombo.SelectedIndex     = Math.Clamp(exportPrefill?.FormatIndex ?? 0, 0, 3);

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = IndexOfDb(prefill?.DbId ?? currentDbId);

        RebuildLocation();
        if (prefill is not null) SelectLocation(prefill.ParentId);
        DbCombo.SelectionChanged += (_, _) => RebuildLocation();

        // Коллекции для экспорта: все допущенные базы (запертые отфильтрованы вызывающим),
        // из каждой — коллекции, разрешенные к просмотру (право ViewCollection).
        _pickerItems = _dbs
            .SelectMany(d => d.Repo.GetAllCollections()
                .Where(c => c.Id is int cid && (d.Repo.Permissions?.Can(PermAction.ViewCollection, cid) ?? true))
                // Пункт списка — одна строка (TitleText); выбор идет по паре (DbId, Id).
                .Select(c => new CollectionPickerFlyout.Item(
                    d.Id, c.Id!.Value, TitleText.FirstLine(c.Name), TitleText.FirstLine(d.Name))))
            .ToList();

        // «В одну» и «в разные» коллекции взаимоисключающие: отметка одного снимает другой.
        MergeSheetsCheck.IsCheckedChanged += (_, _) =>
        { if (MergeSheetsCheck.IsChecked == true) SplitSheetsCheck.IsChecked = false; };
        SplitSheetsCheck.IsCheckedChanged += (_, _) =>
        { if (SplitSheetsCheck.IsChecked == true) MergeSheetsCheck.IsChecked = false; };
        FolderCheck.IsCheckedChanged += (_, _) => UpdateFolderMode();

        // Восстанавливаем введенные значения ДО подписки на TextChanged (чтобы не сбросить ошибку).
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
        if (exportPrefill is not null)
        {
            ExportFolderBox.Text      = exportPrefill.Folder;
            ExportNameBox.Text        = exportPrefill.FileName;
            ExportSheetBox.Text       = exportPrefill.SheetName;
            ExportExcludeColsBox.Text = exportPrefill.ExcludeColumns;

            NoHeaderCheck.IsChecked      = exportPrefill.NoHeader;
            IncludeHiddenCheck.IsChecked = exportPrefill.IncludeHidden;
            SingleFileCheck.IsChecked    = exportPrefill.SingleFile;

            // Восстанавливаем только еще существующие коллекции (могли удалить).
            foreach (var key in exportPrefill.Selected)
                if (_pickerItems.Any(i => (i.DbId, i.CollectionId) == key))
                    _selectedCols.Add(key);
        }
        UpdateStandardHelp();
        UpdateExportHelp(keepPosition: false);
        UpdatePickerDependent();
        UpdateMode(keepPosition: false);

        ModeCombo.SelectionChanged       += (_, _) => UpdateMode(keepPosition: true);
        TypeCombo.SelectionChanged       += (_, _) => UpdateTypeVisibility(keepPosition: true);
        ExportTypeCombo.SelectionChanged += (_, _) => UpdateTypeVisibility(keepPosition: true);
        FormatCombo.SelectionChanged     += (_, _) => { HideError(); UpdateExportHelp(keepPosition: true); };
        SingleFileCheck.IsCheckedChanged += (_, _) => UpdatePickerDependent();

        BrowseBtn.Click  += async (_, _) =>
        { if (FolderCheck.IsChecked == true) await PickFolder(PathBox); else await PickFile(); };
        ExportBrowseBtn.Click += async (_, _) => await PickFolder(ExportFolderBox);
        CollectionPickerBtn.Click += (_, _) => ShowCollectionPicker();
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => TryConfirm();

        NameBox.TextChanged         += (_, _) => HideError();
        PathBox.TextChanged         += (_, _) => { HideError(); UpdateStandardHelp(); };
        ExportFolderBox.TextChanged += (_, _) => HideError();
        ExportNameBox.TextChanged   += (_, _) => HideError();

        if (!string.IsNullOrEmpty(error)) NoticeBanner.Show(error);
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

    // Режим «Импорт»/«Экспорт»: переключаем набор полей левой колонки и подпись кнопки,
    // затем пересчитываем правую колонку (у каждого режима своя панель ручного типа).
    private void UpdateMode(bool keepPosition)
    {
        var pos    = Position;
        var export = ModeCombo.SelectedIndex == 1;
        ImportFields.IsVisible = !export;
        ExportFields.IsVisible = export;
        ConfirmBtn.Content     = export ? "Экспортировать" : "Импортировать";
        if (NoticeBanner.IsVisible) NoticeBanner.Hide();
        UpdateTypeVisibility(keepPosition: false);
        if (keepPosition) KeepWindowPosition(pos);
    }

    // Переключаем пояснение (стандартный) и колонку параметров (ручной, справа — окно
    // расширяется вправо); заголовок левой колонки и линия между колонками видны,
    // только когда колонок две. Правая колонка своя у каждого режима. Держим позицию окна.
    private void UpdateTypeVisibility(bool keepPosition)
    {
        var pos    = Position;
        var export = ModeCombo.SelectedIndex == 1;
        var manual = export ? ExportTypeCombo.SelectedIndex == 1 : TypeCombo.SelectedIndex == 1;

        ManualPanel.IsVisible       = manual && !export;
        ExportManualPanel.IsVisible = manual && export;
        MainHeader.IsVisible        = manual;
        ColumnSeparator.IsVisible   = manual;
        StandardHelp.IsVisible      = !manual;
        ExportHelp.IsVisible        = !manual;
        if (keepPosition) KeepWindowPosition(pos);
    }

    // Режим папки: путь — к папке, названия коллекций берутся из имен файлов.
    private void UpdateFolderMode()
    {
        var folder = FolderCheck.IsChecked == true;
        PathBox.Watermark = folder ? "Путь к папке с файлами" : "Путь к файлу (Excel, CSV, JSON)";
        NameBox.IsEnabled = !folder;
        NameBox.Watermark = folder ? "Названия — из имен файлов" : "Введите название";
    }

    // Пояснение стандартного типа — под формат выбранного файла.
    private void UpdateStandardHelp()
    {
        var pos = Position;
        var ext = System.IO.Path.GetExtension(PathBox.Text?.Trim() ?? "").ToLowerInvariant();
        StandardHelp.Text = ext switch
        {
            ".xlsx" or ".xlsm" or ".xlsb" or ".xls" =>
                "Excel: берется первый лист. Первая строка — заголовки (имена свойств). " +
                "Столбец 1 → название записи, остальные столбцы → текстовые свойства. Данные — " +
                "со второй строки. Скрытые строки, столбцы и листы тоже импортируются. " +
                "Колонка «Описание» создается пустой в конце.",
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

    // Пояснение стандартного типа экспорта — под выбранный формат.
    private void UpdateExportHelp(bool keepPosition)
    {
        var pos = Position;
        ExportHelp.Text = FormatCombo.SelectedIndex switch
        {
            0 or 1 =>
                "Excel: каждая коллекция — свой файл, лист назван по коллекции. Выгружаются " +
                "видимые столбцы в порядке таблицы; числа и даты — настоящими ячейками, " +
                "чекбоксы — ИСТИНА/ЛОЖЬ. Коллекции длиннее 1 048 576 строк продолжаются " +
                "на листах «Имя 2», «Имя 3»… Занятые имена файлов получают суффикс «(2)».",
            2 =>
                "CSV: разделитель «;», кодировка UTF-8 с BOM — Excel открывает кириллицу " +
                "корректно. Каждая коллекция — свой файл; выгружаются видимые столбцы в порядке " +
                "таблицы, даты — как дд.мм.гггг, отмеченные чекбоксы — 1. Занятые имена файлов " +
                "получают суффикс «(2)».",
            _ =>
                "JSON: массив объектов, ключи — названия столбцов. Числа и чекбоксы — " +
                "настоящими типами, даты — в формате хранения (ISO). Каждая коллекция — свой " +
                "файл; выгружаются видимые столбцы. Занятые имена файлов получают суффикс «(2)».",
        };
        if (keepPosition) KeepWindowPosition(pos);
    }

    // ── Пикер коллекций ───────────────────────────────────────────────────────

    private void ShowCollectionPicker()
    {
        var picker = new CollectionPickerFlyout(_pickerItems, _selectedCols);
        picker.Done += (_, _) => { HideError(); UpdatePickerDependent(); };
        picker.ShowAt(CollectionPickerBtn);
    }

    // Текст кнопки-комбо и доступность полей, зависящих от числа выбранных коллекций:
    // «Название файла» — когда файл один (одна коллекция или «в один файл»),
    // «Лист» — только при одной коллекции.
    private void UpdatePickerDependent()
    {
        CollectionPickerText.Text = _selectedCols.Count switch
        {
            0 => "Выберите коллекции",
            1 => _pickerItems.FirstOrDefault(i => _selectedCols.Contains((i.DbId, i.CollectionId)))?.Name ?? "1 коллекция",
            _ when _selectedCols.Count == _pickerItems.Count && _pickerItems.Count > 0 => "Все коллекции",
            var n => $"{n} {PluralCollections(n)}",
        };

        var single     = _selectedCols.Count == 1;
        var singleFile = SingleFileCheck.IsChecked == true;

        ExportNameBox.IsEnabled = single || singleFile;
        ExportNameBox.Watermark = single || singleFile
            ? (singleFile && !single ? "Введите название (пусто — «Экспорт»)" : "Введите название (пусто — имя коллекции)")
            : "Названия — из имен коллекций";

        ExportSheetBox.IsEnabled = single;
        ExportSheetBox.Watermark = single ? "Название листа (пусто — имя коллекции)" : "Доступно при одной коллекции";
    }

    private static string PluralCollections(int n) => (n % 100 is >= 11 and <= 14 ? 0 : n % 10) switch
    {
        1           => "коллекция",
        2 or 3 or 4 => "коллекции",
        _           => "коллекций",
    };

    // ── Пикеры файлов/папок ───────────────────────────────────────────────────

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

    private async Task PickFolder(TextBox target)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = "Выберите папку",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is not null) target.Text = folder.Path.LocalPath;
    }

    // ── Подтверждение ─────────────────────────────────────────────────────────

    // Валидируем ввод и возвращаем заявку. Ошибки ввода показываем в баннере (окно остается).
    private void TryConfirm()
    {
        if (ModeCombo.SelectedIndex == 1) { TryConfirmExport(); return; }

        var manual = TypeCombo.SelectedIndex == 1;
        // Режим папки — общий (живет при поле «Файл»); листовые чекбоксы — часть ручного
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

    // Валидируем ввод экспорта и возвращаем заявку. Чекбоксы и поля ручной панели
    // действуют только в ручном типе (в стандартном их состояние игнорируется).
    private void TryConfirmExport()
    {
        var manual   = ExportTypeCombo.SelectedIndex == 1;
        var folder   = ExportFolderBox.Text?.Trim() ?? "";
        var fileName = ExportNameBox.Text?.Trim() ?? "";
        var format   = (ExportFormat)Math.Max(0, FormatCombo.SelectedIndex);

        var single        = manual && SingleFileCheck.IsChecked == true;
        var noHeader      = manual && NoHeaderCheck.IsChecked == true;
        var includeHidden = manual && IncludeHiddenCheck.IsChecked == true;
        var sheet         = manual ? ExportSheetBox.Text?.Trim() ?? "" : "";
        var exclude       = manual ? ExportExcludeColsBox.Text?.Trim() ?? "" : "";

        if (folder.Length == 0)        { ShowError("Укажите папку для экспорта"); return; }
        if (!Directory.Exists(folder)) { ShowError("Папка не найдена"); return; }
        if (_selectedCols.Count == 0)  { ShowError("Выберите хотя бы одну коллекцию"); return; }
        if (manual && !ExportPlanner.IsExcel(format))
        { ShowError("Ручная выгрузка доступна только для форматов Excel"); return; }
        if (fileName.Length > 0 && fileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        { ShowError("Недопустимые символы в названии файла: \\ / : * ? \" < > |"); return; }
        if (sheet.Length > 31 || sheet.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0)
        { ShowError("Название листа: до 31 символа, без символов [ ] : * ? / \\"); return; }

        // Коллекции — в порядке пикера (базы и их коллекции как в списке выбора).
        var refs = _pickerItems
            .Where(i => _selectedCols.Contains((i.DbId, i.CollectionId)))
            .Select(i => new ExportCollectionRef(i.DbId, i.CollectionId, i.Name, i.DbName))
            .ToList();

        var spec = new ExportSpec(folder, format, refs,
            fileName.Length > 0 && (refs.Count == 1 || single) ? fileName : null,
            manual,
            sheet.Length > 0 && refs.Count == 1 ? sheet : null,
            exclude, noHeader, includeHidden, single);

        var draft = new ExportDraft(folder, fileName, FormatCombo.SelectedIndex,
                                    _selectedCols.ToList(), manual, sheet, exclude,
                                    noHeader, includeHidden, single);
        Close(new ExportRequest(spec, draft));
    }

    private void ShowError(string message)
    {
        var pos = Position;
        NoticeBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!NoticeBanner.IsVisible) return;
        var pos = Position;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }

    // Держим позицию окна при пересчете высоты (SizeToContent), чтобы оно не «прыгало».
    private void KeepWindowPosition(PixelPoint pos) =>
        SmartBase.Controls.Behaviors.WindowPlacement.KeepPositionAfterLayout(this, pos);
}
