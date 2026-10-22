# DesktopCapture (Windows system + mic prototype)

## Purpose
First implementation increment for CuePilot audio pipeline:
- launchable app
- system audio interception on Windows (WASAPI loopback)
- default microphone interception on Windows (WASAPI capture endpoint)
- live capture diagnostics in console
- optional WAV dump for verification

## Prerequisites
- Windows
- .NET SDK available in environment

## Run
From `apps/DesktopCapture`:

```powershell
dotnet run
```

Run with output file:

```powershell
dotnet run -- ".\captures\system-audio.wav"
```

When output path is provided, app writes:
- system stream to the path you passed (example `system-audio.wav`)
- mic stream to `<name>-mic.wav` (example `system-audio-mic.wav`)

Run with `whisper.cpp` transcription enabled:

```powershell
dotnet run -- --chunk-seconds 5 --stt-dir ".\stt-chunks" --whisper-bin "D:\AI\CuePilot\tools\whisper.cpp\Release\whisper-cli.exe" --whisper-model "D:\AI\CuePilot\models\ggml-base.bin" --lang ru
```

Optional combined run (recording + transcription):

```powershell
dotnet run -- ".\captures\system-audio.wav" --chunk-seconds 5 --whisper-bin "D:\AI\CuePilot\tools\whisper.cpp\Release\whisper-cli.exe" --whisper-model "D:\AI\CuePilot\models\ggml-base.bin" --lang ru
```

Run soak test for 10 minutes with automatic PASS/FAIL:

```powershell
dotnet run -- --chunk-seconds 5 --soak-minutes 10 --stt-dir ".\stt-chunks" --whisper-bin "D:\AI\CuePilot\tools\whisper.cpp\Release\whisper-cli.exe" --whisper-model "D:\AI\CuePilot\models\ggml-base.bin" --lang ru
```

Run soak test with formal JSON report for QA:

```powershell
dotnet run -- --chunk-seconds 5 --soak-minutes 10 --soak-report ".\qa\soak-report.json" --stt-dir ".\stt-chunks" --whisper-bin "D:\AI\CuePilot\tools\whisper.cpp\Release\whisper-cli.exe" --whisper-model "D:\AI\CuePilot\models\ggml-base.bin" --lang ru
```

Run with transcription through an OpenAI-compatible API (Polza.ai) instead of local whisper:

```powershell
$env:CUEPILOT_STT_API_KEY = "<key>"   # the key is passed only through the environment
dotnet run -- --chunk-seconds 5 --stt-provider api --stt-api-url "https://polza.ai/api/v1" --stt-api-model "openai/gpt-4o-mini-transcribe" --lang ru
```

If `--whisper-bin`/`--whisper-model` are also set, local whisper is used as a fallback when an API call fails.

## STT options
| Flag | Default | Meaning |
|---|---|---|
| `--chunk-seconds N` | 5 | Chunk length sent to STT |
| `--lang ru` | ru | Recognition language (`auto` to detect) |
| `--stt-dir path` | `.\stt-chunks` | Temporary folder for chunk WAV files |
| `--silence-threshold X` | 0.004 | Chunks whose speech level (p95 of 30 ms RMS) is below X are skipped: saves CPU/money and avoids whisper hallucinations on silence. `0` disables the gate |
| `--max-stt-lag S` | 60 | Chunks waiting in the queue longer than S seconds are dropped (`[stt-drop] ...`) so live transcription catches up. `0` disables |
| `--keep-chunks` | off | Keep WAV/TXT files after transcription (by default they are deleted) |
| `--stt-provider api` | local | Transcribe through `{url}/audio/transcriptions` |
| `--stt-api-url URL` | | API base URL, e.g. `https://polza.ai/api/v1` |
| `--stt-api-model ID` | | e.g. `openai/gpt-4o-mini-transcribe` or `openai/whisper-large-v3-turbo` |

Local whisper runs with `-nt -sns` (no timestamps, suppress non-speech tokens). In API mode system and mic are transcribed by separate workers; the tail of the previous phrase of the same source is sent as `prompt` for continuity.

## What you should see
- startup message confirming system + mic capture mode
- device safety snapshot before start:
  - `[device-check][before] ... mix=<sampleRate>Hz/<channels>ch/<bits>bit`
- periodic stats every second:
  - elapsed time
  - `system.avg` and `system.peak`
  - `mic.avg` and `mic.peak`
  - `stt.queue` (chunks waiting), `stt.lag` (age of the oldest waiting chunk), `stt.skipped` (silent chunks), `stt.dropped` (stale chunks), `stt.cost` (API cost, RUB)
- chunk events in `stt-chunks` directory (only when STT is disabled)
- `[stt][system] ...` and `[stt][mic] ...` lines when STT is enabled; empty results print nothing
- `[stt-error][source] ...`, `[stt-drop] ...`, `[stt-fallback][source] ...` service lines
- device safety snapshot after stop:
  - `[device-check][after] ... mix=<sampleRate>Hz/<channels>ch/<bits>bit`
  - `[device-check] PASS: capture did not change default output endpoint state.`
- in soak mode:
  - `[soak] target duration reached, stopping capture.`
  - `[soak] result=PASS|FAIL ...`
  - `[soak] report saved: <path.json>` when `--soak-report` is set

Press `Enter` to stop recording.

## Notes
- If no default mic endpoint is available, app continues with system-only capture and prints a warning.
- Whisper mode expects a working `whisper.cpp` CLI binary and model file path.
- Soak test PASS requires:
  - full target duration reached,
  - no system/mic/stt runtime errors,
  - no output endpoint state change (device, volume, mute, mix format).
- `--soak-report` writes structured JSON for regressions (`soak`, `capture`, `stt`, `deviceCheck` blocks).
- It is a bootstrap step aligned with CuePilot `Windows-first` docs.
- Next increment should add:
  1) explicit source-tagged event stream (`system` vs `mic`),
  2) ring buffer + chunk emission for STT bridge,
  3) device-selection override for non-default microphones.
