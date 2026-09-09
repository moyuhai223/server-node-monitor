# DESIGN.md — 总体架构与实施蓝图(候选设计 C)

> 本文是六份设计文档的总纲。接口层面的细节分别定案于:`PROTOCOL.md`(Hub/DTO/AOT 协议)、`API.md`(REST)、`DATA.md`(实体/降采样/流量/告警/设置)、`DEPLOY.md`(配置/反代/systemd/安装脚本/CI)、`FRONTEND.md`(管理后台/公开大屏)。本文负责:模块边界、进程内组件与数据流、后台任务、启动顺序、配置来源、日志、安全模型、Agent 采集实现、测试策略、实施顺序与每个模块的验收清单。
>
> 设计角度:**从用户出发**——先定管理后台页面与大屏体验、REST 与实时消息形状,再反推服务端组件与探针能力。所有 BRIEF §3 的 10 个问题在 §0 给出结论索引。

---

## 0. BRIEF §3 十问定案索引

| # | 问题 | 结论(一句话) | 详见 |
|---|---|---|---|
| 1 | MessagePack + AOT 路径 | 路径 1:在 `SNM.Contracts` vendoring MIT 的 `MessagePackHubProtocolWorker` 等 4 个文件,实现 `StaticMessagePackHubProtocol`(Name `messagepack`/Version 2/线格式相同),参数序列化走封闭类型集合的手写 `AgentFormatters`;Agent 不引用 `MessagePackSerializer`/任何 Resolver,ILC 0 警告;Master 与浏览器用官方包;JIT 下字节级等价测试。当前不需要任何 `UnconditionalSuppressMessage`。参考实现已在 `spikes/aot-messagepack/Spike.Contracts`。 | PROTOCOL §0、§8、§9 |
| 2 | 心跳/注册 DTO | `NodeInfo`(13 字段,连接后 `Hello`)与 `Heartbeat`(10 个无符号整数字段,典型 DTO 40 B、帧 50 B,方法名 `Hb`)分离;`IpReport` 每 300 s;无 DateTime,Master 打 `ReceivedAt`。 | PROTOCOL §2、§3.3 |
| 3 | 数据模型与降采样 | `Metrics1m/1h/1d` 同构表(均值+峰值+样本数+区间流量+在线秒),PK `(NodeId, TsMs)`;心跳内存聚合成分钟桶,每分钟 :03 批量 UPSERT;每小时 :02 上卷 1h、每日 00:10 UTC 上卷 1d、每小时 :05 清理;`RollupState` 水位补跑;WAL + 启动 `MigrateAsync`。 | DATA §1、§2.5、§5 |
| 4 | 流量 Delta | Agent 过滤虚拟网卡后上报**聚合累计值**;Master 持久化锚点 `NodeTrafficState`,`cur<last` 或 BootId 变化 = 清零(取 `cur`,受 `Uptime×100 Gbps` 上限约束),正向跳变超 `100 Gbps×dt` 丢弃;按节点时区归入 `TrafficDaily` 与账单周期 `TrafficMonthly`;重置日 1–31 月底钳制;锚点与累加同事务每 60 s 落盘。 | DATA §6、PROTOCOL §5.3 |
| 5 | 告警引擎 | 6 规则 `Offline/CpuHigh/MemHigh/DiskHigh/TrafficHigh/Expiry`,全局阈值 + 节点覆盖,状态机 `Ok→Pending→Firing→Ok` 持久化,去重键 `(NodeId,Rule)`,冷却 30–60 分钟内触发记事件但 `Suppressed`,恢复发 🟢,`Offline/Expiry` 每 24 h 重复提醒;渠道 Telegram(HTML)/Webhook(HMAC 签名)存 DB,支持测试消息。 | DATA §8、API §5、§10 |
| 6 | REST 与模板兼容 | 服务端兼容 vue-naive-admin 2.x 的登录/用户/菜单树/菜单校验接口与 `{code,data,message}` 信封、`pageNo/pageSize→{pageData,total}` 分页;前端少量改造(去验证码、refresh 改 POST + 401 静默刷新、`/api` 基址、Vite 代理不 rewrite、`/hubs` ws 代理、删除 pms/demo 页面)。 | API §0–§2、FRONTEND §3 |
| 7 | 实时推送形状 | Public/Admin Hub:连接即推 `Snapshot`(每节点最近 60 点),每 2 s 一次批量 `Tick` 增量(有变化才发),`NodeChanged/NodeRemoved` 即时,Admin 额外 `Alert`;每节点 90 点环形缓冲;浏览器 DTO 用字符串 Key(msgpack map)。 | PROTOCOL §4 |
| 8 | Agent 采集与网络 | Linux 读 `/proc/stat`、`/proc/meminfo`、`/proc/net/dev`、`/proc/mounts`+`DriveInfo`、`/proc/cpuinfo`(`physical id` 去重 → `2x`);Windows 用 `LibraryImport` P/Invoke `GetSystemTimes/GlobalMemoryStatusEx/GetIfTable2/GetLogicalProcessorInformationEx` + 注册表 CPU 名;`--proxy http://` 或 `socks5://` 设到 `HttpConnectionOptions.Proxy`(作用于 HttpClient 与 ClientWebSocket),WebSocket 经代理失败时 SignalR 自动回退 LongPolling;重连指数退避永不放弃;CLI/环境变量表见 §7.6。 | 本文 §7 |
| 9 | 安装脚本生成 | 后台 `GET /api/nodes/{id}/install-script` 返回一行命令 + 全文;脚本由匿名 `GET /install/{installToken}/agent.sh|agent.ps1` 提供(限速、`no-store`);脚本按 `uname -m` 从 `agent.releaseBaseUrl` 下载 + `SHA256SUMS` 校验 + 非 root 用户 `snm-agent` + systemd `Restart=always`,幂等,`-- uninstall` 卸载。 | API §4.3、DEPLOY §4 |
| 10 | 测试策略 | 单元:契约字节等价/Delta/降采样/防抖/账单周期/GeoIP;集成:`WebApplicationFactory` + 真实 SignalR msgpack 客户端(静态协议与官方协议各一)+ 临时 SQLite;前端 `npm run build`;`scripts/e2e.sh` 本机端到端(Master + Agent JIT + Webhook 接收器)。 | 本文 §10 |

---

## 1. 系统架构

### 1.1 拓扑

