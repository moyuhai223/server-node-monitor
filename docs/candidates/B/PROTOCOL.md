# PROTOCOL.md — Hub 协议与数据契约(候选设计 B)

> `src/SNM.Contracts` 的唯一规范。§8 的 C# 草案可直接落地(命名空间、类名、常量与本文各表一致)。线格式 = 官方 SignalR MessagePack Hub Protocol(`messagepack` v2);探针端用 Contracts 内的 AOT 实现,Master/浏览器用官方实现。

## 1. 端点、传输与鉴权

| Hub | 路径 | 客户端 | 传输 | 鉴权 | 协议实现 |
|---|---|---|---|---|---|
| AgentHub | `/hubs/agent` | snm-agent(.NET AOT) | WebSockets,回退 LongPolling(无 SSE) | 方案 `AgentKey`:`Authorization: Bearer <AgentKey>`(SignalR `AccessTokenProvider` 自动放入头;LongPolling 每个请求都带);也接受 `?access_token=` | 客户端 `SnmMessagePackHubProtocol`(Contracts);服务端官方 |
| PublicHub | `/hubs/public` | 浏览器(大屏) | WebSockets/SSE 不适用二进制 → WebSockets,回退 LongPolling | 匿名;`public.enabled=false` 时 `OnConnectedAsync` 抛 `HubException("public dashboard disabled")` | `@microsoft/signalr-protocol-msgpack` 10.0.11(基于 `@msgpack/msgpack` ^2.7) |
| AdminHub | `/hubs/admin` | 浏览器(后台) | 同上 | JWT:`accessTokenFactory` → 浏览器放 `?access_token=`;服务端 `JwtBearerEvents.OnMessageReceived` 在路径前缀 `/hubs/admin` 时从 query 取 token | 同上 |

服务端统一 Hub 选项:`KeepAliveInterval=10s`,`ClientTimeoutInterval=30s`,`HandshakeTimeout=15s`,`EnableDetailedErrors=false`(Development 为 true),`MaximumReceiveMessageSize`:agent 64 KB,public 4 KB(仅 `GetSnapshot` 无参),admin 16 KB。`MaximumParallelInvocationsPerClient=1`(保序)。三 Hub 均 `AddMessagePackProtocol()`(官方,默认 `SignalRResolver`:`DynamicEnumAsStringResolver + ContractlessStandardResolver`,`MessagePackSecurity.UntrustedData`),同时保留 JSON 协议(浏览器调试用,探针永不协商 JSON)。

## 2. 服务端时间戳与单位

- 探针 DTO **不含任何时间字段**(`ElapsedMs` 是间隔,不是时刻)。Master 在 Hub 方法入口取 `TimeProvider.GetUtcNow()` 作为样本时间,`Environment.TickCount64` 作为单调时间。
- 单位(与 DATA.md 一致):CPU ‰(`ushort` 0..1000);内存/交换/磁盘 **MiB**(`uint`);网络累计 **字节**(`ulong`);速率 **字节/秒**;负载 ×100(`ushort`);Uptime 秒(`uint`);间隔毫秒(`ushort`,饱和到 65535);浏览器侧时刻 Unix **毫秒**(`long`)。

## 3. AgentHub 方法

### 3.1 客户端 → 服务端

| 线上名(常量) | 签名(服务端) | 频率 | 说明 |
|---|---|---|---|
| `reg`(`AgentHubMethods.Register`) | `Task<AgentConfigDto> Register(RegisterDto dto)` | 每次连接/重连成功后立即一次 | 幂等更新节点清单;返回当前探针配置。服务端长度钳制:字符串 ≤255(CpuModel/Hostname/Os/Kernel)、`Disks ≤ 64`、`Interfaces ≤ 64`、`Ips ≤ 64`,每个 IP 字符串 ≤ 45 |
| `hb`(`AgentHubMethods.Heartbeat`) | `Task Heartbeat(HeartbeatDto dto)` | 每 `HeartbeatSec`(默认 2s) | 纯内存处理(DATA.md §3);`SendAsync` 不等待 |
| `ip`(`AgentHubMethods.ReportIps`) | `Task ReportIps(IpReportDto dto)` | 每 `IpReportSec`(默认 300s)+ 连接后 5s | 与服务端捕获的公网 IP 合并去重写 `Nodes.IpsJson` |
| `disk`(`AgentHubMethods.ReportDisks`) | `Task ReportDisks(DiskReportDto dto)` | 每 `DiskReportSec`(默认 60s)+ 连接后 3s | 整表替换 `NodeDisks`;`DiskTotalMb` 同步更新 |

服务端方法用 `[HubMethodName("hb")]` 等绑定短名;C# 方法名保持可读。

### 3.2 服务端 → 客户端(下行白名单,**仅此一条**)

| 线上名 | 签名(客户端 `On`) | 触发 | 说明 |
|---|---|---|---|
| `cfg`(`AgentHubMethods.ApplyConfig`) | `void ApplyConfig(AgentConfigDto cfg)` | 设置 `agent.*` 被修改时向全部在线探针推送;另外 `reg` 的返回值即同一 DTO | 仅整数字段,范围受限(§4.6);探针收到后重建 `PeriodicTimer`;其他任何方法名探针忽略并记 Warn |

## 4. 探针 DTO(整数键,数组编码)

所有探针 DTO:`[MessagePackObject]` + `[Key(n)]`,`sealed class`,属性 `{ get; set; }`,字符串默认 `""`,数组默认 `[]`。编码为 **定长数组**(长度 = 最大 Key+1),与官方 `DynamicObjectResolver` 完全一致;读取时长度不足用默认值、超出跳过(前向兼容)。

### 4.1 `HeartbeatDto`(实测:典型 37 B,帧 47 B;最小 25/35 B;最坏 48/58 B)

| Key | 属性 | C# 类型 | 单位 | 含义 |
|---|---|---|---|---|
| 0 | Cpu | ushort | ‰ | 全局 CPU 使用率(所有逻辑核聚合) |
| 1 | MemUsedMb | uint | MiB | 物理内存已用 = total - available |
| 2 | SwapUsedMb | uint | MiB | 交换已用 |
| 3 | Load1 | ushort | ×100 | 1 分钟负载;Windows 恒 0 |
| 4 | DiskUsedMb | uint | MiB | 计入挂载点已用之和 |
| 5 | NetRxBytes | ulong | B | 计入网卡累计接收字节(内核原始计数器之和) |
| 6 | NetTxBytes | ulong | B | 累计发送 |
| 7 | UptimeSec | uint | s | 系统开机时长 |
| 8 | ElapsedMs | ushort | ms | 距上一次采样的单调时钟间隔;进程首个样本为 0;>65535 饱和 |

