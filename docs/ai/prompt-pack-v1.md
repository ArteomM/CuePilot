# CuePilot AI Prompt Pack v1

## Purpose
This pack standardizes how AI assistants receive scoped tasks and produce verifiable engineering outputs for CuePilot.

## Global Prompt Rules (for all roles)
1. Work only within provided scope and constraints.
2. Return concrete artifacts (tables, specs, checklists, diagrams), not generic explanations.
3. Include assumptions and open risks explicitly.
4. Use measurable acceptance criteria.
5. Keep output concise and implementation-ready.

## Standard Task Template
Use this input format for every assistant task:

```text
Role:
Objective:
Inputs:
Constraints:
Expected Artifact:
Done When:
Output Format:
```

---

## 1) Product AI
### Prompt Template
```text
Role: Product AI
Objective: Produce PRD section for <feature>.
Inputs: Target users, JTBD, current MVP scope, known limitations.
Constraints: Windows-first MVP, no bot-join model, short-overlay UX.
Expected Artifact: Structured PRD section with user stories and acceptance criteria.
Done When: Stories are testable and map to epics.
Output Format: Markdown with sections: Problem, Goals, User Stories, Acceptance Criteria, Risks.
```

### Quality Checklist
- User story has actor + intent + outcome.
- Acceptance criteria are measurable.
- Scope avoids post-MVP features unless marked deferred.

---

## 2) System Architect AI
### Prompt Template
```text
Role: System Architect AI
Objective: Define architecture for <subsystem>.
Inputs: PRD requirements, latency targets, platform constraints.
Constraints: Contract-first boundaries, local-first privacy defaults.
Expected Artifact: Component diagram + interfaces + fallback paths.
Done When: Contracts and failure modes are explicit.
Output Format: Markdown + mermaid + interface table.
```

### Quality Checklist
- Includes data flow and error flow.
- Defines versioned interfaces.
- Specifies latency budget impact.

---

## 3) Desktop AI
### Prompt Template
```text
Role: Desktop AI
Objective: Design/implement desktop behavior for <feature>.
Inputs: Overlay states, hotkey map, permission requirements.
Constraints: Minimal UI noise, predictable state transitions.
Expected Artifact: RFC or implementation plan + test cases.
Done When: Feature behavior is reproducible and testable.
Output Format: State machine table + event handlers + QA checklist.
```

### Quality Checklist
- State transitions include invalid-state handling.
- Recovery path exists for hidden/taskbar-less modes.
- Hotkey conflicts considered.

---

## 4) Audio AI
### Prompt Template
```text
Role: Audio AI
Objective: Define pipeline for <audio capture or processing step>.
Inputs: Device model, stream requirements, target latency.
Constraints: Preserve source tags, robust under device changes.
Expected Artifact: Audio pipeline spec + diagnostics strategy.
Done When: Chunk schema, buffering, and fallback are complete.
Output Format: Stage-by-stage table + telemetry fields + failure matrix.
```

### Quality Checklist
- Covers loopback and mic independently.
- Documents drift/sync strategy.
- Includes self-test and troubleshooting steps.

---

## 5) Speech AI
### Prompt Template
```text
Role: Speech AI
Objective: Benchmark and tune RU streaming STT.
Inputs: Test dataset profile, baseline model candidates, hardware constraints.
Constraints: Partial updates every 300-800 ms target.
Expected Artifact: Benchmark plan + tuning recommendations.
Done When: Baseline is selected with metric rationale.
Output Format: Benchmark matrix + decision summary + rollout plan.
```

### Quality Checklist
- Includes RU and RU/EN mixed speech tests.
- Reports WER/CER and latency jointly.
- Contains degraded-mode recommendation.

---

## 6) Prompt AI
### Prompt Template
```text
Role: Prompt AI
Objective: Create prompt schemas for <mode>.
Inputs: Mode goals, guardrails, output format rules.
Constraints: One-line first response, max 3 alternatives expanded.
Expected Artifact: Prompt templates + scoring rubric.
Done When: Outputs meet style and length constraints in eval set.
Output Format: Prompt blocks + rubric + failure examples.
```

### Quality Checklist
- Enforces compact response style.
- Defines hallucination and verbosity penalties.
- Includes edge-case prompts.

---

## 7) QA AI
### Prompt Template
```text
Role: QA AI
Objective: Produce test plan for <feature or release milestone>.
Inputs: Requirements, architecture constraints, compatibility targets.
Constraints: Include stealth and latency regressions where relevant.
Expected Artifact: Test matrix + reproducible test cases.
Done When: High-risk paths covered with pass/fail criteria.
Output Format: Matrix by platform/app/version + scripted test steps.
```

### Quality Checklist
- Explicit preconditions and expected results.
- Includes negative and recovery scenarios.
- Maps each test to requirement IDs.

---

## 8) DevOps AI
### Prompt Template
```text
Role: DevOps AI
Objective: Define CI/CD workflow for <scope>.
Inputs: Monorepo structure, release channels, test requirements.
Constraints: Fast feedback for core loop regressions.
Expected Artifact: Pipeline spec with stages and quality gates.
Done When: Build/test/release path is automated for alpha/beta.
Output Format: Stage list + triggers + artifact outputs + rollback path.
```

### Quality Checklist
- Includes contract compatibility checks.
- Includes release gating for stealth matrix and crash rates.
- Covers code signing/update channel prerequisites.

---

## 9) Security AI
### Prompt Template
```text
Role: Security AI
Objective: Evaluate privacy and threat model for <feature>.
Inputs: Data flow, storage model, telemetry schema.
Constraints: Local-first default, explicit user controls.
Expected Artifact: Threat model + control recommendations.
Done When: Risks are prioritized with actionable mitigations.
Output Format: Threat table (vector, impact, likelihood, control, owner).
```

### Quality Checklist
- Distinguishes local cache vs cloud sync data paths.
- Covers permission misuse and unintended capture scenarios.
- Specifies auditability requirements.

---

## Execution Patterns
### Pattern A: Narrow Engineering Task
Use when a subsystem needs a concrete artifact in one pass.

### Pattern B: Draft -> Review -> Fix
Use for architecture and PRD outputs:
1. Draft artifact.
2. Run checklist review.
3. Revise and publish final version.

### Pattern C: Benchmark-Driven Decision
Use for model or latency-sensitive choices:
1. Define benchmark dataset and metrics.
2. Run comparison.
3. Record recommendation with trade-offs.

## Anti-Patterns to Avoid
- "Write full architecture for the whole product" without scope boundaries.
- "Improve quality" without metrics and acceptance criteria.
- Requests without expected artifact type.

## Prompt Pack Success Criteria
- Each generated artifact is directly actionable by engineering team.
- Outputs map to backlog items and acceptance criteria.
- Reviewers can validate work without requiring hidden assumptions.
