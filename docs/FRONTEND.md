> 本文件是最终采纳的设计(候选方案 A),已按实现落地;与实现的差异见 docs/IMPLEMENTATION_NOTES.md。

# FRONTEND — 管理后台(vue-naive-admin 2.x 改造)与公开大屏(候选方案 A)

> UI 文案简体中文;代码标识符英文。REST 契约见 API.md,实时契约见 PROTOCOL.md §5–§6。

---

## 0. 定案

| # | 定案 |
|---|---|
| 模板策略(Q6 前端侧) | 保留 vue-naive-admin 2.x(commit `a55f0a2`)的布局、路由守卫、`MeCrud/MeModal/CommonPage`、Pinia 持久化与图标体系;**删除**权限/角色/演示页面;**改造** env、vite 代理、http 拦截器(refresh)、登录页、用户菜单;**新增** 5 个业务页面 + 实时 store |
| 路由模式 | history(`VITE_USE_HASH=false`),`base=/admin/`,Master 对 `/admin/*` 回退 `admin/index.html` |
| 实时(Q7 前端侧) | 管理端 `src/realtime/adminHub.js` 单例连接 `/hubs/admin`(MessagePack,`accessTokenFactory`),写入 `useLiveStore`;大屏 `web/public/js/app.js` 连接 `/hubs/public`,`snapshot → batch` 增量,60 点 canvas 波形 |
| 图表 | ECharts 6 + vue-echarts 8(模板已有),按需注册组件,`echarts/core` |
| 构建输出 | `web/admin` → `src/SNM.Master/wwwroot/admin/`;`web/public` → `src/SNM.Master/wwwroot/`(`index.html`、`css/`、`js/`、`vendor/`);`scripts/build-web.sh` 统一执行;`wwwroot/` 整体不提交 |

---

## 1. 管理后台工程设置

### 1.1 环境文件

`web/admin/.env`:
```
VITE_TITLE = 'Server Node Monitor'
VITE_USE_HASH = 'false'
VITE_PUBLIC_PATH = '/admin/'
VITE_AXIOS_BASE_URL = '/api'
```
`.env.development`:`VITE_PROXY_TARGET = 'http://127.0.0.1:5080'`(其余同上);`.env.production`:同 `.env`。

### 1.2 `vite.config.js` 改动

- `base: VITE_PUBLIC_PATH`(保持)。
- `server.proxy`:
  ```js
  proxy: {
    '/api':  { target: VITE_PROXY_TARGET, changeOrigin: true },          // 删除原 rewrite(服务端路径本就以 /api 开头)
    '/hubs': { target: VITE_PROXY_TARGET, changeOrigin: true, ws: true },
    '/install': { target: VITE_PROXY_TARGET, changeOrigin: true },
  }
  ```
  删除 `/runapi`。
- `build.outDir: path.resolve(process.cwd(), '../../src/SNM.Master/wwwroot/admin')`,`build.emptyOutDir: true`。
- 其余插件保留(`pluginIcons`/`pluginPagePathes` 无害)。

### 1.3 `package.json`

- `name` → `snm-admin`;删除 `scripts.postinstall`、`simple-git-hooks`、`lint-staged` 字段与 `simple-git-hooks`、`lint-staged` 依赖(避免在 monorepo 根 `.git` 安装钩子);删除 `xlsx`(MeCrud 的导出改为可选:移除 `handleExport` 与 `xlsx` import)。
- 新增依赖:`@microsoft/signalr@10.0.11`、`@microsoft/signalr-protocol-msgpack@10.0.11`。
- 删除文件 `pnpm-lock.yaml`、`pnpm-workspace.yaml`;保留 `package-lock.json`(npm)。`.npmrc` 保留 `registry=https://registry.npmmirror.com`(可访问)。

### 1.4 `index.html`

`<html lang="zh-CN">`;保留 loading 骨架;`<link rel="icon" href="/favicon.png">` 由 Vite 按 `base` 重写(构建后核对为 `/admin/favicon.png`)。

---

## 2. 模板文件改造清单

### 2.1 修改

