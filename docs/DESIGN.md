> 本文件是最终采纳的设计(候选方案 A),已按实现落地;与实现的差异见 docs/IMPLEMENTATION_NOTES.md。

# DESIGN — 总体架构、模块边界、进程内组件、配置与实施顺序(候选方案 A)

> 配套文档:PROTOCOL.md(线协议与 Agent 内部)、DATA.md(数据模型/降采样/流量/告警)、API.md(REST)、DEPLOY.md(部署/CI)、FRONTEND.md(前端)。本文回答"系统怎么拼起来、谁先做、怎么验收"。

---

## 0. BRIEF §3 十个问题的定案索引

| # | 问题 | 定案(一句话) | 详见 |
|---|---|---|---|
| 1 | MessagePack + AOT | vendoring `MessagePackHubProtocolWorker` + 静态 formatter 的 `SnmMessagePackHubProtocol`;本机实证分析器 0 警告、ILC 0 IL 警告、帧字节与官方相同 | PROTOCOL §2 |
| 2 | 心跳/注册 DTO | `HeartbeatDto` 9 字段 int-Key,典型 36 B(帧 46 B);`RegisterDto` 16 字段;`StatusReportDto` 每 5 min;`AgentConfigDto` 唯一下行 | PROTOCOL §4 |
| 3 | 数据模型与降采样 | SQLite WAL + EF 迁移;心跳内存分钟聚合 → `Metrics1m/1h/1d` 同构表(均值+峰值+字节和+样本数);`PeriodicTimer` 调度表;按主键 upsert 幂等 | DATA §1–§3 |
| 4 | 流量 Delta | Agent 过滤网卡上报聚合累计值;服务端 `cur≥prev→差值 / 重启→cur / 未重启回退→0`;100 Gbit/s 可信上限;账期 = 重置日 + 月末钳制 + 节点/全局时区;基线持久化 | DATA §4 |
| 5 | 告警引擎 | 6 条规则、Normal→Pending→Firing 状态机、连续次数、30–60 min 冷却仅抑制新触发通知、恢复必通知;Telegram/Webhook(HMAC、模板)动态配置 + 测试 | DATA §5 |
| 6 | REST 与模板兼容 | 服务端兼容模板登录/用户/菜单接口 + 新增 refresh;统一 `{code,message,data}`;`pageNo/pageSize → pageData/total` | API |
| 7 | 实时推送 | 三 Hub 均 MessagePack;连接即 `snapshot`(含 60 点环形缓冲),每 2 s `batch`(仅 dirty 节点);浏览器 DTO string-Key | PROTOCOL §5–§6 |
| 8 | Agent 采集与网络 | `/proc`+`DriveInfo`(Linux)、`LibraryImport` Win32(Windows);`NetworkInformation` 发现 IP;`--proxy` 同时作用于 HTTP 与 WebSocket(IL 证实);WS→LongPolling 回退;永不放弃的指数退避 | PROTOCOL §7 |
| 9 | 安装脚本 | 后台生成 24 h 一次性令牌 URL,`curl -fsSL …/install/<token> \| sudo bash`;脚本幂等、校验 sha256、非 root 用户、systemd、`uninstall` | API §4.10、DEPLOY §4 |
| 10 | 测试策略 | 单元(字节等价/Delta/降采样/防抖/账期/GeoIP/proc 解析)+ 集成(`WebApplicationFactory` + 真实 SignalR 客户端 + 临时 SQLite)+ 前端构建 + `scripts/e2e.sh` | 本文 §9 |

---

## 1. 总体架构

```
┌────────────────────────┐   MessagePack/SignalR (/hubs/agent, AgentKey)   ┌──────────────────────────────────────────┐
│  SNM.Agent (Native AOT) │ ───────────────────────────────────────────────▶ │  SNM.Master (ASP.NET Core 10, JIT)        │
│  Linux x64/arm64, Win   │ ◀── configure ─────────────────────────────────  │  ┌─ AgentHub ─▶ NodeRegistry(内存真相) ─┐ │
│  /proc, Win32, DriveInfo│                                                  │  │      │  Delta / 1m 聚合 / 环形缓冲     │ │
└────────────────────────┘                                                  │  │      ▼                                  │ │
        ▲ 可经 HTTP/SOCKS5 代理                                             │  │  BackgroundServices: Broadcaster(2s)    │ │
        │                                                                   │  │  MinuteFlush(60s) Rollup(1h/1d)         │ │
┌────────────────────────┐   /hubs/public (匿名, 脱敏)                        │  │  Retention Alerts Expiry GeoIP Maint    │ │
│ web/public 静态大屏     │ ◀───────────────────────────────────────────────  │  │      │                                  │ │
└────────────────────────┘                                                  │  │      ▼                                  │ │
┌────────────────────────┐   /hubs/admin (JWT) + /api/* (JWT REST)           │  │  EF Core ─▶ SQLite (WAL) snm.db         │ │
│ web/admin (vue-naive)   │ ◀──────────────────────────────────────────────▶ │  └─ Notifier ─▶ Telegram / Webhook       │ │
└────────────────────────┘                                                  └──────────────────────────────────────────┘
                 Nginx / 1Panel 反代 (TLS, X-Forwarded-*) ─▶ 127.0.0.1:5080
```

