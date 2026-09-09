# DATA.md — 数据模型、时序降采样、流量与账单、告警、设置(候选设计 C)

> 存储引擎:SQLite(WAL)+ EF Core 10(`Microsoft.EntityFrameworkCore.Sqlite` 10.0.11)。`DbContext` 名为 `SnmDbContext`,位于 `src/SNM.Master/Data/`。所有时间戳列以 **Unix 毫秒 UTC(`long`)** 存储(列名以 `Ms` 结尾),日期列以 `yyyyMMdd` 整数(`int`,列名以 `Date` 结尾,节点本地时区)存储;金额用 `decimal`(SQLite 存 TEXT,聚合在内存完成)。JSON 列为 TEXT,由 `System.Text.Json` 源生成上下文 `SnmJsonContext` 序列化(Master 非 AOT,但统一走源生成便于未来裁剪)。

---

## 0. 决策摘要(对应 BRIEF §3 第 3、4、5 题)

| 问题 | 结论 |
|---|---|
| Q3 数据模型与降采样 | 3 张同构时序表 `Metrics1m/Metrics1h/Metrics1d`,主键 `(NodeId, TsMs)`,均值 + 峰值 + 样本数 + 区间流量增量。心跳 2 s **不落库**,`MinuteAggregator` 在内存累加,`MinuteFlusher` 每分钟 +3 s 批量写入已关闭的分钟桶。`HourlyRollup` 在每小时 02 分把上一小时的 1m 行加权聚合成 1h 行;`DailyRollup` 在 UTC 00:10 把昨天的 1h 行聚合成 1d 行;`RetentionSweeper` 每小时 05 分删除 1m>25h、1h>8d、1d>31d。所有写入为 `INSERT … ON CONFLICT DO UPDATE`(幂等),`RollupState` 表记录已完成水位,重启补跑。调度统一用 `BackgroundService + PeriodicTimer`(§5.6)。 |
| Q4 流量 Delta | Agent 过滤虚拟网卡后上报**聚合累计字节**(见 PROTOCOL §5.3);Master 以持久化锚点(`NodeTrafficState`)做差,计数器下降或 BootId 变化判定为清零(取当前值为增量,并用 `UptimeSec × 100 Gbps` 做可信上限),增量按收到时刻的**节点本地日期**记入 `TrafficDaily`,按**账单周期**记入 `TrafficMonthly`。账单周期 = 重置日 1–31,月底钳制(`min(day, DaysInMonth)`),时区 = 节点 `TimeZone` ?? 全局 `general.timeZone`(默认 `Asia/Shanghai`)。内存累加、每 60 s 与锚点同事务落盘,Master 重启不丢不重。 |
| Q5 告警 | 6 条规则:`Offline`(>30 s)、`CpuHigh`(≥90% 持续 300 s)、`MemHigh`(≥90% 持续 300 s)、`DiskHigh`(任一挂载 ≥90%)、`TrafficHigh`(周期用量 ≥80%)、`Expiry`(≤7 天或已过期)。全局阈值存 `Settings.alert`,节点可覆盖(可空列)。状态机 `Ok → Pending → Firing → Ok` 持久化在 `AlertStates`,事件写 `AlertEvents`;去重键 `(NodeId, Rule)` 同时只允许一条未恢复事件;冷却 30 分钟(可配 30–60),冷却期内的再次触发记录事件但 `Suppressed=true` 不外发;恢复发 🟢 通知;`Offline`/`Expiry` 持续中每 24 h 重复提醒。渠道 `NotificationChannels`(Telegram/Webhook)存 DB,改动即时生效,支持测试消息。 |

---

## 1. 存储与连接

| 项 | 值 |
|---|---|
| 数据库文件 | `{SNM_DATA_DIR}/snm.db`(默认 `./data/snm.db`),另有 `snm.db-wal`、`snm.db-shm` |
| 连接串 | `Data Source={path};Cache=Shared;Pooling=True;Default Timeout=5` |
| 连接打开时执行(`DbConnectionInterceptor.ConnectionOpened`) | `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-20000;` |
| 建库时一次性 | `PRAGMA auto_vacuum=INCREMENTAL;`(在首个迁移的 `migrationBuilder.Sql` 中,且必须在任何表创建之前执行) |
| 写入纪律 | 后台服务的批量写共享一个 `SqliteWriteGate`(`SemaphoreSlim(1,1)`),每批一个显式事务;REST 写直接走 `DbContext`,依赖 `busy_timeout` |
| 维护 | 每日 UTC 04:00:`PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum(2000); PRAGMA optimize;` |
| 迁移 | EF Core Migrations 代码提交在 `src/SNM.Master/Data/Migrations/`;启动时 `await db.Database.MigrateAsync()`(在任何 BackgroundService 之前,见 DESIGN §5);**禁止** `EnsureCreated`。开发期生成:`dotnet ef migrations add <Name> -p src/SNM.Master -o Data/Migrations`(需 `Microsoft.EntityFrameworkCore.Design` 10.0.11 作 PrivateAssets)。测试用临时文件 + `MigrateAsync`。 |
| 模型约定 | 表名 = 实体复数名(`Nodes`、`AlertEvents`…);`string` 列默认 `TEXT` 无长度限制,业务层截断;所有 `bool` 存 INTEGER 0/1 |

---

## 2. 实体清单

### 2.1 `AdminUsers`

| 列 | 类型 | 可空 | 默认 | 说明 |
|---|---|---|---|---|
| `Id` | INTEGER PK autoincrement | 否 | | |
| `Username` | TEXT COLLATE NOCASE | 否 | | 唯一索引 `UX_AdminUsers_Username`;3–32 字符 |
| `PasswordHash` | TEXT | 否 | | `PasswordHasher<AdminUser>`(PBKDF2-HMAC-SHA512,100k 次,V3 格式) |
| `NickName` | TEXT | 是 | | |
| `Avatar` | TEXT | 是 | | URL |
| `Email` | TEXT | 是 | | |
| `CreatedMs` / `UpdatedMs` | INTEGER | 否 | | |
| `LastLoginMs` | INTEGER | 是 | | |
| `LastLoginIp` | TEXT | 是 | | |

