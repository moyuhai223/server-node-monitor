# 设计约束简报 (BRIEF) — 所有设计/实现 agent 的共同输入

> 本文件由编排者根据本机环境勘查与 PRD 写成。**第 2 节是已锁定决策,不要重新讨论**;第 3 节是设计阶段必须定案的问题。PRD 原文见 `docs/PRD.md`。

## 0. 目标与验收

交付一个完整可运行的 monorepo:Contracts / Agent / Master / 管理后台(web/admin) / 公开大屏(web/public) / deploy + CI / docs / tests。

验收标准(全部满足才算完成):

1. `dotnet build ServerNodeMonitor.sln -c Release` 0 错误;`SNM.Agent`、`SNM.Contracts` 在 AOT/Trim/SingleFile 分析器开启下 **0 个 IL2xxx/IL3xxx 警告**。
2. `dotnet test` 全部通过(契约序列化、流量 Delta、降采样、告警防抖、账单周期、Hub 集成测试)。
3. `web/admin` `npm run build` 通过;`web/public` 可直接由 Master 托管。
4. 端到端:本机起 Master + Agent(JIT 方式 `dotnet run`),管理后台可登录、节点显示在线、IP/国旗/CPU/内存/磁盘/网速正确、ECharts 有数据;公开大屏实时刷新、不含任何敏感字段;停掉 Agent 后 >30s 触发离线告警(Webhook 本地接收器可验证),恢复后收到恢复通知。
5. GitHub Actions 工作流能对 Agent 做 linux-x64 / linux-arm64 / win-x64 的 Native AOT 发布(本机无法执行,靠 YAML 正确性 + 分析器零警告保证)。

## 1. 本机环境事实

- Windows 11 **ARM64** 虚拟机,4 核,24GB 内存,Git Bash(MINGW64,`PROCESSOR_ARCHITECTURE=AMD64` 为 x64 仿真 shell)。
- .NET SDK 位于 `~/.dotnet`(win-arm64):8.0.421 与 **10.0.400**。使用前必须:
  ```bash
  export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
  ```
  `C:\Program Files\dotnet` 只有 8.0 运行时、无 SDK,**不要用**。项目根有 `global.json` 固定 10.0.400。
- **没有 MSVC 链接器、没有 Docker、没有 WSL** → Native AOT 在本机只能跑到 ILC 编译阶段;跨 OS AOT 不受支持。因此:
  - Agent 的 AOT 正确性靠:`PublishAot=true` + `IsAotCompatible=true` + `EnableTrimAnalyzer/EnableAotAnalyzer/EnableSingleFileAnalyzer` 构建 **零警告**,以及 ILC 阶段无错误。
  - ILC 验证命令(已实测可绕过链接器探测,ILC 会真正执行,最后 `link` 步骤失败是预期,ILC 阶段的 IL 警告/错误以此为准):
    ```bash
    dotnet publish src/SNM.Agent -c Release -r win-arm64 -p:PublishAot=true -p:IlcUseEnvironmentalTools=true -p:TrimmerSingleWarn=false
    ```
  - 功能验证用 JIT(`dotnet run`)。
  - 真正的 AOT 二进制交给 `deploy/.github/workflows/agent-aot.yml`(GitHub Actions,ubuntu/windows runner)。
