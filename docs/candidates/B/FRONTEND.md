# FRONTEND.md — 管理后台与公开大屏(候选设计 B)

> 两个前端:`web/admin`(vue-naive-admin 2.x 改造,Vite 8 / Vue 3.5 / Naive UI 2.44 / Pinia 3 / vue-router 5 / UnoCSS / 纯 JS)与 `web/public`(无框架静态页)。包管理用 **npm**(无 pnpm)。UI 文案简体中文,代码标识符英文。

## 1. 约定

| 项 | 决定 |
|---|---|
| 路由模式 | history(`VITE_USE_HASH='false'`),`VITE_PUBLIC_PATH='/admin/'`;Master 对 `/admin/*` 回退 `admin/index.html` |
| API 基址 | `VITE_AXIOS_BASE_URL='/api'`(所有环境);开发期 Vite 代理 `/api` 与 `/hubs`(ws)到 `http://127.0.0.1:5080`,**不重写路径** |
| 标题 | `VITE_TITLE='Server Node Monitor'`;页面标题 `${route.meta.title} | Server Node Monitor` |
| 新增依赖 | `@microsoft/signalr@10.0.11`、`@microsoft/signalr-protocol-msgpack@10.0.11`(精确版本) |
| 移除依赖 | 无强制;`xlsx` 保留(MeCrud 导出用);`vue3-intro-step` 保留(BeginnerGuide)或删除均可 |
| 格式化工具 | `src/utils/format.js`:`formatBytes(b, digits=1)`(B/KB/MB/GB/TB,1024 进制)、`formatBps(b)`(B/s→ `1.2 MB/s`)、`formatPermille(p)`(→ `23.5%`)、`formatDuration(sec)`(`3天 4小时`)、`flagEmoji(cc)`(两字母 → 区域指示符;空 → 🏳)、`formatDate/DateTime` 复用模板 dayjs |
| 主题 | 保留模板主题系统;`defaultPrimaryColor` 改 `#2F6FED` |
| 图表 | ECharts 6 + vue-echarts 8,按需 `echarts.use([...])` 于 `src/utils/echarts.js` 统一注册:`LineChart, BarChart, GaugeChart, GridComponent, TooltipComponent, LegendComponent, DataZoomComponent, MarkLineComponent, CanvasRenderer, UniversalTransition` |

## 2. 模板文件改造清单

### 2.1 修改

| 文件 | 改动 |
|---|---|
| `package.json` | `name: snm-admin`;添加两个 signalr 依赖;删除 `postinstall: npx simple-git-hooks`(monorepo 下 .git 在上层,钩子无意义);删除 `simple-git-hooks`/`lint-staged` 配置块 |
| `.env` | `VITE_TITLE='Server Node Monitor'`;`VITE_USE_HASH='false'`;`VITE_PUBLIC_PATH='/admin/'`;`VITE_AXIOS_BASE_URL='/api'` |
| `.env.development` | `VITE_PROXY_TARGET='http://127.0.0.1:5080'`;其余同 `.env` |
| `.env.production` | 同 `.env`(删除 apifox mock 地址) |
| `vite.config.js` | `server.proxy`:`'/api': { target, changeOrigin: true }`(**删除 rewrite**),新增 `'/hubs': { target, changeOrigin: true, ws: true }`,删除 `/runapi`;`build.outDir='../../src/SNM.Master/wwwroot/admin'`,`build.emptyOutDir=true`;保留其余插件 |
| `src/settings.js` | `basePermissions = []`(删除外链菜单);`defaultPrimaryColor='#2F6FED'`;`layoutSettingVisible=false` |
| `src/api/index.js` | 保留 `getUser/logout/getRolePermissions/validateMenuPath`;`refreshToken: data => request.post('/auth/refresh', data, { needToken: false })`;删除 `switchCurrentRole` |
| `src/store/modules/auth.js` | state 增加 `refreshToken`;`setToken({accessToken, refreshToken})` 两者都存;`persist.key='snm_auth'` |
| `src/utils/http/interceptors.js` | `resReject`:当 `status===401 && code!==11008 && authStore.refreshToken && !config._retried` → 调 `api.refreshToken({refreshToken})` → `setToken` → `config._retried=true` 重放请求;并发请求共享同一个刷新 Promise;失败则走原 `resolveResError` |
| `src/utils/http/helpers.js` | 401/11007/11008 文案保持;新增 `case 429: message='请求过于频繁'` |
| `src/router/basic-routes.js` | `Home` 组件改 `@/views/dashboard/index.vue`,`meta.title='总览'` |
| `src/views/login/index.vue` | 删除验证码输入框、`initCaptcha`、“一键体验”按钮与 `quickLogin`;标题用 `VITE_TITLE`;登录成功 `authStore.setToken(data)`(含 refreshToken);“记住我”只记用户名(**不再把密码存 localStorage**) |
| `src/views/profile/index.vue` | 仅保留“修改密码”卡片(`oldPassword/newPassword/confirm`,8..64,两次一致校验);删除头像/资料修改 |
| `src/views/profile/api.js` | 仅 `changePassword: data => request.post('/auth/password', data)` |
| `src/layouts/components/UserAvatar.vue` | 删除“切换角色”项与 `RoleSelect` 引用;下拉:个人资料、退出登录 |
| `src/layouts/normal/header/index.vue`(及 `full`/`simple` 的 header 若引用) | 删除 GitHub/Gitee 图标按钮;新增 `RealtimeIndicator`(AdminHub 连接状态圆点 + “实时”字样) |
| `src/layouts/components/BeginnerGuide.vue` | 删除(与之相关的 header 引用一并移除) |
| `src/components/common/TheFooter.vue` | 文案 `Server Node Monitor © 2026` |
| `src/assets/images/logo.png`、`public/favicon.png` | 替换为项目图标(简单 SVG 转 PNG 即可);新增 `public/avatar.svg`(默认头像) |
| `index.html` | `lang="zh-CN"` |
| `README.md` | 替换为本项目说明 |

