namespace SmartReviewSystem.Models.DevOps;

/// <summary>
/// Represents a single phase execution event in the orchestrator's history.
/// Tracks when a phase started, ended, its status, and any errors encountered.
/// </summary>
internal sealed class PhaseHistoryEvent
{
    public string PhaseName { get; init; } = string.Empty;
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public TimeSpan? Duration => EndTime is not null && StartTime is not null
        ? EndTime.Value - StartTime.Value
        : null;
    public PhaseStatus FinalStatus { get; init; } = PhaseStatus.InProgress;
    public bool HasError { get; init; }
    public DateTimeOffset? ErrorTime { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

/// <summary>
/// Represents the overall execution summary for a story across all phases.
/// Tracks timeline metrics and consolidated error information.
/// </summary>
internal sealed class PhaseHistorySummary
{
    public DateTimeOffset? ExecutionStarted { get; init; }
    public DateTimeOffset? ExecutionCompleted { get; init; }
    public TimeSpan? TotalDuration => ExecutionCompleted is not null && ExecutionStarted is not null
        ? ExecutionCompleted.Value - ExecutionStarted.Value
        : null;
    public string CurrentRunningPhase { get; init; } = string.Empty;
    public string FinalStatus { get; init; } = string.Empty;

    public int TotalPhases { get; init; }
    public int CompletedPhases { get; init; }
    public int ErroredPhases { get; init; }
    public int RecoveredErrors { get; init; }

    /// <summary>
    /// Success rate as percentage (0-100). Phases with errors that later completed count as recovered.
    /// </summary>
    public decimal SuccessRate { get; init; }

    public List<PhaseHistoryEvent> PhaseHistory { get; init; } = new();
}

/// <summary>
/// Enum representing the possible statuses of a phase execution.
/// </summary>
internal enum PhaseStatus
{
    Completed,
    InProgress,
    Error,
    Skipped
}
