using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SNM.Master.Api;
using SNM.Master.Auth;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Options;

namespace SNM.Master.Services;

/// <summary>Single-admin account: seeding, login with lockout, password/profile changes, token-version cache.</summary>
public sealed class AdminUserService(IDbContextFactory<SnmDbContext> dbFactory, JwtTokenService jwt, SettingsService settings,
    IOptions<SnmOptions> options, ILogger<AdminUserService> logger)
{
    private readonly ConcurrentDictionary<int, int> _tokenVersions = new();

    public async Task SeedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.AdminUsers.AnyAsync(ct)) return;
        var opts = options.Value.Admin;
        var username = string.IsNullOrWhiteSpace(opts.User) ? "admin" : opts.User.Trim();
        var password = opts.Password;
        var generated = false;
        if (string.IsNullOrWhiteSpace(password))
        {
            password = PasswordHasher.GeneratePassword();
            generated = true;
        }
        db.AdminUsers.Add(new AdminUser
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            NickName = "管理员",
            CreatedAt = DateTime.UtcNow,
            PasswordChangedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        if (generated)
            logger.LogWarning("Created admin user '{User}' with a generated password: {Password}  (change it after the first login)", username, password);
        else
            logger.LogInformation("Created admin user '{User}' from configuration", username);
    }

    /// <summary>Token version of the user (cached; -1 when the user does not exist).</summary>
    public async ValueTask<int> GetTokenVersionAsync(int userId, CancellationToken ct)
    {
        if (_tokenVersions.TryGetValue(userId, out var v)) return v;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AdminUsers.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.TokenVersion }).FirstOrDefaultAsync(ct);
        v = row?.TokenVersion ?? -1;
        _tokenVersions[userId] = v;
        return v;
    }

    public async Task<TokenPair> LoginAsync(string? username, string? password, string? userAgent, string? ip, CancellationToken ct)
    {
        username = (username ?? "").Trim();
        password ??= "";
        if (username.Length is 0 or > 32 || password.Length is 0 or > 128) throw ApiException.Business(400, ApiCodes.LoginFailed, "用户名或密码错误");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == username, ct);
        var now = DateTime.UtcNow;
        var s = settings.Snapshot;
        if (user is null)
        {
            // equalize timing with a real hash check
            PasswordHasher.Verify(password, PasswordHasher.Hash("x"));
            logger.LogWarning("Login failed for unknown user '{User}' from {Ip}", username, ip);
            throw ApiException.Business(400, ApiCodes.LoginFailed, "用户名或密码错误");
        }
        if (user.LockedUntil is { } locked && locked > now)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((locked - now).TotalMinutes));
            throw ApiException.Business(400, ApiCodes.AccountLocked, $"账户已锁定,请 {minutes} 分钟后再试");
        }
        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            user.FailedLogins++;
            if (user.FailedLogins >= s.LoginMaxFailures)
            {
                user.LockedUntil = now.AddMinutes(s.LoginLockMinutes);
                user.FailedLogins = 0;
                logger.LogWarning("User '{User}' locked for {Minutes} min after repeated failures from {Ip}", username, s.LoginLockMinutes, ip);
            }
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Login failed for '{User}' from {Ip}", username, ip);
            throw ApiException.Business(400, ApiCodes.LoginFailed, "用户名或密码错误");
        }
        user.FailedLogins = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync(ct);
        _tokenVersions[user.Id] = user.TokenVersion;
        logger.LogInformation("User '{User}' logged in from {Ip}", username, ip);
        return await jwt.IssueAsync(user, userAgent, ip, ct);
    }

    public async Task<AdminUser?> GetAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task ChangePasswordAsync(int id, string? oldPassword, string? newPassword, string? keepRefreshToken, CancellationToken ct)
    {
        var v = new Validator();
        v.When(string.IsNullOrEmpty(oldPassword), "oldPassword", "不能为空");
        v.When(newPassword is null || newPassword.Length < 8 || newPassword.Length > 64, "newPassword", "长度必须在 8–64 之间");
        v.ThrowIfInvalid();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.Unauthorized(401, "用户不存在");
        if (!PasswordHasher.Verify(oldPassword!, user.PasswordHash)) throw ApiException.Business(400, ApiCodes.OldPasswordWrong, "旧密码错误");
        user.PasswordHash = PasswordHasher.Hash(newPassword!);
        user.PasswordChangedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await jwt.RevokeOthersAsync(id, keepRefreshToken, ct);
        logger.LogWarning("User '{User}' changed the password", user.Username);
    }

    /// <summary>logout all: revoke every refresh token and invalidate outstanding access tokens.</summary>
    public async Task LogoutAllAsync(int id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return;
        user.TokenVersion++;
        await db.SaveChangesAsync(ct);
        _tokenVersions[id] = user.TokenVersion;
        await jwt.RevokeAsync(id, null, all: true, ct);
    }

    public async Task<AdminUser> UpdateProfileAsync(int id, string? nickName, string? avatar, string? email, CancellationToken ct)
    {
        var v = new Validator();
        v.When(nickName is { Length: > 32 }, "nickName", "长度不能超过 32");
        v.When(avatar is { Length: > 512 }, "avatar", "长度不能超过 512");
        v.When(email is { Length: > 128 }, "email", "长度不能超过 128");
        v.ThrowIfInvalid();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.Unauthorized(401, "用户不存在");
        if (nickName is not null) user.NickName = nickName.Trim();
        if (avatar is not null) user.Avatar = avatar.Trim();
        if (email is not null) user.Email = email.Trim();
        await db.SaveChangesAsync(ct);
        return user;
    }

    /// <summary>vue-naive-admin compatible user detail (docs/API.md 2.4).</summary>
    public static object ToDetail(AdminUser u)
    {
        var role = new { id = 1, code = "SUPER_ADMIN", name = "超级管理员", enable = true };
        return new
        {
            id = u.Id,
            username = u.Username,
            enable = true,
            profile = new
            {
                id = u.Id,
                nickName = string.IsNullOrEmpty(u.NickName) ? u.Username : u.NickName,
                avatar = string.IsNullOrEmpty(u.Avatar) ? $"https://api.dicebear.com/9.x/identicon/svg?seed={Uri.EscapeDataString(u.Username)}" : u.Avatar,
                gender = 0,
                address = (string?)null,
                email = u.Email,
            },
            roles = new[] { role },
            currentRole = role,
        };
    }
}
