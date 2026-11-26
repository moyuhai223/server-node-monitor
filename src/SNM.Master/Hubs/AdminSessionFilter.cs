using Microsoft.AspNetCore.SignalR;
using SNM.Master.Auth;

namespace SNM.Master.Hubs;

/// <summary>Registration, invocation checks and cleanup apply only to the admin hub.</summary>
public sealed class AdminSessionFilter(AdminSessionState sessions) : IHubFilter
{
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var caller = context.Context;
        // JWT validation primed the version; check and enroll atomically in case logout raced the handshake.
        if (!AdminSessionState.TryReadClaims(caller.User, out var id, out var version) ||
            !sessions.TryRegister(caller.ConnectionId, id, version, caller.Abort))
            throw Revoked(caller);
        try { await next(context); }
        catch { sessions.Remove(caller.ConnectionId); throw; }
    }

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next) =>
        sessions.IsCurrentConnection(context.Context.ConnectionId) ? next(context) : throw Revoked(context.Context);

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        sessions.Remove(context.Context.ConnectionId);
        return next(context, exception);
    }

    private static HubException Revoked(HubCallerContext caller)
    {
        caller.Abort();
        return new HubException("Admin session is no longer valid.");
    }
}

/// <summary>Do not broadcast to connections that have yet to register or whose socket is still closing.</summary>
public sealed class AdminLiveClients(IHubContext<AdminHub> hub, AdminSessionState sessions)
{
    public IClientProxy Current => hub.Clients.Clients(sessions.Recipients());
}
