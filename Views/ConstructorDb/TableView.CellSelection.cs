using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Core.Models;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Выделение ячеек прямоугольником — как в Excel: клик ставит рамку на одну ячейку, протягивание
/// с зажатой кнопкой и Shift+клик растягивают ее до прямоугольника. Выделение СТРОК живет
/// отдельно и переехало на служебный столбец «№»: клик по номеру выделяет строку целиком и
/// открывает запись, Shift и Ctrl по номеру набирают несколько строк — на этом держатся
/// пакетные операции (удаление, дубль, «Расположение»).
///
/// Координаты диапазона — индексы В ПОКАЗАННОЙ таблице (позиция строки и позиция столбца), а не
/// идентификаторы: строки виртуализованы и переиспользуются при прокрутке, привязываться к ним
/// нельзя. Идентификаторы резолвятся в момент операции (см. <see cref="CellsOf"/>).
///
/// Рамка рисуется НЕ в ячейке, а отдельным слоем поверх сетки (как полоски ресайза): ячейка
/// при прокрутке достается другой строке, и рамка «поехала» бы вместе с ней.
/// </summary>
public partial class TableView
{
    // Выделение — РОВНО ОДИН прямоугольник (или ничего). Держать список кусков было бы нечем
    // набрать: выделение ставится только кликом и Shift-кликом, а они всегда дают сплошной
    // прямоугольник. Отсюда и главное свойство картинки: рамка всегда одна, «прямоугольника
    // в прямоугольнике» не бывает в принципе.
    private CellRange? _cellRange;

    /// <summary>Выделены ли сейчас ячейки (для пунктов меню и горячих клавиш).</summary>
    public bool HasCellSelection => _cellRange is not null;

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

    /// <summary>Выделить прямоугольник — из клика и из пунктов меню («вся строка», «весь
    /// столбец»).</summary>
    private void SetCellSelection(CellRange range)
    {
        _cellRange = range;
        ClearRecordSelection();   // либо ячейки, либо строки — как в Excel
        AfterSelectionChanged();
    }

    /// <summary>Снять выделение ячеек (уход из коллекции, выделение строк).</summary>
    public void ClearCellSelection()
    {
        if (_cellRange is null) return;
        _cellRange = null;
        AfterSelectionChanged();
    }

    private void AfterSelectionChanged()
    {
        ApplyCellSelectionClass();
        RefreshCellSelectionOverlay();
    }

    // Пока выделены ячейки, строка не должна выглядеть выделенной — хотя внутри DataGrid она
    // выделена (без этого не работает правка, см. ScheduleRecordSelectionDrop). Класс гасит
    // только ВИД: стили — в TableView.axaml.
    private void ApplyCellSelectionClass() => RecordsGrid.Classes.Set("cellselect", HasCellSelection);

    /// <summary>
    /// Клик по ячейке. Обычный выделяет одну; Shift дотягивает прямоугольник от той ячейки, с
    /// которой начали, — как в Excel, одним куском на сколько угодно строк и столбцов.
    /// Других способов выделить ячейки нет: ни Ctrl, ни протягивания мышью.
    /// </summary>
    private void BeginCellSelection(int row, int column, bool extend)
    {
        if (!IsDataColumn(column)) return;

        SetCellSelection(extend && _cellRange is { } current
            ? current.WithFocus(row, column)   // якорь стоит, двигается только дальний угол
            : CellRange.Single(row, column));

        // Фокус себе НЕ забираем. Кнопка в Avalonia на потере фокуса сбрасывает IsPressed, а без
        // него PointerReleased не поднимает Click — забрав фокус здесь, мы убивали бы клик по
        // галочке и по скрепке «Файла» (их ячейки — обычные кнопки). Фокус и так остается внутри
        // таблицы: обычной ячейке его ставит сам грид, а в ячейке с кнопкой — сама кнопка.
    }

    // ── Разрешение координат в данные ─────────────────────────────────────────

    /// <summary>Строка по позиции в показанной таблице; null — строки нет или она еще грузится.</summary>
    private TableRowVm? RowAt(int index)
    {
        var source = _activeRows ?? (System.Collections.IList?)_virtualRows;
        if (source is null || index < 0 || index >= source.Count) return null;
        return source[index] as TableRowVm is { IsAddRow: false, IsLoading: false } row ? row : null;
    }

