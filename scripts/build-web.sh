#!/usr/bin/env bash
# Builds web/admin (Vite) and copies web/public + the SignalR browser bundles into the Master's wwwroot.
# Usage: scripts/build-web.sh [--out <dir>] [--skip-install]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="src/SNM.Master/wwwroot"; SKIP=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) OUT="$2"; shift ;;
    --skip-install) SKIP=1 ;;
    *) echo "unknown arg $1" >&2; exit 2 ;;
  esac
  shift
done
OUT_ABS="$(mkdir -p "$OUT" && cd "$OUT" && pwd)"

install_deps() {
  local dir="$1"
  if [ -f "$dir/package-lock.json" ]; then (cd "$dir" && npm ci --no-audit --no-fund); else (cd "$dir" && npm install --no-audit --no-fund); fi
}
if [ "$SKIP" = 0 ]; then install_deps web/admin; install_deps web/public; fi

# admin SPA -> $OUT/admin (vite.config.js reads VITE_OUT_DIR)
rm -rf "$OUT_ABS/admin"
(cd web/admin && VITE_OUT_DIR="$OUT_ABS/admin" npm run build)

# public dashboard -> $OUT root
mkdir -p "$OUT_ABS/vendor" "$OUT_ABS/css" "$OUT_ABS/js"
cp web/public/index.html web/public/favicon.svg "$OUT_ABS/"
cp web/public/css/*.css "$OUT_ABS/css/"
cp web/public/js/*.js "$OUT_ABS/js/"
cp web/public/node_modules/@microsoft/signalr/dist/browser/signalr.min.js "$OUT_ABS/vendor/"
cp web/public/node_modules/@microsoft/signalr-protocol-msgpack/dist/browser/signalr-protocol-msgpack.min.js "$OUT_ABS/vendor/"
# keep the development copy in sync so the Master can serve web/public directly
mkdir -p web/public/vendor
cp "$OUT_ABS/vendor/"*.js web/public/vendor/
echo "web assets written to $OUT_ABS"
