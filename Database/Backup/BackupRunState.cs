namespace SmartBase.Database.Backup;

/// <summary>Результат попытки отдельно от якоря расписания. Running, оставшийся после
/// перезапуска, означает прерванную попытку; LastSuccess меняется только после готовой копии.</summary>
public enum BackupRunOutcome { None, Running, Succeeded, Failed, Interrupted, Canceled }

public sealed record BackupRunState(
    DateTime? LastSuccess = null,
    DateTime? StartedAt = null,
    BackupRunOutcome Outcome = BackupRunOutcome.None,
    string? Error = null);
