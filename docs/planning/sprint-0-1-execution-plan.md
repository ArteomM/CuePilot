# CuePilot Sprint 0-1 Execution Plan (Windows-first)

## Scope
This plan operationalizes the approved artifacts into two first sprints:
- **Sprint 0**: research spikes + engineering bootstrap
- **Sprint 1**: desktop shell + permissions + baseline overlay

Assumed cadence: 2 weeks per sprint.

## Team Lanes
- **Desktop**: app shell, overlay, hotkeys, tray
- **Audio/Speech**: capture spike and STT readiness checks
- **Platform**: repo tooling, CI skeleton, release channels
- **Product/QA**: acceptance criteria, test matrix, benchmark setup

## Sprint 0 (Weeks 1-2): Spike + Bootstrap
### Sprint Goal
De-risk core loop feasibility and prepare implementation-ready foundation.

### Commitments
1. Validate technical feasibility of:
   - WASAPI loopback + mic capture in same timeline
   - low-latency RU STT partial stream
   - overlay behavior and first stealth feasibility checks
2. Bootstrap monorepo and CI skeleton.
3. Produce benchmark/eval harness for STT and suggestion quality.

### Backlog Slice (from v1 backlog)
- E1-1 Tauri shell bootstrap
- E2-1 WASAPI loopback spike
- E2-2 Mic capture spike
- E3-1 STT provider abstraction draft
- E6-1 Stealth capability detection spike (research mode)
- E8-1 Regression suite skeleton (pipeline smoke)

### Sprint 0 Tasks
#### Desktop Lane
- Initialize `apps/desktop` skeleton with Tauri + React/TS.
- Implement minimal overlay window with show/hide.
- Add diagnostics view placeholder (device and permissions cards).

#### Audio/Speech Lane
- Create standalone spike module for system audio capture.
- Create standalone spike module for mic capture.
- Emit shared `AudioChunk` event shape and log timing.
- Run RU STT baseline quick benchmark on pilot sample pack.

#### Platform Lane
- Set up workspace (`pnpm` + monorepo orchestrator).
- Add root scripts: `lint`, `typecheck`, `test`, `ci:smoke`.
- Configure CI workflow with PR checks and artifact retention.

#### Product/QA Lane
- Freeze MVP acceptance criteria mapping (PRD -> backlog IDs).
- Create initial compatibility matrix template for stealth checks.
- Prepare 50-sample RU STT benchmark index.

### Sprint 0 Exit Criteria
- Desktop app launches and renders overlay in local dev.
- Capture spikes demonstrate stable stream acquisition on Windows.
- STT baseline produces partial and final events on pilot set.
- CI pipeline executes basic checks on pull request.
- Risk log updated with validated blockers and mitigations.

### Sprint 0 Deliverables
- `apps/desktop` runnable skeleton
- spike report (`audio`, `stt`, `stealth`)
- benchmark index and run instructions
- CI config and branch quality gates

## Sprint 1 (Weeks 3-4): Core Desktop Shell
### Sprint Goal
Ship a usable alpha shell that can run session lifecycle, permissions, and baseline overlay UX.

### Commitments
1. Implement production-ready shell flows:
   - onboarding + permission wizard
   - tray and global hotkeys
   - overlay state machine
2. Integrate first pipeline wiring:
   - capture input status stream to diagnostics
   - transcript drawer with mocked/live partial feed option
3. Introduce privacy badge and stealth toggle UX with capability feedback.

### Backlog Slice (from v1 backlog)
- E1-2 Permission wizard
- E1-3 Overlay manager
- E1-4 Tray + hotkeys
- E2-5 Audio diagnostics panel
- E6-1 Stealth toggle and capability detection

### Sprint 1 Tasks
#### Desktop Lane
- Build overlay state machine (`hidden`, `mini`, `expanded`).
- Add mode switch (`meeting`, `sales`, `interview`) in command bar.
- Implement transcript drawer UI with event-driven updates.

#### Audio/Speech Lane
- Wire capture status to diagnostics panel.
- Implement audio self-test flow (pass/fail + reasons).
- Define and validate transcript event schema in runtime bridge.

#### Platform Lane
- Add packaging pipeline for internal alpha artifacts.
- Add crash-reporting stub with non-PII metadata.
- Add telemetry event schema validation in CI.

#### Product/QA Lane
- Execute smoke matrix for user critical flows:
  - start session
  - permission failure recovery
  - show/hide/expand via hotkeys
  - stealth toggle state feedback
- Validate UX copy and warning clarity.

### Sprint 1 Exit Criteria
- User can complete onboarding and start an alpha session.
- Overlay states and global hotkeys are stable.
- Diagnostics can detect at least top 3 permission/device failures.
- Privacy badge and stealth capability statuses are visible.
- Alpha release checklist passes with no unresolved P0 defects.

### Sprint 1 Deliverables
- alpha-ready desktop shell build
- UX and diagnostics baseline
- validated state machine + hotkey behaviors
- QA smoke report and known limitations list

## Dependencies and Critical Path
1. `E1-1` (shell bootstrap) must finish before most Sprint 1 items.
2. Capture spike outputs (`E2-1`, `E2-2`) unblock diagnostics design.
3. STT schema draft (`E3-1`) required before transcript drawer integration.

## Risks in First Two Sprints
- Unexpected permission edge-cases on Windows setups.
- Hotkey conflicts with conferencing apps.
- Overlay z-order inconsistencies in multi-monitor setups.
- Capture spikes passing but failing under long-session load.

## Mitigations
- Add startup diagnostics and explicit remediation actions.
- Add hotkey customization and conflict detection.
- Run long-session soak tests (>= 60 minutes).
- Gate Sprint 1 completion on soak + smoke outcomes.

## Definition of Done (for Sprint Tasks)
- Feature implemented and tested against acceptance criteria.
- Logs/diagnostics included for failure troubleshooting.
- Documentation updated in `docs/`.
- No unresolved P0 regression introduced.
