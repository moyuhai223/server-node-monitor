# DEPLOY.md — 配置、反代、systemd、安装脚本、CI 与运维(候选设计 C)

> 面向部署者与 M8 实现者。配置键的权威表在 DESIGN §4.2;安装脚本对应 API §4.3 的端点与占位符;CI 产物命名在 §5.3 定案。全部脚本/YAML 为可直接落盘的完整文本(占位 `OWNER/server-node-monitor` 需替换为真实仓库)。

---

## 0. 决策摘要

| 问题 | 结论 |
|---|---|
| Q9 安装脚本 | 一行 `curl -fsSL https://<master>/install/<installToken>/agent.sh | sudo bash`。脚本由 Master 按节点渲染(嵌入模板 `Templates/install-agent.sh` 替换 5 个占位符),完成:root 检查 → `uname -m` 判定 `linux-x64/linux-arm64` → 从 `__RELEASE_BASE__` 下载二进制与 `SHA256SUMS` 并校验 → 建系统用户 `snm-agent` → 安装到 `/opt/snm-agent/snm-agent` → 写 `/etc/snm-agent/agent.env`(0640)→ 写加固的 systemd unit(`Restart=always`)→ `enable --now`。重复执行 = 升级/改配置 + 重启;`… | sudo bash -s -- uninstall` 完整卸载。Windows 用 `agent.ps1`(计划任务 ONSTART、失败自动重启)。 |
| Master 发布形态 | 自包含单文件 `linux-x64`/`linux-arm64`(`snm-master-<rid>.tar.gz`,内含 `wwwroot/`),systemd 运行于 `/opt/snm-master`,数据在 `/var/lib/snm`;另提供多阶段 `Dockerfile`(镜像 `ghcr.io/OWNER/snm-master`)。TLS/反代交给 Nginx 或 1Panel。 |
| CI | `deploy/.github/workflows/agent-aot.yml`(矩阵三平台 Native AOT,tag 触发发布 + `SHA256SUMS`)与 `master.yml`(构建/测试/前端/AOT 零警告断言/Docker)。GitHub 只读根目录 `.github/workflows`,因此根目录保留**同内容副本**,CI 内 `diff -r` 防漂移。 |
| Agent AOT arm64 | 首选 GitHub 原生 ARM runner `ubuntu-24.04-arm`(公开仓库免费);备选 x64 交叉编译(§5.1 注释)。 |

---

## 1. Master 配置

### 1.1 目录布局(Linux 裸机)

| 路径 | 内容 | 属主/权限 |
|---|---|---|
| `/opt/snm-master/` | `SNM.Master`(单文件)、`appsettings.json`、`wwwroot/` | `root:root 0755`;文件 0644,可执行 0755 |
| `/etc/snm-master/master.env` | 环境变量(密码/密钥等) | `root:snm 0640` |
| `/var/lib/snm/` | `snm.db`、`snm.db-wal`、`snm.db-shm`、`geoip/`、`jwt.key`、`backup/` | `snm:snm 0750`(由 systemd `StateDirectory=snm` 创建) |
| `/var/log/` | 无(日志进 journald:`journalctl -u snm-master -f`) | |

### 1.2 `appsettings.json`(随发布包,完整默认值)

```json
{
  "Snm": {
    "Listen": "http://127.0.0.1:5080",
    "DataDir": "./data",
    "Admin": { "User": "admin", "Password": "" },
    "Seed": { "PublicBaseUrl": "", "ReleaseBaseUrl": "https://github.com/OWNER/server-node-monitor/releases/latest/download" },
    "KnownProxies": "",
    "ForwardLimit": 1
  },
  "Jwt": { "SigningKey": "", "AccessTokenMinutes": 120, "RefreshTokenDays": 30, "Issuer": "snm", "Audience": "snm-admin" },
  "Realtime": { "PublicMaxConnections": 500 },
  "Database": { "BusyTimeoutMs": 5000 },
  "Logging": {
    "Json": false,
    "LogLevel": {
      "Default": "Information",
      "SNM": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.AspNetCore.SignalR": "Warning",
      "Microsoft.AspNetCore.Http.Connections": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

### 1.3 `/etc/snm-master/master.env`(示例)

```ini
SNM_LISTEN=http://127.0.0.1:5080
SNM_DATA_DIR=/var/lib/snm
SNM_ADMIN_USER=admin
SNM_ADMIN_PASSWORD=ChangeMe-Str0ng!
SNM_PUBLIC_BASE_URL=https://monitor.example.com
SNM_RELEASE_BASE_URL=https://github.com/OWNER/server-node-monitor/releases/latest/download
# 反代与 Master 不在同一台机器时填写反代 IP,多个逗号分隔
# SNM_KNOWN_PROXIES=10.0.0.2
# SNM_LOG_LEVEL=Information
# SNM_LOG_JSON=false
DOTNET_gcServer=0
```
`SNM_ADMIN_PASSWORD` 只在首次建库时生效;之后请从文件中删除并在后台改密。

---

## 2. Master systemd

`deploy/snm-master.service`:

```ini
[Unit]
Description=Server Node Monitor - Master
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=snm
Group=snm
WorkingDirectory=/opt/snm-master
EnvironmentFile=-/etc/snm-master/master.env
ExecStart=/opt/snm-master/SNM.Master
Restart=always
RestartSec=3
KillSignal=SIGTERM
TimeoutStopSec=30
StateDirectory=snm
StateDirectoryMode=0750
LimitNOFILE=65536
# hardening
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
RestrictNamespaces=true
LockPersonality=true
SystemCallArchitectures=native
ReadWritePaths=/var/lib/snm

