namespace MigrationDashboard.Web.Models;

public static class ReviewStatuses
{
    public const string Escalated = "ESCALATED";
    public const string Dismissed = "DISMISSED";
    public const string Actioned = "ACTIONED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Pending = "PENDING";

    public static IReadOnlyList<string> All { get; } =
    [
        Escalated,
        Dismissed,
        Actioned,
        Acknowledged,
        Pending
    ];
}
