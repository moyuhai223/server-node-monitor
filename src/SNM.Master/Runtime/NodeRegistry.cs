using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SNM.Contracts;
using SNM.Master.Data;
using SNM.Master.Data.Entities;
using SNM.Master.Services;

namespace SNM.Master.Runtime;

/// <summary>Singleton in-memory registry of all nodes (docs/DESIGN.md 3.1). Loaded once at startup; the database is a snapshot.</summary>
public sealed class NodeRegistry(IDbContextFactory<SnmDbContext> dbFactory, SettingsService settings, ILogger<NodeRegistry> logger)
{
    private readonly ConcurrentDictionary<int, NodeRuntime> _nodes = new();
    private readonly ConcurrentDictionary<string, int> _keyIndex = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once LoadAsync has finished; background services await it.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Raised when node metadata changed (create/update/delete/reorder/register). Empty array = everything.</summary>
    public event Action<int[]>? NodesChanged;

    public IEnumerable<NodeRuntime> All => _nodes.Values;
    public int Count => _nodes.Count;

    public NodeRuntime? Get(int id) => _nodes.GetValueOrDefault(id);

    public bool TryGetByKey(string agentKey, out NodeRuntime node)
    {
        node = null!;
        if (!_keyIndex.TryGetValue(agentKey, out var id)) return false;
        return _nodes.TryGetValue(id, out node!);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var nodes = await db.Nodes.AsNoTracking().ToListAsync(ct);
        var states = await db.TrafficStates.AsNoTracking().ToDictionaryAsync(x => x.NodeId, ct);
        var now = DateTime.UtcNow;
        var snap = settings.Snapshot;

        foreach (var n in nodes)
        {
            var rt = new NodeRuntime(n);
            var tz = BillingPeriod.ResolveTimeZone(n.TimeZoneId, snap.TimeZoneId);
            var today = BillingPeriod.LocalToday(now, tz);
            var periodStart = BillingPeriod.PeriodStart(today, n.TrafficResetDay);
            var monthly = await db.TrafficMonthly.AsNoTracking().FirstOrDefaultAsync(x => x.NodeId == n.Id && x.PeriodStart == periodStart, ct);
            var daily = await db.TrafficDaily.AsNoTracking().FirstOrDefaultAsync(x => x.NodeId == n.Id && x.Date == today, ct);
            TrafficEngine.LoadFrom(rt.Traffic, states.GetValueOrDefault(n.Id), monthly, daily, today, n.TrafficResetDay);
            // A node that was Online when the master stopped is Unknown until it reports again.
            if (rt.Status == NodeStatus.Online) { rt.Status = NodeStatus.Unknown; rt.StateDirty = true; }
            _nodes[n.Id] = rt;
            _keyIndex[n.AgentKey] = n.Id;
        }

        logger.LogInformation("Loaded {Count} nodes into the registry", _nodes.Count);
        _ready.TrySetResult();
    }

    /// <summary>Called by NodeService after a node was created or its entity updated. Replaces Meta and re-indexes the key.</summary>
    public NodeRuntime Upsert(Node entity)
    {
        var rt = _nodes.AddOrUpdate(entity.Id,
            _ => new NodeRuntime(entity),
            (_, existing) =>
            {
                var oldKey = existing.Meta.AgentKey;
                if (oldKey != entity.AgentKey) _keyIndex.TryRemove(oldKey, out _);
                existing.Meta = entity;
                existing.Disks = NodeRuntime.ParseDisks(entity.DisksJson);
                return existing;
            });
        _keyIndex[entity.AgentKey] = entity.Id;
        return rt;
    }

    public NodeRuntime? Remove(int id)
    {
        if (!_nodes.TryRemove(id, out var rt)) return null;
        _keyIndex.TryRemove(rt.Meta.AgentKey, out _);
        return rt;
    }

    /// <summary>Reloads the traffic runtime from the database (after reset-day / time zone changes made through REST).</summary>
    public async Task ReloadTrafficAsync(NodeRuntime rt, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var n = rt.Meta;
        var tz = BillingPeriod.ResolveTimeZone(n.TimeZoneId, settings.Snapshot.TimeZoneId);
        var today = BillingPeriod.LocalToday(DateTime.UtcNow, tz);
        var periodStart = BillingPeriod.PeriodStart(today, n.TrafficResetDay);
        var state = await db.TrafficStates.AsNoTracking().FirstOrDefaultAsync(x => x.NodeId == n.Id, ct);
        var monthly = await db.TrafficMonthly.AsNoTracking().FirstOrDefaultAsync(x => x.NodeId == n.Id && x.PeriodStart == periodStart, ct);
        var daily = await db.TrafficDaily.AsNoTracking().FirstOrDefaultAsync(x => x.NodeId == n.Id && x.Date == today, ct);
        lock (rt.Sync)
        {
            var prevRx = rt.Traffic.PrevRx; var prevTx = rt.Traffic.PrevTx; var prevAt = rt.Traffic.PrevAt;
            var boot = rt.Traffic.BootTimeAtPrev; var conn = rt.Traffic.PrevConnectionId;
            TrafficEngine.LoadFrom(rt.Traffic, state, monthly, daily, today, n.TrafficResetDay);
            // keep the live (possibly newer than DB) counter baseline
            if (prevRx >= 0) { rt.Traffic.PrevRx = prevRx; rt.Traffic.PrevTx = prevTx; rt.Traffic.PrevAt = prevAt; rt.Traffic.BootTimeAtPrev = boot; rt.Traffic.PrevConnectionId = conn; }
            rt.Traffic.Dirty = true;
            rt.Traffic.PeriodChanged = true;
        }
    }

    public void RaiseNodesChanged(params int[] ids)
    {
        try { NodesChanged?.Invoke(ids); }
        catch (Exception ex) { logger.LogWarning(ex, "NodesChanged handler failed"); }
    }
}
