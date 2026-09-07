# API.md — REST 接口全清单(候选设计 C)

> 所有接口前缀 `/api`。响应统一为 JSON 信封(与 vue-naive-admin 2.x 的 `src/utils/http/interceptors.js` 约定完全一致)。管理端接口除登录/刷新外全部要求 `Authorization: Bearer <accessToken>`。时间字段为 ISO-8601 UTC(`2026-09-07T01:23:45.123Z`),日期字段为 `yyyy-MM-dd`。
>
> 对应 BRIEF §3 第 6、9 题的决策见 §0。

---

## 0. 决策摘要

| 问题 | 结论 |
|---|---|
| Q6 REST 与模板兼容 | **服务端兼容模板期望的登录/用户/权限接口 + 少量前端改造**。兼容:`POST /api/auth/login`、`GET /api/user/detail`、`GET /api/role/permissions/tree`、`GET /api/permission/menu/validate`、`POST /api/auth/logout`、`POST /api/auth/password`、`PATCH /api/user/profile/{id}`,信封 `{code:0,data,message}`,分页 `pageNo/pageSize → {pageData,total}`。改造(见 FRONTEND.md §3):去掉验证码/一键体验、刷新 token 改为 `POST /api/auth/refresh/token` 并在 401 时静默刷新一次、`VITE_AXIOS_BASE_URL='/api'`、Vite 代理不再 rewrite、增加 `/hubs` ws 代理。不实现的模板接口(`/auth/captcha`、`/auth/current-role/switch/*`、`/auth/role/toggle`、`/role`、`/user`(用户管理)、`/permission`(资源管理))返回 404,对应页面从模板删除。 |
| Q9 安装脚本 | `GET /api/nodes/{id}/install-script?os=linux|windows` 返回一行命令 + 脚本全文;脚本本体由**匿名**端点 `GET /install/{installToken}/agent.sh`(或 `agent.ps1`)提供,`installToken` 为节点专属 43 字符随机串(可轮换),`Cache-Control: no-store`,每 IP 每分钟 10 次限速。脚本从 `agent.releaseBaseUrl` 按 `uname -m` 下载对应二进制并校验 `SHA256SUMS`,写 systemd unit(非 root 用户 `snm-agent`,`Restart=always`),幂等,`bash -s -- uninstall` 卸载。模板全文见 DEPLOY.md §4。 |

---

## 1. 通用约定

### 1.1 信封

成功:
```json
{ "code": 0, "message": "ok", "data": { } }
```
失败(HTTP 状态码与 `code` 一致,业务码除外):
```json
{ "code": 400, "message": "参数错误: trafficResetDay 必须在 1-31 之间", "data": null }
```
校验失败(HTTP 422):
```json
{ "code": 422, "message": "校验失败", "data": { "errors": { "publicName": ["不能为空"], "trafficResetDay": ["必须在 1-31 之间"] } } }
```

| HTTP | code | 场景 | 前端行为(模板 `resolveResError`) |
|---|---|---|---|
| 200 | 0 | 成功 | |
| 400 | 400 | 参数错误 | 弹 message |
| 400 | 10001 | 用户名或密码错误 | 弹 message(登录页) |
| 401 | 401 | access token 缺失/过期/无效 | **改造后**:先尝试 refresh 一次;失败→"登录已过期,是否重新登录?" |
| 401 | 11007 | refresh token 无效或过期 | 弹"…是否重新登录" |
| 401 | 11008 | 账号不存在/被禁用 | 同上 |
| 403 | 403 | 非管理员(理论不出现) | "请求被拒绝" |
| 404 | 404 | 资源不存在 | "请求资源或接口不存在" |
| 409 | 409 | 唯一性冲突(节点名重复) | 弹 message |
| 422 | 422 | 字段校验失败 | 弹 message,表单可读 `data.errors` |
| 429 | 429 | 限速 | "尝试过于频繁,请稍后再试" |
| 500 | 500 | 服务器异常 | "服务器发生异常" |

未捕获异常由 `ExceptionHandlerMiddleware` 转为 500 信封并记 Error 日志(生产不回传堆栈)。

### 1.2 分页

