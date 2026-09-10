#!/usr/bin/env bash
# Server Node Monitor - master (server) one-click installer for Linux x86_64 / aarch64 with systemd.
#
#   curl -fsSL https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-master.sh | sudo bash -s -- --public-url https://m.example.com
#   bash <(curl -fsSL https://raw.githubusercontent.com/moyuhai223/server-node-monitor/main/deploy/install-master.sh)
#
# Commands
#   install              first install or in-place upgrade (default)
#   upgrade              alias of install (keeps /etc/snm-master/master.env and the data directory)
#   uninstall [--purge]  remove the service and program; --purge also deletes data, config and the service user
#   status               service state, installed version and health
# Options
#   --version vX.Y.Z     pin a release (default: latest GitHub release)
#   --port N             listen port on 127.0.0.1 (default 5080)
#   --listen URL         full Kestrel URL, overrides --port (e.g. http://0.0.0.0:5080)
#   --public-url URL     public address used in agent install scripts, e.g. https://m.example.com
#   --admin-user NAME    initial admin user (default admin; first install only)
#   --admin-password PW  initial admin password (default: generated and printed once; first install only)
#   --data-dir DIR       data directory (default /var/lib/snm-master; first install only)
#   --timezone TZ        IANA time zone seed (default: system time zone; first install only)
#   --nginx DOMAIN       also write an nginx reverse-proxy site for DOMAIN (plain HTTP; add TLS with certbot)
#   --yes                non-interactive: never prompt, use defaults
#   --dry-run            download and verify, but only print privileged actions
set -euo pipefail

REPO="moyuhai223/server-node-monitor"
APP_DIR=/opt/snm-master
CONF_DIR=/etc/snm-master
ENV_FILE=$CONF_DIR/master.env
UNIT_FILE=/etc/systemd/system/snm-master.service
SERVICE=snm-master
SVC_USER=snm-master
DATA_DIR_DEFAULT=/var/lib/snm-master

ACTION=install
VERSION=""; PORT=5080; LISTEN=""; PUBLIC_URL=""; ADMIN_USER=admin; ADMIN_PASSWORD=""; DATA_DIR=""; TIMEZONE=""
NGINX_DOMAIN=""; YES=0; PURGE=0; DRY_RUN=${SNM_DRY_RUN:-0}; RID=${SNM_RID:-}
# Scratch directory: a dedicated global (never the environment's TMP/TEMP) so the EXIT trap can only remove our own snm-master.* dir.
SNM_TMPDIR=""
cleanup_tmp() { case "$(basename "${SNM_TMPDIR:-}")" in snm-master.*) rm -rf "$SNM_TMPDIR" ;; esac; }
trap cleanup_tmp EXIT

log()  { printf '\033[1;32m[snm-master]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[snm-master]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[snm-master] ERROR:\033[0m %s\n' "$*" >&2; exit 1; }
run()  { if [ "$DRY_RUN" = 1 ]; then echo "+ $*"; else "$@"; fi; }

usage() {
  cat <<'EOF'
Server Node Monitor master installer

  install-master.sh [install|upgrade|uninstall|status] [options]

Options:
  --version vX.Y.Z     pin a release (default: latest)
  --port N             listen port on 127.0.0.1 (default 5080)
  --listen URL         full Kestrel URL, overrides --port
  --public-url URL     public address, e.g. https://m.example.com
  --admin-user NAME    initial admin user (default admin)
  --admin-password PW  initial admin password (default: generated)
  --data-dir DIR       data directory (default /var/lib/snm-master)
  --timezone TZ        IANA time zone (default: system)
  --nginx DOMAIN       write an nginx site for DOMAIN (HTTP; run certbot for TLS)
  --purge              with uninstall: also delete data, config and the service user
  --yes                non-interactive
  --dry-run            print privileged actions instead of executing them
EOF
}

