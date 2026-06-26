namespace MigrationDashboard.Web.Services;

public interface IEditSessionRegistry
{
    string CreateEditGrant(string userName);
    bool TryRedeemEditGrant(string grantToken, string userName, string circuitId);
    bool HasActiveEditSession(string userName, string circuitId);
    bool RevokeCircuitSession(string circuitId);
    string? GetEditorForCircuit(string circuitId);
    bool RevokeUserSessions(string userName);
}
