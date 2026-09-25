using Avalonia.Input;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Выделение ячеек прямоугольником, как в табличном редакторе: клик ставит рамку на одну ячейку,
/// Shift+клик и протягивание мышью растягивают ее до прямоугольника, стрелки двигают, Shift+стрелки
/// растягивают. Выделение СТРОК живет отдельно и переехало на служебный столбец «№»: клик по
/// номеру выделяет строку целиком и открывает запись, Shift и Ctrl по номеру набирают несколько
/// строк — на этом держатся пакетные операции (удаление, дубль, «Расположение»).
///
/// Координаты диапазона — индексы В ПОКАЗАННОЙ таблице (позиция строки и позиция столбца), а не
/// идентификаторы: строки виртуализованы и переиспользуются при прокрутке, привязываться к ним
/// нельзя. Идентификаторы резолвятся в момент операции (см. <see cref="SelectedCellsAsync"/>), а чтобы
/// выделение не указывало на чужие данные после сортировки или сдвига колонки, рядом хранятся
/// метки записи и столбцов (см. <see cref="FollowSelectionIdentity"/>).
///
/// Рамку и тонировку рисует сама сетка в кадре по строкам среза и ключам крайних видимых колонок
/// (<c>PushCellSelection</c>) — отдельного слоя и пересчета геометрии нет.
/// </summary>
public partial class TableView
{
    // Выделение — РОВНО ОДИН прямоугольник (или ничего). Все способы его поставить — клик,
    // Shift-клик, протягивание, стрелки — дают сплошной прямоугольник. Отсюда и главное свойство
    // картинки: рамка всегда одна, «прямоугольника в прямоугольнике» не бывает в принципе.
    private CellRange? _cellRange;

    /// <summary>
    /// Чьи это ячейки: запись под якорем и ключи столбцов якоря и фокуса. Позиции после
    /// сортировки, фильтра, фонового обновления от соседа или сдвига колонки указывают уже на
    /// ДРУГИЕ данные — и Delete стер бы не то, что выделяли. По меткам выделение переезжает за
    /// своими записью и столбцами, а если их больше нет на экране — снимается.
    /// <c>AnchorRecordId</c> = 0 — строка якоря еще не загрузилась, метка допишется позже.
    /// </summary>
    private sealed record SelectionIdentity(int AnchorRecordId, string AnchorKey, string FocusKey);
    private SelectionIdentity? _selectionIdentity;

    /// <summary>Выделены ли сейчас ячейки (для пунктов меню и горячих клавиш).</summary>
    public bool HasCellSelection => _cellRange is not null;

    /// <summary>Размер выделения изменился: подпись «Выделено: 3 × 2» для нижней панели или
    /// null, когда выделена одна ячейка или ничего.</summary>
    public event EventHandler<string?>? CellSelectionChanged;
    private string? _lastSelectionLabel;

    private bool IsCellSelected(int row, int column) =>
        _cellRange is { } range && range.Contains(row, column);

    // ── Границы «настоящих» столбцов ──────────────────────────────────────────
    //
    // Нулевой столбец — служебный «№», последний — «+» для нового свойства. Выделение ячеек
    // живет строго между ними: в служебных столбцах значений нет.
    private int FirstDataColumn => 1;
    private int LastDataColumn  => _colDefs.Count - 2;

    // Скрытые в окне «Отображение» колонки остаются в _colDefs (там держится порядок,
    // фильтры и перемещение) — но для выделения и буфера их нет: копировать и заполнять
    // то, чего человек не видит, нельзя.
    private bool IsDataColumn(int index) =>
        index >= FirstDataColumn && index <= LastDataColumn
        && !_hiddenColumns.Contains(_colDefs[index].Key);

    // ── Установка выделения ───────────────────────────────────────────────────

    /// <summary>Выделить прямоугольник — из клика, клавиш и пунктов меню («вся строка», «весь
    /// столбец»).</summary>
    private void SetCellSelection(CellRange range)
    {
        _cellRange         = range;
        _selectionIdentity = IdentityOf(range);
        ClearRecordSelection();   // либо ячейки, либо строки: иначе Delete был бы двусмысленным
        AfterSelectionChanged();
    }

