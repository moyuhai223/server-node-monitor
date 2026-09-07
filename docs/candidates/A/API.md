# API — 管理端 REST 全清单与 vue-naive-admin 兼容契约(候选方案 A)

> 所有接口位于 `/api/...`,JSON(UTF-8,camelCase)。除标注“公开”外均需 `Authorization: Bearer <accessToken>`。实时数据走 SignalR(见 PROTOCOL.md §5–§6),本文只含 HTTP。

---

## 0. 定案(BRIEF §3 Q6 / Q9)

| # | 定案 | 理由 |
|---|---|---|
| Q6 | **服务端兼容模板期望的登录/用户/权限接口 + 少量前端改造**。服务端实现 `POST /api/auth/login`、`GET /api/user/detail`、`GET /api/role/permissions/tree`、`GET /api/permission/menu/validate`、`POST /api/auth/logout`、`POST /api/auth/password`、`PATCH /api/user/profile/{id}`,并新增 `POST /api/auth/refresh`;响应包 `{code, message, data}`(`code=0` 成功);分页入参 `pageNo/pageSize`、出参 `data.pageData/total`(模板 `MeCrud` 约定)。前端改造清单见 FRONTEND.md §2 | 模板的 `permission-guard`/`store/helper`/`interceptors` 已实现完整的登录态与动态菜单流程,复用成本最低;改造点集中在 `.env`、`vite.config.js`、`http/helpers.js`、`login/index.vue`、`api/index.js`、`settings.js` |
| Q9 | 后台 `GET /api/nodes/{id}/install-script` 返回一次性令牌 URL 与脚本正文;`GET /install/{token}`(公开、24 h 有效、限速)返回渲染好的 bash 脚本,一键命令 `curl -fsSL https://m.example.com/install/<token> \| sudo bash`;脚本模板见 DEPLOY.md §4 | 脚本内含 AgentKey,必须用短期令牌而非 JWT(目标机上没有 JWT) |

---

## 1. 通用约定

### 1.1 响应包

```json
{ "code": 0, "message": "OK", "data": { } }
```

错误:HTTP 状态码 + 同形包,`data` 为 `null` 或校验详情。

```json
{ "code": 400, "message": "参数校验失败", "data": { "errors": { "publicName": ["长度必须在 1–64 之间"], "trafficResetDay": ["必须在 1–31 之间"] } } }
```

| HTTP | `code` | 含义 | 前端处理(模板 `resolveResError`) |
|---|---|---|---|
| 200/201 | 0 | 成功 | |
| 400 | 400 | 参数校验失败 | 提示 `message` |
| 400 | 10001 | 用户名或密码错误 | 提示 |
| 400 | 10002 | 账户已锁定,请 N 分钟后再试 | 提示 |
| 400 | 10010 | 旧密码错误 | 提示 |
| 401 | 401 | access token 缺失/过期/`tv` 版本不匹配 | 拦截器先 `POST /api/auth/refresh` 重试一次,失败则弹“登录已过期” |
| 401 | 10011 | refresh token 无效/已撤销 | 直接登出 |
| 403 | 403 | 无权限(非 admin 角色) | 提示 |
| 404 | 404 / 20002 | 资源不存在 / 节点不存在 | 提示 |
| 409 | 20001 | 节点名称已存在 | 提示 |
| 422 | 20004 | 状态不允许(如对未连接节点推送配置) | 提示 |
| 429 | 429 | 触发限速 | 提示 |
| 502 | 30001 / 30002 | 通知渠道测试失败 / GeoIP 刷新失败(`data.detail` 为上游错误) | 提示 |
| 500 | 500 | 服务器异常(`message` 固定“服务器发生异常”,详情仅日志) | 提示 |

### 1.2 分页

请求:`pageNo`(1 起,默认 1)、`pageSize`(默认 20,最大 200;`0` = 不分页返回全部)。响应:

```json
{ "code": 0, "data": { "pageData": [ ], "total": 137, "pageNo": 1, "pageSize": 20 } }
```

排序参数 `sortBy`(白名单字段)+ `sortDir`(`asc|desc`),仅在标注支持的接口有效。

### 1.3 数据类型

- 时间:ISO 8601 UTC 字符串 `2026-09-07T02:13:22.123Z`;日期:`2026-09-30`。
- 字节:JSON 数字(`long`,< 2^53 安全)。千分比字段名以 `Permille` 结尾或注明。
- Id:整数。国家码:大写两字母或 `""`。

