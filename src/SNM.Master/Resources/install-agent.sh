#!/usr/bin/env bash
# Server Node Monitor agent installer
# Generated {{GENERATED_AT}} by master {{MASTER_VERSION}} for node "{{NODE_NAME}}"
# Usage: install.sh [--proxy URL] [--net-if a,b] [--version vX.Y.Z] | uninstall
#   curl -fsSL <url> | sudo bash
#   curl -fsSL <url> | sudo bash -s -- --proxy socks5://10.0.0.1:1080
#   curl -fsSL <url> | sudo bash -s -- uninstall
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

if [ "$DRY_RUN" != 1 ]; then
  [ "$(id -u)" -eq 0 ] || die "please run as root (sudo)"
  command -v systemctl >/dev/null 2>&1 || die "systemd is required"
fi
[ "$(uname -s)" = Linux ] || die "this installer supports Linux only; use ?os=windows for the PowerShell installer"

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
if [ "$DRY_RUN" = 1 ]; then log "dry run complete"; exit 0; fi
sleep 2
if systemctl is-active --quiet "$SERVICE"; then
  log "snm-agent is running ($NEW_VER). Logs: journalctl -u $SERVICE -f"
else
  warn "service is not active; last log lines:"; journalctl -u "$SERVICE" -n 20 --no-pager || true; exit 1
fi