请求查询串 `pageNo`(从 1,默认 1)、`pageSize`(默认 10,最大 200);响应 `data: { "pageData": [...], "total": 123 }`。排序 `sortBy`(字段名)+ `sortDir`(`asc|desc`),各接口列出允许字段。

### 1.3 鉴权

- `Authorization: Bearer <accessToken>`;JWT HS256,`iss=snm`, `aud=snm-admin`, claims `sub`(用户 Id)、`name`、`snm:role=admin`、`jti`;有效期 `Jwt:AccessTokenMinutes`(默认 120)。
- Refresh token:43 字符随机,存 SHA-256,有效期 `Jwt:RefreshTokenDays`(30),**每次刷新轮换**(旧 token 立即失效);登出撤销。
- 限速:`/api/auth/login` 每 IP 5 次/分钟(`security.loginMaxPerMinute`);`/install/*` 每 IP 10 次/分钟。
- 所有 `/api/**`(除 `auth/login`、`auth/refresh/token`、`system/health`)加 `[Authorize(Policy="AdminOnly")]`。

### 1.4 JSON 命名

camelCase;`long` 字节数以 JSON number 传输(< 2^53);`decimal` 金额以 number(两位小数)传输;枚举以小写字符串传输(见各接口)。

---

## 2. 认证与用户(模板兼容)

### `POST /api/auth/login`
匿名。请求:
```json
{ "username": "admin", "password": "Secret#123" }
```
(模板还会传 `captcha`、`isQuick`,服务端忽略未知字段。)
响应:
```json
{ "code": 0, "data": { "accessToken": "eyJhbGciOi…", "refreshToken": "kQm9…43chars", "expiresIn": 7200, "tokenType": "Bearer" } }
```
失败:`400/10001 用户名或密码错误`;`429`。成功后记录 `LastLoginMs/LastLoginIp`。

### `POST /api/auth/refresh/token`
匿名(凭 refresh token)。请求 `{ "refreshToken": "kQm9…" }`;响应同登录(新的一对 token)。失败 `401/11007`。

### `POST /api/auth/logout`
撤销请求头 access token 对应用户的**当前** refresh token(请求体 `{ "refreshToken": "…" }` 可选;缺省撤销该用户全部)。响应 `{code:0}`。模板以 `needTip:false` 调用,失败静默。

### `POST /api/auth/password`
```json
{ "oldPassword": "…", "newPassword": "…" }
```
规则:新密码 8–64 字符,至少含字母与数字。成功后撤销全部 refresh token(当前浏览器需重新登录——前端在成功提示后跳登录)。错误:`400 原密码错误`、`422`。

### `GET /api/user/detail`
模板 `getUserInfo()` 读取 `id, username, profile{avatar,nickName,gender,address,email}, roles, currentRole`:
```json
{ "code": 0, "data": {
  "id": 1, "username": "admin", "enable": true,
  "profile": { "id": 1, "userId": 1, "nickName": "管理员", "avatar": "https://…/avatar.png", "gender": 0, "address": null, "email": "ops@example.com" },
  "roles": [ { "id": 1, "code": "SUPER_ADMIN", "name": "超级管理员", "enable": true } ],
  "currentRole": { "id": 1, "code": "SUPER_ADMIN", "name": "超级管理员", "enable": true }
} }
```
单角色 → 模板隐藏"切换角色"。

### `PATCH /api/user/profile/{id}`
`{ "nickName": "运维", "avatar": "https://…", "email": "…" }`(`gender/address` 接受并忽略);`id` 必须等于当前用户。响应 `{code:0}`。

