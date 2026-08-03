# Architecture and safety boundaries

## Product boundary

Ally Bindings is a local controller-mapping selector for the case where multiple streamed Xbox titles share one Windows Remote Play process. It does not modify Armoury Crate records or infer the game inside the video stream. M1/M2 firmware writes are implemented behind a source-level validation gate that remains closed. Passive capture is retained as a diagnostic, not the default route to product proof; a separately packaged controlled validator now owns the single fixed physical write experiment.

## Runtime shape

```text
XInput controller
      │ snapshots (read-only today)
      ▼
┌─────────────────────────┐
│ Windows host            │
│ XInput monitor          │
│ chord recognizer        │
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
- Branded tray/executable icon, startup registration and Ctrl+Alt+F12 panic/default hotkey.
- A `CurrentUserOnly` local named-pipe activation channel: a normal second launch asks the existing sign-in-started tray process to reveal its window instead of opening a duplicate process.
- Profile/shortcut editor and truthful backend state.

WPF was chosen over WinUI 3 for this narrow tray utility: fewer deployment/runtime moving parts, native Windows rendering and straightforward cross-target compilation. There is no Electron/browser runtime.

## Profile format

The canonical path is `%LOCALAPPDATA%\AllyBindings\config.json`. The entire user state is one schema-versioned document so profile and shortcut changes commit together.

```json
{
  "schemaVersion": 3,
  "activeProfileId": "elden-ring",
  "controllerIndex": null,
  "runAtStartup": true,
  "enableAsusRearButtonMappings": false,
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

Backend results distinguish a selected app profile from a mapping physically applied to controller output. The preview backend always keeps physical passthrough intact and returns `CommandAccepted = false`. In the capture-only build, `ArmouryProtocolValidation` prevents the Windows host from constructing a write-capable ASUS backend, and the backend independently rejects custom/reset operations by default.

### ASUS rear-button protocol boundary

- Positive DMI gate: exact manufacturer `ASUSTeK COMPUTER INC.` plus product `RC71L`, `RC72LA`, `RC73XA`, or `RC73YA`; firmware may expose the same supported token twice (`RC73XA_RC73XA`), which is accepted only when both tokens match.
- Positive HID gate: ASUS VID `0x0B05`, corroborated Ally embedded-controller PID `0x1ABE`/`0x1B4C`, openable interface, feature report `0x5A` whose own descriptor length is at least 50 bytes. PID `0x1B6E` is explicitly rejected as ProArt PZ13 hardware.
- Mapping command: report `0x5A`, command `0xD1`, zone `0x08`.
- Both primary and secondary paddle slots receive the selected action to avoid retaining a stale Armoury secondary action.
- `CustomWritesApproved` and `RecoveryWritesApproved` are both `false`; profile, panic, exit and stale-marker paths therefore send no ASUS report.
- Native reset authorization depends only on `RecoveryWritesApproved`; custom mappings require both validation gates so they cannot be enabled without an approved recovery path.
- `IAsusRearButtonDevice.ReadFeatureReportAsync` is a distinct write-incapable seam. Its HidSharp implementation reads report `0x5A` from every positively identified compatible interface exactly once, uses descriptor-sized buffers bounded to 50–64 bytes, serializes access with the existing HID gate, times out after three seconds and never retries or falls back to a write.
- `AsusFeatureReportSnapshotService` is an unelevated evidence plane with no dependency on ETW, helper IPC or the controller backend. It revalidates exact model/interface identity at all four stages, retains bounded bytes plus hashes, runs a pure diff/expected-vector analyzer, writes one three-file ZIP, and permanently labels the output diagnostic-only with zero hardware-unlock authority.
- `AllyBindings.HardwareValidator` is a standalone console artifact, not a runtime mode of the WPF application. It references neither Ally Bindings Core, HidSharp nor the generic HID adapter; narrow native SetupAPI/HID calls validate VID/PID/caps/report ID on the pinned handle. It requires elevation, exact RC73XA/PID_1B4C identity, one compatible interface, interactive exact confirmation, a write-through machine-wide one-shot claim and append-only audits. Display, hash, audit and SET_FEATURE share one approved immutable wire operation. Its only mutation is one literal M1=A/M2=B SET_FEATURE call; it has no readback, reset, general-mapping or retry path and never changes the application's source-level gates. Runnable artifacts are signed with GitHub build provenance and produced only by a manual main-branch workflow behind the `hardware-lab-approval` environment.
- The USB ETW logger remains a separate deeper diagnostic fallback. It confirms the supported ROG Ally model plus compatible ASUS HID feature-report interfaces, obtains explicit confirmation, self-elevates the same executable, enables UCX/USBXHCI/USBHUB3 with `FullDataBusTrace`, and revalidates the identity after capture.
- The system-wide ETW stream is filtered in memory. The callback retains exact bounded 50–64-byte fields beginning `5A D1 02 08 2C`, with provider/event metadata and a per-candidate SHA-256, plus bounded metadata-only UCX class/control-transfer field shapes and counts. Priority transfer-data/status shapes have capacity reserved separately from lower-priority framing; pointer/identity field metadata and generic transfer values are excluded. It never writes a broad trace.
- Sequence matching is diagnostic only. Every capture remains review-required and cannot unlock writes or clear recovery state until physical Ally validation binds the Windows-build-specific ETW schema, selected interface, control-transfer setup packet and payload boundary.
- Filtered report JSON is hashed and bundled locally. No raw ETL/PCAP is created. Missing providers, oversized/dropped events, device ambiguity and target-identity changes fail closed.
- The helper uses a fixed ETW session name so a later run reclaims any logger orphaned by an uncatchable hard process termination; normal cancellation and parent disconnect stop cooperatively.
- No physical controller is hidden and no virtual output is created by this backend.

A real backend must additionally stream normalized input through `MappingEngine`, produce exactly one virtual Xbox device and prove fail-open recovery. No physical-device hide action belongs in the generic UI/core layer.

## Safety invariants

1. Never hide a physical controller before a healthy output device exists.
2. Never claim a profile was applied when only app state changed.
3. `Default` always exists and cannot carry remaps.
4. Ctrl+Alt+F12 does not depend on the controller chord/profile.
5. Disconnect cancels uncommitted selections.
6. Startup registration is per-user, opt-in and removable.
7. Launching the app and explicit feature snapshots install no driver and require no elevation. Explicit Armoury tap or USB ETW capture requests one-time elevation for the same executable's temporary helper and installs nothing.
8. Normal diagnostics contain status/config metadata, not controller input history. Capture bundles are separate private artifacts created only on explicit request.
9. No injection into games, Xbox, anti-cheat or arbitrary processes. The opt-in diagnostic tap may temporarily inject only its embedded capture DLL into exact allowlisted x64 ASUS-signed Armoury processes under trusted system install roots; it has zero write authority and must be positively unloaded before completion.
10. No custom or recovery M1/M2 report can be emitted while either source-level validation gate is closed.

## Release gate

The UI/core/package may ship as preview. A build must not be called a working remapper until the real backend passes every hard-fail condition in `HARDWARE-SPIKE.md` on Daniel's Ally X, including duplicate-input, Command Centre, suspend/resume, forced kill and uninstall rollback.
