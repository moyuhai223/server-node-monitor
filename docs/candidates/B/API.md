# API.md — REST 接口全清单(候选设计 B)

> 所有 REST 由 `SNM.Master` 以 Minimal API 实现;前端 `web/admin` 的 axios `baseURL='/api'`。本文的路径均已含 `/api` 前缀(公开静态端点除外)。

## 1. 通用约定

| 项 | 约定 |
|---|---|
| 响应包 | 成功 `{"code":0,"message":"OK","data":<any>}`;失败 `{"code":<int>,"message":"<中文>","data":null}`。`code` 非 0 一律为失败;HTTP 状态码与 `code` 同步(4xx/5xx),业务码(1xxxx)用 HTTP 400/401 |
| 内容类型 | `application/json; charset=utf-8`;JSON camelCase,`null` 字段省略(`WhenWritingNull`),枚举为字符串,时间为 ISO-8601 UTC 字符串(`2026-09-07T02:15:30Z`)或明确标注的 Unix 毫秒 |
| 鉴权 | `Authorization: Bearer <accessToken>`;除 §2.1/§2.2、§9 外全部必需;缺失/无效 → `401 {"code":401,"message":"未登录或登录已过期"}`;过期 → `code=11007`(前端弹“重新登录”);`TokenVersion` 不符 → `code=11008` |
| 分页 | 查询参数 `pageNo`(默认 1)、`pageSize`(默认 10,最大 200);响应 `data:{"pageData":[...],"total":<int>}`(**与模板 `MeCrud` 契约一致**) |
| 排序 | 查询参数 `sort=field,asc|desc`(仅在标注支持的端点) |
| 校验错误 | `422 {"code":422,"message":"PublicName 不能为空","data":{"errors":{"publicName":["不能为空"]}}}`(message 取第一条) |
| 冲突 | `409 {"code":409,"message":"..."}` |
| 未找到 | `404 {"code":404,"message":"节点不存在"}` |
| 限流 | `429 {"code":429,"message":"请求过于频繁"}`,登录 10 次/分钟/IP |
| 服务器错误 | `500 {"code":500,"message":"服务器发生异常"}`(不泄露堆栈;日志含 TraceId,响应头 `X-Trace-Id`) |
| 幂等 | PUT/PATCH/DELETE 幂等;POST 创建非幂等 |
| PATCH 语义 | JSON 中出现的键才更新;显式 `null` 表示清空(可空列) |
| 字节/MB | API 中的容量字段一律 **字节**(`long`),前端格式化;仅 `*Mb` 后缀字段为 MiB |

## 2. 模板兼容接口(vue-naive-admin 2.x 期望)

### 2.1 `POST /api/auth/login`(匿名,限流)

请求:`{"username":"admin","password":"secret","captcha":"","isQuick":false}`(`captcha/isQuick` 接受但忽略;登录页已删除验证码)。

响应:`{"code":0,"message":"OK","data":{"accessToken":"eyJ...","refreshToken":"5f0c...(43字符)","expiresIn":7200,"tokenType":"Bearer"}}`

错误:`400 {"code":10001,"message":"用户名或密码错误"}`(用户不存在与密码错误同文案,恒定耗时 ≥ 200 ms)。

### 2.2 `POST /api/auth/refresh`(匿名)

请求 `{"refreshToken":"5f0c..."}` → 响应同登录(新 access + **新** refresh,旧 refresh 作废)。错误:`401 {"code":11008,"message":"刷新令牌无效"}`。模板原 `GET /auth/refresh/token` 不实现(前端 `src/api/index.js` 改为本端点,见 FRONTEND.md)。

### 2.3 `POST /api/auth/logout`(JWT)

请求体任意(模板发 `{}`);撤销该用户全部刷新令牌;响应 `{"code":0,"message":"OK","data":null}`。

### 2.4 `POST /api/auth/password`(JWT)

请求 `{"oldPassword":"...","newPassword":"..."}`;新密码 8..64 字符。成功后 `TokenVersion++`(所有会话失效,前端随后收到 11008 → 重新登录)。错误:`400 {"code":10002,"message":"原密码错误"}`。

