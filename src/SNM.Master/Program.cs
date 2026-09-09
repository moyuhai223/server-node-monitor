using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using MessagePack;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using SNM.Contracts;
using SNM.Master.Alerting;
using SNM.Master.Api;
using SNM.Master.Api.Endpoints;
using SNM.Master.Auth;
using SNM.Master.Background;
using SNM.Master.Config;
using SNM.Master.Data;
using SNM.Master.Hubs;
using SNM.Master.Options;
using SNM.Master.Runtime;
using SNM.Master.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- 1. configuration: appsettings -> appsettings.{Env} -> env (Snm__*) -> SNM_* aliases -> command line (docs/DESIGN.md 5-6)
builder.Configuration.AddInMemoryCollection(EnvAlias.Read(Environment.GetEnvironmentVariable));
builder.Configuration.AddCommandLine(args);
var snm = builder.Configuration.GetSection(SnmOptions.Section).Get<SnmOptions>() ?? new SnmOptions();
builder.Services.Configure<SnmOptions>(builder.Configuration.GetSection(SnmOptions.Section));

// ---- 2. options validation + data directories
var paths = new DataPaths(Path.IsPathRooted(snm.DataDir) ? snm.DataDir : Path.Combine(builder.Environment.ContentRootPath, snm.DataDir));
Directory.CreateDirectory(paths.DataDir);
Directory.CreateDirectory(paths.GeoIpDir);
Directory.CreateDirectory(paths.BackupsDir);
var listenUrls = snm.Listen.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (listenUrls.Length == 0) throw new InvalidOperationException("Snm:Listen must contain at least one URL");
foreach (var url in listenUrls)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new InvalidOperationException($"Snm:Listen contains an invalid URL: {url}");
}
builder.WebHost.UseUrls(listenUrls);

// ---- 3. logging
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
    o.UseUtcTimestamp = true;
});
builder.Host.UseSystemd();

// ---- 4. services
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<StartupInitializer>();
builder.Services.AddHostedService<StartupHostedService>();
builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

builder.Services.AddDbContextFactory<SnmDbContext>(o => o
    .UseSqlite($"Data Source={paths.DbPath}")
    .AddInterceptors(new SqlitePragmaInterceptor()));
builder.Services.AddSingleton<DbWriteLock>();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = Math.Max(1, snm.ForwardLimit);
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    foreach (var p in snm.KnownProxies)
    {
        if (IPAddress.TryParse(p, out var ip)) o.KnownProxies.Add(ip);
    }
    foreach (var n in snm.KnownNetworks)
    {
        if (System.Net.IPNetwork.TryParse(n, out var net)) o.KnownIPNetworks.Add(net);
    }
});

builder.Services.AddHttpClient("geoip", c =>
{
    c.Timeout = TimeSpan.FromSeconds(120);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"snm-master/{AppInfo.ShortVersion}");
});
builder.Services.AddHttpClient("notify", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"snm-master/{AppInfo.ShortVersion}");
});

// core singletons (memory is the truth, see docs/DESIGN.md 3)
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddSingleton<GeoIpService>();
builder.Services.AddSingleton<AgentConnectionTracker>();
builder.Services.AddSingleton<NodeIngestService>();
builder.Services.AddSingleton<LiveSnapshotBuilder>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<AdminUserService>();
builder.Services.AddSingleton<NodeService>();
builder.Services.AddSingleton<InstallScriptService>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<AlertQueryService>();
builder.Services.AddSingleton<HubStats>();

// background services (singletons so that services can talk to them)
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatcher>());
builder.Services.AddSingleton<AlertEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertEngine>());
builder.Services.AddSingleton<MinuteFlushService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MinuteFlushService>());
builder.Services.AddHostedService<RealtimeBroadcaster>();
builder.Services.AddHostedService<HourlyRollupService>();
builder.Services.AddHostedService<DailyRollupService>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<GeoIpRefreshService>();

// authentication: JWT for admins (REST + /hubs/admin), AgentKey for probes (/hubs/agent)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, AgentKeyAuthenticationHandler>(AgentKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<JwtTokenService, AdminUserService>((o, jwt, users) =>
{
    o.TokenValidationParameters = jwt.ValidationParameters;
    o.MapInboundClaims = false;
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            // browsers cannot set headers on WebSockets: SignalR appends ?access_token=
            if (ctx.Request.Path.StartsWithSegments(HubRoutes.Admin) && ctx.Request.Query.TryGetValue("access_token", out var token) && !string.IsNullOrEmpty(token))
                ctx.Token = token;
            return Task.CompletedTask;
        },
        OnTokenValidated = async ctx =>
        {
            var sub = ctx.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var tv = ctx.Principal?.FindFirst(JwtTokenService.TokenVersionClaim)?.Value;
            if (!int.TryParse(sub, out var userId) || !int.TryParse(tv, out var version) || await users.GetTokenVersionAsync(userId, ctx.HttpContext.RequestAborted) != version)
                ctx.Fail("token version mismatch");
        },
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().RequireRole("admin"));

