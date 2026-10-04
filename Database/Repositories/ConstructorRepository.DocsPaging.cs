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
/// <b>Страница читается от соседа, а не смещением.</b> Порядок ленты — ключ
/// (<see cref="DocCursor"/>), и страница «после известного документа» стоит одинаково на любой
/// глубине, тогда как OFFSET шагает по каждой пропущенной строке. Прыжок ползунком соседа не
/// имеет — тогда смещение считается от ближнего края потока или от ближнего известного
/// документа (<see cref="DocSeek"/>; выбор — <c>Views/Library/DocSeekPlan.cs</c>).
///
/// <b>Размер потока при фильтре типов — из гистограммы типов</b>
/// (<see cref="DocTypeHistogram"/>): она же дает числа выпадашки навбара, а фильтр ставится только
/// через выпадашку, поэтому к смене типов гистограмма уже посчитана и новый счет не нужен. Без
/// фильтра размер — точный счет (<see cref="CountDocs"/>): он идет покрывающим индексом и дешев.
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

    /// <summary>
    /// Сколько документов коллекции какого типа: значение «Типа документа» → число документов
    /// (пустой тип — пустая строка, как в <see cref="DocView"/>). Из нее без единого запроса
    /// получается и размер потока в любом виде фильтра (<see cref="CountFor"/>), и числа
    /// выпадашки навбара.
    /// </summary>
    /// <param name="TypeFieldId">Поле «Тип документа» коллекции; null — его нет.</param>
    public sealed record DocTypeHistogram(int? TypeFieldId, IReadOnlyDictionary<string, int> Counts)
    {
        public int Total => Counts.Values.Sum();

        /// <summary>Сколько документов подходит виду — сумма по отмеченным типам.</summary>
        public int CountFor(DocView view) =>
            view.Types is null ? Total : view.Types.Sum(t => Counts.GetValueOrDefault(t));
    }

    /// <summary>Откуда идти к странице потока.</summary>
    public enum DocSeekOrigin
    {
        /// <summary>Смещением от начала потока.</summary>
        Start,
        /// <summary>Смещением от конца: поток читается в обратном порядке, страница разворачивается.</summary>
        End,
        /// <summary>От известного документа вперед — keyset-курсор.</summary>
        After,
        /// <summary>От известного документа назад — прокрутка вверх после прыжка вглубь.</summary>
        Before,
    }

    /// <summary>
    /// Путь к странице потока: откуда идти и сколько документов пропустить от этой точки.
    /// </summary>
    /// <param name="Anchor">Документ-сосед для <see cref="DocSeekOrigin.After"/> и <see cref="DocSeekOrigin.Before"/>.</param>
    public readonly record struct DocSeek(DocSeekOrigin From, int Skip, DocCursor Anchor = default)
    {
        public static DocSeek FromStart(int skip) => new(DocSeekOrigin.Start, skip);
    }

    // Условие вида и его параметры. Типы подставляются плейсхолдерами (@t0, @t1, …), а не
    // текстом: имена типов приходят от пользователя и в SQL попадать не должны.
    private static (string Where, List<string> Types) TypeFilterSql(DocView view,
                                                                    string typeExpr = DocTypeValueExpr)
    {
        if (view.Types is null) return ("1 = 1", []);
        if (view.Types.Count == 0) return ("1 = 0", []);

        var types = view.Types.ToList();
        var holes = string.Join(",", types.Select((_, i) => "@t" + i));
        // COALESCE: у документа без проставленного типа значения в db_field_values нет вовсе,
        // и подзапрос вернет NULL — в IN он не попадет ни при каком наборе. Пустой тип должен
        // сравниваться как пустая строка, иначе галочка «(Пусто)» ничего не находила бы.
        return ($"COALESCE({typeExpr}, '') IN ({holes})", types);
    }

    // Значение «Типа документа» записи. Скалярный подзапрос, а не соединение: соединение
    // размножило бы строки, если ролью помечено два поля (копии коллекций), и идет сиком по
    // кластерному PK (RecordId, FieldId) таблицы db_field_values.
    private const string DocTypeValueExpr =
        "(SELECT vt.Value FROM db_field_values vt WHERE vt.RecordId = r.Id AND vt.FieldId = @type)";

    // То же, когда коллекция заранее не известна (поиск идет по всем отмеченным разом): поле
    // роли находится по коллекции файлового столбца f. Один и тот же текст стоит и в списке
    // колонок, и в условии вида — иначе тип, по которому фильтруют, и тип, который показывают,
    // однажды разошлись бы.
    private const string DocTypeAnyExpr = @"(SELECT vt.Value FROM db_field_values vt
                          JOIN db_fields ft ON ft.Id = vt.FieldId
                         WHERE vt.RecordId = r.Id AND ft.CollectionId = f.CollectionId
                           AND ft.IsDocType = 1
                         ORDER BY ft.Position, ft.Id LIMIT 1)";

    // Тело запроса ленты одной коллекции: ведет db_records по индексу (CollectionId, Position),
    // путь приезжает соединением сиком по PK, тип — подзапросом. Строка без пути — не документ:
    // показывать в библиотеке нечего (у задач наоборот — пустой исполнитель значит «никому не
    // роздано», и карточка нужна).
    private const string DocBody = @"
        FROM db_records r
        JOIN db_fields f       ON f.CollectionId = r.CollectionId
                              AND f.FieldType = 'file' AND f.IsDocFile = 1
        JOIN db_field_values v ON v.RecordId = r.Id AND v.FieldId = f.Id AND v.Value <> ''
        WHERE r.CollectionId = @c";

    private const string DocOrder     = "r.Position, r.Id, f.Position, f.Id";
    private const string DocOrderDesc = "r.Position DESC, r.Id DESC, f.Position DESC, f.Id DESC";

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

        var typeByCollection = DocTypeFieldsByCollection();

        foreach (var colId in collections)
        {
            var typeId = typeByCollection.GetValueOrDefault(colId, 0);
            var total  = CountDocs(colId, typeId, view ?? DocView.All, cap: 0);
            result[colId] = new DocCollectionStats(total, typeId > 0 ? typeId : null);
        }
        return result;
    }

    // Связь «коллекция → ее поле типа» снимается ОДИН раз через EF (db_fields мала) и
    // передается в запросы готовым id. Искать поле подзапросом на каждую строку нельзя: на
    // большой коллекции это столько же лишних выборок по db_fields, сколько в ней записей —
    // ровно тот дефект, который чинили в счетчике задач.
    private Dictionary<int, int> DocTypeFieldsByCollection() =>
        RoleOrder(db.DbFields
                .Where(f => f.IsDocType && (f.FieldType == "select" || f.FieldType == "status")))
            .Select(f => new { f.CollectionId, f.Id })
            .ToList()
            .GroupBy(x => x.CollectionId)
            .ToDictionary(g => g.Key, g => g.First().Id);

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

    /// <summary>Страница ленты одной коллекции смещением от начала потока, в порядке показа.</summary>
    public List<DocRecord> GetDocPage(int collectionId, int typeFieldId, DocView view,
                                      int skip, int take) =>
        GetDocPage(collectionId, typeFieldId, view, DocSeek.FromStart(skip), take);

    /// <summary>
    /// Страница ленты одной коллекции, в порядке показа, — по заданному пути
    /// (<see cref="DocSeek"/>). От соседа (<see cref="DocSeekOrigin.After"/>,
    /// <see cref="DocSeekOrigin.Before"/>) страница стоит одинаково на любой глубине; смещение
    /// шагает по каждой пропущенной строке, поэтому его и считают от ближнего края.
    /// </summary>
    public List<DocRecord> GetDocPage(int collectionId, int typeFieldId, DocView view,
                                      DocSeek seek, int take)
    {
        var result = new List<DocRecord>();
        if (take <= 0) return result;

        var (where, types) = TypeFilterSql(view);
        var backward = seek.From is DocSeekOrigin.End or DocSeekOrigin.Before;

        // Граница по r.Position есть у КАЖДОГО пути, а не только у курсора. Без нее планировщик
        // шел от индекса значений и сортировал всю коллекцию ради одной страницы: на 125 000
        // документов 75 мс на первую страницу. С ней он идет индексом db_records(CollectionId,
        // Position) в порядке показа и останавливается на LIMIT — меньше миллисекунды. Для
        // смещения от края граница пустая по смыслу (все позиции) и нужна только планировщику.
        var bound = seek.From switch
        {
            DocSeekOrigin.After  => $"({DocOrder}) > (@kp, @ki, @kfp, @kfi)",
            DocSeekOrigin.Before => $"({DocOrder}) < (@kp, @ki, @kfp, @kfi)",
            DocSeekOrigin.End    => "r.Position <= @edge",
            _                    => "r.Position >= @edge",
        };
        var sql = $@"SELECT {DocCols}, COALESCE({DocTypeValueExpr}, ''), r.Position, f.Position
                     {DocBody} AND ({where}) AND {bound}
                     ORDER BY {(backward ? DocOrderDesc : DocOrder)} LIMIT @take OFFSET @skip";

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
            AddParam(cmd, "@skip").Value = Math.Max(0, seek.Skip);
            if (seek.From is DocSeekOrigin.After or DocSeekOrigin.Before)
            {
                AddParam(cmd, "@kp").Value  = seek.Anchor.RecordPosition;
                AddParam(cmd, "@ki").Value  = seek.Anchor.RecordId;
                AddParam(cmd, "@kfp").Value = seek.Anchor.FieldPosition;
                AddParam(cmd, "@kfi").Value = seek.Anchor.FieldId;
            }
            else AddParam(cmd, "@edge").Value = backward ? int.MaxValue : int.MinValue;
            for (var i = 0; i < types.Count; i++) AddParam(cmd, "@t" + i).Value = types[i];

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) result.Add(ReadDocPageRow(rdr));
        }
        finally { if (opened) conn.Close(); }

        // Назад читали в обратном порядке — страница разворачивается в порядок показа.
        if (backward) result.Reverse();
        StampCollection(result, collectionId);
        return result;
    }

    /// <summary>Фоновая страница смещением от начала на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<DocRecord>> GetDocPageAsync(int collectionId, int typeFieldId, DocView view,
                                                 int skip, int take) =>
        GetDocPageAsync(collectionId, typeFieldId, view, DocSeek.FromStart(skip), take);

    /// <summary>Фоновая страница по заданному пути на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<DocRecord>> GetDocPageAsync(int collectionId, int typeFieldId, DocView view,
                                                 DocSeek seek, int take) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocPage(collectionId, typeFieldId, view, seek, take);
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
        Cursor        = new DocCursor(rdr.GetInt32(7), rdr.GetInt32(0), rdr.GetInt32(8), rdr.GetInt32(1)),
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
    /// <paramref name="limit"/> — см. пояснение в заголовке файла; потолок на раздел делит
    /// между базами зовущий (<c>Views/Library/DocSearchBudget.cs</c>).
    ///
    /// Фильтр типов (<paramref name="view"/>) стоит ВНУТРИ запроса, а не поверх выдачи: иначе
    /// база возвращала бы ровно потолок строк, фильтр отсеивал половину, и признак обрезки
    /// молчал бы там, где выдача обрезана, — а находки нужного типа за потолком не доезжали бы
    /// вовсе.
    ///
    /// SQLite сравнивает без учета регистра только латиницу, поэтому кириллица перебирается
    /// регистровыми вариантами — тот же прием, что в поиске календаря. PostgreSQL в <c>LIKE</c>
    /// учитывает регистр ВСЕГДА, и у латиницы тоже: по «gost» не находилось «GoSt», а файл
    /// «MiXeD-Case.pdf» по «mixed-case» не находился вовсе. Поэтому там сравнение — <c>ILIKE</c>:
    /// латиницу он сводит при любой локали сервера, кириллицу — при локали, которая ее знает, а
    /// варианты остаются ради сервера с локалью «C». Набор путей уходит в <c>IN</c> по индексу
    /// <c>db_field_values(FieldId, Value)</c>: это точный заход, а не скан.
    ///
    /// «Ё» не отличается от «е» — как и в поиске по тексту (<c>DocTextIndex.YoSpellings</c>):
    /// «Учёт материалов» находится по «учет», а «Учет» — по «учёт». Сложение — <c>REPLACE</c>,
    /// он есть у обоих провайдеров; вариантами здесь не обойтись — их число росло бы вдвое с
    /// каждым «е» запроса.
    ///
    /// Пути приходят КЛЮЧАМИ кеша (нормализованными), а в поле записи лежит написание самого
    /// человека, поэтому каждый путь разворачивается в свои написания
    /// (<c>DocTextIndex.Spellings</c>) — без этого поиск по содержимому молча терял бы находки.
    ///
    /// Поля документов берутся списком ЗАРАНЕЕ, и обход идет только по их значениям. Без списка
    /// планировщик выбирал полный проход индекса <c>db_field_values</c> — значения ВСЕХ полей
    /// всех коллекций — и сравнивал подстроку раньше, чем отсеивал поля не из раздела: на
    /// 500 000 документов (2,5 млн значений) 2,9 с против 0,65 с.
    /// </summary>
    public List<DocRecord> SearchDocs(ISet<int>? activeIds, string query,
                                      IReadOnlyCollection<string> paths, DocView? view = null,
                                      int limit = 500)
    {
        var result = new List<DocRecord>();
        if (activeIds is { Count: 0 }) return result;
        if (view is { Types.Count: 0 }) return result;   // ни одного типа — искать негде

        query = (query ?? "").Trim();
        if (query.Length == 0 && paths.Count == 0) return result;

        var docFields = db.DbFields.AsNoTracking()
            .Where(f => f.FieldType == "file" && f.IsDocFile)
            .Select(f => new { f.Id, f.CollectionId })
            .ToList()
            .Where(f => activeIds is null || activeIds.Contains(f.CollectionId))
            .Select(f => f.Id)
            .ToList();
        if (docFields.Count == 0) return result;

        // «ё» — как «е», и в запросе, и в названии с путем: так же, как поиск по тексту. Без «е»
        // в запросе сложение ничего не меняет, и колонки сравниваются как есть.
        var folded   = query.Replace('ё', 'е').Replace('Ё', 'Е');
        var withYe   = folded.Contains('е') || folded.Contains('Е');
        string Fold(string column) => withYe ? $"REPLACE(REPLACE({column}, 'ё', 'е'), 'Ё', 'Е')" : column;

        var variants = folded.Length > 0 ? SearchVariants(folded) : [];
        var like     = LikeOp(db.IsPostgres);
        var likes    = new List<string>();
        for (var i = 0; i < variants.Count; i++)
            likes.Add($"({Fold("r.Title")} {like} @q{i} ESCAPE '\\' OR {Fold("v.Value")} {like} @q{i} ESCAPE '\\')");

        var pathList = paths.SelectMany(DocIndex.DocTextIndex.Spellings)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
        var byPath   = pathList.Count > 0
            ? $"v.Value IN ({string.Join(",", pathList.Select((_, i) => "@p" + i))})"
            : "";

        var conditions = new List<string>();
        if (likes.Count > 0)      conditions.Add("(" + string.Join(" OR ", likes) + ")");
        if (byPath.Length > 0)    conditions.Add(byPath);
        var where = string.Join(" OR ", conditions);
        var (typeWhere, types) = TypeFilterSql(view ?? DocView.All, DocTypeAnyExpr);

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT r.Id, f.Id, f.Name, f.CollectionId, c.Name, c.Color, r.Title, v.Value,
                       {DocTypeAnyExpr}
                FROM db_field_values v
                JOIN db_fields f      ON f.Id = v.FieldId
                JOIN db_collections c ON c.Id = f.CollectionId
                JOIN db_records r     ON r.Id = v.RecordId AND r.CollectionId = f.CollectionId
                WHERE v.FieldId IN ({string.Join(",", docFields)}) AND v.Value <> '' AND ({where})
                  AND {typeWhere}
                ORDER BY f.CollectionId, r.Position, r.Id, f.Position, f.Id
                LIMIT {Math.Max(1, limit)}";

            for (var i = 0; i < variants.Count; i++)
                AddParam(cmd, "@q" + i).Value = "%" + EscapeLike(variants[i]) + "%";
            for (var i = 0; i < pathList.Count; i++)
                AddParam(cmd, "@p" + i).Value = pathList[i];
            for (var i = 0; i < types.Count; i++)
                AddParam(cmd, "@t" + i).Value = types[i];

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) result.Add(ReadDocRow(rdr));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Фоновый поиск на ОТДЕЛЬНОМ подключении.</summary>
    public Task<List<DocRecord>> SearchDocsAsync(ISet<int>? activeIds, string query,
                                                 IReadOnlyCollection<string> paths,
                                                 DocView? view = null, int limit = 500) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).SearchDocs(activeIds, query, paths, view, limit);
        });

    // Строка выдачи поиска. В отличие от страницы ленты (ReadDocPageRow) выдача идет по всем
    // отмеченным коллекциям сразу, поэтому название и цвет коллекции едут в каждой строке.
    private static DocRecord ReadDocRow(System.Data.Common.DbDataReader rdr) => new()
    {
        Id             = rdr.GetInt32(0),
        FileFieldId    = rdr.GetInt32(1),
        FileFieldName  = rdr.IsDBNull(2) ? "" : rdr.GetString(2),
        CollectionId   = rdr.GetInt32(3),
        CollectionName = rdr.IsDBNull(4) ? "" : rdr.GetString(4),
        Color          = rdr.IsDBNull(5) ? "#007AFF" : rdr.GetString(5),
        Title          = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
        Path           = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
        DocType        = rdr.IsDBNull(8) ? "" : rdr.GetString(8),
    };

    /// <summary>
    /// Количества по типам документа для выпадашки навбара: значение → сколько документов.
    /// Пустой тип отдается пустой строкой — подпись «(Пусто)» рисует сам список.
    /// Считается по активным коллекциям, каждая своим полем типа.
    /// </summary>
    public List<(string Value, int Count)> GetDocTypeCounts(ISet<int>? activeIds = null) =>
        MergeTypeCounts(GetDocTypeHistograms(activeIds).Values);

    /// <summary>
    /// Сложить гистограммы коллекций в список выпадашки. Порядок как в выпадашке таблицы: по
    /// убыванию количества, пустое значение — последним.
    /// </summary>
    public static List<(string Value, int Count)> MergeTypeCounts(IEnumerable<DocTypeHistogram> histograms)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var h in histograms)
            foreach (var (value, count) in h.Counts)
                totals[value] = totals.GetValueOrDefault(value) + count;

        return [.. totals.OrderBy(p => p.Key.Length == 0)
                         .ThenByDescending(p => p.Value)
                         .ThenBy(p => p.Key, StringComparer.CurrentCulture)
                         .Select(p => (p.Key, p.Value))];
    }

    /// <summary>
    /// Гистограммы типов по активным коллекциям: id коллекции → сколько документов какого типа.
    /// Семантика <paramref name="activeIds"/> — как везде в разделах: null = все коллекции БД,
    /// ПУСТОЙ набор = ни одной.
    ///
    /// Счет — полный проход коллекции с поиском типа у каждой записи (на 125 000 документов
    /// около четверти секунды), поэтому зовущий держит посчитанное у себя и передает его в
    /// <paramref name="known"/>: такие коллекции не пересчитываются, а возвращаются как есть.
    /// Когда посчитанное устарело, решает зовущий, — репозиторий о правках не знает.
    /// </summary>
    public Dictionary<int, DocTypeHistogram> GetDocTypeHistograms(
        ISet<int>? activeIds = null, IReadOnlyDictionary<int, DocTypeHistogram>? known = null)
    {
        var result = new Dictionary<int, DocTypeHistogram>();
        if (activeIds is { Count: 0 }) return result;

        var collections = GetDocCollectionIds().ToList();
        if (activeIds is not null) collections.RemoveAll(c => !activeIds.Contains(c));
        if (collections.Count == 0) return result;

        var typeByCollection = DocTypeFieldsByCollection();

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            foreach (var colId in collections)
            {
                if (known is not null && known.TryGetValue(colId, out var ready))
                {
                    result[colId] = ready;
                    continue;
                }

                var typeId = typeByCollection.GetValueOrDefault(colId, 0);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT COALESCE({DocTypeValueExpr}, '') AS T, COUNT(*)
                    {DocBody}
                    GROUP BY T";
                AddParam(cmd, "@c").Value    = colId;
                AddParam(cmd, "@type").Value = typeId > 0 ? typeId : -1;

                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var value = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                    counts[value] = counts.GetValueOrDefault(value) + Convert.ToInt32(rdr.GetValue(1));
                }
                result[colId] = new DocTypeHistogram(typeId > 0 ? typeId : null, counts);
            }
        }
        finally { if (opened) conn.Close(); }

        return result;
    }

    /// <summary>Фоновые гистограммы типов на ОТДЕЛЬНОМ подключении.</summary>
    public Task<Dictionary<int, DocTypeHistogram>> GetDocTypeHistogramsAsync(
        ISet<int>? activeIds = null, IReadOnlyDictionary<int, DocTypeHistogram>? known = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetDocTypeHistograms(activeIds, known);
        });
}
