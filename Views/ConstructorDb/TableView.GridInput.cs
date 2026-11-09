using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Views.ConstructorDb.DrawnGrid;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Ввод сетки: перевод нажатий поверхности (<see cref="GridSurface"/>) в вызовы модели выделения
/// (партиалы <c>CellSelection</c>, <c>CellClipboard</c>, <c>CellUndo</c>, <c>MenuShortcuts</c>),
/// протягивание с автопрокруткой, открытие меню у указателя и окна фильтра у кнопки заголовка,
/// правка ячейки накладным редактором (<see cref="EditorHost"/>) с теми же редакторами по типам
/// (<c>BuildCellEditor</c>) и правилами коммита: Enter, Tab, клик мимо редактора, подмена строк
/// пишут; Escape не пишет; уход фокуса пишет, не закрывая. Второй клик по той же ячейке открывает
/// правку по своей паре «прошлый клик / выделенная ячейка»; набор на выделенной ячейке открывает ее
/// с набранным (партиал <c>CellTyping</c>). Галочка и скрепка «Файла» — кнопки на отпускании, имя
/// файла — действие <c>FileEditor</c> (открыть, поправить путь руками).
/// </summary>
public partial class TableView
{
    // Протягивание выделения: левая кнопка нажата на ячейке (без Shift) и еще не отпущена.
    // Указатель захвачен поверхностью, чтобы движения за краем таблицы тоже приходили — там
    // работает автопрокрутка.
    private bool            _dragging;
    private Point           _dragPoint;
    private DispatcherTimer? _autoScroll;

    // Правка: строка и запись правящейся ячейки. Строки списка переиспользуются, и после перестройки
    // в строке редактора может оказаться другая запись — набранное в нее не пишется.
    private TableRowVm? _editRow;
    private int         _editRecordId;

    // Нажатие на кнопку, нарисованную сеткой: галочку, скрепку «Файла» или кнопку фильтра в заголовке.
    // Действие — на отпускании над тем же местом, как у кнопки.
    private enum GridButton { Checkbox, Paperclip, Filter }
    private (int Row, string Key, Rect Area, GridButton Kind)? _buttonPress;

    // Второй клик по выделенной ячейке: правка открывается на отпускании, если выделение не утянули в
    // другую ячейку, — нажать на выделенную ячейку и потянуть значит протянуть выделение.
    private (int Row, int Column)? _editOnRelease;

    // Номер нажатия на поверхность: одиночный клик по имени файла ждет, не станет ли он двойным (как
    // LinkHost у FileEditor), и отложенное действие выполняется, только если нажатий больше не было;
    // тот же номер отсекает ответ диска, пришедший после следующего клика.
    private int _pressSeq;

    // Плашка «Папка недоступна» под ячейкой авто-привязки (в панели записи ее показывает FileEditor).
    private Popup?           _notice;
    private DispatcherTimer? _noticeTimer;

    // Для стендов: сетка, прямоугольник ячеек, выделенные колонки, последнее открытое меню и плашка.
    internal GridPanel              DrawnPanel         => RecordsGrid;
    internal string?                DrawnNoticeText    =>
        _notice is { IsOpen: true, Child: Border { Child: NoticeBanner banner } } ? banner.Message : null;
    internal CellRange?             CurrentCellRange   => _cellRange;
    internal IReadOnlyList<string>  SelectedColumnKeys => _selectedColumnKeys;
    internal ContextMenu?           LastOpenedMenu     { get; private set; }

    // Меню открываются у указателя на самой поверхности (ContextMenu ставится по положению мыши).
    // Open не поднимает Opening (его поднимает только запрос контекстного меню), поэтому пересчеты
    // «при открытии» — цвета, сдвиг записи, «Действия», вставка — зовутся здесь.
    private void OpenMenu(ContextMenu? menu)
    {
        if (menu is null) return;
        LastOpenedMenu = menu;
        RunMenuOpening(menu);
        menu.Open(RecordsGrid.Surface);
    }