单管理员:首次启动若表为空,用 `SNM_ADMIN_USER`/`SNM_ADMIN_PASSWORD` 创建;二者缺失时用户名 `admin`、随机 16 字符密码,以 Warning 级日志打印一次 `Initial admin password: …`。

### 2.2 `RefreshTokens`

| 列 | 类型 | 可空 | 说明 |
|---|---|---|---|
| `Id` | INTEGER PK | 否 | |
| `UserId` | INTEGER FK→AdminUsers | 否 | 级联删除 |
| `TokenHash` | TEXT | 否 | 唯一索引;SHA-256(Base64Url) of 32 随机字节 token |
| `ExpiresMs` | INTEGER | 否 | 签发 + `Jwt:RefreshTokenDays`(30) |
| `CreatedMs` | INTEGER | 否 | |
| `RevokedMs` | INTEGER | 是 | 登出/轮换/改密时置值 |
| `ReplacedByHash` | TEXT | 是 | 轮换链 |
| `UserAgent` / `Ip` | TEXT | 是 | 审计 |

清理:`RetentionSweeper` 删除 `ExpiresMs < now − 7d` 或 `RevokedMs < now − 7d` 的行。改密 → 撤销该用户全部 refresh token。

### 2.3 `Nodes`(核心)

| 列 | 类型 | 可空 | 默认 | 说明 |
|---|---|---|---|---|
| `Id` | INTEGER PK | 否 | | REST/Hub 使用的节点 Id |
| `Name` | TEXT | 否 | | 内部名称,1–64 字符,唯一索引 `UX_Nodes_Name`(NOCASE) |
| `PublicName` | TEXT | 否 | =Name | 大屏展示名,1–64 |
| `AdminRemark` | TEXT | 是 | | 私密运维备注 ≤ 1024 |
| `GroupName` | TEXT | 是 | | 分组标签 ≤ 32 |
| `Enabled` | INTEGER(bool) | 否 | 1 | 禁用 → Agent 401、不采集、不告警 |
| `IsPublic` | INTEGER(bool) | 否 | 1 | 是否出现在公开大屏 |
| `SortOrder` | INTEGER | 否 | 0 | 大屏/列表排序 |
| `AgentKey` | TEXT | 否 | | 43 字符 Base64Url,唯一索引 `UX_Nodes_AgentKey` |
| `InstallToken` | TEXT | 否 | | 43 字符 Base64Url,唯一索引 `UX_Nodes_InstallToken`;安装脚本 URL 用 |
| `IntervalMs` | INTEGER | 否 | 2000 | 1000–60000 |
| `IpReportIntervalSec` | INTEGER | 否 | 300 | 60–3600 |
| `CountryCodeAuto` | TEXT | 是 | | GeoIP 结果,2 字符大写 |
| `CountryCodeOverride` | TEXT | 是 | | 管理员覆盖;有效值 = Override ?? Auto |
| `TimeZone` | TEXT | 是 | | IANA id;空 → 全局 |
| `TrafficLimitGb` | INTEGER | 否 | 0 | 0 = 不限;单位 GB(10^9 字节,与商家一致;UI 标注) |
| `TrafficResetDay` | INTEGER | 否 | 1 | 1–31 |
| `TrafficMode` | INTEGER | 否 | 0 | `0`=Rx+Tx `1`=Tx only `2`=Rx only `3`=max(Rx,Tx) |
| `Provider` | TEXT | 是 | | 供应商 ≤ 64 |
| `ProviderUrl` | TEXT | 是 | | |
| `Price` | TEXT(decimal) | 是 | | ≥ 0 |
| `Currency` | TEXT | 否 | 'USD' | `USD`/`CNY`/`EUR`(白名单,可在 `general.currencies` 扩展) |
| `BillingCycle` | INTEGER | 否 | 1 | `0`=OneTime `1`=Monthly `2`=Quarterly `3`=SemiAnnual `4`=Yearly `5`=Biennial `6`=Triennial |
| `ExpiresDate` | INTEGER(yyyyMMdd) | 是 | | 到期日 |
| `AutoRenew` | INTEGER(bool) | 否 | 0 | |
| `BillingNote` | TEXT | 是 | | |
| `AlertsEnabled` | INTEGER(bool) | 否 | 1 | |
| `OfflineSecondsOverride` | INTEGER | 是 | | 节点级阈值覆盖,空 = 全局 |
| `CpuHighPctOverride` / `MemHighPctOverride` / `DiskHighPctOverride` / `TrafficHighPctOverride` / `ExpiryDaysOverride` | INTEGER | 是 | | 同上 |
| `Hostname` / `Os` / `Arch` / `CpuModel` / `AgentVersion` / `BootId` | TEXT | 是 | | 来自 `Hello` |
| `OsKind` | INTEGER | 否 | 0 | |
| `CpuCores` | INTEGER | 否 | 0 | |
| `MemTotalMb` / `SwapTotalMb` | INTEGER | 否 | 0 | |
| `DisksJson` | TEXT | 否 | '[]' | `DiskInfo[]` |
| `NetInterfacesJson` | TEXT | 否 | '[]' | `string[]` |
| `IpsJson` | TEXT | 否 | '[]' | `IpEntry[]`(§7) |
| `LastRemoteIp` | TEXT | 是 | | 最近一次连接的服务端观测 IP |
| `LastSeenMs` | INTEGER | 是 | | 最近心跳(每 60 s 由 `TrafficStateFlusher` 顺带落盘,并在断开时落盘) |
| `LastHelloMs` | INTEGER | 是 | | |
| `CreatedMs` / `UpdatedMs` | INTEGER | 否 | | |

