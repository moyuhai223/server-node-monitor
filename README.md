# Server Node Monitor

企业级服务器节点监控平台:**.NET 10 Native AOT 探针** + **ASP.NET Core Master(SignalR + MessagePack + EF Core SQLite WAL)** + **vue-naive-admin 管理后台** + **纯静态实时大屏**。需求见 [docs/PRD.md](docs/PRD.md),设计契约见 `docs/{DESIGN,PROTOCOL,API,DATA,DEPLOY,FRONTEND}.md`,实现差异见 [docs/IMPLEMENTATION_NOTES.md](docs/IMPLEMENTATION_NOTES.md)。

## 目录

```
src/SNM.Contracts   MessagePack DTO、Hub 常量、AOT 安全的 MessagePack Hub 协议(vendored MIT 代码 + 静态 formatter)
src/SNM.Agent       探针(Native AOT,Linux x64/arm64 + Windows x64;只读、不监听、无远程执行、无自更新)
src/SNM.Master      Master(REST API、三个 SignalR Hub、时序降采样、流量 Delta 计费、告警、Telegram/Webhook、安装脚本)
web/admin           管理后台(vue-naive-admin 2.x 改造,Vite 8 / Vue 3.5 / Naive UI / ECharts)
web/public          公开大屏(无框架,仅一条 SignalR 连接)
tests/              xunit:契约字节等价、探针解析器、Master 集成(WebApplicationFactory + 真实 Hub 往返)
deploy/             systemd、Nginx、Docker;.github/workflows 产出 AOT 探针与 Master 发行包
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

## 部署

1. Master:`deploy/systemd/snm-master.service` + `deploy/systemd/master.env.example`(或 `deploy/docker/`);默认只监听 `127.0.0.1:5080`,TLS/域名交给 Nginx/1Panel(`deploy/nginx/snm.conf`,`/hubs/` 需 WebSocket)。
2. 系统设置 → 站点:填写 `site.publicBaseUrl`;探针:填写 `agent.releaseBaseUrl`(GitHub Release 下载地址)。
3. 节点管理 → 新建节点 → 安装脚本:在目标机执行 `curl -fsSL https://<master>/install/<token> | sudo bash`(Windows:`irm "...?os=windows" | iex`)。
4. 探针二进制由 `.github/workflows/agent-aot.yml` 在打 `vX.Y.Z` tag 时构建(linux-x64 / linux-arm64 / win-x64,IL 警告为零才通过)。

## 安全模型

探针只持有节点专属 `AgentKey`,通过 `X-SNM-Agent-Key` 头连接 `/hubs/agent`;Master 对探针的下行消息只有 `configure`(采集间隔)。管理端为 JWT(access + 轮换 refresh),公开大屏只暴露脱敏字段(有测试保证)。

## 许可

`src/SNM.Contracts/Protocol/Vendored` 来自 dotnet/aspnetcore(MIT),见 `src/SNM.Contracts/THIRD-PARTY-NOTICES.md`。
