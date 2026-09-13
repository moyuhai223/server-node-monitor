#!/usr/bin/env bash
# Scaffolds a new public-dashboard theme from the built-in "minimal" reference implementation (docs/THEMES.md).
# Usage: scripts/new-theme.sh <id> [display name] [target-dir]
#   scripts/new-theme.sh my-theme "我的主题"            -> web/themes/my-theme   (built in, shipped with the master)
#   scripts/new-theme.sh my-theme "我的主题" ~/src/snm-theme-my-theme   (standalone repo: pack + upload)
set -euo pipefail
cd "$(dirname "$0")/.."
ID=${1:-}; NAME=${2:-$ID}; TARGET=${3:-"web/themes/$ID"}
printf '%s' "$ID" | grep -Eq '^[a-z0-9][a-z0-9-]{1,31}$' || { echo "usage: $0 <id: 2-32 lowercase letters/digits/-> [name] [target-dir]" >&2; exit 2; }
[ -d "$TARGET" ] && { echo "$TARGET already exists" >&2; exit 2; }
[ "$ID" != default ] && [ "$ID" != minimal ] || { echo "$ID is a built-in theme id" >&2; exit 2; }

mkdir -p "$TARGET"
cp web/themes/minimal/index.html web/themes/minimal/style.css web/themes/minimal/theme.js "$TARGET/"
sed -i "s/createClient({ theme: 'minimal' })/createClient({ theme: '$ID' })/" "$TARGET/theme.js"
sed -i "s/minimal theme/$ID theme/" "$TARGET/index.html"
PY=$(command -v python3 || command -v python)
"$PY" - "$TARGET" "$ID" "$NAME" <<'EOF'
import json, sys, io, os
target, tid, name = sys.argv[1:4]
manifest = {"id": tid, "name": name, "version": "0.1.0", "author": "", "description": "", "homepage": "", "entry": "index.html", "preview": "", "sdk": 1}
io.open(os.path.join(target, "theme.json"), "w", encoding="utf-8", newline="\n").write(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
EOF
cat > "$TARGET/README.md" <<EOF
# $NAME (\`$ID\`) — Server Node Monitor 大屏主题

基于内置 \`minimal\` 主题生成。开发指南与 SDK 事件/数据说明:
https://github.com/moyuhai223/server-node-monitor/blob/main/docs/THEMES.md

## 本地开发

1. 把本目录放到(或软链到)Master 仓库的 \`web/themes/$ID\`,用 \`scripts/dev.sh\` 启动开发 Master;
2. 打开 \`http://127.0.0.1:5080/themes/$ID/\` 预览(无需重启,改文件后刷新即可);
3. 后台 系统设置 → 大屏主题 → 启用,\`/\` 即切换为本主题。

## 打包 / 发布

\`\`\`bash
scripts/pack-theme.sh <本目录>        # 生成 snm-theme-$ID-<version>.zip
\`\`\`

把 zip 上传到后台 **系统设置 → 大屏主题**,或作为 GitHub Release 资产发布给其他人。
EOF
echo "theme scaffold written to $TARGET"
ls -1 "$TARGET"