| 文件 | 改动 |
|---|---|
| `src/settings.js` | `basePermissions = []`(移除外链菜单);`defaultPrimaryColor='#2F6FED'`(可选);`layoutSettingVisible=true` 保留 |
| `src/api/index.js` | `getUser` 保留;`refreshToken: data => request.post('/auth/refresh', data, { needToken: false, needTip: false })`;`logout: data => request.post('/auth/logout', data, { needTip: false })`;删除 `switchCurrentRole`;保留 `getRolePermissions`、`validateMenuPath` |
| `src/store/modules/auth.js` | state 增加 `refreshToken`;`setToken({accessToken, refreshToken})` 同时保存;`persist.pick=['accessToken','refreshToken']`;`logout()` 前调用 `useLiveStore().disconnect()` 并 `api.logout({refreshToken})`(忽略错误);删除 `switchCurrentRole` |
| `src/utils/http/interceptors.js` | `resReject`:若 `status===401` 且请求不是 `/auth/login`、`/auth/refresh` 且 `config._retried!==true` → 调用单例 `refreshAccessToken()`(并发请求共享同一个 Promise)→ 成功则 `config._retried=true`、更新 `Authorization` 重发;失败或 `code===10011` → 走原 `resolveResError(401)` 弹窗登出 |
| `src/utils/http/helpers.js` | 403/404/500 分支改为 `message = message ?? '默认文案'`(优先显示服务端 `message`);401 分支保持;新增 `case 429: message='请求过于频繁,请稍后再试'` |
| `src/views/login/index.vue` | 删除验证码输入/图片/`initCaptcha`、“一键体验”按钮与 `quickLogin`;`lStorage` 只记住 `username`(不再保存密码);登录成功 `authStore.setToken(data)`(含 refreshToken);左侧横幅图替换为纯 CSS 渐变 + 标题“Server Node Monitor / 服务器节点监控”;删除 `login_banner.webp/login_bg.webp` 引用 |
| `src/views/login/api.js` | 仅保留 `login` |
| `src/store/helper.js` | `getUserInfo` 保持字段;`getPermissions` 保持(服务端返回菜单树) |
| `src/router/basic-routes.js` | `Home` 的 `meta.title='总览大盘'`,`meta.keepAlive=true` |
| `src/views/home/index.vue` | **重写为总览大盘**(§5.2) |
| `src/layouts/components/UserAvatar.vue` | 删除“切换角色”项与 `RoleSelect`;第二行显示 `管理员` 固定文案 |
| `src/layouts/components/index.js` | 移除 `RoleSelect`、`BeginnerGuide` 导出 |
| `src/layouts/normal/header/index.vue`、`src/layouts/full/header/index.vue` | 删除 `BeginnerGuide`、GitHub/Gitee 图标;在 `ToggleTheme` 左侧插入 `<LiveStatus />`(实时连接指示,§4.3) |
| `src/components/common/TheFooter.vue` | 文案 `Server Node Monitor · 版本 {{ version }}`(版本来自 `useSystemStore` 的 `/api/system/info`,加载前显示空) |
| `src/layouts/components/SideLogo.vue`、`src/components/common/TheLogo.vue` | logo 改为 `@/assets/images/logo.svg`(新建,简单的节点/脉搏图形) |
| `src/components/me/crud/index.vue` | 删除 `xlsx` 导入与 `handleExport`;其余不变 |
| `src/views/profile/index.vue` | 保留;删除“更改头像”说明文字中的模板提示 |
| `src/views/error-page/404.vue` / `403.vue` | 保留 |
| `src/App.vue` | 增加:`watch(() => authStore.accessToken, token => token ? liveStore.connect() : liveStore.disconnect(), { immediate: true })` |
| `src/store/index.js` | 增加 `export * from './modules/live'`、`./modules/system` |
| `README.md` | 替换为项目说明 |

### 2.2 删除

`src/views/pms/**`、`src/views/demo/**`、`src/views/base/**`、`src/views/iframe/**`、`src/layouts/components/RoleSelect.vue`、`src/layouts/components/BeginnerGuide.vue`、`src/assets/icons/isme/{apifox,gitee,docs,naiveui,awesome}.svg`、`src/assets/images/{isme.png,login_banner.webp,login_bg.webp}`、`pnpm-lock.yaml`、`pnpm-workspace.yaml`。`vue3-intro-step` 依赖随 `BeginnerGuide` 删除(同时移除 `vite.config.js` 的 `optimizeDeps.include`)。

### 2.3 新增

| 文件 | 内容 |
|---|---|
| `src/realtime/adminHub.js` | SignalR 连接单例(§4) |
| `src/store/modules/live.js` | `useLiveStore`(§4.2) |
| `src/store/modules/system.js` | `useSystemStore`:`/api/system/info` 与 `/api/settings` 缓存(站点标题、时区) |
| `src/utils/format.js` | `formatBytes(n, digits=1)`(B/KB/MB/GB/TB,1024 进制)、`formatBps(n)`(`1.2 MB/s`)、`formatPermille(p)`(`23.7%`)、`formatDuration(sec)`(`3天 4小时`/`12分钟`)、`formatRelative(ts)`(`5 秒前`)、`formatDateTime(iso)`(按浏览器时区 `YYYY-MM-DD HH:mm:ss`)、`percentColor(pct)`(<60 绿 `#18a058`、<85 橙 `#f0a020`、否则红 `#d03050`) |
| `src/utils/flag.js` | `flagEmoji(cc)`(两字母 → 区域指示符;空 → `🏳️`)、`countryName(cc)`(中文名映射,≈ 250 项) |
| `src/constants/snm.js` | `RULE_NAMES {1:'离线',2:'CPU 高负载',3:'流量预警',4:'流量超限',5:'即将到期',6:'磁盘告急'}`、`SEVERITY {1:'提示',2:'警告',3:'严重'}`、`STATUS {0:'未知',1:'在线',2:'离线'}`、`CURRENCIES ['USD','CNY','EUR']`、`BILLING_CYCLES [{0,'无'},{1,'月付'},{3,'季付'},{6,'半年付'},{12,'年付'},{24,'两年付'},{36,'三年付'}]`、`COUNT_MODES [{0,'上行+下行'},{1,'仅上行'},{2,'仅下行'},{3,'取较大值'}]`、`TIMEZONES`(常用 IANA 列表,`Intl.supportedValuesOf('timeZone')` 可用时用之) |
| `src/components/snm/NodeStatusTag.vue` | `props: status, lastSeen` → `NTag`(在线绿/离线红/未知灰)+ tooltip “最后上报 x 秒前” |
| `src/components/snm/MiniBar.vue` | `props: value(0–100), label, width=90` → 细进度条 + 百分比文字,颜色 `percentColor` |
| `src/components/snm/TrafficBar.vue` | `props: used, limit, mode` → `已用 621 GB / 1 TB` + 进度条;`limit=0` 显示 `不限` |
| `src/components/snm/FlagName.vue` | 国旗 + `publicName` + 小字 `adminRemark` |
| `src/components/snm/IpList.vue` | 主 IP + `+N` 的 `NPopover` 全量列表(公网/内网标签、来源图标、复制按钮) |
| `src/components/snm/NodeSparkline.vue` | canvas 60 点迷你波形(复用大屏的绘制函数,复制到 `src/utils/sparkline.js`) |
| `src/components/snm/MetricChart.vue` | 封装 `VChart`:`props: metric, range, points, dark` → ECharts option(§5.4) |
| `src/components/snm/InstallScriptModal.vue`、`NodeFormModal.vue`、`ChannelFormModal.vue`、`KeyRevealModal.vue` | 见各页面 |
| `src/views/nodes/index.vue`、`detail.vue`、`api.js` | 节点管理/详情 |
| `src/views/alerts/index.vue`、`api.js` | 告警记录 |
| `src/views/settings/index.vue`、`components/{SiteTab,AgentTab,AlertTab,ChannelsTab,FinanceTab,SecurityTab,GeoIpTab,SystemTab}.vue`、`api.js` | 系统设置 |
| `src/views/home/api.js` | `getSummary` |
| `src/assets/images/logo.svg` | |

