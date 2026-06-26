using Microsoft.AspNetCore.Identity;
using MigrationDashboard.Web.Models;

namespace MigrationDashboard.Web.Services;

public static class EditorPasswordHashUtility
{
    public static string GenerateHash(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new ArgumentException("Username is required.", nameof(userName));
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        var normalizedUserName = userName.Trim();
        var appUser = new EditorAppUser(
            0,
            normalizedUserName,
            string.Empty,
            true,
            DateTime.UtcNow,
            null,
            null);

        var passwordHasher = new PasswordHasher<EditorAppUser>();
        return passwordHasher.HashPassword(appUser, password);
    }
}
