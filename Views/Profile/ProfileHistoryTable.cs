using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.Controls.Components;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.ViewModels.ConstructorDb;
using ChangeLogRow = SmartBase.Database.Repositories.ConstructorRepository.ChangeLogRow;

namespace SmartBase.Views.Profile;

/// <summary>
/// «История изменений» — таблица журнала выбранной базы в стиле таблицы конструктора: та же
/// рисованная сетка (<see cref="ServiceGrid"/>: те же линии и шапка) и то же окно фильтра и
/// сортировки в заголовке колонки (<see cref="ColumnFilterPanel"/>: каскад фильтров, автоотметка
/// найденного, Enter). Выбор базы — в навбаре (NavbarProfile), только среди баз с допуском. Строки
/// подгружаются keyset-страницами по 200 при прокрутке к концу (GetChangeLogPageAsync, отдельное
/// подключение) — миллионы строк не фризят UI. Фильтры и сортировка считаются в SQL.
/// </summary>
public partial class ProfileView
{
    private const int HistoryPageSize = 200;

    // Колонки журнала: ключ (как в ChangeLogQuery), заголовок и иконка типа. Ширина
    // у всех одинаковая — звездная, окно делится поровну. Иконка — та же, что у поля
    // в панели деталей: колонка и карточка одного поля обязаны читаться как одно целое
    // (в «Диагностике» ровно так же — LogsDetailFields).
    private static readonly (string Key, string Header, string IconKey)[] HistoryColumns =
    [
        ("user",       "Пользователь", "IconUser"),
        ("action",     "Действие",     "IconPencil"),
        ("type",       "Тип",          "IconBlocks"),
        ("object",     "Объект",       "IconFieldText"),
        ("collection", "Коллекция",    "IconCollection"),
        ("date",       "Дата и время", "IconClock"),
    ];

    private List<DatabaseManager> _historyDbs = [];
    private DatabaseManager? _historyDb;
    private bool _historyLoading;
    private bool _historyEnd;
    // Поколение запроса: смена базы/фильтра во время чтения страницы обесценивает результат.
    private int _historyGen;
    // Прочитанные строки в порядке показа: последняя — keyset-курсор следующей страницы.
    private readonly List<ChangeLogRow> _historyRows = [];

    // key колонки → набор разрешенных СЫРЫХ значений; нет ключа = фильтра нет (как в TableView).
    private readonly Dictionary<string, HashSet<string>> _historyFilters = [];
    private string? _historySortKey;
    private bool _historySortAsc;

    // ── Панель ────────────────────────────────────────────────────────────────

    private void RebuildHistoryPanel()
    {
        _historyDbs = GrantedHistoryDbs();

        // Список баз — в навбаре; выбор сохраняется, если база еще смонтирована и доступна.
        var keepDbId = _historyDb?.DbId;
        var names = _historyDbs.Select(DbChoice).ToList();
        var idx = keepDbId is int id ? _historyDbs.FindIndex(d => d.DbId == id) : -1;
        HeaderBar.SetHistoryDbs(names, idx >= 0 ? idx : (names.Count > 0 ? 0 : -1));

        if (HistoryGrid.Columns.Count == 0)
        {
            BuildHistoryColumns();
            HistoryGrid.NearEnd         += (_, _) => _ = LoadMoreHistoryAsync();
            HistoryGrid.FilterRequested += (_, e) => ShowHistoryFilterFlyout(e.Anchor, e.Key);
        }
        HookHistoryDetail();
        OnHistoryDbChanged();
    }

    // Базы, чью историю можно смотреть, — только те, к содержимому которых есть допуск. Историю
    // базы, откуда человека убрали или где у него отозвали допуск, он не видит так же, как ее
    // данные; вернули допуск — снова видны все строки, и свои, и чужие.
    private List<DatabaseManager> GrantedHistoryDbs() =>
        _workspace?.Mounted.Where(m => m.Permissions.AccessGranted).ToList() ?? [];

    /// <summary>
    /// Доступ к базам поменялся на ходу (допуск отозвали или вернули, базу подключили или
    /// отключили): открытая история следует за ним сразу, а не при следующем заходе в раздел.
    /// Набор баз прежний — таблицу под руками не перечитываем.
    /// </summary>
    private void RefreshHistoryAccess()
    {
        if (!HistoryPanel.IsVisible) return;
        if (GrantedHistoryDbs().SequenceEqual(_historyDbs)) return;
        RebuildHistoryPanel();
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
        HistoryGrid.RefreshHeader();
        ReloadHistoryRows();
    }

