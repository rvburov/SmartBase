using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Снимок времени общий для пакета: соседние строки не получают разные границы расчета.
    internal Func<DateTime> DatePeriodClock { get; set; } = () => DateTime.Now;

    private sealed class DatePeriodCreations
    {
        internal readonly HashSet<DbRecordEntity> Records = [];
        internal readonly HashSet<int> RawIds = [];
    }
    private static readonly ConditionalWeakTable<AppDbContext, DatePeriodCreations> PendingDatePeriods = new();

    internal static void NoteDatePeriodCreation(AppDbContext context, DbRecordEntity record) =>
        PendingDatePeriods.GetValue(context, _ => new()).Records.Add(record);

    private void ForgetDatePeriodCreation(DbRecordEntity record)
    {
        if (PendingDatePeriods.TryGetValue(db, out var pending)) pending.Records.Remove(record);
    }

    private void NoteDatePeriodCreation(IEnumerable<int> ids) =>
        PendingDatePeriods.GetValue(db, _ => new()).RawIds.UnionWith(ids);

    internal static void DatePeriodTransactionStarted(AppDbContext context)
    {
        if (!PendingDatePeriods.TryGetValue(context, out var pending)) return;
        // Добавление перед неявной транзакцией EF еще не имеет постоянного ключа.
        pending.Records.RemoveWhere(r => r.Id > 0);
        pending.RawIds.Clear();
    }

    internal static void DatePeriodTransactionEnded(AppDbContext context)
    {
        if (!PendingDatePeriods.TryGetValue(context, out var pending)) return;
        pending.Records.Clear();
        pending.RawIds.Clear();
    }

    internal static void DatePeriodTransactionCommitting(AppDbContext context)
    {
        if (!PendingDatePeriods.TryGetValue(context, out var pending)) return;
        // В неявной транзакции CLR Id еще временный: EF переносит результат RETURNING
        // из sidecar в сущность только в AcceptAllChanges, уже после коммита.
        var ids = pending.RawIds.Concat(pending.Records.Select(r => context.Entry(r).Property(e => e.Id).CurrentValue))
            .Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return;
        var repo = new ConstructorRepository(context);
        foreach (var chunk in ids.Chunk(200)) repo.InitializeFixedDatePeriods(chunk);
    }

    private static Action CaptureDatePeriodCreations(AppDbContext context)
    {
        var records = PendingDatePeriods.TryGetValue(context, out var state) ? state.Records.ToArray() : [];
        var ids = state?.RawIds.ToArray() ?? [];
        return () =>
        {
            DatePeriodTransactionEnded(context);
            var pending = PendingDatePeriods.GetValue(context, _ => new());
            pending.Records.UnionWith(records);
            pending.RawIds.UnionWith(ids);
        };
    }

    private void ValidateDatePeriodField(DbField field)
    {
        if (field.FieldType != "date_period") return;
        var config = DatePeriodConfig.FromConfig(field.ConfigJson)
            ?? throw new InvalidOperationException("Настройте свойства-источники, дни недели и часы периода.");
        var wanted = config.EndFieldId is int end ? new[] { config.StartFieldId, end } : [config.StartFieldId];
        var valid = db.DbFields.AsNoTracking().Where(f => f.CollectionId == field.CollectionId
            && f.FieldType == "date" && Enumerable.Contains(wanted, f.Id)).Select(f => f.Id).ToHashSet();
        if (wanted.Any(id => !valid.Contains(id)))
            throw new InvalidOperationException("Выберите свойства типа «Дата» из этой коллекции.");
        // Вычисляемое число не становится источником календаря, задач или библиотеки.
        field.IsCalStart = field.IsCalEnd = field.IsTaskExec = field.IsTaskAssigner = field.IsTaskStatus
            = field.IsDocFile = field.IsDocType = field.IsDescription = false;
    }

    private DbField? DatePeriodField(int fieldId) => db.DbFields.AsNoTracking()
        .Where(f => f.Id == fieldId && f.FieldType == "date_period").ToList().Select(ToDbField).FirstOrDefault();

    // Тот же порядок, что у SetFieldValue: записи, затем метаданные свойств, затем значения.
    // Чтение настройки повторяется после блокировки: пока ожидали, сосед мог сменить тип.
    private void LockDatePeriodMetadata(IEnumerable<int> fieldIds)
    {
        if (!db.IsPostgres) return;
        foreach (var id in fieldIds.Distinct().Order())
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
            command.CommandText = "SELECT id FROM db_fields WHERE id = @id FOR SHARE";
            AddParam(command, "@id").Value = id;
            command.ExecuteScalar();
        }
    }

    private void LockDatePeriodSources(IEnumerable<DbField> fields) => LockDatePeriodMetadata(fields
        .Select(f => DatePeriodConfig.FromConfig(f.ConfigJson)).Where(c => c is not null)
        .SelectMany(c => c!.EndFieldId is int end ? new[] { c.StartFieldId, end } : [c.StartFieldId]));

    /// <summary>Только расчетные значения периода. Зависимости читаются и за пределами окна колонок.</summary>
    private Dictionary<int, Dictionary<int, string>> ReadDatePeriods(IReadOnlyCollection<int> recordIds,
        IReadOnlyCollection<int>? fieldIds = null, bool force = false, CancellationToken ct = default,
        IReadOnlyDictionary<int, Dictionary<int, string>>? loaded = null,
        IReadOnlyCollection<int>? loadedFieldIds = null)
    {
        var result = new Dictionary<int, Dictionary<int, string>>();
        if (recordIds.Count == 0 || fieldIds is { Count: 0 }) return result;
        var query = db.DbFields.AsNoTracking().Where(f => f.FieldType == "date_period");
        if (fieldIds is not null) query = query.Where(f => fieldIds.Contains(f.Id));
        else
        {
            var collections = db.DbRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id)).Select(r => r.CollectionId);
            query = query.Where(f => collections.Contains(f.CollectionId));
        }
        var fields = query.ToList().Select(ToDbField).ToList();
        if (fields.Count == 0) return result;
        var configs = fields.ToDictionary(f => f.Id!.Value, f => DatePeriodConfig.FromConfig(f.ConfigJson));
        var calculated = configs.Values.Where(c => c is not null && (c.Automatic || force)).ToArray();
        var dependencies = calculated.SelectMany(c =>
            c!.EndFieldId is int end ? new[] { c.StartFieldId, end } : [c.StartFieldId]).Distinct().ToArray();
        var sources = dependencies.Length == 0 ? new Dictionary<int, int>() : db.DbFields.AsNoTracking()
            .Where(f => Enumerable.Contains(dependencies, f.Id) && f.FieldType == "date")
            .Select(f => new { f.Id, f.CollectionId }).ToList().ToDictionary(f => f.Id, f => f.CollectionId);
        var calculators = configs.Where(pair => pair.Value is not null && (pair.Value.Automatic || force))
            .ToDictionary(pair => pair.Key, pair => DatePeriodValue.Prepare(pair.Value!));
        // Страница уже прочитала свои ячейки. Дочитываем только даты за пределами окна,
        // а сохраненный фиксированный результат не требует ни дат, ни текущего времени.
        var selected = dependencies.Concat(configs.Where(pair => pair.Value is { Automatic: false } && !force)
            .Select(pair => pair.Key)).Distinct()
            .Where(id => loaded is null || loadedFieldIds is not null && !loadedFieldIds.Contains(id)).ToArray();
        var now = calculated.Length > 0 ? DatePeriodClock() : default;
        // Автоматическое «сейчас» обновляется по минутам, как его показ и окно фильтра.
        // Иначе выбранные в фильтре 7200 секунд уже через секунду не совпали бы с ячейкой.
        var minuteNow = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, now.Kind);
        var byCollection = fields.GroupBy(field => field.CollectionId).ToDictionary(group => group.Key, group => group.ToArray());
        var visible = byCollection.Keys.ToDictionary(id => id, id => Permissions?.Can(PermAction.ViewCollection, id) != false);
        foreach (var chunk in recordIds.Distinct().Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            var records = db.DbRecords.AsNoTracking().Where(r => Enumerable.Contains(chunk, r.Id))
                .Select(r => new { r.Id, r.CollectionId }).ToList();
            var values = selected.Length == 0 ? new Dictionary<(int, int), string>() : db.DbFieldValues.AsNoTracking()
                .Where(v => Enumerable.Contains(chunk, v.RecordId) && Enumerable.Contains(selected, v.FieldId))
                .Select(v => new { v.RecordId, v.FieldId, v.Value }).ToList()
                .ToDictionary(v => (v.RecordId, v.FieldId), v => v.Value);
            string? SourceValue(int record, int field) => loaded is not null
                && (loadedFieldIds is null || loadedFieldIds.Contains(field))
                    ? loaded.GetValueOrDefault(record)?.GetValueOrDefault(field)
                    : values.GetValueOrDefault((record, field));
            foreach (var record in records)
            foreach (var field in byCollection.GetValueOrDefault(record.CollectionId) ?? [])
            {
                var id = field.Id!.Value;
                var value = "";
                var config = configs[id];
                if (visible[record.CollectionId] && config is not null)
                {
                    if (!config.Automatic && !force) value = SourceValue(record.Id, id) ?? "";
                    else if (sources.GetValueOrDefault(config.StartFieldId) == record.CollectionId
                        && (config.EndFieldId is null || sources.GetValueOrDefault(config.EndFieldId.Value) == record.CollectionId)
                        && DateValue.TryParse(SourceValue(record.Id, config.StartFieldId), out var start))
                    {
                        if (config.EndFieldId is null)
                            value = calculators[id].Calculate(start, null, config.Automatic ? minuteNow : now);
                        else if (DateValue.TryParse(SourceValue(record.Id, config.EndFieldId.Value), out var end))
                            value = calculators[id].Calculate(start, end, now);
                    }
                }
                if (!result.TryGetValue(record.Id, out var byField)) result[record.Id] = byField = [];
                byField[id] = value;
            }
        }
        return result;
    }

    private void FillDatePeriodValues(IReadOnlyCollection<int> recordIds,
        Dictionary<int, Dictionary<int, string>> values, IReadOnlyCollection<int>? fieldIds = null,
        bool allValuesLoaded = false)
    {
        foreach (var (recordId, periods) in ReadDatePeriods(recordIds, fieldIds, loaded: values,
                     loadedFieldIds: allValuesLoaded ? null : fieldIds))
        {
            if (!values.TryGetValue(recordId, out var byField)) values[recordId] = byField = [];
            foreach (var (fieldId, value) in periods) byField[fieldId] = value;
        }
    }

    public Task<Dictionary<int, Dictionary<int, string>>> ReadDatePeriodsAsync(IReadOnlyList<int> recordIds,
        IReadOnlyList<int>? fieldIds = null, CancellationToken ct = default)
    {
        var path = DbPath;
        var permissions = Permissions;
        var now = DatePeriodClock();
        var ids = recordIds.ToArray();
        var selected = fieldIds?.ToArray();
        return Task.Run(() =>
        {
            using var context = new AppDbContext(path);
            context.ApplyPerformancePragmas();
            return new ConstructorRepository(context) { Permissions = permissions, DatePeriodClock = () => now }
                .ReadDatePeriods(ids, selected, ct: ct);
        }, ct);
    }

    /// <summary>Зафиксировать периоды после всех начальных значений, в той же транзакции создания.</summary>
    private void InitializeFixedDatePeriods(IReadOnlyCollection<int> recordIds)
    {
        var collections = db.DbRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id))
            .Select(r => r.CollectionId).Distinct().ToList();
        var candidates = db.DbFields.AsNoTracking().Where(f => collections.Contains(f.CollectionId) && f.FieldType == "date_period")
            .Select(f => f.Id).ToList();
        if (candidates.Count == 0) return;
        LockDatePeriodMetadata(candidates);
        var fixedFields = db.DbFields.AsNoTracking().Where(f => candidates.Contains(f.Id) && f.FieldType == "date_period")
            .ToList().Select(ToDbField).Where(f => DatePeriodConfig.FromConfig(f.ConfigJson) is { Automatic: false }).ToList();
        LockDatePeriodSources(fixedFields);
        var fields = fixedFields.Select(f => f.Id!.Value).ToArray();
        if (fields.Length == 0) return;
        var values = ReadDatePeriods(recordIds, fields, force: true);
        foreach (var (record, byField) in values)
        foreach (var (field, value) in byField)
            // При неявной транзакции EF внешний SaveChanges еще не принял Added-сущности.
            // Вложенный SaveChanges повторно вставил бы их. SQL пишет только снимок, без
            // обхода трекера; существующий снимок копии коллекции не перезаписывается.
            db.Database.ExecuteSqlRaw(
                "INSERT INTO \"db_field_values\" (\"recordid\", \"fieldid\", \"value\") VALUES ({0}, {1}, {2}) " +
                "ON CONFLICT (\"recordid\", \"fieldid\") DO NOTHING", record, field, value);
    }

    public string RecalculateDatePeriod(int recordId, int fieldId)
    {
        RecalculateDatePeriods([recordId], [fieldId]);
        return GetFieldValues(recordId).GetValueOrDefault(fieldId, "");
    }

    public void RecalculateDatePeriods(IReadOnlyCollection<int> recordIds, IReadOnlyCollection<int>? fieldIds = null,
        CancellationToken ct = default)
    {
        if (recordIds.Count == 0) return;
        ExecuteAtomicWrite(() =>
        {
            ct.ThrowIfCancellationRequested();
            LockRecordsForWrite(recordIds);
            var records = db.DbRecords.Where(r => recordIds.Contains(r.Id)).ToList();
            foreach (var collection in records.Select(r => r.CollectionId).Distinct())
            {
                Demand(PermAction.ViewCollection, collection);
                Demand(PermAction.EditRecord, collection);
            }
            var collectionIds = records.Select(r => r.CollectionId).Distinct().ToList();
            var fieldQuery = db.DbFields.AsNoTracking().Where(f => f.FieldType == "date_period"
                && collectionIds.Contains(f.CollectionId));
            if (fieldIds is not null) fieldQuery = fieldQuery.Where(f => fieldIds.Contains(f.Id));
            var candidates = fieldQuery.Select(f => f.Id).ToList();
            LockDatePeriodMetadata(candidates);
            var fields = db.DbFields.AsNoTracking().Where(f => candidates.Contains(f.Id) && f.FieldType == "date_period")
                .ToList().Select(ToDbField)
                .Where(f => DatePeriodConfig.FromConfig(f.ConfigJson) is { Automatic: false }).ToList();
            LockDatePeriodSources(fields);
            foreach (var field in fields.Where(f => records.Any(r => r.CollectionId == f.CollectionId))) ValidateDatePeriodField(field);
            var selected = fields.Select(f => f.Id!.Value).ToArray();
            if (selected.Length == 0) return 0;
            var calculated = ReadDatePeriods(recordIds, selected, force: true, ct: ct);
            var old = db.DbFieldValues.AsNoTracking().Where(v => recordIds.Contains(v.RecordId) && Enumerable.Contains(selected, v.FieldId)).ToList()
                .ToDictionary(v => (v.RecordId, v.FieldId));
            var changed = new HashSet<int>();
            foreach (var (record, values) in calculated)
            foreach (var (field, value) in values)
            {
                ct.ThrowIfCancellationRequested();
                if (old.TryGetValue((record, field), out var existing) && existing.Value == value) continue;
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO \"db_field_values\" (\"recordid\", \"fieldid\", \"value\") VALUES ({0}, {1}, {2}) " +
                    "ON CONFLICT (\"recordid\", \"fieldid\") DO UPDATE SET \"value\" = EXCLUDED.\"value\"", record, field, value);
                if (db.DbFieldValues.Local.FirstOrDefault(v => v.RecordId == record && v.FieldId == field) is { } tracked)
                {
                    tracked.Value = value;
                    var entry = db.Entry(tracked);
                    entry.Property(v => v.Value).OriginalValue = value;
                    entry.Property(v => v.Value).IsModified = false;
                }
                changed.Add(record);
            }
            foreach (var record in records.Where(r => changed.Contains(r.Id))) TouchUpdated(record);
            foreach (var group in records.Where(r => changed.Contains(r.Id)).GroupBy(r => r.CollectionId))
                Log("update", "record", CollectionNameOf(group.Key) ?? "", CollectionNameOf(group.Key),
                    count: group.Count(), detail: "Пересчет периода времени");
            ct.ThrowIfCancellationRequested();
            db.SaveChanges();
            foreach (var collection in records.Where(r => changed.Contains(r.Id)).Select(r => r.CollectionId).Distinct())
                NotifyRelationDataChanged(collection);
            ct.ThrowIfCancellationRequested();
            return 0;
        });
    }

    public Task RecalculateDatePeriodsAsync(IReadOnlyList<int> recordIds, IReadOnlyList<int>? fieldIds = null,
        CancellationToken ct = default)
    {
        var path = DbPath;
        var permissions = Permissions;
        var now = DatePeriodClock();
        var ids = recordIds.ToArray();
        var selected = fieldIds?.ToArray();
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var context = new AppDbContext(path);
            context.ApplyPerformancePragmas();
            new ConstructorRepository(context) { Permissions = permissions, DatePeriodClock = () => now }
                .RecalculateDatePeriods(ids, selected, ct);
        }, ct);
    }

    private Dictionary<int, string> DatePeriodColumnValues(DbField field, IReadOnlyCollection<int> recordIds,
        CancellationToken ct = default) => ReadDatePeriods(recordIds, [field.Id!.Value], ct: ct)
        .ToDictionary(r => r.Key, r => r.Value.GetValueOrDefault(field.Id.Value, ""));

    private List<(string Value, int Count)> DatePeriodCounts(DbField field, IQueryable<DbRecordEntity> scope,
        CancellationToken ct = default) => DatePeriodCountsStream(field, scope, ct);

    private IQueryable<DbRecordEntity> DatePeriodFilter(IQueryable<DbRecordEntity> scope, DbField field,
        HashSet<string> selected, bool excluded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (selected.Count == 0) return excluded ? scope : scope.Where(_ => false);
        var config = DatePeriodConfig.FromConfig(field.ConfigJson);
        if (config is null || Permissions?.Can(PermAction.ViewCollection, field.CollectionId) == false)
            return selected.Contains("") != excluded ? scope : scope.Where(_ => false);
        if (!config.Automatic)
        {
            // Фиксированный период — уже сохраненное число: отбор целиком на сервере,
            // включая отсутствующий снимок и отбор «кроме», без чтения исходных дат.
            var fieldId = field.Id!.Value;
            var values = selected.ToList();
            var empty = selected.Contains("");
            var matching = db.DbFieldValues.Where(v => v.FieldId == fieldId && values.Contains(v.Value))
                .Select(v => v.RecordId);
            return excluded
                ? scope.Where(r => !matching.Contains(r.Id) && (!empty || db.DbFieldValues.Any(v =>
                    v.RecordId == r.Id && v.FieldId == fieldId && v.Value != "")))
                : scope.Where(r => matching.Contains(r.Id) || (empty && !db.DbFieldValues.Any(v =>
                    v.RecordId == r.Id && v.FieldId == fieldId && v.Value != "")));
        }
        var hits = new List<int>();
        var misses = new List<int>();
        foreach (var (id, value) in StreamDatePeriodColumn(field, scope, ct))
            (selected.Contains(value) != excluded ? hits : misses).Add(id);
        ct.ThrowIfCancellationRequested();
        // Возвращаем меньшую сторону: одинаковый период у миллионов строк не должен
        // превращаться в параметр со всеми их id, когда подошли все или почти все.
        if (misses.Count == 0) return scope;
        if (hits.Count == 0) return scope.Where(_ => false);
        return hits.Count <= misses.Count ? scope.Where(r => hits.Contains(r.Id))
                                         : scope.Where(r => !misses.Contains(r.Id));
    }

    private ColumnValueSnapshot DatePeriodSnapshot(int collectionId, string key, DbField field,
        Dictionary<string, HashSet<string>> others, bool excludeHidden, IQueryable<DbRecordEntity> scope,
        CancellationToken ct, ColumnValueSnapshot? reuse)
    {
        var scopeKey = SnapshotScope(collectionId, key, others, excludeHidden);
        if (DatePeriodConfig.FromConfig(field.ConfigJson) is { Automatic: true, EndFieldId: null })
            scopeKey += "\u0001minute=" + (DatePeriodClock().Ticks / TimeSpan.TicksPerMinute)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return reuse?.Scope == scopeKey ? reuse : new ColumnValueSnapshot(scopeKey, DatePeriodCounts(field, scope, ct));
    }

    private List<(int FieldId, string Value)> DatePeriodSourceChanges(int recordId, int editedFieldId)
    {
        var collection = CollectionOfRecord(recordId);
        var fields = db.DbFields.AsNoTracking().Where(f => f.CollectionId == collection && f.FieldType == "date_period")
            .Select(f => new { f.Id, f.ConfigJson }).ToList()
            .Where(f => DatePeriodConfig.FromConfig(f.ConfigJson) is { Automatic: true } cfg
                && (cfg.StartFieldId == editedFieldId || cfg.EndFieldId == editedFieldId)).Select(f => f.Id).ToArray();
        if (fields.Length == 0) return [];
        return ReadDatePeriods([recordId], fields).GetValueOrDefault(recordId, [])
            .Select(v => (v.Key, v.Value)).ToList();
    }

    private List<(int RecordId, int FieldId, string Value)> DatePeriodBulkSourceChanges(int collectionId, List<CellChange> changes)
    {
        var result = new List<(int, int, string)>();
        var byRecord = changes.Where(c => c.FieldId.HasValue).GroupBy(c => c.RecordId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.FieldId!.Value).ToHashSet());
        if (byRecord.Count == 0) return result;
        var fields = db.DbFields.AsNoTracking().Where(f => f.CollectionId == collectionId && f.FieldType == "date_period")
            .Select(f => new { f.Id, f.ConfigJson }).ToList()
            .Select(f => (f.Id, Config: DatePeriodConfig.FromConfig(f.ConfigJson)))
            .Where(f => f.Config is { Automatic: true }).ToDictionary(f => f.Id, f => f.Config!);
        if (fields.Count == 0) return result;
        var ids = byRecord.Keys.ToArray();
        foreach (var (record, values) in ReadDatePeriods(ids, fields.Keys.ToArray()))
        foreach (var (field, value) in values)
        {
            var config = fields[field];
            if (byRecord[record].Contains(config.StartFieldId)
                || config.EndFieldId is int end && byRecord[record].Contains(end)) result.Add((record, field, value));
        }
        return result;
    }
}