### `GET /api/role/permissions/tree`
返回菜单/路由树(模板 `permissionStore.setPermissions` 消费;`component` 为 `/src/views/**` 路径,用 `import.meta.glob('@/views/**/*.vue')` 解析;`icon` 必须在 UnoCSS safelist 内,因此只用 `i-fe:*`):
```json
{ "code": 0, "data": [
  { "id": 1, "code": "Home", "name": "总览大盘", "type": "MENU", "parentId": null, "path": "/", "redirect": null,
    "icon": "i-fe:home", "component": "/src/views/home/index.vue", "layout": "", "keepAlive": false, "show": true, "enable": true, "order": 0, "children": [] },
  { "id": 2, "code": "Nodes", "name": "节点管理", "type": "MENU", "parentId": null, "path": "/nodes",
    "icon": "i-fe:server", "component": "/src/views/nodes/index.vue", "layout": "", "keepAlive": true, "show": true, "enable": true, "order": 10, "children": [] },
  { "id": 3, "code": "NodeDetail", "name": "节点详情", "type": "MENU", "parentId": null, "path": "/nodes/:id",
    "icon": "i-fe:activity", "component": "/src/views/nodes/detail.vue", "layout": "", "keepAlive": false, "show": false, "enable": true, "order": 11, "children": [] },
  { "id": 4, "code": "Alerts", "name": "告警记录", "type": "MENU", "parentId": null, "path": "/alerts",
    "icon": "i-fe:bell", "component": "/src/views/alerts/index.vue", "layout": "", "keepAlive": true, "show": true, "enable": true, "order": 20, "children": [] },
  { "id": 5, "code": "Channels", "name": "通知渠道", "type": "MENU", "parentId": null, "path": "/channels",
    "icon": "i-fe:send", "component": "/src/views/channels/index.vue", "layout": "", "keepAlive": false, "show": true, "enable": true, "order": 30, "children": [] },
  { "id": 6, "code": "Settings", "name": "系统设置", "type": "MENU", "parentId": null, "path": "/settings",
    "icon": "i-fe:settings", "component": "/src/views/settings/index.vue", "layout": "", "keepAlive": false, "show": true, "enable": true, "order": 40, "children": [] },
  { "id": 7, "code": "Profile", "name": "个人资料", "type": "MENU", "parentId": null, "path": "/profile",
    "icon": "i-fe:user", "component": "/src/views/profile/index.vue", "layout": "", "keepAlive": false, "show": false, "enable": true, "order": 99, "children": [] }
] }
```
树是**服务端常量**(`MenuCatalog.cs`),不存库。`show:false` 的项仍注册路由(模板 `getMenuItem` 逻辑)。`keepAlive:true` 的页面组件必须 `defineOptions({ name: '<code>' })`。

### `GET /api/permission/menu/validate?path=/nodes`
响应 `{ "code": 0, "data": true }`(路径匹配菜单树任一 `path`,含 `/nodes/:id` 的模式匹配)。模板用它区分 403/404。

---

## 3. 总览大盘

### `GET /api/dashboard/summary`
```json
{ "code": 0, "data": {
  "serverTime": "2026-09-07T01:23:45Z",
  "nodes": { "total": 12, "enabled": 12, "online": 11, "offline": 1, "public": 10 },
  "alerts": { "open": 1, "last24h": 3, "suppressedLast24h": 0 },
  "expiry": { "within7d": 2, "within30d": 4, "expired": 0 },
  "cost": {
    "monthly": [ { "currency": "USD", "amount": 123.45 }, { "currency": "CNY", "amount": 88.00 } ],
    "yearly":  [ { "currency": "USD", "amount": 1481.40 }, { "currency": "CNY", "amount": 1056.00 } ],
    "nodesWithPrice": 9
  },
  "traffic": {
    "cycleTotalBytes": 1234567890123,
    "top": [ { "nodeId": 3, "name": "hk-01", "publicName": "HK-Node-01", "pct": 82.1, "usedBytes": 821000000000, "limitBytes": 1000000000000, "cycleEnd": "2026-10-01" } ]
  },
  "geoip": { "loaded": true, "lastSuccess": "2026-09-01T00:00:00Z" },
  "master": { "version": "1.0.0+abc1234", "uptimeSec": 86400, "dbSizeBytes": 20480000 }
} }
```
`cost.monthly` = Σ `price / cycleMonths`(Monthly=1, Quarterly=3, SemiAnnual=6, Yearly=12, Biennial=24, Triennial=36;OneTime 不计),按币种分组;`yearly = monthly × 12`。`traffic.top` 最多 5 条,按 `pct desc`(仅 `limitGb>0`)。

### `GET /api/dashboard/alerts/recent?limit=10`
最近事件(含已恢复),元素结构同 §6 `AlertEventDto`。

