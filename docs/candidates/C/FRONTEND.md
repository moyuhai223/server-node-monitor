# FRONTEND.md — 管理后台与公开大屏(候选设计 C)

> 面向 M6(管理后台)与 M7(公开大屏)实现者。REST 形状以 API.md 为准,实时消息以 PROTOCOL §4 为准,本文只定义**前端如何消费**与**页面长什么样**。UI 文案简体中文,代码标识符英文。

---

## 0. 决策摘要

| 问题 | 结论 |
|---|---|
| 模板策略(Q6 前端侧) | 保留 vue-naive-admin 2.x(commit `a55f0a2e`)的壳:布局、Tab、主题、路由守卫、`permission` store、`http` 封装、`useCrud/useForm/useModal`、`MeCrud/MeModal` 组件全部不改;**改造 9 个文件、删除 3 组目录、新增 7 个页面目录与 1 个实时层**。菜单树来自服务端 `GET /api/role/permissions/tree`(API §2),前端 `basePermissions` 清空。 |
| 认证改造 | `auth` store 增加 `refreshToken`;响应拦截器对 `401` 先用 `POST /api/auth/refresh/token` 静默刷新一次并重放原请求(并发请求共享同一个刷新 Promise),刷新失败才弹"登录已过期";登录页去掉验证码、一键体验,`记住我` 仅记用户名。 |
| 实时消费(Q7) | 单例 `useRealtime()`(`src/composables/useRealtime.js`)在登录后建立 `/hubs/admin` 连接,写入 Pinia `live` store(`nodes: Map<id, AdminNodeLive>`、`hist: Map<id, LivePoint[]>`、`state`);页面只读 store,不直接碰连接。`Snapshot` 整体替换、`Tick` 局部合并、`NodeChanged` upsert、`NodeRemoved` 删除、`Alert` 推通知 + 追加到告警页。 |
| 图表 | ECharts 6 + vue-echarts 8,按需注册(`echarts/core` + `LineChart/BarChart` + `Grid/Tooltip/Legend/DataZoom` + `CanvasRenderer`),一个 `useEChartTheme()` 跟随暗色模式。历史图数据源 `GET /api/nodes/{id}/metrics?range=`,实时波浪图数据源 `live.hist`。 |
| 公开大屏 | 纯静态 `index.html + css/app.css + js/app.js`,零框架、零 `/api` 请求;SignalR 两个浏览器包从 npm 复制到 `js/vendor/`(自托管,无 CDN 依赖);每节点一张卡片,`<canvas>` 手绘 CPU/内存双线波浪图(60 点窗口),2 s 随 `Tick` 追加;暗色主题、CSS Grid 自适应。 |
| 构建 | `web/admin`:`vite build` 直出 `src/SNM.Master/wwwroot/admin/`(`base:'/admin/'`,history 路由,Master `MapFallbackToFile`);`web/public`:`npm run build` 复制 vendor 后由 `scripts/build-web.sh` 拷到 `wwwroot/`。两者产物均不入库。 |

---

## 1. 管理后台:技术基线与目录

- 技术栈(BRIEF 锁定):Vite 8 / Vue 3.5 / Naive UI 2.44 / Pinia 3 / vue-router 5 / UnoCSS 66 / 纯 JS;`echarts` 6 + `vue-echarts` 8;`@microsoft/signalr` 10.0.11 + `@microsoft/signalr-protocol-msgpack` 10.0.11;`dayjs`(模板已有)。
- 包管理:`npm`(仓库无 pnpm)。删除模板的 `pnpm-lock.yaml`、`pnpm-workspace.yaml`,提交 `package-lock.json`。
- 移除依赖:`xlsx`、`vue3-intro-step`(模板 `BeginnerGuide`)。新增依赖:`@microsoft/signalr`、`@microsoft/signalr-protocol-msgpack`。

改造后 `web/admin/src` 目录(仅列变化与新增):

```
src/
  api/index.js                     [改] 基础接口(见 §2.3)
  settings.js                      [改] basePermissions = [];VITE_TITLE 相关文案
  store/modules/auth.js            [改] + refreshToken
  store/modules/live.js            [增] 实时状态
  store/helper.js                  [不改]
  utils/http/interceptors.js       [改] 401 静默刷新
  utils/http/helpers.js            [改] 增加 10001/11007/11008/409/422/429 文案
  utils/format.js                  [增] 字节/速率/百分比/时长/日期格式化、国旗 emoji
  composables/useRealtime.js       [增] SignalR 连接生命周期
  composables/useEChartTheme.js    [增]
  components/snm/                  [增] CountryFlag.vue MiniBar.vue StatusTag.vue IpList.vue TrafficBar.vue LiveSparkline.vue MetricChart.vue ConnState.vue InstallScriptModal.vue
  components/snm/index.js          [增] 导出
  layouts/normal/header/index.vue  [改] 加入 <ConnState/> 与告警铃铛
  layouts/components/UserAvatar.vue[改] 删除"切换角色"项与 RoleSelect
  layouts/components/BeginnerGuide.vue [删] 及其引用
  views/login/index.vue            [改] 去验证码/一键体验
  views/login/api.js               [改]
  views/home/index.vue             [改] 总览大盘
  views/nodes/index.vue            [增] 节点管理(列表 + 表单)
  views/nodes/detail.vue           [增] 节点详情
  views/nodes/api.js               [增]
  views/nodes/components/NodeForm.vue  [增]
  views/alerts/index.vue  alerts/api.js          [增]
  views/channels/index.vue  channels/api.js  channels/components/ChannelForm.vue [增]
  views/settings/index.vue  settings/api.js      [增]
  views/profile/index.vue  profile/api.js        [改] 去 gender/address
  views/pms/  views/demo/  views/base/  views/iframe/   [删]
  assets/images/login_banner.webp, login_bg.webp        [保留可用;logo 替换为项目 logo]
```

---

## 2. 认证与模板契约(前端侧)

### 2.1 环境变量

`.env`:
```ini
VITE_TITLE = 'Server Node Monitor'
VITE_USE_HASH = 'false'
VITE_PUBLIC_PATH = '/admin/'
VITE_AXIOS_BASE_URL = '/api'
```
`.env.development`:
```ini
VITE_PROXY_TARGET = 'http://127.0.0.1:5080'
```
`.env.production`:空(全部走同源)。删除模板中 apifox mock 地址。

### 2.2 `vite.config.js` 改动

```js
base: '/admin/',
server: {
  host: '127.0.0.1', port: 3200,
  proxy: {
    '/api':  { target: VITE_PROXY_TARGET, changeOrigin: true },              // 不再 rewrite
    '/hubs': { target: VITE_PROXY_TARGET, changeOrigin: true, ws: true },
    '/install': { target: VITE_PROXY_TARGET, changeOrigin: true },
  },
},
build: {
  outDir: path.resolve(process.cwd(), '../../src/SNM.Master/wwwroot/admin'),
  emptyOutDir: true,
  chunkSizeWarningLimit: 1024,
  rollupOptions: { output: { manualChunks: { echarts: ['echarts', 'vue-echarts'], signalr: ['@microsoft/signalr', '@microsoft/signalr-protocol-msgpack'], naive: ['naive-ui'] } } },
},
```
删除 `/runapi` 代理与 `VueDevTools()`(可保留仅开发)。`index.html` 的 `<link rel="icon" href="/favicon.png">` 改为相对 `favicon.png`(Vite 会按 base 处理 `/favicon.png` → `/admin/favicon.png`,保持绝对写法亦可)。

