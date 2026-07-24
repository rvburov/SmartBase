using System.Data.Common;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Exporters;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository(AppDbContext db)
{
    // Системное поле «Дата» — единственное, что осталось schema-driven.
    // Название и Описание подняты до колонок Title/Description в db_records.
    private const string FieldDate = "Дата";

    /// <summary>Путь к файлу БД этого репозитория — для переноса коллекции в другую БД
    /// (ATTACH источника на подключении целевой БД, см. MoveCollectionIsolated).</summary>
    public string DbPath => db.DbPath;

    /// <summary>
    /// Права текущего пользователя в этой базе (ставит DatabaseManager). Публичные мутаторы
    /// первой строкой зовут <see cref="Demand"/> — страховку к скрытым кнопкам UI: запрещённое
    /// действие кидает PermissionDeniedException. Внутренние изолированные клоны репозитория
    /// (фоновые подключения массовых операций) прав не несут — их работа уже разрешена
    /// проверкой публичной точки входа. UI читает это же свойство для видимости кнопок
    /// (Permissions.Can); null — репозиторий без менеджера (тесты, генераторы) без ограничений.
    /// </summary>
    public PermissionService? Permissions { get; init; }

    private void Demand(PermAction action, int? collectionId = null) =>
        Permissions?.Demand(action, collectionId);

    // Владелец-коллекция записи/поля — для проверки прав с per-collection переопределением.
    private int? CollectionOfRecord(int recordId) =>
        db.DbRecords.Where(r => r.Id == recordId).Select(r => (int?)r.CollectionId).FirstOrDefault();
    private int? CollectionOfField(int fieldId) =>
        db.DbFields.Where(f => f.Id == fieldId).Select(f => (int?)f.CollectionId).FirstOrDefault();

    // ── Журнал изменений (change_log) ─────────────────────────────────────────
    //
    // Факты: кто (снапшот CurrentUser), когда, что сделал; у правок — компактное
    // «до → после» (Detail, Utils/ChangeDetail), длинные тексты свёрнуты ещё при
    // записи. Пишется через ТОТ ЖЕ контекст, что и операция — в т.ч. изолированный
    // фоновый (строка журнала не конкурирует с общим контекстом и коммитится вместе
    // с операцией, если вызвана до SaveChanges). Пакетные пути пишут ОДНУ строку
    // с Count — массовые операции журналом не замедляются.

    // Добавить строку журнала (без SaveChanges — коммитится ближайшим сохранением).
    // detail — готовый компактный текст «до → после» (Utils/ChangeDetail) для правок.
    // Возвращает строку — для слияния побуквенных правок (BeginEditMerge/EndEditMerge).
    private ChangeLogEntity Log(string action, string targetType, string targetName,
                                string? collectionName = null, long? count = null, string? detail = null)
    {
        var row = new ChangeLogEntity
        {
            Ts             = DateTime.UtcNow.ToString("o"),
            UserKey        = CurrentUser.Key,
            UserName       = CurrentUser.DisplayName,
            Action         = action,
            TargetType     = targetType,
            TargetName     = targetName,
            CollectionName = collectionName,
            Count          = count,
            Detail         = detail,
        };
        db.ChangeLog.Add(row);
        return row;
    }

    // ── Слияние побуквенных правок ────────────────────────────────────────────
    //
    // Заголовок/описание/поля в панели записи сохраняются на каждую букву (защита от
    // потери ввода) — без слияния журнал фиксировал бы промежуточные состояния
    // («Проверка» → «3Проверка», «3Проверка» → «35Проверка», …). Недавняя строка журнала
    // той же цели помнится в памяти: следующая правка УДАЛЯЕТ её и пишет новую от
    // ИСХОДНОГО «до» к последнему «после» — в журнале остаётся конечный результат.
    // Именно удаление+вставка, не UPDATE строки: новый Id будит поллер соседей на каждое
    // изменение (UPDATE старой строки они бы не увидели). Правка вернула исходное
    // значение — строка убирается совсем (события нет).
    private sealed record EditMerge(ChangeLogEntity Row, string OriginalOld, DateTime LastAt);
    private readonly Dictionary<(string Kind, int RecordId, int SubId), EditMerge> _editMerges = [];
    private static readonly TimeSpan EditMergeWindow = TimeSpan.FromSeconds(30);

    // Исходное «до» для цели: недавняя строка той же цели снимается (её заменит новая);
    // правка после паузы (окно истекло) — обычная новая строка с oldValue как есть.
    private string BeginEditMerge((string Kind, int RecordId, int SubId) key, string oldValue)
    {
        if (_editMerges.Count > 256)
            foreach (var stale in _editMerges
                         .Where(kv => DateTime.UtcNow - kv.Value.LastAt >= EditMergeWindow)
                         .Select(kv => kv.Key).ToList())
                _editMerges.Remove(stale);

        if (!_editMerges.TryGetValue(key, out var merge)) return oldValue;
        _editMerges.Remove(key);
        if (DateTime.UtcNow - merge.LastAt >= EditMergeWindow) return oldValue;

        if (db.Entry(merge.Row).State is not (EntityState.Deleted or EntityState.Detached))
            db.ChangeLog.Remove(merge.Row);
        return merge.OriginalOld;
    }

    private void EndEditMerge((string Kind, int RecordId, int SubId) key,
                              ChangeLogEntity row, string originalOld) =>
        _editMerges[key] = new EditMerge(row, originalOld, DateTime.UtcNow);

    // Log + немедленный SaveChanges — для bulk-путей, где своего SaveChanges рядом нет.
    private void LogNow(string action, string targetType, string targetName,
                        string? collectionName = null, long? count = null)
    {
        Log(action, targetType, targetName, collectionName, count);
        db.SaveChanges();
    }

    private string? CollectionNameOf(int? collectionId) =>
        collectionId is int cid
            ? db.DbCollections.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefault()
            : null;

    /// <summary>
    /// Опции типа свойства «пользователь»: отображаемые имена ДОПУЩЕННЫХ пользователей
    /// базы (db_users). В личной базе допущен только владелец — выбрать можно лишь себя.
    /// Значение хранится строкой-именем (как select): фильтр/сортировка/экспорт работают
    /// без доработок, а старые значения остаются историческим снапшотом.
    /// </summary>
    public List<string> GetUserOptions() =>
        db.DbUsers.Where(u => u.IsApproved)
                  .OrderBy(u => u.DisplayName)
                  .Select(u => u.DisplayName)
                  .ToList();

    /// <summary>Строка журнала для панели «История изменений».</summary>
    public sealed record ChangeLogRow(long Id, string Ts, string UserName, string Action,
                                      string TargetType, string TargetName,
                                      string? CollectionName, long? Count, string? Detail);

    /// <summary>
    /// Параметры страницы журнала для таблицы «История изменений». Колонки журнала
    /// адресуются ключами: user/action/type/object/collection/date (date — день,
    /// префикс Ts «yyyy-MM-dd»). Filters — колонка → допустимые СЫРЫЕ значения
    /// (пустой словарь — без фильтра). Сортировка — по одной колонке; для date
    /// хронология = порядок Id (журнал только дописывается), отдельного ORDER BY
    /// по строке Ts не нужно. AfterValue/AfterId — keyset-курсор: значение
    /// сорт-колонки и Id последней показанной строки (null — с начала).
    /// </summary>
    public sealed record ChangeLogQuery
    {
        public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Filters { get; init; }
            = new Dictionary<string, IReadOnlyCollection<string>>();
        public string? SortColumn { get; init; }
        public bool    SortAsc    { get; init; }
        public string? AfterValue { get; init; }
        public long?   AfterId    { get; init; }
    }

    /// <summary>
    /// Страница журнала для таблицы истории: фильтры и сортировка считаются в SQL,
    /// страницы — keyset (по Id, при сортировке — по паре «значение колонки + Id»),
    /// поэтому миллионы строк листаются без загрузки в память. Отдельное подключение —
    /// безопасно из фона.
    /// </summary>
    public Task<List<ChangeLogRow>> GetChangeLogPageAsync(ChangeLogQuery query, int take)
    {
        var path = db.DbPath;
        return Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            ctx.ApplyPerformancePragmas();
            IQueryable<ChangeLogEntity> q = ctx.ChangeLog.AsNoTracking();

            foreach (var (column, values) in query.Filters)
            {
                var set = values as HashSet<string> ?? [.. values];
                q = column switch
                {
                    "user"       => q.Where(e => set.Contains(e.UserName)),
                    "action"     => q.Where(e => set.Contains(e.Action)),
                    "type"       => q.Where(e => set.Contains(e.TargetType)),
                    "object"     => q.Where(e => set.Contains(e.TargetName)),
                    "collection" => q.Where(e => set.Contains(e.CollectionName ?? "")),
                    "date"       => q.Where(e => set.Contains(e.Ts.Substring(0, 10))),
                    _            => q,
                };
            }

            if (query.SortColumn is null or "date")
            {
                // Хронология = порядок Id; «Сначала старые» — Id ASC, иначе новые сверху.
                var asc = query.SortColumn == "date" && query.SortAsc;
                if (query.AfterId is long a) q = asc ? q.Where(e => e.Id > a) : q.Where(e => e.Id < a);
                q = asc ? q.OrderBy(e => e.Id) : q.OrderByDescending(e => e.Id);
            }
            else
            {
                q = SortChangeLog(q, query.SortColumn, query.SortAsc, query.AfterValue, query.AfterId);
            }

            return q.Take(take)
                    .Select(e => new ChangeLogRow(e.Id, e.Ts, e.UserName, e.Action,
                                                  e.TargetType, e.TargetName, e.CollectionName,
                                                  e.Count, e.Detail))
                    .ToList();
        });
    }

    // Сортировка журнала по строковой колонке + keyset-срез после курсора. Селектор
    // колонки один, предикат среза собирается Expression-ами из него же — сравнение
    // строк уходит в SQL тем же ORDER BY-компаратором (string.Compare → «>»/«<»),
    // поэтому страницы не теряют и не дублируют строки. Равные значения добираются
    // по Id DESC (ThenByDescending — вторая компонента курсора).
    private static IQueryable<ChangeLogEntity> SortChangeLog(IQueryable<ChangeLogEntity> q,
        string column, bool asc, string? afterValue, long? afterId)
    {
        Expression<Func<ChangeLogEntity, string>> sel = column switch
        {
            "user"       => e => e.UserName,
            "action"     => e => e.Action,
            "type"       => e => e.TargetType,
            "object"     => e => e.TargetName,
            "collection" => e => e.CollectionName ?? "",
            _            => e => e.UserName,
        };

        if (afterValue is not null && afterId is long id)
        {
            // (значение после курсора) ИЛИ (то же значение И Id < курсора).
            var p     = sel.Parameters[0];
            var value = sel.Body;
            var after = Expression.Constant(afterValue);
            var cmp   = Expression.Call(typeof(string), nameof(string.Compare), null, value, after);
            var zero  = Expression.Constant(0);
            var ahead = asc ? Expression.GreaterThan(cmp, zero) : Expression.LessThan(cmp, zero);
            var same  = Expression.Equal(value, after);
            var older = Expression.LessThan(
                Expression.Property(p, nameof(ChangeLogEntity.Id)), Expression.Constant(id));
            var body  = Expression.OrElse(ahead, Expression.AndAlso(same, older));
            q = q.Where(Expression.Lambda<Func<ChangeLogEntity, bool>>(body, p));
        }

        var ordered = asc ? q.OrderBy(sel) : q.OrderByDescending(sel);
        return ordered.ThenByDescending(e => e.Id);
    }

    /// <summary>
    /// Значения колонки журнала с количеством — для окна фильтра таблицы истории
    /// (SQL-агрегат на фоновом потоке, отдельное подключение — как GetColumnCountsAsync).
    /// Ключи колонок — как в <see cref="ChangeLogQuery"/>; date группируется по дню.
    /// </summary>
    public Task<List<(string Value, int Count)>> GetChangeLogCountsAsync(string column)
    {
        var path = db.DbPath;
        return Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            ctx.ApplyPerformancePragmas();
            var src = ctx.ChangeLog.AsNoTracking();
            var groups = column switch
            {
                "user"       => src.GroupBy(e => e.UserName),
                "action"     => src.GroupBy(e => e.Action),
                "type"       => src.GroupBy(e => e.TargetType),
                "object"     => src.GroupBy(e => e.TargetName),
                "collection" => src.GroupBy(e => e.CollectionName ?? ""),
                "date"       => src.GroupBy(e => e.Ts.Substring(0, 10)),
                _            => null,
            };
            return groups is null
                ? []
                : groups.Select(g => new { g.Key, Cnt = g.Count() })
                        .OrderBy(x => x.Key)
                        .AsEnumerable()
                        .Select(x => (x.Key, x.Cnt))
                        .ToList();
        });
    }

    // ── Очистка журнала (change_log) ──────────────────────────────────────────
    //
    // Разовая очистка (панель администратора) и автоочистка по возрасту при
    // монтировании. Оба пути — bulk `ExecuteDelete`: DELETE уходит в SQL одним
    // запросом (без загрузки строк в память), провайдеро-независимо (SQLite/PG).

    /// <summary>
    /// Удаляет строки журнала старше <paramref name="olderThanUtc"/> (UTC); null —
    /// весь журнал. Только администратор. Возвращает число удалённых строк.
    /// </summary>
    public int PurgeChangeLog(DateTime? olderThanUtc)
    {
        if (Permissions is { IsAdmin: false })
            throw new InvalidOperationException(
                "Очистить журнал может только администратор базы данных.");

        // Кеш слияния побуквенных правок мог держать ссылки на удаляемые строки —
        // сбрасываем, иначе следующая правка попыталась бы удалить уже удалённую строку.
        _editMerges.Clear();

        return olderThanUtc is DateTime cut
            ? db.ChangeLog.Where(e => string.Compare(e.Ts, cut.ToString("o")) < 0).ExecuteDelete()
            : db.ChangeLog.ExecuteDelete();
    }

    /// <summary>
    /// Автоочистка журнала при монтировании: удаляет строки старше <paramref name="months"/>
    /// месяцев на СВЕЖЕМ подключении в фоне (не блокирует старт, не трогает общий контекст).
    /// Это настроенная администратором политика самой базы (db_meta) — прав не требует.
    /// Best-effort: файл занят/недоступен — очистка повторится при следующем монтировании.
    /// </summary>
    public void ApplyChangeLogRetention(bool enabled, int months)
    {
        if (!enabled || months <= 0) return;
        var cutoff = DateTime.UtcNow.AddMonths(-months).ToString("o");
        var path   = db.DbPath;
        _ = Task.Run(() =>
        {
            try
            {
                using var ctx = new AppDbContext(path);
                ctx.ApplyPerformancePragmas();
                ctx.ChangeLog.Where(e => string.Compare(e.Ts, cutoff) < 0).ExecuteDelete();
            }
            catch { /* автоочистка не критична: повторится при следующем запуске */ }
        });
    }

    // ── Группы ────────────────────────────────────────────────────────────────

    public List<DbGroup> GetAllGroups() =>
        db.DbGroups
          .OrderBy(g => g.Position)
          .Select(g => new DbGroup { Id = g.Id, Name = g.Name, ParentId = g.ParentId, Position = g.Position })
          .ToList();

    public DbGroup AddGroup(DbGroup group)
    {
        Demand(PermAction.CreateCollection);
        var entity = new DbGroupEntity { Name = group.Name, ParentId = group.ParentId, Position = group.Position };
        db.DbGroups.Add(entity);
        Log("create", "group", group.Name);
        db.SaveChanges();
        group.Id = entity.Id;
        return group;
    }

    public void UpdateGroup(DbGroup group)
    {
        Demand(PermAction.EditCollection);
        var entity = db.DbGroups.Find(group.Id);
        if (entity is null) return;
        // Перестановки (drag/сортировка) журнал не засоряют — только переименование/перенос.
        if (entity.Name != group.Name || entity.ParentId != group.ParentId)
            Log("update", "group", group.Name,
                detail: entity.Name != group.Name ? ChangeDetail.Rename(entity.Name, group.Name) : null);
        entity.Name     = group.Name;
        entity.ParentId = group.ParentId;
        entity.Position = group.Position;
        db.SaveChanges();
    }

    // Существует ли группа с таким именем на том же уровне (с тем же родителем).
    // excludeId — id исключаемой группы (для переименования самой себя).
    public bool GroupNameExists(string name, int? parentId, int? excludeId = null)
    {
        var q = db.DbGroups.Where(g => g.ParentId == parentId && g.Name == name);
        if (excludeId is int id) q = q.Where(g => g.Id != id);
        return q.Any();
    }

    /// <summary>
    /// Полный дубль группы СО ВСЕМ содержимым (подгруппы + коллекции + записи + значения + UI) в той же
    /// БД. Структуру (группы + шеллы коллекций) создаём через EF, данные коллекций — bulk-SQL
    /// (<see cref="CopyCollectionData"/>), как дубль коллекции. Верхняя группа получает имя «X (копия)»,
    /// вложенные элементы — свои имена. <paramref name="progress"/> — нарастающее число скопированных
    /// записей по всем коллекциям ветки (для %). Возвращает id новой верхней группы.
    /// </summary>
    public int DuplicateGroupCore(int groupId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        Demand(PermAction.DuplicateCollection);
        var src = db.DbGroups.Find(groupId);
        if (src is null) throw new InvalidOperationException($"Group {groupId} not found");

        // 1) Структура: верхняя группа «(копия)» под тем же родителем; подгруппы — со своими именами.
        var groupMap = new Dictionary<int, int>();   // старый id группы → новый
        var newRootId = CloneGroupTree(groupId, src.ParentId, isRoot: true, groupMap);

        // 2) Коллекции ветки: шелл в новую группу + bulk-копия записей/значений (общий % по ветке).
        var subtree = groupMap.Keys.ToList();
        var cols = db.DbCollections
            .Where(c => c.GroupId != null && subtree.Contains(c.GroupId.Value))
            .OrderBy(c => c.GroupId).ThenBy(c => c.Position)
            .Select(c => new { c.Id, c.GroupId })
            .ToList();

        long basis = 0;
        foreach (var c in cols)
        {
            var rc    = db.DbRecords.Count(r => r.CollectionId == c.Id);
            var shell = CreateCollectionShell(c.Id, overrideGroupId: groupMap[c.GroupId!.Value], addCopySuffix: false);
            if (shell.Collection.Id is int newColId)
                CopyCollectionData(c.Id, newColId, new BaseProgress(progress, basis), batchSize);
            basis += rc;
        }
        LogNow("duplicate", "group", src.Name, count: basis);
        progress?.Report((int)basis);
        return newRootId;
    }

    /// <summary>Полный дубль группы на ОТДЕЛЬНОМ подключении (фон + прогресс для больших веток) —
    /// не трогает общий Context (см. правило об общем AppDbContext), безопасно из Task.Run.</summary>
    public int DuplicateGroupIsolated(int groupId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        Demand(PermAction.DuplicateCollection);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        return new ConstructorRepository(ctx).DuplicateGroupCore(groupId, progress, batchSize);
    }

    // Рекурсивно копирует только СТРУКТУРУ групп (без коллекций): новая группа под targetParentId,
    // затем её подгруппы. Заполняет map (старый id → новый). Верхняя получает «(копия)», вложенные — имена как есть.
    private int CloneGroupTree(int srcGroupId, int? targetParentId, bool isRoot, Dictionary<int, int> map)
    {
        var src    = db.DbGroups.Find(srcGroupId)!;
        var maxPos = db.DbGroups.Where(g => g.ParentId == targetParentId).Select(g => (int?)g.Position).Max() ?? 0;
        var name   = isRoot
            ? NextCopyName(src.Name, cand => db.DbGroups.Any(g => g.ParentId == targetParentId && g.Name == cand))
            : src.Name;

        var copy = new DbGroupEntity { Name = name, ParentId = targetParentId, Position = maxPos + 1000 };
        db.DbGroups.Add(copy);
        db.SaveChanges();
        map[srcGroupId] = copy.Id;

        foreach (var childId in db.DbGroups.Where(g => g.ParentId == srcGroupId).OrderBy(g => g.Position)
                     .Select(g => g.Id).ToList())
            CloneGroupTree(childId, copy.Id, false, map);
        return copy.Id;
    }

    public void DeleteGroup(int groupId)
    {
        Demand(PermAction.DeleteCollection);
        // Одна строка журнала на ветку (рекурсия по подгруппам — внутри core).
        var name = db.DbGroups.Where(g => g.Id == groupId).Select(g => g.Name).FirstOrDefault();
        DeleteGroupCore(groupId);
        if (name is not null) LogNow("delete", "group", name);
    }

    private void DeleteGroupCore(int groupId)
    {
        // Рекурсивно удаляем все вложенные группы.
        var childIds = db.DbGroups.Where(g => g.ParentId == groupId).Select(g => g.Id).ToList();
        foreach (var childId in childIds)
            DeleteGroupCore(childId);

        // Удаляем коллекции этой группы; поля/записи/значения уйдут каскадно, UI-строки без FK — явно.
        var colIds = db.DbCollections.Where(c => c.GroupId == groupId).Select(c => c.Id).ToList();
        db.DbCollections.RemoveRange(db.DbCollections.Where(c => c.GroupId == groupId));

        var entity = db.DbGroups.Find(groupId);
        if (entity is not null) db.DbGroups.Remove(entity);
        db.SaveChanges();
        foreach (var colId in colIds)
            DeleteCollectionUiRows(colId);
    }

    // UI-настройки коллекции без FK (ui_*, record_display, column_meta) чистим явно при ЛЮБОМ
    // удалении коллекции: каскада на них нет, строки сиротеют, а id коллекций SQLite
    // переиспользует — фантомные настройки достались бы будущей коллекции с тем же id.
    private void DeleteCollectionUiRows(int collectionId)
    {
        foreach (var t in new[] { "ui_row_heights", "record_display", "ui_col_widths",
                                  "ui_collection_settings", "column_meta" })
            db.Database.ExecuteSqlRaw($"DELETE FROM {t} WHERE CollectionId = {collectionId}");
    }

    // Id всех групп ветки (сама группа + потомки на любой глубине). Один проход по таблице групп.
    private List<int> SubtreeGroupIds(int rootGroupId)
    {
        var byParent = db.DbGroups.Select(g => new { g.Id, g.ParentId }).ToList()
            .Where(g => g.ParentId.HasValue).ToLookup(g => g.ParentId!.Value, g => g.Id);
        var result = new List<int>();
        var stack  = new Stack<int>();
        stack.Push(rootGroupId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            result.Add(id);
            foreach (var child in byParent[id]) stack.Push(child);
        }
        return result;
    }

    /// <summary>Асинхронная версия <see cref="CountGroupSubtree"/> на ОТДЕЛЬНОМ подключении — для
    /// подсчёта перед массовыми операциями сайдбара: COUNT по ветке с миллионами записей — сотни мс,
    /// на UI-потоке это фриз перед окном прогресса (множится при мультивыборе).</summary>
    public Task<(int Collections, long Records)> CountGroupSubtreeAsync(int groupId) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountGroupSubtree(groupId);
        });

    /// <summary>Число коллекций во всей ветке группы и суммарное число их записей — для индикатора
    /// (дубль/удаление/перенос группы показывают «Коллекций: X · Записей: Y»).</summary>
    public (int Collections, long Records) CountGroupSubtree(int groupId)
    {
        var groupIds = SubtreeGroupIds(groupId);
        var colIds   = db.DbCollections.Where(c => c.GroupId != null && groupIds.Contains(c.GroupId.Value))
                         .Select(c => c.Id).ToList();
        long records = colIds.Count == 0 ? 0 : db.DbRecords.LongCount(r => colIds.Contains(r.CollectionId));
        return (colIds.Count, records);
    }

    /// <summary>Удалить группу со всей веткой на ОТДЕЛЬНОМ подключении (фон + прогресс) — для больших
    /// веток. Не трогает общий Context (см. правило об общем AppDbContext), безопасно из Task.Run.</summary>
    public void DeleteGroupIsolated(int groupId, IProgress<int>? progress = null)
    {
        Demand(PermAction.DeleteCollection);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        new ConstructorRepository(ctx).DeleteGroupData(groupId, progress);
    }

    /// <summary>
    /// Удалить группу со всей веткой ПАКЕТАМИ: коллекции ветки по очереди тем же путём, что
    /// <see cref="DeleteCollectionData"/> (транзакция на пакет, явные bulk-DELETE значений/проекции,
    /// чистка UI-строк без FK), затем строки групп ветки. <paramref name="progress"/> — нарастающее
    /// число удалённых записей по всей ветке (для %).
    /// </summary>
    public void DeleteGroupData(int groupId, IProgress<int>? progress = null, int batchSize = 10000,
                                bool log = true)
    {
        var groupName = db.DbGroups.Where(g => g.Id == groupId).Select(g => g.Name).FirstOrDefault();
        var groupIds = SubtreeGroupIds(groupId);
        var colIds   = db.DbCollections.Where(c => c.GroupId != null && groupIds.Contains(c.GroupId.Value))
                         .Select(c => c.Id).ToList();
        var total    = colIds.Count == 0 ? 0L : db.DbRecords.LongCount(r => colIds.Contains(r.CollectionId));

        long basis = 0;
        foreach (var cid in colIds)
        {
            var rc = db.DbRecords.LongCount(r => r.CollectionId == cid);
            DeleteCollectionRows(cid, basis, progress, batchSize);
            basis += rc;
        }

        // Строки групп ветки (ParentId — SET NULL, не каскад, поэтому удаляем явно весь набор).
        using var tx = db.Database.BeginTransaction();
        foreach (var gid in groupIds)
            db.Database.ExecuteSqlInterpolated($"DELETE FROM db_groups WHERE Id = {gid}");
        tx.Commit();

        if (log && groupName is not null) LogNow("delete", "group", groupName, count: total);
        progress?.Report((int)total);   // финально 100% для этой ветки
    }

    // ── Коллекции ─────────────────────────────────────────────────────────────

    public List<DbCollection> GetAllCollections() =>
        db.DbCollections
          .OrderBy(c => c.Position)
          .Select(c => new DbCollection
          {
              Id = c.Id, Name = c.Name, GroupId = c.GroupId,
              Position = c.Position, Icon = c.Icon, Color = c.Color,
          })
          .ToList();

    public List<DbCollection> GetCollectionsByGroup(int? groupId)
    {
        var query = groupId is null
            ? db.DbCollections.Where(c => c.GroupId == null)
            : db.DbCollections.Where(c => c.GroupId == groupId);

        return query.OrderBy(c => c.Position)
                    .Select(c => new DbCollection
                    {
                        Id = c.Id, Name = c.Name, GroupId = c.GroupId,
                        Position = c.Position, Icon = c.Icon, Color = c.Color,
                    })
                    .ToList();
    }

    // Существует ли коллекция с таким именем на том же уровне (в той же группе).
    // excludeId — id исключаемой коллекции (для переименования самой себя).
    public bool CollectionNameExists(string name, int? groupId, int? excludeId = null)
    {
        var q = db.DbCollections.Where(c => c.GroupId == groupId && c.Name == name);
        if (excludeId is int id) q = q.Where(c => c.Id != id);
        return q.Any();
    }

    public DbCollection AddCollection(DbCollection col)
    {
        Demand(PermAction.CreateCollection);
        var entity = new DbCollectionEntity
        {
            Name = col.Name, GroupId = col.GroupId,
            Position = col.Position, Icon = col.Icon, Color = col.Color,
        };
        db.DbCollections.Add(entity);
        Log("create", "collection", col.Name);
        db.SaveChanges();
        col.Id = entity.Id;

        // Title и Description теперь являются колонками таблицы db_records.
        // В схеме DbFields их больше не создаём.
        return col;
    }

    // Ключи системных колонок (см. TableRowVm.TitleKey/DescKey). Дублируем строками,
    // чтобы слой БД не зависел от ViewModels.
    private const string TitleKey = "__title__";
    private const string DescKey  = "__desc__";

    /// <summary>
    /// Создать коллекцию и залить записи ПОТОКОМ: маленькая shell-транзакция (коллекция + текстовые
    /// поля + подпись «Название» + порядок колонок, «Описание» пустое в конце), затем строки
    /// пакетами по <paramref name="batchSize"/> — каждый пакет в СВОЕЙ транзакции (WAL не
    /// разрастается на весь объём, отмена срабатывает между пакетами). Записи и значения пишутся
    /// сырым ADO.NET (prepared INSERT), а НЕ через EF: EF на каждую из миллионов сущностей тратит
    /// построение графа и чтение сгенерированного Id — ≈60× медленнее сырого пути. Внутри своей
    /// транзакции мы единственный писатель → id пакета непрерывны: RecordId = firstNew + индекс.
    /// Отмена или любая ошибка после shell-транзакции (включая ошибки источника строк) → созданная
    /// коллекция удаляется целиком (батч-удаление), исключение пробрасывается (SqliteException —
    /// дружелюбной ImportException). Имя занято → «(копия)»-вариант (мульти-файловый импорт).
    /// Возвращает id коллекции. <paramref name="rowsWritten"/> — нарастающее число строк, раз в пакет.
    /// </summary>
    public int ImportStreamedCollection(string collectionName, int? groupId, ImportSchema schema,
        IEnumerable<ImportRow> rows, CancellationToken ct,
        IProgress<long>? rowsWritten = null, int batchSize = 20_000)
    {
        Demand(PermAction.CreateCollection);
        collectionName = UniqueCollectionName(collectionName, groupId);

        int colId;
        List<int> fieldIds;
        using (var tx = db.Database.BeginTransaction())
        {
            var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
            var col = new DbCollectionEntity
            {
                Name = collectionName, GroupId = groupId, Position = maxPos + 1000,
                Icon = "table", Color = "#007AFF",
            };
            db.DbCollections.Add(col);
            db.SaveChanges();
            colId = col.Id;

            // Свойства по столбцам — текстовые, в порядке схемы. Имена уже уникальны.
            var fields = schema.FieldNames
                .Select((name, i) => new DbFieldEntity
                {
                    CollectionId = col.Id, Name = name, FieldType = "text", Position = (i + 1) * 1000,
                })
                .ToList();
            db.DbFields.AddRange(fields);
            db.SaveChanges();
            fieldIds = fields.Select(f => f.Id).ToList();

            // Подпись колонки «Название» из заголовка (пусто/дефолт → имя по умолчанию).
            SetSystemFieldName(col.Id, TitleKey, schema.TitleHeader, "Название");

            // Порядок колонок: «Название», затем свойства, «Описание» — в конце.
            var order = new List<string> { TitleKey };
            order.AddRange(fieldIds.Select(id => id.ToString()));
            order.Add(DescKey);
            SaveColumnOrder(col.Id, order);

            tx.Commit();
        }

        try
        {
            WriteRowsBatched(colId, fieldIds, schema, rows, ct, rowsWritten, batchSize);
        }
        catch (SqliteException ex)
        {
            // Оборачиваем в дружелюбную ImportException мимо ImportErrors.Map — логируем здесь.
            // Serilog.Log полностью, т.к. у репозитория есть свой метод Log (журнал изменений).
            Serilog.Log.Error(ex, "Ошибка записи в БД при импорте коллекции «{Name:l}»", collectionName);
            TryDeleteImported(colId);
            throw ex.SqliteErrorCode == 13   // SQLITE_FULL
                ? new ImportException("Недостаточно места на диске — импорт отменён, частичные данные удалены.")
                : new ImportException("Ошибка записи в базу данных. Импорт отменён.");
        }
        catch (Npgsql.PostgresException ex)
        {
            Serilog.Log.Error(ex, "Ошибка записи в PostgreSQL при импорте коллекции «{Name:l}»", collectionName);
            TryDeleteImported(colId);
            throw ex.SqlState == "53100"   // disk_full
                ? new ImportException("Недостаточно места на диске — импорт отменён, частичные данные удалены.")
                : new ImportException("Ошибка записи в базу данных. Импорт отменён.");
        }
        catch
        {
            TryDeleteImported(colId);
            throw;
        }

        LogNow("import", "collection", collectionName,
               count: db.DbRecords.LongCount(r => r.CollectionId == colId));
        return colId;
    }

    // Недолитая коллекция удаляется целиком (батч-удаление, перезапускаемое). Ошибку удаления
    // глотаем — важнее показать исходную причину; при сбое хвост доудалит повторное удаление.
    // Без строки журнала: пользователь этой коллекции не видел (сбой/отмена импорта).
    private void TryDeleteImported(int collectionId)
    {
        try { DeleteCollectionData(collectionId, log: false); }
        catch { /* best-effort */ }
    }

    // Батч-цикл записи (модель транзакций — DeleteCollectionRows): prepared-команды создаются один
    // раз, на каждый пакет переприсваивается транзакция. Прогресс — раз в пакет.
    private void WriteRowsBatched(int collectionId, List<int> fieldIds, ImportSchema schema,
        IEnumerable<ImportRow> rows, CancellationToken ct, IProgress<long>? rowsWritten, int batchSize)
    {
        // Шаг Position: 1000 (есть куда вставлять строки между импортированными), но на
        // многомиллионных объёмах — 1: (idx+1)*1000 в int переполняется после ~2,1 млн строк.
        var posStep = schema.RowCount > 2_000_000 ? 1L : 1000L;
        var now     = DateTime.Now.ToString("o");
        var conn    = db.Database.GetDbConnection();

        using var insRec = conn.CreateCommand();
        // RETURNING Id (SQLite ≥3.35 и PG): id каждой записи получаем из самой вставки — без
        // допущения «новые id непрерывны» (на PG identity-последовательность делят все клиенты).
        insRec.CommandText =
            "INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title, Description) " +
            "VALUES (@cid, @created, @created, @author, @author, @pos, 0, @title, NULL) RETURNING Id";
        var rCid     = AddParam(insRec, "@cid",     System.Data.DbType.Int32);
        var rCreated = AddParam(insRec, "@created", System.Data.DbType.String);
        var rAuthor  = AddParam(insRec, "@author",  System.Data.DbType.String);
        var rPos     = AddParam(insRec, "@pos",     System.Data.DbType.Int64);
        var rTitle   = AddParam(insRec, "@title",   System.Data.DbType.String);
        rCid.Value     = collectionId;
        rCreated.Value = now;
        rAuthor.Value  = CurrentUser.DisplayName;

        using var insVal = conn.CreateCommand();
        insVal.CommandText = "INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES (@rid, @fid, @val)";
        var vRid = AddParam(insVal, "@rid", System.Data.DbType.Int64);
        var vFid = AddParam(insVal, "@fid", System.Data.DbType.Int32);
        var vVal = AddParam(insVal, "@val", System.Data.DbType.String);

        long written  = 0;
        var  prepared = false;
        var  batch    = new List<ImportRow>(Math.Min(batchSize, 4096));
        var  newIds   = new List<long>(Math.Min(batchSize, 4096));
        using var e   = rows.GetEnumerator();
        var  more     = true;

        while (more)
        {
            ct.ThrowIfCancellationRequested();

            batch.Clear();
            while (batch.Count < batchSize && (more = e.MoveNext()))
                batch.Add(e.Current);
            if (batch.Count == 0) break;

            using var tx = db.Database.BeginTransaction();
            var dbTx = tx.GetDbTransaction();
            insRec.Transaction = dbTx;
            insVal.Transaction = dbTx;
            if (!prepared) { insRec.Prepare(); insVal.Prepare(); prepared = true; }

            newIds.Clear();
            for (var i = 0; i < batch.Count; i++)
            {
                rPos.Value   = (written + i + 1) * posStep;
                rTitle.Value = batch[i].Title ?? "";
                newIds.Add(Convert.ToInt64(insRec.ExecuteScalar()!));
            }

            for (var i = 0; i < batch.Count; i++)
            {
                var row = batch[i];
                vRid.Value = newIds[i];
                for (var f = 0; f < fieldIds.Count; f++)
                {
                    vFid.Value = fieldIds[f];
                    vVal.Value = (f < row.FieldValues.Count ? row.FieldValues[f] : "") ?? "";
                    insVal.ExecuteNonQuery();
                }
            }

            tx.Commit();
            written += batch.Count;
            rowsWritten?.Report(written);
        }

        rowsWritten?.Report(written);
    }

    public void UpdateCollection(DbCollection col)
    {
        Demand(PermAction.EditCollection, col.Id);
        var entity = db.DbCollections.Find(col.Id);
        if (entity is null) return;
        // Перестановки (Position) журнал не засоряют — только смысловые изменения.
        if (entity.Name != col.Name || entity.GroupId != col.GroupId ||
            entity.Icon != col.Icon || entity.Color   != col.Color)
            Log("update", "collection", col.Name,
                detail: entity.Name != col.Name ? ChangeDetail.Rename(entity.Name, col.Name) : null);
        entity.Name     = col.Name;
        entity.GroupId  = col.GroupId;
        entity.Position = col.Position;
        entity.Icon     = col.Icon;
        entity.Color    = col.Color;
        db.SaveChanges();
    }

    public void DeleteCollection(int collectionId)
    {
        Demand(PermAction.DeleteCollection, collectionId);
        var entity = db.DbCollections.Find(collectionId);
        if (entity is not null)
        {
            Log("delete", "collection", entity.Name);
            db.DbCollections.Remove(entity);
        }
        db.SaveChanges();
        DeleteCollectionUiRows(collectionId);
    }

    /// <summary>
    /// Удалить коллекцию на ОТДЕЛЬНОМ подключении (фон + прогресс) — для больших коллекций.
    /// Не трогает общий Context (см. правило об общем AppDbContext), поэтому безопасно из Task.Run.
    /// </summary>
    public void DeleteCollectionIsolated(int collectionId, IProgress<int>? progress = null)
    {
        Demand(PermAction.DeleteCollection, collectionId);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        new ConstructorRepository(ctx).DeleteCollectionData(collectionId, progress);
    }

    /// <summary>
    /// Удалить записи коллекции ПАКЕТАМИ, затем строку коллекции и UI-настройки. Каждый пакет —
    /// СВОЯ транзакция: прежде вся операция шла одной гигантской транзакцией, и WAL разрастался на
    /// весь объём изменений (десятки ГБ на многомиллионных коллекциях); частичное удаление при сбое
    /// не страшно — повтор операции доудаляет остаток. Значения и проекция календаря удаляются
    /// ЯВНЫМ bulk-DELETE до записей (каскаду по FK остаётся проверить пустоту), UI-строки без FK
    /// (ui_row_heights, record_display и коллекция-level) чистятся явно — иначе они сиротели, а id
    /// коллекций SQLite переиспользует (фантомные настройки у будущей коллекции с тем же id).
    /// <paramref name="progress"/> — нарастающее число удалённых записей.
    /// </summary>
    public void DeleteCollectionData(int collectionId, IProgress<int>? progress = null, int batchSize = 10000,
                                     bool log = true)
    {
        var name     = CollectionNameOf(collectionId);
        var recCount = db.DbRecords.Count(r => r.CollectionId == collectionId);
        DeleteCollectionRows(collectionId, 0, progress, batchSize);
        if (log && name is not null) LogNow("delete", "collection", name, count: recCount);
        progress?.Report(recCount);   // финально 100% для этой коллекции
    }

    // Удаление одной коллекции пакетами (транзакция на пакет, см. DeleteCollectionData): значения и
    // проекция — bulk-DELETE по срезу записей, затем записи; в конце строка коллекции (каскад уберёт
    // поля) и UI-строки без FK. progressBase — сдвиг прогресса при удалении ветки группы. Числа в
    // SQL — int/long, которые мы формируем сами, поэтому интерполяция в Raw безопасна.
    private void DeleteCollectionRows(int collectionId, long progressBase, IProgress<int>? progress, int batchSize)
    {
        long deleted = 0;
        var  prevId  = 0;
        while (true)
        {
            using var tx = db.Database.BeginTransaction();
            var end   = DeleteBatchEndId(collectionId, prevId, batchSize);
            var slice = $"CollectionId = {collectionId} AND Id > {prevId}" + (end is int u ? $" AND Id <= {u}" : "");
            var ids   = $"(SELECT Id FROM db_records WHERE {slice})";
            db.Database.ExecuteSqlRaw($"DELETE FROM db_field_values WHERE RecordId IN {ids}");
            db.Database.ExecuteSqlRaw($"DELETE FROM calendar_index  WHERE RecordId IN {ids}");
            db.Database.ExecuteSqlRaw($"DELETE FROM db_records WHERE {slice}");
            tx.Commit();

            if (end is not int e) break;   // последний (неполный) пакет удалён
            deleted += batchSize; prevId = e;
            progress?.Report((int)(progressBase + deleted));
        }

        using var tail = db.Database.BeginTransaction();
        // UI-настройки коллекции: FK-каскада у этих таблиц нет — только явная чистка.
        DeleteCollectionUiRows(collectionId);
        // Строка коллекции: каскад уберёт поля (их строки calendar_index уже удалены выше).
        db.Database.ExecuteSqlInterpolated($"DELETE FROM db_collections WHERE Id = {collectionId}");
        tail.Commit();
    }

    // Id записи на позиции offset−1 в срезе коллекции после prevId (порядок Id) — граница пакета.
    // null, если записей меньше (последний неполный пакет). Параметры — int, не строки.
    private int? DeleteBatchEndId(int collectionId, int prevId, int offset)
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        c.CommandText = $@"SELECT Id FROM db_records
            WHERE CollectionId = {collectionId} AND Id > {prevId}
            ORDER BY Id LIMIT 1 OFFSET {offset - 1}";
        using var rdr = c.ExecuteReader();
        return rdr.Read() ? rdr.GetInt32(0) : null;
    }

    // «Скелет» дубля: новая коллекция + число записей источника (для строк-заглушек таблицы),
    // пока сами записи/значения копируются отдельно (см. CopyCollectionData).
    public sealed record CollectionShell(DbCollection Collection, int SourceRecordCount);

    public DbCollection DuplicateCollection(int collectionId)
    {
        Demand(PermAction.DuplicateCollection, collectionId);
        var shell = CreateCollectionShell(collectionId);
        if (shell.Collection.Id is int newId)
            CopyCollectionData(collectionId, newId);
        LogNow("duplicate", "collection", shell.Collection.Name, count: shell.SourceRecordCount);
        return shell.Collection;
    }

    /// <summary>
    /// Создать «скелет» дубля коллекции: строка коллекции + копии полей (колонок), без записей.
    /// Записи/значения копирует затем <see cref="CopyCollectionData"/> (sync) или
    /// <see cref="DuplicateCollectionIsolated"/> (фон + прогресс). Возвращает новую коллекцию
    /// и число записей источника.
    /// </summary>
    /// <param name="overrideGroupId">Если задан — коллекция-копия кладётся в ЭТУ группу (для дубля
    /// группы: копии коллекций идут в новые подгруппы), иначе в группу источника.</param>
    /// <param name="addCopySuffix">true — имя «X (копия)» (дубль одной коллекции); false — имя как у
    /// источника (дубль группы: суффикс «(копия)» получает только верхняя группа, вложенное — нет).</param>
    public CollectionShell CreateCollectionShell(int collectionId, int? overrideGroupId = null, bool addCopySuffix = true)
    {
        var src = db.DbCollections.Find(collectionId);
        if (src is null) throw new InvalidOperationException($"Collection {collectionId} not found");

        var maxPos     = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        var targetGroup = overrideGroupId ?? src.GroupId;

        // Уникальное имя на целевом уровне: «X (копия)», «X (копия) 2» … (без стакания «копия»).
        // При дубле группы (addCopySuffix=false) имя коллекции сохраняем — новая подгруппа пуста, конфликта нет.
        var name = addCopySuffix
            ? NextCopyName(src.Name, candidate => db.DbCollections.Any(c => c.GroupId == targetGroup && c.Name == candidate))
            : src.Name;

        var copy = new DbCollectionEntity
        {
            Name     = name,
            GroupId  = targetGroup,
            Position = maxPos + 1000,
            Icon     = src.Icon,
            Color    = src.Color,
        };
        db.DbCollections.Add(copy);
        db.SaveChanges();

        // Поля (колонки) немногочисленны — копируем через EF в порядке Id; этот же порядок
        // позволит сопоставить старые и новые поля при копировании значений (CopyCollectionData).
        var fields = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId)
            .OrderBy(f => f.Id)
            .ToList();
        foreach (var f in fields)
        {
            db.DbFields.Add(new DbFieldEntity
            {
                CollectionId = copy.Id, Name = f.Name, FieldType = f.FieldType,
                ConfigJson = f.ConfigJson, Position = f.Position,
                IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
                IsSystem = f.IsSystem, IsRequired = f.IsRequired,
                IsTaskExec = f.IsTaskExec, IsTaskAssigner = f.IsTaskAssigner, IsTaskStatus = f.IsTaskStatus,
            });
        }
        db.SaveChanges();

        var recCount = db.DbRecords.Count(r => r.CollectionId == collectionId);
        var dto = new DbCollection
        {
            Id = copy.Id, Name = copy.Name, GroupId = copy.GroupId,
            Position = copy.Position, Icon = copy.Icon, Color = copy.Color,
        };
        return new CollectionShell(dto, recCount);
    }

    /// <summary>
    /// Скопировать записи и значения из исходной коллекции в уже созданный «скелет» (newCollectionId).
    /// Только bulk-SQL без материализации в EF. Карта полей old→new — по Position (скелет копирует
    /// Position дословно). Всё в ОДНОЙ транзакции (атомарно, без частичной копии при ошибке):
    ///  1) ВСЕ записи одним `INSERT … SELECT … ORDER BY Position, Id` (id новых записей — непрерывный
    ///     блок в этом порядке: firstNew = last_insert_rowid − recCount + 1);
    ///  2) значения — КРУПНЫМИ пакетами (по batchSize записей), но ВСЕ поля за один `INSERT` через
    ///     временную карту полей `_dup_fmap` (а не по одному полю с `ROW_NUMBER` на каждом — это было
    ///     в ~3.4× медленнее). Пакеты нужны лишь для гранулярности прогресса (окно с %); внутри
    ///     пакета новый RecordId = (firstNew + processed) + локальный ранг − 1. Тем же пакетом
    ///     копируются строки проекции `calendar_index` (1:1 c перемэппингом id, без пересборки).
    /// <paramref name="progress"/> — нарастающее число скопированных записей (для процентов в окне).
    /// </summary>
    public void CopyCollectionData(int srcCollectionId, int newCollectionId,
                                   IProgress<int>? progress = null, int batchSize = 10000)
    {
        // PG: непрерывного блока id не существует (identity-последовательность делят все
        // клиенты) — карта id строится явно через nextval, см. CopyCollectionDataPg.
        if (db.IsPostgres)
        {
            CopyCollectionDataPg(srcCollectionId, newCollectionId, progress, batchSize);
            return;
        }

        var recCount = db.DbRecords.Count(r => r.CollectionId == srcCollectionId);

        // old FieldId → new FieldId по Position (см. BuildFieldMap). Карта нужна и для значений,
        // и для перемэппинга ключей UI-колонок (ширины/порядок/видимость).
        var fieldMap = BuildFieldMap(srcCollectionId, newCollectionId);

        using var tx = db.Database.BeginTransaction();

        // Временная карта полей (old_fid → new_fid) — чтобы копировать значения ВСЕХ полей за один
        // INSERT (JOIN _dup_fmap), а не по одному полю; та же карта перемэппливает ключи UI-колонок.
        // Создаём заранее (нужна и при пустой коллекции — UI колонок копируем без записей). Живёт на
        // этом подключении; пересоздаём.
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_fmap");
        db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
        foreach (var (oldFid, newFid) in fieldMap)
            db.Database.ExecuteSqlInterpolated($"INSERT INTO _dup_fmap(old_fid, new_fid) VALUES ({oldFid}, {newFid})");

        long firstNew = 0;
        if (recCount > 0)
        {
            var createdAt = DateTime.Now.ToString("o");
            var createdBy = CurrentUser.DisplayName;   // дубль коллекции = новые записи (свой автор/время)

            // 1) Все записи одним INSERT … SELECT в порядке (Position, Id). Новые id — непрерывный блок
            //    в этом порядке (SQLite присваивает rowid по порядку SELECT, мы единственный писатель).
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Description)
                SELECT {newCollectionId}, {createdAt}, {createdAt}, {createdBy}, {createdBy}, Position, StartDt, EndDt, AllDay, Title, Description
                FROM db_records WHERE CollectionId = {srcCollectionId} ORDER BY Position, Id");
            firstNew = LastInsertRowid() - recCount + 1;

            // 2) Значения и проекция календаря крупными пакетами по (Position, Id): на пакет — один
            //    INSERT значений по всем полям и один INSERT строк calendar_index. Проекция источника
            //    всегда полная (видимость поля — фильтр чтения, см. SetDateFieldVisible), поэтому дубль
            //    копирует её 1:1 с перемэппингом id — прежняя пересборка (RebuildCalendarIndexForCollection:
            //    чтение значений EF-страницами + парсинг дат + по-строчный INSERT) на больших коллекциях
            //    добавляла минуты «висящего» на 100% прогресса ПОСЛЕ копии данных. Числа в SQL — int/long,
            //    которые мы формируем сами, поэтому интерполяция в Raw безопасна (как в attached-переносе).
            void CopySlice(long bbase, int fromPos, int fromId, (int Pos, int Id)? upper)
            {
                var slice = $"(Position > {fromPos} OR (Position = {fromPos} AND Id > {fromId}))" +
                            (upper is { } u ? $" AND (Position < {u.Pos} OR (Position = {u.Pos} AND Id <= {u.Id}))" : "");
                var rank = $@"(SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                              FROM db_records WHERE CollectionId = {srcCollectionId} AND {slice}) r";
                db.Database.ExecuteSqlRaw($@"
                    INSERT INTO db_field_values (RecordId, FieldId, Value)
                    SELECT {bbase} + r.rn - 1, fm.new_fid, fv.Value
                    FROM db_field_values fv
                    JOIN {rank} ON r.old_id = fv.RecordId
                    JOIN _dup_fmap fm ON fm.old_fid = fv.FieldId");
                db.Database.ExecuteSqlRaw($@"
                    INSERT INTO calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay)
                    SELECT {bbase} + r.rn - 1, fm.new_fid, {newCollectionId}, ci.StartIso, ci.EndIso, ci.AllDay
                    FROM calendar_index ci
                    JOIN {rank} ON r.old_id = ci.RecordId
                    JOIN _dup_fmap fm ON fm.old_fid = ci.FieldId");
            }

            int prevPos = int.MinValue, prevId = 0, processed = 0;
            while (processed < recCount)
            {
                var bbase = firstNew + processed;   // новый RecordId первой записи пакета = bbase
                var end   = BatchEndCursor(srcCollectionId, prevPos, prevId, batchSize);
                CopySlice(bbase, prevPos, prevId, end);
                if (end is { } e)
                {
                    processed += batchSize;
                    prevPos = e.Pos; prevId = e.Id;
                }
                else
                {
                    processed = recCount;   // последний (неполный) пакет: без верхней границы
                }
                progress?.Report(Math.Min(processed, recCount));
            }
        }
        else
        {
            progress?.Report(0);
        }

        // 3) UI-настройки коллекции (ширины/порядок/видимость колонок, высоты/видимость строк, режим
        //    просмотра, подписи системных колонок) — чтобы дубль был ПОЛНОЙ копией, как перенос в другую
        //    БД (см. CopyMovedUi). Поле-/коллекция-level копируем всегда, строки — только при наличии записей.
        CopyDuplicatedUi(srcCollectionId, newCollectionId, firstNew, recCount);

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_fmap");
        tx.Commit();
    }

    // UI-настройки коллекции при дубле В ТОЙ ЖЕ БД: ключи-id полей перемэппливаются через временную
    // карту _dup_fmap (та же, что для значений), записи — по рангу (Position, Id) → firstNew + ранг − 1
    // (новые id непрерывны). Ключи column_meta: числовой id поля → новый, системные
    // (__title__/__desc__) — как есть. Поле-/коллекция-level копируем всегда; строки (row_heights,
    // record_display) — только при наличии записей. Зеркало CopyMovedUi для одной БД (без ATTACH).
    private void CopyDuplicatedUi(int srcCol, int newCol, long firstNew, long recCount)
    {
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO ui_col_widths (CollectionId, FieldId, Width)
            SELECT {newCol}, fm.new_fid, w.Width FROM ui_col_widths w
            JOIN _dup_fmap fm ON fm.old_fid = w.FieldId WHERE w.CollectionId = {srcCol}");

        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO ui_collection_settings (CollectionId, ColResize, RowResize, ViewMode)
            SELECT {newCol}, ColResize, RowResize, ViewMode FROM ui_collection_settings WHERE CollectionId = {srcCol}");

        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName)
            SELECT {newCol},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cm.Key END,
                   cm.Position, cm.Visible, cm.Level, cm.DisplayName
            FROM column_meta cm
            LEFT JOIN _dup_fmap fm ON cm.Key GLOB '[0-9]*' AND CAST(cm.Key AS INTEGER) = fm.old_fid
            WHERE cm.CollectionId = {srcCol}");

        if (recCount > 0)
        {
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO ui_row_heights (CollectionId, RecordId, Height)
                SELECT {newCol}, {firstNew} + r.rn - 1, h.Height FROM ui_row_heights h
                JOIN (SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                      FROM db_records WHERE CollectionId = {srcCol}) r ON r.old_id = h.RecordId
                WHERE h.CollectionId = {srcCol}");

            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO record_display (CollectionId, RecordId, Visible, Level)
                SELECT {newCol}, {firstNew} + r.rn - 1, d.Visible, d.Level FROM record_display d
                JOIN (SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                      FROM db_records WHERE CollectionId = {srcCol}) r ON r.old_id = d.RecordId
                WHERE d.CollectionId = {srcCol}");
        }
    }

    // PG-вариант дубля данных коллекции (см. CopyCollectionData — SQLite-путь). Арифметика
    // «firstNew + ранг − 1» на PG неприменима: identity-последовательность db_records общая
    // для всех клиентов, соседние id может занять чужая вставка. Вместо этого карта id строится
    // явно: временная _dup_rmap(old_id, new_id, rn), где new_id — nextval() последовательности
    // (конкурентно-безопасно: выданные значения никому больше не достанутся), записи вставляются
    // с ЯВНЫМИ id по карте (identity BY DEFAULT это допускает и не рассинхронизируется — id взяты
    // из самой последовательности), значения/проекция/UI-строки джойнятся через карту. Одна
    // транзакция, батчи по rn — только ради гранулярности прогресса, как в SQLite-пути.
    private void CopyCollectionDataPg(int srcCollectionId, int newCollectionId,
                                      IProgress<int>? progress, int batchSize)
    {
        var recCount = db.DbRecords.Count(r => r.CollectionId == srcCollectionId);

        var fieldMap = BuildFieldMap(srcCollectionId, newCollectionId);

        using var tx = db.Database.BeginTransaction();

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_fmap");
        db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
        foreach (var (oldFid, newFid) in fieldMap)
            db.Database.ExecuteSqlInterpolated($"INSERT INTO _dup_fmap(old_fid, new_fid) VALUES ({oldFid}, {newFid})");

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_rmap");
        db.Database.ExecuteSqlRaw($@"
            CREATE TEMP TABLE _dup_rmap AS
            SELECT Id AS old_id,
                   nextval(pg_get_serial_sequence('db_records', 'id')) AS new_id,
                   ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
            FROM db_records WHERE CollectionId = {srcCollectionId}");

        if (recCount > 0)
        {
            var createdAt = DateTime.Now.ToString("o");
            var createdBy = CurrentUser.DisplayName;   // дубль коллекции = новые записи (свой автор/время)
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Description)
                SELECT m.new_id, {newCollectionId}, {createdAt}, {createdAt}, {createdBy}, {createdBy}, r.Position, r.StartDt, r.EndDt, r.AllDay, r.Title, r.Description
                FROM db_records r JOIN _dup_rmap m ON m.old_id = r.Id");

            var processed = 0;
            while (processed < recCount)
            {
                var lo = processed;
                var hi = Math.Min(recCount, processed + batchSize);
                db.Database.ExecuteSqlInterpolated($@"
                    INSERT INTO db_field_values (RecordId, FieldId, Value)
                    SELECT m.new_id, fm.new_fid, fv.Value
                    FROM db_field_values fv
                    JOIN _dup_rmap m  ON m.old_id  = fv.RecordId
                    JOIN _dup_fmap fm ON fm.old_fid = fv.FieldId
                    WHERE m.rn > {lo} AND m.rn <= {hi}");
                db.Database.ExecuteSqlInterpolated($@"
                    INSERT INTO calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay)
                    SELECT m.new_id, fm.new_fid, {newCollectionId}, ci.StartIso, ci.EndIso, ci.AllDay
                    FROM calendar_index ci
                    JOIN _dup_rmap m  ON m.old_id  = ci.RecordId
                    JOIN _dup_fmap fm ON fm.old_fid = ci.FieldId
                    WHERE m.rn > {lo} AND m.rn <= {hi}");
                processed = hi;
                progress?.Report(processed);
            }
        }
        else
        {
            progress?.Report(0);
        }

        // UI-настройки коллекции — зеркало CopyDuplicatedUi, записи через карту _dup_rmap.
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO ui_col_widths (CollectionId, FieldId, Width)
            SELECT {newCollectionId}, fm.new_fid, w.Width FROM ui_col_widths w
            JOIN _dup_fmap fm ON fm.old_fid = w.FieldId WHERE w.CollectionId = {srcCollectionId}");

        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO ui_collection_settings (CollectionId, ColResize, RowResize, ViewMode)
            SELECT {newCollectionId}, ColResize, RowResize, ViewMode FROM ui_collection_settings
            WHERE CollectionId = {srcCollectionId}");

        // Числовой ключ column_meta сопоставляется текстом (Key = CAST(old_fid AS TEXT)) —
        // без GLOB (его в PG нет) и без CAST(Key AS INTEGER), падающего на '__title__'.
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName)
            SELECT {newCollectionId},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cm.Key END,
                   cm.Position, cm.Visible, cm.Level, cm.DisplayName
            FROM column_meta cm
            LEFT JOIN _dup_fmap fm ON cm.Key = CAST(fm.old_fid AS TEXT)
            WHERE cm.CollectionId = {srcCollectionId}");

        if (recCount > 0)
        {
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO ui_row_heights (CollectionId, RecordId, Height)
                SELECT {newCollectionId}, m.new_id, h.Height FROM ui_row_heights h
                JOIN _dup_rmap m ON m.old_id = h.RecordId WHERE h.CollectionId = {srcCollectionId}");

            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO record_display (CollectionId, RecordId, Visible, Level)
                SELECT {newCollectionId}, m.new_id, d.Visible, d.Level FROM record_display d
                JOIN _dup_rmap m ON m.old_id = d.RecordId WHERE d.CollectionId = {srcCollectionId}");
        }

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_rmap");
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_fmap");
        tx.Commit();
    }

    // Карта полей old→new по Position (скелет копирует Position дословно, Position уникален
    // в пределах коллекции) — общая для SQLite- и PG-путей дубля.
    private Dictionary<int, int> BuildFieldMap(int srcCollectionId, int newCollectionId)
    {
        var oldFields = db.DbFields.AsNoTracking().Where(f => f.CollectionId == srcCollectionId)
            .Select(f => new { f.Id, f.Position }).ToList();
        var newByPos = new Dictionary<int, int>();
        foreach (var nf in db.DbFields.AsNoTracking().Where(f => f.CollectionId == newCollectionId)
                     .Select(f => new { f.Id, f.Position }))
            newByPos[nf.Position] = nf.Id;
        var fieldMap = new Dictionary<int, int>(oldFields.Count);
        foreach (var of in oldFields)
            if (newByPos.TryGetValue(of.Position, out var newFid)) fieldMap[of.Id] = newFid;
        return fieldMap;
    }

    // id последней вставленной строки на текущем подключении (в открытой транзакции).
    private long LastInsertRowid()
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        c.CommandText = "SELECT last_insert_rowid()";
        return (long)c.ExecuteScalar()!;
    }

    // Именованный параметр для сырых ADO.NET-команд (bulk-импорт). Имя — как плейсхолдер (@name).
    // Тип обязателен для команд с Prepare(): Npgsql должен знать типы параметров ДО присвоения
    // значений (SQLite безразличен) — иначе Prepare падает «must have its NpgsqlDbType … set».
    private static System.Data.Common.DbParameter AddParam(System.Data.Common.DbCommand cmd, string name,
                                                           System.Data.DbType? type = null)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        if (type is { } t) p.DbType = t;
        cmd.Parameters.Add(p);
        return p;
    }

    // (Position, Id) записи на позиции offset−1 в keyset-срезе источника после курсора — т.е.
    // граница пакета из batchSize записей. null, если записей меньше (последний неполный пакет).
    // Параметры — int, не строки.
    private (int Pos, int Id)? BatchEndCursor(int srcCollectionId, int prevPos, int prevId, int offset)
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        c.CommandText = $@"
            SELECT Position, Id FROM db_records
            WHERE CollectionId = {srcCollectionId}
              AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))
            ORDER BY Position, Id
            LIMIT 1 OFFSET {offset - 1}";
        using var rdr = c.ExecuteReader();
        return rdr.Read() ? (rdr.GetInt32(0), rdr.GetInt32(1)) : null;
    }

    /// <summary>
    /// Полный дубль коллекции (коллекция + колонки + записи + значения) на ОТДЕЛЬНОМ подключении.
    /// Для фонового дублирования больших коллекций с окном прогресса: НЕ трогает общий Context
    /// (см. правило об общем AppDbContext — фоновая работа только на свежем подключении), поэтому
    /// безопасно вызывать из Task.Run. <paramref name="progress"/> сообщает нарастающее число
    /// скопированных записей (для процентов в окне). Возвращает id новой коллекции (0 при ошибке).
    /// </summary>
    public int DuplicateCollectionIsolated(int srcCollectionId, IProgress<int>? progress = null)
    {
        Demand(PermAction.DuplicateCollection, srcCollectionId);
        return DuplicateCollectionIsolatedCore(srcCollectionId, progress);
    }

    private int DuplicateCollectionIsolatedCore(int srcCollectionId, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        var repo  = new ConstructorRepository(ctx);
        var shell = repo.CreateCollectionShell(srcCollectionId);
        if (shell.Collection.Id is not int newId) return 0;
        repo.CopyCollectionData(srcCollectionId, newId, progress);
        repo.LogNow("duplicate", "collection", shell.Collection.Name, count: shell.SourceRecordCount);
        return newId;
    }

    /// <summary>
    /// Потоковый импорт в новую коллекцию на ОТДЕЛЬНОМ подключении (не общий <c>Context</c>).
    /// Вызывается уже с фонового потока (источник читается по ходу записи), поэтому синхронно.
    /// Главное — НЕ трогать общий контекст с фонового потока: иначе параллельное обращение к нему
    /// из UI (дерево/календарь) или фоновое копирование дубля валит EF («A second operation was
    /// started on this context instance»). Свежий контекст + WAL + busy_timeout делают импорт
    /// безопасным рядом с любыми другими операциями над той же БД.
    /// </summary>
    public int ImportStreamedCollectionIsolated(string collectionName, int? groupId, ImportSchema schema,
        IEnumerable<ImportRow> rows, CancellationToken ct, IProgress<long>? rowsWritten = null)
    {
        Demand(PermAction.CreateCollection);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();   // большой кеш+mmap — без них вставка в глобальные индексы db_field_values деградирует
        return new ConstructorRepository(ctx).ImportStreamedCollection(collectionName, groupId, schema, rows, ct, rowsWritten);
    }

    // ── Экспорт таблиц (Core/Exporters, см. docs/features/import-export.md) ──
    //
    // Экспорт — чистое чтение: все методы работают на ОТДЕЛЬНЫХ подключениях (вызовы
    // идут с фонового потока оркестрации, общий Context трогать нельзя — правило
    // однопоточности), WAL допускает такое чтение рядом с любыми операциями. Demand не
    // зовётся: изолированные клоны прав не несут (см. Permissions), проверку
    // ViewCollection делает оркестрация по кешу прав до начала чтения.

    /// <summary>
    /// Колонки экспорта в порядке таблицы: сохранённый column_order, иначе дефолт как у
    /// TableView.EffectiveColumnPosition (Название=1000, Описание=2000, поле=3000+Position).
    /// markdown-поля исключаются (они не колонки таблицы). includeHidden=false — только
    /// видимые колонки (column_display; отсутствие ключа = видима). Подписи системных
    /// колонок — из system_field_meta (дефолты «Название»/«Описание»).
    /// </summary>
    public List<ExportColumn> GetExportColumns(int collectionId, bool includeHidden)
    {
        var fields = GetFields(collectionId).Where(f => f.FieldType != "markdown").ToList();
        var names  = GetSystemFieldNames(collectionId);
        var order  = GetColumnOrder(collectionId);
        HashSet<string> hidden = includeHidden
            ? []
            : GetColumnDisplay(collectionId).Where(kv => !kv.Value.Visible).Select(kv => kv.Key).ToHashSet();

        int Pos(string key, int fieldPos) =>
            order.TryGetValue(key, out var p) ? p
            : key == TitleKey ? 1000
            : key == DescKey  ? 2000
            : 3000 + fieldPos;

        var cols = new List<(ExportColumn Col, int Pos)>
        {
            (new ExportColumn(TitleKey, names.GetValueOrDefault(TitleKey, "Название"), null, "text"), Pos(TitleKey, 0)),
            (new ExportColumn(DescKey,  names.GetValueOrDefault(DescKey,  "Описание"), null, "text"), Pos(DescKey, 0)),
        };
        foreach (var f in fields)
        {
            var key = f.Id!.Value.ToString();
            cols.Add((new ExportColumn(key, f.Name, f.Id, f.FieldType, f.ConfigJson), Pos(key, f.Position)));
        }

        // Стабильная сортировка: у колонок с равными позициями сохраняется порядок добавления.
        return cols
            .Where(c => !hidden.Contains(c.Col.Key))
            .OrderBy(c => c.Pos)
            .Select(c => c.Col)
            .ToList();
    }

    /// <summary>То же на ОТДЕЛЬНОМ подключении — для вызова с фонового потока оркестрации.</summary>
    public List<ExportColumn> GetExportColumnsIsolated(int collectionId, bool includeHidden)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return new ConstructorRepository(ctx).GetExportColumns(collectionId, includeHidden);
    }

    /// <summary>Синхронный COUNT на ОТДЕЛЬНОМ подключении (оркестрация уже в Task.Run) —
    /// точный total для процентов фазы записи.</summary>
    public int CountRecordsIsolated(int collectionId, bool excludeHidden = false)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return new ConstructorRepository(ctx).CountRecords(collectionId, excludeHidden);
    }

    /// <summary>
    /// Строки коллекции ПОТОКОМ для экспорта на ОТДЕЛЬНОМ подключении: keyset-страницы
    /// <see cref="GetRecordsPageAfter"/> (row-value сик, O(take) на любой глубине) +
    /// значения пакетом <see cref="GetFieldValuesBatch"/> (EF9 транслирует Contains в
    /// json_each — один параметр, лимита 999 нет). Память O(страницы). Bulk-прагмы:
    /// чтение db_field_values на десятках ГБ в стандартные 64 МБ кеша не влезает.
    /// Значения — string[] в порядке columns: Название → Title, Описание → Description,
    /// поле → значение (отсутствующее → ""). rowsRead — нарастающий итог раз в страницу.
    /// </summary>
    public IEnumerable<string[]> ReadExportRowsIsolated(int collectionId,
        IReadOnlyList<ExportColumn> columns, bool excludeHidden, CancellationToken ct,
        IProgress<long>? rowsRead = null, int pageSize = 2000)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        var repo = new ConstructorRepository(ctx);

        long total = 0;
        int? afterPos = null, afterId = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = repo.GetRecordsPageAfter(collectionId, afterPos, afterId, pageSize, excludeHidden);
            if (page.Count == 0) yield break;

            var values = repo.GetFieldValuesBatch(
                page.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToList());

            foreach (var r in page)
            {
                var vals = r.Id is int rid ? values.GetValueOrDefault(rid) : null;
                var row  = new string[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    var c = columns[i];
                    row[i] = c.Key == TitleKey ? r.Title
                           : c.Key == DescKey  ? r.Description ?? ""
                           : c.FieldId is int fid && vals is not null ? vals.GetValueOrDefault(fid, "") : "";
                }
                yield return row;
            }

            total += page.Count;
            rowsRead?.Report(total);
            var last = page[^1];
            afterPos = last.Position;
            afterId  = last.Id;
        }
    }

    /// <summary>Строка журнала «экспортировал коллекцию …» на ОТДЕЛЬНОМ подключении
    /// (LogNow пишет через тот же контекст, что и операция — здесь операция фоновая).</summary>
    public void LogExportIsolated(string collectionName, long count)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        new ConstructorRepository(ctx).LogNow("export", "collection", collectionName, count: count);
    }

    // ── Перенос между БД (мульти-маунт) ───────────────────────────────────────
    //
    // Из-за изоляции id (DbId, Id) коллекция/группа физически живут в своём .db-файле,
    // поэтому «перенос в другую БД» = глубокая копия снимка в целевой репозиторий (с
    // перенумерацией id) + удаление из исходного. Переносится всё: поля, записи, значения
    // и UI-настройки коллекции (ширины/порядок/видимость колонок, высоты строк, отображение
    // записей, подписи системных колонок) — ключи-id (поле/запись) перемэппливаются на новые.

    // Снимок коллекции: метаданные + поля + записи со значениями + UI-настройки.
    // Ключи значений и UI — СТАРЫЕ id поля/записи; при импорте перемэппливаются.
    public sealed record CollectionSnapshot(DbCollection Collection, List<DbField> Fields,
                                            List<RecordSnapshot> Records, UiSnapshot Ui);
    public sealed record RecordSnapshot(DbRecord Record, Dictionary<int, string> ValuesByOldFieldId);

    // UI-настройки коллекции для переноса (отвязанные от контекста-источника значения).
    public sealed record UiSnapshot(
        List<(int FieldId, int Width)>                        ColWidths,
        List<(int RecordId, int Height)>                      RowHeights,
        (bool ColResize, bool RowResize, string ViewMode)?    Settings,
        List<(string Key, int? Position, bool? Visible, int? Level, string? DisplayName)> ColumnMeta,
        List<(int RecordId, bool Visible, int Level)>         RecordDisplay);

    // Снимок группы со всей вложенной веткой (подгруппы + коллекции).
    public sealed record GroupSnapshot(DbGroup Group, List<GroupSnapshot> Subgroups, List<CollectionSnapshot> Collections);

    /// <summary>Снять коллекцию (с полями, записями и значениями) для переноса в другую БД.</summary>
    public CollectionSnapshot ExportCollection(int collectionId)
    {
        var col     = GetAllCollections().First(c => c.Id == collectionId);
        var fields  = GetFields(collectionId);
        var records = GetRecords(collectionId);
        var byRec   = GetFieldValuesBatch(records.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToList());

        var recSnaps = new List<RecordSnapshot>(records.Count);
        foreach (var r in records)
        {
            Dictionary<int, string> vals = new();
            if (r.Id is int rid && byRec.TryGetValue(rid, out var v)) vals = v;
            recSnaps.Add(new RecordSnapshot(r, vals));
        }

        return new CollectionSnapshot(col, fields, recSnaps, ExportUi(collectionId));
    }

    // Снять UI-настройки коллекции в отвязанные значения (AsNoTracking — детач от источника).
    private UiSnapshot ExportUi(int collectionId)
    {
        var settings = db.UiCollectionSettings.AsNoTracking().FirstOrDefault(s => s.CollectionId == collectionId);
        return new UiSnapshot(
            ColWidths:     db.UiColWidths.AsNoTracking().Where(w => w.CollectionId == collectionId).ToList()
                             .Select(w => (w.FieldId, w.Width)).ToList(),
            RowHeights:    db.UiRowHeights.AsNoTracking().Where(h => h.CollectionId == collectionId).ToList()
                             .Select(h => (h.RecordId, h.Height)).ToList(),
            Settings:      settings is null ? null : (settings.ColResize, settings.RowResize, settings.ViewMode),
            ColumnMeta:    db.ColumnMeta.AsNoTracking().Where(m => m.CollectionId == collectionId).ToList()
                             .Select(m => (m.Key, m.Position, m.Visible, m.Level, m.DisplayName)).ToList(),
            RecordDisplay: db.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == collectionId).ToList()
                             .Select(d => (d.RecordId, d.Visible, d.Level)).ToList());
    }

    /// <summary>Импортировать снимок коллекции под группу targetGroupId — атомарно (одна транзакция). Возвращает новую коллекцию.</summary>
    public DbCollection ImportCollection(CollectionSnapshot snap, int? targetGroupId)
    {
        using var tx = db.Database.BeginTransaction();
        var result = ImportCollectionCore(snap, targetGroupId);
        tx.Commit();
        return result;
    }

    private DbCollection ImportCollectionCore(CollectionSnapshot snap, int? targetGroupId)
    {
        var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        var copy = new DbCollectionEntity
        {
            Name = snap.Collection.Name, GroupId = targetGroupId,
            Position = maxPos + 1000, Icon = snap.Collection.Icon, Color = snap.Collection.Color,
        };
        db.DbCollections.Add(copy);
        db.SaveChanges();

        var fieldMap = new Dictionary<int, int>();   // старый id поля → новый
        foreach (var f in snap.Fields)
        {
            var nf = new DbFieldEntity
            {
                CollectionId = copy.Id, Name = f.Name, FieldType = f.FieldType,
                ConfigJson = f.ConfigJson, Position = f.Position,
                IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
                IsSystem = f.IsSystem, IsRequired = f.IsRequired,
                IsTaskExec = f.IsTaskExec, IsTaskAssigner = f.IsTaskAssigner, IsTaskStatus = f.IsTaskStatus,
            };
            db.DbFields.Add(nf);
            db.SaveChanges();
            if (f.Id is int oldFid) fieldMap[oldFid] = nf.Id;
        }

        var recordMap = new Dictionary<int, int>();   // старый id записи → новый
        foreach (var rs in snap.Records)
        {
            var r = rs.Record;
            var nr = new DbRecordEntity
            {
                CollectionId = copy.Id, CreatedAt = r.CreatedAt ?? DateTime.Now.ToString("o"),
                Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt,
                AllDay = r.AllDay, Title = r.Title, Description = r.Description,
            };
            db.DbRecords.Add(nr);
            db.SaveChanges();
            if (r.Id is int oldRid) recordMap[oldRid] = nr.Id;

            foreach (var (oldFid, val) in rs.ValuesByOldFieldId)
                if (fieldMap.TryGetValue(oldFid, out var nfid))
                    db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = nr.Id, FieldId = nfid, Value = val });
            db.SaveChanges();
        }

        ImportUi(snap.Ui, copy.Id, fieldMap, recordMap);

        return new DbCollection
        {
            Id = copy.Id, Name = copy.Name, GroupId = copy.GroupId,
            Position = copy.Position, Icon = copy.Icon, Color = copy.Color,
        };
    }

    // Восстановить UI-настройки в новой коллекции: id полей/записей — через карты,
    // системные ключи (__title__/__desc__) и настройки коллекции — как есть.
    private void ImportUi(UiSnapshot ui, int newCollectionId,
                          Dictionary<int, int> fieldMap, Dictionary<int, int> recordMap)
    {
        foreach (var (oldFid, width) in ui.ColWidths)
            if (fieldMap.TryGetValue(oldFid, out var nfid))
                db.UiColWidths.Add(new UiColWidthEntity { CollectionId = newCollectionId, FieldId = nfid, Width = width });

        foreach (var (oldRid, height) in ui.RowHeights)
            if (recordMap.TryGetValue(oldRid, out var nrid))
                db.UiRowHeights.Add(new UiRowHeightEntity { CollectionId = newCollectionId, RecordId = nrid, Height = height });

        if (ui.Settings is { } st)
            db.UiCollectionSettings.Add(new UiCollectionSettingEntity
            {
                CollectionId = newCollectionId, ColResize = st.ColResize, RowResize = st.RowResize, ViewMode = st.ViewMode,
            });

        foreach (var (key, pos, vis, lvl, name) in ui.ColumnMeta)
            db.ColumnMeta.Add(new ColumnMetaEntity
            {
                CollectionId = newCollectionId, Key = RemapColKey(key, fieldMap),
                Position = pos, Visible = vis, Level = lvl, DisplayName = name,
            });

        foreach (var (oldRid, vis, lvl) in ui.RecordDisplay)
            if (recordMap.TryGetValue(oldRid, out var nrid))
                db.RecordDisplay.Add(new RecordDisplayEntity
                {
                    CollectionId = newCollectionId, RecordId = nrid, Visible = vis, Level = lvl,
                });

        db.SaveChanges();
    }

    // Ключ колонки: системные (__title__/__desc__) оставляем как есть; числовой id поля — на новый.
    private static string RemapColKey(string key, Dictionary<int, int> fieldMap) =>
        int.TryParse(key, out var oldFid) && fieldMap.TryGetValue(oldFid, out var nfid) ? nfid.ToString() : key;

    /// <summary>Снять группу со всей вложенной веткой (подгруппы + коллекции) для переноса в другую БД.</summary>
    public GroupSnapshot ExportGroup(int groupId)
    {
        var groups = GetAllGroups();
        var g      = groups.First(x => x.Id == groupId);
        var subs   = groups.Where(x => x.ParentId == groupId)
                           .Select(x => ExportGroup(x.Id!.Value)).ToList();
        var cols   = GetCollectionsByGroup(groupId)
                           .Select(c => ExportCollection(c.Id!.Value)).ToList();
        return new GroupSnapshot(g, subs, cols);
    }

    /// <summary>Импортировать снимок группы (с веткой) под targetParentId — атомарно (одна транзакция). Возвращает новую группу.</summary>
    public DbGroup ImportGroup(GroupSnapshot snap, int? targetParentId)
    {
        using var tx = db.Database.BeginTransaction();
        var result = ImportGroupCore(snap, targetParentId);
        tx.Commit();
        return result;
    }

    private DbGroup ImportGroupCore(GroupSnapshot snap, int? targetParentId)
    {
        var maxPos = db.DbGroups.Where(x => x.ParentId == targetParentId).Select(x => (int?)x.Position).Max() ?? 0;
        var copy = new DbGroupEntity { Name = snap.Group.Name, ParentId = targetParentId, Position = maxPos + 1000 };
        db.DbGroups.Add(copy);
        db.SaveChanges();

        // Внутри уже открытой транзакции — зовём *Core (без вложенной транзакции).
        foreach (var sub in snap.Subgroups)   ImportGroupCore(sub, copy.Id);
        foreach (var col in snap.Collections) ImportCollectionCore(col, copy.Id);

        return new DbGroup { Id = copy.Id, Name = copy.Name, ParentId = copy.ParentId, Position = copy.Position };
    }

    // ── Перенос целиком: export → import (в транзакции) → delete; откат копии при сбое ──
    //
    // import уже атомарен (транзакция целевой БД) — частичной копии не бывает. Если удаление
    // из исходной упадёт после копии, удаляем копию из целевой (компенсация) и возвращаемся к
    // исходному состоянию. Дубль возможен лишь при двойном сбое (исчезающе редко) — данные целы.

    /// <summary>Перенести коллекцию из source в target (с именем newName). Бросает при сбое — после отката копии.</summary>
    public static DbCollection MoveCollection(ConstructorRepository source, ConstructorRepository target,
                                              int collectionId, int? targetGroupId, string newName)
    {
        // Перенос = изъятие из источника + создание в целевой: проверяем права ОБЕИХ баз.
        source.Demand(PermAction.DeleteCollection, collectionId);
        target.Demand(PermAction.CreateCollection);
        var snap = source.ExportCollection(collectionId);
        snap.Collection.Name = newName;
        var moved = target.ImportCollection(snap, targetGroupId);
        try
        {
            source.DeleteCollection(collectionId);
        }
        catch
        {
            if (moved.Id is int newId) target.DeleteCollection(newId);   // откат копии — без дубля
            throw;
        }
        target.LogNow("move", "collection", newName);
        return moved;
    }

    /// <summary>Перенести группу (с веткой) из source в target. Бросает при сбое — после отката копии.</summary>
    public static DbGroup MoveGroup(ConstructorRepository source, ConstructorRepository target,
                                    int groupId, int? targetParentId, string newName)
    {
        // Перенос = изъятие из источника + создание в целевой: проверяем права ОБЕИХ баз.
        source.Demand(PermAction.DeleteCollection);
        target.Demand(PermAction.CreateCollection);
        var snap = source.ExportGroup(groupId);
        snap.Group.Name = newName;
        var moved = target.ImportGroup(snap, targetParentId);
        try
        {
            source.DeleteGroup(groupId);
        }
        catch
        {
            if (moved.Id is int newId) target.DeleteGroup(newId);   // откат копии — без дубля
            throw;
        }
        target.LogNow("move", "group", newName);
        return moved;
    }

    // ── Быстрый перенос коллекции в другую БД (bulk-SQL через ATTACH) ──────────
    //
    // Старый MoveCollection материализует ВСЮ коллекцию в память (ExportCollection) и пишет её в
    // целевую БД через EF по одной записи (ImportCollection) — на больших коллекциях это минуты
    // (как был импорт до перевода на bulk). Здесь то же, что дубль, только источник — присоединённый
    // файл: на подключении ЦЕЛЕВОЙ БД делаем `ATTACH src`, копируем шелл/записи/значения/UI bulk-ом
    // (id полей и записей перемэппливаются), затем удаляем коллекцию из источника на ОТДЕЛЬНОМ
    // подключении. Копия и удаление раздельны (в WAL межбазовая транзакция не атомарна), с той же
    // компенсацией, что и MoveCollection: при сбое удаления откатываем копию.

    /// <summary>
    /// Перенести коллекцию в ДРУГУЮ БД быстро (bulk-SQL через ATTACH) на ОТДЕЛЬНЫХ подключениях —
    /// не трогает общий Context (см. правило об общем AppDbContext), поэтому безопасно из Task.Run.
    /// <c>this</c> — репозиторий ЦЕЛЕВОЙ БД (куда переносим); источник задаётся путём к его .db-файлу.
    /// <paramref name="progress"/> — нарастающее число перенесённых записей (для % в окне). Возвращает
    /// id новой коллекции в целевой БД (0 — если копировать нечего). Бросает при сбое (после отката копии).
    /// </summary>
    public int MoveCollectionIsolated(string sourceDbPath, int srcCollectionId, int? targetGroupId,
                                      IProgress<int>? progress = null, int batchSize = 10000)
    {
        // this — ЦЕЛЕВАЯ база: проверяем право создавать в ней. Право удаления в источнике
        // проверяет UI по репозиторию источника (его PermissionService здесь недоступен).
        Demand(PermAction.CreateCollection);

        // Пара с PostgreSQL (или PG→PG): ATTACH существует только у SQLite — переносим
        // generic-путём на двух независимых подключениях (см. ConstructorRepository.CrossMove).
        if (db.IsPostgres || PgConnectionInfo.IsPostgresConnectionString(sourceDbPath))
            return MoveCollectionGeneric(sourceDbPath, srcCollectionId, targetGroupId, progress, batchSize);

        using var targetCtx = new AppDbContext(db.DbPath);
        targetCtx.ApplyBulkPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);

        // Прогресс покрывает ОБЕ фазы: копию (0..recCount) и удаление исходника (recCount..2·recCount).
        // Иначе бар замирал на 100% на всё время удаления (каскад записей/значений — минуты на больших).
        var (newColId, recCount) = target.CopyCollectionFromAttached(sourceDbPath, srcCollectionId, targetGroupId, progress, batchSize);
        if (newColId == 0) return 0;

        // Имя копируется как есть → если в целевой группе уже есть одноимённая коллекция, даём «(копия)».
        target.UniquifyCollectionName(newColId, targetGroupId);

        // Источник чистим на отдельном подключении (каскад: записи→значения, коллекция→поля).
        // Журнал: в источнике «удалил» пишет DeleteCollectionData, в целевой — «перенёс» ниже.
        try
        {
            using var srcCtx = new AppDbContext(sourceDbPath);
            srcCtx.ApplyBulkPerformancePragmas();
            new ConstructorRepository(srcCtx).DeleteCollectionData(srcCollectionId, new BaseProgress(progress, recCount));
        }
        catch
        {
            try { target.DeleteCollectionData(newColId, log: false); } catch { /* копию убрать не удалось — данные целы в обеих БД */ }
            throw;
        }
        target.LogNow("move", "collection", target.CollectionNameOf(newColId) ?? "", count: recCount);
        return newColId;
    }

    /// <summary>
    /// Перенести группу СО ВСЕЙ веткой в ДРУГУЮ БД быстро (bulk-SQL через ATTACH). <c>this</c> —
    /// репозиторий ЦЕЛЕВОЙ БД; источник задаётся путём к его .db-файлу. Структуру групп и все коллекции
    /// ветки копируем в ОДНОЙ транзакции целевой БД, затем удаляем ветку из источника на ОТДЕЛЬНОМ
    /// подключении (с компенсацией как в MoveCollectionIsolated). <paramref name="progress"/> —
    /// нарастающее число перенесённых записей. Возвращает id новой верхней группы (0 — нечего копировать).
    /// </summary>
    public int MoveGroupIsolated(string sourceDbPath, int srcGroupId, int? targetParentId,
                                 IProgress<int>? progress = null, int batchSize = 10000)
    {
        // this — ЦЕЛЕВАЯ база (см. MoveCollectionIsolated: источник проверяет UI).
        Demand(PermAction.CreateCollection);

        // Пара с PostgreSQL (или PG→PG): generic-путь вместо ATTACH (см. CrossMove).
        if (db.IsPostgres || PgConnectionInfo.IsPostgresConnectionString(sourceDbPath))
            return MoveGroupGeneric(sourceDbPath, srcGroupId, targetParentId, progress, batchSize);

        using var targetCtx = new AppDbContext(db.DbPath);
        targetCtx.ApplyBulkPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);

        // Прогресс покрывает ОБЕ фазы: копию ветки (0..total) и удаление исходной ветки (total..2·total).
        var (newRootId, total) = target.CopyGroupFromAttached(sourceDbPath, srcGroupId, targetParentId, progress, batchSize);
        if (newRootId == 0) return 0;

        // Имя корня ветки копируется как есть → если на целевом уровне уже есть одноимённая группа,
        // даём «(копия)». Вложенные группы/коллекции легли в новую (пустую) ветку — там конфликтов нет.
        target.UniquifyGroupName(newRootId, targetParentId);

        try
        {
            using var srcCtx = new AppDbContext(sourceDbPath);
            srcCtx.ApplyBulkPerformancePragmas();
            new ConstructorRepository(srcCtx).DeleteGroupData(srcGroupId, new BaseProgress(progress, total));
        }
        catch
        {
            try { target.DeleteGroupData(newRootId); } catch { /* копию убрать не удалось — данные целы в обеих БД */ }
            throw;
        }
        var movedName = targetCtx.DbGroups.Where(g => g.Id == newRootId)
                                          .Select(g => g.Name).FirstOrDefault() ?? "";
        target.LogNow("move", "group", movedName, count: total);
        return newRootId;
    }

    // Перенос группы из присоединённой БД: одна транзакция целевой БД — сначала структура групп
    // (CloneGroupTreeAttached, имена через INSERT…SELECT, без материализации в C#), затем все коллекции
    // ветки по очереди с общим % (progressBase накапливает перенесённые записи).
    private (int NewRootId, long TotalRecords) CopyGroupFromAttached(string sourceDbPath, int srcGroupId,
                                      int? targetParentId, IProgress<int>? progress, int batchSize)
    {
        return WithAttachedSource(sourceDbPath, conn =>
        {
            using var tx = conn.BeginTransaction();

            var map     = new Dictionary<int, int>();   // старый id группы → новый
            var root    = CloneGroupTreeAttached(conn, tx, srcGroupId, targetParentId, map);

            // Коллекции ветки (GroupId в наборе старых id) — по очереди, с накоплением прогресса.
            var oldGroupIds = string.Join(",", map.Keys);
            var cols = ReadIntPairs(conn, tx,
                $"SELECT Id, GroupId FROM src.db_collections WHERE GroupId IN ({oldGroupIds}) ORDER BY GroupId, Position");

            long basis = 0;
            foreach (var (colId, oldGroup) in cols)
            {
                var (_, rc) = CopyOneCollectionFromAttached(conn, tx, colId, map[oldGroup], progress, basis, batchSize);
                basis += rc;
            }

            tx.Commit();
            return (root, basis);   // basis = суммарно перенесённых записей ветки (для прогресса удаления)
        });
    }

    // Рекурсивно копирует структуру групп из src в main (INSERT…SELECT — имя остаётся в SQL, без
    // инъекции). Заполняет map (старый id → новый). Возвращает id новой верхней группы.
    private int CloneGroupTreeAttached(DbConnection conn, DbTransaction tx, int srcGroupId,
                                       int? targetParentId, Dictionary<int, int> map)
    {
        var parentVal  = targetParentId is int pid ? pid.ToString() : "NULL";
        var parentCond = targetParentId is int p2 ? $"ParentId = {p2}" : "ParentId IS NULL";
        var maxPos     = ScalarLong(conn, tx, $"SELECT COALESCE(MAX(Position), 0) FROM main.db_groups WHERE {parentCond}");

        Exec(conn, tx, $@"
            INSERT INTO main.db_groups (Name, ParentId, Position)
            SELECT Name, {parentVal}, {maxPos + 1000} FROM src.db_groups WHERE Id = {srcGroupId}");
        var newId = (int)ScalarLong(conn, tx, "SELECT last_insert_rowid()");
        map[srcGroupId] = newId;

        foreach (var childId in ReadIntList(conn, tx, $"SELECT Id FROM src.db_groups WHERE ParentId = {srcGroupId} ORDER BY Position"))
            CloneGroupTreeAttached(conn, tx, childId, newId, map);
        return newId;
    }

    // Прогресс со сдвигом: пробрасывает baseValue + value (общий % по нескольким коллекциям ветки,
    // когда CopyCollectionData/CopyOneCollection сообщает 0..recCount для каждой по отдельности).
    private sealed class BaseProgress(IProgress<int>? inner, long baseValue) : IProgress<int>
    {
        public void Report(int value) => inner?.Report((int)(baseValue + value));
    }

    // ATTACH источника (src) на подключение целевой БД, выполнить body, DETACH. Подключение держим
    // открытым на всё время: ATTACH живёт на подключении. Путь источника — через параметр (в пути
    // iCloud пробелы и спецсимволы). Используется и переносом коллекции, и переносом группы.
    private T WithAttachedSource<T>(string sourceDbPath, Func<DbConnection, T> body)
    {
        var conn = db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using (var att = conn.CreateCommand())
            {
                att.CommandText = "ATTACH DATABASE @src AS src";
                var p = att.CreateParameter(); p.ParameterName = "@src"; p.Value = sourceDbPath; att.Parameters.Add(p);
                att.ExecuteNonQuery();
            }
            try { return body(conn); }
            finally
            {
                using var det = conn.CreateCommand();
                det.CommandText = "DETACH src";
                det.ExecuteNonQuery();
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    // Перенос одной коллекции из присоединённой БД: одна транзакция целевой БД.
    private (int NewColId, long RecCount) CopyCollectionFromAttached(string sourceDbPath, int srcCollectionId,
                                           int? targetGroupId, IProgress<int>? progress, int batchSize)
    {
        return WithAttachedSource(sourceDbPath, conn =>
        {
            using var tx = conn.BeginTransaction();
            var r = CopyOneCollectionFromAttached(conn, tx, srcCollectionId, targetGroupId, progress, 0, batchSize);
            tx.Commit();
            return r;
        });
    }

    // Копия ОДНОЙ коллекции из src в main в УЖЕ открытой транзакции (для переноса группы — много
    // коллекций в одной транзакции). Шелл (коллекция + поля) bulk-ом из src, карта полей old→new по
    // Position (temp _move_fmap), записи одним INSERT…SELECT (новые id — непрерывный блок в порядке
    // Position,Id), значения крупными пакетами (один INSERT по всем полям через _move_fmap; пакеты —
    // ради гранулярности %), затем UI-настройки. progressBase — сколько записей уже перенесено ранее
    // (для общего % по группе). Возвращает (id новой коллекции, число её записей). Числа в SQL — int/
    // long, которые мы формируем сами (не пользовательский ввод), поэтому интерполяция безопасна.
    private (int NewColId, long RecCount) CopyOneCollectionFromAttached(DbConnection conn, DbTransaction tx,
        int srcCollectionId, int? targetGroupId, IProgress<int>? progress, long progressBase, int batchSize)
    {
        var recCount = ScalarLong(conn, tx, $"SELECT COUNT(*) FROM src.db_records WHERE CollectionId = {srcCollectionId}");

        var maxPos   = ScalarLong(conn, tx, "SELECT COALESCE(MAX(Position), 0) FROM main.db_collections");
        var groupSql = targetGroupId is int gid ? gid.ToString() : "NULL";

        // 1) Строка коллекции (имя/иконку/цвет переносим как есть; группа — целевая).
        Exec(conn, tx, $@"
            INSERT INTO main.db_collections (Name, GroupId, Position, Icon, Color)
            SELECT Name, {groupSql}, {maxPos + 1000}, Icon, Color FROM src.db_collections WHERE Id = {srcCollectionId}");
        var newColId = (int)ScalarLong(conn, tx, "SELECT last_insert_rowid()");

        // 2) Поля (Position копируем дословно — по нему строим карту old→new).
        Exec(conn, tx, $@"
            INSERT INTO main.db_fields (CollectionId, Name, FieldType, ConfigJson, Position, IsCalStart, IsCalEnd, IsSystem, IsRequired, IsTaskExec, IsTaskAssigner, IsTaskStatus)
            SELECT {newColId}, Name, FieldType, ConfigJson, Position, IsCalStart, IsCalEnd, IsSystem, IsRequired, IsTaskExec, IsTaskAssigner, IsTaskStatus
            FROM src.db_fields WHERE CollectionId = {srcCollectionId} ORDER BY Id");

        Exec(conn, tx, "DROP TABLE IF EXISTS temp._move_fmap");
        Exec(conn, tx, "CREATE TEMP TABLE _move_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
        Exec(conn, tx, $@"
            INSERT INTO _move_fmap(old_fid, new_fid)
            SELECT s.Id, n.Id FROM src.db_fields s
            JOIN main.db_fields n ON n.CollectionId = {newColId} AND n.Position = s.Position
            WHERE s.CollectionId = {srcCollectionId}");

        if (recCount > 0)
        {
            // 3) Записи одним INSERT … SELECT в порядке (Position, Id) — новые id непрерывным блоком.
            Exec(conn, tx, $@"
                INSERT INTO main.db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Description)
                SELECT {newColId}, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Description
                FROM src.db_records WHERE CollectionId = {srcCollectionId} ORDER BY Position, Id");
            var firstNew = ScalarLong(conn, tx, "SELECT last_insert_rowid()") - recCount + 1;

            // 4) Значения и проекция календаря крупными пакетами по (Position, Id): на пакет — один
            //    INSERT значений по всем полям и один INSERT строк calendar_index из src (проекция
            //    источника всегда полная — копируем 1:1 с перемэппингом id вместо пересборки с
            //    парсингом дат, которая на больших коллекциях добавляла минуты после копии).
            void CopySlice(long bbase, long fromPos, long fromId, (long Pos, long Id)? upper)
            {
                var slice = $"(Position > {fromPos} OR (Position = {fromPos} AND Id > {fromId}))" +
                            (upper is { } u ? $" AND (Position < {u.Pos} OR (Position = {u.Pos} AND Id <= {u.Id}))" : "");
                var rank = $@"(SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                              FROM src.db_records WHERE CollectionId = {srcCollectionId} AND {slice}) r";
                Exec(conn, tx, $@"
                    INSERT INTO main.db_field_values (RecordId, FieldId, Value)
                    SELECT {bbase} + r.rn - 1, fm.new_fid, fv.Value
                    FROM src.db_field_values fv
                    JOIN {rank} ON r.old_id = fv.RecordId
                    JOIN _move_fmap fm ON fm.old_fid = fv.FieldId");
                Exec(conn, tx, $@"
                    INSERT INTO main.calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay)
                    SELECT {bbase} + r.rn - 1, fm.new_fid, {newColId}, ci.StartIso, ci.EndIso, ci.AllDay
                    FROM src.calendar_index ci
                    JOIN {rank} ON r.old_id = ci.RecordId
                    JOIN _move_fmap fm ON fm.old_fid = ci.FieldId");
            }

            long prevPos = long.MinValue, prevId = 0, processed = 0;
            while (processed < recCount)
            {
                var bbase = firstNew + processed;
                var end   = AttachedBatchEnd(conn, tx, srcCollectionId, prevPos, prevId, batchSize);
                CopySlice(bbase, prevPos, prevId, end);
                if (end is { } e) { processed += batchSize; prevPos = e.Pos; prevId = e.Id; }
                else              { processed = recCount; }   // последний (неполный) пакет
                progress?.Report((int)(progressBase + Math.Min(processed, recCount)));
            }

            CopyMovedUi(conn, tx, srcCollectionId, newColId, firstNew, recCount);
        }
        else
        {
            CopyMovedUi(conn, tx, srcCollectionId, newColId, 0, 0);
            progress?.Report((int)progressBase);
        }

        Exec(conn, tx, "DROP TABLE IF EXISTS temp._move_fmap");
        return (newColId, recCount);
    }

    // UI-настройки переносимой коллекции: ключи-id полей/записей перемэппливаются (поля — через
    // _move_fmap; записи — по рангу в порядке Position,Id → firstNew + ранг − 1). Ключи колонок
    // (column_meta) числовые → новый id поля, системные (__title__/__desc__) — как есть.
    private static void CopyMovedUi(DbConnection conn, DbTransaction tx, int srcCol, int newCol, long firstNew, long recCount)
    {
        Exec(conn, tx, $@"
            INSERT INTO main.ui_col_widths (CollectionId, FieldId, Width)
            SELECT {newCol}, fm.new_fid, w.Width FROM src.ui_col_widths w
            JOIN _move_fmap fm ON fm.old_fid = w.FieldId WHERE w.CollectionId = {srcCol}");

        Exec(conn, tx, $@"
            INSERT INTO main.ui_collection_settings (CollectionId, ColResize, RowResize, ViewMode)
            SELECT {newCol}, ColResize, RowResize, ViewMode FROM src.ui_collection_settings WHERE CollectionId = {srcCol}");

        Exec(conn, tx, $@"
            INSERT INTO main.column_meta (CollectionId, Key, Position, Visible, Level, DisplayName)
            SELECT {newCol},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cm.Key END,
                   cm.Position, cm.Visible, cm.Level, cm.DisplayName
            FROM src.column_meta cm
            LEFT JOIN _move_fmap fm ON cm.Key GLOB '[0-9]*' AND CAST(cm.Key AS INTEGER) = fm.old_fid
            WHERE cm.CollectionId = {srcCol}");

        if (recCount > 0)
        {
            var rank = $@"(SELECT Id AS old_id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                          FROM src.db_records WHERE CollectionId = {srcCol})";
            Exec(conn, tx, $@"
                INSERT INTO main.ui_row_heights (CollectionId, RecordId, Height)
                SELECT {newCol}, {firstNew} + r.rn - 1, h.Height FROM src.ui_row_heights h
                JOIN {rank} r ON r.old_id = h.RecordId WHERE h.CollectionId = {srcCol}");
            Exec(conn, tx, $@"
                INSERT INTO main.record_display (CollectionId, RecordId, Visible, Level)
                SELECT {newCol}, {firstNew} + r.rn - 1, d.Visible, d.Level FROM src.record_display d
                JOIN {rank} r ON r.old_id = d.RecordId WHERE d.CollectionId = {srcCol}");
        }
    }

    // (Position, Id) записи источника на позиции offset−1 после курсора — граница пакета значений.
    // null — записей меньше (последний неполный пакет). Источник — присоединённая схема src.
    private static (long Pos, long Id)? AttachedBatchEnd(DbConnection conn, DbTransaction tx,
        int srcCollectionId, long prevPos, long prevId, int offset)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = $@"
            SELECT Position, Id FROM src.db_records
            WHERE CollectionId = {srcCollectionId}
              AND (Position > {prevPos} OR (Position = {prevPos} AND Id > {prevId}))
            ORDER BY Position, Id LIMIT 1 OFFSET {offset - 1}";
        using var rdr = c.ExecuteReader();
        return rdr.Read() ? (rdr.GetInt64(0), rdr.GetInt64(1)) : null;
    }

    private static void Exec(DbConnection conn, DbTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx; c.CommandText = sql; c.ExecuteNonQuery();
    }

    private static long ScalarLong(DbConnection conn, DbTransaction? tx, string sql)
    {
        using var c = conn.CreateCommand();
        if (tx is not null) c.Transaction = tx;
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar());
    }

    private static List<int> ReadIntList(DbConnection conn, DbTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        using var rdr = c.ExecuteReader();
        var list = new List<int>();
        while (rdr.Read()) list.Add(rdr.GetInt32(0));
        return list;
    }

    // Пары (col0, col1) из запроса — для (Id, GroupId) коллекций поддерева при переносе группы.
    private static List<(int A, int B)> ReadIntPairs(DbConnection conn, DbTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx; c.CommandText = sql;
        using var rdr = c.ExecuteReader();
        var list = new List<(int, int)>();
        while (rdr.Read()) list.Add((rdr.GetInt32(0), rdr.GetInt32(1)));
        return list;
    }

    // ── Поля ──────────────────────────────────────────────────────────────────

    public List<DbField> GetFields(int collectionId) =>
        db.DbFields
          .Where(f => f.CollectionId == collectionId)
          .OrderBy(f => f.Position)
          .Select(f => new DbField
          {
              Id = f.Id, CollectionId = f.CollectionId, Name = f.Name,
              FieldType = f.FieldType, ConfigJson = f.ConfigJson, Position = f.Position,
              IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
              IsSystem = f.IsSystem, IsRequired = f.IsRequired,
              IsTaskExec = f.IsTaskExec, IsTaskAssigner = f.IsTaskAssigner, IsTaskStatus = f.IsTaskStatus,
          })
          .ToList();

    public DbField AddField(DbField field)
    {
        Demand(PermAction.CreateField, field.CollectionId);
        if (db.DbFields.Any(f => f.CollectionId == field.CollectionId && f.Name == field.Name))
            throw new InvalidOperationException("Поле с таким именем уже существует.");

        // Первое поле-дата в коллекции по умолчанию видно в календаре (IsCalStart); его
        // значения попадают в индексную проекцию calendar_index (см. SyncCalendarIndexValue).
        if (field.FieldType == "date" &&
            !db.DbFields.Any(f => f.CollectionId == field.CollectionId && f.FieldType == "date" && f.IsCalStart))
            field.IsCalStart = true;

        var entity = new DbFieldEntity
        {
            CollectionId = field.CollectionId, Name = field.Name,
            FieldType    = field.FieldType,    ConfigJson = field.ConfigJson,
            Position     = field.Position,     IsCalStart = field.IsCalStart,
            IsCalEnd     = field.IsCalEnd,     IsSystem = field.IsSystem,
            IsRequired   = field.IsRequired,
            IsTaskExec   = field.IsTaskExec,   IsTaskAssigner = field.IsTaskAssigner,
            IsTaskStatus = field.IsTaskStatus,
        };
        db.DbFields.Add(entity);
        Log("create", "field", field.Name, CollectionNameOf(field.CollectionId));
        db.SaveChanges();
        field.Id = entity.Id;
        return field;
    }

    public void UpdateField(DbField field)
    {
        Demand(PermAction.EditField, field.CollectionId);
        var entity = db.DbFields.Find(field.Id);
        if (entity is null) return;
        if (entity.Name != field.Name &&
            db.DbFields.Any(f => f.CollectionId == entity.CollectionId && f.Name == field.Name && f.Id != field.Id))
            throw new InvalidOperationException("Поле с таким именем уже существует.");

        // Пересборка проекции нужна только при смене ТИПА (содержимое проекции реально меняется:
        // текст→дата заливает строки, дата→текст очищает). Видимость (IsCalStart) — чистый
        // фильтр чтения, пересборки не требует (см. SetDateFieldVisible) — раньше её смена
        // здесь тоже пересобирала проекцию (минуты фриза на больших коллекциях).
        var oldType            = entity.FieldType;
        var calRelevantChanged = entity.FieldType != field.FieldType;

        // Число с выбранным десятичным разделителем: если поле стало числом или сменился разделитель —
        // приведём уже сохранённые значения к нему (канон хранения). Считаем ДО перезаписи ConfigJson.
        var newMark      = NumberFormatValue.FromConfig(field.ConfigJson).DecimalSeparator;
        var oldMark      = NumberFormatValue.FromConfig(entity.ConfigJson).DecimalSeparator;
        var sepMigration = field.FieldType == "number" && (oldType != "number" || oldMark != newMark);

        // Перестановки (Position) журнал не засоряют — только смысловые изменения.
        if (entity.Name != field.Name || entity.FieldType != field.FieldType ||
            entity.ConfigJson != field.ConfigJson || entity.IsRequired != field.IsRequired)
            Log("update", "field", field.Name, CollectionNameOf(field.CollectionId),
                detail: entity.Name != field.Name ? ChangeDetail.Rename(entity.Name, field.Name) : null);

        entity.Name       = field.Name;
        entity.FieldType  = field.FieldType;
        entity.ConfigJson = field.ConfigJson;
        entity.Position   = field.Position;
        entity.IsCalStart = field.IsCalStart;
        entity.IsCalEnd   = field.IsCalEnd;
        entity.IsSystem   = field.IsSystem;
        entity.IsRequired = field.IsRequired;
        entity.IsTaskExec     = field.IsTaskExec;
        entity.IsTaskAssigner = field.IsTaskAssigner;
        entity.IsTaskStatus   = field.IsTaskStatus;
        db.SaveChanges();

        // Тип пересёк границу «дата» → приводим уже сохранённые значения к новому виду
        // (текст↔дата без потери данных). Делаем ДО пересборки calendar_index, чтобы
        // индекс строился уже по каноническим значениям.
        if (oldType != field.FieldType && field.Id is int convFid)
            ConvertFieldValuesForTypeChange(convFid, oldType, field.FieldType);

        // Число: привести десятичный разделитель уже сохранённых значений к выбранному
        // (после конверсии типа — чтобы нормализовать итоговые числовые строки).
        if (sepMigration && field.Id is int sepFid)
            NormalizeNumberSeparatorValues(sepFid, newMark);

        // Поле стало/перестало быть видимой датой → пересобрать его строки проекции.
        if (calRelevantChanged && field.Id is int fid)
            RebuildCalendarIndexForField(fid);
    }

    // Приводит десятичный разделитель всех значений числового поля к выбранному (канон хранения):
    // одиночная замена символа, цифры сохраняются; нечисловые значения не трогаются. Пишем сырым
    // ADO.NET пакетом в одной транзакции (значений может быть сотни тысяч) — как смена типа.
    private void NormalizeNumberSeparatorValues(int fieldId, DecimalMark mark)
    {
        var rows = db.DbFieldValues.AsNoTracking()
            .Where(v => v.FieldId == fieldId && v.Value != "")
            .Select(v => new { v.RecordId, v.Value })
            .ToList();

        var updates = new List<(int RecordId, string Value)>();
        foreach (var r in rows)
        {
            var converted = NumberFormatValue.NormalizeStorage(r.Value, mark);
            if (converted != r.Value) updates.Add((r.RecordId, converted));
        }
        if (updates.Count == 0) return;

        var conn    = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        using (var tx  = conn.BeginTransaction())
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $"UPDATE db_field_values SET Value = @val WHERE RecordId = @rid AND FieldId = {fieldId}";
            var pVal = AddParam(upd, "@val");
            var pRid = AddParam(upd, "@rid");
            upd.Prepare();

            foreach (var (recordId, value) in updates)
            {
                pVal.Value = value;
                pRid.Value = recordId;
                upd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (!wasOpen) conn.Close();
    }

    /// <summary>Итог применения авто-привязки файлов.</summary>
    public enum FileAutoResolveResult { Ok, FolderInaccessible, NoRule, Cancelled }

    /// <summary>Есть ли у поля хоть одно непустое значение (для подтверждения затирания при
    /// включении авто-привязки).</summary>
    public bool FieldHasValues(int fieldId) =>
        db.DbFieldValues.AsNoTracking().Any(v => v.FieldId == fieldId && v.Value != "");

    /// <summary>
    /// Материализует авто-привязку файлов поля «файл»: сканирует папку (верхний уровень, регистр
    /// учитывается), сопоставляет по «значение_текст-столбца.формат», и ПЕРЕЗАПИСЫВАЕТ значения поля
    /// найденными путями (существующие значения очищаются — теперь столбец ведёт правило). Значения
    /// материализуются в db_field_values, поэтому фильтр/сортировка работают штатно.
    /// Папка недоступна → значения не трогаем, возвращаем статус (UI покажет плашку).
    /// </summary>
    public FileAutoResolveResult ResolveAutoFileField(int fieldId)
    {
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null || field.FieldType != "file") return FileAutoResolveResult.NoRule;
        Demand(PermAction.EditRecord, field.CollectionId);
        return ResolveAutoFileFieldCore(fieldId, null, default);
    }

    /// <summary>Фоновый вариант применения авто-привязки (для окна прогресса): проверка прав — на
    /// общем репозитории, работа — на изолированном подключении (общий Context однопоточный, см.
    /// правило об AppDbContext), поэтому безопасно из Task.Run. <paramref name="progress"/> — доля
    /// 0..1; <paramref name="ct"/> — отмена (при отмене значения не меняются, статус Cancelled).</summary>
    public Task<FileAutoResolveResult> ResolveAutoFileFieldAsync(
        int fieldId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null || field.FieldType != "file") return Task.FromResult(FileAutoResolveResult.NoRule);
        Demand(PermAction.EditRecord, field.CollectionId);
        var path = db.DbPath;
        // ct в Task.Run НЕ передаём: задача всегда стартует и обрабатывает отмену кооперативно
        // (возвращает Cancelled), чтобы await у вызывающего не бросал TaskCanceledException.
        return Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            return new ConstructorRepository(ctx).ResolveAutoFileFieldCore(fieldId, progress, ct);
        });
    }

    private FileAutoResolveResult ResolveAutoFileFieldCore(int fieldId, IProgress<double>? progress, CancellationToken ct)
    {
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null || field.FieldType != "file") return FileAutoResolveResult.NoRule;

        var rule = FileAutoRule.FromConfig(field.ConfigJson);
        if (rule is null) return FileAutoResolveResult.NoRule;

        if (!TryScanFolder(rule.Folder, out var files)) return FileAutoResolveResult.FolderInaccessible;

        // Источник значений: числовое текст-поле → db_field_values по (FieldId, Value); системная
        // колонка «Название»/«Описание» → db_records по (Id, Title/Description) этой коллекции
        // (в db_field_values их нет). SQL — некавыченный PascalCase: PG складывает в нижний регистр
        // своих колонок, SQLite регистронезависим.
        var (countSql, selectSql) = SourceStreamSql(rule, field.CollectionId);

        // Значения столбца-источника СТРИМИМ сырым reader'ом (без EF-материализации всех строк —
        // на миллионах записей это критично по памяти) и буферим ТОЛЬКО совпадения. Затем
        // перезаписываем файл-поле.
        var matches = new List<(int RecordId, string Value)>();
        var conn    = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            // Всего значений источника — для процентов (чтение = 0..90% прогресса).
            long total;
            using (var cnt = conn.CreateCommand())
            {
                cnt.CommandText = countSql;
                total = Convert.ToInt64(cnt.ExecuteScalar() ?? 0L);
            }

            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = selectSql;
                using var reader = sel.ExecuteReader();
                long read = 0;
                while (reader.Read())
                {
                    if (ct.IsCancellationRequested) return FileAutoResolveResult.Cancelled;   // ничего не записано
                    var target = rule.TargetFileName(reader.GetString(1));
                    if (target.Length > 0 && files.TryGetValue(target, out var full))
                        matches.Add((reader.GetInt32(0), full));
                    if (++read % 5000 == 0 && total > 0) progress?.Report(0.9 * read / total);
                }
            }
            // reader закрыт → нет конфликта параллельных команд на соединении (важно для Npgsql).
            // Запись = 90..100%. Отмена внутри → откат транзакции (значения не меняются).
            if (!RewriteFieldValues(fieldId, matches, progress, ct)) return FileAutoResolveResult.Cancelled;
            progress?.Report(1.0);
            return FileAutoResolveResult.Ok;
        }
        finally { if (!wasOpen) conn.Close(); }
    }

    // SQL потоковой выборки значений источника (COUNT + SELECT (RecordId, Value)). Текст-поле —
    // из db_field_values по FieldId; системные «Название»/«Описание» — из db_records этой коллекции
    // (Title/Description). Некавыченные PascalCase-идентификаторы работают в SQLite и PG (см. выше).
    private static (string Count, string Select) SourceStreamSql(FileAutoRule rule, int collectionId)
    {
        if (rule.SourceFieldId is var fid && fid > 0)
            return ($"SELECT COUNT(*) FROM db_field_values WHERE FieldId = {fid} AND Value <> ''",
                    $"SELECT RecordId, Value FROM db_field_values WHERE FieldId = {fid} AND Value <> ''");

        var col = rule.SourceIsDesc ? "Description" : "Title";   // NULL <> '' → строка отсеивается
        return ($"SELECT COUNT(*) FROM db_records WHERE CollectionId = {collectionId} AND {col} <> ''",
                $"SELECT Id, {col} FROM db_records WHERE CollectionId = {collectionId} AND {col} <> ''");
    }

    // Значение столбца-источника для ОДНОЙ записи (точечный пересчёт). Текст-поле — из
    // db_field_values; системные «Название»/«Описание» — из самой записи.
    private string ReadSourceValue(FileAutoRule rule, int recordId)
    {
        if (rule.SourceFieldId is var fid && fid > 0)
            return db.DbFieldValues.AsNoTracking()
                .Where(v => v.FieldId == fid && v.RecordId == recordId)
                .Select(v => v.Value).FirstOrDefault() ?? "";

        var rec = db.DbRecords.AsNoTracking().FirstOrDefault(r => r.Id == recordId);
        if (rec is null) return "";
        return (rule.SourceIsDesc ? rec.Description : rec.Title) ?? "";
    }

    // Скан папки в словарь имя→путь (только верхний уровень, регистр учитывается — Ordinal).
    // false — папки нет или нет доступа.
    private static bool TryScanFolder(string folder, out Dictionary<string, string> files)
    {
        files = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!System.IO.Directory.Exists(folder)) return false;
            foreach (var path in System.IO.Directory.EnumerateFiles(folder))
                files[System.IO.Path.GetFileName(path)] = path;   // имена в папке уникальны
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            files = new Dictionary<string, string>(StringComparer.Ordinal);
            return false;
        }
    }

    // Перезаписать ВСЕ значения поля: удалить прежние + вставить переданные (одна транзакция,
    // сырой ADO.NET — значений могут быть сотни тысяч). PK db_field_values — (RecordId, FieldId).
    // false — операцию отменили (ct): транзакция откатана, значения НЕ изменены. progress — 0.9..1.0.
    private bool RewriteFieldValues(int fieldId, IReadOnlyList<(int RecordId, string Value)> rows,
                                    IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var conn    = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM db_field_values WHERE FieldId = {fieldId}";
                del.ExecuteNonQuery();
            }
            if (rows.Count > 0)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = $"INSERT INTO db_field_values (RecordId, FieldId, Value) VALUES (@rid, {fieldId}, @val)";
                var pRid = AddParam(ins, "@rid");
                var pVal = AddParam(ins, "@val");
                ins.Prepare();
                for (var i = 0; i < rows.Count; i++)
                {
                    if (ct.IsCancellationRequested) { tx.Rollback(); return false; }
                    pRid.Value = rows[i].RecordId;
                    pVal.Value = rows[i].Value;
                    ins.ExecuteNonQuery();
                    if ((i & 8191) == 0) progress?.Report(0.9 + 0.1 * i / rows.Count);
                }
            }
            tx.Commit();
            return true;
        }
        finally { if (!wasOpen) conn.Close(); }
    }

    // Пересчитать авто-file поля коллекции для ОДНОЙ записи (добавление записи / правка
    // столбца-источника). Дёшево: точечная проверка файла на диске, без bulk-скана.
    private void ResolveAutoFileForRecord(int collectionId, int recordId)
    {
        var autoFields = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && f.FieldType == "file")
            .Select(f => new { f.Id, f.ConfigJson }).ToList();
        foreach (var af in autoFields)
        {
            var rule = FileAutoRule.FromConfig(af.ConfigJson);
            if (rule is null) continue;

            var matched = MatchSingle(rule, ReadSourceValue(rule, recordId));
            SetAutoFileValue(af.Id, recordId, matched);
        }
    }

    // Точечное совпадение файла для записи (регистр учитывается). "" — не найден/пусто.
    private static string MatchSingle(FileAutoRule rule, string sourceValue)
    {
        var target = rule.TargetFileName(sourceValue);
        if (target.Length == 0) return "";
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(rule.Folder, target))
                if (string.Equals(System.IO.Path.GetFileName(path), target, StringComparison.Ordinal))
                    return path;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        return "";
    }

    // Записать значение авто-file ячейки напрямую (мимо SetFieldValue: без прав-повторов и без
    // журнала — это авто-подстановка, а не ручная правка). Пустое → строку удаляем.
    private void SetAutoFileValue(int fieldId, int recordId, string value)
    {
        var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == fieldId);
        if (value.Length == 0)
        {
            if (fv is not null) { db.DbFieldValues.Remove(fv); db.SaveChanges(); }
            return;
        }
        if (fv is null) db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = recordId, FieldId = fieldId, Value = value });
        else            fv.Value = value;
        db.SaveChanges();
    }

    // Приводит одно значение свойства от типа fromType к типу toType. Все типы, кроме «дата»,
    // хранятся как обычные строки → конверсия нужна только при пересечении границы «дата»
    // (текст/число/… ↔ дата). Неразбираемое как дата значение оставляем как есть — это не
    // потеря: произвольный текст просто не является датой. Используется и при смене типа поля
    // (ConvertFieldValuesForTypeChange), и при переносе записи в коллекцию с другой схемой
    // (CopyFieldValuesByName).
    internal static string ConvertValueForType(string value, string fromType, string toType)
    {
        if (value.Length == 0 || fromType == toType) return value;
        var toDate   = toType   == "date" && fromType != "date";
        var fromDate = fromType == "date" && toType   != "date";
        if (!toDate && !fromDate) return value;                 // оба не-дата → plain string, как есть
        if (!DateValue.TryParse(value, out var dv)) return value;
        return toDate ? dv.ToStorage() : dv.ToDisplay();
    }

    // При смене типа поля на «дата»/с «даты» приводим уже сохранённые значения к нужному
    // виду — чтобы перевод текст↔дата шёл без потери данных:
    //   • текст → дата: разбираем значение и пишем ISO-канон (как пишет редактор даты),
    //     чтобы дата сразу читалась таблицей, календарём и редактором;
    //   • дата → текст: разбираем ISO-канон и пишем человекочитаемое дд.MM.гггг,
    //     чтобы текст сохранил дату в привычном виде, а не как 2026-02-01.
    // Неразбираемое значение оставляем как есть — это не потеря: произвольный текст просто
    // не является датой. Пишем сырым ADO.NET пакетом в одной транзакции (значений может быть
    // сотни тысяч); EF-трекер не задействован — db_field_values этого поля здесь не отслеживаются.
    private void ConvertFieldValuesForTypeChange(int fieldId, string oldType, string newType)
    {
        var toDate   = newType == "date" && oldType != "date";
        var fromDate = oldType == "date" && newType != "date";
        if (!toDate && !fromDate) return;

        var rows = db.DbFieldValues.AsNoTracking()
            .Where(v => v.FieldId == fieldId && v.Value != "")
            .Select(v => new { v.RecordId, v.Value })
            .ToList();

        var updates = new List<(int RecordId, string Value)>();
        foreach (var r in rows)
        {
            var converted = ConvertValueForType(r.Value, oldType, newType);
            if (converted != r.Value) updates.Add((r.RecordId, converted));
        }
        if (updates.Count == 0) return;

        var conn    = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        using (var tx  = conn.BeginTransaction())
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = $"UPDATE db_field_values SET Value = @val WHERE RecordId = @rid AND FieldId = {fieldId}";
            var pVal = AddParam(upd, "@val");
            var pRid = AddParam(upd, "@rid");
            upd.Prepare();

            foreach (var (recordId, value) in updates)
            {
                pVal.Value = value;
                pRid.Value = recordId;
                upd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        if (!wasOpen) conn.Close();
    }

    public DbField DuplicateField(int fieldId)
    {
        Demand(PermAction.CreateField, CollectionOfField(fieldId));
        var copy = CreateFieldCopyEntity(fieldId);

        // Копируем значения ячеек исходного свойства во всех записях ОДНИМ bulk-SQL —
        // на больших коллекциях это сотни тысяч строк, поэлементная вставка через EF
        // подвешивала бы приложение. Конфликтов по (RecordId, FieldId) нет: поле новое.
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO db_field_values (RecordId, FieldId, Value)
            SELECT RecordId, {copy.Id}, Value FROM db_field_values WHERE FieldId = {fieldId}");

        LogNow("duplicate", "field", copy.Name, CollectionNameOf(copy.CollectionId));
        return ToDbField(copy);
    }

    // Метаданные копии поля (имя «(копия)», сдвиг позиций соседей, новая строка db_fields) БЕЗ
    // значений. Значения копирует вызывающий: одним SQL (DuplicateField) или пакетами с прогрессом
    // (DuplicateFieldData).
    private DbFieldEntity CreateFieldCopyEntity(int fieldId)
    {
        var src = db.DbFields.Find(fieldId);
        if (src is null) throw new InvalidOperationException($"Field {fieldId} not found");

        var name = NextCopyName(src.Name,
            candidate => db.DbFields.Any(f => f.CollectionId == src.CollectionId && f.Name == candidate));

        // Сдвигаем все поля правее исходного, освобождая место под копию.
        var following = db.DbFields
            .Where(f => f.CollectionId == src.CollectionId && f.Position > src.Position)
            .ToList();
        foreach (var f in following)
            f.Position += 1000;

        var copy = new DbFieldEntity
        {
            CollectionId = src.CollectionId,     Name = name,
            FieldType    = src.FieldType,        ConfigJson = src.ConfigJson,
            Position     = src.Position + 1000,  IsCalStart = false,
            IsCalEnd     = false,                IsSystem = false,
            IsRequired   = false,
            IsTaskExec   = false, IsTaskAssigner = false, IsTaskStatus = false,
        };
        db.DbFields.Add(copy);
        db.SaveChanges();
        return copy;
    }

    private static DbField ToDbField(DbFieldEntity e) => new()
    {
        Id = e.Id, CollectionId = e.CollectionId, Name = e.Name,
        FieldType = e.FieldType, ConfigJson = e.ConfigJson, Position = e.Position,
    };

    // ── Тяжёлые операции со свойствами: счётчик + пакетные версии с прогрессом ──
    // Удаление и дубль поля затрагивают его значения по ВСЕМ записям коллекции (на больших
    // коллекциях — сотни тысяч строк). Маленькие поля контроллер делает синхронно мгновенно;
    // большие — через *Isolated на отдельном подключении в фоне, с окном прогресса (как дубль/
    // удаление коллекций в сайдбаре). Пакеты — только ради гранулярности прогресса: по SQL это
    // та же работа, что один INSERT…SELECT / каскадный DELETE.

    /// <summary>Число значений поля по всем записям — для выбора «мгновенно» vs «окно прогресса».</summary>
    public int CountFieldValues(int fieldId) =>
        db.DbFieldValues.Count(v => v.FieldId == fieldId);

    /// <summary>Число значений по каждому из полей (для прогресса bulk-операций со свойствами) на
    /// ОТДЕЛЬНОМ подключении — чтобы счёт по сотням тысяч строк шёл в фоне, не подвешивая UI до
    /// появления индикатора. Одним GROUP BY-запросом (индекс (FieldId,Value)). Порядок результата
    /// совпадает с fieldIds; для полей без значений — 0.</summary>
    public List<int> CountFieldValuesIsolated(IReadOnlyList<int> fieldIds)
    {
        if (fieldIds.Count == 0) return [];
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var byField = ctx.DbFieldValues
            .Where(v => fieldIds.Contains(v.FieldId))
            .GroupBy(v => v.FieldId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        return fieldIds.Select(id => byField.TryGetValue(id, out var c) ? c : 0).ToList();
    }

    /// <summary>Дубль поля на ОТДЕЛЬНОМ подключении (для фона) с прогрессом по скопированным значениям.</summary>
    public DbField DuplicateFieldIsolated(int fieldId, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        // Владельца-коллекцию ищем через СВЕЖИЙ контекст: обёртку зовут из фонового
        // потока, а общий контекст — только для UI-потока (сама Demand читает кеш).
        Demand(PermAction.CreateField, repo.CollectionOfField(fieldId));
        return repo.DuplicateFieldData(fieldId, progress);
    }

    /// <summary>Удаление ОДНОГО поля на ОТДЕЛЬНОМ подключении (для фона) с прогрессом по его значениям.
    /// Несколько полей контроллер удаляет в цикле — чтобы показывать «N/M столбцов».</summary>
    public void DeleteFieldIsolated(int fieldId, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        // Лукап через свежий контекст — обёртка работает в фоне (см. DuplicateFieldIsolated).
        Demand(PermAction.DeleteField, repo.CollectionOfField(fieldId));
        repo.DeleteFieldData(fieldId, progress);
    }


    /// <summary>Дубль поля ПАКЕТАМИ: метаданные мгновенно, значения — порциями по списку RecordId.</summary>
    public DbField DuplicateFieldData(int fieldId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        var copy   = CreateFieldCopyEntity(fieldId);
        var copyId = copy.Id;

        var ids = FieldValueRecordIdsSorted(fieldId);   // PK-порядок → последовательные обновления
        using var tx = db.Database.BeginTransaction();
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var inList = JoinIdRange(ids, i, batchSize);
            // Копируем строго по PK (FieldId + RecordId IN …) — без скана значений чужих полей.
            // ExecuteSqlRaw: в готовой строке нет фигурных скобок (числа и список «1,2,3»).
            db.Database.ExecuteSqlRaw(
                $"INSERT INTO db_field_values (RecordId, FieldId, Value) " +
                $"SELECT RecordId, {copyId}, Value FROM db_field_values " +
                $"WHERE FieldId = {fieldId} AND RecordId IN ({inList})");
            progress?.Report(Math.Min(ids.Count, i + batchSize));
        }
        tx.Commit();
        LogNow("duplicate", "field", copy.Name, CollectionNameOf(copy.CollectionId));
        return ToDbField(copy);
    }

    /// <summary>Удаление поля ПАКЕТАМИ: значения порциями по списку RecordId (прогресс), затем строка
    /// поля (каскад на calendar_index по FK) и чистка column_meta — как в DeleteField.</summary>
    public void DeleteFieldData(int fieldId, IProgress<int>? progress = null, int batchSize = 10000)
    {
        var field = db.DbFields.Find(fieldId);
        if (field is null) return;
        var colId = field.CollectionId;

        var ids = FieldValueRecordIdsSorted(fieldId);
        using var tx = db.Database.BeginTransaction();
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var inList = JoinIdRange(ids, i, batchSize);
            db.Database.ExecuteSqlRaw(
                $"DELETE FROM db_field_values WHERE FieldId = {fieldId} AND RecordId IN ({inList})");
            progress?.Report(Math.Min(ids.Count, i + batchSize));
        }

        // Строка поля (FK ON DELETE CASCADE уберёт calendar_index) + чистка column_meta
        // (FK нет — ключ строковый), как в синхронном DeleteField.
        var key = fieldId.ToString();
        db.Database.ExecuteSqlInterpolated($"DELETE FROM db_fields WHERE Id = {fieldId}");
        db.Database.ExecuteSqlInterpolated(
            $"DELETE FROM column_meta WHERE CollectionId = {colId} AND Key = {key}");
        tx.Commit();
        LogNow("delete", "field", field.Name, CollectionNameOf(colId));
    }

    // RecordId всех значений поля одним индексным запросом ((FieldId,Value) покрывает: RecordId —
    // часть PK, лежит в хвосте индекса), отсортированные. RecordId-порядок = порядок PK
    // (RecordId, FieldId), поэтому удаление/вставка по нему дают ПОСЛЕДОВАТЕЛЬНЫЕ обновления
    // таблицы и индексов (на больших полях это в разы быстрее случайного порядка, который
    // давал бы один общий DELETE по (FieldId,Value)).
    private List<long> FieldValueRecordIdsSorted(int fieldId)
    {
        var ids = new List<long>();
        var conn = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        try
        {
            using var c = conn.CreateCommand();
            if (db.Database.CurrentTransaction is { } t) c.Transaction = t.GetDbTransaction();
            c.CommandText = $"SELECT RecordId FROM db_field_values WHERE FieldId = {fieldId}";
            using var rdr = c.ExecuteReader();
            while (rdr.Read()) ids.Add(rdr.GetInt64(0));
        }
        finally { if (!wasOpen) conn.Close(); }
        ids.Sort();
        return ids;
    }

    // «a,b,c» из среза ids[start .. start+count) — для списка Id IN (…). Только числа (без инъекций).
    private static string JoinIdRange(List<long> ids, int start, int count) =>
        string.Join(",", ids.GetRange(start, Math.Min(count, ids.Count - start)));

    // Имя копии в стиле macOS (без стакания слова «копия»). Отрезаем уже имеющийся
    // суффикс «(копия)»/«(копия) N» и подбираем первый свободный: «X (копия)»,
    // «X (копия) 2», «X (копия) 3» … exists — проверка занятости имени.
    private static string NextCopyName(string sourceName, Func<string, bool> exists)
    {
        var stem = StripCopySuffix(sourceName);
        if (string.IsNullOrEmpty(stem)) stem = sourceName;

        string Make(int n) => n <= 1 ? $"{stem} (копия)" : $"{stem} (копия) {n}";

        var name = Make(1);
        var k = 2;
        while (exists(name)) name = Make(k++);
        return name;
    }

    // Убирает хвостовые суффиксы копий — «(копия)» и «(копия) N», один или несколько:
    // «8888 (копия) 2» → «8888», «8888 (копия) (копия)» → «8888».
    private static string StripCopySuffix(string name) =>
        Regex.Replace(name, @"(\s+\(копия\)(\s+\d+)?)+$", "").TrimEnd();

    // ── Уникальные имена при переносе/изменении (как дубль: «X», затем «X (копия)», «X (копия) 2» …) ──

    /// <summary>Имя коллекции, свободное в целевой группе: если <paramref name="desired"/> занято —
    /// «(копия)»-вариант (как дубль), иначе <paramref name="desired"/> без изменений.
    /// <paramref name="excludeId"/> — id самой коллекции (не считать её саму конфликтом).</summary>
    public string UniqueCollectionName(string desired, int? groupId, int? excludeId = null) =>
        CollectionNameExists(desired, groupId, excludeId)
            ? NextCopyName(desired, c => CollectionNameExists(c, groupId, excludeId))
            : desired;

    /// <summary>Имя группы, свободное на целевом уровне (родитель): если занято — «(копия)»-вариант.</summary>
    public string UniqueGroupName(string desired, int? parentId, int? excludeId = null) =>
        GroupNameExists(desired, parentId, excludeId)
            ? NextCopyName(desired, c => GroupNameExists(c, parentId, excludeId))
            : desired;

    /// <summary>Если имя коллекции совпало с другой в её группе — переименовать в «(копия)»-вариант
    /// (для bulk-переноса в другую БД, где имя копируется как есть). No-op, если конфликта нет.</summary>
    public void UniquifyCollectionName(int collectionId, int? groupId)
    {
        var col = db.DbCollections.Find(collectionId);
        if (col is null) return;
        var unique = UniqueCollectionName(col.Name, groupId, excludeId: collectionId);
        if (unique == col.Name) return;
        col.Name = unique;
        db.SaveChanges();
    }

    /// <summary>Если имя группы совпало с другой на её уровне — переименовать в «(копия)»-вариант.</summary>
    public void UniquifyGroupName(int groupId, int? parentId)
    {
        var g = db.DbGroups.Find(groupId);
        if (g is null) return;
        var unique = UniqueGroupName(g.Name, parentId, excludeId: groupId);
        if (unique == g.Name) return;
        g.Name = unique;
        db.SaveChanges();
    }

    public void DeleteField(int fieldId)
    {
        Demand(PermAction.DeleteField, CollectionOfField(fieldId));
        var entity = db.DbFields.Find(fieldId);
        if (entity is null) { db.SaveChanges(); return; }

        var colId = entity.CollectionId;
        Log("delete", "field", entity.Name, CollectionNameOf(colId));
        db.DbFields.Remove(entity);

        // Чистим метаданные колонки этого поля (порядок/видимость), чтобы в column_meta не
        // копились мёртвые ключи (иначе перемещение колонок начинает «не работать»).
        var key     = fieldId.ToString();
        var metaRow = db.ColumnMeta.FirstOrDefault(m => m.CollectionId == colId && m.Key == key);
        if (metaRow is not null) db.ColumnMeta.Remove(metaRow);

        db.SaveChanges();
    }

    public void ReorderFields(List<int> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var entity = db.DbFields.Find(orderedIds[i]);
            if (entity is not null) entity.Position = (i + 1) * 1000;
        }
        db.SaveChanges();
    }

    // ── Записи (низкоуровневые DbRecord) ──────────────────────────────────────

    public List<DbRecord> GetRecords(int collectionId) =>
        db.DbRecords
          .Where(r => r.CollectionId == collectionId)
          .OrderBy(r => r.Position)
          .Select(r => new DbRecord
          {
              Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
              UpdatedAt = r.UpdatedAt, CreatedBy = r.CreatedBy, UpdatedBy = r.UpdatedBy,
              Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
              Title = r.Title, Description = r.Description,
          })
          .ToList();

    /// <summary>
    /// Лёгкая выборка (Id, Title) всех записей коллекции в порядке (Position, Id) — для окна
    /// «Отображение записей». Только две колонки через проекцию + AsNoTracking: не материализует
    /// полные сущности и не засоряет трекер (на коллекциях в сотни тысяч строк это заметно).
    /// </summary>
    public List<(int Id, string Title)> GetRecordTitlesOrdered(int collectionId) =>
        db.DbRecords.AsNoTracking()
          .Where(r => r.CollectionId == collectionId)
          .OrderBy(r => r.Position).ThenBy(r => r.Id)
          .Select(r => new { r.Id, r.Title })
          .ToList()
          .Select(r => (r.Id, r.Title ?? ""))
          .ToList();

    /// <summary>То же, но на ОТДЕЛЬНОМ подключении — чтобы окно «Отображение записей» грузило весь
    /// список (на больших коллекциях это сотни тысяч строк) в фоновом потоке, не подвешивая UI.</summary>
    public List<(int Id, string Title)> GetRecordTitlesOrderedIsolated(int collectionId)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return new ConstructorRepository(ctx).GetRecordTitlesOrdered(collectionId);
    }

    /// <summary>Асинхронная версия <see cref="CountRecords"/> на ОТДЕЛЬНОМ подключении — для
    /// подсчёта перед массовыми операциями сайдбара без фриза UI (COUNT на 10 млн — сотни мс).</summary>
    public Task<int> CountRecordsAsync(int collectionId) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountRecords(collectionId);
        });

    // excludeHidden=true исключает записи, скрытые в окне «Отображение записей»
    // (record_display.Visible=false). Фильтр уходит в SQL до Count/Skip/Take, поэтому
    // виртуализация и пагинация таблицы сохраняются (см. VirtualRowList).
    public int CountRecords(int collectionId, bool excludeHidden = false) =>
        db.DbRecords.Count(r => r.CollectionId == collectionId
            && (!excludeHidden || !db.RecordDisplay.Any(d =>
                   d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)));

    // Страницы таблицы — raw SQL: EF-формы не масштабируются на миллионы строк. Смещение и
    // keyset обязаны идти ПО ИНДЕКСУ (CollectionId, Position): row-value сравнение
    // (Position, Id) > (@p, @id) даёт индексный сик (0 мс на любой глубине), а OR-форма
    // (Position > @p OR (Position = @p AND Id > @id)), в которую транслируется LINQ, НЕ ложится
    // на индекс — SQLite сканирует коллекцию с НАЧАЛА (на 10 млн — сотни мс на КАЖДУЮ страницу
    // скролла, т.е. «keyset» был O(глубины)). Хвост Id в ключе сика бесплатен: Id = rowid,
    // он неявно завершает каждую запись индекса.
    private const string RecordCols =
        "r.Id, r.CollectionId, r.CreatedAt, r.Position, r.StartDt, r.EndDt, r.AllDay, r.Title, r.Description, " +
        "r.UpdatedAt, r.CreatedBy, r.UpdatedBy";

    // Фильтр скрытых записей (окно «Отображение записей») для raw SQL страниц таблицы.
    private static string HiddenFilter(bool excludeHidden, string alias = "r") => excludeHidden
        ? $" AND NOT EXISTS (SELECT 1 FROM record_display d" +
          $" WHERE d.CollectionId = {alias}.CollectionId AND d.RecordId = {alias}.Id AND d.Visible = 0)"
        : "";

    /// <summary>Страница по offset (прыжок скроллбаром). Смещение идёт по ПОКРЫВАЮЩЕМУ индексу
    /// (CollectionId, Position): подзапрос отбирает только Id — пропускаемые записи не читаются
    /// из таблицы, join достаёт колонки лишь для строк страницы (~9 млн глубины: 190 мс вместо
    /// сотен и секунды холодным).</summary>
    public List<DbRecord> GetRecordsPage(int collectionId, int skip, int take, bool excludeHidden = false)
    {
        if (take <= 0) return [];
        return ReadRecordRows($@"SELECT {RecordCols}
                                 FROM db_records r
                                 JOIN (SELECT i.Id FROM db_records i
                                       WHERE i.CollectionId = @c{HiddenFilter(excludeHidden, "i")}
                                       ORDER BY i.Position, i.Id LIMIT @take OFFSET @skip) page ON page.Id = r.Id
                                 ORDER BY r.Position, r.Id",
                              ("@c", collectionId), ("@take", take), ("@skip", skip));
    }

    /// <summary>
    /// Keyset-пагинация: следующая страница ПОСЛЕ курсора (afterPosition, afterId), упорядочение
    /// по <c>(Position, Id)</c> — тот же ключ, что и составной индекс db_records(CollectionId, Position).
    /// Стоимость O(take) на любой глубине — row-value сик (см. комментарий у RecordCols).
    /// Курсор null/null — с самого начала. Двойной ключ (Position, Id) устойчив к дублям Position
    /// (после серий вставок/свопов), иначе строки с равной Position могли бы потеряться/повториться.
    /// </summary>
    public List<DbRecord> GetRecordsPageAfter(int collectionId, int? afterPosition, int? afterId,
                                              int take, bool excludeHidden = false)
    {
        if (take <= 0) return [];
        var cursor = afterPosition is not null && afterId is not null
            ? " AND (r.Position, r.Id) > (@p, @id)"
            : "";
        var args = new List<(string, object)> { ("@c", collectionId), ("@take", take) };
        if (cursor.Length > 0) { args.Add(("@p", afterPosition!.Value)); args.Add(("@id", afterId!.Value)); }
        return ReadRecordRows($@"SELECT {RecordCols} FROM db_records r
                                 WHERE r.CollectionId = @c{cursor}{HiddenFilter(excludeHidden)}
                                 ORDER BY r.Position, r.Id LIMIT @take",
                              args.ToArray());
    }

    /// <summary>Keyset НАЗАД: до take записей НЕПОСРЕДСТВЕННО ПЕРЕД курсором (beforePosition,
    /// beforeId), в обычном порядке таблицы. Для скролла ВВЕРХ (в т.ч. после прыжка вглубь):
    /// страница выше грузится сиком от ПЕРВОЙ строки страницы ниже — O(take) на любой глубине,
    /// вместо offset-запроса O(глубины) на каждую страницу. Реверс-скан индекса + разворот в памяти.</summary>
    public List<DbRecord> GetRecordsPageBefore(int collectionId, int beforePosition, int beforeId,
                                               int take, bool excludeHidden = false)
    {
        if (take <= 0) return [];
        var rows = ReadRecordRows($@"SELECT {RecordCols} FROM db_records r
                                     WHERE r.CollectionId = @c AND (r.Position, r.Id) < (@p, @id){HiddenFilter(excludeHidden)}
                                     ORDER BY r.Position DESC, r.Id DESC LIMIT @take",
                                  ("@c", collectionId), ("@p", beforePosition), ("@id", beforeId), ("@take", take));
        rows.Reverse();
        return rows;
    }

    // Общий читатель страниц db_records (raw SQL → DbRecord).
    private List<DbRecord> ReadRecordRows(string sql, params (string Name, object Value)[] args)
    {
        var result = new List<DbRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                AddParam(cmd, name).Value = value;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result.Add(new DbRecord
                {
                    Id           = rdr.GetInt32(0),
                    CollectionId = rdr.GetInt32(1),
                    CreatedAt    = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                    Position     = rdr.GetInt32(3),
                    StartDt      = rdr.IsDBNull(4) ? null : rdr.GetString(4),
                    EndDt        = rdr.IsDBNull(5) ? null : rdr.GetString(5),
                    AllDay       = rdr.GetInt32(6) != 0,
                    Title        = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                    Description  = rdr.IsDBNull(8) ? null : rdr.GetString(8),
                    UpdatedAt    = rdr.IsDBNull(9)  ? null : rdr.GetString(9),
                    CreatedBy    = rdr.IsDBNull(10) ? null : rdr.GetString(10),
                    UpdatedBy    = rdr.IsDBNull(11) ? null : rdr.GetString(11),
                });
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    // AsNoTracking обязателен: читатели зовут метод постранично на ОДНОМ контексте
    // (потоковый экспорт — сотни раз подряд), и без него change tracker копил бы ВСЕ
    // материализованные значения до конца операции (на 1 млн записей — гигабайты).
    public Dictionary<int, Dictionary<int, string>> GetFieldValuesBatch(IReadOnlyList<int> recordIds)
    {
        if (recordIds.Count == 0) return [];
        return db.DbFieldValues.AsNoTracking()
            .Where(fv => recordIds.Contains(fv.RecordId))
            .GroupBy(fv => fv.RecordId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(fv => fv.FieldId, fv => fv.Value));
    }

    /// <summary>Есть ли у записи сосед выше/ниже в порядке коллекции (Position, Id) — для пунктов
    /// «Сдвинуть вверх/вниз». Row-value сик по индексу (CollectionId, Position): O(1) на любой
    /// глубине. Прежний путь считал РАНГ записи (COUNT до её глубины) + полный COUNT коллекции —
    /// сотни мс на миллионах строк, и вызывался при КАЖДОЙ реализации строки (построение
    /// контекстного меню в OnLoadingRow) — это подвешивало прокрутку колесом.</summary>
    public bool HasNeighborRecord(int collectionId, int recordId, bool up)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            var cmp = up ? "<" : ">";
            cmd.CommandText = $@"SELECT EXISTS (
                                   SELECT 1 FROM db_records n
                                   JOIN db_records r ON r.Id = @id
                                   WHERE n.CollectionId = @c AND (n.Position, n.Id) {cmp} (r.Position, r.Id))";
            AddParam(cmd, "@id").Value = recordId;
            AddParam(cmd, "@c").Value  = collectionId;
            return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
        }
        finally { if (opened) conn.Close(); }
    }

    public DbRecord? GetRecord(int recordId)
    {
        var r = db.DbRecords.Find(recordId);
        if (r is null) return null;
        return new DbRecord
        {
            Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt, CreatedBy = r.CreatedBy, UpdatedBy = r.UpdatedBy,
            Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
            Title = r.Title, Description = r.Description,
        };
    }

    /// <summary>Записать название. Возвращает побочные изменения авто-привязки файлов (если
    /// «Название» — источник авто-file поля, его ячейка пересчитывается) — для мгновенного показа.</summary>
    public IReadOnlyList<(int FieldId, string Value)> SetRecordTitle(int recordId, string title)
    {
        Demand(PermAction.EditRecord, CollectionOfRecord(recordId));
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return [];
        var changed = rec.Title != title;
        if (changed)
        {
            // Побуквенное сохранение панели записи — сливаем в одну строку журнала.
            var key = (Kind: "title", RecordId: recordId, SubId: 0);
            var old = BeginEditMerge(key, rec.Title);
            if (old != title)
                EndEditMerge(key, Log("update", "record", title, CollectionNameOf(rec.CollectionId),
                                      detail: ChangeDetail.Rename(old, title)), old);
        }
        rec.Title = title;
        TouchUpdated(rec);
        SaveIgnoringRemoteDelete(rec);
        // «Название» как источник авто-привязки — пересчитать файл-поля этой записи.
        return changed ? ReactToSourceEdit(recordId, r => r.SourceIsTitle) : [];
    }

    /// <summary>Записать описание. Возвращает побочные изменения авто-привязки файлов (если
    /// «Описание» — источник авто-file поля) — для мгновенного показа.</summary>
    public IReadOnlyList<(int FieldId, string Value)> SetRecordDescription(int recordId, string? description)
    {
        Demand(PermAction.EditRecord, CollectionOfRecord(recordId));
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return [];
        var normalized = string.IsNullOrEmpty(description) ? null : description;
        var changed = rec.Description != normalized;
        if (changed)
        {
            // Побуквенное сохранение панели записи — сливаем в одну строку журнала.
            var key = (Kind: "desc", RecordId: recordId, SubId: 0);
            var old = BeginEditMerge(key, rec.Description ?? "");
            if (old != (normalized ?? ""))
                EndEditMerge(key, Log("update", "record", rec.Title, CollectionNameOf(rec.CollectionId),
                                      detail: ChangeDetail.Value(rec.Title, "Описание", old, normalized ?? "")),
                             old);
        }
        rec.Description = normalized;
        TouchUpdated(rec);
        SaveIgnoringRemoteDelete(rec);
        // «Описание» как источник авто-привязки — пересчитать файл-поля этой записи.
        return changed ? ReactToSourceEdit(recordId, r => r.SourceIsDesc) : [];
    }

    // Проставляет аудит «последней правки» записи (для типов свойств «Дата/Изменение …»):
    // время = сейчас, автор = снимок имени текущего пользователя. Вызывать в путях правки
    // данных записи (значение поля, название, описание, даты) перед SaveChanges.
    private static void TouchUpdated(DbRecordEntity rec)
    {
        rec.UpdatedAt = DateTime.Now.ToString("o");
        rec.UpdatedBy = CurrentUser.DisplayName;
    }

    // Многопользовательский режим: сущность мог удалить другой пользователь между нашим
    // чтением и записью — UPDATE попадает в 0 строк (DbUpdateConcurrencyException). Удалённое
    // не воскрешаем: отцепляем от трекера и дожимаем остальное (строку журнала).
    private void SaveIgnoringRemoteDelete(object entity)
    {
        try { db.SaveChanges(); }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(entity).State = EntityState.Detached;
            db.SaveChanges();
        }
    }

    public DbRecord AddRecord(DbRecord record)
    {
        Demand(PermAction.CreateRecord, record.CollectionId);
        var createdAt = record.CreatedAt ?? DateTime.Now.ToString("o");
        var author    = CurrentUser.DisplayName;
        var entity = new DbRecordEntity
        {
            CollectionId = record.CollectionId,
            CreatedAt    = createdAt,
            // Аудит-метаданные: при создании автор/время правки = автор/время создания.
            UpdatedAt    = createdAt,
            CreatedBy    = author,
            UpdatedBy    = author,
            Position     = record.Position,
            StartDt      = record.StartDt,
            EndDt        = record.EndDt,
            AllDay       = record.AllDay,
            Title        = record.Title,
            Description  = record.Description,
        };
        db.DbRecords.Add(entity);
        Log("create", "record", record.Title, CollectionNameOf(record.CollectionId));
        db.SaveChanges();
        record.Id = entity.Id;

        // Поля типа «пользователь»: новая запись по умолчанию получает текущего
        // пользователя (диалог записи может тут же перезаписать значение выбором).
        var userFields = db.DbFields
            .Where(f => f.CollectionId == record.CollectionId && f.FieldType == "user")
            .Select(f => f.Id).ToList();
        if (userFields.Count > 0)
        {
            foreach (var fid in userFields)
                db.DbFieldValues.Add(new DbFieldValueEntity
                {
                    RecordId = entity.Id, FieldId = fid, Value = CurrentUser.DisplayName,
                });
            db.SaveChanges();
        }

        return record;
    }

    // Создаёт запись сразу после afterRecordId (по позиции).
    public DbRecord AddRecordAfter(DbRecord record, int afterRecordId)
    {
        record.Position = PositionAfterRecord(record.CollectionId, afterRecordId);
        return AddRecord(record);
    }

    // Позиция для вставки записи сразу после afterRecordId (между ней и следующей).
    // Соседи берутся запросами по индексу (CollectionId, Position) — без загрузки всей
    // коллекции в память (иначе на 100k+ зависание). Инициатор не найден — позиция в конце.
    // Если зазора между соседями нет — редкая перенумерация одним bulk-SQL (RenumberPositions),
    // тоже без материализации в EF.
    private int PositionAfterRecord(int collectionId, int afterRecordId)
    {
        var after = db.DbRecords.AsNoTracking()
            .Where(r => r.Id == afterRecordId && r.CollectionId == collectionId)
            .Select(r => new { r.Position, r.Id })
            .FirstOrDefault();

        if (after is null)   // инициатор не найден — в конец
            return (db.DbRecords.Where(r => r.CollectionId == collectionId)
                        .Select(r => (int?)r.Position).Max() ?? 0) + 1000;

        var nextPos = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId
                && (r.Position > after.Position || (r.Position == after.Position && r.Id > after.Id)))
            .OrderBy(r => r.Position).ThenBy(r => r.Id)
            .Select(r => (int?)r.Position)
            .FirstOrDefault();

        if (nextPos is not int np) return after.Position + 1000;          // после последней
        if (np - after.Position >= 2) return after.Position + (np - after.Position) / 2;

        // Зазора нет — перенумеровываем всю коллекцию одним SQL-запросом, затем считаем середину.
        RenumberPositions(collectionId);
        var ap = db.DbRecords.AsNoTracking().Where(r => r.Id == afterRecordId).Select(r => r.Position).First();
        var n2 = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId && r.Position > ap)
            .OrderBy(r => r.Position).Select(r => (int?)r.Position).FirstOrDefault();
        return n2 is int n ? ap + (n - ap) / 2 : ap + 1000;
    }

    // Перенумеровать Position всех записей коллекции с шагом 1000 ОДНИМ bulk-SQL-запросом
    // (оконная функция ROW_NUMBER), не загружая записи в EF. Используется как редкий
    // «ребаланс», когда между соседями не осталось зазора для вставки.
    private void RenumberPositions(int collectionId)
    {
        db.Database.ExecuteSqlInterpolated($@"
            UPDATE db_records
            SET Position = sub.rn * 1000
            FROM (SELECT Id, ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
                  FROM db_records WHERE CollectionId = {collectionId}) AS sub
            WHERE db_records.Id = sub.Id");
        // Сбрасываем закешированные EF позиции, чтобы последующие чтения видели свежие значения.
        foreach (var e in db.ChangeTracker.Entries<DbRecordEntity>().ToList())
            if (e.Entity.CollectionId == collectionId) e.Reload();
    }

    public void UpdateRecord(DbRecord record)
    {
        Demand(PermAction.EditRecord, record.CollectionId);
        if (record.Id is null) return;
        var entity = db.DbRecords.Find(record.Id.Value);
        if (entity is null) return;

        // Перестановки (Position) журнал не засоряют — только смысловые изменения.
        // «до → после» — для переименования и правки описания (даты видны в самой записи).
        var meaningfulChange =
            entity.Title != record.Title || entity.Description != record.Description ||
            entity.StartDt != record.StartDt || entity.EndDt != record.EndDt ||
            entity.AllDay != record.AllDay || entity.CollectionId != record.CollectionId;
        if (meaningfulChange)
            Log("update", "record", record.Title, CollectionNameOf(record.CollectionId),
                detail: entity.Title != record.Title
                    ? ChangeDetail.Rename(entity.Title, record.Title)
                    : entity.Description != record.Description
                        ? ChangeDetail.Value(record.Title, "Описание",
                                             entity.Description ?? "", record.Description ?? "")
                        : null);

        entity.CollectionId = record.CollectionId;
        entity.Position     = record.Position;
        entity.StartDt      = record.StartDt;
        entity.EndDt        = record.EndDt;
        entity.AllDay       = record.AllDay;
        entity.Title        = record.Title;
        entity.Description  = record.Description;
        if (meaningfulChange) TouchUpdated(entity);   // аудит «последней правки» (не для чистых перестановок)
        db.SaveChanges();
    }

    public void ReorderRecords(List<int> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var entity = db.DbRecords.Find(orderedIds[i]);
            if (entity is not null) entity.Position = (i + 1) * 1000;
        }
        db.SaveChanges();
    }

    // Переместить запись на одну позицию вверх/вниз в порядке коллекции (по (Position, Id)).
    // Сосед ищется ОДНИМ индексным запросом (не загрузкой всей коллекции — иначе на 100k+
    // зависание), затем меняем местами Position двух записей. Крайняя запись — no-op.
    public void MoveRecord(int collectionId, int recordId, bool up)
    {
        Demand(PermAction.EditRecord, collectionId);
        var rec = db.DbRecords.Find(recordId);
        if (rec is null || rec.CollectionId != collectionId) return;
        var p = rec.Position;
        var id = rec.Id;

        // Ближайший сосед в нужную сторону по упорядочению (Position, Id).
        var neighbor = up
            ? db.DbRecords.Where(r => r.CollectionId == collectionId
                  && (r.Position < p || (r.Position == p && r.Id < id)))
                .OrderByDescending(r => r.Position).ThenByDescending(r => r.Id).FirstOrDefault()
            : db.DbRecords.Where(r => r.CollectionId == collectionId
                  && (r.Position > p || (r.Position == p && r.Id > id)))
                .OrderBy(r => r.Position).ThenBy(r => r.Id).FirstOrDefault();
        if (neighbor is null) return;   // крайняя запись — no-op

        if (neighbor.Position != rec.Position)
            (rec.Position, neighbor.Position) = (neighbor.Position, rec.Position);
        else
            // Позиции слиплись (ничью решал Id) — сдвигаем текущую на 1, чтобы реально
            // переставить относительно соседа. Без глобальной перенумерации.
            rec.Position = up ? rec.Position - 1 : rec.Position + 1;

        db.SaveChanges();
    }

    public void DeleteRecord(int recordId)
    {
        Demand(PermAction.DeleteRecord, CollectionOfRecord(recordId));
        var entity = db.DbRecords.Find(recordId);
        if (entity is not null)
        {
            Log("delete", "record", entity.Title, CollectionNameOf(entity.CollectionId));
            db.DbRecords.Remove(entity);
        }
        db.SaveChanges();
    }

    public DbRecord DuplicateRecord(int recordId)
    {
        Demand(PermAction.DuplicateRecord, CollectionOfRecord(recordId));
        var src = db.DbRecords.Find(recordId);
        if (src is null) throw new InvalidOperationException($"Record {recordId} not found");

        var copy = new DbRecordEntity
        {
            CollectionId = src.CollectionId,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = PositionAfterRecord(src.CollectionId, recordId),   // сразу после исходной
            StartDt      = src.StartDt,
            EndDt        = src.EndDt,
            AllDay       = src.AllDay,
            Title        = src.Title,
            Description  = src.Description,
        };
        db.DbRecords.Add(copy);
        Log("duplicate", "record", src.Title, CollectionNameOf(src.CollectionId));
        db.SaveChanges();

        var fieldValues = db.DbFieldValues.Where(fv => fv.RecordId == recordId).ToList();
        foreach (var fv in fieldValues)
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = copy.Id, FieldId = fv.FieldId, Value = fv.Value });
        db.SaveChanges();

        // Копия записи могла принести значения видимых полей-дат — наполняем проекцию календаря.
        RebuildCalendarIndexForRecord(copy.Id);

        return new DbRecord
        {
            Id           = copy.Id,
            CollectionId = copy.CollectionId,
            Position     = copy.Position,
            Title        = copy.Title,
            Description  = copy.Description,
        };
    }

    /// <summary>Дубль ОДНОЙ записи (копия сразу после исходной, по середине — без сдвига хвоста) на
    /// ОТДЕЛЬНОМ подключении: чтобы контроллер выполнял вставку в фоне (Task.Run) и обновлял таблицу
    /// уже после — без «моргания», которое даёт синхронная вставка-плюс-refresh в одном кадре UI.</summary>
    public DbRecord DuplicateRecordIsolated(int recordId)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        // Лукап через свежий контекст: обёртку зовут из фонового потока, общий контекст —
        // только для UI-потока (Demand читает кеш, потокобезопасна).
        Demand(PermAction.DuplicateRecord, repo.CollectionOfRecord(recordId));
        return repo.DuplicateRecord(recordId);
    }

    /// <summary>Создать запись (в конце или сразу после afterRecordId) на ОТДЕЛЬНОМ подключении —
    /// чтобы контроллер вставлял в фоне (Task.Run) и обновлял таблицу уже после, без «моргания».</summary>
    public DbRecord AddRecordIsolated(DbRecord record, int? afterRecordId)
    {
        Demand(PermAction.CreateRecord, record.CollectionId);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        if (afterRecordId is { } aid) return repo.AddRecordAfter(record, aid);
        record.Position = (repo.CountRecords(record.CollectionId) + 1) * 1000;
        return repo.AddRecord(record);
    }

    // ── Пакетные операции над выбранными записями (мультивыделение в таблице) ────
    // Удаление/дубль МНОГИХ записей сразу — bulk-SQL мимо EF (как дубль/удаление коллекций),
    // на ОТДЕЛЬНОМ подключении в фоне (см. принцип 5 в docs/architecture/performance). Прежний
    // путь — цикл DeleteRecord/DuplicateRecord с SaveChanges на КАЖДУЮ запись — на 1000+ строк
    // давал сотни/тысячи транзакций и фриз UI.

    /// <summary>Удалить выбранные записи на ОТДЕЛЬНОМ подключении (фон + прогресс).</summary>
    public void DeleteRecordsIsolated(IReadOnlyList<int> recordIds, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        // Право проверяем по коллекции ПЕРВОЙ записи (мультивыделение таблицы всегда
        // в одной коллекции); лукап — через свежий контекст (обёртка работает в фоне).
        if (recordIds.Count > 0)
            Demand(PermAction.DeleteRecord, repo.CollectionOfRecord(recordIds[0]));
        repo.DeleteRecordsData(recordIds, progress);
    }

    /// <summary>Удалить записи ПАКЕТАМИ по списку их Id (значения и calendar_index уходят каскадом по
    /// FK ON DELETE CASCADE). record_display/ui_row_heights с записью FK-связи НЕ имеют (каскада нет) —
    /// чистим явно, иначе настройки видимости/высоты «осиротеют» и могут прилипнуть к новой записи с
    /// тем же rowid. Пакеты — ради гранулярности прогресса.</summary>
    public void DeleteRecordsData(IReadOnlyList<int> recordIds, IProgress<int>? progress = null, int batchSize = 10000,
                                  bool log = true)
    {
        if (recordIds.Count == 0) return;
        var ids = recordIds.Distinct().ToList();

        // Одна строка журнала на пакет; имя коллекции — до удаления (мультивыделение
        // таблицы всегда в одной коллекции).
        var colName = log ? CollectionNameOf(CollectionOfRecord(ids[0])) : null;

        using var tx = db.Database.BeginTransaction();
        var done = 0;
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var inList = string.Join(",", ids.Skip(i).Take(batchSize));
            db.Database.ExecuteSqlRaw($"DELETE FROM db_records     WHERE Id       IN ({inList})");
            db.Database.ExecuteSqlRaw($"DELETE FROM record_display WHERE RecordId IN ({inList})");
            db.Database.ExecuteSqlRaw($"DELETE FROM ui_row_heights WHERE RecordId IN ({inList})");
            done = Math.Min(ids.Count, i + batchSize);
            progress?.Report(done);
        }
        tx.Commit();
        if (log) LogNow("delete", "records", "", colName, ids.Count);
    }

    /// <summary>Дубль выбранных записей на ОТДЕЛЬНОМ подключении (фон + прогресс). Возвращает id копий
    /// (для выделения их голубым после операции).</summary>
    public List<int> DuplicateRecordsIsolated(int collectionId, IReadOnlyList<int> recordIds, IProgress<int>? progress = null)
    {
        Demand(PermAction.DuplicateRecord, collectionId);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return new ConstructorRepository(ctx).DuplicateRecordsData(collectionId, recordIds, progress);
    }

    /// <summary>Дубль выбранных записей коллекции bulk-SQL: копии встают СРАЗУ ПОСЛЕ последней (самой
    /// нижней по порядку Position, Id) выбранной записи — как дубль одной записи ставит копию следом за
    /// исходной, а не в конец таблицы. Поля те же (одна коллекция) → FieldId не перемэппливаем. Всё в одной
    /// транзакции: освобождаем count слотов сразу после якоря сдвигом ТОЛЬКО хвоста (без перенумерации всей
    /// коллекции); 1) записи одним INSERT…SELECT в эти слоты (новые id — непрерывный блок в порядке вставки:
    /// firstNew = last_insert_rowid − count + 1); 2) значения пакетами по рангу (новый RecordId = firstNew +
    /// ранг − 1), один INSERT по всем полям на пакет. progress — нарастающее число скопированных записей.
    /// Возвращает id созданных копий (непрерывный блок firstNew..firstNew+count−1) — для их выделения.</summary>
    public List<int> DuplicateRecordsData(int collectionId, IReadOnlyList<int> recordIds,
                                          IProgress<int>? progress = null, int batchSize = 10000)
    {
        if (recordIds.Count == 0) return [];
        var ids = recordIds.Distinct().ToList();

        // PG: явная карта id через nextval вместо арифметики непрерывного блока
        // (см. CopyCollectionDataPg — та же причина: identity-последовательность общая).
        if (db.IsPostgres) return DuplicateRecordsDataPg(collectionId, ids, progress, batchSize);

        using var tx = db.Database.BeginTransaction();

        // Временный набор выбранных Id — чтобы ранг (ROW_NUMBER по Position,Id) и JOIN значений шли
        // по одному подмножеству. Живёт на этом подключении; пересоздаём.
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_recsel");
        db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_recsel(src_id INTEGER PRIMARY KEY)");
        foreach (var id in ids)
            db.Database.ExecuteSqlInterpolated($"INSERT INTO _dup_recsel(src_id) VALUES ({id})");

        // Реально существующие в коллекции из выбранных (мусорные id отсекаем).
        var count = ScalarInt($@"
            SELECT COUNT(*) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
            WHERE r.CollectionId = {collectionId}");
        if (count == 0)
        {
            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_recsel");
            tx.Commit();
            progress?.Report(0);
            return [];
        }

        // Якорь — ПОСЛЕДНЯЯ (самая нижняя) выбранная запись в порядке (Position, Id): её Position, и среди
        // делящих эту Position — максимальный Id. Копии встанут сразу после неё.
        var anchorPos = ScalarInt($@"
            SELECT MAX(r.Position) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
            WHERE r.CollectionId = {collectionId}");
        var anchorId  = ScalarInt($@"
            SELECT MAX(r.Id) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
            WHERE r.CollectionId = {collectionId} AND r.Position = {anchorPos}");

        // Освобождаем count слотов сразу после якоря: всё, что идёт после него в порядке (Position, Id),
        // сдвигаем на (count+1)*1000 — с запасом, чтобы копии (anchorPos + ранг*1000, ранг 1..count) не
        // столкнулись даже с записями, делящими Position якоря (Id > anchorId). Перенумерация всей коллекции
        // не нужна — трогаем только хвост. Сами выбранные (Position ≤ anchorPos, при равенстве Id ≤ anchorId)
        // НЕ сдвигаются, поэтому их ранг для значений совпадёт с рангом при вставке копий.
        var shift = (count + 1) * 1000;
        db.Database.ExecuteSqlInterpolated($@"
            UPDATE db_records SET Position = Position + {shift}
            WHERE CollectionId = {collectionId}
              AND (Position > {anchorPos} OR (Position = {anchorPos} AND Id > {anchorId}))");

        var createdAt = DateTime.Now.ToString("o");
        var createdBy = CurrentUser.DisplayName;   // дубль = новая запись (свой автор/время)

        // 1) Копии записей в освобождённый диапазон сразу после якоря: Position = anchorPos + ранг*1000
        //    (ранг по Position, Id исходников). Новые rowid — непрерывный блок в этом порядке (мы
        //    единственный писатель в транзакции).
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Description)
            SELECT {collectionId}, {createdAt}, {createdAt}, {createdBy}, {createdBy},
                   {anchorPos} + ROW_NUMBER() OVER (ORDER BY r.Position, r.Id) * 1000,
                   r.StartDt, r.EndDt, r.AllDay, r.Title, r.Description
            FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
            WHERE r.CollectionId = {collectionId}
            ORDER BY r.Position, r.Id");
        var firstNew = (int)(LastInsertRowid() - count + 1);

        // 2) Значения пакетами по рангу: новый RecordId = firstNew + ранг − 1, один INSERT по всем полям.
        var processed = 0;
        while (processed < count)
        {
            var lo = processed;
            var hi = Math.Min(count, processed + batchSize);
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO db_field_values (RecordId, FieldId, Value)
                SELECT {firstNew} + (r.rn - 1), fv.FieldId, fv.Value
                FROM db_field_values fv
                JOIN (SELECT s.src_id AS old_id,
                             ROW_NUMBER() OVER (ORDER BY rr.Position, rr.Id) AS rn
                      FROM _dup_recsel s JOIN db_records rr ON rr.Id = s.src_id
                      WHERE rr.CollectionId = {collectionId}) r
                  ON r.old_id = fv.RecordId
                WHERE r.rn > {lo} AND r.rn <= {hi}");
            processed = hi;
            progress?.Report(processed);
        }

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_recsel");
        tx.Commit();

        // Копии могли принести значения видимых полей-дат → наполняем их проекцию календаря
        // по диапазону новых id (без удаления — строки новые).
        var lastNew = firstNew + count - 1;
        FillCalendarIndex(q => q.Where(v => v.RecordId >= firstNew && v.RecordId <= lastNew));

        LogNow("duplicate", "records", "", CollectionNameOf(collectionId), count);
        return Enumerable.Range(firstNew, count).ToList();   // id копий (для выделения)
    }

    // PG-вариант дубля выбранных записей (см. DuplicateRecordsData — SQLite-путь): вместо
    // арифметики «firstNew + ранг − 1» — явная карта id через nextval (identity-последовательность
    // общая для всех клиентов, непрерывного блока не существует). Логика та же: якорь, сдвиг
    // хвоста, копии сразу после якоря, значения пакетами, проекция календаря по списку новых id.
    private List<int> DuplicateRecordsDataPg(int collectionId, List<int> ids,
                                             IProgress<int>? progress, int batchSize)
    {
        List<int> newIds;
        using (var tx = db.Database.BeginTransaction())
        {
            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_recsel");
            db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_recsel(src_id INTEGER PRIMARY KEY)");
            foreach (var id in ids)
                db.Database.ExecuteSqlInterpolated($"INSERT INTO _dup_recsel(src_id) VALUES ({id})");

            var count = ScalarInt($@"
                SELECT COUNT(*) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
                WHERE r.CollectionId = {collectionId}");
            if (count == 0)
            {
                db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_recsel");
                tx.Commit();
                progress?.Report(0);
                return [];
            }

            var anchorPos = ScalarInt($@"
                SELECT MAX(r.Position) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
                WHERE r.CollectionId = {collectionId}");
            var anchorId  = ScalarInt($@"
                SELECT MAX(r.Id) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
                WHERE r.CollectionId = {collectionId} AND r.Position = {anchorPos}");

            var shift = (count + 1) * 1000;
            db.Database.ExecuteSqlInterpolated($@"
                UPDATE db_records SET Position = Position + {shift}
                WHERE CollectionId = {collectionId}
                  AND (Position > {anchorPos} OR (Position = {anchorPos} AND Id > {anchorId}))");

            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_rmap");
            db.Database.ExecuteSqlRaw($@"
                CREATE TEMP TABLE _dup_rmap AS
                SELECT r.Id AS old_id,
                       nextval(pg_get_serial_sequence('db_records', 'id')) AS new_id,
                       ROW_NUMBER() OVER (ORDER BY r.Position, r.Id) AS rn
                FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
                WHERE r.CollectionId = {collectionId}");

            var createdAt = DateTime.Now.ToString("o");
            var createdBy = CurrentUser.DisplayName;   // дубль = новая запись (свой автор/время)
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Description)
                SELECT m.new_id, {collectionId}, {createdAt}, {createdAt}, {createdBy}, {createdBy}, {anchorPos} + m.rn * 1000,
                       r.StartDt, r.EndDt, r.AllDay, r.Title, r.Description
                FROM db_records r JOIN _dup_rmap m ON m.old_id = r.Id");

            var processed = 0;
            while (processed < count)
            {
                var lo = processed;
                var hi = Math.Min(count, processed + batchSize);
                db.Database.ExecuteSqlInterpolated($@"
                    INSERT INTO db_field_values (RecordId, FieldId, Value)
                    SELECT m.new_id, fv.FieldId, fv.Value
                    FROM db_field_values fv
                    JOIN _dup_rmap m ON m.old_id = fv.RecordId
                    WHERE m.rn > {lo} AND m.rn <= {hi}");
                processed = hi;
                progress?.Report(processed);
            }

            newIds = new List<int>(count);
            using (var c = db.Database.GetDbConnection().CreateCommand())
            {
                c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
                c.CommandText = "SELECT new_id FROM _dup_rmap ORDER BY rn";
                using var rdr = c.ExecuteReader();
                while (rdr.Read()) newIds.Add(Convert.ToInt32(rdr.GetValue(0)));
            }

            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_rmap");
            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_recsel");
            tx.Commit();
        }

        // Копии могли принести значения видимых полей-дат → наполняем их проекцию по списку id.
        if (newIds.Count > 0)
        {
            var idsForFill = newIds;
            FillCalendarIndex(q => q.Where(v => idsForFill.Contains(v.RecordId)));
        }

        LogNow("duplicate", "records", "", CollectionNameOf(collectionId), newIds.Count);
        return newIds;
    }

    // Скалярный int на текущем подключении (в открытой транзакции) — для bulk-операций с temp-таблицами.
    private int ScalarInt(string sql)
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        if (db.Database.CurrentTransaction is { } t) c.Transaction = t.GetDbTransaction();
        c.CommandText = sql;
        return Convert.ToInt32(c.ExecuteScalar());
    }

    // ── Перенос записи в другую коллекцию / БД ──────────────────────────────────
    // Грамотный перенос: значения сопоставляются с полями целевой коллекции ПО ИМЕНИ (без учёта
    // регистра) с приведением типов (ConvertValueForType — текст↔дата и т.д.). Совпадающие по
    // имени поля переносятся; для остальных — createMissingFields решает: создать недостающее
    // свойство в цели (полное сохранение) или отбросить значение. Общая логика для таблицы
    // («Расположение») и календаря (смена коллекции/БД в окне записи).

    /// <summary>Переносит значения свойств записи-источника (в репозитории <paramref name="source"/> —
    /// может быть другой БД) в УЖЕ созданную запись целевой коллекции этого репозитория, сопоставляя
    /// поля по имени с приведением типов. <paramref name="createMissingFields"/> — создавать в цели
    /// отсутствующие свойства (иначе их значения отбрасываются). <paramref name="excludeTargetFieldId"/>
    /// — целевое поле, в которое писать нельзя (например, уже выставленное поле-дата календаря —
    /// чтобы не затереть отредактированную дату).</summary>
    public void CopyFieldValuesByName(
        ConstructorRepository source, int sourceRecordId, int sourceCollectionId,
        int targetRecordId, int targetCollectionId,
        bool createMissingFields, int excludeTargetFieldId = 0)
    {
        var srcFields = source.GetFields(sourceCollectionId).ToDictionary(f => f.Id!.Value);
        var srcValues = source.GetFieldValues(sourceRecordId);
        if (srcValues.Count == 0) return;

        var targetByName = new Dictionary<string, DbField>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in GetFields(targetCollectionId))
            targetByName.TryAdd(tf.Name, tf);

        // Позиция для создаваемых недостающих свойств — в конец схемы цели.
        var nextPos = (db.DbFields.Where(f => f.CollectionId == targetCollectionId)
                                  .Select(f => (int?)f.Position).Max() ?? 0) + 1000;

        foreach (var (srcFieldId, value) in srcValues)
        {
            if (value.Length == 0 || !srcFields.TryGetValue(srcFieldId, out var meta)) continue;

            if (!targetByName.TryGetValue(meta.Name, out var target))
            {
                if (!createMissingFields) continue;
                target = AddField(new DbField
                {
                    CollectionId = targetCollectionId, Name = meta.Name,
                    FieldType    = meta.FieldType,      ConfigJson = meta.ConfigJson,
                    Position     = nextPos,             IsCalStart = meta.IsCalStart,
                    IsCalEnd     = meta.IsCalEnd,       IsRequired = meta.IsRequired,
                    IsTaskExec   = meta.IsTaskExec,     IsTaskAssigner = meta.IsTaskAssigner,
                    IsTaskStatus = meta.IsTaskStatus,
                });
                nextPos += 1000;
                targetByName[meta.Name] = target;
            }

            if (target.Id == excludeTargetFieldId) continue;
            SetFieldValue(targetRecordId, target.Id!.Value, ConvertValueForType(value, meta.FieldType, target.FieldType));
        }
    }

    /// <summary>Пакетный перенос записей из коллекции <paramref name="sourceCollectionId"/> в целевую
    /// коллекцию этого репозитория на ОТДЕЛЬНЫХ подключениях (фон + прогресс) — чтобы не блокировать
    /// UI-контексты. <paramref name="sourceDbPath"/> — путь БД-источника (совпадает с этим репо при
    /// переносе внутри одной БД). Возвращает id созданных записей.</summary>
    public List<int> MoveRecordsToCollectionIsolated(
        string sourceDbPath, int sourceCollectionId, IReadOnlyList<int> recordIds, int targetCollectionId,
        bool createMissingFields, IProgress<int>? progress = null)
    {
        if (recordIds.Count == 0) return [];
        // this — ЦЕЛЕВАЯ база: создание записей в целевой коллекции. Право удаления в
        // источнике проверяет UI по репозиторию источника (перенос в одной базе покрыт:
        // this и есть источник — проверяем и удаление).
        Demand(PermAction.CreateRecord, targetCollectionId);
        if (string.Equals(sourceDbPath, DbPath, StringComparison.OrdinalIgnoreCase))
            Demand(PermAction.DeleteRecord, sourceCollectionId);

        using var targetCtx = new AppDbContext(DbPath);
        targetCtx.ApplyPerformancePragmas();
        var targetRepo = new ConstructorRepository(targetCtx);

        var sameDb = string.Equals(sourceDbPath, DbPath, StringComparison.OrdinalIgnoreCase);
        AppDbContext? sourceCtx = null;
        if (!sameDb) { sourceCtx = new AppDbContext(sourceDbPath); sourceCtx.ApplyPerformancePragmas(); }
        var sourceRepo = sameDb ? targetRepo : new ConstructorRepository(sourceCtx!);

        try { return targetRepo.MoveRecordsCore(sourceRepo, sourceCollectionId, recordIds, targetCollectionId, createMissingFields, progress); }
        finally { sourceCtx?.Dispose(); }
    }

    // Перенос записей выполняется на контекстах targetRepo (this) и sourceRepo. Главное про
    // производительность (как DuplicateRecordsData/DeleteRecordsData, см. docs/architecture/performance):
    //   • план сопоставления полей считается ОДИН раз — все записи из одной коллекции-источника;
    //   • значения каждой записи пишутся одним AddRange (без SaveChanges на каждое значение и без
    //     инкрементной синхронизации индекса);
    //   • проекция календаря наполняется ОДИН раз по диапазону новых id (новые id непрерывны —
    //     мы единственный писатель);
    //   • исходные записи удаляются bulk-ом (DeleteRecordsData, каскад чистит значения и индекс);
    //   • копирование идёт в одной транзакции (один коммит), трекер EF чистим после каждой записи —
    //     память ограничена независимо от размера пакета.
    private List<int> MoveRecordsCore(ConstructorRepository source, int sourceCollectionId,
        IReadOnlyList<int> recordIds, int targetCollectionId, bool createMissingFields, IProgress<int>? progress)
    {
        // 1) План: srcFieldId → (целевое поле, типы). Недостающие свойства создаём ОДИН раз (при флаге),
        //    до транзакции копирования (AddField коммитит сам).
        var srcFields    = source.GetFields(sourceCollectionId);
        var targetByName = new Dictionary<string, DbField>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in GetFields(targetCollectionId)) targetByName.TryAdd(tf.Name, tf);

        var nextFieldPos = (db.DbFields.Where(f => f.CollectionId == targetCollectionId)
                                       .Select(f => (int?)f.Position).Max() ?? 0) + 1000;

        var plan = new Dictionary<int, (int FieldId, string From, string To)>();
        foreach (var sf in srcFields)
        {
            if (!targetByName.TryGetValue(sf.Name, out var tf))
            {
                if (!createMissingFields) continue;
                tf = AddField(new DbField
                {
                    CollectionId = targetCollectionId, Name = sf.Name,
                    FieldType    = sf.FieldType,        ConfigJson = sf.ConfigJson,
                    Position     = nextFieldPos,        IsCalStart = sf.IsCalStart,
                    IsCalEnd     = sf.IsCalEnd,         IsRequired = sf.IsRequired,
                    IsTaskExec   = sf.IsTaskExec,       IsTaskAssigner = sf.IsTaskAssigner,
                    IsTaskStatus = sf.IsTaskStatus,
                });
                nextFieldPos += 1000;
                targetByName[sf.Name] = tf;
            }
            plan[sf.Id!.Value] = (tf.Id!.Value, sf.FieldType, tf.FieldType);
        }

        // 2) Копируем записи (одна транзакция, один коммит). Position новых — в конец, по 1000.
        var posBase = db.DbRecords.Where(r => r.CollectionId == targetCollectionId)
                                  .Select(r => (int?)r.Position).Max() ?? 0;
        var newIds        = new List<int>(recordIds.Count);
        var copiedSources = new List<int>(recordIds.Count);
        var done = 0;

        using (var tx = db.Database.BeginTransaction())
        {
            foreach (var srcId in recordIds)
            {
                var header = source.GetRecord(srcId);
                if (header is null) { progress?.Report(++done); continue; }

                posBase += 1000;
                var rec = new DbRecordEntity
                {
                    CollectionId = targetCollectionId, CreatedAt = DateTime.Now.ToString("o"),
                    Position     = posBase,            StartDt   = header.StartDt,
                    EndDt        = header.EndDt,       AllDay    = header.AllDay,
                    Title        = header.Title,       Description = header.Description,
                };
                db.DbRecords.Add(rec);
                db.SaveChanges();   // нужен Id записи для значений (в рамках транзакции, без коммита)

                var values = source.GetFieldValues(srcId);
                var rows   = new List<DbFieldValueEntity>(values.Count);
                foreach (var (srcFieldId, value) in values)
                {
                    if (value.Length == 0 || !plan.TryGetValue(srcFieldId, out var t)) continue;
                    rows.Add(new DbFieldValueEntity
                    {
                        RecordId = rec.Id, FieldId = t.FieldId,
                        Value    = ConvertValueForType(value, t.From, t.To),
                    });
                }
                if (rows.Count > 0) { db.DbFieldValues.AddRange(rows); db.SaveChanges(); }

                newIds.Add(rec.Id);
                copiedSources.Add(srcId);

                db.ChangeTracker.Clear();   // запись и значения уже во вставке транзакции — трекер не нужен
                progress?.Report(++done);
            }
            tx.Commit();
        }

        // 3) Проекция календаря — один раз по СПИСКУ новых id (EF переведёт Contains в IN):
        // диапазон firstNew..lastNew полагался на непрерывность новых id — на PG соседние id
        // могли достаться другим клиентам (общая identity-последовательность).
        if (newIds.Count > 0)
            FillCalendarIndex(q => q.Where(v => newIds.Contains(v.RecordId)));

        // 4) Исходные записи — bulk-удалением (каскад уносит их значения и calendar_index).
        // Без строки «удалил» в журнале — это перенос, одна строка «перенёс» ниже.
        if (copiedSources.Count > 0) source.DeleteRecordsData(copiedSources, log: false);

        if (newIds.Count > 0)
            LogNow("move", "records", "", CollectionNameOf(targetCollectionId), newIds.Count);
        return newIds;
    }

    // ── Значения полей ────────────────────────────────────────────────────────

    public Dictionary<int, string> GetFieldValues(int recordId) =>
        db.DbFieldValues
          .Where(fv => fv.RecordId == recordId)
          .ToDictionary(fv => fv.FieldId, fv => fv.Value);

    /// <summary>Все значения полей коллекции одним запросом (для фильтрации/сортировки).</summary>
    public Dictionary<int, Dictionary<int, string>> GetFieldValuesByCollection(int collectionId) =>
        db.DbFieldValues
          .Where(fv => fv.Record.CollectionId == collectionId)
          .Select(fv => new { fv.RecordId, fv.FieldId, fv.Value })
          .ToList()
          .GroupBy(x => x.RecordId)
          .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.FieldId, x => x.Value));

    // ── Фильтр/сортировка в SQL (для виртуализации отфильтрованного представления) ──
    //
    // Вместо загрузки ВСЕХ строк в память (старый LoadAllRows — зависание на больших
    // коллекциях) фильтр уходит в SQL и возвращает только id совпавших записей. Показ
    // затем виртуализируется постранично по этому списку id (см. VirtualRowList в режиме
    // списка id). Сортировка — отдельным шагом в UI (тот же компаратор, что и раньше),
    // по значениям одной колонки (GetColumnValues), а не по всем полям.

    /// <summary>
    /// id записей коллекции, прошедших фильтры (key → набор допустимых значений), в порядке
    /// (Position, Id). Пустое значение "" в наборе допускает записи без значения/с пустым.
    /// excludeHidden — исключить скрытые в окне «Отображение записей».
    /// </summary>
    public List<int> GetFilteredRecordIds(int collectionId,
        IReadOnlyDictionary<string, HashSet<string>> filters, bool excludeHidden = false)
    {
        var q = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId
                && (!excludeHidden || !db.RecordDisplay.Any(d =>
                       d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)));

        foreach (var (key, allowed) in filters)
        {
            var allow = allowed.ToList();   // EF переведёт Contains → SQL IN (...)
            if (key == TitleKey)
                q = q.Where(r => allow.Contains(r.Title));
            else if (key == DescKey)
                q = q.Where(r => allow.Contains(r.Description ?? ""));
            else if (int.TryParse(key, out var fid))
            {
                var hasEmpty = allowed.Contains("");
                q = q.Where(r =>
                    db.DbFieldValues.Any(fv => fv.RecordId == r.Id && fv.FieldId == fid && allow.Contains(fv.Value))
                    || (hasEmpty && !db.DbFieldValues.Any(fv => fv.RecordId == r.Id && fv.FieldId == fid && fv.Value != "")));
            }
        }

        return q.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToList();
    }

    /// <summary>
    /// Значения одной колонки по всем записям коллекции (id → значение) — для сортировки
    /// отфильтрованного списка в UI тем же компаратором, что и раньше. Грузит ровно одну
    /// колонку, а не все поля (в отличие от GetFieldValuesByCollection).
    /// </summary>
    public Dictionary<int, string> GetColumnValues(int collectionId, string key)
    {
        if (key == TitleKey)
            return db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId)
                     .Select(r => new { r.Id, r.Title }).ToList()
                     .ToDictionary(x => x.Id, x => x.Title ?? "");
        if (key == DescKey)
            return db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId)
                     .Select(r => new { r.Id, r.Description }).ToList()
                     .ToDictionary(x => x.Id, x => x.Description ?? "");
        if (int.TryParse(key, out var fid))
            return db.DbFieldValues.AsNoTracking()
                     .Where(fv => fv.FieldId == fid && fv.Record.CollectionId == collectionId)
                     .Select(fv => new { fv.RecordId, fv.Value }).ToList()
                     .ToDictionary(x => x.RecordId, x => x.Value);
        return new();
    }

    /// <summary>Записи по списку id в ТОМ ЖЕ порядке, что и переданный список (для постраничной
    /// подгрузки отфильтрованного/отсортированного представления). ids — одна страница (≤ PageSize).</summary>
    public List<DbRecord> GetRecordsByIdsOrdered(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return [];
        var byId = db.DbRecords.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new DbRecord
            {
                Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
                Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
                Title = r.Title, Description = r.Description,
            })
            .ToList()
            .ToDictionary(r => r.Id!.Value);

        var result = new List<DbRecord>(ids.Count);
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var r)) result.Add(r);
        return result;
    }

    /// <summary>
    /// Асинхронно (на фоновом потоке, отдельное read-only подключение) грузит страницу записей
    /// и их значения. Нужно виртуализации таблицы, чтобы подгрузка строк при скролле НЕ блокировала
    /// UI-поток (ноль микроподвисаний даже при экстремально быстром скролле). WAL → фоновое чтение
    /// не конфликтует с основным контекстом. Режим выбирается параметрами: idSlice (фильтр/сортировка),
    /// иначе keyset вперёд по (afterPos, afterId), иначе keyset назад по (beforePos, beforeId) —
    /// скролл вверх, иначе offset по skip (прыжок скроллбаром).
    /// </summary>
    public Task<(List<DbRecord> Records, Dictionary<int, Dictionary<int, string>> Values)> LoadPageAsync(
        int collectionId, bool excludeHidden, IReadOnlyList<int>? idSlice,
        int skip, int take, int? afterPos, int? afterId, int? beforePos = null, int? beforeId = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);

            var records = idSlice is not null
                ? repo.GetRecordsByIdsOrdered(idSlice)
                : afterPos is int p && afterId is int aid
                    ? repo.GetRecordsPageAfter(collectionId, p, aid, take, excludeHidden)
                    : beforePos is int bp && beforeId is int bid
                        ? repo.GetRecordsPageBefore(collectionId, bp, bid, take, excludeHidden)
                        : repo.GetRecordsPage(collectionId, skip, take, excludeHidden);

            var ids    = records.Select(r => r.Id!.Value).ToList();
            var values = repo.GetFieldValuesBatch(ids);
            return (records, values);
        });

    // ── Фильтр колонок (Excel-стиль): уникальные значения с количеством ───────
    //
    // Порядок значений — как в таблице: по первой позиции записи с этим значением
    // (MIN(Position)); «(Пусто)» всегда последним. Это нужно панели фильтра — при открытии
    // значения идут в том же порядке, что строки таблицы, а сортировку пользователь выбирает сам.

    public List<(string Value, int Count)> GetTitleCounts(int collectionId)
    {
        var groups = db.DbRecords
            .Where(r => r.CollectionId == collectionId)
            .GroupBy(r => r.Title)
            .Select(g => new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(r => r.Position) })
            .ToList();
        return OrderValueGroups(groups.Select(g => (g.Value ?? "", g.Cnt, g.FirstPos)));
    }

    public List<(string Value, int Count)> GetDescriptionCounts(int collectionId)
    {
        var groups = db.DbRecords
            .Where(r => r.CollectionId == collectionId)
            .GroupBy(r => r.Description ?? "")
            .Select(g => new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(r => r.Position) })
            .ToList();
        return OrderValueGroups(groups.Select(g => (g.Value, g.Cnt, g.FirstPos)));
    }

    /// <summary>
    /// Значения поля с количеством в порядке таблицы (по первой позиции). Записи без строки
    /// значения учитываются в пустом значении "" — в UI оно показывается как «(Пусто)» и идёт в конец.
    /// </summary>
    public List<(string Value, int Count)> GetFieldValueCounts(int collectionId, int fieldId)
    {
        var groups = db.DbFieldValues
            .Where(fv => fv.FieldId == fieldId && fv.Record.CollectionId == collectionId)
            .GroupBy(fv => fv.Value)
            .Select(g => new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(fv => fv.Record.Position) })
            .ToList();

        // Непустые — по первой позиции (порядок таблицы).
        var result = groups.Where(g => g.Value != "")
            .OrderBy(g => g.FirstPos)
            .Select(g => (Value: g.Value ?? "", Count: g.Cnt))
            .ToList();

        // «(Пусто)» = записи без строки значения + с пустым значением — последним.
        var total   = db.DbRecords.Count(r => r.CollectionId == collectionId);
        var emptied = total - groups.Where(g => g.Value != "").Sum(g => g.Cnt);
        if (emptied > 0) result.Add(("", emptied));
        return result;
    }

    // Счётчики значений колонки (Title/Desc/поле) по ключу — диспетчер для фона.
    private List<(string Value, int Count)> ColumnCounts(int collectionId, string key) =>
        key == TitleKey ? GetTitleCounts(collectionId)
        : key == DescKey ? GetDescriptionCounts(collectionId)
        : int.TryParse(key, out var fid) ? GetFieldValueCounts(collectionId, fid)
        : [];

    /// <summary>
    /// Асинхронно (на фоновом потоке, отдельное read-only подключение) считает значения колонки
    /// с количеством — чтобы окно фильтра открывалось мгновенно, а UI не подвисал на подсчёте.
    /// WAL допускает одновременное чтение, поэтому фоновое подключение не конфликтует с основным.
    /// </summary>
    public Task<List<(string Value, int Count)>> GetColumnCountsAsync(int collectionId, string key) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).ColumnCounts(collectionId, key);
        });

    // Упорядочить группы значений по первой позиции (порядок таблицы); пустое — последним.
    private static List<(string Value, int Count)> OrderValueGroups(
        IEnumerable<(string Value, int Count, int FirstPos)> groups)
    {
        var list   = groups.ToList();
        var result = list.Where(g => g.Value != "")
            .OrderBy(g => g.FirstPos)
            .Select(g => (g.Value, g.Count))
            .ToList();
        var empty = list.Where(g => g.Value == "").Sum(g => g.Count);
        if (empty > 0) result.Add(("", empty));
        return result;
    }

    // Канон хранения для поля «Число»: приводит десятичный разделитель значения к выбранному в
    // ConfigJson поля. Для не-числовых полей и нечисловых значений — возвращает как есть.
    private string NormalizeNumberValueForField(int fieldId, string value)
    {
        if (value.Length == 0) return value;
        var f = db.DbFields.AsNoTracking().Where(x => x.Id == fieldId)
            .Select(x => new { x.FieldType, x.ConfigJson }).FirstOrDefault();
        if (f is null || f.FieldType != "number") return value;
        return NumberFormatValue.NormalizeStorage(value, NumberFormatValue.FromConfig(f.ConfigJson).DecimalSeparator);
    }

    /// <summary>Записать значение ячейки. Возвращает побочные изменения авто-привязки файлов
    /// (пересчитанные файл-столбцы этой записи: FieldId → путь/"") — UI применяет их к строке,
    /// чтобы файл-ячейка обновилась мгновенно. Обычно пусто.</summary>
    public IReadOnlyList<(int FieldId, string Value)> SetFieldValue(int recordId, int fieldId, string value)
    {
        Demand(PermAction.EditRecord, CollectionOfRecord(recordId));

        // Число: приводим десятичный разделитель к выбранному в свойстве (канон хранения).
        // Нечисловое значение и не-числовые поля остаются как есть.
        value = NormalizeNumberValueForField(fieldId, value);

        var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == fieldId);

        // Реальное изменение значения (правка ячейки — ручное действие, но сохранение записи
        // прогоняет ВСЕ поля через этот метод): им гейтим и журнал, и аудит «последней правки».
        var changed = fv is null ? value.Length > 0 : fv.Value != value;

        // Журнал — только при реальном изменении значения.
        if (changed)
        {
            var rec = db.DbRecords.AsNoTracking()
                .Where(r => r.Id == recordId)
                .Select(r => new { r.Title, r.CollectionId })
                .FirstOrDefault();
            if (rec is not null)
            {
                // «до → после»: имя свойства + компактный дифф значения (старое уже в fv).
                // Побуквенное сохранение панели записи — сливаем в одну строку журнала.
                var key = (Kind: "field", RecordId: recordId, SubId: fieldId);
                var old = BeginEditMerge(key, fv?.Value ?? "");
                if (old != value)
                {
                    var f = db.DbFields.AsNoTracking()
                        .Where(x => x.Id == fieldId).Select(x => new { x.Name, x.FieldType }).FirstOrDefault();
                    var fieldName = f?.Name ?? "";
                    // «Файл»: в журнале показываем имя файла, а не абсолютный путь (как в ячейке
                    // и фильтре). Слияние правок идёт по сырым old/value — свёртка только для показа.
                    var (detailOld, detailNew) = f?.FieldType == "file"
                        ? (FileValue.DisplayName(old), FileValue.DisplayName(value))
                        : (old, value);
                    EndEditMerge(key, Log("update", "record", rec.Title, CollectionNameOf(rec.CollectionId),
                                          detail: ChangeDetail.Value(rec.Title, fieldName, detailOld, detailNew)),
                                 old);
                }
            }
        }

        // Многопользовательский режим: между нашей проверкой и записью ту же ячейку мог
        // изменить другой пользователь (другой процесс). Политика — «последний побеждает»:
        //  • гонка на ПЕРВОЕ значение (оба увидели «строки нет», оба INSERT) — проигравший
        //    ловит PK-конфликт (SQLITE_CONSTRAINT=19 / PG unique_violation 23505)
        //    и превращает вставку в UPDATE;
        //  • строку/запись успели удалить — UPDATE попадает в 0 строк
        //    (DbUpdateConcurrencyException): значение не воскрешаем, синхронизация вскоре
        //    уберёт запись и из нашего UI.
        // Оба случая воспроизведены стендом Tests/MultiUserHarness (--contend).
        if (fv is null)
        {
            var added = new DbFieldValueEntity { RecordId = recordId, FieldId = fieldId, Value = value };
            db.DbFieldValues.Add(added);
            try { db.SaveChanges(); }
            catch (DbUpdateException ex) when (ex.InnerException
                is SqliteException { SqliteErrorCode: 19 }
                or Npgsql.PostgresException { SqlState: "23505" })
            {
                db.Entry(added).State = EntityState.Detached;
                // Идентификаторы в кавычках нижним регистром: SQLite матчит их без учёта
                // регистра, PG-модель и так lowercase — одна строка на оба провайдера.
                db.Database.ExecuteSqlRaw(
                    "UPDATE \"db_field_values\" SET \"value\" = {0} WHERE \"recordid\" = {1} AND \"fieldid\" = {2}",
                    value, recordId, fieldId);
                db.SaveChanges();   // дожать строку журнала, не сохранённую упавшим SaveChanges
            }
        }
        else
        {
            fv.Value = value;
            try { db.SaveChanges(); }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(fv).State = EntityState.Detached;
                db.SaveChanges();   // дожать строку журнала
            }
        }

        // Аудит «последней правки» записи (для типов «Дата/Изменение …») — только при реальном
        // изменении значения. Правим db_records сырым SQL (кавычки-lowercase матчатся обоими
        // провайдерами, как в conflict-ветке выше), т.к. сущность записи здесь не грузится.
        if (changed)
        {
            var now    = DateTime.Now.ToString("o");
            var author = CurrentUser.DisplayName;
            db.Database.ExecuteSqlRaw(
                "UPDATE \"db_records\" SET \"updatedat\" = {0}, \"updatedby\" = {1} WHERE \"id\" = {2}",
                now, author, recordId);
            // Если запись уже в трекере (её грузили через GetRecord/Find) — синхронизируем, иначе
            // сырой UPDATE прошёл бы мимо кеша и GetRecord вернул бы старое «изменено».
            if (db.DbRecords.Local.FirstOrDefault(x => x.Id == recordId) is { } trackedRec)
            {
                trackedRec.UpdatedAt = now;
                trackedRec.UpdatedBy = author;
            }
        }

        // Поддерживаем индексную проекцию календаря (для видимых полей-дат).
        SyncCalendarIndexValue(recordId, fieldId, value);

        // Реактивная авто-привязка: если правили текст-столбец, служащий источником авто-file
        // поля — пересчитать файл для этой записи. Возвращаем побочные изменения (fieldId → новое
        // значение файла), чтобы UI мгновенно обновил именно файл-ячейку этой строки.
        return changed ? ReactToSourceEditForAutoFile(recordId, fieldId) : [];
    }

    // Если правили текст-поле editedFieldId, служащее источником авто-file полей — пересчитать их.
    private List<(int FieldId, string Value)> ReactToSourceEditForAutoFile(int recordId, int editedFieldId) =>
        ReactToSourceEdit(recordId, rule => rule.SourceFieldId == editedFieldId);

    // Пересчитать авто-file поля записи, чей источник подходит под <paramref name="isSource"/>
    // (правка текст-столбца-источника, «Названия» или «Описания»). Возвращает список изменённых
    // (FieldId файл-поля, новое значение — путь или "" при отсутствии совпадения) — для мгновенного
    // обновления файл-ячеек в UI без перезагрузки.
    private List<(int FieldId, string Value)> ReactToSourceEdit(int recordId, Func<FileAutoRule, bool> isSource)
    {
        var changes = new List<(int FieldId, string Value)>();
        var collectionId = CollectionOfRecord(recordId);
        if (collectionId is null) return changes;
        var fileFields = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && f.FieldType == "file")
            .Select(f => new { f.Id, f.ConfigJson }).ToList();
        foreach (var ff in fileFields)
        {
            var rule = FileAutoRule.FromConfig(ff.ConfigJson);
            if (rule is null || !isSource(rule)) continue;
            var matched = MatchSingle(rule, ReadSourceValue(rule, recordId));
            SetAutoFileValue(ff.Id, recordId, matched);
            changes.Add((ff.Id, matched));
        }
        return changes;
    }

    /// <summary>
    /// Включает/выключает показ поля-даты в календаре (флаг <c>IsCalStart</c>). Несколько
    /// полей-дат коллекции могут быть включены одновременно — каждое даёт свои события.
    /// Видимость — ЧИСТЫЙ ФИЛЬТР ЧТЕНИЯ (HiddenFieldsClause во всех читателях calendar_index),
    /// БЕЗ пересборки проекции: прежний RebuildCalendarIndexForField удалял/заливал строки поля
    /// синхронно на UI-потоке — на 10 млн записей клик чекбокса вешал приложение на минуты.
    /// Проекция всегда полная (SyncCalendarIndexValue/FillCalendarIndex пишут ВСЕ date-поля).
    /// Возвращает true, если при ВКЛЮЧЕНИИ проекция поля пуста при непустых значениях —
    /// наследие старого поведения (строки удалила прошлая версия): вызывающий должен один раз
    /// залить её фоном (<see cref="FillCalendarIndexForFieldAsync"/> с окном прогресса).
    /// </summary>
    public bool SetDateFieldVisible(int fieldId, bool visible)
    {
        Demand(PermAction.EditField, CollectionOfField(fieldId));
        var field = db.DbFields.Find(fieldId);
        if (field is null || field.FieldType != "date" || field.IsCalStart == visible) return false;
        field.IsCalStart = visible;
        db.SaveChanges();

        if (!visible) return false;
        if (db.CalendarIndex.Any(x => x.FieldId == fieldId)) return false;   // проекция уже есть
        return db.DbFieldValues.Any(v => v.FieldId == fieldId && v.Value != "");
    }

    /// <summary>Число непустых значений поля — для окна прогресса заливки проекции.</summary>
    public Task<int> CountFieldValuesAsync(int fieldId) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return ctx.DbFieldValues.Count(v => v.FieldId == fieldId && v.Value != "");
        });

    /// <summary>Разовая ФОНОВАЯ заливка проекции поля (изолированное подключение) — включение
    /// поля, чью проекцию удалила старая версия. Прогресс — число обработанных значений.</summary>
    public Task FillCalendarIndexForFieldAsync(int fieldId, IProgress<int>? progress = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyBulkPerformancePragmas();
            new ConstructorRepository(ctx).FillCalendarIndex(q => q.Where(v => v.FieldId == fieldId), progress);
        });

    // ── UI настройки ──────────────────────────────────────────────────────────

    public (bool ColResize, bool RowResize, string ViewMode) GetUiSettings(int collectionId)
    {
        var s = db.UiCollectionSettings.Find(collectionId);
        // Нет своей записи у коллекции → вид по умолчанию берём из настроек (Оформление).
        // Свой сохранённый вид коллекции (переключали вручную) имеет приоритет.
        return s is null
            ? (false, false, Utils.AppSettings.LoadConstructorView())
            : (s.ColResize, s.RowResize, s.ViewMode);
    }

    public void SaveUiSettings(int collectionId, bool colResize, bool rowResize, string viewMode)
    {
        var s = db.UiCollectionSettings.Find(collectionId);
        if (s is null)
        {
            db.UiCollectionSettings.Add(new UiCollectionSettingEntity
            {
                CollectionId = collectionId, ColResize = colResize,
                RowResize = rowResize, ViewMode = viewMode,
            });
        }
        else
        {
            s.ColResize = colResize;
            s.RowResize = rowResize;
            s.ViewMode  = viewMode;
        }
        db.SaveChanges();
    }

    // Строка column_meta живёт, пока задан хоть один аспект (порядок/видимость/подпись);
    // сброс последнего удаляет её, чтобы «отсутствие строки = дефолт» оставалось верным
    // и таблица не копила пустых строк.
    private void RemoveColumnMetaIfEmpty(ColumnMetaEntity m)
    {
        if (m.Position is null && m.Visible is null && m.Level is null && m.DisplayName is null)
            db.ColumnMeta.Remove(m);
    }

    // Подписи системных колонок «Название»/«Описание» (только заданные пользователем).
    // Ключ — TableRowVm.TitleKey / DescKey; значение — отображаемое имя.
    public Dictionary<string, string> GetSystemFieldNames(int collectionId) =>
        db.ColumnMeta
          .Where(m => m.CollectionId == collectionId && m.DisplayName != null)
          .ToDictionary(m => m.Key, m => m.DisplayName!);

    // Upsert подписи. Пустое имя или совпадение с дефолтом — удаляем строку
    // (колонка возвращается к имени по умолчанию).
    public void SetSystemFieldName(int collectionId, string key, string? displayName, string defaultName)
    {
        Demand(PermAction.EditField, collectionId);
        SetSystemFieldNameCore(collectionId, key, displayName, defaultName);
    }

    private void SetSystemFieldNameCore(int collectionId, string key, string? displayName, string defaultName)
    {
        var trimmed = displayName?.Trim();
        var reset   = string.IsNullOrEmpty(trimmed) || trimmed == defaultName;

        var row = db.ColumnMeta.FirstOrDefault(m => m.CollectionId == collectionId && m.Key == key);

        if (reset)
        {
            if (row is not null)
            {
                row.DisplayName = null;
                RemoveColumnMetaIfEmpty(row);
            }
        }
        else if (row is null)
        {
            db.ColumnMeta.Add(new ColumnMetaEntity
            {
                CollectionId = collectionId, Key = key, DisplayName = trimmed,
            });
        }
        else
        {
            row.DisplayName = trimmed;
        }

        db.SaveChanges();
    }

    // ── Порядок колонок (единый для Title/Desc и пользовательских) ─────────────

    // Сохранённый порядок (key -> position); пусто, если в коллекции ещё не двигали.
    public Dictionary<string, int> GetColumnOrder(int collectionId) =>
        db.ColumnMeta
          .Where(m => m.CollectionId == collectionId && m.Position != null)
          .ToDictionary(m => m.Key, m => m.Position!.Value);

    // Перезаписать аспект «порядок» в column_meta из заданной последовательности ключей:
    // позиции устаревших ключей сбрасываются (строки-сироты без других аспектов удаляются),
    // остальные аспекты (видимость/уровень, подпись) не трогаем.
    private void WriteColumnOrder(int collectionId, IReadOnlyList<string> orderedKeys)
    {
        var rows  = db.ColumnMeta.Where(m => m.CollectionId == collectionId).ToList();
        var byKey = rows.ToDictionary(m => m.Key);
        var keep  = orderedKeys.ToHashSet();
        foreach (var m in rows)
            if (!keep.Contains(m.Key))
            {
                m.Position = null;
                RemoveColumnMetaIfEmpty(m);
            }
        for (var i = 0; i < orderedKeys.Count; i++)
        {
            if (byKey.TryGetValue(orderedKeys[i], out var m)) m.Position = (i + 1) * 1000;
            else db.ColumnMeta.Add(new ColumnMetaEntity
            {
                CollectionId = collectionId, Key = orderedKeys[i], Position = (i + 1) * 1000,
            });
        }
        db.SaveChanges();
    }

    // Переместить колонку key на одну позицию влево/вправо в общем ряду движимых
    // колонок. movableKeys — текущий порядок всех движимых колонок (как их строит
    // TableView); нужен для ленивой материализации дефолтного порядка.
    public void MoveColumn(int collectionId, string key, bool left, IReadOnlyList<string> movableKeys)
    {
        // Синхронизируем порядок с актуальным набором колонок (movableKeys): сбрасываем
        // устаревшие ключи (поля могли быть удалены) и перезаписываем порядок целиком в
        // текущей раскладке. Это убирает рассинхрон, из-за которого своп не находил ключ
        // и молча ничего не делал.
        WriteColumnOrder(collectionId, movableKeys);

        var rows = db.ColumnMeta
            .Where(m => m.CollectionId == collectionId && m.Position != null)
            .OrderBy(m => m.Position)
            .ToList();

        var idx = rows.FindIndex(o => o.Key == key);
        if (idx < 0) return;

        var swapIdx = left ? idx - 1 : idx + 1;
        if (swapIdx < 0 || swapIdx >= rows.Count) return;   // крайняя колонка — no-op

        (rows[idx].Position, rows[swapIdx].Position) = (rows[swapIdx].Position, rows[idx].Position);
        db.SaveChanges();
    }

    // Поставить колонку newKey сразу после afterKey в общем порядке. movableKeys —
    // актуальный порядок колонок ДО вставки (newKey там в конце, как создаёт AddField).
    // Перезаписываем порядок из movableKeys, затем перемещаем newKey за afterKey.
    public void MoveColumnAfter(int collectionId, string newKey, string afterKey, IReadOnlyList<string> movableKeys)
    {
        // Целевой порядок: как movableKeys, но newKey вынут и вставлен после afterKey.
        var order = movableKeys.Where(k => k != newKey).ToList();
        var at    = order.IndexOf(afterKey);
        if (at < 0) order.Add(newKey);              // afterKey не найден — оставляем в конце
        else        order.Insert(at + 1, newKey);

        WriteColumnOrder(collectionId, order);
    }

    // Перезаписать порядок колонок целиком из заданной последовательности ключей
    // (как они идут слева-направо). Используется окном «Отображение» при сохранении.
    public void SaveColumnOrder(int collectionId, IReadOnlyList<string> orderedKeys) =>
        WriteColumnOrder(collectionId, orderedKeys);

    public Dictionary<int, int> GetColWidths(int collectionId) =>
        db.UiColWidths
          .Where(w => w.CollectionId == collectionId)
          .ToDictionary(w => w.FieldId, w => w.Width);

    public void SaveColWidths(int collectionId, Dictionary<int, int> widths)
    {
        db.UiColWidths.RemoveRange(db.UiColWidths.Where(w => w.CollectionId == collectionId));
        db.UiColWidths.AddRange(widths.Select(kv => new UiColWidthEntity
        {
            CollectionId = collectionId, FieldId = kv.Key, Width = kv.Value,
        }));
        db.SaveChanges();
    }

    // ── Отображение колонок (видимость + уровень; окно «Отображение») ──────────

    // key → (Visible, Level). Только сохранённые строки; отсутствие ключа = (видима, 1).
    public Dictionary<string, (bool Visible, int Level)> GetColumnDisplay(int collectionId) =>
        db.ColumnMeta
          .Where(m => m.CollectionId == collectionId && m.Visible != null)
          .ToDictionary(m => m.Key, m => (m.Visible!.Value, m.Level ?? 1));

    // Перезаписываем аспект «отображение» (Visible/Level) целиком. Дефолт (видима, уровень 1)
    // не храним — так таблица не разрастается, а новые колонки по умолчанию видимы. Прочие
    // аспекты (порядок, подпись) не трогаем; строки-сироты без аспектов удаляются.
    public void SaveColumnDisplay(int collectionId, IEnumerable<(string Key, bool Visible, int Level)> entries)
    {
        var rows  = db.ColumnMeta.Where(m => m.CollectionId == collectionId).ToList();
        var byKey = rows.ToDictionary(m => m.Key);
        foreach (var m in rows) { m.Visible = null; m.Level = null; }
        foreach (var (key, visible, level) in entries)
        {
            if (visible && level <= 1) continue;   // дефолт — не храним
            if (byKey.TryGetValue(key, out var m))
            {
                m.Visible = visible;
                m.Level   = Math.Max(1, level);
            }
            else db.ColumnMeta.Add(new ColumnMetaEntity
            {
                CollectionId = collectionId, Key = key, Visible = visible, Level = Math.Max(1, level),
            });
        }
        foreach (var m in rows) RemoveColumnMetaIfEmpty(m);
        db.SaveChanges();
    }

    // ── Отображение записей (видимость + уровень; окно «Отображение записей») ────

    // recordId → (Visible, Level). Только сохранённые строки; отсутствие ключа = (видима, 1).
    public Dictionary<int, (bool Visible, int Level)> GetRecordDisplay(int collectionId) =>
        db.RecordDisplay
          .Where(d => d.CollectionId == collectionId)
          .ToDictionary(d => d.RecordId, d => (d.Visible, d.Level));

    // Перезаписываем отображение записей целиком. Дефолт (видима, уровень 1) не храним —
    // так таблица не разрастается, а новые записи по умолчанию видимы.
    public void SaveRecordDisplay(int collectionId, IEnumerable<(int RecordId, bool Visible, int Level)> entries)
    {
        db.RecordDisplay.RemoveRange(db.RecordDisplay.Where(d => d.CollectionId == collectionId));
        foreach (var (recordId, visible, level) in entries)
        {
            if (visible && level <= 1) continue;   // дефолт — не храним
            db.RecordDisplay.Add(new RecordDisplayEntity
            {
                CollectionId = collectionId, RecordId = recordId, Visible = visible, Level = Math.Max(1, level),
            });
        }
        db.SaveChanges();
    }

    // Пакетная перестановка порядка записей (окно «Отображение записей»). Переписывает
    // Position по порядку списка через bulk-SQL (временная таблица id→Pos + один UPDATE FROM),
    // а не загрузкой всех записей в EF — иначе на коллекциях в сотни тысяч строк зависание.
    public void SaveRecordOrder(int collectionId, IReadOnlyList<int> orderedRecordIds)
    {
        if (orderedRecordIds.Count == 0) return;

        var conn   = db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) conn.Open();
        using (var tx = conn.BeginTransaction())
        {
            void Exec(string sql, System.Data.Common.DbTransaction t)
            {
                using var c = conn.CreateCommand();
                c.Transaction = t; c.CommandText = sql; c.ExecuteNonQuery();
            }

            Exec("CREATE TEMP TABLE _ord (Id INTEGER PRIMARY KEY, Pos INTEGER)", tx);

            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO _ord (Id, Pos) VALUES (@id, @pos)";
                var pId  = ins.CreateParameter(); pId.ParameterName  = "@id";  ins.Parameters.Add(pId);
                var pPos = ins.CreateParameter(); pPos.ParameterName = "@pos"; ins.Parameters.Add(pPos);
                for (var i = 0; i < orderedRecordIds.Count; i++)
                {
                    pId.Value  = orderedRecordIds[i];
                    pPos.Value = (i + 1) * 1000;
                    ins.ExecuteNonQuery();
                }
            }

            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText =
                    "UPDATE db_records SET Position = (SELECT Pos FROM _ord WHERE _ord.Id = db_records.Id) " +
                    "WHERE CollectionId = @c AND Id IN (SELECT Id FROM _ord)";
                var pC = upd.CreateParameter(); pC.ParameterName = "@c"; pC.Value = collectionId; upd.Parameters.Add(pC);
                upd.ExecuteNonQuery();
            }

            Exec("DROP TABLE _ord", tx);
            tx.Commit();
        }
        if (!wasOpen) conn.Close();

        // Освежаем позиции у отслеживаемых записей этой коллекции (правка шла мимо EF).
        foreach (var e in db.ChangeTracker.Entries<DbRecordEntity>().ToList())
            if (e.Entity.CollectionId == collectionId) e.Reload();
    }

    public Dictionary<int, int> GetRowHeights(int collectionId) =>
        db.UiRowHeights
          .Where(h => h.CollectionId == collectionId)
          .ToDictionary(h => h.RecordId, h => h.Height);

    public void SaveRowHeights(int collectionId, Dictionary<int, int> heights)
    {
        db.UiRowHeights.RemoveRange(db.UiRowHeights.Where(h => h.CollectionId == collectionId));
        db.UiRowHeights.AddRange(heights.Select(kv => new UiRowHeightEntity
        {
            CollectionId = collectionId, RecordId = kv.Key, Height = kv.Value,
        }));
        db.SaveChanges();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ─── CalendarRecord API (для календарных представлений) ───────────────────
    // ══════════════════════════════════════════════════════════════════════════

    public CalendarRecord AddCalendarRecord(CalendarRecord r)
    {
        Demand(PermAction.CreateRecord, r.CollectionId);
        var colId = r.CollectionId ?? GetOrCreateDefaultCollection();
        var dateFieldId = ResolveCalendarDateField(colId, r.FieldId);

        var maxPos = db.DbRecords
            .Where(rec => rec.CollectionId == colId)
            .Select(rec => (int?)rec.Position)
            .Max() ?? 0;

        var rec = new DbRecordEntity
        {
            CollectionId = colId,
            StartDt      = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o"),
            EndDt        = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o"),
            AllDay       = r.AllDay,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = maxPos + 1000,
            Title        = r.Title,
            Description  = string.IsNullOrEmpty(r.Description) ? null : r.Description,
        };
        db.DbRecords.Add(rec);
        Log("create", "record", r.Title, CollectionNameOf(colId));
        db.SaveChanges();

        SetCalendarDateValue(rec.Id, dateFieldId, r);

        r.Id           = rec.Id;
        r.CollectionId = colId;
        r.FieldId      = dateFieldId;
        return r;
    }

    /// <summary>Массовый дубль календарных записей на ОТДЕЛЬНОМ подключении (фон + прогресс) —
    /// см. <see cref="DuplicateCalendarRecordsData"/>. Возвращает копии с проставленными Id/FieldId.</summary>
    public List<CalendarRecord> DuplicateCalendarRecordsIsolated(
        IReadOnlyList<CalendarRecord> records, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return new ConstructorRepository(ctx).DuplicateCalendarRecordsData(records, progress);
    }

    /// <summary>Массовый дубль календарных записей — та же «календарная» копия, что и
    /// <see cref="AddCalendarRecord"/> (шапка + поле-дата + calendar_index; остальные свойства
    /// НЕ копируются — как у одиночного дубля календаря), но ОДНОЙ транзакцией с батчевыми
    /// SaveChanges: цикл AddCalendarRecord с двумя SaveChanges и MAX(Position) на КАЖДУЮ запись
    /// вешал UI на сотнях/тысячах. Позиция и поле-дата считаются один раз на коллекцию.
    /// Возвращает копии (Id/FieldId проставлены) — для выделения, дельты Журнала и якоря.</summary>
    public List<CalendarRecord> DuplicateCalendarRecordsData(
        IReadOnlyList<CalendarRecord> records, IProgress<int>? progress = null, int batchSize = 500)
    {
        var copies = new List<CalendarRecord>(records.Count);
        if (records.Count == 0) return copies;

        using var tx = db.Database.BeginTransaction();
        var maxPos    = new Dictionary<int, int>();          // CollectionId → последняя занятая позиция
        var dateField = new Dictionary<(int Col, int Pref), int>();   // поле-дата коллекции
        var done = 0;

        foreach (var chunk in records.Chunk(batchSize))
        {
            var pairs = new List<(CalendarRecord Src, DbRecordEntity Rec, int FieldId)>(chunk.Length);
            foreach (var r in chunk)
            {
                var colId = r.CollectionId ?? GetOrCreateDefaultCollection();
                if (!maxPos.TryGetValue(colId, out var pos))
                    pos = db.DbRecords.Where(x => x.CollectionId == colId)
                                      .Select(x => (int?)x.Position).Max() ?? 0;
                pos += 1000;
                maxPos[colId] = pos;

                if (!dateField.TryGetValue((colId, r.FieldId), out var fid))
                    dateField[(colId, r.FieldId)] = fid = ResolveCalendarDateField(colId, r.FieldId);

                var rec = new DbRecordEntity
                {
                    CollectionId = colId,
                    StartDt      = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o"),
                    EndDt        = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o"),
                    AllDay       = r.AllDay,
                    CreatedAt    = DateTime.Now.ToString("o"),
                    Position     = pos,
                    Title        = r.Title,
                    Description  = string.IsNullOrEmpty(r.Description) ? null : r.Description,
                };
                db.DbRecords.Add(rec);
                pairs.Add((r, rec, fid));
            }
            db.SaveChanges();   // id копий появились — дальше значения и индекс

            foreach (var (src, rec, fid) in pairs)
                db.DbFieldValues.Add(new DbFieldValueEntity
                {
                    RecordId = rec.Id,
                    FieldId  = fid,
                    Value    = new DateValue(src.StartDt, src.EndDt, src.AllDay).ToStorage(),
                });
            db.SaveChanges();

            foreach (var (src, rec, fid) in pairs)
            {
                SyncCalendarIndexValue(rec.Id, fid,
                    new DateValue(src.StartDt, src.EndDt, src.AllDay).ToStorage());
                copies.Add(new CalendarRecord
                {
                    DbId = src.DbId, Id = rec.Id, CollectionId = rec.CollectionId, FieldId = fid,
                    Title = src.Title, Description = src.Description,
                    StartDt = src.StartDt, EndDt = src.EndDt, AllDay = src.AllDay, Color = src.Color,
                });
            }
            done += chunk.Length;
            progress?.Report(done);
        }
        tx.Commit();
        return copies;
    }

    public void UpdateCalendarRecord(CalendarRecord r)
    {
        if (r.Id is null) return;
        Demand(PermAction.EditRecord, CollectionOfRecord(r.Id.Value));

        var rec = db.DbRecords.Find(r.Id.Value);
        if (rec is null) return;

        var oldColId = rec.CollectionId;
        var newColId = r.CollectionId ?? oldColId;

        if (oldColId != newColId)
        {
            // Смена коллекции = перенос: создаём новую запись в целевой коллекции (правленый
            // header + выбранное поле-дата + индекс), затем грамотно переносим остальные значения
            // свойств (по имени, с приведением типов) — иначе они терялись. Недостающие в цели
            // свойства НЕ создаём (окно календаря визуально не меняем). Поле-дату исключаем, чтобы
            // не затереть отредактированную дату.
            var oldId = rec.Id;
            r.CollectionId = newColId;
            r.Id = null;
            AddCalendarRecord(r);
            CopyFieldValuesByName(this, oldId, oldColId, r.Id!.Value, newColId,
                                  createMissingFields: false, excludeTargetFieldId: r.FieldId);
            DeleteRecord(oldId);
            return;
        }

        rec.StartDt     = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o");
        rec.EndDt       = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o");
        rec.AllDay      = r.AllDay;
        rec.Title       = r.Title;
        rec.Description = string.IsNullOrEmpty(r.Description) ? null : r.Description;
        Log("update", "record", r.Title, CollectionNameOf(newColId));
        db.SaveChanges();

        var dateFieldId = ResolveCalendarDateField(newColId, r.FieldId);
        SetCalendarDateValue(rec.Id, dateFieldId, r);
        r.FieldId = dateFieldId;
    }

    public void DeleteCalendarRecord(int id)
    {
        Demand(PermAction.DeleteRecord, CollectionOfRecord(id));
        var rec = db.DbRecords.Find(id);
        if (rec is not null)
        {
            Log("delete", "record", rec.Title, CollectionNameOf(rec.CollectionId));
            db.DbRecords.Remove(rec);
            db.SaveChanges();
        }
    }

    public List<CalendarRecord> GetCalendarRecordsByRange(DateTime start, DateTime end, ISet<int>? activeIds = null) =>
        ReadCalendarEvents(start, end, activeIds);

    /// <summary>То же, что <see cref="GetCalendarRecordsByRange"/>, но на ОТДЕЛЬНОМ свежем
    /// подключении. Для фонового тика службы уведомлений: чтение наступивших событий не должно
    /// идти на UI-потоке — на больших БД скан 62-дневного буфера calendar_index стоит десятки мс
    /// и «морозит» интерфейс. Общий контекст однопоточный, поэтому читаем через
    /// <c>new AppDbContext</c> (та же схема, что <see cref="LoadTasksAsync"/>). Вызывать из
    /// фонового потока; на шаред-репо трогается только неизменяемый <c>db.DbPath</c>.</summary>
    public List<CalendarRecord> GetCalendarRecordsByRangeIsolated(DateTime start, DateTime end, ISet<int>? activeIds = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        return new ConstructorRepository(ctx).GetCalendarRecordsByRange(start, end, activeIds);
    }

    public int CountCalendarRecords() => db.CalendarIndex.Count();

    // ── Пагинация «Расписания» (ленивая подгрузка списка одного дня) ─────────────
    // «Расписание» показывает события, НАЧИНАющиеся в этом дне (StartDt.Date == день),
    // в порядке: all-day первыми, затем по времени начала. Курсор keyset — по этому же
    // порядку (AllDay DESC, StartIso, RecordId, FieldId), чтобы догрузка была O(limit).

    // Фильтр видимости для ВСЕХ читающих запросов calendar_index:
    //  • активные коллекции: null — без фильтра (все), ПУСТОЙ набор — НИ ОДНОЙ («AND 1=0»).
    //    Раньше пустой набор «отключал» фильтр, и снятие всех галок БД в сайдбаре показывало
    //    все события вместо ни одного — галка и события «работали не сообща»;
    //  • скрытые поля-даты (IsCalStart=0): видимость поля — теперь ЧИСТЫЙ ФИЛЬТР ЧТЕНИЯ,
    //    проекция не пересобирается (см. SetDateFieldVisible). Обычно скрытых нет — фильтр
    //    не добавляется вовсе; db_fields крошечная, её запрос копеечный.
    private string VisibilityFilter(ISet<int>? activeIds, string prefix = "")
    {
        // Коллекции, у которых ВСЕ поля-даты скрыты, отсекаются целиком на уровне CollectionId:
        // фильтр по FieldId — residual (не ложится на индекс), и «галка включена, но поля
        // скрыты» платила бы полные сканы диапазона (день-гигант — секунды «Загрузки…»)
        // ради заведомо пустого результата.
        var dateFields = db.DbFields.Where(f => f.FieldType == "date")
            .Select(f => new { f.Id, f.CollectionId, f.IsCalStart }).ToList();
        var fullyHidden = dateFields.GroupBy(f => f.CollectionId)
            .Where(g => g.All(x => !x.IsCalStart)).Select(g => g.Key).ToHashSet();

        List<int> cols;
        if (activeIds is null)
        {
            // Без фильтра коллекций; если полностью скрытых нет — хватает residual по полям.
            if (fullyHidden.Count == 0)
            {
                var allHidden = dateFields.Where(x => !x.IsCalStart).Select(x => x.Id).ToList();
                return allHidden.Count == 0 ? "" : $" AND {prefix}FieldId NOT IN ({string.Join(",", allHidden)})";
            }
            cols = db.DbCollections.Select(c => c.Id).Where(id => !fullyHidden.Contains(id)).ToList();
        }
        else
            cols = activeIds.Where(id => !fullyHidden.Contains(id)).ToList();

        // Пустой набор (все галки сняты / всё скрыто) — НИ ОДНОЙ, а не «без фильтра».
        if (cols.Count == 0) return " AND 1=0";

        var f = $" AND {prefix}CollectionId IN ({string.Join(",", cols)})";
        var restHidden = dateFields
            .Where(x => !x.IsCalStart && !fullyHidden.Contains(x.CollectionId))
            .Select(x => x.Id).ToList();
        if (restHidden.Count > 0) f += $" AND {prefix}FieldId NOT IN ({string.Join(",", restHidden)})";
        return f;
    }

    // Часть фильтра по скрытым полям-датам — для запросов, где коллекция уже зафиксирована
    // (per-collection проходы GetCalendarRecordsByRangeCapped).
    private string HiddenFieldsClause(string prefix = "")
    {
        var hidden = db.DbFields
            .Where(f => f.FieldType == "date" && !f.IsCalStart)
            .Select(f => f.Id)
            .ToList();
        return hidden.Count == 0 ? "" : $" AND {prefix}FieldId NOT IN ({string.Join(",", hidden)})";
    }

    // Коллекции, у которых ВСЕ поля-даты скрыты, — per-collection проходы пропускают их целиком.
    private HashSet<int> FullyHiddenDateCollections() =>
        db.DbFields.Where(f => f.FieldType == "date")
            .GroupBy(f => f.CollectionId)
            .Where(g => g.All(x => !x.IsCalStart))
            .Select(g => g.Key)
            .ToHashSet();

    /// <summary>Число событий, начинающихся в дне [dayStart, dayEnd) (для «показано N из total»).</summary>
    public int CountCalendarDayEvents(DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds)
    {
        var s = DateTime.SpecifyKind(dayStart, DateTimeKind.Unspecified).ToString("o");
        var e = DateTime.SpecifyKind(dayEnd,   DateTimeKind.Unspecified).ToString("o");
        var col = VisibilityFilter(activeIds);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM calendar_index WHERE StartIso >= @s AND StartIso < @e{col}";
            AddParam(cmd, "@s").Value = s;
            AddParam(cmd, "@e").Value = e;
            var o = cmd.ExecuteScalar();
            return o is null or DBNull ? 0 : Convert.ToInt32(o);
        }
        finally { if (opened) conn.Close(); }
    }

    /// <summary>
    /// Число событий ПО ДНЯМ в окне [start, end): "yyyy-MM-dd" → count, только дни с событиями,
    /// по возрастанию. Для «Журнала»: полоса прокрутки на весь объём окна без материализации
    /// событий (день с миллионами строк даёт одну строку счётчика). Скан по покрывающему индексу.
    /// </summary>
    public List<(string Day, int Count)> GetCalendarDayCountsByRange(
        DateTime start, DateTime end, ISet<int>? activeIds)
    {
        var s = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
        var e = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");
        var col = VisibilityFilter(activeIds);
        var result = new List<(string, int)>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT substr(StartIso, 1, 10) AS d, COUNT(*)
                                 FROM calendar_index
                                 WHERE StartIso >= @s AND StartIso < @e{col}
                                 GROUP BY d ORDER BY d";
            AddParam(cmd, "@s").Value = s;
            AddParam(cmd, "@e").Value = e;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result.Add((rdr.GetString(0), rdr.GetInt32(1)));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Фоновая (изолированное подключение) версия <see cref="GetCalendarDayCountsByRange"/> —
    /// на большом окне GROUP BY сканирует миллионы строк индекса (секунды) и не должен трогать UI-поток.</summary>
    public Task<List<(string Day, int Count)>> LoadCalendarDayCountsAsync(
        DateTime start, DateTime end, ISet<int>? activeIds) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetCalendarDayCountsByRange(start, end, activeIds);
        });

    /// <summary>
    /// ПОСЕКУНДНАЯ гистограмма событий дня: "HH:MM:SS" → count, по возрастанию времени. Для
    /// «Журнала» на гигантских днях (миллионы событий): даёт порядок по времени БЕЗ материализации
    /// дня — карта секунд вместо самих событий. Скан индекса дня — фоново (изолированное подключение).
    /// </summary>
    public Task<List<(string Time, int Count)>> LoadCalendarDayTimeHistogramAsync(
        DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);

            var s = DateTime.SpecifyKind(dayStart, DateTimeKind.Unspecified).ToString("o");
            var e = DateTime.SpecifyKind(dayEnd,   DateTimeKind.Unspecified).ToString("o");
            var col = repo.VisibilityFilter(activeIds);   // фильтр — на ИЗОЛИРОВАННОМ контексте (мы в фоне)
            var result = new List<(string, int)>();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            // StartIso: "yyyy-MM-ddTHH:mm:ss.fffffff" → substr(...,12,8) = "HH:MM:SS".
            cmd.CommandText = $@"SELECT substr(StartIso, 12, 8) AS t, COUNT(*)
                                 FROM calendar_index
                                 WHERE StartIso >= @s AND StartIso < @e{col}
                                 GROUP BY t ORDER BY t";
            AddParam(cmd, "@s").Value = s;
            AddParam(cmd, "@e").Value = e;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result.Add((rdr.GetString(0), rdr.GetInt32(1)));
            return result;
        });

    /// <summary>
    /// Поиск событий окна по подстроке заголовка — для «Расписания»/«Журнала». Фильтр — нативным
    /// SQL LIKE (прогон миллионов строк через приложение на порядок медленнее — замерено).
    /// LIKE регистронезависим только для ASCII, поэтому ищем регистровые варианты подстроки
    /// (как ввели / нижний / ВЕРХНИЙ / С заглавной) — это покрывает практический ввод кириллицы.
    /// До <paramref name="limit"/> совпадений в хронологическом порядке. Фоново на изолированном
    /// подключении: окно с миллионами событий сканируется секунды и не трогает UI-поток.
    /// (FTS5-trigram пробовали и откатили: минуты бэкафилла при первом старте больших БД и
    /// деградация на частых подстроках — см. миграции Add/RemoveRecordsTitleFtsTrigram.)
    /// </summary>
    public Task<List<CalendarRecord>> SearchCalendarEventsAsync(
        DateTime start, DateTime end, ISet<int>? activeIds, string query, int limit) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();

            var s = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
            var e = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");
            // Фильтр — на ИЗОЛИРОВАННОМ контексте (мы в фоне, общий db трогать нельзя).
            var col = new ConstructorRepository(ctx).VisibilityFilter(activeIds, "ci.");

            // Регистровые варианты подстроки; спецсимволы LIKE экранируем.
            static string Esc(string q) =>
                q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var lower = query.ToLowerInvariant();
            var variants = new HashSet<string>(StringComparer.Ordinal)
            {
                query, lower, query.ToUpperInvariant(),
                lower.Length > 0 ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower,
            };
            var like = string.Join(" OR ", variants.Select((_, i) => $"r.Title LIKE @q{i} ESCAPE '\\'"));

            var result = new List<CalendarRecord>();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT ci.RecordId, ci.FieldId, ci.CollectionId, ci.StartIso, ci.EndIso, ci.AllDay,
                                        r.Title, r.Description, c.Name, c.Color
                                 FROM calendar_index ci
                                 JOIN db_records r ON r.Id = ci.RecordId
                                 JOIN db_collections c ON c.Id = r.CollectionId
                                 WHERE ci.StartIso >= @s AND ci.StartIso < @e{col} AND ({like})
                                 ORDER BY ci.StartIso, ci.RecordId, ci.FieldId
                                 LIMIT @lim";
            AddParam(cmd, "@s").Value = s;
            AddParam(cmd, "@e").Value = e;
            AddParam(cmd, "@lim").Value = limit;
            var vi = 0;
            foreach (var v in variants) AddParam(cmd, $"@q{vi++}").Value = $"%{Esc(v)}%";

            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                if (!DateTime.TryParse(rdr.GetString(3), out var st)) continue;
                if (!DateTime.TryParse(rdr.GetString(4), out var en)) continue;
                result.Add(new CalendarRecord
                {
                    Id = rdr.GetInt32(0), FieldId = rdr.GetInt32(1), CollectionId = rdr.GetInt32(2),
                    StartDt = st, EndDt = en, AllDay = rdr.GetInt32(5) != 0,
                    Title = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                    Description = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                    CollectionName = rdr.IsDBNull(8) ? "" : rdr.GetString(8),
                    Color = rdr.IsDBNull(9) ? "#007AFF" : rdr.GetString(9),
                });
            }
            return result;
        });

    /// <summary>
    /// Страница событий дня ОДНОЙ фазы <paramref name="allDay"/> (1 — «весь день»/многодневные,
    /// 0 — по времени), упорядоченная по StartIso ASC. Такой порядок совпадает с индексом
    /// (CollectionId, AllDay, StartIso), поэтому выборка идёт seek'ом без сортировки всего дня
    /// (иначе на дне с миллионами событий SQLite строит TEMP B-TREE и подвешивает UI на секунды).
    /// Keyset после (<paramref name="afterStartIso"/>, <paramref name="afterRecordId"/>,
    /// <paramref name="afterFieldId"/>); afterStartIso=null — с начала фазы. До <paramref name="limit"/>
    /// событий. Порядок «Расписания» (all-day первыми, затем по времени) собирает пейджер, вызывая
    /// фазу 1, затем фазу 0 (см. <see cref="CalendarDayPager"/>).
    /// </summary>
    public List<CalendarRecord> GetCalendarDayEventsPage(
        DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds,
        int allDay, string? afterStartIso, int afterRecordId, int afterFieldId, int limit)
    {
        var startIso = DateTime.SpecifyKind(dayStart, DateTimeKind.Unspecified).ToString("o");
        var endIso   = DateTime.SpecifyKind(dayEnd,   DateTimeKind.Unspecified).ToString("o");
        var col = VisibilityFilter(activeIds, "ci.");
        var keyset = afterStartIso is null ? "" :
            @" AND (ci.StartIso > @cs
                OR (ci.StartIso = @cs AND ci.RecordId > @cr)
                OR (ci.StartIso = @cs AND ci.RecordId = @cr AND ci.FieldId > @cf))";
        var sql = $@"SELECT ci.RecordId, ci.FieldId, ci.CollectionId, ci.StartIso, ci.EndIso, ci.AllDay,
                            r.Title, r.Description, c.Name, c.Color
                     FROM calendar_index ci
                     JOIN db_records r ON r.Id = ci.RecordId
                     JOIN db_collections c ON c.Id = r.CollectionId
                     WHERE ci.StartIso >= @s AND ci.StartIso < @e AND ci.AllDay = @ad{col}{keyset}
                     ORDER BY ci.StartIso, ci.RecordId, ci.FieldId
                     LIMIT @lim";

        var result = new List<CalendarRecord>(limit);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParam(cmd, "@s").Value = startIso;
            AddParam(cmd, "@e").Value = endIso;
            AddParam(cmd, "@ad").Value = allDay;
            AddParam(cmd, "@lim").Value = limit;
            if (afterStartIso is not null)
            {
                AddParam(cmd, "@cs").Value = afterStartIso;
                AddParam(cmd, "@cr").Value = afterRecordId;
                AddParam(cmd, "@cf").Value = afterFieldId;
            }
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                if (!DateTime.TryParse(rdr.GetString(3), out var st)) continue;
                if (!DateTime.TryParse(rdr.GetString(4), out var en)) continue;
                result.Add(new CalendarRecord
                {
                    Id             = rdr.GetInt32(0),
                    FieldId        = rdr.GetInt32(1),
                    CollectionId   = rdr.GetInt32(2),
                    StartDt        = st,
                    EndDt          = en,
                    AllDay         = rdr.GetInt32(5) != 0,
                    Title          = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                    Description    = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                    CollectionName = rdr.IsDBNull(8) ? "" : rdr.GetString(8),
                    Color          = rdr.IsDBNull(9) ? "#007AFF" : rdr.GetString(9),
                });
            }
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    // Число событий дня одной фазы allDay (быстро по индексу (CollectionId, AllDay, StartIso)) —
    // для стыковки offset all-day→timed и подсчёта.
    private int CountCalendarDayPhase(DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds, int allDay)
    {
        var s = DateTime.SpecifyKind(dayStart, DateTimeKind.Unspecified).ToString("o");
        var e = DateTime.SpecifyKind(dayEnd,   DateTimeKind.Unspecified).ToString("o");
        var col = VisibilityFilter(activeIds);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM calendar_index WHERE StartIso >= @s AND StartIso < @e AND AllDay = @ad{col}";
            AddParam(cmd, "@s").Value = s;
            AddParam(cmd, "@e").Value = e;
            AddParam(cmd, "@ad").Value = allDay;
            var o = cmd.ExecuteScalar();
            return o is null or DBNull ? 0 : Convert.ToInt32(o);
        }
        finally { if (opened) conn.Close(); }
    }

    /// <summary>
    /// Страница событий дня по ГЛОБАЛЬНОМУ смещению <paramref name="skip"/> в порядке «Расписания»
    /// (all-day первыми, затем timed; внутри — по StartIso). Пропуск идёт по покрывающему индексу
    /// (подзапрос LIMIT/OFFSET на calendar_index), а join к db_records/db_collections — только для
    /// строк страницы, поэтому даже прыжок в конец дня с миллионами событий — доли секунды. Нужно
    /// для произвольного доступа полосой прокрутки (перетаскивание, в т.ч. в самый низ).
    /// </summary>
    public List<CalendarRecord> GetCalendarDayEventsByOffset(
        DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds, int skip, int take)
    {
        if (take <= 0 || skip < 0) return new List<CalendarRecord>();
        var result = new List<CalendarRecord>(take);
        var allDayCount = CountCalendarDayPhase(dayStart, dayEnd, activeIds, 1);
        if (skip < allDayCount)
        {
            var n = Math.Min(take, allDayCount - skip);
            result.AddRange(DayPhaseByOffset(dayStart, dayEnd, activeIds, 1, skip, n));
            if (result.Count < take)   // хвост all-day закончился — добираем timed с начала
                result.AddRange(DayPhaseByOffset(dayStart, dayEnd, activeIds, 0, 0, take - result.Count));
        }
        else
        {
            result.AddRange(DayPhaseByOffset(dayStart, dayEnd, activeIds, 0, skip - allDayCount, take));
        }
        return result;
    }

    // Одна фаза окна дня по offset: пропуск на покрывающем индексе (подзапрос), join только к странице.
    private List<CalendarRecord> DayPhaseByOffset(DateTime dayStart, DateTime dayEnd,
                                                  ISet<int>? activeIds, int allDay, int offset, int take)
    {
        var startIso = DateTime.SpecifyKind(dayStart, DateTimeKind.Unspecified).ToString("o");
        var endIso   = DateTime.SpecifyKind(dayEnd,   DateTimeKind.Unspecified).ToString("o");
        var col = VisibilityFilter(activeIds);
        // ORDER BY (StartIso, RecordId, FieldId) — тот же полный ключ, что и в keyset-выборке
        // (GetCalendarDayEventsPage), чтобы offset-прыжки и keyset-скролл давали ОДИН порядок
        // (без дублей/пропусков на границах страниц с равным StartIso). Быстрый пропуск идёт по
        // индексу (CollectionId, AllDay, StartIso, RecordId, FieldId) без временной сортировки.
        var sql = $@"SELECT ci.RecordId, ci.FieldId, ci.CollectionId, ci.StartIso, ci.EndIso, ci.AllDay,
                            r.Title, r.Description, c.Name, c.Color
                     FROM (
                         SELECT RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay
                         FROM calendar_index
                         WHERE StartIso >= @s AND StartIso < @e AND AllDay = @ad{col}
                         ORDER BY StartIso, RecordId, FieldId
                         LIMIT @lim OFFSET @off
                     ) ci
                     JOIN db_records r ON r.Id = ci.RecordId
                     JOIN db_collections c ON c.Id = r.CollectionId
                     ORDER BY ci.StartIso, ci.RecordId, ci.FieldId";
        var result = new List<CalendarRecord>(take);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddParam(cmd, "@s").Value = startIso;
            AddParam(cmd, "@e").Value = endIso;
            AddParam(cmd, "@ad").Value = allDay;
            AddParam(cmd, "@lim").Value = take;
            AddParam(cmd, "@off").Value = offset;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                if (!DateTime.TryParse(rdr.GetString(3), out var st)) continue;
                if (!DateTime.TryParse(rdr.GetString(4), out var en)) continue;
                result.Add(new CalendarRecord
                {
                    Id = rdr.GetInt32(0), FieldId = rdr.GetInt32(1), CollectionId = rdr.GetInt32(2),
                    StartDt = st, EndDt = en, AllDay = rdr.GetInt32(5) != 0,
                    Title = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                    Description = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                    CollectionName = rdr.IsDBNull(8) ? "" : rdr.GetString(8),
                    Color = rdr.IsDBNull(9) ? "#007AFF" : rdr.GetString(9),
                });
            }
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Фоновая (изолированное подключение) загрузка страницы дня по offset — для UI без фризов.</summary>
    public Task<List<CalendarRecord>> LoadCalendarDayPageAsync(
        DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds, int skip, int take) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetCalendarDayEventsByOffset(dayStart, dayEnd, activeIds, skip, take);
        });

    /// <summary>Фоновая (изолированное подключение) keyset-страница фазы дня — для гладкой
    /// последовательной прокрутки: O(страницы), не зависит от глубины (см. GetCalendarDayEventsPage).</summary>
    public Task<List<CalendarRecord>> LoadCalendarDayPageAfterAsync(
        DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds,
        int allDay, string? afterStartIso, int afterRecordId, int afterFieldId, int limit) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).GetCalendarDayEventsPage(
                dayStart, dayEnd, activeIds, allDay, afterStartIso, afterRecordId, afterFieldId, limit);
        });

    /// <summary>Фоновый подсчёт числа событий дня (для размера полосы прокрутки) — изолированное подключение.</summary>
    public Task<int> CountCalendarDayEventsAsync(DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountCalendarDayEvents(dayStart, dayEnd, activeIds);
        });

    /// <summary>Ранг события внутри дня — число событий строго ДО него в порядке «Расписания»
    /// (all-day первыми, затем (StartIso, RecordId, FieldId)); изолированное подключение.
    /// Нужен точечной правке offset-режима: вставить строку созданной/изменённой записи в кеш
    /// гигантского дня на точное место без перечитывания дня (COUNT по покрывающему индексу).</summary>
    public Task<int> CountCalendarDayEventsBeforeAsync(DateTime dayStart, DateTime dayEnd,
        ISet<int>? activeIds, bool allDay, DateTime start, int recordId, int fieldId) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx);
            var before = allDay ? 0 : repo.CountCalendarDayPhase(dayStart, dayEnd, activeIds, 1);
            return before + repo.CountCalendarDayPhaseBefore(
                dayStart, dayEnd, activeIds, allDay ? 1 : 0, start, recordId, fieldId);
        });

    // Число событий фазы дня с ключом (StartIso, RecordId, FieldId) СТРОГО МЕНЬШЕ заданного —
    // тот же полный ключ, что у offset/keyset-выборок (один порядок, без расхождений).
    private int CountCalendarDayPhaseBefore(DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds,
                                            int allDay, DateTime start, int recordId, int fieldId)
    {
        var s = DateTime.SpecifyKind(dayStart, DateTimeKind.Unspecified).ToString("o");
        var e = DateTime.SpecifyKind(dayEnd,   DateTimeKind.Unspecified).ToString("o");
        var k = DateTime.SpecifyKind(start,    DateTimeKind.Unspecified).ToString("o");
        var col = VisibilityFilter(activeIds);
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT COUNT(*) FROM calendar_index
                WHERE StartIso >= @s AND StartIso < @e AND AllDay = @ad{col}
                  AND (StartIso < @k OR (StartIso = @k AND (RecordId < @r OR (RecordId = @r AND FieldId < @f))))";
            AddParam(cmd, "@s").Value  = s;
            AddParam(cmd, "@e").Value  = e;
            AddParam(cmd, "@ad").Value = allDay;
            AddParam(cmd, "@k").Value  = k;
            AddParam(cmd, "@r").Value  = recordId;
            AddParam(cmd, "@f").Value  = fieldId;
            var o = cmd.ExecuteScalar();
            return o is null or DBNull ? 0 : Convert.ToInt32(o);
        }
        finally { if (opened) conn.Close(); }
    }

    // Верхняя граница длительности «короткого/timed» события — задаёт, насколько далеко
    // назад от начала окна сканируется timed-под-запрос (нижняя граница StartIso). Событие
    // длиннее считается «многодневным» и берётся отдельным all-day-под-запросом без нижней
    // границы, поэтому НЕ теряется. 62 дня — с большим запасом: не-all-day событий такой
    // длины в календаре не бывает (длинное — это all-day/«проект»). Влияет только на скорость
    // timed-под-запроса, не на корректность.
    private const int CalendarTimedBufferDays = 62;

    // Строит события окна календаря из проекции calendar_index. Чтобы индекс не сканировал
    // всё «от начала времён» (StartIso имеет верхнюю границу окна, но не нижнюю), окно
    // берётся ДВУМЯ под-запросами по индексу (CollectionId, AllDay, StartIso):
    //   • timed (AllDay=0): с нижней границей StartIso = начало окна − буфер → быстрый
    //     двусторонний seek (основная масса событий);
    //   • all-day (AllDay=1): без нижней границы (их мало → дёшево) — чтобы многодневные
    //     события, начавшиеся ДО окна (панель AllDayBar), не потерялись.
    // Пересечение окна: StartIso < конец И EndIso >= начало (ISO-строки сравниваются
    // лексикографически — формат фиксирован). Результат объединяется и сортируется по началу
    // (как раньше). Raw-SQL — чтобы гарантированно использовать индекс по AllDay.
    // AddDays с защитой от выхода за диапазон DateTime: Журнал маленьких баз (InMemory) читает
    // «всю историю» с rangeStart = 0001-01-01 — вычитание буфера кидало ArgumentOutOfRange,
    // исключение глотал catch построения, и Журнал показывался ПУСТЫМ.
    private static DateTime SafeAddDays(DateTime dt, int days) =>
        days < 0
            ? (dt.Ticks + (long)days * TimeSpan.TicksPerDay < DateTime.MinValue.Ticks ? DateTime.MinValue : dt.AddDays(days))
            : (dt.Ticks + (long)days * TimeSpan.TicksPerDay > DateTime.MaxValue.Ticks ? DateTime.MaxValue : dt.AddDays(days));

    private List<CalendarRecord> ReadCalendarEvents(DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds)
    {
        var startIso = DateTime.SpecifyKind(rangeStart, DateTimeKind.Unspecified).ToString("o");
        var endIso   = DateTime.SpecifyKind(rangeEnd,   DateTimeKind.Unspecified).ToString("o");
        var lowerIso = SafeAddDays(DateTime.SpecifyKind(rangeStart, DateTimeKind.Unspecified),
                                   -CalendarTimedBufferDays).ToString("o");

        // Фильтр видимости: активные коллекции + скрытые поля-даты (пустой набор = ничего).
        var colFilter = VisibilityFilter(activeIds, "ci.");

        var result = new List<CalendarRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            // Короткие/timed — с нижней границей; многодневные/all-day — без неё (их мало).
            ReadCalendarRows(conn, result,
                $"ci.AllDay = 0{colFilter} AND ci.StartIso >= @lo AND ci.StartIso < @end AND ci.EndIso >= @start",
                [("@lo", lowerIso), ("@end", endIso), ("@start", startIso)]);
            ReadCalendarRows(conn, result,
                $"ci.AllDay = 1{colFilter} AND ci.StartIso < @end AND ci.EndIso >= @start",
                [("@end", endIso), ("@start", startIso)]);
        }
        finally { if (opened) conn.Close(); }

        result.Sort((a, b) => a.StartDt.CompareTo(b.StartDt));
        return result;
    }

    /// <summary>
    /// Окно календаря для видов-сеток (День/Неделя/Месяц/Год) с ОГРАНИЧЕНИЕМ числа timed-событий
    /// на день: гигантский день (миллионы записей) отдаёт сетке первые <paramref name="perDayCap"/>
    /// строк вместо всех — больше ячейке всё равно не отрисовать, остальное показывает «+N ещё»
    /// по карте счётчиков (<see cref="GetCalendarDayCountsByRange"/>). Чтение — per-day ×
    /// per-collection: каждый запрос фиксирует (CollectionId, AllDay) одним значением, ложится
    /// сиком на индекс (CollectionId, AllDay, StartIso) и обрывается LIMIT'ом, НЕ сканируя день
    /// целиком (общий ORDER BY с IN по коллекциям заставил бы SQLite сортировать все миллионы
    /// строк во временной таблице — LIMIT бы не спас). Для дней внутри окна условие EndIso >= start
    /// выполняется автоматически (событие заканчивается не раньше своего старта) — фильтр целиком
    /// индексный. Спилловеры (timed, начавшиеся ДО окна, но пересекающие его) — отдельным проходом
    /// по буферу CalendarTimedBufferDays, как в <see cref="ReadCalendarEvents"/>; all-day — тоже
    /// per-collection с cap (обычно их мало, но импорт дат «без времени» может дать миллионы).
    /// </summary>
    public List<CalendarRecord> GetCalendarRecordsByRangeCapped(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perDayCap)
    {
        // Per-collection запросы — нужны явные id: null — все коллекции БД, ПУСТОЙ набор —
        // ни одной (циклы не выполняются). Коллекции, у которых ВСЕ поля-даты скрыты,
        // пропускаются целиком; частично скрытые поля отсекает residual-фильтр чтения.
        var fullyHidden = FullyHiddenDateCollections();
        var colIds = (activeIds is null
            ? db.DbCollections.Select(c => c.Id).AsEnumerable()
            : activeIds)
            .Where(id => !fullyHidden.Contains(id)).ToList();
        var hidden = HiddenFieldsClause("ci.");

        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);
        var lowerIso = Iso(SafeAddDays(rangeStart, -CalendarTimedBufferDays));

        var result = new List<CalendarRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            // Timed по дням окна (окна сеток выровнены по дням — см. GetDateRange контроллера).
            for (var day = rangeStart.Date; day < rangeEnd; day = day.AddDays(1))
            {
                var dayIso  = Iso(day);
                var nextIso = Iso(day.AddDays(1));
                foreach (var colId in colIds)
                    ReadCalendarRows(conn, result,
                        $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                        " AND ci.StartIso >= @d AND ci.StartIso < @d2",
                        [("@d", dayIso), ("@d2", nextIso)], perDayCap);
            }

            // Timed-спилловеры (начались ДО окна, но пересекают его) — сик по EndIso-индексу:
            // EndIso ∈ [начало окна, начало+буфер). Верхняя граница следует из того же инварианта
            // «timed длиннее буфера не бывает», что и нижняя по StartIso (StartIso ≥ lo и
            // длительность ≤ буфера ⇒ EndIso < win+буфер). Прежний проход сканировал весь
            // StartIso-буфер с проверкой EndIso чтением строк — день-гигант в буфере стоил секунд
            // при каждом открытии «Дня»/«Недели» следующие 62 дня (замер на 10 млн: 3.15 с → 7 мс).
            // Порядок LIMIT'а — по EndIso: его отдаёт сам индекс, скан обрывается на cap.
            var upperIso = Iso(SafeAddDays(rangeStart, CalendarTimedBufferDays));
            foreach (var colId in colIds)
                ReadCalendarRows(conn, result,
                    $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                    " AND ci.EndIso >= @win AND ci.EndIso < @hi" +
                    " AND ci.StartIso >= @lo AND ci.StartIso < @win",
                    [("@win", startIso), ("@hi", upperIso), ("@lo", lowerIso)], perDayCap, "ci.EndIso");

            // All-day/многодневные — сик по EndIso-индексу: у StartIso нет нижней границы
            // (многодневное могло начаться сколь угодно давно), и прежний StartIso-скан шёл от
            // НАЧАЛА таймлайна с проверкой EndIso чтением строк — миллионы прошлых all-day
            // (импорт дат «без времени») стоили секунды на каждое окно после них (замер:
            // 2.2 с → 0 мс). EndIso >= start отсекает прошлое сиком; верхней границы нет —
            // all-day, в отличие от timed, не ограничен буфером по длительности. Cap тем же
            // LIMIT: сетке и панели «весь день» больше всё равно не отрисовать.
            foreach (var colId in colIds)
                ReadCalendarRows(conn, result,
                    $"ci.CollectionId = {colId} AND ci.AllDay = 1{hidden}" +
                    " AND ci.EndIso >= @start AND ci.StartIso < @end",
                    [("@start", startIso), ("@end", endIso)], perDayCap, "ci.EndIso");
        }
        finally { if (opened) conn.Close(); }

        result.Sort((a, b) => a.StartDt.CompareTo(b.StartDt));
        return result;
    }

    /// <summary>Фоновая (изолированное подключение) загрузка окна сетки: только capped-события,
    /// индексными сиками — миллисекунды на любом окне. Карта счётчиков дней («+N ещё»
    /// Месяца/Года) сюда сознательно НЕ входит: её GROUP BY по окну с днём-гигантом — секунды,
    /// поэтому контроллер грузит её ВТОРОЙ фазой (<see cref="LoadCalendarDayCountsAsync"/>)
    /// и тихо уточняет уже показанную сетку.</summary>
    public Task<List<CalendarRecord>> LoadCalendarGridRangeAsync(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perDayCap) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx)
                .GetCalendarRecordsByRangeCapped(rangeStart, rangeEnd, activeIds, perDayCap);
        });

    /// <summary>Фоновая загрузка окна Диаграммы (изолированное подключение) —
    /// см. <see cref="GetDiagramRecordsCapped"/>.</summary>
    public Task<List<CalendarRecord>> LoadDiagramWindowAsync(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perCollectionCap) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx)
                .GetDiagramRecordsCapped(rangeStart, rangeEnd, activeIds, perCollectionCap);
        });

    /// <summary>Фоновая загрузка агрегатов окна Диаграммы (изолированное подключение) —
    /// см. <see cref="GetDiagramStats"/>.</summary>
    public Task<Dictionary<int, DiagramGroupStat>> LoadDiagramStatsAsync(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx)
                .GetDiagramStats(rangeStart, rangeEnd, activeIds);
        });

    /// <summary>
    /// Окно Диаграммы: не более <paramref name="perCollectionCap"/> событий каждого класса
    /// (timed в окне / timed-спилловеры / all-day) НА КОЛЛЕКЦИЮ — Диаграмма всё равно рисует
    /// максимум ~200 строк на группу, а её окно может быть годом или десятилетием.
    /// Per-day чтение сеток (<see cref="GetCalendarRecordsByRangeCapped"/>) здесь не годится:
    /// на 10-летнем окне это 3653 дня × коллекции запросов и до perDayCap строк с КАЖДОГО
    /// дня — десятки секунд и миллионы строк в памяти при том, что показана будет тысяча.
    /// Здесь — 3 индексных сика с LIMIT на коллекцию: память и время не зависят от длины окна
    /// и объёма БД. Какие именно события попали под cap — «первые» в порядке своего индекса
    /// (по StartIso / EndIso); точные счётчики и охват групп считает <see cref="GetDiagramStats"/>.
    /// </summary>
    public List<CalendarRecord> GetDiagramRecordsCapped(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perCollectionCap)
    {
        var fullyHidden = FullyHiddenDateCollections();
        var colIds = (activeIds is null
            ? db.DbCollections.Select(c => c.Id).AsEnumerable()
            : activeIds)
            .Where(id => !fullyHidden.Contains(id)).ToList();
        var hidden = HiddenFieldsClause("ci.");

        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);
        var lowerIso = Iso(SafeAddDays(rangeStart, -CalendarTimedBufferDays));
        var upperIso = Iso(SafeAddDays(rangeStart, CalendarTimedBufferDays));

        var result = new List<CalendarRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            foreach (var colId in colIds)
            {
                // Timed, начавшиеся в окне: порядок отдаёт индекс (CollectionId, AllDay,
                // StartIso), residual-фильтра нет (EndIso ≥ StartIso ≥ start автоматически).
                ReadCalendarRows(conn, result,
                    $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                    " AND ci.StartIso >= @start AND ci.StartIso < @end",
                    [("@start", startIso), ("@end", endIso)], perCollectionCap);

                // Timed-спилловеры — сик по EndIso-индексу, как в окне сеток.
                ReadCalendarRows(conn, result,
                    $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                    " AND ci.EndIso >= @win AND ci.EndIso < @hi" +
                    " AND ci.StartIso >= @lo AND ci.StartIso < @win",
                    [("@win", startIso), ("@hi", upperIso), ("@lo", lowerIso)], perCollectionCap, "ci.EndIso");

                // All-day/многодневные — сик по EndIso-индексу (у StartIso нет нижней границы).
                ReadCalendarRows(conn, result,
                    $"ci.CollectionId = {colId} AND ci.AllDay = 1{hidden}" +
                    " AND ci.EndIso >= @start AND ci.StartIso < @end",
                    [("@start", startIso), ("@end", endIso)], perCollectionCap, "ci.EndIso");
            }
        }
        finally { if (opened) conn.Close(); }

        result.Sort((a, b) => a.StartDt.CompareTo(b.StartDt));
        return result;
    }

    /// <summary>
    /// Точные агрегаты окна Диаграммы по коллекциям: число событий, min(начало), max(конец)
    /// по классам timed/all-day — для счётчиков групп, «+N ещё» и суммарных баров, которые
    /// должны считаться по ВСЕМ событиям окна, а не по обрезанным cap'ом. Все запросы —
    /// покрывающие индексные сканы (в таблицу не ходят): COUNT пропорционален строкам окна
    /// (индексный, ~миллионы/с), MIN/MAX — сик до первой подходящей строки. Контроллер зовёт
    /// это ФОНОВОЙ фазой 2 (как карту счётчиков Месяца/Года) и тихо уточняет уже показанное.
    /// </summary>
    public Dictionary<int, DiagramGroupStat> GetDiagramStats(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds)
    {
        var fullyHidden = FullyHiddenDateCollections();
        var colIds = (activeIds is null
            ? db.DbCollections.Select(c => c.Id).AsEnumerable()
            : activeIds)
            .Where(id => !fullyHidden.Contains(id)).ToList();
        var hidden = HiddenFieldsClause();

        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);
        var lowerIso = Iso(SafeAddDays(rangeStart, -CalendarTimedBufferDays));
        var upperIso = Iso(SafeAddDays(rangeStart, CalendarTimedBufferDays));
        // Timed длиннее буфера не бывает ⇒ их EndIso < конец окна + буфер.
        var endHiIso = Iso(SafeAddDays(rangeEnd, CalendarTimedBufferDays));

        var result = new Dictionary<int, DiagramGroupStat>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            foreach (var colId in colIds)
            {
                // (count, minStart[, maxEnd]) одним сканом соответствующего диапазона индекса.
                (int Count, DateTime? Min, DateTime? Max) Agg(string select, string where,
                    params (string Name, string Value)[] args)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT {select} FROM calendar_index WHERE CollectionId = {colId}{where}{hidden}";
                    foreach (var (name, value) in args) AddParam(cmd, name).Value = value;
                    using var rdr = cmd.ExecuteReader();
                    if (!rdr.Read()) return (0, null, null);
                    var count = rdr.IsDBNull(0) ? 0 : rdr.GetInt32(0);
                    DateTime? At(int i) =>
                        rdr.FieldCount > i && !rdr.IsDBNull(i) && DateTime.TryParse(rdr.GetString(i), out var d)
                            ? d : null;
                    return (count, At(1), At(2));
                }

                // Timed, начавшиеся в окне: диапазон StartIso ∈ [start, end) индекса
                // (CollectionId, AllDay, StartIso, …) — без residual.
                var tWin = Agg("COUNT(*), MIN(StartIso)",
                    " AND AllDay = 0 AND StartIso >= @start AND StartIso < @end",
                    ("@start", startIso), ("@end", endIso));
                // Только MAX(EndIso) — единственный агрегат включает min/max-оптимизацию
                // SQLite: обратный сик по EndIso-индексу до первой строки, прошедшей
                // residual (он тоже в индексе). COUNT рядом заставил бы сканировать
                // весь диапазон.
                DateTime? AggMaxEnd(string where, params (string Name, string Value)[] args)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT MAX(EndIso) FROM calendar_index WHERE CollectionId = {colId}{where}{hidden}";
                    foreach (var (name, value) in args) AddParam(cmd, name).Value = value;
                    var v = cmd.ExecuteScalar();
                    return v is string s && DateTime.TryParse(s, out var d) ? d : null;
                }

                // Timed-спилловеры — тот же EndIso-сик, что у чтения строк.
                var tSpill = Agg("COUNT(*), MIN(StartIso)",
                    " AND AllDay = 0 AND EndIso >= @win AND EndIso < @hi" +
                    " AND StartIso >= @lo AND StartIso < @win",
                    ("@win", startIso), ("@hi", upperIso), ("@lo", lowerIso));
                // Max(конец) timed.
                var tMaxEnd = AggMaxEnd(
                    " AND AllDay = 0 AND EndIso >= @win AND EndIso < @endHi" +
                    " AND StartIso >= @lo AND StartIso < @end",
                    ("@win", startIso), ("@endHi", endHiIso), ("@lo", lowerIso), ("@end", endIso));
                // All-day: count/min/max одним сканом EndIso-диапазона (residual StartIso — в индексе).
                var aday = Agg("COUNT(*), MIN(StartIso), MAX(EndIso)",
                    " AND AllDay = 1 AND EndIso >= @start AND StartIso < @end",
                    ("@start", startIso), ("@end", endIso));

                var count = tWin.Count + tSpill.Count + aday.Count;
                if (count == 0) continue;
                var mins = new[] { tWin.Min, tSpill.Min, aday.Min }.Where(d => d.HasValue).ToList();
                result[colId] = new DiagramGroupStat
                {
                    Count        = count,
                    AllDayCount  = aday.Count,
                    MinStart     = mins.Min()!.Value,
                    MaxEndTimed  = tMaxEnd,
                    MaxEndAllDay = aday.Max,
                };
            }
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    // Общий читатель окна: строки calendar_index (+заголовок записи и цвет коллекции) по условию
    // WHERE над ci. limit > 0 добавляет ORDER BY {orderBy} LIMIT n — чтобы порядок отдал индекс
    // и LIMIT остановил скан, условие обязано фиксировать ci.CollectionId и ci.AllDay одним
    // значением, а orderBy — совпадать с range-колонкой использованного индекса
    // (см. GetCalendarRecordsByRangeCapped).
    private static void ReadCalendarRows(System.Data.Common.DbConnection conn, List<CalendarRecord> into,
                                         string where, IReadOnlyList<(string Name, string Value)> args,
                                         int limit = 0, string orderBy = "ci.StartIso")
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT ci.RecordId, ci.FieldId, ci.CollectionId, ci.StartIso, ci.EndIso, ci.AllDay,
                                    r.Title, r.Description, c.Name, c.Color
                             FROM calendar_index ci
                             JOIN db_records r ON r.Id = ci.RecordId
                             JOIN db_collections c ON c.Id = r.CollectionId
                             WHERE {where}"
                          + (limit > 0 ? $" ORDER BY {orderBy} LIMIT {limit}" : "");
        foreach (var (name, value) in args)
            AddParam(cmd, name).Value = value;
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            if (!DateTime.TryParse(rdr.GetString(3), out var s)) continue;
            if (!DateTime.TryParse(rdr.GetString(4), out var e)) continue;
            into.Add(new CalendarRecord
            {
                Id             = rdr.GetInt32(0),
                FieldId        = rdr.GetInt32(1),
                CollectionId   = rdr.GetInt32(2),
                StartDt        = s,
                EndDt          = e,
                AllDay         = rdr.GetInt32(5) != 0,
                Title          = rdr.IsDBNull(6) ? "" : rdr.GetString(6),
                Description    = rdr.IsDBNull(7) ? "" : rdr.GetString(7),
                CollectionName = rdr.IsDBNull(8) ? "" : rdr.GetString(8),
                Color          = rdr.IsDBNull(9) ? "#007AFF" : rdr.GetString(9),
            });
        }
    }

    // ── Поддержка проекции calendar_index ─────────────────────────────────────

    // Синхронизирует строку проекции для одного значения поля. Видимое поле-дата с
    // распознаваемым значением → upsert; иначе → удалить строку. Вызывать ПОСЛЕ записи
    // значения в db_field_values (читаем переданное value, в БД не лезем повторно).
    private void SyncCalendarIndexValue(int recordId, int fieldId, string? value)
    {
        // Пишем ЛЮБОЕ поле-дату, включая скрытое (IsCalStart=0): видимость — фильтр чтения,
        // проекция всегда полная — включение поля обратно не требует пересборки.
        var field = db.DbFields.Find(fieldId);
        if (field is { FieldType: "date" } && DateValue.TryParse(value, out var dv))
        {
            var (s, e) = dv.ToRecordColumns();
            var allDay = dv.AllDay ? 1 : 0;
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay)
                VALUES ({recordId}, {fieldId}, {field.CollectionId}, {s}, {e}, {allDay})
                ON CONFLICT(RecordId, FieldId) DO UPDATE SET
                    CollectionId = excluded.CollectionId, StartIso = excluded.StartIso,
                    EndIso = excluded.EndIso, AllDay = excluded.AllDay");
        }
        else
        {
            db.Database.ExecuteSqlInterpolated(
                $"DELETE FROM calendar_index WHERE RecordId = {recordId} AND FieldId = {fieldId}");
        }
    }

    private void RebuildCalendarIndexForField(int fieldId)
    {
        db.Database.ExecuteSqlInterpolated($"DELETE FROM calendar_index WHERE FieldId = {fieldId}");
        FillCalendarIndex(q => q.Where(v => v.FieldId == fieldId));
    }

    private void RebuildCalendarIndexForRecord(int recordId)
    {
        db.Database.ExecuteSqlInterpolated($"DELETE FROM calendar_index WHERE RecordId = {recordId}");
        FillCalendarIndex(q => q.Where(v => v.RecordId == recordId));
    }

    // Наполняет calendar_index из значений видимых полей-дат, отобранных scope. Читаем
    // постранично (keyset по PK (RecordId, FieldId)) через EF и вставляем пакетами
    // подготовленным INSERT — память ограничена размером пакета (важно для бэкафилла
    // на миллионах строк).
    private void FillCalendarIndex(Func<IQueryable<DbFieldValueEntity>, IQueryable<DbFieldValueEntity>> scope,
                                   IProgress<int>? progress = null)
    {
        const int batch = 20000;
        // ВСЕ поля-даты, включая скрытые (IsCalStart=0): видимость — фильтр чтения,
        // проекция держится полной (см. SetDateFieldVisible).
        var calFieldIds = db.DbFields.Where(f => f.FieldType == "date").Select(f => f.Id);
        var baseQ = scope(db.DbFieldValues.Where(v => calFieldIds.Contains(v.FieldId) && v.Value != ""));

        var done    = 0;
        var lastRid = 0;
        var lastFid = 0;
        while (true)
        {
            var page = baseQ
                .Where(v => v.RecordId > lastRid || (v.RecordId == lastRid && v.FieldId > lastFid))
                .OrderBy(v => v.RecordId).ThenBy(v => v.FieldId)
                .Select(v => new { v.RecordId, v.FieldId, v.Value, v.Field.CollectionId })
                .Take(batch)
                .ToList();
            if (page.Count == 0) break;

            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == System.Data.ConnectionState.Open;
            if (!wasOpen) conn.Open();
            using (var tx = conn.BeginTransaction())
            using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                // ON CONFLICT DO UPDATE вместо INSERT OR REPLACE: работает на обоих провайдерах
                // (SQLite ≥3.24 и PG) и не пересоздаёт строку (REPLACE = DELETE+INSERT с каскадами).
                ins.CommandText = @"INSERT INTO calendar_index
                    (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay)
                    VALUES (@rid, @fid, @cid, @s, @e, @a)
                    ON CONFLICT(RecordId, FieldId) DO UPDATE SET
                        CollectionId = excluded.CollectionId, StartIso = excluded.StartIso,
                        EndIso = excluded.EndIso, AllDay = excluded.AllDay";
                var pRid = AddParam(ins, "@rid", System.Data.DbType.Int32);
                var pFid = AddParam(ins, "@fid", System.Data.DbType.Int32);
                var pCid = AddParam(ins, "@cid", System.Data.DbType.Int32);
                var pS   = AddParam(ins, "@s",   System.Data.DbType.String);
                var pE   = AddParam(ins, "@e",   System.Data.DbType.String);
                var pA   = AddParam(ins, "@a",   System.Data.DbType.Int32);
                ins.Prepare();

                foreach (var row in page)
                {
                    if (!DateValue.TryParse(row.Value, out var dv)) continue;
                    var (s, e) = dv.ToRecordColumns();
                    pRid.Value = row.RecordId; pFid.Value = row.FieldId; pCid.Value = row.CollectionId;
                    pS.Value = s; pE.Value = e; pA.Value = dv.AllDay ? 1 : 0;
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }
            if (!wasOpen) conn.Close();

            done += page.Count;
            progress?.Report(done);
            lastRid = page[^1].RecordId;
            lastFid = page[^1].FieldId;
        }
    }

    // ── Вспомогательные методы для CalendarRecord ─────────────────────────────

    private int GetOrCreateDefaultCollection()
    {
        var col = db.DbCollections.FirstOrDefault(c => c.Name == "Без названия");
        if (col is not null) return col.Id;

        var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        col = new DbCollectionEntity { Name = "Без названия", Position = maxPos + 1000, Color = "#007AFF" };
        db.DbCollections.Add(col);
        db.SaveChanges();

        EnsureSystemFields(col.Id);
        return col.Id;
    }

    private Dictionary<string, int> EnsureSystemFields(int collectionId)
    {
        var existing = db.DbFields
            .Where(f => f.CollectionId == collectionId)
            .GroupBy(f => f.Name)
            .ToDictionary(g => g.Key, g => g.Min(f => f.Id));

        if (existing.ContainsKey(FieldDate)) return existing;

        var maxPos = db.DbFields
            .Where(f => f.CollectionId == collectionId)
            .Select(f => (int?)f.Position)
            .Max() ?? 0;

        // Системное поле «Дата» — источник календаря (если другой ещё не назначен).
        var hasCalField = db.DbFields.Any(f =>
            f.CollectionId == collectionId && f.FieldType == "date" && f.IsCalStart);

        var field = new DbFieldEntity
        {
            CollectionId = collectionId,
            Name         = FieldDate,
            FieldType    = "date",
            Position     = maxPos + 1000,
            IsSystem     = true,
            IsCalStart   = !hasCalField,
            IsRequired   = false,
        };
        db.DbFields.Add(field);
        db.SaveChanges();
        existing[FieldDate] = field.Id;

        return existing;
    }

    // Поле-дата, в которое класть дату события. Если в окне явно выбрано поле
    // (preferredFieldId — date-поле этой коллекции), пишем в него; иначе фолбэк на
    // EnsureCalendarDateField (видимое поле или авто-«Дата»).
    private int ResolveCalendarDateField(int collectionId, int preferredFieldId)
    {
        if (preferredFieldId > 0)
        {
            var f = db.DbFields.FirstOrDefault(x =>
                x.Id == preferredFieldId && x.CollectionId == collectionId && x.FieldType == "date");
            if (f is not null) return f.Id;
        }
        return EnsureCalendarDateField(collectionId);
    }

    // id поля-источника календаря коллекции: существующее поле-дата с IsCalStart, иначе
    // системное «Дата» (создаётся при необходимости и помечается видимым). Фолбэк, когда
    // явное поле не выбрано.
    private int EnsureCalendarDateField(int collectionId)
    {
        var source = db.DbFields.FirstOrDefault(f =>
            f.CollectionId == collectionId && f.FieldType == "date" && f.IsCalStart);
        if (source is not null) return source.Id;

        // Источника нет (коллекция без даты или помечена «Не отображать») — гарантируем
        // системное «Дата» и делаем его источником: добавление события через календарь
        // должно показать запись в календаре.
        var fieldIds  = EnsureSystemFields(collectionId);
        var dateId    = fieldIds[FieldDate];
        var dateField = db.DbFields.Find(dateId);
        if (dateField is not null && !dateField.IsCalStart)
        {
            dateField.IsCalStart = true;
            db.SaveChanges();
        }
        return dateId;
    }

    // Пишет дату записи в поле-источник календаря (db_field_values). Колонки StartDt/EndDt
    // уже проставлены напрямую в Add/UpdateCalendarRecord — здесь синхронизируем значение
    // самого поля, чтобы оно совпадало с тем, что показывает таблица.
    private void SetCalendarDateValue(int recordId, int dateFieldId, CalendarRecord r)
    {
        var dateStr = new DateValue(r.StartDt, r.EndDt, r.AllDay).ToStorage();

        var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == dateFieldId);
        if (fv is null)
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = recordId, FieldId = dateFieldId, Value = dateStr });
        else
            fv.Value = dateStr;
        db.SaveChanges();

        // Поддерживаем индексную проекцию календаря.
        SyncCalendarIndexValue(recordId, dateFieldId, dateStr);
    }

    // ── Разовая миграция/бэкафилл календарных дат (вызывается при старте) ──────

    /// <summary>
    /// 1) Системные поля «Дата» из эпохи, когда они были типа text, переводит в date
    ///    и помечает источником календаря. 2) Бэкафилл: для записей с заполненным
    ///    календарным полем-датой, но пустыми StartDt — проецирует значение на колонки,
    ///    чтобы такие записи появились в календаре. Идемпотентно и дёшево.
    /// </summary>
    /// <param name="progress">Прогресс тяжёлой фазы (первое открытие старой базы): (обработано,
    /// всего) строк-значений дат; (0, 0) — фаза началась, объём ещё не посчитан. Быстрый путь
    /// (индекс уже построен) колбэк не дёргает — на обычных запусках этап не мелькает.</param>
    public void MigrateAndBackfillCalendarDates(Action<long, long>? progress = null)
    {
        var legacy = db.DbFields.Where(f => f.Name == FieldDate && f.FieldType == "text").ToList();
        foreach (var f in legacy)
        {
            f.FieldType = "date";
            var hasOther = db.DbFields.Any(o =>
                o.CollectionId == f.CollectionId && o.Id != f.Id && o.FieldType == "date" && o.IsCalStart);
            if (!hasOther) f.IsCalStart = true;
        }
        if (legacy.Count > 0) db.SaveChanges();

        // Коллекции, где есть поля-даты, но ни одно не назначено календарным: первое
        // (по позиции) становится источником календаря — чтобы существующие даты тоже
        // попали в календарь.
        var calCollections = db.DbFields
            .Where(f => f.FieldType == "date" && f.IsCalStart)
            .Select(f => f.CollectionId)
            .Distinct()
            .ToHashSet();

        var orphanDateFields = db.DbFields
            .Where(f => f.FieldType == "date" && !calCollections.Contains(f.CollectionId))
            .OrderBy(f => f.CollectionId).ThenBy(f => f.Position)
            .ToList();

        var assigned = new HashSet<int>();
        foreach (var f in orphanDateFields)
            if (assigned.Add(f.CollectionId)) f.IsCalStart = true;
        if (assigned.Count > 0) db.SaveChanges();

        var calFieldIds = db.DbFields
            .Where(f => f.FieldType == "date" && f.IsCalStart)
            .Select(f => f.Id)
            .ToList();
        if (calFieldIds.Count == 0) return;

        // Индекс календаря уже построен → БД мигрирована, а новые данные (создание/импорт/
        // правка) поддерживают StartDt и индекс инкрементально. Значит недобэкафилленных
        // записей нет — пропускаем тяжёлый скан db_field_values на каждом монтировании
        // (он нужен ровно один раз, при первом открытии старой БД). Ускоряет подключение
        // больших БД.
        if (db.CalendarIndex.Any()) return;

        progress?.Invoke(0, 0);   // тяжёлая фаза началась (скан значений; объём ещё не посчитан)

        var pending = db.DbFieldValues
            .Where(v => calFieldIds.Contains(v.FieldId) && v.Value != "" && v.Record.StartDt == null)
            .Select(v => new { v.RecordId, v.Value })
            .ToList();

        var touched = false;
        foreach (var x in pending)
        {
            if (!DateValue.TryParse(x.Value, out var dv)) continue;
            var rec = db.DbRecords.Find(x.RecordId);
            if (rec is null) continue;
            var (startIso, endIso) = dv.ToRecordColumns();
            rec.StartDt = startIso;
            rec.EndDt   = endIso;
            rec.AllDay  = dv.AllDay;
            touched = true;
        }
        if (touched) db.SaveChanges();

        // Индексная проекция календаря: строим один раз, если пуста (первый запуск после
        // обновления). Дальше поддерживается инкрементально при записи/копировании.
        if (!db.CalendarIndex.Any())
        {
            // Общее число значений полей-дат — знаменатель процентов на сплэше (по индексу
            // (FieldId, Value), без скана таблицы).
            var dateFieldIds = db.DbFields.Where(f => f.FieldType == "date").Select(f => f.Id).ToList();
            var total = db.DbFieldValues.LongCount(v => dateFieldIds.Contains(v.FieldId) && v.Value != "");
            FillCalendarIndex(q => q, new SyncProgress<int>(done => progress?.Invoke(done, total)));
        }
    }

    // IProgress без маршалинга в SynchronizationContext (стандартный Progress<T> постит
    // асинхронно): бэкафилл идёт в фоне старта, колбэк должен дёргаться синхронно и по порядку.
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
