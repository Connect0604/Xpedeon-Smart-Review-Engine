namespace SmartReviewSystem.Models.DevOps;

internal sealed class DevOpsBugItem
{
    public int Id { get; init; }
    public string WorkItemType { get; init; } = "Bug";
    public string Title { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public int ParentStoryId { get; init; }
    public string WorkItemUrl { get; init; } = string.Empty;
}