# ---------------------------------------------------------------- arguments
while [ $# -gt 0 ]; do
  case "$1" in
    install|upgrade) ACTION=install ;;
    uninstall|status) ACTION=$1 ;;
    --version=*) VERSION=${1#*=} ;;         --version) VERSION=${2:-}; shift ;;
    --port=*) PORT=${1#*=} ;;               --port) PORT=${2:-}; shift ;;
    --listen=*) LISTEN=${1#*=} ;;           --listen) LISTEN=${2:-}; shift ;;
    --public-url=*) PUBLIC_URL=${1#*=} ;;   --public-url) PUBLIC_URL=${2:-}; shift ;;
    --admin-user=*) ADMIN_USER=${1#*=} ;;   --admin-user) ADMIN_USER=${2:-}; shift ;;
    --admin-password=*) ADMIN_PASSWORD=${1#*=} ;; --admin-password) ADMIN_PASSWORD=${2:-}; shift ;;
    --data-dir=*) DATA_DIR=${1#*=} ;;       --data-dir) DATA_DIR=${2:-}; shift ;;
    --timezone=*) TIMEZONE=${1#*=} ;;       --timezone) TIMEZONE=${2:-}; shift ;;
    --nginx=*) NGINX_DOMAIN=${1#*=} ;;      --nginx) NGINX_DOMAIN=${2:-}; shift ;;
    --purge) PURGE=1 ;;
    --yes|-y) YES=1 ;;
    --dry-run) DRY_RUN=1 ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
  shift
done

# ---------------------------------------------------------------- helpers
have() { command -v "$1" >/dev/null 2>&1; }

dl() { # url dest
  if have curl; then curl -fsSL --retry 3 --connect-timeout 15 -o "$2" "$1"
  elif have wget; then wget -q --tries=3 --timeout=15 -O "$2" "$1"
  else die "curl or wget is required"; fi
}

interactive() { [ "$YES" != 1 ] && { : < /dev/tty; } 2>/dev/null; }

ask() { # var prompt default
  local val=""
  if ! interactive; then printf -v "$1" '%s' "$3"; return; fi
  printf '%s [%s]: ' "$2" "$3" > /dev/tty
  IFS= read -r val < /dev/tty || val=""
  printf -v "$1" '%s' "${val:-$3}"
}

detect_tz() {
  local tz=""
  have timedatectl && tz=$(timedatectl show -p Timezone --value 2>/dev/null || true)
  [ -n "$tz" ] || tz=$(cat /etc/timezone 2>/dev/null || true)
  if [ -z "$tz" ] && [ -L /etc/localtime ]; then tz=$(readlink -f /etc/localtime 2>/dev/null | sed -E 's#.*/zoneinfo/##'); fi
  printf '%s' "$tz"
}

gen_password() {
  local pw
  pw=$(head -c 96 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')
  printf '%s' "${pw:0:20}"
}

port_of() { # listen url -> port
  local p
  p=$(printf '%s' "$1" | sed -nE 's#^[a-z]+://[^/]*:([0-9]+)/?$#\1#p')
  printf '%s' "${p:-80}"
}

env_get() { sed -n "s/^$1=//p" "$ENV_FILE" 2>/dev/null | head -1; }

os_check() {
  if [ "$DRY_RUN" != 1 ]; then
    [ "$(uname -s)" = Linux ] || die "this installer supports Linux only (for other hosts use the Docker image, see deploy/docker)"
    [ "$(id -u)" -eq 0 ] || die "please run as root (sudo)"
    have systemctl || die "systemd is required"
    if ldd --version 2>&1 | grep -qi musl || [ -f /etc/alpine-release ]; then
      die "musl-based systems (Alpine) are not supported by the published build; use the Docker image"
    fi
  fi
  if [ -z "$RID" ]; then
    case "$(uname -m)" in
      x86_64|amd64) RID=linux-x64 ;;
      aarch64|arm64) RID=linux-arm64 ;;
      *) die "unsupported architecture: $(uname -m)" ;;
    esac
  fi
  have tar || die "tar is required"
  have sha256sum || die "sha256sum is required (coreutils)"
}

resolve_version() {
  if [ -z "$VERSION" ]; then
    if have curl; then
      VERSION=$(curl -fsSIL -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest" 2>/dev/null | sed -E 's#.*/tag/##')
    else
      VERSION=$(wget -qO- "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)
    fi
    [ -n "$VERSION" ] && [ "$VERSION" != "latest" ] || die "cannot resolve the latest release; pass --version vX.Y.Z"
  fi
  case "$VERSION" in v*) ;; *) VERSION="v$VERSION" ;; esac
  BASE="https://github.com/$REPO/releases/download/$VERSION"
}

