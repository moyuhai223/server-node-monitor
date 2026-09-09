using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SNM.Contracts;
using SNM.Contracts.Dtos;
using SNM.Master.Hubs;
using SNM.Master.Runtime;
using SNM.Master.Services;

namespace SNM.Master.Background;

/// <summary>Every 2 s: offline detection (R9) and batched live pushes to the public/admin hubs; metadata pushes on NodesChanged.</summary>
public sealed class RealtimeBroadcaster : SnmBackgroundService
{
    private readonly IHubContext<PublicHub> _publicHub;
    private readonly IHubContext<AdminHub> _adminHub;
    private readonly LiveSnapshotBuilder _builder;
    private readonly SettingsService _settings;
    private readonly ConcurrentQueue<int[]> _metaChanges = new();

    public RealtimeBroadcaster(IHubContext<PublicHub> publicHub, IHubContext<AdminHub> adminHub, LiveSnapshotBuilder builder,
        SettingsService settings, NodeRegistry registry, ILogger<RealtimeBroadcaster> logger) : base(logger, registry)
    {
        _publicHub = publicHub;
        _adminHub = adminHub;
        _builder = builder;
        _settings = settings;
        registry.NodesChanged += ids => _metaChanges.Enqueue(ids);
        settings.Changed += keys =>
        {
            if (keys.Any(k => k.StartsWith("site.public", StringComparison.Ordinal) || k.StartsWith("public.", StringComparison.Ordinal) || k == "alert.offlineTimeoutSec"))
                _metaChanges.Enqueue([]);
        };
    }

    protected override TimeSpan Period => TimeSpan.FromSeconds(2);

    protected override async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var snap = _settings.Snapshot;
        var publicItems = new List<PublicNodeLiveDto>();
        var adminItems = new List<AdminNodeLiveDto>();

        foreach (var node in Registry.All)
        {
            var meta = node.Meta;
            var timeout = meta.OfflineAlertSec ?? snap.OfflineTimeoutSec;
            if (node.Status == NodeStatus.Online && node.LastSeenAt is { } seen && (now - seen).TotalSeconds > timeout)
            {
                lock (node.Sync)
                {
                    if (node.Status == NodeStatus.Online)
                    {
                        node.Status = NodeStatus.Offline;
                        node.StatusChangedAt = now;
                        node.StateDirty = true;
                        node.Dirty = true;
                        // Rates are meaningless once the node stopped reporting.
                        if (node.Live is { } live) node.Live = live with { RxBps = 0, TxBps = 0 };
                    }
                }
                Logger.LogWarning("Node {NodeId} ({Name}) is offline (last seen {Seen:u})", node.Id, meta.PublicName, seen);
            }

            if (!node.Dirty) continue;
            node.Dirty = false;
            adminItems.Add(_builder.AdminLive(node));
            if (_builder.IsPublicVisible(node)) publicItems.Add(_builder.PublicLive(node));
        }

        var ts = LiveSnapshotBuilder.UnixMs(now);
        if (publicItems.Count > 0)
            await _publicHub.Clients.All.SendAsync(PublicHubMethods.Batch, new PublicBatchDto { ServerTs = ts, Items = publicItems.ToArray() }, ct);
        if (adminItems.Count > 0)
            await _adminHub.Clients.All.SendAsync(AdminHubMethods.Batch, new AdminBatchDto { ServerTs = ts, Items = adminItems.ToArray() }, ct);

        if (!_metaChanges.IsEmpty)
        {
            var ids = new HashSet<int>();
            var all = false;
            while (_metaChanges.TryDequeue(out var batch))
            {
                if (batch.Length == 0) all = true;
                foreach (var id in batch) ids.Add(id);
            }
            await _publicHub.Clients.All.SendAsync(PublicHubMethods.NodesChanged, _builder.PublicNodes().Select(n => _builder.PublicNode(n, withHistory: false)).ToArray(), ct);
            await _adminHub.Clients.All.SendAsync(AdminHubMethods.NodesChanged, all ? Array.Empty<int>() : ids.ToArray(), ct);
        }
    }
}
