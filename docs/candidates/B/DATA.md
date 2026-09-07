# DATA.md — 数据模型、降采样、流量计费、告警与设置(候选设计 B)

> 本文是 Master 端所有持久化数据与数据计算引擎的唯一契约。实现者(`src/SNM.Master`)按本文建表、写迁移、实现后台任务;测试(`tests/SNM.Master.Tests`)按第 11 节用例验收。文档中的标识符(表名、列名、类名、设置键)必须原样使用。

## 0. 全局约定

| 约定 | 决定 |
|---|---|
| 数据库 | SQLite 单文件 `{DataDir}/snm.db`,`journal_mode=WAL`,`synchronous=NORMAL`,`busy_timeout=5000`,`foreign_keys=ON`,`temp_store=MEMORY`,`cache_size=-32768`(32 MB)。连接串:`Data Source={path};Cache=Shared;Pooling=True` |
| ORM | EF Core Sqlite 10.0.11;`SnmDbContext`;实体类放 `SNM.Master/Data/Entities/`,配置放 `SNM.Master/Data/Configurations/*Configuration.cs`(`IEntityTypeConfiguration<T>`) |
| 时间 | **热路径表**(`Metrics1m/1h/1d`、`TrafficDaily/TrafficMonthly`、`NodeRuntime`)的时间列全部为 `long` Unix **秒**(UTC),列名后缀 `Unix`;低频表用 `DateTime`(Kind=Utc,EF 默认 TEXT ISO-8601)。对外 API 一律 Unix **毫秒**或 ISO-8601 字符串,见 API.md |
| 整数 | 字节计数、MB 计数、速率用 `long`(SQLite INTEGER 有符号 64 位;**不要用 `ulong` 实体属性**,Microsoft.Data.Sqlite 对超过 `long.MaxValue` 的 `ulong` 会抛异常)。DTO 中的 `ulong/uint/ushort` 在 Hub 层转换为 `long/int` |
| 单位 | CPU 千分比 `‰`(0..1000);内存/交换/磁盘 **MiB**(1 MiB = 1048576 B,文中简写 MB);网络累计 **字节**;速率 **字节/秒**(bps 在本文指 bytes/s);Load1 = 1 分钟负载 × 100 |
| 主键 | 低频表 `Id INTEGER PRIMARY KEY AUTOINCREMENT`;时序表复合主键 `(NodeId, BucketStartUnix)` |
| 删除 | 节点删除 → 级联删除其全部时序/流量/告警/磁盘/状态行(`OnDelete(Cascade)`) |
| 布尔 | SQLite INTEGER 0/1,EF 默认映射 |
| 小数 | 金额 `decimal(18,2)` → EF Sqlite 默认 TEXT 存储,排序/比较在应用层 |
| JSON 列 | 后缀 `Json`,`TEXT`,由 `System.Text.Json` 序列化(Master 非 AOT,可用反射;但推荐 `JsonSerializerContext` 源生成以统一) |
| 写入并发 | 所有热路径写入(每分钟 flush、rollup、retention)通过单例 `SqliteWriteGate : SemaphoreSlim(1,1)` 串行;REST 的低频写入直接走 EF(靛 busy_timeout 兜底) |

## 1. 实体清单

| 表 | 类 | 用途 | 行数量级(100 节点) |
|---|---|---|---|
| `AdminUsers` | `AdminUser` | 单管理员账号 | 1 |
| `RefreshTokens` | `RefreshToken` | JWT 刷新令牌(哈希) | <50 |
| `Nodes` | `Node` | 节点配置 + 资产/财务 + 清单(注册信息) | 100 |
| `NodeRuntime` | `NodeRuntime` | 节点运行态持久化(最后心跳、流量计数器基线) | 100 |
| `NodeDisks` | `NodeDisk` | 每挂载点磁盘明细(最新一份) | ~300 |
| `Metrics1m` | `Metric1m` | 1 分钟均值/峰值,保留 25h | 144k |
| `Metrics1h` | `Metric1h` | 1 小时,保留 8d | 19k |
| `Metrics1d` | `Metric1d` | 1 天,保留 32d | 3.2k |
| `TrafficDaily` | `TrafficDaily` | 节点本地日流量,保留 400d | 40k |
| `TrafficMonthly` | `TrafficMonthly` | 账单周期流量,永久 | 1.2k/年 |
| `AlertStates` | `AlertState` | 告警状态机持久化 (NodeId, RuleKey) | 600 |
| `AlertEvents` | `AlertEvent` | 告警事件(触发/恢复),保留 90d | 视情况 |
| `AlertChannels` | `AlertChannel` | 通知渠道(Telegram/Webhook) | <10 |
| `NotificationLogs` | `NotificationLog` | 通知发送日志,保留 30d | 视情况 |
| `Settings` | `Setting` | 键值设置 | <60 |

### 1.1 `AdminUsers`

| 列 | 类型 | 空 | 说明 |
|---|---|---|---|
| Id | INTEGER PK AUTOINCREMENT | 否 | |
| Username | TEXT(64) | 否 | UNIQUE,大小写敏感精确匹配 |
| PasswordHash | TEXT(256) | 否 | 格式 `pbkdf2-sha256$600000$<salt b64>$<hash b64>`;`Rfc2898DeriveBytes.Pbkdf2`,16 字节盐,32 字节输出 |
| TokenVersion | INTEGER | 否 | 默认 1;改密/强制登出时 +1,JWT 内 `tv` claim 不等则拒绝 |
| CreatedAt | TEXT(DateTime) | 否 | |
| UpdatedAt | TEXT(DateTime) | 否 | |
| LastLoginAt | TEXT(DateTime) | 是 | |

索引:`UX_AdminUsers_Username (Username)`。

### 1.2 `RefreshTokens`

| 列 | 类型 | 空 | 说明 |
|---|---|---|---|
| Id | INTEGER PK | 否 | |
| UserId | INTEGER FK→AdminUsers.Id Cascade | 否 | |
| TokenHash | TEXT(64) | 否 | UNIQUE;SHA-256 hex of 明文刷新令牌(明文 = 32 字节随机 base64url,43 字符) |
| ExpiresAt | TEXT(DateTime) | 否 | 创建 + `Snm:Jwt:RefreshTokenDays`(默认 14) |
| CreatedAt | TEXT(DateTime) | 否 | |
| RevokedAt | TEXT(DateTime) | 是 | 登出/轮换时置值 |
| ReplacedByHash | TEXT(64) | 是 | 轮换链(检测重放:已轮换的令牌再次使用 → 撤销整条链) |
| RemoteIp | TEXT(45) | 是 | |
| UserAgent | TEXT(256) | 是 | |

索引:`UX_RefreshTokens_TokenHash`、`IX_RefreshTokens_UserId`、`IX_RefreshTokens_ExpiresAt`。

### 1.3 `Nodes`

