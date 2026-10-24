# Emerald 原界面可选主题实施状态

## 已确认范围

保留上游 Vue/Tailwind 组件、Cobe 地球、ECharts 地图/曲线、卡片/列表、深浅色与详情页。只在 SNM 新增可选主题 emerald；原始 web/emerald、现有 default/minimal/panorama、后端与 Agent 不变。不推送、不发版、不更改生产启用主题。

## 本次已执行（2026-10-24）

- 锁定依赖安装成功：pnpm install --frozen-lockfile --ignore-scripts，供应链检查通过，exit 0。
- 修复 /vendor/snm-client.js 的 TypeScript 模块声明；完整 vue-tsc 与 Vite 生产构建通过。
- 修复公开规格隐藏时的真实内存/磁盘百分比、全丢包线路、过期/不支持及最近失败状态；未测量不伪造 0ms。
- 统一 scripts/build-web.sh 构建 Emerald；CI 与 Docker 使用锁文件、禁用依赖安装脚本；支持 Windows pnpm.cmd。
- 统一构建产生 default、emerald、minimal、panorama 四套主题；输出包含 MIT LICENSE.txt。
- 独立 SNM 上传 ZIP 已生成并在隔离 Master 安装成功（HTTP 201，安装后未启用）。
- 真实 MessagePack Agent → Master → 公共 SDK 验证通过：SG、12.5% CPU、536870912 bytes 内存、60 点历史、100% 丢包、真实 0ms 与不支持排除。
- HTTP 校验通过：全部 28 个编译 JS/CSS、入口/清单/预览/许可证；根入口 base 与两种 hash 入口返回 200。仅在隔离环境测试启用后恢复 default，不代表浏览器路由渲染通过。
- 公开规格/流量隐藏经过真实 SDK 验证；14 项 Node 回归、216 项 .NET 回归通过。

## 第一个未完成步骤

浏览器界面交互验收。浏览器工具拒绝 http://127.0.0.1:52417 的来源授权，并明确返回用户拒绝；未绕过此限制。HTTP/SDK 验证不能替代浏览器的地球/地图、深浅色、搜索/国家筛选、列表切换、详情刷新、移动布局、断线/空态验收。

## 接下来

用户允许浏览器访问本地验收地址后，继续浏览器验收并修复观察到的问题；在此之前不标记完整功能验收完成。源码事务 ZIP 与真正可安装 ZIP 分开保留，复用同一套四角色证据。不自动推送、发布或切换生产主题。
