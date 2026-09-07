# DATA — 数据模型、时序降采样、流量 Delta 引擎、告警状态机(候选方案 A)

> 所有表由 EF Core 迁移创建;实现者不得改列名/类型/索引。单位沿用 PROTOCOL.md §0(MB = MiB,字节为 `long`,千分比为 `int`)。时间列后缀 `At`/`Utc` 为 UTC。

---

## 0. 定案摘要(BRIEF §3 Q3 / Q4 / Q5 数据面)

| # | 定案 |
|---|---|
| Q3 | SQLite 单文件 `snm.db`,WAL;EF Core 10 迁移随代码提交,启动 `Migrate()`;心跳只进内存,按服务端分钟桶聚合,`Metrics1m/1h/1d` 三张同构表(均值 + 峰值 + 字节和 + 样本数),`BackgroundService + PeriodicTimer` 调度,全部任务幂等(按主键 upsert、按游标删除) |
| Q4 | Agent 侧过滤网卡并上报**聚合累计值**;服务端 Delta:`cur ≥ prev → cur−prev`;`cur < prev` 且节点重启(BootTime 变化 > 120 s)→ `cur`;`cur < prev` 且未重启 → `0`(重新基线);可信度上限 100 Gbit/s;账期 = 重置日 + 月末钳制 + 节点/全局时区;`TrafficState`(基线,持久化)、`TrafficDaily`(本地日)、`TrafficMonthly`(账期) |
| Q5 | 状态机 Normal → Pending(连续计数)→ Firing → Resolved;`AlertStates` 持久化(重启不重复告警);`AlertEvents` 审计;冷却 30–60 min 只抑制同键**新触发**的通知,恢复通知对已通知事件必发;渠道表 `NotificationChannels`(Telegram/Webhook,JSON 配置,动态生效,可测试);投递记录 `NotificationDeliveries`(3 次重试) |

---

## 1. 存储概览

### 1.1 文件布局(`Snm:DataDir`,默认 `./data`)

```
data/
  snm.db  snm.db-wal  snm.db-shm      SQLite 主库(WAL)
  geoip/asn-country-ipv4-num.csv      GeoIP 数据集(启动异步下载,每 7 天刷新)
  geoip/asn-country-ipv6-num.csv
  geoip/meta.json                     {"downloadedAtUtc":"...","ipv4Rows":n,"ipv6Rows":n,"etagV4":"","etagV6":""}
  backups/                            手工/脚本备份目录(DEPLOY.md)
```

### 1.2 连接与 PRAGMA

- 连接串:`Data Source={DataDir}/snm.db;Pooling=True;Default Timeout=30`。
- 首次创建数据库(文件不存在)时,在迁移前执行:`PRAGMA journal_mode=WAL; PRAGMA auto_vacuum=INCREMENTAL;`(`auto_vacuum` 必须在建表前设置)。
- 每个连接打开时(`DbConnectionInterceptor.ConnectionOpened`):`PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-20000;`。
- 写入纪律:进程内唯一 `DbWriteLock`(`SemaphoreSlim(1,1)`),所有**后台任务**的写事务必须持锁;REST 写操作走 EF 默认(SQLite 自身串行 + `busy_timeout`),数量小。读不受影响(WAL)。
- EF 注册:`AddDbContextFactory<SnmDbContext>()`(后台服务)+ `AddDbContext<SnmDbContext>()`(请求作用域,内部复用工厂)。`NoTracking` 为默认查询行为。

### 1.3 迁移策略

- 迁移文件位于 `src/SNM.Master/Data/Migrations/`,与模型快照一起提交;开发期 `dotnet ef migrations add <Name> --project src/SNM.Master`(需要 `Microsoft.EntityFrameworkCore.Design`,`PrivateAssets=all`)。
- 启动顺序:打开连接 → 若为新库执行 1.2 的 PRAGMA → `Database.Migrate()` → 种子(`Settings` 默认值缺失键补齐、`AdminUsers` 为空则创建)→ 其余服务启动。迁移失败 → 进程退出码 3,日志说明。
- 规则:已发布的迁移不修改;列删除/改类型走新迁移(EF 对 SQLite 自动重建表);大表(`Metrics*`)避免重建型迁移——新增列一律 `nullable` 或带默认值。
- 兼容检查:若库中存在业务表但无 `__EFMigrationsHistory` → 拒绝启动并提示"数据库不是由本程序创建"。

### 1.4 类型映射约定

| C# | SQLite 列 | 说明 |
|---|---|---|
| `int`/`long`/`bool`/`byte` | INTEGER | bool 为 0/1 |
| `DateTime`(UTC) | TEXT `yyyy-MM-dd HH:mm:ss.fffffff` | EF 默认;所有 `*At` 列 |
| `DateOnly` | TEXT `yyyy-MM-dd` | 账期、日流量、到期日 |
| `decimal` | TEXT | 价格,`decimal(12,2)` 由代码校验 |
| `string` | TEXT | 长度在代码层用 `[MaxLength]` 校验(SQLite 不强制) |
| 时序表时间 | INTEGER Unix 秒 | `Ts` 列,便于范围扫描与保留删除 |

---

## 2. 实体清单

| 表 | 用途 | 增长 | 生命周期 |
|---|---|---|---|
| `Nodes` | 节点主档(配置 + 硬件 + 财务 + 状态快照) | 每节点 1 行 | 手工 |
| `NodeIps` | 合并后的 IP 集合 | 每节点 ≤ ~20 行 | 24 h 未见删除 |
| `InstallTokens` | 安装脚本一次性令牌 | 少量 | 过期删除 |
| `Metrics1m` / `Metrics1h` / `Metrics1d` | 时序聚合 | 1440/24/1 行·节点·天 | 25 h / 8 d / 31 d |
| `TrafficState` | Delta 基线 | 每节点 1 行 | 与节点同寿 |
| `TrafficDaily` | 节点本地日流量 | 1 行·节点·天 | 400 天 |
| `TrafficMonthly` | 账期流量 | 1 行·节点·账期 | 永久 |
| `AlertStates` | 规则状态机 | 每节点每规则 1 行 | 与节点同寿 |
| `AlertEvents` | 告警事件(触发/恢复) | 事件数 | 180 天(可配) |
| `NotificationChannels` | 通知渠道 | 少量 | 手工 |
| `NotificationDeliveries` | 投递记录 | 每事件每渠道每次尝试 1 行 | 30 天(可配) |
| `Settings` | 键值设置 | ~40 行 | 永久 |
| `AdminUsers` | 管理员(单账号,表结构允许多) | 1 行 | 永久 |
| `RefreshTokens` | 刷新令牌(哈希) | 每次登录 1 行 | 过期/撤销后 7 天删除 |

外键:`NodeIps`、`InstallTokens`、`TrafficState`、`AlertStates` → `Nodes(Id) ON DELETE CASCADE`;`AlertEvents.NodeId` → `ON DELETE SET NULL`(保留历史,`NodeName` 为快照);`NotificationDeliveries.EventId` → `AlertEvents ON DELETE CASCADE`,`ChannelId` → `ON DELETE SET NULL`;`RefreshTokens.UserId` → CASCADE。`Metrics*`、`TrafficDaily`、`TrafficMonthly` **无外键**(大表),删除节点时由 `NodeService.DeleteAsync` 在同一事务里显式删除。

