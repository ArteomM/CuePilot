# CuePilotDesktop (WPF Desktop App)

## Purpose
CuePilotDesktop is the working desktop UX around a single active session:
- session list screen (`Home`) with drill-down into a selected session
- session workspace with `Summary / Transcript / Usage`
- mini assist overlay + indicator bar
- `What should I say` generation on current session context
- `Smart` mode (extra reasoning before answering)
- LLM assist pipeline through an OpenAI-compatible API (Polza.ai by default)
- global hotkeys for core actions

## Prerequisites
- Windows
- local .NET 8 SDK available (for example `%USERPROFILE%\.dotnet8`)
- `apps/DesktopCapture` project present (from source; the installed app ships `DesktopCapture.exe` next to `CuePilotDesktop.exe`)
- optional `whisper.cpp` binary and model in repo root:
  - `tools/whisper.cpp/Release/whisper-cli.exe`
  - `models/ggml-base.bin`

## Install (setup, version 1.0.0)
Build the installer on Windows from the repository root:

```powershell
.\build\build-installer.cmd
```

Result: `build\installer\CuePilot-Setup-1.0.0.exe` (about 200 MB: self-contained .NET 8 runtime, DesktopCapture, whisper.cpp and `ggml-base.bin`).
Requires the .NET 8 SDK, `tools\whisper.cpp\Release\whisper-cli.exe` and `models\ggml-base.bin`.

The setup:
- installs per user into `%LocalAppData%\Programs\CuePilot` (no admin rights);
- creates shortcuts in the Start Menu and (optionally) on the Desktop;
- registers an uninstaller in `Settings -> Apps` (`uninstall.cmd` in the install folder);
- never packs `secrets.txt`: put the key into `Settings -> API key` or `%AppData%\CuePilot\secrets.txt`.

Reinstalling over an existing version keeps settings and sessions (`%AppData%\CuePilot`).

## Launch from source
From repository root:

```powershell
$env:PATH="$env:USERPROFILE\.dotnet8;$env:PATH"
dotnet run --project .\apps\CuePilotDesktop\CuePilotDesktop.csproj
```

Recommended one-click launch:

```powershell
.\run-cuepilot.cmd
```

## LLM setup (Polza.ai)
Default provider is [Polza.ai](https://polza.ai) (`https://polza.ai/api/v1`, OpenAI-compatible, prices in RUB).
Existing settings are migrated automatically on first start (local `localhost` endpoints are left as is).

API key lookup order (the first non-empty wins):
1. `API key` field in `Profile -> Settings`
2. environment variable `POLZA_API_KEY` (or `CUEPILOT_API_KEY`)
3. `secrets.txt`, line `ApiKey=...`: repository root, next to the exe, or `%AppData%\CuePilot\secrets.txt` (installed app)

`secrets.txt` must never be committed or shared.

Models (editable in Settings):
| Purpose | Default | Notes |
|---|---|---|
| Live hints, Q&A, follow-ups (`Model`) | `google/gemini-3.8-flash` | fast, cheap, accepts screenshots |
| Summary and follow-up email (`Analysis model`) | `anthropic/claude-sonnet-5.5` | higher quality for long transcripts; empty = use `Model` |
| Cheaper alternative | `anthropic/claude-haiku-5.5` | |

`Smart mode` sends `reasoning: { effort: "medium" }`. If a model rejects the reasoning block, the client remembers it and retries without it.
Request cost (`usage.cost_rub`) is shown on the `Usage` tab, per request and per session.

## Speech-to-text (local or API)
`Settings -> Speech recognition`:
- off (default): local `whisper.cpp` (`tools/whisper.cpp/Release/whisper-cli.exe` + `models/ggml-base.bin`)
- `Transcribe through API`: chunks are sent to `{base URL}/audio/transcriptions`
  - `openai/gpt-4o-mini-transcribe` (default, about 0.36 RUB/min, best quality/price for Russian)
  - `openai/whisper-large-v3-turbo` (about 0.05 RUB/min, budget option)
  - the STT key is optional: if empty, `SttApiKey=` from `secrets.txt`, then the LLM key (same host) is used
  - local whisper stays as a fallback when an API call fails

Audio chunks live in `%LocalAppData%\CuePilot\stt-chunks` (override: `SttDirectoryPath` in settings). Silent chunks are skipped, stale chunks (older than 60 s in the queue) are dropped, chunk files are deleted after transcription.
The overlay shows `STT lag` when transcription falls behind by 5 s or more.

## Data and logs
- sessions: `%AppData%\CuePilot\sessions\{id}.json` (one file per session; the old `sessions.json` is migrated and renamed to `sessions.json.migrated`)
- settings: `%AppData%\CuePilot\settings.json`
- screenshots for visual context: `%AppData%\CuePilot\visual-context`, trimmed together with sessions
- app log: `%LocalAppData%\CuePilot\logs\cuepilot-yyyyMMdd.log` (7 days)

## Session + LLM flow
- User opens a session from list.
- UI binds to selected session context only.
- `Summary` and `Transcript` use only selected session data.
- `What should I say` invokes:
  - `MainViewModel -> ConversationAssistService -> OpenAiAssistClient -> Polza.ai (OpenAI-compatible API)`
- `Smart` mode switches to deeper reasoning prompt path.
- `Summary` and `Email` use `Analysis model`; the rest use `Model`.

## Global hotkeys
Current default keymap:
- `Ctrl+/` show/hide overlay
- `Ctrl+Enter` ask (`What should I say`)
- `Ctrl+R` clear chat context
- `Ctrl+Shift+/` start/stop session capture
- `Ctrl+Arrow` move overlay widget
- `Ctrl+Shift+Up/Down` scroll/chat tab navigation

Hotkeys are editable in `Settings`.

## Notes
- Capture backend runs through `CaptureOrchestratorService`.
- If Whisper binary/model are missing, capture still runs; STT-dependent features degrade gracefully.
- API key must not be logged, rendered in transcript, or echoed in UI logs.
