> 本文件是最终采纳的设计(候选方案 A),已按实现落地;与实现的差异见 docs/IMPLEMENTATION_NOTES.md。

# DEPLOY — 部署、安装脚本、CI、配置对照、升级与备份(候选方案 A)

## 一键安装(已实现,以此为准)

| 脚本 | 用途 | 命令 |
|---|---|---|
| `deploy/install-master.sh` | 服务端:下载 Release 包 → 校验 sha256 → 创建 `snm-master` 用户 → `/opt/snm-master` + `/var/lib/snm-master` + `/etc/snm-master/master.env` → systemd → 等待 `/healthz`;可选 `--nginx DOMAIN` 写反代站点;`upgrade` 自动备份数据库;`uninstall [--purge]`;`status` | `curl -fsSL https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-master.sh \| sudo bash -s -- --public-url https://m.example.com` |
| `deploy/install-agent.sh` | 探针(Linux):既是 Master 在 `/install/<token>` 渲染的模板(`{{SERVER_URL}}` / `{{AGENT_KEY}}` / `{{RELEASE_BASE_URL}}` 被填入),也可独立运行,参数/环境变量优先级高于模板值 | `curl -fsSL .../deploy/install-agent.sh \| sudo bash -s -- --server https://m.example.com --key snmk_xxx [--proxy socks5://...]` |
| `deploy/install-agent.ps1` | 探针(Windows x64):同上,值来自 `$env:SNM_SERVER` / `$env:SNM_KEY`(或 Master 渲染) | `$env:SNM_SERVER='https://m.example.com'; $env:SNM_KEY='snmk_xxx'; irm .../deploy/install-agent.ps1 \| iex` |

单文件自包含的 Master 需要一个可写目录解压原生库,安装脚本在 unit 里设置 `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/snm-master/.net`(与 `ProtectSystem=strict` + `ReadWritePaths` 配合)。以下为设计文档原文。

---

## 0. 定案(BRIEF §3 Q9 与 CI 产物)

| 项 | 定案 |
|---|---|
| Agent 产物 | `snm-agent-linux-x64.tar.gz`、`snm-agent-linux-arm64.tar.gz`、`snm-agent-win-x64.zip`,各附 `.sha256`(`sha256sum` 格式一行);GitHub Release 资产;`latest` 可用 `https://github.com/moyuhai223/server-node-monitor/releases/latest/download/<asset>` |
| Master 产物 | `snm-master-linux-x64.tar.gz`、`snm-master-linux-arm64.tar.gz`(自包含单文件 JIT,含 `wwwroot/`)+ 容器镜像 `ghcr.io/moyuhai223/snm-master:<version>` / `:latest`(linux/amd64 + arm64) |
| 安装脚本 | 后台按节点渲染 `deploy/install-agent.sh.tmpl`,通过 `GET /install/{token}` 分发;幂等(重复执行 = 升级/修复);`uninstall` 子命令;专用系统用户 `snm-agent`;`uname -m` 选择架构;sha256 校验;systemd `Restart=always` |
| Windows Agent | `snm-agent.exe` + 计划任务(开机启动、SYSTEM 或专用账户),脚本 `install-agent.ps1.tmpl`(`irm … \| iex`);不做 Windows 服务宿主(避免 Hosting 依赖) |
| 反代 | Nginx 或 1Panel(OpenResty)终止 TLS,转发到 `127.0.0.1:5080`,必须转发 WebSocket 升级头与 `X-Forwarded-*` |

---

## 1. 产物命名与目录

| 产物 | 内容 | 安装位置 |
|---|---|---|
| `snm-agent-<rid>.tar.gz` | 单文件 `snm-agent`(可执行,`chmod 755`) | `/opt/snm-agent/snm-agent` |
| `snm-agent-win-x64.zip` | `snm-agent.exe` | `C:\Program Files\snm-agent\snm-agent.exe` |
| `snm-master-<rid>.tar.gz` | `SNM.Master`(自包含单文件)、`appsettings.json`、`wwwroot/`、`LICENSE`、`THIRD-PARTY-NOTICES.md` | `/opt/snm-master/` |
| 镜像 `ghcr.io/moyuhai223/snm-master` | 同上,入口 `dotnet SNM.Master.dll`,`/data` 卷 | Docker |

版本号来源:Git tag `vX.Y.Z` → `-p:Version=X.Y.Z`;`InformationalVersion` 附加 commit sha(SourceLink);Agent `--version` 与 Master `/api/system/info.version` 输出一致。

---

## 2. Master 部署(systemd + 反代)

