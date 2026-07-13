namespace SmartReviewSystem.Models.DevOps;

internal sealed class DevOpsStoryItem
{
    public int Id { get; init; }
    public string WorkItemType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public string TeamMember { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public string OrchestratorPhase { get; init; } = string.Empty;
    public DateTimeOffset? OrchestratorPhaseUpdated { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? CompletionDate { get; set; }
    public string Mfe { get; init; } = string.Empty;
    public string ExecutionMode { get; init; } = string.Empty;
    public string? BranchName { get; init; }
    public string WorkItemUrl { get; init; } = string.Empty;
    public int BugCount { get; set; }
    public List<DevOpsAttachmentItem> Attachments { get; init; } = new();

    /// <summary>
    /// Tracks whether implementation details (revisions, timestamps) have been loaded for this story.
    /// Used for lazy-loading to improve initial page load performance.
    /// </summary>
    public bool ImplementationDetailsLoaded { get; set; }

    /// <summary>
    /// Gets the total implementation time (days, hours, minutes) from orchestrator start to completion.
    /// </summary>
    public TimeSpan? ImplementationDuration => StartDate is not null && CompletionDate is not null
        ? CompletionDate.Value - StartDate.Value
        : null;

    /// <summary>
    /// The implementation cost extracted from comments (e.g., "Total Claude cost: $65.86")
    /// </summary>
    public string? ImplementationCost { get; set; }

    /// <summary>
    /// The orchestrator comment timestamp for the pre-flight checks message.
    /// Used only for display in the tracker grid.
    /// </summary>
    public DateTimeOffset? ImplementationStartedAt { get; set; }

    /// <summary>
    /// The orchestrator comment timestamp for the implementation complete message.
    /// Used only for display in the tracker grid.
    /// </summary>
    public DateTimeOffset? ImplementationEndedAt { get; set; }

    /// <summary>
    /// The phase history summary loaded on-demand from orchestrator comments.
    /// Contains per-phase execution events, timings, and error tracking.
    /// </summary>
    public PhaseHistorySummary? PhaseHistorySummary { get; set; }

    /// <summary>
    /// Tracks whether phase history has been loaded for this story.
    /// Used for lazy-loading to avoid repeated Azure DevOps calls.
    /// </summary>
    public bool PhaseHistoryLoaded { get; set; }
}