按模板惯例,每个视图目录一个 `api.js`:

```js
// src/views/nodes/api.js
import { request } from '@/utils'
export default {
  list: params => request.get('/nodes', { params }),
  get: id => request.get(`/nodes/${id}`),
  create: data => request.post('/nodes', data),
  update: (id, data) => request.patch(`/nodes/${id}`, data),
  remove: id => request.delete(`/nodes/${id}`),
  rotateKey: id => request.post(`/nodes/${id}/rotate-key`),
  revealKey: id => request.post(`/nodes/${id}/reveal-key`),
  reorder: ids => request.post('/nodes/reorder', { ids }),
  installScript: (id, params) => request.get(`/nodes/${id}/install-script`, { params }),
  metrics: (id, range) => request.get(`/nodes/${id}/metrics`, { params: { range } }),
  traffic: (id, periods = 12) => request.get(`/nodes/${id}/traffic`, { params: { periods } }),
  resetTraffic: id => request.post(`/nodes/${id}/traffic/reset`, { scope: 'period' }),
  alerts: (id, params) => request.get(`/nodes/${id}/alerts`, { params }),
}
```

---

## 3. 认证与 HTTP 层流程

```
登录页 → POST /api/auth/login → authStore.setToken({accessToken, refreshToken}) → router.push(redirect || '/')
permission-guard → GET /api/user/detail + GET /api/role/permissions/tree → 动态添加路由 → 菜单
任一请求 401 → refreshAccessToken()(单例 Promise)→ POST /api/auth/refresh {refreshToken}
   成功 → setToken(新对) → 重放原请求(仅一次)→ liveStore.reconnect()(新 token)
   失败 → $dialog “登录已过期,是否重新登录?” → authStore.logout()
退出 → POST /api/auth/logout {refreshToken} → 清 store → /login
```

`refreshAccessToken` 位于 `src/utils/http/refresh.js`,避免循环依赖(只 import `useAuthStore` 与 `axios` 原生实例,不走带拦截器的 `request`)。

---

## 4. 实时层(管理端)

### 4.1 `src/realtime/adminHub.js`

```js
import * as signalR from '@microsoft/signalr'
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack'
let connection = null
export function createAdminHub({ getToken, onSnapshot, onBatch, onAlert, onNodesChanged, onState }) {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/admin', { accessTokenFactory: getToken, transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling })
    .withHubProtocol(new MessagePackHubProtocol())
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ctx => Math.min(60000, [0, 2000, 5000, 10000, 30000][ctx.previousRetryCount] ?? 60000) })
    .configureLogging(signalR.LogLevel.Warning)
    .build()
  connection.serverTimeoutInMilliseconds = 45000
  connection.keepAliveIntervalInMilliseconds = 15000
  connection.on('snapshot', onSnapshot); connection.on('batch', onBatch)
  connection.on('alert', onAlert); connection.on('nodesChanged', onNodesChanged)
  connection.onreconnecting(() => onState('reconnecting'))
  connection.onreconnected(async () => { onState('connected'); onSnapshot(await connection.invoke('GetSnapshot')) })
  connection.onclose(() => onState('disconnected'))
  return connection
}
```

### 4.2 `useLiveStore`(Pinia,不持久化)

| state | 说明 |
|---|---|
| `state: 'idle'|'connecting'|'connected'|'reconnecting'|'disconnected'` | 头部指示 |
| `nodes: Map<number, AdminNodeLiveDto>`(用 `shallowRef` + 手动 `triggerRef` 保证 100 节点 × 2 s 的更新开销可控) | 当前值 |
| `history: Map<number, {ts:[],cpu:[],mem:[],rx:[],tx:[]}>` | 每节点 ≤ 60 点;`batch` 到达时 push(`mem` 用 `memUsedMb/memTotalMb` 由页面换算,store 存 MB) |
| `lastServerTs` | |
| `alerts: AdminAlertDto[]`(最近 50 条) | 首页“进行中”与全局通知 |
| `nodesChangedTick` | `nodesChanged` 计数器,页面 `watch` 后刷新 REST |

