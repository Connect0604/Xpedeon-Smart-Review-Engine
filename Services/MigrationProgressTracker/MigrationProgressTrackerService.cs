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
    private const string OrchestratorDashboardStoryQuery = "[System.WorkItemType] = 'User Story' AND [System.Tags] CONTAINS 'AI development with revised orchestration'";
    private const string MissingStoryReason = "No matching DevOps story title";
    private const string MissingPageReason = "Matched story found, but PAGE_NAME is blank";
    private const string MissingPhaseUpdatedReason = "Matched form is completed, but OrchestratorPhaseUpdated is missing";
    private const string MissingLegacyStepReason = "DevOps story exists in orchestrator tracker, but no legacy STEP_NAME matches";

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

        var orchestratorStories = await azureDevOpsService.GetStoriesWithAttachmentsAsync(
            devOpsOptions.Organization,
            devOpsOptions.Project,
            devOpsOptions.PatToken,
            OrchestratorDashboardStoryQuery,
            cancellationToken,
            includeRevisionMetadata: false);

        return BuildDashboard(legacyRows, stories, orchestratorStories);
    }

    private static async Task<List<LegacyInventoryRow>> LoadLegacyInventoryAsync(string connectionString, CancellationToken cancellationToken)
    {
        const string sql = """
SELECT
    steps.STEP_TYPE,
    steps.PROCESS_CODE,
    steps.STEP_CODE,
    steps.STEP_NAME,
    steps.PAGE_NAME,
    steps.MICRO_FRONTEND_NAME,
    steps.FORM_NAME,
    ISNULL(inventory.PROCESS_NAME, '') AS PROCESS_NAME
FROM dbo.PC_PROCESS_STEPS_DEFAULT steps
LEFT JOIN dbo.PC_PROCESS_INVENTORY inventory
    ON inventory.PROCESS_CODE = steps.PROCESS_CODE
WHERE steps.PROCESS_CODE NOT IN
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
        var microFrontendNameOrdinal = reader.GetOrdinal("MICRO_FRONTEND_NAME");
        var formNameOrdinal = reader.GetOrdinal("FORM_NAME");
        var processNameOrdinal = reader.GetOrdinal("PROCESS_NAME");

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyInventoryRow(
                reader.IsDBNull(stepTypeOrdinal) ? string.Empty : reader.GetString(stepTypeOrdinal),
                reader.IsDBNull(processCodeOrdinal) ? string.Empty : reader.GetString(processCodeOrdinal),
                reader.IsDBNull(stepCodeOrdinal) ? string.Empty : reader.GetString(stepCodeOrdinal),
                reader.IsDBNull(stepNameOrdinal) ? string.Empty : reader.GetString(stepNameOrdinal),
                reader.IsDBNull(pageNameOrdinal) ? null : reader.GetString(pageNameOrdinal),
                reader.IsDBNull(microFrontendNameOrdinal) ? null : reader.GetString(microFrontendNameOrdinal),
                reader.IsDBNull(formNameOrdinal) ? null : reader.GetString(formNameOrdinal),
                reader.IsDBNull(processNameOrdinal) ? string.Empty : reader.GetString(processNameOrdinal)));
        }

        return rows;
    }

    private static MigrationProgressTrackerViewModel BuildDashboard(
        List<LegacyInventoryRow> legacyRows,
        List<DevOpsStoryItem> stories,
        List<DevOpsStoryItem>? orchestratorStories = null)
    {
        var storyMatches = stories
            .GroupBy(s => NormalizeKey(s.Title))
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).First(), StringComparer.OrdinalIgnoreCase);

        var legacyStepNames = legacyRows
            .Select(row => NormalizeKey(row.StepName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var legacyRowsByStepName = legacyRows
            .GroupBy(row => NormalizeKey(row.StepName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var diagnostics = legacyRows.Select(row =>
        {
            storyMatches.TryGetValue(NormalizeKey(row.StepName), out var story);
            return BuildExclusionDiagnostic(
                row,
                story,
                legacyRowsByStepName.TryGetValue(NormalizeKey(row.StepName), out var relatedRows) ? relatedRows : [row]);
        })
        .Where(diagnostic => diagnostic is not null)
        .Cast<MigrationProgressExclusionDiagnostic>()
        .Concat(BuildUnmatchedStoryDiagnostics(orchestratorStories ?? stories, legacyRowsByStepName))
        .OrderBy(diagnostic => StepTypeSortKey(diagnostic.StepType))
        .ThenBy(diagnostic => diagnostic.ProcessCode)
        .ThenBy(diagnostic => diagnostic.StepCode)
        .ThenBy(diagnostic => diagnostic.MatchedStoryTitle)
        .ToList();

        var overviewSourceItems = legacyRows.Select(row =>
        {
            storyMatches.TryGetValue(NormalizeKey(row.StepName), out var story);
            var isCompleted = story is not null && !string.IsNullOrWhiteSpace(row.PageName);
            return new MigrationProgressOverviewSourceItem(
                row.StepType,
                row.ProcessCode,
                isCompleted ? story?.OrchestratorPhaseUpdated : null);
        }).ToList();

        var items = legacyRows.Select(row =>
        {
            storyMatches.TryGetValue(NormalizeKey(row.StepName), out var story);
            var isCompleted = story is not null && !string.IsNullOrWhiteSpace(row.PageName);
            return new MigrationProgressItem(
                row.StepType,
                row.ProcessCode,
                row.ProcessName,
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
                    g.Select(i => i.ProcessName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty,
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
            diagnostics,
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

    private static MigrationProgressExclusionDiagnostic? BuildExclusionDiagnostic(
        LegacyInventoryRow row,
        DevOpsStoryItem? story,
        IReadOnlyCollection<LegacyInventoryRow> sameNameRows)
    {
        var matchDetail = BuildMatchDetail(row, sameNameRows);

        if (story is null)
        {
            return new MigrationProgressExclusionDiagnostic(
                null,
                row.StepType,
                row.ProcessCode,
                row.StepCode,
                row.StepName,
                row.PageName,
                row.MicroFrontendName,
                row.FormName,
                null,
                null,
                null,
                null,
                MissingStoryReason,
                matchDetail);
        }

        if (string.IsNullOrWhiteSpace(row.PageName))
        {
            return new MigrationProgressExclusionDiagnostic(
                story.Id,
                row.StepType,
                row.ProcessCode,
                row.StepCode,
                row.StepName,
                row.PageName,
                row.MicroFrontendName,
                row.FormName,
                story.Title,
                story.WorkItemUrl,
                story.State,
                story.OrchestratorPhaseUpdated,
                MissingPageReason,
                matchDetail);
        }

        if (story.OrchestratorPhaseUpdated is null)
        {
            return new MigrationProgressExclusionDiagnostic(
                story.Id,
                row.StepType,
                row.ProcessCode,
                row.StepCode,
                row.StepName,
                row.PageName,
                row.MicroFrontendName,
                row.FormName,
                story.Title,
                story.WorkItemUrl,
                story.State,
                null,
                MissingPhaseUpdatedReason,
                matchDetail);
        }

        return null;
    }

    private static IEnumerable<MigrationProgressExclusionDiagnostic> BuildUnmatchedStoryDiagnostics(
        IEnumerable<DevOpsStoryItem> orchestratorStories,
        IReadOnlyDictionary<string, List<LegacyInventoryRow>> legacyRowsByStepName)
    {
        var legacyStepNames = legacyRowsByStepName.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var story in orchestratorStories
                     .GroupBy(story => NormalizeKey(story.Title))
                     .Select(group => group.OrderBy(story => story.Id).First())
                     .OrderBy(story => story.Title, StringComparer.OrdinalIgnoreCase))
        {
            var normalizedTitle = NormalizeKey(story.Title);
            if (string.IsNullOrWhiteSpace(normalizedTitle) || legacyStepNames.Contains(normalizedTitle))
            {
                continue;
            }

            var singularPluralCandidates = legacyRowsByStepName
                .Where(pair => NormalizeLooseKey(pair.Key) == NormalizeLooseKey(normalizedTitle))
                .SelectMany(pair => pair.Value)
                .ToList();

            yield return new MigrationProgressExclusionDiagnostic(
                story.Id,
                "-",
                "-",
                "-",
                story.Title,
                null,
                null,
                null,
                story.Title,
                story.WorkItemUrl,
                story.State,
                story.OrchestratorPhaseUpdated,
                MissingLegacyStepReason,
                singularPluralCandidates.Count == 0
                    ? null
                    : $"No exact STEP_NAME match. Similar legacy names: {string.Join("; ", singularPluralCandidates.Select(FormatLegacyRowRef))}");
        }
    }

    private static string? BuildMatchDetail(LegacyInventoryRow row, IReadOnlyCollection<LegacyInventoryRow> sameNameRows)
    {
        if (sameNameRows.Count <= 1)
        {
            return null;
        }

        var alternatives = sameNameRows
            .Where(candidate =>
                !string.Equals(candidate.ProcessCode, row.ProcessCode, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.StepCode, row.StepCode, StringComparison.OrdinalIgnoreCase))
            .Select(FormatLegacyRowRef)
            .ToList();

        if (alternatives.Count == 0)
        {
            return null;
        }

        return $"Title-only match. The same STEP_NAME also exists in: {string.Join("; ", alternatives)}";
    }

    private static string FormatLegacyRowRef(LegacyInventoryRow row)
    {
        var mfe = string.IsNullOrWhiteSpace(row.MicroFrontendName) ? "No MFE" : row.MicroFrontendName;
        var form = string.IsNullOrWhiteSpace(row.FormName) ? "No Form" : row.FormName;
        return $"{row.StepType}/{row.ProcessCode}/{row.StepCode} ({mfe}, {form})";
    }

    private static string NormalizeLooseKey(string value)
    {
        var normalized = NormalizeKey(value);
        if (normalized.EndsWith("IES", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[..^3] + "Y";
        }

        return normalized.EndsWith("S", StringComparison.OrdinalIgnoreCase) && normalized.Length > 1
            ? normalized[..^1]
            : normalized;
    }

    private static int StepTypeSortKey(string stepType)
    {
        var index = Array.FindIndex(
            StepTypeOrder,
            ordered => string.Equals(ordered, stepType, StringComparison.OrdinalIgnoreCase));

        return index >= 0 ? index : int.MaxValue;
    }

    internal static MigrationProgressTrackerViewModel BuildDashboardForTests(
        List<LegacyInventoryRow> legacyRows,
        List<DevOpsStoryItem> stories,
        List<DevOpsStoryItem>? orchestratorStories = null) =>
        BuildDashboard(legacyRows, stories, orchestratorStories);
}
