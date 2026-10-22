# ADR-002: Platform Strategy (Windows-first MVP)

- Status: Accepted
- Date: 2026-04-22
- Decision Owners: CuePilot Core Team
- Related: `docs/prd/PRD-v1.md`, `docs/architecture/technical-architecture-v1.md`

## Context
CuePilot depends on platform-specific behavior for:
- system audio capture,
- overlay rendering and focus management,
- capture exclusion/stealth controls,
- app compatibility with conferencing and recording tools.

Attempting full parity from day one across Windows and macOS increases risk, timeline, and debugging complexity in MVP.

## Decision
Adopt a `Windows-first` MVP strategy. Build and validate the full core-loop on Windows first, then expand to macOS with defined readiness gates.

## Why Windows-first
- High priority for system loopback capture with clear implementation path.
- Fastest route to validate real-time assistant core-loop under target latency.
- Reduces combinatorial QA burden for stealth and capture behavior in early phases.

## Scope Impact
### Included in MVP
- Full pipeline support on Windows:
  - system audio + mic,
  - RU streaming STT,
  - suggestion overlay,
  - baseline stealth modes and compatibility matrix.

### Deferred to Phase 2
- macOS parity for capture and stealth behavior.
- Broader cross-platform compatibility hardening.

## Risks
- Product perception risk from delayed macOS support.
- Architecture drift if Windows-specific assumptions leak into core abstractions.

## Mitigations
- Keep core interfaces platform-agnostic (`AudioCapturePort`, `OverlayPort`, `StealthPort`).
- Maintain compatibility test matrix format reusable across OS targets.
- Plan explicit platform adapters with shared contract tests.

## Readiness Gates for macOS Expansion
Proceed to macOS after all conditions are true:
1. Windows alpha achieves TTFS P50 <= 2.0s and P95 <= 3.5s.
2. Crash-free session rate >= 99.0% in closed beta cohort.
3. Stealth compatibility matrix completed for Windows target apps.
4. Audio diagnostics and fallback flow validated with real users.

## Consequences
### Positive
- Faster MVP learning cycle.
- Better quality at the core loop before platform spread.
- Lower early QA and support burden.

### Negative
- Delayed reach for macOS users.
- Potential backlog pressure for parity work post-MVP.

## Revisit Criteria
Revisit this decision if:
- market demand for macOS exceeds 40% of qualified beta requests,
- Windows-specific blockers materially slow product validation,
- critical ecosystem dependency is available only on macOS path.
