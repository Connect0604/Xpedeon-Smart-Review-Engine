namespace MigrationDashboard.Web.Models;

public sealed record UpdateReviewStatusRequest(
    int DetailId,
    string ReviewStatus,
    string? Remarks = null);