索引:`IX_Nodes_SortOrder (SortOrder, Id)`、`IX_Nodes_ExpiresDate (ExpiresDate)`。

### 2.4 `NodeTrafficState`(流量锚点,一行/节点)

| 列 | 类型 | 可空 | 说明 |
|---|---|---|---|
| `NodeId` | INTEGER PK FK→Nodes(级联删除) | 否 | |
| `LastRxBytes` / `LastTxBytes` | INTEGER(ulong 存为 long,值 < 2^63) | 是 | 锚点计数器;空 = 无锚点 |
| `LastSampleMs` | INTEGER | 是 | 锚点对应的收到时刻 |
| `BootId` | TEXT | 是 | 锚点对应的 BootId |
| `UpdatedMs` | INTEGER | 否 | |

### 2.5 `Metrics1m` / `Metrics1h` / `Metrics1d`(同构)

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` | INTEGER FK→Nodes(级联删除) | PK 第一列 |
| `TsMs` | INTEGER | 桶起点 Unix 毫秒 UTC(1m:整分;1h:整点;1d:UTC 零点)PK 第二列 |
| `Samples` | INTEGER | 1m:心跳条数;1h/1d:下级样本数之和 |
| `CpuAvg` / `CpuMax` | INTEGER | ‰ |
| `Load1Avg` | INTEGER | ×100(N/A 样本不计入) |
| `MemUsedAvgMb` / `MemUsedMaxMb` | INTEGER | MiB |
| `SwapUsedAvgMb` | INTEGER | |
| `DiskUsedAvgMb` | INTEGER | 所有挂载已用之和 |
| `RxBpsAvg` / `RxBpsMax` / `TxBpsAvg` / `TxBpsMax` | INTEGER | B/s |
| `RxBytes` / `TxBytes` | INTEGER | 该桶内的流量增量(Delta 之和) |
| `OnlineSec` | INTEGER | 该桶内被判定在线的秒数(1m:`Samples × interval` 钳制 60;上卷相加)→ 可算可用率 |

主键 `(NodeId, TsMs)`;索引 `IX_Metrics1m_TsMs (TsMs)`(供保留删除),1h/1d 同。行大小约 90 字节;100 节点 24 h 的 1m 行 ≈ 144k 行 ≈ 15 MB。

### 2.6 `RollupState`

| 列 | 类型 | 说明 |
|---|---|---|
| `Key` | TEXT PK | `rollup.1h.watermark`、`rollup.1d.watermark`、`retention.lastRunMs`、`geoip.lastRefreshMs` |
| `ValueMs` | INTEGER | 已完成的桶起点(含) |
| `UpdatedMs` | INTEGER | |

### 2.7 `TrafficDaily`

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` | INTEGER FK | PK1 |
| `LocalDate` | INTEGER(yyyyMMdd) | 节点本地日期 PK2 |
| `RxBytes` / `TxBytes` | INTEGER | 当日增量和 |
| `UpdatedMs` | INTEGER | |

### 2.8 `TrafficMonthly`(账单周期)

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` | INTEGER FK | PK1 |
| `CycleStartDate` | INTEGER(yyyyMMdd) | 周期起点(含)PK2 |
| `CycleEndDate` | INTEGER(yyyyMMdd) | 周期终点(不含)= 下个周期起点 |
| `RxBytes` / `TxBytes` | INTEGER | |
| `LimitBytes` | INTEGER | 周期结束时冻结的限额快照(周期进行中 = 当前设置) |
| `UpdatedMs` | INTEGER | |

### 2.9 `AlertStates`(状态机,一行/节点/规则)

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` | INTEGER FK | PK1 |
| `Rule` | TEXT | PK2:`Offline`/`CpuHigh`/`MemHigh`/`DiskHigh`/`TrafficHigh`/`Expiry` |
| `State` | INTEGER | `0`=Ok `1`=Pending `2`=Firing |
| `FirstBreachMs` | INTEGER 可空 | 进入 Pending 的时刻 |
| `FirstClearMs` | INTEGER 可空 | Firing 中首次观察到恢复条件的时刻(用于恢复滞回) |
| `OpenEventId` | INTEGER 可空 | 当前未恢复的 `AlertEvents.Id` |
| `CooldownUntilMs` | INTEGER 可空 | 冷却截止 |
| `LastNotifiedMs` | INTEGER 可空 | 上次外发时刻(重复提醒依据) |
| `LastEvalMs` | INTEGER | |
| `LastValue` | REAL | 最近评估值(调试/展示) |

### 2.10 `AlertEvents`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | INTEGER PK | |
| `NodeId` | INTEGER FK(级联删除) | |
| `NodeName` | TEXT | 快照(节点改名后历史可读) |
| `Rule` | TEXT | |
| `Level` | TEXT | `info`/`warning`/`critical` |
| `Status` | TEXT | `firing`/`resolved` |
| `Title` / `Message` | TEXT | 中文 |
| `Value` / `Threshold` | REAL | 触发值/阈值(单位随规则:秒、%、天) |
| `StartedMs` | INTEGER | |
| `ResolvedMs` | INTEGER 可空 | |
| `NotifiedMs` | INTEGER 可空 | 触发通知成功时刻 |
| `RecoveryNotifiedMs` | INTEGER 可空 | |
| `NotifyError` | TEXT 可空 | 最后一次发送失败原因 |
| `Suppressed` | INTEGER(bool) | 因冷却未外发 |
| `AckedMs` | INTEGER 可空 | 管理员确认 |

索引:`IX_AlertEvents_StartedMs (StartedMs DESC)`、`IX_AlertEvents_Node (NodeId, StartedMs DESC)`、`IX_AlertEvents_Open (Status) WHERE Status='firing'`(过滤索引)。