[Install]
WantedBy=multi-user.target
```

安装步骤(`deploy/install-master.sh` 亦按此实现,支持重复执行):

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin snm 2>/dev/null || true
sudo mkdir -p /opt/snm-master /etc/snm-master
sudo tar -xzf snm-master-linux-x64.tar.gz -C /opt/snm-master
sudo cp deploy/master.env.example /etc/snm-master/master.env && sudo chown root:snm /etc/snm-master/master.env && sudo chmod 640 /etc/snm-master/master.env
sudo cp deploy/snm-master.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now snm-master
journalctl -u snm-master -n 50 --no-pager      # 首次启动会打印 "Initial admin password"(若未设置密码)
```

---

## 3. 反向代理

### 3.1 Nginx(`deploy/nginx.conf.example`)

```nginx
map $http_upgrade $connection_upgrade { default upgrade; '' close; }

server {
    listen 80;
    server_name monitor.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name monitor.example.com;

    ssl_certificate     /etc/letsencrypt/live/monitor.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/monitor.example.com/privkey.pem;

    client_max_body_size 1m;

    # SignalR hubs: WebSocket upgrade, long timeouts, no buffering
    location /hubs/ {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_buffering off;
        proxy_cache off;
    }

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 120s;
    }
}
```

要点:`X-Forwarded-For` 用 `$remote_addr`(不要 `$proxy_add_x_forwarded_for`,配合 Master `ForwardLimit=1` 防伪造);Master 仅信任 loopback,同机反代无需配置 `SNM_KNOWN_PROXIES`;`/hubs/agent` 的 LongPolling 每次请求可挂起最长 90 s,`proxy_read_timeout ≥ 120s` 即可,WebSocket 按 3600 s。

### 3.2 1Panel

1. 「网站 → 创建网站 → 反向代理」:域名 `monitor.example.com`,代理地址 `http://127.0.0.1:5080`。
2. 「网站 → 设置 → HTTPS」申请/上传证书,开启强制 HTTPS。
3. 「网站 → 设置 → 反向代理 → 编辑」确认或补充:开启 WebSocket(1Panel 的反代模板默认含 `Upgrade/Connection` 头),并在「配置文件」中把 `/hubs/` 的 `proxy_read_timeout` 改为 `3600s`、加入 `proxy_buffering off;`。
4. `X-Forwarded-For`:1Panel 模板默认 `$proxy_add_x_forwarded_for`;Master `ForwardLimit=1` 只取最右一跳(即 1Panel 看到的客户端 IP),安全。
5. 1Panel 的 OpenResty 与 Master 同机 → `SNM_KNOWN_PROXIES` 留空。

### 3.3 直连(无反代,仅内网/测试)

`SNM_LISTEN=http://0.0.0.0:5080`;此时无 TLS,AgentKey 明文传输,仅限可信内网。

---

## 4. Agent 安装脚本

### 4.1 端点与渲染

- 后台「节点 → 安装脚本」调用 `GET /api/nodes/{id}/install-script?os=linux|windows`(API §4.3)得到命令与全文;脚本本体由匿名端点 `GET /install/{installToken}/agent.sh` / `agent.ps1` 提供(限速 10/min/IP,`Cache-Control: no-store`)。
- 模板为 Master 的嵌入资源 `Templates/install-agent.sh`、`Templates/install-agent.ps1`;`InstallScriptService.Render()` 做纯文本替换:

| 占位符 | 值 |
|---|---|
| `__SERVER_URL__` | `general.publicBaseUrl`(去尾部 `/`);为空时由请求推断 `X-Forwarded-Proto://Host` |
| `__AGENT_KEY__` | 节点 `AgentKey` |
| `__RELEASE_BASE__` | `agent.agentVersionPin` 为空 → `agent.releaseBaseUrl`;非空且 base 以 `/releases/latest/download` 结尾 → 替换为 `/releases/download/v{pin}`;非空且其他形式 → `{base}/v{pin}` |
| `__VERSION__` | `agent.agentVersionPin` 或 `latest`(仅写入 env 文件供显示) |
| `__NODE_NAME__` | 节点 `Name`(仅用于脚本输出提示) |

安全提示:渲染后的脚本含 AgentKey,后台弹窗与脚本头部均标注「此命令包含节点密钥,请勿转发」。

### 4.2 `Templates/install-agent.sh`(完整模板)