actions:`connect()`(需要 token;`state='connecting'`;`start()` 后 `state='connected'`,快照由服务端主动发送)、`disconnect()`、`reconnect()`(token 刷新后 `stop()`→`start()`)、`ensureHistory(nodeId)`(详情页打开时 `invoke('GetHistory', id)` 填充)。`alert` 事件 → `$notification[severity==3?'error':'warning']({ title, content: message, duration: 8000 })` + 追加 `alerts`。

### 4.3 `LiveStatus.vue`(头部)

圆点 + 文案:`实时已连接` 绿 / `重连中…` 橙(脉冲动画)/ `未连接` 灰;点击重连。

---

## 5. 页面规格

通用:所有页面使用 `CommonPage`(带标题栏)或 `AppPage`;表格 `NDataTable`,分页 `pageNo/pageSize`;所有时间用 `formatDateTime`(浏览器时区)并在 tooltip 给 ISO 原值;数字千分位。

### 5.1 登录页 `/login`(`views/login/index.vue`)

字段:用户名(必填,≤ 32)、密码(必填,≤ 64);回车提交;错误 `10001/10002` 直接 `$message.error(message)`。记住用户名开关。

### 5.2 总览大盘 `/`(`views/home/index.vue`,菜单 `Home`)

数据源:`GET /api/dashboard/summary`(进入页面与每 60 s)+ `useLiveStore`(实时)。

布局(自上而下):
1. **统计卡片行**(`n-grid` 6 列,窄屏 2–3 列):`节点总数`、`在线`(绿)、`离线`(红,点击跳 `/nodes?status=2`)、`进行中告警`(点击跳 `/alerts?status=1`)、`7 天内到期`、`月度支出(MRR)`(`¥356.42`,副文 `USD 41.66 · CNY 56.00`)。在线/离线数以 live store 为准实时变化。
2. **节点状态概览**(左 2/3):紧凑表格,列 `节点`(FlagName)、`状态`(NodeStatusTag)、`CPU`(MiniBar)、`内存`(MiniBar)、`网速`(`↓ 1.2 MB/s ↑ 380 KB/s`)、`流量`(TrafficBar)、`到期`(daysLeft 标签);按状态离线优先、再按排序;点击行进入详情。
3. **进行中告警**(右 1/3):`AlertEvent` 列表(级别色条 + 标题 + 相对时间 + 确认按钮)。
4. **最近告警**(时间线,10 条)与 **即将到期**(表:节点、到期日、剩余天、供应商、价格、续费链接)。

### 5.3 节点管理 `/nodes`(`views/nodes/index.vue`)

查询栏:`关键词`(名称/备注/主机名/IP)、`状态`(全部/在线/离线/未知)、`启用`(全部/是/否)。操作栏按钮:`新建节点`、`拖拽排序`(切换到可拖拽模式,保存调用 `reorder`)。

表格列(`scroll-x=1600`):

| 列 | 内容 | 宽 |
|---|---|---|
| 节点 | `FlagName`(国旗 + PublicName;下行小字 AdminRemark) | 220 |
| 状态 | `NodeStatusTag` + 最后上报相对时间 | 120 |
| IP | `IpList`(主公网 IP + `+N`) | 170 |
| 系统 | `os` 第一段 + `arch`;下行 `cpuCores C / memTotal G` | 180 |
| CPU | `MiniBar`(live.cpuPermille/10) | 110 |
| 内存 | `MiniBar`(memUsed/memTotal) | 110 |
| 磁盘 | `MiniBar`(Σused/Σtotal),tooltip 各挂载点 | 110 |
| 网速 | `↓ rx` / `↑ tx`(`formatBps`) | 140 |
| 流量 | `TrafficBar` | 180 |
| 到期 | 日期 + `剩余 N 天` 标签(≤7 橙、≤1/过期红、无则 `—`) | 130 |
| 操作 | `详情` `编辑` `安装脚本` `更多▾`(查看密钥 / 轮换密钥 / 清零本期流量 / 启用·禁用 / 删除) | 260,固定右 |

实时:`watch(liveStore.nodes)` 按 `id` 更新行内 live 字段(不重新请求);`nodesChangedTick` 变化 → 重新 `list`。

**新建/编辑弹窗 `NodeFormModal`**(`MeModal` 640px,`n-tabs`):

