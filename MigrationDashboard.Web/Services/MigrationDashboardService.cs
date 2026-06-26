using MigrationDashboard.Web.Models;

namespace MigrationDashboard.Web.Services;

public sealed class MigrationDashboardService(
    IMigrationDashboardRepository repository,
    IEditorIdentityAccessor editorIdentityAccessor) : IMigrationDashboardService
{
    public MigrationDashboardService(IMigrationDashboardRepository repository)
        : this(repository, new AnonymousEditorIdentityAccessor())
    {
    }

    public async Task<IReadOnlyList<MigrationFormListItem>> GetFormsAsync(string? searchTerm, string? searchScope, CancellationToken cancellationToken)
    {
        var forms = await repository.GetFormsAsync(cancellationToken);
        var normalizedSearchTerm = NormalizeSearchTerm(searchTerm);
        IReadOnlySet<int>? matchingFormIds = null;

        if (normalizedSearchTerm is not null && IsAllSearchScope(searchScope))
        {
            var objectMatchingFormIds = await repository.SearchFormIdsByObjectAsync(normalizedSearchTerm, cancellationToken);
            var events = await repository.GetChangeEventsAsync(cancellationToken);
            var eventDetails = await repository.GetChangeEventDetailsAsync(cancellationToken);
            var eventRowMatchingIds = events
                .Where(item => EventRowMatches(item, normalizedSearchTerm))
                .Select(item => item.EventId)
                .ToHashSet();

            var eventDetailMatchingFormIds = eventDetails
                .Where(detail => EventDetailMatches(detail, normalizedSearchTerm))
                .Select(detail => detail.FormId)
                .ToHashSet();

            var eventRowMatchingFormIds = eventDetails
                .Where(detail => eventRowMatchingIds.Contains(detail.EventId))
                .Select(detail => detail.FormId)
                .ToHashSet();

            matchingFormIds = objectMatchingFormIds
                .Concat(eventDetailMatchingFormIds)
                .Concat(eventRowMatchingFormIds)
                .ToHashSet();
        }

        return forms
            .Where(form => normalizedSearchTerm is null
                || MatchesFormSearchScope(form, normalizedSearchTerm, searchScope)
                || (matchingFormIds?.Contains(form.FormId) ?? false))
            .OrderBy(form => form.FormName)
            .ToList();
    }

    public async Task<MigrationDashboardViewModel?> GetDashboardAsync(int formId, string? layerFilter, string? ownershipFilter, string? searchTerm, string? searchScope, CancellationToken cancellationToken)
    {
        var form = await repository.GetFormDetailAsync(formId, cancellationToken);
        if (form is null)
        {
            return null;
        }

        var rows = await repository.GetOwnershipRowsAsync(formId, cancellationToken);
        var normalizedSearchTerm = NormalizeSearchTerm(searchTerm);

        var baseFilteredRows = rows
            .Where(row => string.IsNullOrWhiteSpace(layerFilter)
                || string.Equals(row.Layer, layerFilter, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(ownershipFilter)
                || string.Equals(row.OwnershipCategory, ownershipFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Layer)
            .ThenBy(row => row.ObjectName)
            .ToList();

        var filteredRows = baseFilteredRows;
        if (normalizedSearchTerm is not null)
        {
            if (IsSpecificFormFieldScope(searchScope))
            {
                filteredRows = MatchesFormSearchScope(form, normalizedSearchTerm, searchScope)
                    ? baseFilteredRows
                    : [];

                return new MigrationDashboardViewModel(form, filteredRows);
            }

            var searchFilteredRows = baseFilteredRows
                .Where(row => RowMatches(row, normalizedSearchTerm))
                .ToList();

            if (searchFilteredRows.Count > 0)
            {
                filteredRows = searchFilteredRows;
            }
            else if (!FormMatches(form, normalizedSearchTerm)
                && !await EventMatchesFormAsync(form.FormId, normalizedSearchTerm, cancellationToken))
            {
                filteredRows = searchFilteredRows;
            }
        }

        return new MigrationDashboardViewModel(form, filteredRows);
    }

    public async Task<ChangeEventFeedViewModel> GetEventsAsync(string? searchTerm, string? searchScope, CancellationToken cancellationToken)
    {
        var normalizedSearchTerm = NormalizeSearchTerm(searchTerm);
        var events = await repository.GetChangeEventsAsync(cancellationToken);
        var eventDetails = await repository.GetChangeEventDetailsAsync(cancellationToken);

        if (normalizedSearchTerm is not null)
        {
            if (IsSpecificFormFieldScope(searchScope))
            {
                var forms = await repository.GetFormsAsync(cancellationToken);
                var matchingFormIds = forms
                    .Where(form => MatchesFormSearchScope(form, normalizedSearchTerm, searchScope))
                    .Select(form => form.FormId)
                    .ToHashSet();

                eventDetails = eventDetails
                    .Where(item => matchingFormIds.Contains(item.FormId))
                    .ToList();

                var matchingEventIds = eventDetails
                    .Select(item => item.EventId)
                    .ToHashSet();

                events = events
                    .Where(item => matchingEventIds.Contains(item.EventId))
                    .ToList();
            }
            else
            {
                var matchingEventIds = events
                    .Where(item => EventRowMatches(item, normalizedSearchTerm)
                        || eventDetails.Any(detail => detail.EventId == item.EventId && EventDetailMatches(detail, normalizedSearchTerm)))
                    .Select(item => item.EventId)
                    .ToHashSet();

                events = events
                    .Where(item => matchingEventIds.Contains(item.EventId))
                    .ToList();

                eventDetails = eventDetails
                    .Where(item => matchingEventIds.Contains(item.EventId))
                    .ToList();
            }
        }

        return new ChangeEventFeedViewModel(
            events
                .OrderByDescending(item => item.EventTimestamp)
                .ThenByDescending(item => item.EventId)
                .ToList(),
            eventDetails
                .OrderByDescending(item => item.EventTimestamp)
                .ThenByDescending(item => item.DetailId)
                .ToList());
    }

    public async Task<IReadOnlyList<AuditLogRow>> GetAuditLogsAsync(string? formName, string? processCode, string? stepCode, string? searchTerm, CancellationToken cancellationToken)
    {
        var auditLogs = await repository.GetAuditLogsAsync(cancellationToken);
        var normalizedSearchTerm = NormalizeSearchTerm(searchTerm);
        var normalizedFormName = NormalizeSearchTerm(formName);
        var normalizedProcessCode = NormalizeSearchTerm(processCode);
        var normalizedStepCode = NormalizeSearchTerm(stepCode);

        return auditLogs
            .Where(item => normalizedFormName is null
                || (item.FormName?.Contains(normalizedFormName, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(item => normalizedProcessCode is null
                || (item.ProcessCode?.Contains(normalizedProcessCode, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(item => normalizedStepCode is null
                || (item.StepCode?.Contains(normalizedStepCode, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(item => normalizedSearchTerm is null || AuditLogMatches(item, normalizedSearchTerm))
            .OrderByDescending(item => item.ChangedDate)
            .ThenByDescending(item => item.AuditId)
            .ToList();
    }

    public Task UpdateOwnershipAsync(UpdateOwnershipRowRequest request, CancellationToken cancellationToken)
    {
        return UpdateOwnershipInternalAsync(request, cancellationToken);
    }

    public Task UpdateReviewStatusAsync(UpdateReviewStatusRequest request, CancellationToken cancellationToken)
    {
        return UpdateReviewStatusInternalAsync(request, cancellationToken);
    }

    private static string? NormalizeSearchTerm(string? searchTerm)
    {
        return string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm.Trim();
    }

    private static bool FormMatches(MigrationFormListItem form, string searchTerm)
    {
        return ContainsIgnoreCase(form.FormName, searchTerm)
            || ContainsIgnoreCase(form.ProcessCode, searchTerm)
            || ContainsIgnoreCase(form.StepCode, searchTerm);
    }

    private static bool FormMatches(MigrationFormDetail form, string searchTerm)
    {
        return ContainsIgnoreCase(form.FormName, searchTerm)
            || ContainsIgnoreCase(form.ProcessCode, searchTerm)
            || ContainsIgnoreCase(form.StepCode, searchTerm)
            || ContainsIgnoreCase(form.Remarks, searchTerm);
    }

    private static bool MatchesFormSearchScope(MigrationFormListItem form, string searchTerm, string? searchScope)
    {
        return searchScope switch
        {
            SearchScopes.FormName => ContainsIgnoreCase(form.FormName, searchTerm),
            SearchScopes.ProcessCode => ContainsIgnoreCase(form.ProcessCode, searchTerm),
            SearchScopes.StepCode => ContainsIgnoreCase(form.StepCode, searchTerm),
            _ => FormMatches(form, searchTerm)
        };
    }

    private static bool MatchesFormSearchScope(MigrationFormDetail form, string searchTerm, string? searchScope)
    {
        return searchScope switch
        {
            SearchScopes.FormName => ContainsIgnoreCase(form.FormName, searchTerm),
            SearchScopes.ProcessCode => ContainsIgnoreCase(form.ProcessCode, searchTerm),
            SearchScopes.StepCode => ContainsIgnoreCase(form.StepCode, searchTerm),
            _ => FormMatches(form, searchTerm)
        };
    }

    private static bool RowMatches(OwnershipObjectRow row, string searchTerm)
    {
        return row.ObjectName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.ObjectType.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.Layer.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.OwnershipCategory.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || (row.Remarks?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool EventRowMatches(ChangeEventRow row, string searchTerm)
    {
        return row.EventId.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || ContainsIgnoreCase(row.BuildId, searchTerm)
            || ContainsIgnoreCase(row.BuildNumber, searchTerm)
            || ContainsIgnoreCase(row.CommitId, searchTerm)
            || ContainsIgnoreCase(row.BranchName, searchTerm)
            || ContainsIgnoreCase(row.ChangedBy, searchTerm)
            || ContainsIgnoreCase(row.AlertSentTo, searchTerm);
    }

    private static bool EventDetailMatches(ChangeEventDetailRow row, string searchTerm)
    {
        return row.EventId.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.FormId.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.ObjectName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.ChangedFilePath.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.OwnershipCategory.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || ContainsIgnoreCase(row.Layer, searchTerm)
            || ContainsIgnoreCase(row.ObjectType, searchTerm)
            || row.ReviewStatus.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || ContainsIgnoreCase(row.Remarks, searchTerm);
    }

    private static bool AuditLogMatches(AuditLogRow row, string searchTerm)
    {
        return row.AuditId.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.EntityName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.EntityKey.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || row.ActionName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || (row.FieldName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.OldValue?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.NewValue?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false)
            || row.ChangedBy.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
            || (row.FormName?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.ProcessCode?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.StepCode?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool ContainsIgnoreCase(string? value, string searchTerm)
    {
        return value?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private async Task<bool> EventMatchesFormAsync(int formId, string searchTerm, CancellationToken cancellationToken)
    {
        var events = await repository.GetChangeEventsAsync(cancellationToken);
        var eventDetails = await repository.GetChangeEventDetailsAsync(cancellationToken);

        var matchingEventIds = events
            .Where(item => EventRowMatches(item, searchTerm)
                || eventDetails.Any(detail => detail.EventId == item.EventId && EventDetailMatches(detail, searchTerm)))
            .Select(item => item.EventId)
            .ToHashSet();

        return eventDetails.Any(detail => detail.FormId == formId && matchingEventIds.Contains(detail.EventId));
    }

    private static bool IsAllSearchScope(string? searchScope)
    {
        return !IsSpecificFormFieldScope(searchScope);
    }

    private static bool IsSpecificFormFieldScope(string? searchScope)
    {
        return string.Equals(searchScope, SearchScopes.FormName, StringComparison.Ordinal)
            || string.Equals(searchScope, SearchScopes.ProcessCode, StringComparison.Ordinal)
            || string.Equals(searchScope, SearchScopes.StepCode, StringComparison.Ordinal);
    }

    private async Task UpdateOwnershipInternalAsync(UpdateOwnershipRowRequest request, CancellationToken cancellationToken)
    {
        var currentEditor = await editorIdentityAccessor.GetCurrentEditorAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(currentEditor))
        {
            throw new InvalidOperationException("An authenticated editor is required before saving.");
        }

        if (!OwnershipCategories.All.Contains(request.OwnershipCategory, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("OwnershipCategory is invalid.", nameof(request));
        }

        await repository.UpdateOwnershipAsync(request with
        {
            Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim()
        }, currentEditor.Trim(), cancellationToken);
    }

    private async Task UpdateReviewStatusInternalAsync(UpdateReviewStatusRequest request, CancellationToken cancellationToken)
    {
        var currentEditor = await editorIdentityAccessor.GetCurrentEditorAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(currentEditor))
        {
            throw new InvalidOperationException("An authenticated editor is required before saving.");
        }

        if (!ReviewStatuses.All.Contains(request.ReviewStatus, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("ReviewStatus is invalid.", nameof(request));
        }

        await repository.UpdateReviewStatusAsync(request with
        {
            ReviewStatus = request.ReviewStatus.Trim().ToUpperInvariant(),
            Remarks = string.IsNullOrWhiteSpace(request.Remarks) ? null : request.Remarks.Trim()
        }, currentEditor.Trim(), cancellationToken);
    }

    private sealed class AnonymousEditorIdentityAccessor : IEditorIdentityAccessor
    {
        public Task<string?> GetCurrentEditorAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }
}
