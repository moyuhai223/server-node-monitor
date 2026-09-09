using System.Net;

namespace SNM.Agent.Net;

/// <summary>Reconnect back-off (docs/PROTOCOL.md 7.9): 1,2,4,8,16,32,60,60... seconds with +-20% jitter.</summary>
internal sealed class Backoff
{
    private static readonly int[] Steps = [1, 2, 4, 8, 16, 32, 60];
    private int _attempt;

    public int Attempt => _attempt;

    public void Reset() => _attempt = 0;

    public TimeSpan Next(FailureKind kind)
    {
        _attempt++;
        double seconds = kind switch
        {
            FailureKind.AuthRejected => 60,
            FailureKind.ProtocolMismatch => 600,
            _ => Steps[Math.Min(_attempt - 1, Steps.Length - 1)],
        };
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    /// <summary>Log every attempt for the first 10, then one in ten.</summary>
    public bool ShouldLog => _attempt <= 10 || _attempt % 10 == 0;
}

internal enum FailureKind { Network, AuthRejected, ProtocolMismatch }

/// <summary>--proxy parsing (docs/PROTOCOL.md 7.8).</summary>
internal static class ProxyParser
{
    /// <summary>Returns the proxy to use; <paramref name="forceDirect"/> is true for "none".</summary>
    public static IWebProxy? Parse(string? value, out bool forceDirect)
    {
        forceDirect = false;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) { forceDirect = true; return null; }
        var uri = new Uri(value, UriKind.Absolute);
        var proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
        if (uri.UserInfo.Length > 0)
        {
            var parts = uri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            // hide credentials from the Address used for logging
            proxy.Address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
        }
        return proxy;
    }
}