进程:仅两个可执行体——`snm-agent`(每台被监控机 1 个,无监听端口)与 `SNM.Master`(1 个,监听 127.0.0.1:5080)。浏览器侧两套静态资源由 Master 托管。

---

## 2. 仓库与模块边界

| 目录 | 产物 | 允许依赖 | 禁止 |
|---|---|---|---|
| `src/SNM.Contracts` | 类库(`IsAotCompatible`) | `MessagePack`、`Microsoft.AspNetCore.SignalR.Common` | 任何 Master/Agent 代码;`MessagePackSerializer`/resolver 引用(PROTOCOL §2.3) |
| `src/SNM.Agent` | Native AOT 控制台 `snm-agent` | `SNM.Contracts`、`Microsoft.AspNetCore.SignalR.Client` | Hosting/Configuration.Binder/Logging.Console/System.Text.Json 反射/任何监听 API |
| `src/SNM.Master` | Web 应用 | `SNM.Contracts`、SignalR(官方 MessagePack 协议包)、EF Core Sqlite、JwtBearer | 引用 Agent |
| `tests/SNM.Contracts.Tests` | xunit | Contracts、`MessagePack`、`Microsoft.AspNetCore.SignalR.Protocols.MessagePack`(官方,用于字节对照) | |
| `tests/SNM.Master.Tests` | xunit + `Microsoft.AspNetCore.Mvc.Testing` | Master、Contracts、`SignalR.Client` | |
| `tests/SNM.Agent.Tests` | xunit | Agent(`InternalsVisibleTo`) | |
| `web/admin` | Vite 构建 → `src/SNM.Master/wwwroot/admin/` | npm | pnpm |
| `web/public` | 静态 + 两个 vendor js → `src/SNM.Master/wwwroot/` | npm(仅取 vendor) | 框架 |
| `deploy/` | systemd、install 模板、Dockerfile、workflows | | |
| `scripts/` | `env.sh`、`dev.sh`、`build-web.sh`、`e2e.sh`、`ilc-check.sh` | bash | jq(本机无) |
| `tools/SNM.WebhookSink` | 极小 ASP.NET 程序,e2e 用 Webhook 接收器(写 JSONL 文件) | | |

`Directory.Build.props`:`net10.0`、`Nullable`、`ImplicitUsings`、`LangVersion latest`、`Deterministic`、`ContinuousIntegrationBuild`(CI 下)、公司/版本元数据(`Version` 默认 `1.0.0`,CI 用 `-p:Version`)。`Directory.Packages.props`:BRIEF 锁定版本 + `Microsoft.AspNetCore.SignalR.Common` 10.0.11 + `Microsoft.EntityFrameworkCore.Design` 10.0.11(PrivateAssets) + `xunit` 2.9.x/`xunit.runner.visualstudio` 3.x/`Microsoft.NET.Test.Sdk` 17.x/`coverlet.collector`(可选)。

### 2.1 Master 源码结构

```
src/SNM.Master/
  Program.cs                     组装(§5 启动顺序)
  appsettings.json  appsettings.Development.json
  Options/SnmOptions.cs          "Snm" 节绑定(§6)
  Config/EnvAliasConfigurationSource.cs   SNM_* 友好环境变量映射
  Data/SnmDbContext.cs  Data/Entities/*.cs  Data/Migrations/*  Data/SqlitePragmaInterceptor.cs  Data/DbWriteLock.cs
  Auth/AgentKeyAuthenticationHandler.cs  Auth/JwtTokenService.cs  Auth/PasswordHasher.cs  Auth/RefreshTokenService.cs  Auth/LoginThrottle.cs
  Hubs/AgentHub.cs  Hubs/PublicHub.cs  Hubs/AdminHub.cs
  Runtime/NodeRegistry.cs  NodeRuntime.cs  RingBuffer.cs  MinuteAccumulator.cs  TrafficRuntime.cs  TrafficEngine.cs  BillingPeriod.cs  IpMerger.cs  LiveSnapshotBuilder.cs
  Services/SettingsService.cs  NodeService.cs  InstallScriptService.cs  GeoIpService.cs  MetricsQueryService.cs  TrafficQueryService.cs  DashboardService.cs
  Alerting/AlertEngine.cs  AlertRules.cs  AlertTexts.cs  NotificationDispatcher.cs  TelegramNotifier.cs  WebhookNotifier.cs
  Background/RealtimeBroadcaster.cs  MinuteFlushService.cs  HourlyRollupService.cs  DailyRollupService.cs  RetentionService.cs  ExpiryCheckService.cs  GeoIpRefreshService.cs  DbMaintenanceService.cs
  Api/ApiResponse.cs  ApiException.cs  ApiExceptionMiddleware.cs  Api/Endpoints/{Auth,User,Menu,Dashboard,Nodes,Install,Alerts,Settings,Channels,System}Endpoints.cs
  Api/Dto/*.cs                   REST 输入/输出模型(与 Contracts 的 Hub DTO 分开)
  wwwroot/                       由 scripts/build-web.sh 生成(不提交)
```

