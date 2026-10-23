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

/// <summary>
/// Одна строка массовой заливки в готовую коллекцию (<see cref="ConstructorRepository.BulkAddRecords"/>):
/// название записи и значения свойств В ПОРЯДКЕ переданного списка полей. Пустое значение
/// (или <c>null</c>) ячейку не создает.
///
/// Отличие от <see cref="SmartBase.Core.Importers.ImportRow"/>: тот описывает строку ИСТОЧНИКА
/// импорта, где все свойства текстовые и создаются на лету; здесь схема уже есть, поля могут
/// быть любых типов, а значения приходят в каноне хранения этих типов.
/// </summary>
public sealed record BulkRecordRow(string Title, IReadOnlyList<string?> Values);

public partial class ConstructorRepository(AppDbContext db)
{
    // Системное поле «Дата» — единственное, что осталось schema-driven.
    // «Название» — колонка Title в db_records; «Описание» — ОБЫЧНОЕ поле коллекции с ролью
    // IsDescription (заводится только календарем и задачами, см. EnsureDescriptionField).
    private const string FieldDate = "Дата";
    private const string FieldDescription = "Описание";

    /// <summary>Путь к файлу БД этого репозитория — для переноса коллекции в другую БД
    /// (ATTACH источника на подключении целевой БД, см. MoveCollectionIsolated).</summary>
    public string DbPath => db.DbPath;

    /// <summary>Провайдер этой БД — PostgreSQL. Межбазовый перенос с участием PG использует
    /// generic-путь на двух подключениях (ATTACH существует только у SQLite).</summary>
    public bool IsPostgres => db.IsPostgres;

    /// <summary>
    /// Права текущего пользователя в этой базе (ставит DatabaseManager). Публичные мутаторы
    /// первой строкой зовут <see cref="Demand"/> — страховку к скрытым кнопкам UI: запрещенное
    /// действие кидает PermissionDeniedException. Внутренние изолированные клоны репозитория
    /// (фоновые подключения массовых операций) прав не несут — их работа уже разрешена
    /// проверкой публичной точки входа. UI читает это же свойство для видимости кнопок
    /// (Permissions.Can); null — репозиторий без менеджера (тесты, генераторы) без ограничений.
    /// </summary>
    public PermissionService? Permissions { get; init; }

    // Изолированные команды уже разрешены внешним репозиторием. Их отложенная
    // автоподстановка отдельно сохраняет права для повторной проверки перед записью.
    internal PermissionService? FileAutoPermissions { get; init; }
    private PermissionService? AutoFilePermissions => FileAutoPermissions ?? Permissions;

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
    // «до → после» (Detail, Utils/ChangeDetail), длинные тексты свернуты еще при
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
    // той же цели помнится в памяти: следующая правка УДАЛЯЕТ ее и пишет новую от
    // ИСХОДНОГО «до» к последнему «после» — в журнале остается конечный результат.
    // Именно удаление+вставка, не UPDATE строки: новый Id будит поллер соседей на каждое
    // изменение (UPDATE старой строки они бы не увидели). Правка вернула исходное
    // значение — строка убирается совсем (события нет).
    private sealed record EditMerge(ChangeLogEntity Row, string OriginalOld, DateTime LastAt);
    private readonly Dictionary<(string Kind, int RecordId, int SubId), EditMerge> _editMerges = [];
    private static readonly TimeSpan EditMergeWindow = TimeSpan.FromSeconds(30);

    // Исходное «до» для цели: недавняя строка той же цели снимается (ее заменит новая);
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

    /// <summary>Имя коллекции по id («» — коллекции уже нет). Для текстов окон подтверждения.</summary>
    public string CollectionName(int collectionId) => CollectionNameOf(collectionId) ?? "";

    private string? CollectionNameOf(int? collectionId) =>
        collectionId is int cid
            ? db.DbCollections.Where(c => c.Id == cid).Select(c => c.Name).FirstOrDefault()
            : null;

    /// <summary>
    /// Опции типа свойства «пользователь»: отображаемые имена ДОПУЩЕННЫХ пользователей
    /// базы (db_users). В личной базе допущен только владелец — выбрать можно лишь себя.
    /// Значение хранится строкой-именем (как select): фильтр/сортировка/экспорт работают
    /// без доработок, а старые значения остаются историческим снапшотом.
    ///
    /// Читается КАЖДЫЙ раз при построении редактора (запрос дешевый — десятки строк):
    /// состав пользователей меняют соседи, и выпадающий список обязан быть живым —
    /// допущенный появляется, потерявший допуск исчезает без перезапуска.
    ///
    /// <paramref name="includeValue"/> — текущее значение ячейки: оно попадает в опции,
    /// даже если такого пользователя среди допущенных уже нет (ушел из базы, переименовался).
    /// Иначе редактор показал бы пустоту вместо исторического снапшота, а первый же клик
    /// затер бы значение. Историческое значение идет последним — «живые» опции сверху.
    /// </summary>
    public List<string> GetUserOptions(string? includeValue = null)
    {
        var options = db.DbUsers.AsNoTracking()
                                .Where(u => u.IsApproved)
                                .OrderBy(u => u.DisplayName)
                                .Select(u => u.DisplayName)
                                .ToList();
        if (!string.IsNullOrEmpty(includeValue) && !options.Contains(includeValue))
            options.Add(includeValue);
        return options;
    }

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
            return ChangeLogPage(ctx, query, take);
        });
    }

    // Одна страница журнала на ГОТОВОМ контексте: фильтры + сортировка + keyset-срез.
    // Общая для таблицы истории и для выгрузки в CSV — иначе выгрузка отдавала бы
    // не то, что показано на экране (или теряла строки на границе страниц).
    private static List<ChangeLogRow> ChangeLogPage(AppDbContext ctx, ChangeLogQuery query, int take)
    {
        IQueryable<ChangeLogEntity> q = ctx.ChangeLog.AsNoTracking();
        q = FilterChangeLog(q, query.Filters);

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
    }

    private static IQueryable<ChangeLogEntity> FilterChangeLog(IQueryable<ChangeLogEntity> q,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> filters)
    {
        foreach (var (column, values) in filters)
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
        return q;
    }

    /// <summary>Заголовки CSV-выгрузки журнала — в порядке колонок таблицы истории.</summary>
    public static readonly string[] ChangeLogCsvHeaders =
        ["Пользователь", "Действие", "Тип", "Объект", "Коллекция", "Дата и время"];

    /// <summary>
    /// Сколько строк журнала отдаст выгрузка с этими фильтрами — число для окна
    /// подтверждения и знаменатель прогресса. Считается в SQL, отдельное подключение.
    /// </summary>
    public Task<long> CountChangeLogAsync(ChangeLogQuery query)
    {
        var path = db.DbPath;
        return Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            ctx.ApplyPerformancePragmas();
            return FilterChangeLog(ctx.ChangeLog.AsNoTracking(), query.Filters).LongCount();
        });
    }

    /// <summary>
    /// Ленивый поток строк журнала для выгрузки в CSV: значения уже человеческие
    /// («Изменение», «Запись», «до → после», локальное время), порядок и фильтры —
    /// как в таблице истории.
    ///
    /// Одно СВОЕ подключение на всю выгрузку, AsNoTracking: в памяти живет одна страница
    /// (или одна строка ридера), поэтому миллионы строк выгружаются без роста памяти.
    /// Метод-итератор (yield) — вызывать из фонового потока.
    ///
    /// Два режима чтения. Хронология (без сортировки и «Дата и время») — keyset-страницы
    /// по <paramref name="pageSize"/>: срез идет по первичному ключу, каждая страница
    /// стоит один поиск по индексу. Сортировка по строковой колонке — ОДИН запрос
    /// с ORDER BY и потоковое чтение: у change_log вторичных индексов нет, поэтому
    /// keyset-страницами база сортировала бы всю таблицу заново на КАЖДУЮ страницу
    /// (замер: 300 тыс. строк — 14,7 с против 1,6 с, дальше квадратично).
    /// </summary>
    public IEnumerable<string[]> ReadChangeLogCsvRowsIsolated(ChangeLogQuery query,
        CancellationToken ct = default, int pageSize = 5000)
    {
        using var ctx = new AppDbContext(db.DbPath);
        // Обычные прагмы, не bulk: чтение строго последовательное, страницы мелкие —
        // мегабайтные mmap/кеш дали бы тот же темп, но раздули бы рабочий набор
        // (на замере 10 млн строк — 2,2 ГБ против ~300 МБ).
        ctx.ApplyPerformancePragmas();

        foreach (var row in ChangeLogCsvSource(ctx, query, ct, pageSize))
        {
            ct.ThrowIfCancellationRequested();
            yield return
            [
                row.UserName,
                ChangeLogLabels.ActionLabel(row.Action),
                ChangeLogLabels.TypeLabel(row.TargetType),
                ChangeLogLabels.ObjectText(row.TargetType, row.TargetName, row.Count, row.Detail),
                row.CollectionName ?? "",
                ChangeLogLabels.FormatTs(row.Ts, withSeconds: true),
            ];
        }
    }

    private static IEnumerable<ChangeLogRow> ChangeLogCsvSource(AppDbContext ctx,
        ChangeLogQuery query, CancellationToken ct, int pageSize)
    {
        if (query.SortColumn is not (null or "date"))
        {
            // Сортировка по колонке: один ORDER BY на всю выгрузку, строки тянутся
            // ридером по мере записи в файл (без ToList — материализации нет).
            // Временный файл сортировки пусть живет на диске: temp_store=MEMORY из
            // общих прагм на миллионах строк съел бы сотни мегабайт. На PostgreSQL
            // прагм нет — размещением временных файлов сортировки управляет сервер.
            if (!ctx.IsPostgres) ctx.Database.ExecuteSqlRaw("PRAGMA temp_store=FILE;");
            var filtered = FilterChangeLog(ctx.ChangeLog.AsNoTracking(), query.Filters);
            var sorted   = SortChangeLog(filtered, query.SortColumn!, query.SortAsc,
                                         afterValue: null, afterId: null);
            foreach (var row in sorted.Select(e => new ChangeLogRow(e.Id, e.Ts, e.UserName, e.Action,
                                                                    e.TargetType, e.TargetName, e.CollectionName,
                                                                    e.Count, e.Detail)))
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
            yield break;
        }

        var cursor = query;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = ChangeLogPage(ctx, cursor, pageSize);
            if (page.Count == 0) yield break;

            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }

            if (page.Count < pageSize) yield break;
            cursor = cursor with { AfterId = page[^1].Id, AfterValue = null };
        }
    }


    // Сортировка журнала по строковой колонке + keyset-срез после курсора. Селектор
    // колонки один, предикат среза собирается Expression-ами из него же — сравнение
    // строк уходит в SQL тем же ORDER BY-компаратором (string.Compare → «>»/«<»),
    // поэтому страницы не теряют и не дублируют строки. Равные значения добираются
    // по Id DESC (ThenByDescending — вторая компонента курсора).
    private static IQueryable<ChangeLogEntity> SortChangeLog(IQueryable<ChangeLogEntity> q,
        string column, bool asc, string? afterValue, long? afterId)
    {
        // «Действие» и «Тип» сортируются по ПОДПИСИ, а не по коду: в ячейке написано
        // «Изменение», и «От А до Я» по коду «update» ставило бы его после «Удаления».
        Expression<Func<ChangeLogEntity, string>> sel = column switch
        {
            "user"       => e => e.UserName,
            "action"     => Labeled(e => e.Action, ChangeLogLabels.Actions),
            "type"       => Labeled(e => e.TargetType, ChangeLogLabels.Types),
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

    // Код → подпись прямо в SQL: вложенные «код = X ? подпись : …» EF переводит в CASE, незнакомый
    // код остается как есть — ровно как ChangeLogLabels.ActionLabel/TypeLabel в ячейке.
    private static Expression<Func<ChangeLogEntity, string>> Labeled(
        Expression<Func<ChangeLogEntity, string>> code, IReadOnlyDictionary<string, string> labels)
    {
        Expression body = code.Body;
        foreach (var (raw, label) in labels)
            body = Expression.Condition(Expression.Equal(code.Body, Expression.Constant(raw)),
                                        Expression.Constant(label), body);
        return Expression.Lambda<Func<ChangeLogEntity, string>>(body, code.Parameters);
    }

    /// <summary>
    /// Значение сортируемой колонки у строки — вторая половина keyset-курсора
    /// (<see cref="ChangeLogQuery.AfterValue"/>). Живет рядом с <see cref="SortChangeLog"/>:
    /// курсор обязан совпадать с выражением ORDER BY, иначе страницы теряют или дублируют строки.
    /// </summary>
    public static string ChangeLogSortValue(string column, ChangeLogRow row) => column switch
    {
        "user"       => row.UserName,
        "action"     => ChangeLogLabels.ActionLabel(row.Action),
        "type"       => ChangeLogLabels.TypeLabel(row.TargetType),
        "object"     => row.TargetName,
        "collection" => row.CollectionName ?? "",
        "date"       => row.Ts.Length >= 10 ? row.Ts[..10] : row.Ts,
        _            => row.UserName,
    };

    /// <summary>
    /// Значения колонки журнала с количеством — для окна фильтра таблицы истории
    /// (SQL-агрегат на фоновом потоке, отдельное подключение — как GetColumnCountsAsync).
    /// Ключи колонок — как в <see cref="ChangeLogQuery"/>; date группируется по дню.
    ///
    /// Каскад, как у таблицы конструктора: считаются только строки, прошедшие
    /// <paramref name="filters"/> ОСТАЛЬНЫХ колонок. Свой фильтр колонки отбрасывается здесь
    /// же — иначе снятое значение пропало бы из списка и вернуть его в отбор было бы нечем.
    /// </summary>
    public Task<List<(string Value, int Count)>> GetChangeLogCountsAsync(string column,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? filters = null)
    {
        var path   = db.DbPath;
        var others = filters?.Where(kv => kv.Key != column).ToDictionary(kv => kv.Key, kv => kv.Value)
                     ?? new Dictionary<string, IReadOnlyCollection<string>>();
        return Task.Run(() =>
        {
            using var ctx = new AppDbContext(path);
            ctx.ApplyPerformancePragmas();
            var src = FilterChangeLog(ctx.ChangeLog.AsNoTracking(), others);
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
    /// весь журнал. Только администратор. Возвращает число удаленных строк.
    /// </summary>
    public int PurgeChangeLog(DateTime? olderThanUtc)
    {
        if (Permissions is { IsAdmin: false })
            throw new InvalidOperationException(
                "Очистить журнал может только администратор базы данных.");

        // Кеш слияния побуквенных правок мог держать ссылки на удаляемые строки —
        // сбрасываем, иначе следующая правка попыталась бы удалить уже удаленную строку.
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
    ///
    /// Возвращает запущенную задачу: ее ДОЛЖЕН дождаться DatabaseManager.Dispose, иначе
    /// чистка переживает закрытие базы и продолжает держать файл. Замер до правки: из 12
    /// открытий-закрытий файл оставался занят сразу после Dispose семь раз (освобождался
    /// в пределах ~700 мс). Это ломало не только уборку в тестах: перемонтирование сразу
    /// после размонтирования (штатный сценарий DbHealthMonitor после «связь потеряна»)
    /// первым делом выполняет PRAGMA journal_mode, а она падает SQLITE_BUSY, пока файл
    /// держит другое соединение.
    /// </summary>
    public Task ApplyChangeLogRetention(bool enabled, int months)
    {
        if (!enabled || months <= 0) return Task.CompletedTask;
        var cutoff = DateTime.UtcNow.AddMonths(-months).ToString("o");
        var path   = db.DbPath;
        return Task.Run(() =>
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
    /// БД. Структуру (группы + шеллы коллекций) создаем через EF, данные коллекций — bulk-SQL
    /// (<see cref="CopyCollectionData"/>), как дубль коллекции. Верхняя группа получает имя «X (копия)»,
    /// вложенные элементы — свои имена. <paramref name="progress"/> — нарастающее число скопированных
    /// записей по всем коллекциям ветки (для %). Возвращает id новой верхней группы.
    /// </summary>
    public int DuplicateGroupCore(int groupId, IProgress<int>? progress = null, int? batchSize = null)
    {
        Demand(PermAction.DuplicateCollection);
        var src = db.DbGroups.Find(groupId);
        if (src is null) throw new InvalidOperationException($"Group {groupId} not found");
        using var relationCopy = BeginRelationCopy();

        // 1) Структура: верхняя группа «(копия)» под тем же родителем; подгруппы — со своими именами.
        //    Вместе с ней — маркер незавершенной копии на всю ветку (ConstructorRepository.Pending):
        //    выход из программы посреди дубля оставит ветку с половиной коллекций, ее уберет монтирование.
        var groupMap = new Dictionary<int, int>();   // старый id группы → новый
        int newRootId;
        PendingCopy pending;
        using (var tx = db.Database.BeginTransaction())
        {
            newRootId = CloneGroupTree(groupId, src.ParentId, isRoot: true, groupMap);
            pending   = BeginPending(db, PendingKind.DuplicateGroup, newRootId, singleTx: db.IsPostgres);
            tx.Commit();
        }

        try
        {
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
                    CopyCollectionData(c.Id, newColId, new BaseProgress(progress, basis), batchSize, pending,
                        relationCopy: relationCopy);
                // На PG коллекция копируется одной транзакцией и сама не отмечается — отметка между ними.
                if (db.IsPostgres) BeatPending(db, pending);
                basis += rc;
            }
            using (var tx = db.Database.BeginTransaction())
            {
                CompleteRelationCopy(relationCopy, "");
                LogNow("duplicate", "group", src.Name, count: basis);
                FinishPending(db, pending);
                tx.Commit();
            }
            progress?.Report((int)basis);
            return newRootId;
        }
        catch (Exception ex)
        {
            // Недописанная ветка не остается под видом настоящей; маркер забрала уборка соседа —
            // ветку удаляет она.
            if (ex is not PendingLostException)
            {
                try { DeleteGroupData(newRootId, log: false, enforceRestrict: false); }
                catch { /* маркер остался — доудалит монтирование */ }
                DropPending(db, pending);
            }
            throw;
        }
    }

    /// <summary>Полный дубль группы на ОТДЕЛЬНОМ подключении (фон + прогресс для больших веток) —
    /// не трогает общий Context (см. правило об общем AppDbContext), безопасно из Task.Run.</summary>
    public int DuplicateGroupIsolated(int groupId, IProgress<int>? progress = null, int? batchSize = null)
    {
        Demand(PermAction.DuplicateCollection);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        return new ConstructorRepository(ctx).DuplicateGroupCore(groupId, progress, batchSize);
    }

    // Рекурсивно копирует только СТРУКТУРУ групп (без коллекций): новая группа под targetParentId,
    // затем ее подгруппы. Заполняет map (старый id → новый). Верхняя получает «(копия)», вложенные — имена как есть.
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
        EnsureDeleteAllowedForCollections(CollectionIdsInGroup(groupId));
        using var tx = BeginWrite();
        // Одна строка журнала на ветку (рекурсия по подгруппам — внутри core).
        var name = db.DbGroups.Where(g => g.Id == groupId).Select(g => g.Name).FirstOrDefault();
        DeleteGroupCore(groupId);
        if (name is not null) LogNow("delete", "group", name);
        tx.Commit();
    }

    private void DeleteGroupCore(int groupId)
    {
        // Рекурсивно удаляем все вложенные группы.
        var childIds = db.DbGroups.Where(g => g.ParentId == groupId).Select(g => g.Id).ToList();
        foreach (var childId in childIds)
            DeleteGroupCore(childId);

        // Удаляем коллекции этой группы; поля/записи/значения уйдут каскадно, UI-строки без FK — явно.
        var colIds = db.DbCollections.Where(c => c.GroupId == groupId).Select(c => c.Id).ToList();
        db.DbRecordLinks.Where(l => l.TargetDbId == 0 && db.DbRecords.Any(r => r.Id == l.ToRecordId && colIds.Contains(r.CollectionId)))
            .ExecuteDelete();
        db.DbCollections.RemoveRange(db.DbCollections.Where(c => c.GroupId == groupId));

        var entity = db.DbGroups.Find(groupId);
        if (entity is not null) db.DbGroups.Remove(entity);
        db.SaveChanges();
        foreach (var colId in colIds)
        {
            DeleteCollectionUiRows(colId);
            NotifyRelationDataChanged(colId);
        }
    }

    // UI-настройки коллекции без FK (ui_*, record_display, column_meta) чистим явно при ЛЮБОМ
    // удалении коллекции: каскада на них нет, строки сиротеют, а id коллекций SQLite
    // переиспользует — фантомные настройки достались бы будущей коллекции с тем же id.
    private void DeleteCollectionUiRows(int collectionId)
    {
        // EF1002 «возможная SQL-инъекция»: в строку подставляются только целые идентификаторы
        // и имена таблиц, заданные здесь же в коде. Значений от пользователя в ней нет — те
        // уходят параметрами. Сырой SQL намеренно: пакетные DELETE и INSERT…SELECT на тысячах
        // строк параметрами не выражаются.
        #pragma warning disable EF1002
        // cell_style обычно уносит FK-каскад вместе с записями, но чистим и здесь: если записи
        // ушли другим путем, строки осиротели бы, а id коллекций SQLite переиспользует.
        foreach (var t in new[] { "ui_row_heights", "record_display", "ui_col_widths",
                                  "ui_collection_settings", "column_meta", "cell_style" })
            db.Database.ExecuteSqlRaw($"DELETE FROM {t} WHERE CollectionId = {collectionId}");
        #pragma warning restore EF1002
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
    /// подсчета перед массовыми операциями сайдбара: COUNT по ветке с миллионами записей — сотни мс,
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
    /// веток. Не трогает общий Context (см. правило об общем AppDbContext), безопасно из Task.Run.
    /// <paramref name="collectionsTogether"/> — коллекции, удаляемые в той же операции (выделение
    /// нескольких узлов): ссылки из них запрету удаления не мешают.</summary>
    public void DeleteGroupIsolated(int groupId, IProgress<int>? progress = null,
                                    IReadOnlyCollection<int>? collectionsTogether = null)
    {
        Demand(PermAction.DeleteCollection);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        new ConstructorRepository(ctx) { ReferenceViewer = Permissions }
            .DeleteGroupData(groupId, progress, collectionsTogether: collectionsTogether);
    }

    /// <summary>
    /// Удалить группу со всей веткой ПАКЕТАМИ: коллекции ветки по очереди тем же путем, что
    /// <see cref="DeleteCollectionData"/> (транзакция на пакет, явные bulk-DELETE значений/проекции,
    /// чистка UI-строк без FK), затем строки групп ветки. <paramref name="progress"/> — нарастающее
    /// число удаленных записей по всей ветке (для %). <paramref name="enforceRestrict"/> = false —
    /// служебная уборка (откат копии, остаток прерванного переноса): запрет удаления проверен до
    /// операции или к ней не относится.
    /// </summary>
    public void DeleteGroupData(int groupId, IProgress<int>? progress = null, int? batchSize = null,
                                bool log = true, IReadOnlyCollection<int>? collectionsTogether = null,
                                bool enforceRestrict = true)
    {
        var groupName = db.DbGroups.Where(g => g.Id == groupId).Select(g => g.Name).FirstOrDefault();
        var groupIds = SubtreeGroupIds(groupId);
        var colIds   = db.DbCollections.Where(c => c.GroupId != null && groupIds.Contains(c.GroupId.Value))
                         .Select(c => c.Id).ToList();
        if (enforceRestrict) EnsureDeleteAllowedForCollections(colIds, collectionsTogether);
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
        foreach (var collection in colIds) NotifyRelationDataChanged(collection);
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

        // «Название» — колонка Title в db_records, полем оно не заводится. «Описание» —
        // обычное поле, но его создают только календарь и задачи (EnsureDescriptionField).
        return col;
    }

    // Ключ системной колонки «Название» (см. TableRowVm.TitleKey). Дублируем строкой,
    // чтобы слой БД не зависел от ViewModels.
    private const string TitleKey = "__title__";

    /// <summary>
    /// Создать коллекцию и залить записи ПОТОКОМ: маленькая shell-транзакция (коллекция + текстовые
    /// поля + подпись «Название» + порядок колонок, «Описание» пустое в конце), затем строки
    /// пакетами по <paramref name="batchSize"/> — каждый пакет в СВОЕЙ транзакции (WAL не
    /// разрастается на весь объем, отмена срабатывает между пакетами). Записи и значения пишутся
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
        if (schema.PortableSchema is not null)
            return ImportPortableStreamedCollection(collectionName, groupId, schema, rows, ct, rowsWritten, batchSize);

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

            // Порядок колонок: «Название», затем свойства. Поля «Описание» у импорта нет —
            // оно заводится только календарным/задачным путем (EnsureDescriptionField).
            var order = new List<string> { TitleKey };
            order.AddRange(fieldIds.Select(id => id.ToString()));
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
                ? new ImportException("Недостаточно места на диске — импорт отменен, частичные данные удалены.")
                : new ImportException("Ошибка записи в базу данных. Импорт отменен.");
        }
        catch (Npgsql.PostgresException ex)
        {
            Serilog.Log.Error(ex, "Ошибка записи в PostgreSQL при импорте коллекции «{Name:l}»", collectionName);
            TryDeleteImported(colId);
            throw ex.SqlState == "53100"   // disk_full
                ? new ImportException("Недостаточно места на диске — импорт отменен, частичные данные удалены.")
                : new ImportException("Ошибка записи в базу данных. Импорт отменен.");
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
        try { DeleteCollectionData(collectionId, log: false, enforceRestrict: false); }
        catch { /* best-effort */ }
    }

    // Батч-цикл записи (модель транзакций — DeleteCollectionRows): prepared-команды создаются один
    // раз, на каждый пакет переприсваивается транзакция. Прогресс — раз в пакет.
    private void WriteRowsBatched(int collectionId, List<int> fieldIds, ImportSchema schema,
        IEnumerable<ImportRow> rows, CancellationToken ct, IProgress<long>? rowsWritten, int batchSize,
        Action<IReadOnlyList<ImportRow>, IReadOnlyList<long>>? onBatch = null)
    {
        // Шаг Position: 1000 (есть куда вставлять строки между импортированными), но на
        // многомиллионных объемах — 1: (idx+1)*1000 в int переполняется после ~2,1 млн строк.
        var posStep = schema.RowCount > 2_000_000 ? 1L : 1000L;
        var now     = DateTime.Now.ToString("o");
        var conn    = db.Database.GetDbConnection();

        using var insRec = conn.CreateCommand();
        // RETURNING Id (SQLite ≥3.35 и PG): id каждой записи получаем из самой вставки — без
        // допущения «новые id непрерывны» (на PG identity-последовательность делят все клиенты).
        insRec.CommandText =
            "INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title) " +
            "VALUES (@cid, @created, @created, @author, @author, @pos, 0, @title) RETURNING Id";
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

            using var tx = db.Database.CurrentTransaction is null ? db.Database.BeginTransaction() : null;
            var dbTx = db.Database.CurrentTransaction!.GetDbTransaction();
            var useBatch = conn.CanCreateBatch;   // PostgreSQL: пакет команд = один сетевой ход
            insRec.Transaction = dbTx;
            insVal.Transaction = dbTx;
            if (!prepared && !useBatch) { insRec.Prepare(); insVal.Prepare(); prepared = true; }

            newIds.Clear();
            if (useBatch)
            {
                for (var chunk = 0; chunk < batch.Count; chunk += NetBatchCommands)
                {
                    using var nb = conn.CreateBatch();
                    nb.Transaction = dbTx;
                    var chunkEnd = Math.Min(chunk + NetBatchCommands, batch.Count);
                    for (var i = chunk; i < chunkEnd; i++)
                    {
                        rPos.Value   = (written + i + 1) * posStep;
                        rTitle.Value = batch[i].Title ?? "";
                        AddBatchCommand(nb, insRec);
                    }
                    // RETURNING Id: один результат на команду, строго в порядке команд пакета.
                    using var r = nb.ExecuteReader();
                    do { r.Read(); newIds.Add(Convert.ToInt64(r.GetValue(0))); } while (r.NextResult());
                }
            }
            else
                for (var i = 0; i < batch.Count; i++)
                {
                    rPos.Value   = (written + i + 1) * posStep;
                    rTitle.Value = batch[i].Title ?? "";
                    newIds.Add(Convert.ToInt64(insRec.ExecuteScalar()!));
                }

            using var net = useBatch ? new NetBatcher(conn, dbTx) : null;
            for (var i = 0; i < batch.Count; i++)
            {
                var row = batch[i];
                vRid.Value = newIds[i];
                for (var f = 0; f < fieldIds.Count; f++)
                {
                    // Пустая ячейка строки в db_field_values не заводится (как в BulkAddRecords):
                    // отсутствие строки читается как пусто, а на импорте с редкими колонками
                    // пустые ячейки — десятки процентов таблицы значений и ее индекса.
                    if (schema.PortableSchema?.Fields[f].FieldType is "relation" or "lookup" or "rollup") continue;
                    var value = f < row.FieldValues.Count ? row.FieldValues[f] : null;
                    if (string.IsNullOrEmpty(value)) continue;
                    vFid.Value = fieldIds[f];
                    vVal.Value = value;
                    if (net is not null) net.Add(insVal);
                    else insVal.ExecuteNonQuery();
                }
            }
            net?.Flush();

            onBatch?.Invoke(batch, newIds);
            tx?.Commit();
            written += batch.Count;
            rowsWritten?.Report(written);
        }

        rowsWritten?.Report(written);
    }

    /// <summary>
    /// Массовая заливка записей в УЖЕ СОЗДАННУЮ коллекцию — тот же сырой путь, что у импорта
    /// (<see cref="WriteRowsBatched"/>), но поверх готовой схемы: поля создал вызывающий, и
    /// типы у них могут быть любые (число, дата, файл), а не только текст.
    ///
    /// Зачем не через <c>AddRecord</c> + <c>SetFieldValue</c>: тот путь рассчитан на правку
    /// ОДНОЙ ячейки руками и на каждое значение делает проверку права, чтение типа поля, поиск
    /// прежнего значения, строку журнала, проверку событий задач и свой <c>SaveChanges</c> —
    /// то есть свою транзакцию. На своде трудоемкости в 600 тыс. строк это ~4,2 млн коммитов и
    /// столько же строк журнала: замер дал 18 строк/с против 13 300 строк/с здесь.
    ///
    /// Журнал получает ОДНУ строку с числом записей — как и все пакетные пути.
    /// Значения по умолчанию для полей типа «пользователь» здесь НЕ проставляются (это делает
    /// <see cref="AddRecord"/>): у массовой заливки все значения приходят от вызывающего.
    /// </summary>
    /// <param name="fieldIds">Свойства коллекции в том порядке, в каком идут значения строки.</param>
    /// <param name="rows">Строки потоком — в памяти живет только текущий пакет.</param>
    /// <param name="rowCount">Ожидаемое число строк: шаг <c>Position</c> и размер списка id.</param>
    /// <returns>Id созданных записей в порядке строк — вызывающему они нужны, чтобы разложить
    /// оформление (заливку строк и ячеек) пакетами.</returns>
    public List<int> BulkAddRecords(int collectionId, IReadOnlyList<int> fieldIds,
        IEnumerable<BulkRecordRow> rows, long rowCount, CancellationToken ct,
        IProgress<long>? rowsWritten = null, int batchSize = 10_000)
    {
        Demand(PermAction.CreateRecord, collectionId);

        // Шаг Position — как у импорта: 1000 оставляет место для вставки строк между
        // залитыми, но на многомиллионных объемах (idx+1)*1000 переполняет int.
        var posStep = rowCount > 2_000_000 ? 1L : 1000L;
        var now     = DateTime.Now.ToString("o");
        var conn    = db.Database.GetDbConnection();

        using var insRec = conn.CreateCommand();
        insRec.CommandText =
            "INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, AllDay, Title) " +
            "VALUES (@cid, @created, @created, @author, @author, @pos, 0, @title) RETURNING Id";
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
        var vRid = AddParam(insVal, "@rid", System.Data.DbType.Int32);
        var vFid = AddParam(insVal, "@fid", System.Data.DbType.Int32);
        var vVal = AddParam(insVal, "@val", System.Data.DbType.String);

        // Id записи в модели — int (DbRecordEntity.Id), поэтому сужение из ExecuteScalar
        // безопасно; long тут только потому, что SQLite отдает INTEGER как Int64.
        var ids   = new List<int>(rowCount is > 0 and < int.MaxValue ? (int)rowCount : 0);
        var batch = new List<BulkRecordRow>(Math.Min(batchSize, 4096));

        long written  = 0;
        var  prepared = false;
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
            var useBatch = conn.CanCreateBatch;   // PostgreSQL: пакет команд = один сетевой ход
            insRec.Transaction = dbTx;
            insVal.Transaction = dbTx;
            if (!prepared && !useBatch) { insRec.Prepare(); insVal.Prepare(); prepared = true; }

            var first = ids.Count;
            if (useBatch)
            {
                for (var chunk = 0; chunk < batch.Count; chunk += NetBatchCommands)
                {
                    using var nb = conn.CreateBatch();
                    nb.Transaction = dbTx;
                    var chunkEnd = Math.Min(chunk + NetBatchCommands, batch.Count);
                    for (var i = chunk; i < chunkEnd; i++)
                    {
                        rPos.Value   = (written + i + 1) * posStep;
                        rTitle.Value = batch[i].Title ?? "";
                        AddBatchCommand(nb, insRec);
                    }
                    // RETURNING Id: один результат на команду, строго в порядке команд пакета.
                    using var r = nb.ExecuteReader();
                    do { r.Read(); ids.Add(Convert.ToInt32(r.GetValue(0))); } while (r.NextResult());
                }
            }
            else
                for (var i = 0; i < batch.Count; i++)
                {
                    rPos.Value   = (written + i + 1) * posStep;
                    rTitle.Value = batch[i].Title ?? "";
                    ids.Add(Convert.ToInt32(insRec.ExecuteScalar()!));
                }

            using var net = useBatch ? new NetBatcher(conn, dbTx) : null;
            for (var i = 0; i < batch.Count; i++)
            {
                var values = batch[i].Values;
                vRid.Value = ids[first + i];
                for (var f = 0; f < fieldIds.Count && f < values.Count; f++)
                {
                    // Пустая ячейка строки в db_field_values не заводится: на своде это
                    // десятки процентов строк таблицы значений, а читается пустое и так пустым.
                    var value = values[f];
                    if (string.IsNullOrEmpty(value)) continue;
                    vFid.Value = fieldIds[f];
                    vVal.Value = value;
                    if (net is not null) net.Add(insVal);
                    else insVal.ExecuteNonQuery();
                }
            }
            net?.Flush();

            tx.Commit();
            written += batch.Count;
            rowsWritten?.Report(written);
        }

        var name = CollectionNameOf(collectionId) ?? "";
        LogNow("create", "record", name, name, count: written);
        rowsWritten?.Report(written);
        NotifyRelationDataChanged(collectionId);
        return ids;
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
        EnsureDeleteAllowedForCollections([collectionId]);
        using var tx = BeginWrite();
        db.DbRecordLinks.Where(l => l.TargetDbId == 0 && db.DbRecords.Any(r => r.Id == l.ToRecordId && r.CollectionId == collectionId))
            .ExecuteDelete();
        var entity = db.DbCollections.Find(collectionId);
        if (entity is not null)
        {
            Log("delete", "collection", entity.Name);
            db.DbCollections.Remove(entity);
        }
        db.SaveChanges();
        DeleteCollectionUiRows(collectionId);
        NotifyRelationDataChanged(collectionId);
        tx.Commit();
    }

    /// <summary>
    /// Удалить коллекцию на ОТДЕЛЬНОМ подключении (фон + прогресс) — для больших коллекций.
    /// Не трогает общий Context (см. правило об общем AppDbContext), поэтому безопасно из Task.Run.
    /// </summary>
    public void DeleteCollectionIsolated(int collectionId, IProgress<int>? progress = null,
                                         IReadOnlyCollection<int>? collectionsTogether = null)
    {
        Demand(PermAction.DeleteCollection, collectionId);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        new ConstructorRepository(ctx) { ReferenceViewer = Permissions }
            .DeleteCollectionData(collectionId, progress, collectionsTogether: collectionsTogether);
        // Хвост журнала переносит закрытие контекста (AppDbContext.Dispose массового профиля).
    }

    /// <summary>
    /// Удалить записи коллекции ПАКЕТАМИ, затем строку коллекции и UI-настройки. Каждый пакет —
    /// СВОЯ транзакция: прежде вся операция шла одной гигантской транзакцией, и WAL разрастался на
    /// весь объем изменений (десятки ГБ на многомиллионных коллекциях); частичное удаление при сбое
    /// не страшно — повтор операции доудаляет остаток. Значения и проекция календаря удаляются
    /// ЯВНЫМ bulk-DELETE до записей (каскаду по FK остается проверить пустоту), UI-строки без FK
    /// (ui_row_heights, record_display и коллекция-level) чистятся явно — иначе они сиротели, а id
    /// коллекций SQLite переиспользует (фантомные настройки у будущей коллекции с тем же id).
    /// Размер пакета — по времени транзакции, как у дубля (<see cref="BatchPacer"/>): мелкие пакеты
    /// на данных вразнобой переписывают одни и те же страницы глобальных индексов значений и
    /// календаря на каждом коммите. Заданный <paramref name="batchSize"/> — постоянный (тесты, стенды).
    /// <paramref name="progress"/> — нарастающее число удаленных записей.
    /// </summary>
    public void DeleteCollectionData(int collectionId, IProgress<int>? progress = null,
                                     int? batchSize = null, bool log = true,
                                     IReadOnlyCollection<int>? collectionsTogether = null, bool enforceRestrict = true)
    {
        if (enforceRestrict) EnsureDeleteAllowedForCollections([collectionId], collectionsTogether);
        var name     = CollectionNameOf(collectionId);
        var recCount = db.DbRecords.Count(r => r.CollectionId == collectionId);
        DeleteCollectionRows(collectionId, 0, progress, batchSize);
        NotifyRelationDataChanged(collectionId);
        if (log && name is not null) LogNow("delete", "collection", name, count: recCount);
        progress?.Report(recCount);   // финально 100% для этой коллекции
    }

    // Удаление одной коллекции пакетами (транзакция на пакет, см. DeleteCollectionData): значения и
    // проекция — bulk-DELETE по записям пакета, затем записи; в конце строка коллекции (каскад уберет
    // поля) и UI-строки без FK. progressBase — сдвиг прогресса при удалении ветки группы. Размер
    // пакета — по времени транзакции (BatchPacer), заданный явно — постоянный. Числа в SQL —
    // int/long, которые мы формируем сами, поэтому интерполяция в Raw безопасна.
    //
    // Записи пакета на SQLite — первые по индексу коллекции (CollectionId, Position), во временной
    // таблице: удаленные из индекса уходят, и следующий пакет снова берет первые — каждый выбор
    // стоит размера пакета. Прежняя граница пакета по Id (ORDER BY Id … OFFSET) сортировала на
    // каждом пакете все оставшиеся записи коллекции — квадрат от ее размера (минуты на 10 млн), а
    // срез по Id без индекса коллекции (+CollectionId) просматривал бы записи всех коллекций в
    // окне id. На PG граница по Id остается (планировщик идет по первичному ключу).
    private void DeleteCollectionRows(int collectionId, long progressBase, IProgress<int>? progress, int? batchSize)
    {
        var  sqlite  = !db.IsPostgres;
        var  pacer   = new BatchPacer(batchSize, sqlite && NetworkPath.IsNetwork(db.DbPath));
        long deleted = 0;
        var  prevId  = 0;
        if (sqlite) db.Database.OpenConnection();   // временная таблица живет на подключении между пакетами
        try
        {
            if (sqlite)
            {
                db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._del_ids");
                db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _del_ids(Id INTEGER PRIMARY KEY)");
            }
            while (true)
            {
                var size  = pacer.Size;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                bool last;
                string ids, slice;
                using (var tx = db.Database.BeginTransaction())
                {
                    // EF1002 «возможная SQL-инъекция»: в строку подставляются только целые идентификаторы
                    // и имена таблиц, заданные здесь же в коде. Значений от пользователя в ней нет — те
                    // уходят параметрами. Сырой SQL намеренно: пакетные DELETE и INSERT…SELECT на тысячах
                    // строк параметрами не выражаются.
                    #pragma warning disable EF1002
                    if (sqlite)
                    {
                        db.Database.ExecuteSqlRaw("DELETE FROM temp._del_ids");
                        var n = db.Database.ExecuteSqlRaw($@"
                            INSERT INTO temp._del_ids(Id)
                            SELECT Id FROM db_records
                            WHERE CollectionId = {collectionId} LIMIT {size}");
                        last  = n < size;
                        ids   = "(SELECT Id FROM temp._del_ids)";
                        slice = $"Id IN {ids}";
                    }
                    else
                    {
                        var end = DeleteBatchEndId(collectionId, prevId, size);
                        slice   = $"CollectionId = {collectionId} AND Id > {prevId}" + (end is int u ? $" AND Id <= {u}" : "");
                        ids     = $"(SELECT Id FROM db_records WHERE {slice})";
                        last    = end is null;
                        if (end is int e) prevId = e;
                    }
                    db.Database.ExecuteSqlRaw($"DELETE FROM db_field_values WHERE RecordId IN {ids}");
                    db.Database.ExecuteSqlRaw($"DELETE FROM calendar_index  WHERE RecordId IN {ids}");
                    // Входящие связи в этой же БД на удаляемые записи (в т.ч. из других коллекций): FK на
                    // ToRecordId снят — чистим явно. Исходящие уходят каскадом FromRecordId с db_records.
                    db.Database.ExecuteSqlRaw($"DELETE FROM db_record_links WHERE TargetDbId = 0 AND ToRecordId IN {ids}");
                    db.Database.ExecuteSqlRaw($"DELETE FROM db_records WHERE {slice}");
                    #pragma warning restore EF1002
                    tx.Commit();
                }
                if (last) break;   // последний (неполный) пакет удален
                pacer.Done(watch.Elapsed);
                CheckpointIfWalLarge();
                deleted += size;
                progress?.Report((int)(progressBase + deleted));
            }

            using var tail = db.Database.BeginTransaction();
            // UI-настройки коллекции: FK-каскада у этих таблиц нет — только явная чистка.
            DeleteCollectionUiRows(collectionId);
            // Строка коллекции: каскад уберет поля (их строки calendar_index уже удалены выше).
            db.Database.ExecuteSqlInterpolated($"DELETE FROM db_collections WHERE Id = {collectionId}");
            tail.Commit();
        }
        finally
        {
            if (sqlite)
            {
                try { db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._del_ids"); }
                catch { /* временная таблица уйдет вместе с подключением */ }
                db.Database.CloseConnection();
            }
        }
    }

    // Id записи на позиции offset−1 в срезе коллекции после prevId (порядок Id) — граница пакета на
    // PG. null, если записей меньше (последний неполный пакет). Параметры — int, не строки.
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
        var shell = DuplicateCollectionCore(collectionId, progress: null);
        LogNow("duplicate", "collection", shell.Collection.Name, count: shell.SourceRecordCount);
        return shell.Collection;
    }

    /// <summary>
    /// Создать «скелет» дубля коллекции: строка коллекции + копии полей (колонок), без записей.
    /// Записи/значения копирует затем <see cref="CopyCollectionData"/> (sync) или
    /// <see cref="DuplicateCollectionIsolated"/> (фон + прогресс). Возвращает новую коллекцию
    /// и число записей источника.
    /// </summary>
    /// <param name="overrideGroupId">Если задан — коллекция-копия кладется в ЭТУ группу (для дубля
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
                IsDescription = f.IsDescription,
                IsDocFile = f.IsDocFile, IsDocType = f.IsDocType,
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

    /// <summary>Прежний ключ маркера незавершенного дубля (один на базу, без владельца). Новые
    /// сборки его не пишут (см. ConstructorRepository.Pending), а оставленный старой сборкой
    /// убирают при монтировании, как раньше.</summary>
    internal const string PendingDuplicateKey = "duplicate.pending.collection";

    /// <summary>Журнал WAL больше этого — операция между своими пакетами сама переносит его в файл
    /// (<see cref="CheckpointBetweenChunks"/>). Фоновый перенос за непрерывной пакетной записью не
    /// успевает: дубль коллекции на 1 млн записей с датами и текстами вразнобой раздул журнал до
    /// 15 ГБ и кончился «database or disk is full». Перенесенный целиком журнал следующий пакет
    /// пишет с начала.</summary>
    private const long BulkWalCheckpointBytes = 512L << 20;

    private void CheckpointIfWalLarge()
    {
        if (db.IsPostgres || NetworkPath.IsNetwork(db.DbPath)) return;
        long length;
        try { length = new FileInfo(db.DbPath + "-wal").Length; }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (length > BulkWalCheckpointBytes) CheckpointBetweenChunks();
    }

    /// <summary>
    /// Полный дубль коллекции: шелл (коллекция + поля) и маркер незавершенной копии одной
    /// транзакцией, затем данные (<see cref="CopyCollectionData"/>). Прежде шелл фиксировался до
    /// маркера, и выход из программы между ними оставлял пустую «копию». Возвращает шелл.
    /// </summary>
    private CollectionShell DuplicateCollectionCore(int collectionId, IProgress<int>? progress, int? batchSize = null)
    {
        CollectionShell shell;
        PendingCopy pending;
        using (var tx = db.Database.BeginTransaction())
        {
            shell   = CreateCollectionShell(collectionId);
            pending = BeginPending(db, PendingKind.DuplicateCollection, shell.Collection.Id!.Value, singleTx: db.IsPostgres);
            tx.Commit();
        }
        CopyCollectionData(collectionId, shell.Collection.Id!.Value, progress, batchSize, pending, finishPending: true);
        return shell;
    }

    /// <summary>
    /// Скопировать записи и значения из исходной коллекции в уже созданный «скелет» (newCollectionId).
    /// Только bulk-SQL без материализации в EF. Карта полей old→new — по Position (скелет копирует
    /// Position дословно).
    ///  1) Карта записей <c>_dup_rmap(rn, old_id)</c> — ранг записи источника в порядке (Position, Id) —
    ///     и резерв блока id под все записи копии одной короткой транзакцией: новый id записи ранга rn
    ///     = firstNew + rn − 1 (<see cref="ReserveRecordIds"/>). Карта не зависит от правок источника
    ///     по ходу копии.
    ///  2) Пакеты по рангу, у каждого СВОЯ транзакция: записи пакета с зарезервированными id, их
    ///     значения по всем полям (через временную карту полей <c>_dup_fmap</c>) и строки
    ///     <c>calendar_index</c> (1:1 с перемэппингом id, без пересборки).
    ///  3) UI-настройки коллекции — последней транзакцией, вместе со снятием маркера.
    ///
    /// Транзакция на пакет, а не одна на всю копию: одной транзакцией дубль на 2 млн записей раздувал
    /// журнал WAL до 9,4 ГБ, а у общей базы в сетевой папке (журнал DELETE) долгая транзакция,
    /// вытеснив страницы из кеша, берет исключительный замок — до конца копии базу не могли читать ни
    /// интерфейс, ни соседи. Размер пакета — по времени транзакции (<see cref="BatchPacer"/>), журнал
    /// между пакетами переносится в файл, когда вырос (<see cref="CheckpointIfWalLarge"/>).
    /// Записи вставляются в своих пакетах, а не все в первой транзакции: на 10 млн записей она одна
    /// держала бы базу десятки секунд.
    ///
    /// Сбой посреди копии — копия удаляется здесь же; выход из программы — маркер незавершенной копии
    /// (ConstructorRepository.Pending), ее уберет монтирование. <paramref name="pending"/> — маркер,
    /// поставленный вызывающим вместе с шеллом (дубль коллекции — свой, дубль группы — общий на ветку);
    /// без него копия ставит свой. <paramref name="finishPending"/> — снять маркер в последней
    /// транзакции копии (у ветки группы его снимает вызывающий после всех коллекций).
    /// <paramref name="progress"/> — нарастающее число скопированных записей (для процентов в окне).
    /// </summary>
    internal void CopyCollectionData(int srcCollectionId, int newCollectionId,
                                     IProgress<int>? progress = null, int? batchSize = null,
                                     PendingCopy? pending = null, bool finishPending = false,
                                     RelationCopyMap? relationCopy = null)
    {
        var ownPending = pending is null;
        finishPending |= ownPending;
        var ownRelationCopy = relationCopy is null;
        using var localRelationCopy = ownRelationCopy ? BeginRelationCopy() : null;
        relationCopy ??= localRelationCopy!;
        var fieldMap = BuildFieldMap(srcCollectionId, newCollectionId);
        relationCopy.AddCollection(srcCollectionId, newCollectionId, fieldMap);

        // PG: непрерывного блока id не существует (identity-последовательность делят все
        // клиенты) — карта id строится явно через nextval, см. CopyCollectionDataPg.
        if (db.IsPostgres)
        {
            pending ??= BeginPendingInOwnTransaction(PendingKind.DuplicateCollection, newCollectionId, singleTx: true);
            try
            {
                CopyCollectionDataPg(srcCollectionId, newCollectionId, progress, batchSize ?? 10000, finishPending ? pending : null,
                    relationCopy, ownRelationCopy);
            }
            catch (Exception ex) when (ex is not PendingLostException)
            {
                // Транзакция копии откатилась, а шелл и маркер зафиксированы раньше — шелл убираем.
                try { DeleteCollectionData(newCollectionId, log: false, enforceRestrict: false); }
                catch { /* маркер остался — доудалит монтирование */ }
                if (finishPending) DropPending(db, pending);
                throw;
            }
            return;
        }

        // old FieldId → new FieldId по имени (см. BuildFieldMap). Карта нужна и для значений,
        // и для перемэппинга ключей UI-колонок (ширины/порядок/видимость).
        // Временные карты живут на подключении, а копия идет несколькими транзакциями: вне
        // транзакции EF закрывает подключение после каждой команды, и карты пропадали бы.
        db.Database.OpenConnection();
        try
        {
            long recCount, firstNew = 0;
            bool recordsInBatches;
            using (var tx = db.Database.BeginTransaction())
            {
                // Временная карта полей (old_fid → new_fid) — чтобы копировать значения ВСЕХ полей за
                // один INSERT (JOIN _dup_fmap), а не по одному полю; та же карта перемэппливает ключи
                // UI-колонок. Нужна и при пустой коллекции — UI колонок копируем без записей.
                db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_fmap");
                db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
                foreach (var (oldFid, newFid) in fieldMap)
                    db.Database.ExecuteSqlInterpolated($"INSERT INTO _dup_fmap(old_fid, new_fid) VALUES ({oldFid}, {newFid})");

                db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_rmap");
                db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_rmap(rn INTEGER PRIMARY KEY, old_id INTEGER NOT NULL)");
                recCount = db.Database.ExecuteSqlInterpolated($@"
                    INSERT INTO _dup_rmap(rn, old_id)
                    SELECT ROW_NUMBER() OVER (ORDER BY Position, Id), Id
                    FROM db_records WHERE CollectionId = {srcCollectionId}");

                // Блок id под записи копии: записи пойдут своими пакетами. Без AUTOINCREMENT у
                // db_records (файлы не из миграций программы) резерв не держится — тогда, как раньше,
                // все записи одним INSERT здесь же: новые id — непрерывный блок в порядке ранга.
                recordsInBatches = recCount > 0 && RecordIdsAutoincrement();
                if (recordsInBatches) firstNew = ReserveRecordIds(recCount);
                else if (recCount > 0)
                {
                    InsertDuplicatedRecords(newCollectionId, firstNew: null, "1 = 1");
                    firstNew = LastInsertRowid() - recCount + 1;
                }
                pending ??= BeginPending(db, PendingKind.DuplicateCollection, newCollectionId);
                tx.Commit();
            }

            // Проверку внешних ключей на время заливки снимаем: записи, поля и карты созданы этой
            // же операцией, ссылки верны по построению, а проверка — по два поиска в индексах на
            // каждую строку значений и проекции (−15 % времени дубля на 300 тыс. записей). Чужое
            // удаление копии или ее полей посреди заливки (общая база) ловит проверка в начале
            // каждого пакета — внутри его транзакции, так что между проверкой и вставкой их никто
            // не удалит. Прагма меняется только вне транзакции; на любом выходе возвращается (а
            // следующее открытие подключения из пула и так ставит ее заново, см.
            // AppDbContext.ApplyConnectionPragmas).
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=OFF;");

            // Значения и проекция календаря пакетами по рангу, транзакция на пакет. Проекция
            // источника всегда полная (видимость поля — фильтр чтения, см. SetDateFieldVisible),
            // поэтому дубль копирует ее 1:1 с перемэппингом id — прежняя пересборка
            // (RebuildCalendarIndexForCollection) на больших коллекциях добавляла минуты «висящего»
            // на 100% прогресса ПОСЛЕ копии данных. Числа в SQL — long, которые мы формируем сами,
            // поэтому интерполяция в Raw безопасна (как в attached-переносе).
            long processed = 0;
            var  pacer     = new BatchPacer(batchSize, NetworkPath.IsNetwork(db.DbPath));
            if (recCount == 0) progress?.Report(0);
            while (processed < recCount)
            {
                var upTo  = Math.Min(recCount, processed + pacer.Size);
                var slice = $"m.rn > {processed} AND m.rn <= {upTo}";
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using (var tx = db.Database.BeginTransaction())
                {
                    if (db.DbCollections.Count(c => c.Id == newCollectionId) == 0
                        || db.DbFields.Count(f => f.CollectionId == newCollectionId) != fieldMap.Count)
                        throw new InvalidOperationException("Копию коллекции или ее свойства удалили, пока она заполнялась.");
                    if (recordsInBatches) InsertDuplicatedRecords(newCollectionId, firstNew, slice);

                    // EF1002 «возможная SQL-инъекция»: в строку подставляются только целые
                    // идентификаторы и имена таблиц, заданные здесь же в коде. Значений от
                    // пользователя в ней нет. Сырой SQL намеренно: пакетные INSERT…SELECT на тысячах
                    // строк параметрами не выражаются. Записи источника, удаленные по ходу копии
                    // (общая база), из пакета выпадают вместе со своими значениями — значений без
                    // записи-копии не бывает.
                    #pragma warning disable EF1002
                    db.Database.ExecuteSqlRaw($@"
                        INSERT INTO db_field_values (RecordId, FieldId, Value)
                        SELECT {firstNew} + m.rn - 1, fm.new_fid, fv.Value
                        FROM _dup_rmap m
                        JOIN db_records nr ON nr.Id = {firstNew} + m.rn - 1
                        JOIN db_field_values fv ON fv.RecordId = m.old_id
                        JOIN _dup_fmap fm ON fm.old_fid = fv.FieldId
                        WHERE {slice}");
                    db.Database.ExecuteSqlRaw($@"
                        INSERT INTO calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay)
                        SELECT {firstNew} + m.rn - 1, fm.new_fid, {newCollectionId}, ci.StartIso, ci.EndIso, ci.AllDay
                        FROM _dup_rmap m
                        JOIN db_records nr ON nr.Id = {firstNew} + m.rn - 1
                        JOIN calendar_index ci ON ci.RecordId = m.old_id
                        JOIN _dup_fmap fm ON fm.old_fid = ci.FieldId
                        WHERE {slice}");
                    #pragma warning restore EF1002
                    BeatPending(db, pending);
                    tx.Commit();
                }
                pacer.Done(watch.Elapsed);
                CheckpointIfWalLarge();
                processed = upTo;
                progress?.Report((int)processed);
            }
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");

            // UI-настройки коллекции (ширины/порядок/видимость колонок, высоты/видимость строк, режим
            // просмотра, подписи системных колонок) — чтобы дубль был ПОЛНОЙ копией, как перенос в
            // другую БД (см. CopyMovedUi). Поле-/коллекция-level копируем всегда, строки — только при
            // наличии записей.
            using (var tx = db.Database.BeginTransaction())
            {
                CopyDuplicatedUi(srcCollectionId, newCollectionId, firstNew, recCount);
                #pragma warning disable EF1002
                db.Database.ExecuteSqlRaw($@"
                    INSERT INTO _relation_copy_rmap(old_id,new_id)
                    SELECT m.old_id,{firstNew} + m.rn - 1 FROM _dup_rmap m
                    JOIN db_records r ON r.Id = {firstNew} + m.rn - 1");
                #pragma warning restore EF1002
                if (ownRelationCopy) CompleteRelationCopy(relationCopy, "");
                if (finishPending) FinishPending(db, pending);
                else               BeatPending(db, pending);
                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            // Сбой посреди копии (диск, занятая база): недописанная копия не должна остаться под
            // видом настоящей. Не вышло убрать — уберет монтирование по маркеру. Удаление копии
            // держится на каскадах — внешние ключи возвращаем первым делом. Маркер забрала уборка
            // соседа (PendingLostException) — копию удаляет она.
            try { db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;"); } catch { /* вернет следующее открытие */ }
            if (ex is not PendingLostException)
            {
                try { DeleteCollectionData(newCollectionId, log: false, enforceRestrict: false); }
                catch { /* маркер остался — доудалит монтирование */ }
            }
            if (finishPending && ex is not PendingLostException) DropPending(db, pending);
            throw;
        }
        finally
        {
            try { db.Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;"); } catch { /* вернет следующее открытие */ }
            try
            {
                db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_fmap");
                db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_rmap");
            }
            catch { /* временные таблицы уйдут вместе с подключением */ }
            db.Database.CloseConnection();
        }
    }

    // Записи копии ранга из среза карты: firstNew задан — с зарезервированными id (firstNew + rn − 1),
    // null — с id от базы (непрерывный блок в порядке ранга, см. CopyCollectionData).
    private void InsertDuplicatedRecords(int newCollectionId, long? firstNew, string slice)
    {
        var createdAt = DateTime.Now.ToString("o");
        var createdBy = CurrentUser.DisplayName;   // дубль коллекции = новые записи (свой автор/время)
        var idCol = firstNew is null ? "" : "Id, ";
        var idVal = firstNew is long f ? $"{f} + m.rn - 1, " : "";
        #pragma warning disable EF1002   // в строке только числа и имена из кода; значения — параметрами
        db.Database.ExecuteSqlRaw($@"
            INSERT INTO db_records ({idCol}CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Color)
            SELECT {idVal}{newCollectionId}, {{0}}, {{0}}, {{1}}, {{1}}, r.Position, r.StartDt, r.EndDt, r.AllDay, r.Title, r.Color
            FROM _dup_rmap m JOIN db_records r ON r.Id = m.old_id WHERE {slice} ORDER BY m.rn", createdAt, createdBy);
        #pragma warning restore EF1002
    }

    // У db_records ли AUTOINCREMENT: только тогда база не отдаст зарезервированные id другим записям
    // (новый id всегда больше sqlite_sequence). Таблицы из миграций программы — с ним.
    private bool RecordIdsAutoincrement()
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        c.CommandText = "SELECT sql FROM main.sqlite_schema WHERE type = 'table' AND name = 'db_records'";
        return (c.ExecuteScalar() as string)?.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) == true;
    }

    // Зарезервировать count id записей подряд: sqlite_sequence сдвигается за блок, и записи других
    // писателей (интерфейс, соседи по общей базе) получат id после него. Возвращает первый id блока.
    // Звать в транзакции записи.
    private long ReserveRecordIds(long count)
    {
        using var c = db.Database.GetDbConnection().CreateCommand();
        c.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        c.CommandText = @"SELECT MAX(COALESCE((SELECT seq FROM main.sqlite_sequence WHERE name = 'db_records'), 0),
                                     COALESCE((SELECT MAX(Id) FROM main.db_records), 0))";
        var first = Convert.ToInt64(c.ExecuteScalar()) + 1;
        c.CommandText = $"UPDATE main.sqlite_sequence SET seq = {first + count - 1} WHERE name = 'db_records'";
        if (c.ExecuteNonQuery() == 0)
        {
            c.CommandText = $"INSERT INTO main.sqlite_sequence(name, seq) VALUES ('db_records', {first + count - 1})";
            c.ExecuteNonQuery();
        }
        return first;
    }

    // Маркер своей транзакцией — для путей, где вызывающий не поставил его вместе с шеллом.
    private PendingCopy BeginPendingInOwnTransaction(string kind, int target, bool singleTx = false)
    {
        using var tx = db.Database.BeginTransaction();
        var p = BeginPending(db, kind, target, singleTx);
        tx.Commit();
        return p;
    }

    // UI-настройки коллекции при дубле В ТОЙ ЖЕ БД: ключи-id полей перемэппливаются через временную
    // карту _dup_fmap (та же, что для значений), записи — по карте рангов _dup_rmap → firstNew + ранг − 1
    // (новые id непрерывны). Ключи column_meta: числовой id поля → новый, системный
    // «__title__» — как есть. Поле-/коллекция-level копируем всегда; строки (row_heights,
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
            INSERT INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName, Bg, Fg, Width, HAlign, VAlign,
                                     BgAt, FgAt, HAlignAt, VAlignAt)
            SELECT {newCol},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cm.Key END,
                   cm.Position, cm.Visible, cm.Level, cm.DisplayName, cm.Bg, cm.Fg, cm.Width, cm.HAlign, cm.VAlign,
                   cm.BgAt, cm.FgAt, cm.HAlignAt, cm.VAlignAt
            FROM column_meta cm
            LEFT JOIN _dup_fmap fm ON cm.Key GLOB '[0-9]*' AND CAST(cm.Key AS INTEGER) = fm.old_fid
            WHERE cm.CollectionId = {srcCol}");

        if (recCount > 0)
        {
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO ui_row_heights (CollectionId, RecordId, Height)
                SELECT {newCol}, {firstNew} + r.rn - 1, h.Height FROM ui_row_heights h
                JOIN _dup_rmap r ON r.old_id = h.RecordId
                JOIN db_records nr ON nr.Id = {firstNew} + r.rn - 1
                WHERE h.CollectionId = {srcCol}");

            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO record_display (CollectionId, RecordId, Visible, Level, Bg, Fg, HAlign, VAlign,
                                            BgAt, FgAt, HAlignAt, VAlignAt)
                SELECT {newCol}, {firstNew} + r.rn - 1, d.Visible, d.Level, d.Bg, d.Fg, d.HAlign, d.VAlign,
                       d.BgAt, d.FgAt, d.HAlignAt, d.VAlignAt FROM record_display d
                JOIN _dup_rmap r ON r.old_id = d.RecordId
                JOIN db_records nr ON nr.Id = {firstNew} + r.rn - 1
                WHERE d.CollectionId = {srcCol}");

            // Оформление ячеек: запись — по рангу (как выше), колонка — по карте полей
            // (числовой Key = id поля, «__title__» остается как есть — как у column_meta).
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO cell_style (CollectionId, RecordId, Key, Bg, Fg, HAlign, VAlign)
                SELECT {newCol}, {firstNew} + r.rn - 1,
                       CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cs.Key END,
                       cs.Bg, cs.Fg, cs.HAlign, cs.VAlign
                FROM cell_style cs
                JOIN _dup_rmap r ON r.old_id = cs.RecordId
                JOIN db_records nr ON nr.Id = {firstNew} + r.rn - 1
                LEFT JOIN _dup_fmap fm ON cs.Key GLOB '[0-9]*' AND CAST(cs.Key AS INTEGER) = fm.old_fid
                WHERE cs.CollectionId = {srcCol}");
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
                                      IProgress<int>? progress, int batchSize, PendingCopy? finish,
                                      RelationCopyMap relationCopy, bool completeRelations)
    {
        var recCount = db.DbRecords.Count(r => r.CollectionId == srcCollectionId);

        var fieldMap = BuildFieldMap(srcCollectionId, newCollectionId);

        using var tx = db.Database.BeginTransaction();

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_fmap");
        db.Database.ExecuteSqlRaw("CREATE TEMP TABLE _dup_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
        foreach (var (oldFid, newFid) in fieldMap)
            db.Database.ExecuteSqlInterpolated($"INSERT INTO _dup_fmap(old_fid, new_fid) VALUES ({oldFid}, {newFid})");

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_rmap");
        // EF1002 «возможная SQL-инъекция»: в строку подставляются только целые идентификаторы
        // и имена таблиц, заданные здесь же в коде. Значений от пользователя в ней нет — те
        // уходят параметрами. Сырой SQL намеренно: пакетные DELETE и INSERT…SELECT на тысячах
        // строк параметрами не выражаются.
        #pragma warning disable EF1002
        db.Database.ExecuteSqlRaw($@"
            CREATE TEMP TABLE _dup_rmap AS
            SELECT Id AS old_id,
                   nextval(pg_get_serial_sequence('db_records', 'id')) AS new_id,
                   ROW_NUMBER() OVER (ORDER BY Position, Id) AS rn
            FROM db_records WHERE CollectionId = {srcCollectionId}");
        #pragma warning restore EF1002

        if (recCount > 0)
        {
            var createdAt = DateTime.Now.ToString("o");
            var createdBy = CurrentUser.DisplayName;   // дубль коллекции = новые записи (свой автор/время)
            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Color)
                SELECT m.new_id, {newCollectionId}, {createdAt}, {createdAt}, {createdBy}, {createdBy}, r.Position, r.StartDt, r.EndDt, r.AllDay, r.Title, r.Color
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
            INSERT INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName, Bg, Fg, Width, HAlign, VAlign,
                                     BgAt, FgAt, HAlignAt, VAlignAt)
            SELECT {newCollectionId},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cm.Key END,
                   cm.Position, cm.Visible, cm.Level, cm.DisplayName, cm.Bg, cm.Fg, cm.Width, cm.HAlign, cm.VAlign,
                   cm.BgAt, cm.FgAt, cm.HAlignAt, cm.VAlignAt
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
                INSERT INTO record_display (CollectionId, RecordId, Visible, Level, Bg, Fg, HAlign, VAlign,
                                            BgAt, FgAt, HAlignAt, VAlignAt)
                SELECT {newCollectionId}, m.new_id, d.Visible, d.Level, d.Bg, d.Fg, d.HAlign, d.VAlign,
                       d.BgAt, d.FgAt, d.HAlignAt, d.VAlignAt FROM record_display d
                JOIN _dup_rmap m ON m.old_id = d.RecordId WHERE d.CollectionId = {srcCollectionId}");

            db.Database.ExecuteSqlInterpolated($@"
                INSERT INTO cell_style (CollectionId, RecordId, Key, Bg, Fg, HAlign, VAlign)
                SELECT {newCollectionId}, m.new_id,
                       CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cs.Key END,
                       cs.Bg, cs.Fg, cs.HAlign, cs.VAlign
                FROM cell_style cs
                JOIN _dup_rmap m ON m.old_id = cs.RecordId
                LEFT JOIN _dup_fmap fm ON cs.Key = CAST(fm.old_fid AS TEXT)
                WHERE cs.CollectionId = {srcCollectionId}");
        }

        db.Database.ExecuteSqlRaw("INSERT INTO _relation_copy_rmap(old_id,new_id) SELECT old_id,new_id FROM _dup_rmap");
        if (completeRelations) CompleteRelationCopy(relationCopy, "");
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_rmap");
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_fmap");
        // Маркер снимается той же транзакцией, что фиксирует копию: на PG она одна на всю копию.
        if (finish is not null) FinishPending(db, finish);
        tx.Commit();
    }

    // Имена уникальны в коллекции и копируются дословно. Position не является ключом:
    // одинаковые позиции допустимы, например после импорта или параллельной перестановки.
    private Dictionary<int, int> BuildFieldMap(int srcCollectionId, int newCollectionId)
    {
        var oldFields = db.DbFields.AsNoTracking().Where(f => f.CollectionId == srcCollectionId)
            .Select(f => new { f.Id, f.Name }).ToList();
        var newByName = db.DbFields.AsNoTracking().Where(f => f.CollectionId == newCollectionId)
            .Select(f => new { f.Id, f.Name }).ToDictionary(f => f.Name, f => f.Id, StringComparer.Ordinal);
        var fieldMap = new Dictionary<int, int>(oldFields.Count);
        foreach (var of in oldFields)
        {
            if (!newByName.TryGetValue(of.Name, out var newFid))
                throw new InvalidOperationException("Состав свойств источника изменился во время копирования коллекции.");
            fieldMap[of.Id] = newFid;
        }
        return fieldMap;
    }

    /// <summary>
    /// Размер пакета массовой копии по времени транзакции. Мелкие пакеты на настоящих данных (даты
    /// и тексты вразнобой с порядком записей) дорогие: каждая фиксация переписывает почти всю
    /// задетую область глобальных индексов (IX_db_field_values_FieldId_Value, индексы
    /// calendar_index), и на 300 тыс. записей пакеты по 10 тыс. шли 77 с и 3,5 ГБ журнала против
    /// 37 с и 0,7 ГБ по 100 тыс. Огромная транзакция тоже не годится: пока она идет, запись в базу ждет
    /// (интерфейс, соседи по общей базе — до busy_timeout 10–15 с, потом отказ). Поэтому пакет
    /// растет, пока транзакция укладывается в целевое время, и сжимается, когда нет. Цель у
    /// локальной базы (WAL) — 8 с: чтение пишущая транзакция там не держит, а запись ждет меньше
    /// своего таймаута (10 с); у общей базы в сетевой папке (журнал DELETE) — 4 с: там большая
    /// транзакция, вытеснив страницы из кеша, запирает и чтение соседей. Замер (300 тыс. записей
    /// вразнобой, локально): цель 4 с — 45 с, 8 с — 35,5 с, одна транзакция — 34 с.
    /// Заданный явно размер (тесты, стенды) — постоянный.
    /// </summary>
    private sealed class BatchPacer(int? fixedSize, bool shared)
    {
        private const int Start = 20_000, Min = 5_000, Max = 250_000;

        private readonly TimeSpan _target = TimeSpan.FromSeconds(shared ? 4 : 8);

        public int Size { get; private set; } = fixedSize ?? Start;

        public void Done(TimeSpan elapsed)
        {
            if (fixedSize is not null) return;
            var ratio = _target.TotalMilliseconds / Math.Max(1, elapsed.TotalMilliseconds);
            Size = (int)Math.Clamp(Size * Math.Clamp(ratio, 0.5, 2.0), Min, Max);
        }
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
        var shell = repo.DuplicateCollectionCore(srcCollectionId, progress);
        repo.LogNow("duplicate", "collection", shell.Collection.Name, count: shell.SourceRecordCount);
        return shell.Collection.Id ?? 0;
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
        return new ConstructorRepository(ctx) { Permissions = Permissions }.ImportStreamedCollection(collectionName, groupId, schema, rows, ct, rowsWritten);
    }

    // ── Экспорт таблиц (Core/Exporters, см. docs/features/import-export.md) ──
    //
    // Экспорт — чистое чтение: все методы работают на ОТДЕЛЬНЫХ подключениях (вызовы
    // идут с фонового потока оркестрации, общий Context трогать нельзя — правило
    // однопоточности), WAL допускает такое чтение рядом с любыми операциями. Demand не
    // зовется: изолированные клоны прав не несут (см. Permissions), проверку
    // ViewCollection делает оркестрация по кешу прав до начала чтения.

    /// <summary>
    /// Колонки экспорта в порядке таблицы: сохраненный column_order, иначе дефолт как у
    /// TableView.EffectiveColumnPosition (Название=1000, поле=3000+Position).
    /// markdown-поля исключаются (они не колонки таблицы). includeHidden=false — только
    /// видимые колонки (column_display; отсутствие ключа = видима). Подпись «Названия» —
    /// из column_meta (дефолт «Название»); «Описание» — обычное поле и идет наравне с ними.
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
            : 3000 + fieldPos;

        var cols = new List<(ExportColumn Col, int Pos)>
        {
            (new ExportColumn(TitleKey, names.GetValueOrDefault(TitleKey, "Название"), null, "text"), Pos(TitleKey, 0)),
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
    /// Значения — string[] в порядке columns: Название → Title, поле → значение
    /// (отсутствующее → ""). rowsRead — нарастающий итог раз в страницу.
    /// </summary>
    public IEnumerable<string[]> ReadExportRowsIsolated(int collectionId,
        IReadOnlyList<ExportColumn> columns, bool excludeHidden, CancellationToken ct,
        IProgress<long>? rowsRead = null, int pageSize = 2000, bool portableRelations = false)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        var repo = new ConstructorRepository(ctx) { Permissions = Permissions };

        long total = 0;
        int? afterPos = null, afterId = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = repo.GetRecordsPageAfter(collectionId, afterPos, afterId, pageSize, excludeHidden);
            if (page.Count == 0) yield break;

            var pageIds = page.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToList();
            var values = repo.GetFieldValuesBatch(pageIds);
            var relationLinks = columns.Where(c => c.FieldType == "relation" && c.FieldId.HasValue)
                .ToDictionary(c => c.FieldId!.Value,
                    c => repo.GetRelationLinksBatch(c.FieldId!.Value, pageIds));
            var relationFieldIds = relationLinks.Keys.ToList();
            var portableEdges = portableRelations
                ? ctx.DbRecordLinks.AsNoTracking().Where(l => relationFieldIds.Contains(l.FieldId) && pageIds.Contains(l.FromRecordId))
                    .OrderBy(l => l.Position).ThenBy(l => l.Id).ToList()
                    .GroupBy(l => (l.FromRecordId, l.FieldId)).ToDictionary(g => g.Key,
                        g => g.Select(l => new RelationTarget(l.TargetDbId, l.ToRecordId)).ToList())
                : null;

            foreach (var r in page)
            {
                var vals = r.Id is int rid ? values.GetValueOrDefault(rid) : null;
                var row  = new string[columns.Count + (portableRelations ? 1 : 0)];
                for (var i = 0; i < columns.Count; i++)
                {
                    var c = columns[i];
                    row[i] = c.FieldType == "relation" && c.FieldId is int relationId
                           ? string.Join(RelationConfig.FromConfig(c.ConfigJson)?.SeparatorString ?? ", ",
                               relationLinks[relationId].GetValueOrDefault(r.Id!.Value)?.Select(l => l.Display) ?? [])
                           : c.Key == TitleKey ? r.Title
                           : c.FieldId is int fid && vals is not null ? vals.GetValueOrDefault(fid, "") : "";
                }
                if (portableRelations)
                {
                    var portableValues = columns.Where(c => c.FieldId.HasValue && c.FieldType is not ("relation" or "lookup" or "rollup"))
                        .ToDictionary(c => c.FieldId!.Value, c => vals?.GetValueOrDefault(c.FieldId!.Value, "") ?? "");
                    var portableLinks = relationLinks.ToDictionary(p => p.Key,
                        p => portableEdges?.GetValueOrDefault((r.Id!.Value, p.Key)) ?? []);
                    row[^1] = System.Text.Json.JsonSerializer.Serialize(new RelationPortableRow(r.Id!.Value,
                        columns.Any(c => c.Key == TitleKey) ? r.Title : "", portableValues, portableLinks));
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
    // Из-за изоляции id (DbId, Id) коллекция/группа физически живут в своем .db-файле,
    // поэтому «перенос в другую БД» = глубокая копия снимка в целевой репозиторий (с
    // перенумерацией id) + удаление из исходного. Переносится все: поля, записи, значения
    // и UI-настройки коллекции (ширины/порядок/видимость колонок, высоты строк, отображение
    // записей, подписи системных колонок) — ключи-id (поле/запись) перемэппливаются на новые.

    // Снимок коллекции: метаданные + поля + записи со значениями + UI-настройки.
    // Ключи значений и UI — СТАРЫЕ id поля/записи; при импорте перемэппливаются.
    public sealed record CollectionSnapshot(DbCollection Collection, List<DbField> Fields,
                                            List<RecordSnapshot> Records, UiSnapshot Ui)
    {
        public string? SourceDatabaseIdentity { get; init; }
        public int SourceDbId { get; init; }
        public Dictionary<int, string> DatabaseIdentities { get; init; } = [];
        public List<RelationSnapshotEdge> Links { get; init; } = [];
    }
    public sealed record RelationSnapshotEdge(int FieldId, int FromRecordId, RelationTarget Target, int Position);
    public sealed record RecordSnapshot(DbRecord Record, Dictionary<int, string> ValuesByOldFieldId);

    // UI-настройки коллекции для переноса (отвязанные от контекста-источника значения).
    public sealed record UiSnapshot(
        List<(int FieldId, int Width)>                        ColWidths,
        List<(int RecordId, int Height)>                      RowHeights,
        (bool ColResize, bool RowResize, string ViewMode)?    Settings,
        List<(string Key, int? Position, bool? Visible, int? Level, string? DisplayName, string? Bg, string? Fg, int? Width,
              string? HAlign, string? VAlign, StyleStamps At)> ColumnMeta,
        List<(int RecordId, bool Visible, int Level, string? Bg, string? Fg, string? HAlign, string? VAlign,
              StyleStamps At)> RecordDisplay,
        List<(int RecordId, string Key, string? Bg, string? Fg, string? HAlign, string? VAlign)> CellStyles);

    // Снимок группы со всей вложенной веткой (подгруппы + коллекции).
    public sealed record GroupSnapshot(DbGroup Group, List<GroupSnapshot> Subgroups, List<CollectionSnapshot> Collections);

    /// <summary>Снять коллекцию (с полями, записями и значениями) для переноса в другую БД.</summary>
    public CollectionSnapshot ExportCollection(int collectionId)
    {
        Demand(PermAction.ViewCollection, collectionId);
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

        var fieldIds = fields.Where(f => f.FieldType == "relation").Select(f => f.Id!.Value).ToList();
        var links = db.DbRecordLinks.AsNoTracking().Where(l => fieldIds.Contains(l.FieldId))
            .OrderBy(l => l.Position).ThenBy(l => l.Id).ToList()
            .Select(l => new RelationSnapshotEdge(l.FieldId, l.FromRecordId, new(l.TargetDbId, l.ToRecordId), l.Position)).ToList();
        var referenced = links.Select(l => l.Target).Concat(fields.Select(f => RelationConfig.FromConfig(f.ConfigJson))
            .Where(c => c is not null).Select(c => new RelationTarget(c!.TargetDbId, 0)));
        return new CollectionSnapshot(col, fields, recSnaps, ExportUi(collectionId))
        {
            SourceDatabaseIdentity = RelationDatabaseIdentity, SourceDbId = RelationDbResolver.DbIdFor(this),
            DatabaseIdentities = GetRelationDatabaseIdentities(referenced), Links = links,
        };
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
                             .Select(m => (m.Key, m.Position, m.Visible, m.Level, m.DisplayName, m.Bg, m.Fg, m.Width,
                                           m.HAlign, m.VAlign, StampsOf(m))).ToList(),
            RecordDisplay: db.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == collectionId).ToList()
                             .Select(d => (d.RecordId, d.Visible, d.Level, d.Bg, d.Fg, d.HAlign, d.VAlign, StampsOf(d))).ToList(),
            CellStyles:    db.CellStyles.AsNoTracking().Where(c => c.CollectionId == collectionId).ToList()
                             .Select(c => (c.RecordId, c.Key, c.Bg, c.Fg, c.HAlign, c.VAlign)).ToList());
    }

    /// <summary>Импортировать снимок коллекции под группу targetGroupId — атомарно (одна транзакция). Возвращает новую коллекцию.</summary>
    public DbCollection ImportCollection(CollectionSnapshot snap, int? targetGroupId)
    {
        Demand(PermAction.CreateCollection);
        var state = PrepareRelationSnapshotImport([snap]);
        using var tx = db.Database.BeginTransaction();
        try
        {
            var result = ImportCollectionCore(snap, targetGroupId, state);
            CompleteRelationSnapshotImport(state);
            foreach (var collectionId in state.Collections.Values) NotifyRelationDataChanged(collectionId);
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private DbCollection ImportCollectionCore(CollectionSnapshot snap, int? targetGroupId, RelationSnapshotImport state)
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
                IsDescription = f.IsDescription,
                IsDocFile = f.IsDocFile, IsDocType = f.IsDocType,
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
                AllDay = r.AllDay, Title = r.Title,
                Color = r.Color,
            };
            db.DbRecords.Add(nr);
            db.SaveChanges();
            if (r.Id is int oldRid) recordMap[oldRid] = nr.Id;

            foreach (var (oldFid, val) in rs.ValuesByOldFieldId)
                if (fieldMap.TryGetValue(oldFid, out var nfid)
                    && snap.Fields.First(f => f.Id == oldFid).FieldType is not ("relation" or "lookup" or "rollup"))
                    db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = nr.Id, FieldId = nfid, Value = val });
            db.SaveChanges();
        }

        state.Collections.Add(snap.Collection.Id!.Value, copy.Id);
        foreach (var pair in fieldMap) state.Fields.Add(pair.Key, pair.Value);
        foreach (var pair in recordMap) state.Records.Add(pair.Key, pair.Value);
        ImportUi(snap.Ui, copy.Id, fieldMap, recordMap);

        return new DbCollection
        {
            Id = copy.Id, Name = copy.Name, GroupId = copy.GroupId,
            Position = copy.Position, Icon = copy.Icon, Color = copy.Color,
        };
    }

    // Восстановить UI-настройки в новой коллекции: id полей/записей — через карты,
    // системный ключ «__title__» и настройки коллекции — как есть.
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

        foreach (var (key, pos, vis, lvl, name, bg, fg, width, hAlign, vAlign, at) in ui.ColumnMeta)
        {
            var meta = new ColumnMetaEntity
            {
                CollectionId = newCollectionId, Key = RemapColKey(key, fieldMap),
                Position = pos, Visible = vis, Level = lvl, DisplayName = name, Bg = bg, Fg = fg,
                Width = width, HAlign = hAlign, VAlign = vAlign,
            };
            SetStamps(meta, at);
            db.ColumnMeta.Add(meta);
        }

        foreach (var (oldRid, vis, lvl, bg, fg, hAlign, vAlign, at) in ui.RecordDisplay)
        {
            if (!recordMap.TryGetValue(oldRid, out var nrid)) continue;
            var display = new RecordDisplayEntity
            {
                CollectionId = newCollectionId, RecordId = nrid, Visible = vis, Level = lvl,
                Bg = bg, Fg = fg, HAlign = hAlign, VAlign = vAlign,
            };
            SetStamps(display, at);
            db.RecordDisplay.Add(display);
        }

        foreach (var (oldRid, key, bg, fg, hAlign, vAlign) in ui.CellStyles)
            if (recordMap.TryGetValue(oldRid, out var nrid))
                db.CellStyles.Add(new CellStyleEntity
                {
                    CollectionId = newCollectionId, RecordId = nrid, Key = RemapColKey(key, fieldMap),
                    Bg = bg, Fg = fg, HAlign = hAlign, VAlign = vAlign,
                });

        db.SaveChanges();
    }

    // Ключ колонки: системный «__title__» оставляем как есть; числовой id поля — на новый.
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
        Demand(PermAction.CreateCollection);
        var state = PrepareRelationSnapshotImport(RelationGroupCollections(snap));
        using var tx = db.Database.BeginTransaction();
        try
        {
            var result = ImportGroupCore(snap, targetParentId, state);
            CompleteRelationSnapshotImport(state);
            foreach (var collectionId in state.Collections.Values) NotifyRelationDataChanged(collectionId);
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private DbGroup ImportGroupCore(GroupSnapshot snap, int? targetParentId, RelationSnapshotImport state)
    {
        var maxPos = db.DbGroups.Where(x => x.ParentId == targetParentId).Select(x => (int?)x.Position).Max() ?? 0;
        var copy = new DbGroupEntity { Name = snap.Group.Name, ParentId = targetParentId, Position = maxPos + 1000 };
        db.DbGroups.Add(copy);
        db.SaveChanges();

        // Внутри уже открытой транзакции — зовем *Core (без вложенной транзакции).
        foreach (var sub in snap.Subgroups)   ImportGroupCore(sub, copy.Id, state);
        foreach (var col in snap.Collections) ImportCollectionCore(col, copy.Id, state);

        return new DbGroup { Id = copy.Id, Name = copy.Name, ParentId = copy.ParentId, Position = copy.Position };
    }

    // ── Перенос целиком: export → import (в транзакции) → delete; откат копии при сбое ──
    //
    // import уже атомарен (транзакция целевой БД) — частичной копии не бывает. Если удаление
    // из исходной упадет после копии, удаляем копию из целевой (компенсация) и возвращаемся к
    // исходному состоянию. Дубль возможен лишь при двойном сбое (исчезающе редко) — данные целы.

    /// <summary>Перенести коллекцию из source в target (с именем newName). Бросает при сбое — после отката копии.</summary>
    public static DbCollection MoveCollection(ConstructorRepository source, ConstructorRepository target,
                                              int collectionId, int? targetGroupId, string newName)
    {
        // Перенос = изъятие из источника + создание в целевой: проверяем права ОБЕИХ баз.
        source.Demand(PermAction.DeleteCollection, collectionId);
        target.Demand(PermAction.CreateCollection);
        target.DemandMoveFromAllowed(source);
        source.EnsureDeleteAllowedForCollections([collectionId], move: true);   // до копии: откатывать нечего
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
        target.DemandMoveFromAllowed(source);
        source.EnsureDeleteAllowedForCollections(source.CollectionIdsInGroup(groupId), move: true);
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
    // Старый MoveCollection материализует ВСЮ коллекцию в память (ExportCollection) и пишет ее в
    // целевую БД через EF по одной записи (ImportCollection) — на больших коллекциях это минуты
    // (как был импорт до перевода на bulk). Здесь то же, что дубль, только источник — присоединенный
    // файл: на подключении ЦЕЛЕВОЙ БД делаем `ATTACH src`, копируем шелл/записи/значения/UI bulk-ом
    // (id полей и записей перемэппливаются) пакетами, у каждого своя транзакция приемника, затем
    // удаляем коллекцию из источника на ОТДЕЛЬНОМ подключении (DeleteMovedSource). Копия и удаление
    // раздельны (в WAL межбазовая транзакция не атомарна).
    //
    // Прежде вся копия шла одной транзакцией приемника: на миллионах записей она держала запись в
    // целевую базу минутами (интерфейс и соседи по общей базе получали отказ по таймауту) и
    // раздувала журнал на весь объем. Теперь пакеты — по времени транзакции (BatchPacer), как у
    // дубля, под маркером незавершенной копии (ConstructorRepository.Pending). Источник на все время
    // копии заморожен отдельным подключением (WithAttachedSource) — как раньше его замораживала
    // единая транзакция.

    /// <summary>
    /// Перенести коллекцию в ДРУГУЮ БД быстро (bulk-SQL через ATTACH) на ОТДЕЛЬНЫХ подключениях —
    /// не трогает общий Context (см. правило об общем AppDbContext), поэтому безопасно из Task.Run.
    /// <c>this</c> — репозиторий ЦЕЛЕВОЙ БД (куда переносим); источник задается путем к его .db-файлу.
    /// <paramref name="progress"/> — нарастающее число перенесенных записей (для % в окне). Возвращает
    /// id новой коллекции в целевой БД (0 — если копировать нечего). Бросает при сбое (после отката копии).
    /// <paramref name="batchSize"/> — постоянный размер пакета (тесты, стенды); без него — по времени.
    /// </summary>
    public int MoveCollectionIsolated(string sourceDbPath, int srcCollectionId, int? targetGroupId,
                                      IProgress<int>? progress = null, int? batchSize = null,
                                      int sourceDbId = 0, int targetDbId = 0)
    {
        // this — ЦЕЛЕВАЯ база: проверяем право создавать в ней. Право удаления в источнике
        // проверяет UI по репозиторию источника (его PermissionService здесь недоступен).
        Demand(PermAction.CreateCollection);

        // Из закрытого контура в менее строгую базу данные не переезжают: иначе запрет на
        // исходящие обходится в два приема — перенес и выгрузил уже оттуда.
        // См. ConstructorRepository.Confidentiality.cs.
        DemandMoveFromAllowed(sourceDbPath);
        EnsureMoveAllowed(sourceDbPath, repo => [srcCollectionId]);

        // Пара с PostgreSQL (или PG→PG): ATTACH существует только у SQLite — переносим
        // generic-путем на двух независимых подключениях (см. ConstructorRepository.CrossMove).
        if (db.IsPostgres || PgConnectionInfo.IsPostgresConnectionString(sourceDbPath))
            return MoveCollectionGeneric(sourceDbPath, srcCollectionId, targetGroupId, progress, batchSize, sourceDbId, targetDbId);

        using var targetCtx = new AppDbContext(db.DbPath);
        targetCtx.ApplyBulkPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);
        using var relationCopy = target.BeginRelationCopy(sourceDbId, targetDbId, crossDatabase: true);

        // Копия под маркером; в ее последней транзакции — правка конфигов полей-связей (перенесены
        // дословно — переписываем под целевую базу согласованно с переписыванием ребер в копии:
        // внешняя цель → межбазовая на источник, само-связь → на новую коллекцию), уникальное имя
        // («(копия)», если в целевой группе уже есть одноименная) и снятие маркера.
        var state = new AttachedCopy();
        long recCount;
        try
        {
            recCount = target.WithAttachedSource(sourceDbPath, conn =>
                target.CopyOneCollectionFromAttached(conn, srcCollectionId, targetGroupId, progress, 0, batchSize, state,
                    newColId =>
                    {
                        target.CompleteRelationCopy(relationCopy, "src.");
                        target.UniquifyCollectionName(newColId, targetGroupId);
                        FinishPending(targetCtx, state.Pending!);
                    },
                    relationCopy).RecCount);
        }
        catch (Exception ex) when (ex is not PendingLostException)
        {
            target.DropFailedCopy(state, group: false);
            throw;
        }
        if (state.NewId == 0) return 0;

        // Прогресс покрывает ОБЕ фазы: копию (0..recCount) и удаление исходника (recCount..2·recCount).
        // Журнал: в источнике «удалил» пишет DeleteCollectionData, в целевой — «перенес» ниже.
        DeleteMovedSource(target, sourceDbPath, srcCollectionId, group: false, state.NewId, progress, recCount, batchSize);
        LogMoved(target, "collection", target.CollectionNameOf(state.NewId) ?? "", recCount);
        return state.NewId;
    }

    /// <summary>
    /// Перенести группу СО ВСЕЙ веткой в ДРУГУЮ БД быстро (bulk-SQL через ATTACH). <c>this</c> —
    /// репозиторий ЦЕЛЕВОЙ БД; источник задается путем к его .db-файлу. Структуру групп и коллекции
    /// ветки копируем пакетами под одним маркером на ветку, затем удаляем ветку из источника на
    /// ОТДЕЛЬНОМ подключении (как в MoveCollectionIsolated). <paramref name="progress"/> —
    /// нарастающее число перенесенных записей. Возвращает id новой верхней группы (0 — нечего копировать).
    /// </summary>
    public int MoveGroupIsolated(string sourceDbPath, int srcGroupId, int? targetParentId,
                                 IProgress<int>? progress = null, int? batchSize = null,
                                 int sourceDbId = 0, int targetDbId = 0)
    {
        // this — ЦЕЛЕВАЯ база (см. MoveCollectionIsolated: источник проверяет UI).
        Demand(PermAction.CreateCollection);

        // Тот же запрет, что и у переноса коллекции: из закрытого контура наружу нельзя.
        DemandMoveFromAllowed(sourceDbPath);
        EnsureMoveAllowed(sourceDbPath, repo => repo.CollectionIdsInGroup(srcGroupId));

        // Пара с PostgreSQL (или PG→PG): generic-путь вместо ATTACH (см. CrossMove).
        if (db.IsPostgres || PgConnectionInfo.IsPostgresConnectionString(sourceDbPath))
            return MoveGroupGeneric(sourceDbPath, srcGroupId, targetParentId, progress, batchSize, sourceDbId, targetDbId);

        using var targetCtx = new AppDbContext(db.DbPath);
        targetCtx.ApplyBulkPerformancePragmas();
        var target = new ConstructorRepository(targetCtx);

        var state = new AttachedCopy();
        long total;
        try
        {
            total = target.CopyGroupFromAttached(sourceDbPath, srcGroupId, targetParentId, progress, batchSize,
                                                 state, sourceDbId, targetDbId);
        }
        catch (Exception ex) when (ex is not PendingLostException)
        {
            target.DropFailedCopy(state, group: true);
            throw;
        }
        if (state.NewId == 0) return 0;

        // Прогресс покрывает ОБЕ фазы: копию ветки (0..total) и удаление исходной ветки (total..2·total).
        DeleteMovedSource(target, sourceDbPath, srcGroupId, group: true, state.NewId, progress, total, batchSize);
        var movedName = targetCtx.DbGroups.Where(g => g.Id == state.NewId)
                                          .Select(g => g.Name).FirstOrDefault() ?? "";
        LogMoved(target, "group", movedName, total);
        return state.NewId;
    }

    // Ход копии из присоединенной базы — для уборки после сбоя, когда источник уже отсоединен:
    // что создано в приемнике (коллекция или корень ветки) и под каким маркером.
    private sealed class AttachedCopy
    {
        public int          NewId;
        public PendingCopy? Pending;
    }

    // Сбой копии: недописанная копия не остается под видом настоящей. Зовется после отсоединения
    // источника — с присоединенным, замороженным источником транзакции удаления ждали бы его замка.
    // Не вышло — маркер остался, доудалит уборка при монтировании.
    private void DropFailedCopy(AttachedCopy state, bool group)
    {
        if (state.NewId == 0) return;
        try
        {
            if (group) DeleteGroupData(state.NewId, log: false, enforceRestrict: false);
            else       DeleteCollectionData(state.NewId, log: false, enforceRestrict: false);
        }
        catch { /* маркер остался — доудалит уборка */ }
        DropPending(db, state.Pending);
    }

    // Перенос группы из присоединенной БД: первая транзакция — структура групп (CloneGroupTreeAttached,
    // имена через INSERT…SELECT, без материализации в C#) и маркер ветки, затем коллекции ветки по
    // очереди пакетами с общим % (progressBase накапливает перенесенные записи), последняя — правка
    // конфигов связей, имя корня и снятие маркера. Возвращает число перенесенных записей ветки.
    private long CopyGroupFromAttached(string sourceDbPath, int srcGroupId, int? targetParentId,
                                       IProgress<int>? progress, int? batchSize, AttachedCopy state,
                                       int sourceDbId = 0, int targetDbId = 0)
    {
        using var relationCopy = BeginRelationCopy(sourceDbId, targetDbId, crossDatabase: true);
        return WithAttachedSource(sourceDbPath, conn =>
        {
            var map = new Dictionary<int, int>();   // старый id группы → новый
            List<(int A, int B)> cols = [];
            int root = 0;
            PendingCopy? pending = null;
            InAttachedTx(conn, tx =>
            {
                if (ScalarLong(conn, tx, $"SELECT COUNT(*) FROM src.db_groups WHERE Id = {srcGroupId}") == 0) return;
                root    = CloneGroupTreeAttached(conn, tx, srcGroupId, targetParentId, map);
                pending = BeginPending(db, PendingKind.MoveGroup, root);
                // Коллекции ветки (GroupId в наборе старых id).
                var oldGroupIds = string.Join(",", map.Keys);
                cols = ReadIntPairs(conn, tx,
                    $"SELECT Id, GroupId FROM src.db_collections WHERE GroupId IN ({oldGroupIds}) ORDER BY GroupId, Position");
            });
            if (root == 0) return 0L;   // группы в источнике нет — копировать нечего
            state.NewId   = root;
            state.Pending = pending;

            // Карта всей ветки собирается до переноса ребер: цель может находиться в коллекции,
            // которая будет скопирована последней.
            long basis = 0;
            foreach (var (colId, oldGroup) in cols)
            {
                var (_, rc) = CopyOneCollectionFromAttached(conn, colId, map[oldGroup], progress, basis, batchSize,
                                                           state, finalize: null, relationCopy);
                basis += rc;
            }

            // Имя корня ветки копируется как есть → если на целевом уровне уже есть одноименная
            // группа, даем «(копия)». Вложенные группы/коллекции легли в новую (пустую) ветку.
            InAttachedTx(conn, _ =>
            {
                CompleteRelationCopy(relationCopy, "src.");
                UniquifyGroupName(root, targetParentId);
                FinishPending(db, state.Pending!);
            });
            return basis;   // суммарно перенесенных записей ветки (для прогресса удаления)
        });
    }

    // Рекурсивно копирует структуру групп из src в main (INSERT…SELECT — имя остается в SQL, без
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
    // открытым на все время: ATTACH живет на подключении. Путь источника — через параметр (в пути
    // пользователя пробелы и спецсимволы). Используется и переносом коллекции, и переносом группы.
    //
    // Источник на все это время заморожен: пишущая транзакция ОТДЕЛЬНОГО подключения к нему (holder)
    // не дает никому писать, пока копия идет пакетами своих транзакций приемника. Ранги записей
    // источника не меняются между пакетами, и не теряется запись, добавленная посреди копии
    // (удаление источника после копии стерло бы ее вместе с перенесенными). Читать источник holder
    // не мешает ни нам, ни соседям. Поэтому транзакции приемника — отложенные (InAttachedTx):
    // IMMEDIATE брал бы замок записи всех присоединенных баз и ждал бы holder.
    private T WithAttachedSource<T>(string sourceDbPath, Func<DbConnection, T> body)
    {
        using var holderCtx = new AppDbContext(sourceDbPath);
        using var holder    = holderCtx.Database.BeginTransaction();

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

    // Транзакция приемника при присоединенном источнике. Отложенная (BEGIN DEFERRED): IMMEDIATE взял
    // бы замок записи и у src, а его держит holder (WithAttachedSource). Замок записи main берется
    // первым же оператором — пустой записью в main: иначе первое чтение начало бы снимок, и чужая
    // запись до нашей вставки обернулась бы отказом SQLITE_BUSY_SNAPSHOT без ожидания. Транзакция
    // видна EF (UseTransaction) — маркер, имя и конфиги связей пишутся через контекст в ней же.
    private void InAttachedTx(DbConnection conn, Action<DbTransaction> body)
    {
        var tx = ((SqliteConnection)conn).BeginTransaction(deferred: true);
        try
        {
            Exec(conn, tx, "UPDATE main.db_meta SET Value = Value WHERE 0");
            db.Database.UseTransaction(tx);
            body(tx);
            tx.Commit();
        }
        catch
        {
            // Откат после сбоя записи (диск, память) SQLite мог уже сделать сам — тогда ROLLBACK
            // бросил бы «no transaction is active» и спрятал бы настоящую ошибку.
            try { tx.Rollback(); } catch { /* уже откатилась */ }
            throw;
        }
        finally
        {
            db.Database.UseTransaction(null);
            try { tx.Dispose(); } catch { /* уже откатилась */ }
        }
    }

    // Копия ОДНОЙ коллекции из src в main пакетами, у каждого своя транзакция приемника (InAttachedTx).
    //  1) Шелл (коллекция + поля), карта полей old→new по Position (temp _move_fmap), карта рангов
    //     записей источника в порядке (Position, Id) (temp _move_rmap) и резерв блока id
    //     (ReserveRecordIds): новый id записи ранга rn = firstNew + rn − 1. Маркер — в этой же
    //     транзакции: свой (PendingKind.MoveCollection), если у state его еще нет (перенос коллекции),
    //     иначе маркер ветки (перенос группы).
    //  2) Пакеты по рангу: записи, значения по всем полям (через _move_fmap), строки calendar_index
    //     (проекция источника всегда полная — копируем 1:1 с перемэппингом id вместо пересборки с
    //     парсингом дат) и отметка владельца маркера.
    //  3) Последняя транзакция: UI-настройки, связи, finalize (у переноса коллекции — конфиги связей,
    //     имя и снятие маркера).
    // progressBase — сколько записей уже перенесено ранее (для общего % по группе). Возвращает (id
    // новой коллекции, число ее записей). Числа в SQL — int/long, которые мы формируем сами (не
    // пользовательский ввод), поэтому интерполяция безопасна.
    private (int NewColId, long RecCount) CopyOneCollectionFromAttached(DbConnection conn,
        int srcCollectionId, int? targetGroupId, IProgress<int>? progress, long progressBase, int? batchSize,
        AttachedCopy state, Action<int>? finalize, RelationCopyMap relationCopy)
    {
        int  newColId = 0, fieldCount = 0;
        long recCount = 0, firstNew = 0;
        var  recordsInBatches = false;
        try
        {
            InAttachedTx(conn, tx =>
            {
                // Коллекции в источнике нет — копировать нечего (last_insert_rowid ниже вернул бы
                // id чужой вставки этого подключения).
                if (ScalarLong(conn, tx, $"SELECT COUNT(*) FROM src.db_collections WHERE Id = {srcCollectionId}") == 0) return;
                recCount = ScalarLong(conn, tx, $"SELECT COUNT(*) FROM src.db_records WHERE CollectionId = {srcCollectionId}");
                var maxPos   = ScalarLong(conn, tx, "SELECT COALESCE(MAX(Position), 0) FROM main.db_collections");
                var groupSql = targetGroupId is int gid ? gid.ToString() : "NULL";

                // Строка коллекции (имя/иконку/цвет переносим как есть; группа — целевая).
                Exec(conn, tx, $@"
                    INSERT INTO main.db_collections (Name, GroupId, Position, Icon, Color)
                    SELECT Name, {groupSql}, {maxPos + 1000}, Icon, Color FROM src.db_collections WHERE Id = {srcCollectionId}");
                newColId = (int)ScalarLong(conn, tx, "SELECT last_insert_rowid()");

                // Поля копируем дословно; карта old→new использует уникальное имя, а не позицию.
                Exec(conn, tx, $@"
                    INSERT INTO main.db_fields (CollectionId, Name, FieldType, ConfigJson, Position, IsCalStart, IsCalEnd, IsSystem, IsRequired, IsTaskExec, IsTaskAssigner, IsTaskStatus, IsDescription, IsDocFile, IsDocType)
                    SELECT {newColId}, Name, FieldType, ConfigJson, Position, IsCalStart, IsCalEnd, IsSystem, IsRequired, IsTaskExec, IsTaskAssigner, IsTaskStatus, IsDescription, IsDocFile, IsDocType
                    FROM src.db_fields WHERE CollectionId = {srcCollectionId} ORDER BY Id");
                fieldCount = (int)ScalarLong(conn, tx, $"SELECT COUNT(*) FROM main.db_fields WHERE CollectionId = {newColId}");

                Exec(conn, tx, "DROP TABLE IF EXISTS temp._move_fmap");
                Exec(conn, tx, "CREATE TEMP TABLE _move_fmap(old_fid INTEGER PRIMARY KEY, new_fid INTEGER)");
                Exec(conn, tx, $@"
                    INSERT INTO _move_fmap(old_fid, new_fid)
                    SELECT s.Id, n.Id FROM src.db_fields s
                    JOIN main.db_fields n ON n.CollectionId = {newColId} AND n.Name = s.Name
                    WHERE s.CollectionId = {srcCollectionId}");

                Exec(conn, tx, "DROP TABLE IF EXISTS temp._move_rmap");
                Exec(conn, tx, "CREATE TEMP TABLE _move_rmap(rn INTEGER PRIMARY KEY, old_id INTEGER NOT NULL)");
                Exec(conn, tx, $@"
                    INSERT INTO _move_rmap(rn, old_id)
                    SELECT ROW_NUMBER() OVER (ORDER BY Position, Id), Id
                    FROM src.db_records WHERE CollectionId = {srcCollectionId}");
                // По старому id ищут UI-строки записей и связи.
                Exec(conn, tx, "CREATE INDEX temp._move_rmap_old ON _move_rmap(old_id)");

                // Блок id под записи копии: записи пойдут своими пакетами. Без AUTOINCREMENT у
                // db_records (файлы не из миграций программы) резерв не держится — тогда все записи
                // одним INSERT здесь же: новые id — непрерывный блок в порядке ранга.
                recordsInBatches = recCount > 0 && RecordIdsAutoincrement();
                if (recordsInBatches) firstNew = ReserveRecordIds(recCount);
                else if (recCount > 0)
                {
                    InsertMovedRecords(conn, tx, newColId, firstNew: null, "1 = 1");
                    firstNew = ScalarLong(conn, tx, "SELECT last_insert_rowid()") - recCount + 1;
                }
                state.Pending ??= BeginPending(db, PendingKind.MoveCollection, newColId);
            });
            if (newColId == 0) return (0, 0);
            if (state.NewId == 0) state.NewId = newColId;

            // Проверку внешних ключей на время заливки снимаем, как у дубля (CopyCollectionData):
            // ссылки верны по построению, чужое удаление копии ловит проверка в каждом пакете.
            Exec(conn, null, "PRAGMA foreign_keys=OFF;");
            long processed = 0;
            var  pacer     = new BatchPacer(batchSize, NetworkPath.IsNetwork(db.DbPath));
            while (processed < recCount)
            {
                var upTo  = Math.Min(recCount, processed + pacer.Size);
                var slice = $"m.rn > {processed} AND m.rn <= {upTo}";
                var watch = System.Diagnostics.Stopwatch.StartNew();
                InAttachedTx(conn, tx =>
                {
                    if (ScalarLong(conn, tx, $"SELECT COUNT(*) FROM main.db_fields WHERE CollectionId = {newColId}") != fieldCount
                        || ScalarLong(conn, tx, $"SELECT COUNT(*) FROM main.db_collections WHERE Id = {newColId}") == 0)
                        throw new InvalidOperationException("Копию коллекции или ее свойства удалили, пока она заполнялась.");
                    if (recordsInBatches) InsertMovedRecords(conn, tx, newColId, firstNew, slice);
                    Exec(conn, tx, $@"
                        INSERT INTO main.db_field_values (RecordId, FieldId, Value)
                        SELECT {firstNew} + m.rn - 1, fm.new_fid, fv.Value
                        FROM _move_rmap m
                        JOIN src.db_field_values fv ON fv.RecordId = m.old_id
                        JOIN _move_fmap fm ON fm.old_fid = fv.FieldId
                        WHERE {slice}");
                    Exec(conn, tx, $@"
                        INSERT INTO main.calendar_index (RecordId, FieldId, CollectionId, StartIso, EndIso, AllDay)
                        SELECT {firstNew} + m.rn - 1, fm.new_fid, {newColId}, ci.StartIso, ci.EndIso, ci.AllDay
                        FROM _move_rmap m
                        JOIN src.calendar_index ci ON ci.RecordId = m.old_id
                        JOIN _move_fmap fm ON fm.old_fid = ci.FieldId
                        WHERE {slice}");
                    BeatPending(db, state.Pending!);
                });
                pacer.Done(watch.Elapsed);
                CheckpointIfWalLarge();
                processed = upTo;
                progress?.Report((int)(progressBase + processed));
            }
            Exec(conn, null, "PRAGMA foreign_keys=ON;");
            if (recCount == 0) progress?.Report((int)progressBase);

            InAttachedTx(conn, tx =>
            {
                CopyMovedUi(conn, tx, srcCollectionId, newColId, firstNew, recCount);
                var fields = ReadIntPairs(conn, tx, "SELECT old_fid,new_fid FROM _move_fmap")
                    .ToDictionary(p => p.A, p => p.B);
                relationCopy.AddCollection(srcCollectionId, newColId, fields);
                Exec(conn, tx, $@"
                    INSERT INTO _relation_copy_rmap(old_id,new_id)
                    SELECT old_id,{firstNew} + rn - 1 FROM _move_rmap");

                if (finalize is not null) finalize(newColId);
                else                      BeatPending(db, state.Pending!);
            });
            return (newColId, recCount);
        }
        finally
        {
            try { Exec(conn, null, "PRAGMA foreign_keys=ON;"); } catch { /* вернет следующее открытие */ }
            try
            {
                Exec(conn, null, "DROP TABLE IF EXISTS temp._move_fmap");
                Exec(conn, null, "DROP TABLE IF EXISTS temp._move_rmap");
            }
            catch { /* временные таблицы уйдут вместе с подключением */ }
        }
    }

    // Записи копии ранга из среза карты _move_rmap: firstNew задан — с зарезервированными id
    // (firstNew + rn − 1), null — с id от базы (непрерывный блок в порядке ранга).
    private static void InsertMovedRecords(DbConnection conn, DbTransaction tx, int newColId, long? firstNew, string slice)
    {
        var idCol = firstNew is null ? "" : "Id, ";
        var idVal = firstNew is long f ? $"{f} + m.rn - 1, " : "";
        Exec(conn, tx, $@"
            INSERT INTO main.db_records ({idCol}CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Color)
            SELECT {idVal}{newColId}, r.CreatedAt, r.UpdatedAt, r.CreatedBy, r.UpdatedBy, r.Position, r.StartDt, r.EndDt, r.AllDay, r.Title, r.Color
            FROM _move_rmap m JOIN src.db_records r ON r.Id = m.old_id WHERE {slice} ORDER BY m.rn");
    }

    // UI-настройки переносимой коллекции: ключи-id полей/записей перемэппливаются (поля — через
    // _move_fmap; записи — по карте рангов _move_rmap → firstNew + ранг − 1). Ключи колонок
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
            INSERT INTO main.column_meta (CollectionId, Key, Position, Visible, Level, DisplayName, Bg, Fg, Width, HAlign, VAlign,
                                     BgAt, FgAt, HAlignAt, VAlignAt)
            SELECT {newCol},
                   CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cm.Key END,
                   cm.Position, cm.Visible, cm.Level, cm.DisplayName, cm.Bg, cm.Fg, cm.Width, cm.HAlign, cm.VAlign,
                   cm.BgAt, cm.FgAt, cm.HAlignAt, cm.VAlignAt
            FROM src.column_meta cm
            LEFT JOIN _move_fmap fm ON cm.Key GLOB '[0-9]*' AND CAST(cm.Key AS INTEGER) = fm.old_fid
            WHERE cm.CollectionId = {srcCol}");

        if (recCount > 0)
        {
            Exec(conn, tx, $@"
                INSERT INTO main.ui_row_heights (CollectionId, RecordId, Height)
                SELECT {newCol}, {firstNew} + r.rn - 1, h.Height FROM src.ui_row_heights h
                JOIN _move_rmap r ON r.old_id = h.RecordId WHERE h.CollectionId = {srcCol}");
            Exec(conn, tx, $@"
                INSERT INTO main.record_display (CollectionId, RecordId, Visible, Level, Bg, Fg, HAlign, VAlign,
                                            BgAt, FgAt, HAlignAt, VAlignAt)
                SELECT {newCol}, {firstNew} + r.rn - 1, d.Visible, d.Level, d.Bg, d.Fg, d.HAlign, d.VAlign,
                       d.BgAt, d.FgAt, d.HAlignAt, d.VAlignAt FROM src.record_display d
                JOIN _move_rmap r ON r.old_id = d.RecordId WHERE d.CollectionId = {srcCol}");
            Exec(conn, tx, $@"
                INSERT INTO main.cell_style (CollectionId, RecordId, Key, Bg, Fg, HAlign, VAlign)
                SELECT {newCol}, {firstNew} + r.rn - 1,
                       CASE WHEN fm.new_fid IS NOT NULL THEN CAST(fm.new_fid AS TEXT) ELSE cs.Key END,
                       cs.Bg, cs.Fg, cs.HAlign, cs.VAlign
                FROM src.cell_style cs
                JOIN _move_rmap r ON r.old_id = cs.RecordId
                LEFT JOIN _move_fmap fm ON cs.Key GLOB '[0-9]*' AND CAST(cs.Key AS INTEGER) = fm.old_fid
                WHERE cs.CollectionId = {srcCol}");
        }
    }

    private static void Exec(DbConnection conn, DbTransaction? tx, string sql)
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
              IsDescription = f.IsDescription,
              IsDocFile = f.IsDocFile, IsDocType = f.IsDocType,
          })
          .ToList();

    /// <summary>
    /// Заводит поле ровно с теми флагами, что передали. Показ в календаре (<c>IsCalStart</c>)
    /// сюда не подмешивается: раньше первое поле-дата коллекции включалось само, и любая дата —
    /// «дата выдачи», «дата расчета», «дата создания» — становилась потоком событий, который
    /// пользователь потом выключал руками (см. проверку журнала в <c>PdfWatermark</c>).
    /// Теперь правило одно: тумблер включает человек. Единственное исключение — создание
    /// события ЧЕРЕЗ календарь: там источник дат доводит <see cref="EnsureCalendarDateField"/>,
    /// иначе созданная запись не появилась бы в разделе, из которого ее и завели.
    /// </summary>
    public DbField AddField(DbField field) => AddField(field, copy: false);

    /// <param name="copy">Поле — копия уже сохраненного (перенос записей с созданием недостающих
    /// свойств): автосвязь создается без подбора — ключи ее правила еще чужие, их перепишут после
    /// создания, подбор пойдет после переноса; условие выбора и столбец данных не проверяются
    /// заново — они уже были приняты, а номера в них перепишутся (нечитаемое условие копируется как
    /// есть и так же закрывает новые связи).</param>
    private DbField AddField(DbField field, bool copy)
    {
        Demand(PermAction.CreateField, field.CollectionId);
        // Правило, которое не готово к подбору (потеряно, новее, повреждено), сохраняется как есть,
        // без подбора: связей у нового поля еще нет.
        if (!copy && field.FieldType == "relation" && RelationConfig.FromConfig(field.ConfigJson)?.Auto is { IsReady: true }
            && !IsApplyingAutoRelation)
        {
            var id = SaveAutoRelationField(field, PreviewAutoRelation(field));
            return ToDbField(db.DbFields.AsNoTracking().Single(f => f.Id == id));
        }
        // Копия столбца данных ссылается на связь источника, пока ее не переписали: проверять нечего.
        if (!copy)
        {
            ValidateRelationComputedField(field);
            ValidateRelationCondition(field);
        }
        if (db.DbFields.Any(f => f.CollectionId == field.CollectionId && f.Name == field.Name))
            throw new InvalidOperationException("Поле с таким именем уже существует.");

        var entity = new DbFieldEntity
        {
            CollectionId = field.CollectionId, Name = field.Name,
            FieldType    = field.FieldType,    ConfigJson = field.ConfigJson,
            Position     = field.Position,     IsCalStart = field.IsCalStart,
            IsCalEnd     = field.IsCalEnd,     IsSystem = field.IsSystem,
            IsRequired   = field.IsRequired,
            IsTaskExec   = field.IsTaskExec,   IsTaskAssigner = field.IsTaskAssigner,
            IsTaskStatus = field.IsTaskStatus, IsDescription  = field.IsDescription,
            IsDocFile    = field.IsDocFile,    IsDocType      = field.IsDocType,
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
        if (field.FieldType == "relation" && RelationConfig.FromConfig(field.ConfigJson)?.Auto is { } auto
            && !IsApplyingAutoRelation)
        {
            if (auto.IsReady)
            {
                SaveAutoRelationField(field, PreviewAutoRelation(field));
                return;
            }
            // Правило, которое не готово к подбору, можно только оставить как есть (переименовать
            // свойство, сменить разделитель); задать его заново — выбрать пары. Цель связи с ним не
            // меняется: номера свойств в его парах относятся к прежней коллекции. Исключение — выбор
            // базы для старой межбазовой связи: коллекция та же, прежний номер базы не определен.
            var stored = RelationConfig.FromConfig(db.DbFields.AsNoTracking().Where(f => f.Id == field.Id)
                .Select(f => f.ConfigJson).FirstOrDefault());
            var proposed = RelationConfig.FromConfig(field.ConfigJson)!;
            if (stored?.Auto != auto)
                throw new InvalidOperationException("Выберите пары свойств автоподбора.");
            if (stored.TargetCollectionId != proposed.TargetCollectionId
                || stored.TargetDbId != proposed.TargetDbId && !IsLegacyRelationDb(stored.TargetDbId))
                throw new InvalidOperationException(auto.State == RelationAutoState.Newer
                    ? "Правило автоподбора создано более новой версией программы: чтобы сменить цель связи, выберите «Ручной выбор» или обновите программу."
                    : "Цель связи изменилась: выберите пары свойств автоподбора заново.");
        }
        ValidateRelationComputedField(field);
        ValidateRelationCondition(field);
        var oldType = db.DbFields.AsNoTracking().Where(f => f.Id == field.Id).Select(f => f.FieldType).FirstOrDefault();
        using var transaction = db.Database.CurrentTransaction is null &&
            (oldType == "relation" || field.FieldType == "relation") ? db.Database.BeginTransaction() : null;
        try
        {
            UpdateFieldCore(field);
            transaction?.Commit();
        }
        catch
        {
            if (transaction is not null)
            {
                transaction.Rollback();
                db.ChangeTracker.Clear();
            }
            throw;
        }
    }

    private void UpdateFieldCore(DbField field)
    {
        Demand(PermAction.EditField, field.CollectionId);
        var entity = db.DbFields.Find(field.Id);
        if (entity is null) return;
        if (entity.Name != field.Name &&
            db.DbFields.Any(f => f.CollectionId == entity.CollectionId && f.Name == field.Name && f.Id != field.Id))
            throw new InvalidOperationException("Поле с таким именем уже существует.");
        ValidateAndConvertRelationFieldChange(ToDbField(entity), field);

        // Пересборка проекции нужна только при смене ТИПА (содержимое проекции реально меняется:
        // текст→дата заливает строки, дата→текст очищает). Видимость (IsCalStart) — чистый
        // фильтр чтения, пересборки не требует (см. SetDateFieldVisible) — раньше ее смена
        // здесь тоже пересобирала проекцию (минуты фриза на больших коллекциях).
        var oldType            = entity.FieldType;
        var calRelevantChanged = entity.FieldType != field.FieldType;

        // Число с выбранным десятичным разделителем: если поле стало числом или сменился разделитель —
        // приведем уже сохраненные значения к нему (канон хранения). Считаем ДО перезаписи ConfigJson.
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
        entity.IsDescription  = field.IsDescription;
        entity.IsDocFile      = field.IsDocFile;
        entity.IsDocType      = field.IsDocType;
        // Смена типа рвет привязку к разделу так же, как удаление столбца: раздел физически не
        // сможет читать данные (статус без списка статусов, срок без даты). Флаг, оставшийся на
        // несовместимом типе, — роль-призрак: данные в раздел не идут, а плашка «привязано к
        // разделу» продолжает висеть. Снимаем здесь, чтобы состояние базы совпадало с тем, что
        // видит пользователь; окно свойства предупреждает об этом ДО сохранения.
        ClearRolesIncompatibleWithType(entity, field.FieldType);
        db.SaveChanges();

        // Тип пересек границу «дата» → приводим уже сохраненные значения к новому виду
        // (текст↔дата без потери данных). Делаем ДО пересборки calendar_index, чтобы
        // индекс строился уже по каноническим значениям.
        if (oldType != field.FieldType && field.Id is int convFid)
            ConvertFieldValuesForTypeChange(convFid, oldType, field.FieldType);

        // Число: привести десятичный разделитель уже сохраненных значений к выбранному
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
            var pVal = AddParam(upd, "@val", System.Data.DbType.String);
            var pRid = AddParam(upd, "@rid", System.Data.DbType.Int32);
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

    // При смене типа поля на «дата»/с «даты» приводим уже сохраненные значения к нужному
    // виду — чтобы перевод текст↔дата шел без потери данных:
    //   • текст → дата: разбираем значение и пишем ISO-канон (как пишет редактор даты),
    //     чтобы дата сразу читалась таблицей, календарем и редактором;
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
            var pVal = AddParam(upd, "@val", System.Data.DbType.String);
            var pRid = AddParam(upd, "@rid", System.Data.DbType.Int32);
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
        Demand(PermAction.DuplicateField, CollectionOfField(fieldId));
        using var tx = BeginWrite();
        var copy = CreateFieldCopyEntity(fieldId);

        // Копируем значения ячеек исходного свойства во всех записях ОДНИМ bulk-SQL —
        // на больших коллекциях это сотни тысяч строк, поэлементная вставка через EF
        // подвешивала бы приложение. Конфликтов по (RecordId, FieldId) нет: поле новое.
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO db_field_values (RecordId, FieldId, Value)
            SELECT RecordId, {copy.Id}, Value FROM db_field_values WHERE FieldId = {fieldId}");
        CopyFieldRelationEdges(fieldId, copy.Id);
        RemapCopiedRelationConfigs(new Dictionary<int, int>(), new Dictionary<int, int> { [fieldId] = copy.Id }, 0, 0);

        LogNow("duplicate", "field", copy.Name, CollectionNameOf(copy.CollectionId));
        tx.Commit();
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
            IsDescription = false,
            IsDocFile    = false, IsDocType = false,
        };
        db.DbFields.Add(copy);
        db.SaveChanges();
        return copy;
    }

    private static DbField ToDbField(DbFieldEntity e) => new()
    {
        Id = e.Id, CollectionId = e.CollectionId, Name = e.Name,
        FieldType = e.FieldType, ConfigJson = e.ConfigJson, Position = e.Position,
        IsCalStart = e.IsCalStart, IsCalEnd = e.IsCalEnd,
        IsSystem = e.IsSystem, IsRequired = e.IsRequired,
        IsTaskExec = e.IsTaskExec, IsTaskAssigner = e.IsTaskAssigner,
        IsTaskStatus = e.IsTaskStatus, IsDescription = e.IsDescription,
        IsDocFile = e.IsDocFile, IsDocType = e.IsDocType,
    };

    // ── Тяжелые операции со свойствами: счетчик + пакетные версии с прогрессом ──
    // Удаление и дубль поля затрагивают его значения по ВСЕМ записям коллекции (на больших
    // коллекциях — сотни тысяч строк). Маленькие поля контроллер делает синхронно мгновенно;
    // большие — через *Isolated на отдельном подключении в фоне, с окном прогресса (как дубль/
    // удаление коллекций в сайдбаре). Пакеты — только ради гранулярности прогресса: по SQL это
    // та же работа, что один INSERT…SELECT / каскадный DELETE.

    /// <summary>Число значений поля по всем записям — для выбора «мгновенно» vs «окно прогресса».</summary>
    public int CountFieldValues(int fieldId) =>
        db.DbFieldValues.Count(v => v.FieldId == fieldId);

    /// <summary>Число значений по каждому из полей (для прогресса bulk-операций со свойствами) на
    /// ОТДЕЛЬНОМ подключении — чтобы счет по сотням тысяч строк шел в фоне, не подвешивая UI до
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
        // Значения столбца лежат у каждой записи — правка трогает страницу каждой записи; журнал
        // переносится в файл уже в фоне, после того как дубль виден (WalCheckpointer).
        ctx.ApplyBulkPerformancePragmas();
        ctx.DeferCheckpoint();
        var repo = new ConstructorRepository(ctx);
        // Владельца-коллекцию ищем через СВЕЖИЙ контекст: обертку зовут из фонового
        // потока, а общий контекст — только для UI-потока (сама Demand читает кеш).
        Demand(PermAction.DuplicateField, repo.CollectionOfField(fieldId));
        return repo.DuplicateFieldData(fieldId, progress);
    }

    /// <summary>Удаление ОДНОГО поля на ОТДЕЛЬНОМ подключении (для фона) с прогрессом по его значениям.
    /// Несколько полей контроллер удаляет в цикле — чтобы показывать «N/M столбцов».</summary>
    public void DeleteFieldIsolated(int fieldId, IProgress<int>? progress = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyBulkPerformancePragmas();
        ctx.DeferCheckpoint();   // перенос журнала — в фоне, см. DuplicateFieldIsolated
        var repo = new ConstructorRepository(ctx);
        // Лукап через свежий контекст — обертка работает в фоне (см. DuplicateFieldIsolated).
        Demand(PermAction.DeleteField, repo.CollectionOfField(fieldId));
        repo.DeleteFieldData(fieldId, progress);
    }


    // Столбец больше этого — коммит порциями: одна транзакция держала бы в журнале страницы всей
    // таблицы до конца операции (значения столбца лежат у каждой записи; на 10 млн записей это
    // десятки ГБ журнала рядом с базой). Между порциями операция сама переносит журнал в файл
    // (CheckpointBetweenChunks): фоновый перенос за непрерывной записью не успевает, и журнал рос
    // бы дальше порции (замер: 4,5 ГБ через несколько минут дубля столбца на 10 млн). Перенесенный
    // целиком журнал следующая порция пишет с начала (до journal_size_limit). Столбец меньше —
    // одной транзакцией, перенос — в фоне.
    private const int FieldOpChunkValues = 500_000;

    // Перенести журнал в файл между порциями огромной правки (см. FieldOpChunkValues) — PASSIVE:
    // не ждет читателей и не держит их. TRUNCATE ждал читателей и не пускал новые — интерфейс,
    // читавший страницу таблицы, стоял до 5,6 с.
    private void CheckpointBetweenChunks()
    {
        if (db.IsPostgres || NetworkPath.IsNetwork(db.DbPath)) return;
        try { db.Database.ExecuteSqlRaw("PRAGMA main.wal_checkpoint(PASSIVE);"); }
        catch (SqliteException ex) when (DbHealthMonitor.IsBusyError(ex)) { }
    }

    /// <summary>Дубль поля ПАКЕТАМИ: значения порциями по списку RecordId. Копия поля создается в той
    /// же транзакции, что и ее значения: прерванный дубль не оставляет пустого столбца (у огромного
    /// столбца, который коммитится порциями, — остаются уже скопированные порции).</summary>
    public DbField DuplicateFieldData(int fieldId, IProgress<int>? progress = null, int batchSize = 10000,
                                      int chunkValues = FieldOpChunkValues)
    {
        Demand(PermAction.DuplicateField, CollectionOfField(fieldId));
        var ids = db.DbFieldValues.Where(v => v.FieldId == fieldId).Select(v => v.RecordId)
            .Union(db.DbRecordLinks.Where(l => l.FieldId == fieldId).Select(l => l.FromRecordId))
            .Select(id => (long)id).OrderBy(id => id).ToList();
        var tx  = db.Database.BeginTransaction();
        try
        {
            var copy   = CreateFieldCopyEntity(fieldId);
            var copyId = copy.Id;
            RemapCopiedRelationConfigs(new Dictionary<int, int>(), new Dictionary<int, int> { [fieldId] = copyId }, 0, 0);
            for (var i = 0; i < ids.Count; i += batchSize)
            {
                if (i > 0 && i % chunkValues < batchSize) { tx.Commit(); tx.Dispose(); CheckpointBetweenChunks(); tx = db.Database.BeginTransaction(); }
                var inList = JoinIdRange(ids, i, batchSize);
                // Копируем строго по PK (FieldId + RecordId IN …) — без скана значений чужих полей.
                // ExecuteSqlRaw: в готовой строке нет фигурных скобок (числа и список «1,2,3»).
                db.Database.ExecuteSqlRaw(
                    $"INSERT INTO db_field_values (RecordId, FieldId, Value) " +
                    $"SELECT RecordId, {copyId}, Value FROM db_field_values " +
                    $"WHERE FieldId = {fieldId} AND RecordId IN ({inList})");
                CopyFieldRelationEdges(fieldId, copyId, inList);
                progress?.Report(Math.Min(ids.Count, i + batchSize));
            }
            tx.Commit();
            LogNow("duplicate", "field", copy.Name, CollectionNameOf(copy.CollectionId));
            return ToDbField(copy);
        }
        finally { tx.Dispose(); }
    }

    /// <summary>Удаление поля ПАКЕТАМИ: значения порциями по списку RecordId (прогресс), затем строка
    /// поля (каскад на calendar_index по FK) и чистка column_meta — как в DeleteField.</summary>
    public void DeleteFieldData(int fieldId, IProgress<int>? progress = null, int batchSize = 10000,
                                int chunkValues = FieldOpChunkValues)
    {
        var field = db.DbFields.Find(fieldId);
        if (field is null) return;
        var colId = field.CollectionId;

        var ids = FieldValueRecordIdsSorted(fieldId);
        // Огромный столбец коммитится порциями (FieldOpChunkValues): прерванное удаление оставляет
        // столбец с частью значений; строка поля уходит последней порцией.
        var tx = db.Database.BeginTransaction();
        try
        {
            for (var i = 0; i < ids.Count; i += batchSize)
            {
                if (i > 0 && i % chunkValues < batchSize) { tx.Commit(); tx.Dispose(); CheckpointBetweenChunks(); tx = db.Database.BeginTransaction(); }
                var inList = JoinIdRange(ids, i, batchSize);
                // EF1002 «возможная SQL-инъекция»: в строку подставляются только целые идентификаторы
                // и имена таблиц, заданные здесь же в коде. Значений от пользователя в ней нет — те
                // уходят параметрами. Сырой SQL намеренно: пакетные DELETE и INSERT…SELECT на тысячах
                // строк параметрами не выражаются.
                #pragma warning disable EF1002
                db.Database.ExecuteSqlRaw(
                    $"DELETE FROM db_field_values WHERE FieldId = {fieldId} AND RecordId IN ({inList})");
                #pragma warning restore EF1002
                progress?.Report(Math.Min(ids.Count, i + batchSize));
            }

            // Данные из связанных записей без своей связи не считаются — уходят вместе с ней.
            if (field.FieldType == "relation") RemoveRelationDependentFields(fieldId, colId);

            // Строка поля (FK ON DELETE CASCADE уберет calendar_index) + чистка column_meta и
            // оформления ее ячеек (FK нет — ключ строковый), как в синхронном DeleteField.
            var key = fieldId.ToString();
            db.Database.ExecuteSqlInterpolated($"DELETE FROM db_fields WHERE Id = {fieldId}");
            db.Database.ExecuteSqlInterpolated(
                $"DELETE FROM column_meta WHERE CollectionId = {colId} AND Key = {key}");
            db.Database.ExecuteSqlInterpolated(
                $"DELETE FROM cell_style WHERE CollectionId = {colId} AND Key = {key}");
            tx.Commit();
        }
        finally { tx.Dispose(); }
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
    /// <paramref name="excludeId"/> — id самой коллекции (не считать ее саму конфликтом).</summary>
    public string UniqueCollectionName(string desired, int? groupId, int? excludeId = null) =>
        CollectionNameExists(desired, groupId, excludeId)
            ? NextCopyName(desired, c => CollectionNameExists(c, groupId, excludeId))
            : desired;

    /// <summary>Имя группы, свободное на целевом уровне (родитель): если занято — «(копия)»-вариант.</summary>
    public string UniqueGroupName(string desired, int? parentId, int? excludeId = null) =>
        GroupNameExists(desired, parentId, excludeId)
            ? NextCopyName(desired, c => GroupNameExists(c, parentId, excludeId))
            : desired;

    /// <summary>Если имя коллекции совпало с другой в ее группе — переименовать в «(копия)»-вариант
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

    /// <summary>Если имя группы совпало с другой на ее уровне — переименовать в «(копия)»-вариант.</summary>
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
        if (entity.FieldType == "relation") RemoveRelationDependentFields(fieldId, colId);
        Log("delete", "field", entity.Name, CollectionNameOf(colId));
        db.DbFields.Remove(entity);

        // Чистим метаданные колонки этого поля (порядок/видимость), чтобы в column_meta не
        // копились мертвые ключи (иначе перемещение колонок начинает «не работать»).
        var key     = fieldId.ToString();
        var metaRow = db.ColumnMeta.FirstOrDefault(m => m.CollectionId == colId && m.Key == key);
        if (metaRow is not null) db.ColumnMeta.Remove(metaRow);

        // Оформление ячеек этой колонки — по той же причине: Key строковый, FK на поле нет.
        db.CellStyles.RemoveRange(db.CellStyles.Where(c => c.CollectionId == colId && c.Key == key));

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

    // Все записи коллекции — сырым читателем страниц (ReadRecordRows), а не EF-проекцией: в
    // старых файлах «Название» бывает NULL (см. DatabaseManager.RepairNullTitles), а EF для
    // непустой по модели колонки вырезает `?? ""` из запроса и падает на материализации
    // («The data is NULL at ordinal» — так рушилось открытие панели записи). Сырой читатель
    // терпит NULL и отдает пустое название.
    public List<DbRecord> GetRecords(int collectionId) =>
        ReadRecordRows($@"SELECT {RecordCols} FROM db_records r
                          WHERE r.CollectionId = @c
                          ORDER BY r.Position, r.Id",
                       ("@c", collectionId));

    /// <summary>
    /// Легкая выборка (Id, Title) всех записей коллекции в порядке (Position, Id) — для окна
    /// «Отображение записей». Только две колонки сырым SQL: не материализует полные сущности и
    /// не засоряет трекер (на коллекциях в сотни тысяч строк это заметно); NULL в «Названии»
    /// старых файлов читается как пустая строка (EF-проекция на нем падала бы).
    /// </summary>
    public List<(int Id, string Title)> GetRecordTitlesOrdered(int collectionId)
    {
        var result = new List<(int Id, string Title)>();
        var conn   = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Title FROM db_records WHERE CollectionId = @c ORDER BY Position, Id";
            AddParam(cmd, "@c").Value = collectionId;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
                result.Add((rdr.GetInt32(0), rdr.IsDBNull(1) ? "" : rdr.GetString(1)));
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    /// <summary>Id записей коллекции в порядке таблицы (Position, Id) на ОТДЕЛЬНОМ подключении —
    /// список окна «Отображение записей». Названия окно подгружает порциями для видимых строк:
    /// весь список с названиями на 10 млн записей — гигабайт памяти и полминуты до показа.</summary>
    public int[] GetRecordIdsOrderedIsolated(int collectionId)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        // Сырой читатель: на 10 млн id материализация EF стоила секунды сверх самого чтения индекса.
        var conn = ctx.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM db_records WHERE CollectionId = @c ORDER BY Position, Id";
        AddParam(cmd, "@c").Value = collectionId;
        var ids = new List<int>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) ids.Add(rd.GetInt32(0));
        return [.. ids];
    }

    /// <summary>Id записей коллекции, чье название содержит <paramref name="query"/> без учета
    /// регистра (у кириллицы тоже), — поиск окна «Отображение записей» по базе, на ОТДЕЛЬНОМ
    /// подключении. Не больше <paramref name="cap"/> + 1: лишний означает «найдено больше».</summary>
    public List<int> SearchRecordIdsIsolated(int collectionId, string query, int cap, CancellationToken ct)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var q = query.Trim();
        var records = ctx.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);
        if (ctx.IsPostgres)
        {
            var pattern = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            return records.Where(r => EF.Functions.ILike(r.Title, pattern, "\\")).Select(r => r.Id).Take(cap + 1).ToList();
        }
        // Отмена прерывает запрос из самой функции сравнения (как у поиска значений фильтра).
        ctx.Database.OpenConnection();
        ((SqliteConnection)ctx.Database.GetDbConnection()).CreateFunction(SqlText.ContainsName,
            (string? value, string? part) =>
            {
                ct.ThrowIfCancellationRequested();
                return value is not null && part is not null && value.Contains(part, StringComparison.OrdinalIgnoreCase);
            }, isDeterministic: true);
        try
        {
            return records.Where(r => SqlText.ContainsIgnoreCase(r.Title, q)).Select(r => r.Id).Take(cap + 1).ToList();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    /// <summary>Асинхронная версия <see cref="CountRecords"/> на ОТДЕЛЬНОМ подключении — для
    /// подсчета перед массовыми операциями сайдбара и уточнения протяженности таблицы без фриза
    /// UI (COUNT на 9 млн — 0,7–1,3 с, со скрытыми записями — 3 с).</summary>
    public Task<int> CountRecordsAsync(int collectionId, bool excludeHidden = false) =>
        Task.Run(() =>
        {
            using var slow = Logging.Diag.Measure("счет записей коллекции", Logging.Diag.Background,
                                                  $"{DbErrors.Display(db.DbPath)}, коллекция {collectionId}");
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountRecords(collectionId, excludeHidden);
        });

    // excludeHidden=true исключает записи, скрытые в окне «Отображение записей»
    // (record_display.Visible=false). Фильтр уходит в SQL до Count/Skip/Take, поэтому
    // виртуализация и пагинация таблицы сохраняются (см. VirtualRowList).
    /// <summary>Снимок всего, что таблица читает при открытии коллекции (см. <see cref="CollectionOpenSnapshot"/>),
    /// на ЭТОМ контексте — для фонового чтения см. <see cref="ReadOpenSnapshotAsync"/>.
    /// <paramref name="deferCount"/> — вместо точного COUNT (0,7–3 с на 9 млн строк) в снимок идет
    /// оценка <see cref="EstimateRecordCount"/> с пометкой <see cref="CollectionOpenSnapshot.CountIsEstimate"/>;
    /// точное число таблица уточняет в фоне (<see cref="CountRecordsAsync"/>), не задерживая первый кадр.</summary>
    public CollectionOpenSnapshot ReadOpenSnapshot(int collectionId, bool deferCount = false)
    {
        var collection    = GetAllCollections().FirstOrDefault(c => c.Id == collectionId);
        var recordDisplay = GetRecordDisplay(collectionId);
        var anyHidden     = recordDisplay.Any(kv => !kv.Value.Visible);
        if (deferCount)
        {
            var estimate = EstimateRecordCount(collectionId);
            return new CollectionOpenSnapshot(
                collection,
                GetUiSettings(collectionId),
                GetFields(collectionId),
                GetSystemFieldNames(collectionId),
                GetColumnOrder(collectionId),
                GetColumnDisplay(collectionId),
                GetColumnStyles(collectionId),
                GetColumnWidths(collectionId),
                recordDisplay,
                GetRowHeights(collectionId),
                GetRowStyles(collectionId),
                estimate,
                estimate,
                CountIsEstimate: true);
        }
        var count = CountRecords(collectionId);
        return new CollectionOpenSnapshot(
            collection,
            GetUiSettings(collectionId),
            GetFields(collectionId),
            GetSystemFieldNames(collectionId),
            GetColumnOrder(collectionId),
            GetColumnDisplay(collectionId),
            GetColumnStyles(collectionId),
            GetColumnWidths(collectionId),
            recordDisplay,
            GetRowHeights(collectionId),
            GetRowStyles(collectionId),
            count,
            anyHidden ? CountRecords(collectionId, excludeHidden: true) : count);
    }

    /// <summary>То же в фоне на собственном контексте (общий контекст не потокобезопасен): UI-поток
    /// ждет один await вместо десятка обращений к файлу — на медленной сетевой папке это секунды.</summary>
    public Task<CollectionOpenSnapshot> ReadOpenSnapshotAsync(int collectionId, bool deferCount = false) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).ReadOpenSnapshot(collectionId, deferCount);
        });

    public int CountRecords(int collectionId, bool excludeHidden = false) =>
        db.DbRecords.Count(r => r.CollectionId == collectionId
            && (!excludeHidden || !db.RecordDisplay.Any(d =>
                   d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)));

    // Страницы таблицы — raw SQL: EF-формы не масштабируются на миллионы строк. Смещение и
    // keyset обязаны идти ПО ИНДЕКСУ (CollectionId, Position): row-value сравнение
    // (Position, Id) > (@p, @id) дает индексный сик (0 мс на любой глубине), а OR-форма
    // (Position > @p OR (Position = @p AND Id > @id)), в которую транслируется LINQ, НЕ ложится
    // на индекс — SQLite сканирует коллекцию с НАЧАЛА (на 10 млн — сотни мс на КАЖДУЮ страницу
    // скролла, т.е. «keyset» был O(глубины)). Хвост Id в ключе сика бесплатен: Id = rowid,
    // он неявно завершает каждую запись индекса.
    private const string RecordCols =
        "r.Id, r.CollectionId, r.CreatedAt, r.Position, r.StartDt, r.EndDt, r.AllDay, r.Title, " +
        "r.UpdatedAt, r.CreatedBy, r.UpdatedBy, r.Color";

    // Описание записи для календарных карточек: значение поля с ролью «Описание»
    // (IsDescription). Скалярный подзапрос, а не JOIN: не размножает строки, если ролью
    // помечено два поля (копии коллекций), и идет сиком по PK (RecordId, FieldId)
    // db_field_values. Флаги в PG хранятся integer 0/1 — сравнение общее для обоих провайдеров.
    private const string DescExpr =
        "(SELECT fv.Value FROM db_field_values fv JOIN db_fields fd ON fd.Id = fv.FieldId" +
        " WHERE fv.RecordId = r.Id AND fd.IsDescription = 1 LIMIT 1)";

    // Фильтр скрытых записей (окно «Отображение записей») для raw SQL страниц таблицы.
    private static string HiddenFilter(bool excludeHidden, string alias = "r") => excludeHidden
        ? $" AND NOT EXISTS (SELECT 1 FROM record_display d" +
          $" WHERE d.CollectionId = {alias}.CollectionId AND d.RecordId = {alias}.Id AND d.Visible = 0)"
        : "";

    /// <summary>Страница по offset (прыжок скроллбаром). Смещение идет по ПОКРЫВАЮЩЕМУ индексу
    /// (CollectionId, Position): подзапрос отбирает только Id — пропускаемые записи не читаются
    /// из таблицы, join достает колонки лишь для строк страницы. При известном числе записей
    /// <paramref name="total"/> смещение идет с БЛИЖНЕГО края: прыжок в конец 9 млн строк — 1,5 мс
    /// вместо 600–750 (скан индекса до глубины); середина в любом случае ~300 мс.</summary>
    public List<DbRecord> GetRecordsPage(int collectionId, int skip, int take, bool excludeHidden = false, int? total = null)
    {
        if (take <= 0) return [];
        var fromEnd = total is int n && skip > n / 2;
        var offset  = fromEnd ? Math.Max(0, total!.Value - skip - take) : skip;
        var limit   = fromEnd ? Math.Min(take, Math.Max(0, total!.Value - skip)) : take;
        if (limit <= 0) return [];
        var order   = fromEnd ? "i.Position DESC, i.Id DESC" : "i.Position, i.Id";
        return ReadRecordRows($@"SELECT {RecordCols}
                                 FROM db_records r
                                 JOIN (SELECT i.Id FROM db_records i
                                       WHERE i.CollectionId = @c{HiddenFilter(excludeHidden, "i")}
                                       ORDER BY {order} LIMIT @take OFFSET @skip) page ON page.Id = r.Id
                                 ORDER BY r.Position, r.Id",
                              ("@c", collectionId), ("@take", limit), ("@skip", offset));
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
            while (rdr.Read()) result.Add(ReadRecord(rdr));
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
        var values = db.DbFieldValues.AsNoTracking()
            .Where(fv => recordIds.Contains(fv.RecordId))
            .GroupBy(fv => fv.RecordId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(fv => fv.FieldId, fv => fv.Value));
        FillRelationComputedValues(recordIds, values);
        return values;
    }

    /// <summary>Есть ли у записи сосед выше/ниже в порядке коллекции (Position, Id) — для пунктов
    /// «Сдвинуть вверх/вниз». Row-value сик по индексу (CollectionId, Position): O(1) на любой
    /// глубине. Прежний путь считал РАНГ записи (COUNT до ее глубины) + полный COUNT коллекции —
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

    public DbRecord? GetRecord(int recordId, bool refresh = false)
    {
        var r = db.DbRecords.Find(recordId);
        if (r is not null && refresh)
        {
            db.Entry(r).Reload();
            if (db.Entry(r).State == EntityState.Detached) return null;
        }
        if (r is null) return null;
        return new DbRecord
        {
            Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt, CreatedBy = r.CreatedBy, UpdatedBy = r.UpdatedBy,
            Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
            Title = r.Title,
            Color = r.Color,
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
        var derived = changed ? ReactToSourceEdit(recordId, r => r.SourceIsTitle) : [];
        if (changed)
        {
            NotifyRelationDataChanged(rec.CollectionId, 0, recordId);
            foreach (var (field, _) in derived) NotifyRelationDataChanged(rec.CollectionId, field, recordId);
        }
        return derived;
    }

    /// <summary>
    /// Записать описание записи. Описание — обычное поле коллекции с ролью «Описание»
    /// (<see cref="DbFieldEntity.IsDescription"/>), значение живет в db_field_values, поэтому
    /// вся работа (журнал, аудит, авто-привязка файлов) идет общим путем SetFieldValue.
    /// Поля нет (коллекция никогда не создавалась календарем/задачей, либо его удалили) —
    /// писать некуда, правка молча игнорируется. Возвращает побочные изменения авто-привязки.
    /// </summary>
    public IReadOnlyList<(int FieldId, string Value)> SetRecordDescription(int recordId, string? description)
    {
        if (CollectionOfRecord(recordId) is not int collectionId) return [];
        if (DescriptionFieldId(collectionId) is not int fieldId) return [];
        return SetFieldValue(recordId, fieldId, description ?? "");
    }

    /// <summary>Значение поля-описания записи ("" — поля нет либо оно пустое).</summary>
    public string GetRecordDescription(int recordId)
    {
        if (CollectionOfRecord(recordId) is not int collectionId) return "";
        if (DescriptionFieldId(collectionId) is not int fieldId) return "";
        return db.DbFieldValues.AsNoTracking()
                 .Where(v => v.RecordId == recordId && v.FieldId == fieldId)
                 .Select(v => v.Value).FirstOrDefault() ?? "";
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
    // чтением и записью — UPDATE попадает в 0 строк (DbUpdateConcurrencyException). Удаленное
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
            Color        = record.Color,
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

        ResolveAutoFileForRecord(record.CollectionId, entity.Id);
        NotifyRelationDataChanged(record.CollectionId, recordId: entity.Id);
        return record;
    }

    // Создает запись сразу после afterRecordId (по позиции).
    public DbRecord AddRecordAfter(DbRecord record, int afterRecordId)
    {
        record.Position = PositionAfterRecord(record.CollectionId, afterRecordId);
        return AddRecord(record);
    }

    // Позиция для вставки записи сразу после afterRecordId; инициатор не найден — в конец.
    private int PositionAfterRecord(int collectionId, int afterRecordId) =>
        AllocatePositions(collectionId, RecordKey(collectionId, afterRecordId), 1).At(0);

    // ── Номера новых записей ─────────────────────────────────────────────────
    // Порядок записей коллекции — (Position, Id), Position — int. Новая запись получает номер
    // между якорем и следующей записью. Места нет — раздвигаются только ближайшие следующие
    // записи (не больше PositionWindow); не хватает и их — новые записи получают номер якоря:
    // Id у них больше всех, и в порядке (Position, Id) они встают сразу за якорем и записями с
    // тем же номером. Прежде при нехватке места перенумеровывалась вся коллекция шагом 1000, а
    // массовый дубль сдвигал весь хвост: на 10 млн записей — две минуты на UI-потоке, и номера
    // уходили за пределы int (такие записи переставали читаться). Отсюда номера всегда в int.
    private const int PositionStep   = 1000;
    private const int PositionWindow = 1024;

    // Номера пакета новых записей: i-я (с нуля) получает Anchor + (i + 1) · Num / Den.
    private readonly record struct PositionRun(long Anchor, long Num, long Den)
    {
        public int At(long i) => (int)(Anchor + (i + 1) * Num / Den);
        // То же в SQL: rank — ранг записи в пакете, с единицы.
        public string Sql(string rank) => $"{Anchor} + ({rank} * {Num}) / {Den}";
    }

    // Ключ записи (Position, Id); null — записи нет в этой коллекции.
    private (long Pos, int Id)? RecordKey(int collectionId, int recordId)
    {
        var k = ReadRecordKeys("SELECT Position, Id FROM db_records WHERE Id = @id AND CollectionId = @c",
                               ("@c", collectionId), ("@id", recordId));
        return k.Count > 0 ? k[0] : null;
    }

    // Номера для count новых записей сразу после якоря (null — в конец коллекции).
    private PositionRun AllocatePositions(int collectionId, (long Pos, int Id)? anchor, int count)
    {
        if (anchor is null)
        {
            var last = ReadRecordKeys(
                "SELECT Position, Id FROM db_records WHERE CollectionId = @c ORDER BY Position DESC, Id DESC LIMIT 1",
                ("@c", collectionId));
            // Пустая коллекция: шаг 1000, на больших пакетах мельче — чтобы уложиться в int.
            if (last.Count == 0)
                return new PositionRun(0, Math.Clamp(int.MaxValue / ((long)count + 1), 1, PositionStep), 1);
            anchor = last[0];
        }
        var a = anchor.Value.Pos;
        var next = ReadRecordKeys(
            "SELECT Position, Id FROM db_records WHERE CollectionId = @c AND (Position, Id) > (@p, @id) " +
            "ORDER BY Position, Id LIMIT @n",
            ("@c", collectionId), ("@p", a), ("@id", anchor.Value.Id), ("@n", PositionWindow + 1));

        for (var j = 0; j <= Math.Min(next.Count, PositionWindow); j++)
        {
            // Раздвигаем j следующих записей; j == next.Count — за ними конец коллекции.
            var end = j == next.Count;
            var upper = end ? (long)int.MaxValue + 1 : next[j].Pos;
            var need  = (long)count + j;
            if (upper - a - 1 < need) continue;
            var run = end
                ? new PositionRun(a, Math.Min(PositionStep, (upper - a - 1) / need), 1)
                : new PositionRun(a, upper - a, need + 1);
            if (j > 0) MoveRecordPositions(next.Take(j).Select((k, i) => (k.Id, run.At(count + i))).ToList());
            return run;
        }
        return new PositionRun(Math.Clamp(a, int.MinValue, int.MaxValue), 0, 1);
    }

    // Ключи (Position, Id) по запросу. Position читается как long: номер за пределами int
    // (базы, испорченные прежней перенумерацией) не должен ронять чтение.
    private List<(long Pos, int Id)> ReadRecordKeys(string sql, params (string Name, object Value)[] args)
    {
        db.Database.OpenConnection();
        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            if (db.Database.CurrentTransaction is { } t) cmd.Transaction = t.GetDbTransaction();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) AddParam(cmd, name).Value = value;
            var keys = new List<(long, int)>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) keys.Add((Convert.ToInt64(rd.GetValue(0)), Convert.ToInt32(rd.GetValue(1))));
            return keys;
        }
        finally { db.Database.CloseConnection(); }
    }

    // Новые номера раздвинутых записей; закешированные EF копии перечитываем.
    private void MoveRecordPositions(List<(int Id, int Pos)> moves)
    {
        db.Database.OpenConnection();
        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            if (db.Database.CurrentTransaction is { } t) cmd.Transaction = t.GetDbTransaction();
            cmd.CommandText = "UPDATE db_records SET Position = @p WHERE Id = @id";
            var p  = AddParam(cmd, "@p",  System.Data.DbType.Int32);
            var id = AddParam(cmd, "@id", System.Data.DbType.Int32);
            foreach (var m in moves)
            {
                p.Value = m.Pos;
                id.Value = m.Id;
                cmd.ExecuteNonQuery();
            }
        }
        finally { db.Database.CloseConnection(); }
        var moved = moves.Select(m => m.Id).ToHashSet();
        foreach (var e in db.ChangeTracker.Entries<DbRecordEntity>().ToList())
            if (moved.Contains(e.Entity.Id) && e.State == EntityState.Unchanged) e.Reload();
    }

    public void UpdateRecord(DbRecord record)
    {
        Demand(PermAction.EditRecord, record.CollectionId);
        if (record.Id is null) return;
        var entity = db.DbRecords.Find(record.Id.Value);
        if (entity is null) return;

        var titleChanged = entity.Title != record.Title;

        // Перестановки (Position) журнал не засоряют — только смысловые изменения.
        // «до → после» — для переименования (даты видны в самой записи, описание ведет
        // журнал своим путем как обычное поле — см. SetFieldValue).
        var meaningfulChange =
            entity.Title != record.Title ||
            entity.StartDt != record.StartDt || entity.EndDt != record.EndDt ||
            entity.AllDay != record.AllDay || entity.CollectionId != record.CollectionId;
        if (meaningfulChange)
            Log("update", "record", record.Title, CollectionNameOf(record.CollectionId),
                detail: entity.Title != record.Title
                    ? ChangeDetail.Rename(entity.Title, record.Title)
                    : null);

        entity.CollectionId = record.CollectionId;
        entity.Position     = record.Position;
        entity.StartDt      = record.StartDt;
        entity.EndDt        = record.EndDt;
        entity.AllDay       = record.AllDay;
        entity.Title        = record.Title;
        if (meaningfulChange) TouchUpdated(entity);   // аудит «последней правки» (не для чистых перестановок)
        db.SaveChanges();
        if (titleChanged)
        {
            ReactToSourceEdit(entity.Id, rule => rule.SourceIsTitle);
            NotifyRelationDataChanged(entity.CollectionId, recordId: entity.Id);
        }
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

    /// <summary>Удалить запись. <paramref name="deletedTogether"/> — записи, удаляемые в той же
    /// операции по одной (входящее сообщение): ссылки из них запрету удаления не мешают.</summary>
    public void DeleteRecord(int recordId, IReadOnlyCollection<int>? deletedTogether = null)
    {
        Demand(PermAction.DeleteRecord, CollectionOfRecord(recordId));
        EnsureDeleteAllowed([recordId], deletedTogether);
        var entity = db.DbRecords.Find(recordId);
        if (entity is not null)
        {
            Log("delete", "record", entity.Title, CollectionNameOf(entity.CollectionId));
            // Входящие связи в этой же БД (цель = удаляемая запись): FK на ToRecordId снят (цель
            // может быть межбазовой), чистим прикладно. Исходящие уходят каскадом FromRecordId.
            var incoming = db.DbRecordLinks.Where(l => l.TargetDbId == 0 && l.ToRecordId == recordId).ToList();
            db.DbRecordLinks.RemoveRange(incoming);
            db.DbRecords.Remove(entity);
        }
        db.SaveChanges();
        if (entity is not null) NotifyRelationDataChanged(entity.CollectionId);
    }

    public DbRecord DuplicateRecord(int recordId)
    {
        Demand(PermAction.DuplicateRecord, CollectionOfRecord(recordId));
        var src = db.DbRecords.Find(recordId);
        if (src is null) throw new InvalidOperationException($"Record {recordId} not found");
        // Связи на записи вне условия выбора копия не получает (иначе дубль стал бы обходом условия).
        // Решается до сохранения копии: сбой базы цели не оставит копию без связей.
        var (refused, problems) = RefusedDuplicateTargets(src.CollectionId, [recordId]);
        var refusedSet = refused.ToHashSet();

        var copy = new DbRecordEntity
        {
            CollectionId = src.CollectionId,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = PositionAfterRecord(src.CollectionId, recordId),   // сразу после исходной
            StartDt      = src.StartDt,
            EndDt        = src.EndDt,
            AllDay       = src.AllDay,
            Title        = src.Title,
            Color        = src.Color,
        };
        db.DbRecords.Add(copy);
        Log("duplicate", "record", src.Title, CollectionNameOf(src.CollectionId));
        db.SaveChanges();

        var fieldValues = db.DbFieldValues.Where(fv => fv.RecordId == recordId).ToList();
        foreach (var fv in fieldValues)
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = copy.Id, FieldId = fv.FieldId, Value = fv.Value });
        db.SaveChanges();

        // Исходящие связи (relation): копия ссылается на те же записи-цели (ToRecordId/Position без
        // изменений), FromRecordId — новая копия. Входящие ссылки на исходную запись НЕ копируются.
        var links = db.DbRecordLinks.AsNoTracking().Where(l => l.FromRecordId == recordId).ToList();
        var skipped = links.Where(l => refusedSet.Contains((l.FieldId, l.TargetDbId, l.ToRecordId)))
            .GroupBy(l => l.FieldId).ToDictionary(g => g.Key, g => new SkippedLinks(g.Count(), problems.GetValueOrDefault(g.Key)));
        links = links.Where(l => !refusedSet.Contains((l.FieldId, l.TargetDbId, l.ToRecordId))).ToList();
        ReportSkippedDuplicateLinks(skipped, 1);
        if (links.Count > 0)
        {
            foreach (var l in links)
                db.DbRecordLinks.Add(new DbRecordLinkEntity
                {
                    FieldId = l.FieldId, FromRecordId = copy.Id, ToRecordId = l.ToRecordId,
                    TargetDbId = l.TargetDbId, Position = l.Position,
                });
            db.SaveChanges();
        }

        // Копия записи могла принести значения видимых полей-дат — наполняем проекцию календаря.
        RebuildCalendarIndexForRecord(copy.Id);
        NotifyRelationDataChanged(copy.CollectionId, recordId: copy.Id);

        return new DbRecord
        {
            Id           = copy.Id,
            CollectionId = copy.CollectionId,
            Position     = copy.Position,
            Title        = copy.Title,
            Color        = copy.Color,
        };
    }

    /// <summary>Дубль ОДНОЙ записи (копия сразу после исходной, по середине — без сдвига хвоста) на
    /// ОТДЕЛЬНОМ подключении: чтобы контроллер выполнял вставку в фоне (Task.Run) и обновлял таблицу
    /// уже после — без «моргания», которое дает синхронная вставка-плюс-refresh в одном кадре UI.</summary>
    public DbRecord DuplicateRecordIsolated(int recordId)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        // Лукап через свежий контекст: обертку зовут из фонового потока, общий контекст —
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
        var repo = new ConstructorRepository(ctx) { FileAutoPermissions = AutoFilePermissions };
        if (afterRecordId is { } aid) return repo.AddRecordAfter(record, aid);
        record.Position = repo.AllocatePositions(record.CollectionId, null, 1).At(0);
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
        var repo = new ConstructorRepository(ctx) { ReferenceViewer = Permissions };
        // Право проверяем по коллекции ПЕРВОЙ записи (мультивыделение таблицы всегда
        // в одной коллекции); лукап — через свежий контекст (обертка работает в фоне).
        if (recordIds.Count > 0)
            Demand(PermAction.DeleteRecord, repo.CollectionOfRecord(recordIds[0]));
        repo.DeleteRecordsData(recordIds, progress);
    }

    /// <summary>Удалить записи ПАКЕТАМИ по списку их Id (значения и calendar_index уходят каскадом по
    /// FK ON DELETE CASCADE). record_display/ui_row_heights с записью FK-связи НЕ имеют (каскада нет) —
    /// чистим явно, иначе настройки видимости/высоты «осиротеют» и могут прилипнуть к новой записи с
    /// тем же rowid. Пакеты — ради гранулярности прогресса.</summary>
    public void DeleteRecordsData(IReadOnlyList<int> recordIds, IProgress<int>? progress = null, int batchSize = 10000,
                                  bool log = true, bool enforceRestrict = true)
    {
        if (recordIds.Count == 0) return;
        var ids = recordIds.Distinct().ToList();
        if (enforceRestrict) EnsureDeleteAllowed(ids);
        var affectedCollections = db.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => r.CollectionId).Distinct().ToList();

        // Одна строка журнала на пакет; имя коллекции — до удаления (мультивыделение
        // таблицы всегда в одной коллекции).
        var colName = log ? CollectionNameOf(CollectionOfRecord(ids[0])) : null;

        using var tx = db.Database.BeginTransaction();
        var done = 0;
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var inList = string.Join(",", ids.Skip(i).Take(batchSize));
            // Входящие связи в этой же БД (цель ∈ удаляемых): FK на ToRecordId снят — чистим явно.
            // Исходящие связи удаляемых записей уходят каскадом FromRecordId вместе с db_records.
            // EF1002 «возможная SQL-инъекция»: в строку подставляются только целые идентификаторы
            // и имена таблиц, заданные здесь же в коде. Значений от пользователя в ней нет — те
            // уходят параметрами. Сырой SQL намеренно: пакетные DELETE и INSERT…SELECT на тысячах
            // строк параметрами не выражаются.
            #pragma warning disable EF1002
            db.Database.ExecuteSqlRaw($"DELETE FROM db_record_links WHERE TargetDbId = 0 AND ToRecordId IN ({inList})");
            db.Database.ExecuteSqlRaw($"DELETE FROM db_records     WHERE Id       IN ({inList})");
            db.Database.ExecuteSqlRaw($"DELETE FROM record_display WHERE RecordId IN ({inList})");
            db.Database.ExecuteSqlRaw($"DELETE FROM ui_row_heights WHERE RecordId IN ({inList})");
            #pragma warning restore EF1002
            done = Math.Min(ids.Count, i + batchSize);
            progress?.Report(done);
        }
        foreach (var collection in affectedCollections) NotifyRelationDataChanged(collection);
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

    /// <summary>То же, что <see cref="DuplicateRecordsIsolated"/>, но возвращает СООТВЕТСТВИЕ
    /// «исходная запись → ее копия». Нужно календарю: он строит из копий события (дельта Журнала,
    /// якорь прокрутки, перенос выделения) и обязан знать, какому источнику какая копия досталась;
    /// таблице хватает голого списка id для выделения.
    ///
    /// Опирается на общий контракт обоих bulk-путей (<see cref="DuplicateRecordsData"/> и его
    /// PG-вариант): id копий возвращаются в порядке РАНГА источников — <c>ORDER BY Position, Id</c>.
    /// Тем же ключом сортируем источники и сшиваем попарно. Мусорные id обе стороны отсекают
    /// одинаково (фильтр по коллекции), поэтому длины совпадают.</summary>
    public List<(int SrcId, int NewId)> DuplicateRecordsMappedIsolated(
        int collectionId, IReadOnlyList<int> recordIds, IProgress<int>? progress = null)
    {
        Demand(PermAction.DuplicateRecord, collectionId);
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var repo = new ConstructorRepository(ctx);
        var ids  = recordIds.Distinct().ToList();

        // Порядок источников снимаем ДО дубля: сами выбранные записи он не двигает (сдвигается только
        // хвост коллекции), но зависеть от этого незачем.
        var ordered = repo.RecordIdsInDuplicateOrder(collectionId, ids);
        var newIds  = repo.DuplicateRecordsData(collectionId, ids, progress);
        return ordered.Zip(newIds, (src, created) => (src, created)).ToList();
    }

    // Id записей коллекции из набора в порядке (Position, Id) — том самом ранге, в котором оба
    // bulk-пути дубля раскладывают копии.
    private List<int> RecordIdsInDuplicateOrder(int collectionId, IReadOnlyList<int> ids) =>
        db.DbRecords.AsNoTracking()
          .Where(r => r.CollectionId == collectionId && ids.Contains(r.Id))
          .OrderBy(r => r.Position).ThenBy(r => r.Id)
          .Select(r => r.Id)
          .ToList();

    /// <summary>Дубль выбранных записей коллекции bulk-SQL: копии встают СРАЗУ ПОСЛЕ последней (самой
    /// нижней по порядку Position, Id) выбранной записи — как дубль одной записи ставит копию следом за
    /// исходной, а не в конец таблицы. Поля те же (одна коллекция) → FieldId не перемэппливаем. Все в одной
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

        // Связи на записи вне условия выбора копии не получают (см. RefusedDuplicateTargets).
        var (refused, problems) = RefusedDuplicateTargets(collectionId, ids);
        using var tx = db.Database.BeginTransaction();

        // Временный набор выбранных Id — чтобы ранг (ROW_NUMBER по Position,Id) и JOIN значений шли
        // по одному подмножеству. Живет на этом подключении; пересоздаем.
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

        // Якорь — ПОСЛЕДНЯЯ (самая нижняя) выбранная запись в порядке (Position, Id): ее Position, и среди
        // делящих эту Position — максимальный Id. Копии встанут сразу после нее.
        var anchorPos = ScalarInt($@"
            SELECT MAX(r.Position) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
            WHERE r.CollectionId = {collectionId}");
        var anchorId  = ScalarInt($@"
            SELECT MAX(r.Id) FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
            WHERE r.CollectionId = {collectionId} AND r.Position = {anchorPos}");

        // Номера копий сразу после якоря (AllocatePositions): раздвигаются только ближайшие следующие
        // записи, хвост коллекции не трогается. Сами выбранные (Position ≤ anchorPos, при равенстве
        // Id ≤ anchorId) не двигаются, поэтому их ранг для значений совпадет с рангом при вставке копий.
        var run = AllocatePositions(collectionId, (anchorPos, anchorId), count);

        var createdAt = DateTime.Now.ToString("o");
        var createdBy = CurrentUser.DisplayName;   // дубль = новая запись (свой автор/время)

        // 1) Копии записей сразу после якоря, номера — по рангу (Position, Id) исходников. Новые rowid —
        //    непрерывный блок в этом порядке (мы единственный писатель в транзакции).
        // EF1002: в строку подставляются только числа номеров, посчитанные здесь же.
        #pragma warning disable EF1002
        db.Database.ExecuteSqlRaw($@"
            INSERT INTO db_records (CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Color)
            SELECT {{0}}, {{1}}, {{1}}, {{2}}, {{2}},
                   {run.Sql("ROW_NUMBER() OVER (ORDER BY r.Position, r.Id)")},
                   r.StartDt, r.EndDt, r.AllDay, r.Title, r.Color
            FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
            WHERE r.CollectionId = {{0}}
            ORDER BY r.Position, r.Id", collectionId, createdAt, createdBy);
        #pragma warning restore EF1002
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

        // Связи (relation) одним INSERT…SELECT: FromRecordId → новый id по тому же рангу, что и значения
        // ({firstNew} + ранг − 1); ToRecordId/Position без изменений — копия ссылается на те же цели.
        db.Database.ExecuteSqlInterpolated($@"
            INSERT INTO db_record_links (FieldId, FromRecordId, ToRecordId, TargetDbId, Position)
            SELECT l.FieldId, {firstNew} + (r.rn - 1), l.ToRecordId, l.TargetDbId, l.Position
            FROM db_record_links l
            JOIN (SELECT s.src_id AS old_id,
                         ROW_NUMBER() OVER (ORDER BY rr.Position, rr.Id) AS rn
                  FROM _dup_recsel s JOIN db_records rr ON rr.Id = s.src_id
                  WHERE rr.CollectionId = {collectionId}) r
              ON r.old_id = l.FromRecordId");
        var skipped = refused.Count == 0 ? []
            : DropRefusedDuplicateLinks(refused, problems, $"SELECT Id FROM db_records WHERE Id BETWEEN {firstNew} AND {firstNew + count - 1}");

        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS temp._dup_recsel");
        NotifyRelationDataChanged(collectionId);
        tx.Commit();
        ReportSkippedDuplicateLinks(skipped, count);

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
        var (refused, problems) = RefusedDuplicateTargets(collectionId, ids);
        Dictionary<int, SkippedLinks> skipped = [];
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

            var run = AllocatePositions(collectionId, (anchorPos, anchorId), count);

            db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS _dup_rmap");
            // EF1002 «возможная SQL-инъекция»: в строку подставляются только целые идентификаторы
            // и имена таблиц, заданные здесь же в коде. Значений от пользователя в ней нет — те
            // уходят параметрами. Сырой SQL намеренно: пакетные DELETE и INSERT…SELECT на тысячах
            // строк параметрами не выражаются.
            #pragma warning disable EF1002
            db.Database.ExecuteSqlRaw($@"
                CREATE TEMP TABLE _dup_rmap AS
                SELECT q.old_id, nextval(pg_get_serial_sequence('db_records', 'id')) AS new_id, q.rn
                FROM (SELECT r.Id AS old_id, ROW_NUMBER() OVER (ORDER BY r.Position, r.Id) AS rn
                      FROM db_records r JOIN _dup_recsel s ON s.src_id = r.Id
                      WHERE r.CollectionId = {collectionId}
                      ORDER BY rn) q");

            // Номер копии — по рангу; id выдаются в том же порядке (выборка выше упорядочена), чтобы
            // копии с одинаковым номером (места у якоря нет) шли по рангу.
            var createdAt = DateTime.Now.ToString("o");
            var createdBy = CurrentUser.DisplayName;   // дубль = новая запись (свой автор/время)
            db.Database.ExecuteSqlRaw($@"
                INSERT INTO db_records (Id, CollectionId, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy, Position, StartDt, EndDt, AllDay, Title, Color)
                SELECT m.new_id, {{0}}, {{1}}, {{1}}, {{2}}, {{2}}, {run.Sql("m.rn")},
                       r.StartDt, r.EndDt, r.AllDay, r.Title, r.Color
                FROM db_records r JOIN _dup_rmap m ON m.old_id = r.Id", collectionId, createdAt, createdBy);
            #pragma warning restore EF1002

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

            // Связи (relation): FromRecordId → new_id по карте _dup_rmap; цели/позиции без изменений.
            db.Database.ExecuteSqlRaw(@"
                INSERT INTO db_record_links (FieldId, FromRecordId, ToRecordId, TargetDbId, Position)
                SELECT l.FieldId, m.new_id, l.ToRecordId, l.TargetDbId, l.Position
                FROM db_record_links l
                JOIN _dup_rmap m ON m.old_id = l.FromRecordId");
            if (refused.Count > 0) skipped = DropRefusedDuplicateLinks(refused, problems, "SELECT new_id FROM _dup_rmap");

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
            NotifyRelationDataChanged(collectionId);
            tx.Commit();
        }
        ReportSkippedDuplicateLinks(skipped, ids.Count);

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
    // Грамотный перенос: значения сопоставляются с полями целевой коллекции ПО ИМЕНИ (без учета
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

        // Перенос — не назначение: значения переезжают в ДРУГУЮ запись, исполнитель и стадия
        // у задачи не менялись. Без этого каждый перенос записи слал бы исполнителю ложное
        // «вам назначена задача».
        using var quiet = SuppressTaskEvents();

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
                // Флаги разделов не копируем — ни задачные (см. MoveRecordsCore), ни календарный:
                // перенос ОДНОЙ записи не должен молча включать коллекцию-приемник в «Задачи» или
                // в календарь. Столбец приезжает пустой ролью, а показывать его в разделе — решение
                // владельца коллекции-приемника (тумблер в свойствах). Копию/перенос КОЛЛЕКЦИИ это
                // не касается: там коллекция остается собой и флаги едут следом.
                target = AddField(new DbField
                {
                    CollectionId = targetCollectionId, Name = meta.Name,
                    FieldType    = meta.FieldType,      ConfigJson = meta.ConfigJson,
                    Position     = nextPos,             IsCalStart = false,
                    IsCalEnd     = meta.IsCalEnd,       IsRequired = meta.IsRequired,
                });
                nextPos += 1000;
                targetByName[meta.Name] = target;
            }

            if (target.Id == excludeTargetFieldId) continue;
            if (FieldTypeInfo.IsRelationComputed(meta.FieldType) || FieldTypeInfo.IsRelationComputed(target.FieldType)) continue;
            SetFieldValue(targetRecordId, target.Id!.Value, ConvertValueForType(value, meta.FieldType, target.FieldType));
        }
    }

    /// <summary>Пакетный перенос записей из коллекции <paramref name="sourceCollectionId"/> в целевую
    /// коллекцию этого репозитория на ОТДЕЛЬНЫХ подключениях (фон + прогресс) — чтобы не блокировать
    /// UI-контексты. <paramref name="sourceDbPath"/> — путь БД-источника (совпадает с этим репо при
    /// переносе внутри одной БД). Возвращает id созданных записей.</summary>
    public List<int> MoveRecordsToCollectionIsolated(
        string sourceDbPath, int sourceCollectionId, IReadOnlyList<int> recordIds, int targetCollectionId,
        bool createMissingFields, int sourceDbId, int targetDbId, IProgress<int>? progress = null)
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
        var targetRepo = new ConstructorRepository(targetCtx) { ReferenceViewer = Permissions };

        var sameDb = string.Equals(sourceDbPath, DbPath, StringComparison.OrdinalIgnoreCase);
        AppDbContext? sourceCtx = null;
        if (!sameDb) { sourceCtx = new AppDbContext(sourceDbPath); sourceCtx.ApplyPerformancePragmas(); }
        var sourceRepo = sameDb ? targetRepo : new ConstructorRepository(sourceCtx!);

        // Перенос — копия и удаление оригинала: связь с запретом удаления держит и перенос, в том
        // числе ссылки между переносимыми записями (копии их не сохраняют). Проверка — до копии.
        sourceRepo.EnsureDeleteAllowed(recordIds, move: true);
        try { return targetRepo.MoveRecordsCore(sourceRepo, sourceCollectionId, recordIds, targetCollectionId, createMissingFields, sourceDbId, targetDbId, progress); }
        finally { sourceCtx?.Dispose(); }
    }

    /// <summary>
    /// Что ПОТЕРЯЕТСЯ при переносе записей в другую коллекцию — для окна подтверждения ДО старта.
    /// <c>this</c> — ЦЕЛЕВОЙ репозиторий (как у <see cref="MoveRecordsCore"/>), источник передается.
    ///
    /// Считается по СХЕМЕ — два списка полей (<c>db_fields</c>, десятки строк), а не по данным:
    /// стоимость не зависит от числа переносимых записей, скана значений и ребер нет. Поэтому здесь,
    /// в отличие от проверки входящих связей (<see cref="RelationDeleteImpact"/>, миллионы ребер →
    /// только ФАКТ через EXISTS), можно позволить себе полный список ИМЕН: решение «отменять ли
    /// перенос» зависит именно от того, какие свойства пропадут.
    ///
    /// ВАЖНО: правила обязаны совпадать с планом в <see cref="MoveRecordsCore"/> (сопоставление по
    /// имени + для связей совместимость целевой коллекции и ПЕРЕПИСАННОЙ целевой БД) — иначе окно
    /// соврет. Меняешь план — меняй и здесь.
    /// </summary>
    /// <returns>
    /// Dropped — свойств нет в целевой коллекции и создавать их не просили (значения не переедут);
    /// LinksDropped — поле-связь есть, но несовместимо (само поле переедет, связи — нет).
    /// </returns>
    public (List<string> Dropped, List<string> LinksDropped) FieldsLostOnMove(
        ConstructorRepository source, int sourceCollectionId, int targetCollectionId,
        bool createMissingFields, int sourceDbId, int targetDbId)
    {
        // Те же формулы, что в MoveRecordsCore.
        var sameDatabase = ReferenceEquals(db, source.RelationLifecycleContext)
                           || string.Equals(DbPath, source.DbPath, StringComparison.Ordinal);
        var canMoveLinks = sameDatabase || sourceDbId > 0 && targetDbId > 0;
        int RelTargetDb(int cfgOrLinkDb)
        {
            if (sameDatabase) return sourceDbId > 0 && cfgOrLinkDb == sourceDbId ? 0 : cfgOrLinkDb;
            var abs = cfgOrLinkDb == 0 ? sourceDbId : cfgOrLinkDb;
            return abs == targetDbId ? 0 : abs;
        }

        var targetByName = new Dictionary<string, DbField>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in GetFields(targetCollectionId)) targetByName.TryAdd(tf.Name, tf);

        var dropped      = new List<string>();
        var linksDropped = new List<string>();
        foreach (var sf in source.GetFields(sourceCollectionId))
        {
            if (!targetByName.TryGetValue(sf.Name, out var tf))
            {
                // Поля в цели нет: либо создадим (переедет все), либо свойство молча пропадет.
                if (!createMissingFields) dropped.Add(sf.Name);
                // Созданное поле-связь получает переписанный конфиг — но без известных DbId ребра
                // все равно не копируются.
                else if (sf.FieldType == "relation" && !canMoveLinks) linksDropped.Add(sf.Name);
                continue;
            }
            // Не-связь: значение переносится по имени с приведением типа (CopyFieldValuesByName).
            if (sf.FieldType != "relation") continue;

            var sc = RelationConfig.FromConfig(sf.ConfigJson);
            var tc = RelationConfig.FromConfig(tf.ConfigJson);
            var ok = canMoveLinks && tf.FieldType == "relation" && sc is not null && tc is not null
                     && tc.TargetCollectionId == sc.TargetCollectionId
                     && tc.TargetDbId == RelTargetDb(sc.TargetDbId);
            if (!ok) linksDropped.Add(sf.Name);
        }
        return (dropped, linksDropped);
    }

    // Перенос записей выполняется на контекстах targetRepo (this) и sourceRepo. Главное про
    // производительность (как DuplicateRecordsData/DeleteRecordsData, см. docs/architecture/performance):
    //   • план сопоставления полей считается ОДИН раз — все записи из одной коллекции-источника;
    //   • значения каждой записи пишутся одним AddRange (без SaveChanges на каждое значение и без
    //     инкрементной синхронизации индекса);
    //   • проекция календаря наполняется ОДИН раз по диапазону новых id (новые id непрерывны —
    //     мы единственный писатель);
    //   • исходные записи удаляются bulk-ом (DeleteRecordsData, каскад чистит значения и индекс);
    //   • копирование идет в одной транзакции (один коммит), трекер EF чистим после каждой записи —
    //     память ограничена независимо от размера пакета.
    private List<int> MoveRecordsCore(ConstructorRepository source, int sourceCollectionId,
        IReadOnlyList<int> recordIds, int targetCollectionId, bool createMissingFields,
        int sourceDbId, int targetDbId, IProgress<int>? progress)
    {
        // Переинтерпретация целевой БД связи под целевую базу переноса. «Абсолютная» БД цели: 0 в
        // конфиге/ребре означает БД ИСТОЧНИКА (sourceDbId). В целевой базе она снова 0, если совпала
        // с назначением (стала внутрибазовой), иначе — межбазовая на ту же абсолютную БД. Одна формула
        // покрывает и внутрибазовый перенос (source==target: 0→0), и межбазовый (0→sourceDbId и т.д.).
        var sameDatabase = ReferenceEquals(db, source.RelationLifecycleContext)
                           || string.Equals(DbPath, source.DbPath, StringComparison.Ordinal);
        var canMoveLinks = sameDatabase || sourceDbId > 0 && targetDbId > 0;
        int RelTargetDb(int cfgOrLinkDb)
        {
            if (sameDatabase) return sourceDbId > 0 && cfgOrLinkDb == sourceDbId ? 0 : cfgOrLinkDb;
            var abs = cfgOrLinkDb == 0 ? sourceDbId : cfgOrLinkDb;
            return abs == targetDbId ? 0 : abs;
        }
        string RewriteRelConfig(DbField sf)
        {
            if (RelationConfig.FromConfig(sf.ConfigJson) is not { } sc) return sf.ConfigJson;
            return (sc with { TargetDbId = RelTargetDb(sc.TargetDbId) }).ToConfigJson();
        }

        // 1) План: srcFieldId → (целевое поле, типы). Недостающие свойства создаем ОДИН раз (при флаге),
        //    до транзакции копирования (AddField коммитит сам).
        var srcFields    = source.GetFields(sourceCollectionId);
        var targetByName = new Dictionary<string, DbField>(StringComparer.OrdinalIgnoreCase);
        foreach (var tf in GetFields(targetCollectionId)) targetByName.TryAdd(tf.Name, tf);
        // Кардинальность проверяем до создания недостающих полей и до записи/удаления данных.
        // Нельзя молча выбирать одну из нескольких целей при переносе в существующий single.
        foreach (var sf in srcFields.Where(f => f.FieldType == "relation"))
        {
            if (!canMoveLinks || !targetByName.TryGetValue(sf.Name, out var tf) || tf.FieldType != "relation") continue;
            var sc = RelationConfig.FromConfig(sf.ConfigJson);
            var tc = RelationConfig.FromConfig(tf.ConfigJson);
            if (sc is not null && tc is { Single: true } && tc.TargetCollectionId == sc.TargetCollectionId
                && tc.TargetDbId == RelTargetDb(sc.TargetDbId))
                EnsureSingleRelationMove(source, sf.Id!.Value, recordIds, tf.Name);
        }

        var nextFieldPos = (db.DbFields.Where(f => f.CollectionId == targetCollectionId)
                                       .Select(f => (int?)f.Position).Max() ?? 0) + 1000;

        var plan = new Dictionary<int, (int FieldId, string From, string To)>();
        var createdFields = new HashSet<int>();
        // Недостающие свойства и перепись их ключей — одна операция: копии ссылаются на номера
        // свойств источника, и сбой между созданием и переписью оставил бы готовое правило или
        // столбец данных, указывающие на чужие свойства.
        using var schemaTx = createMissingFields && db.Database.CurrentTransaction is null ? db.Database.BeginTransaction() : null;
        foreach (var sf in srcFields)
        {
            if (!targetByName.TryGetValue(sf.Name, out var tf))
            {
                if (!createMissingFields) continue;
                // Для создаваемого поля-связи переписываем целевую БД под целевую базу переноса
                // (иначе TargetDbId «поедет»: внутрибазовая связь на источник станет ссылкой в никуда).
                var cfgJson = canMoveLinks && sf.FieldType == "relation" ? RewriteRelConfig(sf) : sf.ConfigJson;
                // Флаги разделов (IsTaskExec/IsTaskAssigner/IsTaskStatus и календарный IsCalStart)
                // НЕ копируем: они подписывают на раздел ВСЮ коллекцию-приемник, а пользователь
                // просил перенести записи, а не превращать «Клиентов» в задачи и не заливать
                // календарь чужими датами. Роль — свойство коллекции, а не переносимых значений;
                // перенос ЦЕЛОЙ коллекции (DuplicateCollection, CrossMove) ее сохраняет — там
                // коллекция и остается собой. Показать приехавшую дату в календаре — решение
                // владельца приемника, тумблером в свойствах столбца.
                // Автосвязь создается без подбора: ее ключи — номера свойств источника, их перепишет
                // RemapCopiedRelationConfigs ниже, а подбор запустит уведомление после переноса.
                tf = AddField(new DbField
                {
                    CollectionId = targetCollectionId, Name = sf.Name,
                    FieldType    = sf.FieldType,        ConfigJson = cfgJson,
                    Position     = nextFieldPos,        IsCalStart = false,
                    IsCalEnd     = sf.IsCalEnd,         IsRequired = sf.IsRequired,
                }, copy: true);
                nextFieldPos += 1000;
                targetByName[sf.Name] = tf;
                createdFields.Add(tf.Id!.Value);
            }
            plan[sf.Id!.Value] = (tf.Id!.Value, sf.FieldType, tf.FieldType);
        }
        if (createdFields.Count > 0)
            RemapCopiedRelationConfigs(new Dictionary<int, int>(), plan.ToDictionary(p => p.Key, p => p.Value.FieldId),
                0, 0, onlyNewFieldIds: createdFields);
        schemaTx?.Commit();

        // Связи (relation): переносим там, где исходное И целевое поле — «Связь», а конфиг цели указывает
        // на ту же коллекцию И ту же (переписанную под целевую базу) целевую БД. Созданные выше поля уже
        // с переписанным конфигом; для существующих полей-совпадений по имени проверяем совместимость.
        var relPlan = new List<(int SrcFieldId, int TgtFieldId)>();
        if (canMoveLinks)
            foreach (var sf in srcFields)
            {
                if (sf.FieldType != "relation" || !targetByName.TryGetValue(sf.Name, out var tf) || tf.FieldType != "relation") continue;
                var sc = RelationConfig.FromConfig(sf.ConfigJson);
                var tc = RelationConfig.FromConfig(tf.ConfigJson);
                if (sc is null || tc is null) continue;
                if (tc.TargetCollectionId != sc.TargetCollectionId || tc.TargetDbId != RelTargetDb(sc.TargetDbId)) continue;
                if (tc.Single) EnsureSingleRelationMove(source, sf.Id!.Value, recordIds, tf.Name);
                relPlan.Add((sf.Id!.Value, tf.Id!.Value));
            }

        // Новые записи приемника не получают связей вне условия выбора его свойства — как дубль,
        // иначе перенос стал бы обходом условия. Решается до транзакции, по всем переносимым записям.
        var conditionChecks = new Dictionary<int, (RelationConditionVerdict Verdict, int TargetDbId)>();
        foreach (var (srcFid, tgtFid) in relPlan)
        {
            var tc = RelationConfig.FromConfig(db.DbFields.AsNoTracking().Where(f => f.Id == tgtFid)
                .Select(f => f.ConfigJson).FirstOrDefault());
            if (tc is not { HasCondition: true }) continue;
            var targets = recordIds.SelectMany(id => source.GetLinkEdges(srcFid, id))
                .Where(e => RelTargetDb(e.TargetDbId) == tc.TargetDbId).Select(e => e.ToRecordId).ToHashSet();
            if (targets.Count > 0) conditionChecks[tgtFid] = (EvaluateConditionOnTarget(tc, targets), tc.TargetDbId);
        }
        var skippedLinks = new Dictionary<int, SkippedLinks>();

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
                // Аудит переносим ЦЕЛИКОМ (четыре поля): «Расположение» перекладывает запись,
                // а не создает новую. Иначе StampNewRecordAudit (пустые CreatedBy/UpdatedBy) и
                // CreatedAt = «сейчас» показывали бы запись коллеги созданной вами сегодня —
                // при том что перенос КОЛЛЕКЦИИ аудит сохраняет (INSERT … SELECT CreatedAt,
                // UpdatedAt, CreatedBy, UpdatedBy), и продукт противоречил сам себе.
                // У старых записей поля могут быть пусты — тогда штамп сработает как раньше.
                var rec = new DbRecordEntity
                {
                    CollectionId = targetCollectionId, CreatedAt = header.CreatedAt ?? DateTime.Now.ToString("o"),
                    UpdatedAt    = header.UpdatedAt ?? "",
                    CreatedBy    = header.CreatedBy ?? "",
                    UpdatedBy    = header.UpdatedBy ?? "",
                    Position     = posBase,            StartDt   = header.StartDt,
                    EndDt        = header.EndDt,       AllDay    = header.AllDay,
                    Title        = header.Title,
                };
                db.DbRecords.Add(rec);
                db.SaveChanges();   // нужен Id записи для значений (в рамках транзакции, без коммита)

                var values = source.GetFieldValues(srcId);
                var rows   = new List<DbFieldValueEntity>(values.Count);
                foreach (var (srcFieldId, value) in values)
                {
                    if (value.Length == 0 || !plan.TryGetValue(srcFieldId, out var t)) continue;
                    if (FieldTypeInfo.IsRelationComputed(t.From) || FieldTypeInfo.IsRelationComputed(t.To)) continue;
                    rows.Add(new DbFieldValueEntity
                    {
                        RecordId = rec.Id, FieldId = t.FieldId,
                        Value    = ConvertValueForType(value, t.From, t.To),
                    });
                }
                if (rows.Count > 0) { db.DbFieldValues.AddRange(rows); db.SaveChanges(); }

                // Связи-relation переносимых полей: цели/порядок исходной записи → на новую запись,
                // TargetDbId переписан под целевую базу (внутрибазовая связь на источник становится
                // межбазовой, связь на базу-назначение — внутрибазовой, на третью базу — без изменений).
                if (relPlan.Count > 0)
                {
                    var linkRows = new List<DbRecordLinkEntity>();
                    foreach (var (srcFid, tgtFid) in relPlan)
                    {
                        var edges = source.GetLinkEdges(srcFid, srcId);
                        var position = 0;
                        foreach (var edge in edges)
                        {
                            var targetDb = RelTargetDb(edge.TargetDbId);
                            if (conditionChecks.TryGetValue(tgtFid, out var check) && targetDb == check.TargetDbId
                                && !check.Verdict.Allowed.Contains(edge.ToRecordId))
                            {
                                skippedLinks[tgtFid] = new(skippedLinks.GetValueOrDefault(tgtFid).Count + 1, check.Verdict.Broken);
                                continue;
                            }
                            linkRows.Add(new DbRecordLinkEntity
                            {
                                FieldId = tgtFid, FromRecordId = rec.Id, ToRecordId = edge.ToRecordId,
                                TargetDbId = targetDb, Position = ++position * 1000,
                            });
                        }
                    }
                    if (linkRows.Count > 0) { db.DbRecordLinks.AddRange(linkRows); db.SaveChanges(); }
                }

                newIds.Add(rec.Id);
                copiedSources.Add(srcId);

                db.ChangeTracker.Clear();   // запись и значения уже во вставке транзакции — трекер не нужен
                progress?.Report(++done);
            }
            NotifyRelationDataChanged(targetCollectionId);
            tx.Commit();
        }
        ReportSkippedLinks(skippedLinks, newIds.Count == 1 ? "Запись перенесена." : "Записи перенесены.", "перенесено");

        // 3) Проекция календаря — один раз по СПИСКУ новых id (EF переведет Contains в IN):
        // диапазон firstNew..lastNew полагался на непрерывность новых id — на PG соседние id
        // могли достаться другим клиентам (общая identity-последовательность).
        if (newIds.Count > 0)
            FillCalendarIndex(q => q.Where(v => newIds.Contains(v.RecordId)));

        // 4) Исходные записи — bulk-удалением (каскад уносит их значения и calendar_index).
        // Без строки «удалил» в журнале — это перенос, одна строка «перенес» ниже.
        // Запрет проверен до копии; повторная проверка после коммита копий нашла бы ссылки самих
        // копий на оригиналы и оставила бы записи в двух местах.
        if (copiedSources.Count > 0) source.DeleteRecordsData(copiedSources, log: false, enforceRestrict: false);

        if (newIds.Count > 0)
            LogNow("move", "records", "", CollectionNameOf(targetCollectionId), newIds.Count);
        return newIds;
    }

    // ── Значения полей ────────────────────────────────────────────────────────

    // Фоновая авто-привязка пишет из отдельного контекста. Читаем сохраненные значения,
    // чтобы после «Сохранить» таблица и страница записи не возвращали прежний путь из tracker.
    public Dictionary<int, string> GetFieldValues(int recordId)
    {
        var values = db.DbFieldValues.AsNoTracking()
          .Where(fv => fv.RecordId == recordId)
          .ToDictionary(fv => fv.FieldId, fv => fv.Value);
        var batch = new Dictionary<int, Dictionary<int, string>> { [recordId] = values };
        FillRelationComputedValues([recordId], batch);
        return batch[recordId];
    }

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
        IReadOnlyDictionary<string, HashSet<string>> filters, bool excludeHidden = false,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign = null) =>
        FilteredRecords(collectionId, filters, excludeHidden, resolveForeign)
            .OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).ToList();

    /// <summary>
    /// Записи коллекции, прошедшие фильтры колонок, как запрос (без материализации) — общая
    /// основа списка id отфильтрованной таблицы (<see cref="GetFilteredRecordIds"/>) и
    /// Excel-каскада счетчиков в окне фильтра (<see cref="GetColumnCounts"/>): список значений
    /// колонки считается по тому же срезу, что показан в таблице. Условия колонок соединяются
    /// через И; внутри колонки — IN (...) по набору значений.
    /// </summary>
    private IQueryable<DbRecordEntity> FilteredRecords(int collectionId,
        IReadOnlyDictionary<string, HashSet<string>> filters, bool excludeHidden,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign)
    {
        var q = db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId
                && (!excludeHidden || !db.RecordDisplay.Any(d =>
                       d.CollectionId == collectionId && d.RecordId == r.Id && !d.Visible)));
        if (filters.Count == 0) return q;

        // Ключи-связи (db_record_links, а не db_field_values) — своя ветка фильтра.
        var relFieldIds = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && f.FieldType == "relation")
            .Select(f => f.Id).ToHashSet();
        // Чекбоксы — две группы, а не значения как есть: «отмечено» и все остальное (пусто, «0»
        // снятой галочки, текст, оставшийся от прежнего типа столбца). См. CheckboxValue.
        var boxFieldIds = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && f.FieldType == "checkbox")
            .Select(f => f.Id).ToHashSet();
        var fileFieldIds = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && f.FieldType == "file")
            .Select(f => f.Id).ToHashSet();
        var relatedComputed = db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && (f.FieldType == "lookup" || f.FieldType == "rollup"))
            .ToDictionary(f => f.Id, ToDbField);

        // ФОРМА УСЛОВИЯ ВАЖНА: набор значений уходит в SQL как `IN (json_each(@p))`, и SQLite
        // перебирает его заново на каждую строку, если он стоит внутри КОРРЕЛИРОВАННОГО EXISTS
        // (10 млн записей × 1444 значения «одного дня» — больше 5 минут). Поэтому строка
        // проверяется через `r.Id IN (SELECT RecordId ... WHERE Value IN (...))` — некоррелированный
        // подзапрос: список id собирается ОДИН раз по индексу (FieldId, Value), дальше только
        // проба по нему (те же 10 млн × 1444 — 12 с). Ветка «(Пусто)» коррелирована, но без списка.
        // Данные из связанных записей отбираются вторым проходом, по срезу остальных фильтров.
        var computedFilters = new List<(DbField Field, HashSet<string> Allowed)>();
        foreach (var (key, allowed) in filters)
        {
            // Отбор по цвету ячеек колонки (ColorQuery) — особый ключ, набор — метки цвета.
            if (ColorQuery.IsColorKey(key))
            {
                q = ColorFilter(q, collectionId, ColorQuery.ColumnOf(key), allowed);
                continue;
            }
            if (int.TryParse(key, out var computedId) && relatedComputed.TryGetValue(computedId, out var computedField))
            {
                computedFilters.Add((computedField, allowed));
                continue;
            }
            if (int.TryParse(key, out var boxId) && boxFieldIds.Contains(boxId))
            {
                q = CheckboxFilter(q, boxId, allowed);
                continue;
            }

            if (int.TryParse(key, out var fileId) && fileFieldIds.Contains(fileId))
            {
                q = FileFilter(q, fileId, allowed);
                continue;
            }

            // Отбор «кроме» (FilterExclusion): набор — исключаемые значения. Его дает окно фильтра колонки,
            // у которой значений больше, чем оно показывает; у связей такого окна нет.
            if (FilterExclusion.IsExclude(allowed))
            {
                var excl       = FilterExclusion.Values(allowed);
                var exclEmpty  = excl.Contains("");
                if (key == TitleKey)
                    q = q.Where(r => !excl.Contains(r.Title ?? ""));
                else if (int.TryParse(key, out var xf) && !relFieldIds.Contains(xf))
                    q = q.Where(r =>
                        !db.DbFieldValues.Where(fv => fv.FieldId == xf && excl.Contains(fv.Value))
                                         .Select(fv => fv.RecordId).Contains(r.Id)
                        && (!exclEmpty || db.DbFieldValues.Any(fv => fv.RecordId == r.Id && fv.FieldId == xf && fv.Value != "")));
                continue;
            }

            var allow = allowed.ToList();   // EF переведет Contains → SQL IN (...)
            if (key == TitleKey)
                q = q.Where(r => allow.Contains(r.Title));
            else if (int.TryParse(key, out var fid))
            {
                var hasEmpty = allowed.Contains("");
                if (relFieldIds.Contains(fid))
                {
                    // Связь: строка проходит, если есть ребро к ИМЕНОВАННОЙ цели с Title ∈ allowed
                    // (семантика ИЛИ). «(нет связей)» "" — нет ребра ни к одной именованной цели.
                    var config = RelationConfig.FromConfig(db.DbFields.AsNoTracking()
                        .Where(f => f.Id == fid).Select(f => f.ConfigJson).FirstOrDefault());
                    var hasCrossDb = Permissions is not null || config?.DisplayField != RelationConfig.TitleField ||
                        db.DbRecordLinks.Any(l => l.FieldId == fid && l.TargetDbId != 0);
                    if (!hasCrossDb)
                    {
                        // Чисто внутрибазовое поле — серверный IN по ребрам (без материализации ребер
                        // в памяти). TargetDbId==0 обязателен: иначе осиротевшее межбазовое ребро с
                        // коллизией id могло бы совпасть с чужим локальным Title.
                        q = q.Where(r =>
                            db.DbRecordLinks
                                .Where(l => l.FieldId == fid && l.TargetDbId == 0
                                    && db.DbRecords.Where(t => allow.Contains(t.Title ?? "")).Select(t => t.Id).Contains(l.ToRecordId))
                                .Select(l => l.FromRecordId).Contains(r.Id)
                            || (hasEmpty && !db.DbRecordLinks.Any(l => l.FieldId == fid && l.FromRecordId == r.Id && l.TargetDbId == 0
                                && db.DbRecords.Any(t => t.Id == l.ToRecordId && (t.Title ?? "") != ""))));
                    }
                    else
                    {
                        // Есть межбазовые ребра — цели (в т.ч. из ДРУГОЙ базы) резолвим тем же путем, что
                        // ячейка/счетчики (иначе межбазовые ссылки не фильтровались бы). Множества ограничены
                        // связанными записями.
                        var bySource     = RelationTitlesBySource(collectionId, fid, resolveForeign);
                        var matchIds     = bySource.Where(kv => kv.Value.Any(t => t.Length > 0 && allowed.Contains(t)))
                                                   .Select(kv => kv.Key).ToHashSet();
                        var namedSources = bySource.Where(kv => kv.Value.Any(t => t.Length > 0))
                                                   .Select(kv => kv.Key).ToHashSet();
                        q = q.Where(r => matchIds.Contains(r.Id) || (hasEmpty && !namedSources.Contains(r.Id)));
                    }
                }
                else
                {
                    q = q.Where(r =>
                        db.DbFieldValues.Where(fv => fv.FieldId == fid && allow.Contains(fv.Value))
                                        .Select(fv => fv.RecordId).Contains(r.Id)
                        || (hasEmpty && !db.DbFieldValues.Any(fv => fv.RecordId == r.Id && fv.FieldId == fid && fv.Value != "")));
                }
            }
        }

        foreach (var (field, allowed) in computedFilters) q = ComputedFilter(q, collectionId, field, allowed);
        return q;
    }

    // Отбор по данным из связанных записей. Малый срез считается сам и отбирается списком
    // подошедших. Иначе — столбец целиком: у большинства записей одно значение (Default, итог без
    // целей), поэтому список строится из меньшей стороны — подошедших или не подошедших записей с
    // ребрами.
    private IQueryable<DbRecordEntity> ComputedFilter(IQueryable<DbRecordEntity> q, int collectionId, DbField field,
        HashSet<string> allowed)
    {
        var excluded = FilterExclusion.IsExclude(allowed);
        var selected = excluded ? FilterExclusion.Values(allowed).ToHashSet() : allowed;
        bool Keep(string value) => selected.Contains(value) != excluded;
        var ct = ComputedToken(default);
        using var phase = ComputedRun?.Phase($"Столбец «{field.Name}»: чтение записей");
        ct.ThrowIfCancellationRequested();
        var scope = q.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).Take(SubsetLimit + 1).ToList();
        if (scope.Count <= SubsetLimit)
        {
            var valueOf = RelationComputedValuesFor(collectionId, field, scope, ct);
            var hit = scope.Where(id => Keep(valueOf(id))).ToList();
            return q.Where(r => hit.Contains(r.Id));
        }
        var column = ReadRelationComputedColumn(collectionId, field, ct);
        if (column.ColumnError is { } constant) return Keep(constant) ? q : q.Where(r => false);
        if (Keep(column.Default))
        {
            var miss = column.IdsWhere(v => !Keep(v));
            return q.Where(r => !miss.Contains(r.Id));
        }
        var matching = column.IdsWhere(Keep);
        return q.Where(r => matching.Contains(r.Id));
    }

    // Отбор по чекбоксу: в наборе (или в исключениях отбора «кроме») важна только группа значения.
    // Отмеченные — по индексу (FieldId, Value) некоррелированным списком id, как у остальных колонок.
    private IQueryable<DbRecordEntity> CheckboxFilter(IQueryable<DbRecordEntity> q, int fieldId, HashSet<string> allowed)
    {
        bool on, off;
        if (FilterExclusion.IsExclude(allowed))
        {
            var excl = FilterExclusion.Values(allowed);
            on  = !excl.Any(CheckboxValue.IsChecked);
            off = excl.All(CheckboxValue.IsChecked);
        }
        else
        {
            on  = allowed.Any(CheckboxValue.IsChecked);
            off = !allowed.All(CheckboxValue.IsChecked);
        }
        if (on && off) return q;

        var spellings = CheckboxValue.CheckedSpellings.ToList();
        var checkedIds = db.DbFieldValues.Where(fv => fv.FieldId == fieldId && spellings.Contains(fv.Value))
                                         .Select(fv => fv.RecordId);
        return on  ? q.Where(r => checkedIds.Contains(r.Id))
             : off ? q.Where(r => !checkedIds.Contains(r.Id))
             :       q.Where(r => false);
    }

    /// <summary>
    /// Значения одной колонки по всем записям коллекции (id → значение) — для сортировки
    /// отфильтрованного списка в UI тем же компаратором, что и раньше. Грузит ровно одну
    /// колонку, а не все поля (в отличие от GetFieldValuesByCollection).
    /// </summary>
    public Dictionary<int, string> GetColumnValues(int collectionId, string key,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign = null)
    {
        using var foreignScope = (resolveForeign?.Target as ForeignPropertyReader)?.OpenScope();
        if (key == TitleKey)
            return db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId)
                     .Select(r => new { r.Id, r.Title }).ToList()
                     .ToDictionary(x => x.Id, x => x.Title ?? "");
        if (int.TryParse(key, out var fid))
        {
            var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fid);
            if (field is not null && FieldTypeInfo.IsRelationComputed(field.FieldType))
                return GetRelationComputedColumnValues(collectionId, ToDbField(field));
            if (field?.FieldType == "relation")
            {
                // Сортировка «по показанному тексту»: значение = названия связанных записей (в т.ч. из
                // ДРУГОЙ базы) через разделитель поля, как в ячейке. Записи без связей → "" (в конец при А→Я).
                var sep = RelationConfig.FromConfig(field.ConfigJson)?.SeparatorString ?? ", ";
                return RelationTitlesBySource(collectionId, fid, resolveForeign)
                    .ToDictionary(kv => kv.Key, kv => string.Join(sep, kv.Value));
            }
            return db.DbFieldValues.AsNoTracking()
                     .Where(fv => fv.FieldId == fid && fv.Record.CollectionId == collectionId)
                     .Select(fv => new { fv.RecordId, fv.Value }).ToList()
                     .ToDictionary(x => x.RecordId, x => x.Value);
        }
        return new();
    }

    /// <summary>Записи по списку id в ТОМ ЖЕ порядке, что и переданный список (для постраничной
    /// подгрузки отфильтрованного/отсортированного представления). ids — одна страница (≤ PageSize).</summary>
    public List<DbRecord> GetRecordsByIdsOrdered(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return [];
        // Сырой читатель (как у страниц): терпит NULL в «Названии» старых файлов, на котором
        // EF-проекция падала бы (см. GetRecords). Список id — параметрами, страница ≤ PageSize.
        var names = ids.Select((_, i) => $"@i{i}").ToArray();
        var args  = ids.Select((id, i) => ($"@i{i}", (object)id)).ToArray();
        var byId  = ReadRecordRows($"SELECT {RecordCols} FROM db_records r WHERE r.Id IN ({string.Join(",", names)})", args)
            .ToDictionary(r => r.Id!.Value);

        var result = new List<DbRecord>(ids.Count);
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var r)) result.Add(r);
        return result;
    }

    // Фоновая страница таблицы (записи + значения окна колонок + оформление ячеек) —
    // LoadPageAsync в ConstructorRepository.GridPages.cs.

    // ── Фильтр колонок (Excel-стиль): уникальные значения с количеством ───────
    //
    // Порядок значений — как в таблице: по первой позиции записи с этим значением
    // (MIN(Position)); «(Пусто)» всегда последним. Это нужно панели фильтра — при открытии
    // значения идут в том же порядке, что строки таблицы, а сортировку пользователь выбирает сам.
    //
    // Счетчики считаются по СРЕЗУ записей (scope): вся коллекция у публичных методов, а у окна
    // фильтра — записи, прошедшие фильтры остальных колонок (Excel-каскад, см. GetColumnCounts).

    private IQueryable<DbRecordEntity> Records(int collectionId) =>
        db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);

    public List<(string Value, int Count)> GetTitleCounts(int collectionId) =>
        TitleCounts(Records(collectionId));

    private List<(string Value, int Count)> TitleCounts(IQueryable<DbRecordEntity> scope)
    {
        var groups = scope
            .GroupBy(r => r.Title)
            .Select(g => new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(r => r.Position) })
            .ToList();
        return OrderValueGroups(groups.Select(g => (g.Value ?? "", g.Cnt, g.FirstPos)));
    }

    /// <summary>
    /// Значения поля с количеством в порядке таблицы (по первой позиции). Записи без строки
    /// значения учитываются в пустом значении "" — в UI оно показывается как «(Пусто)» и идет в конец.
    /// </summary>
    public List<(string Value, int Count)> GetFieldValueCounts(int collectionId, int fieldId) =>
        FieldValueCountsWhole(collectionId, fieldId);

    // Сколько первых записей таблицы просматривается ради порядка значений (FieldValueCountsWhole).
    private const int FirstAppearanceWalk = 20_000;

    /// <summary>
    /// Значения поля по всей коллекции — без обхода всех записей: счетчики по индексу
    /// (FieldId, Value) (значения поля есть только у записей его коллекции), порядок таблицы — по
    /// первому появлению в первых <see cref="FirstAppearanceWalk"/> записях. Прежний JOIN каждой
    /// строки значения с записью ради MIN(Position) на 10 млн записей стоил 26 с даже у колонки из
    /// четырех значений. Не встретившиеся в начале таблицы значения идут следом по алфавиту; у
    /// коллекции не больше <see cref="FirstAppearanceWalk"/> записей начало — вся таблица, и порядок
    /// тот же, что по MIN(Position).
    /// </summary>
    private List<(string Value, int Count)> FieldValueCountsWhole(int collectionId, int fieldId)
    {
        if (IsFileField(fieldId)) return FileColumnCounts(fieldId, Records(collectionId));
        if (RelatedComputedField(fieldId) is not null)
            return RelatedComputedCounts(fieldId, Records(collectionId));
        var counts = db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == fieldId && v.Value != "")
                       .GroupBy(v => v.Value).Select(g => new { g.Key, Cnt = g.Count() }).ToList()
                       .ToDictionary(x => x.Key, x => x.Cnt);
        var order = new List<string>(counts.Count);
        var seen  = new HashSet<string>();
        var head  = (from r in Records(collectionId)
                     join fv in db.DbFieldValues.AsNoTracking() on r.Id equals fv.RecordId
                     where fv.FieldId == fieldId && fv.Value != ""
                     orderby r.Position, r.Id
                     select fv.Value).Take(FirstAppearanceWalk);
        foreach (var v in head)
        {
            if (seen.Add(v)) order.Add(v);
            if (order.Count == counts.Count) break;
        }
        order.AddRange(counts.Keys.Where(k => !seen.Contains(k)).Order(StringComparer.Ordinal));

        var result  = order.Select(v => (v, counts[v])).ToList();
        var emptied = Records(collectionId).Count() - counts.Values.Sum();
        if (emptied > 0) result.Add(("", emptied));
        return result;
    }

    private List<(string Value, int Count)> FieldValueCounts(int fieldId, IQueryable<DbRecordEntity> scope)
    {
        if (IsFileField(fieldId)) return FileColumnCounts(fieldId, scope);
        if (RelatedComputedField(fieldId) is not null) return RelatedComputedCounts(fieldId, scope);
        // JOIN со срезом записей: группировка идет по индексу (FieldId, Value), срез отсекает
        // записи чужой коллекции и не прошедшие фильтры других колонок.
        var groups = (from fv in db.DbFieldValues.AsNoTracking()
                      join r in scope on fv.RecordId equals r.Id
                      where fv.FieldId == fieldId
                      group r by fv.Value into g
                      select new { Value = g.Key, Cnt = g.Count(), FirstPos = g.Min(r => r.Position) })
                     .ToList();

        // Непустые — по первой позиции (порядок таблицы).
        var result = groups.Where(g => g.Value != "")
            .OrderBy(g => g.FirstPos)
            .Select(g => (Value: g.Value ?? "", Count: g.Cnt))
            .ToList();

        // «(Пусто)» = записи среза без строки значения + с пустым значением — последним.
        var total   = scope.Count();
        var emptied = total - groups.Where(g => g.Value != "").Sum(g => g.Cnt);
        if (emptied > 0) result.Add(("", emptied));
        return result;
    }

    // Названия связанных записей по каждому источнику коллекции, В ПОРЯДКЕ ребер (Position, Id), с
    // резолвом МЕЖБАЗОВЫХ целей из их баз — как в ячейке (RelationSelectBox). Единая основа для
    // сортировки/фильтра/счетчиков столбца-связи: раньше все три джойнили db_record_links с ЛОКАЛЬНОЙ
    // db_records по ToRecordId → межбазовые цели не находились (падали в «(нет связей)») или при
    // коллизии id клеили чужой заголовок. resolveForeign: (dbId, ids) → Id→Title целевой базы (null —
    // база недоступна); по умолчанию из реестра (RelationDbResolver, только UI-поток). Фон (счетчики)
    // передает свой резолвер поверх свежих изолированных подключений (пути сняты заранее на UI).
    private Dictionary<int, List<string>> RelationTitlesBySource(
        int collectionId, int fieldId,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign = null)
    {
        Demand(PermAction.ViewCollection, collectionId);
        var config = RelationConfig.FromConfig(db.DbFields.AsNoTracking()
            .Where(f => f.Id == fieldId).Select(f => f.ConfigJson).FirstOrDefault());
        var propertyKey = config?.DisplayField ?? RelationConfig.TitleField;
        var customResolver = resolveForeign;
        resolveForeign = (dbId, ids) =>
        {
            var mismatch = propertyKey != RelationConfig.TitleField && config is not null
                && !IsExpectedRelationTargetDatabase(dbId, config.TargetDbId);
            var key = mismatch ? RelationConfig.TitleField : propertyKey;
            var values = customResolver?.Target is ForeignPropertyReader reader
                ? reader.ResolveProperty(dbId, ids, key)
                : customResolver is not null ? customResolver(dbId, ids)
                : RelationDbResolver.RepoFor(dbId)?.GetRecordPropertyValues(ids, key);
            return mismatch && values is not null
                ? values.ToDictionary(p => p.Key, p => p.Value == RelationProperty.NoAccess
                    ? p.Value : RelationProperty.MissingField)
                : values;
        };

        // Все ребра поля от записей этой коллекции, в порядке ребра; TargetDbId различает базу-цель.
        var edges = (from l in db.DbRecordLinks.AsNoTracking()
                     where l.FieldId == fieldId
                     join src in db.DbRecords.AsNoTracking() on l.FromRecordId equals src.Id
                     where src.CollectionId == collectionId
                     orderby l.Position, l.Id
                     select new { l.FromRecordId, l.ToRecordId, l.TargetDbId }).ToList();
        if (edges.Count == 0) return new();

        // Внутрибазовые цели — JOIN (без гигантского IN на тысячах id): ToRecordId → Title.
        var localMismatch = propertyKey != RelationConfig.TitleField && config is not null
            && !IsExpectedRelationTargetDatabase(0, config.TargetDbId);
        var sameDb = GetRecordPropertyValues(edges.Where(e => e.TargetDbId == 0)
            .Select(e => e.ToRecordId).Distinct().ToArray(), localMismatch ? RelationConfig.TitleField : propertyKey);
        if (localMismatch)
            sameDb = sameDb.ToDictionary(p => p.Key, p => p.Value == RelationProperty.NoAccess
                ? p.Value : RelationProperty.MissingField);

        // Межбазовые цели — резолвим из их баз пакетно по (dbId → набор id); id межбазовых целей мало.
        var crossDb = new Dictionary<(int To, int Db), string?>();
        foreach (var grp in edges.Where(e => e.TargetDbId != 0).GroupBy(e => e.TargetDbId))
        {
            var ids = grp.Select(e => e.ToRecordId).Distinct().ToList();
            var map = resolveForeign(grp.Key, ids);
            foreach (var id in ids)
                crossDb[(id, grp.Key)] =
                    map is null                        ? UnavailableLabel(grp.Key)
                  : map.TryGetValue(id, out var t)     ? t
                  :                                      null;   // записи нет → ребро пропускаем («связи нет»)
        }

        var bySource = new Dictionary<int, List<string>>();
        foreach (var e in edges)   // ребра уже в порядке (Position, Id)
        {
            var title = e.TargetDbId == 0
                ? (sameDb.TryGetValue(e.ToRecordId, out var t) ? t : null)     // нет строки → цель удалена
                : crossDb.GetValueOrDefault((e.ToRecordId, e.TargetDbId));
            if (title is null) continue;
            if (!bySource.TryGetValue(e.FromRecordId, out var list))
                bySource[e.FromRecordId] = list = new();
            list.Add(title);
        }
        return bySource;
    }

    /// <summary>
    /// Значения столбца-связи с количеством для панели фильтра (Excel-стиль): каждый пункт —
    /// НАЗВАНИЕ связанной записи (Title цели, в т.ч. из ДРУГОЙ базы), счетчик — сколько записей-
    /// источников коллекции ссылаются хотя бы на одну запись с этим названием. «(нет связей)» = "" —
    /// источники без связей (либо связанные только с безымянными/удаленными целями), последним. Порядок
    /// непустых — по первой позиции источника (порядок таблицы). resolveForeign — см. RelationTitlesBySource.
    /// </summary>
    public List<(string Value, int Count)> GetRelationValueCounts(
        int collectionId, int fieldId,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign = null) =>
        RelationValueCounts(collectionId, fieldId, resolveForeign, Records(collectionId));

    private List<(string Value, int Count)> RelationValueCounts(
        int collectionId, int fieldId,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign,
        IQueryable<DbRecordEntity> scope)
    {
        var bySource = RelationTitlesBySource(collectionId, fieldId, resolveForeign);
        // Позиции источников СРЕЗА: он же задает, какие источники вообще считаются
        // (при каскаде — только прошедшие фильтры других колонок).
        var pos = scope.Select(r => new { r.Id, r.Position }).ToList()
                       .ToDictionary(x => x.Id, x => x.Position);

        // value → (уникальные источники, минимальная позиция); мульти-связь на одно имя не дублирует счетчик.
        var byValue = new Dictionary<string, (HashSet<int> Sources, int MinPos)>();
        foreach (var (src, titles) in bySource)
        {
            if (!pos.TryGetValue(src, out var p)) continue;   // источник вне среза
            foreach (var title in titles.Where(t => t.Length > 0).Distinct())
            {
                if (byValue.TryGetValue(title, out var agg))
                {
                    agg.Sources.Add(src);
                    byValue[title] = (agg.Sources, Math.Min(agg.MinPos, p));
                }
                else byValue[title] = (new HashSet<int> { src }, p);
            }
        }

        var result = byValue.OrderBy(kv => kv.Value.MinPos)
            .Select(kv => (Value: kv.Key, Count: kv.Value.Sources.Count))
            .ToList();

        var totalSrc  = pos.Count;
        var withNamed = bySource.Count(kv => pos.ContainsKey(kv.Key) && kv.Value.Any(t => t.Length > 0));   // источники среза с ИМЕНОВАННОЙ целью
        if (totalSrc - withNamed > 0) result.Add(("", totalSrc - withNamed));
        return result;
    }

    private bool IsRelationField(int fieldId) =>
        db.DbFields.AsNoTracking().Any(f => f.Id == fieldId && f.FieldType == "relation");

    /// <summary>
    /// Значения колонки с количеством для окна фильтра — Excel-каскад: считаются только по
    /// записям, прошедшим фильтры ОСТАЛЬНЫХ колонок (свой фильтр колонки, если он есть в
    /// <paramref name="filters"/>, не учитывается — иначе снятое значение нельзя было бы вернуть
    /// в отбор), а при <paramref name="excludeHidden"/> — без скрытых в «Отображении записей».
    /// Без фильтров — по всей колонке. Порядок — как в таблице, «(Пусто)» последним.
    /// </summary>
    public List<(string Value, int Count)> GetColumnCounts(int collectionId, string key,
        IReadOnlyDictionary<string, HashSet<string>>? filters = null, bool excludeHidden = false,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign = null)
    {
        using var foreignScope = (resolveForeign?.Target as ForeignPropertyReader)?.OpenScope();
        var others = OtherFilters(filters, key);
        var scope  = FilteredRecords(collectionId, others, excludeHidden, resolveForeign);
        return key == TitleKey ? TitleCounts(scope)
            : int.TryParse(key, out var fid)
                ? (IsRelationField(fid) ? RelationValueCounts(collectionId, fid, resolveForeign, scope)
                   : IsFileField(fid) ? FileColumnCounts(fid, scope)
                   : IsCheckboxField(fid) ? CheckboxCounts(fid, scope, whole: others.Count == 0 && !excludeHidden)
                   : others.Count == 0 && !excludeHidden ? FieldValueCountsWhole(collectionId, fid)
                   : FieldValueCounts(fid, scope))
                : [];
    }

    private bool IsCheckboxField(int fieldId) =>
        db.DbFields.AsNoTracking().Any(f => f.Id == fieldId && f.FieldType == "checkbox");

    // Чекбокс в окне фильтра — две строки: «отмечено» (CheckboxValue.Checked) и «не отмечено» ("",
    // туда же «0» снятой галочки и любой другой текст). Значения как есть давали несколько строк
    // «—» рядом с «(Пусто)», и отобрать все неотмеченные одной галочкой было нельзя.
    // whole — срез равен всей коллекции: отмеченные считаются по индексу (FieldId, Value) без обхода
    // записей (значения поля принадлежат только записям его коллекции), как у FieldValueCountsWhole.
    private List<(string Value, int Count)> CheckboxCounts(int fieldId, IQueryable<DbRecordEntity> scope, bool whole)
    {
        var spellings = CheckboxValue.CheckedSpellings.ToList();
        var on  = whole
            ? db.DbFieldValues.AsNoTracking().Count(fv => fv.FieldId == fieldId && spellings.Contains(fv.Value))
            : scope.Count(r => db.DbFieldValues.Where(fv => fv.FieldId == fieldId && spellings.Contains(fv.Value))
                                               .Select(fv => fv.RecordId).Contains(r.Id));
        var off = scope.Count() - on;
        var result = new List<(string Value, int Count)>(2);
        if (on  > 0) result.Add((CheckboxValue.Checked, on));
        if (off > 0) result.Add(("", off));
        return result;
    }

    /// <summary>Сколько значений показывает окно фильтра колонки, у которой разных значений больше:
    /// первые по алфавиту, остальные — поиском по базе (<see cref="GetColumnValueListAsync"/>).</summary>
    public const int FilterValueCap = 1000;

    /// <summary>Значения колонки для окна фильтра. <paramref name="Capped"/> — разных значений больше
    /// <see cref="FilterValueCap"/>: в <paramref name="Values"/> первые по алфавиту или найденные поиском
    /// (не больше <see cref="FilterValueCap"/>), «(Пусто)» последним (без поиска);
    /// <paramref name="DistinctTotal"/> — сколько разных значений всего (без поиска; −1 — не считалось),
    /// <paramref name="More"/> — найдено больше показанного. <paramref name="Snapshot"/> — значения
    /// колонки данных из связанных записей, посчитанные для окна: поиск и итоги того же окна идут по
    /// нему, не пересчитывая столбец.</summary>
    public sealed record ColumnValueList(List<(string Value, int Count)> Values, bool Capped, long DistinctTotal, bool More,
        ColumnValueSnapshot? Snapshot = null);

    /// <summary>Значения колонки данных из связанных записей с числом записей — на время окна
    /// фильтра. Годен только для той же базы, коллекции, колонки и тех же фильтров остальных
    /// колонок; живет не дольше окна, поэтому права и данные не устаревают дольше открытого окна.</summary>
    public sealed class ColumnValueSnapshot
    {
        internal ColumnValueSnapshot(string scope, List<(string Value, int Count)> counts)
        {
            Scope = scope;
            Counts = counts;
        }

        internal string Scope { get; }
        internal List<(string Value, int Count)> Counts { get; }
    }

    // Чем определен срез окна фильтра: база, коллекция, колонка, фильтры остальных колонок, скрытые.
    private string SnapshotScope(int collectionId, string key, Dictionary<string, HashSet<string>> others, bool excludeHidden) =>
        string.Join("\u0001", new[] { db.DbPath, collectionId.ToString(), key, excludeHidden ? "1" : "0" }
            .Concat(others.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + string.Join("\u0002", kv.Value.Order(StringComparer.Ordinal)))));

    /// <summary>
    /// Значения колонки для окна фильтра — как <see cref="GetColumnCountsAsync"/> (Excel-каскад,
    /// фон, отдельное подключение), но на колонке с тысячами разных значений не все: на 10 млн
    /// разных значений полный список строился 105 с и занимал гигабайты. Такой колонке окно
    /// показывает первые <see cref="FilterValueCap"/> по алфавиту (<see cref="ColumnValueList.Capped"/>),
    /// остальное ищется <paramref name="search"/> по базе. Связи и даты — всегда полным списком
    /// (у дат — дерево «год → месяц → день» по всем значениям).
    /// </summary>
    public Task<ColumnValueList> GetColumnValueListAsync(int collectionId, string key,
        IReadOnlyDictionary<string, HashSet<string>>? filters = null, bool excludeHidden = false,
        string? search = null, CancellationToken ct = default, ColumnValueSnapshot? reuse = null)
    {
        var others     = OtherFilters(filters, key);
        var crossPaths = CrossDbPathsForKeys(collectionId, others.Keys.Append(key));
        var run        = RelationColumnRun.Capture(ct);
        return Task.Run(() =>
        {
            using var _ = run;
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx) { Permissions = Permissions, ComputedRun = run };
            return repo.ColumnValues(collectionId, key, others, excludeHidden, ForeignResolver(crossPaths), search, ct, reuse);
        }, ct);
    }

    private ColumnValueList ColumnValues(int collectionId, string key, Dictionary<string, HashSet<string>> others,
        bool excludeHidden, Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign,
        string? search, CancellationToken ct, ColumnValueSnapshot? reuse = null)
    {
        int? fid = int.TryParse(key, out var parsed) ? parsed : null;
        // Отбор по данным из связанных записей в других колонках считается сразу — через снятые на
        // UI-потоке пути баз (область чтения), а не через общие подключения окна.
        IQueryable<DbRecordEntity> Scope()
        {
            using var foreignScope = (resolveForeign?.Target as ForeignPropertyReader)?.OpenScope();
            return FilteredRecords(collectionId, others, excludeHidden, resolveForeign);
        }
        if (fid is int fileId && IsFileField(fileId))
            return FileColumnValues(fileId, Scope(), search, ct);
        if (fid is int computedId && RelatedComputedField(computedId) is { } computedField)
        {
            var snapshot = ComputedSnapshot(collectionId, key, computedId, others, excludeHidden, resolveForeign, ct, reuse);
            // Поиск — по показанному тексту («11,7» находит итог 11.75), первые значения — порядком
            // колонки (итог — как число).
            var counts = snapshot.Counts;
            var computedPattern = search?.Trim();
            if (!string.IsNullOrEmpty(computedPattern))
                counts = counts.Where(v => FieldTypeInfo.FormatComputedDisplay(v.Value, computedField.FieldType)
                    .Contains(computedPattern, StringComparison.OrdinalIgnoreCase)).ToList();
            var total = counts.Count(v => v.Value.Length > 0);
            var capped = total > FilterValueCap;
            var computedPage = RelationComputedSort.Ascending(counts.Where(v => v.Value.Length > 0), v => v.Value,
                computedField.FieldType).Take(FilterValueCap).ToList();
            // Ограниченный список досчитывает «(Пусто)» итогами окна сам: здесь его нет, иначе
            // строка встала бы дважды.
            if (!capped) computedPage.AddRange(counts.Where(v => v.Value.Length == 0));
            ct.ThrowIfCancellationRequested();
            return new(computedPage, capped, total, capped, snapshot);
        }
        var cappable = key == TitleKey || (fid is { } id && db.DbFields.AsNoTracking()
                           .Any(f => f.Id == id && f.FieldType != "relation" && f.FieldType != "date"
                                     && f.FieldType != "checkbox"));
        // Больше ли разных непустых значений, чем показывает окно: DISTINCT с LIMIT идет по индексу
        // (FieldId, Value) и останавливается на первом лишнем.
        var many = cappable && (fid is { } f0
            ? db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == f0 && v.Value != "")
                .Select(v => v.Value).Distinct().Take(FilterValueCap + 1).Count() > FilterValueCap
            : Records(collectionId).Where(r => r.Title != "")
                .Select(r => r.Title).Distinct().Take(FilterValueCap + 1).Count() > FilterValueCap);
        if (!many)
        {
            var all = GetColumnCounts(collectionId, key, others, excludeHidden, resolveForeign);
            return new(all, false, all.Count, false);
        }
        ct.ThrowIfCancellationRequested();

        var narrowed = others.Count > 0 || excludeHidden;
        var scope    = Scope();
        var query    = FilterSelection.IsEmptyQuery(search) ? null : search!.Trim();
        var pattern  = query is null ? null : "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        // Поиск без учета регистра — функцией на каждой строке (SqlText): отмена прерывает запрос
        // из нее же, иначе прежний поиск по 10 млн значений доживал бы свои секунды после каждой
        // новой буквы. Функция регистрируется на открытом соединении этого фонового контекста.
        if (pattern is not null && !db.IsPostgres && db.Database.GetDbConnection() is SqliteConnection sqlite)
        {
            db.Database.OpenConnection();
            sqlite.CreateFunction(SqlText.ContainsName, (string? value, string? part) =>
            {
                ct.ThrowIfCancellationRequested();
                return value is not null && part is not null && value.Contains(part, StringComparison.OrdinalIgnoreCase);
            }, isDeterministic: true);
        }

        List<(string Value, int Count)> page;
        try
        {
            if (fid is { } fieldId)
            {
                var values = CappedValues(fieldId, scope, narrowed);
                if (pattern is not null)
                    values = db.IsPostgres
                        ? values.Where(v => EF.Functions.ILike(v.Value, pattern, "\\"))
                        : values.Where(v => SqlText.ContainsIgnoreCase(v.Value, query!));
                page = values.GroupBy(v => v.Value).OrderBy(g => g.Key).Take(FilterValueCap + 1)
                             .Select(g => new { g.Key, Count = g.Count() }).ToList()
                             .Select(x => (x.Key, x.Count)).ToList();
            }
            else
            {
                var titles = scope.Where(r => r.Title != "");
                if (pattern is not null)
                    titles = db.IsPostgres
                        ? titles.Where(r => EF.Functions.ILike(r.Title, pattern, "\\"))
                        : titles.Where(r => SqlText.ContainsIgnoreCase(r.Title, query!));
                page = titles.GroupBy(r => r.Title).OrderBy(g => g.Key).Take(FilterValueCap + 1)
                             .Select(g => new { g.Key, Count = g.Count() }).ToList()
                             .Select(x => (x.Key ?? "", x.Count)).ToList();
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        var more = page.Count > FilterValueCap;
        if (more) page.RemoveAt(page.Count - 1);
        return new(page, true, -1, more);
    }

    // Значения колонки данных из связанных записей по срезу окна: из снимка того же окна, если он
    // годен, иначе расчет одной колонки.
    private ColumnValueSnapshot ComputedSnapshot(int collectionId, string key, int fieldId,
        Dictionary<string, HashSet<string>> others, bool excludeHidden,
        Func<int, IReadOnlyList<int>, Dictionary<int, string>?>? resolveForeign, CancellationToken ct, ColumnValueSnapshot? reuse,
        IQueryable<DbRecordEntity>? scope = null)
    {
        var scopeKey = SnapshotScope(collectionId, key, others, excludeHidden);
        if (reuse?.Scope == scopeKey) return reuse;
        using var foreignScope = (resolveForeign?.Target as ForeignPropertyReader)?.OpenScope();
        ct.ThrowIfCancellationRequested();
        scope ??= FilteredRecords(collectionId, others, excludeHidden, resolveForeign);
        return new ColumnValueSnapshot(scopeKey, RelatedComputedCounts(fieldId, scope, ct));
    }

    // Непустые значения поля ограниченного списка — по срезу, если его сузили фильтры других
    // колонок или скрытые записи (иначе значения поля и есть значения записей коллекции).
    private IQueryable<DbFieldValueEntity> CappedValues(int fieldId, IQueryable<DbRecordEntity> scope, bool narrowed)
    {
        var values = db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == fieldId && v.Value != "");
        return narrowed ? values.Where(v => scope.Select(r => r.Id).Contains(v.RecordId)) : values;
    }

    /// <summary>
    /// Итоги ограниченного списка окна фильтра (<see cref="ColumnValueList.Capped"/>): сколько разных
    /// непустых значений у колонки в срезе и сколько записей среза без значения («(Пусто)»). Окно
    /// досчитывает их после показа первых значений: на 10 млн записей это секунды, а список нужен сразу.
    /// </summary>
    public Task<(long Distinct, int Empty)> GetColumnValueTotalsAsync(int collectionId, string key,
        IReadOnlyDictionary<string, HashSet<string>>? filters = null, bool excludeHidden = false,
        CancellationToken ct = default, ColumnValueSnapshot? reuse = null)
    {
        var others     = OtherFilters(filters, key);
        var crossPaths = CrossDbPathsForKeys(collectionId, others.Keys.Append(key));
        var run        = RelationColumnRun.Capture(ct);
        return Task.Run(() =>
        {
            using var _ = run;
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo  = new ConstructorRepository(ctx) { Permissions = Permissions, ComputedRun = run };
            var resolver = ForeignResolver(crossPaths);
            using var foreignScope = (resolver?.Target as ForeignPropertyReader)?.OpenScope();
            var scope = repo.FilteredRecords(collectionId, others, excludeHidden, resolver);
            if (int.TryParse(key, out var fid))
            {
                if (repo.IsFileField(fid)) return repo.FileColumnValueTotals(fid, scope, ct);
                if (repo.RelatedComputedField(fid) is not null)
                {
                    var counts = repo.ComputedSnapshot(collectionId, key, fid, others, excludeHidden, resolver, ct, reuse, scope).Counts;
                    return ((long)counts.Count(v => v.Value.Length > 0), counts.Where(v => v.Value.Length == 0).Sum(v => v.Count));
                }
                var values = repo.CappedValues(fid, scope, others.Count > 0 || excludeHidden);
                var distinct = values.Select(v => v.Value).Distinct().LongCount();
                ct.ThrowIfCancellationRequested();
                return (distinct, scope.Count() - values.Count());
            }
            var titles = scope.Where(r => r.Title != "").Select(r => r.Title).Distinct().LongCount();
            ct.ThrowIfCancellationRequested();
            return (titles, scope.Count(r => r.Title == "" || r.Title == null));
        }, ct);
    }

    // Фильтры остальных колонок — снимок (копия наборов): фон читает его, пока UI может
    // менять живой словарь таблицы.
    // Свой отбор колонки — и по значениям, и по цвету (ColorQuery) — ее список не режет.
    private static Dictionary<string, HashSet<string>> OtherFilters(
        IReadOnlyDictionary<string, HashSet<string>>? filters, string key) =>
        filters is null ? new()
            : filters.Where(kv => ColorQuery.ColumnOf(kv.Key) != key)
                     .ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));

    /// <summary>
    /// Асинхронно (на фоновом потоке, отдельное read-only подключение) считает значения колонки
    /// с количеством — чтобы окно фильтра открывалось мгновенно, а UI не подвисал на подсчете.
    /// WAL допускает одновременное чтение, поэтому фоновое подключение не конфликтует с основным.
    /// filters/excludeHidden — Excel-каскад, см. <see cref="GetColumnCounts"/>.
    /// </summary>
    public Task<List<(string Value, int Count)>> GetColumnCountsAsync(int collectionId, string key,
        IReadOnlyDictionary<string, HashSet<string>>? filters = null, bool excludeHidden = false)
    {
        // Снимок фильтров других колонок берем на вызывающем (UI) потоке — фон живой словарь не трогает.
        var others = OtherFilters(filters, key);

        // Столбец-связь может ссылаться на ДРУГИЕ базы: их пути снимаем здесь, на UI-потоке (реестр/общие
        // репозитории — только отсюда), а фон читает Title целей по этим путям СВЕЖИМИ изолированными
        // подключениями — общий контекст чужой базы не трогаем (см. правило об общем AppDbContext).
        // Связью может быть и считаемая колонка, и любая из фильтрующих (каскад резолвит их цели тем же путем).
        // Для поля-связи crossPaths НЕ null (даже пустой): резолвер отдается всегда, чтобы дефолт
        // RelationTitlesBySource (RelationDbResolver.RepoFor → общий контекст чужой базы) НИКОГДА не
        // выполнялся из фона. Путь не снят на UI → dbId нет в карте → null → «(база недоступна)».
        var crossPaths = CrossDbPathsForKeys(collectionId, others.Keys.Append(key));
        var run        = RelationColumnRun.Capture(CancellationToken.None);

        return Task.Run(() =>
        {
            using var _ = run;
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx) { Permissions = Permissions, ComputedRun = run };
            return repo.GetColumnCounts(collectionId, key, others, excludeHidden, ForeignResolver(crossPaths));
        });
    }

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
        var collectionId = CollectionOfRecord(recordId);
        Demand(PermAction.EditRecord, collectionId);

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
                // Метаданные поля нужны и журналу (имя, тип), и событиям задач (роли) —
                // читаем ОДНИМ запросом, тем же, что был здесь и раньше.
                var f = db.DbFields.AsNoTracking()
                    .Where(x => x.Id == fieldId)
                    .Select(x => new EditedFieldMeta(x.Name, x.FieldType, x.IsTaskExec, x.IsTaskStatus))
                    .FirstOrDefault();

                // «до → после»: имя свойства + компактный дифф значения (старое уже в fv).
                // Побуквенное сохранение панели записи — сливаем в одну строку журнала.
                var key = (Kind: "field", RecordId: recordId, SubId: fieldId);
                var old = BeginEditMerge(key, fv?.Value ?? "");
                if (old != value)
                {
                    var fieldName = f?.Name ?? "";
                    // «Файл»: в журнале показываем имя файла, а не абсолютный путь (как в ячейке
                    // и фильтре). Слияние правок идет по сырым old/value — свертка только для показа.
                    var (detailOld, detailNew) = f?.FieldType == "file"
                        ? (FileValue.DisplayName(old), FileValue.DisplayName(value))
                        : (old, value);
                    EndEditMerge(key, Log("update", "record", rec.Title, CollectionNameOf(rec.CollectionId),
                                          detail: ChangeDetail.Value(rec.Title, fieldName, detailOld, detailNew)),
                                 old);
                }

                // Правили исполнителя или стадию — кладем в очередь уведомление получателю.
                // Старым значением идет ФАКТИЧЕСКОЕ значение ячейки, а не слитое «до» журнала.
                TryWriteTaskEvent(recordId, rec.Title, rec.CollectionId, fieldId, f, fv?.Value ?? "", value);
            }
        }

        // Многопользовательский режим: между нашей проверкой и записью ту же ячейку мог
        // изменить другой пользователь (другой процесс). Политика — «последний побеждает»:
        //  • гонка на ПЕРВОЕ значение (оба увидели «строки нет», оба INSERT) — проигравший
        //    ловит PK-конфликт (SQLITE_CONSTRAINT=19 / PG unique_violation 23505)
        //    и превращает вставку в UPDATE;
        //  • строку/запись успели удалить — UPDATE попадает в 0 строк
        //    (DbUpdateConcurrencyException): значение не воскрешаем, синхронизация вскоре
        //    уберет запись и из нашего UI.
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
                // Идентификаторы в кавычках нижним регистром: SQLite матчит их без учета
                // регистра, PG-модель и так lowercase — одна строка на оба провайдера.
                db.Database.ExecuteSqlRaw(
                    "UPDATE \"db_field_values\" SET \"value\" = {0} WHERE \"recordid\" = {1} AND \"fieldid\" = {2}",
                    value, recordId, fieldId);
                db.SaveChanges();   // дожать строку журнала, не сохраненную упавшим SaveChanges
            }
        }
        else if (value.Length == 0)
        {
            // Очистка ячейки: строку удаляем, а не оставляем с пустым значением — инвариант
            // «нет строки = пусто» (так читают страницы, счетчики фильтра, экспорт), и таблица
            // значений с ее индексом не копят пустые строки. Чужое удаление той же строки
            // (0 затронутых) гасит SaveReconcilingPhantomDeletes — это тот же исход.
            db.DbFieldValues.Remove(fv);
            db.SaveChanges();
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
            // Если запись уже в трекере (ее грузили через GetRecord/Find) — синхронизируем, иначе
            // сырой UPDATE прошел бы мимо кеша и GetRecord вернул бы старое «изменено».
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
        var derived = changed ? ReactToSourceEditForAutoFile(recordId, fieldId) : [];
        if (changed && collectionId is int changedCollection)
        {
            NotifyRelationDataChanged(changedCollection, fieldId, recordId);
            foreach (var (derivedField, _) in derived) NotifyRelationDataChanged(changedCollection, derivedField, recordId);
        }
        return derived;
    }

    // Если правили текст-поле editedFieldId, служащее источником авто-file полей — пересчитать их.
    private List<(int FieldId, string Value)> ReactToSourceEditForAutoFile(int recordId, int editedFieldId) =>
        ReactToSourceEdit(recordId, rule => rule.SourceFieldId == editedFieldId);

    // Пересчитать авто-file поля записи, чей источник подходит под <paramref name="isSource"/>
    // (правка текст-столбца-источника, «Названия» или «Описания»). Возвращает список измененных
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
            var matched = ResolveOrQueueAutoFile(ff.Id, recordId, rule);
            if (matched is null) continue;
            SetAutoFileValue(ff.Id, recordId, matched);
            changes.Add((ff.Id, matched));
        }
        return changes;
    }

    /// <summary>
    /// Включает/выключает показ поля-даты в календаре (флаг <c>IsCalStart</c>). Несколько
    /// полей-дат коллекции могут быть включены одновременно — каждое дает свои события.
    /// Видимость — ЧИСТЫЙ ФИЛЬТР ЧТЕНИЯ (CalendarFields.HiddenClause во всех читателях calendar_index),
    /// БЕЗ пересборки проекции: прежний RebuildCalendarIndexForField удалял/заливал строки поля
    /// синхронно на UI-потоке — на 10 млн записей клик чекбокса вешал приложение на минуты.
    /// Проекция всегда полная (SyncCalendarIndexValue/FillCalendarIndex пишут ВСЕ date-поля).
    /// Возвращает true, если при ВКЛЮЧЕНИИ проекция поля пуста при непустых значениях —
    /// наследие старого поведения (строки удалила прошлая версия): вызывающий должен один раз
    /// залить ее фоном (<see cref="FillCalendarIndexForFieldAsync"/> с окном прогресса).
    /// </summary>
    public bool SetDateFieldVisible(int fieldId, bool visible, bool adoptByName = true)
    {
        Demand(PermAction.EditField, CollectionOfField(fieldId));
        var field = db.DbFields.Find(fieldId);
        if (field is null || field.FieldType != "date" || field.IsCalStart == visible) return false;
        field.IsCalStart = visible;
        db.SaveChanges();

        if (!visible) return false;

        // Включенная дата = запись показывается в календаре, а окно события правит еще и
        // описание — значит роль «Описание» коллекции нужна так же, как поле-дата. Одноименный
        // столбец подтягивается или обходится стороной по ответу пользователя (см. PlanRoles).
        EnsureRoles(field.CollectionId, CalendarRoles, adoptByName, excludeFieldId: fieldId);
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
        // Нет своей записи у коллекции → все три значения берем из настроек раздела
        // «Конструктор базы данных». Свои сохраненные (переключали вручную в меню коллекции)
        // имеют приоритет: настройка задает, с чего коллекция начинает, а не чем живет.
        return s is null
            ? (Utils.ConstructorSettings.ColResize, Utils.ConstructorSettings.RowResize,
               Utils.AppSettings.LoadConstructorView())
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

    // Строка column_meta живет, пока задан хоть один аспект (порядок/видимость/подпись/
    // оформление/ширина); сброс последнего удаляет ее, чтобы «отсутствие строки = дефолт»
    // оставалось верным и таблица не копила пустых строк.
    // ВАЖНО: Width и оформление (HasTableStyle — цвет и расположение текста) обязаны быть в этом
    // списке — иначе SaveColumnDisplay и WriteColumnOrder, обнуляющие свои аспекты у всех строк
    // коллекции, снесли бы строку с одной лишь сохраненной шириной или расположением, и они
    // молча терялись бы после окна «Отображение».
    private void RemoveColumnMetaIfEmpty(ColumnMetaEntity m)
    {
        if (m.Position is null && m.Visible is null && m.Level is null && m.DisplayName is null
            && !HasTableStyle(m) && m.Width is null)
            db.ColumnMeta.Remove(m);
    }

    // Подписи системных колонок «Название»/«Описание» (только заданные пользователем).
    // Ключ — TableRowVm.TitleKey; значение — отображаемое имя.
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

    // Сохраненный порядок (key -> position); пусто, если в коллекции еще не двигали.
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
    // актуальный порядок колонок ДО вставки (newKey там в конце, как создает AddField).
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

    // ── Ручные ширины колонок (аспект Width в column_meta) ─────────────────────
    // Ключ строковый — тот же, что у порядка/видимости/подписи/цвета: id поля строкой либо
    // TableRowVm.TitleKey для системной колонки «Название». Числовой ключ прежней таблицы
    // ui_col_widths системную колонку покрыть не мог, из-за чего она оставалась «резиновой»
    // и ломала ручной ресайз (см. GetColWidths ниже — легаси).

    /// <summary>Ключ колонки → ручная ширина в DIP. Только заданные колонки: отсутствие
    /// ключа = ширина не задана (колонка получает стандартную).</summary>
    public Dictionary<string, double> GetColumnWidths(int collectionId) =>
        db.ColumnMeta
          .Where(m => m.CollectionId == collectionId && m.Width != null)
          .ToDictionary(m => m.Key, m => (double)m.Width!.Value);

    /// <summary>Точечный upsert ширин (как SetColumnStyle): строки column_meta не сносим —
    /// в них живут порядок, видимость, подпись и цвет. Ключей нет в словаре → Width = null.
    /// Пишем только реально изменившиеся значения: вызывается на каждое отпускание мыши.</summary>
    public void SaveColumnWidths(int collectionId, IReadOnlyDictionary<string, double> widths)
    {
        var rows = db.ColumnMeta.Where(m => m.CollectionId == collectionId).ToList();
        var seen = new HashSet<string>();
        var dirty = false;

        foreach (var row in rows)
        {
            seen.Add(row.Key);
            var want = widths.TryGetValue(row.Key, out var w) && w > 0
                ? (int?)Math.Round(w)
                : null;
            if (row.Width == want) continue;

            row.Width = want;
            dirty = true;
            RemoveColumnMetaIfEmpty(row);
        }

        foreach (var (key, w) in widths)
        {
            if (w <= 0 || seen.Contains(key)) continue;
            db.ColumnMeta.Add(new ColumnMetaEntity
            {
                CollectionId = collectionId, Key = key, Width = (int)Math.Round(w),
            });
            dirty = true;
        }

        if (dirty) db.SaveChanges();
    }

    /// <summary>Сбросить все ручные ширины коллекции («Стандартная ширина столбцов»).
    /// Прочие аспекты строк column_meta сохраняются.</summary>
    public void ClearColumnWidths(int collectionId)
    {
        var rows = db.ColumnMeta
            .Where(m => m.CollectionId == collectionId && m.Width != null)
            .ToList();
        if (rows.Count == 0) return;

        foreach (var row in rows)
        {
            row.Width = null;
            RemoveColumnMetaIfEmpty(row);
        }
        db.SaveChanges();
    }

    // ЛЕГАСИ (таблица ui_col_widths): из UI не вызывается — ширины ведет column_meta.Width.
    // Оставлено, чтобы старые базы читались без ошибок.
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

    // key → (Visible, Level). Только сохраненные строки; отсутствие ключа = (видима, 1).
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

    // recordId → (Visible, Level). Только сохраненные строки; отсутствие ключа = (видима, 1).
    public Dictionary<int, (bool Visible, int Level)> GetRecordDisplay(int collectionId) =>
        db.RecordDisplay
          .Where(d => d.CollectionId == collectionId)
          .ToDictionary(d => d.RecordId, d => (d.Visible, d.Level));

    // Переписываем АСПЕКТ «видимость/уровень» у всех записей коллекции. Дефолт (видима,
    // уровень 1) не храним — так таблица не разрастается, а новые записи по умолчанию видимы.
    //
    // Раньше метод сносил все строки коллекции и писал заново. С появлением аспектов Bg/Fg
    // так нельзя: окно «Отображение записей» ничего не знает о цвете и стерло бы его. Поэтому
    // строки обновляются на месте, а удаляются только те, у которых после сброса не осталось
    // ни одного заданного аспекта (тот же контракт, что RemoveColumnMetaIfEmpty у колонок).
    public void SaveRecordDisplay(int collectionId, IEnumerable<(int RecordId, bool Visible, int Level)> entries)
    {
        var byRecord = db.RecordDisplay.Where(d => d.CollectionId == collectionId)
                                       .ToDictionary(d => d.RecordId);
        var touched  = new HashSet<int>();

        foreach (var (recordId, visible, level) in entries)
        {
            touched.Add(recordId);
            var lvl = Math.Max(1, level);
            if (byRecord.TryGetValue(recordId, out var row))
            {
                row.Visible = visible;
                row.Level   = lvl;
            }
            else if (!visible || lvl > 1)   // дефолт для новой строки не храним
            {
                db.RecordDisplay.Add(new RecordDisplayEntity
                {
                    CollectionId = collectionId, RecordId = recordId, Visible = visible, Level = lvl,
                });
            }
        }

        // Записи, которых нет в списке, возвращаются к дефолту видимости — но их цвет остается.
        foreach (var (recordId, row) in byRecord)
        {
            if (!touched.Contains(recordId)) { row.Visible = true; row.Level = 1; }
            RemoveRecordDisplayIfEmpty(row);
        }

        db.SaveChanges();
    }

    // Строка record_display живет, пока задан хоть один аспект: скрытость, уровень > 1,
    // свое оформление (цвет, расположение текста). Иначе «отсутствие строки = дефолт» перестало
    // бы быть верным.
    private void RemoveRecordDisplayIfEmpty(RecordDisplayEntity d)
    {
        if (d.Visible && d.Level <= 1 && !HasTableStyle(d))
            db.RecordDisplay.Remove(d);
    }

    // ── Оформление таблицы: цвет и расположение текста ─────────────────────────
    //
    // Три уровня, все — настройки ЭТОЙ таблицы (см. RowStyle.Resolve): record_display на
    // строку, cell_style на ячейку, column_meta на колонку; у каждого уровня одни и те же
    // аспекты Bg/Fg/HAlign/VAlign (ITableStyleRow). В журнал не пишутся, как ширины колонок и
    // высоты строк, и за пределами таблицы не видны.
    //
    // Сквозного цвета записи (db_records.Color) здесь БОЛЬШЕ НЕТ: цвет строки в таблице
    // читается как оформление таблицы, а не как свойство записи, и в других разделах
    // всплывал полосками, которых там не ждут. Уровень убран, см. DbRecordEntity.Color.
    // «Без цвета» и «По умолчанию» = null: строка-дельта удаляется, если аспектов не осталось.
    // Несколько аспектов за раз («По умолчанию» снимает оба расположения) пишутся одним
    // сохранением — перегрузки со списком изменений.
    //
    // Что выставлено последним, то и видно (RowStyle.Resolve). Строке и колонке выставление
    // ставит отметку (ITableStyleStamps) и снимает этот аспект у их ячеек — одной командой
    // базе, по всем записям, а не по загруженным. «По умолчанию» строке или колонке тоже
    // ставит отметку (значения нет, отметка есть): оно перебивает выставленное раньше другим
    // уровнем. Строке такая отметка нужна, только если у какой-то колонки аспект задан, —
    // иначе перебивать нечего, и строка-дельта просто удаляется.

    /// <summary>Один аспект оформления строк этой коллекции; null — сбросить аспект.</summary>
    public StyleStamps SetRowStyle(int collectionId, IReadOnlyList<int> recordIds, StyleAspect aspect, string? value) =>
        SetRowStyle(collectionId, recordIds, [(aspect, value)]);

    /// <summary>Несколько аспектов оформления строк одним сохранением. Возвращает отметки так, как
    /// они записаны (у сброса без отметки — 0): сетка кладет их в свой словарь, не перечитывая базу.</summary>
    public StyleStamps SetRowStyle(int collectionId, IReadOnlyList<int> recordIds,
                                   IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        if (recordIds.Count == 0 || changes.Count == 0) return default;
        Demand(PermAction.EditRecord, collectionId);
        var stored = StoredChanges(changes);

        // Сброс отмечается, только если аспект задает хоть одна колонка — иначе перебивать нечего.
        var columnAspects = ColumnStyledAspects(collectionId);
        var applied       = Stamps(stored, StyleStamps.Next(), columnAspects.Contains);

        using var tx = db.Database.BeginTransaction();
        var existing = db.RecordDisplay
            .Where(d => d.CollectionId == collectionId && recordIds.Contains(d.RecordId))
            .ToDictionary(d => d.RecordId);

        foreach (var recordId in recordIds)
        {
            if (!existing.TryGetValue(recordId, out var row))
            {
                row = new RecordDisplayEntity { CollectionId = collectionId, RecordId = recordId };
                db.RecordDisplay.Add(row);
            }
            SetTableStyle(row, stored, applied);
            RemoveRecordDisplayIfEmpty(row);
        }
        db.SaveChanges();

        ClearCellAspects(db.CellStyles.Where(c => c.CollectionId == collectionId && recordIds.Contains(c.RecordId)),
                         stored.Select(c => c.Aspect));
        tx.Commit();
        return applied;
    }

    // Аспекты, заданные хоть одной колонкой коллекции (column_meta мала — читается целиком).
    private HashSet<StyleAspect> ColumnStyledAspects(int collectionId)
    {
        var aspects = new HashSet<StyleAspect>();
        foreach (var m in db.ColumnMeta.AsNoTracking()
                     .Where(m => m.CollectionId == collectionId
                                 && (m.Bg != null || m.Fg != null || m.HAlign != null || m.VAlign != null))
                     .Select(m => new { m.Bg, m.Fg, m.HAlign, m.VAlign }))
        {
            if (m.Bg     is not null) aspects.Add(StyleAspect.Background);
            if (m.Fg     is not null) aspects.Add(StyleAspect.Text);
            if (m.HAlign is not null) aspects.Add(StyleAspect.HAlign);
            if (m.VAlign is not null) aspects.Add(StyleAspect.VAlign);
        }
        return aspects;
    }

    // Задан ли аспект своим значением хоть у одной строки коллекции.
    private bool RowsStyle(int collectionId, StyleAspect aspect)
    {
        var rows = db.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == collectionId);
        return aspect switch
        {
            StyleAspect.Background => rows.Any(d => d.Bg != null),
            StyleAspect.Text       => rows.Any(d => d.Fg != null),
            StyleAspect.HAlign     => rows.Any(d => d.HAlign != null),
            StyleAspect.VAlign     => rows.Any(d => d.VAlign != null),
            _                      => false,
        };
    }

    // Смена строки или колонки — последнее слово для ее ячеек: свой этот аспект у них снимается
    // (RowStyle.Resolve считает ячейку всегда последней), опустевшие строки cell_style удаляются.
    private static void ClearCellAspects(IQueryable<CellStyleEntity> cells, IEnumerable<StyleAspect> aspects)
    {
        foreach (var aspect in aspects.Distinct())
        {
            switch (aspect)
            {
                case StyleAspect.Background:
                    cells.Where(c => c.Bg != null).ExecuteUpdate(s => s.SetProperty(c => c.Bg, (string?)null));
                    break;
                case StyleAspect.Text:
                    cells.Where(c => c.Fg != null).ExecuteUpdate(s => s.SetProperty(c => c.Fg, (string?)null));
                    break;
                case StyleAspect.HAlign:
                    cells.Where(c => c.HAlign != null).ExecuteUpdate(s => s.SetProperty(c => c.HAlign, (string?)null));
                    break;
                case StyleAspect.VAlign:
                    cells.Where(c => c.VAlign != null).ExecuteUpdate(s => s.SetProperty(c => c.VAlign, (string?)null));
                    break;
            }
        }
        cells.Where(c => c.Bg == null && c.Fg == null && c.HAlign == null && c.VAlign == null).ExecuteDelete();
    }

    /// <summary>
    /// Массовое оформление строк — сырой upsert пакетами вместо EF, пара к
    /// <see cref="BulkAddRecords"/>. <see cref="SetRowStyle(int, IReadOnlyList{int}, StyleAspect, string)"/>
    /// на каждый вызов читает существующие строки оформления и копит сущности в трекере: на
    /// своде трудоемкости раскраска 100 тыс. строк занимала 46 из 54 секунд заливки, ровно по
    /// этой причине. Здесь трекер не участвует, а каждый пакет идет своей транзакцией.
    ///
    /// Снимать оформление этим путем нельзя (пустая строка оформления не удаляется) — для
    /// точечных правок и сброса остается SetRowStyle. Отметку выставления строка получает, как и
    /// там, а ячейки этих строк не трогаются: путь — для только что созданных записей (свод
    /// трудоемкости красит их, а следом свои ячейки), снимать у них нечего.
    /// </summary>
    public void BulkSetRowStyle(int collectionId, IReadOnlyList<int> recordIds, StyleAspect aspect,
                                string value, CancellationToken ct, int batchSize = 20_000)
    {
        if (recordIds.Count == 0 || RowStyle.Stored(aspect, value) is not { } stored) return;
        Demand(PermAction.EditRecord, collectionId);

        var column = RowStyle.Column(aspect);
        var stamp  = RowStyle.StampColumn(aspect);
        var conn   = db.Database.GetDbConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO record_display (CollectionId, RecordId, Visible, Level, {column}, {stamp}) " +
            "VALUES (@cid, @rid, @visible, @level, @val, @at) " +
            $"ON CONFLICT (CollectionId, RecordId) DO UPDATE SET {column} = excluded.{column}, {stamp} = excluded.{stamp}";
        var pCid = AddParam(cmd, "@cid", System.Data.DbType.Int32);
        var pRid = AddParam(cmd, "@rid", System.Data.DbType.Int32);
        // Visible/Level — дефолты сущности. Visible — ЧИСЛО 0/1, а не DbType.Boolean:
        // bool-колонки в схемах ОБОИХ провайдеров хранятся как integer (BoolToZeroOneConverter,
        // см. AppDbContext.OnModelCreating), и boolean-параметр на PostgreSQL падает уже на
        // Prepare: 42804 «столбец visible имеет тип integer, а выражение — boolean».
        var pVis = AddParam(cmd, "@visible", System.Data.DbType.Int32);
        var pLvl = AddParam(cmd, "@level",   System.Data.DbType.Int32);
        var pVal = AddParam(cmd, "@val",     System.Data.DbType.String);
        var pAt  = AddParam(cmd, "@at",      System.Data.DbType.Int64);
        pCid.Value = collectionId;
        pVis.Value = 1;
        pLvl.Value = 1;
        pVal.Value = stored;
        pAt.Value  = StyleStamps.Next();

        RunBatched(cmd, recordIds.Count, batchSize, ct, i => pRid.Value = recordIds[i]);
    }

    /// <summary>Массовое оформление ячеек — то же, что <see cref="BulkSetRowStyle"/>, но по
    /// (запись, колонка): ключ колонки — как в column_meta («__title__» либо id поля строкой).</summary>
    public void BulkSetCellStyle(int collectionId, IReadOnlyList<(int RecordId, string Key)> cells,
                                 StyleAspect aspect, string value, CancellationToken ct,
                                 int batchSize = 20_000)
    {
        if (cells.Count == 0 || RowStyle.Stored(aspect, value) is not { } stored) return;
        Demand(PermAction.EditRecord, collectionId);

        var column = RowStyle.Column(aspect);
        var conn   = db.Database.GetDbConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO cell_style (CollectionId, RecordId, Key, {column}) " +
            "VALUES (@cid, @rid, @key, @val) " +
            $"ON CONFLICT (CollectionId, RecordId, Key) DO UPDATE SET {column} = excluded.{column}";
        var pCid = AddParam(cmd, "@cid", System.Data.DbType.Int32);
        var pRid = AddParam(cmd, "@rid", System.Data.DbType.Int32);
        var pKey = AddParam(cmd, "@key", System.Data.DbType.String);
        var pVal = AddParam(cmd, "@val", System.Data.DbType.String);
        pCid.Value = collectionId;
        pVal.Value = stored;

        RunBatched(cmd, cells.Count, batchSize, ct, i =>
        {
            pRid.Value = cells[i].RecordId;
            pKey.Value = cells[i].Key;
        });
    }

    // Пакетный прогон подготовленной команды: пакет = транзакция (WAL не разрастается на весь
    // объем, отмена срабатывает между пакетами), Prepare один раз на все пакеты.
    // На PostgreSQL строки внутри транзакции уходят пачками DbBatch (см. NetBatcher):
    // поштучный ExecuteNonQuery — это сетевой ход на каждую строку.
    private void RunBatched(DbCommand cmd, int count, int batchSize, CancellationToken ct,
                            Action<int> bind)
    {
        var useBatch = db.Database.GetDbConnection().CanCreateBatch;
        var prepared = false;
        for (var start = 0; start < count; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            using var tx = db.Database.BeginTransaction();
            var dbTx = tx.GetDbTransaction();
            var end  = Math.Min(start + batchSize, count);

            if (useBatch)
            {
                using var net = new NetBatcher(db.Database.GetDbConnection(), dbTx);
                for (var i = start; i < end; i++)
                {
                    bind(i);
                    net.Add(cmd);
                }
                net.Flush();
            }
            else
            {
                cmd.Transaction = dbTx;
                if (!prepared) { cmd.Prepare(); prepared = true; }
                for (var i = start; i < end; i++)
                {
                    bind(i);
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    // ── Пакетная отправка команд (PostgreSQL) ──────────────────────────────────
    //
    // На SQLite поштучная prepared-команда в транзакции — самый быстрый путь: файл рядом,
    // раунд-трипов нет (13 тыс. строк/с у заливки). На PostgreSQL каждый Execute* — синхронный
    // сетевой ход, и свод в 600 тыс. строк превращался в ~4,2 млн ходов — десятки минут одной
    // латентности. DbBatch (NpgsqlBatch) шлет пакет команд одним ходом и выполняет их строго
    // по порядку; Microsoft.Data.Sqlite DbBatch не поддерживает, поэтому признак ветки —
    // CanCreateBatch, и SQLite-путь остается прежним. Одинаковый текст команд пакета
    // авто-препарится сервером (MaxAutoPrepare в PgConnectionInfo).

    /// <summary>Команд в одном сетевом ходе DbBatch — потолок памяти Bind-сообщения,
    /// на порядок меньше лимита протокола (65535 параметров на команду тут не грозит:
    /// параметры считаются на команду, а не на пакет).</summary>
    private const int NetBatchCommands = 2_000;

    /// <summary>Снимок команды в пакет: текст и ТЕКУЩИЕ значения параметров копируются,
    /// поэтому вызывающий продолжает мутировать одни и те же параметры, как при
    /// поштучном prepared-прогоне.</summary>
    private static void AddBatchCommand(DbBatch batch, DbCommand cmd)
    {
        var bc = batch.CreateBatchCommand();
        bc.CommandText = cmd.CommandText;
        foreach (DbParameter p in cmd.Parameters)
        {
            var np = bc.CreateParameter();
            np.ParameterName = p.ParameterName;
            np.DbType        = p.DbType;
            np.Value         = p.Value;
            bc.Parameters.Add(np);
        }
        batch.BatchCommands.Add(bc);
    }

    /// <summary>Копилка команд без результата: шлет пакет, когда набирается
    /// <see cref="NetBatchCommands"/>, остаток — <see cref="Flush"/> (обязателен перед коммитом).</summary>
    private sealed class NetBatcher(DbConnection conn, DbTransaction tx) : IDisposable
    {
        private DbBatch? _batch;

        public void Add(DbCommand cmd)
        {
            if (_batch is null)
            {
                _batch = conn.CreateBatch();
                _batch.Transaction = tx;
            }
            AddBatchCommand(_batch, cmd);
            if (_batch.BatchCommands.Count >= NetBatchCommands) Flush();
        }

        public void Flush()
        {
            if (_batch is null) return;
            _batch.ExecuteNonQuery();
            _batch.Dispose();
            _batch = null;
        }

        public void Dispose() { _batch?.Dispose(); _batch = null; }
    }

    /// <summary>Один аспект оформления ячеек; key — «__title__» либо строковый id поля.</summary>
    public void SetCellStyle(int collectionId, IReadOnlyList<(int RecordId, string Key)> cells,
                             StyleAspect aspect, string? value) =>
        SetCellStyle(collectionId, cells, [(aspect, value)]);

    /// <summary>Несколько аспектов оформления ячеек одним сохранением.</summary>
    public void SetCellStyle(int collectionId, IReadOnlyList<(int RecordId, string Key)> cells,
                             IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        if (cells.Count == 0 || changes.Count == 0) return;
        Demand(PermAction.EditRecord, collectionId);
        var stored    = StoredChanges(changes);
        var recordIds = cells.Select(c => c.RecordId).Distinct().ToList();

        var existing = db.CellStyles
            .Where(c => c.CollectionId == collectionId && recordIds.Contains(c.RecordId))
            .ToList()
            .ToDictionary(c => (c.RecordId, c.Key));

        foreach (var (recordId, key) in cells)
        {
            if (existing.TryGetValue((recordId, key), out var row))
            {
                SetTableStyle(row, stored);
                if (!HasTableStyle(row)) db.CellStyles.Remove(row);
            }
            else if (stored.Any(c => c.Value is not null))
            {
                var added = new CellStyleEntity { CollectionId = collectionId, RecordId = recordId, Key = key };
                SetTableStyle(added, stored);
                db.CellStyles.Add(added);
            }
        }
        db.SaveChanges();
    }

    /// <summary>Один аспект оформления колонок; аспект column_meta, как видимость и подпись.
    /// Список ключей, а не один: колонки выделяются пачкой (Shift+клик по заголовкам), и
    /// оформление применяется ко всему выделению — как дубль и удаление свойств.</summary>
    public StyleStamps SetColumnStyle(int collectionId, IReadOnlyList<string> keys, StyleAspect aspect, string? value) =>
        SetColumnStyle(collectionId, keys, [(aspect, value)]);

    /// <summary>Несколько аспектов оформления колонок одним сохранением. Возвращает отметки,
    /// как у SetRowStyle.</summary>
    public StyleStamps SetColumnStyle(int collectionId, IReadOnlyList<string> keys,
                                      IReadOnlyList<(StyleAspect Aspect, string? Value)> changes)
    {
        if (keys.Count == 0 || changes.Count == 0) return default;
        Demand(PermAction.EditField, collectionId);
        var stored = StoredChanges(changes);

        // Сброс отмечается, только если аспект задан хоть у одной строки — иначе перебивать нечего.
        // Строк record_display бывает много, поэтому вопрос задается по аспекту и только о сбросах.
        var rowAspects = stored.Where(c => c.Value is null).Select(c => c.Aspect).Distinct()
                               .Where(a => RowsStyle(collectionId, a)).ToHashSet();
        var applied    = Stamps(stored, StyleStamps.Next(), rowAspects.Contains);

        using var tx = db.Database.BeginTransaction();
        var existing = db.ColumnMeta
            .Where(m => m.CollectionId == collectionId && keys.Contains(m.Key))
            .ToDictionary(m => m.Key);

        foreach (var key in keys)
        {
            if (!existing.TryGetValue(key, out var row))
            {
                row = new ColumnMetaEntity { CollectionId = collectionId, Key = key };
                db.ColumnMeta.Add(row);
            }
            SetTableStyle(row, stored, applied);
            RemoveColumnMetaIfEmpty(row);
        }
        db.SaveChanges();

        ClearCellAspects(db.CellStyles.Where(c => c.CollectionId == collectionId && keys.Contains(c.Key)),
                         stored.Select(c => c.Aspect));
        tx.Commit();
        return applied;
    }

    // Значения, годные к записи: мусор (битый hex, незнакомое слово) становится сбросом аспекта.
    private static List<(StyleAspect Aspect, string? Value)> StoredChanges(
        IReadOnlyList<(StyleAspect Aspect, string? Value)> changes) =>
        changes.Select(c => (c.Aspect, RowStyle.Stored(c.Aspect, c.Value))).ToList();

    private static void SetTableStyle(ITableStyleRow row, List<(StyleAspect Aspect, string? Value)> changes)
    {
        foreach (var (aspect, value) in changes)
        {
            switch (aspect)
            {
                case StyleAspect.Background: row.Bg     = value; break;
                case StyleAspect.Text:       row.Fg     = value; break;
                case StyleAspect.HAlign:     row.HAlign = value; break;
                case StyleAspect.VAlign:     row.VAlign = value; break;
            }
        }
    }

    // Отметки выставления по аспектам: значение отмечается всегда, сброс — только если
    // stampReset(аспект): тогда это выставленное «По умолчанию», а не отсутствие настройки.
    private static StyleStamps Stamps(List<(StyleAspect Aspect, string? Value)> changes, long at,
                                      Func<StyleAspect, bool> stampReset)
    {
        var stamps = default(StyleStamps);
        foreach (var (aspect, value) in changes)
            stamps = stamps.With(aspect, value is not null || stampReset(aspect) ? at : 0);
        return stamps;
    }

    // Строке и колонке — значения вместе с отметками (0 — отметки нет).
    private static void SetTableStyle(ITableStyleStamps row, List<(StyleAspect Aspect, string? Value)> changes,
                                      StyleStamps applied)
    {
        SetTableStyle(row, changes);
        foreach (var (aspect, _) in changes)
        {
            long? stamp = applied.Of(aspect) is > 0 and var at ? at : null;
            switch (aspect)
            {
                case StyleAspect.Background: row.BgAt     = stamp; break;
                case StyleAspect.Text:       row.FgAt     = stamp; break;
                case StyleAspect.HAlign:     row.HAlignAt = stamp; break;
                case StyleAspect.VAlign:     row.VAlignAt = stamp; break;
            }
        }
    }

    private static StyleStamps StampsOf(ITableStyleStamps row) =>
        new(row.BgAt ?? 0, row.FgAt ?? 0, row.HAlignAt ?? 0, row.VAlignAt ?? 0);

    private static void SetStamps(ITableStyleStamps row, StyleStamps at)
    {
        row.BgAt     = at.Bg     > 0 ? at.Bg     : null;
        row.FgAt     = at.Fg     > 0 ? at.Fg     : null;
        row.HAlignAt = at.HAlign > 0 ? at.HAlign : null;
        row.VAlignAt = at.VAlign > 0 ? at.VAlign : null;
    }

    // Отметка без значения — тоже оформление: выставленное «По умолчанию» (см. RowStyle.At).
    private static bool HasTableStyle(ITableStyleRow row) =>
        row.Bg is not null || row.Fg is not null || row.HAlign is not null || row.VAlign is not null
        || row is ITableStyleStamps s
           && (s.BgAt is not null || s.FgAt is not null || s.HAlignAt is not null || s.VAlignAt is not null);

    /// <summary>Оформление строк коллекции: recordId → стиль (аспекты record_display с
    /// отметками). Только заданные строки: таблица разрежена, отсутствие ключа = строка без
    /// своего оформления.</summary>
    public Dictionary<int, RowStyle> GetRowStyles(int collectionId) =>
        db.RecordDisplay.AsNoTracking()
          .Where(d => d.CollectionId == collectionId
                      && (d.Bg != null || d.Fg != null || d.HAlign != null || d.VAlign != null
                          || d.BgAt != null || d.FgAt != null || d.HAlignAt != null || d.VAlignAt != null))
          .Select(d => new { d.RecordId, d.Bg, d.Fg, d.HAlign, d.VAlign, d.BgAt, d.FgAt, d.HAlignAt, d.VAlignAt })
          .ToList()
          .ToDictionary(d => d.RecordId, d => RowStyle.Clean(d.Bg, d.Fg, d.HAlign, d.VAlign,
              new StyleStamps(d.BgAt ?? 0, d.FgAt ?? 0, d.HAlignAt ?? 0, d.VAlignAt ?? 0)));

    /// <summary>Оформление колонок: key → стиль (аспекты column_meta с отметками).</summary>
    public Dictionary<string, RowStyle> GetColumnStyles(int collectionId) =>
        db.ColumnMeta.AsNoTracking()
          .Where(m => m.CollectionId == collectionId
                      && (m.Bg != null || m.Fg != null || m.HAlign != null || m.VAlign != null
                          || m.BgAt != null || m.FgAt != null || m.HAlignAt != null || m.VAlignAt != null))
          .Select(m => new { m.Key, m.Bg, m.Fg, m.HAlign, m.VAlign, m.BgAt, m.FgAt, m.HAlignAt, m.VAlignAt })
          .ToList()
          .ToDictionary(m => m.Key, m => RowStyle.Clean(m.Bg, m.Fg, m.HAlign, m.VAlign,
              new StyleStamps(m.BgAt ?? 0, m.FgAt ?? 0, m.HAlignAt ?? 0, m.VAlignAt ?? 0)));

    /// <summary>Оформление ячеек ОДНОЙ СТРАНИЦЫ записей: recordId → (key → стиль).
    /// Зовется постранично рядом с GetFieldValuesBatch; AsNoTracking по той же причине.</summary>
    public Dictionary<int, Dictionary<string, RowStyle>> GetCellStylesBatch(
        int collectionId, IReadOnlyList<int> recordIds)
    {
        if (recordIds.Count == 0) return [];
        return db.CellStyles.AsNoTracking()
            .Where(c => c.CollectionId == collectionId && recordIds.Contains(c.RecordId))
            .Select(c => new { c.RecordId, c.Key, c.Bg, c.Fg, c.HAlign, c.VAlign })
            .ToList()
            .GroupBy(c => c.RecordId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Key, c => RowStyle.Clean(c.Bg, c.Fg, c.HAlign, c.VAlign)));
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

    public CalendarRecord AddCalendarRecord(CalendarRecord r) => AddCalendarRecordCore(r, resolveAutoFiles: true);

    private CalendarRecord AddCalendarRecordCore(CalendarRecord r, bool resolveAutoFiles)
    {
        Demand(PermAction.CreateRecord, r.CollectionId);
        var colId = r.CollectionId ?? GetOrCreateDefaultCollection();
        var dateFieldId = ResolveCalendarDateField(colId, r.FieldId);
        var descFieldId = EnsureDescriptionField(colId);

        var rec = new DbRecordEntity
        {
            CollectionId = colId,
            StartDt      = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o"),
            EndDt        = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o"),
            AllDay       = r.AllDay,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = AllocatePositions(colId, null, 1).At(0),
            Title        = r.Title,
        };
        db.DbRecords.Add(rec);
        Log("create", "record", r.Title, CollectionNameOf(colId));
        db.SaveChanges();

        SetCalendarDateValue(rec.Id, dateFieldId, r);
        // Календарь — один из двух путей, которые заводят поле «Описание» (второй — задачи):
        // поле появляется у коллекции при КАЖДОМ создании записи отсюда, даже если текст пуст.
        SetFieldValue(rec.Id, descFieldId, r.Description ?? "");

        r.Id           = rec.Id;
        r.CollectionId = colId;
        r.FieldId      = dateFieldId;
        if (resolveAutoFiles) ResolveAutoFileForRecord(colId, rec.Id);
        NotifyRelationDataChanged(colId, recordId: rec.Id);
        return r;
    }

    // Отдельного «календарного» дубля больше нет: он копировал только шапку и поле-дату, и копия
    // из календаря отличалась от копии той же записи из таблицы (ни прочих свойств, ни связей).
    // Календарь ходит в общие DuplicateRecord / DuplicateRecordsMappedIsolated.

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
            // Смена коллекции = перенос: создаем новую запись в целевой коллекции (правленый
            // header + выбранное поле-дата + индекс), затем грамотно переносим остальные значения
            // свойств (по имени, с приведением типов) — иначе они терялись. Недостающие в цели
            // свойства НЕ создаем (окно календаря визуально не меняем). Поле-дату исключаем, чтобы
            // не затереть отредактированную дату.
            //
            // Связи лежат не в db_field_values, а в db_record_links — CopyFieldValuesByName их не
            // видит, и без CopyRelationLinksByName исходящие связи записи пропадали молча (тогда как
            // тот же перенос из таблицы, через MoveRecordsCore, их сохраняет). Перенос здесь всегда
            // ВНУТРИ одной базы, поэтому DbId не передаем: TargetDbId ребер не пересчитывается.
            // Входящие связи все равно оборвутся (у копии новый Id) — предупреждает UI.
            var oldId = rec.Id;
            var audit = (rec.CreatedAt, rec.UpdatedAt, rec.CreatedBy, rec.UpdatedBy);
            var previousDto = (r.Id, r.CollectionId, r.FieldId);
            using var move = BeginWrite();
            try
            {
                // Проверяем до создания записи, в том числе при перехвате отказа внутри чужой tx.
                var targetRelations = new Dictionary<string, DbField>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in GetFields(newColId).Where(f => f.FieldType == "relation"))
                    targetRelations.TryAdd(field.Name, field);
                foreach (var field in GetFields(oldColId).Where(f => f.FieldType == "relation"))
                {
                    if (!targetRelations.TryGetValue(field.Name, out var target)) continue;
                    var sourceConfig = RelationConfig.FromConfig(field.ConfigJson);
                    var targetConfig = RelationConfig.FromConfig(target.ConfigJson);
                    if (sourceConfig is not null && targetConfig is { Single: true }
                        && targetConfig.TargetCollectionId == sourceConfig.TargetCollectionId
                        && targetConfig.TargetDbId == sourceConfig.TargetDbId)
                        EnsureSingleRelationMove(this, field.Id!.Value, [oldId], target.Name);
                }
                r.CollectionId = newColId;
                r.Id = null;
                // Перенос сначала копирует все значения. Иначе старый файловый путь
                // затрет результат начального подбора, а источник-текст еще не заполнен.
                AddCalendarRecordCore(r, resolveAutoFiles: false);
                CopyFieldValuesByName(this, oldId, oldColId, r.Id!.Value, newColId,
                                      createMissingFields: false, excludeTargetFieldId: r.FieldId);
                CopyRelationLinksByName(this, oldId, oldColId, r.Id!.Value, newColId);
                ResolveAutoFileForRecord(newColId, r.Id.Value);
                RestoreMovedRecordAudit(r.Id!.Value, audit);
                DeleteRecord(oldId);
                move.Commit();
            }
            catch
            {
                (r.Id, r.CollectionId, r.FieldId) = previousDto;
                throw;
            }
            return;
        }

        var titleChanged = rec.Title != r.Title;
        rec.StartDt     = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o");
        rec.EndDt       = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o");
        rec.AllDay      = r.AllDay;
        rec.Title       = r.Title;
        Log("update", "record", r.Title, CollectionNameOf(newColId));
        db.SaveChanges();

        var dateFieldId = ResolveCalendarDateField(newColId, r.FieldId);
        SetCalendarDateValue(rec.Id, dateFieldId, r);
        r.FieldId = dateFieldId;
        // Описание — обычное поле: правка события пишет его туда же, куда и создание.
        SetFieldValue(rec.Id, EnsureDescriptionField(newColId), r.Description ?? "");
        if (titleChanged) ReactToSourceEdit(rec.Id, rule => rule.SourceIsTitle);
        NotifyRelationDataChanged(newColId, recordId: rec.Id);
    }

    /// <summary>
    /// Возвращает перенесенной записи аудит источника (четыре поля). Смена коллекции у события
    /// ПЕРЕСОЗДАЕТ строку <c>db_records</c>, и запись обрастает чужим аудитом трижды:
    /// <see cref="AddCalendarRecord"/> ставит <c>CreatedAt</c> = «сейчас», <c>StampNewRecordAudit</c>
    /// (AppDbContext) — текущего пользователя автором, а копирование значений и связей
    /// (<c>SetFieldValue</c>/<c>TouchUpdated</c>) добивает «изменено». Для ПЕРЕНОСА все это ложь:
    /// событие коллеги выглядело бы созданным вами сегодня — ровно тот дефект, что закрыт в
    /// <see cref="MoveRecordsCore"/> для переноса из таблицы; правка события БЕЗ смены коллекции
    /// аудит тоже не трогает. Поэтому вызывать нужно ПОСЛЕ копирования значений и связей —
    /// до него штампы легли бы поверх восстановленного.
    /// Пустые поля источника (старые записи) не трогаем: там штамп — единственное, что есть.
    /// </summary>
    private void RestoreMovedRecordAudit(
        int recordId, (string CreatedAt, string UpdatedAt, string CreatedBy, string UpdatedBy) source)
    {
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return;
        if (source.CreatedAt.Length > 0) rec.CreatedAt = source.CreatedAt;
        if (source.UpdatedAt.Length > 0) rec.UpdatedAt = source.UpdatedAt;
        if (source.CreatedBy.Length > 0) rec.CreatedBy = source.CreatedBy;
        if (source.UpdatedBy.Length > 0) rec.UpdatedBy = source.UpdatedBy;
        db.SaveChanges();
    }

    public void DeleteCalendarRecord(int id)
    {
        Demand(PermAction.DeleteRecord, CollectionOfRecord(id));
        EnsureDeleteAllowed([id]);
        DeleteCalendarRecordCore(id);
    }

    /// <summary>Удалить набор записей календаря или задач одной базы одной операцией: права на все
    /// их коллекции и запрет удаления проверяются до первой записи, удаление идет одной
    /// транзакцией. Ссылки между записями набора запрету не мешают — набор уходит целиком или не
    /// уходит вовсе, поэтому сбой на середине не оставит источник без удаленной цели.</summary>
    public void DeleteCalendarRecords(IReadOnlyCollection<int> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return;
        foreach (var collection in list.Select(CollectionOfRecord).Distinct())
            Demand(PermAction.DeleteRecord, collection);
        EnsureDeleteAllowed(list);
        using var write = BeginWrite();
        foreach (var id in list) DeleteCalendarRecordCore(id);
        write.Commit();
    }

    private void DeleteCalendarRecordCore(int id)
    {
        var rec = db.DbRecords.Find(id);
        if (rec is not null)
        {
            Log("delete", "record", rec.Title, CollectionNameOf(rec.CollectionId));
            // Входящие связи в этой же БД: FK на ToRecordId снят (цель может быть межбазовой) —
            // чистим прикладно, как DeleteRecord/DeleteRecordsData. Без этого удаление из календаря
            // и задач оставляло висячие ребра (невидимые в ячейке, но живущие в db_record_links).
            // Исходящие уходят каскадом FromRecordId вместе с записью.
            var incoming = db.DbRecordLinks.Where(l => l.TargetDbId == 0 && l.ToRecordId == id).ToList();
            db.DbRecordLinks.RemoveRange(incoming);
            db.DbRecords.Remove(rec);
            db.SaveChanges();
            NotifyRelationDataChanged(rec.CollectionId);
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
    //    не добавляется вовсе; db_fields крошечная, ее запрос копеечный.
    private string VisibilityFilter(ISet<int>? activeIds, string prefix = "")
    {
        // Коллекции, у которых ВСЕ поля-даты скрыты, отсекаются целиком на уровне CollectionId:
        // фильтр по FieldId — residual (не ложится на индекс), и «галка включена, но поля
        // скрыты» платила бы полные сканы диапазона (день-гигант — секунды «Загрузки…»)
        // ради заведомо пустого результата.
        var fields = ReadCalendarFields(withCollections: activeIds is null);
        var fullyHidden = fields.FullyHidden;

        List<int> cols;
        if (activeIds is null)
        {
            // Без фильтра коллекций; если полностью скрытых нет — хватает residual по полям.
            if (fullyHidden.Count == 0) return fields.HiddenClause(prefix);
            cols = fields.Collections!.Where(id => !fullyHidden.Contains(id)).ToList();
        }
        else
            cols = activeIds.Where(id => !fullyHidden.Contains(id)).ToList();

        // Пустой набор (все галки сняты / все скрыто) — НИ ОДНОЙ, а не «без фильтра».
        if (cols.Count == 0) return " AND 1=0";

        var f = $" AND {prefix}CollectionId IN ({string.Join(",", cols)})";
        var restHidden = new List<int>();
        foreach (var (id, col) in fields.HiddenDateFields)
            if (!fullyHidden.Contains(col)) restHidden.Add(id);
        if (restHidden.Count > 0) f += $" AND {prefix}FieldId NOT IN ({string.Join(",", restHidden)})";
        return f;
    }

    // Поля, от которых зависят чтения календаря: поля-даты с видимостью и поля с ролью
    // «Описание», плюс (по запросу) все коллекции базы. Читаются прямым запросом: db_fields
    // крошечная, а прежние четыре запроса EF (скрытые поля, целиком скрытые коллекции, список
    // коллекций, описания) при первом показе календаря в процессе собирались заново — 0,3 с на
    // все базы разом, причем фоновые чтения баз ждали друг друга на общем кеше запросов EF.
    // Хранится в обычных словарях и множествах целых, без кортежей: коллекции над кортежами
    // компилируются на лету при первом обращении (в предкомпилированном коде их нет), и это
    // тоже ложилось на первый показ календаря.
    private sealed class CalendarFields
    {
        public readonly Dictionary<int, int> HiddenDateFields = [];        // поле → коллекция
        public readonly HashSet<int> DateCollections = [];                 // есть поля-даты
        public readonly HashSet<int> VisibleDateCollections = [];          // есть видимое поле-дата
        public readonly Dictionary<int, List<int>> DescriptionFields = []; // коллекция → поля по номеру
        public List<int>? Collections;

        // Коллекции, у которых ВСЕ поля-даты скрыты, — per-collection проходы пропускают их целиком.
        public HashSet<int> FullyHidden
        {
            get
            {
                var result = new HashSet<int>(DateCollections);
                result.ExceptWith(VisibleDateCollections);
                return result;
            }
        }

        // Часть фильтра по скрытым полям-датам — для запросов, где коллекция уже зафиксирована
        // (per-collection проходы GetCalendarRecordsByRangeCapped).
        public string HiddenClause(string prefix = "") =>
            HiddenDateFields.Count == 0 ? "" : $" AND {prefix}FieldId NOT IN ({string.Join(",", HiddenDateFields.Keys)})";

        // Коллекции чтения: активные (null — все) без целиком скрытых.
        public List<int> Readable(ISet<int>? activeIds)
        {
            var fullyHidden = FullyHidden;
            return (activeIds ?? (IEnumerable<int>)Collections!).Where(id => !fullyHidden.Contains(id)).ToList();
        }

        // Описание для карточек ОДНОЙ коллекции: ее поля с ролью «Описание» известны заранее, и
        // значение берется сиком по ключу (RecordId, FieldId), а не перебором всех значений записи
        // с соединением полей на каждое событие (DescExpr). Порядок тот же — первое по номеру поле
        // со значением. Нет таких полей — NULL без подзапроса. Для чтений, где коллекция
        // зафиксирована условием.
        public string Description(int collectionId) =>
            DescriptionFields.TryGetValue(collectionId, out var ids)
                ? "(SELECT fv.Value FROM db_field_values fv WHERE fv.RecordId = r.Id" +
                  $" AND fv.FieldId IN ({string.Join(",", ids)}) LIMIT 1)"
                : "NULL";
    }

    private CalendarFields ReadCalendarFields(bool withCollections)
    {
        var fields = new CalendarFields();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                if (db.Database.CurrentTransaction is { } t) cmd.Transaction = t.GetDbTransaction();
                cmd.CommandText = "SELECT Id, CollectionId, FieldType, IsCalStart, IsDescription FROM db_fields " +
                                  "WHERE FieldType = 'date' OR IsDescription = 1 ORDER BY Id";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    var id  = Convert.ToInt32(rdr.GetValue(0));
                    var col = Convert.ToInt32(rdr.GetValue(1));
                    if (rdr.GetString(2) == "date")
                    {
                        fields.DateCollections.Add(col);
                        if (Convert.ToInt32(rdr.GetValue(3)) != 0) fields.VisibleDateCollections.Add(col);
                        else fields.HiddenDateFields[id] = col;
                    }
                    if (Convert.ToInt32(rdr.GetValue(4)) != 0)
                    {
                        if (!fields.DescriptionFields.TryGetValue(col, out var ids))
                            fields.DescriptionFields[col] = ids = [];
                        ids.Add(id);
                    }
                }
            }
            if (withCollections)
            {
                using var cmd = conn.CreateCommand();
                if (db.Database.CurrentTransaction is { } t) cmd.Transaction = t.GetDbTransaction();
                cmd.CommandText = "SELECT Id FROM db_collections";
                using var rdr = cmd.ExecuteReader();
                fields.Collections = [];
                while (rdr.Read()) fields.Collections.Add(Convert.ToInt32(rdr.GetValue(0)));
            }
        }
        finally { if (opened) conn.Close(); }
        return fields;
    }

    /// <summary>Число событий, начинающихся в дне [dayStart, dayEnd) (для «показано N из total»):
    /// те же счетчики дней, что у «Журнала» — полный день берется из сводки, а не счетом строк
    /// (день-гигант на 5 млн событий — это 0,7 с счета на каждый показ дня).</summary>
    public int CountCalendarDayEvents(DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds) =>
        GetCalendarDayCountsByRange(dayStart, dayEnd, activeIds).Sum(d => d.Count);

    /// <summary>
    /// Число событий ПО ДНЯМ в окне [start, end): "yyyy-MM-dd" → count, только дни с событиями,
    /// по возрастанию. Для «Журнала», карты счетчиков Месяца и Года, счета дня «Расписания»:
    /// полоса прокрутки на весь объем окна без материализации событий.
    ///
    /// Полные дни окна берутся из сводки <c>calendar_day_counts</c> (миграция
    /// AddCalendarDayCounts, ее держат триггеры проекции): строка на коллекцию, день и поле —
    /// счет не зависит от числа событий. Неполные крайние дни (окно не с полуночи или не до
    /// полуночи) и база без сводки считаются по самой проекции (<see cref="CountDaysByIndex"/>).
    /// </summary>
    public List<(string Day, int Count)> GetCalendarDayCountsByRange(
        DateTime start, DateTime end, ISet<int>? activeIds)
    {
        // Те же коллекции и скрытые поля, что у фильтра видимости: коллекции с целиком скрытыми
        // датами пропускаются, частично скрытые поля отсекает условие по FieldId.
        var fields = ReadCalendarFields(withCollections: activeIds is null);
        var colIds = fields.Readable(activeIds);
        var hidden = fields.HiddenClause();

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (colIds.Count == 0 || end <= start) return [];

        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try { FillDayCounts(conn, colIds, hidden, start, end, counts); }
        finally { if (opened) conn.Close(); }
        return counts.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // Счет по дням окна [start, end) в counts: полные дни — из сводки, неполные крайние дни и
    // база без сводки — по самой проекции.
    private void FillDayCounts(System.Data.Common.DbConnection conn, List<int> colIds, string hidden,
                               DateTime start, DateTime end, SortedDictionary<string, int> counts)
    {
        var firstFull = start.TimeOfDay == TimeSpan.Zero ? start.Date : SafeAddDays(start.Date, 1);
        var endFull   = end.Date;
        if (firstFull < endFull && HasCalendarDayCounts(conn))
        {
            CountDaysBySummary(conn, colIds, hidden, firstFull, endFull, counts);
            if (start < firstFull) CountDaysByIndex(conn, colIds, hidden, start, firstFull, counts);
            if (endFull < end)     CountDaysByIndex(conn, colIds, hidden, endFull, end, counts);
        }
        else CountDaysByIndex(conn, colIds, hidden, start, end, counts);
    }

    // Полные дни [from, to) из сводки: несколько строк на день (по коллекциям и полям).
    private static void CountDaysBySummary(System.Data.Common.DbConnection conn, List<int> colIds, string hidden,
                                           DateTime from, DateTime to, SortedDictionary<string, int> counts)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"SELECT StartDay, SUM(Events) FROM calendar_day_counts
                             WHERE CollectionId IN ({string.Join(",", colIds)})
                               AND StartDay >= @from AND StartDay < @to AND Events > 0{hidden}
                             GROUP BY StartDay";
        AddParam(cmd, "@from").Value = from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        AddParam(cmd, "@to").Value   = to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var n = Convert.ToInt32(rdr.GetValue(1));
            if (n > 0) counts[rdr.GetString(0)] = counts.GetValueOrDefault(rdr.GetString(0)) + n;
        }
    }

    // Счет по самой проекции — от дня с событиями к следующему по индексу (CollectionId, AllDay,
    // StartIso, …), на коллекцию и признак «весь день»: первое начало не раньше курсора (сик),
    // затем COUNT этого дня (диапазон индекса), курсор — на следующий день. Цена — число дней с
    // событиями плюс число событий (прежний GROUP BY по substr(StartIso, 1, 10) гнал каждую строку
    // через временное дерево и был в 6–7 раз медленнее). Нужен для неполных крайних дней окна и
    // для базы без сводки.
    private static void CountDaysByIndex(System.Data.Common.DbConnection conn, List<int> colIds, string hidden,
                                         DateTime start, DateTime end, SortedDictionary<string, int> counts)
    {
        var s = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
        var e = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");

        using var next = conn.CreateCommand();
        next.CommandText = $@"SELECT StartIso FROM calendar_index
                              WHERE CollectionId = @c AND AllDay = @a
                                AND StartIso >= @from AND StartIso < @to{hidden}
                              ORDER BY StartIso LIMIT 1";
        var nc = AddParam(next, "@c"); var na = AddParam(next, "@a");
        var nf = AddParam(next, "@from"); AddParam(next, "@to").Value = e;

        using var count = conn.CreateCommand();
        count.CommandText = $@"SELECT COUNT(*) FROM calendar_index
                               WHERE CollectionId = @c AND AllDay = @a
                                 AND StartIso >= @from AND StartIso < @to{hidden}";
        var cc = AddParam(count, "@c"); var ca = AddParam(count, "@a");
        var cf = AddParam(count, "@from"); var ct = AddParam(count, "@to");

        foreach (var colId in colIds)
            foreach (var allDay in new[] { 0, 1 })
            {
                nc.Value = cc.Value = colId;
                na.Value = ca.Value = allDay;
                var cursor = s;
                while (string.CompareOrdinal(cursor, e) < 0)
                {
                    nf.Value = cursor;
                    if (next.ExecuteScalar() is not string first || first.Length < 10) break;

                    // Граница дня — строкой даты: "2026-07-02" меньше любого "2026-07-02T…",
                    // поэтому полночь следующего дня в этот день не попадает.
                    var day     = first[..10];
                    var nextDay = DateOnly.ParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                                          .AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    var upTo    = string.CompareOrdinal(nextDay, e) < 0 ? nextDay : e;
                    cf.Value = cursor;
                    ct.Value = upTo;
                    var n = Convert.ToInt32(count.ExecuteScalar() ?? 0);
                    if (n > 0) counts[day] = counts.GetValueOrDefault(day) + n;
                    cursor = upTo;
                }
            }
    }

    // Есть ли в базе сводка календаря (миграции AddCalendarDayCounts, AddCalendarSpans): база,
    // открытая этой версией, их имеет всегда — проверка страхует чтение от базы, к которой
    // миграция не прошла.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Db, string Table), bool> CalendarTablesPresent = new();

    private bool HasCalendarDayCounts(System.Data.Common.DbConnection conn) =>
        HasCalendarTable(conn, "calendar_day_counts");

    private bool HasCalendarTable(System.Data.Common.DbConnection conn, string table)
    {
        if (CalendarTablesPresent.TryGetValue((db.DbPath, table), out var known)) return known;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = db.IsPostgres
            ? $"SELECT to_regclass('{table}') IS NOT NULL"
            : $"SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{table}')";
        var present = Convert.ToBoolean(cmd.ExecuteScalar());
        if (present) CalendarTablesPresent[(db.DbPath, table)] = true;   // отсутствие не запоминаем: миграция может догнать
        return present;
    }

    // ── Сводка длительностей (calendar_spans) ───────────────────────────────────────
    //
    // Наибольшая длительность события на коллекцию и признак «весь день»; ее держат триггеры
    // проекции (миграция AddCalendarSpans). Событие, пересекающее момент T, началось не раньше
    // T − M и кончается не позже T + M, поэтому события, начавшиеся до окна, и самый поздний
    // конец ищутся в полосе шириной M у границы окна, а не во всем прошлом или будущем
    // коллекции (на 10 млн событий — это миллионы строк индекса на каждое окно). Граница только
    // растет (удаление ее не уменьшает): чтение от этого шире, но событий не теряет.

    // Длительность, которую триггер ставит неразобранной дате: границы нет.
    private const long CalendarSpanUnbounded = 99_999_999_999;

    // Коллекция → длительности по виду: [0] — со временем, [1] — «весь день»; -1 — событий этого
    // вида нет. Массив, а не ключ-кортеж: словарь над кортежем компилировался бы на лету при
    // первом показе календаря (см. CalendarFields).
    private sealed class CalendarSpans(Dictionary<int, long[]>? seconds)
    {
        /// <summary>Сводка есть в базе; без нее чтения идут как до нее.</summary>
        public bool Known => seconds is not null;

        /// <summary>Есть ли у коллекции события этого вида (без сводки неизвестно — считаем, что есть).</summary>
        public bool Has(int collectionId, bool allDay) =>
            seconds is null || (seconds.TryGetValue(collectionId, out var s) && s[allDay ? 1 : 0] >= 0);

        /// <summary>Верхняя граница длительности события этого вида в коллекции; null — границы
        /// нет (сводки нет, дата не разобрана или событий этого вида нет — тогда см. <see cref="Has"/>).</summary>
        public TimeSpan? Max(int collectionId, bool allDay) =>
            seconds is not null && seconds.TryGetValue(collectionId, out var s) && s[allDay ? 1 : 0] is var v and >= 0
                ? Bound(v) : null;

        /// <summary>То же по всем коллекциям базы (для чтений с фильтром по нескольким коллекциям).</summary>
        public TimeSpan? MaxAll(bool allDay)
        {
            if (seconds is null) return null;
            long max = 0;
            foreach (var s in seconds.Values) max = Math.Max(max, s[allDay ? 1 : 0]);
            return Bound(max);
        }

        private static TimeSpan? Bound(long s) => s >= CalendarSpanUnbounded ? null : TimeSpan.FromSeconds(s);
    }

    private CalendarSpans ReadCalendarSpans(System.Data.Common.DbConnection conn)
    {
        if (!HasCalendarTable(conn, "calendar_spans")) return new CalendarSpans(null);
        var map = new Dictionary<int, long[]>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CollectionId, AllDay, MaxSeconds FROM calendar_spans";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var col = Convert.ToInt32(rdr.GetValue(0));
            if (!map.TryGetValue(col, out var s)) map[col] = s = [-1, -1];
            s[Convert.ToInt32(rdr.GetValue(1)) != 0 ? 1 : 0] = Convert.ToInt64(rdr.GetValue(2));
        }
        return new CalendarSpans(map);
    }

    // Сдвиг момента с защитой от выхода за диапазон DateTime (граница «вся история» — 0001 год).
    private static DateTime SafeAdd(DateTime dt, TimeSpan delta) =>
        delta < TimeSpan.Zero
            ? (dt.Ticks + delta.Ticks < DateTime.MinValue.Ticks ? DateTime.MinValue : dt + delta)
            : (dt.Ticks > DateTime.MaxValue.Ticks - delta.Ticks ? DateTime.MaxValue : dt + delta);

    /// <summary>
    /// Убрать из сводки дней нулевые строки. Триггер удаления строки проекции только уменьшает
    /// счетчик (массовое удаление не платит вторым DELETE за каждую строку), поэтому пустые дни
    /// копятся — их убирают здесь, при подключении базы. Чтение нули и так пропускает.
    /// </summary>
    public void TidyCalendarDayCounts()
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            if (!HasCalendarDayCounts(conn)) return;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM calendar_day_counts WHERE Events <= 0";
            cmd.ExecuteNonQuery();
        }
        finally { if (opened) conn.Close(); }
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
    /// «Журнала» на гигантских днях (миллионы событий): дает порядок по времени БЕЗ материализации
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
    /// На PostgreSQL LIKE различает регистр и у латиницы — там сравнение ILIKE (<c>LikeOp</c>).
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
            var op   = LikeOp(ctx.IsPostgres);
            var like = string.Join(" OR ", variants.Select((_, i) => $"r.Title {op} @q{i} ESCAPE '\\'"));

            var result = new List<CalendarRecord>();
            var conn = ctx.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT ci.RecordId, ci.FieldId, ci.CollectionId, ci.StartIso, ci.EndIso, ci.AllDay,
                                        r.Title, {DescExpr}, c.Name, c.Color
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
                if (!TryParseIso(rdr.GetString(3), out var st)) continue;
                if (!TryParseIso(rdr.GetString(4), out var en)) continue;
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
    /// (CollectionId, AllDay, StartIso), поэтому выборка идет seek'ом без сортировки всего дня
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
                            r.Title, {DescExpr}, c.Name, c.Color
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
                if (!TryParseIso(rdr.GetString(3), out var st)) continue;
                if (!TryParseIso(rdr.GetString(4), out var en)) continue;
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
    // для стыковки offset all-day→timed и подсчета.
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
    /// (all-day первыми, затем timed; внутри — по StartIso). Пропуск идет по покрывающему индексу
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
        // (без дублей/пропусков на границах страниц с равным StartIso). Быстрый пропуск идет по
        // индексу (CollectionId, AllDay, StartIso, RecordId, FieldId) без временной сортировки.
        var sql = $@"SELECT ci.RecordId, ci.FieldId, ci.CollectionId, ci.StartIso, ci.EndIso, ci.AllDay,
                            r.Title, {DescExpr}, c.Name, c.Color
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
                if (!TryParseIso(rdr.GetString(3), out var st)) continue;
                if (!TryParseIso(rdr.GetString(4), out var en)) continue;
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

    /// <summary>Фоновый подсчет числа событий дня (для размера полосы прокрутки) — изолированное подключение.</summary>
    public Task<int> CountCalendarDayEventsAsync(DateTime dayStart, DateTime dayEnd, ISet<int>? activeIds) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx).CountCalendarDayEvents(dayStart, dayEnd, activeIds);
        });

    /// <summary>Ранг события внутри дня — число событий строго ДО него в порядке «Расписания»
    /// (all-day первыми, затем (StartIso, RecordId, FieldId)); изолированное подключение.
    /// Нужен точечной правке offset-режима: вставить строку созданной/измененной записи в кеш
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

    // Граница длительности timed-события для базы БЕЗ сводки длительностей (calendar_spans):
    // насколько далеко назад от начала окна ищутся timed, начавшиеся до него. Держится на
    // допущении «timed длиннее 62 дней не бывает» — timed длиннее в такой базе сетки теряют.
    // Со сводкой граница точная (наибольшая длительность коллекции) и допущения нет.
    private const int CalendarTimedBufferDays = 62;

    // Строит события окна календаря из проекции calendar_index. Чтобы индекс не сканировал
    // все «от начала времен» (StartIso имеет верхнюю границу окна, но не нижнюю), окно
    // берется ДВУМЯ под-запросами по индексу (CollectionId, AllDay, StartIso):
    //   • timed (AllDay=0): с нижней границей StartIso = начало окна − буфер → быстрый
    //     двусторонний seek (основная масса событий);
    //   • all-day (AllDay=1): без нижней границы (их мало → дешево) — чтобы многодневные
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
        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);

        // Фильтр видимости: активные коллекции + скрытые поля-даты (пустой набор = ничего).
        var colFilter = VisibilityFilter(activeIds, "ci.");

        var result = new List<CalendarRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            // Нижняя граница начала — наибольшая длительность вида по базе (сводка длительностей);
            // без сводки timed — прежний буфер, all-day — без границы.
            var spans   = ReadCalendarSpans(conn);
            var timedLo = spans.Known ? spans.MaxAll(false) : TimeSpan.FromDays(CalendarTimedBufferDays);
            var allDayLo = spans.MaxAll(true);
            var timedArgs = new List<(string, string)> { ("@end", endIso), ("@start", startIso) };
            if (timedLo is { } tl) timedArgs.Add(("@lo", Iso(SafeAdd(rangeStart, -tl))));
            var allDayArgs = new List<(string, string)> { ("@end", endIso), ("@start", startIso) };
            if (allDayLo is { } al) allDayArgs.Add(("@lo", Iso(SafeAdd(rangeStart, -al))));

            ReadCalendarRows(conn, result,
                $"ci.AllDay = 0{colFilter}{(timedLo is null ? "" : " AND ci.StartIso >= @lo")} AND ci.StartIso < @end AND ci.EndIso >= @start",
                timedArgs);
            ReadCalendarRows(conn, result,
                $"ci.AllDay = 1{colFilter}{(allDayLo is null ? "" : " AND ci.StartIso >= @lo")} AND ci.StartIso < @end AND ci.EndIso >= @start",
                allDayArgs);
        }
        finally { if (opened) conn.Close(); }

        result.Sort((a, b) => a.StartDt.CompareTo(b.StartDt));
        return result;
    }

    /// <summary>
    /// Окно календаря для видов-сеток (День/Неделя/Месяц/Год) с ОГРАНИЧЕНИЕМ числа timed-событий
    /// на день: гигантский день (миллионы записей) отдает сетке первые <paramref name="perDayCap"/>
    /// строк вместо всех — больше ячейке все равно не отрисовать, остальное показывает «+N еще»
    /// по карте счетчиков (<see cref="GetCalendarDayCountsByRange"/>). Чтение — per-day ×
    /// per-collection: каждый запрос фиксирует (CollectionId, AllDay) одним значением, ложится
    /// сиком на индекс (CollectionId, AllDay, StartIso) и обрывается LIMIT'ом, НЕ сканируя день
    /// целиком (общий ORDER BY с IN по коллекциям заставил бы SQLite сортировать все миллионы
    /// строк во временной таблице — LIMIT бы не спас). Для дней внутри окна условие EndIso >= start
    /// выполняется автоматически (событие заканчивается не раньше своего старта) — фильтр целиком
    /// индексный. Спилловеры (timed, начавшиеся ДО окна, но пересекающие его) — отдельным проходом
    /// по буферу CalendarTimedBufferDays, как в <see cref="ReadCalendarEvents"/>; all-day — тоже
    /// per-collection с cap (обычно их мало, но импорт дат «без времени» может дать миллионы).
    /// <paramref name="allDayCap"/> — свой cap all-day прохода (он один на все окно, а не на
    /// день): Месяц берет timed по нескольку десятков на день — больше ячейка не показывает.
    /// <paramref name="allDayPerDay"/> задан — «весь день» тоже читается по дням окна, не больше
    /// стольких на день и коллекцию (плюс многодневные, начатые в дне, тем же cap), а общий
    /// <paramref name="allDayCap"/> остается только начатым до окна. Без этого месяц с тысячами
    /// записей «весь день» в день получал первые allDayCap по дате конца: записи доставались двум-трем
    /// первым дням, в остальных ячейках было одно «+N еще».
    /// </summary>
    public List<CalendarRecord> GetCalendarRecordsByRangeCapped(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perDayCap, int? allDayCap = null,
        int? multiDayPerDay = null, int? allDayPerDay = null)
    {
        // Per-collection запросы — нужны явные id: null — все коллекции БД, ПУСТОЙ набор —
        // ни одной (циклы не выполняются). Коллекции, у которых ВСЕ поля-даты скрыты,
        // пропускаются целиком; частично скрытые поля отсекает residual-фильтр чтения.
        var fields = ReadCalendarFields(withCollections: activeIds is null);
        var colIds = fields.Readable(activeIds);
        var hidden = fields.HiddenClause("ci.");
        var desc   = fields.Description;

        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);

        var result = new List<CalendarRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            // Дни окна отличаются только значениями параметров: команда каждой коллекции и прохода
            // готовится один раз, а не на каждый день (у «Месяца» 42 дня × коллекции × 2 прохода).
            using var rows = new CalendarRowReader(conn);
            var spans = ReadCalendarSpans(conn);

            // Timed по дням окна (окна сеток выровнены по дням — см. GetDateRange контроллера).
            // Коллекции без timed (по сводке длительностей) не спрашиваются вовсе.
            var timedCols = colIds.Where(c => spans.Has(c, false)).ToList();
            for (var day = rangeStart.Date; day < rangeEnd; day = day.AddDays(1))
            {
                var dayIso  = Iso(day);
                var nextIso = Iso(day.AddDays(1));
                foreach (var colId in timedCols)
                    rows.Read(result,
                        $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                        " AND ci.StartIso >= @d AND ci.StartIso < @d2",
                        [("@d", dayIso), ("@d2", nextIso)], perDayCap, desc: desc(colId));
            }

            // Многодневные timed, начинающиеся в дне окна (конец — в следующем дне или позже), —
            // отдельно от однодневных: при малом cap дня (Месяц) переполненный день иначе отдавал
            // только ранние события, и многодневные, начавшиеся вечером, пропадали. Сик по
            // EndIso-индексу от следующей полуночи в полосе наибольшей длительности, начало — в
            // дне (residual в том же индексе). Со строками однодневного прохода они пересекаются
            // (начало в том же дне) — повторы отбрасываются.
            if (multiDayPerDay is { } multiCap)
            {
                // Ключ «запись, поле» числом, а не кортежем (см. CalendarFields).
                static long Key(CalendarRecord r) => ((long)(r.Id ?? 0) << 32) | (uint)r.FieldId;
                var seen = new HashSet<long>();
                foreach (var r in result) if (!r.AllDay) seen.Add(Key(r));
                var multi = new List<CalendarRecord>();
                for (var day = rangeStart.Date; day < rangeEnd; day = day.AddDays(1))
                {
                    var dayIso  = Iso(day);
                    var nextIso = Iso(day.AddDays(1));
                    foreach (var colId in timedCols)
                    {
                        var (band, args) = NearEdge(spans.Max(colId, false), day, day.AddDays(1));
                        rows.Read(multi,
                            $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                            " AND ci.EndIso >= @d2 AND ci.StartIso >= @d AND ci.StartIso < @d2" + band,
                            [("@d", dayIso), ("@d2", nextIso), .. args], multiCap, "ci.EndIso", desc(colId));
                    }
                }
                foreach (var r in multi) if (seen.Add(Key(r))) result.Add(r);
            }

            // Timed-спилловеры (начались ДО окна, но пересекают его) — сик по EndIso-индексу:
            // EndIso ∈ [начало окна, начало + M], StartIso ∈ [начало − M, начало), где M —
            // наибольшая длительность timed коллекции (сводка длительностей): полоса у границы
            // окна, а не все будущее коллекции. Без сводки M — буфер CalendarTimedBufferDays
            // (прежнее допущение). Прежний проход сканировал StartIso-буфер с проверкой EndIso
            // чтением строк — день-гигант в буфере стоил секунд при каждом открытии «Дня»/«Недели»
            // (замер на 10 млн: 3.15 с → 7 мс). Порядок LIMIT'а — по EndIso: его отдает сам индекс.
            foreach (var colId in timedCols)
            {
                var span = spans.Known ? spans.Max(colId, false) : TimeSpan.FromDays(CalendarTimedBufferDays);
                var (band, args) = NearEdge(span, rangeStart, rangeStart);
                ReadCalendarRows(conn, result,
                    $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                    " AND ci.EndIso >= @start AND ci.StartIso < @start" + band,
                    [("@start", startIso), .. args], perDayCap, "ci.EndIso", desc(colId));
            }

            // All-day/многодневные — сик по EndIso-индексу: многодневное могло начаться задолго до
            // окна, поэтому StartIso-скан шел бы от НАЧАЛА таймлайна — миллионы прошлых all-day
            // (импорт дат «без времени») стоили секунды на каждое окно после них (замер: 2.2 с →
            // 0 мс). EndIso >= start отсекает прошлое сиком, граница сверху — конец окна + M
            // all-day коллекции (без сводки — все будущее). Cap тем же LIMIT: сетке и панели
            // «весь день» больше все равно не отрисовать.
            var allDayCols = colIds.Where(c => spans.Has(c, true)).ToList();
            if (allDayPerDay is not { } dayCap)
            {
                foreach (var colId in allDayCols)
                {
                    var (band, args) = NearEdge(spans.Max(colId, true), rangeStart, rangeEnd);
                    ReadCalendarRows(conn, result,
                        $"ci.CollectionId = {colId} AND ci.AllDay = 1{hidden}" +
                        " AND ci.EndIso >= @start AND ci.StartIso < @end" + band,
                        [("@start", startIso), ("@end", endIso), .. args], allDayCap ?? perDayCap, "ci.EndIso", desc(colId));
                }
            }
            else
            {
                // По дням окна, как timed: каждый день получает свои записи, сколько бы их ни было
                // в соседних. Многодневные, начатые в дне, — отдельным проходом по концам (в
                // переполненном дне начало у всех одно, и cap отдал бы однодневные вперемешку с ними).
                static long Key(CalendarRecord r) => ((long)(r.Id ?? 0) << 32) | (uint)r.FieldId;
                var seen = new HashSet<long>();
                var allDay = new List<CalendarRecord>();
                for (var day = rangeStart.Date; day < rangeEnd; day = day.AddDays(1))
                {
                    var dayIso  = Iso(day);
                    var nextIso = Iso(day.AddDays(1));
                    foreach (var colId in allDayCols)
                    {
                        rows.Read(allDay,
                            $"ci.CollectionId = {colId} AND ci.AllDay = 1{hidden}" +
                            " AND ci.StartIso >= @d AND ci.StartIso < @d2",
                            [("@d", dayIso), ("@d2", nextIso)], dayCap, desc: desc(colId));
                        var (band, args) = NearEdge(spans.Max(colId, true), day, day.AddDays(1));
                        rows.Read(allDay,
                            $"ci.CollectionId = {colId} AND ci.AllDay = 1{hidden}" +
                            " AND ci.EndIso >= @d2 AND ci.StartIso >= @d AND ci.StartIso < @d2" + band,
                            [("@d", dayIso), ("@d2", nextIso), .. args], dayCap, "ci.EndIso", desc(colId));
                    }
                }

                // Начатые до окна и пересекающие его — полоса у начала окна по EndIso-индексу.
                foreach (var colId in allDayCols)
                {
                    var (band, args) = NearEdge(spans.Max(colId, true), rangeStart, rangeStart);
                    ReadCalendarRows(conn, allDay,
                        $"ci.CollectionId = {colId} AND ci.AllDay = 1{hidden}" +
                        " AND ci.EndIso >= @start AND ci.StartIso < @start" + band,
                        [("@start", startIso), .. args], allDayCap ?? perDayCap, "ci.EndIso", desc(colId));
                }
                foreach (var r in allDay) if (seen.Add(Key(r))) result.Add(r);
            }
        }
        finally { if (opened) conn.Close(); }

        result.Sort((a, b) => a.StartDt.CompareTo(b.StartDt));
        return result;
    }

    /// <summary>Фоновая (изолированное подключение) загрузка окна сетки: только capped-события,
    /// индексными сиками — миллисекунды на любом окне. Карта счетчиков дней («+N еще»
    /// Месяца/Года) сюда сознательно НЕ входит: ее GROUP BY по окну с днем-гигантом — секунды,
    /// поэтому контроллер грузит ее ВТОРОЙ фазой (<see cref="LoadCalendarDayCountsAsync"/>)
    /// и тихо уточняет уже показанную сетку.</summary>
    public Task<List<CalendarRecord>> LoadCalendarGridRangeAsync(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perDayCap, int? allDayCap = null,
        int? multiDayPerDay = null, int? allDayPerDay = null) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx)
                .GetCalendarRecordsByRangeCapped(rangeStart, rangeEnd, activeIds, perDayCap, allDayCap, multiDayPerDay,
                                                 allDayPerDay);
        });

    /// <summary>
    /// МНОГОДНЕВНЫЕ события окна (день конца ≠ дню начала), пересекающие его. Для вида «Год»,
    /// который сами события окна не читает (сотни тысяч строк на год): числам достаточно карты
    /// счетчиков, но карта считает событие по дню НАЧАЛА — многодневное иначе подсвечивало бы
    /// один день из своего диапазона вместо всех. Таких событий мало (это «проекты», а не
    /// рядовые встречи), поэтому их можно прочитать целиком и разложить по дням в виде.
    /// Форма — как у all-day-прохода сетки: сик по EndIso-индексу (у StartIso нижней границы
    /// нет — многодневное могло начаться сколь угодно давно), многодневность — residual по
    /// дате из того же индекса. Cap на коллекцию: подсветка чисел от лишних не изменится.
    /// </summary>
    public List<CalendarRecord> GetCalendarSpanningRecords(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perCollectionCap)
    {
        var fields = ReadCalendarFields(withCollections: activeIds is null);
        var colIds = fields.Readable(activeIds);
        var hidden = fields.HiddenClause("ci.");
        var desc   = fields.Description;

        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);
        const string spans = " AND substr(ci.EndIso, 1, 10) <> substr(ci.StartIso, 1, 10)";

        var result = new List<CalendarRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            // Полоса у окна шириной наибольшей длительности вида (сводка длительностей): конец
            // многодневного — не позже конца окна + M, без сводки — все будущее коллекции.
            var bounds = ReadCalendarSpans(conn);
            foreach (var colId in colIds)
                foreach (var allDay in new[] { false, true })
                {
                    if (!bounds.Has(colId, allDay)) continue;
                    var (band, args) = NearEdge(bounds.Max(colId, allDay), rangeStart, rangeEnd);
                    ReadCalendarRows(conn, result,
                        $"ci.CollectionId = {colId} AND ci.AllDay = {(allDay ? 1 : 0)}{hidden}" +
                        " AND ci.EndIso >= @start AND ci.StartIso < @end" + spans + band,
                        [("@start", startIso), ("@end", endIso), .. args], perCollectionCap, "ci.EndIso", desc(colId));
                }
        }
        finally { if (opened) conn.Close(); }

        result.Sort((a, b) => a.StartDt.CompareTo(b.StartDt));
        return result;
    }

    /// <summary>Фоновая (изолированное подключение) версия
    /// <see cref="GetCalendarSpanningRecords"/> — как и карта счетчиков, не на UI-потоке.</summary>
    public Task<List<CalendarRecord>> LoadCalendarSpanningAsync(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perCollectionCap) =>
        Task.Run(() =>
        {
            using var ctx = new AppDbContext(db.DbPath);
            ctx.ApplyPerformancePragmas();
            return new ConstructorRepository(ctx)
                .GetCalendarSpanningRecords(rangeStart, rangeEnd, activeIds, perCollectionCap);
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
    /// (timed в окне / timed-спилловеры / all-day) НА КОЛЛЕКЦИЮ — Диаграмма все равно рисует
    /// максимум ~200 строк на группу, а ее окно может быть годом или десятилетием.
    /// Per-day чтение сеток (<see cref="GetCalendarRecordsByRangeCapped"/>) здесь не годится:
    /// на 10-летнем окне это 3653 дня × коллекции запросов и до perDayCap строк с КАЖДОГО
    /// дня — десятки секунд и миллионы строк в памяти при том, что показана будет тысяча.
    /// Здесь — 3 индексных сика с LIMIT на коллекцию: память и время не зависят от длины окна
    /// и объема БД. Какие именно события попали под cap — «первые» в порядке своего индекса
    /// (по StartIso / EndIso); точные счетчики и охват групп считает <see cref="GetDiagramStats"/>.
    /// </summary>
    public List<CalendarRecord> GetDiagramRecordsCapped(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds, int perCollectionCap)
    {
        var fields = ReadCalendarFields(withCollections: activeIds is null);
        var colIds = fields.Readable(activeIds);
        var hidden = fields.HiddenClause("ci.");
        var desc   = fields.Description;

        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);

        var result = new List<CalendarRecord>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            var spans = ReadCalendarSpans(conn);
            foreach (var colId in colIds)
            {
                if (spans.Has(colId, false))
                {
                    // Timed, начавшиеся в окне: порядок отдает индекс (CollectionId, AllDay,
                    // StartIso), residual-фильтра нет (EndIso ≥ StartIso ≥ start автоматически).
                    ReadCalendarRows(conn, result,
                        $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                        " AND ci.StartIso >= @start AND ci.StartIso < @end",
                        [("@start", startIso), ("@end", endIso)], perCollectionCap, desc: desc(colId));

                    // Timed-спилловеры (начались ДО окна и пересекают его) — сик по EndIso-индексу
                    // в полосе шириной наибольшей длительности timed коллекции (сводка
                    // длительностей), residual StartIso лежит в том же индексе. Без сводки — без
                    // границы: буфер сеток (CalendarTimedBufferDays) держится на допущении «timed
                    // длиннее двух месяцев не бывает», а в Диаграмме окно — до 10 лет, и
                    // растянутое мышью timed-событие на полгода терялось в КАЖДОМ окне, которое
                    // оно лишь пересекает (см. CalendarWindowTests).
                    var (band, args) = NearEdge(spans.Max(colId, false), rangeStart, rangeStart);
                    ReadCalendarRows(conn, result,
                        $"ci.CollectionId = {colId} AND ci.AllDay = 0{hidden}" +
                        " AND ci.EndIso >= @start AND ci.StartIso < @start" + band,
                        [("@start", startIso), .. args], perCollectionCap, "ci.EndIso", desc(colId));
                }

                // All-day/многодневные — сик по EndIso-индексу, сверху — конец окна + M all-day.
                if (spans.Has(colId, true))
                {
                    var (band, args) = NearEdge(spans.Max(colId, true), rangeStart, rangeEnd);
                    ReadCalendarRows(conn, result,
                        $"ci.CollectionId = {colId} AND ci.AllDay = 1{hidden}" +
                        " AND ci.EndIso >= @start AND ci.StartIso < @end" + band,
                        [("@start", startIso), ("@end", endIso), .. args], perCollectionCap, "ci.EndIso", desc(colId));
                }
            }
        }
        finally { if (opened) conn.Close(); }

        result.Sort((a, b) => a.StartDt.CompareTo(b.StartDt));
        return result;
    }

    /// <summary>
    /// Точные агрегаты окна Диаграммы по коллекциям: число событий, min(начало), max(конец)
    /// по классам timed/all-day — для счетчиков групп, «+N еще» и суммарных баров, которые
    /// должны считаться по ВСЕМ событиям окна, а не по обрезанным cap'ом. Начавшиеся в окне
    /// считает сводка дней (полный день — строка на поле), начавшиеся до окна, all-day и самый
    /// поздний конец — покрывающие сканы EndIso-индекса в полосе шириной наибольшей длительности
    /// вида у границы окна (сводка длительностей); первое начало в окне — сик. Цена не зависит от
    /// объема коллекции и длины окна (прежние сканы шли по всему будущему коллекции: на 10 млн
    /// событий — 1 с на окно). Контроллер зовет это ФОНОВОЙ фазой 2 (как карту счетчиков
    /// Месяца/Года) и тихо уточняет уже показанное.
    /// </summary>
    public Dictionary<int, DiagramGroupStat> GetDiagramStats(
        DateTime rangeStart, DateTime rangeEnd, ISet<int>? activeIds)
    {
        var fields = ReadCalendarFields(withCollections: activeIds is null);
        var colIds = fields.Readable(activeIds);
        var hidden = fields.HiddenClause();

        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        var startIso = Iso(rangeStart);
        var endIso   = Iso(rangeEnd);

        var result = new Dictionary<int, DiagramGroupStat>();
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) conn.Open();
        try
        {
            var spans = ReadCalendarSpans(conn);
            foreach (var colId in colIds)
            {
                var hasTimed  = spans.Has(colId, false);
                var hasAllDay = spans.Has(colId, true);
                if (!hasTimed && !hasAllDay) continue;

                // Одна строка агрегатов по условию над calendar_index коллекции.
                object?[] Row(string select, string where, IEnumerable<(string Name, string Value)> args)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT {select} FROM calendar_index WHERE CollectionId = {colId}{where}{hidden}";
                    foreach (var (name, value) in args) AddParam(cmd, name).Value = value;
                    using var rdr = cmd.ExecuteReader();
                    var row = new object?[rdr.FieldCount];
                    if (rdr.Read())
                        for (var i = 0; i < row.Length; i++) row[i] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
                    return row;
                }
                static int Int(object? v) => v is null ? 0 : Convert.ToInt32(v);
                static DateTime? Date(object? v) => v is string s && DateTime.TryParse(s, out var d) ? d : null;

                // Начавшиеся в окне — оба вида: полные дни из сводки дней, неполные края — по индексу.
                var days = new SortedDictionary<string, int>(StringComparer.Ordinal);
                FillDayCounts(conn, [colId], hidden, rangeStart, rangeEnd, days);
                var startsInWindow = days.Values.Sum();

                // All-day, пересекающие окно: число, первое начало, последний конец и сколько из них
                // начались в окне — одним сканом EndIso-диапазона (residual StartIso — в индексе).
                int aCount = 0, aInWindow = 0;
                DateTime? aMin = null, aMax = null;
                if (hasAllDay)
                {
                    var (band, args) = NearEdge(spans.Max(colId, true), rangeStart, rangeEnd, "");
                    var r = Row("COUNT(*), MIN(StartIso), MAX(EndIso), SUM(CASE WHEN StartIso >= @start THEN 1 ELSE 0 END)",
                                " AND AllDay = 1 AND EndIso >= @start AND StartIso < @end" + band,
                                [("@start", startIso), ("@end", endIso), .. args]);
                    (aCount, aMin, aMax, aInWindow) = (Int(r[0]), Date(r[1]), Date(r[2]), Int(r[3]));
                }

                int tSpill = 0;
                DateTime? tMin = null, tMaxEnd = null;
                if (hasTimed)
                {
                    var span = spans.Max(colId, false);

                    // Timed, начавшиеся ДО окна и пересекающие его: полоса у начала окна.
                    var (band, args) = NearEdge(span, rangeStart, rangeStart, "");
                    var r = Row("COUNT(*), MIN(StartIso)",
                                " AND AllDay = 0 AND EndIso >= @start AND StartIso < @start" + band,
                                [("@start", startIso), .. args]);
                    (tSpill, tMin) = (Int(r[0]), Date(r[1]));

                    // Первое начало timed в окне — сик (MIN — единственный агрегат, SQLite берет
                    // первую строку диапазона индекса); нужно, только если до окна timed не начинались.
                    if (tSpill == 0)
                        tMin = Date(Row("MIN(StartIso)", " AND AllDay = 0 AND StartIso >= @start AND StartIso < @end",
                                        [("@start", startIso), ("@end", endIso)])[0]);

                    // Самый поздний конец timed окна: обратный сик по EndIso-индексу от конца окна + M
                    // до первой строки, начавшейся раньше конца окна. MAX — единственный агрегат:
                    // COUNT рядом заставил бы сканировать весь диапазон.
                    var (endBand, endArgs) = NearEdge(span, rangeStart, rangeEnd, "");
                    tMaxEnd = Date(Row("MAX(EndIso)", " AND AllDay = 0 AND EndIso >= @start AND StartIso < @end" + endBand,
                                       [("@start", startIso), ("@end", endIso), .. endArgs])[0]);
                }

                // Все начавшиеся в окне (сводка) без all-day из них — это timed окна.
                var count = (startsInWindow - aInWindow) + tSpill + aCount;
                if (count <= 0) continue;
                var mins = new[] { tMin, aMin }.Where(d => d.HasValue).ToList();
                result[colId] = new DiagramGroupStat
                {
                    Count        = count,
                    AllDayCount  = aCount,
                    MinStart     = mins.Count > 0 ? mins.Min()!.Value : rangeStart,
                    MaxEndTimed  = tMaxEnd,
                    MaxEndAllDay = aMax,
                };
            }
        }
        finally { if (opened) conn.Close(); }
        return result;
    }

    // Полоса у границы окна для событий, пересекающих ее: наибольшая длительность вида M (сводка
    // длительностей) ограничивает конец сверху (edge + M) и начало снизу (from − M). null — без
    // границы (сводки нет или длительность не разобрана): условие не добавляется.
    private static (string Sql, (string Name, string Value)[] Args) NearEdge(TimeSpan? span, DateTime from, DateTime edge,
                                                                             string prefix = "ci.")
    {
        if (span is not { } m) return ("", []);
        string Iso(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Unspecified).ToString("o");
        return ($" AND {prefix}EndIso <= @spanHi AND {prefix}StartIso >= @spanLo",
                [("@spanHi", Iso(SafeAdd(edge, m))), ("@spanLo", Iso(SafeAdd(from, -m)))]);
    }

    // Дата строки проекции: пишется форматом «o» без зоны (2026-09-21T00:31:12.0000000), и точный
    // разбор этого формата в разы дешевле общего (на неделе в девять тысяч событий общий разбор
    // стоил десятки мс); иной вид строки — общим разбором, как раньше.
    private static bool TryParseIso(string s, out DateTime value) =>
        DateTime.TryParseExact(s, "O", System.Globalization.CultureInfo.InvariantCulture,
                               System.Globalization.DateTimeStyles.None, out value)
        || DateTime.TryParse(s, out value);

    // Общий читатель окна: строки calendar_index (+заголовок записи и цвет коллекции) по условию
    // WHERE над ci. limit > 0 добавляет ORDER BY {orderBy} LIMIT n — чтобы порядок отдал индекс
    // и LIMIT остановил скан, условие обязано фиксировать ci.CollectionId и ci.AllDay одним
    // значением, а orderBy — совпадать с range-колонкой использованного индекса
    // (см. GetCalendarRecordsByRangeCapped).
    private static void ReadCalendarRows(System.Data.Common.DbConnection conn, List<CalendarRecord> into,
                                         string where, IReadOnlyList<(string Name, string Value)> args,
                                         int limit = 0, string orderBy = "ci.StartIso", string? desc = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = CalendarRowsSql(where, limit, orderBy, desc);
        foreach (var (name, value) in args)
            AddParam(cmd, name).Value = value;
        ReadCalendarRows(cmd, into);
    }

    private static string CalendarRowsSql(string where, int limit, string orderBy, string? desc) =>
        $@"SELECT ci.RecordId, ci.FieldId, ci.CollectionId, ci.StartIso, ci.EndIso, ci.AllDay,
                  r.Title, {desc ?? DescExpr}, c.Name, c.Color
           FROM calendar_index ci
           JOIN db_records r ON r.Id = ci.RecordId
           JOIN db_collections c ON c.Id = r.CollectionId
           WHERE {where}"
        + (limit > 0 ? $" ORDER BY {orderBy} LIMIT {limit}" : "");

    // Тот же читатель для серии запросов, различающихся только значениями параметров (дни окна):
    // команда на каждый текст запроса создается и готовится один раз, дальше меняются значения.
    private sealed class CalendarRowReader(System.Data.Common.DbConnection conn) : IDisposable
    {
        private readonly Dictionary<string, System.Data.Common.DbCommand> _commands = new(StringComparer.Ordinal);

        public void Read(List<CalendarRecord> into, string where, IReadOnlyList<(string Name, string Value)> args,
                         int limit = 0, string orderBy = "ci.StartIso", string? desc = null)
        {
            var sql = CalendarRowsSql(where, limit, orderBy, desc);
            if (!_commands.TryGetValue(sql, out var cmd))
            {
                cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, _) in args) AddParam(cmd, name);
                _commands[sql] = cmd;
            }
            foreach (var (name, value) in args) cmd.Parameters[name].Value = value;
            ReadCalendarRows(cmd, into);
        }

        public void Dispose()
        {
            foreach (var cmd in _commands.Values) cmd.Dispose();
        }
    }

    private static void ReadCalendarRows(System.Data.Common.DbCommand cmd, List<CalendarRecord> into)
    {
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            if (!TryParseIso(rdr.GetString(3), out var s)) continue;
            if (!TryParseIso(rdr.GetString(4), out var e)) continue;
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
                // (SQLite ≥3.24 и PG) и не пересоздает строку (REPLACE = DELETE+INSERT с каскадами).
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

    // ── Поле «Описание» ───────────────────────────────────────────────────────

    /// <summary>
    /// Id поля с ролью «Описание» этой коллекции (null — поля нет). Роль ищется ПО ФЛАГУ,
    /// а не по имени: поле переименовывается как любое другое. Двух ролей быть не должно,
    /// но при копировании коллекций берем первое по (Position, Id) — как остальные роли.
    /// </summary>
    public int? DescriptionFieldId(int collectionId) =>
        db.DbFields.AsNoTracking()
          .Where(f => f.CollectionId == collectionId && f.IsDescription)
          .OrderBy(f => f.Position).ThenBy(f => f.Id)
          .Select(f => (int?)f.Id)
          .FirstOrDefault();

    /// <summary>
    /// Гарантирует поле «Описание» (тип «Текст») в коллекции и возвращает его Id. Заводят его
    /// только два пути — создание записи через календарь и через задачу; в остальных местах
    /// коллекция живет без описания, пока пользователь не добавит поле сам. Поле обычное
    /// (IsSystem = false): его можно переименовать, сменить тип и удалить.
    /// <paramref name="adoptByName"/> = true — одноименный столбец получает роль (данные из
    /// него поедут в панель записи), false — он остается нетронутым, а поле заводится свое
    /// с суффиксом « 2» (ответ пользователя в окне подтверждения).
    /// </summary>
    public int EnsureDescriptionField(int collectionId, bool adoptByName = true)
    {
        EnsureRoles(collectionId, [FieldRole.Description], adoptByName);
        return DescriptionFieldId(collectionId)
               ?? throw new InvalidOperationException("Не удалось создать поле «Описание»");
    }

    // ── Вспомогательные методы для CalendarRecord ─────────────────────────────

    private int GetOrCreateDefaultCollection()
    {
        var col = db.DbCollections.FirstOrDefault(c => c.Name == "Без названия");
        if (col is not null) return col.Id;

        // Создание — только с правом на коллекции. Спрашиваем именно здесь и только в ветке
        // создания: вызывающие (AddTaskRecord/AddCalendarRecord с collection = null) проверили
        // лишь CreateRecord, и без этой строки «Без названия» заводилась в обход прав. Когда
        // коллекция уже есть, права на нее не нужны — там достаточно CreateRecord.
        Demand(PermAction.CreateCollection);

        var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        col = new DbCollectionEntity { Name = "Без названия", Position = maxPos + 1000, Color = "#007AFF" };
        db.DbCollections.Add(col);
        db.SaveChanges();

        EnsureRoles(col.Id, [FieldRole.CalendarDate], adoptByName: true);
        return col.Id;
    }

    // Поле-дата, в которое класть дату события. Если в окне явно выбрано поле
    // (preferredFieldId — date-поле этой коллекции), пишем в него; иначе фолбэк на
    // EnsureCalendarDateField (видимое поле, первое существующее или авто-«Дата»).
    private int ResolveCalendarDateField(int collectionId, int preferredFieldId)
    {
        if (preferredFieldId > 0)
        {
            var f = db.DbFields.FirstOrDefault(x =>
                x.Id == preferredFieldId && x.CollectionId == collectionId && x.FieldType == "date");
            // Выбрать в окне события можно и выключенное поле-дату — тогда тумблер поднимаем
            // мы: человек прямо указал, куда класть дату события, и ожидает увидеть его в
            // календаре, а не гадать, почему запись создалась и пропала.
            if (f is not null) { ShowDateFieldInCalendar(f); return f.Id; }
        }
        return EnsureCalendarDateField(collectionId);
    }

    // id поля-источника календаря коллекции: включенное поле-дата, иначе первое существующее
    // (его и включаем), иначе роль «Дата» (одноименный столбец получает флаг либо заводится
    // системное «Дата»). Фолбэк, когда явное поле не выбрано.
    private int EnsureCalendarDateField(int collectionId, bool adoptByName = true)
    {
        if (CalendarDateFieldId(collectionId) is int source) return source;

        // Источника нет. Раньше здесь сразу доводилась роль — и коллекция с выключенным
        // «Дедлайном» получала ВТОРОЕ поле-дату: роль ищет столбец по имени «Дата», а
        // «Дедлайн» под это имя не подходит. Теперь сперва включаем первое существующее
        // поле-дату (порядок RoleOrder — как у всех ролей): дату человек завел сам, событие
        // создает через календарь — показать надо именно ее, а не плодить дубль.
        var existing = db.DbFields
            .Where(f => f.CollectionId == collectionId && f.FieldType == "date")
            .OrderBy(f => f.Position).ThenBy(f => f.Id)
            .FirstOrDefault();
        if (existing is not null) { ShowDateFieldInCalendar(existing); return existing.Id; }

        // Дат в коллекции нет вовсе — доводим роль: добавление события через календарь
        // должно показать запись в календаре.
        EnsureRoles(collectionId, [FieldRole.CalendarDate], adoptByName);
        return CalendarDateFieldId(collectionId)
               ?? throw new InvalidOperationException("Не удалось создать поле «Дата»");
    }

    // Поднимает тумблер показа в календаре у поля-даты. ЕДИНСТВЕННОЕ место, где флаг
    // включается не рукой пользователя: запись заводят ИЗ календаря, и не показать ее там —
    // значит потерять ее из виду сразу после создания. Во всех остальных случаях (создание
    // свойства, перенос записи, импорт) дата остается обычной колонкой, пока человек сам не
    // включит тумблер в свойствах или чеклистом коллекций в сайдбаре.
    private void ShowDateFieldInCalendar(DbFieldEntity field)
    {
        if (field.IsCalStart) return;
        Demand(PermAction.EditField, field.CollectionId);
        field.IsCalStart = true;
        Log("update", "field", field.Name, CollectionNameOf(field.CollectionId));
        db.SaveChanges();
    }

    /// <summary>Есть ли у коллекции видимое поле-дата (участвует ли она в календаре) — для плашки
    /// «привязано к разделу»: без источника дат календарь коллекцию не показывает, и заявлять
    /// привязку к нему не о чем.</summary>
    public bool HasCalendarDateField(int collectionId) => CalendarDateFieldId(collectionId) is not null;

    /// <summary>
    /// Есть ли в базе хоть одно поле-дата, показываемое в календаре. Тумблер снят по умолчанию,
    /// поэтому «пустой календарь» — обычное состояние новой базы, а не поломка: раздел показывает
    /// подсказку, где этот тумблер искать (см. пустое состояние CalendarControllerView).
    /// </summary>
    public bool HasAnyCalendarDateField() =>
        db.DbFields.Any(f => f.FieldType == "date" && f.IsCalStart);

    /// <summary>
    /// Коллекции, у которых включено хотя бы одно поле-дата, — те, что участвуют в календаре.
    /// Ими боковая панель календаря ограничивает список, когда включена настройка
    /// «Только коллекции с датами в календаре». Один запрос на базу.
    /// </summary>
    public HashSet<int> GetCalendarCollectionIds() =>
        db.DbFields.AsNoTracking()
          .Where(f => f.FieldType == "date" && f.IsCalStart)
          .Select(f => f.CollectionId)
          .Distinct()
          .ToHashSet();

    /// <summary>
    /// Подключить коллекцию к календарю — зеркало <see cref="EnsureTaskCollection"/>: включить
    /// первое поле-дату либо завести «Дата» с показом в календаре. Зовет боковая панель, когда
    /// коллекцию создают в ней при настройке «Только коллекции с датами в календаре»: иначе
    /// новая коллекция пропала бы из списка в момент создания. Возвращает id поля-источника.
    /// </summary>
    public int EnsureCalendarCollection(int collectionId) => EnsureCalendarDateField(collectionId);

    // Первое (по Position, Id — общее правило ролей) поле-дата коллекции, видимое в календаре.
    private int? CalendarDateFieldId(int collectionId) =>
        db.DbFields.Where(f => f.CollectionId == collectionId && f.FieldType == "date" && f.IsCalStart)
          .OrderBy(f => f.Position).ThenBy(f => f.Id)
          .Select(f => (int?)f.Id)
          .FirstOrDefault();

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
    ///    чтобы такие записи появились в календаре. Идемпотентно и дешево.
    ///
    /// Тумблер «Отображать в календаре» у полей пользователя здесь не трогается: раньше
    /// коллекции, где ни одна дата не включена, получали включенной первую дату при КАЖДОМ
    /// подключении — новая дата (свойство, импорт, свод трудоемкости) снова оказывалась в
    /// календаре после перезапуска, а выключить единственную дату коллекции было нельзя.
    /// Текстовое поле пользователя с именем «Дата» тоже остается текстом: переводится только
    /// системное.
    /// </summary>
    /// <param name="progress">Прогресс тяжелой фазы (первое открытие старой базы): (обработано,
    /// всего) строк-значений дат; (0, 0) — фаза началась, объем еще не посчитан. Быстрый путь
    /// (индекс уже построен) колбэк не дергает — на обычных запусках этап не мелькает.</param>
    public void MigrateAndBackfillCalendarDates(Action<long, long>? progress = null)
    {
        var legacy = db.DbFields.Where(f => f.Name == FieldDate && f.FieldType == "text" && f.IsSystem).ToList();
        foreach (var f in legacy)
        {
            f.FieldType = "date";
            var hasOther = db.DbFields.Any(o =>
                o.CollectionId == f.CollectionId && o.Id != f.Id && o.FieldType == "date" && o.IsCalStart);
            if (!hasOther) f.IsCalStart = true;
        }
        if (legacy.Count > 0) db.SaveChanges();

        var calFieldIds = db.DbFields
            .Where(f => f.FieldType == "date" && f.IsCalStart)
            .Select(f => f.Id)
            .ToList();
        if (calFieldIds.Count == 0) return;

        // Индекс календаря уже построен → БД мигрирована, а новые данные (создание/импорт/
        // правка) поддерживают StartDt и индекс инкрементально. Значит недобэкафилленных
        // записей нет — пропускаем тяжелый скан db_field_values на каждом монтировании
        // (он нужен ровно один раз, при первом открытии старой БД). Ускоряет подключение
        // больших БД.
        if (db.CalendarIndex.Any()) return;

        progress?.Invoke(0, 0);   // тяжелая фаза началась (скан значений; объем еще не посчитан)

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
    // асинхронно): бэкафилл идет в фоне старта, колбэк должен дергаться синхронно и по порядку.
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
