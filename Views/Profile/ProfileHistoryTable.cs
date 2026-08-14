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
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.Controls.Behaviors;
using SmartBase.Controls.Components;
using ChangeLogRow = SmartBase.Database.Repositories.ConstructorRepository.ChangeLogRow;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.Profile;

/// <summary>
/// «История изменений» — таблица журнала выбранной базы в стиле таблицы конструктора
/// (TableView): те же линии/шапка и тот же Excel-фильтр с сортировкой в заголовке
/// каждой колонки. Выбор базы — в навбаре (NavbarProfile). Строки подгружаются
/// keyset-страницами по 200 при прокрутке к концу (GetChangeLogPageAsync, отдельное
/// подключение) — миллионы строк не фризят UI. Фильтры и сортировка считаются в SQL.
/// </summary>
public partial class ProfileView
{
    private const int HistoryPageSize = 200;

    // Колонки журнала: ключ (как в ChangeLogQuery) и заголовок. Ширина у всех
    // одинаковая — звёздная, окно делится поровну.
    private static readonly (string Key, string Header)[] HistoryColumns =
    [
        ("user",       "Пользователь"),
        ("action",     "Действие"),
        ("type",       "Тип"),
        ("object",     "Объект"),
        ("collection", "Коллекция"),
        ("date",       "Дата и время"),
    ];

    private List<DatabaseManager> _historyDbs = [];
    private DatabaseManager? _historyDb;
    private bool _historyLoading;
    private bool _historyEnd;
    // Поколение запроса: смена базы/фильтра во время чтения страницы обесценивает результат.
    private int _historyGen;
    private readonly AvaloniaList<ChangeLogRow> _historyRows = [];

    // key колонки → набор разрешённых СЫРЫХ значений; нет ключа = фильтра нет (как в TableView).
    private readonly Dictionary<string, HashSet<string>> _historyFilters = [];
    private string? _historySortKey;
    private bool _historySortAsc;

    // Шевроны шапки: ключ колонки → Path, чтобы подсвечивать активный фильтр/сортировку.
    private readonly Dictionary<string, Path> _historyChevrons = [];
    private Flyout? _historyFlyout;
    private ScrollBar? _historyScroll;

    // ── Панель ────────────────────────────────────────────────────────────────

    private void RebuildHistoryPanel()
    {
        _historyDbs = _workspace?.Mounted.Where(m => m.Permissions.AccessGranted).ToList() ?? [];

        // Список баз — в навбаре; выбор сохраняется, если база ещё смонтирована.
        var keepDbId = _historyDb?.DbId;
        var names = _historyDbs
            .Select(d => string.IsNullOrWhiteSpace(d.Name) ? "База данных" : d.Name)
            .ToList();
        var idx = keepDbId is int id ? _historyDbs.FindIndex(d => d.DbId == id) : -1;
        HeaderBar.SetHistoryDbs(names, idx >= 0 ? idx : (names.Count > 0 ? 0 : -1));

        if (HistoryGrid.Columns.Count == 0)
        {
            BuildHistoryColumns();
            HistoryGrid.ItemsSource = _historyRows;
        }
        OnHistoryDbChanged();
    }

    // Выбор базы в навбаре (и первичное применение при открытии раздела).
    private void OnHistoryDbChanged()
    {
        var i = HeaderBar.HistoryDbIndex;
        var db = i >= 0 && i < _historyDbs.Count ? _historyDbs[i] : null;
        // Фильтры/сортировка не переносятся между базами (как в конструкторе между коллекциями).
        if (!ReferenceEquals(db, _historyDb))
        {
            _historyFilters.Clear();
            _historySortKey = null;
        }
        _historyDb = db;
        UpdateHistoryChevrons();
        ReloadHistoryRows();
    }

    // Перечитать строки с текущими фильтрами/сортировкой (скролл возвращается к началу).
    private void ReloadHistoryRows()
    {
        _historyGen++;
        _historyRows.Clear();
        _historyEnd = _historyDb is null;
        HistoryEmptyText.IsVisible = false;
        _ = LoadMoreHistoryAsync();
    }