### `GET /api/dashboard/expiring?days=30`
```json
{ "code": 0, "data": [ { "nodeId": 3, "name": "hk-01", "provider": "BWH", "expiresAt": "2026-09-12", "daysLeft": 5, "price": 49.99, "currency": "USD", "cycle": "yearly", "autoRenew": false } ] }
```

---

## 4. 节点

### 4.1 DTO

`NodeDto`(详情;列表项 = 去掉 `agentKey`、`installToken`、`billing.note`、`adminRemark` 保留):
```json
{
  "id": 3, "name": "hk-01", "publicName": "HK-Node-01", "adminRemark": "核心 DB-勿动", "group": "生产",
  "enabled": true, "isPublic": true, "sortOrder": 10, "alertsEnabled": true,
  "countryCode": "HK", "countryCodeAuto": "HK", "countryCodeOverride": null,
  "agentKey": "kQm9xV…(43)", "installToken": "Zp1…(43)",
  "intervalMs": 2000, "ipReportIntervalSec": 300,
  "traffic": { "limitGb": 1000, "resetDay": 1, "mode": "sum", "timeZone": null, "effectiveTimeZone": "Asia/Shanghai" },
  "billing": { "provider": "BandwagonHost", "providerUrl": "https://bwh81.net", "price": 49.99, "currency": "USD",
               "cycle": "yearly", "expiresAt": "2027-03-01", "autoRenew": false, "note": "", "daysLeft": 175, "monthlyCost": 4.17 },
  "alertOverrides": { "offlineSeconds": null, "cpuHighPct": null, "memHighPct": null, "diskHighPct": null, "trafficHighPct": null, "expiryDays": null },
  "info": { "hostname": "hk-01", "os": "Ubuntu 24.04.1 LTS", "osKind": "linux", "arch": "x64",
            "cpuModel": "Intel(R) Xeon(R) Platinum 8375C CPU @ 2.90GHz", "cpuCores": 2, "memTotalMb": 1970, "swapTotalMb": 0,
            "disks": [ { "mount": "/", "fs": "ext4", "totalMb": 40000 } ], "netInterfaces": ["eth0"],
            "agentVersion": "1.0.0+abc1234", "bootId": "8c1e…", "lastHello": "2026-09-07T01:00:00Z" },
  "ips": [ { "ip": "203.0.113.7", "family": 4, "kind": "public", "source": "server", "lastSeen": "2026-09-07T01:23:44Z" },
           { "ip": "10.0.0.5", "family": 4, "kind": "private", "source": "agent", "lastSeen": "2026-09-07T01:20:00Z" } ],
  "live": { "online": true, "connected": true, "lastSeen": "2026-09-07T01:23:45Z", "lastRemoteIp": "203.0.113.7",
            "cpu": 123, "load1": 52, "memUsedMb": 843, "swapUsedMb": 0, "diskUsedMb": [18432], "rxBps": 1200, "txBps": 3400, "uptimeSec": 864000 },
  "cycle": { "start": "2026-09-01", "end": "2026-10-01", "rxBytes": 400000000000, "txBytes": 421000000000,
             "usedBytes": 821000000000, "limitBytes": 1000000000000, "pct": 82.1 },
  "openAlerts": ["TrafficHigh"],
  "createdAt": "2026-08-01T00:00:00Z", "updatedAt": "2026-09-06T12:00:00Z"
}
```
枚举字符串:`traffic.mode` ∈ `sum|tx|rx|max`;`billing.cycle` ∈ `onetime|monthly|quarterly|semiannual|yearly|biennial|triennial`;`info.osKind` ∈ `other|linux|windows`。`cpu` 为千分比,`load1` ×100(65535 → `null`)。

`NodeUpsert`(POST 全量 / PATCH 部分,缺省字段不改):

