#!/usr/bin/env bash
# Builds web/admin (Vite), the theme SDK bundle (web/sdk/dist -> /vendor) and copies built-in themes into the Master's wwwroot.
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
# Node on Windows does not understand MSYS paths (/c/...): hand Vite a native path
if command -v cygpath >/dev/null 2>&1; then OUT_NODE="$(cygpath -w "$OUT_ABS")"; else OUT_NODE="$OUT_ABS"; fi

install_deps() {
  local dir="$1"
  if [ -f "$dir/package-lock.json" ]; then (cd "$dir" && npm ci --no-audit --no-fund); else (cd "$dir" && npm install --no-audit --no-fund); fi
}
if [ "$SKIP" = 0 ]; then install_deps web/admin; install_deps web/sdk; fi

# admin SPA -> $OUT/admin (vite.config.js reads VITE_OUT_DIR)
rm -rf "$OUT_ABS/admin"
(cd web/admin && MSYS_NO_PATHCONV=1 VITE_OUT_DIR="$OUT_NODE/admin" npm run build)

# theme SDK -> web/sdk/dist (served at /vendor in development) and $OUT/vendor
mkdir -p web/sdk/dist
cp web/sdk/snm-client.js web/sdk/dist/
cp web/sdk/node_modules/@microsoft/signalr/dist/browser/signalr.min.js web/sdk/dist/
cp web/sdk/node_modules/@microsoft/signalr-protocol-msgpack/dist/browser/signalr-protocol-msgpack.min.js web/sdk/dist/
rm -rf "$OUT_ABS/vendor"; mkdir -p "$OUT_ABS/vendor"
cp web/sdk/dist/* "$OUT_ABS/vendor/"

# built-in themes -> $OUT/themes/<id> (each folder has a theme.json)
rm -rf "$OUT_ABS/themes"; mkdir -p "$OUT_ABS/themes"
for t in web/themes/*/; do
  id=$(basename "$t")
  [ -f "$t/theme.json" ] || { echo "skip $t (no theme.json)"; continue; }
  mkdir -p "$OUT_ABS/themes/$id"
  (cd "$t" && find . -type f ! -path './node_modules/*' ! -name 'package-lock.json' ! -name 'package.json' -exec cp --parents {} "$OUT_ABS/themes/$id/" \;)
done
# legacy files from older builds
rm -f "$OUT_ABS/index.html" "$OUT_ABS/favicon.svg"; rm -rf "$OUT_ABS/css" "$OUT_ABS/js"
echo "web assets written to $OUT_ABS (themes: $(ls "$OUT_ABS/themes" | tr '\n' ' '))"