### 2.5 `GET /api/user/detail`(JWT)

```json
{"code":0,"message":"OK","data":{
  "id":1,"username":"admin","enable":true,"createTime":"2026-09-01T00:00:00Z","updateTime":"2026-09-01T00:00:00Z",
  "profile":{"id":1,"userId":1,"nickName":"admin","gender":0,"avatar":"/admin/avatar.svg","address":"","email":""},
  "roles":[{"id":1,"code":"SUPER_ADMIN","name":"超级管理员","enable":true}],
  "currentRole":{"id":1,"code":"SUPER_ADMIN","name":"超级管理员","enable":true}}}
```

(`store/helper.js getUserInfo` 只读 `id/username/profile/roles/currentRole`;单角色 → 头像菜单不显示“切换角色”。)

### 2.6 `GET /api/role/permissions/tree`(JWT)

返回**服务端静态**菜单树(与模板 `permission.setPermissions` 结构一致;`component` 必须存在于 `web/admin/src/views/**`):

```json
{"code":0,"message":"OK","data":[
 {"id":1,"code":"Home","name":"总览","type":"MENU","path":"/","component":"/src/views/dashboard/index.vue","icon":"i-fe:home","layout":"","keepAlive":false,"enable":true,"show":true,"order":1,"children":[]},
 {"id":2,"code":"Monitor","name":"探针视图","type":"MENU","path":"/monitor","component":"/src/views/monitor/index.vue","icon":"i-fe:activity","layout":"","keepAlive":true,"enable":true,"show":true,"order":2,"children":[
   {"id":21,"code":"MonitorDetail","name":"节点详情","type":"MENU","parentId":2,"path":"/monitor/detail","component":"/src/views/monitor/detail.vue","icon":"i-fe:activity","layout":"","keepAlive":false,"enable":true,"show":false,"order":1}]},
 {"id":3,"code":"Nodes","name":"节点配置","type":"MENU","path":"/nodes","component":"/src/views/nodes/index.vue","icon":"i-fe:server","layout":"","keepAlive":true,"enable":true,"show":true,"order":3,"children":[
   {"id":31,"code":"AddNode","name":"新增节点","type":"BUTTON","parentId":3,"enable":true,"show":true,"order":1},
   {"id":32,"code":"EditNode","name":"编辑节点","type":"BUTTON","parentId":3,"enable":true,"show":true,"order":2},
   {"id":33,"code":"DeleteNode","name":"删除节点","type":"BUTTON","parentId":3,"enable":true,"show":true,"order":3}]},
 {"id":4,"code":"Traffic","name":"流量统计","type":"MENU","path":"/traffic","component":"/src/views/traffic/index.vue","icon":"i-fe:bar-chart-2","layout":"","keepAlive":false,"enable":true,"show":true,"order":4,"children":[]},
 {"id":5,"code":"AlertCenter","name":"告警中心","type":"MENU","path":"","component":"","icon":"i-fe:bell","layout":"","keepAlive":false,"enable":true,"show":true,"order":5,"children":[
   {"id":51,"code":"AlertEvents","name":"告警记录","type":"MENU","parentId":5,"path":"/alerts","component":"/src/views/alerts/index.vue","icon":"i-fe:alert-circle","layout":"","keepAlive":false,"enable":true,"show":true,"order":1},
   {"id":52,"code":"AlertChannels","name":"通知渠道","type":"MENU","parentId":5,"path":"/alerts/channels","component":"/src/views/alerts/channels.vue","icon":"i-fe:send","layout":"","keepAlive":false,"enable":true,"show":true,"order":2}]},
 {"id":6,"code":"Settings","name":"系统设置","type":"MENU","path":"/settings","component":"/src/views/settings/index.vue","icon":"i-fe:settings","layout":"","keepAlive":false,"enable":true,"show":true,"order":6,"children":[]},
 {"id":9,"code":"Profile","name":"个人资料","type":"MENU","path":"/profile","component":"/src/views/profile/index.vue","icon":"i-fe:user","layout":"","keepAlive":false,"enable":true,"show":false,"order":99,"children":[]}
]}
```