| 字段 | 类型 | 校验 | 默认(POST) |
|---|---|---|---|
| `name` | string | 必填 1–64,唯一(NOCASE) | — |
| `publicName` | string | 1–64 | =name |
| `adminRemark` | string? | ≤1024 | null |
| `group` | string? | ≤32 | null |
| `enabled` / `isPublic` / `alertsEnabled` | bool | | true |
| `sortOrder` | int | −10000..10000 | max+10 |
| `countryCodeOverride` | string? | `^[A-Z]{2}$` 或 null | null |
| `intervalMs` | int | 1000–60000 | `agent.intervalMs` |
| `ipReportIntervalSec` | int | 60–3600 | `agent.ipReportIntervalSec` |
| `traffic.limitGb` | int | 0–1000000 | 0 |
| `traffic.resetDay` | int | 1–31 | 1 |
| `traffic.mode` | string | 枚举 | `sum` |
| `traffic.timeZone` | string? | IANA 可解析 | null |
| `billing.provider` / `providerUrl` / `note` | string? | ≤64 / ≤256(http(s)) / ≤1024 | null |
| `billing.price` | number? | ≥0,两位小数 | null |
| `billing.currency` | string | ∈ `general.currencies` | `general.defaultCurrency` |
| `billing.cycle` | string | 枚举 | `monthly` |
| `billing.expiresAt` | string? | `yyyy-MM-dd` | null |
| `billing.autoRenew` | bool | | false |
| `alertOverrides.*` | int? | 与全局同范围 | null |

### 4.2 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/nodes` | 分页列表。查询:`keyword`(匹配 name/publicName/hostname/ip)、`group`、`status`(`online|offline|disabled`)、`public`(bool)、`sortBy` ∈ `sortOrder|name|cpu|mem|trafficPct|expiresAt|lastSeen`、`sortDir`。响应 `{pageData:[NodeListItem], total}`。列表默认 `sortOrder asc, id asc`。 |
| `GET` | `/api/nodes/all` | 轻量全量:`[{id, name, publicName, group, countryCode, online}]`(下拉/筛选用) |
| `GET` | `/api/nodes/groups` | `["生产","测试"]` 去重分组名 |
| `POST` | `/api/nodes` | 创建;生成 `agentKey`、`installToken`;响应 `NodeDto`(含 key)。409 名称冲突。触发 `NodeChanged` 广播。 |
| `GET` | `/api/nodes/{id}` | 详情 `NodeDto` |
| `PATCH` | `/api/nodes/{id}` | 部分更新;`intervalMs/ipReportIntervalSec` 变化 → 向该节点连接推 `ApplyConfig`;`enabled=false` → Abort 连接;到期日/开关变化 → 立即评估 `Expiry`;`isPublic`/`publicName`/`sortOrder`/国家变化 → 广播 `NodeChanged`/`NodeRemoved`。 |
| `DELETE` | `/api/nodes/{id}` | 删除(级联);Abort 连接;广播 `NodeRemoved`。 |
| `POST` | `/api/nodes/{id}/rotate-key` | 轮换 AgentKey **与** InstallToken;Abort 现有连接;响应 `{ "agentKey": "…", "installToken": "…" }` |
| `PUT` | `/api/nodes/order` | `{ "ids": [5, 3, 9] }` → 依次写 `sortOrder = 10, 20, 30…`;广播 `NodeChanged`(逐个) |
| `GET` | `/api/nodes/{id}/install-script?os=linux` | 见 4.3 |
| `GET` | `/api/nodes/{id}/metrics?range=24h` | 见 4.4 |
| `GET` | `/api/nodes/{id}/traffic` | 见 4.5 |
| `GET` | `/api/nodes/{id}/alerts?pageNo&pageSize` | 该节点事件分页,元素 `AlertEventDto` |

`NodeListItem` 补充计算字段:`trafficPct`(number?)、`daysLeft`(int?)、`memPct`(千分比)、`diskPct`(最大挂载使用千分比)。

### 4.3 安装脚本