| 页签 | 字段(表单 path) | 控件 | 校验 |
|---|---|---|---|
| 基本 | `publicName` | `n-input` | 必填 1–64 |
| | `adminRemark` | `n-input` | ≤ 256 |
| | `enabled`、`publicVisible` | `n-switch` | |
| | `sortOrder` | `n-input-number` | 整数 |
| | `countryCodeOverride` | `n-select` 可搜索(`自动识别` + 国家列表,显示国旗+中文名) | |
| | `timeZoneId` | `n-select` 可搜索(`跟随全局设置` + TIMEZONES) | |
| | `intervalMs` | `n-input-number` step 500 | 1000–60000 |
| | `notes` | `n-input type=textarea` | ≤ 2000 |
| 流量 | `traffic.limitValue` + `traffic.limitUnit`(GB/TB) | 数字 + 单位选择 → 提交换算 `limitBytes`(1 GB = 10⁹ B,与 VPS 商家一致;单位说明文字) | ≥ 0 |
| | `traffic.countMode` | `n-radio-group` COUNT_MODES | |
| | `traffic.resetDay` | `n-input-number` | 1–31,提示“大于当月天数时按月末计” |
| 财务 | `finance.vendor` | `n-input` | ≤ 64 |
| | `finance.price` | `n-input`(字符串,`/^\d+(\.\d{1,2})?$/`) | |
| | `finance.currency` | `n-select` CURRENCIES | |
| | `finance.billingCycleMonths` | `n-select` BILLING_CYCLES | |
| | `finance.expiresAt` | `n-date-picker type=date`(value-format `yyyy-MM-dd`) | |
| | `finance.autoRenew` | `n-switch` | |
| | `finance.renewUrl` | `n-input` | http(s) |
| 告警 | `alerts.alertsEnabled` | `n-switch` | |
| | `alerts.cpuAlertPct`/`trafficAlertPct`/`offlineAlertSec`/`diskAlertPct` | `n-input-number` placeholder `留空 = 全局 (90)`(占位显示全局当前值,来自 `useSystemStore.settings`) | 范围同 API |

创建成功后自动打开 **安装脚本弹窗** 并展示一次性的完整 Key 提示。

**安装脚本弹窗 `InstallScriptModal`**:页签 `Linux` / `Windows`;顶部一键命令(`n-input readonly` + 复制按钮);说明行“令牌有效期至 {expiresAt},脚本内含该节点专属密钥,请勿泄露”;`重新生成令牌` 按钮(`renew=true`);可折叠 `查看完整脚本`(`n-code` 等宽);`带代理安装` 提示:`| sudo bash -s -- --proxy socks5://10.0.0.1:1080`。

**查看密钥 `KeyRevealModal`**:二次确认 → `reveal-key` → 显示 Key + 复制;**轮换密钥**:`$dialog.warning('轮换后旧密钥立即失效,需要重新安装或更新探针配置')` → 成功后显示新 Key。

### 5.4 节点详情 `/nodes/:id`(`views/nodes/detail.vue`,隐藏菜单,标签页标题“节点详情 · {publicName}”)

数据源:`GET /api/nodes/{id}`(进入 + `nodesChangedTick`)、`liveStore.ensureHistory(id)`、`GET /api/nodes/{id}/metrics?range`、`GET /api/nodes/{id}/traffic`、`GET /api/nodes/{id}/alerts`。

区块:
1. **头部卡**:国旗 + 名称 + 状态标签 + 备注;右侧按钮 `编辑`、`安装脚本`、`返回`。`n-descriptions`(3 列):主机名、系统、内核、架构、虚拟化、CPU(型号 / 核数)、内存 / Swap 总量、探针版本 / 协议、运行时长(`formatDuration(live.up)`)、开机时间、心跳间隔、时区、服务端捕获 IP、最后注册时间。
2. **IP 列表卡**:表格 `地址`、`类型`(公网/内网)、`族`、`来源`(探针 / 服务端 / 两者)、`首次/最近出现`;国家码显示“自动识别 US · 覆盖 —”。
3. **实时指标卡**(每 2 s 更新):四个 `n-progress type=circle`(CPU %、内存 %、Swap %、磁盘 %),右侧数字:`↓ rx ↑ tx`、`负载 0.45`、`Swap 0 MB / 8 GB`、`本账期已用 621 GB / 1 TB (62.1%)`;下方 `NodeSparkline`(最近 2 分钟 CPU + 网速)。
4. **历史图表卡**:`n-radio-group` 范围 `24 小时 / 7 天 / 30 天`;`n-tabs` 指标 `CPU / 内存 / 网络速率 / 流量 / 负载 / 磁盘`;`MetricChart`(高 320)。选项:
   - 公共:`xAxis type:'time'`(`ts*1000`),`tooltip trigger:'axis'`,`dataZoom`(24h 时 `slider` + `inside`),`grid {left:56,right:24,top:32,bottom:56}`,暗色跟随 `appStore.isDark`(`theme` 传 `'dark'`);`connectNulls:false`。
   - CPU:series `平均`(`cpuAvg/10`,area 渐变)、`峰值`(`cpuMax/10`,虚线);y 0–100 `%`。
   - 内存:`已用(均值)` `memUsedAvgMb/1024` GB、`峰值`;y 上限 `memTotalMb/1024`。
   - 网络速率:`下行 rxBpsAvg`、`上行 txBpsAvg`(y 轴 `formatBps`);峰值作为 tooltip 附加。
   - 流量:柱状 `rxBytes`/`txBytes` 堆叠(y `formatBytes`);tooltip 合计。
   - 负载:`load1Avg/100` 折线 + `load1Max/100` 虚线;参考线 `cpuCores`。
   - 磁盘:`diskUsedMb/diskTotalMb*100` 折线,y 0–100 %。
   - 无数据:`n-empty` “该时间范围内暂无数据(需运行 ≥ 1 分钟)”。
5. **流量卡**:当前账期进度条(`periodStart ~ periodEnd`,`时区`)、`n-statistic` 下行/上行/计费;`日流量` 柱状图(`daily`,rx/tx 堆叠,x 为日期);`历史账期` 表(账期、下行、上行、计费、限额、状态);按钮 `清零本期流量`(二次确认)。
6. **告警卡**:该节点告警分页表(同 §5.5 列的子集)。

