# CuePilot Monorepo Structure v1

## 1) Repository Layout
```text
CuePilot/
  apps/
    desktop/                 # Tauri + React/TS desktop client
    backend/                 # Minimal API (auth/flags/metering)
  services/
    stt-gateway/             # STT provider abstraction service
    orchestrator/            # Reasoning/policy service
  packages/
    shared-types/            # Shared schemas/contracts (TS/Rust bindings strategy)
    prompt-kit/              # Prompt templates and policy prompts
    evals/                   # Offline eval scripts and datasets metadata
    telemetry-schema/        # Event schemas and validators
  infra/
    terraform/               # Optional infra definitions (post-local-first phase)
  qa/
    test-matrices/           # Compatibility and regression matrices
  docs/
    prd/
    architecture/
    adr/
    planning/
    evals/
    ai/
```

## 2) Ownership Model
- `apps/desktop`: Desktop team (UI + native integration collaboration).
- `services/stt-gateway`: Speech team.
- `services/orchestrator`: Prompt/AI systems team.
- `packages/shared-types`: Architecture owner + each service owner approval.
- `packages/prompt-kit`: Prompt team with product review gate.
- `packages/evals`: QA + Speech + Prompt joint ownership.
- `packages/telemetry-schema`: Backend + product analytics owner.

## 3) Package Boundaries and Rules
### 3.1 Allowed Dependencies
- `apps/desktop` -> `packages/shared-types`, `packages/prompt-kit`, `packages/telemetry-schema`
- `apps/backend` -> `packages/shared-types`, `packages/telemetry-schema`
- `services/stt-gateway` -> `packages/shared-types`, `packages/evals` (test/dev only)
- `services/orchestrator` -> `packages/shared-types`, `packages/prompt-kit`, `packages/telemetry-schema`

### 3.2 Forbidden Dependencies
- No service imports from `apps/*`.
- No circular dependency between `services/stt-gateway` and `services/orchestrator`.
- `packages/evals` cannot be runtime dependency for production paths.

## 4) Contract Strategy
All cross-module communication must be contract-first.

### Required contract artifacts
- Event schemas:
  - `AudioChunk`
  - `TranscriptEvent`
  - `ContextPacket`
  - `SuggestionPayload`
- Error model:
  - transport errors
  - provider errors
  - policy/validation errors

### Versioning convention
- Semantic versioning for shared schema package.
- Breaking changes require:
  1. migration note,
  2. compatibility checklist,
  3. change in `docs/architecture`.

## 5) Recommended Internal Structure
### `apps/desktop`
```text
apps/desktop/
  src/
    ui/
    features/
      overlay/
      transcript/
      settings/
      diagnostics/
    state/
    adapters/
  src-tauri/
    src/
      audio_engine/
      context_engine/
      stealth_manager/
      diagnostics/
```

### `services/stt-gateway`
```text
services/stt-gateway/
  src/
    providers/
    streaming/
    normalization/
    health/
  tests/
```

### `services/orchestrator`
```text
services/orchestrator/
  src/
    policy_router/
    prompt_runtime/
    ranking/
    mode_packs/
```

## 6) Build and CI Conventions
- Monorepo tool (choose one at bootstrap): `pnpm workspaces` + `turbo` or `nx`.
- Required CI pipelines:
  - lint + typecheck per package
  - unit tests per changed scope
  - integration suite for `speech -> suggestion` path
  - contract compatibility checks for shared schema changes

## 7) Branching and Release Strategy
- Trunk-based development with short-lived feature branches.
- Feature flags for incomplete capabilities.
- Desktop release channels:
  - `alpha` (internal),
  - `beta` (closed cohort),
  - `stable` (post-MVP).

## 8) Documentation Rules
- PRD changes go to `docs/prd`.
- Architecture changes go to `docs/architecture`.
- Any structural decision requires ADR in `docs/adr`.
- QA matrices must be updated in `qa/test-matrices`.

## 9) Security and Privacy Repository Rules
- No raw call recordings committed to repo.
- Any sample transcript data must be anonymized.
- Secrets only via environment management, never in committed files.

## 10) Bootstrap Sequence
1. Initialize workspace toolchain and root scripts.
2. Scaffold `apps/desktop` skeleton.
3. Add `packages/shared-types` first, then `stt-gateway` and `orchestrator`.
4. Wire CI checks for contracts and latency regressions.
5. Add QA matrices and beta release checklist.