`GET /api/nodes/{id}/install-script?os=linux|windows`(默认 linux):
```json
{ "code": 0, "data": {
  "os": "linux",
  "baseUrl": "https://monitor.example.com", "baseUrlSource": "settings",
  "scriptUrl": "https://monitor.example.com/install/Zp1…/agent.sh",
  "command": "curl -fsSL https://monitor.example.com/install/Zp1…/agent.sh | sudo bash",
  "commandWithProxy": "curl -fsSL https://monitor.example.com/install/Zp1…/agent.sh | sudo SNM_PROXY=socks5://10.0.0.1:1080 bash",
  "uninstallCommand": "curl -fsSL https://monitor.example.com/install/Zp1…/agent.sh | sudo bash -s -- uninstall",
  "manualCommand": "/opt/snm-agent/snm-agent --server https://monitor.example.com --key kQm9…",
  "releaseBaseUrl": "https://github.com/OWNER/server-node-monitor/releases/latest/download",
  "script": "#!/usr/bin/env bash\nset -euo pipefail\n…"
} }
```
Windows:`scriptUrl` 以 `agent.ps1` 结尾,`command` = `powershell -ExecutionPolicy Bypass -c "irm https://…/agent.ps1 | iex"`。`baseUrlSource` 为 `settings`(`general.publicBaseUrl`)或 `request`(未配置时用 `X-Forwarded-Proto/Host` 或 `Host` 推断,前端显示提示"建议在系统设置中填写公网地址")。

匿名端点:
- `GET /install/{installToken}/agent.sh` → `text/x-shellscript; charset=utf-8`,`Cache-Control: no-store`,`Content-Disposition: inline; filename=agent.sh`;token 不存在或节点禁用 → 404 纯文本 `not found`。
- `GET /install/{installToken}/agent.ps1` → `text/plain; charset=utf-8`。
- 模板占位符 `__SERVER_URL__`、`__AGENT_KEY__`、`__RELEASE_BASE__`、`__VERSION__`(`agent.agentVersionPin` 或空=latest)、`__NODE_NAME__`,替换后输出(DEPLOY.md §4)。

### 4.4 历史指标

`GET /api/nodes/{id}/metrics?range=24h|7d|30d`(默认 24h):
```json
{ "code": 0, "data": {
  "range": "24h", "stepSec": 60, "from": "2026-09-06T01:24:00Z", "to": "2026-09-07T01:24:00Z", "memTotalMb": 1970, "diskTotalMb": 40000,
  "points": [
    { "t": 1757117040000, "n": 30, "cpuAvg": 123, "cpuMax": 400, "load1Avg": 52, "memAvg": 843, "memMax": 900, "swapAvg": 0,
      "diskAvg": 18432, "rxAvg": 1200, "rxMax": 5000, "txAvg": 3400, "txMax": 9000, "rxBytes": 72000, "txBytes": 204000, "onlineSec": 60 }
  ]
} }
```
`t` Unix 毫秒;单位同 DATA §2.5;最后一点可为"进行中的桶"(`partial:true`)。

### 4.5 流量

`GET /api/nodes/{id}/traffic`:
```json
{ "code": 0, "data": {
  "cycle": { "start": "2026-09-01", "end": "2026-10-01", "resetDay": 1, "mode": "sum", "timeZone": "Asia/Shanghai",
             "rxBytes": 400000000000, "txBytes": 421000000000, "usedBytes": 821000000000, "limitBytes": 1000000000000, "pct": 82.1,
             "daysElapsed": 6, "daysTotal": 30, "projectedBytes": 4105000000000 },
  "daily": [ { "date": "2026-08-09", "rxBytes": 1, "txBytes": 2 }, … ],           // 最近 30 个本地日,缺日补 0
  "cycles": [ { "start": "2026-08-01", "end": "2026-09-01", "rxBytes": 1, "txBytes": 2, "limitBytes": 1000000000000 }, … ]  // 最近 6 个周期,含当前
} }
```
`projectedBytes = usedBytes / max(daysElapsed,1) × daysTotal`。

---

## 5. 通知渠道

`ChannelDto`:
```json
{ "id": 1, "name": "运维 TG 群", "type": "telegram", "enabled": true, "minLevel": "warning", "rules": [],
  "config": { "botToken": "***…AbCd", "chatId": "-1001234567890", "parseMode": "HTML", "disableNotification": false, "apiBase": "https://api.telegram.org" },
  "lastTestAt": "2026-09-01T00:00:00Z", "lastSentAt": null, "lastError": null, "createdAt": "…", "updatedAt": "…" }
```
Webhook `config`:`{ "url": "https://hooks.example.com/snm", "method": "POST", "secret": "***…9f2a", "headers": { "X-Token": "…" }, "timeoutSec": 10, "insecureSkipTlsVerify": false }`。

