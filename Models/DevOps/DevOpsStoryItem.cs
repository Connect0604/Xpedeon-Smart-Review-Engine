namespace SmartReviewSystem.Models.DevOps;

internal sealed class DevOpsStoryItem
{
    public int Id { get; init; }
    public string WorkItemType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public string OrchestratorPhase { get; init; } = string.Empty;
    public DateTimeOffset? OrchestratorPhaseUpdated { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? CompletionDate { get; set; }
    public string Mfe { get; init; } = string.Empty;
    public string ExecutionMode { get; init; } = string.Empty;
    public string WorkItemUrl { get; init; } = string.Empty;
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
}