    /// <summary>Снять выделение ячеек (уход из коллекции, выделение строк и колонок, Escape).</summary>
    public void ClearCellSelection()
    {
        _dragSelecting = false;
        EndDrag();
        if (_cellRange is null) return;
        _cellRange         = null;
        _selectionIdentity = null;
        AfterSelectionChanged();
    }

    // Рамка — часть кадра сетки; наверх уходит только смена подписи счетчика.
    private void AfterSelectionChanged()
    {
        PushCellSelection();
        RaiseSelectionLabel();
    }

    // Источник строк показанной таблицы (полный список или срез отбора и сортировки). Все разрешения
    // позиций в строки идут через него.
    private VirtualRowList? RowsSource => RecordsGrid.Rows;

    private SelectionIdentity? IdentityOf(CellRange range)
    {
        if (range.AnchorColumn < 0 || range.AnchorColumn >= _colDefs.Count
            || range.FocusColumn < 0 || range.FocusColumn >= _colDefs.Count) return null;

        return new SelectionIdentity(CachedRowAt(range.AnchorRow)?.RecordId ?? 0,
                                     _colDefs[range.AnchorColumn].Key,
                                     _colDefs[range.FocusColumn].Key);
    }

    // Подпись только при изменении: пересчет идет и на каждую перестройку строк, а панель на
    // каждый проход перерисовывать незачем.
    private void RaiseSelectionLabel()
    {
        var label = SelectionLabel();
        if (label == _lastSelectionLabel) return;
        _lastSelectionLabel = label;
        CellSelectionChanged?.Invoke(this, label);
    }

    private string? SelectionLabel()
    {
        if (_cellRange is not { } range) return null;
        var columns = range.VisibleColumns(VisibleDataColumns()).Count;
        if ((long)range.RowCount * columns <= 1) return null;
        return $"Выделено: {range.RowCount:N0} × {columns:N0}";
    }

    /// <summary>
    /// Клик по ячейке. Обычный выделяет одну и начинает протягивание; Shift дотягивает
    /// прямоугольник от той ячейки, с которой начали, — одним куском на сколько угодно строк и
    /// столбцов.
    /// </summary>
    private void BeginCellSelection(int row, int column, bool extend)
    {
        if (!IsDataColumn(column)) return;

        SetCellSelection(extend && _cellRange is { } current
            ? current.WithFocus(row, column)   // якорь стоит, двигается только дальний угол
            : CellRange.Single(row, column));

        _dragSelecting = !extend;
    }

    /// <summary>
    /// Выделение следует за своими записью и столбцами (см. <see cref="SelectionIdentity"/>).
    /// Столбцы ищутся по ключу всегда — их порядок знает таблица. Запись — только среди
    /// загруженных строк: пока строка под якорем грузится, судить не о чем, и выделение ждет.
    /// Своя запись не нашлась — выделение снимается: оставить рамку на чужих данных хуже.
    /// </summary>
    private void FollowSelectionIdentity()
    {
        if (_cellRange is not { } range || _selectionIdentity is not { } id) return;

        var anchorCol = _colDefs.FindIndex(d => d.Key == id.AnchorKey);
        var focusCol  = _colDefs.FindIndex(d => d.Key == id.FocusKey);
        var rowCount  = VisibleRowCount;
        if (!IsDataColumn(anchorCol) || !IsDataColumn(focusCol) || rowCount <= 0 || range.Top >= rowCount)
        {
            ClearCellSelection();
            return;
        }

        var anchorRow = range.AnchorRow;
        var focusRow  = range.FocusRow;

        if (CachedRowAt(anchorRow) is { } atAnchor)
        {
            if (id.AnchorRecordId == 0)
                _selectionIdentity = id with { AnchorRecordId = atAnchor.RecordId };
            else if (atAnchor.RecordId != id.AnchorRecordId)
            {
                if (RowsSource?.FindCachedByRecordId(id.AnchorRecordId) is not { } moved)
                {
                    ClearCellSelection();
                    return;
                }
                // Форма выделения сохраняется: сдвигаем прямоугольник целиком за якорем.
                var shift = moved.RowNumber - 1 - anchorRow;
                anchorRow += shift;
                focusRow  += shift;
            }
        }

        var next = new CellRange(Math.Clamp(anchorRow, 0, rowCount - 1), anchorCol,
                                 Math.Clamp(focusRow,  0, rowCount - 1), focusCol);
        if (next != range) _cellRange = next;
    }

