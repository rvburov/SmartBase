using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;
using SmartBase.Logging;
using SmartBase.Utils;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.DrawnGrid;
using SmartBase.ViewModels.ConstructorDb;
using LogRow = SmartBase.Logging.LogReader.LogRow;
using Path = Avalonia.Controls.Shapes.Path;

namespace SmartBase.Views.Settings;

/// <summary>
/// «Диагностика» — таблица журнала logs.db (Serilog) с панелью деталей выбранной строки.
/// Раздел «Настройки» (единый файл на приложение — системная, а не пользовательская вещь,
/// поэтому здесь, а не в «Профиле»). Таблица — рисованная сетка служебных таблиц
/// (<see cref="ServiceGrid"/>): колонки описаны определениями, строки — <see cref="TableRowVm.Static"/>
/// над строкой журнала. Фильтр и сортировка в заголовке колонки — то же окно, что у таблицы
/// конструктора (<see cref="ColumnFilterPanel"/>: каскад фильтров, автоотметка найденного, Enter);
/// данные читаются через <see cref="LogReader"/> (raw SQL по logs.db). Строки подгружаются
/// keyset-страницами по 200 при прокрутке к концу.
/// </summary>
public partial class SettingsView
{
    private const int LogsPageSize = 200;

    // Колонки лога: ключ (как в LogQuery), заголовок и иконка. Ширина звездная — окно делится поровну.
    // Иконка — та же, что у поля в панели деталей: колонка и карточка одного поля читаются как одно целое.
    private static readonly (string Key, string Header, string IconKey)[] LogsColumns =
    [
        ("date",      "Время",     "IconClock"),
        ("level",     "Уровень",   "IconFieldStatus"),
        ("message",   "Сообщение", "IconMessageWarning"),
        ("exception", "Ошибка",    "IconTriangleAlert"),
    ];

    private readonly LogReader _logReader = new(AppSettings.LogsDbPath);
    private bool _logsLoading;
    private bool _logsEnd;
    // Поколение запроса: смена фильтра во время чтения страницы обесценивает результат.
    private int _logsGen;
    // Прочитанные строки в порядке показа: последняя — keyset-курсор следующей страницы.
    private readonly List<LogRow> _logsRows = [];

    private readonly Dictionary<string, HashSet<string>> _logsFilters = [];
    private string? _logsSortKey;
    private bool _logsSortAsc;

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
        if (LogsGrid.Columns.Count == 0) BuildLogsColumns();
        if (!_logsSelectionHooked)
        {
            LogsGrid.SelectionChanged += OnLogsSelectionChanged;
            LogsGrid.NearEnd          += (_, _) => _ = LoadMoreLogsAsync();
            LogsGrid.FilterRequested  += (_, e) => ShowLogsFilterFlyout(e.Anchor, e.Key);
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
        LogsGrid.RefreshHeader();
        ReloadLogsRows();
    }

    // Перечитать строки с текущими фильтрами/сортировкой (скролл возвращается к началу).
    private void ReloadLogsRows()
    {
        // Выбранная строка после смены фильтра могла исчезнуть — закрываем детали.
        CloseLogsDetail();
        _logsGen++;
        _logsRows.Clear();
        LogsGrid.Clear();
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
                    : LogReader.SortValue(_logsSortKey, last),
            };
            var page = await _logReader.GetPageAsync(query, LogsPageSize);

            // Пока читали страницу, могли переключить фильтр — результат уже не наш.
            if (gen != _logsGen) return;

