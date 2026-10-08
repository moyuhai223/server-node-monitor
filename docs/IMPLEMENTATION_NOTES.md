# 实现说明与设计差异(IMPLEMENTATION_NOTES)

## Panorama 与延时探测（2026-09-30）

- 新增独立 `panorama` 主题，复用原生主题 SDK；增加卡片/列表、搜索/状态筛选、深浅色、响应式布局和每线路延时曲线。
- Agent 主动获取 ICMP/TCP 配置并独立上报测量；原心跳保持 9 字段。使用 AOT 静态 formatter，不执行远程命令或外部 ping 程序。
- Master 将目标配置保存到现有 Settings 表，结果仅在内存保留最近 60 次；目标修订与节点归属校验、配置删除清理、公共字段脱敏已覆盖测试。
- Windows 本机验证：183 项 .NET 测试、2 项 Node 测试通过，管理后台构建成功，新表单 ESLint 无警告；浏览器验证表单保存、搜索/空结果、视图切换、390px 深色布局，以及真实 Agent 的回环 ICMP、TCP 成功和关闭端口失败。
- Native AOT：win-arm64 ILC 完成、未出现 IL 警告，最终链接因缺少可用 MSVC 链接器未完成。Linux ICMP 权限及各平台最终发行二进制仍需 CI/真实机器验证。
- 使用、升级、统计语义及 Linux ICMP 能力配置见 [PROBES.md](PROBES.md)。本期不包含长期延时历史和延时告警。

`docs/{DESIGN,PROTOCOL,API,DATA,DEPLOY,FRONTEND}.md` 是采纳的设计(候选方案 A)。实现过程中的差异与补充如下;以本文件与代码为准。

## 已验证的事实(2026-09-09,本机 Windows 11 ARM64 VM)

| 检查 | 结果 |
|---|---|
| `dotnet build ServerNodeMonitor.slnx -c Release` | 0 错误 0 警告(Contracts/Agent 开启 AOT/Trim/SingleFile 分析器 + `TreatWarningsAsErrors`) |
| `dotnet test` | 166 通过(Contracts 80 / Agent 61 / Master 25) |
| Agent ILC(`scripts/ilc-check.sh`,win-arm64) | ILC 完成、**0 条 IL 警告**;仅 link 步骤因无 MSVC 失败(预期) |
| `scripts/e2e.sh` 等价流程 | 注册/心跳/IP 合并/1 分钟桶/离线告警/Webhook 投递/恢复 全部通过 |
| 浏览器验证 | `/admin/` 登录、总览、节点管理、节点详情(图表/流量/告警)、`/` 大屏实时刷新且载荷不含 IP/主机名/备注 |
| GitHub Actions(2026-09-10,`v1.0.0`) | `agent-aot`:linux-x64 / linux-arm64 / win-x64 三平台 Native AOT 发布成功,IL 警告门禁通过;`master-build`:Linux 上构建 + 166 测试 + ILC 门禁通过,发布 `snm-master-linux-{x64,arm64}.tar.gz`,推送多架构镜像 `ghcr.io/moyuhai223/snm-master:{v1.0.0,latest}` |
| 发布产物实测 | 下载 Release 的 `snm-agent-win-x64.zip`(6.7 MB 单文件 AOT 二进制),sha256 校验一致,在本机以 x64 仿真运行 `--version` / `test` 正常采集 |

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
| GeoIP 刷新 | 后台每 6 小时 | 默认 GitHub Releases `server-country`，旧 npm 缓存自动迁移；成功后重算所有自动国家。后台与节点重算同时尊重配置/运行时开关；管理员可手动下载，运行时禁用时不重算。两套 CSV 通过单一清单原子发布 |
| 系统信息 `queues.notificationsPending` | 队列长度 | 单消费者 Channel 不支持 `Count`,改为手动计数 |
| 时序表 `DiskUsedMb/DiskTotalMb` | — | 1 分钟桶存均值(汇总磁盘),用于 30 天磁盘曲线 |
| 大屏字节单位 | — | 流量/网速十进制(1 TB = 1000 GB,与商家口径一致);内存/磁盘 1024 进制 |
| Master 版本号 | `-p:Version` | `Directory.Build.props` 默认 `1.0.0-dev`,CI 用 tag 覆盖;`InformationalVersion` 含 commit sha |
| 安装脚本 | `deploy/install-agent.sh.tmpl` 模板 + Master 内嵌副本 | 单一来源:`deploy/install-agent.sh` / `.ps1` 直接被 Master 嵌入(csproj 链接),模板占位符未渲染时脚本按参数/环境变量独立运行;新增 `deploy/install-master.sh` 服务端一键安装(见 README) |
| 发行包内容 | — | `appsettings.Development.json` 不再进入 publish 输出(`CopyToPublishDirectory=Never`),安装脚本也会删除旧包里的该文件 |