### 2.1 `Nodes`

| 列 | 类型 | 空 | 默认 | 说明 |
|---|---|---|---|---|
| `Id` | INTEGER PK AUTOINCREMENT | N | | |
| `PublicName` | TEXT(64) | N | | 前台名;**唯一索引** `UX_Nodes_PublicName` |
| `AdminRemark` | TEXT(256) | Y | | 后台私密备注 |
| `AgentKey` | TEXT(48) | N | | `snmk_` + 43 base64url;**唯一索引** `UX_Nodes_AgentKey` |
| `KeyRotatedAt` | TEXT | Y | | |
| `Enabled` | INTEGER | N | 1 | 禁用后 Agent 401、不评估告警、不出现在大屏 |
| `PublicVisible` | INTEGER | N | 1 | 是否出现在公开大屏 |
| `SortOrder` | INTEGER | N | 0 | 列表/大屏排序,升序 |
| `CountryCodeOverride` | TEXT(2) | Y | | 管理员覆盖,大写 |
| `CountryCodeAuto` | TEXT(2) | Y | | GeoIP 结果 |
| `TimeZoneId` | TEXT(64) | Y | | IANA;null → `site.timeZone` |
| `IntervalMs` | INTEGER | N | 2000 | 心跳间隔,1000–60000 |
| `Hostname` | TEXT(64) | Y | | 来自 Register |
| `Os` | TEXT(128) | Y | | |
| `Kernel` | TEXT(64) | Y | | |
| `Arch` | TEXT(16) | Y | | |
| `CpuModel` | TEXT(128) | Y | | 含 `2x ` 前缀 |
| `CpuCores` | INTEGER | N | 0 | |
| `MemTotalMb` | INTEGER | N | 0 | |
| `SwapTotalMb` | INTEGER | N | 0 | |
| `DisksJson` | TEXT | Y | | `[{"mount":"/","fs":"ext4","totalMb":80000}]`,顺序 = 心跳索引 |
| `NetIfs` | TEXT(256) | Y | | 计入网卡 |
| `Virt` | TEXT(32) | Y | | |
| `AgentVersion` | TEXT(32) | Y | | |
| `ProtocolVersion` | INTEGER | N | 0 | |
| `BootTimeUtc` | TEXT | Y | | `now − UptimeSec`,Register/Status 时更新 |
| `FirstSeenAt` | TEXT | Y | | 首次 Register |
| `LastRegisterAt` | TEXT | Y | | |
| `LastSeenAt` | TEXT | Y | | 任何上行消息;每分钟 flush 回写(内存为准) |
| `LastRemoteIp` | TEXT(45) | Y | | 服务端捕获 |
| `Status` | INTEGER | N | 0 | 0/1/2,flush 回写 |
| `StatusChangedAt` | TEXT | Y | | |
| `TrafficLimitBytes` | INTEGER | N | 0 | 0 = 不限 |
| `TrafficResetDay` | INTEGER | N | 1 | 1–31 |
| `TrafficCountMode` | INTEGER | N | 0 | 0 Rx+Tx / 1 Tx / 2 Rx / 3 max |
| `Vendor` | TEXT(64) | Y | | |
| `Price` | TEXT(decimal) | Y | | 每账期价格 |
| `Currency` | TEXT(3) | Y | | USD / CNY / EUR |
| `BillingCycleMonths` | INTEGER | N | 0 | 0 无 / 1 / 3 / 6 / 12 / 24 / 36 |
| `ExpiresAt` | TEXT(date) | Y | | |
| `AutoRenew` | INTEGER | N | 0 | |
| `RenewUrl` | TEXT(512) | Y | | |
| `Notes` | TEXT(2000) | Y | | |
| `AlertsEnabled` | INTEGER | N | 1 | |
| `CpuAlertPct` | INTEGER | Y | | null → 全局 |
| `TrafficAlertPct` | INTEGER | Y | | |
| `OfflineAlertSec` | INTEGER | Y | | |
| `DiskAlertPct` | INTEGER | Y | | |
| `CreatedAt` / `UpdatedAt` | TEXT | N | | |

索引:`UX_Nodes_PublicName`、`UX_Nodes_AgentKey`、`IX_Nodes_SortOrder`。

MRR 计算(仪表盘):`Σ Price / BillingCycleMonths × rate[Currency]`(`BillingCycleMonths=0` 或 `Price` 为空的节点不计),`rate` 来自 `finance.rates`(单位:1 单位该货币 = 多少基准货币)。

### 2.2 `NodeIps`

| 列 | 类型 | 空 | 说明 |
|---|---|---|---|
| `NodeId` | INTEGER FK | N | |
| `Address` | TEXT(45) | N | 规范化字符串(`IPAddress.ToString()`,无 scope) |
| `Family` | INTEGER | N | 4 / 6 |
| `IsPublic` | INTEGER | N | 服务端判定(非私网/保留) |
| `Source` | INTEGER | N | 1 Agent 上报 / 2 服务端捕获 / 3 两者 |
| `FirstSeenAt` / `LastSeenAt` | TEXT | N | |

主键 `(NodeId, Address)`;索引 `IX_NodeIps_LastSeenAt`。清理:每日 03:15 UTC 删除 `LastSeenAt < now − 24 h` 的行;Agent 每次 `status` 全量集合到达时,不在集合中且 `Source=1` 的行直接删除(`Source=3` 降为 2)。

### 2.3 `InstallTokens`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` INTEGER PK | | |
| `NodeId` INTEGER FK | | |
| `Token` TEXT(43) | 唯一索引 | 32 B 随机 base64url |
| `ExpiresAt` TEXT | | 创建 + `agent.installTokenTtlHours`(24) |
| `CreatedAt` TEXT | | |
| `UsedCount` INTEGER | | 每次 `GET /install/{token}` +1 |
| `LastUsedAt` TEXT / `LastUsedIp` TEXT(45) | | |

密钥轮换 → 删除该节点全部令牌;每日清理过期 7 天以上的行。

