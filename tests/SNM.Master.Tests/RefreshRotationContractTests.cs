using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SNM.Master.Auth;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Options;
using SNM.Master.Services;

namespace SNM.Master.Tests;

// A private SQLite file per case. No HTTP server or externally usable credentials.
internal sealed class RotationStore : IDbContextFactory<SnmDbContext>, IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"snm-rotation-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<SnmDbContext> _options;
    internal RotationStore(params IInterceptor[] interceptors)
    {
        _options = new DbContextOptionsBuilder<SnmDbContext>().UseSqlite($"Data Source={_file};Pooling=False")
            .AddInterceptors(interceptors).Options;
    }
    public SnmDbContext CreateDbContext() => new(_options);
    public async Task SeedAsync(string hash, DateTime expires, bool revoked = false)
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.AdminUsers.Add(new AdminUser { Id = 1, Username = "fixture" });
        db.RefreshTokens.Add(new RefreshToken { UserId = 1, TokenHash = hash, ExpiresAt = expires, RevokedAt = revoked ? DateTime.UtcNow : null });
        await db.SaveChangesAsync();
    }
    public JwtTokenService Service() => new(Microsoft.Extensions.Options.Options.Create(new SnmOptions { Jwt = { Secret = Tokens.RandomBase64Url() } }),
        new SettingsService(this, NullLogger<SettingsService>.Instance), this, NullLogger<JwtTokenService>.Instance);
    public void Dispose() => File.Delete(_file);
}

public sealed class RefreshRotationContractTests
{
    [Fact]
    public async Task PredecessorIsClaimedInTheSameTransactionBeforeInsertingSuccessor()
    {
        var original = Tokens.RandomBase64Url();
        var observer = new InsertObserver(Tokens.Sha256Base64(original));
        using var store = new RotationStore(observer);
        await store.SeedAsync(observer.Hash, DateTime.UtcNow.AddDays(1));
        var issued = await store.Service().RefreshAsync(original, null, null, default);
        Assert.NotNull(issued);
        Assert.True(observer.SawClaim, "Successor insertion must see its own transaction's persisted predecessor claim.");
        await using var check = store.CreateDbContext();
        var parent = await check.RefreshTokens.SingleAsync(t => t.TokenHash == observer.Hash);
        var child = await check.RefreshTokens.SingleAsync(t => t.TokenHash == parent.ReplacedByHash);
        Assert.NotNull(parent.RevokedAt);
        Assert.Null(child.RevokedAt);
        Assert.Equal(Tokens.Sha256Base64(issued.RefreshToken), child.TokenHash);
    }

    [Fact]
    public async Task FailedSuccessorInsertLeavesOriginalUnclaimed()
    {
        var original = Tokens.RandomBase64Url();
        var observer = new InsertObserver(Tokens.Sha256Base64(original)) { Fail = true };
        using var store = new RotationStore(observer);
        await store.SeedAsync(observer.Hash, DateTime.UtcNow.AddDays(1));
        var service = store.Service();
        await Assert.ThrowsAsync<IOException>(() => service.RefreshAsync(original, null, null, default));
        await using var check = store.CreateDbContext();
        var row = Assert.Single(await check.RefreshTokens.AsNoTracking().ToListAsync());
        Assert.Null(row.RevokedAt);
        Assert.Null(row.ReplacedByHash);
        observer.Fail = false;
        Assert.NotNull(await service.RefreshAsync(original, null, null, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOrAlreadyRevokedRowsDoNotProduceSuccessors(bool revoked)
    {
        var value = Tokens.RandomBase64Url();
        var hash = Tokens.Sha256Base64(value);
        using var store = new RotationStore();
        await store.SeedAsync(hash, DateTime.UtcNow.AddDays(revoked ? 1 : -1), revoked);
        await using var db = store.CreateDbContext();
        db.RefreshTokens.Add(new RefreshToken { UserId = 1, TokenHash = "fixture-sibling", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        var service = store.Service();
        Assert.Null(await service.RefreshAsync(value, null, null, default));
        Assert.Equal(2, await db.RefreshTokens.CountAsync());
        var sibling = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == "fixture-sibling");
        Assert.Equal(revoked, sibling.RevokedAt.HasValue); // keep the existing user-wide replay policy
    }

    private sealed class InsertObserver(string hash) : SaveChangesInterceptor
    {
        public string Hash => hash;
        public bool SawClaim, Fail;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var db = (SnmDbContext)e.Context!;
            var child = db.ChangeTracker.Entries<RefreshToken>().SingleOrDefault(x => x.State == EntityState.Added && x.Entity.TokenHash != hash);
            if (child is null) return result;
            SawClaim = db.Database.CurrentTransaction != null && await db.RefreshTokens.AsNoTracking()
                .AnyAsync(t => t.TokenHash == hash && t.RevokedAt != null && t.ReplacedByHash == child.Entity.TokenHash, ct);
            if (Fail) throw new IOException("stand-in successor write failed");
            return result;
        }
    }
}