## 主题系统(2026-09-11)

- `web/public` 拆为 `web/sdk`(`snm-client.js` + SignalR 包 → `/vendor/`)与 `web/themes/<id>`(内置 `default`、`minimal`),每个主题一个 `theme.json`。
- Master:`ThemeService`(扫描 `wwwroot/themes` 与 `<DataDir>/themes`,zip 安装校验:id 规则、路径穿越、扩展名白名单、解压上限、SDK 版本、内置 id 冲突)+ `ThemeMiddleware`(`/` = 启用主题,`/themes/<id>/` = 预览;保留前缀 `/api /hubs /admin /vendor /install /healthz`)+ `/api/settings/themes` 管理接口 + 设置键 `site.theme` / `site.themeOptions`。
- `PublicSiteDto` 新增 `theme`、`opts` 字段;站点级设置变化通过 Public Hub 重发快照,SDK 检测主题变化自动 reload。
- 旧的 `Snm:Dev:PublicSourceDir` 改为 `Snm:Dev:WebSourceDir`(开发时直接从 `web/` 提供 SDK 与主题)。
- 测试:`ThemeTests`(安装/启用/预览/删除回退、恶意包拒绝、目录穿越、未知主题回退)。

## 尚未做 / 需要在真实环境补充验证

- Native AOT 二进制由 GitHub Actions 产出并已实测可运行(见上表);本机仍无法自行链接,`scripts/ilc-check.sh` 只到 ILC 阶段。
- Linux 采集器(`/proc`、`/sys`)只经过解析器单元测试(固定文本样本)与 Windows 上的 JIT 运行验证;首次在 Linux 上部署时请先执行 `snm-agent test` 核对网卡/挂载点名单。
- Telegram 渠道未做真实发送(需要 Bot Token);Webhook 已用本地接收器验证含 HMAC 签名头。
- `web/admin` 未做移动端适配打磨;表格在窄窗口依赖横向滚动。
- 反代(Nginx/1Panel)配置提供了模板但未在线验证 WebSocket 升级。

## CI 踩坑记录(v1.0.0 发布过程)

1. 根 `.gitignore` 的 `data/` 规则在 Windows(大小写不敏感)上把 `src/SNM.Master/Data/` 排除在仓库之外,Linux 上编译失败 → 改为具体路径规则。
2. `node:22-alpine` 没有 `bash` → 构建阶段 `apk add bash`。
3. Docker `VERSION` 构建参数来自 `github.ref_name`,带 `v` 前缀会触发 NETSDK1018 → Dockerfile 内 `${VERSION#v}`。
4. arm64 镜像在 QEMU 下执行 `npm ci` 时 Node 报 Illegal instruction → 前端与 IL 构建阶段固定 `--platform=$BUILDPLATFORM`,仅运行时镜像多架构。
5. 重打同名 tag 前需先删除已存在的 Release,否则旧 Release 会残留为无标签草稿。
