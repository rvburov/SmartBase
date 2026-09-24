using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartBase.Controls.Behaviors;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Views.ConstructorDb.DrawnGrid;

/// <summary>
/// Рисованная сетка с полосами прокрутки: <see cref="GridSurface"/> в левом верхнем углу,
/// вертикальная полоса справа, горизонтальная снизу. Полосы стандартные (тема приложения),
/// протяженности считаются из раскладки колонок и числа строк — сумма высот 10 млн строк это
/// обычное число, никакой виртуализации полос не нужно.
///
/// <see cref="Load"/> открывает коллекцию по снимку так же, как <c>TableView</c>: колонки из полей
/// и системного «Названия» в сохраненном порядке, скрытые в «Отображении» не строятся, ширины —
/// ручные из <c>column_meta</c> в ручном режиме, строки — тот же <see cref="VirtualRowList"/>.
/// Прототип этапа 0 плана [drawn-grid.md]: без правки, выделения, меню и фильтров.
/// </summary>
public sealed class GridPanel : Avalonia.Controls.Grid
{
    private const int NumColWidth  = 36;
    private const int PlusColWidth = 36;

    private readonly ScrollBar _vertical   = new() { Orientation = Orientation.Vertical,   AllowAutoHide = false };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false };
    private readonly Border    _corner     = new();

    private VirtualRowList?             _rows;
    private List<TableColumnDef>        _defs        = [];
    private Dictionary<string, double>  _widths      = [];
    private HashSet<string>             _hidden      = [];
    private Dictionary<int, RowStyle>   _rowStyles   = [];
    private Dictionary<string, RowStyle> _colStyles  = [];
    private readonly RelationLinksCache _relLinks    = new();
    private bool _syncingBars;

    /// <summary>Отладочный выключатель для стендов: не трогать полосы прокрутки при сдвиге.</summary>
    public static bool DebugSkipBars { get; set; }

    /// <summary>Отладочный делитель диапазона вертикальной полосы для стендов (1 — пиксели).</summary>
    public static double DebugBarDivisor { get; set; } = 1;

    public GridSurface Surface { get; } = new();

    public GridPanel()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions    = new RowDefinitions("*,Auto");

        SetColumn(Surface, 0);      SetRow(Surface, 0);
        SetColumn(_vertical, 1);    SetRow(_vertical, 0);
        SetColumn(_horizontal, 0);  SetRow(_horizontal, 1);
        SetColumn(_corner, 1);      SetRow(_corner, 1);

        Children.Add(Surface);
        Children.Add(_vertical);
        Children.Add(_horizontal);
        Children.Add(_corner);

        Surface.ExtentChanged += (_, _) => SyncBars();
        Surface.OffsetChanged += (_, _) => SyncBars();
        Surface.SizeChanged   += (_, _) => { if (_rows is not null) Surface.SetColumns(BuildLayout()); };

        _vertical.Scroll   += (_, e) => { if (!_syncingBars) Surface.OffsetY = e.NewValue * DebugBarDivisor; };
        _horizontal.Scroll += (_, e) => { if (!_syncingBars) Surface.OffsetX = e.NewValue; };

        AttachedToVisualTree += (_, _) =>
        {
            if (this.TryFindResource("BrushScrollbarTrack", out var b) && b is IBrush brush)
                _corner.Background = brush;
        };
    }

    public IReadOnlyList<TableColumnDef> Columns => _defs;
    public int RowCount => _rows?.Count ?? 0;

    /// <summary>Открыть коллекцию по снимку открытия (прочитан заранее, UI-поток в базу не ходит).</summary>
    public void Load(ConstructorRepository repo, int collectionId, CollectionOpenSnapshot snapshot)
    {
        var (colResize, _, _) = snapshot.UiSettings;

        _defs      = BuildDefs(snapshot);
        _hidden    = snapshot.ColumnDisplay.Where(kv => !kv.Value.Visible).Select(kv => kv.Key).ToHashSet();
        _widths    = colResize ? snapshot.ColumnWidths : [];
        _rowStyles = snapshot.RowStyles;
        _colStyles = snapshot.ColumnStyles;
        _relLinks.Clear();

        var hiddenRecords = snapshot.RecordDisplay.Any(kv => !kv.Value.Visible);
        _rows = new VirtualRowList(repo, collectionId, snapshot.Fields,
                                   excludeHidden: hiddenRecords, relLinks: _relLinks,
                                   knownCount: snapshot.VisibleRecordCount);

        Surface.Attach(_rows, BuildLayout(),
                       rid => _rowStyles.GetValueOrDefault(rid),
                       key => _colStyles.GetValueOrDefault(key),
                       _relLinks);
        Surface.OffsetX = 0;
        Surface.OffsetY = 0;
        SyncBars();
    }

    private ColumnLayout BuildLayout() =>
        ColumnLayout.Build(_defs, _widths, _hidden, Surface.Bounds.Width,
                           ConstructorSettings.ColWidth, 1.0 / LineMetrics.EffectiveScale(this));

    private void SyncBars()
    {
        if (DebugSkipBars) return;
        _syncingBars = true;
        try
        {
            var maxY = Math.Max(0, Surface.ExtentHeight - Surface.ViewportHeight);
            var maxX = Math.Max(0, Surface.ExtentWidth  - Surface.ViewportWidth);

            var d = DebugBarDivisor;
            _vertical.Minimum      = 0;
            _vertical.Maximum      = maxY / d;
            _vertical.ViewportSize = Surface.ViewportHeight / d;
            _vertical.SmallChange  = Surface.RowPitch / d;
            _vertical.LargeChange  = Math.Max(Surface.RowPitch, Surface.ViewportHeight) / d;
            _vertical.Value        = Surface.OffsetY / d;

            _horizontal.Minimum      = 0;
            _horizontal.Maximum      = maxX;
            _horizontal.ViewportSize = Surface.ViewportWidth;
            _horizontal.SmallChange  = 48;
            _horizontal.LargeChange  = Math.Max(48, Surface.ViewportWidth);
            _horizontal.Value        = Surface.OffsetX;
        }
        finally
        {
            _syncingBars = false;
        }
    }

    // Колонки — как TableView.BuildColDefs, без меню и обработчиков: «№», движимые в сохраненном
    // порядке (Название = 1000, поля = 3000 + Position), «+».
    private List<TableColumnDef> BuildDefs(CollectionOpenSnapshot s)
    {
        var titleName = s.SystemNames.TryGetValue(TableRowVm.TitleKey, out var n) && !string.IsNullOrWhiteSpace(n)
            ? n
            : "Название";

        var movable = new List<(TableColumnDef Def, int Pos)>
        {
            (new TableColumnDef { Key = TableRowVm.TitleKey, Header = titleName, FieldType = "text",
                                  IsStarWidth = true, MinWidth = ConstructorSettings.ColWidth },
             s.ColumnOrder.GetValueOrDefault(TableRowVm.TitleKey, 1000)),
        };

        foreach (var f in s.Fields)
        {
            var fid = f.Id!.Value;
            var key = fid.ToString();
            movable.Add((new TableColumnDef
            {
                Key            = key,
                Header         = f.Name,
                FieldType      = f.FieldType,
                IsStarWidth    = true,
                MinWidth       = ConstructorSettings.ColWidth,
                IsReadOnly     = f.FieldType is "checkbox" or "file" || FieldTypeInfo.IsComputed(f.FieldType),
                Separator      = MultiSelectValue.SeparatorFromConfig(f.ConfigJson),
                NumberFormat   = NumberFormatValue.FromConfig(f.ConfigJson),
                FileAutoFolder = FileAutoRule.FromConfig(f.ConfigJson)?.Folder ?? "",
                Relation       = f.FieldType == "relation" ? RelationConfig.FromConfig(f.ConfigJson) : null,
                Tag            = fid,
            }, s.ColumnOrder.GetValueOrDefault(key, 3000 + f.Position)));
        }

        var defs = new List<TableColumnDef>(movable.Count + 2)
        {
            new() { Key = TableColumnDef.NumKey, Header = "№", IsStarWidth = false,
                    FixedWidth = NumColumnWidth(s.VisibleRecordCount), CanResize = false, IsReadOnly = true },
        };
        defs.AddRange(movable.OrderBy(m => m.Pos).Select(m => m.Def));
        defs.Add(new() { Key = TableColumnDef.AddPropKey, Header = "+", IsStarWidth = false,
                         FixedWidth = PlusColWidth, CanResize = false, IsReadOnly = true, MaxWidth = PlusColWidth });
        return defs;
    }

    // Ширина «№»: под самый длинный номер (число записей) с полями по 8 — как NumColumnWidth.
    private double NumColumnWidth(int recordCount)
    {
        var text     = Math.Max(1, recordCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fontSize = this.TryFindResource("FontSizeBase", out var fs) && fs is double d ? d : 12;
        var family   = this.TryFindResource("FontFamilyBase", out var ff) && ff is FontFamily fam ? fam : FontFamily.Default;
        var measured = new TextLayout(text, new Typeface(family), fontSize, Brushes.Black).Width;
        return Math.Max(NumColWidth, Math.Ceiling(measured) + 16);
    }
}
