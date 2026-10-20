using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Logging;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Views.ConstructorDb.DrawnGrid;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Таблица коллекции — фасад над рисованной сеткой (<see cref="GridPanel"/>, план drawn-grid.md).
/// Сетка рисует и раскладывает: шапку, ячейки, линии, цвета, выделение, наведение, полосы, накладной
/// редактор; данные листает <see cref="VirtualRowList"/>. Здесь — все, что знает о коллекции и о
/// программе вокруг: определения колонок с меню и хуками, права, фильтр и сортировка (окно фильтра,
/// память отбора), контекстные меню, операции над записями и свойствами, события наверх. Модель
/// выделения, буфер обмена, отмена и клавиши — в партиалах <c>CellSelection</c>, <c>CellClipboard</c>,
/// <c>CellUndo</c>, <c>MenuShortcuts</c>; перевод нажатий и клавиш сетки в ее вызовы и правка ячейки —
/// в <c>GridInput</c>. Публичный API таблицы (открытие, перезагрузки, прокрутка, выделение записей)
/// остался прежним — контроллер и панель записи о сетке не знают.
/// </summary>
public partial class TableView : UserControl
{
    // ── Публичные события ─────────────────────────────────────────────────────
    public event EventHandler<int>? RecordClicked;
    // Переход к записи в ДРУГОЙ БД (чип межбазовой связи): (DbId цели, Id записи).
    public event EventHandler<(int DbId, int RecordId)>? CrossDbRecordRequested;
    // Значение записи изменено прямо в таблице (правка ячейки: имя/описание/поле). Аргумент —
    // RecordId. MainWindow обновляет открытую панель «Страница записи», если это та же запись.
    public event EventHandler<int>? RecordValueEdited;
    // Удаление записей из таблицы (одной или мультивыделения: Shift/Ctrl-клик → меню
    // «Удалить» или клавиша Delete) — всегда пакетно, с подтверждением; для одной записи
    // список из одного id (как удаление свойств).
    public event EventHandler<IReadOnlyList<int>>? RecordsDeleteRequested;
    // Дубль записей: список id (как удаление). Одна запись из меню строки — список из одного id;
    // если строка входит в мультивыделение — дублируется весь набор.
    public event EventHandler<IReadOnlyList<int>>? RecordDuplicateRequested;
    // «Расположение»: перенести записи в другую коллекцию/БД (список id, как дубль/удаление).
    // Одна запись из меню строки — список из одного id; если строка в мультивыделении — весь набор.
    public event EventHandler<IReadOnlyList<int>>? RecordRelocateRequested;
    // Open = открывать ли запись справа после создания. Из таблицы все три пути («+»-строка,
    // меню пустой области, меню строки) решают это одинаково — по настройке «Открывать новую
    // запись»; стандартно она выключена, то есть запись появляется в таблице, а панель не
    // выезжает. Событие несет уже готовый ответ, чтобы контроллер не знал о настройках.
    // AfterId = id записи-инициатора (меню строки) — создать сразу после нее; null —
    // создать в конце («+»-строка и меню пустой области таблицы).
    public event EventHandler<(bool Open, int? AfterId)>? RecordCreateRequested;
    public event EventHandler?      PropertyCreateRequested;
    // Создать свойство сразу справа от колонки (string = ключ колонки-инициатора).
    public event EventHandler<string>? PropertyCreateAfterRequested;
    public event EventHandler<int>? PropertyEditRequested;
    // Дублирование свойств (одного или мультивыделения колонок: меню «Дублировать») — всегда
    // пакетно, как удаление; для одного поля список из одного id.
    public event EventHandler<IReadOnlyList<int>>? PropertyDuplicateRequested;
    // Открыть окно «Отображение» (менеджер видимости/уровней колонок). Окно глобальное
    // (список всех колонок), поэтому без аргумента — инициатор-колонка не важна.
    public event EventHandler? ColumnDisplayRequested;
    // Открыть окно «Отображение записей» (менеджер видимости/уровней строк). Окно
    // глобальное (список всех записей), поэтому без аргумента — инициатор-строка не важна.
    public event EventHandler? RecordDisplayRequested;
    // Удаление пользовательских полей (Delete или меню «Удалить») — всегда пакетно,
    // с подтверждением; для одного поля список из одного id.
    public event EventHandler<IReadOnlyList<int>>? PropertiesDeleteRequested;
    // Переименование системной колонки «Название»/«Описание» (string = ключ колонки).
    public event EventHandler<string>? SystemFieldRenameRequested;

    /// <summary>Срабатывает после построения колонок — родитель может навесить хуки на дефы.</summary>
    public event EventHandler<IReadOnlyList<TableColumnDef>>? ColumnsBuilt;

    // ── Константы геометрии ───────────────────────────────────────────────────
    // MinColWidth — СТАНДАРТНАЯ ширина движимой колонки (настройка раздела, стандарт 160):
    // колонка без ручной ширины в стандартном режиме не бывает уже нее. Значение читается на
    // месте — определения колонок строятся заново при каждой загрузке и смене настройки.
    private static int MinColWidth      => ConstructorSettings.ColWidth;
    private const int NumColWidth       = 36;   // ширину «№» под число записей меряет сама сетка
    private const int PlusColWidth      = 36;

    // Боковой отступ содержимого ячейки — ОДИН на показ и на правку. Показ рисует сетка с этим
    // отступом, правка ставит его Padding'ом редактора; разойдись эти два числа, и текст прыгал бы
    // вбок при входе в правку (редактор без Padding берет общий стандарт полей формы 10px).
    // Правый отступ у списков больше: там за ним шеврон.
    private const double CellContentPad = 6;
    private const double CellDropdownPadRight = 8;
    private static readonly Thickness CellContentMargin  = new(CellContentPad, 0, CellContentPad, 0);
    private static readonly Thickness CellDropdownPadding = new(CellContentPad, 0, CellDropdownPadRight, 0);
    // Список «по центру»: слева — колонка стрелки шаблона (32, ComboBoxTheme) плюс правый отступ,
    // полоса значения симметрична относительно ячейки.
    private static readonly Thickness CellDropdownCenteredPadding = new(32 + CellDropdownPadRight, 0, CellDropdownPadRight, 0);

    // ── Состояние ─────────────────────────────────────────────────────────────
    private ConstructorRepository? _repository;
    private int  _collectionId;
    private bool _manualColResize;
    private bool _manualRowResize;
    private List<DbField>        _fields  = [];
    // Снимок открытия коллекции, прочитанный в фоне (см. Load с snapshot): пока он есть,
    // LoadColumnState берет данные из него, а не ходит в базу с UI-потока.
    private CollectionOpenSnapshot? _snapshot;
    private List<TableColumnDef> _colDefs = [];
    // Пользовательские подписи системных колонок: ключ → отображаемое имя.
    private Dictionary<string, string> _systemNames = [];
    // Сохраненный порядок колонок: ключ → позиция (пусто, если еще не двигали).
    private Dictionary<string, int> _columnOrder = [];
    // Скрытые колонки (окно «Отображение»): ключ есть в множестве ⇒ колонка скрыта.
    // Колонки из _colDefs не удаляются (порядок/перемещение целы) — в раскладке сетки их просто нет.
    private HashSet<string> _hiddenColumns = [];
    // Выделенные по клику в заголовок колонки (голубая заливка ячеек — как у выделенной
    // записи). Пусто — нет выделения. Обычный клик — одна колонка; Shift+клик — toggle.
    private readonly List<string> _selectedColumnKeys = [];

    // ── Фильтр/сортировка колонок (чеклист значений) ───────────────────────────────
    // key колонки → набор разрешенных значений; null-наличие ключа = фильтра нет.
    private readonly Dictionary<string, HashSet<string>> _filters = [];
    private string? _sortKey;
    private bool    _sortAsc = true;

    // Оформление колонок ЭТОЙ коллекции (ключ колонки → стиль) — читается вместе с состоянием
    // колонок и отдается сетке; оформление строк и ячеек живет в ней и в самих строках.
    private Dictionary<string, RowStyle> _columnStyles = [];

    // Ручные ширины колонок: ключ → ширина (DIP). Читаются вместе с состоянием колонок и отдаются
    // сетке; сама модель живет в ней (жест меняет ее и сохраняет один раз на отпускание).
    private Dictionary<string, double> _colWidths = [];

    public TableView()
    {
        InitializeComponent();
        // Фокусируемый контрол: при Shift/Ctrl-выделении строк уводим фокус сюда из
        // текстовых полей (например, правой панели открытой записи), иначе Delete будет
        // трактоваться как «удалить символ», а не как удаление выделенных записей.
        Focusable = true;
        // Delete по выделенным колонкам и записям, Enter и Tab в правке. Tunnel — раньше редактора.
        AddHandler(KeyDownEvent, OnTableKeyDown, RoutingStrategies.Tunnel);
        InitGridInput();   // нажатия, протягивание, правка — TableView.GridInput.cs
    }

    /// <summary>Пользователь сейчас редактирует ячейку (открыт накладной редактор).</summary>
    public bool IsCellEditing { get; private set; }

    // Какая именно ячейка сейчас в правке (позиции строки и колонки).
    private (int Row, int Column)? _editingCell;

    // Две последние ячейки, по которым нажали левой кнопкой: правка по мыши открывается, только
    // если ПРЕДЫДУЩИЙ клик пришелся в ту же ячейку (см. OnGridPressed).
    private (int Row, int Column)? _pressPrevCell;
    private (int Row, int Column)? _pressLastCell;

    /// <summary>Закрыть открытую правку с записью набранного — перед подменой строк (новый список,
    /// пересборка колонок): набранное пишется в строку, пока она жива.</summary>
    private void ForgetCellEditing()
    {
        if (IsCellEditing) CommitEdit(focusGrid: false);
    }

    // Открытый текстовый редактор ячейки (текст, ссылка, число, «Название»): сам TextBox, его
    // строка и ключ столбца. В базу значение уходит из FlushEditingBox: при коммите правки, при
    // уходе фокуса из редактора (человек кликнул в панель записи, а редактор остался открыт) и
    // когда редактор снимается вместе со строками (ForgetCellEditing). Каждая буква отдельной
    // транзакцией шла бы в базу заметной задержкой на сетевой папке и будила бы панель записи.
    private TextBox?        _editingBox;
    private TableRowVm?     _editingBoxRow;
    private string?         _editingBoxKey;
    private TableColumnDef? _editingBoxDef;

    private void FlushEditingBox()
    {
        if (_editingBox is not { } box || _editingBoxRow is not { } row || _editingBoxKey is not { } key) return;
        try { row[key] = CommitText(box.Text ?? "", _editingBoxDef); }   // строка пишет в базу сама и совпавшее пропускает
        catch (Exception ex)
        {
            // База уже отключена (редактор закрывается вместе с ней) — набранное не спасти, но и
            // ронять закрытие нельзя.
            Serilog.Log.Warning(ex, "Значение ячейки при закрытии редактора не записалось");
        }
    }

    private void DropEditingBox()
    {
        _editingBox    = null;
        _editingBoxRow = null;
        _editingBoxKey = null;
        _editingBoxDef = null;
    }

    /// <summary>Текст редактора → значение для хранения. Число правится в том же виде, в каком
    /// показано («1 234,50 ₽»), поэтому перед записью его надо разобрать обратно в канон. Все
    /// остальные типы (и число, которое разобрать не удалось) пишутся как набрано — так же, как
    /// хранится нечисловое значение после смены типа поля.</summary>
    private static string CommitText(string text, TableColumnDef? def)
    {
        if (def is not { FieldType: "number" } d) return text;
        if (text.Trim().Length == 0) return "";
        return NumberFormatValue.TryParseDisplay(text, d.NumberFormat, d.NumberFormat.DecimalSeparator, out var storage)
            ? storage
            : text;
    }

