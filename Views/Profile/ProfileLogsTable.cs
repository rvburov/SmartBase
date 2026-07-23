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
using LogRow = SmartBase.Logging.LogReader.LogRow;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.Profile;

/// <summary>
/// «Логи» — таблица диагностического журнала logs.db (Serilog) в том же стиле, что и
/// «История изменений» (<see cref="ProfileView"/> в ProfileHistoryTable.cs): те же
/// линии/шапка и тот же Excel-фильтр с сортировкой в заголовке колонки. Отличие: logs.db —
/// единый файл на приложение (не per-database), поэтому выбора базы в навбаре нет, а данные
/// читаются через <see cref="LogReader"/> (raw SQL по logs.db), а не через репозиторий.
/// Строки подгружаются keyset-страницами по 200 при прокрутке к концу.
/// </summary>
public partial class ProfileView
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

    // ── Панель ────────────────────────────────────────────────────────────────

    private void RebuildLogsPanel()
    {
        if (LogsGrid.Columns.Count == 0)
        {
            BuildLogsColumns();
            LogsGrid.ItemsSource = _logsRows;
        }
        UpdateLogsChevrons();
        ReloadLogsRows();
    }

    // Перечитать строки с текущими фильтрами/сортировкой (скролл возвращается к началу).
    private void ReloadLogsRows()
    {
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

    // ── Колонки и шапка ───────────────────────────────────────────────────────

    private void BuildLogsColumns()
    {
        _logsChevrons.Clear();
        foreach (var def in LogsColumns)
        {
            LogsGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header       = BuildLogsHeader(def.Key, def.Header),
                Width        = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth     = 110,
                CellTemplate = BuildLogsCellTemplate(def.Key),
            });
        }
    }

    private Control BuildLogsHeader(string key, string header)
    {
        var text = new TextBlock
        {
            Text              = header,
            FontSize          = (double)this.FindResource("FontSizeBase")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var chevron = BuildLogsChevron(key);
        Grid.SetColumn(chevron, 1);
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

        return host;
    }

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
            var text = new TextBlock
            {
                Text              = LogsCellText(key, row),
                Margin            = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            // Сообщение и стектрейс часто длиннее колонки — полный текст в тултипе.
            if (key is "message" or "exception" && text.Text.Length > 0)
                ToolTip.SetTip(text, LogsTooltipText(key, row));
            return text;
        });

    private static string LogsCellText(string key, LogRow row) => key switch
    {
        "date"      => FormatLogsTs(row.Timestamp),
        "level"     => LogsLevelLabel(row.Level),
        "message"   => row.Message,
        "exception" => LogsExceptionSummary(row.Exception),
        _           => "",
    };

    // В ячейке — первая строка исключения; в тултипе — полный текст (тип + стектрейс).
    private static string LogsTooltipText(string key, LogRow row) => key switch
    {
        "message"   => row.Message,
        "exception" => row.Exception ?? "",
        _           => "",
    };

    private static string LogsLevelLabel(string level) => level switch
    {
        "Verbose"     => "Подробно",
        "Debug"       => "Отладка",
        "Information" => "Информация",
        "Warning"     => "Предупреждение",
        "Error"       => "Ошибка",
        "Fatal"       => "Критическая",
        _             => level,
    };

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
    private static string FormatLogsTs(string ts) =>
        DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AssumeUniversal |
                          System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : ts;

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
            Height          = 200,
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ItemsSource     = shown,
            ItemTemplate    = BuildLogsFilterValueTemplate(fontBase, secondary),
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

        var valuesBox = new StackPanel { Spacing = 4 };
        valuesBox.Children.Add(selectAll);
        valuesBox.Children.Add(valuesList);

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
            Padding      = new Thickness(8, 6),
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
                valuesHost.Child = valuesBox;
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

    private static IDataTemplate BuildLogsFilterValueTemplate(double fontBase, IBrush secondary) =>
        new FuncDataTemplate<LogsFilterValueVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(LogsFilterValueVm.Display)));

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

            var cnt = new TextBlock
            {
                Foreground          = secondary,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 0, 12, 0),
            };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(LogsFilterValueVm.CountText)));

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);  row.Children.Add(cb);
            Grid.SetColumn(cnt, 1); row.Children.Add(cnt);
            return row;
        });

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