`Home` 与模板 `basic-routes.js` 的 `Home` 同名,守卫 `!router.hasRoute` 不会重复注册;`MonitorDetail` 隐藏(`show:false`)但注册路由,侧栏高亮父项。按钮码用于 `v-permission`。

### 2.7 `GET /api/permission/menu/validate?path=/nodes`(JWT)

`data: true|false` —— 路径是否存在于上表任一 `enable:true` 的 MENU(前缀精确匹配)。守卫据此区分 403/404。

### 2.8 不实现的模板接口

`GET /auth/captcha`、`POST /auth/current-role/switch/{role}`、`PATCH /user/profile/{id}`、`/user`、`/role/*`、`/permission/*`(除 2.6/2.7)——对应页面/入口在 FRONTEND.md §2 中删除或隐藏;若被调用返回 `404 {"code":404}`。

## 3. 总览

### 3.1 `GET /api/dashboard/summary`

```json
{"code":0,"message":"OK","data":{
  "serverTime":"2026-09-07T02:15:30Z",
  "nodes":{"total":12,"enabled":11,"online":10,"offline":1,"neverSeen":0},
  "alerts":{"firing":2,"last24h":5},
  "expiring":{"within7d":1,"within30d":3,"expired":0},
  "mrr":{"baseCurrency":"CNY","total":1268.40,"byCurrency":[{"currency":"USD","monthly":120.50,"inBase":867.60},{"currency":"CNY","monthly":400.80,"inBase":400.80}]},
  "traffic":{"nodesWithLimit":6,"over80":1,"totalCountedBytes":1234567890123},
  "connections":{"agents":10,"browsers":3}}}
```

`mrr.byCurrency[].monthly = Σ RenewPrice/BillingCycleMonths`(仅 `Enabled` 且价格非空节点),`inBase = monthly × finance.fxRates[currency]`(无汇率 → `inBase:null` 且不计入 total)。

### 3.2 `GET /api/dashboard/expiring?days=30`

`data:[{"id":3,"publicName":"HK-Node-01","adminRemark":"核心 DB","provider":"DMIT","expiresAt":"2026-09-12T00:00:00Z","daysLeft":5,"renewPrice":9.90,"currency":"USD","billingCycleMonths":1,"autoRenew":false}]`,按 `daysLeft` 升序(含已过期,负数)。

### 3.3 `GET /api/dashboard/alerts/recent?limit=10` → `data: AlertEvent[]`(§7.1 结构),按 `firedAt` 降序。

## 4. 节点

### 4.1 数据结构

**NodeConfig(请求体,POST 全量 / PATCH 部分)**

| 字段 | 类型 | 必填(POST) | 校验 | 说明 |
|---|---|---|---|---|
| publicName | string | 是 | 1..64 | |
| adminRemark | string? | 否 | ≤512 | |
| groupName | string? | 否 | ≤64 | |
| sortOrder | int | 否(0) | | |
| enabled | bool | 否(true) | | |
| trafficLimitBytes | long | 否(0) | ≥0 | 0 不限 |
| trafficResetDay | int | 否(设置默认) | 1..31 | |
| trafficCountMode | string | 否(`sum`) | `sum\|rx\|tx\|max` | |
| timeZoneId | string? | 否 | 合法 IANA | null=全局 |
| provider | string? | 否 | ≤64 | |
| renewPrice | decimal? | 否 | ≥0 | |
| currency | string? | 否 | 3 字母 | |
| billingCycleMonths | int? | 否 | 1..120 | |
| purchasedAt | date? | 否 | `YYYY-MM-DD` | |
| expiresAt | date? | 否 | `YYYY-MM-DD` | |
| autoRenew | bool | 否(false) | | |
| countryCodeOverride | string? | 否 | 2 字母大写或 null | |
| alertOverrides | object? | 否 | 键见 DATA.md §6.2 | |

**Node(响应)** = NodeConfig 字段 + 只读:

```json
{"id":1,"publicName":"HK-Node-01","adminRemark":"核心 DB-勿动","groupName":"HK","sortOrder":0,"enabled":true,
 "agentKey":"Zm9vYmFy...43chars","agentKeyRotatedAt":null,
 "trafficLimitBytes":1099511627776,"trafficResetDay":1,"trafficCountMode":"sum","timeZoneId":null,
 "provider":"DMIT","renewPrice":9.90,"currency":"USD","billingCycleMonths":1,"purchasedAt":"2025-09-12","expiresAt":"2026-09-12","autoRenew":false,
 "countryCodeOverride":null,"alertOverrides":null,
 "inventory":{"hostname":"hk1","os":"Ubuntu 24.04.2 LTS","kernel":"6.8.0-45-generic","arch":"x64","cpuModel":"Intel(R) Xeon(R) Platinum 8375C","cpuCores":2,"memTotalBytes":2147483648,"swapTotalBytes":0,"diskTotalBytes":42949672960,"agentVersion":"1.0.0","interfaces":["eth0"],"registeredAt":"2026-09-01T00:00:00Z"},
 "network":{"publicIp":"203.0.113.10","countryCodeAuto":"HK","countryCode":"HK","ips":[{"ip":"203.0.113.10","v":4,"src":"server","public":true},{"ip":"10.0.0.5","v":4,"src":"agent","public":false},{"ip":"2001:db8::1","v":6,"src":"agent","public":true}]},
 "runtime":{"online":true,"lastSeen":"2026-09-07T02:15:28Z","lastConnected":"2026-09-06T22:00:01Z","lastDisconnected":null,"uptimeSec":864000,
            "cpuPermille":235,"memUsedBytes":1234567890,"swapUsedBytes":0,"diskUsedBytes":21474836480,"load1":87,"rxBps":123456,"txBps":65432,"rxTotalBytes":123456789012,"txTotalBytes":98765432109},
 "traffic":{"periodStart":"2026-09-01T00:00:00+08:00","periodEnd":"2026-10-01T00:00:00+08:00","rxBytes":100,"txBytes":200,"countedBytes":300,"limitBytes":1099511627776,"pct":0.0,"daysLeft":24},
 "disks":[{"mount":"/","fsType":"ext4","totalBytes":42949672960,"usedBytes":21474836480,"updatedAt":"2026-09-07T02:15:00Z"}],
 "firing":["cpu"],
 "createdAt":"2026-08-01T00:00:00Z","updatedAt":"2026-09-01T00:00:00Z"}
```

### 4.2 端点

| 方法/路径 | 说明 | 请求 | 响应 `data` |
|---|---|---|---|
| `GET /api/nodes` | 分页列表(配置+运行态) | `pageNo,pageSize,keyword`(匹配 publicName/adminRemark/hostname/ip),`online=true\|false`,`enabled`,`group`,`sort=publicName\|sortOrder\|expiresAt\|lastSeen,asc\|desc`(默认 `sortOrder,asc`) | `{pageData: Node[], total}`(列表项省略 `disks`、`network.ips` 仅前 3 个 + `ipCount`) |
| `GET /api/nodes/all` | 下拉用 | 无 | `[{id, publicName, groupName, enabled, online}]` |
| `GET /api/nodes/groups` | 分组名列表 | | `["HK","US"]` |
| `POST /api/nodes` | 创建(生成 AgentKey) | NodeConfig | Node(201) |
| `GET /api/nodes/{id}` | 详情 | | Node |
| `PATCH /api/nodes/{id}` | 部分更新;`enabled=false` 会断开探针;`trafficResetDay/timeZoneId` 变化触发换周期(DATA.md §5.4);保存后立即评估 expiry 规则 | NodeConfig 子集 | Node |
| `DELETE /api/nodes/{id}` | 删除(级联),断开探针 | | null |
| `POST /api/nodes/{id}/rotate-key` | 轮换 AgentKey,踢掉当前连接 | | `{agentKey, rotatedAt}` |
| `GET /api/nodes/{id}/install-command` | 生成安装命令 | | 见 4.3 |
| `POST /api/nodes/{id}/traffic/reset` | 手工开始新账期 | | Node.traffic |
| `POST /api/nodes/{id}/alerts/test?rule=offline` | 以该节点为对象发送一条测试告警到所有渠道(`event:"test"`) | | `{sent:2, failed:0}` |