### 4.2 `RegisterDto`(典型 ≈201 B)

| Key | 属性 | 类型 | 含义 |
|---|---|---|---|
| 0 | AgentVersion | string | 如 `1.0.0`(AssemblyInformationalVersion 去掉 `+hash`) |
| 1 | Hostname | string | `--name` 覆盖后的主机名 |
| 2 | Os | string | `PRETTY_NAME` / Windows 产品名 |
| 3 | Kernel | string | 内核/构建号 |
| 4 | Arch | string | `x64` / `arm64` / `x86` / `arm` |
| 5 | CpuModel | string | 多路时前缀 `2x `(N=`physical id` 去重数,N=1 不加) |
| 6 | CpuCores | ushort | 逻辑核心 |
| 7 | MemTotalMb | uint | |
| 8 | SwapTotalMb | uint | |
| 9 | Disks | DiskInfoDto[] | 计入挂载点(含容量与当前用量) |
| 10 | Interfaces | string[] | 计入流量的网卡名 |
| 11 | Ips | string[] | 本地 IP(过滤规则见 DESIGN §10.3),IPv4 先、字符串升序 |

### 4.3 `DiskInfoDto`

| Key | 属性 | 类型 | 含义 |
|---|---|---|---|
| 0 | Mount | string | `/`、`/data`、`C:\` |
| 1 | FsType | string | `ext4`/`NTFS` |
| 2 | TotalMb | uint | |
| 3 | UsedMb | uint | |

### 4.4 `DiskReportDto` — `Key(0) DiskInfoDto[] Disks`

### 4.5 `IpReportDto` — `Key(0) string[] Ips`(规则同 RegisterDto.Ips)

### 4.6 `AgentConfigDto`(下行/注册返回)

| Key | 属性 | 类型 | 范围 | 含义 |
|---|---|---|---|---|
| 0 | HeartbeatSec | ushort | 1..60 | 心跳间隔;探针钳制到范围,越界取默认 2 |
| 1 | IpReportSec | ushort | 60..3600 | 默认 300 |
| 2 | DiskReportSec | ushort | 30..3600 | 默认 60 |
| 3 | ServerUnixSec | uint | | 服务端当前时间(秒),探针仅用于日志打印时钟偏差,不参与任何数据 |

## 5. 浏览器 DTO(字符串键,map 编码,camelCase)

`[MessagePackObject]` + `[Key("camelName")]`;`@msgpack/msgpack` 解码为普通对象,JS 直接 `node.cpu`。`long` 时间为 Unix 毫秒;`ulong` 一律改用 `long`(JS Number 精度足够到 2^53)。可空用 `?`,序列化为 nil → JS `null`。

### 5.1 公共

| 类型 | 字段(键) | 类型 | 含义 |
|---|---|---|---|
| `WavePointDto` | `t` | long | 样本服务器时间 ms |
| | `cpu` | ushort | ‰ |
| | `mem` | ushort | ‰(memUsed/memTotal) |
| | `rx` / `tx` | long | 字节/秒 |
| `NodeStatusEventDto` | `id` | int | 节点 Id |
| | `online` | bool | |
| | `t` | long | 变更时刻 ms |

### 5.2 PublicHub

| 类型 | 字段 | 类型 | 含义 |
|---|---|---|---|
| `PublicNodeTickDto` | `id` | int | |
| | `t` | long | 样本时刻 ms |
| | `cpu` `mem` `disk` | ushort | ‰ |
| | `rx` `tx` | long | 字节/秒 |
| | `load1` | ushort | ×100 |
| | `uptime` | long | 秒 |
| | `traffic` | byte? | 账期用量百分比 0..100(>100 钳 100);无限额或 `public.showTraffic=false` → null |
| `PublicNodeDto` | `id` | int | |
| | `name` | string | PublicName |
| | `group` | string? | GroupName |
| | `cc` | string? | 生效国家码(大写两字母)→ 前端转国旗 Emoji |
| | `online` | bool | |
| | `cores` | ushort | CpuCores |
| | `memMb` | long | MemTotalMb |
| | `diskMb` | long | DiskTotalMb(`public.showDisk=false` → 0 且 tick.disk=0) |
| | `lastSeen` | long? | ms |
| | `last` | PublicNodeTickDto? | 最新样本(离线且从未有样本 → null) |
| | `wave` | WavePointDto[] | 最近 ≤90 点(旧→新) |
| `PublicSnapshotDto` | `t` | long | 服务端时间 ms |
| | `title` | string | `general.publicTitle` |
| | `tickSec` | byte | 广播周期 |
| | `wavePoints` | ushort | 环形缓冲容量 |
| | `nodes` | PublicNodeDto[] | 仅 `Enabled` 节点,按 SortOrder,Id |
| `PublicTickDto` | `t` | long | |
| | `nodes` | PublicNodeTickDto[] | 自上次 tick 以来有新样本的节点 |

服务端→客户端方法:`snapshot(PublicSnapshotDto)`(连接建立时;节点集合/名称/国家/分组/启用变化时全体重推)、`tick(PublicTickDto)`(每 2s,空则不发)、`status(NodeStatusEventDto)`(上下线)。客户端→服务端:`GetSnapshot() : PublicSnapshotDto`。

### 5.3 AdminHub

| 类型 | 字段 | 类型 | 含义 |
|---|---|---|---|
| `AdminNodeTickDto` | PublicNodeTickDto 全部字段 + | | |
| | `memUsedMb` `swapUsedMb` `diskUsedMb` | long | |
| | `rxTotal` `txTotal` | long | 累计计数器原值 |
| | `periodRx` `periodTx` `periodCounted` | long | 当前账期累计与按计费模式计入值 |
| `AdminIpDto` | `ip` | string | |
| | `v` | byte | 4/6 |
| | `src` | string | `agent` / `server` |
| | `public` | bool | 非私网/保留 |
| `AdminDiskDto` | `mount` `fs` | string | |
| | `totalMb` `usedMb` | long | |
| `TrafficPeriodDto` | `start` `end` | long | ms |
| | `rx` `tx` `counted` `limit` | long | 字节;limit 0=不限 |
| | `pct` | double? | |
| | `resetDay` | byte | |
| | `mode` | byte | 0..3 |
| | `tz` | string | |
| `AdminNodeDto` | `id` `publicName` `adminRemark` `group` `sortOrder` `enabled` | | 配置 |
| | `hostname` `os` `kernel` `arch` `cpuModel` `cores` `memMb` `swapMb` `diskMb` `agentVersion` | | 清单 |
| | `ips` | AdminIpDto[] | 合并后 |
| | `publicIp` `cc` `ccAuto` `ccOverride` | string? | |
| | `online` `lastSeen` `registeredAt` | | |
| | `expiresAt` | long? | ms(UTC 零点) |
| | `traffic` | TrafficPeriodDto? | |
| | `disks` | AdminDiskDto[] | |
| | `firing` | string[] | 当前 Firing 的规则键 |
| | `last` | AdminNodeTickDto? | |
| | `wave` | WavePointDto[] | |
| `AdminSnapshotDto` | `t` `nodes` | long / AdminNodeDto[] | 含禁用节点 |
| | `agentsOnline` `browsers` | int | 连接计数 |
| `AdminTickDto` | `t` `nodes` | long / AdminNodeTickDto[] | |
| `AlertEventDto` | `id` `nodeId` `publicName` `rule` `ruleName` | | |
| | `severity` `status` | byte | 1..3 / 1 firing 2 resolved |
| | `firedAt` `resolvedAt` | long / long? | ms |
| | `value` `threshold` | double | |
| | `title` `message` | string | |
| | `notified` | bool | |

服务端→客户端:`snapshot(AdminSnapshotDto)`、`tick(AdminTickDto)`、`status(NodeStatusEventDto)`、`alert(AlertEventDto)`(触发与恢复各一次)。客户端→服务端:`GetSnapshot() : AdminSnapshotDto`、`GetNode(int id) : AdminNodeDto?`。

## 6. 连接、重连与时序规则

### 6.1 探针连接时序

```text
Agent                                            Master
  │ POST /hubs/agent/negotiate  (Authorization: Bearer KEY)   │
  │────────────────────────────────────────────────────────►│ AgentKey 认证:未知→401;节点 Enabled=0→403
  │◄──── {connectionToken, availableTransports} ────────────│
  │ GET /hubs/agent?id=token (WebSocket upgrade, 同头)       │
  │────────────────────────────────────────────────────────►│ OnConnectedAsync: 绑定 NodeId;踢掉同节点旧连接(Abort);
  │ handshake {"protocol":"messagepack","version":2}         │   记录 PublicIp(RemoteIpAddress);GeoIP;若之前离线→广播 status(online)
  │◄──── handshake {} ──────────────────────────────────────│
  │ reg(RegisterDto)  [InvokeAsync, 15s 超时]                │
  │◄──── Completion(AgentConfigDto) ────────────────────────│ 更新清单;返回配置
  │ disk(DiskReportDto) +3s;ip(IpReportDto) +5s              │
  │ hb(HeartbeatDto) 每 HeartbeatSec ......                  │ 每条入口打服务器时间戳
  │◄──── cfg(AgentConfigDto)(仅设置变化时)──────────────────│
  │◄──── Ping 每 10s / ──── Ping 每 10s ───────────────────►│ 任一方 30s 无消息 → 断开
