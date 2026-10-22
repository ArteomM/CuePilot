# CuePilot Stealth Overlay UI/UX Specification

## Figma Input and Interpretation

Source node reviewed via MCP: `ymLSnQfYNaL2x5bKvzcZ1B`, `node 416:1246` ("Cover").

Observed visual baseline from source:
- dark neutral canvas (`#272A2B`) with high readability white text;
- minimal composition with strong hierarchy and restrained effects;
- non-noisy, editorial feel with one focal visual layer.

How it is applied to CuePilot:
- keep restrained premium tone and low-noise layout language;
- replace "cover-style composition" with utility-first desktop interaction model;
- prioritize tactical real-time assistance and keyboard ergonomics.

Reference: [Figma source node](https://www.figma.com/design/ymLSnQfYNaL2x5bKvzcZ1B/100-card-design-templates-UI-kit--Community-?node-id=416-1246&p=f&t=wha7JVXp4OQz6qjC-0).

---

## 1. Product Design Concept

`CuePilot` is a desktop-native "stealth overlay assistant" for live meetings.

Core concept:
- primary UI is a compact movable command overlay, always near the user workflow;
- expanded panel exists for tactical depth, not chat history;
- dashboard stores depth (transcript, timeline, session memory, search);
- settings control trust: visibility, permissions, shortcuts, audio quality.

Principle:
- glance value in 1-2 seconds;
- respond-first UX (best response first, alternatives second);
- no CRM metaphors, no chat feed dependency, no marketing-heavy surfaces.

---

## 2. Visual Direction

### Style Keywords
- restrained, premium, operational, low-noise, desktop utility.

### Theme Strategy
- default dark theme for focus and reduced distraction in calls;
- optional light theme for daytime setups and accessibility preference.

### Color Language
- base: charcoal/graphite/soft-black surfaces;
- text: off-white with tiered contrast;
- accent: single cool accent (`cyan` or `electric blue`) used only for:
  - state confirmation,
  - focus rings,
  - key actionable controls.

### Material and Surfaces
- semi-translucent matte panels;
- subtle backdrop blur for overlay windows;
- low-contrast 1px borders;
- short, soft elevation (no floating "neon card" effect).

### Typography
- primary: Inter/SF Pro/Geist style grotesk;
- compact line-height for tactical reading;
- concise copy blocks, no verbose labels.

---

## 3. Component List

Global component system:
- `OverlayShell` (drag handle, safe hit zones, focus ring);
- `StatusDot` (`idle/listening/thinking/muted/hidden/error`);
- `PromptField` (single-line with command behavior and keyboard hint);
- `ActionChips` (`Summarize`, `Suggest response`, `Follow up`, `Objection help`);
- `SuggestionLine` (single best suggestion, one-line clamp);
- `SourceIndicators` (mic, system context, invisibility status);
- `ResponseBlock` (best answer / alternative / follow-up);
- `ContextStrip` (last 1-3 minute key points);
- `TranscriptRow` (speaker, timestamp, source tag, confidence hint);
- `PermissionCard` (status + action + reason);
- `AudioMeter` (live dB-like level + threshold markers);
- `ShortcutRow` (action, keybinding, conflict state).

Behavioral primitives:
- `SmartCollapse` (expanded -> compact with context retention);
- `FocusMode` (reduced UI noise during active speech);
- `NoSignalState`, `PermissionMissingState`, `BackendDisconnectedState`.

---

## 4. Screen-by-Screen Breakdown

## 4.1 Onboarding (3-5 steps max)
- Step 1: value framing ("Real-time meeting help, minimal distraction");
- Step 2: permissions preview ("what and why");
- Step 3: audio test shortcut;
- Step 4: shortcut tutorial (toggle overlay/capture);
- Step 5: first empty compact overlay.

Tone:
- calm, technical, trust-first;
- no hype language, no growth/marketing blocks.

## 4.2 Permission Setup
- separate cards:
  - microphone;
  - system audio/screen context;
  - accessibility (if required by OS);
  - screen recording (OS-dependent).
- each card contains:
  - current state (enabled/missing/blocked),
  - short explanation,
  - CTA (`Open system settings`, `Retry check`).

## 4.3 Compact Overlay Widget (Primary Surface)
- width: ~520-620px; height: ~72-108px;
- structure:
  - left: drag handle + status dot;
  - center: prompt/input (`Ask about your conversation...`);
  - right: mic toggle, screen context toggle, invisibility indicator;
  - bottom line: one contextual suggestion.
- supports:
  - keyboard-first navigation,
  - hide/show shortcut,
  - snap to display edges.

## 4.4 Expanded Assist Panel
- opens from compact overlay;
- tactical hierarchy:
  1) Suggested response (best),
  2) Alternative response,
  3) Follow-up question,
  4) Key points (last 1-3 min),
  5) Current context strip.