### 4.3 `GET /api/nodes/{id}/install-command`

```json
{"code":0,"message":"OK","data":{
 "serverUrl":"https://m.example.com",
 "linux":"curl -fsSL https://m.example.com/install.sh | sudo bash -s -- --server https://m.example.com --key Zm9v... --name hk1",
 "linuxUninstall":"curl -fsSL https://m.example.com/install.sh | sudo bash -s -- uninstall",
 "linuxManual":"sudo SNM_SERVER=https://m.example.com SNM_KEY=Zm9v... bash install-agent.sh",
 "windows":"powershell -NoProfile -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create((irm https://m.example.com/install.ps1))) -Server https://m.example.com -Key Zm9v... -Name hk1\"",
 "windowsUninstall":"powershell -NoProfile -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create((irm https://m.example.com/install.ps1))) -Uninstall\"",
 "releaseBaseUrl":"https://github.com/OWNER/REPO/releases/latest/download",
 "notes":["需要 root/管理员权限","脚本会创建系统用户 snm-agent 并写入 /etc/snm-agent/agent.env(0600)"]}}
```

`serverUrl` = 设置 `general.masterPublicUrl` → `Snm:PublicUrl` → 请求的 `X-Forwarded-Proto://Host`。可选查询 `?proxy=socks5://10.0.0.1:1080` 把 `--proxy` 追加到命令;`?name=` 覆盖 `--name`(默认 hostname 或 publicName)。

## 5. 性能历史

### 5.1 `GET /api/nodes/{id}/metrics?range=24h`

参数:`range=24h|7d|30d`(必填其一)或 `from`/`to`(Unix 毫秒,自动选层:跨度 ≤26h→1m,≤8d→1h,否则 1d;最多返回 2000 点)。

```json
{"code":0,"message":"OK","data":{
 "nodeId":1,"range":"24h","stepSec":60,"from":1757116800000,"to":1757203200000,
 "points":[
  {"t":1757116800000,"cpuAvg":235,"cpuMax":610,"memUsedAvg":1234567890,"memUsedMax":1300000000,"swapUsedAvg":0,"diskUsedAvg":21474836480,
   "load1Avg":87,"load1Max":150,"rxAvgBps":123456,"rxMaxBps":900000,"txAvgBps":65432,"txMaxBps":400000,"rxBytes":7407360,"txBytes":3925920,"samples":30}
 ],
 "totals":{"rxBytes":123456789,"txBytes":98765432}}}
```

`cpu*` 为 ‰;`memUsed*`/`diskUsedAvg` 字节;`load1*` ×100 或 null。缺桶不补点。

### 5.2 `GET /api/nodes/{id}/metrics/latest` → `data: Node.runtime`(同 §4.1)。

### 5.3 `GET /api/nodes/{id}/wave` → `data:{points:[{t,cpu,mem,rx,tx}]}`(内存环形缓冲,最多 90 点;用于详情页首屏,之后由 AdminHub 推送)。

## 6. 流量

### 6.1 `GET /api/nodes/{id}/traffic?months=6&days=31`

```json
{"code":0,"message":"OK","data":{
 "current":{"periodStart":"2026-09-01T00:00:00+08:00","periodEnd":"2026-10-01T00:00:00+08:00","rxBytes":100,"txBytes":200,"countedBytes":300,"limitBytes":1099511627776,"pct":0.0,"countMode":"sum","resetDay":1,"timeZoneId":"Asia/Shanghai","daysLeft":24,"projectedBytes":390},
 "history":[{"periodStart":"2026-08-01T00:00:00+08:00","periodEnd":"2026-09-01T00:00:00+08:00","rxBytes":1,"txBytes":2,"countedBytes":3,"limitBytes":1099511627776,"pct":0.0,"closedReason":"rollover"}],
 "daily":[{"date":"2026-09-06","rxBytes":10,"txBytes":20}]}}
```

`projectedBytes = countedBytes / 已过秒数 × 周期总秒数`(已过 < 1h 时为 null)。`history` 为已关闭周期,按开始时间降序,最多 `months`(默认 6,最大 24);`daily` 最近 `days`(默认 31,最大 400)天,含无数据日(0)。

