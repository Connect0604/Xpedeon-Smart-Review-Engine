using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SmartReviewSystem.Models.DevOps;

namespace SmartReviewSystem.Services.DevOps;

internal sealed class AzureDevOpsService(HttpClient httpClient) : IAzureDevOpsService
{
    private const string OrchestratorPhaseFieldName = "Custom.OrchestratorPhase";
    private const string MfeFieldName = "Custom.Module";
    private const string ExecutionModeFieldName = "Custom.ExecutionMode";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<List<DevOpsStoryItem>> GetStoriesWithAttachmentsAsync(
        string organization,
        string project,
        string patToken,
        string wiqlCondition,
        CancellationToken cancellationToken,
        bool includeRevisionMetadata = false)
    {
        var baseUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit";
        using var wiqlRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/wiql?api-version=7.1");
        ApplyPatHeader(wiqlRequest, patToken);

        var query = string.IsNullOrWhiteSpace(wiqlCondition)
            ? "[System.WorkItemType] = 'User Story' AND [System.State] <> 'Closed'"
            : wiqlCondition;

        wiqlRequest.Content = JsonContent.Create(new
        {
            query = $"SELECT [System.Id] FROM WorkItems WHERE {query} ORDER BY [System.ChangedDate] DESC"
        });

        using var wiqlResponse = await httpClient.SendAsync(wiqlRequest, cancellationToken);
        wiqlResponse.EnsureSuccessStatusCode();

        var wiqlData = await wiqlResponse.Content.ReadFromJsonAsync<WiqlResponse>(JsonOptions, cancellationToken);
        var ids = wiqlData?.WorkItems?.Select(w => w.Id).Where(id => id > 0).Take(200).ToList() ?? new List<int>();
        if (ids.Count == 0)
        {
            return new List<DevOpsStoryItem>();
        }

        var idsParam = string.Join(",", ids);
        using var workItemsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/workitems?ids={idsParam}&$expand=Relations&api-version=7.1");
        ApplyPatHeader(workItemsRequest, patToken);

        using var workItemsResponse = await httpClient.SendAsync(workItemsRequest, cancellationToken);
        workItemsResponse.EnsureSuccessStatusCode();

        var workItemsData = await workItemsResponse.Content.ReadFromJsonAsync<WorkItemsResponse>(JsonOptions, cancellationToken);
        var items = workItemsData?.Value ?? new List<WorkItemDto>();

        // Only fetch revision metadata if requested (saves 200+ API calls if not needed)
        List<StoryRevisionMetadata> allRevisions = new();
        if (includeRevisionMetadata && items.Count > 0)
        {
            // Fetch all revision metadata in parallel instead of sequentially
            var revisionTasks = items.Select(item =>
                GetRevisionMetadataAsync(baseUrl, item.Id, patToken, cancellationToken));
            allRevisions = (await Task.WhenAll(revisionTasks)).ToList();
        }
        else
        {
            // No revision metadata needed - create empty list
            allRevisions = Enumerable.Range(0, items.Count).Select(_ => new StoryRevisionMetadata()).ToList();
        }

        var stories = new List<DevOpsStoryItem>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var revisionMetadata = allRevisions[i];

            var workItemType = item.Fields.TryGetValue("System.WorkItemType", out var typeValue) ? typeValue?.ToString() ?? "Unknown" : "Unknown";
            var title = item.Fields.TryGetValue("System.Title", out var titleValue) ? titleValue?.ToString() ?? "(No title)" : "(No title)";
            var state = item.Fields.TryGetValue("System.State", out var stateValue) ? stateValue?.ToString() ?? "Unknown" : "Unknown";
            var assigned = item.Fields.TryGetValue("System.AssignedTo", out var assignedValue) ? ExtractAssignedTo(assignedValue) : "Unassigned";
            var tags = item.Fields.TryGetValue("System.Tags", out var tagsValue) ? tagsValue?.ToString() ?? string.Empty : string.Empty;
            var orchestratorPhase = item.Fields.TryGetValue(OrchestratorPhaseFieldName, out var phaseValue) ? phaseValue?.ToString() ?? string.Empty : string.Empty;
            var mfe = item.Fields.TryGetValue(MfeFieldName, out var mfeValue) ? mfeValue?.ToString() ?? string.Empty : string.Empty;
            var executionMode = item.Fields.TryGetValue(ExecutionModeFieldName, out var executionModeValue) ? executionModeValue?.ToString() ?? string.Empty : string.Empty;

            var attachments = (item.Relations ?? new List<RelationDto>())
                .Where(r => string.Equals(r.Rel, "AttachedFile", StringComparison.OrdinalIgnoreCase))
                .Select(r =>
                {
                    var name = r.Attributes?.TryGetValue("name", out var nameValue) == true ? nameValue?.ToString() ?? "attachment" : "attachment";
                    return new DevOpsAttachmentItem
                    {
                        Name = name,
                        Url = r.Url ?? string.Empty,
                        SizeBytes = TryParseLong(r.Attributes?.TryGetValue("resourceSize", out var s) == true ? s : null),
                        Extension = GetExtension(name),
                        IsSupported = IsSupportedAttachment(name),
                        AttachedBy = ExtractRelationUserName(r.Attributes, "authorizedBy"),
                        AttachedOn = ExtractRelationDate(
                            r.Attributes,
                            "authorizedDate",
                            "resourceCreatedDate",
                            "createdDate")
                    };
                })
                .ToList();

            stories.Add(new DevOpsStoryItem
            {
                Id = item.Id,
                WorkItemType = workItemType,
                Title = title,
                State = state,
                AssignedTo = assigned,
                Tags = tags,
                OrchestratorPhase = orchestratorPhase,
                OrchestratorPhaseUpdated = revisionMetadata.OrchestratorPhaseUpdated,
                StartDate = revisionMetadata.StartDate,
                Mfe = mfe,
                ExecutionMode = executionMode,
                WorkItemUrl = $"https://dev.azure.com/{Uri.EscapeDataString(organization)}/{Uri.EscapeDataString(project)}/_workitems/edit/{item.Id}",
                Attachments = attachments
            });
        }

