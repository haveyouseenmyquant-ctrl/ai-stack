#!/bin/bash
# Compiles AI Stack and assembles a signed app bundle.
#   scripts/make_bundle.sh "<path/to/AI Stack.app>" [universal]
set -euo pipefail
cd "$(dirname "$0")/.."
BUNDLE="$1"
ARCH_FLAGS=(--arch arm64)
[ "${2:-}" = "universal" ] && ARCH_FLAGS=(--arch arm64 --arch x86_64)

if [ ! -f Support/AppIcon.icns ]; then
  echo "→ Rendering icon"
  TMP=$(mktemp -d)
  swift scripts/make_icon.swift "$TMP/icon.png"
  mkdir -p "$TMP/AppIcon.iconset"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$TMP/icon.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$TMP/icon.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$TMP/AppIcon.iconset" -o Support/AppIcon.icns
  rm -rf "$TMP"
fi

echo "→ Compiling (release, ${ARCH_FLAGS[*]})"
swift build -c release "${ARCH_FLAGS[@]}"
BIN="$(swift build -c release "${ARCH_FLAGS[@]}" --show-bin-path)/AIStack"

echo "→ Bundling"
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources" "$BUNDLE/Contents/Library/LaunchAgents"
cp "$BIN" "$BUNDLE/Contents/MacOS/AIStack"
cp Support/Info.plist "$BUNDLE/Contents/Info.plist"
cp Support/AppIcon.icns "$BUNDLE/Contents/Resources/AppIcon.icns"
cp shared/doctor-catalogue.json "$BUNDLE/Contents/Resources/doctor-catalogue.json"   # Laptop Doctor rules, shared with Windows
cp Support/com.gaspardoliviers.aistack.agent.plist "$BUNDLE/Contents/Library/LaunchAgents/"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $(date +%Y%m%d%H%M)" "$BUNDLE/Contents/Info.plist"

# A stable signing identity keeps macOS privacy permissions (Accessibility, Microphone…) across updates.
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning | grep -m1 -E 'Developer ID Application|Apple Development' | sed -E 's/.*"(.*)"/\1/' || true)}"
if [ -z "$IDENTITY" ]; then
  echo "→ No signing identity found; ad-hoc signing (permissions may need re-granting after updates)"
  IDENTITY="-"
else
  echo "→ Signing with: $IDENTITY"
fi
for attempt in 1 2 3; do
  xattr -cr "$BUNDLE" # codesign rejects Finder info / provenance attributes
  if codesign --force --sign "$IDENTITY" --timestamp=none "$BUNDLE" 2>/dev/null; then break; fi
  [ "$attempt" = 3 ] && { echo "✗ codesign failed"; codesign --force --sign "$IDENTITY" --timestamp=none "$BUNDLE"; exit 1; }
  sleep 1
done
