#!/usr/bin/env bash
# Builds a local MySSH.app (NativeAOT, osx-arm64) for trying the app on this Mac.
#
#   tools/macos/make-local-app.sh <output.app> [publish-dir]
#
# Signing: MYSSH_SIGN_IDENTITY selects the codesign identity (a name or SHA-1 from
# `security find-identity -v -p codesigning`). Default "-" = ad-hoc.
#
# Why it matters: the vault's master key lives in the login keychain, and macOS lets an app read it without asking only
# if the app's code identity matches the one the user allowed. An ad-hoc signature's identity is the binary's hash, so
# every rebuild looks like a new app and the keychain asks again. A real certificate (e.g. "Apple Development") gives a
# stable identity (bundle id + certificate): after one "Always Allow", rebuilt apps keep access.
set -euo pipefail
OUT="${1:?usage: make-local-app.sh <output.app> [publish-dir]}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PUB="${2:-$(mktemp -d)}"
IDENTITY="${MYSSH_SIGN_IDENTITY:--}"

dotnet publish "$ROOT/src/MySSH.Desktop" -c Release -r osx-arm64 -o "$PUB" -p:StripSymbols=true >/dev/null
rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Resources"
rsync -a --exclude '*.dSYM' --exclude '*.pdb' "$PUB/" "$OUT/Contents/MacOS/"
cp "$ROOT/src/MySSH.Desktop/macOS/Info.plist" "$OUT/Contents/"
cp "$ROOT/assets/icons/icon.icns" "$OUT/Contents/Resources/"
codesign --force --deep --timestamp=none --sign "$IDENTITY" "$OUT"
codesign --verify --deep "$OUT"
echo "built $OUT (signed: $IDENTITY)"
codesign -dr - "$OUT" 2>&1 | grep designated || true
