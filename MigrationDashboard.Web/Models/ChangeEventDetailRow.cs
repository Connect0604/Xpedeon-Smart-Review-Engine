namespace MigrationDashboard.Web.Models;

public sealed record ChangeEventDetailRow(
    int DetailId,
    int EventId,
    int FormId,
    string ObjectName,
    int? ObjectOwnershipId,
    string ChangedFilePath,
    string OwnershipCategory,
    string? Layer,
    string? ObjectType,
    string ReviewStatus,
    DateTime EventTimestamp,
    string? ModifiedBy,
    DateTime? ModifiedDate,
    string? Remarks = null);
