namespace SmartReviewSystem.Models.DevOps;

internal sealed class DevOpsOptions
{
    public string Organization { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string PatToken { get; init; } = string.Empty;
    public string WiqlCondition { get; init; } = "[System.WorkItemType] = 'User Story' AND [System.State] <> 'Closed'";
}
