using Microsoft.EntityFrameworkCore;
using SNM.Master.Auth;

namespace SNM.Master.Tests;

public class RefreshClaimStorageTests
{
    [Fact]
    public async Task TwoIndependentSqliteWritersCanClaimADummyRowOnlyOnce()
    {
        // Exercise only the storage primitive: no login, bearer token or issued successor is involved.
        using var store = new RotationStore();
        await store.SeedAsync("dummy-storage-row", DateTime.UtcNow.AddHours(1));
        await using var read = store.CreateDbContext();
        var id = await read.RefreshTokens.Select(t => t.Id).SingleAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Write(string label)
        {
            await using var db = store.CreateDbContext();
            await start.Task;
            return await JwtTokenService.TryClaimAsync(db, id, DateTime.UtcNow, label, default);
        }
        var first = Write("first"); var second = Write("second");
        start.SetResult();
        var result = await Task.WhenAll(first, second);
        Assert.Single(result, won => won);
        var row = await read.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.NotNull(row.RevokedAt);
        Assert.Equal(result[0] ? "first" : "second", row.ReplacedByHash);
    }
}
