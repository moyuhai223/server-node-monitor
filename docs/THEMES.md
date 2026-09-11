# 大屏主题开发指南(THEMES)

公开大屏(访客界面)与 Master 完全解耦:**主题只负责渲染**,数据、连接、重连、历史缓冲全部由 Master 提供的 **主题 SDK**(`/vendor/snm-client.js`)处理。任何人都可以用纯 HTML/CSS/JS 写一个主题,打成 zip 在后台 **系统设置 → 大屏主题** 上传并一键切换。

## 1. 目录与请求路径

```
web/sdk/            SDK 源码(snm-client.js)+ SignalR 浏览器包;构建后位于 Master 的 /vendor/
web/themes/<id>/    内置主题(default、minimal),构建后位于 wwwroot/themes/<id>/
<DataDir>/themes/<id>/   用户上传的主题(默认 /var/lib/snm-master/themes)
```

| 路径 | 内容 |
|---|---|
| `/` | **当前启用**主题的入口(设置 `site.theme`,默认 `default`) |
| `/<file>` | 当前主题目录内的文件(`style.css`、`theme.js`、图片…) |
| `/themes/<id>/` | 任意已安装主题(预览用;后台"预览"按钮打开的就是它) |
| `/vendor/snm-client.js`、`/vendor/signalr.min.js`、`/vendor/signalr-protocol-msgpack.min.js` | 共享 SDK,**始终用绝对路径引用** |

主题内部的资源用**相对路径**(`href="style.css"`)。在 `/` 上 Master 会给入口页注入 `<base href="/themes/<id>/">`,所以相对资源始终从各自主题的 URL 加载(不同主题不会互相污染浏览器缓存,切换主题只需 reload);自己的 `index.html` 里不要写 `<base>`。禁止访问 `..`,只允许常见静态文件扩展名(html/css/js/json/svg/png/jpg/webp/ico/woff/woff2/ttf/mp4/webm/wasm 等)。

## 2. 主题包结构

```
my-theme/
  theme.json      清单(必需)
  index.html      入口(必需,名字可在清单里改)
  theme.js / style.css / ...   任意
  preview.svg|png 后台卡片预览图(可选)
```

`theme.json`:

```json
{
  "id": "my-theme",          // 2–32 位小写字母/数字/短横线,全局唯一;不能与内置主题重名
  "name": "我的主题",
  "version": "1.0.0",
  "author": "you",
  "description": "一句话说明",
  "homepage": "https://...",
  "entry": "index.html",     // 主题目录内的 .html
  "preview": "preview.svg",  // 可选
  "sdk": 1                   // 需要的 SDK 版本;高于 Master 支持的版本会被拒绝
}
```

打包:`scripts/pack-theme.sh web/themes/my-theme` → `snm-theme-my-theme-1.0.0.zip`(清单可以在 zip 根或单层子目录内;非白名单扩展名会被跳过/拒绝;解压后 ≤ 20 MB,可用 `Snm:Themes:MaxUploadBytes` 调整)。

## 3. 最小主题

```html
<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8"><title>节点状态</title><link rel="stylesheet" href="style.css"></head>
<body>
  <h1 id="title"></h1><ul id="list"></ul>
  <script src="/vendor/signalr.min.js"></script>
  <script src="/vendor/signalr-protocol-msgpack.min.js"></script>
  <script type="module">
    import { createClient, fmt } from '/vendor/snm-client.js'
    const client = createClient({ theme: 'my-theme' })
    const render = (state) => {
      document.getElementById('title').textContent = state.site.title
      document.getElementById('list').innerHTML = state.nodes.map(n =>
        `<li>${fmt.flag(n.cc)} ${n.name} — ${n.live.online ? '在线' : '离线'} · CPU ${fmt.percent(n.live.cpu)} · ↓${fmt.bps(n.live.rx)}</li>`).join('')
    }
    client.on('snapshot', render); client.on('nodes', render); client.on('update', render)
    client.start()
  </script>
</body></html>
```

`web/themes/minimal/` 是一份完整的参考实现(约 80 行 JS)。