### 2.1 目录与用户

```bash
sudo useradd --system --home /var/lib/snm-master --shell /usr/sbin/nologin snm-master
sudo mkdir -p /opt/snm-master /var/lib/snm-master /etc/snm-master
sudo tar xzf snm-master-linux-x64.tar.gz -C /opt/snm-master
sudo chown -R root:root /opt/snm-master && sudo chmod 755 /opt/snm-master/SNM.Master
sudo chown -R snm-master:snm-master /var/lib/snm-master
```

`/etc/snm-master/master.env`(`chmod 600 root:root`):

```bash
SNM_LISTEN=http://127.0.0.1:5080
SNM_DATA_DIR=/var/lib/snm-master
SNM_PUBLIC_BASE_URL=https://m.example.com
SNM_KNOWN_PROXIES=127.0.0.1,::1
SNM_ADMIN_USER=admin
SNM_ADMIN_PASSWORD=ChangeMe-Now-2026!
# SNM_JWT_SECRET=<64+ 随机字符,不设则自动生成并存库>
# SNM_TIMEZONE=Asia/Shanghai
# SNM_LOG_LEVEL=Information
ASPNETCORE_ENVIRONMENT=Production
DOTNET_gcServer=0
DOTNET_GCHeapHardLimit=0x20000000
```

`SNM_ADMIN_PASSWORD` 仅首次建库使用;建库后建议从文件中删除。

### 2.2 `deploy/systemd/snm-master.service`

```ini
[Unit]
Description=Server Node Monitor Master
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=snm-master
Group=snm-master
WorkingDirectory=/opt/snm-master
EnvironmentFile=/etc/snm-master/master.env
ExecStart=/opt/snm-master/SNM.Master
Restart=always
RestartSec=3
KillSignal=SIGTERM
TimeoutStopSec=30
LimitNOFILE=65536
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/snm-master
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictSUIDSGID=true

[Install]
WantedBy=multi-user.target
```

```bash
sudo cp deploy/systemd/snm-master.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now snm-master
sudo journalctl -u snm-master -f      # 首次启动会打印随机管理员密码(若未设置 SNM_ADMIN_PASSWORD)
```

### 2.3 Nginx(`deploy/nginx/snm.conf`)

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

server {
    listen 80;
    server_name m.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name m.example.com;

    ssl_certificate     /etc/letsencrypt/live/m.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/m.example.com/privkey.pem;

    client_max_body_size 1m;
    gzip on; gzip_types text/plain text/css application/javascript application/json image/svg+xml;

    # SignalR hubs: WebSocket upgrade + long timeouts (keep-alive ping every 15 s)
    location /hubs/ {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;
        proxy_buffering off;
    }

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_read_timeout 60s;
    }
}
```

- Master 只信任 `KnownProxies`(默认 loopback)发来的 `X-Forwarded-For`,因此探针公网 IP = Nginx 看到的 `$remote_addr`。
- 若前面还有 Cloudflare:在 Nginx 中改为 `proxy_set_header X-Forwarded-For $http_cf_connecting_ip;`(并按 Cloudflare 官方列表限制来源),Master 无需改动;或者设置 `SNM_FORWARD_LIMIT=2` 并把 Cloudflare 网段加入 `SNM_KNOWN_NETWORKS`。

### 2.4 1Panel 说明

1. 网站 → 创建网站 → 反向代理,域名 `m.example.com`,代理地址 `http://127.0.0.1:5080`。
2. 该网站 → 反向代理 → 编辑 `/` 规则:开启 **WebSocket** 支持(1Panel 会写入 `Upgrade/Connection` 头);若版本无此开关,手动在配置中加入 §2.3 `location /hubs/` 块。
3. 证书:1Panel 申请 Let's Encrypt 并开启强制 HTTPS。
4. 1Panel 的 OpenResty 与 Master 同机 → `SNM_KNOWN_PROXIES` 保持默认;若 OpenResty 在 Docker 网络中,把其网段填入 `SNM_KNOWN_NETWORKS=172.16.0.0/12`。
5. 验证:浏览器打开 `https://m.example.com/admin/`;`curl -sI https://m.example.com/hubs/public/negotiate -X POST` 应返回 200 JSON;后台“系统信息”页可见 `hubs.publicClients`。

### 2.5 Docker

`deploy/docker/Dockerfile`:

```dockerfile
# ---- web assets ----
FROM node:22-alpine AS web
WORKDIR /src
COPY web/admin/package.json web/admin/package-lock.json web/admin/
COPY web/public/package.json web/public/package-lock.json web/public/
RUN cd web/admin && npm ci --no-audit --no-fund && cd ../public && npm ci --no-audit --no-fund
COPY web/ web/
COPY scripts/build-web.sh scripts/
RUN bash scripts/build-web.sh --out /out/wwwroot --skip-install

# ---- master build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SNM.Contracts/SNM.Contracts.csproj src/SNM.Contracts/
COPY src/SNM.Master/SNM.Master.csproj src/SNM.Master/
RUN dotnet restore src/SNM.Master/SNM.Master.csproj
COPY src/ src/
COPY --from=web /out/wwwroot src/SNM.Master/wwwroot
ARG VERSION=1.0.0
RUN dotnet publish src/SNM.Master/SNM.Master.csproj -c Release -o /app --no-restore -p:Version=$VERSION

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV SNM_LISTEN=http://0.0.0.0:5080 \
    SNM_DATA_DIR=/data \
    SNM_KNOWN_NETWORKS=172.16.0.0/12,10.0.0.0/8,192.168.0.0/16 \
    ASPNETCORE_ENVIRONMENT=Production
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
VOLUME /data
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["dotnet", "SNM.Master.dll"]
```

`deploy/docker/docker-compose.yml`:

```yaml
services:
  snm-master:
    image: ghcr.io/moyuhai223/snm-master:1.0.0
    container_name: snm-master
    restart: unless-stopped
    ports:
      - "127.0.0.1:5080:5080"        # 仍由宿主机 Nginx/1Panel 反代
    environment:
      SNM_ADMIN_USER: admin
      SNM_ADMIN_PASSWORD: ChangeMe-Now-2026!
      SNM_PUBLIC_BASE_URL: https://m.example.com
      SNM_TIMEZONE: Asia/Shanghai
    volumes:
      - ./data:/data                 # 宿主机目录需 chown 1654:1654(镜像内 app 用户)
```

---

## 3. Agent 手工安装(脚本之外的参考)

```bash
curl -fsSLO https://github.com/moyuhai223/server-node-monitor/releases/latest/download/snm-agent-linux-x64.tar.gz
curl -fsSLO https://github.com/moyuhai223/server-node-monitor/releases/latest/download/snm-agent-linux-x64.tar.gz.sha256
sha256sum -c snm-agent-linux-x64.tar.gz.sha256
sudo mkdir -p /opt/snm-agent && sudo tar xzf snm-agent-linux-x64.tar.gz -C /opt/snm-agent
/opt/snm-agent/snm-agent --version
/opt/snm-agent/snm-agent test                      # 本机采集自检,不联网
SNM_SERVER=https://m.example.com SNM_KEY=snmk_... /opt/snm-agent/snm-agent run
```

运行依赖:glibc ≥ 2.27(Ubuntu 18.04+/Debian 10+/CentOS 8+/Rocky/Alma 8+),`libssl`(1.1 或 3.x)与 `zlib`。Alpine/musl 不支持(需另行以 `linux-musl-x64` RID 发布,本版本未包含;脚本检测到 musl 时报错退出)。

---

## 4. 安装脚本模板 `deploy/install-agent.sh.tmpl`

Master 渲染规则(`InstallScriptService`):占位符 `{{SERVER_URL}}`(`site.publicBaseUrl` 或推导)、`{{AGENT_KEY}}`、`{{RELEASE_BASE_URL}}`(`agent.releaseBaseUrl`)、`{{NODE_NAME}}`(PublicName,仅注释)、`{{GENERATED_AT}}`、`{{MASTER_VERSION}}`。所有值经白名单校验(URL `^https?://[A-Za-z0-9.\-:/_]+$`,Key `^snmk_[A-Za-z0-9_-]{43}$`),渲染为单引号包裹的 bash 字面量。脚本用法:

```
curl -fsSL https://m.example.com/install/<token> | sudo bash                          # 安装/升级
curl -fsSL https://m.example.com/install/<token> | sudo bash -s -- --proxy socks5://10.0.0.1:1080
curl -fsSL https://m.example.com/install/<token> | sudo bash -s -- uninstall
```