        return stories;
    }

    public async Task<string> DownloadAttachmentTextAsync(string attachmentUrl, string patToken, CancellationToken cancellationToken)
    {
        var url = attachmentUrl.Contains("api-version=", StringComparison.OrdinalIgnoreCase)
            ? attachmentUrl
            : attachmentUrl + (attachmentUrl.Contains('?') ? "&" : "?") + "api-version=7.1";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyPatHeader(request, patToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return DecodeText(bytes);
    }

    private static bool IsSupportedAttachment(string name)
    {
        var ext = Path.GetExtension(name);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".htm", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExtension(string name)
    {
        var ext = Path.GetExtension(name);
        return string.IsNullOrWhiteSpace(ext) ? "(no extension)" : ext.ToLowerInvariant();
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void ApplyPatHeader(HttpRequestMessage request, string patToken)
    {
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{patToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static string ExtractAssignedTo(object? assignedValue)
    {
        if (assignedValue is null)
        {
            return "Unassigned";
        }

        if (assignedValue is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("displayName", out var displayName))
            {
                return displayName.GetString() ?? "Unassigned";
            }
        }

        return assignedValue.ToString() ?? "Unassigned";
    }

    private static long? TryParseLong(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var elementLong))
            {
                return elementLong;
            }

            if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var strLong))
            {
                return strLong;
            }
        }

        return long.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ExtractRelationDate(Dictionary<string, object?>? attributes, params string[] keys)
    {
        if (attributes is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (!attributes.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), out var elementDate))
                {
                    return elementDate;
                }
            }

            if (DateTimeOffset.TryParse(value.ToString(), out var parsedDate))
            {
                return parsedDate;
            }
        }

        return null;
    }

    private static string ExtractRelationUserName(Dictionary<string, object?>? attributes, string key)
    {
        if (attributes is null || !attributes.TryGetValue(key, out var value) || value is null)
        {
            return "Unknown";
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("displayName", out var displayName))
            {
                return displayName.GetString() ?? "Unknown";
            }

            if (element.TryGetProperty("uniqueName", out var uniqueName))
            {
                return uniqueName.GetString() ?? "Unknown";
            }
        }

        return value.ToString() ?? "Unknown";
    }

    private async Task<StoryRevisionMetadata> GetRevisionMetadataAsync(
        string baseUrl,
        int workItemId,
        string patToken,
        CancellationToken cancellationToken)
    {
        using var revisionsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{baseUrl}/workItems/{workItemId}/revisions?api-version=7.1");
        ApplyPatHeader(revisionsRequest, patToken);

        using var revisionsResponse = await httpClient.SendAsync(revisionsRequest, cancellationToken);
        revisionsResponse.EnsureSuccessStatusCode();

        var revisionsData = await revisionsResponse.Content.ReadFromJsonAsync<RevisionsResponse>(JsonOptions, cancellationToken);
        if (revisionsData?.Value is null || revisionsData.Value.Count == 0)
        {
            return new StoryRevisionMetadata();
        }

        string? previousState = null;
        string? previousPhase = null;
        DateTimeOffset? startDate = null;
        DateTimeOffset? phaseUpdated = null;

        foreach (var revision in revisionsData.Value)
        {
            var fields = revision.Fields;
            var currentState = GetFieldString(fields, "System.State");
            var currentPhase = GetFieldString(fields, OrchestratorPhaseFieldName);
            var changedDate = GetFieldDate(fields, "System.ChangedDate");

            if (startDate is null &&
                changedDate is not null &&
                string.Equals(currentState, "Coding In Progress", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(previousState, "Coding In Progress", StringComparison.OrdinalIgnoreCase))
            {
                startDate = changedDate;
            }

            if (!string.IsNullOrWhiteSpace(previousPhase) &&
                !string.Equals(previousPhase, currentPhase, StringComparison.OrdinalIgnoreCase) &&
                changedDate is not null)
            {
                phaseUpdated = changedDate;
            }

            previousState = currentState;
            previousPhase = currentPhase;
        }

        return new StoryRevisionMetadata
        {
            StartDate = startDate,
            OrchestratorPhaseUpdated = phaseUpdated
        };
    }

    private static string GetFieldString(Dictionary<string, object?> fields, string fieldName)
    {
        return fields.TryGetValue(fieldName, out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;
    }

    private static DateTimeOffset? GetFieldDate(Dictionary<string, object?> fields, string fieldName)
    {
        if (!fields.TryGetValue(fieldName, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), out var elementDate))
            {
                return elementDate;
            }

            return null;
        }

        return DateTimeOffset.TryParse(value.ToString(), out var parsedDate) ? parsedDate : null;
    }

    private sealed class WiqlResponse
    {
        public List<WiqlWorkItem> WorkItems { get; init; } = new();
    }

    private sealed class WiqlWorkItem
    {
        public int Id { get; init; }
    }

    private sealed class WorkItemsResponse
    {
        public List<WorkItemDto> Value { get; init; } = new();
    }

    private sealed class WorkItemDto
    {
        public int Id { get; init; }
        public Dictionary<string, object?> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RelationDto>? Relations { get; init; }
    }

    private sealed class RevisionsResponse
    {
        public List<RevisionDto> Value { get; init; } = new();
    }

    private sealed class RevisionDto
    {
        public Dictionary<string, object?> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RelationDto
    {
        public string? Rel { get; init; }
        public string? Url { get; init; }
        public Dictionary<string, object?>? Attributes { get; init; }
    }

    private sealed class StoryRevisionMetadata
    {
        public DateTimeOffset? StartDate { get; init; }
        public DateTimeOffset? OrchestratorPhaseUpdated { get; init; }
    }
}
