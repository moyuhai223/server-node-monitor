#!/usr/bin/env bash
# Local development: starts the Master (Development, data in ./data/dev) and an agent for this machine.
# Usage: scripts/dev.sh [--no-agent]   (Ctrl+C stops both)
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/env.sh
NO_AGENT=0; [ "${1:-}" = "--no-agent" ] && NO_AGENT=1
DATA="$(pwd)/data/dev"; mkdir -p "$DATA"
PORT=${SNM_DEV_PORT:-5080}
BASE="http://127.0.0.1:$PORT"
ADMIN_PASSWORD=${SNM_ADMIN_PASSWORD:-admin123456}

echo "[dev] building solution"
dotnet build ServerNodeMonitor.slnx -c Debug -nologo -v q

echo "[dev] starting master on $BASE (data: $DATA)"
SNM_DATA_DIR="$DATA" SNM_LISTEN="$BASE" ASPNETCORE_ENVIRONMENT=Development SNM_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  SNM_PUBLIC_BASE_URL="$BASE" Snm__Dev__PublicSourceDir="$(pwd)/web/public" \
  dotnet run --project src/SNM.Master --no-build &
MASTER_PID=$!
cleanup() { echo; echo "[dev] stopping"; kill "$MASTER_PID" "${AGENT_PID:-}" 2>/dev/null || true; wait 2>/dev/null || true; }
trap cleanup EXIT INT TERM

for i in $(seq 1 60); do
  sleep 1
  if curl -fsS -o /dev/null "$BASE/healthz" 2>/dev/null; then break; fi
  if ! kill -0 "$MASTER_PID" 2>/dev/null; then echo "[dev] master exited"; exit 1; fi
done

if [ "$NO_AGENT" = 1 ]; then wait "$MASTER_PID"; exit 0; fi

KEY_FILE="$DATA/agent.key"
if [ ! -s "$KEY_FILE" ]; then
  echo "[dev] creating node 'dev-local' and saving its key to $KEY_FILE"
  TOKEN=$(curl -fsS -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' \
    -d "{\"username\":\"admin\",\"password\":\"$ADMIN_PASSWORD\"}" | $(command -v python3 || command -v python) -c 'import sys,json; print(json.load(sys.stdin)["data"]["accessToken"])')
  curl -fsS -X POST "$BASE/api/nodes" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"publicName":"dev-local","adminRemark":"scripts/dev.sh","traffic":{"limitBytes":1000000000000}}' \
    | $(command -v python3 || command -v python) -c 'import sys,json; print(json.load(sys.stdin)["data"]["agentKey"])' > "$KEY_FILE"
fi

echo "[dev] starting agent"
dotnet run --project src/SNM.Agent --no-build -- run --server "$BASE" --key "$(cat "$KEY_FILE")" --log-level debug &
AGENT_PID=$!
echo "[dev] admin: $BASE/admin/  (admin / $ADMIN_PASSWORD)   public: $BASE/   vite dev: cd web/admin && npm run dev"
wait "$MASTER_PID"
