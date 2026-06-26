namespace MigrationDashboard.Web.Models;

public sealed record OwnershipObjectRow(
    OwnershipObjectKey Key,
    int FormId,
    string Layer,
    string ObjectName,
    string ObjectType,
    string OwnershipCategory,
    string? Remarks,
    string? CreatedBy,
    DateTime? CreatedDate,
    string? ModifiedBy,
    DateTime? ModifiedDate);