```
                     ┌──────────────────────────── Master 进程(SNM.Master,ASP.NET Core 10)────────────────────────────┐
  Agent(AOT) ──ws──▶│ /hubs/agent  AgentHub ──▶ IngestPipeline ──▶ LiveStore(内存) ──▶ RealtimeBroadcaster ─▶ /hubs/public │◀──ws── 大屏浏览器
  Agent(AOT) ──lp──▶│      ▲                      │  │  │                 ▲                          └─▶ /hubs/admin  │◀──ws── 管理后台
                     │   AgentKeyAuth              │  │  └─ TrafficEngine ─┤                                           │
                     │                             │  └──── MinuteAggregator ──▶ MinuteFlusher ──▶ SQLite(WAL)         │
                     │                             └────── HealthMonitor ──▶ AlertEvaluator ──▶ NotificationDispatcher ┼──▶ Telegram / Webhook
                     │ /api/**  Controllers(JWT) ──▶ Services ──▶ SQLite ; /install/{token} ; /(wwwroot: 大屏, /admin/ SPA)│
                     │ 后台:HourlyRollup DailyRollup RetentionSweeper TrafficStateFlusher GeoIpRefresher DbMaintenance    │
                     └──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **单进程、单数据库文件**:Master 是唯一有状态的进程;所有热状态在 `LiveStore`(内存),冷状态在 SQLite。不引入 Redis/消息队列。
- **Agent 只出不进**:不监听端口、无 OTA、无命令执行;唯一下行消息 `ApplyConfig(AgentConfig)`(PROTOCOL §3.3)。
- **浏览器两条通道**:REST(配置/历史查询)+ SignalR(实时)。公开大屏**只有** SignalR。

### 1.2 项目与依赖

| 项目 | 类型 | 引用 | 包(全部集中在 `Directory.Packages.props`) |
|---|---|---|---|
| `src/SNM.Contracts` | classlib,`IsAotCompatible` | — | `MessagePack` 2.5.302、`Microsoft.AspNetCore.SignalR.Common` 10.0.11 |
| `src/SNM.Agent` | exe,`PublishAot` | Contracts | `Microsoft.AspNetCore.SignalR.Client` 10.0.11 |
| `src/SNM.Master` | web | Contracts | `Microsoft.AspNetCore.SignalR.Protocols.MessagePack`、`Microsoft.EntityFrameworkCore.Sqlite`、`Microsoft.EntityFrameworkCore.Design`(PrivateAssets)、`Microsoft.AspNetCore.Authentication.JwtBearer` 各 10.0.11 |
| `tests/SNM.Contracts.Tests` | xunit | Contracts | `MessagePack`(反射 resolver 交叉验证)、`Microsoft.AspNetCore.SignalR.Protocols.MessagePack` |
| `tests/SNM.Master.Tests` | xunit | Master、Contracts | `Microsoft.AspNetCore.Mvc.Testing`、`Microsoft.AspNetCore.SignalR.Client`、`Microsoft.Extensions.TimeProvider.Testing` |
| `tests/SNM.Agent.Tests` | xunit | Agent(`InternalsVisibleTo`) | — |

新增包理由(BRIEF §2.1 要求):`Microsoft.AspNetCore.SignalR.Common` 提供 `IHubProtocol/HubMessage/IInvocationBinder`,是 SignalR.Client 的传递依赖,AOT 干净;`Microsoft.Extensions.TimeProvider.Testing` 仅测试项目用,提供 `FakeTimeProvider` 加速后台服务测试。**不引入** System.CommandLine、Serilog、Polly、Dapper、Swashbuckle 等任何额外包。

`Directory.Build.props`(全仓):`net10.0`、`Nullable=enable`、`ImplicitUsings=enable`、`LangVersion=latest`、`TreatWarningsAsErrors=true`、`InvariantGlobalization=true`、`SatelliteResourceLanguages=en`、`Deterministic=true`、`ContinuousIntegrationBuild`(CI 时)。`SNM.Agent.csproj` 另加:`PublishAot=true`、`IsAotCompatible=true`、`EnableTrimAnalyzer/EnableAotAnalyzer/EnableSingleFileAnalyzer=true`、`OptimizationPreference=Size`、`StripSymbols=true`、`UseSystemResourceKeys=true`、`EventSourceSupport=false`、`HttpActivityPropagationSupport=false`、`AllowUnsafeBlocks=true`、`AssemblyName=snm-agent`。

### 1.3 目录(BRIEF §2.2 固定布局的细化)

```
src/SNM.Contracts/
  Constants.cs                    ProtocolInfo/HubPaths/AgentHubMethods/PublicHubMethods/AdminHubMethods/OsKinds/Units/Limits
  Agent/AgentDtos.cs              NodeInfo DiskInfo Heartbeat IpReport HelloResult AgentConfig(整数 Key)
  Realtime/RealtimeDtos.cs        LivePoint PublicNodeCard PublicSnapshot PublicNodeUpdate PublicTick IpEntry DiskLive AdminPoint AdminNodeLive AdminNodeUpdate AdminTick AdminSnapshot AlertPush(字符串 Key)
  Serialization/AgentFormatters.cs
  Protocol/StaticMessagePackHubProtocol.cs
  Protocol/Vendored/{MessagePackHubProtocolWorker,BinaryMessageParser,BinaryMessageFormatter,MemoryBufferWriter}.cs
  THIRD-PARTY-NOTICES.md
src/SNM.Agent/
  Program.cs                      入口:解析 CLI/env → 构建 HubClient → RunAsync;处理 SIGTERM/Ctrl+C
  Cli/AgentOptions.cs  Cli/ArgParser.cs
  Collect/ISystemCollector.cs  Collect/LinuxCollector.cs  Collect/WindowsCollector.cs  Collect/NetFilter.cs  Collect/IpDiscovery.cs  Collect/DiskSelector.cs
  Collect/Native/Win32.cs         LibraryImport 声明
  Net/HubClient.cs  Net/SnmRetryPolicy.cs  Net/ProxyFactory.cs
  Logging/AgentConsoleLoggerProvider.cs
  AgentVersion.cs                 InformationalVersion 读取