### 1.4 鉴权与限速

- JWT(HS256,密钥 `auth.jwtSecret`),claims:`sub`(用户 Id)、`name`、`role=admin`、`tv`(TokenVersion)、`jti`、`exp`(`auth.accessTokenMinutes`)。服务端校验 `tv == AdminUsers.TokenVersion`(缓存)。
- 限速(ASP.NET `RateLimiter`,按客户端 IP,已过 `ForwardedHeaders`):`/api/auth/login` 10 次/分;`/api/auth/refresh` 30 次/分;`/install/*` 30 次/分;其余 `/api` 600 次/分。超限 429。
- CORS:不启用(同源 + Vite dev 代理)。

---

## 2. vue-naive-admin 兼容接口

### 2.1 `POST /api/auth/login`(公开)

请求(模板还会带 `captcha`、`isQuick`,服务端忽略):

```json
{ "username": "admin", "password": "S3cret!pass" }
```

响应:

```json
{ "code": 0, "message": "OK", "data": {
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "refreshToken": "8fQ2m0v4d7w9Kx1LrTnB3sHcYpUeZ6aGqIjRfM5NwVo",
  "tokenType": "Bearer", "expiresIn": 7200 } }
```

失败:`400/10001`;连续失败达 `auth.loginMaxFailures` → `400/10002`。写 `RefreshTokens`(记录 UA/IP)。

### 2.2 `POST /api/auth/refresh`(公开)

请求 `{ "refreshToken": "..." }` → 响应同 2.1(新 access + **新** refresh,旧 refresh 撤销)。失败 `401/10011`。

### 2.3 `POST /api/auth/logout`

请求 `{ "refreshToken": "...", "all": false }`(两者可选)→ 撤销指定令牌;`all=true` 撤销该用户全部并 `TokenVersion++`。响应 `data: {}`。

### 2.4 `GET /api/user/detail`

```json
{ "code": 0, "data": {
  "id": 1, "username": "admin", "enable": true,
  "profile": { "id": 1, "nickName": "管理员", "avatar": "https://api.dicebear.com/9.x/identicon/svg?seed=admin", "gender": 0, "address": null, "email": null },
  "roles": [ { "id": 1, "code": "SUPER_ADMIN", "name": "超级管理员", "enable": true } ],
  "currentRole": { "id": 1, "code": "SUPER_ADMIN", "name": "超级管理员", "enable": true } } }
```

(模板 `store/helper.getUserInfo` 读取 `id/username/profile.*/roles/currentRole`。)

### 2.5 `PATCH /api/user/profile/{id}`

请求 `{ "nickName": "运维", "avatar": "https://...", "email": "ops@example.com", "gender": 1, "address": "..." }`(任意子集;`gender/address` 接受但不存储)。`{id}` 必须等于当前用户。响应 `data`: 同 2.4 的对象。

### 2.6 `POST /api/auth/password`

请求 `{ "oldPassword": "...", "newPassword": "..." }`;新密码 8–64 位。成功后撤销**其他**刷新令牌(当前会话保持)。失败 `400/10010`。

### 2.7 `GET /api/role/permissions/tree`(菜单树,服务端静态定义)

```json
{ "code": 0, "data": [
  { "id": 1, "code": "Home", "name": "总览大盘", "type": "MENU", "parentId": null, "path": "/", "redirect": null,
    "icon": "i-fe:home", "component": "/src/views/home/index.vue", "layout": "", "keepAlive": true, "order": 1, "enable": true, "show": true, "children": [] },
  { "id": 2, "code": "Nodes", "name": "节点管理", "type": "MENU", "parentId": null, "path": "/nodes", "redirect": null,
    "icon": "i-fe:server", "component": "/src/views/nodes/index.vue", "layout": "", "keepAlive": true, "order": 2, "enable": true, "show": true,
    "children": [
      { "id": 3, "code": "NodeDetail", "name": "节点详情", "type": "MENU", "parentId": 2, "path": "/nodes/:id", "redirect": null,
        "icon": "i-fe:activity", "component": "/src/views/nodes/detail.vue", "layout": "", "keepAlive": false, "order": 1, "enable": true, "show": false, "children": [] } ] },
  { "id": 4, "code": "Alerts", "name": "告警记录", "type": "MENU", "parentId": null, "path": "/alerts", "redirect": null,
    "icon": "i-fe:bell", "component": "/src/views/alerts/index.vue", "layout": "", "keepAlive": true, "order": 3, "enable": true, "show": true, "children": [] },
  { "id": 5, "code": "Settings", "name": "系统设置", "type": "MENU", "parentId": null, "path": "/settings", "redirect": null,
    "icon": "i-fe:settings", "component": "/src/views/settings/index.vue", "layout": "", "keepAlive": false, "order": 4, "enable": true, "show": true, "children": [] },
  { "id": 6, "code": "Profile", "name": "个人资料", "type": "MENU", "parentId": null, "path": "/profile", "redirect": null,
    "icon": "i-fe:user", "component": "/src/views/profile/index.vue", "layout": "", "keepAlive": false, "order": 99, "enable": true, "show": false, "children": [] }
] }
```

