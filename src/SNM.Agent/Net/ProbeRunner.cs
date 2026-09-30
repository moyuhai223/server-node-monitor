using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using SNM.Agent.Logging;
using SNM.Contracts;
using SNM.Contracts.Dtos;

namespace SNM.Agent.Net;

internal static class ProbeRunner
{
    // Direct node network only. The Master's HTTP/SOCKS proxy does not apply to measurements.
    internal static async Task<ProbeResultDto> MeasureAsync(ProbeTargetDto t, CancellationToken ct)
    {
        var result = new ProbeResultDto { Id = t.Id, Revision = t.Revision, Status = 2 };
        if (t.Kind > 1 || t.TimeoutMs is < 200 or > 10000 || t.IntervalSec is < 10 or > 3600
            || t.Address.Length is 0 or > 253 || (t.Kind == 1 && t.Port == 0)) return result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(t.TimeoutMs);
        try
        {
            var addresses = IPAddress.TryParse(t.Address, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(t.Address, timeout.Token);
            if (addresses.Length == 0) return result;
            // Prefer IPv4 for hostnames; explicit IPv6 addresses stay IPv6. DNS time is excluded from RTT.
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
            var start = Stopwatch.GetTimestamp();
            if (t.Kind == 0)
            {
                // On Unix, Ping can otherwise fall back to an external ping executable.
                // Require raw-socket support explicitly so the standalone agent never does that.
                if (!OperatingSystem.IsWindows())
                {
                    using var permission = new Socket(address.AddressFamily, SocketType.Raw,
                        address.AddressFamily == AddressFamily.InterNetworkV6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
                }
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(t.TimeoutMs), cancellationToken: timeout.Token);
                result.Status = reply.Status == IPStatus.Success ? (byte)0 : reply.Status == IPStatus.TimedOut ? (byte)1 : (byte)2;
                if (result.Status == 0) result.Microseconds = (int)Math.Min(reply.RoundtripTime * 1000, 10_000_000);
            }
            else
            {
                using var tcp = new TcpClient(address.AddressFamily);
                await tcp.ConnectAsync(address, t.Port, timeout.Token);
                result.Status = 0;
                result.Microseconds = (int)Math.Clamp(Stopwatch.GetElapsedTime(start).TotalMicroseconds, 0, 10_000_000);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result.Status = 1; }
        catch (PlatformNotSupportedException) { result.Status = 3; }
        catch (UnauthorizedAccessException) { result.Status = 3; }
        catch (PingException ex) { result.Status = ex.InnerException is SocketException { SocketErrorCode: SocketError.AccessDenied } ? (byte)3 : (byte)2; }
        catch (SocketException ex) { result.Status = ex.SocketErrorCode == SocketError.AccessDenied ? (byte)3 : (byte)2; }
        return result;
    }

    internal static async Task RunAsync(HubConnection conn, CancellationToken ct)
    {
        ProbeTargetDto[] targets = [];
        var next = new Dictionary<string, long>();
        long refreshed = 0;
        while (!ct.IsCancellationRequested)
        {
            if (refreshed == 0 || Stopwatch.GetElapsedTime(refreshed).TotalSeconds >= 30)
            {
                try
                {
                    using var fetch = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    fetch.CancelAfter(TimeSpan.FromSeconds(10));
                    targets = (await conn.InvokeAsync<ProbeConfigDto>(AgentHubMethods.GetProbes, fetch.Token)).Targets;
                    if (targets.Length > 16) targets = [];
                    var revisions = targets.Select(t => t.Revision).ToHashSet();
                    foreach (var key in next.Keys.Where(k => !revisions.Contains(k)).ToArray()) next.Remove(key);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is HubException or OperationCanceledException or InvalidOperationException)
                {
                    targets = []; // Stop probing when configuration cannot be refreshed.
                    AgentLog.Debug("Probe configuration unavailable; performance reporting remains active.");
                }
                refreshed = Stopwatch.GetTimestamp();
            }
            var due = targets.Where(t => !next.TryGetValue(t.Revision, out var at) || Stopwatch.GetElapsedTime(at).TotalSeconds >= t.IntervalSec).ToArray();
            foreach (var t in due) next[t.Revision] = Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (target, token) =>
            {
                var result = await MeasureAsync(target, token);
                using var send = CancellationTokenSource.CreateLinkedTokenSource(token);
                send.CancelAfter(TimeSpan.FromSeconds(10));
                await conn.SendAsync(AgentHubMethods.ProbeResult, result, send.Token);
            });
            await Task.Delay(1000, ct);
        }
    }
}
