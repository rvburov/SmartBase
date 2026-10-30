using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb;

/// <summary>
/// «Форматирование текста» в меню таблицы. Пока в нем одно подменю — «Расположение текста»: где
/// стоит текст в ячейке по горизонтали и по вертикали. Уровни те же, что у «Изменить цвет», и каскад
/// тот же (RowStyle.Resolve: ячейка → строка → колонка, горизонталь и вертикаль — каждая сама по
/// себе): меню ячейки — выделенные ячейки, меню «№» — выделенные строки, меню заголовка — выделенные
/// столбцы вместе с будущими записями. «По умолчанию» снимает оба аспекта уровня: дальше решает
/// уровень ниже, а в конце — правило типа (число вправо, остальное влево, по вертикали центр).
///
/// Галочки: в меню ячейки — у того, что видно в ячейке; в меню столбца — у расположения столбца (не
/// задано — правило типа); в меню строки — у заданного строке (не задано — галочек нет: у строки
/// разнотипные ячейки). Галочке расположение не нужно — у нее нет текста: в ее меню пункта нет, а в
/// выделении ее ячейки и столбцы пропускаются.
///
/// Горизонталь — еще и клавишами текстового редактора (Ctrl+L, Ctrl+E, Ctrl+R): по выделенным
/// столбцам, иначе по выделенным ячейкам, иначе по выделенным строкам — тем же путем, что пункт меню.
/// </summary>
public partial class TableView
{
    private static readonly (CellHAlign Align, string Text, string Shortcut)[] HorizontalItems =
    [
        (CellHAlign.Left,   "Горизонтально слева",     "table.alignLeft"),
        (CellHAlign.Center, "Горизонтально по центру", "table.alignCenter"),
        (CellHAlign.Right,  "Горизонтально справа",    "table.alignRight"),
    ];

    private static readonly (CellVAlign Align, string Text)[] VerticalItems =
    [
        (CellVAlign.Top,    "Вертикально сверху"),
        (CellVAlign.Center, "Вертикально по центру"),
        (CellVAlign.Bottom, "Вертикально снизу"),
    ];

    // «По умолчанию» снимает оба расположения уровня одной записью.
    private static readonly (StyleAspect Aspect, string? Value)[] ResetAlignment =
        [(StyleAspect.HAlign, null), (StyleAspect.VAlign, null)];

    /// <summary>
    /// Подменю «Форматирование текста» → «Расположение текста». <paramref name="current"/> — что
    /// отметить галочкой (спрашивается на каждое открытие: расположение могли сменить клавишами или в
    /// другом меню); null в группе — галочки в ней нет. <paramref name="apply"/> пишет изменения по
    /// адресату меню.
    /// </summary>
    private void AddTextFormatItems(ContextMenu menu,
                                    Func<(CellHAlign? H, CellVAlign? V)> current,
                                    Action<IReadOnlyList<(StyleAspect Aspect, string? Value)>> apply)
    {
        var placement = new MenuItem { Header = MenuIcons.TextPlacementHeader() };
        MenuIcons.FixSubmenuGap(placement);

        var horizontal = new List<(MenuItem Item, CellHAlign Align, string Text)>();
        foreach (var (align, text, shortcut) in HorizontalItems)
        {
            var item  = new MenuItem { InputGesture = MenuGesture(shortcut) };
            var value = CellAlign.Store(align);
            item.Click += (_, _) => apply([(StyleAspect.HAlign, value)]);
            placement.Items.Add(item);
            horizontal.Add((item, align, text));
        }

        placement.Items.Add(new Separator());

        var vertical = new List<(MenuItem Item, CellVAlign Align, string Text)>();
        foreach (var (align, text) in VerticalItems)
        {
            var item  = new MenuItem();
            var value = CellAlign.Store(align);
            item.Click += (_, _) => apply([(StyleAspect.VAlign, value)]);
            placement.Items.Add(item);
            vertical.Add((item, align, text));
        }

        placement.Items.Add(new Separator());

        var reset = new MenuItem { Header = MenuIcons.PlainHeader("По умолчанию") };
        reset.Click += (_, _) => apply(ResetAlignment);
        placement.Items.Add(reset);

        void Refresh()
        {
            var (h, v) = current();
            foreach (var (item, align, text) in horizontal) item.Header = MenuIcons.ChoiceHeader(text, h == align);
            foreach (var (item, align, text) in vertical)   item.Header = MenuIcons.ChoiceHeader(text, v == align);
        }
        Refresh();
        OnMenuOpening(menu, Refresh);

        var host = new MenuItem { Header = MenuIcons.TextFormattingHeader() };
        MenuIcons.FixSubmenuGap(host);
        host.Items.Add(placement);
        menu.Items.Add(host);
    }