```

### 6.2 重连

- 探针:`WithAutomaticReconnect(AgentRetryPolicy)`(1,2,4,8,16,32,60,60,… 秒 ±20% 抖动,永不放弃);`Reconnected` 事件后**必须重新发送 `reg`**(服务端以连接为单位维护绑定,重连是新连接)。初次 `StartAsync` 失败走同样退避的外层循环。重连期间采集循环继续(保持 CPU 差分基线),但不发送、不缓存。
- 服务端:`OnDisconnectedAsync` 记 `LastDisconnectedUnix`,**不**立即标记离线;离线由告警评估��� `alert.offlineSec`(30s)判定并广播 `status(online=false)`;30s 内重连成功则对外无感。
- 浏览器:`withAutomaticReconnect([0,2000,5000,10000,30000])` 然后前端自行每 30s 重试(`onclose` 里 setTimeout 重新 `start`);`onreconnected` 无需动作(新连接会收到 `snapshot`)。

### 6.3 时间戳、重复与乱序

| 场景 | 处理 |
|---|---|
| 样本时间 | 服务端入口 `UtcNow`;速率优先用 DTO.ElapsedMs(探针单调),否则服务端单调 TickCount 差 |
| 同节点两条连接并存(旧连接未超时) | 新连接建立时 `Abort()` 旧连接;旧连接残留的 `hb/ip/disk` 因 `ConnectionId != state.ConnectionId` **直接丢弃**(Trace 日志) |
| 同一连接内乱序 | WebSocket/LongPolling 单连接保序且 `MaximumParallelInvocationsPerClient=1`,不会发生 |
| 重复心跳(<200 ms 内两条) | 第二条不算速率(`elapsedMs<200`),Delta 仍按增量累加(计数器单调,不会重复计费) |
| 心跳先于 `reg`(重连后探针顺序异常) | 若节点已有清单(`RegisteredAt!=null`)照常处理;否则处理数值但 `MemTotalMb` 未知时 `mem‰` 记 0,等待 `reg` |
| 服务端时钟回拨/前跳 | 桶归属用墙钟(可能产生重复桶 → UPSERT 合并);速率/冷却/离线判定全部用单调时钟(`TickCount64`);账期边界用墙钟 |
| 探针时钟 | 完全不使用 |
| Master 重启 | 节点全部视为离线直至新心跳;`NodeRuntime.LastSeenUnix` 保留用于展示“最后在线” |

## 7. 错误处理

| 层 | 情况 | 行为 |
|---|---|---|
| 认证 | 密钥无效 | negotiate 401,响应体 `{"code":401,"message":"invalid agent key"}`;探针记 Warn(每 10 次失败记一次),退避重试(密钥可能稍后被轮换回来/节点被创建) |
| 认证 | 节点禁用 | 403;探针同上 |
| Hub | `reg` 参数绑定失败(类型不符/版本不兼容) | SignalR 返回 Completion(error);探针记 Error,60s 后重连重试;服务端日志含 ConnectionId、NodeId |
| Hub | `hb` 绑定失败 | 服务端记 Warn(`InvocationBindingFailureMessage`),丢弃 |
| Hub | 数值越界 | 钳制(cpu≤1000、used≤total 等),不报错 |
| Hub | 消息超 64 KB | 连接关闭(SignalR 默认),探针重连 |
| Hub | 服务端关闭(部署) | 发送 `Close(allowReconnect=true)`;探针立即进入重连退避 |
| Hub | 探针收到未知下行方法 | 忽略 + Warn(官方客户端对未注册方法本身只记 Warn) |
| 协议 | 反序列化到未注册类型 | `SnmMessagePackHubProtocolWorker` 抛 `NotSupportedException("Type X is not registered in SnmFormatterRegistry")`(编译期把全部 DTO 列入 registry,单测覆盖) |
| 协议 | 数组长度 > 上限(Ips/Disks/Interfaces 64,字符串 > 1024 字节) | 读取端 `InvalidDataException`;服务端由官方协议 `UntrustedData` + Hub 内钳制共同保护 |
| 浏览器 | JWT 过期 | negotiate 401 → 前端拦截 → 刷新 token 后重连(FRONTEND.md §4) |
| 浏览器 | 大屏关闭 | `HubException` 文案 `public dashboard disabled` → 页面显示“看板已关闭” |

## 8. C# 契约草案(`src/SNM.Contracts`)

### 8.0 项目文件

```xml
<!-- src/SNM.Contracts/SNM.Contracts.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsAotCompatible>true</IsAotCompatible>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <RootNamespace>SNM.Contracts</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MessagePack" />                              <!-- 2.5.302 -->
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Common" />      <!-- 10.0.11: IHubProtocol / HubMessage / HubProtocolConstants -->
  </ItemGroup>
