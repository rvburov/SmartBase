using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using SmartBase.Controls.Components;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Правка набором, как в электронных таблицах. Ячейка выделена — печатайте: набранное встает в нее
/// вместо прежнего значения, Enter только записывает, Escape возвращает прежнее. Какой тип как
/// открывается набором и какой пункт списка выбрать — чистые правила <see cref="CellTyping"/>.
///
/// Правка, начатая набором, идет в режиме «ввод»: стрелки записывают значение и переводят выделение
/// так же, как с таблицы (с Shift — растягивают, с Ctrl — к краю). F2 переключает в режим «правка»,
/// где стрелки ходят по тексту, и обратно; клик в поле тоже переводит в «правку». Правка, открытая
/// Enter, F2 или вторым кликом, сразу идет в «правке» — как раньше.
///
/// Список (список, статус, пользователь) по набору раскрывается с пунктом, начинающимся с
/// набранного; следующие буквы уточняют, пробел и Backspace правят набранное. Как бы список ни
/// открыли, выбор в нем пишется при записи правки (Enter, Tab, клик мимо) и при уходе фокуса из
/// него, а Escape уходит без записи — как с текстом. Мультисписок и связь по набору раскрывают свой
/// выбор с набранным в поле поиска. Дата по цифре открывается ручным вводом с этой цифрой. Цифра в
/// дате и символ в мультисписке или связи, открытых Enter или кликом (фокус там остается в
/// таблице), делают то же.
/// </summary>
public partial class TableView
{
    // Режим «ввод»: правка начата набором, стрелки записывают значение и двигают выделение.
    private bool _enterMode;

    // Правка списка: выбор в этой правке менялся (_listDirty) — при записи его есть что писать, и
    // ключ колонки, куда писать (_listKey ставит OpenEditor).
    private bool    _listDirty;
    private string? _listKey;

    // Что набрано в списке в этой правке: следующие буквы уточняют поиск пункта.
    private string _listTerm = "";

    // Для стендов: правка в режиме «ввод».
    internal bool IsEnterMode => _enterMode;

    // Правится список (список, статус, пользователь).
    private bool IsListEditing => RecordsGrid.Editor.Editor is ComboBox;

    // Новая правка и закрытие правки начинают с чистого листа.
    private void ResetTyping()
    {
        _enterMode = false;
        _listDirty = false;
        _listKey   = null;
        _listTerm  = "";
    }

    // Ввод текста — туннелем, раньше полей и списков (у списка свой поиск по буквам, он ищет только
    // среди показанных пунктов и не знает, что «ё» — это «е»).
    private void OnTableTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        var typed = e.Text;