    // ── Адресаты меню ─────────────────────────────────────────────────────────

    /// <summary>Меню ячейки: все выделенные ячейки; отметка — то, что видно в ячейке под кликом.</summary>
    private void AddCellTextFormatItems(ContextMenu menu, TableRowVm row, TableColumnDef def)
    {
        if (!CellAlign.Applies(def.FieldType)) return;
        AddTextFormatItems(menu,
            () =>
            {
                var style = ResolvedStyle(row, def.Key);
                return (style.HAlign ?? CellAlign.DefaultH(def.FieldType), style.VAlign ?? CellVAlign.Center);
            },
            changes => Fire(() => ApplySelectedCellsStyleAsync(changes)));
    }

    /// <summary>Меню строки: строка или все выделенные строки; отметка — заданное строке.</summary>
    private void AddRowTextFormatItems(ContextMenu menu, Func<TableRowVm?> row) =>
        AddTextFormatItems(menu,
            () =>
            {
                var style = RowStyleOf(RecordIdOf(row));
                return (style.HAlign, style.VAlign);
            },
            changes =>
            {
                if (TargetRecordIds(row) is { Count: > 0 } ids) ApplyRowStyle(ids, changes);
            });

    /// <summary>Меню заголовка: столбец или все выделенные столбцы; отметка — расположение столбца,
    /// не задано — правило типа.</summary>
    private void AddColumnTextFormatItems(ContextMenu menu, string key, string fieldType)
    {
        if (!CellAlign.Applies(fieldType)) return;
        AddTextFormatItems(menu,
            () =>
            {
                var style = ColumnStyleOf(key);
                return (style.HAlign ?? CellAlign.DefaultH(fieldType), style.VAlign ?? CellVAlign.Center);
            },
            changes =>
            {
                if (AlignableColumnKeys(TargetColumnKeys(key)) is { Count: > 0 } keys) ApplyColumnStyle(keys, changes);
            });
    }

    // Столбцы, у которых есть что располагать: галочки из выделения выпадают.
    private List<string> AlignableColumnKeys(IEnumerable<string> keys) =>
        [.. keys.Where(k => _colDefs.FirstOrDefault(d => d.Key == k) is { } def && CellAlign.Applies(def.FieldType))];

    /// <summary>Выделенные ячейки — те же, что у цвета и очистки; ячейки галочек пропускаются, когда
    /// меняется расположение текста.</summary>
    private async Task ApplySelectedCellsStyleAsync(IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        if (await SelectedCellsAsync() is not { Count: > 0 } cells) return;
        var alignment = changes.Any(c => c.Aspect is StyleAspect.HAlign or StyleAspect.VAlign);
        var targets   = cells.Where(t => !alignment || CellAlign.Applies(t.Column.FieldType))
                             .Select(t => (t.Row.RecordId, t.Column.Key))
                             .ToList();
        if (targets.Count > 0) ApplyCellStyleChange(targets, changes);
    }

    // ── Клавиши ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ctrl+L / Ctrl+E / Ctrl+R — горизонталь выделенного: выделенные столбцы (по заголовку), иначе
    /// выделенные ячейки (пока фокус в таблице — как у буфера обмена), иначе выделенные строки.
    /// Путь записи — тот же, что у пунктов меню. Нечего выравнивать — клавиша не наша.
    /// </summary>
    private bool HandleAlignShortcut(KeyEventArgs e)
    {
        CellHAlign? align = ShortcutSettings.Matches("table.alignLeft", e)   ? CellHAlign.Left
                          : ShortcutSettings.Matches("table.alignCenter", e) ? CellHAlign.Center
                          : ShortcutSettings.Matches("table.alignRight", e)  ? CellHAlign.Right
                          : null;
        if (align is not { } h) return false;
        IReadOnlyList<(StyleAspect Aspect, string? Value)> changes = [(StyleAspect.HAlign, CellAlign.Store(h))];

        if (_selectedColumnKeys.Count > 0)
        {
            if (AlignableColumnKeys(_selectedColumnKeys) is not { Count: > 0 } keys) return false;
            ApplyColumnStyle(keys, changes);
            return true;
        }

        if (HasCellSelection && IsKeyboardFocusWithin)
        {
            Fire(() => ApplySelectedCellsStyleAsync(changes));
            return true;
        }

        if (SelectedRecordIds() is not { Count: > 0 } ids) return false;
        ApplyRowStyle(ids, changes);
        return true;
    }
}