说明:`Home` 的 `code` 必须与模板 `basic-routes.js` 中的 `name: 'Home'` 一致,`permission-guard` 会因 `router.hasRoute('Home')` 为真而不重复添加路由;`NodeDetail`、`Profile` 为隐藏菜单(`show:false`)但 `enable:true` 以注册路由。无 BUTTON 类型权限(前端不使用 `v-permission`)。

### 2.8 `GET /api/permission/menu/validate?path=/nodes/12`

`data: true`(路径匹配任一菜单 `path` 模式,含隐藏项)/ `false`。用于模板区分 403/404。

### 2.9 未实现(模板不会在单角色场景调用)

`POST /api/auth/current-role/switch/{role}`、`GET /api/auth/captcha`、`GET /api/auth/refresh/token`(模板旧签名;前端改为 2.2)。返回 404。

---

## 3. 仪表盘

### `GET /api/dashboard/summary`

```json
{ "code": 0, "data": {
  "nodes": { "total": 12, "online": 10, "offline": 1, "unknown": 1, "disabled": 0 },
  "alerts": { "firing": 2, "last24h": 5 },
  "expiring": { "withinDays": 7, "count": 2, "expired": 0,
    "items": [ { "id": 3, "publicName": "HK-Node-01", "expiresAt": "2026-09-12", "daysLeft": 5, "vendor": "BandwagonHost", "price": "49.99", "currency": "USD", "billingCycleMonths": 12 } ] },
  "finance": { "baseCurrency": "CNY", "mrrBase": 356.42,
    "byCurrency": [ { "currency": "USD", "monthly": 41.66 }, { "currency": "CNY", "monthly": 56.00 } ], "nodesWithPrice": 9 },
  "traffic": { "periodBilledBytes": 3821000000000, "periodLimitBytes": 9000000000000, "nodesOverWarn": 1 },
  "recentAlerts": [ { "id": 1024, "nodeId": 12, "nodeName": "HK-Node-01", "rule": 1, "ruleName": "离线", "status": 1, "severity": 3,
                     "title": "[离线] HK-Node-01", "startedAt": "2026-09-07T02:13:22Z", "resolvedAt": null } ],
  "system": { "version": "1.0.0", "protocolVersion": 1, "startedAt": "2026-09-06T20:00:00Z", "uptimeSec": 22402,
              "dbSizeBytes": 26214400, "geoip": { "ready": true, "lastRefreshUtc": "2026-09-05T03:00:12Z", "ipv4Rows": 231000, "ipv6Rows": 96000 } } } }
```

---

## 4. 节点

### 4.1 节点对象(`NodeDetail`,列表项与详情同形;列表省略 `notes`)