### 2.3 `src/api/index.js`

```js
import { request } from '@/utils'
export default {
  getUser: () => request.get('/user/detail'),
  refreshToken: refreshToken => request.post('/auth/refresh/token', { refreshToken }, { needToken: false, needTip: false, _isRefresh: true }),
  logout: refreshToken => request.post('/auth/logout', { refreshToken }, { needTip: false }),
  getRolePermissions: () => request.get('/role/permissions/tree'),
  validateMenuPath: path => request.get(`/permission/menu/validate?path=${encodeURIComponent(path)}`),
}
```
删除 `switchCurrentRole`。`views/login/api.js` 只保留 `login: data => request.post('/auth/login', data, { needToken: false })`。

### 2.4 `store/modules/auth.js`

```js
state: () => ({ accessToken: undefined, refreshToken: undefined, expiresAt: 0 }),
actions: {
  setToken({ accessToken, refreshToken, expiresIn }) {
    this.accessToken = accessToken
    if (refreshToken) this.refreshToken = refreshToken
    this.expiresAt = Date.now() + (expiresIn ?? 7200) * 1000
  },
  resetToken() { this.$reset() },
  toLogin() { /* 模板原样 */ },
  resetLoginState() { /* 模板原样,另外调用 useLiveStore().reset() 与 stopRealtime() */ },
  async logout() { this.resetLoginState(); this.toLogin() },
},
persist: { key: 'snm_auth' },
```
删除 `switchCurrentRole`。登录成功后 `authStore.setToken(data)`(`data` 即 API §2 登录响应)。

### 2.5 `utils/http/interceptors.js` 的 401 处理

```js
let refreshing = null
async function tryRefresh() {
  const auth = useAuthStore()
  if (!auth.refreshToken) throw new Error('no refresh token')
  refreshing ??= api.refreshToken(auth.refreshToken)
    .then(({ data }) => { auth.setToken(data); return data.accessToken })
    .finally(() => { refreshing = null })
  return refreshing
}
async function resReject(error) {
  const { response, config } = error || {}
  const code = response?.data?.code ?? response?.status
  if (code === 401 && config && !config._retried && !config._isRefresh) {
    try {
      const token = await tryRefresh()
      config._retried = true
      config.headers.Authorization = `Bearer ${token}`
      return axiosInstance(config)                    // 重放
    } catch { /* 落到下面的统一处理 → 弹"登录已过期" */ }
  }
  /* 其余与模板一致:resolveResError(code, message, needTip) */
}
```
`resResolve` 中业务码 `401` 同样走该逻辑(服务端 401 时 HTTP 状态与 code 一致,实际只会进 `resReject`)。`helpers.js` 的 `resolveResError` 增加:`10001 → 用户名或密码错误`(直接显示服务端 message)、`409/422/429 → 显示服务端 message`、`11007/11008 → 与模板相同的"是否重新登录"`。

### 2.6 登录页

- 移除验证码输入框与图片、`quickLogin` 按钮、`toggleRole`。
- 字段:用户名(必填,3–32)、密码(必填,≤64)、`记住用户名` 复选框(`lStorage.set('loginInfo', { username })`,**不再存密码**)。
- 提交:`api.login({ username, password })` → `authStore.setToken(data)` → `$message.success('登录成功')` → `router.replace(route.query.redirect || '/')`。
- 文案:标题取 `VITE_TITLE`;副标题「服务器节点监控平台」;按钮「登录」;错误由拦截器弹出。

### 2.7 权限守卫与菜单

`router/guards/permission-guard.js` **不改**:它调用 `getUserInfo()`(`GET /api/user/detail`)与 `getPermissions()`(`GET /api/role/permissions/tree`),把 `component` 字段经 `import.meta.glob('@/views/**/*.vue')` 解析。因此:
- `settings.js` 的 `basePermissions = []`(删除外链组)。
- 服务端菜单树(API §2)中的 `component` 路径必须与本文件树一致:`/src/views/home/index.vue`、`/src/views/nodes/index.vue`、`/src/views/nodes/detail.vue`、`/src/views/alerts/index.vue`、`/src/views/channels/index.vue`、`/src/views/settings/index.vue`、`/src/views/profile/index.vue`。
- 图标 `i-fe:home/server/activity/bell/send/settings/user` 均存在于 `src/assets/icons/feather/`(safelist 自动生成)。
- `keepAlive:true` 的页面(`Nodes`、`Alerts`)在 `<script setup>` 内 `defineOptions({ name: 'Nodes' })`/`'Alerts'`(与菜单 `code` 一致,模板 `KeepAlive :include` 按组件名匹配)。
- `basic-routes.js` 保留 `Login/404/403`,删除硬编码的 `Home`(改由菜单树提供;否则与服务端 `Home` 重名)。

### 2.8 `UserAvatar.vue` / 头部

- 下拉菜单仅「个人资料」「退出登录」;删除 `RoleSelect` 与「切换角色」;`[角色]` 显示 `currentRole.name`(服务端返回「超级管理员」)。
- 退出:`api.logout(authStore.refreshToken)` 后 `authStore.logout()`。
- 头部(`layouts/normal/header/index.vue`)在右侧加入 `<ConnState />`(实时连接状态点:绿「实时」/黄「重连中」/灰「未连接」,tooltip 显示最近 `Tick` 时间)与 `<n-badge :value="liveStore.unreadAlerts">` 铃铛(点击跳 `/alerts` 并清零)。移除 `BeginnerGuide`。

---

## 3. 模板文件改造清单

