namespace MigrationDashboard.Web.Models;

public sealed record UpdateOwnershipRowRequest(
    OwnershipObjectKey Key,
    string OwnershipCategory,
    string? Remarks);