```json
{
  "id": 12, "publicName": "HK-Node-01", "adminRemark": "核心 DB-勿动", "enabled": true, "publicVisible": true, "sortOrder": 10,
  "countryCode": "HK", "countryCodeAuto": "HK", "countryCodeOverride": null, "timeZoneId": "Asia/Hong_Kong", "intervalMs": 2000,
  "agentKeyMasked": "snmk_****Q7pZ", "keyRotatedAt": null,
  "hardware": { "hostname": "db-hk-01", "os": "Ubuntu 22.04.4 LTS", "kernel": "5.15.0-113-generic", "arch": "x64",
                "cpuModel": "2x Intel(R) Xeon(R) Gold 6148 CPU @ 2.40GHz", "cpuCores": 80, "memTotalMb": 386000, "swapTotalMb": 8192,
                "disks": [ { "mount": "/", "fs": "ext4", "totalMb": 80000 }, { "mount": "/data", "fs": "xfs", "totalMb": 2000000 } ],
                "netIfs": "eth0,eth1", "virt": "kvm", "agentVersion": "1.0.0+3f2a9c1", "protocolVersion": 1, "bootTimeUtc": "2026-08-28T02:11:40Z" },
  "ips": [ { "address": "203.0.113.10", "family": 4, "isPublic": true, "source": 3, "firstSeenAt": "2026-08-01T00:00:00Z", "lastSeenAt": "2026-09-07T02:10:00Z" },
           { "address": "10.0.0.5", "family": 4, "isPublic": false, "source": 1, "firstSeenAt": "2026-08-01T00:00:00Z", "lastSeenAt": "2026-09-07T02:10:00Z" },
           { "address": "2001:db8::10", "family": 6, "isPublic": true, "source": 1, "firstSeenAt": "2026-08-01T00:00:00Z", "lastSeenAt": "2026-09-07T02:10:00Z" } ],
  "remoteIp": "203.0.113.10",
  "state": { "status": 1, "connected": true, "firstSeenAt": "2026-08-01T00:00:00Z", "lastSeenAt": "2026-09-07T02:13:20Z", "lastRegisterAt": "2026-09-06T20:00:05Z", "statusChangedAt": "2026-09-06T20:00:05Z", "uptimeSec": 864100 },
  "live": { "cpuPermille": 237, "memUsedMb": 1843, "swapUsedMb": 0, "diskUsedMb": [ 18432, 1200000 ], "rxBps": 1250000, "txBps": 380000, "load1": 45, "ts": "2026-09-07T02:13:20Z" },
  "traffic": { "limitBytes": 1000000000000, "countMode": 0, "resetDay": 1, "periodStart": "2026-09-01", "periodEnd": "2026-10-01",
               "rxBytes": 523000000000, "txBytes": 98000000000, "billedBytes": 621000000000, "pct": 62.1 },
  "finance": { "vendor": "BandwagonHost", "price": "49.99", "currency": "USD", "billingCycleMonths": 12, "expiresAt": "2026-09-12", "daysLeft": 5, "autoRenew": false, "renewUrl": "https://bwh81.net/clientarea.php" },
  "alerts": { "alertsEnabled": true, "cpuAlertPct": null, "trafficAlertPct": null, "offlineAlertSec": null, "diskAlertPct": null, "firing": [ { "rule": 1, "since": "2026-09-07T02:13:22Z" } ] },
  "notes": "2026-08 迁移自旧机房",
  "createdAt": "2026-08-01T00:00:00Z", "updatedAt": "2026-09-05T10:00:00Z"
}
```

`live` 在节点从未上报时为 `null`;`traffic.pct` 为百分数(保留 1 位小数),`limitBytes=0` 时为 `null`。

### 4.2 `GET /api/nodes`

查询:`keyword`(匹配 `publicName/adminRemark/hostname/ip`)、`status`(0/1/2)、`enabled`(true/false)、`pageNo`、`pageSize`(默认 0 = 全部)、`sortBy ∈ {sortOrder, publicName, status, lastSeenAt, expiresAt, trafficPct}`、`sortDir`。响应 `data.pageData: NodeDetail[]`(不含 `notes`)。

### 4.3 `POST /api/nodes`

```json
{ "publicName": "HK-Node-01", "adminRemark": "核心 DB-勿动", "enabled": true, "publicVisible": true, "sortOrder": 10,
  "countryCodeOverride": null, "timeZoneId": null, "intervalMs": 2000,
  "traffic": { "limitBytes": 1000000000000, "countMode": 0, "resetDay": 1 },
  "finance": { "vendor": "BandwagonHost", "price": "49.99", "currency": "USD", "billingCycleMonths": 12, "expiresAt": "2026-09-12", "autoRenew": false, "renewUrl": null },
  "alerts": { "alertsEnabled": true, "cpuAlertPct": null, "trafficAlertPct": null, "offlineAlertSec": null, "diskAlertPct": null },
  "notes": null }
```

只有 `publicName` 必填,其余取默认(`intervalMs` 取 `agent.defaultIntervalMs`)。响应 **201**,`data` = `NodeDetail` + `"agentKey": "snmk_..."`(完整密钥仅在此处、轮换、`reveal-key` 与安装脚本中出现)。

校验:

| 字段 | 规则 |
|---|---|
| `publicName` | 1–64 字符,去首尾空白,唯一(409/20001) |
| `adminRemark` | ≤ 256 |
| `countryCodeOverride` | `null` 或 `^[A-Z]{2}$` |
| `timeZoneId` | `null` 或 `TimeZoneInfo.TryFindSystemTimeZoneById` 成功 |
| `intervalMs` | 1000–60000 |
| `traffic.limitBytes` | ≥ 0 |
| `traffic.countMode` | 0–3 |
| `traffic.resetDay` | 1–31 |
| `finance.price` | `null` 或十进制字符串,≥ 0,≤ 2 位小数(字符串传输避免浮点) |
| `finance.currency` | `null` / `USD` / `CNY` / `EUR` |
| `finance.billingCycleMonths` | 0/1/3/6/12/24/36 |
| `finance.expiresAt` | `null` 或 `yyyy-MM-dd` |
| `finance.renewUrl` | `null` 或 http(s) URL ≤ 512 |
| `alerts.cpuAlertPct` / `diskAlertPct` | `null` 或 50–100 |
| `alerts.trafficAlertPct` | `null` 或 50–99 |
| `alerts.offlineAlertSec` | `null` 或 10–600 |
| `notes` | ≤ 2000 |

### 4.4 `GET /api/nodes/{id}` → `NodeDetail`(含 `notes`)。404/20002。

### 4.5 `PATCH /api/nodes/{id}`

请求:4.3 的任意子集(嵌套对象也可部分给出)。副作用:`intervalMs` 变化且节点在线 → 推 `configure`;`enabled=false` → 断开连接并标记 Offline;`expiresAt` 变化 → 立即执行到期评估;`resetDay/timeZoneId/countMode/limitBytes` 变化 → 下一心跳重算账期/比例。响应 `NodeDetail`。

### 4.6 `DELETE /api/nodes/{id}` → `data: {}`;级联删除全部数据(DATA.md §9.3)。

### 4.7 `POST /api/nodes/{id}/rotate-key` → `data: { "agentKey": "snmk_..." , "keyRotatedAt": "..." }`;旧连接立即断开,旧安装令牌失效。

### 4.8 `POST /api/nodes/{id}/reveal-key` → `data: { "agentKey": "snmk_..." }`(写一条 Warning 审计日志,含操作者与 IP)。

### 4.9 `POST /api/nodes/reorder`

请求 `{ "ids": [5, 12, 3] }` → 按数组顺序写 `SortOrder = 10, 20, 30…`;未出现的节点保持原值。响应 `data: {}`;推送 `nodes`/`nodesChanged`。

### 4.10 `GET /api/nodes/{id}/install-script?os=linux&renew=false`

```json
{ "code": 0, "data": {
  "os": "linux",
  "token": "fM3xQ9…(43 chars)", "expiresAt": "2026-09-08T02:13:22Z",
  "url": "https://m.example.com/install/fM3xQ9…",
  "oneLiner": "curl -fsSL https://m.example.com/install/fM3xQ9… | sudo bash",
  "uninstallOneLiner": "curl -fsSL https://m.example.com/install/fM3xQ9… | sudo bash -s -- uninstall",
  "script": "#!/usr/bin/env bash\nset -euo pipefail\n# Server Node Monitor agent installer (generated 2026-09-07T02:13:22Z for node HK-Node-01)\n..." } }
```

- 复用未过期令牌;`renew=true` 或无有效令牌时新建。`os=windows` 返回 PowerShell 脚本与 `irm https://m.example.com/install/<token>?os=windows | iex` 一键命令。
- `site.publicBaseUrl` 为空时用请求头推导(`X-Forwarded-Proto`/`X-Forwarded-Host` → `Host`);推导失败(如 `127.0.0.1`)返回 `422/20004` 提示先在设置里填写公开地址。

### 4.11 `GET /install/{token}`(公开,`text/x-shellscript; charset=utf-8`)

有效 → 200 + 脚本正文(`Cache-Control: no-store`),`UsedCount++`;无效/过期 → 404 文本 `install token invalid or expired`。`?os=windows` → `text/plain` PowerShell。脚本模板与占位符见 DEPLOY.md §4。

### 4.12 `GET /api/nodes/{id}/metrics?range=24h`

`range ∈ {24h, 7d, 30d}`(可选 `from`/`to` Unix 秒进一步裁剪)。

```json
{ "code": 0, "data": { "range": "24h", "stepSec": 60, "fromTs": 1757116800, "toTs": 1757203200,
  "points": [ { "ts": 1757116800, "samples": 30, "cpuAvg": 231, "cpuMax": 412, "memUsedAvgMb": 1840, "memUsedMaxMb": 1902, "swapUsedAvgMb": 0,
                "diskUsedMb": 1218432, "diskTotalMb": 2080000, "rxBpsAvg": 1200000, "rxBpsMax": 4500000, "txBpsAvg": 380000, "txBpsMax": 900000,
                "rxBytes": 72000000, "txBytes": 22800000, "load1Avg": 45, "load1Max": 80 } ] } }
```

