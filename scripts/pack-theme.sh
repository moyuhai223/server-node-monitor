#!/usr/bin/env bash
# Packs a theme directory into a zip that the master accepts at 系统设置 → 大屏主题 → 上传 (docs/THEMES.md).
# Usage: scripts/pack-theme.sh <theme-dir> [output.zip]
set -euo pipefail
DIR=${1:-}
[ -n "$DIR" ] && [ -f "$DIR/theme.json" ] || { echo "usage: $0 <theme-dir with theme.json> [output.zip]" >&2; exit 2; }
ID=$(sed -n 's/.*"id" *: *"\([^"]*\)".*/\1/p' "$DIR/theme.json" | head -1)
VER=$(sed -n 's/.*"version" *: *"\([^"]*\)".*/\1/p' "$DIR/theme.json" | head -1)
[ -n "$ID" ] || { echo "theme.json has no id" >&2; exit 2; }
OUT=${2:-"snm-theme-$ID-${VER:-0.0.0}.zip"}
PY=$(command -v python3 || command -v python) || { echo "python is required" >&2; exit 2; }
"$PY" - "$DIR" "$OUT" <<'EOF'
import os, sys, zipfile
src, out = sys.argv[1], sys.argv[2]
allowed = {'.html','.htm','.css','.js','.mjs','.json','.map','.svg','.png','.jpg','.jpeg','.gif','.webp','.avif','.ico','.woff','.woff2','.ttf','.otf','.eot','.txt','.md','.webmanifest','.mp4','.webm','.mp3','.wasm'}
count = 0
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for root, dirs, files in os.walk(src):
        dirs[:] = [d for d in dirs if d not in ('node_modules', '.git', 'dist')]
        for f in files:
            p = os.path.join(root, f); rel = os.path.relpath(p, src).replace(os.sep, '/')
            if os.path.splitext(f)[1].lower() not in allowed: print('skip (type not allowed):', rel); continue
            z.write(p, rel); count += 1
print(f'{out}: {count} files')
EOF