### 2.11 `NotificationChannels`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | INTEGER PK | |
| `Name` | TEXT | 1–64 |
| `Type` | TEXT | `telegram` / `webhook` |
| `Enabled` | INTEGER(bool) | |
| `MinLevel` | TEXT | `info`/`warning`/`critical`,低于不发 |
| `RulesJson` | TEXT | `string[]`,空数组 = 全部规则 |
| `ConfigJson` | TEXT | 见下 |
| `CreatedMs` / `UpdatedMs` | INTEGER | |
| `LastTestMs` / `LastSentMs` | INTEGER 可空 | |
| `LastError` | TEXT 可空 | |

`ConfigJson`:
- telegram:`{"botToken":"123456:ABC…","chatId":"-1001234567890","parseMode":"HTML","disableNotification":false,"apiBase":"https://api.telegram.org"}`
- webhook:`{"url":"https://…","method":"POST","secret":"","headers":{"X-Token":"…"},"timeoutSec":10,"insecureSkipTlsVerify":false}`

### 2.12 `Settings`

| 列 | 类型 | 说明 |
|---|---|---|
| `Key` | TEXT PK | 组名:`general`/`agent`/`alert`/`geoip`/`retention`/`geoip.status`/`security` |
| `ValueJson` | TEXT | 组对象 |
| `UpdatedMs` | INTEGER | |

默认值(缺行即默认;`SettingsService` 启动时把缺失组写入):

```json
{
  "general":   { "siteName": "Server Node Monitor", "publicTitle": "节点状态", "publicBaseUrl": "",
                 "timeZone": "Asia/Shanghai", "currencies": ["USD", "CNY", "EUR"], "defaultCurrency": "USD",
                 "publicDashboardEnabled": true, "publicShowOffline": true },
  "agent":     { "intervalMs": 2000, "ipReportIntervalSec": 300,
                 "releaseBaseUrl": "https://github.com/OWNER/server-node-monitor/releases/latest/download",
                 "agentVersionPin": "" },
  "alert":     { "offlineSeconds": 30, "cpuHighPct": 90, "cpuSustainSec": 300, "memHighPct": 90, "memSustainSec": 300,
                 "diskHighPct": 90, "trafficHighPct": 80, "expiryDays": 7, "cooldownMinutes": 30, "renotifyHours": 24,
                 "recoverHysteresisPct": 5, "recoverSustainSec": 60, "expiryCheckTime": "09:00",
                 "rulesEnabled": { "Offline": true, "CpuHigh": true, "MemHigh": true, "DiskHigh": true, "TrafficHigh": true, "Expiry": true } },
  "geoip":     { "enabled": true, "refreshDays": 7,
                 "ipv4Url": "https://cdn.jsdelivr.net/npm/@ip-location-db/asn-country/asn-country-ipv4-num.csv",
                 "ipv6Url": "https://cdn.jsdelivr.net/npm/@ip-location-db/asn-country/asn-country-ipv6-num.csv" },
  "geoip.status": { "loaded": false, "ipv4Ranges": 0, "ipv6Ranges": 0, "lastSuccessMs": null, "lastAttemptMs": null, "lastError": null },
  "retention": { "alertDays": 180, "trafficDailyDays": 400 },
  "security":  { "loginMaxPerMinute": 5, "installScriptMaxPerMinute": 10 }
}
```

约束:`cooldownMinutes` 30–60;`offlineSeconds` 10–600;各 `*Pct` 50–100;`expiryDays` 1–60;`intervalMs` 1000–60000;`timeZone` 必须能被 `TimeZoneInfo.FindSystemTimeZoneById` 解析。环境变量 `SNM_PUBLIC_BASE_URL`、`SNM_RELEASE_BASE_URL` 仅在**首次建库**时作为种子写入 `general.publicBaseUrl`/`agent.releaseBaseUrl`。

---

## 3. 实体关系

```
AdminUsers 1──n RefreshTokens
Nodes 1──1 NodeTrafficState
Nodes 1──n Metrics1m / Metrics1h / Metrics1d
Nodes 1──n TrafficDaily / TrafficMonthly
Nodes 1──n AlertStates / AlertEvents
NotificationChannels (独立)   Settings (独立)   RollupState (独立)
```
所有 `Nodes` 子表 `ON DELETE CASCADE`。删除节点时同时清理内存 `LiveStore` 并广播 `NodeRemoved`。

---

## 4. 内存态(不落库,DESIGN 中的 `LiveStore`)

每节点 `LiveNode`:`ConnectionId?`, `RemoteIp`, `LastSeenMs`, `LastSeq`, `LastHeartbeat`(原始 DTO), `LastReceivedMs`, `Ring<LivePoint>(90)`, `MinuteBucket`(当前分钟累加器), `TrafficAccumulator`(自上次落盘以来的 Δrx/Δtx 按 `(LocalDate, CycleStart)` 分桶), `TrafficAnchor`(与 `NodeTrafficState` 同步), `CycleRx/CycleTx`(当前周期累计,启动时从 `TrafficMonthly` 读), `OpenAlerts`。启动时由 `Nodes` + `NodeTrafficState` + `TrafficMonthly`(当前周期)+ `AlertStates` 重建。

---

## 5. 时序数据

### 5.1 1 分钟聚合(内存)