```bash
#!/usr/bin/env bash
# Server Node Monitor - agent installer for node "__NODE_NAME__"
# WARNING: this script embeds the node's AgentKey. Do not share it.
# Usage:
#   curl -fsSL <url> | sudo bash                       # install / upgrade / reconfigure (idempotent)
#   curl -fsSL <url> | sudo SNM_PROXY=socks5://10.0.0.1:1080 bash
#   curl -fsSL <url> | sudo bash -s -- uninstall       # remove service, binary, config and user
#   curl -fsSL <url> | sudo bash -s -- status
set -euo pipefail

SERVER_URL="__SERVER_URL__"
AGENT_KEY="__AGENT_KEY__"
RELEASE_BASE="__RELEASE_BASE__"
AGENT_VERSION="__VERSION__"

SVC_NAME="snm-agent"
SVC_USER="snm-agent"
INSTALL_DIR="/opt/snm-agent"
BIN_PATH="${INSTALL_DIR}/snm-agent"
CONF_DIR="/etc/snm-agent"
ENV_FILE="${CONF_DIR}/agent.env"
UNIT_FILE="/etc/systemd/system/${SVC_NAME}.service"
ACTION="${1:-install}"

log()  { printf '\033[1;34m[snm]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[snm]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[snm]\033[0m %s\n' "$*" >&2; exit 1; }

require_root() { [ "$(id -u)" -eq 0 ] || die "please run as root (sudo)."; }
require_systemd() { command -v systemctl >/dev/null 2>&1 && [ -d /run/systemd/system ] || die "systemd is required."; }

detect_arch() {
  case "$(uname -m)" in
    x86_64|amd64)   echo "linux-x64" ;;
    aarch64|arm64)  echo "linux-arm64" ;;
    *) die "unsupported architecture: $(uname -m) (supported: x86_64, aarch64)" ;;
  esac
}

fetch() { # fetch <url> <dest>
  if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 --connect-timeout 15 -o "$2" "$1";
  elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1";
  else die "curl or wget is required."; fi
}

do_uninstall() {
  require_root; require_systemd
  log "stopping and removing ${SVC_NAME} ..."
  systemctl disable --now "${SVC_NAME}" 2>/dev/null || true
  rm -f "${UNIT_FILE}"; systemctl daemon-reload
  rm -rf "${INSTALL_DIR}" "${CONF_DIR}"
  if id "${SVC_USER}" >/dev/null 2>&1; then userdel "${SVC_USER}" 2>/dev/null || true; fi
  log "uninstalled."
}

do_status() { systemctl --no-pager status "${SVC_NAME}" || true; }

do_install() {
  require_root; require_systemd
  [ -n "${SERVER_URL}" ] && [ -n "${AGENT_KEY}" ] || die "script is not rendered (missing server url / key)."
  local arch asset tmp sums expected actual
  arch="$(detect_arch)"; asset="snm-agent-${arch}"
  tmp="$(mktemp -d)"; trap 'rm -rf "${tmp}"' EXIT

  log "downloading ${RELEASE_BASE}/${asset} (version: ${AGENT_VERSION}) ..."
  fetch "${RELEASE_BASE}/${asset}" "${tmp}/${asset}"
  fetch "${RELEASE_BASE}/SHA256SUMS" "${tmp}/SHA256SUMS"
  expected="$(grep -E "[[:space:]]\*?${asset}\$" "${tmp}/SHA256SUMS" | awk '{print $1}' | head -n1)"
  [ -n "${expected}" ] || die "checksum for ${asset} not found in SHA256SUMS."
  actual="$(sha256sum "${tmp}/${asset}" | awk '{print $1}')"
  [ "${expected}" = "${actual}" ] || die "checksum mismatch: expected ${expected}, got ${actual}."
  log "checksum OK."

  if ! id "${SVC_USER}" >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "${SVC_USER}"
    log "created system user ${SVC_USER}."
  fi

  mkdir -p "${INSTALL_DIR}" "${CONF_DIR}"
  install -m 0755 -o root -g root "${tmp}/${asset}" "${BIN_PATH}.new"
  mv -f "${BIN_PATH}.new" "${BIN_PATH}"          # atomic replace; running process keeps old inode until restart

  umask 077
  {
    echo "SNM_SERVER=${SERVER_URL}"
    echo "SNM_KEY=${AGENT_KEY}"
    [ -n "${SNM_PROXY:-}" ]       && echo "SNM_PROXY=${SNM_PROXY}"
    [ -n "${SNM_NAME:-}" ]        && echo "SNM_NAME=${SNM_NAME}"
    [ -n "${SNM_NET_IF:-}" ]      && echo "SNM_NET_IF=${SNM_NET_IF}"
    [ -n "${SNM_NET_EXCLUDE:-}" ] && echo "SNM_NET_EXCLUDE=${SNM_NET_EXCLUDE}"
    [ -n "${SNM_LOG_LEVEL:-}" ]   && echo "SNM_LOG_LEVEL=${SNM_LOG_LEVEL}"
    echo "SNM_AGENT_VERSION=${AGENT_VERSION}"
    true
  } > "${ENV_FILE}.new"
  chown root:"${SVC_USER}" "${ENV_FILE}.new"; chmod 0640 "${ENV_FILE}.new"; mv -f "${ENV_FILE}.new" "${ENV_FILE}"
  umask 022

  cat > "${UNIT_FILE}" <<UNIT
[Unit]
Description=Server Node Monitor - Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SVC_USER}
Group=${SVC_USER}
EnvironmentFile=${ENV_FILE}
ExecStart=${BIN_PATH}
Restart=always
RestartSec=5
StartLimitIntervalSec=0
KillSignal=SIGTERM
TimeoutStopSec=10
LimitNOFILE=16384
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
RestrictNamespaces=true
LockPersonality=true
CapabilityBoundingSet=
SystemCallArchitectures=native

[Install]
WantedBy=multi-user.target
UNIT

  systemctl daemon-reload
  systemctl enable "${SVC_NAME}" >/dev/null
  if systemctl is-active --quiet "${SVC_NAME}"; then systemctl restart "${SVC_NAME}"; log "restarted ${SVC_NAME}."; else systemctl start "${SVC_NAME}"; log "started ${SVC_NAME}."; fi
  sleep 2
  if systemctl is-active --quiet "${SVC_NAME}"; then
    log "OK: $("${BIN_PATH}" --version 2>/dev/null || echo snm-agent) is running as ${SVC_USER}. Logs: journalctl -u ${SVC_NAME} -f"
  else
    warn "service is not active; last log lines:"; journalctl -u "${SVC_NAME}" -n 20 --no-pager || true; exit 1
  fi
}

case "${ACTION}" in
  install|"") do_install ;;
  uninstall)  do_uninstall ;;
  status)     do_status ;;
  *) die "unknown action: ${ACTION} (install|uninstall|status)" ;;
esac
```