            var first = _logsRows.Count;
            _logsRows.AddRange(page);
            LogsGrid.AppendRows(page.Select((row, i) => TableRowVm.Static(first + i + 1, key => LogsCellText(key, row), row)));
            if (page.Count < LogsPageSize) _logsEnd = true;
            // Пустая таблица объясняет себя: фильтры ничего не пропустили или журнал пуст.
            LogsEmptyText.Text      = _logsFilters.Count > 0 ? "Нет записей, подходящих под фильтры." : "Записей пока нет.";
            LogsEmptyText.IsVisible = _logsRows.Count == 0;
        }
        catch (Exception ex)
        {
            // logs.db занят или поврежден — таблица остается с тем, что уже показано. Прокрутка
            // повторяет чтение на каждый шаг, поэтому запись не чаще раза в минуту.
            if (Diag.Throttle("logs-table-page", TimeSpan.FromMinutes(1)))
                Serilog.Log.Warning(ex, "Страница журнала диагностики не прочиталась");
        }
        finally
        {
            _logsLoading = false;
            // Пока шло чтение, таблицу перезапросили (фильтр, сортировка): первая страница нового
            // запроса не стартовала — чтение было занято. Догоняем, иначе таблица осталась бы пустой.
            if (gen != _logsGen) _ = LoadMoreLogsAsync();
        }
    }

    // ── Выгрузка в CSV ────────────────────────────────────────────────────────
    //
    // Кнопка в навбаре раздела. Выгружается ровно то, что показано: те же фильтры
    // и та же сортировка; строки читаются в фоне (ReadCsvRows), поэтому объем лога
    // ограничивает только диск.

    private bool _logsExporting;

    private async Task ExportLogsToCsv()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        // Подсчет строк на большом логе занимает секунды, и окон в это время еще нет —
        // второй клик по кнопке дал бы вторую выгрузку и второе окно подтверждения.
        if (_logsExporting) return;

        // Снимок состояния экрана ДО ожидания: фильтр могли поменять, пока считались строки.
        var query = new LogReader.LogQuery
        {
            Filters = _logsFilters.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value),
            SortColumn = _logsSortKey,
            SortAsc    = _logsSortAsc,
        };
        var filtered = query.Filters.Count > 0;

        _logsExporting = true;
        try
        {
            long total;
            try
            {
                total = await _logReader.CountAsync(query);
            }
            catch (Exception ex)
            {
                // logs.db читается мимо EF (raw SQL): поврежденный или занятый файл
                // не должен молча гасить кнопку.
                Serilog.Log.Warning(ex, "Не удалось посчитать строки диагностики для выгрузки");
                await ConfirmDialog.Info(
                        "Не удалось прочитать журнал диагностики. Попробуйте еще раз позже.")
                    .ShowDialog(owner);
                return;
            }

            await CsvExportRunner.Run(owner, new CsvExportRunner.Job(
                Title:           "Выгрузка диагностики",
                What:            "Диагностика",
                TotalRows:       total,
                Filtered:        filtered,
                DefaultFileName: CsvExportRunner.FileName("Диагностика", DateTime.Now),
                Headers:         LogReader.CsvHeaders,
                Rows:            ct => _logReader.ReadCsvRows(query, ct)));
        }
        finally { _logsExporting = false; }
    }

    // ── Колонки ───────────────────────────────────────────────────────────────

    // Колонки-текст поровну от ширины окна, не уже 110; в шапке — иконка поля и кнопка фильтра.
    // Последняя колонка («Ошибка») не рисует правую линию заголовка: ее правый край и так очерчен
    // краем таблицы или полосой, а вторая линия давала «двойную/толстую».
    private void BuildLogsColumns()
    {
        LogsGrid.SetColumns(LogsColumns.Select((def, i) => new TableColumnDef
        {
            Key             = def.Key,
            Header          = def.Header,
            FieldType       = "text",
            IsStarWidth     = true,
            MinWidth        = 110,
            HeaderIconKey   = def.IconKey,
            HeaderRightLine = i < LogsColumns.Length - 1,
        }));
        // Активный фильтр или сортировка — синяя воронка вместо шеврона в шапке.
        LogsGrid.IsColumnFiltered = key => _logsFilters.ContainsKey(key) || _logsSortKey == key;
    }

    private static Geometry LogsIcon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    private static double LogsIconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 16;

    // ── Ячейки ────────────────────────────────────────────────────────────────

    // Полный текст сообщения/ошибки показывает панель деталей (клик по строке), поэтому
    // подсказки с полным текстом у ячейки нет.
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

    // ── Фильтр и сортировка колонок (общее окно, как в таблице конструктора) ──

    private void ShowLogsFilterFlyout(Control anchor, string key)
    {
        // Снимок фильтров остальных колонок — на UI-потоке: список значений считается в фоне
        // по строкам, прошедшим их (каскад), а живой словарь фон не читает.
        var others = ColumnCascade.Others(_logsFilters, key);
        var date   = key == "date";

        ColumnFilterPanel.Show(this, anchor, new ColumnFilterSpec
        {
            Key        = key,
            AscLabel   = date ? "Сначала старые" : "От А до Я",
            DescLabel  = date ? "Сначала новые"  : "От Я до А",
            Sort       = _logsSortKey == key ? _logsSortAsc : null,
            Filter     = _logsFilters.GetValueOrDefault(key),
            Display    = raw => LogsDisplayValue(key, raw),
            IsDate     = date,
            LoadValues = () => _logReader.GetCountsAsync(key, others),
            LoadFailed = ex => Serilog.Log.Warning(ex, "Список значений фильтра диагностики не посчитан: колонка {Column:l}", key),
            Apply      = result =>
            {
                result.ApplyTo(_logsFilters, ref _logsSortKey, ref _logsSortAsc);
                LogsGrid.RefreshHeader();
                ReloadLogsRows();
            },
        });
    }

    // ── Детали выбранной строки (карточки полей + полный текст) ─────────────────
    //
    // Клик по строке таблицы открывает правую панель с карточками полей (как карточки
    // свойств «Страницы записи»); клик по карточке раскрывает полный текст поля снизу.
    // Крестик в шапке закрывает панель. Оформление зеркалит RecordPageView.

    private void OnLogsSelectionChanged(object? sender, EventArgs e)
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
        LogsGrid.ClearSelection();
    }

    private void CopyLogsDetailField()
    {
        var text = LogsDetailFullText.Text;
        if (string.IsNullOrEmpty(text)) return;
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    // ── Ручка-разделитель: делит карточки (сверху) и полный текст (снизу) ────────
    // По умолчанию строки делят место поровну (обе 1*). Перетаскивание задает строке
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
        // Не даем карточкам съесть весь текст: оставляем нижнему блоку запас (шапка+крошка+минимум).
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
        // Клик по карточке выделяет ее. Нажатие на ручку ⋮ обработчик грипа помечает
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
        // Названиям не даем съесть все значение: значению оставляем минимум места.
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
}