</Project>
```

`Directory.Packages.props` 需新增 `Microsoft.AspNetCore.SignalR.Common` 10.0.11(理由:`IHubProtocol` 与 `HubMessage` 家族定义于此;它是 SignalR.Client 的传递依赖,版本一致)。**禁止**在 Contracts/Agent 引用 `Microsoft.AspNetCore.SignalR.Protocols.MessagePack`。

### 8.1 常量

```csharp
namespace SNM.Contracts;

public static class HubPaths
{
    public const string Agent = "/hubs/agent";
    public const string Public = "/hubs/public";
    public const string Admin = "/hubs/admin";
}

/// <summary>Wire names are deliberately short on the agent hub to keep the heartbeat frame under 50 bytes.</summary>
public static class AgentHubMethods
{
    // client -> server
    public const string Register = "reg";
    public const string Heartbeat = "hb";
    public const string ReportIps = "ip";
    public const string ReportDisks = "disk";
    // server -> client (the ONLY downlink message)
    public const string ApplyConfig = "cfg";
}

public static class BrowserHubMethods
{
    // server -> client
    public const string Snapshot = "snapshot";
    public const string Tick = "tick";
    public const string Status = "status";
    public const string Alert = "alert";           // admin only
    // client -> server
    public const string GetSnapshot = "GetSnapshot";
    public const string GetNode = "GetNode";       // admin only
}

public static class Units
{
    public const int CpuPermilleMax = 1000;
    public const long MiB = 1024 * 1024;
    public const int MaxDisks = 64;
    public const int MaxInterfaces = 64;
    public const int MaxIps = 64;
    public const int MaxStringBytes = 1024;
}

public static class AgentDefaults
{
    public const ushort HeartbeatSec = 2, HeartbeatSecMin = 1, HeartbeatSecMax = 60;
    public const ushort IpReportSec = 300, IpReportSecMin = 60, IpReportSecMax = 3600;
    public const ushort DiskReportSec = 60, DiskReportSecMin = 30, DiskReportSecMax = 3600;
}

public static class ProtocolInfo
{
    public const string Name = "messagepack";   // must equal the official protocol name
    public const int Version = 2;               // must equal the official protocol version
}
```

### 8.2 探针 DTO

```csharp
using MessagePack;
namespace SNM.Contracts;

[MessagePackObject]
public sealed class HeartbeatDto
{
    [Key(0)] public ushort Cpu { get; set; }          // permille 0..1000
    [Key(1)] public uint MemUsedMb { get; set; }
    [Key(2)] public uint SwapUsedMb { get; set; }
    [Key(3)] public ushort Load1 { get; set; }        // load average x100, 0 on Windows
    [Key(4)] public uint DiskUsedMb { get; set; }
    [Key(5)] public ulong NetRxBytes { get; set; }    // raw kernel counters summed over counted NICs
    [Key(6)] public ulong NetTxBytes { get; set; }
    [Key(7)] public uint UptimeSec { get; set; }
    [Key(8)] public ushort ElapsedMs { get; set; }    // monotonic interval since previous sample, 0 for the first
}

[MessagePackObject]
public sealed class DiskInfoDto
{
    [Key(0)] public string Mount { get; set; } = "";
    [Key(1)] public string FsType { get; set; } = "";
    [Key(2)] public uint TotalMb { get; set; }
    [Key(3)] public uint UsedMb { get; set; }
}

[MessagePackObject]
public sealed class RegisterDto
{
    [Key(0)] public string AgentVersion { get; set; } = "";
    [Key(1)] public string Hostname { get; set; } = "";
    [Key(2)] public string Os { get; set; } = "";
    [Key(3)] public string Kernel { get; set; } = "";
    [Key(4)] public string Arch { get; set; } = "";
    [Key(5)] public string CpuModel { get; set; } = "";   // "2x Intel(R) Xeon(R) ..." for multi-socket
    [Key(6)] public ushort CpuCores { get; set; }
    [Key(7)] public uint MemTotalMb { get; set; }
    [Key(8)] public uint SwapTotalMb { get; set; }
    [Key(9)] public DiskInfoDto[] Disks { get; set; } = [];
    [Key(10)] public string[] Interfaces { get; set; } = [];
    [Key(11)] public string[] Ips { get; set; } = [];
}

[MessagePackObject] public sealed class DiskReportDto { [Key(0)] public DiskInfoDto[] Disks { get; set; } = []; }
[MessagePackObject] public sealed class IpReportDto   { [Key(0)] public string[] Ips { get; set; } = []; }

[MessagePackObject]
public sealed class AgentConfigDto
{
    [Key(0)] public ushort HeartbeatSec { get; set; } = AgentDefaults.HeartbeatSec;
    [Key(1)] public ushort IpReportSec { get; set; } = AgentDefaults.IpReportSec;
    [Key(2)] public ushort DiskReportSec { get; set; } = AgentDefaults.DiskReportSec;
    [Key(3)] public uint ServerUnixSec { get; set; }   // informational only (clock skew logging)
}
```

### 8.3 浏览器 DTO(仅供 Master 使用;探针不引用)

```csharp
using MessagePack;
namespace SNM.Contracts.Browser;

[MessagePackObject] public sealed class WavePointDto
{ [Key("t")] public long T { get; set; } [Key("cpu")] public ushort Cpu { get; set; } [Key("mem")] public ushort Mem { get; set; }
  [Key("rx")] public long Rx { get; set; } [Key("tx")] public long Tx { get; set; } }

[MessagePackObject] public sealed class NodeStatusEventDto
{ [Key("id")] public int Id { get; set; } [Key("online")] public bool Online { get; set; } [Key("t")] public long T { get; set; } }

