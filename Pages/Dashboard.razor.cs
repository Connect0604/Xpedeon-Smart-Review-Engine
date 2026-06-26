using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.DevOps;
using SmartReviewSystem.Services.DevOps;

namespace SmartReviewSystem.Pages;

public partial class Dashboard : ComponentBase, IAsyncDisposable
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
    private string OrchestratorFilter = "Coding In Progress";
    private string ActiveTab = "running";
    private string LoadError = string.Empty;
    private string ConnectionStatus = "Idle";
    private bool IsLoadingStories;
    private bool IsLoadingAllImplementationDetails;
    private List<DevOpsStoryItem> Stories = new();
    private PeriodicTimer? AutoReloadTimer;
    private bool AutoReloadEnabled;
    private int ReloadIntervalSeconds = 300;

    private bool AllImplementationDetailsLoaded =>
        Stories.Count == 0 || Stories.All(s => s.ImplementationDetailsLoaded);

    private List<DevOpsStoryItem> FilteredStories =>
        DevOpsDashboardStoryFilter.Apply(Stories, SearchText, StateFilter).ToList();

    private List<DevOpsStoryItem> RunningStories =>
        DevOpsDashboardStoryFilter.GetRunningStories(Stories, OrchestratorFilter).ToList();

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

        var dashboardConfig = Configuration.GetSection("Dashboard").Get<DashboardOptions>() ?? new DashboardOptions();
        AutoReloadEnabled = dashboardConfig.AutoReloadEnabled;
        ReloadIntervalSeconds = dashboardConfig.ReloadIntervalSeconds > 0 ? dashboardConfig.ReloadIntervalSeconds : 300;

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
        }
        else
        {
            ConnectionStatus = "Connecting";
            await LoadStoriesAsync();
        }

        if (AutoReloadEnabled)
        {
            StartAutoReload();
        }
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
            // Load stories WITHOUT revision metadata on initial load for better performance
            // Implementation details will be loaded on-demand when user clicks "Load Details"
            Stories = await AzureDevOpsService.GetStoriesWithAttachmentsAsync(
                DevOpsOrganization.Trim(),
                DevOpsProject.Trim(),
                DevOpsPatToken.Trim(),
                "[System.WorkItemType] = 'User Story' AND [System.Tags] CONTAINS 'AI development with revised orchestration'",
                CancellationToken.None,
                includeRevisionMetadata: false);

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

    private static int GetProgressPercentage(string phase) =>
        OrchestratorPhaseProgressHelper.GetProgressPercentage(phase);

    private static OrchestratorPhaseProgressHelper.PhaseStage? GetPhaseStageInfo(string phase) =>
        OrchestratorPhaseProgressHelper.GetPhaseStageInfo(phase);

    private static string FormatImplementationTime(TimeSpan? duration) =>
        ImplementationTimeHelper.FormatDuration(duration);

    private static string FormatImplementationTimeTooltip(DateTimeOffset? startDate, DateTimeOffset? completionDate)
    {
        if (startDate is null || completionDate is null)
        {
            return "Implementation time not yet calculated. Start: Orchestrator plan approval, End: Testing Requested state";
        }

        var startLocal = startDate.Value.ToLocalTime();
        var completionLocal = completionDate.Value.ToLocalTime();

        return $"Started (Plan Approved): {startLocal:g}\nCompleted (Testing Requested): {completionLocal:g}";
    }

    private static string FormatImplementationCost(string? cost) =>
        string.IsNullOrWhiteSpace(cost) ? "-" : $"${cost}";

    private async Task LoadAllImplementationDetailsAsync()
    {
        if (IsLoadingAllImplementationDetails || AllImplementationDetailsLoaded)
        {
            return;
        }

        IsLoadingAllImplementationDetails = true;

        try
        {
            // Load implementation details for all stories that haven't been loaded yet
            var tasksToLoad = Stories
                .Where(s => !s.ImplementationDetailsLoaded)
                .Select(s => AzureDevOpsService.LoadImplementationDetailsAsync(
                    s,
                    DevOpsOrganization.Trim(),
                    DevOpsProject.Trim(),
                    DevOpsPatToken.Trim(),
                    CancellationToken.None))
                .ToList();

            if (tasksToLoad.Count > 0)
            {
                await Task.WhenAll(tasksToLoad);
                await InvokeAsync(StateHasChanged);
            }
        }
        finally
        {
            IsLoadingAllImplementationDetails = false;
        }
    }

    private void SetActiveTab(string tabName)
    {
        ActiveTab = tabName;
    }

    private void StartAutoReload()
    {
        AutoReloadTimer = new PeriodicTimer(TimeSpan.FromSeconds(ReloadIntervalSeconds));
        _ = AutoReloadTickAsync();
    }

    private async Task AutoReloadTickAsync()
    {
        try
        {
            while (await AutoReloadTimer!.WaitForNextTickAsync())
            {
                await LoadStoriesAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Timer was disposed, this is expected
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        AutoReloadTimer?.Dispose();
        GC.SuppressFinalize(this);
        await ValueTask.CompletedTask;
    }
}

internal class DashboardOptions
{
    public bool AutoReloadEnabled { get; set; } = true;
    public int ReloadIntervalSeconds { get; set; } = 300;
}
