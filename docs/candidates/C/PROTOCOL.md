# PROTOCOL.md — 通信协议与契约(候选设计 C)

> 本文是 Agent ⇄ Master、浏览器 ⇄ Master 之间**唯一的接口契约**。所有 DTO、Hub 路径、方法名、单位、字节格式在此定案;`src/SNM.Contracts` 的代码必须与第 9 节的草案一致(允许补充 XML 注释,不允许改字段/Key/类型)。
>
> 术语:**Agent** = 探针(.NET 10 Native AOT 控制台);**Master** = 服务端;**Public** = 匿名大屏浏览器;**Admin** = 管理后台浏览器。

---

## 0. 决策摘要(对应 BRIEF §3 第 1、2、7 题)

| 问题 | 结论 |
|---|---|
| Q1 MessagePack + Native AOT 路径 | **采用 BRIEF 候选路径 1(vendoring)**:在 `SNM.Contracts/Protocol/Vendored/` 复制 dotnet/aspnetcore(MIT)的 `MessagePackHubProtocolWorker.cs`、`BinaryMessageParser.cs`、`BinaryMessageFormatter.cs`、`MemoryBufferWriter.cs`,实现 `StaticMessagePackHubProtocol : IHubProtocol`(`Name="messagepack"`,`Version=2`,`TransferFormat.Binary`,线格式与官方完全一致)。`Serialize/DeserializeObject` 对**封闭的已知类型集合**做 `switch` 静态分派,调用手写的 `AgentFormatters.WriteXxx/ReadXxx`(只用 `MessagePackWriter`/`MessagePackReader`)。Agent 代码路径**不引用** `MessagePackSerializer`、任何 `*Resolver`、`MessagePackSerializerOptions`,因此 MessagePack.dll 内部的动态代码被裁掉,ILC 输出 0 条 IL 警告。Master 与浏览器继续使用官方 `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` / `@microsoft/signalr-protocol-msgpack`。手写 formatter 与 `[MessagePackObject]/[Key]` 契约的字节级等价由 `tests/SNM.Contracts.Tests` 在 JIT 下用反射 resolver 交叉验证(§8)。**当前不需要任何 `UnconditionalSuppressMessage`**;若实现阶段出现,必须在本文 §8.4 登记理由。 |
| Q2 心跳/注册 DTO | 注册 `NodeInfo`(静态信息,连接建立后通过 `Hello` 上报,返回 `HelloResult` 配置)与心跳 `Heartbeat`(10 个字段,全部无符号整数,**实测典型 40 字节**、小型 VPS 28 字节、双盘+超 4GB 计数器 59 字节;含 SignalR 调用帧后典型 50 字节,方法名取 `"Hb"` 即为此)分离;IP 列表 `IpReport` 每 300 s 单独上报。所有 DTO **不含 DateTime**,时间戳一律由 Master 在收到时打 `DateTimeOffset.UtcNow`。 |
| Q7 实时推送形状 | Public/Admin Hub 均为"连接即推快照 + 每 2 s 一次批量 `Tick` 增量 + 结构变更事件(`NodeChanged`/`NodeRemoved`)+ 告警事件(仅 Admin)"。Master 为每节点维护容量 90 点(3 分钟)的环形缓冲,快照携带最近 60 点。面向浏览器的 DTO 使用**字符串 Key(camelCase)**,序列化为 msgpack map,JS 端直接得到对象;面向 Agent 的 DTO 使用**整数 Key**,序列化为 msgpack array,追求最小字节。 |

---

## 1. 总体拓扑

```
┌──────────────┐   /hubs/agent  (WebSocket|LongPolling, msgpack, Bearer=AgentKey)   ┌──────────────┐
│  SNM.Agent   │ ─────────────────────────────────────────────────────────────────▶ │              │
│ (AOT, 静态协议)│ ◀──── ApplyConfig(AgentConfig) 仅配置类下行 ─────────────────────── │  SNM.Master  │
└──────────────┘                                                                    │              │
┌──────────────┐   /hubs/public (WebSocket, msgpack, 匿名)                            │  官方 msgpack │
│ 公开大屏 JS   │ ◀──── Snapshot / Tick / NodeChanged / NodeRemoved ────────────────── │  协议         │
└──────────────┘                                                                    │              │
┌──────────────┐   /hubs/admin  (WebSocket, msgpack, JWT access_token)               │              │
│ 管理后台 Vue  │ ◀──── Snapshot / Tick / NodeChanged / NodeRemoved / Alert ────────── │              │
│              │ ────── GetSnapshot() ───────────────────────────────────────────────▶ │              │
└──────────────┘                                                                    └──────────────┘
```

- 三个 Hub 都只启用 MessagePack 协议(Master 侧 `AddSignalR().AddMessagePackProtocol()`;浏览器使用 `withHubProtocol(new MessagePackHubProtocol())`;Agent 使用 `StaticMessagePackHubProtocol`)。JSON 协议在 Master 侧通过 `SupportedProtocols = ["messagepack"]` 关闭,避免误用。
- 传输:Agent 允许 `WebSockets | LongPolling`(msgpack 为二进制,SSE 自动被 SignalR 排除);浏览器默认自动协商。
- Agent 绝不监听端口;Master → Agent 的下行消息**仅有一条**:`ApplyConfig(AgentConfig)`(§4.3),外加 `Hello` 的返回值 `HelloResult`(也是配置)。

---

## 2. 单位与类型约定

| 量 | C# 类型 | 单位/编码 | 说明 |
|---|---|---|---|
| CPU 使用率 | `ushort` | 千分比 0–1000 | 全部逻辑核聚合;`1000` = 100% |
| 1 分钟负载 | `ushort` | ×100(`152` = 1.52) | Windows 无负载概念,填 `65535`(`Units.LoadNotAvailable`) |
| 内存/交换/磁盘容量 | `uint` | MiB(1 MiB = 1048576 B,向下取整) | 上限 4 PiB,足够 |
| 网卡累计流量 | `ulong` | 字节 | 所有"计入网卡"的 rx/tx 累计和(Agent 侧过滤,§5.3) |
| 采样间隔 | `ushort` | 毫秒 | Agent 用 `Stopwatch` 实测的相邻两次采样间隔;首包为 0 |
| 运行时长 | `uint` | 秒 | |
| 时间戳(浏览器 DTO) | `long` | Unix 毫秒 UTC | Agent DTO **不含任何时间字段** |
| 速率(浏览器 DTO) | `ulong` | 字节/秒 | 由 Master 计算 |
| 国家码 | `string?` | ISO 3166-1 alpha-2 大写,如 `"SG"` | 空 = 未知 |
| 字符串长度 | — | ≤ `Limits.MaxStringLength`(256)字符,超出 Master 截断 | Agent 侧也应截断 |
| 数组长度 | — | 磁盘 ≤ 32、IP ≤ 32、网卡名 ≤ 32 | 超出 Master 截断并记 Warning |

时间戳规则:**所有 Agent 上报的数据以 Master 收到的 `DateTimeOffset.UtcNow` 为时间**(`ReceivedAt`)。心跳点时间 = `ReceivedAt`;1 分钟桶 = `ReceivedAt` 向下取整到分钟(UTC)。

---

## 3. Agent ⇄ Master:`/hubs/agent`

### 3.1 鉴权(AgentKey)

- AgentKey:每节点唯一,32 字节随机(`RandomNumberGenerator.GetBytes(32)`)→ Base64Url 编码,43 个字符,正则 `^[A-Za-z0-9_-]{43}$`。后台可轮换(旧 key 立即失效,连接被 `Abort`)。
- 传输:Agent 用 `HttpConnectionOptions.AccessTokenProvider = () => Task.FromResult(agentKey)`。SignalR 客户端对 WebSocket/negotiate 请求附加 `Authorization: Bearer <AgentKey>` 头;LongPolling 亦使用该头(.NET 客户端在非浏览器环境对所有传输都用 Header)。Master 同时接受查询串 `?access_token=` 以兼容代理剥头场景。
- 附加头:`X-Agent-Version: <semver>`(用于日志/统计,不参与鉴权)。
- Master 侧:自定义认证方案 `"AgentKey"`(`AuthenticationHandler<AgentKeyOptions>`),仅在路径前缀 `/hubs/agent` 生效。校验流程:取 Bearer → 正则校验格式 → `Nodes` 表按 `AgentKey` 唯一索引查找 → 节点存在且 `Enabled=true` → 签发 `ClaimsPrincipal`(claims:`snm:node_id`=节点 Id、`snm:role`=`agent`)。失败返回 **401**(negotiate 阶段即失败,Agent 在 `StartAsync` 抛 `HttpRequestException` 401)。`Enabled=false` 的节点同样 401(不是 Hello 拒绝),避免占用连接。
- 连接建立瞬间 Master 读取 `HttpContext.Connection.RemoteIpAddress`(已过 `ForwardedHeaders` 中间件,仅信任 loopback 与配置的 `KnownProxies`),作为该节点的"服务端观测公网 IP",用于 GeoIP 与 IP 合并。

### 3.2 方法一览

| 方向 | 方法名常量 | 线上名称 | 签名(Master 侧 Hub) | 语义 |
|---|---|---|---|---|
| Agent→Master | `AgentHubMethods.Hello` | `"Hello"` | `Task<HelloResult> Hello(NodeInfo info)` | 注册/重注册:上报静态信息,取回配置。**每次连接建立(含自动重连)后必须首先调用**。 |
| Agent→Master | `AgentHubMethods.Ips` | `"Ips"` | `Task Ips(IpReport report)` | 上报本机 IP 列表(Hello 之后立即一次,之后每 `IpReportIntervalSec` 一次,变化时立即) |
| Agent→Master | `AgentHubMethods.Heartbeat` | `"Hb"` | `Task Hb(Heartbeat hb)` | 心跳,每 `IntervalMs`(默认 2000) 一次,`SendAsync`(不等待完成) |
| Master→Agent | `AgentHubMethods.ApplyConfig` | `"ApplyConfig"` | 客户端 `On<AgentConfig>("ApplyConfig", cfg => ...)` | **唯一下行消息**。管理员修改采集间隔/IP 上报间隔时推送;Agent 立即应用,不落盘。 |

