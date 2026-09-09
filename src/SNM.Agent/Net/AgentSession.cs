using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SNM.Agent.Cli;
using SNM.Agent.Logging;
using SNM.Agent.Sampling;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Contracts.Protocol;

namespace SNM.Agent.Net;

internal static class HubConnectionBuilderExtensions
{
    /// <summary>Replaces the default JsonHubProtocol with the reflection-free MessagePack protocol (docs/PROTOCOL.md A.10).</summary>
    public static IHubConnectionBuilder UseSnmMessagePackProtocol(this IHubConnectionBuilder builder)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<IHubProtocol, SnmMessagePackHubProtocol>());
        return builder;
    }
}

/// <summary>Connect -> register -> heartbeat/status loops -> reconnect forever (docs/PROTOCOL.md 7.1, 7.8, 7.9).</summary>
internal sealed class AgentSession(CliOptions opts, SampleBuilder sampler)
{
    private readonly Backoff _backoff = new();
    private readonly AgentLoggerProvider _loggerProvider = new();

    private volatile ushort _intervalMs = opts.IntervalMs;
    private volatile ushort _statusIntervalSec = ProtocolConstants.DefaultStatusIntervalSec;

    public async Task RunForeverAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var kind = FailureKind.Network;
            string? lastError = null;
            HubConnection? conn = null;
            try
            {
                conn = BuildConnection();
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                conn.Closed += ex => { closed.TrySetResult(ex); return Task.CompletedTask; };
                conn.On<AgentConfigDto>(AgentHubMethods.Configure, cfg => ApplyConfig(cfg, "configure"));

                using (var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    startCts.CancelAfter(TimeSpan.FromSeconds(20));
                    await conn.StartAsync(startCts.Token);
                }
                AgentLog.Info($"connected to {opts.Server} (connectionId={conn.ConnectionId ?? "?"})");

                sampler.ResetBaseline();
                var register = sampler.BuildRegister(_intervalMs);
                AgentConfigDto cfg;
                using (var regCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    regCts.CancelAfter(TimeSpan.FromSeconds(15));
                    cfg = await conn.InvokeAsync<AgentConfigDto>(AgentHubMethods.Register, register, regCts.Token);
                }
                ApplyConfig(cfg, "register");
                _backoff.Reset();

                using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hb = HeartbeatLoopAsync(conn, loopCts.Token);
                var st = StatusLoopAsync(conn, loopCts.Token);
                var finished = await Task.WhenAny(hb, st, closed.Task);
                loopCts.Cancel();
                if (finished == closed.Task)
                {
                    var ex = await closed.Task;
                    lastError = ex is null ? "connection closed by server" : ex.Message;
                }
                else
                {
                    try { await finished; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { lastError = ex.Message; }
                    lastError ??= "loop ended";
                }
                try { await Task.WhenAll(hb, st).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { /* cancelled */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
                if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    kind = FailureKind.AuthRejected;
                    AgentLog.Warn($"master rejected the agent key ({(int)ex.StatusCode.Value}); check --key / node enabled state");
                }
            }
            catch (HubException ex) when (ex.Message.Contains("protocol version", StringComparison.OrdinalIgnoreCase))
            {
                lastError = ex.Message;
                kind = FailureKind.ProtocolMismatch;
                AgentLog.Error($"protocol mismatch: {ex.Message}; upgrade the agent");
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            finally
            {
                if (conn is not null)
                {
                    try { await conn.DisposeAsync(); } catch { /* ignore */ }
                }
            }

            if (ct.IsCancellationRequested) break;
            var delay = _backoff.Next(kind);
            AgentLog.Info($"disconnected ({lastError ?? "unknown"}); reconnecting in {delay.TotalSeconds:F1}s (attempt {_backoff.Attempt})");
            if (!_backoff.ShouldLog) AgentLog.Debug("(further reconnect attempts are logged once every 10 tries)");
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private HubConnection BuildConnection()
    {
        var proxy = ProxyParser.Parse(opts.Proxy, out var forceDirect);
        var transports = opts.Transport switch
        {
            "websockets" => HttpTransportType.WebSockets,
            "longpolling" => HttpTransportType.LongPolling,
            _ => HttpTransportType.WebSockets | HttpTransportType.LongPolling,
        };
        var builder = new HubConnectionBuilder()
            .WithUrl(opts.Server + HubRoutes.Agent, transports, o =>
            {
                o.Headers[ProtocolConstants.AgentKeyHeader] = opts.Key;
                o.Headers["X-SNM-Agent"] = Program.UserAgent;
                o.SkipNegotiation = opts.Transport == "websockets";
                o.CloseTimeout = TimeSpan.FromSeconds(5);
                if (proxy is not null) o.Proxy = proxy;
                o.WebSocketConfiguration = ws =>
                {
                    ws.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    if (forceDirect) ws.Proxy = null;
                    if (opts.Insecure) ws.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                };
                o.HttpMessageHandlerFactory = h =>
                {
                    if (h is HttpClientHandler hch)
                    {
                        if (forceDirect) hch.UseProxy = false;
                        if (opts.Insecure) hch.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                    }
                    return h;
                };
            })
            .ConfigureLogging(lb =>
            {
                lb.SetMinimumLevel(opts.LogLevel == LogLevel.Trace ? LogLevel.Trace : LogLevel.Warning);
                lb.AddProvider(_loggerProvider);
            })
            .UseSnmMessagePackProtocol();
        var conn = builder.Build();
        conn.ServerTimeout = TimeSpan.FromSeconds(45);
        conn.KeepAliveInterval = TimeSpan.FromSeconds(15);
        conn.HandshakeTimeout = TimeSpan.FromSeconds(15);
        return conn;
    }

    private void ApplyConfig(AgentConfigDto cfg, string source)
    {
        var interval = (ushort)Math.Clamp(cfg.IntervalMs == 0 ? opts.IntervalMs : cfg.IntervalMs, ProtocolConstants.MinIntervalMs, ProtocolConstants.MaxIntervalMs);
        var status = (ushort)Math.Clamp(cfg.StatusIntervalSec == 0 ? ProtocolConstants.DefaultStatusIntervalSec : cfg.StatusIntervalSec, ProtocolConstants.MinStatusIntervalSec, ProtocolConstants.MaxStatusIntervalSec);
        var changed = interval != _intervalMs || status != _statusIntervalSec;
        _intervalMs = interval;
        _statusIntervalSec = status;
        if (source == "register" || changed) AgentLog.Info($"{(source == "register" ? "registered" : "configured")}: interval={interval}ms statusInterval={status}s");
    }

    private async Task HeartbeatLoopAsync(HubConnection conn, CancellationToken ct)
    {
        uint seq = 0;
        var lastSample = Stopwatch.GetTimestamp();
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_intervalMs, ct);
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = first ? 0u : (uint)Math.Clamp(Stopwatch.GetElapsedTime(lastSample, now).TotalMilliseconds, 0, uint.MaxValue);
            lastSample = now;
            first = false;
            seq++;
            var hb = sampler.BuildHeartbeat(seq, elapsedMs);
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(10));
            await conn.SendAsync(AgentHubMethods.Heartbeat, hb, sendCts.Token);
            if (AgentLog.IsEnabled(LogLevel.Trace)) AgentLog.Trace($"hb seq={seq} cpu={hb.Cpu} mem={hb.MemUsedMb}MB rx={hb.NetRxBytes} tx={hb.NetTxBytes} elapsed={elapsedMs}ms");
        }
    }

    private async Task StatusLoopAsync(HubConnection conn, CancellationToken ct)
    {
        var lastSent = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            var due = Stopwatch.GetElapsedTime(lastSent).TotalSeconds >= _statusIntervalSec;
            var status = sampler.BuildStatusIfNeeded(due);
            if (status is null) continue;
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sendCts.CancelAfter(TimeSpan.FromSeconds(10));
            await conn.SendAsync(AgentHubMethods.ReportStatus, status, sendCts.Token);
            lastSent = Stopwatch.GetTimestamp();
            AgentLog.Debug($"status sent: ips={status.Ips?.Length ?? 0} disks={status.Disks?.Length ?? 0} procs={status.ProcCount}");
        }
    }
}
