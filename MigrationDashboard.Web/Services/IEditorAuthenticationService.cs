using MigrationDashboard.Web.Models;

namespace MigrationDashboard.Web.Services;

public interface IEditorAuthenticationService
{
    Task<EditorAuthenticationState> GetAuthenticationStateAsync(CancellationToken cancellationToken = default);
    Task<bool> TryActivateEditSessionAsync(string grantToken, CancellationToken cancellationToken = default);
    string BuildEditModeReturnUrl();
    string BuildLogoutUrl(string returnUrl);
}
