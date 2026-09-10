#!/usr/bin/env bash
# Server Node Monitor - agent installer for Linux x86_64 / aarch64 with systemd.
#
# Standalone (values from arguments or environment):
#   curl -fsSL https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-agent.sh \
#     | sudo bash -s -- --server https://m.example.com --key snmk_xxxxxxxx [--proxy socks5://10.0.0.1:1080] [--net-if eth0]
# Served by a master with the node's values pre-filled (节点管理 → 安装脚本):
#   curl -fsSL https://m.example.com/install/<token> | sudo bash
# Uninstall / status:
#   ... | sudo bash -s -- uninstall        ... | sudo bash -s -- status
#
# Rendered {{GENERATED_AT}} by master {{MASTER_VERSION}} for node "{{NODE_NAME}}" (placeholders stay literal in the standalone copy).
set -euo pipefail

# ---- values filled in by the master; left untouched when the script is used standalone ----
TPL_SERVER='{{SERVER_URL}}'
TPL_KEY='{{AGENT_KEY}}'
TPL_RELEASE='{{RELEASE_BASE_URL}}'
tpl() { case "$1" in '{{'*'}}') printf '' ;; *) printf '%s' "$1" ;; esac; }

SNM_SERVER="${SNM_SERVER:-$(tpl "$TPL_SERVER")}"
SNM_KEY="${SNM_KEY:-$(tpl "$TPL_KEY")}"
RELEASE_BASE="${SNM_RELEASE_BASE:-$(tpl "$TPL_RELEASE")}"
PROXY="${SNM_PROXY:-}"
NET_IF="${SNM_NET_IF:-}"
DISK_INCLUDE="${SNM_DISK_INCLUDE:-}"
NODE_NAME_OVERRIDE="${SNM_NAME:-}"
INTERVAL="${SNM_INTERVAL:-}"
PIN_VERSION="${SNM_VERSION:-}"
DRY_RUN=${SNM_DRY_RUN:-0}

INSTALL_DIR=/opt/snm-agent
CONF_DIR=/etc/snm-agent
ENV_FILE=$CONF_DIR/agent.env
UNIT_FILE=/etc/systemd/system/snm-agent.service
SERVICE=snm-agent
SVC_USER=snm-agent

log()  { printf '\033[1;32m[snm]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[snm]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[snm] ERROR:\033[0m %s\n' "$*" >&2; exit 1; }
run()  { if [ "$DRY_RUN" = 1 ]; then echo "+ $*"; else "$@"; fi; }

usage() {
  cat <<'EOF'
Server Node Monitor agent installer

  install-agent.sh [install|uninstall|status] [options]

Options (or environment variables):
  --server URL          SNM_SERVER        master origin, e.g. https://m.example.com (required)
  --key KEY             SNM_KEY           node key from the admin UI, snmk_... (required)
  --proxy URL           SNM_PROXY         http://, https://, socks5://, socks4:// proxy for the agent (and the download)
  --net-if a,b          SNM_NET_IF        NICs whose counters are summed (default: automatic filter)
  --disk-include /,/d   SNM_DISK_INCLUDE  mount points to report (default: automatic filter)
  --name NAME           SNM_NAME          override the reported hostname
  --interval MS         SNM_INTERVAL      heartbeat interval 1000-60000 (the master's value wins)
  --version vX.Y.Z      SNM_VERSION       pin a release (default: latest)
  --release-base URL    SNM_RELEASE_BASE  download base (default: GitHub releases of the project)
  --dry-run             SNM_DRY_RUN=1     print privileged actions instead of executing them
EOF
}

ACTION=install
while [ $# -gt 0 ]; do
  case "$1" in
    install|uninstall|status) ACTION=$1 ;;
    --server=*) SNM_SERVER=${1#*=} ;;             --server) SNM_SERVER=${2:-}; shift ;;
    --key=*) SNM_KEY=${1#*=} ;;                   --key) SNM_KEY=${2:-}; shift ;;
    --proxy=*) PROXY=${1#*=} ;;                   --proxy) PROXY=${2:-}; shift ;;
    --net-if=*) NET_IF=${1#*=} ;;                 --net-if) NET_IF=${2:-}; shift ;;
    --disk-include=*) DISK_INCLUDE=${1#*=} ;;     --disk-include) DISK_INCLUDE=${2:-}; shift ;;
    --name=*) NODE_NAME_OVERRIDE=${1#*=} ;;       --name) NODE_NAME_OVERRIDE=${2:-}; shift ;;
    --interval=*) INTERVAL=${1#*=} ;;             --interval) INTERVAL=${2:-}; shift ;;
    --version=*) PIN_VERSION=${1#*=} ;;           --version) PIN_VERSION=${2:-}; shift ;;
    --release-base=*) RELEASE_BASE=${1#*=} ;;     --release-base) RELEASE_BASE=${2:-}; shift ;;
    --dry-run) DRY_RUN=1 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done
