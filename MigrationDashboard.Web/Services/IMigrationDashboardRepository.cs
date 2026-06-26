using MigrationDashboard.Web.Models;

namespace MigrationDashboard.Web.Services;

public interface IMigrationDashboardRepository
{
    Task<IReadOnlyList<MigrationFormListItem>> GetFormsAsync(CancellationToken cancellationToken);
    Task<MigrationFormDetail?> GetFormDetailAsync(int formId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OwnershipObjectRow>> GetOwnershipRowsAsync(int formId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChangeEventRow>> GetChangeEventsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ChangeEventDetailRow>> GetChangeEventDetailsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditLogRow>> GetAuditLogsAsync(CancellationToken cancellationToken);
    Task<IReadOnlySet<int>> SearchFormIdsByObjectAsync(string searchTerm, CancellationToken cancellationToken);
    Task<EditorAppUser?> GetActiveEditorAsync(string userName, CancellationToken cancellationToken);
    Task RecordLoginAsync(int userId, CancellationToken cancellationToken);
    Task RecordLogoutAsync(string userName, CancellationToken cancellationToken);
    Task RecordDisconnectAsync(string userName, CancellationToken cancellationToken);
    Task UpdateOwnershipAsync(UpdateOwnershipRowRequest request, string modifiedBy, CancellationToken cancellationToken);
    Task UpdateReviewStatusAsync(UpdateReviewStatusRequest request, string modifiedBy, CancellationToken cancellationToken);
}
