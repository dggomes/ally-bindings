# Ally X hardware validation

## Goal

Prove a safe physical-input → mapped virtual Xbox controller path on Daniel's ROG Xbox Ally X. The application shell is already testable without drivers; this spike is the hard gate for claiming controller remapping works.

The remaining full-backend unknown is not generic XInput reading. It is whether the standard built-in controls can be transformed and restored without duplicate input or broken Armoury/Command Centre behaviour. M1/M2 have a narrower, separately gated firmware-mapping path described below.

## M1/M2 research result (2026-08-01)

M1/M2 are not independent XInput buttons. ASUS configures them as firmware-level primary/secondary actions:

- ASUS's official remapping guide says M1/M2 are secondary-function modifiers by default and can be assigned their own actions by clearing **Set as Secondary Function**.
- G-Helper applies those assignments through ASUS HID feature report `0x5A`, command `0xD1`, mapping zone `0x08`.
- Handheld Companion independently uses the same report family/zone and identifies Xbox ROG Ally X as DMI model `RC73XA`.
- Linux ROG Ally support likewise treats M1/M2 as ASUS firmware-programmable keyboard/controller actions rather than gamepad button bits.

This supports a narrow direct-write backend; it does **not** make M1/M2 readable through `XInputGetState`. Ally Bindings therefore offers M1/M2 only as physical mapping sources, never as shortcut-chord buttons.

Implementation safety gates:

1. Both custom and recovery writes are source locked until the passive Armoury capture below is reviewed.
2. The app must confirm the supported ROG Ally model and compatible ASUS feature-report interfaces before starting Windows USB ETW, then revalidate that identity after capture.
3. The real-time ETW filter retains only metadata-decoded 50–64-byte binary fields containing the exact ASUS `5A D1 02 08 2C` rear-mapping prefix; zero padding is preserved, non-zero trailing bytes fail exact validation, and no raw ETL is written.
4. Before a later build can unlock writes, Armoury's `M1=A/M2=B`, `M1=X/M2=Y`, and Reset-to-Default reports must be compared byte-for-byte with the clean-room builder.
5. The later write path must still require exact DMI manufacturer/model, known ASUS VID/PID, an openable report `0x5A`, and mapping zone `0x08`.
6. Standard mappings remain preview-only and are labelled as such.

Known limitation: there is no proven read-back path for preserving a user's custom Armoury M1/M2 assignment. The validation run deliberately changes mappings through Armoury and finishes with Armoury's own reset. Photograph/export any custom assignments first. The ETW stream is system-wide while active; the capture ZIP is private diagnostic data containing only bounded matching candidate fields, not a raw system trace.

## Passive Armoury capture gate

1. Run **Capture Armoury M1/M2 protocol (passive)**; no separate capture software is required.
2. Confirm the displayed ROG Ally model and compatible ASUS feature-report interfaces.
3. Accept Windows' one-time UAC prompt for the same Ally Bindings executable acting as the temporary ETW helper.
4. Apply the three prompted Armoury states: `M1=A/M2=B`, `M1=X/M2=Y`, then Armoury Reset to Default. Capture stops automatically.
5. Retain the single ZIP, record the displayed bundle SHA-256 outside the capture directory, and verify its manifest says `rawSystemTraceWritten: false`. The user-writable ZIP is not immutable provenance; require at least two independent matching captures before proposing a protocol change.
6. Compare mapping prefix (`5A D1 02 08 2C`), action ordering/slots, complete 50-byte vector plus any preserved zero padding, provider/event/field metadata and default modifier bytes.
7. Keep both write gates closed on any unexplained extra command, mismatch, dropped/oversized event, ambiguous device identity, unavailable provider or absent reset packet.

Research references:

- ASUS: <https://rog.asus.com/articles/guides/how-to-remap-buttons-and-create-custom-game-profiles-on-the-rog-ally/>
- G-Helper protocol implementation: <https://github.com/seerge/g-helper/blob/main/app/Ally/AllyControl.cs>
- G-Helper HID transport: <https://github.com/seerge/g-helper/blob/main/app/USB/AsusHid.cs>
- Handheld Companion Ally device family: <https://github.com/Valkirie/HandheldCompanion/tree/main/HandheldCompanion/Devices/ASUS>
- Linux ROG Ally driver research: <https://github.com/NeroReflex/ROGueENEMY>

## Preconditions

- Use the physical ROG Xbox Ally X, plugged into power.
- Record Windows build, Armoury Crate version, ASUS System Control Interface version and controller firmware.
- Export/photograph existing Armoury controller mappings.
- Create a Windows restore point before installing any filter/virtual-controller driver.
- Keep a keyboard/touch recovery path available.
- Test in Windows controller diagnostics, the Remote Play menu or a non-competitive local title; never an anti-cheat game.
- Start from an unsigned, self-contained Ally Bindings CI artifact in preview mode.

## Inventory

Capture a redacted inventory before changing anything:

1. `joy.cpl` device list and button map.
2. Windows Settings controller entries.
3. Device Manager hardware IDs for gamepad/HID interfaces.
4. XInput index used by the built-in controller.
5. Confirm on this firmware that M1/M2 do not appear as independent XInput buttons, then validate the feature-report mapping path below.
6. Whether Command Centre/Armoury buttons enter XInput at all.
7. Behaviour in embedded and desktop control modes.

Do not treat rear paddles as independent inputs. The implemented path configures what firmware emits; it does not capture raw paddle presses.

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
| M1/M2 opt-in off | No feature-report write; Armoury assignment unchanged | Any hardware mutation |
| M1=A, M2=RT | Each paddle emits only its assigned action | Missing, doubled or stale secondary action |
| M1/M2 restore | Stock modifier behavior returns | Custom action remains/stuck input |
| Armoury after M1/M2 apply | Conflict behavior documented; no corruption/crash | Corruption, crash or unrecoverable mapping |
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