# ---------------------------------------------------------------- status / uninstall
do_status() {
  echo "installed version: $(cat "$APP_DIR/VERSION" 2>/dev/null || echo '(not installed)')"
  if have systemctl; then systemctl status "$SERVICE" --no-pager -l 2>/dev/null | head -12 || true; fi
  local listen port
  listen=$(env_get SNM_LISTEN); port=$(port_of "${listen:-http://127.0.0.1:5080}")
  if have curl; then
    echo "health: $(curl -fsS "http://127.0.0.1:$port/api/health" 2>/dev/null || echo 'not responding')"
  fi
}

do_uninstall() {
  if have systemctl; then
    run systemctl disable --now "$SERVICE" 2>/dev/null || true
  fi
  run rm -f "$UNIT_FILE"
  have systemctl && run systemctl daemon-reload
  run rm -rf "$APP_DIR" "$APP_DIR.old" "$APP_DIR.new"
  for f in /etc/nginx/conf.d/snm-master.conf /etc/nginx/sites-enabled/snm-master.conf /etc/nginx/sites-available/snm-master.conf; do
    if [ -f "$f" ]; then run rm -f "$f"; NGINX_TOUCHED=1; fi
  done
  if [ "${NGINX_TOUCHED:-0}" = 1 ] && have nginx && nginx -t >/dev/null 2>&1; then run systemctl reload nginx || true; fi
  if [ "$PURGE" = 1 ]; then
    local data; data=$(env_get SNM_DATA_DIR); data=${data:-$DATA_DIR_DEFAULT}
    run rm -rf "$data" "$CONF_DIR"
    id "$SVC_USER" >/dev/null 2>&1 && run userdel "$SVC_USER" || true
    log "uninstalled (data, config and user removed)"
  else
    log "uninstalled; data and config kept (use 'uninstall --purge' to delete them)"
  fi
}