| 列 | 类型 | 空 | 默认 | 说明 |
|---|---|---|---|---|
| Id | INTEGER PK | 否 | | 节点 ID,对外 API/Hub 使用 |
| PublicName | TEXT(64) | 否 | | 大屏脱敏名,如 `HK-Node-01`;唯一性不强制,但 UI 提示重复 |
| AdminRemark | TEXT(512) | 是 | | 后台私密备注 |
| GroupName | TEXT(64) | 是 | | 分组(大屏与列表可按组显示) |
| SortOrder | INTEGER | 否 | 0 | 排序权重(升序),相同按 Id |
| Enabled | INTEGER(bool) | 否 | 1 | 关闭后拒绝该节点探针连接、不评估告警、不在大屏显示 |
| AgentKey | TEXT(64) | 否 | | UNIQUE;32 字节 `RandomNumberGenerator` → base64url(43 字符,无填充)。**明文存储**(后台需随时生成安装脚本;数据目录 0700 保护;可轮换) |
| AgentKeyRotatedAt | TEXT(DateTime) | 是 | | |
| Hostname | TEXT(255) | 是 | | 以下 “清单” 列由注册消息写入 |
| Os | TEXT(128) | 是 | | 如 `Ubuntu 24.04.2 LTS` / `Windows Server 2022` |
| Kernel | TEXT(128) | 是 | | `6.8.0-45-generic` / `10.0.20348` |
| Arch | TEXT(16) | 是 | | `x64` / `arm64` |
| CpuModel | TEXT(255) | 是 | | 探针已含 `2x ` 前缀,如 `2x Intel(R) Xeon(R) Gold 6230` |
| CpuCores | INTEGER | 是 | | 逻辑核心总数 |
| MemTotalMb | INTEGER | 是 | | |
| SwapTotalMb | INTEGER | 是 | | |
| DiskTotalMb | INTEGER | 是 | | 计入的全部挂载点容量之和 |
| AgentVersion | TEXT(32) | 是 | | |
| InterfacesJson | TEXT | 是 | | 探针计入流量的网卡名数组 `["eth0","eth1"]` |
| IpsJson | TEXT | 是 | `[]` | **合并后**的 IP 列表 JSON 数组:探针上报的本地 IP(去重、排序)∪ 服务端捕获的公网 IP;每项 `{"ip":"1.2.3.4","v":4,"src":"agent"\|"server","public":true}` |
| PublicIp | TEXT(45) | 是 | | 服务端在连接时捕获的远端 IP(经 ForwardedHeaders) |
| CountryCodeAuto | TEXT(2) | 是 | | GeoIP 对 PublicIp 的解析结果,ISO 3166-1 alpha-2 大写 |
| CountryCodeOverride | TEXT(2) | 是 | | 管理员手动覆盖;**生效国家码 = Override ?? Auto** |
| RegisteredAt | TEXT(DateTime) | 是 | | 首次注册 |
| TrafficLimitBytes | INTEGER | 否 | 0 | 0 = 不限 |
| TrafficResetDay | INTEGER | 否 | 1 | 1..31,月底钳制 |
| TrafficCountMode | INTEGER | 否 | 0 | 0=Sum(rx+tx) 1=Rx 2=Tx 3=Max(rx,tx) |
| TimeZoneId | TEXT(64) | 是 | | IANA,如 `Asia/Shanghai`;空 → 全局 `general.timeZone` |
| Provider | TEXT(64) | 是 | | 供应商 |
| RenewPrice | TEXT(decimal 18,2) | 是 | | 每个账期价格 |
| Currency | TEXT(3) | 是 | | `USD`/`CNY`/`EUR`(UI 下拉,后端不限制) |
| BillingCycleMonths | INTEGER | 是 | | 1/3/6/12/24/36;MRR = RenewPrice / BillingCycleMonths |
| PurchasedAt | TEXT(DateTime) | 是 | | |
| ExpiresAt | TEXT(DateTime) | 是 | | 到期日(UTC 00:00 存储,UI 按日期) |
| AutoRenew | INTEGER(bool) | 否 | 0 | 仅展��� |
| AlertOverridesJson | TEXT | 是 | | 节点级阈值覆盖,部分键,如 `{"offlineSec":60,"cpuPercent":95,"trafficPercent":90,"disabledRules":["mem"]}`;键集合见 §6.2 |
| CreatedAt / UpdatedAt | TEXT(DateTime) | 否 | | |

索引:`UX_Nodes_AgentKey (AgentKey)`、`IX_Nodes_Enabled_SortOrder (Enabled, SortOrder)`、`IX_Nodes_ExpiresAt (ExpiresAt)`。

### 1.4 `NodeRuntime`(1:1 于 Nodes,主键即 NodeId)

把高频更新的运行态与配置分离,避免每分钟改写宽表 `Nodes`。

| 列 | 类型 | 空 | 说明 |
|---|---|---|---|
| NodeId | INTEGER PK FK→Nodes Cascade | 否 | |
| LastSeenUnix | INTEGER | 是 | 最后一次收到心跳/注册的服务器时间(秒) |
| LastConnectedUnix | INTEGER | 是 | 最近一次 Hub 连接建立 |
| LastDisconnectedUnix | INTEGER | 是 | |
| LastUptimeSec | INTEGER | 是 | 最后心跳的 UptimeSec,用于重启判定 |
| LastRxBytes | INTEGER | 是 | 流量 Delta 基线(最后一次样本的累计值) |
| LastTxBytes | INTEGER | 是 | |
| LastSampleUnix | INTEGER | 是 | 基线样本的服务器时间(秒) |
| UpdatedUnix | INTEGER | 否 | |

(最新样本值 cpu/mem/rate 等**不持久化**,Master 重启后由新心跳填充。)

写入时机:每分钟 flush 事务内 UPSERT;优雅关闭时写一次。**`LastRx/Tx/Uptime/Sample` 四列必须与 `TrafficDaily/TrafficMonthly` 在同一事务内提交**(§5.6),否则崩溃恢复会重复计费或漏计。

### 1.5 `NodeDisks`

