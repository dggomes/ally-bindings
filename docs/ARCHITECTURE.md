# Architecture and safety boundaries

## Product boundary

Ally Bindings is a local controller-mapping selector for the case where multiple streamed Xbox titles share one Windows Remote Play process. It does not modify Armoury Crate records or infer the game inside the video stream. M1/M2 firmware writes remain behind closed source-level validation gates. Passive protocol capture is retained only as a deeper diagnostic. Product proof follows a software-first path: Armoury creates vendor-supported F12/F11 paddle assignments once, then Ally Bindings globally suppresses non-injected F12/F11 and interprets them locally. `WH_KEYBOARD_LL` cannot attribute those events to a device, so physical keyboard F11/F12 are unavailable while remapping is active and Armoury's injection flags remain a physical gate.

## Runtime shape

```text
XInput controller
      │ snapshots (read-only today)
      ▼
┌─────────────────────────┐
│ Windows host            │
│ pinned XInput monitor   │
│ F12/F11 paddle hook     │
│ chord recognizer        │
│ fixed recovery gesture  │
│ controller UI router    │
│ tray / WPF main window  │
│ non-activating overlay  │
└──────────┬──────────────┘
           │ selection
           ▼
┌─────────────────────────┐       ┌──────────────────────────┐
│ AllyBindings.Core       │       │ JSON profile store       │
│ profile cycle machine   │◄─────►│ %LOCALAPPDATA%           │
│ pure mapping transform  │       │ temp + backup + recovery │
│ backend contract        │       └──────────────────────────┘
└──────────┬──────────────┘
           │ apply/restore
           ▼
┌─────────────────────────┐
│ Controller backend      │
│ Preview (implemented)   │
│ Full ViGEm mirror       │
│ ASUS M1/M2 (locked)     │
│ Physical/virtual (gate) │
└─────────────────────────┘
```

## Projects

### `AllyBindings.Core`

Cross-platform .NET 8 library with no Windows UI dependency:

- XInput-compatible normalized controller state.
- Named profiles and one-to-one button mappings.
- Configuration validation/normalization.
- Atomic JSON persistence with backup and corrupt-file recovery.
- Pure mapping transform.
- Backend health/apply contract.
- Deterministic controller-chord carousel state machine.
- Pure rear-paddle overlay, complete realtime mapping pipeline and immutable controller-only recovery state machine.
- PII/input-history-free diagnostics projection.

### `AllyBindings.Windows`

Windows 10 2004+ WPF host:

- 20 ms XInput polling through `xinput1_4.dll`.
- Controller index auto-discovery or persisted preferred index.
- View + Menu default chord with hold/release/debounce gating.
- Non-activating, always-on-top profile overlay.
- RT confirmation while the configured chord remains held to open the editor.
- Edge-triggered controller UI routing for D-pad focus, A/B navigation, LB/RB sections and X/Y primary actions while the main window is active.
- Four controller/touch workspaces with a visual controller binding surface and prominent capture/update maintenance surface.
- Controller-aware binding-picker, profile-keyboard and decision-dialog layers; only Windows UAC and fatal pre-UI startup failures remain system-owned.
- Integrated, temporarily elevated Windows USB ETW logger with an in-memory exact-prefix filter for Armoury M1/M2 feature reports.
- Positively gated ASUS HID adapter whose custom and reset writes are source-disabled pending capture analysis.
- Complete ViGEm Xbox mirror with physical-slot pinning, F12→M1/F11→M2 capture, profile overlays and fault-driven virtual disconnect.
- Branded tray/executable icon, startup registration and immutable controller-only emergency bypass.
- A `CurrentUserOnly` local named-pipe activation channel: a normal second launch asks the existing sign-in-started tray process to reveal its window instead of opening a duplicate process.
- Profile/shortcut editor and truthful backend state.

WPF was chosen over WinUI 3 for this narrow tray utility: fewer deployment/runtime moving parts, native Windows rendering and straightforward cross-target compilation. There is no Electron/browser runtime.

### `AllyBindings.SoftwareProbe.Core`

Cross-platform evidence-only assembly. It contains the immutable session/checkpoint model, bounded F11/F12 event journal, atomic JSON persistence, and deterministic three-file ZIP/manifest builder. It has no reference to `AllyBindings.Core`, Windows input APIs, HID APIs, ViGEm, HidHide or driver-management code.

### `AllyBindings.M1M2Probe`

Separate Windows console diagnostic:

