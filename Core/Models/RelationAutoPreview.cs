namespace SmartBase.Core.Models;

public enum RelationAutoStatus
{
    Ready, Applied, NoRule, Unavailable, NoAccess, InvalidRule, Ambiguous, Cancelled, Conflict,
}

/// <summary>Изменение одной ячейки, рассчитанное без записи в базу.</summary>
public sealed record RelationAutoChange(int RecordId, string Title,
    IReadOnlyList<RelationTarget> OldTargets, IReadOnlyList<RelationTarget> NewTargets);

/// <summary>Неизменяемый результат предварительного подбора. При применении снимок проверяется
/// повторно: чужая правка не должна быть затерта результатом старого предварительного просмотра.</summary>
public sealed class RelationAutoPreview
{
    public RelationAutoStatus Status { get; }
    public bool CanApply => Status == RelationAutoStatus.Ready;
    public int FieldId { get; }
    public int CollectionId { get; }
    public string ConfigJson { get; }
    public int Added { get; }
    public int Changed { get; }
    public int Removed { get; }
    public int Unmatched { get; }
    public int Ambiguous { get; }
    /// <summary>Записей-источников, чье совпадение отброшено условием выбора записей.</summary>
    public int OutsideCondition { get; }
    /// <summary>Из записей без совпадения — с пустым значением первой пары.</summary>
    public int UnmatchedEmpty { get; }
    /// <summary>Записей, совпавших только без учета регистра и пробелов по краям (считается для окна).</summary>
    public int FoldOnly { get; }
    public int Unavailable => Status is RelationAutoStatus.Unavailable or RelationAutoStatus.NoAccess ? 1 : 0;
    public string Message { get; }
    public IReadOnlyList<RelationAutoChange> Changes { get; }
    internal string Fingerprint { get; }
    internal string DatabaseIdentity { get; }
    internal DbField? ProposedField { get; }

    internal RelationAutoPreview(RelationAutoStatus status, int fieldId, int collectionId,
        string configJson, string message, IReadOnlyList<RelationAutoChange>? changes = null,
        int unmatched = 0, int ambiguous = 0, string fingerprint = "", string databaseIdentity = "",
        DbField? proposedField = null, int outsideCondition = 0, int unmatchedEmpty = 0, int foldOnly = 0)
    {
        Status = status;
        FieldId = fieldId;
        CollectionId = collectionId;
        ConfigJson = configJson;
        Message = message;
        Changes = Array.AsReadOnly((changes ?? []).ToArray());
        Added = Changes.Count(c => c.OldTargets.Count == 0 && c.NewTargets.Count > 0);
        Removed = Changes.Count(c => c.OldTargets.Count > 0 && c.NewTargets.Count == 0);
        Changed = Changes.Count - Added - Removed;
        Unmatched = unmatched;
        Ambiguous = ambiguous;
        OutsideCondition = outsideCondition;
        UnmatchedEmpty = unmatchedEmpty;
        FoldOnly = foldOnly;
        Fingerprint = fingerprint;
        DatabaseIdentity = databaseIdentity;
        ProposedField = proposedField;
    }
}

public sealed record RelationAutoApplyResult(RelationAutoStatus Status, int Changed, string Message)
{
    public bool Applied => Status == RelationAutoStatus.Applied;
}

public sealed class RelationAutoApplyException(RelationAutoStatus status, string message)
    : InvalidOperationException(message)
{
    public RelationAutoStatus Status { get; } = status;
}