| 列 | 类型 | 空 | 说明 |
|---|---|---|---|
| Id | INTEGER PK | 否 | |
| NodeId | INTEGER FK Cascade | 否 | |
| Mount | TEXT(255) | 否 | 挂载点 `/`、`/data`、`C:\` |
| FsType | TEXT(32) | 是 | `ext4`/`xfs`/`NTFS` |
| TotalMb | INTEGER | 否 | |
| UsedMb | INTEGER | 否 | |
| UpdatedUnix | INTEGER | 否 | |

索引:`UX_NodeDisks_NodeId_Mount (NodeId, Mount)`。每次 `DiskReport`(默认 60s)整表替换该节点的行:不在报告中的挂载点删除。

### 1.6 `Metrics1m` / `Metrics1h` / `Metrics1d`(三表同构)

| 列 | 类型 | 说明 |
|---|---|---|
| NodeId | INTEGER FK Cascade | 复合 PK 第一列 |
| BucketStartUnix | INTEGER | 桶起点(秒):1m 表为 60 的倍数;1h 表为 3600 的倍数;1d 表为 86400 的倍数(**UTC 日界**) |
| CpuAvg | INTEGER | ‰,样本加权均值,四舍五入 |
| CpuMax | INTEGER | ‰ |
| MemUsedAvgMb | INTEGER | |
| MemUsedMaxMb | INTEGER | |
| SwapUsedAvgMb | INTEGER | |
| DiskUsedAvgMb | INTEGER | |
| Load1Avg | INTEGER | 可空(Windows 无负载 → NULL) |
| Load1Max | INTEGER | 可空 |
| RxRateAvgBps | INTEGER | 字节/秒;= RxBytesDelta / 有效秒数 |
| RxRateMaxBps | INTEGER | 桶内单样本速率峰值 |
| TxRateAvgBps | INTEGER | |
| TxRateMaxBps | INTEGER | |
| RxBytesDelta | INTEGER | 桶内累计增量(经 Delta 引擎判定后的有效字节) |
| TxBytesDelta | INTEGER | |
| Samples | INTEGER | 1m:心跳样本数(2s 间隔满桶 = 30);1h/1d:下级样本数之和 |
| SpanSec | INTEGER | 有效覆盖秒数(1m:Σ ElapsedMs/1000,上限 60;1h/1d:Σ 下级 SpanSec)。用于速率加权 |

主键 `(NodeId, BucketStartUnix)`;附加索引 `IX_<表>_BucketStartUnix (BucketStartUnix)`(保留任务按时间范围删除)。

### 1.7 `TrafficDaily`

| 列 | 类型 | 说明 |
|---|---|---|
| NodeId | INTEGER FK Cascade | PK-1 |
| LocalDate | TEXT(10) | PK-2,节点时区本地日期 `YYYY-MM-DD` |
| RxBytes | INTEGER | |
| TxBytes | INTEGER | |
| UpdatedUnix | INTEGER | |

### 1.8 `TrafficMonthly`(账单周期)

| 列 | 类型 | 说明 |
|---|---|---|
| Id | INTEGER PK | |
| NodeId | INTEGER FK Cascade | |
| PeriodStartUnix | INTEGER | 周期起点(UTC 秒);UNIQUE(NodeId, PeriodStartUnix) |
| PeriodEndUnix | INTEGER | 周期终点(不含) |
| RxBytes / TxBytes | INTEGER | 周期内累计 |
| LimitBytesSnapshot | INTEGER | 周期创建/最近修改时的限额快照(历史行不随配置变化) |
| ResetDaySnapshot | INTEGER | |
| CountModeSnapshot | INTEGER | |
| TimeZoneSnapshot | TEXT(64) | |
| IsClosed | INTEGER(bool) | 周期结束或手工重置后置 1 |
| ClosedReason | TEXT(16) | `rollover` / `manual` / `config` |
| UpdatedUnix | INTEGER | |

索引:`UX_TrafficMonthly_Node_Start (NodeId, PeriodStartUnix)`、`IX_TrafficMonthly_Node_Closed (NodeId, IsClosed)`。**任一时刻每节点恰有一行 `IsClosed=0`**(当前周期)。

### 1.9 `AlertStates`

| 列 | 类型 | 说明 |
|---|---|---|
| NodeId | INTEGER FK Cascade | PK-1 |
| RuleKey | TEXT(16) | PK-2:`offline` `cpu` `mem` `disk` `traffic` `expiry` |
| State | INTEGER | 0 Normal,1 Pending,2 Firing,3 Recovering |
| ConsecutiveHits | INTEGER | 连续命中次数 |
| ConsecutiveOks | INTEGER | 连续恢复次数 |
| OpenEventId | INTEGER | 当前未关闭的 AlertEvents.Id,可空 |
| LastFiredUnix | INTEGER | 可空 |
| LastNotifiedUnix | INTEGER | 可空;冷却计算基准 |
| UpdatedUnix | INTEGER | |

### 1.10 `AlertEvents`

| 列 | 类型 | 说明 |
|---|---|---|
| Id | INTEGER PK | |
| NodeId | INTEGER FK Cascade | |
| RuleKey | TEXT(16) | |
| Severity | INTEGER | 1 Info 2 Warning 3 Critical |
| Status | INTEGER | 1 Firing 2 Resolved |
| FiredUnix | INTEGER | |
| ResolvedUnix | INTEGER | 可空 |
| Value | REAL | 触发时观测值(秒/百分比/字节) |
| Threshold | REAL | 触发阈值 |
| Title | TEXT(128) | 中文标题,如 `HK-Node-01 离线` |
| Message | TEXT(1024) | 中文正文 |
| Notified | INTEGER(bool) | 触发通知是否实际发送(冷却内为 0) |
| NotifiedUnix | INTEGER | 可空 |
| RecoveryNotified | INTEGER(bool) | |
| Occurrences | INTEGER | Recovering→Firing 反复次数 |
| DedupKey | TEXT(32) | `{NodeId}:{RuleKey}` 便于查询 |

索引:`IX_AlertEvents_Node_Rule_Status (NodeId, RuleKey, Status)`、`IX_AlertEvents_FiredUnix (FiredUnix)`、`IX_AlertEvents_Status (Status)`。

### 1.11 `AlertChannels`

| 列 | 类型 | 说明 |
|---|---|---|
| Id | INTEGER PK | |
| Name | TEXT(64) | |
| Type | INTEGER | 1 Telegram 2 Webhook |
| Enabled | INTEGER(bool) | |
| MinSeverity | INTEGER | 1..3,低于该级别不发 |
| ConfigJson | TEXT | 见 §6.6(含密钥;API 返回时脱敏) |
| CreatedAt / UpdatedAt | TEXT(DateTime) | |

### 1.12 `NotificationLogs`

| 列 | 类型 | 说明 |
|---|---|---|
| Id | INTEGER PK | |
| ChannelId | INTEGER FK Cascade | |
| AlertEventId | INTEGER | 可空(测试消息为 NULL) |
| Kind | TEXT(16) | `firing` / `resolved` / `test` |
| SentUnix | INTEGER | |
| Success | INTEGER(bool) | |
| HttpStatus | INTEGER | 可空 |
| Error | TEXT(512) | 可空 |
| DurationMs | INTEGER | |
| Attempt | INTEGER | 1..3 |

索引:`IX_NotificationLogs_SentUnix`。

### 1.13 `Settings`

| 列 | 类型 | 说明 |
|---|---|---|
| Key | TEXT(64) PK | 小写点分,如 `alert.cpuPercent` |
| Value | TEXT | 字符串;数值/布尔/JSON 均以文本存,类型由 §7 键表决定 |
| UpdatedAt | TEXT(DateTime) | |

## 2. WAL、迁移与启动

1. **迁移策略**:使用 EF Core Migrations(检入 `src/SNM.Master/Data/Migrations/`,首个迁移名 `Initial`)。启动时 `StartupInitializer`(第一个 `IHostedService`)执行 `db.Database.MigrateAsync()`;**禁止** `EnsureCreated`(与迁移不兼容)。开发期新增迁移:`dotnet ef migrations add <Name> --project src/SNM.Master`(需 `Microsoft.EntityFrameworkCore.Design`,`PrivateAssets=all`)。
2. **PRAGMA**:迁移完成后在同一连接执行 `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;`。`journal_mode` 是持久属性,只需一次;其余通过 `SqliteConnection` 打开时的 `DbConnectionInterceptor`(`ConnectionOpened`)每连接执行(`foreign_keys/busy_timeout/temp_store` 是连接级)。
3. **文件权限**:首次创建 `{DataDir}`(0700)与 `snm.db`(0600,Linux 下 `File.SetUnixFileMode`)。
4. **备份**:在线备份用 `VACUUM INTO '{DataDir}/backup/snm-YYYYMMDD-HHmm.db'`(API `POST /api/system/backup` 与每日 04:30 UTC 任务,保留最近 7 份);**不要**直接 `cp snm.db`(WAL 内容会丢);冷备需同时拷 `snm.db`、`snm.db-wal`、`snm.db-shm` 或先 `PRAGMA wal_checkpoint(TRUNCATE)`。
5. **维护**:每日 04:30 UTC `PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize;`;`VACUUM` 默认关闭(设置 `maintenance.vacuumWeekly=false`),开启后周日 04:40 UTC 执行。
6. **种子**:`AdminUsers` 为空时用 `Snm:Admin:Username/Password`(环境变量 `SNM_ADMIN_USER/SNM_ADMIN_PASSWORD`)创建;未提供密码 → 生成 16 字符随机密码并以 **Warning** 级别打印一次(仅首次)。`Settings` 缺失键不落库,读取时用 §7 默认值;仅显式保存的键写入。
7. **启动顺序**(全部在 `StartupInitializer.StartAsync` 内顺序完成,之后其他 BackgroundService 才开始):迁移 → PRAGMA → 种子管理员 → 加载 Settings 缓存 → 加载 Nodes+NodeRuntime 到 `NodeStateStore` → 加载 `AlertStates` → 为每个节点确保当前 `TrafficMonthly` 行存在(§5.4)→ 执行一次补偿 rollup(§4.4)→ 标记 ready(`/healthz` 返回 200)。

## 3. 心跳内存聚合 → `Metrics1m`

心跳(默认 2s)**不落库**。`NodeStateStore`(单例,`ConcurrentDictionary<int, NodeRuntimeState>`)为每节点维护:

```text
NodeRuntimeState
  ConnectionId, IsOnline, LastSeenUnixMs, LastSeenTick(Environment.TickCount64)
  Latest: LatestSample (cpu‰, memUsedMb, swapUsedMb, diskUsedMb, load1, rxBps, txBps, uptimeSec, rxTotal, txTotal, serverUnixMs)
  Wave: RingBuffer<WavePoint>(capacity = Snm:Realtime:WavePoints, 默认 90)   // 推给浏览器的波浪图
  Traffic: TrafficAccumulator (§5)
  Bucket: MinuteBucket?  { BucketStartUnix, Samples, SpanMs, CpuSum, CpuMax, MemSum, MemMax, SwapSum, DiskSum, Load1Sum, Load1Max, Load1Samples, RxDelta, TxDelta, RxRateMax, TxRateMax }
  Alerts: Dictionary<RuleKey, AlertRuntime>
  Dirty: bool (自上次广播后有新样本)
