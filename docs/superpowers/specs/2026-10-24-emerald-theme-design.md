# Emerald 可选主题适配设计

- 日期：2026-10-24
- 目标仓库：Server Node Monitor
- 目标：保留 `binaryu/monitor-theme-emerald` 的 Emerald 原界面风格，并新增一个可由 SNM 后台选择的 `emerald` 主题；不覆盖现有 `default`、`minimal`、`panorama`。

## 已确认的约束

1. 主题只能通过公开大屏 SDK 获取数据：`/vendor/snm-client.js` 与其 SignalR 依赖；不调用管理 API，不修改 Agent、SignalR 协议或后台。
2. 适配源码位于 `web/emerald-snm`，构建产物及主题清单位于 `web/themes/emerald/theme.json`，入口为 `index.html`，SDK 版本为 1。
3. 公开数据没有费用、GPU、私有 IP、备注或真实机房坐标。对应的 Emerald 私有信息卡隐藏；国家位置只使用 `node.cc` 的国家级近似坐标。
4. SNM 主题在 `/` 与 `/themes/emerald/` 两种路径运行，因此资源必须使用主题相对路径，SDK 依赖使用 `/vendor/` 绝对路径。

## 用户可见设计

### 首页

- Emerald 的浅色/深色背景、绿色主色、圆角半透明面板、顶部站点品牌与状态连接提示。
- 顶部展示模式保留 Emerald 的语义：自转地球、静止地球、点状地图、仅卡片、隐藏；沿用上游 Cobe 3D 地球与 ECharts 世界地图，不用简化示意图代替原界面。
- 地球和地图节点使用国家代码聚合，点位由 `cc` 映射到国家级坐标；不假造 IP 位置。
- 地球下方保留节点分组、搜索、卡片/列表切换、公告与空态。
- 节点卡片保留 Emerald 的指标层级：名称/国旗/地区、在线状态、CPU/内存/磁盘、上下行、流量（仅当 SNM 提供额度）、运行时长、最后上报、线路延迟/丢包趋势。
- 节点卡片点击通过 hash 路由打开上游节点详情页；不依赖不存在的后端路由。

### 详情面板

- 继续使用 Emerald 的深浅色玻璃面板和绿色强调色。
- 展示公开规格、在线状态、CPU/内存/磁盘、上下行、累计流量、运行时长、最近 60 点实时历史图表、探测线路图表。
- 不展示 Emerald 原项目依赖的费用、续费、IPv4/IPv6、GPU 等非公开字段。

### 主题配置

`site.options` 支持以下参数（均有安全默认值）：

- `earthViewMode`: `earth` / `earth-stop` / `maps` / `cards` / `hide`
- `defaultViewMode`: `card` / `list`
- `accent`: 合法的六位十六进制颜色
- `alertEnabled`、`alertTitle`、`alertContent`
- `showClock`、`showFooter`
- `backgroundEnabled`、`lightBackgroundUrl`、`darkBackgroundUrl`、`backgroundBlur`、`backgroundOverlay`

主题参数不改变数据契约，非法值回退到默认值。

## 数据适配

- `snapshot`/`nodes`：全量节点和站点设置。
- `update`：按 `changedIds` 局部刷新卡片与图表。
- `probes`：刷新节点线路延迟/丢包数据；未测量显示“未测量”，不显示伪造的 0ms。
- `site`：更新站点标题、副标题、公告、主题参数与页面图标。
- `connection`/`tick`：连接状态、相对时间与时钟。
- 沿用上游格式化与 ECharts 图表组件；单位换算仅位于 SDK 映射边界，不重复转换。

## 可靠性与安全

- 节点名称等公开文本使用 Vue 模板插值；沿用上游组件与固定结构，不将节点名称拼接为 HTML。
- 搜索只在已收到的公开内存状态中运行；不读取 IP、管理端点或浏览器跨域数据。
- 主题销毁/重绘使用浏览器原生事件；响应式布局覆盖 360px 以上移动屏幕。
- 现有主题文件不改；新主题可被 Master 扫描、预览和切换。

## 验收标准

- `theme.json` 的 `id` 为 `emerald`，且与现有主题不冲突。
- `/themes/emerald/` 可直接加载入口、样式和脚本；脚本仅引用 `/vendor/*` 与主题相对资源。
- 在真实 SDK 快照、更新、探测和断线事件下，首页、卡片/列表、详情面板和深浅色切换都不抛异常。
- 静态校验、仓库测试与 .NET 测试通过；原 `default`、`minimal`、`panorama` 字节保持不变。

## 当前验证状态（2026-10-24）

锁定依赖（禁用安装脚本）安装、完整 TypeScript 检查、Vue 生产构建和统一 web 构建已通过；可安装 ZIP 在隔离 Master 安装成功（201），真实 MessagePack Agent/公共 SDK、资源 HTTP、14 项 Node 回归与 216 项 .NET 回归通过。未修改生产默认主题。

浏览器来源授权被拒绝，尚未完成视觉/交互验收。HTTP/SDK 测试不等于地球/地图、列表、搜索、深浅色、详情刷新或移动端的浏览器验收；完整交付状态仍为待验收。上一次权限审查服务不可用及首次编译错误保留在事务证据中，不计入本次成功结果。