### 2.2 Agent 源码结构

```
src/SNM.Agent/
  Program.cs  Cli/CliOptions.cs  Cli/Usage.cs
  Logging/SimpleConsoleLoggerProvider.cs
  Net/AgentSession.cs  Net/Backoff.cs  Net/ProxyParser.cs  Net/HubConnectionBuilderExtensions.cs
  Collectors/Abstractions.cs      ICpuSampler IMemorySampler ILoadSampler INetSampler IDiskSampler ISystemInfo IIpDiscovery
  Collectors/Linux/ProcStat.cs ProcMemInfo.cs ProcLoadAvg.cs ProcNetDev.cs ProcMounts.cs LinuxSystemInfo.cs
  Collectors/Windows/Kernel32.cs IpHlpApi.cs WindowsCpuSampler.cs WindowsMemorySampler.cs WindowsNetSampler.cs WindowsSystemInfo.cs
  Collectors/Shared/IpDiscovery.cs NicFilter.cs DiskFilter.cs DriveInfoDiskSampler.cs
  Sampling/SampleSet.cs RegisterBuilder.cs HeartbeatBuilder.cs StatusBuilder.cs
```

---

## 3. Master 进程内组件与数据流

### 3.1 `NodeRegistry`(单例,内存真相)

```
NodeRegistry
  ConcurrentDictionary<int, NodeRuntime> Nodes
  ConcurrentDictionary<string, int> KeyIndex            // AgentKey → NodeId(认证 O(1))
  event NodesChanged(int[] ids)                         // 元数据变化 → Hub 推送
NodeRuntime
  NodeEntity Meta (最近一次从 DB 加载/保存的实体副本)
  string? ConnectionId; bool Connected; DateTime? LastSeenAt; byte Status; DateTime? StatusChangedAt
  uint LastSeq; bool InventoryMismatch; string RemoteIp
  LiveSample Live (cpu, memUsedMb, swapUsedMb, diskUsedMb[], rxBps, txBps, load1, ts)
  RingBuffer<LivePoint> History (60)
  MinuteAccumulator Acc; ConcurrentQueue<Metrics1mRow> FlushQueue
  TrafficRuntime Traffic
  volatile bool Dirty
```

启动时由 `Nodes` + `TrafficState` + 当前 `TrafficMonthly/TrafficDaily` + `AlertStates` 装载。REST 修改节点 → `NodeService` 写库后回填 `Meta` 并触发 `NodesChanged`。

### 3.2 数据流

1. **上行**:`AgentHub.Heartbeat` → R1–R6(PROTOCOL §4.6)→ `TrafficEngine.OnHeartbeat` → `Acc` / `History` / `Live` → `Dirty=true`。全程无 DB 访问,单心跳处理 < 50 µs。
2. **广播**:`RealtimeBroadcaster` 每 2 s:扫描 dirty(同时做离线判定 R9)→ `LiveSnapshotBuilder` 生成 `PublicBatchDto`/`AdminBatchDto` → `IHubContext<PublicHub>/<AdminHub>.Clients.All.SendAsync` → 清 dirty。
3. **持久化**:`MinuteFlushService` 每 60 s 持 `DbWriteLock`,一个事务写 `Metrics1m` 完成桶、`TrafficState/Daily/Monthly` 脏行、`Nodes` 状态列、脏 `AlertStates`。
4. **告警**:`AlertEvaluator`(在 `AlertEngine` 内,10 s)读内存状态 → 状态机 → `AlertEvents` 插入(小事务,立即)→ `NotificationDispatcher` 队列 → HTTP 出站 → `NotificationDeliveries`;同时 `AdminHub` 推 `alert`。
5. **查询**:REST 读 `Metrics*`/`Traffic*`/`AlertEvents`(EF,`NoTracking`)并与 `NodeRegistry` 的实时值合并输出。

### 3.3 并发模型

- Hub 方法按连接串行(`MaximumParallelInvocationsPerClient=1`),不同节点并行;`NodeRuntime` 的写只发生在其连接线程 + 广播器读(用 `Volatile`/不可变快照对象 `LiveSample` 替换整体引用,避免锁)。
- `RingBuffer` 由生产者线程写、广播/`GetHistory` 读:读取时复制到数组(容量 60,可忽略成本)。
- DB 写串行(`DbWriteLock`),读并发。