```

**样本处理(`HeartbeatProcessor.Process(nodeId, HeartbeatDto dto)`,Hub 线程,必须 O(1) 且无 IO):**

```text
nowMs   = UnixMs(UtcNow);  nowTick = TickCount64
st      = store[nodeId]
// 1. 校验与钳制
cpu     = min(dto.Cpu, 1000)
memUsed = min(dto.MemUsedMb, node.MemTotalMb or uint.Max)
// 2. 有效间隔(优先探针单调时钟,其次服务器单调时钟)
elapsedMs = dto.ElapsedMs > 0 ? dto.ElapsedMs : (st.LastSeenTick == 0 ? 0 : nowTick - st.LastSeenTick)
elapsedMs = clamp(elapsedMs, 0, 600_000)          // >10 分钟按 10 分钟计(用于速率),流量归属仍按真实时间(§5)
// 3. 流量 Delta(§5.2)→ (rxDelta, txDelta, valid)
(rxDelta, txDelta) = st.Traffic.Apply(dto.NetRxBytes, dto.NetTxBytes, dto.UptimeSec, nowMs, elapsedMs)
rxBps = elapsedMs >= 200 ? rxDelta * 1000 / elapsedMs : 0     // <200ms 视为突发重复,不算速率
txBps = ...
// 4. 最新样本 + 波浪图
st.Latest = {...}; st.Wave.Push(new WavePoint(nowMs, cpu, memPermille, rxBps, txBps)); st.Dirty = true
st.LastSeenUnixMs = nowMs; st.LastSeenTick = nowTick
// 5. 分钟桶
bucketStart = nowMs / 1000 / 60 * 60
if st.Bucket == null || st.Bucket.BucketStartUnix != bucketStart:
    if st.Bucket != null: flushQueue.Enqueue(st.Bucket)     // 关闭旧桶(Channel<MinuteBucket>,无界)
    st.Bucket = new MinuteBucket(bucketStart)
b = st.Bucket
b.Samples++; b.SpanMs += elapsedMs
b.CpuSum += cpu; b.CpuMax = max; b.MemSum += memUsed; b.MemMax = max; b.SwapSum += swap; b.DiskSum += disk
if dto.Load1 > 0 || node.IsLinux: b.Load1Sum += load1; b.Load1Samples++; b.Load1Max = max
b.RxDelta += rxDelta; b.TxDelta += txDelta; b.RxRateMax = max(rxBps); b.TxRateMax = max(txBps)
```

**Flush(`MetricsFlushService`,每 60s 在秒数 = 2 时唤醒,即 HH:MM:02):**

1. 取出 `flushQueue` 全部已关闭桶;另外扫描 store,把 `BucketStartUnix < 当前分钟起点` 且仍挂在 `st.Bucket` 上的桶(节点已停止发心跳)也关闭并取出。
2. 单事务(经 `SqliteWriteGate`):
   - `INSERT INTO Metrics1m ... ON CONFLICT(NodeId,BucketStartUnix) DO UPDATE SET` 各字段按 **样本加权合并**(`CpuAvg=(CpuAvg*Samples+excluded.CpuAvg*excluded.Samples)/(Samples+excluded.Samples)`,Max 取大,Delta/Samples/SpanSec 相加)。正常运行不会冲突;冲突只在崩溃恢复重放时出现,因此幂等。
   - 行值:`CpuAvg=round(CpuSum/Samples)`,`RxRateAvgBps = SpanMs>0 ? RxDelta*1000/SpanMs : 0`,`SpanSec=min(round(SpanMs/1000),60)`,`Load1Avg = Load1Samples>0 ? round(Load1Sum/Load1Samples) : NULL`。
   - `TrafficDaily/TrafficMonthly` UPSERT(§5.5)与 `NodeRuntime` UPSERT(§1.4)。
3. 失败:记录 Error,桶放回队列(最多重试 3 轮,之后丢弃并计数 `snm_metrics_dropped_buckets`),流量累加器不清零(仍在内存,下次一起提交)。
4. 优雅关闭(`StopAsync`):关闭所有节点当前桶并 flush 一次(含不满一分钟的桶,`SpanSec` 按实际)。

## 4. 降采样(rollup)与保留(retention)

### 4.1 层级

| 层 | 表 | 桶 | 数据源 | 保留(删除条件) | 图表范围 |
|---|---|---|---|---|---|
| 热 | Metrics1m | 60s | 心跳内存聚合 | `BucketStartUnix < now-25h` | 24h(最多 1440 点) |
| 温 | Metrics1h | 3600s | Metrics1m | `< now-8d` | 7d(168 点) |
| 冷 | Metrics1d | 86400s(UTC) | Metrics1h | `< now-32d` | 30d(30 点) |

保留窗口比 PRD 的 24h/7d/30d 各多 1h/1d/2d,以保证 rollup 与图表边界的完整性;`>30 天` 数据被 Metrics1d 的 32d 删除覆盖(第 31/32 天仅为缓冲)。

### 4.2 Rollup SQL(幂等;`@start` 为目标桶起点,`@len` 为 3600 或 86400,源表为下一级)

```sql
INSERT INTO Metrics1h (NodeId, BucketStartUnix, CpuAvg, CpuMax, MemUsedAvgMb, MemUsedMaxMb, SwapUsedAvgMb,
  DiskUsedAvgMb, Load1Avg, Load1Max, RxRateAvgBps, RxRateMaxBps, TxRateAvgBps, TxRateMaxBps,
  RxBytesDelta, TxBytesDelta, Samples, SpanSec)
SELECT NodeId, @start,
  CAST(ROUND(SUM(CpuAvg * Samples) * 1.0 / SUM(Samples)) AS INTEGER), MAX(CpuMax),
  CAST(ROUND(SUM(MemUsedAvgMb * Samples) * 1.0 / SUM(Samples)) AS INTEGER), MAX(MemUsedMaxMb),
  CAST(ROUND(SUM(SwapUsedAvgMb * Samples) * 1.0 / SUM(Samples)) AS INTEGER),
  CAST(ROUND(SUM(DiskUsedAvgMb * Samples) * 1.0 / SUM(Samples)) AS INTEGER),
  CASE WHEN SUM(CASE WHEN Load1Avg IS NULL THEN 0 ELSE Samples END) = 0 THEN NULL
       ELSE CAST(ROUND(SUM(COALESCE(Load1Avg,0) * Samples) * 1.0 / SUM(CASE WHEN Load1Avg IS NULL THEN 0 ELSE Samples END)) AS INTEGER) END,
  MAX(Load1Max),
  CASE WHEN SUM(SpanSec) = 0 THEN 0 ELSE SUM(RxBytesDelta) / SUM(SpanSec) END, MAX(RxRateMaxBps),
  CASE WHEN SUM(SpanSec) = 0 THEN 0 ELSE SUM(TxBytesDelta) / SUM(SpanSec) END, MAX(TxRateMaxBps),
  SUM(RxBytesDelta), SUM(TxBytesDelta), SUM(Samples), SUM(SpanSec)
