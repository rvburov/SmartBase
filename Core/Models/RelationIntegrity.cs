namespace SmartBase.Core.Models;

public enum RelationIntegrityStatus
{
    Completed, Cancelled, Unavailable, NoAccess, Conflict, BackupFailed, InvalidPlan, Applied,
}

public enum RelationIntegrityIssueKind
{
    MissingSourceRecord, MissingSourceField, MissingSourceCollection, InvalidSourceFieldType, SourceCollectionMismatch,
    MissingTargetRecord, MissingTargetCollection, TargetUnavailable, TargetAccessDenied,
    MissingConfiguration, InvalidConfiguration, MissingDependency, InvalidDependency, DependencyCycle, DependencyUnverified,
    CanonicalDuplicate, SingleCardinality, InvalidSelfLink, InvalidTargetIdentity,
    LegacyMixedTarget, TargetCollectionMismatch, NumericProjectionCollision,
}

public sealed record RelationIntegrityScanOptions(int PageSize = 500, int MaxIssues = 2000);
public sealed record RelationIntegrityProgress(long ScannedEdges, long ScannedFields, long Issues);

/// <summary>Один проверенный факт. Заголовки содержат только доступные пользователю данные.
/// Стабильный ключ относится к конкретному ребру или свойству, а не к его месту в списке.</summary>
public sealed record RelationIntegrityIssue(string Key, RelationIntegrityIssueKind Kind,
    int SourceDbId, string SourceDatabaseIdentity, int? CollectionId, int? FieldId,
    int? RecordId, int? EdgeId, int? TargetDbId, int? TargetRecordId,
    string CollectionName, string FieldName, string RecordTitle, string Description,
    bool IsConfirmed, bool CanRepair, bool IsInformational = false);

public sealed class RelationIntegrityReport
{
    public RelationIntegrityStatus Status { get; }
    public int SourceDbId { get; }
    public string SourceDatabaseIdentity { get; }
    public DateTime CreatedUtc { get; }
    public long ScannedEdges { get; }
    public long ScannedFields { get; }
    public long ConfirmedIssues { get; }
    public long UnverifiedIssues { get; }
    public long InformationalIssues { get; }
    public IReadOnlyList<RelationIntegrityIssue> Issues { get; }
    public IReadOnlyDictionary<RelationIntegrityIssueKind, long> Counts { get; }
    public bool IsTruncated { get; }
    public string Message { get; }
    internal string Fingerprint { get; }
    internal RelationIntegrityScanOptions Options { get; }

    internal RelationIntegrityReport(RelationIntegrityStatus status, int dbId, string identity,
        long edges, long fields, long confirmedIssues, long unverifiedIssues, IReadOnlyList<RelationIntegrityIssue> issues,
        IReadOnlyDictionary<RelationIntegrityIssueKind, long> counts, bool truncated, string message,
        string fingerprint, RelationIntegrityScanOptions options)
    {
        Status = status;
        SourceDbId = dbId;
        SourceDatabaseIdentity = identity;
        CreatedUtc = DateTime.UtcNow;
        ScannedEdges = edges;
        ScannedFields = fields;
        UnverifiedIssues = unverifiedIssues;
        InformationalIssues = counts.Where(p => p.Key is RelationIntegrityIssueKind.LegacyMixedTarget
            or RelationIntegrityIssueKind.NumericProjectionCollision or RelationIntegrityIssueKind.TargetCollectionMismatch).Sum(p => p.Value);
        ConfirmedIssues = Math.Max(0, confirmedIssues - InformationalIssues);
        Issues = Array.AsReadOnly(issues.ToArray());
        Counts = new System.Collections.ObjectModel.ReadOnlyDictionary<RelationIntegrityIssueKind, long>(
            new Dictionary<RelationIntegrityIssueKind, long>(counts));
        IsTruncated = truncated;
        Message = message;
        Fingerprint = fingerprint;
        Options = options;
    }
}

public sealed class RelationIntegrityRepairPlan
{
    public RelationIntegrityStatus Status { get; }
    public bool CanApply => Status == RelationIntegrityStatus.Completed && Issues.Count > 0;
    public int SourceDbId { get; }
    public string SourceDatabaseIdentity { get; }
    public string Fingerprint { get; }
    public DateTime CreatedUtc { get; }
    public IReadOnlyList<RelationIntegrityIssue> Issues { get; }
    public int EdgesToRemove { get; }
    public string Message { get; }
    internal RelationIntegrityScanOptions Options { get; }
    internal string SourceFingerprint { get; }

    internal RelationIntegrityRepairPlan(RelationIntegrityStatus status, int dbId, string identity,
        string fingerprint, string sourceFingerprint, IReadOnlyList<RelationIntegrityIssue> issues,
        string message, RelationIntegrityScanOptions options)
    {
        Status = status;
        SourceDbId = dbId;
        SourceDatabaseIdentity = identity;
        Fingerprint = fingerprint;
        CreatedUtc = DateTime.UtcNow;
        SourceFingerprint = sourceFingerprint;
        Issues = Array.AsReadOnly(issues.ToArray());
        EdgesToRemove = Issues.Where(i => i.EdgeId.HasValue).Select(i => i.EdgeId).Distinct().Count();
        Message = message;
        Options = options;
    }
}

public sealed record RelationIntegrityBackupRequest(int SourceDbId, string SourceDatabaseIdentity,
    string PlanFingerprint, string Provider);

/// <summary>Выдается только службой после создания и проверки настоящей резервной копии.</summary>
public sealed class RelationIntegrityBackup
{
    public int SourceDbId { get; }
    public string SourceDatabaseIdentity { get; }
    public string PlanFingerprint { get; }
    public string Provider { get; }
    public string ArtifactPath { get; }
    public string ContentSha256 { get; }
    public DateTime CreatedUtc { get; }

    internal RelationIntegrityBackup(int sourceDbId, string sourceDatabaseIdentity, string planFingerprint,
        string provider, string artifactPath, string contentSha256, DateTime createdUtc)
    {
        SourceDbId = sourceDbId;
        SourceDatabaseIdentity = sourceDatabaseIdentity;
        PlanFingerprint = planFingerprint;
        Provider = provider;
        ArtifactPath = artifactPath;
        ContentSha256 = contentSha256;
        CreatedUtc = createdUtc;
    }
}

public sealed record RelationIntegrityRepairResult(RelationIntegrityStatus Status, int RemovedEdges,
    string Message, RelationIntegrityBackup? Backup = null)
{
    public bool Applied => Status == RelationIntegrityStatus.Applied;
}
