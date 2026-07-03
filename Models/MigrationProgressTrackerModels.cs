namespace SmartReviewSystem.Models;

internal sealed record LegacyInventoryRow(
    string StepType,
    string ProcessCode,
    string StepCode,
    string StepName,
    string? PageName);

internal sealed record MigrationProgressStoryMatch(
    int StoryId,
    string Title,
    string WorkItemUrl);

internal sealed record MigrationProgressItem(
    string StepType,
    string ProcessCode,
    string StepCode,
    string StepName,
    string? PageName,
    string Status,
    string? MatchedStoryTitle,
    string? MatchedStoryUrl);

internal sealed record MigrationProgressGroup(
    string Key,
    string Label,
    int Total,
    int Completed,
    int Pending,
    decimal CompletionPercentage,
    List<MigrationProgressItem> Items);

internal sealed record MigrationProgressStepTypeSummary(
    string StepType,
    int Total,
    int Completed,
    int Pending,
    decimal CompletionPercentage,
    List<MigrationProgressGroup> ProcessGroups);

internal sealed record MigrationProgressProcessSummary(
    string ProcessCode,
    int Total,
    int Completed,
    int Pending,
    decimal CompletionPercentage,
    List<MigrationProgressStepTypeSummary> StepTypeSummaries);

internal sealed record MigrationProgressTrackerViewModel(
    int TotalLegacy,
    int TotalCompleted,
    int TotalPending,
    decimal OverallCompletionPercentage,
    List<MigrationProgressStepTypeSummary> StepTypeSummaries,
    List<MigrationProgressProcessSummary> ProcessSummaries);