FROM Metrics1m
WHERE BucketStartUnix >= @start AND BucketStartUnix < @start + @len
GROUP BY NodeId
ON CONFLICT(NodeId, BucketStartUnix) DO UPDATE SET
  CpuAvg=excluded.CpuAvg, CpuMax=excluded.CpuMax, MemUsedAvgMb=excluded.MemUsedAvgMb, MemUsedMaxMb=excluded.MemUsedMaxMb,
  SwapUsedAvgMb=excluded.SwapUsedAvgMb, DiskUsedAvgMb=excluded.DiskUsedAvgMb, Load1Avg=excluded.Load1Avg, Load1Max=excluded.Load1Max,
  RxRateAvgBps=excluded.RxRateAvgBps, RxRateMaxBps=excluded.RxRateMaxBps, TxRateAvgBps=excluded.TxRateAvgBps, TxRateMaxBps=excluded.TxRateMaxBps,
  RxBytesDelta=excluded.RxBytesDelta, TxBytesDelta=excluded.TxBytesDelta, Samples=excluded.Samples, SpanSec=excluded.SpanSec;
```

1d 版本把源表换成 `Metrics1h`,目标 `Metrics1d`,`@len=86400`。**整段重算 + UPSERT** 使任务天然幂等:重复执行同一 `@start` 结果相同。

### 4.3 调度(`RollupService`,`PeriodicTimer(30s)` 轮询下面的“到点”条件,各任务记录 `LastRunUnix` 于 Settings `job.<name>.lastRun`)

| 任务 | 触发时刻 | 动作 |
|---|---|---|
| `rollup-1h` | 每小时 HH:01:30 | 对 `H-2` 与 `H-1` 两个小时桶各执行 §4.2(H 为当前整点) |
| `rollup-1d` | 每日 00:10:00 UTC | 对 `D-2`、`D-1` 两个 UTC 日桶执行 1d rollup |
| `retention` | 每小时 HH:07:00 | `DELETE FROM Metrics1m WHERE BucketStartUnix < now-25h`;同理 1h(8d)、1d(32d);`TrafficDaily` 400d(`LocalDate < date(now-400d)`);`AlertEvents` `Status=2 AND ResolvedUnix < now-{alert.retentionDays}`;`NotificationLogs` 30d;`RefreshTokens` 过期或已撤销超 7d;分批 `LIMIT 5000` 循环直到 changes()=0 |
| `maintenance` | 每日 04:30 UTC | `wal_checkpoint(TRUNCATE)`,`PRAGMA optimize`,`VACUUM INTO` 备份;可选 VACUUM |

同一任务在一个周期内只执行一次(比较 `LastRunUnix` 与本周期起点),所以进程重启不会重复,也不会漏(到点后的任何时刻都会补执行)。

### 4.4 启动补偿

`StartupInitializer` 最后一步:对 `[now-26h, now)` 内的每个整点执行 1h rollup;对 `[now-3d, now)` 内每个 UTC 日执行 1d rollup。全部幂等。目的:Master 停机期间漏掉的 rollup 用现存 1m/1h 数据补齐;停机期间没有 1m 数据的小时自然为空(图上断点),不伪造。

### 4.5 查询映射(供 API.md)

`range=24h` → Metrics1m `[now-24h, now)`;`7d` → Metrics1h `[now-7d, now)`;`30d` → Metrics1d `[now-30d, now)`。返回按 `BucketStartUnix` 升序;不补零、不插值(前端把缺桶渲染为断点)。

## 5. 流量 Delta 引擎与账单周期

### 5.1 输入与职责划分

- **探针侧**(定案:Agent 过滤 + 上报聚合值):对“计入网卡”集合求 `Σ rx_bytes`、`Σ tx_bytes`(内核累计计数器,64 位原始值,不做回绕修正),随心跳上报 `NetRxBytes/NetTxBytes`;注册时上报计入网卡名单 `Interfaces`。网卡过滤规则(Linux):**排除** 名称匹配 `lo, docker*, br-*, veth*, virbr*, vnet*, tun*, tap*, wg*, tailscale*, zt*, flannel*, cni*, cali*, kube-*, dummy*, ifb*, gre*, sit*, ip6tnl*, teql*, nlmon*` 或含 `.`/`@`(VLAN/macvlan 子接口)或为 bonding slave(存在 `/sys/class/net/<if>/bonding_slave`);**其余**中,保留有 `/sys/class/net/<if>/device` 符号链接(物理/virtio/xen)或名称匹配 `eth*, ens*, enp*, eno*, em*, venet*, wl*` 者。Windows:`GetIfTable2` 中 `OperStatus=Up` 且类型为 `IF_TYPE_ETHERNET_CSMACD(6)`/`IF_TYPE_IEEE80211(71)`,排除 `Loopback/Tunnel`,并排除 `Description` 含 `Virtual, VMware, Hyper-V, WSL, Bluetooth, TAP, WireGuard, Npcap, Loopback` 者。CLI `--nic-include/--nic-exclude`(glob,逗号分隔)在上述规则之后再调整。理由:主机上才有 sysfs/驱动信息;服务端每网卡聚合会把回绕/热插拔判定复杂度乘以网卡数,而聚合值配合下面的回绕规则已足够精确。
- **服务端**:对聚合值做 Delta、判定回绕/清零、归属账期与本地日、累计与限额告警。

### 5.2 Delta 判定(`TrafficAccumulator.Apply`;每个心跳调用;无 IO)

```text
输入: rxNew, txNew (ulong), uptimeSec (uint), nowMs, elapsedMs
状态: LastRx, LastTx (long?), LastUptimeSec (long?), LastSampleMs (long?)
常量: MAX_PLAUSIBLE_BPS = traffic.maxPlausibleGbps(默认 40) * 125_000_000   // 字节/秒
      U32 = 4_294_967_296

if LastSampleMs == null:                     // (E1) 首个样本(节点首次注册 / 数据被清空)
    baseline(); return (0, 0)                // 只建基线,不计费
gapSec = max(1, (nowMs - LastSampleMs) / 1000)   // 真实时间差(可能是断线数小时)
rebooted = uptimeSec + 5 < gapSec ? false : (uptimeSec < LastUptimeSec)   // 见下方说明
delta(new, last):
    if new >= last:                          // (E2) 正常单调递增(含探针重启、断线重连:内核计数器仍在,gap 内流量全部计入)
        d = new - last
    elif rebooted:                           // (E3) 机器重启,计数器从 0 重新累计:自开机以来的字节全部落在 gap 内 → 计入 new
        d = new
    elif last < U32 and new < U32 and (U32 - last + new) <= MAX_PLAUSIBLE_BPS * gapSec:
        d = U32 - last + new                 // (E4) 32 位计数器回绕(老内核/部分驱动)
    else:
        d = 0                                // (E5) 无法解释的回退(网卡被移除/重建、探针网卡过滤规则变更):重建基线,不计费,Warning 日志
    cap = MAX_PLAUSIBLE_BPS * gapSec
    if d > cap: d = 0; log Warning "implausible delta"   // (E6) 防御异常值
    return d
