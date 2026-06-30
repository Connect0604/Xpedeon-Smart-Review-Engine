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
                CompletionDate = revisionMetadata.CompletionDate,
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

        // Fetch comments to find orchestrator plan approval and completion dates
        System.Diagnostics.Debug.WriteLine($"[GetRevisionMetadataAsync] Fetching comments for work item {workItemId}");
        var (orchestratorApprovalDate, orchestratorCompletionDate, implementationCost) = await GetOrchestratorDatesAsync(baseUrl, workItemId, patToken, cancellationToken);

        string? previousState = null;
        string? previousPhase = null;
        DateTimeOffset? startDate = null;
        DateTimeOffset? completionDate = null;
        DateTimeOffset? phaseUpdated = null;

        foreach (var revision in revisionsData.Value)
        {
            var fields = revision.Fields;
            var currentState = GetFieldString(fields, "System.State");
            var currentPhase = GetFieldString(fields, OrchestratorPhaseFieldName);
            var changedDate = GetFieldDate(fields, "System.ChangedDate");

            // If orchestrator approval date exists, use it as start date
            if (startDate is null && orchestratorApprovalDate is not null)
            {
                System.Diagnostics.Debug.WriteLine($"[GetRevisionMetadataAsync] Using orchestrator approval date as start date: {orchestratorApprovalDate}");
                startDate = orchestratorApprovalDate;
            }

            // Fallback to "Coding In Progress" state change if no orchestrator approval found
            if (startDate is null &&
                changedDate is not null &&
                string.Equals(currentState, "Coding In Progress", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(previousState, "Coding In Progress", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[GetRevisionMetadataAsync] Using Coding In Progress state as start date: {changedDate}");
                startDate = changedDate;
            }

            // If orchestrator completion date exists, use it as completion date
            if (completionDate is null && orchestratorCompletionDate is not null)
            {
                System.Diagnostics.Debug.WriteLine($"[GetRevisionMetadataAsync] Using orchestrator completion date: {orchestratorCompletionDate}");
                completionDate = orchestratorCompletionDate;
            }
            // Otherwise, fallback to "Testing Requested" state change
            else if (startDate is not null &&
                completionDate is null &&
                changedDate is not null &&
                string.Equals(currentState, "Testing Requested", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(previousState, "Testing Requested", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[GetRevisionMetadataAsync] Found completion date (Testing Requested): {changedDate}");
                completionDate = changedDate;
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

        System.Diagnostics.Debug.WriteLine($"[GetRevisionMetadataAsync] Final metadata - StartDate: {startDate}, CompletionDate: {completionDate}");

        return new StoryRevisionMetadata
        {
            StartDate = startDate,
            CompletionDate = completionDate,
            OrchestratorPhaseUpdated = phaseUpdated,
            ImplementationCost = implementationCost
        };
    }

    private async Task<(DateTimeOffset? ApprovalDate, DateTimeOffset? CompletionDate, string? ImplementationCost)> GetOrchestratorDatesAsync(
        string baseUrl,
        int workItemId,
        string patToken,
        CancellationToken cancellationToken)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Starting for work item {workItemId}");

            using var commentsRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl}/workItems/{workItemId}/comments?api-version=7.1-preview.3");
            ApplyPatHeader(commentsRequest, patToken);

            System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Request URL: {commentsRequest.RequestUri}");

            using var commentsResponse = await httpClient.SendAsync(commentsRequest, cancellationToken);

            System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Response Status: {commentsResponse.StatusCode}");

            if (!commentsResponse.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Comments API failed for work item {workItemId}: {commentsResponse.StatusCode}");
                return (null, null, null);
            }

            var responseContent = await commentsResponse.Content.ReadAsStringAsync(cancellationToken);
            System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Response Content Length: {responseContent?.Length ?? 0}");

            if (responseContent?.Length > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Response Content (first 500 chars): {responseContent.Substring(0, Math.Min(500, responseContent.Length))}");
            }

            if (string.IsNullOrEmpty(responseContent))
            {
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Comments API returned empty response for work item {workItemId}");
                return (null, null, null);
            }

            CommentsResponse? commentsData = null;
            try
            {
                commentsData = JsonSerializer.Deserialize<CommentsResponse>(responseContent, JsonOptions);
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] JSON Deserialized successfully");
            }
            catch (Exception jsonEx)
            {
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] JSON Deserialization Error: {jsonEx.Message}");
                return (null, null, null);
            }

            // Handle both "value" and "comments" property names
            var commentsList = commentsData?.Value ?? commentsData?.Comments;
            System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Comments list count: {commentsList?.Count ?? 0}");

            if (commentsList is null || commentsList.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] No comments found for work item {workItemId}");
                return (null, null, null);
            }

            // Log all comments for debugging
            for (int i = 0; i < commentsList.Count; i++)
            {
                var comment = commentsList[i];
                var commentContent = comment.Content ?? comment.Text;
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] Comment {i}: CreatedDate={comment.CreatedDate}, Content={commentContent?.Substring(0, Math.Min(100, commentContent?.Length ?? 0)) ?? "null"}");
            }

            // Find the first comment from orchestrator that contains "plan approved"
            var approvalComment = commentsList.FirstOrDefault(c =>
            {
                var content = c.Content ?? c.Text;
                return !string.IsNullOrEmpty(content) &&
                    content.Contains("plan approved", StringComparison.OrdinalIgnoreCase) &&
                    content.Contains("orchestrator", StringComparison.OrdinalIgnoreCase) &&
                    c.CreatedDate.HasValue;
            });

            DateTimeOffset? approvalDate = null;
            if (approvalComment?.CreatedDate is not null)
            {
                approvalDate = approvalComment.CreatedDate;
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] ✓ FOUND orchestrator approval date for work item {workItemId}: {approvalDate}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] ✗ No orchestrator approval comment found for work item {workItemId}");
            }

            // Find the first comment from orchestrator that contains "Implementation complete"
            var completionComment = commentsList.FirstOrDefault(c =>
            {
                var content = c.Content ?? c.Text;
                return !string.IsNullOrEmpty(content) &&
                    content.Contains("implementation complete", StringComparison.OrdinalIgnoreCase) &&
                    content.Contains("orchestrator", StringComparison.OrdinalIgnoreCase) &&
                    c.CreatedDate.HasValue;
            });

            DateTimeOffset? completionDate = null;
            if (completionComment?.CreatedDate is not null)
            {
                completionDate = completionComment.CreatedDate;
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] ✓ FOUND orchestrator completion date for work item {workItemId}: {completionDate}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] ✗ No orchestrator completion comment found for work item {workItemId}");
            }

            var implementationCost = ExtractImplementationCost(commentsList);
            return (approvalDate, completionDate, implementationCost);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GetOrchestratorDatesAsync] ✗ Exception for work item {workItemId}: {ex.Message}\n{ex.StackTrace}");
            return (null, null, null);
        }
    }

    private static string? ExtractImplementationCost(List<CommentDto>? commentsList)
    {
        if (commentsList is null || commentsList.Count == 0)
        {
            return null;
        }

        foreach (var comment in commentsList)
        {
            var content = comment.Content ?? comment.Text;
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            // Look for "Total Claude cost: $XX.XX" pattern
            if (content.Contains("Total Claude cost", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the cost value using regex
                var match = System.Text.RegularExpressions.Regex.Match(
                    content,
                    @"Total\s+Claude\s+cost:\s*\$?([\d,]+\.?\d*)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (match.Success && match.Groups.Count > 1)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        return null;
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

    private sealed class CommentsResponse
    {
        // Handle both "value" (standard ADO REST response) and "comments" (alternative format)
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public List<CommentDto>? Value { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("comments")]
        public List<CommentDto>? Comments { get; init; }
    }

    private sealed class CommentDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string? Content { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string? Text { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("createdDate")]
        public DateTimeOffset? CreatedDate { get; init; }
    }

    public async Task LoadImplementationDetailsAsync(
        DevOpsStoryItem story,
        string organization,
        string project,
        string patToken,
        CancellationToken cancellationToken)
    {
        if (story.ImplementationDetailsLoaded)
        {
            return;
        }

        var baseUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit";
        var revisionMetadata = await GetRevisionMetadataAsync(baseUrl, story.Id, patToken, cancellationToken);

        // Update the story with loaded implementation details
        story.StartDate = revisionMetadata.StartDate;
        story.CompletionDate = revisionMetadata.CompletionDate;
        story.OrchestratorPhaseUpdated = revisionMetadata.OrchestratorPhaseUpdated;
        story.ImplementationCost = revisionMetadata.ImplementationCost;
        story.ImplementationDetailsLoaded = true;
    }

    public async Task LoadPhaseHistoryAsync(
        DevOpsStoryItem story,
        string organization,
        string project,
        string patToken,
        CancellationToken cancellationToken)
    {
        if (story.PhaseHistoryLoaded)
        {
            return;
        }

        var baseUrl = $"https://dev.azure.com/{organization}/{project}/_apis/wit";
        var phaseHistory = await GetPhaseHistoryAsync(baseUrl, story.Id, patToken, cancellationToken);

        story.PhaseHistorySummary = phaseHistory;
        story.PhaseHistoryLoaded = true;
    }

    private async Task<PhaseHistorySummary> GetPhaseHistoryAsync(
        string baseUrl,
        int workItemId,
        string patToken,
        CancellationToken cancellationToken)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] Fetching phase history for work item {workItemId}");

            // Fetch revision history with pagination support
            var allRevisions = new List<RevisionDto>();
            int skip = 0;
            const int pageSize = 200; // Azure DevOps default is 100, let's fetch more per page

            while (true)
            {
                using var revisionsRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}/workItems/{workItemId}/revisions?$skip={skip}&$top={pageSize}&api-version=7.1");
                ApplyPatHeader(revisionsRequest, patToken);

                using var revisionsResponse = await httpClient.SendAsync(revisionsRequest, cancellationToken);
                if (!revisionsResponse.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] Revisions API failed for work item {workItemId}: {revisionsResponse.StatusCode}");
                    if (allRevisions.Count == 0)
                    {
                        return new PhaseHistorySummary();
                    }
                    break; // We have some data, continue with what we have
                }

                var revisionsData = await revisionsResponse.Content.ReadFromJsonAsync<RevisionsResponse>(JsonOptions, cancellationToken);
                if (revisionsData?.Value is null || revisionsData.Value.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] No more revisions found for work item {workItemId} at skip={skip}");
                    break; // No more revisions to fetch
                }

                allRevisions.AddRange(revisionsData.Value);
                System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] Fetched {revisionsData.Value.Count} revisions for work item {workItemId} (skip={skip})");

                // If we got fewer items than requested, we've reached the end
                if (revisionsData.Value.Count < pageSize)
                {
                    break;
                }

                skip += pageSize;
            }

            if (allRevisions.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] No revisions found for work item {workItemId}");
                return new PhaseHistorySummary();
            }

            System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] Total revisions fetched: {allRevisions.Count} for work item {workItemId}");

            // Convert revisions to parser format
            var revisionEvents = allRevisions
                .Select(r => new OrchestratorPhaseHistoryParser.RevisionEventDto
                {
                    ChangedDate = GetFieldDate(r.Fields, "System.ChangedDate"),
                    OrchestratorPhase = GetFieldString(r.Fields, "Custom.OrchestratorPhase"),
                    State = GetFieldString(r.Fields, "System.State")
                })
                .ToList();

            // Fetch comments for error information
            List<OrchestratorPhaseHistoryParser.CommentDto>? commentDtos = null;
            try
            {
                using var commentsRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}/workItems/{workItemId}/comments?api-version=7.1-preview.3");
                ApplyPatHeader(commentsRequest, patToken);

                using var commentsResponse = await httpClient.SendAsync(commentsRequest, cancellationToken);
                if (commentsResponse.IsSuccessStatusCode)
                {
                    var commentsData = await commentsResponse.Content.ReadFromJsonAsync<CommentsResponse>(JsonOptions, cancellationToken);
                    var commentsList = commentsData?.Value ?? commentsData?.Comments;

                    if (commentsList?.Count > 0)
                    {
                        commentDtos = commentsList
                            .Select(c => new OrchestratorPhaseHistoryParser.CommentDto
                            {
                                Content = c.Content,
                                Text = c.Text,
                                CreatedDate = c.CreatedDate
                            })
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] Failed to fetch comments: {ex.Message}");
                // Continue with just revisions
            }

            // Parse phase history from revisions and comments
            var phaseHistory = OrchestratorPhaseHistoryParser.ParsePhaseHistoryFromRevisions(revisionEvents, commentDtos);
            System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] Parsed {phaseHistory.TotalPhases} phases for work item {workItemId}");

            return phaseHistory;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GetPhaseHistoryAsync] Exception for work item {workItemId}: {ex.Message}\n{ex.StackTrace}");
            return new PhaseHistorySummary();
        }
    }

    private sealed class StoryRevisionMetadata
    {
        public DateTimeOffset? StartDate { get; init; }
        public DateTimeOffset? CompletionDate { get; init; }
        public DateTimeOffset? OrchestratorPhaseUpdated { get; init; }
        public string? ImplementationCost { get; init; }
    }
}