幂等性矩阵:

| 状态 | 再次执行 `install` 的效果 |
|---|---|
| 未安装 | 全新安装并启动 |
| 已安装同版本 | 重新下载校验、覆盖同内容二进制与 env、`restart` |
| 已安装旧版本 | 二进制原子替换、`restart` → 升级 |
| Key 已轮换(脚本为新 Key) | env 更新、`restart` → 新 Key 生效 |
| 服务被手动 stop | 重新 `start` |
| 用户/目录被部分删除 | 缺什么补什么 |

`RestrictAddressFamilies` 必须包含 `AF_NETLINK`(.NET 在 Linux 用 netlink 枚举网卡/IP);`ProtectSystem=strict` 下 Agent 无需写任何路径。

### 4.3 `Templates/install-agent.ps1`(Windows,完整模板)

```powershell
#Requires -RunAsAdministrator
# Server Node Monitor - agent installer (Windows x64). Embeds the node's AgentKey; do not share.
#   irm <url> | iex                       -> install / upgrade
#   & ([scriptblock]::Create((irm <url>))) uninstall
param([string]$Action = "install")
$ErrorActionPreference = "Stop"
$ServerUrl   = "__SERVER_URL__"
$AgentKey    = "__AGENT_KEY__"
$ReleaseBase = "__RELEASE_BASE__"
$InstallDir  = "$env:ProgramFiles\snm-agent"
$BinPath     = "$InstallDir\snm-agent.exe"
$EnvPath     = "$InstallDir\agent.env"
$TaskName    = "snm-agent"

function Uninstall-Agent {
  schtasks /End /TN $TaskName 2>$null | Out-Null
  schtasks /Delete /TN $TaskName /F 2>$null | Out-Null
  Get-Process snm-agent -ErrorAction SilentlyContinue | Stop-Process -Force
  Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
  Write-Host "[snm] uninstalled."
}

function Install-Agent {
  if ([Environment]::Is64BitOperatingSystem -eq $false -or $env:PROCESSOR_ARCHITECTURE -ne "AMD64") { throw "only Windows x64 is supported." }
  New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
  $tmp = Join-Path $env:TEMP "snm-agent-win-x64.exe"
  Write-Host "[snm] downloading $ReleaseBase/snm-agent-win-x64.exe ..."
  Invoke-WebRequest -Uri "$ReleaseBase/snm-agent-win-x64.exe" -OutFile $tmp -UseBasicParsing
  $sums = (Invoke-WebRequest -Uri "$ReleaseBase/SHA256SUMS" -UseBasicParsing).Content
  $expected = ($sums -split "`n" | Where-Object { $_ -match "snm-agent-win-x64\.exe\s*$" } | Select-Object -First 1) -split "\s+" | Select-Object -First 1
  if (-not $expected) { throw "checksum for snm-agent-win-x64.exe not found." }
  $actual = (Get-FileHash -Algorithm SHA256 $tmp).Hash.ToLower()
  if ($actual -ne $expected.ToLower()) { throw "checksum mismatch: expected $expected got $actual" }
  Write-Host "[snm] checksum OK."
  schtasks /End /TN $TaskName 2>$null | Out-Null
  Get-Process snm-agent -ErrorAction SilentlyContinue | Stop-Process -Force
  Move-Item -Force $tmp $BinPath
  # config file (read by the scheduled task through cmd /c set) - restrict ACL to SYSTEM and Administrators
  @("SNM_SERVER=$ServerUrl", "SNM_KEY=$AgentKey", $(if ($env:SNM_PROXY) { "SNM_PROXY=$env:SNM_PROXY" })) | Where-Object { $_ } | Set-Content -Encoding ASCII $EnvPath
  icacls $EnvPath /inheritance:r /grant:r "SYSTEM:(R)" "Administrators:(R)" | Out-Null
  $cmd = "cmd /c `"for /f `"usebackq tokens=1,* delims==`" %a in (`"$EnvPath`") do set %a=%b & `"$BinPath`"`""
  $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
    <Enabled>true</Enabled><Hidden>true</Hidden>
  </Settings>
  <Actions Context="Author"><Exec><Command>cmd.exe</Command><Arguments>/c "for /f "usebackq tokens=1,* delims==" %a in ("$EnvPath") do @set %a=%b &amp;&amp; "$BinPath""</Arguments></Exec></Actions>
</Task>
"@
  $xmlPath = Join-Path $env:TEMP "snm-agent-task.xml"; $xml | Set-Content -Encoding Unicode $xmlPath
  schtasks /Create /TN $TaskName /XML $xmlPath /F | Out-Null
  schtasks /Run /TN $TaskName | Out-Null
  Start-Sleep 2
  if (Get-Process snm-agent -ErrorAction SilentlyContinue) { Write-Host "[snm] OK: snm-agent is running (scheduled task '$TaskName', SYSTEM, restart on failure)." }
  else { throw "snm-agent did not start; check Task Scheduler history." }
}

switch ($Action) { "uninstall" { Uninstall-Agent } default { Install-Agent } }
```
说明:Windows 版以 SYSTEM 计划任务运行(开机触发、失败 1 分钟后重启,最多 999 次),避免为 AOT 控制台程序引入 Windows Service 宿主依赖;Windows 为次要平台(BRIEF §2.2)。

