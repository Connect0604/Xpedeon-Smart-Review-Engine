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
    private List<DevOpsStoryItem> Stories = new();
    private PeriodicTimer? AutoReloadTimer;
    private bool AutoReloadEnabled;
    private int ReloadIntervalSeconds = 300;

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
            // Load stories WITHOUT revision metadata for fast dashboard loading
            // Revision metadata requires 200+ API calls and is not essential for status dashboard
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
