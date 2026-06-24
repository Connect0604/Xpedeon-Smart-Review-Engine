using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.Agents;
using SmartReviewSystem.Models.Ai;
using SmartReviewSystem.Models.DevOps;
using SmartReviewSystem.Models.Ui;
using SmartReviewSystem.Services.DevOps;
using SmartReviewSystem.Services.Ollama;
using SmartReviewSystem.Services.Orchestration;

namespace SmartReviewSystem.Pages;

public partial class Home : ComponentBase, IAsyncDisposable
{
    [Inject]
    private IJSRuntime JS { get; set; } = default!;
    [Inject]
    private HttpClient Http { get; set; } = default!;
    [Inject]
    private IAzureDevOpsService AzureDevOpsService { get; set; } = default!;
    [Inject]
    private IOllamaService OllamaService { get; set; } = default!;
    [Inject]
    private IConfiguration Configuration { get; set; } = default!;
    [Inject]
    private ConfigRoutingStrategy ConfigStrategy { get; set; } = default!;
    [Inject]
    private LlmRoutingStrategy LlmStrategy { get; set; } = default!;
    [Inject]
    private ReviewOrchestrator Orchestrator { get; set; } = default!;

    private enum SectionFilterMode
    {
        All,
        ReviewRequired,
        CustomPattern
    }

    private sealed record ConfiguredSectionTab(string Name, IReadOnlyList<string> Patterns);

    private enum UploadSource
    {
        Local,
        AzureDevOps
    }

    private readonly List<SectionModel> Sections = new();
    private string? UploadedFileName;
    private DevOpsStoryItem? ActiveStory;
    private string UploadError = string.Empty;
    private string? SelectedSectionId;
    private string SectionSearchText = string.Empty;
    private SectionFilterMode SectionFilter = SectionFilterMode.All;
    private readonly List<ConfiguredSectionTab> ConfiguredSectionTabs = new();
    private string? ActiveCustomTabName;
    private bool IsDragging;
    private bool IsRunningAiReview;
    private bool IsFullScanEnabled;
    private bool IsFullScanRunning;
    private string? FullScanActiveSectionId;
    private int FullScanProgress;
    private int FullScanTotal;
    private RoutingMode ActiveRoutingMode;
    private IReadOnlyList<SectionPromptStep> CurrentSteps = Array.Empty<SectionPromptStep>();
    private List<SpokeResult> SpokeResults = new();
    private CancellationTokenSource? _reviewCts;
    private readonly Dictionary<string, (List<SpokeResult> Results, IReadOnlyList<SectionPromptStep> Steps)> _reviewCache = new();
    private UploadSource CurrentUploadSource = UploadSource.AzureDevOps;
    private string DevOpsOrganization = string.Empty;
    private string DevOpsProject = string.Empty;
    private string DevOpsPatToken = string.Empty;
    private string DevOpsCondition = "[System.WorkItemType] = 'User Story' AND [System.State] <> 'Closed'";
    private string DevOpsBuiltQuery = string.Empty;
    private bool UseAdvancedWiql;
    private string DevOpsTagFilter = "AI development with revised orchestration"; //"Master AI Development";
    private string DevOpsStateFilter = "Any";
    private string DevOpsAssignedFilter = string.Empty;
    private bool DevOpsOnlyWithAttachments = true;
    private string DevOpsStorySearch = string.Empty;
    private string DevOpsError = string.Empty;
    private bool IsLoadingStories;
    private List<DevOpsStoryItem> DevOpsStories = new();
    private int DevOpsTotalStories;
    private int DevOpsStoriesWithAttachments;
    private int DevOpsSupportedAttachmentCount;
    private int DevOpsUnsupportedAttachmentCount;
    private string DevOpsUnsupportedExtensionSummary = string.Empty;
    private string DevOpsConnectionStatus = "Idle";
    private bool IsDevOpsFiltersCollapsed = true;
    private readonly Dictionary<string, (DevOpsStoryItem Story, DevOpsAttachmentItem Attachment)> _selectedDevOpsAttachments = new(StringComparer.OrdinalIgnoreCase);
    private int SelectedAttachmentCount => _selectedDevOpsAttachments.Count;
    private string? _selectedSourceFile;
    private RazorMockScreen? GeneratedMockScreen;
    private string GeneratedMockJson = string.Empty;
    private string GeneratedMockHtml = string.Empty;
    private string MockGeneratorError = string.Empty;
    private bool IsGeneratingAiMock;
    private bool IsCapturingMockPng;
    private bool IsGeneratingComparison;
    private string ComparisonReportMarkdown = string.Empty;
    private int ComparisonProgressPercent;
    private string ComparisonProgressLabel = string.Empty;
    private CancellationTokenSource? _comparisonProgressCts;
    private bool ShowMockAttachmentPicker;
    private List<DevOpsAttachmentItem> AvailableMockAttachments = new();
    private string? SelectedMockAttachmentUrl;

    protected override void OnInitialized()
    {
        var routingModeValue = Configuration["Validation:Controls:RoutingMode"] ?? "Static";
        ActiveRoutingMode = Enum.TryParse<RoutingMode>(routingModeValue, ignoreCase: true, out var parsed)
            ? parsed
            : RoutingMode.Static;

        IsFullScanEnabled = Configuration.GetValue<bool>("Validation:Controls:FullScan", false);

        var options = Configuration.GetSection("DevOps").Get<DevOpsOptions>() ?? new DevOpsOptions();
        DevOpsOrganization = options.Organization;
        DevOpsProject = options.Project;
        DevOpsPatToken = options.PatToken;
        DevOpsCondition = string.IsNullOrWhiteSpace(options.WiqlCondition)
            ? "[System.WorkItemType] = 'User Story' AND [System.State] <> 'Closed'"
            : options.WiqlCondition;
        DevOpsBuiltQuery = DevOpsCondition;
        LoadConfiguredSectionTabsFromSettings();
    }