点按 `ts` 升序;缺失桶不补零(前端用 `connectNulls:false` 断线表示离线)。

### 4.13 `GET /api/nodes/{id}/traffic?periods=12`

```json
{ "code": 0, "data": {
  "current": { "periodStart": "2026-09-01", "periodEnd": "2026-10-01", "rxBytes": 523000000000, "txBytes": 98000000000, "billedBytes": 621000000000,
               "limitBytes": 1000000000000, "pct": 62.1, "countMode": 0, "resetDay": 1, "timeZoneId": "Asia/Hong_Kong",
               "daily": [ { "date": "2026-09-01", "rxBytes": 21000000000, "txBytes": 4000000000 }, { "date": "2026-09-02", "rxBytes": 19000000000, "txBytes": 3800000000 } ] },
  "history": [ { "periodStart": "2026-08-01", "periodEnd": "2026-09-01", "rxBytes": 780000000000, "txBytes": 150000000000, "billedBytes": 930000000000, "limitBytes": 1000000000000, "closed": true } ] } }
```

### 4.14 `POST /api/nodes/{id}/traffic/reset`

请求 `{ "scope": "period" }` → 当前账期累加值清零(DATA.md §4.3)。响应 `data: traffic`(同 4.13 `current`)。

### 4.15 `GET /api/nodes/{id}/alerts?pageNo=1&pageSize=20` → 同 §5.1 的分页结构,限定该节点。

---

## 5. 告警

### 5.1 `GET /api/alerts`

查询:`nodeId`、`rule`(1–6)、`status`(1/2)、`severity`、`from`/`to`(ISO 时间,按 `startedAt`)、`acknowledged`(true/false)、`pageNo`、`pageSize`、`sortBy ∈ {startedAt, resolvedAt, severity}`。

```json
{ "code": 0, "data": { "total": 57, "pageNo": 1, "pageSize": 20, "pageData": [
  { "id": 1024, "nodeId": 12, "nodeName": "HK-Node-01", "rule": 1, "ruleName": "离线", "subject": "", "status": 1, "severity": 3,
    "title": "[离线] HK-Node-01", "message": "节点已离线 48 秒(最后上报 2026-09-07 10:12:34 +08:00)", "value": 48, "threshold": 30,
    "startedAt": "2026-09-07T02:13:22Z", "resolvedAt": null, "notified": true, "acknowledgedAt": null,
    "deliveries": [ { "channelId": 1, "channelName": "TG 运维群", "kind": 1, "attempt": 1, "ok": true, "statusCode": 200, "error": null, "elapsedMs": 412, "createdAt": "2026-09-07T02:13:23Z" } ] } ] } }
```

`ruleName` 映射:1 离线、2 CPU 高负载、3 流量预警、4 流量超限、5 即将到期、6 磁盘告急。

### 5.2 `GET /api/alerts/active` → `data: AlertEvent[]`(`status=1`,按 `severity desc, startedAt desc`)。

### 5.3 `GET /api/alerts/{id}` → 单个事件(含 `deliveries`)。

### 5.4 `POST /api/alerts/{id}/ack` → `data: { "acknowledgedAt": "..." }`(幂等)。

### 5.5 `DELETE /api/alerts?before=2026-06-01T00:00:00Z&status=2` → `data: { "deleted": 120 }`。

---

## 6. 设置

### 6.1 `GET /api/settings`

```json
{ "code": 0, "data": {
  "site": { "title": "Server Node Monitor", "publicTitle": "节点状态", "publicSubtitle": "", "publicBaseUrl": "https://m.example.com", "timeZone": "Asia/Shanghai" },
  "public": { "showSpecs": true, "showTraffic": true },
  "agent": { "releaseBaseUrl": "https://github.com/OWNER/server-node-monitor/releases/latest/download", "defaultIntervalMs": 2000, "statusIntervalSec": 300, "installTokenTtlHours": 24 },
  "alert": { "enabled": true, "offlineTimeoutSec": 30, "offlineConsecutive": 2, "cpuPct": 90, "cpuSustainMin": 5, "trafficWarnPct": 80,
             "expiryDays": 7, "expiryCheckHour": 9, "cooldownMin": 30, "repeatMin": 0, "diskEnabled": false, "diskPct": 90 },
  "finance": { "baseCurrency": "CNY", "rates": { "CNY": 1, "USD": 7.2, "EUR": 7.8 } },
  "auth": { "accessTokenMinutes": 120, "refreshTokenDays": 30, "loginMaxFailures": 5, "loginLockMinutes": 15 },
  "geoip": { "enabled": true, "lastRefreshUtc": "2026-09-05T03:00:12Z", "ipv4Rows": 231000, "ipv6Rows": 96000, "lastError": null, "ready": true },
  "retention": { "metrics1mHours": 25, "metrics1hDays": 8, "metrics1dDays": 31, "alertEventDays": 180, "deliveryDays": 30, "trafficDailyDays": 400 } } }
```