### 4.4 手动运行(不装服务)

```bash
./snm-agent --server https://monitor.example.com --key <AgentKey> [--proxy socks5://10.0.0.1:1080] [--name web-01]
```
参数全表见 DESIGN §7.6。

---

## 5. GitHub Actions

工作流源文件位于 `deploy/.github/workflows/`(BRIEF 固定布局);GitHub 只识别根目录 `.github/workflows/`,因此 M8 需 `cp -r deploy/.github .github`,`master.yml` 中的 `diff -r deploy/.github .github` 步骤保证两处一致。

### 5.1 `agent-aot.yml`

```yaml
name: agent-aot

on:
  push:
    tags: ['v*']
  pull_request:
    paths:
      - 'src/SNM.Agent/**'
      - 'src/SNM.Contracts/**'
      - 'Directory.Build.props'
      - 'Directory.Packages.props'
      - 'global.json'
      - '.github/workflows/agent-aot.yml'
  workflow_dispatch:

permissions:
  contents: write

jobs:
  publish:
    name: AOT ${{ matrix.rid }}
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false
      matrix:
        include:
          - { rid: linux-x64,   os: ubuntu-24.04,     bin: snm-agent,     asset: snm-agent-linux-x64 }
          - { rid: linux-arm64, os: ubuntu-24.04-arm, bin: snm-agent,     asset: snm-agent-linux-arm64 }
          - { rid: win-x64,     os: windows-2022,     bin: snm-agent.exe, asset: snm-agent-win-x64.exe }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - name: Install native toolchain (Linux)
        if: runner.os == 'Linux'
        run: sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
      - name: Restore
        run: dotnet restore src/SNM.Agent/SNM.Agent.csproj -r ${{ matrix.rid }}
      - name: Publish (Native AOT)
        shell: bash
        run: |
          dotnet publish src/SNM.Agent/SNM.Agent.csproj -c Release -r ${{ matrix.rid }} --no-restore \
            -p:PublishAot=true -p:TrimmerSingleWarn=false -p:ContinuousIntegrationBuild=true \
            -o out/${{ matrix.rid }} 2>&1 | tee publish.log
      - name: Assert zero IL warnings
        shell: bash
        run: |
          if grep -E 'warning IL[23][0-9]{3}' publish.log; then echo "::error::AOT/trim warnings found"; exit 1; fi
      - name: Rename & checksum
        shell: bash
        run: |
          mkdir -p dist
          cp "out/${{ matrix.rid }}/${{ matrix.bin }}" "dist/${{ matrix.asset }}"
          cd dist && sha256sum "${{ matrix.asset }}" > "${{ matrix.asset }}.sha256"
          "./${{ matrix.asset }}" --version || true
      - uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.asset }}
          path: dist/*
          if-no-files-found: error

  release:
    name: GitHub Release
    needs: publish
    if: startsWith(github.ref, 'refs/tags/v')
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/download-artifact@v4
        with:
          path: dist
          merge-multiple: true
      - name: Build SHA256SUMS
        run: |
          cd dist && rm -f *.sha256 && sha256sum snm-agent-* > SHA256SUMS && cat SHA256SUMS
      - uses: softprops/action-gh-release@v2
        with:
          files: |
            dist/snm-agent-linux-x64
            dist/snm-agent-linux-arm64
            dist/snm-agent-win-x64.exe
            dist/SHA256SUMS
          generate_release_notes: true
```