`Hello`/`Ips`/`Hb` 均为单参数;Master 侧 Hub 方法不返回 `Hb`/`Ips` 的结果(Agent 用 `SendAsync`,避免等待 Completion 帧)。

### 3.3 DTO 定义(整数 Key → msgpack array)

#### `NodeInfo`(注册)

| Key | 字段 | C# 类型 | 单位 | 含义 / 取值 |
|---|---|---|---|---|
| 0 | `ProtocolVersion` | `byte` | — | 固定 `ProtocolInfo.Version`(=1)。不等则 `HelloResult.Accepted=false`。 |
| 1 | `AgentVersion` | `string` | — | 程序集 `InformationalVersion`,如 `"1.0.0+abc1234"` |
| 2 | `Hostname` | `string` | — | `Environment.MachineName`(Linux 取 `/proc/sys/kernel/hostname` 更准) |
| 3 | `Os` | `string` | — | Linux:`/etc/os-release` 的 `PRETTY_NAME`;Windows:`RuntimeInformation.OSDescription` |
| 4 | `OsKind` | `byte` | 枚举 | `0`=Other `1`=Linux `2`=Windows(`OsKinds` 常量) |
| 5 | `Arch` | `string` | — | `RuntimeInformation.OSArchitecture` 小写:`"x64"`/`"arm64"`/`"x86"`/`"arm"` |
| 6 | `CpuModel` | `string` | — | 含 socket 前缀:`"2x Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz"`;单路不加前缀 |
| 7 | `CpuCores` | `ushort` | 个 | 逻辑核心数 `Environment.ProcessorCount` |
| 8 | `MemTotalMb` | `uint` | MiB | 物理内存总量 |
| 9 | `SwapTotalMb` | `uint` | MiB | Swap/页面文件总量(无则 0) |
| 10 | `Disks` | `DiskInfo[]` | — | 计入统计的挂载点表,**顺序即 `Heartbeat.DiskUsedMb` 的下标顺序** |
| 11 | `NetInterfaces` | `string[]` | — | 计入流量统计的网卡名(如 `["eth0"]`),仅展示用 |
| 12 | `BootId` | `string` | — | Linux `/proc/sys/kernel/random/boot_id`;Windows 取 `Environment.TickCount64` 推算的开机时刻字符串。用于 Master 判断"是否重启"(辅助流量计数器清零判定) |

#### `DiskInfo`

| Key | 字段 | 类型 | 含义 |
|---|---|---|---|
| 0 | `Mount` | `string` | 挂载点 `/`、`/data`、`C:\` |
| 1 | `Fs` | `string` | 文件系统 `ext4`/`xfs`/`NTFS` |
| 2 | `TotalMb` | `uint` | 总容量 MiB |

#### `Heartbeat`(心跳,固定 10 元素数组)

| Key | 字段 | 类型 | 单位 | 含义 |
|---|---|---|---|---|
| 0 | `Cpu` | `ushort` | ‰ | 上一采样区间内全局 CPU 使用率 |
| 1 | `Load1` | `ushort` | ×100 | 1 分钟负载;Windows = 65535 |
| 2 | `MemUsedMb` | `uint` | MiB | Linux:`MemTotal - MemAvailable`;Windows:`TotalPhys - AvailPhys` |
| 3 | `SwapUsedMb` | `uint` | MiB | Linux:`SwapTotal - SwapFree`;Windows:`max(0,(TotalPageFile-AvailPageFile)-(TotalPhys-AvailPhys))` |
| 4 | `DiskUsedMb` | `uint[]` | MiB | 与 `NodeInfo.Disks` 同序的已用容量。长度不一致时 Master 只取 `min(len)` 并记一次 Warning,Agent 会在下一次挂载表变化检测(每 60 s)后重发 `Hello` |
| 5 | `NetRxBytes` | `ulong` | B | 计入网卡的接收累计字节和(开机以来) |
| 6 | `NetTxBytes` | `ulong` | B | 计入网卡的发送累计字节和 |
| 7 | `ElapsedMs` | `ushort` | ms | 与上一次采样的实测间隔(`Stopwatch`),首包 0;Master 用它算速率(§5.2) |
| 8 | `UptimeSec` | `uint` | s | 开机时长 |
| 9 | `Seq` | `uint` | — | Agent 进程内单调递增序号,从 1 开始;用于去重/乱序判定(§5.1) |

**实测大小**(MessagePack 2.5.302,`MessagePackSerializerOptions.Standard`,与手写 formatter 逐字节相等):

| 场景 | DTO 字节 | 含 SignalR Invocation 帧(`[1,{},nil,"Hb",[dto],[]]` + varint 长度前缀) |
|---|---|---|
| 小型 VPS(1 盘,计数器 < 4 GiB) | 28 | 38 |
| 典型(1 盘,计数器 > 4 GiB,uptime 10 天) | **40** | **50** |
| 2 盘 + 超大计数器 | 59 | 69 |

取舍说明:PRD "单次心跳 50 字节内"以 DTO 口径稳定达成;若方法名用 `"Heartbeat"` 帧为 57 字节,故线上名取 `"Hb"`。WebSocket 帧头另加 2–8 字节(客户端掩码 +4)。放弃的字段:每网卡分项(改为 Agent 聚合)、进程数、TCP 连接数、磁盘 IO(超出 PRD 范围)。

#### `IpReport`

| Key | 字段 | 类型 | 含义 |
|---|---|---|---|
| 0 | `Ips` | `string[]` | 本机 Up 网卡上的单播地址文本(IPv4 点分 / IPv6 压缩格式,不含 zone id `%eth0`),已按 §5.4 规则过滤、去重、排序。Master 负责公网/私网分类。 |

#### `HelloResult`(`Hello` 的返回值,Master→Agent)

| Key | 字段 | 类型 | 含义 |
|---|---|---|---|
| 0 | `Accepted` | `bool` | `false` 时 Agent 记录 `Message`,断开并按 §6.2 的"拒绝退避"重试 |
| 1 | `IntervalMs` | `ushort` | 心跳间隔,范围 `[1000, 60000]`,默认 2000 |
| 2 | `IpReportIntervalSec` | `ushort` | IP 上报间隔,范围 `[60, 3600]`,默认 300 |
| 3 | `ServerTimeMs` | `long` | Master 当前 Unix 毫秒(仅供 Agent 日志显示时钟偏差,不用于数据) |
| 4 | `Message` | `string?` | 拒绝原因或提示,如 `"unsupported protocol version 2 (server=1)"` |

#### `AgentConfig`(唯一下行推送)

| Key | 字段 | 类型 | 含义 |
|---|---|---|---|
| 0 | `IntervalMs` | `ushort` | 同上 |
| 1 | `IpReportIntervalSec` | `ushort` | 同上 |

> 下行白名单(BRIEF 安全红线):**`ApplyConfig(AgentConfig)` 是 Master→Agent 唯一的方法调用**;`HelloResult` 是 `Hello` 的返回值。二者仅含数值配置,不含任何路径、命令、URL。Agent 对配置做范围钳制后应用。

### 3.4 连接生命周期(时序)

```
Agent                                   Master(/hubs/agent)
  │ negotiate POST (Authorization: Bearer <AgentKey>, X-Agent-Version)
  │───────────────────────────────────────▶│ AgentKey 认证 → 401 或 200{connectionToken, transports}
  │ WebSocket 升级 (同头) / LongPolling      │
  │───────────────────────────────────────▶│ OnConnectedAsync:
  │                                        │   nodeId = claims; remoteIp = Connection.RemoteIpAddress
  │                                        │   LiveStore.Attach(nodeId, connectionId, remoteIp)
  │                                        │   若该节点已有旧连接 → 旧连接 Abort()(新连接优先)
  │ handshake {"protocol":"messagepack","version":2}
  │───────────────────────────────────────▶│ 
  │ Invoke Hello(NodeInfo)                 │ 校验 ProtocolVersion、截断字段、写 Nodes 静态列
  │───────────────────────────────────────▶│ 重置该连接的 lastSeq=0、流量锚点按 BootId 判定(§5.3)
  │◀───────────────────────────────────────│ Completion HelloResult{Accepted, IntervalMs, ...}
  │ Send Ips(IpReport)                     │ 分类、合并 remoteIp、GeoIP → 更新 Nodes.IpsJson/CountryCode
  │───────────────────────────────────────▶│ 
  │ Send Hb(Heartbeat)  每 IntervalMs        │ ReceivedAt=UtcNow;去重;速率;Delta;环形缓冲;1m 聚合;
  │───────────────────────────────────────▶│ 标记 online;→ Broadcaster 待发队列
  │        ...                             │
  │◀───────────────────────────────────────│ ApplyConfig(AgentConfig)  (管理员改配置时)
  │ 应用新间隔                               │
  │  (网络中断)                              │ OnDisconnectedAsync: LiveStore.Detach(connectionId)
  │ 自动重连(退避 §6.2) → 重新 negotiate      │   connected=false;online 由 30s 超时规则决定
  │ Reconnected 事件 → 再次 Hello → Ips → Hb │