```bash
#!/usr/bin/env bash
# Server Node Monitor agent installer
# Generated {{GENERATED_AT}} by master {{MASTER_VERSION}} for node "{{NODE_NAME}}"
# Usage: install.sh [--proxy URL] [--net-if a,b] [--version vX.Y.Z] | uninstall
set -euo pipefail

SNM_SERVER='{{SERVER_URL}}'
SNM_KEY='{{AGENT_KEY}}'
RELEASE_BASE='{{RELEASE_BASE_URL}}'

INSTALL_DIR=/opt/snm-agent
CONF_DIR=/etc/snm-agent
ENV_FILE=$CONF_DIR/agent.env
UNIT_FILE=/etc/systemd/system/snm-agent.service
SERVICE=snm-agent
SVC_USER=snm-agent
DRY_RUN=${SNM_DRY_RUN:-0}

log()  { printf '\033[1;32m[snm]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[snm]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[snm] ERROR:\033[0m %s\n' "$*" >&2; exit 1; }
run()  { if [ "$DRY_RUN" = 1 ]; then echo "+ $*"; else "$@"; fi; }

ACTION=install; PROXY=""; NET_IF=""; PIN_VERSION=""
while [ $# -gt 0 ]; do
  case "$1" in
    uninstall) ACTION=uninstall ;;
    --proxy) PROXY="$2"; shift ;;
    --net-if) NET_IF="$2"; shift ;;
    --version) PIN_VERSION="$2"; shift ;;
    *) die "unknown argument: $1" ;;
  esac; shift
done

[ "$(id -u)" -eq 0 ] || die "please run as root (sudo)"
command -v systemctl >/dev/null 2>&1 || die "systemd is required"
[ "$(uname -s)" = Linux ] || die "this installer supports Linux only; see docs for Windows"

if [ "$ACTION" = uninstall ]; then
  log "stopping and removing $SERVICE"
  run systemctl disable --now "$SERVICE" 2>/dev/null || true
  run rm -f "$UNIT_FILE"; run systemctl daemon-reload
  run rm -rf "$INSTALL_DIR" "$CONF_DIR"
  if id "$SVC_USER" >/dev/null 2>&1; then run userdel "$SVC_USER" || true; fi
  log "uninstalled"; exit 0
fi

# --- architecture / libc ---
case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) die "unsupported architecture: $(uname -m)" ;;
esac
if ldd --version 2>&1 | grep -qi musl || [ -f /etc/alpine-release ]; then
  die "musl-based systems (Alpine) are not supported by this build"
fi
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required (coreutils)"
if command -v curl >/dev/null 2>&1; then DL="curl -fsSL --retry 3 --connect-timeout 15 -o"; elif command -v wget >/dev/null 2>&1; then DL="wget -q --tries=3 --timeout=15 -O"; else die "curl or wget is required"; fi
# reuse --proxy for the download itself when it is an http(s) proxy
case "$PROXY" in http://*|https://*) export https_proxy="$PROXY" http_proxy="$PROXY" ;; socks5://*|socks4*://*) export ALL_PROXY="$PROXY" ;; esac

ASSET="snm-agent-$RID.tar.gz"
if [ -n "$PIN_VERSION" ]; then BASE="${RELEASE_BASE%/latest/download}/download/$PIN_VERSION"; else BASE="$RELEASE_BASE"; fi
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
log "downloading $BASE/$ASSET"
$DL "$TMP/$ASSET" "$BASE/$ASSET"
$DL "$TMP/$ASSET.sha256" "$BASE/$ASSET.sha256"
( cd "$TMP" && sha256sum -c "$ASSET.sha256" >/dev/null ) || die "checksum verification failed"
tar xzf "$TMP/$ASSET" -C "$TMP"
[ -x "$TMP/snm-agent" ] || chmod 755 "$TMP/snm-agent"
NEW_VER=$("$TMP/snm-agent" --version 2>/dev/null || echo unknown)

# --- user, dirs, binary (idempotent) ---
id "$SVC_USER" >/dev/null 2>&1 || run useradd --system --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
run mkdir -p "$INSTALL_DIR" "$CONF_DIR"
if [ -x "$INSTALL_DIR/snm-agent" ] && cmp -s "$INSTALL_DIR/snm-agent" "$TMP/snm-agent"; then
  log "binary unchanged ($NEW_VER)"
else
  run install -m 755 -o root -g root "$TMP/snm-agent" "$INSTALL_DIR/snm-agent.new"
  run mv -f "$INSTALL_DIR/snm-agent.new" "$INSTALL_DIR/snm-agent"
  log "installed binary $NEW_VER"
fi

# --- environment file (0600 root; read by systemd before dropping privileges) ---
{
  echo "SNM_SERVER=$SNM_SERVER"
  echo "SNM_KEY=$SNM_KEY"
  [ -n "$PROXY" ] && echo "SNM_PROXY=$PROXY"
  [ -n "$NET_IF" ] && echo "SNM_NET_IF=$NET_IF"
  echo "SNM_LOG_LEVEL=info"
} > "$TMP/agent.env"
run install -m 600 -o root -g root "$TMP/agent.env" "$ENV_FILE"

# --- systemd unit ---
cat > "$TMP/unit" <<'UNIT'
[Unit]
Description=Server Node Monitor Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=snm-agent
Group=snm-agent
EnvironmentFile=/etc/snm-agent/agent.env
ExecStart=/opt/snm-agent/snm-agent run
Restart=always
RestartSec=5
LimitNOFILE=65536
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
CapabilityBoundingSet=
AmbientCapabilities=

[Install]
WantedBy=multi-user.target
UNIT
if ! cmp -s "$TMP/unit" "$UNIT_FILE" 2>/dev/null; then run install -m 644 "$TMP/unit" "$UNIT_FILE"; run systemctl daemon-reload; fi

run systemctl enable "$SERVICE" >/dev/null 2>&1 || true
run systemctl restart "$SERVICE"
sleep 2
if systemctl is-active --quiet "$SERVICE"; then
  log "snm-agent is running ($NEW_VER). Logs: journalctl -u $SERVICE -f"
else
  warn "service is not active; last log lines:"; journalctl -u "$SERVICE" -n 20 --no-pager || true; exit 1
fi
```

