using SmartReviewSystem.Models.DevOps;

namespace SmartReviewSystem.Services.DevOps;

internal sealed class DevOpsDashboardState
{
    public List<DevOpsStoryItem> Stories { get; private set; } = new();

    public string ConnectionStatus { get; private set; } = "Idle";

    public string LoadError { get; private set; } = string.Empty;

    public bool HasLoadedOnce { get; private set; }

    public bool HasStories => Stories.Count > 0;

    public void SetStories(IEnumerable<DevOpsStoryItem> stories, string connectionStatus = "Connected", string loadError = "")
    {
        Stories = stories.ToList();
        ConnectionStatus = connectionStatus;
        LoadError = loadError;
        HasLoadedOnce = true;
    }

    public void MarkLoadAttempt(string connectionStatus = "Idle", string loadError = "")
    {
        Stories = new List<DevOpsStoryItem>();
        ConnectionStatus = connectionStatus;
        LoadError = loadError;
        HasLoadedOnce = true;
    }
}
