#!/usr/bin/env bash
# Builds an UNSIGNED Windows (x64) release of MySSH with Velopack — runs on macOS or Linux.
#
#   tools/release/release-windows.sh <version>          e.g. 0.2.0
#
# NativeAOT cannot cross-compile to Windows, so this is a self-contained JIT build with ReadyToRun (same code as the
# AOT build; larger download, slightly slower first start). It is NOT trimmed: trimming outside NativeAOT breaks type
# forwarders at startup. Without a code-signing certificate, SmartScreen shows "Windows protected your PC" on first run
# (More info → Run anyway).
#
# Output: .work/release/ (MySSH-win-Setup.exe, MySSH-win-Portable.zip, the .nupkg, releases.win.json, assets.win.json,
# RELEASES). Upload them to the same GitHub release as the macOS files; the Windows updater reads releases.win.json.
set -euo pipefail
VERSION="${1:?usage: release-windows.sh <version>}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
REPO="https://github.com/dollarkillerx/MySSH"
OUT="${MYSSH_RELEASE_OUT:-$ROOT/.work/release}"
VPK="$(command -v vpk || echo "$HOME/.dotnet/tools/vpk")"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PUB="$WORK/publish"

echo "==> publish $VERSION (win-x64, self-contained, ReadyToRun)"
dotnet publish "$ROOT/src/MySSH.Desktop" -c Release -r win-x64 --self-contained -p:PublishAot=false -p:PublishReadyToRun=true \
  -p:Version="$VERSION" -o "$PUB" --artifacts-path "$WORK/artifacts" >/dev/null
rm -f "$PUB"/*.pdb
test -f "$PUB/MySSH.exe" && test -f "$PUB/myssh-mcp.exe"

mkdir -p "$OUT"
echo "==> previous release (for delta updates)"
"$VPK" "[win]" download github --repoUrl "$REPO" -o "$OUT" -c win || echo "   none found; full package only"

NOTES=()
[ -f "$ROOT/docs/releases/$VERSION.md" ] && NOTES=(--releaseNotes "$ROOT/docs/releases/$VERSION.md")

echo "==> pack (unsigned)"
"$VPK" "[win]" pack -u MySSH -v "$VERSION" -p "$PUB" -e MySSH.exe -o "$OUT" -c win -r win-x64 \
  --packTitle MySSH --packAuthors "MySSH contributors" --icon "$ROOT/assets/icons/icon.ico" "${NOTES[@]}"
echo "done: $OUT"
ls -lh "$OUT" | grep -i -E "win|RELEASES"
