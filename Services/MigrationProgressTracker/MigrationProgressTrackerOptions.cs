namespace SmartReviewSystem.Services.MigrationProgressTracker;

internal sealed class MigrationProgressTrackerOptions
{
    public string ConnectionStringName { get; set; } = "MigrationProgressTracker";
    public string AzureDevOpsStoryQuery { get; set; } = "[System.WorkItemType] = 'User Story' AND [System.Tags] CONTAINS 'AI development with revised orchestration' AND ([System.State] = 'Testing Requested' OR [System.State] = 'Closed' OR [System.State] = 'Resolved')";
}