rxD = delta(rxNew, LastRx); txD = delta(txNew, LastTx)
attribute(rxD, txD, fromMs = LastSampleMs, toMs = nowMs)   // §5.3
LastRx = rxNew; LastTx = txNew; LastUptimeSec = uptimeSec; LastSampleMs = nowMs
return (rxD, txD)
```

重启判定:`uptimeSec < LastUptimeSec` 即为重启(uptime 单调,只有重启会变小)。第一行的 `uptimeSec + 5 < gapSec` 是补充:若 uptime 比 gap 还短,说明重启发生在 gap 内 → 也视为重启(处理探针首次心跳 uptime 与上次基线不可比的情况:例如 Master 长时间停机后节点已重启多次,以“开机时间落在 gap 内”为准)。两者取或:`rebooted = uptimeSec < LastUptimeSec || uptimeSec + 5 < gapSec`。

边界用例(测试必须覆盖):

| 用例 | 输入 | 期望 |
|---|---|---|
| 首包 | Last=null | 不计费,基线建立 |
| 常规 | last=100, new=160, gap 2s | +60 |
| 探针重启 | 探针停 10 分钟,计数器继续:last=100,new=5000,uptime 增大 | +4900(全部计入) |
| 主机重启 | last=100 GB,new=3 MB,uptime=120 | +3 MB |
| 主机重启 + 长断线 | gap=3 天,uptime=2 天,new=50 GB | +50 GB(rebooted 成立) |
| 32 位回绕 | last=4_294_000_000,new=1_000_000,gap 2s,速率上限内 | +1_967_296 |
| 网卡移除 | last=10 GB,new=4 GB,uptime 增大,值 > U32 | +0,重建基线 |
| 异常值 | 2s 内 +10 TB | +0(cap) |
| 重复/乱序 | 同一连接不可能;旧连接残留消息在 Hub 层丢弃(PROTOCOL.md §6) | 不进入引擎 |

### 5.3 归属(账期与本地日)

```text
attribute(rxD, txD, fromMs, toMs):
    if rxD == 0 and txD == 0: return
    period = CurrentPeriod(node)                       // §5.4,含 StartUnix/EndUnix
    if toMs/1000 >= period.EndUnix:                    // gap 跨越账期边界(长断线),按时间比例切分
        shareOld = (period.EndUnix*1000 - fromMs) / (toMs - fromMs)   // 0..1
        addToPeriod(period, floor(rxD*shareOld), floor(txD*shareOld))
        Rollover(node, period)                          // 关闭旧周期,创建新周期(§5.4)
        attribute(rxD - floor(rxD*shareOld), txD - floor(txD*shareOld), period.EndUnix*1000, toMs)   // 递归,余量进入新周期(可能再跨)
        return
    addToPeriod(period, rxD, txD)
    // 本地日:按 toMs 所在本地日归属;gap 跨日时同样按比例切分(实现同上,边界 = 下一本地零点)
    addToDaily(localDate(toMs, tz), ...)
```

内存累加器 `TrafficAccumulator.PendingMonthly[(periodStartUnix)] += (rx,tx)`、`PendingDaily[localDate] += (rx,tx)`,每分钟 flush 时 UPSERT(§5.5)并清零。**边界正好落在 2s 样本内的误差 ≤ 一个样本**,可接受。

### 5.4 账单周期计算(`BillingPeriod.Compute(nowUtc, resetDay, tz)`)

```text
local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz)              // tz = node.TimeZoneId ?? settings general.timeZone(默认 Asia/Shanghai)
anchor(y, m) = new DateTime(y, m, min(resetDay, DaysInMonth(y, m)), 0, 0, 0)   // 月底钳制:31 → 2 月为 28/29
a = anchor(local.Year, local.Month)
if local >= a: start = a;  end = anchor(next month of a)
else:          end = a;    start = anchor(previous month of a)
toUtc(dt): if tz.IsInvalidTime(dt) dt = dt.AddHours(1);           // DST 跳过的 00:00 → 01:00
           off = tz.GetUtcOffset(dt)  (模糊时间取标准时偏移)      ; return dt - off
return (StartUnix = toUtc(start), EndUnix = toUtc(end))
```

例:resetDay=31,tz=Asia/Shanghai:周期序列 `01-31 → 02-28 → 03-31 → 04-30 → 05-31`。resetDay=1:自然月。

**当前周期行的维护**:
- 启动与节点创建/编辑时:`EnsureCurrentPeriod(node)`:计算 `(start,end)`;若不存在 `IsClosed=0` 行 → 创建;若存在但 `PeriodStartUnix != start`(配置改了 resetDay/tz)→ 旧行 `IsClosed=1, ClosedReason='config', PeriodEndUnix=now` 并创建新行(**新周期从现在起算,不追溯**;UI 需提示)。
- 每分钟 flush 与每次 attribute:`now >= EndUnix` → `Rollover`:旧行 `IsClosed=1, ClosedReason='rollover'`,新行 `(end, nextEnd)`,`traffic` 告警状态归零(§6)。
- 手工重置(`POST /api/nodes/{id}/traffic/reset`):旧行 `IsClosed=1, ClosedReason='manual', PeriodEndUnix=now`;新行 `(now, 原 end)`(保持原重置日节律)。
- 修改 `TrafficLimitBytes/CountMode` 只更新当前行的快照列,不换周期。

**用量**:`counted = mode==Sum ? rx+tx : mode==Rx ? rx : mode==Tx ? tx : max(rx,tx)`;`pct = limit>0 ? counted*100/limit : null`。

### 5.5 Flush SQL

```sql
INSERT INTO TrafficMonthly (NodeId, PeriodStartUnix, PeriodEndUnix, RxBytes, TxBytes, LimitBytesSnapshot, ResetDaySnapshot, CountModeSnapshot, TimeZoneSnapshot, IsClosed, UpdatedUnix)
VALUES (@n, @s, @e, @rx, @tx, @lim, @rd, @cm, @tz, 0, @now)
ON CONFLICT(NodeId, PeriodStartUnix) DO UPDATE SET RxBytes = RxBytes + excluded.RxBytes, TxBytes = TxBytes + excluded.TxBytes, UpdatedUnix = excluded.UpdatedUnix;

INSERT INTO TrafficDaily (NodeId, LocalDate, RxBytes, TxBytes, UpdatedUnix) VALUES (@n, @d, @rx, @tx, @now)
ON CONFLICT(NodeId, LocalDate) DO UPDATE SET RxBytes = RxBytes + excluded.RxBytes, TxBytes = TxBytes + excluded.TxBytes, UpdatedUnix = excluded.UpdatedUnix;