- **已实测的 AOT 事实(2026-09-06,SDK 10.0.400 / 包 10.0.11)**:
  - `HubConnectionBuilder().WithUrl(...).Build()` 在 AOT 分析器下 **0 警告**(SignalR .NET 客户端本身 AOT 友好)。
  - `AddMessagePackProtocol()` 触发 **IL2026**:该方法标注 `[RequiresUnreferencedCode("MessagePack does not currently support trimming or native AOT.")]`。官方 `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 内部对参数用非泛型 `MessagePackSerializer.Serialize(Type, ...)` / `Deserialize(Type, ...)`,而 MessagePack 2.5 的非泛型路径依赖 `MakeGenericMethod` + `Expression.Compile()`(AOT 下只能靠解释器,且带 byref 参数),**不能作为 Agent 的方案直接使用**。
  - 候选路径(研究阶段必须用 spike 实证并定案):
    1. **在 `SNM.Contracts` 中 vendoring 一份 MIT 许可的 `MessagePackHubProtocolWorker`(dotnet/aspnetcore `release/10.0` 分支 `src/SignalR/common/Protocols.MessagePack/src/Protocol/MessagePackHubProtocolWorker.cs` 及其依赖的 `BinaryMessageParser/BinaryMessageFormatter` 等 shared 源码),实现自己的 `IHubProtocol`(Name 仍为 `messagepack`、Version 与官方一致、线格式完全兼容),`SerializeArgument/DeserializeObject` 改为对已知类型做静态 type-switch + 手写 `IMessagePackFormatter<T>`,完全不走反射/动态代码。Master 与浏览器继续用官方包。** 这是编排者倾向的方案。
    2. 官方协议包 + `StaticCompositeResolver` + 泛型 rooting + `UnconditionalSuppressMessage`(运行时行为无法在本机验证,风险高)。
    3. 其他(需说明)。
  - 无论哪条路径,都必须在 JIT 下用集成测试证明:Agent 侧协议 ⇄ Master 侧官方 MessagePack 协议 互通,且 DTO 字节序列与 `[MessagePackObject]/[Key]` 契约完全一致。
  - **ILC 实测**(上面的 ILC 验证命令,对"SignalR 客户端 + `AddMessagePackProtocol()`"的最小程序):ILC 正常生成 `*.obj`;`TrimmerSingleWarn=false` 下共 ~60 条 IL3050/IL2060/IL2070/IL2091 警告,**全部来自 MessagePack.dll 内部**的 `DynamicObjectResolver`/`DynamicUnionResolver`/`DynamicGenericResolver`/`DynamicEnumAsStringResolver`/`AttributeFormatterResolver`/`BuiltinResolverGetFormatterHelper`/`FormatterResolverExtensions.GetFormatterDynamic`/`MessagePackSecurity.ObjectFallbackEqualityComparer`(Reflection.Emit、`MakeGenericType/Method`)。结论:只要 Agent 代码路径**不引用** `MessagePackSerializer`/`StandardResolver`/`GetFormatterDynamic` 等动态入口(只用 `MessagePackWriter`/`MessagePackReader` + 手写 formatter),这些代码会被裁掉、警告消失。Agent 的验收口径:ILC 输出(`TrimmerSingleWarn=false`)**0 条 IL 警告**。
- Node v22.22 / npm 10.9;**无 pnpm**(需要可 `npm i -g pnpm` 或 `corepack enable`),无 jq。
- 网络全部可达:nuget.org、registry.npmjs.org、github.com(git clone 正常)、cdn.jsdelivr.net。
- 4 核 → 并行 agent 上限 2;每个实现 agent 必须自给自足、自己验证。

## 2. 已锁定的技术决策(不要重新讨论)

### 2.1 版本与包(Central Package Management)

- 所有 C# 项目 TFM `net10.0`;`Directory.Packages.props` 集中管理版本:
  - `Microsoft.AspNetCore.SignalR.Client` 10.0.11
  - `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 10.0.11
  - `MessagePack` **2.5.302**(与 SignalR 协议包依赖一致,**不升 3.x**)
  - `Microsoft.EntityFrameworkCore.Sqlite` 10.0.11(需要时 + `Microsoft.EntityFrameworkCore.Design` 10.0.11)
  - `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.11
  - `Microsoft.AspNetCore.Mvc.Testing` 10.0.11、`xunit`、`Microsoft.NET.Test.Sdk`(最新稳定)
  - 其他包需在 PROTOCOL/DESIGN 中说明理由,且必须 AOT 友好(Agent 侧)。
- 前端:vue-naive-admin **2.x 分支,commit `a55f0a2e6fe0561ce62c3452b96ec4b4cbcb8553`**(Vite 8 / Vue 3.5 / Naive UI 2.44 / Pinia 3 / vue-router 5 / UnoCSS 66 / 纯 JS)。ECharts 6 + vue-echarts 8。浏览器 SignalR:`@microsoft/signalr` 10.0.11 + `@microsoft/signalr-protocol-msgpack` 10.0.11。

### 2.2 仓库布局(固定)

```
server-node-monitor/
  global.json  Directory.Build.props  Directory.Packages.props  ServerNodeMonitor.sln  .editorconfig  .gitignore  README.md
  docs/                 PRD.md BRIEF.md DESIGN.md PROTOCOL.md API.md DATA.md DEPLOY.md
  src/SNM.Contracts/    MessagePack DTO + 常量(Hub 路径、方法名、单位约定);Agent 与 Master 共同引用;IsAotCompatible
  src/SNM.Agent/        Native AOT 控制台探针(Linux x64/arm64 为主,Windows x64 次之)
  src/SNM.Master/       ASP.NET Core Web API + SignalR + EF Core SQLite(WAL);托管 wwwroot(公开大屏在 /,管理后台在 /admin/)
  tests/SNM.Contracts.Tests/  tests/SNM.Master.Tests/  tests/SNM.Agent.Tests/
  web/admin/            vue-naive-admin 2.x 改造;vite base '/admin/';构建产物输出到 src/SNM.Master/wwwroot/admin/
  web/public/           公开大屏纯静态(index.html + css + js,无框架);构建/复制到 src/SNM.Master/wwwroot/
  deploy/               systemd unit 模板、install-agent.sh 模板、Master Dockerfile、.github/workflows/*.yml
  scripts/              env.sh、dev.sh(本机同时起 Master+Agent)、build-web.sh 等
```

### 2.3 运行与安全

- Master 默认监听 `http://127.0.0.1:5080`(appsettings/环境变量可改);TLS/反代由外部(Nginx/1Panel)完成。启用 `ForwardedHeaders`,仅信任 loopback 与配置的代理地址,用于获取探针真实公网 IP。
- URL 空间:`/` 公开大屏;`/admin/` 管理后台 SPA;`/api/...` REST;`/hubs/agent`(探针,MessagePack,AgentKey 鉴权)、`/hubs/public`(匿名,仅脱敏数据)、`/hubs/admin`(JWT,全量数据)。三个 Hub 均启用 MessagePack 协议。
- 管理端认证:JWT access + refresh token;单管理员账号;首次启动由 `SNM_ADMIN_USER` / `SNM_ADMIN_PASSWORD`(或 appsettings)初始化,密码哈希(`PasswordHasher` 或 PBKDF2)存 DB;后台可改密。开发模式 Vite dev server 代理 `/api` 与 `/hubs`(含 ws)到 `127.0.0.1:5080`,因此不需要 CORS。
- GeoIP:离线数据集 `https://cdn.jsdelivr.net/npm/@ip-location-db/asn-country/asn-country-ipv4-num.csv` 与 `.../asn-country-ipv6-num.csv`(每行 `start,end,CC`,IPv4 为 32 位十进制,IPv6 为 128 位十进制整数,已按 start 排序)。首次启动异步下载到 `data/geoip/`,每 7 天刷新;下载失败不影响启动(国家码为空直至下次成功);管理员手动覆盖优先于自动识别。
- 安全红线:Agent 不监听任何端口、无远程执行、无自更新;Master→Agent 的下行消息仅限配置类(如采集间隔),且必须在 PROTOCOL.md 中逐条列明。AgentKey 每节点唯一、随机 ≥32 字节、可在后台轮换。
- 语言:代码标识符与注释英文;UI 文案简体中文;文档简体中文。
- 时序保留:热 24h(1 分钟均值)/温 7d(1 小时均值)/冷 30d(1 天均值),>30 天删除。心跳 2s 不落库,在内存聚合为 1 分钟桶后写入。

## 3. 设计阶段必须定案的问题(逐条给出明确结论 + 理由)

1. **MessagePack + Native AOT 实现路径(最关键)**:调研 `Microsoft.AspNetCore.SignalR.Protocols.MessagePack` 10.0 与 `Microsoft.AspNetCore.SignalR.Client` 10.0 的 AOT 注解(`RequiresUnreferencedCode`/`RequiresDynamicCode`);MessagePack 2.5.302 在 AOT 下的可用路径(手写 `IMessagePackFormatter<T>` + `StaticCompositeResolver`,或 `mpc` 预生成,或其他),非泛型 `MessagePackSerializer.Serialize(Type, ...)` 路径的动态代码风险与规避(例如显式 rooting 泛型实例化);哪些警告需要 `UnconditionalSuppressMessage` 并写明理由;以及如何在 JIT 下用单元测试证明手写/生成 formatter 与 `[MessagePackObject]/[Key]` 契约**字节级等价**(Master 可用标准反射 resolver,Agent 用静态 resolver,两者互通)。
2. **心跳/注册 DTO**:字段、类型、Key 索引、单位(CPU 千分比 `ushort`,内存 MB `uint`,磁盘数组、网卡累计字节 `ulong` 等),50 字节目标的取舍与实测大小;注册 DTO(主机名、OS、CpuModel 含 `2x` 前缀、核心数、总内存、总磁盘、Agent 版本、IP 列表)与心跳 DTO 分离;IP 列表定时(如 5 分钟)单独上报。服务端打时间戳,DTO 不含 DateTime。
3. **数据模型与降采样**:EF Core 实体、SQLite WAL、启动自动迁移;`Metrics1m/1h/1d` 表结构、索引、聚合字段(均值 + 峰值)、保留策略;调度方式(`BackgroundService` + `PeriodicTimer`),各任务的时间点与幂等。
4. **流量 Delta 引擎**:计数器回绕/清零判定、首包、断线重连、多网卡聚合(排除 `lo`、`docker*`、`veth*`、`br-*`、`virbr*`、`tun/tap` 等,Agent 侧过滤 + 上报聚合值,或上报每网卡由服务端聚合——选一并说明)、账单周期(重置日 1–31,月底钳制,时区来自节点或全局设置)、`TrafficMonthly/TrafficDaily` 表。
5. **告警引擎**:规则(离线 >30s、流量 >80%、CPU >90% 持续、到期 <7 天;阈值全局可配、节点可覆盖)、连续触发阈值、冷却 30–60 分钟、恢复通知、去重键、`AlertEvent` 表与后台查看;渠道 Telegram Bot(token + chat_id,Markdown/HTML)、Webhook(URL、可选 HMAC 签名头、JSON 负载模板);渠道配置存 DB 动态生效,支持"发送测试消息"。
6. **管理端 REST API 全清单**:路径、方法、鉴权、请求/响应 JSON 示例、错误格式、分页约定;必须研究 vue-naive-admin 2.x 模板的 `src/api`、`src/store/modules`、`src/router/guards`、`src/utils/http`,决定是**服务端兼容模板期望的登录/用户/权限接口**还是**改造模板**(推荐兼容 + 少量改造,并列出改造文件清单)。
7. **实时推送形状**:`/hubs/public` 与 `/hubs/admin` 的快照 + 增量消息、节点上线/下线事件、波浪图窗口(服务端每节点内存环形缓冲最近 N 个点,新连接先推快照)、推送节流(如每 2s 批量广播)。
8. **Agent 采集与网络**:Linux `/proc/stat`、`/proc/meminfo`、`/proc/net/dev`、`/proc/mounts` + `statvfs`(或 `DriveInfo`)、`/proc/cpuinfo`(socket 数 → `2x` 前缀);Windows `GetSystemTimes`、`GlobalMemoryStatusEx`、`GetIfTable2`、`DriveInfo`、注册表 CPU 名称(用 `LibraryImport` 源生成 P/Invoke);IP 发现过滤规则(保留私网地址,剔除 127/8、::1、169.254/16、fe80::/10、未指定地址);`--proxy`(HTTP/SOCKS5:`SocketsHttpHandler.Proxy` 与 `ClientWebSocketOptions.Proxy`,说明 WebSockets/LongPolling 在代理下的行为与回退);断线重连(指数退避,永不放弃);CLI 参数(`--server`、`--key`、`--proxy`、`--interval`、`--name` 等)与环境变量;日志与 `--version`。
9. **安装脚本生成**:后台一键生成 `curl -fsSL ... | bash` 脚本:从可配置的 Release 基地址按 `uname -m` 下载对应二进制、校验、写 systemd unit(非 root 专用用户、`Restart=always`)、幂等、可 `uninstall`。
10. **测试策略**:单元(契约字节等价、Delta、降采样、防抖、账单周期、GeoIP 查找)、集成(`WebApplicationFactory` + 真实 SignalR MessagePack 客户端 + 临时 SQLite 文件)、前端构建、端到端脚本 `scripts/e2e.sh`。

## 4. 设计文档产出要求

- `docs/DESIGN.md`:总体架构、模块职责、目录、进程内组件与数据流、定时任务表、启动顺序、配置来源。
- `docs/PROTOCOL.md`:全部 DTO(字段/类型/Key/单位/含义)、Hub 路径与方法签名(客户端→服务端、服务端→客户端)、鉴权方式、连接/重连/时序图、错误处理;**附完整 C# 契约代码草案**(可直接落到 `SNM.Contracts`)。
- `docs/API.md`:REST 全清单,每个接口带 JSON 示例、错误格式、分页、鉴权;以及 vue-naive-admin 兼容接口。
- `docs/DATA.md`:实体/表/列/索引、降采样与保留任务、流量与账单表、告警表、设置表、迁移策略。
- `docs/DEPLOY.md`:配置项(appsettings + 环境变量对照表)、反代示例(Nginx / 1Panel)、systemd、CI 产物命名、升级与备份(SQLite WAL 注意事项)。
- 文档必须精确到**实现者无需再做任何接口层面的决定**;不同模块由互不通信的 agent 实现,文档是唯一契约。
