using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Logging;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Страницы таблицы для виртуального списка строк (этап 1 плана drawn-grid.md, «Данные»).
///
/// Три режима показа, все — на индексах, без материализации коллекции:
/// <list type="bullet">
/// <item>обычный порядок (Position, Id) — keyset в обе стороны и смещение по покрывающему индексу
///   <c>(CollectionId, Position)</c>; смещение идет с того края, который ближе (прыжок в конец
///   9 млн строк — 1,5 мс вместо 700);</item>
/// <item>сортировка по колонке — keyset по индексу <c>(FieldId, Value)</c>: у таблицы без rowid в
///   хвосте индекса лежит RecordId, поэтому пара (Value, RecordId) — полный ключ сика. Вид состоит
///   из «головы» (непустые значения в порядке индекса) и «хвоста» (записи без значения, в порядке
///   таблицы — пустые всегда в конце, как у прежнего компаратора). Порядок головы — байтовый
///   порядок SQLite: для дат в ISO-каноне он хронологический, для текста — регистрозависимый (до
///   типизированных значений, Этап 4 дорожной карты); числа этим путем не сортируются
///   (<see cref="ColumnSort.IsIndexSortable"/>), для них — список id в фоне;</item>
/// <item>список id (фильтр, типизированная сортировка) — страницы срезом списка.</item>
/// </list>
/// Значения страницы читаются только для окна колонок (<paramref name="fieldIds"/>): на 1000
/// свойствах строка несет десятки значений, а видно 10–20 колонок; недостающие дочитываются при
/// прокрутке вбок (<see cref="LoadValuesAsync"/>). Все чтения — на отдельном подключении в фоне.
/// </summary>
public sealed partial class ConstructorRepository
{
    /// <summary>Записи одной страницы и, в режиме сортировки по индексу, значение колонки
    /// сортировки у каждой (курсор keyset соседних страниц; null у записи хвоста — без значения).
    /// <see cref="HeadCount"/> — длина головы, если чтение ее выяснило (голова кончилась внутри
    /// страницы) или знало.</summary>
    public sealed record RecordPage(List<DbRecord> Records, string?[]? SortValues = null, int? HeadCount = null)
    {
        public static readonly RecordPage Empty = new([]);
    }

    // Одна запись из читателя raw SQL (порядок колонок — RecordCols).
    private static DbRecord ReadRecord(DbDataReader rdr) => new()
    {
        Id           = rdr.GetInt32(0),
        CollectionId = rdr.GetInt32(1),
        CreatedAt    = rdr.IsDBNull(2) ? null : rdr.GetString(2),
        Position     = rdr.GetInt32(3),
        StartDt      = rdr.IsDBNull(4) ? null : rdr.GetString(4),
        EndDt        = rdr.IsDBNull(5) ? null : rdr.GetString(5),
        AllDay       = rdr.GetInt32(6) != 0,
        Title        = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
        UpdatedAt    = rdr.IsDBNull(8)  ? null : rdr.GetString(8),
        CreatedBy    = rdr.IsDBNull(9)  ? null : rdr.GetString(9),
        UpdatedBy    = rdr.IsDBNull(10) ? null : rdr.GetString(10),
        Color        = rdr.IsDBNull(11) ? null : rdr.GetString(11),
    };

    /// <summary>Связи записей страницы по полям-связям окна: fieldId → (recordId → цели). Записи
    /// без ребер в словаре поля отсутствуют.</summary>
    public sealed class PageLinks : Dictionary<int, Dictionary<int, List<(int Id, string Title)>>>;

    /// <summary>Страница таблицы одним фоновым чтением: записи, значения ячеек окна, оформление
    /// ячеек и связи по полям-связям окна (иначе их читала бы UI-нить на общем контексте).</summary>
    public sealed record PageData(RecordPage Page,
                                  Dictionary<int, Dictionary<int, string>> Values,
                                  Dictionary<int, Dictionary<string, RowStyle>> CellStyles,
                                  PageLinks Links);

    /// <summary>Дочитанные значения колонок и связи по дочитанным полям-связям.</summary>
    public sealed record TopUpData(Dictionary<int, Dictionary<int, string>> Values, PageLinks Links);

