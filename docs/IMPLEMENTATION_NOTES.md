# 实现说明与设计差异(IMPLEMENTATION_NOTES)

`docs/{DESIGN,PROTOCOL,API,DATA,DEPLOY,FRONTEND}.md` 是采纳的设计(候选方案 A)。实现过程中的差异与补充如下;以本文件与代码为准。

## 已验证的事实(2026-09-09,本机 Windows 11 ARM64 VM)

| 检查 | 结果 |
|---|---|
| `dotnet build ServerNodeMonitor.slnx -c Release` | 0 错误 0 警告(Contracts/Agent 开启 AOT/Trim/SingleFile 分析器 + `TreatWarningsAsErrors`) |
| `dotnet test` | 166 通过(Contracts 80 / Agent 61 / Master 25) |
| Agent ILC(`scripts/ilc-check.sh`,win-arm64) | ILC 完成、**0 条 IL 警告**;仅 link 步骤因无 MSVC 失败(预期) |
| `scripts/e2e.sh` 等价流程 | 注册/心跳/IP 合并/1 分钟桶/离线告警/Webhook 投递/恢复 全部通过 |
| 浏览器验证 | `/admin/` 登录、总览、节点管理、节点详情(图表/流量/告警)、`/` 大屏实时刷新且载荷不含 IP/主机名/备注 |

## 与设计文档的差异

| 主题 | 设计 | 实现 |
|---|---|---|
| 解决方案文件 | `ServerNodeMonitor.sln` | `ServerNodeMonitor.slnx`(.NET 10 新格式) |
| 菜单树 | 研究文档建议 `/monitor` + `/monitor/:id` | 采用 API.md 的 `Nodes`(`/nodes`)+ 隐藏子项 `NodeDetail`(`/nodes/:id`),节点配置与探针视图合并在一个页面/详情页 |
| 刷新令牌 | `POST /api/auth/refresh`,失败码 `11007` | 失败码 **10011**(`ApiCodes.RefreshInvalid`),前端拦截器同时把 `401/10011` 视为需要重新登录 |
| 改密后会话 | 部分文档要求 `TokenVersion++` | 改密**不**使当前会话失效;撤销除当前 refresh token 外的其他刷新令牌(请求体可带 `refreshToken`);`POST /api/auth/logout {all:true}` 才会 `TokenVersion++` |
| 静态文件与路由 | `MapFallbackToFile("/admin/{*path}")` | 显式 `UseRouting()` 放在静态文件之后,回退路由使用 `{*path:nonfile}`;仅当大屏 `index.html` 缺失时才注册 `/` 的提示端点(否则会遮蔽静态文件) |
| Windows 探针安装 | schtasks 命令行带参数 | `Register-ScheduledTask`(SYSTEM、开机触发、失败 1 分钟重启),配置写入 `%ProgramData%\snm-agent\agent.env`(ACL 仅 SYSTEM/Administrators),探针用 `--env-file` 读取;卸载/代理通过 `SNM_UNINSTALL=1` / `SNM_PROXY` 环境变量传入(`irm | iex` 不便传参) |
| Agent `User-Agent` | 覆盖 UA | 使用自定义头 `X-SNM-Agent: snm-agent/<ver> (<os>; <arch>)`,避免与 SignalR 客户端 UA 冲突 |
| Windows 网卡过滤 | Type/OperStatus/名称规则 | 额外排除 `InterfaceAndOperStatusFlags.FilterInterface`(NDIS 轻量筛选器会镜像计数)与 `Local Area Connection*` 别名 |
| GeoIP 刷新 | 后台每 6 小时 | 同时尊重 `Snm:GeoIp:Enabled`(配置)与 `geoip.enabled`(运行时设置) |
| 系统信息 `queues.notificationsPending` | 队列长度 | 单消费者 Channel 不支持 `Count`,改为手动计数 |
| 时序表 `DiskUsedMb/DiskTotalMb` | — | 1 分钟桶存均值(汇总磁盘),用于 30 天磁盘曲线 |
| 大屏字节单位 | — | 流量/网速十进制(1 TB = 1000 GB,与商家口径一致);内存/磁盘 1024 进制 |
| Master 版本号 | `-p:Version` | `Directory.Build.props` 默认 `1.0.0-dev`,CI 用 tag 覆盖;`InformationalVersion` 含 commit sha |

## 尚未做 / 需要在真实环境补充验证

- **Native AOT 二进制**只能由 GitHub Actions(`.github/workflows/agent-aot.yml`)产出;本机没有 MSVC 链接器,无法运行 AOT 产物做运行时验证(ILC 零警告是目前能达到的上限)。
- Linux 采集器(`/proc`、`/sys`)只经过解析器单元测试(固定文本样本)与 Windows 上的 JIT 运行验证;首次在 Linux 上部署时请先执行 `snm-agent test` 核对网卡/挂载点名单。
- Telegram 渠道未做真实发送(需要 Bot Token);Webhook 已用本地接收器验证含 HMAC 签名头。
- `web/admin` 未做移动端适配打磨;表格在窄窗口依赖横向滚动。
- 反代(Nginx/1Panel)配置提供了模板但未在线验证 WebSocket 升级。
