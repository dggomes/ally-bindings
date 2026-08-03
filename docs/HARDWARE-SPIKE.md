# Ally X hardware validation

## Goal

Prove a safe physical-input → mapped virtual Xbox controller path on Daniel's ROG Xbox Ally X. The application shell is already testable without drivers; this spike is the hard gate for claiming controller remapping works.

The remaining full-backend unknown is not generic XInput reading. It is whether the standard built-in controls can be transformed and restored without duplicate input or broken Armoury/Command Centre behaviour. M1/M2 have a narrower, separately gated firmware-mapping path described below.

## M1/M2 research result (2026-08-01)

M1/M2 are not independent XInput buttons. ASUS configures them as firmware-level primary/secondary actions:

- ASUS's official remapping guide says M1/M2 are secondary-function modifiers by default and can be assigned their own actions by clearing **Set as Secondary Function**.
- G-Helper applies those assignments through ASUS HID feature report `0x5A`, command `0xD1`, mapping zone `0x08`.
- Handheld Companion independently uses the same report family/zone and identifies Xbox ROG Ally X as DMI model token `RC73XA`; Daniel's firmware exposes the full DMI product name `ROG Xbox Ally X RC73XA_RC73XA` (including the equivalent repeated model token).
- Linux ROG Ally support likewise treats M1/M2 as ASUS firmware-programmable keyboard/controller actions rather than gamepad button bits.

This supports a narrow direct-write backend; it does **not** make M1/M2 readable through `XInputGetState`. Ally Bindings therefore offers M1/M2 only as physical mapping sources, never as shortcut-chord buttons.

Implementation safety gates:

1. Both custom and recovery writes are source locked until the passive Armoury capture below is reviewed.
2. The app must confirm the supported ROG Ally model and compatible ASUS feature-report interfaces before starting Windows USB ETW, then revalidate that identity after capture.
3. The real-time ETW filter retains only exact metadata-decoded 50–64-byte fields containing the ASUS `5A D1 02 08 2C` rear-mapping prefix plus bounded metadata-only UCX control-transfer field shapes/counts; generic transfer values are never serialized, zero padding in exact candidates is preserved, non-zero trailing bytes fail validation, and no raw ETL is written.
4. Before a later build can unlock writes, Armoury's `M1=A/M2=B`, `M1=X/M2=Y`, and Reset-to-Default reports must be compared byte-for-byte with the clean-room builder.
5. The later write path must still require exact DMI manufacturer/model, known ASUS VID/PID, an openable report `0x5A`, and mapping zone `0x08`.
6. Standard mappings remain preview-only and are labelled as such.

Known limitation: feature report `0x5A` readback is now implemented as a discovery instrument but has not yet been physically validated. The run deliberately changes mappings through Armoury and finishes with Armoury's own reset. Photograph/export custom assignments first. Successful readbacks are private controller-configuration bytes; they remain review-required and cannot unlock writes.

## Armoury protocol evidence gate

### Physical capture 1 — clean but inconclusive (2026-08-02)

- Bundle SHA-256: `e6744dc606b0a3de12a8e4a7f1a5205e4f7b1bf8bcbc0c2ef2e6da24e2c57ffc`.
- Target identity: `ROG Xbox Ally X RC73XA_RC73XA`, ASUS `VID_0B05&PID_1B4C`, report `0x5A` interface.
- All three action windows completed: `M1=A/M2=B`, `M1=X/M2=Y`, and Armoury Reset to Default.
- The integrated ETW session enabled UCX, USBXHCI and USBHUB3 with `FullDataBusTrace`, observed 4,874 events and decoded 15,464 binary bytes.
- There were zero lost events, oversized events, decode failures, ambiguous candidates, dropped candidates or aggregate-limit failures.
- No decoded binary field contained the assumed exact `5A D1 02 08 2C` prefix, so all three report windows remained empty and the capture is not unlock evidence.

### Physical capture 2 — button presses do not change the result (2026-08-02)

- Bundle SHA-256: `b24394b7dcb75971d1525750b4fc5d819c3fdbad3ae70fc2d24ad3f6f41a8941`.
- The same three Armoury assignment/reset windows were completed, with physical M1/M2 presses added after assignment.
- The session observed 4,140 events and decoded 13,359 binary bytes with zero losses, decode failures, oversized events, ambiguity, drops or aggregate-limit failures.
- It again retained zero exact reports. Physical rear-button presses generate input traffic; they are not required to make Armoury send its configuration write.

### Physical capture 3 — v12 identifies the nested ETW payload boundary (2026-08-02)

- Bundle SHA-256: `b25997e8cc5439e6f2686a02a62f6c4202c698313049243844808a39d06d92c9`.
- The schema-v5 capture observed 3,396 events and decoded 10,735 top-level binary bytes with zero event loss, oversized events, decode failures, ambiguity, candidate drops or aggregate-limit failures.
- All three QPC-attributed action phases were present. UCX recorded repeated class-interface and control-transfer traffic in each phase, including 104/53/53 `URB_FUNCTION_CLASS_INTERFACE_Start` events and 382/161/161 `URB_FUNCTION_CONTROL_TRANSFER_Stop` events.
- TraceEvent exposed each UCX transfer body as a dictionary-backed nested structure (`Other` at the top level), while v12 inspected only top-level byte arrays. Consequently the capture retained only 16-byte USBXHCI command TRBs, found no ASUS marker, and exhausted the metadata-shape quota on scalar rundown/control fields.
- The result remains non-conclusive and cannot unlock either write gate, but it gives a specific instrumentation fix: recursively inspect bounded nested ETW structures in memory, serialize only property framing for binary-bearing events, and continue to exclude generic payload bytes from the ZIP.

