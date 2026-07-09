namespace SmartReviewSystem.Models.DevOps;

internal sealed class StoryBugGroup
{
    public DevOpsStoryItem Story { get; init; } = new();
    public List<DevOpsBugItem> Bugs { get; init; } = new();
    public int BugCount => Bugs.Count;
}