### 2.2 删除

`src/views/pms/**`、`src/views/demo/**`、`src/views/base/**`、`src/views/iframe/**`、`src/views/home/**`、`src/layouts/components/RoleSelect.vue`、`src/layouts/components/BeginnerGuide.vue`、`src/assets/icons/isme/{apifox,gitee,docs,naiveui,awesome,dialog}.svg`(保留目录与至少一个图标以免 `FileSystemIconLoader` 报错——保留 `dialog.svg`)、`pnpm-lock.yaml`、`pnpm-workspace.yaml`、`.vscode/`(可选)。`build/plugin-isme/page-pathes.js` 与 `icons.js` 保留(无害)。

### 2.3 新增

| 文件 | 内容 |
|---|---|
| `src/utils/format.js` | §1 格式化函数 |
| `src/utils/echarts.js` | ECharts 按需注册 + 通用 option 工厂 `lineOption({series, unit, yFormatter})` |
| `src/utils/signalr.js` | `createHub(path)`:`new HubConnectionBuilder().withUrl(path, { accessTokenFactory: () => useAuthStore().accessToken }).withHubProtocol(new MessagePackHubProtocol()).withAutomaticReconnect([0,2000,5000,10000,30000]).configureLogging(LogLevel.Warning).build()`;`onclose` 30s 后重试;401 时先调 `api.refreshToken` 再重连 |
| `src/store/modules/realtime.js` | Pinia store:`connected`,`nodes: Map<id, AdminNodeDto>`,`ticks`,`alerts[]`(最近 50);actions `connect()/disconnect()`,处理 `snapshot/tick/status/alert`;`getters.list`(数组按 sortOrder);全局单例,`App.vue` 登录后 `connect()`,登出 `disconnect()` |
| `src/components/common/RealtimeIndicator.vue` | 连接状态点(绿 已连接 / 黄 重连中 / 红 断开) |
| `src/components/snm/{FlagCell.vue, UsageBar.vue, BytesText.vue, NodeStatusTag.vue, Sparkline.vue}` | 复用单元:国旗、微型进度条(带百分比与阈值变色 ≥90 红 ≥70 橙)、字节文本、在线/离线标签、迷你折线(canvas,复用大屏算法) |
| `src/views/dashboard/{index.vue, api.js}` | §4.1 |
| `src/views/monitor/{index.vue, detail.vue, api.js, components/MetricsCharts.vue, components/NodeInfoCard.vue}` | §4.2/§4.3 |
| `src/views/nodes/{index.vue, api.js, components/NodeForm.vue, components/InstallScriptModal.vue}` | §4.4 |
| `src/views/traffic/{index.vue, api.js}` | §4.5 |
| `src/views/alerts/{index.vue, channels.vue, api.js, components/ChannelForm.vue}` | §4.6/§4.7 |
| `src/views/settings/{index.vue, api.js}` | §4.8 |
| `src/views/profile/{index.vue, api.js}`(重写) | §4.9 |