`auth.jwtSecret` 永不返回;`retention.metrics*` 只读。

### 6.2 `PATCH /api/settings`

请求为 6.1 结构的任意子集(如 `{ "alert": { "cpuPct": 85 }, "site": { "publicTitle": "我的节点" } }`);逐键按 DATA.md §6 校验,任一失败整体 400 并列出 `errors["alert.cpuPct"]`。响应:完整 6.1。

### 6.3 通知渠道

`GET /api/settings/channels`

```json
{ "code": 0, "data": [
  { "id": 1, "type": "telegram", "name": "TG 运维群", "enabled": true, "ruleMask": 0, "minSeverity": 1,
    "config": { "botToken": "1234****", "chatId": "-1001234567890", "parseMode": "HTML", "disableNotification": false, "messageThreadId": null },
    "lastTestAt": "2026-09-01T00:00:00Z", "lastSuccessAt": "2026-09-07T02:13:23Z", "lastError": null, "createdAt": "...", "updatedAt": "..." },
  { "id": 2, "type": "webhook", "name": "内部告警网关", "enabled": true, "ruleMask": 30, "minSeverity": 2,
    "config": { "url": "https://hooks.example.com/snm", "method": "POST", "secret": "****", "headers": { "X-Team": "ops" }, "bodyTemplate": null, "timeoutSec": 10 },
    "lastTestAt": null, "lastSuccessAt": null, "lastError": null, "createdAt": "...", "updatedAt": "..." } ] }
```

`POST /api/settings/channels` 请求 `{ type, name, enabled, ruleMask, minSeverity, config }` → 201 + 对象。`PATCH /api/settings/channels/{id}`:任意子集;`config.botToken`/`config.secret` 为 `"****"` 或缺省时保留原值。`DELETE /api/settings/channels/{id}` → `{}`。

校验:`type ∈ {telegram, webhook}`;`name` 1–64;telegram `botToken` 匹配 `^\d+:[A-Za-z0-9_-]{30,}$`,`chatId` 非空;webhook `url` http(s),`method ∈ {POST, PUT}`,`timeoutSec` 3–60,`headers` ≤ 10 项且键不得为 `Content-Type/Authorization` 之外的敏感头覆盖(允许 `Authorization`);`ruleMask` 0–126;`minSeverity` 1–3。

`POST /api/settings/channels/{id}/test` 与 `POST /api/settings/channels/test`(请求体为未保存的渠道对象)→

```json
{ "code": 0, "data": { "ok": true, "statusCode": 200, "elapsedMs": 412, "response": "{\"ok\":true,...}" } }
```

失败 → `502/30001`,`data` 同形(`ok:false`,`error`)。

### 6.4 GeoIP

`POST /api/settings/geoip/refresh` → 同步执行下载(最长 60 s);成功 `data: geoip`(6.1 结构);失败 `502/30002`。`GET /api/settings/geoip/status` → `data: geoip` + `"lookup": { "ip": "203.0.113.10", "cc": "US" }`(用请求方 IP 演示)。

### 6.5 `GET /api/system/info`

```json
{ "code": 0, "data": { "version": "1.0.0", "protocolVersion": 1, "framework": ".NET 10.0.11", "os": "Linux 6.8.0 x64",
  "startedAt": "2026-09-06T20:00:00Z", "uptimeSec": 22402, "dataDir": "/var/lib/snm-master", "dbSizeBytes": 26214400, "walSizeBytes": 4194304,
  "nodes": { "total": 12, "connected": 10 }, "hubs": { "publicClients": 3, "adminClients": 1 },
  "queues": { "flushPending": 0, "notificationsPending": 0 } } }
```

### 6.6 健康检查(公开)

