#!/usr/bin/env bash
# Scaffolds a new public-dashboard theme (in this repository, under web/themes/) from the built-in "minimal" reference implementation (docs/THEMES.md).
# Usage: scripts/new-theme.sh <id> [display name] [target-dir]
#   scripts/new-theme.sh my-theme "我的主题"            -> web/themes/my-theme   (built in, shipped with the master)
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

基于内置 \`minimal\` 主题生成,位于本仓库 \`web/themes/$ID\`。开发指南与 SDK 事件/数据说明见 \`docs/THEMES.md\`。

## 本地开发

1. \`scripts/dev.sh\` 启动开发 Master(直接从 \`web/themes\` 读取,改文件后刷新即可);
2. 打开 \`http://127.0.0.1:5080/themes/$ID/\` 预览;
3. 后台 系统设置 → 大屏主题 → 启用,\`/\` 即切换为本主题。

## 发布

提交到仓库后,\`scripts/build-web.sh\` 会把它作为内置主题打进 Master 发行包(下次打 tag 即随版本发布)。
如需单独分发给别的 Master:\`scripts/pack-theme.sh web/themes/$ID\` 生成 zip,在后台 **系统设置 → 大屏主题** 上传。
EOF
echo "theme scaffold written to $TARGET"
ls -1 "$TARGET"