[MessagePackObject] public class PublicNodeTickDto
{ [Key("id")] public int Id { get; set; } [Key("t")] public long T { get; set; }
  [Key("cpu")] public ushort Cpu { get; set; } [Key("mem")] public ushort Mem { get; set; } [Key("disk")] public ushort Disk { get; set; }
  [Key("rx")] public long Rx { get; set; } [Key("tx")] public long Tx { get; set; } [Key("load1")] public ushort Load1 { get; set; }
  [Key("uptime")] public long Uptime { get; set; } [Key("traffic")] public byte? Traffic { get; set; } }

[MessagePackObject] public sealed class PublicNodeDto
{ [Key("id")] public int Id { get; set; } [Key("name")] public string Name { get; set; } = ""; [Key("group")] public string? Group { get; set; }
  [Key("cc")] public string? Cc { get; set; } [Key("online")] public bool Online { get; set; } [Key("cores")] public ushort Cores { get; set; }
  [Key("memMb")] public long MemMb { get; set; } [Key("diskMb")] public long DiskMb { get; set; } [Key("lastSeen")] public long? LastSeen { get; set; }
  [Key("last")] public PublicNodeTickDto? Last { get; set; } [Key("wave")] public WavePointDto[] Wave { get; set; } = []; }

[MessagePackObject] public sealed class PublicSnapshotDto
{ [Key("t")] public long T { get; set; } [Key("title")] public string Title { get; set; } = ""; [Key("tickSec")] public byte TickSec { get; set; }
  [Key("wavePoints")] public ushort WavePoints { get; set; } [Key("nodes")] public PublicNodeDto[] Nodes { get; set; } = []; }

[MessagePackObject] public sealed class PublicTickDto
{ [Key("t")] public long T { get; set; } [Key("nodes")] public PublicNodeTickDto[] Nodes { get; set; } = []; }

[MessagePackObject] public sealed class AdminNodeTickDto : PublicNodeTickDto
{ [Key("memUsedMb")] public long MemUsedMb { get; set; } [Key("swapUsedMb")] public long SwapUsedMb { get; set; } [Key("diskUsedMb")] public long DiskUsedMb { get; set; }
  [Key("rxTotal")] public long RxTotal { get; set; } [Key("txTotal")] public long TxTotal { get; set; }
  [Key("periodRx")] public long PeriodRx { get; set; } [Key("periodTx")] public long PeriodTx { get; set; } [Key("periodCounted")] public long PeriodCounted { get; set; } }

[MessagePackObject] public sealed class AdminIpDto
{ [Key("ip")] public string Ip { get; set; } = ""; [Key("v")] public byte V { get; set; } [Key("src")] public string Src { get; set; } = "agent"; [Key("public")] public bool Public { get; set; } }

[MessagePackObject] public sealed class AdminDiskDto
{ [Key("mount")] public string Mount { get; set; } = ""; [Key("fs")] public string Fs { get; set; } = ""; [Key("totalMb")] public long TotalMb { get; set; } [Key("usedMb")] public long UsedMb { get; set; } }

[MessagePackObject] public sealed class TrafficPeriodDto
{ [Key("start")] public long Start { get; set; } [Key("end")] public long End { get; set; } [Key("rx")] public long Rx { get; set; } [Key("tx")] public long Tx { get; set; }
  [Key("counted")] public long Counted { get; set; } [Key("limit")] public long Limit { get; set; } [Key("pct")] public double? Pct { get; set; }
  [Key("resetDay")] public byte ResetDay { get; set; } [Key("mode")] public byte Mode { get; set; } [Key("tz")] public string Tz { get; set; } = ""; }

[MessagePackObject] public sealed class AdminNodeDto
{ [Key("id")] public int Id { get; set; } [Key("publicName")] public string PublicName { get; set; } = ""; [Key("adminRemark")] public string? AdminRemark { get; set; }
  [Key("group")] public string? Group { get; set; } [Key("sortOrder")] public int SortOrder { get; set; } [Key("enabled")] public bool Enabled { get; set; }
  [Key("hostname")] public string? Hostname { get; set; } [Key("os")] public string? Os { get; set; } [Key("kernel")] public string? Kernel { get; set; } [Key("arch")] public string? Arch { get; set; }
  [Key("cpuModel")] public string? CpuModel { get; set; } [Key("cores")] public ushort Cores { get; set; } [Key("memMb")] public long MemMb { get; set; } [Key("swapMb")] public long SwapMb { get; set; }
  [Key("diskMb")] public long DiskMb { get; set; } [Key("agentVersion")] public string? AgentVersion { get; set; } [Key("ips")] public AdminIpDto[] Ips { get; set; } = [];
  [Key("publicIp")] public string? PublicIp { get; set; } [Key("cc")] public string? Cc { get; set; } [Key("ccAuto")] public string? CcAuto { get; set; } [Key("ccOverride")] public string? CcOverride { get; set; }
  [Key("online")] public bool Online { get; set; } [Key("lastSeen")] public long? LastSeen { get; set; } [Key("registeredAt")] public long? RegisteredAt { get; set; } [Key("expiresAt")] public long? ExpiresAt { get; set; }
  [Key("traffic")] public TrafficPeriodDto? Traffic { get; set; } [Key("disks")] public AdminDiskDto[] Disks { get; set; } = []; [Key("firing")] public string[] Firing { get; set; } = [];
  [Key("last")] public AdminNodeTickDto? Last { get; set; } [Key("wave")] public WavePointDto[] Wave { get; set; } = []; }

[MessagePackObject] public sealed class AdminSnapshotDto
{ [Key("t")] public long T { get; set; } [Key("nodes")] public AdminNodeDto[] Nodes { get; set; } = []; [Key("agentsOnline")] public int AgentsOnline { get; set; } [Key("browsers")] public int Browsers { get; set; } }

[MessagePackObject] public sealed class AdminTickDto
{ [Key("t")] public long T { get; set; } [Key("nodes")] public AdminNodeTickDto[] Nodes { get; set; } = []; }

[MessagePackObject] public sealed class AlertEventDto
{ [Key("id")] public long Id { get; set; } [Key("nodeId")] public int NodeId { get; set; } [Key("publicName")] public string PublicName { get; set; } = "";
  [Key("rule")] public string Rule { get; set; } = ""; [Key("ruleName")] public string RuleName { get; set; } = ""; [Key("severity")] public byte Severity { get; set; } [Key("status")] public byte Status { get; set; }
  [Key("firedAt")] public long FiredAt { get; set; } [Key("resolvedAt")] public long? ResolvedAt { get; set; } [Key("value")] public double Value { get; set; } [Key("threshold")] public double Threshold { get; set; }
  [Key("title")] public string Title { get; set; } = ""; [Key("message")] public string Message { get; set; } = ""; [Key("notified")] public bool Notified { get; set; } }
