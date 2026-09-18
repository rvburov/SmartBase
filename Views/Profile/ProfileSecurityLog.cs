using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Utils;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.Profile;

/// <summary>
/// Раздел «Журнал безопасности» — что уходило наружу и что было заблокировано.
///
/// <b>Почему это раздел, а не строка панели администратора.</b> Журнал один на всю программу:
/// база в нем — колонка строки, а не хозяин настройки. Пока он открывался кнопкой из панели,
/// управляющей ОДНОЙ базой, экран обещал, что относится к ней, — и это было неправдой.
///
/// <b>Почему таблицей, а не блоками.</b> Прежний экран был статьей: проверка целости, отбор,
/// выгрузка и сведения о файле шли блоками с пояснениями, а сами события ютились в узкой таблице
/// внизу. Читают журнал не как статью: ищут строку. Поэтому теперь экран устроен как «История
/// изменений» — та же шапка с воронками, те же линии, та же панель «Детали» справа, — а действия
/// (проверка цепочки и выгрузка) ушли в навбар, итог проверки и путь к файлу — в строку состояния.
/// Объяснения переехали в справку: экран — инструмент, а не рассказ о себе.
///
/// Отбор уходит в SQL (<see cref="SecurityFilter"/>), а не отсеивает показанную пачку: журнал
/// никто не чистит, он копится годами, и одно заблокированное событие полугодовой давности иначе
/// не найти. Строки читаются страницами по ключу последней показанной (keyset) — тем же приемом,
/// что и «История изменений».
///
/// Сортировка есть только у времени: строки журнала связаны в цепочку, и «отсортировать по
/// пользователю» для нее — бессмыслица. Поэтому у остальных колонок в окне отбора пунктов
/// сортировки нет вовсе (<see cref="ColumnFilterSpec.Sortable"/>).
/// </summary>
public partial class ProfileView
{
    private const int SecurityLogPageSize = 200;

    // Колонки: ключ отбора (имя из SecurityJournal.Filterable; пусто — колонка без воронки),
    // заголовок и иконка типа. Те же подписи стоят в карточках «Деталей» и в выгрузке CSV.
    private static readonly (string Key, string Header, string IconKey)[] SecurityLogColumns =
    [
        ("Day",        "Дата и время", "IconClock"),
        ("Event",      "Действие",     "IconPencil"),
        ("Who",        "Пользователь", "IconUser"),
        ("DbId",       "База данных",  "IconDatabase"),
        ("Collection", "Коллекция",    "IconCollection"),
        ("Address",    "Назначение",   "IconLink"),
        ("Allowed",    "Результат",    "IconShieldCheck"),
    ];

    // Колонка адреса не отбирается: адресов столько же, сколько запросов, и список значений в
    // воронке был бы длиннее самого журнала.
    private static bool SecurityLogFilterable(string key) => key != "Address";

    private readonly AvaloniaList<SecurityEntry> _logRows = [];
    private readonly Dictionary<string, HashSet<string>> _logFilters = [];
    private readonly Dictionary<string, Path> _logChevrons = [];

    private bool _logNewestFirst = true;
    private bool _logLoading;
    private bool _logEnd;
    // Поколение запроса: смена отбора во время чтения страницы обесценивает результат.
    private int  _logGen;
    private ScrollBar? _logScroll;
    private bool _logHooked;

    // Переход «В журнал» из контура безопасности просит показать сразу блокировки.
    private bool _logBlockedPending;

    // Итог последней проверки цепочки: null — в этот заход не проверяли, 0 — цела, иначе ключ
    // первой разошедшейся строки.
    private long? _logChain;

    private static SecurityJournal? Journal => SecurityJournal.Current;

    // ── Показ раздела ─────────────────────────────────────────────────────────

    private void RebuildSecurityLog()
    {
        if (!_logHooked)
        {
            BuildSecurityLogColumns();
            LogGrid.ItemsSource     = _logRows;
            LogGrid.SelectionChanged += OnSecurityLogSelectionChanged;
            LogDetailCloseBtn.Click += (_, _) => CloseLogDetail();
            LogDetailCopyBtn.Click  += (_, _) => CopyLogRow();
            _logHooked = true;
        }

        // Пришли по кнопке «В журнал» — показываем ровно то, за чем шли.
        if (_logBlockedPending)
        {
            _logBlockedPending = false;
            _logFilters.Clear();
            _logFilters["Allowed"] = ["0"];
        }

        UpdateSecurityLogChevrons();
        RefreshSecurityLogStatus();
        ReloadSecurityLogRows();
    }

