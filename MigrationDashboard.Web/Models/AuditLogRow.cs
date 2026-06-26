namespace MigrationDashboard.Web.Models;

public sealed record AuditLogRow(
    long AuditId,
    string EntityName,
    string EntityKey,
    string ActionName,
    string? FieldName,
    string? OldValue,
    string? NewValue,
    string ChangedBy,
    DateTime ChangedDate,
    int? FormId,
    string? FormName,
    string? ProcessCode,
    string? StepCode);