### 2.4 `Metrics1m` / `Metrics1h` / `Metrics1d`(三表同构)

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` | INTEGER | |
| `Ts` | INTEGER | 桶起点 Unix 秒(1m:60 的倍数;1h:3600;1d:86400,UTC) |
| `Samples` | INTEGER | 聚合的心跳数(1h/1d 为下层桶样本之和) |
| `CpuAvg` / `CpuMax` | INTEGER | ‰ |
| `MemUsedAvgMb` / `MemUsedMaxMb` | INTEGER | |
| `SwapUsedAvgMb` | INTEGER | |
| `DiskUsedMb` | INTEGER | 所有挂载点已用之和的均值 |
| `DiskTotalMb` | INTEGER | 桶末总容量(1h/1d 取 MAX) |
| `RxBpsAvg` / `RxBpsMax` | INTEGER | 字节/秒 |
| `TxBpsAvg` / `TxBpsMax` | INTEGER | |
| `RxBytes` / `TxBytes` | INTEGER | 桶内 Delta 字节和(可直接画流量柱状图) |
| `Load1Avg` / `Load1Max` | INTEGER | ×100 |

主键 `(NodeId, Ts)`;索引 `IX_<table>_Ts`(保留删除用)。

### 2.5 `TrafficState`

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` INTEGER PK FK | | |
| `PrevRx` / `PrevTx` INTEGER | −1 = 无基线 | 最后接受的累计计数器 |
| `PrevAtUtc` TEXT | | 该样本的服务端时间 |
| `BootTimeAtPrevUtc` TEXT | 可空 | 取样时已知的节点启动时间 |
| `PrevConnectionId` TEXT(64) | 可空 | 用于 `ElapsedMs` 可信判定 |
| `UpdatedAt` TEXT | | |

### 2.6 `TrafficDaily`

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` INTEGER | | |
| `Date` TEXT(date) | 节点时区的本地日 | |
| `RxBytes` / `TxBytes` INTEGER | | |
| `UpdatedAt` TEXT | | |

主键 `(NodeId, Date)`。保留 `retention.trafficDailyDays`(400)。

### 2.7 `TrafficMonthly`(账期)

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` INTEGER | | |
| `PeriodStart` TEXT(date) | 本地日,含 | |
| `PeriodEnd` TEXT(date) | 本地日,**不含** | |
| `RxBytes` / `TxBytes` INTEGER | | |
| `BilledBytes` INTEGER | 按 `CountMode` 派生并存储 | |
| `LimitBytes` INTEGER | 写入时的限额快照 | |
| `CountMode` INTEGER / `ResetDay` INTEGER | 快照 | |
| `Closed` INTEGER | 账期结束后置 1 | |
| `UpdatedAt` TEXT | | |

主键 `(NodeId, PeriodStart)`。

### 2.8 `AlertStates`

| 列 | 类型 | 说明 |
|---|---|---|
| `NodeId` INTEGER FK | | |
| `Rule` INTEGER | `AlertRule` 常量 | |
| `Subject` TEXT(128) | 子对象,默认 `''`(DiskHigh 为挂载点;Traffic 为账期起点;Expiry 为到期日) | |
| `State` INTEGER | 0 Normal / 1 Pending / 2 Firing | |
| `Consecutive` INTEGER | 连续满足次数 | |
| `ResolveCount` INTEGER | 连续不满足次数 | |
| `FiringSince` TEXT | 可空 | |
| `LastNotifiedAt` TEXT | 可空 | |
| `CooldownUntil` TEXT | 可空 | 冷却截止 |
| `LastValue` REAL | 最近观测值 | |
| `OpenEventId` INTEGER | 可空 | 当前未恢复事件 |
| `UpdatedAt` TEXT | | |

主键 `(NodeId, Rule, Subject)`。每 10 s 评估后仅把**有变化**的状态行写库(与分钟 flush 合并事务)。

### 2.9 `AlertEvents`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` INTEGER PK AUTOINCREMENT | | |
| `NodeId` INTEGER | 可空(节点删除后置空) | |
| `NodeName` TEXT(64) | 快照 PublicName | |
| `Rule` INTEGER | | |
| `Subject` TEXT(128) | | |
| `Status` INTEGER | 1 Firing / 2 Resolved | |
| `Severity` INTEGER | 1/2/3 | |
| `Title` TEXT(200) | 如 `[离线] HK-Node-01` | |
| `Message` TEXT(2000) | 中文正文 | |
| `Value` REAL / `Threshold` REAL | 触发值/阈值(单位随规则:秒、%、字节比、天) | |
| `DedupKey` TEXT(160) | `{rule}:{nodeId}:{subject}` | |
| `StartedAt` TEXT | | |
| `ResolvedAt` TEXT | 可空 | |
| `Notified` INTEGER | 触发通知是否发送(非冷却期) | |
| `AcknowledgedAt` TEXT | 可空 | 管理员确认 |

索引:`IX_AlertEvents_StartedAt`(DESC 扫描)、`IX_AlertEvents_NodeId_StartedAt`、`IX_AlertEvents_Status`。

### 2.10 `NotificationChannels`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` INTEGER PK | | |
| `Type` TEXT(16) | `telegram` / `webhook` | |
| `Name` TEXT(64) | | |
| `Enabled` INTEGER | | |
| `ConfigJson` TEXT | 见下 | |
| `RuleMask` INTEGER | 位掩码 `1<<rule`;0 = 全部 | |
| `MinSeverity` INTEGER | 1–3,默认 1 | |
| `CreatedAt` / `UpdatedAt` / `LastTestAt` / `LastSuccessAt` TEXT | | |
| `LastError` TEXT(500) | | |

`ConfigJson` 结构:

```jsonc
// telegram
{ "botToken": "123456:AAH...", "chatId": "-1001234567890", "parseMode": "HTML", "disableNotification": false, "messageThreadId": null }
// webhook
{ "url": "https://hooks.example.com/snm", "method": "POST", "secret": "hmac-secret-or-empty",
  "headers": { "X-Custom": "v" }, "bodyTemplate": null, "timeoutSec": 10 }
```

### 2.11 `NotificationDeliveries`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` INTEGER PK | | |
| `EventId` INTEGER FK | | |
| `ChannelId` INTEGER FK(可空) | | |
| `Kind` INTEGER | 1 触发 / 2 恢复 / 3 测试 | |
| `Attempt` INTEGER | 1–3 | |
| `Ok` INTEGER | | |
| `StatusCode` INTEGER | HTTP 状态或 0 | |
| `Error` TEXT(500) | | |
| `ElapsedMs` INTEGER | | |
| `CreatedAt` TEXT | | |

### 2.12 `Settings`

| 列 | 类型 | 说明 |
|---|---|---|
| `Key` TEXT PK | 如 `alert.cpuPct` | |
| `Value` TEXT | JSON 字面量(`"str"`、`90`、`true`、`{...}`) | |
| `UpdatedAt` TEXT | | |

键全集与默认值见 §6。

### 2.13 `AdminUsers`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` INTEGER PK | | |
| `Username` TEXT(32) | 唯一索引 | |
| `PasswordHash` TEXT(200) | `pbkdf2-sha256$210000$<salt b64>$<hash b64>`(salt 16 B,hash 32 B) | |
| `NickName` TEXT(32) / `Avatar` TEXT(512) / `Email` TEXT(128) | 可空 | 兼容模板个人资料页 |
| `TokenVersion` INTEGER | 写入 JWT `tv` claim;改密/“退出所有设备”时 +1 → 旧 access token 立即失效 | |
| `FailedLogins` INTEGER / `LockedUntil` TEXT | 登录防爆破:`auth.loginMaxFailures`(5)次后锁 `auth.loginLockMinutes`(15) | |
| `CreatedAt` / `PasswordChangedAt` / `LastLoginAt` TEXT | | |