```
onHeartbeat(node, hb, receivedMs, rxBps, txBps, dRx, dTx):
  bucketMs = receivedMs - receivedMs % 60000
  b = node.MinuteBucket
  if b == null or b.TsMs != bucketMs:
      if b != null: FlushQueue.Enqueue(b)         # 上一分钟关闭(晚到的心跳只可能进新桶,因为时间取 receivedMs)
      b = node.MinuteBucket = new Bucket(bucketMs)
  b.Samples++
  b.CpuSum += hb.Cpu;               b.CpuMax = max(b.CpuMax, hb.Cpu)
  if hb.Load1 != 65535: b.LoadSum += hb.Load1; b.LoadN++
  b.MemSum += hb.MemUsedMb;         b.MemMax = max(...)
  b.SwapSum += hb.SwapUsedMb
  b.DiskSum += sum(hb.DiskUsedMb)
  b.RxBpsSum += rxBps; b.RxBpsMax = max; b.TxBpsSum += txBps; b.TxBpsMax = max
  b.RxBytes += dRx; b.TxBytes += dTx
  b.OnlineSec = min(60, b.Samples * node.IntervalMs / 1000)
```

写入行:`Avg = Sum / Samples`(Load 用 `LoadN`,为 0 时写 0)。

### 5.2 `MinuteFlusher`(每分钟第 3 秒)

- 触发:`PeriodicTimer` 对齐到 `mm:03`(启动时先 `Delay` 到下一个整分 +3 s)。
- 动作:对所有节点,若 `MinuteBucket.TsMs < 当前整分` → 关闭并入队;然后一次事务把队列中的全部桶 `INSERT INTO Metrics1m … ON CONFLICT(NodeId,TsMs) DO UPDATE SET …`(同桶重复写取**最新**——只在异常重放时发生)。
- 失败(SQLITE_BUSY 等):桶留在队列,下一分钟重试;队列上限 10000 桶,超出丢最旧并记 Error。
- 关闭前(`ApplicationStopping`):强制 flush 当前未满的分钟桶(允许 Samples < 满)。

### 5.3 `HourlyRollup`(每小时 02 分 00 秒 UTC)

```
target = floor(now - 1h to hour)                        # 上一个完整小时
from   = RollupState["rollup.1h.watermark"] + 1h (若无则 = min(TsMs) of Metrics1m 向下取整到小时, 但不早于 now - 25h)
for hourTs in [from .. target] step 1h:                 # 补跑
   rows = SELECT NodeId, SUM(Samples) n, SUM(CpuAvg*Samples)/SUM(Samples) cpuAvg, MAX(CpuMax) cpuMax,
                 SUM(Load1Avg*Samples)/SUM(Samples), SUM(MemUsedAvgMb*Samples)/SUM(Samples), MAX(MemUsedMaxMb),
                 SUM(SwapUsedAvgMb*Samples)/SUM(Samples), SUM(DiskUsedAvgMb*Samples)/SUM(Samples),
                 SUM(RxBpsAvg*Samples)/SUM(Samples), MAX(RxBpsMax), SUM(TxBpsAvg*Samples)/SUM(Samples), MAX(TxBpsMax),
                 SUM(RxBytes), SUM(TxBytes), SUM(OnlineSec)
          FROM Metrics1m WHERE TsMs >= hourTs AND TsMs < hourTs + 3600000 GROUP BY NodeId
   UPSERT Metrics1h (NodeId, hourTs, …)                 # 幂等
   RollupState["rollup.1h.watermark"] = hourTs           # 同事务
```
加权平均按 `Samples` 权重(SQLite 整数除法前先乘,用 `CAST(... AS REAL)` 再 `ROUND`)。

### 5.4 `DailyRollup`(每日 00:10:00 UTC)

同 5.3,源 `Metrics1h`,目标 `Metrics1d`,桶 = UTC 日;水位 `rollup.1d.watermark`。天粒度采用 UTC(与账单周期的本地日无关;图表前端按浏览器时区显示刻度)。

### 5.5 `RetentionSweeper`(每小时 05 分)

| 表 | 删除条件 | 说明 |
|---|---|---|
| `Metrics1m` | `TsMs < now − 25h` | 多留 1 h 保证小时上卷完成 |
| `Metrics1h` | `TsMs < now − 8d` | |
| `Metrics1d` | `TsMs < now − 31d` | PRD:>30 天销毁 |
| `AlertEvents` | `StartedMs < now − retention.alertDays` 且 `Status='resolved'` | |
| `TrafficDaily` | `LocalDate < today − retention.trafficDailyDays` | |
| `RefreshTokens` | 见 2.2 | |
| `TrafficMonthly` | 不删除 | 每节点每月 1 行 |

每批 `DELETE … LIMIT 5000`(SQLite 需 `SQLITE_ENABLE_UPDATE_DELETE_LIMIT`,Microsoft.Data.Sqlite 自带的 e_sqlite3 已启用;若不可用则改为按 `TsMs` 范围分段删),批间 `Task.Delay(50ms)`,避免长事务阻塞写入。

### 5.6 调度总表

| 服务 | 周期/时刻 | 幂等/补跑 |
|---|---|---|
| `MinuteFlusher` | 每分钟 :03 | UPSERT;失败留队列 |
| `HourlyRollup` | 每小时 :02:00 UTC | 水位补跑;UPSERT |
| `DailyRollup` | 每日 00:10:00 UTC | 水位补跑;UPSERT |
| `RetentionSweeper` | 每小时 :05:00 | 天然幂等 |
| `TrafficStateFlusher` | 每 60 s(启动后 60 s 起) | 锚点 + 累加同事务 |
| `HealthMonitor` | 每 5 s | 在线状态评估 |
| `RealtimeBroadcaster` | 每 2 s | 无状态 |
| `AlertEvaluator` | 每 10 s;`Expiry` 每日 `alert.expiryCheckTime`(全局时区)+ 节点保存后立即 | 状态机持久化 |
| `NotificationDispatcher` | 事件驱动(Channel 队列) | 重试 3 次(1 s/5 s/30 s) |
| `GeoIpRefresher` | 启动后 5 s;成功后每 `refreshDays`;失败后 1 h | 原子替换文件 |
| `DbMaintenance` | 每日 04:00 UTC | checkpoint/incremental_vacuum/optimize |

