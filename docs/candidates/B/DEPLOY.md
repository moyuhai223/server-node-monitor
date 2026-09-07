# DEPLOY.md — 部署、CI、备份与升级(候选设计 B)

> 覆盖 `deploy/` 与 `scripts/` 目录的全部产物、Master 的 systemd/反代/Docker 运行方式、探针安装脚本模板、GitHub Actions、以及 SQLite WAL 的备份升级注意事项。配置项全表见 DESIGN.md §7,此处只列部署相关。

## 1. `deploy/` 目录清单

```text
deploy/
  snm-master.service              # Master systemd unit(§2.2)
  snm-agent.service.tmpl          # 探针 unit 模板(install-agent.sh 内嵌同样内容,此文件供人工安装)
  install-agent.sh                # Linux 一键安装脚本模板(§4);Master 以 /install.sh 提供
  install-agent.ps1               # Windows 安装脚本模板(§5);Master 以 /install.ps1 提供
  nginx/snm.conf                  # Nginx 反代示例(§3.1)
  docker/Dockerfile               # Master 多阶段镜像(§2.4)
  docker/docker-compose.yml       # 示例编排
  docker/entrypoint.sh            # 可选:修正 /data 权限
  .github/workflows/agent-aot.yml # 探针 Native AOT 发布(§6.1)—— 同步到仓库根 .github/workflows/
  .github/workflows/master.yml    # 构建/测试/发布 Master(§6.2)
scripts/
  env.sh dev.sh build-web.sh e2e.sh sync-workflows.sh publish-master.sh
```

GitHub 只读取仓库根 `.github/workflows/`;`deploy/.github/workflows/` 为规范源,`scripts/sync-workflows.sh` 复制到根目录,`master.yml` 中有一步 `diff -r deploy/.github/workflows .github/workflows` 保证同步。

## 2. Master 部署

### 2.1 产物

`scripts/publish-master.sh <rid>`:`scripts/build-web.sh` → `dotnet publish src/SNM.Master -c Release -r <rid> --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/master/<rid>` → `tar czf snm-master-<rid>.tar.gz`(含 `SNM.Master`、`appsettings.json`、`wwwroot/`)。RID:`linux-x64`、`linux-arm64`(JIT,自包含,无需安装 .NET)。Windows 上运行 Master 用 `win-x64` 同法(开发验证)。

### 2.2 systemd(`deploy/snm-master.service`)

```ini
[Unit]
Description=Server Node Monitor Master
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=snm
Group=snm
WorkingDirectory=/opt/snm-master
ExecStart=/opt/snm-master/SNM.Master
Restart=always
RestartSec=3
KillSignal=SIGTERM
TimeoutStopSec=20
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080
Environment=SNM_DATA_DIR=/var/lib/snm
Environment=DOTNET_gcServer=0
EnvironmentFile=-/etc/snm/master.env
# hardening
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
ReadWritePaths=/var/lib/snm
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
```

安装步骤(文档化到 README):`useradd --system --home /var/lib/snm --shell /usr/sbin/nologin snm`;解包到 `/opt/snm-master`;`mkdir -p /var/lib/snm && chown snm:snm /var/lib/snm && chmod 700 /var/lib/snm`;`/etc/snm/master.env`(0600)写 `SNM_ADMIN_USER`、`SNM_ADMIN_PASSWORD`(首次)、`SNM_JWT_SECRET`(可选)、`SNM_KNOWN_PROXIES`(如 Nginx 不在本机);`systemctl enable --now snm-master`;`journalctl -u snm-master -f` 看首次生成的管理员密码。

### 2.3 反向代理

Master 仅监听 `127.0.0.1:5080`,TLS 与公网暴露由反代负责;必须转发 `X-Forwarded-For/Proto`,否则探针公网 IP 全部显示为 127.0.0.1。WebSocket 需 `Upgrade` 头;`proxy_read_timeout` 必须大于 SignalR keep-alive(10s)与客户端超时(30s),建议 ≥120s。