备选(私有仓库无 ARM runner 时)——在 `ubuntu-24.04` 上交叉编译 `linux-arm64`,替换该矩阵项的 `os` 并在 Publish 前增加:

```yaml
      - name: Cross toolchain for linux-arm64
        if: matrix.rid == 'linux-arm64'
        run: |
          sudo dpkg --add-architecture arm64
          sudo sed -i 's/^deb /deb [arch=amd64] /' /etc/apt/sources.list.d/ubuntu.sources || true
          echo "deb [arch=arm64] http://ports.ubuntu.com/ubuntu-ports noble main universe" | sudo tee /etc/apt/sources.list.d/arm64.list
          sudo apt-get update
          sudo apt-get install -y clang llvm lld gcc-aarch64-linux-gnu binutils-aarch64-linux-gnu zlib1g-dev:arm64
```
并给 publish 追加 `-p:CppCompilerAndLinker=clang -p:LinkerFlavor=lld -p:ObjCopyName=aarch64-linux-gnu-objcopy`。这是 .NET 官方文档描述的交叉编译方式;首选仍是原生 ARM runner。

### 5.2 `master.yml`

```yaml
name: master

on:
  push:
    branches: [main]
    tags: ['v*']
  pull_request:
  workflow_dispatch:

permissions:
  contents: write
  packages: write

env:
  DOTNET_NOLOGO: 1
  DOTNET_CLI_TELEMETRY_OPTOUT: 1

jobs:
  build-test:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-node@v4
        with: { node-version: 22, cache: npm, cache-dependency-path: 'web/*/package-lock.json' }
      - name: Workflows in sync
        run: diff -r deploy/.github .github
      - name: Lint shell & workflows
        run: |
          sudo apt-get update && sudo apt-get install -y shellcheck
          bash -n src/SNM.Master/Templates/install-agent.sh && shellcheck -S warning src/SNM.Master/Templates/install-agent.sh scripts/*.sh
          curl -sSfL https://raw.githubusercontent.com/rhysd/actionlint/main/scripts/download-actionlint.bash | bash -s -- latest /tmp
          /tmp/actionlint .github/workflows/*.yml
      - name: Web build
        run: bash scripts/build-web.sh
      - name: Public dashboard field whitelist
        run: bash scripts/check-public-fields.sh
      - name: Restore & build
        run: |
          dotnet restore ServerNodeMonitor.sln
          dotnet build ServerNodeMonitor.sln -c Release --no-restore
      - name: Test
        run: dotnet test ServerNodeMonitor.sln -c Release --no-build --logger "trx;LogFileName=test.trx" --results-directory TestResults
      - uses: actions/upload-artifact@v4
        if: always()
        with: { name: test-results, path: TestResults }
      - name: Agent AOT smoke (linux-x64, zero IL warnings)
        run: |
          sudo apt-get install -y clang zlib1g-dev
          dotnet publish src/SNM.Agent/SNM.Agent.csproj -c Release -r linux-x64 -p:PublishAot=true -p:TrimmerSingleWarn=false -o out/agent 2>&1 | tee aot.log
          ! grep -E 'warning IL[23][0-9]{3}' aot.log
          ./out/agent/snm-agent --version
      - name: Vulnerable packages (report only)
        run: dotnet list ServerNodeMonitor.sln package --vulnerable --include-transitive || true

  publish-master:
    needs: build-test
    if: startsWith(github.ref, 'refs/tags/v')
    runs-on: ubuntu-24.04
    strategy:
      matrix:
        rid: [linux-x64, linux-arm64]
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - uses: actions/setup-node@v4
        with: { node-version: 22 }
      - run: bash scripts/build-web.sh
      - name: Publish self-contained single-file
        run: |
          dotnet publish src/SNM.Master/SNM.Master.csproj -c Release -r ${{ matrix.rid }} --self-contained true \
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ContinuousIntegrationBuild=true -o out/master
          cp deploy/snm-master.service deploy/master.env.example deploy/install-master.sh out/master/
          tar -C out/master -czf snm-master-${{ matrix.rid }}.tar.gz .
          sha256sum snm-master-${{ matrix.rid }}.tar.gz > snm-master-${{ matrix.rid }}.tar.gz.sha256
      - uses: softprops/action-gh-release@v2
        with:
          files: |
            snm-master-${{ matrix.rid }}.tar.gz
            snm-master-${{ matrix.rid }}.tar.gz.sha256

  docker:
    needs: build-test
    if: github.event_name != 'pull_request'
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - uses: docker/metadata-action@v5
        id: meta
        with:
          images: ghcr.io/${{ github.repository_owner }}/snm-master
          tags: |
            type=ref,event=branch
            type=semver,pattern={{version}}
            type=semver,pattern={{major}}.{{minor}}
      - uses: docker/build-push-action@v6
        with:
          context: .
          file: deploy/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: ${{ startsWith(github.ref, 'refs/tags/v') || github.ref == 'refs/heads/main' }}
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
```

