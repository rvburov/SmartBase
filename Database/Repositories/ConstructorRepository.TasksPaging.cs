using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Постраничное чтение раздела «Задачи» — то, чем живёт виртуальный список
/// (<c>Views/Tasks/TaskVirtualList.cs</c>) на базах, которые целиком в память не берут.
///
/// Страницами читается ЛЮБОЙ вид раздела, а не только «Все задачи»: поиск и каждый из пяти
/// фильтров уходят в SQL тем же условием, каким они описаны в <c>TaskFilters.Matches</c>.
/// Крышки, за которой задачи становились недостижимы, в разделе больше нет — как в таблице
/// конструктора, где фильтр и сортировка тоже считаются в базе, а строки подтягиваются
/// прокруткой.
///
/// Здесь нет ни одной новой таблицы: задача = запись коллекции один-в-один (исполнитель у
/// коллекции один), поэтому и размер группы, и порядок в ней ложатся на уже существующие
/// индексы — <c>db_records(CollectionId, Position)</c> и <c>calendar_index(CollectionId, StartIso)</c>.
///
/// Порядок внутри группы тот же, что и в памяти (<c>TaskGrouping.ByDueThenId</c>): срок ↑,
/// «без даты» в конец, тай-брейк по <c>Id</c>. Одним индексным запросом он не выражается,
/// поэтому группа читается ДВУМЯ последовательными потоками, склеенными встык, — ровно как
/// календарь склеивает фазы all-day → timed:
/// <list type="bullet">
/// <item>«со сроком» — <c>calendar_index</c> по полю-дате коллекции, <c>ORDER BY StartIso, RecordId</c>.
/// Это в точности порядок индекса <c>(CollectionId, StartIso)</c>: у таблицы WITHOUT ROWID хвостом
/// каждой записи индекса идёт PK <c>(RecordId, FieldId)</c>, поэтому сортировать нечего;</item>
/// <item>«без срока» — записи коллекции, у которых строки в проекции нет, <c>ORDER BY Id</c>.</item>
/// </list>
/// Границу задаёт <see cref="TaskViewSize.Dated"/>: строки [0, Dated) берутся из первого потока.
///
/// Коллекция с НЕСКОЛЬКИМИ календарными полями-датами читается общим путём (см.
/// <see cref="TaskCollectionStats.MultiDate"/>): там срок — наименьшая из дат записи, двумя
/// индексными потоками это не выражается. Такая коллекция сортируется запросом и на больших
/// объёмах листается медленнее, зато ни одна её задача не становится недостижимой.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>
    /// Что разделу нужно знать о коллекции, не читая её задач.
    /// </summary>
    /// <param name="Total">Всего задач (= записей коллекции с полем-исполнителем).</param>
    /// <param name="Done">Из них в статусе «Выполнено».</param>
    /// <param name="DueFieldId">
    /// Единственное календарное поле-дата коллекции — им задаётся срок. null — либо полей-дат
    /// нет вовсе (все задачи «без срока»), либо их несколько (см. <paramref name="MultiDate"/>).
    /// </param>
    /// <param name="MultiDate">
    /// У коллекции НЕСКОЛЬКО календарных полей-дат: сроком служит наименьшая, и порядок
    /// перестаёт быть порядком индекса. Читается общим путём — медленнее, но полностью.
    /// </param>
    public sealed record TaskCollectionStats(int Total, int Done, int? DueFieldId, bool MultiDate);

    /// <summary>Размер группы в конкретном виде: сколько задач ему подходит и сколько из них
    /// со сроком (граница между потоками чтения).</summary>
    /// <param name="Total">Подходят виду.</param>
    /// <param name="Dated">Из них со сроком — длина первого потока.</param>
    /// <param name="Done">Из них выполнены — знаменатель и числитель индикатора в заголовке.</param>
    public readonly record struct TaskViewSize(int Total, int Dated, int Done);

    /// <summary>
    /// Вид раздела: правило быстрого фильтра плюс поиск. Одна структура на счёт и на страницы —
    /// иначе счётчик и список считали бы по разным правилам.
    /// </summary>
    /// <param name="Kind">Значение <c>TaskFilterKind</c> строкой: репозиторий не знает о слое показа.</param>
    /// <param name="Search">Подстрока названия; пусто — поиск выключен.</param>
    /// <param name="MeVariants">
    /// Чем может быть записан текущий пользователь в поле-исполнителе. В памяти сравнение идёт
    /// без учёта регистра, а SQLite так кириллицу не сравнивает — варианты считает вызывающий.
    /// </param>
    /// <param name="Now">«Сейчас» — от него считаются «Сегодня» и просрочка.</param>
    public sealed record TaskView(string Kind, string Search,
                                  IReadOnlyCollection<string> MeVariants, DateTime Now)
    {
        /// <summary>Вид без сужения: все задачи, поиск пуст.</summary>
        public bool IsAll => Kind == "All" && Search.Length == 0;
    }

    /// <summary>
    /// Размеры и форма всех задачных коллекций базы одним заходом: <c>id коллекции → </c>
    /// <see cref="TaskCollectionStats"/>. Семантика <paramref name="activeIds"/> — как везде в
    /// разделе: null = все коллекции БД, ПУСТОЙ набор = ни одной.
    /// </summary>
    public Dictionary<int, TaskCollectionStats> GetTaskCollectionStats(ISet<int>? activeIds = null)
    {
        var counts = CountTasksByCollection(activeIds);
        if (counts.Count == 0) return [];

        // Календарные поля-даты по коллекциям. db_fields — маленькая таблица, читаем через EF:
        // так же поступает вся ролевая разметка раздела.
        var dateFields = db.DbFields
            .Where(f => f.FieldType == "date" && f.IsCalStart)
            .Select(f => new { f.CollectionId, f.Id })
            .ToList()
            .GroupBy(x => x.CollectionId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var result = new Dictionary<int, TaskCollectionStats>(counts.Count);
        foreach (var (colId, c) in counts)
        {
            var ids = dateFields.GetValueOrDefault(colId) ?? [];
            result[colId] = new TaskCollectionStats(
                Total:      c.Total,
                Done:       c.Done,
                DueFieldId: ids.Count == 1 ? ids[0] : null,
                MultiDate:  ids.Count > 1);
        }
        return result;
    }

    /// <summary>Фоновые размеры коллекций на ОТДЕЛЬНОМ подключении.</summary>
    public Task<Dictionary<int, TaskCollectionStats>> GetTaskCollectionStatsAsync(ISet<int>? activeIds = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetTaskCollectionStats(activeIds);
        });

    // ── Условия вида ─────────────────────────────────────────────────────────

    // Роли коллекции, нужные условиям: исполнитель и статус. Снимаются ОДИН раз на коллекцию и
    // передаются в запросы готовыми id — искать поле подзапросом на каждую строку нельзя,
    // на большой коллекции это столько же лишних выборок по db_fields, сколько в ней записей.
    private (int? Exec, int? Status) TaskRoleIds(int collectionId) =>
        (TaskExecFieldId(collectionId),
         RoleOrder(db.DbFields.Where(f => f.CollectionId == collectionId && f.IsTaskStatus))
             .Select(f => (int?)f.Id).FirstOrDefault());

    // Регистровые варианты подстроки поиска: SQLite сравнивает без учёта регистра только
    // латиницу, поэтому кириллицу перебираем вариантами — тот же приём, что в поиске календаря.
    private static List<string> SearchVariants(string query)
    {
        var lower = query.ToLowerInvariant();
        return [.. new HashSet<string>(StringComparer.Ordinal)
        {
            query, lower, query.ToUpperInvariant(),
            lower.Length > 0 ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower,
        }];
    }

    private static string EscapeLike(string q) =>
        q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Условие вида для одного потока. Возвращает <c>null</c>, если поток виду не подходит
    /// ВООБЩЕ — «Сегодня» и «Просрочено» требуют срока, «Без срока» его запрещает: такой поток
    /// не читается и не считается вовсе.
    /// </summary>
    /// <param name="dated">Поток «со сроком»: срок доступен прямыми колонками проекции.</param>
    /// <param name="due">Выражения границ срока: начало и конец (для потока «без срока» — null).</param>
    private static string? ViewCondition(TaskView view, bool dated, (string Start, string End)? due,
                                         int? statusFieldId)
    {
        var parts = new List<string>();

        switch (view.Kind)
        {
            case "Today":
                if (!dated || due is not { } t) return null;
                parts.Add($"substr({t.Start}, 1, 10) <= @today");
                parts.Add($"substr(COALESCE({t.End}, {t.Start}), 1, 10) >= @today");
                break;

            case "Overdue":
                if (!dated || due is not { } o) return null;
                parts.Add(statusFieldId is null ? "1 = 1" : "COALESCE(vs.Value, '') <> @done");
                parts.Add($"COALESCE({o.End}, {o.Start}) <= @now");
                break;

            case "NoDue":
                if (dated) return null;
                break;

            case "AssignedToMe":
                parts.Add(view.MeVariants.Count == 0
                    ? "1 = 0"
                    : $"COALESCE(ve.Value, '') IN ({string.Join(",", view.MeVariants.Select((_, i) => "@me" + i))})");
                break;

            case "Done":
                parts.Add(statusFieldId is null ? "1 = 0" : "COALESCE(vs.Value, '') = @done");
                break;
        }

        if (view.Search.Length > 0)
            parts.Add("(" + string.Join(" OR ",
                SearchVariants(view.Search).Select((_, i) => $"r.Title LIKE @q{i} ESCAPE '\\'")) + ")");

        return parts.Count == 0 ? "1 = 1" : string.Join(" AND ", parts);
    }

    // Нужны ли условию значения исполнителя/статуса — лишние соединения на каждую строку
    // добавлять незачем.
    private static bool NeedsExec(TaskView v)   => v.Kind == "AssignedToMe";
    private static bool NeedsStatus(TaskView v) => v.Kind is "Done" or "Overdue";
    private static bool NeedsRecord(TaskView v) => v.Search.Length > 0;

    private void BindView(System.Data.Common.DbCommand cmd, TaskView view, int collectionId)
    {
        AddParam(cmd, "@c").Value     = collectionId;
        AddParam(cmd, "@done").Value  = TaskStatusDone;
        AddParam(cmd, "@now").Value   = DateTime.SpecifyKind(view.Now, DateTimeKind.Unspecified).ToString("o");
        AddParam(cmd, "@today").Value = view.Now.ToString("yyyy-MM-dd");

        var i = 0;
        foreach (var v in view.MeVariants) AddParam(cmd, "@me" + i++).Value = v;

        var k = 0;
        foreach (var v in SearchVariants(view.Search)) AddParam(cmd, $"@q{k++}").Value = $"%{EscapeLike(v)}%";
    }

    // ── Счёт и страницы ──────────────────────────────────────────────────────

    /// <summary>
    /// Сколько задач коллекции подходит виду и сколько из них со сроком. Считается ПО БАЗЕ:
    /// по этим числам строится полоса прокрутки и заголовок группы, а значит крышки нигде нет.
    /// </summary>
    public TaskViewSize CountTaskView(int collectionId, TaskCollectionStats stats, TaskView view)
    {
        // Вид без сужения: числа уже посчитаны общим счётом коллекций, спрашивать нечего.
        if (view.IsAll && !stats.MultiDate)
            return new TaskViewSize(stats.Total, CountDated(collectionId, stats), stats.Done);

        var dated   = CountStream(collectionId, stats, view, dated: true, cap: 0);
        var undated = stats.MultiDate ? (0, 0) : CountStream(collectionId, stats, view, dated: false, cap: 0);
        return new TaskViewSize(dated.Total + undated.Item1, dated.Total, dated.Done + undated.Item2);
    }

    /// <summary>
    /// Сколько задач раздела подходит каждому правилу — для пилюль быстрого фильтра. Считается
    /// ПОД КРЫШКОЙ <paramref name="cap"/>: пилюля печатает «999+», точное число ей не нужно, а
    /// шесть полных проходов по коллекции-полумиллионнику стоили бы секунд. Размеры групп, по
    /// которым строится полоса прокрутки, считаются отдельно и ТОЧНО
    /// (<see cref="CountTaskView"/>) — крышка живёт только в пилюле.
    /// </summary>
    public Dictionary<string, int> CountTaskViews(Dictionary<int, TaskCollectionStats> stats,
                                                  IReadOnlyList<string> kinds, string search,
                                                  IReadOnlyCollection<string> meVariants,
                                                  DateTime now, int cap)
    {
        var result = new Dictionary<string, int>(kinds.Count);
        foreach (var kind in kinds)
        {
            var view = new TaskView(kind, search, meVariants, now);
            var sum  = 0;
            foreach (var (colId, s) in stats)
            {
                if (sum >= cap) break;                       // дальше считать незачем — уже «999+»
                var left = cap - sum;
                sum += CountStream(colId, s, view, dated: true, cap: left).Total;
                if (sum >= cap || s.MultiDate) continue;
                sum += CountStream(colId, s, view, dated: false, cap: cap - sum).Total;
            }
            result[kind] = Math.Min(sum, cap);
        }
        return result;
    }

    /// <summary>Фоновые счётчики пилюль на ОТДЕЛЬНОМ подключении.</summary>
    public Task<Dictionary<string, int>> CountTaskViewsAsync(
        Dictionary<int, TaskCollectionStats> stats, IReadOnlyList<string> kinds, string search,
        IReadOnlyCollection<string> meVariants, DateTime now, int cap) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountTaskViews(stats, kinds, search, meVariants, now, cap);
        });

    // Сколько записей коллекции имеют срок — граница потоков для вида «Все задачи».
    private int CountDated(int collectionId, TaskCollectionStats stats)
    {
        if (stats.DueFieldId is not int due) return 0;
        return (int)Scalar($@"SELECT COUNT(*) FROM calendar_index ci
                              WHERE ci.CollectionId = @c AND ci.FieldId = @due",
                           ("@c", collectionId), ("@due", due));
    }

    // cap > 0 — считаем не дальше него: `COUNT(*) FROM (… LIMIT cap)`. Цена такого счёта не
    // зависит от размера коллекции, а «выполнено» под крышкой не нужно (это счёт для пилюли).
    private (int Total, int Done) CountStream(int collectionId, TaskCollectionStats stats,
                                              TaskView view, bool dated, int cap)
    {
        if (cap < 0) return (0, 0);
        var parts = ViewParts(collectionId, stats, view, dated);
        if (!parts.Ok) return (0, 0);

        var sql = cap > 0
            ? $"SELECT COUNT(*), 0 FROM (SELECT 1 {parts.Body} LIMIT {cap})"
            : $"SELECT COUNT(*), SUM(CASE WHEN {parts.DoneExpr} = @done THEN 1 ELSE 0 END) {parts.Body}";

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            BindView(cmd, view, collectionId);
            if (stats.DueFieldId is int due) AddParam(cmd, "@due").Value = due;
            var (_, statusId) = TaskRoleIds(collectionId);
            AddParam(cmd, "@status").Value = statusId ?? -1;
            AddParam(cmd, "@exec").Value   = TaskExecFieldId(collectionId) ?? -1;

            using var rdr = cmd.ExecuteReader();
            return rdr.Read() ? (rdr.GetInt32(0), rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1)) : (0, 0);
        }
        finally { if (opened) conn.Close(); }
    }

    /// <summary>
    /// Id записей одной страницы группы, в порядке показа. <paramref name="skip"/> отсчитывается
    /// от начала ПОТОКА, а не группы: вызывающий сам решает по <see cref="TaskViewSize.Dated"/>,
    /// из какого потока берёт строки.
    ///
    /// Смещение — обычный OFFSET по индексу. Пропускаемые строки не читаются из таблицы, но по
    /// дереву SQLite всё равно шагает, поэтому прыжок ползунком в конец коллекции-миллионника
    /// стоит линейно от глубины — как и у «Расписания». Ради этого страницы и грузятся в фоне
    /// с дебаунсом.
    /// </summary>
    public List<int> GetTaskPageIds(int collectionId, TaskCollectionStats stats, TaskView view,
                                    bool dated, int skip, int take)
    {
        if (take <= 0) return [];
        var parts = ViewParts(collectionId, stats, view, dated);
        if (!parts.Ok) return [];
        var sql = $"SELECT {parts.IdExpr} {parts.Body} ORDER BY {parts.Order} LIMIT @take OFFSET @skip";

        var result = new List<int>(take);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            BindView(cmd, view, collectionId);
            AddParam(cmd, "@take").Value = take;
            AddParam(cmd, "@skip").Value = skip;
            if (stats.DueFieldId is int due) AddParam(cmd, "@due").Value = due;
            var (_, statusId) = TaskRoleIds(collectionId);
            AddParam(cmd, "@status").Value = statusId ?? -1;
            AddParam(cmd, "@exec").Value   = TaskExecFieldId(collectionId) ?? -1;

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) result.Add(rdr.GetInt32(0));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Части запроса потока — из них собираются и счёт, и страница, поэтому считают
    /// и показывают они заведомо одно и то же. <c>Ok = false</c> — поток виду не подходит вовсе
    /// («Сегодня» без срока, «Без срока» со сроком): его не читают и не считают.</summary>
    private readonly record struct ViewSql(string IdExpr, string Body, string Order, string DoneExpr, bool Ok);

    private static readonly ViewSql NoStream = new("", "", "", "", false);

    // Значение статуса записи для агрегата «сколько выполнено». Подзапрос по PK, а не соединение:
    // соединение размножило бы строки, если статусов у коллекции почему-то оказалось два.
    private const string DoneValueExpr =
        "COALESCE((SELECT v2.Value FROM db_field_values v2" +
        " WHERE v2.RecordId = {0} AND v2.FieldId = @status), '')";

    private ViewSql ViewParts(int collectionId, TaskCollectionStats stats, TaskView view, bool dated)
    {
        var (_, statusId) = TaskRoleIds(collectionId);

        // Коллекция с несколькими полями-датами: индексных потоков нет, читаем общим путём —
        // срок берётся подзапросом (наименьшая из дат записи), а порядок строит сама СУБД.
        // Медленнее, зато ни одна задача не становится недостижимой.
        if (stats.MultiDate)
        {
            if (!dated) return NoStream;   // весь набор отдаёт первый «поток»
            var where = ViewCondition(view, dated: true, (DueStartExpr, DueEndExpr), statusId)
                        ?? ViewCondition(view, dated: false, null, statusId);
            if (where is null) return NoStream;

            return new ViewSql(
                IdExpr:   "r.Id",
                Body:     $@"FROM db_records r
                             {Joins(view, "r.Id")}
                             WHERE r.CollectionId = @c AND ({where})",
                Order:    $"({DueStartExpr}) IS NULL, {DueStartExpr}, r.Id",
                DoneExpr: string.Format(DoneValueExpr, "r.Id"),
                Ok:       true);
        }

        // Поток «со сроком»: строки проекции по полю-дате коллекции. Срок — прямые колонки,
        // порядок — порядок индекса (CollectionId, StartIso) с PK-хвостом (RecordId, FieldId).
        if (dated)
        {
            if (stats.DueFieldId is null) return NoStream;
            var where = ViewCondition(view, dated: true, ("ci.StartIso", "ci.EndIso"), statusId);
            if (where is null) return NoStream;

            return new ViewSql(
                IdExpr:   "ci.RecordId",
                Body:     $@"FROM calendar_index ci
                             {Joins(view, "ci.RecordId")}
                             WHERE ci.CollectionId = @c AND ci.FieldId = @due AND ({where})",
                Order:    "ci.StartIso, ci.RecordId",
                DoneExpr: string.Format(DoneValueExpr, "ci.RecordId"),
                Ok:       true);
        }

        // Поток «без срока»: записи коллекции без строки в проекции по полю-дате.
        var cond = ViewCondition(view, dated: false, null, statusId);
        if (cond is null) return NoStream;

        var noDue = stats.DueFieldId is int d
            ? $" AND NOT EXISTS (SELECT 1 FROM calendar_index ci WHERE ci.RecordId = r.Id AND ci.FieldId = {d})"
            : "";

        return new ViewSql(
            IdExpr:   "r.Id",
            Body:     $@"FROM db_records r
                         {Joins(view, "r.Id")}
                         WHERE r.CollectionId = @c{noDue} AND ({cond})",
            Order:    "r.Id",
            DoneExpr: string.Format(DoneValueExpr, "r.Id"),
            Ok:       true);
    }

    // Соединения под условие вида: добавляем только то, что оно спрашивает. Лишнее соединение
    // на каждую строку — это лишний сик на каждую строку.
    private static string Joins(TaskView view, string recordExpr)
    {
        var parts = new List<string>();
        if (NeedsRecord(view) && recordExpr != "r.Id")
            parts.Add($"JOIN db_records r ON r.Id = {recordExpr}");
        if (NeedsExec(view))
            parts.Add($"LEFT JOIN db_field_values ve ON ve.RecordId = {recordExpr} AND ve.FieldId = @exec");
        if (NeedsStatus(view))
            parts.Add($"LEFT JOIN db_field_values vs ON vs.RecordId = {recordExpr} AND vs.FieldId = @status");
        return string.Join("\n", parts);
    }

    private long Scalar(string sql, params (string Name, object Value)[] args)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) AddParam(cmd, name).Value = value;
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        finally { if (opened) conn.Close(); }
    }

    /// <summary>
    /// Карточки задач по готовому списку записей, в ТОМ ЖЕ порядке. Проекция — та же, что у
    /// <see cref="GetTaskRecords"/>, поэтому страница показывает ровно то же, что показал бы
    /// сплошной прочит: расхождения между режимами взяться неоткуда.
    /// </summary>
    public List<TaskRecord> GetTaskRecordsByIds(int collectionId, IReadOnlyList<int> recordIds)
    {
        if (recordIds.Count == 0) return [];

        var byId = new Dictionary<int, TaskRecord>(recordIds.Count);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT r.Id, f.Id, f.CollectionId, c.Name, COALESCE(r.Color, c.Color),
                       r.Title, {DescExpr}, v.Value,
                       (SELECT va.Value FROM db_field_values va
                          JOIN db_fields fa ON fa.Id = va.FieldId
                         WHERE va.RecordId = r.Id AND fa.CollectionId = f.CollectionId
                           AND fa.IsTaskAssigner = 1
                         ORDER BY fa.Position, fa.Id LIMIT 1),
                       (SELECT vs.Value FROM db_field_values vs
                          JOIN db_fields fs ON fs.Id = vs.FieldId
                         WHERE vs.RecordId = r.Id AND fs.CollectionId = f.CollectionId
                           AND fs.IsTaskStatus = 1
                         ORDER BY fs.Position, fs.Id LIMIT 1),
                       (SELECT ci.FieldId || '|' || ci.StartIso || '|' || ci.EndIso || '|' || ci.AllDay
                          FROM calendar_index ci
                          JOIN db_fields fd ON fd.Id = ci.FieldId
                         WHERE ci.RecordId = r.Id AND fd.IsCalStart = 1 AND fd.FieldType = 'date'
                         ORDER BY ci.StartIso LIMIT 1)
                FROM db_fields f
                JOIN db_collections c ON c.Id = f.CollectionId
                JOIN db_records r     ON r.CollectionId = f.CollectionId
                LEFT JOIN db_field_values v ON v.RecordId = r.Id AND v.FieldId = f.Id
                WHERE f.FieldType = 'user' AND f.IsTaskExec = 1
                  AND f.CollectionId = @c
                  AND r.Id IN ({string.Join(",", recordIds)})";
            AddParam(cmd, "@c").Value = collectionId;

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var t = ReadTaskRow(rdr);
                if (t.Id is int id) byId[id] = t;
            }
        }
        finally { if (opened) conn.Close(); }

        // Порядок задаёт страница, а не SQL: `IN (…)` его не гарантирует, а строки уже разложены
        // по местам в виртуальном списке.
        var result = new List<TaskRecord>(recordIds.Count);
        foreach (var id in recordIds)
            if (byId.TryGetValue(id, out var t)) result.Add(t);
        return result;
    }

    /// <summary>Фоновая страница на ОТДЕЛЬНОМ подключении: виртуальный список тянет их пачками
    /// параллельно, а общий контекст однопоточный.</summary>
    public Task<List<TaskRecord>> LoadTaskPageAsync(int collectionId, TaskCollectionStats stats,
                                                    TaskView view, bool dated, int skip, int take) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);
            var ids  = repo.GetTaskPageIds(collectionId, stats, view, dated, skip, take);
            return repo.GetTaskRecordsByIds(collectionId, ids);
        });

    /// <summary>Фоновые размеры групп в текущем виде — по одному заходу на коллекцию.</summary>
    public Task<Dictionary<int, TaskViewSize>> CountTaskViewAsync(
        Dictionary<int, TaskCollectionStats> stats, TaskView view) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo   = new ConstructorRepository(ctx);
            var result = new Dictionary<int, TaskViewSize>(stats.Count);
            foreach (var (colId, s) in stats) result[colId] = repo.CountTaskView(colId, s, view);
            return result;
        });

    // Срок записи — первая по началу ВИДИМАЯ календарная дата, ровно как в проекции
    // GetTaskRecords. Нужны обе границы по отдельности, поэтому подзапроса два.
    private const string DueStartExpr =
        "(SELECT ci.StartIso FROM calendar_index ci JOIN db_fields fd ON fd.Id = ci.FieldId" +
        " WHERE ci.RecordId = r.Id AND fd.IsCalStart = 1 AND fd.FieldType = 'date'" +
        " ORDER BY ci.StartIso LIMIT 1)";

    private const string DueEndExpr =
        "(SELECT ci.EndIso FROM calendar_index ci JOIN db_fields fd ON fd.Id = ci.FieldId" +
        " WHERE ci.RecordId = r.Id AND fd.IsCalStart = 1 AND fd.FieldType = 'date'" +
        " ORDER BY ci.StartIso LIMIT 1)";
}