    private IEnumerable<DevOpsStoryItem> FilteredDevOpsStories
    {
        get
        {
            IEnumerable<DevOpsStoryItem> query = DevOpsStories;

            if (DevOpsOnlyWithAttachments)
            {
                query = query.Where(story => story.Attachments.Count > 0);
            }

            var search = DevOpsStorySearch.Trim();
            if (string.IsNullOrWhiteSpace(search))
            {
                return query.OrderByDescending(story => story.Id);
            }

            return query
                .Where(story =>
                    story.Id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    story.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    story.AssignedTo.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    story.Tags.Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(story => story.Id);
        }
    }

    private bool HasFileLoaded => Sections.Count > 0;
    private string ActiveContextLabel => HasFileLoaded
        ? UploadedFileName ?? "Analysis loaded"
        : CurrentUploadSource == UploadSource.AzureDevOps
            ? "Azure DevOps source"
            : "Local upload source";
    private SectionModel? CurrentSection => Sections.FirstOrDefault(s => s.Id == SelectedSectionId);
    private bool CurrentSectionIsRazorPage =>
        CurrentSection is not null && IsLikelyRazorSection(CurrentSection.Heading, CurrentSection.Content);

    private IRoutingStrategy ActiveStrategy =>
        ActiveRoutingMode == RoutingMode.Static ? ConfigStrategy : LlmStrategy;

    private bool CurrentSectionHasAiSupport =>
        CurrentSection is not null && (
            ActiveRoutingMode == RoutingMode.Dynamic ||
            OllamaService.HasSectionPrompt(CurrentSection.Heading)
        );
    private string ModelDisplay => OllamaService.GetConfiguredModelsDisplay();
    private string PrimaryModel => OllamaService.GetPrimaryModel();
    private string? LastUsedModel => OllamaService.GetLastUsedModel();
    private bool LastCallUsedFallback => OllamaService.WasLastCallFallbackUsed();
    private IEnumerable<SectionModel> FilteredSections
    {
        get
        {
            var query = SectionSearchText.Trim();
            IEnumerable<SectionModel> sections = SectionFilter switch
            {
                SectionFilterMode.ReviewRequired => Sections.Where(section =>
                    section.Heading.Contains("review", StringComparison.OrdinalIgnoreCase) ||
                    section.Content.Contains("review", StringComparison.OrdinalIgnoreCase)),
                SectionFilterMode.CustomPattern => GetCustomPatternSections(),
                _ => Sections
            };

            if (!string.IsNullOrWhiteSpace(query))
            {
                sections = sections.Where(section =>
                    section.Heading.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    section.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(_selectedSourceFile))
            {
                sections = sections.Where(section => string.Equals(section.SourceFile, _selectedSourceFile, StringComparison.OrdinalIgnoreCase));
            }

            return sections;
        }
    }

    private bool ShouldShowDefaultReviewRequiredTab =>
        ConfiguredSectionTabs.All(tab => !tab.Name.Equals("Review Required", StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<string> ActiveHighlightTerms
    {
        get
        {
            var search = SectionSearchText.Trim();
            if (!string.IsNullOrWhiteSpace(search))
            {
                return new[] { search };
            }

            if (SectionFilter == SectionFilterMode.ReviewRequired)
            {
                return new[] { "review" };
            }

            if (SectionFilter == SectionFilterMode.CustomPattern && !string.IsNullOrWhiteSpace(ActiveCustomTabName))
            {
                var configuredTab = ConfiguredSectionTabs.FirstOrDefault(tab =>
                    tab.Name.Equals(ActiveCustomTabName, StringComparison.OrdinalIgnoreCase));
                return configuredTab?.Patterns ?? Array.Empty<string>();
            }

            return Array.Empty<string>();
        }
    }

    private void OnDragEnter()
    {
        IsDragging = true;
    }

    private void OnDragOver()
    {
        IsDragging = true;
    }

    private void OnDragLeave()
    {
        IsDragging = false;
    }

    private void OnDrop()
    {
        IsDragging = false;
    }

    private async Task HandleFileSelected(InputFileChangeEventArgs args)
    {
        IsDragging = false;
        UploadError = string.Empty;

        var file = args.File;
        if (file is null)
        {
            UploadError = "No file selected.";
            return;
        }

        var extension = Path.GetExtension(file.Name);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".txt", ".markdown" };
        if (!allowed.Contains(extension))
        {
            UploadError = "Unsupported file type. Please upload a .md, .txt, or .markdown file.";
            return;
        }

        try
        {
            using var stream = file.OpenReadStream(maxAllowedSize: 10 * 1024 * 1024);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            ActiveStory = null;
            ProcessUploadedContent(file.Name, content);
        }
        catch (Exception ex)
        {
            UploadError = $"Failed to read file: {ex.Message}";
        }
    }

    private async Task LoadStoriesFromDevOpsAsync()
    {
        DevOpsError = string.Empty;
        UploadError = string.Empty;
        _selectedDevOpsAttachments.Clear();
        DevOpsStories = new List<DevOpsStoryItem>();
        DevOpsTotalStories = 0;
        DevOpsStoriesWithAttachments = 0;
        DevOpsSupportedAttachmentCount = 0;
        DevOpsUnsupportedAttachmentCount = 0;
        DevOpsUnsupportedExtensionSummary = string.Empty;
        DevOpsConnectionStatus = "Loading";
        DevOpsBuiltQuery = UseAdvancedWiql ? DevOpsCondition.Trim() : BuildGuidedWiqlCondition();

        if (string.IsNullOrWhiteSpace(DevOpsOrganization) || string.IsNullOrWhiteSpace(DevOpsProject) || string.IsNullOrWhiteSpace(DevOpsPatToken))
        {
            DevOpsError = "Organization, project, and PAT token are required.";
            DevOpsConnectionStatus = "Failed";
            return;
        }

        IsLoadingStories = true;
        try
        {
            DevOpsStories = await AzureDevOpsService.GetStoriesWithAttachmentsAsync(
                DevOpsOrganization.Trim(),
                DevOpsProject.Trim(),
                DevOpsPatToken.Trim(),
                DevOpsBuiltQuery,
                CancellationToken.None);

            DevOpsTotalStories = DevOpsStories.Count;
            DevOpsStoriesWithAttachments = DevOpsStories.Count(s => s.Attachments.Count > 0);
            DevOpsSupportedAttachmentCount = DevOpsStories.Sum(s => s.Attachments.Count(a => a.IsSupported));
            DevOpsUnsupportedAttachmentCount = DevOpsStories.Sum(s => s.Attachments.Count(a => !a.IsSupported));
            DevOpsUnsupportedExtensionSummary = string.Join(", ",
                DevOpsStories
                    .SelectMany(s => s.Attachments)
                    .Where(a => !a.IsSupported)
                    .GroupBy(a => a.Extension)
                    .OrderByDescending(g => g.Count())
                    .Take(6)
                    .Select(g => $"{g.Key} ({g.Count()})"));

            if (DevOpsTotalStories == 0)
            {
                DevOpsError = "No user stories matched this WIQL condition.";
                DevOpsConnectionStatus = "Connected";
            }
            else if (DevOpsSupportedAttachmentCount == 0)
            {
                DevOpsError = "Connected successfully, but none of the fetched stories has supported text attachments (.md/.txt/.markdown).";
                DevOpsConnectionStatus = "Connected";
            }
            else
            {
                DevOpsConnectionStatus = "Connected";
            }
        }
        catch (Exception ex)
        {
            DevOpsError = $"Failed to load stories: {ex.Message}";
            DevOpsConnectionStatus = "Failed";
        }
        finally
        {
            IsLoadingStories = false;
        }
    }

    private async Task AnalyzeDevOpsAttachmentAsync(DevOpsStoryItem story, DevOpsAttachmentItem attachment)
    {
        UploadError = string.Empty;
        DevOpsError = string.Empty;
        if (!attachment.IsSupported)
        {
            DevOpsError = $"Attachment '{attachment.Name}' is not a supported text file (.md/.txt/.markdown).";
            return;
        }

        try
        {
            if (IsHtmlAttachment(attachment.Name))
            {
                var htmlContent = await AzureDevOpsService.DownloadAttachmentTextAsync(attachment.Url, DevOpsPatToken.Trim(), CancellationToken.None);
                try
                {
                    await JS.InvokeVoidAsync("smartReview.openHtmlInNewTab", htmlContent, attachment.Name);
                }
                catch
                {
                    await JS.InvokeVoidAsync("openHtmlInNewTab", htmlContent, attachment.Name);
                }
                return;
            }

            var content = await AzureDevOpsService.DownloadAttachmentTextAsync(attachment.Url, DevOpsPatToken.Trim(), CancellationToken.None);
            var fileName = $"US-{story.Id}-{attachment.Name}";
            ActiveStory = story;
            ProcessUploadedContent(fileName, content);
        }
        catch (Exception ex)
        {
            DevOpsError = $"Failed to analyze attachment '{attachment.Name}': {ex.Message}";
        }
    }

    private IReadOnlyList<string> AvailableSourceFiles => Sections
        .Select(s => s.SourceFile)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string BuildAttachmentSelectionKey(DevOpsStoryItem story, DevOpsAttachmentItem attachment)
    {
        return $"{story.Id}|{attachment.Url}";
    }

    private bool IsAttachmentSelected(DevOpsStoryItem story, DevOpsAttachmentItem attachment)
    {
        return _selectedDevOpsAttachments.ContainsKey(BuildAttachmentSelectionKey(story, attachment));
    }

    private void ToggleAttachmentSelection(DevOpsStoryItem story, DevOpsAttachmentItem attachment, object? value)
    {
        if (!attachment.IsSupported)
        {
            return;
        }

        var key = BuildAttachmentSelectionKey(story, attachment);
        var isChecked = value as bool? == true;
        if (isChecked)
        {
            _selectedDevOpsAttachments[key] = (story, attachment);
        }
        else
        {
            _selectedDevOpsAttachments.Remove(key);
        }
    }

    private void ClearSelectedAttachments()
    {
        _selectedDevOpsAttachments.Clear();
    }

    private async Task AnalyzeSelectedDevOpsAttachmentsAsync()
    {
        UploadError = string.Empty;
        DevOpsError = string.Empty;

        var selected = _selectedDevOpsAttachments.Values
            .Where(s => s.Attachment.IsSupported)
            .OrderBy(s => s.Story.Id)
            .ThenBy(s => s.Attachment.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
        {
            DevOpsError = "Select one or more supported text attachments first.";
            return;
        }

        try
        {
            var mergedSections = new List<SectionModel>();
            DevOpsStoryItem? firstStory = null;

            foreach (var (story, attachment) in selected)
            {
                var content = await AzureDevOpsService.DownloadAttachmentTextAsync(attachment.Url, DevOpsPatToken.Trim(), CancellationToken.None);
                firstStory ??= story;
                var sourceFile = $"US-{story.Id}-{attachment.Name}";
                mergedSections.AddRange(ParseSections(content, sourceFile));
            }

            ActiveStory = firstStory;
            var fileLabel = selected.Count == 1
                ? $"US-{selected[0].Story.Id}-{selected[0].Attachment.Name}"
                : $"US-batch-{selected.Count}-attachments.md";
            LoadSections(fileLabel, mergedSections);
            _selectedDevOpsAttachments.Clear();
        }
        catch (Exception ex)
        {
            DevOpsError = $"Failed to analyze selected attachments: {ex.Message}";
        }
    }

    private static bool IsHtmlAttachment(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeWiqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private string BuildGuidedWiqlCondition()
    {
        var conditions = new List<string>
        {
            "[System.WorkItemType] = 'User Story'"
        };

        if (!string.IsNullOrWhiteSpace(DevOpsTagFilter))
        {
            conditions.Add($"[System.Tags] CONTAINS '{EscapeWiqlLiteral(DevOpsTagFilter.Trim())}'");
        }

        if (!string.Equals(DevOpsStateFilter, "Any", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(DevOpsStateFilter))
        {
            conditions.Add($"[System.State] = '{EscapeWiqlLiteral(DevOpsStateFilter.Trim())}'");
        }

        if (!string.IsNullOrWhiteSpace(DevOpsAssignedFilter))
        {
            conditions.Add($"[System.AssignedTo] CONTAINS '{EscapeWiqlLiteral(DevOpsAssignedFilter.Trim())}'");
        }

        return string.Join(" AND ", conditions);
    }

    private void SetUploadSource(UploadSource source)
    {
        CurrentUploadSource = source;
        UploadError = string.Empty;
        DevOpsError = string.Empty;
    }

    private void ProcessUploadedContent(string fileName, string content)
    {
        LoadSections(fileName, ParseSections(content, fileName));
    }

    private void LoadSections(string fileName, List<SectionModel> parsedSections)
    {
        UploadedFileName = fileName;
        ActiveCustomTabName = null;
        Sections.Clear();
        Sections.AddRange(parsedSections.Select((section, index) => new SectionModel
        {
            Id = $"section-{index + 1}",
            Letter = section.Letter,
            Heading = section.Heading,
            LineStart = section.LineStart,
            LineCount = section.LineCount,
            Content = section.Content,
            SourceFile = section.SourceFile
        }));
        SelectedSectionId = Sections.FirstOrDefault()?.Id;
        SectionSearchText = string.Empty;
        SectionFilter = SectionFilterMode.All;
        RefreshSourceFilters();
        ResetMockAttachmentSelection();

        if (Sections.Count == 0)
        {
            UploadError = "The uploaded file has no parsable sections.";
        }
    }

    private void ResetAll()
    {
        UploadedFileName = null;
        ActiveStory = null;
        UploadError = string.Empty;
        SelectedSectionId = null;
        Sections.Clear();
        SectionSearchText = string.Empty;
        SectionFilter = SectionFilterMode.All;
        ActiveCustomTabName = null;
        DevOpsError = string.Empty;
        DevOpsTotalStories = 0;
        DevOpsStoriesWithAttachments = 0;
        DevOpsSupportedAttachmentCount = 0;
        DevOpsUnsupportedAttachmentCount = 0;
        DevOpsUnsupportedExtensionSummary = string.Empty;
        DevOpsConnectionStatus = "Idle";
        DevOpsBuiltQuery = string.Empty;
        _selectedDevOpsAttachments.Clear();
        _selectedSourceFile = null;
        SpokeResults = new();
        CurrentSteps = Array.Empty<SectionPromptStep>();
        IsRunningAiReview = false;
        IsFullScanRunning = false;
        FullScanActiveSectionId = null;
        FullScanProgress = 0;
        FullScanTotal = 0;
        _reviewCache.Clear();
        CancelActiveReview();
        ResetMockGeneratorState();
    }

    private void SelectSection(string sectionId)
    {
        if (SelectedSectionId == sectionId && !IsFullScanRunning)
            return;

        CancelActiveReview();

        if (IsFullScanRunning)
        {
            IsFullScanRunning = false;
            FullScanActiveSectionId = null;
        }

        SelectedSectionId = sectionId;
        IsRunningAiReview = false;
        ResetMockGeneratorState();

        if (_reviewCache.TryGetValue(sectionId, out var cached))
        {
            SpokeResults = cached.Results;
            CurrentSteps = cached.Steps;
        }
        else
        {
            SpokeResults = new();
            CurrentSteps = Array.Empty<SectionPromptStep>();
        }
    }

    private async Task RunAiReviewAsync()
    {
        if (CurrentSection is null || IsRunningAiReview || IsFullScanRunning)
            return;

        CancelActiveReview();
        _reviewCts = new CancellationTokenSource();
        var ct = _reviewCts.Token;

        SpokeResults = new();
        CurrentSteps = Array.Empty<SectionPromptStep>();
        StateHasChanged();

        try
        {
            await RunAiReviewCoreAsync(CurrentSection, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            StateHasChanged();
        }
    }

    private async Task RunDevExpressPropertyValidationAsync()
    {
        if (CurrentSection is null || IsRunningAiReview || IsFullScanRunning || !CurrentSectionIsRazorPage)
            return;

        CancelActiveReview();
        _reviewCts = new CancellationTokenSource();
        var ct = _reviewCts.Token;

        IsRunningAiReview = true;
        SpokeResults = new();
        CurrentSteps = Array.Empty<SectionPromptStep>();
        StateHasChanged();

        try
        {
            var steps = OllamaService
                .GetPromptSteps(CurrentSection.Heading)
                .Where(s => s.Label.Equals("DevExpress Control Property Validation", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (steps.Count == 0)
            {
                SpokeResults = new List<SpokeResult>
                {
                    new SpokeResult
                    {
                        Label = "DevExpress Control Property Validation",
                        Status = SpokeStatus.Failed,
                        Error = "Validation step is not configured under Razor Page prompts."
                    }
                };
                return;
            }

            CurrentSteps = steps;
            SpokeResults = steps.Select(s => new SpokeResult { Label = s.Label }).ToList();
            StateHasChanged();

            await Orchestrator.RunAsync(
                CurrentSection.Heading,
                CurrentSection.Content,
                steps,
                SpokeResults,
                StateHasChanged,
                ct);

            _reviewCache[CurrentSection.Id] = (SpokeResults, CurrentSteps);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsRunningAiReview = false;
            StateHasChanged();
        }
    }

    private async Task RunFullScanAsync()
    {
        if (IsFullScanRunning || IsRunningAiReview || Sections.Count == 0)
            return;

        CancelActiveReview();
        _reviewCts = new CancellationTokenSource();
        var ct = _reviewCts.Token;

        IsFullScanRunning = true;
        FullScanProgress = 0;
        FullScanTotal = Sections.Count;
        StateHasChanged();

        try
        {
            foreach (var section in Sections)
            {
                ct.ThrowIfCancellationRequested();

                FullScanActiveSectionId = section.Id;
                SelectedSectionId = section.Id;
                SpokeResults = _reviewCache.TryGetValue(section.Id, out var cached)
                    ? cached.Results
                    : new List<SpokeResult>();
                CurrentSteps = _reviewCache.TryGetValue(section.Id, out var cachedSteps)
                    ? cachedSteps.Steps
                    : Array.Empty<SectionPromptStep>();
                StateHasChanged();

                await RunAiReviewCoreAsync(section, ct);

                FullScanProgress++;
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            IsFullScanRunning = false;
            FullScanActiveSectionId = null;
            IsRunningAiReview = false;
            StateHasChanged();
        }
    }

    private async Task RunAiReviewCoreAsync(SectionModel section, CancellationToken ct)
    {
        IsRunningAiReview = true;
        SpokeResults = new();
        CurrentSteps = Array.Empty<SectionPromptStep>();
        StateHasChanged();

        try
        {
            var steps = await ActiveStrategy.ResolveStepsAsync(section.Heading, section.Content, ct);
            if (steps.Count == 0)
            {
                CurrentSteps = Array.Empty<SectionPromptStep>();
                SpokeResults = new List<SpokeResult>
                {
                    new SpokeResult
                    {
                        Label = "AI Review",
                        Status = SpokeStatus.Failed,
                        Error = $"No AI prompts configured for section '{section.Heading}'."
                    }
                };
                _reviewCache[section.Id] = (SpokeResults, CurrentSteps);
                return;
            }

            CurrentSteps = steps;
            SpokeResults = steps.Select(s => new SpokeResult { Label = s.Label }).ToList();
            StateHasChanged();

            await Orchestrator.RunAsync(
                section.Heading,
                section.Content,
                steps,
                SpokeResults,
                StateHasChanged,
                ct);

            _reviewCache[section.Id] = (SpokeResults, CurrentSteps);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SpokeResults = new List<SpokeResult>
            {
                new SpokeResult
                {
                    Label = "Error",
                    Status = SpokeStatus.Failed,
                    Error = $"Review failed: {ex.Message}"
                }
            };
        }
        finally
        {
            IsRunningAiReview = false;
        }
    }
    private void CancelActiveReview()
    {
        _reviewCts?.Cancel();
        _reviewCts?.Dispose();
        _reviewCts = null;
    }

    public ValueTask DisposeAsync()
    {
        CancelActiveReview();
        return ValueTask.CompletedTask;
    }

    private void SetSectionFilter(SectionFilterMode filter)
    {
        SectionFilter = filter;
        if (filter != SectionFilterMode.CustomPattern)
        {
            ActiveCustomTabName = null;
        }
    }

    private void SetCustomSectionFilter(string tabName)
    {
        ActiveCustomTabName = tabName;
        SectionFilter = SectionFilterMode.CustomPattern;
    }

    private IEnumerable<SectionModel> GetCustomPatternSections()
    {
        if (string.IsNullOrWhiteSpace(ActiveCustomTabName))
        {
            return Sections;
        }

        var configuredTab = ConfiguredSectionTabs.FirstOrDefault(tab =>
            tab.Name.Equals(ActiveCustomTabName, StringComparison.OrdinalIgnoreCase));

        if (configuredTab is null || configuredTab.Patterns.Count == 0)
        {
            return Sections;
        }

        return Sections.Where(section => configuredTab.Patterns.Any(pattern =>
            section.Heading.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
            section.Content.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
    }

    private void LoadConfiguredSectionTabsFromSettings()
    {
        ConfiguredSectionTabs.Clear();
        var options = Configuration.GetSection("SectionTabs").Get<List<SectionFilterTabOptions>>() ?? new List<SectionFilterTabOptions>();
        foreach (var option in options)
        {
            var tabName = NormalizeWhitespace(option.Tab);
            var patterns = BuildConfiguredPatterns(option);
            if (string.IsNullOrWhiteSpace(tabName) || patterns.Count == 0)
            {
                continue;
            }

            if (ConfiguredSectionTabs.Any(tab => tab.Name.Equals(tabName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ConfiguredSectionTabs.Add(new ConfiguredSectionTab(tabName, patterns));
        }
    }

    private static List<string> BuildConfiguredPatterns(SectionFilterTabOptions option)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(option.Patten))
        {
            values.Add(option.Patten);
        }

        if (!string.IsNullOrWhiteSpace(option.Pattern))
        {
            values.Add(option.Pattern);
        }

        if (option.Pattens is not null)
        {
            values.AddRange(option.Pattens);
        }

        if (option.Patterns is not null)
        {
            values.AddRange(option.Patterns);
        }

        return values
            .Select(NormalizeWhitespace)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeWhitespace(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    private void GenerateRazorMockScreen()
    {
        MockGeneratorError = string.Empty;
        if (CurrentSection is null)
        {
            MockGeneratorError = "Select a section first.";
            return;
        }

        var razorCode = ExtractRazorCode(CurrentSection.Content);
        if (string.IsNullOrWhiteSpace(razorCode))
        {
            MockGeneratorError = "No Razor code block found in this section.";
            GeneratedMockScreen = null;
            GeneratedMockJson = string.Empty;
            GeneratedMockHtml = string.Empty;
            return;
        }

        var screen = BuildMockScreenModel(razorCode, CurrentSection.Heading);
        GeneratedMockScreen = screen;
        GeneratedMockJson = JsonSerializer.Serialize(screen, new JsonSerializerOptions { WriteIndented = true });
        GeneratedMockHtml = BuildMockScreenHtml(screen);
    }

    private async Task GenerateRazorMockScreenWithAiAsync()
    {
        if (IsGeneratingAiMock)
        {
            return;
        }

        MockGeneratorError = string.Empty;
        if (CurrentSection is null)
        {
            MockGeneratorError = "Select a section first.";
            return;
        }

        var razorCode = ExtractRazorCode(CurrentSection.Content);
        if (string.IsNullOrWhiteSpace(razorCode))
        {
            MockGeneratorError = "No Razor code block found in this section.";
            return;
        }

        IsGeneratingAiMock = true;
        StateHasChanged();
        try
        {
            var fastModel = BuildMockScreenModel(razorCode, CurrentSection.Heading);
            var prompt = BuildAiMockPrompt(fastModel);
            var json = await OllamaService.GenerateJsonAsync(prompt);

            var aiModel = JsonSerializer.Deserialize<AiMockScreenSpec>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (!IsValidAiSpec(aiModel))
            {
                throw new InvalidOperationException("AI mock schema was invalid.");
            }

            var merged = ToRazorMockScreen(aiModel!);
            GeneratedMockScreen = merged;
            GeneratedMockJson = JsonSerializer.Serialize(aiModel, new JsonSerializerOptions { WriteIndented = true });
            GeneratedMockHtml = BuildMockScreenHtml(merged);
        }
        catch (Exception ex)
        {
            GenerateRazorMockScreen();
            MockGeneratorError = $"AI design failed, showing fast mock instead. ({ex.Message})";
        }
        finally
        {
            IsGeneratingAiMock = false;
            StateHasChanged();
        }
    }

    private async Task CopyCurrentSectionContentAsync()
    {
        if (CurrentSection is null)
        {
            return;
        }

        var text = CurrentSection.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("smartReview.copyTextToClipboard", text);
        }
        catch
        {
            // Ignore clipboard failures to avoid interrupting review flow.
        }
    }

    private async Task CaptureGeneratedMockAsync()
    {
        if (GeneratedMockScreen is null || IsCapturingMockPng)
        {
            return;
        }

        IsCapturingMockPng = true;
        MockGeneratorError = string.Empty;
        StateHasChanged();
        try
        {
            var safeTitle = Regex.Replace(GeneratedMockScreen.Title ?? "mock-screen", @"[^A-Za-z0-9\-]+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(safeTitle))
            {
                safeTitle = "mock-screen";
            }

            var fileName = $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            await JS.InvokeVoidAsync("smartReview.captureElementAsPng", "srs-generated-mock-render", fileName);
        }
        catch (Exception ex)
        {
            MockGeneratorError = $"Capture failed: {ex.Message}";
        }
        finally
        {
            IsCapturingMockPng = false;
            StateHasChanged();
        }
    }

    private void ResetMockGeneratorState()
    {
        GeneratedMockScreen = null;
        GeneratedMockJson = string.Empty;
        GeneratedMockHtml = string.Empty;
        MockGeneratorError = string.Empty;
        ComparisonReportMarkdown = string.Empty;
        ResetMockAttachmentSelection();
    }

    private void ResetMockAttachmentSelection()
    {
        ShowMockAttachmentPicker = false;
        AvailableMockAttachments.Clear();
        SelectedMockAttachmentUrl = null;
    }

    private async Task GenerateRazorMockComparisonAsync()
    {
        if (IsGeneratingComparison || CurrentSection is null || !CurrentSectionIsRazorPage)
        {
            return;
        }

        SyncActiveStoryWithLoadedSections();

        if (ActiveStory is null)
        {
            MockGeneratorError = "Load a user story attachment first so mock files can be selected.";
            return;
        }

        AvailableMockAttachments = ActiveStory.Attachments
            .Where(IsMockAttachmentCandidate)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (AvailableMockAttachments.Count == 0)
        {
            MockGeneratorError = "No mock attachments found for selected user story.";
            return;
        }

        MockGeneratorError = string.Empty;
        ComparisonReportMarkdown = string.Empty;
        SelectedMockAttachmentUrl = AvailableMockAttachments[0].Url;
        ShowMockAttachmentPicker = true;
        StateHasChanged();
        await Task.Delay(20);
        await JS.InvokeVoidAsync("smartReview.scrollToElement", "srs-mock-attachment-picker");
    }

    private void SyncActiveStoryWithLoadedSections()
    {
        if (Sections.Count == 0 || DevOpsStories.Count == 0)
        {
            return;
        }

        var ids = Sections
            .Select(s => ExtractStoryIdFromSourceFile(s.SourceFile))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count != 1)
        {
            return;
        }

        var loadedStoryId = ids[0];
        if (ActiveStory?.Id == loadedStoryId)
        {
            return;
        }

        var matching = DevOpsStories.FirstOrDefault(s => s.Id == loadedStoryId);
        if (matching is not null)
        {
            ActiveStory = matching;
        }
    }

    private static int? ExtractStoryIdFromSourceFile(string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return null;
        }

        var match = Regex.Match(sourceFile, @"US-(\d+)-", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private void CancelMockAttachmentPicker()
    {
        ShowMockAttachmentPicker = false;
    }

    private async Task ConfirmMockAttachmentComparisonAsync()
    {
        if (ActiveStory is null || CurrentSection is null || string.IsNullOrWhiteSpace(SelectedMockAttachmentUrl))
        {
            return;
        }

        if (!CurrentSectionIsRazorPage)
        {
            MockGeneratorError = "Current section is not a Razor Page section.";
            return;
        }

        var razorCode = ExtractRazorCode(CurrentSection.Content);
        if (string.IsNullOrWhiteSpace(razorCode))
        {
            MockGeneratorError = "No Razor code block found in this section.";
            return;
        }

        var selectedAttachment = AvailableMockAttachments.FirstOrDefault(a => string.Equals(a.Url, SelectedMockAttachmentUrl, StringComparison.OrdinalIgnoreCase));
        if (selectedAttachment is null)
        {
            MockGeneratorError = "Select a mock attachment.";
            return;
        }

        IsGeneratingComparison = true;
        ShowMockAttachmentPicker = false;
        MockGeneratorError = string.Empty;
        ComparisonReportMarkdown = string.Empty;
        StartComparisonProgress("Preparing comparison...");
        StateHasChanged();

        try
        {
            UpdateComparisonProgress(28, "Loading selected mock...");
            var mockupHtml = await AzureDevOpsService.DownloadAttachmentTextAsync(
                selectedAttachment.Url,
                DevOpsPatToken.Trim(),
                CancellationToken.None);
            UpdateComparisonProgress(58, "Analyzing structures...");
            var prompt = BuildComparisonPrompt(razorCode, mockupHtml);
            ComparisonReportMarkdown = await OllamaService.GenerateTextAsync(prompt);
            UpdateComparisonProgress(88, "Finalizing report...");
            if (string.IsNullOrWhiteSpace(ComparisonReportMarkdown))
            {
                throw new InvalidOperationException("Comparison output was empty.");
            }
            UpdateComparisonProgress(100, "Comparison complete.");
        }
        catch (Exception ex)
        {
            MockGeneratorError = $"Comparison generation failed: {ex.Message}";
            StopComparisonProgress(reset: true);
        }
        finally
        {
            IsGeneratingComparison = false;
            if (string.IsNullOrWhiteSpace(MockGeneratorError))
            {
                _ = CompleteAndHideComparisonProgressAsync();
            }
            StateHasChanged();
        }
    }

    private void StartComparisonProgress(string initialLabel)
    {
        StopComparisonProgress(reset: true);
        ComparisonProgressPercent = 8;
        ComparisonProgressLabel = initialLabel;
        _comparisonProgressCts = new CancellationTokenSource();
        _ = SimulateComparisonProgressAsync(_comparisonProgressCts.Token);
    }

    private void UpdateComparisonProgress(int percent, string label)
    {
        ComparisonProgressPercent = Math.Max(ComparisonProgressPercent, Math.Min(percent, 100));
        ComparisonProgressLabel = label;
    }

    private void StopComparisonProgress(bool reset)
    {
        _comparisonProgressCts?.Cancel();
        _comparisonProgressCts?.Dispose();
        _comparisonProgressCts = null;

        if (reset)
        {
            ComparisonProgressPercent = 0;
            ComparisonProgressLabel = string.Empty;
            return;
        }

        ComparisonProgressPercent = 100;
    }

    private async Task CompleteAndHideComparisonProgressAsync()
    {
        StopComparisonProgress(reset: false);
        await InvokeAsync(StateHasChanged);
        await Task.Delay(900);
        if (!IsGeneratingComparison)
        {
            StopComparisonProgress(reset: true);
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task SimulateComparisonProgressAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ComparisonProgressPercent < 92)
            {
                await Task.Delay(350, ct);
                ComparisonProgressPercent = Math.Min(92, ComparisonProgressPercent + 3);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsMockAttachmentCandidate(DevOpsAttachmentItem attachment)
    {
        if (attachment is null)
        {
            return false;
        }

        var ext = attachment.Extension ?? string.Empty;
        var name = attachment.Name ?? string.Empty;
        var supportedExt = ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase);
        if (!supportedExt)
        {
            return false;
        }

        var positiveKeywords = new[] { "mock", "mockup", "wireframe", "screen", "ui" };
        var negativeKeywords = new[] { "plan", "report", "qa-report", "analysis", "checklist", "notes" };

        var hasPositive = positiveKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (!hasPositive)
        {
            return false;
        }

        var hasNegative = negativeKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        return !hasNegative;
    }

    private string BuildComparisonPrompt(string razorCode, string mockupHtml)
    {
        var template = Configuration["Validation:ComparisonPromptTemplate"];
        if (string.IsNullOrWhiteSpace(template) ||
            string.Equals(template, "__USE_DEFAULT_COMPARISON_PROMPT__", StringComparison.OrdinalIgnoreCase))
        {
            template = DefaultComparisonPromptTemplate;
        }

        var resolved = template
            .Replace("{{RAZOR_CODE}}", razorCode, StringComparison.Ordinal)
            .Replace("{{MOCKUP_HTML}}", mockupHtml, StringComparison.Ordinal);

        var hasRazorPlaceholder = template.Contains("{{RAZOR_CODE}}", StringComparison.Ordinal);
        var hasMockupPlaceholder = template.Contains("{{MOCKUP_HTML}}", StringComparison.Ordinal);
        if (!hasRazorPlaceholder || !hasMockupPlaceholder)
        {
            resolved += $"""

Razor file:
```razor
{razorCode}
```

Mockup HTML:
```html
{mockupHtml}
```
""";
        }

        return resolved;
    }

    private bool TryGetComparisonStructurePanels(out string razorStructure, out string mockupStructure)
    {
        razorStructure = string.Empty;
        mockupStructure = string.Empty;

        if (string.IsNullOrWhiteSpace(ComparisonReportMarkdown))
        {
            return false;
        }

        var razorMatch = Regex.Match(
            ComparisonReportMarkdown,
            @"##\s*Razor\s*```text\s*(?<body>[\s\S]*?)```",
            RegexOptions.IgnoreCase);

        var mockupMatch = Regex.Match(
            ComparisonReportMarkdown,
            @"##\s*Mockup\s*```text\s*(?<body>[\s\S]*?)```",
            RegexOptions.IgnoreCase);

        if (!razorMatch.Success || !mockupMatch.Success)
        {
            return false;
        }

        razorStructure = razorMatch.Groups["body"].Value.Trim();
        mockupStructure = mockupMatch.Groups["body"].Value.Trim();
        return !string.IsNullOrWhiteSpace(razorStructure) && !string.IsNullOrWhiteSpace(mockupStructure);
    }

    private const string DefaultComparisonPromptTemplate = """
You are an expert Razor-to-Mockup UI migration analyzer.

Your task is to compare:

1. Razor component structure
2. Mockup HTML structure

The MOCKUP is the PRIMARY SOURCE OF TRUTH.

You must generate output in EXACTLY the same format and structure shown below.

==================================================
CRITICAL PARSING RULE
=====================

Preserve EXACT physical source order from the Razor file.

DO NOT:

* infer semantic workflow order
* rearrange controls logically
* optimize hierarchy
* reorder based on UX assumptions
* interpret intended workflow

The generated structure MUST strictly follow:

* exact control order
* exact nesting order
* exact column order
* exact source-code hierarchy

from the Razor source file.

==================================================
RULES
=====

1. ONLY compare:

   * controls
   * control hierarchy
   * control sequence
   * field order
   * field captions

2. IGNORE:

   * styling
   * CSS
   * colors
   * spacing
   * UX improvements
   * visual enhancements
   * app shell differences
   * responsiveness
   * typography
   * animations
   * visual states

3. Treat MOCKUP as expected/base structure.

4. Show ONLY differences.

5. DO NOT explain business logic.

6. DO NOT generate prose paragraphs.

7. Output MUST remain highly structured.

==================================================
IMPORTANT SOURCE ORDER RULE
===========================

The structure diagram MUST reflect EXACT Razor source order.

Example:

If Razor contains:

<MasterGrid />
<CompanyFilter />
<ContextIndicator />

Then output MUST be:

Master Grid
↓
Company Filter
↓
Context Indicator

Even if UX or semantics suggest otherwise.

==================================================
GROUPING RULE
=============

If a Razor grid column contains:

GroupIndex="0"

DO NOT display it as a normal sequential field.

Instead represent it as:

⚠ Grouped By: FieldName

Example:

Wrong:
CostHeadName
↓
Checkbox

Correct:
⚠ Grouped By: CostHeadName
↓
Checkbox

==================================================
HIGHLIGHTING RULES
==================

Use these markers EXACTLY:

✅ Matching controls = normal text only
❌ Missing/mismatched controls
🔄 Sequence mismatch
➕ Extra controls
⚠ Different hierarchy/grouping

DO NOT invent new symbols.

==================================================
OUTPUT FORMAT
=============

# COMPLETE CONTROL & FIELD SEQUENCE COMPARISON

# OVERALL PAGE STRUCTURE

## Razor

```text
Header
↓
Master Grid
    ├── Code
    ├── Name
    ├── 🔄 InterProject
    ├── 🔄 VoucherKind
    ├── TaxesEnabled
    ├── DoNotBookCommitment
    └── DoNotShowInCtd
↓
❌ Company Filter
↓
Context Indicator
↓
Tabs
    ├── Account Links Grid
    │   ├── IsLinked
    │   ├── AccountCode
    │   └── AccountName
    │
    └── Cost Code Grid
        ├── ⚠ Grouped By: CostHeadName
        ├── 🔄 IsLinked
        ├── CostCode
        └── CostCodeName
```

---

## Mockup

```text
Header
↓
Master Grid
    ├── Code
    ├── Name
    ├── VoucherKind
    ├── InterProject
    ├── TaxesEnabled
    ├── DoNotBookCommitment
    ├── DoNotShowInCtd
    └── ➕ Actions
↓
Context Indicator
↓
Company Filter
↓
Tabs
    ├── Account Links Grid
    │   ├── Select
    │   ├── Account Number
    │   └── Description
    │
    └── Cost Code Grid
        ├── Select
        ├── Cost Head
        ├── Cost Code
        └── Description
```

---

# COMPLETE DIFFERENCE SUMMARY

| Mockup/Base Expectation                                                              | Current Razor Difference                               | Type                   | Rectification Prompt                                                                                                                           |
| ------------------------------------------------------------------------------------ | ------------------------------------------------------ | ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------- |
| Company Filter should appear AFTER Context Indicator and BEFORE Tabs                 | Razor places Company Filter BEFORE Context Indicator   | ⚠ Hierarchy Difference | Move the Company Filter section below Context Indicator and above Tabs to match the mockup structure.                                          |
| Master Grid field order should be: `Code → Name → VoucherKind → InterProject`        | Razor uses: `Code → Name → InterProject → VoucherKind` | 🔄 Sequence Mismatch   | Reorder the `DxGridDataColumn` definitions inside `XpedeonCrudGrid` so that `VoucherKind` appears before `InterProject`.                       |
| Master Grid should contain an `Actions` column at the end                            | Razor Master Grid does not contain an Actions column   | ➕ Missing Control      | Add an Actions column at the end of the `XpedeonCrudGrid` with row-level actions matching the mockup structure.                                |
| Cost Code Grid should display Cost Head as visible column                            | Razor uses `GroupIndex="0"` for CostHeadName           | ⚠ Different Hierarchy  | Remove `GroupIndex="0"` from `CostHeadName` and render it as a visible standard column.                                                        |
| Cost Code Grid field order should be: `Select → Cost Head → Cost Code → Description` | Razor uses grouped CostHead followed by checkbox       | 🔄 Sequence Mismatch   | Reorder Cost Code Grid columns so the checkbox column appears first, followed by visible `CostHeadName`, then `CostCode`, then `CostCodeName`. |
| Account Links Grid captions should match mockup                                      | Razor uses technical labels                            | ❌ Label Mismatch       | Rename Account Grid captions to `Select`, `Account Number`, and `Description` while preserving bindings.                                       |
| Cost Code Grid captions should match mockup                                          | Razor uses technical labels                            | ❌ Label Mismatch       | Rename Cost Code Grid captions to `Select`, `Cost Code`, and `Description` while preserving bindings.                                          |
| Master Grid caption should display `Type`                                            | Razor uses `VoucherKind` caption                       | ❌ Label Mismatch       | Change the `VoucherKind` column caption to `Type`.                                                                                             |

==================================================
COMPARISON REQUIREMENTS
=======================

You MUST compare:

1. Exact top-level layout order
2. Exact grid order
3. Exact tab order
4. Exact child section order
5. Exact column order
6. Exact field captions
7. Missing controls
8. Extra controls
9. Grouped vs visible columns
10. Hierarchy mismatches
11. Source-order mismatches

==================================================
FIELD ORDER RULES
=================

Field order comparison is mandatory.

Example:

Razor:
Code
↓
Name
↓
InterProject
↓
VoucherKind

Mockup:
Code
↓
Name
↓
VoucherKind
↓
InterProject

Must generate:

🔄 Sequence Mismatch

==================================================
LABEL RULES
===========

If captions differ:

Example:
IsLinked vs Select

Then generate:

❌ Label Mismatch

==================================================
RECTIFICATION PROMPT RULES
==========================

Every mismatch MUST contain:

* precise fix instruction
* exact control name
* exact movement/reorder instruction
* exact expected structure

Do NOT generate vague prompts.

==================================================
IMPORTANT
=========

1. Keep output deterministic.
2. Preserve exact section headings.
3. Preserve markdown table structure.
4. Preserve tree indentation.
5. Do NOT summarize.
6. Do NOT shorten.
7. Do NOT add extra commentary.
8. Output should be production-review quality.
9. Preserve EXACT Razor source order.
10. Never infer semantic order over physical order.

Now analyze the supplied Razor file and Mockup HTML and generate the comparison report in EXACTLY this format.
""";


    private static string ExtractRazorCode(string sectionContent)
    {
        if (string.IsNullOrWhiteSpace(sectionContent))
        {
            return string.Empty;
        }

        var blockRegex = new Regex(@"```(?<lang>[a-zA-Z0-9_-]+)?\s*\r?\n(?<code>[\s\S]*?)```", RegexOptions.IgnoreCase);
        var matches = blockRegex.Matches(sectionContent);
        if (matches.Count > 0)
        {
            // Prefer blocks that look like Razor/component markup.
            foreach (Match m in matches)
            {
                var code = m.Groups["code"].Value.Trim();
                if (LooksLikeRazorMarkup(code))
                {
                    return code;
                }
            }

            // Fallback to first known markup language block.
            foreach (Match m in matches)
            {
                var lang = (m.Groups["lang"].Value ?? string.Empty).Trim().ToLowerInvariant();
                if (lang is "razor" or "cshtml" or "html")
                {
                    return m.Groups["code"].Value.Trim();
                }
            }

            // Do not fallback to arbitrary code blocks like csharp.
            return string.Empty;
        }

        return sectionContent.Contains("@page", StringComparison.OrdinalIgnoreCase)
            ? sectionContent.Trim()
            : string.Empty;
    }

    private static bool IsLikelyRazorSection(string heading, string content)
    {
        if (!string.IsNullOrWhiteSpace(heading) &&
            heading.Contains("Razor Page", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return content.Contains("```razor", StringComparison.OrdinalIgnoreCase)
            || content.Contains("@page", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<Dx", StringComparison.OrdinalIgnoreCase)
            || content.Contains("<Xpedeon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRazorMarkup(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return code.Contains("@page", StringComparison.OrdinalIgnoreCase)
            || code.Contains("<Dx", StringComparison.OrdinalIgnoreCase)
            || code.Contains("<Xpedeon", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(code, @"<\s*[A-Z][A-Za-z0-9]*", RegexOptions.Compiled);
    }

    private static RazorMockScreen BuildMockScreenModel(string razorCode, string fallbackTitle)
    {
        var title = ExtractTitle(razorCode, fallbackTitle);
        var breadcrumbs = ExtractBreadcrumbHints(razorCode);
        var controls = ExtractControls(razorCode);
        var tabNames = ExtractTabNames(razorCode);
        var gridColumns = ExtractGridColumns(razorCode);

        return new RazorMockScreen
        {
            Title = title,
            Breadcrumbs = breadcrumbs,
            Controls = controls,
            TabNames = tabNames,
            GridColumns = gridColumns
        };
    }

    private static string ExtractTitle(string razorCode, string fallbackTitle)
    {
        var titleMatch = Regex.Match(razorCode, "Title\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (titleMatch.Success)
        {
            return titleMatch.Groups[1].Value.Trim();
        }

        var pageMatch = Regex.Match(razorCode, "@page\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (pageMatch.Success)
        {
            return pageMatch.Groups[1].Value.Trim('/');
        }

        return string.IsNullOrWhiteSpace(fallbackTitle) ? "Generated screen" : fallbackTitle;
    }

    private static List<string> ExtractBreadcrumbHints(string razorCode)
    {
        var crumbs = new List<string>();
        foreach (Match match in Regex.Matches(razorCode, "\"([^\"]{2,})\""))
        {
            var token = match.Groups[1].Value.Trim();
            if (token.Length > 40)
            {
                continue;
            }

            if (!Regex.IsMatch(token, "^[A-Za-z][A-Za-z0-9\\s\\-/&]+$"))
            {
                continue;
            }

            if (crumbs.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            crumbs.Add(token);
            if (crumbs.Count == 4)
            {
                break;
            }
        }

        return crumbs;
    }

    private static List<RazorMockControl> ExtractControls(string razorCode)
    {
        var controls = new List<RazorMockControl>();
        var tagRegex = new Regex(@"<(?<name>[A-Za-z][A-Za-z0-9]*)\b(?<attrs>[^>]*)/?>", RegexOptions.Compiled);

        foreach (Match tagMatch in tagRegex.Matches(razorCode))
        {
            var name = tagMatch.Groups["name"].Value;
            if (!IsMockableTag(name))
            {
                continue;
            }

            if (IsNoiseTag(name))
            {
                continue;
            }

            var attrs = tagMatch.Groups["attrs"].Value;
            var label = ExtractAttributeValue(attrs, "Caption")
                        ?? ExtractAttributeValue(attrs, "Text")
                        ?? ExtractAttributeValue(attrs, "Label")
                        ?? name;

            var controlType = MapControlType(name);
            controls.Add(new RazorMockControl
            {
                Name = name,
                ControlType = controlType,
                Label = NormalizeLabel(label)
            });
        }

        return controls;
    }

    private static bool IsMockableTag(string tagName)
    {
        if (tagName.StartsWith("Dx", StringComparison.Ordinal))
        {
            return true;
        }

        if (tagName.StartsWith("Xpedeon", StringComparison.Ordinal))
        {
            return true;
        }

        return tagName is "SimpleListPageHeader" or "EditForm" or "ValidationSummary";
    }

    private static bool IsNoiseTag(string tagName)
    {
        return tagName.EndsWith("Settings", StringComparison.OrdinalIgnoreCase)
               || tagName is "DxListEditorColumn"
               || tagName is "DxModelHost";
    }

    private static string? ExtractAttributeValue(string attrs, string attrName)
    {
        var match = Regex.Match(attrs, $"{attrName}\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string MapControlType(string tagName)
    {
        return tagName switch
        {
            "DxTextBox" => "Text Input",
            "DxComboBox" => "Dropdown",
            "DxDateEdit" => "Date Picker",
            "DxCheckBox" => "Checkbox",
            "DxButton" => "Button",
            "DxGrid" => "Data Grid",
            "DxTabs" => "Tabs",
            "DxTabPage" => "Tab Page",
            "DxFormLayout" => "Form",
            "DxMemo" => "Multi-line Input",
            "SimpleListPageHeader" => "Page Header",
            _ when tagName.StartsWith("Dx", StringComparison.Ordinal) => "DevExpress Component",
            _ => "Generic Component"
        };
    }

    private static string NormalizeLabel(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Field";
        }

        var nameofMatch = Regex.Match(raw, @"nameof\(([^)]+)\)", RegexOptions.IgnoreCase);
        if (nameofMatch.Success)
        {
            var token = nameofMatch.Groups[1].Value.Trim();
            var lastPart = token.Split('.').LastOrDefault() ?? token;
            return HumanizeToken(lastPart);
        }

        if (raw.StartsWith("@", StringComparison.Ordinal))
        {
            return "Field";
        }

        return HumanizeToken(raw);
    }

    private static string HumanizeToken(string value)
    {
        var trimmed = value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Field";
        }

        var withSpaces = Regex.Replace(trimmed, "([a-z])([A-Z])", "$1 $2");
        withSpaces = withSpaces.Replace("_", " ", StringComparison.Ordinal);
        return Regex.Replace(withSpaces, @"\s+", " ").Trim();
    }

    private static List<string> ExtractTabNames(string razorCode)
    {
        var names = new List<string>();
        var tabMatches = Regex.Matches(razorCode, @"<TextTemplate>([\s\S]*?)</TextTemplate>", RegexOptions.IgnoreCase);
        foreach (Match tab in tabMatches)
        {
            var content = tab.Groups[1].Value;
            var literal = Regex.Match(content, "\"([^\"]+)\"");
            var raw = literal.Success ? literal.Groups[1].Value : content;
            var normalized = NormalizeLabel(raw);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("Field", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!names.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(normalized);
            }
        }

        return names;
    }

    private static List<string> ExtractGridColumns(string razorCode)
    {
        var columns = new List<string>();
        var columnMatches = Regex.Matches(razorCode, @"<DxGridDataColumn\b([\s\S]*?)(/?>)", RegexOptions.IgnoreCase);
        foreach (Match columnMatch in columnMatches)
        {
            var attrs = columnMatch.Groups[1].Value;
            var fieldName = ExtractAttributeValue(attrs, "FieldName") ?? string.Empty;
            var label = NormalizeLabel(fieldName);
            if (label.Equals("Field", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!columns.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                columns.Add(label);
            }
        }

        return columns;
    }

    private static string BuildMockScreenHtml(RazorMockScreen screen)
    {
        var fieldControls = screen.Controls
            .Where(c => c.ControlType is "Text Input" or "Dropdown" or "Date Picker" or "Checkbox" or "Multi-line Input")
            .ToList();
        var tabNames = screen.TabNames.Count > 0
            ? screen.TabNames
            : screen.Controls.Where(c => c.ControlType == "Tab Page").Select(c => c.Label).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var hasGrid = screen.GridColumns.Count > 0 || screen.Controls.Any(c => c.ControlType == "Data Grid");

        if (fieldControls.Count == 0)
        {
            fieldControls = new List<RazorMockControl>
            {
                new() { Name = "XpedeonCompanySelector", ControlType = "Dropdown", Label = "Company" }
            };
        }

        var cols = screen.GridColumns.Count > 0 ? screen.GridColumns.Take(6).ToList() : new List<string> { "Code", "Name", "Type" };
        var tabs = tabNames.Count > 0 ? tabNames : new List<string> { "Account Links", "Cost Code Links" };
        var title = EscapeHtml(screen.Title.Contains("localizer", StringComparison.OrdinalIgnoreCase) ? "Journal Voucher Types" : screen.Title);
        var breadcrumb = screen.Breadcrumbs.Count > 0 ? string.Join(" / ", screen.Breadcrumbs.Select(EscapeHtml)) : "Nominal Ledger / Setup";

        var sb = new StringBuilder();
        sb.Append("""
<style>
.tm{background:#e8e4f5;border:1px solid rgba(196,191,224,.55);border-radius:12px;overflow:hidden;font-family:'Barlow',Segoe UI,Arial,sans-serif}
.tm-head{padding:12px 16px;border-bottom:1px solid rgba(196,191,224,.55);background:#fff}
.tm-bc{font-size:11px;color:#9489c0}
.tm-title{font-family:'Barlow Condensed',Segoe UI,sans-serif;font-size:24px;color:#452e82;font-weight:700;margin:2px 0 0}
.tm-content{padding:14px}
.tm-card{background:#fff;border:1px solid rgba(196,191,224,.55);border-radius:12px;overflow:hidden;margin-bottom:12px}
.tm-card-h{padding:10px 14px;background:#faf9fe;border-bottom:1px solid rgba(196,191,224,.5);font-size:12px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#452e82}
.tm-grid{width:100%;border-collapse:collapse}
.tm-grid th{font-size:10px;text-transform:uppercase;letter-spacing:.1em;color:#9489c0;background:#faf9fe;padding:10px 12px;text-align:left;border-bottom:1px solid rgba(196,191,224,.5)}
.tm-grid td{padding:11px 12px;border-bottom:1px solid rgba(196,191,224,.25)}
.tm-tabs{display:flex;gap:6px;padding:10px 12px;background:#fff;border-bottom:1px solid rgba(196,191,224,.5)}
.tm-tab{padding:6px 12px;border-radius:999px;font-size:12px;background:#f3f0fd;color:#5a4e8a;border:1px solid rgba(196,191,224,.7)}
.tm-tab.active{background:#e2d8ff;color:#452e82;font-weight:700}
.tm-actions{display:flex;justify-content:flex-end;gap:8px;padding:12px}
.tm-btn{padding:7px 14px;border-radius:8px;border:1px solid transparent;font-size:12px}
.tm-btn-save{background:#8c5cff;color:#fff}
.tm-btn-cancel{background:#fff;color:#5a4e8a;border-color:rgba(196,191,224,.8)}
.tm-filter{padding:10px 12px;border-bottom:1px solid rgba(196,191,224,.4);display:grid;grid-template-columns:220px 1fr;gap:10px;align-items:center}
.tm-sel{height:30px;border:1px solid rgba(196,191,224,.8);border-radius:8px;background:#fff}
</style>
""");
        sb.Append("<div class=\"tm\">");
        sb.Append($"<div class=\"tm-head\"><div class=\"tm-bc\">{breadcrumb}</div><div class=\"tm-title\">{title}</div></div>");
        sb.Append("<div class=\"tm-content\">");
        sb.Append("<div class=\"tm-card\">");
        sb.Append("<div class=\"tm-filter\"><strong>Company</strong><div class=\"tm-sel\"></div></div>");
        sb.Append("<div class=\"tm-card-h\">Journal Voucher Types</div>");
        if (hasGrid)
        {
            sb.Append("<table class=\"tm-grid\"><thead><tr>");
            foreach (var c in cols) sb.Append($"<th>{EscapeHtml(c)}</th>");
            sb.Append("</tr></thead><tbody><tr>");
            foreach (var _ in cols) sb.Append("<td>...</td>");
            sb.Append("</tr><tr>");
            foreach (var _ in cols) sb.Append("<td>...</td>");
            sb.Append("</tr></tbody></table>");
        }
        sb.Append("</div>");
        sb.Append("<div class=\"tm-card\">");
        sb.Append("<div class=\"tm-tabs\">");
        for (var i = 0; i < tabs.Count; i++) sb.Append($"<div class=\"tm-tab {(i == 0 ? "active" : string.Empty)}\">{EscapeHtml(tabs[i])}</div>");
        sb.Append("</div>");
        sb.Append("<div class=\"tm-card-h\">Linked Details</div>");
        sb.Append("<table class=\"tm-grid\"><thead><tr><th>Select</th><th>Code</th><th>Description</th></tr></thead><tbody><tr><td>âœ“</td><td>...</td><td>...</td></tr><tr><td>âœ“</td><td>...</td><td>...</td></tr></tbody></table>");
        sb.Append("</div>");
        sb.Append("<div class=\"tm-actions\"><button class=\"tm-btn tm-btn-save\">Save</button><button class=\"tm-btn tm-btn-cancel\">Cancel</button></div>");
        sb.Append("</div></div>");
        return sb.ToString();
    }

    private static string BuildAiMockPrompt(RazorMockScreen fastModel)
    {
        var inputJson = JsonSerializer.Serialize(fastModel, new JsonSerializerOptions { WriteIndented = true });
        return $$"""
You are a UI designer. Generate a polished mock screen specification from parsed Razor controls.
Return only valid JSON. No markdown. No explanation.

Required JSON schema:
{
  "title": "string",
  "breadcrumbs": ["string"],
  "sections": [
    {
      "name": "string",
      "kind": "form|tabs|table|actions",
      "fields": [
        { "label": "string", "controlType": "Text Input|Dropdown|Date Picker|Checkbox|Multi-line Input|Button", "placeholder": "string" }
      ],
      "tabs": ["string"],
      "columns": ["string"],
      "actions": ["string"]
    }
  ]
}

Rules:
- Keep labels human-friendly and short.
- Group similar fields under one form section.
- If controls include grids, add a table section with inferred columns.
- If controls include tab pages, add tabs section.
- Add actions section with Save and Cancel if no actions found.

Input model:
{{inputJson}}
""";
    }

    private static bool IsValidAiSpec(AiMockScreenSpec? spec)
    {
        return spec is not null
               && !string.IsNullOrWhiteSpace(spec.Title)
               && spec.Sections is not null
               && spec.Sections.Count > 0;
    }

    private static RazorMockScreen ToRazorMockScreen(AiMockScreenSpec spec)
    {
        var controls = new List<RazorMockControl>();
        var tabNames = new List<string>();
        var gridColumns = new List<string>();
        foreach (var section in spec.Sections)
        {
            foreach (var field in section.Fields)
            {
                controls.Add(new RazorMockControl
                {
                    Name = field.ControlType.Replace(" ", string.Empty, StringComparison.Ordinal),
                    ControlType = field.ControlType,
                    Label = field.Label
                });
            }

            foreach (var action in section.Actions)
            {
                controls.Add(new RazorMockControl
                {
                    Name = "DxButton",
                    ControlType = "Button",
                    Label = action
                });
            }

            foreach (var tab in section.Tabs)
            {
                tabNames.Add(tab);
                controls.Add(new RazorMockControl
                {
                    Name = "DxTabPage",
                    ControlType = "Tab Page",
                    Label = tab
                });
            }

            if (section.Kind.Equals("table", StringComparison.OrdinalIgnoreCase))
            {
                gridColumns.AddRange(section.Columns);
                controls.Add(new RazorMockControl
                {
                    Name = "DxGrid",
                    ControlType = "Data Grid",
                    Label = "Grid"
                });
            }
        }

        return new RazorMockScreen
        {
            Title = spec.Title,
            Breadcrumbs = spec.Breadcrumbs ?? new List<string>(),
            Controls = controls,
            TabNames = tabNames.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            GridColumns = gridColumns.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private sealed class RazorMockScreen
    {
        public string Title { get; init; } = string.Empty;
        public List<string> Breadcrumbs { get; init; } = new();
        public List<RazorMockControl> Controls { get; init; } = new();
        public List<string> TabNames { get; init; } = new();
        public List<string> GridColumns { get; init; } = new();
    }

    private sealed class RazorMockControl
    {
        public string Name { get; init; } = string.Empty;
        public string ControlType { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
    }

    private sealed class AiMockScreenSpec
    {
        public string Title { get; init; } = string.Empty;
        public List<string> Breadcrumbs { get; init; } = new();
        public List<AiMockSectionSpec> Sections { get; init; } = new();
    }

    private sealed class AiMockSectionSpec
    {
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = "form";
        public List<AiMockFieldSpec> Fields { get; init; } = new();
        public List<string> Tabs { get; init; } = new();
        public List<string> Columns { get; init; } = new();
        public List<string> Actions { get; init; } = new();
    }

    private sealed class AiMockFieldSpec
    {
        public string Label { get; init; } = string.Empty;
        public string ControlType { get; init; } = "Text Input";
        public string Placeholder { get; init; } = string.Empty;
    }

    private void RefreshSourceFilters()
    {
        _selectedSourceFile = null;
    }

    private void SelectSourceFile(string sourceFile)
    {
        _selectedSourceFile = sourceFile;
        var filtered = FilteredSections.ToList();
        if (filtered.Count == 0)
        {
            SelectedSectionId = null;
            return;
        }

        if (!filtered.Any(s => s.Id == SelectedSectionId))
        {
            SelectedSectionId = filtered[0].Id;
        }
    }

    private void ShowAllSourceFiles()
    {
        _selectedSourceFile = null;
        var filtered = FilteredSections.ToList();
        if (filtered.Count == 0)
        {
            SelectedSectionId = null;
            return;
        }

        if (!filtered.Any(s => s.Id == SelectedSectionId))
        {
            SelectedSectionId = filtered[0].Id;
        }
    }

    private void RemoveSourceFileFromList(string sourceFile)
    {
        var remaining = Sections
            .Where(section => !string.Equals(section.SourceFile, sourceFile, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (remaining.Count == Sections.Count)
        {
            return;
        }

        Sections.Clear();
        Sections.AddRange(remaining.Select((section, index) => new SectionModel
        {
            Id = $"section-{index + 1}",
            Letter = section.Letter,
            Heading = section.Heading,
            LineStart = section.LineStart,
            LineCount = section.LineCount,
            Content = section.Content,
            SourceFile = section.SourceFile
        }));

        if (string.Equals(_selectedSourceFile, sourceFile, StringComparison.OrdinalIgnoreCase))
        {
            _selectedSourceFile = null;
        }

        _reviewCache.Clear();

        if (Sections.Count == 0)
        {
            SelectedSectionId = null;
            return;
        }

        if (!Sections.Any(s => s.Id == SelectedSectionId))
        {
            SelectedSectionId = Sections[0].Id;
        }
    }

    private static List<SectionModel> ParseSections(string markdown, string sourceFile)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var sections = new List<SectionModel>();
        var headingRegex = new Regex(@"^##\s+(.+)$", RegexOptions.Compiled);

        var currentHeading = "Preamble";
        var currentStart = 1;
        var buffer = new List<string>();
        var encounteredHeading = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var headingMatch = headingRegex.Match(line);
            if (headingMatch.Success)
            {
                if (encounteredHeading || buffer.Count > 0)
                {
                    AddSection(sections, currentHeading, currentStart, buffer, sourceFile);
                }

                currentHeading = headingMatch.Groups[1].Value.Trim();
                currentStart = i + 1;
                buffer = new List<string>();
                encounteredHeading = true;
                continue;
            }

            buffer.Add(line);
        }

        if (encounteredHeading || buffer.Count > 0)
        {
            AddSection(sections, currentHeading, currentStart, buffer, sourceFile);
        }

        if (sections.Count == 0)
        {
            sections.Add(new SectionModel
            {
                Id = "section-1",
                Letter = "A",
                Heading = "Preamble",
                LineStart = 1,
                LineCount = lines.Length,
                Content = markdown.Trim(),
                SourceFile = sourceFile
            });
        }

        return sections;
    }

    private static void AddSection(List<SectionModel> sections, string heading, int lineStart, List<string> lines, string sourceFile)
    {
        var parsed = ParseHeading(heading, sections.Count);
        var content = string.Join('\n', lines).TrimEnd();

        sections.Add(new SectionModel
        {
            Id = $"section-{sections.Count + 1}",
            Letter = parsed.Letter,
            Heading = parsed.Title,
            LineStart = lineStart,
            LineCount = Math.Max(1, lines.Count),
            Content = content,
            SourceFile = sourceFile
        });
    }

    private static (string Letter, string Title) ParseHeading(string heading, int sectionIndex)
    {
        if (heading.Equals("Preamble", StringComparison.OrdinalIgnoreCase))
        {
            return ("P", "Preamble");
        }

        var match = Regex.Match(heading, @"^\s*([A-Z])\.\s+(.+)$");
        if (match.Success)
        {
            return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
        }

        var letter = ((char)('A' + (sectionIndex % 26))).ToString();
        return (letter, heading.Trim());
    }

    private static string RenderMarkdown(string markdown, IReadOnlyList<string>? highlightTerms = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "<p>(No section content)</p>";
        }

        var normalized = markdown.Replace("\r\n", "\n");
        var codeBlocks = new List<string>();

        normalized = Regex.Replace(
            normalized,
            @"```([^\r\n`]*)\r?\n([\s\S]*?)```",
            match =>
            {
                var language = match.Groups[1].Value.Trim();
                var code = EscapeHtml(match.Groups[2].Value.TrimEnd('\r', '\n'));
                var languageTag = string.IsNullOrWhiteSpace(language) ? "text" : EscapeHtml(language);
                var html =
                    "<div class=\"srs-code-wrap\">" +
                    "<div class=\"srs-code-lang\">" + languageTag + "</div>" +
                    "<pre><code>" + code + "</code></pre>" +
                    "</div>";
                codeBlocks.Add(html);
                return $"@@CODEBLOCK_{codeBlocks.Count - 1}@@";
            },
            RegexOptions.Multiline);

        var lines = normalized.Split('\n');
        var sb = new StringBuilder();

        var i = 0;
        while (i < lines.Length)
        {
            var rawLine = lines[i];
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                i++;
                continue;
            }

            if (TryRenderCodePlaceholder(trimmed, codeBlocks, sb))
            {
                i++;
                continue;
            }

            if (TryRenderHeading(line, sb))
            {
                i++;
                continue;
            }

            if (IsHorizontalRule(line))
            {
                sb.AppendLine("<hr />");
                i++;
                continue;
            }

            if (trimmed.StartsWith(">"))
            {
                var blockquoteLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    var quoteLine = lines[i].TrimStart();
                    var content = quoteLine.Length > 1 ? quoteLine[1..].TrimStart() : string.Empty;
                    blockquoteLines.Add($"<p>{FormatInline(content)}</p>");
                    i++;
                }

                sb.AppendLine($"<blockquote>{string.Join(string.Empty, blockquoteLines)}</blockquote>");
                continue;
            }

            if (IsTableHeaderLine(line, lines, i))
            {
                var tableHtml = RenderTable(lines, ref i);
                sb.AppendLine(tableHtml);
                continue;
            }

            if (IsUnorderedListLine(line))
            {
                sb.AppendLine("<ul>");
                while (i < lines.Length && IsUnorderedListLine(lines[i]))
                {
                    var item = Regex.Replace(lines[i], @"^\s*-\s+", string.Empty);
                    sb.AppendLine($"<li>{FormatInline(item)}</li>");
                    i++;
                }
                sb.AppendLine("</ul>");
                continue;
            }

            if (IsOrderedListLine(line))
            {
                sb.AppendLine("<ol>");
                while (i < lines.Length && IsOrderedListLine(lines[i]))
                {
                    var item = Regex.Replace(lines[i], @"^\s*\d+\.\s+", string.Empty);
                    sb.AppendLine($"<li>{FormatInline(item)}</li>");
                    i++;
                }
                sb.AppendLine("</ol>");
                continue;
            }

            var paragraphLines = new List<string>();
            while (i < lines.Length)
            {
                var look = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(look) ||
                    Regex.IsMatch(look, @"^@@CODEBLOCK_\d+@@$") ||
                    IsHorizontalRule(lines[i]) ||
                    IsUnorderedListLine(lines[i]) ||
                    IsOrderedListLine(lines[i]) ||
                    lines[i].TrimStart().StartsWith(">") ||
                    Regex.IsMatch(lines[i], @"^#{2,4}\s+") ||
                    IsTableHeaderLine(lines[i], lines, i))
                {
                    break;
                }

                paragraphLines.Add(FormatInline(lines[i].Trim()));
                i++;
            }

            if (paragraphLines.Count > 0)
            {
                sb.AppendLine($"<p>{string.Join("<br />", paragraphLines)}</p>");
            }
            else
            {
                i++;
            }
        }

        var html = sb.ToString();
        return HighlightSearchTermsInHtml(html, highlightTerms);
    }

    private static string HighlightSearchTermsInHtml(string html, IReadOnlyList<string>? highlightTerms)
    {
        var terms = (highlightTerms ?? Array.Empty<string>())
            .Select(term => (term ?? string.Empty).Trim())
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(term => term.Length)
            .ToList();

        if (terms.Count == 0)
        {
            return html;
        }

        var safePattern = string.Join("|", terms.Select(Regex.Escape));
        var tokenized = Regex.Split(html, "(<[^>]+>)");
        for (var i = 0; i < tokenized.Length; i++)
        {
            var token = tokenized[i];
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith("<", StringComparison.Ordinal))
            {
                continue;
            }

            tokenized[i] = Regex.Replace(
                token,
                safePattern,
                "<mark class=\"srs-search-hit\">$0</mark>",
                RegexOptions.IgnoreCase);
        }

        return string.Concat(tokenized);
    }

    private static bool TryRenderHeading(string line, StringBuilder sb)
    {
        var h4Match = Regex.Match(line, @"^####\s+(.+)$");
        if (h4Match.Success)
        {
            sb.AppendLine($"<h4>{FormatInline(h4Match.Groups[1].Value)}</h4>");
            return true;
        }

        var h3Match = Regex.Match(line, @"^###\s+(.+)$");
        if (h3Match.Success)
        {
            sb.AppendLine($"<h3>{FormatInline(h3Match.Groups[1].Value)}</h3>");
            return true;
        }

        var h2Match = Regex.Match(line, @"^##\s+(.+)$");
        if (h2Match.Success)
        {
            sb.AppendLine($"<h2>{FormatInline(h2Match.Groups[1].Value)}</h2>");
            return true;
        }

        return false;
    }

    private static bool IsHorizontalRule(string line)
    {
        return Regex.IsMatch(line.Trim(), @"^---+$");
    }

    private static bool IsUnorderedListLine(string line)
    {
        return Regex.IsMatch(line, @"^\s*-\s+");
    }

    private static bool IsOrderedListLine(string line)
    {
        return Regex.IsMatch(line, @"^\s*\d+\.\s+");
    }

    private static bool IsTableHeaderLine(string line, IReadOnlyList<string> lines, int index)
    {
        if (index + 1 >= lines.Count)
        {
            return false;
        }

        var next = lines[index + 1];
        var hasPipe = line.Contains('|');
        var separatorMatch = Regex.IsMatch(next.Trim(), @"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$");
        return hasPipe && separatorMatch;
    }

    private static string RenderTable(IReadOnlyList<string> lines, ref int index)
    {
        var headerCells = SplitTableCells(lines[index]);
        index += 2;

        var bodyRows = new List<string>();
        while (index < lines.Count && lines[index].Contains('|') && !string.IsNullOrWhiteSpace(lines[index].Trim()))
        {
            var rowCells = SplitTableCells(lines[index]);
            var cols = new StringBuilder();
            foreach (var cell in rowCells)
            {
                cols.Append($"<td>{FormatInline(cell)}</td>");
            }
            bodyRows.Add($"<tr>{cols}</tr>");
            index++;
        }

        var headBuilder = new StringBuilder();
        foreach (var header in headerCells)
        {
            headBuilder.Append($"<th>{FormatInline(header)}</th>");
        }

        var bodyContent = bodyRows.Count == 0 ? "<tr></tr>" : string.Join(string.Empty, bodyRows);
        return
            "<div class=\"srs-table-wrap\">" +
            "<table>" +
            "<thead><tr>" + headBuilder + "</tr></thead>" +
            "<tbody>" + bodyContent + "</tbody>" +
            "</table>" +
            "</div>";
    }

    private static List<string> SplitTableCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("|"))
        {
            trimmed = trimmed[1..];
        }
        if (trimmed.EndsWith("|"))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    private static bool TryRenderCodePlaceholder(string token, IReadOnlyList<string> codeBlocks, StringBuilder sb)
    {
        var match = Regex.Match(token, @"^@@CODEBLOCK_(\d+)@@$");
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out var index) || index < 0 || index >= codeBlocks.Count)
        {
            return false;
        }

        sb.AppendLine(codeBlocks[index]);
        return true;
    }

    private static MarkupString FormatInlineMarkup(string? text) =>
        new MarkupString(FormatInline(text ?? string.Empty));

    private static string FormatInline(string input)
    {
        var text = EscapeHtml(input);

        text = Regex.Replace(text, @"`([^`\n]+)`", m => $"<code class=\"srs-inline-code\">{m.Groups[1].Value}</code>");
        text = Regex.Replace(text, @"\*\*\*([^\n]+?)\*\*\*", "<strong><em>$1</em></strong>");
        text = Regex.Replace(text, @"\*\*([^\n*]+?)\*\*", "<strong>$1</strong>");
        text = Regex.Replace(text, @"\*([^\n*]+?)\*", "<em>$1</em>");
        text = Regex.Replace(text, @"~~([^\n~]+?)~~", "<del>$1</del>");
        text = Regex.Replace(text, @"\[(.+?)\]\((.+?)\)", "<a href=\"$2\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>");
        return text;
    }

    private static string EscapeHtml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static string JsonElementToDisplay(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "-",
            JsonValueKind.Undefined => "-",
            JsonValueKind.Array => element.ToString(),
            JsonValueKind.Object => element.ToString(),
            _ => element.ToString()
        };
    }
}