### 2.14 `RefreshTokens`

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` INTEGER PK | | |
| `UserId` INTEGER FK | | |
| `TokenHash` TEXT(44) | SHA-256(原始 token) base64;唯一索引 | |
| `ExpiresAt` TEXT | 创建 + `auth.refreshTokenDays` | |
| `CreatedAt` TEXT / `RevokedAt` TEXT | | |
| `ReplacedByHash` TEXT(44) | 轮换链 | |
| `UserAgent` TEXT(256) / `Ip` TEXT(45) | | |

刷新 = 校验未撤销未过期 → 撤销旧行并写 `ReplacedByHash` → 新行。已撤销令牌再次使用 → 撤销该用户全部令牌(防重放)。

---

## 3. 时序数据:聚合、降采样、保留

### 3.1 内存分钟聚合(每节点一个 `MinuteAccumulator`)

```
class MinuteAccumulator {
  long BucketTs;                    // now.ToUnixTimeSeconds() / 60 * 60
  int Samples;
  long CpuSum; int CpuMax;
  long MemSum; long MemMax;
  long SwapSum;
  long DiskUsedSum; long DiskTotal; // DiskTotal = 最后一次的 Σ TotalMb
  double RxBpsSum; long RxBpsMax; double TxBpsSum; long TxBpsMax;
  long RxBytes; long TxBytes;       // 来自 Delta 引擎
  long Load1Sum; int Load1Max;
}

OnHeartbeat(node, hb, now, rxBps, txBps, dRx, dTx):
  bucket = floor(now/60)*60
  if node.Acc.BucketTs != bucket:
      if node.Acc.Samples > 0: node.FlushQueue.Enqueue(node.Acc.ToRow(node.Id))   // 完成的桶
      node.Acc = new MinuteAccumulator { BucketTs = bucket }
  acc.Samples++; acc.CpuSum += hb.Cpu; acc.CpuMax = max(...); ... acc.RxBytes += dRx; ...
```

- 心跳 **不落库**;只有完成的分钟桶进入 `FlushQueue`。
- `MinuteFlushService`:`PeriodicTimer(60 s)`,首次触发对齐到下一分钟的第 5 秒;把所有节点 `FlushQueue` 中的行 + 尚未完成但 `BucketTs < 当前分钟` 的桶(节点刚离线时)以单事务 `INSERT ... ON CONFLICT(NodeId, Ts) DO UPDATE` 写入 `Metrics1m`;同一事务写 `TrafficState`/`TrafficDaily`/`TrafficMonthly` 脏行、`Nodes.LastSeenAt/Status/StatusChangedAt/LastRemoteIp`、脏的 `AlertStates`。
- 均值 = `round(Sum / Samples)`;`RxBpsAvg = round(RxBpsSum / Samples)`。
- 优雅停机(`IHostApplicationLifetime.ApplicationStopping`):把所有未完成桶也 flush(`Samples` 保留实际值)。

### 3.2 小时/天降采样(SQL 集合运算,`ExecuteSqlRaw`)

小时 rollup(`@hourTs` = 上一整点 Unix 秒):

```sql
INSERT INTO Metrics1h (NodeId, Ts, Samples, CpuAvg, CpuMax, MemUsedAvgMb, MemUsedMaxMb, SwapUsedAvgMb,
                       DiskUsedMb, DiskTotalMb, RxBpsAvg, RxBpsMax, TxBpsAvg, TxBpsMax, RxBytes, TxBytes, Load1Avg, Load1Max)