```

注意:`AdminNodeTickDto : PublicNodeTickDto` 继承时官方解析器把基类键一并写入 map;浏览器无感。这些类型**不**进入 `SnmFormatterRegistry`(探针永不序列化它们)。

### 8.4 手写 formatter(与官方 `DynamicObjectResolver` 输出逐字节一致)

规则:非空对象 → `WriteArrayHeader(最大Key+1)` 后按 Key 顺序写;`null` 引用 → `WriteNil`;无符号整数用 `writer.Write(x)`(自动最短编码,与官方 `UInt16/32/64Formatter` 相同);字符串 `writer.Write(string?)`(null→nil);数组 null→nil,否则 `WriteArrayHeader(len)`+元素。读取:`TryReadNil` → null;`ReadArrayHeader` 后按索引 switch,多余项 `Skip()`,缺失项保持默认;越界长度抛 `InvalidDataException`。**不引用** `MessagePackSerializerOptions.Standard`、`StandardResolver`、`MessagePackSerializer`。

```csharp
using System.IO;
using MessagePack;
using MessagePack.Formatters;
namespace SNM.Contracts.Serialization;

public sealed class HeartbeatDtoFormatter : IMessagePackFormatter<HeartbeatDto?>
{
    public static readonly HeartbeatDtoFormatter Instance = new();
    public void Serialize(ref MessagePackWriter w, HeartbeatDto? v, MessagePackSerializerOptions o)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(9);
        w.Write(v.Cpu); w.Write(v.MemUsedMb); w.Write(v.SwapUsedMb); w.Write(v.Load1); w.Write(v.DiskUsedMb);
        w.Write(v.NetRxBytes); w.Write(v.NetTxBytes); w.Write(v.UptimeSec); w.Write(v.ElapsedMs);
    }
    public HeartbeatDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions o)
    {
        if (r.TryReadNil()) return null;
        var n = r.ReadArrayHeader(); var v = new HeartbeatDto();
        for (var i = 0; i < n; i++)
            switch (i)
            {
                case 0: v.Cpu = r.ReadUInt16(); break;      case 1: v.MemUsedMb = r.ReadUInt32(); break;
                case 2: v.SwapUsedMb = r.ReadUInt32(); break; case 3: v.Load1 = r.ReadUInt16(); break;
                case 4: v.DiskUsedMb = r.ReadUInt32(); break; case 5: v.NetRxBytes = r.ReadUInt64(); break;
                case 6: v.NetTxBytes = r.ReadUInt64(); break; case 7: v.UptimeSec = r.ReadUInt32(); break;
                case 8: v.ElapsedMs = r.ReadUInt16(); break;  default: r.Skip(); break;
            }
        return v;
    }
}

public sealed class DiskInfoDtoFormatter : IMessagePackFormatter<DiskInfoDto?>
{
    public static readonly DiskInfoDtoFormatter Instance = new();
    public void Serialize(ref MessagePackWriter w, DiskInfoDto? v, MessagePackSerializerOptions o)
    { if (v is null) { w.WriteNil(); return; } w.WriteArrayHeader(4); w.Write(v.Mount); w.Write(v.FsType); w.Write(v.TotalMb); w.Write(v.UsedMb); }
    public DiskInfoDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions o)
    {
        if (r.TryReadNil()) return null;
        var n = r.ReadArrayHeader(); var v = new DiskInfoDto();
        for (var i = 0; i < n; i++)
            switch (i)
            {
                case 0: v.Mount = Prim.ReadString(ref r); break; case 1: v.FsType = Prim.ReadString(ref r); break;
                case 2: v.TotalMb = r.ReadUInt32(); break;      case 3: v.UsedMb = r.ReadUInt32(); break;
                default: r.Skip(); break;
            }
        return v;
    }
}

public sealed class RegisterDtoFormatter : IMessagePackFormatter<RegisterDto?>
{
    public static readonly RegisterDtoFormatter Instance = new();
    public void Serialize(ref MessagePackWriter w, RegisterDto? v, MessagePackSerializerOptions o)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(12);
        w.Write(v.AgentVersion); w.Write(v.Hostname); w.Write(v.Os); w.Write(v.Kernel); w.Write(v.Arch); w.Write(v.CpuModel);
        w.Write(v.CpuCores); w.Write(v.MemTotalMb); w.Write(v.SwapTotalMb);
        Prim.WriteArray(ref w, v.Disks, DiskInfoDtoFormatter.Instance); Prim.WriteStrings(ref w, v.Interfaces); Prim.WriteStrings(ref w, v.Ips);
    }
    public RegisterDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions o)
    {
        if (r.TryReadNil()) return null;
        var n = r.ReadArrayHeader(); var v = new RegisterDto();
        for (var i = 0; i < n; i++)
            switch (i)
            {
                case 0: v.AgentVersion = Prim.ReadString(ref r); break; case 1: v.Hostname = Prim.ReadString(ref r); break;
                case 2: v.Os = Prim.ReadString(ref r); break;           case 3: v.Kernel = Prim.ReadString(ref r); break;
                case 4: v.Arch = Prim.ReadString(ref r); break;         case 5: v.CpuModel = Prim.ReadString(ref r); break;
                case 6: v.CpuCores = r.ReadUInt16(); break;             case 7: v.MemTotalMb = r.ReadUInt32(); break;
                case 8: v.SwapTotalMb = r.ReadUInt32(); break;
                case 9: v.Disks = Prim.ReadArray(ref r, DiskInfoDtoFormatter.Instance, Units.MaxDisks); break;
                case 10: v.Interfaces = Prim.ReadStrings(ref r, Units.MaxInterfaces); break;
                case 11: v.Ips = Prim.ReadStrings(ref r, Units.MaxIps); break;
                default: r.Skip(); break;
            }
        return v;
    }
}

public sealed class DiskReportDtoFormatter : IMessagePackFormatter<DiskReportDto?>
{
    public static readonly DiskReportDtoFormatter Instance = new();
    public void Serialize(ref MessagePackWriter w, DiskReportDto? v, MessagePackSerializerOptions o)
    { if (v is null) { w.WriteNil(); return; } w.WriteArrayHeader(1); Prim.WriteArray(ref w, v.Disks, DiskInfoDtoFormatter.Instance); }
    public DiskReportDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions o)
    {
        if (r.TryReadNil()) return null;
        var n = r.ReadArrayHeader(); var v = new DiskReportDto();
        for (var i = 0; i < n; i++) { if (i == 0) v.Disks = Prim.ReadArray(ref r, DiskInfoDtoFormatter.Instance, Units.MaxDisks); else r.Skip(); }
        return v;
    }
}

