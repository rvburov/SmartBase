using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartBase.Logging;
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
/// ручные из <c>column_meta</c> в ручном режиме, строки — <see cref="VirtualRowList"/>. Снимок
/// может нести оценку числа записей (<see cref="CollectionOpenSnapshot.CountIsEstimate"/>) — тогда
/// точный счет идет в фоне и полоса прокрутки уточняется по его приходе, первый кадр его не ждет.
/// <see cref="SetQuery"/> меняет отбор и сортировку: источник строк считается в фоне с отменой
/// (<see cref="RowSourcePlanner"/>), UI-поток не ждет.
/// Этапы 0–1 плана [drawn-grid.md]: без правки, выделения и меню.
/// </summary>
public sealed class GridPanel : Avalonia.Controls.Grid
{
    private const int NumColWidth  = 36;
    private const int PlusColWidth = 36;

    private readonly ScrollBar _vertical   = new() { Orientation = Orientation.Vertical,   AllowAutoHide = false };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, AllowAutoHide = false };
    private readonly Border    _corner     = new();

    private ConstructorRepository?      _repo;
    private int                         _collectionId;
    private CollectionOpenSnapshot?     _snapshot;
    private bool                        _hiddenRecords;
    private int?                        _exactCount;     // точное число записей, когда пришло из фона
    private VirtualRowList?             _rows;
    private List<TableColumnDef>        _defs        = [];
    private Dictionary<string, double>  _widths      = [];
    private HashSet<string>             _hidden      = [];
    private Dictionary<int, RowStyle>   _rowStyles   = [];
    private Dictionary<string, RowStyle> _colStyles  = [];
    private readonly RelationLinksCache _relLinks    = new();
    private CancellationTokenSource?    _query;
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

    /// <summary>Текущий список строк (для стендов и проверок).</summary>
    internal VirtualRowList? Rows => _rows;

    /// <summary>Последний запуск <see cref="SetQuery"/>; завершен — источник строк подключен или отменен.</summary>
    public Task QueryTask { get; private set; } = Task.CompletedTask;

    /// <summary>Открыть коллекцию по снимку открытия (прочитан заранее, UI-поток в базу не ходит).</summary>
    public void Load(ConstructorRepository repo, int collectionId, CollectionOpenSnapshot snapshot)
    {
        var (colResize, _, _) = snapshot.UiSettings;

        _repo          = repo;
        _collectionId  = collectionId;
        _snapshot      = snapshot;
        _exactCount    = snapshot.CountIsEstimate ? null : snapshot.VisibleRecordCount;
        _defs          = BuildDefs(snapshot);
        _hidden        = snapshot.ColumnDisplay.Where(kv => !kv.Value.Visible).Select(kv => kv.Key).ToHashSet();
        _widths        = colResize ? snapshot.ColumnWidths : [];
        _rowStyles     = snapshot.RowStyles;
        _colStyles     = snapshot.ColumnStyles;
        _hiddenRecords = snapshot.RecordDisplay.Any(kv => !kv.Value.Visible);

        _query?.Cancel();
        _query = null;
        AttachSource(RowSource.Plain);
    }

    /// <summary>Сменить отбор и сортировку: источник строк считается в фоне (<see cref="RowSourcePlanner"/>),
    /// прежний незавершенный запрос отменяется, устаревший результат не подключается. Без фильтров и
    /// сортировки по индексируемому типу подключение мгновенное.</summary>
    public Task SetQuery(IReadOnlyDictionary<string, HashSet<string>> filters, string? sortKey, bool sortAsc)
    {
        if (_repo is null || _snapshot is null) return Task.CompletedTask;
        _query?.Cancel();
        var cts = _query = new CancellationTokenSource();
        return QueryTask = ApplyQueryAsync(filters, sortKey, sortAsc, cts.Token);
    }

    private async Task ApplyQueryAsync(IReadOnlyDictionary<string, HashSet<string>> filters, string? sortKey, bool sortAsc,
                                       CancellationToken ct)
    {
        RowSource source;
        try
        {
            source = await RowSourcePlanner.BuildAsync(_repo!, _collectionId, _snapshot!.Fields, filters, sortKey, sortAsc,
                                                       _hiddenRecords, ct);
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        // Продолжение после фона — на UI-поток явно (стенды без контекста синхронизации).
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) AttachSource(source);
        else await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { if (!ct.IsCancellationRequested) AttachSource(source); });
    }

    // Новый список строк по источнику; число записей — точное, если уже известно, иначе оценка
    // из снимка с уточнением в фоне (список id знает свою длину сам).
    private void AttachSource(RowSource source)
    {
        var snapshot = _snapshot!;
        _relLinks.Clear();

        var idList   = source as IdListRowSource;
        var known    = idList?.Ids.Count ?? _exactCount ?? snapshot.VisibleRecordCount;
        var estimate = idList is null && _exactCount is null && snapshot.CountIsEstimate;
        var rows = new VirtualRowList(_repo!, _collectionId, snapshot.Fields,
                                      excludeHidden: _hiddenRecords, relLinks: _relLinks,
                                      knownCount: known, source: source, countIsEstimate: estimate);
        _rows = rows;
        if (estimate) _ = RefineCountAsync(rows);

        Surface.Attach(rows, BuildLayout(),
                       rid => _rowStyles.GetValueOrDefault(rid),
                       key => _colStyles.GetValueOrDefault(key),
                       _relLinks);
        Surface.OffsetX = 0;
        Surface.OffsetY = 0;
        SyncBars();
    }

    // Точный счет записей в фоне: полоса прокрутки уточняется по приходе, первый кадр не ждал.
    private async Task RefineCountAsync(VirtualRowList rows)
    {
        try
        {
            var n = await _repo!.CountRecordsAsync(_collectionId, _hiddenRecords);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _exactCount = n;
                if (ReferenceEquals(_rows, rows)) rows.SetCount(n);
            });
        }
        catch (Exception ex)
        {
            DbErrors.HandleRead(ex, _repo?.DbPath);
        }
    }

    private ColumnLayout BuildLayout() =>
        ColumnLayout.Build(_defs, _widths, _hidden, Surface.Bounds.Width,
                           ConstructorSettings.ColWidth, 1.0 / LineMetrics.EffectiveScale(this));

    private void SyncBars()
    {
        if (DebugSkipBars) return;
        var t0 = GridTimings.Start();
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
            GridTimings.Stop(t0, "полосы прокрутки (UI)");
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
