using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    public Task<RelationIntegrityReport> ScanRelationIntegrityAsync(RelationIntegrityScanOptions? options = null,
        IProgress<RelationIntegrityProgress>? progress = null, CancellationToken ct = default)
    {
        var settings = new RelationIntegrityScanOptions(Math.Clamp(options?.PageSize ?? 500, 50, 2000),
            Math.Clamp(options?.MaxIssues ?? 2000, 1, 10000));
        var connections = CaptureIntegrityConnections();
        return Task.Run(() => WithIntegrityConnections(connections,
            repo => repo.ScanRelationIntegrityCore(settings, progress, ct)));
    }

    // В окне захватываются только адреса уже открытых баз. Scoped API без Workspace может
    // иметь прежние смешанные ребра, которых нет в нынешней настройке поля.
    private AutoConnections CaptureIntegrityConnections()
    {
        var ownId = RelationDbResolver.DbIdFor(this);
        var source = new AutoConnection(ownId, DbPath, Permissions);
        var targets = RelationDbResolver.HasScopedResolver ? new Dictionary<int, AutoConnection>()
            : RelationDbResolver.MountedReposExcept(ownId)
                .ToDictionary(p => p.DbId, p => new AutoConnection(p.DbId, p.Repo.DbPath, p.Repo.Permissions));
        if (ownId > 0) targets[ownId] = source;
        if (!RelationDbResolver.HasScopedResolver) return new(source, targets);
        var pending = new Queue<AutoConnection>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(source);
        while (pending.Count > 0 && visited.Count < 256)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current.Path)) continue;
            try
            {
                using var catalog = NewIntegrityContext(current.Path, false);
                var ids = catalog.DbRecordLinks.AsNoTracking().Where(e => e.TargetDbId > 0)
                    .Select(e => e.TargetDbId).Distinct().Take(256).ToList();
                ids.AddRange(catalog.DbFields.AsNoTracking().Where(f => f.FieldType == "relation")
                    .Select(f => f.ConfigJson).ToList().Select(json => StrictConfig(json, out _))
                    .Where(cfg => cfg?.TargetDbId > 0).Select(cfg => cfg!.TargetDbId));
                foreach (var id in ids.Distinct())
                    if (RelationDbResolver.MountedRepoFor(id) is { } repo)
                    {
                        var target = new AutoConnection(id, repo.DbPath, repo.Permissions);
                        targets[id] = target;
                        pending.Enqueue(target);
                    }
            }
            catch (Exception ex) when (ex is DbException or IOException) { /* скан вернет недоступность */ }
        }
        return new(source, targets);
    }

    private static AppDbContext NewIntegrityContext(string path, bool writable)
    {
        var context = new AppDbContext(path);
        if (!context.IsPostgres)
        {
            var settings = new SqliteConnectionStringBuilder(context.Database.GetConnectionString())
            { Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, Pooling = false };
            context.Database.SetConnectionString(settings.ConnectionString);
        }
        return context;
    }

    private static T WithIntegrityConnections<T>(AutoConnections connections,
        Func<ConstructorRepository, T> action, bool writable = false)
    {
        using var source = NewIntegrityContext(connections.Source.Path, writable);
        var repo = new ConstructorRepository(source) { Permissions = connections.Source.Permissions };
        var contexts = new List<AppDbContext>();
        var repositories = new Dictionary<int, ConstructorRepository>();
        ConstructorRepository? Resolve(int id)
        {
            if (id == connections.Source.DbId) return repo;
            if (repositories.TryGetValue(id, out var ready)) return ready;
            if (!connections.Targets.TryGetValue(id, out var target)) return null;
            if (target.Path == connections.Source.Path) return repo;
            var context = NewIntegrityContext(target.Path, writable);
            contexts.Add(context);
            return repositories[id] = new ConstructorRepository(context) { Permissions = target.Permissions };
        }
        using var resolver = RelationDbResolver.UseScopedResolver(Resolve, candidate => ReferenceEquals(candidate, repo)
            ? connections.Source.DbId : repositories.FirstOrDefault(p => ReferenceEquals(p.Value, candidate)).Key);
        try { return action(repo); }
        finally { foreach (var context in contexts) context.Dispose(); }
    }

    private RelationIntegrityReport ScanRelationIntegrityCore(RelationIntegrityScanOptions options,
        IProgress<RelationIntegrityProgress>? progress, CancellationToken ct, IntegrityLocks? locks = null)
    {
        var scan = new IntegrityScan(this, options, progress, ct, locks);
        try { return scan.Run(); }
        catch (OperationCanceledException) { return scan.Failure(RelationIntegrityStatus.Cancelled, "Проверка отменена. Данные не изменялись."); }
        catch (PermissionDeniedException) { return scan.Failure(RelationIntegrityStatus.NoAccess, "Нет доступа к проверяемым данным."); }
        catch (Exception ex) when (ex is DbException or IOException)
        { return scan.Failure(RelationIntegrityStatus.Unavailable, "База недоступна. Проверка не завершена, данные не изменялись."); }
        finally { scan.Dispose(); }
    }

    private sealed class IntegrityCatalog(ConstructorRepository owner,
        Dictionary<int, DbFieldEntity> fields, Dictionary<int, DbCollectionEntity> collections,
        PermissionService? permissions)
    {
        public ConstructorRepository Owner { get; } = owner;
        public Dictionary<int, DbFieldEntity> Fields { get; } = fields;
        public Dictionary<int, DbCollectionEntity> Collections { get; } = collections;
        public PermissionService? Permissions { get; } = permissions;
        public bool Can(PermAction action, int collection) => Permissions?.Can(action, collection) != false;
        public bool Admin => Permissions?.IsAdmin != false;
    }

    private sealed class IntegrityScan(ConstructorRepository owner, RelationIntegrityScanOptions options,
        IProgress<RelationIntegrityProgress>? progress, CancellationToken ct, IntegrityLocks? locks) : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly Dictionary<string, IntegrityCatalog?> _catalogs = new(StringComparer.Ordinal);
        private readonly List<RelationIntegrityIssue> _issues = [];
        private readonly Dictionary<RelationIntegrityIssueKind, long> _counts = [];
        private readonly Dictionary<int, RelationConfig?> _configs = [];
        private readonly string _identity = owner.RelationDatabaseIdentity;
        private readonly int _dbId = RelationDbResolver.DbIdFor(owner);
        private long _edges, _fields, _confirmed, _unverified;
        private int _cellField = -1, _cellRecord = -1;
        private readonly HashSet<(int Db, int Record)> _cellTargets = [];
        private readonly Dictionary<int, int> _cellNumericIds = [];
        private RelationIntegrityIssue? _cellSingle;
        private IntegrityCatalog _source = null!;

        public RelationIntegrityReport Run()
        {
            ct.ThrowIfCancellationRequested();
            _source = Catalog(owner)!;
            if (_source.Permissions?.AccessGranted == false || _source.Collections.Count > 0
                && !_source.Collections.Keys.Any(id => _source.Can(PermAction.ViewCollection, id)))
                return Failure(RelationIntegrityStatus.NoAccess, "Нет доступа к проверяемым данным.");
            foreach (var field in _source.Fields.Values.OrderBy(f => f.Id))
            {
                ct.ThrowIfCancellationRequested();
                if (!_source.Can(PermAction.ViewCollection, field.CollectionId)) continue;
                if (!_source.Collections.ContainsKey(field.CollectionId) && !_source.Admin) continue;
                if (field.FieldType is not ("relation" or "lookup" or "rollup")) continue;
                _fields++;
                CheckField(field);
            }
            int lastField = -1, lastRecord = -1, lastPosition = int.MinValue, lastId = -1;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = owner.AutoContext.DbRecordLinks.AsNoTracking().Where(e => e.FieldId > lastField
                    || e.FieldId == lastField && e.FromRecordId > lastRecord
                    || e.FieldId == lastField && e.FromRecordId == lastRecord && e.Position > lastPosition
                    || e.FieldId == lastField && e.FromRecordId == lastRecord && e.Position == lastPosition && e.Id > lastId)
                    .OrderBy(e => e.FieldId).ThenBy(e => e.FromRecordId).ThenBy(e => e.Position).ThenBy(e => e.Id)
                    .Take(options.PageSize).ToList();
                if (page.Count == 0) break;
                var sourceIds = page.Select(e => e.FromRecordId).Distinct().ToList();
                var sources = owner.AutoContext.DbRecords.AsNoTracking().Where(r => sourceIds.Contains(r.Id))
                    .ToList().ToDictionary(r => r.Id);
                var targets = ReadTargets(page);
                foreach (var edge in page)
                {
                    ct.ThrowIfCancellationRequested();
                    AddHash(JsonSerializer.Serialize(new { edge.Id, edge.FieldId, edge.FromRecordId,
                        edge.TargetDbId, edge.ToRecordId, edge.Position }));
                    sources.TryGetValue(edge.FromRecordId, out var source);
                    _source.Fields.TryGetValue(edge.FieldId, out var field);
                    AddHash(source is null ? "missing-source" : JsonSerializer.Serialize(new { source.Id, source.CollectionId,
                        Title = _source.Can(PermAction.ViewCollection, source.CollectionId) ? source.Title : "" }));
                    CheckEdge(edge, field, source, targets);
                }
                var last = page[^1];
                (lastField, lastRecord, lastPosition, lastId) = (last.FieldId, last.FromRecordId, last.Position, last.Id);
                progress?.Report(new(_edges, _fields, _confirmed + _unverified));
            }
            EndCell();
            progress?.Report(new(_edges, _fields, _confirmed + _unverified));
            return Result(RelationIntegrityStatus.Completed, "Проверка завершена. Данные не изменялись.",
                Convert.ToHexString(_hash.GetHashAndReset()));
        }

        private IntegrityCatalog? Catalog(ConstructorRepository repository)
        {
            var key = repository.RelationDatabaseIdentity;
            if (_catalogs.TryGetValue(key, out var existing)) return existing;
            try
            {
                ct.ThrowIfCancellationRequested();
                locks?.Acquire(repository);
                PermissionService? permissions = null;
                if (repository.Permissions is not null)
                {
                    permissions = new PermissionService(repository.AutoContext) { DbId = RelationDbResolver.DbIdFor(repository) };
                    permissions.Reload(notify: false);
                }
                var fields = new Dictionary<int, DbFieldEntity>();
                var cursor = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = repository.AutoContext.DbFields.AsNoTracking().Where(f => f.Id > cursor)
                        .OrderBy(f => f.Id).Take(options.PageSize).ToList();
                    if (page.Count == 0) break;
                    foreach (var field in page) fields[field.Id] = field;
                    cursor = page[^1].Id;
                }
                var collections = repository.AutoContext.DbCollections.AsNoTracking().ToList().ToDictionary(c => c.Id);
                var catalog = new IntegrityCatalog(repository, fields, collections, permissions);
                _catalogs[key] = catalog;
                AddHash(key);
                AddHash(permissions?.IsAdmin == false ? "user" : "admin");
                foreach (var collection in collections.Values.OrderBy(c => c.Id))
                    AddHash(JsonSerializer.Serialize(new { collection.Id, collection.Name,
                        View = catalog.Can(PermAction.ViewCollection, collection.Id), Edit = catalog.Can(PermAction.EditRecord, collection.Id) }));
                foreach (var field in fields.Values.OrderBy(f => f.Id)) AddHash(JsonSerializer.Serialize(ToDbField(field)));
                return catalog;
            }
            catch (Exception ex) when (ex is DbException or IOException)
            {
                if (ReferenceEquals(repository, owner)) throw;
                AddHash(key + ":unavailable");
                _catalogs[key] = null;
                return null;
            }
        }

        private IntegrityCatalog? Target(int dbId) => dbId == 0 || _dbId > 0 && dbId == _dbId ? _source
            : RelationDbResolver.MountedRepoFor(dbId) is { } repo ? Catalog(repo) : null;

        private bool Property(IntegrityCatalog catalog, int collection, string key) => key == RelationConfig.TitleField
            || int.TryParse(key, out var id) && catalog.Fields.TryGetValue(id, out var field) && field.CollectionId == collection;

        private string PropertyType(IntegrityCatalog catalog, string key) => key == RelationConfig.TitleField ? "text"
            : int.TryParse(key, out var id) && catalog.Fields.TryGetValue(id, out var field) ? field.FieldType : "";

        private void CheckField(DbFieldEntity field)
        {
            if (!_source.Collections.ContainsKey(field.CollectionId))
            {
                FieldIssue(field, RelationIntegrityIssueKind.MissingSourceCollection,
                    "Коллекция исходного свойства отсутствует. Требуется ручная проверка структуры базы.");
                return;
            }
            RelationConfig? relation;
            if (field.FieldType == "relation")
            {
                relation = StrictConfig(field.ConfigJson, out var malformed);
                _configs[field.Id] = relation;
                if (relation is null)
                {
                    FieldIssue(field, malformed ? RelationIntegrityIssueKind.InvalidConfiguration : RelationIntegrityIssueKind.MissingConfiguration,
                        malformed ? "Настройка связи повреждена. Исправьте ее вручную." : "У связи не выбрана целевая коллекция.");
                    return;
                }
                var target = CheckTarget(field, relation);
                if (target is null) return;
                if (!relation.DisplayKeys.Concat(relation.SearchKeys).All(key => Property(target, relation.TargetCollectionId, key))
                    || relation.Auto is { } auto && (!Property(_source, field.CollectionId, auto.SourceField)
                        || !Property(target, relation.TargetCollectionId, auto.TargetField)))
                    FieldIssue(field, RelationIntegrityIssueKind.MissingDependency, "Одно из свойств показа, поиска или сопоставления отсутствует.");
                else if (relation.Auto is { } rule)
                {
                    var sourceType = PropertyType(_source, rule.SourceField);
                    var targetType = PropertyType(target, rule.TargetField);
                    var sourceFamily = RelationAutoMatch.Category(sourceType);
                    var targetFamily = RelationAutoMatch.Category(targetType);
                    if (sourceFamily.Length == 0 || targetFamily.Length == 0
                        || sourceType != "lookup" && targetType != "lookup" && sourceFamily != targetFamily)
                        FieldIssue(field, RelationIntegrityIssueKind.InvalidDependency, "Для автосвязи выбраны несовместимые или неподдерживаемые типы свойств.");
                }
            }
            else
            {
                var computed = RelationComputedConfig.FromConfig(field.ConfigJson);
                if (computed is null || field.FieldType == "lookup" && computed.Aggregate != "lookup"
                    || field.FieldType == "rollup" && !RelationComputedConfig.Aggregates.Contains(computed.Aggregate))
                {
                    FieldIssue(field, RelationIntegrityIssueKind.InvalidConfiguration, "Настройка вычисляемого свойства повреждена.");
                    return;
                }
                if (!_source.Fields.TryGetValue(computed.RelationFieldId, out var sourceRelation)
                    || sourceRelation.FieldType != "relation" || sourceRelation.CollectionId != field.CollectionId)
                {
                    FieldIssue(field, RelationIntegrityIssueKind.MissingDependency, "Исходное свойство связи отсутствует или принадлежит другой коллекции.");
                    return;
                }
                relation = StrictConfig(sourceRelation.ConfigJson, out _);
                if (relation is null)
                {
                    FieldIssue(field, RelationIntegrityIssueKind.InvalidDependency, "Исходное свойство связи не настроено.");
                    return;
                }
                var target = CheckTarget(field, relation);
                if (target is null) return;
                if (computed.Aggregate != "count" && !Property(target, relation.TargetCollectionId, computed.TargetField))
                    FieldIssue(field, RelationIntegrityIssueKind.MissingDependency, "Вычисляемое свойство ссылается на отсутствующий столбец.");
                else if (field.FieldType == "rollup" && computed.Aggregate != "count"
                    && PropertyType(target, computed.TargetField) is not ("number" or "rollup"))
                    FieldIssue(field, RelationIntegrityIssueKind.InvalidDependency, "Числовой итог ссылается на свойство другого типа.");
            }
            if (HasCycle(field, out var incomplete)) FieldIssue(field, RelationIntegrityIssueKind.DependencyCycle,
                "Свойства образуют циклическую зависимость. Исправьте настройки вручную.");
            if (incomplete) FieldIssue(field, RelationIntegrityIssueKind.DependencyUnverified,
                "Цепочка свойств превышает предел глубины проверки. Зависимости не проверены полностью.", false);
        }

        private IntegrityCatalog? CheckTarget(DbFieldEntity field, RelationConfig relation)
        {
            var target = Target(relation.TargetDbId);
            if (target is null)
            {
                FieldIssue(field, RelationIntegrityIssueKind.TargetUnavailable,
                    "Целевая база не открыта или недоступна. Существование целей не проверено.", false);
                return null;
            }
            if (!target.Can(PermAction.ViewCollection, relation.TargetCollectionId))
            {
                FieldIssue(field, RelationIntegrityIssueKind.TargetAccessDenied,
                    "Нет доступа к целевой коллекции. Существование целей не проверено.", false);
                return null;
            }
            if (!target.Collections.ContainsKey(relation.TargetCollectionId))
            {
                FieldIssue(field, RelationIntegrityIssueKind.MissingTargetCollection, "Целевая коллекция отсутствует. Требуется ручная проверка настройки.");
                return null;
            }
            return target;
        }

        private bool HasCycle(DbFieldEntity root, out bool incomplete)
        {
            var limited = false;
            var active = new HashSet<(string, int)>();
            var done = new HashSet<(string, int)>();
            bool Key(IntegrityCatalog catalog, int collection, string key) => int.TryParse(key, out var id)
                && catalog.Fields.TryGetValue(id, out var field) && field.CollectionId == collection
                && catalog.Can(PermAction.ViewCollection, collection) && Walk(catalog, field);
            bool Walk(IntegrityCatalog catalog, DbFieldEntity field)
            {
                var id = (catalog.Owner.RelationDatabaseIdentity, field.Id);
                if (active.Contains(id)) return true;
                if (done.Contains(id)) return false;
                if (active.Count >= 256) { limited = true; return false; }
                active.Add(id);
                var cycle = false;
                if (field.FieldType == "relation" && StrictConfig(field.ConfigJson, out _) is { } relation)
                {
                    var target = relation.TargetDbId == 0 ? catalog : Target(relation.TargetDbId);
                    if (target is not null)
                        cycle = relation.DisplayKeys.Concat(relation.SearchKeys)
                            .Any(key => Key(target, relation.TargetCollectionId, key));
                    if (!cycle && relation.Auto is { } auto)
                    {
                        cycle = Key(catalog, field.CollectionId, auto.SourceField);
                        if (!cycle && target is not null) cycle = Key(target, relation.TargetCollectionId, auto.TargetField);
                    }
                }
                else if (field.FieldType is "lookup" or "rollup" && RelationComputedConfig.FromConfig(field.ConfigJson) is { } computed
                    && catalog.Fields.TryGetValue(computed.RelationFieldId, out var link))
                {
                    cycle = Walk(catalog, link);
                    if (!cycle && computed.Aggregate != "count" && StrictConfig(link.ConfigJson, out _) is { } computedRelation)
                    {
                        var target = computedRelation.TargetDbId == 0 ? catalog : Target(computedRelation.TargetDbId);
                        if (target is not null) cycle = Key(target, computedRelation.TargetCollectionId, computed.TargetField);
                    }
                }
                active.Remove(id);
                done.Add(id);
                return cycle;
            }
            var result = Walk(_source, root);
            incomplete = limited;
            return result;
        }

        private sealed record TargetProof(IntegrityCatalog? Catalog, Dictionary<int, int> Collections, bool Failed);
        private Dictionary<int, TargetProof> ReadTargets(IReadOnlyList<DbRecordLinkEntity> page)
        {
            var result = new Dictionary<int, TargetProof>();
            foreach (var group in page.GroupBy(e => e.TargetDbId).OrderBy(g => g.Key))
            {
                ct.ThrowIfCancellationRequested();
                var target = Target(group.Key);
                if (target is null)
                {
                    AddHash($"target:{group.Key}:unavailable");
                    result[group.Key] = new(null, [], true);
                    continue;
                }
                try
                {
                    var ids = group.Select(e => e.ToRecordId).Distinct().ToList();
                    var records = target.Owner.AutoContext.DbRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
                        .Select(r => new { r.Id, r.CollectionId }).ToList().ToDictionary(r => r.Id, r => r.CollectionId);
                    foreach (var id in ids.Order())
                        AddHash($"target:{group.Key}:{target.Owner.RelationDatabaseIdentity}:{id}:{records.GetValueOrDefault(id)}");
                    result[group.Key] = new(target, records, false);
                }
                catch (Exception ex) when (ex is DbException or IOException)
                {
                    AddHash($"target:{group.Key}:read-error");
                    result[group.Key] = new(target, [], true);
                }
            }
            return result;
        }

        private void CheckEdge(DbRecordLinkEntity edge, DbFieldEntity? field, DbRecordEntity? source,
            IReadOnlyDictionary<int, TargetProof> targets)
        {
            if (source is not null && !_source.Can(PermAction.ViewCollection, source.CollectionId)
                || field is not null && !_source.Can(PermAction.ViewCollection, field.CollectionId)
                || source is null && field is null && !_source.Admin) return;
            var collectionId = source?.CollectionId ?? field?.CollectionId;
            var collectionExists = collectionId is int cid && _source.Collections.ContainsKey(cid);
            if (!collectionExists && !_source.Admin) return;
            _edges++;
            var edit = collectionExists ? _source.Can(PermAction.EditRecord, collectionId!.Value) : _source.Admin;
            RelationIntegrityIssue Issue(RelationIntegrityIssueKind kind, string description, bool confirmed = true, bool repair = false) =>
                new($"edge:{edge.Id}:{kind}", kind, _dbId, _identity, collectionId, edge.FieldId, edge.FromRecordId,
                    edge.Id, edge.TargetDbId, edge.ToRecordId,
                    collectionExists ? _source.Collections[collectionId!.Value].Name : "", field?.Name ?? "", source?.Title ?? "",
                    description, confirmed, repair && edit && _source.Admin);
            void Add(RelationIntegrityIssueKind kind, string message, bool confirmed = true, bool repair = false) =>
                AddIssue(Issue(kind, message, confirmed, repair));
            if (collectionId.HasValue && !collectionExists)
            {
                Add(RelationIntegrityIssueKind.MissingSourceCollection,
                    "Исходная коллекция отсутствует. Требуется ручная проверка структуры базы.");
                if (source is not null && field is not null) return;
            }
            if (source is null) { Add(RelationIntegrityIssueKind.MissingSourceRecord, "Запись-источник отсутствует.", repair: true); return; }
            if (field is null) { Add(RelationIntegrityIssueKind.MissingSourceField, "Свойство-источник отсутствует.", repair: true); return; }
            if (field.FieldType != "relation") { Add(RelationIntegrityIssueKind.InvalidSourceFieldType, "Ребро относится к свойству другого типа. Требуется ручная проверка."); return; }
            if (field.CollectionId != source.CollectionId) { Add(RelationIntegrityIssueKind.SourceCollectionMismatch, "Свойство и запись принадлежат разным коллекциям. Требуется ручная проверка."); return; }
            if (!_configs.TryGetValue(field.Id, out var cfg)) _configs[field.Id] = cfg = StrictConfig(field.ConfigJson, out _);
            if (cfg is null) return;
            if (_cellField != edge.FieldId || _cellRecord != edge.FromRecordId)
            {
                EndCell();
                (_cellField, _cellRecord) = (edge.FieldId, edge.FromRecordId);
                if (cfg.Single) _cellSingle = Issue(RelationIntegrityIssueKind.SingleCardinality,
                    "В одиночной связи несколько разных целей. Выберите нужную цель вручную.");
            }
            var canonicalDb = edge.TargetDbId == _dbId && _dbId > 0 ? 0 : edge.TargetDbId;
            if (!_cellTargets.Add((canonicalDb, edge.ToRecordId)))
                Add(RelationIntegrityIssueKind.CanonicalDuplicate, "Повтор одной цели по полной идентичности базы и записи.", repair: true);
            if (_cellNumericIds.TryGetValue(edge.ToRecordId, out var numericDb) && numericDb != canonicalDb)
                Add(RelationIntegrityIssueKind.NumericProjectionCollision,
                    "Одинаковый числовой id относится к разным базам. Полные цели различны, исправление не требуется.");
            else _cellNumericIds[edge.ToRecordId] = canonicalDb;
            if (edge.TargetDbId < 0 || edge.ToRecordId <= 0)
            { Add(RelationIntegrityIssueKind.InvalidTargetIdentity, "Идентификатор цели недопустим.", repair: true); return; }
            if (canonicalDb == 0 && edge.ToRecordId == source.Id)
            { Add(RelationIntegrityIssueKind.InvalidSelfLink, "Запись ссылается на себя. Такая связь не поддерживается.", repair: true); return; }
            var configuredDb = cfg.TargetDbId == _dbId && _dbId > 0 ? 0 : cfg.TargetDbId;
            if (canonicalDb != configuredDb)
                Add(RelationIntegrityIssueKind.LegacyMixedTarget,
                    "Цель сохранена из другой настройки базы. Ее полная идентичность сохранена, исправление не требуется.");
            var proof = targets[edge.TargetDbId];
            if (proof.Failed || proof.Catalog is null)
            { Add(RelationIntegrityIssueKind.TargetUnavailable, "Целевая база не открыта или недоступна. Удаление цели не доказано.", false); return; }
            if (proof.Collections.TryGetValue(edge.ToRecordId, out var targetCollection))
            {
                if (!proof.Catalog.Can(PermAction.ViewCollection, targetCollection))
                    Add(RelationIntegrityIssueKind.TargetAccessDenied, "Нет доступа к целевой записи. Удаление цели не доказано.", false);
                else if (canonicalDb == configuredDb && targetCollection != cfg.TargetCollectionId)
                    Add(RelationIntegrityIssueKind.TargetCollectionMismatch,
                        "Прежняя цель находится в другой коллекции. Ее полная идентичность сохранена, исправление не требуется.");
                return;
            }
            if (canonicalDb != configuredDb && !proof.Catalog.Admin
                || canonicalDb == configuredDb && (!proof.Catalog.Collections.ContainsKey(cfg.TargetCollectionId)
                    || !proof.Catalog.Can(PermAction.ViewCollection, cfg.TargetCollectionId)))
            { Add(RelationIntegrityIssueKind.TargetAccessDenied, "Недостаточно данных или прав для проверки прежней цели. Удаление не доказано.", false); return; }
            Add(RelationIntegrityIssueKind.MissingTargetRecord, "Целевая запись отсутствует в проверенной доступной базе.", repair: true);
        }

        private void EndCell()
        {
            if (_cellSingle is not null && _cellTargets.Count > 1) AddIssue(_cellSingle);
            _cellSingle = null;
            _cellTargets.Clear();
            _cellNumericIds.Clear();
        }

        private void FieldIssue(DbFieldEntity field, RelationIntegrityIssueKind kind, string description, bool confirmed = true) =>
            AddIssue(new($"field:{field.Id}:{kind}", kind, _dbId, _identity, field.CollectionId, field.Id,
                null, null, null, null, _source.Collections.GetValueOrDefault(field.CollectionId)?.Name ?? "", field.Name, "",
                description, confirmed, false));

        private void AddIssue(RelationIntegrityIssue issue)
        {
            if (issue.Kind is RelationIntegrityIssueKind.LegacyMixedTarget
                or RelationIntegrityIssueKind.NumericProjectionCollision or RelationIntegrityIssueKind.TargetCollectionMismatch)
                issue = issue with { IsInformational = true };
            _counts[issue.Kind] = _counts.GetValueOrDefault(issue.Kind) + 1;
            if (issue.IsConfirmed) _confirmed++; else _unverified++;
            AddHash(JsonSerializer.Serialize(issue));
            if (_issues.Count < options.MaxIssues) _issues.Add(issue);
        }

        private void AddHash(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            _hash.AppendData(BitConverter.GetBytes(bytes.Length));
            _hash.AppendData(bytes);
        }

        public RelationIntegrityReport Failure(RelationIntegrityStatus status, string message) => Result(status, message, "");
        private RelationIntegrityReport Result(RelationIntegrityStatus status, string message, string fingerprint) =>
            new(status, _dbId, _identity, _edges, _fields, _confirmed, _unverified, _issues, _counts,
                _confirmed + _unverified > _issues.Count, message, fingerprint, options);
        public void Dispose() => _hash.Dispose();
    }

    private static RelationConfig? StrictConfig(string? json, out bool malformed)
    {
        malformed = false;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { malformed = true; return null; }
            if (!root.TryGetProperty("targetCollectionId", out var collection)) return null;
            bool Field(string name, JsonElement element) => !element.TryGetProperty(name, out var key)
                || key.ValueKind == JsonValueKind.String && RelationConfig.IsFieldKey(key.GetString());
            malformed = collection.ValueKind != JsonValueKind.Number || !collection.TryGetInt32(out var cid) || cid <= 0
                || root.TryGetProperty("targetDbId", out var database) && (database.ValueKind != JsonValueKind.Number
                    || !database.TryGetInt32(out var dbId) || dbId < 0)
                || root.TryGetProperty("single", out var single) && single.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !Field("displayField", root) || !Field("searchField", root);
            if (root.TryGetProperty("auto", out var auto))
                malformed |= auto.ValueKind != JsonValueKind.Object || !auto.TryGetProperty("sourceField", out _)
                    || !auto.TryGetProperty("targetField", out _) || !Field("sourceField", auto) || !Field("targetField", auto);
            return malformed ? null : RelationConfig.FromConfig(json);
        }
        catch (JsonException) { malformed = true; return null; }
    }
}
