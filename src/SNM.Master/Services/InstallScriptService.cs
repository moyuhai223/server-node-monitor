using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SNM.Master.Api;
using SNM.Master.Auth;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Runtime;

namespace SNM.Master.Services;

public sealed record InstallScriptInfo(string Os, string Token, DateTime ExpiresAt, string Url, string OneLiner, string UninstallOneLiner, string Script);

/// <summary>One-click installer generation (docs/API.md 4.10-4.11, docs/DEPLOY.md 4): short-lived tokens + rendered templates.</summary>
public sealed class InstallScriptService(IDbContextFactory<SnmDbContext> dbFactory, NodeRegistry registry, SettingsService settings, ILogger<InstallScriptService> logger)
{
    private static readonly Regex UrlRule = new(@"^https?://[A-Za-z0-9.\-:/_\[\]%~]+$", RegexOptions.Compiled);
    private static readonly Regex KeyRule = new(@"^snmk_[A-Za-z0-9_-]{43}$", RegexOptions.Compiled);
    private static readonly string ShTemplate = LoadResource("install-agent.sh");
    private static readonly string Ps1Template = LoadResource("install-agent.ps1");

    public static string MasterVersion { get; } =
        (typeof(InstallScriptService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0").Split('+')[0];

    private static string LoadResource(string name)
    {
        using var stream = typeof(InstallScriptService).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"embedded resource {name} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    public static string NormalizeOs(string? os) => string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase) ? "windows" : "linux";

    /// <summary>Base URL for install links: site.publicBaseUrl, else derived from the (forwarded) request.</summary>
    public string ResolveBaseUrl(HttpRequest request)
    {
        var configured = settings.Snapshot.PublicBaseUrl;
        if (configured.Length > 0) return configured.TrimEnd('/');
        var proto = request.Headers["X-Forwarded-Proto"].FirstOrDefault()?.Split(',')[0].Trim();
        var host = request.Headers["X-Forwarded-Host"].FirstOrDefault()?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(proto)) proto = request.Scheme;
        if (string.IsNullOrEmpty(host)) host = request.Host.Value;
        if (string.IsNullOrEmpty(host)) throw ApiException.Business(422, ApiCodes.StateNotAllowed, "无法推导公开地址,请先在系统设置中填写 site.publicBaseUrl");
        var hostOnly = host.Split(':')[0].Trim('[', ']');
        if (hostOnly is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" || (IPAddress.TryParse(hostOnly, out var ip) && !GeoIpService.IsPublic(ip)))
            throw ApiException.Business(422, ApiCodes.StateNotAllowed, "当前访问地址为本机/内网地址,请先在系统设置中填写 site.publicBaseUrl(探针需要能访问的公开地址)");
        return $"{proto}://{host}";
    }

    public async Task<InstallScriptInfo> IssueAsync(int nodeId, string? os, bool renew, HttpRequest request, CancellationToken ct)
    {
        var node = registry.Get(nodeId) ?? throw ApiException.NodeNotFound();
        var baseUrl = ResolveBaseUrl(request);
        if (settings.Snapshot.ReleaseBaseUrl.Length == 0) throw ApiException.Business(422, ApiCodes.StateNotAllowed, "请先在系统设置中填写探针下载地址 agent.releaseBaseUrl");
        os = NormalizeOs(os);
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var token = renew ? null : await db.InstallTokens.Where(x => x.NodeId == nodeId && x.ExpiresAt > now.AddMinutes(30)).OrderByDescending(x => x.ExpiresAt).FirstOrDefaultAsync(ct);
        if (token is null)
        {
            token = new InstallToken
            {
                NodeId = nodeId,
                Token = Tokens.RandomBase64Url(),
                CreatedAt = now,
                ExpiresAt = now.AddHours(Math.Clamp(settings.Snapshot.InstallTokenTtlHours, 1, 168)),
            };
            db.InstallTokens.Add(token);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Issued install token for node {NodeId} ({Name}), valid until {Exp:u}", nodeId, node.Meta.PublicName, token.ExpiresAt);
        }

        var url = $"{baseUrl}/install/{token.Token}";
        var script = Render(os, node.Meta, baseUrl);
        var (oneLiner, uninstall) = os == "windows"
            ? ($"irm \"{url}?os=windows\" | iex", $"$env:SNM_UNINSTALL = \"1\"; irm \"{url}?os=windows\" | iex")
            : ($"curl -fsSL {url} | sudo bash", $"curl -fsSL {url} | sudo bash -s -- uninstall");
        return new InstallScriptInfo(os, token.Token, token.ExpiresAt, url, oneLiner, uninstall, script);
    }

    /// <summary>Public endpoint: returns the rendered script for a valid token, null when invalid/expired.</summary>
    public async Task<string?> RenderByTokenAsync(string token, string? os, string? remoteIp, HttpRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.InstallTokens.FirstOrDefaultAsync(x => x.Token == token, ct);
        var now = DateTime.UtcNow;
        if (row is null || row.ExpiresAt <= now) return null;
        var node = registry.Get(row.NodeId);
        if (node is null) return null;
        row.UsedCount++;
        row.LastUsedAt = now;
        row.LastUsedIp = remoteIp is { Length: <= 45 } ? remoteIp : null;
        await db.SaveChangesAsync(ct);
        string baseUrl;
        try { baseUrl = ResolveBaseUrl(request); }
        catch (ApiException) { baseUrl = settings.Snapshot.PublicBaseUrl.Length > 0 ? settings.Snapshot.PublicBaseUrl : $"{request.Scheme}://{request.Host}"; }
        logger.LogInformation("Install script for node {NodeId} ({Name}) served to {Ip}", node.Id, node.Meta.PublicName, remoteIp);
        return Render(NormalizeOs(os), node.Meta, baseUrl);
    }

    public string Render(string os, Node node, string serverUrl)
    {
        var release = settings.Snapshot.ReleaseBaseUrl;
        if (!UrlRule.IsMatch(serverUrl)) throw ApiException.Business(422, ApiCodes.StateNotAllowed, "公开地址含有非法字符");
        if (release.Length > 0 && !UrlRule.IsMatch(release)) throw ApiException.Business(422, ApiCodes.StateNotAllowed, "探针下载地址含有非法字符");
        if (!KeyRule.IsMatch(node.AgentKey)) throw new InvalidOperationException("agent key has an unexpected format");
        var template = os == "windows" ? Ps1Template : ShTemplate;
        var safeName = Regex.Replace(node.PublicName, "[^\\p{L}\\p{N} ._\\-]", "_");
        return template
            .Replace("{{SERVER_URL}}", serverUrl)
            .Replace("{{AGENT_KEY}}", node.AgentKey)
            .Replace("{{RELEASE_BASE_URL}}", release)
            .Replace("{{NODE_NAME}}", safeName)
            .Replace("{{GENERATED_AT}}", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
            .Replace("{{MASTER_VERSION}}", MasterVersion);
    }
}
