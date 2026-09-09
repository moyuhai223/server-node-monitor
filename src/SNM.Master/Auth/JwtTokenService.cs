using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Options;
using SNM.Master.Services;

namespace SNM.Master.Auth;

public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>HS256 access tokens + rotating refresh tokens (docs/API.md 1.4, docs/DATA.md 2.14).</summary>
public sealed class JwtTokenService(IOptions<SnmOptions> options, SettingsService settings, IDbContextFactory<SnmDbContext> dbFactory, ILogger<JwtTokenService> logger)
{
    public const string Issuer = "snm-master";
    public const string Audience = "snm-admin";
    public const string TokenVersionClaim = "tv";

    private readonly JsonWebTokenHandler _handler = new();
    private SymmetricSecurityKey? _key;

    public SymmetricSecurityKey SigningKey => _key ??= new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.ResolveJwtSecret(options.Value)));

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = true, ValidIssuer = Issuer,
        ValidateAudience = true, ValidAudience = Audience,
        ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
        ValidateIssuerSigningKey = true, IssuerSigningKey = SigningKey,
        NameClaimType = ClaimTypes.Name, RoleClaimType = ClaimTypes.Role,
    };

    public string CreateAccessToken(AdminUser user, out int expiresInSeconds)
    {
        var minutes = Math.Clamp(settings.Snapshot.AccessTokenMinutes, 5, 1440);
        expiresInSeconds = minutes * 60;
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(minutes),
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim(TokenVersionClaim, user.TokenVersion.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return _handler.CreateToken(descriptor);
    }

    /// <summary>Issues a new access + refresh token pair and stores the refresh token hash.</summary>
    public async Task<TokenPair> IssueAsync(AdminUser user, string? userAgent, string? ip, CancellationToken ct)
    {
        var access = CreateAccessToken(user, out var expiresIn);
        var refresh = Tokens.RandomBase64Url();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Tokens.Sha256Base64(refresh),
            ExpiresAt = DateTime.UtcNow.AddDays(Math.Clamp(settings.Snapshot.RefreshTokenDays, 1, 365)),
            CreatedAt = DateTime.UtcNow,
            UserAgent = Truncate(userAgent, 256),
            Ip = Truncate(ip, 45),
        });
        await db.SaveChangesAsync(ct);
        return new TokenPair(access, refresh, expiresIn);
    }

    /// <summary>Rotates a refresh token. Returns null when invalid/expired/revoked (a replayed token revokes the whole family).</summary>
    public async Task<TokenPair?> RefreshAsync(string refreshToken, string? userAgent, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 128) return null;
        var hash = Tokens.Sha256Base64(refreshToken);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (row is null) return null;
        var now = DateTime.UtcNow;
        if (row.RevokedAt is not null)
        {
            // Replay of a rotated token: revoke everything for this user.
            logger.LogWarning("Refresh token replay detected for user {UserId} from {Ip}; revoking all sessions", row.UserId, ip);
            await db.RefreshTokens.Where(x => x.UserId == row.UserId && x.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
            return null;
        }
        if (row.ExpiresAt <= now) return null;
        var user = await db.AdminUsers.FirstOrDefaultAsync(x => x.Id == row.UserId, ct);
        if (user is null) return null;

        var newRefresh = Tokens.RandomBase64Url();
        var newHash = Tokens.Sha256Base64(newRefresh);
        row.RevokedAt = now;
        row.ReplacedByHash = newHash;
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id, TokenHash = newHash,
            ExpiresAt = now.AddDays(Math.Clamp(settings.Snapshot.RefreshTokenDays, 1, 365)),
            CreatedAt = now, UserAgent = Truncate(userAgent, 256), Ip = Truncate(ip, 45),
        });
        await db.SaveChangesAsync(ct);
        var access = CreateAccessToken(user, out var expiresIn);
        return new TokenPair(access, newRefresh, expiresIn);
    }

    public async Task RevokeAsync(int userId, string? refreshToken, bool all, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        if (all || string.IsNullOrWhiteSpace(refreshToken))
        {
            await db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
            return;
        }
        var hash = Tokens.Sha256Base64(refreshToken);
        await db.RefreshTokens.Where(x => x.UserId == userId && x.TokenHash == hash && x.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    /// <summary>Revokes all refresh tokens of the user except the one supplied (password change keeps the current session).</summary>
    public async Task RevokeOthersAsync(int userId, string? keepRefreshToken, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var keepHash = string.IsNullOrWhiteSpace(keepRefreshToken) ? "" : Tokens.Sha256Base64(keepRefreshToken);
        await db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null && x.TokenHash != keepHash).ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
