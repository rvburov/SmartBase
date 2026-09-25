using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Views.ConstructorDb.DrawnGrid;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Переключатель разработчика на время переезда (этапы 2–5 плана drawn-grid.md): при
/// <see cref="DrawnGridSwitch.Enabled"/> фасад подменяет нынешнюю сетку рисованной
/// (<see cref="GridPanel"/>) и перенаправляет ей то, что она уже умеет: открытие, перезагрузку
/// строк и колонок, режимы ширины и высоты, навигацию нижней панели, сохранение смещения, а с
/// этапа 3 — выделение ячеек, записей и колонок, протягивание с автопрокруткой, клавиши, буфер
/// обмена, отмену и все три меню. Модель выделения — та же, что у нынешней сетки (партиалы
/// <c>CellSelection</c>, <c>CellClipboard</c>, <c>CellUndo</c>, <c>MenuShortcuts</c>); здесь
/// только перевод нажатий поверхности в ее вызовы и передача рамки сетке. Правка и фильтр из
/// шапки подключаются на своих этапах; этап 6 удаляет этот файл вместе с нынешней сеткой.
/// </summary>
public partial class TableView
{
    private GridPanel? _drawn;

    // Протягивание выделения: левая кнопка нажата на ячейке (без Shift) и еще не отпущена.
    // Указатель захвачен поверхностью, чтобы движения за краем таблицы тоже приходили — там
    // работает автопрокрутка (у нынешней сетки ее не было).
    private bool            _drawnDragging;
    private Point           _drawnDragPoint;
    private DispatcherTimer? _drawnAutoScroll;

    // Для стендов: сетка, прямоугольник ячеек, выделенные колонки и последнее открытое меню.
    internal GridPanel?             DrawnPanel         => _drawn;
    internal CellRange?             CurrentCellRange   => _cellRange;
    internal IReadOnlyList<string>  SelectedColumnKeys => _selectedColumnKeys;
    internal ContextMenu?           LastOpenedMenu     { get; private set; }

    // Меню открываются у указателя на самой поверхности (ContextMenu ставится по положению мыши).
    private void OpenDrawnMenu(ContextMenu? menu)
    {
        if (menu is null) return;
        LastOpenedMenu = menu;
        menu.Open(_drawn!.Surface);
    }