// rate limiting per client IP (docs/API.md 1.4)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) => await ApiExceptionMiddleware.WriteAsync(ctx.HttpContext, 429, 429, "请求过于频繁,请稍后再试", null);
    static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    static RateLimitPartition<string> Fixed(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Request.Path.StartsWithSegments("/api") ? Fixed("api:" + Ip(ctx), 600) : RateLimitPartition.GetNoLimiter("none"));
    o.AddPolicy("login", ctx => Fixed("login:" + Ip(ctx), 10));
    o.AddPolicy("refresh", ctx => Fixed("refresh:" + Ip(ctx), 30));
    o.AddPolicy("install", ctx => Fixed("install:" + Ip(ctx), 30));
});

// SignalR + MessagePack on all three hubs (docs/PROTOCOL.md 4.1, 5.1)
builder.Services.AddSignalR(o =>
    {
        o.MaximumReceiveMessageSize = ProtocolConstants.MaxHubMessageBytes;
        o.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
        o.KeepAliveInterval = TimeSpan.FromSeconds(15);
        o.HandshakeTimeout = TimeSpan.FromSeconds(15);
        o.MaximumParallelInvocationsPerClient = 1;
        o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    })
    .AddMessagePackProtocol(o =>
    {
        // StandardResolver honours [MessagePackObject]/[Key]; UntrustedData hardens against hostile agents.
        o.SerializerOptions = MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
    })
    .AddHubOptions<PublicHub>(o => o.MaximumReceiveMessageSize = 1024);

var app = builder.Build();

// ---- 5. pipeline (docs/DESIGN.md 5)
app.UseForwardedHeaders();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseStatusCodePages(async ctx =>
{
    var http = ctx.HttpContext;
    if (!http.Request.Path.StartsWithSegments("/api")) return;
    var status = http.Response.StatusCode;
    var message = status switch
    {
        401 => "未登录或登录已过期",
        403 => "无权限",
        404 => "接口不存在",
        405 => "方法不允许",
        _ => "请求失败",
    };
    await ApiExceptionMiddleware.WriteAsync(http, status, status, message, null);
});
app.UseRateLimiter();

var devPublic = snm.Dev.PublicSourceDir.Length > 0 ? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, snm.Dev.PublicSourceDir)) : null;
if (devPublic is not null && Directory.Exists(devPublic) && File.Exists(Path.Combine(devPublic, "index.html")))
{
    var provider = new PhysicalFileProvider(devPublic);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider, OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
    app.Logger.LogInformation("Serving the public dashboard from {Dir} (development)", devPublic);
}
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<AgentHub>(HubRoutes.Agent);
app.MapHub<PublicHub>(HubRoutes.Public);
app.MapHub<AdminHub>(HubRoutes.Admin);

AuthEndpoints.Map(app);
NodesEndpoints.Map(app);
AlertsEndpoints.Map(app);
SettingsEndpoints.Map(app);
SystemEndpoints.Map(app);

var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (File.Exists(Path.Combine(webRoot, "admin", "index.html")))
    app.MapFallbackToFile("/admin/{*path}", "admin/index.html");
else
    app.MapFallback("/admin/{*path}", () => Results.Text("管理后台尚未构建:请运行 scripts/build-web.sh 后重启 Master。", "text/plain; charset=utf-8", statusCode: 404));
app.MapFallback("/api/{**path}", () => Results.Json(ApiResponse.Fail(404, "接口不存在"), ApiJson.Options, statusCode: 404));
app.MapFallback("/", () => Results.Text("Server Node Monitor: public dashboard not built yet (run scripts/build-web.sh).", "text/plain; charset=utf-8", statusCode: 404));

// ---- 6. blocking initialisation: migrate, seed, load memory state (idempotent; also a hosted service for test hosts)
await app.Services.GetRequiredService<StartupInitializer>().EnsureInitializedAsync(app.Lifetime.ApplicationStopping);
{
    var settings = app.Services.GetRequiredService<SettingsService>().Snapshot;
    app.Logger.LogInformation("Server Node Monitor master {Version} starting: listen={Listen} dataDir={DataDir} db={DbSize:N0} bytes nodes={Nodes} timeZone={Tz} publicBaseUrl={PublicBaseUrl}",
        AppInfo.Version, snm.Listen, paths.DataDir, paths.DbSizeBytes(), app.Services.GetRequiredService<NodeRegistry>().Count, settings.TimeZoneId,
        settings.PublicBaseUrl.Length > 0 ? settings.PublicBaseUrl : "(未配置 - 安装脚本需要 site.publicBaseUrl)");
}

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
