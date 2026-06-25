using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.DevOps;
using SmartReviewSystem.Services.DevOps;

namespace SmartReviewSystem.Pages;

public partial class Dashboard : ComponentBase
{
    [Inject]
    private IAzureDevOpsService AzureDevOpsService { get; set; } = default!;

    [Inject]
    private IConfiguration Configuration { get; set; } = default!;

    [Inject]
    private DevOpsDashboardState DashboardState { get; set; } = default!;

    private string DevOpsOrganization = string.Empty;
    private string DevOpsProject = string.Empty;
    private string DevOpsPatToken = string.Empty;
    private string SearchText = string.Empty;
    private string StateFilter = "Any";
    private string LoadError = string.Empty;
    private string ConnectionStatus = "Idle";
    private bool IsLoadingStories;
    private bool IsRunningGridCollapsed;
    private bool IsStoryTrackerCollapsed;
    private List<DevOpsStoryItem> Stories = new();

    private List<DevOpsStoryItem> FilteredStories =>
        DevOpsDashboardStoryFilter.Apply(Stories, SearchText, StateFilter).ToList();

    private List<DevOpsStoryItem> RunningStories =>
        DevOpsDashboardStoryFilter.GetRunningStories(Stories).ToList();

    private bool HasConnectionSettings =>
        !string.IsNullOrWhiteSpace(DevOpsOrganization) &&
        !string.IsNullOrWhiteSpace(DevOpsProject) &&
        !string.IsNullOrWhiteSpace(DevOpsPatToken);

    private string ConnectionStatusCssClass => ConnectionStatus.ToLowerInvariant().Replace(' ', '-');

    protected override async Task OnInitializedAsync()
    {
        var options = Configuration.GetSection("DevOps").Get<DevOpsOptions>() ?? new DevOpsOptions();
        DevOpsOrganization = options.Organization;
        DevOpsProject = options.Project;
        DevOpsPatToken = options.PatToken;
        if (!HasConnectionSettings)
        {
            ConnectionStatus = "Unavailable";
            DashboardState.MarkLoadAttempt(ConnectionStatus, "Azure DevOps organization, project, or PAT is missing from configuration.");
            return;
        }

        if (DashboardState.HasLoadedOnce)
        {
            Stories = DashboardState.Stories.ToList();
            ConnectionStatus = DashboardState.ConnectionStatus;
            LoadError = DashboardState.LoadError;
            return;
        }

        ConnectionStatus = "Connecting";
        await LoadStoriesAsync();
    }

    private async Task LoadStoriesAsync()
    {
        LoadError = string.Empty;
        ConnectionStatus = "Loading";
        IsLoadingStories = true;

        if (!HasConnectionSettings)
        {
            LoadError = "Azure DevOps organization, project, or PAT is missing from configuration.";
            ConnectionStatus = "Unavailable";
            IsLoadingStories = false;
            DashboardState.MarkLoadAttempt(ConnectionStatus, LoadError);
            return;
        }

        try
        {
            Stories = await AzureDevOpsService.GetStoriesWithAttachmentsAsync(
                DevOpsOrganization.Trim(),
                DevOpsProject.Trim(),
                DevOpsPatToken.Trim(),
                "[System.WorkItemType] = 'User Story' AND [System.Tags] CONTAINS 'AI development with revised orchestration'",
                CancellationToken.None);

            ConnectionStatus = Stories.Count == 0 ? "No stories found" : "Connected";
            DashboardState.SetStories(Stories, ConnectionStatus);
        }
        catch (Exception ex)
        {
            Stories = new List<DevOpsStoryItem>();
            LoadError = $"Failed to load stories: {ex.Message}";
            ConnectionStatus = "Failed";
            DashboardState.MarkLoadAttempt(ConnectionStatus, LoadError);
        }
        finally
        {
            IsLoadingStories = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private static string DisplayOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string FormatDate(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("dd MMM yyyy") ?? "-";

    private static string FormatDateTime(DateTimeOffset? value) =>
        value?.ToLocalTime().ToString("dd MMM yyyy HH:mm") ?? "-";

    private void ToggleRunningGrid() =>
        IsRunningGridCollapsed = !IsRunningGridCollapsed;

    private void ToggleStoryTrackerGrid() =>
        IsStoryTrackerCollapsed = !IsStoryTrackerCollapsed;
}