### 5.5 告警记录 `/alerts`(`views/alerts/index.vue`)

查询栏:`节点`(`n-select` 可搜索,来自 `/api/nodes?pageSize=0`)、`规则`(RULE_NAMES)、`级别`、`状态`(进行中/已恢复)、`确认`(全部/未确认/已确认)、`时间范围`(`n-date-picker type=datetimerange`)。操作栏:`清理 90 天前已恢复记录`(`DELETE /api/alerts?before=&status=2`)。

表格列:`时间`(startedAt;下行 `已恢复 ·` resolvedAt / 持续时长)、`节点`(FlagName,点击进详情)、`规则`(标签)、`级别`(提示灰/警告橙/严重红)、`状态`(进行中红点 / 已恢复绿)、`内容`(title + message,`ellipsis tooltip`)、`通知`(`deliveries` 成功数/总数;失败时红色并 tooltip 错误)、`确认`(`acknowledgedAt` 或 `确认` 按钮)、`操作`(`详情` 抽屉:全部字段 + 投递记录表)。

实时:`liveStore.alerts` 新事件到达且当前筛选包含时,顶部出现“有新的告警,点击刷新”。

### 5.6 系统设置 `/settings`(`views/settings/index.vue`,`n-tabs type=line`,每页签独立表单 + `保存` 按钮 → `PATCH /api/settings` 仅提交该页签子对象)

| 页签 | 字段(设置键) | 控件/校验 |
|---|---|---|
| 站点 | `site.title`、`site.publicTitle`、`site.publicSubtitle`、`site.publicBaseUrl`(必须以 http(s) 开头,提示“用于生成安装脚本地址”)、`site.timeZone`(TIMEZONES)、`public.showSpecs`、`public.showTraffic` | 文本/开关 |
| 探针 | `agent.releaseBaseUrl`、`agent.defaultIntervalMs`、`agent.statusIntervalSec`、`agent.installTokenTtlHours` | 数字范围同 DATA.md §6;说明“修改状态上报间隔会即时下发到在线探针” |
| 告警阈值 | `alert.enabled`、`alert.offlineTimeoutSec`、`alert.offlineConsecutive`、`alert.cpuPct`、`alert.cpuSustainMin`、`alert.trafficWarnPct`、`alert.expiryDays`、`alert.expiryCheckHour`、`alert.cooldownMin`(30–60)、`alert.repeatMin`、`alert.diskEnabled`、`alert.diskPct` | `n-input-number` + 单位后缀;每项右侧灰字说明 |
| 通知渠道 | 表格(`名称`、`类型`、`启用`开关、`规则范围`(全部/N 条)、`最低级别`、`最近成功`、`最近错误`、操作 `测试` `编辑` `删除`)+ `新增渠道` → `ChannelFormModal`:`type`(Telegram/Webhook 单选,新建后不可改)、`name`、`enabled`、`ruleMask`(多选 RULE_NAMES,空 = 全部)、`minSeverity`;Telegram:`botToken`(密码框,编辑时占位 `****` 表示保留)、`chatId`、`parseMode`(HTML/Markdown)、`disableNotification`;Webhook:`url`、`method`、`secret`(密码框)、`headers`(键值对动态列表 ≤ 10)、`timeoutSec`、`bodyTemplate`(textarea,折叠的占位符说明);弹窗底部 `发送测试`(未保存时调用 `/channels/test`) | |
| 财务 | `finance.baseCurrency`、`finance.rates.USD/CNY/EUR`(说明“1 单位该货币 = ? 基准货币”) | |
| 安全 | `auth.accessTokenMinutes`、`auth.refreshTokenDays`、`auth.loginMaxFailures`、`auth.loginLockMinutes`;卡片 `修改密码`(旧/新/确认,8–64)→ `/auth/password`;按钮 `退出所有设备`(`/auth/logout {all:true}` → 本地登出) | |
| GeoIP | 状态(`ready`、`lastRefreshUtc`、行数、`lastError`)、`立即刷新` 按钮(loading ≤ 60 s)、`geoip.enabled` 开关、只读数据源地址 | |
| 系统 | `/api/system/info` 全部字段(`n-descriptions`)、保留策略只读表(25 h / 8 d / 31 d / 告警 180 d 可改 / 投递 30 d 可改 / 日流量 400 d 可改)、备份说明文字(指向 DEPLOY.md §7) | |

### 5.7 个人资料 `/profile`

模板页面保留(昵称/头像/邮箱 + 修改密码);`gender/address` 字段保留 UI 但服务端忽略。

---

## 6. 中文文案总表(节选,保证一致)

| 场景 | 文案 |
|---|---|
| 菜单 | 总览大盘 / 节点管理 / 告警记录 / 系统设置 / 个人资料 |
| 状态 | 在线 / 离线 / 未知 / 已禁用 |
| 按钮 | 新建节点 / 编辑 / 详情 / 安装脚本 / 查看密钥 / 轮换密钥 / 清零本期流量 / 删除 / 保存 / 取消 / 复制 / 重新生成令牌 / 发送测试 / 立即刷新 / 确认 / 退出所有设备 |
| 确认框 | 确定删除节点“{name}”?其全部历史数据将被清除。 / 轮换后旧密钥立即失效,需要重新安装或更新探针配置,是否继续? / 确定将本账期已用流量清零? |
| 空态 | 暂无节点,点击“新建节点”开始 / 暂无告警 / 该时间范围内暂无数据(需运行 ≥ 1 分钟) |
| 成功提示 | 保存成功 / 已复制到剪贴板 / 测试消息已发送 / 密钥已轮换 / 已确认 |
| 实时 | 实时已连接 / 重连中… / 未连接(点击重试) |
| 安装脚本 | 在目标服务器以 root 执行以下命令: / 令牌有效期至 {time} / 脚本包含该节点专属密钥,请勿泄露 |

