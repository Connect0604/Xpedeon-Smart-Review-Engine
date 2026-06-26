namespace MigrationDashboard.Web.Models;

public static class OwnershipCategories
{
    public const string BlazorOwned = "BLAZOR_OWNED";
    public const string Shared = "SHARED";
    public const string Legacy = "LEGACY";
    public const string Retiring = "RETIRING";

    public static readonly IReadOnlyList<string> All =
    [
        BlazorOwned,
        Shared,
        Legacy,
        Retiring
    ];
}
