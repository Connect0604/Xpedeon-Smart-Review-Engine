namespace MigrationDashboard.Web.Models;

public sealed record EditorAppUser(
    int UserId,
    string UserName,
    string PasswordHash,
    bool IsActive,
    DateTime CreatedDate,
    DateTime? LastLoginDate,
    DateTime? LastLogoutDate);