---

## 7. 公开大屏 `web/public`

### 7.1 文件

```
web/public/
  package.json                     仅 devDependencies: @microsoft/signalr, @microsoft/signalr-protocol-msgpack(供 build-web.sh 复制 vendor)
  index.html
  css/app.css
  js/app.js                        连接、状态、渲染
  js/format.js                     formatBytes/formatBps/formatDuration/flagEmoji(与后台同实现,复制一份,无构建)
  js/sparkline.js                  canvas 波形绘制
  favicon.svg
  vendor/                          构建时复制:signalr.min.js、signalr-protocol-msgpack.min.js(不提交)
```

`index.html` 以 `<script src="vendor/signalr.min.js">`、`<script src="vendor/signalr-protocol-msgpack.min.js">`、`<script type="module" src="js/app.js">` 引入;`<meta name="robots" content="noindex">`;`<html lang="zh-CN">`;主题:`prefers-color-scheme` 自动 + 右上角切换(localStorage `snm-theme`)。

### 7.2 布局

- **头部**:标题(`site.title`)、副标题、汇总 `在线 10 / 12`、当前时间(每秒)、主题切换。
- **网格**:`display:grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap:16px`;移动端单列。节点按 `order, id` 排序(离线不改变顺序,仅降低不透明度)。
- **底部**:连接状态(`已连接 · 最后更新 12:34:56` / `连接中断,正在重连…` 橙色横幅置顶)、`Powered by Server Node Monitor`。

### 7.3 节点卡片内容(仅 `PublicNodeDto` 字段)

```
┌────────────────────────────────────────────┐
│ 🇭🇰 HK-Node-01                    ● 在线    │  ← name、cc、status(在线绿/离线红/未知灰)
│ 80 核 · 377 GB · 2.0 TB · 已运行 10 天       │  ← showSpecs 时:cores/memMb/diskMb + up
│ CPU  ▇▇▇▇▇▁▁▁▁▁ 23.7%                       │  ← cpu‰
│ 内存 ▇▇▇▇▁▁▁▁▁▁ 45.1%    磁盘 ▇▇▁▁ 23.0%    │  ← mem‰ / disk‰
│ ↓ 1.2 MB/s   ↑ 380 KB/s                     │  ← rx/tx
│ 本月流量 621 GB / 1 TB ▇▇▇▇▇▇▁▁▁▁ 62%        │  ← showTraffic 且 tLimit>0:tUsed/tLimit
│ ╭──────────────────────────────────────╮    │
│ │   ~~~~ CPU 面积  —— 下行  ---- 上行   │    │  ← 2 分钟 60 点波形
│ ╰──────────────────────────────────────╯    │
│ 最后上报 2 秒前                              │  ← ts
└────────────────────────────────────────────┘
```

离线:卡片加 `.offline`(灰度 + 60% 不透明度),状态文字 `离线 · 3 分钟`(`now − ts`),波形停止推进。未知(从未上报):`等待首次上报`。

### 7.4 波形(`js/sparkline.js`)

- `drawSparkline(canvas, {cpu, rx, tx}, {dark})`:宽高随容器(`devicePixelRatio` 缩放);CPU 以 0–1000‰ 映射到左轴,填充半透明主色;rx/tx 以两者的窗口最大值(至少 10 KB/s)自适应映射到右轴,rx 实线、tx 虚线;点数不足 60 时右对齐;无点画基线。
- 数据:`snapshot.hist` 初始化;每个 `batch` 对包含的节点 `push({cpu, mem, rx, tx})` 并裁剪到 60;未包含的节点不推(服务端只发 dirty 节点,因此离线节点自然停住)。
- 绘制节流:`requestAnimationFrame` 合并一帧内的所有卡片重绘;`document.hidden` 时暂停绘制(数据继续累积)。

### 7.5 连接生命周期(`js/app.js`)

```js
const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/public', { transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling })
  .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
  .withAutomaticReconnect({ nextRetryDelayInMilliseconds: c => [0, 2000, 5000, 10000, 30000][c.previousRetryCount] ?? 60000 })
  .configureLogging(signalR.LogLevel.Warning).build()
conn.serverTimeoutInMilliseconds = 45000; conn.keepAliveIntervalInMilliseconds = 15000
conn.on('snapshot', applySnapshot)      // 替换 site/nodes/hist,重建卡片 DOM
conn.on('batch', applyBatch)            // 更新 live + 推波形点 + 标记脏卡片
conn.on('nodes', applyNodes)            // 替换元数据(名称/国家/顺序/规格/限额),保留 hist
conn.onreconnecting(() => setBanner('连接中断,正在重连…'))
conn.onreconnected(async () => { clearBanner(); applySnapshot(await conn.invoke('GetSnapshot')) })
conn.onclose(() => setBanner('连接已关闭,请刷新页面'))   // 自定义重试策略永不返回 null,理论上不触发
start(): conn.start().catch(() => setTimeout(start, 5000))
```