---

## 4. 后台服务与调度

| 服务 | 周期 | 对齐 | 职责 | 失败处理 |
|---|---|---|---|---|
| `RealtimeBroadcaster` | 2 s | — | 离线判定;批量推送 | 捕获并记日志,不中断 |
| `MinuteFlushService` | 60 s | 每分 :05 | 桶/流量/状态落库 | 事务回滚,队列保留,下次重试;连续失败 3 次 → Error 日志 |
| `HourlyRollupService` | 1 h | :02:00 | 1m→1h;启动补算 26 h | 同上 |
| `DailyRollupService` | 24 h | 00:10 UTC | 1h→1d;启动补算 8 d | 同上 |
| `RetentionService` | 1 h | :07:00 | 各表保留删除(分批) | 同上 |
| `AlertEngine` | 10 s | — | 规则评估、状态机、事件 | 单节点异常隔离 |
| `ExpiryCheckService` | 24 h | `alert.expiryCheckHour` 站点时区;启动补跑 | 到期规则 | |
| `NotificationDispatcher` | 事件 | — | 投递 + 重试 | 记录失败;不阻塞评估 |
| `GeoIpRefreshService` | 6 h | 启动 +10 s | 下载/解析/热切换 | 写 `geoip.lastError` |
| `DbMaintenanceService` | 1 h | :00 | 03:00 WAL 截断;周日 03:30 增量真空 | 日志 |

所有服务继承 `SnmBackgroundService`(封装 `PeriodicTimer` 对齐、异常捕获、`ILogger` 分类名 `SNM.Background.<Name>`、停止时 `Flush`)。

---

## 5. 启动顺序(`Program.cs`)

1. 构建配置:`appsettings.json` → `appsettings.{Env}.json` → 环境变量(标准 `Snm__X`)→ `EnvAliasConfigurationSource`(`SNM_*` 友好名,最高优先级)→ 命令行。
2. 绑定 `SnmOptions` 并校验(`Listen` 可解析、`DataDir` 可创建、`KnownProxies` 为合法 IP/CIDR)。创建 `DataDir`、`DataDir/geoip`、`DataDir/backups`。
3. 日志:控制台(systemd/journald 或 Docker 收集),`Logging` 节控制级别;`SNM_LOG_LEVEL` 映射 `Logging:LogLevel:Default`。
4. DI 注册:Options、`SnmDbContext`(工厂 + 作用域)、`DbWriteLock`、`SettingsService`、`NodeRegistry`、认证(`AgentKey` 方案 + `JwtBearer`)、授权策略(`AdminOnly`)、`RateLimiter`、`SignalR().AddMessagePackProtocol()`、`ForwardedHeaders`、各 Service/Notifier/Background。
5. `app` 管道顺序:`ForwardedHeaders` → `ApiExceptionMiddleware` → `RateLimiter` → 静态文件(`/`、`/admin`)→ 认证 → 授权 → 端点(REST、Hub、`/install/{token}`、`/healthz`)→ `MapFallbackToFile("/admin/{*path}", "admin/index.html")`。
6. **迁移与种子(阻塞)**:新库 PRAGMA → `Migrate()` → `quick_check` → `Settings` 缺省键补齐(含 `auth.jwtSecret` 生成、`site.timeZone` 推导)→ `AdminUsers` 为空则按 `Snm:Admin` 创建(密码为空 → 生成 16 位随机密码并以 **Warning** 打印一次)。
7. `NodeRegistry.LoadAsync()`(节点、流量基线、告警状态)。
8. `GeoIpService.TryLoadFromDiskAsync()`(存在则加载,不下载)。
9. 启动 Kestrel;后台服务由 Host 并行启动(它们内部先 `await registry.Ready`)。
10. 日志横幅:版本、监听地址、DataDir、DB 大小、节点数、`site.publicBaseUrl` 是否配置(为空则提示安装脚本需要它)。

停止:`ApplicationStopping` → 广播器停止 → `MinuteFlushService.FlushAllAsync()`(含未完成桶)→ 通知队列等待 ≤ 10 s → 关闭 Hub 连接(SignalR 发送 Close,Agent 进入重连)。

---

## 6. 配置来源与对照表

`appsettings.json`(默认值):

```json
{
  "Snm": {
    "Listen": "http://127.0.0.1:5080",
    "DataDir": "./data",
    "PublicBaseUrl": "",
    "KnownProxies": [ "127.0.0.1", "::1" ],
    "KnownNetworks": [],
    "ForwardLimit": 1,
    "TimeZone": "",
    "Admin": { "User": "admin", "Password": "" },
    "Jwt": { "Secret": "" },
    "GeoIp": { "Enabled": true, "BaseUrl": "https://cdn.jsdelivr.net/npm/@ip-location-db/asn-country", "RefreshDays": 7 },
    "Dev": { "PublicSourceDir": "" }
  },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "Microsoft.EntityFrameworkCore": "Warning" } },
  "AllowedHosts": "*"
}
```