## 3. 认证与实时基础设施

1. **登录流**:`POST /api/auth/login` → `setToken({accessToken, refreshToken})` → 守卫 `getUserInfo()`(`/user/detail`)+ `getPermissions()`(`/role/permissions/tree`)→ 动态注册路由 → 跳转 `redirect` 或 `/`。
2. **刷新**:拦截器单飞刷新(§2.1);刷新失败或 `11008` → `authStore.logout()` + 弹“登录已过期”。
3. **AdminHub**:登录成功且进入布局后 `realtimeStore.connect()`;`snapshot` 覆盖 `nodes`;`tick` 合并到 `nodes[id].last` 并追加 `wave`(裁到 `wavePoints`);`status` 更新 `online/lastSeen` 并 `$message.warning(`${name} 离线`)`/`success(上线)`;`alert` 追加并 `$notification`(firing 红 / resolved 绿,持续 8s)。路由离开不断开(全局连接)。
4. **REST + Hub 合并原则**:页面首屏用 REST(分页、历史),运行态列(CPU/内存/网速/在线)一律取 `realtimeStore.nodes[id]`,避免轮询。

## 4. 管理后台页面规格

通用:所有列表页用 `CommonPage` + `MeCrud`(`remote=true`,契约 `pageNo/pageSize → {pageData,total}`);弹窗用 `MeModal` + `useCrud/useForm`;删除二次确认;操作成功 `$message.success('操作成功')`。表格运行态列每 2s 随 store 变化自动更新(`h()` 渲染函数读取 `realtimeStore.nodes[row.id]`)。

### 4.1 总览 `/`(`views/dashboard/index.vue`)

- 数据:`GET /api/dashboard/summary`(进入页面 + 每 30s)、`GET /api/dashboard/expiring?days=30`、`GET /api/dashboard/alerts/recent?limit=10`;在线/离线数直接来自 `realtimeStore`。
- 布局:4 张统计卡(`n-grid cols=4`,响应式 `s:2 m:4`):**资产总数**(total,副文案 `在线 x / 离线 y`)、**离线预警**(offline,红色;点击跳 `/monitor?online=false`)、**即将到期**(within7d,副文案 `30 天内 n 台`;点击跳 `/nodes?expiring=30`)、**月度支出 MRR**(`¥1,268.40`,副文案按币种 `USD 120.50 · CNY 400.80`)。
- 中部:左 60% “节点实时概览” 表(publicName、国旗、CPU/内存 UsageBar、网速 ↑↓、状态),右 40% “最近告警” 列表(severity 色点、title、相对时间,点击跳 `/alerts`)。
- 底部:“即将到期”表(publicName、供应商、到期日、剩余天数 tag(≤7 红 ≤30 橙)、续费价)。

### 4.2 探针视图 `/monitor`(`views/monitor/index.vue`,keepAlive)

- 顶部筛选:关键字(publicName/备注/IP)、分组、在线状态、显示模式(表格/卡片)。数据源:`realtimeStore.list`(纯前端筛选,无 REST 分页)。
- 表格列(`n-data-table`,`scroll-x=1600`):

| 列 | 宽 | 内容 |
|---|---|---|
| 状态 | 70 | `NodeStatusTag`(在线绿/离线红/未注册灰,离线时 tooltip “最后在线 x 分钟前”) |
| 节点 | 200 | 国旗 Emoji + publicName;第二行灰色 adminRemark(ellipsis) |
| 系统 | 160 | os 简写(去掉 `LTS` 等)+ arch;tooltip 显示 kernel |
| CPU | 160 | `UsageBar` cpu‰ + `cores核` + tooltip cpuModel |
| 内存 | 160 | `UsageBar` memUsed/memTotal + `1.2 GB / 2 GB` |
| 磁盘 | 160 | `UsageBar` diskUsed/diskTotal |
| 网速 | 150 | `↑ 65.4 KB/s` / `↓ 123.4 KB/s`(两行) |
| 流量 | 140 | `UsageBar` periodCounted/limit + `300 GB / 1 TB`;无限额显示 `已用 300 GB` |
| IP | 220 | 合并列表:第一行公网 IP(粗体)+ 国家码,其余灰色,超过 3 个折叠 `+n`,tooltip 全量,`n-button text` 复制 |
| 负载 | 80 | `load1/100` 两位小数,Windows `-` |
| 运行时长 | 100 | `formatDuration(uptime)` |
| 操作 | 120 | 详情(跳 `/monitor/detail?id=`)、编辑(跳 `/nodes?edit=id`) |

