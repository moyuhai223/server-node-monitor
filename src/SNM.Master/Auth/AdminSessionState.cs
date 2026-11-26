using System.Security.Claims;

namespace SNM.Master.Auth;

/// <summary>Single-master session versions and the AdminHub recipients validated against them.</summary>
public sealed class AdminSessionState
{
    private sealed record Connection(int UserId, int Version, Action Abort);
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _versions = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    public static bool TryReadClaims(ClaimsPrincipal? principal, out int userId, out int version)
    {
        version = -1;
        return int.TryParse(principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId) && userId > 0 &&
            int.TryParse(principal?.FindFirst(JwtTokenService.TokenVersionClaim)?.Value, out version) && version >= 0;
    }

    public bool TryGetVersion(int userId, out int version)
    {
        lock (_gate) return _versions.TryGetValue(userId, out version);
    }

    // A concurrent login or cache miss can return an older database snapshot. Never move a version backwards.
    public int ObserveVersion(int userId, int version)
    {
        List<Action> toAbort;
        lock (_gate)
        {
            if (_versions.TryGetValue(userId, out var known) && known >= version) return known;
            if (version < 0) return version;
            _versions[userId] = version;
            toAbort = [];
            foreach (var (id, connection) in _connections)
            {
                if (connection.UserId != userId || connection.Version >= version) continue;
                _connections.Remove(id);
                toAbort.Add(connection.Abort);
            }
        }
        // Remove recipients before signaling transports. No callbacks run while the registry is locked.
        foreach (var abort in toAbort) abort();
        return version;
    }

    public bool TryRegister(string connectionId, int userId, int version, Action abort)
    {
        lock (_gate) return _versions.TryGetValue(userId, out var current) && current == version &&
            _connections.TryAdd(connectionId, new(userId, version, abort));
    }

    public bool IsCurrentConnection(string connectionId)
    {
        lock (_gate) return _connections.TryGetValue(connectionId, out var connection) &&
            _versions[connection.UserId] == connection.Version;
    }

    public void Remove(string connectionId) { lock (_gate) _connections.Remove(connectionId); }
    public string[] Recipients() { lock (_gate) return [.. _connections.Keys]; }
    public int ConnectionCount { get { lock (_gate) return _connections.Count; } }
}