要点:重复执行 = 升级(二进制不同才替换)+ 重写配置 + 重启;`SNM_DRY_RUN=1` 只打印将执行的命令(CI 语法/流程测试);Key 只落在 `0600 root` 的 env 文件,运行用户无权读取该文件(systemd 以 root 读取后注入进程环境)。

### 4.1 Windows 模板 `deploy/install-agent.ps1.tmpl`(`irm https://m.example.com/install/<token>?os=windows | iex`)

流程:要求管理员权限 → 下载 `snm-agent-win-x64.zip` + `.sha256` 到 `%TEMP%` → `Get-FileHash -Algorithm SHA256` 校验 → 解压到 `C:\Program Files\snm-agent\` → 写 `C:\ProgramData\snm-agent\agent.env`(ACL 仅 SYSTEM/Administrators)→ 注册计划任务 `SNM Agent`(`schtasks /Create /TN "SNM Agent" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR "\"C:\Program Files\snm-agent\snm-agent.exe\" run --server ... --key ..."`,失败后 1 分钟重启由 `/RI` 与任务设置保证)→ `schtasks /Run`。`uninstall` 参数:`schtasks /Delete /F`、删除目录。命令行参数可见于任务定义,因此 Windows 上建议使用 env 文件:任务改为运行 `cmd /c "set /p ... "`?——为简单可靠,脚本直接把 `--server/--key` 放在任务命令行(仅管理员可读任务定义),并在文档中说明。

---

## 5. GitHub Actions

### 5.1 `deploy/.github/workflows/agent-aot.yml`(仓库根 `.github/workflows/` 由 `scripts/sync-workflows.sh` 复制,或直接放置于根;两处内容一致)

```yaml
name: agent-aot

on:
  push:
    branches: [ main ]
    tags: [ 'v*' ]
    paths: [ 'src/SNM.Agent/**', 'src/SNM.Contracts/**', 'Directory.*.props', 'global.json', '.github/workflows/agent-aot.yml' ]
  pull_request:
    paths: [ 'src/SNM.Agent/**', 'src/SNM.Contracts/**', 'Directory.*.props', 'global.json' ]
  workflow_dispatch:

permissions:
  contents: read

