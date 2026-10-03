#!/bin/zsh
# Builds BHDisplay and packages it as dist/BHDisplay-<ver>.dmg and dist/BHDisplay-<ver>.pkg.
#
# Distribution model: ad-hoc signed + Hardened Runtime, NOT notarized (no paid Apple
# Developer account). On another Mac the user approves it once via System Settings ›
# Privacy & Security › "Open Anyway" (packaging/dmg-readme.txt is bundled into the DMG).
set -euo pipefail
cd "${0:A:h}"

./build.sh
APP=build.noindex/BHDisplay.app
VERSION=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist")
BUNDLE_ID=$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$APP/Contents/Info.plist")
echo "==> Packaging BHDisplay v$VERSION"
codesign --verify --deep --strict "$APP"
mkdir -p dist

# ---------- DMG ----------
STAGE=$(mktemp -d)
ditto "$APP" "$STAGE/BHDisplay.app"
ln -s /Applications "$STAGE/Applications"
cp packaging/dmg-readme.txt "$STAGE/Read Me — First Launch.txt"
rm -f "dist/BHDisplay-$VERSION.dmg"
hdiutil create -volname "BHDisplay $VERSION" -srcfolder "$STAGE" -ov -format UDZO "dist/BHDisplay-$VERSION.dmg" >/dev/null
rm -rf "$STAGE"

# ---------- PKG (branded installer via productbuild) ----------
PKGROOT=$(mktemp -d)
mkdir -p "$PKGROOT/Applications"
ditto "$APP" "$PKGROOT/Applications/BHDisplay.app"
PKGTMP=$(mktemp -d)
mkdir -p "$PKGTMP/res"
# Not relocatable: always install to /Applications, even if another copy exists elsewhere.
pkgbuild --analyze --root "$PKGROOT" "$PKGTMP/components.plist" >/dev/null
/usr/libexec/PlistBuddy -c 'Add :0:BundleIsRelocatable bool false' "$PKGTMP/components.plist"
pkgbuild --root "$PKGROOT" --component-plist "$PKGTMP/components.plist" --install-location / \
  --identifier "$BUNDLE_ID" --version "$VERSION" \
  --scripts packaging/pkg-scripts --ownership recommended "$PKGTMP/component.pkg" >/dev/null
sed "s/__VERSION__/$VERSION/g" packaging/welcome.html    > "$PKGTMP/res/welcome.html"
sed "s/__VERSION__/$VERSION/g" packaging/conclusion.html > "$PKGTMP/res/conclusion.html"
cat > "$PKGTMP/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="1">
  <title>BHDisplay</title>
  <welcome file="welcome.html" mime-type="text/html"/>
  <conclusion file="conclusion.html" mime-type="text/html"/>
  <volume-check><allowed-os-versions><os-version min="14.0"/></allowed-os-versions></volume-check>
  <options customize="never" require-scripts="false" hostArchitectures="arm64"/>
  <choices-outline><line choice="default"/></choices-outline>
  <choice id="default"><pkg-ref id="$BUNDLE_ID"/></choice>
  <pkg-ref id="$BUNDLE_ID" version="$VERSION">component.pkg</pkg-ref>
</installer-gui-script>
XML
rm -f "dist/BHDisplay-$VERSION.pkg"
productbuild --distribution "$PKGTMP/distribution.xml" --resources "$PKGTMP/res" \
  --package-path "$PKGTMP" "dist/BHDisplay-$VERSION.pkg" >/dev/null
rm -rf "$PKGROOT" "$PKGTMP"

echo "==> Done:"
ls -lh "dist/BHDisplay-$VERSION.dmg" "dist/BHDisplay-$VERSION.pkg"
