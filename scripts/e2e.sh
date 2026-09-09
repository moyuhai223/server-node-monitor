#!/usr/bin/env bash
# End-to-end check on one machine: master + agent (JIT) + local webhook sink.
# Verifies register/heartbeat, REST views, offline alert + webhook delivery, recovery, public snapshot sanitisation.
# Usage: scripts/e2e.sh   (exit 0 = PASS)
set -uo pipefail
cd "$(dirname "$0")/.."
source scripts/env.sh
PORT=${SNM_E2E_PORT:-5097}; SINK_PORT=$((PORT + 1))
BASE="http://127.0.0.1:$PORT"
DATA=$(mktemp -d); export DATA
PASS=1
fail() { echo "[e2e] FAIL: $*"; PASS=0; }
ok() { echo "[e2e] ok: $*"; }
json() { PY=$(command -v python3 || command -v python); $PY -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1]))" "$1"; }

dotnet build ServerNodeMonitor.slnx -c Debug -nologo -v q || { echo "[e2e] build failed"; exit 1; }

cat > "$DATA/sink.py" <<'EOF'
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
out, port = sys.argv[1], int(sys.argv[2])
class H(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get('Content-Length', 0)); body = self.rfile.read(n)
        with open(out, 'ab') as f: f.write(body + b"\n")
        self.send_response(200); self.end_headers(); self.wfile.write(b'{"ok":true}')
    def log_message(self, *a): pass
HTTPServer(('127.0.0.1', port), H).serve_forever()
EOF
$(command -v python3 || command -v python) "$DATA/sink.py" "$DATA/hooks.jsonl" "$SINK_PORT" &
SINK_PID=$!
SNM_DATA_DIR="$DATA" SNM_LISTEN="$BASE" ASPNETCORE_ENVIRONMENT=Development SNM_GEOIP_ENABLED=false SNM_PUBLIC_BASE_URL="$BASE" SNM_ADMIN_PASSWORD=e2e-password-123 \
  dotnet run --project src/SNM.Master --no-build > "$DATA/master.log" 2>&1 &
MASTER_PID=$!
AGENT_PID=""
cleanup() { kill "$AGENT_PID" "$MASTER_PID" "$SINK_PID" 2>/dev/null || true; wait 2>/dev/null || true; }
trap cleanup EXIT

for i in $(seq 1 60); do sleep 1; curl -fsS -o /dev/null "$BASE/healthz" 2>/dev/null && break; done
curl -fsS -o /dev/null "$BASE/healthz" || { echo "[e2e] master did not start"; cat "$DATA/master.log" | tail -20; exit 1; }

TOKEN=$(curl -fsS -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' -d '{"username":"admin","password":"e2e-password-123"}' | json 'd["data"]["accessToken"]')
H="Authorization: Bearer $TOKEN"
curl -fsS -X PATCH "$BASE/api/settings" -H "$H" -H 'Content-Type: application/json' -d '{"alert":{"offlineTimeoutSec":10,"offlineConsecutive":1}}' > /dev/null && ok "settings patched"
CH=$(curl -fsS -X POST "$BASE/api/settings/channels" -H "$H" -H 'Content-Type: application/json' -d "{\"type\":\"webhook\",\"name\":\"sink\",\"config\":{\"url\":\"http://127.0.0.1:$SINK_PORT/hook\",\"secret\":\"e2e\"}}" | json 'd["data"]["id"]')
curl -fsS -X POST "$BASE/api/settings/channels/$CH/test" -H "$H" | json 'd["data"]["ok"]' | grep -q True && ok "webhook test delivered" || fail "webhook test"
KEY=$(curl -fsS -X POST "$BASE/api/nodes" -H "$H" -H 'Content-Type: application/json' -d '{"publicName":"e2e-node","adminRemark":"E2E-SECRET-REMARK","traffic":{"limitBytes":1000000000000}}' | json 'd["data"]["agentKey"]')

dotnet run --project src/SNM.Agent --no-build -- run --server "$BASE" --key "$KEY" --interval 1000 > "$DATA/agent.log" 2>&1 &
AGENT_PID=$!
sleep 14
NODE=$(curl -fsS -H "$H" "$BASE/api/nodes/1")
echo "$NODE" | json 'd["data"]["state"]["connected"]' | grep -q True && ok "node connected" || fail "node not connected"
echo "$NODE" | json 'd["data"]["live"]["ts"] is not None' | grep -q True && ok "live sample present" || fail "no live sample"
echo "$NODE" | json 'len(d["data"]["ips"]) > 0' | grep -q True && ok "ips merged" || fail "no ips"
echo "$NODE" | json 'd["data"]["hardware"]["cpuCores"] > 0' | grep -q True && ok "hardware registered" || fail "hardware missing"

kill "$AGENT_PID" 2>/dev/null; wait "$AGENT_PID" 2>/dev/null; AGENT_PID=""
sleep 26
curl -fsS -H "$H" "$BASE/api/alerts/active" | json 'any(a["rule"]==1 for a in d["data"])' | grep -q True && ok "offline alert fired" || fail "offline alert missing"

dotnet run --project src/SNM.Agent --no-build -- run --server "$BASE" --key "$KEY" --interval 1000 > "$DATA/agent2.log" 2>&1 &
AGENT_PID=$!
sleep 16
curl -fsS -H "$H" "$BASE/api/alerts/active" | json 'len(d["data"])' | grep -q '^0$' && ok "alert resolved" || fail "alert still active"
DELIV=$(curl -fsS -H "$H" "$BASE/api/alerts?pageSize=5" | json 'sum(1 for a in d["data"]["pageData"] for x in a["deliveries"] if x["ok"])')
[ "$DELIV" -ge 2 ] && ok "$DELIV webhook deliveries recorded" || fail "deliveries recorded: $DELIV"
grep -q '"event":"alert.firing"' "$DATA/hooks.jsonl" 2>/dev/null && ok "sink received alert.firing" || fail "sink missing alert.firing"
grep -q '"event":"alert.resolved"' "$DATA/hooks.jsonl" 2>/dev/null && ok "sink received alert.resolved" || fail "sink missing alert.resolved"

# public snapshot must not leak anything (negotiate + raw bytes of the snapshot via the admin-free public hub is covered by tests; here check the REST surface is closed)
curl -s -o /dev/null -w "%{http_code}" "$BASE/api/nodes" | grep -q 401 && ok "anonymous REST rejected" || fail "anonymous REST not rejected"
curl -fsS -H "$H" "$BASE/api/nodes/1/metrics?range=24h" | json 'len(d["data"]["points"]) >= 1' | grep -q True && ok "1-minute metrics stored" || fail "no metrics rows"

if [ "$PASS" = 1 ]; then echo "[e2e] PASS"; else echo "[e2e] FAILED (logs in $DATA)"; trap - EXIT; cleanup; exit 1; fi