| appsettings 键 | 友好环境变量 | 标准环境变量 | 默认 | 说明 |
|---|---|---|---|---|
| `Snm:Listen` | `SNM_LISTEN` | `Snm__Listen` | `http://127.0.0.1:5080` | Kestrel 监听,多个用 `;` 分隔;Docker 内 `http://0.0.0.0:5080` |
| `Snm:DataDir` | `SNM_DATA_DIR` | `Snm__DataDir` | `./data` | 数据库/GeoIP/备份目录 |
| `Snm:PublicBaseUrl` | `SNM_PUBLIC_BASE_URL` | `Snm__PublicBaseUrl` | 空 | 首次启动写入设置 `site.publicBaseUrl`(设置已有值时不覆盖) |
| `Snm:KnownProxies` | `SNM_KNOWN_PROXIES`(逗号分隔) | `Snm__KnownProxies__0` | `127.0.0.1,::1` | `ForwardedHeaders` 信任的代理 IP |
| `Snm:KnownNetworks` | `SNM_KNOWN_NETWORKS`(逗号 CIDR) | `Snm__KnownNetworks__0` | 空 | Docker 网段等 |
| `Snm:ForwardLimit` | `SNM_FORWARD_LIMIT` | `Snm__ForwardLimit` | 1 | 多层代理时的跳数 |
| `Snm:TimeZone` | `SNM_TIMEZONE` | `Snm__TimeZone` | 空(=服务器本地) | 首次启动写入 `site.timeZone` |
| `Snm:Admin:User` | `SNM_ADMIN_USER` | `Snm__Admin__User` | `admin` | 仅首次建库生效 |
| `Snm:Admin:Password` | `SNM_ADMIN_PASSWORD` | `Snm__Admin__Password` | 空 → 随机生成并打印 | 仅首次建库生效 |
| `Snm:Jwt:Secret` | `SNM_JWT_SECRET` | `Snm__Jwt__Secret` | 空 → 生成并存库 | ≥ 32 字符;设置后优先于库中值 |
| `Snm:GeoIp:Enabled` | `SNM_GEOIP_ENABLED` | `Snm__GeoIp__Enabled` | `true` | |
| `Snm:GeoIp:BaseUrl` | `SNM_GEOIP_BASE_URL` | `Snm__GeoIp__BaseUrl` | jsDelivr 地址 | 内网可换镜像 |
| `Snm:Dev:PublicSourceDir` | — | `Snm__Dev__PublicSourceDir` | 空 | Development 下直接从 `web/public` 提供 `/` 便于调试 |
| `Logging:LogLevel:Default` | `SNM_LOG_LEVEL` | `Logging__LogLevel__Default` | `Information` | `Trace/Debug/Information/Warning/Error` |
| — | `ASPNETCORE_ENVIRONMENT` | | `Production` | |

优先级(高→低):命令行 > 友好环境变量 > 标准环境变量 > `appsettings.{Env}.json` > `appsettings.json` > 代码默认。运行期可变项(阈值、渠道、站点信息)在 DB `Settings`,不在配置文件(DATA.md §6)。

---

## 7. 日志

| 组件 | 载体 | 格式 | 关键类别 |
|---|---|---|---|
| Master | `Microsoft.Extensions.Logging` 控制台(`SimpleConsole`,单行,UTC 时间戳 `timestampFormat "yyyy-MM-dd HH:mm:ss.fff "`);Docker/journald 收集 | `2026-09-07 02:13:22.123 info: SNM.Hubs.AgentHub[0] node 12 registered ...` | `SNM.Hubs.*`、`SNM.Runtime.Traffic`、`SNM.Alerting.*`、`SNM.Background.*`、`SNM.Api`、`SNM.Auth` |
| Agent | 自带 `SimpleConsoleLoggerProvider`(stdout) | `2026-09-07T02:13:22.123Z INFO  ...` | 见 PROTOCOL §7.10 |

Master 必记事件(Information):节点连接/注册/断开(含 connectionId、remoteIp、agentVersion)、状态翻转、账期切换、告警触发/恢复/投递结果、后台任务耗时(> 2 s 记 Warning)、REST 写操作(API.md §8)。Debug:每次 flush 行数、rollup 影响行数。绝不记录 AgentKey、JWT、Bot Token、密码。

---

## 8. 安全模型

