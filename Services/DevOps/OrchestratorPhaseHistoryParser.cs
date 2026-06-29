using System.Text.RegularExpressions;
using SmartReviewSystem.Models.DevOps;

namespace SmartReviewSystem.Services.DevOps;

/// <summary>
/// Service for parsing orchestrator phase history from Azure DevOps revisions and comments.
/// Extracts phase transitions, timings, and error events to build a complete execution timeline.
/// </summary>
internal sealed class OrchestratorPhaseHistoryParser
{
    /// <summary>
    /// Parses revision history to extract phase transitions and build phase history.
    /// Each time the Custom.OrchestratorPhase field changes to a different value, that's a phase transition event.
    /// </summary>
    public static PhaseHistorySummary ParsePhaseHistoryFromRevisions(
        List<RevisionEventDto> revisions,
        List<CommentDto>? comments = null)
    {
        if (revisions is null || revisions.Count == 0)
        {
            return new PhaseHistorySummary();
        }

        // Parse errors from comments first
        var errorsByPhase = new Dictionary<string, List<(DateTimeOffset time, string message)>>();
        if (comments?.Count > 0)
        {
            var errors = ExtractErrorsFromComments(comments);
            errorsByPhase = errors;
        }

        // Parse phase transitions from revision history
        string? previousPhase = null;
        DateTimeOffset? executionStarted = null;
        DateTimeOffset? executionCompleted = null;
        var phaseTransitions = new List<(string phase, DateTimeOffset time)>();
        var phaseFirstOccurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var revision in revisions.OrderBy(r => r.ChangedDate))
        {
            if (!revision.ChangedDate.HasValue)
                continue;

            var currentPhase = revision.OrchestratorPhase ?? string.Empty;

            // Only process non-empty phases
            if (string.IsNullOrWhiteSpace(currentPhase))
            {
                continue;
            }

            // Track when execution started (first non-empty orchestrator phase)
            if (executionStarted is null)
            {
                executionStarted = revision.ChangedDate;
            }

            // Detect phase transitions - only record when phase actually changes
            if (!string.Equals(previousPhase, currentPhase, StringComparison.OrdinalIgnoreCase))
            {
                phaseTransitions.Add((currentPhase, revision.ChangedDate.Value));

                // Track first occurrence of each phase name
                if (!phaseFirstOccurrence.ContainsKey(currentPhase))
                {
                    phaseFirstOccurrence[currentPhase] = phaseTransitions.Count - 1;
                }

                previousPhase = currentPhase;
            }

            // Track completion (when phase is marked "Complete") - only on first occurrence
            if (string.Equals(currentPhase, "Complete", StringComparison.OrdinalIgnoreCase) && executionCompleted is null)
            {
                executionCompleted = revision.ChangedDate;
            }
        }

