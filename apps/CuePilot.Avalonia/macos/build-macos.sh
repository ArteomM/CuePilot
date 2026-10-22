#!/bin/bash
# Builds CuePilot.app for macOS and packs it into a zip. Runs on a Mac (or a macOS CI runner) with
# the .NET 8 SDK, Xcode command line tools (swiftc) and cmake.
#
#   apps/CuePilot.Avalonia/macos/build-macos.sh [arm64|x64]     (default: arm64, Apple Silicon)
#
# Result: dist/macos/CuePilot-<version>-mac-<arch>.zip with an ad-hoc signed (not notarized) CuePilot.app.
set -euo pipefail

ARCH="${1:-arm64}"
case "$ARCH" in
  arm64) RID=osx-arm64; SWIFT_TARGET=arm64-apple-macos13.0; CMAKE_ARCH=arm64 ;;
  x64)   RID=osx-x64;   SWIFT_TARGET=x86_64-apple-macos13.0; CMAKE_ARCH=x86_64 ;;
  *) echo "unknown arch: $ARCH (arm64 or x64)"; exit 64 ;;
esac

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
APP_PROJECT="$ROOT/apps/CuePilot.Avalonia"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$APP_PROJECT/CuePilot.Avalonia.csproj" | head -1)"
WORK="$ROOT/dist/macos/work-$ARCH"
APP="$ROOT/dist/macos/CuePilot.app"
WHISPER_TAG="${WHISPER_TAG:-v1.7.4}"

rm -rf "$WORK" "$APP"
mkdir -p "$WORK/stage"
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "[build] CuePilot $VERSION for $RID"
dotnet publish "$APP_PROJECT/CuePilot.Avalonia.csproj" -c Release -r "$RID" --self-contained true -o "$WORK/stage" -v q --nologo
dotnet publish "$ROOT/apps/DesktopCapture/DesktopCapture.csproj" -c Release -r "$RID" --self-contained true -p:CuePilotMac=true -o "$WORK/stage" -v q --nologo

echo "[build] audio helper"
swiftc -O -swift-version 5 -target "$SWIFT_TARGET" "$APP_PROJECT/macos/AudioHelper/main.swift" -o "$WORK/stage/cuepilot-audio"

echo "[build] whisper.cpp $WHISPER_TAG"
if [ ! -d "$WORK/../whisper.cpp-$WHISPER_TAG" ]; then
  git clone --depth 1 --branch "$WHISPER_TAG" https://github.com/ggml-org/whisper.cpp.git "$WORK/../whisper.cpp-$WHISPER_TAG"
fi
# Static libraries: whisper-cli and whisper-server then need no dylibs inside the bundle. Metal is embedded.
# GGML_NATIVE=OFF: no -march=native, so the Intel build can be cross-compiled on Apple Silicon and runs on any Mac.
cmake -S "$WORK/../whisper.cpp-$WHISPER_TAG" -B "$WORK/whisper-build" -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES="$CMAKE_ARCH" -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 \
  -DBUILD_SHARED_LIBS=OFF -DGGML_NATIVE=OFF -DGGML_METAL_EMBED_LIBRARY=ON -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_SERVER=ON -DWHISPER_SDL2=OFF
cmake --build "$WORK/whisper-build" --config Release -j --target whisper-cli whisper-server
cp "$WORK/whisper-build/bin/whisper-cli" "$WORK/whisper-build/bin/whisper-server" "$WORK/stage/"

echo "[build] CuePilot.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$WORK/stage/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" "$APP_PROJECT/macos/Info.plist" > "$APP/Contents/Info.plist"

ICONSET="$WORK/CuePilot.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256; do
  sips -z $size $size "$ROOT/apps/CuePilotDesktop/cuepilot-icon.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  if [ $double -le 256 ]; then
    sips -z $double $double "$ROOT/apps/CuePilotDesktop/cuepilot-icon.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  fi
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/CuePilot.icns"

chmod +x "$APP/Contents/MacOS/CuePilot" "$APP/Contents/MacOS/DesktopCapture" "$APP/Contents/MacOS/cuepilot-audio" \
  "$APP/Contents/MacOS/whisper-cli" "$APP/Contents/MacOS/whisper-server"

# Ad-hoc signature: Apple Silicon refuses to run unsigned code. Not notarized, so the first launch needs
# System Settings → Privacy & Security → «Open Anyway» (see README-tester.md).
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

ZIP="$ROOT/dist/macos/CuePilot-$VERSION-mac-$ARCH.zip"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

# Installer: a disk image with CuePilot.app and a shortcut to «Программы» to drag it onto.
DMG="$ROOT/dist/macos/CuePilot-$VERSION-mac-$ARCH.dmg"
DMG_ROOT="$WORK/dmg"
rm -rf "$DMG_ROOT" "$DMG"
mkdir -p "$DMG_ROOT"
cp -R "$APP" "$DMG_ROOT/"
ln -s /Applications "$DMG_ROOT/Applications"
hdiutil create -volname "CuePilot $VERSION" -srcfolder "$DMG_ROOT" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
echo "[build] done: $ZIP, $DMG"