    // Связи записей по каждому полю-связи — на этом (изолированном) репозитории.
    private PageLinks ReadPageLinks(IReadOnlyList<int> recordIds, IReadOnlyList<int>? relationFieldIds)
    {
        var links = new PageLinks();
        if (relationFieldIds is null || recordIds.Count == 0) return links;
        foreach (var fid in relationFieldIds) links[fid] = GetLinksBatch(fid, recordIds);
        return links;
    }

    /// <summary>Окно колонок шире этого читается как «все поля»: матрица точечных проб
    /// (строки × поля) по первичному ключу дороже одного диапазона на запись.</summary>
    public const int FieldWindowLimit = 400;

    // ── Страница: записи + значения окна + оформление ─────────────────────────

    /// <summary>
    /// Страница таблицы на отдельном подключении в фоне. <paramref name="records"/> выбирает записи
    /// на изолированном репозитории — режим (keyset, смещение, срез списка id, сортировка по
    /// индексу) решает вызывающий, здесь только чтение. <paramref name="fieldIds"/> — окно колонок
    /// (null — все поля, пустой список — только записи без значений); <paramref name="relationFieldIds"/> —
    /// поля-связи окна, чьи ребра читаются здесь же для табличного кеша связей.
    /// </summary>
    public Task<PageData> LoadPageAsync(int collectionId, Func<ConstructorRepository, RecordPage> records,
                                        IReadOnlyList<int>? fieldIds, IReadOnlyList<int>? relationFieldIds = null) =>
        Task.Run(() =>
        {
            var t0 = GridTimings.Start();
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);
            GridTimings.Stop(t0, "страница (фон): подключение");

            var t1 = GridTimings.Start();
            var page   = records(repo);
            GridTimings.Stop(t1, "страница (фон): записи");
            var ids    = page.Records.Select(r => r.Id!.Value).ToList();
            var t2 = GridTimings.Start();
            var values = repo.GetFieldValuesBatch(ids, fieldIds);
            GridTimings.Stop(t2, "страница (фон): значения");
            var t3 = GridTimings.Start();
            var cells  = repo.GetCellStylesBatch(collectionId, ids);
            GridTimings.Stop(t3, "страница (фон): оформление ячеек");
            var t4 = GridTimings.Start();
            var links  = repo.ReadPageLinks(ids, relationFieldIds);
            GridTimings.Stop(t4, "страница (фон): связи");
            return new PageData(page, values, cells, links);
        });

    /// <summary>Дочитать значения колонок, которых не было в окне при загрузке страницы (прокрутка
    /// вбок): recordId → (fieldId → значение); записи без значения в словарь не попадают. Плюс связи
    /// по дочитанным полям-связям.</summary>
    public Task<TopUpData> LoadValuesAsync(IReadOnlyList<int> recordIds, IReadOnlyList<int> fieldIds,
                                           IReadOnlyList<int>? relationFieldIds = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);
            return new TopUpData(repo.GetFieldValuesBatch(recordIds, fieldIds), repo.ReadPageLinks(recordIds, relationFieldIds));
        });

    /// <summary>Значения записей только по полям <paramref name="fieldIds"/>; null или шире
    /// <see cref="FieldWindowLimit"/> — все поля. Сырой SQL: диапазон по первичному ключу
    /// (RecordId, FieldId) на запись, а при окне — точечные пробы; порциями по 200 записей. Это
    /// горячий путь каждой страницы, EF-вариант <see cref="GetFieldValuesBatch(IReadOnlyList{int})"/>
    /// с группировкой анонимных объектов здесь вдвое дороже.</summary>
    public Dictionary<int, Dictionary<int, string>> GetFieldValuesBatch(IReadOnlyList<int> recordIds,
                                                                        IReadOnlyList<int>? fieldIds)
    {
        var result = new Dictionary<int, Dictionary<int, string>>();
        if (recordIds.Count == 0 || fieldIds is { Count: 0 }) return result;
        var allFields = fieldIds is null || fieldIds.Count > FieldWindowLimit;

        var conn   = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            var fieldFilter = allFields ? "" : $" AND FieldId IN ({string.Join(",", fieldIds!.Select((_, i) => $"@f{i}"))})";
            const int Chunk = 200;
            for (var start = 0; start < recordIds.Count; start += Chunk)
            {
                var take  = Math.Min(Chunk, recordIds.Count - start);
                var names = string.Join(",", Enumerable.Range(0, take).Select(i => $"@i{i}"));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT RecordId, FieldId, Value FROM db_field_values WHERE RecordId IN ({names}){fieldFilter}";
                for (var i = 0; i < take; i++) AddParam(cmd, $"@i{i}").Value = recordIds[start + i];
                if (!allFields)
                    for (var i = 0; i < fieldIds!.Count; i++) AddParam(cmd, $"@f{i}").Value = fieldIds[i];

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var rid = rdr.GetInt32(0);
                    if (!result.TryGetValue(rid, out var byField)) result[rid] = byField = [];
                    byField[rdr.GetInt32(1)] = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                }
            }
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    // ── Сортировка по индексу (FieldId, Value): голова и хвост ────────────────

    // Фильтр скрытых записей по RecordId (для запросов от таблицы значений, где нет алиаса записи).
    private static string HiddenFilterByRecordId(bool excludeHidden, string recordIdExpr) => excludeHidden
        ? $" AND NOT EXISTS (SELECT 1 FROM record_display d WHERE d.CollectionId = @c AND d.RecordId = {recordIdExpr} AND d.Visible = 0)"
        : "";

    /// <summary>
    /// Страница «головы» сортированного вида: записи с НЕПУСТЫМ значением поля в порядке индекса
    /// (Value, RecordId), <paramref name="asc"/> — направление. Режим — по параметрам: keyset после
    /// курсора (<paramref name="afterValue"/>, <paramref name="afterId"/>) — следующая страница в
    /// порядке показа; keyset до курсора (<paramref name="beforeValue"/>, <paramref name="beforeId"/>) —
    /// страница выше при прокрутке вверх; иначе смещение <paramref name="skip"/> по покрывающему
    /// индексу, а при известной длине головы <paramref name="headCount"/> — с ближнего края.
    /// Возвращает записи в порядке показа и значение сортировки у каждой.
    /// </summary>
    public RecordPage GetRecordsSortedHead(int collectionId, int fieldId, bool asc, bool excludeHidden, int take,
                                           string? afterValue, int? afterId, string? beforeValue, int? beforeId,
                                           int skip = 0, int? headCount = null)
    {
        if (take <= 0) return RecordPage.Empty;
        var hidden = HiddenFilterByRecordId(excludeHidden, "fv.RecordId");
        var args   = new List<(string, object)> { ("@f", fieldId) };
        if (excludeHidden) args.Add(("@c", collectionId));

        // Порядок индекса «в направлении показа» (forward) и против него.
        string Order(string alias, bool forward) =>
            asc == forward ? $"{alias}.Value, {alias}.RecordId" : $"{alias}.Value DESC, {alias}.RecordId DESC";

        if (afterValue is not null && afterId is int aid)
        {
            args.Add(("@v", afterValue)); args.Add(("@id", aid)); args.Add(("@take", take));
            return ReadSortedRows($@"SELECT {RecordCols}, fv.Value FROM db_field_values fv
                                     JOIN db_records r ON r.Id = fv.RecordId
                                     WHERE fv.FieldId = @f AND fv.Value <> '' AND (fv.Value, fv.RecordId) {(asc ? ">" : "<")} (@v, @id){hidden}
                                     ORDER BY {Order("fv", true)} LIMIT @take", reverse: false, args);
        }
        if (beforeValue is not null && beforeId is int bid)
        {
            args.Add(("@v", beforeValue)); args.Add(("@id", bid)); args.Add(("@take", take));
            return ReadSortedRows($@"SELECT {RecordCols}, fv.Value FROM db_field_values fv
                                     JOIN db_records r ON r.Id = fv.RecordId
                                     WHERE fv.FieldId = @f AND fv.Value <> '' AND (fv.Value, fv.RecordId) {(asc ? "<" : ">")} (@v, @id){hidden}
                                     ORDER BY {Order("fv", false)} LIMIT @take", reverse: true, args);
        }

        // Смещение: подзапрос по покрывающему индексу отбирает только (RecordId, Value) — записи
        // пропускаемых строк не читаются; с конца головы, если так ближе.
        var fromEnd = headCount is int h && skip > h / 2;
        var offset  = fromEnd ? Math.Max(0, headCount!.Value - skip - take) : skip;
        var limit   = fromEnd ? Math.Min(take, Math.Max(0, headCount!.Value - skip)) : take;
        if (limit <= 0) return RecordPage.Empty;
        args.Add(("@take", limit)); args.Add(("@skip", offset));
        return ReadSortedRows($@"SELECT {RecordCols}, page.Value FROM
                                   (SELECT fv.RecordId, fv.Value FROM db_field_values fv
                                    WHERE fv.FieldId = @f AND fv.Value <> ''{hidden}
                                    ORDER BY {Order("fv", !fromEnd)} LIMIT @take OFFSET @skip) page
                                 JOIN db_records r ON r.Id = page.RecordId
                                 ORDER BY {Order("page", !fromEnd)}", reverse: fromEnd, args);
    }

    /// <summary>
    /// Страница «хвоста» сортированного вида: записи БЕЗ непустого значения поля, в порядке
    /// таблицы (Position, Id) независимо от направления сортировки — пустые всегда в конце.
    /// Режимы как у <see cref="GetRecordsSortedHead"/>: keyset после/до курсора по (Position, Id),
    /// иначе смещение; при известных длинах хвоста и головы — с ближнего края, а отбор «без
    /// значения» — списком id головы (<c>NOT IN</c>), когда голова короче пропускаемой части.
    /// </summary>
    public List<DbRecord> GetRecordsWithoutValue(int collectionId, int fieldId, bool excludeHidden, int take,
                                                 int? afterPos, int? afterId, int? beforePos, int? beforeId,
                                                 int skip = 0, int? tailCount = null, int? headCount = null)
    {
        if (take <= 0) return [];
        var hidden = HiddenFilter(excludeHidden);
        var probe  = " AND NOT EXISTS (SELECT 1 FROM db_field_values v WHERE v.RecordId = r.Id AND v.FieldId = @f AND v.Value <> '')";
        var args   = new List<(string, object)> { ("@c", collectionId), ("@f", fieldId) };

        if (afterPos is int ap && afterId is int aid)
        {
            args.Add(("@p", ap)); args.Add(("@id", aid)); args.Add(("@take", take));
            return ReadRecordRows($@"SELECT {RecordCols} FROM db_records r
                                     WHERE r.CollectionId = @c{probe} AND (r.Position, r.Id) > (@p, @id){hidden}
                                     ORDER BY r.Position, r.Id LIMIT @take", args.ToArray());
        }
        if (beforePos is int bp && beforeId is int bid)
        {
            args.Add(("@p", bp)); args.Add(("@id", bid)); args.Add(("@take", take));
            var rows = ReadRecordRows($@"SELECT {RecordCols} FROM db_records r
                                         WHERE r.CollectionId = @c{probe} AND (r.Position, r.Id) < (@p, @id){hidden}
                                         ORDER BY r.Position DESC, r.Id DESC LIMIT @take", args.ToArray());
            rows.Reverse();
            return rows;
        }

        var fromEnd = tailCount is int t && skip > t / 2;
        var offset  = fromEnd ? Math.Max(0, tailCount!.Value - skip - take) : skip;
        var limit   = fromEnd ? Math.Min(take, Math.Max(0, tailCount!.Value - skip)) : take;
        if (limit <= 0) return [];
        // Пропускаемых строк больше, чем записей в голове — дешевле один раз собрать id головы.
        if (headCount is int hc && hc < offset)
            probe = " AND r.Id NOT IN (SELECT v.RecordId FROM db_field_values v WHERE v.FieldId = @f AND v.Value <> '')";
        args.Add(("@take", limit)); args.Add(("@skip", offset));
        var order = fromEnd ? "i.Position DESC, i.Id DESC" : "i.Position, i.Id";
        return ReadRecordRows($@"SELECT {RecordCols} FROM db_records r
                                 JOIN (SELECT i.Id FROM db_records i
                                       WHERE i.CollectionId = @c{probe.Replace("r.Id", "i.Id")}{HiddenFilter(excludeHidden, "i")}
                                       ORDER BY {order} LIMIT @take OFFSET @skip) page ON page.Id = r.Id
                                 ORDER BY r.Position, r.Id", args.ToArray());
    }

    /// <summary>Длина «головы» сортированного вида: сколько записей несут непустое значение поля.
    /// Скан диапазона индекса (FieldId, Value): на 9 млн значений — около 2 с, поэтому в UI
    /// зовется только через <see cref="CountNonEmptyValuesAsync"/>.</summary>
    public int CountNonEmptyValues(int collectionId, int fieldId, bool excludeHidden)
    {
        var args = new List<(string, object)> { ("@f", fieldId) };
        if (excludeHidden) args.Add(("@c", collectionId));
        return (int)ScalarSql($"SELECT COUNT(*) FROM db_field_values fv WHERE fv.FieldId = @f AND fv.Value <> ''" +
                              HiddenFilterByRecordId(excludeHidden, "fv.RecordId"), args.ToArray());
    }

    /// <summary><see cref="CountNonEmptyValues"/> на отдельном подключении в фоне.</summary>
    public Task<int> CountNonEmptyValuesAsync(int collectionId, int fieldId, bool excludeHidden, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var slow = Diag.Measure("длина головы сортировки", Diag.UiQuery,
                                          $"{SmartBase.Utils.DbErrors.Display(db.DbPath)}, коллекция {collectionId}, поле {fieldId}");
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountNonEmptyValues(collectionId, fieldId, excludeHidden);
        }, ct);

    // ── Счетчик записей без ожидания ─────────────────────────────────────────

    /// <summary>
    /// Оценка числа записей за O(1): позиция последней записи, деленная на шаг позиций
    /// (записи получают <c>(число + 1) × 1000</c>, перестановка переписывает тем же шагом).
    /// Завышена после удалений и не учитывает скрытые — годится только протяженности полосы
    /// прокрутки до прихода точного <see cref="CountRecordsAsync"/>: точный COUNT на 9 млн строк —
    /// 0,7–1,3 с, а со скрытыми записями — 3 с, и первый кадр таблицы его не ждет.
    /// </summary>
    public int EstimateRecordCount(int collectionId)
    {
        var last = ScalarSql("SELECT COALESCE((SELECT r.Position FROM db_records r WHERE r.CollectionId = @c " +
                             "ORDER BY r.Position DESC, r.Id DESC LIMIT 1), -1)", ("@c", collectionId));
        if (last < 0) return 0;
        return (int)Math.Max(1, last / 1000);
    }

    // Скаляр raw SQL на подключении этого контекста.
    private long ScalarSql(string sql, params (string Name, object Value)[] args)
    {
        var conn   = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) AddParam(cmd, name).Value = value;
            var v = cmd.ExecuteScalar();
            return v is null || v is DBNull ? 0 : Convert.ToInt64(v);
        }
        finally { if (opened) conn.Close(); }
    }

    // Читатель страницы головы: колонки записи + значение сортировки (индекс 12).
    private RecordPage ReadSortedRows(string sql, bool reverse, List<(string Name, object Value)> args)
    {
        var records = new List<DbRecord>();
        var values  = new List<string?>();
        var conn    = db.Database.GetDbConnection();
        var opened  = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) AddParam(cmd, name).Value = value;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                records.Add(ReadRecord(rdr));
                values.Add(rdr.IsDBNull(12) ? "" : rdr.GetString(12));
            }
        }
        finally { if (opened) conn.Close(); }

        if (reverse) { records.Reverse(); values.Reverse(); }
        return new RecordPage(records, values.ToArray());
    }

    // ── Отбор и сортировка в фоне (список id) ─────────────────────────────────

    /// <summary>
    /// Список id записей для показа с фильтрами и сортировкой — в фоне на отдельном подключении,
    /// с отменой. Отбор — <see cref="FilteredRecords"/> (как в окне фильтра); порядок:
    /// без сортировки — таблицы (Position, Id); «Название» и вычисляемые колонки — по колонке
    /// записи в SQL; связь — по показанному тексту (названия целей) прежним компаратором;
    /// число — типизированным ключом (пустые и нечисловые в конце); остальные типы — по значению
    /// в SQL (байтовый порядок, как у сортировки по индексу без фильтра). Устойчивость: равные
    /// значения идут в порядке таблицы.
    /// </summary>
    public Task<List<int>> GetFilteredRecordIdsAsync(int collectionId,
        IReadOnlyDictionary<string, HashSet<string>> filters, bool excludeHidden,
        string? sortKey, bool sortAsc, string? sortFieldType, CancellationToken ct)
    {
        // Снимки на вызывающем (UI) потоке: фон живые словари и реестр баз не трогает.
        var filtersCopy = filters.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
        var crossPaths  = CrossDbPathsForKeys(collectionId, filtersCopy.Keys.Append(sortKey ?? ""));
        var dbPath      = db.DbPath;

        return Task.Run(async () =>
        {
            using var slow = Diag.Measure("отбор и сортировка таблицы", Diag.UiQuery,
                                          $"{SmartBase.Utils.DbErrors.Display(dbPath)}, коллекция {collectionId}, фильтров {filtersCopy.Count}" +
                                          (sortKey is null ? "" : $", сортировка по {sortKey}"));
            await using var ctx = new AppDbContext(dbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);
            var ids  = await repo.FilteredIdsSortedAsync(collectionId, filtersCopy, excludeHidden, sortKey, sortAsc,
                                                         sortFieldType, ForeignResolver(crossPaths), ct);
            slow.Context += $", отобрано {ids.Count:N0}";
            return ids;
        }, ct);
    }

    private async Task<List<int>> FilteredIdsSortedAsync(int collectionId, Dictionary<string, HashSet<string>> filters,
        bool excludeHidden, string? sortKey, bool sortAsc, string? sortFieldType,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign, CancellationToken ct)
    {
        var q = FilteredRecords(collectionId, filters, excludeHidden, resolveForeign);

        if (sortKey is null)
            return await q.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);

        if (sortKey == TitleKey)
            return await OrderedIds(q, r => r.Title, sortAsc).ToListAsync(ct);

        var fieldType = sortFieldType ?? "text";
        if (FieldTypeInfo.IsComputed(fieldType))
        {
            return fieldType switch
            {
                "created_at" => await OrderedIds(q, r => r.CreatedAt, sortAsc).ToListAsync(ct),
                "updated_at" => await OrderedIds(q, r => r.UpdatedAt, sortAsc).ToListAsync(ct),
                "created_by" => await OrderedIds(q, r => r.CreatedBy, sortAsc).ToListAsync(ct),
                _            => await OrderedIds(q, r => r.UpdatedBy, sortAsc).ToListAsync(ct),
            };
        }

        if (!int.TryParse(sortKey, out var fid))
            return await q.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);

        if (fieldType == "relation")
        {
            // Как раньше: показанный текст ячейки (названия целей через разделитель), компаратор
            // текста; записи без связей — в конце. Устойчиво (OrderBy) — равные в порядке таблицы.
            var ordered = await q.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);
            var field   = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fid);
            var sep     = RelationConfig.FromConfig(field?.ConfigJson)?.SeparatorString ?? ", ";
            var titles  = RelationTitlesBySource(collectionId, fid, resolveForeign);
            ct.ThrowIfCancellationRequested();
            var cmp = ColumnSort.BuildComparer("text", sortAsc);
            return ordered.OrderBy(id => titles.TryGetValue(id, out var t) ? string.Join(sep, t) : "",
                                   Comparer<string>.Create((a, b) => cmp(a, b))).ToList();
        }

        if (fieldType == "number")
            return await NumberSortedIdsAsync(collectionId, q, fid, sortAsc, filters.Count == 0, excludeHidden, ct);

        // Текст и прочие строковые типы (дата в ISO-каноне тоже): порядок значения в SQL, пустые и
        // отсутствующие — в конце, равные — в порядке таблицы.
        var withValue = q.Select(r => new
        {
            r.Id, r.Position,
            Value = db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == fid).Select(v => v.Value).FirstOrDefault(),
        });
        // Отсутствующая строка значения (NULL) и пустая строка — один и тот же «хвост»: COALESCE,
        // иначе NULL и '' разошлись бы по направлению и сломали порядок таблицы внутри хвоста.
        var byEmpty = withValue.OrderBy(x => x.Value == null || x.Value == "" ? 1 : 0);
        var sorted  = sortAsc ? byEmpty.ThenBy(x => x.Value ?? "") : byEmpty.ThenByDescending(x => x.Value ?? "");
        return await sorted.ThenBy(x => x.Position).ThenBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
    }

    // Порядок по колонке самой записи (название, аудит): пустые в конце, направление, равные — в
    // порядке таблицы. Ключ «пусто» собирается из того же выражения, чтобы EF перевел его в SQL.
    private static IQueryable<int> OrderedIds(IQueryable<DbRecordEntity> q,
                                              Expression<Func<DbRecordEntity, string>> key, bool asc)
    {
        var empty = Expression.Lambda<Func<DbRecordEntity, int>>(
            Expression.Condition(Expression.Equal(key.Body, Expression.Constant("")),
                                 Expression.Constant(1), Expression.Constant(0)),
            key.Parameters);
        var byEmpty = q.OrderBy(empty);
        var sorted  = asc ? byEmpty.ThenBy(key) : byEmpty.ThenByDescending(key);
        return sorted.ThenBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id);
    }

    // Число: типизированный ключ в памяти. Без фильтров — сырой поток (Id, Value) по всей коллекции
    // (материализация 10 млн объектов EF заняла бы минуты); с фильтрами — проекция отобранных.
    private async Task<List<int>> NumberSortedIdsAsync(int collectionId, IQueryable<Entities.DbRecordEntity> q, int fid,
                                                       bool asc, bool wholeCollection, bool excludeHidden, CancellationToken ct)
    {
        var keys = new List<(double Key, int Order, int Id)>();
        if (wholeCollection)
        {
            var conn   = db.Database.GetDbConnection();
            var opened = conn.State != System.Data.ConnectionState.Open;
            if (opened) await conn.OpenAsync(ct);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"SELECT r.Id, fv.Value FROM db_records r
                                     LEFT JOIN db_field_values fv ON fv.RecordId = r.Id AND fv.FieldId = @f
                                     WHERE r.CollectionId = @c{HiddenFilter(excludeHidden)}
                                     ORDER BY r.Position, r.Id";
                AddParam(cmd, "@f").Value = fid;
                AddParam(cmd, "@c").Value = collectionId;
                // Читатель синхронный: мы уже на фоновом потоке, а асинхронное чтение по строке на
                // миллионах строк стоит заметно дороже; отмена проверяется каждые 10 тыс. строк.
                using var rdr = cmd.ExecuteReader();
                var order = 0;
                while (rdr.Read())
                {
                    keys.Add((ColumnSort.NumberKey(rdr.IsDBNull(1) ? "" : rdr.GetString(1)), order, rdr.GetInt32(0)));
                    if ((++order & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                }
            }
            finally { if (opened) await conn.CloseAsync(); }
        }
        else
        {
            var rows = await q.OrderBy(r => r.Position).ThenBy(r => r.Id)
                .Select(r => new
                {
                    r.Id,
                    Value = db.DbFieldValues.Where(v => v.RecordId == r.Id && v.FieldId == fid).Select(v => v.Value).FirstOrDefault(),
                })
                .ToListAsync(ct);
            for (var i = 0; i < rows.Count; i++) keys.Add((ColumnSort.NumberKey(rows[i].Value ?? ""), i, rows[i].Id));
        }
        ct.ThrowIfCancellationRequested();

        // Пустые и нечисловые (ключ — бесконечность) всегда в конце; среди чисел — направление;
        // равные — в порядке таблицы.
        keys.Sort((a, b) =>
        {
            var aEmpty = double.IsPositiveInfinity(a.Key);
            var bEmpty = double.IsPositiveInfinity(b.Key);
            if (aEmpty || bEmpty) return aEmpty == bEmpty ? a.Order.CompareTo(b.Order) : (aEmpty ? 1 : -1);
            var c = a.Key.CompareTo(b.Key);
            if (c != 0) return asc ? c : -c;
            return a.Order.CompareTo(b.Order);
        });
        return keys.Select(k => k.Id).ToList();
    }

    // Пути баз межбазовых связей для полей-связей среди ключей (UI-поток: реестр/общие репозитории
    // только отсюда). null — связей среди ключей нет, фон резолвер не получает.
    private Dictionary<int, string>? CrossDbPathsForKeys(int collectionId, IEnumerable<string> keys)
    {
        Dictionary<int, string>? crossPaths = null;
        foreach (var k in keys)
            if (int.TryParse(k, out var fid) && IsRelationField(fid))
            {
                crossPaths ??= new();
                foreach (var (dbId, path) in CrossDbPathsForField(collectionId, fid)) crossPaths[dbId] = path;
            }
        return crossPaths;
    }

    // Резолвер названий целей чужих баз для фонового потока: свежие изолированные подключения по
    // снятым заранее путям; путь не снят → null → «(база недоступна)», как в ячейке.
    private static Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? ForeignResolver(Dictionary<int, string>? crossPaths) =>
        crossPaths is null ? null
        : (dbId, ids) =>
        {
            if (!crossPaths.TryGetValue(dbId, out var path)) return null;
            using var fctx = new AppDbContext(path);
            fctx.ApplyPerformancePragmas();
            return new ConstructorRepository(fctx).GetRecordTitles(ids);
        };
}