```

规则:
1. Agent 在**同一连接上**只有在 `Hello` 返回 `Accepted=true` 之后才发送 `Ips`/`Hb`;`Hello` 超时(10 s)或异常 → 5 s 后重试 `Hello`,期间不发心跳。
2. Master 收到某连接在 `Hello` 之前的 `Hb`(理论上只在 Master 端进程内状态丢失时发生):从 `Nodes` 表恢复静态信息后照常处理,并记 Information 日志;不向 Agent 发送任何"请重新 Hello"消息(不在下行白名单)。
3. 节点被删除/禁用/轮换 Key:Master 通过保存的 `connectionId` 调用 `Context.Abort()`;Agent 重连时 negotiate 得到 401,进入"鉴权失败退避"(§6.2)。
4. 心跳采用 `SendAsync`(无 Completion 往返);Master 端异常仅记日志,不回传。

### 3.5 Agent 侧协议注册(必须按此写法)

```csharp
var builder = new HubConnectionBuilder()
    .WithUrl(new Uri(serverBase, HubPaths.Agent), HttpTransportType.WebSockets | HttpTransportType.LongPolling, o =>
    {
        o.AccessTokenProvider = () => Task.FromResult<string?>(agentKey);
        o.Headers["X-Agent-Version"] = AgentVersion.Current;
        o.Proxy = proxy;                       // null 或 WebProxy(http/socks5)
        o.CloseTimeout = TimeSpan.FromSeconds(5);
    })
    .WithAutomaticReconnect(new SnmRetryPolicy())      // 指数退避,永不返回 null
    .ConfigureLogging(l => { l.SetMinimumLevel(level); l.AddProvider(new AgentConsoleLoggerProvider()); });
builder.Services.AddSingleton<IHubProtocol, StaticMessagePackHubProtocol>(); // 最后注册者胜出,替代默认 JsonHubProtocol
HubConnection connection = builder.Build();
connection.ServerTimeout   = TimeSpan.FromSeconds(30);  // 与 Master KeepAliveInterval(15s) 匹配
connection.KeepAliveInterval = TimeSpan.FromSeconds(15);
```

**禁止**在 Agent 或 Contracts 中调用 `AddMessagePackProtocol()`、`MessagePackSerializer.*`、`StandardResolver`、`ContractlessStandardResolver`、`MessagePackSerializerOptions.Standard`(会把动态 resolver 拉回可达图并产生 IL2026/IL3050)。

---

## 4. 浏览器 ⇄ Master:`/hubs/public` 与 `/hubs/admin`

### 4.1 鉴权

| Hub | 鉴权 | 说明 |
|---|---|---|
| `/hubs/public` | 匿名 | 只推送脱敏 DTO(§4.3),无客户端→服务端方法。连接数上限 `Realtime:PublicMaxConnections`(默认 500),超出时 `OnConnectedAsync` 直接 `Abort`。 |
| `/hubs/admin` | JWT(`[Authorize(AuthenticationSchemes = "Bearer")]`) | 浏览器 `withUrl('/hubs/admin', { accessTokenFactory: () => authStore.accessToken })` → SignalR JS 以 `?access_token=` 查询串附带;Master 的 `JwtBearerEvents.OnMessageReceived` 对路径以 `/hubs/` 开头的请求从查询串读 token。token 过期 → 连接关闭(`CloseMessage`),前端刷新 token 后重连。 |

### 4.2 方法一览

| Hub | 方向 | 方法名 | 参数 | 时机 |
|---|---|---|---|---|
| public | S→C | `Snapshot` | `PublicSnapshot` | 连接建立后立即(`OnConnectedAsync` 中 `Clients.Caller`) |
| public | S→C | `Tick` | `PublicTick` | 每 2 s 一次(有变化才发) |
| public | S→C | `NodeChanged` | `PublicNodeCard` | 节点新增、`PublicName`/国家/排序/`IsPublic=true` 变更 |
| public | S→C | `NodeRemoved` | `int nodeId` | 节点删除或 `IsPublic=false` |
| admin | S→C | `Snapshot` | `AdminSnapshot` | 连接建立后立即;或客户端调用 `GetSnapshot` |
| admin | S→C | `Tick` | `AdminTick` | 每 2 s 一次 |
| admin | S→C | `NodeChanged` | `AdminNodeLive` | 节点新增/配置变更/静态信息变更(Hello)/IP 列表变更 |
| admin | S→C | `NodeRemoved` | `int nodeId` | 节点删除 |
| admin | S→C | `Alert` | `AlertPush` | 告警触发/恢复(含被冷却抑制的事件,`suppressed=true`) |
| admin | C→S | `GetSnapshot` | — → `AdminSnapshot` | 前端切换页面/重连后主动拉取 |

分组:public 连接加入组 `"public"`,admin 连接加入组 `"admin"`;Broadcaster 用 `IHubContext<PublicHub>.Clients.Group("public")`。

### 4.3 浏览器 DTO(字符串 Key → msgpack map,JS 直接得到对象)

命名空间 `SNM.Contracts.Realtime`。所有 Key 为 camelCase 字符串。

#### `LivePoint`(波浪图点,Public 与 Admin 共用)

| Key | 字段 | C# | 含义 |
|---|---|---|---|
| `t` | `T` | `long` | Master 收到心跳的 Unix 毫秒 |
| `cpu` | `Cpu` | `ushort` | ‰ |
| `mem` | `Mem` | `ushort` | 内存使用 ‰ = `MemUsedMb*1000/MemTotalMb` |
| `rx` | `RxBps` | `ulong` | 接收速率 B/s |
| `tx` | `TxBps` | `ulong` | 发送速率 B/s |

#### `PublicNodeCard`

| Key | 字段 | C# | 含义 |
|---|---|---|---|
| `id` | `Id` | `int` | 节点 Id(非敏感) |
| `name` | `Name` | `string` | **PublicName** |
| `cc` | `CountryCode` | `string?` | 有效国家码(手动覆盖优先) |
| `online` | `Online` | `bool` | 最近心跳 ≤ OfflineSeconds |
| `order` | `SortOrder` | `int` | 排序值,前端按 `order asc, id asc` |
| `hist` | `History` | `LivePoint[]` | 最近 ≤60 点(时间升序) |

> 脱敏清单(Public 绝不包含):IP、主机名、OS、CPU 型号、核数、内存/磁盘绝对值(只有千分比)、供应商/价格/到期、AdminRemark、AgentKey、告警内容。实现要求:`PublicNodeCard` 的构造函数只接受这些字段,`tests/SNM.Master.Tests/PublicHubSanitizationTests` 用反射断言 `PublicNodeCard`/`PublicTick` 的公共属性集合恰为上表。

#### `PublicSnapshot`

| Key | 字段 | C# | 含义 |
|---|---|---|---|
| `t` | `ServerTimeMs` | `long` | |
| `tickMs` | `TickMs` | `int` | 广播周期(2000) |
| `title` | `Title` | `string` | 大屏标题(设置项 `general.publicTitle`) |
| `nodes` | `Nodes` | `PublicNodeCard[]` | 仅 `IsPublic=true && Enabled=true` 的节点 |

#### `PublicNodeUpdate` / `PublicTick`

| DTO | Key | 字段 | C# | 含义 |
|---|---|---|---|---|
| `PublicNodeUpdate` | `id` | `Id` | `int` | |
| | `online` | `Online` | `bool` | 当前在线状态(每次都带,前端直接覆盖) |
| | `p` | `Point` | `LivePoint?` | 本周期内最新一点;若本周期无心跳但状态变化则为 `null` |
| `PublicTick` | `t` | `ServerTimeMs` | `long` | |
| | `u` | `Updates` | `PublicNodeUpdate[]` | 本周期有变化的节点 |

#### `AdminNodeLive`(全量)

| Key | 字段 | C# | 含义 |
|---|---|---|---|
| `id` | `Id` | `int` | |
| `name` | `Name` | `string` | 内部名称 |
| `publicName` | `PublicName` | `string` | |
| `cc` | `CountryCode` | `string?` | 有效国家码 |
| `ccAuto` | `CountryCodeAuto` | `string?` | GeoIP 自动识别值 |
| `online` | `Online` | `bool` | |
| `connected` | `Connected` | `bool` | 当前是否有活动 SignalR 连接 |
| `lastSeen` | `LastSeenMs` | `long?` | 最近心跳 Unix 毫秒 |
| `ips` | `Ips` | `IpEntry[]` | 合并后的 IP 列表 |
| `hostname` | `Hostname` | `string?` | |
| `os` | `Os` | `string?` | |
| `arch` | `Arch` | `string?` | |
| `cpuModel` | `CpuModel` | `string?` | |
| `cores` | `CpuCores` | `int` | |
| `memTotal` | `MemTotalMb` | `uint` | |
| `swapTotal` | `SwapTotalMb` | `uint` | |
| `disks` | `Disks` | `DiskLive[]` | `{mount, fs, total, used}` |
| `agentVersion` | `AgentVersion` | `string?` | |
| `uptime` | `UptimeSec` | `uint` | |
| `cpu` | `Cpu` | `ushort` | ‰(最新) |
| `load1` | `Load1` | `ushort` | ×100,65535=N/A |
| `memUsed` | `MemUsedMb` | `uint` | |
| `swapUsed` | `SwapUsedMb` | `uint` | |
| `rx` | `RxBps` | `ulong` | |
| `tx` | `TxBps` | `ulong` | |
| `cycleRx` | `CycleRxBytes` | `ulong` | 本计费周期累计 |
| `cycleTx` | `CycleTxBytes` | `ulong` | |
| `cycleUsed` | `CycleUsedBytes` | `ulong` | 按 `TrafficMode` 计算的已用 |
| `limitGb` | `TrafficLimitGb` | `uint` | 0 = 不限 |
| `cycleStart` | `CycleStart` | `string` | `yyyy-MM-dd`(节点时区) |
| `cycleEnd` | `CycleEnd` | `string` | `yyyy-MM-dd`(下个周期起点) |
| `expiresAt` | `ExpiresAt` | `string?` | `yyyy-MM-dd` |
| `alerts` | `OpenAlerts` | `string[]` | 当前 firing 的规则名,如 `["Offline"]` |
| `hist` | `History` | `LivePoint[]` | 最近 ≤60 点 |

`IpEntry`:`{ "ip": string, "family": 4|6, "kind": "public"|"private", "source": "agent"|"server", "lastSeen": long }`。
`DiskLive`:`{ "mount": string, "fs": string, "total": uint, "used": uint }`(MiB)。

#### `AdminPoint` / `AdminNodeUpdate` / `AdminTick` / `AdminSnapshot`

| DTO | Key | 字段 | C# |
|---|---|---|---|
| `AdminPoint` | 继承 `LivePoint` 的 5 个 Key,另加 `load1`(`ushort`)、`memUsed`(`uint`)、`swapUsed`(`uint`)、`diskUsed`(`uint[]`)、`uptime`(`uint`)、`cycleRx`(`ulong`)、`cycleTx`(`ulong`)、`cycleUsed`(`ulong`) |
| `AdminNodeUpdate` | `id`(int)、`online`(bool)、`connected`(bool)、`p`(`AdminPoint?`) |
| `AdminTick` | `t`(long)、`u`(`AdminNodeUpdate[]`) |
| `AdminSnapshot` | `t`(long)、`tickMs`(int)、`nodes`(`AdminNodeLive[]`,含 `IsPublic=false` 与 `Enabled=false` 的节点) |

#### `AlertPush`

| Key | 字段 | C# | 含义 |
|---|---|---|---|
| `id` | `Id` | `long` | AlertEvent Id |
| `nodeId` | `NodeId` | `int` | |
| `nodeName` | `NodeName` | `string` | 内部名称 |
| `rule` | `Rule` | `string` | `Offline`/`CpuHigh`/`MemHigh`/`DiskHigh`/`TrafficHigh`/`Expiry` |
| `level` | `Level` | `string` | `info`/`warning`/`critical` |
| `status` | `Status` | `string` | `firing`/`resolved` |
| `title` | `Title` | `string` | 中文标题 |
| `message` | `Message` | `string` | 中文正文 |
| `suppressed` | `Suppressed` | `bool` | 是否因冷却未外发通知 |
| `at` | `AtMs` | `long` | 事件时间 |

### 4.4 广播与节流

- `RealtimeBroadcaster`(`BackgroundService`,`PeriodicTimer(2000 ms)`):每周期从 `LiveStore` 取出"自上周期以来有新点或状态变化"的节点集合,组装一条 `PublicTick` 与一条 `AdminTick`,分别发往两个组;空集合不发送。100 节点每周期约 3 KB(public)/7 KB(admin)。
- 环形缓冲:`LiveStore` 每节点 `RingBuffer<LivePoint>` 容量 90;快照携带最近 60 点。心跳间隔非 2 s 的节点点数按实际间隔(前端按 `t` 画,不假定等距)。
- 在线判定:`Online = now - LastSeen <= OfflineSeconds`(默认 30 s),由 `HealthMonitor` 每 5 s 评估;状态翻转的节点在下一周期 `Tick` 中以 `p=null, online=false` 形式下发。
- 结构变更(`NodeChanged`/`NodeRemoved`)不节流,即时发送;紧随其后的 `Tick` 会带上该节点最新点。

### 4.5 浏览器端连接示例(`web/public/js/app.js` 与 admin `useRealtime.js` 共用逻辑)

```js
const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/public')                                   // admin: withUrl('/hubs/admin', { accessTokenFactory })
  .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
  .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])     // 之后每 30 s(自定义 nextRetryDelayInMilliseconds 恒 30000)
  .configureLogging(signalR.LogLevel.Warning)
  .build()
