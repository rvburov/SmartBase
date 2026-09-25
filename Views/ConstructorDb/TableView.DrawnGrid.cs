using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
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
/// только перевод нажатий поверхности в ее вызовы и передача рамки сетке.
///
/// Этап 4 — правка: один накладной редактор (<see cref="EditorHost"/>) с теми же редакторами по
/// типам, что у нынешней сетки (<c>BuildCellEditor</c>), и теми же правилами коммита — Enter, Tab,
/// клик мимо редактора, подмена строк пишут; Escape не пишет; уход фокуса пишет, не закрывая.
/// Второй клик по той же ячейке открывает правку по своей паре «прошлый клик / выделенная ячейка»
/// без обходов невидимого выделения строки. Галочка и скрепка «Файла» — кнопки на отпускании, имя
/// файла — действие <c>FileEditor</c> (открыть, поправить путь руками).
///
/// Этап 5 — операции: кнопка фильтра в заголовке открывает то же окно фильтра и сортировки, что у
/// нынешней сетки, прикрепленное к невидимому якорю на месте кнопки; отбор и сортировка считаются в
/// фоне (<see cref="GridPanel.SetQuery"/>), запомненные за коллекцией приходят с открытием. Колонки
/// пересобираются по одному чтению состояния колонок, строки и прокрутка при этом остаются, а отбор по
/// удаленной колонке снимается. Этап 6 удаляет этот файл вместе с нынешней сеткой.
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

    // Правка: строка и запись правящейся ячейки. Строки списка переиспользуются, и после перестройки
    // в строке редактора может оказаться другая запись — набранное в нее не пишется.
    private TableRowVm? _drawnEditRow;
    private int         _drawnEditRecordId;

    // Нажатие на кнопку, нарисованную сеткой: галочку, скрепку «Файла» или кнопку фильтра в заголовке.
    // Действие — на отпускании над тем же местом, как у кнопки.
    private enum DrawnButton { Checkbox, Paperclip, Filter }
    private (int Row, string Key, Rect Area, DrawnButton Kind)? _drawnButtonPress;

    // Второй клик по выделенной ячейке: правка открывается на отпускании, если выделение не утянули в
    // другую ячейку, — нажать на выделенную ячейку и потянуть значит протянуть выделение.
    private (int Row, int Column)? _drawnEditOnRelease;

    // Номер нажатия на поверхность: одиночный клик по имени файла ждет, не станет ли он двойным (как
    // LinkHost у FileEditor), и отложенное действие выполняется, только если нажатий больше не было;
    // тот же номер отсекает ответ диска, пришедший после следующего клика.
    private int _pressSeq;

    // Плашка «Папка недоступна» под ячейкой авто-привязки (у нынешней таблицы ее показывает FileEditor).
    private Popup?           _drawnNotice;
    private DispatcherTimer? _drawnNoticeTimer;

    // Для стендов: сетка, прямоугольник ячеек, выделенные колонки, последнее открытое меню и плашка.
    internal GridPanel?             DrawnPanel         => _drawn;
    internal string?                DrawnNoticeText    =>
        _drawnNotice is { IsOpen: true, Child: Border { Child: NoticeBanner banner } } ? banner.Message : null;
    internal CellRange?             CurrentCellRange   => _cellRange;
    internal IReadOnlyList<string>  SelectedColumnKeys => _selectedColumnKeys;
    internal ContextMenu?           LastOpenedMenu     { get; private set; }

    // Меню открываются у указателя на самой поверхности (ContextMenu ставится по положению мыши).
    // Open не поднимает Opening (его поднимает только запрос контекстного меню), поэтому пересчеты
    // «при открытии» — цвета, сдвиг записи, «Действия», вставка — зовутся здесь.
    private void OpenDrawnMenu(ContextMenu? menu)
    {
        if (menu is null) return;
        LastOpenedMenu = menu;
        RunMenuOpening(menu);
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
        _drawn.Pressed       += OnDrawnPressed;
        _drawn.RowsChanged   += (_, _) => { FollowDrawnEdit(); SyncDrawnSelection(); };
        _drawn.RowsReplacing += (_, _) => ForgetCellEditing();   // набранное — в строку, пока она живая
        _drawn.RecordEdited  += (_, rid) => RecordValueEdited?.Invoke(this, rid);

        // Escape в правке — отмена без записи. Всплытием, а не туннелем: раскрытый список закрывает
        // себя по Escape сам (и помечает клавишу), правка при этом остается.
        AddHandler(KeyDownEvent, OnDrawnEditKeyDown, RoutingStrategies.Bubble);

        // Протягивание: движения и отпускание — с handledEventsToo, как у нынешней сетки.
        _drawn.Surface.AddHandler(PointerMovedEvent,       OnDrawnPointerMoved,    RoutingStrategies.Bubble, handledEventsToo: true);
        _drawn.Surface.AddHandler(PointerReleasedEvent,    OnDrawnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        _drawn.Surface.AddHandler(PointerCaptureLostEvent, (_, _) => { EndDrawnDrag(); _drawnButtonPress = null; _drawnEditOnRelease = null; },
                                  RoutingStrategies.Bubble, handledEventsToo: true);
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

        // Любое нажатие отменяет ждущий одиночный клик по имени файла (второй клик двойного — тоже).
        _pressSeq++;
        _drawnButtonPress   = null;
        _drawnEditOnRelease = null;

        // Нажатие мимо редактора закрывает правку с записью: клик в другую ячейку, шапку, «№», пустое
        // место. Кроме линии самой правящейся ячейки: она часть ячейки, как у DataGridCell, где клик
        // по краю ячейки в правке ничего не делает.
        if (IsCellEditing)
        {
            if (hit.Kind == GridHitKind.Cell && hit.Row == _drawn.Editor.Row && hit.Def?.Key == _drawn.Editor.Key)
            {
                e.Pointer.Handled = true;
                return;
            }
            CommitDrawnEdit(focusGrid: false);
        }

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
                // Кнопка фильтра — кнопка: окно фильтра открывается на отпускании над ней, выделение
                // колонки не трогается (у нынешней шапки кнопка забирает нажатие себе).
                if (left && hit.Kind == GridHitKind.HeaderFilter)
                {
                    _drawnButtonPress = (-1, hdef.Key, hit.Bounds, DrawnButton.Filter);
                    e.Pointer.Handled = true;
                }
                // Меню шапки — фабрика колонки, как у нынешней сетки (строится на нажатии).
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
            // Второй клик по той же ячейке (медленный или двойной) — правка: ячейка уже выделена одна,
            // и прошлый клик был по ней же. Клик по соседней — только выделение, даже в той же строке.
            // Сама правка — на отпускании (см. _drawnEditOnRelease).
            var here  = (hit.Row, colIdx);
            var again = !shift && _cellRange is { IsSingleCell: true } cur
                        && cur.AnchorRow == hit.Row && cur.AnchorColumn == colIdx && _pressLastCell == here;
            _pressPrevCell = _pressLastCell;
            _pressLastCell = here;

            // Обычный клик выделяет одну ячейку и начинает протягивание; Shift дотягивает
            // прямоугольник от той, с которой начали.
            BeginCellSelection(hit.Row, colIdx, shift);

            // Галочка и скрепка — кнопки: действие на отпускании над тем же местом. Имя файла — на
            // нажатии, как у FileEditor (одиночный клик ждет, не станет ли он двойным).
            var point = e.Pointer.GetPosition(surface);
            if (!shift && !ctrl && DrawnButtonAt(vm, def, hit.Bounds, point) is { } button)
                _drawnButtonPress = (hit.Row, def.Key, button.Area, button.Paperclip ? DrawnButton.Paperclip : DrawnButton.Checkbox);
            else if (!shift && !ctrl && def.FieldType == "file" && !vm.IsLoading)
                OnDrawnFileLinkPressed(hit.Row, def, e.Pointer.ClickCount);

            if (again) _drawnEditOnRelease = here;
            // Двойной клик по имени файла открыл ручную правку пути: мышь дальше — в поле, не в ячейки.
            if (IsCellEditing)
            {
                _dragSelecting = false;
                e.Pointer.Handled = true;
                return;
            }
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

        if (_drawnEditOnRelease is { } pending)
        {
            _drawnEditOnRelease = null;
            if (e.InitialPressMouseButton == MouseButton.Left && _cellRange is { IsSingleCell: true } range
                && range.AnchorRow == pending.Row && range.AnchorColumn == pending.Column)
                BeginDrawnEdit(pending.Row, pending.Column, byMouse: true);
        }

        if (_drawnButtonPress is not { } press) return;
        _drawnButtonPress = null;
        if (e.InitialPressMouseButton != MouseButton.Left || !press.Area.Contains(e.GetPosition(_drawn!.Surface))) return;
        switch (press.Kind)
        {
            case DrawnButton.Paperclip: PickDrawnFile(press.Row, press.Key); break;
            case DrawnButton.Checkbox:  ToggleDrawnCheckbox(press.Row, ColumnIndexOf(press.Key)); break;
            case DrawnButton.Filter:    ShowDrawnFilterFlyout(press.Key, press.Area); break;
        }
    }

    // Окно фильтра и сортировки — то же, что у нынешней шапки, у правого края кнопки; якорь — невидимый
    // прямоугольник сетки на месте нарисованной кнопки.
    private void ShowDrawnFilterFlyout(string key, Rect button)
    {
        if (_drawn is null || _colDefs.FirstOrDefault(d => d.Key == key) is not { } def) return;
        ShowFilterFlyout(_drawn.AnchorAt(button), def);
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

    // ── Правка ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Открыть правку ячейки: второй клик по ней, Enter или F2 на якоре выделения. Редактор — тот же,
    /// что у нынешней таблицы (<c>BuildCellEditor</c>), кладется над ячейкой. Фокус — как у нее: в
    /// поле у текстовых типов, в список у списков; у даты, мультисписка и связи остается в таблице —
    /// их открывают кликом, а Enter, Tab и Escape работают с таблицы. Галочка, файл и вычисляемые
    /// колонки — только для чтения (правятся кликом или никак). Без права правки записей — окно с
    /// причиной, если правку просили мышью; клавиатура молчит, как у нынешней таблицы.
    /// </summary>
    private void BeginDrawnEdit(int row, int column, bool byMouse)
    {
        if (_drawn is null || !IsDataColumn(column) || CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm) return;
        var def = _colDefs[column];
        if (def.IsReadOnly) return;
        if (!Can(PermAction.EditRecord))
        {
            if (byMouse) PermissionNotice.Show(this, PermAction.EditRecord);
            return;
        }
        if (IsCellEditing) CommitDrawnEdit(focusGrid: false);

        ScrollCellIntoView(row, column);
        var editor = BuildCellEditor(def, vm, _drawn.RelationLinks);
        editor.DataContext = vm;
        // Редактор связи пишет связи сам и выбрасывает запись из кеша, а ячейка под ним читает только
        // кеш — перечитываем, чтобы после правки она показала новые названия.
        if (editor is RelationSelectBox relation)
        {
            var rid = vm.RecordId;
            relation.Changed += (_, _) => _drawn?.Rows?.RefreshLinks(rid);
        }
        if (editor is TextBox { TextAlignment: TextAlignment.Right } rightAligned) KeepRightTextInPlace(rightAligned);
        OpenDrawnEditor(row, column, vm, editor);
        if (editor is TextBox or ComboBox) editor.Focus();
    }

    // Поле ввода раскладывает текст по ширине, округленной ВВЕРХ до целого DIP (так делает его
    // презентер текста), а ширины колонок рисованной сетки кратны физическому пикселю — при масштабе
    // 2 бывают половинки. Прижатый вправо текст (число) уезжал бы от этого на полпикселя вправо от
    // показа. Дробный остаток уходит в левый отступ: ширина текста целая, правый край — там же, где
    // у показа (стережет Tests/EditGeometry).
    private static void KeepRightTextInPlace(TextBox box)
    {
        void Fit()
        {
            var text = box.Bounds.Width - CellContentPad * 2;
            if (text <= 0) return;
            var rest    = text - Math.Floor(text + 1e-6);
            var padding = new Thickness(CellContentPad + Math.Max(0, rest), 0, CellContentPad, 0);
            if (box.Padding != padding) box.Padding = padding;
        }
        box.SizeChanged += (_, _) => Fit();
    }

    private void OpenDrawnEditor(int row, int column, TableRowVm vm, Control editor)
    {
        var def = _colDefs[column];
        IsCellEditing      = true;
        _editingCell       = (row, column);
        _drawnEditRow      = vm;
        _drawnEditRecordId = vm.RecordId;
        _editingBox        = editor as TextBox;   // текст пишется один раз, при коммите (FlushEditingBox)
        _editingBoxRow     = vm;
        _editingBoxKey     = def.Key;
        _editingBoxDef     = def;
        _drawn!.Editor.Open(row, def.Key, editor);
    }

    /// <summary>Закрыть правку с записью набранного: Enter, Tab, клик мимо редактора, подмена строк,
    /// закрытие окна. Редакторы с выбором уже записали свое на каждом изменении.</summary>
    private void CommitDrawnEdit(bool focusGrid)
    {
        if (!IsCellEditing || _drawn is null) return;
        // Путь, набранный руками: FileEditor пишет его сам и закрывает правку своим событием.
        if (_drawn.Editor.Editor is FileEditor file) file.CommitManualEdit();
        if (IsCellEditing && _drawnEditRow is { } row && row.RecordId == _drawnEditRecordId) FlushEditingBox();
        CloseDrawnEditor(focusGrid);
    }

    /// <summary>Закрыть правку без записи: Escape, правка этой же записи в панели записи.</summary>
    private void CancelDrawnEdit(bool focusGrid)
    {
        if (!IsCellEditing || _drawn is null) return;
        if (_drawn.Editor.Editor is FileEditor file) file.CancelManualEdit();
        CloseDrawnEditor(focusGrid);
    }

    // Снять редактор. Фокус — таблице, если его просили (Enter, Tab, Escape) или он был в редакторе:
    // иначе он ушел бы в никуда вместе с редактором. Ушедший в панель записи фокус не отбирается.
    private void CloseDrawnEditor(bool focusGrid)
    {
        if (_drawn is null) return;
        var hadFocus = _drawn.Editor.IsKeyboardFocusWithin;
        DropEditingBox();
        IsCellEditing      = false;
        _editingCell       = null;
        _drawnEditRow      = null;
        _drawnEditRecordId = 0;
        _drawn.Editor.Close();
        if (focusGrid || hadFocus) _drawn.Surface.Focus();
    }

    // Строки перестроились (заполнение страницы, структурное обновление): редактор едет за своей
    // записью. Строке досталась другая запись — правка закрывается без записи (набранное ушло бы в
    // чужую запись); строки редактора больше нет в списке — закрывается с записью.
    private void FollowDrawnEdit()
    {
        if (!IsCellEditing || _drawn is null || _drawnEditRow is not { } vm || _editingCell is not { } cell) return;
        if (vm.RecordId != _drawnEditRecordId) { CancelDrawnEdit(focusGrid: false); return; }
        var index = vm.RowNumber - 1;
        if (!ReferenceEquals(CachedRowAt(index), vm)) { CommitDrawnEdit(focusGrid: false); return; }
        if (index == cell.Row) return;
        _editingCell = (index, cell.Column);
        _drawn.Editor.MoveTo(index);
    }

    private void OnDrawnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCellEditing || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        CancelDrawnEdit(focusGrid: true);
    }

    // ── Галочка и «Файл» ──────────────────────────────────────────────────────

    // Кнопка ячейки под точкой: квадрат галочки или скрепка «Файла» (у авто-привязки ее нет).
    private (Rect Area, bool Paperclip)? DrawnButtonAt(TableRowVm vm, TableColumnDef def, Rect cell, Point p)
    {
        if (vm.IsLoading || vm.IsAddRow) return null;
        var surface = _drawn!.Surface;
        if (def.FieldType == "checkbox")
            return surface.CheckboxRect(cell) is var box && box.Contains(p) ? (box, false) : null;
        if (def.FieldType == "file" && surface.FileButtonRect(cell, def) is { } clip && clip.Contains(p))
            return (clip, true);
        return null;
    }

    /// <summary>Переключить галочку (клик по квадрату, пробел на выделенной ячейке). false — в ячейке
    /// не галочка.</summary>
    private bool ToggleDrawnCheckbox(int row, int column)
    {
        if (column < 0 || column >= _colDefs.Count || _colDefs[column].FieldType != "checkbox") return false;
        if (CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm) return true;
        var key = _colDefs[column].Key;
        PermissionNotice.Run(this, Can(PermAction.EditRecord), PermAction.EditRecord,
                             () => vm[key] = IsTruthy(vm[key]) ? "0" : "1");
        return true;
    }

    // Скрепка: системный выбор файла, путь — в запись. Пока открыт диалог, строка могла достаться
    // другой записи — пишем в ту, по которой щелкнули.
    private async void PickDrawnFile(int row, string key)
    {
        if (CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }
        var rid = vm.RecordId;
        if (await FileEditor.PickPathAsync(top) is not { } path) return;
        var target = vm.RecordId == rid ? vm : RowsSource?.FindCachedByRecordId(rid);
        if (target is not null) target[key] = path;
    }

    // Нажатие на имя файла — как LinkHost у FileEditor: двойной клик правит путь руками (не у
    // авто-привязки), одиночный ждет 250 мс, не станет ли двойным, и тогда открывает файл.
    private void OnDrawnFileLinkPressed(int row, TableColumnDef def, int clickCount)
    {
        if (def.FileAutoFolder.Length == 0 && clickCount >= 2)
        {
            BeginDrawnFileEdit(row, def);
            return;
        }
        var seq = _pressSeq;
        var key = def.Key;
        _ = Task.Delay(250).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (seq == _pressSeq) OnDrawnFileSingleClick(row, key);
        }), TaskScheduler.Default);
    }

    // Одиночный клик по имени: файл есть — открыть; нет — поправить путь руками; пусто («Выбрать
    // файл…») — ничего. Авто-привязка: папка недоступна — плашка, файл есть — открыть. Диск
    // спрашивается в фоне: на сетевой папке ответ идет секундами, а UI-поток ждать не должен.
    private void OnDrawnFileSingleClick(int row, string key)
    {
        if (CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm) return;
        if (_colDefs.FirstOrDefault(d => d.Key == key) is not { } def) return;
        var seq    = _pressSeq;
        var rid    = vm.RecordId;
        var path   = vm[def.Key].Trim();
        var folder = def.FileAutoFolder;
        if (folder.Length == 0 && path.Length == 0) return;

        // Ответ — на UI-поток явно (стенды без контекста синхронизации).
        _ = Task.Run(() => (folder.Length == 0 || Directory.Exists(folder), path.Length > 0 && File.Exists(path)))
                .ContinueWith(t => Dispatcher.UIThread.Post(() =>
                {
                    if (!t.IsCompletedSuccessfully) return;
                    if (seq != _pressSeq || vm.RecordId != rid) return;   // пока ждали диск, щелкнули еще раз
                    var (folderOk, fileOk) = t.Result;
                    if (folder.Length > 0)
                    {
                        if (!folderOk) ShowDrawnCellNotice(row, def.Key, "Папка недоступна");
                        else if (fileOk) FileEditor.OpenWithSystem(path);
                        return;
                    }
                    if (fileOk) FileEditor.OpenWithSystem(path);
                    else BeginDrawnFileEdit(row, def);
                }), TaskScheduler.Default);
    }

    // Ручная правка пути: над ячейкой — тот же FileEditor, что в ячейке нынешней таблицы, сразу с
    // полем ввода (живая проверка пути, плашка «Укажите путь…»). Путь пишет он сам; закончил правку —
    // редактор снимается.
    private void BeginDrawnFileEdit(int row, TableColumnDef def)
    {
        if (_drawn is null || def.FileAutoFolder.Length > 0) return;
        if (CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }
        var column = ColumnIndexOf(def.Key);
        if (column < 0) return;
        if (IsCellEditing) CommitDrawnEdit(focusGrid: false);

        var rid    = vm.RecordId;
        var editor = new FileEditor
        {
            Flat               = true,
            ContentLeftPadding = CellContentPad,
            VerticalAlignment  = VerticalAlignment.Center,
            Storage            = vm[def.Key],
            DataContext        = vm,
        };
        editor.Changed   += (_, _) => { if (vm.RecordId == rid) vm[def.Key] = editor.Storage ?? ""; };
        editor.EditEnded += (_, _) =>
        {
            if (IsCellEditing && ReferenceEquals(_drawn?.Editor.Editor, editor)) CloseDrawnEditor(focusGrid: false);
        };
        OpenDrawnEditor(row, column, vm, editor);
        // Поле пути — в содержимом FileEditor, а оно попадает в визуальное дерево с первой раскладкой;
        // до нее фокус в поле не ставится.
        editor.UpdateLayout();
        editor.BeginManualEdit();
    }

    // Плашка под ячейкой на пару секунд — как FlashError у FileEditor («Папка недоступна»).
    private void ShowDrawnCellNotice(int row, string key, string message)
    {
        if (_drawn is null || _drawn.Surface.EditorRect(row, key) is not { } cell) return;
        if (_drawnNotice is null)
        {
            _drawnNotice = new Popup
            {
                Placement             = PlacementMode.BottomEdgeAlignedLeft,
                VerticalOffset        = 2,
                IsLightDismissEnabled = false,
                InheritsTransform     = true,
            };
            Root.Children.Add(_drawnNotice);
        }
        var banner = new NoticeBanner { MinWidth = 220 };
        banner.Show(message);
        _drawnNotice.Child = new Border
        {
            Background   = this.FindResource("BrushBg") as IBrush,
            CornerRadius = this.FindResource("RadiusBase") is CornerRadius r ? r : default,
            Child        = banner,
        };
        _drawnNotice.PlacementTarget = _drawn.Surface;
        _drawnNotice.PlacementRect   = cell;
        _drawnNotice.IsOpen          = true;

        if (_drawnNoticeTimer is null)
        {
            _drawnNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _drawnNoticeTimer.Tick += (_, _) =>
            {
                _drawnNoticeTimer.Stop();
                if (_drawnNotice is not null) _drawnNotice.IsOpen = false;
            };
        }
        _drawnNoticeTimer.Stop();
        _drawnNoticeTimer.Start();
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

    // Открытие коллекции рисованной сеткой: колонки строит фасад (с меню и хуками) по тому же снимку,
    // что получает сетка, — снимок читается один раз. Без готового снимка (обновление той же
    // коллекции, смена вида) он читается на месте без COUNT: точное число записей сетка уточнит в фоне.
    // Отбор и сортировка — как у нынешней таблицы: не переносятся между коллекциями, при настройке
    // «Запоминать фильтры и сортировку» подставляется запомненное за этой коллекцией.
    private void LoadDrawn(ConstructorRepository repository, int collectionId, CollectionOpenSnapshot? snapshot)
    {
        var sameCollection = _lastLoadedCollectionId == collectionId;
        ForgetCellEditing();   // набранное в открытом редакторе — в прежнюю строку, пока она жива
        _repository   = repository;
        _collectionId = collectionId;
        var snap      = snapshot ?? repository.ReadOpenSnapshot(collectionId, deferCount: true);
        _snapshot     = snap;
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
            if (ConstructorSettings.RememberFilters
                && ConstructorFilterMemory.Load(repository.DbPath, collectionId) is { } remembered)
            {
                foreach (var (col, values) in remembered.Filters) _filters[col] = values;
                _sortKey = remembered.SortKey;
                _sortAsc = remembered.SortAsc;
            }

            var (colResize, rowResize, _) = snap.UiSettings;
            _manualColResize = colResize;
            _manualRowResize = rowResize;

            LoadColumnState();
            _colDefs = BuildColDefs();
            // Запомненный отбор по колонке, которой больше нет, снимается — и в памяти тоже.
            if (PruneFilters() && ConstructorSettings.RememberFilters)
                ConstructorFilterMemory.Save(repository.DbPath, collectionId, _filters, _sortKey, _sortAsc);
            ColumnsBuilt?.Invoke(this, _colDefs);

            _drawn!.Load(repository, collectionId, snap, _colDefs, keepOffsets: sameCollection,
                         CopyFilters(), _sortKey, _sortAsc);
            _lastLoadedCollectionId = collectionId;
        }
        finally
        {
            _snapshot = null;
        }
    }

    // Пересборка колонок (сдвиг, «Отображение», создание, удаление, дубль, смена типа, переименование
    // «Названия»): состояние колонок читается один раз и отдается сетке — строки и прокрутка остаются.
    private void ReloadColumnsDrawn()
    {
        if (_repository is null) return;
        ForgetCellEditing();   // колонка редактора могла скрыться или уехать
        LoadColumnState();
        _colDefs = BuildColDefs();
        ColumnsBuilt?.Invoke(this, _colDefs);
        _drawn!.ReloadColumns(_colDefs, _fields, _hiddenColumns, _columnStyles, _colWidths);

        // Удаленные колонки уходят из выделения колонок; оставшиеся — в новом порядке колонок, как их
        // держит SelectColumn (порядок удаления и дубля).
        _selectedColumnKeys.RemoveAll(k => ColumnIndexOf(k) < 0);
        _selectedColumnKeys.Sort((a, b) => ColumnIndexOf(a).CompareTo(ColumnIndexOf(b)));
        // Колонки могли переехать или скрыться: рамка и заливка колонок — по новым позициям.
        SyncDrawnSelection();
        ApplyColumnSelection();
        // Отбор по удаленной колонке снимается, строки пересчитываются без него.
        if (PruneFilters()) ApplyFilters();
    }
}