- generates no assignment input; Armoury's virtual keyboard is the sole assignment path;
- hooks only F11/F12 and can suppress only those two keys;
- enumerates XInput slots and reads DMI/service state without retaining machine name, username, paths or process data;
- detects but never installs/configures ViGEmBus or HidHide;
- creates one temporary ViGEm Xbox 360 controller only during `bridge`;
- maps F12 to A and F11 to B, ignores injected events, releases both buttons and disconnects in `finally`;
- never links `AllyBindings.Core`, HidSharp, ASUS protocol builders or an ASUS HID writer.

## Profile format

The canonical path is `%LOCALAPPDATA%\AllyBindings\config.json`. The entire user state is one schema-versioned document so profile and shortcut changes commit together. Controller recovery and virtual startup faults also create `%LOCALAPPDATA%\AllyBindings\virtual-remapping-disabled`; startup checks this durable fail-safe before creating ViGEm output, and only a later explicit successful enable/save transaction clears it.

```json
{
  "schemaVersion": 4,
  "activeProfileId": "elden-ring",
  "controllerIndex": null,
  "runAtStartup": true,
  "enableAsusRearButtonMappings": false,
  "enableVirtualControllerRemapping": false,
  "asusRearButtonMappingActive": false,
  "shortcut": {
    "buttons": ["View", "Menu"],
    "holdMilliseconds": 250,
    "commitDelayMilliseconds": 900
  },
  "profiles": [
    { "id": "default", "name": "Default", "enabled": true, "bindings": {} },
    { "id": "elden-ring", "name": "Elden Ring", "enabled": true, "bindings": { "A": "B", "M1": "RightTrigger" } }
  ]
}
```

Writes go to a same-directory temporary file, preserve the prior valid file as `.bak`, then replace the canonical file. Invalid JSON is copied to a timestamped `.corrupt-*` file before returning to Default.

## Carousel state machine

The controller shortcut is deliberately pure/testable:

1. Detect all configured buttons down.
2. Require the hold threshold.
3. Emit one selection per press, never key-repeat while held.
4. If RT rises after the hold threshold while the chord remains exact, cancel the pending selection and request the editor.
5. Require release before another selection.
6. Reset the inactivity timer after release.
7. Commit after the inactivity timeout.
8. Cancel pending selection if the controller disconnects.

Cycle items are enabled profiles only. Opening the editor is a separate deliberate chord+RT gesture, so rotating can never land on it accidentally. The first activation advances from the active profile to the next item.

## Backend contract

```csharp
public interface IControllerBackend : IAsyncDisposable
{
    BackendStatus GetStatus();
    Task<BackendStatus> InitializeAsync(CancellationToken cancellationToken = default);
    Task<BackendApplyResult> ApplyAsync(MappingProfile profile, CancellationToken cancellationToken = default);
    Task<BackendApplyResult> RestoreDefaultAsync(CancellationToken cancellationToken = default);
}
```

Backend results distinguish a selected app profile from output actually submitted. The preview backend keeps physical passthrough intact and returns `CommandAccepted = false`. The virtual backend mirrors the complete pinned physical XInput report, applies the pure mapping pipeline, overlays paddle state and submits one complete virtual Xbox report. `ArmouryProtocolValidation` still prevents construction of a write-capable ASUS backend.

### ASUS rear-button protocol boundary