    private async Task LoadMoreHistoryAsync()
    {
        if (_historyDb is null || _historyLoading || _historyEnd) return;
        _historyLoading = true;
        var gen       = _historyGen;
        var requestDb = _historyDb;
        try
        {
            var last  = _historyRows.Count > 0 ? _historyRows[^1] : null;
            var query = new ConstructorRepository.ChangeLogQuery
            {
                Filters = _historyFilters.ToDictionary(
                    kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value),
                SortColumn = _historySortKey,
                SortAsc    = _historySortAsc,
                AfterId    = last?.Id,
                AfterValue = last is null || _historySortKey is null
                    ? null
                    : HistoryRawValue(_historySortKey, last),
            };
            var page = await requestDb.Constructor.GetChangeLogPageAsync(query, HistoryPageSize);

            // Пока читали страницу, могли переключить базу/фильтр — результат уже не наш.
            if (gen != _historyGen) return;

            _historyRows.AddRange(page);
            if (page.Count < HistoryPageSize) _historyEnd = true;
            HistoryEmptyText.IsVisible = _historyRows.Count == 0;

            // Подписка на вертикальный скролл — после того, как шаблон DataGrid построен.
            Dispatcher.UIThread.Post(EnsureHistoryScrollHook, DispatcherPriority.Background);
        }
        finally { _historyLoading = false; }
    }

