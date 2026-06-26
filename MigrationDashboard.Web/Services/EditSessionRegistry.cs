using System.Collections.Concurrent;

namespace MigrationDashboard.Web.Services;

public sealed class EditSessionRegistry(TimeProvider timeProvider) : IEditSessionRegistry
{
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, EditGrant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveEditSession> _sessionsByCircuit = new(StringComparer.Ordinal);

    public string CreateEditGrant(string userName)
    {
        var normalizedUserName = NormalizeUserName(userName);
        var token = Guid.NewGuid().ToString("N");
        _grants[token] = new EditGrant(normalizedUserName, timeProvider.GetUtcNow().Add(GrantLifetime));
        return token;
    }

    public bool TryRedeemEditGrant(string grantToken, string userName, string circuitId)
    {
        if (string.IsNullOrWhiteSpace(grantToken) || string.IsNullOrWhiteSpace(circuitId))
        {
            return false;
        }

        var normalizedUserName = NormalizeUserName(userName);
        if (!_grants.TryRemove(grantToken, out var grant))
        {
            return false;
        }

        if (!string.Equals(grant.UserName, normalizedUserName, StringComparison.Ordinal))
        {
            return false;
        }

        if (grant.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return false;
        }

        _sessionsByCircuit[circuitId] = new ActiveEditSession(normalizedUserName);
        return true;
    }

    public bool HasActiveEditSession(string userName, string circuitId)
    {
        if (string.IsNullOrWhiteSpace(circuitId))
        {
            return false;
        }

        return _sessionsByCircuit.TryGetValue(circuitId, out var session)
            && string.Equals(session.UserName, NormalizeUserName(userName), StringComparison.Ordinal);
    }

    public bool RevokeCircuitSession(string circuitId)
    {
        return !string.IsNullOrWhiteSpace(circuitId)
            && _sessionsByCircuit.TryRemove(circuitId, out _);
    }

    public string? GetEditorForCircuit(string circuitId)
    {
        return !string.IsNullOrWhiteSpace(circuitId) && _sessionsByCircuit.TryGetValue(circuitId, out var session)
            ? session.UserName
            : null;
    }

    public bool RevokeUserSessions(string userName)
    {
        var normalizedUserName = NormalizeUserName(userName);
        var matchingCircuits = _sessionsByCircuit
            .Where(entry => string.Equals(entry.Value.UserName, normalizedUserName, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var circuitId in matchingCircuits)
        {
            _sessionsByCircuit.TryRemove(circuitId, out _);
        }

        return matchingCircuits.Count > 0;
    }

    private static string NormalizeUserName(string userName)
    {
        return string.IsNullOrWhiteSpace(userName) ? string.Empty : userName.Trim();
    }

    private sealed record EditGrant(string UserName, DateTimeOffset ExpiresAt);
    private sealed record ActiveEditSession(string UserName);
}
