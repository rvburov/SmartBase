using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// Пункты контекстных меню таблицы с клавиатуры. Каждый пункт, который можно вызвать клавишей,
/// показывает свое сочетание подсказкой (<see cref="MenuGestures"/>) и зовет ровно тот же код,
/// что клик по нему, — с теми же правами и подтверждениями.
///
/// Цель действия берется из выделения, а не из строки под курсором: у клавиатуры курсора нет.
/// Запись — выделенные строки, иначе запись активной ячейки; свойство — выделенные столбцы,
/// иначе столбец активной ячейки. Нечего взять — клавиша не наша, и ее увидит следующий
/// обработчик.
/// </summary>
public partial class TableView
{
    private bool HandleMenuShortcut(KeyEventArgs e)
    {
        // ── Записи ────────────────────────────────────────────────────────────
        if (ShortcutSettings.Matches("table.createRecord", e))
        {
            PermissionNotice.Run(this, Can(PermAction.CreateRecord), PermAction.CreateRecord,
                () => RecordCreateRequested?.Invoke(this, (ConstructorSettings.OpenNewRecord, TargetRecordId())));
            return true;
        }

        if (ShortcutSettings.Matches("table.openRecord", e))
        {
            if (TargetRecordId() is not { } rid) return false;
            RecordClicked?.Invoke(this, rid);
            return true;
        }

        if (ShortcutSettings.Matches("table.duplicateRecord", e))
        {
            if (TargetRecordIds() is not { Count: > 0 } ids) return false;
            PermissionNotice.Run(this, Can(PermAction.DuplicateRecord), PermAction.DuplicateRecord,
                () => RecordDuplicateRequested?.Invoke(this, ids));
            return true;
        }

        if (ShortcutSettings.Matches("table.relocateRecords", e))
        {
            if (TargetRecordIds() is not { Count: > 0 } ids) return false;
            // Перенос = изъятие записи из коллекции: право как у удаления (так же в меню).
            PermissionNotice.Run(this, Can(PermAction.DeleteRecord), PermAction.DeleteRecord,
                () => RecordRelocateRequested?.Invoke(this, ids));
            return true;
        }

        if (ShortcutSettings.Matches("table.recordDisplay", e))
        {
            RecordDisplayRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (ShortcutSettings.Matches("table.moveRecordUp", e))   return MoveRecordByShortcut(up: true);
        if (ShortcutSettings.Matches("table.moveRecordDown", e)) return MoveRecordByShortcut(up: false);

        if (ShortcutSettings.Matches("table.rowHeightMode", e))
        {
            SetRowResizeMode(!_manualRowResize);
            return true;
        }

        // ── Свойства (столбцы) ────────────────────────────────────────────────
        if (ShortcutSettings.Matches("table.createProperty", e))
        {
            PermissionNotice.Run(this, Can(PermAction.CreateField), PermAction.CreateField,
                () => PropertyCreateRequested?.Invoke(this, EventArgs.Empty));
            return true;
        }

        if (ShortcutSettings.Matches("table.editProperty", e))
        {
            if (TargetColumnKey() is not { } key) return false;
            PermissionNotice.Run(this, Can(PermAction.EditField), PermAction.EditField, () =>
            {
                // Системная колонка («Название») правится переименованием, у нее нет id поля.
                if (int.TryParse(key, out var fieldId)) PropertyEditRequested?.Invoke(this, fieldId);
                else                                    SystemFieldRenameRequested?.Invoke(this, key);
            });
            return true;
        }

        if (ShortcutSettings.Matches("table.duplicateProperty", e))
        {
            if (TargetFieldIds() is not { Count: > 0 } ids) return false;
            PermissionNotice.Run(this, Can(PermAction.DuplicateField), PermAction.DuplicateField,
                () => PropertyDuplicateRequested?.Invoke(this, ids));
            return true;
        }

        if (ShortcutSettings.Matches("table.columnDisplay", e))
        {
            ColumnDisplayRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (ShortcutSettings.Matches("table.moveColumnRight", e)) return MoveColumnByShortcut(left: false);
        if (ShortcutSettings.Matches("table.moveColumnLeft", e))  return MoveColumnByShortcut(left: true);

        if (ShortcutSettings.Matches("table.columnWidthMode", e))
        {
            SetColResizeMode(!_manualColResize);
            return true;
        }

        // ── Само меню ─────────────────────────────────────────────────────────
        if (ShortcutSettings.Matches("app.contextMenu", e)) return OpenContextMenuByShortcut();

        return false;
    }

    // ── Цель действия ─────────────────────────────────────────────────────────

    // Записи: выделенные строки, иначе запись активной ячейки.
    private List<int> TargetRecordIds()
    {
        var selected = SelectedRecordIds();
        if (selected.Count > 0) return selected;
        return TargetRecordId() is { } rid ? [rid] : [];
    }

    private int? TargetRecordId()
    {
        var selected = SelectedRecordIds();
        if (selected.Count > 0) return selected[0];
        if (_cellRange is not { } range) return null;
        return CachedRowAt(range.AnchorRow) is { RecordId: > 0 } row ? row.RecordId : null;
    }

    // Столбцы: выделенные по заголовку, иначе столбец активной ячейки. Служебные «№» и «+»
    // не в счет — над ними действий нет.
    private string? TargetColumnKey()
    {
        if (_selectedColumnKeys.Count > 0) return _selectedColumnKeys[0];
        if (_cellRange is not { } range) return null;
        if (range.AnchorColumn < 0 || range.AnchorColumn >= _colDefs.Count) return null;

        var key = _colDefs[range.AnchorColumn].Key;
        return key is TableColumnDef.NumKey or TableColumnDef.AddPropKey ? null : key;
    }

    // Только пользовательские поля: у системных ключ не число, дублировать и удалять их нельзя.
    private List<int> TargetFieldIds()
    {
        var keys = _selectedColumnKeys.Count > 0
            ? _selectedColumnKeys
            : TargetColumnKey() is { } key ? [key] : [];

        return keys.Where(k => int.TryParse(k, out _)).Select(int.Parse).ToList();
    }

    // ── Действия, у которых есть свои условия ─────────────────────────────────

    // Сдвиг записи живет только в «своем» порядке: при сортировке и фильтре пункта нет и в меню.
    private bool MoveRecordByShortcut(bool up)
    {
        if (_sortKey is not null || _filters.Count > 0) return false;
        if (TargetRecordId() is not { } rid) return false;
        if (!CanMoveRecord(rid, up)) return false;

        MoveRecord(rid, up);
        return true;
    }

    private bool MoveColumnByShortcut(bool left)
    {
        if (TargetColumnKey() is not { } key) return false;
        if (!CanMoveColumn(key, left)) return false;

        MoveColumn(key, left);
        return true;
    }

    // Контекстное меню с клавиатуры: меню выделенного столбца, если выделены столбцы, иначе
    // меню строки активной ячейки. Открывается у самой таблицы — под курсором мыши его ставит
    // сам Avalonia, а с клавиатуры курсора нет.
    private bool OpenContextMenuByShortcut()
    {
        if (_selectedColumnKeys.Count > 0 && ColumnMenuFor(_selectedColumnKeys[0]) is { } columnMenu)
        {
            columnMenu.Open(this);
            return true;
        }

        if (RowUnderSelection() is { ContextMenu: { } rowMenu })
        {
            rowMenu.Open(this);
            return true;
        }

        BuildGridContextMenu().Open(this);
        return true;
    }

    // Меню заголовка нужного столбца — собирается той же фабрикой, что и при правом щелчке
    // по заголовку (состав зависит от типа поля и прав).
    private ContextMenu? ColumnMenuFor(string key) =>
        _colDefs.FirstOrDefault(d => d.Key == key) is { BuildHeaderMenu: { } factory } def
            ? factory(def)
            : null;

    // Строка активной ячейки среди разложенных гридом: у нее уже есть свое меню записи.
    private DataGridRow? RowUnderSelection()
    {
        if (_cellRange is not { } range) return null;
        if (CachedRowAt(range.AnchorRow) is not { } vm) return null;

        return RecordsGrid.GetVisualDescendants()
                          .OfType<DataGridRow>()
                          .FirstOrDefault(r => ReferenceEquals(r.DataContext, vm));
    }
}
