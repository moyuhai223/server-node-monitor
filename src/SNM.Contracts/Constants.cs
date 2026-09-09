namespace SNM.Contracts;

/// <summary>SignalR hub routes hosted by the Master.</summary>
public static class HubRoutes
{
    public const string Agent = "/hubs/agent";
    public const string Public = "/hubs/public";
    public const string Admin = "/hubs/admin";
}

/// <summary>Wire names of the agent hub. Kept short on purpose: the target name is part of every frame.</summary>
public static class AgentHubMethods
{
    // agent -> master
    public const string Register = "register";      // InvokeAsync<AgentConfigDto>(RegisterDto)
    public const string Heartbeat = "hb";            // SendAsync(HeartbeatDto)
    public const string ReportStatus = "status";     // SendAsync(StatusReportDto)

    // master -> agent (the complete set of downlink messages)
    public const string Configure = "configure";     // AgentConfigDto
}

public static class PublicHubMethods
{
    // master -> browser
    public const string Snapshot = "snapshot";       // PublicSnapshotDto
    public const string Batch = "batch";             // PublicBatchDto
    public const string NodesChanged = "nodes";      // PublicNodeDto[] (Hist = null)

    // browser -> master
    public const string GetSnapshot = "GetSnapshot"; // () -> PublicSnapshotDto
}

public static class AdminHubMethods
{
    // master -> browser
    public const string Snapshot = "snapshot";         // AdminSnapshotDto
    public const string Batch = "batch";               // AdminBatchDto
    public const string Alert = "alert";               // AdminAlertDto
    public const string NodesChanged = "nodesChanged"; // int[] nodeIds (empty = all)

    // browser -> master
    public const string GetSnapshot = "GetSnapshot";   // () -> AdminSnapshotDto
    public const string GetHistory = "GetHistory";     // (int nodeId) -> AdminHistoryDto?
}

public static class ProtocolConstants
{
    /// <summary>Agent wire protocol version carried in RegisterDto. Bump only for incompatible changes (see docs/PROTOCOL.md).</summary>
    public const ushort ProtocolVersion = 1;

    public const string AgentKeyHeader = "X-SNM-Agent-Key";
    public const string AgentKeyQueryParam = "access_token";
    public const string AgentKeyPrefix = "snmk_";
    /// <summary>Prefix + 43 base64url chars (32 random bytes).</summary>
    public const int AgentKeyLength = 48;

    public const ushort DefaultIntervalMs = 2000;
    public const ushort MinIntervalMs = 1000;
    public const ushort MaxIntervalMs = 60000;
    public const ushort DefaultStatusIntervalSec = 300;
    public const ushort MinStatusIntervalSec = 60;
    public const ushort MaxStatusIntervalSec = 3600;

    public const int MaxDisks = 16;
    public const int MaxIps = 16;
    public const int MaxStringChars = 256;
    /// <summary>Static readers reject any array header larger than this.</summary>
    public const int MaxArrayHeader = 256;
    /// <summary>Ring buffer size per node for the live sparkline (60 points x 2 s = 2 minutes).</summary>
    public const int HistoryPoints = 60;
    public const int MaxHubMessageBytes = 64 * 1024;
    public const ushort CpuPermilleMax = 1000;
    public const int MiB = 1024 * 1024;
}

public static class NodeStatus
{
    public const byte Unknown = 0;
    public const byte Online = 1;
    public const byte Offline = 2;
}

public static class AlertRule
{
    public const byte Offline = 1;
    public const byte CpuHigh = 2;
    public const byte TrafficWarn = 3;
    public const byte TrafficExceeded = 4;
    public const byte Expiry = 5;
    public const byte DiskHigh = 6;
}

public static class AlertEventStatus
{
    public const byte Firing = 1;
    public const byte Resolved = 2;
}

public static class AlertSeverity
{
    public const byte Info = 1;
    public const byte Warning = 2;
    public const byte Critical = 3;
}

public static class TrafficCountMode
{
    public const byte RxPlusTx = 0;
    public const byte TxOnly = 1;
    public const byte RxOnly = 2;
    public const byte MaxOfRxTx = 3;
}