    /// <summary>Сколько строк с данными показано сейчас (без строки «+»).</summary>
    private int VisibleRowCount
    {
        get
        {
            var source = _activeRows ?? (System.Collections.IList?)_virtualRows;
            return source is null ? 0 : Math.Max(0, source.Count - 1);
        }
    }

    /// <summary>Все выделенные ячейки, разрешенные в строки и столбцы, в порядке обхода таблицы
    /// (слева направо, сверху вниз). Строки, не успевшие подгрузиться, пропускаются: читать у них
    /// нечего, а ждать загрузку посреди копирования — значит подвесить интерфейс.</summary>
    private List<(TableRowVm Row, TableColumnDef Column)> SelectedCells()
    {
        var cells = new List<(TableRowVm, TableColumnDef)>();
        if (_cellRange is not { } range) return cells;

        for (var r = range.Top; r <= range.Bottom; r++)
        {
            if (RowAt(r) is not { } row) continue;
            for (var c = range.Left; c <= range.Right; c++)
                if (IsDataColumn(c))
                    cells.Add((row, _colDefs[c]));
        }
        return cells;
    }

    // ── Текст ячейки ──────────────────────────────────────────────────────────

    /// <summary>
    /// Что показано в ячейке — именно это уходит в буфер обмена. Повторяет правила конвертеров
    /// таблицы: дата человекочитаемо, чекбокс галочкой, число в формате столбца, мультисписок
    /// через свой разделитель. Копировать сырое хранение нельзя: человек ждет в буфере то, что
    /// видит на экране.
    /// </summary>
    private static string CellText(TableRowVm row, TableColumnDef def)
    {
        if (FieldTypeInfo.IsComputed(def.FieldType))
            return FieldTypeInfo.FormatComputedDisplay(ComputedRaw(row, def.FieldType), def.FieldType);

        var raw = row[def.Key];
        return def.FieldType switch
        {
            "checkbox"    => raw is "1" or "true" or "True" ? "✓" : "",
            "date"        => DateValue.TryParse(raw, out var dv) ? dv.ToDisplay() : raw,
            "multiselect" => MultiSelectValue.Display(raw, def.Separator),
            "number"      => NumberFormatValue.Format(raw, def.NumberFormat),
            "file"        => FileValue.DisplayName(raw),
            _             => raw,
        };
    }

    private static string ComputedRaw(TableRowVm row, string fieldType) => fieldType switch
    {
        "created_at" => row.CreatedAt,
        "updated_at" => row.UpdatedAt,
        "created_by" => row.CreatedBy,
        "updated_by" => row.UpdatedBy,
        _            => "",
    };

    // ── Отрисовка рамки ───────────────────────────────────────────────────────
    //
    // Одна рамка на все выделение целиком (а не вокруг каждой ячейки): так же выглядит выделение
    // в Excel, и внутри диапазона не появляется ни сетки из линий, ни «прямоугольника в
    // прямоугольнике».
    // Заливки нет: выделение — обводка, значение под ней должно читаться.

    // Геометрия уже нарисованной рамки. Пересчет идет из LayoutUpdated, а любая правка дерева
    // Canvas оттуда запускает новый проход разметки — тот снова зовет пересчет, и цикл кормит
    // сам себя, пока Avalonia не уронит приложение с «Infinite layout loop detected». Сверка
    // «ничего не изменилось» разрывает петлю; та же защита стоит у полосок ресайза строк и
    // рамок выделения колонок.
    private readonly List<Rect> _lastCellBoxes = [];
    private bool _cellOverlayPending;