| 威胁 | 控制 |
|---|---|
| 伪造探针 | 每节点唯一 48 字符 `AgentKey`(32 B CSPRNG),`X-SNM-Agent-Key` 头;禁用/轮换立即生效(内存字典 + `Abort()` 旧连接) |
| 探针被攻陷后反向控制 Master | Agent 只能调用 3 个 Hub 方法,全部为"上报";DTO 逐字段校验/钳制;字符串截断;数组上限;单连接每分钟反序列化失败 > 10 次断开 |
| Master 被攻陷后控制探针 | 下行仅 `configure`(2 个整数);Agent 无执行/下载/写文件能力;无自更新 |
| 探针暴露面 | 不监听端口;systemd 非 root 用户 + `ProtectSystem=strict` 等加固 |
| 管理后台暴力破解 | 登录限速 10/min/IP;账户 5 次失败锁 15 min;密码 PBKDF2-SHA256 210k |
| 令牌泄露 | access 2 h;refresh 30 d 轮换,重放检测撤销全部;`TokenVersion` 一键失效;JWT 密钥 64 B 随机 |
| 安装脚本泄露 AgentKey | 令牌 URL 24 h 有效、限速、`no-store`;后台可随时轮换密钥;脚本仅包含该节点的 Key |
| 大屏泄露信息 | `PublicSnapshotDto` 字段白名单 + 反射/字节级测试;`PublicVisible` 逐节点开关;大屏页面不调用任何 `/api` |
| 反代伪造 IP | `ForwardedHeaders` 仅信任 `KnownProxies/KnownNetworks`,`ForwardLimit` 默认 1 |
| Webhook 伪造 | HMAC-SHA256 签名头(时间戳 + 正文)供接收方校验 |
| 敏感信息进日志/接口 | Bot Token/secret 掩码返回;`reveal-key` 审计;日志脱敏规则 |
| SQL 注入 | 全部 EF 参数化;rollup 原生 SQL 只含参数 |
| 依赖供应链 | CPM 锁定版本;CI `dotnet list package --vulnerable` 作为非阻塞检查 |

---

## 9. 测试策略(Q10)

### 9.1 单元测试

| 项目 | 测试类 | 覆盖 |
|---|---|---|
| `SNM.Contracts.Tests` | `ByteEquivalenceTests`、`FrameCompatibilityTests`、`ForwardCompatibilityTests`、`ResolverRulesTests` | PROTOCOL §2.4 |
| `SNM.Master.Tests` | `TrafficEngineTests` | 首包基线、正常增量、重启(`BootTime` 变化)回退=cur、未重启回退=0、100 Gbit/s 上限、`ElapsedMs` 可信/不可信速率、`NetIfs` 变化重基线、Master 重启基线恢复 |
| | `BillingPeriodTests` | 重置日 1/15/28/29/30/31 × 各月(含闰年 2 月)、跨年、时区(UTC / Asia/Shanghai / America/Los_Angeles 含夏令时日)、修改重置日中途切期 |
| | `MinuteAccumulatorTests` / `RollupTests` | 桶切换、均值/峰值、字节和、1m→1h→1d SQL 在临时 SQLite 上的数值正确性与幂等(重复执行结果不变) |
| | `RetentionTests` | 边界 25 h/8 d/31 d,分批删除 |
| | `AlertEngineTests` | 连续次数、冷却抑制、静默事件的静默恢复、恢复必通知、账期切换静默关闭、到期规则(≤7/≤1/续费恢复)、`repeatMin`、全局/节点开关 |
| | `GeoIpLookupTests` | 二分查找边界(区间首/尾/间隙)、IPv4 映射 IPv6、私网跳过、坏文件拒载 |
| | `PasswordHasherTests`、`JwtTokenServiceTests`、`RefreshTokenServiceTests`(轮换、重放撤销)、`InstallScriptServiceTests`(占位符全替换、Key 转义、URL 推导)、`SettingsValidationTests` |
| `SNM.Agent.Tests` | `ProcParserTests`、`NicFilterTests`、`IpFilterTests`、`DiskFilterTests`、`BackoffTests`、`CliParseTests`、`ProxyParserTests`(http 带凭据、socks5、none) | PROTOCOL §9 |

### 9.2 集成测试(`SNM.Master.Tests`,`WebApplicationFactory<Program>`,每个测试类独立临时 SQLite 文件,`Snm:DataDir` 指向临时目录)

