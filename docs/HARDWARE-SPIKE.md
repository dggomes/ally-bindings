# Ally X hardware validation

## Goal

Prove a safe physical-input → mapped virtual Xbox controller path on Daniel's ROG Xbox Ally X. The application shell is already testable without drivers; this spike is the hard gate for claiming controller remapping works.

The key unknown is not generic XInput reading. It is whether the built-in Ally controls—including rear paddles and ASUS buttons—can be captured, transformed and restored without duplicate input or broken Armoury/Command Centre behaviour.

## Preconditions

- Use the physical ROG Xbox Ally X, plugged into power.
- Record Windows build, Armoury Crate version, ASUS System Control Interface version and controller firmware.
- Export/photograph existing Armoury controller mappings.
- Create a Windows restore point before installing any filter/virtual-controller driver.
- Keep a keyboard/touch recovery path available.
- Test in Windows controller diagnostics, the Remote Play menu or a non-competitive local title; never an anti-cheat game.
- Start from an unsigned-free/self-contained Ally Bindings CI artifact in preview mode.

## Inventory

Capture a redacted inventory before changing anything:

1. `joy.cpl` device list and button map.
2. Windows Settings controller entries.
3. Device Manager hardware IDs for gamepad/HID interfaces.
4. XInput index used by the built-in controller.
5. Whether M1/M2 are visible as independent buttons, keyboard events or Armoury-only modifiers.
6. Whether Command Centre/Armoury buttons enter XInput at all.
7. Behaviour in embedded and desktop control modes.

Do not assume rear paddles are independent inputs. If Armoury resolves them before Windows exposes the pad, Ally Bindings cannot safely offer direct rear-button mappings without an ASUS-supported path.

## Candidate backend decision order

### 1. Supported ASUS/Armoury interface

Use only if a documented/stable local invocation can change the live controller layout without mutating private databases or injecting into Armoury. Record the exact API/tool, provenance and rollback.

### 2. Maintained physical-hide + virtual Xbox backend

A candidate must:

- read the physical Ally controller while hidden from consumer apps;
- create one healthy virtual Xbox/XInput controller before hiding anything;
- identify physical vs virtual devices deterministically;
- preserve ASUS-specific buttons or leave them outside the interception path;
- recover on process kill, suspend and device reconnect;
- use signed components with acceptable licence/maintenance posture;
- uninstall and unhide cleanly.

Do not auto-install it from Ally Bindings. Installation and enabling remain explicit on-device operations.

### 3. Windows-supported virtual HID path

Prefer a supported Windows API/driver path if it can meet the same safety properties without a fragile legacy stack. Reject it if the native/driver burden exceeds this narrow app or cannot provide robust rollback.

## Minimal adapter proof

Before wiring the WPF app, build the smallest adapter that:

1. selects the physical XInput/HID device explicitly;
2. starts virtual output;
3. verifies virtual output health;
4. hides the physical device from the test consumer;
5. passes snapshots through `AllyBindings.Core.MappingEngine`;
6. applies Default and one obvious A/B swap profile;
7. restores/unhides on normal exit and a watchdog/failure path.

The adapter must implement `IControllerBackend`; profile parsing and mapping logic stay in Core.

## Test matrix

| Test | Expected | Hard fail |
|---|---|---|
| Baseline enumeration | Physical interfaces/buttons understood | Unknown duplicate/input path |
| Rear paddles | Exposure model documented | Claimed mapping without visible signal |
| Start output | One healthy virtual Xbox pad | Output unavailable/unstable |
| Hide physical | Test consumer sees one pad | No input or two pads |
| Default | Exact baseline Xbox layout | Missing/stuck/wrong input |
| A/B swap | Only A/B output changes | Other controls drift |
| Switch 20 times | No ghosts/missed transitions | Stuck/duplicated presses |
| Shortcut chord | Overlay cycles once per press | Repeat storm or gameplay lockout |
| Remote Play | Streamed Xbox sees one mapped pad | Duplicate controllers/input |
| Command Centre | Still opens and functions | Broken ASUS control |
| Armoury open/close | No corruption/reset | Armoury damage/crash |
| Sleep/resume | Input path recovers safely | Hidden/unusable controller |
| Controller reconnect | Same deterministic topology | Virtual/physical index confusion |
| Kill Ally Bindings | Physical path remains/returns usable | Controller stranded hidden |
| Kill backend/watchdog | Fail-open recovery | Reboot required for input |
| Disable startup | App no longer launches | Persistent unwanted startup |
| Uninstall backend | Original behaviour restored | Residual hidden device/driver |

## Pass rule

Enable a real backend only if every hard-fail condition passes and install/rollback are reproducible. Any duplicate input, controller lockout, Command Centre regression or ambiguous device identity keeps the app in preview mode.

## Evidence to retain

- Redacted device/driver inventory.
- Backend source/version/signing/licence rationale.
- Install, enable, disable and uninstall commands.
- Full matrix with timestamps and software/firmware versions.
- Diagnostics export before/after.
- Minimal adapter branch/commit and CI result.