INSERT INTO NodeRuntime (NodeId, LastRxBytes, LastTxBytes, LastUptimeSec, LastSampleUnix, LastSeenUnix, UpdatedUnix) VALUES (...)
ON CONFLICT(NodeId) DO UPDATE SET LastRxBytes=excluded.LastRxBytes, LastTxBytes=excluded.LastTxBytes, LastUptimeSec=excluded.LastUptimeSec, LastSampleUnix=excluded.LastSampleUnix, LastSeenUnix=excluded.LastSeenUnix, UpdatedUnix=excluded.UpdatedUnix;
```

### 5.6 崩溃一致性论证

内存中的 `Pending*` 增量与 `Last*` 基线在**同一事务**提交并同时清零/推进。崩溃时:数据库里的 `Last*` 对应的增量已全部入库;崩溃后新心跳与旧 `Last*` 做 Delta,重新得到崩溃前未入库的那部分增量(计数器单调),既不重复也不遗漏。唯一损失:崩溃前 ≤60s 的 `Metrics1m` 桶。

## 6. 告警引擎

### 6.1 规则(插件接口 `IAlertRule`)

```csharp
public interface IAlertRule
{
    string Key { get; }                    // "offline" | "cpu" | "mem" | "disk" | "traffic" | "expiry"
    string DisplayName { get; }            // 中文:离线超时 / CPU 高负载 / 内存高占用 / 磁盘空间不足 / 流量超标 / 即将到期
    AlertSeverity Severity { get; }        // offline=Critical, traffic=Warning(≥100% 时 Critical), expiry=Warning(已过期 Critical), cpu/mem/disk=Warning
    int ConsecutiveHits(EffectiveThresholds t);   // 进入 Firing 需要的连续命中次数
    int ConsecutiveOks(EffectiveThresholds t);    // 回到 Normal 需要的连续未命中次数
    RuleVerdict Evaluate(NodeEvalContext ctx);    // Hit(value, threshold, text) | Ok(value) | Skip(不改变状态)
}
```

评估周期:`AlertEvaluationService` 每 **10s** 对每个 `Enabled` 节点跑全部规则(expiry 除外,见下)。

| Key | 命中条件(有效阈值 t 见 §6.2) | 值 | Hits | Oks | 说明 |
|---|---|---|---|---|---|
| offline | `RegisteredAt != null && now - LastSeen > t.offlineSec` | 失联秒数 | 1 | 1 | 时间窗本身就是防抖;恢复条件 = 收到心跳(`LastSeen` 更新) |
| cpu | 在线 && 最近 60s 波浪图均值 ≥ `t.cpuPercent*10`(‰) | 均值 % | `ceil(t.cpuSustainSec/10)`(默认 300s → 30) | 6(60s) | 离线时 Skip(保持状态) |
| mem | 在线 && `memUsed/memTotal*100 ≥ t.memPercent` | % | 6 | 6 | 默认 **禁用**(`alert.memEnabled=false`) |
| disk | 在线 && 任一挂载点 `used/total*100 ≥ t.diskPercent`(取最大) | % | 1 | 1 | 数据来自 NodeDisks(60s 更新) |
| traffic | `limit>0 && pct ≥ t.trafficPercent` | pct | 1 | 1 | 账期 Rollover 时强制 Ok;`pct ≥ 100` 时 Severity=Critical 且标题 “流量已耗尽” |
| expiry | `ExpiresAt != null && (ExpiresAt.LocalDate - today(tz)).Days < t.expiryDays` | 剩余天数(可负) | 1 | 1 | **每日 09:00(general.timeZone)评估一次 + 节点保存后立即评估**;Firing 期间每 24h 重新通知(不受冷却限制);已过期(天数<0)Severity=Critical |

### 6.2 有效阈值

全局键(§7,`alert.*`)被节点 `AlertOverridesJson` 覆盖(部分键即可):`{"offlineSec":60,"cpuPercent":95,"cpuSustainSec":600,"memPercent":95,"diskPercent":95,"trafficPercent":90,"expiryDays":14,"disabledRules":["mem","disk"]}`。`disabledRules` 中的规则对该节点直接 Skip 且状态归 Normal。`cooldownMin/consecutive` 只允许全局。

### 6.3 状态机(每个 `(NodeId, RuleKey)`,持久化于 AlertStates)

```text
状态: Normal(0) Pending(1) Firing(2) Recovering(3)
每次 Evaluate:
  Skip → 不变
  Hit:
    Normal     → hits=1; if hits>=N → fire() else Pending
    Pending    → hits++; if hits>=N → fire()
    Firing     → 不变(更新 event.Value 为最新值)
    Recovering → oks=0; Firing; event.Occurrences++ (同一事件继续)
  Ok:
    Normal     → 不变
    Pending    → hits=0; Normal
    Firing     → oks=1; if oks>=M → resolve() else Recovering
    Recovering → oks++; if oks>=M → resolve()
fire():
  state=Firing; hits=0; LastFiredUnix=now
  event = new AlertEvent{Status=Firing, FiredUnix=now, Value, Threshold, Title, Message, Occurrences=1}; OpenEventId=event.Id
  cooldownSec = clamp(alert.cooldownMin,30,60)*60
  if LastNotifiedUnix == null || now - LastNotifiedUnix >= cooldownSec:
       event.Notified=true; LastNotifiedUnix=now; enqueue(Notification.Firing(event))
  else: event.Notified=false                       // 冷却期内:记录但不发
  broadcast admin hub "alert"(event)
resolve():
  state=Normal; hits=oks=0
  event.Status=Resolved; event.ResolvedUnix=now; OpenEventId=null
  if event.Notified: event.RecoveryNotified=true; enqueue(Notification.Resolved(event))   // 只有发过触发通知才发恢复
  broadcast admin hub "alert"(event)
节点被禁用/删除: 所有 Firing/Recovering 事件 Resolved(不通知),状态清空
Master 启动: 从 AlertStates 恢复状态与 LastNotifiedUnix(冷却跨重启生效);对 OpenEventId 指向的事件继续跟踪
```

冷却语义(对应 PRD “30~60 分钟静默冷却期”):同一 `(节点,规则)` 两次**触发通知**间隔 ≥ 冷却时间;冷却内的再次触发只记事件不发通知,其恢复也不发(避免抖动刷屏)。`expiry` 规则例外:处于 Firing 时每 24h 重发一次(`LastNotifiedUnix + 86400 <= now`)。

### 6.4 通知内容

标题/正文模板(中文,Telegram 用 HTML parse_mode;Webhook 用 JSON):

```text
触发: 🔴 [告警] {PublicName} {RuleDisplayName}
      规则:{规则描述,如 离线超时 (>30s)}
      当前值:{value 文案,如 已失联 45 秒 / CPU 均值 96% 持续 5 分钟 / 已用 82% (410 GB / 500 GB) / 剩余 5 天 (2026-09-12)}
      节点:{PublicName}{AdminRemark 非空时追加 " · " + AdminRemark}
      时间:{yyyy-MM-dd HH:mm:ss} ({tz})
恢复: 🟢 [恢复] {PublicName} {RuleDisplayName}已恢复
      持续:{FiredUnix→ResolvedUnix 的时长,如 3 分 20 秒}
      时间:...
测试: 🔔 [测试] Server Node Monitor 通知渠道测试成功({ChannelName})
```

Webhook 默认 JSON 载荷(`Content-Type: application/json; charset=utf-8`):

```json
{"event":"alert.firing","eventId":123,"nodeId":1,"publicName":"HK-Node-01","adminRemark":"核心 DB-勿动",
 "rule":"offline","ruleName":"离线超时","severity":"critical","status":"firing","value":45,"threshold":30,
 "title":"HK-Node-01 离线超时","message":"已失联 45 秒","firedAt":"2026-09-07T02:15:30Z","resolvedAt":null,
 "durationSec":null,"master":"https://m.example.com","time":"2026-09-07T02:15:30Z"}
```

`event` ∈ `alert.firing | alert.resolved | test`;`severity` ∈ `info|warning|critical`。请求头:`User-Agent: SNM-Master/{version}`、`X-SNM-Event`、`X-SNM-Timestamp`(Unix 秒)、配置了 `secret` 时 `X-SNM-Signature: sha256=<hex(HMAC-SHA256(secret, "{timestamp}.{body}"))>`。自定义模板(`bodyTemplate`)支持占位符 `{{title}} {{message}} {{event}} {{publicName}} {{adminRemark}} {{rule}} {{ruleName}} {{severity}} {{status}} {{value}} {{threshold}} {{firedAt}} {{resolvedAt}} {{durationSec}} {{nodeId}} {{master}}`,值做 JSON 字符串转义(`{{{x}}}` 三括号不转义)。

### 6.5 发送(`NotificationDispatcher`)

`Channel<NotificationJob>`(有界 1000,满则丢最旧并计数);消费者串行;每渠道:超时 10s;失败重试 3 次(间隔 5s/30s/120s);每次尝试写 `NotificationLogs`。Telegram:`POST https://api.telegram.org/bot{token}/sendMessage` `{"chat_id":..,"text":..,"parse_mode":"HTML","disable_web_page_preview":true,"message_thread_id":可选}`;出站代理 `Snm:Notify:HttpProxy`(appsettings/环境变量,非 DB)。同一事件对所有 `Enabled && MinSeverity <= event.Severity` 的渠道发送。

### 6.6 渠道 ConfigJson

| Type | 字段 | 必填 | 说明 |
|---|---|---|---|
| Telegram(1) | `botToken` | 是 | `123456:ABC...`;API 返回时脱敏为 `123456:****` |
| | `chatId` | 是 | 字符串(可负数、可 `@channel`) |
| | `threadId` | 否 | 话题 ID |
| Webhook(2) | `url` | 是 | http/https |
| | `method` | 否 | 默认 `POST`;允许 `POST/PUT` |
| | `headers` | 否 | 对象 `{"Authorization":"Bearer x"}`;返回时值脱敏为 `****` |
| | `secret` | 否 | HMAC 密钥,返回脱敏 |
| | `bodyTemplate` | 否 | 空 = 默认 JSON |
| | `contentType` | 否 | 默认 `application/json; charset=utf-8` |

