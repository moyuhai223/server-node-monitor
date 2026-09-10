using System.ComponentModel.DataAnnotations;

namespace SNM.Master.Data.Entities;

// Column/table names follow docs/DATA.md. All DateTime values are UTC; DateOnly values are node/site-local dates.

public sealed class Node
{
    public int Id { get; set; }
    [MaxLength(64)] public string PublicName { get; set; } = "";
    [MaxLength(256)] public string? AdminRemark { get; set; }
    [MaxLength(48)] public string AgentKey { get; set; } = "";
    public DateTime? KeyRotatedAt { get; set; }
    public bool Enabled { get; set; } = true;
    public bool PublicVisible { get; set; } = true;
    public int SortOrder { get; set; }
    [MaxLength(2)] public string? CountryCodeOverride { get; set; }
    [MaxLength(2)] public string? CountryCodeAuto { get; set; }
    [MaxLength(64)] public string? TimeZoneId { get; set; }
    public int IntervalMs { get; set; } = 2000;

    // Hardware (from Register)
    [MaxLength(64)] public string? Hostname { get; set; }
    [MaxLength(128)] public string? Os { get; set; }
    [MaxLength(64)] public string? Kernel { get; set; }
    [MaxLength(16)] public string? Arch { get; set; }
    [MaxLength(128)] public string? CpuModel { get; set; }
    public int CpuCores { get; set; }
    public long MemTotalMb { get; set; }
    public long SwapTotalMb { get; set; }
    /// <summary>JSON array of {mount, fs, totalMb}; order = heartbeat disk index.</summary>
    public string? DisksJson { get; set; }
    [MaxLength(256)] public string? NetIfs { get; set; }
    [MaxLength(32)] public string? Virt { get; set; }
    [MaxLength(32)] public string? AgentVersion { get; set; }
    public int ProtocolVersion { get; set; }
    public DateTime? BootTimeUtc { get; set; }

    // State snapshot (memory is the truth; flushed every minute)
    public DateTime? FirstSeenAt { get; set; }
    public DateTime? LastRegisterAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    [MaxLength(45)] public string? LastRemoteIp { get; set; }
    public int Status { get; set; }
    public DateTime? StatusChangedAt { get; set; }

    // Traffic accounting
    public long TrafficLimitBytes { get; set; }
    public int TrafficResetDay { get; set; } = 1;
    public int TrafficCountMode { get; set; }

    // Finance (Wallos-lite)
    [MaxLength(64)] public string? Vendor { get; set; }
    public decimal? Price { get; set; }
    [MaxLength(3)] public string? Currency { get; set; }
    public int BillingCycleMonths { get; set; }
    public DateOnly? ExpiresAt { get; set; }
    public bool AutoRenew { get; set; }
    [MaxLength(512)] public string? RenewUrl { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }

    // Alert overrides (null => global)
    public bool AlertsEnabled { get; set; } = true;
    public int? CpuAlertPct { get; set; }
    public int? TrafficAlertPct { get; set; }
    public int? OfflineAlertSec { get; set; }
    public int? DiskAlertPct { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class NodeIp
{
    public int NodeId { get; set; }
    [MaxLength(45)] public string Address { get; set; } = "";
    public int Family { get; set; }
    public bool IsPublic { get; set; }
    /// <summary>1 agent-reported, 2 server-captured, 3 both.</summary>
    public int Source { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public sealed class InstallToken
{
    public int Id { get; set; }
    public int NodeId { get; set; }
    [MaxLength(43)] public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int UsedCount { get; set; }
    public DateTime? LastUsedAt { get; set; }
    [MaxLength(45)] public string? LastUsedIp { get; set; }
}

/// <summary>Shape shared by Metrics1m / Metrics1h / Metrics1d (three independent tables).</summary>
public abstract class MetricBucket
{
    public int NodeId { get; set; }
    /// <summary>Bucket start, Unix seconds UTC (multiple of 60 / 3600 / 86400).</summary>
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

public sealed class Metric1m : MetricBucket { }
public sealed class Metric1h : MetricBucket { }
public sealed class Metric1d : MetricBucket { }

public sealed class TrafficState
{
    public int NodeId { get; set; }
    /// <summary>-1 = no baseline.</summary>
    public long PrevRx { get; set; } = -1;
    public long PrevTx { get; set; } = -1;
    public DateTime? PrevAtUtc { get; set; }
    public DateTime? BootTimeAtPrevUtc { get; set; }
    [MaxLength(64)] public string? PrevConnectionId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TrafficDaily
{
    public int NodeId { get; set; }
    public DateOnly Date { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class TrafficMonthly
{
    public int NodeId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public long RxBytes { get; set; }
    public long TxBytes { get; set; }
    public long BilledBytes { get; set; }
    public long LimitBytes { get; set; }
    public int CountMode { get; set; }
    public int ResetDay { get; set; }
    public bool Closed { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AlertState
{
    public int NodeId { get; set; }
    public int Rule { get; set; }
    [MaxLength(128)] public string Subject { get; set; } = "";
    /// <summary>0 Normal / 1 Pending / 2 Firing.</summary>
    public int State { get; set; }
    public int Consecutive { get; set; }
    public int ResolveCount { get; set; }
    public DateTime? FiringSince { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
    public DateTime? CooldownUntil { get; set; }
    public double LastValue { get; set; }
    public long? OpenEventId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AlertEvent
{
    public long Id { get; set; }
    public int? NodeId { get; set; }
    [MaxLength(64)] public string NodeName { get; set; } = "";
    public int Rule { get; set; }
    [MaxLength(128)] public string Subject { get; set; } = "";
    /// <summary>1 Firing / 2 Resolved.</summary>
    public int Status { get; set; }
    public int Severity { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(2000)] public string Message { get; set; } = "";
    public double Value { get; set; }
    public double Threshold { get; set; }
    [MaxLength(160)] public string DedupKey { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public bool Notified { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
}

public sealed class NotificationChannel
{
    public int Id { get; set; }
    /// <summary>telegram / webhook.</summary>
    [MaxLength(16)] public string Type { get; set; } = "";
    [MaxLength(64)] public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string ConfigJson { get; set; } = "{}";
    /// <summary>Bit mask 1&lt;&lt;rule; 0 = all rules.</summary>
    public int RuleMask { get; set; }
    public int MinSeverity { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastTestAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    [MaxLength(500)] public string? LastError { get; set; }
}

public sealed class NotificationDelivery
{
    public long Id { get; set; }
    public long? EventId { get; set; }
    public int? ChannelId { get; set; }
    [MaxLength(64)] public string ChannelName { get; set; } = "";
    /// <summary>1 firing / 2 recovery / 3 test.</summary>
    public int Kind { get; set; }
    public int Attempt { get; set; }
    public bool Ok { get; set; }
    public int StatusCode { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    public int ElapsedMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Setting
{
    [MaxLength(64)] public string Key { get; set; } = "";
    /// <summary>JSON literal.</summary>
    public string Value { get; set; } = "null";
    public DateTime UpdatedAt { get; set; }
}

public sealed class AdminUser
{
    public int Id { get; set; }
    [MaxLength(32)] public string Username { get; set; } = "";
    [MaxLength(200)] public string PasswordHash { get; set; } = "";
    [MaxLength(32)] public string? NickName { get; set; }
    [MaxLength(512)] public string? Avatar { get; set; }
    [MaxLength(128)] public string? Email { get; set; }
    public int TokenVersion { get; set; }
    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

public sealed class RefreshToken
{
    public long Id { get; set; }
    public int UserId { get; set; }
    [MaxLength(44)] public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    [MaxLength(44)] public string? ReplacedByHash { get; set; }
    [MaxLength(256)] public string? UserAgent { get; set; }
    [MaxLength(45)] public string? Ip { get; set; }
}
