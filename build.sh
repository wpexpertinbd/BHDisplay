#!/bin/zsh
# Builds BHDisplay.app (menu-bar monitor input switcher).
#   ./build.sh            build into ./build
#   ./build.sh --install  build, then replace /Applications/BHDisplay.app and relaunch it
set -euo pipefail
cd "${0:A:h}"

NAME=BHDisplay
BUNDLE_ID=com.biswashost.bhdisplay
VERSION=1.0.1
BUILD_NUM=2
APP=build/$NAME.app

rm -rf build && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
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
</dict></plist>
PLIST
# Ad-hoc signature with the hardened runtime (no Developer ID on this machine).
codesign --force --options runtime --timestamp=none --sign - "$APP"
codesign --verify --strict "$APP"

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
  if [[ -e "$DEST" ]]; then rm -rf "$DEST.old"; mv "$DEST" "$DEST.old"; fi
  if ! mv "$STAGE" "$DEST"; then                     # roll back rather than leave no app
    [[ -e "$DEST.old" ]] && mv "$DEST.old" "$DEST"
    echo "install failed; previous version restored" >&2; exit 1
  fi
  rm -rf "$DEST.old"
  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
  open "$DEST"
  echo "Installed $DEST"
fi
