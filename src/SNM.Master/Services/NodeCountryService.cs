using System.Net;
using Microsoft.EntityFrameworkCore;
using SNM.Master.Data;
using SNM.Master.Runtime;

namespace SNM.Master.Services;

/// <summary>Reconciles persisted automatic countries after loading or refreshing GeoIP data.</summary>
public sealed class NodeCountryService(GeoIpService geoIp, SettingsService settings,
    IDbContextFactory<SnmDbContext> dbFactory, NodeRegistry registry)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<int> RecalculateAsync(CancellationToken ct)
    {
        if (!geoIp.Enabled || !settings.Snapshot.GeoIpEnabled || !geoIp.Ready) return 0;
        await _gate.WaitAsync(ct);
        var changed = new List<int>();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var nodes = await db.Nodes.AsNoTracking()
                .Select(n => new { n.Id, n.LastRemoteIp, n.CountryCodeAuto }).ToListAsync(ct);
            foreach (var node in nodes)
            {
                ct.ThrowIfCancellationRequested();
                if (!IPAddress.TryParse(node.LastRemoteIp, out var ip)) continue;
                var cc = geoIp.Lookup(ip);
                if (cc is null || cc == node.CountryCodeAuto) continue;

                // Avoid replacing unrelated metadata or applying a result for an IP
                // that changed while this refresh was in flight. Never write overrides.
                var updated = await db.Nodes.Where(n => n.Id == node.Id
                        && n.LastRemoteIp == node.LastRemoteIp && n.CountryCodeAuto == node.CountryCodeAuto)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.CountryCodeAuto, cc), ct);
                if (updated == 0) continue;
                if (registry.Get(node.Id) is { } runtime)
                {
                    lock (runtime.Sync)
                    {
                        if (runtime.Meta.LastRemoteIp == node.LastRemoteIp && runtime.Meta.CountryCodeAuto == node.CountryCodeAuto)
                            runtime.Meta.CountryCodeAuto = cc;
                    }
                }
                changed.Add(node.Id);
            }
            return changed.Count;
        }
        finally
        {
            _gate.Release();
            // Publish completed writes even when a later node was cancelled/failed.
            if (changed.Count > 0) registry.RaiseNodesChanged(changed.ToArray());
        }
    }
}