| 操作 | 文件 | 说明 |
|---|---|---|
| 改 | `package.json` | name `snm-admin`;移除 `xlsx`、`vue3-intro-step`;新增 `@microsoft/signalr@10.0.11`、`@microsoft/signalr-protocol-msgpack@10.0.11`;移除 `simple-git-hooks`/`postinstall`(monorepo 根不是 npm 项目);scripts 保留 `dev/build/preview/lint:fix` |
| 删 | `pnpm-lock.yaml`、`pnpm-workspace.yaml` | 用 npm |
| 改 | `.env`、`.env.development`、`.env.production` | §2.1 |
| 改 | `vite.config.js` | §2.2 |
| 改 | `index.html` | `<html lang="zh-CN">`;标题占位不变 |
| 改 | `src/settings.js` | `basePermissions = []`;`defaultPrimaryColor` 可保留 |
| 改 | `src/api/index.js`、`src/views/login/api.js`、`src/views/profile/api.js` | §2.3;profile 保留 `changePassword/updateProfile` |
| 改 | `src/store/modules/auth.js` | §2.4 |
| 增 | `src/store/modules/live.js` | §4.1 |
| 改 | `src/store/modules/index.js` | 导出 `useLiveStore` |
| 改 | `src/utils/http/interceptors.js`、`src/utils/http/helpers.js` | §2.5 |
| 增 | `src/utils/format.js`;改 `src/utils/index.js` 导出 | §4.2 |
| 改 | `src/router/basic-routes.js` | 删除 `Home` 项 |
| 改 | `src/views/login/index.vue` | §2.6 |
| 改 | `src/layouts/components/UserAvatar.vue`、`src/layouts/normal/header/index.vue`、`src/layouts/components/index.js` | §2.8;删除 `RoleSelect.vue`、`BeginnerGuide.vue` 导出 |
| 删 | `src/layouts/components/RoleSelect.vue`、`src/layouts/components/BeginnerGuide.vue` | |
| 删 | `src/views/pms/**`、`src/views/demo/**`、`src/views/base/**`、`src/views/iframe/**` | 模板演示页;`iframe` 删除后 `permission.js` 的外链分支不会触发(菜单无 http 路径),保留代码无害 |
| 改 | `src/views/home/index.vue` | §4.3 |
| 改 | `src/views/profile/index.vue` | 去 `gender/address`,保留昵称/头像 URL/邮箱与改密表单;改密成功后 `authStore.logout()`(服务端已撤销 refresh) |
| 增 | `src/views/nodes/**`、`src/views/alerts/**`、`src/views/channels/**`、`src/views/settings/**` | §4.4–§4.7 |
| 增 | `src/components/snm/**`、`src/composables/useRealtime.js`、`src/composables/useEChartTheme.js` | §4.1、§4.2 |
| 改 | `src/components/index.js` | `export * from './snm'` |
| 改 | `src/assets/images/logo.png` | 项目 logo(简单 SVG 转 PNG 即可) |
| 改 | `README.md` | 项目说明 |

未列出的模板文件(`layouts/*`、`components/common/*`、`components/me/*`、`composables/useCrud|useForm|useModal|useAliveData`、`directives`、`styles`、`utils/naiveTools|storage|is|common`、`store/modules/app|permission|router|tab|user`、`router/guards/*`、`views/error-page/*`、`build/*`、`uno.config.js`、`eslint.config.js`、`jsconfig.json`)**保持原样**。

---

## 4. 管理后台页面规格

### 4.1 实时层

`src/store/modules/live.js`:

```js
export const useLiveStore = defineStore('live', {
  state: () => ({
    state: 'idle',            // idle | connecting | live | reconnecting | offline
    lastTickMs: 0,
    nodes: {},                // id -> AdminNodeLive(不含 hist)
    hist: {},                 // id -> LivePoint[](≤ 90)
    alerts: [],               // 最近 100 条 AlertPush(新的在前)
    unreadAlerts: 0,
  }),
  getters: {
    list: s => Object.values(s.nodes),
    onlineCount: s => Object.values(s.nodes).filter(n => n.online).length,
    byId: s => id => s.nodes[id],
  },
  actions: {
    replaceAll(snapshot) { /* nodes = {}, hist = {}; for n of snapshot.nodes: nodes[n.id] = omit(n,'hist'); hist[n.id] = n.hist */ },
    applyTick(tick) {
      this.lastTickMs = tick.t
      for (const u of tick.u) {
        const n = this.nodes[u.id]; if (!n) continue
        n.online = u.online; n.connected = u.connected
        if (u.p) {
          Object.assign(n, { cpu: u.p.cpu, load1: u.p.load1, memUsed: u.p.memUsed, swapUsed: u.p.swapUsed, rx: u.p.rx, tx: u.p.tx,
                             uptime: u.p.uptime, cycleRx: u.p.cycleRx, cycleTx: u.p.cycleTx, cycleUsed: u.p.cycleUsed, lastSeen: u.p.t })
          n.disks = n.disks.map((d, i) => ({ ...d, used: u.p.diskUsed[i] ?? d.used }))
          const h = (this.hist[u.id] ??= []); h.push({ t: u.p.t, cpu: u.p.cpu, mem: u.p.mem, rx: u.p.rx, tx: u.p.tx }); if (h.length > 90) h.splice(0, h.length - 90)
        }
      }
    },
    upsert(node) { this.nodes[node.id] = omit(node, 'hist'); this.hist[node.id] = node.hist ?? this.hist[node.id] ?? [] },
    remove(id) { delete this.nodes[id]; delete this.hist[id] },
    pushAlert(a) { this.alerts.unshift(a); if (this.alerts.length > 100) this.alerts.pop(); this.unreadAlerts++ },
    reset() { this.$reset() },
  },
})
```

`src/composables/useRealtime.js`(模块级单例):

```js
import * as signalR from '@microsoft/signalr'
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack'
let conn = null, stopped = false
export function startRealtime() {
  if (conn) return
  const live = useLiveStore(), auth = useAuthStore()
  stopped = false
  conn = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/admin', { accessTokenFactory: () => auth.accessToken })
    .withHubProtocol(new MessagePackHubProtocol())
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ctx => [0, 2000, 5000, 10000][ctx.previousRetryCount] ?? 30000 })
    .configureLogging(signalR.LogLevel.Warning)
    .build()
  conn.on('Snapshot', s => { live.replaceAll(s); live.state = 'live' })
  conn.on('Tick', t => live.applyTick(t))
  conn.on('NodeChanged', n => live.upsert(n))
  conn.on('NodeRemoved', id => live.remove(id))
  conn.on('Alert', a => { live.pushAlert(a); notifyAlert(a) })
  conn.onreconnecting(() => { live.state = 'reconnecting' })
  conn.onreconnected(() => { live.state = 'live' })      // 服务端会重推 Snapshot
  conn.onclose(async () => {
    live.state = 'offline'; if (stopped) return
    try { await api.refreshToken(auth.refreshToken).then(({ data }) => auth.setToken(data)) } catch { auth.logout(); return }
    setTimeout(() => conn?.start().then(() => { live.state = 'live' }).catch(() => {}), 5000)
  })
  live.state = 'connecting'
  conn.start().catch(() => { live.state = 'offline'; setTimeout(startRealtime, 5000) })
}
export async function stopRealtime() { stopped = true; const c = conn; conn = null; await c?.stop() }
export const useRealtime = () => ({ start: startRealtime, stop: stopRealtime })
```
`startRealtime()` 在 `layouts/normal/index.vue` 的 `onMounted` 调用(登录后的所有页面都在该布局下);`stopRealtime()` 在 `auth.resetLoginState()` 调用。`notifyAlert(a)`:`window.$notification[a.level === 'critical' ? 'error' : a.level === 'warning' ? 'warning' : 'info']({ title: (a.status === 'resolved' ? '已恢复:' : '') + a.title, content: a.message, duration: 8000 })`,`suppressed=true` 时标题追加「(冷却中,未外发)」。

### 4.2 共享组件与格式化

`src/utils/format.js`:

