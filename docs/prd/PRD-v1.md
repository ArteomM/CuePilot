# CuePilot PRD v1 (Windows-first MVP)

## 1) Product Vision
CuePilot is a desktop AI prompter for Russian-speaking meetings that runs locally on the user's device, without joining calls as a bot participant. It listens to system audio and microphone, builds live context, and surfaces short, actionable prompts in an overlay visible only to the user.

## 2) Problem Statement
Users in interviews, sales calls, and working meetings lose response quality under time pressure. Existing assistants often require a bot in the call or produce long outputs that are hard to use live. CuePilot should provide sub-second guidance with minimal UI noise while preserving privacy and call UX.

## 3) Goals and Non-Goals
### Goals (MVP)
- Real-time capture of system audio + microphone on Windows.
- Russian-first streaming STT with partial and final hypotheses.
- Overlay with 1-3 concise suggestions and one "next best question".
- Predictable stealth behavior for screen sharing/recording where supported by OS/app stack.
- No meeting-bot join model.

### Non-Goals (MVP)
- CRM integrations and automated outbound follow-ups.
- Full meeting intelligence suite (deep summaries, workflow automation).
- Enterprise-grade multi-tenant administration.
- Complex RAG over large corporate knowledge bases.

## 4) Target Users
- Sales and presales specialists in Russian-speaking calls.
- Recruiters and interviewers/candidates.
- Customer support and solution consultants.

## 5) Jobs To Be Done (JTBD)
1. "When I hear a difficult question, help me answer in one sentence immediately."
2. "When discussion stalls, suggest the best next clarifying question."
3. "When I miss details, keep a lightweight live transcript and key action cues."
4. "When sharing screen, keep assistant hints private and non-disruptive."

## 6) Core User Scenarios
### Meeting Mode
- User starts session before/at meeting start.
- CuePilot captures audio streams and shows concise prompts.
- User expands overlay only when needed.

### Sales Mode
- Detect objections and generate short objection-handling responses.
- Offer "next best move" based on current speaker turn intent.

### Interview Mode
- Generate direct answers + clarifier options.
- Provide fast structure prompts for system design/coding discussions.

## 7) MVP Feature Set
1. **Audio Capture**: system loopback + microphone, source tagging.
2. **RU Streaming STT**: partial/final transcript stream, punctuation restoration baseline.
3. **Suggestion Overlay**:
   - Mini mode: one-line answer.
   - Expanded mode: up to 3 options + next best question.
4. **Stealth Controls**:
   - Exclude overlay from capture when supported.
   - Quick hide/show hotkey.
   - Optional hide from taskbar behavior.
5. **Session Utilities**:
   - Temporary transcript drawer.
   - Privacy badge for active data sources.

## 8) UX Principles
- Never break call flow.
- Show short outputs first; full response only on explicit expand.
- Keep overlay movable, transparent, and low-jank.
- Make privacy state obvious at all times.

## 9) Functional Requirements
### FR-1 Audio Pipeline
- Capture system audio and mic concurrently.
- Maintain synchronized timeline with source tags.
- Recover gracefully from device switch/disconnect.

### FR-2 STT Pipeline
- Stream partial hypotheses every 300-800 ms target window.
- Emit final segments with timestamps and confidence.
- Support mixed RU/EN speech in baseline quality mode.

### FR-3 Suggestion Engine
- Build context from last N turns.
- Generate ranked outputs:
  1) one-line response,
  2) next best question,
  3) short bullet plan.
- Enforce output length constraints by mode.

### FR-4 Overlay and Interaction
- Always-on-top overlay with mini/expanded states.
- Global hotkeys: show/hide, expand/collapse, mode switch.
- Transcript drawer on demand.

### FR-5 Privacy and Stealth
- Toggle "exclude from capture" capability where available.
- Surface compatibility warnings when exclusion is unsupported.
- Store only temporary local session cache by default.

## 10) Non-Functional Requirements (MVP)
- **Latency**:
  - Time-to-first-suggestion (TTFS): P50 <= 2.0s, P95 <= 3.5s.
  - Transcript lag (partial): P50 <= 0.8s.
- **Reliability**:
  - Crash-free session rate >= 99.0% (beta target).
- **Performance**:
  - No major UI stutter during active audio/STT processing.
- **Security/Privacy**:
  - Local-only by default; cloud sync opt-in.
  - Redaction pipeline before any upload in future cloud modes.

## 11) Success Metrics
- TTFS (Time-to-first-suggestion)
- Transcript lag
- Suggestion acceptance rate
- Manual expand rate
- Hide/show toggle frequency
- STT correction rate
- Session crash-free rate

## 12) MVP Acceptance Criteria
1. User can complete a full call with active system+mic capture and no bot join.
2. Overlay presents concise useful suggestions in real time (<= 3 items).
3. Stealth mode has documented behavior and pass/fail matrix on target apps.
4. Russian speech quality is acceptable for assisted response use cases in closed beta.
5. Privacy status is always visible and controllable.

## 13) Risks and Mitigations
- **Audio capture instability** -> setup wizard + diagnostics + fallback instructions.
- **Stealth incompatibility** -> compatibility matrix + graceful degradation.
- **High latency under load** -> strict token budgets + policy router + local fallback paths.
- **Noisy outputs** -> ranking policy and hard response-length guardrails.

## 14) Release Scope
### Included in Closed Beta
- Windows desktop app
- Meeting/Sales/Interview modes
- RU streaming STT baseline
- Suggestion overlay + transcript drawer
- Initial stealth feature set with limitations

### Deferred Post-MVP
- macOS parity
- integrations ecosystem
- advanced notes automation
- enterprise deployment model
