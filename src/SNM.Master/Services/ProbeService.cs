using System.Net;
using System.Text.Json;
using SNM.Contracts.Dtos;
using SNM.Master.Api;
using SNM.Master.Runtime;

namespace SNM.Master.Services;

public sealed record ProbeTarget
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Address { get; init; } = "";
    public byte Kind { get; init; }
    public int Port { get; init; } = 443;
    public int IntervalSec { get; init; } = 30;
    public int TimeoutMs { get; init; } = 2000;
    public int[] NodeIds { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public string Revision { get; init; } = "";
}

/// <summary>Private persisted configuration and bounded, memory-only measurement windows.</summary>
public sealed class ProbeService(SettingsService settings, NodeRegistry registry)
{
    public const string SettingKey = "probes.targets";
    private readonly SemaphoreSlim _save = new(1, 1);
    private readonly Lock _load = new();
    private volatile ProbeTarget[]? _targets;
    public ProbeTarget[] Targets
    {
        get
        {
            if (_targets is { } cached) return cached;
            lock (_load) return _targets ??= JsonSerializer.Deserialize<ProbeTarget[]>(settings.GetRaw(SettingKey)) ?? [];
        }
    }

    public async Task SaveAsync(ProbeTarget[] targets, CancellationToken ct)
    {
        if (targets.Length > 16) throw ApiException.BadRequest("最多配置 16 个探测目标");
        var ids = new HashSet<int>();
        foreach (var t in targets)
        {
            if (t is null || t.Id <= 0 || !ids.Add(t.Id)) throw ApiException.BadRequest("目标 ID 必须为不重复的正整数");
            if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 48) throw ApiException.BadRequest("线路名称须为 1–48 字符");
            if (string.IsNullOrWhiteSpace(t.Address) || t.Address.Length > 253 ||
                !(IPAddress.TryParse(t.Address, out _) || Uri.CheckHostName(t.Address) == UriHostNameType.Dns))
                throw ApiException.BadRequest("请输入 IP 或域名，不包含协议、路径或端口");
            if (t.Kind > 1 || t.Port is < 1 or > 65535 || t.IntervalSec is < 10 or > 3600 || t.TimeoutMs is < 200 or > 10000)
                throw ApiException.BadRequest("协议、端口、间隔或超时超出范围");
            if (t.NodeIds is null || t.NodeIds.Length is 0 or > 512 || t.NodeIds.Distinct().Count() != t.NodeIds.Length || t.NodeIds.Any(id => registry.Get(id) is null))
                throw ApiException.BadRequest("请选择 1–512 个有效且不重复的节点");
        }
        await _save.WaitAsync(ct);
        try
        {
            var old = Targets;
            var updated = targets.Select(t =>
            {
                var previous = old.FirstOrDefault(x => x.Id == t.Id);
                // Reuse a window only if the measurement definition and assignment are unchanged.
                var same = previous is not null && previous.Address == t.Address && previous.Kind == t.Kind
                    && previous.Port == t.Port && previous.IntervalSec == t.IntervalSec && previous.TimeoutMs == t.TimeoutMs
                    && previous.Enabled == t.Enabled && previous.NodeIds.Order().SequenceEqual(t.NodeIds.Order());
                return t with { Revision = same ? previous!.Revision : Guid.NewGuid().ToString("N"), Name = t.Name.Trim() };
            }).ToArray();
            await settings.SetInternalAsync(SettingKey, SettingsService.J(updated), ct);
            _targets = updated;
            foreach (var node in registry.All)
            {
                var revisions = updated.Where(t => t.Enabled && t.NodeIds.Contains(node.Id)).Select(t => t.Revision).ToHashSet();
                lock (node.Sync)
                    foreach (var key in node.ProbeSamples.Keys.Where(k => !revisions.Contains(k)).ToArray()) node.ProbeSamples.Remove(key);
            }
            registry.RaiseNodesChanged();
        }
        finally { _save.Release(); }
    }

    public ProbeConfigDto Configuration(NodeRuntime node) => new()
    {
        Targets = Targets.Where(t => node.Meta.Enabled && t.Enabled && t.NodeIds.Contains(node.Id)).Select(t => new ProbeTargetDto
        {
            Id = t.Id, Revision = t.Revision, Kind = t.Kind, Address = t.Address, Port = (ushort)t.Port,
            IntervalSec = (ushort)t.IntervalSec, TimeoutMs = (ushort)t.TimeoutMs,
        }).ToArray(),
    };

    public bool Accept(NodeRuntime node, string connectionId, ProbeResultDto result, DateTime now)
    {
        lock (node.Sync)
        {
            if (!node.Meta.Enabled || !node.Registered || !node.Connected || node.ConnectionId != connectionId) return false;
            var target = Targets.FirstOrDefault(t => t.Id == result.Id && t.Revision == result.Revision && t.Enabled && t.NodeIds.Contains(node.Id));
            if (target is null || result.Status > 3 || (result.Status == 0 && (result.Microseconds < 0 || result.Microseconds > target.TimeoutMs * 1000))) return false;
            if (!node.ProbeSamples.TryGetValue(target.Revision, out var samples)) node.ProbeSamples[target.Revision] = samples = new Queue<PublicProbePointDto>();
            var ts = LiveSnapshotBuilder.UnixMs(now);
            if (samples.Count > 0 && ts - samples.Last().Ts < target.IntervalSec * 800L) return false;
            samples.Enqueue(new PublicProbePointDto { Ts = ts, State = result.Status, DurationUs = result.Status == 0 ? result.Microseconds : -1 });
            while (samples.Count > 60) samples.Dequeue();
            node.ProbesDirty = true;
            return true;
        }
    }

    public PublicProbeDto[] Snapshot(NodeRuntime node)
    {
        lock (node.Sync) return Targets.Where(t => node.Meta.Enabled && t.Enabled && t.NodeIds.Contains(node.Id)).Select(t => new PublicProbeDto
        {
            Id = t.Id, Name = t.Name, Kind = t.Kind, IntervalSec = t.IntervalSec,
            Points = node.ProbeSamples.TryGetValue(t.Revision, out var samples) ? samples.ToArray() : [],
        }).ToArray();
    }
}