| 函数 | 规则 |
|---|---|
| `fmtBytes(n)` | 流量/累计字节,十进制 1000:`B/KB/MB/GB/TB`,保留 1–2 位小数(`821.0 GB`) |
| `fmtBps(n)` | 速率,十进制:`B/s KB/s MB/s GB/s`;`0` 显示 `0 B/s` |
| `fmtMiB(mb)` | 内存/磁盘(输入 MiB),二进制:`< 1024 → "843 MiB"`,否则 `"1.92 GiB"`(2 位小数),`≥ 1 TiB → TiB` |
| `permille(v)` | 千分比 → `"12.3%"`(1 位小数) |
| `pct(used, total)` | `total>0 ? used/total*100 : null` |
| `fmtLoad(v)` | `65535 → "N/A"`,否则 `(v/100).toFixed(2)` |
| `fmtUptime(sec)` | `"12 天 3 小时"` / `"3 小时 5 分"` / `"5 分"` |
| `fmtTime(ms)` | `dayjs(ms).format('YYYY-MM-DD HH:mm:ss')`;`fmtAgo(ms)` → `"5 秒前"`/`"3 分钟前"`/`"2 小时前"`/`"3 天前"` |
| `flagEmoji(cc)` | `cc` 两字母 → `String.fromCodePoint(...[...cc.toUpperCase()].map(c => 0x1F1E6 + c.charCodeAt(0) - 65))`;空 → `🏳️` |
| `ruleText(rule)`、`levelText(level)`、`cycleText(cycle)`、`modeText(mode)` | 文案表见 §7 |

`src/components/snm/`:

| 组件 | Props | 表现 |
|---|---|---|
| `CountryFlag` | `cc`, `size=18` | 国旗 emoji + tooltip 国家码;空显示 `🏳️` 与「未知」 |
| `StatusTag` | `online`, `enabled`, `connected` | `n-tag` 圆点:禁用→灰「已禁用」;在线→绿「在线」;离线→红「离线」;`connected && !online`(刚连上还没心跳)→黄「连接中」 |
| `MiniBar` | `value`(0–100), `text`, `warn=80`, `danger=90` | 宽 90 px 高 8 px 的 `n-progress`(`type=line`,无指示器),按阈值绿/黄/红;右侧 `text` |
| `IpList` | `ips: IpEntry[]`, `max=2` | 公网优先展示前 `max` 个,`+N` 弹 `n-popover` 全量表格(IP / 类型 公网|内网 / 来源 探针|服务端 / 最近出现);每个 IP 可点击复制 |
| `TrafficBar` | `used`, `limitGb`, `mode` | `limitGb=0` → 文本「不限 · 已用 X」;否则进度条 + `X / Y GB(82.1%)`;≥80 黄 ≥95 红 |
| `LiveSparkline` | `points: LivePoint[]`, `height=36`, `field='cpu'|'mem'` | `<canvas>` 折线 + 渐变面积,60 点窗口;用于列表行内与详情页头部;实现与大屏共用算法(§5.4) |
| `MetricChart` | `range`, `series`, `points`, `unit` | vue-echarts 封装(§4.5.2) |
| `ConnState` | — | 读 `live.state` |
| `InstallScriptModal` | `nodeId` | §4.4.4 |

### 4.3 总览大盘 `/`(`views/home/index.vue`,菜单 code `Home`)

数据:`GET /api/dashboard/summary`(进入页面 + 每 60 s 刷新)、`GET /api/dashboard/alerts/recent?limit=10`、`GET /api/dashboard/expiring?days=30`;实时在线数取 `live.onlineCount`(比 REST 更快)。

布局(UnoCSS grid,≥1200 px 四列,≤768 px 单列):

| 卡片 | 主数值 | 副文案 | 点击 |
|---|---|---|---|
| 资产总数 | `nodes.total` | `在线 {live.onlineCount} · 离线 {total-online} · 公开 {public}` | `/nodes` |
| 离线预警 | `nodes.offline` | `当前告警 {alerts.open} · 24h 内 {alerts.last24h}` | `/alerts?status=firing` |
| 即将到期 | `expiry.within7d` | `30 天内 {within30d} · 已过期 {expired}` | `/nodes?sortBy=expiresAt&sortDir=asc` |
| 月度固定支出 | 默认币种的 `cost.monthly`(`$123.45`) | 其他币种逐个(`¥88.00`),`{nodesWithPrice} 台有价格` | — |

第二行两列:「流量用量 Top 5」(`traffic.top`,每行 `CountryFlag + publicName + TrafficBar`,右侧周期结束日)与「最近告警」(`alerts/recent`,每行 `levelTag + ruleText · nodeName · fmtAgo(startedAt)`,`status=resolved` 加绿勾;实时 `live.alerts` 有新事件时置顶插入)。第三行:「即将到期」表(`name / provider / expiresAt / daysLeft(≤7 红)/ price currency / cycleText`)。底部小字:`Master {master.version} · 运行 {fmtUptime} · 数据库 {fmtBytes(dbSizeBytes)} · GeoIP {loaded ? '已加载' : '未加载'}`。

### 4.4 节点管理 `/nodes`(`views/nodes/index.vue`,code `Nodes`,`keepAlive`)

#### 4.4.1 数据源

- 列表:`GET /api/nodes?pageNo&pageSize&keyword&group&status&public&sortBy&sortDir`(分页由 `MeCrud`/`n-data-table` remote 模式驱动;默认 `pageSize=20`)。
- 实时:表格行渲染时用 `live.byId(row.id)` 覆盖 `online/cpu/memUsed/rx/tx/cycleUsed/disks/lastSeen/ips/cc` 字段(`computed` 合并,REST 行仅提供分页与静态列);`NodeChanged/NodeRemoved` 触发 `refresh()`(200 ms 防抖)。
- 分组下拉:`GET /api/nodes/groups`。

#### 4.4.2 表格列

| 列 | 内容 | 宽 | 排序 |
|---|---|---|---|
| 节点 | `CountryFlag(cc)` + `publicName`(粗)/ 下一行小字 `name · hostname`;`adminRemark` 非空显示备注图标 tooltip | 220 | `name` |
| 状态 | `StatusTag`;下方小字 `fmtAgo(lastSeen)` | 100 | — |
| IP | `IpList` | 180 | — |
| CPU | `MiniBar(value=cpu/10, text=permille(cpu))`;tooltip `负载 {fmtLoad(load1)} · {cpuModel} × {cores}` | 150 | `cpu` |
| 内存 | `MiniBar(value=memUsed/memTotal*100, text="843 MiB / 1.92 GiB")` | 170 | `mem` |
| 磁盘 | 最大使用率挂载的 `MiniBar` + `mount`;tooltip 全部挂载 | 150 | — |
| 网速 | `↓ fmtBps(rx)` / `↑ fmtBps(tx)` 两行等宽字体 | 120 | — |
| 流量 | `TrafficBar(cycleUsed, limitGb, mode)`;下方小字 `周期 {cycleStart} ~ {cycleEnd}` | 200 | `trafficPct` |
| 到期 | `expiresAt` + `daysLeft`(≤7 红、≤30 黄、已过期红「已过期」;空「—」);小字 `provider · price currency/cycleText` | 150 | `expiresAt` |
| 操作 | 「详情」「编辑」「安装脚本」下拉「更多」:轮换密钥 / 启用·禁用 / 删除 | 200 | — |

