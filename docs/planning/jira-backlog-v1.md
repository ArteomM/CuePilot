# CuePilot Jira Backlog v1 (Windows-first MVP)

## Usage
This document is structured for direct transfer into Jira:
- **Epic** -> Jira Epic
- **Story/Task** -> Jira Story/Task
- Each item includes acceptance criteria and dependencies.

Priority scale:
- `P0` critical for MVP core-loop
- `P1` important for beta readiness
- `P2` post-MVP/backlog

---

## Epic E1: Desktop Shell and App Lifecycle (P0)
### Goal
Deliver a stable desktop foundation with onboarding, permissions, tray, and overlay states.

### Issues
#### E1-1 Tauri shell bootstrap with environment profiles (P0)
**Description**: Create desktop skeleton with dev/beta/prod profiles and config wiring.  
**Acceptance Criteria**:
- App launches in dev and packaged mode.
- Environment profile is visible in diagnostics screen.
- Crash-safe startup and fallback logging enabled.
**Dependencies**: none

#### E1-2 Permission wizard for audio/screen/accessibility prerequisites (P0)
**Description**: Build onboarding that validates required capabilities and gives recovery instructions.  
**Acceptance Criteria**:
- Wizard checks mic and system audio capture capability.
- User gets actionable remediation steps on failure.
- Session cannot start without required baseline permissions.
**Dependencies**: E1-1

#### E1-3 Overlay window manager with mini/expanded/hidden states (P0)
**Description**: Implement controllable overlay lifecycle and positioning.  
**Acceptance Criteria**:
- Mini, expanded, hidden transitions work via UI and hotkeys.
- Overlay position persists between sessions.
- Overlay remains responsive under active transcript updates.
**Dependencies**: E1-1

#### E1-4 Tray integration and global hotkeys (P0)
**Description**: Add tray menu and global shortcuts for show/hide/expand/mode switch.  
**Acceptance Criteria**:
- Hotkeys trigger actions in all supported app focus states.
- Tray reflects session state (idle/active/error).
- Conflicting hotkeys are detected and surfaced.
**Dependencies**: E1-3

---

## Epic E2: Audio Capture Pipeline (P0)
### Goal
Capture system + mic audio with low latency and reliable diagnostics.

### Issues
#### E2-1 WASAPI loopback capture adapter (P0)
**Description**: Implement system audio loopback ingestion on Windows.  
**Acceptance Criteria**:
- Captures active system output stream.
- Provides stream metadata and health state.
- Handles output device switch without app crash.
**Dependencies**: E1-2

#### E2-2 Microphone capture adapter (P0)
**Description**: Implement mic input capture with device selection and monitoring.  
**Acceptance Criteria**:
- Captures selected input reliably for >60 min run.
- Supports device re-selection during idle state.
- Input level visualization available in diagnostics.
**Dependencies**: E1-2

#### E2-3 Source tagging + timeline synchronization (P0)
**Description**: Merge captured streams into synchronized timeline while preserving source tags.  
**Acceptance Criteria**:
- Every chunk has source tag (`system`/`mic`).
- Drift stays within defined sync threshold.
- Stream sync telemetry emitted.
**Dependencies**: E2-1, E2-2

#### E2-4 VAD and ring buffer module (P0)
**Description**: Add chunking and short-term buffering to feed STT efficiently.  
**Acceptance Criteria**:
- Voice activity detection marks speech/non-speech boundaries.
- Buffer overflow handling is deterministic and logged.
- Average chunk dispatch cadence meets architecture budget.
**Dependencies**: E2-3

#### E2-5 Audio diagnostics panel and self-test flow (P1)
**Description**: Build user-facing test flow for capture health checks.  
**Acceptance Criteria**:
- User can run one-click capture test before session start.
- Result shows pass/fail and probable causes.
- Diagnostic export includes device and stream metadata.
**Dependencies**: E2-4, E1-4

---

## Epic E3: RU Streaming STT (P0)
### Goal
Provide Russian-first low-latency transcription with partial/final outputs.