public sealed class IpReportDtoFormatter : IMessagePackFormatter<IpReportDto?>
{
    public static readonly IpReportDtoFormatter Instance = new();
    public void Serialize(ref MessagePackWriter w, IpReportDto? v, MessagePackSerializerOptions o)
    { if (v is null) { w.WriteNil(); return; } w.WriteArrayHeader(1); Prim.WriteStrings(ref w, v.Ips); }
    public IpReportDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions o)
    {
        if (r.TryReadNil()) return null;
        var n = r.ReadArrayHeader(); var v = new IpReportDto();
        for (var i = 0; i < n; i++) { if (i == 0) v.Ips = Prim.ReadStrings(ref r, Units.MaxIps); else r.Skip(); }
        return v;
    }
}

public sealed class AgentConfigDtoFormatter : IMessagePackFormatter<AgentConfigDto?>
{
    public static readonly AgentConfigDtoFormatter Instance = new();
    public void Serialize(ref MessagePackWriter w, AgentConfigDto? v, MessagePackSerializerOptions o)
    { if (v is null) { w.WriteNil(); return; } w.WriteArrayHeader(4); w.Write(v.HeartbeatSec); w.Write(v.IpReportSec); w.Write(v.DiskReportSec); w.Write(v.ServerUnixSec); }
    public AgentConfigDto? Deserialize(ref MessagePackReader r, MessagePackSerializerOptions o)
    {
        if (r.TryReadNil()) return null;
        var n = r.ReadArrayHeader(); var v = new AgentConfigDto();
        for (var i = 0; i < n; i++)
            switch (i)
            {
                case 0: v.HeartbeatSec = r.ReadUInt16(); break; case 1: v.IpReportSec = r.ReadUInt16(); break;
                case 2: v.DiskReportSec = r.ReadUInt16(); break; case 3: v.ServerUnixSec = r.ReadUInt32(); break;
                default: r.Skip(); break;
            }
        return v;
    }
}

/// <summary>Primitive helpers shared by formatters. No options/resolver usage.</summary>
internal static class Prim
{
    public static string ReadString(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return "";
        if (r.TryReadStringSpan(out var span))                       // contiguous fast path (advances the reader)
        {
            if (span.Length > Units.MaxStringBytes) throw new InvalidDataException("string too long");
            return System.Text.Encoding.UTF8.GetString(span);
        }
        var s = r.ReadString() ?? "";                                // multi-segment sequence path (TryReadStringSpan did not advance)
        if (System.Text.Encoding.UTF8.GetByteCount(s) > Units.MaxStringBytes) throw new InvalidDataException("string too long");
        return s;
    }
    public static void WriteStrings(ref MessagePackWriter w, string[]? a)
    { if (a is null) { w.WriteNil(); return; } w.WriteArrayHeader(a.Length); foreach (var s in a) w.Write(s); }
    public static string[] ReadStrings(ref MessagePackReader r, int max)
    {
        if (r.TryReadNil()) return [];
        var n = r.ReadArrayHeader(); if (n > max) throw new InvalidDataException($"array length {n} exceeds {max}");
        var a = new string[n]; for (var i = 0; i < n; i++) a[i] = r.ReadString() ?? ""; return a;
    }
    public static void WriteArray<T>(ref MessagePackWriter w, T[]? a, IMessagePackFormatter<T?> f) where T : class
    { if (a is null) { w.WriteNil(); return; } w.WriteArrayHeader(a.Length); foreach (var x in a) f.Serialize(ref w, x, null!); }
    public static T[] ReadArray<T>(ref MessagePackReader r, IMessagePackFormatter<T?> f, int max) where T : class, new()
    {
        if (r.TryReadNil()) return [];
        var n = r.ReadArrayHeader(); if (n > max) throw new InvalidDataException($"array length {n} exceeds {max}");
        var a = new T[n]; for (var i = 0; i < n; i++) a[i] = f.Deserialize(ref r, null!) ?? new T(); return a;
    }
}
```

> 实现提示:`Prim.ReadString` 先处理 nil,再走 `TryReadStringSpan` 连续缓冲快路径(成功即已前进),失败(字符串跨多段 `ReadOnlySequence`)时 `ReadString()` 读取;单测必须构造多段 `ReadOnlySequence<byte>` 覆盖两条路径。`IMessagePackFormatter<T>.Serialize/Deserialize` 的 `options` 参数在探针路径为 `null!`,所有 formatter 不得触碰它。

### 8.5 类型注册表、Worker 与协议

```csharp
namespace SNM.Contracts.Serialization;

/// <summary>Static type switch — the only dispatch mechanism (no reflection, no resolvers).</summary>
public static class SnmFormatterRegistry
{
    public static bool TryWrite(ref MessagePackWriter w, Type type, object value)
    {
        if (type == typeof(HeartbeatDto))   { HeartbeatDtoFormatter.Instance.Serialize(ref w, (HeartbeatDto)value, null!); return true; }
        if (type == typeof(RegisterDto))    { RegisterDtoFormatter.Instance.Serialize(ref w, (RegisterDto)value, null!); return true; }
        if (type == typeof(IpReportDto))    { IpReportDtoFormatter.Instance.Serialize(ref w, (IpReportDto)value, null!); return true; }
        if (type == typeof(DiskReportDto))  { DiskReportDtoFormatter.Instance.Serialize(ref w, (DiskReportDto)value, null!); return true; }
        if (type == typeof(DiskInfoDto))    { DiskInfoDtoFormatter.Instance.Serialize(ref w, (DiskInfoDto)value, null!); return true; }
        if (type == typeof(AgentConfigDto)) { AgentConfigDtoFormatter.Instance.Serialize(ref w, (AgentConfigDto)value, null!); return true; }
        if (type == typeof(string))         { w.Write((string)value); return true; }
        if (type == typeof(int))            { w.Write((int)value); return true; }
        if (type == typeof(bool))           { w.Write((bool)value); return true; }
        return false;
    }
    public static bool TryRead(ref MessagePackReader r, Type type, out object? value)
    {
        if (type == typeof(AgentConfigDto)) { value = AgentConfigDtoFormatter.Instance.Deserialize(ref r, null!); return true; }
        if (type == typeof(HeartbeatDto))   { value = HeartbeatDtoFormatter.Instance.Deserialize(ref r, null!); return true; }
        if (type == typeof(RegisterDto))    { value = RegisterDtoFormatter.Instance.Deserialize(ref r, null!); return true; }
        if (type == typeof(IpReportDto))    { value = IpReportDtoFormatter.Instance.Deserialize(ref r, null!); return true; }
        if (type == typeof(DiskReportDto))  { value = DiskReportDtoFormatter.Instance.Deserialize(ref r, null!); return true; }
        if (type == typeof(DiskInfoDto))    { value = DiskInfoDtoFormatter.Instance.Deserialize(ref r, null!); return true; }
        if (type == typeof(string))         { value = r.ReadString(); return true; }
        if (type == typeof(int))            { value = r.ReadInt32(); return true; }
        if (type == typeof(bool))           { value = r.ReadBoolean(); return true; }
        if (type == typeof(object))         { r.Skip(); value = null; return true; }   // untyped completion payloads
        value = null; return false;
    }
}

