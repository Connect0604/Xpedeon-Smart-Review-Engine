namespace MigrationDashboard.Web.Models;

public sealed record EditorAuthenticationState(
    bool IsAuthenticated,
    string? UserName,
    bool HasActiveEditSession = false);