| 测试 | 步骤与断言 |
|---|---|
| `AgentHubIntegrationTests` | 用 `SnmMessagePackHubProtocol` 的 `HubConnection` 连 `/hubs/agent`:错误 Key → 401;正确 Key → `register` 返回配置;3 次 `hb` → `GET /api/nodes/{id}` 的 `live` 与 `ips`(含服务端捕获 IP)正确;`Seq` 重复被丢弃;第二连接抢占后第一连接关闭 |
| `PublicHubIntegrationTests` | 官方 MessagePack 客户端连 `/hubs/public`:收到 `snapshot`;心跳后 ≤ 3 s 收到 `batch`;序列化字节不含节点 IP/主机名/备注;`PublicVisible=false` 节点不出现 |
| `AdminHubIntegrationTests` | JWT 通过 `access_token` 连接;`GetHistory` 返回 ≤ 60 点;告警触发时收到 `alert` |
| `AuthApiTests` | 登录/刷新/轮换/重放/登出/改密/锁定 |
| `NodesApiTests` | CRUD、校验错误格式、唯一名 409、轮换密钥使旧连接 401、安装脚本令牌流程(`/install/{token}` 内容含 Key 与 Server URL、过期 404) |
| `MetricsPipelineTests` | 注入心跳 → 手动触发 flush/rollup 服务方法 → `GET /api/nodes/{id}/metrics?range=24h|7d|30d` 返回点 |
| `AlertOfflineE2ETests` | 用 `FakeTimeProvider`(`TimeProvider` 注入)推进时间:心跳停止 → 30 s + 2 次评估 → 事件 Firing 且 Webhook(测试内 `HttpMessageHandler` 桩)收到 `alert.firing`;恢复 → `alert.resolved` |
| `TemplateCompatTests` | `/api/user/detail`、`/api/role/permissions/tree`、`/api/permission/menu/validate` 形状快照 |

时间:所有时间相关服务通过 `TimeProvider` 注入(`TimeProvider.System` 生产;测试用 `Microsoft.Extensions.TimeProvider.Testing` 的 `FakeTimeProvider`)。

### 9.3 前端与端到端

- `web/admin`:`npm ci && npm run build` 必须 0 错误;`npm run lint:fix -- --max-warnings=0`(可选)。
- `scripts/e2e.sh`(Git Bash/Linux 可跑,不依赖 jq;用 `grep -o`/`sed` 提取 JSON 字段):
  1. `source scripts/env.sh`;`dotnet build -c Release`;`bash scripts/build-web.sh`。
  2. 起 `tools/SNM.WebhookSink`(端口 5090,写 `/tmp/snm-e2e/webhook.jsonl`)。
  3. 起 Master(`SNM_DATA_DIR=/tmp/snm-e2e/data SNM_ADMIN_PASSWORD=E2ePass123! SNM_PUBLIC_BASE_URL=http://127.0.0.1:5080 ASPNETCORE_ENVIRONMENT=Development`),轮询 `/healthz` ≤ 30 s。
  4. 登录取 token;`PATCH /api/settings` 把 `alert.offlineTimeoutSec=30`、`alert.offlineConsecutive=1`、`alert.cooldownMin=30`;创建 Webhook 渠道指向 `http://127.0.0.1:5090/hook`,测试消息成功。
  5. `POST /api/nodes` 得到 `agentKey`;起 Agent `dotnet run --project src/SNM.Agent -- run --server http://127.0.0.1:5080 --key …`。
  6. 轮询 `GET /api/nodes/{id}` ≤ 40 s:`state.status==1`、`live.cpuPermille` 存在、`ips` 非空、`hardware.cpuCores>0`。
  7. `GET /`(大屏 HTML 200)与 `GET /admin/`(200);用 `curl` 拉 `/api/nodes/{id}` 的 IP 与 `hostname`,断言 `GET /`(HTML)与 `wwwroot/js/app.js` 中不含它们(静态检查),并用 `tools/SNM.WebhookSink` 之外的小工具 `tools/SNM.HubProbe`(可选)抓 `/hubs/public` 快照字节做子串断言。
  8. 等 ≥ 70 s 后 `GET /api/nodes/{id}/metrics?range=24h` 至少 1 个点。
  9. 杀掉 Agent;≤ 60 s 内 `webhook.jsonl` 出现 `"event":"alert.firing"` 且 `"rule":"offline"`。
  10. 重启 Agent;≤ 30 s 内出现 `"event":"alert.resolved"`。
  11. 清理进程;输出 `E2E PASS`/`E2E FAIL: <step>` 并以退出码表示。

---

## 10. 实施顺序与验收(供互不通信的实现 agent 逐模块认领)