        // Deduplicate phases that appear multiple times
        // Keep only the first occurrence of each phase, but update end time based on next phase
        var uniquePhases = new List<(string phase, DateTimeOffset time)>();
        var seenPhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < phaseTransitions.Count; i++)
        {
            var (phaseName, _) = phaseTransitions[i];

            // If we've already seen this phase, skip it (don't re-add it)
            if (seenPhases.Contains(phaseName))
            {
                continue;
            }

            // Add the first occurrence with its original timestamp
            uniquePhases.Add((phaseName, phaseTransitions[i].time));
            seenPhases.Add(phaseName);
        }

        phaseTransitions = uniquePhases;

        // Build phase history events from transitions
        var phaseList = new List<PhaseHistoryEvent>();
        for (int i = 0; i < phaseTransitions.Count; i++)
        {
            var (phaseName, startTime) = phaseTransitions[i];

            // End time is when the next phase started
            DateTimeOffset? endTime = null;
            if (i < phaseTransitions.Count - 1)
            {
                // For Error phase, duration should be 0 or minimal (it's a point-in-time event, not a work phase)
                if (string.Equals(phaseName, "Error", StringComparison.OrdinalIgnoreCase))
                {
                    endTime = startTime;
                }
                else
                {
                    endTime = phaseTransitions[i + 1].time;
                }
            }
            else if (!string.Equals(phaseName, "Complete", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(phaseName, "Error", StringComparison.OrdinalIgnoreCase))
            {
                // For non-Complete, non-Error phases that are last, use execution completion time
                endTime = executionCompleted;
            }
            else if (string.Equals(phaseName, "Complete", StringComparison.OrdinalIgnoreCase) && executionCompleted.HasValue)
            {
                // For Complete phase, end time is same as start time (zero duration)
                endTime = startTime;
            }
            else if (string.Equals(phaseName, "Error", StringComparison.OrdinalIgnoreCase))
            {
                // For Error phase that is last, end time is same as start time (zero duration)
                endTime = startTime;
            }

            // Determine phase status
            var phaseStatus = PhaseStatus.InProgress;
            if (endTime.HasValue && string.Equals(phaseName, "Complete", StringComparison.OrdinalIgnoreCase))
            {
                phaseStatus = PhaseStatus.Completed;
            }
            else if (endTime.HasValue)
            {
                phaseStatus = PhaseStatus.Completed;
            }

            // Check for errors in this phase
            bool hasError = errorsByPhase.ContainsKey(phaseName);
            DateTimeOffset? errorTime = null;
            string errorMessage = string.Empty;

            if (hasError && errorsByPhase[phaseName].Count > 0)
            {
                var firstError = errorsByPhase[phaseName].First();
                errorTime = firstError.time;
                errorMessage = firstError.message;
            }

            var phaseEvent = new PhaseHistoryEvent
            {
                PhaseName = phaseName,
                StartTime = startTime,
                EndTime = endTime,
                FinalStatus = phaseStatus,
                HasError = hasError,
                ErrorTime = errorTime,
                ErrorMessage = CleanErrorMessage(errorMessage)
            };

            phaseList.Add(phaseEvent);
        }

        // Calculate metrics
        var completedPhases = phaseList.Count(p => p.FinalStatus == PhaseStatus.Completed);
        var erroredPhases = phaseList.Count(p => p.HasError);
        var recoveredErrors = phaseList.Count(p => p.HasError && p.FinalStatus == PhaseStatus.Completed);

        decimal successRate = phaseList.Count > 0
            ? (completedPhases * 100m) / phaseList.Count
            : 0;

        var currentRunningPhase = previousPhase ?? string.Empty;
        var finalStatus = string.Equals(currentRunningPhase, "Complete", StringComparison.OrdinalIgnoreCase)
            ? "Completed"
            : string.Empty;

        return new PhaseHistorySummary
        {
            ExecutionStarted = executionStarted,
            ExecutionCompleted = executionCompleted,
            CurrentRunningPhase = currentRunningPhase,
            FinalStatus = finalStatus,
            TotalPhases = phaseList.Count,
            CompletedPhases = completedPhases,
            ErroredPhases = erroredPhases,
            RecoveredErrors = recoveredErrors,
            SuccessRate = successRate,
            PhaseHistory = phaseList.OrderBy(p => p.StartTime ?? DateTimeOffset.MinValue).ToList()
        };
    }

    /// <summary>
    /// Extracts error events from comments, grouped by phase name.
    /// Handles various error formats and HTML-encoded content.
    /// </summary>
    private static Dictionary<string, List<(DateTimeOffset time, string message)>> ExtractErrorsFromComments(
        List<CommentDto> comments)
    {
        var errors = new Dictionary<string, List<(DateTimeOffset time, string message)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var comment in comments.Where(c => c.CreatedDate.HasValue))
        {
            var content = comment.Content ?? comment.Text ?? string.Empty;

            // Decode HTML entities if present
            content = System.Net.WebUtility.HtmlDecode(content);

            // Look for error patterns:
            // 1. "Error in phase: <PhaseName>"
            // 2. "Error: <message>"
            // 3. HTML error blocks

            var errorMatch = Regex.Match(
                content,
                @"(?:Error in phase|error in)\s*:\s*([^\n<;]+)",
                RegexOptions.IgnoreCase);

            if (errorMatch.Success)
            {
                var phaseName = errorMatch.Groups[1].Value.Trim();
                if (!errors.ContainsKey(phaseName))
                    errors[phaseName] = new();

                var cleanedMessage = CleanErrorMessage(content);
                errors[phaseName].Add((comment.CreatedDate.Value, cleanedMessage));
            }
            else if (content.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                // Generic error - try to associate with current phase if available
                // Extract phase name if present
                var phaseMatch = Regex.Match(
                    content,
                    @"(?:phase|stage)\s*:\s*([^\n<;]+)",
                    RegexOptions.IgnoreCase);

                if (phaseMatch.Success)
                {
                    var phaseName = phaseMatch.Groups[1].Value.Trim();
                    if (!errors.ContainsKey(phaseName))
                        errors[phaseName] = new();

                    var cleanedMessage = CleanErrorMessage(content);
                    errors[phaseName].Add((comment.CreatedDate.Value, cleanedMessage));
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Cleans error messages by removing HTML tags and decoding entities.
    /// </summary>
    private static string CleanErrorMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        // Remove HTML tags
        var cleaned = Regex.Replace(message, @"<[^>]*>", string.Empty);

        // Decode HTML entities
        cleaned = System.Net.WebUtility.HtmlDecode(cleaned);

        // Remove extra whitespace
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        // Limit length
        return cleaned.Length > 200 ? cleaned.Substring(0, 200) : cleaned;
    }

    /// <summary>
    /// DTO representing a single revision from Azure DevOps work item history.
    /// </summary>
    internal sealed class RevisionEventDto
    {
        public DateTimeOffset? ChangedDate { get; init; }
        public string? OrchestratorPhase { get; init; }
        public string? State { get; init; }
    }

    /// <summary>
    /// DTO for comment data from Azure DevOps.
    /// </summary>
    internal sealed class CommentDto
    {
        public string? Content { get; init; }
        public string? Text { get; init; }
        public DateTimeOffset? CreatedDate { get; init; }
    }
}
