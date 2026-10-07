# 延时探测与 Panorama 主题

## 使用

1. 更新 Master、Web SDK 与需要探测的 Agent。现有性能协议不变，旧 Agent 可继续监控，但不会执行探测。
2. 后台 **系统设置 → 延时探测 → 新增目标**，填写公开线路名称、目标 IP/域名、ICMP/TCP、TCP 端口、间隔及超时，保存。所有已启用节点自动参与，后续新增节点也自动加入，无需手动选择。旧目标保留原分配，打开此页面并保存后会统一切换为全部节点。
3. 后台 **系统设置 → 大屏主题 → Panorama 全景 → 启用**。主题也可在 `/themes/panorama/` 预览。

目标必须由管理员选择实际需要测量的地址。没有内置运营商地址，避免把任意公共 DNS 的网络路径标成某个运营商。支持 IPv4、IPv6、域名；域名优先使用解析到的 IPv4，想固定 IPv6 可直接填 IPv6 地址。

Agent 通常在 30 秒内获取配置；正在进行的批次可能使其延后（最多 16 个目标、4 并发、每次最多 10 秒）。禁止任意命令、脚本和外部 ping 程序。测量始终走节点直连网络，`--proxy` 仅控制与 Master 的连接。

## 指标含义

- ICMP：系统提供的 Echo RTT，毫秒精度；回环可能确实为 0 ms。丢包率为窗口内失败/超时占有效尝试比例，DNS/网络错误也算失败。
- TCP：DNS 解析后的 TCP 建连时间，展示到 0.1 ms，不包括 HTTP、TLS 或业务处理时间。统计名称为“失败率”，不是 IP 丢包率。
- 最近 60 次结果缓存在 Master 内存，首连/重连快照带回缓存；重启 Master 后重新积累。本期不包含长期延时存储和告警。
- 未测量、节点离线、数据过期、超时、错误、不支持分别显示。失败点断开折线并用红点标记；不支持不计入失败率分母。统计基于最近有效尝试，不把节点未上报期间当作成功或失败。

## Linux ICMP 权限

TCP 无需额外权限。ICMP 需要 raw socket 能力；默认加固安装可能未授予 `CAP_NET_RAW`，这时显示“不支持”，可使用 TCP。若需要 ICMP，可由运维为 `snm-agent` 的 systemd 服务配置：

```ini
[Service]
CapabilityBoundingSet=CAP_NET_RAW
AmbientCapabilities=CAP_NET_RAW
```

应用服务配置后重启 Agent。代码在 Unix 上先检查 raw socket 权限，避免 .NET 在缺少权限时使用外部 ping 可执行文件。没有自动修改主机权限的行为。

## 协议与管理接口

- `GET /api/settings/probes`：管理员获取私有目标、节点及当前结果。
- `PUT /api/settings/probes`：管理员整体保存目标数组；最多 16 个，间隔 10–3600 秒，超时 200–10000 ms。界面保存 `allNodes: true`、`nodeIds: []`，动态覆盖所有已启用节点，无需维护节点 ID 列表。旧客户端仍可使用 `allNodes: false`（缺省值）和 1–512 个有效且不重复的 `nodeIds`。仅改变节点数量不会重置已有探测结果。
- Agent `getProbes`：注册后获取仅分配给本节点的 `ProbeConfigDto`；Agent `probe`：独立上报 `ProbeResultDto`。数组型 DTO 使用 AOT 静态 formatter，与现有 9 字段心跳分离。
- 配置含服务端生成的修订标识；目标修改后旧结果被拒绝。结果校验节点归属、当前注册连接、修订、数值范围和上报频率。
- Public Hub `probes`：`{id, probes:[{id,name,kind,interval,points:[{ts,state,us}]}]}`。SDK 提供 `node.probes` 和 `probes(state,nodeId)` 事件，时间戳由服务端生成。
- `state`：0 成功，1 超时，2 错误，3 不支持；失败时 `us=-1`。公开 DTO 不含目标地址、修订标识或私密资产字段。
- 配置复用 SQLite Settings，无新增迁移；通用设置接口不能绕过专用验证修改该内部配置。

## 主题分发

`web/themes/panorama` 随 Master 构建自动打包。独立 ZIP 分发时应使用未与服务器内置主题冲突的 ID（同时修改 `theme.json` 和 `theme.js` 的客户端主题 ID）；内置主题通过更新 Master/web 资源升级。

主题参数支持 `{ "accent": "#526be8" }`。卡片/列表和深浅色选择保存在浏览器本地。主题布局参考 [Komari 官方前端](https://github.com/komari-monitor/komari-web)，使用本项目原生 HTML/CSS/JS 独立实现。

验证命令：`dotnet test ServerNodeMonitor.slnx -c Release`、`node --test scripts/probes.test.mjs`、`cd web/admin && npm ci && npm run build`。
