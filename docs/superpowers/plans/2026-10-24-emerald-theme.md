# Emerald 可选主题实施计划

## 1. 主题包骨架

- 创建 `web/themes/emerald/`。
- 添加 `theme.json`、`index.html`、`style.css`、`theme.js`、`probes.js`、`preview.svg`。
- 只复制 MIT 许可下必要的视觉内容，保留 `binaryu/monitor-theme-emerald` 的作者链接在清单说明中。

## 2. 公开 SDK 适配

- 在 `theme.js` 中创建 `createClient({ theme: "emerald", historyPoints: 60 })`。
- 实现 snapshot/nodes/update/site/connection/tick/probes 事件处理。
- 以 `node.live`、`node.hist`、`node.probes` 渲染公开指标；不补造 Emerald 没有的字段。

## 3. Emerald 首页

- 复刻 Emerald 的顶部品牌、状态、公告、地球/点阵地图区、摘要统计、分组、搜索、卡片/列表节点区和页脚。
- 用 Canvas 点阵球实现无依赖的轻量地球，并用国家级坐标显示节点聚合；地图模式使用同一份坐标数据的平面投影。
- 使用 localStorage 保存深浅色、卡片/列表与地球模式。

## 4. 节点详情

- 用 hash（`#node=<id>`）打开右侧/全屏详情面板，避免依赖不存在的 `/instance/:id` 后端路由。
- 复用 SDK 的实时历史和探测数据绘制 Sparkline、延迟折线与丢包状态；关闭详情回到当前筛选状态。

## 5. 配置与无障碍

- 读取 `site.options`，验证颜色、背景 URL、模式枚举与布尔值。
- 保留键盘可操作的按钮、搜索、aria-label、状态文本和 reduced-motion 规则。
- 公告文本以纯文本显示，背景 URL 仅接受 `https:` 或同源相对 URL。

## 6. 验证

- 运行主题清单/资源引用静态校验、`git diff --check`、`node --test scripts/probes.test.mjs`、`dotnet test ServerNodeMonitor.slnx -c Release`。
- 手动用浏览器加载本地静态预览，确认空态、快照、主题参数、深浅色和详情 hash。
- 按项目强制交易记录创建 `MODIFIED_FILE`、`DIFF_FILE`、`VERIFICATION.txt`、`ROLLBACK.sh`，记录原主题目录哈希、候选主题校验、回滚恢复哈希与重新应用结果。
