using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Постраничное чтение раздела «Задачи» — то, чем живет виртуальный список
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
/// каждой записи индекса идет PK <c>(RecordId, FieldId)</c>, поэтому сортировать нечего;</item>
/// <item>«без срока» — записи коллекции, у которых строки в проекции нет, <c>ORDER BY Id</c>.</item>
/// </list>
/// Границу задает <see cref="TaskViewSize.Dated"/>: строки [0, Dated) берутся из первого потока.
///
/// Коллекция с НЕСКОЛЬКИМИ календарными полями-датами (см. <see cref="TaskCollectionStats.MultiDate"/>)
/// делится так же: «со сроком» — записи, у которых есть хоть одна календарная дата (срок —
/// наименьшая из них, порядок строит СУБД, но только на этих записях), «без срока» — остальные
/// в порядке Id.
///
/// Поток «без срока» в SQLite читается проходом таблицы в порядке Id от первой записи коллекции
/// (<see cref="TaskCollectionStats.FirstRecordId"/>) до набора страницы, а не выборкой всей
/// коллекции по индексу с сортировкой: на коллекции в полмиллиона сортировка стоила секунды на
/// КАЖДУЮ страницу. Редкая стадия и «Назначено мне» читаются от индекса значений — ими движут
/// совпадения, а не записи. Счет потока «без срока» для видов без условия на срок — счет по
/// всей коллекции от индекса значений минус счет потока «со сроком» (см. TryCountUndated).
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>
    /// Что разделу нужно знать о коллекции, не читая ее задач.
    /// </summary>
    /// <param name="Total">Всего задач (= записей коллекции с полем-исполнителем).</param>
    /// <param name="Done">Из них в статусе «Выполнено».</param>
    /// <param name="DueFieldId">
    /// Единственное календарное поле-дата коллекции — им задается срок. null — либо полей-дат
    /// нет вовсе (все задачи «без срока»), либо их несколько (см. <paramref name="MultiDate"/>).
    /// </param>
    /// <param name="MultiDate">
    /// У коллекции НЕСКОЛЬКО календарных полей-дат: сроком служит наименьшая, и порядок
    /// перестает быть порядком индекса. Читается общим путем — медленнее, но полностью.
    /// </param>
    /// <param name="FirstRecordId">Наименьший Id записи коллекции: нижняя граница прохода
    /// страниц «без срока» в порядке Id.</param>
    public sealed record TaskCollectionStats(int Total, int Done, int? DueFieldId, bool MultiDate,
                                             int FirstRecordId = 0);

    /// <summary>Размер группы в конкретном виде: сколько задач ему подходит и сколько из них
    /// со сроком (граница между потоками чтения).</summary>
    /// <param name="Total">Подходят виду.</param>
    /// <param name="Dated">Из них со сроком — длина первого потока.</param>
    /// <param name="Done">Из них выполнены — знаменатель и числитель индикатора в заголовке.</param>
    public readonly record struct TaskViewSize(int Total, int Dated, int Done);

    /// <summary>
    /// Вид раздела: правило быстрого фильтра плюс поиск. Одна структура на счет и на страницы —
    /// иначе счетчик и список считали бы по разным правилам.
    /// </summary>
    /// <param name="Kind">Значение <c>TaskFilterKind</c> строкой: репозиторий не знает о слое показа.</param>
    /// <param name="Search">Подстрока названия; пусто — поиск выключен.</param>
    /// <param name="MeVariants">
    /// Чем может быть записан текущий пользователь в поле-исполнителе. В памяти сравнение идет
    /// без учета регистра, а SQLite так кириллицу не сравнивает — варианты считает вызывающий.
    /// </param>
    /// <param name="Now">«Сейчас» — от него считаются «Сегодня» и просрочка.</param>
    /// <param name="Stage">Имя стадии для вида <c>Stage</c> (значение поля «Статус»).</param>
    /// <param name="HideDone">Скрывать выполненные во всех видах, кроме стадии «Выполнено»
    /// (настройка «Показывать выполненные» выключена).</param>
    /// <param name="OverdueNextDay">Просрочка со следующего дня после срока, а не с момента срока.</param>
    public sealed record TaskView(string Kind, string Search,
                                  IReadOnlyCollection<string> MeVariants, DateTime Now,
                                  string Stage = "", bool HideDone = false, bool OverdueNextDay = false)
    {
        /// <summary>Вид без сужения: все задачи, поиск пуст, выполненные не скрыты.</summary>
        public bool IsAll => Kind == "All" && Search.Length == 0 && !HideDone;

        /// <summary>Вид одной стадии.</summary>
        public bool IsStage => Kind == "Stage";

        /// <summary>Стадия «Выполнено»: единственный вид, в котором скрытые выполненные видны.</summary>
        public bool IsDoneStage => IsStage && Stage == TaskStatusDone;

        /// <summary>Ключ вида — тот же, что <c>TaskFilter.Key</c>: «All», …, «Stage:В работе».</summary>
        public string Key => IsStage ? "Stage:" + Stage : Kind;
    }

    /// <summary>
    /// Размеры и форма всех задачных коллекций базы одним заходом: <c>id коллекции → </c>
    /// <see cref="TaskCollectionStats"/>. Семантика <paramref name="activeIds"/> — как везде в
    /// разделе: null = все коллекции БД, ПУСТОЙ набор = ни одной.
    /// </summary>
    public Dictionary<int, TaskCollectionStats> GetTaskCollectionStats(ISet<int>? activeIds = null)
    {
        var counts = CountTasksWithFirstId(activeIds);
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
                Total:         c.Total,
                Done:          c.Done,
                DueFieldId:    ids.Count == 1 ? ids[0] : null,
                MultiDate:     ids.Count > 1,
                FirstRecordId: c.FirstId);
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

    // Регистровые варианты подстроки поиска: SQLite сравнивает без учета регистра только
    // латиницу, поэтому кириллицу перебираем вариантами — тот же прием, что в поиске календаря.
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

        // Скрытые выполненные не попадают никуда, кроме собственной стадии (TaskFilters.Matches).
        if (view.HideDone && !view.IsDoneStage && statusFieldId is not null)
            parts.Add("COALESCE(vs.Value, '') <> @done");

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
                // Момент просрочки — как в TaskFilters.IsOverdue: конец срока прошел либо
                // день конца срока уже позади.
                parts.Add(view.OverdueNextDay
                    ? $"substr(COALESCE({o.End}, {o.Start}), 1, 10) < @today"
                    : $"COALESCE({o.End}, {o.Start}) <= @now");
                break;

            case "NoDue":
                if (dated) return null;
                break;

            case "AssignedToMe":
                parts.Add(view.MeVariants.Count == 0
                    ? "1 = 0"
                    : $"COALESCE(ve.Value, '') IN ({string.Join(",", view.MeVariants.Select((_, i) => "@me" + i))})");
                break;

            case "Stage":
                // Пустой статус карточка показывает как «Запланировано» — эта стадия берет и его.
                // Поля-статуса нет вовсе: все статусы пустые, то есть все «Запланировано».
                var planned = view.Stage == TaskStatusPlanned;
                if (statusFieldId is null) parts.Add(planned ? "1 = 1" : "1 = 0");
                else parts.Add(planned ? "COALESCE(vs.Value, '') IN ('', @stage)"
                                       : "COALESCE(vs.Value, '') = @stage");
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
    private static bool NeedsStatus(TaskView v) => v.Kind is "Stage" or "Overdue" || v.HideDone;
    private static bool NeedsRecord(TaskView v) => v.Search.Length > 0;

    private void BindView(System.Data.Common.DbCommand cmd, TaskView view, int collectionId)
    {
        AddParam(cmd, "@c").Value     = collectionId;
        AddParam(cmd, "@done").Value  = TaskStatusDone;
        AddParam(cmd, "@stage").Value = view.Stage;
        AddParam(cmd, "@now").Value   = DateTime.SpecifyKind(view.Now, DateTimeKind.Unspecified).ToString("o");
        AddParam(cmd, "@today").Value = view.Now.ToString("yyyy-MM-dd");

        var i = 0;
        foreach (var v in view.MeVariants) AddParam(cmd, "@me" + i++).Value = v;

        var k = 0;
        foreach (var v in SearchVariants(view.Search)) AddParam(cmd, $"@q{k++}").Value = $"%{EscapeLike(v)}%";
    }

    // ── Счет и страницы ──────────────────────────────────────────────────────

    /// <summary>
    /// Сколько задач коллекции подходит виду и сколько из них со сроком. Считается ПО БАЗЕ:
    /// по этим числам строится полоса прокрутки и заголовок группы, а значит крышки нигде нет.
    /// </summary>
    public TaskViewSize CountTaskView(int collectionId, TaskCollectionStats stats, TaskView view)
    {
        // Вид без сужения: числа уже посчитаны общим счетом коллекций, спрашивать нечего.
        if (view.IsAll)
            return new TaskViewSize(stats.Total, CountDated(collectionId, stats), stats.Done);

        var cache   = new CountCache();
        var dated   = CountStream(collectionId, stats, view, dated: true, cap: 0, cache);
        var undated = CountStream(collectionId, stats, view, dated: false, cap: 0, cache);
        return new TaskViewSize(dated.Total + undated.Item1, dated.Total, dated.Done + undated.Item2);
    }

    /// <summary>
    /// Сколько задач раздела подходит каждому правилу — для пилюль быстрого фильтра. Считается
    /// ПОД КРЫШКОЙ <paramref name="cap"/>: пилюля печатает «999+», точное число ей не нужно, а
    /// шесть полных проходов по коллекции-полумиллионнику стоили бы секунд. Размеры групп, по
    /// которым строится полоса прокрутки, считаются отдельно и ТОЧНО
    /// (<see cref="CountTaskView"/>) — крышка живет только в пилюле.
    /// </summary>
    /// <param name="views">Виды пунктов навбара (стандартные и стадии); ответ — по <see cref="TaskView.Key"/>.</param>
    public Dictionary<string, int> CountTaskViews(Dictionary<int, TaskCollectionStats> stats,
                                                  IReadOnlyList<TaskView> views, int cap)
    {
        var result = new Dictionary<string, int>(views.Count);
        // Гистограмма статуса и счет потока «со сроком» — одни на все пилюли: виды различаются
        // условием, а не данными.
        var cache = new CountCache();
        foreach (var view in views)
        {
            var sum = 0;
            foreach (var (colId, s) in stats)
            {
                if (sum >= cap) break;                       // дальше считать незачем — уже «999+»
                var left = cap - sum;
                sum += CountStream(colId, s, view, dated: true, cap: left, cache).Total;
                if (sum >= cap) continue;
                sum += CountStream(colId, s, view, dated: false, cap: cap - sum, cache).Total;
            }
            result[view.Key] = Math.Min(sum, cap);
        }
        return result;
    }

    /// <summary>Фоновые счетчики пилюль на ОТДЕЛЬНОМ подключении.</summary>
    public Task<Dictionary<string, int>> CountTaskViewsAsync(
        Dictionary<int, TaskCollectionStats> stats, IReadOnlyList<TaskView> views, int cap) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountTaskViews(stats, views, cap);
        });

    // Сколько записей коллекции имеют срок — граница потоков для вида «Все задачи».
    private int CountDated(int collectionId, TaskCollectionStats stats)
    {
        if (stats.MultiDate)
            return (int)Scalar($@"SELECT COUNT(*) FROM ({CalStartRecordsSql}) AS d", ("@c", collectionId));
        if (stats.DueFieldId is not int due) return 0;
        return (int)Scalar($@"SELECT COUNT(*) FROM calendar_index ci
                              WHERE ci.CollectionId = @c AND ci.FieldId = @due",
                           ("@c", collectionId), ("@due", due));
    }

    // Записи коллекции с хоть одной календарной датой (поток «со сроком» при нескольких
    // полях-датах): индекс calendar_index(CollectionId, …) — цена по числу датированных записей.
    private const string CalStartRecordsSql =
        "SELECT DISTINCT ci.RecordId FROM calendar_index ci JOIN db_fields fd ON fd.Id = ci.FieldId" +
        " WHERE ci.CollectionId = @c AND fd.IsCalStart = 1 AND fd.FieldType = 'date'";

    // У записи есть календарная дата (любое поле-дата с показом) — отрицание дает «без срока».
    private const string HasCalStartSql =
        "SELECT 1 FROM calendar_index ci JOIN db_fields fd ON fd.Id = ci.FieldId" +
        " WHERE ci.RecordId = r.Id AND fd.IsCalStart = 1 AND fd.FieldType = 'date'";

    // Счет одного захода (размеры групп вида или пилюли): гистограммы статуса и точные счета
    // потока «со сроком» переиспользуются между видами и потоками — данные те же, различается
    // только условие.
    private sealed class CountCache
    {
        public Dictionary<int, Dictionary<string, int>> Histograms { get; } = [];
        public Dictionary<string, (int Total, int Done)> Dated     { get; } = [];
    }

    // cap > 0 — считаем не дальше него: `COUNT(*) FROM (… LIMIT cap)`. Цена такого счета не
    // зависит от размера коллекции, а «выполнено» под крышкой не нужно (это счет для пилюли).
    private (int Total, int Done) CountStream(int collectionId, TaskCollectionStats stats,
                                              TaskView view, bool dated, int cap, CountCache? cache = null)
    {
        if (cap < 0) return (0, 0);
        cache ??= new CountCache();
        if (!dated && TryCountUndated(collectionId, stats, view, cache, out var fast))
            return cap > 0 ? (Math.Min(fast.Total, cap), 0) : fast;

        // Точный счет потока «со сроком» нужен и самому виду, и вычитанию из счета по всей
        // коллекции (TryCountUndated) — второй раз его не считаем.
        var key = $"{collectionId}|{view.Key}|{view.HideDone}|{view.Search}";
        if (dated && cap == 0 && cache.Dated.TryGetValue(key, out var known)) return known;
        var counted = CountStreamSql(collectionId, stats, view, dated, cap);
        if (dated && cap == 0) cache.Dated[key] = counted;
        return counted;
    }

    private (int Total, int Done) CountStreamSql(int collectionId, TaskCollectionStats stats,
                                                 TaskView view, bool dated, int cap)
    {
        var parts = ViewParts(collectionId, stats, view, dated);
        if (!parts.Ok) return (0, 0);

        // Алиас capped обязателен: PostgreSQL не принимает подзапрос во FROM без алиаса
        // (SQLite прощает), а битый счет тут глотается вызывающим — пилюли просто врали бы.
        var sql = cap > 0
            ? $"SELECT COUNT(*), 0 FROM (SELECT 1 {parts.Body} LIMIT {cap}) AS capped"
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

    // ── Быстрый счет по индексу значений ─────────────────────────────────────
    //
    // Виды, чье условие — только статус и исполнитель («Все задачи», стадии, «Назначено мне»,
    // «Без срока», со скрытыми выполненными и без), в потоке «без срока» считаются не проходом
    // по записям с подзапросами на каждую, а вычитанием: счет по ВСЕЙ коллекции от индекса
    // db_field_values(FieldId, Value) — гистограмма статуса (стадия → сколько задач), «без
    // статуса» = всего − сумма гистограммы, исполнитель — сик по индексу — минус точный счет
    // потока «со сроком» (он идет по проекции calendar_index и стоит по числу датированных
    // задач). На коллекции в полмиллиона проход стоил 3–6 с на КАЖДЫЙ вид (восемь пилюль —
    // восемь проходов), вычитание — десятки миллисекунд на все сразу. Значения без записи не
    // живут: удаление записи уносит их явно или каскадом. Сумма гистограммы больше числа задач
    // или отрицательная разность — признак обратного (или устаревшего общего счета), и тогда
    // поток считается общим путем.
    private bool TryCountUndated(int collectionId, TaskCollectionStats stats, TaskView view,
                                 CountCache cache, out (int Total, int Done) result)
    {
        result = (0, 0);
        if (view.Search.Length > 0 || view.Kind is not ("All" or "Stage" or "AssignedToMe" or "NoDue")) return false;

        // «Без срока» в потоке без срока — это «Все задачи» того же потока.
        var basis = view.Kind == "NoDue" ? view with { Kind = "All" } : view;
        if (!TryCountWhole(collectionId, stats, basis, cache, out var whole)) return false;

        var datedPart = stats.MultiDate || stats.DueFieldId is not null
            ? CountStream(collectionId, stats, basis, dated: true, cap: 0, cache)
            : (Total: 0, Done: 0);
        var total = whole.Total - datedPart.Total;
        var done  = whole.Done  - datedPart.Done;
        if (total < 0 || done < 0) return false;
        result = (total, done);
        return true;
    }

    // Счет вида по всей коллекции — только от индекса значений (см. TryCountUndated).
    private bool TryCountWhole(int collectionId, TaskCollectionStats stats, TaskView view,
                               CountCache cache, out (int Total, int Done) result)
    {
        result = (0, 0);
        var (execId, statusId) = TaskRoleIds(collectionId);
        var total    = stats.Total;
        var hideDone = view.HideDone && !view.IsDoneStage && statusId is not null;

        var histogram = statusId is int sid ? StatusHistogram(sid, cache.Histograms) : [];
        var counted   = histogram.Values.Sum();
        if (counted > total) return false;
        int Stage(string value) => histogram.GetValueOrDefault(value);
        var doneCount = Stage(TaskStatusDone);

        switch (view.Kind)
        {
            case "All":
                result = hideDone ? (total - doneCount, 0) : (total, doneCount);
                return true;

            case "Stage":
                // Пустой статус карточка показывает как «Запланировано» (ViewCondition): стадия
                // берет и записи без значения, и пустые значения.
                var planned = view.Stage == TaskStatusPlanned;
                if (statusId is null)       result = (planned ? total : 0, 0);
                else if (planned)           result = (total - counted + Stage("") + Stage(TaskStatusPlanned), 0);
                else                        result = (Stage(view.Stage), view.Stage == TaskStatusDone ? Stage(view.Stage) : 0);
                return true;

            default:   // AssignedToMe
                if (view.MeVariants.Count == 0 || execId is null) return true;   // (0, 0), как «1 = 0»
                // Пустой вариант совпал бы с задачами БЕЗ исполнителя — это общий путь.
                if (view.MeVariants.Any(string.IsNullOrEmpty)) return false;
                var (mine, mineDone) = CountAssigned(execId.Value, statusId, view.MeVariants);
                if (mine > total) return false;
                result = hideDone ? (mine - mineDone, 0) : (mine, mineDone);
                return true;
        }
    }

    // Стадия → сколько значений поля-статуса ее несут: только индекс (FieldId, Value).
    private Dictionary<string, int> StatusHistogram(int statusFieldId,
                                                    Dictionary<int, Dictionary<string, int>>? cache)
    {
        if (cache is not null && cache.TryGetValue(statusFieldId, out var hit)) return hit;

        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value, COUNT(*) FROM db_field_values WHERE FieldId = @f GROUP BY Value";
            AddParam(cmd, "@f").Value = statusFieldId;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) histogram[rdr.GetString(0)] = rdr.GetInt32(1);
        }
        finally { if (opened) conn.Close(); }

        if (cache is not null) cache[statusFieldId] = histogram;
        return histogram;
    }

    // Задачи, где исполнитель — один из вариантов текущего пользователя, и сколько из них
    // выполнено: сик по индексу (FieldId, Value) и по PK статуса на каждое совпадение.
    private (int Total, int Done) CountAssigned(int execFieldId, int? statusFieldId, IReadOnlyCollection<string> me)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT COUNT(*), SUM(CASE WHEN COALESCE(vs.Value, '') = @done THEN 1 ELSE 0 END)
                FROM db_field_values ve
                LEFT JOIN db_field_values vs ON vs.RecordId = ve.RecordId AND vs.FieldId = @status
                WHERE ve.FieldId = @exec AND ve.Value IN ({string.Join(",", me.Select((_, i) => "@me" + i))})";
            AddParam(cmd, "@exec").Value   = execFieldId;
            AddParam(cmd, "@status").Value = statusFieldId ?? -1;
            AddParam(cmd, "@done").Value   = TaskStatusDone;
            var i = 0;
            foreach (var v in me) AddParam(cmd, "@me" + i++).Value = v;
            using var rdr = cmd.ExecuteReader();
            return rdr.Read() ? (rdr.GetInt32(0), rdr.IsDBNull(1) ? 0 : rdr.GetInt32(1)) : (0, 0);
        }
        finally { if (opened) conn.Close(); }
    }

    /// <summary>
    /// Id записей одной страницы группы, в порядке показа. <paramref name="skip"/> отсчитывается
    /// от начала ПОТОКА, а не группы: вызывающий сам решает по <see cref="TaskViewSize.Dated"/>,
    /// из какого потока берет строки.
    ///
    /// Смещение — обычный OFFSET по индексу. Пропускаемые строки не читаются из таблицы, но по
    /// дереву SQLite все равно шагает, поэтому прыжок ползунком в конец коллекции-миллионника
    /// стоит линейно от глубины — как и у «Расписания». Ради этого страницы и грузятся в фоне
    /// с дебаунсом.
    /// </summary>
    public List<int> GetTaskPageIds(int collectionId, TaskCollectionStats stats, TaskView view,
                                    bool dated, int skip, int take)
    {
        if (take <= 0) return [];
        var parts = (!dated ? SparsePageParts(collectionId, stats, view) : null)
                    ?? ViewParts(collectionId, stats, view, dated, page: true);
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

    /// <summary>Части запроса потока — из них собираются и счет, и страница, поэтому считают
    /// и показывают они заведомо одно и то же. <c>Ok = false</c> — поток виду не подходит вовсе
    /// («Сегодня» без срока, «Без срока» со сроком): его не читают и не считают.</summary>
    private readonly record struct ViewSql(string IdExpr, string Body, string Order, string DoneExpr, bool Ok);

    private static readonly ViewSql NoStream = new("", "", "", "", false);

    // Значение статуса записи для агрегата «сколько выполнено». Подзапрос по PK, а не соединение:
    // соединение размножило бы строки, если статусов у коллекции почему-то оказалось два.
    private const string DoneValueExpr =
        "COALESCE((SELECT v2.Value FROM db_field_values v2" +
        " WHERE v2.RecordId = {0} AND v2.FieldId = @status), '')";

    /// <param name="page">Части для страницы (а не для счета): поток «без срока» в SQLite тогда
    /// идет проходом таблицы в порядке Id — страница останавливается на своем LIMIT.</param>
    private ViewSql ViewParts(int collectionId, TaskCollectionStats stats, TaskView view, bool dated,
                              bool page = false)
    {
        var (_, statusId) = TaskRoleIds(collectionId);

        // Коллекция с несколькими полями-датами: срок — наименьшая из дат записи (подзапрос), и
        // порядок строит СУБД, но только на записях, у которых дата есть (их мало); остальные —
        // поток «без срока» в порядке Id, как у всех.
        if (stats.MultiDate && dated)
        {
            var where = ViewCondition(view, dated: true, (DueStartExpr, DueEndExpr), statusId);
            if (where is null) return NoStream;

            return new ViewSql(
                IdExpr:   "r.Id",
                Body:     $@"FROM db_records r
                             {Joins(view, "r.Id")}
                             WHERE r.Id IN ({CalStartRecordsSql}) AND ({where})",
                Order:    $"{DueStartExpr}, r.Id",
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

        // Страница в SQLite — проход таблицы в порядке Id от первой записи коллекции: ORDER BY
        // совпадает с порядком прохода, и чтение останавливается на LIMIT. Через индекс коллекции
        // СУБД брала ВСЕ записи, считала условие на каждой и сортировала — секунды на странице
        // коллекции в полмиллиона. Счет проходит всю коллекцию все равно — ему индекс лучше.
        var records = page && !db.IsPostgres
            ? $"db_records r NOT INDEXED"
            : "db_records r";
        var from = page && !db.IsPostgres && stats.FirstRecordId > 0
            ? $"r.Id >= {stats.FirstRecordId} AND "
            : "";

        return new ViewSql(
            IdExpr:   "r.Id",
            Body:     $@"FROM {records}
                         {Joins(view, "r.Id")}
                         WHERE {from}r.CollectionId = @c{NoDueClause(stats)} AND ({cond})",
            Order:    "r.Id",
            DoneExpr: string.Format(DoneValueExpr, "r.Id"),
            Ok:       true);
    }

    // Условие потока «без срока»: у записи нет строки проекции по полю-сроку (или ни одной
    // календарной даты, если полей несколько).
    private static string NoDueClause(TaskCollectionStats stats) =>
        stats.MultiDate                 ? $" AND NOT EXISTS ({HasCalStartSql})"
        : stats.DueFieldId is int d     ? $" AND NOT EXISTS (SELECT 1 FROM calendar_index ci WHERE ci.RecordId = r.Id AND ci.FieldId = {d})"
        : "";

    // Страница редкой стадии или «Назначено мне» в потоке «без срока»: совпадений мало, и
    // проход записей в порядке Id искал бы их по всей коллекции. Тогда движут совпадения из
    // индекса значений (FieldId, Value), а сортировка — только их. Редкость проверяется счетом
    // под крышкой: плотный вид останется проходу по Id, где страница — первые же строки.
    private ViewSql? SparsePageParts(int collectionId, TaskCollectionStats stats, TaskView view)
    {
        if (view.Search.Length > 0) return null;
        var (execId, statusId) = TaskRoleIds(collectionId);

        string driver, value;
        if (view.Kind == "Stage" && view.Stage != TaskStatusPlanned && statusId is int sid)
        {
            driver = $"db_field_values vs";
            value  = $"vs.FieldId = {sid} AND vs.Value = @stage";
        }
        else if (view.Kind == "AssignedToMe" && execId is int eid && view.MeVariants.Count > 0
                 && !view.MeVariants.Any(string.IsNullOrEmpty))
        {
            driver = "db_field_values ve";
            value  = $"ve.FieldId = {eid} AND ve.Value IN ({string.Join(",", view.MeVariants.Select((_, i) => "@me" + i))})";
        }
        else return null;

        // Крышка редкости: совпадений меньше восьмой части коллекции (и не больше 20 тысяч
        // сортируемых строк на страницу) — ведем от индекса.
        var limit = Math.Min(20_000, Math.Max(1, stats.Total / 8));
        var alias = driver[^2..];
        var sample = $"SELECT COUNT(*) FROM (SELECT 1 FROM {driver} WHERE {value} LIMIT {limit}) AS capped";
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        long matches;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sample;
            BindView(cmd, view, collectionId);
            matches = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }
        finally { if (opened) conn.Close(); }
        if (matches >= limit) return null;

        var cond = ViewCondition(view, dated: false, null, statusId);
        if (cond is null) return NoStream;
        // Соединение со статусом нужно «Назначено мне» только ради скрытых выполненных; у стадии
        // статус — сам ведущий набор.
        var statusJoin = alias == "ve" && NeedsStatus(view)
            ? "LEFT JOIN db_field_values vs ON vs.RecordId = r.Id AND vs.FieldId = @status"
            : "";

        return new ViewSql(
            IdExpr:   "r.Id",
            Body:     $@"FROM {driver}
                         JOIN db_records r ON r.Id = {alias}.RecordId
                         {statusJoin}
                         WHERE {value} AND r.CollectionId = @c{NoDueClause(stats)} AND ({cond})",
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
                SELECT r.Id, f.Id, f.CollectionId, c.Name, c.Color,
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

        // Порядок задает страница, а не SQL: `IN (…)` его не гарантирует, а строки уже разложены
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
