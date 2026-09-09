#!/usr/bin/env bash
# Native AOT gate for the agent without a platform linker (docs/BRIEF.md 1): ILC must compile with zero IL warnings.
# Usage: scripts/ilc-check.sh [rid]     (default rid: win-arm64 on Windows hosts, linux-x64 elsewhere)
set -uo pipefail
cd "$(dirname "$0")/.."
source scripts/env.sh
RID=${1:-}
if [ -z "$RID" ]; then
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) RID=$([ "$(uname -m)" = "aarch64" ] || uname -a | grep -qi arm64 && echo win-arm64 || echo win-x64) ;;
    Darwin) RID=$([ "$(uname -m)" = "arm64" ] && echo osx-arm64 || echo osx-x64) ;;
    *) RID=$([ "$(uname -m)" = "aarch64" ] && echo linux-arm64 || echo linux-x64) ;;
  esac
fi
LOG=$(mktemp)
echo "[ilc] publishing SNM.Agent for $RID (link step may fail without a platform linker; that is fine)"
dotnet publish src/SNM.Agent/SNM.Agent.csproj -c Release -r "$RID" -p:PublishAot=true -p:IlcUseEnvironmentalTools=true -p:TrimmerSingleWarn=false > "$LOG" 2>&1
CODE=$?
WARNINGS=$(grep -c "warning IL" "$LOG" || true)
if ! grep -q "Generating native code" "$LOG"; then
  echo "[ilc] FAIL: ILC did not run"; grep -E "error" "$LOG" | head -20; rm -f "$LOG"; exit 1
fi
if [ "$WARNINGS" != "0" ]; then
  echo "[ilc] FAIL: $WARNINGS IL warning(s)"; grep "warning IL" "$LOG" | sort -u | head -40; rm -f "$LOG"; exit 1
fi
if [ $CODE -eq 0 ]; then
  echo "[ilc] PASS: native binary produced with 0 IL warnings"
else
  echo "[ilc] PASS: ILC compiled with 0 IL warnings (final link skipped/failed: no platform linker on this host)"
fi
rm -f "$LOG"