internal sealed class SnmMessagePackHubProtocolWorker : SNM.Contracts.Internal.MessagePackHubProtocolWorker
{
    protected override object? DeserializeObject(ref MessagePackReader reader, Type type, string field)
    {
        if (SnmFormatterRegistry.TryRead(ref reader, type, out var v)) return v;
        throw new NotSupportedException($"Type '{type.FullName}' for '{field}' is not registered in SnmFormatterRegistry.");
    }
    protected override void Serialize(ref MessagePackWriter writer, Type type, object value)
    {
        if (!SnmFormatterRegistry.TryWrite(ref writer, type, value))
            throw new NotSupportedException($"Type '{type.FullName}' is not registered in SnmFormatterRegistry.");
    }
}

/// <summary>AOT-friendly IHubProtocol, wire-compatible with the official MessagePack hub protocol (name "messagepack", version 2).</summary>
public sealed class SnmMessagePackHubProtocol : Microsoft.AspNetCore.SignalR.Protocol.IHubProtocol
{
    private readonly SnmMessagePackHubProtocolWorker _worker = new();
    public string Name => ProtocolInfo.Name;
    public int Version => ProtocolInfo.Version;
    public Microsoft.AspNetCore.Connections.TransferFormat TransferFormat => Microsoft.AspNetCore.Connections.TransferFormat.Binary;
    public bool IsVersionSupported(int version) => version <= Version;
    public bool TryParseMessage(ref System.Buffers.ReadOnlySequence<byte> input, Microsoft.AspNetCore.SignalR.IInvocationBinder binder,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Microsoft.AspNetCore.SignalR.Protocol.HubMessage? message)
        => _worker.TryParseMessage(ref input, binder, out message);
    public void WriteMessage(Microsoft.AspNetCore.SignalR.Protocol.HubMessage message, System.Buffers.IBufferWriter<byte> output) => _worker.WriteMessage(message, output);
    public ReadOnlyMemory<byte> GetMessageBytes(Microsoft.AspNetCore.SignalR.Protocol.HubMessage message) => _worker.GetMessageBytes(message);
}
```

**Vendored 文件**(目录 `src/SNM.Contracts/Internal/`,命名空间改为 `SNM.Contracts.Internal`,保留 MIT 头注释,项目根放 `THIRD-PARTY-NOTICES.txt`):`MessagePackHubProtocolWorker.cs`(abstract,原样;`ProtocolHelper.TryGetReturnType` 改为本地 `static Type? TryGetReturnType(IInvocationBinder b, string id) { try { return b.GetReturnType(id); } catch { return null; } }`;保留 `#if NETCOREAPP` 的 `TryReadStringSpan` 分支)、`BinaryMessageParser.cs`、`BinaryMessageFormatter.cs`、`MemoryBufferWriter.cs`。来源:`spikes/aot-messagepack/upstream-reference/`(dotnet/aspnetcore `release/10.0`)。

### 8.6 探针接线

```csharp
var builder = new HubConnectionBuilder()
    .WithUrl(new Uri(new Uri(serverBase), HubPaths.Agent), o =>
    {
        o.AccessTokenProvider = () => Task.FromResult<string?>(agentKey);      // -> Authorization: Bearer <key>
        o.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
        if (proxy is not null) o.Proxy = proxy;                                 // WebProxy("http://..." | "socks5://...")
        o.Headers["X-SNM-Agent"] = agentVersion;
    })
    .WithServerTimeout(TimeSpan.FromSeconds(30))
    .WithKeepAliveInterval(TimeSpan.FromSeconds(10))
    .WithAutomaticReconnect(new AgentRetryPolicy());
builder.Services.RemoveAll<IHubProtocol>();                                     // drop JsonHubProtocol so it is trimmed away
builder.Services.AddSingleton<IHubProtocol, SnmMessagePackHubProtocol>();
var conn = builder.Build();
conn.On<AgentConfigDto>(AgentHubMethods.ApplyConfig, cfg => scheduler.Apply(cfg));
// after connect / reconnect:
var cfg = await conn.InvokeAsync<AgentConfigDto>(AgentHubMethods.Register, registerDto, ct);
// every tick:
await conn.SendAsync(AgentHubMethods.Heartbeat, heartbeatDto, ct);
```

`Build()` 只解析单个 `IHubProtocol`(最后注册者生效);`RemoveAll` 让 `JsonHubProtocol` 与 `System.Text.Json` 反射路径被裁剪。`HubConnection.On<T>` / `InvokeAsync<T>` / `SendAsync` 在 10.0.11 无 `RequiresUnreferencedCode/RequiresDynamicCode` 标注(已核对包 XML 文档为 0 处)。

### 8.7 Contracts 测试(tests/SNM.Contracts.Tests)

1. `ByteEquivalence_<Dto>`:`MessagePackSerializer.Serialize(dto, MessagePackSerializerOptions.Standard)`(官方,测试项目可用反射)与手写 formatter 输出 `SequenceEqual`;200 组随机值 + 边界(0、Max、空数组、空串、`Ips` 64 项)。
2. `RoundTrip_<Dto>`:官方写→手写读、手写写→官方读,属性逐一相等。
3. `Protocol_Equivalence`:对 `InvocationMessage("hb",[dto])`、`InvocationMessage("reg",[dto], invocationId="1")`、`CompletionMessage(result: AgentConfigDto)`、`CompletionMessage(error)`、`PingMessage`、`CloseMessage(allowReconnect:true)`、`AckMessage(5)`、`SequenceMessage(7)`,`SnmMessagePackHubProtocol.GetMessageBytes` 与 `new MessagePackHubProtocol().GetMessageBytes` 相等;`TryParseMessage` 交叉解析(`IInvocationBinder` 测试桩返回对应类型)。
4. `Partial_Frame_Returns_False`、`Unknown_Type_Throws_NotSupported`、`Oversized_Array_Throws_InvalidData`、`Forward_Compat_Extra_Fields_Skipped`(用 `MessagePackWriter` 手工写 10 元素数组解析为 HeartbeatDto 成功)。
5. `Heartbeat_Size_Budget`:典型值 body ≤ 40 B、帧 ≤ 50 B(回归保护 47 B 实测)。

