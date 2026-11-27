#!/usr/bin/env bash
# Exercise the actual installer offline. Capture its unit before --dry-run skips installation.
set -euo pipefail
installer=${1:-"$(dirname "$0")/../deploy/install-agent.sh"}
work=$(mktemp -d); trap 'rm -rf -- "$work"' EXIT
export SNM_TEST_DIR="$work"
awk() {
  local -a args=("$@")
  if [[ ${!#} == /proc/1/status ]]; then args[$(($# - 1))]="$SNM_TEST_DIR/status"; fi
  command awk "${args[@]}"
}
cmp() {
  if [[ ${*: -1} == /etc/systemd/system/snm-agent.service ]]; then
    cp -- "${@: -2:1}" "$SNM_TEST_DIR/unit"; return 1
  fi
  command cmp "$@"
}
export -f awk cmp
failed=0
check() {
  local name=$1 bounded=$2 permitted=$3 expected=$4 actual_b actual_a
  rm -f -- "$work/status" "$work/unit"
  if [[ $bounded != missing ]]; then printf 'CapPrm:\t%s\nCapBnd:\t%s\n' "$permitted" "$bounded" > "$work/status"; fi
  bash "$installer" --dry-run --server https://master.invalid --key "snmk_$(printf 'A%.0s' {1..43})" >"$work/out" 2>"$work/err" || { cat "$work/err" >&2; return 1; }
  actual_b=$(sed -n 's/^CapabilityBoundingSet=//p' "$work/unit"); actual_a=$(sed -n 's/^AmbientCapabilities=//p' "$work/unit")
  printf '%s: bounding=%s ambient=%s\n' "$name" "${actual_b:-none}" "${actual_a:-none}"
  if [[ $actual_b != "$expected" || $actual_a != "$expected" ]] || ! grep -Fxq "CapabilityBoundingSet=$expected" "$work/unit" || ! grep -Fxq "AmbientCapabilities=$expected" "$work/unit"; then failed=1; fi
  for line in 'User=snm-agent' 'Group=snm-agent' 'NoNewPrivileges=true'; do grep -Fxq "$line" "$work/unit" || failed=1; done
  if [[ -z $expected ]]; then grep -q 'CAP_NET_RAW.*TCP' "$work/err" || failed=1; fi
}
check capable      0000000000002000 0000000000002000 CAP_NET_RAW
check restricted   0000000000001fff ffffffffffffffff ''
check notPermitted ffffffffffffffff 0000000000001fff ''
check unreadable   missing          missing          ''
check malformed    not_hex          ffffffffffffffff ''
check typical      00000000A80425FB 00000000A80425FB CAP_NET_RAW
(( failed == 0 )) || exit 1
# Opt-in Linux CI smoke: apply the generated unit's capabilities as a non-root user.
# Opening and closing an ICMP socket matches the agent's permission check; no packets sent.
if [[ ${SNM_TEST_SYSTEMD:-0} == 1 ]]; then
  props=()
  while IFS= read -r line; do props+=(--property="$line"); done < <(grep -E '^(NoNewPrivileges|CapabilityBoundingSet|AmbientCapabilities)=' "$work/unit")
  sudo -n systemd-run --quiet --wait --pipe --collect --property=User=nobody "${props[@]}" \
    python3 -c 'import os,socket; assert os.geteuid() != 0; socket.socket(socket.AF_INET,socket.SOCK_RAW,socket.IPPROTO_ICMP).close(); print("ICMP raw socket: OK")'
fi