启动顺序与依赖见 DESIGN.md §5。所有服务用 `PeriodicTimer`,对齐算法:`await Task.Delay(nextAligned - now)` 后进入 `while (await timer.WaitForNextTickAsync(ct))`;每次 tick 内部 try/catch 全部异常并记日志,**绝不让 BackgroundService 因异常退出**。

### 5.7 查询(供 API `GET /api/nodes/{id}/metrics`)

| range | 表 | 时间窗 | 点数上限 |
|---|---|---|---|
| `24h` | `Metrics1m` | `now − 24h .. now` | 1440(+ 当前未落库的分钟桶实时值可选追加 1 点) |
| `7d` | `Metrics1h` | `now − 7d .. now` | 168(+ 当前小时由 1m 实时聚合 1 点) |
| `30d` | `Metrics1d` | `now − 30d .. now` | 30(+ 当天由 1h 实时聚合 1 点) |

缺桶不补零,前端按 `t` 画,断档显示为空隙(`connectNulls=false`)。

---

## 6. 流量 Delta 引擎

### 6.1 输入/状态

- 输入:每次心跳 `(NetRxBytes, NetTxBytes, ElapsedMs, UptimeSec)` + `ReceivedMs`;`Hello` 带 `BootId`。
- 状态(内存 + `NodeTrafficState`):`Anchor{Rx, Tx, SampleMs, BootId}`。
- 常量:`MaxPlausibleBps = 12_500_000_000`(100 Gbps)。

### 6.2 算法(伪代码)

```
onHello(node, info):
    if node.Anchor != null and node.Anchor.BootId != info.BootId and info.BootId != "":
        node.RebootedSinceAnchor = true            # 下一次心跳按"清零"处理

onHeartbeat(node, hb, receivedMs) -> (dRx, dTx):
    a = node.Anchor
    if a == null:                                  # 首包(节点首次接入 / 状态被清)
        dRx = dTx = 0
        # 例外:能确认是刚开机(Uptime 很短且计数器不大)时,把开机以来的量计入
        if hb.UptimeSec <= 600 and hb.NetRxBytes <= MaxPlausibleBps * hb.UptimeSec: dRx = hb.NetRxBytes
        if hb.UptimeSec <= 600 and hb.NetTxBytes <= MaxPlausibleBps * hb.UptimeSec: dTx = hb.NetTxBytes
    else:
        dtSec = max(1, (receivedMs - a.SampleMs) / 1000)
        dRx = delta(hb.NetRxBytes, a.Rx, dtSec, hb.UptimeSec, node.RebootedSinceAnchor)
        dTx = delta(hb.NetTxBytes, a.Tx, dtSec, hb.UptimeSec, node.RebootedSinceAnchor)
    node.RebootedSinceAnchor = false
    node.Anchor = { Rx: hb.NetRxBytes, Tx: hb.NetTxBytes, SampleMs: receivedMs, BootId: node.BootId }
    attribute(node, dRx, dTx, receivedMs)
    return (dRx, dTx)

delta(cur, last, dtSec, uptimeSec, rebooted):
    if rebooted or cur < last:                     # 计数器清零(重启、网卡重建、Agent 网卡集合变化导致变小)
        d = cur                                     # 开机(或清零)以来的量
        cap = MaxPlausibleBps * min(uptimeSec, dtSec + 60)
        return d <= cap ? d : 0                     # 不可信(例如新增了带历史计数的网卡)→ 放弃这一段,重新锚定
    d = cur - last
    if d > MaxPlausibleBps * dtSec:                 # 正向跳变不可信(网卡集合变化把大计数器加进来)
        log warning "implausible counter jump"; return 0
    return d

attribute(node, dRx, dTx, receivedMs):
    tz    = node.TimeZone ?? settings.general.timeZone
    local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(receivedMs), tz)
    day   = local.Date                              # yyyyMMdd
    (cycleStart, cycleEnd) = ComputeCycle(day, node.TrafficResetDay)
    node.Acc[(day, cycleStart)].Rx += dRx; .Tx += dTx      # 内存累加,60 s 落盘
    if cycleStart != node.CurrentCycleStart:                # 跨周期:新周期从 0 开始 => 自动清零
        node.CurrentCycleStart = cycleStart; node.CycleRx = node.CycleTx = 0
        freeze LimitBytes on the previous TrafficMonthly row
    node.CycleRx += dRx; node.CycleTx += dTx
```

`TrafficStateFlusher` 每 60 s 一个事务:`UPSERT NodeTrafficState(anchor)`;对 `Acc` 中每个 `(day, cycleStart)`:`UPSERT TrafficDaily … SET RxBytes = RxBytes + excluded.RxBytes`、`UPSERT TrafficMonthly … SET RxBytes = RxBytes + excluded.RxBytes, LimitBytes = 当前限额`;清空 `Acc`;同时写 `Nodes.LastSeenMs`。锚点与累加**同事务**保证"每字节恰好记一次":重启后从持久化锚点做差,覆盖的正是尚未落盘的那段。

### 6.3 边界情形一览

| 情形 | 行为 |
|---|---|
| 首包 | 只锚定(除"刚开机 ≤600 s"例外) |
| Agent 重启(机器未重启) | 计数器连续,正常差值;Agent 离线期间的流量在重连首包一次性补齐(计数器是累计的) |
| 机器重启 | `Hello.BootId` 变化或 `cur < last` → 取 `cur`(开机以来)为增量,受 `Uptime × 100 Gbps` 上限约束 |
| Master 重启 | 锚点每 60 s 持久化且与累加同事务,不丢不重;首包差值覆盖停机期 |
| 计数器 32 位回绕 | 表现为 `cur < last` → 按清零处理(记入 `cur`,下界估计);64 位内核无此问题 |
| 网卡集合变化(插拔/Agent 参数改) | 突增超 `100 Gbps × dt` → 丢弃该次增量并重新锚定;突减 → 清零规则 + 上限约束 |
| 断线重连,ElapsedMs 很大 | 速率计算用服务端 Δt;流量增量不受影响 |
| 账单重置日 | `ComputeCycle` 产生新 `cycleStart`,新周期行从 0 累计;旧行 `LimitBytes` 冻结 |
| 重置日 29/30/31 在短月 | 钳制到该月最后一天(见 6.4 例) |
| 时区变更(节点或全局) | 后续增量按新时区归日/归周期;历史行不重算 |
| 时钟回拨(Master) | `receivedMs` 单调性不依赖;`dtSec` 取 `max(1, …)`;桶键取当前时刻 |
| 限额调整 | 影响用量百分比与告警;`TrafficMonthly.LimitBytes` 在下一次落盘更新 |
| 删除节点 | 级联删除全部流量行 |

