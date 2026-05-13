using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.DevOps;
using SmartReviewSystem.Models.Ui;
using SmartReviewSystem.Services.DevOps;

namespace SmartReviewSystem.Pages;

public partial class Home : ComponentBase
{
    [Inject]
    private IJSRuntime JS { get; set; } = default!;
    [Inject]
    private IAzureDevOpsService AzureDevOpsService { get; set; } = default!;
    [Inject]
    private IConfiguration Configuration { get; set; } = default!;

    private readonly IReadOnlyList<string> SeverityOrder = new[] { "error", "warning", "info" };

    private enum SectionFilterMode
    {
        All,
        Issues,
        Clean,
        ReviewRequired,
        CustomPattern
    }

    private sealed record ConfiguredSectionTab(string Name, IReadOnlyList<string> Patterns);

    private enum UploadSource
    {
        Local,
        AzureDevOps
    }

    // ============================================================
    // SECTION 1: ANTIPATTERN RULEBOOK
    // ============================================================
    private readonly IReadOnlyList<AntipatternRule> Rulebook = CreateRulebook();

    // ============================================================
    // SECTION 2: MARKDOWN PARSER (regex-based renderer)
    // ============================================================
    private readonly List<SectionModel> Sections = new();
    private readonly HashSet<string> ExpandedReasonKeys = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<Violation>> ViolationsBySection = new(StringComparer.OrdinalIgnoreCase);
    private string? UploadedFileName;
    private string UploadError = string.Empty;
    private string? SelectedSectionId;
    private string SectionSearchText = string.Empty;
    private SectionFilterMode SectionFilter = SectionFilterMode.All;
    private readonly List<ConfiguredSectionTab> ConfiguredSectionTabs = new();
    private string? ActiveCustomTabName;
    private bool IsDragging;
    private bool IsViolationsPanelCollapsed;
    private bool ShowRevisionModal;
    private string RevisionPromptText = string.Empty;
    private string CopyButtonLabel = "Copy to Clipboard";
    private UploadSource CurrentUploadSource = UploadSource.AzureDevOps;
    private string DevOpsOrganization = string.Empty;
    private string DevOpsProject = string.Empty;
    private string DevOpsPatToken = string.Empty;
    private string DevOpsCondition = "[System.WorkItemType] = 'User Story' AND [System.State] <> 'Closed'";
    private string DevOpsBuiltQuery = string.Empty;
    private bool UseAdvancedWiql;
    private string DevOpsTagFilter = "Master AI Development";
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

