using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using SNM.Contracts;
using SNM.Contracts.Dtos;

namespace SNM.Master.Tests;

public sealed class AdminHubSessionTests(MasterFactory factory) : IClassFixture<MasterFactory>
{
    private HubConnection Connect(HttpClient session)
    {
        // A persistent socket, not long polling: disconnect must come from the server.
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Authorization = session.DefaultRequestHeaders.Authorization!.ToString();
        return new HubConnectionBuilder().WithUrl(new Uri(factory.Server.BaseAddress, HubRoutes.Admin), o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            o.SkipNegotiation = true;
            o.WebSocketFactory = async (context, ct) => await ws.ConnectAsync(context.Uri, ct);
        }).Build();
    }

    private static async Task Ready(HubConnection connection)
    {
        var initial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = connection.On<AdminSnapshotDto>(AdminHubMethods.Snapshot, _ => initial.TrySetResult());
        await connection.StartAsync();
        await initial.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task LogoutAllClosesBothOpenSocketsAndAllowsFreshLogin()
    {
        using var client = await factory.LoginAsync();
        await using var first = Connect(client);
        await using var second = Connect(client);
        var firstClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Closed += _ => { firstClosed.TrySetResult(); return Task.CompletedTask; };
        second.Closed += _ => { secondClosed.TrySetResult(); return Task.CompletedTask; };
        await Task.WhenAll(Ready(first), Ready(second));
        using var response = await client.PostAsJsonAsync("/api/auth/logout", new { all = true });
        response.EnsureSuccessStatusCode();
        await Task.WhenAll(firstClosed.Task, secondClosed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        using var fresh = await factory.LoginAsync();
        await using var next = Connect(fresh);
        await Ready(next);
        Assert.Equal(HubConnectionState.Connected, next.State);
    }
}