脱敏:`botToken`/`secret` 读取时显示 `***…` + 末 4 位;写入时若值以 `***` 开头则**保持原值**。

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/channels` | 全部(不分页) |
| `POST` | `/api/channels` | 创建;校验:`name` 1–64;telegram 需 `botToken` 匹配 `^\d+:[A-Za-z0-9_-]{30,}$`、`chatId` 非空;webhook 需 `url` http(s)、`method` ∈ `POST|PUT`、`timeoutSec` 3–60 |
| `PATCH` | `/api/channels/{id}` | 部分更新 |
| `DELETE` | `/api/channels/{id}` | |
| `POST` | `/api/channels/{id}/test` | 用已保存配置发送测试消息;响应 `{ "ok": true, "latencyMs": 412, "response": "200 OK" }` 或 `{ "ok": false, "error": "Telegram: 400 Bad Request: chat not found" }`(HTTP 仍 200) |
| `POST` | `/api/channels/test` | 用请求体中的未保存配置测试(`{type, config}`),同上 |

---

## 6. 告警

`AlertEventDto`:
```json
{ "id": 101, "nodeId": 3, "nodeName": "hk-01", "rule": "Offline", "level": "critical", "status": "firing",
  "title": "节点离线", "message": "hk-01 已离线 35 秒(最近在线 2026-09-07 09:20:31 +08:00)", "value": 35, "threshold": 30,
  "startedAt": "2026-09-07T01:21:06Z", "resolvedAt": null, "notifiedAt": "2026-09-07T01:21:07Z", "recoveryNotifiedAt": null,
  "notifyError": null, "suppressed": false, "ackedAt": null, "durationSec": 35 }
```

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/api/alerts` | 分页;查询 `nodeId`、`rule`、`level`、`status`(`firing|resolved`)、`from`/`to`(ISO)、`suppressed`(bool);默认 `startedAt desc` |
| `GET` | `/api/alerts/open` | 当前 firing 的全部事件(不分页) |
| `GET` | `/api/alerts/{id}` | |
| `POST` | `/api/alerts/{id}/ack` | 标记已确认(`ackedAt=now`),不影响状态机 |
| `POST` | `/api/alerts/{id}/resolve` | 手动关闭(仅对 `Expiry`/`TrafficHigh` 允许;状态机置 Ok,`CooldownUntil=now+cooldown`,消息追加"手动关闭");其余规则 400 |
| `GET` | `/api/alerts/stats?days=7` | `{ "byRule": {"Offline": 3, …}, "byDay": [{"date":"2026-09-01","count":2}] }` |

---

## 7. 系统设置

### `GET /api/settings`
返回 DATA.md §2.12 全部组(`geoip.status` 只读):
```json
{ "code": 0, "data": { "general": { … }, "agent": { … }, "alert": { … }, "geoip": { … }, "retention": { … }, "security": { … },
  "geoipStatus": { "loaded": true, "ipv4Ranges": 401233, "ipv6Ranges": 190112, "lastSuccess": "…", "lastAttempt": "…", "lastError": null },
  "timeZones": ["Asia/Shanghai", "Asia/Hong_Kong", "UTC", …] } }
```

### `PUT /api/settings`
请求体为部分组对象,例如 `{ "alert": { "cooldownMinutes": 45 }, "general": { "publicTitle": "我的节点" } }`;服务端逐字段合并并校验(范围见 DATA §2.12),整体原子写入;响应新的完整设置。修改后即时生效:阈值(下一评估周期)、`agent.intervalMs`(仅影响新建节点默认值)、`general.publicTitle`(向 public 组广播一次 `Snapshot`)。

### `POST /api/settings/geoip/refresh`
立即触发下载;响应 `{ "started": true }`;进度看 `geoipStatus`。

### `GET /api/system/info`
```json
{ "code": 0, "data": { "version": "1.0.0+abc1234", "runtime": ".NET 10.0.11", "os": "Linux 6.8 x64", "startedAt": "…", "uptimeSec": 86400,
  "dbPath": "/var/lib/snm/snm.db", "dbSizeBytes": 20480000, "walSizeBytes": 1048576, "dataDir": "/var/lib/snm",
  "listen": "http://127.0.0.1:5080", "publicConnections": 12, "adminConnections": 1, "agentConnections": 11,
  "counters": { "heartbeatsTotal": 1234567, "heartbeatsDropped": 3, "bindFailures": 0 } } }
```

