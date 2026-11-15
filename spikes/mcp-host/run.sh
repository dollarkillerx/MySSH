#!/usr/bin/env bash
# End-to-end spike run: build, launch the Avalonia host (tray/headless + auto-approve), run McpProbe, kill host.
# usage: ./run.sh [--no-ui] [--hybrid]
#   --no-ui   skip phase C (interactive dialog, nobody clicks -> timeout)
#   --hybrid  also run phase D: HttpServerSessionMode.StatefulForInitializeClients cancellation matrix
#             (informational: the 2025-11-25 CTS case is EXPECTED to FAIL there, see FINDINGS.md)
set -uo pipefail
cd "$(dirname "$0")"
HERE=$PWD
WORK=$HERE/.work
CFG=Release
NO_UI=0; HYBRID=0
for a in "$@"; do case $a in --no-ui) NO_UI=1;; --hybrid) HYBRID=1;; esac; done

HOST_BIN=$HERE/MySSH.Spike.McpHost/bin/$CFG/net10.0/MySSH.Spike.McpHost
BRIDGE=$HERE/myssh-mcp/bin/$CFG/net10.0/myssh-mcp
PROBE=$HERE/McpProbe/bin/$CFG/net10.0/McpProbe
HOST_PID=""
FAILS=0

cleanup() {
  if [[ -n "$HOST_PID" ]] && kill -0 "$HOST_PID" 2>/dev/null; then kill -9 "$HOST_PID" 2>/dev/null; fi
  pkill -9 -f "$BRIDGE" 2>/dev/null || true
}
trap cleanup EXIT

echo "== build ($CFG)"
for p in MySSH.Spike.McpHost myssh-mcp McpProbe; do
  dotnet build "$p" -c $CFG -nologo -v q -clp:ErrorsOnly >/dev/null || { echo "build failed: $p"; exit 1; }
done

mkdir -p "$WORK"; chmod 700 "$WORK"
: > "$WORK/events.log"

start_host() { # $1 = phase name, rest = host args
  local phase=$1; shift
  rm -f "$WORK/endpoint.json"
  "$HOST_BIN" "$@" --work-dir="$WORK" >"$WORK/host-$phase.log" 2>&1 &
  HOST_PID=$!
  for _ in $(seq 1 100); do [[ -f "$WORK/endpoint.json" ]] && break; sleep 0.1; done
  [[ -f "$WORK/endpoint.json" ]] || { echo "host did not start"; cat "$WORK/host-$phase.log"; exit 1; }
  local rss; rss=$(ps -o rss= -p "$HOST_PID" | tr -d ' ')
  echo "== host up (pid $HOST_PID, args: $*) RSS=$((rss/1024))MB"
  grep -E '"(hostStartMs|processUptimeAtReadyMs|workingSet[A-Za-z]+MB)"' "$WORK/endpoint.json" | tr -d '\n '; echo
}

stop_host() {
  local rss; rss=$(ps -o rss= -p "$HOST_PID" 2>/dev/null | tr -d ' ')
  echo "== stopping host pid $HOST_PID (RSS before stop: $((${rss:-0}/1024))MB) with SIGTERM"
  kill -TERM "$HOST_PID" 2>/dev/null
  for _ in $(seq 1 50); do kill -0 "$HOST_PID" 2>/dev/null || break; sleep 0.1; done
  if kill -0 "$HOST_PID" 2>/dev/null; then echo "!! host ignored SIGTERM, killing"; kill -9 "$HOST_PID"; FAILS=$((FAILS+1)); fi
  wait "$HOST_PID" 2>/dev/null; echo "   host exit code: $?"
  [[ -S "$WORK/myssh.sock" ]] && { echo "!! socket file left behind"; FAILS=$((FAILS+1)); }
  HOST_PID=""
}

run_probe() { "$PROBE" --work-dir "$WORK" --bridge "$BRIDGE" "$@" || FAILS=$((FAILS+1)); }

echo; echo "######## phase A: --tray --auto-approve=allow"
start_host allow --tray --auto-approve=allow
run_probe --phase allow
stop_host

echo; echo "######## phase B: --tray --auto-approve=deny"
start_host deny --tray --auto-approve=deny
run_probe --phase deny
stop_host

if [[ $NO_UI == 0 ]]; then
  echo; echo "######## phase C: UI dialog, nobody clicks, --approval-timeout=5"
  MYSSH_SNAPSHOT_DIR="$WORK" start_host timeout --approval-timeout=5
  run_probe --phase timeout --timeout-s 5
  [[ -f "$WORK/approval-dialog.png" ]] && echo "   dialog snapshot (RenderTargetBitmap): $WORK/approval-dialog.png"
  stop_host
fi

if [[ $HYBRID == 1 ]]; then
  echo; echo "######## phase D (informational): MYSSH_HTTP_MODE=hybrid, cancellation matrix"
  MYSSH_HTTP_MODE=hybrid start_host hybrid --tray --auto-approve=allow
  "$PROBE" --work-dir "$WORK" --bridge "$BRIDGE" --phase cancel || echo "   (phase D failures are informational)"
  stop_host
fi

echo; echo "== leftover process check"
LEFT=$(pgrep -fl "MySSH.Spike.McpHost|myssh-mcp|McpProbe" || true)
if [[ -n "$LEFT" ]]; then echo "!! leftovers:"; echo "$LEFT"; FAILS=$((FAILS+1)); else echo "PASS  no leftover host/bridge/probe processes"; fi

echo; echo "== server-side evidence (events.log, cancellation & approval lines)"
grep -E "CANCELLED|APPROVAL|LOCAL SESSION|REJECT|HOST READY" "$WORK/events.log" | sed 's/^/   /'

echo; [[ $FAILS == 0 ]] && echo "ALL PHASES PASS" || echo "FAILURES: $FAILS"
exit $FAILS
