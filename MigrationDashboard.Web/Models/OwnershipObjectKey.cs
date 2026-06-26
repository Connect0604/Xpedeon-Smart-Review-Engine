namespace MigrationDashboard.Web.Models;

public sealed record OwnershipObjectKey(
    int FormId,
    string Layer,
    string ObjectName,
    string ObjectType);