`GET /healthz` → `200 text/plain "ok"`(仅进程存活);`GET /api/health` → `{ "code": 0, "data": { "status": "ok", "db": true, "geoip": true } }`(DB `SELECT 1` 失败 → 503)。

---

## 7. 接口总表

| 方法 | 路径 | 鉴权 | 说明 |
|---|---|---|---|
| POST | `/api/auth/login` | 公开(限速) | 登录 |
| POST | `/api/auth/refresh` | 公开(限速) | 刷新令牌(轮换) |
| POST | `/api/auth/logout` | JWT | 撤销刷新令牌 |
| POST | `/api/auth/password` | JWT | 修改密码 |
| GET | `/api/user/detail` | JWT | 当前用户(模板) |
| PATCH | `/api/user/profile/{id}` | JWT | 资料(模板) |
| GET | `/api/role/permissions/tree` | JWT | 菜单树(模板) |
| GET | `/api/permission/menu/validate` | JWT | 路径校验(模板) |
| GET | `/api/dashboard/summary` | JWT | 总览 |
| GET | `/api/nodes` | JWT | 节点列表 |
| POST | `/api/nodes` | JWT | 创建(返回密钥) |
| GET | `/api/nodes/{id}` | JWT | 详情 |
| PATCH | `/api/nodes/{id}` | JWT | 更新 |
| DELETE | `/api/nodes/{id}` | JWT | 删除 |
| POST | `/api/nodes/{id}/rotate-key` | JWT | 轮换密钥 |
| POST | `/api/nodes/{id}/reveal-key` | JWT | 查看密钥(审计) |
| POST | `/api/nodes/reorder` | JWT | 排序 |
| GET | `/api/nodes/{id}/install-script` | JWT | 安装脚本 + 一键命令 |
| GET | `/install/{token}` | 公开(限速) | 脚本正文 |
| GET | `/api/nodes/{id}/metrics` | JWT | 24h/7d/30d 时序 |
| GET | `/api/nodes/{id}/traffic` | JWT | 账期/日流量 |
| POST | `/api/nodes/{id}/traffic/reset` | JWT | 清零本期 |
| GET | `/api/nodes/{id}/alerts` | JWT | 节点告警 |
| GET | `/api/alerts` | JWT | 告警列表 |
| GET | `/api/alerts/active` | JWT | 进行中 |
| GET | `/api/alerts/{id}` | JWT | 详情 |
| POST | `/api/alerts/{id}/ack` | JWT | 确认 |
| DELETE | `/api/alerts` | JWT | 清理 |
| GET | `/api/settings` | JWT | 设置 |
| PATCH | `/api/settings` | JWT | 更新设置 |
| GET/POST | `/api/settings/channels` | JWT | 渠道列表/新建 |
| PATCH/DELETE | `/api/settings/channels/{id}` | JWT | 渠道更新/删除 |
| POST | `/api/settings/channels/{id}/test`、`/api/settings/channels/test` | JWT | 测试消息 |
| POST | `/api/settings/geoip/refresh` | JWT | 刷新 GeoIP |
| GET | `/api/settings/geoip/status` | JWT | GeoIP 状态 |
| GET | `/api/system/info` | JWT | 系统信息 |
| GET | `/healthz`、`/api/health` | 公开 | 健康检查 |

静态与 SPA:`/` → `wwwroot/index.html`(公开大屏);`/admin/*` → `wwwroot/admin/index.html`(history 模式回退,`MapFallbackToFile("/admin/{*path}", "admin/index.html")`);`/api/*` 与 `/hubs/*` 不参与回退,未匹配返回 404 JSON。

---

## 8. 服务端实现约束

- 控制器风格:Minimal API 分组(`MapGroup("/api/nodes")`)或 `[ApiController]` 均可,但必须使用统一的 `ApiResponse<T>` 包装与 `ProblemDetails → ApiResponse` 转换中间件(未处理异常 → 500 包)。
- 模型绑定失败(JSON 语法错)→ `400`,`message: "请求体不是合法 JSON"`。
- JSON 选项:`PropertyNamingPolicy = CamelCase`、`DefaultIgnoreCondition = Never`(字段稳定存在,便于前端)、`NumberHandling = Strict`、`decimal` 以字符串输出(`JsonConverter`)。
- 所有写接口记录 `Information` 日志:`{Method} {Path} by {User} from {Ip} → {Status}`;`reveal-key`/`rotate-key`/`DELETE` 记录 `Warning`。