    // Зовется из конструктора: рисованная сетка поверх спрятанной нынешней.
    private void InitDrawnGrid()
    {
        if (!DrawnGridSwitch.Enabled) return;
        _drawn = new GridPanel();
        RecordsGrid.IsVisible         = false;
        CellSelectionLayer.IsVisible  = false;
        RowResizeLayer.IsVisible      = false;
        ColResizeLayer.IsVisible      = false;
        Root.Children.Add(_drawn);

        _drawn.VisibleRangeChanged    += (_, range) => VisibleRangeChanged?.Invoke(this, range);
        _drawn.RecordCreateRequested  += (_, _) => PermissionNotice.Run(this, Can(PermAction.CreateRecord), PermAction.CreateRecord,
            () => RecordCreateRequested?.Invoke(this, (ConstructorSettings.OpenNewRecord, null)));
        _drawn.PropertyCreateRequested += (_, _) => PermissionNotice.Run(this, Can(PermAction.CreateField), PermAction.CreateField,
            () => PropertyCreateRequested?.Invoke(this, EventArgs.Empty));
        _drawn.Pressed     += OnDrawnPressed;
        _drawn.RowsChanged += (_, _) => SyncDrawnSelection();

        // Протягивание: движения и отпускание — с handledEventsToo, как у нынешней сетки.
        _drawn.Surface.AddHandler(PointerMovedEvent,       OnDrawnPointerMoved,    RoutingStrategies.Bubble, handledEventsToo: true);
        _drawn.Surface.AddHandler(PointerReleasedEvent,    OnDrawnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        _drawn.Surface.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrawnDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // ── Нажатия ───────────────────────────────────────────────────────────────

    private void OnDrawnPressed(object? sender, GridPointerEventArgs e)
    {
        var surface = _drawn!.Surface;
        var props   = e.Pointer.GetCurrentPoint(surface).Properties;
        var left    = props.IsLeftButtonPressed;
        var right   = props.IsRightButtonPressed;
        var mods    = e.Pointer.KeyModifiers;
        var shift   = mods.HasFlag(KeyModifiers.Shift);
        var ctrl    = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
        var hit     = e.Hit;

        // Клавиши таблицы слушаются при фокусе внутри нее; сетка — единственный фокусируемый
        // контрол, кнопок в ячейках нет, так что фокус забирается на любом нажатии.
        surface.Focus();

        switch (hit.Kind)
        {
            case GridHitKind.Header:
            case GridHitKind.HeaderFilter:
                if (hit.Def is not { } hdef) return;
                if (left && hit.Kind == GridHitKind.Header && hdef.Key != TableColumnDef.NumKey)
                {
                    hdef.OnHeaderClick?.Invoke(hdef);
                    SelectColumn(hdef.Key, shift);   // выделение колонки гасит выделение ячеек
                }
                // Левый клик по кнопке фильтра — флайаут этапа 5. Меню шапки — фабрика колонки, как у
                // нынешней сетки (строится на нажатии).
                if (right && hdef.BuildHeaderMenu is { } factory) OpenDrawnMenu(factory(hdef));
                return;

            case GridHitKind.HeaderAdd:
                return;   // создание свойства — панель

            case GridHitKind.AddRow:
                ClearColumnSelection();
                if (right) OpenDrawnMenu(BuildGridContextMenu());
                return;   // создание записи левой — панель

            case GridHitKind.Cell:
                break;

            default:
                if (right) OpenDrawnMenu(BuildGridContextMenu());   // пустое место под строками
                return;
        }

        // Клик по любой ячейке снимает выделение колонки — как у нынешней сетки.
        ClearColumnSelection();
        if (hit.Def is not { } def || surface.Rows is not { } rows || hit.Row < 0 || hit.Row >= rows.Count) return;
        var vm = rows[hit.Row];

        if (def.Key == TableColumnDef.NumKey)
        {
            OnDrawnNumPressed(vm, left, right, toggle: shift || ctrl, e.Pointer.ClickCount);
            return;
        }
        if (def.Key == TableColumnDef.AddPropKey)
        {
            if (left)
                PermissionNotice.Run(this, Can(PermAction.CreateField), PermAction.CreateField,
                                     () => PropertyCreateRequested?.Invoke(this, EventArgs.Empty));
            return;
        }

        var colIdx = ColumnIndexOf(def.Key);
        if (colIdx < 0) return;
        def.OnCellClick?.Invoke(vm, def);

        if (left)
        {
            // Обычный клик выделяет одну ячейку и начинает протягивание; Shift дотягивает
            // прямоугольник от той, с которой начали. Второй клик по той же ячейке — правка (этап 4).
            BeginCellSelection(hit.Row, colIdx, shift);
            if (_dragSelecting)
            {
                _drawnDragging  = true;
                _drawnDragPoint = e.Pointer.GetPosition(surface);
                e.Pointer.Pointer.Capture(surface);
            }
            e.Pointer.Handled = true;
            return;
        }
        if (right)
        {
            // Вне выделения — переносим его на эту ячейку, внутри — оставляем: меню работает по
            // всему диапазону.
            if (!IsCellSelected(hit.Row, colIdx)) SetCellSelection(CellRange.Single(hit.Row, colIdx));
            OpenDrawnMenu(BuildCellContextMenu(vm, def));
        }
    }

    // Столбец «№» — он и только он работает со СТРОКАМИ: Shift и Ctrl точечно добавляют или
    // убирают запись из выделения, обычный клик выделяет одну и открывает запись (при настройке
    // «двойным кликом» — на втором клике), правый — меню записи: вне выделения запись выделяется
    // одна, внутри — выделение сохраняется, и меню работает по всему набору.
    private void OnDrawnNumPressed(TableRowVm vm, bool left, bool right, bool toggle, int clickCount)
    {
        ClearCellSelection();   // либо ячейки, либо строки: иначе Delete был бы двусмысленным
        if (vm.IsAddRow || vm.RecordId <= 0) return;   // заглушка «Загрузка…» — записи еще нет

        if (left && toggle) { _drawn!.ToggleRecordSelection(vm.RecordId); return; }
        if (left)
        {
            _drawn!.SetRecordSelection([vm.RecordId]);
            if (!ConstructorSettings.OpenByDoubleClick || clickCount >= 2) RecordClicked?.Invoke(this, vm.RecordId);
            return;
        }
        if (right)
        {
            if (!_drawn!.IsRecordSelected(vm.RecordId)) _drawn.SetRecordSelection([vm.RecordId]);
            OpenDrawnMenu(BuildRowContextMenu(() => vm));
        }
    }

    private int ColumnIndexOf(string key) => _colDefs.FindIndex(d => d.Key == key);

    // ── Протягивание с автопрокруткой ─────────────────────────────────────────

    private void OnDrawnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_drawnDragging || _cellRange is null) return;
        var surface = _drawn!.Surface;
        // Кнопку отпустили где-то вне таблицы (захват уже снят) — выделение больше не тянется.
        if (!e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) { EndDrawnDrag(); return; }
        _drawnDragPoint = e.GetPosition(surface);
        DrawnDragStep();
    }

