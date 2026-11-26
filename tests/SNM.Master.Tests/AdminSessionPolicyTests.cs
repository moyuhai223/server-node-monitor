using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using SNM.Master.Auth;
using SNM.Master.Hubs;

namespace SNM.Master.Tests;

public class AdminSessionPolicyTests
{
    [Fact]
    public void RevocationIsMonotonicAccountScopedAndRemovesRecipientsBeforeAbort()
    {
        var state = new AdminSessionState();
        state.ObserveVersion(5, 1);
        state.ObserveVersion(8, 1);
        var aborted = 0;
        Assert.True(state.TryRegister("old", 5, 1, () => { Assert.DoesNotContain("old", state.Recipients()); aborted++; }));
        Assert.True(state.TryRegister("other", 8, 1, () => throw new InvalidOperationException()));
        Assert.Equal(2, state.ObserveVersion(5, 2));
        Assert.Equal(2, state.ObserveVersion(5, 1)); // late login/read or a delayed notification
        Assert.False(state.TryRegister("late", 5, 1, () => { })); // pre-authenticated handshake finishes after logout
        Assert.True(state.TryRegister("new", 5, 2, () => { }));
        Assert.Equal(1, aborted);
        Assert.True(state.IsCurrentConnection("other"));
        Assert.True(state.IsCurrentConnection("new"));
        state.Remove("new");
        Assert.Equal(new[] { "other" }, state.Recipients());
    }

    [Fact]
    public async Task FilterRunsNextOnlyWhileRegistered()
    {
        var state = new AdminSessionState();
        var filter = new AdminSessionFilter(state);
        var caller = new TestCaller();
        using var services = new ServiceCollection().BuildServiceProvider();
        using var hub = new TestHub();
        var context = new HubInvocationContext(caller, services, hub, typeof(TestHub).GetMethod(nameof(TestHub.Ping))!, []);
        var invoked = 0;
        ValueTask<object?> Next(HubInvocationContext _) => ValueTask.FromResult<object?>(++invoked);
        state.ObserveVersion(5, 0);
        Assert.True(state.TryRegister(caller.ConnectionId, 5, 0, caller.Abort));
        await filter.InvokeMethodAsync(context, Next);
        state.ObserveVersion(5, 1);
        await Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(context, Next));
        Assert.Equal(1, invoked);
        Assert.True(caller.Aborted);
    }

    [Theory]
    [InlineData("1", null)]
    [InlineData("1", "-1")]
    [InlineData("0", "0")]
    [InlineData(null, "0")]
    public void InvalidClaimsDoNotIdentifyASession(string? id, string? version)
    {
        var identity = new ClaimsIdentity();
        if (id != null) identity.AddClaim(new(ClaimTypes.NameIdentifier, id));
        if (version != null) identity.AddClaim(new(JwtTokenService.TokenVersionClaim, version));
        Assert.False(AdminSessionState.TryReadClaims(new ClaimsPrincipal(identity), out _, out _));
    }

    private sealed class TestHub : Hub { public void Ping() { } }
    private sealed class TestCaller : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId => "test";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }
}