conn.on('Snapshot', s => store.replaceAll(s))
conn.on('Tick', t => store.applyTick(t))
conn.on('NodeChanged', c => store.upsert(c))
conn.on('NodeRemoved', id => store.remove(id))
conn.onreconnecting(() => ui.setState('reconnecting'))
conn.onreconnected(() => ui.setState('live'))            // Snapshot 会由服务端在 OnConnectedAsync 再推一次
conn.onclose(() => { ui.setState('offline'); setTimeout(start, 5000) })
```

注意:`@msgpack/msgpack` 把 `uint64` 解成 Number(< 2^53 精确;流量字节远低于此)。

---

## 5. Master 侧处理规则

### 5.1 去重与乱序

- 每个连接维护 `lastSeq`(在 `Hello` 成功时置 0)。收到 `Hb`:`if (hb.Seq <= lastSeq && lastSeq != 0) → 丢弃(Debug 日志,计数器 `snm_agent_hb_dropped_total`)`;否则 `lastSeq = hb.Seq` 并处理。
- 跨连接:新连接 `Hello` 后 `lastSeq=0`,Agent 进程重启后 `Seq` 从 1 重新开始,不会误判。
- 同一节点两个连接同时存在(Key 被复用到两台机器):后连接者胜出,前者 `Abort`;`Nodes.LastRemoteIp` 变化频繁时记 Warning "AgentKey 可能被多台机器使用"。
- SignalR 单连接内消息有序,不会出现真正乱序;`Seq` 主要防重放与代理重试。

### 5.2 速率计算

```
dt_ms = hb.ElapsedMs in [200, 60000] ? hb.ElapsedMs : (ReceivedAt - prev.ReceivedAt).TotalMilliseconds
if prev == null || hb.ElapsedMs == 0 → 速率 = 0(不画点?——仍记点,rx=tx=0)
dRx = hb.NetRxBytes >= prev.NetRxBytes ? hb.NetRxBytes - prev.NetRxBytes : hb.NetRxBytes   // 清零判定见 DATA.md §6
RxBps = dRx * 1000 / dt_ms;TxBps 同理
```

### 5.3 网卡过滤(Agent 侧,上报聚合值)

选择"Agent 过滤 + 上报聚合累计值":心跳体积恒定、Master 无需维护网卡表、Delta 引擎面对的是单调计数器。规则(Linux 读 `/proc/net/dev`;Windows 用 `NetworkInterface`):

- 排除名称前缀:`lo`, `docker`, `br-`, `veth`, `virbr`, `vnet`, `vmbr`(仅当存在其他物理网卡时), `tun`, `tap`, `wg`, `tailscale`, `zt`, `lxc`, `lxd`, `cni`, `flannel`, `kube`, `cali`, `dummy`, `sit`, `ip6tnl`, `gre`, `vxlan`, `podman`, `nerdctl`, `ifb`, `teql`, `fwbr`, `fwpr`, `fwln`, `ppp`(仅 `--net-if` 显式包含时计入), `virbr`。
- Windows 排除:`NetworkInterfaceType.Loopback/Tunnel/Ppp`,`Description` 含 `Virtual`、`VMware`、`Hyper-V`、`WSL`、`Bluetooth`、`TAP`、`Npcap`、`Loopback`;仅 `OperationalStatus.Up`。
- 覆盖:`--net-if eth0,ens3`(白名单,优先级最高)/`--net-exclude wlan0`(追加排除)。
- 若过滤后为空:回退为"除 `lo` 外全部 Up 网卡",并记 Warning。
- Agent 每 60 s 重新枚举网卡;若计入集合变化,则下一次 `Hello`(立即触发)更新 `NetInterfaces`,Master 按 BootId 未变但计数器下降的规则处理为"清零"。

### 5.4 IP 发现过滤(Agent 侧)

保留:IPv4/IPv6 单播地址,网卡 `OperationalStatus.Up`(Linux 用 `NetworkInterface` 同样可用)。剔除:`127.0.0.0/8`、`::1`、`169.254.0.0/16`、`fe80::/10`、`0.0.0.0`、`::`、组播、临时/Deprecated IPv6(`DuplicateAddressDetectionState != Preferred` 时剔除,Linux 上属性不可用则保留)、被 §5.3 排除网卡上的地址(docker0 的 172.17.0.1 等)。去重、按 family(4 先)与文本排序、上限 32。**Master** 负责分类:私网 = `10/8`、`172.16/12`、`192.168/16`、`100.64/10`、`fc00::/7`;其余为公网。合并规则见 DATA.md §7。

---

## 6. 错误处理与重连

### 6.1 Master 侧

| 场景 | 处理 |
|---|---|
| AgentKey 无效/节点禁用 | negotiate 401,不建立连接 |
| `Hello.ProtocolVersion != 1` | 返回 `Accepted=false, Message="unsupported protocol version"`,随后 `Context.Abort()`(延迟 1 s 让 Completion 送达) |
| DTO 字段越界(字符串 > 256、数组 > 32、`IntervalMs` 越界) | 截断/钳制并继续;记 Warning(每节点每小时最多 1 条) |
| `Hb` 反序列化失败(`InvocationBindingFailureMessage`) | SignalR 记录错误并忽略该消息;连接保持;计数器 +1 |
| Hub 方法抛异常 | `Hb`/`Ips` 为 Send 语义,异常仅记日志;`Hello` 抛异常 → 客户端收到 `HubException`,Agent 5 s 后重试 |
| 数据库写失败(SQLITE_BUSY 超过 busy_timeout) | 聚合数据留在内存队列,下一分钟重试;告警/流量锚点写入重试 3 次 |
| 同节点重复连接 | 新胜旧,旧 `Abort` |

### 6.2 Agent 侧重连策略(`SnmRetryPolicy : IRetryPolicy` + 外层循环)

- 自动重连退避序列(秒):`1, 2, 4, 8, 16, 32, 60, 60, ...`,每次加 ±20% 抖动;**永不返回 `null`**(永不放弃)。
- 初始 `StartAsync` 失败(服务端未启动、DNS 失败)同样按上述序列循环。
- **鉴权失败退避**:`StartAsync` 抛出带 401/403 的 `HttpRequestException` → 固定 300 s 重试(Key 可能刚被轮换,等管理员更新 env);每次失败 Error 级日志一条。
- `HelloResult.Accepted=false` → 断开,按 Message 分类:协议版本不符 → 600 s 重试;其他 → 300 s。
- `Reconnected` 事件 → 立即 `Hello` → `Ips` → 恢复心跳;`Seq` 继续递增(不重置);`ElapsedMs` 首个心跳取真实间隔(可能很大 → Master 钳制到 60000 并用服务端 Δt)。
- 采集失败(如 `/proc/stat` 读取异常):该字段沿用上次值,Warning 一次/10 分钟;绝不因采集异常退出进程。
- 进程退出码:`0` 正常(SIGTERM),`2` 参数错误,`3` 缺少 `--server/--key`。

### 6.3 浏览器侧

- `withAutomaticReconnect` 后 `onclose` 时 5 s 后手动重启连接(永不放弃)。
- 重连后服务端会重新推 `Snapshot`,前端用 `Snapshot` 整体替换本地状态(解决离线期间的丢失)。
- admin token 过期:`onclose` 且 `authStore` 能刷新 → 刷新后重连;否则跳登录。

---

## 7. Master 侧 SignalR 配置(固定值)

```csharp
builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
    o.HandshakeTimeout = TimeSpan.FromSeconds(15);
    o.MaximumReceiveMessageSize = 64 * 1024;          // 最大入站帧 64 KiB(NodeInfo 上限约 6 KiB)
    o.StreamBufferCapacity = 10;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    o.SupportedProtocols = new List<string> { "messagepack" };
}).AddMessagePackProtocol();   // 默认 SerializerOptions(SignalRResolver = DynamicEnumAsStringResolver + ContractlessStandardResolver;
                               // 我们的 DTO 均带 [MessagePackObject],由 StandardResolver 路径处理,不走 contractless)
