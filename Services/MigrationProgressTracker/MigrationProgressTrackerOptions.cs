namespace SmartReviewSystem.Services.MigrationProgressTracker;

internal sealed class MigrationProgressTrackerOptions
{
    public string ConnectionStringName { get; set; } = "MigrationProgressTracker";
    public string AzureDevOpsStoryQuery { get; set; } = "[System.WorkItemType] = 'User Story' AND [System.State] <> 'Closed'";
}