    /// <summary>Пересчет рамок после прокрутки и ресайза. Менять дерево прямо в LayoutUpdated
    /// нельзя, поэтому работа откладывается на тик диспетчера (как у полосок ресайза).</summary>
    private void ScheduleCellOverlayRefresh()
    {
        // Ячеек не выделено и рамки на экране нет — на прокрутке не делаем вообще ничего.
        if (_cellRange is null && _lastCellBoxes.Count == 0) return;
        if (_cellOverlayPending) return;

        _cellOverlayPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _cellOverlayPending = false;
            RefreshCellSelectionOverlay();
        }, DispatcherPriority.Background);
    }

    private void RefreshCellSelectionOverlay()
    {
        // Сначала считаем целевую геометрию, НЕ трогая дерево.
        var target = new List<Rect>();
        if (_cellRange is { } range && !HidesOverlayWhileEditing(range) && SelectionBounds(range) is { } bounds)
            target.Add(bounds);

        if (SameBoxes(target, _lastCellBoxes)) return;

        _lastCellBoxes.Clear();
        _lastCellBoxes.AddRange(target);

        CellSelectionLayer.Children.Clear();
        foreach (var rect in target)
        {
            var box = new Rectangle
            {
                Width           = rect.Width,
                Height          = rect.Height,
                // Прямые углы: рамка садится ровно на линии сетки, скругление оставляло бы
                // разрывы по углам ячейки.
                RadiusX         = 0,
                RadiusY         = 0,
                Stroke          = this.TryFindResource("BrushAccent", out var accent) && accent is IBrush b
                                      ? b : Brushes.DodgerBlue,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, rect.X);
            Canvas.SetTop(box, rect.Y);
            CellSelectionLayer.Children.Add(box);
        }
    }

    /// <summary>
    /// Прячем свою рамку, только когда выделена РОВНО та ячейка, что сейчас в правке: у редактора
    /// есть собственная рамка, и две вокруг одной ячейки читаются как ошибка отрисовки.
    ///
    /// А вот растянутое выделение при открытой правке прятать нельзя: человек тянет Shift'ом от
    /// ячейки, в которой только что печатал, и обязан видеть, что выделяется.
    /// </summary>
    private bool HidesOverlayWhileEditing(CellRange range) =>
        IsCellEditing
        && range.IsSingleCell
        && _editingCell is { } cell
        && range.Contains(cell.Row, cell.Column);

    // Сравнение с допуском в полпикселя: раскладка дает микроскопические расхождения координат,
    // и точное сравнение double держало бы layout-цикл живым.
    private static bool SameBoxes(List<Rect> a, List<Rect> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].X      - b[i].X)      > 0.5 || Math.Abs(a[i].Y      - b[i].Y)      > 0.5
             || Math.Abs(a[i].Width  - b[i].Width)  > 0.5 || Math.Abs(a[i].Height - b[i].Height) > 0.5)
                return false;
        return true;
    }

    /// <summary>Прямоугольник диапазона в координатах слоя; null — на экране не видно ни одной
    /// его ячейки (прокрутили мимо).</summary>
    private Rect? SelectionBounds(CellRange range)
    {
        Rect? union = null;

        foreach (var cell in RealizedCells())
        {
            if (!range.Contains(cell.Row, cell.Column)) continue;
            // Ячейка скрытой колонки и служебная filler-ячейка живут в дереве, но имеют нулевой
            // размер: их прямоугольник растянул бы рамку к началу координат.
            if (!cell.Cell.IsVisible || cell.Cell.Bounds.Width <= 0 || cell.Cell.Bounds.Height <= 0) continue;

            var bounds = CellBoundsInLayer(cell.Cell);
            if (bounds is not { } r) continue;
            union = union is { } u ? u.Union(r) : r;
        }

        return union;
    }

    // Ячейки, реально построенные сейчас (виртуализация строит только видимые строки).
    //
    // Номер колонки берем по ПОРЯДКУ ячейки в строке: публичного «какая у тебя колонка» у
    // DataGridCell в Avalonia 11.2.3 нет (свойство internal), а порядок ячеек в презентере
    // совпадает с порядком колонок. Ячейки идут слева направо, включая служебные «№» и «+».
    private IEnumerable<(int Row, int Column, DataGridCell Cell)> RealizedCells()
    {
        foreach (var gridRow in RecordsGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            // Строка в правке или с фокусом, уехавшая за экран, остается в дереве со старой
            // раскладкой (см. IsStaleRow) — ее прямоугольник растянул бы рамку мимо экрана и
            // заставил бы геометрию дрожать от прохода к проходу.
            if (IsStaleRow(gridRow)) continue;
            if (gridRow.DataContext is not TableRowVm vm || vm.IsAddRow) continue;
            var rowIndex = vm.RowNumber - 1;

            var colIndex = 0;
            foreach (var cell in gridRow.GetVisualDescendants().OfType<DataGridCell>())
                yield return (rowIndex, colIndex++, cell);
        }
    }

    private Rect? CellBoundsInLayer(DataGridCell cell)
    {
        var origin = cell.TranslatePoint(default, CellSelectionLayer);
        return origin is { } p ? new Rect(p, cell.Bounds.Size) : null;
    }
}