app.MapHub<AgentHub>(HubPaths.Agent, o => o.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling)
   .RequireAuthorization("AgentOnly");
app.MapHub<PublicHub>(HubPaths.Public);
app.MapHub<AdminHub>(HubPaths.Admin).RequireAuthorization("AdminOnly");
```

`ProtocolVersion` 常量 = 1;升级协议时:新增字段只能追加更大的 Key(手写 reader 对多余元素 `Skip`,对缺失元素取默认值),破坏性变更才递增 `ProtocolInfo.Version`。

---

## 8. 等价性与 AOT 验证方案(BRIEF Q1/Q10)

### 8.1 `tests/SNM.Contracts.Tests`(JIT,可用反射 resolver)

| 测试 | 断言 |
|---|---|
| `Formatter_ByteEquivalence_<Dto>` | 对每个 Agent DTO 的 6 组样本(零值、典型、最大值、空数组、null 字符串、长字符串 256)`MessagePackSerializer.Serialize(dto, Standard)` 与 `AgentFormatters.WriteXxx` 输出逐字节相等 |
| `Formatter_RoundTrip_<Dto>` | 手写 Write → 反射 Deserialize 深度相等;反射 Serialize → 手写 Read 深度相等 |
| `Formatter_ForwardCompat` | 手写 Read 能读取比当前多 1 个元素的数组(多余元素被 Skip)、少 1 个元素(缺失取默认)、`nil`(返回 null) |
| `HubProtocol_FrameEquivalence` | `new StaticMessagePackHubProtocol().GetMessageBytes(InvocationMessage("Hb",[hb]))` 与 `new MessagePackHubProtocol().GetMessageBytes(同)` 逐字节相等;`Hello` 的 `CompletionMessage(HelloResult)`、`ApplyConfig` 的 Invocation、`PingMessage`、`CloseMessage` 同理 |
| `HubProtocol_CrossParse` | 用测试 `IInvocationBinder` 让两种协议互相解析对方的帧,参数深度相等 |
| `HubProtocol_UnknownType_Throws` | 静态协议序列化未知类型抛 `InvalidDataException`,消息含类型名 |
| `Heartbeat_SizeBudget` | 典型样本 DTO ≤ 48 字节、帧 ≤ 56 字节(防止未来加字段时无意突破预算) |

### 8.2 `tests/SNM.Master.Tests`

- `AgentHubInteropTests`:`WebApplicationFactory<Program>` + 临时 SQLite 文件;客户端 A 用 `StaticMessagePackHubProtocol`(与 Agent 完全相同的注册代码),客户端 B 用官方 `AddMessagePackProtocol()`;两者都能完成 `Hello → Ips → Hb`,`LiveStore` 状态正确,`HelloResult.IntervalMs` 正确;错误 Key → 401。
- `PublicHubSanitizationTests`:反射断言 `PublicNodeCard`/`PublicTick`/`PublicNodeUpdate`/`PublicSnapshot` 的属性集合与 §4.3 完全一致;官方 msgpack 客户端连接 `/hubs/public` 收到的 `Snapshot` 里所有 map key 均在白名单内。

### 8.3 AOT 验证(本机可执行)

```bash
source scripts/env.sh
dotnet build src/SNM.Agent -c Release              # 分析器:0 个 IL2xxx/IL3xxx
dotnet publish src/SNM.Agent -c Release -r win-arm64 -p:PublishAot=true -p:IlcUseEnvironmentalTools=true -p:TrimmerSingleWarn=false
# 期望:ILC 阶段 0 条 IL 警告;最后 link 步骤因无 MSVC 失败属预期
```

### 8.4 `UnconditionalSuppressMessage` 登记表

| 位置 | 警告 | 理由 |
|---|---|---|
| (空) | — | 当前设计不需要任何抑制。若实现阶段出现,须在此登记并在代码用 `// AOT:` 注释说明。 |

---

## 9. C# 契约代码草案(可直接落到 `src/SNM.Contracts`)

### 9.1 `SNM.Contracts.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>SNM.Contracts</RootNamespace>
    <IsAotCompatible>true</IsAotCompatible>
    <IsTrimmable>true</IsTrimmable>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <!-- MessagePack 2.5.302: only MessagePackWriter/Reader + [MessagePackObject]/[Key] attributes are used. -->
    <PackageReference Include="MessagePack" />
    <!-- Provides IHubProtocol, HubMessage, HubProtocolConstants, IInvocationBinder for the static protocol.
         Rationale for adding to Directory.Packages.props (10.0.11): transitive dependency of SignalR.Client anyway; AOT-clean. -->
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Common" />
  </ItemGroup>
  <ItemGroup>
    <None Include="THIRD-PARTY-NOTICES.md" Pack="true" />
  </ItemGroup>
</Project>
```

`Directory.Packages.props` 新增:`<PackageVersion Include="Microsoft.AspNetCore.SignalR.Common" Version="10.0.11" />`。

### 9.2 `Constants.cs`

```csharp
namespace SNM.Contracts;

/// <summary>Wire-protocol level constants shared by Agent, Master and tests.</summary>
public static class ProtocolInfo
{
    /// <summary>Application protocol version carried in <see cref="NodeInfo.ProtocolVersion"/>. Bump only on breaking changes.</summary>
    public const byte Version = 1;
    /// <summary>SignalR hub protocol name; must match the official MessagePack protocol.</summary>
    public const string HubProtocolName = "messagepack";
    /// <summary>SignalR hub protocol version; must match the official MessagePack protocol.</summary>
    public const int HubProtocolVersion = 2;
}

public static class HubPaths
{
    public const string Agent = "/hubs/agent";
    public const string Public = "/hubs/public";
    public const string Admin = "/hubs/admin";
}

/// <summary>Method names on <see cref="HubPaths.Agent"/>.</summary>
public static class AgentHubMethods
{
    // Agent -> Master
    public const string Hello = "Hello";
    public const string Ips = "Ips";
    /// <summary>Heartbeat. Deliberately short: it is the hot path (typical frame 50 bytes with this name).</summary>
    public const string Heartbeat = "Hb";
    // Master -> Agent (the ONLY downstream invocation; configuration only)
    public const string ApplyConfig = "ApplyConfig";
}

/// <summary>Method names on <see cref="HubPaths.Public"/> (server -> client only).</summary>
public static class PublicHubMethods
{
    public const string Snapshot = "Snapshot";
    public const string Tick = "Tick";
    public const string NodeChanged = "NodeChanged";
    public const string NodeRemoved = "NodeRemoved";
}

/// <summary>Method names on <see cref="HubPaths.Admin"/>.</summary>
public static class AdminHubMethods
{
    // server -> client
    public const string Snapshot = "Snapshot";
    public const string Tick = "Tick";
    public const string NodeChanged = "NodeChanged";
    public const string NodeRemoved = "NodeRemoved";
    public const string Alert = "Alert";
    // client -> server
    public const string GetSnapshot = "GetSnapshot";
}

public static class OsKinds
{
    public const byte Other = 0;
    public const byte Linux = 1;
    public const byte Windows = 2;
}

public static class Units
{
    /// <summary>CPU and memory percentages are transmitted as permille (0..1000).</summary>
    public const ushort PermilleMax = 1000;
    /// <summary>Load average is transmitted ×100; this sentinel means "not available" (Windows).</summary>
    public const ushort LoadNotAvailable = ushort.MaxValue;
    public const uint BytesPerMiB = 1024 * 1024;
}

