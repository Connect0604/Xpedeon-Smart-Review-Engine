namespace SmartReviewSystem.Models;

internal sealed record LegacyInventoryRow(
    string StepType,
    string ProcessCode,
    string StepCode,
    string StepName,
    string? PageName,
    string? MicroFrontendName,
    string? FormName);

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

internal sealed record MigrationProgressOverviewSourceItem(
    string StepType,
    string ProcessCode,
    DateTimeOffset? CompletedAt);

internal sealed record MigrationProgressExclusionDiagnostic(
    int? StoryId,
    string StepType,
    string ProcessCode,
    string StepCode,
    string StepName,
    string? PageName,
    string? MicroFrontendName,
    string? FormName,
    string? MatchedStoryTitle,
    string? MatchedStoryUrl,
    string? StoryState,
    DateTimeOffset? OrchestratorPhaseUpdated,
    string Reason,
    string? MatchDetail);

internal sealed record MigrationProgressOverviewKpi(
    string Key,
    string Label,
    string Value,
    string SupportingText);

internal sealed record MigrationProgressChartDatum(
    string Argument,
    string Series,
    decimal Value);

internal enum MigrationProgressTrendGranularity
{
    Weekly,
    Monthly,
    Quarterly,
    Yearly
}

internal sealed record MigrationProgressTrendPoint(
    DateTimeOffset BucketStart,
    DateTimeOffset BucketEnd,
    string BucketLabel,
    string TooltipLabel,
    int CompletedCount,
    int MastersCompletedCount,
    int DocumentsCompletedCount,
    int ReportsCompletedCount);

internal sealed record MigrationProgressTrendSeries(
    MigrationProgressTrendGranularity Granularity,
    string Label,
    List<MigrationProgressTrendPoint> Points);

internal sealed record MigrationProgressOverviewStepTypeTile(
    string StepType,
    string Label,
    int Total,
    int Completed,
    int Pending,
    decimal CompletionPercentage,
    string Badge);

internal sealed record MigrationProgressInsight(
    string Key,
    string Label,
    string Value,
    string SupportingText,
    string Tone);

internal sealed record MigrationProgressOverviewViewModel(
    string CompletionHeadline,
    List<MigrationProgressOverviewKpi> Kpis,
    List<MigrationProgressChartDatum> CompletedStepTypeBreakdown,
    List<MigrationProgressChartDatum> PendingStepTypeBreakdown,
    List<MigrationProgressChartDatum> PendingProcessRanking,
    MigrationProgressTrendGranularity DefaultTrendGranularity,
    List<MigrationProgressTrendSeries> CompletionTrends,
    List<MigrationProgressOverviewStepTypeTile> StepTypeTiles,
    List<MigrationProgressInsight> Insights);

internal sealed record MigrationProgressTrackerViewModel(
    int TotalLegacy,
    int TotalCompleted,
    int TotalPending,
    decimal OverallCompletionPercentage,
    List<string> ExcludedProcessCodes,
    MigrationProgressOverviewViewModel Overview,
    List<MigrationProgressExclusionDiagnostic> ExclusionDiagnostics,
    List<MigrationProgressStepTypeSummary> StepTypeSummaries,
    List<MigrationProgressProcessSummary> ProcessSummaries);
