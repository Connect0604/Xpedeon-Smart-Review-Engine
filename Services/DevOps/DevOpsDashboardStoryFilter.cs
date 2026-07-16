using SmartReviewSystem.Models.DevOps;

namespace SmartReviewSystem.Services.DevOps;

internal static class DevOpsDashboardStoryFilter
{
    private static readonly string[] InFlightStates =
    [
        "Coding In Progress",
        "Testing Requested",
        "Resolved"
    ];

    public static IEnumerable<DevOpsStoryItem> Apply(IEnumerable<DevOpsStoryItem> stories, string? searchText, string? stateFilter)
    {
        var query = stories;
        var search = searchText?.Trim() ?? string.Empty;
        var state = stateFilter?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(state) && !string.Equals(state, "Any", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(state, "In Flight", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(story => InFlightStates.Contains(story.State, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                query = query.Where(story => string.Equals(story.State, state, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(story =>
                story.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                story.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                story.AssignedTo.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                story.TeamMember.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                story.Mfe.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                story.ExecutionMode.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                story.OrchestratorPhase.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderBy(story => story.Id);
    }

    public static IEnumerable<DevOpsStoryItem> GetRunningStories(IEnumerable<DevOpsStoryItem> stories, string? filter = null)
    {
        var query = stories
            .Where(story => string.Equals(story.ExecutionMode, "Claude Orchestrator", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(filter) && !string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(story => string.Equals(story.State, "Coding In Progress", StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderBy(story => story.Id);
    }
}
