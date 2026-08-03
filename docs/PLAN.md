# Delivery plan

## Outcome

A lightweight Windows tray application that lets Daniel rotate named controller profiles, deliberately open the editor with chord+RT, bind the ASUS M1/M2 rear paddles, return to Default instantly and eventually apply all standard mappings safely during Xbox Remote Play on the ROG Xbox Ally X.

## Current capability

### Application foundation — implemented

- [x] .NET 8 solution split into cross-platform core, WPF host and tests.
- [x] Versioned JSON profile schema, normalization, atomic writes, backup and recovery.
- [x] Guaranteed immutable Default profile.
- [x] Pure one-to-one XInput button mapping engine.
- [x] Backend status/apply/restore contract and truthful preview backend.
- [x] XInput controller polling with disconnect handling.
- [x] Configurable two-button controller chord; View + Menu default.
- [x] Hold/debounce/release/inactivity carousel state machine.
- [x] Small always-on-top overlay.
- [x] Explicit hold-chord-then-RT editor activation, separate from carousel rotation.
- [x] M1/M2 profile sources and controller-button/trigger targets.
- [x] ASUS feature-report backend implemented behind closed custom/recovery validation gates; no M1/M2 write is enabled before physical capture analysis.
- [x] Integrated Windows USB ETW Armoury logger with bounded in-memory candidate filtering, action markers, hashes and privacy-minimised review bundle; no external driver/tool and no unlock/recovery authority before physical schema validation.
- [x] WPF profile/shortcut editor, tray mode and opt-in sign-in startup.
- [x] Ctrl+Alt+F12 panic/default shortcut.
- [x] Redacted diagnostics export.
- [x] Digest-verified GitHub updater with hardened extraction, atomic replacement, explicit startup-health handshake and rollback.
- [x] Linux/macOS core tests plus Windows build/test/package CI.
- [x] Use the public source repository's Releases feed with automatic checks enabled by default and repository-scoped `GITHUB_TOKEN` publishing.

### Physical remapping — release gate

- [ ] Inventory actual Ally X input/HID/XInput topology.
- [x] Establish rear-paddle behavior: firmware-managed, not exposed as independent XInput buttons; configurable via ASUS HID mapping zone.
- [ ] Select an output/hiding backend with acceptable maintenance, signing and licence posture.
- [ ] Build a minimal physical-input → mapping engine → virtual-output adapter.
- [ ] Prove exactly one controller reaches Remote Play.
- [ ] Prove Command Centre and Armoury remain functional.
- [ ] Pass suspend/resume, reconnect, forced-kill and uninstall rollback tests.
- [ ] Enable the backend only behind an explicit compatibility decision.

## Shortcut product contract

- The app must be running in the tray to hear controller input.
- Each held/released configured chord advances one item.
- Enabled profiles appear in stored order; the editor is never a carousel item.
- Stopping on a profile selects/applies it after the inactivity timeout.
- Holding the armed chord and then pressing RT activates the editor and cancels the pending profile.
- Disconnect before commit cancels rather than applying stale intent.
- Face-button-only chords are allowed but visibly warned until safe interception can swallow them.

## Hardware/backend work

Follow `HARDWARE-SPIKE.md` on the physical Ally X. The backend should be a narrow adapter around the tested core—not a second profile store or duplicate mapping engine.

Required runtime sequence:

1. Identify the intended physical controller deterministically.
2. Start virtual output and verify it accepts/report states.
3. Whitelist Ally Bindings for physical reads if a filter is used.
4. Hide the physical device from consumers only after output health is green.
5. Feed physical snapshots through the pure mapping engine.
6. On panic/shutdown/fault, restore Default and unhide/fail open.

## Packaging and on-device validation

1. Download the self-contained `AllyBindings-win-x64` CI artifact.
2. Run without enabling sign-in startup; verify main/tray/overlay first.
3. Verify View + Menu and a temporary A + B chord in a controller test page, not a game.
4. Verify profile JSON recovery by testing a copy, never the only config.
5. Enable sign-in startup and confirm it can be disabled from the UI.
6. Run the hardware matrix before integrating any driver/backend.
7. Record Windows, Armoury, firmware and backend versions with results.

## Non-goals

- Automatic title detection from Remote Play video/OCR.
- General Armoury Crate profile mutation beyond the explicit M1/M2 firmware mapping opt-in.
- TDP, fan, RGB, display or game launching.
- Macros, turbo, scripts, anti-cheat bypasses or competitive automation.
- Accounts, cloud sync, telemetry or a network listener.

## Definition of done

### Preview application

A clean CI checkout builds a downloadable Windows app; Daniel can create profiles, rotate them with the controller, use chord+RT to open the editor, use tray/startup mode and see truthful locked/preview backend status. Public M1/M2 writes remain unavailable. A standalone controlled artifact performs only the one-shot RC73XA/PID_1B4C validation; it is built manually from an approved main commit and is never shipped in the app package or release.

### Remapping release

During Xbox Remote Play, selecting a named profile produces exactly the chosen controller layout with no duplicate input; Default/panic, Command Centre, sleep/reconnect, app failure and uninstall all return to a usable controller state.
