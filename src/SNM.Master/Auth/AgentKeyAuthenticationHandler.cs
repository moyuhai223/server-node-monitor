using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SNM.Contracts;
using SNM.Master.Runtime;

namespace SNM.Master.Auth;

/// <summary>Authenticates agents by the X-SNM-Agent-Key header (or access_token query for debugging) against the node registry.</summary>
public sealed class AgentKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggerFactory, UrlEncoder encoder, NodeRegistry registry)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "AgentKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? key = Request.Headers[ProtocolConstants.AgentKeyHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) key = Request.Query[ProtocolConstants.AgentKeyQueryParam].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return Task.FromResult(AuthenticateResult.NoResult());

        if (!Tokens.IsAgentKeyFormat(key))
        {
            Logger.LogWarning("Agent key with invalid format from {Ip}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("invalid agent key format"));
        }
        if (!registry.TryGetByKey(key, out var node))
        {
            Logger.LogWarning("Unknown agent key from {Ip}", Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("unknown agent key"));
        }
        if (!node.Meta.Enabled)
        {
            Logger.LogWarning("Disabled node {NodeId} tried to connect from {Ip}", node.Id, Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("node disabled"));
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, node.Id.ToString()),
            new Claim(ClaimTypes.Name, node.Meta.PublicName),
            new Claim(ClaimTypes.Role, "agent"),
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
