namespace MigrationDashboard.Web.Models;

public sealed record ChangeEventRow(
    int EventId,
    string? BuildId,
    string? BuildNumber,
    string? CommitId,
    string? BranchName,
    string? ChangedBy,
    DateTime EventTimestamp,
    int FormsAffected,
    int ObjectsAffected,
    string? AlertSentTo);