保存(PATCH)时字段值为 `****` 表示“保持原值”。

## 7. 设置键(`Settings`)

类型:`int` `bool` `string` `json` `decimal`。未存储 → 默认值。所有键通过 `ISettingsService`(内存缓存 + 写穿;修改后发布 `SettingsChanged` 事件供告警/推送/探针配置下发使用)。

| 键 | 类型 | 默认 | 范围/说明 |
|---|---|---|---|
| general.siteTitle | string | `Server Node Monitor` | 后台标题 |
| general.publicTitle | string | `节点状态` | 大屏标题 |
| general.timeZone | string | `Asia/Shanghai` | IANA;节点未设 tz 时使用;告警时间显示 |
| general.masterPublicUrl | string | 空 | 如 `https://m.example.com`;安装命令与 Webhook `master` 字段;空 → 用请求的 Host |
| agent.heartbeatSec | int | 2 | 1..60,下发给探针 |
| agent.ipReportSec | int | 300 | 60..3600 |
| agent.diskReportSec | int | 60 | 30..3600 |
| alert.offlineSec | int | 30 | 10..3600 |
| alert.cpuPercent | int | 90 | 50..100 |
| alert.cpuSustainSec | int | 300 | 30..3600 |
| alert.memEnabled | bool | false | |
| alert.memPercent | int | 90 | |
| alert.diskEnabled | bool | true | |
| alert.diskPercent | int | 90 | |
| alert.trafficPercent | int | 80 | 1..100 |
| alert.expiryDays | int | 7 | 1..90 |
| alert.expiryCheckHour | int | 9 | 0..23,本地小时 |
| alert.cooldownMin | int | 30 | **30..60**(钳制) |
| alert.retentionDays | int | 90 | 事件保留 |
| traffic.defaultResetDay | int | 1 | 新建节点默认 |
| traffic.maxPlausibleGbps | int | 40 | Delta 上限 |
| public.enabled | bool | true | 关闭后 `/` 返回 404,`/hubs/public` 拒绝 |
| public.showTraffic | bool | true | 大屏显示流量百分比 |
| public.showUptime | bool | true | |
| public.showDisk | bool | true | |
| install.releaseBaseUrl | string | `https://github.com/OWNER/REPO/releases/latest/download` | 安装脚本下载基地址(实现者填真实仓库) |
| install.agentVersion | string | `latest` | 固定版本时填 tag,如 `v1.2.0`(基地址随之变为 `.../releases/download/v1.2.0`) |
| finance.baseCurrency | string | `CNY` | MRR 汇总币种 |
| finance.fxRates | json | `{"USD":7.2,"EUR":7.8,"CNY":1}` | 1 单位外币 = x 基准币 |
| maintenance.vacuumWeekly | bool | false | |
| geoip.lastUpdatedUnix | int | 0 | 系统写 |
| geoip.ipv4Rows / geoip.ipv6Rows | int | 0 | 系统写 |
| job.rollup-1h.lastRun 等 | int | 0 | 系统写(§4.3) |

## 8. 管理员与令牌

- 登录:`Username` 精确匹配 → PBKDF2 校验 → 签发 JWT(HS256,`Snm:Jwt:Secret`;claims:`sub`=Id,`name`,`tv`=TokenVersion,`jti`,`exp`=now+`Snm:Jwt:AccessTokenMinutes`(120),`iss=snm`,`aud=snm-admin`)+ 刷新令牌(明文返回一次,DB 存 SHA-256)。
- 刷新:校验哈希存在、未撤销、未过期 → 撤销旧行(`RevokedAt`,`ReplacedByHash`)→ 新 access + 新 refresh。已撤销令牌再次出现 → 撤销该用户全部刷新令牌(重放防御)。
- 改密:`TokenVersion++`(所有 access 立即失效)+ 撤销全部刷新令牌。
- 登录限流:`/api/auth/login` 每 IP 每分钟 10 次(`AddRateLimiter` 固定窗口,429)。
- Jwt Secret:未配置时首次启动生成 64 字节随机写入 `{DataDir}/jwt.key`(0600)并复用。

## 9. GeoIP

- 数据:`asn-country-ipv4-num.csv`(`start,end,CC`,32 位十进制)与 `asn-country-ipv6-num.csv`(128 位十进制),下载到 `{DataDir}/geoip/`,先写 `.tmp` 再原子改名;记录 `geoip.lastUpdatedUnix`。
- 内存结构:`uint[] v4Start, v4End; string[] v4Cc`(按 start 升序,文件已排序,加载时校验并排序);v6 用 `UInt128[]`。查找:`Array.BinarySearch(start, ip)` → 若负取 `~idx-1` → 校验 `ip <= end[idx]` → cc,否则 null。IPv4-mapped IPv6(`::ffff:a.b.c.d`)先转 v4。私网/保留地址直接返回 null。
- 刷新:`GeoIpUpdateService` 启动 5s 后检查;文件缺失或 `lastUpdated` 距今 ≥ `Snm:GeoIp:RefreshDays`(7)→ 下载;之后每小时检查一次;下载失败只记 Warning,`CountryCodeAuto` 保持旧值(首次无数据则为空)。刷新成功后对所有节点重算 `CountryCodeAuto`。
- 覆盖:`CountryCodeOverride` 由 `PATCH /api/nodes/{id}` 设置(`null` 清除)。生效值 `Override ?? Auto` 写入 Hub 快照。

## 10. 数据量与索引评估(100 节点)

Metrics1m 144k 行 × ~90 B ≈ 13 MB(含索引 ~20 MB);1h/1d 可忽略;TrafficDaily 40k 行 ≈ 3 MB;整体 `.db` 稳定在 30~50 MB 量级,每分钟一次写事务(≤100 行 + 若干 UPSERT),WAL 每日 checkpoint,查询 24h 图表按主键范围扫描 ≤1440 行。1000 节点时 Metrics1m ≈ 1.44M 行/25h ≈ 200 MB,仍可用;超出则建议调低 `Snm:Retention:Metrics1mHours` 或改用外部 DB(非本期目标)。

## 11. 单元/集成测试用例(tests/SNM.Master.Tests)

| 类别 | 用例 |
|---|---|
| Delta | §5.2 表中 9 个用例;跨账期切分比例;跨本地日切分;maxPlausible 边界 |
| 账期 | resetDay 1/15/31 × 月份 1/2/12 × tz Asia/Shanghai、UTC、America/New_York(含 DST 3 月/11 月);月底钳制;`EnsureCurrentPeriod` 配置变更换周期;Rollover 连续两次 |
| 聚合 | 30 个心跳 → 1 桶(CpuAvg/Max/Span);跨分钟切桶;节点停止后桶被 flush 扫描关闭;冲突 UPSERT 加权合并 |
| Rollup | 60 个 1m → 1h 值正确(加权);缺失分钟;Load1 全 NULL → NULL;重复执行幂等;启动补偿不改已完整桶 |
| Retention | 各表边界(`<` 严格)分批删除 |
| 告警 | 每条规则的 Hit/Ok 序列 → 状态迁移;N/M 边界;冷却内再触发不通知、其恢复不通知;expiry 24h 重发;节点禁用清空;重启恢复 LastNotified |
| 通知 | Telegram/Webhook 载荷格式;HMAC 签名可验证;模板占位符;重试计数与日志 |
| GeoIP | 边界 ip=start/end;不在任何区间;v4-mapped v6;文件缺失 |
| 设置 | 默认值、范围钳制、`****` 保留语义 |
| 集成 | `WebApplicationFactory` + 临时 SQLite 文件:登录→创建节点→模拟探针(真实 SignalR 客户端 + 官方 MessagePack)注册/心跳→`/api/nodes/{id}/metrics` 有 1m 数据(测试内触发 flush)→停止心跳 35s 后 `AlertEvents` 出现 offline 且本地 Webhook 接收器收到载荷→恢复心跳收到 resolved |