    protected override void OnInitialized()
    {
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
    private int TotalIssueCount => ViolationsBySection.Values.Sum(violations => violations.Count);
    private int CleanSectionCount => Sections.Count(section => GetViolationsForSection(section.Id).Count == 0);
    private IEnumerable<SectionModel> IssueSections => Sections.Where(section => GetViolationsForSection(section.Id).Count > 0);

    private IEnumerable<SectionModel> FilteredSections
    {
        get
        {
            var query = SectionSearchText.Trim();
            IEnumerable<SectionModel> sections = SectionFilter switch
            {
                SectionFilterMode.Issues => Sections.Where(section => GetViolationsForSection(section.Id).Count > 0),
                SectionFilterMode.Clean => Sections.Where(section => GetViolationsForSection(section.Id).Count == 0),
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
            ProcessUploadedContent(fileName, content);
        }
        catch (Exception ex)
        {
            DevOpsError = $"Failed to analyze attachment '{attachment.Name}': {ex.Message}";
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
        UploadedFileName = fileName;
        ActiveCustomTabName = null;
        Sections.Clear();
        Sections.AddRange(ParseSections(content));
        SelectedSectionId = Sections.FirstOrDefault()?.Id;
        var activeProfiles = DetectActiveRuleProfiles(content, fileName);
        ViolationsBySection = RunAllRules(Sections, Rulebook, activeProfiles);
        ExpandedReasonKeys.Clear();
        SectionSearchText = string.Empty;
        SectionFilter = SectionFilterMode.All;
        IsViolationsPanelCollapsed = false;
        ShowRevisionModal = false;
        CopyButtonLabel = "Copy to Clipboard";

        if (Sections.Count == 0)
        {
            UploadError = "The uploaded file has no parsable sections.";
        }
    }

    private void ResetAll()
    {
        UploadedFileName = null;
        UploadError = string.Empty;
        SelectedSectionId = null;
        Sections.Clear();
        ViolationsBySection = new Dictionary<string, List<Violation>>(StringComparer.OrdinalIgnoreCase);
        ExpandedReasonKeys.Clear();
        SectionSearchText = string.Empty;
        SectionFilter = SectionFilterMode.All;
        ActiveCustomTabName = null;
        IsViolationsPanelCollapsed = false;
        ShowRevisionModal = false;
        RevisionPromptText = string.Empty;
        CopyButtonLabel = "Copy to Clipboard";
        DevOpsError = string.Empty;
        DevOpsTotalStories = 0;
        DevOpsStoriesWithAttachments = 0;
        DevOpsSupportedAttachmentCount = 0;
        DevOpsUnsupportedAttachmentCount = 0;
        DevOpsUnsupportedExtensionSummary = string.Empty;
        DevOpsConnectionStatus = "Idle";
        DevOpsBuiltQuery = string.Empty;
    }

    private void SelectSection(string sectionId)
    {
        SelectedSectionId = sectionId;
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

    private bool HasAdjacentIssue(int direction)
    {
        if (SelectedSectionId is null)
        {
            return false;
        }

        var issueSections = IssueSections.ToList();
        var currentIndex = issueSections.FindIndex(section => section.Id == SelectedSectionId);
        if (currentIndex < 0)
        {
            currentIndex = Sections.FindIndex(section => section.Id == SelectedSectionId);
            return direction > 0
                ? issueSections.Any(section => Sections.IndexOf(section) > currentIndex)
                : issueSections.Any(section => Sections.IndexOf(section) < currentIndex);
        }

        var nextIndex = currentIndex + direction;
        return nextIndex >= 0 && nextIndex < issueSections.Count;
    }

    private void SelectAdjacentIssue(int direction)
    {
        if (SelectedSectionId is null)
        {
            return;
        }

        var issueSections = IssueSections.ToList();
        if (issueSections.Count == 0)
        {
            return;
        }

        var currentIssueIndex = issueSections.FindIndex(section => section.Id == SelectedSectionId);
        if (currentIssueIndex >= 0)
        {
            var nextIssueIndex = Math.Clamp(currentIssueIndex + direction, 0, issueSections.Count - 1);
            SelectedSectionId = issueSections[nextIssueIndex].Id;
            return;
        }

        var currentSectionIndex = Sections.FindIndex(section => section.Id == SelectedSectionId);
        var nextSection = direction > 0
            ? issueSections.FirstOrDefault(section => Sections.IndexOf(section) > currentSectionIndex)
            : issueSections.LastOrDefault(section => Sections.IndexOf(section) < currentSectionIndex);

        if (nextSection is not null)
        {
            SelectedSectionId = nextSection.Id;
        }
    }

    private void ToggleViolationsPanel()
    {
        IsViolationsPanelCollapsed = !IsViolationsPanelCollapsed;
    }

    private void ToggleReason(string key)
    {
        if (!ExpandedReasonKeys.Add(key))
        {
            ExpandedReasonKeys.Remove(key);
        }
    }

    private string BuildReasonKey(string sectionId, Violation violation, int groupIndex)
    {
        return $"{sectionId}|{violation.RuleId}|{groupIndex}|{violation.Matched.GetHashCode()}";
    }

    private List<Violation> GetViolationsForSection(string sectionId)
    {
        return ViolationsBySection.TryGetValue(sectionId, out var violations)
            ? violations
            : new List<Violation>();
    }

    private SectionStatus GetSectionStatus(string sectionId)
    {
        var violations = GetViolationsForSection(sectionId);
        var errorCount = violations.Count(v => IsSeverity(v.Severity, "error"));
        var warningCount = violations.Count(v => IsSeverity(v.Severity, "warning"));
        var total = violations.Count;

        if (errorCount > 0)
        {
            return new SectionStatus
            {
                BadgeText = total.ToString(),
                CssClass = "srs-badge-error"
            };
        }

        if (warningCount > 0 || total > 0)
        {
            return new SectionStatus
            {
                BadgeText = total.ToString(),
                CssClass = "srs-badge-warning"
            };
        }

        return new SectionStatus
        {
            BadgeText = "OK",
            CssClass = "srs-badge-clean"
        };
    }

    private static bool IsSeverity(string value, string expected)
    {
        return value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSeverityHeading(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "error" => "Errors",
            "warning" => "Warnings",
            "info" => "Info",
            _ => "Other"
        };
    }

    private static string GetSeverityClass(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "error" => "severity-error",
            "warning" => "severity-warning",
            "info" => "severity-info",
            _ => "severity-info"
        };
    }

    private static string GetSectionConclusion(IReadOnlyCollection<Violation> violations)
    {
        if (violations.Count == 0)
        {
            return "This section is clean.";
        }

        var errorCount = violations.Count(violation => IsSeverity(violation.Severity, "error"));
        var warningCount = violations.Count(violation => IsSeverity(violation.Severity, "warning"));

        if (errorCount > 0)
        {
            return $"{errorCount} blocking issue{(errorCount == 1 ? string.Empty : "s")} needs revision.";
        }

        if (warningCount > 0)
        {
            return $"{warningCount} warning{(warningCount == 1 ? string.Empty : "s")} should be checked.";
        }

        return $"{violations.Count} note{(violations.Count == 1 ? string.Empty : "s")} found.";
    }

    private static string GetSectionAction(IReadOnlyCollection<Violation> violations)
    {
        if (violations.Count == 0)
        {
            return "No action is required for this section. Move to the next section that has issues.";
        }

        var firstError = violations.FirstOrDefault(violation => IsSeverity(violation.Severity, "error"));
        var primaryViolation = firstError ?? violations.First();
        return $"Start with {primaryViolation.RuleId}: {primaryViolation.Fix}";
    }

    private void OpenRevisionPromptModal()
    {
        RevisionPromptText = BuildRevisionPrompt();
        ShowRevisionModal = true;
        CopyButtonLabel = "Copy to Clipboard";
    }

    private void CloseRevisionPromptModal()
    {
        ShowRevisionModal = false;
    }

    private async Task CopyRevisionPromptAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", RevisionPromptText);
            CopyButtonLabel = "Copied";
            StateHasChanged();
            await Task.Delay(2000);
            CopyButtonLabel = "Copy to Clipboard";
            StateHasChanged();
        }
        catch
        {
            CopyButtonLabel = "Copy failed";
        }
    }

    private static List<SectionModel> ParseSections(string markdown)
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
                    AddSection(sections, currentHeading, currentStart, buffer);
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
            AddSection(sections, currentHeading, currentStart, buffer);
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
                Content = markdown.Trim()
            });
        }

        return sections;
    }

    private static void AddSection(List<SectionModel> sections, string heading, int lineStart, List<string> lines)
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
            Content = content
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

    // ============================================================
    // SECTION 3: DETECTION ENGINE (run rules against sections)
    // ============================================================
    private static Dictionary<string, List<Violation>> RunAllRules(
        IReadOnlyList<SectionModel> sections,
        IReadOnlyList<AntipatternRule> rules,
        IReadOnlySet<RuleProfile> activeProfiles)
    {
        var results = new Dictionary<string, List<Violation>>(StringComparer.OrdinalIgnoreCase);
        var effectiveRules = rules
            .Where(rule => rule.Profile == RuleProfile.Any || activeProfiles.Contains(rule.Profile))
            .ToList();

        foreach (var section in sections)
        {
            var violations = new List<Violation>();

            foreach (var rule in effectiveRules)
            {
                var matches = rule.Detect(section.Content);
                foreach (var match in matches)
                {
                    var excerpt = !string.IsNullOrWhiteSpace(match.Context)
                        ? match.Context
                        : match.Matched;

                    violations.Add(new Violation
                    {
                        RuleId = rule.Id,
                        Severity = rule.Severity,
                        Title = rule.Title,
                        Matched = Truncate(excerpt, 120),
                        Fix = rule.Fix,
                        Reason = rule.Reason
                    });
                }
            }

            results[section.Id] = violations
                .OrderBy(v => SeverityRank(v.Severity))
                .ThenBy(v => v.RuleId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return results;
    }

    private static IReadOnlySet<RuleProfile> DetectActiveRuleProfiles(string content, string fileName)
    {
        var normalized = content ?? string.Empty;
        var normalizedFileName = fileName ?? string.Empty;
        var scores = new Dictionary<RuleProfile, int>
        {
            [RuleProfile.Code] = 0,
            [RuleProfile.UserStory] = 0,
            [RuleProfile.ModuleSpec] = 0
        };

        if (Regex.IsMatch(normalized, @"<DxGrid\b|<XpedeonCrudGrid\b|DxComboBoxSettings|IEntityTypeConfiguration|FromSqlInterpolated|FromSqlRaw|HasTrigger\s*\(", RegexOptions.IgnoreCase))
        {
            scores[RuleProfile.Code] += 3;
        }
        if (Regex.IsMatch(normalizedFileName, @"\.(cs|razor|proto|sql)$", RegexOptions.IgnoreCase))
        {
            scores[RuleProfile.Code] += 2;
        }

        if (Regex.IsMatch(normalized, @"^###\s+(?:US|UUS|U)-\d+.*$|^##\s*User Stor(y|ies)", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            scores[RuleProfile.UserStory] += 3;
        }
        if (Regex.IsMatch(normalized, @"\bAcceptance Criteria\b|\bEdge Cases\b|\bComplexity\b", RegexOptions.IgnoreCase))
        {
            scores[RuleProfile.UserStory] += 1;
        }

        if (Regex.IsMatch(normalized, @"^##\s*Scope|^##\s*Success Criteria|^##\s*Current Tech Stack|^##\s*Stored Procedure Inventory|^##\s*Business Rule Consolidation", RegexOptions.IgnoreCase | RegexOptions.Multiline))
        {
            scores[RuleProfile.ModuleSpec] += 3;
        }
        if (Regex.IsMatch(normalizedFileName, @"plan|migration|module", RegexOptions.IgnoreCase))
        {
            scores[RuleProfile.ModuleSpec] += 1;
        }

        var maxScore = scores.Values.Max();
        if (maxScore <= 0)
        {
            return new HashSet<RuleProfile> { RuleProfile.ModuleSpec };
        }

        var active = scores
            .Where(kvp => kvp.Value > 0 && maxScore - kvp.Value <= 1)
            .Select(kvp => kvp.Key)
            .ToHashSet();

        if (active.Count == 0)
        {
            active.Add(scores.OrderByDescending(kvp => kvp.Value).First().Key);
        }

        return active;
    }

    private static int SeverityRank(string severity)
    {
        return severity.ToLowerInvariant() switch
        {
            "error" => 0,
            "warning" => 1,
            "info" => 2,
            _ => 3
        };
    }

    private static IReadOnlyList<AntipatternRule> CreateRulebook()
    {
        var rules = new List<AntipatternRule>
        {
            new()
            {
                Id = "AP-UI-001",
                Category = "UI / Razor",
                Severity = "error",
                Title = "FilterFieldNames on DxGrid columns",
                Detect = DetectApUi001,
                Fix = "Remove FilterFieldNames. Use ValueFieldName + TextFieldName on the combo editor instead.",
                Reason = "DevExpress 25.2.3 throws KeyNotFoundException."
            },
            new()
            {
                Id = "AP-UI-002",
                Category = "UI / Razor",
                Severity = "error",
                Title = "CellDisplayTemplate on required FK columns",
                Detect = DetectApUi002,
                Fix = "Remove CellDisplayTemplate from required FK columns. Use EditSettings with DxComboBoxSettings instead.",
                Reason = "Suppresses validation icon on required fields."
            },
            new()
            {
                Id = "AP-UI-003",
                Category = "UI / Razor",
                Severity = "error",
                Title = "Columns without paired EditFormat on combo cells",
                Detect = DetectApUi003,
                Fix = "Add `<EditFormat>{0} - {1}</EditFormat>` after the `<Columns>` block inside every DxComboBoxSettings.",
                Reason = "DevExpress 25.2.3 KeyNotFoundException (dead-end #2)."
            },
            new()
            {
                Id = "AP-UI-004",
                Category = "UI / Razor",
                Severity = "error",
                Title = "SortIndex on grid with FocusedRowEnabled",
                Detect = DetectApUi004,
                Fix = "Remove SortIndex. Control sort order via in-place list Sort after data load.",
                Reason = "Causes dead-end focus loop (dead-end #7)."
            },
            new()
            {
                Id = "AP-UI-005",
                Category = "UI / Razor",
                Severity = "error",
                Title = "CustomizeElement on XpedeonCrudGrid",
                Detect = DetectApUi005,
                Fix = "Remove CustomizeElement. Only valid on raw DxGrid, not XpedeonCrudGrid wrapper.",
                Reason = "Silent crash - XpedeonCrudGrid does not expose this event."
            },
            new()
            {
                Id = "AP-UI-006",
                Category = "UI / Razor",
                Severity = "error",
                Title = "Raw DxGrid used for CRUD surfaces",
                Detect = DetectApUi006,
                Fix = "Replace DxGrid with XpedeonCrudGrid for CRUD surfaces.",
                Reason = "XpedeonCrudGrid wraps DxGrid with standardized CRUD behavior."
            },
            new()
            {
                Id = "AP-UI-007",
                Category = "UI / Razor",
                Severity = "error",
                Title = "Reassigning FormModel collection lists",
                Detect = DetectApUi007,
                Fix = "Use in-place mutation: list.Clear() then list.AddRange(newData). Never reassign the list reference.",
                Reason = "Reassignment breaks Blazor change tracking and DxGrid data binding."
            },
            new()
            {
                Id = "AP-UI-008",
                Category = "UI / Razor",
                Severity = "warning",
                Title = "Missing TextFieldName=\"DisplayText\" on FK combo columns",
                Detect = DetectApUi008,
                Fix = "Add TextFieldName=\"DisplayText\" to every DxComboBoxSettings bound to an FK lookup.",
                Reason = "Without it, the combo displays the raw value instead of the formatted display text."
            },
            new()
            {
                Id = "AP-UI-009",
                Category = "UI / Razor",
                Severity = "warning",
                Title = "Missing EnableValidation=\"false\" on DxComboBox inside CellEditTemplate",
                Detect = DetectApUi009,
                Fix = "Add EnableValidation=\"false\" to every DxComboBox inside CellEditTemplate.",
                Reason = "Prevents duplicate validation messages."
            },
            new()
            {
                Id = "AP-UI-010",
                Category = "UI / Razor",
                Severity = "error",
                Title = "CaptionPosition.Horizontal",
                Detect = DetectApUi010,
                Fix = "Remove. Use default caption positioning.",
                Reason = "Banned - breaks Xpedeon layout system."
            },
            new()
            {
                Id = "AP-UI-011",
                Category = "UI / Razor",
                Severity = "error",
                Title = "ColSpanMd",
                Detect = DetectApUi011,
                Fix = "Remove. Use CSS grid or Xpedeon layout classes.",
                Reason = "Banned - not supported in the component library version."
            },
            new()
            {
                Id = "AP-UI-012",
                Category = "UI / Razor",
                Severity = "error",
                Title = "NewItemRowPosition (wrong property name)",
                Detect = DetectApUi012,
                Fix = "Replace with EditNewRowPosition.",
                Reason = "Wrong property name - it's EditNewRowPosition on XpedeonCrudGrid."
            },
            new()
            {
                Id = "AP-UI-013",
                Category = "UI / Razor",
                Severity = "error",
                Title = "Visible attribute on DxTabPage",
                Detect = DetectApUi013,
                Fix = "Use conditional render (@if block) to include/exclude the DxTabPage element entirely.",
                Reason = "DxTabs derives its tab strip from rendered children. Visible attribute doesn't work - omit the child instead."
            },
            new()
            {
                Id = "AP-BE-001",
                Category = "Backend / Data",
                Severity = "error",
                Title = "FromSqlInterpolated with multi-result-set SPs",
                Detect = DetectApBe001,
                Fix = "Use raw SqlConnection / SqlDataReader with NextResult() pattern.",
                Reason = "FromSqlInterpolated silently drops result sets 2+."
            },
            new()
            {
                Id = "AP-BE-002",
                Category = "Backend / Data",
                Severity = "error",
                Title = "EF migration for ROW_VERSION columns (RD-1)",
                Detect = DetectApBe002,
                Fix = "Do NOT generate an EF migration for ROW_VERSION. Columns already exist in DB (reviewer added them). Map via [Timestamp] / .IsRowVersion() in EF config.",
                Reason = "RD-1 resolved - migration would fail or duplicate."
            },
            new()
            {
                Id = "AP-BE-003",
                Category = "Backend / Data",
                Severity = "error",
                Title = "Positional int cast for string-backed enums",
                Detect = DetectApBe003,
                Fix = "Use explicit string mapping. SubledgerApplicableTo stores \"AP\"/\"AR\" as strings, not integers.",
                Reason = "Positional cast produces 0/1 instead of \"AP\"/\"AR\", corrupting data."
            },
            new()
            {
                Id = "AP-BE-004",
                Category = "Backend / Data",
                Severity = "warning",
                Title = "Missing HasTrigger() in EF configuration",
                Detect = DetectApBe004,
                Fix = "Add .HasTrigger(\"TRG_FC_INS_<TABLE>\"), .HasTrigger(\"TRG_FC_UPD_<TABLE>\"), .HasTrigger(\"TRG_FC_DEL_<TABLE>\").",
                Reason = "EF Core 9 without HasTrigger() throws 'trigger affects rowcount' exception."
            },
            new()
            {
                Id = "AP-BE-005",
                Category = "Backend / Data",
                Severity = "warning",
                Title = "Missing sentinel-zero handling in nullable-required-FK mappers",
                Detect = DetectApBe005,
                Fix = "DTO->Entity: use `?? 0`. Entity->DTO: convert 0 back to null. Proto->DTO: check HasXxx.",
                Reason = "Prevents zero-FK insertion violating referential integrity."
            },
            new()
            {
                Id = "AP-BE-006",
                Category = "Backend / Data",
                Severity = "warning",
                Title = "Missing IsRowVersion() wiring",
                Detect = DetectApBe006,
                Fix = "Add .Property(e => e.RowVersion).IsRowVersion().",
                Reason = "Required for optimistic concurrency."
            },
            new()
            {
                Id = "AP-BE-007",
                Category = "Backend / Data",
                Severity = "error",
                Title = "Module-specific filters on shared ListComponents RPCs",
                Detect = DetectApBe007,
                Fix = "Create a module-local RPC. Do not modify shared ListComponents definitions.",
                Reason = "Shared RPCs serve multiple consumers - module-specific params break others."
            },
            new()
            {
                Id = "AP-XC-001",
                Category = "Cross-Cutting",
                Severity = "warning",
                Title = "Missing [Display(Name)] on enum values",
                Detect = DetectApXc001,
                Fix = "Add [Display(Name = \"...\")] attribute to every enum value.",
                Reason = "Required for GetEnumDisplayName() in combo editors."
            },
            new()
            {
                Id = "AP-XC-002",
                Category = "Cross-Cutting",
                Severity = "warning",
                Title = "Missing DisplayText partial-class extension",
                Detect = DetectApXc002,
                Fix = "Create partial-class extension: `string DisplayText => string.IsNullOrEmpty(<Name>) ? (<Code> ?? string.Empty) : $\"{<Code>} - {<Name>}\";`",
                Reason = "Combo editors bind to DisplayText - without extension, runtime error."
            },
            new()
            {
                Id = "AP-XC-003",
                Category = "Cross-Cutting",
                Severity = "info",
                Title = "Section references archetype without following conventions",
                Detect = DetectApXc003,
                Fix = "Cross-reference the archetype's documented patterns and ensure conventions are followed.",
                Reason = "Archetypes define proven patterns; deviating introduces bugs."
            },
            new()
            {
                Id = "MIGR-US-001",
                Category = "Migration / User Stories",
                Severity = "error",
                Title = "User story missing Description field",
                Detect = DetectMigrUs001,
                Fix = "Add a clear description of what the user story does. Example: '### Description\\nUsers can create a new invoice with line items and save it to the database.'",
                Reason = "Claude orchestrator needs to understand what the feature does to generate accurate migration plan."
            },
            new()
            {
                Id = "MIGR-US-002",
                Category = "Migration / User Stories",
                Severity = "error",
                Title = "User story missing Phase assignment",
                Detect = DetectMigrUs002,
                Fix = "Add phase assignment. Example: '### Phase\\nPhase 1 (Immediate)' or 'Phase 2 (Follow-up)'.",
                Reason = "Migration must be phased; orchestrator needs to know execution order and dependency management."
            },
            new()
            {
                Id = "MIGR-US-003",
                Category = "Migration / User Stories",
                Severity = "error",
                Title = "User story missing Complexity assessment",
                Detect = DetectMigrUs003,
                Fix = "Add complexity level. Example: '### Complexity\\nMedium - Requires 3-5 days, moderate business logic, standard data mapping.'",
                Reason = "Team capacity planning and risk assessment depend on accurate complexity estimates."
            },
            new()
            {
                Id = "MIGR-US-004",
                Category = "Migration / User Stories",
                Severity = "error",
                Title = "User story missing Current Tech documentation",
                Detect = DetectMigrUs004,
                Fix = "Document current implementation. Example: '### Current Tech\\nWinForms with DevExpress GridControl, SQL Server stored procedures for validation.'",
                Reason = "Orchestrator must understand existing implementation to plan accurate replacement."
            },
            new()
            {
                Id = "MIGR-US-005",
                Category = "Migration / User Stories",
                Severity = "error",
                Title = "User story missing Target Tech documentation",
                Detect = DetectMigrUs005,
                Fix = "Document target implementation. Example: '### Target Tech\\nBlazer DataGrid, gRPC service with validation, EF Core entity mapping.'",
                Reason = "Orchestrator needs to know architecture decisions to generate development tasks."
            },
            new()
            {
                Id = "MIGR-US-006",
                Category = "Migration / User Stories",
                Severity = "warning",
                Title = "User story missing Acceptance Criteria",
                Detect = DetectMigrUs006,
                Fix = "Add acceptance criteria. Example: '### Acceptance Criteria\\n- Can enter invoice data and save\\n- Validation matches legacy behavior\\n- Handles edge cases (negative amounts, duplicate line items)'.",
                Reason = "Teams need explicit criteria to determine when migration is complete."
            },
            new()
            {
                Id = "MIGR-US-007",
                Category = "Migration / User Stories",
                Severity = "warning",
                Title = "User story missing Edge Cases documentation",
                Detect = DetectMigrUs007,
                Fix = "Document edge cases. Example: '### Edge Cases\\n- Null values in optional fields\\n- Concurrent edits (optimistic locking)\\n- Maximum invoice size limits.'",
                Reason = "Edge cases often cause migration delays; explicit documentation prevents surprises."
            },
            new()
            {
                Id = "MIGR-US-008",
                Category = "Migration / User Stories",
                Severity = "info",
                Title = "User story missing Risk Assessment",
                Detect = DetectMigrUs008,
                Fix = "Document risks. Example: '### Risks\\n- DevExpress Blazor GridControl API differs from WinForms\\n- Data sync complexity during parallel run'.",
                Reason = "Risk assessment helps orchestrator suggest mitigation strategies."
            },
            // Module Migration Validation Rules (TIER 2)
            new()
            {
                Id = "MIGR-MOD-001",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Scope definition",
                Detect = DetectMigrMod001,
                Fix = "Add '## Scope' or '### Scope' section listing what IS included and what is EXCLUDED from this module migration.",
                Reason = "Orchestrator needs clear module boundaries to generate accurate implementation plan."
            },
            new()
            {
                Id = "MIGR-MOD-002",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Success Criteria",
                Detect = DetectMigrMod002,
                Fix = "Add '## Success Criteria' section with testable/measurable criteria (functional, performance, cutover).",
                Reason = "Objective completion criteria enable go/no-go cutover decisions."
            },
            new()
            {
                Id = "MIGR-MOD-003",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Current Tech Stack",
                Detect = DetectMigrMod003,
                Fix = "Add '## Current Tech Stack' section listing platform (WinForms/WPF), frameworks (DevExpress versions), and data access pattern.",
                Reason = "Orchestrator must understand legacy architecture to plan equivalent modern implementation."
            },
            new()
            {
                Id = "MIGR-MOD-004",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Stored Procedure Inventory",
                Detect = DetectMigrMod004,
                Fix = "Add '## Stored Procedure Inventory' section listing all SPs with purpose, parameters, result sets, and business logic.",
                Reason = "SPs contain validation rules that must transfer to modern layers (gRPC, EF, UI)."
            },
            new()
            {
                Id = "MIGR-MOD-005",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Business Rule Consolidation",
                Detect = DetectMigrMod005,
                Fix = "Add '## Business Rule Consolidation' section listing all 50+ validation rules with current enforcement layer and modern owner (UI/API/EF/DB).",
                Reason = "Rules scattered across layers are fragile; consolidation improves reliability and prevents silent failures."
            },
            new()
            {
                Id = "MIGR-MOD-006",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Regression Test Scenarios",
                Detect = DetectMigrMod006,
                Fix = "Add '## Regression Test Catalog' section with 60+ test scenarios including scenario, inputs, expected outputs, and test level.",
                Reason = "Tests define the behavioral contract; orchestrator uses them to understand feature complexity."
            },
            new()
            {
                Id = "MIGR-MOD-007",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Test Classification Levels",
                Detect = DetectMigrMod007,
                Fix = "Add test coverage matrix classifying tests by level (UI, Domain, API, DB, E2E) with count and percentage of total.",
                Reason = "Test level distribution indicates code quality; ensures comprehensive coverage (not just UI-level tests)."
            },
            new()
            {
                Id = "MIGR-MOD-008",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Form Lifecycle States",
                Detect = DetectMigrMod008,
                Fix = "Add '## Form Lifecycle' or '## State Transition Matrix' section defining states (Load, Edit, Validate, Save, Close) and transitions.",
                Reason = "Form state machine drives UI behavior (enable/disable); orchestrator uses this to generate PageModel event handlers."
            },
            new()
            {
                Id = "MIGR-MOD-009",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Field Dependency Matrix",
                Detect = DetectMigrMod009,
                Fix = "Add field enabling rules matrix showing which fields enable/disable based on other field values (conditional logic).",
                Reason = "Complex enabling rules are easy to miss; table format ensures all conditional logic is documented."
            },
            new()
            {
                Id = "MIGR-MOD-010",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Legacy-to-Modern Field Mapping",
                Detect = DetectMigrMod010,
                Fix = "Add '## Field-to-Domain Mapping' table mapping legacy columns → modern entity properties (1:1), including data type conversions.",
                Reason = "Prevents data loss; EF entity configuration depends on correct mapping."
            },
            new()
            {
                Id = "MIGR-MOD-011",
                Category = "Migration / Module Specification",
                Severity = "error",
                Title = "Module specification missing Validation Ownership Matrix",
                Detect = DetectMigrMod011,
                Fix = "Add validation ownership table showing which layer owns each rule (UI, gRPC, EF, DB). Ensure no rule left without modern owner.",
                Reason = "Clear ownership prevents validation inconsistencies and silent failures."
            },
            new()
            {
                Id = "MIGR-MOD-012",
                Category = "Migration / Module Specification",
                Severity = "warning",
                Title = "Module specification missing Test Traceability Markers",
                Detect = DetectMigrMod012,
                Fix = "Add test case IDs (TC-001 format) and code↔test cross-references so developers know which tests validate each component.",
                Reason = "Without traceability, developers don't know which tests to run after code changes."
            },
            new()
            {
                Id = "MIGR-MOD-013",
                Category = "Migration / Module Specification",
                Severity = "warning",
                Title = "Module specification missing Deployment Strategy",
                Detect = DetectMigrMod013,
                Fix = "Add '## Deployment Strategy' section describing parallel run approach, cutover steps (old+new→new only), and rollback plan.",
                Reason = "Risk management depends on clear cutover and rollback procedures."
            },
            new()
            {
                Id = "MIGR-MOD-014",
                Category = "Migration / Module Specification",
                Severity = "warning",
                Title = "Module specification missing Database Schema Compatibility",
                Detect = DetectMigrMod014,
                Fix = "Add '## Database Schema Compatibility' section confirming EF entities map 1:1 to existing tables (no migrations needed).",
                Reason = "Prevents expensive data migrations; allows gradual cutover with old+new running simultaneously."
            },
            new()
            {
                Id = "MIGR-MOD-015",
                Category = "Migration / Module Specification",
                Severity = "info",
                Title = "Module specification missing Performance Targets",
                Detect = DetectMigrMod015,
                Fix = "Add '## Performance Targets' section specifying measurable SLAs (grid load time, gRPC latency, save operation, etc.).",
                Reason = "Performance targets provide go/no-go cutover decision criteria."
            }
        };

        return ClassifyRules(rules);
    }

    private static IReadOnlyList<AntipatternRule> ClassifyRules(IReadOnlyList<AntipatternRule> rules)
    {
        return rules
            .Select(rule =>
            {
                var profile = RuleProfile.Any;
                if (rule.Id.StartsWith("AP-", StringComparison.OrdinalIgnoreCase))
                {
                    profile = RuleProfile.Code;
                }
                else if (rule.Id.StartsWith("MIGR-US-", StringComparison.OrdinalIgnoreCase))
                {
                    profile = RuleProfile.UserStory;
                }
                else if (rule.Id.StartsWith("MIGR-MOD-", StringComparison.OrdinalIgnoreCase))
                {
                    profile = RuleProfile.ModuleSpec;
                }

                return new AntipatternRule
                {
                    Id = rule.Id,
                    Category = rule.Category,
                    Severity = rule.Severity,
                    Title = rule.Title,
                    Profile = profile,
                    Detect = rule.Detect,
                    Fix = rule.Fix,
                    Reason = rule.Reason
                };
            })
            .ToList();
    }

    private static List<RuleMatch> DetectApUi001(string content)
    {
        return RegexMatches(content, "FilterFieldNames");
    }

    private static List<RuleMatch> DetectApUi002(string content)
    {
        var results = new List<RuleMatch>();
        var templateRegex = new Regex("<CellDisplayTemplate>", RegexOptions.IgnoreCase);
        var columnRegex = new Regex("<DxGridDataColumn\\b[^>]*>", RegexOptions.IgnoreCase);
        var fieldRegex = new Regex("FieldName\\s*=\\s*(?:\"([^\"]+)\"|'([^']+)')", RegexOptions.IgnoreCase);
        var readOnlyRegex = new Regex("ReadOnly\\s*=\\s*(?:\"true\"|'true')", RegexOptions.IgnoreCase);

        foreach (Match templateMatch in templateRegex.Matches(content))
        {
            var before = content[..templateMatch.Index];
            var columns = columnRegex.Matches(before);
            if (columns.Count == 0)
            {
                continue;
            }

            var nearestColumn = columns[^1];
            var columnTag = nearestColumn.Value;
            var fieldMatch = fieldRegex.Match(columnTag);
            if (!fieldMatch.Success)
            {
                continue;
            }

            var fieldValue = fieldMatch.Groups[1].Success
                ? fieldMatch.Groups[1].Value
                : fieldMatch.Groups[2].Value;

            fieldValue = fieldValue.Replace("@nameof(", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(')', '"', '\'', ' ');

            var isFkCandidate =
                fieldValue.Contains("Code", StringComparison.OrdinalIgnoreCase) ||
                fieldValue.Contains("Id", StringComparison.OrdinalIgnoreCase) ||
                fieldValue.Contains("Number", StringComparison.OrdinalIgnoreCase);

            var isReadOnly = readOnlyRegex.IsMatch(columnTag);
            var isClosed = fieldValue.Equals("IsClosed", StringComparison.OrdinalIgnoreCase);

            if (isFkCandidate && !isReadOnly && !isClosed)
            {
                results.Add(BuildMatch(content, templateMatch.Index, templateMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApUi003(string content)
    {
        var results = new List<RuleMatch>();
        var blocks = GetComboSettingsBlocks(content);
        foreach (var block in blocks)
        {
            var hasColumns = Regex.IsMatch(block.Text, "<Columns>", RegexOptions.IgnoreCase);
            var hasEditFormat = Regex.IsMatch(block.Text, "<EditFormat>", RegexOptions.IgnoreCase);
            if (hasColumns && !hasEditFormat)
            {
                results.Add(BuildMatch(content, block.Index, block.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApUi004(string content)
    {
        var results = new List<RuleMatch>();
        var sortMatches = Regex.Matches(content, "SortIndex", RegexOptions.IgnoreCase);
        var focusedMatches = Regex.Matches(content, "FocusedRowEnabled\\s*=\\s*\"true\"", RegexOptions.IgnoreCase);
        if (sortMatches.Count == 0 || focusedMatches.Count == 0)
        {
            return results;
        }

        foreach (Match sort in sortMatches)
        {
            var nearFocused = focusedMatches.Cast<Match>().Any(f => Math.Abs(f.Index - sort.Index) <= 2000);
            if (nearFocused)
            {
                results.Add(BuildMatch(content, sort.Index, sort.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApUi005(string content)
    {
        var results = new List<RuleMatch>();
        var customizeMatches = Regex.Matches(content, "CustomizeElement", RegexOptions.IgnoreCase).Cast<Match>().ToList();
        var wrapperMatches = Regex.Matches(content, "XpedeonCrudGrid", RegexOptions.IgnoreCase).Cast<Match>().ToList();

        foreach (var customize in customizeMatches)
        {
            var nearWrapper = wrapperMatches.Any(wrapper => Math.Abs(wrapper.Index - customize.Index) <= 500);
            if (nearWrapper)
            {
                results.Add(BuildMatch(content, customize.Index, customize.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApUi006(string content)
    {
        var results = new List<RuleMatch>();
        var gridMatches = Regex.Matches(content, "<DxGrid\\b[^>]*>", RegexOptions.IgnoreCase);
        var crudSignals = new[]
        {
            "EditModelSaving",
            "EditNewRowPosition",
            "CustomizeEditModel",
            "UnsavedChanges",
            "OnDelete"
        };

        foreach (Match gridMatch in gridMatches)
        {
            var windowLength = Math.Min(3000, content.Length - gridMatch.Index);
            var block = content.Substring(gridMatch.Index, windowLength);
            var hasCrudSignal = crudSignals.Any(signal => block.Contains(signal, StringComparison.OrdinalIgnoreCase));
            if (!hasCrudSignal)
            {
                continue;
            }

            var hasShowAllRows = block.Contains("ShowAllRows=\"true\"", StringComparison.OrdinalIgnoreCase);
            var hasEditModelSaving = block.Contains("EditModelSaving", StringComparison.OrdinalIgnoreCase);
            if (hasShowAllRows && !hasEditModelSaving)
            {
                continue;
            }

            results.Add(BuildMatch(content, gridMatch.Index, gridMatch.Length));
        }

        return results;
    }

    private static List<RuleMatch> DetectApUi007(string content)
    {
        return RegexMatches(content, @"FormModel\.(Headers|Accounts|EntityRoleLinks)\s*=\s*");
    }

    private static List<RuleMatch> DetectApUi008(string content)
    {
        var results = new List<RuleMatch>();
        var blocks = GetComboSettingsBlocks(content);
        foreach (var block in blocks)
        {
            var hasValueField = Regex.IsMatch(block.Text, "ValueFieldName\\s*=", RegexOptions.IgnoreCase);
            var hasDisplayText = Regex.IsMatch(block.Text, "TextFieldName\\s*=\\s*(?:\"DisplayText\"|'DisplayText')", RegexOptions.IgnoreCase);
            if (hasValueField && !hasDisplayText)
            {
                results.Add(BuildMatch(content, block.Index, block.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApUi009(string content)
    {
        var results = new List<RuleMatch>();
        var blockRegex = new Regex("<CellEditTemplate\\b[^>]*>([\\s\\S]*?)</CellEditTemplate>", RegexOptions.IgnoreCase);
        var comboRegex = new Regex("<DxComboBox\\b(?!Settings)[^>]*>", RegexOptions.IgnoreCase);

        foreach (Match blockMatch in blockRegex.Matches(content))
        {
            var blockText = blockMatch.Value;
            foreach (Match comboMatch in comboRegex.Matches(blockText))
            {
                var hasFlag = Regex.IsMatch(comboMatch.Value, "EnableValidation\\s*=\\s*(?:\"false\"|'false')", RegexOptions.IgnoreCase);
                if (!hasFlag)
                {
                    var absoluteIndex = blockMatch.Index + comboMatch.Index;
                    results.Add(BuildMatch(content, absoluteIndex, comboMatch.Length));
                }
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApUi010(string content)
    {
        return RegexMatches(content, "CaptionPosition\\.Horizontal");
    }

    private static List<RuleMatch> DetectApUi011(string content)
    {
        return RegexMatches(content, "ColSpanMd");
    }

    private static List<RuleMatch> DetectApUi012(string content)
    {
        return RegexMatches(content, "(?<!Edit)NewItemRowPosition\\b");
    }

    private static List<RuleMatch> DetectApUi013(string content)
    {
        return RegexMatches(content, "<DxTabPage[^>]*\\bVisible\\s*=");
    }

    private static List<RuleMatch> DetectApBe001(string content)
    {
        var fromSqlMatches = Regex.Matches(content, "FromSqlInterpolated|FromSqlRaw", RegexOptions.IgnoreCase);
        if (fromSqlMatches.Count == 0)
        {
            return new List<RuleMatch>();
        }

        var hasMultiResultSignal = Regex.IsMatch(
            content,
            "SPN_FC_GET_SUBLEDGER_TYPE|multi-result|NextResult|3 result sets|multiple result",
            RegexOptions.IgnoreCase);

        if (!hasMultiResultSignal)
        {
            return new List<RuleMatch>();
        }

        return fromSqlMatches.Cast<Match>().Select(m => BuildMatch(content, m.Index, m.Length)).ToList();
    }

    private static List<RuleMatch> DetectApBe002(string content)
    {
        var normalized = content.ToLowerInvariant();
        var hasMigration = normalized.Contains("migration");
        var hasRowVersion = normalized.Contains("row_version");
        var hasExemption =
            normalized.Contains("already present") ||
            normalized.Contains("already added") ||
            normalized.Contains("removed") ||
            normalized.Contains("no migration required");

        if (hasMigration && hasRowVersion && !hasExemption)
        {
            var index = normalized.IndexOf("row_version", StringComparison.Ordinal);
            if (index < 0)
            {
                index = normalized.IndexOf("migration", StringComparison.Ordinal);
            }
            return new List<RuleMatch> { BuildMatch(content, Math.Max(0, index), 20) };
        }

        return new List<RuleMatch>();
    }

    private static List<RuleMatch> DetectApBe003(string content)
    {
        return RegexMatches(content, @"\(int\)\s*(SubledgerApplicableTo|ApplicableTo)|SubledgerApplicableTo.*\(int\)");
    }

    private static List<RuleMatch> DetectApBe004(string content)
    {
        var results = new List<RuleMatch>();
        var toTableRegex = new Regex("\\.ToTable\\(\"(?<table>FC_SUBLEDGER_TYPE|FC_SUB_LED_ACCOUNT|FC_SUBLEDGER_ENTITY_ROLES)\"\\)", RegexOptions.IgnoreCase);
        foreach (Match match in toTableRegex.Matches(content))
        {
            var start = Math.Max(0, match.Index - 380);
            var length = Math.Min(content.Length - start, 760);
            var snippet = content.Substring(start, length);
            var hasTrigger = Regex.IsMatch(snippet, "HasTrigger", RegexOptions.IgnoreCase);
            if (!hasTrigger)
            {
                results.Add(BuildMatch(content, match.Index, match.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApBe005(string content)
    {
        var hasTargetField = Regex.IsMatch(content, "CostAccountLineNumber|CostCode|EntityRoleNo", RegexOptions.IgnoreCase);
        var hasMapperContext = Regex.IsMatch(content, "\\bmapper\\b|\\bmap\\b", RegexOptions.IgnoreCase);
        var hasSentinelHandling = Regex.IsMatch(content, @"\?\?\s*0|\bHas[A-Za-z0-9_]*\b|\bhas_[A-Za-z0-9_]+\b", RegexOptions.IgnoreCase);

        if (hasTargetField && hasMapperContext && !hasSentinelHandling)
        {
            var idx = Regex.Match(content, "CostAccountLineNumber|CostCode|EntityRoleNo", RegexOptions.IgnoreCase).Index;
            return new List<RuleMatch> { BuildMatch(content, idx, 24) };
        }

        return new List<RuleMatch>();
    }

    private static List<RuleMatch> DetectApBe006(string content)
    {
        var hasRowVersion = Regex.IsMatch(content, "RowVersion|ROW_VERSION", RegexOptions.IgnoreCase);
        var hasConfigContext = Regex.IsMatch(content, "Configuration|IEntityTypeConfiguration", RegexOptions.IgnoreCase);
        var hasIsRowVersion = Regex.IsMatch(content, "IsRowVersion\\s*\\(\\s*\\)", RegexOptions.IgnoreCase);
        var hasPropertyRowVersion = Regex.IsMatch(content, "\\.Property\\s*\\(.*RowVersion", RegexOptions.IgnoreCase);

        if (hasRowVersion && hasConfigContext && !hasIsRowVersion && !hasPropertyRowVersion)
        {
            var idx = Regex.Match(content, "RowVersion|ROW_VERSION", RegexOptions.IgnoreCase).Index;
            return new List<RuleMatch> { BuildMatch(content, idx, 20) };
        }

        return new List<RuleMatch>();
    }

    private static List<RuleMatch> DetectApBe007(string content)
    {
        var results = new List<RuleMatch>();
        var paragraphs = Regex.Split(content, @"\r?\n\s*\r?\n");
        var rpcPattern = "Get_AllChartOfAccountsList|Get_AllEntityRoles|Get_AllInternalEntities";
        var changePattern = "add parameter|add filter|modify|extend";
        var offset = 0;

        foreach (var paragraph in paragraphs)
        {
            if (Regex.IsMatch(paragraph, rpcPattern, RegexOptions.IgnoreCase) &&
                Regex.IsMatch(paragraph, changePattern, RegexOptions.IgnoreCase))
            {
                var idx = content.IndexOf(paragraph, offset, StringComparison.Ordinal);
                if (idx < 0)
                {
                    idx = offset;
                }
                results.Add(BuildMatch(content, idx, Math.Min(30, paragraph.Length)));
            }

            offset += paragraph.Length + 1;
        }

        return results;
    }

    private static List<RuleMatch> DetectApXc001(string content)
    {
        var results = new List<RuleMatch>();
        var enumRegex = new Regex(@"enum\s+\w+\s*\{[\s\S]*?\}", RegexOptions.IgnoreCase);
        foreach (Match enumMatch in enumRegex.Matches(content))
        {
            var hasDisplay = Regex.IsMatch(enumMatch.Value, @"\[Display\s*\(\s*Name", RegexOptions.IgnoreCase);
            if (!hasDisplay)
            {
                results.Add(BuildMatch(content, enumMatch.Index, enumMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApXc002(string content)
    {
        var results = new List<RuleMatch>();
        var matches = Regex.Matches(content, "TextFieldName\\s*=\\s*(?:\"DisplayText\"|'DisplayText')", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return results;
        }

        var hasExtension = Regex.IsMatch(content, "DisplayText\\s*=>|Extensions", RegexOptions.IgnoreCase);
        if (!hasExtension)
        {
            foreach (Match match in matches)
            {
                results.Add(BuildMatch(content, match.Index, match.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectApXc003(string content)
    {
        var archetypes = new[]
        {
            "ContractPayOthElementsPage",
            "SecurityRoleConfiguration",
            "MemorandumAccountTypesPage"
        };

        var results = new List<RuleMatch>();
        foreach (var archetype in archetypes)
        {
            foreach (Match match in Regex.Matches(content, Regex.Escape(archetype), RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, match.Index, match.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs001(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+description", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs002(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+phase", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs003(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+complexity", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs004(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+current\s+tech", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs005(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+target\s+tech", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs006(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+acceptance\s+criteria", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs007(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+edge\s+cases", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrUs008(string content)
    {
        var results = new List<RuleMatch>();
        var userStoryPattern = new Regex(@"^###\s+(?:US|UUS|U)-\d+.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (Match usMatch in userStoryPattern.Matches(content))
        {
            var nextUsIndex = content.IndexOf("\n### ", usMatch.Index + 1, StringComparison.OrdinalIgnoreCase);
            if (nextUsIndex < 0) nextUsIndex = content.Length;

            var usBlock = content[usMatch.Index..nextUsIndex];
            if (!Regex.IsMatch(usBlock, @"###\s+risks?", RegexOptions.IgnoreCase))
            {
                results.Add(BuildMatch(content, usMatch.Index, usMatch.Length));
            }
        }

        return results;
    }

    // Module Migration Validation Detection Methods (TIER 2)
    private static List<RuleMatch> DetectMigrMod001(string content)
    {
        return HasSection(content, @"##\s+scope|###\s+scope") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod002(string content)
    {
        return HasSection(content, @"##\s+success\s+criteria|###\s+success\s+criteria") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod003(string content)
    {
        return HasSection(content, @"##\s+current\s+tech|###\s+current\s+tech") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod004(string content)
    {
        return HasSection(content, @"##\s+stored\s+procedure|###\s+stored\s+procedure|##\s+sp\s+inventory|###\s+sp\s+inventory") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod005(string content)
    {
        return HasSection(content, @"##\s+business\s+rule|###\s+business\s+rule") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod006(string content)
    {
        return HasSection(content, @"##\s+regression\s+test|###\s+regression\s+test|##\s+test\s+catalog|###\s+test\s+catalog") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod007(string content)
    {
        return HasSection(content, @"test\s+coverage\s+by\s+level|level\s+classification|test\s+level") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod008(string content)
    {
        return HasSection(content, @"##\s+form\s+lifecycle|###\s+form\s+lifecycle|##\s+state\s+transition|###\s+state\s+transition") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod009(string content)
    {
        return HasSection(content, @"field\s+dependency|field\s+enabling") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod010(string content)
    {
        return HasSection(content, @"##\s+field.*mapping|###\s+field.*mapping|##\s+legacy.*domain|###\s+legacy.*domain") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod011(string content)
    {
        return HasSection(content, @"validation\s+ownership|ownership\s+matrix") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod012(string content)
    {
        var results = new List<RuleMatch>();
        var hasTraceability = HasSection(content, @"test\s+id|test\s+case\s+id|tc-\d+|traceability");
        var hasTestSection = HasSection(content, @"##\s+test|###\s+test");

        if (hasTestSection && !hasTraceability)
        {
            results.Add(BuildMatch(content, 0, Math.Min(100, content.Length)));
        }

        return results;
    }

    private static List<RuleMatch> DetectMigrMod013(string content)
    {
        return HasSection(content, @"##\s+deployment|###\s+deployment|##\s+parallel\s+run|###\s+parallel\s+run") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod014(string content)
    {
        return HasSection(content, @"##\s+database|###\s+database|schema\s+compatibility|ef\s+mapping") ? new() : FindDocumentStart(content);
    }

    private static List<RuleMatch> DetectMigrMod015(string content)
    {
        var results = new List<RuleMatch>();
        var hasPerformance = HasSection(content, @"##\s+performance|###\s+performance|performance\s+target|sla");

        if (!hasPerformance)
        {
            results.Add(BuildMatch(content, 0, Math.Min(100, content.Length)));
        }

        return results;
    }

    private static bool HasSection(string content, string pattern)
    {
        return Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private static List<RuleMatch> FindDocumentStart(string content)
    {
        var results = new List<RuleMatch>();
        var match = Regex.Match(content, @"^#", RegexOptions.Multiline);
        if (match.Success)
        {
            results.Add(BuildMatch(content, match.Index, match.Length));
        }
        else if (content.Length > 0)
        {
            results.Add(BuildMatch(content, 0, Math.Min(100, content.Length)));
        }

        return results;
    }

    private static List<(int Index, int Length, string Text)> GetComboSettingsBlocks(string content)
    {
        var blocks = new List<(int Index, int Length, string Text)>();
        var startRegex = new Regex("<DxComboBoxSettings\\b[^>]*>", RegexOptions.IgnoreCase);
        var starts = startRegex.Matches(content).Cast<Match>().ToList();
        if (starts.Count == 0)
        {
            return blocks;
        }

        const string closeTag = "</DxComboBoxSettings>";
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i].Index;
            var nextStart = i + 1 < starts.Count ? starts[i + 1].Index : content.Length;
            var closeIndex = content.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
            var end = closeIndex >= 0 && closeIndex < nextStart
                ? closeIndex + closeTag.Length
                : nextStart;

            var length = Math.Max(0, end - start);
            var text = length > 0 ? content.Substring(start, length) : string.Empty;
            blocks.Add((start, length, text));
        }

        return blocks;
    }

    private static List<RuleMatch> RegexMatches(string content, string pattern)
    {
        return Regex.Matches(content, pattern, RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => BuildMatch(content, match.Index, match.Length))
            .ToList();
    }

    private static RuleMatch BuildMatch(string content, int index, int matchLength)
    {
        var safeIndex = Math.Clamp(index, 0, Math.Max(0, content.Length - 1));
        var safeLength = Math.Max(1, matchLength);
        var snippet = BuildContextSnippet(content, safeIndex, safeLength);
        var matched = content.Substring(safeIndex, Math.Min(safeLength, content.Length - safeIndex));

        return new RuleMatch
        {
            Index = safeIndex,
            Matched = matched,
            Context = snippet
        };
    }

    private static string BuildContextSnippet(string content, int index, int matchLength, int radius = 68)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var start = Math.Max(0, index - radius);
        var end = Math.Min(content.Length, index + matchLength + radius);
        var slice = content[start..end];
        return Regex.Replace(slice, @"\s+", " ").Trim();
    }

    // ============================================================
    // SECTION 4: REVISION PROMPT GENERATOR
    // ============================================================
    private string BuildRevisionPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("The following violations were found in your implementation plan.");
        sb.AppendLine("For each violation, the canonical fix is provided. Please revise the indicated sections:");
        sb.AppendLine();

        var totalViolations = 0;
        var totalErrors = 0;
        var totalWarnings = 0;
        var totalInfo = 0;
        var cleanSections = 0;
        var sectionsNeedingRevision = new List<string>();

        foreach (var section in Sections)
        {
            var violations = GetViolationsForSection(section.Id);
            if (violations.Count == 0)
            {
                cleanSections++;
                continue;
            }

            sb.AppendLine($"## Section {section.Letter} - {section.Heading}");
            sectionsNeedingRevision.Add(section.Letter);
            for (var i = 0; i < violations.Count; i++)
            {
                var violation = violations[i];
                var severityUpper = violation.Severity.ToUpperInvariant();
                var matchedSnippet = Truncate(violation.Matched, 80);

                sb.AppendLine($"{i + 1}. **[{violation.RuleId}] {severityUpper}** - {violation.Title}");
                sb.AppendLine($"   Matched: `{matchedSnippet}`");
                sb.AppendLine($"   Fix: {violation.Fix}");
                sb.AppendLine($"   Reason: {violation.Reason}");
                sb.AppendLine();

                totalViolations++;
                if (IsSeverity(violation.Severity, "error"))
                {
                    totalErrors++;
                }
                else if (IsSeverity(violation.Severity, "warning"))
                {
                    totalWarnings++;
                }
                else if (IsSeverity(violation.Severity, "info"))
                {
                    totalInfo++;
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine($"- Total violations: {totalViolations}");
        sb.AppendLine($"- Errors: {totalErrors}");
        sb.AppendLine($"- Warnings: {totalWarnings}");
        sb.AppendLine($"- Info: {totalInfo}");
        sb.AppendLine($"- Clean sections: {cleanSections} / {Sections.Count}");
        sb.AppendLine($"- Sections needing revision: {(sectionsNeedingRevision.Count == 0 ? "None" : string.Join(", ", sectionsNeedingRevision.Distinct()))}");
        return sb.ToString();
    }

    // ============================================================
    // SECTION 5: UI COMPONENTS (panels, badges, modal)
    // ============================================================
    private static string TruncateForDisplay(string text, int maxLength)
    {
        return Truncate(text, maxLength);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "...";
    }

}
