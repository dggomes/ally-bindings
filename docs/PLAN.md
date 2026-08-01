# Implementation plan

## Outcome

A lightweight Windows application that lets Daniel choose a named controller-binding profile while Xbox Remote Play is already running on the ROG Xbox Ally X.

## Scope contract

**In scope:** default controller bindings, rear-button mappings, named presets, instant manual selection, local config, overlay confirmation, safe reset.

**Out of scope:** game detection inside the video stream, TDP/fan/RGB/display changes, game launching, macros, telemetry, accounts, and Armoury Crate profile editing.

## Phase 0 — hardware/backend spike (blocking)

**Goal:** prove the physical Ally X can be safely remapped into one virtual Xbox controller.

Tasks:

1. Inventory physical HID/XInput devices and identify how rear buttons and Command Centre are presented.
2. Capture baseline mappings and Armoury/firmware versions.
3. Test candidate backend A (supported ASUS path) then B/C from `HARDWARE-SPIKE.md`.
4. Run the full test matrix, including Remote Play, sleep/wake, and forced backend failure.
5. Select backend only with a documented install, rollback, and licence decision.

Acceptance criteria:

- One input path, no duplicates.
- Default/restoration path is proven.
- Command Centre and Armoury Crate remain functional.
- No backend means no v1 implementation.

## Phase 1 — mapping core and CLI proof

**Goal:** build the smallest end-to-end, testable remapping proof before any UI.

Deliverables:

- `.NET` solution with `AllyBindings.Core`, `AllyBindings.Backend`, and `AllyBindings.Cli`.
- Versioned JSON profile schema and atomic profile-store implementation.
- Pure mapping-engine tests: identity map, button swap, rear-to-stick-click mapping, invalid profile rejection.
- CLI: `list`, `apply <profile>`, `default`, `status`, `restore`.
- Structured local log with no controller-input recording.

Acceptance criteria:

- Profiles apply to Xbox Remote Play during a live session.
- CLI restoration works after backend restart.
- Unit tests cover mapping resolution and profile persistence.

## Phase 2 — minimal tray and overlay UX

**Goal:** eliminate command-line friction without inventing a launcher.

Deliverables:

- WinUI 3 tray host.
- Compact profile picker opened by a configurable global hotkey.
- Active-profile toast/overlay lasting roughly one second.
- `Default` profile pinned first; panic-reset hotkey independent of profiles.
- Profile editor for standard button mappings only.
- Accessibility: keyboard navigation, readable focus states, no icon-only mystery controls.

Acceptance criteria:

- Switch a profile in two actions or fewer.
- Overlay identifies the applied profile and succeeds/fails truthfully.
- UI remains low-idle and starts only when enabled by the user.

## Phase 3 — resilience and packaging

**Goal:** make it safe to trust on a sofa, not merely functional at a desk.

Deliverables:

- Startup checks that never hide input until output is healthy.
- Suspend/resume and Remote Play reconnect handling.
- Backend watchdog/recovery with clear status, never silent failure.
- Export/import profiles and diagnostics (with no secrets/input logs).
- MSIX or signed installer strategy based on backend requirements.
- README quick start, rollback guide, privacy statement, and compatibility matrix.

Acceptance criteria:

- Forced crash test returns a usable controller state.
- Install/uninstall does not damage Armoury configuration.
- Fresh-machine setup is reproducible from documentation.

## Phase 4 — optional future work (only after v1 is solid)

- Read a documented Xbox title signal, if one exists, and offer optional auto-select with confirmation.
- Per-profile haptic/LED hints only if achievable without broad Armoury integration.
- Import/export Armoury-style named layouts manually (not live profile manipulation).

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Xbox Remote Play exposes no title identity | Manual selector is the intended v1 design. |
| Controller hiding causes an input lockout | Do not hide until virtual output passes a health check; retain panic reset; test forced kill. |
| ASUS/Windows update changes device topology | Device-identification diagnostics and explicit compatibility gate; never silently remap unknown device. |
| Driver is unsigned, abandoned, or licence-incompatible | Reject it; do not ship around an unsafe backend. |
| Armoury Crate conflicts with remapping | Test with Armoury installed; if conflict persists, stop or require a documented mutually-exclusive setup. |
| Feature creep into a general Ally utility | Non-goals are enforced; keep app focused on mappings. |

## Definition of done for v1

Daniel can start Xbox Remote Play, select `Elden Ring` or `Lies of P` from a lightweight picker, receive exactly the chosen controller mapping, switch back to `Default` instantly, and recover cleanly from sleep/reconnect/app failure — without touching Armoury Crate or risking a stranded controller.