    private void OnDrawnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drawnDragging && ReferenceEquals(e.Pointer.Captured, _drawn!.Surface)) e.Pointer.Capture(null);
        EndDrawnDrag();
    }

    private void EndDrawnDrag()
    {
        _drawnDragging = false;
        _drawnAutoScroll?.Stop();
    }

    // Один шаг протягивания: указатель за краем области строк — прокрутка в его сторону (и таймер,
    // пока он там стоит), затем фокус выделения переезжает на ячейку под прижатой к краю точкой.
    private void DrawnDragStep()
    {
        if (!_drawnDragging || _drawn is null || _cellRange is not { } range) { _drawnAutoScroll?.Stop(); return; }
        var surface = _drawn.Surface;

        var (clamped, dx, dy) = DragScroll.Step(_drawnDragPoint, _drawn.BodyRect(), surface.RowPitch);
        if (dx != 0 || dy != 0)
        {
            _drawn.ScrollBy(dx, dy);
            _drawnAutoScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => DrawnDragStep());
            if (!_drawnAutoScroll.IsEnabled) _drawnAutoScroll.Start();
        }
        else _drawnAutoScroll?.Stop();

        if (DrawnCellNear(clamped) is not { } cell) return;
        if (cell.Row == range.FocusRow && cell.Column == range.FocusColumn) return;
        SetCellSelection(range.WithFocus(cell.Row, cell.Column));
    }

    // Ячейка значения, ближайшая к точке поверхности: строка по раскладке (за краями и на строке
    // «+» — крайняя строка с данными), колонка по раскладке; служебные «№» и «+» — ближайшая
    // колонка с данными. Координаты — позиции показанной таблицы, как у CellRange.
    private (int Row, int Column)? DrawnCellNear(Point p)
    {
        var surface  = _drawn!.Surface;
        var dataRows = VisibleRowCount;
        var layout   = surface.Columns;
        if (dataRows <= 0 || layout.Count == 0) return null;

        var yContent = p.Y - surface.HeaderHeight + surface.OffsetY;
        var row = surface.RowLayout.IndexAt(yContent, surface.RowCount);
        if (row < 0) row = yContent < 0 ? 0 : dataRows - 1;
        row = Math.Min(row, dataRows - 1);

        var xContent = p.X + surface.OffsetX;
        var c = layout.IndexAt(xContent);
        if (c < 0) c = xContent < 0 ? 0 : layout.Count - 1;
        var data = VisibleDataColumns();
        if (data.Count == 0) return null;
        var key    = layout[c].Key;
        var column = key == TableColumnDef.NumKey     ? data[0]
                   : key == TableColumnDef.AddPropKey ? data[^1]
                   : ColumnIndexOf(key);
        return column < 0 ? null : (row, column);
    }

    // ── Рамка и выделение — сетке ─────────────────────────────────────────────

    // Строки или колонки сменились (новый список, заполнение страницы, пересборка колонок):
    // выделение догоняет свои запись и столбцы, рамка и счетчик обновляются.
    private void SyncDrawnSelection()
    {
        if (_drawn is null) return;
        if (_cellRange is not null) FollowSelectionIdentity();
        PushDrawnCellSelection();
        RaiseSelectionLabel();
    }

    // Рамка — сетке: строки среза и ключи крайних ВИДИМЫХ колонок диапазона (скрытые внутри не
    // считаются — их нет ни в раскладке, ни в буфере). Тонировка — когда видимых ячеек больше одной.
    private void PushDrawnCellSelection()
    {
        if (_drawn is null) return;
        if (_cellRange is not { } range) { _drawn.ClearCellSelection(); return; }
        var columns = range.VisibleColumns(VisibleDataColumns());
        if (columns.Count == 0) { _drawn.ClearCellSelection(); return; }
        _drawn.SetCellSelection(range.Top, range.Bottom, _colDefs[columns[0]].Key, _colDefs[columns[^1]].Key,
                                tinted: (long)range.RowCount * columns.Count > 1);
    }

    // ── Открытие и пересборка ─────────────────────────────────────────────────

    // Открытие коллекции рисованной сеткой: колонки строит фасад (с меню и хуками), снимок —
    // как у нынешней (без него читается на месте).
    private void LoadDrawn(ConstructorRepository repository, int collectionId, CollectionOpenSnapshot? snapshot)
    {
        var sameCollection = _lastLoadedCollectionId == collectionId;
        _repository   = repository;
        _collectionId = collectionId;
        _snapshot     = snapshot;
        try
        {
            // Выделение к ДРУГОЙ коллекции отношения не имеет; перезагрузку той же выделение
            // переживает (уехавшие позиции догоняет FollowSelectionIdentity по RowsChanged).
            if (!sameCollection)
            {
                ClearCellSelection();
                ForgetUndo();
                ClearColumnSelection();
                _drawn!.ClearRecordSelection();
            }

            _filters.Clear();
            _sortKey = null;
            var (colResize, rowResize, _) = snapshot?.UiSettings ?? repository.GetUiSettings(collectionId);
            _manualColResize = colResize;
            _manualRowResize = rowResize;

            LoadColumnState();
            _colDefs = BuildColDefs();
            ColumnsBuilt?.Invoke(this, _colDefs);

            _drawn!.Load(repository, collectionId, snapshot ?? repository.ReadOpenSnapshot(collectionId), _colDefs,
                         keepOffsets: sameCollection);
            _lastLoadedCollectionId = collectionId;
        }
        finally
        {
            _snapshot = null;
        }
    }

    private void ReloadColumnsDrawn()
    {
        if (_repository is null) return;
        LoadColumnState();
        _colDefs = BuildColDefs();
        ColumnsBuilt?.Invoke(this, _colDefs);
        _drawn!.ReloadColumns(_colDefs);
        // Колонки могли переехать или скрыться: рамка и заливка колонок — по новым позициям.
        SyncDrawnSelection();
        ApplyColumnSelection();
    }
}
