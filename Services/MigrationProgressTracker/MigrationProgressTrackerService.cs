using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmartReviewSystem.Models;
using SmartReviewSystem.Models.DevOps;
using SmartReviewSystem.Services.DevOps;

namespace SmartReviewSystem.Services.MigrationProgressTracker;

internal sealed class MigrationProgressTrackerService(
    IConfiguration configuration,
    IAzureDevOpsService azureDevOpsService) : IMigrationProgressTrackerService
{
    private static readonly string[] StepTypeOrder = ["M", "D", "R"];
    private static readonly string[] ExcludedProcessCodes = ["BI", "EINVOICING", "PROPERTYSALES", "PROPERTYSALESMASTER"];

    public async Task<MigrationProgressTrackerViewModel> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection("MigrationProgressTracker").Get<MigrationProgressTrackerOptions>() ?? new MigrationProgressTrackerOptions();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Missing connection string '{options.ConnectionStringName}'.");
        }

        var legacyRows = await LoadLegacyInventoryAsync(connectionString, cancellationToken);
        var devOpsOptions = configuration.GetSection("DevOps").Get<DevOpsOptions>() ?? new DevOpsOptions();
        var stories = await azureDevOpsService.GetStoriesWithAttachmentsAsync(
            devOpsOptions.Organization,
            devOpsOptions.Project,
            devOpsOptions.PatToken,
            options.AzureDevOpsStoryQuery,
            cancellationToken,
            includeRevisionMetadata: false);

        return BuildDashboard(legacyRows, stories);
    }

    private static async Task<List<LegacyInventoryRow>> LoadLegacyInventoryAsync(string connectionString, CancellationToken cancellationToken)
    {
        const string sql = """
SELECT
    STEP_TYPE,
    PROCESS_CODE,
    STEP_CODE,
    STEP_NAME,
    PAGE_NAME
FROM dbo.PC_PROCESS_STEPS_DEFAULT
WHERE PROCESS_CODE NOT IN
(
    'BI',
    'EINVOICING',
    'PROPERTYSALES',
    'PROPERTYSALESMASTER'
)
""";

        var rows = new List<LegacyInventoryRow>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var stepTypeOrdinal = reader.GetOrdinal("STEP_TYPE");
        var processCodeOrdinal = reader.GetOrdinal("PROCESS_CODE");
        var stepCodeOrdinal = reader.GetOrdinal("STEP_CODE");
        var stepNameOrdinal = reader.GetOrdinal("STEP_NAME");
        var pageNameOrdinal = reader.GetOrdinal("PAGE_NAME");

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyInventoryRow(
                reader.IsDBNull(stepTypeOrdinal) ? string.Empty : reader.GetString(stepTypeOrdinal),
                reader.IsDBNull(processCodeOrdinal) ? string.Empty : reader.GetString(processCodeOrdinal),
                reader.IsDBNull(stepCodeOrdinal) ? string.Empty : reader.GetString(stepCodeOrdinal),
                reader.IsDBNull(stepNameOrdinal) ? string.Empty : reader.GetString(stepNameOrdinal),
                reader.IsDBNull(pageNameOrdinal) ? null : reader.GetString(pageNameOrdinal)));
        }

        return rows;
    }

    private static MigrationProgressTrackerViewModel BuildDashboard(List<LegacyInventoryRow> legacyRows, List<DevOpsStoryItem> stories)
    {
        var storyMatches = stories
            .GroupBy(s => NormalizeKey(s.Title))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First(), StringComparer.OrdinalIgnoreCase);

        var overviewSourceItems = legacyRows.Select(row =>
        {
            storyMatches.TryGetValue(NormalizeKey(row.StepName), out var story);
            return new MigrationProgressOverviewSourceItem(
                row.StepType,
                row.ProcessCode,
                story?.OrchestratorPhaseUpdated);
        }).ToList();

        var items = legacyRows.Select(row =>
        {
            storyMatches.TryGetValue(NormalizeKey(row.StepName), out var story);
            var isCompleted = story is not null && !string.IsNullOrWhiteSpace(row.PageName);
            return new MigrationProgressItem(
                row.StepType,
                row.ProcessCode,
                row.StepCode,
                row.StepName,
                row.PageName,
                isCompleted ? "Completed" : "Pending",
                story?.Title,
                story?.WorkItemUrl);
        }).ToList();

        var stepTypeSummaries = StepTypeOrder
            .Select(stepType => BuildStepTypeSummary(stepType, items))
            .Concat(items
                .Select(i => i.StepType)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(stepType => StepTypeOrder.All(ordered => !string.Equals(ordered, stepType, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(stepType => stepType)
                .Select(stepType => BuildStepTypeSummary(stepType, items)))
            .ToList();

        var processSummaries = items
            .GroupBy(i => i.ProcessCode, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var groupedStepTypes = g
                    .GroupBy(i => i.StepType, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(grp => Array.IndexOf(StepTypeOrder, grp.Key.ToUpperInvariant()))
                    .ThenBy(grp => grp.Key)
                    .Select(stepGroup =>
                        BuildStepTypeSummary(stepGroup.Key, stepGroup.ToList(), processFilter: g.Key))
                    .ToList();

                return new MigrationProgressProcessSummary(
                    g.Key,
                    g.Count(),
                    g.Count(i => string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase)),
                    g.Count(i => string.Equals(i.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
                    GetPercentage(g.Count(i => string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase)), g.Count()),
                    groupedStepTypes);
            })
            .ToList();

        var overview = MigrationProgressOverviewBuilder.Build(
            overviewSourceItems,
            ExcludedProcessCodes,
            DateTimeOffset.UtcNow);

        var totalLegacy = overviewSourceItems.Count;
        var totalCompleted = overviewSourceItems.Count(item => item.CompletedAt is not null);

        return new MigrationProgressTrackerViewModel(
            totalLegacy,
            totalCompleted,
            totalLegacy - totalCompleted,
            GetPercentage(totalCompleted, totalLegacy),
            ExcludedProcessCodes.OrderBy(code => code).ToList(),
            overview,
            stepTypeSummaries,
            processSummaries);
    }

    private static string NormalizeKey(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static MigrationProgressStepTypeSummary BuildStepTypeSummary(string stepType, IReadOnlyCollection<MigrationProgressItem> items, string? processFilter = null)
    {
        var filtered = items.Where(i =>
            string.Equals(i.StepType, stepType, StringComparison.OrdinalIgnoreCase) &&
            (processFilter is null || string.Equals(i.ProcessCode, processFilter, StringComparison.OrdinalIgnoreCase)));

        var list = filtered.ToList();
        return new MigrationProgressStepTypeSummary(
            stepType,
            list.Count,
            list.Count(i => string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase)),
            list.Count(i => string.Equals(i.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
            GetPercentage(list.Count(i => string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase)), list.Count),
            list.GroupBy(i => i.ProcessCode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key)
                .Select(g => new MigrationProgressGroup(
                    g.Key,
                    g.Key,
                    g.Count(),
                    g.Count(i => string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase)),
                    g.Count(i => string.Equals(i.Status, "Pending", StringComparison.OrdinalIgnoreCase)),
                    GetPercentage(g.Count(i => string.Equals(i.Status, "Completed", StringComparison.OrdinalIgnoreCase)), g.Count()),
                    g.OrderBy(i => i.StepCode).ToList()))
                .ToList());
    }

    private static decimal GetPercentage(int completed, int total) =>
        total <= 0 ? 0 : Math.Round((decimal)completed * 100m / total, 1);

    internal static MigrationProgressTrackerViewModel BuildDashboardForTests(List<LegacyInventoryRow> legacyRows, List<DevOpsStoryItem> stories) =>
        BuildDashboard(legacyRows, stories);
}