页面加载**不发起任何 `fetch`/`XMLHttpRequest`**;唯一 HTTP 请求是 SignalR 的 negotiate(`POST /hubs/public/negotiate`)与静态资源。每秒定时器只更新时钟与“x 秒前”文字。

### 7.6 脱敏检查清单(大屏)

- [ ] 页面 JS 只读取 `PublicSnapshotDto/PublicBatchDto/PublicNodeDto/PublicNodeLiveDto/PublicHistoryDto/PublicSiteDto` 中列出的键;不存在 `ip`、`hostname`、`remark`、`vendor`、`price`、`expires`、`key` 等字符串。
- [ ] 不引用 `/api/*`;DevTools 网络面板仅 `negotiate` + WebSocket + 静态文件。
- [ ] 生产构建无 `console.log` 打印载荷。
- [ ] `robots noindex`;不在 `document.title` 之外泄露信息。
- [ ] 自动化:`PublicHubSanitizationTests`(服务端)+ `scripts/e2e.sh` 第 7 步字节检查。

### 7.7 大屏文案

`在线 {n} / {total}`、`已运行 {d} 天 {h} 小时`、`本月流量`、`不限`、`最后上报 {x} 前`、`离线 · {duration}`、`等待首次上报`、`连接中断,正在重连…`、`已连接 · 最后更新 {time}`、`{n} 核 · {mem} · {disk}`。

---

## 8. 构建与复制流水线

### 8.1 `scripts/build-web.sh`

```bash
#!/usr/bin/env bash
# Builds web/admin (Vite) and copies web/public + vendor bundles into the Master's wwwroot.
# Usage: scripts/build-web.sh [--out <dir>] [--skip-install]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="src/SNM.Master/wwwroot"; SKIP=0
while [ $# -gt 0 ]; do case "$1" in --out) OUT="$2"; shift;; --skip-install) SKIP=1;; *) echo "unknown arg $1"; exit 2;; esac; shift; done

if [ "$SKIP" = 0 ]; then (cd web/admin && npm ci --no-audit --no-fund); (cd web/public && npm ci --no-audit --no-fund); fi

# admin SPA -> $OUT/admin (vite outDir is relative to web/admin; override via env when --out differs)
(cd web/admin && VITE_OUT_DIR="$(cd "$(dirname "$OUT")" && pwd)/$(basename "$OUT")/admin" npm run build)

# public dashboard -> $OUT root
mkdir -p "$OUT/vendor" "$OUT/css" "$OUT/js"
cp web/public/index.html web/public/favicon.svg "$OUT/"
cp web/public/css/*.css "$OUT/css/"
cp web/public/js/*.js "$OUT/js/"
cp web/public/node_modules/@microsoft/signalr/dist/browser/signalr.min.js "$OUT/vendor/"
cp web/public/node_modules/@microsoft/signalr-protocol-msgpack/dist/browser/signalr-protocol-msgpack.min.js "$OUT/vendor/"
echo "web assets written to $OUT"
```

`vite.config.js` 的 `build.outDir` 读取 `process.env.VITE_OUT_DIR || path.resolve(process.cwd(), '../../src/SNM.Master/wwwroot/admin')`。`.gitignore` 建议改为忽略整个 `src/SNM.Master/wwwroot/`(由编排者维护)。Master 在 Development 且 `wwwroot/index.html` 缺失时,`/` 返回纯文本提示“run scripts/build-web.sh”。

### 8.2 开发流程

- `scripts/dev.sh`:`source scripts/env.sh` → 后台启动 Master(`SNM_DATA_DIR=./data/dev SNM_ADMIN_PASSWORD=admin123 ASPNETCORE_ENVIRONMENT=Development Snm__Dev__PublicSourceDir=web/public dotnet run --project src/SNM.Master`)→ 等待 `/healthz` → 若 `./data/dev/agent.key` 不存在则登录并创建节点 `本机` 保存 Key → 启动 Agent(`dotnet run --project src/SNM.Agent -- run --server http://127.0.0.1:5080 --key $(cat ./data/dev/agent.key) --log-level debug`)→ `Ctrl+C` 同时结束。
- 前端:`cd web/admin && npm run dev`(`http://localhost:3200/admin/`,代理到 5080);大屏直接访问 `http://127.0.0.1:5080/`(Development 从 `web/public` 实时读取;vendor 需先跑一次 `build-web.sh` 或手动复制)。

### 8.3 验收清单(M7/M8)

- [ ] `npm run build` 0 错误;`wwwroot/admin/index.html` 引用以 `/admin/assets/` 开头。
- [ ] 登录 → 菜单 5 项(含隐藏 2 项不显示)→ 节点页表格列完整、实时数字每 2 s 变化。
- [ ] 详情页三个范围的图表有数据(运行 ≥ 1 min / 1 h / 1 d 后分别出现)。
- [ ] 告警页收到离线/恢复事件并弹通知。
- [ ] 设置页每个页签保存成功并回显;渠道测试成功。
- [ ] token 过期后(可把 `auth.accessTokenMinutes` 设 5 验证)自动刷新且实时连接自动重连。
- [ ] 大屏:卡片 ≤ 2 s 更新;断开 Master 出现横幅;恢复后自动重连并重同步;网络面板无 `/api` 请求;`view-source` 与载荷中不含任何 IP/主机名/备注。