[ -n "$RELEASE_BASE" ] || RELEASE_BASE="https://github.com/moyuhai223/server-node-monitor/releases/latest/download"
RELEASE_BASE=${RELEASE_BASE%/}
SNM_SERVER=${SNM_SERVER%/}

if [ "$DRY_RUN" != 1 ]; then
  [ "$(uname -s)" = Linux ] || die "this installer supports Linux only; for Windows use install-agent.ps1 (or ?os=windows on the master)"
  [ "$(id -u)" -eq 0 ] || die "please run as root (sudo)"
  command -v systemctl >/dev/null 2>&1 || die "systemd is required"
fi

# ---------------------------------------------------------------- status / uninstall
if [ "$ACTION" = status ]; then
  echo "binary : $("$INSTALL_DIR/snm-agent" --version 2>/dev/null || echo '(not installed)')"
  command -v systemctl >/dev/null 2>&1 && { systemctl status "$SERVICE" --no-pager -l 2>/dev/null | head -12 || true; }
  [ -f "$ENV_FILE" ] && echo "server : $(sed -n 's/^SNM_SERVER=//p' "$ENV_FILE")"
  exit 0
fi

if [ "$ACTION" = uninstall ]; then
  log "stopping and removing $SERVICE"
  run systemctl disable --now "$SERVICE" 2>/dev/null || true
  run rm -f "$UNIT_FILE"; run systemctl daemon-reload
  run rm -rf "$INSTALL_DIR" "$CONF_DIR"
  if id "$SVC_USER" >/dev/null 2>&1; then run userdel "$SVC_USER" || true; fi
  log "uninstalled"; exit 0
fi

# ---------------------------------------------------------------- validation
[ -n "$SNM_SERVER" ] || die "--server (or SNM_SERVER) is required, e.g. --server https://m.example.com"
case "$SNM_SERVER" in http://*|https://*) ;; *) die "--server must be an http(s) origin such as https://m.example.com" ;; esac
[ -n "$SNM_KEY" ] || die "--key (or SNM_KEY) is required (create a node in the admin UI to get its snmk_... key)"
printf '%s' "$SNM_KEY" | grep -Eq '^snmk_[A-Za-z0-9_-]{43}$' || die "the key has an unexpected format (expected snmk_ + 43 characters)"
if [ -n "$INTERVAL" ]; then printf '%s' "$INTERVAL" | grep -Eq '^[0-9]+$' || die "--interval must be a number of milliseconds"; fi

# ---------------------------------------------------------------- architecture / tools
case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) die "unsupported architecture: $(uname -m)" ;;
esac
if [ "$DRY_RUN" != 1 ]; then
  if ldd --version 2>&1 | grep -qi musl || [ -f /etc/alpine-release ]; then
    die "musl-based systems (Alpine) are not supported by this build"
  fi
fi
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required (coreutils)"
if command -v curl >/dev/null 2>&1; then DL="curl -fsSL --retry 3 --connect-timeout 15 -o"
elif command -v wget >/dev/null 2>&1; then DL="wget -q --tries=3 --timeout=15 -O"
else die "curl or wget is required"; fi
# reuse --proxy for the download itself
case "$PROXY" in http://*|https://*) export https_proxy="$PROXY" http_proxy="$PROXY" ;; socks5://*|socks4*://*) export ALL_PROXY="$PROXY" ;; esac