public static class Limits
{
    public const ushort MinIntervalMs = 1000;
    public const ushort MaxIntervalMs = 60000;
    public const ushort DefaultIntervalMs = 2000;
    public const ushort MinIpReportIntervalSec = 60;
    public const ushort MaxIpReportIntervalSec = 3600;
    public const ushort DefaultIpReportIntervalSec = 300;
    public const int MaxDisks = 32;
    public const int MaxIps = 32;
    public const int MaxNetInterfaces = 32;
    public const int MaxStringLength = 256;
    /// <summary>Ring buffer capacity per node on the Master (points).</summary>
    public const int RingCapacity = 90;
    /// <summary>Points sent in snapshots.</summary>
    public const int HistoryPoints = 60;
    public const int BroadcastIntervalMs = 2000;
    public const int DefaultOfflineSeconds = 30;
    /// <summary>AgentKey: 32 random bytes, Base64Url => 43 chars.</summary>
    public const int AgentKeyLength = 43;
}
```

### 9.3 `Agent/AgentDtos.cs`(整数 Key)

```csharp
using MessagePack;

namespace SNM.Contracts;

// NOTE: All Agent-facing DTOs use integer keys => serialized as msgpack arrays.
// Field order/keys are frozen by PROTOCOL.md §3.3. Hand-written formatters live in Serialization/AgentFormatters.cs
// and MUST stay byte-identical to what MessagePack's StandardResolver produces for these attributes.

[MessagePackObject]
public sealed class NodeInfo
{
    [Key(0)] public byte ProtocolVersion { get; set; } = ProtocolInfo.Version;
    [Key(1)] public string AgentVersion { get; set; } = "";
    [Key(2)] public string Hostname { get; set; } = "";
    [Key(3)] public string Os { get; set; } = "";
    [Key(4)] public byte OsKind { get; set; }
    [Key(5)] public string Arch { get; set; } = "";
    [Key(6)] public string CpuModel { get; set; } = "";
    [Key(7)] public ushort CpuCores { get; set; }
    [Key(8)] public uint MemTotalMb { get; set; }
    [Key(9)] public uint SwapTotalMb { get; set; }
    [Key(10)] public DiskInfo[] Disks { get; set; } = Array.Empty<DiskInfo>();
    [Key(11)] public string[] NetInterfaces { get; set; } = Array.Empty<string>();
    [Key(12)] public string BootId { get; set; } = "";
}

[MessagePackObject]
public sealed class DiskInfo
{
    [Key(0)] public string Mount { get; set; } = "";
    [Key(1)] public string Fs { get; set; } = "";
    [Key(2)] public uint TotalMb { get; set; }
}

[MessagePackObject]
public sealed class Heartbeat
{
    /// <summary>CPU usage, permille (0..1000).</summary>
    [Key(0)] public ushort Cpu { get; set; }
    /// <summary>1-minute load average ×100; <see cref="Units.LoadNotAvailable"/> when unsupported.</summary>
    [Key(1)] public ushort Load1 { get; set; }
    [Key(2)] public uint MemUsedMb { get; set; }
    [Key(3)] public uint SwapUsedMb { get; set; }
    /// <summary>Used MiB per mount, same order as <see cref="NodeInfo.Disks"/>.</summary>
    [Key(4)] public uint[] DiskUsedMb { get; set; } = Array.Empty<uint>();
    /// <summary>Cumulative received bytes over counted interfaces (since boot).</summary>
    [Key(5)] public ulong NetRxBytes { get; set; }
    /// <summary>Cumulative transmitted bytes over counted interfaces (since boot).</summary>
    [Key(6)] public ulong NetTxBytes { get; set; }
    /// <summary>Measured milliseconds since the previous sample (Stopwatch); 0 for the first sample.</summary>
    [Key(7)] public ushort ElapsedMs { get; set; }
    [Key(8)] public uint UptimeSec { get; set; }
    /// <summary>Monotonic per-process sequence number starting at 1.</summary>
    [Key(9)] public uint Seq { get; set; }
}

[MessagePackObject]
public sealed class IpReport
{
    [Key(0)] public string[] Ips { get; set; } = Array.Empty<string>();
}

[MessagePackObject]
public sealed class HelloResult
{
    [Key(0)] public bool Accepted { get; set; }
    [Key(1)] public ushort IntervalMs { get; set; } = Limits.DefaultIntervalMs;
    [Key(2)] public ushort IpReportIntervalSec { get; set; } = Limits.DefaultIpReportIntervalSec;
    [Key(3)] public long ServerTimeMs { get; set; }
    [Key(4)] public string? Message { get; set; }
}

/// <summary>The only Master -> Agent push. Configuration values only.</summary>
[MessagePackObject]
public sealed class AgentConfig
{
    [Key(0)] public ushort IntervalMs { get; set; } = Limits.DefaultIntervalMs;
    [Key(1)] public ushort IpReportIntervalSec { get; set; } = Limits.DefaultIpReportIntervalSec;
}
```

### 9.4 `Realtime/RealtimeDtos.cs`(字符串 Key,浏览器)

```csharp
using MessagePack;

namespace SNM.Contracts.Realtime;

// Browser-facing DTOs use string keys (camelCase) => msgpack maps => plain JS objects.
// These types are never referenced by SNM.Agent (trimmed away there).

[MessagePackObject]
public class LivePoint
{
    [Key("t")] public long T { get; set; }
    [Key("cpu")] public ushort Cpu { get; set; }
    [Key("mem")] public ushort Mem { get; set; }
    [Key("rx")] public ulong RxBps { get; set; }
    [Key("tx")] public ulong TxBps { get; set; }
}

[MessagePackObject]
public sealed class PublicNodeCard
{
    [Key("id")] public int Id { get; set; }
    [Key("name")] public string Name { get; set; } = "";
    [Key("cc")] public string? CountryCode { get; set; }
    [Key("online")] public bool Online { get; set; }
    [Key("order")] public int SortOrder { get; set; }
    [Key("hist")] public LivePoint[] History { get; set; } = Array.Empty<LivePoint>();
}

[MessagePackObject]
public sealed class PublicSnapshot
{
    [Key("t")] public long ServerTimeMs { get; set; }
    [Key("tickMs")] public int TickMs { get; set; } = Limits.BroadcastIntervalMs;
    [Key("title")] public string Title { get; set; } = "";
    [Key("nodes")] public PublicNodeCard[] Nodes { get; set; } = Array.Empty<PublicNodeCard>();
}

[MessagePackObject]
public sealed class PublicNodeUpdate
{
    [Key("id")] public int Id { get; set; }
    [Key("online")] public bool Online { get; set; }
    [Key("p")] public LivePoint? Point { get; set; }
}

[MessagePackObject]
public sealed class PublicTick
{
    [Key("t")] public long ServerTimeMs { get; set; }
    [Key("u")] public PublicNodeUpdate[] Updates { get; set; } = Array.Empty<PublicNodeUpdate>();
}

[MessagePackObject]
public sealed class IpEntry
{
    [Key("ip")] public string Ip { get; set; } = "";
    [Key("family")] public byte Family { get; set; }            // 4 | 6
    [Key("kind")] public string Kind { get; set; } = "public";   // "public" | "private"
    [Key("source")] public string Source { get; set; } = "agent"; // "agent" | "server"
    [Key("lastSeen")] public long LastSeenMs { get; set; }
}

[MessagePackObject]
public sealed class DiskLive
{
    [Key("mount")] public string Mount { get; set; } = "";
    [Key("fs")] public string Fs { get; set; } = "";
    [Key("total")] public uint TotalMb { get; set; }
    [Key("used")] public uint UsedMb { get; set; }
}

[MessagePackObject]
public sealed class AdminPoint : LivePoint
{
    [Key("load1")] public ushort Load1 { get; set; }
    [Key("memUsed")] public uint MemUsedMb { get; set; }
    [Key("swapUsed")] public uint SwapUsedMb { get; set; }
    [Key("diskUsed")] public uint[] DiskUsedMb { get; set; } = Array.Empty<uint>();
    [Key("uptime")] public uint UptimeSec { get; set; }
    [Key("cycleRx")] public ulong CycleRxBytes { get; set; }
    [Key("cycleTx")] public ulong CycleTxBytes { get; set; }
    [Key("cycleUsed")] public ulong CycleUsedBytes { get; set; }
}

[MessagePackObject]
public sealed class AdminNodeLive
{
    [Key("id")] public int Id { get; set; }
    [Key("name")] public string Name { get; set; } = "";
    [Key("publicName")] public string PublicName { get; set; } = "";
    [Key("cc")] public string? CountryCode { get; set; }
    [Key("ccAuto")] public string? CountryCodeAuto { get; set; }
    [Key("online")] public bool Online { get; set; }
    [Key("connected")] public bool Connected { get; set; }
    [Key("lastSeen")] public long? LastSeenMs { get; set; }
    [Key("ips")] public IpEntry[] Ips { get; set; } = Array.Empty<IpEntry>();
    [Key("hostname")] public string? Hostname { get; set; }
    [Key("os")] public string? Os { get; set; }
    [Key("arch")] public string? Arch { get; set; }
    [Key("cpuModel")] public string? CpuModel { get; set; }
    [Key("cores")] public int CpuCores { get; set; }
    [Key("memTotal")] public uint MemTotalMb { get; set; }
    [Key("swapTotal")] public uint SwapTotalMb { get; set; }
    [Key("disks")] public DiskLive[] Disks { get; set; } = Array.Empty<DiskLive>();
    [Key("agentVersion")] public string? AgentVersion { get; set; }
    [Key("uptime")] public uint UptimeSec { get; set; }
    [Key("cpu")] public ushort Cpu { get; set; }
    [Key("load1")] public ushort Load1 { get; set; }
    [Key("memUsed")] public uint MemUsedMb { get; set; }
    [Key("swapUsed")] public uint SwapUsedMb { get; set; }
    [Key("rx")] public ulong RxBps { get; set; }
    [Key("tx")] public ulong TxBps { get; set; }
    [Key("cycleRx")] public ulong CycleRxBytes { get; set; }
    [Key("cycleTx")] public ulong CycleTxBytes { get; set; }
    [Key("cycleUsed")] public ulong CycleUsedBytes { get; set; }
    [Key("limitGb")] public uint TrafficLimitGb { get; set; }
    [Key("cycleStart")] public string CycleStart { get; set; } = "";
    [Key("cycleEnd")] public string CycleEnd { get; set; } = "";
    [Key("expiresAt")] public string? ExpiresAt { get; set; }
    [Key("alerts")] public string[] OpenAlerts { get; set; } = Array.Empty<string>();
    [Key("hist")] public LivePoint[] History { get; set; } = Array.Empty<LivePoint>();
}

