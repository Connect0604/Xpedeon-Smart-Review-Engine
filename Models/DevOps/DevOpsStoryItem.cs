namespace SmartReviewSystem.Models.DevOps;

internal sealed class DevOpsStoryItem
{
    public int Id { get; init; }
    public string WorkItemType { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public List<DevOpsAttachmentItem> Attachments { get; init; } = new();
}
