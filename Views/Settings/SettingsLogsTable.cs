using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Logging;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using LogRow = SmartBase.Logging.LogReader.LogRow;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.Settings;

/// <summary>
/// «Диагностика» — таблица журнала logs.db (Serilog) с панелью деталей выбранной строки.
/// Раздел «Настройки» (единый файл на приложение — системная, а не пользовательская вещь,
/// поэтому здесь, а не в «Профиле»). Тот же Excel-фильтр с сортировкой в заголовке колонки,
/// что и в «Истории изменений»; данные читаются через <see cref="LogReader"/> (raw SQL по
/// logs.db). Строки подгружаются keyset-страницами по 200 при прокрутке к концу.
/// </summary>
public partial class SettingsView
{
    private const int LogsPageSize = 200;

    // Колонки лога: ключ (как в LogQuery) и заголовок. Ширина звёздная — окно делится поровну.
    private static readonly (string Key, string Header)[] LogsColumns =
    [
        ("date",      "Время"),
        ("level",     "Уровень"),
        ("message",   "Сообщение"),
        ("exception", "Ошибка"),
    ];

    private readonly LogReader _logReader = new(AppSettings.LogsDbPath);
    private bool _logsLoading;
    private bool _logsEnd;
    // Поколение запроса: смена фильтра во время чтения страницы обесценивает результат.
    private int _logsGen;
    private readonly AvaloniaList<LogRow> _logsRows = [];

    private readonly Dictionary<string, HashSet<string>> _logsFilters = [];
    private string? _logsSortKey;
    private bool _logsSortAsc;

    private readonly Dictionary<string, Path> _logsChevrons = [];
    private Flyout? _logsFlyout;
    private ScrollBar? _logsScroll;

    // ── Панель деталей выбранной строки ─────────────────────────────────────────
    // Поля-карточки в панели деталей: ключ (как в LogsColumns), подпись, иконка типа.
    private static readonly (string Key, string Name, string IconKey)[] LogsDetailFields =
    [
        ("date",      "Время",     "IconClock"),
        ("level",     "Уровень",   "IconFieldStatus"),
        ("message",   "Сообщение", "IconMessageWarning"),
        ("exception", "Ошибка",    "IconTriangleAlert"),
    ];

    private bool _logsSelectionHooked;
    private string _logsDetailFieldKey = "message";   // какое поле открыто снизу (запоминается между строками)
    private Border? _logsDetailSelectedCard;
    private readonly Dictionary<string, Border> _logsDetailCards = [];

    // Ширина панели деталей (px) — тянется ручкой LogsDetailResizeGrip; помнится в пределах сессии.
    private double _logsDetailWidth = 440;
    private const double LogsDetailWidthMin = 320;
    private const double LogsDetailWidthMax = 900;

    // Общая ширина колонки названий у всех карточек — тянется ручкой ⋮ (как в записи).
    private readonly List<ColumnDefinition> _logsNameCols = [];
    private double _logsNameColWidth = 120;
    private const double LogsNameColMin = 70;

    // ── Панель ────────────────────────────────────────────────────────────────

    private void RebuildLogsPanel()
    {
        if (LogsGrid.Columns.Count == 0)
        {
            BuildLogsColumns();
            LogsGrid.ItemsSource = _logsRows;
        }
        if (!_logsSelectionHooked)
        {
            LogsGrid.SelectionChanged += OnLogsSelectionChanged;
            LogsDetailCloseBtn.Click  += (_, _) => CloseLogsDetail();
            LogsDetailCopyBtn.Click   += (_, _) => CopyLogsDetailField();
            LogsPropsResizeGrip.PointerPressed  += OnLogsGripPressed;
            LogsPropsResizeGrip.PointerMoved    += OnLogsGripMoved;
            LogsPropsResizeGrip.PointerReleased += OnLogsGripReleased;
            LogsDetailResizeGrip.PointerPressed  += OnLogsDetailGripPressed;
            LogsDetailResizeGrip.PointerMoved    += OnLogsDetailGripMoved;
            LogsDetailResizeGrip.PointerReleased += OnLogsDetailGripReleased;
            _logsSelectionHooked = true;
        }
        UpdateLogsChevrons();
        ReloadLogsRows();
    }

    // Перечитать строки с текущими фильтрами/сортировкой (скролл возвращается к началу).
    private void ReloadLogsRows()
    {
        // Выбранная строка после смены фильтра могла исчезнуть — закрываем детали.
        CloseLogsDetail();
        _logsGen++;
        _logsRows.Clear();
        _logsEnd = false;
        LogsEmptyText.IsVisible = false;
        _ = LoadMoreLogsAsync();
    }