- 卡片模式:每节点一张卡(国旗+名称+状态,CPU/内存/磁盘三根 UsageBar,`Sparkline` 90 点 CPU),用于大量节点浏览。

### 4.3 节点详情 `/monitor/detail?id=`(`views/monitor/detail.vue`,隐藏菜单)

- 首屏:`GET /api/nodes/{id}`(配置+清单+运行态+磁盘+账期)、`GET /api/nodes/{id}/wave`;之后运行态跟随 store。
- 头部 `n-page-header`:返回、国旗+publicName、状态标签、`adminRemark`;右侧按钮:编辑(跳 `/nodes?edit=`)、安装脚本。
- 信息卡(`n-descriptions` 3 列):主机名、系统、内核、架构、CPU 型号(含 `2x`)、核心、内存/交换总量、探针版本、注册时间、公网 IP、国家(自动/覆盖)、全部 IP(可复制)、计入网卡。
- 实时卡:4 个 `n-progress` 圆环(CPU、内存、磁盘、流量)+ 网速 + 负载 + 运行时长;波浪图(`Sparkline` 双线 CPU/内存 + 网速柱)。
- 历史图表(`components/MetricsCharts.vue`):`n-radio-group` 切换 `24h | 7d | 30d` → `GET /api/nodes/{id}/metrics?range=`;四张 ECharts 折线:CPU(cpuAvg/cpuMax,%)、内存(memUsedAvg,自动单位)、网络(rxAvgBps/txAvgBps 双线 + rxMaxBps 虚线)、磁盘(diskUsedAvg)与负载(可选 tab);`dataZoom` 滑块;`connectNulls:false` 使缺桶断开;tooltip 时间按浏览器时区 `MM-DD HH:mm`;每 60s 自动刷新当前范围(`24h` 时)。
- 磁盘表:mount、fsType、总量、已用、`UsageBar`。
- 流量卡:当前账期区间、已用/限额/百分比、预计用量(projected)、剩余天数;“最近 31 天” 柱图(`GET /api/nodes/{id}/traffic`)与历史账期表。
- 告警:该节点最近 20 条(`GET /api/alerts?nodeId=`)。

### 4.4 节点配置 `/nodes`(`views/nodes/index.vue`,keepAlive)

- `MeCrud` + `GET /api/nodes`;查询项:关键字、分组、启用状态、`expiring`(≤7/≤30 天)。
- 列:状态、publicName/adminRemark、分组、供应商、到期日(剩余天数 tag)、续费价(`9.90 USD/月`)、流量限额/重置日(`1 TB · 每月 1 日`)、启用开关(`n-switch` → `PATCH enabled`)、操作(编辑、安装脚本、轮换密钥、删除)。
- 新增/编辑弹窗 `NodeForm.vue`(`MeModal width=720px`,`n-form label-width=110 label-placement=left`,`n-tabs`):
  - **基本**:显示名称 publicName(必填,1–64)、私密备注 adminRemark(textarea ≤512)、分组(`n-select tag filterable` 取 `/api/nodes/groups`)、排序 sortOrder(数字)、启用(switch)、国家覆盖(`n-select` 常用国家 + 手输两字母,可清空)。
  - **流量**:限额(`n-input-number` + 单位选择 GB/TB → 换算字节;0 或空=不限)、重置日(1–31,提示“超过当月天数时取月末”)、计费方式(`sum/rx/tx/max` 单选)、时区(`n-select filterable` 取 settings.timeZones,空=跟随全局)。修改重置日/时区时 `n-alert warning`:“保存后将从现在开始新的计费周期”。
  - **财务**:供应商、续费价格(decimal ≥0)、币种(USD/CNY/EUR 可输入)、计费周期(月/季/半年/年/2 年/3 年 → 1/3/6/12/24/36)、购买日期、到期日期(`n-date-picker`)、自动续费。
  - **告警覆盖**:每条规则一行:启用(默认跟随全局)、阈值输入(留空=全局);对应 `alertOverrides`。
  - 校验规则在 `n-form-item :rule`;保存 → `POST/PATCH`;成功后刷新表格与 `realtimeStore`(收到 `snapshot` 重推)。