[MessagePackObject]
public sealed class AdminNodeUpdate
{
    [Key("id")] public int Id { get; set; }
    [Key("online")] public bool Online { get; set; }
    [Key("connected")] public bool Connected { get; set; }
    [Key("p")] public AdminPoint? Point { get; set; }
}

[MessagePackObject]
public sealed class AdminTick
{
    [Key("t")] public long ServerTimeMs { get; set; }
    [Key("u")] public AdminNodeUpdate[] Updates { get; set; } = Array.Empty<AdminNodeUpdate>();
}

[MessagePackObject]
public sealed class AdminSnapshot
{
    [Key("t")] public long ServerTimeMs { get; set; }
    [Key("tickMs")] public int TickMs { get; set; } = Limits.BroadcastIntervalMs;
    [Key("nodes")] public AdminNodeLive[] Nodes { get; set; } = Array.Empty<AdminNodeLive>();
}

[MessagePackObject]
public sealed class AlertPush
{
    [Key("id")] public long Id { get; set; }
    [Key("nodeId")] public int NodeId { get; set; }
    [Key("nodeName")] public string NodeName { get; set; } = "";
    [Key("rule")] public string Rule { get; set; } = "";
    [Key("level")] public string Level { get; set; } = "warning";
    [Key("status")] public string Status { get; set; } = "firing";
    [Key("title")] public string Title { get; set; } = "";
    [Key("message")] public string Message { get; set; } = "";
    [Key("suppressed")] public bool Suppressed { get; set; }
    [Key("at")] public long AtMs { get; set; }
}
```

### 9.5 `Serialization/AgentFormatters.cs`(手写,零反射)

```csharp
using MessagePack;

namespace SNM.Contracts.Serialization;

/// <summary>
/// Hand-written MessagePack formatters for the Agent-facing DTOs.
/// Output is byte-identical to MessagePack.StandardResolver for the [MessagePackObject]/[Key] annotations:
/// array header = maxKey+1, members in key order, smallest integer encoding, nil for null strings/arrays.
/// Readers tolerate longer arrays (extra elements skipped) and shorter arrays (missing => default) and nil (=> null).
/// Only MessagePackWriter/MessagePackReader are used => no reflection, no dynamic code, AOT/trim clean.
/// </summary>
public static class AgentFormatters
{
    // ---------- primitives / helpers ----------

    private static void WriteString(ref MessagePackWriter w, string? s)
    {
        if (s is null) { w.WriteNil(); return; }
        w.Write(s);
    }

    private static string? ReadString(ref MessagePackReader r) => r.TryReadNil() ? null : r.ReadString();