        if (IsCellEditing)
        {
            switch (RecordsGrid.Editor.Editor)
            {
                case ComboBox combo when IsFromEditor(e, combo):
                    e.Handled = SearchList(combo, typed);
                    break;
                // Дата, мультисписок и связь, открытые Enter или кликом, держат фокус в таблице:
                // набор начинает в них ручной ввод или поиск.
                case DateEditor date when IsGridSource(e) && CellTyping.Start("date", typed) == CellTypingStart.Date:
                    StartDateInput(date, typed);
                    e.Handled = true;
                    break;
                case MultiSelectBox or RelationSelectBox when IsGridSource(e)
                                                           && CellTyping.Start("multiselect", typed) == CellTypingStart.Picker:
                    StartPickerSearch(RecordsGrid.Editor.Editor, typed);
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (!IsGridSource(e) || _cellRange is not { } range || _selectedColumnKeys.Count > 0) return;
        if (BeginEditByTyping(range.AnchorRow, range.AnchorColumn, typed)) e.Handled = true;
    }

    // Ввод пришел в саму таблицу (фокус на сетке), а не в поле где-то внутри нее.
    private bool IsGridSource(RoutedEventArgs e) =>
        ReferenceEquals(e.Source, RecordsGrid.Surface) || ReferenceEquals(e.Source, this);

    // Событие пришло из редактора: из него самого, из его пунктов во всплывающем окне (они связаны с
    // ним логическим деревом) или из таблицы, у которой остался фокус.
    private bool IsFromEditor(RoutedEventArgs e, Control editor) =>
        IsGridSource(e) || IsWithin(e.Source, editor);

    private static bool IsWithin(object? element, Control editor) =>
        element is ILogical logical && (ReferenceEquals(logical, editor) || logical.GetLogicalAncestors().Contains(editor));

    /// <summary>
    /// Открыть правку активной ячейки набором: текст — с набранным вместо прежнего значения и
    /// кареткой после него, список — раскрытым на пункте по набранному, дата — ручным вводом,
    /// мультисписок и связь — выбором с набранным в поиске. false — набор ячейку не открывает: тип
    /// правится не текстом, набранное ему не подходит, строка еще не загружена, нет права правки
    /// (клавиатура молчит, как у Enter).
    /// </summary>
    private bool BeginEditByTyping(int row, int column, string typed)
    {
        if (!IsDataColumn(column)) return false;
        var def   = _colDefs[column];
        var start = def.IsReadOnly ? CellTypingStart.None : CellTyping.Start(def.FieldType, typed);
        if (start == CellTypingStart.None) return false;
        if (BeginEdit(row, column, byMouse: false) is not { } editor) return false;

        switch (editor)
        {
            case TextBox box:
                box.Text = typed;
                box.SelectionStart = box.SelectionEnd = box.CaretIndex = typed.Length;
                _enterMode = true;
                break;

            case ComboBox combo:
                // Раскладка до раскрытия: иначе список раскроется, не поставив фокус на пункт, и
                // стрелки по нему не пойдут.
                combo.UpdateLayout();
                SearchList(combo, typed);
                combo.IsDropDownOpen = true;
                break;

            case DateEditor date:
                StartDateInput(date, typed);
                _enterMode = true;
                break;

            case MultiSelectBox or RelationSelectBox:
                StartPickerSearch(editor, typed);
                break;
        }
        return true;
    }

    // Набор в списке: пункт, начинающийся со всего набранного в этой правке (CellTyping.FindOption).
    // Раскрытый список сам переводит фокус на выбранный пункт — Enter и стрелки идут от него.
    // true — набор принят списком.
    private bool SearchList(ComboBox combo, string typed)
    {
        if (typed.Any(char.IsControl)) return false;
        var (term, index) = CellTyping.FindOption(ListOptions(combo), _listTerm, typed, combo.SelectedIndex);
        _listTerm = term;
        if (index >= 0) combo.SelectedIndex = index;
        return true;
    }

    private static List<string> ListOptions(ComboBox combo) =>
        combo.Items.Cast<object?>().Select(o => o?.ToString() ?? "").ToList();

    // Ручной ввод даты с набранного. Поле ввода попадает в дерево с первой раскладкой редактора,
    // до нее фокус в него не ставится.
    private static void StartDateInput(DateEditor date, string typed)
    {
        date.UpdateLayout();
        date.BeginManualInput(typed);
    }

    // Выбор мультисписка или связи с набранным в поиске. Раскладка — до раскрытия: всплывающее
    // окно ставится по месту редактора, а у только что созданного редактора места еще нет.
    private static void StartPickerSearch(Control? picker, string typed)
    {
        if (picker is null) return;
        picker.UpdateLayout();
        switch (picker)
        {
            case MultiSelectBox multi:   multi.OpenSearch(typed);   break;
            case RelationSelectBox link: link.OpenSearch(typed);    break;
        }
    }

    // Каретка стоит в текстовом поле правки: в поле ячейки или в ручном вводе даты.
    private bool EditingTextHasFocus =>
        _editingBox is { IsKeyboardFocusWithin: true }
        || RecordsGrid.Editor.Editor is DateEditor { IsManualInputFocused: true };

    /// <summary>
    /// Клавиши правки, которые зависят от того, как она начата и что правится. Зовется из туннеля
    /// таблицы — раньше редактора и его всплывающего окна. true — клавиша обработана.
    /// </summary>
    private bool HandleTypingKey(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;

        // F2 в поле — переключатель «ввод» и «правка».
        if (e.Key == Key.F2 && mods == KeyModifiers.None && EditingTextHasFocus)
        {
            _enterMode = !_enterMode;
            e.Handled  = true;
            return true;
        }

        // «Ввод»: стрелка записывает значение и двигает выделение так же, как с таблицы.
        if (_enterMode && e.Key is (Key.Up or Key.Down or Key.Left or Key.Right)
            && (mods & KeyModifiers.Alt) == 0 && EditingTextHasFocus)
        {
            e.Handled = true;
            CommitEditAndFocusGrid();
            HandleSelectionKey(e);
            return true;
        }

        // Список и выбор мультисписка или связи: Escape отменяет правку сразу, а не только
        // сворачивает раскрытое окно (у списка — без записи выбора).
        if (e.Key == Key.Escape && mods == KeyModifiers.None
            && RecordsGrid.Editor.Editor is ComboBox or MultiSelectBox or RelationSelectBox)
        {
            e.Handled = true;
            CancelEdit(focusGrid: true);
            return true;
        }

        if (RecordsGrid.Editor.Editor is not ComboBox combo) return false;

        // Пока в списке что-то набрано, пробел и Backspace правят набранное: «в р» находит «В работе»,
        // а не выбирает пункт пробелом.
        if (_listTerm.Length > 0 && mods == KeyModifiers.None)
        {
            if (e.Key == Key.Space)
            {
                SearchList(combo, " ");
                e.Handled = true;
                return true;
            }
            if (e.Key == Key.Back)
            {
                _listTerm = _listTerm[..^1];
                if (CellTyping.IndexOf(ListOptions(combo), _listTerm) is >= 0 and var index) combo.SelectedIndex = index;
                e.Handled = true;
                return true;
            }
        }

        // Ход по пунктам клавишами начинает набор заново.
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) _listTerm = "";
        return false;
    }

    // Раскрытый список при записи правки берет пункт под фокусом — как сам список выбирает его по
    // Enter, — и сворачивается.
    private void TakeFocusedListItem()
    {
        if (RecordsGrid.Editor.Editor is not ComboBox { IsDropDownOpen: true } combo) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is ComboBoxItem item
            && combo.IndexFromContainer(item) is >= 0 and var index)
            combo.SelectedIndex = index;
        combo.IsDropDownOpen = false;
    }

    // Выбор в списке пишется при записи правки — и только если он менялся: значение не из пунктов
    // (пункт убрали из настроек свойства) иначе стерлось бы пустым выбором.
    private void FlushDeferredList(TableRowVm row)
    {
        if (_listDirty && _listKey is { } key && RecordsGrid.Editor.Editor is ComboBox combo)
            row[key] = combo.SelectedItem?.ToString() ?? "";
        _listDirty = false;
    }

    // Фокус ушел из списка и его пунктов (клик в панель записи, в боковую панель) — выбор пишется, а
    // правка остается открытой, как у текстового поля. Иначе правка той же записи в панели отменила
    // бы правку ячейки вместе с выбором. Смотрится после ухода: фокус в раскрытые пункты самого списка
    // — не уход.
    private void FlushListWhenFocusLeft(ComboBox combo)
    {
        if (!ReferenceEquals(RecordsGrid.Editor.Editor, combo) || !_listDirty) return;
        if (IsWithin(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(), combo)) return;
        if (_editRow is { } row && row.RecordId == _editRecordId) FlushDeferredList(row);
    }
}