顶部工具栏:关键字搜索(`keyword`,回车触发)、分组 `n-select`、状态 `n-select`(全部/在线/离线/已禁用)、公开 `n-select`(全部/公开/隐藏)、「新增节点」按钮、「排序模式」开关(开启后行首出现拖拽柄,拖拽结束调用 `PUT /api/nodes/order {ids}`;用 HTML5 原生拖拽,不加依赖)。

#### 4.4.3 节点表单(`components/NodeForm.vue`,`n-drawer` 宽 640,`n-tabs`)

新增走 `POST /api/nodes`(全量),编辑走 `PATCH /api/nodes/{id}`(只发变更字段:提交前 `diff(initial, form)`)。校验用 `n-form` rules,与 API §4.1 `NodeUpsert` 完全一致:

| Tab | 字段 | 控件 | 校验 / 文案 |
|---|---|---|---|
| 基本 | `name` | `n-input` | 必填,1–64;「内部名称(仅后台可见)」 |
| | `publicName` | `n-input` | 1–64,默认同步 `name`(用户未改动前);「大屏展示名(公开)」 |
| | `group` | `n-select` 可创建(`tag filterable`) | ≤32 |
| | `adminRemark` | `n-input type=textarea` | ≤1024;「私密运维备注」 |
| | `enabled` / `isPublic` / `alertsEnabled` | `n-switch` ×3 | 「启用」「显示在公开大屏」「启用告警」 |
| | `sortOrder` | `n-input-number` | −10000..10000 |
| | `countryCodeOverride` | `n-select` 可清空(ISO 列表常量 `src/utils/countries.js`,显示 emoji + 中文名 + 代码) | 空 = 自动识别(显示当前 `countryCodeAuto`) |
| | `intervalMs` / `ipReportIntervalSec` | `n-input-number` | 1000–60000 / 60–3600;帮助「修改后立即推送到探针」 |
| 流量 | `traffic.limitGb` | `n-input-number` 后缀 GB | 0–1000000;0 = 不限 |
| | `traffic.resetDay` | `n-input-number` | 1–31;帮助「月底不足时钳制到当月最后一天」 |
| | `traffic.mode` | `n-radio-group` | `sum 上下行合计` / `tx 仅上行` / `rx 仅下行` / `max 上下行取大` |
| | `traffic.timeZone` | `n-select filterable`(选项来自 `GET /api/settings` 的 `timeZones`) | 空 = 全局时区(显示 `effectiveTimeZone`) |
| 账单 | `billing.provider` / `providerUrl` | `n-input` | ≤64 / http(s) URL |
| | `billing.price` + `billing.currency` | `n-input-number`(2 位小数) + `n-select`(`general.currencies`) | ≥0 |
| | `billing.cycle` | `n-select` | `cycleText` 映射 |
| | `billing.expiresAt` | `n-date-picker type=date`(`value-format=yyyy-MM-dd`) | 可空 |
| | `billing.autoRenew` | `n-switch` | 「自动续费」 |
| | `billing.note` | textarea | ≤1024 |
| 告警覆盖 | `alertOverrides.offlineSeconds/cpuHighPct/memHighPct/diskHighPct/trafficHighPct/expiryDays` | `n-input-number` 可清空,placeholder 显示全局值「全局:30」 | 与全局同范围 |

新增成功后弹出 `InstallScriptModal`(引导立即安装)。删除确认文案:「删除节点「{name}」将同时删除其全部历史指标、流量与告警记录,且探针将无法再连接。确定删除?」。

#### 4.4.4 安装脚本弹窗(`InstallScriptModal.vue`)

`GET /api/nodes/{id}/install-script?os=linux|windows`(`n-tabs`:Linux / Windows)。展示:
1. 一行命令 `command`(`n-input readonly` + 「复制」按钮 `navigator.clipboard.writeText`,成功 `$message.success('已复制')`)。
2. 折叠「使用代理」:`commandWithProxy`,可输入代理地址替换示例 `socks5://10.0.0.1:1080` 后再复制。
3. 折叠「卸载」:`uninstallCommand`;折叠「手动运行」:`manualCommand`。
4. `baseUrlSource==='request'` 时顶部 `n-alert type=warning`:「当前地址由本次请求推断为 {baseUrl},建议在系统设置中填写公网访问地址」。
5. 红色提示:「命令包含节点密钥,请勿转发给无关人员」。
6. 「查看脚本全文」展开 `<pre>`(`script`)。

「轮换密钥」确认:「轮换后旧密钥立即失效,该节点探针将断开,需重新执行安装命令。确定?」→ `POST /api/nodes/{id}/rotate-key` → 直接打开安装脚本弹窗。

### 4.5 节点详情 `/nodes/:id`(`views/nodes/detail.vue`,code `NodeDetail`,`show:false`)

#### 4.5.1 区块

1. **头部卡**:`CountryFlag + publicName`(大号)、`name`、`StatusTag`、按钮「编辑」「安装脚本」「返回列表」。四个实时数值块(读 `live.byId`):CPU `permille(cpu)` + `LiveSparkline(field=cpu)`;内存 `fmtMiB(memUsed)/fmtMiB(memTotal)` + sparkline(mem);网速 `↓ rx ↑ tx`;运行 `fmtUptime(uptime)`。
2. **基本信息**(`n-descriptions` 3 列):主机名、操作系统、架构、CPU 型号(含 `2x`)、逻辑核心、内存总量、Swap、Agent 版本、BootId、最近 Hello、最近心跳、服务端观测 IP(`live.lastRemoteIp`)、分组、备注、国家(自动/覆盖)。
3. **IP 列表**(`n-table`):IP / 版本 / 公网·内网 / 来源 / 最近出现。
4. **磁盘**(`n-table`):挂载点 / 文件系统 / 已用 / 总量 / `MiniBar`。
5. **性能图表**:`n-radio-group`(24 小时 / 7 天 / 30 天)→ `GET /api/nodes/{id}/metrics?range=`;四张 `MetricChart`(见 4.5.2);`24h` 每 60 s 自动刷新,其他区间切换时加载。
6. **流量**:`GET /api/nodes/{id}/traffic`:当前周期卡(`TrafficBar`、`已用/限额/剩余`、`周期 start ~ end`、`已过 {daysElapsed}/{daysTotal} 天`、`预计用量 fmtBytes(projectedBytes)`,超限额红字)、「近 30 天每日流量」柱状图(`daily`,rx/tx 堆叠)、「历史周期」表(`cycles`:周期 / 下行 / 上行 / 合计 / 限额 / 占比)。
7. **告警记录**:`GET /api/nodes/{id}/alerts?pageNo&pageSize`,列同 §4.6,不含节点列。

#### 4.5.2 ECharts 规格(`MetricChart.vue`)

- 注册:`use([CanvasRenderer, LineChart, BarChart, GridComponent, TooltipComponent, LegendComponent, DataZoomComponent])`;`provide(THEME_KEY, isDark ? 'dark' : undefined)`;`autoresize`。
- x 轴 `type:'time'`,数据 `[t, value]`;`connectNulls:false`(缺桶留空);`24h` 刻度 `HH:mm`,`7d` `MM-DD HH:mm`,`30d` `MM-DD`;`dataZoom` inside + slider(仅 24h)。
- 四图与数据映射(`points[i]`,DATA §2.5 / API §4.4):