### 5.3 产物命名

| 产物 | 名称 | 来源 |
|---|---|---|
| Agent Linux x64 | `snm-agent-linux-x64` | `agent-aot.yml` |
| Agent Linux arm64 | `snm-agent-linux-arm64` | |
| Agent Windows x64 | `snm-agent-win-x64.exe` | |
| 校验和 | `SHA256SUMS`(`sha256sum` 格式:`<hex>  <filename>`,每行一个) | release job |
| Master | `snm-master-linux-x64.tar.gz`、`snm-master-linux-arm64.tar.gz`(+ `.sha256`) | `master.yml` |
| 镜像 | `ghcr.io/OWNER/snm-master:<semver>`、`:main` | |

版本号:tag `vX.Y.Z` → `Directory.Build.props` 中 `<Version>` 由 CI 用 `-p:Version=${GITHUB_REF_NAME#v}` 注入(本地默认 `0.0.0-dev`);`InformationalVersion` 自动带 `+<sha>`,即 `NodeInfo.AgentVersion` 与 `/api/system/info.version` 的值。安装脚本的 `agent.releaseBaseUrl` 默认指向 `releases/latest/download`,因此发布新 tag 后无需改设置。

---

## 6. Docker

### 6.1 `deploy/Dockerfile`(多阶段,支持 buildx 多架构)

```dockerfile
# syntax=docker/dockerfile:1.7
# ---- web assets ----
FROM node:22-alpine AS web
WORKDIR /src
COPY web/admin/package.json web/admin/package-lock.json web/admin/
RUN cd web/admin && npm ci
COPY web/public/package.json web/public/package-lock.json web/public/
RUN cd web/public && npm ci
COPY web/ web/
COPY scripts/build-web.sh scripts/
RUN mkdir -p src/SNM.Master && bash scripts/build-web.sh --no-install

# ---- .NET build ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ServerNodeMonitor.sln ./
COPY src/SNM.Contracts/SNM.Contracts.csproj src/SNM.Contracts/
COPY src/SNM.Master/SNM.Master.csproj src/SNM.Master/
RUN dotnet restore src/SNM.Master/SNM.Master.csproj -a $TARGETARCH
COPY src/SNM.Contracts/ src/SNM.Contracts/
COPY src/SNM.Master/ src/SNM.Master/
RUN dotnet publish src/SNM.Master/SNM.Master.csproj -c Release -a $TARGETARCH --no-restore --self-contained false -o /app
COPY --from=web /src/src/SNM.Master/wwwroot /app/wwwroot

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV SNM_LISTEN=http://0.0.0.0:5080 \
    SNM_DATA_DIR=/data \
    DOTNET_gcServer=0
RUN mkdir -p /data && chown app:app /data
USER app
VOLUME ["/data"]
EXPOSE 5080
ENTRYPOINT ["dotnet", "SNM.Master.dll"]
```
`build-web.sh --no-install` 表示跳过 `npm ci`(镜像内已装)。镜像内为框架依赖发布(基础镜像自带运行时),体积更小;裸机 tarball 为自包含。

### 6.2 `deploy/docker-compose.yml`

```yaml
services:
  snm-master:
    image: ghcr.io/OWNER/snm-master:latest
    container_name: snm-master
    restart: unless-stopped
    ports:
      - "127.0.0.1:5080:5080"        # 仍由宿主机 Nginx/1Panel 反代并终止 TLS
    environment:
      SNM_ADMIN_USER: admin
      SNM_ADMIN_PASSWORD: ChangeMe-Str0ng!
      SNM_PUBLIC_BASE_URL: https://monitor.example.com
      # 容器网络下反代地址不是 loopback,必须声明信任的代理网段:
      SNM_KNOWN_PROXIES: 172.16.0.0/12
    volumes:
      - snm-data:/data
volumes:
  snm-data:
```
注意:容器内 Master 看到的连接来源是 Docker 网桥地址,`SNM_KNOWN_PROXIES` 必须包含反代所在网段,否则 `X-Forwarded-For` 不被采纳,所有节点公网 IP 都会显示为网桥 IP。

---

## 7. 备份、升级与恢复(SQLite WAL 注意事项)

### 7.1 备份

- **正确做法(在线)**:`sqlite3 /var/lib/snm/snm.db "VACUUM INTO '/var/lib/snm/backup/snm-$(date +%F).db'"`(需 `apt install sqlite3`;`VACUUM INTO` 产生一致的单文件快照,包含 WAL 中尚未 checkpoint 的数据)。`deploy/backup.sh` 实现该命令 + 保留最近 14 份 + 可选 `gzip`。
- **替代做法(离线)**:`systemctl stop snm-master` 后复制 **三个文件** `snm.db`、`snm.db-wal`、`snm.db-shm`(WAL 模式下只拷 `.db` 会丢最近写入),再 `start`。
- **禁止**:运行中直接 `cp snm.db`(可能不一致);使用 `rsync` 分别同步三个文件(时间点不一致)。
- `geoip/` 可不备份(可重新下载);`jwt.key` 需备份(丢失 → 所有登录会话失效,需重新登录,不影响数据)。
- 建议 cron:`17 3 * * * /opt/snm-master/backup.sh`(在 `DbMaintenance` 04:00 UTC 之前或之后均可)。

