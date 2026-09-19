using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Постраничное чтение раздела «Библиотека документов» — то, чем живет виртуальный список
/// (<c>Views/Library/DocVirtualList.cs</c>) на базах, которые целиком в память не берут.
///
/// Устройство проще, чем у задач: у них порядок «срок ↑, без даты в конец» не выражается одним
/// индексом, поэтому группа читается двумя склеенными потоками. Здесь порядок — порядок самой
/// коллекции (<c>Position, Id</c>), то есть ровно порядок индекса <c>db_records(CollectionId,
/// Position)</c>, а разные файловые столбцы одной записи идут подряд по <c>(f.Position, f.Id)</c>.
/// Один поток, один запрос, никаких проекций и новых таблиц.
///
/// Фильтр по типу документа — обычное SQL-условие, поэтому он ЛИСТАЕТСЯ: сколько бы типов ни
/// сняли галочками, недостижимых документов не появляется.
///
/// <b>Поиск — исключение и листается не будет.</b> Текст документов лежит не в базе, а в
/// отдельном кеше (<c>Database/DocIndex/DocTextIndex.cs</c>), и соединить два источника
/// постранично без временной таблицы нельзя. Поэтому поиск отдает ОГРАНИЧЕННУЮ выдачу и
/// целиком живет в памяти — тот же прием, что у поиска календаря
/// (<c>SearchCalendarEventsAsync</c>): найденное показывается сразу, а хвост за крышкой
/// отсекается явно, и контроллер об этом говорит.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>Что разделу нужно знать о коллекции, не читая ее документов.</summary>
    /// <param name="Total">Всего документов = записей × включенных файловых столбцов с непустым путем.</param>
    /// <param name="TypeFieldId">Поле «Тип документа» коллекции; null — его нет.</param>
    public sealed record DocCollectionStats(int Total, int? TypeFieldId);

    /// <summary>
    /// Сужение ленты: набор допустимых типов документа. <c>null</c> — фильтр выключен (все
    /// типы), ПУСТОЙ набор — не подходит ничего. Пустой тип записан пустой строкой — это
    /// значение «(Пусто)» в выпадашке.
    /// </summary>
    public sealed record DocView(IReadOnlySet<string>? Types)
    {
        public bool IsAll => Types is null;

        public static DocView All { get; } = new((IReadOnlySet<string>?)null);
    }

    // Условие вида и его параметры. Типы подставляются плейсхолдерами (@t0, @t1, …), а не
    // текстом: имена типов приходят от пользователя и в SQL попадать не должны.
    private static (string Where, List<string> Types) TypeFilterSql(DocView view)
    {
        if (view.Types is null) return ("1 = 1", []);
        if (view.Types.Count == 0) return ("1 = 0", []);

        var types = view.Types.ToList();
        var holes = string.Join(",", types.Select((_, i) => "@t" + i));
        // COALESCE: у документа без проставленного типа значения в db_field_values нет вовсе,
        // и подзапрос вернет NULL — в IN он не попадет ни при каком наборе. Пустой тип должен
        // сравниваться как пустая строка, иначе галочка «(Пусто)» ничего не находила бы.
        return ($"COALESCE({DocTypeValueExpr}, '') IN ({holes})", types);
    }

    // Значение «Типа документа» записи. Скалярный подзапрос, а не соединение: соединение
    // размножило бы строки, если ролью помечено два поля (копии коллекций), и идет сиком по
    // кластерному PK (RecordId, FieldId) таблицы db_field_values.
    private const string DocTypeValueExpr =
        "(SELECT vt.Value FROM db_field_values vt WHERE vt.RecordId = r.Id AND vt.FieldId = @type)";

    // Тело запроса ленты одной коллекции: ведет db_records по индексу (CollectionId, Position),
    // путь приезжает соединением сиком по PK, тип — подзапросом.
    private const string DocBody = @"
        FROM db_records r
        JOIN db_fields f       ON f.CollectionId = r.CollectionId
                              AND f.FieldType = 'file' AND f.IsDocFile = 1
        JOIN db_field_values v ON v.RecordId = r.Id AND v.FieldId = f.Id AND v.Value <> ''
        WHERE r.CollectionId = @c";

    private const string DocOrder = "r.Position, r.Id, f.Position, f.Id";

    private const string DocCols =
        "r.Id, f.Id, f.Name, r.CollectionId, r.Title, v.Value";

    /// <summary>
    /// Размеры всех библиотечных коллекций базы одним заходом: <c>id коллекции → </c>
    /// <see cref="DocCollectionStats"/>. Семантика <paramref name="activeIds"/> — как везде в
    /// разделах: null = все коллекции БД, ПУСТОЙ набор = ни одной.
    /// </summary>
    public Dictionary<int, DocCollectionStats> GetDocCollectionStats(ISet<int>? activeIds = null,
                                                                     DocView? view = null)
    {
        var result = new Dictionary<int, DocCollectionStats>();
        if (activeIds is { Count: 0 }) return result;

        var collections = GetDocCollectionIds().ToList();
        if (activeIds is not null) collections.RemoveAll(c => !activeIds.Contains(c));
        if (collections.Count == 0) return result;

        // Связь «коллекция → ее поле типа» снимается ОДИН раз через EF (db_fields мала) и
        // передается в запросы готовым id. Искать поле подзапросом на каждую строку нельзя: на
        // большой коллекции это столько же лишних выборок по db_fields, сколько в ней записей —
        // ровно тот дефект, который чинили в счетчике задач.
        var typeByCollection = RoleOrder(db.DbFields
                .Where(f => f.IsDocType && (f.FieldType == "select" || f.FieldType == "status")))
            .Select(f => new { f.CollectionId, f.Id })
            .ToList()
            .GroupBy(x => x.CollectionId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        foreach (var colId in collections)
        {
            var typeId = typeByCollection.GetValueOrDefault(colId, 0);
            var total  = CountDocs(colId, typeId, view ?? DocView.All, cap: 0);
            result[colId] = new DocCollectionStats(total, typeId > 0 ? typeId : null);
        }
        return result;
    }

    /// <summary>Фоновые размеры коллекций на ОТДЕЛЬНОМ подключении (общий контекст однопоточный).</summary>
    public Task<Dictionary<int, DocCollectionStats>> GetDocCollectionStatsAsync(
        ISet<int>? activeIds = null, DocView? view = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocCollectionStats(activeIds, view);
        });

    /// <summary>
    /// Сколько документов коллекции подходят виду. <paramref name="cap"/> &gt; 0 — считаем не
    /// дальше него (<c>COUNT(*) FROM (… LIMIT cap)</c>): цена такого счета не зависит от размера
    /// коллекции, и он годится для пилюль с количеством. Точный счет нужен только там, где от
    /// него зависит длина полосы прокрутки.
    /// </summary>
    public int CountDocs(int collectionId, int typeFieldId, DocView view, int cap = 0)
    {
        var (where, types) = TypeFilterSql(view);
        // Алиас capped обязателен: PostgreSQL не принимает подзапрос во FROM без алиаса
        // (SQLite прощает).
        var sql = cap > 0
            ? $"SELECT COUNT(*) FROM (SELECT 1 {DocBody} AND ({where}) LIMIT {cap}) AS capped"
            : $"SELECT COUNT(*) {DocBody} AND ({where})";

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParam(cmd, "@c").Value    = collectionId;
            AddParam(cmd, "@type").Value = typeFieldId > 0 ? typeFieldId : -1;
            for (var i = 0; i < types.Count; i++) AddParam(cmd, "@t" + i).Value = types[i];
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
        finally { if (opened) conn.Close(); }
    }

    /// <summary>
    /// Страница ленты одной коллекции, в порядке показа. Смещение — обычный OFFSET по индексу:
    /// пропускаемые строки не читаются из таблицы, но по дереву SQLite все равно шагает, поэтому
    /// прыжок ползунком в конец коллекции-миллионника стоит линейно от глубины. Ради этого
    /// страницы и грузятся в фоне с дебаунсом.
    /// </summary>
    public List<DocRecord> GetDocPage(int collectionId, int typeFieldId, DocView view,
                                      int skip, int take)
    {
        var result = new List<DocRecord>();
        if (take <= 0) return result;

        var (where, types) = TypeFilterSql(view);
        var sql = $@"SELECT {DocCols}, COALESCE({DocTypeValueExpr}, '')
                     {DocBody} AND ({where})
                     ORDER BY {DocOrder} LIMIT @take OFFSET @skip";

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParam(cmd, "@c").Value    = collectionId;
            AddParam(cmd, "@type").Value = typeFieldId > 0 ? typeFieldId : -1;
            AddParam(cmd, "@take").Value = take;
            AddParam(cmd, "@skip").Value = skip;
            for (var i = 0; i < types.Count; i++) AddParam(cmd, "@t" + i).Value = types[i];

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) result.Add(ReadDocPageRow(rdr));
        }
        finally { if (opened) conn.Close(); }

        StampCollection(result, collectionId);
        return result;
    }

    /// <summary>Фоновая страница на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<DocRecord>> GetDocPageAsync(int collectionId, int typeFieldId, DocView view,
                                                 int skip, int take) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocPage(collectionId, typeFieldId, view, skip, take);
        });

    /// <summary>Фоновый счет на ОТДЕЛЬНОМ подключении.</summary>
    public Task<int> CountDocsAsync(int collectionId, int typeFieldId, DocView view, int cap = 0) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountDocs(collectionId, typeFieldId, view, cap);
        });

    // Проекция страницы: без названия коллекции и цвета — их знает вызывающий (одна коллекция
    // на страницу), и тянуть их в каждую строку незачем.
    private static DocRecord ReadDocPageRow(System.Data.Common.DbDataReader rdr) => new()
    {
        Id            = rdr.GetInt32(0),
        FileFieldId   = rdr.GetInt32(1),
        FileFieldName = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
        CollectionId  = rdr.GetInt32(3),
        Title         = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
        Path          = rdr.IsDBNull(5) ? "" : rdr.GetString(5),
        DocType       = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
    };

    // Название и цвет коллекции — одним чтением на страницу, а не колонкой в каждой строке.
    private void StampCollection(List<DocRecord> rows, int collectionId)
    {
        if (rows.Count == 0) return;
        var info = db.DbCollections.Where(c => c.Id == collectionId)
                                   .Select(c => new { c.Name, c.Color })
                                   .FirstOrDefault();
        if (info is null) return;
        foreach (var r in rows)
        {
            r.CollectionName = info.Name;
            r.Color          = info.Color;
        }
    }

    // ── Поиск ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Поиск документов: по названию записи и имени файла (SQL <c>LIKE</c>) плюс по путям,
    /// найденным в текстовом кеше (<paramref name="paths"/>). Выдача ограничена
    /// <paramref name="limit"/> — см. пояснение в заголовке файла.
    ///
    /// SQLite сравнивает без учета регистра только латиницу, поэтому кириллица перебирается
    /// регистровыми вариантами — тот же прием, что в поиске календаря. Набор путей уходит в
    /// <c>IN</c> по индексу <c>db_field_values(FieldId, Value)</c>: это точный заход, а не скан.
    /// </summary>
    public List<DocRecord> SearchDocs(ISet<int>? activeIds, string query,
                                      IReadOnlyCollection<string> paths, int limit = 500)
    {
        var result = new List<DocRecord>();
        if (activeIds is { Count: 0 }) return result;

        query = (query ?? "").Trim();
        if (query.Length == 0 && paths.Count == 0) return result;

        var colFilter = activeIds is null
            ? ""
            : $" AND f.CollectionId IN ({string.Join(",", activeIds)})";

        var variants = query.Length > 0 ? SearchVariants(query) : [];
        var likes    = new List<string>();
        for (var i = 0; i < variants.Count; i++)
            likes.Add($"(r.Title LIKE @q{i} ESCAPE '\\' OR v.Value LIKE @q{i} ESCAPE '\\')");

        var pathList = paths.ToList();
        var byPath   = pathList.Count > 0
            ? $"v.Value IN ({string.Join(",", pathList.Select((_, i) => "@p" + i))})"
            : "";

        var conditions = new List<string>();
        if (likes.Count > 0)      conditions.Add("(" + string.Join(" OR ", likes) + ")");
        if (byPath.Length > 0)    conditions.Add(byPath);
        var where = string.Join(" OR ", conditions);

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT r.Id, f.Id, f.Name, f.CollectionId, c.Name, c.Color, r.Title, v.Value,
                       (SELECT vt.Value FROM db_field_values vt
                          JOIN db_fields ft ON ft.Id = vt.FieldId
                         WHERE vt.RecordId = r.Id AND ft.CollectionId = f.CollectionId
                           AND ft.IsDocType = 1
                         ORDER BY ft.Position, ft.Id LIMIT 1)
                FROM db_fields f
                JOIN db_collections c ON c.Id = f.CollectionId
                JOIN db_records r     ON r.CollectionId = f.CollectionId
                JOIN db_field_values v ON v.RecordId = r.Id AND v.FieldId = f.Id AND v.Value <> ''
                WHERE f.FieldType = 'file' AND f.IsDocFile = 1{colFilter} AND ({where})
                ORDER BY f.CollectionId, r.Position, r.Id, f.Position, f.Id
                LIMIT {Math.Max(1, limit)}";

            for (var i = 0; i < variants.Count; i++)
                AddParam(cmd, "@q" + i).Value = "%" + EscapeLike(variants[i]) + "%";
            for (var i = 0; i < pathList.Count; i++)
                AddParam(cmd, "@p" + i).Value = pathList[i];

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) result.Add(ReadDocRow(rdr));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Фоновый поиск на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<DocRecord>> SearchDocsAsync(ISet<int>? activeIds, string query,
                                                 IReadOnlyCollection<string> paths, int limit = 500) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).SearchDocs(activeIds, query, paths, limit);
        });

    /// <summary>
    /// Количества по типам документа для выпадашки навбара: значение → сколько документов.
    /// Пустой тип отдается пустой строкой — подпись «(Пусто)» рисует сам список.
    /// Считается по активным коллекциям, каждая своим полем типа.
    /// </summary>
    public List<(string Value, int Count)> GetDocTypeCounts(ISet<int>? activeIds = null)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        if (activeIds is { Count: 0 }) return [];

        var collections = GetDocCollectionIds().ToList();
        if (activeIds is not null) collections.RemoveAll(c => !activeIds.Contains(c));
        if (collections.Count == 0) return [];

        var typeByCollection = RoleOrder(db.DbFields
                .Where(f => f.IsDocType && (f.FieldType == "select" || f.FieldType == "status")))
            .Select(f => new { f.CollectionId, f.Id })
            .ToList()
            .GroupBy(x => x.CollectionId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            foreach (var colId in collections)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT COALESCE({DocTypeValueExpr}, '') AS T, COUNT(*)
                    {DocBody}
                    GROUP BY T";
                AddParam(cmd, "@c").Value    = colId;
                AddParam(cmd, "@type").Value = typeByCollection.GetValueOrDefault(colId, -1);

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var value = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                    totals[value] = totals.GetValueOrDefault(value) + rdr.GetInt32(1);
                }
            }
        }
        finally { if (opened) conn.Close(); }

        // Порядок как в выпадашке таблицы: по убыванию количества, пустое значение — последним.
        return [.. totals.OrderBy(p => p.Key.Length == 0)
                         .ThenByDescending(p => p.Value)
                         .ThenBy(p => p.Key, StringComparer.CurrentCulture)
                         .Select(p => (p.Key, p.Value))];
    }

    /// <summary>Фоновые количества по типам на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<(string Value, int Count)>> GetDocTypeCountsAsync(ISet<int>? activeIds = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocTypeCounts(activeIds);
        });
}
