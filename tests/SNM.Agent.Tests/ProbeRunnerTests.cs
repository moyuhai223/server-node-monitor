using System.Net;
using System.Net.Sockets;
using SNM.Agent.Net;
using SNM.Contracts.Dtos;

namespace SNM.Agent.Tests;

public class ProbeRunnerTests
{
    private static ProbeTargetDto Target(int port) => new() { Id = 1, Revision = "v1", Kind = 1, Address = "127.0.0.1", Port = (ushort)port, IntervalSec = 10, TimeoutMs = 1000 };

    [Fact]
    public async Task Tcp_success_failure_and_cancellation_are_distinct()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var result = await ProbeRunner.MeasureAsync(Target(port), CancellationToken.None);
            Assert.Equal(0, result.Status); Assert.InRange(result.Microseconds, 0, 1_000_000);
            using var accepted = await listener.AcceptTcpClientAsync();
        }
        finally { listener.Stop(); }
        var failed = await ProbeRunner.MeasureAsync(Target(port), CancellationToken.None);
        Assert.NotEqual(0, failed.Status); Assert.Equal(-1, failed.Microseconds);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProbeRunner.MeasureAsync(Target(port), cancelled.Token));
    }

    [Fact]
    public async Task Invalid_configuration_never_probes()
    {
        var target = Target(443); target.Kind = 255;
        var result = await ProbeRunner.MeasureAsync(target, CancellationToken.None);
        Assert.Equal(2, result.Status); Assert.Equal(-1, result.Microseconds);
    }
}