### Issues
#### E3-1 STT provider abstraction (P0)
**Description**: Define provider-agnostic STT interface with streaming events.  
**Acceptance Criteria**:
- Supports partial and final transcript events.
- Supports fallback provider registration.
- Unified event schema used by context layer.
**Dependencies**: E2-4

#### E3-2 Baseline RU streaming model integration (P0)
**Description**: Integrate initial RU-capable model path.  
**Acceptance Criteria**:
- RU partial updates emitted every 300-800 ms target.
- Final segment event includes timestamps.
- Error states map to user-readable diagnostics.
**Dependencies**: E3-1

#### E3-3 RU/EN mixed speech handling and punctuation restoration (P1)
**Description**: Improve mixed-language output quality and readability.  
**Acceptance Criteria**:
- Mixed utterances maintain acceptable readability.
- Punctuation restoration improves transcript usability in user test.
- Configurable quality/performance profile.
**Dependencies**: E3-2

#### E3-4 STT fallback and timeout policy (P1)
**Description**: Add provider timeout handling and degraded operation strategy.  
**Acceptance Criteria**:
- Timeout triggers fallback route within configured threshold.
- Session remains active in degraded mode.
- User receives clear status message when fallback is active.
**Dependencies**: E3-2

---

## Epic E4: Suggestion Engine and Policy Router (P0)
### Goal
Transform transcript context into short ranked suggestions in real time.

### Issues
#### E4-1 Context window builder from speaker turns (P0)
**Description**: Assemble rolling context packet from transcript and mode hints.  
**Acceptance Criteria**:
- Context includes recent turns and mode metadata.
- Context truncation follows token budget policy.
- Context packet schema validated by tests.
**Dependencies**: E3-2

#### E4-2 Output schema and length guardrails (P0)
**Description**: Define strict output shape for mini and expanded overlays.  
**Acceptance Criteria**:
- Mini response is one line.
- Expanded response returns <=3 alternatives + next best question.
- Guardrails block verbose output violations.
**Dependencies**: E4-1

#### E4-3 Policy router (template vs model path) (P1)
**Description**: Route requests by urgency/latency budget and confidence.  
**Acceptance Criteria**:
- Router chooses fast path for low-complexity prompts.
- Timeouts trigger template fallback.
- Routing decisions are logged for analysis.
**Dependencies**: E4-2

#### E4-4 Mode packs (Meeting/Sales/Interview) (P1)
**Description**: Add mode-specific prompting and ranking behavior.  
**Acceptance Criteria**:
- Mode switch affects suggestion style immediately.
- Acceptance rate telemetry segmented by mode.
- No regression on TTFS target.
**Dependencies**: E4-3

---

## Epic E5: Screen Context (ROI-first) (P1)
### Goal
Enable low-risk manual screen context ingestion for better relevance.

### Issues
#### E5-1 Manual ROI capture flow (P1)
**Description**: Let user select screen region and trigger analysis.  
**Acceptance Criteria**:
- User can capture specific region without full-screen persistence.
- Region metadata linked to current session.
- Capture action available by hotkey.
**Dependencies**: E1-4

#### E5-2 OCR extraction and normalization pipeline (P1)
**Description**: Parse captured region text and produce clean context snippets.  
**Acceptance Criteria**:
- OCR text is available to context layer within target delay.
- Empty/low-confidence OCR handled gracefully.
- Normalization removes obvious OCR artifacts.
**Dependencies**: E5-1

#### E5-3 Context injection policy for screen data (P1)
**Description**: Merge OCR context into reasoning packet with confidence checks.  
**Acceptance Criteria**:
- Screen context can be toggled per session.
- Injection policy prevents noisy context overload.
- Screen-context hit rate metric is emitted.
**Dependencies**: E5-2, E4-1

---

## Epic E6: Privacy and Stealth Layer (P0)
### Goal
Provide transparent privacy controls and practical capture-exclusion behavior.

### Issues
#### E6-1 Stealth toggle and capability detection (P0)
**Description**: Detect support status and expose user controls.  
**Acceptance Criteria**:
- Toggle states: enabled/disabled/unsupported.
- Unsupported environments show reason and fallback guidance.
- State reflected in privacy badge.
**Dependencies**: E1-3