### 7.2 升级 Master

```bash
sudo systemctl stop snm-master
sudo /opt/snm-master/backup.sh                     # 或 VACUUM INTO
sudo tar -xzf snm-master-linux-x64.tar.gz -C /opt/snm-master   # 覆盖二进制与 wwwroot,不触碰 /var/lib/snm 与 /etc/snm-master
sudo systemctl start snm-master
journalctl -u snm-master -n 100 --no-pager | grep -E 'Applying migration|ready'
```
- 迁移在启动时自动执行(DATA §1),**只向前**;升级前必须备份。
- 回滚:`stop` → 恢复备份的 `.db`(删除新的 `-wal/-shm`)→ 解压旧 tarball → `start`。新版本新增的列在旧版本不识别时无害,但已执行的迁移无法被旧版本"降级",所以回滚必须同时回滚数据库。
- Docker:`docker compose pull && docker compose up -d`(数据卷不变);升级前 `docker compose exec snm-master sqlite3 …` 不可用(镜像无 sqlite3),用 `docker run --rm -v snm-data:/data alpine sh -c "apk add sqlite && sqlite3 /data/snm.db \"VACUUM INTO '/data/backup/pre-upgrade.db'\""`。

### 7.3 升级 Agent

无 OTA。发布新 tag 后,在每台机器重新执行同一条安装命令(脚本幂等,原子替换二进制并重启);或用后台的节点列表按 `agentVersion` 列筛出旧版本逐台处理。`agent.agentVersionPin` 可把所有新安装钉在某个版本。

### 7.4 恢复

```bash
sudo systemctl stop snm-master
sudo rm -f /var/lib/snm/snm.db /var/lib/snm/snm.db-wal /var/lib/snm/snm.db-shm
sudo cp /var/lib/snm/backup/snm-2026-09-01.db /var/lib/snm/snm.db
sudo chown snm:snm /var/lib/snm/snm.db
sudo systemctl start snm-master
```
恢复后 Agent 侧无需操作(Key 在 DB 中);恢复点之后的历史指标与流量丢失,`NodeTrafficState` 锚点回到旧值,第一批心跳按 Delta 规则(DATA §6.3 "Master 重启")补齐差值。

### 7.5 迁移到另一台机器

停服 → 拷贝 `/var/lib/snm`(三文件 + `jwt.key`)与 `/etc/snm-master/master.env` → 新机安装同版本 → 启动 → 改 DNS。Agent 通过域名连接,无需改动;若改了地址,需重新执行各节点安装命令(新脚本内含新地址)。

---

## 8. 运维检查与故障排查

| 现象 | 检查 | 处理 |
|---|---|---|
| 后台节点全部离线但 Agent 日志正常 | `journalctl -u snm-master | grep 'Agent connected'`;Nginx 是否转发 `/hubs/` WebSocket | 修正反代 `Upgrade/Connection` 头与超时 |
| 节点公网 IP 显示为 `127.0.0.1` 或网桥地址 | `SNM_KNOWN_PROXIES`;Nginx `X-Forwarded-For` | 见 §3.1 / §6.2 |
| Agent 日志 `401` | Key 是否被轮换/节点被禁用 | 重新执行安装命令 |
| Agent 日志 `WebSockets failed, using LongPolling` | 代理不支持 WebSocket | 正常,可忽略;或换 HTTP 代理 |
| 国旗为空 | 后台「系统设置 → GeoIP」状态;`data/geoip/` 是否有文件;出网是否可达 jsDelivr | 点「立即刷新」;或离线手动放置两个 CSV 到 `geoip/` 后重启 |
| 告警未发送 | 「通知渠道 → 发送测试」;`AlertEvents.notifyError`;冷却期(`suppressed`) | 修正 token/URL;等待冷却 |
| `SQLITE_BUSY` 日志 | 磁盘 IO;是否有外部进程打开了 DB | 不要用外部工具长时间持有写锁;备份用 `VACUUM INTO` |
| DB 体积持续增长 | `GET /api/system/info.dbSizeBytes/walSizeBytes` | 每日 04:00 UTC 自动 checkpoint;可手动 `sqlite3 snm.db "PRAGMA wal_checkpoint(TRUNCATE);"` |
| 忘记管理员密码 | — | `stop` → `sqlite3 snm.db "DELETE FROM AdminUsers;"` → 设置 `SNM_ADMIN_PASSWORD` → `start`(表为空时重新种子) |
| 大屏空白 | 浏览器控制台;`general.publicDashboardEnabled` | 打开设置;检查 `/hubs/public` 反代 |

健康探活:`GET /api/system/health` → `{"status":"ok"}`(DB 不可写时 503),可接入 1Panel/Uptime 监控。