| 图 | 系列 | 值 | y 轴 |
|---|---|---|---|
| CPU 使用率 | 均值 `cpuAvg/10`,峰值 `cpuMax/10`(虚线) | % | 0–100 固定 |
| 内存 | 均值 `memAvg`,峰值 `memMax`(虚线);参考线 `memTotalMb` | MiB(格式化 `fmtMiB`) | 0–`memTotalMb` |
| 网速 | 下行 `rxAvg`,上行 `txAvg`(面积),峰值 tooltip 显示 `rxMax/txMax` | `fmtBps` | 自动 |
| 流量(每桶) | 下行 `rxBytes`,上行 `txBytes`(柱状堆叠) | `fmtBytes` | 自动 |

- 颜色:CPU `#5b8ff9`,内存 `#5ad8a6`,下行 `#5b8ff9`,上行 `#f6bd16`,峰值同色 60% 透明虚线;暗色主题用 ECharts `dark`。
- tooltip 统一 `trigger:'axis'`,时间用 `fmtTime`,值用对应格式化;`n` 样本数与 `onlineSec` 在 tooltip 附加「样本 30 · 在线 60 s」。
- 空数据显示 `n-empty description="暂无数据(探针接入后每分钟产生一个点)"`。

### 4.6 告警记录 `/alerts`(`views/alerts/index.vue`,code `Alerts`,`keepAlive`)

- 数据:`GET /api/alerts?pageNo&pageSize&nodeId&rule&level&status&from&to&suppressed`,默认 `pageSize=20`;`GET /api/alerts/stats?days=7` 渲染顶部小条(`byRule` 计数 chips + 7 天每日柱状小图)。
- 筛选:节点(`GET /api/nodes/all` 下拉,搜索)、规则(`ruleText` 六项)、级别、状态(进行中/已恢复)、时间范围(`n-date-picker type=datetimerange`)、「仅看被抑制」开关。路由 query 支持 `status=firing` 预设(大盘跳转)。
- 列:级别 `n-tag`(info 蓝/warning 橙/critical 红)| 规则 `ruleText` | 节点(`nodeName`,点击进详情)| 标题/正文(正文小字,超长省略 tooltip)| 触发值/阈值(`value` / `threshold`,单位随规则:秒·%·天)| 开始 `fmtTime(startedAt)` | 持续 `durationSec` 格式化 | 状态(进行中 红点 / 已恢复 绿勾 + `resolvedAt`)| 通知(`notifiedAt` 绿「已发送」;`suppressed` 灰「已抑制」;`notifyError` 红「失败」tooltip 错误;均无 → 「—」)| 操作:「确认」(`ackedAt` 为空时;`POST /api/alerts/{id}/ack`)、「手动关闭」(仅 `rule ∈ Expiry,TrafficHigh` 且 firing;`POST /api/alerts/{id}/resolve`,确认文案「将关闭该告警并进入冷却期,确定?」)。
- 实时:`live.alerts` 新事件且当前筛选匹配时,若在第 1 页则插到表首(同 id 已存在则更新状态);进入页面时 `live.unreadAlerts = 0`。

### 4.7 通知渠道 `/channels`(`views/channels/index.vue`,code `Channels`)

- 数据:`GET /api/channels`(不分页),卡片网格(每卡:类型图标 TG/Webhook、名称、启用开关(`PATCH {enabled}` 即时)、`minLevel` 标签、规则范围「全部规则」或列表、最近发送 `fmtAgo(lastSentAt)`、`lastError` 红字、按钮「测试」「编辑」「删除」)。
- 表单(`ChannelForm.vue`,`n-modal` 宽 560):

| 字段 | 控件 | 校验 |
|---|---|---|
| `name` | input | 必填 1–64 |
| `type` | radio `telegram / webhook`(编辑时禁用) | |
| `enabled` | switch | |
| `minLevel` | select `info 提示 / warning 警告 / critical 严重` | |
| `rules` | `n-checkbox-group` 六规则,空 = 全部 | |
| telegram: `config.botToken` | input password,placeholder「留空保持不变」(编辑时显示 `***…AbCd`) | 新建必填,`^\d+:[A-Za-z0-9_-]{30,}$` |
| `config.chatId` | input | 必填 |
| `config.parseMode` | select `HTML/Markdown`(默认 HTML) | |
| `config.disableNotification` | switch「静默推送」 | |
| `config.apiBase` | input,默认 `https://api.telegram.org` | URL |
| webhook: `config.url` | input | 必填 http(s) |
| `config.method` | select POST/PUT | |
| `config.secret` | input password「用于 X-SNM-Signature HMAC 签名,可空」 | |
| `config.headers` | 动态键值对列表(`n-dynamic-input`) | key 非空 |
| `config.timeoutSec` | number 3–60 | |
| `config.insecureSkipTlsVerify` | switch,红字提示 | |

- 「测试」:已保存渠道 → `POST /api/channels/{id}/test`;表单内「发送测试」→ `POST /api/channels/test {type, config}`(未保存配置)。结果 `ok:true` → `$message.success('测试消息已发送({latencyMs} ms)')`;否则 `$message.error(error)`。
- 帮助折叠面板「Webhook 负载格式」内嵌 API §10.2 的 JSON 示例与签名说明。

### 4.8 系统设置 `/settings`(`views/settings/index.vue`,code `Settings`)

`GET /api/settings` 一次拉取,`n-tabs` 分组,每组独立「保存」→ `PUT /api/settings { <group>: {...} }`(只发该组),成功后用响应整体回填。范围校验与 DATA §2.12 一致。