    // ── Клавиатура ────────────────────────────────────────────────────────────

    /// <summary>
    /// Клавиши, которые двигают или меняют выделение ячеек: стрелки, Home/End, PageUp/PageDown,
    /// Tab (с Ctrl — к краю таблицы, с Shift — растянуть), Ctrl+A, Escape; Enter и F2 открывают
    /// правку активной ячейки; пробел переключает выделенную галочку. true — клавиша обработана.
    /// Сама арифметика — в <see cref="CellRangeNavigation"/>.
    /// </summary>
    private bool HandleSelectionKey(KeyEventArgs e)
    {
        if (_cellRange is not { } range) return false;

        // Выделить все — единственная переназначаемая клавиша этого обработчика; навигация,
        // Escape и вход в правку вшиты в поведение таблицы и в настройках только показываются.
        if (ShortcutSettings.Matches("table.selectAll", e))
        {
            if (CellRangeNavigation.SelectAll(VisibleRowCount, VisibleDataColumns()) is { } all)
                SetCellSelection(all);
            return true;
        }

        var mods  = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Alt)) return false;
        var ctrl  = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
        var shift = mods.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Escape when mods == KeyModifiers.None:
                ClearCellSelection();
                return true;

            // Правка с клавиатуры — активной ячейки (якоря), как второй клик.
            case Key.Enter or Key.F2 when mods == KeyModifiers.None:
                BeginEdit(range.AnchorRow, range.AnchorColumn, byMouse: false);
                return true;

            // Пробел на выделенной галочке переключает ее (кнопки в ячейке нет — квадрат нарисован).
            case Key.Space when mods == KeyModifiers.None && range.IsSingleCell
                                && ToggleCheckbox(range.AnchorRow, range.AnchorColumn):
                return true;
        }

        CellNavKey? nav = e.Key switch
        {
            Key.Up                   => ctrl ? CellNavKey.CtrlUp    : CellNavKey.Up,
            Key.Down                 => ctrl ? CellNavKey.CtrlDown  : CellNavKey.Down,
            Key.Left                 => ctrl ? CellNavKey.CtrlLeft  : CellNavKey.Left,
            Key.Right                => ctrl ? CellNavKey.CtrlRight : CellNavKey.Right,
            Key.Home                 => ctrl ? CellNavKey.CtrlHome  : CellNavKey.Home,
            Key.End                  => ctrl ? CellNavKey.CtrlEnd   : CellNavKey.End,
            Key.PageUp   when !ctrl  => CellNavKey.PageUp,
            Key.PageDown when !ctrl  => CellNavKey.PageDown,
            Key.Tab      when !ctrl  => shift ? CellNavKey.ShiftTab : CellNavKey.Tab,
            _                        => null,
        };
        if (nav is not { } key) return false;

        var moved = CellRangeNavigation.Navigate(range, key, extend: shift, VisibleRowCount,
                                                 VisibleDataColumns(), PageRows());
        if (moved is not { } next) return true;

        SetCellSelection(next);
        ScrollCellIntoView(next.FocusRow, next.FocusColumn);
        return true;
    }

    // На сколько строк листают PageUp/PageDown: сколько их сейчас на экране.
    private int PageRows() => RecordsGrid.PageRows();

    /// <summary>Прокрутить таблицу к ячейке: арифметика раскладок (Ctrl+End на 10 млн строк — сразу),
    /// страницу заведет само окно.</summary>
    private void ScrollCellIntoView(int row, int column)
    {
        if (row < 0 || row >= VisibleRowCount || column < 0 || column >= _colDefs.Count) return;
        RecordsGrid.ScrollToCell(row, _colDefs[column].Key);
    }

    // ── Протягивание мышью ────────────────────────────────────────────────────

    // Идет протягивание: левая кнопка нажата на ячейке (без Shift) и еще не отпущена. Само
    // протягивание с автопрокруткой — в TableView.GridInput.cs.
    private bool _dragSelecting;

    // ── Разрешение координат в данные ─────────────────────────────────────────

    /// <summary>Строка по позиции, только если она уже загружена; загрузку не заводит.</summary>
    private TableRowVm? CachedRowAt(int index) => RowsSource?.TryGetCached(index);

    /// <summary>Сколько строк с данными показано сейчас (без строки «+»).</summary>
    private int VisibleRowCount => RowsSource is { } source ? Math.Max(0, source.Count - 1) : 0;

    /// <summary>
    /// Сколько строк выделения обрабатывается за один раз. Больше <see cref="SyncRangeRows"/>
    /// строки читаются в фоне порциями с окном прогресса, но и так столбец в миллионы записей —
    /// это минуты работы и сотни мегабайт в памяти; для него человеку предлагается сузить
    /// таблицу фильтром.
    /// </summary>
    private const int MaxRangeRows = 100_000;

    /// <summary>До скольких строк выделение читается сразу, без фона: такие диапазоны в основном
    /// уже лежат в кеше таблицы, а недостающее дочитывается за доли секунды.
    /// Значение живет в <see cref="VirtualRowList"/>: кеш страниц обязан вмещать этот диапазон,
    /// иначе синхронный путь пошел бы читать строки из базы прямо на UI-потоке.</summary>
    private const int SyncRangeRows = VirtualRowList.SyncRangeRows;

    /// <summary>Больше ли строк, чем обрабатывается за раз. Если да — человеку сказано, почему
    /// ничего не произошло и что сделать: молча обработать часть столбца было бы хуже всего.</summary>
    private static bool TooManyRows(int rows)
    {
        if (rows <= MaxRangeRows) return false;
        AppNotifications.Info(
            $"За один раз обрабатывается не больше {MaxRangeRows:N0} строк, а выделено {rows:N0}. " +
            "Выделите меньше строк или сузьте таблицу фильтром.");
        return true;
    }

    /// <summary>Сколько ячеек обрабатывается за один раз: строки ограничены
    /// <see cref="MaxRangeRows"/>, но столбцов в коллекции бывают сотни.</summary>
    private const long MaxRangeCells = 200_000;

    private static bool TooManyCells(long cells)
    {
        if (cells <= MaxRangeCells) return false;
        AppNotifications.Info(
            $"За один раз обрабатывается не больше {MaxRangeCells:N0} ячеек, а выделено {cells:N0}. " +
            "Выделите меньше строк или столбцов.");
        return true;
    }

    /// <summary>Строки с данными в позициях [top, top + count) — ВСЕ, а не только прокрученные.
    /// Большой диапазон читается в фоне порциями и берет значения только
    /// <paramref name="fieldIds"/>. null — строк больше, чем обрабатывается за раз, или чтение
    /// отменили (человеку уже сказано или он сам нажал «Отмена»).</summary>
    private async Task<IReadOnlyList<TableRowVm>?> RowsOfRangeAsync(int top, int count, IReadOnlyList<int> fieldIds)
    {
        var source = RowsSource;
        if (source is null || count <= 0) return [];
        if (TooManyRows(count)) return null;
        if (count <= SyncRangeRows) return source.MaterializeRange(top, count, fieldIds);

        return await WithProgressAsync<IReadOnlyList<TableRowVm>>("Чтение выделенных строк…",
            async (progress, ct) => await source.MaterializeRangeAsync(top, count, fieldIds, progress, ct));
    }

    /// <summary>
    /// Долгая работа над выделением — с окном прогресса, но только если она не уложилась в
    /// 0,4 секунды: окно, мелькающее на каждую вставку пары ячеек, раздражало бы сильнее ожидания.
    /// Пока окно открыто, таблица недоступна — второе действие поверх недописанного первого
    /// перепутало бы результаты. null — работу отменили.
    /// </summary>
    private async Task<T?> WithProgressAsync<T>(string phase, Func<IProgress<double>, CancellationToken, Task<T>> work,
                                               bool cancellable = true)
    {
        using var cts = new CancellationTokenSource();
        Controls.Dialogs.ProgressDialog? dialog = null;
        var progress = new Progress<double>(f => dialog?.SetProgress(f, phase));
        var task     = work(progress, cts.Token);

        if (VisualRoot is Avalonia.Controls.Window window && await Task.WhenAny(task, Task.Delay(400)) != task)
        {
            dialog = new Controls.Dialogs.ProgressDialog { Title = "Выделенные ячейки" };
            dialog.SetProgress(0, phase);
            if (cancellable) dialog.EnableCancel(() => cts.Cancel());
            var closed = dialog.ShowDialog(window);
            try { await task; } catch { /* исход разбирается ниже */ }
            dialog.Done();
            await closed;
        }

        try { return await task; }
        catch (OperationCanceledException) { return default; }
    }

    // Числовые ключи столбцов — id полей, значения которых нужны операции.
    private List<int> FieldIdsOf(IEnumerable<int> columns) =>
        [.. columns.Select(c => int.TryParse(_colDefs[c].Key, out var id) ? id : 0).Where(id => id > 0)];

    /// <summary>Видимые столбцы значений в порядке показа — ровно те, что копируются и
    /// заполняются вставкой.</summary>
    private List<int> VisibleDataColumns()
    {
        var columns = new List<int>();
        for (var c = FirstDataColumn; c <= LastDataColumn; c++)
            if (IsDataColumn(c)) columns.Add(c);
        return columns;
    }

    /// <summary>Все выделенные ячейки, разрешенные в строки и столбцы, в порядке обхода таблицы
    /// (слева направо, сверху вниз). Строки, которые таблица еще не подгрузила, дочитываются —
    /// обработать молча только прокрученную часть столбца значило бы соврать об итоге.
    /// null — выделено больше строк, чем обрабатывается за раз.</summary>
    private async Task<List<(TableRowVm Row, TableColumnDef Column)>?> SelectedCellsAsync()
    {
        FollowSelectionIdentity();

        var cells = new List<(TableRowVm, TableColumnDef)>();
        if (_cellRange is not { } range) return cells;

        var columns = range.VisibleColumns(VisibleDataColumns());
        if (columns.Count == 0) return cells;
        if (TooManyCells((long)range.RowCount * columns.Count)) return null;
        // Очистке и цвету нужны только id записей — значения полей не читаются.
        if (await RowsOfRangeAsync(range.Top, range.RowCount, []) is not { } rows) return null;

        foreach (var row in rows)
            foreach (var c in columns)
                cells.Add((row, _colDefs[c]));
        return cells;
    }

    // ── Текст ячейки ──────────────────────────────────────────────────────────

    /// <summary>Что показано в ячейке — именно это уходит в буфер обмена (правила показа — в
    /// <see cref="CellDisplayText"/>, там же их проверяет круговой тест). Вычисляемые столбцы берут
    /// значение из аудита записи.</summary>
    private static string CellText(TableRowVm row, TableColumnDef def) =>
        FieldTypeInfo.IsComputed(def.FieldType)
            ? FieldTypeInfo.FormatComputedDisplay(ComputedRaw(row, def.FieldType), def.FieldType)
            : CellDisplayText.Of(def.FieldType, row[def.Key], def.NumberFormat, def.Separator);

    private static string ComputedRaw(TableRowVm row, string fieldType) => fieldType switch
    {
        "created_at" => row.CreatedAt,
        "updated_at" => row.UpdatedAt,
        "created_by" => row.CreatedBy,
        "updated_by" => row.UpdatedBy,
        _            => "",
    };
}