## 4. SDK API(`/vendor/snm-client.js`,SDK 1)

```js
import { createClient, fmt, drawSparkline, STATUS, SDK_VERSION } from '/vendor/snm-client.js'
// 也可通过全局 window.SNM 访问(非 module 脚本)
const client = createClient({
  theme: 'my-theme',    // 传入自己的 id:管理员切换到其他主题时页面自动 reload
  autoReload: true,
  historyPoints: 60,    // 每节点保留的实时点数
  hubUrl: '/hubs/public',
})
```

### 事件

| 事件 | 参数 | 时机 |
|---|---|---|
| `snapshot` | `(state)` | 连接建立/重连后的全量数据;站点设置变化时也会重发 |
| `update` | `(state, changedIds)` | 每 ~2 秒一次,只含有变化的节点 id |
| `nodes` | `(state)` | 节点增删改(名称、国家、顺序、规格、限额) |
| `site` | `(site, state)` | 站点信息(标题、副标题、显示开关、主题参数)变化 |
| `connection` | `({ status, error }, state)` | `connecting / connected / reconnecting / disconnected` |
| `tick` | `(now, state)` | 每秒(用于"x 秒前"之类的文字);`tick:false` 可关闭 |

`client.start()` / `client.stop()` / `client.refresh()`(主动拉一次快照)/ `client.state`。

### `state`

```ts
state.site   = { title, subtitle, showSpecs, showTraffic, offlineSec, theme, options }   // options = 后台"主题参数"JSON
state.nodes  = Node[]            // 已按 order, id 排序
state.byId   = Map<id, Node>
state.connection, state.lastUpdate, state.serverTs, state.sdkVersion

Node = { id, name, cc, order, cores, memMb, diskMb, tLimit, live, hist }
live = { status, online, offline, unknown,
         cpu, mem, disk (千分比 0–1000), cpuPct, memPct, diskPct (0–100),
         rx, tx (B/s), up (秒), tUsed (字节), trafficPct (0–100 或 null), ts (最后上报 Unix ms) }
hist = { ts[], cpu[], mem[], rx[], tx[] }   // 最近 60 点(在线时每 2 秒一点)
```

**这是访客可见的全部数据**:没有 IP、主机名、备注、财务、密钥。主题不能也不需要调用 `/api/*`。

### 工具

`fmt.bytes / mb / bps / bits / duration / ago / percent / flag / country / clock`;`drawSparkline(canvas, hist, { dark, cpuColor, rxColor, txColor, points })` 画 CPU 面积 + 上下行折线。

### 主题参数

后台 **大屏主题 → 主题参数** 是一段自由 JSON(设置 `site.themeOptions`),原样出现在 `state.site.options`,由主题自行解释并在 `site` 事件里应用。默认主题支持 `{ "accent": "#2f80ed", "columns": 340, "showClock": true, "showFooter": true }`。

## 5. 管理接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/settings/themes` | `{ active, sdk, userDir, items[] }` |
| POST | `/api/settings/themes` | multipart `file`(zip)或 `application/zip` 原始体;同 id 覆盖,返回 201 |
| DELETE | `/api/settings/themes/{id}` | 删除用户主题(内置不可删;正在使用时回退到 default) |
| POST | `/api/settings/themes/reload` | 重新扫描目录(手动复制主题到 `<DataDir>/themes/` 后使用) |
| PATCH | `/api/settings` | `{ "site": { "theme": "<id>", "themeOptions": "{...}" } }` 切换/配置 |

切换主题后 Master 通过 Public Hub 推送新快照,SDK 检测到 `site.theme` 与自身不一致即 reload,访客无需刷新。

## 6. 版本与兼容

- SDK 版本由 `SDK_VERSION` / `/api/settings/themes` 的 `sdk` 字段给出;新增字段和事件不升版本,破坏性变更才升。
- 主题清单的 `sdk` 大于 Master 支持的版本时拒绝安装。
- 独立主题仓库建议结构:根目录即主题内容 + `theme.json`,用 GitHub Release 发布 zip;用户下载后直接上传即可。
