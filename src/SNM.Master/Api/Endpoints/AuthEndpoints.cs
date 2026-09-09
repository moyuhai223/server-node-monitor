using System.Text.RegularExpressions;
using SNM.Master.Auth;
using SNM.Master.Services;

namespace SNM.Master.Api.Endpoints;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record LogoutRequest(string? RefreshToken, bool? All);
public sealed record PasswordRequest(string? OldPassword, string? NewPassword, string? RefreshToken);
public sealed record ProfileRequest(string? NickName, string? Avatar, string? Email);

/// <summary>Auth + vue-naive-admin compatible user/menu endpoints (docs/API.md 2).</summary>
public static class AuthEndpoints
{
    /// <summary>Static menu tree consumed by the template's permission store (docs/API.md 2.7).</summary>
    public static readonly object[] MenuTree =
    [
        Menu(1, "Home", "总览大盘", null, "/", "i-fe:home", "/src/views/home/index.vue", keepAlive: true, order: 1, show: true, children: []),
        Menu(2, "Nodes", "节点管理", null, "/nodes", "i-fe:server", "/src/views/nodes/index.vue", keepAlive: true, order: 2, show: true, children:
        [
            Menu(3, "NodeDetail", "节点详情", 2, "/nodes/:id", "i-fe:activity", "/src/views/nodes/detail.vue", keepAlive: false, order: 1, show: false, children: []),
        ]),
        Menu(4, "Alerts", "告警记录", null, "/alerts", "i-fe:bell", "/src/views/alerts/index.vue", keepAlive: true, order: 3, show: true, children: []),
        Menu(5, "Settings", "系统设置", null, "/settings", "i-fe:settings", "/src/views/settings/index.vue", keepAlive: false, order: 4, show: true, children: []),
        Menu(6, "Profile", "个人资料", null, "/profile", "i-fe:user", "/src/views/profile/index.vue", keepAlive: false, order: 99, show: false, children: []),
    ];

    private static readonly Regex[] MenuPathPatterns =
    [
        new("^/$"), new("^/nodes$"), new("^/nodes/[^/]+$"), new("^/alerts$"), new("^/settings$"), new("^/profile$"),
    ];

    private static object Menu(int id, string code, string name, int? parentId, string path, string icon, string component, bool keepAlive, int order, bool show, object[] children) => new
    {
        id, code, name, type = "MENU", parentId, path, redirect = (string?)null, icon, component, layout = "", keepAlive, order, enable = true, show, children,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var open = app.MapGroup("/api/auth");

        open.MapPost("/login", async (LoginRequest req, HttpContext ctx, AdminUserService users, CancellationToken ct) =>
        {
            var pair = await users.LoginAsync(req.Username, req.Password, ctx.UserAgent(), ctx.ClientIp(), ct);
            return Results.Ok(ApiResponse.Ok(TokenBody(pair)));
        }).RequireRateLimiting("login");

        open.MapPost("/refresh", async (RefreshRequest req, HttpContext ctx, JwtTokenService jwt, CancellationToken ct) =>
        {
            var pair = await jwt.RefreshAsync(req.RefreshToken ?? "", ctx.UserAgent(), ctx.ClientIp(), ct)
                       ?? throw ApiException.Unauthorized(ApiCodes.RefreshInvalid, "登录已失效,请重新登录");
            return Results.Ok(ApiResponse.Ok(TokenBody(pair)));
        }).RequireRateLimiting("refresh");

        var auth = app.MapGroup("/api").RequireAuthorization("Admin");

        auth.MapPost("/auth/logout", async (LogoutRequest? req, HttpContext ctx, JwtTokenService jwt, AdminUserService users, CancellationToken ct) =>
        {
            var userId = ctx.UserId();
            if (req?.All == true) await users.LogoutAllAsync(userId, ct);
            else await jwt.RevokeAsync(userId, req?.RefreshToken, all: false, ct);
            return Results.Ok(ApiResponse.Ok(new { }));
        });

        auth.MapPost("/auth/password", async (PasswordRequest req, HttpContext ctx, AdminUserService users, CancellationToken ct) =>
        {
            await users.ChangePasswordAsync(ctx.UserId(), req.OldPassword, req.NewPassword, req.RefreshToken, ct);
            return Results.Ok(ApiResponse.Ok(new { }));
        });

        auth.MapGet("/user/detail", async (HttpContext ctx, AdminUserService users, CancellationToken ct) =>
        {
            var user = await users.GetAsync(ctx.UserId(), ct) ?? throw ApiException.Unauthorized(401, "用户不存在");
            return Results.Ok(ApiResponse.Ok(AdminUserService.ToDetail(user)));
        });

        auth.MapPatch("/user/profile/{id:int}", async (int id, ProfileRequest req, HttpContext ctx, AdminUserService users, CancellationToken ct) =>
        {
            if (id != ctx.UserId()) throw ApiException.Forbidden("只能修改自己的资料");
            var user = await users.UpdateProfileAsync(id, req.NickName, req.Avatar, req.Email, ct);
            return Results.Ok(ApiResponse.Ok(AdminUserService.ToDetail(user)));
        });

        auth.MapGet("/role/permissions/tree", () => Results.Ok(ApiResponse.Ok(MenuTree)));

        auth.MapGet("/permission/menu/validate", (string? path) =>
        {
            path = string.IsNullOrEmpty(path) ? "/" : path.Split('?')[0];
            var ok = MenuPathPatterns.Any(p => p.IsMatch(path));
            return Results.Ok(ApiResponse.Ok(ok));
        });
    }

    private static object TokenBody(TokenPair pair) => new { accessToken = pair.AccessToken, refreshToken = pair.RefreshToken, tokenType = "Bearer", expiresIn = pair.ExpiresIn };
}
