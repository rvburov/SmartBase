using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    public Task<RelationIntegrityRepairPlan> PreviewRelationIntegrityRepairAsync(RelationIntegrityReport report,
        IReadOnlyCollection<string>? issueKeys = null, CancellationToken ct = default)
    {
        var selected = issueKeys?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            ?? report.Issues.Where(i => i.CanRepair).Select(i => i.Key).Order(StringComparer.Ordinal).ToArray();
        if (db.Database.CurrentTransaction is not null)
            return Task.FromResult(IntegrityPlanFailure(report, RelationIntegrityStatus.Conflict,
                "Завершите текущую операцию перед исправлением связей."));
        var connections = CaptureIntegrityConnections();
        return Task.Run(() => WithIntegrityConnections(connections, repo =>
            repo.PreviewIntegrityRepairCore(report, selected, ct)));
    }

    public async Task<RelationIntegrityRepairResult> ApplyRelationIntegrityRepairAsync(RelationIntegrityRepairPlan plan,
        Func<RelationIntegrityBackupRequest, CancellationToken, Task<RelationIntegrityBackup>> createBackup,
        CancellationToken ct = default)
    {
        if (!plan.CanApply || db.Database.CurrentTransaction is not null
            || plan.SourceDatabaseIdentity != RelationDatabaseIdentity
            || plan.SourceDbId != RelationDbResolver.DbIdFor(this))
            return new(RelationIntegrityStatus.InvalidPlan, 0, "План недействителен. Выполните проверку и подтверждение заново.");
        if (createBackup is null)
            return new(RelationIntegrityStatus.BackupFailed, 0, "Служба резервного копирования недоступна. Данные не изменялись.");
        var connections = CaptureIntegrityConnections();
        RelationIntegrityBackup? backup = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var initial = await Task.Run(() => WithIntegrityConnections(connections,
                repo => repo.ValidateIntegrityPlan(plan, ct)));
            if (initial is not null) return initial;
            // Ни scoped resolver, ни транзакция не переживают await. Копия создается и
            // проверяется службой до открытия транзакции исправления.
            var request = new RelationIntegrityBackupRequest(plan.SourceDbId, plan.SourceDatabaseIdentity,
                plan.Fingerprint, IsPostgres ? "postgres" : "sqlite");
            backup = await createBackup(request, ct);
            var validBackup = await Task.Run(() => ValidateIntegrityBackup(plan, request, backup, ct));
            if (!validBackup)
                return new(RelationIntegrityStatus.BackupFailed, 0, "Резервная копия не прошла проверку. Данные не изменялись.");
            var result = await Task.Run(() => WithIntegrityConnections(connections,
                repo => repo.ApplyIntegrityRepairCore(plan, backup, ct), writable: true));
            if (result.Applied)
            {
                try
                {
                    foreach (var fieldId in plan.Issues.Where(i => i.FieldId.HasValue).Select(i => i.FieldId!.Value).Distinct())
                        DetachAutoRelationTracking(fieldId);
                    foreach (var cell in plan.Issues.Where(i => i.CollectionId.HasValue && i.FieldId.HasValue)
                                 .Select(i => (Collection: i.CollectionId!.Value, Field: i.FieldId!.Value)).Distinct())
                        NotifyRelationDataChanged(cell.Collection, cell.Field);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Коммит уже состоялся. Закрытое окно или контекст не меняют его результат.
                    Serilog.Log.Warning("Связи исправлены, но обновление открытого представления отложено");
                }
            }
            return result;
        }
        catch (OperationCanceledException)
        { return new(RelationIntegrityStatus.Cancelled, 0, "Исправление отменено. Данные не изменялись.", backup); }
        catch (Exception ex) when (ex is DbException or DbUpdateException or IOException or UnauthorizedAccessException)
        { return new(backup is null ? RelationIntegrityStatus.BackupFailed : RelationIntegrityStatus.Unavailable,
            0, "Не удалось безопасно завершить исправление. Изменения связей отменены.", backup); }
        catch (InvalidOperationException)
        { return new(backup is null ? RelationIntegrityStatus.BackupFailed : RelationIntegrityStatus.Conflict,
            0, "Условия операции изменились. Выполните проверку заново.", backup); }
    }

    private RelationIntegrityRepairPlan PreviewIntegrityRepairCore(RelationIntegrityReport report,
        IReadOnlyList<string> keys, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (report.Status != RelationIntegrityStatus.Completed || report.SourceDatabaseIdentity != RelationDatabaseIdentity
                || report.SourceDbId != RelationDbResolver.DbIdFor(this) || report.Fingerprint.Length == 0)
                return IntegrityPlanFailure(report, RelationIntegrityStatus.InvalidPlan, "Отчет недействителен. Выполните проверку заново.");
            var byKey = report.Issues.ToDictionary(i => i.Key, StringComparer.Ordinal);
            if (keys.Count == 0 || keys.Any(k => !byKey.TryGetValue(k, out var issue) || !issue.CanRepair || !issue.IsConfirmed))
                return IntegrityPlanFailure(report, RelationIntegrityStatus.InvalidPlan, "Выберите подтвержденные проблемы, допускающие безопасное исправление.");
            var fresh = ScanRelationIntegrityCore(report.Options, null, ct);
            if (fresh.Status != RelationIntegrityStatus.Completed)
                return IntegrityPlanFailure(report, fresh.Status, fresh.Message);
            if (fresh.Fingerprint != report.Fingerprint)
                return IntegrityPlanFailure(report, RelationIntegrityStatus.Conflict, "Данные изменились. Выполните проверку заново.");
            var selected = keys.Select(k => byKey[k]).ToArray();
            return new(RelationIntegrityStatus.Completed, report.SourceDbId, report.SourceDatabaseIdentity,
                IntegrityPlanFingerprint(report.Fingerprint, keys), report.Fingerprint, selected,
                $"Будет удалено недействительных или повторяющихся ребер: {selected.Select(i => i.EdgeId).Distinct().Count()}. Остальные связи сохранятся.", report.Options);
        }
        catch (OperationCanceledException) { return IntegrityPlanFailure(report, RelationIntegrityStatus.Cancelled, "Подготовка отменена."); }

        catch (Exception ex) when (ex is DbException or IOException)
        { return IntegrityPlanFailure(report, RelationIntegrityStatus.Unavailable, "База недоступна. План не подготовлен."); }
    }

    private RelationIntegrityRepairResult? ValidateIntegrityPlan(RelationIntegrityRepairPlan plan, CancellationToken ct,
        IntegrityLocks? locks = null)
    {
        var fresh = ScanRelationIntegrityCore(plan.Options, null, ct, locks);
        if (fresh.Status != RelationIntegrityStatus.Completed)
            return new(fresh.Status, 0, fresh.Message);
        if (fresh.Fingerprint != plan.SourceFingerprint || plan.SourceDatabaseIdentity != RelationDatabaseIdentity
            || plan.SourceDbId != RelationDbResolver.DbIdFor(this)
            || IntegrityPlanFingerprint(fresh.Fingerprint, plan.Issues.Select(i => i.Key)) != plan.Fingerprint)
            return new(RelationIntegrityStatus.Conflict, 0, "Данные или настройки изменились. Выполните проверку заново.");
        var current = fresh.Issues.ToDictionary(i => i.Key, StringComparer.Ordinal);
        if (plan.Issues.Any(i => !current.TryGetValue(i.Key, out var issue) || !issue.CanRepair || !issue.IsConfirmed || issue.EdgeId != i.EdgeId))
            return new(RelationIntegrityStatus.Conflict, 0, "Выбранные проблемы больше не подтверждены. Данные не изменялись.");
        return null;
    }

    private RelationIntegrityRepairResult ApplyIntegrityRepairCore(RelationIntegrityRepairPlan plan,
        RelationIntegrityBackup backup, CancellationToken ct)
    {
        using var locks = new IntegrityLocks(this);
        ct.ThrowIfCancellationRequested();
        locks.Acquire(this);
        using var savepoint = new AutoWriteScope(db);
        var invalid = ValidateIntegrityPlan(plan, ct, locks);
        if (invalid is not null) return invalid with { Backup = backup };
        var ids = plan.Issues.Where(i => i.EdgeId.HasValue).Select(i => i.EdgeId!.Value).Distinct().Order().ToArray();
        var removed = 0;
        foreach (var chunk in ids.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = "DELETE FROM db_record_links WHERE Id IN (" + string.Join(',', chunk) + ")";
            removed += command.ExecuteNonQuery();
        }
        if (removed != ids.Length) return new(RelationIntegrityStatus.Conflict, 0,
            "Состав ребер изменился. Изменения отменены, повторите проверку.", backup);
        ct.ThrowIfCancellationRequested();
        Log("repair", "record", "Проверка целостности связей", count: removed,
            detail: JsonSerializer.Serialize(new
            {
                plan = plan.Fingerprint, backup = backup.ContentSha256,
                edges = plan.Issues.Select(i => new { i.EdgeId, i.FieldId, i.RecordId, kind = i.Kind.ToString() }).ToArray(),
            }));
        db.SaveChanges();
        ct.ThrowIfCancellationRequested();
        savepoint.Commit();
        locks.CommitSource();
        return new(RelationIntegrityStatus.Applied, removed, $"Исправлено ребер: {removed}. Резервная копия проверена и сохранена.", backup);
    }

    private static string IntegrityPlanFingerprint(string sourceFingerprint, IEnumerable<string> keys) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { sourceFingerprint, keys = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() }))));

    private static RelationIntegrityRepairPlan IntegrityPlanFailure(RelationIntegrityReport report,
        RelationIntegrityStatus status, string message) =>
        new(status, report.SourceDbId, report.SourceDatabaseIdentity, "", report.Fingerprint, [], message, report.Options);

    private static bool ValidateIntegrityBackup(RelationIntegrityRepairPlan plan, RelationIntegrityBackupRequest request,
        RelationIntegrityBackup? backup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (backup is null || backup.SourceDbId != request.SourceDbId
            || backup.SourceDatabaseIdentity != request.SourceDatabaseIdentity || backup.PlanFingerprint != request.PlanFingerprint
            || backup.Provider != request.Provider || backup.CreatedUtc < plan.CreatedUtc
            || backup.ContentSha256.Length != 64 || string.IsNullOrWhiteSpace(backup.ArtifactPath)) return false;
        try
        {
            using var stream = File.OpenRead(backup.ArtifactPath);
            if (stream.Length == 0) return false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), backup.ContentSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Во время окончательной проверки источник и проверяемые цели не могут изменяться.
    // SQLite Serializable берет write reservation; для PostgreSQL явные табличные замки
    // защищают также отсутствие цели (ее нельзя восстановить между проверкой и удалением).
    private sealed class IntegrityLocks(ConstructorRepository? source = null) : IDisposable
    {
        private readonly List<(ConstructorRepository Repo, IDbContextTransaction Transaction)> _held = [];
        private bool _committed;
        public void Acquire(ConstructorRepository repo)
        {
            if (_held.Any(item => item.Repo.RelationDatabaseIdentity == repo.RelationDatabaseIdentity)) return;
            var tx = repo.AutoContext.Database.BeginTransaction(IsolationLevel.Serializable);
            _held.Add((repo, tx));
            if (repo.IsPostgres)
            {
                var tables = "db_collections, db_fields, db_records, db_record_links, db_users, db_permissions, db_meta";
                repo.AutoContext.Database.ExecuteSqlRaw("LOCK TABLE " + tables + (ReferenceEquals(repo, source)
                    ? " IN SHARE ROW EXCLUSIVE MODE" : " IN SHARE MODE"));
            }
        }
        public void CommitSource()
        {
            var current = _held.Single(item => ReferenceEquals(item.Repo, source));
            current.Transaction.Commit();
            _committed = true;
        }
        public void Dispose()
        {
            foreach (var item in _held.AsEnumerable().Reverse())
            {
                if (!_committed || !ReferenceEquals(item.Repo, source))
                    try { item.Transaction.Rollback(); } catch { /* исходная ошибка важнее отказа отката */ }
                item.Transaction.Dispose();
                if (!_committed && ReferenceEquals(item.Repo, source)) item.Repo.AutoContext.ChangeTracker.Clear();
            }
        }
    }
}