    // Зовется из конструктора: события сетки — фасаду.
    private void InitGridInput()
    {
        RecordsGrid.VisibleRangeChanged    += (_, range) => VisibleRangeChanged?.Invoke(this, range);
        RecordsGrid.RecordCreateRequested  += (_, _) => PermissionNotice.Run(this, Can(PermAction.CreateRecord), PermAction.CreateRecord,
            () => RecordCreateRequested?.Invoke(this, (ConstructorSettings.OpenNewRecord, null)));
        RecordsGrid.PropertyCreateRequested += (_, _) => PermissionNotice.Run(this, Can(PermAction.CreateField), PermAction.CreateField,
            () => PropertyCreateRequested?.Invoke(this, EventArgs.Empty));
        RecordsGrid.Pressed       += OnGridPressed;
        RecordsGrid.RowsChanged   += (_, _) => { FollowEdit(); SyncSelection(); };
        RecordsGrid.RowsReplacing += (_, _) => ForgetCellEditing();   // набранное — в строку, пока она живая
        RecordsGrid.RecordEdited  += (_, rid) => RecordValueEdited?.Invoke(this, rid);

        // Escape в правке — отмена без записи. Всплытием, а не туннелем: раскрытый список закрывает
        // себя по Escape сам (и помечает клавишу), правка при этом остается.
        AddHandler(KeyDownEvent, OnEditKeyDown, RoutingStrategies.Bubble);

        // Набор на выделенной ячейке открывает правку с набранным (TableView.CellTyping.cs). Туннелем —
        // раньше списка с его встроенным поиском по буквам.
        AddHandler(TextInputEvent, OnTableTextInput, RoutingStrategies.Tunnel);
        // Клик в поле правки, начатой набором, переводит ее из «ввода» в «правку»: стрелки — по тексту.
        RecordsGrid.Editor.AddHandler(PointerPressedEvent, (_, _) => _enterMode = false,
                                      RoutingStrategies.Tunnel, handledEventsToo: true);

        // Протягивание: движения и отпускание — с handledEventsToo, чтобы дойти и сквозь редактор.
        RecordsGrid.Surface.AddHandler(PointerMovedEvent,       OnGridPointerMoved,    RoutingStrategies.Bubble, handledEventsToo: true);
        RecordsGrid.Surface.AddHandler(PointerReleasedEvent,    OnGridPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        RecordsGrid.Surface.AddHandler(PointerCaptureLostEvent, (_, _) => { EndDrag(); _buttonPress = null; _editOnRelease = null; },
                                       RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // ── Нажатия ───────────────────────────────────────────────────────────────

    private void OnGridPressed(object? sender, GridPointerEventArgs e)
    {
        var surface = RecordsGrid.Surface;
        var props   = e.Pointer.GetCurrentPoint(surface).Properties;
        var left    = props.IsLeftButtonPressed;
        var right   = props.IsRightButtonPressed;
        var mods    = e.Pointer.KeyModifiers;
        var shift   = mods.HasFlag(KeyModifiers.Shift);
        var ctrl    = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
        var hit     = e.Hit;

        // Любое нажатие отменяет ждущий одиночный клик по имени файла (второй клик двойного — тоже).
        _pressSeq++;
        _buttonPress   = null;
        _editOnRelease = null;

        // Нажатие мимо редактора закрывает правку с записью: клик в другую ячейку, шапку, «№», пустое
        // место. Кроме линии самой правящейся ячейки: она часть ячейки, и клик по краю ячейки в
        // правке ничего не делает.
        if (IsCellEditing)
        {
            if (hit.Kind == GridHitKind.Cell && hit.Row == RecordsGrid.Editor.Row && hit.Def?.Key == RecordsGrid.Editor.Key)
            {
                e.Pointer.Handled = true;
                return;
            }
            CommitEdit(focusGrid: false);
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
                    // Выделение колонки гасит выделение ячеек. Shift или Ctrl — добавить или снять
                    // одну, Ctrl+Shift — все от последней выделенной кликом (как у «№» и в задачах).
                    if (shift && ctrl) SelectColumnRange(hdef.Key);
                    else               SelectColumn(hdef.Key, shift || ctrl);
                }
                // Кнопка фильтра — кнопка: окно фильтра открывается на отпускании над ней, выделение
                // колонки не трогается.
                if (left && hit.Kind == GridHitKind.HeaderFilter)
                {
                    _buttonPress = (-1, hdef.Key, hit.Bounds, GridButton.Filter);
                    e.Pointer.Handled = true;
                }
                // Меню шапки — фабрика колонки (строится на нажатии). Как у «№»: колонка вне
                // выделения выделяется одна, внутри — набор сохраняется, и меню работает по всему.
                if (right && hdef.BuildHeaderMenu is { } factory)
                {
                    if (!_selectedColumnKeys.Contains(hdef.Key)) SelectColumn(hdef.Key, shift: false);
                    OpenMenu(factory(hdef));
                }
                return;

            case GridHitKind.HeaderAdd:
                return;   // создание свойства — панель

            case GridHitKind.AddRow:
                ClearColumnSelection();
                if (right) OpenMenu(BuildGridContextMenu());
                return;   // создание записи левой — панель

            case GridHitKind.Cell:
                break;

            default:
                if (right) OpenMenu(BuildGridContextMenu());   // пустое место под строками
                return;
        }

        // Клик по любой ячейке снимает выделение колонки.
        ClearColumnSelection();
        if (hit.Def is not { } def || surface.Rows is not { } rows || hit.Row < 0 || hit.Row >= rows.Count) return;
        var vm = rows[hit.Row];

        if (def.Key == TableColumnDef.NumKey)
        {
            OnNumPressed(vm, left, right, toggle: shift || ctrl, range: shift && ctrl, e.Pointer.ClickCount);
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
            // Сама правка — на отпускании (см. _editOnRelease).
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
            if (!shift && !ctrl && ButtonAt(vm, def, hit.Bounds, point) is { } button)
                _buttonPress = (hit.Row, def.Key, button.Area, button.Paperclip ? GridButton.Paperclip : GridButton.Checkbox);
            else if (!shift && !ctrl && def.FieldType == "file" && !vm.IsLoading
                     && surface.FilePathAt(hit, point) is { } path)
                OnFileLinkPressed(hit.Row, def, path, e.Pointer.ClickCount);

            if (again) _editOnRelease = here;
            // Двойной клик по имени файла открыл ручную правку пути: мышь дальше — в поле, не в ячейки.
            if (IsCellEditing)
            {
                _dragSelecting = false;
                e.Pointer.Handled = true;
                return;
            }
            if (_dragSelecting)
            {
                _dragging  = true;
                _dragPoint = e.Pointer.GetPosition(surface);
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
            OpenMenu(BuildCellContextMenu(vm, def));
        }
    }

    // Столбец «№» — он и только он работает со СТРОКАМИ: Shift и Ctrl точечно добавляют или
    // убирают запись из выделения, Ctrl+Shift добавляет все записи от последней выделенной кликом
    // до этой (как в задачах), обычный клик выделяет одну и открывает запись (при настройке
    // «двойным кликом» — на втором клике), правый — меню записи: вне выделения запись выделяется
    // одна, внутри — выделение сохраняется, и меню работает по всему набору.
    private void OnNumPressed(TableRowVm vm, bool left, bool right, bool toggle, bool range, int clickCount)
    {
        ClearCellSelection();   // либо ячейки, либо строки: иначе Delete был бы двусмысленным
        if (vm.IsAddRow || vm.RecordId <= 0) return;   // заглушка «Загрузка…» — записи еще нет

        if (left && range && SelectRecordRange(vm)) return;
        if (left && toggle)
        {
            RecordsGrid.ToggleRecordSelection(vm.RecordId);
            if (RecordsGrid.IsRecordSelected(vm.RecordId)) _recordAnchor = vm.RecordId;
            return;
        }
        if (left)
        {
            RecordsGrid.SetRecordSelection([vm.RecordId]);
            _recordAnchor = vm.RecordId;
            if (!ConstructorSettings.OpenByDoubleClick || clickCount >= 2) RecordClicked?.Invoke(this, vm.RecordId);
            return;
        }
        if (right)
        {
            if (!RecordsGrid.IsRecordSelected(vm.RecordId)) RecordsGrid.SetRecordSelection([vm.RecordId]);
            OpenMenu(BuildRowContextMenu(() => vm));
        }
    }

    // Якорь диапазона записей — последняя выделенная кликом по «№»: Ctrl+Shift+клик добавляет все
    // записи между ней и кликнутой. Хранится записью, а не номером строки: после сортировки или
    // правки соседа строка якоря могла переехать.
    private int _recordAnchor;

    /// <summary>Ctrl+Shift+клик по «№»: добавить к выделению записи от якоря до <paramref name="vm"/>
    /// по порядку показа. false — якоря нет или его строки сейчас нет в загруженных: тогда клик
    /// работает как Ctrl+клик.</summary>
    private bool SelectRecordRange(TableRowVm vm)
    {
        if (_recordAnchor <= 0 || RowsSource?.FindCachedByRecordId(_recordAnchor) is not { } anchor) return false;
        var from  = Math.Min(anchor.RowNumber, vm.RowNumber) - 1;
        var count = Math.Abs(anchor.RowNumber - vm.RowNumber) + 1;
        Fire(async () =>
        {
            // Строки между якорем и кликом бывают не загружены (прокрутили далеко) — их id
            // дочитываются тем же путем, что и выделение ячеек.
            if (await RowsOfRangeAsync(from, count, []) is not { } rows) return;
            RecordsGrid.SetRecordSelection(RecordsGrid.SelectedRecordIds.Concat(
                rows.Where(r => !r.IsAddRow && r.RecordId > 0).Select(r => r.RecordId)).ToList());
        });
        return true;
    }

    private int ColumnIndexOf(string key) => _colDefs.FindIndex(d => d.Key == key);

    // ── Протягивание с автопрокруткой ─────────────────────────────────────────

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _cellRange is null) return;
        var surface = RecordsGrid.Surface;
        // Кнопку отпустили где-то вне таблицы (захват уже снят) — выделение больше не тянется.
        if (!e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) { EndDrag(); return; }
        _dragPoint = e.GetPosition(surface);
        DragStep();
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && ReferenceEquals(e.Pointer.Captured, RecordsGrid.Surface)) e.Pointer.Capture(null);
        EndDrag();

        if (_editOnRelease is { } pending)
        {
            _editOnRelease = null;
            if (e.InitialPressMouseButton == MouseButton.Left && _cellRange is { IsSingleCell: true } range
                && range.AnchorRow == pending.Row && range.AnchorColumn == pending.Column)
                BeginEdit(pending.Row, pending.Column, byMouse: true);
        }

        if (_buttonPress is not { } press) return;
        _buttonPress = null;
        if (e.InitialPressMouseButton != MouseButton.Left || !press.Area.Contains(e.GetPosition(RecordsGrid.Surface))) return;
        switch (press.Kind)
        {
            case GridButton.Paperclip: PickFile(press.Row, press.Key); break;
            case GridButton.Checkbox:  ToggleCheckbox(press.Row, ColumnIndexOf(press.Key)); break;
            case GridButton.Filter:    ShowFilterFlyoutAt(press.Key, press.Area); break;
        }
    }

