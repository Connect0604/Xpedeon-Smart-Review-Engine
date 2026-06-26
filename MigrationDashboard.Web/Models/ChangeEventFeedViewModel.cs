namespace MigrationDashboard.Web.Models;

public sealed record ChangeEventFeedViewModel(
    IReadOnlyList<ChangeEventRow> Events,
    IReadOnlyList<ChangeEventDetailRow> EventDetails);