- 安装脚本弹窗 `InstallScriptModal.vue`:`GET /api/nodes/{id}/install-command`;Tabs `Linux | Windows | 手动`;代码块 `n-code`/`pre` + 复制按钮(`navigator.clipboard`),可选代理输入框(变更时带 `?proxy=` 重新请求);显示 AgentKey(默认打码,点击眼睛显示);“卸载命令”折叠。
- 轮换密钥:确认对话框“轮换后当前探针将断开,需要用新密钥重新安装或修改 /etc/snm-agent/agent.env” → `POST rotate-key` → 弹出新密钥与安装命令。
- 路由参数 `?edit=id` 打开编辑弹窗;`?expiring=30` 预填筛选。

### 4.5 流量统计 `/traffic`(`views/traffic/index.vue`)

`GET /api/traffic/overview`(`MeCrud`,默认 `sort=pct,desc`);列:节点、限额、已用(UsageBar + 字节)、↑/↓ 分别、周期结束、剩余天数、重置日;行点击展开:`GET /api/nodes/{id}/traffic` 的 31 天柱图与历史周期表;顶部汇总卡:有限额节点数、≥80% 数、总已用。

### 4.6 告警记录 `/alerts`(`views/alerts/index.vue`)

- 顶部:Firing 事件条(`GET /api/alerts/active`,红色卡片列表,实时随 store `alerts` 更新)。
- `MeCrud` + `GET /api/alerts`;查询项:节点(`/api/nodes/all`)、规则(`/api/alerts/rules`)、状态、级别、时间范围(`n-date-picker daterange`)。
- 列:级别(color tag:critical 红/warning 橙/info 蓝)、状态(触发中/已恢复)、节点、规则、标题、值/阈值、触发时间、恢复时间、持续时长、已通知(✓/冷却中)、次数。
- 操作:批量清理按钮 → `DELETE /api/alerts?before=&status=resolved`。

### 4.7 通知渠道 `/alerts/channels`(`views/alerts/channels.vue`)

- 卡片列表(`GET /api/alert-channels`):名称、类型图标、启用开关、最低级别、最近发送结果(时间+成功/失败)、操作:测试、编辑、日志、删除。
- 表单 `ChannelForm.vue`:名称(必填)、类型(Telegram/Webhook 单选,新建时可改)、启用、最低级别(info/warning/critical);Telegram:Bot Token(密码框,必填,正则校验)、Chat ID(必填)、Thread ID;Webhook:URL(必填 http/https)、方法(POST/PUT)、自定义请求头(键值对编辑器)、签名密钥(密码框)、Content-Type、自定义 Body 模板(textarea + 占位符提示 + “使用默认 JSON”)。底部“发送测试”按钮(未保存时调 `POST /api/alert-channels/test`,已保存调 `/{id}/test`),结果以 `n-alert` 显示 httpStatus/耗时/错误。
- 日志抽屉:`GET /api/alert-channels/{id}/logs`。

### 4.8 系统设置 `/settings`(`views/settings/index.vue`)

`GET /api/settings` → `n-tabs`:**常规**(后台标题、大屏标题、全局时区、Master 公网地址)、**探针**(心跳/IP/磁盘上报间隔,提示范围;保存后“已下发到在线探针”)、**告警阈值**(离线秒数、CPU% 与持续秒数、内存开关+%、磁盘开关+%、流量%、到期天数与检查时刻、冷却分钟(30–60 滑块)、事件保留天数)、**流量**(默认重置日、异常速率上限 Gbps)、**公开大屏**(启用、显示流量/运行时长/磁盘;预览链接 `/`)、**安装**(Release 基地址、探针版本)、**财务**(基准币种、汇率表键值编辑)、**系统信息**(`GET /api/system/info` 只读:版本、运行时长、DB 大小、GeoIP 状态 + “立即刷新” 按钮、备份列表 + “立即备份”、任务最近运行时间)。每个 tab 独立“保存”按钮 → `PUT /api/settings` 仅提交该组。

### 4.9 个人资料 `/profile`

