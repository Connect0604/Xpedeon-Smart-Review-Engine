using SmartReviewSystem.Models;

namespace SmartReviewSystem.Services.MigrationProgressTracker;

internal static class MigrationProgressOverviewBuilder
{
    private static readonly string[] StepTypeOrder = ["M", "D", "R"];

    internal static MigrationProgressOverviewViewModel Build(
        IReadOnlyCollection<MigrationProgressOverviewSourceItem> items,
        IReadOnlyCollection<string> excludedProcessCodes,
        DateTimeOffset now)
    {
        var total = items.Count;
        var completed = items.Count(item => item.CompletedAt is not null);
        var pending = total - completed;
        var completionRate = GetPercentage(completed, total);
        var processCodesCovered = items
            .Select(item => item.ProcessCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var completedThisMonth = items.Count(item =>
            item.CompletedAt is not null &&
            item.CompletedAt.Value.Year == now.Year &&
            item.CompletedAt.Value.Month == now.Month);

        var stepTypeTiles = BuildStepTypeTiles(items);
        var topPendingProcesses = BuildPendingProcessRanking(items);
        var completionTrends = BuildCompletionTrends(items);

        var bestStepType = stepTypeTiles
            .OrderByDescending(tile => tile.CompletionPercentage)
            .ThenByDescending(tile => tile.Completed)
            .ThenBy(tile => tile.Label)
            .FirstOrDefault();
        var weakestStepType = stepTypeTiles
            .OrderBy(tile => tile.CompletionPercentage)
            .ThenByDescending(tile => tile.Pending)
            .ThenBy(tile => tile.Label)
            .FirstOrDefault();
        var zeroCompletionProcessCodes = items
            .GroupBy(item => item.ProcessCode, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.All(item => item.CompletedAt is null));

        _ = excludedProcessCodes;

        var kpis = new List<MigrationProgressOverviewKpi>
        {
            new("total-legacy", "Total Legacy", total.ToString(), "Tracked legacy forms"),
            new("completed", "Completed", completed.ToString(), "Reached Testing Requested"),
            new("pending", "Pending", pending.ToString(), "Still before testing handoff"),
            new("completion-rate", "Completion %", $"{completionRate:0.0}%", "Overall completion rate"),
            new("process-codes", "Process Codes", processCodesCovered.ToString(), "Distinct process areas covered"),
            new("completed-this-month", "Completed This Month", completedThisMonth.ToString(), now.ToString("MMMM yyyy"))
        };

        var completedStepTypeBreakdown = stepTypeTiles
            .Select(tile => new MigrationProgressChartDatum(tile.Label, "Completed", tile.Completed))
            .ToList();

        var pendingStepTypeBreakdown = stepTypeTiles
            .Select(tile => new MigrationProgressChartDatum(tile.Label, "Pending", tile.Pending))
            .ToList();

        var insights = new List<MigrationProgressInsight>
        {
            new(
                "best-step-type",
                "Best Performer",
                bestStepType?.Label ?? "No data",
                bestStepType is null ? "No step type data is available." : $"{bestStepType.CompletionPercentage:0.0}% complete",
                "good"),
            new(
                "weakest-step-type",
                "Needs Attention",
                weakestStepType?.Label ?? "No data",
                weakestStepType is null ? "No step type data is available." : $"{weakestStepType.Pending} pending items",
                "warning"),
            new(
                "top-backlog-process",
                "Top Backlog Process",
                topPendingProcesses.FirstOrDefault()?.Argument ?? "None",
                topPendingProcesses.FirstOrDefault() is null ? "No pending process codes." : $"{topPendingProcesses[0].Value:0} pending items",
                "critical"),
            new(
                "zero-completion-processes",
                "Zero-Completion Process Codes",
                zeroCompletionProcessCodes.ToString(),
                "Process codes without a Testing Requested milestone yet",
                "neutral")
        };

        return new MigrationProgressOverviewViewModel(
            $"{completed} of {total} completed",
            kpis,
            completedStepTypeBreakdown,
            pendingStepTypeBreakdown,
            topPendingProcesses,
            MigrationProgressTrendGranularity.Weekly,
            completionTrends,
            stepTypeTiles,
            insights);
    }

    private static List<MigrationProgressOverviewStepTypeTile> BuildStepTypeTiles(IReadOnlyCollection<MigrationProgressOverviewSourceItem> items)
    {
        var orderedStepTypes = StepTypeOrder
            .Concat(items
                .Select(item => item.StepType)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(stepType => StepTypeOrder.All(ordered => !string.Equals(ordered, stepType, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(stepType => stepType))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return orderedStepTypes
            .Select(stepType =>
            {
                var matchingItems = items
                    .Where(item => string.Equals(item.StepType, stepType, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var completed = matchingItems.Count(item => item.CompletedAt is not null);
                var total = matchingItems.Count;
                var pending = total - completed;
                var completionPercentage = GetPercentage(completed, total);

                return new MigrationProgressOverviewStepTypeTile(
                    stepType,
                    StepTypeLabel(stepType),
                    total,
                    completed,
                    pending,
                    completionPercentage,
                    completionPercentage >= 60m ? "Best performer" : pending == total && total > 0 ? "No completions yet" : "Active pipeline");
            })
            .ToList();
    }

    private static List<MigrationProgressChartDatum> BuildPendingProcessRanking(IReadOnlyCollection<MigrationProgressOverviewSourceItem> items)
    {
        return items
            .GroupBy(item => item.ProcessCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var total = group.Count();
                var completed = group.Count(item => item.CompletedAt is not null);
                var pending = total - completed;
                var completionPercentage = GetPercentage(completed, total);

                return new
                {
                    ProcessCode = group.Key,
                    Pending = pending,
                    CompletionPercentage = completionPercentage
                };
            })
            .Where(group => group.Pending > 0)
            .OrderByDescending(group => group.Pending)
            .ThenBy(group => group.CompletionPercentage)
            .ThenBy(group => group.ProcessCode)
            .Take(5)
            .Select(group => new MigrationProgressChartDatum(group.ProcessCode, "Pending", group.Pending))
            .ToList();
    }

    private static List<MigrationProgressTrendSeries> BuildCompletionTrends(IReadOnlyCollection<MigrationProgressOverviewSourceItem> items)
    {
        var completedItems = items
            .Where(item => item.CompletedAt is not null)
            .OrderBy(item => item.CompletedAt)
            .ToList();

        if (completedItems.Count == 0)
        {
            return [];
        }

        return new List<MigrationProgressTrendSeries>
        {
            BuildTrendSeries(
                completedItems,
                MigrationProgressTrendGranularity.Weekly,
                "Weekly",
                item => GetWeekStart(item.CompletedAt!.Value),
                start => start.AddDays(6),
                start => $"{start:dd MMM} - {start.AddDays(6):dd MMM}",
                start => $"Week of {start:dd MMM} - {start.AddDays(6):dd MMM}"),
            BuildTrendSeries(
                completedItems,
                MigrationProgressTrendGranularity.Monthly,
                "Monthly",
                item => GetMonthStart(item.CompletedAt!.Value),
                start => new DateTimeOffset(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month), 0, 0, 0, TimeSpan.Zero),
                start => start.ToString("MMM yyyy"),
                start => start.ToString("MMMM yyyy")),
            BuildTrendSeries(
                completedItems,
                MigrationProgressTrendGranularity.Quarterly,
                "Quarterly",
                item => GetQuarterStart(item.CompletedAt!.Value),
                start => GetQuarterStart(start).AddMonths(3).AddDays(-1),
                start => $"Q{GetQuarter(start)} {start:yyyy}",
                start => $"Quarter {GetQuarter(start)} {start:yyyy}"),
            BuildTrendSeries(
                completedItems,
                MigrationProgressTrendGranularity.Yearly,
                "Yearly",
                item => GetYearStart(item.CompletedAt!.Value),
                start => new DateTimeOffset(start.Year, 12, 31, 0, 0, 0, TimeSpan.Zero),
                start => start.ToString("yyyy"),
                start => start.ToString("yyyy"))
        };
    }

    private static MigrationProgressTrendSeries BuildTrendSeries(
        IReadOnlyCollection<MigrationProgressOverviewSourceItem> completedItems,
        MigrationProgressTrendGranularity granularity,
        string label,
        Func<MigrationProgressOverviewSourceItem, DateTimeOffset> bucketStartFactory,
        Func<DateTimeOffset, DateTimeOffset> bucketEndFactory,
        Func<DateTimeOffset, string> bucketLabelFactory,
        Func<DateTimeOffset, string> tooltipLabelFactory)
    {
        var points = completedItems
            .GroupBy(bucketStartFactory)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var bucketStart = group.Key;
                return new MigrationProgressTrendPoint(
                    bucketStart,
                    bucketEndFactory(bucketStart),
                    bucketLabelFactory(bucketStart),
                    tooltipLabelFactory(bucketStart),
                    group.Count(),
                    group.Count(item => string.Equals(item.StepType, "M", StringComparison.OrdinalIgnoreCase)),
                    group.Count(item => string.Equals(item.StepType, "D", StringComparison.OrdinalIgnoreCase)),
                    group.Count(item => string.Equals(item.StepType, "R", StringComparison.OrdinalIgnoreCase)));
            })
            .ToList();

        return new MigrationProgressTrendSeries(granularity, label, points);
    }

    private static DateTimeOffset GetWeekStart(DateTimeOffset value)
    {
        var date = value.Date;
        var delta = ((int)date.DayOfWeek + 6) % 7;
        return new DateTimeOffset(date.AddDays(-delta), TimeSpan.Zero);
    }

    private static DateTimeOffset GetMonthStart(DateTimeOffset value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset GetQuarterStart(DateTimeOffset value)
    {
        var quarterStartMonth = ((value.Month - 1) / 3) * 3 + 1;
        return new DateTimeOffset(value.Year, quarterStartMonth, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset GetYearStart(DateTimeOffset value) =>
        new(value.Year, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static int GetQuarter(DateTimeOffset value) => ((value.Month - 1) / 3) + 1;

    private static string StepTypeLabel(string stepType) => stepType.ToUpperInvariant() switch
    {
        "M" => "Masters",
        "D" => "Documents",
        "R" => "Reports",
        _ => stepType
    };

    private static decimal GetPercentage(int completed, int total) =>
        total <= 0 ? 0 : Math.Round((decimal)completed * 100m / total, 1);
}
