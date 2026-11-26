#!/usr/bin/env bash
# Smoke test on the Android emulator in CI: installs the APK, starts Replio, waits until the screen is drawn and
# reports whether the app is still alive, with its lines from logcat. The emulator build (x86_64, demo data) also
# opens each page through an intent extra and saves a screenshot of it to shots/ (printed by the next CI step).
set -u
APK="$1"
PACKAGE="ru.replio.app"
mkdir -p shots

adb install -r "$APK" || { echo "::error::The APK did not install"; exit 1; }
adb logcat -c
adb shell settings put system screen_off_timeout 1800000 || true
adb shell svc power stayon true || true
adb shell input keyevent KEYCODE_WAKEUP || true
adb shell wm dismiss-keyguard || true
# The extra makes the app send one speech API request with a made-up key (Platform/SelfTest.cs).
ACTIVITY="$(adb shell cmd package resolve-activity --brief -c android.intent.category.LAUNCHER "$PACKAGE" | tr -d '\r' | tail -1)"
echo "Launcher activity: $ACTIVITY"
adb shell am start -n "$ACTIVITY" --es replio_selftest_stt "https://polza.ai/api/v1" --ez replio_demo_seed true \
  || adb shell monkey -p "$PACKAGE" -c android.intent.category.LAUNCHER 1 > /dev/null

# Wait until the screen is drawn («[ui] ready» from Views/MainView.Demo.cs), at most 4 minutes.
READY=""
for _ in $(seq 1 120); do
  if adb logcat -d -s Replio:I | grep -q "\[ui\] ready"; then
    READY=1
    break
  fi
  sleep 2
done
[ -n "$READY" ] && echo "The screen is drawn" || echo "::warning::No «[ui] ready» in the log after 4 minutes"
sleep 6

shot() {
  adb exec-out screencap -p > "shots/$1.png" || rm -f "shots/$1.png"
}

shot home
for page in group actions session live livetext setup settings; do
  adb shell am start -n "$ACTIVITY" --es replio_demo "$page" > /dev/null
  sleep 5
  shot "$page"
done
adb shell input keyevent KEYCODE_BACK
sleep 1

PID="$(adb shell pidof "$PACKAGE" | tr -d '\r')"
adb logcat -d > logcat.txt
echo "----- logcat (app, runtime, crashes) -----"
grep -E "FATAL EXCEPTION|AndroidRuntime|monodroid|mono-rt|DOTNET|Avalonia|Replio|$PACKAGE" logcat.txt | grep -v "AiAiEcho\|FontLog\|ImsResolver\|CarrierSvc" | tail -150
echo "----- self-test -----"
grep -F "[selftest]" logcat.txt || echo "(no self-test lines)"

if grep -q "\[selftest\] STT API FAILED" logcat.txt; then
  echo "::error::The speech API request failed on the phone (see [selftest] in the log)"
  exit 1
elif grep -q "\[selftest\] STT API request OK" logcat.txt; then
  echo "Speech API request works on the phone"
else
  echo "::warning::The speech API self-test gave no answer (network on the emulator?)"
fi

if grep -A3 "FATAL EXCEPTION" logcat.txt | grep -q "$PACKAGE"; then
  echo "::error::Replio crashed (FATAL EXCEPTION in logcat)"
  exit 1
fi

if [ -z "$PID" ]; then
  echo "::error::Replio is not running after the screens were opened"
  exit 1
fi
echo "Replio is running (pid $PID)"
