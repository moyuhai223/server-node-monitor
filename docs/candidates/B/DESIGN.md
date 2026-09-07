# DESIGN.md — 总体设计(候选设计 B:数据与运维优先)

> 六份文档的关系:本文定架构、组件、调度、配置、安全与实现顺序;PROTOCOL.md 定 Hub/DTO 契约(含可直接落地的 C# 代码);API.md 定 REST;DATA.md 定持久化与计算引擎;DEPLOY.md 定部署/CI/备份;FRONTEND.md 定两个前端。任何冲突以更具体的文档为准(PROTOCOL/API/DATA > DESIGN)。

## 1. 架构总览

```text
                    ┌───────────────────────── Master (ASP.NET Core, JIT, 1 进程) ─────────────────────────┐
  Agent (AOT)       │  /hubs/agent  AgentHub ──► HeartbeatProcessor ──► NodeStateStore(内存: 最新样本/环形缓冲/分钟桶/流量累加器) │
  snm-agent ══MsgPack/WS══►│                      │                              │             ▲                   │
  (每2s hb, 5min ip,│                      └─ TrafficAccumulator(Delta)      │ 每60s flush │                   │
   60s disk)        │                                                        ▼             │                   │
                    │  /hubs/public  PublicHub ◄── RealtimeBroadcaster(2s tick) ◄── NodeStateStore              │
  浏览器(大屏) ◄═══│  /hubs/admin   AdminHub  ◄──────────┘   AlertEvaluationService(10s) ─► NotificationDispatcher ─► Telegram/Webhook
  浏览器(后台) ◄═══│  /api/*        REST(JWT)  ──► Services ──► SnmDbContext(EF Core) ──► SQLite WAL  {DataDir}/snm.db
                    │  /admin/*      SPA 静态     /  大屏静态     /install.sh /install.ps1     /healthz            │
                    │  后台任务: MetricsFlush · Rollup · Retention · Maintenance · GeoIpUpdate · ExpiryCheck        │
                    └──────────────────────────────────────────────────────────────────────────────────────────┘
  Nginx/1Panel(TLS,反代,X-Forwarded-For) ──► 127.0.0.1:5080
```

- **三个 Hub 均用 MessagePack**;探针用 Contracts 内自实现的 AOT 友好 `SnmMessagePackHubProtocol`(线格式与官方一致),Master 与浏览器用官方实现。
- **探针只发不收**(除唯一的下行配置消息 `cfg`),不监听端口,无命令执行,无自更新。
- **心跳不落库**:内存聚合为 1 分钟桶,每分钟一个事务写入;降采样 1m→1h→1d;流量按 Delta 累计到账期/本地日。

## 2. BRIEF §3 十个问题的定案(摘要,细节见对应文档)

| # | 问题 | 定案 | 理由 |
|---|---|---|---|
| 1 | MessagePack + AOT 路径 | **路径 1**:在 `SNM.Contracts` vendoring MIT 的 `MessagePackHubProtocolWorker`/`BinaryMessageParser`/`BinaryMessageFormatter`/`MemoryBufferWriter`(源自 dotnet/aspnetcore release/10.0,已在 `spikes/aot-messagepack/upstream-reference/` 留档),实现 `SnmMessagePackHubProtocol : IHubProtocol`(Name=`messagepack`,Version=2),`Serialize/DeserializeObject` 用 **静态 type-switch + 手写 `IMessagePackFormatter<T>`**(`SnmFormatterRegistry`),只依赖 `MessagePackWriter/Reader`,不引用 `MessagePackSerializer`/任何 Resolver。Agent 侧 `builder.Services.RemoveAll<IHubProtocol>()` 后注册自实现协议,使 JSON 协议与 MessagePack 动态解析器���部被裁剪。**不需要任何 `UnconditionalSuppressMessage`**。Master 用官方 `AddMessagePackProtocol()`(JIT,允许 IL2026)。字节级等价用 `SNM.Contracts.Tests` 证明:对每个 DTO,`MessagePackSerializer.Serialize(dto, StandardResolver)` 与手�� formatter 输出逐字节相等,且互相反序列化往返相等;再用 `WebApplicationFactory` 集成测试证明 Agent 协议 ⇄ Master 官方协议互通 | BRIEF 实测:只要不触达动态入口,ILC 零警告;官方包的 `AddMessagePackProtocol` 带 `RequiresUnreferencedCode`,非泛型 `Serialize(Type)` 走 `Expression.Compile`,不可用于 AOT;路径 2 无法在本机验证运行时行为 |
| 2 | 心跳/注册 DTO | `HeartbeatDto` 9 字段、全部整数(见 PROTOCOL §3):**实测** 典型值 body 37 B,完整 SignalR 帧 47 B(目标名 `hb`);最坏 48/58 B;注册 `RegisterDto` 13 字段 ≈ 201 B(仅注册/重连时一次);IP 列表 `IpReportDto` 每 300s 单独上报;磁盘明细 `DiskReportDto` 每 60s。DTO 不含 DateTime,服务端打时间戳;`ElapsedMs`(ushort)承载探针单调时钟间隔 | 50 B 目标必须把 Hub 目标名缩到 2~4 字符,并把慢变量(磁盘明细、IP)移出心跳;心跳只带聚合磁盘用量 |
| 3 | 数据模型与降采样 | 见 DATA.md §1–§4:三张同构表 `Metrics1m/1h/1d`(均值+峰值+Delta+Samples+SpanSec),复合主键,`long` Unix 秒;`BackgroundService + PeriodicTimer` 轮询“到点”条件并以 Settings 记录 `lastRun` 实现幂等与补跑;整段重算 + UPSERT | SQLite 单写者;时间列用整数便于 raw SQL;补偿式调度对停机友好 |
| 4 | 流量 Delta | Agent 过滤 + 上报聚合累计值;服务端 E1–E6 六条判定(首包基线、单调增量、重启→计入 new、32 位回绕、无法解释回退→重建基线、上限防御);账期 `BillingPeriod.Compute`(重置日 1–31,月底钳制,节点 tz 或全局 tz,DST 处理);跨边界按时间比例切分;`TrafficMonthly/TrafficDaily` UPSERT 与基线同事务 | 见 DATA.md §5 |
| 5 | 告警引擎 | 6 条规则插件(`offline/cpu/mem/disk/traffic/expiry`),4 态状态机(Normal/Pending/Firing/Recovering),连续命中 N / 连续恢复 M,冷却 30–60 min(钳制),恢复通知仅对已通知事件,去重键 `{NodeId}:{RuleKey}`;`AlertStates` 持久化跨重启;Telegram(HTML)+ Webhook(JSON + HMAC)+ 测试发送 | 见 DATA.md §6 |
| 6 | REST 与模板兼容 | **服务端兼容模板期望的登录/用户/权限接口 + 少量前端改造**:实现 `POST /api/auth/login`、`GET /api/user/detail`、`GET /api/role/permissions/tree`、`GET /api/permission/menu/validate`、`POST /api/auth/logout`、`POST /api/auth/password`、新增 `POST /api/auth/refresh`;响应包 `{code:0,message,data}`;分页 `pageNo/pageSize → {pageData,total}`(与 `MeCrud` 契约一致);菜单树由服务端静态给出。前端改造清单见 FRONTEND.md §2 | 模板的 axios 拦截器、路由守卫、`MeCrud` 都依赖这套形状,兼容成本最低;删除模板的 pms/demo 页面 |
| 7 | 实时推送 | 连接即推 `snapshot`(含每节点最近 90 点波浪);每 2s 一次批量 `tick`(仅脏节点);`status` 上下线事件;节点集合/公开字段变化时重推 `snapshot`;`alert` 仅 admin。浏览器 DTO 用 **字符串键(camelCase)** 的 `[MessagePackObject]`,探针 DTO 用整数键 | 浏览器侧可读性优先,带宽在 10 KB/s 内;探针侧极致压缩 |
| 8 | Agent 采集与网络 | Linux `/proc` + `statvfs`(`LibraryImport`);Windows `GetSystemTimes/GlobalMemoryStatusEx/GetIfTable2/GetTickCount64/GetLogicalProcessorInformationEx` + 注册表 CPU 名 + `DriveInfo`;IP 过滤保留私网、剔除 127/8、::1、169.254/16、fe80::/10、未指定;`--proxy` 设 `HttpConnectionOptions.Proxy=new WebProxy(uri)`(支持 `http://`、`socks5://`、`socks4://`,含用户名密码),10.0 客户端在 HTTP/1.1 下把它直接赋给 `ClientWebSocketOptions.Proxy`,WebSocket 经 HTTP CONNECT/SOCKS 隧道;传输限定 `WebSockets | LongPolling`(SSE 不支持二进制);代理禁止 WebSocket 时自动回退 LongPolling;重连指数退避 1→60s 永不放弃;CLI/环境变量表见 §10 | 已核对 10.0 `WebSocketsTransport` 源码 |
| 9 | 安装脚本 | 公开静态端点 `GET /install.sh`、`GET /install.ps1`(无密钥,模板替换 release 基地址与 Master URL);后台生成一行命令 `curl -fsSL https://m/install.sh \| sudo bash -s -- --server https://m --key KEY --name NAME`;脚本:`uname -m` 选 linux-x64/arm64,下载 + `sha256sum -c`,`/opt/snm-agent`,专用用户 `snm-agent`,`EnvironmentFile=/etc/snm-agent/agent.env`(0600),systemd `Restart=always` + 沙箱指令,幂等,`uninstall` 子命令;Windows 用计划任务(LocalService,开机启动,失败重启) | 密钥只出现在管理员复制的命令行,脚本本体可公开缓存 |
| 10 | 测试 | 单元:契约字节等价、Delta、账期、聚合/rollup、防抖、GeoIP;集成:`WebApplicationFactory` + 真实 SignalR 客户端(官方 MessagePack 与自实现协议各一遍)+ 临时 SQLite;前端 `npm run build`;`scripts/e2e.sh` 起 Master+Agent(JIT)跑断线告警链路 | 见 §12 |

## 3. 模块边界与目录

| 模块 | 目录 | 职责 | 依赖 |
|---|---|---|---|
| Contracts | `src/SNM.Contracts` | DTO、Hub 路径/方法名常量、单位常量、手写 formatter、`SnmMessagePackHubProtocol`、vendored worker;`IsAotCompatible=true`;**零 IL 警告** | MessagePack 2.5.302、Microsoft.AspNetCore.SignalR.Common(随 Client 包传递;显式引用 `Microsoft.AspNetCore.SignalR.Client.Core` 以获得 `IHubProtocol`) |
| Agent | `src/SNM.Agent` | 采集、连接、CLI;`PublishAot` | Contracts、SignalR.Client 10.0.11 |
| Master | `src/SNM.Master` | Hub、REST、引擎、后台任务、静态托管 | Contracts、EF Core Sqlite、JwtBearer、SignalR.Protocols.MessagePack |
| Admin UI | `web/admin` | vue-naive-admin 改造 | 无 C# 依赖;构建产物 → `src/SNM.Master/wwwroot/admin/` |
| Public UI | `web/public` | 纯静态大屏 | Master csproj 以 `Content` 链接方式打包(见 FRONTEND.md §8) |
| Tests | `tests/*` | 见 §12 | |
| Deploy | `deploy/` | systemd、install 脚本、Dockerfile、nginx 示例、workflows | |
| Scripts | `scripts/` | `env.sh` `dev.sh` `build-web.sh` `e2e.sh` `sync-workflows.sh` | |

Master 内部命名空间/目录:

```text
SNM.Master/
  Program.cs                         // 组合根
  Options/SnmOptions.cs              // 强类型配置 (Snm:*)
  Data/{SnmDbContext.cs, Entities/, Configurations/, Migrations/}
  Realtime/{NodeStateStore.cs, NodeRuntimeState.cs, RingBuffer.cs, HeartbeatProcessor.cs, RealtimeBroadcaster.cs, SnapshotBuilder.cs}
  Hubs/{AgentHub.cs, PublicHub.cs, AdminHub.cs, HubClientTracker.cs}
  Auth/{AgentKeyAuthenticationHandler.cs, JwtService.cs, PasswordHasher.cs, RefreshTokenService.cs}
  Traffic/{TrafficAccumulator.cs, BillingPeriod.cs, TrafficService.cs}
  Metrics/{MinuteBucket.cs, MetricsFlushService.cs, RollupService.cs, RetentionService.cs, MaintenanceService.cs, MetricsQueryService.cs}
  Alerts/{IAlertRule.cs, Rules/*.cs, AlertEngine.cs, AlertEvaluationService.cs, ExpiryCheckService.cs, NotificationDispatcher.cs, Channels/{TelegramSender.cs, WebhookSender.cs}}
  GeoIp/{GeoIpDatabase.cs, GeoIpUpdateService.cs}
  Settings/{SettingsService.cs, SettingKeys.cs}
  Api/{ApiResponse.cs, ApiException.cs, ExceptionMiddleware.cs, Controllers/*.cs 或 Endpoints/*.cs (Minimal API 分组)}
  Install/{InstallScriptService.cs}
  Startup/StartupInitializer.cs
  wwwroot/ (admin/ 由 vite 输出; 大屏由 csproj Content 链接自 web/public)
```

REST 用 **Minimal API + `MapGroup`**(每个资源一个 `*Endpoints.cs`),Master 非 AOT 可用反射 JSON;统一 `JsonSerializerOptions`:camelCase、忽略 null(`WhenWritingNull`)、枚举字符串。

## 4. 进程内组件与数据流

1. **探针连接**:`negotiate`(POST,携带 `Authorization: Bearer <AgentKey>`)→ `AgentKeyAuthenticationHandler` 查 `NodeStateStore.KeyIndex`(内存字典 AgentKey→NodeId,节点增删/轮换时刷新)→ 失败 401;成功则 WebSocket 升级 → `AgentHub.OnConnectedAsync`:记录 `ConnectionId`,若该节点已有其他连接 → 对旧连接 `Abort()`;捕获远端 IP(`HttpContext.Connection.RemoteIpAddress`,经 `UseForwardedHeaders`)→ `PublicIp` + GeoIP → 广播 `status`(仅在从离线变为在线时)。
2. **注册** `reg(RegisterDto)` → 更新 `Nodes` 清单列(同步 EF 写,一次性)→ 返回 `AgentConfigDto`。
3. **心跳** `hb(HeartbeatDto)` → `HeartbeatProcessor.Process`(纯内存:钳制、Delta、速率、波浪、分钟桶、Dirty)。任何 DB 访问都不在此路径。
4. **IP/磁盘报告** → 合并写 `Nodes.IpsJson` / 替换 `NodeDisks`(低频 EF 写)。
5. **广播** `RealtimeBroadcaster` 每 2s:收集 Dirty 节点 → `PublicTickDto`/`AdminTickDto` → `Clients.All.SendAsync("tick")`;无浏览器连接时跳过序列化(`HubClientTracker` 计数)。
6. **Flush** 每分钟:见 DATA.md §3。
7. **告警** 每 10s:`AlertEngine.EvaluateAll()` 读 `NodeStateStore` + `NodeDisks` 缓存 + 当前账期用量 → 状态机 → 事件写 DB → `NotificationDispatcher` 队列 → HTTP。
8. **REST 查询**:直接 EF 读;节点列表与详情把 `NodeStateStore` 的运行态与 DB 配置合并返回。

线程模型:Hub 方法在 SignalR 调度线程执行,`NodeRuntimeState` 内部用 `lock(state)` 保护(每节点独立锁,粒度小);后台服务通过 `Interlocked/lock` 读取快照;所有 SQLite 写经 `SqliteWriteGate`。

## 5. 后台服务与调度表

| 服务(`BackgroundService`) | 周期 | 时刻/条件 | 工作 | 幂等性 |
|---|---|---|---|---|
| `StartupInitializer`(IHostedService,最先) | 一次 | 启动 | 迁移、PRAGMA、种子、缓存加载、当前账期确保、补偿 rollup | 重复启动安全 |
| `MetricsFlushService` | 60s | HH:MM:02 | 关闭分钟桶 → 一个事务写 Metrics1m + Traffic* + NodeRuntime | UPSERT 加权合并 |
| `RealtimeBroadcaster` | 2s(`Snm:Realtime:TickSeconds`) | 连续 | tick 广播;每 30s 若有节点 LastSeen 超时 → 由 AlertEvaluation 负责状态,广播器仅发数据 | 无状态 |
| `AlertEvaluationService` | 10s | 连续 | 5 条即时规则评估 + 上下线状态切换与 `status` 广播 | 状态机幂等 |
| `ExpiryCheckService` | 30s 轮询 | 每日 `alert.expiryCheckHour`(本地)一次 + 节点保存事件 | expiry 规则 | `job.expiry.lastRun` |
| `RollupService` | 30s 轮询 | HH:01:30(1h),00:10 UTC(1d) | 重算最近 2 个桶 | 整段重算 |
| `RetentionService` | 30s 轮询 | HH:07:00 | 分批删除 | 天然幂等 |
| `MaintenanceService` | 30s 轮询 | 04:30 UTC | checkpoint、optimize、`VACUUM INTO` 备份(保留 7 份)、可选 VACUUM | |
| `GeoIpUpdateService` | 1h 检查 | 启动 +5s;文件缺失或 ≥7d | 下载、加载、重算国家码 | 原子替换 |
| `NotificationDispatcher` | 事件驱动 | `Channel<NotificationJob>` | 发送、重试、日志 | 每次尝试记日志 |
| `AgentConfigPusher`(事件驱动,非独立服务) | — | `SettingsChanged`(agent.* 键) | 向所有在线探针 `cfg` | |

所有轮询型任务共用一个 `JobScheduler` 帮助类:`ShouldRun(jobKey, dueUnix)` = `lastRun < dueUnix && now >= dueUnix`,执行后写 `job.<key>.lastRun = dueUnix`。

## 6. 启动顺序

```text
1 Host 构建:配置(§7)→ 日志 → Options 校验(URL、DataDir 可写、Jwt Secret 长度≥32)
2 StartupInitializer.StartAsync(顺序,失败则进程退出,退出码 2)
3 其余 BackgroundService 启动(顺序无关,均等待 StartupInitializer 完成的 TaskCompletionSource `AppReady`)
4 Kestrel 监听(Urls,默认 http://127.0.0.1:5080);/healthz 在 AppReady 之前返回 503
5 优雅关闭(SIGTERM,`HostOptions.ShutdownTimeout=15s`):停止接收 Hub 消息 → MetricsFlush 最终 flush → AlertStates 落库 → Dispatcher 排空(最多 10s)→ 退出
```

## 7. 配置来源

优先级(高→低):命令行 `--Snm:Key=value` → 环境变量 → `appsettings.{Environment}.json` → `appsettings.json` → 代码默认。环境变量约定:`AddEnvironmentVariables()`(无前缀,兼容 `ASPNETCORE_*`)+ 少量**别名**在 Program 中手工映射(表中“别名”列)。嵌套键用 `__`。

| appsettings 键 | 环境变量(别名) | 默认 | 说明 |
|---|---|---|---|
| `Urls` | `ASPNETCORE_URLS` | `http://127.0.0.1:5080` | Kestrel 监听;Docker 内为 `http://0.0.0.0:5080` |
| `Snm:DataDir` | `SNM_DATA_DIR` | `./data`(相对工作目录) | DB、GeoIP、备份、jwt.key |
| `Snm:Database:Path` | `SNM_DB_PATH` | `{DataDir}/snm.db` | |
| `Snm:Admin:Username` | `SNM_ADMIN_USER` | `admin` | 仅首次种子 |
| `Snm:Admin:Password` | `SNM_ADMIN_PASSWORD` | 空 → 随机生成并打印 | 仅首次种子 |
| `Snm:Jwt:Secret` | `SNM_JWT_SECRET` | 空 → `{DataDir}/jwt.key` | ≥32 字符 |
| `Snm:Jwt:AccessTokenMinutes` | `SNM__JWT__ACCESSTOKENMINUTES` | 120 | |
| `Snm:Jwt:RefreshTokenDays` | | 14 | |
| `Snm:ForwardedHeaders:KnownProxies` | `SNM_KNOWN_PROXIES`(逗号) | 空(仅 loopback) | 反代 IP;`Snm:ForwardedHeaders:KnownNetworks` 支持 CIDR 列表 |
| `Snm:ForwardedHeaders:ForwardLimit` | | 1 | |
| `Snm:PublicUrl` | `SNM_PUBLIC_URL` | 空 | 与设置 `general.masterPublicUrl` 二选一,设置优先 |
| `Snm:GeoIp:Enabled` | | true | |
| `Snm:GeoIp:RefreshDays` | | 7 | |
| `Snm:GeoIp:Ipv4Url` / `Ipv6Url` | | jsDelivr 两个 CSV URL | |
| `Snm:Realtime:TickSeconds` | | 2 | 1..10 |
| `Snm:Realtime:WavePoints` | | 90 | 30..300 |
| `Snm:Hub:ClientTimeoutSeconds` | | 30 | 所有 Hub |
| `Snm:Hub:KeepAliveSeconds` | | 10 | |
| `Snm:Hub:AgentMaxMessageBytes` | | 65536 | 探针 Hub `MaximumReceiveMessageSize` |
| `Snm:Hub:PublicMaxConnections` | | 500 | 超出拒绝(`OnConnectedAsync` 抛 HubException) |
| `Snm:Retention:Metrics1mHours` / `Metrics1hDays` / `Metrics1dDays` | | 25 / 8 / 32 | |
| `Snm:Retention:TrafficDailyDays` | | 400 | |
| `Snm:Retention:NotificationLogDays` | | 30 | |
| `Snm:Notify:HttpProxy` | `SNM_NOTIFY_PROXY` | 空 | Telegram/Webhook 出站代理 `http://` 或 `socks5://` |
| `Snm:Notify:TimeoutSeconds` | | 10 | |
| `Snm:Traffic:MaxPlausibleGbps` | | 40 | 可被设置 `traffic.maxPlausibleGbps` 覆盖 |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` | Information | |
| `Logging:LogLevel:Microsoft.AspNetCore` | | Warning | |
| `Logging:Console:FormatterName` | | simple(`SNM_LOG_JSON=true` 时 json) | |

启动时把生效配置(敏感项打码)以 Information 打印一行 JSON。

## 8. 日志

- 仅用 `Microsoft.Extensions.Logging` 控制台(stdout → journald/Docker),不引入第三方日志包。
- 类别:`SNM.Hub.Agent`、`SNM.Hub.Browser`、`SNM.Metrics`、`SNM.Traffic`、`SNM.Alerts`、`SNM.Notify`、`SNM.GeoIp`、`SNM.Auth`、`SNM.Startup`。
- 结构化字段:`NodeId`、`ConnectionId`、`RuleKey`、`ChannelId`、`Elapsed`;使用 `LoggerMessage` 源生成器定义(高频路径零分配)。
- 级别约定:每个心跳 **Trace**;连接/断开 Information;Delta 异常判定 Warning;通知失败 Warning;DB 失败 Error。
- 内置计数器(`System.Diagnostics.Metrics` `Meter("SNM.Master")`):`snm_heartbeats_total`、`snm_agents_online`、`snm_browser_connections`、`snm_flush_duration_ms`、`snm_metrics_dropped_buckets`、`snm_notifications_total{result}`。通过 `dotnet-counters` 观察,不额外暴露 HTTP 端点。
- Agent:stdout,`--log-level`(trace|debug|info|warn|error,默认 info),每次心跳 Trace,连接状态变化 Info,采集异常 Warn(不中断循环)。

## 9. 安全模型

| 面 | 措施 |
|---|---|
| 探针 → Master | 每节点唯一 `AgentKey`(32 B 随机 base64url),`Authorization: Bearer` 头(或 `?access_token=`),自定义认证方案 `AgentKey`;错误密钥 401 且不泄露是否存在;单节点单连接(后连踢先连);消息大小上限 64 KB;DTO 数值钳制;禁用节点拒绝连接(403) |
| Master → 探针 | 仅 `cfg(AgentConfigDto)` 一条下行(字段全为整数范围受限),PROTOCOL.md 逐条列明;探针忽略未知方法 |
| 管理员 | PBKDF2-SHA256 600k 迭代;JWT HS256 2h + 刷新令牌 14d 轮换 + 重放检测;`TokenVersion` 使改密即全局登出;登录限流 10/min/IP;所有 `/api/*`(除 auth/login、auth/refresh、`/healthz`、`/install.*`)需 JWT;Hub `/hubs/admin` 需 JWT(query `access_token`) |
| 大屏 | 匿名;`PublicHub` 无任何客户端→服务端方法除 `GetSnapshot`;DTO 白名单字段(FRONTEND.md §7 检查清单);连接数上限;`public.enabled=false` 全部关闭 |
| 传输 | TLS 由反代提供;`UseForwardedHeaders` 只信任 loopback + 配置代理;Master 不直接暴露公网(文档明示) |
| 存储 | `{DataDir}` 0700、`snm.db` 0600;AgentKey/渠道密钥明文存 DB(理由:需要复现安装命令与调用第三方 API),API 返回渠道密钥脱敏;备份文件同权限 |
| HTTP 头 | `X-Content-Type-Options: nosniff`、`Referrer-Policy: same-origin`、`X-Frame-Options: DENY`(`/admin`),大屏 CSP `default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self' ws: wss:` |
| 供应链 | Central Package Management 锁版本;Agent 仅 3 个包(SignalR.Client、MessagePack、Contracts);CI 生成 SHA256SUMS |
| 红线 | Agent 不监听端口、无 OTA、无命令执行、不读取密钥以外的任何配置文件;Master 不向探针下发任何字符串型/路径型字段 |

## 10. Agent 设计

### 10.1 运行模型

单个 `Program.Main`(无 Generic Host,减小体积):解析参数 → 构建 `HubConnection` → 外层连接循环 → 采集循环(`PeriodicTimer(interval)`)→ 信号处理(SIGTERM/Ctrl+C → 取消令牌 → 关闭连接 → 退出码 0)。目标体积:linux-x64 ≤ 12 MB(`StripSymbols=true`,`InvariantGlobalization=true`,`UseSystemResourceKeys=true`,`OptimizationPreference=Size` 可选)。

### 10.2 CLI 与环境变量

| 参数 | 环境变量 | 必填 | 默认 | 说明 |
|---|---|---|---|---|
| `--server <url>` | `SNM_SERVER` | 是 | | Master 基地址(`https://m.example.com`),自动拼接 `/hubs/agent` |
| `--key <key>` | `SNM_KEY` | 是 | | AgentKey |
| `--name <hostname>` | `SNM_NAME` | 否 | 系统主机名 | 覆盖上报的 Hostname(不影响 PublicName) |
| `--proxy <url>` | `SNM_PROXY` | 否 | 无 | `http://[user:pass@]host:port`、`socks5://...`、`socks4://...` |
| `--interval <sec>` | `SNM_INTERVAL` | 否 | 2 | 1..60;服务端 `cfg` 可覆盖(服务端优先) |
| `--nic-include <globs>` | `SNM_NIC_INCLUDE` | 否 | | 逗号分隔,强制计入 |
| `--nic-exclude <globs>` | `SNM_NIC_EXCLUDE` | 否 | | 强制排除 |
| `--mount-exclude <globs>` | `SNM_MOUNT_EXCLUDE` | 否 | 见 §10.3 | 额外排除挂载点 |
| `--insecure` | `SNM_INSECURE=1` | 否 | false | 跳过 TLS 证书校验(自签名) |
| `--log-level <lvl>` | `SNM_LOG_LEVEL` | 否 | info | |
| `--dry-run` | | 否 | | 采集一次并以 JSON 打印(`Utf8JsonWriter` 手写,无反射),不连接,退出 |
| `--version` / `--help` | | | | |

参数优先级:CLI > 环境变量;缺 `--server/--key` → 打印用法,退出码 64。

### 10.3 采集(每 tick)

| 指标 | Linux | Windows |
|---|---|---|
| CPU ‰ | `/proc/stat` 首行 `cpu` 累计 jiffies,与上一 tick 差值:`busy=(total-idle-iowait)`,‰=`busy*1000/total`;首个 tick 返回 0 | `GetSystemTimes(idle,kernel,user)`,busy=`(kernel+user)-idle` |
| 内存/交换 MB | `/proc/meminfo`:`MemTotal`、`MemAvailable`(used = total - available)、`SwapTotal-SwapFree` | `GlobalMemoryStatusEx`:`ullTotalPhys-ullAvailPhys`;交换 = `ullTotalPageFile-ullAvailPageFile - 物理部分`(取 ≥0) |
| 磁盘 | `/proc/mounts` 过滤:fstype ∈ `{ext2,ext3,ext4,xfs,btrfs,zfs,f2fs,jfs,reiserfs,ntfs,vfat,exfat,fuseblk,apfs}`,排除挂载点前缀 `/proc,/sys,/dev,/run,/boot/efi,/snap,/var/lib/docker,/var/lib/containers`,同一设备只计一次;`statvfs`:total=`f_blocks*f_frsize`,used=`(f_blocks-f_bfree)*f_frsize` | `DriveInfo.GetDrives()` 中 `DriveType.Fixed && IsReady` |
| 网络累计字节 | `/proc/net/dev` 各行 rx bytes(第 1 列)/tx bytes(第 9 列),按 DATA.md §5.1 规则过滤后求和 | `GetIfTable2` → `MIB_IF_ROW2.InOctets/OutOctets` 过滤求和;`FreeMibTable` |
| 负载 ×100 | `/proc/loadavg` 第一个数 | 0 |
| Uptime 秒 | `/proc/uptime` 第一个数 | `GetTickCount64()/1000` |
| ElapsedMs | `Stopwatch` 自上一 tick;首次 0 | 同 |
| 注册信息 | `/etc/os-release PRETTY_NAME`、`/proc/sys/kernel/osrelease`、`/proc/cpuinfo`(`model name` 第一处;`physical id` 去重计数 → `Nx ` 前缀,N=1 时不加)、`Environment.ProcessorCount`、`RuntimeInformation.OSArchitecture`(x64/arm64) | `RtlGetVersion`/`Environment.OSVersion` + 注册表 `ProductName`;`HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0\ProcessorNameString`;`GetLogicalProcessorInformationEx(RelationProcessorPackage)` 计数 |
| IP 列表 | `NetworkInterface.GetAllNetworkInterfaces()`:`OperationalStatus.Up`,排除 `Loopback/Tunnel/Unknown` 类型与 §5.1 排除名单;地址排除 127/8、::1、169.254/16、fe80::/10、0.0.0.0、::;保留私网与公网;去重,IPv4 在前,字符串排序 | 同(.NET API) |

所有 `/proc` 读取用 `File.ReadAllBytes` + `Utf8Parser` 手写解析(无正则、无 LINQ),P/Invoke 用 `[LibraryImport]` + `partial` 源生成(AOT 必需)。任一采集失败 → 记 Warn,该字段沿用上次值。

### 10.4 网络

- `HubConnectionBuilder().WithUrl(server + HubPaths.Agent, o => { o.AccessTokenProvider = () => Task.FromResult(key); o.Transports = WebSockets | LongPolling; o.Proxy = proxy; o.Headers["X-SNM-Agent"] = version; if (insecure) o.HttpMessageHandlerFactory = h => { ((HttpClientHandler)h).ServerCertificateCustomValidationCallback = (_,_,_,_) => true; return h; }; o.WebSocketConfiguration = ws => { if (insecure) ws.RemoteCertificateValidationCallback = (_,_,_,_) => true; }; }).WithServerTimeout(30s).WithKeepAliveInterval(10s).WithAutomaticReconnect(new AgentRetryPolicy())`,并 `builder.Services.RemoveAll<IHubProtocol>(); builder.Services.AddSingleton<IHubProtocol, SnmMessagePackHubProtocol>();`。
- `AgentRetryPolicy.NextRetryDelay`:`min(60, 2^attempt) s ± 20% 抖动`,`null` 永不返回(永不放弃)。初次连接失败(`StartAsync` 抛异常)由外层循环用同一策略重试。
- 事件:`Reconnected` → 重新 `reg`(幂等)→ 应用返回的 `cfg`;`Closed`(自动重连放弃不会发生,但服务端 `Abort` 会触发)→ 外层循环重连。
- 代理行为:HTTP 代理走 `CONNECT` 隧道升级 WebSocket;SOCKS5 由 `SocketsHttpHandler` 原生支持;若代理拒绝升级(HTTP 4xx/协议错误),SignalR 客户端自动降级到 LongPolling(二进制 POST,每 2s 一次请求,可接受);不支持 SSE。
- 消息发送:`hb/ip/disk` 用 `SendAsync`(不等待完成),`reg` 用 `InvokeAsync<AgentConfigDto>`(超时 15s);未连接时跳过本 tick 的发送(不缓存,采集继续以保持 CPU 差分与 Elapsed 正确)。

## 11. 实现顺序与验收

| 序 | 模块 | 产出 | 验收检查 |
|---|---|---|---|
| 1 | 仓库骨架 | sln、Directory.Build/Packages.props、global.json、空项目、`scripts/env.sh` | `dotnet build` 通过 |
| 2 | Contracts | DTO、常量、formatter、协议、vendored worker | `dotnet build src/SNM.Contracts -c Release` 零警告;`SNM.Contracts.Tests`:每个 DTO 字节等价 + 往返;`SnmMessagePackHubProtocol` 对 6 类 HubMessage 与官方协议输出逐字节相等 |
| 3 | Master 数据层 | 实体、迁移、PRAGMA、Settings、Auth | 启动创建 `snm.db`(WAL);登录/刷新/改密集成测试 |
| 4 | Master Hub + 实时 | AgentHub/PublicHub/AdminHub、NodeStateStore、Broadcaster | 官方 MessagePack 客户端模拟探针:注册→心跳→`/hubs/admin` 收到 snapshot+tick |
| 5 | 引擎 | Delta、账期、分钟桶、flush、rollup、retention | DATA.md §11 单元测试全绿;集成:心跳 → 1m 行 |
| 6 | 告警 | 规则、状态机、渠道、Dispatcher | 单元;集成:停止心跳 35s → offline 事件 + 本地 Webhook 收到;恢复通知 |
| 7 | REST | API.md 全部端点 | 每端点至少一个集成测试(200 + 一个错误路径) |
| 8 | Agent | 采集、CLI、连接 | `dotnet run -- --dry-run` 输出合理;ILC 命令零 IL 警告;与 Master 端到端 |
| 9 | Admin UI | FRONTEND.md §2–§5 | `npm run build` 通过;登录、节点在线、图表有数据 |
| 10 | Public UI | FRONTEND.md §6–§7 | 无敏感字段(grep 检查脚本);实时刷新 |
| 11 | Deploy/CI | DEPLOY.md 全部文件 | `actionlint`(可选)+ YAML 解析;`bash -n install-agent.sh`;Dockerfile 构建(CI) |
| 12 | E2E | `scripts/e2e.sh` | BRIEF §0 第 4 条全部 |

并行建议(2 个 agent):A = 2→4→7→9;B = 3→5→6→8→10→11;E2E 收尾。

## 12. 测试策略(问题 10)

- `tests/SNM.Contracts.Tests`:字节等价(手写 formatter vs `MessagePackSerializer` + `StandardResolver`,随机化 200 组值含边界 0/Max/空数组/空字符串);往返;`SnmMessagePackHubProtocol` vs `MessagePackHubProtocol` 对 `Invocation/Completion/Ping/Close/Ack/Sequence/StreamItem` 的 `GetMessageBytes` 相等,`TryParseMessage` 互相解析;未注册类型抛 `NotSupportedException`;不完整帧返回 false 不抛。
- `tests/SNM.Master.Tests`:DATA.md §11 单元 + 集成(`WebApplicationFactory<Program>`,`Snm:DataDir` 指向临时目录,`Snm:Realtime:TickSeconds=1`,时间抽象 `TimeProvider`(注入 `FakeTimeProvider` 驱动调度与冷却)。集成中探针用两种客户端各跑一遍:官方 `AddMessagePackProtocol()` 与 Contracts 的 `SnmMessagePackHubProtocol`。
- `tests/SNM.Agent.Tests`:`/proc` 解析器用样例文本(多 CPU、双路 cpuinfo、含 docker/veth 的 net/dev、含 32 位回绕的两次快照)、网卡/挂载过滤规则、IP 过滤、CLI 解析、`AgentRetryPolicy` 序列。
- 前端:`npm run build`(admin);大屏 `node scripts/check-public.mjs`(FRONTEND.md §7:检查 JS 不引用敏感字段名)。
- `scripts/e2e.sh`:起 Master(临时 DataDir、内置 Webhook 接收器 `scripts/webhook-sink.py` 或 .NET 小程序)→ 创建节点 → 起 Agent(JIT)→ 轮询 API 断言在线、IP、国家码可空、metrics 有点 → kill Agent → 等待 ≤45s 断言 offline 事件与 Webhook 文件 → 重启 Agent → 断言 resolved。