    // Окно фильтра и сортировки у правого края кнопки заголовка; якорь — невидимый прямоугольник
    // сетки на месте нарисованной кнопки (Flyout открывается только у контрола).
    private void ShowFilterFlyoutAt(string key, Rect button)
    {
        if (_colDefs.FirstOrDefault(d => d.Key == key) is not { } def) return;
        ShowFilterFlyout(RecordsGrid.AnchorAt(button), def);
    }

    private void EndDrag()
    {
        _dragging = false;
        _autoScroll?.Stop();
    }

    // Один шаг протягивания: указатель за краем области строк — прокрутка в его сторону (и таймер,
    // пока он там стоит), затем фокус выделения переезжает на ячейку под прижатой к краю точкой.
    private void DragStep()
    {
        if (!_dragging || _cellRange is not { } range) { _autoScroll?.Stop(); return; }
        var surface = RecordsGrid.Surface;

        var (clamped, dx, dy) = DragScroll.Step(_dragPoint, RecordsGrid.BodyRect(), surface.RowPitch);
        if (dx != 0 || dy != 0)
        {
            RecordsGrid.ScrollBy(dx, dy);
            _autoScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => DragStep());
            if (!_autoScroll.IsEnabled) _autoScroll.Start();
        }
        else _autoScroll?.Stop();