用户名(只读)、修改密码表单(原密码、新密码、确认;8–64;一致性校验)→ `POST /api/auth/password` → 成功提示“密码已修改,请重新登录” → `authStore.logout()`。

### 4.10 登录 `/login`

用户名、密码、记住用户名、登录按钮;错误 `10001` 显示“用户名或密码错误”;`429` 显示“尝试过于频繁,请稍后再试”。

## 5. 实时更新规则(后台)

| 事件 | store 处理 | UI 反应 |
|---|---|---|
| `snapshot` | 覆盖 `nodes`(保留本地 UI 状态如展开行) | 所有运行态列刷新 |
| `tick` | `nodes[id].last = tick`;`wave.push(...)` 裁剪到 `wavePoints` | 表格/圆环/Sparkline 每 2s 更新;ECharts 历史图不变 |
| `status` | `online/lastSeen` | 状态标签变色;`$message` 提示(同一节点 60s 内不重复) |
| `alert` | `alerts.unshift`(≤50) | `$notification`;告警页 Firing 条刷新;总览“最近告警”刷新 |
| 连接断开 | `connected=false` | 顶栏指示器黄/红;运行态列显示灰色“数据可能已过期” |

页面中禁止用 `setInterval` 轮询运行态;允许 30–60s 轮询的仅:总览 summary、详情页 24h 图表、设置页系统信息。

## 6. 公开大屏(`web/public`)

### 6.1 文件

```text
web/public/
  index.html
  css/app.css
  js/app.js                 # 业务逻辑(ES2020,无构建)
  js/vendor/signalr.min.js  js/vendor/signalr-protocol-msgpack.min.js   # build-web.sh 从 node_modules 复制,gitignored
  check-public.mjs          # 敏感字段检查脚本(§7)
```

`index.html` 只引用同源资源(CSP `default-src 'self'`);不请求任何 `/api/*`;不含任何第三方 CDN。

### 6.2 布局

- 顶栏:标题(`snapshot.title`)、右侧统计 `在线 x / 总数 y`、连接状态点、深浅色切换(跟随 `prefers-color-scheme`,可手动切,存 localStorage)。
- 主体:响应式卡片网格(`grid-template-columns: repeat(auto-fill, minmax(320px, 1fr))`,间距 16px);按 `group` 分节(有分组时显示分组标题),节内按 snapshot 顺序。
- 底栏:`Powered by Server Node Monitor` + 最后更新时间。
- 移动端(<600px)单列;卡片高度固定 ~210px。

### 6.3 节点卡片内容(白名单)

| 区域 | 内容 | 数据 |
|---|---|---|
| 头 | 国旗 Emoji(`cc` → 区域指示符,空显示 🏳)+ `name` + 状态点(在线绿/离线红,离线时卡片整体降饱和度并显示“离线 · 最后在线 x 分钟前”) | `PublicNodeDto` |
| 指标行 | CPU `23.5%`、内存 `45%`、磁盘 `62%`(`public.showDisk=false` 时隐藏)三根细进度条,≥90 红 ≥70 橙 | `last` / `tick` |
| 网络行 | `↑ 65.4 KB/s  ↓ 123.4 KB/s` | `tick.tx/rx` |
| 波浪图 | canvas 高 56px,宽随卡片:CPU 面积图(主色,渐变填充)+ 内存折线(次色);最多 `wavePoints` 点,新点从右侧推入,`requestAnimationFrame` 平滑过渡 300ms;无数据显示占位虚线 | `wave` + `tick` |
| 底行 | `cores 核 · memMb 格式化 · 运行 3 天 4 小时`(`showUptime=false` 隐藏运行时长)、流量 `已用 23%`(`traffic` 非 null 时) | 静态字段 + tick |

### 6.4 连接生命周期(`js/app.js`)

```text
start():
  conn = new signalR.HubConnectionBuilder().withUrl('/hubs/public').withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
         .withAutomaticReconnect([0, 2000, 5000, 10000, 30000]).configureLogging(signalR.LogLevel.Warning).build()
  conn.on('snapshot', s => { state.nodes = new Map(s.nodes.map(n => [n.id, n])); state.title = s.title; renderAll(); })
  conn.on('tick', t => { for (const n of t.nodes) applyTick(n); state.lastUpdate = t.t; })   // 只更新对应 DOM 节点的文本/宽度/波浪
  conn.on('status', e => setOnline(e.id, e.online, e.t))
  conn.onreconnecting(() => indicator('reconnecting')); conn.onreconnected(() => indicator('online'))   // 新连接会收到 snapshot
  conn.onclose(err => { indicator('offline'); if (String(err).includes('disabled')) showBanner('看板已关闭'); else setTimeout(start, 30000); })
  try { await conn.start(); indicator('online'); } catch { indicator('offline'); setTimeout(start, 5000); }
document.visibilitychange: 隐藏 >5 分钟后恢复时不做特殊处理(SignalR 自己保持/重连;tick 一直到达)
```

