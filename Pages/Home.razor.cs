using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.Ai;
using SmartReviewSystem.Models.DevOps;
using SmartReviewSystem.Models.Ui;
using SmartReviewSystem.Services.DevOps;
using SmartReviewSystem.Services.Ollama;

namespace SmartReviewSystem.Pages;

public partial class Home : ComponentBase, IAsyncDisposable
{
    [Inject]
    private IJSRuntime JS { get; set; } = default!;
    [Inject]
    private IAzureDevOpsService AzureDevOpsService { get; set; } = default!;
    [Inject]
    private IOllamaService OllamaService { get; set; } = default!;
    [Inject]
    private IConfiguration Configuration { get; set; } = default!;

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
    private string UploadError = string.Empty;
    private string? SelectedSectionId;
    private string SectionSearchText = string.Empty;
    private SectionFilterMode SectionFilter = SectionFilterMode.All;
    private readonly List<ConfiguredSectionTab> ConfiguredSectionTabs = new();
    private string? ActiveCustomTabName;
    private bool IsDragging;
    private bool IsRunningAiReview;
    private int CurrentRunningStepIndex = -1;
    private IReadOnlyList<SectionPromptStep> CurrentSteps = Array.Empty<SectionPromptStep>();
    private List<AiReviewStepState> AiReviewSteps = new();
    private CancellationTokenSource? _reviewCts;
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
        SectionSearchText = string.Empty;
        SectionFilter = SectionFilterMode.All;

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
        AiReviewSteps = new();
        CurrentSteps = Array.Empty<SectionPromptStep>();
        CurrentRunningStepIndex = -1;
        IsRunningAiReview = false;
        CancelActiveReview();
    }

    private void SelectSection(string sectionId)
    {
        if (SelectedSectionId == sectionId)
        {
            return;
        }

        CancelActiveReview();
        SelectedSectionId = sectionId;
        AiReviewSteps = new();
        CurrentSteps = Array.Empty<SectionPromptStep>();
        CurrentRunningStepIndex = -1;
        IsRunningAiReview = false;
    }

    private async Task RunAiReviewAsync()
    {
        if (CurrentSection is null || IsRunningAiReview)
            return;

        CancelActiveReview();
        _reviewCts = new CancellationTokenSource();
        var ct = _reviewCts.Token;

        var steps = OllamaService.GetPromptSteps(CurrentSection.Heading);
        CurrentSteps = steps;
        AiReviewSteps = steps.Select(_ => new AiReviewStepState()).ToList();
        IsRunningAiReview = true;

        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                CurrentRunningStepIndex = i;
                StateHasChanged();

                var step = steps[i];
                var state = AiReviewSteps[i];
                var hasSchema = step.OutputSchema?.Fields.Count > 0;

                await foreach (var token in OllamaService.StreamStepAsync(
                    CurrentSection.Heading, step, CurrentSection.Content, ct))
                {
                    state.RawResult += token;
                    if (!hasSchema) StateHasChanged();
                }

                TryParseStepResult(state, step);
                StateHasChanged();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (CurrentRunningStepIndex >= 0 && CurrentRunningStepIndex < AiReviewSteps.Count)
                AiReviewSteps[CurrentRunningStepIndex].Error = $"Review failed: {ex.Message}";
        }
        finally
        {
            IsRunningAiReview = false;
            CurrentRunningStepIndex = -1;
            StateHasChanged();
        }
    }

    private static void TryParseStepResult(AiReviewStepState state, SectionPromptStep step)
    {
        if (step.OutputSchema?.Fields.Count is null or 0 || string.IsNullOrWhiteSpace(state.RawResult))
            return;

        try
        {
            var start = state.RawResult.IndexOf('{');
            var end = state.RawResult.LastIndexOf('}');
            var json = start >= 0 && end > start ? state.RawResult[start..(end + 1)] : state.RawResult;

            using var doc = JsonDocument.Parse(json);
            state.Parsed = doc.RootElement
                .EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.Clone());
        }
        catch
        {
            // leave Parsed null — UI falls back to raw result text
        }
    }

    private sealed class AiReviewStepState
    {
        public string RawResult { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public Dictionary<string, JsonElement>? Parsed { get; set; }
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

}