### 6.4 账单周期计算(`BillingCycle.ComputeCycle`)

```
ComputeCycle(localToday: DateOnly, resetDay: int) -> (start: DateOnly, endExclusive: DateOnly)
  d = clamp(resetDay, 1, 31)
  anchor(y, m) = new DateOnly(y, m, min(d, DaysInMonth(y, m)))
  thisAnchor = anchor(localToday.Year, localToday.Month)
  if localToday >= thisAnchor:
      start = thisAnchor
      (y2, m2) = AddMonths(localToday.Year, localToday.Month, +1); end = anchor(y2, m2)
  else:
      (y0, m0) = AddMonths(localToday.Year, localToday.Month, -1); start = anchor(y0, m0); end = thisAnchor
```

例(单元测试用例):

| 今天(本地) | resetDay | start | end |
|---|---|---|---|
| 2026-09-07 | 1 | 2026-09-01 | 2026-10-01 |
| 2026-09-07 | 15 | 2026-08-15 | 2026-09-15 |
| 2026-02-15 | 31 | 2026-01-31 | 2026-02-28 |
| 2026-03-01 | 31 | 2026-02-28 | 2026-03-31 |
| 2028-02-29 | 30 | 2028-02-29 | 2028-03-30 |
| 2026-12-31 | 31 | 2026-12-31 | 2027-01-31 |

用量(`TrafficMode`):`sum`=Rx+Tx、`tx`=Tx、`rx`=Rx、`max`=max(Rx,Tx);`pct = used / (LimitGb × 10^9) × 100`,`LimitGb=0` 时 `pct=null`。

---

## 7. IP 合并与 GeoIP

### 7.1 `IpsJson` 元素

```json
{ "ip": "203.0.113.7", "family": 4, "kind": "public", "source": "server", "lastSeen": 1757200000000 }
```

### 7.2 合并算法(每次 `Ips` 上报、每次连接建立)

```
entries = parse(node.IpsJson)
now = receivedMs
for s in report.Ips (≤32):
    if !IPAddress.TryParse(s) or isLoopback/linkLocal/unspecified/multicast → skip
    upsert(entries, ip=canonical(s), family, kind=classify(ip), source keep "server" if existing was server else "agent", lastSeen=now)
remote = connection.RemoteIpAddress (IPv4-mapped IPv6 先 MapToIPv4)
if remote != null and !isLoopback(remote): upsert(entries, remote, kind=classify(remote), source="server", lastSeen=now)
entries.RemoveAll(e => e.lastSeen < now - 24h)
sort by (kind public first, family 4 first, ip text); truncate 32
node.IpsJson = serialize(entries)
autoCc = GeoIp.Lookup(first public with source=="server") ?? GeoIp.Lookup(first public any) ?? node.CountryCodeAuto
if autoCc != node.CountryCodeAuto: update + broadcast NodeChanged
```
`classify`:私网 = `10/8`、`172.16/12`、`192.168/16`、`100.64/10`、`fc00::/7`;其余 `public`。

### 7.3 GeoIP 数据

- 文件:`{DataDir}/geoip/asn-country-ipv4-num.csv`、`asn-country-ipv6-num.csv`、`meta.json`(`{ "ipv4": {"etag":"","lastModified":"","downloadedMs":0,"rows":0}, "ipv6": {...} }`)。
- 加载:启动时若文件存在立即解析到内存(`uint[] Start, End; string[] Cc`;IPv6 用 `UInt128[]`,`UInt128.Parse`),二分查找 `Start <= x`,再校验 `x <= End`;IPv4 约 40 万行、IPv6 约 20 万行,内存 < 20 MB。
- 刷新:`GeoIpRefresher` 下载到 `*.tmp` → 校验行数 > 1000 且首行格式 `^\d+,\d+,[A-Z]{2}$` → `File.Move(overwrite)` → 重新加载 → 更新 `Settings["geoip.status"]` 与 `RollupState["geoip.lastRefreshMs"]`。发送 `If-None-Match`,304 视为成功。
- 失败:不影响启动,`geoip.status.lastError` 记录,1 h 后重试;国家码保持为空/旧值。
- 覆盖:`Nodes.CountryCodeOverride` 优先;清空覆盖立即回退到 `CountryCodeAuto`。国旗 Emoji 由前端根据 2 字母码生成(`String.fromCodePoint(0x1F1E6 + c - 65)`)。

---

## 8. 告警引擎

### 8.1 规则目录