src/SNM.Master/
  Program.cs                      组合根(§5 启动顺序)
  appsettings.json  appsettings.Development.json
  Config/SnmOptions.cs  Config/JwtOptions.cs  Config/FlatEnvMapper.cs
  Data/SnmDbContext.cs  Data/Entities/*.cs  Data/Migrations/*  Data/SqliteWriteGate.cs  Data/SqlitePragmaInterceptor.cs  Data/SnmJsonContext.cs
  Auth/AgentKeyAuthenticationHandler.cs  Auth/JwtTokenService.cs  Auth/PasswordService.cs  Auth/AuthPolicies.cs
  Hubs/AgentHub.cs  Hubs/PublicHub.cs  Hubs/AdminHub.cs
  Live/LiveStore.cs  Live/LiveNode.cs  Live/RingBuffer.cs  Live/IngestPipeline.cs  Live/MinuteAggregator.cs  Live/TrafficEngine.cs  Live/BillingCycle.cs  Live/IpMerger.cs
  Realtime/RealtimeBroadcaster.cs  Realtime/SnapshotFactory.cs
  Alerts/AlertEvaluator.cs  Alerts/AlertRules.cs  Alerts/AlertStateMachine.cs  Alerts/NotificationDispatcher.cs  Alerts/Channels/{TelegramSender,WebhookSender}.cs  Alerts/MessageTemplates.cs
  Background/StartupInitializer.cs  Background/MinuteFlusher.cs  Background/HourlyRollup.cs  Background/DailyRollup.cs  Background/RetentionSweeper.cs  Background/TrafficStateFlusher.cs  Background/HealthMonitor.cs  Background/GeoIpRefresher.cs  Background/DbMaintenance.cs  Background/Scheduling.cs
  GeoIp/GeoIpDatabase.cs  GeoIp/GeoIpLoader.cs
  Services/NodeService.cs  Services/SettingsService.cs  Services/ChannelService.cs  Services/AlertQueryService.cs  Services/MetricsQueryService.cs  Services/TrafficQueryService.cs  Services/DashboardService.cs  Services/InstallScriptService.cs  Services/MenuCatalog.cs
  Api/ApiEnvelope.cs  Api/ApiException.cs  Api/ExceptionHandling.cs  Api/Paging.cs
  Api/Controllers/{Auth,User,Permission,Dashboard,Nodes,Channels,Alerts,Settings,System,Public,Install}Controller.cs
  Templates/install-agent.sh  Templates/install-agent.ps1(EmbeddedResource)
  wwwroot/                        构建产物(git 忽略):index.html css/ js/(大屏) admin/(SPA)
tests/…  web/admin  web/public  deploy/  scripts/
```

---

## 2. 进程内组件与数据流(Master)

### 2.1 组件职责

| 组件 | 生命周期 | 职责 | 输入 → 输出 |
|---|---|---|---|
| `AgentKeyAuthenticationHandler` | 请求级 | `/hubs/agent` 的 Bearer AgentKey 认证(PROTOCOL §3.1),查 `Nodes` 表(带 60 s 内存缓存,轮换/禁用时失效) | Header/query → `ClaimsPrincipal{snm:node_id, snm:role=agent}` |
| `AgentHub` | 连接级 | `OnConnected`:`LiveStore.Attach(nodeId, connId, remoteIp)`,踢旧连接;`Hello/Ips/Hb` 校验后交 `IngestPipeline`;`OnDisconnected`:`Detach` | Hub 调用 → LiveStore 变更 |
| `IngestPipeline` | 单例 | 每条心跳的处理顺序:去重(Seq)→ 打 `ReceivedAt` → 速率 → `TrafficEngine.Apply` → `MinuteAggregator.Add` → 环形缓冲 → 标记 dirty 供广播 | `Heartbeat` → `LivePoint`、`dRx/dTx` |
| `LiveStore` | 单例 | 全部节点热状态(DATA §4);`ConcurrentDictionary<int, LiveNode>`;每节点一把 `lock`;提供快照构造所需的只读视图 | — |
| `TrafficEngine` | 单例 | DATA §6 的 Delta/归属/周期算法;内存累加器;`TrafficStateFlusher` 每 60 s 取走 | 心跳 → 增量、周期累计 |
| `MinuteAggregator` | 单例 | DATA §5.1 分钟桶;关闭的桶进 `Channel<Bucket>` | 心跳 → 桶 |
| `HealthMonitor` | BackgroundService 5 s | `Online = now − LastSeen ≤ OfflineSeconds`;翻转时标记 dirty(进 Tick)并触发 `AlertEvaluator` 的 `Offline` 即时评估 | — |
| `RealtimeBroadcaster` | BackgroundService 2 s | 收集 dirty 节点 → `PublicTick`/`AdminTick` → 两个组;`NodeChanged/NodeRemoved` 由 `NodeService` 通过它即时发送 | LiveStore → Hub 组 |
| `SnapshotFactory` | 单例 | 由 LiveStore + Nodes 缓存构造 `PublicSnapshot`/`AdminSnapshot`/`PublicNodeCard`/`AdminNodeLive`(脱敏在此处集中实现) | — |
| `AlertEvaluator` | BackgroundService 10 s | DATA §8 状态机;事件写库;推 `Alert` 到 admin 组;入队通知 | LiveStore/DB → AlertEvents |
| `NotificationDispatcher` | BackgroundService(队列) | 消费 `Channel<AlertNotification>`,按渠道发送 + 重试,回写结果 | 事件 → HTTP |
| `SettingsService` | 单例 | 设置组缓存(`Current` 不可变快照 + `Changed` 事件);写入原子;阈值变化即时可见 | DB ⇄ 内存 |
| `GeoIpDatabase/GeoIpRefresher` | 单例 / BackgroundService | 加载 CSV 到数组、二分查找;定期刷新(DATA §7.3) | IP → CC |
| `NodeService` | scoped | 节点 CRUD 的业务副作用:生成 Key/Token、Abort 连接、推 `ApplyConfig`、广播结构变更、立即评估 `Expiry` | REST → DB + LiveStore + Hubs |
| `InstallScriptService` | 单例 | 模板占位符替换、命令行拼装、`baseUrl` 推断 | 节点 + 设置 → 脚本文本 |
| `MenuCatalog` | 静态 | 菜单树常量(API §2) | — |
| `StartupInitializer` | IHostedService(第一个注册) | §5 启动序列;完成后 `StartupGate.Ready.SetResult()` | — |

### 2.2 心跳路径(热路径,目标 < 50 µs/条,无 DB 访问)

```
AgentHub.Hb(hb)
  nodeId = Context.User claim; live = LiveStore.Get(nodeId)           # 缺失(Master 重启后 Hello 之前) → 从 DB 懒加载静态信息
  lock(live):
    if hb.Seq <= live.LastSeq && live.LastSeq != 0 → drop, counter++, return
    live.LastSeq = hb.Seq; now = UtcNow
    (rxBps, txBps) = Rate(hb, live.LastHeartbeat, live.LastReceivedMs, now)      # PROTOCOL §5.2
    (dRx, dTx)     = TrafficEngine.Apply(live, hb, now)                           # DATA §6.2
    MinuteAggregator.Add(live, hb, now, rxBps, txBps, dRx, dTx)                   # DATA §5.1
    point = new LivePoint{ T=now, Cpu=hb.Cpu, Mem=hb.MemUsedMb*1000/MemTotalMb, RxBps, TxBps }
    live.Ring.Push(point); live.LastHeartbeat = hb; live.LastReceivedMs = now; live.LastSeenMs = now
    live.Dirty = true                                                             # Broadcaster 下个周期取走
```
DB 写入**从不**发生在心跳路径上;所有落盘由后台服务批量完成。

### 2.3 REST 写路径的副作用矩阵(实现 `NodeService` 时逐项对照)

| 操作 | DB | LiveStore | Hub | 其他 |
|---|---|---|---|---|
| 创建节点 | INSERT Nodes(+AgentKey/InstallToken) | `Add(LiveNode)` | admin `NodeChanged`;若 `IsPublic` → public `NodeChanged` | — |
| 修改 `intervalMs/ipReportIntervalSec` | UPDATE | 更新配置 | 向节点连接 `ApplyConfig` | — |
| `enabled=false` | UPDATE | `Online=false` | `Context.Abort()`;admin `NodeChanged`;public `NodeRemoved` | AgentKey 缓存失效;AlertEvaluator 下一 tick 自动 resolve |
| `isPublic/publicName/sortOrder/countryCodeOverride` 变化 | UPDATE | 更新 | public `NodeChanged` 或 `NodeRemoved`;admin `NodeChanged` | — |
| 到期日/告警开关/覆盖阈值变化 | UPDATE | 更新 | — | 立即评估该节点 `Expiry` |
| 轮换 Key | UPDATE | — | Abort 连接 | 缓存失效 |
| 删除节点 | DELETE(级联) | `Remove` | Abort;admin+public `NodeRemoved` | 清理 AlertStates 内存 |
| 修改设置 `general.publicTitle` | UPSERT Settings | — | public 组重推 `Snapshot` | — |
| 修改渠道 | UPSERT | — | — | `NotificationDispatcher` 下次发送读取新配置(无缓存) |

---

## 3. 后台服务与调度总表

所有后台任务都是 `BackgroundService`,共用 `Background/Scheduling.cs` 的两个原语:
- `AlignedPeriodicTimer(period, offset)`:先 `Task.Delay` 到下一个 `offset` 对齐点,再 `PeriodicTimer(period)`;例如 `period=1min, offset=3s` → 每分钟 :03。
- `RunSafely(Func<CancellationToken, Task>)`:tick 内部 try/catch 全部异常并记 `Error` 日志(同一异常消息每 10 分钟最多 1 条),**永不让服务退出**。

所有服务在 `ExecuteAsync` 开头 `await StartupGate.Ready`(迁移、种子、LiveStore 加载完成后才开始)。

| 服务 | 周期 / 时刻 | 动作 | 幂等/补跑 | 文档 |
|---|---|---|---|---|
| `StartupInitializer` | 启动一次 | 迁移、种子、加载 GeoIP、重建 LiveStore | — | §5 |
| `HealthMonitor` | 每 5 s | 在线判定、翻转标 dirty、触发 `Offline` 即时评估 | 无状态 | PROTOCOL §4.4 |
| `RealtimeBroadcaster` | 每 2 s(`Limits.BroadcastIntervalMs`) | 组装并发送 `PublicTick/AdminTick` | 无状态 | PROTOCOL §4.4 |
| `MinuteFlusher` | 每分钟 :03 | 关闭上一分钟桶,批量 UPSERT `Metrics1m` | UPSERT;失败留队列(上限 10000) | DATA §5.2 |
| `TrafficStateFlusher` | 每 60 s(启动 +60 s) | 锚点 + `TrafficDaily/Monthly` 累加 + `Nodes.LastSeenMs` 同事务 | 同事务保证不丢不重 | DATA §6.2 |
| `HourlyRollup` | 每小时 :02:00 UTC | 1m → 1h(水位补跑) | UPSERT + `RollupState` | DATA §5.3 |
| `DailyRollup` | 每日 00:10:00 UTC | 1h → 1d | 同上 | DATA §5.4 |
| `RetentionSweeper` | 每小时 :05:00 | 删除过期 1m/1h/1d/AlertEvents/TrafficDaily/RefreshTokens | 天然幂等,分批 5000 行 | DATA §5.5 |
| `AlertEvaluator` | 每 10 s;`Expiry` 每日 `alert.expiryCheckTime`(全局时区)+ 节点保存后立即;启动后 60 s 内不评估 `Offline` | 状态机推进、事件写库、`Alert` 推送、通知入队 | 状态持久化 | DATA §8.3 |
| `NotificationDispatcher` | 事件驱动 | 发送 Telegram/Webhook,重试 1 s/5 s/30 s | 每事件每渠道一次 | DATA §8.4 |
| `GeoIpRefresher` | 启动 +5 s;成功后每 `geoip.refreshDays`;失败后 1 h | 下载 → 校验 → 原子替换 → 重载 → 状态写 Settings | 幂等 | DATA §7.3 |
| `DbMaintenance` | 每日 04:00 UTC | `wal_checkpoint(TRUNCATE)`、`incremental_vacuum(2000)`、`optimize` | 幂等 | DATA §1 |

写入纪律:以上服务的批量写都经 `SqliteWriteGate`(`SemaphoreSlim(1,1)`)串行化并各自开显式事务;REST 写直接走 `DbContext`(依赖 `busy_timeout=5000`)。

---

## 4. 配置来源

### 4.1 加载顺序(后者覆盖前者)

1. `appsettings.json`(随 Master 发布)
2. `appsettings.{Environment}.json`
3. 环境变量(标准 `Section__Key` 形式,例如 `Jwt__AccessTokenMinutes=60`)
4. **扁平 `SNM_*` 便捷变量**(`Config/FlatEnvMapper.cs` 在 3 之后把下表映射为配置键,便于 systemd/Docker 书写)
5. 命令行 `--Section:Key=value`

### 4.2 配置键与环境变量对照

| 配置键(appsettings) | 便捷环境变量 | 默认 | 说明 |
|---|---|---|---|
| `Snm:Listen` | `SNM_LISTEN` | `http://127.0.0.1:5080` | Kestrel 监听地址(等价于 `ASPNETCORE_URLS`,后者若设置则优先);容器内用 `http://0.0.0.0:5080` |
| `Snm:DataDir` | `SNM_DATA_DIR` | `./data` | 数据目录:`snm.db*`、`geoip/`、`jwt.key`、`backup/`;启动时创建 |
| `Snm:Admin:User` | `SNM_ADMIN_USER` | `admin` | 仅在 `AdminUsers` 表为空时用于创建首个管理员 |
| `Snm:Admin:Password` | `SNM_ADMIN_PASSWORD` | (随机 16 字符,Warning 日志打印一次) | 同上 |
| `Snm:Seed:PublicBaseUrl` | `SNM_PUBLIC_BASE_URL` | 空 | 首次建库写入 `general.publicBaseUrl`(安装脚本用的公网地址) |
| `Snm:Seed:ReleaseBaseUrl` | `SNM_RELEASE_BASE_URL` | `https://github.com/OWNER/server-node-monitor/releases/latest/download` | 首次建库写入 `agent.releaseBaseUrl` |
| `Snm:KnownProxies` | `SNM_KNOWN_PROXIES` | 空(仅 loopback) | 逗号分隔 IP 或 CIDR,`ForwardedHeaders` 信任列表;Nginx 与 Master 同机时留空即可 |
| `Snm:ForwardLimit` | — | `1` | `ForwardedHeadersOptions.ForwardLimit` |
| `Jwt:SigningKey` | `SNM_JWT_KEY` | 空 → 自动生成 64 字节写入 `{DataDir}/jwt.key`(0600) | HS256 密钥(Base64);显式配置时不写文件 |
| `Jwt:AccessTokenMinutes` | `SNM_JWT_ACCESS_MINUTES` | `120` | |
| `Jwt:RefreshTokenDays` | `SNM_JWT_REFRESH_DAYS` | `30` | |
| `Jwt:Issuer` / `Jwt:Audience` | — | `snm` / `snm-admin` | |
| `Realtime:PublicMaxConnections` | `SNM_PUBLIC_MAX_CONN` | `500` | 超出直接 Abort |
| `Logging:LogLevel:Default` | `SNM_LOG_LEVEL` | `Information` | |
| `Logging:Json` | `SNM_LOG_JSON` | `false` | `true` 用 `JsonConsole` |
| `Database:BusyTimeoutMs` | — | `5000` | PRAGMA busy_timeout |

运行期可变的业务设置(阈值、渠道、GeoIP 地址、大屏标题等)**不在**配置文件,而在 DB `Settings` 表(DATA §2.12),通过后台修改。`appsettings.json` 完整默认内容见 DEPLOY §1.2。Agent 的 CLI/环境变量见 §7.6。

---

## 5. 启动顺序(`Program.cs`)

```
1  builder = WebApplication.CreateBuilder(args)
2  配置:AddJsonFile ×2 → AddEnvironmentVariables → FlatEnvMapper.Apply → AddCommandLine
3  Kestrel:UseUrls(Snm:Listen);Directory.CreateDirectory(DataDir)
4  日志:ClearProviders → AddSimpleConsole/AddJsonConsole(§6)
5  服务注册:
   - Options:SnmOptions、JwtOptions(密钥文件在此加载/生成)
   - DbContext:AddDbContextFactory<SnmDbContext>(UseSqlite(connStr).AddInterceptors(SqlitePragmaInterceptor)) + AddDbContext(同配置,scoped 给 Controller)
   - 认证:AddAuthentication().AddJwtBearer("Bearer", …OnMessageReceived 当路径以 /hubs/ 开头时从 ?access_token 取 token)
                              .AddScheme<AgentKeyOptions, AgentKeyAuthenticationHandler>("AgentKey", …)
   - 授权:AddAuthorizationBuilder().AddPolicy("AdminOnly", p => p.AddAuthenticationSchemes("Bearer").RequireClaim("snm:role","admin"))
                                    .AddPolicy("AgentOnly", p => p.AddAuthenticationSchemes("AgentKey").RequireClaim("snm:role","agent"))
   - 限速:AddRateLimiter(FixedWindow 按 RemoteIp 分区:policy "login" 5/min、"install" 10/min,阈值来自 Settings.security 启动快照)
   - SignalR:AddSignalR(PROTOCOL §7 固定值).AddMessagePackProtocol()
   - Controllers:AddControllers().AddJsonOptions(camelCase;枚举由自定义 converter 输出小写字符串;WriteIndented=false)
   - 单例:LiveStore、IngestPipeline、TrafficEngine、MinuteAggregator、SettingsService、GeoIpDatabase、SnapshotFactory、InstallScriptService、JwtTokenService、PasswordService、SqliteWriteGate、StartupGate、Meter、TimeProvider.System
   - scoped:NodeService、ChannelService、AlertQueryService、MetricsQueryService、TrafficQueryService、DashboardService
   - Hosted(注册顺序即启动顺序):StartupInitializer、HealthMonitor、RealtimeBroadcaster、MinuteFlusher、TrafficStateFlusher、AlertEvaluator、NotificationDispatcher、HourlyRollup、DailyRollup、RetentionSweeper、GeoIpRefresher、DbMaintenance
6  app 管道(顺序固定):
   UseForwardedHeaders(XForwardedFor|XForwardedProto, KnownProxies/KnownNetworks 来自配置, ForwardLimit)
   UseExceptionHandler(ApiEnvelope 500)  → UseStatusCodePages(仅 /api 前缀输出信封)
   静态文件:UseDefaultFiles + UseStaticFiles(wwwroot;index.html 与 admin/index.html → Cache-Control: no-cache;/admin/assets/* 与文件名含 hash 的资源 → public,max-age=31536000,immutable)
   UseRouting → UseRateLimiter → UseAuthentication → UseAuthorization
   MapControllers();MapHub<AgentHub>(HubPaths.Agent, WebSockets|LongPolling).RequireAuthorization("AgentOnly");MapHub<PublicHub>(HubPaths.Public);MapHub<AdminHub>(HubPaths.Admin).RequireAuthorization("AdminOnly")
   MapGet("/admin", → 301 "/admin/");MapFallbackToFile("/admin/{*path}", "admin/index.html")
7  app.RunAsync()
   StartupInitializer.StartAsync(在其他 Hosted 之前被调用,且在 StartAsync 内**同步等待**完成):
     a  MigrateAsync(禁止 EnsureCreated)
     b  SettingsService.EnsureDefaultsAsync(缺失组写默认;首次建库写 Seed 的 publicBaseUrl/releaseBaseUrl)
     c  AdminUsers 为空 → 创建管理员(打印随机密码 Warning)
     d  GeoIpDatabase.TryLoadFromDisk(存在即加载,不存在跳过)
     e  LiveStore.LoadAsync:Nodes → LiveNode;NodeTrafficState → 锚点;TrafficMonthly(当前周期)→ CycleRx/Tx;AlertStates → 状态机内存
     f  StartupGate.Ready.SetResult();日志 "SNM Master ready, listen=…, nodes=N"
   其他服务 ExecuteAsync 内 await StartupGate.Ready 后开始。
8  关闭(SIGTERM,HostOptions.ShutdownTimeout=20 s):ApplicationStopping → MinuteFlusher 强制 flush 当前桶、TrafficStateFlusher 立即落盘、AlertEvaluator 落盘 LastEval;Hub 连接由 Kestrel 关闭。
```

`Program` 类需 `public partial class Program {}` 以供 `WebApplicationFactory<Program>`。

---

## 6. 日志

- 提供者:`Microsoft.Extensions.Logging` 控制台;`SimpleConsole`(单行、`TimestampFormat="yyyy-MM-dd HH:mm:ss.fff "`、`UseUtcTimestamp=true`)或 `JsonConsole`(`Logging:Json=true`)。systemd 收集到 journald,Docker 收集 stdout。不写文件、不引第三方日志库。
- 级别:`SNM.*` Information;`Microsoft.AspNetCore` Warning;`Microsoft.AspNetCore.SignalR`、`Microsoft.AspNetCore.Http.Connections` Warning;`Microsoft.EntityFrameworkCore.Database.Command` Warning(开发环境 Information)。
- 结构化字段约定(消息模板参数名):`NodeId`、`NodeName`、`ConnectionId`、`RemoteIp`、`Rule`、`EventId`、`Channel`。
- 必须出现的日志(供 e2e 与运维依赖):启动 ready 行;管理员初始密码(仅生成时);Agent 连接/断开(Information:`Agent connected NodeId={NodeId} RemoteIp={RemoteIp}`);告警触发/恢复/抑制(Information);通知发送失败(Warning,含渠道与错误);GeoIP 刷新成功/失败;每日维护完成。
- 噪声抑制:同一 `(NodeId, 消息类别)` 的 Warning 每小时最多 1 条(`RateLimitedLogger` 小工具);心跳丢弃只计数不逐条记录。
- 指标:`System.Diagnostics.Metrics` `Meter("SNM.Master")`:`snm_agent_hb_total`、`snm_agent_hb_dropped_total`、`snm_bind_failures_total`、`snm_notifications_total{channel,result}`、`snm_live_connections{hub}`;仅通过 `GET /api/system/info.counters` 暴露(不做 Prometheus 端点)。
- Agent 日志:自定义极简 `AgentConsoleLoggerProvider`(避免 `Microsoft.Extensions.Logging.Console` 的格式化器体积),格式 `HH:mm:ss LVL message`;`--log-level` 控制;SignalR 客户端内部日志接入同一 provider。

---

## 7. Agent(SNM.Agent)设计

### 7.1 运行流程

```
main:
  opts = ArgParser.Parse(args, env)          # 错误 → 退出码 2;缺 --server/--key → 3;--version → 打印退出 0
  collector = OperatingSystem.IsLinux() ? new LinuxCollector(opts) : OperatingSystem.IsWindows() ? new WindowsCollector(opts) : Unsupported(退出 2)
  client = new HubClient(opts, collector)     # PROTOCOL §3.5 的构建方式
  await client.RunAsync(cts.Token)           # 内含:connect loop → Hello → Ips → 心跳循环 → 处理 Reconnected/Closed
```

`HubClient.RunAsync`:
1. `StartAsync` 失败 → 按 PROTOCOL §6.2 退避(401/403 → 300 s);成功 → `Hello(NodeInfo)`;`Accepted=false` → 断开 + 退避。
2. 启动三个循环(同一 `CancellationTokenSource`,连接关闭时取消):
   - **心跳循环**:`PeriodicTimer(IntervalMs)`;每 tick `collector.Sample()` → `Heartbeat` → `SendAsync("Hb")`;`Seq++`;`ElapsedMs = Stopwatch` 实测(> 65535 钳制)。
   - **IP 循环**:立即一次,之后每 `IpReportIntervalSec`;结果与上次不同也立即发(比较排序后的列表)。
   - **拓扑巡检**:每 60 s 重新枚举挂载点与网卡;集合变化 → 重新 `Hello`(更新 `Disks/NetInterfaces`)。
3. `On<AgentConfig>("ApplyConfig")` → 钳制到 `[Limits.Min,Max]` → 重建 `PeriodicTimer`。
4. `Reconnected` → 重跑步骤 1 的 `Hello` 与 IP 立即上报;`Seq` 不重置。`Closed`(理论上不会发生,因为重试策略永不返回 null;若发生)→ 外层循环重新 `StartAsync`。
5. SIGTERM/Ctrl+C → `StopAsync`(CloseTimeout 5 s)→ 退出 0。

### 7.2 Linux 采集(`LinuxCollector`)

| 指标 | 来源 | 算法 |
|---|---|---|
| CPU ‰ | `/proc/stat` 首行 `cpu` | `busy = user+nice+system+irq+softirq+steal`,`total = busy+idle+iowait`;`Cpu = Δbusy*1000/Δtotal`(Δtotal=0 → 上次值);首个样本 0 |
| Load1 | `/proc/loadavg` 第一列 | `round(x*100)`,上限 65534 |
| 内存 | `/proc/meminfo` | `MemUsedMb = (MemTotal − MemAvailable)/1024`(无 `MemAvailable` 的旧内核:`MemTotal − MemFree − Buffers − Cached − SReclaimable`);`SwapUsedMb = (SwapTotal − SwapFree)/1024` |
| 磁盘 | `/proc/mounts` + `DriveInfo(mount)` | 挂载点白名单 fstype ∈ `ext2/3/4, xfs, btrfs, zfs, f2fs, jfs, reiserfs, ntfs, ntfs3, vfat, exfat, fuseblk`;排除挂载点前缀 `/proc /sys /dev /run /boot/efi /snap /var/lib/docker /var/lib/containers`;同一设备多次挂载只取最短路径;`TotalMb ≥ 1024` 才计入;上限 32;**已用 = `TotalSize − TotalFreeSpace`**(与 `df` 的 Used 列一致) |
| 网卡累计 | `/proc/net/dev` | 对 `NetFilter.IsCounted(name)` 的行求 `rx_bytes`、`tx_bytes` 之和(PROTOCOL §5.3 规则);Up 状态读 `/sys/class/net/<if>/operstate`(`up` 或 `unknown` 视为 Up) |
| Uptime | `/proc/uptime` 第一列 | 取整 |
| BootId | `/proc/sys/kernel/random/boot_id` | 原文 |
| 主机名 | `/proc/sys/kernel/hostname` | `--name` 覆盖 |
| OS | `/etc/os-release` `PRETTY_NAME` | 失败 → `RuntimeInformation.OSDescription` |
| CpuModel | `/proc/cpuinfo` | `model name` 首个值;socket 数 = distinct `physical id` 个数(无该字段 → 1);`>1` 时前缀 `"{n}x "`;ARM 无 `model name` 时用 `Hardware` 行,或拼 `"ARM implementer 0x41 part 0xd0c"` |
| CpuCores | `Environment.ProcessorCount` | |
| IP | `NetworkInterface.GetAllNetworkInterfaces()`(AOT 可用) | PROTOCOL §5.4 过滤;被排除网卡上的地址不上报 |

文件读取用 `File.ReadAllText`/`File.ReadLines` + `Span` 解析,避免正则与 LINQ 分配;所有解析容错(缺行取 0)。

### 7.3 Windows 采集(`WindowsCollector`,`LibraryImport` 源生成 P/Invoke,`Collect/Native/Win32.cs`)

| 指标 | API | 说明 |
|---|---|---|
| CPU | `kernel32!GetSystemTimes(out idle, out kernel, out user)` | `busy = (kernel−idle)+user`,`total = kernel+user`,Δ 法同 Linux |
| 内存/Swap | `kernel32!GlobalMemoryStatusEx(ref MEMORYSTATUSEX)` | `MemUsed = TotalPhys − AvailPhys`;`SwapUsed = max(0,(TotalPageFile−AvailPageFile)−MemUsed)`;`SwapTotal = max(0, TotalPageFile − TotalPhys)` |
| 磁盘 | `DriveInfo.GetDrives()` | `DriveType.Fixed && IsReady`;`Mount = "C:\"`,`Fs = DriveFormat` |
| 网卡累计 | `iphlpapi!GetIfTable2(out MIB_IF_TABLE2*)` + `FreeMibTable` | `InOctets/OutOctets`(64 位);过滤 `Type == IF_TYPE_SOFTWARE_LOOPBACK`、`OperStatus != IfOperStatusUp`、`Description` 含 PROTOCOL §5.3 的关键字;用 `Alias` 作为名字 |
| Uptime | `kernel32!GetTickCount64()/1000` | |
| BootId | `(UtcNow − GetTickCount64 ms).ToUnixTimeSeconds()` 取整到 10 s 的字符串 | 重启后必然变化 |
| CpuModel | 注册表 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString`(`Microsoft.Win32.Registry`,AOT 友好) | socket 数用 `GetLogicalProcessorInformationEx(RelationProcessorPackage)` 计数 |
| Load1 | — | 固定 `Units.LoadNotAvailable` |
| 主机名/OS | `Environment.MachineName` / `RuntimeInformation.OSDescription` | |
| IP | `NetworkInterface.GetAllNetworkInterfaces()` | 同 Linux |

所有结构体 `[StructLayout(LayoutKind.Sequential)]`;禁止 `DllImport`(运行时封送需要 IL stub,AOT 分析器会警告)。

### 7.4 网卡过滤与 IP 发现

实现 PROTOCOL §5.3/§5.4;`NetFilter` 纯函数、可单测:`IsCounted(name, isUp, isLoopback, description?, whitelist, blacklist)`。`--net-if` 白名单优先;过滤后为空 → 除 `lo` 外全部 Up 网卡 + Warning。

### 7.5 代理与传输

- `--proxy http://host:port`、`http://user:pass@host:port`、`socks5://host:port`、`socks5://user:pass@host:port`(亦接受 `socks4://`、`socks4a://`)→ `new WebProxy(uri) { Credentials = … }`,赋给 `HttpConnectionOptions.Proxy`。SignalR .NET 客户端把它同时设到 `HttpClientHandler.Proxy`(negotiate/LongPolling;`SocketsHttpHandler` 原生支持 http 与 socks4/4a/5)与 `ClientWebSocketOptions.Proxy`(WebSocket)。
- 行为与回退:HTTP 代理下 WebSocket 通过 `CONNECT` 隧道正常工作;SOCKS5 代理下 .NET 7+ 的 `ClientWebSocket` 经内部 `SocketsHttpHandler` 建连,通常可用;**若 WebSocket 建连失败**(代理不支持/被拦),SignalR 客户端按 negotiate 返回的传输列表自动回退到 **LongPolling**(纯 HTTP,任何代理都支持),日志 Warning 一次"WebSockets failed, using LongPolling";功能等价,只是心跳延迟略高。Agent 不设置 `SkipNegotiation`,以保留回退能力。
- `--insecure`:接受自签名证书(`HttpMessageHandlerFactory` 设 `ServerCertificateCustomValidationCallback`,并对 `WebSocketConfiguration` 设 `RemoteCertificateValidationCallback`),默认关闭并在启用时打 Warning。
- 环境变量 `HTTPS_PROXY/ALL_PROXY` 不自动读取(显式优于隐式),避免意外经代理。

### 7.6 CLI 参数与环境变量

| 参数 | 环境变量 | 必填 | 默认 | 说明 |
|---|---|---|---|---|
| `--server <url>` | `SNM_SERVER` | 是 | — | Master 基地址,如 `https://monitor.example.com`;自动拼 `/hubs/agent` |
| `--key <AgentKey>` | `SNM_KEY` | 是 | — | 43 字符 Base64Url |
| `--proxy <url>` | `SNM_PROXY` | 否 | 无 | §7.5 |
| `--interval <ms>` | `SNM_INTERVAL` | 否 | 2000 | 本地初始心跳间隔;`Hello` 返回后以服务端值为准 |
| `--name <hostname>` | `SNM_NAME` | 否 | 系统主机名 | 覆盖上报的 `NodeInfo.Hostname` |
| `--net-if a,b` | `SNM_NET_IF` | 否 | 自动 | 计入流量的网卡白名单 |
| `--net-exclude a,b` | `SNM_NET_EXCLUDE` | 否 | 无 | 追加排除 |
| `--disk-exclude /a,/b` | `SNM_DISK_EXCLUDE` | 否 | 无 | 追加排除挂载点前缀 |
| `--insecure` | `SNM_INSECURE=1` | 否 | 关 | 跳过 TLS 校验 |
| `--log-level trace|debug|info|warn|error` | `SNM_LOG_LEVEL` | 否 | `info` | |
| `--version` | — | — | — | 打印 `snm-agent 1.0.0+abc1234 (linux-x64, .NET 10.0.11, protocol 1)` 退出 0 |
| `--help` | — | — | — | 用法,退出 0 |

CLI 优先于环境变量;systemd unit 通过 `EnvironmentFile=/etc/snm-agent/agent.env` 传参(DEPLOY §4)。退出码:0 正常、2 参数错误、3 缺必填。

### 7.7 AOT 纪律(实现者自检清单)

- 不使用:反射(`GetType().GetProperty` 等)、`dynamic`、`Activator.CreateInstance(Type)`、LINQ 表达式树、`System.Text.Json` 无源生成、`RegexOptions.Compiled`(可用源生成 `[GeneratedRegex]`)、`DllImport`、`MessagePackSerializer`。
- 使用:`LibraryImport`、`Span<T>`/`Utf8Parser`、`ArrayPool`、手写解析。
- 每次改动后运行 `scripts/verify-aot.sh`(`dotnet build -c Release` 0 IL 警告 + ILC 验证命令 0 IL 警告)。
- 体积目标:linux-x64 单文件 ≤ 15 MB(SignalR 客户端 + HTTP 栈约 10–12 MB);常驻内存 ≤ 30 MB。

---

## 8. 安全模型

| 资产/入口 | 威胁 | 对策 |
|---|---|---|
| `/hubs/agent` | 伪造节点、重放、Key 泄露 | 43 字符随机 AgentKey(256 bit),仅 Bearer/查询串传输(TLS 由反代保证);后台轮换即时 Abort;`Seq` 去重;同 Key 双连接后者胜出 + Warning;`Enabled=false` 直接 401 |
| Master→Agent 下行 | RCE/配置注入 | 白名单仅 `ApplyConfig(AgentConfig)` 两个数值字段(PROTOCOL §3.3);Agent 钳制范围;Agent 无监听端口、无 OTA;`HelloResult.Message` 仅记日志 |
| `/api/**` | 未授权访问、暴力破解、token 盗用 | JWT HS256(密钥 64 字节,文件 0600);access 120 min;refresh 30 天、SHA-256 存储、每次轮换、登出/改密撤销;登录 5 次/分钟/IP;单管理员角色 `snm:role=admin` |
| `/hubs/admin` | 同上 | 与 REST 同一 JWT;`?access_token=` 仅对 `/hubs/` 路径生效 |
| `/hubs/public` | 信息泄露、连接耗尽 | `PublicNodeCard` 白名单字段(PROTOCOL §4.3)+ 反射测试;`PublicMaxConnections`;`general.publicDashboardEnabled=false` 时 `OnConnectedAsync` 直接 Abort 且 `/` 返回 404 |
| `/install/{token}` | 脚本含 AgentKey 被枚举 | 43 字符随机 token(与 Key 独立,可一起轮换);10 次/分钟/IP;`Cache-Control: no-store`;404 不区分"不存在/禁用";脚本内提示"含密钥,勿转发" |
| 安装脚本本身 | 供应链 | 从 `agent.releaseBaseUrl` 下载并校验 `SHA256SUMS`;非 root 专用用户;`ProtectSystem=strict` 等 systemd 加固(DEPLOY §4) |
| 反代与真实 IP | `X-Forwarded-For` 伪造 | `ForwardedHeaders` 只信任 loopback + `Snm:KnownProxies`;`ForwardLimit=1` |
| Webhook 出站 | 目标伪造 | 可选 HMAC-SHA256 `X-SNM-Signature`(API §10.2);`insecureSkipTlsVerify` 默认 false |
| Telegram token / Webhook secret | 泄露 | REST 读取脱敏 `***…末4位`;写入以 `***` 开头保持原值;日志不打印 |
| 密码 | 弱哈希 | `PasswordHasher<AdminUser>`(PBKDF2-HMAC-SHA512 100k,V3);改密规则 8–64 含字母数字 |
| SQLite | 并发损坏 | 单进程;WAL;`busy_timeout`;备份用 `VACUUM INTO`(DEPLOY §7) |
| 依赖 | 漏洞 | 版本集中锁定;`dotnet list package --vulnerable` 在 CI 执行(仅报告) |

---

## 9. 模块边界与实施顺序

### 9.1 模块划分与并行计划(4 核 → 同时最多 2 个实现 agent)

```
M0 仓库骨架 ─┐
             ├─▶ M1 Contracts(+tests) ─▶ ┬─▶ M2 Master 数据层+认证+REST ─▶ M3 Master 实时链路 ─▶ M4 Master 后台任务+告警+GeoIP
             │                             └─▶ M5 Agent(与 M2–M4 并行)
             │                                  M6 管理后台前端(按 API/PROTOCOL 文档开工,联调依赖 M3)
             │                                  M7 公开大屏(依赖 M3)
             └────────────────────────────────▶ M8 deploy + CI + scripts + e2e(最后,依赖全部)
```
时序建议:`M0 → M1 → (M2→M3→M4) ∥ M5 → M6 ∥ M7 → M8`。

### 9.2 各模块范围与验收清单

**M0 仓库骨架**(文件:`global.json`、`Directory.Build.props`、`Directory.Packages.props`、`ServerNodeMonitor.sln`、`.editorconfig`、`.gitignore`、`README.md`、`scripts/env.sh`、空项目 csproj 6 个)
- 验收:`source scripts/env.sh && dotnet build ServerNodeMonitor.sln -c Release` 0 错误 0 警告;`.gitignore` 含 `bin/ obj/ node_modules/ data/ src/SNM.Master/wwwroot/ web/public/js/vendor/ *.db *.db-wal *.db-shm .env.local`。

**M1 Contracts**(PROTOCOL §9 全部代码 + vendoring + `tests/SNM.Contracts.Tests`)
- 验收:PROTOCOL §8.1 全部测试通过;`dotnet build src/SNM.Contracts -c Release` 0 IL 警告;`Heartbeat_SizeBudget` 通过(典型 DTO ≤ 48 B);`THIRD-PARTY-NOTICES.md` 存在。

**M2 Master 数据层 + 认证 + REST**(DATA §1–§3、§7.1;API §1–§7、§9;`MenuCatalog`;`InstallScriptService` 的文本生成;不含 Hub 与后台服务,但 `LiveStore` 骨架与 `StartupInitializer` 在此模块)
- 验收:`dotnet ef migrations` 已提交且启动自动迁移;`WebApplicationFactory` 测试:登录 → `user/detail` → `role/permissions/tree` → 节点 CRUD → 409/422 错误信封 → refresh 轮换 → 改密撤销;`GET /api/system/health` 200;`GET /install/{token}/agent.sh` 返回替换后的脚本、错误 token 404、限速 429;`BillingCycleTests` 通过。

**M3 Master 实时链路**(Hubs、`AgentKeyAuthenticationHandler`、`IngestPipeline`、`LiveStore` 完整、`TrafficEngine`、`MinuteAggregator`、`HealthMonitor`、`RealtimeBroadcaster`、`SnapshotFactory`、`MinuteFlusher`、`TrafficStateFlusher`)
- 验收:`AgentHubInteropTests`(静态协议客户端与官方协议客户端各完成 `Hello→Ips→Hb`)、`PublicHubSanitizationTests`、`TrafficDeltaTests`、`MinuteAggregatorTests` 通过;集成测试断言:连 `/hubs/public` 立即收到 `Snapshot`,发心跳后 ≤ 3 s 收到含该节点的 `Tick`;停发心跳 >30 s 后 `Tick` 带 `online=false`;`Metrics1m` 在下一分钟 :03 后有行;`NodeTrafficState` 60 s 内有锚点。

**M4 Master 后台任务 + 告警 + 通知 + GeoIP**(`HourlyRollup`、`DailyRollup`、`RetentionSweeper`、`DbMaintenance`、`AlertEvaluator`、`NotificationDispatcher`、Telegram/Webhook 发送器、`GeoIpDatabase/Refresher`、`IpMerger`、`MetricsQueryService`、`TrafficQueryService`、`DashboardService` 的实时字段)
- 验收:`RollupTests`、`RetentionTests`、`AlertStateMachineTests`、`GeoIpLookupTests`、`IpMergeTests` 通过;集成:节点离线 >30 s → `AlertEvents` 出现 `Offline firing`,测试内 Webhook 接收器收到 `alert.firing` 且 HMAC 校验通过;恢复心跳 → `resolved` + `alert.resolved`;冷却期内再次离线 → 事件 `Suppressed=true` 且无 HTTP 请求;`POST /api/channels/{id}/test` 返回 `ok:true`;`GET /api/nodes/{id}/metrics?range=24h` 返回分钟点。

**M5 Agent**(§7 全部;`tests/SNM.Agent.Tests`:`/proc` 样本文本解析、`NetFilter`、IP 过滤、CPU Δ 计算、CLI 解析)
- 验收:`dotnet build src/SNM.Agent -c Release` 0 IL 警告;ILC 验证命令 0 IL 警告(link 失败属预期);`dotnet run --project src/SNM.Agent -- --server http://127.0.0.1:5080 --key <k>` 连上本机 Master,后台节点显示在线且 CPU/内存/磁盘/网速/IP 合理;`--version`、`--help`、缺参退出码 3、错误代理地址退出码 2;kill Master 后 Agent 持续退避重连并在 Master 恢复后 ≤ 60 s 重新在线;Windows 本机(本 VM 为 win-arm64)`dotnet run` 亦能采集(允许 Load1=N/A)。

**M6 管理后台**(FRONTEND §1–§4)
- 验收:`npm run build` 通过且产物在 `src/SNM.Master/wwwroot/admin/`;登录/登出/access 过期后自动续期;七个页面均可打开;节点列表实时更新(CPU 条 2 s 变化);ECharts 三个区间有数据;安装脚本弹窗可复制;告警页收到实时推送与右上角通知;设置页保存后再次读取一致;`eslint` 0 error。

**M7 公开大屏**(FRONTEND §5)
- 验收:`web/public` 构建复制到 wwwroot 后访问 `/`,无任何 `/api` 请求(DevTools Network 仅 `negotiate` + WebSocket + 静态资源);卡片 2 s 更新;断网重连后自动恢复;`scripts/check-public-fields.sh` 通过;375 px 宽度单列可用。

**M8 部署 + CI + 脚本 + e2e**(DEPLOY 全部;`scripts/dev.sh`、`build-web.sh`、`verify-aot.sh`、`e2e.sh`、`check-public-fields.sh`;`deploy/.github/workflows/agent-aot.yml`、`master.yml`)
- 验收:`scripts/e2e.sh` 在本机完成 BRIEF §0 第 4 条全部步骤并以 0 退出;工作流 YAML 通过 `actionlint`(CI 内);Dockerfile 由 CI `master.yml` 的 `docker build` 步骤验证;`install-agent.sh` 通过 `bash -n` 与 `shellcheck`(CI 内)。

### 9.3 跨模块契约冻结点

- `SNM.Contracts` 的 DTO/Key/常量在 M1 完成后冻结;后续改动需同时改 PROTOCOL.md 并遵守 `ProtocolInfo.Version` 规则(PROTOCOL §7、§10)。
- REST JSON 形状以 API.md 为准;前端不得依赖未文档化字段。
- 数据库列以 DATA.md 为准;新增列必须新迁移。
- 设置组键名(`general.*` 等)以 DATA §2.12 为准,前后端共用。

---

## 10. 测试策略(BRIEF Q10)

| 层 | 项目 | 关键用例 | 运行 |
|---|---|---|---|
| 契约单元 | `tests/SNM.Contracts.Tests` | PROTOCOL §8.1:字节等价、往返、前向兼容、帧等价、未知类型、大小预算 | `dotnet test` |
| Master 单元 | `tests/SNM.Master.Tests`(无 Web 主机) | DATA §10:BillingCycle、TrafficDelta、MinuteAggregator、Rollup(临时 SQLite 文件)、Retention、AlertStateMachine(注入 `FakeTimeProvider`)、GeoIpLookup、IpMerge;`InstallScriptService` 占位符替换;`PasswordService`;`JwtTokenService` 过期 | `dotnet test` |
| Master 集成 | `tests/SNM.Master.Tests/Integration` | `WebApplicationFactory<Program>` + 每个测试类独立临时 SQLite 文件(`SNM_DATA_DIR` 指向临时目录)+ `FakeTimeProvider` 加速后台服务;真实 `HubConnection`(静态协议 & 官方 msgpack);REST 全流程;Webhook 接收器为测试内最小 Kestrel 服务器 | `dotnet test` |
| Agent 单元 | `tests/SNM.Agent.Tests` | `/proc/*` 样本解析(含双路 CPU、无 MemAvailable、ARM cpuinfo)、NetFilter、IP 过滤、CPU Δ、ArgParser | `dotnet test` |
| AOT 静态 | `scripts/verify-aot.sh` | 分析器 0 警告 + ILC 0 IL 警告 | 手动/CI |
| 前端 | `web/admin` | `npm run build`、`eslint`;`web/public` `node --check js/app.js` | CI |
| 端到端 | `scripts/e2e.sh` | 起 Master(临时 DataDir、`SNM_ADMIN_PASSWORD=e2e-pass-123`)→ 登录 → 创建节点 → 起 Agent(JIT)→ 轮询 `GET /api/nodes/{id}` 至 `live.online=true` 且 `ips` 非空 → 等到下一个整分 :05 后检查 `metrics?range=24h` 有点 → 起本地 Webhook 接收器(`tools/webhook-sink`,`dotnet run` 的 20 行最小 Kestrel,把请求体写到文件)并创建渠道 → kill Agent → 等待 ≤ 45 s 出现 `alert.firing` → 重启 Agent → 等待 `alert.resolved` → curl `/` 与 `/admin/` 200 → 清理。全程 ≤ 4 分钟 | 手动/CI(Linux runner) |

时间抽象:Master 全部时间读取经 `TimeProvider`(.NET 8+ 内置)注入,测试用 `FakeTimeProvider`。

---

## 11. 脚本(`scripts/`)

| 脚本 | 作用 |
|---|---|
| `env.sh` | 已存在:导出 `DOTNET_ROOT/PATH` |
| `build-web.sh` | `web/admin`:`npm ci && npm run build`(产物直出 `src/SNM.Master/wwwroot/admin/`);`web/public`:`npm ci && npm run build`(复制 signalr 两个浏览器包到 `js/vendor/`)后 `cp -r index.html css js` 到 `src/SNM.Master/wwwroot/` |
| `dev.sh` | `build-web.sh`(可 `--skip-web`)→ 后台 `dotnet run --project src/SNM.Master`(`SNM_DATA_DIR=./data`,`SNM_ADMIN_PASSWORD` 默认 `admin123`)→ 等 `/api/system/health` → 若无 `AGENT_KEY` 环境变量则登录并创建/读取节点 `local-dev` 拿 Key → 前台 `dotnet run --project src/SNM.Agent -- --server http://127.0.0.1:5080 --key $KEY`;Ctrl+C 同时结束两者 |
| `verify-aot.sh` | §7.7 两条命令,grep `IL[23][0-9]{3}` 计数为 0 才返回 0 |
| `e2e.sh` | §10 端到端 |
| `check-public-fields.sh` | 扫描 `web/public/js/*.js` 中访问的属性名必须 ⊆ PROTOCOL §4.3 Public 白名单(`id name cc online order hist t cpu mem rx tx tickMs title nodes u p`) |

---

## 12. 容量与性能目标

| 指标 | 目标 | 依据 |
|---|---|---|
| 节点数 | 500 节点 @ 2 s 心跳 = 250 msg/s | 心跳路径无锁竞争(每节点独立锁)、无 DB 写;SignalR 单核可处理数千 msg/s |
| Master 内存 | 500 节点 < 300 MB | 每节点环形缓冲 90 点 × 40 B ≈ 4 KB;GeoIP < 20 MB |
| DB 体积 | 100 节点 30 天 ≈ 20 MB | DATA §9 |
| 广播 | 100 节点 Tick ≈ 3 KB(public)/7 KB(admin) 每 2 s | PROTOCOL §4.4 |
| REST 延迟 | 列表接口 < 100 ms(100 节点) | 列表来自 LiveStore + 一次 Nodes 查询 |
| Agent | CPU < 0.5%(2 s 间隔),RSS < 30 MB,二进制 ≤ 15 MB | §7.7 |
