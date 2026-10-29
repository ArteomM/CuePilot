#!/usr/bin/env bash
# Smoke test on the Android emulator in CI: installs the APK, starts CuePilot, waits and reports whether the app
# is still alive, with its lines from logcat and a small screenshot (base64 JPEG between SCREENSHOT markers).
set -u
APK="$1"
PACKAGE="app.cuepilot.mobile"

adb install -r "$APK" || { echo "::error::The APK did not install"; exit 1; }
adb logcat -c
adb shell monkey -p "$PACKAGE" -c android.intent.category.LAUNCHER 1 > /dev/null
sleep 50

PID="$(adb shell pidof "$PACKAGE" | tr -d '\r')"
adb logcat -d > logcat.txt
echo "----- logcat (app, runtime, crashes) -----"
grep -E "FATAL EXCEPTION|AndroidRuntime|monodroid|mono-rt|DOTNET|Avalonia|CuePilot|$PACKAGE" logcat.txt | tail -200

adb exec-out screencap -p > screen.png
if command -v convert > /dev/null && [ -s screen.png ]; then
  convert screen.png -resize '360x>' -quality 55 screen.jpg
  echo "SCREENSHOT-BEGIN"
  base64 -w 0 screen.jpg
  echo
  echo "SCREENSHOT-END"
else
  echo "No screenshot (ImageMagick missing or screencap failed)"
fi

if [ -z "$PID" ]; then
  echo "::error::CuePilot is not running 50 s after start"
  exit 1
fi
echo "CuePilot is running (pid $PID)"
