#!/usr/bin/env bash
# AOT gate for third-party code (design §1). Our own src/ libraries are already analysed with IsAotCompatible
# (their trim/AOT warnings are build errors). Third-party assemblies only report a collapsed IL2104/IL3053, which
# the app project tolerates; this script publishes with per-member warnings and fails on anything not in the
# reviewed allowlist (tools/aot/allowed-warnings.txt). Usage: tools/aot/check-aot-warnings.sh <rid> [project]
set -euo pipefail
RID="${1:?usage: $0 <rid> [project]}"
PROJECT="${2:-src/MySSH.Desktop}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
# ILC only re-emits warnings when it recompiles
rm -rf "$ROOT/$PROJECT"/obj/Release/net10.0/"$RID"/native
DOTNET_CLI_UI_LANGUAGE=en dotnet publish "$ROOT/$PROJECT" -c Release -r "$RID" -o "$OUT/bin" \
  -p:TrimmerSingleWarn=false -p:TreatWarningsAsErrors=false > "$OUT/publish.log" 2>&1 || { cat "$OUT/publish.log"; exit 1; }
# "…: warning IL2026: Namespace.Type.Member(args): message [project]" -> "IL2026 Namespace.Type.Member(args)"
grep -oE 'warning IL[0-9]+: [^:]+' "$OUT/publish.log" | sed -E 's/^warning (IL[0-9]+): /\1 /' | sort -u > "$OUT/found.txt" || true
grep -vE '^\s*(#|$)' "$ROOT/tools/aot/allowed-warnings.txt" | sort -u > "$OUT/allowed.txt"
NEW="$(comm -23 "$OUT/found.txt" "$OUT/allowed.txt")"
if [[ -n "$NEW" ]]; then
  echo "New trim/AOT warnings (review, fix, or add to tools/aot/allowed-warnings.txt with a reason):"
  echo "$NEW"
  exit 1
fi
STALE="$(comm -13 "$OUT/found.txt" "$OUT/allowed.txt")"
[[ -n "$STALE" ]] && { echo "Allowlist entries no longer produced (can be removed):"; echo "$STALE"; }
echo "AOT gate OK: $(wc -l < "$OUT/found.txt" | tr -d ' ') known third-party warnings, 0 new. Binary: $(du -sh "$OUT/bin" | cut -f1)"
