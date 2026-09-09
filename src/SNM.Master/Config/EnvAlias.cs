namespace SNM.Master.Config;

/// <summary>
/// Friendly SNM_* environment variables mapped onto the "Snm" configuration section (docs/DEPLOY.md).
/// Added after the standard environment provider so they take precedence over Snm__* names.
/// </summary>
public static class EnvAlias
{
    private static readonly (string Env, string Key)[] Scalars =
    [
        ("SNM_LISTEN", "Snm:Listen"),
        ("SNM_DATA_DIR", "Snm:DataDir"),
        ("SNM_PUBLIC_BASE_URL", "Snm:PublicBaseUrl"),
        ("SNM_FORWARD_LIMIT", "Snm:ForwardLimit"),
        ("SNM_TIMEZONE", "Snm:TimeZone"),
        ("SNM_ADMIN_USER", "Snm:Admin:User"),
        ("SNM_ADMIN_PASSWORD", "Snm:Admin:Password"),
        ("SNM_JWT_SECRET", "Snm:Jwt:Secret"),
        ("SNM_GEOIP_ENABLED", "Snm:GeoIp:Enabled"),
        ("SNM_GEOIP_BASE_URL", "Snm:GeoIp:BaseUrl"),
        ("SNM_LOG_LEVEL", "Logging:LogLevel:Default"),
    ];

    private static readonly (string Env, string Key)[] Lists =
    [
        ("SNM_KNOWN_PROXIES", "Snm:KnownProxies"),
        ("SNM_KNOWN_NETWORKS", "Snm:KnownNetworks"),
    ];

    public static IEnumerable<KeyValuePair<string, string?>> Read(Func<string, string?> getEnv)
    {
        foreach (var (env, key) in Scalars)
        {
            var v = getEnv(env);
            if (!string.IsNullOrWhiteSpace(v)) yield return new(key, v.Trim());
        }

        foreach (var (env, key) in Lists)
        {
            var v = getEnv(env);
            if (string.IsNullOrWhiteSpace(v)) continue;
            var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Clear inherited entries beyond the provided ones by writing the exact list.
            for (var i = 0; i < parts.Length; i++) yield return new($"{key}:{i}", parts[i]);
        }
    }
}
