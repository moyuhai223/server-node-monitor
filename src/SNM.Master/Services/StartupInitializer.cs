using Microsoft.EntityFrameworkCore;
using SNM.Master.Data;
using SNM.Master.Options;
using SNM.Master.Runtime;

namespace SNM.Master.Services;

/// <summary>
/// Blocking start-up work (docs/DESIGN.md 5 step 6-8): PRAGMAs + migrations + quick_check, settings seed, admin seed,
/// registry load, GeoIP disk load. Idempotent; called by Program before Kestrel starts and by integration tests explicitly.
/// </summary>
public sealed class StartupInitializer(
    IDbContextFactory<SnmDbContext> dbFactory, DataPaths paths, SettingsService settings, AdminUserService users,
    NodeRegistry registry, GeoIpService geoIp, Microsoft.Extensions.Options.IOptions<SnmOptions> options, ILogger<StartupInitializer> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public bool IsInitialized => _done;

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_done) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_done) return;
            await InitializeCoreAsync(ct);
            _done = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        var fresh = !File.Exists(paths.DbPath);
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync(ct);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = fresh ? "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode=WAL;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (db.Database.GetMigrations().Any())
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count > 0) logger.LogInformation("Applying {Count} database migration(s): {Names}", pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync(ct);
            }
            else
            {
                logger.LogWarning("No EF migrations compiled in; falling back to EnsureCreated (development only)");
                await db.Database.EnsureCreatedAsync(ct);
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA quick_check;";
                var result = (await cmd.ExecuteScalarAsync(ct))?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) logger.LogError("SQLite quick_check reported: {Result}", result);
            }
        }

        await settings.InitializeAsync(options.Value, ct);
        await users.SeedAsync(ct);
        await registry.LoadAsync(ct);
        await geoIp.TryLoadFromDiskAsync(ct);
    }
}

/// <summary>Runs the initializer as the first hosted service (a no-op when Program already ran it).</summary>
public sealed class StartupHostedService(StartupInitializer initializer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => initializer.EnsureInitializedAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
