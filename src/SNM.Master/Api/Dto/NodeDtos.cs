using System.Text.Json.Serialization;
using SNM.Contracts.Dtos;

namespace SNM.Master.Api.Dto;

// REST output models for nodes (docs/API.md 4.1). Input is applied from JsonElement (partial updates), see NodeService.

public sealed class NodeDetailDto
{
    public int Id { get; set; }
    public string PublicName { get; set; } = "";
    public string? AdminRemark { get; set; }
    public bool Enabled { get; set; }
    public bool PublicVisible { get; set; }
    public int SortOrder { get; set; }
    public string CountryCode { get; set; } = "";
    public string? CountryCodeAuto { get; set; }
    public string? CountryCodeOverride { get; set; }
    public string? TimeZoneId { get; set; }
    public int IntervalMs { get; set; }
    public string AgentKeyMasked { get; set; } = "";
    public DateTime? KeyRotatedAt { get; set; }

    /// <summary>Only present in the create / rotate responses.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentKey { get; set; }

    public NodeHardwareDto Hardware { get; set; } = new();
    public NodeIpDto[] Ips { get; set; } = [];
    public string? RemoteIp { get; set; }
    public NodeStateDto State { get; set; } = new();
    public NodeLiveDto? Live { get; set; }
    public NodeTrafficDto Traffic { get; set; } = new();
    public NodeFinanceDto Finance { get; set; } = new();
    public NodeAlertsDto Alerts { get; set; } = new();
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class NodeHardwareDto
{
    public string? Hostname { get; set; }
    public string? Os { get; set; }
    public string? Kernel { get; set; }
    public string? Arch { get; set; }
    public string? CpuModel { get; set; }
    public int CpuCores { get; set; }
    public long MemTotalMb { get; set; }
    public long SwapTotalMb { get; set; }
    public NodeDiskDto[] Disks { get; set; } = [];
    public string? NetIfs { get; set; }
    public string? Virt { get; set; }
    public string? AgentVersion { get; set; }
    public int ProtocolVersion { get; set; }
    public DateTime? BootTimeUtc { get; set; }
}

public sealed class NodeDiskDto
{
    public string Mount { get; set; } = "";
    public string Fs { get; set; } = "";
    public long TotalMb { get; set; }

    public static NodeDiskDto From(DiskInfoDto d) => new() { Mount = d.Mount, Fs = d.Fs, TotalMb = d.TotalMb };
}

public sealed class NodeIpDto
{
    public string Address { get; set; } = "";
    public int Family { get; set; }
    public bool IsPublic { get; set; }
    public int Source { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public sealed class NodeStateDto
{
    public int Status { get; set; }
    public bool Connected { get; set; }
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastRegisterAt { get; set; }
    public DateTime? StatusChangedAt { get; set; }
    public long UptimeSec { get; set; }
}

public sealed class NodeLiveDto
{
    public int CpuPermille { get; set; }
    public long MemUsedMb { get; set; }
    public long SwapUsedMb { get; set; }
    public long[] DiskUsedMb { get; set; } = [];
    public long RxBps { get; set; }
    public long TxBps { get; set; }
    public int Load1 { get; set; }
    public DateTime Ts { get; set; }
}

public class NodeTrafficDto
{
    public long LimitBytes { get; set; }
    public int CountMode { get; set; }
    public int ResetDay { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public long BilledBytes { get; set; }
    public double? Pct { get; set; }
    public string? TimeZoneId { get; set; }
}

public sealed class NodeFinanceDto
{
    public string? Vendor { get; set; }
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public int BillingCycleMonths { get; set; }
    public DateOnly? ExpiresAt { get; set; }
    public int? DaysLeft { get; set; }
    public bool AutoRenew { get; set; }
    public string? RenewUrl { get; set; }
}

public sealed class NodeAlertsDto
{
    public bool AlertsEnabled { get; set; }
    public int? CpuAlertPct { get; set; }
    public int? TrafficAlertPct { get; set; }
    public int? OfflineAlertSec { get; set; }
    public int? DiskAlertPct { get; set; }
    public FiringDto[] Firing { get; set; } = [];
}

public sealed class FiringDto
{
    public int Rule { get; set; }
    public DateTime Since { get; set; }
}

public sealed class MetricPointDto
{
    public long Ts { get; set; }
    public int Samples { get; set; }
    public int CpuAvg { get; set; }
    public int CpuMax { get; set; }
    public long MemUsedAvgMb { get; set; }
    public long MemUsedMaxMb { get; set; }
    public long SwapUsedAvgMb { get; set; }
    public long DiskUsedMb { get; set; }
    public long DiskTotalMb { get; set; }
    public long RxBpsAvg { get; set; }
    public long RxBpsMax { get; set; }
    public long TxBpsAvg { get; set; }
    public long TxBpsMax { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public int Load1Avg { get; set; }
    public int Load1Max { get; set; }
}

public sealed class MetricsResponseDto
{
    public string Range { get; set; } = "";
    public int StepSec { get; set; }
    public long FromTs { get; set; }
    public long ToTs { get; set; }
    public MetricPointDto[] Points { get; set; } = [];
}

public sealed class TrafficDailyDto
{
    public DateOnly Date { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
}

public sealed class TrafficCurrentDto : NodeTrafficDto
{
    public TrafficDailyDto[] Daily { get; set; } = [];
}

public sealed class TrafficHistoryDto
{
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public long BilledBytes { get; set; }
    public long LimitBytes { get; set; }
    public bool Closed { get; set; }
}

public sealed class TrafficResponseDto
{
    public TrafficCurrentDto Current { get; set; } = new();
    public TrafficHistoryDto[] History { get; set; } = [];
}

public sealed record NodeListQuery(string? Keyword, int? Status, bool? Enabled, int PageNo, int PageSize, string? SortBy, string? SortDir);