### 6.2 `GET /api/traffic/overview?pageNo&pageSize&sort=pct,desc` → `{pageData:[{id,publicName,groupName,online,limitBytes,countedBytes,rxBytes,txBytes,pct,periodEnd,daysLeft,resetDay}],total}`(仅 `Enabled`;`limitBytes=0` 的 `pct:null` 排最后)。

## 7. 告警

### 7.1 事件

```json
{"id":123,"nodeId":1,"publicName":"HK-Node-01","rule":"offline","ruleName":"离线超时","severity":"critical","status":"firing",
 "firedAt":"2026-09-07T02:15:30Z","resolvedAt":null,"durationSec":null,"value":45,"threshold":30,
 "title":"HK-Node-01 离线超时","message":"已失联 45 秒","notified":true,"notifiedAt":"2026-09-07T02:15:31Z","recoveryNotified":false,"occurrences":1}
```

| 方法/路径 | 说明 | 参数 |
|---|---|---|
| `GET /api/alerts` | 分页事件 | `pageNo,pageSize,nodeId,rule,status=firing\|resolved,severity,from,to`(ISO 或 Unix 毫秒),`sort=firedAt,desc`(默认) |
| `GET /api/alerts/active` | 全部 Firing 事件 | |
| `GET /api/alerts/{id}` | 单条 | |
| `DELETE /api/alerts?before=2026-08-01T00:00:00Z&status=resolved` | 手工清理 | 返回 `{deleted: n}` |
| `GET /api/alerts/rules` | 规则元数据(供设置页与筛选) | `[{key:"offline",name:"离线超时",severity:"critical",unit:"sec",globalKeys:["alert.offlineSec"]}...]` |

### 7.2 渠道

渠道对象:`{"id":1,"name":"运维群","type":"telegram","enabled":true,"minSeverity":"warning","config":{"botToken":"123456:****","chatId":"-100123","threadId":null},"createdAt":"...","updatedAt":"...","lastResult":{"at":"...","success":true,"error":null}}`;`type=webhook` 时 `config:{"url":"https://...","method":"POST","headers":{"Authorization":"****"},"secret":"****","bodyTemplate":null,"contentType":"application/json; charset=utf-8"}`。

| 方法/路径 | 说明 |
|---|---|
| `GET /api/alert-channels` | 列表(不分页) |
| `POST /api/alert-channels` | 创建;校验:telegram 需 botToken(`^\d+:[A-Za-z0-9_-]{30,}$`)与 chatId;webhook 需 http(s) URL |
| `PATCH /api/alert-channels/{id}` | 部分更新;密钥字段传 `****` 或省略 = 保留 |
| `DELETE /api/alert-channels/{id}` | |
| `POST /api/alert-channels/{id}/test` | 发送测试消息 → `{success:true,httpStatus:200,durationMs:340,error:null}`(失败 HTTP 200 + `success:false`,`code=0`) |
| `POST /api/alert-channels/test` | 用请求体中的未保存配置测试(同上响应) |
| `GET /api/alert-channels/{id}/logs?pageNo&pageSize` | NotificationLogs 分页 |

## 8. 设置与系统

### 8.1 `GET /api/settings` → 全部键的**生效值**(未存储用默认),按组:

```json
{"code":0,"message":"OK","data":{
 "general":{"siteTitle":"Server Node Monitor","publicTitle":"节点状态","timeZone":"Asia/Shanghai","masterPublicUrl":""},
 "agent":{"heartbeatSec":2,"ipReportSec":300,"diskReportSec":60},
 "alert":{"offlineSec":30,"cpuPercent":90,"cpuSustainSec":300,"memEnabled":false,"memPercent":90,"diskEnabled":true,"diskPercent":90,"trafficPercent":80,"expiryDays":7,"expiryCheckHour":9,"cooldownMin":30,"retentionDays":90},
 "traffic":{"defaultResetDay":1,"maxPlausibleGbps":40},
 "public":{"enabled":true,"showTraffic":true,"showUptime":true,"showDisk":true},
 "install":{"releaseBaseUrl":"https://github.com/OWNER/REPO/releases/latest/download","agentVersion":"latest"},
 "finance":{"baseCurrency":"CNY","fxRates":{"USD":7.2,"EUR":7.8,"CNY":1}},
 "maintenance":{"vacuumWeekly":false},
 "timeZones":["Asia/Shanghai","Asia/Hong_Kong","Asia/Singapore","Asia/Tokyo","UTC","Europe/London","Europe/Berlin","America/New_York","America/Los_Angeles"]}}
```