    private void ReloadSecurityLogRows()
    {
        CloseLogDetail();
        _logGen++;
        _logRows.Clear();
        _logEnd = Journal is null;
        _ = LoadMoreSecurityLogAsync();
        ShowSecurityLogEmptyText();
    }

    private async Task LoadMoreSecurityLogAsync()
    {
        if (Journal is not { } journal || _logLoading || _logEnd) return;

        _logLoading = true;
        var gen = _logGen;

        try
        {
            var filter = SecurityLogFilter();
            var after  = _logRows.Count > 0 ? _logRows[^1].Id : 0;
            var newest = _logNewestFirst;

            // Чтение — в фоне: журнал за год лежит на диске, и страница с отбором по дню считается
            // не мгновенно, а интерфейс в это время должен оставаться живым.
            var page = await Task.Run(() =>
                journal.Page(filter, SecurityLogPageSize, after, newest));

            // Пока читали страницу, отбор успели сменить — эта страница уже не наша.
            if (gen != _logGen) return;

            _logRows.AddRange(page);
            if (page.Count < SecurityLogPageSize) _logEnd = true;
            ShowSecurityLogEmptyText();

            // Подписка на вертикальный скролл — после того, как шаблон DataGrid построен.
            Dispatcher.UIThread.Post(EnsureSecurityLogScrollHook, DispatcherPriority.Background);
        }
        finally
        {
            _logLoading = false;
            // Пока шло чтение, таблицу перезапросили: первая страница нового отбора не стартовала.
            if (gen != _logGen) _ = LoadMoreSecurityLogAsync();
        }
    }

    // Пустая таблица объясняет себя: журнала нет, отбор ничего не пропустил или событий не было.
    private void ShowSecurityLogEmptyText()
    {
        LogEmptyText.Text = Journal is null
            ? "Журнал безопасности не открылся при запуске программы. Отправка данных наружу не "
              + "работает: обмен, о котором не осталось записи, не допускается. Причина записана "
              + "в разделе «Настройки → Диагностика»."
            : _logFilters.Count > 0
                ? "Под выбранные условия не подошло ни одной строки."
                : "Событий безопасности еще не было: наружу ничего не уходило и направлений не "
                  + "подтверждали.";

        LogEmptyText.IsVisible = _logRows.Count == 0;
    }