SELECT NodeId, @hourTs, SUM(Samples),
       CAST(ROUND(SUM(CpuAvg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(CpuMax),
       CAST(ROUND(SUM(MemUsedAvgMb*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(MemUsedMaxMb),
       CAST(ROUND(SUM(SwapUsedAvgMb*Samples)*1.0/SUM(Samples)) AS INTEGER),
       CAST(ROUND(SUM(DiskUsedMb*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(DiskTotalMb),
       CAST(ROUND(SUM(RxBpsAvg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(RxBpsMax),
       CAST(ROUND(SUM(TxBpsAvg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(TxBpsMax),
       SUM(RxBytes), SUM(TxBytes),
       CAST(ROUND(SUM(Load1Avg*Samples)*1.0/SUM(Samples)) AS INTEGER), MAX(Load1Max)
FROM Metrics1m
WHERE Ts >= @hourTs AND Ts < @hourTs + 3600
GROUP BY NodeId
ON CONFLICT(NodeId, Ts) DO UPDATE SET
  Samples=excluded.Samples, CpuAvg=excluded.CpuAvg, CpuMax=excluded.CpuMax,
  MemUsedAvgMb=excluded.MemUsedAvgMb, MemUsedMaxMb=excluded.MemUsedMaxMb, SwapUsedAvgMb=excluded.SwapUsedAvgMb,
  DiskUsedMb=excluded.DiskUsedMb, DiskTotalMb=excluded.DiskTotalMb,
  RxBpsAvg=excluded.RxBpsAvg, RxBpsMax=excluded.RxBpsMax, TxBpsAvg=excluded.TxBpsAvg, TxBpsMax=excluded.TxBpsMax,
  RxBytes=excluded.RxBytes, TxBytes=excluded.TxBytes, Load1Avg=excluded.Load1Avg, Load1Max=excluded.Load1Max;
```

天 rollup 同形:源 `Metrics1h`,目标 `Metrics1d`,窗口 `[@dayTs, @dayTs+86400)`(UTC 日)。

- 幂等:按主键 upsert,可重复执行。
- 补算:启动时对最近 26 小时的每个整点、最近 8 天的每个 UTC 日各执行一次(覆盖 Master 停机期间漏算)。
- 展示时区:桶边界固定 UTC;前端按浏览器时区展示;1d 桶在 UTC+8 显示为“08:00 起的一天”,可接受(文档化)。

### 3.3 保留策略(`RetentionService`,每小时第 7 分)

| 表 | 删除条件 | 说明 |
|---|---|---|
| `Metrics1m` | `Ts < now − 25 h` | 留 1 h 余量供小时 rollup |
| `Metrics1h` | `Ts < now − 8 d` | |
| `Metrics1d` | `Ts < now − 31 d` | PRD:超 30 天销毁 |
| `AlertEvents` | `StartedAt < now − retention.alertEventDays` 且 `Status=2` | |
| `NotificationDeliveries` | `CreatedAt < now − retention.deliveryDays` | |
| `TrafficDaily` | `Date < today − retention.trafficDailyDays` | |
| `NodeIps` | `LastSeenAt < now − 24 h` | 每日 03:15 |
| `InstallTokens` / `RefreshTokens` | 过期 7 天以上 | 每日 |

删除语句分批:`DELETE FROM Metrics1m WHERE rowid IN (SELECT rowid FROM Metrics1m WHERE Ts < @cutoff LIMIT 5000)` 循环至影响行数 0,每批间 `await Task.Delay(50)`,持 `DbWriteLock`。

### 3.4 维护任务

| 任务 | 时刻(UTC) | 语句 |
|---|---|---|
| WAL 截断 | 每日 03:00 | `PRAGMA wal_checkpoint(TRUNCATE)` |
| 增量真空 | 周日 03:30 | `PRAGMA incremental_vacuum(2000)` |
| 完整性检查 | 启动时(仅日志) | `PRAGMA quick_check` |

### 3.5 调度总表(全部 `BackgroundService` + `PeriodicTimer`;首次触发对齐到指定秒/分)

| 服务 | 周期 | 触发时刻 | 幂等手段 |
|---|---|---|---|
| `RealtimeBroadcaster` | 2 s | — | 内存 |
| `AlertEvaluator` | 10 s | — | 状态机 + 去重键 |
| `MinuteFlushService` | 60 s | 每分 :05 | 主键 upsert |
| `HourlyRollupService` | 1 h | 每时 :02:00 | upsert |
| `DailyRollupService` | 24 h | 00:10:00 | upsert |
| `RetentionService` | 1 h | 每时 :07:00 | 游标删除 |
| `ExpiryCheckService` | 24 h | `alert.expiryCheckHour`(站点时区);启动时若今天尚未执行则补跑 | `Subject=ExpiresAt`,每日最多 1 次评估 |
| `GeoIpRefreshService` | 6 h | 启动后 10 s 首次 | 文件原子替换 |
| `DbMaintenanceService` | 1 h | :00 检查是否到达 03:00 / 周日 03:30 | 幂等 PRAGMA |
| `NotificationDispatcher` | 事件驱动 | `Channel<Job>` | 投递表记录 |

### 3.6 查询映射(REST `GET /api/nodes/{id}/metrics`)

| `range` | 表 | 窗口 | 期望点数 |
|---|---|---|---|
| `24h` | `Metrics1m` | `Ts ≥ now−86400` | ≤ 1440 |
| `7d` | `Metrics1h` | `Ts ≥ now−604800` | ≤ 168 |
| `30d` | `Metrics1d` | `Ts ≥ now−2592000` | ≤ 30 |

---

## 4. 流量 Delta 引擎

### 4.1 输入与输出

输入:每次心跳的 `NetRxBytes`/`NetTxBytes`(累计)、`ElapsedMs`、`Seq`、服务端 `now`、节点 `BootTimeUtc`、连接 Id。输出:`dRx/dTx`(本次增量字节)、`rxBps/txBps`(实时速率)、`TrafficDaily`/`TrafficMonthly` 累加、分钟桶 `RxBytes/TxBytes`。

### 4.2 算法(伪代码,C# 语义)

```
sealed class TrafficRuntime {      // 内存;镜像到 TrafficState + 当前 TrafficDaily/TrafficMonthly 行
  long PrevRx = -1, PrevTx = -1;
  DateTime PrevAt; DateTime? BootTimeAtPrev; string? PrevConnectionId;
  DateOnly Day; long DayRx, DayTx;
  DateOnly PeriodStart, PeriodEnd; long PerRx, PerTx;
  bool Dirty;
}

const long MaxBytesPerSecond = 12_500_000_000;   // 100 Gbit/s 可信上限

(long dRx, long dTx, long rxBps, long txBps) OnHeartbeat(Node n, HeartbeatDto hb, DateTime now, string connId)
{
    var rt = n.Traffic;
    var tz = ResolveTz(n.TimeZoneId ?? Settings.site.timeZone);            // 非法 → UTC + Warning(每节点一次)
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, tz));
    EnsurePeriod(n, rt, today);                                              // 4.4
    if (rt.Day != today) { rt.Day = today; rt.DayRx = rt.DayTx = 0; LoadDailyIfExists(); }

    if (rt.PrevRx < 0) { Baseline(rt, hb, now, n, connId); return (0, 0, 0, 0); }   // 首包:只建基线

    bool rebooted = n.BootTimeUtc is DateTime b && rt.BootTimeAtPrev is DateTime pb && Math.Abs((b - pb).TotalSeconds) > 120;
    long dRx = Delta((long)hb.NetRxBytes, rt.PrevRx, rebooted);
    long dTx = Delta((long)hb.NetTxBytes, rt.PrevTx, rebooted);

    double dtServer = Math.Max((now - rt.PrevAt).TotalSeconds, 0.2);
    bool elapsedTrusted = hb.Seq > 1 && connId == rt.PrevConnectionId && hb.ElapsedMs >= 200 && hb.ElapsedMs <= 120_000;
    double rateDt = elapsedTrusted ? hb.ElapsedMs / 1000.0 : dtServer;

    long maxPlausible = (long)(Math.Max(dtServer, rateDt) * MaxBytesPerSecond);
    if (dRx > maxPlausible) { Log.Warn("implausible rx delta"); dRx = 0; }
    if (dTx > maxPlausible) { Log.Warn("implausible tx delta"); dTx = 0; }

    long rxBps = (long)Math.Round(dRx / rateDt), txBps = (long)Math.Round(dTx / rateDt);

    rt.DayRx += dRx; rt.DayTx += dTx; rt.PerRx += dRx; rt.PerTx += dTx;
    rt.PrevRx = (long)hb.NetRxBytes; rt.PrevTx = (long)hb.NetTxBytes; rt.PrevAt = now;
    rt.BootTimeAtPrev = n.BootTimeUtc; rt.PrevConnectionId = connId; rt.Dirty = true;
    return (dRx, dTx, rxBps, txBps);
}

static long Delta(long cur, long prev, bool rebooted)
{
    if (cur >= prev) return cur - prev;        // 正常单调递增
    if (rebooted) return cur;                  // 计数器随系统重启清零:开机至今全部为新增流量
    return 0;                                  // 未重启却回退(网卡集合变化 / 驱动重载):放弃本次差值,重新建基线
}
```

`BilledBytes = mode switch { 0 => Rx+Tx, 1 => Tx, 2 => Rx, 3 => max(Rx,Tx) }`;`pct = LimitBytes > 0 ? BilledBytes / LimitBytes : 0`。

### 4.3 边界情形

| 情形 | 行为 | 结果 |
|---|---|---|
| 节点首次接入(无 `TrafficState`) | 只建基线,不计增量 | 首包 0 字节 |
| Agent 重启、系统未重启 | 新连接 `register`,`BootTimeUtc` 不变;计数器继续 | `cur ≥ prev` → 精确增量,含 Agent 停机期间的流量 |
| VPS 重启(网卡计数清零) | `register` 带新 `UptimeSec` → `BootTimeUtc` 变化 > 120 s → `rebooted=true` | `cur < prev` → 增量 = `cur`(开机以来全部流量);重启前最后一次样本之后到关机之间的流量不可得(文档化损失) |
| 网卡消失(如 `eth1` 拔出)导致求和下降 | `rebooted=false` | 增量 0,重新基线;该周期少计约 1 个心跳间隔的流量 |
| 网卡新增 | 求和上跳,`cur ≥ prev` | 会把新网卡自启动以来的累计一次性计入(实际上是历史流量)。缓解:Agent 在 `NetIfs` 变化时先发 `status`;Master 收到 `NetIfs` 变化时**重新基线**(把下一次心跳当首包)。规则:`status.NetIfs != node.NetIfs → rt.PrevRx = rt.PrevTx = -1` |
| Master 重启 | 启动时从 `TrafficState` 加载基线 | 首个心跳 `cur ≥ prev` → 精确补回停机期间流量 |
| Master 停机期间节点重启 | 重连 `register` 更新 `BootTimeUtc`,`BootTimeAtPrev` 来自 DB | `rebooted=true` → 增量 = `cur` |
| 断线很久后重连 | `dtServer` 很大;`elapsedTrusted=false`(连接变化) | 增量精确;速率 = 增量 / 停机时长(均值) |
| 时钟:服务端时间回拨 | `dtServer` 钳制 ≥ 0.2 s | 速率异常但流量不受影响 |
| 重复/乱序心跳 | 已在 Hub 层按 `Seq` 丢弃(PROTOCOL R3) | 不进入引擎 |
| `ulong` 计数器超过 `long.MaxValue` | 现实不可能(9.2 EB) | 强转后为负 → `cur < prev` 分支 → 0 |
| 管理员修改 `TrafficResetDay`/时区 | 下一心跳 `EnsurePeriod` 得到新 `PeriodStart` | 当前账期行关闭(`Closed=1`),新账期从 0 开始;告警状态重置 |
| 管理员“重置本期流量” | `PerRx=PerTx=0`,`TrafficMonthly` 当前行清零,`UpdatedAt=now` | 基线不变 |
| 节点禁用 | Agent 401,不再有心跳 | 基线保留;启用后 `cur ≥ prev` 精确补回 |

### 4.4 账期计算(重置日 + 月末钳制 + 时区)

```
DateOnly PeriodStart(DateOnly today, int resetDay)
{
    int d = Math.Min(resetDay, DateTime.DaysInMonth(today.Year, today.Month));
    if (today.Day >= d) return new DateOnly(today.Year, today.Month, d);
    var pm = today.AddMonths(-1);
    int d2 = Math.Min(resetDay, DateTime.DaysInMonth(pm.Year, pm.Month));
    return new DateOnly(pm.Year, pm.Month, d2);
}

DateOnly PeriodEnd(DateOnly start, int resetDay)        // 不含
{
    var nm = start.AddMonths(1);                        // AddMonths 自动钳制到月末
    int d = Math.Min(resetDay, DateTime.DaysInMonth(nm.Year, nm.Month));
    return new DateOnly(nm.Year, nm.Month, d);
}

void EnsurePeriod(Node n, TrafficRuntime rt, DateOnly today)
{
    var start = PeriodStart(today, n.TrafficResetDay);
    if (start == rt.PeriodStart) return;
    if (rt.PeriodStart != default) CloseMonthlyRow(n.Id, rt);   // Closed=1, 写最终 BilledBytes
    rt.PeriodStart = start; rt.PeriodEnd = PeriodEnd(start, n.TrafficResetDay);
    (rt.PerRx, rt.PerTx) = LoadMonthlyIfExists(n.Id, start);    // Master 重启回到同一账期时恢复
    AlertEngine.ResetTrafficStates(n.Id);                        // TrafficWarn/TrafficExceeded 静默关闭
    rt.Dirty = true;
}
```

示例:`resetDay=31`:1 月 31 日 → 账期 [01-31, 02-28)(平年);2 月 15 日 → [01-31, 02-28);2 月 28 日 → [02-28, 03-31)。`resetDay=30`:2 月 → 起点 2 月 28/29 日,终点 3 月 30 日。日流量 `Date` 与账期均使用节点时区(`Nodes.TimeZoneId` → `site.timeZone` → UTC)。一次 2 s 心跳跨越本地零点时整笔计入接收时刻所在日(误差 ≤ 一个心跳)。

### 4.5 持久化时机

- `MinuteFlushService` 每 60 s 把 `Dirty` 的 `TrafficRuntime` 写 `TrafficState` + upsert `TrafficDaily(Day)` + upsert `TrafficMonthly(PeriodStart)`(`BilledBytes`、`LimitBytes`、`CountMode`、`ResetDay` 一起刷新)。
- 最坏丢失:Master 崩溃时最多 60 s 的日/账期累加值;基线 `PrevRx/PrevTx` 同样最多回退 60 s,重启后 `cur ≥ prev` 自动补回 → **账期总量不丢**(唯一不精确的是日/账期边界附近的归属)。

---

## 5. 告警引擎

### 5.1 规则表

| Rule | 条件(`AlertEvaluator` 每 10 s;Expiry 每日) | 阈值键(默认) | 节点覆盖列 | 触发连续次数 | 恢复条件 | 严重级 | `Subject` |
|---|---|---|---|---|---|---|---|
| `Offline`(1) | `Enabled && FirstSeenAt!=null && now−LastSeenAt > offlineSec` | `alert.offlineTimeoutSec`(30) | `OfflineAlertSec` | `alert.offlineConsecutive`(2)→ 实际 ≈ 40–50 s 后通知 | 收到任一上行消息(`Status=Online`)1 次 | Critical | `''` |
| `CpuHigh`(2) | 环形缓冲中最近 60 s 内 ≥ 10 个点的 CPU 均值 ≥ `pct×10` | `alert.cpuPct`(90)、`alert.cpuSustainMin`(5) | `CpuAlertPct` | `cpuSustainMin×6`(=30 次 = 5 min) | 均值 < `(pct−10)×10` 连续 12 次(2 min) | Warning | `''` |
| `TrafficWarn`(3) | `LimitBytes>0 && BilledBytes/LimitBytes ≥ warnPct/100` | `alert.trafficWarnPct`(80) | `TrafficAlertPct` | 1 | 账期切换(静默)或比例回落 < 阈值(限额被调大)→ 恢复通知 | Warning | `PeriodStart` |
| `TrafficExceeded`(4) | `BilledBytes ≥ LimitBytes` | — | — | 1 | 同上 | Critical | `PeriodStart` |
| `Expiry`(5) | `ExpiresAt!=null && (ExpiresAt − todayLocal).Days ≤ expiryDays`(每日 `expiryCheckHour`) | `alert.expiryDays`(7)、`alert.expiryCheckHour`(9) | — | 1 | `ExpiresAt` 被改到 `> today + expiryDays`(续费)→ 恢复通知“已续费” | Warning(≤7 d);≤1 d 或已过期升级为 Critical 并**再通知一次**(`Subject` 追加 `:critical`) | `ExpiresAt` |
| `DiskHigh`(6,默认关闭) | 任一挂载 `Used/Total ≥ pct` | `alert.diskEnabled`(false)、`alert.diskPct`(90) | `DiskAlertPct` | 3(30 s) | `< pct−5` 连续 3 次 | Warning | 挂载点 |

全局开关 `alert.enabled`;节点开关 `AlertsEnabled`;二者任一关闭时状态机仍运行(便于后台看状态),但不创建通知任务(`Notified=false`)。

### 5.2 状态机

```
Evaluate(node, rule, subject, cond, value, now):
  st = States.GetOrAdd((node.Id, rule, subject))
  st.LastValue = value
  switch (st.State)
    Normal:
      if cond: st.State = Pending; st.Consecutive = 1; TryFire()
    Pending:
      if cond: st.Consecutive++; TryFire()
      else:    st.State = Normal; st.Consecutive = 0
    Firing:
      if !cond: st.ResolveCount++; if st.ResolveCount >= rule.ResolveConsecutive: Resolve()
      else:     st.ResolveCount = 0; MaybeRepeat()

  TryFire():
    if st.Consecutive < rule.Consecutive: return
    st.State = Firing; st.FiringSince = now; st.ResolveCount = 0
    ev = AlertEvents.Insert(Firing, node snapshot, rule, subject, title/message 由 AlertTexts 生成, value, threshold, DedupKey)
    canNotify = settings.alert.enabled && node.AlertsEnabled && (st.CooldownUntil is null || now >= st.CooldownUntil)
    ev.Notified = canNotify
    if canNotify: st.LastNotifiedAt = now; st.CooldownUntil = now + alert.cooldownMin; Dispatcher.Enqueue(ev, kind=Firing)
    st.OpenEventId = ev.Id; AdminHub.Broadcast(alert, ev)

  Resolve(silent=false):
    ev = AlertEvents[st.OpenEventId]; ev.Status = Resolved; ev.ResolvedAt = now
    if ev.Notified && !silent: Dispatcher.Enqueue(ev, kind=Recovery)      // 恢复通知不受冷却限制
    st.State = Normal; st.Consecutive = 0; st.ResolveCount = 0; st.OpenEventId = null
    AdminHub.Broadcast(alert, ev)

  MaybeRepeat():
    if alert.repeatMin > 0 && ev.Notified && now − st.LastNotifiedAt >= repeatMin: 再次通知(kind=Firing, 标题加“仍在持续”), st.LastNotifiedAt = now
```

- 冷却语义(PRD “告警发送后进入 30–60 分钟静默冷却期;恢复正常后发送恢复通知”):`alert.cooldownMin` 校验范围 30–60。冷却只抑制**同一 (node, rule, subject) 的新触发通知**;冷却期内触发的事件照常入库(`Notified=false`),其恢复也静默,避免“只见恢复不见触发”。
- 启动恢复:从 `AlertStates` 加载;`Firing` 状态不重复通知;`CooldownUntil` 继续有效。
- 去重键:`DedupKey = $"{rule}:{nodeId}:{subject}"`;同一键同时最多一个 `Status=Firing` 事件(代码保证,`OpenEventId`)。
- 节点删除:状态与事件按外键处理(事件保留、`NodeId` 置空)。

### 5.3 通知文本(`AlertTexts`,中文,Telegram 用 HTML)

```
🔴 <b>[离线] HK-Node-01</b>
节点已离线 48 秒(最后上报 2026-09-07 10:12:34 +08:00)
备注:核心 DB-勿动
时间:2026-09-07 10:13:22 +08:00
```
```
🟢 <b>[恢复] HK-Node-01</b>
节点恢复在线,离线时长 3 分 12 秒
时间:2026-09-07 10:16:34 +08:00
```
其余规则标题:`[CPU 高负载]`、`[流量预警]`(“本账期已用 812.3 GB / 1000 GB(81.2%),账期 09-01 ~ 09-30”)、`[流量超限]`、`[即将到期]`(“将于 2026-09-14 到期,剩余 7 天;供应商 BandwagonHost,续费 $49.99/年”)、`[已过期]`、`[磁盘告急]`。时间按站点时区格式化。Webhook 载荷中同时给出结构化字段与该文本(`text`)。

### 5.4 投递(`NotificationDispatcher`)

- 事件 → 对每个 `Enabled` 且 `(RuleMask==0 || RuleMask & (1<<rule)) && Severity ≥ MinSeverity` 的渠道生成任务;`Channel<Job>` 单消费者,渠道之间并行度 4。
- 重试:3 次,间隔 5 s / 30 s / 120 s;每次写 `NotificationDeliveries`;最终失败写 `Channels.LastError`。
- Telegram:`POST https://api.telegram.org/bot{token}/sendMessage`,JSON `{chat_id, text, parse_mode, disable_web_page_preview:true, disable_notification, message_thread_id?}`;成功判定 `HTTP 2xx && ok==true`;HTML 转义节点名/备注。
- Webhook:`POST url`,头 `Content-Type: application/json`、`User-Agent: snm-master/{version}`、`X-SNM-Event: alert.firing|alert.resolved|test`、`X-SNM-Delivery: {guid}`、`X-SNM-Timestamp: {unix}`、若 `secret` 非空 `X-SNM-Signature: sha256={HMAC-SHA256(secret, timestamp + "." + body) hex}`;自定义 `headers` 追加;超时 `timeoutSec`;成功 = 2xx。默认载荷:

```json
{
  "event": "alert.firing",
  "id": 1024,
  "rule": "offline",
  "severity": "critical",
  "status": "firing",
  "title": "[离线] HK-Node-01",
  "message": "节点已离线 48 秒(最后上报 2026-09-07 10:12:34 +08:00)",
  "text": "🔴 [离线] HK-Node-01\n节点已离线 48 秒 ...",
  "value": 48,
  "threshold": 30,
  "node": { "id": 12, "name": "HK-Node-01", "remark": "核心 DB-勿动", "countryCode": "HK" },
  "startedAt": "2026-09-07T02:13:22Z",
  "resolvedAt": null,
  "site": { "title": "Server Node Monitor", "url": "https://m.example.com/admin/" }
}
```
`bodyTemplate` 非空时以模板替换占位符 `{{event}} {{id}} {{rule}} {{severity}} {{status}} {{title}} {{message}} {{text}} {{value}} {{threshold}} {{node.id}} {{node.name}} {{node.remark}} {{node.countryCode}} {{startedAt}} {{resolvedAt}} {{site.title}} {{site.url}}`,替换值做 JSON 字符串转义(不含外层引号,模板作者自己加引号)。

### 5.5 到期巡检(`ExpiryCheckService`)

每日站点时区 `alert.expiryCheckHour` 整点(启动时若当天未跑则 10 s 后补跑;`Settings` 记 `alert.lastExpiryCheckDate`):对每个 `Enabled` 且 `ExpiresAt` 非空的节点计算 `daysLeft = ExpiresAt − todayLocal`;`daysLeft ≤ expiryDays` → `Evaluate(Expiry, subject=ExpiresAt, cond=true, value=daysLeft)`;`daysLeft ≤ 1` → 额外 `Evaluate(Expiry, subject=ExpiresAt+":critical")`;否则对该节点所有 Expiry 状态 `Evaluate(cond=false)`(续费后触发恢复)。管理员保存节点时若 `ExpiresAt` 变化,立即对该节点执行一次同样逻辑。

---

## 6. 设置键(`Settings` 表;`SettingsService` 内存缓存 + 变更事件)

| 键 | 类型 | 默认 | 校验 | 生效方式 |
|---|---|---|---|---|
| `site.title` | string | `Server Node Monitor` | 1–64 | 即时 |
| `site.publicTitle` | string | `节点状态` | 1–64 | 推送 `nodes`/下次快照 |
| `site.publicSubtitle` | string | `` | ≤ 128 | 同上 |
| `site.publicBaseUrl` | string | `` | 合法 http(s) origin 或空(空时用请求的 `X-Forwarded-Proto`/`Host` 推导) | 安装脚本生成 |
| `site.timeZone` | string | 首次启动 = 服务器本地时区的 IANA 名(`TimeZoneInfo.Local.Id`,Windows 名经 `TryConvertWindowsIdToIanaId`),失败 `UTC` | 有效 IANA | 账期/日流量/通知时间 |
| `public.showSpecs` | bool | true | | 快照 |
| `public.showTraffic` | bool | true | | 快照 |
| `agent.releaseBaseUrl` | string | `https://github.com/OWNER/server-node-monitor/releases/latest/download` | http(s) URL,无尾 `/` | 安装脚本 |
| `agent.defaultIntervalMs` | int | 2000 | 1000–60000 | 新节点默认 |
| `agent.statusIntervalSec` | int | 300 | 60–3600 | 下次 register / 立即 `configure` 全部在线节点 |
| `agent.installTokenTtlHours` | int | 24 | 1–168 | |
| `alert.enabled` | bool | true | | 即时 |
| `alert.offlineTimeoutSec` | int | 30 | 10–600 | 即时 |
| `alert.offlineConsecutive` | int | 2 | 1–10 | |
| `alert.cpuPct` | int | 90 | 50–100 | |
| `alert.cpuSustainMin` | int | 5 | 1–60 | |
| `alert.trafficWarnPct` | int | 80 | 50–99 | |
| `alert.expiryDays` | int | 7 | 1–60 | |
| `alert.expiryCheckHour` | int | 9 | 0–23 | |
| `alert.cooldownMin` | int | 30 | 30–60 | |
| `alert.repeatMin` | int | 0 | 0 或 30–1440 | |
| `alert.diskEnabled` | bool | false | | |
| `alert.diskPct` | int | 90 | 50–100 | |
| `alert.lastExpiryCheckDate` | string | `` | 内部 | |
| `finance.baseCurrency` | string | `CNY` | USD/CNY/EUR | 仪表盘 |
| `finance.rates` | object | `{"CNY":1,"USD":7.2,"EUR":7.8}` | 三键均 > 0 | 仪表盘 |
| `auth.jwtSecret` | string | 首次启动生成 64 B 随机 base64 | **不经 API 暴露**;env `SNM_JWT_SECRET` 优先 | 重启后仍有效 |
| `auth.accessTokenMinutes` | int | 120 | 5–1440 | 新令牌 |
| `auth.refreshTokenDays` | int | 30 | 1–365 | |
| `auth.loginMaxFailures` | int | 5 | 3–20 | |
| `auth.loginLockMinutes` | int | 15 | 1–1440 | |
| `geoip.enabled` | bool | true | | |
| `geoip.lastRefreshUtc` / `geoip.ipv4Rows` / `geoip.ipv6Rows` / `geoip.lastError` | 状态 | | 只读 | |
| `retention.alertEventDays` | int | 180 | 7–3650 | |
| `retention.deliveryDays` | int | 30 | 7–365 | |
| `retention.trafficDailyDays` | int | 400 | 31–3650 | |

时序保留(25 h / 8 d / 31 d)为代码常量,不可配置(BRIEF 锁定)。

---

## 7. GeoIP 数据与覆盖

- 数据集:`asn-country-ipv4-num.csv`(每行 `start,end,CC`,32 位十进制)、`asn-country-ipv6-num.csv`(128 位十进制),已按 `start` 升序。
- 加载:解析为 `uint[] StartV4, EndV4; string[] CcV4`(共享字符串池,CC 仅 ~250 个)与 `UInt128[]` 版本;二分查找最后一个 `Start ≤ ip`,校验 `ip ≤ End`。IPv4 映射的 IPv6(`::ffff:a.b.c.d`)按 v4 查;私网/保留地址跳过。
- 刷新:`GeoIpRefreshService` 每 6 h 检查 `meta.json.downloadedAtUtc` 是否 ≥ 7 d(或文件缺失)→ 下载到 `*.tmp` → 解析验证(行数 > 100 000 / > 10 000)→ 原子 `File.Move(overwrite)` → 热切换内存数组 → 写 `meta.json` 与 `geoip.*` 设置。失败只写 `geoip.lastError`,不影响启动。
- 触发查询的时机:Agent 连接建立(`LastRemoteIp`)、`status`/`register` 携带的新公网 IP、GeoIP 数据加载完成后对所有 `CountryCodeAuto` 为空的节点补查。
- 覆盖:`Nodes.CountryCodeOverride`(REST 可设/清);展示值 `Override ?? Auto ?? ""`。

---

## 8. 容量估算与索引理由

| 表 | 100 节点行数 | 行大小 | 体积 |
|---|---|---|---|
| `Metrics1m` | ≤ 150 000 | ~110 B(+ PK 索引 ~30 B) | ~20 MB |
| `Metrics1h` | ≤ 19 200 | | ~2.5 MB |
| `Metrics1d` | ≤ 3 100 | | < 0.5 MB |
| `TrafficDaily` | 36 500 / 年 | ~60 B | ~3 MB / 年 |
| 其余 | 千行级 | | 忽略 |

写入速率:每分钟 1 个事务,≈ 100 行 `Metrics1m` + ≈ 300 行 upsert,SQLite 轻松承受。索引最小化:大表仅主键 + `Ts`;`AlertEvents` 三个索引覆盖列表页的过滤/排序。

---

## 9. 一致性与并发规则(实现者清单)

1. 内存为实时真相(`NodeRegistry`),数据库为持久快照;REST 读取节点“实时值”时**合并**内存快照,不读 `Nodes.Status` 等回写列(它们最多滞后 60 s,仅供重启恢复与离线时展示)。
2. 所有后台写事务持 `DbWriteLock`;单事务不超过 5 000 行(分批)。
3. 节点删除:持锁 → 事务内删 `Metrics*`/`TrafficDaily`/`TrafficMonthly`/`Nodes`(级联其余)→ 提交 → 从 `NodeRegistry` 移除 → `Abort()` 其连接 → 推送 `nodes`/`nodesChanged`。
4. 密钥轮换:事务更新 `AgentKey`/`KeyRotatedAt`、删除 `InstallTokens` → 刷新认证字典 → `Abort()` 旧连接。
5. 设置变更:写库 → 更新缓存 → 触发 `SettingsChanged(keys)`;告警阈值即时生效于下一次评估;`agent.statusIntervalSec` 变化向所有在线 Agent 推送 `configure`。
6. 时序与流量表不建外键、不做级联,由服务层负责一致性;单元测试 `NodeDeleteCascadeTests` 断言删除后各表无残留。