    private async Task LoadMoreLogsAsync()
    {
        if (_logsLoading || _logsEnd) return;
        _logsLoading = true;
        var gen = _logsGen;
        try
        {
            var last  = _logsRows.Count > 0 ? _logsRows[^1] : null;
            var query = new LogReader.LogQuery
            {
                Filters = _logsFilters.ToDictionary(
                    kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value),
                SortColumn = _logsSortKey,
                SortAsc    = _logsSortAsc,
                AfterId    = last?.Id,
                AfterValue = last is null || _logsSortKey is null
                    ? null
                    : LogsRawValue(_logsSortKey, last),
            };
            var page = await _logReader.GetPageAsync(query, LogsPageSize);

            // Пока читали страницу, могли переключить фильтр — результат уже не наш.
            if (gen != _logsGen) return;

            _logsRows.AddRange(page);
            if (page.Count < LogsPageSize) _logsEnd = true;
            LogsEmptyText.IsVisible = _logsRows.Count == 0;

            Dispatcher.UIThread.Post(EnsureLogsScrollHook, DispatcherPriority.Background);
        }
        finally { _logsLoading = false; }
    }

    // Следующая страница — при прокрутке к концу (порог ~300px до низа).
    private void EnsureLogsScrollHook()
    {
        if (_logsScroll is not null) return;
        _logsScroll = LogsGrid.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(s => s.Orientation == Orientation.Vertical);
        if (_logsScroll is null) return;

        _logsScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            if (_logsScroll.Maximum > 0 && _logsScroll.Value >= _logsScroll.Maximum - 300)
                _ = LoadMoreLogsAsync();
        };
    }

    // Сырое значение колонки строки лога — для фильтров и keyset-курсора сортировки.
    private static string LogsRawValue(string key, LogRow row) => key switch
    {
        "level"     => row.Level,
        "message"   => row.Message,
        "exception" => row.Exception ?? "",
        "date"      => row.Timestamp.Length >= 10 ? row.Timestamp[..10] : row.Timestamp,
        _           => "",
    };

    // ── Выгрузка в CSV ────────────────────────────────────────────────────────
    //
    // Кнопка в навбаре раздела. Выгружается ровно то, что показано: те же фильтры
    // и та же сортировка; строки читаются keyset-страницами уже в фоне (ReadCsvRows),
    // поэтому объём лога ограничивает только диск.

    private async Task ExportLogsToCsv()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var query = new LogReader.LogQuery
        {
            Filters = _logsFilters.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value),
            SortColumn = _logsSortKey,
            SortAsc    = _logsSortAsc,
        };
        var total = await _logReader.CountAsync(query);

        await CsvExportRunner.Run(owner, new CsvExportRunner.Job(
            Title:           "Выгрузка диагностики",
            What:            "Диагностика",
            TotalRows:       total,
            Filtered:        _logsFilters.Count > 0,
            DefaultFileName: CsvExportRunner.FileName("Диагностика", DateTime.Now),
            Headers:         LogReader.CsvHeaders,
            Rows:            ct => _logReader.ReadCsvRows(query, ct)));
    }

    // ── Колонки и шапка ───────────────────────────────────────────────────────

    private void BuildLogsColumns()
    {
        _logsChevrons.Clear();
        for (var i = 0; i < LogsColumns.Length; i++)
        {
            var def = LogsColumns[i];
            // Последняя колонка («Ошибка») не рисует правую линию заголовка: её правый край
            // и так очерчен краем таблицы/скроллом, а вторая линия давала «двойную/толстую».
            // Так же поступает таблица конструктора (WrapHeaderContent addRightLine=false
            // для последней колонки «+»).
            var addRightLine = i < LogsColumns.Length - 1;
            // Иконка типа колонки — та же, что у поля в панели деталей (LogsDetailFields).
            var iconKey = LogsDetailFields.First(f => f.Key == def.Key).IconKey;
            LogsGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header       = BuildLogsHeader(def.Key, def.Header, iconKey, addRightLine),
                Width        = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth     = 110,
                CellTemplate = BuildLogsCellTemplate(def.Key),
            });
        }
    }

    private Control BuildLogsHeader(string key, string header, string iconKey, bool addRightLine)
    {
        var text = new TextBlock
        {
            Text              = header,
            FontSize          = (double)this.FindResource("FontSizeBase")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
        };

        // Сетка [иконка типа][текст][шеврон] — 1-в-1 как шапка таблицы конструктора
        // (TableView.MakeTextHeader): иконка IconSizeSmall с отступом 6px справа.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var icon = BuildLogsHeaderIcon(iconKey);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var chevron = BuildLogsChevron(key);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var border = new Border
        {
            Background          = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
            Padding             = new Thickness(6, 0),
            Child               = grid,
        };
        border.Classes.Add("colHeader");

        var host = new Grid
        {
            Background          = Brushes.Transparent,
            ColumnDefinitions   = new ColumnDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(border, 0);
        host.Children.Add(border);

        if (addRightLine)
        {
            var line = new Rectangle
            {
                Fill              = (IBrush)this.FindResource("BrushSeparator")!,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            line.Bind(Rectangle.WidthProperty,              line.GetResourceObservable("SeparatorThickness"));
            line.Bind(Layoutable.UseLayoutRoundingProperty, line.GetResourceObservable("LineUseLayoutRounding"));
            RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
            Grid.SetColumn(line, 1);
            host.Children.Add(line);
        }

        return host;
    }

    // Иконка типа в шапке колонки — как в таблице конструктора (TableView.BuildTypeIcon):
    // Viewbox IconSizeSmall, отступ 6px справа, обводка BrushSecondaryText / IconStrokeSmall.
    private Control BuildLogsHeaderIcon(string iconKey) => new Viewbox
    {
        Width             = LogsIconSize("IconSizeSmall"),
        Height            = LogsIconSize("IconSizeSmall"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin            = new Thickness(0, 0, 6, 0),
        Child = new Canvas
        {
            Width  = 24,
            Height = 24,
            Children =
            {
                new Path
                {
                    Data            = LogsIcon(iconKey),
                    Stroke          = (IBrush)this.FindResource("BrushSecondaryText")!,
                    StrokeThickness = LogsIconSize("IconStrokeSmall"),
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    private Control BuildLogsChevron(string key)
    {
        var path = new Path
        {
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        _logsChevrons[key] = path;
        ApplyLogsChevronState(key, path);

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);

        var btn = new Button
        {
            Width             = 20,
            Height            = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Content           = new Viewbox
            {
                Width  = LogsIconSize("IconSizeSmall"),
                Height = LogsIconSize("IconSizeSmall"),
                Child  = canvas,
            },
        };
        btn.Classes.Add("icon");
        btn.Classes.Add("filterChevron");
        ToolTip.SetTip(btn, "Фильтр и сортировка");
        btn.Click += (_, _) => ShowLogsFilterFlyout(btn, key);
        return btn;
    }

    private void ApplyLogsChevronState(string key, Path path)
    {
        var active  = _logsFilters.ContainsKey(key) || _logsSortKey == key;
        path.Data   = active ? LogsIcon("IconFilter") : LogsIcon("IconChevronDown");
        path.Stroke = active
            ? (IBrush)this.FindResource("BrushAccent")!
            : (IBrush)this.FindResource("BrushSecondaryText")!;
    }

    private void UpdateLogsChevrons()
    {
        foreach (var (key, path) in _logsChevrons)
            ApplyLogsChevronState(key, path);
    }

    private static Geometry LogsIcon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    private static double LogsIconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 16;

    // ── Ячейки ────────────────────────────────────────────────────────────────

    private IDataTemplate BuildLogsCellTemplate(string key) =>
        new FuncDataTemplate<LogRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            // Полный текст сообщения/ошибки показывает панель деталей (клик по строке),
            // поэтому тултипа с полным текстом в ячейке больше нет.
            return new TextBlock
            {
                Text              = LogsCellText(key, row),
                Margin            = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
        });

    private static string LogsCellText(string key, LogRow row) => key switch
    {
        "date"      => FormatLogsTs(row.Timestamp),
        "level"     => LogsLevelLabel(row.Level),
        "message"   => row.Message,
        "exception" => LogsExceptionSummary(row.Exception),
        _           => "",
    };

    // Подписи — из общего LogLabels: тот же текст в таблице и в выгрузке CSV.
    private static string LogsLevelLabel(string level) => LogLabels.LevelLabel(level);

    // Краткое представление исключения в ячейке: первая непустая строка (тип + текст).
    private static string LogsExceptionSummary(string? exception)
    {
        if (string.IsNullOrWhiteSpace(exception)) return "";
        foreach (var line in exception.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }
        return exception.Trim();
    }

    // UTC-текст Serilog → локальное «дд.MM.гггг ЧЧ:мм:сс».
    private static string FormatLogsTs(string ts) => LogLabels.FormatTs(ts);

    // Значение в окне фильтра: сырое → отображаемое (те же подписи, что в ячейках).
    private static string LogsDisplayValue(string key, string raw)
    {
        if (raw.Length == 0) return "(Пусто)";
        return key switch
        {
            "level"     => LogsLevelLabel(raw),
            "exception" => LogsExceptionSummary(raw),
            "date"      => DateTime.TryParseExact(raw, "yyyy-MM-dd", null,
                               System.Globalization.DateTimeStyles.None, out var d)
                           ? d.ToString("dd.MM.yyyy")
                           : raw,
            _           => raw,
        };
    }

    // ── Фильтр и сортировка колонок (Excel-стиль, зеркало ProfileHistoryTable) ──

    private void ShowLogsFilterFlyout(Control anchor, string key)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        flyout.Content = WindowZoom.WrapPopupContent(BuildLogsFilterPanel(key, () => flyout.Hide()));
        flyout.Closed += (_, _) => { if (ReferenceEquals(_logsFlyout, flyout)) _logsFlyout = null; };
        _logsFlyout = flyout;
        flyout.ShowAt(anchor);
    }

    private Control BuildLogsFilterPanel(string key, Action close)
    {
        var secondary = (IBrush)this.FindResource("BrushSecondaryText")!;
        var fontBase  = (double)this.FindResource("FontSizeBase")!;
        var radius    = (CornerRadius)this.FindResource("RadiusBase")!;
        var family    = this.TryFindResource("FontFamilyBase", out var f) ? f as FontFamily : null;
        // Ширины строк и закреплённый справа счётчик — см. FilterValueMetrics.
        var metrics   = new FilterValueMetrics();

        var panel = new StackPanel
        {
            Width   = 240,
            Margin  = new Thickness(12),
            Spacing = 2,
            Cursor  = new Cursor(StandardCursorType.Arrow),
        };

        var baseCounts = new List<(string Value, int Count)>();
        var domain     = new HashSet<string>();
        var loaded     = false;

        var (ascLabel, descLabel) = key == "date"
            ? ("Сначала старые", "Сначала новые")
            : ("От А до Я", "От Я до А");
        bool? pendingSort = _logsSortKey == key ? _logsSortAsc : null;

        var sortSection = new StackPanel { Spacing = 2 };
        panel.Children.Add(sortSection);

        Action rebuildListRef = () => { };

        List<(string Raw, int Count)> counts = [];
        void ReloadCounts()
        {
            bool? asc = pendingSort ?? (_logsSortKey == key ? _logsSortAsc : (bool?)null);
            counts = SortLogsValueCounts(baseCounts, key, asc);
        }
        ReloadCounts();

        void RenderSort()
        {
            sortSection.Children.Clear();
            sortSection.Children.Add(MakeLogsFlatItem(ascLabel,
                () => { pendingSort = true;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                LogsIcon("IconSortAz"), active: pendingSort == true));
            sortSection.Children.Add(MakeLogsFlatItem(descLabel,
                () => { pendingSort = false; RenderSort(); ReloadCounts(); rebuildListRef(); },
                LogsIcon("IconSortZa"), active: pendingSort == false));
            sortSection.Children.Add(MakeLogsFlatItem("Снять сортировку",
                () => { pendingSort = null;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                LogsIcon("IconClose")));
        }
        RenderSort();

        panel.Children.Add(MakeLogsSeparator());

        var search = new TextBox
        {
            Watermark    = "Поиск значений...",
            Height       = 30,
            CornerRadius = radius,
            Background   = Brushes.White,
            Margin       = new Thickness(0, 0, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(search);

        var checkedSet       = new HashSet<string>();
        var syncingSelectAll = false;

        var selectAll = new CheckBox
        {
            Content   = "(Выделить всё)",
            FontSize  = fontBase,
            IsChecked = !_logsFilters.ContainsKey(key),
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        var shown   = new AvaloniaList<LogsFilterValueVm>();
        var vmCache = new Dictionary<string, LogsFilterValueVm>();
        LogsFilterValueVm Vm(string raw, int count) =>
            vmCache.TryGetValue(raw, out var v) ? v
                : vmCache[raw] = new LogsFilterValueVm(raw, LogsDisplayValue(key, raw), count,
                                                       checkedSet, () => SyncSelectAll());

        var valuesList = new ListBox
        {
            // Высоты нет: список занимает остаток строки карточки фиксированной высоты и
            // сам ужимается, когда снизу появляется горизонтальная полоса.
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ItemsSource     = shown,
            ItemTemplate    = BuildLogsFilterValueTemplate(fontBase, secondary, metrics),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        void FlatRow(params string[] pseudo) =>
            valuesList.Styles.Add(new Style(x =>
            {
                Selector s = x.OfType<ListBoxItem>();
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

        // «Выделить всё» — закреплённая шапка списка; полоса прокрутки стоит на её уровне
        // (отдельной колонкой), как вертикальная полоса таблицы стоит на уровне шапки колонок.
        // gap=2: вместе с верхним отступом первой строки (Padding 0,2) даёт под шапкой
        // ровно такой же просвет, как между значениями.
        var valuesBox = ScrollBarLink.HeaderedList(selectAll, valuesList, gap: 2,
                                                   scroller: metrics.Track);

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
            Background   = Brushes.White,
            BorderBrush  = (IBrush)this.FindResource("BrushSeparator")!,
            CornerRadius = radius,
            // Padding НУЛЕВОЙ: полосы прокрутки списка прилегают ко всем краям карточки без
            // белого зазора, а воздух вокруг содержимого задаёт ScrollBarLink.HeaderedList.
            // ClipToBounds — квадратные углы полос срезаются скруглением карточки.
            Padding      = new Thickness(0),
            ClipToBounds = true,
            Height       = 232,
            Child        = loadingText,
        };
        valuesHost.Bind(Border.BorderThicknessProperty, valuesHost.GetResourceObservable("BorderLine"));
        panel.Children.Add(valuesHost);

        List<(string Raw, int Count)> Matched()
        {
            var q = search.Text?.Trim() ?? "";
            return q.Length == 0
                ? counts
                : counts.Where(c => LogsDisplayValue(key, c.Raw)
                    .Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        void RebuildList()
        {
            shown.Clear();
            shown.AddRange(Matched().Select(c => Vm(c.Raw, c.Count)));
            // Ширина строки фиксируется по текущему набору — иначе экстент горизонтальной
            // прокрутки считался бы только по видимым строкам и полоса прыгала бы.
            metrics.Measure(shown.Select(v => v.Display), shown.Select(v => v.CountText),
                            fontBase, family);
        }
        rebuildListRef = RebuildList;

        void SyncSelectAll()
        {
            syncingSelectAll    = true;
            selectAll.IsChecked = Matched().All(c => checkedSet.Contains(c.Raw));
            syncingSelectAll    = false;
        }

        search.TextChanged += (_, _) => { RebuildList(); SyncSelectAll(); };
        selectAll.IsCheckedChanged += (_, _) =>
        {
            if (syncingSelectAll) return;
            var on = selectAll.IsChecked == true;
            foreach (var (raw, _) in Matched())
            {
                if (on) checkedSet.Add(raw);
                else    checkedSet.Remove(raw);
            }
            RebuildList();
        };

        panel.Children.Add(MakeLogsSeparator());

        var okBtn = new Button
        {
            Content             = "Готово",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        okBtn.Classes.Add("dialog");
        okBtn.Click += (_, _) =>
        {
            if (pendingSort is bool asc) { _logsSortKey = key; _logsSortAsc = asc; }
            else if (_logsSortKey == key) _logsSortKey = null;

            if (loaded)
            {
                var hasSearch = !string.IsNullOrWhiteSpace(search.Text);
                var effective = hasSearch
                    ? Matched().Select(c => c.Raw).Where(checkedSet.Contains).ToHashSet()
                    : [.. checkedSet];

                if (domain.SetEquals(effective)) _logsFilters.Remove(key);
                else _logsFilters[key] = effective;
            }

            UpdateLogsChevrons();
            ReloadLogsRows();
            close();
        };
        panel.Children.Add(okBtn);

        // Фоновая загрузка значений (SQL-агрегат по logs.db, отдельное подключение).
        _logReader.GetCountsAsync(key).ContinueWith(t =>
        {
            if (t.IsFaulted || t.IsCanceled) return;
            var data = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                baseCounts.Clear();
                baseCounts.AddRange(data);
                domain.Clear();
                foreach (var c in data) domain.Add(c.Value);

                checkedSet.Clear();
                if (_logsFilters.TryGetValue(key, out var ff)) checkedSet.UnionWith(ff);
                else checkedSet.UnionWith(domain);

                ReloadCounts();
                // «Загрузка…» → список значений; пустой список показывает надпись
                // «Список пуст» / «Ничего не найдено» вместо голой белой карточки.
                valuesHost.Child = Controls.Components.EmptyListHint.Wrap(
                    valuesList, search, secondary, fontBase, content: valuesBox);
                RebuildList();
                SyncSelectAll();
                loaded = true;
            });
        });

        return panel;
    }

    // Порядок значений в окне фильтра: выбранная сортировка; без неё — дата новыми
    // сверху, остальные по подписи, «(Пусто)» в конце.
    private static List<(string Raw, int Count)> SortLogsValueCounts(
        List<(string Value, int Count)> src, string key, bool? asc)
    {
        var items = src.Select(c => (Raw: c.Value, c.Count)).ToList();
        var empty = items.Where(c => c.Raw.Length == 0).ToList();
        items = items.Where(c => c.Raw.Length > 0).ToList();

        if (key == "date")
        {
            items = asc == true
                ? items.OrderBy(c => c.Raw, StringComparer.Ordinal).ToList()
                : items.OrderByDescending(c => c.Raw, StringComparer.Ordinal).ToList();
        }
        else
        {
            items = asc == false
                ? items.OrderByDescending(c => LogsDisplayValue(key, c.Raw),
                                          StringComparer.CurrentCultureIgnoreCase).ToList()
                : items.OrderBy(c => LogsDisplayValue(key, c.Raw),
                                StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        items.AddRange(empty);
        return items;
    }

    private Viewbox MakeLogsStrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width  = LogsIconSize("IconSizeMedium"),
        Height = LogsIconSize("IconSizeMedium"),
        VerticalAlignment = VerticalAlignment.Center,
        Child  = new Canvas
        {
            Width  = 24,
            Height = 24,
            Children =
            {
                new Path
                {
                    Data            = data,
                    Stroke          = stroke,
                    StrokeThickness = LogsIconSize("IconStrokeMedium"),
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    private Button MakeLogsFlatItem(string text, Action onClick, Geometry? icon = null, bool active = false)
    {
        var fg = (IBrush)this.FindResource(active ? "BrushAccent" : "BrushPrimaryText")!;

        object content = text;
        if (icon is not null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var iconBox = MakeLogsStrokeIcon(icon, fg);
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
                var check = MakeLogsStrokeIcon(LogsIcon("IconCheck"), fg);
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

    private Border MakeLogsSeparator()
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

    private static IDataTemplate BuildLogsFilterValueTemplate(double fontBase, IBrush secondary,
                                                              FilterValueMetrics metrics) =>
        new FuncDataTemplate<LogsFilterValueVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(LogsFilterValueVm.Display)));
            // Ширина ВСЕХ строк одинаковая (по самому широкому значению) — иначе экстент
            // горизонтальной прокрутки считался бы по видимым строкам и полоса дёргалась.
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
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(LogsFilterValueVm.Checked)) { Mode = BindingMode.TwoWay });

            var cnt = new TextBlock { Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(LogsFilterValueVm.CountText)));

            // Счётчик ЗАКРЕПЛЁН у правого края видимой области: сдвиг компенсирует
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

            // Прозрачное МЕСТО под счётчик — одинаковой ширины во всех строках, чтобы от
            // количества цифр не зависела ширина строки, а с ней и экстент прокрутки.
            var cntSlot = new Border { Child = cntHost };
            cntSlot.Bind(Layoutable.MinWidthProperty,
                         new Binding(nameof(FilterValueMetrics.CountMinWidth)) { Source = metrics });

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);      row.Children.Add(cb);
            Grid.SetColumn(cntSlot, 1); row.Children.Add(cntSlot);
            return row;
        });

    // ── Детали выбранной строки (карточки полей + полный текст) ─────────────────
    //
    // Клик по строке таблицы открывает правую панель с карточками полей (как карточки
    // свойств «Страницы записи»); клик по карточке раскрывает полный текст поля снизу.
    // Крестик в шапке закрывает панель. Оформление зеркалит RecordPageView.

    private void OnLogsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LogsGrid.SelectedItem is LogRow row) OpenLogsDetail(row);
    }

    private void OpenLogsDetail(LogRow row)
    {
        BuildLogsDetailCards(row);
        ApplyLogsCardsHeight();
        LogsDetailPanel.Width         = _logsDetailWidth;
        LogsDetailDivider.IsVisible   = true;
        LogsDetailPanel.IsVisible     = true;
        LogsDetailResizeGrip.IsVisible = true;
    }

    private void CloseLogsDetail()
    {
        LogsDetailPanel.IsVisible      = false;
        LogsDetailDivider.IsVisible    = false;
        LogsDetailResizeGrip.IsVisible = false;
        LogsDetailCardsHost.Children.Clear();
        _logsDetailCards.Clear();
        _logsNameCols.Clear();
        _logsDetailSelectedCard = null;
        LogsDetailFullText.Text = "";
        // Снять выделение — иначе повторный клик по той же строке не даст SelectionChanged.
        LogsGrid.SelectedItem = null;
    }

    private void CopyLogsDetailField()
    {
        var text = LogsDetailFullText.Text;
        if (string.IsNullOrEmpty(text)) return;
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    // ── Ручка-разделитель: делит карточки (сверху) и полный текст (снизу) ────────
    // По умолчанию строки делят место поровну (обе 1*). Перетаскивание задаёт строке
    // карточек пиксельную высоту; выбранная высота помнится в пределах сессии и
    // переносится между строками — как PropsResizeGrip «Страницы записи».
    private bool    _logsGripDragging;
    private double  _logsGripStartY;
    private double  _logsGripStartHeight;
    private double? _logsCardsHeight;   // высота блока карточек (null = поровну, 1*)

    private const double LogsCardsHeightMin = 48;

    // Строка карточек в LogsDetailGrid (Auto,*,Auto,*): индекс 1.
    private RowDefinition LogsCardsRow => LogsDetailGrid.RowDefinitions[1];

    private void ApplyLogsCardsHeight() =>
        LogsCardsRow.Height = _logsCardsHeight is double h
            ? new GridLength(h, GridUnitType.Pixel)
            : new GridLength(1, GridUnitType.Star);

    private void OnLogsGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _logsGripDragging    = true;
        _logsGripStartY      = e.GetPosition(LogsDetailPanel).Y;
        _logsGripStartHeight = LogsCardsRow.ActualHeight;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnLogsGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_logsGripDragging) return;

        var dy  = e.GetPosition(LogsDetailPanel).Y - _logsGripStartY;
        // Не даём карточкам съесть весь текст: оставляем нижнему блоку запас (шапка+крошка+минимум).
        var max = Math.Max(LogsCardsHeightMin, LogsDetailPanel.Bounds.Height - 180);
        var newHeight = Math.Clamp(_logsGripStartHeight + dy, LogsCardsHeightMin, max);

        LogsCardsRow.Height = new GridLength(newHeight, GridUnitType.Pixel);
        _logsCardsHeight = newHeight;
    }

    private void OnLogsGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _logsGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ── Ручка ширины панели деталей (между «Диагностикой» и «Деталями») ─────────
    // Тянем за левый край панели: движение вправо сужает панель (она справа) — как
    // ручка панели записи (MainWindow.OnRecordGrip*). Ширина помнится в пределах сессии.
    private bool   _logsDetailGripDragging;
    private double _logsDetailGripStartX;
    private double _logsDetailGripStartWidth;

    private void OnLogsDetailGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _logsDetailGripDragging   = true;
        _logsDetailGripStartX     = e.GetPosition(this).X;
        _logsDetailGripStartWidth = _logsDetailWidth;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnLogsDetailGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_logsDetailGripDragging) return;

        var dx = e.GetPosition(this).X - _logsDetailGripStartX;
        _logsDetailWidth = Math.Clamp(_logsDetailGripStartWidth - dx,
                                      LogsDetailWidthMin, LogsDetailWidthMax);
        LogsDetailPanel.Width = _logsDetailWidth;
    }

    private void OnLogsDetailGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _logsDetailGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // Построить карточки полей выбранной строки; открыть снизу ранее выбранное поле
    // (или «Сообщение» по умолчанию) — как RestoreBottom в «Странице записи».
    private void BuildLogsDetailCards(LogRow row)
    {
        LogsDetailCardsHost.Children.Clear();
        _logsDetailCards.Clear();
        _logsNameCols.Clear();
        _logsDetailSelectedCard = null;

        foreach (var (key, name, iconKey) in LogsDetailFields)
        {
            var card = BuildLogsDetailCard(key, name, iconKey, row);
            _logsDetailCards[key] = card;
            LogsDetailCardsHost.Children.Add(card);
        }

        var openKey = LogsDetailFields.Any(f => f.Key == _logsDetailFieldKey)
            ? _logsDetailFieldKey : "message";
        OpenLogsDetailField(openKey, row);
    }

    // Карточка поля 1-в-1 как карточка свойства записи (RecordPageView.BuildCardShell):
    // [иконка типа + подпись] · [ручка ⋮] · [значение-превью]. Клик выделяет карточку.
    private Border BuildLogsDetailCard(string key, string name, string iconKey, LogRow row)
    {
        var nameLabel = new TextBlock
        {
            Text              = name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(8, 0, 4, 0),
            FontSize          = (double)this.FindResource("FontSizeBase")!,
        };
        nameLabel.Classes.Add("detailName");

        var nameRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds      = true,
        };
        var icon = MakeLogsCardIcon(iconKey);
        Grid.SetColumn(icon, 0);      nameRow.Children.Add(icon);
        Grid.SetColumn(nameLabel, 1); nameRow.Children.Add(nameLabel);

        var full    = LogsDetailFullValue(key, row);
        var preview = new TextBlock
        {
            Text              = full.Length == 0 ? "—" : full.ReplaceLineEndings(" ").Trim(),
            Foreground        = (IBrush)this.FindResource(full.Length == 0
                                    ? "BrushSecondaryText" : "BrushPrimaryText")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Margin            = new Thickness(2.5, 0, 0, 0),
        };

        // 3 колонки: [название (общая ширина, ресайз ⋮)] · [ручка ⋮] · [значение *].
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(new GridLength(_logsNameColWidth, GridUnitType.Pixel)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
        };
        _logsNameCols.Add(grid.ColumnDefinitions[0]);

        var grip = BuildLogsColumnGrip();
        Grid.SetColumn(nameRow, 0); grid.Children.Add(nameRow);
        Grid.SetColumn(grip,    1); grid.Children.Add(grip);
        Grid.SetColumn(preview, 2); grid.Children.Add(preview);

        var card = new Border { Classes = { "detailRow" }, Child = grid };
        // Клик по карточке выделяет её. Нажатие на ручку ⋮ обработчик грипа помечает
        // Handled (пузырьком, раньше карточки) — поэтому drag ручки карточку не выделяет.
        card.PointerPressed += (_, _) => OpenLogsDetailField(key, row);
        return card;
    }

    // Раскрыть поле снизу: подсветить карточку (синяя, как в записи), заполнить крошку и текст.
    private void OpenLogsDetailField(string key, LogRow row)
    {
        _logsDetailFieldKey = key;

        _logsDetailSelectedCard?.Classes.Remove("selected");
        if (_logsDetailCards.TryGetValue(key, out var card))
        {
            card.Classes.Add("selected");
            _logsDetailSelectedCard = card;
        }

        LogsDetailFieldName.Text = LogsDetailFields.First(f => f.Key == key).Name;
        LogsDetailFullText.Text  = LogsDetailFullValue(key, row);
    }

    // Ручка ⋮ карточки: три вертикальные точки; drag меняет общую ширину колонки названий.
    private Border BuildLogsColumnGrip()
    {
        var dots = new StackPanel
        {
            Orientation         = Orientation.Vertical,
            Spacing             = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
            dots.Children.Add(new Ellipse
            {
                Width  = 3,
                Height = 3,
                Fill   = (IBrush)this.FindResource("BrushSecondaryText")!,
            });

        var grip = new Border { Classes = { "detailGrip" }, Child = dots };
        grip.PointerPressed  += OnLogsColGripPressed;
        grip.PointerMoved    += OnLogsColGripMoved;
        grip.PointerReleased += OnLogsColGripReleased;
        return grip;
    }

    // ── Ручка ⋮: общая ширина колонки названий у всех карточек (как в записи) ────
    private bool   _logsColGripDragging;
    private double _logsColGripStartX;
    private double _logsColGripStartWidth;

    private void OnLogsColGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _logsColGripDragging   = true;
        _logsColGripStartX     = e.GetPosition(this).X;
        _logsColGripStartWidth = _logsNameColWidth;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnLogsColGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_logsColGripDragging) return;

        var dx  = e.GetPosition(this).X - _logsColGripStartX;
        // Названиям не даём съесть всё значение: значению оставляем минимум места.
        var max = Math.Max(LogsNameColMin, LogsDetailCardsHost.Bounds.Width - 160);
        _logsNameColWidth = Math.Clamp(_logsColGripStartWidth + dx, LogsNameColMin, max);
        foreach (var col in _logsNameCols)
            col.Width = new GridLength(_logsNameColWidth, GridUnitType.Pixel);
    }

    private void OnLogsColGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _logsColGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // Полное значение поля: время/уровень — отформатированные, сообщение и ошибка — как есть.
    private static string LogsDetailFullValue(string key, LogRow row) => key switch
    {
        "date"      => FormatLogsTs(row.Timestamp),
        "level"     => LogsLevelLabel(row.Level),
        "message"   => row.Message,
        "exception" => row.Exception ?? "",
        _           => "",
    };

    // Обводочная иконка 16×16 из реестра Icons.axaml с классом detailIcon (синеет в выбранной карточке).
    private Control MakeLogsCardIcon(string key)
    {
        var path = new Path
        {
            Data            = LogsIcon(key),
            StrokeThickness = LogsIconSize("IconStrokeSmall"),
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        path.Classes.Add("detailIcon");

        return new Viewbox
        {
            Width             = 16,
            Height            = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Child             = new Canvas { Width = 24, Height = 24, Children = { path } },
        };
    }

    private sealed class LogsFilterValueVm : System.ComponentModel.INotifyPropertyChanged
    {
        public string Raw       { get; }
        public string Display   { get; }
        public string CountText { get; }

        private readonly HashSet<string> _checked;
        private readonly Action _onChanged;

        public LogsFilterValueVm(string raw, string display, int count, HashSet<string> checkedSet, Action onChanged)
        {
            Raw = raw; Display = display; CountText = count.ToString();
            _checked = checkedSet; _onChanged = onChanged;
        }

        public bool Checked
        {
            get => _checked.Contains(Raw);
            set
            {
                if (value) _checked.Add(Raw); else _checked.Remove(Raw);
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Checked)));
                _onChanged();
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