- Positive DMI gate: exact manufacturer `ASUSTeK COMPUTER INC.` plus product `RC71L`, `RC72LA`, `RC73XA`, or `RC73YA`; firmware may expose the same supported token twice (`RC73XA_RC73XA`), which is accepted only when both tokens match.
- Positive HID gate: ASUS VID `0x0B05`, corroborated Ally embedded-controller PID `0x1ABE`/`0x1B4C`, openable interface, feature report `0x5A` whose own descriptor length is at least 50 bytes. PID `0x1B6E` is explicitly rejected as ProArt PZ13 hardware.
- Mapping command: report `0x5A`, command `0xD1`, zone `0x08`.
- Custom mapping reports populate only the requested primary paddle actions and explicitly clear both 11-byte secondary slots. The known native/default reset packet remains byte-for-byte unchanged.
- `CustomWritesApproved` and `RecoveryWritesApproved` are both `false`; profile, fixed controller-only emergency recovery, exit and stale-marker paths therefore send no ASUS report.
- Native reset authorization depends only on `RecoveryWritesApproved`; custom mappings require both validation gates so they cannot be enabled without an approved recovery path.
- `IAsusRearButtonDevice.ReadFeatureReportAsync` is a distinct write-incapable seam. Its HidSharp implementation reads report `0x5A` from every positively identified compatible interface exactly once, uses descriptor-sized buffers bounded to 50–64 bytes, serializes access with the existing HID gate, times out after three seconds and never retries or falls back to a write.
- `AsusFeatureReportSnapshotService` is an unelevated evidence plane with no dependency on ETW, helper IPC or the controller backend. It revalidates exact model/interface identity at all four stages, retains bounded bytes plus hashes, runs a pure diff/expected-vector analyzer, writes one three-file ZIP, and permanently labels the output diagnostic-only with zero hardware-unlock authority.
- The former one-shot `AllyBindings.HardwareValidator` project, `HidD_SetFeature` import, manual workflow, package and runbook/evidence machinery are removed. The software-probe package is built by ordinary PR CI and its compiled executable is scanned to reject dormant hardware-write, driver-install and device-hiding symbols.
- The USB ETW logger remains a separate deeper diagnostic fallback. It confirms the supported ROG Ally model plus compatible ASUS HID feature-report interfaces, obtains explicit confirmation, self-elevates the same executable, enables UCX/USBXHCI/USBHUB3 with `FullDataBusTrace`, and revalidates the identity after capture.
- The system-wide ETW stream is filtered in memory. The callback retains exact bounded 50–64-byte fields beginning `5A D1 02 08 2C`, with provider/event metadata and a per-candidate SHA-256, plus bounded metadata-only UCX class/control-transfer field shapes and counts. Priority transfer-data/status shapes have capacity reserved separately from lower-priority framing; pointer/identity field metadata and generic transfer values are excluded. It never writes a broad trace.
- Sequence matching is diagnostic only. Every capture remains review-required and cannot unlock writes or clear recovery state until physical Ally validation binds the Windows-build-specific ETW schema, selected interface, control-transfer setup packet and payload boundary.
- Filtered report JSON is hashed and bundled locally. No raw ETL/PCAP is created. Missing providers, oversized/dropped events, device ambiguity and target-identity changes fail closed.
- The helper uses a fixed ETW session name so a later run reclaims any logger orphaned by an uncatchable hard process termination; normal cancellation and parent disconnect stop cooperatively.
- No physical controller is hidden by any current component. The diagnostic software probe creates temporary output only during its timed bridge command; the separate opt-in full-mirror validation backend persists only while enabled and is guarded by physical-slot pinning plus the durable fail-open latch.

The validation backend streams normalized input through `MappingEngine` and produces one virtual Xbox device. Promotion still requires packaged Windows and physical proof of fail-open recovery and single-controller coexistence. No physical-device hide action belongs in the generic UI/core layer.

## Safety invariants

1. Never hide a physical controller before a healthy output device exists.
2. Never claim a profile was applied when only app state changed.
3. `Default` always exists and cannot carry remaps.
4. Fixed View + Menu hold followed by a newly pressed LT hold does not depend on the configurable profile chord.
5. Disconnect cancels uncommitted selections.
6. Startup registration is per-user, opt-in and removable.
7. Launching the app and explicit feature snapshots install no driver and require no elevation. Explicit Armoury tap or USB ETW capture requests one-time elevation for the same executable's temporary helper and installs nothing.
8. Normal diagnostics contain status/config metadata, not controller input history. Capture bundles are separate private artifacts created only on explicit request.
9. No injection into games, Xbox, anti-cheat or arbitrary processes. The opt-in diagnostic tap may temporarily inject only its embedded capture DLL into exact allowlisted x64 ASUS-signed Armoury processes under trusted system install roots; it has zero write authority and must be positively unloaded before completion.
10. No custom or recovery M1/M2 report can be emitted while either source-level validation gate is closed.
11. The software-probe artifact must contain no ASUS HID write API, generic HID writer, driver installer or physical-device hiding primitive—even as a dormant dependency.
12. The full-mirror validation runtime pins the live physical XInput monitor before ViGEm connects and never hides the physical controller. A dedicated non-WPF XInput safety poll recognizes recovery independently of UI routing, persists the restart latch before touching the virtual backend, then attempts synchronous output disconnect; a failure also disables the opt-in.
13. Evidence captures only F11/F12 transitions, capability status and fixed-choice named checkpoints; free-form notes, arbitrary keyboard input, device paths, usernames, machine names and process lists are outside the schema.

## Release gate

The UI/core/package may ship as preview. A build must not be called a working remapper until the software-first path passes `HARDWARE-SPIKE.md` on Daniel's Ally X: F12/F11 identity, suppression, virtual-only Remote Play, coexistence/duplicate-input behavior, Command Centre, cold boot, suspend/resume, forced kill and Armoury restoration. HidHide may be evaluated only if coexistence fails after virtual-only success.
