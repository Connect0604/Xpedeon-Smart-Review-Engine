namespace MigrationDashboard.Web.Models;

public sealed record MigrationFormDetail(
    int FormId,
    string FormName,
    string? ProcessCode,
    string? StepCode,
    DateOnly? HandoffDate,
    string? Remarks,
    string Status,
    string? ownership_updated = null);
