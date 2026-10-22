using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    private bool _savingAutoRelation;
    private bool _refreshingAutoRelations;
    internal bool IsApplyingAutoRelation => _savingAutoRelation;
    private AppDbContext AutoContext => db;

    /// <summary>Предварительный подбор не меняет ни настройку поля, ни существующие ребра.</summary>
    public RelationAutoPreview PreviewAutoRelation(int fieldId, string? configJson = null,
        CancellationToken ct = default)
    {
        var field = db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fieldId);
        if (field is null) return AutoFailure(RelationAutoStatus.NoRule, null, "Свойство не найдено.");
        var proposed = ToDbField(field);
        if (configJson is not null) proposed.ConfigJson = configJson;
        return PreviewAutoRelation(proposed, ct);
    }

    public RelationAutoPreview PreviewAutoRelation(DbField proposedField, CancellationToken ct = default)
    {
        try { return PreviewAutoRelationCore(CloneAutoField(proposedField), ct); }
        catch (OperationCanceledException) { return AutoFailure(RelationAutoStatus.Cancelled, proposedField, "Подбор отменен."); }
        catch (PermissionDeniedException) { return AutoFailure(RelationAutoStatus.NoAccess, proposedField, "Нет доступа к свойствам для подбора связей."); }
        catch (DbException) { return AutoFailure(RelationAutoStatus.Unavailable, proposedField, "База данных недоступна. Прежние связи сохранены."); }
        catch (IOException) { return AutoFailure(RelationAutoStatus.Unavailable, proposedField, "База данных недоступна. Прежние связи сохранены."); }
    }

    /// <summary>Фон использует собственные контексты всех читаемых баз. Общие репозитории окна
    /// и монтирование базы из фонового потока не используются.</summary>
    public Task<RelationAutoPreview> PreviewAutoRelationAsync(DbField proposedField, CancellationToken ct = default)
    {
        Demand(PermAction.ViewCollection, proposedField.CollectionId);
        if (db.Database.CurrentTransaction is not null)
            return Task.FromResult(AutoFailure(RelationAutoStatus.Conflict, proposedField,
                "Фоновый подбор недоступен внутри незавершенной транзакции. Используйте синхронный подбор или завершите операцию."));
        var field = CloneAutoField(proposedField);
        var connections = CaptureAutoConnections(field);
        return Task.Run(() => WithAutoConnections(connections, repo => repo.PreviewAutoRelation(field, ct)));
    }

    /// <summary>Сохранение настройки и результата — одна транзакция. Изменившийся после
    /// предварительного просмотра источник, цель или набор ребер требует нового просмотра.</summary>
    public int SaveAutoRelationField(DbField proposedField, RelationAutoPreview preview, CancellationToken ct = default)
    {
        DemandAutoSave(proposedField);
        var field = CloneAutoField(proposedField);
        // Внешняя составная операция должна откатывать настройку и ребра вместе со своими
        // остальными изменениями. Отдельное подключение здесь не видит ее незавершенные данные.
        var result = db.Database.CurrentTransaction is not null
            ? SaveAutoRelationFieldCore(field, preview, ct)
            : WithAutoConnections(CaptureAutoConnections(field), repo => repo.SaveAutoRelationFieldCore(field, preview, ct));
        proposedField.Id = result;
        DetachAutoRelationTracking(result);
        NotifyRelationDataChanged(proposedField.CollectionId, result);
        return result;
    }

    public async Task<int> SaveAutoRelationFieldAsync(DbField proposedField, RelationAutoPreview preview,
        CancellationToken ct = default)
    {
        DemandAutoSave(proposedField);
        if (db.Database.CurrentTransaction is not null)
            throw new RelationAutoApplyException(RelationAutoStatus.Conflict,
                "Фоновое сохранение недоступно внутри незавершенной транзакции. Используйте синхронное сохранение или завершите операцию.");
        var field = CloneAutoField(proposedField);
        var connections = CaptureAutoConnections(field);
        var result = await Task.Run(() => WithAutoConnections(connections,
            repo => repo.SaveAutoRelationFieldCore(field, preview, ct)));
        proposedField.Id = result;
        // После await снова вызывающий UI-контекст, его tracker не уходит в фон.
        DetachAutoRelationTracking(result);
        NotifyRelationDataChanged(proposedField.CollectionId, result);
        return result;
    }

    public RelationAutoApplyResult ApplyAutoRelation(int fieldId, string? configJson = null,
        CancellationToken ct = default) => ApplyAutoRelation(PreviewAutoRelation(fieldId, configJson, ct), ct);

    public RelationAutoApplyResult ApplyAutoRelation(RelationAutoPreview preview, CancellationToken ct = default)
    {
        if (!preview.CanApply || preview.ProposedField is null)
            return new(preview.Status, 0, preview.Message);
        try
        {
            var proposed = CloneAutoField(preview.ProposedField);
            var current = proposed.Id is int fid ? db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fid) : null;
            if (current is null || JsonSerializer.Serialize(ToDbField(current)) != JsonSerializer.Serialize(proposed))
                SaveAutoRelationField(proposed, preview, ct);
            else
            {
                Demand(PermAction.EditRecord, proposed.CollectionId);
                if (db.Database.CurrentTransaction is not null)
                    SaveAutoRelationFieldCore(proposed, preview, ct, saveMetadata: false);
                else
                    WithAutoConnections(CaptureAutoConnections(proposed), repo =>
                        repo.SaveAutoRelationFieldCore(proposed, preview, ct, saveMetadata: false));
                DetachAutoRelationTracking(proposed.Id!.Value);
                if (preview.Changes.Count > 0) NotifyRelationDataChanged(proposed.CollectionId, proposed.Id.Value);
            }
            return new(RelationAutoStatus.Applied, preview.Changes.Count, "Автоматические связи обновлены.");
        }
        catch (OperationCanceledException) { return new(RelationAutoStatus.Cancelled, 0, "Обновление отменено. Прежние связи сохранены."); }
        catch (RelationAutoApplyException ex) { return new(ex.Status, 0, ex.Message); }
        catch (PermissionDeniedException) { return new(RelationAutoStatus.NoAccess, 0, "Нет прав на обновление связей."); }
        catch (DbException) { return new(RelationAutoStatus.Unavailable, 0, "База данных недоступна. Прежние связи сохранены."); }
        catch (IOException) { return new(RelationAutoStatus.Unavailable, 0, "База данных недоступна. Прежние связи сохранены."); }
    }

    /// <summary>После сохранения исходного/целевого значения обновляет только затронутые
    /// правила. fieldId=0 — «Название», null — изменение состава коллекции или всей схемы.</summary>
    public IReadOnlyList<RelationAutoApplyResult> RefreshAutoRelationsForChange(int collectionId,
        int? changedFieldId = null, int? recordId = null, CancellationToken ct = default)
    {
        if (_savingAutoRelation || _refreshingAutoRelations) return [];
        var fields = db.DbFields.AsNoTracking().Where(f => f.FieldType == "relation").ToList()
            .Select(ToDbField).Where(f => RelationConfig.FromConfig(f.ConfigJson)?.Auto is not null
                && AutoDependsOnChange(f, DbPath, collectionId, changedFieldId))
            .ToList();
        return RefreshAutoRelationFields(fields, ct);
    }

    /// <summary>Поллер или редактор другой базы сообщает об изменении ее данных. Отсутствие
    /// базы при проверке не очищает прежде найденные цели.</summary>
    public IReadOnlyList<RelationAutoApplyResult> RefreshAutoRelationsForExternalChange(int targetDbId,
        int targetCollectionId, CancellationToken ct = default)
    {
        if (_savingAutoRelation || _refreshingAutoRelations || targetDbId <= 0) return [];
        var changed = RelationDbResolver.RepoFor(targetDbId);
        var fields = db.DbFields.AsNoTracking().Where(f => f.FieldType == "relation").ToList()
            .Select(ToDbField).Where(f => RelationConfig.FromConfig(f.ConfigJson) is { Auto: not null } cfg
                && (cfg.TargetDbId == targetDbId && cfg.TargetCollectionId == targetCollectionId
                    || changed is not null && AutoDependsOnChange(f, changed.DbPath, targetCollectionId, null))).ToList();
        return RefreshAutoRelationFields(fields, ct);
    }

    public Task<IReadOnlyList<RelationAutoApplyResult>> RefreshAutoRelationsForChangeAsync(int collectionId,
        int? changedFieldId = null, int? recordId = null, CancellationToken ct = default)
    {
        var work = CaptureAutoRefreshForChange(collectionId, changedFieldId, recordId);
        return Task.Run(() => work(ct));
    }

    public Task<IReadOnlyList<RelationAutoApplyResult>> RefreshAutoRelationsForExternalChangeAsync(int targetDbId,
        int targetCollectionId, CancellationToken ct = default)
    {
        var work = CaptureAutoRefreshForExternalChange(targetDbId, targetCollectionId);
        return Task.Run(() => work(ct));
    }

    /// <summary>Вызывается на владельце контекста до ухода в фон; сам подготовленный callback
    /// читает данные только через новые подключения и не зависит от завершенной внешней транзакции.</summary>
    public Func<CancellationToken, IReadOnlyList<RelationAutoApplyResult>> CaptureAutoRefreshForChange(
        int collectionId, int? changedFieldId = null, int? recordId = null)
    {
        var connections = CaptureAutoConnections(new DbField { CollectionId = collectionId });
        return ct => WithAutoConnections(connections,
            repo => repo.RefreshAutoRelationsForChange(collectionId, changedFieldId, recordId, ct));
    }

    public Func<CancellationToken, IReadOnlyList<RelationAutoApplyResult>> CaptureAutoRefreshForExternalChange(
        int targetDbId, int targetCollectionId)
    {
        var connections = CaptureAutoConnections(new DbField { CollectionId = targetCollectionId });
        return ct => WithAutoConnections(connections,
            repo => repo.RefreshAutoRelationsForExternalChange(targetDbId, targetCollectionId, ct));
    }

    private IReadOnlyList<RelationAutoApplyResult> RefreshAutoRelationFields(IReadOnlyList<DbField> fields, CancellationToken ct)
    {
        var results = new List<RelationAutoApplyResult>();
        _refreshingAutoRelations = true;
        try
        {
            var pending = fields.ToList();
            var failed = new List<DbField>();
            while (pending.Count > 0)
            {
                if (ct.IsCancellationRequested) break;
                var field = pending.FirstOrDefault(candidate => !pending.Any(other => other.Id != candidate.Id
                    && AutoDependsOnChange(candidate, DbPath, other.CollectionId, other.Id)));
                if (field is null)
                {
                    results.Add(new(RelationAutoStatus.InvalidRule, 0, "Правила образуют циклическую зависимость между свойствами."));
                    break;
                }
                pending.Remove(field);
                if (failed.Any(other => AutoDependsOnChange(field, DbPath, other.CollectionId, other.Id)))
                {
                    results.Add(new(RelationAutoStatus.Unavailable, 0, "Зависимое свойство не удалось обновить. Прежние связи сохранены."));
                    failed.Add(field);
                    continue;
                }
                var result = ApplyAutoRelation(field.Id!.Value, ct: ct);
                results.Add(result);
                if (!result.Applied) failed.Add(field);
            }
        }
        finally { _refreshingAutoRelations = false; }
        return results;
    }

    private RelationAutoPreview PreviewAutoRelationCore(DbField field, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Demand(PermAction.ViewCollection, field.CollectionId);
        if (field.FieldType != "relation" || RelationConfig.FromConfig(field.ConfigJson) is not { Auto: { } rule } cfg)
            return AutoFailure(RelationAutoStatus.NoRule, field, "Правило автоматической связи не задано.");
        if (field.Id is int fid && (rule.SourceField == fid.ToString()
            || cfg.TargetDbId == 0 && cfg.TargetCollectionId == field.CollectionId && rule.TargetField == fid.ToString()))
            return AutoFailure(RelationAutoStatus.InvalidRule, field, "Свойство не может подбирать связи по самому себе.");
        var target = cfg.TargetDbId == 0 ? this : RelationDbResolver.RepoFor(cfg.TargetDbId);
        if (target is null) return AutoFailure(RelationAutoStatus.Unavailable, field, "Целевая база недоступна. Прежние связи сохранены.");
        target.Demand(PermAction.ViewCollection, cfg.TargetCollectionId);
        if (!db.DbCollections.AsNoTracking().Any(c => c.Id == field.CollectionId)
            || !target.AutoContext.DbCollections.AsNoTracking().Any(c => c.Id == cfg.TargetCollectionId))
            return AutoFailure(RelationAutoStatus.InvalidRule, field, "Коллекция для подбора связей не найдена.");
        var sourceOption = GetRelationPropertyOptions(field.CollectionId).FirstOrDefault(p => p.Key == rule.SourceField);
        var targetOption = target.GetRelationPropertyOptions(cfg.TargetCollectionId).FirstOrDefault(p => p.Key == rule.TargetField);
        if (sourceOption is null || targetOption is null)
            return AutoFailure(RelationAutoStatus.InvalidRule, field, "Свойство для сопоставления не найдено.");
        var family = RelationAutoMatch.Category(sourceOption.FieldType);
        if (family.Length == 0 || RelationAutoMatch.Category(targetOption.FieldType).Length == 0
            || sourceOption.FieldType != "lookup" && targetOption.FieldType != "lookup"
                && family != RelationAutoMatch.Category(targetOption.FieldType))
            return AutoFailure(RelationAutoStatus.InvalidRule, field, "Для сопоставления выбраны несовместимые типы свойств.");
        if (HasAutoDependencyCycle(field))
            return AutoFailure(RelationAutoStatus.InvalidRule, field, "Правило образует циклическую зависимость между свойствами.");

        var sources = ReadRelationPropertyValues(field.CollectionId, rule.SourceField);
        ct.ThrowIfCancellationRequested();
        var targets = target.ReadRelationPropertyValues(cfg.TargetCollectionId, rule.TargetField);
        ct.ThrowIfCancellationRequested();
        // Условие выбора: подбираются только подходящие записи цели; прежняя цель остается, пока
        // совпадает ключ. Сломанное условие — отказ, прежние связи сохраняются.
        IReadOnlySet<int>? allowed = null;
        if (cfg.HasCondition)
        {
            var verdict = target.EvaluateRelationCondition(cfg.TargetCollectionId, cfg, null);
            if (verdict.Broken is { } broken) return AutoFailure(RelationAutoStatus.InvalidRule, field, broken + " Прежние связи сохранены.");
            allowed = verdict.Allowed;
            ct.ThrowIfCancellationRequested();
        }
        var targetDbId = cfg.TargetDbId;
        var targetIndex = new Dictionary<string, List<RelationTarget>>(StringComparer.Ordinal);
        var sourceDbId = RelationDbResolver.DbIdFor(this);
        var absoluteTargetDbId = cfg.TargetDbId == 0 ? sourceDbId : cfg.TargetDbId;
        foreach (var value in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (!RelationAutoMatch.TryKey(value.Raw, value.FieldType, out var key, absoluteTargetDbId))
                return AutoValueFailure(field, value, "целевом");
            if (key is null) continue;
            if (!targetIndex.TryGetValue(key, out var list)) targetIndex[key] = list = [];
            list.Add(new(targetDbId, value.RecordId));
        }
        var previous = field.Id is int existingField
            ? db.DbRecordLinks.AsNoTracking().Where(l => l.FieldId == existingField)
                .OrderBy(l => l.FromRecordId).ThenBy(l => l.Position).ThenBy(l => l.Id).ToList()
            : [];
        var previousByRecord = previous.GroupBy(l => l.FromRecordId).ToDictionary(g => g.Key,
            g => g.Select(l => new RelationTarget(l.TargetDbId, l.ToRecordId)).ToArray());
        var changes = new List<RelationAutoChange>();
        var unmatched = 0;
        var ambiguous = 0;
        var outside = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (!RelationAutoMatch.TryKey(source.Raw, source.FieldType, out var key, sourceDbId))
                return AutoValueFailure(field, source, "исходном");
            var old = previousByRecord.GetValueOrDefault(source.RecordId, []);
            var found = key is not null && targetIndex.TryGetValue(key, out var matches)
                ? matches.Where(t => t.DbId != 0 || t.RecordId != source.RecordId).Distinct().ToArray()
                : [];
            var wanted = found.Where(t => allowed is null || allowed.Contains(t.RecordId) || old.Contains(t))
                .OrderBy(t => t.DbId).ThenBy(t => t.RecordId).ToArray();
            if (wanted.Length < found.Length) outside++;
            if (wanted.Length == 0) unmatched++;
            if (cfg.Single && wanted.Length > 1) { ambiguous++; continue; }
            if (old.SequenceEqual(wanted)) continue;
            changes.Add(new(source.RecordId, source.Title, Array.AsReadOnly(old), Array.AsReadOnly(wanted)));
        }
        ct.ThrowIfCancellationRequested();
        var fingerprint = AutoFingerprint(field, rule, target, sources, targets, previous, allowed);
        return new(ambiguous > 0 ? RelationAutoStatus.Ambiguous : RelationAutoStatus.Ready,
            field.Id ?? 0, field.CollectionId, field.ConfigJson,
            ambiguous > 0 ? $"Для {ambiguous} записей найдено несколько целей. Выберите режим «Несколько» или уточните значения."
                : "Подбор готов к сохранению.", changes, unmatched, ambiguous, fingerprint, AutoDatabaseIdentity(), field, outside);
    }

    private int SaveAutoRelationFieldCore(DbField field, RelationAutoPreview preview, CancellationToken ct,
        bool saveMetadata = true)
    {
        if (saveMetadata) DemandAutoSave(field);
        else Demand(PermAction.EditRecord, field.CollectionId);
        if (!preview.CanApply || preview.DatabaseIdentity != AutoDatabaseIdentity()
            || preview.CollectionId != field.CollectionId || preview.FieldId != (field.Id ?? 0)
            || preview.ConfigJson != field.ConfigJson)
            throw new RelationAutoApplyException(preview.CanApply ? RelationAutoStatus.Conflict : preview.Status,
                preview.CanApply ? "Настройка изменилась. Повторите предварительный просмотр." : preview.Message);
        ct.ThrowIfCancellationRequested();
        using var tx = new AutoWriteScope(db);
        var fresh = PreviewAutoRelation(field, ct);
        if (!fresh.CanApply) throw new RelationAutoApplyException(fresh.Status, fresh.Message);
        if (fresh.Fingerprint != preview.Fingerprint)
            throw new RelationAutoApplyException(RelationAutoStatus.Conflict, "Данные изменились. Повторите предварительный просмотр.");
        _savingAutoRelation = true;
        try
        {
            if (saveMetadata)
            {
                if (field.Id.HasValue) UpdateField(field);
                else AddField(field);
            }
            ct.ThrowIfCancellationRequested();
            var fieldId = field.Id!.Value;
            WriteAutoRelationChanges(fieldId, fresh.Changes, ct);
            if (fresh.Changes.Count > 0)
            {
                Log("update", "field", field.Name, CollectionNameOf(field.CollectionId), count: fresh.Changes.Count,
                    detail: "Обновление автоматических связей");
                db.SaveChanges();
            }
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return fieldId;
        }
        finally { _savingAutoRelation = false; }
    }

    // Вложенный отказ не оставляет удаленные ребра или измененную настройку даже тогда,
    // когда вызывающий код перехватил ошибку и продолжил внешнюю транзакцию.
    private sealed class AutoWriteScope : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly IDbContextTransaction _transaction;
        private readonly bool _own;
        private readonly string? _savepoint;
        private bool _committed;

        public AutoWriteScope(AppDbContext context)
        {
            _context = context;
            _own = context.Database.CurrentTransaction is null;
            _transaction = context.Database.CurrentTransaction
                ?? context.Database.BeginTransaction(IsolationLevel.Serializable);
            if (_own) return;
            if (!_transaction.SupportsSavepoints)
                throw new RelationAutoApplyException(RelationAutoStatus.Conflict,
                    "Текущая транзакция не поддерживает безопасное вложенное сохранение связей.");
            _savepoint = "auto_relation_" + Guid.NewGuid().ToString("N");
            _transaction.CreateSavepoint(_savepoint);
        }

        public void Commit()
        {
            if (_own) _transaction.Commit();
            else _transaction.ReleaseSavepoint(_savepoint!);
            _committed = true;
        }

        public void Dispose()
        {
            if (!_committed)
            {
                try
                {
                    if (_own) _transaction.Rollback();
                    else
                    {
                        _transaction.RollbackToSavepoint(_savepoint!);
                        _transaction.ReleaseSavepoint(_savepoint!);
                    }
                }
                catch { /* исходная ошибка важнее отказа отката */ }
                _context.ChangeTracker.Clear();
            }
            if (_own) _transaction.Dispose();
        }
    }

    private void WriteAutoRelationChanges(int fieldId, IReadOnlyList<RelationAutoChange> changes, CancellationToken ct)
    {
        if (changes.Count == 0) return;
        var conn = db.Database.GetDbConnection();
        var tx = db.Database.CurrentTransaction!.GetDbTransaction();
        // Пакет удаления и пакет вставки, без SELECT/SaveChanges на каждую запись.
        foreach (var chunk in changes.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            using var delete = conn.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = $"DELETE FROM db_record_links WHERE FieldId = @field AND FromRecordId IN ({string.Join(',', chunk.Select(c => c.RecordId))})";
            AddAutoParameter(delete, "@field", fieldId);
            delete.ExecuteNonQuery();
        }
        var edges = changes.SelectMany(c => c.NewTargets.Select((target, index) =>
            (c.RecordId, Target: target, Position: checked((index + 1) * 1000))));
        foreach (var chunk in edges.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            var rows = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                rows.Add($"(@field,@r{i},@t{i},@d{i},@p{i})");
                AddAutoParameter(insert, $"@r{i}", chunk[i].RecordId);
                AddAutoParameter(insert, $"@t{i}", chunk[i].Target.RecordId);
                AddAutoParameter(insert, $"@d{i}", chunk[i].Target.DbId);
                AddAutoParameter(insert, $"@p{i}", chunk[i].Position);
            }
            AddAutoParameter(insert, "@field", fieldId);
            insert.CommandText = "INSERT INTO db_record_links (FieldId,FromRecordId,ToRecordId,TargetDbId,Position) VALUES " + string.Join(',', rows);
            insert.ExecuteNonQuery();
        }
    }

    private static void AddAutoParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private void DemandAutoSave(DbField field)
    {
        Demand(field.Id.HasValue ? PermAction.EditField : PermAction.CreateField, field.CollectionId);
        Demand(PermAction.EditRecord, field.CollectionId);
    }

    private void DetachAutoRelationTracking(int fieldId)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e =>
                     e.Entity is DbFieldEntity field && field.Id == fieldId
                     || e.Entity is DbRecordLinkEntity link && link.FieldId == fieldId).ToList())
            entry.State = EntityState.Detached;
    }

    private string AutoFingerprint(DbField field, RelationAutoRule rule, ConstructorRepository target,
        IReadOnlyList<RelationPropertyValue> sources,
        IReadOnlyList<RelationPropertyValue> targets, IReadOnlyList<DbRecordLinkEntity> previous,
        IReadOnlySet<int>? allowed = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add(JsonSerializer.Serialize(field));
        var old = field.Id is int id ? db.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == id) : null;
        Add(old is null ? "" : JsonSerializer.Serialize(ToDbField(old)));
        // Даже у пустой коллекции есть схема выбранного ключа. Ее изменение после
        // просмотра требует повторного подтверждения независимо от наличия строк.
        void AddProperty(ConstructorRepository owner, string key)
        {
            Add(key);
            if (!int.TryParse(key, out var propertyId)) return;
            var property = owner.AutoContext.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == propertyId);
            Add(property is null ? "" : JsonSerializer.Serialize(ToDbField(property)));
        }
        AddProperty(this, rule.SourceField);
        AddProperty(target, rule.TargetField);
        foreach (var set in new[] { sources, targets })
        {
            Add(set.Count.ToString());
            foreach (var value in set.OrderBy(v => v.RecordId)) Add(JsonSerializer.Serialize(value));
        }
        foreach (var edge in previous) Add($"{edge.FromRecordId}:{edge.TargetDbId}:{edge.ToRecordId}:{edge.Position}");
        // Подошедшие под условие выбора: смена статуса цели после просмотра требует нового просмотра.
        if (allowed is not null) Add(string.Join(",", allowed.Order()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Зависимости вычисляемых ключей и других автосвязей проверяются по схеме, до чтения данных.
    private bool HasAutoDependencyCycle(DbField proposed)
    {
        var active = new HashSet<(string Database, int Field)>();
        var done = new HashSet<(string Database, int Field)>();
        var rootDatabase = AutoDatabaseIdentity();
        bool WalkProperty(ConstructorRepository owner, string key)
        {
            if (!int.TryParse(key, out var id) || id <= 0) return false;
            if (owner.AutoDatabaseIdentity() == rootDatabase && proposed.Id == id) return Walk(owner, proposed);
            var entity = owner.AutoContext.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == id);
            return entity is not null && Walk(owner, ToDbField(entity));
        }
        bool Walk(ConstructorRepository owner, DbField field)
        {
            var identity = (owner.AutoDatabaseIdentity(), field.Id ?? 0);
            if (done.Contains(identity)) return false;
            if (active.Count >= 64 || !active.Add(identity)) return true;
            var cycle = false;
            if (field.FieldType == "relation" && RelationConfig.FromConfig(field.ConfigJson) is { Auto: { } rule } relation)
            {
                cycle = WalkProperty(owner, rule.SourceField);
                var target = relation.TargetDbId == 0 ? owner : RelationDbResolver.RepoFor(relation.TargetDbId);
                if (!cycle && target is not null) cycle = WalkProperty(target, rule.TargetField);
            }
            else if (field.FieldType is "lookup" or "rollup"
                     && RelationComputedConfig.FromConfig(field.ConfigJson) is { } computed)
            {
                cycle = WalkProperty(owner, computed.RelationFieldId.ToString());
                if (!cycle && computed.Aggregate != "count")
                {
                    var relationEntity = owner.AutoContext.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == computed.RelationFieldId);
                    var cfg = RelationConfig.FromConfig(relationEntity?.ConfigJson);
                    var target = cfg?.TargetDbId == 0 ? owner : cfg is null ? null : RelationDbResolver.RepoFor(cfg.TargetDbId);
                    if (target is not null) cycle = WalkProperty(target, computed.TargetField);
                }
            }
            active.Remove(identity);
            done.Add(identity);
            return cycle;
        }
        return Walk(this, proposed);
    }

    private bool AutoDependsOnChange(DbField proposed, string changedPath, int changedCollection, int? changedField)
    {
        var visited = new HashSet<(string Database, int Collection, string Key)>();
        var changedKey = changedField is int id ? id == 0 ? RelationConfig.TitleField : id.ToString() : null;
        bool Property(ConstructorRepository owner, int collection, string key)
        {
            if (owner.DbPath == changedPath && collection == changedCollection
                && (changedKey is null || key == changedKey)) return true;
            if (!visited.Add((owner.AutoDatabaseIdentity(), collection, key)) || visited.Count > 128
                || !int.TryParse(key, out var fid)) return false;
            var field = owner.AutoContext.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == fid && f.CollectionId == collection);
            if (field is null) return false;
            // Любая ручная правка записи меняет ее аудит, даже если выбран другой столбец.
            if (field.FieldType is "updated_at" or "updated_by"
                && owner.DbPath == changedPath && collection == changedCollection) return true;
            if (field.FieldType == "relation" && RelationConfig.FromConfig(field.ConfigJson) is { Auto: not null } relation)
                return Rule(owner, ToDbField(field), relation);
            if (field.FieldType is not ("lookup" or "rollup")
                || RelationComputedConfig.FromConfig(field.ConfigJson) is not { } computed) return false;
            if (Property(owner, collection, computed.RelationFieldId.ToString())) return true;
            var relationEntity = owner.AutoContext.DbFields.AsNoTracking().FirstOrDefault(f => f.Id == computed.RelationFieldId);
            var cfg = RelationConfig.FromConfig(relationEntity?.ConfigJson);
            if (cfg is null) return false;
            var target = cfg.TargetDbId == 0 ? owner : RelationDbResolver.RepoFor(cfg.TargetDbId);
            if (target is null) return false;
            // Количество зависит от существования целей, остальные агрегаты — еще от их свойства.
            return computed.Aggregate == "count"
                ? target.DbPath == changedPath && cfg.TargetCollectionId == changedCollection && changedKey is null
                : Property(target, cfg.TargetCollectionId, computed.TargetField);
        }
        bool Rule(ConstructorRepository owner, DbField field, RelationConfig cfg)
        {
            if (cfg.Auto is not { } auto) return false;
            if (Property(owner, field.CollectionId, auto.SourceField)) return true;
            var target = cfg.TargetDbId == 0 ? owner : RelationDbResolver.RepoFor(cfg.TargetDbId);
            // Подбор зависит и от свойств условия выбора: запись цели могла начать или перестать подходить.
            return target is not null && (Property(target, cfg.TargetCollectionId, auto.TargetField)
                || (cfg.Where ?? []).Any(c => Property(target, cfg.TargetCollectionId, c.Field)));
        }
        return RelationConfig.FromConfig(proposed.ConfigJson) is { } cfg && Rule(this, proposed, cfg);
    }

    private string AutoDatabaseIdentity() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(db.DbPath)));
    private static DbField CloneAutoField(DbField field) => JsonSerializer.Deserialize<DbField>(JsonSerializer.Serialize(field))!;
    private RelationAutoPreview AutoFailure(RelationAutoStatus status, DbField? field, string message) =>
        new(status, field?.Id ?? 0, field?.CollectionId ?? 0, field?.ConfigJson ?? "", message,
            databaseIdentity: AutoDatabaseIdentity(), proposedField: field);

    private RelationAutoPreview AutoValueFailure(DbField field, RelationPropertyValue value, string location)
    {
        if (value.FieldType.Length == 0 && value.Display == RelationProperty.NoAccess)
            return AutoFailure(RelationAutoStatus.NoAccess, field, "Нет доступа к зависимому свойству. Прежние связи сохранены.");
        if (value.FieldType.Length == 0 && value.Display is RelationProperty.Unavailable or RelationProperty.UnknownBase or "(вычисление недоступно)")
            return AutoFailure(RelationAutoStatus.Unavailable, field, "Зависимую базу не удалось прочитать. Прежние связи сохранены.");
        return AutoFailure(RelationAutoStatus.InvalidRule, field, $"В {location} свойстве есть недоступное значение или значение другого типа.");
    }

    private sealed record AutoConnection(int DbId, string Path, PermissionService? Permissions);
    private sealed record AutoConnections(AutoConnection Source, IReadOnlyDictionary<int, AutoConnection> Targets);

    // Снимок адресов/прав снимается вызывающим UI до Task.Run. Неизвестные базы в фоне не монтируются.
    private AutoConnections CaptureAutoConnections(DbField field)
    {
        var ownId = RelationDbResolver.DbIdFor(this);
        // Scoped-окружение имеет собственное пространство id; одноименные записи глобального
        // Workspace не могут подменять его базу, даже если приложение ранее открывало другую.
        var connections = RelationDbResolver.HasScopedResolver
            ? new Dictionary<int, AutoConnection>()
            : RelationDbResolver.MountedReposExcept(ownId)
                .ToDictionary(p => p.DbId, p => new AutoConnection(p.DbId, p.Repo.DbPath, p.Repo.Permissions));
        var cfg = RelationConfig.FromConfig(field.ConfigJson);
        if (cfg is { TargetDbId: > 0 } && RelationDbResolver.MountedRepoFor(cfg.TargetDbId) is { } target)
            connections[cfg.TargetDbId] = new(cfg.TargetDbId, target.DbPath, target.Permissions);
        if (ownId > 0) connections[ownId] = new(ownId, DbPath, Permissions);
        // В окне все доступные адреса уже находятся в mounted descriptors. Перед каждым
        // поиском читаем только их: каталог сетевой базы нельзя открывать до Task.Run.
        if (!RelationDbResolver.HasScopedResolver)
            return new(new(ownId, DbPath, Permissions), connections);
        // Headless/scoped-резолвер не обязан иметь Workspace. Собираем также адреса целей из
        // конфигов, используя собственный каталог: общий контекст мог только что завершить tx.
        var queue = new Queue<AutoConnection>();
        queue.Enqueue(new(ownId, DbPath, Permissions));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0 && visited.Count < 64)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Path)) continue;
            try
            {
                using var catalog = new AppDbContext(current.Path);
                var fields = catalog.DbFields.AsNoTracking().Where(f => f.FieldType == "relation")
                    .Select(f => new { f.CollectionId, f.ConfigJson }).ToList();
                foreach (var row in fields)
                {
                    if (current.Permissions?.Can(PermAction.ViewCollection, row.CollectionId) == false
                        || RelationConfig.FromConfig(row.ConfigJson) is not { TargetDbId: > 0 } relation) continue;
                    if (connections.TryGetValue(relation.TargetDbId, out var known))
                    {
                        queue.Enqueue(known);
                        continue;
                    }
                    var linked = RelationDbResolver.MountedRepoFor(relation.TargetDbId);
                    if (linked is null) continue;
                    var connection = new AutoConnection(relation.TargetDbId, linked.DbPath, linked.Permissions);
                    connections[relation.TargetDbId] = connection;
                    queue.Enqueue(connection);
                }
            }
            catch (DbException) { /* реальное чтение вернет Unavailable, прежние ребра сохранятся */ }
            catch (IOException) { /* недоступную вложенную базу в фоне не монтируем */ }
        }
        return new(new(ownId, DbPath, Permissions), connections);
    }

    private static T WithAutoConnections<T>(AutoConnections connections, Func<ConstructorRepository, T> action)
    {
        using var source = new AppDbContext(connections.Source.Path);
        var repo = new ConstructorRepository(source) { Permissions = connections.Source.Permissions };
        var contexts = new Dictionary<int, AppDbContext>();
        var repositories = new Dictionary<int, ConstructorRepository>();
        ConstructorRepository? Resolve(int dbId)
        {
            if (dbId == connections.Source.DbId) return repo;
            if (repositories.TryGetValue(dbId, out var ready)) return ready;
            if (!connections.Targets.TryGetValue(dbId, out var connection)) return null;
            if (connection.Path == connections.Source.Path) return repo;
            var context = new AppDbContext(connection.Path);
            contexts[dbId] = context;
            return repositories[dbId] = new ConstructorRepository(context) { Permissions = connection.Permissions };
        }
        using var resolver = RelationDbResolver.UseScopedResolver(Resolve, candidate => ReferenceEquals(candidate, repo)
            ? connections.Source.DbId
            : repositories.FirstOrDefault(p => ReferenceEquals(p.Value, candidate)).Key);
        try { return action(repo); }
        finally { foreach (var context in contexts.Values) context.Dispose(); }
    }
}