| Tab | 字段(键) | 控件/文案 |
|---|---|---|
| 常规 `general` | `siteName` 站点名称;`publicTitle` 大屏标题;`publicBaseUrl` 公网访问地址(帮助「用于生成安装脚本,如 https://monitor.example.com」);`timeZone` 全局时区(select,来自 `timeZones`);`currencies` 币种列表(tags);`defaultCurrency`;`publicDashboardEnabled` 启用公开大屏;`publicShowOffline` 大屏显示离线节点 | |
| 探针 `agent` | `intervalMs` 默认心跳间隔;`ipReportIntervalSec` 默认 IP 上报间隔;`releaseBaseUrl` 探针下载地址;`agentVersionPin` 固定探针版本(空 = latest) | 帮助「仅影响新建节点的默认值 / 新生成的安装脚本」 |
| 告警 `alert` | `offlineSeconds` 离线判定秒;`cpuHighPct`+`cpuSustainSec`;`memHighPct`+`memSustainSec`;`diskHighPct`;`trafficHighPct`;`expiryDays` 到期提前天数;`cooldownMinutes` 冷却(30–60);`renotifyHours` 重复提醒;`recoverHysteresisPct` 恢复滞回;`recoverSustainSec`;`expiryCheckTime` 到期巡检时间(`n-time-picker format=HH:mm`);`rulesEnabled.*` 六个开关 | 每项右侧显示单位 |
| GeoIP `geoip` | `enabled`;`refreshDays`;`ipv4Url`;`ipv6Url`;只读状态卡(`geoipStatus`:已加载/IPv4 段数/IPv6 段数/最近成功/最近尝试/错误)+ 按钮「立即刷新」(`POST /api/settings/geoip/refresh`,之后每 3 s 轮询 `GET /api/settings` 至 `lastAttempt` 变化) | |
| 数据保留 `retention` | `alertDays` 告警保留天数;`trafficDailyDays` 每日流量保留天数;只读说明「指标:1 分钟 24 小时 / 1 小时 7 天 / 1 天 30 天(固定)」 | |
| 安全 `security` | `loginMaxPerMinute`;`installScriptMaxPerMinute` | 帮助「重启后生效」 |
| 系统信息 | `GET /api/system/info` 只读:版本、运行时、OS、启动时间、运行时长、数据目录、DB 大小、WAL 大小、监听地址、连接数(大屏/后台/探针)、计数器 | 「刷新」按钮 |

### 4.9 个人资料 `/profile`

沿用模板页面结构:头像 URL、昵称、邮箱 → `PATCH /api/user/profile/{id}`;改密表单(原密码、新密码、确认;新密码 8–64 且含字母与数字)→ `POST /api/auth/password`,成功 `$message.success('密码已修改,请重新登录')` 后 `authStore.logout()`。删除性别、地址字段。

---

## 5. 公开大屏(`web/public`)

### 5.1 文件

```
web/public/
  package.json          { "name": "snm-public", "private": true, "scripts": { "build": "node build.mjs" },
                          "dependencies": { "@microsoft/signalr": "10.0.11", "@microsoft/signalr-protocol-msgpack": "10.0.11" } }
  build.mjs             复制 node_modules/@microsoft/signalr/dist/browser/signalr.min.js 与
                        node_modules/@microsoft/signalr-protocol-msgpack/dist/browser/signalr-protocol-msgpack.min.js → js/vendor/
  index.html
  css/app.css
  js/app.js             入口(ES module):连接、状态、渲染
  js/sparkline.js       canvas 波浪图(纯函数,可被 admin 复用同算法)
  js/format.js          fmtBps/permille/flagEmoji(与 admin 同规则的最小子集)
  js/vendor/            (构建产物,git 忽略)
  favicon.svg
```
`index.html` 用 `<script src="js/vendor/signalr.min.js">`、`<script src="js/vendor/signalr-protocol-msgpack.min.js">`(UMD,挂 `window.signalR` 与 `window.signalR.protocols.msgpack`),再 `<script type="module" src="js/app.js">`。**无任何外部 URL**(无 CDN、无字体、无统计脚本)。

### 5.2 布局与视觉

- 深色主题(`--bg:#0b0f17 --card:#121826 --border:#1f2937 --text:#e5e7eb --muted:#8b93a7 --ok:#22c55e --bad:#ef4444 --cpu:#60a5fa --mem:#34d399`),系统字体栈,`prefers-color-scheme: light` 时提供浅色变量。
- 结构:
  ```
  <header>  [标题 = Snapshot.title]   [状态点 + 文案:实时 / 重连中… / 已断开]   [在线 11 / 12]   [HH:mm:ss 时钟]
  <main class="grid">  卡片 × N(CSS Grid `repeat(auto-fill, minmax(300px, 1fr))`,gap 12px;≤480px 单列)
  <footer>  「Server Node Monitor」 小字
  ```
- 卡片(`<article class="node" data-id>`):
  ```
  ┌ 🇸🇬 HK-Node-01                       ● 在线 ┐
  │  CPU 12.3%          内存 42.8%              │
  │  [ canvas 波浪图 高 64px:CPU 线 + 内存线 ]   │
  │  ↓ 1.2 MB/s   ↑ 3.4 MB/s          刚刚      │
  └────────────────────────────────────────────┘
  ```
  离线:整卡 `opacity:.55; filter:grayscale(.6)`,状态点红、文案「离线」,波浪图停止追加但保留历史,速率显示 `—`,右下角显示「离线 3 分钟」(基于最后一点 `t`)。
- 排序:`order asc, id asc`;`NodeChanged` 后重排 DOM(`append` 顺序)。
- 动效:新点进入时 CPU/内存数值有 300 ms 颜色过渡;不做整卡闪烁。
- 可访问性:卡片 `role="group" aria-label="{name} {在线|离线}"`;色彩不作为唯一状态指示(有文字)。

### 5.3 数据与状态机(`js/app.js`)

```
state: { title, tickMs, nodes: Map<id, { card, hist: LivePoint[], online, name, cc, order }>, conn: 'connecting'|'live'|'reconnecting'|'offline', lastTick }
Snapshot(s):  title ← s.title; 清空并按 s.nodes 重建卡片(hist = n.hist 最近 60 点);渲染全部
Tick(t):      for u of t.u: node = nodes.get(u.id); if (!node) continue
                node.online = u.online; if (u.p) { hist.push(u.p); hist.length > 60 && hist.shift() }
                标记 dirty;requestAnimationFrame 内统一重绘 dirty 卡片(数值 + canvas)
NodeChanged(c): upsert(新建卡片或更新 name/cc/order/online/hist);重排
NodeRemoved(id): 删除卡片
每 1 s:       更新时钟;更新每卡「x 秒前 / 离线 x 分钟」;若 now - lastTick > 3 × tickMs 且 conn=='live' → 显示「等待数据…」
```
连接建立(PROTOCOL §4.5):
```js
const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/public')
  .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
  .withAutomaticReconnect({ nextRetryDelayInMilliseconds: c => [0, 2000, 5000, 10000][c.previousRetryCount] ?? 30000 })
  .configureLogging(signalR.LogLevel.Warning).build()
conn.onreconnecting(() => setConn('reconnecting')); conn.onreconnected(() => setConn('live'))
conn.onclose(() => { setConn('offline'); setTimeout(start, 5000) })
async function start() { setConn('connecting'); try { await conn.start(); setConn('live') } catch { setConn('offline'); setTimeout(start, 5000) } }
document.addEventListener('visibilitychange', () => { if (!document.hidden && conn.state === 'Disconnected') start() })
```
`Snapshot` 在每次(重)连接后由服务端主动推送,前端以之整体替换本地状态,不需要自行请求。`tickMs` 仅用于"等待数据"判定。

### 5.4 波浪图算法(`js/sparkline.js`,admin `LiveSparkline.vue` 复用)

```
drawSparkline(canvas, points, { fields: ['cpu','mem'], colors, max: 1000, windowMs: 120000 })
  dpr 缩放:canvas.width = clientWidth*dpr, height = clientHeight*dpr
  x 轴:按时间 t 映射(不假定等距):x = (t - (tNow - windowMs)) / windowMs * W,tNow = 最后一点 t;早于窗口的点丢弃
  y 轴:y = H - v / max * (H - 4)   (千分比,max=1000)
  每条线:moveTo/lineTo 折线(lineWidth 1.5,lineJoin round)+ 闭合到底边填充线性渐变(顶部 alpha .35 → 底部 0)
  点数 < 2:画一条基线 + 文字「等待数据」
  离线卡片:同样绘制历史(灰度由 CSS filter 处理)
```
性能:100 卡片每 2 s 重绘 ≤ 100 个 300×64 canvas,单帧 < 8 ms;仅重绘 dirty 卡片;标签页隐藏时跳过绘制(`document.hidden`)。

### 5.5 脱敏与安全清单(实现完成时逐项勾选)

- [ ] `js/*.js` 中对节点对象访问的属性仅限:`id name cc online order hist t cpu mem rx tx tickMs title nodes u p`(`scripts/check-public-fields.sh` 用正则 `\.(\w+)` 与 `\['(\w+)'\]` 抽取并对照白名单)。
- [ ] 页面无 `fetch`/`XMLHttpRequest`/`axios`;Network 面板只有静态资源、`/hubs/public/negotiate`、WebSocket。
- [ ] 不渲染 IP、主机名、OS、CPU 型号、内存/磁盘绝对值、价格、到期、备注、AgentKey、告警文本(服务端本就不发送;前端也不预留字段)。
- [ ] 不写入 `localStorage/cookie`;无第三方脚本;`<meta name="referrer" content="no-referrer">`;`<meta name="robots" content="noindex">`。
- [ ] `general.publicDashboardEnabled=false` 时服务端 `/` 返回 404,前端无需处理;连接被 Abort 时显示「大屏未开放」(`onclose` 且从未收到 `Snapshot` 时的文案)。
- [ ] 标题、节点名以 `textContent` 写入(不用 `innerHTML`),防 XSS。
- [ ] 服务端侧 `PublicHubSanitizationTests` 通过(PROTOCOL §8.2)。

---

## 6. 构建与复制流水线

### 6.1 `scripts/build-web.sh`

```bash
#!/usr/bin/env bash
# Build admin SPA and public dashboard into src/SNM.Master/wwwroot. Usage: build-web.sh [--no-install] [--skip-admin] [--skip-public]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; WWW="$ROOT/src/SNM.Master/wwwroot"
INSTALL=1; ADMIN=1; PUBLIC=1
for a in "$@"; do case "$a" in --no-install) INSTALL=0;; --skip-admin) ADMIN=0;; --skip-public) PUBLIC=0;; esac; done
mkdir -p "$WWW"
if [ "$PUBLIC" = 1 ]; then
  ( cd "$ROOT/web/public" && { [ "$INSTALL" = 0 ] || npm ci --no-audit --no-fund; } && npm run build )
  rm -rf "$WWW/css" "$WWW/js" "$WWW/index.html" "$WWW/favicon.svg"
  cp -r "$ROOT/web/public/index.html" "$ROOT/web/public/favicon.svg" "$ROOT/web/public/css" "$ROOT/web/public/js" "$WWW/"
fi
if [ "$ADMIN" = 1 ]; then
  ( cd "$ROOT/web/admin" && { [ "$INSTALL" = 0 ] || npm ci --no-audit --no-fund; } && npm run build )   # vite outDir = wwwroot/admin (emptyOutDir)
fi
echo "web assets built into $WWW"
```

### 6.2 Master 侧托管约定(M2/M3 实现,前端据此假定)

- `/` → `wwwroot/index.html`(大屏);`/css/*`、`/js/*` 静态;`Cache-Control`:`index.html` `no-cache`,其余 `public, max-age=3600`(大屏资源无 hash)。
- `/admin` → 301 `/admin/`;`/admin/**` 非文件路径 → `wwwroot/admin/index.html`(history 路由);`/admin/assets/*`(Vite hash 文件名)→ `immutable, max-age=31536000`。
- `wwwroot` 缺失(未构建)时 Master 仍能启动,`/` 返回 404 纯文本 `public dashboard not built`;`/api` 与 `/hubs` 不受影响。
- 开发:`npm run dev`(`127.0.0.1:3200/admin/`)经 Vite 代理访问 `127.0.0.1:5080` 的 `/api`、`/hubs`(ws)、`/install`;大屏开发直接访问 Master `/`(改 `web/public` 后重跑 `build-web.sh --skip-admin --no-install`)。

### 6.3 `.gitignore` 相关项

`src/SNM.Master/wwwroot/`、`web/admin/node_modules/`、`web/public/node_modules/`、`web/public/js/vendor/`、`web/admin/dist/`。

---

## 7. 文案表(前后端共用的枚举 → 中文)

| 枚举 | 值 → 文案 |
|---|---|
| 规则 `rule` | `Offline` 节点离线 · `CpuHigh` CPU 持续过高 · `MemHigh` 内存持续过高 · `DiskHigh` 磁盘空间不足 · `TrafficHigh` 流量用量超标 · `Expiry` 即将到期/已过期 |
| 级别 `level` | `info` 提示 · `warning` 警告 · `critical` 严重 |
| 状态 `status` | `firing` 进行中 · `resolved` 已恢复 |
| 账单周期 `cycle` | `onetime` 一次性 · `monthly` 月付 · `quarterly` 季付 · `semiannual` 半年付 · `yearly` 年付 · `biennial` 两年付 · `triennial` 三年付 |
| 流量口径 `mode` | `sum` 上下行合计 · `tx` 仅上行 · `rx` 仅下行 · `max` 上下行取大 |
| 渠道类型 | `telegram` Telegram 机器人 · `webhook` Webhook |
| IP 类型/来源 | `public` 公网 · `private` 内网;`agent` 探针上报 · `server` 服务端观测 |
| 节点状态 | 在线 · 离线 · 连接中 · 已禁用 |
| 连接状态 | 实时 · 连接中 · 重连中 · 未连接 |
| 通用按钮 | 新增 · 编辑 · 删除 · 保存 · 取消 · 确定 · 刷新 · 复制 · 测试 · 详情 · 安装脚本 · 轮换密钥 · 启用 · 禁用 · 确认 · 手动关闭 · 立即刷新 |
| 空状态 | 暂无数据 · 暂无节点,点击「新增节点」开始 · 暂无告警 |

---

## 8. 验收(与 DESIGN §9.2 M6/M7 对应)

- `web/admin`:`npm ci && npm run build` 0 错误;`npx eslint src` 0 error;构建产物在 `src/SNM.Master/wwwroot/admin/` 且 `index.html` 引用路径以 `/admin/` 开头。
- 登录 → 大盘四卡有数 → 节点列表 CPU 条每 2 s 变化 → 新增节点弹出安装脚本可复制 → 详情页三区间图表有数据 → 停 Agent 30 s 后右上角出现「节点离线」通知且告警页第一行为该事件 → 恢复后出现「已恢复」→ 设置页改 `publicTitle` 后大屏标题即时变化 → access token 过期(可把 `Jwt:AccessTokenMinutes` 设 1 测试)后操作不弹登录框、请求自动续期。
- `web/public`:`npm ci && npm run build` 生成 `js/vendor/*.min.js`;`scripts/check-public-fields.sh` 通过;Network 无 `/api`;`Snapshot` 后 ≤ 2 s 渲染全部卡片;断网 → 「重连中…」→ 恢复后卡片自动刷新;375 px 单列。