ASSET="snm-agent-$RID.tar.gz"
if [ -n "$PIN_VERSION" ]; then
  case "$PIN_VERSION" in v*) ;; *) PIN_VERSION="v$PIN_VERSION" ;; esac
  BASE="${RELEASE_BASE%/latest/download}/download/$PIN_VERSION"
else
  BASE="$RELEASE_BASE"
fi
# Scratch directory: dedicated name + guarded cleanup (never rm -rf an inherited TMP/TEMP value)
SNM_TMPDIR=$(mktemp -d "${TMPDIR:-/tmp}/snm-agent.XXXXXX")
cleanup_tmp() { case "$(basename "${SNM_TMPDIR:-}")" in snm-agent.*) rm -rf "$SNM_TMPDIR" ;; esac; }
trap cleanup_tmp EXIT
TMP="$SNM_TMPDIR"
if [ "$DRY_RUN" = 1 ]; then
  log "dry run: would download $BASE/$ASSET and $BASE/$ASSET.sha256"
  printf '#!/bin/sh\necho dry-run\n' > "$TMP/snm-agent"; chmod 755 "$TMP/snm-agent"
else
  log "downloading $BASE/$ASSET"
  $DL "$TMP/$ASSET" "$BASE/$ASSET"
  $DL "$TMP/$ASSET.sha256" "$BASE/$ASSET.sha256"
  ( cd "$TMP" && sha256sum -c "$ASSET.sha256" >/dev/null ) || die "checksum verification failed"
  tar xzf "$TMP/$ASSET" -C "$TMP"
  [ -x "$TMP/snm-agent" ] || chmod 755 "$TMP/snm-agent"
fi
NEW_VER=$("$TMP/snm-agent" --version 2>/dev/null || echo unknown)

# ---------------------------------------------------------------- user, dirs, binary (idempotent)
id "$SVC_USER" >/dev/null 2>&1 || run useradd --system --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
run mkdir -p "$INSTALL_DIR" "$CONF_DIR"
if [ -x "$INSTALL_DIR/snm-agent" ] && cmp -s "$INSTALL_DIR/snm-agent" "$TMP/snm-agent"; then
  log "binary unchanged ($NEW_VER)"
else
  run install -m 755 -o root -g root "$TMP/snm-agent" "$INSTALL_DIR/snm-agent.new"
  run mv -f "$INSTALL_DIR/snm-agent.new" "$INSTALL_DIR/snm-agent"
  log "installed binary $NEW_VER"
fi

# ---------------------------------------------------------------- environment file (0600 root; read by systemd before dropping privileges)
{
  echo "SNM_SERVER=$SNM_SERVER"
  echo "SNM_KEY=$SNM_KEY"
  [ -n "$PROXY" ] && echo "SNM_PROXY=$PROXY"
  [ -n "$NET_IF" ] && echo "SNM_NET_IF=$NET_IF"
  [ -n "$DISK_INCLUDE" ] && echo "SNM_DISK_INCLUDE=$DISK_INCLUDE"
  [ -n "$NODE_NAME_OVERRIDE" ] && echo "SNM_NAME=$NODE_NAME_OVERRIDE"
  [ -n "$INTERVAL" ] && echo "SNM_INTERVAL=$INTERVAL"
  echo "SNM_LOG_LEVEL=info"
} > "$TMP/agent.env"
if [ "$DRY_RUN" = 1 ]; then echo "--- $ENV_FILE (dry run) ---"; sed "s/^SNM_KEY=.*/SNM_KEY=<redacted>/" "$TMP/agent.env"; echo "---"; fi
run install -m 600 -o root -g root "$TMP/agent.env" "$ENV_FILE"

# ---------------------------------------------------------------- systemd unit
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
if [ "$DRY_RUN" = 1 ]; then log "dry run complete"; exit 0; fi
sleep 2
if systemctl is-active --quiet "$SERVICE"; then
  log "snm-agent is running ($NEW_VER) → $SNM_SERVER. Logs: journalctl -u $SERVICE -f"
else
  warn "service is not active; last log lines:"; journalctl -u "$SERVICE" -n 20 --no-pager || true; exit 1
fi