| Rule | 条件(`v` = 评估值) | 阈值来源(节点覆盖 ?? 全局) | Sustain | 恢复条件 | Level | 重复提醒 |
|---|---|---|---|---|---|---|
| `Offline` | `now − LastSeenMs > offlineSeconds×1000`(且节点曾经在线过,`LastSeenMs != null`) | `offlineSeconds` | 0 | 收到心跳(立即) | critical | 每 `renotifyHours` |
| `CpuHigh` | 最近 1 分钟桶(含当前)`CpuAvg ≥ cpuHighPct×10` | `cpuHighPct`, `cpuSustainSec` | `cpuSustainSec` | `Cpu < (pct − hysteresis)` 持续 `recoverSustainSec` | warning | 否 |
| `MemHigh` | `MemUsed/MemTotal ≥ memHighPct%` | `memHighPct`, `memSustainSec` | `memSustainSec` | 同上(滞回) | warning | 否 |
| `DiskHigh` | 任一挂载 `Used/Total ≥ diskHighPct%`(`Total ≥ 1024 MiB` 的挂载才评估) | `diskHighPct` | 0 | 全部挂载 `< pct − 2` | warning | 否 |
| `TrafficHigh` | `cyclePct ≥ trafficHighPct`(`LimitGb>0`) | `trafficHighPct` | 0 | 新周期开始或限额提高使 `pct < 阈值` | warning(≥95% 升级 critical,作为新事件) | 否 |
| `Expiry` | `daysLeft = ExpiresDate − today(全局时区) ≤ expiryDays`(含负数=已过期) | `expiryDays` | 0 | `daysLeft > expiryDays`(改了到期日)| warning(已过期 critical) | 每 `renotifyHours` |

`AlertsEnabled=false` 或 `Enabled=false` 的节点:所有规则视为 Ok,已 Firing 的事件立即 `resolved`(消息"节点已禁用/告警已关闭"),不发通知。`rulesEnabled[rule]=false` 同理全局关闭。

### 8.2 状态机

```
            cond=true                 now-FirstBreach >= Sustain
   Ok ───────────────▶ Pending ─────────────────────────────▶ Firing
    ▲                     │ cond=false                          │
    └─────────────────────┘                                     │ recover-cond 持续 RecoverSustain(Offline: 立即)
    ◀───────────────────────────────────────────────────────────┘  => Resolved
```

进入 `Firing`:
1. 创建 `AlertEvents{Status=firing, StartedMs=now, Value, Threshold}`,`AlertStates.OpenEventId` 指向它。
2. `if now < CooldownUntilMs` → `Suppressed=true`(不外发,但仍推 `Alert` 到 admin hub 并显示"已抑制");否则入队通知,成功后 `NotifiedMs=now`。
3. `CooldownUntilMs = now + cooldownMinutes`;`LastNotifiedMs = now`(未抑制时)。

`Firing` 中:
- 若规则有重复提醒且 `now − LastNotifiedMs ≥ renotifyHours×3600000` → 再发一条"仍在持续"通知(同一事件,不新建)。
- 恢复:`ResolvedMs=now, Status=resolved`;若该事件 `Suppressed=false` → 发 🟢 恢复通知(不受冷却限制);若 `Suppressed=true` → 恢复也静默。`State=Ok`,`OpenEventId=null`。

去重键:`(NodeId, Rule)`;`AlertStates` 主键保证唯一,评估器单线程(所有规则在同一个 `AlertEvaluator` tick 内串行)。

### 8.3 评估调度

- `AlertEvaluator` 每 10 s 遍历 `LiveStore` 全部节点,评估 `Offline/CpuHigh/MemHigh/DiskHigh/TrafficHigh`;状态变化才写 DB(`AlertStates` UPSERT + `AlertEvents`),`LastEvalMs/LastValue` 每 60 s 批量落盘一次。
- `Expiry`:每日 `alert.expiryCheckTime`(全局时区)全量评估;节点保存(到期日/开关变化)后对该节点立即评估。
- 启动:从 `AlertStates` 恢复;`Offline` 规则在 Master 启动后 **60 s 内不评估**(给 Agent 重连时间),避免重启风暴。

### 8.4 通知分发

`NotificationDispatcher` 消费 `Channel<AlertNotification>`(容量 1000,满则丢弃并记 Error):对每个 `Enabled` 且 `MinLevel ≤ level` 且 `Rules 为空或包含 rule` 的渠道发送;每渠道独立重试 3 次(1 s/5 s/30 s);结果写 `AlertEvents.NotifiedMs/NotifyError`(任一渠道成功即视为已通知;错误拼接渠道名)与 `NotificationChannels.LastSentMs/LastError`。消息模板与 Webhook 负载见 API.md §10。

---

## 9. 数据量与容量

| 规模 | 1m 行/天 | 1h 行/周 | 1d 行/月 | DB 体积估算(30 天) |
|---|---|---|---|---|
| 20 节点 | 28.8k | 3.4k | 600 | ≈ 5 MB |
| 100 节点 | 144k | 16.8k | 3k | ≈ 20 MB |
| 500 节点 | 720k | 84k | 15k | ≈ 90 MB |

写入频率:每分钟一批(≤ 节点数行)+ 每 60 s 一批流量 + 事件驱动的告警;SQLite WAL 轻松承载。

---

## 10. 测试点(供 `tests/SNM.Master.Tests`)

| 测试类 | 覆盖 |
|---|---|
| `BillingCycleTests` | §6.4 表中全部用例 + 闰年 + resetDay 越界钳制 |
| `TrafficDeltaTests` | 首包、正常差值、计数器下降、BootId 变化、不可信跳变、开机 ≤600 s 例外、跨日/跨周期归属、时区归日、锚点持久化一致性(模拟重启) |
| `MinuteAggregatorTests` | 均值/峰值/样本数、Load N/A 排除、跨分钟关闭、关闭前 flush |
| `RollupTests` | 1m→1h 加权平均、水位补跑、重复运行幂等 |
| `RetentionTests` | 边界时间戳保留/删除 |
| `AlertStateMachineTests` | Pending→Firing 持续时间、滞回恢复、冷却抑制、恢复通知规则、重复提醒、禁用节点自动恢复、Expiry 日期计算 |
| `GeoIpLookupTests` | 二分查找边界、IPv6 UInt128、未命中、文件缺失 |
| `IpMergeTests` | 分类、去重、server 来源优先、24 h 过期、上限 32 |
