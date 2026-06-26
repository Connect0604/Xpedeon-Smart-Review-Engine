namespace MigrationDashboard.Web.Models;

public sealed record MigrationDashboardViewModel(
    MigrationFormDetail Form,
    IReadOnlyList<OwnershipObjectRow> ObjectRows);