**Nginx(`deploy/nginx/snm.conf`)**

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }
server {
    listen 443 ssl http2;
    server_name m.example.com;
    ssl_certificate     /etc/letsencrypt/live/m.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/m.example.com/privkey.pem;
    client_max_body_size 1m;
    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;
        proxy_buffering off;
    }
}
server { listen 80; server_name m.example.com; return 301 https://$host$request_uri; }
```

**1Panel**:网站 → 创建网站 → 反向代理 → 代理地址 `http://127.0.0.1:5080`;在“反向代理”高级/配置文件中确认包含 `proxy_set_header Upgrade $http_upgrade; proxy_set_header Connection "upgrade";`(1Panel 的反代模板默认带 WebSocket 头;若无则手工加入),把 `proxy_read_timeout` 改为 `120s`;开启 HTTPS(申请证书)。1Panel 与 Master 同机时 `SNM_KNOWN_PROXIES` 留空即可(loopback 默认信任);若 1Panel 的 OpenResty 运行在容器网络,则填容器网关 IP 或 `SNM_KNOWN_NETWORKS=172.16.0.0/12`。

**Caddy(备选)**:`m.example.com { reverse_proxy 127.0.0.1:5080 }`(Caddy 自动处理 WebSocket 与 X-Forwarded-*)。

### 2.4 Docker(`deploy/docker/Dockerfile`)

```dockerfile
# syntax=docker/dockerfile:1.7
FROM node:22-alpine AS web
WORKDIR /src/web/admin
COPY web/admin/package.json web/admin/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/admin ./
COPY web/public /src/web/public
RUN npm run build && mkdir -p /out/admin && cp -r ../../src/SNM.Master/wwwroot/admin/. /out/admin/ \
 && mkdir -p /out/public/js/vendor && cp node_modules/@microsoft/signalr/dist/browser/signalr.min.js node_modules/@microsoft/signalr-protocol-msgpack/dist/browser/signalr-protocol-msgpack.min.js /out/public/js/vendor/

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ServerNodeMonitor.sln ./
COPY src/SNM.Contracts/SNM.Contracts.csproj src/SNM.Contracts/
COPY src/SNM.Master/SNM.Master.csproj src/SNM.Master/
RUN dotnet restore src/SNM.Master/SNM.Master.csproj
COPY src ./src
COPY web/public ./web/public
COPY --from=web /out/admin ./src/SNM.Master/wwwroot/admin
COPY --from=web /out/public/js/vendor ./web/public/js/vendor
RUN dotnet publish src/SNM.Master -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://0.0.0.0:5080 SNM_DATA_DIR=/data DOTNET_gcServer=0
VOLUME /data
EXPOSE 5080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD ["dotnet","SNM.Master.dll","--health-check"]
ENTRYPOINT ["dotnet","SNM.Master.dll"]
```

- 基础镜像 Debian 版(含 tzdata,IANA 时区可用);多架构由 buildx 构建 `linux/amd64,linux/arm64`(publish 不指定 RID → 框架依赖,随基础镜像架构)。
- `--health-check` 为 Master 内置参数:HTTP GET `http://127.0.0.1:5080/healthz`,200 退出 0,否则 1。
- `docker-compose.yml`:`image: ghcr.io/OWNER/snm-master:latest`,`ports: ["127.0.0.1:5080:5080"]`,`volumes: ["./data:/data"]`,`environment: [SNM_ADMIN_USER=admin, SNM_ADMIN_PASSWORD=..., SNM_KNOWN_NETWORKS=172.16.0.0/12]`(容器内看到的是 Docker 网桥地址 → 必须把网桥网段列为受信代理),`restart: unless-stopped`。宿主机反代到 `127.0.0.1:5080`。
- 数据卷权限:`$APP_UID`(1654)需可写 `/data`;`entrypoint.sh`(以 root 起,`chown` 后 `exec setpriv`/`gosu`)为可选方案,默认要求宿主机 `chown 1654:1654 ./data`。

## 3. 开发与验证脚本(`scripts/`)

| 脚本 | 作用 |
|---|---|
| `env.sh` | 已存在;所有脚本首行 `source "$(dirname "$0")/env.sh"` |
| `build-web.sh` | `cd web/admin && npm ci && npm run build`(vite `outDir=../../src/SNM.Master/wwwroot/admin`,`emptyOutDir`)→ 复制 `node_modules/@microsoft/signalr/dist/browser/signalr.min.js` 与 `@microsoft/signalr-protocol-msgpack/dist/browser/signalr-protocol-msgpack.min.js` 到 `web/public/js/vendor/`(gitignored)→ 运行 `node web/public/check-public.mjs`(敏感字段检查) |
| `dev.sh` | 起 Master(`dotnet run --project src/SNM.Master -- --Snm:DataDir=./.dev-data --Snm:Admin:Password=admin123 --Logging:LogLevel:Default=Debug`)→ 等 `/healthz`=200 → 若 `.dev-data/agent.key` 不存在,用 API 登录并创建节点 `dev-local`,把 AgentKey 写入该文件 → 起 Agent(`dotnet run --project src/SNM.Agent -- --server http://127.0.0.1:5080 --key $(cat .dev-data/agent.key) --interval 2 --log-level debug`)→ `trap` 统一结束。可选 `--web` 同时 `npm run dev`(端口 3200,代理到 5080) |
| `e2e.sh` | DESIGN.md §12 的端到端脚本:临时 DataDir、内置 Webhook 接收器(`scripts/webhook-sink.js`,Node 22 http 模块,把请求体追加到文件)、断言用 `curl` + Node 解析 JSON(本机无 jq);退出码 0/1 |
| `sync-workflows.sh` | `cp deploy/.github/workflows/*.yml .github/workflows/` |
| `publish-master.sh <rid>` | §2.1 |
| `ilc-check.sh` | BRIEF §1 的 ILC 验证命令封装,统计 IL 警告数(期望 0),链接步骤失败忽略 |

## 4. 探针安装脚本模板(`deploy/install-agent.sh`)

Master 以 `GET /install.sh` 提供,替换占位符 `__RELEASE_BASE__`(如 `https://github.com/OWNER/REPO/releases/latest/download`,固定版本时 `.../releases/download/v1.2.0`)与 `__DEFAULT_SERVER__`。脚本无任何密钥,密钥来自 `--key`/`SNM_KEY`。

```bash
#!/usr/bin/env bash
# Server Node Monitor agent installer. Usage:
#   curl -fsSL https://MASTER/install.sh | sudo bash -s -- --server https://MASTER --key KEY [--name NAME] [--proxy URL] [--interval 2]
#   curl -fsSL https://MASTER/install.sh | sudo bash -s -- uninstall [--purge]
set -euo pipefail
RELEASE_BASE="${SNM_RELEASE_BASE:-__RELEASE_BASE__}"
SERVER="${SNM_SERVER:-__DEFAULT_SERVER__}"; KEY="${SNM_KEY:-}"; NAME="${SNM_NAME:-}"; PROXY="${SNM_PROXY:-}"; INTERVAL="${SNM_INTERVAL:-2}"
ACTION=install; PURGE=0; NO_START=0
INSTALL_DIR=/opt/snm-agent; BIN="$INSTALL_DIR/snm-agent"; ENV_DIR=/etc/snm-agent; ENV_FILE="$ENV_DIR/agent.env"
UNIT=/etc/systemd/system/snm-agent.service; SVC_USER=snm-agent
while [ $# -gt 0 ]; do case "$1" in
  uninstall) ACTION=uninstall;; --purge) PURGE=1;; --no-start) NO_START=1;;
  --server) SERVER="$2"; shift;; --key) KEY="$2"; shift;; --name) NAME="$2"; shift;;
  --proxy) PROXY="$2"; shift;; --interval) INTERVAL="$2"; shift;; --release-base) RELEASE_BASE="$2"; shift;;
  -h|--help) sed -n '2,4p' "$0"; exit 0;; *) echo "unknown argument: $1" >&2; exit 64;; esac; shift; done
[ "$(id -u)" -eq 0 ] || { echo "please run as root (sudo)" >&2; exit 1; }
command -v systemctl >/dev/null || { echo "systemd is required" >&2; exit 1; }
if [ "$ACTION" = uninstall ]; then
  systemctl disable --now snm-agent 2>/dev/null || true
  rm -f "$UNIT"; systemctl daemon-reload
  rm -rf "$INSTALL_DIR"
  if [ "$PURGE" = 1 ]; then rm -rf "$ENV_DIR"; userdel "$SVC_USER" 2>/dev/null || true; fi
  echo "snm-agent removed"; exit 0
fi
[ -n "$SERVER" ] && [ -n "$KEY" ] || { echo "--server and --key are required" >&2; exit 64; }
case "$(uname -m)" in x86_64|amd64) ARCH=linux-x64;; aarch64|arm64) ARCH=linux-arm64;; *) echo "unsupported arch: $(uname -m)" >&2; exit 1;; esac
ASSET="snm-agent-$ARCH"; TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
DL() { if command -v curl >/dev/null; then curl -fsSL --retry 3 -o "$2" "$1"; else wget -qO "$2" "$1"; fi; }
echo "downloading $RELEASE_BASE/$ASSET"
DL "$RELEASE_BASE/$ASSET" "$TMP/$ASSET"; DL "$RELEASE_BASE/$ASSET.sha256" "$TMP/$ASSET.sha256"
(cd "$TMP" && sha256sum -c "$ASSET.sha256" >/dev/null) || { echo "checksum mismatch" >&2; exit 1; }
id "$SVC_USER" >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
install -d -m 0755 "$INSTALL_DIR"; install -m 0755 -o root -g root "$TMP/$ASSET" "$BIN.new"; mv -f "$BIN.new" "$BIN"   # atomic replace
install -d -m 0700 "$ENV_DIR"
umask 077; { echo "SNM_SERVER=$SERVER"; echo "SNM_KEY=$KEY"; [ -n "$NAME" ] && echo "SNM_NAME=$NAME"; [ -n "$PROXY" ] && echo "SNM_PROXY=$PROXY"; echo "SNM_INTERVAL=$INTERVAL"; } > "$ENV_FILE"; chmod 0600 "$ENV_FILE"
cat > "$UNIT" <<'EOF'
[Unit]
Description=Server Node Monitor Agent
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
User=snm-agent
Group=snm-agent
EnvironmentFile=/etc/snm-agent/agent.env
ExecStart=/opt/snm-agent/snm-agent
Restart=always
RestartSec=5
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
ProtectKernelTunables=yes
ProtectControlGroups=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
CapabilityBoundingSet=
AmbientCapabilities=
MemoryMax=128M
LimitNOFILE=4096
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload; systemctl enable snm-agent >/dev/null
[ "$NO_START" = 1 ] || systemctl restart snm-agent
"$BIN" --version || true
echo "snm-agent installed. status: systemctl status snm-agent ; logs: journalctl -u snm-agent -f"
```

要点:幂等(重复执行=升级二进制+重写 env+重启);`EnvironmentFile` 由 systemd(root)读取后再切换到 `snm-agent` 用户,因此 0600 可行;`ProtectSystem=strict` 下探针只读 `/proc`、`/sys`、`/etc/os-release`,无需写任何路径;`AF_NETLINK` 供 .NET `NetworkInterface` 枚举;`--version` 用于安装后自检(输出 `snm-agent 1.0.0 (linux-x64)`)。人工安装可直接下载二进制并使用 `deploy/snm-agent.service.tmpl`(内容同上)。

## 5. Windows 安装脚本(`deploy/install-agent.ps1`)

参数 `-Server -Key [-Name] [-Proxy] [-Interval 2] [-Uninstall]`。行为:管理员检查;下载 `snm-agent-win-x64.exe` 与 `.sha256` 到 `%ProgramFiles%\snm-agent\`(`Get-FileHash` 校验);写 `%ProgramData%\snm-agent\agent.env`(ACL 仅 SYSTEM/Administrators/LocalService 读);注册**计划任务** `SNM Agent`:触发器 `AtStartup`,主体 `NT AUTHORITY\LocalService`(最低权限即可读性能计数与网卡表),`-ExecutionTimeLimit 0`,`RestartCount 999`/`RestartInterval 1min`,`MultipleInstances IgnoreNew`,动作 `snm-agent.exe --server ... --key ...`(参数写入任务,或用 `--env-file` 读取 agent.env —— 探针支持 `SNM_ENV_FILE`/`--env-file` 从文件加载 `KEY=VALUE`);立即 `Start-ScheduledTask`。`-Uninstall`:停任务、`Unregister-ScheduledTask`、结束进程、删目录。选择计划任务而非 Windows 服务的理由:探针不引入 `ServiceBase`/Hosting 依赖,保持体积与 AOT 简单;计划任务同样具备开机自启、失败重启与低权限运行。

## 6. GitHub Actions

### 6.1 `agent-aot.yml` — 探针 Native AOT 发布

```yaml
name: agent-aot
on:
  push:
    tags: ['v*']
  workflow_dispatch:
permissions:
  contents: write
jobs:
  build:
    strategy:
      fail-fast: false
      matrix:
        include:
          - { os: ubuntu-22.04,      rid: linux-x64,   ext: '' }
          - { os: ubuntu-22.04-arm,  rid: linux-arm64, ext: '' }     # GitHub 托管 arm64 runner(原生编译,避免交叉链接)
          - { os: windows-2022,      rid: win-x64,     ext: '.exe' }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - name: Install native toolchain (Linux)
        if: runner.os == 'Linux'
        run: sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
      - name: Publish (AOT)
        shell: bash
        run: |
          dotnet publish src/SNM.Agent -c Release -r ${{ matrix.rid }} -p:PublishAot=true -p:StripSymbols=true \
            -p:Version=${GITHUB_REF_NAME#v} -p:TrimmerSingleWarn=false -warnaserror -o out
          mv out/SNM.Agent${{ matrix.ext }} out/snm-agent-${{ matrix.rid }}${{ matrix.ext }}
          cd out && sha256sum snm-agent-${{ matrix.rid }}${{ matrix.ext }} > snm-agent-${{ matrix.rid }}${{ matrix.ext }}.sha256
      - name: Smoke test
        shell: bash
        run: out/snm-agent-${{ matrix.rid }}${{ matrix.ext }} --version && out/snm-agent-${{ matrix.rid }}${{ matrix.ext }} --dry-run
      - uses: actions/upload-artifact@v4
        with: { name: agent-${{ matrix.rid }}, path: out/snm-agent-*, if-no-files-found: error }
  release:
    needs: build
    if: startsWith(github.ref, 'refs/tags/v')
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/download-artifact@v4
        with: { path: dist, merge-multiple: true }
      - run: cp deploy/install-agent.sh deploy/install-agent.ps1 dist/ && (cd dist && sha256sum snm-agent-* install-agent.* > SHA256SUMS)
      - uses: softprops/action-gh-release@v2
        with: { files: dist/*, generate_release_notes: true }
```

要点:`-warnaserror` 使任何 IL2xxx/IL3xxx 警告导致失败(BRIEF 验收 1);`TrimmerSingleWarn=false` 展开全部警告;Windows runner 自带 MSVC 链接器;`sha256sum` 在 windows-2022 的 Git Bash 中可用。若 `ubuntu-22.04-arm` 不可用(私有仓库配额),备选交叉编译:`sudo apt-get install -y clang llvm gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu` + `-p:CppCompilerAndLinker=clang -p:SysRoot=/usr/aarch64-linux-gnu`(见 .NET 官方“Cross-compilation” 文档),需 `dotnet publish -r linux-arm64` 在 x64 runner 上执行。

### 6.2 `master.yml` — 构建、测试、发布 Master

```yaml
name: master
on:
  push: { branches: [main], tags: ['v*'] }
  pull_request:
permissions: { contents: write, packages: write }
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-node@v4
        with: { node-version: 22, cache: npm, cache-dependency-path: web/admin/package-lock.json }
      - run: diff -r deploy/.github/workflows .github/workflows          # 工作流同步检查
      - run: bash scripts/build-web.sh
      - run: dotnet build ServerNodeMonitor.sln -c Release -warnaserror
      - run: dotnet test ServerNodeMonitor.sln -c Release --no-build --logger "trx" --results-directory TestResults
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: test-results, path: TestResults }
  ilc-check:                                  # 探针 AOT 分析器 + ILC 零警告(不需要链接成功)
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
      - run: dotnet publish src/SNM.Agent -c Release -r linux-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -warnaserror -o /tmp/aot
  publish:
    needs: [test]
    if: startsWith(github.ref, 'refs/tags/v')
    runs-on: ubuntu-latest
    strategy: { matrix: { rid: [linux-x64, linux-arm64] } }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-node@v4
        with: { node-version: 22 }
      - run: bash scripts/build-web.sh
      - run: bash scripts/publish-master.sh ${{ matrix.rid }} && (cd artifacts && sha256sum snm-master-${{ matrix.rid }}.tar.gz > snm-master-${{ matrix.rid }}.tar.gz.sha256)
      - uses: softprops/action-gh-release@v2
        with: { files: artifacts/snm-master-* }
  docker:
    needs: [test]
    if: startsWith(github.ref, 'refs/tags/v') || github.ref == 'refs/heads/main'
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with: { registry: ghcr.io, username: '${{ github.actor }}', password: '${{ secrets.GITHUB_TOKEN }}' }
      - uses: docker/metadata-action@v5
        id: meta
        with: { images: ghcr.io/${{ github.repository_owner }}/snm-master, tags: 'type=semver,pattern={{version}}\ntype=raw,value=latest,enable=${{ startsWith(github.ref, ''refs/tags/v'') }}\ntype=sha' }
      - uses: docker/build-push-action@v6
        with: { context: ., file: deploy/docker/Dockerfile, platforms: 'linux/amd64,linux/arm64', push: true, tags: '${{ steps.meta.outputs.tags }}', labels: '${{ steps.meta.outputs.labels }}' }
```

### 6.3 产物命名

| 产物 | 名称 |
|---|---|
| 探针 | `snm-agent-linux-x64`、`snm-agent-linux-arm64`、`snm-agent-win-x64.exe`,各附 `.sha256`(`sha256sum` 格式:`<hex>  <filename>`) |
| 安装脚本 | `install-agent.sh`、`install-agent.ps1`(同时由 Master 动态提供) |
| 汇总 | `SHA256SUMS` |
| Master | `snm-master-linux-x64.tar.gz`、`snm-master-linux-arm64.tar.gz` + `.sha256`;镜像 `ghcr.io/OWNER/snm-master:{version|latest|sha-xxxx}` |
| 版本 | Git tag `vX.Y.Z` → `-p:Version=X.Y.Z`;`Directory.Build.props` 默认 `0.1.0-dev`;探针 `--version` 与 Master `/api/system/info.version` 输出 `InformationalVersion` |

`install.releaseBaseUrl` 默认指向 `releases/latest/download`(GitHub 对该路径提供最新 Release 的资产重定向);固定版本时设置 `install.agentVersion=v1.2.0` → 基地址变为 `releases/download/v1.2.0`。

## 7. 升级与备份(SQLite WAL)

### 7.1 备份

| 方式 | 命令 | 说明 |
|---|---|---|
| 在线热备(推荐) | `POST /api/system/backup` 或每日 04:30 UTC 自动 → `{DataDir}/backup/snm-YYYYMMDD-HHmm.db` | `VACUUM INTO` 生成一致快照(单文件,含 WAL 中已提交数据),保留 7 份;外部再用 rsync/rclone 拉走 |
| 命令行热备 | `sqlite3 /var/lib/snm/snm.db ".backup /tmp/snm.db"` | 需要 sqlite3 CLI;同样一致 |
| 冷备 | `systemctl stop snm-master && cp -a /var/lib/snm /backup/` | 必须同时包含 `snm.db`、`snm.db-wal`、`snm.db-shm`(WAL 里可能有未 checkpoint 的数据);单独拷 `snm.db` 会丢最近写入 |
| 禁止 | 运行中直接 `cp snm.db` | 可能得到不一致文件 |

恢复:停服务 → 把备份文件放为 `snm.db`,**删除**残留的 `snm.db-wal/-shm` → 启动(迁移会自动补齐结构差异,仅允许向新版本恢复旧备份)。`jwt.key`、`geoip/` 一并备份可避免全员重新登录与 GeoIP 重下(非必需)。

### 7.2 升级

1. Master:下载新包 → `systemctl stop snm-master`(优雅关闭会 flush 最后一分钟数据)→ 先热备 → 替换 `/opt/snm-master` 内容(保留 `/etc/snm/master.env` 与 `/var/lib/snm`)→ `systemctl start` → 启动日志出现 `Applied migrations: ...` → `/api/system/info.version` 确认。迁移只前进不回退;回退需用升级前的备份。
2. Docker:`docker compose pull && docker compose up -d`(卷不变)。
3. 探针:重跑同一条安装命令(幂等升级),或 `systemctl stop snm-agent && curl -fsSL .../snm-agent-linux-x64 -o /opt/snm-agent/snm-agent && systemctl start snm-agent`。协议前向兼容:新增 DTO 字段只追加 Key,旧探针缺字段取默认;旧 Master 遇到新字段跳过。
4. 配置变更:`Snm:Retention:*` 调小立即生效(下个整点删除);调大不追溯。

### 7.3 运行注意

- `{DataDir}` 放在本地磁盘(WAL 不适合 NFS/SMB)。
- WAL 文件正常在几 MB 内;每日 checkpoint 截断;若持续增大说明有长事务(检查 `/api/system/info.walSizeBytes`)。
- 磁盘剩余 < 200 MB 时 Master 日志 Warning(`MaintenanceService` 检查),< 50 MB 时停止写入 Metrics(仍保留流量与告警写入)。
- 时区:容器/主机时区不影响逻辑(全部 UTC + IANA 转换),但 `tzdata` 必须存在(Debian 镜像已含;Alpine 需 `apk add tzdata`,本设计不用 Alpine)。