    // Следующая страница — при прокрутке к концу (порог ~300px до низа).
    private void EnsureHistoryScrollHook()
    {
        if (_historyScroll is not null) return;
        _historyScroll = HistoryGrid.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(s => s.Orientation == Orientation.Vertical);
        if (_historyScroll is null) return;

        _historyScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            if (_historyScroll.Maximum > 0 && _historyScroll.Value >= _historyScroll.Maximum - 300)
                _ = LoadMoreHistoryAsync();
        };
    }

    // Сырое значение колонки строки журнала — для фильтров и keyset-курсора сортировки.
    private static string HistoryRawValue(string key, ChangeLogRow row) => key switch
    {
        "user"       => row.UserName,
        "action"     => row.Action,
        "type"       => row.TargetType,
        "object"     => row.TargetName,
        "collection" => row.CollectionName ?? "",
        "date"       => row.Ts.Length >= 10 ? row.Ts[..10] : row.Ts,
        _            => "",
    };

    // ── Колонки и шапка ───────────────────────────────────────────────────────

    private void BuildHistoryColumns()
    {
        _historyChevrons.Clear();
        foreach (var def in HistoryColumns)
        {
            HistoryGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header       = BuildHistoryHeader(def.Key, def.Header),
                Width        = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth     = 110,
                CellTemplate = BuildHistoryCellTemplate(def.Key),
            });
        }
    }

    // Шапка колонки: [название][шеврон фильтра] + правая линия — как MakeTextHeader
    // в TableView (без иконки типа свойства).
    private Control BuildHistoryHeader(string key, string header)
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

        var chevron = BuildHistoryChevron(key);
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
        border.Classes.Add("colHeader");   // hover-фон через стиль (Assets/Styles/Controls.axaml)

        // Правая линия шапки — Rectangle в тех же слотах Grid "*,Auto", что и в ячейках
        // (см. WrapHeaderContent в TableView: одинаковое округление при любом зуме/DPI).
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

    // Стрелка в шапке колонки — открывает окно фильтра/сортировки (как в TableView:
    // спокойное состояние — серый шеврон, активный фильтр/сортировка — синяя воронка).
    private Control BuildHistoryChevron(string key)
    {
        var path = new Path
        {
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        _historyChevrons[key] = path;
        ApplyHistoryChevronState(key, path);

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);

        var btn = new Button
        {
            Width             = 20,
            Height            = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Content           = new Viewbox
            {
                Width  = HistoryIconSize("IconSizeSmall"),
                Height = HistoryIconSize("IconSizeSmall"),
                Child  = canvas,
            },
        };
        btn.Classes.Add("icon");
        btn.Classes.Add("filterChevron");
        ToolTip.SetTip(btn, "Фильтр и сортировка");
        btn.Click += (_, _) => ShowHistoryFilterFlyout(btn, key);
        return btn;
    }

    private void ApplyHistoryChevronState(string key, Path path)
    {
        var active  = _historyFilters.ContainsKey(key) || _historySortKey == key;
        path.Data   = active ? HistoryIcon("IconFilter") : HistoryIcon("IconChevronDown");
        path.Stroke = active
            ? (IBrush)this.FindResource("BrushAccent")!
            : (IBrush)this.FindResource("BrushSecondaryText")!;
    }

    private void UpdateHistoryChevrons()
    {
        foreach (var (key, path) in _historyChevrons)
            ApplyHistoryChevronState(key, path);
    }

    private static Geometry HistoryIcon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    private static double HistoryIconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 16;

    // ── Ячейки ────────────────────────────────────────────────────────────────

    private IDataTemplate BuildHistoryCellTemplate(string key) =>
        new FuncDataTemplate<ChangeLogRow>((row, _) =>
        {
            if (row is null) return new TextBlock();
            var text = new TextBlock
            {
                Text              = HistoryCellText(key, row),
                Margin            = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            // «Объект» с «до → после» обрезается чаще других — полный текст в тултипе.
            if (key == "object") ToolTip.SetTip(text, text.Text);
            return text;
        });

    private static string HistoryCellText(string key, ChangeLogRow row) => key switch
    {
        "user"       => row.UserName,
        "action"     => HistoryActionLabel(row.Action),
        "type"       => HistoryTypeLabel(row.TargetType),
        "object"     => HistoryObjectText(row),
        "collection" => row.CollectionName ?? "",
        "date"       => FormatHistoryTs(row.Ts),
        _            => "",
    };

    private static string HistoryActionLabel(string action) => action switch
    {
        "create"       => "Создание",
        "update"       => "Изменение",
        "delete"       => "Удаление",
        "duplicate"    => "Дублирование",
        "import"       => "Импорт",
        "export"       => "Экспорт",
        "move"         => "Перенос",
        "user.join"    => "Присоединение",
        "user.approve" => "Допуск",
        "user.revoke"  => "Отзыв допуска",
        "user.rights"  => "Изменение прав",
        "user.unlock"  => "Доступ по паролю",
        "user.rename"  => "Смена имени",
        _              => action,
    };

    private static string HistoryTypeLabel(string targetType) => targetType switch
    {
        "collection" => "Коллекция",
        "group"      => "Группа",
        "field"      => "Свойство",
        "record"     => "Запись",
        "records"    => "Записи",
        "user"       => "Пользователь",
        _            => targetType,
    };

    // Объект строки: правки с «до → после» показывают готовый Detail («„А" → „Б"»,
    // «Договор 12 (Телефон: «123» → «456»)»); иначе имя цели; пакетные операции —
    // «10 000 записей», дубль/удаление коллекции с записями — «Имя (N записей)».
    private static string HistoryObjectText(ChangeLogRow row)
    {
        if (!string.IsNullOrEmpty(row.Detail)) return row.Detail;
        if (row.TargetType == "records") return RecordsCount(row.Count ?? 0);
        if (row.TargetName.Length == 0)  return "—";
        return row.TargetType is "collection" or "group" && row.Count is > 0
            ? $"{row.TargetName} ({RecordsCount(row.Count.Value)})"
            : row.TargetName;
    }

    // Русское склонение: 1 запись, 2 записи, 10 000 записей.
    private static string RecordsCount(long n)
    {
        var word = (n % 100 is >= 11 and <= 14) ? "записей" : (n % 10) switch
        {
            1           => "запись",
            2 or 3 or 4 => "записи",
            _           => "записей",
        };
        return $"{n:N0} {word}";
    }

    // ISO UTC → локальное «дд.MM.гггг HH:mm».
    private static string FormatHistoryTs(string iso) =>
        DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : iso;

    // Значение в окне фильтра: сырое → отображаемое (те же подписи, что в ячейках).
    private static string HistoryDisplayValue(string key, string raw)
    {
        if (raw.Length == 0) return "(Пусто)";
        return key switch
        {
            "action" => HistoryActionLabel(raw),
            "type"   => HistoryTypeLabel(raw),
            "date"   => DateTime.TryParseExact(raw, "yyyy-MM-dd", null,
                            System.Globalization.DateTimeStyles.None, out var d)
                        ? d.ToString("dd.MM.yyyy")
                        : raw,
            _        => raw,
        };
    }

    // ── Фильтр и сортировка колонок (Excel-стиль, зеркало BuildFilterPanel TableView) ──

    private void ShowHistoryFilterFlyout(Control anchor, string key)
    {
        if (_historyDb is null) return;

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("filterFlyout");
        // Флайаут рендерится в отдельном попап-окне вне зума приложения — контент
        // оборачивается в зум-трансформ (плашка-презентер остаётся вне зума).
        flyout.Content = WindowZoom.WrapPopupContent(BuildHistoryFilterPanel(key, () => flyout.Hide()));
        flyout.Closed += (_, _) => { if (ReferenceEquals(_historyFlyout, flyout)) _historyFlyout = null; };
        _historyFlyout = flyout;
        flyout.ShowAt(anchor);
    }

    private Control BuildHistoryFilterPanel(string key, Action close)
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

        // ── Данные (грузятся в фоне; окно открывается мгновенно) ──
        var baseCounts = new List<(string Value, int Count)>();
        var domain     = new HashSet<string>();
        var loaded     = false;

        // ── Сортировка: копится в pendingSort, применяется по «Готово» ──
        var (ascLabel, descLabel) = key == "date"
            ? ("Сначала старые", "Сначала новые")
            : ("От А до Я", "От Я до А");
        bool? pendingSort = _historySortKey == key ? _historySortAsc : null;

        var sortSection = new StackPanel { Spacing = 2 };
        panel.Children.Add(sortSection);

        Action rebuildListRef = () => { };

        List<(string Raw, int Count)> counts = [];
        void ReloadCounts()
        {
            bool? asc = pendingSort ?? (_historySortKey == key ? _historySortAsc : (bool?)null);
            counts = SortHistoryValueCounts(baseCounts, key, asc);
        }
        ReloadCounts();

        void RenderSort()
        {
            sortSection.Children.Clear();
            sortSection.Children.Add(MakeHistoryFlatItem(ascLabel,
                () => { pendingSort = true;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                HistoryIcon("IconSortAz"), active: pendingSort == true));
            sortSection.Children.Add(MakeHistoryFlatItem(descLabel,
                () => { pendingSort = false; RenderSort(); ReloadCounts(); rebuildListRef(); },
                HistoryIcon("IconSortZa"), active: pendingSort == false));
            sortSection.Children.Add(MakeHistoryFlatItem("Снять сортировку",
                () => { pendingSort = null;  RenderSort(); ReloadCounts(); rebuildListRef(); },
                HistoryIcon("IconClose")));
        }
        RenderSort();

        panel.Children.Add(MakeHistorySeparator());

        // ── Поиск по значениям ──
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
            IsChecked = !_historyFilters.ContainsKey(key),
            Cursor    = new Cursor(StandardCursorType.Hand),
        };

        // Виртуализирующий ListBox — все уникальные значения без лимита и зависаний.
        var shown   = new AvaloniaList<HistoryFilterValueVm>();
        var vmCache = new Dictionary<string, HistoryFilterValueVm>();
        HistoryFilterValueVm Vm(string raw, int count) =>
            vmCache.TryGetValue(raw, out var v) ? v
                : vmCache[raw] = new HistoryFilterValueVm(raw, HistoryDisplayValue(key, raw), count,
                                                          checkedSet, () => SyncSelectAll());

        var valuesList = new ListBox
        {
            // Высоты нет: список занимает остаток строки карточки фиксированной высоты и
            // сам ужимается, когда снизу появляется горизонтальная полоса.
            Background      = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(0),
            ItemsSource     = shown,
            ItemTemplate    = BuildHistoryFilterValueTemplate(fontBase, secondary, metrics),
        };
        valuesList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 2)),
                        new Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });

        // Строки без подсветки наведения/выбора — взаимодействуем только с чекбоксом.
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
                : counts.Where(c => HistoryDisplayValue(key, c.Raw)
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

        panel.Children.Add(MakeHistorySeparator());

        // ── «Готово»: сортировка и фильтр применяются разом ──
        var okBtn = new Button
        {
            Content             = "Готово",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 4, 0, 0),
        };
        okBtn.Classes.Add("dialog");
        okBtn.Click += (_, _) =>
        {
            if (pendingSort is bool asc) { _historySortKey = key; _historySortAsc = asc; }
            else if (_historySortKey == key) _historySortKey = null;

            if (loaded)
            {
                // Excel-режим «Заменить»: при активном поиске фильтр = отмеченные СРЕДИ
                // найденного; поиск пуст — все отмеченные галки по всему домену.
                var hasSearch = !string.IsNullOrWhiteSpace(search.Text);
                var effective = hasSearch
                    ? Matched().Select(c => c.Raw).Where(checkedSet.Contains).ToHashSet()
                    : [.. checkedSet];

                if (domain.SetEquals(effective)) _historyFilters.Remove(key);
                else _historyFilters[key] = effective;
            }

            UpdateHistoryChevrons();
            ReloadHistoryRows();
            close();
        };
        panel.Children.Add(okBtn);

        // ── Фоновая загрузка значений (SQL-агрегат, отдельное подключение) ──
        _historyDb!.Constructor.GetChangeLogCountsAsync(key).ContinueWith(t =>
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
                if (_historyFilters.TryGetValue(key, out var ff)) checkedSet.UnionWith(ff);
                else checkedSet.UnionWith(domain);

                ReloadCounts();
                valuesHost.Child = valuesBox;   // «Загрузка…» → список значений
                RebuildList();
                SyncSelectAll();
                loaded = true;
            });
        });

        return panel;
    }

    // Порядок значений в окне фильтра: выбранная сортировка колонки; без неё — дата
    // новыми сверху (сырое ISO сортируется хронологически), остальные по подписи,
    // «(Пусто)» в конце.
    private static List<(string Raw, int Count)> SortHistoryValueCounts(
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
                ? items.OrderByDescending(c => HistoryDisplayValue(key, c.Raw),
                                          StringComparer.CurrentCultureIgnoreCase).ToList()
                : items.OrderBy(c => HistoryDisplayValue(key, c.Raw),
                                StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        items.AddRange(empty);
        return items;
    }

    // Иконка в стандарте сайдбара: Viewbox «Средний» поверх сетки 24×24 (как в TableView).
    private Viewbox MakeHistoryStrokeIcon(Geometry data, IBrush stroke) => new()
    {
        Width  = HistoryIconSize("IconSizeMedium"),
        Height = HistoryIconSize("IconSizeMedium"),
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
                    StrokeThickness = (double)this.FindResource("IconStrokeMedium")!,
                    StrokeLineCap   = PenLineCap.Round,
                    StrokeJoin      = PenLineJoin.Round,
                },
            },
        },
    };

    private Button MakeHistoryFlatItem(string text, Action onClick, Geometry? icon = null, bool active = false)
    {
        var fg = (IBrush)this.FindResource(active ? "BrushAccent" : "BrushPrimaryText")!;

        object content = text;
        if (icon is not null)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var iconBox = MakeHistoryStrokeIcon(icon, fg);
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
                var check = MakeHistoryStrokeIcon(HistoryIcon("IconCheck"), fg);
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

    // Разделитель в попапе фильтра (контент обёрнут в зум-трансформ — токен обычный).
    private Border MakeHistorySeparator()
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

    // Шаблон строки списка значений (чекбокс + количество) — для виртуализированного
    // ListBox: привязки следуют за DataContext при переиспользовании контейнеров.
    private static IDataTemplate BuildHistoryFilterValueTemplate(double fontBase, IBrush secondary,
                                                                 FilterValueMetrics metrics) =>
        new FuncDataTemplate<HistoryFilterValueVm>((_, _) =>
        {
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            name.Bind(TextBlock.TextProperty, new Binding(nameof(HistoryFilterValueVm.Display)));
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
            cb.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(HistoryFilterValueVm.Checked)) { Mode = BindingMode.TwoWay });

            var cnt = new TextBlock
            {
                Foreground        = secondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment     = TextAlignment.Right,
            };
            cnt.Bind(TextBlock.TextProperty, new Binding(nameof(HistoryFilterValueVm.CountText)));
            // Счётчики стоят столбиком: ширина колонки — по самому длинному числу.
            cnt.Bind(Layoutable.MinWidthProperty,
                     new Binding(nameof(FilterValueMetrics.CountMinWidth)) { Source = metrics });

            // Счётчик ЗАКРЕПЛЁН у правого края видимой области: сдвиг компенсирует
            // горизонтальную прокрутку, фон непрозрачный — длинный текст уезжает под него.
            var pin = new TranslateTransform();
            pin.Bind(TranslateTransform.XProperty,
                     new Binding(nameof(FilterValueMetrics.Pin)) { Source = metrics });
            var cntHost = new Border
            {
                Background      = Brushes.White,
                Padding         = new Thickness(8, 0, 0, 0),
                Child           = cnt,
                RenderTransform = pin,
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(cb, 0);      row.Children.Add(cb);
            Grid.SetColumn(cntHost, 1); row.Children.Add(cntHost);
            return row;
        });

    // Строка списка значений фильтра. Checked читает/пишет общий checkedSet; onChanged
    // обновляет состояние «(Выделить всё)».
    private sealed class HistoryFilterValueVm : System.ComponentModel.INotifyPropertyChanged
    {
        public string Raw       { get; }
        public string Display   { get; }
        public string CountText { get; }

        private readonly HashSet<string> _checked;
        private readonly Action _onChanged;

        public HistoryFilterValueVm(string raw, string display, int count, HashSet<string> checkedSet, Action onChanged)
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
