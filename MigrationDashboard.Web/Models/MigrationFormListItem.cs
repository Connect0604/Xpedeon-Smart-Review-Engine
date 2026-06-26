namespace MigrationDashboard.Web.Models;

public sealed record MigrationFormListItem(
    int FormId,
    string FormName,
    string? ProcessCode,
    string? StepCode,
    DateOnly? HandoffDate,
    string Status);
