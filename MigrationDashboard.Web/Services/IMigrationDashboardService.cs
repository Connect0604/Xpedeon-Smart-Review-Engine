using MigrationDashboard.Web.Models;

namespace MigrationDashboard.Web.Services;

public interface IMigrationDashboardService
{
    Task<IReadOnlyList<MigrationFormListItem>> GetFormsAsync(string? searchTerm, string? searchScope, CancellationToken cancellationToken);
    Task<MigrationDashboardViewModel?> GetDashboardAsync(int formId, string? layerFilter, string? ownershipFilter, string? searchTerm, string? searchScope, CancellationToken cancellationToken);
    Task<ChangeEventFeedViewModel> GetEventsAsync(string? searchTerm, string? searchScope, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditLogRow>> GetAuditLogsAsync(string? formName, string? processCode, string? stepCode, string? searchTerm, CancellationToken cancellationToken);
    Task UpdateOwnershipAsync(UpdateOwnershipRowRequest request, CancellationToken cancellationToken);
    Task UpdateReviewStatusAsync(UpdateReviewStatusRequest request, CancellationToken cancellationToken);
    Task RunSyncBatchAsync(string batchFilePath, CancellationToken cancellationToken);
}
