namespace SmartReviewSystem.Models.DevOps;

internal sealed class MfeModuleItem
{
    public string ModuleName { get; set; } = string.Empty;
    public int StoriesCount { get; set; }
    public int ActivePrCount { get; set; }
    public bool IsAvailable { get; set; } = true;
}