- long answers hidden under explicit expand action.

## 4.5 Real-time Dashboard
- dedicated deep-work screen, separate from overlay:
  - live transcript with speaker labels,
  - session timeline,
  - searchable notes/action items/highlights,
  - history list of sessions,
  - prompt presets/custom actions.
- usage stats are tertiary and tucked into a secondary pane.

## 4.6 Settings
- sections:
  - General,
  - Invisibility,
  - Shortcuts,
  - Audio,
  - Language,
  - Display,
  - Privacy,
  - Account,
  - Updates.
- key controls:
  - show/hide during screen share,
  - show in taskbar toggle,
  - meeting audio language/output language,
  - display/monitor move,
  - auto-launch off,
  - shortcut editor with conflict detection,
  - transcript preview test,
  - screenshot context capture on/off,
  - reset session context,
  - update and restart.

## 4.7 Audio Test Utility Screen
- live input meter;
- source selector;
- transcript preview sample;
- adaptive guidance:
  - `Listening correctly`,
  - `No system audio detected`,
  - `Microphone permission missing`.
- goal: build trust before joining call.

## 4.8 Empty/Failure States
- `No signal`,
- `Permission missing`,
- `Hidden mode active`,
- `Unsupported source`,
- `Model disconnected`,
- `Transcript lag`.

Each state includes:
- direct reason,
- one primary corrective action,
- one optional secondary action.

---

## 5. Design Tokens

## 5.1 Color Tokens (Dark Default)
- `bg.base`: `#141718`
- `bg.surface`: `#1B1F20`
- `bg.overlay`: `rgba(22, 25, 26, 0.72)`
- `border.soft`: `rgba(255,255,255,0.10)`
- `text.primary`: `#F3F5F6`
- `text.secondary`: `#BAC1C4`
- `text.muted`: `#8A9499`
- `accent.primary`: `#36B3E6`
- `state.success`: `#2EA97D`
- `state.warn`: `#D19A48`
- `state.error`: `#C95E5E`

## 5.2 Color Tokens (Light Secondary)
- `bg.base`: `#F5F7F8`
- `bg.surface`: `#FFFFFF`
- `bg.overlay`: `rgba(255,255,255,0.78)`
- `border.soft`: `rgba(10,14,16,0.12)`
- `text.primary`: `#111517`
- `text.secondary`: `#3F4A50`
- accent/state tokens kept same hue family for consistency.

## 5.3 Typography
- `font.family`: Inter/SF Pro/Geist fallback stack;
- `text.xs`: 11/16;
- `text.sm`: 12/18;
- `text.md`: 14/20;
- `text.lg`: 16/22;
- `title.sm`: 18/24 semibold.

## 5.4 Radius, Spacing, Elevation
- radius: `8, 10, 12`;
- spacing scale: `4, 8, 12, 16, 20, 24`;
- shadow:
  - `shadow.sm`: `0 4px 12px rgba(0,0,0,0.18)`,
  - `shadow.md`: `0 8px 24px rgba(0,0,0,0.24)`.

## 5.5 Motion
- duration: `120ms`, `180ms`, `240ms`;
- easing: `cubic-bezier(0.2, 0.8, 0.2, 1)`;
- no long decorative transitions.

---

## 6. Interaction Rules

Overlay rules:
- always accessible by shortcut;
- draggable from dedicated handle and safe top strip;
- snaps to monitor edges with 12px margin;
- remembers per-monitor placement.

Command behavior:
- `Enter`: execute primary intent;
- `Arrow up/down`: cycle suggestion chips;
- `Tab`: move across controls;
- `Esc`: collapse expanded panel or clear focus.

Assist hierarchy:
- always show best single response first;
- alternatives and follow-up remain secondary;
- transcript content does not flood overlay.

Visibility:
- explicit toggle for screen share behavior;
- hidden mode must show clear local indicator when active.

---

## 7. States and Edge Cases

