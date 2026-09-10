# Server Node Monitor

企业级服务器节点监控平台:**.NET 10 Native AOT 探针** + **ASP.NET Core Master(SignalR + MessagePack + EF Core SQLite WAL)** + **vue-naive-admin 管理后台** + **纯静态实时大屏**。需求见 [docs/PRD.md](docs/PRD.md),设计契约见 `docs/{DESIGN,PROTOCOL,API,DATA,DEPLOY,FRONTEND}.md`,实现差异见 [docs/IMPLEMENTATION_NOTES.md](docs/IMPLEMENTATION_NOTES.md)。发行包见 [Releases](https://github.com/moyuhai223/server-node-monitor/releases)(探针 linux-x64 / linux-arm64 / win-x64,Master linux-x64 / linux-arm64,容器镜像 `ghcr.io/moyuhai223/snm-master`)。

## 一键安装

### 服务端(Linux x86_64 / aarch64,systemd)

```bash
curl -fsSL https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-master.sh | sudo bash -s -- --public-url https://m.example.com
```

- 下载最新 Release 并校验 sha256,创建 `snm-master` 系统用户,程序装到 `/opt/snm-master`,数据 `/var/lib/snm-master`,配置 `/etc/snm-master/master.env`(0600),注册 systemd 服务并等待 `/healthz`。
- 默认只监听 `127.0.0.1:5080`;首次安装若未给 `--admin-password` 会生成随机密码并**只打印一次**。
- 反向代理:加 `--nginx m.example.com` 自动写入 Nginx 站点(HTTP,`/hubs/` 已开 WebSocket),随后 `certbot --nginx -d m.example.com` 上 TLS;用 1Panel 的话在面板里把域名反代到 `127.0.0.1:5080` 并开启 WebSocket。
- 升级:重跑同一条命令(会先停服务并备份数据库到 `backups/`);状态:`... | sudo bash -s -- status`;卸载:`... | sudo bash -s -- uninstall`(加 `--purge` 连数据一起删)。
- 其他选项:`--port`、`--listen`、`--data-dir`、`--timezone`、`--version vX.Y.Z`、`--yes`(免交互)、`--dry-run`。容器部署见 [deploy/docker](deploy/docker)。

### 探针(被监控的服务器)

方式一(推荐):后台 **节点管理 → 新建节点 → 安装脚本**,复制带专属密钥的一键命令到目标机执行。

方式二(手动传参,Linux):

```bash
curl -fsSL https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-agent.sh | sudo bash -s -- --server https://m.example.com --key snmk_xxxxxxxx
```

Windows x64(管理员 PowerShell):

```powershell
$env:SNM_SERVER = 'https://m.example.com'; $env:SNM_KEY = 'snmk_xxxxxxxx'; irm https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-agent.ps1 | iex
```

- 探针以专用用户 `snm-agent` 运行(systemd 加固;Windows 为 SYSTEM 计划任务),只读采集、不监听端口、无远程执行、无自更新。
- 内网机器经网关代理:`--proxy socks5://10.0.0.1:1080`(Windows:`$env:SNM_PROXY`);指定网卡 `--net-if eth0`;固定版本 `--version v1.0.0`;卸载 `uninstall`(Windows:`$env:SNM_UNINSTALL='1'`)。
- 第一次在 Linux 上部署时可先 `/opt/snm-agent/snm-agent test` 查看采集到的网卡、挂载点与 IP。

## 目录

```
src/SNM.Contracts   MessagePack DTO、Hub 常量、AOT 安全的 MessagePack Hub 协议(vendored MIT 代码 + 静态 formatter)
src/SNM.Agent       探针(Native AOT,Linux x64/arm64 + Windows x64;只读、不监听、无远程执行、无自更新)
src/SNM.Master      Master(REST API、三个 SignalR Hub、时序降采样、流量 Delta 计费、告警、Telegram/Webhook、安装脚本)
web/admin           管理后台(vue-naive-admin 2.x 改造,Vite 8 / Vue 3.5 / Naive UI / ECharts)
web/public          公开大屏(无框架,仅一条 SignalR 连接)
tests/              xunit:契约字节等价、探针解析器、Master 集成(WebApplicationFactory + 真实 Hub 往返)
deploy/             install-master.sh、install-agent.sh/.ps1(同时是 Master 渲染的模板)、systemd、Nginx、Docker
.github/workflows   agent-aot(三平台 Native AOT)、master-build(测试 + 发行包 + 多架构镜像)
scripts/            env.sh / dev.sh / build-web.sh / e2e.sh / ilc-check.sh
```

## 快速开始(开发)

```bash
source scripts/env.sh                 # ~/.dotnet 下的 .NET 10 SDK
bash scripts/build-web.sh             # npm install + 构建后台/大屏到 src/SNM.Master/wwwroot
bash scripts/dev.sh                   # 启动 Master(http://127.0.0.1:5080, admin/admin123456)并创建本机探针
```

- 后台:`http://127.0.0.1:5080/admin/` · 大屏:`http://127.0.0.1:5080/` · 前端热更新:`cd web/admin && npm run dev`(`http://localhost:3200/admin/`)
- 测试:`dotnet test ServerNodeMonitor.slnx`;端到端:`bash scripts/e2e.sh`;探针 AOT 门禁:`bash scripts/ilc-check.sh`
- 发布:`git tag -a vX.Y.Z -m ... && git push origin vX.Y.Z`,GitHub Actions 自动产出探针/Master 发行包与镜像。

## 安全模型

探针只持有节点专属 `AgentKey`,通过 `X-SNM-Agent-Key` 头连接 `/hubs/agent`;Master 对探针的下行消息只有 `configure`(采集间隔)。管理端为 JWT(access + 轮换 refresh),公开大屏只暴露脱敏字段(有测试保证)。

## 许可

MIT,见 [LICENSE](LICENSE)。`src/SNM.Contracts/Protocol/Vendored` 来自 dotnet/aspnetcore(MIT,见 `src/SNM.Contracts/THIRD-PARTY-NOTICES.md`),`web/admin` 基于 vue-naive-admin(MIT)。
