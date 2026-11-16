#!/usr/bin/env bash
# Builds a signed, notarized macOS (Apple silicon) release of MySSH with Velopack.
#
#   tools/release/release-macos.sh <version>          e.g. 0.2.0
#
# Environment:
#   MYSSH_SIGN_IDENTITY   codesign identity, a "Developer ID Application: ..." name or its SHA-1 (required;
#                         `security find-identity -v -p codesigning`)
#   MYSSH_NOTARY_PROFILE  notarytool keychain profile (default: MySSH-notary; create it once with
#                         `xcrun notarytool store-credentials MySSH-notary --key AuthKey_XXXX.p8 --key-id XXXX --issuer <uuid>`).
#                         "-" = sign only, no notarization (dry run; not for publishing)
#   MYSSH_RELEASE_OUT     output folder (default: .work/release)
#
# Output: .work/release/ (Velopack "Releases" folder: MySSH-osx-Portable.zip, the full / delta .nupkg, releases.osx.json,
# assets.osx.json). Upload them all to the GitHub release; the app's updater reads releases.osx.json from there.
# The vpk CLI version must match the Velopack package in Directory.Packages.props (`dotnet tool install -g vpk --version X`).
set -euo pipefail
VERSION="${1:?usage: release-macos.sh <version>}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IDENTITY="${MYSSH_SIGN_IDENTITY:?set MYSSH_SIGN_IDENTITY to a Developer ID Application identity}"
PROFILE="${MYSSH_NOTARY_PROFILE:-MySSH-notary}"
REPO="https://github.com/dollarkillerx/MySSH"
OUT="${MYSSH_RELEASE_OUT:-$ROOT/.work/release}"
VPK="$(command -v vpk || echo "$HOME/.dotnet/tools/vpk")"

want="$(sed -n 's/.*Include="Velopack" Version="\([^"]*\)".*/\1/p' "$ROOT/Directory.Packages.props")"
have="$("$VPK" -H 2>/dev/null | sed -n 's/.*Velopack CLI \([0-9.]*\).*/\1/p' | head -1)"
if [ -n "$have" ] && [ "$have" != "$want" ]; then echo "vpk $have does not match Velopack $want" >&2; exit 1; fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PUB="$WORK/publish"

echo "==> publish $VERSION (NativeAOT, osx-arm64)"
dotnet publish "$ROOT/src/MySSH.Desktop" -c Release -r osx-arm64 -o "$PUB" -p:Version="$VERSION" -p:StripSymbols=true >/dev/null
rm -rf "$PUB"/*.dSYM "$PUB"/*.dbg

echo "==> self-test"
"$PUB/MySSH" --self-test "$ROOT/tests/fixtures/legacy/appdata/myssh" >/dev/null

cp "$ROOT/src/MySSH.Desktop/macOS/Info.plist" "$WORK/Info.plist"
plutil -replace CFBundleShortVersionString -string "$VERSION" "$WORK/Info.plist"
plutil -replace CFBundleVersion -string "$VERSION" "$WORK/Info.plist"

mkdir -p "$OUT"
echo "==> previous release (for delta updates)"
"$VPK" download github --repoUrl "$REPO" -o "$OUT" -c osx || echo "   none found; full package only"

NOTES=()
[ -f "$ROOT/docs/releases/$VERSION.md" ] && NOTES=(--releaseNotes "$ROOT/docs/releases/$VERSION.md")

NOTARY=(--notaryProfile "$PROFILE")
[ "$PROFILE" = "-" ] && NOTARY=()
echo "==> pack, sign ($IDENTITY), notarize (${PROFILE/#-/skipped})"
"$VPK" pack -u MySSH -v "$VERSION" -p "$PUB" -e MySSH -o "$OUT" -c osx \
  --packTitle MySSH --packAuthors "MySSH contributors" \
  --icon "$ROOT/assets/icons/icon.icns" --plist "$WORK/Info.plist" \
  --signAppIdentity "$IDENTITY" --signEntitlements "$ROOT/tools/release/MySSH.entitlements" \
  "${NOTARY[@]}" --noInst "${NOTES[@]}"

echo "==> verify"
CHECK="$WORK/check"
mkdir -p "$CHECK"
ditto -x -k "$OUT/MySSH-osx-Portable.zip" "$CHECK"
APP="$(find "$CHECK" -maxdepth 1 -name '*.app' | head -1)"
codesign --verify --deep --strict "$APP"
[ "$PROFILE" = "-" ] || xcrun stapler validate "$APP"
spctl --assess --type execute -vv "$APP" 2>&1 | sed 's/^/   /'
echo "done: $OUT"
ls -lh "$OUT"