### `GET /api/system/health`(匿名)
`{ "status": "ok", "db": "ok", "time": "…" }`;DB 不可写时 HTTP 503。供反代/监控探活。

---

## 8. 大屏辅助(匿名,仅静态信息)

### `GET /api/public/meta`
`{ "title": "节点状态", "tickMs": 2000, "showOffline": true, "version": "1.0.0" }` — 大屏页面加载时可选调用(PRD 要求"页面加载仅建立 SignalR 连接",因此 **`Snapshot` 已包含 `title`,此接口仅作后备**,前端默认不调用)。

---

## 9. 端点权限矩阵

| 前缀 | 鉴权 |
|---|---|
| `/api/auth/login`、`/api/auth/refresh/token`、`/api/system/health`、`/api/public/*`、`/install/*` | 匿名(限速) |
| 其余 `/api/**` | `AdminOnly`(JWT) |
| `/hubs/agent` | `AgentOnly`(AgentKey) |
| `/hubs/admin` | `AdminOnly`(JWT via `access_token`) |
| `/hubs/public` | 匿名 |

---

## 10. 外发通知格式(Telegram / Webhook)

### 10.1 Telegram(`sendMessage`,`parse_mode=HTML`)

触发:
```
🔴 <b>节点离线</b> · HK-Node-01
主机:hk-01(🇭🇰 HK)
状态:已离线 35 秒,最近在线 2026-09-07 09:20:31 (+08:00)
阈值:30 秒
时间:2026-09-07 09:21:06 (+08:00)
```
恢复:
```
🟢 <b>恢复:节点离线</b> · HK-Node-01
主机:hk-01(🇭🇰 HK)
离线时长:5 分 12 秒
时间:2026-09-07 09:26:18 (+08:00)
```
其他规则首行:`🟠 <b>CPU 持续过高</b>`(值 `93.2%,持续 5 分钟,阈值 90%`)、`🟠 <b>内存持续过高</b>`、`🟠 <b>磁盘空间不足</b>`(`/ 已用 92.1%`)、`🟠 <b>流量用量超标</b>`(`已用 821.0 GB / 1000 GB(82.1%),周期 09-01 ~ 10-01`)、`🟡 <b>即将到期</b>`(`剩余 5 天,2026-09-12,BandwagonHost,$49.99/年`)、`🔴 <b>已过期</b>`、测试消息 `🔔 <b>测试消息</b> · Server Node Monitor`。时间用全局时区。HTML 转义 `<>&`。

### 10.2 Webhook

`POST {url}`,头:`Content-Type: application/json; charset=utf-8`、`User-Agent: SNM-Master/<version>`、`X-SNM-Event: alert.firing|alert.resolved|alert.renotify|test`、`X-SNM-Timestamp: <unix seconds>`、`X-SNM-Delivery: <uuid>`,配置了 `secret` 时 `X-SNM-Signature: sha256=<hex HMAC-SHA256(secret, timestamp + "." + rawBody)>`,加上 `config.headers` 自定义头。请求体:
```json
{
  "event": "alert.firing",
  "id": 101,
  "rule": "Offline", "level": "critical", "status": "firing",
  "title": "节点离线",
  "message": "hk-01 已离线 35 秒(最近在线 2026-09-07 09:20:31 +08:00)",
  "value": 35, "threshold": 30,
  "node": { "id": 3, "name": "hk-01", "publicName": "HK-Node-01", "countryCode": "HK", "group": "生产", "hostname": "hk-01" },
  "startedAt": "2026-09-07T01:21:06Z", "resolvedAt": null, "sentAt": "2026-09-07T01:21:07Z",
  "site": { "name": "Server Node Monitor", "url": "https://monitor.example.com" }
}
```
2xx 视为成功;其它状态码/超时按 DATA §8.4 重试。测试消息 `event="test"`、`rule="Test"`、`node=null`。
