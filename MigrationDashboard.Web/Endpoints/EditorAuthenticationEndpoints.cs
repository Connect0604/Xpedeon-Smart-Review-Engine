using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Identity;
using MigrationDashboard.Web.Models;
using MigrationDashboard.Web.Services;

namespace MigrationDashboard.Web.Endpoints;

public static class EditorAuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapEditorAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/editor/login", HandleLoginAsync).DisableAntiforgery();
        endpoints.MapGet("/editor/logout", HandleLogoutAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleLoginAsync(
        [FromForm] EditorLoginPostModel request,
        HttpContext httpContext,
        IMigrationDashboardRepository repository,
        IEditSessionRegistry editSessionRegistry,
        PasswordHasher<EditorAppUser> passwordHasher,
        CancellationToken cancellationToken)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return Results.LocalRedirect(BuildFailedLoginUrl("Enter both username and password."));
        }

        var user = await repository.GetActiveEditorAsync(userName, cancellationToken);
        if (user is null)
        {
            return Results.LocalRedirect(BuildFailedLoginUrl("Invalid username or password."));
        }

        var verificationResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verificationResult == PasswordVerificationResult.Failed)
        {
            return Results.LocalRedirect(BuildFailedLoginUrl("Invalid username or password."));
        }

        await repository.RecordLoginAsync(user.UserId, cancellationToken);

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, user.UserName),
                    new Claim(ClaimTypes.Role, "Editor")
                ],
                CookieAuthenticationDefaults.AuthenticationScheme));

        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = true
            });

        var redirectUrl = SanitizeReturnUrl(request.ReturnUrl, "/?enterEditMode=true");
        var editGrant = editSessionRegistry.CreateEditGrant(user.UserName);
        return Results.LocalRedirect(QueryHelpers.AddQueryString(redirectUrl, "editGrant", editGrant));
    }

    private static async Task<IResult> HandleLogoutAsync(
        string? returnUrl,
        HttpContext httpContext,
        IMigrationDashboardRepository repository,
        IEditSessionRegistry editSessionRegistry,
        CancellationToken cancellationToken)
    {
        var userName = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.Identity?.Name
            : null;

        if (!string.IsNullOrWhiteSpace(userName))
        {
            editSessionRegistry.RevokeUserSessions(userName);
            await repository.RecordLogoutAsync(userName, cancellationToken);
        }

        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.LocalRedirect(SanitizeReturnUrl(returnUrl, "/"));
    }

    private static string BuildFailedLoginUrl(string errorMessage)
    {
        return QueryHelpers.AddQueryString(
            "/",
            new Dictionary<string, string?>
            {
                ["loginError"] = errorMessage
            });
    }

    private static string SanitizeReturnUrl(string? returnUrl, string fallback)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return fallback;
        }

        return Uri.TryCreate(returnUrl, UriKind.Relative, out var relativeUri)
            && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? relativeUri.ToString()
            : fallback;
    }
}
