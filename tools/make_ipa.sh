#!/bin/zsh
# Compiles Unity's exported Xcode project into an unsigned IPA for Sideloadly (which signs it).
# Usage: make_ipa.sh <xcode project dir> <derived data dir> <output.ipa>
set -euo pipefail

export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer  # works even if xcode-select points at the command line tools
XCODE_PROJ=${1:A}
DERIVED=${2:A}
OUT=${3:A}
STAGE=$DERIVED-ipa

xcodebuild \
  -project "$XCODE_PROJ/Unity-iPhone.xcodeproj" \
  -scheme Unity-iPhone \
  -configuration Release \
  -sdk iphoneos \
  -destination 'generic/platform=iOS' \
  -derivedDataPath "$DERIVED" \
  CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" \
  build | grep -E "error:|\*\* BUILD" || true

APP=$(find "$DERIVED/Build/Products" -maxdepth 2 -name "*.app" -type d | head -1)
[[ -d "$APP" ]] || { echo "No .app produced"; exit 1; }

rm -rf "$STAGE" && mkdir -p "$STAGE/Payload"
cp -R "$APP" "$STAGE/Payload/"
rm -f "$OUT"
(cd "$STAGE" && zip -qry "$OUT" Payload)
rm -rf "$STAGE"
unzip -tq "$OUT" >/dev/null || { echo "The IPA came out corrupted"; exit 1; }
echo "IPA: $OUT ($(du -h "$OUT" | cut -f1))"