    // Перечитать строки с текущими фильтрами/сортировкой (скролл возвращается к началу).
    private void ReloadHistoryRows()
    {
        // Выбранная строка после смены базы/фильтра могла исчезнуть — закрываем детали.
        CloseHistoryDetail();
        _historyGen++;
        _historyRows.Clear();
        HistoryGrid.Clear();
        _historyEnd = _historyDb is null;
        HistoryEmptyText.IsVisible = false;
        if (_historyDb is null) { ShowHistoryEmptyText(); return; }
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
                Filters    = HistoryFiltersSnapshot(),
                SortColumn = _historySortKey,
                SortAsc    = _historySortAsc,
                AfterId    = last?.Id,
                AfterValue = last is null || _historySortKey is null
                    ? null
                    : ConstructorRepository.ChangeLogSortValue(_historySortKey, last),
            };
            var page = await requestDb.Constructor.GetChangeLogPageAsync(query, HistoryPageSize);

            // Пока читали страницу, могли переключить базу/фильтр — результат уже не наш.
            if (gen != _historyGen) return;

            var first = _historyRows.Count;
            _historyRows.AddRange(page);
            HistoryGrid.AppendRows(page.Select((row, i) => TableRowVm.Static(first + i + 1, key => HistoryCellText(key, row), row)));
            if (page.Count < HistoryPageSize) _historyEnd = true;
            ShowHistoryEmptyText();
        }
        catch (Exception ex)
        {
            // Страница не прочиталась (база недоступна, файл занят): таблица остается с тем, что
            // уже показано, а монитор доступности узнает о пропаже базы.
            DbErrors.HandleRead(ex, requestDb.Constructor.DbPath);
        }
        finally
        {
            _historyLoading = false;
            // Пока шло чтение, таблицу перезапросили (база, фильтр, сортировка): первая страница
            // нового запроса не стартовала — чтение было занято. Догоняем, иначе таблица так и
            // осталась бы пустой.
            if (gen != _historyGen) _ = LoadMoreHistoryAsync();
        }
    }

    // Пустая таблица объясняет себя: нет доступных баз, фильтры ничего не пропустили или журнал пуст.
    private void ShowHistoryEmptyText()
    {
        HistoryEmptyText.Text = _historyDb is null        ? "Нет баз данных, к которым у вас есть доступ."
                              : _historyFilters.Count > 0 ? "Нет записей, подходящих под фильтры."
                              : "Записей в журнале пока нет.";
        HistoryEmptyText.IsVisible = _historyRows.Count == 0;
    }

    private Dictionary<string, IReadOnlyCollection<string>> HistoryFiltersSnapshot() =>
        _historyFilters.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value);

    // ── Выгрузка в CSV ────────────────────────────────────────────────────────
    //
    // Кнопка в навбаре раздела. Выгружается ровно то, что показано: та же база, те же
    // фильтры и сортировка; строки читаются в фоне (ReadChangeLogCsvRowsIsolated) —
    // журнал на миллионы строк не поднимается в память.

    private bool _historyExporting;

    private async Task ExportHistoryToCsv()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        // Подсчет строк на большом журнале занимает секунды, и все это время окон еще нет —
        // второй клик по кнопке запустил бы вторую выгрузку и второе окно подтверждения.
        if (_historyExporting) return;
        if (_historyDb is null)
        {
            await ConfirmDialog.Info("Сначала выберите базу данных в списке справа.").ShowDialog(owner);
            return;
        }

        // Снимок состояния экрана ДО ожидания: пока считаются строки, пользователь может
        // сменить базу или фильтр — тогда окно подтверждения назвало бы одну базу,
        // а выгрузились бы данные другой.
        var db     = _historyDb;
        var gen    = _historyGen;
        var repo   = db.Constructor;
        var dbName = string.IsNullOrWhiteSpace(db.Name) ? "База данных" : db.Name;
        var query  = new ConstructorRepository.ChangeLogQuery
        {
            Filters = _historyFilters.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value),
            SortColumn = _historySortKey,
            SortAsc    = _historySortAsc,
        };
        var filtered = query.Filters.Count > 0;

        _historyExporting = true;
        try
        {
            long total;
            try
            {
                total = await repo.CountChangeLogAsync(query);
            }
            catch (Exception ex) when (DbErrors.Handle(ex, db.Path))
            {
                return;   // тост «база недоступна» уже показан
            }

            // Пока считали, могли переключить базу/фильтр — выгружать снимок молча нельзя.
            if (gen != _historyGen)
            {
                await ConfirmDialog.Info(
                        "Список изменился, пока считались строки. Откройте выгрузку заново.")
                    .ShowDialog(owner);
                return;
            }

            await CsvExportRunner.Run(owner, new CsvExportRunner.Job(
                Title:           "Выгрузка журнала",
                What:            $"История изменений базы «{dbName}»",
                TotalRows:       total,
                Filtered:        filtered,
                DefaultFileName: CsvExportRunner.FileName($"История изменений — {dbName}", DateTime.Now),
                Headers:         ConstructorRepository.ChangeLogCsvHeaders,
                Rows:            ct => repo.ReadChangeLogCsvRowsIsolated(query, ct)));
        }
        finally { _historyExporting = false; }
    }

    // ── Колонки ───────────────────────────────────────────────────────────────

    // Колонки-текст поровну от ширины окна, не уже 110; в шапке — иконка поля и кнопка фильтра.
    // «Объект» с «до → после» обрезается чаще других — полный текст в подсказке ячейки.
    private void BuildHistoryColumns()
    {
        HistoryGrid.SetColumns(HistoryColumns.Select(def => new TableColumnDef
        {
            Key           = def.Key,
            Header        = def.Header,
            FieldType     = "text",
            IsStarWidth   = true,
            MinWidth      = 110,
            HeaderIconKey = def.IconKey,
            CellTip       = def.Key == "object" ? vm => vm["object"] : null,
        }));
        // Активный фильтр или сортировка — синяя воронка вместо шеврона в шапке.
        HistoryGrid.IsColumnFiltered = key => _historyFilters.ContainsKey(key) || _historySortKey == key;
    }

    private static Geometry HistoryIcon(string key) =>
        Application.Current!.TryFindResource(key, out var res) && res is Geometry g
            ? g
            : throw new KeyNotFoundException($"Icon resource '{key}' not found in Icons.axaml");

    private static double HistoryIconSize(string key) =>
        Application.Current!.TryFindResource(key, out var r) && r is double d ? d : 16;

    // ── Ячейки ────────────────────────────────────────────────────────────────

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

    // Подписи и текст объекта — из общего ChangeLogLabels (Core): тот же текст
    // в таблице и в выгрузке CSV.
    private static string HistoryActionLabel(string action) => ChangeLogLabels.ActionLabel(action);

    private static string HistoryTypeLabel(string targetType) => ChangeLogLabels.TypeLabel(targetType);

    // Объект строки: правки с «до → после» показывают готовый Detail («„А" → „Б"»,
    // «Договор 12 (Телефон: «123» → «456»)»); иначе имя цели; пакетные операции —
    // «10 000 записей», дубль/удаление коллекции с записями — «Имя (N записей)».
    private static string HistoryObjectText(ChangeLogRow row) =>
        ChangeLogLabels.ObjectText(row.TargetType, row.TargetName, row.Count, row.Detail);

    // ISO UTC → локальное «дд.MM.гггг HH:mm».
    private static string FormatHistoryTs(string iso) => ChangeLogLabels.FormatTs(iso);

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

    // ── Фильтр и сортировка колонок (общее окно, как в таблице конструктора) ──

    private void ShowHistoryFilterFlyout(Control anchor, string key)
    {
        if (_historyDb is not { } db) return;

        // Снимок фильтров остальных колонок — на UI-потоке: список значений считается в фоне
        // по строкам, прошедшим их (каскад), а живой словарь фон не читает.
        var others = ColumnCascade.Others(_historyFilters, key);
        var date   = key == "date";

        ColumnFilterPanel.Show(this, anchor, new ColumnFilterSpec
        {
            Key        = key,
            AscLabel   = date ? "Сначала старые" : "От А до Я",
            DescLabel  = date ? "Сначала новые"  : "От Я до А",
            Sort       = _historySortKey == key ? _historySortAsc : null,
            Filter     = _historyFilters.GetValueOrDefault(key),
            Display    = raw => HistoryDisplayValue(key, raw),
            IsDate     = date,
            LoadValues = () => db.Constructor.GetChangeLogCountsAsync(key, others),
            LoadFailed = ex => DbErrors.HandleRead(ex, db.Constructor.DbPath),
            Apply      = result =>
            {
                // Окно открывали у одной базы, а «Готово» нажали, когда в навбаре уже другая, —
                // значения чужого журнала в фильтр этой базы не кладем.
                if (!ReferenceEquals(db, _historyDb)) return;
                result.ApplyTo(_historyFilters, ref _historySortKey, ref _historySortAsc);
                HistoryGrid.RefreshHeader();
                ReloadHistoryRows();
            },
        });
    }
}
