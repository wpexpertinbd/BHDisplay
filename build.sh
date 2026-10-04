#!/bin/zsh
# Builds BHDisplay.app (menu-bar monitor input switcher).
#   ./build.sh            build into ./build
#   ./build.sh --install  build, then replace /Applications/BHDisplay.app and relaunch it
set -euo pipefail
cd "${0:A:h}"

NAME=BHDisplay
BUNDLE_ID=com.biswashost.bhdisplay
VERSION=1.2.1
BUILD_NUM=7
# ".noindex" keeps Spotlight/Launchpad from listing this development copy next to the installed app.
APP=build.noindex/$NAME.app

rm -rf build build.noindex && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp Icon/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
swiftc -O -parse-as-library -target arm64-apple-macos14.0 \
  -import-objc-header Sources/Bridge.h \
  Sources/*.swift -o "$APP/Contents/MacOS/$NAME"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleName</key><string>$NAME</string>
  <key>CFBundleExecutable</key><string>$NAME</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$BUILD_NUM</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSUIElement</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHumanReadableCopyright</key><string>© 2026 BiswasHost</string>
  <key>NSLocalNetworkUsageDescription</key><string>BHDisplay finds and connects to BHDisplay on your other computer to share your keyboard and mouse.</string>
</dict></plist>
PLIST
# Signed with the stable self-signed "BHTerminal Dev" identity (hardened runtime, no Developer ID): macOS then
# recognises every update as the same app, so the Accessibility permission (keyboard & mouse sharing) survives
# updates. Ad-hoc signing ties that permission to one exact build. Override with SIGN_ID=- for ad-hoc.
SIGN_ID="${SIGN_ID:-BHTerminal Dev}"
codesign --force --options runtime --timestamp=none --sign "$SIGN_ID" "$APP"
codesign --verify --strict "$APP"
# Keep the dev copy out of Spotlight / "Open With": only /Applications/BHDisplay.app should be registered.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$APP" 2>/dev/null || true

if [[ "${1:-}" == "--install" ]]; then
  DEST=/Applications/$NAME.app
  STAGE=/Applications/.$NAME.app.new
  rm -rf "$STAGE"
  ditto "$APP" "$STAGE"
  codesign --verify --strict "$STAGE"              # never swap in a broken copy
  # Quit only the installed copy: exact executable path, dots escaped, nothing after the name but args.
  PAT="^${DEST//./\\.}/Contents/MacOS/$NAME( |\$)"
  pkill -f "$PAT" 2>/dev/null || true
  for i in {1..10}; do pgrep -f "$PAT" >/dev/null || break; sleep 0.5; done
  # Never delete the old copy: after a .pkg install it is owned by root, which a normal user can only
  # RENAME inside /Applications (not delete, not move to the Trash). Trash it if allowed, else park it.
  OLD=""
  if [[ -e "$DEST" ]]; then
    OLD="$HOME/.Trash/$NAME-replaced-$(date +%Y%m%d-%H%M%S).app"
    if mv "$DEST" "$OLD" 2>/dev/null; then
      /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$OLD" 2>/dev/null || true
    else
      OLD="/Applications/$NAME-old-$(date +%Y%m%d-%H%M%S).app"
      mv "$DEST" "$OLD"
      /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$OLD" 2>/dev/null || true
      echo "note: previous copy is owned by root (installed by the .pkg) — parked as $OLD; delete it in Finder" >&2
    fi
  fi
  if ! mv "$STAGE" "$DEST"; then                     # roll back rather than leave no app
    [[ -n "$OLD" ]] && mv "$OLD" "$DEST"
    echo "install failed; previous version restored" >&2; exit 1
  fi
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
  open "$DEST"
  echo "Installed $DEST"
fi
