# CuePilot MVP Metrics and Benchmarks

## 1) Purpose
Define a measurable framework for evaluating CuePilot core-loop quality:
- speech -> transcript -> context -> suggestion -> user action.

This document is the source of truth for MVP quality gates and beta progression.

## 2) Primary KPI Set (Product + Technical)
### 2.1 TimeToFirstSuggestion (TTFS)
- **Definition**: Time from first detected speech in a turn to first visible suggestion.
- **Formula**: `firstSuggestionRenderedAtMs - turnSpeechStartAtMs`
- **Targets**:
  - P50 <= 2.0s
  - P95 <= 3.5s

### 2.2 TranscriptLag
- **Definition**: Delay between spoken audio and corresponding partial transcript event.
- **Formula**: `partialTranscriptEventAtMs - spokenAudioAtMs`
- **Targets**:
  - P50 <= 0.8s
  - P95 <= 1.5s

### 2.3 SuggestionAcceptanceRate
- **Definition**: Share of sessions/turns where user accepts/uses suggestion signal.
- **Proxy events**: copy, quick-insert, explicit "use", or expanded follow-through.
- **Target**: >= 25% in closed beta (initial benchmark).

### 2.4 ManualExpandRate
- **Definition**: How often users expand overlay to see more context.
- **Interpretation**:
  - too high -> mini outputs may be under-informative,
  - too low + low acceptance -> outputs may be irrelevant.
- **Target band**: 20-45% (mode-dependent).

### 2.5 STTCorrectionRate
- **Definition**: Fraction of transcript segments corrected by user.
- **Formula**: `correctedSegments / totalFinalSegments`
- **Target**: <= 12% in RU-first scenarios.

### 2.6 ScreenContextHitRate (when enabled)
- **Definition**: Share of accepted suggestions that reference useful screen context.
- **Target**: >= 30% for ROI-enabled sessions.

### 2.7 SessionCrashFreeRate
- **Definition**: Sessions without crash or fatal pipeline restart.
- **Target**: >= 99.0% in closed beta.

---

## 3) Derived Operational Metrics
- STT partial cadence (ms between partials).
- Finalization latency (speech end -> final transcript).
- Reasoning latency per request.
- Stealth toggle success rate.
- Audio capture initialization success rate.

## 4) Metric Dimensions (Segmentation)
Track all primary KPIs by:
- mode (`meeting`, `sales`, `interview`)
- language profile (`ru`, `ru_en_mixed`)
- audio source quality bucket
- device class
- app compatibility context (Zoom/Teams/Meet/etc. when available)

## 5) Event Schema (Minimum)
### Required event fields
- `sessionId`
- `eventType`
- `eventTs`
- `mode`
- `pipelineStage`
- `latencyMs` (if applicable)
- `qualityFlags`
- `platformInfo`

### Key event types
- `speech_turn_started`
- `stt_partial_emitted`
- `stt_final_emitted`
- `context_packet_ready`
- `suggestion_rendered`
- `suggestion_expanded`
- `suggestion_accepted`
- `stealth_toggle_changed`
- `session_crashed`

---

## 6) RU STT Benchmark Plan
## 6.1 Dataset Composition (minimum)
- 50 audio samples for MVP benchmark:
  - 30 RU business meeting samples
  - 10 RU/EN mixed samples
  - 10 noisy/overlap samples
- Duration: 20-90 seconds each.
- Include both microphone-like and system-output-like recordings.

## 6.2 Ground Truth
- Human-validated transcript for each sample.
- Mark speaker turn boundaries for subset (>=20 samples).

## 6.3 Evaluation Metrics
- WER (word error rate)
- CER (character error rate for Cyrillic robustness)
- Partial latency
- Finalization latency
- Stability under noise/overlap

## 6.4 Benchmark Procedure
1. Run each candidate model/profile on full dataset.
2. Collect quality and latency metrics in one report.
3. Rank candidates by weighted score:
   - 45% recognition quality (WER/CER),
   - 35% latency,
   - 20% stability/error recovery.

## 6.5 Selection Rule
Select baseline that meets latency targets while maintaining acceptable RU quality for live prompting. If no single candidate satisfies both, deploy dual profile:
- default low-latency profile
- quality-fallback profile.

---

## 7) Suggestion Engine Eval Plan
## 7.1 Eval Dataset
- 150 conversation snippets:
  - 60 meeting
  - 50 sales
  - 40 interview
- Each snippet contains:
  - transcript context window
  - optional screen context
  - expected response style rubric

## 7.2 Scoring Dimensions
- Relevance to last turn intent
- Conciseness (one-line-first compliance)
- Actionability
- Hallucination/unsupported claims risk
- Tone appropriateness for selected mode

### Scoring scale
- 1-5 per dimension
- Weighted final score with relevance and conciseness as top priorities.

## 7.3 Pass Criteria
- Average relevance >= 4.0
- Conciseness compliance >= 95%
- Hallucination risk incidents <= 3% in eval set

## 7.4 Online Validation
In beta, compare variants via A/B:
- prompt template variants
- policy-router thresholds
- output ranking strategies

---

## 8) Latency Budget Validation
Validate stage-level budget against architecture constraints:
- capture/buffer: 50-120 ms
- VAD/chunking: 40-120 ms
- STT partial: 250-600 ms
- context: 20-80 ms
- reasoning: 400-1400 ms
- render: < 50 ms

If any stage repeatedly violates budget:
1. Flag regression
2. identify bottleneck component
3. apply mitigation (smaller context, cheaper route, batching or cache)

## 9) Release Gates (Metrics-Driven)
### Gate A: Internal Alpha
- TTFS P50 <= 2.5s
- Transcript lag P50 <= 1.0s
- Crash-free >= 98.0%

### Gate B: Closed Beta Start
- TTFS P50 <= 2.0s, P95 <= 3.5s
- STT correction rate <= 15%
- Stealth compatibility matrix filled for target apps

### Gate C: MVP Readiness
- TTFS and lag targets sustained for 2 release cycles
- Suggestion acceptance trend stable or improving
- Crash-free >= 99.0%
- Known limitations documented with mitigations

## 10) Reporting Cadence
- Daily: pipeline health and latency dashboard.
- Weekly: quality review (STT + suggestion relevance).
- Per release: compatibility and crash review pack.

## 11) Ownership
- Speech metrics: Speech team
- Suggestion quality: Prompt/AI team
- Reliability/performance: Desktop + Platform teams
- KPI interpretation and product decisions: Product owner + Analytics
