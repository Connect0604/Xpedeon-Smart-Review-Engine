using Microsoft.AspNetCore.Components.Authorization;
using MigrationDashboard.Web.Models;

namespace MigrationDashboard.Web.Services;

public sealed class EditorAuthenticationService(
    AuthenticationStateProvider authenticationStateProvider,
    ICircuitContextAccessor circuitContextAccessor,
    IEditSessionRegistry editSessionRegistry)
    : IEditorAuthenticationService, IEditorIdentityAccessor
{
    public async Task<EditorAuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default)
    {
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var user = authenticationState.User;
        var userName = user.Identity?.IsAuthenticated == true ? user.Identity.Name : null;
        var hasActiveEditSession = !string.IsNullOrWhiteSpace(userName)
            && !string.IsNullOrWhiteSpace(circuitContextAccessor.CircuitId)
            && editSessionRegistry.HasActiveEditSession(userName, circuitContextAccessor.CircuitId);

        return new EditorAuthenticationState(
            !string.IsNullOrWhiteSpace(userName),
            userName,
            hasActiveEditSession);
    }

    public async Task<string?> GetCurrentEditorAsync(CancellationToken cancellationToken = default)
    {
        var authenticationState = await GetAuthenticationStateAsync(cancellationToken);
        return authenticationState.HasActiveEditSession
            ? authenticationState.UserName
            : null;
    }

    public async Task<bool> TryActivateEditSessionAsync(string grantToken, CancellationToken cancellationToken = default)
    {
        var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
        var user = authenticationState.User;
        var userName = user.Identity?.IsAuthenticated == true ? user.Identity.Name : null;
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(circuitContextAccessor.CircuitId))
        {
            return false;
        }

        return editSessionRegistry.TryRedeemEditGrant(grantToken, userName, circuitContextAccessor.CircuitId);
    }

    public string BuildEditModeReturnUrl()
    {
        return "/dashboard?enterEditMode=true";
    }

    public string BuildLogoutUrl(string returnUrl)
    {
        var normalizedReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/dashboard" : returnUrl;
        return $"/editor/logout?returnUrl={Uri.EscapeDataString(normalizedReturnUrl)}";
    }
}