- 页面**不**在 `tick` 缺席时自行推算;若 `now - lastUpdate > 15s` 顶栏显示“数据更新延迟”。
- 节点离线后波浪图停止推进(保留最后形状),状态点变红。
- 性能:100 节点 × 2s tick,DOM 更新只改变化的元素;canvas 重绘仅有新点的卡片。

## 7. 无敏感数据检查清单

| 项 | 要求 | 检查方式 |
|---|---|---|
| Hub DTO | `PublicNodeDto/PublicNodeTickDto/PublicSnapshotDto/PublicTickDto/NodeStatusEventDto/WavePointDto` 不含:IP、主机名、系统/内核、CPU 型号、备注、供应商、价格、到期日、AgentKey、公网 IP、国家覆盖来源、告警内容 | `tests/SNM.Master.Tests/PublicDtoWhitelistTest`:反射枚举字段名 ∈ 白名单集合 |
| REST | 大屏不调用任何 `/api/*` | `check-public.mjs` 断言 `js/app.js`、`index.html` 中不出现 `/api/` |
| 字段名 | `web/public/js/app.js` 不出现 `ip`、`hostname`、`remark`、`price`、`expires`、`agentKey`、`publicIp`、`adminRemark`、`provider`(正则 `\b(ip|ips|hostname|adminRemark|remark|price|expires|agentKey|publicIp|provider|cpuModel)\b`) | `check-public.mjs`(`build-web.sh` 与 CI 执行) |
| 服务端 | `PublicHub` 由 `SnapshotBuilder.BuildPublic()` 生成,禁止直接把 `AdminNodeDto` 投影 | 代码评审 + 单测 |
| 名称 | 大屏只用 `PublicName`;若为空回退 `节点-<id>`,绝不回退 hostname | 单测 |
| 头信息 | 响应头无 `Server` 版本细节(Kestrel 默认不带);`X-SNM-Version` 仅 `/healthz` | |
| 错误 | Hub 异常信息不含内部路径(`EnableDetailedErrors=false`) | |

## 8. 构建与复制流水线

```text
web/admin  ── npm ci && npm run build(vite base '/admin/', outDir ../../src/SNM.Master/wwwroot/admin, emptyOutDir) ──► src/SNM.Master/wwwroot/admin/**   (gitignored)
web/admin/node_modules/@microsoft/... ── build-web.sh 复制 ──► web/public/js/vendor/*.min.js   (gitignored)
web/public/** ── SNM.Master.csproj <Content Include="../../web/public/**" Link="wwwroot/%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" Exclude="../../web/public/check-public.mjs" /> ──► bin/.../wwwroot/**(运行时由 UseStaticFiles 提供;publish 同样带出)
```

- Master 静态托管:`app.UseDefaultFiles(); app.UseStaticFiles();`(`/` → `index.html`);`app.MapFallbackToFile("/admin/{*path}", "admin/index.html")`;`admin/index.html` 与 `index.html` 响应 `Cache-Control: no-cache`,`admin/assets/*`(带 hash)`max-age=31536000, immutable`。
- 开发:`npm run dev`(端口 3200)访问 `http://localhost:3200/admin/`,Vite 代理 `/api` 与 `/hubs`(ws)到 `127.0.0.1:5080`;大屏开发直接访问 Master `http://127.0.0.1:5080/`(修改 web/public 后 `dotnet run` 会因 `PreserveNewest` 复制新文件;或用 `dotnet watch`)。
- CI/Docker:先 `build-web.sh`,再 `dotnet publish`(DEPLOY.md §2.4/§6.2)。
- `.gitignore` 建议追加:`web/public/js/vendor/`、`src/SNM.Master/wwwroot/admin/`(已存在)。
- `npm run build` 是验收项(BRIEF §0 第 3 条);ESLint 非门禁。