| 模块 | 内容 | 依赖 | 验收(全部满足) |
|---|---|---|---|
| **M0 骨架** | `global.json`、`Directory.Build.props/Packages.props`、`.sln`、空项目、`scripts/env.sh`(已有)、`README.md` 目录说明 | — | `dotnet build ServerNodeMonitor.sln -c Release` 0 错误 |
| **M1 Contracts** | PROTOCOL 附录 A 全部代码 + vendored 文件 + `THIRD-PARTY-NOTICES.md`;`tests/SNM.Contracts.Tests` | M0 | 测试全绿;`dotnet build` 0 警告(分析器开);`scripts/ilc-check.sh`(对一个引用 Contracts 且构造 `SnmMessagePackHubProtocol` 的最小控制台执行 BRIEF 的 ILC 命令)`warning IL` 计数 0 |
| **M2 Master 基础** | 配置/选项、`SnmDbContext` + 全部实体 + 初始迁移、PRAGMA 拦截器、`SettingsService`、认证(AgentKey/JWT/refresh/PBKDF2)、`ApiResponse` 中间件、模板兼容接口、`/healthz`、静态托管与 SPA 回退、`NodeRegistry` 装载、Nodes CRUD REST(不含实时值) | M1 | 集成测试 `AuthApiTests`、`TemplateCompatTests`、`NodesApiTests`(除轮换断连)绿;手工 curl 登录成功;`data/snm.db` 为 WAL |
| **M3 Agent** | 全部采集器、CLI、`AgentSession`、重连、日志、`test` 子命令、AOT csproj | M1(可与 M2 并行) | `tests/SNM.Agent.Tests` 绿;`dotnet run -- test` 在 Windows 本机输出合理值;`dotnet build -c Release` 0 警告;ILC 命令 0 IL 警告;体积估算记录 |
| **M4 Master 接入与时序** | `AgentHub`、R1–R10、`TrafficEngine`、`BillingPeriod`、`IpMerger`、`GeoIpService` + 刷新、`MinuteAccumulator`、Flush/Rollup/Retention 服务、metrics/traffic REST、dashboard summary | M2 | `TrafficEngineTests`、`BillingPeriodTests`、`RollupTests`、`AgentHubIntegrationTests`、`MetricsPipelineTests` 绿;本机 Master+Agent 运行 2 min 后 `metrics?range=24h` 有点,节点显示 IP/国旗(GeoIP 下载成功后) |
| **M5 实时 Hub** | `PublicHub`、`AdminHub`、`RealtimeBroadcaster`、`LiveSnapshotBuilder`、脱敏测试 | M4 | `PublicHubIntegrationTests`、`AdminHubIntegrationTests` 绿 |
| **M6 告警与通知** | `AlertEngine`、`AlertTexts`、`ExpiryCheckService`、`NotificationDispatcher`、Telegram/Webhook、渠道 REST、alerts REST、`tools/SNM.WebhookSink` | M4 | `AlertEngineTests`、`AlertOfflineE2ETests` 绿;手工:停 Agent > 30 s 本地 Sink 收到离线,恢复后收到恢复 |
| **M7 管理后台** | FRONTEND.md §2–§6 | M2–M6 的 REST/Hub(可先用接口文档并行开发) | `npm run build` 通过;登录 → 节点在线、IP/国旗/CPU/内存/磁盘/网速正确、ECharts 有数据;所有页面无控制台错误 |
| **M8 公开大屏** | FRONTEND.md §7 + `build-web.sh` | M5 | 打开 `/` 实时刷新;DevTools 网络面板仅 `/hubs/public` 与静态资源;快照字节不含敏感字段 |
| **M9 部署/CI/脚本** | DEPLOY.md 全部:systemd、install 模板与渲染、Dockerfile、两个 workflow、`dev.sh`、`e2e.sh`、`ilc-check.sh`、备份文档 | 全部 | `bash -n` 全部脚本通过;`e2e.sh` PASS;workflow YAML 经 `node -e "require('yaml')…"` 或 `python -c yaml`(CI 上)解析通过;安装脚本在临时目录以 `SNM_DRY_RUN=1` 执行不报错 |

并行建议(4 核 → 2 个实现 agent):A 线 = M1 → M3 → M8/M9 的 Agent 部分;B 线 = M0 → M2 → M4 → M5 → M6;M7 由先空闲者承接。每个模块的 agent 必须在返回前跑通自己的验收命令。

---

## 11. 主要风险与缓解

| 风险 | 缓解 |
|---|---|
| 包升级导致 AOT 警告再现 | `TreatWarningsAsErrors` + CI 中 ILC 警告计数门禁;PROTOCOL §2.3 规则;`ResolverRulesTests` 元数据断言 |
| Windows 采集器无法在 CI(Linux)验证 | 结构大小断言(`sizeof(MIB_IF_ROW2)==1352`)、win-x64 runner 上执行 `snm-agent test` 作为烟测 |
| 反代未开 WebSocket 导致只走 LongPolling | 可用但延迟高;DEPLOY.md 给出 Nginx/1Panel 明确配置;`/api/system/info` 暴露各连接传输类型便于排查(可选) |
| SQLite 单写锁在大量节点下的写放大 | 分钟级批量事务;100 节点每分钟 ≈ 400 行,远低于上限;>1000 节点时建议拆库(超出本版本范围) |
| 时区/账期误解 | `BillingPeriodTests` 覆盖闰年/夏令时;UI 显示账期起止与时区 |
| GeoIP 下载失败 | 不影响启动;国家码为空;后台可手动刷新;支持镜像 URL |