#### E6-2 Capture exclusion integration for overlay window (P0)
**Description**: Implement OS-level exclusion path for supported scenarios.  
**Acceptance Criteria**:
- Exclusion mode can be enabled at runtime.
- Validation utility can confirm expected capture behavior.
- Failures are surfaced with app-specific notes where known.
**Dependencies**: E6-1

#### E6-3 Taskbar visibility behavior controls (P1)
**Description**: Add optional hide from taskbar mode with safe recovery.  
**Acceptance Criteria**:
- User can hide/show from taskbar without losing access.
- Recovery hotkey always works.
- Setting persists across restarts.
**Dependencies**: E1-4, E6-1

#### E6-4 Compatibility matrix for conferencing/recording apps (P0)
**Description**: Validate stealth behavior across target apps and versions.  
**Acceptance Criteria**:
- Matrix includes Zoom, Teams, Meet, Telegram, Discord, OBS.
- Each row includes pass/fail/partial + notes.
- Matrix is versioned and linked in release checklist.
**Dependencies**: E6-2

---

## Epic E7: Backend Essentials and Telemetry (P1)
### Goal
Add minimal backend capabilities after local loop validation.

### Issues
#### E7-1 Feature flag and remote config interface (P1)
**Description**: Define mechanism for enabling/disabling behavior by cohort.  
**Acceptance Criteria**:
- Client can fetch and apply flags safely.
- Default local config works offline.
- Flag fetch failures do not block session.
**Dependencies**: E1-1

#### E7-2 Usage metering event schema (P1)
**Description**: Define and emit core product telemetry events.  
**Acceptance Criteria**:
- Events cover TTFS, transcript lag, acceptance, crash outcomes.
- Event schema versioning defined.
- Sampling policy documented.
**Dependencies**: E4-3

#### E7-3 Auth/subscription placeholders (P2)
**Description**: Add basic account scaffolding for future monetization.  
**Acceptance Criteria**:
- Local guest mode remains available.
- Auth failures do not affect local session loop.
- Entitlement checks behind feature flag.
**Dependencies**: E7-1

---

## Epic E8: QA, Beta Hardening, and Release (P0)
### Goal
Prove stability and usefulness with closed beta users.

### Issues
#### E8-1 End-to-end pipeline regression suite (P0)
**Description**: Build automated regression checks for speech->suggestion loop.  
**Acceptance Criteria**:
- Suite runs in CI for major merges.
- Detects latency and schema regressions.
- Produces human-readable failure reports.
**Dependencies**: E2-4, E3-2, E4-2

#### E8-2 Closed beta feedback protocol (P1)
**Description**: Standardize collection and triage of user sessions.  
**Acceptance Criteria**:
- Feedback form captures key failure modes.
- Weekly triage outputs prioritized fixes.
- Product dashboard tracks top 5 recurrent pain points.
**Dependencies**: E8-1

#### E8-3 Crash reporting and session replay metadata (P1)
**Description**: Improve incident diagnosis while preserving privacy boundaries.  
**Acceptance Criteria**:
- Crash events include non-PII diagnostic context.
- Repro metadata links to pipeline stage.
- Clear opt-in messaging for telemetry.
**Dependencies**: E1-1, E7-2

#### E8-4 Release gate checklist (P0)
**Description**: Define mandatory checks before each beta rollout.  
**Acceptance Criteria**:
- Includes performance, compatibility, and privacy checks.
- Includes rollback and hotfix process.
- Includes known limitations section for release notes.
**Dependencies**: E6-4, E8-1

---

## Cross-Epic Milestones
1. **M1 Core Loop Alpha**: E1, E2, E3, E4 baseline complete.
2. **M2 Stealth + ROI Beta**: E5 and E6 pass target matrix.
3. **M3 Closed Beta Ready**: E8 release gate pass, E7 minimal telemetry live.

## Definition of Done (Global)
- Acceptance criteria met and demonstrated.
- Tests added/updated.
- Diagnostics and logs for failure modes added.
- Documentation updated in `docs/*`.
- No unresolved P0 regressions.