# ---------------------------------------------------------------- install / upgrade
do_install() {
  resolve_version
  SNM_TMPDIR=$(mktemp -d "${TMPDIR:-/tmp}/snm-master.XXXXXX")
  local TMP="$SNM_TMPDIR"
  local ASSET="snm-master-$RID.tar.gz"
  log "downloading $BASE/$ASSET"
  dl "$BASE/$ASSET" "$TMP/$ASSET"
  dl "$BASE/$ASSET.sha256" "$TMP/$ASSET.sha256"
  ( cd "$TMP" && sha256sum -c "$ASSET.sha256" >/dev/null ) || die "checksum verification failed"
  mkdir -p "$TMP/app"
  tar xzf "$TMP/$ASSET" -C "$TMP/app"
  [ -f "$TMP/app/snm-master" ] || die "snm-master binary not found in the archive"
  chmod 755 "$TMP/app/snm-master"
  rm -f "$TMP/app/appsettings.Development.json"
  printf '%s\n' "$VERSION" > "$TMP/app/VERSION"
  log "verified $ASSET ($VERSION)"

  local EXISTING=0
  [ -f "$ENV_FILE" ] && EXISTING=1
  local INSTALLED_VERSION; INSTALLED_VERSION=$(cat "$APP_DIR/VERSION" 2>/dev/null || true)

  if [ "$EXISTING" = 1 ]; then
    # upgrade: everything comes from the existing configuration
    LISTEN=$(env_get SNM_LISTEN); LISTEN=${LISTEN:-http://127.0.0.1:5080}
    DATA_DIR=$(env_get SNM_DATA_DIR); DATA_DIR=${DATA_DIR:-$DATA_DIR_DEFAULT}
    PUBLIC_URL=$(env_get SNM_PUBLIC_BASE_URL)
    log "existing installation found (${INSTALLED_VERSION:-unknown}); upgrading to $VERSION, keeping $ENV_FILE and $DATA_DIR"
  else
    [ -n "$LISTEN" ] || LISTEN="http://127.0.0.1:$PORT"
    DATA_DIR=${DATA_DIR:-$DATA_DIR_DEFAULT}
    if [ -z "$PUBLIC_URL" ]; then
      local def=""; [ -n "$NGINX_DOMAIN" ] && def="http://$NGINX_DOMAIN"
      ask PUBLIC_URL "Public base URL for agent install scripts (e.g. https://m.example.com, empty = set later)" "$def"
    fi
    PUBLIC_URL=${PUBLIC_URL%/}
    if [ -n "$PUBLIC_URL" ]; then
      case "$PUBLIC_URL" in http://*|https://*) ;; *) die "--public-url must start with http:// or https://" ;; esac
    fi
    if [ -z "$ADMIN_PASSWORD" ]; then
      if interactive; then ask ADMIN_PASSWORD "Initial admin password (empty = generate)" ""; fi
      [ -n "$ADMIN_PASSWORD" ] || { ADMIN_PASSWORD=$(gen_password); GENERATED_PW=1; }
    fi
    [ -n "$TIMEZONE" ] || TIMEZONE=$(detect_tz)
  fi
  local PORT_EFFECTIVE; PORT_EFFECTIVE=$(port_of "$LISTEN")

  # user and directories
  if ! id "$SVC_USER" >/dev/null 2>&1; then
    run useradd --system --home-dir "$DATA_DIR" --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
  fi
  run install -d -m 750 -o "$SVC_USER" -g "$SVC_USER" "$DATA_DIR" "$DATA_DIR/.net" "$DATA_DIR/backups"

  # stop, back up the database on upgrade
  if have systemctl && systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
    log "stopping $SERVICE"
    run systemctl stop "$SERVICE"
  fi
  if [ -f "$DATA_DIR/snm.db" ]; then
    local bdir="$DATA_DIR/backups/pre-upgrade-$(date +%Y%m%d-%H%M%S)-$VERSION"
    run install -d -m 750 -o "$SVC_USER" -g "$SVC_USER" "$bdir"
    for f in "$DATA_DIR"/snm.db "$DATA_DIR"/snm.db-wal "$DATA_DIR"/snm.db-shm; do [ -f "$f" ] && run cp -a "$f" "$bdir/"; done
    log "database backed up to $bdir"
  fi

  # program files (atomic swap, previous version kept as $APP_DIR.old)
  run rm -rf "$APP_DIR.new" "$APP_DIR.old"
  run cp -a "$TMP/app" "$APP_DIR.new"
  run chown -R root:root "$APP_DIR.new"
  if [ -d "$APP_DIR" ]; then run mv "$APP_DIR" "$APP_DIR.old"; fi
  run mv "$APP_DIR.new" "$APP_DIR"

  # configuration (first install only)
  if [ "$EXISTING" = 0 ]; then
    {
      echo "# Server Node Monitor master - generated $(date -u +%Y-%m-%dT%H:%M:%SZ) by install-master.sh"
      echo "# Friendly SNM_* variables map onto the Snm configuration section (docs/DEPLOY.md)."
      echo "SNM_LISTEN=$LISTEN"
      echo "SNM_DATA_DIR=$DATA_DIR"
      echo "SNM_PUBLIC_BASE_URL=$PUBLIC_URL"
      echo "SNM_KNOWN_PROXIES=127.0.0.1,::1"
      [ -n "$TIMEZONE" ] && echo "SNM_TIMEZONE=$TIMEZONE"
      echo "# Initial admin account: used on the first start only (the password can be removed afterwards)"
      echo "SNM_ADMIN_USER=$ADMIN_USER"
      echo "SNM_ADMIN_PASSWORD=$ADMIN_PASSWORD"
      echo "ASPNETCORE_ENVIRONMENT=Production"
      echo "DOTNET_gcServer=0"
      echo "DOTNET_GCHeapHardLimit=0x20000000"
    } > "$TMP/master.env"
    run install -d -m 750 "$CONF_DIR"
    run install -m 600 -o root -g root "$TMP/master.env" "$ENV_FILE"
  fi

  # systemd unit (always regenerated from the current configuration)
  local protect_home=true
  case "$DATA_DIR" in /home/*|/root/*) protect_home=false ;; esac
  cat > "$TMP/unit" <<UNIT
[Unit]
Description=Server Node Monitor Master
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SVC_USER
Group=$SVC_USER
WorkingDirectory=$APP_DIR
EnvironmentFile=$ENV_FILE
Environment=HOME=$DATA_DIR
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=$DATA_DIR/.net
ExecStart=$APP_DIR/snm-master
Restart=always
RestartSec=3
KillSignal=SIGTERM
TimeoutStopSec=30
LimitNOFILE=65536
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=$protect_home
PrivateTmp=true
ReadWritePaths=$DATA_DIR
ProtectKernelTunables=true
ProtectControlGroups=true
RestrictSUIDSGID=true

[Install]
WantedBy=multi-user.target
UNIT
  run install -m 644 "$TMP/unit" "$UNIT_FILE"
  if have systemctl; then
    run systemctl daemon-reload
    run systemctl enable "$SERVICE" >/dev/null 2>&1 || true
    run systemctl restart "$SERVICE"
  fi

  # optional nginx site
  [ -n "$NGINX_DOMAIN" ] && write_nginx "$PORT_EFFECTIVE"

  # health
  if [ "$DRY_RUN" != 1 ] && have curl; then
    local ok=0 i
    for i in $(seq 1 30); do
      sleep 1
      if curl -fsS -o /dev/null "http://127.0.0.1:$PORT_EFFECTIVE/healthz" 2>/dev/null; then ok=1; break; fi
    done
    if [ "$ok" != 1 ]; then
      warn "the service did not answer on http://127.0.0.1:$PORT_EFFECTIVE/healthz within 30 s; last log lines:"
      journalctl -u "$SERVICE" -n 30 --no-pager || true
      exit 1
    fi
  fi

  echo
  log "Server Node Monitor master $VERSION is installed and running"
  echo "  service    : systemctl status $SERVICE     logs: journalctl -u $SERVICE -f"
  echo "  listen     : $LISTEN"
  echo "  admin UI   : http://127.0.0.1:$PORT_EFFECTIVE/admin/   public dashboard: http://127.0.0.1:$PORT_EFFECTIVE/"
  [ -n "$PUBLIC_URL" ] && echo "  public URL : $PUBLIC_URL  (admin: $PUBLIC_URL/admin/)"
  echo "  config     : $ENV_FILE      data: $DATA_DIR      program: $APP_DIR (previous: $APP_DIR.old)"
  if [ "$EXISTING" = 0 ]; then
    echo "  admin user : $ADMIN_USER"
    if [ "${GENERATED_PW:-0}" = 1 ]; then echo "  password   : $ADMIN_PASSWORD   (generated; shown only once - change it in 系统设置 → 安全)"; else echo "  password   : (as provided)"; fi
  fi
  echo
  echo "Next steps:"
  if [ -n "$NGINX_DOMAIN" ]; then
    echo "  1. TLS: certbot --nginx -d $NGINX_DOMAIN   then set 系统设置 → 站点 → 公开地址 to https://$NGINX_DOMAIN"
  else
    echo "  1. Put Nginx / 1Panel / Caddy in front of $LISTEN with WebSocket support on /hubs/ (template: deploy/nginx/snm.conf)"
  fi
  echo "  2. 系统设置 → 站点: confirm 公开地址 (used in agent install scripts)"
  echo "  3. 节点管理 → 新建节点 → 安装脚本: run the one-liner on each server"
}

write_nginx() { # port
  local port=$1 target
  if ! have nginx; then warn "nginx is not installed; skipping --nginx (see deploy/nginx/snm.conf)"; return; fi
  if [ -d /etc/nginx/conf.d ]; then target=/etc/nginx/conf.d/snm-master.conf
  elif [ -d /etc/nginx/sites-enabled ]; then target=/etc/nginx/sites-enabled/snm-master.conf
  else warn "no /etc/nginx/conf.d or sites-enabled; skipping --nginx"; return; fi
  cat > "$SNM_TMPDIR/nginx.conf" <<EOF
# Server Node Monitor - generated by install-master.sh (add TLS with: certbot --nginx -d $NGINX_DOMAIN)
server {
    listen 80;
    listen [::]:80;
    server_name $NGINX_DOMAIN;
    client_max_body_size 1m;

    location /hubs/ {
        proxy_pass http://127.0.0.1:$port;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host \$host;
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;
        proxy_buffering off;
    }

    location / {
        proxy_pass http://127.0.0.1:$port;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host \$host;
        proxy_read_timeout 90s;
    }
}
EOF
  [ -f "$target" ] && run cp -a "$target" "$target.bak.$(date +%s)"
  run install -m 644 "$SNM_TMPDIR/nginx.conf" "$target"
  if run nginx -t; then run systemctl reload nginx; log "nginx site written to $target"; else warn "nginx -t failed; fix $target and reload nginx"; fi
}

# ---------------------------------------------------------------- main
case "$ACTION" in
  status) do_status ;;
  uninstall) os_check; do_uninstall ;;
  install) os_check; do_install ;;
esac
