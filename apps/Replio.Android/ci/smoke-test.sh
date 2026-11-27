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
# The emulator's own launcher sometimes stops answering and its «isn't responding» box covers the screenshots.
adb shell settings put secure anr_show_background 0 || true
adb shell settings put global hide_error_dialogs 1 || true
# The Replio button and its menu over other apps, for the «callmenu» and «callhint» screenshots.
adb shell appops set "$PACKAGE" SYSTEM_ALERT_WINDOW allow || true
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
  adb shell am broadcast -a android.intent.action.CLOSE_SYSTEM_DIALOGS > /dev/null 2>&1 || true
  sleep 1
  adb exec-out screencap -p > "shots/$1.png" || rm -f "shots/$1.png"
}

shot home
for page in group actions session live livetext setup settings about; do
  adb shell am start -n "$ACTIVITY" --es replio_demo "$page" > /dev/null
  sleep 5
  shot "$page"
done

# Touch on the home page (Views/LongPress.cs, Views/SheetDrag.cs): a slow pull on a card must not open its menu
# (dragcard: no sheet), holding still must (holdcard: the sheet), and pulling the sheet down closes it (sheetdrag).
adb shell am start -n "$ACTIVITY" --es replio_demo home > /dev/null
sleep 5
adb shell input swipe 540 1050 540 1400 1000
sleep 2
shot dragcard
adb shell input swipe 540 1050 540 1050 1100
sleep 2
shot holdcard
adb shell input swipe 540 1300 540 2200 250
sleep 2
shot sheetdrag
# The finger rests on a card until it is ready (vibration), then pulls (restpull: no sheet, «cancelled» in the log)
# or lifts on the spot (restlift: the sheet). Ready comes at 480 ms, the menu by itself at 840 ms.
adb shell am start -n "$ACTIVITY" --es replio_demo home > /dev/null
sleep 5
adb shell "input motionevent DOWN 540 1050; sleep 0.55; input motionevent MOVE 540 1100; input motionevent MOVE 540 1250; input motionevent MOVE 540 1400; input motionevent UP 540 1400"
sleep 2
shot restpull
adb shell "input motionevent DOWN 540 1050; sleep 0.55; input motionevent UP 540 1050"
sleep 2
shot restlift

# callstart: the Replio button as a call begins; callmenu: its compact menu at the top during a recording;
# callhint: the hint card with one more answer waiting; callmenuidle: the menu before a recording.
for page in callstart callmenu callhint callmenuidle; do
  adb shell am start -n "$ACTIVITY" --es replio_demo "$page" > /dev/null
  sleep 5
  shot "$page"
  if [ "$page" = callmenu ]; then
    # The menu closes when pushed up by a tile (callmenudrag: only the Replio button is left, «menu pushed up» in the log).
    adb shell input swipe 540 520 540 60 250
    sleep 2
    shot callmenudrag
  fi
done
adb shell input keyevent KEYCODE_BACK
sleep 1

PID="$(adb shell pidof "$PACKAGE" | tr -d '\r')"
adb logcat -d > logcat.txt
echo "----- logcat (app, runtime, crashes) -----"
grep -E "FATAL EXCEPTION|AndroidRuntime|monodroid|mono-rt|DOTNET|Avalonia|Replio|$PACKAGE" logcat.txt | grep -v "AiAiEcho\|FontLog\|ImsResolver\|CarrierSvc" | tail -150
echo "----- touch -----"
grep -E "\[ui\] (long press|sheet pulled)|\[calls\] menu pu(lled|shed)|\[demo\] page" logcat.txt | tail -30 || true
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
