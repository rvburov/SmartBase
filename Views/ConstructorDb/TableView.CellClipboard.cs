using Avalonia.Controls;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Копирование, очистка и вставка значений выделенных ячеек — три действия, общие для трех
/// контекстных меню (ячейки, строки, столбца) и трех горячих клавиш.
///
/// Все они работают по ОДНОМУ прямоугольнику: меню строки и столбца перед вызовом просто
/// расширяют выделение на всю строку или весь столбец, поэтому отдельной логики «скопировать
/// строку» и «скопировать столбец» не существует.
///
/// Запись всегда идет пакетным путем репозитория (одна транзакция, одна строка журнала со
/// счетом): обычная правка ячейки дает около 18 значений в секунду, и диапазон в пару тысяч
/// ячеек подвесил бы окно на минуты.
/// </summary>
public partial class TableView
{
    /// <summary>Сколько ячеек очищается молча. Больше — спрашиваем, если включена настройка
    /// «Спрашивать подтверждение при удалении»: стереть полсотни значений одним нажатием и
    /// заметить это потом — дороже, чем лишний вопрос.</summary>
    private const int ClearConfirmThreshold = 10;

    // ── Копирование ───────────────────────────────────────────────────────────

    /// <summary>Значения выделенных ячеек в буфер обмена, в формате Excel (табуляция между
    /// столбцами, перевод строки между строками).</summary>
    public async Task CopyCellsAsync()
    {
        if (_cellRange is not { } range || _repository is null) return;

        var rows = new List<IReadOnlyList<string>>();
        for (var r = range.Top; r <= range.Bottom; r++)
        {
            if (RowAt(r) is not { } row) continue;
            var line = new List<string>();
            for (var c = range.Left; c <= range.Right; c++)
                if (IsDataColumn(c))
                    line.Add(CellText(row, _colDefs[c]));
            if (line.Count > 0) rows.Add(line);
        }
        if (rows.Count == 0) return;

        var text  = TableClipboardFormat.Build(rows);
        var count = rows.Sum(r => r.Count);

        if (!await SetClipboardTextAsync(text)) return;

        // След в журнале: копирование выносит данные наружу так же, как выгрузка в файл.
        try { _repository.LogCellsCopied(_collectionId, count); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { }
    }

    // Буфер может быть занят другой программой — на Windows это штатный отказ. Без своего
    // перехвата исключение всплыло бы глобальным «Непредвиденная ошибка», хотя случилось
    // всего лишь «не удалось скопировать».
    private async Task<bool> SetClipboardTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return false;

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception)
        {
            AppNotifications.Error("Не удалось скопировать: буфер обмена занят другой программой.");
            return false;
        }
    }

    private async Task<string?> GetClipboardTextAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return null;

        try { return await clipboard.GetTextAsync(); }
        catch (Exception)
        {
            AppNotifications.Error("Не удалось прочитать буфер обмена: он занят другой программой.");
            return null;
        }
    }

    // ── Очистка ───────────────────────────────────────────────────────────────

    /// <summary>Стереть значения выделенных ячеек. Сами записи и столбцы остаются — их удаляет
    /// пункт «Удалить».</summary>
    public async Task ClearCellsAsync()
    {
        if (!HasCellSelection || _repository is null) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }

        var cells = SelectedCells();

        // «Название» очистка не трогает: запись без названия превращается в «Без названия» и
        // теряется в списках, боковой панели и связях. Стереть его можно только осознанно —
        // правкой самой записи. Вставке это не мешает: там название заменяется на другое.
        var titles  = cells.Count(t => t.Column.Key == TableRowVm.TitleKey);
        var targets = cells
            .Where(t => t.Column.Key != TableRowVm.TitleKey
                        && TablePastePlanner.IsWritable(PasteColumnOf(t.Column)))
            .ToList();

        if (targets.Count == 0)
        {
            if (titles > 0) AppNotifications.Info("Названия записей очисткой не стираются.");
            return;
        }

        if (targets.Count > ClearConfirmThreshold && ConstructorSettings.ConfirmDelete)
        {
            if (VisualRoot is not Window win) return;
            var dlg = new ConfirmDialog(
                $"Очистить {targets.Count} {CellsPlural(targets.Count)}? Записи и столбцы останутся на месте.",
                confirmText: "Очистить");
            if (await dlg.ShowDialog<bool>(win) != true) return;
        }

        WriteCells(targets.Select(t => (t.Row, t.Column, Value: "")).ToList());

        if (titles > 0) AppNotifications.Info("Названия записей очисткой не стираются.");
    }

    // ── Вставка ───────────────────────────────────────────────────────────────

    /// <summary>Вставить значения из буфера. Выделенная ячейка — левый верхний угол блока,
    /// дальше он разливается вниз и вправо, как в Excel.</summary>
    public async Task PasteCellsAsync()
    {
        if (_cellRange is not { } range || _repository is null) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }

        var text = await GetClipboardTextAsync();
        if (string.IsNullOrEmpty(text)) return;

        var values = TableClipboardFormat.Parse(text);
        if (values.Count == 0) return;

        // Точка вставки — левый верхний угол выделения; размер выделения нужен планировщику,
        // чтобы размножить по нему блок (одно значение — на все выделенные ячейки, пара
        // значений — парами по кругу), как это делает Excel.
        var columns = _colDefs.Select(PasteColumnOf).ToList();
        var plan    = TablePastePlanner.Plan(values, range.Top, range.Left, columns, VisibleRowCount,
                                             selectionRows:    range.Bottom - range.Top  + 1,
                                             selectionColumns: range.Right  - range.Left + 1);

        var writes = new List<(TableRowVm Row, TableColumnDef Column, string Value)>();
        foreach (var cell in plan.Cells)
        {
            if (RowAt(cell.Row) is not { } row) continue;
            writes.Add((row, _colDefs[cell.Column], cell.StorageValue));
        }

        var written = WriteCells(writes);
        ReportPaste(written, plan.SkippedCount);
    }

    // Столбец таблицы глазами вставки: планировщик не должен знать про ViewModels.
    // Скрытая колонка для вставки равна read-only: блок разливается ПО ПОЗИЦИЯМ, и значение,
    // попавшее на скрытую колонку, ушло бы в невидимое поле — человек не увидел бы, что записал.
    private PasteColumn PasteColumnOf(TableColumnDef def) =>
        new(def.Key, def.FieldType,
            // IsReadOnly у колонки означает «нет текстового редактора», а не «значение менять
            // нельзя»: чекбокс правится кликом и из буфера заполняется штатно («да», «1», «✓»).
            // Без этой оговорки вставка и очистка молча обходили бы все колонки-галочки.
            (def.IsReadOnly && def.FieldType != "checkbox") || _hiddenColumns.Contains(def.Key),
            def.Options, def.NumberFormat.DecimalSeparator);

    // ── Запись ────────────────────────────────────────────────────────────────

    // Одним пакетом: значения полей и названия записей разделяются здесь, потому что «Название»
    // живет в самой записи, а не в таблице значений.
    private int WriteCells(IReadOnlyList<(TableRowVm Row, TableColumnDef Column, string Value)> writes)
    {
        if (writes.Count == 0 || _repository is null) return 0;

        var cells  = new List<ConstructorRepository.CellWrite>();
        var titles = new List<(int RecordId, string Title)>();

        foreach (var (row, column, value) in writes)
        {
            if (column.Key == TableRowVm.TitleKey) titles.Add((row.RecordId, value));
            else if (int.TryParse(column.Key, out var fieldId))
                cells.Add(new ConstructorRepository.CellWrite(row.RecordId, fieldId, value));
        }

        int written;
        try { written = _repository.SetCellsBulk(_collectionId, cells, titles); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return 0; }

        if (written > 0) ReloadRows();   // строки перечитываем целиком: правок много и они разные
        return written;
    }

    private void ReportPaste(int written, int skipped)
    {
        if (written == 0 && skipped == 0) return;

        var message = written > 0
            ? $"Вставлено {written} {ValuesPlural(written)}"
            : "Вставлять было нечего";
        if (skipped > 0) message += $", пропущено {skipped}";
        AppNotifications.Info(message + ".");
    }

    // ── Пункты меню ───────────────────────────────────────────────────────────

    /// <summary>
    /// Группа «Копировать / Очистить / Вставить текст» — одна и та же в меню ячейки, строки и
    /// столбца. <paramref name="select"/> выделяет то, над чем меню открыли: для ячейки это уже
    /// сделано правым кликом, для строки и столбца — расширяет прямоугольник на всю строку или
    /// весь столбец. Дальше все три действия работают по общему выделению.
    /// </summary>
    private void AddCellValueItems(ContextMenu menu, Action select)
    {
        menu.Items.Add(new Separator());

        var copy = new MenuItem { Header = Controls.Components.MenuIcons.CopyTextHeader() };
        copy.Click += (_, _) => { select(); _ = CopyCellsAsync(); };
        menu.Items.Add(copy);

        var paste = new MenuItem { Header = Controls.Components.MenuIcons.PasteTextHeader() };
        Guarded(paste, PermAction.EditRecord, () => { select(); _ = PasteCellsAsync(); });
        menu.Items.Add(paste);

        var clear = new MenuItem { Header = Controls.Components.MenuIcons.ClearTextHeader() };
        Guarded(clear, PermAction.EditRecord, () => { select(); _ = ClearCellsAsync(); });
        menu.Items.Add(clear);
    }

    // ── Меню самой ячейки ─────────────────────────────────────────────────────

    // Меню ячейки вешаем на саму ячейку в момент правого клика: контекстное меню открывается на
    // ОТПУСКАНИИ кнопки, так что к этому моменту оно уже на месте, а запрос всплывает от ячейки
    // и до строки не доходит — меню записи поверх не появляется.
    //
    // Меню носит ровно одна ячейка: у прежней снимаем. Иначе переиспользованная при прокрутке
    // ячейка открывала бы меню, собранное для чужой записи и чужого столбца.
    private DataGridCell? _cellMenuHost;

    private void ShowCellMenuOn(DataGridCell cell, TableRowVm row, TableColumnDef def)
    {
        if (_cellMenuHost is { } prev && !ReferenceEquals(prev, cell)) prev.ContextMenu = null;

        _cellMenuHost    = cell;
        cell.ContextMenu = BuildCellContextMenu(row, def);
    }

    /// <summary>
    /// Меню ячейки: только то, что относится к значениям, — про саму запись есть свое меню на
    /// столбце «№». Выделение к этому моменту уже стоит (правый клик его поставил), поэтому
    /// пункты работают по нему и ничего не переустанавливают.
    /// </summary>
    private ContextMenu BuildCellContextMenu(TableRowVm row, TableColumnDef def)
    {
        var menu = new ContextMenu();

        // Шапка со свойством: меню ячейки и меню записи должны различаться с первого взгляда.
        menu.Items.Add(MakeMenuHeaderItem(
            Controls.Components.MenuIcons.PropertyMenuHeader(def.Header, def.FieldType)));

        AddCellValueItems(menu, () => { });

        menu.Items.Add(new Separator());

        // Дорога к карточке: клик по ячейке больше не открывает запись (он выделяет ячейку),
        // поэтому явный пункт обязателен — иначе до записи добираться только через «№».
        var open = new MenuItem { Header = Controls.Components.MenuIcons.EditHeader() };
        open.Click += (_, _) => { if (row.RecordId > 0) RecordClicked?.Invoke(this, row.RecordId); };
        menu.Items.Add(open);

        return menu;
    }

    /// <summary>Выделить все ячейки строки — для пунктов меню строки.</summary>
    private void SelectWholeRow(DataGridRow row)
    {
        if (row.DataContext is not TableRowVm vm || vm.IsAddRow) return;
        var index = vm.RowNumber - 1;
        SetCellSelection(new CellRange(index, FirstDataColumn, index, LastDataColumn));
    }

    /// <summary>Выделить все ячейки столбца — для пунктов меню заголовка.</summary>
    private void SelectWholeColumn(string columnKey)
    {
        var index = _colDefs.FindIndex(d => d.Key == columnKey);
        if (!IsDataColumn(index)) return;

        var rows = VisibleRowCount;
        if (rows <= 0) return;
        SetCellSelection(new CellRange(0, index, rows - 1, index));
    }

    private static string CellsPlural(int n) => Plural(n, "ячейку", "ячейки", "ячеек");
    private static string ValuesPlural(int n) => Plural(n, "значение", "значения", "значений");

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        if (mod100 is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }
}