    private void OnEditingBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _editingBox)) FlushEditingBox();
    }

    /// <summary>Закрыть открытую правку ячейки с записью значения — перед закрытием окна и уходом
    /// в фоновый режим: иначе набранное в редакторе пропало бы вместе с ним.</summary>
    public void CommitCellEdit()
    {
        if (IsCellEditing) CommitEdit(focusGrid: false);
    }

    // Записать правку, закрыть редактор и вернуть фокус таблице (Enter, Tab): без возврата следующий
    // Ctrl+C или Delete до таблицы не дошел бы — редактор ушел вместе с фокусом.
    private void CommitEditAndFocusGrid() => CommitEdit(focusGrid: true);

    // Окно, на которое навешен обработчик Delete для записей (см. OnTopLevelKeyDown).
    private TopLevel? _keyHookTopLevel;

    // Записи удаляем по Delete на уровне окна, а не только когда фокус внутри таблицы:
    // после выделения строк фокус часто оказывается вне сетки (правая панель открытой
    // записи и т.п.), и туннельный OnTableKeyDown на TableView клавишу не получает —
    // именно поэтому Delete «не работал». На уровне окна клавиша приходит всегда.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyHookTopLevel = TopLevel.GetTopLevel(this);
        _keyHookTopLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
        _keyHookTopLevel?.AddHandler(KeyUpEvent,   OnTopLevelKeyUp,   RoutingStrategies.Tunnel);

        // Размер шрифта и стандартная ширина столбцов меняются живьем: сетка перечитывает токены
        // сама, а раскладку колонок (ширины от стандартной) просит фасад.
        FontScale.Changed += OnFontScaleChanged;
        if (_repository is not null && _renderedFontDelta != FontScale.Delta) Reload();
        ConstructorSettings.Changed += OnConstructorSettingsChanged;
        OnConstructorSettingsChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyHookTopLevel?.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
        _keyHookTopLevel?.RemoveHandler(KeyUpEvent,   OnTopLevelKeyUp);
        _keyHookTopLevel = null;
        FontScale.Changed -= OnFontScaleChanged;
        ConstructorSettings.Changed -= OnConstructorSettingsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    // Сверяем ЗНАЧЕНИЕ, а не Stamp: событие Changed поднимают все настройки раздела, а раскладку
    // колонок из них меняет только стандартная ширина. Высота строк доезжает до сетки сама
    // (она слушает те же настройки и перечитывает токены).
    private int _renderedColWidth = ConstructorSettings.ColWidth;

    private void OnConstructorSettingsChanged()
    {
        var widthChanged  = _renderedColWidth != ConstructorSettings.ColWidth;
        _renderedColWidth = ConstructorSettings.ColWidth;
        if (_repository is null || !widthChanged) return;
        Reload();   // новая раскладка, строки и прокрутка на месте
    }

    // Дельта последнего рендера — чтобы перерисовать таблицу при возврате, если размер
    // меняли, пока был открыт другой раздел.
    private int _renderedFontDelta = FontScale.Delta;
    private void OnFontScaleChanged() { if (_repository is not null) Reload(); }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        // IsEffectivelyVisible, а не IsVisible: разделы прячутся через своего предка (модуль
        // конструктора), и у самой таблицы IsVisible остается true. С прежней проверкой Delete в
        // «Задачах» стирал значения невидимой таблицы и заодно удалял выделенную задачу.
        if (!IsEffectivelyVisible) return;
        if (IsTextInputFocused(e)) return;            // печатают в поле — таблица молчит
        if (IsCellEditing) return;                    // клавиши правят значение в редакторе

        // Пункты контекстных меню с клавиатуры — тем же путем, что клик по пункту (права и
        // прогресс спрашивает контроллер).
        if (HandleMenuShortcut(e)) { e.Handled = true; return; }

        // Отмена последней вставки, очистки или вырезания в этой таблице. Выделение для нее не
        // нужно (после вставки его могли снять), нужен лишь фокус в таблице.
        if (HasUndo && IsKeyboardFocusWithin && ShortcutSettings.Matches("table.undo", e))
        {
            Fire(UndoLastCellChangeAsync);
            e.Handled = true;
            return;
        }

        // Выделены ячейки — копирование, вставка и очистка работают по прямоугольнику.
        // Только пока фокус В ТАБЛИЦЕ: иначе Delete по выделенным свойствам в панели записи уходил
        // в ячейки, выделенные когда-то раньше. Проверяем ДО удаления записей: пока выделены
        // ячейки, строки не выделены (одно гасит другое).
        if (HasCellSelection && IsKeyboardFocusWithin && _selectedColumnKeys.Count == 0)
        {
            if (HandleSelectionKey(e)) { e.Handled = true; return; }

            // Сравнение идет с ЦЕЛЫМ сочетанием, поэтому Ctrl+Shift+C и прочие соседи по клавише
            // таблице по-прежнему не принадлежат — ими владеет тот, кому они назначены.
            if (ShortcutSettings.Matches("table.copy",  e)) { Fire(CopyCellsAsync);  e.Handled = true; return; }
            if (ShortcutSettings.Matches("table.cut",   e)) { Fire(CutCellsAsync);   e.Handled = true; return; }
            if (ShortcutSettings.Matches("table.paste", e)) { Fire(PasteCellsAsync); e.Handled = true; return; }
            if (ShortcutSettings.Matches("table.clearCells", e))
            {
                Fire(() => ClearCellsAsync());
                e.Handled = true;
                return;
            }
        }

        if (!ShortcutSettings.Matches("table.deleteRecords", e)) return;
        if (_selectedColumnKeys.Count > 0) return;    // удаление колонок — в OnTableKeyDown

        // Фокус в панели записи: там Delete удаляет выделенные свойства, и отнимать у нее
        // клавишу ради записей таблицы нельзя — туннель окна идет раньше обработчика панели.
        if (e.Source is Visual source && source.FindAncestorOfType<RecordPageView>() is not null) return;

        var ids = SelectedRecordIds();
        if (ids.Count == 0) return;

        RecordsDeleteRequested?.Invoke(this, ids);
        e.Handled = true;
    }

    // Отпускание Tab при выделенных ячейках забираем следом за нажатием (его взял HandleSelectionKey):
    // иначе фокус ушел бы с сетки на следующий контрол окна.
    private void OnTopLevelKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && IsEffectivelyVisible && HasCellSelection && IsKeyboardFocusWithin)
            e.Handled = true;
    }

    private void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        // Клавиши, которые зависят от того, как начата правка и что правится: стрелки «ввода», F2,
        // Escape списка и выбора, пробел и Backspace набранного в списке (TableView.CellTyping.cs).
        if (IsCellEditing && HandleTypingKey(e)) return;

        if (IsCellEditing && e.Key == Key.Enter)
        {
            OnEnterWhileEditing(e);
            return;
        }

        // Tab в редакторе: записать значение и перейти к соседней ячейке — выделением, а не правкой.
        if (IsCellEditing && e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            e.Handled = true;
            TakeFocusedListItem();
            CommitEditAndFocusGrid();

            var key = e.KeyModifiers == KeyModifiers.Shift ? CellNavKey.ShiftTab : CellNavKey.Tab;
            if (_cellRange is { } range
                && CellRangeNavigation.Navigate(range, key, extend: false, VisibleRowCount, VisibleDataColumns(), PageRows()) is { } next)
            {
                SetCellSelection(next);
                ScrollCellIntoView(next.FocusRow, next.FocusColumn);
            }
            return;
        }

        var deleteColumns = ShortcutSettings.Matches("table.deleteColumns", e);
        var deleteRecords = ShortcutSettings.Matches("table.deleteRecords", e);
        if (!deleteColumns && !deleteRecords) return;

        // Колонки имеют приоритет: их выделение — явный жест по заголовку.
        if (deleteColumns && _selectedColumnKeys.Count > 0)
        {
            // Удаляемые = только пользовательские поля (ключ = id поля). Системные
            // («__title__») и №/+ не парсятся в int → отсекаются.
            var fieldIds = _selectedColumnKeys
                .Where(k => int.TryParse(k, out _))
                .Select(int.Parse)
                .ToList();
            if (fieldIds.Count == 0) return;

            // Перед показом диалога оставляем рамки только на удаляемых колонках:
            // с системных («Название»/«Описание») обводка снимается — их удаление не коснется.
            KeepSelectionOnDeletableOnly();
            PropertiesDeleteRequested?.Invoke(this, fieldIds);
            e.Handled = true;
            return;
        }

        // Записи: свое сочетание (стандартно только Delete — Backspace для строк не используем,
        // слишком легко задеть) и не пока редактируется ячейка (тогда клавиша правит текст).
        if (!deleteRecords || IsTextInputFocused(e)) return;

        var ids = SelectedRecordIds();
        if (ids.Count == 0) return;

        RecordsDeleteRequested?.Invoke(this, ids);
        e.Handled = true;
    }

    /// <summary>
    /// Enter в открытом редакторе только фиксирует значение, и выделена остается та же ячейка
    /// (Enter не «стрелка вниз»: иначе рамка и активная ячейка расходились бы, и следующий Delete
    /// или Ctrl+C уходил бы не туда). Так же и в правке, начатой набором.
    ///
    /// Alt+Enter в текстовой ячейке — перевод строки внутри значения. В остальных редакторах он
    /// ничего не делает (перенос в числе или дате превратил бы значение в текст), но и сетке не
    /// отдается.
    /// </summary>
    private void OnEnterWhileEditing(KeyEventArgs e)
    {
        // Раскрытый список Enter тоже записывает сразу, как текст: пункт под фокусом и есть выбор
        // (TakeFocusedListItem берет его так же, как сам список по Enter).
        e.Handled = true;

        if (e.KeyModifiers == KeyModifiers.Alt)
        {
            InsertLineBreak(e.Source);
            return;
        }

        TakeFocusedListItem();
        CommitEditAndFocusGrid();
    }

    // Перевод строки на месте каретки (выделенный текст заменяется) — только в тексте и ссылке.
    // «Название» — одна строка: с переводом оно рвалось бы в списках, боковой панели и связях.
    private void InsertLineBreak(object? source)
    {
        if (_editingCell is not { } cell || cell.Column < 0 || cell.Column >= _colDefs.Count) return;

        var def = _colDefs[cell.Column];
        if (def.Key == TableRowVm.TitleKey || def.FieldType is not ("text" or "url")) return;
        if (source is not Visual visual
            || visual.GetSelfAndVisualAncestors().OfType<TextBox>().FirstOrDefault() is not { } box) return;

        var text  = box.Text ?? "";
        var start = Math.Min(box.SelectionStart, box.SelectionEnd);
        var end   = Math.Max(box.SelectionStart, box.SelectionEnd);

        box.Text           = text[..start] + "\n" + text[end..];
        box.SelectionStart = box.SelectionEnd = box.CaretIndex = start + 1;
    }

    // Выделенные записи в виде списка их id. Пока выделены ЯЧЕЙКИ, записей выделенных нет: попадать
    // в удаление, дубль или «Действия» то, чего человек не видит выделенным, не должно.
    internal List<int> SelectedRecordIds() =>
        HasCellSelection ? [] : [.. RecordsGrid.SelectedRecordIds];

    // Фокус внутри текстового поля ячейки — чтобы Delete правил текст, а не удалял строки.
    private static bool IsTextInputFocused(KeyEventArgs e) =>
        e.Source is TextBox || (e.Source is Visual v && v.FindAncestorOfType<TextBox>() is not null);

    // ── Публичный API ─────────────────────────────────────────────────────────

    // Коллекция, отрисованная в прошлый Load — чтобы понять, сменилась ли она
    // (тогда прокрутка сбрасывается) или это обновление той же (прокрутка хранится).
    private int? _lastLoadedCollectionId;

    // Право текущего пользователя в открытой коллекции (нет сервиса прав — без ограничений).
    // Меню читают его при открытии, поэтому смена прав видна со следующего клика.
    private bool Can(PermAction a) => _repository?.Permissions?.Can(a, _collectionId) ?? true;

    // Запрещенное действие НЕ гасим, а объясняем: пункт остается кликабельным и по клику
    // показывает причину («Недостаточно прав: удаление записей запрещено администратором
    // базы данных») — как замок у коллекции без права просмотра. Молчаливо серый пункт
    // выглядел как поломка: непонятно, кто и что запретил.
    private void Guarded(MenuItem item, PermAction action, Action run) =>
        item.Click += (_, _) => PermissionNotice.Run(this, Can(action), action, run);

    public void Load(ConstructorRepository repository, int collectionId) => Load(repository, collectionId, null);

    /// <summary>Открыть коллекцию. <paramref name="snapshot"/> — данные открытия, прочитанные в фоне
    /// (<see cref="ConstructorRepository.ReadOpenSnapshotAsync"/>): с ним UI-поток в базу не ходит,
    /// только строит колонки; без него (обновление той же коллекции, смена вида) снимок читается на
    /// месте без COUNT — точное число записей сетка уточнит в фоне. Колонки строит фасад (с меню и
    /// хуками) по тому же снимку, что получает сетка, — снимок читается один раз. Отбор и сортировка
    /// не переносятся между коллекциями; при настройке «Запоминать фильтры и сортировку»
    /// подставляется запомненное за этой коллекцией, и пока оно считается, на экране прежние строки
    /// той же коллекции или пустая таблица — не все записи.</summary>
    public void Load(ConstructorRepository repository, int collectionId, CollectionOpenSnapshot? snapshot)
    {
        // Порог медленности: открытие коллекции (колонки, настройки) дольше Diag.UiOpen — в журнал.
        using var slow = Diag.Measure("открытие коллекции", Diag.UiOpen,
                                      $"{DbErrors.Display(repository.DbPath)}, коллекция {collectionId}");

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
                ForgetUndo();   // отмена шагов чужой коллекции скорее навредила бы
                ClearColumnSelection();
                RecordsGrid.ClearRecordSelection();
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

            RecordsGrid.Load(repository, collectionId, snap, _colDefs, keepOffsets: sameCollection,
                             CopyFilters(), _sortKey, _sortAsc);
            _lastLoadedCollectionId = collectionId;
        }
        finally
        {
            _snapshot = null;   // дальнейшие обновления читают базу сами — снимок устаревает
        }
    }

    // Текущая вертикальная прокрутка — для сохранения позиции при создании записи в середине таблицы.
    public double CurrentVerticalOffset() => RecordsGrid.CurrentVerticalOffset();

    // Восстанавливает вертикальную прокрутку после пересборки (доезжает по мере уточнения оценки
    // числа записей).
    public void RestoreVerticalOffset(double offset) => RecordsGrid.RestoreVerticalOffset(offset);

    // ── Навигация по строкам из нижней панели («‹‹ ‹ 10 – 50 из 125 437 › ››») ──────
    // Арифметика — в TableViewportScroll (покрыта тестами) на смещении сетки.

    /// <summary>Видимый кусок таблицы изменился (прокрутка, ресайз, перезагрузка строк).</summary>
    public event EventHandler<TableViewportRange>? VisibleRangeChanged;

    /// <summary>Экран назад (кнопка «‹»).</summary>
    public void ScrollRowsPageBack()    => RecordsGrid.ScrollRowsPageBack();

    /// <summary>Экран вперед (кнопка «›»).</summary>
    public void ScrollRowsPageForward() => RecordsGrid.ScrollRowsPageForward();

    /// <summary>В начало таблицы (кнопка «‹‹»).</summary>
    public void ScrollRowsToStart()     => RecordsGrid.ScrollRowsToStart();

    /// <summary>В конец таблицы (кнопка «››»).</summary>
    public void ScrollRowsToEnd()       => RecordsGrid.ScrollRowsToEnd();

    /// <summary>Номера строк, которые сейчас в окне, и общее число строк среза — из раскладки
    /// сетки арифметикой (<see cref="TableViewportRange.FromSlots"/>).</summary>
    public TableViewportRange VisibleRowRange() => RecordsGrid.VisibleRowRange();

    /// <summary>Прокрутить таблицу к правому краю — к колонке «+» (после добавления свойства):
    /// новая колонка и кнопка «+» остаются видны.</summary>
    public void ScrollToAddPropertyColumn() => RecordsGrid.ScrollToAddPropertyColumn();

    public void Clear()
    {
        ForgetCellEditing();   // набранное в открытом редакторе пишем, пока строки живы
        RecordsGrid.Clear();
        _repository   = null;
        _collectionId = 0;
        _lastLoadedCollectionId = null;
        _fields       = [];
        _colDefs      = [];
        _systemNames  = [];
        _columnOrder  = [];
        _hiddenColumns = [];
        _columnStyles = [];
        _colWidths    = [];
        _filters.Clear();
        _sortKey = null;
        ClearCellSelection();
        ClearColumnSelection();
        ForgetUndo();
    }

    // Режим ширины столбцов (из меню заголовка). Выключение сбрасывает сохраненные ширины на
    // стандарт; режим запоминается за коллекцией — обоими занимается сетка.
    public void SetColResizeMode(bool manual)
    {
        _manualColResize = manual;
        RecordsGrid.SetColResizeMode(manual);
    }

    // Текущий режим ширины столбцов (для отметки активного пункта в меню).
    public bool IsManualColResize => RecordsGrid.IsManualColResize;

    // Режим высоты строк (из меню записи). Выключение сбрасывает сохраненные высоты на стандарт;
    // режим запоминается за коллекцией.
    public void SetRowResizeMode(bool manual)
    {
        _manualRowResize = manual;
        RecordsGrid.SetRowResizeMode(manual);
    }

    // Текущий режим высоты строк (для отметки активного пункта в меню).
    public bool IsManualRowResize => RecordsGrid.IsManualRowResize;

    /// <summary>Показать запись и строку «+» под ней (после создания записи в конце). Строка в
    /// кеше — сразу; иначе ее индекс в срезе считается в фоне.</summary>
    public void ScrollToRecord(int recordId) => RecordsGrid.ScrollToRecord(recordId);

    // ── Построение таблицы ────────────────────────────────────────────────────

    // Смена шрифта или стандартной ширины столбцов: пересобирать нечего — сетка перечитывает
    // токены сама, ей хватает новой раскладки; строки и прокрутка на месте.
    private void Reload()
    {
        if (_repository is null) return;
        _renderedFontDelta = FontScale.Delta;   // таблица отрисована с текущим размером
        RecordsGrid.RefreshLayout();
    }

    /// <summary>Пересчитать строки, не трогая колонки: видимость записей, высоты, оформление строк
    /// перечитываются, отбор и сортировка считаются заново в фоне; прокрутка остается. Вызывать на
    /// операциях над ЗАПИСЯМИ, меняющих их состав или порядок целиком (видимость записей, правило
    /// файла) и при переключении ручной высоты строк.</summary>
    public void ReloadRows()
    {
        if (_repository is null) return;
        ForgetCellEditing();   // список строк сменится — набранное пишем, пока строки живы
        RecordsGrid.ReloadRows();
    }

    /// <summary>Структурное обновление строк (создание, удаление, дубль записей) без пересоздания
    /// списка: число строк и загруженные страницы обновляются на месте (теплый кеш, без заглушек
    /// «Загрузка…»). При отборе или сортировке список пересчитывается целиком.</summary>
    /// <paramref name="countDelta"/> — на сколько изменилось число записей; null — неизвестно
    /// (число уточнит фоновый счет).
    public void ReloadRowsStructural(int? countDelta = null)
    {
        if (_repository is null) return;
        // Строки перезаполняются на месте, и в строке редактора может оказаться другая запись.
        ForgetCellEditing();
        RecordsGrid.ReloadRowsStructural(countDelta);
    }

    /// <summary>Пересобрать колонки, СОХРАНЯЯ строки: сдвиг, «Отображение», создание, удаление,
    /// дубль, смена типа, переименование «Названия». Состояние колонок читается один раз и отдается
    /// сетке — строки и прокрутка остаются, ширины живут по ключам, выделение ячеек и колонок едет
    /// за своими ключами, отбор по удаленной колонке снимается.</summary>
    public void ReloadColumns()
    {
        if (_repository is null) return;
        ForgetCellEditing();   // колонка редактора могла скрыться или уехать
        LoadColumnState();
        _colDefs = BuildColDefs();
        ColumnsBuilt?.Invoke(this, _colDefs);
        RecordsGrid.ReloadColumns(_colDefs, _fields, _hiddenColumns, _columnStyles, _colWidths);

        // Удаленные колонки уходят из выделения колонок; оставшиеся — в новом порядке колонок, как их
        // держит SelectColumn (порядок удаления и дубля).
        _selectedColumnKeys.RemoveAll(k => ColumnIndexOf(k) < 0);
        _selectedColumnKeys.Sort((a, b) => ColumnIndexOf(a).CompareTo(ColumnIndexOf(b)));
        // Колонки могли переехать или скрыться: рамка и заливка колонок — по новым позициям.
        SyncSelection();
        ApplyColumnSelection();
        // Отбор по удаленной колонке снимается, строки пересчитываются без него.
        if (PruneFilters()) ApplyFilters();
    }

    // Состояние, нужное для построения КОЛОНОК (поля, подписи системных, порядок, скрытые,
    // оформление и ширины). Одна точка чтения на все пути пересборки колонок.
    private void LoadColumnState()
    {
        if (_repository is null) return;

        // Снимок открытия (прочитан в фоне) — вместо обращений к базе с UI-потока.
        var snap = _snapshot;
        _fields = (snap?.Fields ?? _repository.GetFields(_collectionId))
            .Where(f => f.FieldType != "markdown")
            .ToList();

        _systemNames = snap?.SystemNames ?? _repository.GetSystemFieldNames(_collectionId);
        _columnOrder = snap?.ColumnOrder ?? _repository.GetColumnOrder(_collectionId);
        _hiddenColumns = (snap?.ColumnDisplay ?? _repository.GetColumnDisplay(_collectionId))
            .Where(kv => !kv.Value.Visible)
            .Select(kv => kv.Key)
            .ToHashSet();
        _columnStyles = snap?.ColumnStyles ?? _repository.GetColumnStyles(_collectionId);
        _colWidths    = snap?.ColumnWidths ?? _repository.GetColumnWidths(_collectionId);
    }

    // ── Точечные изменения набора колонок ─────────────────────────────────────
    // Все сходятся в ReloadColumns: у рисованной сетки пересборка колонок — это новая раскладка
    // и перерисовка, строки при этом не трогаются, поэтому отдельных точечных путей не нужно.

    /// <summary>Создание/«создать после»/дубль свойства: порядок в БД уже выставлен контроллером
    /// (AddField/MoveColumnAfter), здесь — новая раскладка с колонкой на ее месте.</summary>
    public void AddColumnForField(int fieldId) => ReloadColumns();

    /// <summary>Изменение свойства (имя/тип/опции) или переименование системного заголовка.</summary>
    public void RebuildColumn(string key) => ReloadColumns();

    private List<TableColumnDef> BuildColDefs()
    {
        // Движимые колонки: системное «Название» и пользовательские поля (в т.ч. «Описание» —
        // оно обычное поле). Порядок — по эффективной позиции (из column_order, иначе дефолт).
        // № и «+» закреплены.
        var movable = new List<TableColumnDef>
        {
            new() { Key = TableRowVm.TitleKey, Header = SystemHeader(TableRowVm.TitleKey),
                    FieldType = "text",  IsStarWidth = true, MinWidth = MinColWidth,
                    BuildHeaderMenu = _ => BuildSystemHeaderContextMenu(TableRowVm.TitleKey) },
        };

        foreach (var f in _fields)
        {
            var fid = f.Id!.Value;
            movable.Add(new TableColumnDef
            {
                Key         = fid.ToString(),
                Header      = f.Name,
                FieldType   = f.FieldType,
                IsStarWidth = true,
                MinWidth    = MinColWidth,
                // Чекбоксы, «файл» и «вычисляемые» типы (дата/автор создания-изменения) правятся не
                // текстовым редактором — read-only на уровне колонки: галочка и файл правятся кликом.
                IsReadOnly  = f.FieldType is "checkbox" or "file" || FieldTypeInfo.IsComputed(f.FieldType),
                // «Пользователь» — как select, только опции живые: допущенные пользователи
                // базы. Снимок на момент построения колонок; редактор ячейки перечитывает
                // их сам (состав пользователей меняется, пока таблица открыта).
                Options     = f.FieldType == "user"
                    ? _repository?.GetUserOptions() ?? []
                    : ParseSelectOptions(f),
                // Формат склейки «Мультисписка» (запятая/пробел/«;»/перенос) — из ConfigJson поля.
                Separator   = MultiSelectValue.SeparatorFromConfig(f.ConfigJson),
                // Формат показа «Числа» (валюта/проценты/разряды) — из ConfigJson поля.
                NumberFormat = NumberFormatValue.FromConfig(f.ConfigJson),
                // Папка авто-привязки «Файла» (если правило задано) → ячейка read-only.
                FileAutoFolder = FileAutoRule.FromConfig(f.ConfigJson)?.Folder ?? "",
                // Настройка связи (целевая коллекция + one/many), если задана → ячейка рисует плашки.
                Relation    = f.FieldType == "relation" ? RelationConfig.FromConfig(f.ConfigJson) : null,
                Tag         = fid,
                BuildHeaderMenu = _ => BuildHeaderContextMenu(fid, f.IsRequired, f.Name, f.FieldType,
                    fileAuto: FileAutoRule.FromConfig(f.ConfigJson) is not null),
            });
        }

        // Стабильная сортировка по эффективной позиции (OrderBy сохраняет порядок
        // равных — важно, если у пары колонок совпали ключи дефолта).
        var ordered = movable.OrderBy(EffectiveColumnPosition).ToList();

        // Ширина «№» здесь стандартная: под самый длинный номер (число записей) ее меряет сетка по
        // точному счету из фона — COUNT на UI-потоке при каждой пересборке колонок ей не нужен.
        var defs = new List<TableColumnDef>
        {
            new() { Key = TableColumnDef.NumKey, Header = "№",
                    IsStarWidth = false, FixedWidth = NumColWidth,
                    CanResize = false,   IsReadOnly = true },
        };
        defs.AddRange(ordered);
        defs.Add(new() { Key = TableColumnDef.AddPropKey, Header = "+",
                         IsStarWidth = false, FixedWidth = PlusColWidth,
                         CanResize = false,   IsReadOnly = true,
                         MaxWidth  = PlusColWidth });

        return defs;
    }

    // Эффективная позиция движимой колонки: сохраненная (column_order) или дефолт.
    // Дефолт: Title=1000, Desc=2000, пользовательские = 3000 + DbFields.Position.
    private int EffectiveColumnPosition(TableColumnDef def)
    {
        if (_columnOrder.TryGetValue(def.Key, out var pos)) return pos;
        if (def.Key == TableRowVm.TitleKey) return 1000;
        if (int.TryParse(def.Key, out var fid))
        {
            var f = _fields.FirstOrDefault(x => x.Id == fid);
            if (f is not null) return 3000 + f.Position;
        }
        return int.MaxValue;
    }

    // Текущий порядок ключей движимых колонок (без № и «+») — для MoveColumn и
    // вставки нового свойства справа от колонки (контроллер).
    public List<string> MovableKeysInOrder() =>
        _colDefs
            .Where(d => d.Key != TableColumnDef.NumKey && d.Key != TableColumnDef.AddPropKey)
            .Select(d => d.Key)
            .ToList();

    // Движимые колонки (ключ + отображаемое имя) в порядке слева-направо — для окна
    // «Отображение». Включает и скрытые колонки (их там можно вернуть), № и «+» — нет.
    public IReadOnlyList<(string Key, string Name)> MovableColumnsInOrder() =>
        _colDefs
            .Where(d => d.Key != TableColumnDef.NumKey && d.Key != TableColumnDef.AddPropKey)
            .Select(d => (d.Key, d.Header))
            .ToList();

    // ── Операции над свойствами для панели записи ──────────────────────────────
    //
    // Панель записи показывает те же свойства карточками и дает над ними то же контекстное
    // меню, что заголовок колонки. Порядок, цвет и видимость колонок живут ЗДЕСЬ (column_meta
    // + кеши + пересборка колонок), поэтому панель не делает их сама, а просит через
    // контроллер — иначе состояние таблицы и БД разъедутся. См. docs/developer/features/constructor/record-page.md.

    /// <summary>Порядок свойства: «вверх» в панели = «влево» в таблице (список тот же).</summary>
    public void MoveProperty(int fieldId, bool up) => MoveColumn(fieldId.ToString(), left: up);

    /// <summary>Есть ли куда двигать — пункт меню в панели неприменимого направления не показывает.</summary>
    public bool CanMoveProperty(int fieldId, bool up) => CanMoveColumn(fieldId.ToString(), left: up);

    /// <summary>Текущий цвет свойства — панели он нужен, чтобы отметить его в меню «Изменить цвет».</summary>
    public RowStyle PropertyStyle(int fieldId) => ColumnStyleOf(fieldId.ToString());

    /// <summary>Применить цвет к свойствам (тот же путь, что из меню заголовка колонки).</summary>
    public void ApplyPropertyStyle(IReadOnlyList<int> fieldIds, StyleAspect aspect, string? hex) =>
        ApplyColumnStyle([.. fieldIds.Select(id => id.ToString())], aspect, hex);

    // Те же операции для системной колонки «Название» — у нее нет id поля, только ключ.

    /// <summary>Сдвинуть колонку «Название» (из панели записи: вверх = влево).</summary>
    public void MoveTitle(bool up) => MoveColumn(TableRowVm.TitleKey, left: up);

    /// <summary>Есть ли куда двигать колонку «Название».</summary>
    public bool CanMoveTitle(bool up) => CanMoveColumn(TableRowVm.TitleKey, left: up);

    /// <summary>Текущий цвет колонки «Название».</summary>
    public RowStyle TitleStyle() => ColumnStyleOf(TableRowVm.TitleKey);

    /// <summary>Применить цвет к колонке «Название».</summary>
    public void ApplyTitleStyle(StyleAspect aspect, string? hex) =>
        ApplyColumnStyle([TableRowVm.TitleKey], aspect, hex);

    /// <summary>«Обновить привязку» файла из панели — пересканировать папку и перечитать строки.</summary>
    public void RefreshAutoFileProperty(int fieldId) => RefreshAutoFileFromMenu(fieldId);

    // Перемещение колонки влево/вправо: порядок пишется в БД, дальше — новая раскладка: строки и
    // прокрутка на месте, ширины — по ключам, выделение ячеек и колонок едет за своими ключами.
    private void MoveColumn(string key, bool left)
    {
        if (_repository is null) return;
        _repository.MoveColumn(_collectionId, key, left, MovableKeysInOrder());
        ReloadColumns();
    }

    // Можно ли двигать колонку влево/вправо (не крайняя ли она среди движимых).
    // Позицию ищем прямо в _colDefs, без MovableKeysInOrder(): тот строил и возвращал НОВЫЙ
    // список ключей на каждый вызов, а зовут метод по два раза на каждый пункт сдвига.
    private bool CanMoveColumn(string key, bool left)
    {
        static bool Movable(TableColumnDef d) =>
            d.Key != TableColumnDef.NumKey && d.Key != TableColumnDef.AddPropKey;

        var idx = _colDefs.FindIndex(d => d.Key == key);
        if (idx < 0 || !Movable(_colDefs[idx])) return false;

        for (var i = left ? idx - 1 : idx + 1; i >= 0 && i < _colDefs.Count; i += left ? -1 : 1)
            if (Movable(_colDefs[i])) return true;
        return false;
    }

    // Перемещение записи вверх/вниз: порядок пишется в БД, загруженные строки перечитываются НА
    // МЕСТЕ — меняется только ПОРЯДОК записей, не их число, поэтому список не пересоздается: нет
    // мелькания «Загрузка…», прокрутка остается сама.
    private void MoveRecord(int recordId, bool up)
    {
        if (_repository is null) return;

        _repository.MoveRecord(_collectionId, recordId, up);
        // Выделение записей — по id, за сдвинутой записью следует само; строки перечитываются на месте
        // (в строке редактора оказалась бы другая запись — правку записываем до этого).
        ForgetCellEditing();
        RecordsGrid.Rows?.RefreshLoadedPages();
        // Сдвиг с клавиатуры от активной ячейки: рамка едет за записью (следующее нажатие двигает
        // ее дальше); из меню «№» — выделена сама запись.
        if (_cellRange is not null)
        {
            SyncSelection();
            if (_cellRange is { } followed) ScrollCellIntoView(followed.AnchorRow, followed.AnchorColumn);
        }
        else RecordsGrid.SetRecordSelection([recordId]);
    }

    // Точечное обновление ОДНОЙ строки (после правки записи в правой панели): перечитываем ее данные
    // и заполняем строку на месте, без пересборки таблицы.
    public void RefreshRecordRow(int recordId)
    {
        // Снимаем правку ячейки — только если правится ЭТА ЖЕ запись. Редакторы даты/выбора задают
        // значение императивно (Storage/SelectedItem ставятся один раз) — открытый редактор показывал
        // бы устаревшее значение, а при коммите еще и затер бы свежее из панели. Правка ДРУГОЙ
        // записи к обновлению отношения не имеет: раньше ее закрывало любое перечитывание панели,
        // и набор текста в таблице обрывался на паузе.
        if (IsCellEditing && _editRecordId == recordId) CancelEdit(focusGrid: false);
        RecordsGrid.Rows?.RefreshRecord(recordId);
        // Строки, чьи связи ссылаются на измененную запись, тоже обновляем — иначе название цели в их
        // ячейке-связи оставалось бы старым до прокрутки/перезагрузки.
        RecordsGrid.Rows?.RefreshRelationTargets(recordId);
    }

    // Выделить голубым ровно заданные записи (по id), сняв прежнее выделение. Используется после
    // структурных операций (создание/дубль). Подсветка появится у записи, как только ее строка
    // окажется в кадре.
    public void SelectRecords(IReadOnlyList<int> recordIds)
    {
        ClearCellSelection();   // выделяются записи — рамка ячеек гаснет, иначе Delete двусмыслен
        RecordsGrid.SetRecordSelection(recordIds);
    }

    // Снять все выделение записей (после удаления — удаленные строки не должны оставаться «подсвеченными»).
    public void ClearRecordSelection() => RecordsGrid.ClearRecordSelection();

    // Можно ли двигать запись вверх/вниз (не крайняя ли она в порядке коллекции).
    // O(1)-сик соседа: вызывается при открытии контекстного меню — никаких COUNT'ов.
    private bool CanMoveRecord(int recordId, bool up) =>
        _repository is not null && _repository.HasNeighborRecord(_collectionId, recordId, up);

    // ── Иконки ────────────────────────────────────────────────────────────────

    // Иконки меню фильтра — из общего словаря Assets/Styles/Icons.axaml.
    private static Geometry Icon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    // Размер иконки (сторона Viewbox) — из токенов Icons.axaml.
    private static double IconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 16;

    private static Geometry SortAzIcon  => Icon("IconSortAz");
    private static Geometry SortZaIcon  => Icon("IconSortZa");
    private static Geometry CheckIcon   => Icon("IconCheck");
    private static Geometry XIcon       => Icon("IconClose");

    // ── Оформление ячейки у редактора ─────────────────────────────────────────

    /// <summary>Оформление правящейся ячейки — каскад ячейка → строка → колонка (RowStyle.Resolve).
    /// Сетка рисует правящуюся ячейку без значения и цвета: заливку под редактор кладет держатель
    /// правки (EditorHost.EditorFill), цвет текста и расположение редактор ставит себе сам — иначе
    /// оформленная ячейка на клике теряла бы цвет текста, а текст уезжал бы с места.</summary>
    private RowStyle ResolvedStyle(TableRowVm row, string key) =>
        row.IsAddRow ? RowStyle.None
                     : RowStyle.Resolve(row.CellStyle(key), RowStyleOf(row.RecordId), ColumnStyleOf(key));

    /// <summary>Поставить редактору цвет текста ячейки (ничего не делает у неокрашенной).</summary>
    private void ApplyEditorCellStyle(TemplatedControl editor, TableRowVm row, string key)
    {
        if (ColorTools.Text(ResolvedStyle(row, key).Fg) is { } text) editor.Foreground = text;
    }

    /// <summary>Где стоит текст правящейся ячейки по горизонтали: каскад, иначе правило типа.</summary>
    private CellHAlign EditorHAlign(TableRowVm row, TableColumnDef def) =>
        ResolvedStyle(row, def.Key).HAlign ?? CellAlign.DefaultH(def.FieldType);

    private static TextAlignment ToTextAlignment(CellHAlign align) => align switch
    {
        CellHAlign.Center => TextAlignment.Center,
        CellHAlign.Right  => TextAlignment.Right,
        _                 => TextAlignment.Left,
    };

    private static HorizontalAlignment ToHorizontal(CellHAlign align) => align switch
    {
        CellHAlign.Center => HorizontalAlignment.Center,
        CellHAlign.Right  => HorizontalAlignment.Right,
        _                 => HorizontalAlignment.Left,
    };

    // Оформление строк и колонок живет в сетке: она читает его на кадре и перечитывает при перезагрузке.
    private RowStyle RowStyleOf(int recordId) => RecordsGrid.RowStyleOf(recordId);
    private RowStyle ColumnStyleOf(string key) => RecordsGrid.ColumnStyleOf(key);

    // ── Редакторы ячеек ───────────────────────────────────────────────────────

    // Привязать поле-связь к строке: RecordId берется у строки, обновляется по ее уведомлению "Item"
    // (заполнение заглушки / правка) и при смене DataContext. Отписка при отсоединении.
    private static void BindRelationRow(RelationSelectBox rel, TableRowVm initialRow)
    {
        TableRowVm? bound = null;
        void OnRowProp(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if ((e.PropertyName is "Item" or null) && bound is not null) rel.OnRowFilled(bound.RecordId);
        }
        void Bind(TableRowVm? r)
        {
            if (ReferenceEquals(bound, r)) { if (r is not null) rel.RecordId = r.RecordId; return; }
            if (bound is not null) bound.PropertyChanged -= OnRowProp;
            bound = r;
            if (bound is not null) bound.PropertyChanged += OnRowProp;
            rel.RecordId = bound?.RecordId ?? 0;
        }
        rel.DataContextChanged     += (_, _) => Bind(rel.DataContext as TableRowVm);
        rel.DetachedFromVisualTree += (_, _) => { if (bound is not null) { bound.PropertyChanged -= OnRowProp; bound = null; } };
        Bind(initialRow);
    }

    /// <summary>
    /// Редактор ячейки по типу поля — кладется в накладной редактор сетки (<see cref="EditorHost"/>).
    /// Текстовые поля и список пишут значение при коммите правки (<see cref="FlushEditingBox"/>,
    /// <c>FlushDeferredList</c>), мультисписок, связь и выбор даты в календаре — на каждое изменение.
    /// <paramref name="relLinks"/> — кеш связей таблицы: из него редактор связи берет стартовый набор.
    /// </summary>
    private Control BuildCellEditor(TableColumnDef def, TableRowVm row, RelationLinksCache relLinks)
    {
        if (row.IsAddRow) return new Border();

        // Расположение текста ячейки (каскад, иначе правило типа): редактор ставит значение туда же,
        // где его нарисовала сетка. «По центру» у редакторов с кнопкой справа — по центру всей
        // ячейки, а не полосы левее кнопки; «справа» — у самой кнопки (на нее текст не ложится).
        // Вертикаль — забота держателя правки: полоса у края (EditorHost, CellAlign.EditorInset).
        var hAlign = EditorHAlign(row, def);

        switch (def.FieldType)
        {
            case "date":
            {
                // Flat — как у «Файла»: в ячейке рамку рисует сама таблица, собственный бокс
                // редактора дал бы «бокс в боксе» и увел бы дату вправо от надписи показа.
                var editor = new DateEditor
                {
                    Flat               = true,
                    ContentLeftPadding = CellContentPad,
                    VerticalAlignment  = VerticalAlignment.Center,
                    Storage            = row[def.Key],
                };
                editor.SetValueAlignment(ToTextAlignment(hAlign));
                editor.Changed += (_, _) => row[def.Key] = editor.Storage ?? "";
                return editor;
            }

            case "select":
            case "status":  // как select: значение — строка из опций (ConfigJson)
            case "user":   // как select: значение — отображаемое имя пользователя
            {
                // «Пользователь»: опции берем в момент входа в правку, а не из
                // построенных колонок, — состав пользователей базы меняют соседи
                // (присоединился/допущен/отозван), список обязан быть свежим.
                // Плюс текущее значение остается в опциях, даже если такого
                // пользователя в базе больше нет (исторический снапшот).
                var options = def.FieldType == "user" && _repository is not null
                    ? _repository.GetUserOptions(row[def.Key])
                    : def.Options;
                // Растягиваем на всю ячейку и центрируем содержимое: копия темы ставит
                // ComboBox'у VerticalAlignment=Top, и при MinHeight=30 в строке 36px список
                // висел бы у верхнего края — текст вставал на 3px выше надписи показа.
                // По центру: слева отступ на всю колонку стрелки шаблона (32) и правый отступ — полоса
                // значения симметрична, и выбор стоит по центру ячейки, как в показе.
                var combo = new ComboBox
                {
                    BorderThickness            = new Thickness(0),
                    Padding                    = hAlign == CellHAlign.Center ? CellDropdownCenteredPadding : CellDropdownPadding,
                    HorizontalAlignment        = HorizontalAlignment.Stretch,
                    VerticalAlignment          = VerticalAlignment.Stretch,
                    // Слева — прежнее растянутое содержимое темы: без расположения ничего не меняется.
                    HorizontalContentAlignment = hAlign == CellHAlign.Left ? HorizontalAlignment.Stretch : ToHorizontal(hAlign),
                    VerticalContentAlignment   = VerticalAlignment.Center,
                    MinHeight                  = 0,
                    ItemsSource                = options,
                    SelectedItem               = row[def.Key],
                };
                ApplyEditorCellStyle(combo, row, def.Key);
                // Выбор пишется при записи правки (FlushDeferredList) или при уходе фокуса из списка
                // (FlushListWhenFocusLeft), а не на каждое изменение: Escape уходит без записи, как
                // с текстом. Уход фокуса смотрится после самого ухода — фокус в пункты раскрытого
                // списка уходом не считается.
                combo.SelectionChanged += (_, _) =>
                {
                    if (ReferenceEquals(RecordsGrid.Editor.Editor, combo)) _listDirty = true;
                };
                combo.LostFocus += (_, _) => Dispatcher.UIThread.Post(() => FlushListWhenFocusLeft(combo));
                return combo;
            }

            case "multiselect":   // несколько опций: плашки + выпадающий чеклист, значение — JSON-массив
            {
                var box = new MultiSelectBox(def.Options, row[def.Key], flat: true, separator: def.Separator)
                {
                    ContentPadding = new Thickness(CellContentPad, 0),
                };
                box.SetValueAlignment(ToHorizontal(hAlign));
                box.Changed += (_, _) => row[def.Key] = box.Storage;
                return box;
            }

            case "relation" when def.Relation is not null && _repository is not null && def.Tag is int relFid:
            {
                // Правка ячейки: текст + иконка-связь справа, по клику по иконке — чеклист выбора
                // (как чеклист «Мультисписка»). «→» у пункта → открыть цель.
                var rel = new RelationSelectBox(_repository, relFid, def.Relation, editing: true, contentLeftPadding: CellContentPad, cache: relLinks);
                rel.SetValueAlignment(ToHorizontal(hAlign));
                rel.Navigate += (_, t) =>
                {
                    if (t.DbId > 0) CrossDbRecordRequested?.Invoke(this, t);   // межбазовый переход
                    else RecordClicked?.Invoke(this, t.RecordId);              // в текущей БД
                };
                rel.Changed  += (_, _) =>
                {
                    if (rel.DataContext is TableRowVm r) RecordValueEdited?.Invoke(this, r.RecordId);
                };
                BindRelationRow(rel, row);
                return rel;
            }

            // Текстовые редакторы — БЕЗ привязки к строке: значение уходит в базу при коммите
            // правки, а не на каждую букву (см. FlushEditingBox).
            case "number":
            {
                // Число правится В ТОМ ЖЕ ВИДЕ, В КАКОМ ПОКАЗАНО. Подставь редактор сырое
                // значение — «1 234,50 ₽» превратилось бы под курсором в «1234.5»: строка
                // другой длины, и текст прыгал бы вбок на входе в правку. Обратно в канон
                // хранения строку переводит CommitText (TryParseDisplay).
                var tb = new TextBox
                {
                    BorderThickness          = new Thickness(0),
                    Padding                  = CellContentMargin,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    TextAlignment            = ToTextAlignment(hAlign),   // по умолчанию — вправо
                    Text                     = NumberFormatValue.Format(row[def.Key], def.NumberFormat),
                };
                ApplyEditorCellStyle(tb, row, def.Key);
                // Посимвольный фильтр (цифры, один разделитель, ведущий минус) — только у
                // «обычного числа», где показ совпадает с хранением. В остальных форматах в
                // строке законно стоят разряды, знак валюты и «%»; там ввод проверяется целиком
                // при коммите, а неразобранное сохраняется как набрано.
                if (def.NumberFormat.Kind == NumberFormatKind.Plain)
                    tb.AddHandler(InputElement.TextInputEvent, OnNumberTextInput, RoutingStrategies.Tunnel);
                tb.LostFocus += OnEditingBoxLostFocus;
                return tb;
            }

            default:
            {
                // «Без названия» в редакторе — серая подсказка, а не текст: печатать можно сразу,
                // стертое сохранится обратно как «Без названия» (TableRowVm).
                var isTitle = def.Key == TableRowVm.TitleKey;
                var tb = new TextBox
                {
                    BorderThickness          = new Thickness(0),
                    Padding                  = CellContentMargin,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    TextAlignment            = ToTextAlignment(hAlign),
                    Watermark                = isTitle ? TitleText.Placeholder
                                             : def.FieldType == "url" ? "https://" : null,
                    Text                     = isTitle ? TitleText.Editable(row[def.Key]) : row[def.Key],
                };
                ApplyEditorCellStyle(tb, row, def.Key);
                tb.LostFocus += OnEditingBoxLostFocus;
                return tb;
            }
        }
    }

    // Фильтр ввода числовой ячейки: символ принимается только если итоговый текст
    // остается допустимым (частичным) числом — цифры, один разделитель, ведущий минус.
    private static void OnNumberTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb || string.IsNullOrEmpty(e.Text)) return;

        var text  = tb.Text ?? "";
        var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        var end   = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        var prospective = text[..start] + e.Text + text[end..];

        if (!CellTyping.IsPartialNumber(prospective)) e.Handled = true;
    }

    // ── Выделение колонок голубой заливкой (одиночное и Shift-диапазон) ────────

    private void SelectColumn(string key, bool shift)
    {
        // Служебные колонки № и + не выделяем. «Название»/«Описание» выделять можно
        // (заливка появляется), но при удалении они отфильтровываются.
        if (key == TableColumnDef.NumKey || key == TableColumnDef.AddPropKey) return;

        // Выделение колонок гасит выделение ячеек, как и выделение записей: иначе Delete при
        // выделенной колонке стирал бы значения выделенных ранее ячеек вместо удаления свойства.
        ClearCellSelection();

        if (shift)
        {
            // Точечно добавить/убрать одну колонку (toggle), не трогая остальные.
            if (!_selectedColumnKeys.Remove(key))
                _selectedColumnKeys.Add(key);
        }
        else
        {
            // Обычный клик — выделить только эту колонку.
            _selectedColumnKeys.Clear();
            _selectedColumnKeys.Add(key);
        }

        // Порядок выделения держим по порядку колонок — как в списке ключей на удаление.
        _selectedColumnKeys.Sort((a, b) =>
            _colDefs.FindIndex(d => d.Key == a).CompareTo(_colDefs.FindIndex(d => d.Key == b)));

        ApplyColumnSelection();
    }

    private void ClearColumnSelection()
    {
        if (_selectedColumnKeys.Count == 0) return;
        _selectedColumnKeys.Clear();
        ApplyColumnSelection();
    }

    // Заливка выделенных колонок — при рисовании (та же BrushAccentLight, что у выделенной записи);
    // шапка не красится, выделение видно только по ячейкам свойства.
    private void ApplyColumnSelection() => RecordsGrid.SetColumnSelection(_selectedColumnKeys);

    // ── Контекстные меню ──────────────────────────────────────────────────────

    private ContextMenu BuildGridContextMenu()
    {
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = MenuIcons.CreateRecordHeader() }
            .WithGesture("table.createRecord", this);
        // В конце; открывать ли панель — по настройке.
        Guarded(mi, PermAction.CreateRecord,
                () => RecordCreateRequested?.Invoke(this, (ConstructorSettings.OpenNewRecord, null)));
        menu.Items.Add(mi);

        // «Отображение» доступно и отсюда — на случай, если все колонки скрыты и до
        // меню заголовка не добраться (окно показывает полный список колонок).
        var miDisplay = new MenuItem { Header = MenuIcons.DisplayHeader() }
            .WithGesture("table.columnDisplay", this);
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        return menu;
    }

    // Пересчет пунктов меню на каждое открытие (состав зависит от выделения, прав, буфера, настроек
    // обмена). Меню открываются у указателя (ContextMenu.Open), а Open событие Opening не поднимает
    // (его поднимает только запрос контекстного меню), поэтому обработчики хранятся отдельно и
    // зовутся путем открытия (OpenMenu); Opening оставлен на случай штатного запроса.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContextMenu, List<Action>> _menuOpening = new();

    private void OnMenuOpening(ContextMenu menu, Action refresh)
    {
        menu.Opening += (_, _) => refresh();
        _menuOpening.GetOrCreateValue(menu).Add(refresh);
    }

    private void RunMenuOpening(ContextMenu menu)
    {
        if (_menuOpening.TryGetValue(menu, out var refreshers))
            foreach (var refresh in refreshers) refresh();
    }

    // Меню строится с ЛЕНИВЫМ резолвом строки и ее RecordId (см. RecordIdOf), а не с захватом id в
    // момент BuildRowContextMenu: строка сперва заполняется как заглушка «Загрузка…» с RecordId=0, и
    // только потом Fill проставляет реальный id в ТОТ ЖЕ объект. Чтение на момент клика всегда дает
    // актуальный id.
    private ContextMenu BuildRowContextMenu(Func<TableRowVm?> row)
    {
        var menu = new ContextMenu();

        var miCreate = new MenuItem { Header = MenuIcons.CreateRecordHeader() }
            .WithGesture("table.createRecord", this);
        // После этой строки; открывать ли панель — по настройке. Если строка еще не заполнена
        // (id=0) — создаем в конце.
        Guarded(miCreate, PermAction.CreateRecord, () =>
        {
            var rid = RecordIdOf(row);
            RecordCreateRequested?.Invoke(this, (ConstructorSettings.OpenNewRecord, rid > 0 ? rid : (int?)null));
        });
        menu.Items.Add(miCreate);

        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = MenuIcons.EditHeader() }
            .WithGesture("table.openRecord", this);
        miEdit.Click += (_, _) => { var rid = RecordIdOf(row); if (rid > 0) RecordClicked?.Invoke(this, rid); };
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = MenuIcons.DuplicateHeader() }
            .WithGesture("table.duplicateRecord", this);
        Guarded(miDuplicate, PermAction.DuplicateRecord, () => RequestRecordDuplicateFromMenu(row));
        menu.Items.Add(miDuplicate);

        // Перенос = изъятие записи из коллекции: право как у удаления.
        var miRelocate = new MenuItem { Header = MenuIcons.LocationHeader() }
            .WithGesture("table.relocateRecords", this);
        Guarded(miRelocate, PermAction.DeleteRecord, () => RequestRecordRelocateFromMenu(row));
        menu.Items.Add(miRelocate);

        // «Отображение» (записей) — глобальное окно видимости/уровней строк (список всех
        // записей), инициатор-строка не важна.
        var miDisplay = new MenuItem { Header = MenuIcons.DisplayHeader() }
            .WithGesture("table.recordDisplay", this);
        miDisplay.Click += (_, _) => RecordDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColorItems(menu, row);
        AddRowTextFormatItems(menu, row);

        // Работа со значениями строки: копирование, очистка и вставка идут своей группой
        // выше «Удалить» — очистку значений не должно тянуть перепутать с удалением записи.
        AddCellValueItems(menu, () => SelectWholeRow(row));

        AddRecordMoveItems(menu, row);
        AddHeightModeItems(menu);
        AddActionItems(menu);

        menu.Items.Add(new Separator());

        var miDelete = MakeDeleteMenuItem().WithGesture("table.deleteRecords", this);
        Guarded(miDelete, PermAction.DeleteRecord, () => RequestRecordDeleteFromMenu(row));
        menu.Items.Add(miDelete);

        return menu;
    }

    // Текущий RecordId строки (0 — заглушка «Загрузка…» или add-row). Резолвится на момент
    // вызова, поэтому видит id, проставленный async-заполнением строки.
    private static int RecordIdOf(Func<TableRowVm?> row) => row()?.RecordId ?? 0;

    /// <summary>
    /// Действия обмена, настроенные для открытой коллекции (docs/ROADMAP-API.md, шаг 6). Пусто —
    /// пункта в меню нет вовсе.
    ///
    /// Таблица об интеграциях ничего не знает и знать не должна: она показывает то, что ей дали,
    /// и зовет то, что ей дали. Собирает список раздел (<see cref="IntegrationActions"/>).
    /// </summary>
    public Func<IReadOnlyList<DataAction>>? DataActions { get; set; }

    /// <summary>Ключ открытой базы — для следа копирования в журнале безопасности. Таблица базу
    /// по ключу не знает (у нее есть только репозиторий), спрашивает у раздела.</summary>
    public Func<int>? DatabaseId { get; set; }

    // ── Пункт «Действия» в меню строки ────────────────────────────────────────
    //
    // Состав пересчитывается при открытии: обмен могли настроить, пока таблица открыта, а
    // собранное один раз меню показывало бы вчерашний список. Пункт применяется ко ВСЕМУ
    // мультивыделению строк — как удаление и цвет.
    private void AddActionItems(ContextMenu menu)
    {
        if (DataActions is null) return;

        var host = new MenuItem
        {
            Header    = MenuIcons.ActionsHeader(),
            IsVisible = false,
        };

        MenuIcons.FixSubmenuGap(host);
        menu.Items.Add(host);

        OnMenuOpening(menu, () =>
        {
            var actions = DataActions?.Invoke() ?? [];

            host.IsVisible = actions.Count > 0;
            if (actions.Count == 0) return;

            var items = new List<MenuItem>();

            foreach (var action in actions)
            {
                var item = new MenuItem
                {
                    Header = MenuIcons.IntegrationHeader(action.Title),
                };

                var run = action.Run;
                item.Click += (_, _) => _ = run(SelectedRecordIds());
                items.Add(item);
            }

            host.ItemsSource = items;
        });
    }

    // ── Пункты цвета в меню строки ────────────────────────────────────────────
    //
    // Подменю с общей палитрой (ColorMenu): «Фон строки» и «Цвет текста строки». Цвет ячейки
    // живет в меню самой ячейки (AddCellColorItems): меню строки открывается только со столбца
    // «№», и ячейки-адресата у него нет. Все это оформление ЭТОЙ таблицы: за ее пределами цвет
    // не виден (сквозной цвет записи был и убран — см. RowStyle.Resolve). Пункты применяются ко
    // ВСЕМУ мультивыделению строк — как удаление и дубль. Состав пересчитывается при открытии:
    // цвета строки к моменту клика могли измениться, а «текущий» цвет в палитре должен быть
    // отмечен верно.
    private void AddColorItems(ContextMenu menu, Func<TableRowVm?> row)
    {
        var host = new MenuItem { Header = MenuIcons.ColorHeader() };
        MenuIcons.FixSubmenuGap(host);
        host.IsEnabled = Can(PermAction.EditRecord);

        OnMenuOpening(menu, () =>
        {
            host.Items.Clear();
            var ids = TargetRecordIds(row);
            if (ids.Count == 0) { host.IsEnabled = false; return; }
            host.IsEnabled = Can(PermAction.EditRecord);

            // Отмечаем цвет ТОЙ строки, по которой кликнули (в выделении она не обязательно
            // первая), а применяем ко всему набору — как у колонок.
            var rowStyle = RowStyleOf(RecordIdOf(row));
            host.Items.Add(ColorMenu.Build("Фон строки", rowStyle.Bg,
                hex => ApplyRowStyle(ids, StyleAspect.Background, hex)));
            host.Items.Add(ColorMenu.Build("Цвет текста строки", rowStyle.Fg,
                hex => ApplyRowStyle(ids, StyleAspect.Text, hex)));
        });

        menu.Items.Add(new Separator());
        menu.Items.Add(host);
    }

    // Записи, к которым применяется команда меню строки: все мультивыделение, если строка
    // в него входит (правый клик по выделенной строке выделение сохраняет), иначе только она.
    private List<int> TargetRecordIds(Func<TableRowVm?> row)
    {
        var recordId = RecordIdOf(row);
        if (recordId <= 0) return [];
        var selected = SelectedRecordIds();
        return selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
    }

    private void ApplyRowStyle(List<int> recordIds, StyleAspect aspect, string? value) =>
        ApplyRowStyle(recordIds, [(aspect, value)]);

    private void ApplyRowStyle(List<int> recordIds, IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        if (_repository is null) return;
        StyleStamps at;
        try { at = _repository.SetRowStyle(_collectionId, recordIds, changes); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }

        // Сетка держит оформление сама и применяет его при рисовании. Ячейки строк потеряли этот
        // аспект в базе (выставленное последним видно во всей строке) — так же и в загруженных.
        RecordsGrid.ApplyRowStyle(recordIds, changes, at);
        var rows = recordIds.ToHashSet();
        RowsSource?.ForEachCachedRow(row =>
        {
            if (rows.Contains(row.RecordId)) row.DropCellAspects(_ => true, changes);
        });
    }

    private void ApplyCellStyleChange(List<(int RecordId, string Key)> cells,
                                      IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        if (_repository is null) return;
        try { _repository.SetCellStyle(_collectionId, cells, changes); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }

        foreach (var (recordId, key) in cells)
        {
            var row = RowsSource?.FindCachedByRecordId(recordId);   // оформление ячеек живет в строке
            if (row is null) continue;
            var next = new Dictionary<string, RowStyle>();
            if (row.CellStyles is { } existing)
                foreach (var (k, v) in existing) next[k] = v;

            var updated = next.TryGetValue(key, out var c) ? c : RowStyle.None;
            foreach (var (aspect, value) in changes) updated = updated.With(aspect, value);
            if (updated.IsEmpty) next.Remove(key); else next[key] = updated;

            row.SetStyle(next);
        }
    }

    private void ApplyColumnStyle(List<string> keys, StyleAspect aspect, string? value) =>
        ApplyColumnStyle(keys, [(aspect, value)]);

    private void ApplyColumnStyle(List<string> keys, IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        if (_repository is null || keys.Count == 0) return;
        StyleStamps at;
        try { at = _repository.SetColumnStyle(_collectionId, keys, changes); }
        catch (Exception ex) when (DbErrors.Handle(ex, _repository.DbPath)) { return; }

        RecordsGrid.ApplyColumnStyle(keys, changes, at);   // применяется при рисовании
        // Ячейки колонок потеряли этот аспект в базе во всех записях — так же и в загруженных.
        var columns = keys.ToHashSet();
        RowsSource?.ForEachCachedRow(row => row.DropCellAspects(columns.Contains, changes));

        // После цвета снимаем выделение колонок: его голубая заливка лежит ПОД полупрозрачным
        // цветом, и на выделенной колонке разницу «покрасил / снял цвет» почти не видно. Выделение
        // здесь — жест «эти колонки», он свое отработал (так же гаснет после дубля и удаления
        // свойств). Расположение текста видно и под выделением, а клавишами его пробуют подряд
        // («по центру», потом «справа») — выделение остается. Выделение СТРОК не трогаем: это
        // основное состояние таблицы.
        if (changes.Any(c => c.Aspect is StyleAspect.Background or StyleAspect.Text)) ClearColumnSelection();
    }

    // Удаление из меню записи. Если запись входит в текущее мультивыделение (правый
    // клик по выделенной строке его сохраняет) — удаляем весь набор; иначе только ее.
    // В любом случае идем через пакетный путь с подтверждением (RecordsDeleteRequested) —
    // как и удаление свойств.
    private void RequestRecordDeleteFromMenu(Func<TableRowVm?> row)
    {
        var recordId = RecordIdOf(row);
        if (recordId <= 0) return;   // строка-заглушка еще не заполнена — удалять нечего

        var selected = SelectedRecordIds();
        List<int> ids = selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
        RecordsDeleteRequested?.Invoke(this, ids);
    }

    // Дубль из меню записи (по аналогии с удалением): запись в мультивыделении → дублируем весь
    // набор; иначе только ее. Контроллер сам решает: одна — копия после исходной мгновенно,
    // несколько — bulk в фоне с индикатором.
    private void RequestRecordDuplicateFromMenu(Func<TableRowVm?> row)
    {
        var recordId = RecordIdOf(row);
        if (recordId <= 0) return;   // строка-заглушка еще не заполнена — дублировать нечего

        var selected = SelectedRecordIds();
        List<int> ids = selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
        RecordDuplicateRequested?.Invoke(this, ids);
    }

    // «Расположение» из меню записи (как дубль/удаление): запись в мультивыделении →
    // переносим весь набор; иначе только ее. Контроллер открывает окно выбора БД/коллекции.
    private void RequestRecordRelocateFromMenu(Func<TableRowVm?> row)
    {
        var recordId = RecordIdOf(row);
        if (recordId <= 0) return;   // строка-заглушка еще не заполнена — переносить нечего

        var selected = SelectedRecordIds();
        List<int> ids = selected.Contains(recordId) && selected.Count > 1 ? selected : [recordId];
        RecordRelocateRequested?.Invoke(this, ids);
    }

    // Пункты «Сдвинуть вверх/вниз» (именно в таком порядке) с разделителем перед ними.
    // Доступны только без активных фильтра и сортировки: при сортировке порядок задает
    // сортировка, а не позиция, поэтому ручной сдвиг был бы невидим. Неприменимое
    // направление (крайняя запись) скрываем; видимость уточняем при открытии меню — тогда
    // же резолвится RecordId строки (async-заполнение к моменту открытия уже прошло).
    private void AddRecordMoveItems(ContextMenu menu, Func<TableRowVm?> row)
    {
        if (_sortKey is not null || _filters.Count > 0) return;

        var sep    = new Separator();
        var miUp   = new MenuItem { Header = MenuIcons.MoveUpHeader() }
            .WithGesture("table.moveRecordUp", this);
        miUp.Click += (_, _) => { var rid = RecordIdOf(row); if (rid > 0) MoveRecord(rid, up: true); };
        var miDown = new MenuItem { Header = MenuIcons.MoveDownHeader() }
            .WithGesture("table.moveRecordDown", this);
        miDown.Click += (_, _) => { var rid = RecordIdOf(row); if (rid > 0) MoveRecord(rid, up: false); };

        menu.Items.Add(sep);
        menu.Items.Add(miUp);
        menu.Items.Add(miDown);

        void Refresh()
        {
            var rid     = RecordIdOf(row);
            var canUp   = rid > 0 && CanMoveRecord(rid, up: true);
            var canDown = rid > 0 && CanMoveRecord(rid, up: false);
            miUp.IsVisible   = canUp;
            miDown.IsVisible = canDown;
            sep.IsVisible    = canUp || canDown;
        }

        // ТОЛЬКО при открытии меню (оно строится на правом нажатии): два O(1)-сика соседей на клик.
        OnMenuOpening(menu, Refresh);
    }

    // Красный пункт «Удалить» с иконкой корзины — общий для меню записей и свойств.
    private static MenuItem MakeDeleteMenuItem(bool enabled = true) =>
        new() { Header = MenuIcons.DeleteHeader(), IsEnabled = enabled };

    // Некликабельная шапка контекстного меню (карточка свойства). IsHitTestVisible=
    // false — не подсвечивается при наведении и не реагирует на клик.
    private static MenuItem MakeMenuHeaderItem(Control content) => new()
    {
        Header           = content,
        IsHitTestVisible = false,
        Focusable        = false,
    };

    // ── Системная колонка «Название» ──────────────────────────────────────────

    // Публично для контроллера: текущая подпись и имя по умолчанию по ключу колонки.
    public string GetSystemHeader(string key)        => SystemHeader(key);
    public string GetSystemDefaultName(string key)   => SystemDefaultName(key);

    // Имя по умолчанию для системной колонки (она одна — «Название»).
    private static string SystemDefaultName(string key) => "Название";

    // Текущая подпись: пользовательская из метаданных, иначе имя по умолчанию.
    private string SystemHeader(string key) =>
        _systemNames.TryGetValue(key, out var name) ? name : SystemDefaultName(key);

    // Меню заголовка системной колонки «Название»:
    //   Изменить · Создать новое свойство · — · Сдвинуть вправо · Сдвинуть влево
    // Удаление/смена типа/дублирование для нее недоступны.
    private ContextMenu BuildSystemHeaderContextMenu(string key)
    {
        var menu = new ContextMenu();

        // Шапка: иконка типа + подпись колонки + тип «Текст» (Title — текстовый).
        menu.Items.Add(MakeMenuHeaderItem(MenuIcons.PropertyMenuHeader(SystemHeader(key), "text")));
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = MenuIcons.EditHeader() }
            .WithGesture("table.editProperty", this);
        Guarded(miEdit, PermAction.EditField, () => SystemFieldRenameRequested?.Invoke(this, key));
        menu.Items.Add(miEdit);

        var miCreate = new MenuItem { Header = MenuIcons.CreatePropertyHeader() }
            .WithGesture("table.createProperty", this);
        Guarded(miCreate, PermAction.CreateField, () => PropertyCreateAfterRequested?.Invoke(this, key));
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = MenuIcons.DisplayHeader() }
            .WithGesture("table.columnDisplay", this);
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColumnColorItems(menu, key);
        AddColumnTextFormatItems(menu, key, "text");

        // Значения столбца названий: копировать и вставлять их можно, очистка их не трогает
        // (см. ClearCellsAsync) — пункт остается на месте ради единообразия всех трех меню.
        AddCellValueItems(menu, () => SelectWholeColumn(key));

        AddMoveItems(menu, key);
        AddWidthModeItems(menu);
        return menu;
    }

    // Пункты «Сдвинуть вправо/влево» (именно в таком порядке) с разделителем перед
    // ними. Общие для меню системных и пользовательских колонок. Неприменимое
    // направление (крайняя колонка) скрываем.
    private void AddMoveItems(ContextMenu menu, string key)
    {
        var canLeft  = CanMoveColumn(key, left: true);
        var canRight = CanMoveColumn(key, left: false);
        if (!canLeft && !canRight) return;

        menu.Items.Add(new Separator());

        if (canRight)
        {
            var mi = new MenuItem { Header = MenuIcons.MoveRightHeader() }
                .WithGesture("table.moveColumnRight", this);
            mi.Click += (_, _) => MoveColumn(key, left: false);
            menu.Items.Add(mi);
        }
        if (canLeft)
        {
            var mi = new MenuItem { Header = MenuIcons.MoveLeftHeader() }
                .WithGesture("table.moveColumnLeft", this);
            mi.Click += (_, _) => MoveColumn(key, left: true);
            menu.Items.Add(mi);
        }
    }

    // Пункт-переключатель режима ширины столбцов (общий для всей таблицы). Текст
    // показывает режим, в который переключит клик: при стандартной ширине — «Ручная
    // ширина столбцов», при ручной — «Стандартная ширина столбцов». Заголовок
    // пересчитывается при открытии меню.
    private void AddWidthModeItems(ContextMenu menu)
    {
        menu.Items.Add(new Separator());

        var item = new MenuItem().WithGesture("table.columnWidthMode", this);
        item.Click += (_, _) => SetColResizeMode(!IsManualColResize);
        menu.Items.Add(item);

        void Refresh() => item.Header = MenuIcons.WidthModeHeader(
            IsManualColResize ? "Стандартная ширина столбцов" : "Ручная ширина столбцов");

        Refresh();
        OnMenuOpening(menu, Refresh);
    }

    // Пункт-переключатель режима высоты строк (общий для всей таблицы) — в меню записи.
    // Текст показывает режим, в который переключит клик: при стандартной высоте —
    // «Ручная высота строк», при ручной — «Стандартная высота строк». Заголовок
    // пересчитывается при открытии меню.
    private void AddHeightModeItems(ContextMenu menu)
    {
        menu.Items.Add(new Separator());

        var item = new MenuItem().WithGesture("table.rowHeightMode", this);
        item.Click += (_, _) => SetRowResizeMode(!IsManualRowResize);
        menu.Items.Add(item);

        void Refresh() => item.Header = MenuIcons.HeightModeHeader(
            IsManualRowResize ? "Стандартная высота строк" : "Ручная высота строк");

        Refresh();
        OnMenuOpening(menu, Refresh);
    }

    // Меню заголовка пользовательского поля:
    //   Изменить · Дублировать · Создать новое свойство · — · Сдвинуть вправо ·
    //   Сдвинуть влево · — · Удалить
    private ContextMenu BuildHeaderContextMenu(int fieldId, bool isRequired, string name, string fieldType,
                                               bool fileAuto = false)
    {
        var menu = new ContextMenu();

        // Шапка: иконка типа + название свойства + тип данных (некликабельная).
        menu.Items.Add(MakeMenuHeaderItem(MenuIcons.PropertyMenuHeader(name, fieldType)));
        menu.Items.Add(new Separator());

        var miEdit = new MenuItem { Header = MenuIcons.EditHeader() }
            .WithGesture("table.editProperty", this);
        Guarded(miEdit, PermAction.EditField, () => PropertyEditRequested?.Invoke(this, fieldId));
        menu.Items.Add(miEdit);

        var miDuplicate = new MenuItem { Header = MenuIcons.DuplicateHeader() }
            .WithGesture("table.duplicateProperty", this);
        Guarded(miDuplicate, PermAction.DuplicateField, () => RequestDuplicateFromMenu(fieldId));
        menu.Items.Add(miDuplicate);

        var miCreate = new MenuItem { Header = MenuIcons.CreatePropertyHeader() }
            .WithGesture("table.createProperty", this);
        Guarded(miCreate, PermAction.CreateField, () => PropertyCreateAfterRequested?.Invoke(this, fieldId.ToString()));
        menu.Items.Add(miCreate);

        var miDisplay = new MenuItem { Header = MenuIcons.DisplayHeader() }
            .WithGesture("table.columnDisplay", this);
        miDisplay.Click += (_, _) => ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(miDisplay);

        AddColumnColorItems(menu, fieldId.ToString());
        AddColumnTextFormatItems(menu, fieldId.ToString(), fieldType);

        // «Файл» с авто-привязкой: пересканировать папку и пересобрать значения (файлы на диске
        // могли поменяться / появились новые). Значения read-only ведет правило.
        if (fileAuto)
        {
            var miRefresh = new MenuItem { Header = "Обновить привязку" };
            Guarded(miRefresh, PermAction.EditRecord, () => RefreshAutoFileFromMenu(fieldId));
            menu.Items.Add(miRefresh);
        }

        // Значения всего столбца: копирование, вставка и очистка — своей группой, отдельно от
        // «Удалить», которое убирает сам столбец вместе со значениями.
        AddCellValueItems(menu, () => SelectWholeColumn(fieldId.ToString()));

        AddMoveItems(menu, fieldId.ToString());
        AddWidthModeItems(menu);

        menu.Items.Add(new Separator());

        var miDelete = MakeDeleteMenuItem(enabled: !isRequired).WithGesture("table.deleteColumns", this);
        Guarded(miDelete, PermAction.DeleteField, () => RequestDeleteFromMenu(fieldId));
        menu.Items.Add(miDelete);

        return menu;
    }

    // Цвет колонки — тот же аспект column_meta, что видимость и подпись, поэтому право
    // как у правки свойства. Состав пересчитывается при открытии: и отметка текущего цвета,
    // и НАБОР колонок — выделение к моменту клика могло смениться.
    private void AddColumnColorItems(ContextMenu menu, string key)
    {
        var host = new MenuItem { Header = MenuIcons.ColorHeader() };
        MenuIcons.FixSubmenuGap(host);
        host.IsEnabled = Can(PermAction.EditField);

        OnMenuOpening(menu, () =>
        {
            host.Items.Clear();
            var keys = TargetColumnKeys(key);
            var cur  = ColumnStyleOf(key);   // отмечаем цвет колонки, по которой кликнули
            host.Items.Add(ColorMenu.Build("Фон колонки", cur.Bg,
                hex => ApplyColumnStyle(keys, StyleAspect.Background, hex)));
            host.Items.Add(ColorMenu.Build("Цвет текста колонки", cur.Fg,
                hex => ApplyColumnStyle(keys, StyleAspect.Text, hex)));
        });

        menu.Items.Add(host);
    }

    // Колонки, к которым применяется цвет: все выделение, если колонка в него входит
    // (как дубль и удаление свойств), иначе только она. В отличие от них системные
    // колонки НЕ отбрасываются: «Название» красится наравне с остальными.
    private List<string> TargetColumnKeys(string key) =>
        _selectedColumnKeys.Contains(key) && _selectedColumnKeys.Count > 1
            ? [.. _selectedColumnKeys]
            : [key];

    // «Обновить привязку» файла: пересканировать папку, пересобрать значения и перечитать строки.
    // Окно прогресса — только если долго (маленькая коллекция обновляется без мелькания).
    private async void RefreshAutoFileFromMenu(int fieldId)
    {
        if (_repository is null) return;
        var win = VisualRoot as Window;

        using var cts = new CancellationTokenSource();
        ProgressDialog? dlg = null;
        var progress = new Progress<double>(f => dlg?.SetProgress(f, "Поиск файлов…"));
        var bg = _repository.ResolveAutoFileFieldAsync(fieldId, progress, cts.Token);
        if (win is not null && await Task.WhenAny(bg, Task.Delay(400)) != bg)
        {
            dlg = new ProgressDialog { Title = "Привязка файлов" };
            dlg.SetProgress(0, "Поиск файлов…");
            dlg.EnableCancel(() => cts.Cancel());
            var closed = dlg.ShowDialog(win);
            await bg;
            dlg.Done();
            await closed;
        }
        var result = await bg;
        ReloadRows();
        if (result == ConstructorRepository.FileAutoResolveResult.FolderInaccessible && win is not null)
            await ConfirmDialog.Info("Папка недоступна").ShowDialog(win);
    }

    // Удаление из контекстного меню «Удалить». Если поле входит в текущее выделение
    // (несколько колонок) — удаляем весь выделенный набор; иначе только это поле.
    // В любом случае идем через пакетный путь с подтверждением (PropertiesDeleteRequested).
    private void RequestDeleteFromMenu(int fieldId)
    {
        var key = fieldId.ToString();

        List<int> ids;
        if (_selectedColumnKeys.Contains(key) && _selectedColumnKeys.Count > 1)
        {
            ids = _selectedColumnKeys
                .Where(k => int.TryParse(k, out _))
                .Select(int.Parse)
                .ToList();
        }
        else
        {
            ids = [fieldId];
        }

        if (ids.Count == 0) return;

        // Перед показом диалога оставляем рамки только на удаляемых колонках.
        KeepSelectionOnDeletableOnly();
        PropertiesDeleteRequested?.Invoke(this, ids);
    }

    // Дублирование из контекстного меню «Дублировать». Если поле входит в текущее
    // мультивыделение (несколько колонок) — дублируем весь выделенный набор; иначе только это
    // поле. Системные колонки (нечисловые ключи) в набор не попадают. Пакетный путь — как удаление.
    private void RequestDuplicateFromMenu(int fieldId)
    {
        var key = fieldId.ToString();

        List<int> ids =
            _selectedColumnKeys.Contains(key) && _selectedColumnKeys.Count > 1
                ? _selectedColumnKeys.Where(k => int.TryParse(k, out _)).Select(int.Parse).ToList()
                : [fieldId];

        if (ids.Count == 0) return;
        PropertyDuplicateRequested?.Invoke(this, ids);
    }

    // Убирает из выделения системные колонки («Название»/«Описание») — они не
    // удаляются, поэтому при запросе удаления их заливка гаснет, а на удаляемых
    // полях остается видимой, пока открыт диалог подтверждения.
    private void KeepSelectionOnDeletableOnly()
    {
        var removed = _selectedColumnKeys.RemoveAll(k => !int.TryParse(k, out _));
        if (removed > 0) ApplyColumnSelection();
    }

    // ── Фильтр и сортировка колонок (чеклист значений) ──────────────────────────────

    private void ShowFilterFlyout(Control anchor, TableColumnDef def)
    {
        if (_repository is null) return;

        // Порог медленности: построение и показ окна фильтра (значения считаются уже в фоне,
        // их порог — отдельно, см. GetColumnCountsAsync ниже).
        using var slow = Diag.Measure("окно фильтра колонки", Diag.UiOpen,
                                      $"{DbErrors.Display(_repository.DbPath)}, коллекция {_collectionId}, колонка {def.Header}");

        var flyout = PopupPlate.Filter();
        // Флайаут рендерится в отдельном попап-окне вне зума приложения — контент
        // оборачивается в зум-трансформ (плашка-презентер остается вне зума).
        flyout.Content = WindowZoom.WrapPopupContent(BuildFilterPanel(def, () => flyout.Hide()));
        // Окно закроет панель, если потеряет фокус: иначе на macOS она зависает поверх
        // других программ (см. TransientPopups).
        TransientPopups.Track(flyout);
        LastOpenedFilter = flyout;
        flyout.ShowAt(anchor);
    }

    // Для стендов: последнее открытое окно фильтра колонки.
    internal Flyout? LastOpenedFilter { get; private set; }

    private Control BuildFilterPanel(TableColumnDef def, Action close)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;
        var radius    = (CornerRadius)this.FindResource("RadiusBase")!;
        var family    = this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null;
        // Ширины строк и закрепленный справа счетчик — см. FilterValueMetrics.
        var metrics   = new FilterValueMetrics();

        // Курсор-стрелка по умолчанию для всей панели (наследуется вниз): на пустых
        // местах и подложке — стрелка. Интерактивные элементы (кнопки сортировки,
        // чекбоксы значений, «Готово») задают курсор-руку явно и перебивают наследование.
        var panel = new StackPanel
        {
            // Ширина и высота карточки списка — общий стандарт всплывающих списков
            // (PopupContentWidth/PopupListHeight в Controls.axaml).
            Width  = (double)this.FindResource("PopupContentWidth")!,
            Margin = new Thickness(12),
            Spacing = 2,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        // ── Данные (грузятся в ФОНЕ; окно открывается мгновенно) ──
        // Уникальные значения с количеством считаются SQL-агрегатом на фоновом потоке
        // (отдельное read-only подключение, WAL → параллельное чтение). До прихода данных в
        // области списка показывается «Загрузка…». Каскад фильтров: значения и счетчики — только по
        // строкам, прошедшим фильтры ОСТАЛЬНЫХ колонок и не скрытым в «Отображении записей»
        // (свой фильтр колонки список не режет — иначе снятое значение не вернуть). Считается
        // тем же SQL, что и сама фильтрация (GetColumnCounts), в фоне.
        var baseCounts = new List<(string Value, int Count)>();
        var domain     = new HashSet<string>();
        var loaded     = false;

        // Отмеченные сырые значения — общий набор строк списка, дерева дат и правил галочек
        // (FilterSelection: поиск отмечает найденное, очистка возвращает прежние, итог «Готово»).
        // Наполняется по приходе данных (см. фоновую загрузку ниже).
        var checkedSet = new HashSet<string>();
        var selection  = new FilterSelection(checkedSet);
        // Колонка с тысячами разных значений (ColumnValueList.Capped): список — первые
        // FilterValueCap по алфавиту, поиск — запросом к базе, галочки — правилом «все, кроме
        // снятых» (CappedFilterSelection). Null — обычный список всех значений.
        CappedFilterSelection? capped = null;
        CancellationTokenSource? cappedSearch = null;
        // Значения действующего фильтра колонки — при открытии они идут первыми (их видно сразу,
        // не листая весь список). Снимок на момент открытия: от щелчков по галочкам строки не прыгают.
        var initialChosen = _filters.TryGetValue(def.Key, out var f0) ? new HashSet<string>(f0) : null;

        // ── Сортировка ──
        // Выбор не применяется сразу: копится в pendingSort и вступает в силу
        // вместе с фильтром по кнопке «Готово».
        var (ascLabel, descLabel) = def.FieldType switch
        {
            "number" => ("По возрастанию", "По убыванию"),
            "date"   => ("Сначала старые", "Сначала новые"),
            _        => ("От А до Я", "От Я до А"),
        };
        bool? pendingSort = _sortKey == def.Key ? _sortAsc : null;

        var sortSection = new StackPanel { Spacing = 2 };
        panel.Children.Add(sortSection);

        Action rebuildListRef = () => { };   // заполняется после объявления RebuildList

        List<(string Raw, int Count)> counts = [];
        void ReloadCounts()
        {
            // Порядок значений = выбранная сортировка ЭТОЙ колонки (pending или активная),
            // иначе по значению с пустыми в конце; отобранные раньше — впереди остальных.
            bool? asc = pendingSort ?? (_sortKey == def.Key ? _sortAsc : (bool?)null);
            counts = FilterSelection.ChosenFirst(SortValueCounts(baseCounts, def.FieldType, asc),
                                                 c => c.Raw, initialChosen);
        }
        ReloadCounts();

        void RenderSort()
        {
            sortSection.Children.Clear();
            sortSection.Children.Add(MakeFlatItem(ascLabel,
                () => { pendingSort = true;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                SortAzIcon, active: pendingSort == true));
            sortSection.Children.Add(MakeFlatItem(descLabel,
                () => { pendingSort = false; RenderSort(); ReloadCounts(); rebuildListRef(); },
                SortZaIcon, active: pendingSort == false));
            sortSection.Children.Add(MakeFlatItem("Снять сортировку",
                () => { pendingSort = null;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                XIcon));
        }
        RenderSort();

        panel.Children.Add(MakeSeparator());

        // ── Поиск по значениям ──
        var search = new TextBox
        {
            Watermark    = "Поиск значений...",
            Height       = 30,
            CornerRadius = radius,
            Background    = Brushes.White,   // белая карточка на серой подложке (как в сайдбаре)
            Margin       = new Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        var syncingSelectAll = false;

        // Подпись меняется по режиму: при поиске — «(Выделить все найденное)», как в табличных редакторах.
        var selectAll = new CheckBox
        {
            Content   = "(Выделить все)",
            FontSize  = fontBase,
            IsChecked = !_filters.ContainsKey(def.Key),
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        // Список значений — виртуализирующий ListBox: рендерятся только видимые строки,
        // поэтому показываются ВСЕ уникальные значения (без лимита) и без зависания даже
        // при сотнях тысяч значений. «Выделить все» закреплен сверху.
        var shown    = new AvaloniaList<FilterValueVm>();
        var vmCache  = new Dictionary<string, FilterValueVm>();
        FilterValueVm Vm(string raw, int count) =>
            vmCache.TryGetValue(raw, out var v) ? v
                : vmCache[raw] = new FilterValueVm(raw, DisplayValue(raw, def.FieldType, def.Separator, def.NumberFormat), count,
                                                   r => capped?.IsChecked(r) ?? checkedSet.Contains(r),
                                                   (r, on) =>
                                                   {
                                                       if (capped is not null) capped.Set(r, on);
                                                       else if (on) checkedSet.Add(r);
                                                       else checkedSet.Remove(r);
                                                   },
                                                   () => SyncSelectAll());

        var valuesList = new ListBox
        {
            // Высоты нет: список занимает остаток строки карточки фиксированной высоты и
            // сам ужимается, когда снизу появляется горизонтальная полоса.
            Background       = Brushes.Transparent,
            BorderThickness  = new Thickness(0),
            Padding          = new Thickness(0),
            ItemsSource      = shown,
            ItemTemplate     = BuildFilterValueTemplate(fontBase, secondary, metrics),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            // Вертикальный отступ строк — как у списка свойств (StackPanel Spacing=4):
            // 2px сверху/снизу дают такой же воздух между чекбоксами, иначе они слипаются.
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        // Строки без подсветки наведения/выбора (синей/серой) — взаимодействуем только
        // с чекбоксом, поэтому фон контейнера строки прозрачен во всех состояниях.
        void FlatRow(params string[] pseudo) =>
            valuesList.Styles.Add(new Style(x =>
            {
                Avalonia.Styling.Selector s = x.OfType<ListBoxItem>();
                foreach (var p in pseudo) s = s.Class(p);
                return s.Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter");
            })
            {
                Setters = { new Setter(Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });
        FlatRow();
        FlatRow(":pointerover");
        FlatRow(":selected");
        FlatRow(":selected", ":pointerover");

        // «Выделить все» — закрепленная шапка списка; полоса прокрутки стоит на ее уровне
        // (отдельной колонкой), как вертикальная полоса таблицы стоит на уровне шапки колонок.
        // gap=2: вместе с верхним отступом первой строки (Padding 0,2) дает под шапкой
        // ровно такой же просвет, как между значениями.
        var valuesBox = ScrollBarLink.HeaderedList(selectAll, valuesList, gap: 2,
                                                   scroller: metrics.Track);

        // До прихода данных — «Загрузка…»; по готовности заменяется на список значений.
        var loadingText = new TextBlock
        {
            Text                = "Загрузка…",
            Foreground          = secondary,
            FontSize            = fontBase,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        var valuesHost = new Border
        {
            Background       = Brushes.White,   // белая карточка списка на серой подложке (как в сайдбаре)
            BorderBrush     = (IBrush)this.FindResource("BrushSeparator")!,
            CornerRadius    = radius,
            // Padding НУЛЕВОЙ: полосы прокрутки списка прилегают ко всем краям карточки без
            // белого зазора, а воздух вокруг содержимого задает ScrollBarLink.HeaderedList.
            // ClipToBounds — квадратные углы полос срезаются скруглением карточки.
            Padding         = new Thickness(0),
            ClipToBounds    = true,
            // Фикс высота (стандарт) — список не «прыгает» при замене «Загрузка…».
            Height          = (double)this.FindResource("PopupListHeight")!,
            Child           = loadingText,
        };
        // Карточка живет в контенте флайаута, обернутом в зум-трансформ — токен обычный.
        valuesHost.Bind(Border.BorderThicknessProperty, valuesHost.GetResourceObservable("BorderLine"));
        panel.Children.Add(valuesHost);
        var cappedNote = new TextBlock
        {
            Foreground   = secondary,
            FontSize     = fontBase,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(2, 4, 2, 0),
            IsVisible    = false,
        };
        panel.Children.Add(cappedNote);

        // Колонка-дата показывает не плоский список значений, а дерево «Год → Месяц → День»
        // (DateFilterTreeBox): сырое значение даты нечитаемо, а по плоскому списку нельзя
        // выбрать «весь август». Отмечается день, фильтр по-прежнему работает сырыми
        // значениями. Дерево появляется вместе с данными (см. фоновую загрузку ниже).
        var isDate = def.FieldType == "date";
        DateFilterTreeBox? tree = null;
        var treeNewestFirst = false;   // порядок, которым построено текущее дерево

        // Пересобирает дерево под выбранную сортировку колонки и вешает его в карточку.
        void MountTree(bool newestFirst)
        {
            treeNewestFirst = newestFirst;
            tree = DateFilterTreeBox.ForStorageDates(counts, checkedSet, fontBase, secondary, newestFirst);
            tree.SelectionChanged += (_, _) => SyncSelectAll();
            // «(Выделить все)» — общий чекбокс обоих режимов, а шапку HeaderedList он
            // получает во владение. Снимаем его с прежнего родителя: он уже стоит в шапке
            // плоского списка (та строится до загрузки данных), а при смене сортировки —
            // в шапке предыдущего дерева. Иначе Avalonia бросает «already has a visual parent».
            (selectAll.Parent as Panel)?.Children.Remove(selectAll);
            // Пустой результат поиска показывает «Ничего не найдено» — как у плоского списка.
            valuesHost.Child = EmptyListHint.Wrap(
                tree.List, search, secondary, fontBase,
                content: ScrollBarLink.HeaderedList(selectAll, tree.List, gap: 2));
            tree.Search(search.Text);
        }

        List<(string Raw, int Count)> Matched()
        {
            if (capped is not null) return counts;   // уже отобрано запросом к базе
            var q = search.Text?.Trim() ?? "";
            return q.Length == 0
                ? counts
                : counts.Where(c => DisplayValue(c.Raw, def.FieldType, def.Separator, def.NumberFormat)
                    .Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Показанные сейчас сырые значения — общий язык обоих режимов списка: по ним
        // считается «(Выделить все)», автоотметка найденного и режим «Заменить» в
        // «Готово». Плоский список берет их из уже построенного shown (RebuildList идет
        // первым), а не фильтрует значения заново на каждое нажатие клавиши.
        IEnumerable<string> MatchedRaws() =>
            tree is not null ? tree.VisibleRaws : shown.Select(v => v.Raw);

        void RebuildList()
        {
            if (isDate)
            {
                if (tree is null) return;   // значения еще грузятся — дерева нет
                // Сменили сортировку колонки — дерево пересобираем в новом порядке,
                // иначе годы и дни остались бы в прежнем.
                if (treeNewestFirst != (pendingSort == false)) MountTree(pendingSort == false);
                else tree.Search(search.Text);
                return;
            }

            shown.Clear();
            shown.AddRange(Matched().Select(c => Vm(c.Raw, c.Count)));
            // Ширина строки фиксируется по текущему набору — иначе экстент горизонтальной
            // прокрутки считался бы только по видимым строкам и полоса прыгала бы.
            metrics.Measure(shown.Select(v => v.Display), shown.Select(v => v.CountText),
                            fontBase, family);
        }
        rebuildListRef = RebuildList;

        // «Выделить все» отражает состояние видимых значений: снял одну галку —
        // снимается и общая. Guard защищает от каскада, когда IsChecked меняется
        // программно (иначе обработчик ниже снял бы галки со всех значений).
        void SyncSelectAll()
        {
            syncingSelectAll    = true;
            var searching       = capped?.Searching ?? selection.Searching;
            selectAll.Content   = searching ? "(Выделить все найденное)" : "(Выделить все)";
            selectAll.IsChecked = tree is not null ? tree.AllVisibleChecked
                : capped is not null ? (capped.Searching ? shown.All(v => capped.IsChecked(v.Raw)) : capped.AllChecked)
                : shown.All(v => checkedSet.Contains(v.Raw));
            syncingSelectAll    = false;
        }

        // Галочки поменяли не через строки (поиск отметил найденное, очистка вернула прежние) —
        // строкам списка и дереву надо перечитать их из общего набора.
        void RefreshChecks()
        {
            if (tree is not null) tree.RefreshChecked();
            else foreach (var vm in shown) vm.Refresh();
        }

        // Текст поиска изменился: список сужается, найденное отмечается само, а
        // очищенный поиск возвращает галочки, бывшие до ввода. Так же вызывается по приходе
        // данных — на случай, если печатать начали раньше, чем они загрузились.
        void OnSearchChanged()
        {
            if (capped is not null) { SearchCapped(); return; }
            RebuildList();
            selection.OnSearch(search.Text, MatchedRaws());
            RefreshChecks();
            SyncSelectAll();
        }

        // Поиск ограниченного списка — запросом к базе, с паузой на набор: найденное отмечается
        // целиком на каждое изменение текста (CappedFilterSelection.OnSearch), очистка возвращает
        // прежние галочки и первые значения по алфавиту.
        void SearchCapped()
        {
            capped!.OnSearch(search.Text);
            cappedSearch?.Cancel();
            var cts  = cappedSearch = new CancellationTokenSource();
            var text = search.Text;
            RefreshChecks();
            SyncSelectAll();
            _ = LoadCappedAsync();

            async Task LoadCappedAsync()
            {
                try
                {
                    await Task.Delay(250, cts.Token);
                    var list = await _repository!.GetColumnValueListAsync(_collectionId, def.Key, _filters,
                                   excludeHidden: AnyHiddenRecords, search: text, ct: cts.Token);
                    if (cts.IsCancellationRequested) return;
                    ShowCapped(list);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    DbErrors.HandleRead(ex, _repository!.DbPath);
                    Serilog.Log.Warning(ex, "Поиск значений фильтра не выполнен: {Db:l}, коллекция {Collection}, колонка {Column:l}",
                                        DbErrors.Display(_repository.DbPath), _collectionId, def.Header);
                }
            }
        }

        // Ограниченный список пришел (при открытии или поиском): строки, галочки, подпись.
        // «(Пусто)» и число значений досчитываются позже (LoadCappedTotalsAsync).
        ConstructorRepository.ColumnValueList? cappedList = null;
        long cappedDistinct = -1;
        var  cappedEmpty    = 0;
        void ShowCapped(ConstructorRepository.ColumnValueList list)
        {
            cappedList = list;
            baseCounts.Clear();
            baseCounts.AddRange(list.Values);
            if (!capped!.Searching && cappedEmpty > 0) baseCounts.Add(("", cappedEmpty));
            ReloadCounts();
            RebuildList();
            RefreshChecks();
            SyncSelectAll();
            UpdateCappedNote();
        }

        void UpdateCappedNote()
        {
            var cap = ConstructorRepository.FilterValueCap;
            cappedNote.Text = capped!.Searching
                ? (cappedList?.More == true ? $"Найдено больше {cap:N0} — уточните поиск" : "")
                : cappedDistinct > 0
                    ? $"Показаны первые {cap:N0} из {cappedDistinct:N0} значений — остальные ищите поиском"
                    : $"Показаны первые {cap:N0} значений — остальные ищите поиском";
            cappedNote.IsVisible = cappedNote.Text.Length > 0;
        }

        // Итоги ограниченного списка — после показа первых значений: число значений в подпись,
        // «(Пусто)» в конец списка (на 10 млн записей это секунды, список нужен сразу).
        async Task LoadCappedTotalsAsync()
        {
            try
            {
                var (distinct, empty) = await _repository!.GetColumnValueTotalsAsync(_collectionId, def.Key, _filters,
                                                                                    excludeHidden: AnyHiddenRecords);
                cappedDistinct = distinct;
                cappedEmpty    = empty;
                if (!capped!.Searching && cappedList is { } shownList) ShowCapped(shownList);
                else UpdateCappedNote();
            }
            catch (Exception ex)
            {
                DbErrors.HandleRead(ex, _repository!.DbPath);
                Serilog.Log.Warning(ex, "Итоги списка значений фильтра не посчитаны: {Db:l}, коллекция {Collection}, колонка {Column:l}",
                                    DbErrors.Display(_repository.DbPath), _collectionId, def.Header);
            }
        }

        search.TextChanged += (_, _) => OnSearchChanged();
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncingSelectAll) return;
            // «Выделить все» действует на видимое (после поиска) подмножество.
            var on = selectAll.IsChecked == true;
            if (tree is not null) { tree.SetAllVisible(on); return; }
            if (capped is not null)
            {
                capped.SetAll(on, shown.Select(v => v.Raw));
                RefreshChecks();
                SyncSelectAll();
                return;
            }

            foreach (var (raw, _) in Matched())
            {
                if (on) checkedSet.Add(raw);
                else    checkedSet.Remove(raw);
            }
            RebuildList();
        };
        // Первичное наполнение списка — после фоновой загрузки данных (см. ниже).

        panel.Children.Add(MakeSeparator());

        // ── Кнопка «Готово» ──
        // Сортировка и фильтр применяются разом — по этой кнопке или по Enter в поиске.
        var okBtn = new Button
        {
            Content             = "Готово",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        okBtn.Classes.Add("dialog");
        void Apply()
        {
            // Данные еще грузятся — применять нечего (фильтр по колонке не трогаем).
            if (!loaded)
            {
                if (pendingSort is bool a) { _sortKey = def.Key; _sortAsc = a; ApplyFilters(); }
                else if (_sortKey == def.Key) { _sortKey = null; ApplyFilters(); }
                close();
                return;
            }

            if (pendingSort is bool asc) { _sortKey = def.Key; _sortAsc = asc; }
            else if (_sortKey == def.Key) _sortKey = null;

            // Режим «Заменить»: при поиске фильтр = отмеченные СРЕДИ найденного, остальное
            // отсекается; без поиска — все отмеченные. Отмечено все, что есть в колонке (с учетом
            // фильтров других колонок), — фильтр не нужен (FilterSelection.Effective).
            var effective = capped is not null
                ? capped.Effective(shown.Select(v => v.Raw))
                : selection.Effective(MatchedRaws(), domain);
            if (effective is null) _filters.Remove(def.Key);
            else _filters[def.Key] = effective;
            ApplyFilters();
            close();
        }
        okBtn.Click += (_, _) => Apply();
        // Enter в поиске — то же, что «Готово»: набрал, нажал, таблица отобрана.
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
        panel.Children.Add(okBtn);

        // ── Фоновая загрузка значений ──
        // Окно уже показано с «Загрузка…»; считаем значения на фоновом потоке и по готовности
        // (на UI-потоке) наполняем список. Если окно успели закрыть — операции над уже
        // отсоединенными контролами безвредны. Фильтры других колонок и скрытые записи
        // уходят в запрос (каскад); свой фильтр колонки репозиторий отбрасывает сам.
        var countsSlow = Diag.Measure("список значений фильтра", Diag.UiQuery,
                                      $"{DbErrors.Display(_repository!.DbPath)}, коллекция {_collectionId}, колонка {def.Header}");
        _repository!.GetColumnValueListAsync(_collectionId, def.Key, _filters,
                                             excludeHidden: AnyHiddenRecords).ContinueWith(t =>
        {
            countsSlow.Dispose();
            if (t.IsFaulted)
            {
                // Окно так и останется с «Загрузка…» — без записи причину не найти.
                var ex = t.Exception?.GetBaseException();
                if (ex is not null)
                {
                    DbErrors.HandleRead(ex, _repository.DbPath);
                    if (!Database.DbHealthMonitor.IsUnavailableError(ex) && !Database.DbHealthMonitor.IsBusyError(ex))
                        Serilog.Log.Warning(ex, "Список значений фильтра не посчитан: {Db:l}, коллекция {Collection}, колонка {Column:l}",
                                            DbErrors.Display(_repository.DbPath), _collectionId, def.Header);
                }
                return;
            }
            if (t.IsCanceled) return;
            var list = t.Result;
            var data = list.Values;
            Dispatcher.UIThread.Post(() =>
            {
                if (list.Capped)
                {
                    capped = new CappedFilterSelection(_filters.TryGetValue(def.Key, out var cur) ? cur : null);
                    valuesHost.Child = EmptyListHint.Wrap(valuesList, search, secondary, fontBase, content: valuesBox);
                    if (FilterSelection.IsEmptyQuery(search.Text)) ShowCapped(list);
                    else SearchCapped();   // печатать начали до загрузки — ищем по базе
                    loaded = true;
                    _ = LoadCappedTotalsAsync();
                    return;
                }

                baseCounts.Clear();
                baseCounts.AddRange(data);
                domain.Clear();
                foreach (var c in data) domain.Add(c.Value);

                // Галочки — из действующего фильтра колонки, иначе все. Значения фильтра, которых
                // сейчас нет в списке (их строки спрятал фильтр другой колонки), остаются
                // отмеченными: снимут чужой фильтр — они снова в отборе.
                checkedSet.Clear();
                if (_filters.TryGetValue(def.Key, out var ff))
                {
                    // Отбор «кроме» (ставился, когда значений было больше, чем в списке) — полный
                    // список знает все значения, и отбор становится обычным: все, кроме исключенных.
                    if (FilterExclusion.IsExclude(ff)) checkedSet.UnionWith(domain.Except(FilterExclusion.Values(ff)));
                    else checkedSet.UnionWith(ff);
                }
                else checkedSet.UnionWith(domain);

                ReloadCounts();

                if (isDate)
                {
                    // Дерево строится по ТЕКУЩЕЙ сортировке колонки: «Сначала новые» —
                    // годы, месяцы и дни идут сверху вниз от новых к старым.
                    MountTree(newestFirst: pendingSort == false);
                }
                else
                {
                    // «Загрузка…» → список значений; пустой список показывает надпись
                    // «Список пуст» / «Ничего не найдено» вместо голой белой карточки.
                    valuesHost.Child = EmptyListHint.Wrap(
                        valuesList, search, secondary, fontBase, content: valuesBox);
                }

                // Галочки заполнены заново — снимок «до поиска» (если печатали до загрузки)
                // устарел; OnSearchChanged строит список и, если текст уже есть, отмечает найденное.
                selection.Reset();
                OnSearchChanged();
                loaded = true;
            });
        });

        return panel;
    }

    // Иконка в стандарте сайдбара: Viewbox «Средний» поверх сетки 24×24.
    private Viewbox MakeStrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width  = IconSize("IconSizeMedium"),
        Height = IconSize("IconSizeMedium"),
        VerticalAlignment = VerticalAlignment.Center,
        Child  = new Canvas
        {
            Width  = 24,
            Height = 24,
            Children =
            {
                new Avalonia.Controls.Shapes.Path
                {
                    Data            = data,
                    Stroke          = stroke,
                    StrokeThickness = IconSize("IconStrokeMedium"),
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    private Button MakeFlatItem(string text, Action onClick, Geometry? icon = null, bool active = false)
    {
        // Активный пункт (выбранная сортировка) — акцентный цвет и галка справа.
        var fg = (IBrush)this.FindResource(active ? "BrushAccent" : "BrushPrimaryText")!;

        object content = text;
        if (icon is not null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var iconBox = MakeStrokeIcon(icon, fg);
            Grid.SetColumn(iconBox, 0);
            row.Children.Add(iconBox);

            var label = new TextBlock
            {
                Text                = text,
                Foreground          = fg,
                Margin              = new Thickness(8, 0, 0, 0),
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            if (active)
            {
                var check = MakeStrokeIcon(CheckIcon, fg);
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }

            content = row;
        }

        var btn = new Button
        {
            Content         = content,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(8, 6),
            FontSize        = (double)this.FindResource("FontSizeBase")!,
            Foreground      = fg,
            CornerRadius    = (CornerRadius)this.FindResource("RadiusBase")!,
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = icon is not null
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Left,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // Разделитель в попапе фильтра: контент флайаута обернут в зум-трансформ
    // (WrapPopupContent) → токен обычный; Border пустой → Aliased защелкивает линию.
    private Border MakeSeparator()
    {
        var sep = new Border
        {
            Background = (IBrush)this.FindResource("BrushSeparator")!,
            Margin     = new Thickness(0, 6),
        };
        sep.Bind(HeightProperty, sep.GetResourceObservable("SeparatorThickness"));
        sep.Bind(UseLayoutRoundingProperty, sep.GetResourceObservable("LineUseLayoutRounding"));
        RenderOptions.SetEdgeMode(sep, EdgeMode.Aliased);
        return sep;
    }

    // Упорядочивает список значений с количеством для панели фильтра. asc задан — сортировка
    // тем же компаратором, что и таблица (text/number/date); null — порядок как в таблице
    // (репозиторий уже отдал по первой позиции, «(Пусто)» в конце). src — SQL-агрегат по колонке.
    private static List<(string Raw, int Count)> SortValueCounts(
        List<(string Value, int Count)> src, string fieldType, bool? asc)
    {
        if (asc is bool a)
        {
            var cmp = BuildComparer(fieldType, a);
            return src.OrderBy(c => c.Value, Comparer<string>.Create((x, y) => cmp(x, y)))
                      .Select(c => (c.Value, c.Count)).ToList();
        }

        // Без сортировки — порядок как в таблице: репозиторий уже вернул значения по первой
        // позиции записи, «(Пусто)» в конце. Ничего не пересортировываем.
        return src.Select(c => (c.Value, c.Count)).ToList();
    }

    // Шаблон строки списка значений фильтра (чекбокс + количество) — для виртуализированного
    // ListBox: привязки следуют за DataContext при переиспользовании контейнеров.
    private static IDataTemplate BuildFilterValueTemplate(double fontBase, IBrush secondary,
                                                          FilterValueMetrics metrics) =>
        new FuncDataTemplate<FilterValueVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(FilterValueVm.Display)));
            // Ширина ВСЕХ строк одинаковая (по самому широкому значению) — иначе экстент
            // горизонтальной прокрутки считался бы по видимым строкам и полоса дергалась.
            name.Bind(Layoutable.MinWidthProperty,
                      new Binding(nameof(FilterValueMetrics.TextMinWidth)) { Source = metrics });

            var cb = new CheckBox
            {
                Content   = name,
                FontSize  = fontBase,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin    = new Thickness(0),
                Cursor    = new Cursor(StandardCursorType.Hand),
            };
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FilterValueVm.Checked)) { Mode = BindingMode.TwoWay });

            var cnt = new TextBlock { Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(FilterValueVm.CountText)));

            // Счетчик ЗАКРЕПЛЕН у правого края видимой области: сдвиг компенсирует
            // горизонтальную прокрутку, фон непрозрачный — длинный текст уезжает под него.
            // Плашка обхватывает САМО число (Gap слева), поэтому просвет до закрываемого
            // текста одинаковый в любой строке — не зависит от количества цифр.
            var pin = new TranslateTransform();
            pin.Bind(TranslateTransform.XProperty,
                     new Binding(nameof(FilterValueMetrics.Pin)) { Source = metrics });
            var cntHost = new Border
            {
                Background          = Brushes.White,
                Padding             = new Thickness(FilterValueMetrics.Gap, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child               = cnt,
                RenderTransform     = pin,
            };

            // Прозрачное МЕСТО под счетчик — одинаковой ширины во всех строках, чтобы от
            // количества цифр не зависела ширина строки, а с ней и экстент прокрутки.
            var cntSlot = new Border { Child = cntHost };
            cntSlot.Bind(Layoutable.MinWidthProperty,
                         new Binding(nameof(FilterValueMetrics.CountMinWidth)) { Source = metrics });

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);      row.Children.Add(cb);
            Grid.SetColumn(cntSlot, 1); row.Children.Add(cntSlot);
            return row;
        });

    // Строка списка значений фильтра. Checked читает/пишет общий checkedSet; onChanged
    // обновляет состояние «(Выделить все)».
    private sealed class FilterValueVm : System.ComponentModel.INotifyPropertyChanged
    {
        public string Raw       { get; }
        public string Display   { get; }
        public string CountText { get; }

        // Галочка — в общем наборе окна или по правилу «все, кроме снятых» (CappedFilterSelection).
        private readonly Func<string, bool> _isChecked;
        private readonly Action<string, bool> _setChecked;
        private readonly Action _onChanged;

        public FilterValueVm(string raw, string display, int count, Func<string, bool> isChecked,
                             Action<string, bool> setChecked, Action onChanged)
        {
            Raw = raw; Display = display; CountText = count.ToString();
            _isChecked = isChecked; _setChecked = setChecked; _onChanged = onChanged;
        }

        public bool Checked
        {
            get => _isChecked(Raw);
            set
            {
                _setChecked(Raw, value);
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Checked)));
                _onChanged();
            }
        }

        /// <summary>Перечитать галочку из общего набора — ее поменяли не через строку
        /// (поиск отметил найденное, очистка поиска вернула прежние).</summary>
        public void Refresh() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Checked)));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static string DisplayValue(string raw, string fieldType, string separator = ", ", NumberFormat? numberFormat = null)
    {
        // Связь: значение фильтра = название связанной записи (Title); пусто → «(нет связей)».
        if (fieldType == "relation") return raw == "" ? "(нет связей)" : OneLine(raw);
        // Чекбокс: окно фильтра получает две группы (GetColumnCounts) — отмечено и все остальное.
        if (fieldType == "checkbox") return CheckboxValue.IsChecked(raw) ? "✓" : "(Не отмечено)";
        if (raw == "") return "(Пусто)";
        if (fieldType == "multiselect") return OneLine(MultiSelectValue.Display(raw, separator));
        if (fieldType == "number") return NumberFormatValue.Format(raw, numberFormat ?? NumberFormat.Default);
        if (fieldType == "file") return FileValue.DisplayName(raw);   // имя файла, а не абсолютный путь
        return OneLine(raw);
    }

    // Значение в списке фильтра — всегда ОДНА строка: многострочный текст ячейки иначе
    // растягивает строку списка по высоте (и ширину меряет по самой длинной из своих
    // строк). Схлопываем переводы строк в пробел — как Detail в истории (ChangeDetail).
    // Фильтруется все равно исходное значение (FilterValueVm.Raw), меняется только показ.
    private static string OneLine(string s) =>
        s.Contains('\n') || s.Contains('\r') ? s.ReplaceLineEndings(" ").Trim() : s;

    /// <summary>
    /// Применяет активные фильтры/сортировку: сетка считает отбор и сортировку в фоне
    /// (RowSourcePlanner) — воронка в шапке сразу, строки по приходе списка; пока он идет, на
    /// экране прежние строки. Без отбора таблица работает на полном списке, с ним — на списке id
    /// или keyset'е по индексу, все постранично, без загрузки в память.
    /// </summary>
    private void ApplyFilters()
    {
        ForgetCellEditing();   // список строк сменится — набранное пишем, пока строки живы

        PruneFilters();
        if (_repository is null) return;

        // Запоминаем уже вычищенный от мертвых колонок отбор — ровно то, что видно на экране.
        // Точка одна на все пути (окно фильтра, сортировка, удаление поля), поэтому запомненное
        // не расходится с показанным.
        if (ConstructorSettings.RememberFilters && _collectionId > 0)
            ConstructorFilterMemory.Save(_repository.DbPath, _collectionId, _filters, _sortKey, _sortAsc);

        // Копия: набор фильтров фасада меняет окно фильтра, а фоновый запрос читает свой.
        RecordsGrid.SetQuery(CopyFilters(), _sortKey, _sortAsc);
    }

    // Фильтры и сортировка по колонкам, которых больше нет (поле удалили), снимаются. Скрытые в
    // «Отображении» колонки остаются в _colDefs — их отбор действует. true — что-то снято.
    private bool PruneFilters()
    {
        var validKeys = _colDefs.Select(d => d.Key).ToHashSet();
        var stale     = _filters.Keys.Where(k => !validKeys.Contains(k)).ToList();
        foreach (var key in stale) _filters.Remove(key);
        var sortGone = _sortKey is not null && !validKeys.Contains(_sortKey);
        if (sortGone) _sortKey = null;
        return stale.Count > 0 || sortGone;
    }

    private Dictionary<string, HashSet<string>> CopyFilters() =>
        _filters.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));

    // Есть ли скрытые в «Отображении записей» записи: их значения не попадают в списки фильтра.
    private bool AnyHiddenRecords => RecordsGrid.HasHiddenRecords;

    // Компаратор значений колонки (пустые всегда в конце) — общий с фоновой сортировкой, см. ColumnSort.
    private static Func<string, string, int> BuildComparer(string fieldType, bool asc) =>
        ColumnSort.BuildComparer(fieldType, asc);

    // ── Утилиты ────────────────────────────────────────────────────────────────

    private static bool IsTruthy(string v) => v is "1" or "true" or "True";

    private static IReadOnlyList<string> ParseSelectOptions(DbField field)
    {
        try
        {
            using var doc = JsonDocument.Parse(field.ConfigJson ?? "{}");
            if (doc.RootElement.TryGetProperty("options", out var opts) &&
                opts.ValueKind == JsonValueKind.Array)
                return opts.EnumerateArray().Select(o => o.ToString()).ToList();
        }
        catch { }
        return [];
    }
}