### Physical capture 4 — v13 proves binary-only retention is the wrong filter (2026-08-02)

- Bundle SHA-256: `958011b41016f68e12efb611833f404b9e7e0ba05420c2ebae532d4252e32c95`; evidence SHA-256: `23670d424d65c24d1a7e049ad8818ffbc933288510e58bc22039a2e80f9f6e3c`.
- The schema-v6 capture came from released commit `3ecd23f440210f1ffce151f95f8ad2f5e6048850`, observed 3,354 events and decoded 9,988 binary bytes with zero event loss, oversized events, decode failures, ambiguity, candidate drops or aggregate overflow.
- It retained zero exact ASUS reports and zero known-marker observations. The 214 retained shapes were 64 baseline plus 50 in each action phase.
- Every action phase's inventory was dominated by the same 42-field USBXHCI device-rundown schema plus eight XHCI command-TRB fields. Baseline retained a USB configuration descriptor and firmware-hash fields. No UCX action schema survived because those URB structures contained no byte-array leaf or known marker.
- Windows USB provider manifests place full transfer bytes in dedicated FullDataBusTrace completion fields such as `fid_URB_TransferData`, while start/header events carry URB setup/framing. The next diagnostic must therefore retain bounded UCX URB body/status/transfer-data **metadata even when no binary leaf is present**, while excluding identity pointers and unrelated rundown/TRB/hash schemas.

### Physical capture 5 — v14 isolates UCX control traffic but Windows omits transfer data (2026-08-02)

- Bundle SHA-256: `c65339c11d14653d98f6c3cc7c3ac23a009954dc8840ac5f6c81e3fff56e7bbb`; evidence SHA-256: `1f7b4c0f9354ed05e219b12b7f9dbff1d74fc2082041385828223588449aab45`.
- The schema-v7 capture came from released commit `a2c3fad312ac86f60bf6cb1faf658bc99ca6b821`, observed 3,304 events and decoded 9,071 binary bytes with zero loss, oversize, decode failure, ambiguity or dropped candidates.
- Mapping phases 1 and 2 each contained 22 UCX `URB_FUNCTION_CONTROL_TRANSFER_EX_Start` events (ID 23) and 22 matching header-only stop events (ID 24). Those EX events were absent from baseline and reset, proving action-correlated control traffic but not its target or payload.
- Data-bearing completion events 22 and 25 were absent from every phase. Therefore neither `fid_URB_TransferDataLength` nor `fid_URB_TransferData` was available to the decoder. This is provider behaviour, not priority starvation.
- Real manifest leaves `fid_URB_TransferBuffer`, `fid_URB_TransferBufferMDL` and `fid_URB_ReservedHcd_*` bypassed the intended nested deny tokens and wasted framing quota. Preview.15 rejects those exact patterns.

Conclusion: keep both write gates locked. Do not spend another physical run reshuffling ETW quotas. Public G-Helper captures establish the transport as HID Feature `SET_REPORT` (`0x21/0x09`, `wValue=0x035A`) and show that `GET_FEATURE 0x5A` behaves as a last-command/status mailbox rather than a reliable state mirror, so the read-only snapshot is retired as protocol authority.

The preferred next experiment is the self-contained Armoury HID write tap:

1. Run **Capture Armoury M1/M2** and accept the explicit injection/process-risk disclosure and UAC prompt. Close games and anti-cheat software first.
2. Ally Bindings extracts and hash-verifies its embedded capture-only x64 DLL, then targets at most four exact allowlisted ASUS-signed Armoury processes under trusted system install roots.
3. Follow the prompts for `M1=A/M2=B`, `M1=X/M2=Y`, and Armoury Reset to Default. The tap copies only 50–64-byte `5A D1` rear-mapping writes on `VID_0B05&PID_1B4C` handles and leaves each original API call unchanged.
4. Retain the single ZIP, record its displayed SHA-256 separately, and verify the manifest says `rawSystemTraceWritten: false` and `hardwareUnlockEvidence: false`.
5. Exported tap evidence contains only allowlisted process name, phase, ordinal, API result/error and exact bounded report bytes—no raw PID, path, timestamp, QPC, pointer or handle.
6. Require at least two independent matching physical runs plus human review before proposing any protocol change. A result never changes either source-level write gate automatically.

Research references:

- ASUS: <https://rog.asus.com/articles/guides/how-to-remap-buttons-and-create-custom-game-profiles-on-the-rog-ally/>
- G-Helper protocol implementation: <https://github.com/seerge/g-helper/blob/main/app/Ally/AllyControl.cs>
- G-Helper HID transport: <https://github.com/seerge/g-helper/blob/main/app/USB/AsusHid.cs>
- Handheld Companion Ally device family: <https://github.com/Valkirie/HandheldCompanion/tree/main/HandheldCompanion/Devices/ASUS>
- Linux ROG Ally driver research: <https://github.com/NeroReflex/ROGueENEMY>
- Windows UCX ETW manifest archive: <https://github.com/repnz/etw-providers-docs/blob/d5f68e8acda5da154ab44e405b610dd8c2ba1164/Manifests-Win10-18990/Microsoft-Windows-USB-UCX.xml>

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