Primary states:
- `idle`,
- `listening`,
- `thinking`,
- `suggestion-ready`,
- `hidden/invisible`,
- `compact`,
- `expanded`,
- `dashboard-open`,
- `focus-mode`.

Failure/edge states:
- `no-signal` (mic level below threshold);
- `permissions-missing` (OS denial);
- `unsupported-source` (device backend mismatch);
- `backend-disconnected` (local model/runtime unavailable);
- `transcript-lag` (latency warning).

State communication rules:
- one-line operational message;
- one primary corrective CTA;
- never block with modal unless action is mandatory.

---

## 8. Copy Guidelines

Tone:
- short, direct, operational;
- zero marketing language.

Preferred copy:
- `Listening`
- `Ready`
- `Ask about your conversation`
- `Hidden from screen share`
- `Move to another display`
- `Test audio`
- `Clear context`
- `Suggest response`
- `Follow up`
- `Capture screen context`
- `Not hearing system audio`

Avoid:
- "AI magic" language,
- long explanatory paragraphs in live mode,
- emotionally loaded warning copy.

---

## 9. Desktop-Specific UX Patterns

Ergonomics:
- keyboard-first on every major action;
- overlay readable via peripheral vision;
- laptop-safe compact dimensions;
- multi-monitor awareness with explicit monitor targeting;
- click-through-safe interaction zones (prevent accidental call interference).

Call-safe behavior:
- no large popups during active listening;
- no auto-expansion while user is speaking unless explicitly enabled;
- visual updates are subtle and low-frequency.

---

## 10. Suggested Figma Structure

Pages:
- `00 Foundations`
- `01 Overlay`
- `02 Expanded Assist`
- `03 Dashboard`
- `04 Settings`
- `05 Onboarding + Permissions`
- `06 Audio Test + States`
- `07 Prototypes`

Key frame sets:
- `Overlay / Compact / Idle|Listening|Thinking|Ready|Hidden`
- `Overlay / Expanded / Default|Alt|Lag`
- `Dashboard / Live|History|Search`
- `Settings / each section`
- `Onboarding / Step1-5`
- `Permissions / each permission state`

Component library:
- `Primitives` (buttons, fields, chips, badges);
- `Status` (dots, banners, inline alerts);
- `Session` (transcript row, response block, timeline row);
- `Shell` (window chrome, drag handle, monitor badge).

---

## 11. Prompt for React/Tailwind UI Generation

```text
Design and implement a desktop-first AI meeting copilot UI in React + Tailwind.

Product model:
- Primary surface is a compact movable overlay command bar (not a chat app).
- Secondary surface is an expanded tactical assist panel.
- Deep transcript/history lives in a separate dashboard view.
- Add a full settings center (General, Invisibility, Shortcuts, Audio, Language, Display, Privacy, Account, Updates).
- Add onboarding, permission setup, audio test utility, and full empty/error states.

Visual style:
- Default dark theme with light theme option.
- Palette: charcoal/graphite/soft black with muted off-white text.
- One restrained accent color (cyan/electric blue) for focus/action only.
- Semi-transparent matte surfaces with subtle blur.
- Thin low-contrast borders, short soft shadows, medium radii.
- Premium desktop utility feel, compact but breathable.

Interaction:
- Keyboard-first navigation.
- Overlay supports drag, snap, hide/show shortcut, compact/expanded toggle.
- Show states: idle, listening, thinking, suggestion-ready, hidden mode, no signal, permission missing, backend disconnected, transcript lag.
- Use concise operational copy (e.g., Listening, Ready, Suggest response, Follow up).

Information architecture constraints:
- Overlay: status, prompt, 1-3 suggestions, quick chips only.
- Expanded panel: best response, alternative, follow-up, key points, context.
- Dashboard: transcript, timeline, search, session history, notes/highlights.
- Settings: all system controls and privacy toggles.

Do not design as a generic chatbot, CRM, or mobile-first app.
```

---

## 12. Stealth Copilot Quality Checklist

Use this to verify result quality:
- overlay gives value in <=2 second glance;
- compact mode stays uncluttered during active calls;
- best response appears first, without long text wall;
- transcript details are outside compact overlay;
- keyboard flow works end-to-end;
- hidden/invisibility status is always explicit and controllable;
- no brand-copy visuals, no loud gradients, no "AI aesthetic clichés";
- settings communicate trust/control (permissions, privacy, visibility);
- audio test clearly builds confidence before calls;
- UI feels like a professional desktop utility, not a chat product.

