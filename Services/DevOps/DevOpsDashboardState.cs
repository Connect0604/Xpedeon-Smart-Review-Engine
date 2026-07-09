using SmartReviewSystem.Models.DevOps;

namespace SmartReviewSystem.Services.DevOps;

internal sealed class DevOpsDashboardState
{
    public List<DevOpsStoryItem> Stories { get; private set; } = new();
    public List<StoryBugGroup> BugGroups { get; private set; } = new();

    public string ConnectionStatus { get; private set; } = "Idle";

    public string LoadError { get; private set; } = string.Empty;

    public bool HasLoadedOnce { get; private set; }

    public bool HasStories => Stories.Count > 0;

    public void SetStories(IEnumerable<DevOpsStoryItem> stories, string connectionStatus = "Connected", string loadError = "", IEnumerable<StoryBugGroup>? bugGroups = null)
    {
        Stories = stories.ToList();
        BugGroups = bugGroups?.ToList() ?? new List<StoryBugGroup>();
        ConnectionStatus = connectionStatus;
        LoadError = loadError;
        HasLoadedOnce = true;
    }

    public void MarkLoadAttempt(string connectionStatus = "Idle", string loadError = "")
    {
        Stories = new List<DevOpsStoryItem>();
        BugGroups = new List<StoryBugGroup>();
        ConnectionStatus = connectionStatus;
        LoadError = loadError;
        HasLoadedOnce = true;
    }
}
