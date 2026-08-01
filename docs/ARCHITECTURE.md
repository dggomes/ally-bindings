# Architecture and safety boundaries

## Product boundary

Ally Bindings is a local controller-mapping selector for the case where multiple streamed Xbox titles share one Windows Remote Play process. It does not modify Armoury Crate records or infer the game inside the video stream. M1/M2 firmware writes are implemented behind a source-level validation gate that is currently closed; the shipping workflow is passive Armoury traffic capture first.

## Runtime shape

```text
XInput controller
      │ snapshots (read-only today)
      ▼
┌─────────────────────────┐
│ Windows host            │
│ XInput monitor          │
│ chord recognizer        │
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
- Device-address-filtered USBPcap logger and parser for Armoury M1/M2 feature reports.
- Positively gated ASUS HID adapter whose custom and reset writes are source-disabled pending capture analysis.
- Tray icon, startup registration and Ctrl+Alt+F12 panic/default hotkey.
- Profile/shortcut editor and truthful backend state.

WPF was chosen over WinUI 3 for this narrow tray utility: fewer deployment/runtime moving parts, native Windows rendering and straightforward cross-target compilation. There is no Electron/browser runtime.

## Profile format

The canonical path is `%LOCALAPPDATA%\AllyBindings\config.json`. The entire user state is one schema-versioned document so profile and shortcut changes commit together.

```json
{
  "schemaVersion": 2,
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

- Positive DMI gate: exact manufacturer `ASUSTeK COMPUTER INC.` plus product `RC71L`, `RC72LA`, `RC73XA`, or `RC73YA`.
- Positive HID gate: ASUS VID `0x0B05`, corroborated Ally embedded-controller PID `0x1ABE`/`0x1B4C`/`0x1B6E`, openable interface, feature report `0x5A` whose own descriptor length is at least 50 bytes.
- Mapping command: report `0x5A`, command `0xD1`, zone `0x08`.
- Both primary and secondary paddle slots receive the selected action to avoid retaining a stale Armoury secondary action.
- `CustomWritesApproved` and `RecoveryWritesApproved` are both `false`; profile, panic, exit and stale-marker paths therefore send no ASUS report.
- The passive logger enumerates USBPcap interfaces, accepts exactly one ASUS N-KEY address, displays it for explicit confirmation, invokes `USBPcapCMD --devices <address>` through a tracked `start /wait` wrapper, and revalidates the same device identity after capture.
- The parser accepts only outbound HID class-interface `SET_REPORT(feature)` control transfers and retains complete captured payloads alongside declared/captured lengths. Exact matches require report-ID, length, prefix and complete wire-vector agreement.
- A capture is conclusive only when both requested mappings and Armoury's reset appear in their expected action windows with no truncated records; every other result is labelled inconclusive and cannot become unlock evidence.
- The raw PCAP and extracted report JSON are hashed and bundled locally. Device ambiguity fails closed; broad root-hub capture is forbidden.
- No physical controller is hidden and no virtual output is created by this backend.

A real backend must additionally stream normalized input through `MappingEngine`, produce exactly one virtual Xbox device and prove fail-open recovery. No physical-device hide action belongs in the generic UI/core layer.

## Safety invariants

1. Never hide a physical controller before a healthy output device exists.
2. Never claim a profile was applied when only app state changed.
3. `Default` always exists and cannot carry remaps.
4. Ctrl+Alt+F12 does not depend on the controller chord/profile.
5. Disconnect cancels uncommitted selections.
6. Startup registration is per-user, opt-in and removable.
7. Launching the app installs no driver and requires no elevation; an explicit passive capture requires a separately installed USBPcap driver and its UAC prompt.
8. Normal diagnostics contain status/config metadata, not controller input history. Capture bundles are separate private artifacts created only on explicit request.
9. No code injection, Armoury database mutation, macros or network service.
10. No custom or recovery M1/M2 report can be emitted while either source-level validation gate is closed.

## Release gate

The UI/core/package may ship as preview. A build must not be called a working remapper until the real backend passes every hard-fail condition in `HARDWARE-SPIKE.md` on Daniel's Ally X, including duplicate-input, Command Centre, suspend/resume, forced kill and uninstall rollback.