        if (CellNear(clamped) is not { } cell) return;
        if (cell.Row == range.FocusRow && cell.Column == range.FocusColumn) return;
        SetCellSelection(range.WithFocus(cell.Row, cell.Column));
    }

    // Ячейка значения, ближайшая к точке поверхности: строка по раскладке (за краями и на строке
    // «+» — крайняя строка с данными), колонка по раскладке; служебные «№» и «+» — ближайшая
    // колонка с данными. Координаты — позиции показанной таблицы, как у CellRange.
    private (int Row, int Column)? CellNear(Point p)
    {
        var surface  = RecordsGrid.Surface;
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
    /// Открыть правку ячейки: второй клик по ней, Enter или F2 на якоре выделения, набор на нем
    /// (<c>BeginEditByTyping</c> — он же потом кладет набранное в редактор или поиск). Редактор
    /// (<c>BuildCellEditor</c>) кладется над ячейкой. Фокус — в поле у текстовых типов, в список у
    /// списков; у даты, мультисписка и связи остается в таблице — их открывают кликом, а Enter, Tab
    /// и Escape работают с таблицы. Галочка, файл и вычисляемые колонки — только для чтения (правятся
    /// кликом или никак). Без права правки записей — окно с причиной, если правку просили мышью;
    /// клавиатура молчит. Возвращает открытый редактор; null — правка не открылась.
    /// </summary>
    private Control? BeginEdit(int row, int column, bool byMouse)
    {
        if (!IsDataColumn(column) || CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm) return null;
        var def = _colDefs[column];
        if (def.IsReadOnly) return null;
        if (!Can(PermAction.EditRecord))
        {
            if (byMouse) PermissionNotice.Show(this, PermAction.EditRecord);
            return null;
        }
        if (IsCellEditing) CommitEdit(focusGrid: false);

        ScrollCellIntoView(row, column);
        var editor = BuildCellEditor(def, vm, RecordsGrid.RelationLinks);
        editor.DataContext = vm;
        // Редактор связи пишет связи сам и выбрасывает запись из кеша, а ячейка под ним читает только
        // кеш — перечитываем, чтобы после правки она показала новые названия.
        if (editor is RelationSelectBox relation)
        {
            var rid = vm.RecordId;
            relation.Changed += (_, _) => RecordsGrid.Rows?.RefreshLinks(rid);
        }
        if (editor is TextBox { TextAlignment: TextAlignment.Right } rightAligned) KeepRightTextInPlace(rightAligned);
        OpenEditor(row, column, vm, editor);
        if (editor is TextBox or ComboBox) editor.Focus();
        return editor;
    }

    // Поле ввода раскладывает текст по ширине, округленной ВВЕРХ до целого DIP (так делает его
    // презентер текста), а ширины колонок сетки кратны физическому пикселю — при масштабе 2 бывают
    // половинки. Прижатый вправо текст (число) уезжал бы от этого на полпикселя вправо от показа.
    // Дробный остаток уходит в левый отступ: ширина текста целая, правый край — там же, где у показа
    // (стережет Tests/EditGeometry).
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

    private void OpenEditor(int row, int column, TableRowVm vm, Control editor)
    {
        var def = _colDefs[column];
        ResetTyping();   // режимы набора ставит BeginEditByTyping уже после открытия
        if (editor is ComboBox) _listKey = def.Key;   // выбор списка пишется при записи правки
        IsCellEditing  = true;
        _editingCell   = (row, column);
        _editRow       = vm;
        _editRecordId  = vm.RecordId;
        _editingBox    = editor as TextBox;   // текст пишется один раз, при коммите (FlushEditingBox)
        _editingBoxRow = vm;
        _editingBoxKey = def.Key;
        _editingBoxDef = def;

        // Текст сверху или снизу высокой строки: редактор стоит в полосе у края высотой со свой текст.
        // У поля ввода полоса растет с набранными переводами строки (Alt+Enter), у остальных — по
        // строкам показа значения (мультисписок через перенос строки); после правки держатель
        // переставляет полосу.
        Func<int> lines;
        if (editor is TextBox box)
        {
            lines = () => TextLineCount(box.Text);
            box.TextChanged += (_, _) => RelayoutEditor(box);
        }
        else
        {
            lines = () => def.FieldType is "number" or "relation" ? 1
                        : TextLineCount(CellDisplayText.Of(def.FieldType, vm[def.Key], def.NumberFormat, def.Separator));
            if (editor is MultiSelectBox multi) multi.Changed += (_, _) => RelayoutEditor(multi);
        }
        // Длинный текст виден целиком: поле ввода текста и ссылки раскрывается поверх соседних ячеек
        // (настройка «Раскрывать ячейку при вводе»). Число и прочие типы остаются в ячейке.
        var grow = ConstructorSettings.ExpandCellEditor && editor is TextBox && def.FieldType is "text" or "url";
        RecordsGrid.Editor.Open(row, def.Key, editor, lines, grow);
    }

    private void RelayoutEditor(Control editor)
    {
        if (ReferenceEquals(RecordsGrid.Editor.Editor, editor)) RecordsGrid.Editor.Relayout();
    }

    private static int TextLineCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var count = 1;
        foreach (var ch in text) if (ch == '\n') count++;
        return count;
    }

    /// <summary>Закрыть правку с записью набранного: Enter, Tab, клик мимо редактора, подмена строк,
    /// закрытие окна. Список пишет выбор здесь же (<c>FlushDeferredList</c>); мультисписок, связь и
    /// выбор даты в календаре уже записали свое на каждом изменении.</summary>
    private void CommitEdit(bool focusGrid)
    {
        if (!IsCellEditing) return;
        // Путь, набранный руками: FileEditor пишет его сам и закрывает правку своим событием.
        if (RecordsGrid.Editor.Editor is FileEditor file) file.CommitManualEdit();
        var live = IsCellEditing && _editRow is { } row && row.RecordId == _editRecordId;
        // Дату, набранную руками, принимаем явно: поле узнало бы об уходе фокуса только после снятия
        // редактора, а строке к тому времени могла достаться другая запись — тогда набранное не пишем.
        if (RecordsGrid.Editor.Editor is DateEditor date)
        {
            if (live) date.CommitManualInput();
            else      date.CancelManualInput();
        }
        if (live)
        {
            FlushEditingBox();
            FlushDeferredList(_editRow!);
        }
        CloseEditor(focusGrid);
    }

    /// <summary>Закрыть правку без записи: Escape, правка этой же записи в панели записи.</summary>
    private void CancelEdit(bool focusGrid)
    {
        if (!IsCellEditing) return;
        if (RecordsGrid.Editor.Editor is FileEditor file) file.CancelManualEdit();
        // Иначе уход фокуса из снятого поля даты записал бы набранное руками.
        if (RecordsGrid.Editor.Editor is DateEditor date) date.CancelManualInput();
        CloseEditor(focusGrid);
    }

    // Снять редактор. Фокус — таблице, если его просили (Enter, Tab, Escape) или он был в редакторе:
    // иначе он ушел бы в никуда вместе с редактором. Ушедший в панель записи фокус не отбирается.
    private void CloseEditor(bool focusGrid)
    {
        var hadFocus = RecordsGrid.Editor.IsKeyboardFocusWithin;
        DropEditingBox();
        IsCellEditing = false;
        _editingCell  = null;
        _editRow      = null;
        _editRecordId = 0;
        RecordsGrid.Editor.Close();
        // Режимы набора — после снятия редактора: снимаемый список еще может сообщить о выборе, и
        // отложенная правка его не запишет.
        ResetTyping();
        if (focusGrid || hadFocus) RecordsGrid.Surface.Focus();
    }

    // Строки перестроились (заполнение страницы, структурное обновление): редактор едет за своей
    // записью. Строке досталась другая запись — правка закрывается без записи (набранное ушло бы в
    // чужую запись); строки редактора больше нет в списке — закрывается с записью.
    private void FollowEdit()
    {
        if (!IsCellEditing || _editRow is not { } vm || _editingCell is not { } cell) return;
        if (vm.RecordId != _editRecordId) { CancelEdit(focusGrid: false); return; }
        var index = vm.RowNumber - 1;
        if (!ReferenceEquals(CachedRowAt(index), vm)) { CommitEdit(focusGrid: false); return; }
        if (index == cell.Row) return;
        _editingCell = (index, cell.Column);
        RecordsGrid.Editor.MoveTo(index);
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCellEditing || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        CancelEdit(focusGrid: true);
    }

    // ── Галочка и «Файл» ──────────────────────────────────────────────────────

    // Кнопка ячейки под точкой: квадрат галочки или скрепка «Файла» (у авто-привязки ее нет).
    private (Rect Area, bool Paperclip)? ButtonAt(TableRowVm vm, TableColumnDef def, Rect cell, Point p)
    {
        if (vm.IsLoading || vm.IsAddRow) return null;
        var surface = RecordsGrid.Surface;
        if (def.FieldType == "checkbox")
            return surface.CheckboxRect(cell) is var box && box.Contains(p) ? (box, false) : null;
        if (def.FieldType == "file" && surface.FileButtonRect(cell, def) is { } clip && clip.Contains(p))
            return (clip, true);
        return null;
    }

    /// <summary>Переключить галочку (клик по квадрату, пробел на выделенной ячейке). false — в ячейке
    /// не галочка.</summary>
    private bool ToggleCheckbox(int row, int column)
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
    private async void PickFile(int row, string key)
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
    private void OnFileLinkPressed(int row, TableColumnDef def, string path, int clickCount)
    {
        if (def.FileAutoFolder.Length == 0 && clickCount >= 2
            && CachedRowAt(row) is { } vm && FileValue.ReadPaths(vm[def.Key]).Count <= 1)
        {
            BeginFileEdit(row, def);
            return;
        }
        var seq = _pressSeq;
        var key = def.Key;
        _ = Task.Delay(250).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (seq == _pressSeq) OnFileSingleClick(row, key, path);
        }), TaskScheduler.Default);
    }

    // Одиночный клик по имени: файл есть — открыть; нет — поправить путь руками; пусто («Выбрать
    // файл…») — ничего. Авто-привязка: папка недоступна — плашка, файл есть — открыть. Диск
    // спрашивается в фоне: на сетевой папке ответ идет секундами, а UI-поток ждать не должен.
    private void OnFileSingleClick(int row, string key, string path)
    {
        if (CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm) return;
        if (_colDefs.FirstOrDefault(d => d.Key == key) is not { } def) return;
        var seq    = _pressSeq;
        var rid    = vm.RecordId;
        var folder = def.FileAutoFolder;
        if (folder.Length == 0 && path.Length == 0) return;
        if (path.Length > 0 && !FileValue.ReadPaths(vm[def.Key]).Contains(path, StringComparer.Ordinal)) return;

        // Ответ — на UI-поток явно (стенды без контекста синхронизации).
        _ = Task.Run(() => (folder.Length == 0 || Directory.Exists(folder), path.Length > 0 && File.Exists(path)))
                .ContinueWith(t => Dispatcher.UIThread.Post(() =>
                {
                    if (!t.IsCompletedSuccessfully) return;
                    if (seq != _pressSeq || vm.RecordId != rid) return;   // пока ждали диск, щелкнули еще раз
                    var (folderOk, fileOk) = t.Result;
                    if (folder.Length > 0)
                    {
                        if (!folderOk) ShowCellNotice(row, def.Key, "Папка недоступна");
                        else if (fileOk) FileEditor.OpenWithSystem(path);
                        return;
                    }
                    if (fileOk) FileEditor.OpenWithSystem(path);
                    else if (FileValue.ReadPaths(vm[def.Key]).Count <= 1) BeginFileEdit(row, def);
                }), TaskScheduler.Default);
    }

    // Ручная правка пути: над ячейкой — FileEditor сразу с полем ввода (живая проверка пути, плашка
    // «Укажите путь…»). Путь пишет он сам; закончил правку — редактор снимается.
    private void BeginFileEdit(int row, TableColumnDef def)
    {
        if (def.FileAutoFolder.Length > 0) return;
        if (CachedRowAt(row) is not { IsLoading: false, IsAddRow: false } vm) return;
        if (!Can(PermAction.EditRecord)) { PermissionNotice.Show(this, PermAction.EditRecord); return; }
        var column = ColumnIndexOf(def.Key);
        if (column < 0) return;
        if (IsCellEditing) CommitEdit(focusGrid: false);

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
            if (IsCellEditing && ReferenceEquals(RecordsGrid.Editor.Editor, editor)) CloseEditor(focusGrid: false);
        };
        OpenEditor(row, column, vm, editor);
        // Поле пути — в содержимом FileEditor, а оно попадает в визуальное дерево с первой раскладкой;
        // до нее фокус в поле не ставится.
        editor.UpdateLayout();
        editor.BeginManualEdit();
    }

    // Плашка под ячейкой на пару секунд — как FlashError у FileEditor («Папка недоступна»).
    private void ShowCellNotice(int row, string key, string message)
    {
        if (RecordsGrid.Surface.EditorRect(row, key) is not { } cell) return;
        if (_notice is null)
        {
            _notice = new Popup
            {
                Placement             = PlacementMode.BottomEdgeAlignedLeft,
                VerticalOffset        = 2,
                IsLightDismissEnabled = false,
                InheritsTransform     = true,
            };
            Root.Children.Add(_notice);
        }
        var banner = new NoticeBanner { MinWidth = 220 };
        banner.Show(message);
        _notice.Child = new Border
        {
            Background   = this.FindResource("BrushBg") as IBrush,
            CornerRadius = this.FindResource("RadiusBase") is CornerRadius r ? r : default,
            Child        = banner,
        };
        _notice.PlacementTarget = RecordsGrid.Surface;
        _notice.PlacementRect   = cell;
        _notice.IsOpen          = true;

        if (_noticeTimer is null)
        {
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _noticeTimer.Tick += (_, _) =>
            {
                _noticeTimer.Stop();
                if (_notice is not null) _notice.IsOpen = false;
            };
        }
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    // ── Рамка и выделение — сетке ─────────────────────────────────────────────

    // Строки или колонки сменились (новый список, заполнение страницы, пересборка колонок):
    // выделение догоняет свои запись и столбцы, рамка и счетчик обновляются.
    private void SyncSelection()
    {
        if (_cellRange is not null) FollowSelectionIdentity();
        PushCellSelection();
        RaiseSelectionLabel();
    }

    // Рамка — сетке: строки среза и ключи крайних ВИДИМЫХ колонок диапазона (скрытые внутри не
    // считаются — их нет ни в раскладке, ни в буфере). Тонировка — когда видимых ячеек больше одной.
    private void PushCellSelection()
    {
        if (_cellRange is not { } range) { RecordsGrid.ClearCellSelection(); return; }
        var columns = range.VisibleColumns(VisibleDataColumns());
        if (columns.Count == 0) { RecordsGrid.ClearCellSelection(); return; }
        RecordsGrid.SetCellSelection(range.Top, range.Bottom, _colDefs[columns[0]].Key, _colDefs[columns[^1]].Key,
                                     tinted: (long)range.RowCount * columns.Count > 1);
    }
}
