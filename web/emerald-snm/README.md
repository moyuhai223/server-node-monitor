# Emerald · SNM 可选主题

保留 [binaryu/monitor-theme-emerald](https://github.com/binaryu/monitor-theme-emerald) 的 Vue/Tailwind 界面、Cobe 地球、ECharts 地图/曲线、卡片/列表、深浅色与详情页。仅数据边界改用 SNM 公开主题 SDK；不会启用、替换其他主题或更改 Agent。

上游基线：`v1.0.10`（`dc82781a5b52aac760331ddeb4dd89944c33ee2b`）。普通触屏点击可直接进入节点；线路信息和 EWMA 说明按钮支持轻点。SNM 公开协议不提供 Swap 时，不显示交换空间数值或折线。

## 构建与安装

- 在仓库根目录运行 `bash scripts/build-web.sh`，统一构建管理端、SDK 和 Emerald。需要 Node.js 22 与 package.json 指定的 pnpm；安装使用冻结锁文件并禁用依赖安装脚本。
- 单独构建：进入 `web/emerald-snm`，运行 `pnpm install --frozen-lockfile --ignore-scripts`，再运行 `pnpm run build`。
- 输出位于 `web/themes/emerald/`。运行 `bash scripts/pack-theme.sh web/themes/emerald` 生成内置 ID 为 `emerald` 的 ZIP。若现有 Master 已内置 Emerald，请运行 `python scripts/pack-emerald-upload.py` 生成 `emerald-snm` 独立上传包，在后台上传后预览、选择；不会覆盖原主题。
- 在“系统设置 → 大屏主题”预览或选择 Emerald；也可直接访问 `/themes/emerald/`。详情使用 hash 路由，预览入口和根入口均支持刷新。

## 数据与限制

数据来自 `/vendor/snm-client.js`，不请求 monitor-probe 的 REST API；国家采用 SNM 公开的 cc，不二次查询访客 IP。国家坐标为国家中心点，不代表机房精确位置。

保留最近 60 点 CPU、内存、上下行和公开线路探测。未公开的费用、IPv4/IPv6、GPU、访客定位以及未提供的长期历史不展示。隐藏规格时仍展示真实内存/磁盘使用率；线路失败为断点，不伪造 0ms，过期/不支持会标记。

配置使用 SNM 的 site.themeOptions（SDK site.options），选项参见源码 theme-options.json；禁止访客地理定位。上游 MIT 许可随产物保留为 LICENSE.txt。