jobs:
  publish:
    name: publish ${{ matrix.rid }}
    strategy:
      fail-fast: false
      matrix:
        include:
          - { os: ubuntu-22.04,     rid: linux-x64,   ext: tar.gz }
          - { os: ubuntu-22.04-arm, rid: linux-arm64, ext: tar.gz }
          - { os: windows-2022,     rid: win-x64,     ext: zip }
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Install native toolchain (Linux)
        if: runner.os == 'Linux'
        run: sudo apt-get update && sudo apt-get install -y --no-install-recommends clang zlib1g-dev

      - name: Resolve version
        id: ver
        shell: bash
        run: |
          if [[ "$GITHUB_REF" == refs/tags/v* ]]; then echo "version=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"; else echo "version=0.0.0-ci.${GITHUB_RUN_NUMBER}" >> "$GITHUB_OUTPUT"; fi

      - name: Publish Native AOT
        shell: bash
        run: |
          set -o pipefail
          dotnet publish src/SNM.Agent/SNM.Agent.csproj -c Release -r ${{ matrix.rid }} -o out/${{ matrix.rid }} \
            -p:PublishAot=true -p:TrimmerSingleWarn=false -p:Version=${{ steps.ver.outputs.version }} \
            2>&1 | tee publish.log
          if grep -E 'warning IL[0-9]{4}' publish.log; then echo '::error::IL trim/AOT warnings found'; exit 1; fi

      - name: Smoke test
        shell: bash
        run: |
          BIN=$(ls out/${{ matrix.rid }}/snm-agent*); "$BIN" --version
          if [ "${{ runner.os }}" != "Windows" ]; then "$BIN" test; fi
          ls -la out/${{ matrix.rid }}

      - name: Package
        shell: bash
        run: |
          cd out/${{ matrix.rid }}
          rm -f *.pdb *.dbg *.dSYM
          if [ "${{ matrix.ext }}" = "zip" ]; then 7z a -tzip ../snm-agent-${{ matrix.rid }}.zip snm-agent.exe >/dev/null
          else tar czf ../snm-agent-${{ matrix.rid }}.tar.gz snm-agent; fi
          cd ..
          sha256sum snm-agent-${{ matrix.rid }}.${{ matrix.ext }} > snm-agent-${{ matrix.rid }}.${{ matrix.ext }}.sha256
          cat snm-agent-${{ matrix.rid }}.${{ matrix.ext }}.sha256

      - uses: actions/upload-artifact@v4
        with:
          name: snm-agent-${{ matrix.rid }}
          path: out/snm-agent-${{ matrix.rid }}.*
          if-no-files-found: error

  release:
    if: startsWith(github.ref, 'refs/tags/v')
    needs: publish
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      - uses: actions/download-artifact@v4
        with:
          path: dist
          merge-multiple: true
      - uses: softprops/action-gh-release@v2
        with:
          files: dist/*
          generate_release_notes: true
```

说明:`ubuntu-22.04-arm` 为 GitHub 托管 arm64 runner(公共仓库免费);若不可用,可改为在 `ubuntu-22.04` 上交叉编译(`sudo apt-get install -y clang llvm gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu` + `-r linux-arm64 -p:SysRoot=...`),但本方案默认原生 runner。Windows runner 自带 MSVC 链接器。`sha256sum`/`tar`/`7z` 在三种 runner 的 bash 中均可用。

### 5.2 `deploy/.github/workflows/master-build.yml`

```yaml
name: master-build

on:
  push:
    branches: [ main ]
    tags: [ 'v*' ]
  pull_request:
  workflow_dispatch:

permissions:
  contents: read
  packages: write

jobs:
  test:
    runs-on: ubuntu-22.04
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-node@v4
        with: { node-version: 22, cache: npm, cache-dependency-path: web/admin/package-lock.json }
      - name: Build web assets
        run: bash scripts/build-web.sh
      - name: Build solution
        run: dotnet build ServerNodeMonitor.sln -c Release
      - name: Test
        run: dotnet test ServerNodeMonitor.sln -c Release --no-build --logger "trx;LogFileName=results.trx" --results-directory TestResults
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: test-results, path: TestResults }
      - name: Vulnerable packages (informational)
        run: dotnet list ServerNodeMonitor.sln package --vulnerable --include-transitive || true

  publish:
    if: startsWith(github.ref, 'refs/tags/v')
    needs: test
    runs-on: ubuntu-22.04
    strategy:
      matrix:
        rid: [ linux-x64, linux-arm64 ]
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-node@v4
        with: { node-version: 22 }
      - run: bash scripts/build-web.sh
      - name: Publish master
        run: |
          VERSION=${GITHUB_REF_NAME#v}
          dotnet publish src/SNM.Master/SNM.Master.csproj -c Release -r ${{ matrix.rid }} --self-contained true \
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$VERSION -o out/${{ matrix.rid }}
          cp LICENSE out/${{ matrix.rid }}/ 2>/dev/null || true
          tar czf snm-master-${{ matrix.rid }}.tar.gz -C out/${{ matrix.rid }} .
          sha256sum snm-master-${{ matrix.rid }}.tar.gz > snm-master-${{ matrix.rid }}.tar.gz.sha256
      - uses: actions/upload-artifact@v4
        with: { name: snm-master-${{ matrix.rid }}, path: snm-master-${{ matrix.rid }}.tar.gz* }

  docker:
    if: startsWith(github.ref, 'refs/tags/v')
    needs: test
    runs-on: ubuntu-22.04
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with: { registry: ghcr.io, username: "${{ github.actor }}", password: "${{ secrets.GITHUB_TOKEN }}" }
      - uses: docker/build-push-action@v6
        with:
          context: .
          file: deploy/docker/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          build-args: VERSION=${{ github.ref_name }}
          tags: |
            ghcr.io/${{ github.repository_owner }}/snm-master:${{ github.ref_name }}
            ghcr.io/${{ github.repository_owner }}/snm-master:latest

  release:
    if: startsWith(github.ref, 'refs/tags/v')
    needs: [ publish, docker ]
    runs-on: ubuntu-latest
    permissions: { contents: write }
    steps:
      - uses: actions/download-artifact@v4
        with: { path: dist, merge-multiple: true, pattern: snm-master-* }
      - uses: softprops/action-gh-release@v2
        with: { files: dist/* }
```

发布流程:`git tag v1.0.0 && git push --tags` → 两个 workflow 分别把 Agent 与 Master 资产附加到同一个 Release(`action-gh-release` 对已存在的 Release 追加文件)。

### 5.3 本机等价检查(无法运行 Actions 时)

- `scripts/ilc-check.sh`:对 `src/SNM.Agent` 执行 BRIEF 的 ILC 命令(`-r win-arm64 -p:IlcUseEnvironmentalTools=true -p:TrimmerSingleWarn=false`),统计 `warning IL` 为 0 且日志含 `Generating native code` 视为通过(`link` 失败忽略)。
- `bash -n deploy/install-agent.sh.tmpl`(占位符在单引号内,语法可检);`SNM_DRY_RUN=1 bash rendered.sh` 流程演练。
- workflow YAML:`node -e "const y=require('yaml');y.parse(require('fs').readFileSync(process.argv[1],'utf8'))" file.yml`(`yaml` 包在 `web/admin` 的依赖树中已有)。

---

## 6. 配置对照表(与 DESIGN.md §6 相同,便于运维单独查阅)

| 环境变量 | appsettings 键 | 默认 | 用途 |
|---|---|---|---|
| `SNM_LISTEN` | `Snm:Listen` | `http://127.0.0.1:5080` | 监听地址 |
| `SNM_DATA_DIR` | `Snm:DataDir` | `./data` | 数据目录 |
| `SNM_PUBLIC_BASE_URL` | `Snm:PublicBaseUrl` | 空 | 安装脚本 URL 前缀(首启写入设置) |
| `SNM_KNOWN_PROXIES` | `Snm:KnownProxies` | `127.0.0.1,::1` | 反代 IP |
| `SNM_KNOWN_NETWORKS` | `Snm:KnownNetworks` | 空 | 反代网段(CIDR) |
| `SNM_FORWARD_LIMIT` | `Snm:ForwardLimit` | 1 | 代理层数 |
| `SNM_TIMEZONE` | `Snm:TimeZone` | 服务器本地 | 站点时区(首启写入设置) |
| `SNM_ADMIN_USER` / `SNM_ADMIN_PASSWORD` | `Snm:Admin:User/Password` | `admin` / 随机 | 首次初始化 |
| `SNM_JWT_SECRET` | `Snm:Jwt:Secret` | 自动生成 | JWT 签名密钥 |
| `SNM_GEOIP_ENABLED` / `SNM_GEOIP_BASE_URL` | `Snm:GeoIp:*` | `true` / jsDelivr | GeoIP |
| `SNM_LOG_LEVEL` | `Logging:LogLevel:Default` | `Information` | 日志级别 |
| `ASPNETCORE_ENVIRONMENT` | — | `Production` | 环境 |

Agent 侧环境变量见 PROTOCOL.md §7.2(`SNM_SERVER`、`SNM_KEY`、`SNM_PROXY`、`SNM_INTERVAL`、`SNM_NAME`、`SNM_NET_IF`、`SNM_DISK_INCLUDE`、`SNM_TRANSPORT`、`SNM_INSECURE`、`SNM_LOG_LEVEL`)。

---

## 7. 升级与备份(SQLite WAL 注意事项)

### 7.1 备份

- **在线一致备份(推荐)**:`sqlite3 /var/lib/snm-master/snm.db ".backup '/var/lib/snm-master/backups/snm-$(date +%F-%H%M).db'"` 或 `sqlite3 snm.db "VACUUM INTO '/var/lib/snm-master/backups/snm-$(date +%F).db'"`(SQLite ≥ 3.27)。两者在 WAL 模式下都能得到一致快照,不需要停服。
- **文件级备份**:必须先 `systemctl stop snm-master`,再复制 `snm.db`、`snm.db-wal`、`snm.db-shm` 三个文件(或先执行 `sqlite3 snm.db "PRAGMA wal_checkpoint(TRUNCATE);"` 再只复制 `snm.db`)。**只复制运行中的 `snm.db` 会丢失 WAL 中尚未检查点的最近数据,甚至得到不一致文件。**
- 定时任务示例(每日 03:40,保留 14 份):
  ```bash
  0 3 * * *  sqlite3 /var/lib/snm-master/snm.db "VACUUM INTO '/var/lib/snm-master/backups/snm-$(date +\%F).db'" && find /var/lib/snm-master/backups -name 'snm-*.db' -mtime +14 -delete
  ```
- `geoip/` 目录可不备份(可重新下载);`Settings` 中的 `auth.jwtSecret` 随库备份,恢复后旧登录态仍有效。
- Docker:`docker exec snm-master sqlite3 …` 镜像内无 sqlite3 CLI → 在宿主机对卷目录执行(宿主机安装 `sqlite3`),或 `docker run --rm -v $(pwd)/data:/data alpine/sqlite3 /data/snm.db "VACUUM INTO '/data/backups/snm.db'"`。

### 7.2 恢复

1. `systemctl stop snm-master`。
2. 备份当前 `snm.db*`,删除 `snm.db-wal`、`snm.db-shm`(避免与恢复文件不匹配)。
3. 复制备份为 `snm.db`,`chown snm-master:snm-master`。
4. `systemctl start snm-master`;启动会执行 `quick_check` 与前向迁移。**不支持降级到旧版本**(迁移不可逆);降级前必须用升级前的备份。

### 7.3 升级 Master

```bash
sudo systemctl stop snm-master
sqlite3 /var/lib/snm-master/snm.db "VACUUM INTO '/var/lib/snm-master/backups/pre-upgrade-$(date +%F).db'"
sudo tar xzf snm-master-linux-x64.tar.gz -C /opt/snm-master        # 覆盖二进制与 wwwroot;appsettings.json 若被覆盖,配置在 env 文件不受影响
sudo systemctl start snm-master
journalctl -u snm-master -n 50 --no-pager | grep -E 'Applied migration|listening|version'
curl -s http://127.0.0.1:5080/healthz
```

Docker:`docker compose pull && docker compose up -d`(卷内数据自动迁移)。Master 升级期间 Agent 进入退避重连,恢复后按 DATA.md §4 精确补回停机期间流量。

### 7.4 升级 Agent

- 再次执行后台的一键安装命令(幂等:校验、替换二进制、重启服务;配置不变),或 `sudo bash install.sh --version v1.1.0` 固定版本。
- 批量:在管理后台复制各节点命令;或用现有配置管理工具分发同一脚本(脚本内含节点专属 Key,不能跨节点复用)。
- 协议兼容:同主版本 `ProtocolVersion` 内新旧混跑安全(PROTOCOL §8)。

### 7.5 运维检查清单与排障

| 现象 | 检查 |
|---|---|
| 节点始终 Unknown | Agent 日志:`authentication rejected (HTTP 401)` → Key 错误/节点禁用;`negotiate` 404 → `--server` 少了协议或路径不对(只填 origin);TLS 错误 → 证书链或时间不同步 |
| 只走 LongPolling(日志 `transport=LongPolling`) | 反代未转发 `Upgrade/Connection` 头(§2.3/§2.4);可用但配置下发延迟高 |
| 公网 IP 显示为 127.0.0.1 / 内网 IP | `SNM_KNOWN_PROXIES`/`SNM_KNOWN_NETWORKS` 未包含反代地址;或 Nginx 未设 `X-Forwarded-For` |
| 国家码为空 | GeoIP 尚未下载(后台设置 → GeoIP 状态/刷新);服务器无法访问 jsDelivr → 设 `SNM_GEOIP_BASE_URL` 镜像 |
| 流量数值突增 | 查看节点 `netIfs` 是否变化(新网卡历史累计被计入已通过重基线规避);节点是否重启(DATA.md §4.3) |
| 告警未发送 | 设置 → 通知渠道 → 测试;`/api/alerts/{id}` 的 `deliveries` 查看错误;冷却期内的新触发不通知(事件 `notified=false`) |
| 数据库变大 | `PRAGMA wal_checkpoint` 是否执行(每日 03:00);`RetentionService` 日志;`/api/system/info.dbSizeBytes/walSizeBytes` |
| 安装脚本 404 | 令牌过期(24 h)→ 后台重新生成;`site.publicBaseUrl` 与实际域名不一致 |
