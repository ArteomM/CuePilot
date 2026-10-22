# ADR-001: Desktop Stack Selection

- Status: Accepted
- Date: 2026-04-22
- Decision Owners: CuePilot Core Team
- Related: `docs/prd/PRD-v1.md`, `docs/architecture/technical-architecture-v1.md`

## Context
CuePilot must deliver low-latency live assistance with desktop-level capabilities:
- audio capture from system loopback + microphone,
- always-on-top overlay behavior,
- global hotkeys,
- privacy/stealth controls for capture behavior,
- stable performance for long sessions.

The stack should support native OS integrations while keeping distribution footprint acceptable.

## Decision
Use `Tauri 2 + Rust backend + React/TypeScript frontend` as the production-track architecture for desktop application development.

## Alternatives Considered
### 1) Electron + React/TypeScript
Pros:
- Faster onboarding for web teams.
- Mature ecosystem and broad examples.

Cons:
- Higher memory/runtime footprint for always-on assistant workloads.
- Heavier packaging/update artifacts.
- More overhead for low-level media and windowing edge cases.

### 2) Pure native apps (C#/WPF, Swift, etc.)
Pros:
- Deep native control and potentially best platform integration.

Cons:
- Slower cross-platform velocity.
- Higher maintenance cost across UI stacks and teams.

## Rationale
`Tauri + Rust` provides:
- Native interop and system-level access paths suited for audio/window control.
- Smaller runtime footprint than browser-heavy desktop shells.
- Strong performance profile for real-time pipelines and buffer processing.
- Clear boundary: UI iteration speed in React/TS, critical path in Rust.

## Consequences
### Positive
- Better baseline for latency-sensitive audio/STT loop.
- Cleaner long-term path for security/privacy-sensitive features.
- Smaller install/update profile for end users.

### Negative
- Team needs Rust expertise and stronger FFI discipline.
- Plugin/library ecosystem is smaller than Electron's.
- Early-stage debugging complexity can be higher for native integrations.

## Implementation Notes
- Keep critical pipeline in Rust modules (`audio_engine`, `context_engine`, `stealth_manager`).
- Keep UI and interaction flows in React/TS.
- Define typed contracts for Rust <-> frontend events early.

## Revisit Criteria
Revisit this decision if any of the following happens:
1. MVP cannot reach TTFS targets due to integration overhead.
2. Desktop engineering velocity remains blocked for >2 sprints.
3. Required platform capability is unavailable in practical Tauri/Rust path.