### 8.2 `PUT /api/settings`

请求体为上面结构的**任意子集**(按组/键合并),服务端校验范围(DATA.md §7),越界 → 422。写入后:`agent.*` 变化 → 向所有在线探针推 `cfg`;`public.*`/`general.publicTitle` 变化 → 重推大屏 `snapshot`。响应同 8.1。

### 8.3 系统

| 方法/路径 | 说明 | 响应 `data` |
|---|---|---|
| `GET /api/system/info` | 版本与运行状态 | `{"version":"0.1.0+abc123","startedAt":"...","uptimeSec":1234,"dotnet":"10.0.x","os":"Linux 6.8","dataDir":"/var/lib/snm","dbSizeBytes":31457280,"walSizeBytes":4194304,"connections":{"agents":10,"browsers":3},"geoip":{"enabled":true,"loaded":true,"ipv4Rows":300000,"ipv6Rows":150000,"lastUpdatedAt":"...","nextRefreshAt":"..."},"jobs":[{"key":"rollup-1h","lastRun":"..."},...],"counters":{"heartbeatsTotal":123456,"droppedBuckets":0}}` |
| `POST /api/system/geoip/refresh` | 立即下载 GeoIP | `{started:true}`(异步;结果看 info) |
| `POST /api/system/backup` | `VACUUM INTO` 备份 | `{file:"snm-20260907-0215.db","sizeBytes":...}` |
| `GET /api/system/backups` | 备份列表 | `[{file,sizeBytes,createdAt}]` |
| `GET /api/system/logs/notifications?pageNo&pageSize` | 全部渠道通知日志 | 分页 |

## 9. 公开端点(无鉴权)

| 路径 | 说明 |
|---|---|
| `GET /healthz` | 就绪后 `200 text/plain "ok"`;初始化中 503;附头 `X-SNM-Version` |
| `GET /install.sh` | `text/plain; charset=utf-8`,`deploy/install-agent.sh` 模板替换 `__RELEASE_BASE__`(设置 `install.releaseBaseUrl` + `install.agentVersion` 规则)与 `__DEFAULT_SERVER__`(serverUrl);`Cache-Control: no-cache`;`public.enabled` 不影响此端点 |
| `GET /install.ps1` | 同上,Windows 模板 |
| `GET /` `/index.html` `/css/*` `/js/*` | 大屏静态文件;`public.enabled=false` → 404 |
| `/admin/*` | 后台 SPA;未知子路径回退 `admin/index.html` |
| `/hubs/*` | 见 PROTOCOL.md |

## 10. 错误码表

| code | HTTP | 含义 |
|---|---|---|
| 0 | 200/201 | 成功 |
| 400 | 400 | 请求格式错误 |
| 401 | 401 | 未登录/令牌缺失 |
| 403 | 403 | 无权限(预留) |
| 404 | 404 | 资源不存在 |
| 409 | 409 | 冲突(如 PublicName 重复时可选提示;AgentKey 冲突重试生成) |
| 422 | 422 | 校验失败(含 `data.errors`) |
| 429 | 429 | 限流 |
| 500 | 500 | 服务器异常 |
| 10001 | 400 | 用户名或密码错误 |
| 10002 | 400 | 原密码错误 |
| 11007 | 401 | 令牌已过期(前端弹重新登录) |
| 11008 | 401 | 令牌无效/已吊销(改密后) |
| 12001 | 400 | 节点已禁用(探针接入 403 也用此文案) |
| 12002 | 400 | 时区无效 |
| 13001 | 400 | 渠道配置无效 |
| 13002 | 502 | 测试发送失败(仅 `channels/test` 在网络层面完全失败时;HTTP 4xx/5xx 由 `success:false` 表达) |