    private static void WriteStringArray(ref MessagePackWriter w, string[]? a)
    {
        if (a is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(a.Length);
        for (int i = 0; i < a.Length; i++) WriteString(ref w, a[i]);
    }

    private static string[] ReadStringArray(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return Array.Empty<string>();
        int n = r.ReadArrayHeader();
        if (n == 0) return Array.Empty<string>();
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = ReadString(ref r) ?? "";
        return a;
    }

    private static void WriteUInt32Array(ref MessagePackWriter w, uint[]? a)
    {
        if (a is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(a.Length);
        for (int i = 0; i < a.Length; i++) w.Write(a[i]);
    }

    private static uint[] ReadUInt32Array(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return Array.Empty<uint>();
        int n = r.ReadArrayHeader();
        if (n == 0) return Array.Empty<uint>();
        var a = new uint[n];
        for (int i = 0; i < n; i++) a[i] = r.ReadUInt32();
        return a;
    }

    // ---------- DiskInfo ----------

    public static void WriteDiskInfo(ref MessagePackWriter w, DiskInfo? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(3);
        WriteString(ref w, v.Mount);
        WriteString(ref w, v.Fs);
        w.Write(v.TotalMb);
    }

    public static DiskInfo? ReadDiskInfo(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        int n = r.ReadArrayHeader();
        var v = new DiskInfo();
        for (int i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.Mount = ReadString(ref r) ?? ""; break;
                case 1: v.Fs = ReadString(ref r) ?? ""; break;
                case 2: v.TotalMb = r.ReadUInt32(); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }

    private static void WriteDiskInfoArray(ref MessagePackWriter w, DiskInfo[]? a)
    {
        if (a is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(a.Length);
        for (int i = 0; i < a.Length; i++) WriteDiskInfo(ref w, a[i]);
    }

    private static DiskInfo[] ReadDiskInfoArray(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return Array.Empty<DiskInfo>();
        int n = r.ReadArrayHeader();
        if (n == 0) return Array.Empty<DiskInfo>();
        var a = new DiskInfo[n];
        for (int i = 0; i < n; i++) a[i] = ReadDiskInfo(ref r) ?? new DiskInfo();
        return a;
    }

    // ---------- NodeInfo ----------

    public static void WriteNodeInfo(ref MessagePackWriter w, NodeInfo? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(13);
        w.Write(v.ProtocolVersion);
        WriteString(ref w, v.AgentVersion);
        WriteString(ref w, v.Hostname);
        WriteString(ref w, v.Os);
        w.Write(v.OsKind);
        WriteString(ref w, v.Arch);
        WriteString(ref w, v.CpuModel);
        w.Write(v.CpuCores);
        w.Write(v.MemTotalMb);
        w.Write(v.SwapTotalMb);
        WriteDiskInfoArray(ref w, v.Disks);
        WriteStringArray(ref w, v.NetInterfaces);
        WriteString(ref w, v.BootId);
    }

    public static NodeInfo? ReadNodeInfo(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        int n = r.ReadArrayHeader();
        var v = new NodeInfo();
        for (int i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.ProtocolVersion = r.ReadByte(); break;
                case 1: v.AgentVersion = ReadString(ref r) ?? ""; break;
                case 2: v.Hostname = ReadString(ref r) ?? ""; break;
                case 3: v.Os = ReadString(ref r) ?? ""; break;
                case 4: v.OsKind = r.ReadByte(); break;
                case 5: v.Arch = ReadString(ref r) ?? ""; break;
                case 6: v.CpuModel = ReadString(ref r) ?? ""; break;
                case 7: v.CpuCores = r.ReadUInt16(); break;
                case 8: v.MemTotalMb = r.ReadUInt32(); break;
                case 9: v.SwapTotalMb = r.ReadUInt32(); break;
                case 10: v.Disks = ReadDiskInfoArray(ref r); break;
                case 11: v.NetInterfaces = ReadStringArray(ref r); break;
                case 12: v.BootId = ReadString(ref r) ?? ""; break;
                default: r.Skip(); break;
            }
        }
        return v;
    }

    // ---------- Heartbeat ----------

    public static void WriteHeartbeat(ref MessagePackWriter w, Heartbeat? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(10);
        w.Write(v.Cpu);
        w.Write(v.Load1);
        w.Write(v.MemUsedMb);
        w.Write(v.SwapUsedMb);
        WriteUInt32Array(ref w, v.DiskUsedMb);
        w.Write(v.NetRxBytes);
        w.Write(v.NetTxBytes);
        w.Write(v.ElapsedMs);
        w.Write(v.UptimeSec);
        w.Write(v.Seq);
    }

    public static Heartbeat? ReadHeartbeat(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        int n = r.ReadArrayHeader();
        var v = new Heartbeat();
        for (int i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.Cpu = r.ReadUInt16(); break;
                case 1: v.Load1 = r.ReadUInt16(); break;
                case 2: v.MemUsedMb = r.ReadUInt32(); break;
                case 3: v.SwapUsedMb = r.ReadUInt32(); break;
                case 4: v.DiskUsedMb = ReadUInt32Array(ref r); break;
                case 5: v.NetRxBytes = r.ReadUInt64(); break;
                case 6: v.NetTxBytes = r.ReadUInt64(); break;
                case 7: v.ElapsedMs = r.ReadUInt16(); break;
                case 8: v.UptimeSec = r.ReadUInt32(); break;
                case 9: v.Seq = r.ReadUInt32(); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }

    // ---------- IpReport ----------

    public static void WriteIpReport(ref MessagePackWriter w, IpReport? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(1);
        WriteStringArray(ref w, v.Ips);
    }

    public static IpReport? ReadIpReport(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        int n = r.ReadArrayHeader();
        var v = new IpReport();
        for (int i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.Ips = ReadStringArray(ref r); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }

    // ---------- HelloResult ----------

    public static void WriteHelloResult(ref MessagePackWriter w, HelloResult? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(5);
        w.Write(v.Accepted);
        w.Write(v.IntervalMs);
        w.Write(v.IpReportIntervalSec);
        w.Write(v.ServerTimeMs);
        WriteString(ref w, v.Message);
    }

    public static HelloResult? ReadHelloResult(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        int n = r.ReadArrayHeader();
        var v = new HelloResult();
        for (int i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.Accepted = r.ReadBoolean(); break;
                case 1: v.IntervalMs = r.ReadUInt16(); break;
                case 2: v.IpReportIntervalSec = r.ReadUInt16(); break;
                case 3: v.ServerTimeMs = r.ReadInt64(); break;
                case 4: v.Message = ReadString(ref r); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }

    // ---------- AgentConfig ----------

    public static void WriteAgentConfig(ref MessagePackWriter w, AgentConfig? v)
    {
        if (v is null) { w.WriteNil(); return; }
        w.WriteArrayHeader(2);
        w.Write(v.IntervalMs);
        w.Write(v.IpReportIntervalSec);
    }

    public static AgentConfig? ReadAgentConfig(ref MessagePackReader r)
    {
        if (r.TryReadNil()) return null;
        int n = r.ReadArrayHeader();
        var v = new AgentConfig();
        for (int i = 0; i < n; i++)
        {
            switch (i)
            {
                case 0: v.IntervalMs = r.ReadUInt16(); break;
                case 1: v.IpReportIntervalSec = r.ReadUInt16(); break;
                default: r.Skip(); break;
            }
        }
        return v;
    }
}
```

> 注意 `MessagePackReader.ReadInt64()` 接受任意整数编码(正 fixint/uint8/…),与 StandardResolver 的 `Int64Formatter` 行为一致;`ReadUInt16()` 对超范围值抛 `OverflowException`(会包装为 `InvalidDataException`,消息被忽略,连接保持)。

### 9.6 `Protocol/StaticMessagePackHubProtocol.cs`

```csharp
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using SNM.Contracts.Protocol.Vendored;
using SNM.Contracts.Serialization;

namespace SNM.Contracts.Protocol;

/// <summary>
/// AOT-friendly SignalR hub protocol. Wire-compatible with Microsoft.AspNetCore.SignalR.Protocols.MessagePack
/// (same name/version/framing) but serializes hub arguments through a closed set of hand-written formatters.
/// Register on the Agent with: builder.Services.AddSingleton&lt;IHubProtocol, StaticMessagePackHubProtocol&gt;();
/// </summary>
public sealed class StaticMessagePackHubProtocol : IHubProtocol
{
    private readonly StaticMessagePackHubProtocolWorker _worker = new();

    public string Name => ProtocolInfo.HubProtocolName;      // "messagepack"
    public int Version => ProtocolInfo.HubProtocolVersion;   // 2
    public TransferFormat TransferFormat => TransferFormat.Binary;

    public bool IsVersionSupported(int version) => version <= Version;

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [NotNullWhen(true)] out HubMessage? message)
        => _worker.TryParseMessage(ref input, binder, out message);

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output) => _worker.WriteMessage(message, output);

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message) => _worker.GetMessageBytes(message);
}

/// <summary>Type-switch based argument (de)serializer. Add new DTOs here AND in AgentFormatters.</summary>
internal sealed class StaticMessagePackHubProtocolWorker : MessagePackHubProtocolWorker
{
    protected override void Serialize(ref MessagePackWriter writer, Type type, object value)
    {
        switch (value)
        {
            case Heartbeat v: AgentFormatters.WriteHeartbeat(ref writer, v); break;
            case NodeInfo v: AgentFormatters.WriteNodeInfo(ref writer, v); break;
            case IpReport v: AgentFormatters.WriteIpReport(ref writer, v); break;
            case HelloResult v: AgentFormatters.WriteHelloResult(ref writer, v); break;
            case AgentConfig v: AgentFormatters.WriteAgentConfig(ref writer, v); break;
            case DiskInfo v: AgentFormatters.WriteDiskInfo(ref writer, v); break;
            case string s: writer.Write(s); break;
            case bool b: writer.Write(b); break;
            case int i: writer.Write(i); break;
            case long l: writer.Write(l); break;
            case uint u: writer.Write(u); break;
            case ulong ul: writer.Write(ul); break;
            case ushort us: writer.Write(us); break;
            case byte by: writer.Write(by); break;
            case byte[] bytes: writer.Write(bytes); break;
            default:
                throw new InvalidDataException($"Type '{type.FullName}' is not supported by StaticMessagePackHubProtocol; add it to the known-type switch and AgentFormatters.");
        }
    }

    protected override object? DeserializeObject(ref MessagePackReader reader, Type type, string field)
    {
        try
        {
            if (type == typeof(Heartbeat)) return AgentFormatters.ReadHeartbeat(ref reader);
            if (type == typeof(NodeInfo)) return AgentFormatters.ReadNodeInfo(ref reader);
            if (type == typeof(IpReport)) return AgentFormatters.ReadIpReport(ref reader);
            if (type == typeof(HelloResult)) return AgentFormatters.ReadHelloResult(ref reader);
            if (type == typeof(AgentConfig)) return AgentFormatters.ReadAgentConfig(ref reader);
            if (type == typeof(DiskInfo)) return AgentFormatters.ReadDiskInfo(ref reader);
            if (type == typeof(string)) return reader.TryReadNil() ? null : reader.ReadString();
            if (type == typeof(bool)) return reader.ReadBoolean();
            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(long)) return reader.ReadInt64();
            if (type == typeof(uint)) return reader.ReadUInt32();
            if (type == typeof(ulong)) return reader.ReadUInt64();
            if (type == typeof(ushort)) return reader.ReadUInt16();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(byte[])) return reader.ReadBytes()?.ToArray();
            if (type == typeof(object)) { reader.Skip(); return null; }
            throw new InvalidDataException($"Type '{type.FullName}' is not supported by StaticMessagePackHubProtocol.");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"Deserializing object of the `{type.Name}` type for '{field}' failed.", ex);
        }
    }
}
```

### 9.7 Vendored 文件清单与改动(来源:`spikes/aot-messagepack/upstream-reference/`,dotnet/aspnetcore `release/10.0`,MIT)

| 源文件 | 目标 | 必要改动 |
|---|---|---|
| `MessagePackHubProtocolWorker.cs` | `src/SNM.Contracts/Protocol/Vendored/MessagePackHubProtocolWorker.cs` | 命名空间 → `SNM.Contracts.Protocol.Vendored`;`using Microsoft.AspNetCore.Internal;` 删除(同命名空间);`ProtocolHelper.TryGetReturnType(binder, invocationId)` 替换为本文件内 `private static Type? TryGetReturnType(IInvocationBinder binder, string invocationId) { try { return binder.GetReturnType(invocationId); } catch { return null; } }`;保留 `#if NETCOREAPP` 分支(net10.0 定义该符号);其余逐字保留,文件头保留 .NET Foundation MIT 版权行。 |
| `BinaryMessageParser.cs` | 同目录 | 命名空间改为 `SNM.Contracts.Protocol.Vendored`,其余不变 |
| `BinaryMessageFormatter.cs` | 同目录 | 同上 |
| `MemoryBufferWriter.cs` | 同目录 | 同上;`#if DEBUG` 段保留 |
| `LICENSE.txt` | `src/SNM.Contracts/THIRD-PARTY-NOTICES.md` | 前置一段说明:上述 4 个文件源自 dotnet/aspnetcore,MIT |

不 vendoring `MessagePackHubProtocol.cs`/`DefaultMessagePackHubProtocolWorker.cs`(它们依赖 `MessagePackSerializer` 非泛型路径与 `ContractlessStandardResolver`,正是要避开的动态代码)。

> 参考实现:`spikes/aot-messagepack/Spike.Contracts/`(`SnmMessagePackHubProtocol` + `SnmArgumentSerializer` + Vendored/)已在本机验证过分析器 0 警告与 JIT 互通;M1 以其为起点重命名为本节的类型名并补齐全部 DTO。该 spike 额外 vendoring 了 `ProtocolHelper.cs`,与本节"内联 `TryGetReturnType`"二者选一即可,推荐内联以减少文件。

### 9.8 Master 侧 Hub 签名(`src/SNM.Master/Hubs`,供实现者对照)

```csharp
[Authorize(Policy = "AgentOnly")]
public sealed class AgentHub : Hub
{
    public Task<HelloResult> Hello(NodeInfo info);   // returns config; never throws for validation issues (uses Accepted=false)
    public Task Ips(IpReport report);
    public Task Hb(Heartbeat hb);                      // method name must be AgentHubMethods.Heartbeat ("Hb") => use [HubMethodName(AgentHubMethods.Heartbeat)] on a method named Heartbeat
}

public sealed class PublicHub : Hub { /* no client->server methods; OnConnectedAsync pushes Snapshot */ }

[Authorize(Policy = "AdminOnly")]
public sealed class AdminHub : Hub
{
    public Task<AdminSnapshot> GetSnapshot();
}
```

Master 向 Agent 推送配置:`await hubContext.Clients.Client(connectionId).SendAsync(AgentHubMethods.ApplyConfig, new AgentConfig { ... })`。

---

## 10. 版本兼容矩阵

| Agent 协议版本 | Master 协议版本 | 行为 |
|---|---|---|
| 1 | 1 | 正常 |
| 1 | 2(未来,仅追加字段) | Master 读旧数组缺失字段取默认;正常 |
| 2 | 1 | Master 对多余元素 `Skip`;若 `ProtocolVersion` 大于自身支持 → `Accepted=false` 提示升级 Master |

Agent 二进制版本(`AgentVersion`)与协议版本独立;Master 在节点列表显示 Agent 版本,便于管理员滚动升级(无 OTA,靠重跑安装脚本)。
