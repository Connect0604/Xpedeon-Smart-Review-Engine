namespace SmartReviewSystem.Models.DevOps;

internal sealed class BugTrackerRow
{
    public int StoryId { get; init; }
    public string StoryTitle { get; init; } = string.Empty;
    public string StoryWorkItemUrl { get; init; } = string.Empty;
    public int BugId { get; init; }
    public string BugTitle { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string BugWorkItemUrl { get; init; } = string.Empty;
    public string StoryGroupKey => $"{StoryId}|||{StoryTitle}";
}