    private SecurityFilter SecurityLogFilter() =>
        new(Columns: _logFilters.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value));

    private void EnsureSecurityLogScrollHook()
    {
        if (_logScroll is not null) return;
        _logScroll = LogGrid.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(s => s.Orientation == Orientation.Vertical);
        if (_logScroll is null) return;

        _logScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            if (_logScroll.Maximum > 0 && _logScroll.Value >= _logScroll.Maximum - 300)
                _ = LoadMoreSecurityLogAsync();
        };
    }

    // ── Строка состояния ──────────────────────────────────────────────────────

    /// <summary>
    /// Итог проверки цепочки слева, число строк и файл справа. «Цела» здесь не значит «все
    /// хорошо»: цепочка ловит правку и вырезанную строку, но не удаление файла целиком, — и
    /// делать вид, что хэши закрывают и это, нельзя.
    /// </summary>
    private void RefreshSecurityLogStatus()
    {
        if (Journal is not { } journal)
        {
            LogChainText.Text = "Журнал не открыт.";
            LogFileText.Text  = "";
            return;
        }

        var total = journal.Count();

        LogChainText.Text = _logChain switch
        {
            null   => "Цепочку хэшей в этот заход не проверяли.",
            0      => $"Цепочка цела: {Plural((int)Math.Min(int.MaxValue, total), "строка сходится", "строки сходятся", "строк сходятся")}.",
            var id => $"ЦЕПОЧКА РАЗОШЛАСЬ на строке №{id}: файл правили мимо программы.",
        };

        LogChainText.Foreground = Brush(
            _logChain is > 0 ? "BrushRed" : _logChain == 0 ? "BrushGreen" : "BrushSecondaryText",
            _logChain is > 0 ? "#FF3B30"  : _logChain == 0 ? "#34C759"    : "#8E8E93");

        LogFileText.Text = $"Строк: {total:N0} · {journal.Path}";
    }

    /// <summary>
    /// Проверить цепочку по кнопке навбара. Проверка проходит файл целиком, поэтому идет в фоне и
    /// только по требованию: делать ее на каждый показ экрана значило бы перечитывать журнал за
    /// все годы ради списка последних строк.
    /// </summary>
    private async void VerifySecurityChain()
    {
        if (Journal is not { } journal) return;

        var first = await Task.Run(journal.Verify);
        _logChain = first;
        RefreshSecurityLogStatus();

        // Разошедшаяся цепочка — не строка состояния, а событие: о нем говорят окном и словами,
        // что делать дальше.
        if (first != 0 && TopLevel.GetTopLevel(this) is Window owner)
            await ConfirmDialog.Info(
                $"Цепочка журнала безопасности разошлась на строке №{first}.\n\n"
                + "Строку поправили или вырезали мимо программы. Сверьтесь с копией журнала в "
                + "папке аудита организации и сообщите ответственному за интеграции.")
                .ShowDialog(owner);
    }

    // ── Выгрузка в CSV ────────────────────────────────────────────────────────

    private bool _logExporting;

    /// <summary>
    /// Выгрузить отобранное. Выгружается ВСЕ, что прошло отбор, а не показанные страницы: человек,
    /// нажавший «Выгрузить», просит журнал за период, а не картинку экрана. Строки читаются лениво
    /// (<see cref="SecurityJournal.Stream"/>), поэтому размер файла ограничивает только диск.
    /// </summary>
    private async Task ExportSecurityLogToCsv()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (Journal is not { } journal) return;
        if (_logExporting) return;

        // Снимок условий ДО ожидания: их могли поменять, пока считались строки.
        var filter = SecurityLogFilter();

        _logExporting = true;
        try
        {
            await CsvExportRunner.Run(owner, new CsvExportRunner.Job(
                Title:           "Выгрузка журнала безопасности",
                What:            "Журнал безопасности",
                TotalRows:       journal.Count(filter),
                Filtered:        filter.Narrow,
                DefaultFileName: CsvExportRunner.FileName("Журнал безопасности", DateTime.Now),
                Headers:         SecurityJournal.CsvHeaders,
                Rows:            ct => journal.Stream(filter)
                                              .TakeWhile(_ => !ct.IsCancellationRequested)
                                              .Select(SecurityJournal.CsvRow)));
        }
        finally { _logExporting = false; }
    }

    // ── Колонки и шапка ───────────────────────────────────────────────────────

    private void BuildSecurityLogColumns()
    {
        _logChevrons.Clear();

        foreach (var (key, header, iconKey) in SecurityLogColumns)
            LogGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header       = BuildSecurityLogHeader(key, header, iconKey),
                Width        = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth     = key == "Address" ? 180 : 130,
                CellTemplate = BuildSecurityLogCell(key),
            });
    }

    // Шапка колонки — как в «Истории изменений» и таблице конструктора: [иконка][подпись]
    // [шеврон отбора] + правая линия-разделитель.
    private Control BuildSecurityLogHeader(string key, string header, string iconKey)
    {
        var text = new TextBlock
        {
            Text              = header,
            FontSize          = (double)this.FindResource("FontSizeBase")!,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Foreground        = (IBrush)this.FindResource("BrushSecondaryText")!,
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

        var icon = BuildHistoryHeaderIcon(iconKey);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (SecurityLogFilterable(key))
        {
            var chevron = BuildSecurityLogChevron(key);
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

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

    private Control BuildSecurityLogChevron(string key)
    {
        var path = new Path
        {
            StrokeThickness = 2,
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        };
        _logChevrons[key] = path;
        ApplySecurityLogChevronState(key, path);

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
        ToolTip.SetTip(btn, key == "Day" ? "Отбор и сортировка" : "Отбор");
        btn.Click += (_, _) => ShowSecurityLogFilterFlyout(btn, key);
        return btn;
    }

    private void ApplySecurityLogChevronState(string key, Path path)
    {
        // Порядок «сначала старые» — тоже след на колонке времени: без него человек не найдет,
        // чем перевернул таблицу.
        var active  = _logFilters.ContainsKey(key) || (key == "Day" && !_logNewestFirst);
        path.Data   = HistoryIcon(active ? "IconFilter" : "IconChevronDown");
        path.Stroke = active
            ? (IBrush)this.FindResource("BrushAccent")!
            : (IBrush)this.FindResource("BrushSecondaryText")!;
    }

    private void UpdateSecurityLogChevrons()
    {
        foreach (var (key, path) in _logChevrons)
            ApplySecurityLogChevronState(key, path);
    }

    // ── Ячейки ────────────────────────────────────────────────────────────────

    private IDataTemplate BuildSecurityLogCell(string key) =>
        new FuncDataTemplate<SecurityEntry>((entry, _) =>
        {
            if (entry is null) return new TextBlock();

            var text = new TextBlock
            {
                Text              = SecurityLogCellText(key, entry),
                Margin            = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };

            // Решение — цветом: заблокированное событие ищут глазами, а не вычитывают.
            if (key == "Allowed")
                text.Foreground = Brush(entry.Allowed ? "BrushGreen" : "BrushRed",
                                        entry.Allowed ? "#34C759" : "#FF3B30");
            else if (key is "Day" or "Who" or "Address")
                text.Foreground = (IBrush)this.FindResource("BrushSecondaryText")!;

            return text;
        });

    private string SecurityLogCellText(string key, SecurityEntry entry) => key switch
    {
        "Day"        => SecurityLogMoment(entry),
        "Event"      => SecurityJournal.Name(entry.Event),
        "Who"        => entry.Who.Length > 0 ? entry.Who : "—",
        "DbId"       => SecurityLogDatabase(entry.DbId),
        "Collection" => entry.Collection.Length > 0 ? entry.Collection : "—",
        "Address"    => entry.Address.Length > 0 ? entry.Address : "—",
        "Allowed"    => entry.Allowed ? "разрешено" : "блокировка",
        _            => "",
    };

    /// <summary>Момент строки; испорченный правкой файла момент показывается как есть.</summary>
    private static string SecurityLogMoment(SecurityEntry entry) =>
        entry.Moment is { } moment
            ? moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : entry.At;

    /// <summary>Имя базы по ключу; закрытая база названа ключом — журнал переживает базы.</summary>
    private string SecurityLogDatabase(int dbId)
    {
        if (dbId <= 0) return "—";

        var open = _workspace?.Mounted ?? [];

        return open.FirstOrDefault(d => d.DbId == dbId) is { } found
            ? found.Name
            : $"№{dbId} (сейчас не открыта)";
    }

    // Значение в окне отбора: сырое из SQL → те же слова, что в ячейках.
    private string SecurityLogDisplayValue(string key, string raw)
    {
        if (raw.Length == 0) return "(Пусто)";

        return key switch
        {
            "Event"   => int.TryParse(raw, out var kind) ? SecurityJournal.Name((SecurityEvent)kind) : raw,
            "Allowed" => raw == "1" ? "разрешено" : "блокировка",
            "DbId"    => int.TryParse(raw, out var id) ? SecurityLogDatabase(id) : raw,
            "Day"     => DateTime.TryParseExact(raw, "yyyy-MM-dd", null,
                             System.Globalization.DateTimeStyles.None, out var d)
                         ? d.ToString("dd.MM.yyyy")
                         : raw,
            _         => raw,
        };
    }

    // ── Отбор колонки (общее окно, как в остальных таблицах) ──────────────────

    private void ShowSecurityLogFilterFlyout(Control anchor, string key)
    {
        if (Journal is not { } journal) return;

        // Снимок отбора остальных колонок: значения считаются по строкам, прошедшим их (каскад), —
        // иначе снятое значение исчезало бы из списка и вернуть его было бы нечем.
        var others = new SecurityFilter(Columns: _logFilters
            .Where(kv => kv.Key != key)
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value));

        var byTime = key == "Day";

        ColumnFilterPanel.Show(this, anchor, new ColumnFilterSpec
        {
            Key        = key,
            AscLabel   = "Сначала старые",
            DescLabel  = "Сначала новые",
            Sortable   = byTime,
            Sort       = byTime ? !_logNewestFirst : null,
            Filter     = _logFilters.GetValueOrDefault(key),
            Display    = raw => SecurityLogDisplayValue(key, raw),
            IsDate     = byTime,
            LoadValues = () => Task.Run(() => journal.Values(key, others)
                                                     .Select(v => (v.Value, v.Count))
                                                     .ToList()),
            Apply      = result =>
            {
                result.ApplyTo(_logFilters, ref _logSortKeyUnused, ref _logSortAscUnused);

                // Порядок у журнала один — по времени, и держать «ключ сортировки» ему незачем:
                // из итога окна берется только направление, и только у колонки времени.
                if (byTime) _logNewestFirst = result.Sort != true;

                UpdateSecurityLogChevrons();
                ReloadSecurityLogRows();
            },
        });
    }

    // ApplyTo просит переменные под ключ и направление сортировки: у журнала их роль играет
    // _logNewestFirst, а эти два поля существуют только ради подписи метода.
    private string? _logSortKeyUnused;
    private bool    _logSortAscUnused;

    // ── Детали строки ─────────────────────────────────────────────────────────

    private void OnSecurityLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LogGrid.SelectedItem is SecurityEntry entry) OpenLogDetail(entry);
    }

    private void OpenLogDetail(SecurityEntry entry)
    {
        BuildLogDetailCards(entry);
        DetailDivider.IsVisible = true;
        LogDetailPanel.IsVisible = true;
    }

    private void CloseLogDetail()
    {
        if (!LogDetailPanel.IsVisible) return;

        LogDetailPanel.IsVisible = false;
        DetailDivider.IsVisible  = false;
        LogDetailCardsHost.Children.Clear();
        // Снять выделение — иначе повторный клик по той же строке не даст SelectionChanged.
        LogGrid.SelectedItem = null;
    }

    /// <summary>
    /// Карточки полей строки: сперва то же, что в колонках, потом то, чему в колонках места нет.
    /// Пустые поля не показываются вовсе: «Сценарий: —» у подтверждения направления читалось бы
    /// как потеря, а не как «неприменимо».
    /// </summary>
    private void BuildLogDetailCards(SecurityEntry entry)
    {
        LogDetailCardsHost.Children.Clear();

        foreach (var (key, header, _) in SecurityLogColumns)
            AddLogDetailCard(header, SecurityLogCellText(key, entry),
                             key == "Allowed" ? (entry.Allowed ? "BrushGreen" : "BrushRed") : null);

        if (entry.Scenario.Length > 0)  AddLogDetailCard("Сценарий", entry.Scenario);
        else if (entry.Event == SecurityEvent.Send)
            AddLogDetailCard("Сценарий", "нет: запуск сделан руками");

        if (entry.Confirmed.Length > 0) AddLogDetailCard("Направление подтвердил", entry.Confirmed);
        if (entry.Fields.Length > 0)    AddLogDetailCard("Поля", entry.Fields);

        if (entry.Rows > 0 || entry.Bytes > 0)
            AddLogDetailCard("Объем", $"записей: {entry.Rows:N0} · {SecurityLogSize(entry.Bytes)}");

        if (entry.BodyHash.Length > 0)
            AddLogDetailCard(SecurityLogHashCaption(entry.Event), entry.BodyHash);

        AddLogDetailCard("Пояснение",
                         entry.Explanation.Length > 0 ? entry.Explanation : "Без объяснения.");
    }

    // Карточка поля: подпись сверху, значение под ней. Значение переносится по словам — пояснение
    // и отпечаток длиннее строки, а обрезать в панели, которую открыли ради подробностей, нечего.
    private void AddLogDetailCard(string name, string value, string? valueBrushKey = null)
    {
        var label = new TextBlock
        {
            Text       = name,
            FontSize   = (double)this.FindResource("FontSizeSmall")!,
            Foreground = (IBrush)this.FindResource("BrushSecondaryText")!,
        };

        var text = new TextBlock
        {
            Text         = value,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 2, 0, 0),
        };
        if (valueBrushKey is not null)
            text.Foreground = Brush(valueBrushKey, valueBrushKey == "BrushGreen" ? "#34C759" : "#FF3B30");

        LogDetailCardsHost.Children.Add(new Border
        {
            Background   = (IBrush)this.FindResource("BrushBg")!,
            CornerRadius = (CornerRadius)this.FindResource("RadiusBase")!,
            Padding      = new Thickness(10, 8),
            Child        = new StackPanel { Children = { label, text } },
        });
    }

    private static string SecurityLogHashCaption(SecurityEvent kind) => kind switch
    {
        SecurityEvent.Export => "Отпечаток файла",
        SecurityEvent.Copy   => "Отпечаток текста",
        _                    => "Отпечаток тела",
    };

    private static string SecurityLogSize(long bytes) =>
        bytes < 1024 ? $"{bytes} Б"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:N1} КБ"
        : $"{bytes / (1024.0 * 1024):N1} МБ";

    /// <summary>
    /// Строка текстом — чтобы переслать. Секрета в ней нет: адрес лежит в журнале уже
    /// замаскированным, а от тела остался один отпечаток.
    /// </summary>
    private void CopyLogRow()
    {
        if (LogGrid.SelectedItem is not SecurityEntry entry) return;

        var text = string.Join("\n", LogDetailCardsHost.Children
            .OfType<Border>()
            .Select(card => card.Child as StackPanel)
            .Where(stack => stack is not null)
            .Select(stack => (stack!.Children[0] as TextBlock)?.Text + ": "
                           + (stack.Children[1] as TextBlock)?.Text));

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }
}
